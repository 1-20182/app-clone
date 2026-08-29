@echo off
REM Build debug DLL + injector test tool (ASCII only)
setlocal
set VCVARS=C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvarsall.bat
cd /d "%~dp0..\src\sandbox_dll"

call "%VCVARS%" x64 >nul || exit /b 1
cl /nologo /utf-8 /O2 /W3 /LD /DFSDLL_DEBUG fenshen.c /Fe:inject64_dbg.dll /Fo:fenshen_dbg.obj || exit /b 1
cl /nologo /utf-8 /O2 /W3 injector_test.c /Fe:injector_test.exe /Fo:injector_test.obj || exit /b 1
del /q fenshen_dbg.obj injector_test.obj 2>nul
echo OK
endlocal
