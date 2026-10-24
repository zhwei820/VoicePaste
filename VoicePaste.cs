/*
 * VoicePaste — 微信语音输入 → 捕获框 → 干净粘贴到原窗口
 * ------------------------------------------------------------
 * 解决的问题：微信输入法的语音输入会把识别结果通过 IME 上屏到当前焦点
 * 窗口；在终端等会自行回显输入的地方，文字会被插入两遍。
 *
 * 工作流程：
 *   1) 按住 talk_hotkey（默认 Shift+Alt）时，记住当前前台窗口；
 *   2) 把焦点切到一个几乎不可见、但真实可聚焦的捕获文本框；
 *   3) 吞掉自己的热键，并模拟 voice_trigger_hotkey（默认 Win+Alt）
 *      真正调起微信语音；
 *   4) 松开热键后，微信把识别结果提交到捕获框；
 *   5) 轮询捕获框，文字停止增长 commit_delay_ms 后：
 *      写入剪贴板 → 切回原窗口 → Ctrl+V 粘贴；
 *   6) 清空并隐藏捕获框，等待下一次说话。
 *
 * 这是前台控制台程序：命令行启动后一直挂在前台，按 Ctrl+C 退出。
 * 纯 .NET Framework + WinForms，无第三方依赖。
 *
 * 用 Windows 自带 csc 编译（兼容 C# 5）：
 *   csc.exe /nologo /optimize+ /t:exe /out:VoicePaste.exe VoicePaste.cs
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace VoicePaste
{
    // =====================================================================
    // 配置
    // =====================================================================
    static class Config
    {
        public static string TalkHotkey = "Shift+Alt";
        public static string VoiceTriggerHotkey = "Win+Alt";
        public static int CommitDelayMs = 600;
        public static bool PasteOnFinish = true;
        public static bool RestoreClipboard = false;
        public static bool Debug = false;

        public static void Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return;

                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;

                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string value = line.Substring(eq + 1).Trim();

                    switch (key)
                    {
                        case "talk_hotkey":
                        case "start_hotkey": // 兼容旧配置名
                            if (value.Length > 0) TalkHotkey = value;
                            break;

                        case "voice_trigger_hotkey":
                            if (value.Length > 0) VoiceTriggerHotkey = value;
                            break;

                        case "commit_delay_ms":
                            {
                                int delay;
                                if (int.TryParse(value, out delay) && delay >= 0 && delay <= 60000)
                                    CommitDelayMs = delay;
                                break;
                            }

                        case "paste_on_finish":
                            PasteOnFinish = ParseBool(value, PasteOnFinish);
                            break;

                        case "restore_clipboard":
                            RestoreClipboard = ParseBool(value, RestoreClipboard);
                            break;

                        case "debug":
                            Debug = ParseBool(value, Debug);
                            break;
                    }
                }
            }
            catch
            {
                // 配置读取失败时使用默认值，不阻塞程序启动。
            }
        }

        static bool ParseBool(string value, bool defaultValue)
        {
            bool b;
            if (bool.TryParse(value, out b)) return b;
            if (value == "1") return true;
            if (value == "0") return false;
            if (string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(value, "no", StringComparison.OrdinalIgnoreCase)) return false;
            return defaultValue;
        }
    }

    // =====================================================================
    // Win32 互操作
    // =====================================================================
    static class Native
    {
        public const int VK_SHIFT = 0x10;
        public const int VK_CONTROL = 0x11;
        public const int VK_MENU = 0x12;
        public const int VK_LWIN = 0x5B;
        public const int VK_RWIN = 0x5C;
        public const int VK_LSHIFT = 0xA0;
        public const int VK_RSHIFT = 0xA1;
        public const int VK_LCONTROL = 0xA2;
        public const int VK_RCONTROL = 0xA3;
        public const int VK_LMENU = 0xA4;
        public const int VK_RMENU = 0xA5;
        public const int VK_V = 0x56;
        public const int VK_F24 = 0x87;

        public const int WH_KEYBOARD_LL = 13;
        public const int WM_KEYDOWN = 0x0100;
        public const int WM_KEYUP = 0x0101;
        public const int WM_SYSKEYDOWN = 0x0104;
        public const int WM_SYSKEYUP = 0x0105;

        public const uint INPUT_KEYBOARD = 1;
        public const uint KEYEVENTF_KEYUP = 0x0002;
        public const uint LLKHF_INJECTED = 0x10;

        public const int WS_EX_TOOLWINDOW = 0x00000080;

        public const int SW_RESTORE = 9;
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_SHOWWINDOW = 0x0040;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

        // 给我们自己 SendInput 注入的按键打标记，钩子里据此忽略它们。
        public static readonly IntPtr InjectionSignature = new IntPtr(0x56504943); // "VPIC"

        public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct INPUTUNION
        {
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public INPUTUNION u;
        }

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        public static extern void SwitchToThisWindow(IntPtr hWnd, bool altTab);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);

        public static bool IsKeyDown(int vk)
        {
            return (GetAsyncKeyState(vk) & 0x8000) != 0;
        }

        public static string GetWindowTitle(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return "";
            try
            {
                StringBuilder sb = new StringBuilder(512);
                GetWindowText(hWnd, sb, sb.Capacity);
                return sb.ToString();
            }
            catch
            {
                return "";
            }
        }

        // 切换前台窗口。对 Win10/11 来说，只调用 SetForegroundWindow 常常会失败，
        // 这里先附加到当前前台线程和目标线程，再切换，最后恢复。
        public static bool ForceForeground(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) return false;
            if (GetForegroundWindow() == hWnd) return true;

            IntPtr foreground = GetForegroundWindow();
            uint pid;
            uint targetThread = GetWindowThreadProcessId(hWnd, out pid);
            uint currentThread = GetCurrentThreadId();
            uint foregroundThread = foreground != IntPtr.Zero
                ? GetWindowThreadProcessId(foreground, out pid)
                : 0;

            // 常见做法：把当前前台线程的输入队列附加到本线程，
            // 这样 SetForegroundWindow 才不会因为“不是活动进程”而失败。
            bool attachForeground = foregroundThread != 0 && foregroundThread != currentThread;
            bool attachTarget = targetThread != 0
                && targetThread != currentThread
                && targetThread != foregroundThread;

            bool attachedForeground = false;
            bool attachedTarget = false;

            try
            {
                if (attachForeground)
                    attachedForeground = AttachThreadInput(foregroundThread, currentThread, true);
                if (attachTarget)
                    attachedTarget = AttachThreadInput(targetThread, currentThread, true);

                if (IsIconic(hWnd)) ShowWindow(hWnd, SW_RESTORE);

                BringWindowToTop(hWnd);
                SetForegroundWindow(hWnd);

                // 某些情况下 SetForegroundWindow 仍会被前台锁拦住，
                // 用一次置顶再取消置顶来抢一次前台，最后再尝试 SwitchToThisWindow。
                if (GetForegroundWindow() != hWnd)
                {
                    SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0,
                        SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                    SetWindowPos(hWnd, HWND_NOTOPMOST, 0, 0, 0, 0,
                        SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                    SetForegroundWindow(hWnd);
                }

                // 前台锁很严格时，先由本进程制造一次无害按键输入，
                // 让 Windows 认可我们有权切换前台，然后再切换。
                if (GetForegroundWindow() != hWnd)
                {
                    SendKey(VK_F24, false);
                    SetForegroundWindow(hWnd);
                    SendKey(VK_F24, true);
                }

                if (GetForegroundWindow() != hWnd)
                    SwitchToThisWindow(hWnd, true);
            }
            finally
            {
                if (attachedTarget)
                    AttachThreadInput(targetThread, currentThread, false);
                if (attachedForeground)
                    AttachThreadInput(foregroundThread, currentThread, false);
            }

            return GetForegroundWindow() == hWnd;
        }

        public static bool SendKey(int vk, bool keyUp)
        {
            INPUT input = new INPUT();
            input.type = INPUT_KEYBOARD;
            input.u.ki.wVk = (ushort)vk;
            input.u.ki.wScan = 0;
            input.u.ki.dwFlags = keyUp ? KEYEVENTF_KEYUP : 0;
            input.u.ki.time = 0;
            input.u.ki.dwExtraInfo = InjectionSignature;

            INPUT[] inputs = new INPUT[] { input };
            return SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT))) == 1;
        }

        public static bool SendPaste()
        {
            bool ok = SendKey(VK_CONTROL, false);
            ok = SendKey(VK_V, false) && ok;
            ok = SendKey(VK_V, true) && ok;
            ok = SendKey(VK_CONTROL, true) && ok;
            return ok;
        }
    }

    // =====================================================================
    // 热键描述 / 解析 / 匹配
    // =====================================================================
    sealed class HotkeySpec
    {
        public string Spec = "";

        public bool NeedCtrl;
        public bool NeedAlt;
        public bool NeedShift;
        public bool NeedWin;

        // 0 = 任意一侧，-1 = 左，1 = 右
        public int CtrlSide;
        public int AltSide;
        public int ShiftSide;
        public int WinSide;

        public int MainVk; // 0 表示没有主键（纯修饰键组合）

        public bool HasAnyKey
        {
            get { return NeedCtrl || NeedAlt || NeedShift || NeedWin || MainVk != 0; }
        }

        public bool IsSatisfied(bool[] down)
        {
            if (!CheckModifier(NeedCtrl, CtrlSide, Native.VK_LCONTROL, Native.VK_RCONTROL, down)) return false;
            if (!CheckModifier(NeedAlt, AltSide, Native.VK_LMENU, Native.VK_RMENU, down)) return false;
            if (!CheckModifier(NeedShift, ShiftSide, Native.VK_LSHIFT, Native.VK_RSHIFT, down)) return false;
            if (!CheckModifier(NeedWin, WinSide, Native.VK_LWIN, Native.VK_RWIN, down)) return false;
            if (MainVk != 0 && !down[MainVk]) return false;
            return true;
        }

        static bool CheckModifier(bool needed, int side, int leftVk, int rightVk, bool[] down)
        {
            bool left = down[leftVk];
            bool right = down[rightVk];

            if (!needed) return !left && !right;
            if (side < 0) return left;
            if (side > 0) return right;
            return left || right;
        }

        // 这个物理按键是否属于本热键（用来决定是否吞掉按键）
        public bool IsPartOfHotkey(int vk)
        {
            if (MainVk != 0 && vk == MainVk) return true;

            if (NeedCtrl && (vk == Native.VK_LCONTROL || vk == Native.VK_RCONTROL || vk == Native.VK_CONTROL)) return true;
            if (NeedAlt && (vk == Native.VK_LMENU || vk == Native.VK_RMENU || vk == Native.VK_MENU)) return true;
            if (NeedShift && (vk == Native.VK_LSHIFT || vk == Native.VK_RSHIFT || vk == Native.VK_SHIFT)) return true;
            if (NeedWin && (vk == Native.VK_LWIN || vk == Native.VK_RWIN)) return true;

            return false;
        }

        // 本热键是否需要一个“修饰键”物理按键（用于判断模拟语音热键时
        // 是否可以让用户按住的那个键直接传递给微信输入法）。
        public bool RequiresModifierVk(int vk)
        {
            int family;
            int side;
            FamilyOf(vk, out family, out side);
            if (family == 0) return false;

            switch (family)
            {
                case 1: return NeedCtrl && SideMatches(CtrlSide, side);
                case 2: return NeedAlt && SideMatches(AltSide, side);
                case 3: return NeedShift && SideMatches(ShiftSide, side);
                case 4: return NeedWin && SideMatches(WinSide, side);
            }
            return false;
        }

        static bool SideMatches(int requiredSide, int actualSide)
        {
            if (requiredSide == 0) return true;
            if (actualSide == 0) return false;
            return requiredSide == actualSide;
        }

        static void FamilyOf(int vk, out int family, out int side)
        {
            family = 0;
            side = 0;

            if (vk == Native.VK_LCONTROL || vk == Native.VK_CONTROL)
            {
                family = 1;
                side = vk == Native.VK_LCONTROL ? -1 : 0;
            }
            else if (vk == Native.VK_RCONTROL)
            {
                family = 1;
                side = 1;
            }
            else if (vk == Native.VK_LMENU || vk == Native.VK_MENU)
            {
                family = 2;
                side = vk == Native.VK_LMENU ? -1 : 0;
            }
            else if (vk == Native.VK_RMENU)
            {
                family = 2;
                side = 1;
            }
            else if (vk == Native.VK_LSHIFT || vk == Native.VK_SHIFT)
            {
                family = 3;
                side = vk == Native.VK_LSHIFT ? -1 : 0;
            }
            else if (vk == Native.VK_RSHIFT)
            {
                family = 3;
                side = 1;
            }
            else if (vk == Native.VK_LWIN || vk == Native.VK_RWIN)
            {
                family = 4;
                side = vk == Native.VK_LWIN ? -1 : 1;
            }
        }

        public static bool IsModifierVk(int vk)
        {
            return vk == Native.VK_SHIFT
                || vk == Native.VK_CONTROL
                || vk == Native.VK_MENU
                || vk == Native.VK_LWIN
                || vk == Native.VK_RWIN
                || (vk >= Native.VK_LSHIFT && vk <= Native.VK_RMENU);
        }

        public static bool TryParse(string spec, out HotkeySpec result, out string error)
        {
            result = null;
            error = null;

            if (spec == null || spec.Trim().Length == 0)
            {
                error = "热键为空";
                return false;
            }

            HotkeySpec h = new HotkeySpec();
            h.Spec = spec.Trim();

            string[] parts = spec.Split('+');
            foreach (string rawPart in parts)
            {
                string part = rawPart.Trim();
                if (part.Length == 0)
                {
                    error = "热键中含有空的键名";
                    return false;
                }

                int family;
                int side;
                if (TryParseModifier(part, out family, out side))
                {
                    if (!AddModifier(h, family, side, ref error)) return false;
                    continue;
                }

                int vk;
                if (!TryParseKey(part, out vk))
                {
                    error = "无法识别的按键: " + part;
                    return false;
                }

                if (h.MainVk != 0)
                {
                    error = "只能指定一个主键";
                    return false;
                }
                h.MainVk = vk;
            }

            if (!h.HasAnyKey)
            {
                error = "热键为空";
                return false;
            }

            result = h;
            return true;
        }

        static bool AddModifier(HotkeySpec h, int family, int side, ref string error)
        {
            switch (family)
            {
                case 1:
                    h.NeedCtrl = true;
                    return SetSide(ref h.CtrlSide, side, "Ctrl", ref error);
                case 2:
                    h.NeedAlt = true;
                    return SetSide(ref h.AltSide, side, "Alt", ref error);
                case 3:
                    h.NeedShift = true;
                    return SetSide(ref h.ShiftSide, side, "Shift", ref error);
                case 4:
                    h.NeedWin = true;
                    return SetSide(ref h.WinSide, side, "Win", ref error);
            }
            error = "无效的修饰键";
            return false;
        }

        static bool SetSide(ref int current, int side, string name, ref string error)
        {
            if (side == 0) return true;
            if (current != 0 && current != side)
            {
                error = name + " 同时指定了左右两侧";
                return false;
            }
            current = side;
            return true;
        }

        static bool TryParseModifier(string token, out int family, out int side)
        {
            family = 0;
            side = 0;

            string t = token.ToLowerInvariant();
            switch (t)
            {
                case "ctrl":
                case "control":
                case "ctl":
                case "ctrlkey":
                case "controlkey":
                    family = 1; side = 0; return true;

                case "lctrl":
                case "lcontrol":
                case "leftctrl":
                case "leftcontrol":
                case "lctrlkey":
                case "lcontrolkey":
                    family = 1; side = -1; return true;

                case "rctrl":
                case "rcontrol":
                case "rightctrl":
                case "rightcontrol":
                case "rctrlkey":
                case "rcontrolkey":
                    family = 1; side = 1; return true;

                case "alt":
                case "menu":
                case "altkey":
                case "menukey":
                    family = 2; side = 0; return true;

                case "lalt":
                case "lmenu":
                case "leftalt":
                case "leftmenu":
                case "laltkey":
                case "lmenukey":
                    family = 2; side = -1; return true;

                case "ralt":
                case "rmenu":
                case "rightalt":
                case "rightmenu":
                case "raltkey":
                case "rmenukey":
                    family = 2; side = 1; return true;

                case "shift":
                case "shiftkey":
                    family = 3; side = 0; return true;

                case "lshift":
                case "leftshift":
                case "lshiftkey":
                    family = 3; side = -1; return true;

                case "rshift":
                case "rightshift":
                case "rshiftkey":
                    family = 3; side = 1; return true;

                case "win":
                case "windows":
                case "meta":
                case "cmd":
                case "super":
                case "winkey":
                    family = 4; side = 0; return true;

                case "lwin":
                case "leftwin":
                case "lwinkey":
                    family = 4; side = -1; return true;

                case "rwin":
                case "rightwin":
                case "rwinkey":
                    family = 4; side = 1; return true;
            }

            return false;
        }

        static bool TryParseKey(string token, out int vk)
        {
            vk = 0;

            if (token.Length == 1)
            {
                char c = char.ToUpperInvariant(token[0]);
                if (c >= 'A' && c <= 'Z')
                {
                    vk = c;
                    return true;
                }
                if (c >= '0' && c <= '9')
                {
                    vk = c;
                    return true;
                }
            }

            string t = token.ToLowerInvariant();
            switch (t)
            {
                case "esc":
                case "escape": vk = 0x1B; return true;
                case "enter":
                case "return": vk = 0x0D; return true;
                case "space":
                case "spacebar": vk = 0x20; return true;
                case "tab": vk = 0x09; return true;
                case "backspace":
                case "back": vk = 0x08; return true;
                case "delete":
                case "del": vk = 0x2E; return true;
                case "insert":
                case "ins": vk = 0x2D; return true;
                case "home": vk = 0x24; return true;
                case "end": vk = 0x23; return true;
                case "pageup":
                case "pgup": vk = 0x21; return true;
                case "pagedown":
                case "pgdn": vk = 0x22; return true;
                case "up": vk = 0x26; return true;
                case "down": vk = 0x28; return true;
                case "left": vk = 0x25; return true;
                case "right": vk = 0x27; return true;
            }

            Keys key;
            if (Enum.TryParse<Keys>(token, true, out key))
            {
                int value = (int)key;
                if (value >= 3 && value <= 255 && !IsModifierVk(value))
                {
                    vk = value;
                    return true;
                }
            }

            return false;
        }
    }

    // =====================================================================
    // 捕获窗口
    // =====================================================================
    public sealed class CaptureForm : Form
    {
        const int NoTextTimeoutMs = 5000;

        enum SessionState
        {
            Idle,
            Capturing,
            Waiting,
            Pasting,
            Restoring
        }

        static string logPath;

        readonly TextBox box;
        readonly Timer pollTimer;
        readonly Timer pasteTimer;
        readonly Timer restoreTimer;

        HotkeySpec talkSpec;
        HotkeySpec triggerSpec;

        IntPtr targetWindow = IntPtr.Zero;
        string pendingText = "";
        string savedClipboardText;
        bool savedClipboardValid;

        SessionState state = SessionState.Idle;

        // 键盘钩子状态（都在安装钩子的 UI 线程上访问）
        bool logicalTalking;
        bool blockedUntilRelease;
        int nextSessionId;
        int activeSessionId;

        // 捕获 / 轮询状态
        DateTime releasedAt;
        DateTime lastChangeAt;
        string lastText = "";
        bool loggedWait;
        int pasteWaits;

        // 模拟语音热键时真正由我们按下的键
        bool triggerActive;
        readonly List<int> injectedTriggerKeys = new List<int>();

        // 物理按键状态（忽略 SendInput 注入的按键）
        readonly bool[] physDown = new bool[256];
        readonly bool[] suppressedDown = new bool[256];
        readonly bool[] swallowUp = new bool[256];

        IntPtr hookHandle = IntPtr.Zero;
        Native.LowLevelKeyboardProc hookProc;

        public CaptureForm(string lp)
        {
            logPath = lp;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Opacity = 0.01f;
            BackColor = Color.Black;
            Size = new Size(360, 220);

            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(area.Right - Width - 8, area.Bottom - Height - 8);

            box = new TextBox();
            box.Multiline = true;
            box.AcceptsReturn = true;
            box.ScrollBars = ScrollBars.Vertical;
            box.Dock = DockStyle.Fill;
            box.Font = new Font("Microsoft YaHei UI", 12f);
            box.BackColor = Color.Black;
            box.ForeColor = Color.White;
            box.BorderStyle = BorderStyle.None;
            box.TextChanged += delegate
            {
                if (Config.Debug) Log("捕获框文本变化: 长度=" + box.TextLength);
            };
            Controls.Add(box);

            pollTimer = new Timer();
            pollTimer.Interval = 80;
            pollTimer.Tick += PollTick;

            pasteTimer = new Timer();
            pasteTimer.Interval = 30;
            pasteTimer.Tick += PasteTick;

            restoreTimer = new Timer();
            restoreTimer.Interval = 350;
            restoreTimer.Tick += RestoreTick;

            Log("启动 VoicePaste（前台运行，Ctrl+C 退出）");
        }

        // 不显示在 Alt+Tab / 任务栏里
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        public bool Initialize()
        {
            string error;

            if (!HotkeySpec.TryParse(Config.TalkHotkey, out talkSpec, out error))
            {
                string msg = "说话热键无法解析: " + Config.TalkHotkey + "（" + error + "）";
                Log(msg);
                Console.WriteLine("[错误] " + msg);
                return false;
            }

            if (!HotkeySpec.TryParse(Config.VoiceTriggerHotkey, out triggerSpec, out error))
            {
                string msg = "语音热键无法解析: " + Config.VoiceTriggerHotkey + "（" + error + "）";
                Log(msg);
                Console.WriteLine("[错误] " + msg);
                return false;
            }

            if (string.Equals(Config.TalkHotkey.Trim(), Config.VoiceTriggerHotkey.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                string msg = "talk_hotkey 与 voice_trigger_hotkey 相同，可能互相冲突";
                Log("警告：" + msg);
                Console.WriteLine("[警告] " + msg);
            }

            if (talkSpec.MainVk != 0 && talkSpec.MainVk == triggerSpec.MainVk)
            {
                string msg = "两个热键使用了相同的主键，语音模拟可能失效";
                Log("警告：" + msg);
                Console.WriteLine("[警告] " + msg);
            }

            // 先强制创建窗口句柄；之后键盘钩子里的 BeginInvoke 才能工作。
            IntPtr unused = Handle;

            SyncPhysicalState();
            InstallKeyboardHook();

            return hookHandle != IntPtr.Zero;
        }

        void SyncPhysicalState()
        {
            int[] modifiers = new int[]
            {
                Native.VK_LSHIFT, Native.VK_RSHIFT,
                Native.VK_LCONTROL, Native.VK_RCONTROL,
                Native.VK_LMENU, Native.VK_RMENU,
                Native.VK_LWIN, Native.VK_RWIN
            };

            for (int i = 0; i < modifiers.Length; i++)
                physDown[modifiers[i]] = Native.IsKeyDown(modifiers[i]);
        }

        // -----------------------------------------------------------------
        // 键盘钩子
        // -----------------------------------------------------------------
        void InstallKeyboardHook()
        {
            try
            {
                hookProc = HookCallback;

                using (Process current = Process.GetCurrentProcess())
                using (ProcessModule module = current.MainModule)
                {
                    hookHandle = Native.SetWindowsHookEx(
                        Native.WH_KEYBOARD_LL,
                        hookProc,
                        Native.GetModuleHandle(module.ModuleName),
                        0);
                }

                if (hookHandle == IntPtr.Zero)
                {
                    Log("键盘钩子安装失败，错误码 " + Marshal.GetLastWin32Error());
                    Console.WriteLine("[错误] 键盘钩子安装失败");
                }
                else
                {
                    Log("已安装键盘钩子，说话热键: " + talkSpec.Spec);
                }
            }
            catch (Exception ex)
            {
                Log("键盘钩子安装异常: " + ex.Message);
                Console.WriteLine("[错误] 键盘钩子安装异常: " + ex.Message);
            }
        }

        void RemoveKeyboardHook()
        {
            if (hookHandle != IntPtr.Zero)
            {
                Native.UnhookWindowsHookEx(hookHandle);
                hookHandle = IntPtr.Zero;
            }
        }

        IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                bool isDown = msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN;
                bool isUp = msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP;

                if (isDown || isUp)
                {
                    Native.KBDLLHOOKSTRUCT info =
                        (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));

                    bool injected = (info.flags & Native.LLKHF_INJECTED) != 0;
                    bool ourInjection = injected && info.dwExtraInfo == Native.InjectionSignature;
                    int vk = (int)info.vkCode;

                    // 只忽略本程序自己注入的按键，避免误触发和递归；
                    // 其他程序（例如键盘映射工具）注入的按键仍然当作正常输入。
                    if (!ourInjection && vk >= 0 && vk < 256)
                    {
                        bool wasDown = physDown[vk];
                        bool isTalkKey = talkSpec != null && talkSpec.IsPartOfHotkey(vk);
                        bool swallow = false;

                        if (isDown) physDown[vk] = true;
                        else physDown[vk] = false;

                        if (isTalkKey)
                        {
                            if (isDown)
                            {
                                if (logicalTalking || state != SessionState.Idle || blockedUntilRelease)
                                {
                                    // 已经开始说话（或正在处理上一条结果）：全部吞掉。
                                    swallow = true;
                                    swallowUp[vk] = true;
                                }
                                else if (triggerSpec != null && triggerSpec.RequiresModifierVk(vk))
                                {
                                    // 这个键同时也是微信语音热键的一部分，让它的
                                    // keydown 正常传递，微信输入法才能看到它。
                                    // keyup 在说话结束后仍会被吞掉，避免弹出开始菜单。
                                    swallow = false;
                                }
                                else
                                {
                                    // 热键里但不是语音热键需要的键：按下时先吞掉。
                                    swallow = true;
                                    suppressedDown[vk] = true;
                                }
                            }
                            else
                            {
                                swallow = logicalTalking || suppressedDown[vk] || swallowUp[vk];
                                suppressedDown[vk] = false;
                                swallowUp[vk] = false;
                            }
                        }

                        if (isDown && !wasDown
                            && !logicalTalking
                            && state == SessionState.Idle
                            && !blockedUntilRelease
                            && talkSpec.IsSatisfied(physDown))
                        {
                            logicalTalking = true;
                            int id = ++nextSessionId;
                            activeSessionId = id;
                            AddDownTalkKeysToSwallowUp();
                            Post((Action)(() => BeginTalk(id)));
                        }
                        else if (isUp && logicalTalking && !talkSpec.IsSatisfied(physDown))
                        {
                            logicalTalking = false;
                            blockedUntilRelease = AnyTalkKeyDown();
                            int id = activeSessionId;
                            Post((Action)(() => EndTalk(id)));
                        }

                        if (isUp && blockedUntilRelease && !AnyTalkKeyDown())
                            blockedUntilRelease = false;

                        if (swallow) return (IntPtr)1;
                    }
                }
            }

            return Native.CallNextHookEx(hookHandle, nCode, wParam, lParam);
        }

        void Post(Action action)
        {
            try
            {
                if (IsHandleCreated)
                {
                    BeginInvoke(action);
                }
                else
                {
                    action();
                }
            }
            catch
            {
                try { action(); }
                catch { }
            }
        }

        void AddDownTalkKeysToSwallowUp()
        {
            for (int vk = 0; vk < 256; vk++)
            {
                if (physDown[vk] && talkSpec.IsPartOfHotkey(vk))
                    swallowUp[vk] = true;
            }
        }

        bool AnyTalkKeyDown()
        {
            for (int vk = 0; vk < 256; vk++)
            {
                if (physDown[vk] && talkSpec.IsPartOfHotkey(vk))
                    return true;
            }
            return false;
        }

        bool ModifiersOrTalkKeysDown()
        {
            for (int vk = 0; vk < 256; vk++)
            {
                if (!physDown[vk]) continue;
                if (HotkeySpec.IsModifierVk(vk)) return true;
                if (talkSpec != null && talkSpec.IsPartOfHotkey(vk)) return true;
            }
            return false;
        }

        // -----------------------------------------------------------------
        // 一次说话的开始 / 结束
        // -----------------------------------------------------------------
        void BeginTalk(int sessionId)
        {
            if (sessionId != activeSessionId || !logicalTalking) return;

            if (state != SessionState.Idle)
            {
                Log("忽略新的说话热键：上一条语音还在处理中");
                return;
            }

            targetWindow = Native.GetForegroundWindow();
            state = SessionState.Capturing;

            pendingText = "";
            lastText = "";
            loggedWait = false;
            pasteWaits = 0;
            savedClipboardValid = false;
            savedClipboardText = null;

            if (Config.RestoreClipboard)
            {
                savedClipboardText = GetClipboardText();
                savedClipboardValid = savedClipboardText != null;
            }

            Log("开始说话（按住 " + talkSpec.Spec + "）");
            if (Config.Debug)
            {
                Log("目标窗口: " + Native.GetWindowTitle(targetWindow)
                    + " (0x" + targetWindow.ToInt64().ToString("X") + ")");
            }

            ShowCapture();
            PressVoiceTrigger();
        }

        void EndTalk(int sessionId)
        {
            if (sessionId != activeSessionId) return;
            if (state != SessionState.Capturing) return;

            Log("松开 " + talkSpec.Spec + "，等待识别结果...");
            ReleaseVoiceTrigger();

            state = SessionState.Waiting;
            releasedAt = DateTime.UtcNow;
            lastText = box.Text;
            lastChangeAt = releasedAt;

            if (Config.Debug)
            {
                IntPtr fg = Native.GetForegroundWindow();
                Log("松开后前台: " + Native.GetWindowTitle(fg)
                    + " / 0x" + fg.ToInt64().ToString("X")
                    + "，捕获框 focused=" + box.Focused
                    + "，文字长度=" + box.TextLength);
            }

            pollTimer.Interval = 80;
            pollTimer.Start();
        }

        void ShowCapture()
        {
            box.Clear();
            TopMost = true;
            Show();
            Activate();

            bool ok = Native.ForceForeground(Handle);
            ActiveControl = box;
            box.Select();
            bool focused = box.Focus();

            if (Config.Debug)
            {
                Log("捕获框状态: Visible=" + Visible + " TopMost=" + TopMost
                    + " focused=" + box.Focused + " Focus()=" + focused);
            }

            if (!ok)
            {
                IntPtr current = Native.GetForegroundWindow();
                Log("警告：捕获窗口未能获得前台焦点（当前前台: "
                    + Native.GetWindowTitle(current)
                    + " / 0x" + current.ToInt64().ToString("X")
                    + "，捕获窗口 Visible=" + Visible
                    + " TopMost=" + TopMost + "）");
            }
            else if (Config.Debug)
            {
                Log("捕获窗口已获得焦点");
            }
        }

        void HideCapture()
        {
            try
            {
                box.Clear();
                Hide();
            }
            catch
            {
            }
        }

        void ReturnFocusAndHide()
        {
            if (targetWindow != IntPtr.Zero && Native.IsWindow(targetWindow))
            {
                bool ok = Native.ForceForeground(targetWindow);
                if (!ok)
                {
                    IntPtr current = Native.GetForegroundWindow();
                    Log("警告：无法切回原窗口（当前前台: "
                        + Native.GetWindowTitle(current)
                        + " / 0x" + current.ToInt64().ToString("X") + "）");
                }
            }

            HideCapture();
        }

        // -----------------------------------------------------------------
        // 模拟微信语音热键
        // -----------------------------------------------------------------
        void PressVoiceTrigger()
        {
            if (triggerActive) return;
            triggerActive = true;
            injectedTriggerKeys.Clear();

            PressTriggerModifier(triggerSpec.NeedCtrl, triggerSpec.CtrlSide,
                Native.VK_LCONTROL, Native.VK_RCONTROL);
            PressTriggerModifier(triggerSpec.NeedAlt, triggerSpec.AltSide,
                Native.VK_LMENU, Native.VK_RMENU);
            PressTriggerModifier(triggerSpec.NeedShift, triggerSpec.ShiftSide,
                Native.VK_LSHIFT, Native.VK_RSHIFT);
            PressTriggerModifier(triggerSpec.NeedWin, triggerSpec.WinSide,
                Native.VK_LWIN, Native.VK_RWIN);

            if (triggerSpec.MainVk != 0)
                SendTriggerKey(triggerSpec.MainVk);

            Log("已模拟语音热键: " + triggerSpec.Spec);

            if (Config.Debug)
            {
                IntPtr fg = Native.GetForegroundWindow();
                Log("模拟语音热键后前台: " + Native.GetWindowTitle(fg)
                    + " / 0x" + fg.ToInt64().ToString("X")
                    + "，捕获框 focused=" + box.Focused);
            }
        }

        void PressTriggerModifier(bool needed, int side, int leftVk, int rightVk)
        {
            if (!needed) return;

            bool leftDown = physDown[leftVk];
            bool rightDown = physDown[rightVk];

            bool alreadyDown;
            if (side < 0) alreadyDown = leftDown;
            else if (side > 0) alreadyDown = rightDown;
            else alreadyDown = leftDown || rightDown;

            if (alreadyDown) return;

            int vk = side > 0 ? rightVk : leftVk;
            SendTriggerKey(vk);
        }

        void SendTriggerKey(int vk)
        {
            if (Native.SendKey(vk, false))
            {
                injectedTriggerKeys.Add(vk);
            }
            else
            {
                Log("模拟按键失败: VK=0x" + vk.ToString("X2"));
            }
        }

        void ReleaseVoiceTrigger()
        {
            if (!triggerActive) return;
            triggerActive = false;

            for (int i = injectedTriggerKeys.Count - 1; i >= 0; i--)
            {
                if (!Native.SendKey(injectedTriggerKeys[i], true))
                    Log("释放模拟按键失败: VK=0x" + injectedTriggerKeys[i].ToString("X2"));
            }
            injectedTriggerKeys.Clear();

            if (Config.Debug)
            {
                IntPtr fg = Native.GetForegroundWindow();
                Log("结束模拟语音热键后前台: " + Native.GetWindowTitle(fg)
                    + " / 0x" + fg.ToInt64().ToString("X")
                    + "，捕获框 focused=" + box.Focused);
            }
        }

        // -----------------------------------------------------------------
        // 轮询捕获框 → 粘贴
        // -----------------------------------------------------------------
        void PollTick(object sender, EventArgs e)
        {
            if (state != SessionState.Waiting)
            {
                pollTimer.Stop();
                return;
            }

            string current = box.Text;
            DateTime now = DateTime.UtcNow;

            if (!string.Equals(current, lastText, StringComparison.Ordinal))
            {
                lastText = current;
                lastChangeAt = now;
                if (Config.Debug)
                {
                    IntPtr fg = Native.GetForegroundWindow();
                    Log("捕获文字长度: " + current.Length
                        + "，前台: " + Native.GetWindowTitle(fg));
                }
            }

            string trimmed = current.Trim();
            double sinceChange = (now - lastChangeAt).TotalMilliseconds;

            if (trimmed.Length > 0)
            {
                if (sinceChange >= Config.CommitDelayMs)
                {
                    if (ModifiersOrTalkKeysDown())
                    {
                        if (!loggedWait)
                        {
                            loggedWait = true;
                            Log("文字已停止增长，等待按键松开后粘贴...");
                        }
                    }
                    else
                    {
                        FinishWithText(trimmed);
                    }
                }
            }
            else if ((now - releasedAt).TotalMilliseconds >= NoTextTimeoutMs)
            {
                pollTimer.Stop();

                string msg = "超时：松开热键后 " + (NoTextTimeoutMs / 1000)
                    + " 秒仍未捕获到文字。请确认微信语音已启用、voice_trigger_hotkey 配置正确。";
                Log(msg);
                Console.WriteLine("[警告] " + msg);

                ReturnFocusAndHide();
                ResetSession();
            }
        }

        void FinishWithText(string text)
        {
            pollTimer.Stop();
            Log("捕获到文字长度: " + text.Length);
            if (Config.Debug) Log("捕获文字: " + text);

            if (!SetClipboardText(text))
            {
                string msg = "剪贴板写入失败，已取消自动粘贴";
                Log(msg);
                Console.WriteLine("[警告] " + msg);

                ReturnFocusAndHide();
                ResetSession();
                return;
            }

            pendingText = text;

            if (!Config.PasteOnFinish)
            {
                Console.WriteLine("[剪贴板] " + text);
                ReturnFocusAndHide();
                ResetSession();
                return;
            }

            state = SessionState.Pasting;
            pasteWaits = 0;
            pasteTimer.Interval = 30;
            pasteTimer.Start();
        }

        void PasteTick(object sender, EventArgs e)
        {
            if (state != SessionState.Pasting)
            {
                pasteTimer.Stop();
                return;
            }

            // 用户可能还按着 Win/Shift/Alt 或热键主键；此时发送 Ctrl+V
            // 会变成 Win+Ctrl+V 之类。等物理按键全部松开再粘贴。
            if (ModifiersOrTalkKeysDown())
            {
                pasteWaits++;
                if (pasteWaits == 1 || pasteWaits % 50 == 0)
                    Log("等待按键松开后粘贴...");
                return;
            }

            pasteTimer.Stop();

            if (targetWindow == IntPtr.Zero || !Native.IsWindow(targetWindow))
            {
                string msg = "原目标窗口已不存在，文字已保留在剪贴板";
                Log(msg);
                Console.WriteLine("[警告] " + msg);

                HideCapture();
                ResetSession();
                return;
            }

            Native.ForceForeground(targetWindow);

            if (Native.GetForegroundWindow() != targetWindow)
            {
                string msg = "无法切回原目标窗口，文字已保留在剪贴板";
                Log(msg);
                Console.WriteLine("[警告] " + msg);

                HideCapture();
                ResetSession();
                return;
            }

            if (!Native.SendPaste())
                Log("模拟 Ctrl+V 失败，文字已保留在剪贴板");

            Console.WriteLine("[粘贴] " + pendingText);
            HideCapture();

            if (Config.RestoreClipboard && savedClipboardValid)
            {
                state = SessionState.Restoring;
                restoreTimer.Interval = 350;
                restoreTimer.Start();
            }
            else
            {
                ResetSession();
            }
        }

        void RestoreTick(object sender, EventArgs e)
        {
            restoreTimer.Stop();

            if (savedClipboardValid)
            {
                bool ok;
                if (!string.IsNullOrEmpty(savedClipboardText))
                {
                    ok = SetClipboardText(savedClipboardText);
                }
                else
                {
                    try
                    {
                        Clipboard.Clear();
                        ok = true;
                    }
                    catch
                    {
                        ok = false;
                    }
                }

                if (!ok) Log("恢复剪贴板失败");
            }

            ResetSession();
        }

        void ResetSession()
        {
            pollTimer.Stop();
            pasteTimer.Stop();
            restoreTimer.Stop();

            ReleaseVoiceTrigger();

            state = SessionState.Idle;
            pendingText = "";
            lastText = "";
            loggedWait = false;
            pasteWaits = 0;
            savedClipboardValid = false;
            savedClipboardText = null;
            targetWindow = IntPtr.Zero;
        }

        // -----------------------------------------------------------------
        // 剪贴板
        // -----------------------------------------------------------------
        static string GetClipboardText()
        {
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    if (Clipboard.ContainsText()) return Clipboard.GetText();
                    return null;
                }
                catch
                {
                    System.Threading.Thread.Sleep(20);
                }
            }
            return null;
        }

        static bool SetClipboardText(string text)
        {
            for (int i = 0; i < 6; i++)
            {
                try
                {
                    Clipboard.SetText(text);
                    return true;
                }
                catch
                {
                    System.Threading.Thread.Sleep(30);
                }
            }
            return false;
        }

        // -----------------------------------------------------------------
        // 退出
        // -----------------------------------------------------------------
        public void RequestExit()
        {
            try
            {
                if (IsHandleCreated)
                    BeginInvoke((Action)DoExit);
                else
                    DoExit();
            }
            catch
            {
                DoExit();
            }
        }

        void DoExit()
        {
            Log("收到 Ctrl+C，退出");

            pollTimer.Stop();
            pasteTimer.Stop();
            restoreTimer.Stop();

            ReleaseVoiceTrigger();
            logicalTalking = false;
            RemoveKeyboardHook();
            HideCapture();

            Application.ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ReleaseVoiceTrigger();
                RemoveKeyboardHook();

                pollTimer.Dispose();
                pasteTimer.Dispose();
                restoreTimer.Dispose();
            }

            base.Dispose(disposing);
        }

        // -----------------------------------------------------------------
        // 日志
        // -----------------------------------------------------------------
        internal static void Log(string message)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + message;

            try { Console.WriteLine(line); }
            catch { }

            if (!string.IsNullOrEmpty(logPath))
            {
                try
                {
                    File.AppendAllText(logPath, line + Environment.NewLine, new UTF8Encoding(false));
                }
                catch
                {
                }
            }
        }
    }

    // =====================================================================
    // 入口
    // =====================================================================
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; }
            catch { }
            try { Console.Title = "VoicePaste"; }
            catch { }

            string dir = AppDomain.CurrentDomain.BaseDirectory;
            Config.Load(Path.Combine(dir, "voicepaste.conf"));
            string logPath = Path.Combine(dir, "voicepaste.log");

            KillExistingProcesses();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            CaptureForm form = new CaptureForm(logPath);
            if (!form.Initialize())
            {
                form.Dispose();
                Console.WriteLine("按任意键退出...");
                try { Console.ReadKey(true); }
                catch { }
                return;
            }

            Console.WriteLine();
            Console.WriteLine("==============================================");
            Console.WriteLine("  VoicePaste 已运行");
            Console.WriteLine("  按住 " + Config.TalkHotkey + " 说话，松开自动粘贴");
            Console.WriteLine("  按 Ctrl+C 退出");
            Console.WriteLine("==============================================");
            Console.WriteLine();

            Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e)
            {
                e.Cancel = true; // 让 UI 线程清理钩子和定时器后再退出
                form.RequestExit();
            };

            Application.Run();

            form.Dispose();
        }

        // 终止本程序可能残留的旧进程（保留当前实例）
        static void KillExistingProcesses()
        {
            try
            {
                Process current = Process.GetCurrentProcess();
                string currentPath = null;

                try
                {
                    using (ProcessModule currentModule = current.MainModule)
                        currentPath = currentModule.FileName;
                }
                catch
                {
                }

                Process[] processes = Process.GetProcessesByName(current.ProcessName);

                foreach (Process p in processes)
                {
                    if (p.Id == current.Id) continue;

                    bool samePath = false;
                    try
                    {
                        using (ProcessModule module = p.MainModule)
                        {
                            samePath = currentPath == null
                                || string.Equals(module.FileName, currentPath, StringComparison.OrdinalIgnoreCase);
                        }
                    }
                    catch
                    {
                        // 无法读取路径时，按同名进程处理。
                        samePath = true;
                    }

                    if (!samePath) continue;

                    try
                    {
                        int pid = p.Id;
                        p.Kill();
                        p.WaitForExit(2000);
                        Console.WriteLine("[清理] 已终止残留进程 PID=" + pid);
                    }
                    catch (Exception ex)
                    {
                        try { Console.WriteLine("[清理] 终止残留进程失败: " + ex.Message); }
                        catch { }
                    }
                    finally
                    {
                        p.Dispose();
                    }
                }
            }
            catch
            {
            }
        }
    }
}
