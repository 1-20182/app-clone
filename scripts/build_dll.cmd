@echo off
REM Build FenShen sandbox DLLs (x64 + x86) and selftest probe
setlocal
set VCVARS=C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvarsall.bat
set SRC=%~dp0..\src\sandbox_dll
set OUT=%~dp0..\dist\sandbox

if not exist "%OUT%" mkdir "%OUT%"

REM Running clones may still have the DLL mapped (cannot be deleted, but can be renamed)
if exist "%OUT%\inject64.dll" move /y "%OUT%\inject64.dll" "%OUT%\inject64.old.dll" >nul 2>&1
if exist "%OUT%\inject32.dll" move /y "%OUT%\inject32.dll" "%OUT%\inject32.old.dll" >nul 2>&1

echo === Building inject64.dll ===
call "%VCVARS%" x64 >nul || exit /b 1
cd /d "%SRC%"
cl /nologo /utf-8 /O2 /W3 /LD fenshen.c /Fe:"%OUT%\inject64.dll" /Fo:"%SRC%\inject64.obj" || exit /b 1

echo === Building inject32.dll ===
call "%VCVARS%" x86 >nul || exit /b 1
cd /d "%SRC%"
cl /nologo /utf-8 /O2 /W3 /LD fenshen.c /Fe:"%OUT%\inject32.dll" /Fo:"%SRC%\inject32.obj" || exit /b 1

del /q "%SRC%\*.obj" "%SRC%\*.lib" "%SRC%\*.exp" 2>nul

echo === Building selftest ===
call "%VCVARS%" x64 >nul || exit /b 1
cd /d "%SRC%"
cl /nologo /utf-8 /O2 /W3 selftest.c /Fe:"%OUT%\selftest.exe" /Fo:"%SRC%\selftest.obj" || exit /b 1
cl /nologo /utf-8 /O2 /W3 /LD devprobe.c /Fe:"%OUT%\devprobe.dll" /Fo:"%SRC%\devprobe.obj" || exit /b 1
del /q "%SRC%\selftest.obj" "%SRC%\devprobe.obj" "%SRC%\devprobe.lib" "%SRC%\devprobe.exp" 2>nul

echo Done.
dir "%OUT%"
endlocal
