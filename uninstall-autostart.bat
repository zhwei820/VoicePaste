@echo off
setlocal
set "LNK=%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\VoicePaste.lnk"

if exist "%LNK%" (
    del /f /q "%LNK%"
    if exist "%LNK%" (
        echo [错误] 删除失败，请手动删除：%LNK%
        pause
        exit /b 1
    )
    echo 已取消开机自启：%LNK%
) else (
    echo 没有找到启动项，可能本来就没设置。
)

pause
