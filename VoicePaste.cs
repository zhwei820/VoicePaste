/*
 * VoicePaste — 微信语音输入 → 捕获 → 粘贴 到任意窗口
 * --------------------------------------------------------
 * 解决的问题：电脑版微信的语音输入（按住 Ctrl+Win 说话）在 WSL / 终端里输入
 * 会把整句文字"双写"重复。原因是微信靠模拟键盘注入文字，终端又回显一遍。
 *
 * 本程序原理：
 *   1) 你按住“说话”热键（如 Ctrl+Alt+W）→ 程序记住当前前台窗口，并把一个隐藏
 *      文本框设为焦点目标；
 *   2) 程序自动按下并保持 Ctrl+Win（调起微信语音），你只管说话；
 *   3) 你松开“说话”热键 → 程序松开 Ctrl+Win（微信把文字提交进隐藏文本框，不会重复），
 *      读出整段文字 → 复制到剪贴板 → 切回原窗口 → 粘贴（Ctrl+V）。
 *
 * 也就是说：按下热键开始说话，松开热键就直接粘贴。
 *
 * 纯 .NET Framework + WinForms，无第三方依赖，编译成独立 exe。
 * 用 Windows 自带 csc 编译（兼容 C# 5）：
 *   csc.exe /nologo /optimize+ /t:winexe /out:VoicePaste.exe VoicePaste.cs
 *
 * 热键、行为都可在同目录 voicepaste.conf 里改，见下方 Config.Load()。
 */

using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace VoicePaste
{
    // ---------------------------------------------------------------------
    // 配置：默认值 + 从 voicepaste.conf 读取（key=value，# 为注释）
    // ---------------------------------------------------------------------
    static class Config
    {
        public static string TalkHotkey = "Ctrl+Alt+W";    // 按住说话、松开粘贴
        public static string ExitHotkey   = "Ctrl+Alt+X";  // 退出程序
        public static bool   AutoTriggerVoice = true;      // 自动模拟按下 Ctrl+Win 调起微信语音
        public static int    CommitDelayMs = 300;          // 松开 Ctrl+Win 后等待文字落框的时间
        public static bool   PasteOnFinish = true;         // 结束后自动粘贴（否则只放进剪贴板）
        public static bool   RestoreClipboard = false;     // 是否在粘贴后恢复剪贴板原内容

        public static void Load(string path)
        {
            if (!File.Exists(path)) return;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string k = line.Substring(0, eq).Trim();
                string v = line.Substring(eq + 1).Trim();
                switch (k.ToLowerInvariant())
                {
                    case "talk_hotkey":         Config.TalkHotkey = v; break;
                    case "start_hotkey":        Config.TalkHotkey = v; break; // 向后兼容
                    case "exit_hotkey":         Config.ExitHotkey = v; break;
                    case "auto_trigger_voice":  Config.AutoTriggerVoice = ParseBool(v, Config.AutoTriggerVoice); break;
                    case "commit_delay_ms":
                        {
                            int d;
                            if (int.TryParse(v, out d) && d > 0) Config.CommitDelayMs = d;
                            break;
                        }
                    case "paste_on_finish":     Config.PasteOnFinish = ParseBool(v, Config.PasteOnFinish); break;
                    case "restore_clipboard":   Config.RestoreClipboard = ParseBool(v, Config.RestoreClipboard); break;
                }
            }
        }

        static bool ParseBool(string v, bool def)
        {
            bool b;
            if (bool.TryParse(v, out b)) return b;
            if (v == "1") return true;
            if (v == "0") return false;
            return def;
        }
    }

    // ---------------------------------------------------------------------
    // Win32 P/Invoke
    // ---------------------------------------------------------------------
    static class Native
    {
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;
        public const uint MOD_NOREPEAT = 0x4000;

        public const int WM_HOTKEY = 0x0312;
        public const int VK_CONTROL = 0x11;
        public const int VK_LWIN = 0x5B;
        public const int VK_V = 0x56;

        public const uint INPUT_KEYBOARD = 1;
        public const uint KEYEVENTF_KEYUP = 0x0002;

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
            public uint wParamL;
            public uint wParamH;
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
            public INPUTUNION U;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        // --- 低层键盘钩子：用于监听“按下/松开” ---
        public const int WH_KEYBOARD_LL = 13;
        public const int WM_KEYDOWN = 0x0100;
        public const int WM_KEYUP = 0x0101;
        public const int WM_SYSKEYDOWN = 0x0104;
        public const int WM_SYSKEYUP = 0x0105;

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

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);
        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        public static bool IsKeyDown(int vk)
        {
            return (GetAsyncKeyState(vk) & 0x8000) != 0;
        }

        // 模拟按下/松开单个按键
        public static void SendKey(int vk, bool keyUp)
        {
            INPUT[] inp = new INPUT[1];
            inp[0].type = INPUT_KEYBOARD;
            inp[0].U.ki.wVk = (ushort)vk;
            inp[0].U.ki.wScan = 0;
            inp[0].U.ki.dwFlags = keyUp ? KEYEVENTF_KEYUP : 0u;
            inp[0].U.ki.time = 0;
            inp[0].U.ki.dwExtraInfo = IntPtr.Zero;
            SendInput(1, inp, Marshal.SizeOf(typeof(INPUT)));
        }

        // 按下保持 Ctrl+Win（用于调起并保持微信语音输入）
        public static void ComboDown()
        {
            SendKey(VK_CONTROL, false);
            SendKey(VK_LWIN, false);
        }
        // 松开 Ctrl+Win（微信提交文字）
        public static void ComboUp()
        {
            SendKey(VK_LWIN, true);
            SendKey(VK_CONTROL, true);
        }
        // 发送 Ctrl+V 粘贴
        public static void PasteKey()
        {
            SendKey(VK_CONTROL, false);
            SendKey(VK_V, false);
            SendKey(VK_V, true);
            SendKey(VK_CONTROL, true);
        }
    }

    // ---------------------------------------------------------------------
    // 主窗体：既是隐藏捕获框，也是热键宿主 + 托盘图标
    // ---------------------------------------------------------------------
    public class CaptureForm : Form
    {
        const int ID_EXIT = 3;

        TextBox box;
        NotifyIcon tray;
        ContextMenuStrip menu;
        IntPtr targetWindow;
        bool holdingCtrlWin = false;
        bool talking = false;             // 当前是否处于“按住说话”中
        string savedClipboard = null;
        static string logPath;

        // 解析后的“按住说话”热键
        uint talkMod = 0;
        uint talkVk = 0;

        // 低层键盘钩子句柄
        IntPtr hookHandle = IntPtr.Zero;
        Native.LowLevelKeyboardProc hookProc;

        public CaptureForm(string lp)
        {
            logPath = lp;
            Log("启动 VoicePaste");

            // --- 隐藏捕获框 ---
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Opacity = 0.03;                       // 几乎不可见，但保持可聚焦接收 IME
            Size = new Size(320, 180);
            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Size.Width - 12, wa.Bottom - Size.Height - 12);

            box = new TextBox();
            box.Multiline = true;
            box.AcceptsReturn = true;
            box.ScrollBars = ScrollBars.Vertical;
            box.Dock = DockStyle.Fill;
            box.Font = new Font("Microsoft YaHei UI", 12f);
            Controls.Add(box);

            // --- 托盘图标（便于退出/查看状态） ---
            menu = new ContextMenuStrip();
            menu.Items.Add("按住说话 / 松开粘贴", null, (s, e) => { if (!talking) OnTalkDown(); });
            menu.Items.Add("退出", null, (s, e) => DoExit());

            tray = new NotifyIcon();
            tray.Icon = SystemIcons.Application;
            tray.Text = "语音输入粘贴 (VoicePaste)";
            tray.ContextMenuStrip = menu;
            tray.Visible = true;
            tray.DoubleClick += (s, e) => { if (!talking) OnTalkDown(); };
            tray.ShowBalloonTip(1500, "VoicePaste 已运行",
                "按住" + Config.TalkHotkey + "说话\n松开即粘贴\n退出=" + Config.ExitHotkey,
                ToolTipIcon.Info);

            HandleCreated += (s, e) => RegisterExitHotkey();
            HandleCreated += (s, e) => InstallKeyboardHook();
            FormClosed += (s, e) => { RemoveKeyboardHook(); UnregisterExitHotkey(); };
        }

        // -----------------------------------------------------------------
        // 热键：退出键用 RegisterHotKey；说话键用低层键盘钩子（监听按下/松开）
        // -----------------------------------------------------------------
        void RegisterExitHotkey()
        {
            uint mod, vk;
            if (!TryParseHotkey(Config.ExitHotkey, out mod, out vk))
            {
                Log("退出热键解析失败: " + Config.ExitHotkey);
                return;
            }
            if (!Native.RegisterHotKey(this.Handle, ID_EXIT, mod | Native.MOD_NOREPEAT, vk))
            {
                Log("退出热键注册失败(可能被占用): " + Config.ExitHotkey);
                MessageBox.Show("退出热键注册失败(可能被其他程序占用): " + Config.ExitHotkey + "\n请在 voicepaste.conf 中更换。",
                    "VoicePaste", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else
            {
                Log("已注册退出热键: " + Config.ExitHotkey);
            }
        }

        void UnregisterExitHotkey()
        {
            Native.UnregisterHotKey(this.Handle, ID_EXIT);
        }

        void InstallKeyboardHook()
        {
            uint mod, vk;
            if (!TryParseHotkey(Config.TalkHotkey, out mod, out vk))
            {
                Log("说话热键解析失败: " + Config.TalkHotkey);
                MessageBox.Show("说话热键格式无法解析: " + Config.TalkHotkey + "\n示例: Ctrl+Alt+W",
                    "VoicePaste", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            talkMod = mod;
            talkVk = vk;

            hookProc = HookCallback;
            using (var cur = System.Diagnostics.Process.GetCurrentProcess())
            using (var mainModule = cur.MainModule)
            {
                hookHandle = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, hookProc,
                    Native.GetModuleHandle(mainModule.ModuleName), 0);
            }
            if (hookHandle == IntPtr.Zero)
                Log("键盘钩子安装失败，错误码 " + Marshal.GetLastWin32Error());
            else
                Log("已安装键盘钩子，说话热键: " + Config.TalkHotkey + "（按住说话，松开粘贴）");
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
                bool keyDown = (msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN);
                bool keyUp = (msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP);
                if (keyDown || keyUp)
                {
                    if (talkVk == 0)
                    {
                        // 纯修饰键组合（如 Win+Alt）：根据整体按下状态判定开始/结束
                        bool full = ModifiersMatch(talkMod);
                        if (!talking && full)
                        {
                            talking = true;
                            OnTalkDown();
                            return (IntPtr)1;
                        }
                        if (talking && !full)
                        {
                            talking = false;
                            OnTalkUp();
                            return (IntPtr)1;
                        }
                    }
                    else
                    {
                        var info = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
                        if (info.vkCode == talkVk)
                        {
                            bool modOk = ModifiersMatch(talkMod);
                            if (keyDown && modOk && !talking)
                            {
                                talking = true;
                                OnTalkDown();
                                return (IntPtr)1;
                            }
                            else if (keyUp && talking)
                            {
                                talking = false;
                                OnTalkUp();
                                return (IntPtr)1;
                            }
                        }
                    }
                }
            }
            return Native.CallNextHookEx(hookHandle, nCode, wParam, lParam);
        }

        bool ModifiersMatch(uint mod)
        {
            bool ctrl = (mod & Native.MOD_CONTROL) != 0;
            bool alt = (mod & Native.MOD_ALT) != 0;
            bool shift = (mod & Native.MOD_SHIFT) != 0;
            bool win = (mod & Native.MOD_WIN) != 0;

            bool ctrlDown = Native.IsKeyDown(Native.VK_CONTROL);
            bool altDown = Native.IsKeyDown(0xA4);   // VK_LMENU（左 Alt）
            bool shiftDown = Native.IsKeyDown(0x10);  // VK_SHIFT
            bool winDown = Native.IsKeyDown(Native.VK_LWIN) || Native.IsKeyDown(0x5C); // 左/右 Win

            if (ctrl != ctrlDown) return false;
            if (alt != altDown) return false;
            if (shift != shiftDown) return false;
            if (win != winDown) return false;
            return true;
        }

        static bool TryParseHotkey(string spec, out uint mod, out uint vk)
        {
            mod = 0;
            vk = 0;
            if (string.IsNullOrWhiteSpace(spec)) return false;
            var parts = spec.Split('+');
            foreach (var p in parts)
            {
                string t = p.Trim();
                if (t.Length == 0) return false;
                switch (t.ToLowerInvariant())
                {
                    case "ctrl":
                    case "control":
                    case "ctl":
                        mod |= Native.MOD_CONTROL;
                        break;
                    case "alt":
                        mod |= Native.MOD_ALT;
                        break;
                    case "shift":
                        mod |= Native.MOD_SHIFT;
                        break;
                    case "win":
                    case "windows":
                    case "meta":
                    case "cmd":
                        mod |= Native.MOD_WIN;
                        break;
                    default:
                        if (t.Length == 1)
                        {
                            char c = char.ToUpperInvariant(t[0]);
                            if (c >= 'A' && c <= 'Z') { vk = (uint)c; break; }
                            if (c >= '0' && c <= '9') { vk = (uint)c; break; }
                            return false;
                        }
                        Keys key;
                        if (Enum.TryParse(t, true, out key)) { vk = (uint)key; break; }
                        return false;
                }
            }
            return (mod != 0 || vk != 0);
        }

        // -----------------------------------------------------------------
        // 消息循环：退出热键触发
        // -----------------------------------------------------------------
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                if (id == ID_EXIT) { DoExit(); return; }
            }
            base.WndProc(ref m);
        }

        // -----------------------------------------------------------------
        // 动作
        // -----------------------------------------------------------------
        void OnTalkDown()
        {
            Log("开始说话（按下 " + Config.TalkHotkey + "）");
            targetWindow = Native.GetForegroundWindow();   // 记住“要粘贴进”的窗口
            savedClipboard = GetClipboardText();           // 备份原剪贴板（可选恢复）

            box.Clear();
            Show();
            Activate();                                   // 显示并聚焦隐藏捕获框
            box.Focus();

            if (Config.AutoTriggerVoice)
            {
                Native.ComboDown();                       // 按住 Ctrl+Win → 调起微信语音
                holdingCtrlWin = true;
                Log("已模拟按下 Ctrl+Win");
            }
            else
            {
                tray.ShowBalloonTip(1200, "VoicePaste",
                    "请在目标窗口手动点开微信语音，说完后松开 " + Config.TalkHotkey, ToolTipIcon.Info);
            }
        }

        void OnTalkUp()
        {
            if (!IsHandleCreated) return;
            Log("松开 " + Config.TalkHotkey + "，结束并粘贴");
            if (holdingCtrlWin)
            {
                Native.ComboUp();                         // 松开 Ctrl+Win → 微信提交文字到捕获框
                holdingCtrlWin = false;
                System.Threading.Thread.Sleep(Config.CommitDelayMs); // 等文字落框
            }

            string text = box.Text.Trim();
            Log("捕获到文字长度: " + text.Length);
            if (text.Length > 0)
            {
                SetClipboardText(text);                   // 放进剪贴板
                if (Config.PasteOnFinish)
                {
                    Native.SetForegroundWindow(targetWindow); // 切回原窗口
                    System.Threading.Thread.Sleep(60);
                    Native.PasteKey();                    // Ctrl+V 粘贴
                    Log("已粘贴到目标窗口");
                }
                if (Config.RestoreClipboard)
                {
                    RestoreClipboardLater(savedClipboard);
                }
            }
            else
            {
                tray.ShowBalloonTip(1800, "VoicePaste",
                    "未捕获到文字。若微信没提交，请确认微信语音已开启、麦克风正常，可调整 voicepaste.conf 的 commit_delay_ms。",
                    ToolTipIcon.Warning);
            }

            box.Clear();
            Hide();                                       // 隐藏捕获框，让出焦点
            if (!Config.PasteOnFinish)
            {
                Native.SetForegroundWindow(targetWindow); // 至少让原窗口回到前台
            }
        }

        void DoExit()
        {
            Log("退出");
            if (holdingCtrlWin)
            {
                Native.ComboUp();
                holdingCtrlWin = false;
            }
            tray.Visible = false;
            tray.Dispose();
            Close();
            Application.Exit();
        }

        // -----------------------------------------------------------------
        // 剪贴板
        // -----------------------------------------------------------------
        static string GetClipboardText()
        {
            try
            {
                if (Clipboard.ContainsText()) return Clipboard.GetText();
            }
            catch { }
            return null;
        }
        static void SetClipboardText(string s)
        {
            try { Clipboard.SetText(s); }
            catch (Exception ex) { Log("剪贴板写入失败: " + ex.Message); }
        }
        void RestoreClipboardLater(string original)
        {
            System.Threading.Thread.Sleep(150);
            if (original == null) return;
            try
            {
                if (Clipboard.ContainsText()) Clipboard.SetText(original);
            }
            catch { }
        }

        // -----------------------------------------------------------------
        // 日志（方便排查）
        // -----------------------------------------------------------------
        static void Log(string msg)
        {
            try
            {
                File.AppendAllText(logPath,
                    DateTime.Now.ToString("HH:mm:ss") + "  " + msg + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch { }
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            Config.Load(Path.Combine(dir, "voicepaste.conf"));
            string logPath = Path.Combine(dir, "voicepaste.log");

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var form = new CaptureForm(logPath))
            {
                Application.Run(form);
            }
        }
    }
}
