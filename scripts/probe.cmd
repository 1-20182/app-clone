@echo off
REM Logic regression probe suite.
REM   step 1: build the main app (Release)
REM   step 2: core logic probe      (test\probe)     - asserts F1..F6 behaviour
REM   step 3: installer UI probe    (test\setup-ui)  - asserts wizard layout geometry
REM   step 4: UI layout audit       (test\ui-audit)  - overflow / overlap / clip + 150 percent DPI emulation
REM Probes never touch real user data and never create a window handle.
setlocal
set ROOT=%~dp0..

echo [1/4] Building AppClone (Release)...
dotnet build "%ROOT%\src\AppClone\AppClone.csproj" -c Release || exit /b 1

echo [2/4] Running core logic probe...
dotnet run --project "%ROOT%\test\probe\probe.csproj" -c Release || exit /b 1

echo [3/4] Running installer UI probe...
dotnet run --project "%ROOT%\test\setup-ui\setup-ui.csproj" -c Release || exit /b 1

echo [4/4] Running UI layout audit...
dotnet run --project "%ROOT%\test\ui-audit\ui-audit.csproj" -c Release || exit /b 1

echo.
echo All probe assertions passed.
endlocal
