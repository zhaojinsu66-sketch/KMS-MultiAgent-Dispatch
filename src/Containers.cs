using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace AC
{
    // 窗口配套：给每个多开出来的应用窗口配一个它自己的“真空浏览器”
    // （浏览器的访客模式 + 独立的数据文件夹：里面没有任何已登录的账号，关掉就不留痕迹）。
    //  去：这个窗口要打开网页（比如点“用 Google 登录”）时，网址不进你平时的浏览器，而是进它自己的真空浏览器；
    //  回：登录完，浏览器要把结果交还给 Claude 时，KMS 接住这条回传链接，交给这个窗口本身，而不是原窗口。
    // 两头都只是“看到新进程 → 换个地方打开”，不改注册表、不改应用、不碰登录凭证。
    public static class Containers
    {
        class Pair
        {
            public ProcWatch App;       // 盯应用窗口：它要开浏览器时接手
            public ProcWatch Browser;   // 盯它的专用浏览器：浏览器要把结果交回应用时接手
            public DateTime LastTry = DateTime.MinValue;
        }

        static readonly Dictionary<string, Pair> _pairs = new Dictionary<string, Pair>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, DateTime> _delivered = new Dictionary<string, DateTime>();
        static readonly object _lock = new object();

        // “正在等登录结果”的窗口：这段时间里出现的 Claude 登录回传，都交给它
        static AppWindow _pendingWin;
        static DateTime _pendingUntil;
        static bool _pollerRunning;

        // 有事发生时告诉界面（在后台线程上调用）：窗口、说明
        public static Action<AppWindow, string> Notice;
        // 一条登录结果已经交给某个窗口（在后台线程上调用）：界面可以把登录浏览器收起来了
        public static Action<AppWindow> Delivered;

        static readonly string[] BrowserNames = { "msedge.exe", "chrome.exe", "firefox.exe", "brave.exe", "opera.exe", "vivaldi.exe", "360se.exe", "360chrome.exe", "qqbrowser.exe", "sogouexplorer.exe", "iexplore.exe" };
        static readonly Regex UrlRx = new Regex("https?://[^\\s\"]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex LinkRx = new Regex("claude(?:-nest)?://[^\\s\"]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        static void Log(string text)
        {
            try { File.AppendAllText(Path.Combine(Store.DataDir, "containers.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + text + "\r\n", new UTF8Encoding(false)); } catch { }
        }

        static void Tell(AppWindow w, string text)
        {
            var n = Notice;
            if (n != null) { try { n(w, text); } catch { } }
        }

        // 日志里不留登录用的一次性参数，只记到问号（或井号）为止
        public static string Short(string url)
        {
            int q = url.IndexOf('?'); int h = url.IndexOf('#');
            int cut = q < 0 ? h : (h < 0 ? q : Math.Min(q, h));
            return cut < 0 ? url : url.Substring(0, cut) + "…";
        }

        public static string BrowserDir(AppWindow w) { return Path.Combine(Store.DataDir, "real-apps", w.Id + "-browser"); }

        // 用哪个浏览器当“真空浏览器”：优先 Edge（Windows 自带），没有就用 Chrome
        public static string BrowserExe()
        {
            foreach (var name in new[] { "msedge.exe", "chrome.exe" })
                foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
                {
                    try
                    {
                        using (var k = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + name))
                        {
                            var p = k == null ? null : k.GetValue(null) as string;
                            if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
                        }
                    }
                    catch { }
                }
            foreach (var p in new[] { @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
                                      @"C:\Program Files\Google\Chrome\Application\chrome.exe" })
                if (File.Exists(p)) return p;
            return null;
        }

        public static bool IsBrowser(string image)
        {
            var name = Path.GetFileName(image ?? "");
            foreach (var b in BrowserNames) if (string.Equals(b, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // 这个启动参数是不是“请浏览器打开一个网址”——只认最普通的那种；
        // 带调试、无界面、指定数据目录的都不碰（那是程序自己在用浏览器，不是给人看的）
        public static string UrlToOpen(string image, string cmd)
        {
            if (!IsBrowser(image) || string.IsNullOrEmpty(cmd)) return null;
            if (cmd.IndexOf("--type=", StringComparison.OrdinalIgnoreCase) >= 0) return null;
            foreach (var bad in new[] { "--headless", "--remote-debugging", "--user-data-dir", "--app=", "--kiosk", "--load-extension", "--enable-automation" })
                if (cmd.IndexOf(bad, StringComparison.OrdinalIgnoreCase) >= 0) return null;
            var m = UrlRx.Match(cmd);
            return m.Success ? m.Value : null;
        }

        // 这个启动参数是不是“把一条 claude:// 链接交给 Claude”
        public static string LinkToDeliver(string cmd)
        {
            if (string.IsNullOrEmpty(cmd) || cmd.IndexOf("--type=", StringComparison.OrdinalIgnoreCase) >= 0) return null;
            if (cmd.IndexOf("--user-data-dir", StringComparison.OrdinalIgnoreCase) >= 0) return null;   // 已经指明了去哪个窗口的，不用接手
            var m = LinkRx.Match(cmd);
            return m.Success ? m.Value : null;
        }

        // 是不是登录用的回传（Google 登录的结果，或者邮箱登录的链接）
        public static bool IsLoginLink(string link)
        {
            if (string.IsNullOrEmpty(link)) return false;
            return link.IndexOf("://login/", StringComparison.OrdinalIgnoreCase) >= 0 || link.IndexOf("/magic-link", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // 把用户复制来的内容整理成应用认的链接；不是登录链接就返回 null
        public static string NormalizeLink(string text)
        {
            var link = (text ?? "").Trim().Trim('"', '<', '>');
            // 邮箱登录邮件里的链接是 https://claude.ai/magic-link#…，应用认的是 claude://claude.ai/magic-link#…
            var mm = Regex.Match(link, "^https://claude\\.(?:ai|com)/(?:cai/)?magic-link(#\\S+)$", RegexOptions.IgnoreCase);
            if (mm.Success) link = "claude://claude.ai/magic-link" + mm.Groups[1].Value;
            var m = LinkRx.Match(link);
            if (!m.Success || m.Index != 0 || m.Length != link.Length) return null;
            return link;
        }

        static int FindMain(string processName, string dataDir)
        {
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='" + processName + "'"))
                    foreach (ManagementObject o in s.Get())
                    {
                        string cmd = Convert.ToString(o["CommandLine"] ?? "");
                        if (cmd.IndexOf("--type=", StringComparison.Ordinal) >= 0) continue;
                        if (cmd.IndexOf(dataDir, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        return Convert.ToInt32(o["ProcessId"]);
                    }
            }
            catch (Exception ex) { Store.LogError("查找进程", ex); }
            return 0;
        }

        static Pair PairFor(AppWindow w)
        {
            Pair p;
            if (!_pairs.TryGetValue(w.Id, out p)) { p = new Pair(); _pairs[w.Id] = p; }
            return p;
        }

        // 保证这个窗口被盯着。只管 KMS 多开出来的窗口；你平时用的原窗口不动。
        // 要等应用启动了几秒再盯：太早会妨碍它启动时自己的安排。
        public static void Ensure(AppWindow w)
        {
            if (w == null || string.IsNullOrEmpty(w.DataDir)) return;
            lock (_lock)
            {
                var pair = PairFor(w);
                if (pair.App != null && pair.App.RootAlive) return;
                if (pair.App != null) { pair.App.Dispose(); pair.App = null; }
                if ((DateTime.Now - pair.LastTry).TotalSeconds < 2) return;
                pair.LastTry = DateTime.Now;
                var proc = RealApps.Find(w);
                if (proc == null) return;
                try { if ((DateTime.Now - proc.StartTime).TotalSeconds < 8) return; } catch { return; }
                string err;
                var win = w; int root = proc.Id;
                var home = Path.GetDirectoryName(RealApps.FindExe(w.Kind) ?? "") ?? "";
                pair.App = ProcWatch.Attach(root, (pid, parent, image, cmd) => OnAppChild(win, root, home, pid, parent, image, cmd), out err);
                Log(pair.App != null ? "盯上 " + w.Label + "（进程 " + root + "）" : "没盯上 " + w.Label + "：" + err);
            }
        }

        public static bool Watching(AppWindow w)
        {
            lock (_lock) { Pair p; return w != null && _pairs.TryGetValue(w.Id, out p) && p.App != null && p.App.RootAlive; }
        }

        // 应用窗口（或它自带的程序）启动了一个新进程
        static bool OnAppChild(AppWindow w, int root, string home, int pid, int parent, string image, string cmd)
        {
            var url = UrlToOpen(image, cmd);
            if (url == null) return false;
            if (parent != root)
            {
                // 只管应用自己发起的打开网页；里面的 Agent 会话通过命令行自己开浏览器，不归这里管
                var from = ProcWatch.ImagePath(parent) ?? "";
                if (home.Length == 0 || !from.StartsWith(home, StringComparison.OrdinalIgnoreCase)) return false;
            }
            Log(w.Label + " 要打开网页 " + Short(url) + " → 改到它自己的真空浏览器");
            Task.Run(() =>
            {
                if (w.Kind == "claude") Arm(w);
                var err = OpenBrowser(w, url);
                Tell(w, err ?? "已在“" + w.Label + "”专用的真空浏览器里打开网页，请在那个浏览器窗口里登录。");
            });
            return true;
        }

        // 在这个窗口的专用真空浏览器里打开一个网址（url 为空就只是打开浏览器）。返回 null 表示成功。
        public static string OpenBrowser(AppWindow w, string url) { return OpenBrowser(w, url, true, true); }

        // raise：把浏览器窗口提到最前面（要放进 KMS 那一格时不用）；newWindow：另开一个窗口（已经有窗口时传 false，网址就开在它里面）
        public static string OpenBrowser(AppWindow w, string url, bool raise, bool newWindow)
        {
            var exe = BrowserExe();
            if (exe == null) return "电脑上没有找到 Edge 或 Chrome，没法打开真空浏览器。";
            var dir = BrowserDir(w);
            try
            {
                Directory.CreateDirectory(dir);
                var psi = new ProcessStartInfo(exe);
                psi.UseShellExecute = false;
                // 访客模式：不带任何账号、收藏、插件，关掉后不留登录痕迹
                psi.Arguments = "--user-data-dir=\"" + dir + "\" --guest --no-first-run --no-default-browser-check --disable-sync" + (newWindow ? " --new-window" : "")
                              + (string.IsNullOrEmpty(url) ? "" : " \"" + url.Replace("\"", "%22") + "\"");
                RealApps.CleanEnv(psi);
                Process.Start(psi);
                // 这个浏览器如果本来就开着，新进程只是把网址递过去就退出；所以按数据文件夹去找真正的主进程来盯
                int root = 0;
                for (int i = 0; i < 24 && root == 0; i++) { Thread.Sleep(250); root = EnsureBrowserWatch(w); }
                if (root == 0) Log("没找到 " + w.Label + " 的专用浏览器进程");
                else if (raise)
                {
                    // 尽量让它显示在最前面
                    for (int i = 0; i < 12; i++)
                    {
                        Thread.Sleep(300);
                        try { var p = Process.GetProcessById(root); p.Refresh(); if (p.MainWindowHandle != IntPtr.Zero) { RealApps.Raise(p.MainWindowHandle); break; } } catch { break; }
                    }
                }
                return null;
            }
            catch (Exception ex) { Store.LogError("打开真空浏览器", ex); return "打开真空浏览器失败：" + ex.Message; }
        }

        // 这个窗口的专用浏览器在桌面上的独立窗口；没开、或者已经放进 KMS 里时返回 0
        public static IntPtr BrowserWindow(AppWindow w)
        {
            int root = EnsureBrowserWatch(w);
            if (root == 0) return IntPtr.Zero;
            try { var p = Process.GetProcessById(root); p.Refresh(); return p.MainWindowHandle; } catch { return IntPtr.Zero; }
        }

        // 返回专用浏览器主进程的进程号；没开着返回 0
        static int EnsureBrowserWatch(AppWindow w)
        {
            lock (_lock)
            {
                var pair = PairFor(w);
                if (pair.Browser != null && pair.Browser.RootAlive) return pair.Browser.RootPid;
                if (pair.Browser != null) { pair.Browser.Dispose(); pair.Browser = null; }
                int root = FindMain(Path.GetFileName(BrowserExe() ?? "msedge.exe"), BrowserDir(w));
                if (root == 0) return 0;
                string err; var win = w;
                pair.Browser = ProcWatch.Attach(root, (pid, parent, image, cmd) => OnBrowserChild(win, pid, parent, image, cmd), out err);
                Log(pair.Browser != null ? "盯上 " + w.Label + " 的专用浏览器（进程 " + root + "）" : "没盯上 " + w.Label + " 的专用浏览器：" + err);
                return root;
            }
        }

        // 已经开着的专用浏览器（比如 KMS 重开之后）重新盯上
        public static void EnsureBrowser(AppWindow w)
        {
            if (w == null || string.IsNullOrEmpty(w.DataDir) || !Directory.Exists(BrowserDir(w))) return;
            EnsureBrowserWatch(w);
        }

        // 专用浏览器启动了一个新进程：如果是要把 claude:// 链接交给 Claude，就改为交给这个窗口
        static bool OnBrowserChild(AppWindow w, int pid, int parent, string image, string cmd)
        {
            if (IsBrowser(image)) return false;          // 浏览器自己的子进程
            if (w.Kind != "claude") return false;        // Codex 的登录结果走本机端口，自己就能回到对的窗口
            var link = LinkToDeliver(cmd);
            if (link == null) return false;
            Log(w.Label + " 的浏览器交回一条链接 " + Short(link) + "（直接接住）");
            HandleLink(w, link);
            return true;
        }

        // 开始等这个窗口的登录结果（15 分钟内有效）
        public static void Arm(AppWindow w)
        {
            if (w == null || w.Kind != "claude" || string.IsNullOrEmpty(w.DataDir)) return;
            lock (_lock)
            {
                _pendingWin = w; _pendingUntil = DateTime.Now.AddMinutes(15);
                if (_pollerRunning) return;
                _pollerRunning = true;
            }
            var t = new Thread(Poll); t.IsBackground = true; t.Name = "登录回传"; t.Start();
        }

        public static AppWindow Pending
        {
            get { lock (_lock) { return _pendingWin != null && DateTime.Now <= _pendingUntil ? _pendingWin : null; } }
        }

        // 第二道保险：等登录结果期间，留意新冒出来的“把登录回传交给 Claude”的进程。
        // 不管它是哪个浏览器、怎么启动的，都让它原地停住，把链接改交给正在等的那个窗口。
        static void Poll()
        {
            var seen = new HashSet<int>();
            var tries = new Dictionary<int, int>();
            try { foreach (var p in Process.GetProcessesByName("claude")) { seen.Add(p.Id); p.Dispose(); } } catch { }
            while (true)
            {
                AppWindow w;
                lock (_lock)
                {
                    if (_pendingWin == null || DateTime.Now > _pendingUntil) { _pendingWin = null; _pollerRunning = false; return; }
                    w = _pendingWin;
                }
                try
                {
                    foreach (var p in Process.GetProcessesByName("claude"))
                    {
                        int id = p.Id; p.Dispose();
                        if (seen.Contains(id)) continue;
                        var cmd = ProcWatch.CommandLine(id);
                        if (string.IsNullOrEmpty(cmd))
                        {
                            int n; tries.TryGetValue(id, out n); tries[id] = n + 1;
                            if (n >= 20) seen.Add(id);
                            continue;
                        }
                        seen.Add(id);
                        var link = LinkToDeliver(cmd);
                        if (link == null || !IsLoginLink(link)) continue;
                        var image = ProcWatch.ImagePath(id) ?? "";
                        if (image.IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) < 0) continue;   // 只认 Claude 桌面版本身
                        ProcWatch.Hold(id);
                        Log("发现一条登录回传 " + Short(link) + "（进程 " + id + "）→ 交给 " + w.Label);
                        HandleLink(w, link);
                    }
                }
                catch (Exception ex) { Store.LogError("等登录回传", ex); }
                Thread.Sleep(120);
            }
        }

        static void HandleLink(AppWindow w, string link)
        {
            lock (_lock)
            {
                DateTime at;
                if (_delivered.TryGetValue(link, out at) && (DateTime.Now - at).TotalSeconds < 120) return;   // 两道保险同时看到同一条，只交一次
                _delivered[link] = DateTime.Now;
                if (IsLoginLink(link) && _pendingWin == w) _pendingWin = null;
            }
            Task.Run(() =>
            {
                var err = Deliver(w, link);
                Log(err == null ? "已交给 " + w.Label : "没交成：" + err);
                if (err == null && IsLoginLink(link)) { var d = Delivered; if (d != null) { try { d(w); } catch { } } }
                Tell(w, err ?? (IsLoginLink(link) ? "登录结果已交给“" + w.Label + "”。回到这个窗口，稍等几秒看它是否进入主界面。" : "链接已交给“" + w.Label + "”。"));
            });
        }

        // 把一条 claude:// 链接交给指定的窗口（靠它自己的数据文件夹，定位到正在运行的那一份应用）
        public static string Deliver(AppWindow w, string link)
        {
            if (w == null || w.Kind != "claude") return "只有 Claude 窗口需要这样交链接。";
            if (string.IsNullOrEmpty(w.DataDir)) return "这是原窗口，链接本来就会交给它，不需要 KMS 转交。";
            link = NormalizeLink(link);
            if (link == null) return "这不是 Claude 的登录链接。";
            if (RealApps.Find(w) == null) return "“" + w.Label + "”没有开着，请先打开它。";
            var exe = RealApps.FindExe("claude");
            if (exe == null) return "电脑上没有找到 Claude 桌面版。";
            try
            {
                var psi = new ProcessStartInfo(exe);
                psi.UseShellExecute = false;
                psi.Arguments = "--user-data-dir=" + Cli.Quote(w.DataDir) + " -- \"" + link.Replace("\"", "%22") + "\"";
                RealApps.CleanEnv(psi);
                Process.Start(psi);
                return null;
            }
            catch (Exception ex) { Store.LogError("转交登录链接", ex); return "转交登录链接失败：" + ex.Message; }
        }

        public static void StopAll()
        {
            lock (_lock)
            {
                foreach (var p in _pairs.Values) { if (p.App != null) p.App.Dispose(); if (p.Browser != null) p.Browser.Dispose(); }
                _pairs.Clear();
                _pendingWin = null;
            }
        }

        // 验证用：--watchtest <记录文件> <秒数> <接手 0|1> <程序> [参数…]
        // 启动一个程序并盯住它，把它之后启动的每个进程记下来；“接手=1”时按正式规则暂停并结束该接手的进程。
        public static int WatchTest(string[] args)
        {
            var sb = new StringBuilder(); var gate = new object();
            int seconds = int.Parse(args[2]); bool take = args[3] == "1";
            var psi = new ProcessStartInfo(args[4]); psi.UseShellExecute = false;
            var rest = new List<string>();
            for (int i = 5; i < args.Length; i++) rest.Add(args[i].IndexOf(' ') >= 0 ? "\"" + args[i] + "\"" : args[i]);
            psi.Arguments = string.Join(" ", rest.ToArray());
            RealApps.CleanEnv(psi);
            var sw = Stopwatch.StartNew();
            var p = Process.Start(psi);
            string err; int root = p.Id;
            var watch = ProcWatch.Attach(root, (pid, parent, image, cmd) =>
            {
                var url = UrlToOpen(image, cmd); var link = IsBrowser(image) ? null : LinkToDeliver(cmd);
                bool mine = take && (url != null || link != null);
                lock (gate) sb.AppendLine(sw.ElapsedMilliseconds + "ms pid=" + pid + " parent=" + parent + (parent == root ? "(root)" : "") + " image=" + image
                    + "\r\n    cmd=" + cmd + "\r\n    url=" + url + " link=" + link + " taken=" + mine);
                return mine;
            }, out err);
            lock (gate) sb.AppendLine(sw.ElapsedMilliseconds + "ms root=" + root + " attach=" + (watch != null ? "ok" : err));
            Thread.Sleep(seconds * 1000);
            if (watch != null) watch.Dispose();
            bool exited = false; try { exited = p.HasExited; } catch { }
            lock (gate) sb.AppendLine("root exited=" + exited);
            lock (gate) File.WriteAllText(args[1], sb.ToString(), new UTF8Encoding(true));
            return watch != null ? 0 : 1;
        }

        // 验证用：--armtest <窗口编号> <秒数> <记录文件>
        // 让指定窗口进入“等登录结果”状态并保持这么多秒，期间把发生的事记下来（用来验证回传会不会被交给它）。
        public static int ArmTest(AppConfig cfg, string[] args)
        {
            AppWindow w = null;
            foreach (var a in cfg.Apps) if (a.Id == args[1]) w = a;
            var sb = new StringBuilder(); var gate = new object();
            if (w == null) { File.WriteAllText(args[3], "没有这个窗口：" + args[1], new UTF8Encoding(true)); return 1; }
            Notice = (win, text) => { lock (gate) sb.AppendLine(DateTime.Now.ToString("HH:mm:ss.fff") + " [" + win.Label + "] " + text); };
            Ensure(w);
            Arm(w);
            lock (gate) sb.AppendLine(DateTime.Now.ToString("HH:mm:ss.fff") + " 等待中：" + w.Label + " 盯上=" + Watching(w));
            Thread.Sleep(int.Parse(args[2]) * 1000);
            lock (gate) sb.AppendLine(DateTime.Now.ToString("HH:mm:ss.fff") + " 结束 盯上=" + Watching(w) + " 还在等=" + (Pending != null));
            StopAll();
            lock (gate) File.WriteAllText(args[3], sb.ToString(), new UTF8Encoding(true));
            return 0;
        }
    }
}
