@echo off
REM Launch the MapleKiosk.Web Linux release script (deploy/release-linux.ps1).
REM
REM Usage:
REM   release-linux.bat 1.05          Release version 1.05
REM   release-linux.bat               Release default version (1.0.0)
REM
REM Double-click runs with no version (uses the script default). Pass a version
REM as the first argument to stamp it into the assembly.

setlocal
set "SCRIPT=%~dp0release-linux.ps1"

REM Prefer PowerShell 7+ (pwsh); fall back to Windows PowerShell.
where pwsh >nul 2>&1
if %ERRORLEVEL%==0 (
    set "PS=pwsh"
) else (
    set "PS=powershell"
)

if "%~1"=="" (
    "%PS%" -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%"
) else (
    "%PS%" -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" -Version %1
)

set "RC=%ERRORLEVEL%"
echo.
if not "%RC%"=="0" (
    echo Deploy FAILED with exit code %RC%.
) else (
    echo Deploy completed.
)
pause
exit /b %RC%
