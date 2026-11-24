using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace AC
{
    public class NotifyBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void Raise(params string[] names)
        {
            var h = PropertyChanged;
            if (h == null) return;
            foreach (var n in names) h(this, new PropertyChangedEventArgs(n));
        }
    }

    public static class Json
    {
        static JavaScriptSerializer New()
        {
            var s = new JavaScriptSerializer();
            s.MaxJsonLength = int.MaxValue;
            return s;
        }

        public static Dictionary<string, object> Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            try { return New().Deserialize<Dictionary<string, object>>(text); }
            catch { return null; }
        }

        public static T Load<T>(string text) where T : class
        {
            try { return New().Deserialize<T>(text); }
            catch { return null; }
        }

        public static string Save(object o) { return New().Serialize(o); }

        // 解析任意 JSON：对象得到 Dictionary，数组得到 object[]
        public static object Any(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            try { return New().DeserializeObject(text); }
            catch { return null; }
        }

        public static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return null;
            var s = v as string;
            if (s != null) return s;
            if (v is IDictionary || v is IList) return New().Serialize(v);
            return Convert.ToString(v);
        }

        public static Dictionary<string, object> Obj(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v)) return null;
            return v as Dictionary<string, object>;
        }

        public static IList Arr(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v)) return null;
            return v as IList;
        }

        public static bool Bool(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return false;
            if (v is bool) return (bool)v;
            return string.Equals(Convert.ToString(v), "true", StringComparison.OrdinalIgnoreCase);
        }

        // 从模型回复里取出第一个完整的 JSON 对象（允许前后有说明文字或 ``` 包裹）
        public static Dictionary<string, object> Extract(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            int from = 0;
            while (true)
            {
                int start = text.IndexOf('{', from);
                if (start < 0) return null;
                int depth = 0; bool inStr = false; bool esc = false;
                for (int i = start; i < text.Length; i++)
                {
                    char c = text[i];
                    if (inStr)
                    {
                        if (esc) esc = false;
                        else if (c == '\\') esc = true;
                        else if (c == '"') inStr = false;
                        continue;
                    }
                    if (c == '"') inStr = true;
                    else if (c == '{') depth++;
                    else if (c == '}')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            var d = Parse(text.Substring(start, i - start + 1));
                            if (d != null) return d;
                            break;
                        }
                    }
                }
                from = start + 1;
            }
        }
    }

    // 一个账号 = 一种命令行工具 + 一份独立的登录
    public class Account : NotifyBase
    {
        string _label = "";
        string _model = "";
        int _maxParallel = 1;
        bool _enabled = true;
        int _running;
        DateTime _cooldown = DateTime.MinValue;
        string _lastTest = "";
        string _current = "";

        public string Id { get; set; }
        public string Agent { get; set; }
        public bool UseDefaultLogin { get; set; }
        public string ProfileDir { get; set; }
        // 每个账号自己的工作文件夹和执行权限：多个账号各干各的活时，互不影响
        public string ProjectDir { get; set; }
        public string Perm { get; set; }

        public string Label
        {
            get { return _label; }
            set { _label = value ?? ""; Raise("Label", "Display"); }
        }
        public string Model
        {
            get { return _model; }
            set { _model = (value ?? "").Trim(); Raise("Model", "SubText"); }
        }
        // 推理强度：留空表示用该工具自己的默认值
        public string Effort
        {
            get { return _effort; }
            set { _effort = (value ?? "").Trim(); Raise("Effort"); }
        }
        public int MaxParallel
        {
            get { return _maxParallel; }
            set { _maxParallel = value < 1 ? 1 : (value > 8 ? 8 : value); Raise("MaxParallel"); }
        }
        public bool Enabled
        {
            get { return _enabled; }
            set { _enabled = value; Raise("Enabled", "StatusText", "DotColor"); }
        }

        [ScriptIgnore] public int Running
        {
            get { return _running; }
            set { _running = value < 0 ? 0 : value; Raise("Running", "StatusText", "DotColor"); }
        }
        [ScriptIgnore] public DateTime CooldownUntil
        {
            get { return _cooldown; }
            set { _cooldown = value; Raise("StatusText", "DotColor"); }
        }

        // ---- 登录状态和账号身份：由官方命令行自己报告，本程序只负责显示 ----
        string _effort = "", _loginState = "unknown", _identity = "", _plan = "", _lastModel = "", _lastUsage = "";

        [ScriptIgnore] public string LoginState    // unknown | checking | in | out | nocli
        {
            get { return _loginState; }
            set { _loginState = value ?? "unknown"; Raise("LoginState", "LoginLine", "SubText", "DotColor"); }
        }
        [ScriptIgnore] public string Identity
        {
            get { return _identity; }
            set { _identity = value ?? ""; Raise("Identity", "LoginLine", "SubText"); }
        }
        [ScriptIgnore] public string Plan
        {
            get { return _plan; }
            set { _plan = value ?? ""; Raise("Plan", "LoginLine"); }
        }
        // 最近一次真实运行时，命令行自己报告的模型和用量
        [ScriptIgnore] public string LastModel
        {
            get { return _lastModel; }
            set { _lastModel = value ?? ""; Raise("LastModel", "SubText", "ModelLine"); }
        }
        [ScriptIgnore] public string LastUsage
        {
            get { return _lastUsage; }
            set { _lastUsage = value ?? ""; Raise("LastUsage", "ModelLine"); }
        }
        [ScriptIgnore] public string LoginLine
        {
            get
            {
                switch (_loginState)
                {
                    case "in": return "已登录" + (_identity.Length > 0 ? "：" + _identity : "") + (_plan.Length > 0 ? "（" + _plan + "）" : "");
                    case "out": return "未登录。点“登录”，在弹出的窗口里登录。";
                    case "checking": return "正在向命令行查询…";
                    case "nocli": return "电脑上还没有这个工具的命令行程序。";
                    default: return "这个工具没有查询登录状态的命令，请用“测试连接”确认。";
                }
            }
        }
        [ScriptIgnore] public string DirLine
        {
            get { return UseDefaultLogin ? "沿用这台电脑上该工具原有的登录" : (ProfileDir ?? ""); }
        }
        [ScriptIgnore] public string ModelLine
        {
            get
            {
                if (_lastModel.Length == 0) return "还没有真实运行记录。点“测试连接”会真实调用一次模型。";
                return "最近一次实际使用的模型：" + _lastModel + (_lastUsage.Length > 0 ? "　用量：" + _lastUsage : "");
            }
        }
        // 左侧菜单里账号名下面的那行小字
        [ScriptIgnore] public string SubText
        {
            get
            {
                string who = _loginState == "in" ? (_identity.Length > 0 ? _identity : "已登录")
                           : _loginState == "out" ? "未登录"
                           : _loginState == "checking" ? "查询中…"
                           : _loginState == "nocli" ? "未安装命令行" : "登录状态待确认";
                string m = _model.Length > 0 ? Look.ModelLabel(_model) : "默认模型";
                return who + " · " + m;
            }
        }
        [ScriptIgnore] public string DotColor
        {
            get
            {
                if (!_enabled) return "#C8C8C8";
                if (_running > 0) return "#D97757";
                if (CoolingDown) return "#D4A72C";
                if (_loginState == "in") return "#2DA44E";
                if (_loginState == "out" || _loginState == "nocli") return "#CF222E";
                return "#B5B5B5";
            }
        }
        [ScriptIgnore] public DateTime LastUsed { get; set; }
        [ScriptIgnore] public string LastTest
        {
            get { return _lastTest; }
            set { _lastTest = value ?? ""; Raise("LastTest"); }
        }
        [ScriptIgnore] public string CurrentTask
        {
            get { return _current; }
            set { _current = value ?? ""; Raise("CurrentTask"); }
        }
        [ScriptIgnore] public bool CoolingDown { get { return _cooldown > DateTime.Now; } }
        [ScriptIgnore] public string AgentName { get { return Cli.DisplayName(Agent); } }
        [ScriptIgnore] public string Display { get { return AgentName + " · " + Label; } }
        [ScriptIgnore] public string LoginText { get { return UseDefaultLogin ? "系统默认登录" : "独立登录目录"; } }
        [ScriptIgnore] public string StatusText
        {
            get
            {
                if (!_enabled) return "已停用";
                if (CoolingDown) return "冷却至 " + _cooldown.ToString("HH:mm");
                if (_running > 0) return "运行中 ×" + _running;
                return "空闲";
            }
        }
        public void RefreshStatus() { Raise("StatusText", "DotColor"); }

        // 这个账号窗口里显示的内容：一步一步的回复和操作
        [ScriptIgnore] public readonly List<Step> Steps = new List<Step>();
        public event Action<Step> StepAdded;
        public void AddStep(Step s)
        {
            if (s == null) return;
            Steps.Add(s);
            if (Steps.Count > 900) Steps.RemoveRange(0, 300);
            var h = StepAdded;
            if (h != null) h(s);
        }
        // 换一段会话（新会话，或者从历史里打开一段）：界面要整个重画
        public event Action StepsReset;
        public void ResetSteps(IEnumerable<Step> steps)
        {
            Steps.Clear();
            if (steps != null) Steps.AddRange(steps);
            var h = StepsReset;
            if (h != null) h();
        }
        [ScriptIgnore] public ChatSession Session;   // 当前这段会话；还没说过话时是 null
        [ScriptIgnore] public string AvatarText { get { return Agent == "claude" ? "C" : Agent == "codex" ? "X" : "G"; } }
        [ScriptIgnore] public string AvatarColor { get { return Agent == "claude" ? "#D97757" : Agent == "codex" ? "#1F1F1F" : "#3B82F6"; } }

        // 在这个账号的窗口里直接对话时，记住上一轮的会话，下一条消息接着说
        [ScriptIgnore] public string DirectSession = "";
        [ScriptIgnore] public string DirectDir = "";
        [ScriptIgnore] public DateTime RunStart = DateTime.Now;   // 当前这一轮是什么时候开始的，状态行用来计时

        // ---- 用量：套餐限额由命令行在运行时报告；累计数只算经由本程序的运行 ----
        [ScriptIgnore] public readonly List<LimitInfo> Limits = new List<LimitInfo>();
        [ScriptIgnore] public DateTime LimitsAt = DateTime.MinValue;
        [ScriptIgnore] public long TotalIn, TotalOut;
        [ScriptIgnore] public int RunCount;
        [ScriptIgnore] public double MaxLimitPercent
        {
            get { double m = 0; foreach (var l in Limits) if (l.Percent > m) m = l.Percent; return m; }
        }

        // 一次真实运行结束后，把它带回来的凭据记到账号上
        public void NoteRun(RunResult r)
        {
            if (r == null) return;
            if (!string.IsNullOrEmpty(r.Model)) LastModel = r.Model;
            if (!string.IsNullOrEmpty(r.Usage)) LastUsage = r.Usage;
            RunCount++; TotalIn += r.TokensIn; TotalOut += r.TokensOut;
            if (r.Limits.Count > 0)
            {
                foreach (var l in r.Limits) { Limits.RemoveAll(x => x.Name == l.Name); Limits.Add(l); }
                LimitsAt = DateTime.Now;
            }
        }
    }

    // 一项套餐限额：比如“5 小时限额 54%，6:50 重置”
    public class LimitInfo
    {
        public string Name = "";
        public double Percent;
        public string Reset = "";
    }

    // 真实应用多开里的一个窗口：官方桌面应用的一份独立副本，有自己的数据文件夹，所以登录的是它自己的账号
    public class AppWindow : NotifyBase
    {
        string _label = "";
        bool _running;
        public string Id { get; set; }
        public string Kind { get; set; }       // claude | codex | vm
        public string DataDir { get; set; }    // 留空表示应用原本的那一份；虚拟机条目也留空
        public string VmName { get; set; }     // Kind 是 vm 时：VirtualBox 里这台虚拟机的名字
        public string Label
        {
            get { return _label; }
            set { _label = value ?? ""; Raise("Label", "Display"); }
        }
        [ScriptIgnore] public bool Running
        {
            get { return _running; }
            set { if (_running != value) { _running = value; Raise("Running", "SubText", "DotColor"); } }
        }
        [ScriptIgnore] public bool IsVm { get { return Kind == "vm"; } }
        [ScriptIgnore] public string KindName { get { return IsVm ? "虚拟机" : Kind == "codex" ? "Codex 桌面版" : "Claude 桌面版"; } }
        [ScriptIgnore] public string Display { get { return _label; } }
        [ScriptIgnore] public string SubText { get { return KindName + " · " + (IsVm ? (_running ? "已开机" : "未开机") : (_running ? "已打开" : "未打开")); } }
        [ScriptIgnore] public string DotColor { get { return _running ? "#2DA44E" : "#B5B5B5"; } }
        [ScriptIgnore] public string AvatarText { get { return IsVm ? "V" : Kind == "codex" ? "X" : "C"; } }
        [ScriptIgnore] public string AvatarColor { get { return IsVm ? "#3B6FB6" : Kind == "codex" ? "#1F1F1F" : "#D97757"; } }
        [ScriptIgnore] public string DataDirText
        {
            get
            {
                if (IsVm) return "VirtualBox 虚拟机 " + VmName;
                return string.IsNullOrEmpty(DataDir) ? "应用原本的那一份（你平时用的窗口，不是 KMS多控创建的）" : DataDir;
            }
        }
    }

    // 保存在磁盘上的一段会话：标题、所在项目、可以接着说的会话编号，以及一步一步的内容
    public class StepData
    {
        public string K { get; set; }
        public string T { get; set; }
        public string D { get; set; }
    }

    public class ChatSession
    {
        public string Id { get; set; }
        public string AccountId { get; set; }
        public string Title { get; set; }
        public string Dir { get; set; }
        public string CliSession { get; set; }
        public string Created { get; set; }
        public string Updated { get; set; }
        public List<StepData> Steps { get; set; }
        public ChatSession() { Steps = new List<StepData>(); Title = ""; Dir = ""; CliSession = ""; Created = ""; Updated = ""; }
    }

    // 总指挥页的对话也要存盘：每一轮的目标、任务卡片（含每个任务一步一步的过程）、总指挥的回复
    public class SavedTask
    {
        public string Id { get; set; }
        public string Kind { get; set; }
        public string Title { get; set; }
        public string Instructions { get; set; }
        public string State { get; set; }
        public string Status { get; set; }
        public string Account { get; set; }
        public string Elapsed { get; set; }
        public string Model { get; set; }
        public string Usage { get; set; }
        public int Attempts { get; set; }
        public List<StepData> Steps { get; set; }
        public SavedTask() { Steps = new List<StepData>(); }
    }

    public class SavedRun
    {
        public string Goal { get; set; }
        public string Answer { get; set; }
        public string ProjectDir { get; set; }
        public string RunDir { get; set; }
        public string ReportFile { get; set; }
        public string When { get; set; }
        public List<SavedTask> Tasks { get; set; }
        public SavedRun() { Tasks = new List<SavedTask>(); }
    }

    public class SavedChat
    {
        public string Id { get; set; }
        public string Updated { get; set; }
        public List<SavedRun> Runs { get; set; }
        public SavedChat() { Runs = new List<SavedRun>(); }
    }

    public static class ChatStore
    {
        static string Folder { get { return Path.Combine(Store.DataDir, "commander"); } }

        public static SavedChat New()
        {
            var c = new SavedChat();
            c.Id = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 4);
            return c;
        }

        public static void Save(SavedChat c)
        {
            try
            {
                if (c == null || c.Runs.Count == 0) return;
                c.Updated = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                Directory.CreateDirectory(Folder);
                File.WriteAllText(Path.Combine(Folder, c.Id + ".json"), Json.Save(c), new UTF8Encoding(false));
            }
            catch (Exception ex) { Store.LogError("保存总指挥对话", ex); }
        }

        // 最近一次的对话，用来在重新打开时恢复
        public static SavedChat LoadLatest()
        {
            try
            {
                if (!Directory.Exists(Folder)) return null;
                SavedChat best = null;
                foreach (var f in Directory.GetFiles(Folder, "*.json"))
                {
                    var c = Json.Load<SavedChat>(File.ReadAllText(f, Encoding.UTF8));
                    if (c == null || c.Runs == null || c.Runs.Count == 0) continue;
                    if (best == null || string.CompareOrdinal(c.Updated ?? "", best.Updated ?? "") > 0) best = c;
                }
                return best;
            }
            catch (Exception ex) { Store.LogError("读取总指挥对话", ex); return null; }
        }

        public static SavedTask From(TaskItem t)
        {
            var s = new SavedTask();
            s.Id = t.Id; s.Kind = t.Kind; s.Title = t.Title; s.Instructions = t.Instructions; s.State = t.State; s.Status = t.Status;
            s.Account = t.AccountLabel; s.Elapsed = t.Elapsed; s.Model = t.Model; s.Usage = t.Usage; s.Attempts = t.Attempts;
            int from = Math.Max(0, t.Steps.Count - 400);
            for (int i = from; i < t.Steps.Count; i++)
            {
                var d = t.Steps[i].Detail ?? "";
                s.Steps.Add(new StepData { K = t.Steps[i].Kind, T = t.Steps[i].Text, D = d.Length > 6000 ? d.Substring(0, 6000) + "…" : d });
            }
            return s;
        }

        public static TaskItem ToTask(SavedTask s)
        {
            var t = new TaskItem();
            t.Id = s.Id; t.Kind = s.Kind ?? "task"; t.Title = s.Title ?? ""; t.Instructions = s.Instructions ?? "";
            t.AccountLabel = s.Account; t.Elapsed = s.Elapsed ?? ""; t.Model = s.Model ?? ""; t.Usage = s.Usage ?? ""; t.Attempts = s.Attempts;
            // 上次关闭时还没跑完的任务，恢复后标成“已中断”，不假装它还在运行
            bool unfinished = s.State == "running" || s.State == "pending";
            t.State = unfinished ? "skipped" : (s.State ?? "done");
            t.Status = unfinished ? "已中断（程序被关闭）" : (s.Status ?? "");
            if (s.Steps != null) foreach (var d in s.Steps) t.AddStep(new Step(d.K, d.T, string.IsNullOrEmpty(d.D) ? null : d.D));
            return t;
        }
    }

    // 历史会话：每个账号一个文件夹，每段会话一个文件，放在 data\sessions 下
    public static class Sessions
    {
        static string DirFor(string accountId) { return Path.Combine(Store.DataDir, "sessions", accountId); }
        static string Now() { return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"); }

        // 一轮运行结束后调用：没有会话就新建一段，然后把当前内容存盘
        public static void Touch(Account acc, string dir, string title)
        {
            try
            {
                if (acc.Steps.Count == 0) return;
                if (acc.Session == null)
                {
                    var s = new ChatSession();
                    s.Id = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 4);
                    s.AccountId = acc.Id; s.Created = Now();
                    title = (title ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
                    s.Title = title.Length > 48 ? title.Substring(0, 48) + "…" : (title.Length > 0 ? title : "未命名会话");
                    acc.Session = s;
                }
                var cur = acc.Session;
                cur.Dir = dir ?? cur.Dir; cur.CliSession = acc.DirectSession ?? ""; cur.Updated = Now();
                cur.Steps = new List<StepData>();
                foreach (var st in acc.Steps)
                {
                    var d = st.Detail ?? "";
                    cur.Steps.Add(new StepData { K = st.Kind, T = st.Text, D = d.Length > 6000 ? d.Substring(0, 6000) + "…" : d });
                }
                var folder = DirFor(acc.Id);
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, cur.Id + ".json"), Json.Save(cur), new UTF8Encoding(false));
            }
            catch (Exception ex) { Store.LogError("保存会话", ex); }
        }

        // 这个账号的全部历史会话，最近的在前
        public static List<ChatSession> List(string accountId)
        {
            var list = new List<ChatSession>();
            try
            {
                var folder = DirFor(accountId);
                if (!Directory.Exists(folder)) return list;
                foreach (var f in Directory.GetFiles(folder, "*.json"))
                {
                    var s = Json.Load<ChatSession>(File.ReadAllText(f, Encoding.UTF8));
                    if (s != null && !string.IsNullOrEmpty(s.Id)) { if (s.Steps == null) s.Steps = new List<StepData>(); list.Add(s); }
                }
                list.Sort((a, b) => string.CompareOrdinal(b.Updated ?? "", a.Updated ?? ""));
            }
            catch (Exception ex) { Store.LogError("读取历史会话", ex); }
            return list;
        }

        // 搜索：标题、项目文件夹或任何一步的内容里包含这些字
        public static bool Matches(ChatSession s, string query)
        {
            query = (query ?? "").Trim();
            if (query.Length == 0) return true;
            var cmp = StringComparison.OrdinalIgnoreCase;
            if ((s.Title ?? "").IndexOf(query, cmp) >= 0 || (s.Dir ?? "").IndexOf(query, cmp) >= 0) return true;
            foreach (var st in s.Steps) if ((st.T ?? "").IndexOf(query, cmp) >= 0) return true;
            return false;
        }

        public static void Delete(ChatSession s)
        {
            try { var f = Path.Combine(DirFor(s.AccountId), s.Id + ".json"); if (File.Exists(f)) File.Delete(f); }
            catch (Exception ex) { Store.LogError("删除会话", ex); }
        }

        // 打开一段历史会话：恢复内容，并记住它的会话编号，下一条消息接着那一段说
        public static void Open(Account acc, ChatSession s)
        {
            var steps = new List<Step>();
            foreach (var d in s.Steps) steps.Add(new Step(d.K, d.T, string.IsNullOrEmpty(d.D) ? null : d.D));
            acc.Session = s;
            acc.DirectSession = s.CliSession ?? ""; acc.DirectDir = s.Dir ?? "";
            acc.ResetSteps(steps);
        }

        public static void StartNew(Account acc)
        {
            acc.Session = null; acc.DirectSession = ""; acc.DirectDir = "";
            acc.ResetSteps(null);
        }
    }

    // 运行过程中的一步：Agent 说的一段话、做的一个操作，或一条状态
    public class Step
    {
        public string Kind;     // task | user | say | tool | think | info | error | done
        public string Text;
        public string Detail;   // 展开后显示的细节：命令的输出、任务说明等

        public Step(string kind, string text, string detail = null)
        {
            Kind = kind; Text = text ?? ""; Detail = detail;
        }

        // 写进日志文件时的样子
        public string ToLog()
        {
            string head = Kind == "say" ? "" : Kind == "tool" ? "▶ " : Kind == "task" ? "\n════ " : Kind == "user" ? "\n你：" : Kind == "error" ? "✖ "
                        : Kind == "done" ? "──── " : Kind == "think" ? "（思考）" : "· ";
            string detail = Kind == "tool" && !string.IsNullOrEmpty(Detail) ? "\n    " + Detail.Replace("\n", "\n    ") : "";
            return head + Text + detail + "\n";
        }
    }

    public class TaskItem : NotifyBase
    {
        string _status = "等待";
        string _state = "pending";
        string _account = "";
        string _elapsed = "";
        int _attempts;

        public string Id { get; set; }
        public string Kind { get; set; }          // plan | task | review
        public string Title { get; set; }
        public string Instructions { get; set; }
        public string Agent { get; set; }         // claude | codex | gemini | any
        public bool ReadOnly { get; set; }
        public List<string> DependsOn = new List<string>();
        public List<string> Files = new List<string>();
        public HashSet<string> Tried = new HashSet<string>();
        public DateTime Started;
        public DateTime Finished;
        public string Result = "";
        public string Model = "";     // 这次运行时命令行自己报告的模型
        public string Usage = "";     // 以及用量
        public readonly StringBuilder Log = new StringBuilder();
        public readonly List<Step> Steps = new List<Step>();
        public event Action<TaskItem, Step> StepAdded;

        public TaskItem() { Kind = "task"; Agent = "any"; Title = ""; Instructions = ""; }

        public string State   // pending | running | done | failed | skipped
        {
            get { return _state; }
            set { _state = value; Raise("State"); }
        }
        public string Status
        {
            get { return _status; }
            set { if (_status != value) { _status = value; Raise("Status"); } }
        }
        public string AccountLabel
        {
            get { return _account; }
            set { _account = value ?? ""; Raise("AccountLabel"); }
        }
        public string Elapsed
        {
            get { return _elapsed; }
            set { if (_elapsed != value) { _elapsed = value; Raise("Elapsed"); } }
        }
        public int Attempts
        {
            get { return _attempts; }
            set { _attempts = value; Raise("Attempts"); }
        }
        public string AgentName { get { return Agent == "any" ? "任意" : Cli.DisplayName(Agent); } }
        public string DepsText { get { return DependsOn.Count == 0 ? "" : string.Join("、", DependsOn.ToArray()); } }
        public bool Terminal { get { return _state == "done" || _state == "failed" || _state == "skipped"; } }

        public void AddStep(Step s)
        {
            if (s == null) return;
            Steps.Add(s);
            Log.Append(s.ToLog());
            var h = StepAdded;
            if (h != null) h(this, s);
        }
        public void Tick()
        {
            if (_state != "running") return;
            var span = DateTime.Now - Started;
            Elapsed = ((int)span.TotalMinutes).ToString() + ":" + span.Seconds.ToString("00");
        }
    }

    public class AppConfig
    {
        public List<Account> Accounts { get; set; }
        public List<AppWindow> Apps { get; set; }     // 真实应用多开的各个窗口
        public string WizardTarget { get; set; }      // 登录向导进行到一半时，记着是给哪个窗口登录
        public bool VmSeeded { get; set; }            // 已经把电脑上现有的虚拟机列进来过一次（之后新增的用“＋ 添加”）
        public string ClaudePath { get; set; }
        public string CodexPath { get; set; }
        public string GeminiPath { get; set; }
        public int MaxParallel { get; set; }
        public int MaxAttempts { get; set; }
        public int MaxTasks { get; set; }
        public int CooldownMinutes { get; set; }
        public int TimeoutMinutes { get; set; }
        public bool AutoOpenWindows { get; set; }
        public bool FinalReview { get; set; }
        public string Permission { get; set; }       // readonly | edit | full
        public string ChiefId { get; set; }
        public string LastProjectDir { get; set; }

        public AppConfig()
        {
            Accounts = new List<Account>();
            ClaudePath = ""; CodexPath = ""; GeminiPath = "";
            MaxParallel = 4; MaxAttempts = 3; MaxTasks = 8;
            CooldownMinutes = 30; TimeoutMinutes = 30;
            AutoOpenWindows = true; FinalReview = true;
            Permission = "edit"; ChiefId = ""; LastProjectDir = "";
        }

        public void Normalize()
        {
            if (Accounts == null) Accounts = new List<Account>();
            if (Apps == null) Apps = new List<AppWindow>();
            if (MaxParallel < 1) MaxParallel = 1; if (MaxParallel > 16) MaxParallel = 16;
            if (MaxAttempts < 1) MaxAttempts = 1; if (MaxAttempts > 6) MaxAttempts = 6;
            if (MaxTasks < 1) MaxTasks = 1; if (MaxTasks > 20) MaxTasks = 20;
            if (CooldownMinutes < 1) CooldownMinutes = 1;
            if (TimeoutMinutes < 1) TimeoutMinutes = 1;
            if (Permission != "readonly" && Permission != "full") Permission = "edit";
            ClaudePath = ClaudePath ?? ""; CodexPath = CodexPath ?? ""; GeminiPath = GeminiPath ?? "";
            ChiefId = ChiefId ?? ""; LastProjectDir = LastProjectDir ?? "";
            foreach (var a in Accounts)
            {
                if (string.IsNullOrEmpty(a.Id)) a.Id = Guid.NewGuid().ToString("N").Substring(0, 8);
                if (a.MaxParallel < 1) a.MaxParallel = 1;
                if (a.Perm != "readonly" && a.Perm != "full") a.Perm = "edit";
                if (a.ProjectDir == null) a.ProjectDir = "";
            }
        }
    }

    public static class Store
    {
        public static string DataDir;
        static string ConfigFile { get { return Path.Combine(DataDir, "config.json"); } }

        public static void Init(string overrideDir)
        {
            if (!string.IsNullOrEmpty(overrideDir))
            {
                DataDir = overrideDir;
            }
            else
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                // 1) exe 旁边放一个 datadir.txt（安装时写进去）：用它指定的目录，安装版和绿色版就能共用同一份登录数据
                string custom = null;
                try
                {
                    var pointer = Path.Combine(baseDir, "datadir.txt");
                    if (File.Exists(pointer)) custom = File.ReadAllText(pointer).Trim().Trim('"');
                }
                catch { }
                if (!string.IsNullOrEmpty(custom) && Directory.Exists(custom)) DataDir = custom;
                // 2) exe 旁边已经有 data：绿色版用法，数据跟着程序走
                else if (Directory.Exists(Path.Combine(baseDir, "data"))) DataDir = Path.Combine(baseDir, "data");
                // 3) 都没有：放到用户目录下，换安装位置/升级都不会丢登录
                else DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KMS", "data");
            }
            Directory.CreateDirectory(DataDir);
            Directory.CreateDirectory(Path.Combine(DataDir, "profiles"));
            Directory.CreateDirectory(Path.Combine(DataDir, "runs"));
        }

        public static AppConfig Load()
        {
            AppConfig cfg = null;
            try { if (File.Exists(ConfigFile)) cfg = Json.Load<AppConfig>(File.ReadAllText(ConfigFile, Encoding.UTF8)); }
            catch { }
            if (cfg == null) cfg = new AppConfig();
            cfg.Normalize();
            return cfg;
        }

        public static void Save(AppConfig cfg)
        {
            try
            {
                cfg.Normalize();
                var tmp = ConfigFile + ".tmp";
                File.WriteAllText(tmp, Json.Save(cfg), new UTF8Encoding(false));
                if (File.Exists(ConfigFile)) File.Delete(ConfigFile);
                File.Move(tmp, ConfigFile);
            }
            catch (Exception ex) { LogError("保存配置失败", ex); }
        }

        public static string NewProfileDir(string agent, string id)
        {
            var dir = Path.Combine(DataDir, "profiles", agent + "-" + id);
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static void LogError(string what, Exception ex)
        {
            try
            {
                File.AppendAllText(Path.Combine(DataDir, "error.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + what + ": " + ex + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch { }
        }
    }
}
