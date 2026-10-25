@echo off
chcp 936 >nul
REM One-click deploy: verify dist output -> backup installed exe -> copy exe + sandbox -> verify size (auto rollback on failure)
REM Usage: scripts\deploy.cmd           deploy current dist as-is
REM        scripts\deploy.cmd /build    build first, then deploy
setlocal enabledelayedexpansion
set "ROOT=%~dp0.."
set "DIST=%ROOT%\dist"
set "TARGET=E:\Ӧ�÷���"
set "STAMP=%DATE:~0,4%%DATE:~5,2%%DATE:~8,2%"

if /i "%~1"=="/build" (
  call "%~dp0build.cmd"
  if errorlevel 1 exit /b 1
)

if not exist "%DIST%\AppClone.exe" goto noexe
if not exist "%DIST%\sandbox\inject64.dll" goto nodll
if not exist "%DIST%\sandbox\inject32.dll" goto nodll
if not exist "%DIST%\sandbox\devprobe.dll" goto nodll
if not exist "%DIST%\sandbox\selftest.exe" goto nodll
if not exist "%TARGET%" goto notarget

REM A running app locks the exe; report instead of killing the user's process
tasklist /fi "imagename eq AppClone.exe" 2>nul | find /i "AppClone.exe" >nul
if not errorlevel 1 goto running

if not exist "%TARGET%\sandbox" mkdir "%TARGET%\sandbox"

if exist "%TARGET%\AppClone.exe" (
  copy /y "%TARGET%\AppClone.exe" "%TARGET%\AppClone.exe.bak-%STAMP%" >nul
  if errorlevel 1 goto backupfail
  echo Backed up old build -^> AppClone.exe.bak-%STAMP%
)

copy /y "%DIST%\AppClone.exe" "%TARGET%\AppClone.exe" >nul
if errorlevel 1 goto rollback
copy /y "%DIST%\sandbox\inject64.dll" "%TARGET%\sandbox\" >nul
copy /y "%DIST%\sandbox\inject32.dll" "%TARGET%\sandbox\" >nul
copy /y "%DIST%\sandbox\devprobe.dll" "%TARGET%\sandbox\" >nul
copy /y "%DIST%\sandbox\selftest.exe" "%TARGET%\sandbox\" >nul

REM Size check: the deployed single-file exe must match the source byte size
for %%F in ("%DIST%\AppClone.exe") do set "SRCSIZE=%%~zF"
for %%F in ("%TARGET%\AppClone.exe") do set "DSTSIZE=%%~zF"
if not "%SRCSIZE%"=="%DSTSIZE%" goto rollback

echo Deployed: %TARGET%\AppClone.exe (%SRCSIZE% bytes)
exit /b 0

:rollback
echo Deploy failed, rolling back...
if exist "%TARGET%\AppClone.exe.bak-%STAMP%" copy /y "%TARGET%\AppClone.exe.bak-%STAMP%" "%TARGET%\AppClone.exe" >nul
echo Rolled back to AppClone.exe.bak-%STAMP% (restore manually if the backup is missing)
exit /b 1

:noexe
echo Missing %DIST%\AppClone.exe - run scripts\build.cmd first
exit /b 1
:nodll
echo Missing sandbox DLLs under dist\sandbox - run scripts\build.cmd first
exit /b 1
:notarget
echo Install folder not found: %TARGET%
exit /b 1
:running
echo AppClone is running - exit it before deploying
exit /b 1
:backupfail
echo Backup of the installed exe failed - aborted, nothing changed
exit /b 1
