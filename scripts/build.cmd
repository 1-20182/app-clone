@echo off
REM One-click build: sandbox DLLs + self-contained single-file exe
setlocal
set ROOT=%~dp0..

echo [1/2] Building sandbox DLLs...
call "%~dp0build_dll.cmd" || exit /b 1

echo [2/2] Publishing AppClone.exe...
dotnet publish "%ROOT%\src\AppClone" -c Release -r win-x64 --self-contained ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true -o "%ROOT%\dist" || exit /b 1

echo.
echo Done. Output: %ROOT%\dist\AppClone.exe
endlocal
