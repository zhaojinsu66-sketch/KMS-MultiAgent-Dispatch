using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace AC
{
    public static class Program
    {
        // 有些代理软件会同时设置 NO_PROXY 和 no_proxy（只差大小写）。
        // Windows 不介意，但 .NET 的 ProcessStartInfo 环境字典是大小写不敏感的，
        // 一碰 psi.Environment 就抛“已添加项”，于是开应用、跑命令行全部失败。
        // 启动时先把重复的那份删掉，只留全大写的那一个。
        static void SanitizeEnv()
        {
            try
            {
                var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
                {
                    var k = e.Key as string;
                    if (string.IsNullOrEmpty(k)) continue;
                    List<string> list;
                    if (!groups.TryGetValue(k, out list)) { list = new List<string>(); groups[k] = list; }
                    list.Add(k);
                }
                foreach (var g in groups.Values)
                {
                    if (g.Count < 2) continue;
                    string keep = null;
                    foreach (var k in g) if (k == k.ToUpperInvariant()) { keep = k; break; }
                    if (keep == null) keep = g[0];
                    foreach (var k in g)
                    {
                        if (k == keep) continue;
                        try { Environment.SetEnvironmentVariable(k, null); } catch { }
                    }
                }
            }
            catch { }
        }

        [STAThread]
        public static int Main(string[] args)
        {
            SanitizeEnv();
            string mode = args.Length > 0 ? args[0] : "";
            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.DispatcherUnhandledException += (s, e) =>
            {
                Store.LogError("未处理的错误", e.Exception);
                e.Handled = true;
                if (mode == "") MessageBox.Show("出错了：" + e.Exception.Message, "KMS", MessageBoxButton.OK, MessageBoxImage.Warning);
            };

            if (mode == "--selftest")
            {
                Store.Init(Path.Combine(Path.GetTempPath(), "ac-selftest-" + Guid.NewGuid().ToString("N")));
                app.Startup += async (s, e) => { app.Shutdown(await SelfTest.Run(args.Length > 1 ? args[1] : "selftest.log")); };
                return app.Run();
            }

            // AC_DATA_DIR 可以把数据目录换到别处（验证时用，平时不需要设置）
            Store.Init(Environment.GetEnvironmentVariable("AC_DATA_DIR"));
            var cfg = Store.Load();
            if (mode == "--test" || mode == "--run" || mode == "--chat")
            {
                app.Startup += async (s, e) => { app.Shutdown(await Headless.Run(cfg, args)); };
                return app.Run();
            }

            if (mode == "--watchtest" && args.Length >= 5) return Containers.WatchTest(args);
            if (mode == "--armtest" && args.Length >= 4) return Containers.ArmTest(cfg, args);

            // 自检用：--openapp <claude|codex> <数据文件夹或 -> <结果文件>，真实打开一个应用窗口并报告是否起来了
            if (mode == "--openapp" && args.Length >= 4)
            {
                var probe = new AppWindow { Id = "probe", Kind = args[1], Label = "probe", DataDir = args[2] == "-" ? "" : args[2] };
                var err = RealApps.Open(probe);
                Thread.Sleep(15000);
                var found = RealApps.Find(probe);
                string title = ""; int pid = 0;
                if (found != null) { try { found.Refresh(); title = found.MainWindowTitle; pid = found.Id; } catch { } }
                File.WriteAllText(args[3], "exe=" + RealApps.FindExe(args[1]) + "\nerror=" + err + "\nrunning=" + (found != null) + "\npid=" + pid + "\nwindow=" + title + "\n", new UTF8Encoding(true));
                return found != null ? 0 : 1;
            }

            var main = new MainWindow(cfg);
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            app.MainWindow = main.Win;
            main.Win.Show();
            if (mode == "--shot" && args.Length >= 3) main.Shot(args[1], args[2]);
            // 必须等界面消息循环跑起来之后再调用，await 之后才会回到界面线程
            if (mode == "--autorun" && args.Length >= 4)
                main.Win.Dispatcher.BeginInvoke(new Action(() => main.AutoRun(args[1], args[2], args[3])));
            if (mode == "--embed" && args.Length >= 3)
                main.Win.Dispatcher.BeginInvoke(new Action(() => main.AutoEmbed(args[1], args[2])));
            // 自检用：--embedvm <虚拟机名> <截图文件> [type|hold]，真实地把一台虚拟机的窗口放进 KMS
            if (mode == "--embedvm" && args.Length >= 3)
                main.Win.Dispatcher.BeginInvoke(new Action(() => main.AutoEmbedVm(args[1], args[2], args.Length >= 4 ? args[3] : "")));
            // --open <名字>：启动后直接打开“应用多开”里的这一项（名字可以是显示的名称，也可以是虚拟机名）
            if (mode == "--open" && args.Length >= 2)
                main.Win.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => main.OpenByName(args[1])));
            if (mode == "--tile" && args.Length >= 2)
                main.Win.Dispatcher.BeginInvoke(new Action(() => main.AutoTile(args[1])));
            if (mode == "--autochat" && args.Length >= 4)
                main.Win.Dispatcher.BeginInvoke(new Action(() => main.AutoChat(args[1], args[2], args[3])));
            return app.Run();
        }
    }

    public static class Defaults
    {
        static bool HasCliLogin(string agent)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (agent == "claude") return File.Exists(Path.Combine(home, ".claude", ".credentials.json"));
            if (agent == "codex") return File.Exists(Path.Combine(home, ".codex", "auth.json"));
            return File.Exists(Path.Combine(home, ".gemini", "oauth_creds.json"));
        }

        // 新建账号：电脑上已有该工具的命令行登录且还没被占用时，直接沿用；否则给它一个独立的登录目录
        public static Account NewAccount(AppConfig cfg, IEnumerable<Account> existing, string agent)
        {
            var a = new Account();
            a.Id = Guid.NewGuid().ToString("N").Substring(0, 8);
            a.Agent = agent;
            int n = existing.Count(x => x.Agent == agent) + 1;
            a.UseDefaultLogin = HasCliLogin(agent) && !existing.Any(x => x.Agent == agent && x.UseDefaultLogin);
            a.Label = a.UseDefaultLogin ? "本机已登录账号" : "账号 " + n;
            a.ProfileDir = a.UseDefaultLogin ? "" : Store.NewProfileDir(agent, a.Id);
            a.MaxParallel = 1; a.Enabled = true;
            return a;
        }

        // 第一次打开时，为电脑上已经登录过的命令行工具各建一个账号
        public static void Ensure(AppConfig cfg)
        {
            if (cfg.Accounts.Count > 0) return;
            foreach (var agent in Cli.Agents)
                if (Cli.Find(agent, cfg) != null && HasCliLogin(agent))
                    cfg.Accounts.Add(NewAccount(cfg, cfg.Accounts, agent));
            Store.Save(cfg);
        }

        public static string TestDir()
        {
            var d = Path.Combine(Store.DataDir, "test-workdir");
            Directory.CreateDirectory(d);
            return d;
        }

        public const string TestPrompt = "这是一次连接测试。不要读写任何文件，只回复两个字：就绪";
    }

#if OLD_AGENT_WINDOW   // 旧的纯文本窗口，已由 AgentWin.cs 里的新窗口取代
    public class AgentWindow
    {
        public readonly Window Win;
        readonly TextBox _box;
        readonly Account _acc;
        public bool Closing;
        int _shownLen;

        public AgentWindow(Account acc)
        {
            _acc = acc;
            Win = new Window();
            Win.Width = 620; Win.Height = 420; Win.MinWidth = 320; Win.MinHeight = 200;
            Win.Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x12, 0x20));
            Win.FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
            Win.ShowInTaskbar = true;
            Win.SetBinding(Window.TitleProperty, new Binding("Display") { Source = acc, StringFormat = "{0} — Agent 窗口" });

            var root = new DockPanel();
            var head = new Border();
            head.Background = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27));
            head.Padding = new Thickness(12, 8, 12, 8);
            DockPanel.SetDock(head, Dock.Top);
            var hp = new DockPanel();
            var status = new TextBlock(); status.Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA)); status.FontSize = 12;
            status.Margin = new Thickness(12, 0, 0, 0); status.VerticalAlignment = VerticalAlignment.Center;
            status.SetBinding(TextBlock.TextProperty, new Binding("StatusText") { Source = acc });
            DockPanel.SetDock(status, Dock.Right);
            var name = new TextBlock(); name.Foreground = Brushes.White; name.FontWeight = FontWeights.SemiBold; name.FontSize = 14;
            name.SetBinding(TextBlock.TextProperty, new Binding("Display") { Source = acc });
            DockPanel.SetDock(name, Dock.Left);
            var cur = new TextBlock(); cur.Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)); cur.FontSize = 12;
            cur.Margin = new Thickness(12, 0, 0, 0); cur.VerticalAlignment = VerticalAlignment.Center; cur.TextTrimming = TextTrimming.CharacterEllipsis;
            cur.SetBinding(TextBlock.TextProperty, new Binding("CurrentTask") { Source = acc });
            hp.Children.Add(status); hp.Children.Add(name); hp.Children.Add(cur);
            head.Child = hp;

            _box = new TextBox();
            _box.IsReadOnly = true; _box.TextWrapping = TextWrapping.Wrap; _box.BorderThickness = new Thickness(0);
            _box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _box.Background = Win.Background; _box.Foreground = new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB));
            _box.FontFamily = Look.Mono; _box.FontSize = 12.5;
            _box.Padding = new Thickness(10);
            _box.Text = acc.Output.Length > 0 ? acc.Output.ToString() : "等待总指挥派活…\n";
            _shownLen = _box.Text.Length;
            root.Children.Add(head); root.Children.Add(_box);
            Win.Content = root;

            acc.OutputAppended += OnOutput;
            Win.Loaded += (s, e) => _box.ScrollToEnd();
            Win.Closing += (s, e) => { if (!Closing) { e.Cancel = true; Win.Hide(); } };
            Win.Closed += (s, e) => acc.OutputAppended -= OnOutput;
        }

        void OnOutput(string s)
        {
            // 自己记长度：每次都读 TextBox.Text 会把全部内容复制一遍，输出多了会越来越卡
            _shownLen += s.Length;
            if (_shownLen > 400000) { var all = _acc.Output.ToString(); _box.Text = all; _shownLen = all.Length; }
            else _box.AppendText(s);
            _box.ScrollToEnd();
        }
    }

#endif

    public class MainWindow : IAgentHost
    {
        // Agent 窗口需要的几样东西
        public AppConfig Cfg { get { return _cfg; } }
        public void Post(Action a) { Win.Dispatcher.BeginInvoke(a); }
        public void Save() { SaveAll(); }

        // 多账号：项目文件夹和执行权限都是每个账号自己的，互不影响
        public string PermFor(Account a) { return a.Perm == "readonly" || a.Perm == "full" ? a.Perm : "edit"; }
        public string ProjectDirOf(Account a) { return Directory.Exists(a.ProjectDir) ? a.ProjectDir : ""; }
        public string ProjectDirOrAsk(Account a, bool force)
        {
            string dir = ProjectDirOf(a);
            if (dir.Length > 0 && !force) return dir;
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
            {
                dlg.Description = "选一个文件夹给“" + a.Display + "”用。每个账号可以有自己的项目文件夹。";
                if (Directory.Exists(dir)) dlg.SelectedPath = dir;
                else if (Directory.Exists(_projectDir)) dlg.SelectedPath = _projectDir;
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return null;
                a.ProjectDir = dlg.SelectedPath;
            }
            SaveAll();
            return a.ProjectDir;
        }
        public void SetPerm(Account a, string perm) { a.Perm = perm; SaveAll(); RefreshFooter(); }

        public readonly Window Win;
        readonly AppConfig _cfg;
        readonly ObservableCollection<Account> _accounts;
        readonly Dictionary<string, AgentWindow> _wins = new Dictionary<string, AgentWindow>();
        readonly Queue<KeyValuePair<string, FrameworkElement>> _queue = new Queue<KeyValuePair<string, FrameworkElement>>();
        readonly List<string> _history = new List<string>();
        Commander _cmd;
        Account _chief;
        string _projectDir = "";
        bool _busy;          // 正在测试账号
        bool _running;       // 正在执行一轮任务
        bool _stopped;       // 这一轮是被用户点了停止
        StackPanel _card;    // 当前这一轮的任务卡片
        FrameworkElement _statusRow;
        TextBlock _statusLabel, _statusText, _statusTime;
        DateTime _runStart;

        readonly StackPanel ChatPanel, EmptyState;
        readonly ScrollViewer ChatScroll;
        readonly TextBox InputBox;
        readonly TextBlock InputHint, EmptyHint, CliText, DetectText, SaveText;
        readonly Button SendBtn, FolderBtn, ChiefBtn, PermBtn, PlusBtn;
        readonly Ellipse Ring;
        readonly ListBox AccountList;
        readonly ComboBox ModelBox, EffortBox;
        readonly TextBlock ModelHint, NoAccountText;
        readonly RadioButton NavTask, NavSettings;
        readonly Grid PageTask, PageAccounts, PageSettings, PageAgent, PageApps;
        readonly ListBox AppList;
        ObservableCollection<AppWindow> _apps = new ObservableCollection<AppWindow>();   // 真实应用多开的各个窗口
        int _tick;
        readonly Dictionary<string, AgentView> _views = new Dictionary<string, AgentView>();   // 嵌在主窗口里的各个 Agent 对话界面
        readonly HashSet<Account> _awaitLogin = new HashSet<Account>();   // 打开过登录窗口、还没确认登录结果的账号
        bool _loadingAccount;
        DateTime _lastLoginCheck = DateTime.MinValue;

        T F<T>(string name) where T : class
        {
            var x = Win.FindName(name) as T;
            if (x == null) throw new InvalidOperationException("界面缺少控件 " + name);
            return x;
        }

        public MainWindow(AppConfig cfg)
        {
            _cfg = cfg;
            Defaults.Ensure(cfg);
            _accounts = new ObservableCollection<Account>(cfg.Accounts);
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml"))
                Win = (Window)XamlReader.Load(s);

            ChatPanel = F<StackPanel>("ChatPanel"); EmptyState = F<StackPanel>("EmptyState"); ChatScroll = F<ScrollViewer>("ChatScroll");
            InputBox = F<TextBox>("InputBox"); InputHint = F<TextBlock>("InputHint"); EmptyHint = F<TextBlock>("EmptyHint");
            SendBtn = F<Button>("SendBtn"); FolderBtn = F<Button>("FolderBtn"); ChiefBtn = F<Button>("ChiefBtn"); PermBtn = F<Button>("PermBtn"); PlusBtn = F<Button>("PlusBtn");
            Ring = F<Ellipse>("Ring");
            AccountList = F<ListBox>("AccountList");
            ModelBox = F<ComboBox>("ModelBox"); EffortBox = F<ComboBox>("EffortBox");
            ModelHint = F<TextBlock>("ModelHint"); NoAccountText = F<TextBlock>("NoAccountText");
            NavTask = F<RadioButton>("NavTask"); NavSettings = F<RadioButton>("NavSettings");
            CliText = F<TextBlock>("CliText"); DetectText = F<TextBlock>("DetectText"); SaveText = F<TextBlock>("SaveText");
            PageTask = F<Grid>("PageTask"); PageAccounts = F<Grid>("PageAccounts"); PageSettings = F<Grid>("PageSettings"); PageAgent = F<Grid>("PageAgent");

            // 以前保存的 Claude 模型是 fable / opus 这类别名：换成具体的模型名
            var aliases = new Dictionary<string, string> { { "fable", "claude-fable-5-2" }, { "opus", "claude-opus-5-5" }, { "sonnet", "claude-sonnet-5-5" }, { "haiku", "claude-haiku-5-5" } };
            foreach (var a in _accounts)
            {
                string full;
                if (a.Agent == "claude" && aliases.TryGetValue(a.Model.ToLowerInvariant(), out full)) a.Model = full;
            }
            _projectDir = Directory.Exists(_cfg.LastProjectDir) ? _cfg.LastProjectDir : "";
            _chief = _accounts.FirstOrDefault(a => a.Id == _cfg.ChiefId) ?? _accounts.FirstOrDefault();
            AccountList.ItemsSource = _accounts;
            PageSettings.DataContext = _cfg;
            _accounts.CollectionChanged += (s, e) => NoAccountText.Visibility = _accounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            NoAccountText.Visibility = _accounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // ---- 账号调度：真实的官方桌面应用，每个窗口一个账号 ----
            // 上次被强行结束时留在“子窗口”状态的窗口，先恢复成正常窗口
            try { RealApps.RepairRemembered(); RealApps.RepairOrphans(); } catch (Exception ex) { Store.LogError("恢复被留下的窗口", ex); }
            PageApps = F<Grid>("PageApps"); AppList = F<ListBox>("AppList");
            SeedApps();
            _apps = new ObservableCollection<AppWindow>(_cfg.Apps);
            AppList.ItemsSource = _apps;
            AppList.SelectionChanged += (s, e) =>
            {
                var w = AppList.SelectedItem as AppWindow;
                if (w == null) return;
                AccountList.SelectedItem = null;
                NavTask.IsChecked = false; NavSettings.IsChecked = false;
                ShowApp(w);   // 点一下：右边就变成这个真实应用的窗口
            };
            F<Button>("AddAppBtn").Click += (s, e) => AddAppMenu();
            // 右键一个窗口：重命名、登录、放回桌面、删除
            AppList.PreviewMouseRightButtonDown += (s, e) =>
            {
                var hit = AppAt(e.OriginalSource as DependencyObject);
                if (hit != null) AppList.SelectedItem = hit;
            };
            AppList.ContextMenu = AppMenu();
            F<Button>("OpenAppBtn").Click += (s, e) => { var w = PageApps.DataContext as AppWindow; if (w != null) ShowApp(w); };
            F<Button>("PopAppBtn").Click += (s, e) => { var w = PageApps.DataContext as AppWindow; if (w != null) PopApp(w, 0); };
            F<Button>("TileAppsBtn").Click += (s, e) =>
            {
                // 先把嵌在 KMS 里的都放回桌面，稍等它们变回独立窗口，再并排
                ReleaseAllApps();
                var later = new DispatcherTimer(); later.Interval = TimeSpan.FromMilliseconds(500);
                later.Tick += (o, a) => { later.Stop(); TileApps(); };
                later.Start();
            };
            F<Button>("DeleteAppBtn").Click += (s, e) => DeleteApp();
            F<Button>("LoginWizBtn").Click += (s, e) => LoginMenu();
            var webBtn = Win.FindName("WebBtn") as Button;
            if (webBtn != null) webBtn.Click += (s, e) => ToggleWeb();
            // 真空浏览器那边有动静时，在这一页顶上提示一句
            Containers.Notice = (w, text) => Win.Dispatcher.BeginInvoke(new Action(() => ShowAppNotice(w, text)));
            // 登录结果交回应用以后：等浏览器把最后一步走完，自动关掉它并切回应用
            Containers.Delivered = (w) => Win.Dispatcher.BeginInvoke(new Action(() => FinishWeb(w)));
            try
            {
                using (var png = Assembly.GetExecutingAssembly().GetManifestResourceStream("kms.png"))
                    if (png != null) Win.Icon = BitmapFrame.Create(png, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            }
            catch (Exception ex) { Store.LogError("窗口图标", ex); }

            // 左侧菜单：“账号调度”和“设置”
            NavTask.Checked += (s, e) => { ShowPage(PageApps); if (PageApps.DataContext == null) ShowAppsEmptyHint(); };
            NavSettings.Checked += (s, e) => { ShowPage(PageSettings); };
            // 点左侧的账号：右边变成这个 Agent 的对话界面
            AccountList.SelectionChanged += (s, e) => { var a = AccountList.SelectedItem as Account; if (a != null) { AppList.SelectedItem = null; ShowAgent(a); } };
            // 右键账号：先选中它，再弹一个小菜单（重命名、独立窗口、启用/停用、删除…）
            AccountList.PreviewMouseRightButtonDown += (s, e) =>
            {
                var hit = AccountAt(e.OriginalSource as DependencyObject);
                if (hit != null) AccountList.SelectedItem = hit;
            };
            AccountList.ContextMenu = AccountMenu();
            F<Button>("BackToChatBtn").Click += (s, e) => { var a = PageAccounts.DataContext as Account; if (a != null) ShowAgent(a); };
            F<Button>("AddAccountBtn").Click += (s, e) => AddMenu();
            F<Button>("NewChatBtn").Click += (s, e) => NewChat();

            InputBox.TextChanged += (s, e) => InputHint.Visibility = InputBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            InputBox.PreviewKeyDown += OnInputKey;
            SendBtn.Click += (s, e) => { if (_running) StopRun(); else Send(); };
            FolderBtn.Click += (s, e) => Browse();
            ChiefBtn.Click += (s, e) => ChiefMenu();
            PermBtn.Click += (s, e) => PermMenu();
            // 右下角的圆环：点开看担任总指挥的那个账号的用量
            Ring.Cursor = Cursors.Hand; Ring.Fill = Brushes.Transparent; Ring.ToolTip = "查看用量";
            Ring.MouseLeftButtonUp += (s, e) => { if (_chief != null) UsagePopup.Show(Ring, _chief); };
            PlusBtn.Click += (s, e) => PlusMenu();

            F<Button>("LoginBtn").Click += (s, e) => Login();
            F<Button>("RefreshBtn").Click += (s, e) => { var a = Selected(); if (a != null) RefreshLogin(a); };
            F<Button>("LogoutBtn").Click += (s, e) => LogoutAccount();
            EffortBox.SelectionChanged += (s, e) =>
            {
                var a = PageAccounts.DataContext as Account;
                if (_loadingAccount || a == null || EffortBox.SelectedItem == null) return;
                var pick = (string)EffortBox.SelectedItem;
                a.Effort = pick == DefaultChoice ? "" : pick;
                SaveAll();
            };
            ModelBox.LostFocus += (s, e) => { if (!_loadingAccount) SaveAll(); };
            ModelBox.DropDownClosed += (s, e) => { if (!_loadingAccount) SaveAll(); };
            F<Button>("TestBtn").Click += (s, e) => TestAccount();
            F<Button>("OpenWinBtn").Click += (s, e) => { var a = Selected(); if (a != null) WindowFor(a, true); };
            F<Button>("ToggleBtn").Click += (s, e) => { var a = Selected(); if (a != null) { a.Enabled = !a.Enabled; SaveAll(); } };
            F<Button>("DeleteBtn").Click += (s, e) => DeleteAccount();
            F<Button>("SaveBtn").Click += (s, e) => { SaveAll(); PageSettings.DataContext = null; PageSettings.DataContext = _cfg; RefreshCli(); SaveText.Text = "已保存 " + DateTime.Now.ToString("HH:mm:ss"); };

            var timer = new DispatcherTimer(); timer.Interval = TimeSpan.FromSeconds(1);
            timer.Tick += (s, e) =>
            {
                if (_cmd != null) foreach (var t in _cmd.Tasks) t.Tick();
                foreach (var a in _accounts) a.RefreshStatus();
                if (_running && _statusTime != null) _statusTime.Text = Span(DateTime.Now - _runStart);
                WatchSlots();
                // 开着的虚拟机每隔几秒唤醒一次，不然它会不定时停住（原因见 Vms.Kick）
                if (_tick % 4 == 1)
                {
                    var awake = _apps.Where(x => x.IsVm && x.Running).Select(x => x.VmName).ToList();
                    if (awake.Count > 0) Task.Run(() => { foreach (var n in awake) Vms.Kick(n); });
                }
                // 每隔几秒看一眼各个应用窗口是开着还是关了（放到后台线程，不卡界面）
                if (++_tick % 3 == 0 && _apps.Count > 0)
                {
                    var list = _apps.ToList();
                    Task.Run(() =>
                    {
                        RealApps.Refresh(list);
                        // 开着的多开窗口都配上“真空浏览器”这一套（原窗口不动）
                        foreach (var w in list) if (w.Running) { try { Containers.Ensure(w); Containers.EnsureBrowser(w); } catch (Exception ex) { Store.LogError("窗口配套", ex); } }
                    });
                }
            };
            timer.Start();

            Win.Closing += (s, e) =>
            {
                if (_running)
                {
                    if (MessageBox.Show(Win, "任务还在运行，关闭会终止所有正在工作的 Agent。确定关闭吗？", "总指挥",
                            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) { e.Cancel = true; return; }
                    if (_cmd != null) _cmd.Stop();
                }
                // 关闭前把嵌在里面的应用窗口放回桌面，不然它们会跟着 KMS 一起被关掉
                try { ReleaseAllApps(); } catch (Exception ex) { Store.LogError("放回应用窗口", ex); }
                try { Containers.Notice = null; Containers.Delivered = null; Containers.StopAll(); } catch (Exception ex) { Store.LogError("停止窗口配套", ex); }
                // 关闭前把还在跑的命令行进程全部结束，不留在后台继续消耗用量
                Cli.KillAll();
                SaveAll();
                foreach (var w in _wins.Values) { w.Closing = true; w.Win.Close(); }
            };
            Win.Loaded += (s, e) =>
            {
                InputBox.Focus(); RefreshAllLogins();
                // 等窗口显示出来再提醒，自检和截图模式下不打扰
                if (Environment.GetCommandLineArgs().Length <= 1)
                    Win.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(CheckUnfinishedWizard));
            };
            // 恢复上次的内容：总指挥页的对话，以及每个账号最近的一段会话
            try
            {
                RestoreChat();
                foreach (var a in _accounts)
                {
                    var last = Sessions.List(a.Id).FirstOrDefault();
                    if (last != null) Sessions.Open(a, last);
                }
            }
            catch (Exception ex) { Store.LogError("恢复上次的内容", ex); }
            // 用户在登录窗口里登完回到本程序时，自动重新查询那几个账号的登录状态
            Win.Activated += (s, e) =>
            {
                if (_awaitLogin.Count == 0 || (DateTime.Now - _lastLoginCheck).TotalSeconds < 4) return;
                _lastLoginCheck = DateTime.Now;
                foreach (var a in _awaitLogin.ToList()) RefreshLogin(a);
            };
            RefreshCli();
            SetRunning(false);
            RefreshFooter();
            // 现在只有“账号调度”：打开程序就停在这一页，还没选窗口时给一句提示
            ShowPage(PageApps);
            ShowAppsEmptyHint();
        }

        const string DefaultChoice = "默认";

        // ---------- 通用 ----------

        void ShowPage(Grid page)
        {
            PageTask.Visibility = page == PageTask ? Visibility.Visible : Visibility.Collapsed;
            PageApps.Visibility = page == PageApps ? Visibility.Visible : Visibility.Collapsed;
            PageAgent.Visibility = page == PageAgent ? Visibility.Visible : Visibility.Collapsed;
            PageAccounts.Visibility = page == PageAccounts ? Visibility.Visible : Visibility.Collapsed;
            PageSettings.Visibility = page == PageSettings ? Visibility.Visible : Visibility.Collapsed;
            if (page == PageSettings) SaveText.Text = "";
        }

        void RefreshCli()
        {
            var side = new StringBuilder(); var detail = new StringBuilder();
            foreach (var agent in Cli.Agents)
            {
                var p = Cli.Find(agent, _cfg);
                side.AppendLine(Cli.DisplayName(agent) + (p != null ? "  已找到" : "  未安装"));
                detail.AppendLine(Cli.DisplayName(agent) + "：" + (p ?? "未找到"));
            }
            CliText.Text = side.ToString().TrimEnd();
            DetectText.Text = "当前使用的程序\n" + detail.ToString().TrimEnd();
        }

        // ---------- 应用多开 ----------

        // 第一次用：把应用原本的那一份列出来，方便一起排列；之前试开的第二个 Claude 窗口也接进来
        void SeedApps()
        {
            if (_cfg.Apps.Count > 0) return;
            if (RealApps.FindExe("claude") != null) _cfg.Apps.Add(new AppWindow { Id = "claude-main", Kind = "claude", Label = "Claude 原窗口", DataDir = "" });
            var second = Path.Combine(Store.DataDir, "real-apps", "claude-2");
            if (Directory.Exists(second)) _cfg.Apps.Add(new AppWindow { Id = "claude-2", Kind = "claude", Label = "Claude 窗口 2", DataDir = second });
            if (RealApps.FindExe("codex") != null) _cfg.Apps.Add(new AppWindow { Id = "codex-main", Kind = "codex", Label = "Codex 原窗口", DataDir = "" });
            var codex2 = Path.Combine(Store.DataDir, "real-apps", "codex-2");
            if (Directory.Exists(codex2)) _cfg.Apps.Add(new AppWindow { Id = "codex-2", Kind = "codex", Label = "Codex 窗口 2", DataDir = codex2 });
            Store.Save(_cfg);
        }

        static AppWindow NewVm(string vmName)
        {
            return new AppWindow { Id = "vm-" + Guid.NewGuid().ToString("N").Substring(0, 6), Kind = "vm", Label = vmName, DataDir = "", VmName = vmName };
        }


        void AddAppMenu()
        {
            var m = new ContextMenu();
            foreach (var kind in new[] { "claude", "codex" })
            {
                var k = kind;
                var it = new MenuItem(); it.Header = "再开一个 " + (k == "codex" ? "Codex" : "Claude") + " 桌面版窗口";
                it.IsEnabled = RealApps.FindExe(k) != null;
                it.Click += (s, e) => AddApp(k);
                m.Items.Add(it);
            }

            m.PlacementTarget = F<Button>("AddAppBtn");
            m.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            m.IsOpen = true;
        }

        void AddApp(string kind)
        {
            var w = new AppWindow();
            w.Id = kind + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            w.Kind = kind;
            w.Label = (kind == "codex" ? "Codex" : "Claude") + " 窗口 " + (_apps.Count(x => x.Kind == kind) + 1);
            w.DataDir = Path.Combine(Store.DataDir, "real-apps", w.Id);
            _apps.Add(w);
            SaveAll();
            AppList.SelectedItem = w;   // 选中即打开
        }

        void OpenApp(AppWindow w)
        {
            var err = RealApps.Open(w);
            if (err != null) Warn(err);
        }

        // ---- 登录：每个多开窗口配一个自己的真空浏览器（见 Containers.cs） ----
        void LoginMenu()
        {
            var w = PageApps.DataContext as AppWindow;
            if (w == null) { Warn("请先在左侧选中一个窗口。"); return; }
            if (w.IsVm) { Warn("虚拟机里是一整套独立的 Windows。\n直接在虚拟机的画面里打开 Claude 或 Codex，像在一台新电脑上那样登录就行，不需要 KMS 帮忙。"); return; }
            if (string.IsNullOrEmpty(w.DataDir)) { Warn("这是你平时用的原窗口，它照常登录就行，不需要 KMS 帮忙。\n要登录别的账号，请在左侧选一个新开的窗口（比如“" + (w.Kind == "codex" ? "Codex" : "Claude") + " 窗口 2”）。"); return; }
            var m = new ContextMenu();
            var how = new MenuItem(); how.Header = "怎么登录（先看这个）";
            how.Click += (s, e) => LoginHelp(w);
            m.Items.Add(how);
            var open = new MenuItem(); open.Header = "打开这个窗口专用的真空浏览器";
            open.Click += (s, e) => OpenBrowserHere(w, null);
            m.Items.Add(open);
            m.Items.Add(new Separator());
            var signOut = new MenuItem(); signOut.Header = "退出登录 / 换个账号…";
            signOut.Click += (s, e) => SignOutWindow(w);
            m.Items.Add(signOut);
            m.Items.Add(new Separator());
            if (w.Kind == "claude")
            {
                var paste = new MenuItem(); paste.Header = "把剪贴板里的登录链接交给这个窗口";
                paste.ToolTip = "先在邮件或浏览器里复制登录链接，再点这里";
                paste.Click += (s, e) => PasteLoginLink(w);
                m.Items.Add(paste);
                m.Items.Add(new Separator());
                var old = new MenuItem(); old.Header = "备用办法：搬数据的登录向导（要先关掉所有 Claude）";
                old.Click += (s, e) => LoginWizard(w, false);
                m.Items.Add(old);
            }
            m.PlacementTarget = F<Button>("LoginWizBtn");
            m.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            m.IsOpen = true;
        }

        void LoginHelp(AppWindow w)
        {
            if (w.Kind == "codex")
            {
                Warn("给“" + w.Label + "”登录账号：\n\n"
                    + "1. 在这个窗口里照常点登录。\n"
                    + "2. KMS 会自动弹出一个它专用的真空浏览器（访客模式，里面没有任何已登录的账号），在那里面登录你要给这个窗口用的账号。\n"
                    + "3. 登录完，结果会自己回到这个窗口。\n\n"
                    + "你平时的浏览器和原窗口不受影响。");
                return;
            }
            Containers.Arm(w);
            Warn("给“" + w.Label + "”登录账号：\n\n"
                + "用 Google 登录：\n"
                + "1. 在这个窗口里点“Continue with Google”。\n"
                + "2. KMS 会自动弹出一个它专用的真空浏览器（访客模式，里面没有任何已登录的账号）。在那里面登录你要给这个窗口用的 Google 账号。\n"
                + "3. 登录完，浏览器会问“要打开 Claude 吗”，点“打开”。KMS 会把结果交给这个窗口，而不是原窗口。\n\n"
                + "用邮箱登录：\n"
                + "1. 在这个窗口里输入邮箱，点“Continue with email”。\n"
                + "2. 点上面“登录”菜单里的“打开这个窗口专用的真空浏览器”，在里面打开邮箱，点邮件里的登录链接。\n"
                + "   或者：在邮件里右键复制登录链接，再点“把剪贴板里的登录链接交给这个窗口”。\n\n"
                + "你平时的浏览器和原窗口不受影响。接下来 15 分钟内 KMS 会留意这个窗口的登录结果。\n\n"
                + "点“确定”后，这个窗口专用的浏览器会直接开在窗口里。");
            OpenBrowserHere(w, null);
        }

        void PasteLoginLink(AppWindow w)
        {
            string text = "";
            try { text = Clipboard.GetText() ?? ""; } catch { }
            var link = Containers.NormalizeLink(text);
            if (link == null) { Warn("剪贴板里没有 Claude 的登录链接。\n\n请先复制邮件里那条登录链接（以 https://claude.ai/magic-link 开头），再点这里。"); return; }
            var err = Containers.Deliver(w, link);
            ShowAppNotice(w, err ?? "登录链接已交给“" + w.Label + "”。回到这个窗口，稍等几秒看它是否进入主界面。");
            if (err != null) Warn(err);
        }

        void ShowAppNotice(AppWindow w, string text)
        {
            var box = F<TextBlock>("AppNotice");
            box.Text = DateTime.Now.ToString("HH:mm") + "  " + text;
            box.Visibility = Visibility.Visible;
            box.Tag = w == null ? "" : w.Id;
        }

        // ---- 登录向导：给一个新开的 Claude 窗口登录新账号 ----
        // 原理见 SlotSwap.cs：新账号在“默认位置”登录（那里 Google 登录是通的），登完把那份数据搬去当这个窗口。
        void LoginWizard(AppWindow w, bool resume)
        {
            if (w == null) { Warn("请先在左侧选中要登录的那个窗口。"); return; }
            if (w.Kind != "claude") { Warn("登录向导只用于 Claude 桌面版。Codex 窗口直接在它自己的窗口里登录就行。"); return; }
            if (string.IsNullOrEmpty(w.DataDir)) { Warn("这是你平时用的原窗口，它本来就能正常登录，不需要向导。\n请在左侧选一个新开的窗口（比如“Claude 窗口 2”）。"); return; }
            const string cap = "KMS 登录向导";
            if (!resume)
            {
                if (SlotSwap.ParkedDir() != null) { resume = true; }
                else if (MessageBox.Show(Win,
                        "这个向导会让“" + w.Label + "”登录一个新的 Claude 账号。步骤：\n\n"
                        + "1. 你先把所有 Claude 窗口关掉（原窗口和新开的都要关）。\n"
                        + "2. KMS 把你现在这份 Claude 数据改个名放到一边（不删除），然后打开一个空白状态的 Claude。\n"
                        + "3. 你在里面用 Google 登录新账号，登录成功后关掉它。\n"
                        + "4. KMS 把新账号的数据交给“" + w.Label + "”，再把你原来那份数据原样放回去。\n\n"
                        + "全程只改文件夹的名字，不删除任何东西。现在开始吗？", cap, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            }
            if (!resume)
            {
                ReleaseAllApps();
                if (SlotSwap.ClaudeProcessCount() > 0)
                {
                    Warn("还有 Claude 在运行。\n\n请把所有 Claude 窗口都关掉（包括原窗口、新开的窗口，以及右下角托盘里的 Claude 图标——右键点“退出”），然后再点一次“登录向导”。");
                    return;
                }
                var err = SlotSwap.Park();
                if (err != null) { Warn(err); return; }
                _cfg.WizardTarget = w.Id; SaveAll();
                var open = RealApps.Open(new AppWindow { Id = "default", Kind = "claude", Label = "Claude", DataDir = "" }, true);
                if (open != null) { SlotSwap.Restore(); _cfg.WizardTarget = ""; SaveAll(); Warn(open + "\n原来的数据已经恢复。"); return; }
            }
            while (true)
            {
                var r = MessageBox.Show(Win,
                    "现在的 Claude 是空白的新状态（如果它没有自己弹出来，请从开始菜单打开 Claude）。\n\n"
                    + "① 在里面点“用 Google 登录”，选你要给“" + w.Label + "”用的那个账号。\n"
                    + "　 如果浏览器直接用了旧账号：先在浏览器里打开 claude.ai 退出登录，再回到 Claude 重新点登录。\n"
                    + "② 登录成功、看到 Claude 主界面之后，把 Claude 关掉（托盘里的图标也要右键“退出”）。\n"
                    + "③ 回到这里点“是”。\n\n"
                    + "点“否”是放弃：原来的数据会原样恢复。", cap, MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (SlotSwap.ClaudeProcessCount() > 0)
                {
                    Warn("Claude 还开着。请先把它完全关掉（托盘里的图标也要右键“退出”），再继续。");
                    continue;
                }
                if (r != MessageBoxResult.Yes)
                {
                    var back = SlotSwap.Restore();
                    _cfg.WizardTarget = ""; SaveAll();
                    Warn(back ?? "已放弃。原来的数据已经恢复，可以照常打开 Claude。");
                    return;
                }
                break;
            }
            var target = SlotSwap.WindowDir(w.Id);
            var done = SlotSwap.Finish(target);
            if (SlotSwap.ParkedDir() == null) { _cfg.WizardTarget = ""; }
            if (done != null) { SaveAll(); Warn(done); return; }
            w.DataDir = target;
            SaveAll();
            PageApps.DataContext = null; PageApps.DataContext = w;
            Warn("完成。\n\n“" + w.Label + "”现在用的是刚才登录的新账号：在左侧点它，打开的就是那个账号。\n你原来的 Claude 数据已经原样放回，照常打开即可。");
        }

        // 启动时：如果上次的登录向导没走完（原来的数据还放在一边），提醒并让用户选择继续或恢复
        void CheckUnfinishedWizard()
        {
            if (SlotSwap.ParkedDir() == null) return;
            var w = _apps.FirstOrDefault(x => x.Id == _cfg.WizardTarget);
            var r = MessageBox.Show(Win,
                "上次的登录向导没有走完：你原来的 Claude 数据还放在一边（没有丢）。\n\n"
                + "点“是”：继续完成向导。\n点“否”：放弃，把原来的数据恢复原样。", "KMS 登录向导", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r == MessageBoxResult.Yes && w != null) { AppList.SelectedItem = null; PageApps.DataContext = w; LoginWizard(w, true); return; }
            if (SlotSwap.ClaudeProcessCount() > 0) { Warn("要恢复原来的数据，需要先把所有 Claude 窗口关掉（托盘图标也要退出）。关掉后重新打开 KMS 即可。"); return; }
            var back = SlotSwap.Restore();
            _cfg.WizardTarget = ""; SaveAll();
            Warn(back ?? "原来的数据已经恢复，可以照常打开 Claude。");
        }

        // ---- 把真实应用的窗口嵌进 KMS 窗口右侧 ----
        class Slot
        {
            public HostPanel Panel;
            public IntPtr Hwnd = IntPtr.Zero;
            public IntPtr OldStyle = IntPtr.Zero;
            public int PadW, PadH;   // 虚拟机窗口要比这一格多放大的宽高，见 RealApps.FrameSize
            // 这个窗口自己的登录浏览器：和应用占同一格，需要时盖在应用上面
            public HostPanel Web;
            public IntPtr WebHwnd = IntPtr.Zero;
            public IntPtr WebOldStyle = IntPtr.Zero;
            public bool ShowWeb;
        }
        readonly Dictionary<string, Slot> _slots = new Dictionary<string, Slot>();
        System.Windows.Forms.Integration.WindowsFormsHost _wfHost;
        System.Windows.Forms.Panel _wfRoot;
        DispatcherTimer _embedTimer;
        AppWindow _embedTarget;
        int _embedTries;
        int _embedRetries;                       // 应用自己跑回桌面时，自动再放进来几次
        IntPtr _embedSeen = IntPtr.Zero;

        Slot SlotFor(AppWindow w)
        {
            if (_wfHost == null)
            {
                _wfRoot = new System.Windows.Forms.Panel();
                _wfRoot.Dock = System.Windows.Forms.DockStyle.Fill; _wfRoot.BackColor = System.Drawing.Color.White;
                _wfHost = new System.Windows.Forms.Integration.WindowsFormsHost();
                _wfHost.Child = _wfRoot; _wfHost.Visibility = Visibility.Collapsed;
                F<Grid>("AppHostArea").Children.Add(_wfHost);
            }
            Slot s;
            if (!_slots.TryGetValue(w.Id, out s))
            {
                s = new Slot();
                s.Panel = new HostPanel();
                s.Panel.Dock = System.Windows.Forms.DockStyle.Fill; s.Panel.BackColor = System.Drawing.Color.White; s.Panel.Visible = false;
                var slot = s;
                // KMS 窗口改大小时，里面的应用窗口跟着改
                s.Panel.Resize += (o, e) => RealApps.Resize(slot.Hwnd, slot.Panel.ClientSize.Width + slot.PadW, slot.Panel.ClientSize.Height + slot.PadH);
                _wfRoot.Controls.Add(s.Panel);
                s.Web = new HostPanel();
                s.Web.Dock = System.Windows.Forms.DockStyle.Fill; s.Web.BackColor = System.Drawing.Color.White; s.Web.Visible = false;
                s.Web.Resize += (o, e) => RealApps.Resize(slot.WebHwnd, slot.Web.ClientSize.Width, slot.Web.ClientSize.Height);
                _wfRoot.Controls.Add(s.Web);
                _slots[w.Id] = s;
            }
            return s;
        }

        static bool IsEmbedded(Slot s) { return RealApps.IsChildOf(s.Hwnd, s.Panel.Handle); }
        static bool IsWebEmbedded(Slot s) { return s.WebHwnd != IntPtr.Zero && RealApps.IsWindow(s.WebHwnd); }

        // 右边这块只显示当前选中窗口的那一格；它的登录浏览器开着并且轮到它时，显示浏览器，否则显示应用
        void Arrange(Slot current)
        {
            foreach (var s in _slots.Values)
            {
                bool web = s == current && s.ShowWeb && IsWebEmbedded(s);
                s.Web.Visible = web;
                s.Panel.Visible = s == current && !web;
                if (s.Panel.Visible && s.Hwnd != IntPtr.Zero) RealApps.Align(s.Hwnd, s.Panel.Handle);
            }
            var btn = Win.FindName("WebBtn") as Button;
            bool has = current != null && IsWebEmbedded(current);
            if (btn != null)
            {
                btn.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
                if (has) btn.Content = current.ShowWeb ? "返回应用" : "登录浏览器";
            }
        }

        // 账号调度页：还没选窗口时显示的那段话
        void ShowAppsEmptyHint()
        {
            SetHostVisible(false);
            var bar = Win.FindName("AppTopBar") as FrameworkElement;
            if (bar != null) bar.Visibility = Visibility.Collapsed;
            F<TextBlock>("AppHostTitle").Text = "在左边选一个窗口";
            F<TextBlock>("AppHostText").Text = "左边每一条就是一个独立的 Claude 或 Codex 窗口，各自登录各自的账号。想再开一个，点上面的「＋ 添加」。";
        }

        // 在这个窗口里打开它专用的浏览器，并嵌进它那一格（不再单独飘一个浏览器窗口）
        void OpenBrowserHere(AppWindow w, string url)
        {
            if (AppList.SelectedItem != w) AppList.SelectedItem = w; else ShowApp(w);
            Task.Run(() =>
            {
                Containers.Arm(w);
                var err = Containers.OpenBrowser(w, url, true, true);   // 独立窗口打开，能正常打字
                Win.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (err != null) { ShowAppNotice(w, err); return; }
                    ShowAppNotice(w, "浏览器已经打开（独立窗口，会记住“" + w.Label + "”自己的登录状态）。关掉以后随时可以再从「登录 ▾」里打开同一个。");
                }));
            });
        }

        // 转接器传话来：某个多开窗口要打开网页。在它自己的浏览器里打开，并把浏览器放进它那一格。
        void OnRoutedLink(string id, string url)
        {
            var w = _apps.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase) && !x.IsVm && !string.IsNullOrEmpty(x.DataDir));
            if (w == null) { LinkRouter.Forward(url); return; }
            ShowAppNotice(w, "“" + w.Label + "”要打开网页，正在转到它自己的浏览器（就在下面这一格里）…");
            OpenBrowserHere(w, url);
        }

        void EmbedWeb(AppWindow w, Slot s, IntPtr h)
        {
            try
            {
                // 先让这一格显示出来、有了大小，再把浏览器窗口放进去
                SetHostVisible(true);
                s.Web.Visible = true;
                _wfHost.UpdateLayout();
                s.WebOldStyle = RealApps.Embed(h, s.Web.Handle, s.Web.ClientSize.Width, s.Web.ClientSize.Height);
                s.WebHwnd = h; s.Web.Child = h;
            }
            catch (Exception ex) { Store.LogError("嵌入登录浏览器", ex); }
        }

        // “返回应用 / 登录浏览器”按钮
        void ToggleWeb()
        {
            var w = PageApps.DataContext as AppWindow;
            Slot s;
            if (w == null || !_slots.TryGetValue(w.Id, out s) || !IsWebEmbedded(s)) return;
            s.ShowWeb = !s.ShowWeb;
            Arrange(s);
        }

        // 登录结果已经交回应用：稍等一下让浏览器把最后一步走完，然后关掉它，回到应用
        void FinishWeb(AppWindow w)
        {
            Slot s;
            if (w == null || !_slots.TryGetValue(w.Id, out s)) return;
            var later = new DispatcherTimer(); later.Interval = TimeSpan.FromSeconds(3);
            later.Tick += (o, e) =>
            {
                later.Stop();
                if (IsWebEmbedded(s)) RealApps.Close(s.WebHwnd);
                s.ShowWeb = false;
                if (PageApps.DataContext == w) Arrange(s);
            };
            later.Start();
        }

        void SetHostVisible(bool on)
        {
            if (_wfHost != null) _wfHost.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            F<StackPanel>("AppHostHint").Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        }

        const string AppHostNote = "这里显示的是官方桌面应用本身的窗口，功能一样不少。每个窗口有自己独立的数据文件夹，所以登录的是它自己的账号。";
        const string VmHostNote = "这里显示的是虚拟机的画面，里面是一整套独立的 Windows，有它自己的浏览器和账号。开机要一两分钟。键盘被虚拟机占住时，按一下键盘右边的 Ctrl 键放开。";
        readonly HashSet<string> _vmOpening = new HashSet<string>();

        // 点左侧的一个应用窗口：右边就变成它。没开就先打开，开着就直接放进来。
        void ShowApp(AppWindow w)
        {
            PageApps.DataContext = w;
            ShowPage(PageApps);
            var topBar = Win.FindName("AppTopBar") as FrameworkElement;
            if (topBar != null) topBar.Visibility = Visibility.Visible;
            var s = SlotFor(w);
            // 应用窗口一律不嵌进来：直接开成桌面上的正常窗口。
            // 嵌进来（SetParent）会让键盘输入、登录对话框、内置浏览器各种失灵，得不偿失。
            if (!w.IsVm)
            {
                SetHostVisible(false);
                var openErr = RealApps.Open(w, true);
                if (openErr != null) { F<TextBlock>("AppHostTitle").Text = openErr; return; }
                F<TextBlock>("AppHostTitle").Text = w.Label + " 已经打开";
                F<TextBlock>("AppHostText").Text = "它是桌面上的一个独立窗口（不嵌进来：这样打字、对话框、登录浏览器都跟平时一模一样）。\n登录、换账号用顶部「登录 ▾」，窗口不在最前面时点它一下就会切过去。";
                return;
            }
            Arrange(s);
            F<TextBlock>("AppHostText").Text = w.IsVm ? VmHostNote : AppHostNote;
            if (IsEmbedded(s)) { SetHostVisible(true); s.Panel.FocusChild(); return; }
            s.Hwnd = IntPtr.Zero;
            SetHostVisible(s.ShowWeb && IsWebEmbedded(s));
            if (w.IsVm)
            {
                // 启动虚拟机要好几秒，放到后台做，不卡界面；同一台不重复启动
                F<TextBlock>("AppHostTitle").Text = "正在打开虚拟机 " + w.Label + "…";
                if (_vmOpening.Add(w.Id))
                {
                    Task.Run(() => RealApps.Open(w, false)).ContinueWith(t =>
                    {
                        _vmOpening.Remove(w.Id);
                        var problem = t.IsFaulted ? "打开虚拟机 " + w.Label + " 时出错。" : t.Result;
                        if (problem == null) return;
                        Store.LogError("打开虚拟机", new Exception(problem));
                        if (PageApps.DataContext == w && _embedTarget == w)
                        {
                            if (_embedTimer != null) _embedTimer.Stop();
                            F<TextBlock>("AppHostTitle").Text = problem;
                        }
                    }, TaskScheduler.FromCurrentSynchronizationContext());
                }
            }
            else
            {
                F<TextBlock>("AppHostTitle").Text = "正在打开 " + w.Label + "…";
                var err = RealApps.Open(w, false);
                if (err != null) { F<TextBlock>("AppHostTitle").Text = err; return; }
            }
            _embedTarget = w; _embedTries = 0; _embedSeen = IntPtr.Zero; _embedRetries = 0;
            if (_embedTimer == null)
            {
                _embedTimer = new DispatcherTimer(); _embedTimer.Interval = TimeSpan.FromMilliseconds(500);
                _embedTimer.Tick += (o, e) => TryEmbed();
            }
            _embedTimer.Start();
        }

        // 等应用的窗口出现，然后把它放进来
        void TryEmbed()
        {
            var w = _embedTarget;
            if (w == null || PageApps.DataContext != w || PageApps.Visibility != Visibility.Visible) { _embedTimer.Stop(); return; }
            var h = RealApps.TopWindow(w);
            if (h == IntPtr.Zero || h != _embedSeen)
            {
                // 要连续两次看到同一个窗口才动手，避免抓到启动过程中一闪而过的窗口
                _embedSeen = h;
                // 虚拟机开机比应用慢，多等一会儿
                if (++_embedTries > (w.IsVm ? 600 : 90)) { _embedTimer.Stop(); F<TextBlock>("AppHostTitle").Text = "没能打开 " + w.Label + "，请再点一次。"; }
                return;
            }
            _embedTimer.Stop();
            var s = SlotFor(w);
            SetHostVisible(true);
            s.Panel.Visible = true;   // 先显示出来、有了大小，才能把窗口放进去
            _wfHost.UpdateLayout();
            try
            {
                s.PadW = 0; s.PadH = 0;
                if (w.IsVm) RealApps.FrameSize(h, out s.PadW, out s.PadH);
                s.OldStyle = RealApps.Embed(h, s.Panel.Handle, s.Panel.ClientSize.Width + s.PadW, s.Panel.ClientSize.Height + s.PadH);
                s.Hwnd = h;
                s.Panel.Child = h;
                RealApps.Align(h, s.Panel.Handle);
                RealApps.Remember(h);
                // 刚放进来时键盘还在 KMS 这边，过一会儿再交一次给里面的窗口
                s.Panel.FocusChild();
                var focusLater = new DispatcherTimer(); focusLater.Interval = TimeSpan.FromMilliseconds(450);
                focusLater.Tick += (o, a) =>
                {
                    focusLater.Stop();
                    try { RealApps.Align(h, s.Panel.Handle); } catch { }
                    try { s.Panel.FocusChild(); } catch { }
                };
                focusLater.Start();
            }
            catch (Exception ex) { Store.LogError("嵌入应用窗口", ex); SetHostVisible(false); F<TextBlock>("AppHostTitle").Text = "没能把 " + w.Label + " 放进来。"; }
        }

        // 把一个窗口放回桌面，变回普通的独立窗口
        void PopApp(AppWindow w, int index)
        {
            Slot s;
            if (!_slots.TryGetValue(w.Id, out s) || !IsEmbedded(s)) return;
            RealApps.Release(s.Hwnd, s.OldStyle, 90 + index * 48, 70 + index * 48, 1500, 1000);
            RealApps.Forget(s.Hwnd);
            s.Hwnd = IntPtr.Zero; s.Panel.Child = IntPtr.Zero; s.Panel.Visible = false;
            if (PageApps.DataContext == w)
            {
                SetHostVisible(false);
                F<TextBlock>("AppHostTitle").Text = w.Label + " 已放回桌面";
            }
        }

        void ReleaseAllApps()
        {
            int i = 0;
            foreach (var w in _apps.ToList()) PopApp(w, i++);
        }

        // 每秒看一眼放在 KMS 里的窗口：
        // 窗口没了（应用被关掉、虚拟机关机）就把这一格清掉；虚拟机的窗口会自己改大小，改了就拉回这一格的大小。
        void WatchSlots()
        {
            if (_slots.Count == 0) return;
            foreach (var w in _apps)
            {
                Slot s;
                if (!_slots.TryGetValue(w.Id, out s) || s.Hwnd == IntPtr.Zero) continue;
                if (!IsEmbedded(s))
                {
                    // 窗口没了，或者被应用自己从这一格里弄出去了：
                    // 还活着的话，先把它恢复成正常的独立窗口，别让它留在一个看不见的面板里
                    bool alive = RealApps.IsWindow(s.Hwnd);
                    if (alive) RealApps.Detach(s.Hwnd, s.OldStyle);
                    RealApps.Forget(s.Hwnd);
                    s.Hwnd = IntPtr.Zero; s.Panel.Child = IntPtr.Zero; s.Panel.Visible = false;
                    // 有些应用（Codex 就是这样）在启动过程中会把窗口重建一次，等于自己跑回桌面。
                    // 这种情况自动再放进来几次，不用人手点。
                    bool retry = alive && w == _embedTarget && _embedRetries < 6;
                    if (retry)
                    {
                        _embedRetries++;
                        _embedSeen = IntPtr.Zero; _embedTries = 0;
                        if (_embedTimer != null) _embedTimer.Start();
                    }
                    if (PageApps.DataContext == w && PageApps.Visibility == Visibility.Visible && !retry)
                    {
                        SetHostVisible(false);
                        F<TextBlock>("AppHostTitle").Text = alive ? w.Label + " 已经从这一格里出来了（它自己跑回桌面了），点它一下可以再放进来" : w.Label + (w.IsVm ? " 的画面窗口已关闭" : " 已关闭");
                    }
                    continue;
                }
                if (!s.Panel.Visible) continue;
                if (w.IsVm) RealApps.Fit(s.Hwnd, s.Panel.ClientSize.Width + s.PadW, s.Panel.ClientSize.Height + s.PadH);
                else RealApps.Align(s.Hwnd, s.Panel.Handle);   // 应用会按自己记的旧位置再摆一次，隔一秒贴回去
            }
        }

        // 启动参数 --open 用：直接打开“应用多开”里的一项
        public void OpenByName(string name)
        {
            var w = _apps.FirstOrDefault(x => string.Equals(x.Id, name, StringComparison.OrdinalIgnoreCase)
                                           || string.Equals(x.Label, name, StringComparison.OrdinalIgnoreCase)
                                           || (x.IsVm && string.Equals(x.VmName, name, StringComparison.OrdinalIgnoreCase)));
            if (w != null) AppList.SelectedItem = w;
        }

        void TileApps()
        {
            var src = PresentationSource.FromVisual(Win);
            double scale = src != null && src.CompositionTarget != null ? src.CompositionTarget.TransformToDevice.M11 : 1.0;
            var area = SystemParameters.WorkArea;
            int n = RealApps.Tile(_apps, (int)(area.Left * scale), (int)(area.Top * scale), (int)(area.Width * scale), (int)(area.Height * scale));
            if (n == 0) Warn("还没有已打开的应用窗口。先在左侧点一个窗口把它打开。");
        }

        void DeleteApp()
        {
            var w = PageApps.DataContext as AppWindow;
            if (w == null) return;
            if (w.IsVm)
            {
                // 虚拟机条目只是列表里的一项：移除它不动虚拟机本身
                if (MessageBox.Show(Win, "把“" + w.Label + "”从列表里移除吗？\n虚拟机本身不会被删除，也不会被关机；以后可以用“＋ 添加”再放回来。", "KMS",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                PopApp(w, 0);
                _apps.Remove(w);
                PageApps.DataContext = null;
                SaveAll();
                NavTask.IsChecked = true;
                return;
            }
            if (string.IsNullOrEmpty(w.DataDir)) { Warn("这是应用原本的那一份，不是 KMS 创建的，不能在这里删除。"); return; }
            if (RealApps.Find(w) != null) { Warn("这个窗口还开着。请先把它关掉，再删除。"); return; }
            if (MessageBox.Show(Win, "确定删除“" + w.Label + "”吗？\n它的数据文件夹会一并删除，里面的登录和聊天记录都会没有。", "KMS",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            _apps.Remove(w);
            PageApps.DataContext = null;
            SaveAll();
            NavTask.IsChecked = true;
            if (w.DataDir.StartsWith(Path.Combine(Store.DataDir, "real-apps"), StringComparison.OrdinalIgnoreCase))
            {
                try { Directory.Delete(w.DataDir, true); } catch (Exception ex) { Store.LogError("删除应用窗口的数据文件夹", ex); }
            }
        }

        void SaveAll()
        {
            _cfg.Apps = _apps.ToList();
            _cfg.Accounts = _accounts.ToList();
            _cfg.LastProjectDir = _projectDir;
            _cfg.ChiefId = _chief != null ? _chief.Id : "";
            Store.Save(_cfg);
        }

        static void Open(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
        }

        void Warn(string text) { MessageBox.Show(Win, text, "KMS", MessageBoxButton.OK, MessageBoxImage.Information); }

        static string Span(TimeSpan t)
        {
            if (t.TotalMinutes >= 1) return ((int)t.TotalMinutes) + "分钟 " + t.Seconds.ToString("00") + "秒";
            return t.Seconds + "秒";
        }

        static string OneLine(string s, int max)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length > max ? s.Substring(0, max) + "…" : s;
        }

        static string PermLabel(string perm)
        {
            return perm == "readonly" ? "只读分析" : perm == "full" ? "完全自动" : "可修改文件";
        }

        static string ShortPath(string path)
        {
            if (path.Length <= 44) return path;
            return path.Substring(0, 14) + "…" + path.Substring(path.Length - 28);
        }

        bool Browse()
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
            {
                dlg.Description = "选择要让 Agent 们工作的项目文件夹";
                if (Directory.Exists(_projectDir)) dlg.SelectedPath = _projectDir;
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return false;
                _projectDir = dlg.SelectedPath;
            }
            SaveAll(); RefreshFooter();
            return true;
        }

        // ---------- 输入框下面那一行：项目文件夹、总指挥、权限 ----------

        void RefreshFooter()
        {
            if (_chief != null && !_accounts.Contains(_chief)) _chief = _accounts.FirstOrDefault();
            if (_chief == null) _chief = _accounts.FirstOrDefault();
            bool hasDir = _projectDir.Length > 0;
            FolderBtn.Content = hasDir ? "项目文件夹：" + ShortPath(_projectDir) : "选择项目文件夹…";
            FolderBtn.Foreground = hasDir ? Look.Mid : Look.Accent;
            FolderBtn.ToolTip = hasDir ? _projectDir : "Agent 们会在这个文件夹里工作";
            ChiefBtn.Content = _chief != null ? _chief.Display : "还没有账号";
            PermBtn.Content = PermLabel(_cfg.Permission);
            EmptyHint.Text = _accounts.Count == 0 ? "还没有账号。先在左侧菜单的“账号”那里点“＋ 添加”。"
                           : !hasDir ? "先在下面选择项目文件夹，然后写下目标。"
                           : "写下目标，总指挥会把它拆成任务，派给各个账号同时去做。";
        }

        static void Popup(ContextMenu m, Button anchor)
        {
            m.PlacementTarget = anchor;
            m.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
            m.IsOpen = true;
        }

        void ChiefMenu()
        {
            var m = new ContextMenu();
            foreach (var a in _accounts.Where(x => x.Enabled))
            {
                var acc = a;
                var it = new MenuItem(); it.Header = acc.Display; it.IsChecked = acc == _chief;
                it.Click += (s, e) => { _chief = acc; SaveAll(); RefreshFooter(); };
                m.Items.Add(it);
            }
            if (m.Items.Count == 0)
            {
                var it = new MenuItem(); it.Header = "添加账号…";
                it.Click += (s, e) => AddMenu();
                m.Items.Add(it);
            }
            Popup(m, ChiefBtn);
        }

        void PermMenu()
        {
            var m = new ContextMenu();
            foreach (var p in new[] { "readonly", "edit", "full" })
            {
                var perm = p;
                var it = new MenuItem();
                it.Header = PermLabel(perm) + (perm == "readonly" ? "（不改文件）" : perm == "full" ? "（可执行命令）" : "");
                it.IsChecked = _cfg.Permission == perm;
                it.Click += (s, e) => { _cfg.Permission = perm; SaveAll(); RefreshFooter(); };
                m.Items.Add(it);
            }
            Popup(m, PermBtn);
        }

        void PlusMenu()
        {
            var m = new ContextMenu();
            var folder = new MenuItem(); folder.Header = "选择项目文件夹…"; folder.Click += (s, e) => Browse();
            var wins = new MenuItem(); wins.Header = "打开并排列 Agent 窗口";
            wins.Click += (s, e) => { foreach (var a in _accounts.Where(x => x.Enabled)) WindowFor(a, true); Tile(); };
            var open = new MenuItem(); open.Header = "在资源管理器里打开项目文件夹"; open.IsEnabled = Directory.Exists(_projectDir);
            open.Click += (s, e) => Open(_projectDir);
            m.Items.Add(folder); m.Items.Add(open); m.Items.Add(wins);
            Popup(m, PlusBtn);
        }

        // ---------- 对话 ----------

        void AddToChat(FrameworkElement el)
        {
            EmptyState.Visibility = Visibility.Collapsed;
            int at = _statusRow != null ? ChatPanel.Children.IndexOf(_statusRow) : -1;
            if (at >= 0) ChatPanel.Children.Insert(at, el); else ChatPanel.Children.Add(el);
            Win.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => ChatScroll.ScrollToEnd()));
        }

        static FrameworkElement UserBubble(string text, bool queued)
        {
            var b = new Border();
            b.Background = Look.Bubble; b.CornerRadius = new CornerRadius(16); b.Padding = new Thickness(15, 9, 15, 10);
            b.HorizontalAlignment = HorizontalAlignment.Right; b.MaxWidth = 560;
            var t = new TextBlock(); t.Text = text; t.TextWrapping = TextWrapping.Wrap; t.FontSize = Look.Body; t.LineHeight = Look.LineH; t.Foreground = Look.Ink;
            b.Child = t;
            var wrap = new StackPanel(); wrap.Margin = new Thickness(0, 12, 0, 12);
            wrap.Children.Add(b);
            if (queued)
            {
                b.Opacity = 0.6;
                var note = Look.Text("已加入队列，当前任务结束后发送", Look.Grey, 12.5);
                note.HorizontalAlignment = HorizontalAlignment.Right; note.Margin = new Thickness(0, 4, 4, 0);
                wrap.Children.Add(note);
            }
            return wrap;
        }

        static FrameworkElement Assistant(string text)
        {
            var el = Md.Render(text);
            el.Margin = new Thickness(0, 8, 0, 8);
            var menu = new ContextMenu();
            var copy = new MenuItem(); copy.Header = "复制这段文字";
            copy.Click += (s, e) => { try { Clipboard.SetText(text ?? ""); } catch { } };
            menu.Items.Add(copy);
            el.ContextMenu = menu;
            return el;
        }

        SavedChat _chat = ChatStore.New();   // 总指挥页当前这段对话，每一轮结束后存盘

        // 把一轮的结果摆到对话里：真实跑完时用，重新打开恢复时也用
        void ShowRun(SavedRun run, List<TaskItem> tasks)
        {
            AddToChat(UserBubble(run.Goal, false));
            _card = null;
            foreach (var t in tasks) OnTaskAdded(t);
            AddToChat(Assistant(run.Answer ?? ""));
            AddToChat(Links(run.ReportFile, run.RunDir, run.ProjectDir));
        }

        // 重新打开时：把上次的对话原样恢复出来
        void RestoreChat()
        {
            var saved = ChatStore.LoadLatest();
            if (saved == null) return;
            _chat = saved;
            foreach (var run in saved.Runs)
            {
                var tasks = new List<TaskItem>();
                foreach (var st in run.Tasks) tasks.Add(ChatStore.ToTask(st));
                ShowRun(run, tasks);
                _history.Add("用户：" + run.Goal + "\n总指挥：" + OneLine(run.Answer, 1200));
            }
            _card = null;
        }

        FrameworkElement Links(Commander cmd) { return Links(cmd.ReportFile, cmd.RunDir, cmd.ProjectDir); }

        FrameworkElement Links(string reportFile, string runDir, string projectDir)
        {
            var row = new StackPanel(); row.Orientation = Orientation.Horizontal; row.Margin = new Thickness(0, 4, 0, 10);
            Action<string, string> add = (label, path) =>
            {
                var t = Look.Text(label + " ›", Look.Grey, Look.Body);
                t.Cursor = Cursors.Hand; t.Margin = new Thickness(0, 0, 22, 0);
                t.MouseLeftButtonUp += (s, e) => Open(path);
                row.Children.Add(t);
            };
            if (!string.IsNullOrEmpty(reportFile) && File.Exists(reportFile)) add("查看报告文件", reportFile);
            if (!string.IsNullOrEmpty(runDir) && Directory.Exists(runDir)) add("打开结果文件夹", runDir);
            if (!string.IsNullOrEmpty(projectDir) && Directory.Exists(projectDir)) add("打开项目文件夹", projectDir);
            return row;
        }

        void OnTaskAdded(TaskItem t)
        {
            if (t.Kind != "task") { AddToChat(new TaskRow(t, true).Root); return; }
            if (_card == null)
            {
                _card = new StackPanel();
                var box = new Border();
                box.BorderBrush = Look.Line; box.BorderThickness = new Thickness(1); box.CornerRadius = new CornerRadius(12);
                box.Padding = new Thickness(15, 10, 16, 10); box.Margin = new Thickness(-3, 6, -3, 8);
                box.Child = _card;
                AddToChat(box);
            }
            _card.Children.Add(new TaskRow(t, false).Root);
        }

        void SetRunning(bool on)
        {
            _running = on;
            SendBtn.Content = on ? Icons.Stop() : Icons.Send();
            SendBtn.ToolTip = on ? "停止" : "发送";
            InputHint.Text = on ? "按 Enter 加入队列，当前任务结束后自动发送" : "给总指挥下达目标，按 Enter 发送，按 Shift+Enter 换行";
            Ring.Stroke = on ? Look.Accent : Look.Ring;
            if (on)
            {
                _runStart = DateTime.Now;
                var row = new StackPanel(); row.Orientation = Orientation.Horizontal; row.Margin = new Thickness(0, 14, 0, 8);
                row.Children.Add(Icons.Dots());
                _statusLabel = Look.Text("", Look.Mid, Look.Body); _statusLabel.Margin = new Thickness(10, 0, 0, 0);
                _statusText = Look.Text("", Look.Ink, Look.Body); _statusText.Margin = new Thickness(8, 0, 0, 0);
                _statusTime = Look.Text("", Look.Grey, Look.Body); _statusTime.Margin = new Thickness(10, 0, 0, 0);
                row.Children.Add(_statusLabel); row.Children.Add(_statusText); row.Children.Add(_statusTime);
                _statusRow = row;
                ChatPanel.Children.Add(row);
                SetStatus("准备中：正在开始");
            }
            else if (_statusRow != null)
            {
                ChatPanel.Children.Remove(_statusRow);
                _statusRow = null; _statusLabel = null; _statusText = null; _statusTime = null;
            }
        }

        // 状态行：冒号前是灰色的阶段名，冒号后是黑色的具体内容
        void SetStatus(string s)
        {
            if (_statusText == null) return;
            s = (s ?? "").TrimEnd('…', '。');
            int i = s.IndexOf('：');
            _statusLabel.Text = i > 0 ? s.Substring(0, i) : "进行中";
            _statusText.Text = i > 0 ? s.Substring(i + 1) : s;
        }

        void OnInputKey(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter && e.Key != Key.Return) return;
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;   // Shift+Enter 换行
            e.Handled = true;
            Send();
        }

        void StopRun()
        {
            if (_cmd == null || !_running) return;
            _stopped = true;
            _cmd.Stop();
            SetStatus("停止中：正在结束各个 Agent");
        }

        void NewChat()
        {
            if (_running) { Warn("任务还在运行。先点输入框右边的停止，或等它结束。"); return; }
            ChatPanel.Children.Clear();
            _history.Clear(); _queue.Clear(); _card = null;
            _chat = ChatStore.New();   // 之前那段对话仍然留在 data\commander 里
            EmptyState.Visibility = Visibility.Visible;
            NavTask.IsChecked = true;
            RefreshFooter();
            InputBox.Focus();
        }

        // 发送前的检查。不满足时说明原因，输入框里的文字保留。
        bool Ready()
        {
            if (_busy) { Warn("正在测试账号连接，请等它结束后再发送。"); return false; }
            RefreshFooter();
            if (_chief == null) { Warn("还没有账号。请在左侧菜单的“账号”那里点“＋ 添加”，添加并登录至少一个账号。"); return false; }
            if (!_chief.Enabled) { Warn("担任总指挥的账号已停用，请在输入框下面换一个。"); return false; }
            if (Cli.Find(_chief.Agent, _cfg) == null) { Warn("没有找到 " + _chief.AgentName + " 的命令行程序，请先安装或在“设置”里填写路径。"); return false; }
            if (!Directory.Exists(_projectDir) && !Browse()) return false;
            if (_cfg.Permission == "full" &&
                MessageBox.Show(Win, "“完全自动”会让 Agent 不经确认就在你的电脑上执行命令、修改文件。\n只建议用在有备份的项目上。确定继续吗？",
                    "KMS", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return false;
            return true;
        }

        void Send()
        {
            var text = InputBox.Text.Trim();
            if (text.Length == 0) return;
            if (_running)
            {
                InputBox.Clear();
                var bubble = UserBubble(text, true);
                AddToChat(bubble);
                _queue.Enqueue(new KeyValuePair<string, FrameworkElement>(text, bubble));
                return;
            }
            if (!Ready()) return;
            InputBox.Clear();
            RunGoal(text);
        }

        async void RunGoal(string goal)
        {
            try { await RunGoalAsync(goal); }
            catch (Exception ex) { Store.LogError("RunGoal", ex); if (_running) SetRunning(false); Warn("运行出错：" + ex.Message); }
        }

        async Task RunGoalAsync(string goal)
        {
            while (goal != null)
            {
                SaveAll();
                AddToChat(UserBubble(goal, false));
                var cmd = new Commander(_cfg, _accounts);
                cmd.Goal = goal; cmd.ProjectDir = _projectDir; cmd.Perm = _cfg.Permission; cmd.Review = _cfg.FinalReview; cmd.Chief = _chief;
                cmd.Context = string.Join("\n\n", _history.Skip(Math.Max(0, _history.Count - 4)).ToArray());
                cmd.Post = a => Win.Dispatcher.BeginInvoke(a);
                cmd.Status += s => SetStatus(s);
                cmd.TaskStarted += (acc, t) =>
                {
                    if (_cfg.AutoOpenWindows && (!_wins.ContainsKey(acc.Id) || !_wins[acc.Id].Win.IsVisible)) { WindowFor(acc, true); Tile(); }
                };
                cmd.Tasks.CollectionChanged += (s, e) =>
                {
                    if (e.NewItems == null) return;
                    foreach (TaskItem t in e.NewItems) OnTaskAdded(t);
                };
                _cmd = cmd; _card = null; _stopped = false;
                SetRunning(true);
                try { await cmd.Start(); }
                finally { SetRunning(false); }

                string answer = !string.IsNullOrEmpty(cmd.Report) ? cmd.Report : (cmd.LastStatus ?? "已结束。");
                AddToChat(Assistant(answer));
                AddToChat(Links(cmd));
                _history.Add("用户：" + goal + "\n总指挥：" + OneLine(answer, 1200));
                // 存盘：下次打开时这一轮还在
                var record = new SavedRun();
                record.Goal = goal; record.Answer = answer; record.ProjectDir = cmd.ProjectDir; record.RunDir = cmd.RunDir; record.ReportFile = cmd.ReportFile;
                record.When = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                foreach (var t in cmd.Tasks) record.Tasks.Add(ChatStore.From(t));
                _chat.Runs.Add(record);
                ChatStore.Save(_chat);

                goal = null;
                if (_queue.Count == 0) continue;
                if (_stopped)
                {
                    // 用户点了停止：排队的内容不自动发，放回输入框由用户决定
                    var rest = new List<string>();
                    while (_queue.Count > 0) { var q = _queue.Dequeue(); ChatPanel.Children.Remove(q.Value); rest.Add(q.Key); }
                    InputBox.Text = string.Join("\n", rest.ToArray());
                    InputBox.CaretIndex = InputBox.Text.Length;
                }
                else
                {
                    var next = _queue.Dequeue();
                    ChatPanel.Children.Remove(next.Value);
                    goal = next.Key;
                }
            }
            InputBox.Focus();
        }

        // ---------- Agent 窗口 ----------

        AgentWindow WindowFor(Account acc, bool show)
        {
            AgentWindow w;
            if (!_wins.TryGetValue(acc.Id, out w))
            {
                w = new AgentWindow(acc, this);
                _wins[acc.Id] = w;
            }
            if (show) { w.Win.Show(); if (w.Win.WindowState == WindowState.Minimized) w.Win.WindowState = WindowState.Normal; }
            return w;
        }

        // 把已打开的 Agent 窗口排成网格。屏幕够宽时主窗口靠左、Agent 窗口占右边，互不遮挡。
        void Tile()
        {
            var open = _wins.Values.Where(w => w.Win.IsVisible).ToList();
            if (open.Count == 0) return;
            var area = SystemParameters.WorkArea;
            double left = area.Left, width = area.Width;
            bool docked = area.Width >= 1500;
            if (docked)
            {
                double mainW = Math.Max(Win.MinWidth, Math.Floor(area.Width * 0.56));
                Win.WindowState = WindowState.Normal;
                Win.Left = area.Left; Win.Top = area.Top; Win.Width = mainW; Win.Height = area.Height;
                left = area.Left + mainW; width = area.Width - mainW;
            }
            int cols = width < 900 ? (open.Count <= 3 ? 1 : 2) : (int)Math.Ceiling(Math.Sqrt(open.Count));
            int rows = (int)Math.Ceiling(open.Count / (double)cols);
            double w0 = Math.Floor(width / cols), h0 = Math.Floor(area.Height / rows);
            for (int i = 0; i < open.Count; i++)
            {
                var win = open[i].Win;
                win.WindowState = WindowState.Normal;
                win.Left = left + (i % cols) * w0; win.Top = area.Top + (i / cols) * h0;
                win.Width = w0; win.Height = h0;
            }
            if (!docked) Win.Activate();
        }

        // ---------- 账号 ----------

        // 右键落在哪个窗口上
        static AppWindow AppAt(DependencyObject d)
        {
            while (d != null && !(d is ListBoxItem)) d = System.Windows.Media.VisualTreeHelper.GetParent(d);
            var item = d as ListBoxItem;
            return item == null ? null : item.DataContext as AppWindow;
        }

        ContextMenu AppMenu()
        {
            var m = new ContextMenu();
            var ren = new MenuItem(); ren.Header = "重命名…";
            ren.Click += (s, e) => RenameApp(AppList.SelectedItem as AppWindow);
            m.Items.Add(ren);
            var open = new MenuItem(); open.Header = "打开并放进这里";
            open.Click += (s, e) => { var w = AppList.SelectedItem as AppWindow; if (w != null) ShowApp(w); };
            m.Items.Add(open);
            var login = new MenuItem(); login.Header = "登录…";
            login.Click += (s, e) => { var w = AppList.SelectedItem as AppWindow; if (w == null) return; PageApps.DataContext = w; LoginMenu(); };
            m.Items.Add(login);
            var outAcc = new MenuItem(); outAcc.Header = "退出登录 / 换个账号…";
            outAcc.Click += (s, e) => SignOutWindow(AppList.SelectedItem as AppWindow);
            m.Items.Add(outAcc);
            var dataDir = new MenuItem(); dataDir.Header = "打开它的数据文件夹";
            dataDir.Click += (s, e) =>
            {
                var w = AppList.SelectedItem as AppWindow; if (w == null) return;
                var d = string.IsNullOrEmpty(w.DataDir) ? Path.Combine(Store.DataDir, "real-apps") : w.DataDir;
                if (Directory.Exists(d)) Open(d); else Warn("这个窗口还没有数据文件夹。");
            };
            m.Items.Add(dataDir);
            var pop = new MenuItem(); pop.Header = "放回桌面";
            pop.Click += (s, e) => { var w = AppList.SelectedItem as AppWindow; if (w != null) PopApp(w, 0); };
            m.Items.Add(pop);
            m.Items.Add(new Separator());
            var del = new MenuItem(); del.Header = "删除这个窗口";
            del.Click += (s, e) => { var w = AppList.SelectedItem as AppWindow; if (w == null) return; PageApps.DataContext = w; DeleteApp(); };
            m.Items.Add(del);
            return m;
        }

        void RenameApp(AppWindow w)
        {
            if (w == null) return;
            var name = Prompt("给这个窗口起个名字，方便区分是哪个账号。", w.Label);
            if (name == null) return;
            if (name.Length == 0) { Warn("名字不能是空的。"); return; }
            w.Label = name;
            SaveAll();
        }

        // 退出登录 / 换一个账号：关掉这个窗口，把它的登录数据挪到一边（不删除），再开一个没登录的
        void SignOutWindow(AppWindow w)
        {
            if (w == null) return;
            if (string.IsNullOrEmpty(w.DataDir))
            {
                Warn("这一条是你平时用的原窗口，KMS 不动它的登录。\n要换账号，直接在应用自己的头像菜单里退出登录即可。");
                return;
            }
            if (_running) { Warn("有任务正在运行，等它结束后再换账号。"); return; }
            var ask = "给“" + w.Label + "”换一个账号吗？\n\n"
                    + "1. 先把这个窗口关掉\n"
                    + "2. 现在这份登录数据改个名字放到一边（不删除，之后还能找回）\n"
                    + "3. 重新打开一个没登录的窗口，用你要用的账号登录\n\n"
                    + "要继续吗？";
            if (MessageBox.Show(Win, ask, "KMS", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            var s = SlotFor(w);
            IntPtr hwnd = s.Hwnd;
            var proc = RealApps.Find(w);
            if (proc != null)
            {
                ShowAppNotice(w, "正在关掉“" + w.Label + "”…");
                try
                {
                    var target = hwnd != IntPtr.Zero ? hwnd : proc.MainWindowHandle;
                    RealApps.Close(target);
                }
                catch { }
                Cursor old = Win.Cursor;
                try
                {
                    Win.Cursor = Cursors.Wait;
                    for (int i = 0; i < 40 && RealApps.Find(w) != null; i++) System.Threading.Thread.Sleep(250);
                }
                finally { Win.Cursor = old; }
                if (RealApps.Find(w) != null)
                {
                    Warn("“" + w.Label + "”没有关掉（可能最小化到了托盘，或弹了确认框）。\n请把这个应用完全退出（托盘图标也要右键退出），再点一次“退出登录 / 换个账号”。");
                    return;
                }
            }
            if (hwnd != IntPtr.Zero) RealApps.Forget(hwnd);
            s.Hwnd = IntPtr.Zero; s.Panel.Child = IntPtr.Zero; s.Panel.Visible = false;
            SetHostVisible(false);

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            bool movedAll = true;
            foreach (var dir in new[] { w.DataDir, Path.Combine(Store.DataDir, "real-apps", w.Id + "-browser") })
            {
                if (!Directory.Exists(dir)) continue;
                try { Directory.Move(dir, dir + ".logged-out-" + stamp); }
                catch (Exception ex)
                {
                    movedAll = false;
                    Store.LogError("挪开登录数据", ex);
                    Warn("有一份数据没能挪开（可能还被占用）：\n" + dir + "\n" + ex.Message);
                }
            }

            ShowApp(w);   // 重新打开：应该是未登录状态
            ShowAppNotice(w, movedAll
                ? "已经把登录数据挪到一边了。现在用你要用的账号登录；旧数据在 data\\real-apps 里带 .logged-out-" + stamp + " 的文件夹。"
                : "换账号没走完：有一份数据没挪开，看上面的提示。");
        }

        // ---------- 左侧账号列表的右键菜单 ----------

        // 右键落在哪个账号上
        static Account AccountAt(DependencyObject d)
        {
            while (d != null && !(d is ListBoxItem)) d = System.Windows.Media.VisualTreeHelper.GetParent(d);
            var item = d as ListBoxItem;
            return item == null ? null : item.DataContext as Account;
        }

        ContextMenu AccountMenu()
        {
            var m = new ContextMenu();
            var ren = new MenuItem(); ren.Header = "重命名…";
            ren.Click += (s, e) => RenameAccount(AccountList.SelectedItem as Account);
            m.Items.Add(ren);
            var win = new MenuItem(); win.Header = "打开它的独立窗口";
            win.Click += (s, e) => { var a = AccountList.SelectedItem as Account; if (a != null) WindowFor(a, true); };
            m.Items.Add(win);
            var set = new MenuItem(); set.Header = "账号设置";
            set.Click += (s, e) => { var a = AccountList.SelectedItem as Account; if (a != null) ShowAccount(a); };
            m.Items.Add(set);
            var dir = new MenuItem(); dir.Header = "打开它的登录位置";
            dir.Click += (s, e) =>
            {
                var a = AccountList.SelectedItem as Account; if (a == null) return;
                var path = a.UseDefaultLogin ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : a.ProfileDir;
                if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) { Warn("这个账号还没有登录位置（可能还没登录过）。"); return; }
                Open(path);
            };
            m.Items.Add(dir);
            m.Items.Add(new Separator());
            var en = new MenuItem(); en.Header = "启用 / 停用";
            en.Click += (s, e) => { var a = AccountList.SelectedItem as Account; if (a != null) { a.Enabled = !a.Enabled; SaveAll(); } };
            m.Items.Add(en);
            var del = new MenuItem(); del.Header = "删除这个账号";
            del.Click += (s, e) => DeleteAccount(AccountList.SelectedItem as Account);
            m.Items.Add(del);
            return m;
        }

        void RenameAccount(Account a)
        {
            if (a == null) return;
            var name = Prompt("给“" + a.Display + "”起个名字。只影响显示，不影响登录。", a.Label);
            if (name == null) return;
            if (name.Length == 0) { Warn("名字不能是空的。"); return; }
            a.Label = name;
            SaveAll();
        }

        // 一个简单的输入框：点“确定”返回输入的文字，点“取消”返回 null
        string Prompt(string hint, string initial)
        {
            var dlg = new Window();
            dlg.Title = "KMS";
            dlg.Owner = Win;
            dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            dlg.ResizeMode = ResizeMode.NoResize;
            dlg.SizeToContent = SizeToContent.Height;
            dlg.Width = 430;
            dlg.Background = Brushes.White;
            dlg.ShowInTaskbar = false;
            var sp = new StackPanel(); sp.Margin = new Thickness(20, 18, 20, 16);
            sp.Children.Add(Look.Text(hint, Look.Mid, 13));
            var box = new TextBox();
            box.Text = initial ?? "";
            box.FontSize = 15; box.Padding = new Thickness(6, 5, 6, 5); box.Margin = new Thickness(0, 10, 0, 16);
            sp.Children.Add(box);
            var row = new StackPanel(); row.Orientation = Orientation.Horizontal; row.HorizontalAlignment = HorizontalAlignment.Right;
            var ok = new Button(); ok.Content = "确定"; ok.Width = 88; ok.Height = 32; ok.IsDefault = true; ok.Margin = new Thickness(0, 0, 8, 0);
            var cancel = new Button(); cancel.Content = "取消"; cancel.Width = 88; cancel.Height = 32; cancel.IsCancel = true;
            try
            {
                var primary = Win.TryFindResource("Primary") as Style; if (primary != null) ok.Style = primary;
                var flat = Win.TryFindResource("Btn") as Style; if (flat != null) cancel.Style = flat;
            }
            catch { }
            ok.Click += (s, e) => { dlg.DialogResult = true; };
            row.Children.Add(ok); row.Children.Add(cancel);
            sp.Children.Add(row);
            dlg.Content = sp;
            dlg.Loaded += (s, e) => { box.Focus(); box.SelectAll(); };
            return dlg.ShowDialog() == true ? box.Text.Trim() : null;
        }

        Account Selected()
        {
            var a = PageAccounts.DataContext as Account;
            if (a == null) Warn("请先在左侧菜单里点一个账号。");
            return a;
        }

        void AddMenu()
        {
            var m = new ContextMenu();
            foreach (var agent in Cli.Agents)
            {
                var ag = agent;
                var it = new MenuItem(); it.Header = Cli.DisplayName(ag) + " 账号";
                it.Click += (s, e) => AddAccount(ag);
                m.Items.Add(it);
            }
            m.PlacementTarget = F<Button>("AddAccountBtn");
            m.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            m.IsOpen = true;
        }

        // 点左侧账号：主窗口右边变成这个 Agent 的对话界面，可以看它一步一步在做什么，也可以直接给它发消息
        void ShowAgent(Account a)
        {
            NavTask.IsChecked = false; NavSettings.IsChecked = false;
            AgentView view;
            if (!_views.TryGetValue(a.Id, out view))
            {
                view = new AgentView(a, this, true);
                var acc = a;
                view.SettingsClicked += () => ShowAccount(acc);
                view.PopOutClicked += () => WindowFor(acc, true);
                _views[a.Id] = view;
            }
            PageAgent.Children.Clear();
            PageAgent.Children.Add(view.Root);
            ShowPage(PageAgent);
            if (a.LoginState == "unknown") RefreshLogin(a);
            view.FocusInput();
        }

        // 账号设置页：登录的是谁、用什么模型、最近一次真实运行的结果
        async void ShowAccount(Account a)
        {
            NavTask.IsChecked = false; NavSettings.IsChecked = false;
            ShowPage(PageAccounts);
            _loadingAccount = true;
            PageAccounts.DataContext = a;
            ModelBox.ItemsSource = null;
            EffortBox.ItemsSource = new[] { DefaultChoice };
            EffortBox.SelectedIndex = 0;
            ModelHint.Text = "正在读取可选的模型…";
            F<TextBlock>("NetText").Text = Cli.NetworkNote();
            _loadingAccount = false;
            RefreshLogin(a);

            List<ModelInfo> models;
            try { models = await Cli.Models(_cfg, a); }
            catch (Exception ex) { Store.LogError("读取模型", ex); models = new List<ModelInfo>(); }
            if (PageAccounts.DataContext != a) return;   // 等待期间已经切到别的账号
            _loadingAccount = true;
            string keep = a.Model;
            // 以前存的是 fable / opus 这类别名：换成列表里对应的具体模型名
            var byAlias = models.FirstOrDefault(m => keep.Length > 0 && m.Id.StartsWith("claude-" + keep + "-", StringComparison.OrdinalIgnoreCase));
            if (byAlias != null) keep = byAlias.Id;
            ModelBox.ItemsSource = models;
            ModelBox.Text = keep;
            a.Model = keep;
            var efforts = new List<string>(); efforts.Add(DefaultChoice);
            foreach (var m in models) foreach (var e in m.Efforts) if (!efforts.Contains(e)) efforts.Add(e);
            if (a.Effort.Length > 0 && !efforts.Contains(a.Effort)) efforts.Add(a.Effort);
            EffortBox.ItemsSource = efforts;
            EffortBox.SelectedItem = a.Effort.Length > 0 ? a.Effort : DefaultChoice;
            EffortBox.IsEnabled = efforts.Count > 1;
            ModelHint.Text = a.Agent == "gemini" ? "留空用默认模型，也可以直接填模型名"
                           : models.Count > 0 ? "留空用该工具自己的默认模型，也可以从列表里选"
                           : "没读到模型列表，可以留空或直接填模型名";
            _loadingAccount = false;
        }

        async void RefreshLogin(Account a)
        {
            if (a.LoginState == "checking") return;
            a.LoginState = "checking";
            LoginInfo info;
            try { info = await Cli.Status(_cfg, a); }
            catch (Exception ex) { Store.LogError("查询登录状态", ex); info = new LoginInfo(); }
            a.Identity = info.Identity; a.Plan = info.Plan; a.LoginState = info.State;
            if (info.State == "in") _awaitLogin.Remove(a);
        }

        async void RefreshAllLogins()
        {
            foreach (var a in _accounts.ToList())
            {
                if (a.LoginState == "checking") continue;
                a.LoginState = "checking";
                LoginInfo info;
                try { info = await Cli.Status(_cfg, a); }
                catch { info = new LoginInfo(); }
                a.Identity = info.Identity; a.Plan = info.Plan; a.LoginState = info.State;
            }
        }

        void AddAccount(string agent)
        {
            var a = Defaults.NewAccount(_cfg, _accounts, agent);
            _accounts.Add(a);
            if (_chief == null) _chief = a;
            SaveAll(); RefreshFooter();
            AccountList.SelectedItem = a;
            ShowAccount(a);   // 新账号先到设置页，方便登录
            if (Cli.Find(agent, _cfg) == null)
                Warn("已添加，但电脑上还没有 " + a.AgentName + " 的命令行程序。安装后（或在“设置”里填好路径）再点“登录”。");
            else if (a.UseDefaultLogin)
                Warn("已添加，并沿用这台电脑上 " + a.AgentName + " 命令行现有的登录。点“测试连接”确认能用。");
            else
                Warn("已添加。这个账号有自己独立的登录位置，请点“登录”，在弹出的窗口里用你想用的那个账号登录。登录完回到这里，会自动显示登录的是哪个账号。");
        }

        void Login()
        {
            var a = Selected(); if (a == null) return;
            var err = Cli.OpenLogin(_cfg, a);
            if (err != null) { Warn(err); return; }
            _awaitLogin.Add(a);
            a.LastTest = "已打开登录窗口。登录完成后回到这里会自动刷新；没刷新就点“刷新状态”。";
        }

        async void LogoutAccount()
        {
            var a = Selected(); if (a == null) return;
            if (_running || _busy) { Warn("有任务正在运行，等它结束后再退出登录。"); return; }
            string where = a.UseDefaultLogin ? "\n注意：这个账号沿用的是这台电脑上 " + a.AgentName + " 原有的登录，退出后别处用它的地方也要重新登录。" : "";
            if (MessageBox.Show(Win, "确定让“" + a.Display + "”退出登录吗？" + where, "KMS", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            var err = await Cli.Logout(_cfg, a);
            if (err != null) { Warn(err); return; }
            a.LastTest = "已退出登录。";
            RefreshLogin(a);
        }

        async void TestAccount()
        {
            var a = Selected(); if (a == null) return;
            if (_busy || _running) { Warn("有任务正在运行，等它结束后再测试。"); return; }
            _busy = true;
            a.LastTest = "测试中，正在真实调用模型…";
            var sw = Stopwatch.StartNew();
            try
            {
                a.AddStep(new Step("user", "连接测试：" + Defaults.TestPrompt));
                a.RunStart = DateTime.Now;
                // 用普通输出模式：这样 Codex 会自己报告它实际用的模型和推理强度
                var r = await Cli.Run(_cfg, a, Defaults.TestDir(), Defaults.TestPrompt, "readonly",
                    s => Win.Dispatcher.BeginInvoke(new Action(() => a.AddStep(s))), CancellationToken.None, null, true);
                var shown = new TaskCompletionSource<bool>();
                Post(() => shown.TrySetResult(true));
                await shown.Task;
                a.AddStep(r.Ok ? new Step("say", r.Text) : new Step("error", r.Error));
                var secs = (sw.ElapsedMilliseconds / 1000.0).ToString("0.0") + " 秒";
                a.NoteRun(r);
                string when = DateTime.Now.ToString("HH:mm:ss");
                a.LastTest = r.Ok
                    ? "✔ " + when + " 测试通过，用时 " + secs + "\n模型回复：" + OneLine(r.Text, 80)
                      + "\n实际模型：" + (r.Model.Length > 0 ? r.Model : "命令行没有报告") + "　用量：" + (r.Usage.Length > 0 ? r.Usage : "命令行没有报告")
                    : "✖ " + when + " 测试失败：" + OneLine(r.Error, 240);
            }
            catch (Exception ex) { a.LastTest = "✖ 失败：" + ex.Message; }
            finally { _busy = false; }
            RefreshLogin(a);
        }

        void DeleteAccount() { var a = Selected(); if (a != null) DeleteAccount(a); }

        void DeleteAccount(Account a)
        {
            if (a == null) return;
            if (_running) { Warn("任务运行期间不能删除账号。"); return; }
            bool ownDir = !a.UseDefaultLogin && !string.IsNullOrEmpty(a.ProfileDir)
                          && a.ProfileDir.StartsWith(Path.Combine(Store.DataDir, "profiles"), StringComparison.OrdinalIgnoreCase);
            var msg = "确定删除“" + a.Display + "”吗？" + (ownDir ? "\n它独立的登录目录也会一并删除，之后要再用需要重新登录。" : "\n这台电脑上该工具原有的登录不受影响。");
            if (MessageBox.Show(Win, msg, "KMS", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            AgentWindow w;
            if (_wins.TryGetValue(a.Id, out w)) { w.Closing = true; w.Win.Close(); _wins.Remove(a.Id); }
            _awaitLogin.Remove(a);
            AgentView view;
            if (_views.TryGetValue(a.Id, out view)) { view.Dispose(); _views.Remove(a.Id); PageAgent.Children.Clear(); }
            _accounts.Remove(a);
            if (_chief == a) _chief = _accounts.FirstOrDefault();
            PageAccounts.DataContext = null;
            SaveAll();
            NavTask.IsChecked = true;   // 回到对话页
            if (ownDir) { try { Directory.Delete(a.ProfileDir, true); } catch (Exception ex) { Store.LogError("删除登录目录", ex); } }
        }

        // ---------- 自检用：把窗口画面存成图片 ----------

        static void Render(FrameworkElement target, string file, double scale)
        {
            target.UpdateLayout();
            var bmp = new RenderTargetBitmap((int)(target.ActualWidth * scale), (int)(target.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bmp.Render(target);
            var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(file)) enc.Save(fs);
        }

        // 自检用：在真实界面里发送一条目标，跑完后把主窗口和每个 Agent 窗口的画面存下来
        public async void AutoRun(string dir, string shotPrefix, string goal)
        {
            int code = 1;
            try
            {
                await Task.Delay(600);
                _projectDir = dir; RefreshFooter();
                await RunGoalAsync(goal);
                await Task.Delay(900);
                Render((FrameworkElement)Win.Content, shotPrefix + "-main.png", 1.5);
                int i = 0;
                foreach (var w in _wins.Values.Where(x => x.Win.IsVisible)) Render((FrameworkElement)w.Win.Content, shotPrefix + "-agent" + (++i) + ".png", 1);
                var info = new StringBuilder();
                info.AppendLine("status: " + (_cmd != null ? _cmd.LastStatus : ""));
                info.AppendLine("agent windows visible: " + i);
                foreach (var w in _wins.Values) info.AppendLine("window: " + w.Win.Title + " at " + (int)w.Win.Left + "," + (int)w.Win.Top + " size " + (int)w.Win.Width + "x" + (int)w.Win.Height);
                info.AppendLine("main at " + (int)Win.Left + "," + (int)Win.Top + " size " + (int)Win.Width + "x" + (int)Win.Height);
                if (_cmd != null) foreach (var t in _cmd.Tasks) info.AppendLine("task " + t.Id + " [" + t.State + "] " + t.Title + " | " + t.AccountLabel + " | " + t.Elapsed);
                File.WriteAllText(shotPrefix + "-info.txt", info.ToString(), new UTF8Encoding(true));
                code = _cmd != null && _cmd.Tasks.Count > 1 && _cmd.Tasks.All(t => t.State == "done") ? 0 : 1;
            }
            catch (Exception ex) { Store.LogError("AutoRun", ex); }
            Application.Current.Shutdown(code);
        }

        // 自检用：真实地把一个应用窗口嵌进 KMS，截下 KMS 窗口在屏幕上的样子，再把它放回桌面
        public async void AutoEmbed(string appId, string file)
        {
            var note = new StringBuilder();
            try
            {
                await Task.Delay(1200);
                var w = _apps.FirstOrDefault(x => x.Id == appId);
                note.AppendLine("app found in list: " + (w != null));
                Win.Topmost = true; Win.Activate();
                AppList.SelectedItem = w;
                for (int i = 0; i < 60; i++)
                {
                    await Task.Delay(500);
                    Slot s0;
                    if (w != null && _slots.TryGetValue(w.Id, out s0) && IsEmbedded(s0)) break;
                }
                await Task.Delay(2500);
                Slot s;
                bool embedded = w != null && _slots.TryGetValue(w.Id, out s) && IsEmbedded(s);
                note.AppendLine("embedded: " + embedded);
                var src = PresentationSource.FromVisual(Win);
                double sc = src != null && src.CompositionTarget != null ? src.CompositionTarget.TransformToDevice.M11 : 1.0;
                int cw = (int)(Win.ActualWidth * sc), ch = (int)(Win.ActualHeight * sc);
                using (var bmp = new System.Drawing.Bitmap(cw, ch))
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen((int)(Win.Left * sc), (int)(Win.Top * sc), 0, 0, new System.Drawing.Size(cw, ch));
                    bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
                }
                note.AppendLine("captured " + cw + "x" + ch);
                Win.Topmost = false;
                ReleaseAllApps();
                await Task.Delay(800);
                note.AppendLine("released; app still running: " + (w != null && RealApps.Find(w) != null));
            }
            catch (Exception ex) { note.AppendLine("ERROR " + ex); Store.LogError("AutoEmbed", ex); }
            try { File.WriteAllText(file + ".txt", note.ToString(), new UTF8Encoding(true)); } catch { }
            Application.Current.Shutdown(0);
        }

        void ScreenShot(string file)
        {
            var src = PresentationSource.FromVisual(Win);
            double sc = src != null && src.CompositionTarget != null ? src.CompositionTarget.TransformToDevice.M11 : 1.0;
            int cw = (int)(Win.ActualWidth * sc), ch = (int)(Win.ActualHeight * sc);
            using (var bmp = new System.Drawing.Bitmap(cw, ch))
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.CopyFromScreen((int)(Win.Left * sc), (int)(Win.Top * sc), 0, 0, new System.Drawing.Size(cw, ch));
                bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        // 自检用：真实地把一台虚拟机的窗口放进 KMS，截下 KMS 窗口在屏幕上的样子。
        // mode 里带 type：在画面中间点一下，确认键盘输入确实落在虚拟机上之后敲 “ver” 和回车，再截一张。
        // mode 里带 hold：不放回桌面、不退出（用来检查 KMS 被强行结束时虚拟机会怎样）。
        public async void AutoEmbedVm(string vmName, string file, string mode)
        {
            var note = new StringBuilder();
            bool hold = mode.IndexOf("hold", StringComparison.OrdinalIgnoreCase) >= 0;
            try
            {
                await Task.Delay(1200);
                var w = _apps.FirstOrDefault(x => x.IsVm && string.Equals(x.VmName, vmName, StringComparison.OrdinalIgnoreCase));
                if (w == null) { w = NewVm(vmName); _apps.Add(w); }
                note.AppendLine("vm running before: " + Vms.IsRunningNow(vmName));
                Win.Topmost = true; Win.Activate();
                AppList.SelectedItem = w;
                Slot s = null;
                for (int i = 0; i < 160; i++)
                {
                    await Task.Delay(500);
                    if (_slots.TryGetValue(w.Id, out s) && IsEmbedded(s)) break;
                }
                bool embedded = s != null && IsEmbedded(s);
                note.AppendLine("embedded: " + embedded + " | hint text: " + F<TextBlock>("AppHostTitle").Text);
                await Task.Delay(mode.IndexOf("boot", StringComparison.OrdinalIgnoreCase) >= 0 ? 25000 : 4000);
                if (embedded)
                {
                    Native.RECT r; Native.GetWindowRect(s.Hwnd, out r);
                    note.AppendLine("panel px: " + s.Panel.ClientSize.Width + "x" + s.Panel.ClientSize.Height
                        + " | vm window px: " + (r.R - r.L) + "x" + (r.B - r.T)
                        + " | parent is the KMS panel: " + (Native.GetParent(s.Hwnd) == s.Panel.Handle));
                }
                ScreenShot(file);
                note.AppendLine("captured " + file);

                if (embedded && mode.IndexOf("type", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var pt = s.Panel.PointToScreen(new System.Drawing.Point(s.Panel.ClientSize.Width / 2, s.Panel.ClientSize.Height / 2));
                    Native.POINT old; Native.GetCursorPos(out old);
                    Win.Activate();
                    await Task.Delay(400);
                    Native.Click(pt.X, pt.Y);
                    await Task.Delay(900);
                    uint vmPid; Native.GetWindowThreadProcessId(s.Hwnd, out vmPid);
                    IntPtr focus; uint focusPid = Native.FocusPid(out focus);
                    var mine = new System.Windows.Interop.WindowInteropHelper(Win).Handle;
                    // 键盘输入要落在虚拟机那个进程里，而且落在嵌进 KMS 的那个窗口上（不是它弹出的别的对话框）
                    bool inVm = focusPid == vmPid && vmPid != 0;
                    bool inside = focus != IntPtr.Zero && Native.GetAncestor(focus, 2) == mine;
                    note.AppendLine("after a real click in the picture: keyboard focus is in the VM window's process: " + inVm + " | that focus is inside the KMS window: " + inside);
                    if (inVm && inside)
                    {
                        foreach (ushort scan in new ushort[] { 0x2F, 0x12, 0x13, 0x1C }) { Native.Key(scan); await Task.Delay(150); }   // v e r 回车
                        note.AppendLine("typed: ver + Enter");
                        await Task.Delay(3000);
                        ScreenShot(Path.ChangeExtension(file, null) + "-typed.png");
                    }
                    else note.AppendLine("did not type: the keys would not have gone to the VM");
                    Native.SetCursorPos(old.X, old.Y);
                }

                if (hold)
                {
                    note.AppendLine("holding: KMS stays open with the VM inside");
                    File.WriteAllText(file + ".txt", note.ToString(), new UTF8Encoding(true));
                    return;
                }
                Win.Topmost = false;
                ReleaseAllApps();
                await Task.Delay(2000);
                note.AppendLine("released; desktop window found again: " + (Vms.FindWindow(vmName) != IntPtr.Zero) + " | vm still running: " + Vms.IsRunningNow(vmName));
            }
            catch (Exception ex) { note.AppendLine("ERROR " + ex); Store.LogError("AutoEmbedVm", ex); }
            try { File.WriteAllText(file + ".txt", note.ToString(), new UTF8Encoding(true)); } catch { }
            Application.Current.Shutdown(0);
        }

        // 自检用：真实执行一次“并排排列所有已打开的窗口”，把各窗口的状态写到文件里
        public async void AutoTile(string infoFile)
        {
            try
            {
                await Task.Delay(1500);
                var list = _apps.ToList();
                await Task.Run(() => RealApps.Refresh(list));
                var sb = new StringBuilder();
                foreach (var w in list) sb.AppendLine(w.Label + " | " + w.KindName + " | running=" + w.Running + " | " + w.DataDirText);
                var src = PresentationSource.FromVisual(Win);
                double scale = src != null && src.CompositionTarget != null ? src.CompositionTarget.TransformToDevice.M11 : 1.0;
                var area = SystemParameters.WorkArea;
                int n = RealApps.Tile(list, (int)(area.Left * scale), (int)(area.Top * scale), (int)(area.Width * scale), (int)(area.Height * scale));
                sb.AppendLine("tiled windows: " + n + " | screen work area px: " + (int)(area.Width * scale) + "x" + (int)(area.Height * scale));
                File.WriteAllText(infoFile, sb.ToString(), new UTF8Encoding(true));
            }
            catch (Exception ex) { Store.LogError("AutoTile", ex); }
            Application.Current.Shutdown(0);
        }

        // 自检用：在真实界面里点开第一个账号，直接给它发一条消息，等它跑完后存图退出
        public async void AutoChat(string dir, string shotPrefix, string message)
        {
            int code = 1;
            try
            {
                await Task.Delay(600);
                var a = _accounts.FirstOrDefault(x => x.Enabled);
                _projectDir = dir; SaveAll(); RefreshFooter();
                AccountList.SelectedItem = a;
                await Task.Delay(400);
                _views[a.Id].SendText(message);
                await Task.Delay(1500);
                while (a.Running > 0) await Task.Delay(300);
                await Task.Delay(900);
                Render((FrameworkElement)Win.Content, shotPrefix + "-main.png", 1);
                var info = new StringBuilder();
                info.AppendLine("steps: " + a.Steps.Count + " | last: " + (a.Steps.Count > 0 ? a.Steps[a.Steps.Count - 1].Kind + " " + a.Steps[a.Steps.Count - 1].Text : ""));
                info.AppendLine("session saved: " + (a.Session != null) + " | title: " + (a.Session != null ? a.Session.Title : "") + " | resumable: " + (a.DirectSession.Length > 0));
                info.AppendLine("history count: " + Sessions.List(a.Id).Count);
                File.WriteAllText(shotPrefix + "-info.txt", info.ToString(), new UTF8Encoding(true));
                code = a.Session != null && a.Steps.Any(s => s.Kind == "done") ? 0 : 1;
            }
            catch (Exception ex) { Store.LogError("AutoChat", ex); }
            Application.Current.Shutdown(code);
        }

        // 自检用：摆一段示例对话，只用来检查版式，不是真实运行
        void DemoChat()
        {
            AddToChat(UserBubble("把登录页的表单校验重写一遍，并补上测试", false));
            Func<string, string, string, string, string, string, TaskItem> mk = (id, kind, title, state, status, acct) =>
            {
                var t = new TaskItem(); t.Id = id; t.Kind = kind; t.Title = title; t.AccountLabel = acct; t.State = state; t.Status = status;
                t.Elapsed = state == "pending" ? "" : "0:4" + id.Length;
                return t;
            };
            OnTaskAdded(mk("规划", "plan", "总指挥拆分任务", "done", "完成，共 5 个任务", "Claude · 账号 1"));
            OnTaskAdded(mk("T1", "task", "梳理现有校验逻辑", "done", "完成", "Claude · 账号 1"));
            OnTaskAdded(mk("T2", "task", "重写表单校验模块", "done", "完成", "Codex · 账号 1"));
            OnTaskAdded(mk("T3", "task", "更新登录页调用", "done", "完成", "Claude · 账号 2"));
            OnTaskAdded(mk("T4", "task", "补充单元测试", "running", "运行中", "Gemini · 账号 1"));
            OnTaskAdded(mk("T5", "task", "运行测试并修正", "pending", "等待前置任务", ""));
            SetRunning(true);
            SetStatus("执行中：完成 3/5，运行 1");
            _statusTime.Text = "2分钟 14秒";
            _running = false;   // 只是摆样子，不拦截关闭
        }

        public void Shot(string page, string file)
        {
            Win.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                FrameworkElement target = (FrameworkElement)Win.Content;
                double scale = 1;
                if (page == "accounts") { if (_accounts.Count > 0) { AccountList.SelectedIndex = 0; ShowAccount(_accounts[0]); } }
                else if (page == "firstagent" && _accounts.Count > 0) { AccountList.SelectedIndex = 0; }   // 不摆示例：看重新打开后恢复出来的真实内容
                else if (page == "usage" && _accounts.Count > 0)
                {
                    // 摆一组示例数字，只用来检查用量面板的版式，不是真实数据
                    var a = _accounts[_accounts.Count - 1];
                    a.Plan = "Pro"; a.LimitsAt = DateTime.Now; a.RunCount = 3; a.TotalIn = 193266; a.TotalOut = 752;
                    a.LastModel = "claude-fable-5-2"; a.LastUsage = "输入 73,583，输出 398 tokens";
                    a.Limits.Add(new LimitInfo { Name = "5 小时限额", Percent = 54, Reset = "6:50 重置" });
                    a.Limits.Add(new LimitInfo { Name = "每周 · 所有模型", Percent = 47, Reset = "11月17日 5:00 重置" });
                    a.Limits.Add(new LimitInfo { Name = "每周 · Fable", Percent = 94, Reset = "11月17日 5:00 重置" });
                    var pop = UsagePopup.Show(Ring, a);
                    target = (FrameworkElement)pop.Child; scale = 1.5;
                }
                else if (page == "agentpage" && _accounts.Count > 0)
                {
                    // 摆一段示例内容，只用来检查“点左侧账号后主窗口变成对话界面”的版式，不是真实运行
                    var a = _accounts[_accounts.Count - 1];
                    a.AddStep(new Step("user", "把登录页的表单校验拆到单独的文件里"));
                    a.AddStep(new Step("say", "我先看一下现有的校验逻辑在哪里，再决定怎么拆。"));
                    a.AddStep(new Step("tool", "读取 src/login.js", "src/login.js"));
                    a.AddStep(new Step("tool", "搜索 validate", "validate"));
                    a.AddStep(new Step("say", "校验都写在 `login.js` 的提交函数里，一共三处。我把它们移到新文件 `validate.js`，再让登录页调用它。"));
                    a.AddStep(new Step("tool", "编辑 validate.js", "新增  src/validate.js"));
                    a.AddStep(new Step("tool", "编辑 login.js", "修改  src/login.js"));
                    a.AddStep(new Step("tool", "运行命令 npm test", "npm test\n\n结果：\n12 passing"));
                    a.Running = 1; a.RunStart = DateTime.Now.AddSeconds(-134);
                    AccountList.SelectedItem = a;
                }
                else if (page == "settings") { NavSettings.IsChecked = true; }
                else if (page == "demo") { DemoChat(); scale = 1.5; }
                else if (page == "agent" && _accounts.Count > 0)
                {
                    // 摆一段示例内容，只用来检查版式，不是真实运行
                    var a = _accounts[0];
                    a.AddStep(new Step("task", "T2　重写表单校验模块", "把 login.js 里的校验逻辑拆到 validate.js，保持现有行为不变，并补上邮箱格式校验。"));
                    a.AddStep(new Step("say", "我先看一下现有的校验逻辑在哪里，再决定怎么拆。"));
                    a.AddStep(new Step("tool", "读取 src/login.js", "src/login.js"));
                    a.AddStep(new Step("tool", "搜索 validate", "validate"));
                    a.AddStep(new Step("say", "校验都写在 `login.js` 的提交函数里，一共三处。我把它们移到新文件 `validate.js`，再让登录页调用它。"));
                    a.AddStep(new Step("tool", "编辑 validate.js", "新增  src/validate.js"));
                    a.AddStep(new Step("tool", "编辑 login.js", "修改  src/login.js"));
                    a.AddStep(new Step("tool", "运行命令 npm test", "npm test\n\n结果：\n12 passing"));
                    a.AddStep(new Step("say", "改好了，测试全部通过。"));
                    a.AddStep(new Step("tool", "运行命令 git status --short", "git status --short"));
                    a.Running = 1; a.RunStart = DateTime.Now.AddSeconds(-134);
                    var w = WindowFor(a, true);
                    target = (FrameworkElement)w.Win.Content;
                    scale = 1.5;
                }
                // 账号页要等登录状态和模型目录查询回来
                var later = new DispatcherTimer(); later.Interval = TimeSpan.FromMilliseconds(page == "accounts" ? 9000 : 2500);
                later.Tick += (s, e) =>
                {
                    later.Stop();
                    try { Render(target, file, scale); }
                    catch (Exception ex) { Store.LogError("Shot", ex); }
                    Application.Current.Shutdown(0);
                };
                later.Start();
            }));
        }
    }

    // 不开窗口的真实运行，用来验证：--test <日志> 测试所有账号；--run <项目目录> <日志> <权限> <目标>
    public static class Headless
    {
        public static async Task<int> Run(AppConfig cfg, string[] args)
        {
            var log = new StringBuilder();
            string logFile = args[0] == "--test" ? (args.Length > 1 ? args[1] : "test.log") : (args.Length > 2 ? args[2] : "run.log");
            Action flush = () => { try { File.WriteAllText(logFile, log.ToString(), new UTF8Encoding(true)); } catch { } };
            int code = 1;
            try
            {
                Defaults.Ensure(cfg);
                var post = new Action<Action>(a => Application.Current.Dispatcher.BeginInvoke(a));
                if (args[0] == "--test")
                {
                    code = 0;
                    foreach (var agent in Cli.Agents) log.AppendLine("CLI " + agent + ": " + (Cli.Find(agent, cfg) ?? "未找到"));
                    foreach (var a in cfg.Accounts)
                    {
                        var sw = Stopwatch.StartNew();
                        var live = new StringBuilder();
                        var r = await Cli.Run(cfg, a, Defaults.TestDir(), Defaults.TestPrompt, "readonly", s => { lock (live) live.Append(s.ToLog()); }, CancellationToken.None, null, true);
                        log.AppendLine("ACCOUNT " + a.Display + " [" + a.LoginText + "] ok=" + r.Ok + " exit=" + r.ExitCode + " ms=" + sw.ElapsedMilliseconds + " limited=" + r.RateLimited);
                        var st = await Cli.Status(cfg, a);
                        var models = await Cli.Models(cfg, a);
                        log.AppendLine("  login: state=" + st.State + " identity=" + (st.Identity.Length > 0 ? "(有)" : "(无)") + " plan=" + st.Plan);
                        log.AppendLine("  models: " + string.Join(", ", models.Select(m => m.Id).ToArray()));
                        log.AppendLine("  asked: model=" + (a.Model.Length > 0 ? a.Model : "(默认)") + " effort=" + (a.Effort.Length > 0 ? a.Effort : "(默认)"));
                        log.AppendLine("  reported: model=" + r.Model + " usage=" + r.Usage);
                        log.AppendLine("  limits: " + (r.Limits.Count == 0 ? "(无)" : string.Join("; ", r.Limits.Select(l => l.Name + " " + l.Percent + "% " + l.Reset).ToArray())));
                        log.AppendLine("  reply: " + r.Text);
                        if (!r.Ok) { code = 1; log.AppendLine("  error: " + r.Error); }
                        lock (live) log.AppendLine("  --- live output ---\n" + live);
                        flush();
                    }
                    if (cfg.Accounts.Count == 0) { code = 1; log.AppendLine("没有账号"); }
                }
                else if (args[0] == "--chat")
                {
                    // 验证“直接给一个账号发消息，并接着上一轮继续说”：--chat <项目目录> <日志> <第一句> <第二句>
                    var a = cfg.Accounts.FirstOrDefault(x => x.Enabled);
                    string session = "";
                    for (int i = 3; i < args.Length && a != null; i++)
                    {
                        var live = new StringBuilder();
                        var r = await Cli.Run(cfg, a, args[1], args[i], "readonly", s => { lock (live) live.Append(s.Kind + ": " + s.Text.Replace("\n", " ") + "\n"); }, CancellationToken.None, session);
                        log.AppendLine("TURN " + (i - 2) + " resume=" + (session.Length > 0) + " ok=" + r.Ok + " exit=" + r.ExitCode + " session=" + (r.SessionId.Length > 0 ? "(有)" : "(无)") + " sameSession=" + (session.Length > 0 && session == r.SessionId));
                        log.AppendLine("  reply: " + r.Text.Replace("\n", " "));
                        if (!r.Ok) log.AppendLine("  error: " + r.Error);
                        lock (live) log.AppendLine("  steps:\n" + live);
                        code = r.Ok ? 0 : 1;
                        if (!r.Ok) break;
                        session = r.SessionId;
                        flush();
                    }
                }
                else
                {
                    var cmd = new Commander(cfg, cfg.Accounts);
                    cmd.ProjectDir = args[1]; cmd.Perm = args.Length > 3 ? args[3] : "edit"; cmd.Goal = args.Length > 4 ? args[4] : "";
                    cmd.Review = true; cmd.Post = post;
                    cmd.Chief = cfg.Accounts.FirstOrDefault(a => a.Id == cfg.ChiefId && a.Enabled) ?? cfg.Accounts.FirstOrDefault(a => a.Enabled);
                    cmd.Status += s => { log.AppendLine(DateTime.Now.ToString("HH:mm:ss") + " STATUS " + s); flush(); };
                    cmd.TaskStarted += (acc, t) => { log.AppendLine(DateTime.Now.ToString("HH:mm:ss") + " START " + t.Id + " " + t.Title + " -> " + acc.Display); flush(); };
                    await cmd.Start();
                    foreach (var t in cmd.Tasks)
                        log.AppendLine("TASK " + t.Id + " [" + t.State + "] " + t.Title + " | " + t.AccountLabel + " | 尝试 " + t.Attempts + " | " + t.Elapsed + " | 依赖 " + t.DepsText);
                    log.AppendLine("REPORT " + cmd.ReportFile);
                    code = cmd.Tasks.Count > 1 && cmd.Tasks.All(t => t.State == "done") ? 0 : 1;
                }
            }
            catch (Exception ex) { log.AppendLine("EXCEPTION " + ex); }
            log.AppendLine("EXIT " + code);
            flush();
            return code;
        }
    }
}
