using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace AC
{
    // 链接转接：让多开窗口里点出来的网页（比如“用 Google 登录”）只在那个窗口自己的浏览器里打开。
    // 应用是让 Windows 去打开网页的，KMS 拦不住；所以反过来：把一个很小的转接程序 KmsLink.exe 登记成浏览器，
    // 由你在 Windows 设置里把它选成默认浏览器。之后每次有程序要开网页，Windows 先启动它：
    // 是 KMS 多开的窗口要开的，它通过一条本机管道告诉 KMS；其它的原样交给你原来的浏览器。
    public static class LinkRouter
    {
        public const string ProgId = "KmsLinkURL";
        const string ClientKey = @"Software\Clients\StartMenuInternet\KmsLink";
        const string DisplayName = "KMS多控链接转接器";

        // 有窗口要打开网页时调用（在后台线程上）：窗口编号、网址
        public static Action<string, string> Opened;
        static Thread _server;

        public static string Exe { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "KmsLink.exe"); } }
        public static bool Installed { get { return File.Exists(Exe); } }
        public static string PipeName { get { return "kms-link-" + Environment.UserName.ToLowerInvariant(); } }

        static string HttpsProgId()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice"))
                    return k == null ? "" : Convert.ToString(k.GetValue("ProgId") ?? "");
            }
            catch { return ""; }
        }

        // Windows 现在是不是把网页交给转接器
        public static bool IsDefault() { return string.Equals(HttpsProgId(), ProgId, StringComparison.OrdinalIgnoreCase); }

        // 你现在用的浏览器登记的启动命令，比如  "C:\...\msedge.exe" --single-argument %1
        static string CurrentBrowserCommand()
        {
            var id = HttpsProgId();
            if (id.Length == 0 || string.Equals(id, ProgId, StringComparison.OrdinalIgnoreCase)) return "";
            try
            {
                using (var k = Registry.ClassesRoot.OpenSubKey(id + @"\shell\open\command"))
                    return k == null ? "" : Convert.ToString(k.GetValue(null) ?? "");
            }
            catch { return ""; }
        }

        static string ConfigFile { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "KmsLink.cfg"); } }

        static string SavedBrowser()
        {
            try
            {
                foreach (var line in File.ReadAllLines(ConfigFile, Encoding.UTF8))
                    if (line.StartsWith("browser=", StringComparison.Ordinal)) return line.Substring(8).Trim();
            }
            catch { }
            return "";
        }

        // 把转接器登记成一个可以选的浏览器（只写当前用户自己的登记项），并记下“其它网页交给谁”。
        // 选不选它当默认浏览器由用户在 Windows 设置里决定。返回 null 表示成功，否则是说明。
        public static string Register()
        {
            if (!Installed) return "没有找到 KmsLink.exe（应该和 KMS多控在同一个文件夹里）。请重新编译一次。";
            try
            {
                var browser = CurrentBrowserCommand();
                if (browser.Length == 0) browser = SavedBrowser();   // 已经是默认了，就沿用上次记下的
                File.WriteAllLines(ConfigFile, new[]
                {
                    "apps=" + Path.Combine(Store.DataDir, "real-apps") + "\\",
                    "browser=" + browser
                }, new UTF8Encoding(false));

                var exe = Exe; var icon = "\"" + Process.GetCurrentProcess().MainModule.FileName + "\",0";
                var open = "\"" + exe + "\" \"%1\"";
                using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId))
                {
                    k.SetValue(null, DisplayName); k.SetValue("URL Protocol", "");
                    using (var c = k.CreateSubKey(@"shell\open\command")) c.SetValue(null, open);
                    using (var i = k.CreateSubKey("DefaultIcon")) i.SetValue(null, icon);
                    using (var a = k.CreateSubKey("Application")) { a.SetValue("ApplicationName", DisplayName); a.SetValue("ApplicationDescription", "把 KMS多控多开窗口要打开的网页留在那个窗口里，其它网页照常交给你原来的浏览器。"); a.SetValue("ApplicationIcon", icon); }
                }
                using (var k = Registry.CurrentUser.CreateSubKey(ClientKey))
                {
                    k.SetValue(null, DisplayName);
                    using (var c = k.CreateSubKey(@"shell\open\command")) c.SetValue(null, "\"" + exe + "\"");
                    using (var i = k.CreateSubKey("DefaultIcon")) i.SetValue(null, icon);
                    using (var cap = k.CreateSubKey("Capabilities"))
                    {
                        cap.SetValue("ApplicationName", DisplayName);
                        cap.SetValue("ApplicationDescription", "把 KMS多控多开窗口要打开的网页留在那个窗口里，其它网页照常交给你原来的浏览器。");
                        cap.SetValue("ApplicationIcon", icon);
                        using (var u = cap.CreateSubKey("URLAssociations")) { u.SetValue("http", ProgId); u.SetValue("https", ProgId); }
                        using (var f = cap.CreateSubKey("FileAssociations")) { f.SetValue(".htm", ProgId); f.SetValue(".html", ProgId); }
                        using (var s = cap.CreateSubKey("StartMenu")) s.SetValue("StartMenuInternet", "KmsLink");
                    }
                }
                using (var k = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
                    k.SetValue("KmsLink", ClientKey + @"\Capabilities");
                return null;
            }
            catch (Exception ex) { Store.LogError("登记链接转接器", ex); return "登记链接转接器失败：" + ex.Message; }
        }

        // 开始等转接器传话（KMS 运行期间一直开着）
        public static void StartServer()
        {
            if (_server != null) return;
            _server = new Thread(Serve); _server.IsBackground = true; _server.Name = "链接转接"; _server.Start();
        }

        static void Serve()
        {
            while (true)
            {
                try
                {
                    using (var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None))
                    {
                        pipe.WaitForConnection();
                        var sb = new StringBuilder(); var one = new byte[1];
                        while (sb.Length < 16384 && pipe.Read(one, 0, 1) == 1 && one[0] != (byte)'\n') sb.Append((char)one[0]);
                        var line = Encoding.UTF8.GetString(Encoding.GetEncoding(28591).GetBytes(sb.ToString()));
                        var parts = line.Split(new[] { '\t' }, 3);
                        bool ok = parts.Length == 3 && parts[0] == "open" && parts[1].Length > 0 && parts[2].Length > 0;
                        var reply = Encoding.ASCII.GetBytes(ok ? "ok" : "no");
                        pipe.Write(reply, 0, reply.Length); pipe.Flush();
                        try { pipe.WaitForPipeDrain(); } catch { }
                        if (ok) { var h = Opened; if (h != null) { try { h(parts[1], parts[2]); } catch (Exception ex) { Store.LogError("处理转接来的网页", ex); } } }
                    }
                }
                catch (Exception ex) { Store.LogError("链接转接管道", ex); Thread.Sleep(1000); }
            }
        }

        // 这个网页不归 KMS 管：照常交给你原来的浏览器（经转接器走一遍，它认得出不是多开窗口发起的）
        public static void Forward(string url)
        {
            try
            {
                if (Installed) Process.Start(new ProcessStartInfo(Exe, "\"" + url.Replace("\"", "%22") + "\"") { UseShellExecute = false });
            }
            catch (Exception ex) { Store.LogError("交给原来的浏览器", ex); }
        }
    }
}
