using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AC
{
    public delegate Task<RunResult> ExecFn(Account acc, string cwd, string prompt, string perm, Action<Step> onStep, CancellationToken ct);

    // 总指挥：规划 → 派活 → 验收。除了子进程的读写，其余逻辑都在界面线程上顺序执行，不需要加锁。
    public class Commander
    {
        public readonly AppConfig Cfg;
        public readonly ObservableCollection<TaskItem> Tasks = new ObservableCollection<TaskItem>();
        public readonly IList<Account> Accounts;
        public ExecFn Exec;
        public Action<Action> Post;                     // 把回调送回界面线程
        public event Action<string> Status;
        public event Action<Account, TaskItem> TaskStarted;

        public string Goal = "";
        public string ProjectDir = "";
        public string Perm = "edit";
        public bool Review = true;
        public Account Chief;
        public bool Running { get; private set; }
        public string RunDir { get; private set; }
        public string ReportFile { get; private set; }
        public string Report { get; private set; }

        CancellationTokenSource _cts;

        public Commander(AppConfig cfg, IList<Account> accounts)
        {
            Cfg = cfg; Accounts = accounts;
            Exec = (acc, cwd, prompt, perm, onText, ct) => Cli.Run(cfg, acc, cwd, prompt, perm, onText, ct);
            Post = a => a();
        }

        public string Context = "";                     // 同一段对话里此前的来回，供总指挥参考
        public string LastStatus { get; private set; }

        void Say(string s) { LastStatus = s; var h = Status; if (h != null) h(s); }

        void AppendContext(StringBuilder sb)
        {
            if (string.IsNullOrWhiteSpace(Context)) return;
            sb.AppendLine("【此前的对话（仅供了解背景，本次只需要完成下面的总目标）】");
            sb.AppendLine(Clip(Context, 6000));
            sb.AppendLine();
        }
        public void Stop() { if (_cts != null) _cts.Cancel(); }

        bool Usable(Account a) { return a.Enabled && Cli.Find(a.Agent, Cfg) != null; }

        // ---------- 提示词 ----------

        string PlanPrompt()
        {
            var counts = new Dictionary<string, int>();
            foreach (var a in Accounts) if (Usable(a)) { int n; counts.TryGetValue(a.Agent, out n); counts[a.Agent] = n + a.MaxParallel; }
            var who = string.Join("、", counts.Select(kv => kv.Key + "（可同时跑 " + kv.Value + " 个）").ToArray());
            var sb = new StringBuilder();
            sb.AppendLine("你是一个多 Agent 开发团队的总指挥。你现在只负责拆分任务，不要动手修改任何文件，也不要执行会改变项目的命令。");
            sb.AppendLine("可以先查看当前目录下的项目文件来了解情况：列目录、读文件这类只读操作是允许的。");
            sb.AppendLine("总目标里如果有“不要运行命令”之类的要求，那是针对执行者和最终成果的，不限制你做只读查看。");
            sb.AppendLine();
            AppendContext(sb);
            sb.AppendLine("【总目标】");
            sb.AppendLine(Goal.Trim());
            sb.AppendLine();
            sb.AppendLine("【可用的执行者】" + who);
            sb.AppendLine();
            sb.AppendLine("【拆分规则】");
            sb.AppendLine("1. 拆成 1 到 " + Cfg.MaxTasks + " 个子任务。目标很小时只给 1 个任务，不要为了拆而拆。");
            sb.AppendLine("2. 能同时进行的任务不要互相依赖；必须等别人做完才能做的，用 depends_on 写明。");
            sb.AppendLine("3. 同时进行的任务，要修改的文件不能重叠。files 填该任务会修改的文件或目录（相对路径）。");
            sb.AppendLine("4. 只读的调查、分析、审查类任务把 readonly 设为 true，files 留空。");
            sb.AppendLine("5. instructions 必须自成一体：执行者看不到这段对话，只能看到你写的说明和前置任务的结果。写清楚要做什么、做到什么程度算完成。");
            sb.AppendLine("6. agent 填 claude、codex、gemini 之一，只能从上面可用的执行者里选；没有特别偏好就填 any。");
            sb.AppendLine();
            sb.AppendLine("【输出格式】只输出一个 JSON 对象，不要任何其他文字：");
            sb.AppendLine("{\"tasks\":[{\"id\":\"T1\",\"title\":\"简短标题\",\"instructions\":\"详细说明\",\"agent\":\"any\",\"depends_on\":[],\"files\":[],\"readonly\":false}]}");
            return sb.ToString();
        }

        string WorkPrompt(TaskItem t, Dictionary<string, TaskItem> byId)
        {
            var sb = new StringBuilder();
            sb.AppendLine("你是一个多 Agent 开发团队里的执行者，由总指挥分配任务。当前目录就是项目目录。");
            sb.AppendLine();
            sb.AppendLine("【团队总目标（仅供了解背景）】");
            sb.AppendLine(Goal.Trim());
            sb.AppendLine();
            sb.AppendLine("【分配给你的任务 " + t.Id + "：" + t.Title + "】");
            sb.AppendLine(t.Instructions.Trim());
            sb.AppendLine();
            if (t.ReadOnly) sb.AppendLine("这是只读任务：不要修改任何文件。");
            else if (t.Files.Count > 0) sb.AppendLine("只修改这些文件或目录：" + string.Join("、", t.Files.ToArray()) + "。其他执行者正在同时修改别的文件，不要碰范围以外的文件。");
            else sb.AppendLine("只做本任务需要的修改，不要顺手改无关的文件。");
            foreach (var d in t.DependsOn)
            {
                TaskItem dt;
                if (!byId.TryGetValue(d, out dt)) continue;
                sb.AppendLine();
                sb.AppendLine("【前置任务 " + dt.Id + "（" + dt.Title + "）的结果】");
                sb.AppendLine(Clip(dt.Result, 6000));
            }
            sb.AppendLine();
            sb.AppendLine("需要了解现状时可以查看项目里的现有文件，只读查看不受总目标里“不要运行命令”之类要求的限制。");
            sb.AppendLine("完成后用中文简要汇报：做了什么、改了哪些文件、有没有没做完或需要注意的地方。如实汇报，没做到的不要说做到了。");
            return sb.ToString();
        }

        string ReviewPrompt(List<TaskItem> work)
        {
            var sb = new StringBuilder();
            sb.AppendLine("你是多 Agent 开发团队的总指挥。各执行者已经结束工作，请你做最终验收。不要修改任何文件。");
            sb.AppendLine("请实际查看项目里的相关文件来核实，不要只看执行者的汇报：列目录、读文件这类只读操作是允许的。");
            sb.AppendLine("总目标里如果有“不要运行命令”之类的要求，那是针对执行者和最终成果的，不限制你做只读查看。");
            sb.AppendLine();
            AppendContext(sb);
            sb.AppendLine("【总目标】");
            sb.AppendLine(Goal.Trim());
            foreach (var t in work)
            {
                sb.AppendLine();
                sb.AppendLine("【任务 " + t.Id + "：" + t.Title + "】状态：" + t.Status + "，执行者：" + t.AccountLabel);
                sb.AppendLine(Clip(t.Result, 5000));
            }
            sb.AppendLine();
            sb.AppendLine("请用中文写一份验收报告：总目标完成到什么程度、每个任务的结论、发现的问题、建议的下一步。以实际核实到的情况为准，执行者的汇报与实际不符时要指出。");
            sb.AppendLine("报告会显示在对话窗口里：先用一两句话说结论，再用简短的段落或列表展开，不要用表格。");
            return sb.ToString();
        }

        static string Clip(string s, int max)
        {
            s = (s ?? "").Trim();
            if (s.Length == 0) return "（没有返回内容）";
            return s.Length <= max ? s : s.Substring(0, max) + "\n…（已截断）";
        }

        // ---------- 解析总指挥给出的计划 ----------

        public List<TaskItem> ParsePlan(string text, out string problem)
        {
            problem = null;
            var list = new List<TaskItem>();
            var root = Json.Extract(text);
            var arr = Json.Arr(root, "tasks");
            if (arr == null || arr.Count == 0) { problem = "总指挥没有给出可解析的任务列表"; return list; }
            var seen = new HashSet<string>();
            int n = 0;
            foreach (var o in arr)
            {
                var d = o as Dictionary<string, object>;
                if (d == null) continue;
                var instr = (Json.Str(d, "instructions") ?? "").Trim();
                if (instr.Length == 0) continue;
                n++;
                var t = new TaskItem();
                t.Id = (Json.Str(d, "id") ?? "").Trim();
                if (t.Id.Length == 0 || seen.Contains(t.Id)) t.Id = "T" + n;
                seen.Add(t.Id);
                t.Title = (Json.Str(d, "title") ?? "").Trim();
                if (t.Title.Length == 0) t.Title = instr.Length > 30 ? instr.Substring(0, 30) + "…" : instr;
                t.Instructions = instr;
                var agent = (Json.Str(d, "agent") ?? "any").Trim().ToLowerInvariant();
                t.Agent = Array.IndexOf(Cli.Agents, agent) >= 0 ? agent : "any";
                t.ReadOnly = Json.Bool(d, "readonly");
                var deps = Json.Arr(d, "depends_on");
                if (deps != null) foreach (var x in deps) if (x != null) t.DependsOn.Add(Convert.ToString(x).Trim());
                var files = Json.Arr(d, "files");
                if (files != null) foreach (var x in files) if (x != null) { var f = Norm(Convert.ToString(x)); if (f.Length > 0) t.Files.Add(f); }
                list.Add(t);
                if (list.Count >= Cfg.MaxTasks) break;
            }
            if (list.Count == 0) { problem = "总指挥给出的任务都没有说明内容"; return list; }
            // 去掉指向不存在任务或自己的依赖，并打断循环依赖
            var ids = new HashSet<string>(list.Select(t => t.Id));
            foreach (var t in list) t.DependsOn = t.DependsOn.Where(x => ids.Contains(x) && x != t.Id).Distinct().ToList();
            var order = new Dictionary<string, int>();
            for (int i = 0; i < list.Count; i++) order[list[i].Id] = i;
            if (HasCycle(list)) foreach (var t in list) t.DependsOn = t.DependsOn.Where(x => order[x] < order[t.Id]).ToList();
            return list;
        }

        static bool HasCycle(List<TaskItem> list)
        {
            var byId = list.ToDictionary(t => t.Id);
            var state = new Dictionary<string, int>();
            Func<string, bool> visit = null;
            visit = id =>
            {
                int s; state.TryGetValue(id, out s);
                if (s == 1) return true;
                if (s == 2) return false;
                state[id] = 1;
                foreach (var d in byId[id].DependsOn) if (visit(d)) return true;
                state[id] = 2;
                return false;
            };
            foreach (var t in list) if (visit(t.Id)) return true;
            return false;
        }

        static string Norm(string path)
        {
            return (path ?? "").Trim().Replace('\\', '/').TrimStart('.', '/').TrimEnd('/').ToLowerInvariant();
        }

        // 两个会写文件的任务：任何一方没说明范围，或范围有重叠，就不能同时跑
        static bool Overlap(TaskItem a, TaskItem b)
        {
            if (a.ReadOnly || b.ReadOnly) return false;
            if (a.Files.Count == 0 || b.Files.Count == 0) return true;
            foreach (var x in a.Files)
                foreach (var y in b.Files)
                    if (x == y || x.StartsWith(y + "/") || y.StartsWith(x + "/")) return true;
            return false;
        }

        // ---------- 选账号 ----------

        Account Pick(TaskItem t, out string why)
        {
            why = "等待空闲账号";
            var usable = Accounts.Where(Usable).ToList();
            if (usable.Count == 0) { why = "没有可用账号"; return null; }
            var wanted = t.Agent == "any" ? usable : usable.Where(a => a.Agent == t.Agent).ToList();
            // 指定的工具没有账号，或者它的账号全部在冷却：换别家的账号接手
            if (wanted.Count == 0 || wanted.All(a => a.CoolingDown)) wanted = usable;
            var free = wanted.Where(a => !a.CoolingDown && a.Running < a.MaxParallel).ToList();
            if (free.Count == 0)
            {
                if (wanted.All(a => a.CoolingDown))
                    why = "账号都在冷却，最早 " + wanted.Min(a => a.CooldownUntil).ToString("HH:mm") + " 恢复";
                return null;
            }
            // 重试时优先换一个没试过的账号
            var fresh = free.Where(a => !t.Tried.Contains(a.Id)).ToList();
            if (fresh.Count > 0) free = fresh;
            return free.OrderBy(a => a.Running).ThenBy(a => a.LastUsed).First();
        }

        // ---------- 运行一步 ----------

        async Task<RunResult> RunOn(TaskItem t, Account acc, string prompt, string perm, CancellationToken ct)
        {
            t.State = "running"; t.Status = "运行中"; t.Attempts = t.Attempts + 1;
            t.AccountLabel = acc.Display; t.Started = DateTime.Now; t.Elapsed = "0:00";
            t.Tried.Add(acc.Id);
            acc.Running = acc.Running + 1; acc.LastUsed = DateTime.Now; acc.RunStart = DateTime.Now; acc.CurrentTask = t.Id + " " + t.Title;
            // 账号自己的窗口里：先显示总指挥派来了什么任务；任务这边：记下交给了谁
            acc.AddStep(new Step("task", t.Id + "　" + t.Title, t.Kind == "task" ? t.Instructions : null));
            t.AddStep(new Step("info", DateTime.Now.ToString("HH:mm:ss") + " 交给 " + acc.Display + (t.Attempts > 1 ? "（第 " + t.Attempts + " 次尝试）" : "")));
            // 界面上的回调（打开窗口等）出错不能影响任务本身
            var started = TaskStarted;
            if (started != null) { try { started(acc, t); } catch (Exception ex) { Store.LogError("TaskStarted", ex); } }

            RunResult r;
            try
            {
                r = await Exec(acc, ProjectDir, prompt, perm, s => Post(() => { t.AddStep(s); acc.AddStep(s); }), ct);
            }
            catch (Exception ex) { r = new RunResult(); r.Error = "运行出错：" + ex.Message; Store.LogError("RunOn", ex); }
            // 让排在前面的输出先显示完
            var flushed = new TaskCompletionSource<bool>();
            Post(() => flushed.TrySetResult(true));
            await flushed.Task;

            acc.Running = acc.Running - 1; acc.CurrentTask = acc.Running > 0 ? acc.CurrentTask : "";
            t.Tick(); t.Finished = DateTime.Now;
            // 命令行自己报告的实际模型和用量，留作这次确实真实运行过的凭据
            if (!string.IsNullOrEmpty(r.Model)) t.Model = r.Model;
            if (!string.IsNullOrEmpty(r.Usage)) t.Usage = r.Usage;
            acc.NoteRun(r);
            if (r.Ok && acc.LoginState != "in") acc.LoginState = "in";
            if (!r.Ok && r.RateLimited)
            {
                acc.CooldownUntil = DateTime.Now.AddMinutes(Cfg.CooldownMinutes);
                var note = new Step("info", "⚠ " + acc.Display + " 触发用量或频率限制，暂停派活到 " + acc.CooldownUntil.ToString("HH:mm"));
                t.AddStep(note); acc.AddStep(note);
            }
            string proof = (t.Model.Length > 0 ? "　模型 " + t.Model : "") + (t.Usage.Length > 0 ? "　用量 " + t.Usage : "");
            var end = r.Ok ? new Step("done", "完成　用时 " + t.Elapsed + proof) : new Step("error", "没有完成：" + Clip(r.Error, 600));
            t.AddStep(end); acc.AddStep(end);
            Sessions.Touch(acc, ProjectDir, t.Id + " " + t.Title);   // 派来的任务也存进这个账号的历史会话
            return r;
        }

        async Task RunTask(TaskItem t, Account acc, Dictionary<string, TaskItem> byId, CancellationToken ct)
        {
            try
            {
                var r = await RunOn(t, acc, WorkPrompt(t, byId), t.ReadOnly ? "readonly" : Perm, ct);
                if (r.Ok) { t.Result = r.Text; t.State = "done"; t.Status = "完成"; }
                else if (r.Cancelled || ct.IsCancellationRequested) { t.Result = "已停止"; t.State = "failed"; t.Status = "已停止"; }
                else if (t.Attempts < Cfg.MaxAttempts) { t.Result = r.Error; t.State = "pending"; t.Status = "等待重试"; }
                else { t.Result = r.Error; t.State = "failed"; t.Status = "失败"; }
            }
            catch (Exception ex)
            {
                // 无论出什么意外都必须让任务落到一个终态，否则调度循环会一直等它
                Store.LogError("RunTask " + t.Id, ex);
                t.Result = "运行出错：" + ex.Message; t.State = "failed"; t.Status = "失败";
            }
        }

        // ---------- 主流程 ----------

        public async Task Start()
        {
            if (Running) return;
            Running = true;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            Tasks.Clear(); Report = ""; ReportFile = null;
            RunDir = Path.Combine(Store.DataDir, "runs", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            var work = new List<TaskItem>();
            try
            {
                Directory.CreateDirectory(RunDir);
                if (Chief == null || !Usable(Chief)) { Say("总指挥账号不可用：请在左侧菜单的“账号”里添加并登录一个账号。"); return; }

                // 1. 规划
                var plan = new TaskItem(); plan.Id = "规划"; plan.Kind = "plan"; plan.Title = "总指挥拆分任务"; plan.Agent = Chief.Agent; plan.ReadOnly = true;
                Tasks.Add(plan);
                Say("规划中：总指挥正在查看项目并拆分任务");
                string problem = null;
                var planner = Chief;
                while (true)
                {
                    var pr = await RunOn(plan, planner, PlanPrompt(), "readonly", ct);
                    if (ct.IsCancellationRequested) { plan.State = "failed"; plan.Status = "已停止"; Say("已停止。"); return; }
                    if (pr.Ok)
                    {
                        plan.Result = pr.Text;
                        work = ParsePlan(pr.Text, out problem);
                        if (work.Count > 0) break;
                    }
                    else problem = pr.Error;
                    if (plan.Attempts >= 2)
                    {
                        plan.Result = problem ?? ""; plan.State = "failed"; plan.Status = "失败";
                        Say("规划失败：" + Clip(problem, 300));
                        return;
                    }
                    plan.AddStep(new Step("info", "⚠ " + Clip(problem, 300) + "，重试一次"));
                    // 重试时优先换一个没试过、也没在冷却的账号来当总指挥
                    string whyNot;
                    var alt = Pick(plan, out whyNot);
                    if (alt != null) planner = alt;
                }
                plan.State = "done"; plan.Status = "完成，共 " + work.Count + " 个任务";
                var byId = work.ToDictionary(t => t.Id);
                foreach (var t in work) Tasks.Add(t);

                // 2. 派活
                var active = new List<Task>();
                while (true)
                {
                    if (ct.IsCancellationRequested) break;
                    int running = work.Count(t => t.State == "running");
                    string waiting = null;
                    foreach (var t in work)
                    {
                        if (t.State != "pending") continue;
                        if (t.DependsOn.Any(d => byId[d].State == "failed" || byId[d].State == "skipped"))
                        {
                            t.State = "skipped"; t.Status = "已跳过（前置任务未完成）"; t.Result = "前置任务未完成，未执行。";
                            continue;
                        }
                        if (t.DependsOn.Any(d => byId[d].State != "done")) { if (t.Attempts == 0) t.Status = "等待前置任务"; continue; }
                        if (running >= Cfg.MaxParallel) { if (t.Attempts == 0) t.Status = "排队中"; continue; }
                        if (work.Any(o => o.State == "running" && Overlap(o, t))) { if (t.Attempts == 0) t.Status = "等待（避免同时改同一批文件）"; continue; }
                        string why;
                        var acc = Pick(t, out why);
                        if (acc == null)
                        {
                            if (why == "没有可用账号") { t.State = "failed"; t.Status = "失败"; t.Result = "没有可用账号。"; continue; }
                            if (t.Attempts == 0) t.Status = why;
                            waiting = why;
                            continue;
                        }
                        active.Add(RunTask(t, acc, byId, ct));
                        running++;
                    }
                    if (work.All(t => t.Terminal)) break;
                    int done = work.Count(t => t.State == "done");
                    Say("执行中：完成 " + done + "/" + work.Count + "，运行 " + running + (waiting != null && running == 0 ? "，" + waiting : ""));
                    foreach (var a in Accounts) a.RefreshStatus();
                    await Task.Delay(300);
                }
                await Task.WhenAll(active.ToArray());
                if (ct.IsCancellationRequested)
                {
                    foreach (var t in work) if (!t.Terminal) { t.State = "skipped"; t.Status = "已停止"; }
                    Say("已停止。");
                }

                // 3. 验收
                if (!ct.IsCancellationRequested && Review && work.Any(t => t.State == "done"))
                {
                    var rv = new TaskItem(); rv.Id = "验收"; rv.Kind = "review"; rv.Title = "总指挥验收总结"; rv.Agent = Chief.Agent; rv.ReadOnly = true;
                    Tasks.Add(rv);
                    Say("验收中：总指挥正在核对各任务的结果");
                    string why;
                    var reviewer = (!Chief.CoolingDown && Usable(Chief)) ? Chief : Pick(rv, out why);
                    if (reviewer == null) { rv.State = "skipped"; rv.Status = "已跳过（没有空闲账号）"; }
                    else
                    {
                        var rr = await RunOn(rv, reviewer, ReviewPrompt(work), "readonly", ct);
                        if (rr.Ok) { rv.Result = rr.Text; rv.State = "done"; rv.Status = "完成"; Report = rr.Text; }
                        else { rv.Result = rr.Error; rv.State = "failed"; rv.Status = ct.IsCancellationRequested ? "已停止" : "失败"; }
                    }
                }
                if (!ct.IsCancellationRequested)
                {
                    int ok = work.Count(t => t.State == "done");
                    Say(ok == work.Count ? "全部完成：" + ok + " 个任务。" : "结束：" + ok + "/" + work.Count + " 个任务完成，其余失败或被跳过。");
                }
            }
            catch (Exception ex)
            {
                Store.LogError("Commander.Start", ex);
                Say("运行出错：" + ex.Message);
            }
            finally
            {
                try { WriteReport(work); } catch (Exception ex) { Store.LogError("WriteReport", ex); }
                foreach (var a in Accounts) a.RefreshStatus();
                Running = false;
            }
        }

        void WriteReport(List<TaskItem> work)
        {
            if (RunDir == null || Tasks.Count == 0) return;
            var sb = new StringBuilder();
            sb.AppendLine("# 运行报告 " + Path.GetFileName(RunDir));
            sb.AppendLine();
            sb.AppendLine("- 项目文件夹：" + ProjectDir);
            sb.AppendLine("- 总指挥：" + (Chief != null ? Chief.Display : ""));
            sb.AppendLine();
            sb.AppendLine("## 总目标");
            sb.AppendLine();
            sb.AppendLine(Goal.Trim());
            sb.AppendLine();
            if (!string.IsNullOrEmpty(Report)) { sb.AppendLine("## 验收报告"); sb.AppendLine(); sb.AppendLine(Report); sb.AppendLine(); }
            sb.AppendLine("## 各任务结果");
            foreach (var t in work)
            {
                sb.AppendLine();
                sb.AppendLine("### " + t.Id + " " + t.Title);
                sb.AppendLine();
                sb.AppendLine("- 状态：" + t.Status + "　执行者：" + t.AccountLabel + "　尝试次数：" + t.Attempts + "　用时：" + t.Elapsed);
                if (t.Model.Length > 0 || t.Usage.Length > 0)
                    sb.AppendLine("- 实际模型：" + (t.Model.Length > 0 ? t.Model : "未报告") + "　用量：" + (t.Usage.Length > 0 ? t.Usage : "未报告"));
                sb.AppendLine();
                sb.AppendLine(Clip(t.Result, 20000));
            }
            ReportFile = Path.Combine(RunDir, "report.md");
            File.WriteAllText(ReportFile, sb.ToString(), new UTF8Encoding(true));
            foreach (var t in Tasks)
                File.WriteAllText(Path.Combine(RunDir, "log-" + t.Id + ".txt"), t.Log.ToString(), new UTF8Encoding(true));
        }
    }
}
