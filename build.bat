@echo off
rem =====================================================================
rem  VoicePaste 编译脚本
rem
rem  用法:
rem    build.bat            编译 Release 版，自动结束运行中的 VoicePaste.exe
rem    build.bat nokill     不结束进程：程序在跑时只生成 VoicePaste.build.exe
rem    build.bat debug      不优化编译
rem =====================================================================
setlocal EnableExtensions
cd /d "%~dp0"

set "SRC=VoicePaste.cs"
set "OUT=VoicePaste.exe"
set "BIN=VoicePaste.build.exe"
set "NOKILL=0"
set "OPT=/optimize+"

:parse
if "%~1"=="" goto build
if /i "%~1"=="nokill" (
    set "NOKILL=1"
    shift
    goto parse
)
if /i "%~1"=="debug" (
    set "OPT="
    shift
    goto parse
)
if /i "%~1"=="release" (
    set "OPT=/optimize+"
    shift
    goto parse
)
echo [错误] 未知参数: %~1
echo        用法: build.bat [nokill^|debug^|release]
exit /b 1

:build
set "SYS=%SystemRoot%\System32"
if not exist "%SRC%" (
    echo [错误] 当前目录找不到 %SRC%
    exit /b 1
)

rem ---- 定位 csc.exe（64 位优先，回退 32 位，最后查 PATH）----
set "CSC="
if exist "%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" set "CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not defined CSC if exist "%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe" set "CSC=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not defined CSC for /f "delims=" %%F in ('"%SYS%\where.exe" csc.exe 2^>nul') do if not defined CSC set "CSC=%%F"
if not defined CSC (
    echo [错误] 找不到 csc.exe，请确认已安装 .NET Framework 4.x
    exit /b 1
)

rem ---- 检查旧 exe 是否正在运行 ----
set "RUNNING="
"%SYS%\tasklist.exe" /FI "IMAGENAME eq %OUT%" 2>nul | "%SYS%\find.exe" /I "%OUT%" >nul && set "RUNNING=1"

echo [信息] 编译器: %CSC%
echo [信息] 正在编译 %SRC% ...
"%CSC%" /nologo %OPT% /t:winexe /out:"%BIN%" "%SRC%"
if errorlevel 1 (
    echo.
    echo [失败] 编译失败，%OUT% 保持原样未改动。
    if exist "%BIN%" del /f /q "%BIN%" >nul 2>&1
    pause
    exit /b 1
)

rem ---- 替换旧 exe ----
if defined RUNNING (
    if "%NOKILL%"=="1" (
        echo [警告] %OUT% 正在运行，未覆盖。
        echo        新版本已生成: %BIN%
        echo        关闭程序后执行:  move /y "%BIN%" "%OUT%"
        exit /b 0
    )
    echo [信息] 检测到 %OUT% 正在运行，正在结束进程...
    "%SYS%\taskkill.exe" /IM "%OUT%" /F >nul 2>&1
)

set /a TRIES=0
:trymove
move /y "%BIN%" "%OUT%" >nul 2>&1
if not errorlevel 1 goto done
set /a TRIES+=1
if %TRIES% GEQ 15 goto movefailed
"%SYS%\ping.exe" -n 2 127.0.0.1 >nul
goto trymove

:movefailed
echo [错误] 无法覆盖 %OUT%（文件被占用）。
echo        请手动关闭程序后执行:  move /y "%BIN%" "%OUT%"
pause
exit /b 2

:done
echo [完成] 编译成功
for %%F in ("%OUT%") do echo        %%~fF  (%%~zF 字节)
exit /b 0
