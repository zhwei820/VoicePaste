@echo off
rem uninstall-autostart.cmd
rem Remove VoicePaste autostart entry from current user registry.

setlocal
set "REG_KEY=HKCU\Software\Microsoft\Windows\CurrentVersion\Run"
set "REG_VALUE=VoicePaste"

reg delete "%REG_KEY%" /v "%REG_VALUE%" /f
if errorlevel 1 (
    echo [INFO] Autostart entry not found (may not be set).
    pause
    exit /b 1
)

echo [OK] Autostart removed.
pause
