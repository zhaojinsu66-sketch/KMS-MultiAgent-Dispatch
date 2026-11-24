using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

[assembly: AssemblyTitle("KMS多控")]
[assembly: AssemblyProduct("KMS多控")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]

namespace AC
{
    // 调度规则自检：用假的执行器顶替真实命令行，只验证“怎么派活”的逻辑。
    // 这里不连接任何模型，真实运行走的是 Cli.Run。
    public static class SelfTest
    {
        class Span
        {
            public string Task, Acc, Perm, Prompt;
            public DateTime Start, End;
        }

        static string TaskId(string prompt)
        {
            const string mark = "【分配给你的任务 ";
            int i = prompt.IndexOf(mark);
            if (i < 0) return "?";
            int j = prompt.IndexOf('：', i);
            return prompt.Substring(i + mark.Length, j - i - mark.Length);
        }

        static Account Acc(string id, string agent)
        {
            var a = new Account();
            a.Id = id; a.Agent = agent; a.Label = id; a.UseDefaultLogin = true; a.MaxParallel = 1; a.Enabled = true;
            return a;
        }

        static AppConfig Cfg()
        {
            var cfg = new AppConfig();
            // 让“命令行程序是否存在”的检查通过：指向一个肯定存在的文件
            var self = Assembly.GetExecutingAssembly().Location;
            cfg.ClaudePath = self; cfg.CodexPath = self; cfg.GeminiPath = self;
            cfg.MaxParallel = 6; cfg.MaxAttempts = 3; cfg.CooldownMinutes = 5;
            return cfg;
        }

        static async Task<bool> Finish(Commander cmd, int ms)
        {
            var run = cmd.Start();
            return await Task.WhenAny(run, Task.Delay(ms)) == run;
        }

        public static async Task<int> Run(string logFile)
        {
            var log = new StringBuilder();
            int fails = 0;
            Action<bool, string> check = (ok, what) => { log.AppendLine((ok ? "PASS  " : "FAIL  ") + what); if (!ok) fails++; };
            try
            {
                // ---- 小工具 ----
                check(Cli.Quote("abc") == "abc", "参数引用：普通参数不加引号");
                check(Cli.Quote("a b") == "\"a b\"", "参数引用：带空格");
                check(Cli.Quote("a\"b") == "\"a\\\"b\"", "参数引用：带引号");
                check(Cli.Quote("C:\\my dir\\") == "\"C:\\my dir\\\\\"", "参数引用：结尾反斜杠");
                check(Cli.Quote("D:\\a&b") == "\"D:\\a&b\"", "参数引用：带命令行符号的路径加引号");
                check(Cli.LooksRateLimited("You've hit your limit · resets 3pm"), "识别“已达上限，几点重置”这类提示");
                check(Cli.LooksRateLimited("You've hit your usage limit"), "识别用量限制");
                check(Cli.LooksRateLimited("Error 429 Too Many Requests"), "识别 429");
                check(!Cli.LooksRateLimited("syntax error in file main.cs"), "普通错误不当成限额");
                var ex = Json.Extract("好的，计划如下：\n```json\n{\"tasks\":[{\"id\":\"T1\",\"instructions\":\"写 {x} 和 \\\"y\\\" }\"}]}\n```\n完毕 }");
                check(ex != null && Json.Arr(ex, "tasks") != null && Json.Arr(ex, "tasks").Count == 1, "从带说明文字和代码围栏的回复里取出 JSON");
                check(Json.Extract("没有任何 JSON") == null, "没有 JSON 时返回空");

                check(Look.EffortLabel("xhigh") == "超高" && Look.EffortLabel("") == "默认强度" && Look.EffortLabel("max") == "最高", "推理强度显示成中文");
                check(Look.ModelLabel("claude-fable-5-2") == "Fable 5.2" && Look.ModelLabel("gpt-6-luna") == "GPT-6-Luna" && Look.ModelLabel("") == "默认模型", "模型显示成好认的名字");

                // ---- 历史会话：保存、列出、搜索、打开、删除 ----
                {
                    var a = Acc("H1", "claude");
                    a.AddStep(new Step("user", "把登录页的校验拆出来"));
                    a.AddStep(new Step("say", "我先看一下现有逻辑。"));
                    a.AddStep(new Step("tool", "读取 src/login.js", "文件内容…"));
                    a.DirectSession = "sess-123";
                    Sessions.Touch(a, "D:\\proj\\shop", "把登录页的校验拆出来");
                    var first = a.Session;
                    Sessions.StartNew(a);
                    check(a.Steps.Count == 0 && a.Session == null && a.DirectSession == "", "历史会话：开新会话后内容清空、不再接着旧会话");
                    a.AddStep(new Step("user", "写一个问候函数"));
                    Sessions.Touch(a, "D:\\proj\\shop", "写一个问候函数");
                    var all = Sessions.List("H1");
                    check(all.Count == 2 && first != null && all.Exists(s => s.Id == first.Id), "历史会话：两段会话都存了下来");
                    check(all.FindAll(s => Sessions.Matches(s, "login.js")).Count == 1 && all.FindAll(s => Sessions.Matches(s, "问候")).Count == 1 && all.FindAll(s => Sessions.Matches(s, "不存在的词")).Count == 0, "历史会话：能按标题和内容搜索");
                    var old = all.Find(s => s.Id == first.Id);
                    Sessions.Open(a, old);
                    check(a.Steps.Count == 3 && a.Steps[2].Kind == "tool" && a.Steps[2].Detail == "文件内容…" && a.DirectSession == "sess-123" && a.DirectDir == "D:\\proj\\shop", "历史会话：打开后内容和会话编号都恢复，可以接着说");
                    Sessions.Delete(all.Find(s => s.Id != first.Id));
                    check(Sessions.List("H1").Count == 1, "历史会话：可以删除");
                }

                // ---- 登录向导：数据文件夹的挪动和恢复（用临时文件夹模拟，不碰真实数据）----
                {
                    var keep = SlotSwap.DataRoot;
                    var root = Path.Combine(Path.GetTempPath(), "ac-slot-" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        SlotSwap.DataRoot = root;
                        var def = Path.Combine(root, "claude_abc123");
                        Directory.CreateDirectory(def); File.WriteAllText(Path.Combine(def, "account-A.txt"), "A");
                        check(SlotSwap.DefaultDir() == def && SlotSwap.ParkedDir() == null, "登录向导：找到默认数据文件夹");
                        check(SlotSwap.Park() == null && !Directory.Exists(def) && SlotSwap.ParkedDir() != null, "登录向导：第一步把现有数据改名放到一边");
                        check(SlotSwap.Park() != null, "登录向导：进行中不能重复开始");
                        check(SlotSwap.DefaultDir() == def, "登录向导：挪开后仍然知道默认位置在哪");
                        Directory.CreateDirectory(def); File.WriteAllText(Path.Combine(def, "account-B.txt"), "B");   // 模拟新账号登录产生的数据
                        var target = SlotSwap.WindowDir("w1");
                        check(SlotSwap.Finish(target) == null, "登录向导：完成这一步没有报错");
                        check(File.Exists(Path.Combine(target, "account-B.txt")) && File.Exists(Path.Combine(def, "account-A.txt"))
                              && !File.Exists(Path.Combine(def, "account-B.txt")) && SlotSwap.ParkedDir() == null,
                              "登录向导：新账号的数据进了新窗口，原来的数据原样回到默认位置");
                        SlotSwap.Park(); Directory.CreateDirectory(def); File.WriteAllText(Path.Combine(def, "half.txt"), "x");
                        check(SlotSwap.Restore() == null && File.Exists(Path.Combine(def, "account-A.txt")) && !File.Exists(Path.Combine(def, "half.txt"))
                              && Directory.GetDirectories(root, "kms-abandoned-*").Length == 1,
                              "登录向导：中途放弃时原来的数据恢复，半途产生的数据改名保留、不删除");
                        SlotSwap.Park();
                        var msg = SlotSwap.Finish(SlotSwap.WindowDir("w2"));
                        check(msg != null && File.Exists(Path.Combine(def, "account-A.txt")) && SlotSwap.ParkedDir() == null && !Directory.Exists(SlotSwap.WindowDir("w2")),
                              "登录向导：没有登录成功就点完成时，直接恢复原样并说明");
                    }
                    finally { SlotSwap.DataRoot = keep; try { Directory.Delete(root, true); } catch { } }
                }

                // ---- 真空浏览器：哪些进程该接手、链接怎么整理 ----
                {
                    const string edge = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
                    const string app = @"C:\Program Files\WindowsApps\Claude_5.1_x64__x\app\claude.exe";
                    check(Containers.UrlToOpen(edge, "\"" + edge + "\" --single-argument https://claude.com/cai/login/app-google-auth?hop_nonce=abc") == "https://claude.com/cai/login/app-google-auth?hop_nonce=abc",
                          "真空浏览器：应用让系统浏览器打开登录网址时，认得出网址");
                    check(Containers.UrlToOpen(edge, "\"" + edge + "\" --type=renderer --field=https://x.example/") == null
                          && Containers.UrlToOpen(edge, "\"" + edge + "\" --headless --remote-debugging-port=9222 https://x.example/") == null
                          && Containers.UrlToOpen(edge, "\"" + edge + "\" --user-data-dir=D:\\p https://x.example/") == null
                          && Containers.UrlToOpen(@"C:\Windows\System32\cmd.exe", "cmd /c echo https://x.example/") == null,
                          "真空浏览器：浏览器自己的子进程、自动化用的浏览器、别的程序，都不接手");
                    check(Containers.LinkToDeliver("\"" + app + "\" -- \"claude://login/google-auth?code=c&hop_nonce=n\"") == "claude://login/google-auth?code=c&hop_nonce=n"
                          && Containers.LinkToDeliver("\"" + app + "\" --user-data-dir=\"D:\\w2\" -- \"claude://login/google-auth?code=c\"") == null
                          && Containers.LinkToDeliver("\"" + app + "\" --type=renderer") == null && Containers.LinkToDeliver("\"" + app + "\"") == null,
                          "真空浏览器：认得出“把登录回传交给 Claude”的进程；已经指明窗口的不重复接手");
                    check(Containers.IsLoginLink("claude://login/google-auth?code=c") && Containers.IsLoginLink("claude://claude.ai/magic-link#a:b") && !Containers.IsLoginLink("claude://claude.ai/new?q=hi"),
                          "真空浏览器：只把登录用的回传当作登录结果");
                    check(Containers.NormalizeLink("  https://claude.ai/magic-link#tok:ZW1haWw=  ") == "claude://claude.ai/magic-link#tok:ZW1haWw="
                          && Containers.NormalizeLink("claude://login/google-auth?code=c&hop_nonce=n") == "claude://login/google-auth?code=c&hop_nonce=n"
                          && Containers.NormalizeLink("https://example.com/") == null && Containers.NormalizeLink("claude://x y") == null && Containers.NormalizeLink("") == null,
                          "真空浏览器：邮件里的登录链接能整理成应用认的格式，别的内容不收");
                    check(Containers.Short("claude://login/google-auth?code=secret&hop_nonce=n") == "claude://login/google-auth…" && Containers.Short("claude://claude.ai/magic-link#tok:x") == "claude://claude.ai/magic-link…",
                          "真空浏览器：记录里不留登录用的一次性参数");
                    var orig = new AppWindow { Id = "o", Kind = "claude", Label = "原窗口", DataDir = "" };
                    check(Containers.Deliver(orig, "claude://login/google-auth?code=c") != null && Containers.Deliver(new AppWindow { Id = "x", Kind = "codex", Label = "c", DataDir = "D:\\none" }, "claude://login/google-auth?code=c") != null,
                          "真空浏览器：原窗口和 Codex 窗口不做转交");
                }

                var pc = new Commander(Cfg(), new List<Account>());
                string problem;
                var cyc = pc.ParsePlan("{\"tasks\":[{\"id\":\"T1\",\"instructions\":\"a\",\"depends_on\":[\"T2\",\"T9\",\"T1\"]},{\"id\":\"T2\",\"instructions\":\"b\",\"depends_on\":[\"T1\"],\"agent\":\"GPT\"}]}", out problem);
                check(cyc.Count == 2 && cyc[0].DependsOn.Count == 0 && cyc[1].DependsOn.Count == 1 && cyc[1].DependsOn[0] == "T1", "计划解析：去掉不存在的依赖并打断循环依赖");
                check(cyc.Count == 2 && cyc[1].Agent == "any", "计划解析：不认识的工具名改为任意");
                check(pc.ParsePlan("{\"tasks\":[]}", out problem).Count == 0 && problem != null, "计划解析：空计划报告问题");

                // ---- 场景 A：并行、依赖、文件冲突、限额后换账号 ----
                {
                    var accounts = new List<Account> { Acc("A1", "claude"), Acc("A2", "claude"), Acc("B1", "codex") };
                    var cmd = new Commander(Cfg(), accounts);
                    cmd.Goal = "自检"; cmd.ProjectDir = Path.GetTempPath(); cmd.Chief = accounts[0]; cmd.Perm = "edit"; cmd.Review = true;
                    var spans = new List<Span>();
                    var tries = new Dictionary<string, int>();
                    string plan = "{\"tasks\":["
                        + "{\"id\":\"T1\",\"title\":\"a\",\"instructions\":\"do a\",\"agent\":\"claude\",\"files\":[\"src/a\"]},"
                        + "{\"id\":\"T2\",\"title\":\"b\",\"instructions\":\"do b\",\"agent\":\"codex\",\"files\":[\"src/b\"]},"
                        + "{\"id\":\"T3\",\"title\":\"c\",\"instructions\":\"do c\",\"agent\":\"any\",\"depends_on\":[\"T1\",\"T2\"],\"files\":[\"c.txt\"]},"
                        + "{\"id\":\"T4\",\"title\":\"d\",\"instructions\":\"do d\",\"agent\":\"claude\",\"files\":[\"src/d\"]},"
                        + "{\"id\":\"T5\",\"title\":\"e\",\"instructions\":\"do e\",\"agent\":\"any\",\"files\":[\".\\\\src\\\\a\\\\x.txt\"]},"
                        + "{\"id\":\"T6\",\"title\":\"f\",\"instructions\":\"look\",\"agent\":\"any\",\"readonly\":true}]}";
                    cmd.Exec = async (acc, cwd, prompt, perm, onText, ct) =>
                    {
                        var r = new RunResult();
                        if (prompt.Contains("只负责拆分任务")) { await Task.Delay(30); r.Ok = true; r.Text = "计划：\n" + plan; return r; }
                        if (prompt.Contains("最终验收")) { await Task.Delay(30); r.Ok = true; r.Text = "验收报告：全部完成"; return r; }
                        var sp = new Span { Task = TaskId(prompt), Acc = acc.Id, Perm = perm, Prompt = prompt, Start = DateTime.Now };
                        spans.Add(sp);
                        onText(new Step("say", "working"));
                        await Task.Delay(400, ct);
                        sp.End = DateTime.Now;
                        int n; tries.TryGetValue(sp.Task, out n); tries[sp.Task] = n + 1;
                        if (sp.Task == "T4" && n == 0) { r.Error = "You've hit your usage limit"; r.RateLimited = true; return r; }
                        r.Ok = true; r.Text = sp.Task + " 完成 by " + acc.Id;
                        return r;
                    };
                    bool finished = await Finish(cmd, 20000);
                    check(finished && !cmd.Running, "A：流程在限定时间内结束");
                    var work = cmd.Tasks.Where(t => t.Kind == "task").ToList();
                    check(work.Count == 6 && work.All(t => t.State == "done"), "A：6 个任务全部完成");
                    Func<string, Span> last = id => spans.Last(s => s.Task == id);
                    Func<string, Span> first = id => spans.First(s => s.Task == id);
                    if (finished && work.Count == 6 && spans.Count >= 7)
                    {
                        check(first("T1").Start < first("T2").End && first("T2").Start < first("T1").End, "A：互不相关的 T1、T2 同时运行");
                        check(first("T2").Acc == "B1", "A：指定 Codex 的任务派给 Codex 账号");
                        check(last("T3").Start >= last("T1").End && last("T3").Start >= last("T2").End, "A：T3 等前置任务完成后才开始");
                        check(last("T3").Prompt.Contains("T1 完成 by") && last("T3").Prompt.Contains("T2 完成 by"), "A：前置任务的结果传给了后续任务");
                        check(last("T5").Start >= last("T1").End, "A：改同一批文件的 T5 不与 T1 同时运行");
                        check(spans.Count(s => s.Task == "T4") == 2 && first("T4").Acc != last("T4").Acc, "A：T4 触发限额后换了另一个账号重试");
                        check(accounts.First(a => a.Id == first("T4").Acc).CoolingDown, "A：触发限额的账号进入冷却");
                        check(last("T6").Perm == "readonly" && last("T1").Perm == "edit", "A：只读任务用只读权限，其余用所选权限");
                    }
                    else check(false, "A：记录不完整，无法检查细节（任务 " + work.Count + "，记录 " + spans.Count + "）");
                    check(cmd.Tasks.Any(t => t.Kind == "plan" && t.State == "done") && cmd.Tasks.Any(t => t.Kind == "review" && t.State == "done"), "A：规划和验收两步都完成");
                    check(cmd.Report.Contains("验收报告") && cmd.ReportFile != null && File.Exists(cmd.ReportFile), "A：生成了报告文件");
                    check(accounts.All(a => a.Running == 0), "A：结束后没有账号仍被标记为运行中");
                }

                // ---- 场景 B：指定工具的账号全部冷却时，换别家的账号接手 ----
                {
                    var accounts = new List<Account> { Acc("A1", "claude"), Acc("B1", "codex") };
                    var cmd = new Commander(Cfg(), accounts);
                    cmd.Goal = "自检"; cmd.ProjectDir = Path.GetTempPath(); cmd.Chief = accounts[0]; cmd.Review = true;
                    var ran = new List<string>();
                    cmd.Exec = async (acc, cwd, prompt, perm, onText, ct) =>
                    {
                        var r = new RunResult();
                        await Task.Delay(30, ct);
                        if (prompt.Contains("只负责拆分任务")) { r.Ok = true; r.Text = "{\"tasks\":[{\"id\":\"T1\",\"instructions\":\"x\",\"agent\":\"claude\",\"files\":[\"a\"]}]}"; return r; }
                        if (prompt.Contains("最终验收")) { ran.Add("review:" + acc.Id); r.Ok = true; r.Text = "ok"; return r; }
                        ran.Add(acc.Id);
                        if (acc.Agent == "claude") { r.Error = "rate limit exceeded"; r.RateLimited = true; return r; }
                        r.Ok = true; r.Text = "done";
                        return r;
                    };
                    bool finished = await Finish(cmd, 20000);
                    var t1 = cmd.Tasks.FirstOrDefault(t => t.Id == "T1");
                    check(finished && t1 != null && t1.State == "done" && t1.Attempts == 2 && ran.Count >= 2 && ran[0] == "A1" && ran[1] == "B1", "B：Claude 账号限额后由 Codex 账号接手完成");
                    check(ran.Contains("review:B1"), "B：总指挥账号在冷却时，验收改由空闲账号完成");
                }

                // ---- 场景 C：总指挥给不出计划 ----
                {
                    var accounts = new List<Account> { Acc("A1", "claude") };
                    var cmd = new Commander(Cfg(), accounts);
                    cmd.Goal = "自检"; cmd.ProjectDir = Path.GetTempPath(); cmd.Chief = accounts[0];
                    string status = "";
                    cmd.Status += s => status = s;
                    cmd.Exec = async (acc, cwd, prompt, perm, onText, ct) => { await Task.Delay(20, ct); var r = new RunResult(); r.Ok = true; r.Text = "我觉得可以直接做"; return r; };
                    bool finished = await Finish(cmd, 20000);
                    check(finished && cmd.Tasks.Count == 1 && cmd.Tasks[0].State == "failed" && cmd.Tasks[0].Attempts == 2 && status.Contains("规划失败"), "C：计划无法解析时重试一次后明确报告规划失败");
                }

                // ---- 场景 D：任务一直失败 ----
                {
                    var accounts = new List<Account> { Acc("A1", "claude"), Acc("A2", "claude") };
                    var cmd = new Commander(Cfg(), accounts);
                    cmd.Goal = "自检"; cmd.ProjectDir = Path.GetTempPath(); cmd.Chief = accounts[0]; cmd.Review = true;
                    var used = new List<string>();
                    cmd.Exec = async (acc, cwd, prompt, perm, onText, ct) =>
                    {
                        var r = new RunResult();
                        await Task.Delay(30, ct);
                        if (prompt.Contains("只负责拆分任务")) { r.Ok = true; r.Text = "{\"tasks\":[{\"id\":\"T1\",\"instructions\":\"x\",\"files\":[\"a\"]},{\"id\":\"T2\",\"instructions\":\"y\",\"depends_on\":[\"T1\"],\"files\":[\"b\"]}]}"; return r; }
                        used.Add(acc.Id);
                        r.Error = "编译不通过";
                        return r;
                    };
                    bool finished = await Finish(cmd, 20000);
                    var t1 = cmd.Tasks.FirstOrDefault(t => t.Id == "T1");
                    var t2 = cmd.Tasks.FirstOrDefault(t => t.Id == "T2");
                    check(finished && t1 != null && t1.State == "failed" && t1.Attempts == 3, "D：一直失败的任务在 3 次后标记为失败");
                    check(used.Count == 3 && used[0] != used[1], "D：重试时先换没试过的账号");
                    check(t2 != null && t2.State == "skipped" && t2.Attempts == 0, "D：依赖它的任务被跳过而不是执行");
                    check(!cmd.Tasks.Any(t => t.Kind == "review"), "D：没有任何任务成功时不做验收");
                    check(accounts.All(a => !a.CoolingDown), "D：普通失败不会让账号进入冷却");
                }

                // ---- 场景 E：中途停止 ----
                {
                    var accounts = new List<Account> { Acc("A1", "claude"), Acc("A2", "claude") };
                    var cmd = new Commander(Cfg(), accounts);
                    cmd.Goal = "自检"; cmd.ProjectDir = Path.GetTempPath(); cmd.Chief = accounts[0]; cmd.Review = true;
                    cmd.Exec = async (acc, cwd, prompt, perm, onText, ct) =>
                    {
                        var r = new RunResult();
                        if (prompt.Contains("只负责拆分任务")) { r.Ok = true; r.Text = "{\"tasks\":[{\"id\":\"T1\",\"instructions\":\"x\",\"files\":[\"a\"]},{\"id\":\"T2\",\"instructions\":\"y\",\"files\":[\"b\"]},{\"id\":\"T3\",\"instructions\":\"z\",\"depends_on\":[\"T1\"],\"files\":[\"c\"]}]}"; return r; }
                        await Task.Delay(8000, ct);
                        r.Ok = true; r.Text = "done";
                        return r;
                    };
                    var run = cmd.Start();
                    await Task.Delay(700);
                    bool wasRunning = cmd.Tasks.Count(t => t.State == "running") == 2;
                    cmd.Stop();
                    bool finished = await Task.WhenAny(run, Task.Delay(5000)) == run;
                    check(wasRunning, "E：停止前有 2 个任务在并行运行");
                    check(finished && !cmd.Running && cmd.Tasks.Where(t => t.Kind == "task").All(t => t.Terminal && t.State != "done"), "E：点停止后很快结束，任务都标记为已停止");
                    check(accounts.All(a => a.Running == 0), "E：停止后没有账号仍被标记为运行中");
                }
            }
            catch (Exception e)
            {
                fails++;
                log.AppendLine("FAIL  自检过程抛出异常：" + e);
            }
            log.AppendLine(fails == 0 ? "RESULT 全部通过" : "RESULT 有 " + fails + " 项未通过");
            try { File.WriteAllText(logFile, log.ToString(), new UTF8Encoding(true)); } catch { }
            return fails == 0 ? 0 : 1;
        }
    }
}
