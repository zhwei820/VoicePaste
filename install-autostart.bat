@echo off
setlocal
set "EXE=%~dp0VoicePaste.exe"
set "WD=%~dp0"
set "LNK=%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\VoicePaste.lnk"

if not exist "%EXE%" (
    echo [错误] 找不到 VoicePaste.exe：%EXE%
    pause
    exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -Command "$w=New-Object -ComObject WScript.Shell; $s=$w.CreateShortcut($env:LNK); $s.TargetPath=$env:EXE; $s.WorkingDirectory=$env:WD; $s.WindowStyle=7; $s.Description='VoicePaste'; $s.Save()"

if not exist "%LNK%" (
    echo [错误] 创建启动项失败：%LNK%
    pause
    exit /b 1
)

echo 已设置开机自启（登录 Windows 后自动运行，窗口最小化）。
echo 启动项：%LNK%
echo 取消请运行 uninstall-autostart.bat，或直接删除上面的 .lnk 文件。
pause
