using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AC
{
    // 虚拟机多开：每个账号一台 VirtualBox 虚拟机，里面是一整套独立的 Windows。
    // KMS 只通过 VirtualBox 自带的命令行（VBoxManage）查看和启动虚拟机，再找到它在桌面上的窗口放进来。
    // 虚拟机本身的安装、克隆见 docs\虚拟机多开-搭建步骤.md。
    public static class Vms
    {
        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

        static string _dir;
        static readonly object _lock = new object();
        static HashSet<string> _running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static DateTime _runningAt = DateTime.MinValue;

        // VirtualBox 装在哪里；没装返回 null
        public static string InstallDir
        {
            get
            {
                if (_dir != null) return _dir.Length == 0 ? null : _dir;
                string found = null;
                try
                {
                    using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Oracle\VirtualBox"))
                    {
                        var v = k == null ? null : k.GetValue("InstallDir") as string;
                        if (!string.IsNullOrEmpty(v) && File.Exists(Path.Combine(v, "VBoxManage.exe"))) found = v;
                    }
                }
                catch { }
                if (found == null)
                {
                    foreach (var v in new[] { Environment.GetEnvironmentVariable("VBOX_MSI_INSTALL_PATH"), @"C:\Program Files\Oracle\VirtualBox" })
                        if (!string.IsNullOrEmpty(v) && File.Exists(Path.Combine(v, "VBoxManage.exe"))) { found = v; break; }
                }
                _dir = found ?? "";
                return found;
            }
        }

        public static bool Installed { get { return InstallDir != null; } }

        // 运行一条 VBoxManage 命令，返回它的输出；失败时 code 不为 0
        static string Manage(string args, int timeoutMs, out int code)
        {
            code = -1;
            var dir = InstallDir;
            if (dir == null) return "";
            try
            {
                var psi = new ProcessStartInfo(Path.Combine(dir, "VBoxManage.exe"), args);
                psi.UseShellExecute = false; psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8; psi.StandardErrorEncoding = Encoding.UTF8;
                using (var p = Process.Start(psi))
                {
                    var err = new StringBuilder();
                    p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
                    p.BeginErrorReadLine();
                    var read = p.StandardOutput.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return "VBoxManage 没有在规定时间内返回"; }
                    p.WaitForExit();
                    code = p.ExitCode;
                    var text = read.Result;
                    lock (err) return code == 0 ? text : (text + err).Trim();
                }
            }
            catch (Exception ex) { Store.LogError("运行 VBoxManage " + args, ex); return ex.Message; }
        }

        static List<string> Names(string text)
        {
            var list = new List<string>();
            foreach (Match m in Regex.Matches(text ?? "", "^\"(.+)\" \\{[0-9a-fA-F-]+\\}\\s*$", RegexOptions.Multiline)) list.Add(m.Groups[1].Value);
            return list;
        }

        // 电脑上登记的所有虚拟机的名字
        public static List<string> List()
        {
            int code; var text = Manage("list vms", 15000, out code);
            return code == 0 ? Names(text) : new List<string>();
        }

        // 正在运行的虚拟机；结果留用几秒，免得界面每次刷新都去问一遍
        static HashSet<string> RunningNames(bool fresh)
        {
            lock (_lock)
            {
                if (!fresh && (DateTime.Now - _runningAt).TotalSeconds < 5) return _running;
                int code; var text = Manage("list runningvms", 15000, out code);
                if (code == 0) _running = new HashSet<string>(Names(text), StringComparer.OrdinalIgnoreCase);
                _runningAt = DateTime.Now;
                return _running;
            }
        }

        public static bool IsRunning(string name) { return !string.IsNullOrEmpty(name) && RunningNames(false).Contains(name); }
        public static bool IsRunningNow(string name) { return !string.IsNullOrEmpty(name) && RunningNames(true).Contains(name); }

        // 这台虚拟机在桌面上的独立窗口；没有窗口、或者已经放进 KMS 里时返回 0。
        // 窗口标题的样子是“名字 (快照名) [正在运行] - Oracle VirtualBox”。
        public static IntPtr FindWindow(string name)
        {
            if (string.IsNullOrEmpty(name)) return IntPtr.Zero;
            IntPtr found = IntPtr.Zero;
            var sb = new StringBuilder(512);
            EnumWindows((h, l) =>
            {
                if (!IsWindowVisible(h)) return true;
                sb.Length = 0;
                if (GetWindowTextW(h, sb, sb.Capacity) <= 0) return true;
                var title = sb.ToString();
                if (!title.EndsWith("VirtualBox", StringComparison.Ordinal)) return true;
                if (!title.StartsWith(name + " (", StringComparison.OrdinalIgnoreCase) && !title.StartsWith(name + " [", StringComparison.OrdinalIgnoreCase)) return true;
                uint pid; GetWindowThreadProcessId(h, out pid);
                try { if (!Process.GetProcessById((int)pid).ProcessName.Equals("VirtualBoxVM", StringComparison.OrdinalIgnoreCase)) return true; }
                catch { return true; }
                found = h;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        // 让这台虚拟机开着并且有窗口。没开机就开机；开着但没有窗口（窗口被关掉、后台运行）就把窗口接上。
        // 用“可分离”的方式启动：画面窗口和虚拟机本身是两个进程，窗口没了虚拟机照常运行。
        // 返回 null 表示成功，否则是说明。
        public static string Open(string name)
        {
            var dir = InstallDir;
            if (dir == null) return "电脑上没有找到 VirtualBox，请先安装它。";
            if (string.IsNullOrEmpty(name)) return "这个条目没有对应的虚拟机。";
            if (FindWindow(name) != IntPtr.Zero) return null;
            if (!IsRunningNow(name)) return Start(name);

            // 开着，但桌面上没有它的窗口
            if (Session(name) != "headless")
                return "虚拟机“" + name + "”正在运行，但找不到它的窗口。请在任务栏里找到它的 VirtualBox 窗口；如果没有，只能把这台虚拟机关机后再从这里打开。";
            try
            {
                var psi = new ProcessStartInfo(Path.Combine(dir, "VirtualBoxVM.exe"), "--startvm \"" + name + "\" --separate");
                psi.UseShellExecute = false;
                RealApps.CleanEnv(psi);
                Process.Start(psi);
            }
            catch (Exception ex) { return "打开虚拟机“" + name + "”的窗口失败：" + ex.Message; }
            for (int i = 0; i < 30; i++)
            {
                System.Threading.Thread.Sleep(500);
                if (FindWindow(name) != IntPtr.Zero) return null;
            }
            // 窗口接不上：上一次的画面窗口没有正常收尾（比如 KMS 被强行结束）时会这样。
            // 把虚拟机的状态存到磁盘再接着开，里面正在运行的程序都原样保留。
            int code; var text = Manage("controlvm \"" + name + "\" savestate", 600000, out code);
            if (code != 0) return "虚拟机“" + name + "”正在后台运行，但画面窗口接不上，保存状态也没有成功：" + text;
            System.Threading.Thread.Sleep(1500);
            return Start(name);
        }

        static string Start(string name)
        {
            int code; var text = Manage("startvm \"" + name + "\" --type separate", 180000, out code);
            lock (_lock) _runningAt = DateTime.MinValue;
            return code == 0 ? null : "启动虚拟机“" + name + "”失败：" + text;
        }

        static readonly HashSet<string> _kicking = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 让一台开着的虚拟机“继续走”。
        // 这台电脑开着内存完整性，VirtualBox 只能走兼容模式；实测这种模式下虚拟机内部的时钟会不定时停住
        // （画面不动、里面的程序不走，最长见过 46 分钟），而对它做一次调试查询就立刻恢复。
        // 所以虚拟机开着的时候，KMS 每隔几秒查一次。查询本身不改变虚拟机里的任何东西。
        public static void Kick(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            lock (_kicking) { if (!_kicking.Add(name)) return; }   // 上一次还没返回就不叠加
            try { int code; Manage("debugvm \"" + name + "\" getregisters --cpu=0 rip", 8000, out code); }
            finally { lock (_kicking) _kicking.Remove(name); }
        }

        // 这台虚拟机现在是由谁带着运行的：headless 表示在后台运行、画面窗口是另外接上去的；查不到返回空
        static string Session(string name)
        {
            int code; var text = Manage("showvminfo \"" + name + "\" --machinereadable", 20000, out code);
            if (code != 0) return "";
            var m = Regex.Match(text, "^SessionName=\"(.*)\"\\s*$", RegexOptions.Multiline);
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }
    }

    // 自检时模拟一次真实的鼠标点击和键盘输入，用来确认放进 KMS 的窗口确实收得到输入
    public static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
        [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Explicit)] struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
        [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public INPUTUNION u; }
        [StructLayout(LayoutKind.Sequential)]
        public struct GUITHREADINFO { public int cbSize; public uint flags; public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret; public RECT rcCaret; }

        [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);

        public static void Click(int x, int y)
        {
            SetCursorPos(x, y);
            var a = new INPUT[2];
            a[0].type = 0; a[0].u.mi.dwFlags = 0x0002;   // 左键按下
            a[1].type = 0; a[1].u.mi.dwFlags = 0x0004;   // 左键松开
            SendInput(2, a, Marshal.SizeOf(typeof(INPUT)));
        }

        // 按一下键：scan 是键盘扫描码
        public static void Key(ushort scan)
        {
            var a = new INPUT[2];
            a[0].type = 1; a[0].u.ki.wScan = scan; a[0].u.ki.dwFlags = 0x0008;            // 按扫描码按下
            a[1].type = 1; a[1].u.ki.wScan = scan; a[1].u.ki.dwFlags = 0x0008 | 0x0002;   // 松开
            SendInput(2, a, Marshal.SizeOf(typeof(INPUT)));
        }

        // 当前键盘输入落在哪个进程的窗口上（0 表示查不到）
        public static uint FocusPid(out IntPtr focus)
        {
            var info = new GUITHREADINFO(); info.cbSize = Marshal.SizeOf(typeof(GUITHREADINFO));
            focus = IntPtr.Zero;
            if (!GetGUIThreadInfo(0, ref info)) return 0;
            focus = info.hwndFocus;
            if (focus == IntPtr.Zero) return 0;
            uint pid; GetWindowThreadProcessId(focus, out pid);
            return pid;
        }
    }
}
