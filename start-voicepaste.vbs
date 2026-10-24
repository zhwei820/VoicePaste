' start-voicepaste.vbs
' 一键静默启动 VoicePaste.exe（不弹出黑窗口，运行后在托盘显示图标）
Option Explicit

Dim fso, shell, dir, exe

Set fso = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")

' 以本脚本所在目录为基准，避免工作目录问题
dir = fso.GetParentFolderName(WScript.ScriptFullName)
exe = fso.BuildPath(dir, "VoicePaste.exe")

If Not fso.FileExists(exe) Then
    MsgBox "找不到 VoicePaste.exe：" & vbCrLf & exe, vbExclamation, "VoicePaste"
    WScript.Quit 1
End If

' 0=隐藏窗口, False=不等待返回。工作目录设为脚本所在目录，保证能读到 voicepaste.conf
shell.Run """" & exe & """", 0, False
