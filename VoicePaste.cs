/*
 * VoicePaste — 微信语音输入 → 捕获 → 粘贴 到任意窗口
 * --------------------------------------------------------
 * 解决的问题：电脑版微信的语音输入（按住 Ctrl+Win 说话）在把文字注入到
 * 终端等窗口时会"双写"重复（微信靠模拟键盘输入文字，终端又回显一遍）。
 *
 * 本程序原理：
 *   1) 你按住“说话”热键（默认 Win+左Alt）→ 程序记住当前前台窗口，并把一个
 *      隐藏文本框设为焦点目标；
 *   2) 程序补按下 Ctrl（配合你按住的 Win）调起微信语音，你只管说话；
 *   3) 你松开热键 → 程序松开 Ctrl，微信把文字提交进隐藏文本框（不会重复），
 *      读出整段文字 → 复制到剪贴板 → 切回原窗口 → 粘贴（Ctrl+V）。
 *
 * 也就是说：按住热键开始说话，松开热键就直接粘贴。
 *
 * 这是前台控制台程序：命令行启动后一直挂在前台，按 Ctrl+C 直接退出。
 * 纯 .NET Framework + WinForms，无第三方依赖，编译成独立 exe。
 * 用 Windows 自带 csc 编译（兼容 C# 5）：
 *   csc.exe /nologo /optimize+ /t:exe /out:VoicePaste.exe VoicePaste.cs
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
        public static string TalkHotkey = "Win+Alt";       // 按住说话、松开粘贴（热键需包含主键；纯修饰键如 Win+Alt 也支持）
        public static bool   AutoTriggerVoice = true;      // 自动模拟按键调起微信语音
        public static int    CommitDelayMs = 300;          // 松开热键后等待文字落框的时间
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

        public const int VK_CONTROL = 0x11;
        public const int VK_LWIN = 0x5B;
        public const int VK_RWIN = 0x5C;
        public const int VK_LMENU = 0xA4;   // 左 Alt
        public const int VK_RMENU = 0xA5;   // 右 Alt
        public const int VK_V = 0x56;

        public const uint INPUT_KEYBOARD = 1;
        public const uint KEYEVENTF_KEYUP = 0x0002;
        public const uint LLKHF_INJECTED = 0x10; // 键盘事件来自 SendInput/模拟

        // 低层键盘钩子
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

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
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

        // 调起微信语音：按下 Ctrl（Win 由用户物理按住，故不重复按 Win）
        public static void VoiceDown()
        {
            SendKey(VK_CONTROL, false);
        }
        // 结束：松开 Ctrl（微信提交文字）
        public static void VoiceUp()
        {
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
    // 隐藏捕获框：接收微信注入的文字，避免在目标窗口双写
    // ---------------------------------------------------------------------
    public class CaptureForm : Form
    {
        TextBox box;
        IntPtr targetWindow;
        bool talking = false;             // 当前是否处于“按住说话”中
        string savedClipboard = null;
        static string logPath;

        // 解析后的“按住说话”热键
        uint talkMod = 0;
        uint talkVk = 0;
        bool hotkeyHasWin = false;        // 热键是否含 Win
        bool wantWin = false;             // 是否要求 Win 修饰键
        bool wantAlt = false;             // 是否要求 Alt 修饰键

        // 物理按键跟踪（避免被模拟按键干扰）
        bool physWin = false;
        bool physAlt = false;

        // 低层键盘钩子句柄
        IntPtr hookHandle = IntPtr.Zero;
        Native.LowLevelKeyboardProc hookProc;

        public CaptureForm(string lp)
        {
            logPath = lp;
            Log("启动 VoicePaste（前台运行，Ctrl+C 退出）");

            // --- 隐藏捕获框：几乎不可见，但保持可聚焦接收 IME ---
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Opacity = 0.03;
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
        }

        // -----------------------------------------------------------------
        // 热键解析 / 键盘钩子
        // -----------------------------------------------------------------
        public void InstallKeyboardHook()
        {
            try
            {
                uint mod, vk;
                if (!TryParseHotkey(Config.TalkHotkey, out mod, out vk))
                {
                    Log("说话热键解析失败: " + Config.TalkHotkey);
                    Console.WriteLine("[错误] 说话热键格式无法解析: " + Config.TalkHotkey + "（示例: Win+Alt 或 Ctrl+Alt+W）");
                    return;
                }
                talkMod = mod;
                talkVk = vk;
                hotkeyHasWin = (mod & Native.MOD_WIN) != 0;
                wantWin = hotkeyHasWin;
                wantAlt = (mod & Native.MOD_ALT) != 0;

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
                    Log("已安装键盘钩子，说话热键: " + Config.TalkHotkey);
            }
            catch (Exception ex)
            {
                Log("键盘钩子安装异常: " + ex.Message);
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
                bool keyDown = (msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN);
                bool keyUp = (msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP);
                if (keyDown || keyUp)
                {
                    var info = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
                    bool injected = (info.flags & Native.LLKHF_INJECTED) != 0; // 忽略自己模拟的按键

                    if (talkVk == 0)
                    {
                        // 纯修饰键组合（如 Win+左Alt）：精确跟踪物理按键状态
                        if (!injected)
                        {
                            uint vk = info.vkCode;
                            bool isWin = (vk == Native.VK_LWIN || vk == Native.VK_RWIN);
                            bool isLAlt = (vk == Native.VK_LMENU);

                            // 只关心与热键相关的键
                            bool relevant = (wantWin && isWin) || (wantAlt && isLAlt);
                            if (relevant)
                            {
                                if (!talking)
                                {
                                    if (isWin) physWin = keyDown;
                                    if (isLAlt) physAlt = keyDown;

                                    bool full = (!wantWin || physWin) && (!wantAlt || physAlt);
                                    if (full)
                                    {
                                        talking = true;
                                        OnTalkDown();
                                    }
                                }
                                else
                                {
                                    if (keyUp)
                                    {
                                        talking = false;
                                        physWin = false;
                                        physAlt = false;
                                        OnTalkUp();
                                    }
                                }
                                // 吞掉 Win / 左Alt，避免弹开始菜单、激活菜单栏
                                return (IntPtr)1;
                            }
                        }
                    }
                    else
                    {
                        // 有主键（如 Ctrl+Alt+W）
                        var vk = info.vkCode;
                        if (!injected && vk == talkVk)
                        {
                            if (keyDown && ModifiersMatch(talkMod) && !talking)
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

            bool ctrlDown = IsKeyDown(Native.VK_CONTROL);
            bool altDown = IsKeyDown(Native.VK_LMENU);
            bool shiftDown = IsKeyDown(0x10);  // VK_SHIFT
            bool winDown = IsKeyDown(Native.VK_LWIN) || IsKeyDown(Native.VK_RWIN);

            if (ctrl != ctrlDown) return false;
            if (alt != altDown) return false;
            if (shift != shiftDown) return false;
            if (win != winDown) return false;
            return true;
        }

        static bool IsKeyDown(int vk)
        {
            return (GetKeyState(vk) & 0x8000) != 0;
        }

        [DllImport("user32.dll")]
        static extern short GetKeyState(int nVirtKey);

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
        // 动作
        // -----------------------------------------------------------------
        void OnTalkDown()
        {
            Log("开始说话（按下 " + Config.TalkHotkey + "）");
            targetWindow = Native.GetForegroundWindow();   // 记住“要粘贴进”的窗口
            savedClipboard = GetClipboardText();           // 备份原剪贴板（可选恢复）

            box.Clear();
            Show();
            Activate();
            box.Focus();

            if (Config.AutoTriggerVoice)
            {
                if (hotkeyHasWin)
                {
                    // 物理 Win 已按住，只补按 Ctrl 即可凑成 Ctrl+Win 调起微信语音
                    Native.VoiceDown();
                }
                else
                {
                    // 热键不含 Win，需同时模拟 Ctrl+Win
                    Native.SendKey(Native.VK_CONTROL, false);
                    Native.SendKey(Native.VK_LWIN, false);
                }
                Log("已调起微信语音");
            }
            else
            {
                Console.WriteLine("请在目标窗口手动点开微信语音，说完后松开 " + Config.TalkHotkey);
            }
        }

        void OnTalkUp()
        {
            if (!IsHandleCreated) return;
            Log("松开 " + Config.TalkHotkey + "，结束并粘贴");

            if (Config.AutoTriggerVoice)
            {
                if (hotkeyHasWin)
                {
                    Native.VoiceUp();  // 松开 Ctrl
                }
                else
                {
                    Native.SendKey(Native.VK_LWIN, true);
                    Native.SendKey(Native.VK_CONTROL, true);
                }
                System.Threading.Thread.Sleep(Config.CommitDelayMs); // 等文字落框
            }

            string text = box.Text.Trim();
            Log("捕获到文字长度: " + text.Length);

            if (text.Length > 0)
            {
                SetClipboardText(text);
                if (Config.PasteOnFinish)
                {
                    Native.SetForegroundWindow(targetWindow);
                    System.Threading.Thread.Sleep(60);
                    Native.PasteKey();
                    Console.WriteLine("[粘贴] " + text);
                }
                else
                {
                    Console.WriteLine("[剪贴板] " + text);
                }
                if (Config.RestoreClipboard)
                {
                    RestoreClipboardLater(savedClipboard);
                }
            }
            else
            {
                Console.WriteLine("[警告] 未捕获到文字。请确认微信语音已开启、麦克风正常，可调整 voicepaste.conf 的 commit_delay_ms。");
            }

            box.Clear();
            Hide();
            if (!Config.PasteOnFinish)
            {
                Native.SetForegroundWindow(targetWindow);
            }
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
        // 供 Main 调用的退出入口（在 UI 线程上安全执行）
        // -----------------------------------------------------------------
        public void RequestExit()
        {
            try
            {
                if (IsHandleCreated)
                {
                    BeginInvoke((Action)(() => DoExit()));
                }
                else
                {
                    DoExit();
                }
            }
            catch
            {
                DoExit();
            }
        }

        void DoExit()
        {
            Log("收到 Ctrl+C，退出");
            if (talking)
            {
                // 清理：松开可能还按着的模拟键
                if (Config.AutoTriggerVoice)
                {
                    Native.VoiceUp();                                 // 松 Ctrl
                    Native.SendKey(Native.VK_LWIN, true);             // 松 Win（尽力清理）
                }
                talking = false;
            }
            RemoveKeyboardHook();
            Application.Exit();
        }

        // -----------------------------------------------------------------
        // 日志：同时写到控制台 + voicepaste.log
        // -----------------------------------------------------------------
        static void Log(string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + msg;
            try { Console.WriteLine(line); }
            catch { }
            try
            {
                File.AppendAllText(logPath, line + Environment.NewLine, Encoding.UTF8);
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

            Console.OutputEncoding = Encoding.UTF8;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 启动前清理残留的同名进程（避免重复占用全局键盘钩子/热键）
            KillExistingProcesses();

            using (var form = new CaptureForm(logPath))
            {
                // 强制创建窗口句柄，并显式安装键盘钩子
                form.CreateControl();
                form.InstallKeyboardHook();

                Console.WriteLine();
                Console.WriteLine("==============================================");
                Console.WriteLine("  VoicePaste 已运行");
                Console.WriteLine("  按住 " + Config.TalkHotkey + " 说话，松开即粘贴");
                Console.WriteLine("  按 Ctrl+C 退出");
                Console.WriteLine("==============================================");
                Console.WriteLine();

                // Ctrl+C 处理：请求 UI 线程安全退出
                Console.CancelKeyPress += (s, e) =>
                {
                    e.Cancel = true;      // 阻止即时终止，交给 UI 线程清理
                    form.RequestExit();
                };

                Application.Run();
            }
        }

        // 终止本程序可能残留的旧进程（保留当前实例）
        static void KillExistingProcesses()
        {
            try
            {
                string myName = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
                int myId = System.Diagnostics.Process.GetCurrentProcess().Id;
                var procs = System.Diagnostics.Process.GetProcessesByName(myName);
                foreach (var p in procs)
                {
                    if (p.Id == myId) continue; // 跳过自己
                    try
                    {
                        p.Kill();
                        p.WaitForExit(2000);
                        Console.WriteLine("[清理] 已终止残留进程 PID=" + p.Id);
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}
