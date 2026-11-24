using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace AC
{
    // Agent 对话界面向主窗口要的东西
    public interface IAgentHost
    {
        AppConfig Cfg { get; }
        string PermFor(Account a);                        // 这个账号的执行权限
        string ProjectDirOf(Account a);                   // 这个账号的项目文件夹；没有就是空
        string ProjectDirOrAsk(Account a, bool force);    // 取或换这个账号的项目文件夹，取消返回 null
        void Post(Action a);                              // 把回调送回界面线程
        void SetPerm(Account a, string perm);             // 换这个账号的执行权限
        void Save();                   // 保存账号设置
    }

    // 一个账号的对话界面，用起来和 Claude 的 Code 窗口一样：
    // 上面是一步一步的回复和操作，下面是带计时的状态行和输入框，可以直接给这个账号发消息。
    // 它既嵌在主窗口里（点左侧账号时显示），也可以放进单独的窗口。
    public class AgentView
    {
        public readonly DockPanel Root = new DockPanel();
        public event Action SettingsClicked;   // 点了“账号设置”
        public event Action PopOutClicked;     // 点了“独立窗口”

        readonly Account _acc;
        readonly IAgentHost _host;
        readonly StepList _list = new StepList();
        readonly ScrollViewer _scroll = new ScrollViewer();
        readonly TextBox _input = new TextBox();
        readonly TextBlock _hint, _statusLabel, _statusText, _statusTime, _footLeft;
        TextBlock _modelLink, _effortLink, _permLink, _histLink;
        readonly StackPanel _empty = new StackPanel();   // 还没有内容时，中间显示的欢迎文字
        readonly StackPanel _statusRow = new StackPanel();
        readonly ContentControl _sendIcon = new ContentControl();
        readonly Ellipse _ring = new Ellipse();
        readonly Border _loginBar = new Border();
        readonly DispatcherTimer _timer = new DispatcherTimer();
        CancellationTokenSource _cts;
        bool _direct;            // 这里发的消息正在执行
        bool _shownStop;
        bool _fullConfirmed;
        string _action = "";     // 最近在做的一件事，显示在状态行

        static TextBlock Link(string text, Action click)
        {
            var t = Look.Text(text, Look.Mid, 13);
            t.Cursor = Cursors.Hand; t.Margin = new Thickness(16, 0, 0, 0); t.Background = Brushes.Transparent;
            t.MouseEnter += (s, e) => t.Foreground = Look.Ink;
            t.MouseLeave += (s, e) => t.Foreground = Look.Mid;
            t.MouseLeftButtonUp += (s, e) => click();
            return t;
        }

        public AgentView(Account acc, IAgentHost host, bool embedded)
        {
            _acc = acc; _host = host;
            Root.Background = Brushes.White;

            // 顶部：是哪个账号、登录的是谁、用什么模型
            var head = new Border();
            head.Background = Look.Make("#F7F7F5"); head.BorderBrush = Look.Make("#ECECEA"); head.BorderThickness = new Thickness(0, 0, 0, 1);
            head.Padding = new Thickness(22, 12, 20, 12);
            DockPanel.SetDock(head, Dock.Top);
            var hp = new DockPanel();
            var avatar = Look.Avatar(acc, 30); avatar.Margin = new Thickness(0, 0, 12, 0);
            hp.Children.Add(avatar);
            {
                // 每个 Agent 页面都有：开一段新会话、翻历史会话
                var hist = Link("历史", () => ShowHistory());
                var fresh = Link("新会话", () => NewSession());
                _histLink = hist;
                DockPanel.SetDock(hist, Dock.Right); DockPanel.SetDock(fresh, Dock.Right);
                hp.Children.Add(hist); hp.Children.Add(fresh);
            }
            if (embedded)
            {
                var pop = Link("独立窗口", () => { var h = PopOutClicked; if (h != null) h(); });
                var set = Link("账号设置", () => { var h = SettingsClicked; if (h != null) h(); });
                DockPanel.SetDock(pop, Dock.Right); DockPanel.SetDock(set, Dock.Right);
                hp.Children.Add(pop); hp.Children.Add(set);
            }
            var state = Look.Text("", Look.Mid, 12.5); state.Margin = new Thickness(12, 0, 0, 0);
            state.SetBinding(TextBlock.TextProperty, new Binding("StatusText") { Source = acc });
            DockPanel.SetDock(state, Dock.Right);
            var name = Look.Text("", Look.Ink, 15); name.FontWeight = FontWeights.SemiBold;
            name.SetBinding(TextBlock.TextProperty, new Binding("Display") { Source = acc });
            var sub = Look.Text("", Look.Grey, 12.5); sub.Margin = new Thickness(12, 1, 0, 0); sub.TextTrimming = TextTrimming.CharacterEllipsis;
            sub.SetBinding(TextBlock.TextProperty, new Binding("SubText") { Source = acc });
            hp.Children.Add(state); hp.Children.Add(name); hp.Children.Add(sub);
            head.Child = hp;

            // 没登录时的提醒条
            _loginBar.Background = Look.Make("#FFF4E5"); _loginBar.BorderBrush = Look.Make("#F3D9B1"); _loginBar.BorderThickness = new Thickness(0, 0, 0, 1);
            _loginBar.Padding = new Thickness(22, 9, 20, 9); _loginBar.Visibility = Visibility.Collapsed;
            DockPanel.SetDock(_loginBar, Dock.Top);
            var lb = new DockPanel(); lb.LastChildFill = false;
            lb.Children.Add(Look.Text("这个账号还没有登录，登录后才能给它派活或发消息。", Look.Make("#8A5A00"), 13));
            if (embedded) lb.Children.Add(Link("去登录 ›", () => { var h = SettingsClicked; if (h != null) h(); }));
            _loginBar.Child = lb;

            // 底部：输入框和下面一行（项目文件夹、模型、权限）
            var bottom = new StackPanel(); bottom.Margin = new Thickness(20, 0, 20, 14); bottom.MaxWidth = 768;
            DockPanel.SetDock(bottom, Dock.Bottom);
            var box = new Border();
            box.Background = Brushes.White; box.BorderBrush = Look.Make("#D9D9D9"); box.BorderThickness = new Thickness(1);
            box.CornerRadius = new CornerRadius(18); box.Padding = new Thickness(14, 6, 8, 6);
            var inner = new DockPanel();
            var send = new Border(); send.Background = Brushes.Transparent; send.Cursor = Cursors.Hand; send.VerticalAlignment = VerticalAlignment.Bottom;
            send.Child = _sendIcon; _sendIcon.Focusable = false; _sendIcon.Content = Icons.Send();
            send.MouseLeftButtonUp += (s, e) => { if (_direct) StopDirect(); else SendDirect(); };
            DockPanel.SetDock(send, Dock.Right);
            var area = new Grid(); area.Margin = new Thickness(0, 0, 8, 0);
            _hint = Look.Text("", Look.Grey, Look.Body); _hint.IsHitTestVisible = false; _hint.Margin = new Thickness(3, 0, 0, 0); _hint.TextTrimming = TextTrimming.CharacterEllipsis;
            _input.Background = Brushes.Transparent; _input.BorderThickness = new Thickness(0); _input.FontSize = Look.Body; _input.Foreground = Look.Ink;
            _input.AcceptsReturn = true; _input.TextWrapping = TextWrapping.Wrap; _input.MinHeight = 28; _input.MaxHeight = 140; _input.Padding = new Thickness(0, 3, 0, 3);
            _input.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; _input.VerticalContentAlignment = VerticalAlignment.Center;
            _input.TextChanged += (s, e) => _hint.Visibility = _input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _input.PreviewKeyDown += (s, e) =>
            {
                if (e.Key != Key.Enter && e.Key != Key.Return) return;
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;
                e.Handled = true;
                SendDirect();
            };
            area.Children.Add(_hint); area.Children.Add(_input);
            inner.Children.Add(send); inner.Children.Add(area);
            box.Child = inner;
            var foot = new DockPanel(); foot.Margin = new Thickness(8, 9, 6, 0); foot.LastChildFill = false;
            _footLeft = Look.Text("", Look.Mid, 12.5); _footLeft.TextTrimming = TextTrimming.CharacterEllipsis; _footLeft.MaxWidth = 360;
            // 点一下就能给这个账号换项目文件夹（只影响它自己，别的账号不动）
            _footLeft.Cursor = Cursors.Hand; _footLeft.Background = Brushes.Transparent;
            _footLeft.MouseEnter += (s, e) => _footLeft.Foreground = Look.Ink;
            _footLeft.MouseLeave += (s, e) => _footLeft.Foreground = Look.Mid;
            _footLeft.MouseLeftButtonUp += (s, e) => PickDir();
            // 右下角：模型、推理强度、权限都可以直接点选；圆环点开是用量
            _ring.Width = 14; _ring.Height = 14; _ring.StrokeThickness = 2; _ring.Stroke = Look.Ring; _ring.VerticalAlignment = VerticalAlignment.Center;
            var ringHit = new Border(); ringHit.Background = Brushes.Transparent; ringHit.Cursor = Cursors.Hand; ringHit.Padding = new Thickness(8, 4, 4, 4);
            ringHit.ToolTip = "查看用量"; ringHit.Child = _ring;
            ringHit.MouseLeftButtonUp += (s, e) => UsagePopup.Show(ringHit, _acc);
            DockPanel.SetDock(ringHit, Dock.Right);
            _permLink = Link("", PermMenu); _effortLink = Link("", EffortMenu); _modelLink = Link("", ModelMenu);
            _modelLink.Foreground = Look.Ink;
            _modelLink.MouseLeave += (s, e) => _modelLink.Foreground = Look.Ink;
            DockPanel.SetDock(_permLink, Dock.Right); DockPanel.SetDock(_effortLink, Dock.Right); DockPanel.SetDock(_modelLink, Dock.Right);
            foot.Children.Add(_footLeft); foot.Children.Add(ringHit); foot.Children.Add(_permLink); foot.Children.Add(_effortLink); foot.Children.Add(_modelLink);
            // 输入框下面垫一层很淡的阴影，和主对话页一致
            var lifted = new Grid();
            var glow = new Border(); glow.Background = Brushes.White; glow.CornerRadius = new CornerRadius(18);
            var soft = new System.Windows.Media.Effects.DropShadowEffect();
            soft.BlurRadius = 16; soft.ShadowDepth = 2; soft.Direction = 270; soft.Opacity = 0.08; soft.Color = Colors.Black;
            glow.Effect = soft;
            lifted.Children.Add(glow); lifted.Children.Add(box);
            bottom.Children.Add(lifted); bottom.Children.Add(foot);

            // 中间：一步一步的内容，最后跟着状态行
            _statusRow.Orientation = Orientation.Horizontal; _statusRow.Margin = new Thickness(0, 12, 0, 10); _statusRow.Visibility = Visibility.Collapsed;
            _statusRow.Children.Add(Icons.Dots());
            _statusLabel = Look.Text("", Look.Mid, Look.Body); _statusLabel.Margin = new Thickness(10, 0, 0, 0);
            _statusText = Look.Text("", Look.Ink, Look.Body); _statusText.Margin = new Thickness(8, 0, 0, 0); _statusText.TextTrimming = TextTrimming.CharacterEllipsis; _statusText.MaxWidth = 420;
            _statusTime = Look.Text("", Look.Grey, Look.Body); _statusTime.Margin = new Thickness(10, 0, 0, 0);
            _statusRow.Children.Add(_statusLabel); _statusRow.Children.Add(_statusText); _statusRow.Children.Add(_statusTime);
            var content = new StackPanel(); content.Margin = new Thickness(40, 20, 40, 12); content.MaxWidth = 768;
            content.Children.Add(_list.Panel); content.Children.Add(_statusRow);
            _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; _scroll.Focusable = false;
            _scroll.Content = content;

            // 空白时的欢迎文字，叠在内容区中间
            _empty.VerticalAlignment = VerticalAlignment.Center; _empty.HorizontalAlignment = HorizontalAlignment.Center; _empty.Margin = new Thickness(30, 0, 30, 60);
            _empty.IsHitTestVisible = false;
            var bigAvatar = Look.Avatar(acc, 46); bigAvatar.HorizontalAlignment = HorizontalAlignment.Center; bigAvatar.Margin = new Thickness(0, 0, 0, 18);
            var hello = Look.Text("", Look.Ink, 26); hello.HorizontalAlignment = HorizontalAlignment.Center; hello.TextWrapping = TextWrapping.Wrap; hello.TextAlignment = TextAlignment.Center;
            hello.SetBinding(TextBlock.TextProperty, new Binding("Display") { Source = acc, StringFormat = "想让 {0} 做什么？" });
            var tip = Look.Text("它会一步一步告诉你在做什么。右上角的“历史”可以翻以前的会话。", Look.Grey, 14);
            tip.HorizontalAlignment = HorizontalAlignment.Center; tip.Margin = new Thickness(0, 12, 0, 0); tip.TextWrapping = TextWrapping.Wrap; tip.TextAlignment = TextAlignment.Center;
            _empty.Children.Add(bigAvatar); _empty.Children.Add(hello); _empty.Children.Add(tip);
            var middle = new Grid();
            middle.Children.Add(_scroll); middle.Children.Add(_empty);

            Root.Children.Add(head); Root.Children.Add(_loginBar); Root.Children.Add(bottom); Root.Children.Add(middle);

            Reload();
            acc.StepAdded += OnStep;
            acc.StepsReset += Reload;

            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += (s, e) => Refresh();
            _timer.Start();
            Root.Loaded += (s, e) => { Refresh(); _scroll.ScrollToEnd(); };
            Refresh();
        }

        // 整个重画：新会话，或者从历史里打开了一段
        void Reload()
        {
            _list.Clear();
            foreach (var s in _acc.Steps) _list.Add(s);
            _action = _acc.Steps.Count > 0 && _acc.Steps[_acc.Steps.Count - 1].Kind == "tool" ? _acc.Steps[_acc.Steps.Count - 1].Text : "";
            _empty.Visibility = _acc.Steps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _scroll.ScrollToEnd();
        }

        void NewSession()
        {
            if (_acc.Running > 0) { _acc.AddStep(new Step("info", "它还在干活，等这一轮结束后再开新会话。")); return; }
            Sessions.StartNew(_acc);
            _input.Focus();
        }

        // 历史会话面板：上面搜索，下面按时间列出，点一下打开
        void ShowHistory()
        {
            var pop = new System.Windows.Controls.Primitives.Popup();
            pop.PlacementTarget = _histLink; pop.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            pop.StaysOpen = false; pop.AllowsTransparency = true; pop.HorizontalOffset = -330; pop.VerticalOffset = 6;
            var card = new Border();
            card.Background = Brushes.White; card.BorderBrush = Look.Line; card.BorderThickness = new Thickness(1); card.CornerRadius = new CornerRadius(12);
            card.Padding = new Thickness(14, 12, 10, 12); card.Width = 400; card.Margin = new Thickness(14);
            var shadow = new System.Windows.Media.Effects.DropShadowEffect();
            shadow.BlurRadius = 18; shadow.ShadowDepth = 3; shadow.Direction = 270; shadow.Opacity = 0.14; shadow.Color = Colors.Black;
            card.Effect = shadow;
            var sp = new StackPanel();
            var all = Sessions.List(_acc.Id);

            var searchBox = new Border(); searchBox.Background = Look.Soft; searchBox.CornerRadius = new CornerRadius(8); searchBox.Padding = new Thickness(10, 5, 10, 5); searchBox.Margin = new Thickness(0, 0, 6, 8);
            var sg = new Grid();
            var ph = Look.Text("搜索历史会话（标题或内容）", Look.Grey, 13.5); ph.IsHitTestVisible = false;
            var q = new TextBox(); q.Background = Brushes.Transparent; q.BorderThickness = new Thickness(0); q.FontSize = 13.5; q.Padding = new Thickness(0);
            sg.Children.Add(ph); sg.Children.Add(q); searchBox.Child = sg;
            var rows = new StackPanel();
            var scroll = new ScrollViewer(); scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; scroll.MaxHeight = 380; scroll.Content = rows; scroll.Focusable = false;

            Action fill = null;
            fill = () =>
            {
                rows.Children.Clear();
                int shown = 0;
                foreach (var s in all)
                {
                    if (!Sessions.Matches(s, q.Text)) continue;
                    var session = s;
                    var row = new Border(); row.Background = Brushes.Transparent; row.CornerRadius = new CornerRadius(8); row.Padding = new Thickness(10, 7, 8, 7); row.Cursor = Cursors.Hand;
                    row.Margin = new Thickness(0, 0, 6, 1);
                    bool current = _acc.Session != null && _acc.Session.Id == session.Id;
                    if (current) row.Background = Look.Make("#F1F1EF");
                    row.MouseEnter += (o, e) => row.Background = Look.Make("#F4F4F2");
                    row.MouseLeave += (o, e) => row.Background = current ? Look.Make("#F1F1EF") : Brushes.Transparent;
                    var dp = new DockPanel();
                    var del = Look.Text("删除", Look.Grey, 12); del.Cursor = Cursors.Hand; del.Margin = new Thickness(8, 0, 0, 0);
                    DockPanel.SetDock(del, Dock.Right);
                    del.MouseLeftButtonUp += (o, e) =>
                    {
                        e.Handled = true;
                        if (current) return;   // 正在用的这一段不删
                        Sessions.Delete(session); all.Remove(session); fill();
                    };
                    var two = new StackPanel();
                    var title = Look.Text(session.Title, Look.Ink, 13.5); title.TextTrimming = TextTrimming.CharacterEllipsis;
                    string when = (session.Updated ?? "").Length >= 16 ? session.Updated.Substring(5, 11) : (session.Updated ?? "");
                    string where = string.IsNullOrEmpty(session.Dir) ? "" : "　" + System.IO.Path.GetFileName(session.Dir.TrimEnd('\\', '/'));
                    var meta = Look.Text(when + where + (current ? "　当前" : ""), Look.Grey, 12); meta.Margin = new Thickness(0, 2, 0, 0); meta.TextTrimming = TextTrimming.CharacterEllipsis;
                    two.Children.Add(title); two.Children.Add(meta);
                    dp.Children.Add(del); dp.Children.Add(two);
                    row.Child = dp;
                    row.MouseLeftButtonUp += (o, e) =>
                    {
                        if (_acc.Running > 0) { pop.IsOpen = false; _acc.AddStep(new Step("info", "它还在干活，等这一轮结束后再切换会话。")); return; }
                        Sessions.Open(_acc, session);
                        pop.IsOpen = false;
                    };
                    rows.Children.Add(row);
                    shown++;
                }
                if (shown == 0)
                {
                    var none = Look.Text(all.Count == 0 ? "还没有历史会话。给它发过消息或派过任务之后，会话会自动保存在这里。" : "没有找到包含这些字的会话。", Look.Grey, 13);
                    none.TextWrapping = TextWrapping.Wrap; none.Margin = new Thickness(10, 10, 10, 10);
                    rows.Children.Add(none);
                }
            };
            q.TextChanged += (o, e) => { ph.Visibility = q.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; fill(); };
            fill();
            sp.Children.Add(searchBox); sp.Children.Add(scroll);
            card.Child = sp; pop.Child = card; pop.IsOpen = true;
            q.Focus();
        }

        // 自检用：等同于在输入框里打字再按回车
        public void SendText(string text) { _input.Text = text; SendDirect(); }

        public void Dispose()
        {
            _acc.StepsReset -= Reload;
            _acc.StepAdded -= OnStep;
            _timer.Stop();
            if (_cts != null) _cts.Cancel();
        }

        public void FocusInput() { _input.Focus(); }

        void OnStep(Step s)
        {
            _empty.Visibility = Visibility.Collapsed;
            _list.Add(s);
            if (s.Kind == "tool") _action = s.Text;
            else if (s.Kind == "say" || s.Kind == "task" || s.Kind == "user") _action = "";
            _scroll.ScrollToEnd();
            Refresh();
        }

        static string Span(TimeSpan t)
        {
            if (t.TotalSeconds < 0) t = TimeSpan.Zero;
            if (t.TotalMinutes >= 1) return ((int)t.TotalMinutes) + "分钟 " + t.Seconds.ToString("00") + "秒";
            return t.Seconds + "秒";
        }

        // 状态行、输入框提示和底部那行小字，每秒刷新一次
        void Refresh()
        {
            bool busy = _acc.Running > 0;
            _statusRow.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            _loginBar.Visibility = _acc.LoginState == "out" ? Visibility.Visible : Visibility.Collapsed;
            if (busy)
            {
                // 状态行：前面是在做什么（灰），中间是对象（黑），后面是已经过了多久
                string label = "思考中", target = "";
                if (_action.Length > 0)
                {
                    int sp = _action.IndexOf(' ');
                    label = sp > 0 ? _action.Substring(0, sp) + "中" : _action;
                    target = sp > 0 ? _action.Substring(sp + 1) : "";
                }
                _statusLabel.Text = label; _statusText.Text = target;
                _statusTime.Text = Span(DateTime.Now - _acc.RunStart);
            }
            _hint.Text = _direct ? "正在执行，点右边的方块可以停止"
                       : busy ? "正在执行总指挥派来的任务，结束后可以直接给它发消息"
                       : "直接给 " + _acc.Display + " 发消息，按 Enter 发送，按 Shift+Enter 换行";
            if (_shownStop != _direct) { _shownStop = _direct; _sendIcon.Content = _direct ? Icons.Stop() : Icons.Send(); }
            // 圆环：某项限额用到九成以上变红，干活时是橙色
            _ring.Stroke = _acc.MaxLimitPercent >= 90 ? Look.Red : busy ? Look.Accent : Look.Ring;
            // 模型和强度显示成好认的名字，比如“Fable 5.2　超高”；鼠标停上去能看到原始名称
            _modelLink.Text = Look.ModelLabel(_acc.Model);
            _modelLink.ToolTip = _acc.Model.Length > 0 ? _acc.Model : "用该工具自己的默认模型";
            _effortLink.Text = Look.EffortLabel(_acc.Effort);
            _effortLink.ToolTip = _acc.Effort.Length > 0 ? "推理强度 " + _acc.Effort : "用该工具自己的默认推理强度";
            _effortLink.Visibility = _acc.Agent == "gemini" ? Visibility.Collapsed : Visibility.Visible;
            string perm = _host.PermFor(_acc);
            _permLink.Text = perm == "readonly" ? "只读分析" : perm == "full" ? "完全自动" : "可修改文件";
            string dir = _host.ProjectDirOf(_acc);
            _footLeft.Text = dir.Length == 0 ? "还没选项目文件夹 · 点这里选" : "项目文件夹：" + (dir.Length > 40 ? "…" + dir.Substring(dir.Length - 38) : dir);
            _footLeft.ToolTip = dir.Length == 0 ? "给这个账号选一个工作文件夹，每个账号可以不一样" : dir + "　（点一下可以换）";
        }

        // 底部的项目文件夹：点一下给这个账号换（只影响它自己）
        void PickDir()
        {
            if (_acc.Running > 0) { _acc.AddStep(new Step("info", "它正在干活，等这一轮结束后再换项目文件夹。")); return; }
            if (_host.ProjectDirOrAsk(_acc, true) == null) return;
            Refresh();
        }

        void StopDirect()
        {
            if (_cts != null) _cts.Cancel();
        }

        // ---------- 右下角的三个选择：模型、推理强度、权限 ----------

        static void Open(ContextMenu m, UIElement anchor)
        {
            m.PlacementTarget = anchor;
            m.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
            m.IsOpen = true;
        }

        static MenuItem Item(string header, bool on, Action pick)
        {
            var it = new MenuItem(); it.Header = header; it.IsChecked = on;
            it.Click += (s, e) => pick();
            return it;
        }

        async void ModelMenu()
        {
            System.Collections.Generic.List<ModelInfo> models;
            try { models = await Cli.Models(_host.Cfg, _acc); } catch { models = new System.Collections.Generic.List<ModelInfo>(); }
            var m = new ContextMenu();
            m.Items.Add(Item("默认模型", _acc.Model.Length == 0, () => { _acc.Model = ""; _host.Save(); Refresh(); }));
            foreach (var x in models)
            {
                var id = x.Id;
                m.Items.Add(Item(Look.ModelLabel(id) + "　" + id, _acc.Model == id, () => { _acc.Model = id; _host.Save(); Refresh(); }));
            }
            if (_acc.Model.Length > 0 && !models.Exists(x => x.Id == _acc.Model)) m.Items.Add(Item(_acc.Model, true, () => { }));
            Open(m, _modelLink);
        }

        async void EffortMenu()
        {
            System.Collections.Generic.List<ModelInfo> models;
            try { models = await Cli.Models(_host.Cfg, _acc); } catch { models = new System.Collections.Generic.List<ModelInfo>(); }
            // 选了具体模型就只列它支持的强度，否则列出全部
            var efforts = new System.Collections.Generic.List<string>();
            foreach (var x in models)
                if (_acc.Model.Length == 0 || x.Id == _acc.Model)
                    foreach (var e in x.Efforts) if (!efforts.Contains(e)) efforts.Add(e);
            var m = new ContextMenu();
            m.Items.Add(Item("默认强度", _acc.Effort.Length == 0, () => { _acc.Effort = ""; _host.Save(); Refresh(); }));
            foreach (var e in efforts)
            {
                var level = e;
                m.Items.Add(Item(Look.EffortLabel(level) + "　" + level, _acc.Effort == level, () => { _acc.Effort = level; _host.Save(); Refresh(); }));
            }
            Open(m, _effortLink);
        }

        void PermMenu()
        {
            var m = new ContextMenu();
            string perm = _host.PermFor(_acc);
            m.Items.Add(Item("只读分析（不改文件）", perm == "readonly", () => { _host.SetPerm(_acc, "readonly"); Refresh(); }));
            m.Items.Add(Item("可修改文件", perm == "edit", () => { _host.SetPerm(_acc, "edit"); Refresh(); }));
            m.Items.Add(Item("完全自动（可执行命令）", perm == "full", () => { _host.SetPerm(_acc, "full"); Refresh(); }));
            Open(m, _permLink);
        }

        // 直接给这个账号发消息：真实启动它的命令行，接着上一轮的会话继续
        async void SendDirect()
        {
            var text = _input.Text.Trim();
            if (text.Length == 0 || _direct) return;
            if (_acc.Running >= _acc.MaxParallel)
            {
                _acc.AddStep(new Step("info", "这个账号正在执行任务，等它结束后再发。也可以在账号设置里把“同时任务数”调大。"));
                return;
            }
            if (!_acc.Enabled) { _acc.AddStep(new Step("info", "这个账号已停用，先在账号设置里启用它。")); return; }
            if (Cli.Find(_acc.Agent, _host.Cfg) == null) { _acc.AddStep(new Step("error", "没有找到 " + _acc.AgentName + " 的命令行程序，请先安装或在“设置”里填写路径。")); return; }
            string dir = _host.ProjectDirOrAsk(_acc, false);
            if (dir == null) return;
            string perm = _host.PermFor(_acc);
            if (perm == "full" && !_fullConfirmed)
            {
                var owner = Window.GetWindow(Root);
                var ask = "当前权限是“完全自动”：它可以不经确认就执行命令、修改文件。确定继续吗？";
                var answer = owner != null ? MessageBox.Show(owner, ask, "KMS多控", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                                           : MessageBox.Show(ask, "KMS多控", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) return;
                _fullConfirmed = true;
            }

            _input.Clear();
            _acc.AddStep(new Step("user", text));
            _acc.Running = _acc.Running + 1; _acc.RunStart = DateTime.Now; _acc.LastUsed = DateTime.Now; _acc.CurrentTask = "直接对话";
            _direct = true; _action = ""; _cts = new CancellationTokenSource();
            Refresh();
            string resume = string.Equals(_acc.DirectDir, dir, StringComparison.OrdinalIgnoreCase) ? _acc.DirectSession : "";
            RunResult r;
            try
            {
                r = await Cli.Run(_host.Cfg, _acc, dir, text, perm, s => _host.Post(() => _acc.AddStep(s)), _cts.Token, resume);
            }
            catch (Exception ex) { r = new RunResult(); r.Error = "运行出错：" + ex.Message; Store.LogError("直接对话", ex); }
            // 让排在前面的内容先显示完
            var flushed = new TaskCompletionSource<bool>();
            _host.Post(() => flushed.TrySetResult(true));
            await flushed.Task;

            string took = Span(DateTime.Now - _acc.RunStart);
            _acc.Running = _acc.Running - 1; _acc.CurrentTask = "";
            _direct = false;
            _acc.NoteRun(r);
            if (!string.IsNullOrEmpty(r.SessionId)) { _acc.DirectSession = r.SessionId; _acc.DirectDir = dir; }
            if (r.Ok)
            {
                if (_acc.LoginState != "in") _acc.LoginState = "in";
                _acc.AddStep(new Step("done", "完成　用时 " + took + (r.Model.Length > 0 ? "　模型 " + r.Model : "") + (r.Usage.Length > 0 ? "　用量 " + r.Usage : "")));
            }
            else if (r.Cancelled) _acc.AddStep(new Step("info", "已停止。"));
            else
            {
                if (r.RateLimited) _acc.CooldownUntil = DateTime.Now.AddMinutes(_host.Cfg.CooldownMinutes);
                _acc.DirectSession = "";   // 出错后下一条消息从新会话开始
                _acc.AddStep(new Step("error", r.Error));
            }
            Sessions.Touch(_acc, dir, text);   // 存进历史会话
            Refresh();
            _input.Focus();
        }
    }

    // 把一个账号的对话界面放进单独的窗口（“独立窗口”）
    public class AgentWindow
    {
        public readonly Window Win;
        public readonly AgentView View;
        public bool Closing;

        public AgentWindow(Account acc, IAgentHost host)
        {
            Win = new Window();
            Win.Width = 760; Win.Height = 640; Win.MinWidth = 420; Win.MinHeight = 320;
            Win.Background = Brushes.White;
            Win.FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
            Win.FontSize = 14; Win.UseLayoutRounding = true; Win.SnapsToDevicePixels = true;
            Win.SetBinding(Window.TitleProperty, new Binding("Display") { Source = acc, StringFormat = "{0} — Agent 窗口" });
            View = new AgentView(acc, host, false);
            Win.Content = View.Root;
            Win.Closing += (s, e) => { if (!Closing) { e.Cancel = true; Win.Hide(); } };
            Win.Closed += (s, e) => View.Dispose();
        }
    }
}
