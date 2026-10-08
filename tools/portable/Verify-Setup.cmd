@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Verify-Setup.ps1"
set "check_result=%errorlevel%"
echo.
pause
exit /b %check_result%
