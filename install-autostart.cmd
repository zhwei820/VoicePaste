@echo off
rem install-autostart.cmd
rem Set VoicePaste to start on Windows login (current user, no admin needed).

setlocal
set "DIR=%~dp0"
set "VBS=%DIR%start-voicepaste.vbs"
set "REG_KEY=HKCU\Software\Microsoft\Windows\CurrentVersion\Run"
set "REG_VALUE=VoicePaste"

if not exist "%VBS%" (
    echo [ERROR] Not found: %VBS%
    pause
    exit /b 1
)

reg add "%REG_KEY%" /v "%REG_VALUE%" /t REG_SZ /d "\"%VBS%\"" /f
if errorlevel 1 (
    echo [FAILED] Could not write registry value.
    pause
    exit /b 1
)

echo [OK] Autostart enabled.
echo   Target:  %VBS%
echo   Registry: %REG_KEY%\%REG_VALUE%
echo.
echo To remove autostart, run uninstall-autostart.cmd
pause
