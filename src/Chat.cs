using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace AC
{
    // 对话界面用到的颜色和字体
    public static class Look
    {
        public static Brush Make(string hex)
        {
            var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
            b.Freeze();
            return b;
        }
        public static readonly Brush Ink = Make("#1A1A1A");
        public static readonly Brush Mid = Make("#6F6F6F");
        public static readonly Brush Grey = Make("#8C8C8C");
        public static readonly Brush Line = Make("#E6E6E6");
        public static readonly Brush Soft = Make("#F4F4F2");
        public static readonly Brush Bubble = Make("#F1F1EF");
        public static readonly Brush Accent = Make("#D97757");
        public static readonly Brush Red = Make("#CF222E");
        public static readonly Brush DoneFill = Make("#5E5E5E");
        public static readonly Brush Ring = Make("#D0D0D0");
        public static readonly FontFamily Mono = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI");
        public const double Body = 15;
        public const double LineH = 24;

        // 推理强度显示成中文，和 Claude 窗口里的叫法一致
        public static string EffortLabel(string e)
        {
            switch ((e ?? "").ToLowerInvariant())
            {
                case "": return "默认强度";
                case "minimal": return "最低";
                case "low": return "低";
                case "medium": return "中";
                case "high": return "高";
                case "xhigh": return "超高";
                case "max": return "最高";
                case "ultra": return "极限";
                default: return e;
            }
        }

        // 模型显示成好认的名字：claude-fable-5-2 → Fable 5.2，gpt-6-luna → GPT-6-Luna
        public static string ModelLabel(string id)
        {
            if (string.IsNullOrEmpty(id)) return "默认模型";
            var parts = id.Split('-');
            if (parts.Length >= 3 && parts[0] == "claude")
            {
                string ver = "";
                for (int i = 2; i < parts.Length; i++) ver += (i > 2 ? "." : "") + parts[i];
                return char.ToUpperInvariant(parts[1][0]) + parts[1].Substring(1) + " " + ver;
            }
            if (parts.Length >= 2 && parts[0] == "gpt")
            {
                string name = "GPT";
                for (int i = 1; i < parts.Length; i++)
                    name += "-" + (parts[i].Length > 0 && char.IsLetter(parts[i][0]) ? char.ToUpperInvariant(parts[i][0]) + parts[i].Substring(1) : parts[i]);
                return name;
            }
            return id;
        }

        // 账号头像：一个带颜色的圆角方块加一个字母，右下角叠一个状态圆点
        public static FrameworkElement Avatar(Account acc, double size)
        {
            var g = new Grid(); g.Width = size; g.Height = size; g.VerticalAlignment = VerticalAlignment.Center;
            var box = new Border(); box.CornerRadius = new CornerRadius(size * 0.28); box.Background = Make(acc.AvatarColor);
            var letter = Text(acc.AvatarText, Brushes.White, size * 0.5); letter.FontWeight = FontWeights.SemiBold; letter.HorizontalAlignment = HorizontalAlignment.Center;
            box.Child = letter;
            var dot = new Ellipse(); dot.Width = size * 0.36; dot.Height = size * 0.36; dot.Stroke = Brushes.White; dot.StrokeThickness = 1.6;
            dot.HorizontalAlignment = HorizontalAlignment.Right; dot.VerticalAlignment = VerticalAlignment.Bottom; dot.Margin = new Thickness(0, 0, -3, -3);
            dot.SetBinding(Ellipse.FillProperty, new System.Windows.Data.Binding("DotColor") { Source = acc });
            g.Children.Add(box); g.Children.Add(dot);
            return g;
        }

        public static TextBlock Text(string text, Brush color, double size)
        {
            var t = new TextBlock();
            t.Text = text; t.Foreground = color; t.FontSize = size;
            t.VerticalAlignment = VerticalAlignment.Center;
            return t;
        }
    }

    public static class Icons
    {
        static Path Stroke(string data, Brush brush, double thickness)
        {
            var p = new Path();
            p.Data = Geometry.Parse(data); p.Stroke = brush; p.StrokeThickness = thickness;
            p.Width = 16; p.Height = 16;
            p.StrokeStartLineCap = PenLineCap.Round; p.StrokeEndLineCap = PenLineCap.Round; p.StrokeLineJoin = PenLineJoin.Round;
            return p;
        }

        // 任务状态图标：完成＝实心圆打勾，进行中＝转动的细圆环，等待＝虚线圆
        public static FrameworkElement For(string state)
        {
            var g = new Grid();
            g.Width = 16; g.Height = 16; g.VerticalAlignment = VerticalAlignment.Center;
            g.LayoutTransform = new ScaleTransform(0.875, 0.875);   // 画在 16 的格子里，显示成 14
            if (state == "done")
            {
                var e = new Ellipse(); e.Fill = Look.DoneFill; g.Children.Add(e);
                g.Children.Add(Stroke("M4.5,8.3 L7,10.8 L11.5,5.7", Brushes.White, 1.6));
            }
            else if (state == "running")
            {
                var e = new Ellipse(); e.Stroke = Look.Ring; e.StrokeThickness = 1.5; e.Margin = new Thickness(0.75); g.Children.Add(e);
                var arc = Stroke("M8,1.5 A6.5,6.5 0 0 1 14.5,8", Look.Mid, 1.5);
                arc.RenderTransformOrigin = new Point(0.5, 0.5);
                var rot = new RotateTransform(); arc.RenderTransform = rot;
                var spin = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(1.1)));
                spin.RepeatBehavior = RepeatBehavior.Forever;
                rot.BeginAnimation(RotateTransform.AngleProperty, spin);
                g.Children.Add(arc);
            }
            else if (state == "failed")
            {
                var e = new Ellipse(); e.Stroke = Look.Red; e.StrokeThickness = 1.4; e.Margin = new Thickness(0.7); g.Children.Add(e);
                g.Children.Add(Stroke("M5.6,5.6 L10.4,10.4 M10.4,5.6 L5.6,10.4", Look.Red, 1.5));
            }
            else
            {
                var e = new Ellipse(); e.Stroke = Look.Grey; e.StrokeThickness = 1.3; e.Margin = new Thickness(0.65);
                e.StrokeDashArray = new DoubleCollection(new[] { 2.2, 2.2 });
                g.Children.Add(e);
                if (state == "skipped") g.Children.Add(Stroke("M5.5,8 L10.5,8", Look.Grey, 1.4));
            }
            return g;
        }

        // 状态行前面的橙色跳动小点
        public static FrameworkElement Dots()
        {
            var c = new Canvas();
            c.Width = 18; c.Height = 16; c.VerticalAlignment = VerticalAlignment.Center;
            double[][] at = { new[] { 1.0, 3.0 }, new[] { 7.0, 6.5 }, new[] { 2.5, 10.0 }, new[] { 12.5, 9.5 } };
            for (int i = 0; i < at.Length; i++)
            {
                var e = new Ellipse(); e.Width = 3.4; e.Height = 3.4; e.Fill = Look.Accent;
                Canvas.SetLeft(e, at[i][0]); Canvas.SetTop(e, at[i][1]);
                var a = new DoubleAnimation(1, 0.25, new Duration(TimeSpan.FromSeconds(0.7)));
                a.AutoReverse = true; a.RepeatBehavior = RepeatBehavior.Forever; a.BeginTime = TimeSpan.FromSeconds(i * 0.18);
                e.BeginAnimation(UIElement.OpacityProperty, a);
                c.Children.Add(e);
            }
            return c;
        }

        public static FrameworkElement Send()
        {
            var g = new Grid(); g.Width = 28; g.Height = 28;
            var e = new Ellipse(); e.Fill = Look.Ink; g.Children.Add(e);
            var p = new Path();
            p.Data = Geometry.Parse("M14,20 L14,8.5 M9.2,13 L14,8.2 L18.8,13");
            p.Stroke = Brushes.White; p.StrokeThickness = 1.8;
            p.StrokeStartLineCap = PenLineCap.Round; p.StrokeEndLineCap = PenLineCap.Round; p.StrokeLineJoin = PenLineJoin.Round;
            g.Children.Add(p);
            return g;
        }

        public static FrameworkElement Stop()
        {
            var g = new Grid(); g.Width = 28; g.Height = 28;
            var r = new Rectangle(); r.Width = 13; r.Height = 13; r.RadiusX = 3; r.RadiusY = 3; r.Fill = Look.Ink;
            g.Children.Add(r);
            return g;
        }
    }

    // 把模型回复里常见的 Markdown 排成可读的样子：标题、加粗、列表、表格、代码
    public static class Md
    {
        static readonly Regex Numbered = new Regex(@"^(\d+)[\.、\)]\s+(.*)$", RegexOptions.Compiled);

        static readonly Regex LinkRx = new Regex(@"\[([^\]\[]+)\]\(([^)\s]+)\)", RegexOptions.Compiled);

        static void Inline(TextBlock tb, string text)
        {
            // [文件名](很长的路径) 这种链接只显示文件名，按代码样式标出来
            text = LinkRx.Replace(text, m => "`" + m.Groups[1].Value.Trim('`') + "`");
            var buf = new StringBuilder();
            bool bold = false, code = false;
            Action flush = () =>
            {
                if (buf.Length == 0) return;
                var run = new Run(buf.ToString());
                if (bold) run.FontWeight = FontWeights.SemiBold;
                if (code) { run.FontFamily = Look.Mono; run.FontSize = tb.FontSize - 1.5; run.Background = Look.Soft; }
                tb.Inlines.Add(run);
                buf.Length = 0;
            };
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '`') { flush(); code = !code; continue; }
                if (!code && c == '*' && i + 1 < text.Length && text[i + 1] == '*') { flush(); bold = !bold; i++; continue; }
                buf.Append(c);
            }
            flush();
        }

        static TextBlock Para(string text, double size, bool bold)
        {
            var tb = new TextBlock();
            tb.TextWrapping = TextWrapping.Wrap; tb.FontSize = size; tb.LineHeight = Math.Round(size * 1.6);
            tb.Foreground = Look.Ink;
            if (bold) tb.FontWeight = FontWeights.SemiBold;
            Inline(tb, text);
            return tb;
        }

        static FrameworkElement Table(List<string> rows)
        {
            var grid = new Grid(); grid.Margin = new Thickness(0, 8, 0, 4);
            int r = 0, cols = 0;
            foreach (var row in rows)
            {
                var cells = row.Trim().Trim('|').Split('|');
                if (Regex.IsMatch(row, @"^[\s\|\-:]+$")) continue;
                while (cols < cells.Length) { grid.ColumnDefinitions.Add(new ColumnDefinition()); cols++; }
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                for (int c = 0; c < cells.Length; c++)
                {
                    var cell = new Border();
                    cell.BorderBrush = Look.Line; cell.BorderThickness = new Thickness(0, 0, 0, 1); cell.Padding = new Thickness(0, 6, 12, 6);
                    cell.Child = Para(cells[c].Trim(), 14, r == 0);
                    Grid.SetRow(cell, r); Grid.SetColumn(cell, c);
                    grid.Children.Add(cell);
                }
                r++;
            }
            return grid;
        }

        public static FrameworkElement Render(string text)
        {
            var panel = new StackPanel();
            panel.MaxWidth = 690; panel.HorizontalAlignment = HorizontalAlignment.Left;
            bool inCode = false, gap = false;
            StringBuilder codeBuf = null;
            List<string> table = null;
            foreach (var raw in (text ?? "").Replace("\r", "").Split('\n'))
            {
                var line = raw.TrimEnd();
                var t = line.Trim();
                if (t.StartsWith("```"))
                {
                    if (!inCode) { inCode = true; codeBuf = new StringBuilder(); }
                    else
                    {
                        inCode = false;
                        var box = new Border();
                        box.Background = Look.Soft; box.CornerRadius = new CornerRadius(8); box.Padding = new Thickness(12, 8, 12, 8); box.Margin = new Thickness(0, 8, 0, 4);
                        var ct = new TextBlock(); ct.Text = codeBuf.ToString().TrimEnd(); ct.FontFamily = Look.Mono; ct.FontSize = 13; ct.TextWrapping = TextWrapping.Wrap; ct.Foreground = Look.Ink;
                        box.Child = ct; panel.Children.Add(box);
                    }
                    continue;
                }
                if (inCode) { codeBuf.AppendLine(raw); continue; }
                if (t.Length > 1 && t.StartsWith("|") && t.EndsWith("|")) { if (table == null) table = new List<string>(); table.Add(t); continue; }
                if (table != null) { panel.Children.Add(Table(table)); table = null; }
                if (t.Length == 0) { gap = true; continue; }

                FrameworkElement el;
                var num = Numbered.Match(t);
                if (t.StartsWith("#"))
                {
                    int level = 0; while (level < t.Length && t[level] == '#') level++;
                    var h = Para(t.Substring(level).Trim(), level <= 1 ? 18 : level == 2 ? 16.5 : 15, true);
                    h.Margin = new Thickness(0, panel.Children.Count == 0 ? 0 : 14, 0, 2);
                    el = h; gap = false;
                }
                else if (t.StartsWith("- ") || t.StartsWith("* ") || t.StartsWith("• ") || num.Success)
                {
                    var g = new Grid();
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(num.Success ? 26 : 18) });
                    g.ColumnDefinitions.Add(new ColumnDefinition());
                    var mark = Para(num.Success ? num.Groups[1].Value + "." : "•", Look.Body, false);
                    var body = Para(num.Success ? num.Groups[2].Value : t.Substring(2).Trim(), Look.Body, false);
                    Grid.SetColumn(body, 1);
                    g.Children.Add(mark); g.Children.Add(body);
                    g.Margin = new Thickness(line.StartsWith("  ") ? 20 : 2, gap ? 8 : 2, 0, 0);
                    el = g; gap = false;
                }
                else
                {
                    var p = Para(t, Look.Body, false);
                    p.Margin = new Thickness(0, gap && panel.Children.Count > 0 ? 12 : 0, 0, 0);
                    el = p; gap = false;
                }
                panel.Children.Add(el);
            }
            if (table != null) panel.Children.Add(Table(table));
            if (inCode && codeBuf != null && codeBuf.Length > 0) panel.Children.Add(Para(codeBuf.ToString(), 13, false));
            return panel;
        }
    }

    // 点右下角的圆环弹出的用量面板：套餐限额（哪一项、用了百分之几、什么时候重置）和经由本程序的用量
    public static class UsagePopup
    {
        static FrameworkElement Row(LimitInfo l)
        {
            var sp = new StackPanel(); sp.Margin = new Thickness(0, 12, 0, 0);
            var line = new DockPanel(); line.LastChildFill = false;
            var pct = Look.Text(l.Percent.ToString("0") + "%", Look.Mid, 13); pct.Margin = new Thickness(10, 0, 0, 0);
            DockPanel.SetDock(pct, Dock.Right);
            var reset = Look.Text(l.Reset, Look.Grey, 12.5);
            DockPanel.SetDock(reset, Dock.Right);
            line.Children.Add(pct); line.Children.Add(reset); line.Children.Add(Look.Text(l.Name, Look.Ink, 13.5));
            double p = Math.Max(0, Math.Min(100, l.Percent));
            var track = new Grid(); track.Height = 4; track.Margin = new Thickness(0, 7, 0, 0);
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(p, 0.01), GridUnitType.Star) });
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(100 - p, 0.01), GridUnitType.Star) });
            var bg = new Border(); bg.Background = Look.Make("#EDEDED"); bg.CornerRadius = new CornerRadius(2); Grid.SetColumnSpan(bg, 2);
            var bar = new Border(); bar.Background = p >= 90 ? Look.Red : Look.Make("#2F6FEB"); bar.CornerRadius = new CornerRadius(2);
            track.Children.Add(bg); track.Children.Add(bar);
            sp.Children.Add(line); sp.Children.Add(track);
            return sp;
        }

        static TextBlock Note(string text)
        {
            var t = Look.Text(text, Look.Mid, 12.5);
            t.TextWrapping = TextWrapping.Wrap; t.LineHeight = 19; t.Margin = new Thickness(0, 10, 0, 0);
            return t;
        }

        public static System.Windows.Controls.Primitives.Popup Show(UIElement target, Account acc)
        {
            if (acc == null) return null;
            var pop = new System.Windows.Controls.Primitives.Popup();
            pop.PlacementTarget = target; pop.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
            pop.StaysOpen = false; pop.AllowsTransparency = true; pop.HorizontalOffset = -330; pop.VerticalOffset = -4;
            var card = new Border();
            card.Background = Brushes.White; card.BorderBrush = Look.Line; card.BorderThickness = new Thickness(1); card.CornerRadius = new CornerRadius(12);
            card.Padding = new Thickness(18, 15, 18, 16); card.Width = 350; card.Margin = new Thickness(14);
            var shadow = new System.Windows.Media.Effects.DropShadowEffect();
            shadow.BlurRadius = 18; shadow.ShadowDepth = 3; shadow.Direction = 270; shadow.Opacity = 0.14; shadow.Color = Colors.Black;
            card.Effect = shadow;
            var sp = new StackPanel();

            var head = new DockPanel(); head.LastChildFill = false;
            string url = acc.Agent == "claude" ? "https://claude.ai/settings/usage" : acc.Agent == "codex" ? "https://chatgpt.com/codex/settings/usage" : null;
            if (url != null)
            {
                var all = Look.Text("查看全部 →", Look.Ink, 12.5); all.Cursor = Cursors.Hand; all.ToolTip = "在浏览器里打开官方的用量页面";
                all.MouseLeftButtonUp += (s, e) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { } pop.IsOpen = false; };
                DockPanel.SetDock(all, Dock.Right);
                head.Children.Add(all);
            }
            head.Children.Add(Look.Text("套餐使用限额" + (acc.Plan.Length > 0 ? " · " + acc.Plan : ""), Look.Grey, 12.5));
            sp.Children.Add(head);

            if (acc.Limits.Count > 0)
            {
                foreach (var l in acc.Limits) sp.Children.Add(Row(l));
                sp.Children.Add(Note("更新于 " + acc.LimitsAt.ToString("H:mm") + "，数据由 " + acc.AgentName + " 的命令行在运行时报告。"));
            }
            else if (acc.Agent == "claude")
                sp.Children.Add(Note("还没有数据。Claude 的命令行在真实运行时才会报告限额：用这个账号运行一次之后，这里会显示 5 小时限额和每周限额。"));
            else
                sp.Children.Add(Note(acc.AgentName + " 的命令行不提供套餐限额的百分比。" + (url != null ? "点右上角的“查看全部”可以到官网的用量页面查看。" : "")));

            var sep = new Border(); sep.Height = 1; sep.Background = Look.Line; sep.Margin = new Thickness(0, 14, 0, 0);
            sp.Children.Add(sep);
            var t1 = Look.Text("经由 KMS多控的运行（本次打开以来）", Look.Grey, 12.5); t1.Margin = new Thickness(0, 12, 0, 0);
            sp.Children.Add(t1);
            var t2 = Look.Text(acc.RunCount == 0 ? "还没有运行过" : "运行 " + acc.RunCount + " 次　输入 " + acc.TotalIn.ToString("N0") + "　输出 " + acc.TotalOut.ToString("N0") + " tokens", Look.Ink, 13.5);
            t2.Margin = new Thickness(0, 5, 0, 0); t2.TextWrapping = TextWrapping.Wrap;
            sp.Children.Add(t2);
            if (acc.LastModel.Length > 0) sp.Children.Add(Note("最近一次：模型 " + acc.LastModel + (acc.LastUsage.Length > 0 ? "，" + acc.LastUsage : "")));

            card.Child = sp; pop.Child = card; pop.IsOpen = true;
            return pop;
        }
    }

    // 把一步一步的内容排成 Claude 的 Code 窗口那样：
    // Agent 说的话是正文，每个操作是一行可展开的灰字，派来的任务和你发的消息是右侧的气泡。
    public class StepList
    {
        public readonly StackPanel Panel = new StackPanel();
        Group _group;   // 正在累积的一组连续操作

        public void Clear() { Panel.Children.Clear(); _group = null; }

        public void Add(Step s)
        {
            if (s.Kind == "think") return;   // 思考过程不占地方，完整内容在日志里
            if (s.Kind == "tool")
            {
                // 连续的操作合并成一行汇总，比如“已运行 2 个命令, 已编辑 1 个文件”，点开看每一步
                if (_group == null) { _group = new Group(); Panel.Children.Add(_group.Root); }
                _group.Add(s);
                return;
            }
            _group = null;
            var el = Build(s);
            if (el == null) return;
            Panel.Children.Add(el);
            if (Panel.Children.Count > 700) Panel.Children.RemoveRange(0, 200);
        }

        class Group
        {
            public readonly StackPanel Root = new StackPanel();
            readonly List<Step> _steps = new List<Step>();
            readonly TextBlock _text, _chev;
            readonly Border _box = new Border();
            readonly StackPanel _inner = new StackPanel();
            bool _open;

            public Group()
            {
                Root.Margin = new Thickness(0, 8, 0, 8);
                var head = new DockPanel(); head.LastChildFill = false; head.Background = Brushes.Transparent; head.Cursor = Cursors.Hand;
                _text = Look.Text("", Look.Grey, Look.Body); _text.TextTrimming = TextTrimming.CharacterEllipsis;
                _chev = Look.Text("›", Look.Grey, 16); _chev.Margin = new Thickness(8, -2, 0, 0); _chev.RenderTransformOrigin = new Point(0.5, 0.55);
                head.Children.Add(_text); head.Children.Add(_chev);
                _box.BorderBrush = Look.Make("#ECECEA"); _box.BorderThickness = new Thickness(2, 0, 0, 0);
                _box.Padding = new Thickness(14, 2, 0, 2); _box.Margin = new Thickness(2, 6, 0, 2);
                _box.Child = _inner; _box.Visibility = Visibility.Collapsed;
                head.MouseLeftButtonUp += (o, e) =>
                {
                    _open = !_open;
                    if (_open) Fill();
                    _box.Visibility = _open ? Visibility.Visible : Visibility.Collapsed;
                    _chev.RenderTransform = _open ? new RotateTransform(90) : null;
                };
                Root.Children.Add(head); Root.Children.Add(_box);
            }

            public void Add(Step s)
            {
                _steps.Add(s);
                _text.Text = Summary();
                if (_open) _inner.Children.Add(ToolLine(s));
            }

            void Fill()
            {
                _inner.Children.Clear();
                foreach (var s in _steps) _inner.Children.Add(ToolLine(s));
            }

            string Summary()
            {
                if (_steps.Count == 1) return _steps[0].Text;
                int cmd = 0, edit = 0, read = 0, search = 0, other = 0;
                foreach (var s in _steps)
                {
                    var t = s.Text;
                    if (t.StartsWith("运行命令")) cmd++;
                    else if (t.StartsWith("编辑") || t.StartsWith("写入")) edit++;
                    else if (t.StartsWith("读取")) read++;
                    else if (t.StartsWith("搜索") || t.StartsWith("查找文件") || t.StartsWith("联网搜索") || t.StartsWith("打开网页")) search++;
                    else other++;
                }
                var parts = new List<string>();
                if (cmd > 0) parts.Add("已运行 " + cmd + " 个命令");
                if (edit > 0) parts.Add("已编辑 " + edit + " 个文件");
                if (read > 0) parts.Add("已读取 " + read + " 个文件");
                if (search > 0) parts.Add("已搜索 " + search + " 次");
                if (other > 0) parts.Add("其他 " + other + " 个操作");
                return string.Join(", ", parts.ToArray());
            }
        }

        static TextBlock Wrapped(string text, Brush color, double size)
        {
            var t = Look.Text(text, color, size);
            t.TextWrapping = TextWrapping.Wrap; t.LineHeight = Math.Round(size * 1.55);
            return t;
        }

        static string Short(string s, int max)
        {
            s = (s ?? "").Replace("\r", "").Trim();
            return s.Length > max ? s.Substring(0, max) + "…" : s;
        }

        static FrameworkElement Bubble(string caption, string text, string body)
        {
            var b = new Border();
            b.Background = Look.Bubble; b.CornerRadius = new CornerRadius(16); b.Padding = new Thickness(15, 9, 15, 10);
            b.HorizontalAlignment = HorizontalAlignment.Right; b.MaxWidth = 540; b.Margin = new Thickness(40, 14, 0, 12);
            var sp = new StackPanel();
            if (caption != null) { var c = Look.Text(caption, Look.Grey, 12); c.Margin = new Thickness(0, 0, 0, 3); sp.Children.Add(c); }
            sp.Children.Add(Wrapped(text, Look.Ink, Look.Body));
            if (!string.IsNullOrEmpty(body)) { var d = Wrapped(Short(body, 420), Look.Mid, 13); d.Margin = new Thickness(0, 5, 0, 0); sp.Children.Add(d); }
            b.Child = sp;
            return b;
        }

        // 一个操作：一行灰字，点一下展开命令和结果
        static FrameworkElement ToolLine(Step s)
        {
            var root = new StackPanel(); root.Margin = new Thickness(0, 3, 0, 3);
            var head = new DockPanel(); head.LastChildFill = false; head.Background = Brushes.Transparent; head.Cursor = Cursors.Hand;
            var text = Look.Text(s.Text, Look.Grey, Look.Body); text.TextTrimming = TextTrimming.CharacterEllipsis;
            var chev = Look.Text("›", Look.Grey, 16); chev.Margin = new Thickness(8, -2, 0, 0); chev.RenderTransformOrigin = new Point(0.5, 0.55);
            head.Children.Add(text); head.Children.Add(chev);
            var box = new Border();
            box.Background = Look.Make("#FAFAF8"); box.BorderBrush = Look.Make("#ECECEA"); box.BorderThickness = new Thickness(1);
            box.CornerRadius = new CornerRadius(8); box.Padding = new Thickness(12, 8, 8, 8); box.Margin = new Thickness(0, 6, 0, 6);
            box.Visibility = Visibility.Collapsed;
            var detail = new TextBox();
            detail.IsReadOnly = true; detail.TextWrapping = TextWrapping.Wrap; detail.BorderThickness = new Thickness(0);
            detail.Background = Brushes.Transparent; detail.Foreground = Look.Ink; detail.Padding = new Thickness(0);
            detail.FontFamily = Look.Mono; detail.FontSize = 12.5; detail.MaxHeight = 240;
            detail.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            box.Child = detail;
            head.MouseLeftButtonUp += (o, e) =>
            {
                bool open = box.Visibility != Visibility.Visible;
                if (open) detail.Text = string.IsNullOrEmpty(s.Detail) ? "这一步没有更多细节。" : s.Detail;   // 结果可能是后来才补上的，展开时再取
                box.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                chev.RenderTransform = open ? new RotateTransform(90) : null;
                e.Handled = true;
            };
            root.Children.Add(head); root.Children.Add(box);
            return root;
        }

        static FrameworkElement Build(Step s)
        {
            switch (s.Kind)
            {
                case "say":
                {
                    var el = Md.Render(s.Text);
                    el.Margin = new Thickness(0, 8, 0, 8);
                    return el;
                }
                case "task": return Bubble("总指挥派来的任务", s.Text, s.Detail);
                case "user": return Bubble(null, s.Text, null);
                case "tool": return ToolLine(s);
                case "think":
                {
                    var t = Wrapped(Short(s.Text.Replace("\n", " "), 240), Look.Grey, 13.5);
                    t.FontStyle = FontStyles.Italic; t.Margin = new Thickness(0, 4, 0, 4);
                    return t;
                }
                case "error":
                {
                    var t = Wrapped(s.Text, Look.Red, 14); t.Margin = new Thickness(0, 6, 0, 8);
                    return t;
                }
                case "done":
                {
                    var t = Wrapped(s.Text, Look.Grey, 13); t.Margin = new Thickness(0, 4, 0, 10);
                    return t;
                }
                default:
                {
                    var t = Wrapped(Short(s.Text, 600), Look.Grey, 12.5); t.Margin = new Thickness(0, 2, 0, 2);
                    return t;
                }
            }
        }
    }

    // 对话里的一行任务：卡片里的任务带状态图标；规划和验收是一行灰色小字。点一下展开它一步一步的过程。
    public class TaskRow
    {
        public readonly StackPanel Root = new StackPanel();
        readonly TaskItem _t;
        readonly bool _summary;
        readonly ContentControl _icon = new ContentControl();
        readonly TextBlock _title, _meta, _chev;
        readonly Border _detailBox = new Border();
        readonly StepList _steps = new StepList();
        readonly ScrollViewer _scroll = new ScrollViewer();
        string _iconState;
        bool _open;

        public TaskRow(TaskItem t, bool summary)
        {
            _t = t; _summary = summary;
            var head = new DockPanel();
            head.Background = Brushes.Transparent; head.Cursor = Cursors.Hand; head.LastChildFill = false;
            _title = Look.Text("", Look.Ink, Look.Body);
            _title.TextTrimming = TextTrimming.CharacterEllipsis;
            _meta = Look.Text("", Look.Grey, 13);
            _chev = Look.Text("›", Look.Grey, 16);
            _chev.Margin = new Thickness(8, -2, 0, 0);
            _chev.RenderTransformOrigin = new Point(0.5, 0.55);
            if (summary)
            {
                head.Margin = new Thickness(0, 10, 0, 10);
                head.Children.Add(_title); head.Children.Add(_chev);
            }
            else
            {
                head.Margin = new Thickness(0, 4, 0, 4);
                _icon.Margin = new Thickness(0, 1, 7, 0); _icon.Focusable = false;
                DockPanel.SetDock(_meta, Dock.Right);
                _meta.Margin = new Thickness(12, 0, 0, 0);
                head.Children.Add(_meta); head.Children.Add(_icon); head.Children.Add(_title);
            }
            head.MouseLeftButtonUp += (s, e) => Toggle();

            // 展开后：这个任务一步一步的回复和操作，和 Agent 自己窗口里看到的一样
            _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; _scroll.MaxHeight = 340; _scroll.Focusable = false;
            _scroll.Content = _steps.Panel;
            _detailBox.BorderBrush = Look.Make("#ECECEA"); _detailBox.BorderThickness = new Thickness(2, 0, 0, 0);
            _detailBox.Padding = new Thickness(14, 2, 6, 2);
            _detailBox.Margin = new Thickness(summary ? 2 : 6, 2, 0, 10);
            _detailBox.Child = _scroll; _detailBox.Visibility = Visibility.Collapsed;

            Root.Children.Add(head); Root.Children.Add(_detailBox);
            t.PropertyChanged += OnChanged;
            t.StepAdded += OnStep;
            Refresh();
        }

        void OnChanged(object s, PropertyChangedEventArgs e) { Refresh(); }

        // 卡片里只放模型名，括号里的推理强度留给展开后的输出和报告
        static string ShortModel(string model)
        {
            int i = model.IndexOf('（');
            return i > 0 ? model.Substring(0, i) : model;
        }

        void OnStep(TaskItem t, Step step)
        {
            if (!_open) return;
            _steps.Add(step);
            _scroll.ScrollToEnd();
        }

        void ShowLog()
        {
            _steps.Clear();
            if (_t.Kind == "task")
                _steps.Add(new Step("info", "任务说明：" + _t.Instructions + "\n指定工具：" + _t.AgentName + "　前置任务：" + (_t.DependsOn.Count == 0 ? "无" : _t.DepsText)
                    + "　修改范围：" + (_t.ReadOnly ? "只读，不改文件" : _t.Files.Count == 0 ? "未说明（不会与其他改文件的任务同时运行）" : string.Join("、", _t.Files.ToArray()))));
            if (_t.Steps.Count == 0) _steps.Add(new Step("info", "还没开始执行。"));
            // 只摆最近的一部分，完整内容在结果文件夹的日志里
            int from = Math.Max(0, _t.Steps.Count - 300);
            if (from > 0) _steps.Add(new Step("info", "…前面还有 " + from + " 步，完整内容见结果文件夹里的日志"));
            for (int i = from; i < _t.Steps.Count; i++) _steps.Add(_t.Steps[i]);
            _scroll.ScrollToEnd();
        }

        void Toggle()
        {
            _open = !_open;
            _detailBox.Visibility = _open ? Visibility.Visible : Visibility.Collapsed;
            _chev.RenderTransform = _open ? new RotateTransform(90) : null;
            if (_open) ShowLog();
        }

        void Refresh()
        {
            var state = _t.State;
            if (_summary)
            {
                string text;
                if (_t.Kind == "plan")
                    text = state == "done" ? "总指挥已拆分任务：" + _t.Status.Replace("完成，", "")
                         : state == "failed" ? (_t.Status == "已停止" ? "拆分任务时被停止" : "拆分任务没有成功")
                         : "总指挥正在查看项目并拆分任务";
                else
                    text = state == "done" ? "总指挥已完成验收"
                         : state == "failed" ? (_t.Status == "已停止" ? "验收时被停止" : "验收没有成功")
                         : state == "skipped" ? "验收" + _t.Status
                         : "总指挥正在验收";
                if (state == "running" && !string.IsNullOrEmpty(_t.Elapsed)) text += "　" + _t.Elapsed;
                else if ((state == "done" || state == "failed") && !string.IsNullOrEmpty(_t.AccountLabel)) text += "　" + _t.AccountLabel + "　" + _t.Elapsed;
                _title.Text = text;
                _title.Foreground = state == "failed" ? Look.Red : Look.Grey;
                return;
            }
            if (_iconState != state) { _iconState = state; _icon.Content = Icons.For(state); }
            _title.Text = _t.Id + "　" + _t.Title;
            _title.Foreground = state == "done" || state == "skipped" ? Look.Mid : Look.Ink;
            string meta;
            if (state == "pending") meta = _t.Status == "等待" ? "" : _t.Status;
            else if (state == "running") meta = _t.AccountLabel + "　" + _t.Elapsed;
            else if (state == "done") meta = _t.AccountLabel + (_t.Model.Length > 0 ? " · " + ShortModel(_t.Model) : "") + "　" + _t.Elapsed;
            else meta = _t.Status + (string.IsNullOrEmpty(_t.AccountLabel) ? "" : "　" + _t.AccountLabel);
            _meta.Text = meta;
            _meta.Foreground = state == "failed" ? Look.Red : Look.Grey;
        }
    }
}
