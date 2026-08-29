@echo off
REM Build the installer: pack payload.zip, then publish single-file setup exe
setlocal
set ROOT=%~dp0..

echo [1/3] Packing payload...
powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%\scripts\pack_payload.ps1" || exit /b 1

echo [2/3] Publishing installer...
dotnet publish "%ROOT%\src\AppClone.Setup" -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o "%ROOT%\dist" || exit /b 1

echo [3/3] Renaming...
if exist "%ROOT%\dist\AppCloneSetup.exe" move /y "%ROOT%\dist\AppCloneSetup.exe" "%ROOT%\dist\AppClone-Setup.exe" >nul

echo.
echo Done: %ROOT%\dist\AppClone-Setup.exe
endlocal
