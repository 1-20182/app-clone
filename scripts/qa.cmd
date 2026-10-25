@echo off
REM ============================================================
REM  AppClone - one-click full QA
REM
REM   [0/5] preflight: no running AppClone.exe
REM   [1/5] build main app (Release, expect 0 errors)
REM   [2/5] core logic probe      (86 assertions, exit 0 = all pass)
REM   [3/5] installer UI probe    (77 assertions, exit 0 = all pass)
REM   [3.5/5] form layout audit   (all forms, multi-width geometry, exit 0 = no overlap/clipping)
REM   [4/5] portable build  -> dist\AppClone.exe
REM   [5/5] installer build -> dist\AppClone-Setup.exe
REM
REM  Isolation test (bash scripts/test.sh, 15 assertions) must be run
REM  separately in Git Bash - it is a POSIX script, cmd cannot run it.
REM
REM  NOTE: KEEP THIS FILE ASCII-ONLY. cmd.exe parses .cmd with the OEM
REM  codepage (GBK on zh-CN Windows), so UTF-8 Chinese comments corrupt
REM  parsing and the script dies with "not a recognized command".
REM  All other .cmd files in this repo follow the same rule.
REM ============================================================
setlocal
set ROOT=%~dp0..

echo.
echo ###### [0/5] preflight: ensure AppClone.exe is not running ######
tasklist /fi "imagename eq AppClone.exe" 2>nul | find /i "AppClone.exe" >nul
if not errorlevel 1 (
    echo [ERROR] AppClone.exe is running. Exit it completely ^(including tray^) and retry.
    exit /b 1
)
echo [OK] no running AppClone.exe

echo.
echo ###### [1/5] build main app ######
dotnet build "%ROOT%\src\AppClone\AppClone.csproj" -c Release
if errorlevel 1 goto fail

echo.
echo ###### [2/5] core logic probe ^(86 assertions^) ######
dotnet run --project "%ROOT%\test\probe\probe.csproj" -c Release
if errorlevel 1 goto fail

echo.
echo ###### [3/5] installer UI probe ^(77 assertions^) ######
dotnet run --project "%ROOT%\test\setup-ui\setup-ui.csproj" -c Release
if errorlevel 1 goto fail

echo.
echo ###### [3.5/5] form layout audit ######
dotnet run --project "%ROOT%\test\ui-audit\ui-audit.csproj" -c Release
if errorlevel 1 goto fail

echo.
echo ###### [4/5] portable build ######
call "%ROOT%\scripts\build.cmd"
if errorlevel 1 goto fail

echo.
echo ###### [5/5] installer ######
call "%ROOT%\scripts\build_installer.cmd"
if errorlevel 1 goto fail

echo.
echo ============================================================
echo  QA PASSED: dist\AppClone.exe + dist\AppClone-Setup.exe ready
echo  Run separately: bash scripts/test.sh  (isolation, 15 assertions)
echo ============================================================
endlocal
exit /b 0

:fail
echo.
echo ============================================================
echo  QA FAILED: see output above.
echo ============================================================
endlocal
exit /b 1
