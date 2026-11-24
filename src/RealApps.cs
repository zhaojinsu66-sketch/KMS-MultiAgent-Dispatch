using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace AC
{
    // KMS 里放真实窗口的那一格。
    // 别的程序的窗口放进来以后，Windows 不会自动把键盘交给它，点了也打不了字。
    // 而且跨进程直接 SetFocus 是不生效的：必须先把两个线程的输入队列临时接到一起，
    // 再把焦点交给里面的窗口（Electron 的窗口还要收到一次“我被激活了”才开始收键盘）。
    public class HostPanel : System.Windows.Forms.Panel
    {
        public IntPtr Child = IntPtr.Zero;
        [DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

        // 把键盘交给里面那个窗口
        public void FocusChild()
        {
            var h = Child;
            if (h == IntPtr.Zero || !IsWindow(h)) return;
            uint target = 0, self = 0;
            bool attached = false;
            try
            {
                target = GetWindowThreadProcessId(h, IntPtr.Zero);
                self = GetCurrentThreadId();
                if (target != 0 && target != self) attached = AttachThreadInput(self, target, true);
                SetFocus(h);
                SendMessage(h, 0x0006, new IntPtr(1), IntPtr.Zero);   // WM_ACTIVATE / WA_ACTIVE
            }
            catch { }
            finally
            {
                if (attached) { try { AttachThreadInput(self, target, false); } catch { } }
            }
        }

        protected override void WndProc(ref System.Windows.Forms.Message m)
        {
            base.WndProc(ref m);
            if (Child == IntPtr.Zero || !IsWindow(Child)) return;
            if (m.Msg == 0x0210)   // WM_PARENTNOTIFY：里面的窗口上有鼠标键按下
            {
                int what = (int)(m.WParam.ToInt64() & 0xFFFF);
                if (what == 0x0201 || what == 0x0204 || what == 0x0207) FocusChild();   // 左键、右键、中键
            }
            else if (m.Msg == 0x0007) FocusChild();   // WM_SETFOCUS：这一格自己拿到了键盘
        }
    }

    // 真实应用多开：把官方的桌面应用（Claude、Codex）同时启动多份。
    // 每一份用自己的数据文件夹，所以各自登录各自的账号，互不影响，窗口都在同一个屏幕上。
    // 这里启动的就是官方应用本身，没有修改它，也不接触里面的登录信息。
    public static class RealApps
    {
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")] static extern IntPtr SetParent(IntPtr child, IntPtr parent);
        [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);
        [DllImport("user32.dll")] static extern bool MoveWindow(IntPtr h, int x, int y, int w, int height, bool repaint);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);

        const int GWL_STYLE = -16;
        const long WS_POPUP = 0x80000000L, WS_CHILD = 0x40000000L, WS_CAPTION = 0x00C00000L, WS_THICKFRAME = 0x00040000L,
                   WS_SYSMENU = 0x00080000L, WS_MINIMIZEBOX = 0x00020000L, WS_MAXIMIZEBOX = 0x00010000L;

        // 贴靠：窗口保持独立（样式完全不动，所以打字、对话框、标题栏都跟平时一样），
        // 只把它摆到 KMS 那一格的屏幕位置上，并认 KMS 做“主人”（总在 KMS 上面、跟着最小化）。
        public static void Float(IntPtr hwnd, IntPtr owner, int x, int y, int width, int height)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;
            try
            {
                if (IsIconic(hwnd)) ShowWindow(hwnd, 9);   // 先还原，否则摆好了也看不见
                SetWindowLongPtr(hwnd, -8, owner);         // GWL_HWNDPARENT = 主人窗口
                SetWindowPos(hwnd, IntPtr.Zero, x, y, Math.Max(width, 320), Math.Max(height, 240), 0x0004 | 0x0010);   // 不改层级 | 不激活
                if (!IsWindowVisible(hwnd)) ShowWindow(hwnd, 4);   // 显示但不抢焦点
            }
            catch { }
        }

        public static void Unfloat(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;
            try
            {
                SetWindowLongPtr(hwnd, -8, IntPtr.Zero);
                ShowWindow(hwnd, 5);
                SetWindowPos(hwnd, IntPtr.Zero, 90, 70, 1400, 950, 0x0004 | 0x0020);
            }
            catch { }
        }

        public static void HideWindow(IntPtr hwnd)
        {
            if (hwnd != IntPtr.Zero && IsWindow(hwnd)) ShowWindow(hwnd, 0);
        }

        // 把一个真实应用的窗口放进 KMS 自己的窗口里：去掉它的标题栏和边框，让它成为 KMS 里那块区域的子窗口。
        public static IntPtr Embed(IntPtr hwnd, IntPtr host, int width, int height)
        {
            var old = GetWindowLongPtr(hwnd, GWL_STYLE);
            // 最大化或最小化的窗口要先还原，否则放进来以后大小和位置都会是“还原前”那一套，错位还多出一块
            if (IsIconic(hwnd) || IsZoomed(hwnd)) ShowWindow(hwnd, 9);
            long style = old.ToInt64();
            style &= ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
            style |= WS_CHILD;
            SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style));
            SetParent(hwnd, host);
            MoveWindow(hwnd, 0, 0, Math.Max(width, 200), Math.Max(height, 150), true);
            ShowWindow(hwnd, 5);
            return old;
        }

        // 把窗口从“子窗口”状态放出来，恢复成一个正常的独立窗口
        // （用在：应用自己把父窗口换掉了、KMS 决定不再托管它的时候，别把它留在看不见的地方）
        public static void Detach(IntPtr hwnd, IntPtr oldStyle)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;
            try
            {
                SetParent(hwnd, IntPtr.Zero);
                long style = oldStyle.ToInt64();
                if (style == 0)
                    style = WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX;
                SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style));
                SetWindowPos(hwnd, IntPtr.Zero, 80, 60, 1200, 800, 0x0004 | 0x0020);
                ShowWindow(hwnd, 5);
            }
            catch { }
        }

        public static void Resize(IntPtr hwnd, int width, int height)
        {
            if (hwnd != IntPtr.Zero && IsWindow(hwnd)) MoveWindow(hwnd, 0, 0, Math.Max(width, 200), Math.Max(height, 150), true);
        }

        // 一个独立窗口的标题栏和边框一共占多少宽、多少高（像素）。
        // VirtualBox 的窗口被去掉标题栏和边框以后，里面的内容仍按“边框还在”来排，会比窗口小这么一圈；
        // 所以放进 KMS 时把窗口多放大这么多，内容就正好铺满那一格，多出来的边被那一格裁掉。
        public static void FrameSize(IntPtr hwnd, out int width, out int height)
        {
            width = 0; height = 0;
            Native.RECT win, client;
            if (hwnd == IntPtr.Zero || !Native.GetWindowRect(hwnd, out win) || !Native.GetClientRect(hwnd, out client)) return;
            width = Math.Max(0, (win.R - win.L) - (client.R - client.L));
            height = Math.Max(0, (win.B - win.T) - (client.B - client.T));
        }

        // 窗口自己改了大小时，把它拉回所在那一格的大小（大小没变就不动它）
        public static void Fit(IntPtr hwnd, int width, int height)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;
            Native.RECT r;
            if (!Native.GetWindowRect(hwnd, out r)) return;
            int w = Math.Max(width, 200), h = Math.Max(height, 150);
            if (r.R - r.L != w || r.B - r.T != h) MoveWindow(hwnd, 0, 0, w, h, true);
        }

        // 记下“哪些窗口正被嵌在 KMS 里”。万一 KMS 被强行结束，下次启动就能按这份清单把它们恢复正常，
        // 不用去猜哪个窗口是孤儿。
        static string RememberFile { get { return System.IO.Path.Combine(Store.DataDir, "embedded.txt"); } }
        static readonly object _rememberLock = new object();

        public static void Remember(IntPtr hwnd)
        {
            try
            {
                lock (_rememberLock)
                {
                    var set = LoadRemembered();
                    if (!set.Contains(hwnd)) set.Add(hwnd);
                    SaveRemembered(set);
                }
            }
            catch { }
        }

        public static void Forget(IntPtr hwnd)
        {
            try
            {
                lock (_rememberLock)
                {
                    var set = LoadRemembered();
                    if (set.Remove(hwnd)) SaveRemembered(set);
                }
            }
            catch { }
        }

        static List<IntPtr> LoadRemembered()
        {
            var list = new List<IntPtr>();
            try
            {
                if (!File.Exists(RememberFile)) return list;
                foreach (var line in File.ReadAllLines(RememberFile))
                {
                    long v;
                    if (long.TryParse(line.Trim(), out v) && v != 0) list.Add(new IntPtr(v));
                }
            }
            catch { }
            return list;
        }

        static void SaveRemembered(List<IntPtr> list)
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var h in list) sb.AppendLine(h.ToInt64().ToString());
                File.WriteAllText(RememberFile, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        // 启动时：把清单里“还活着、但已经没有父窗口”的那些窗口恢复成正常窗口，然后清空清单
        public static int RepairRemembered()
        {
            int n = 0;
            List<IntPtr> list;
            lock (_rememberLock) { list = LoadRemembered(); }
            foreach (var h in list)
            {
                try
                {
                    if (!IsWindow(h)) continue;
                    if (GetParent(h) != IntPtr.Zero) continue;
                    long style = GetWindowLongPtr(h, GWL_STYLE).ToInt64();
                    if ((style & WS_CHILD) == 0) continue;
                    style &= ~WS_CHILD;
                    style |= WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX;
                    SetWindowLongPtr(h, GWL_STYLE, new IntPtr(style));
                    SetWindowPos(h, IntPtr.Zero, 60, 40, 1400, 950, 0x0004 | 0x0020);
                    ShowWindow(h, 5);
                    n++;
                }
                catch (Exception ex) { Store.LogError("恢复被留下的窗口", ex); }
            }
            lock (_rememberLock) { SaveRemembered(new List<IntPtr>()); }
            return n;
        }

        // 上一次 KMS 被强行结束时，嵌进去的窗口会留在“子窗口”状态、但没有父窗口：
        // 于是它既不在 KMS 里，也不像个正常窗口，点也点不出来。启动时把它们恢复原样。
        public static int RepairOrphans()
        {
            int fixedCount = 0;
            foreach (var kind in new[] { "claude", "codex" })
            {
                List<KeyValuePair<int, string>> procs;
                try { procs = MainProcesses(kind); } catch { continue; }
                foreach (var p in procs)
                {
                    foreach (var h in WindowsOf(p.Key))
                    {
                        try
                        {
                            if (GetParent(h) != IntPtr.Zero) continue;
                            long style = GetWindowLongPtr(h, GWL_STYLE).ToInt64();
                            if ((style & WS_CHILD) == 0) continue;                    // 正常的独立窗口，不动它
                            style &= ~WS_CHILD;
                            style |= WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX;
                            SetWindowLongPtr(h, GWL_STYLE, new IntPtr(style));
                            SetWindowPos(h, IntPtr.Zero, 60, 40, 1400, 950, 0x0004 | 0x0020);   // 不改层级 | 显示
                            ShowWindow(h, 5);
                            fixedCount++;
                        }
                        catch (Exception ex) { Store.LogError("恢复被留下的窗口", ex); }
                    }
                }
            }
            return fixedCount;
        }

        static List<IntPtr> WindowsOf(int pid)
        {
            var list = new List<IntPtr>();
            try
            {
                EnumWindows((h, l) =>
                {
                    uint p; GetWindowThreadProcessId(h, out p);
                    if (p == (uint)pid) list.Add(h);
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return list;
        }

        // 这个窗口现在还是不是嵌在 host 里面（只看它还在不在是不够的：父窗口可能已经被换掉）
        public static bool IsChildOf(IntPtr hwnd, IntPtr host)
        {
            return hwnd != IntPtr.Zero && host != IntPtr.Zero && IsWindow(hwnd) && GetParent(hwnd) == host;
        }

        // 把嵌进来的窗口重新贴回它那一格：左上角归 0,0，大小正好等于这一格。
        // 有些应用（尤其 Electron 的）在被 SetParent 之后会按自己记住的旧位置/旧大小重新摆一次，
        // 所以要隔一会儿再对一次，不能只在放进来的时候摆一次。
        public static void Align(IntPtr hwnd, IntPtr host)
        {
            if (hwnd == IntPtr.Zero || host == IntPtr.Zero || !IsWindow(hwnd)) return;
            Native.RECT hr, wr;
            if (!Native.GetWindowRect(host, out hr) || !Native.GetWindowRect(hwnd, out wr)) return;
            int w = hr.R - hr.L, h = hr.B - hr.T;
            if (w <= 0 || h <= 0) return;
            if (wr.L != hr.L || wr.T != hr.T || (wr.R - wr.L) != w || (wr.B - wr.T) != h)
                MoveWindow(hwnd, 0, 0, w, h, true);
        }

        // 把嵌在 KMS 里的窗口放回桌面，恢复成普通的独立窗口
        public static void Release(IntPtr hwnd, IntPtr oldStyle, int x, int y, int width, int height)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;
            SetParent(hwnd, IntPtr.Zero);
            SetWindowLongPtr(hwnd, GWL_STYLE, oldStyle);
            SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, 0x0004 | 0x0020 | 0x0040);   // 不改层级 | 重画边框 | 显示
        }

        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam);

        // 关掉一个由 KMS 打开的窗口（比如登录用的真空浏览器）：发一条正常的“关闭”消息，让它自己收尾
        public static void Close(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;
            PostMessage(hwnd, 0x0010, IntPtr.Zero, IntPtr.Zero);   // WM_CLOSE
        }

        // 应用装在哪里：从 Windows 的应用登记里按包名找，应用升级换了版本号也能找到
        static string PackageRoot(string prefix)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages"))
                {
                    if (k == null) return null;
                    string best = null;
                    foreach (var name in k.GetSubKeyNames())
                    {
                        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                        using (var p = k.OpenSubKey(name))
                        {
                            var root = p == null ? null : p.GetValue("PackageRootFolder") as string;
                            if (!string.IsNullOrEmpty(root) && Directory.Exists(root) && (best == null || string.CompareOrdinal(root, best) > 0)) best = root;
                        }
                    }
                    return best;
                }
            }
            catch { return null; }
        }

        static string ProcName(string kind) { return kind == "codex" ? "ChatGPT.exe" : "claude.exe"; }

        public static string FindExe(string kind)
        {
            var root = kind == "codex" ? PackageRoot("OpenAI.Codex_") : PackageRoot("Claude_");
            if (root == null) return null;
            var exe = Path.Combine(root, "app", kind == "codex" ? "ChatGPT.exe" : "Claude.exe");
            return File.Exists(exe) ? exe : null;
        }

        // 正在运行的应用主进程，以及各自用的数据文件夹（从它的启动参数里看）
        static List<KeyValuePair<int, string>> MainProcesses(string kind)
        {
            var list = new List<KeyValuePair<int, string>>();
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT ProcessId, CommandLine, ExecutablePath FROM Win32_Process WHERE Name='" + ProcName(kind) + "'"))
                    foreach (ManagementObject o in s.Get())
                    {
                        string cmd = Convert.ToString(o["CommandLine"] ?? "");
                        string path = Convert.ToString(o["ExecutablePath"] ?? "");
                        if (path.IndexOf("WindowsApps", StringComparison.OrdinalIgnoreCase) < 0) continue;   // 排除同名的命令行程序
                        if (cmd.IndexOf("--type=", StringComparison.Ordinal) >= 0) continue;                 // 只要主进程
                        list.Add(new KeyValuePair<int, string>(Convert.ToInt32(o["ProcessId"]), cmd));
                    }
            }
            catch (Exception ex) { Store.LogError("查询应用进程", ex); }
            return list;
        }

        static bool Matches(AppWindow w, string cmd)
        {
            if (string.IsNullOrEmpty(w.DataDir))
            {
                // 原窗口：就是这台电脑上原本那一份，命令行里不带 KMS 的 real-apps 目录
                return cmd.IndexOf("real-apps", StringComparison.OrdinalIgnoreCase) < 0;
            }
            return cmd.IndexOf(w.DataDir, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static Process Find(AppWindow w)
        {
            if (w.IsVm) return null;   // 虚拟机不按进程找，见 Vms
            foreach (var p in MainProcesses(w.Kind))
                if (Matches(w, p.Value))
                {
                    try { return Process.GetProcessById(p.Key); } catch { }
                }
            return null;
        }

        // 刷新每个窗口“已打开 / 未打开”的状态
        public static void Refresh(IEnumerable<AppWindow> apps)
        {
            var cache = new Dictionary<string, List<KeyValuePair<int, string>>>();
            foreach (var w in apps)
            {
                if (w.IsVm) { w.Running = Vms.IsRunning(w.VmName); continue; }
                List<KeyValuePair<int, string>> procs;
                if (!cache.TryGetValue(w.Kind ?? "claude", out procs)) { procs = MainProcesses(w.Kind); cache[w.Kind ?? "claude"] = procs; }
                bool on = false;
                foreach (var p in procs) if (Matches(w, p.Value)) { on = true; break; }
                w.Running = on;
            }
        }

        static void Front(Process p)
        {
            try
            {
                p.Refresh();
                var h = p.MainWindowHandle;
                if (h == IntPtr.Zero) return;
                if (IsIconic(h)) ShowWindow(h, 9);   // 还原最小化的窗口
                SetForegroundWindow(h);
            }
            catch { }
        }

        // 把一个窗口提到最前面给用户看（不抢输入也行，至少要露出来）
        public static void Raise(IntPtr h)
        {
            if (h == IntPtr.Zero) return;
            if (IsIconic(h)) ShowWindow(h, 9);
            SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040);   // 先置顶
            SetWindowPos(h, new IntPtr(-2), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040);   // 再取消“总在最前”，只留在上面
            SetForegroundWindow(h);
        }

        // 这个应用实例当前在桌面上的独立窗口；没开、或者已经嵌进 KMS 里时返回 0
        public static IntPtr TopWindow(AppWindow w)
        {
            if (w.IsVm) return Vms.FindWindow(w.VmName);
            var p = Find(w);
            if (p == null) return IntPtr.Zero;
            try { p.Refresh(); return p.MainWindowHandle; } catch { return IntPtr.Zero; }
        }

        // 打开这个窗口；已经开着就切到它。返回 null 表示成功，否则是说明。
        public static string Open(AppWindow w) { return Open(w, true); }

        public static string Open(AppWindow w, bool bringToFront)
        {
            if (w.IsVm)
            {
                var vmErr = Vms.Open(w.VmName);
                if (vmErr == null && bringToFront) Raise(Vms.FindWindow(w.VmName));
                return vmErr;
            }
            var running = Find(w);
            if (running != null) { if (bringToFront) Front(running); return null; }
            var exe = FindExe(w.Kind);
            if (exe == null) return "电脑上没有找到 " + w.KindName + "，请先安装它。";
            try
            {
                var psi = new ProcessStartInfo(exe);
                psi.UseShellExecute = false;
                if (!string.IsNullOrEmpty(w.DataDir))
                {
                    Directory.CreateDirectory(w.DataDir);
                    psi.Arguments = "--user-data-dir=" + Cli.Quote(w.DataDir);
                    if (w.Kind == "codex")
                    {
                        // Codex 的登录和设置另外放在 CODEX_HOME 里，也给它一份独立的
                        var home = Path.Combine(w.DataDir, "codex-home");
                        Directory.CreateDirectory(home);
                        psi.Environment["CODEX_HOME"] = home;
                    }
                }
                CleanEnv(psi);
                Process.Start(psi);
                return null;
            }
            catch (Exception ex) { Store.LogError("打开应用 " + w.Kind, ex); return "打开 " + w.KindName + " 失败：" + ex.Message; }
        }

        // 如果 KMS 自己是从某个 Claude 会话里启动的，别把那个会话的环境变量带给新开的程序
        public static void CleanEnv(ProcessStartInfo psi)
        {
            var drop = new List<string>();
            foreach (var k in psi.Environment.Keys)
            {
                var u = k.ToUpperInvariant();
                if (u.StartsWith("CLAUDE") || u.StartsWith("ELECTRON") || u.StartsWith("ANTHROPIC") || u.StartsWith("CHROME_CRASHPAD") || u == "NODE_OPTIONS") drop.Add(k);
            }
            foreach (var k in drop) psi.Environment.Remove(k);
        }

        // 把所有已经打开的窗口在屏幕上并排排好。参数是屏幕可用区域（像素）。
        public static int Tile(IEnumerable<AppWindow> apps, int left, int top, int width, int height)
        {
            var handles = new List<IntPtr>();
            foreach (var w in apps)
            {
                if (w.IsVm) { var vh = Vms.FindWindow(w.VmName); if (vh != IntPtr.Zero) handles.Add(vh); continue; }
                var p = Find(w);
                if (p == null) continue;
                try { p.Refresh(); if (p.MainWindowHandle != IntPtr.Zero) handles.Add(p.MainWindowHandle); } catch { }
            }
            int n = handles.Count;
            if (n == 0) return 0;
            int cols = n <= 3 ? n : (int)Math.Ceiling(Math.Sqrt(n));
            int rows = (int)Math.Ceiling(n / (double)cols);
            int cw = width / cols, ch = height / rows;
            for (int i = 0; i < n; i++)
            {
                ShowWindow(handles[i], 9);   // 先还原，最大化或最小化的窗口才能改大小
                SetWindowPos(handles[i], IntPtr.Zero, left + (i % cols) * cw, top + (i / cols) * ch, cw, ch, 0x0004 | 0x0040);
            }
            return n;
        }
    }
}
