using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AC
{
    public class RunResult
    {
        public bool Ok;
        public string Text = "";
        public string Error = "";
        public bool RateLimited;
        public bool Cancelled;
        public int ExitCode;
        public string Model = "";       // 实际使用的模型
        public string Usage = "";       // 命令行自己报告的用量
        public string SessionId = "";   // 这一轮的会话编号，下一条消息可以接着说
        public long TokensIn, TokensOut;
        public List<LimitInfo> Limits = new List<LimitInfo>();   // 命令行报告的套餐限额
    }

    public class LoginInfo
    {
        public string State = "unknown";   // unknown | in | out | nocli
        public string Identity = "";
        public string Plan = "";
    }

    public class ModelInfo
    {
        public string Id { get; set; }      // 传给命令行的具体模型名
        public string Name { get; set; }    // 给人看的名字
        public List<string> Efforts = new List<string>();
        public ModelInfo() { Id = ""; Name = ""; }
    }

    // 只负责启动各家官方命令行程序，并把它们的输出整理成一步一步的内容。
    public static class Cli
    {
        public static readonly string[] Agents = new[] { "claude", "codex", "gemini" };

        public static string DisplayName(string agent)
        {
            switch (agent)
            {
                case "claude": return "Claude";
                case "codex": return "Codex";
                case "gemini": return "Gemini";
                default: return agent ?? "";
            }
        }

        // ---------- 找到命令行程序 ----------

        static string OnPath(params string[] names)
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var raw in path.Split(';'))
            {
                var dir = raw.Trim().Trim('"');
                if (dir.Length == 0) continue;
                foreach (var n in names)
                {
                    try
                    {
                        var f = Path.Combine(dir, n);
                        if (File.Exists(f)) return f;
                    }
                    catch { }
                }
            }
            return null;
        }

        // 在 root\*\...\fileName 这种随版本变化的目录里找最新的一个
        static string Newest(string root, int depth, string middle, string fileName)
        {
            string best = null; DateTime bestTime = DateTime.MinValue;
            try
            {
                if (!Directory.Exists(root)) return null;
                var level = new List<string>(); level.Add(root);
                for (int i = 0; i < depth; i++)
                {
                    var next = new List<string>();
                    foreach (var d in level)
                    {
                        try { next.AddRange(Directory.GetDirectories(d)); } catch { }
                    }
                    if (i == 0 && middle != null)
                    {
                        var withMid = new List<string>();
                        foreach (var d in next) withMid.Add(Path.Combine(d, middle));
                        next = withMid;
                    }
                    level = next;
                }
                foreach (var d in level)
                {
                    var f = Path.Combine(d, fileName);
                    if (!File.Exists(f)) continue;
                    var t = File.GetLastWriteTime(f);
                    if (t > bestTime) { bestTime = t; best = f; }
                }
            }
            catch { }
            return best;
        }

        static readonly Dictionary<string, KeyValuePair<DateTime, string>> FoundCache = new Dictionary<string, KeyValuePair<DateTime, string>>();

        // 调度循环每秒会问好几次“这个工具装了没有”，结果缓存几秒，避免反复扫磁盘
        public static string Find(string agent, AppConfig cfg)
        {
            string custom = (agent == "claude" ? cfg.ClaudePath : agent == "codex" ? cfg.CodexPath : cfg.GeminiPath) ?? "";
            string key = agent + "|" + custom;
            lock (FoundCache)
            {
                KeyValuePair<DateTime, string> hit;
                if (FoundCache.TryGetValue(key, out hit) && (DateTime.Now - hit.Key).TotalSeconds < 5) return hit.Value;
            }
            string result = Locate(agent, custom);
            lock (FoundCache) FoundCache[key] = new KeyValuePair<DateTime, string>(DateTime.Now, result);
            return result;
        }

        static string Locate(string agent, string custom)
        {
            if (!string.IsNullOrWhiteSpace(custom))
            {
                custom = custom.Trim().Trim('"');
                return File.Exists(custom) ? custom : null;
            }
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string found = null;
            if (agent == "claude")
            {
                found = OnPath("claude.exe", "claude.cmd");
                if (found == null) { var f = Path.Combine(home, ".local", "bin", "claude.exe"); if (File.Exists(f)) found = f; }
                if (found == null) { var f = Path.Combine(roaming, "npm", "claude.cmd"); if (File.Exists(f)) found = f; }
                // Claude 桌面版自带的同一个命令行程序：<LocalAppData>\Claude-Data\<包名>\claude-code\<版本>\<哈希>\claude.exe
                if (found == null) found = Newest(Path.Combine(local, "Claude-Data"), 3, "claude-code", "claude.exe");
            }
            else if (agent == "codex")
            {
                found = OnPath("codex.exe", "codex.cmd");
                if (found == null) { var f = Path.Combine(roaming, "npm", "codex.cmd"); if (File.Exists(f)) found = f; }
                // Codex 桌面版自带的命令行程序
                if (found == null) found = Newest(Path.Combine(local, "OpenAI", "Codex", "bin"), 1, null, "codex.exe");
            }
            else if (agent == "gemini")
            {
                found = OnPath("gemini.exe", "gemini.cmd");
                if (found == null) { var f = Path.Combine(roaming, "npm", "gemini.cmd"); if (File.Exists(f)) found = f; }
            }
            return found;
        }

        // ---------- 启动进程 ----------

        public static string Quote(string a)
        {
            // 命令行符号也要加引号：通过 .cmd 启动时这些字符会被 cmd 当成语法
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"', '&', '|', '<', '>', '^', '(', ')', '%', '!', ',', ';', '=' }) < 0) return a;
            var sb = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in a)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { sb.Append('\\', slashes * 2 + 1); sb.Append('"'); slashes = 0; continue; }
                sb.Append('\\', slashes); slashes = 0; sb.Append(c);
            }
            sb.Append('\\', slashes * 2);
            sb.Append('"');
            return sb.ToString();
        }

        static string Join(List<string> args)
        {
            var parts = new List<string>();
            foreach (var a in args) parts.Add(Quote(a));
            return string.Join(" ", parts.ToArray());
        }

        // 如果本程序是从另一个 Claude 会话里启动的，去掉那个会话留下的环境变量，
        // 否则子进程会被当成“会话里的子会话”而不是用户自己的独立登录。
        static void CleanEnv(IDictionary<string, string> env)
        {
            if (!env.ContainsKey("CLAUDECODE") && !env.ContainsKey("CLAUDE_CODE_ENTRYPOINT")) return;
            var drop = new List<string>();
            foreach (var k in env.Keys)
            {
                var u = k.ToUpperInvariant();
                if (u.StartsWith("CLAUDE_CODE_") || u.StartsWith("CLAUDE_AGENT_SDK_") || u.StartsWith("CLAUDE_PREVIEW_")
                    || u == "CLAUDECODE" || u == "CLAUDE_PID" || u == "ANTHROPIC_BASE_URL")
                    drop.Add(k);
            }
            foreach (var k in drop) env.Remove(k);
        }

        // Windows 的系统代理（浏览器用的那个）。返回 http://主机:端口，没开代理返回 null。
        public static string SystemProxy()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
                {
                    if (k == null || Convert.ToInt32(k.GetValue("ProxyEnable", 0)) != 1) return null;
                    var server = (k.GetValue("ProxyServer") as string ?? "").Trim();
                    if (server.Length == 0) return null;
                    if (server.Contains("="))
                    {
                        // 形如 http=主机:端口;https=主机:端口;socks=…：优先用 https 那一项
                        string pick = null;
                        foreach (var part in server.Split(';'))
                        {
                            var kv = part.Split(new[] { '=' }, 2);
                            if (kv.Length != 2) continue;
                            var name = kv[0].Trim().ToLowerInvariant();
                            if (name == "https") { pick = kv[1].Trim(); break; }
                            if (name == "http" && pick == null) pick = kv[1].Trim();
                        }
                        if (string.IsNullOrEmpty(pick)) return null;
                        server = pick;
                    }
                    return server.Contains("://") ? server : "http://" + server;
                }
            }
            catch { return null; }
        }

        // 浏览器会自动走系统代理，命令行程序不会。这里把系统代理交给它们，
        // 否则在需要代理才能访问这些服务的网络里，登录和运行都会被拒绝。
        static void ApplyProxy(IDictionary<string, string> env)
        {
            if (env.ContainsKey("HTTPS_PROXY") || env.ContainsKey("HTTP_PROXY") || env.ContainsKey("ALL_PROXY")) return;   // 用户自己设过就不动
            var proxy = SystemProxy();
            if (proxy == null) return;
            env["HTTPS_PROXY"] = proxy;
            env["HTTP_PROXY"] = proxy;
            if (!env.ContainsKey("NO_PROXY")) env["NO_PROXY"] = "localhost,127.0.0.1,::1";
        }

        // 给界面显示用：命令行会怎么联网
        public static string NetworkNote()
        {
            foreach (var n in new[] { "HTTPS_PROXY", "HTTP_PROXY", "ALL_PROXY" })
            {
                var v = Environment.GetEnvironmentVariable(n);
                if (!string.IsNullOrEmpty(v)) return "走环境变量里设置的代理 " + Regex.Replace(v, "//[^@/]*@", "//");
            }
            var p = SystemProxy();
            return p != null ? "走系统代理 " + p.Replace("http://", "") : "直连（系统代理没有开启）";
        }

        // 多账号隔离：每个账号一个独立目录，用各家官方支持的环境变量指过去
        static void ApplyProfile(Account acc, IDictionary<string, string> env)
        {
            CleanEnv(env);
            ApplyProxy(env);
            if (acc.UseDefaultLogin || string.IsNullOrEmpty(acc.ProfileDir)) return;
            Directory.CreateDirectory(acc.ProfileDir);
            if (acc.Agent == "claude") env["CLAUDE_CONFIG_DIR"] = acc.ProfileDir;
            else if (acc.Agent == "codex") env["CODEX_HOME"] = acc.ProfileDir;
            else if (acc.Agent == "gemini") env["GEMINI_CLI_HOME"] = acc.ProfileDir;
        }

        static ProcessStartInfo Start(string exe, List<string> args)
        {
            var psi = new ProcessStartInfo();
            var ext = Path.GetExtension(exe).ToLowerInvariant();
            if (ext == ".cmd" || ext == ".bat")
            {
                psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                psi.Arguments = "/d /s /c \"" + Quote(exe) + " " + Join(args) + "\"";
            }
            else
            {
                psi.FileName = exe;
                psi.Arguments = Join(args);
            }
            psi.UseShellExecute = false;
            return psi;
        }

        static void KillTree(Process p)
        {
            try
            {
                if (p.HasExited) return;
                var k = new ProcessStartInfo("taskkill.exe", "/PID " + p.Id + " /T /F");
                k.UseShellExecute = false; k.CreateNoWindow = true;
                using (var kp = Process.Start(k)) { kp.WaitForExit(5000); }
            }
            catch { try { p.Kill(); } catch { } }
        }

        // 正在运行的子进程。关闭程序时要把它们都结束掉，不能留在后台继续消耗用量。
        static readonly List<Process> Live = new List<Process>();
        public static void KillAll()
        {
            Process[] all;
            lock (Live) all = Live.ToArray();
            foreach (var p in all) KillTree(p);
        }

        // ---------- 识别限额和未登录 ----------

        static readonly Regex LimitRx = new Regex(
            @"usage limit|rate.?limit|too many requests|\b429\b|quota|limit reached|resource.?exhausted|hit (your|the)( \w+){0,3} limit|limit (will )?resets?|额度|限额|用量上限",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        static readonly Regex LoginRx = new Regex(
            @"not logged in|please run /login|login required|not authenticated|401 unauthorized|invalid api key|please log ?in",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        static readonly Regex BlockedRx = new Regex(
            @"blocked before it reached|network this request came from isn.t allowed|unsupported region|unsupported_country|country, region, or territory not supported",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // 连不上服务时各家命令行的说法
        static readonly Regex NetRx = new Regex(
            @"workspace routing discovery failed|connection failed|error sending request|stream disconnected|failed to connect|connection refused|connection reset|ENOTFOUND|ECONNREFUSED|ECONNRESET|ETIMEDOUT|fetch failed|network error|unable to connect|socket hang up",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool LooksRateLimited(string text)
        {
            return !string.IsNullOrEmpty(text) && LimitRx.IsMatch(text);
        }

        // 输出里还混着 Agent 自己说的话和它运行的命令的结果，只有报错的那几行才能用来判断限额，
        // 否则正在改的代码里出现“rate limit”这种词，就会把一次普通失败误判成账号被限额。
        static bool ErrorLinesLimited(string tail)
        {
            foreach (var line in tail.Split('\n'))
                if (line.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 && LimitRx.IsMatch(line)) return true;
            return false;
        }

        // ---------- 把各家的输出整理成一步一步的内容 ----------

        class Parse
        {
            public string Agent;
            public bool Plain;
            public string Final;
            public bool IsError;
            public string ErrorText;
            public string Model = "", Effort = "", Usage = "", SessionId = "";
            public bool TokensNext;
            public long In, Out;
            public readonly List<LimitInfo> Limits = new List<LimitInfo>();

            // Claude 在运行中报告的套餐限额：哪一种限额、用了多少、什么时候重置
            public void AddLimit(Dictionary<string, object> info)
            {
                if (info == null) return;
                string type = Json.Str(info, "rateLimitType") ?? Json.Str(info, "rate_limit_type") ?? "";
                string name = type == "five_hour" ? "5 小时限额" : type == "seven_day" ? "每周 · 所有模型"
                            : type == "seven_day_opus" ? "每周 · Opus" : type == "seven_day_sonnet" ? "每周 · Sonnet"
                            : type == "overage" ? "超额用量" : type.StartsWith("seven_day_") ? "每周 · " + type.Substring(10) : (type.Length > 0 ? type : "限额");
                object v; double pct = -1;
                if (info.TryGetValue("utilization", out v) && v != null) { try { pct = Convert.ToDouble(v); } catch { } }
                if (pct >= 0 && pct <= 1.0) pct *= 100;
                string reset = "";
                if (info.TryGetValue("resetsAt", out v) && v != null)
                {
                    try
                    {
                        var when = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(Convert.ToDouble(v)).ToLocalTime();
                        reset = (when.Date == DateTime.Now.Date ? "" : when.ToString("M月d日 ")) + when.ToString("H:mm") + " 重置";
                    }
                    catch { }
                }
                var status = Json.Str(info, "status") ?? "";
                if (pct < 0) { if (status == "rejected") pct = 100; else return; }   // 没给百分比的事件不显示
                var l = new LimitInfo(); l.Name = name; l.Percent = Math.Round(pct); l.Reset = reset;
                Limits.RemoveAll(x => x.Name == name);
                Limits.Add(l);
            }
            public readonly StringBuilder Buf = new StringBuilder();                       // Gemini 分片到达的一段话
            public readonly Dictionary<string, Step> Tools = new Dictionary<string, Step>(); // Claude：操作编号 → 那一步，用来补上操作结果
            public readonly StringBuilder Tail = new StringBuilder();
            public readonly HashSet<string> Echo = new HashSet<string>();
            public void Keep(string s)
            {
                if (Echo.Contains(s.Trim())) return;
                lock (Tail)
                {
                    Tail.AppendLine(s);
                    if (Tail.Length > 6000) Tail.Remove(0, Tail.Length - 4000);
                }
            }
        }

        static string Clip(string s, int max)
        {
            s = s ?? "";
            return s.Length > max ? s.Substring(0, max) + "…" : s;
        }

        static string OneLine(string s, int max)
        {
            return Clip((s ?? "").Replace("\r", " ").Replace("\n", " ").Trim(), max);
        }

        static long Num(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return 0;
            try { return Convert.ToInt64(v); } catch { return 0; }
        }

        static string UsageText(long input, long output)
        {
            return input + output > 0 ? "输入 " + input.ToString("N0") + "，输出 " + output.ToString("N0") + " tokens" : "";
        }

        // Claude 的一次操作 → 一行人话
        static Step ClaudeTool(string name, Dictionary<string, object> input)
        {
            string file = Json.Str(input, "file_path") ?? Json.Str(input, "path") ?? Json.Str(input, "notebook_path") ?? "";
            string cmd = Json.Str(input, "command") ?? "";
            string text;
            switch (name)
            {
                case "Read": text = "读取 " + file; break;
                case "Write": text = "写入 " + file; break;
                case "Edit": case "MultiEdit": case "NotebookEdit": text = "编辑 " + file; break;
                case "Bash": case "PowerShell": text = "运行命令 " + OneLine(cmd, 110); break;
                case "Grep": text = "搜索 " + OneLine(Json.Str(input, "pattern"), 90); break;
                case "Glob": text = "查找文件 " + OneLine(Json.Str(input, "pattern"), 90); break;
                case "WebFetch": text = "打开网页 " + OneLine(Json.Str(input, "url"), 100); break;
                case "WebSearch": text = "联网搜索 " + OneLine(Json.Str(input, "query"), 90); break;
                case "Task": case "Agent": text = "派出子代理 " + OneLine(Json.Str(input, "description"), 90); break;
                case "TodoWrite": text = "更新任务清单"; break;
                default: text = (name ?? "操作") + " " + OneLine(file.Length > 0 ? file : cmd, 100); break;
            }
            string detail = cmd.Length > 0 ? cmd : (input != null ? Clip(Json.Save(input), 2000) : null);
            return new Step("tool", text.Trim(), detail);
        }

        static string ResultText(object content)
        {
            var s = content as string;
            if (s != null) return s;
            var list = content as IList;
            if (list == null) return "";
            var sb = new StringBuilder();
            foreach (var o in list)
            {
                var d = o as Dictionary<string, object>;
                var t = Json.Str(d, "text");
                if (!string.IsNullOrEmpty(t)) sb.AppendLine(t);
            }
            return sb.ToString();
        }

        // Codex 把命令包在一层 PowerShell 里，显示时去掉外壳
        static string ShortCommand(string command)
        {
            command = (command ?? "").Trim();
            int i = command.IndexOf(" -Command ", StringComparison.OrdinalIgnoreCase);
            if (i > 0) command = command.Substring(i + 10).Trim().Trim('"', '\'');
            return OneLine(command, 110);
        }

        static readonly Regex CodexModelRx = new Regex(@"^model:\s*(\S+)", RegexOptions.Compiled);
        static readonly Regex CodexEffortRx = new Regex(@"^reasoning effort:\s*(\S+)", RegexOptions.Compiled);
        static readonly Regex NumberRx = new Regex(@"^[\d,\.]+$", RegexOptions.Compiled);

        // Codex 的普通输出模式（只在“测试连接”时用）：开头会打印它实际用的模型和推理强度，结尾打印用量
        static void ScanCodex(Parse st, string line)
        {
            var t = line.Trim();
            lock (st)
            {
                if (st.TokensNext) { st.TokensNext = false; if (NumberRx.IsMatch(t)) st.Usage = t + " tokens"; return; }
                if (t == "tokens used") { st.TokensNext = true; return; }
                if (st.Model.Length == 0) { var m = CodexModelRx.Match(t); if (m.Success) { st.Model = m.Groups[1].Value; return; } }
                if (st.Effort.Length == 0) { var m = CodexEffortRx.Match(t); if (m.Success) st.Effort = m.Groups[1].Value; }
            }
        }

        static void FlushBuf(Parse st, Action<Step> emit)
        {
            if (st.Buf.Length == 0) return;
            var text = st.Buf.ToString().Trim();
            st.Buf.Length = 0;
            if (text.Length > 0) emit(new Step("say", text));
        }

        static void Translate(Parse st, string line, Action<Step> emit)
        {
            if (line.Length == 0) return;
            if (st.Agent == "codex" && st.Plain) { ScanCodex(st, line); st.Keep(line); emit(new Step("info", line)); return; }
            var d = line[0] == '{' ? Json.Parse(line) : null;
            if (d == null) { st.Keep(line); emit(new Step("info", line)); return; }
            var type = Json.Str(d, "type") ?? "";

            if (st.Agent == "claude")
            {
                if (type == "system")
                {
                    if (Json.Str(d, "subtype") != "init") return;
                    st.Model = Json.Str(d, "model") ?? "";
                    st.SessionId = Json.Str(d, "session_id") ?? st.SessionId;
                    emit(new Step("info", "已连接" + (st.Model.Length > 0 ? "，模型 " + st.Model : "")));
                }
                else if (type == "assistant")
                {
                    var content = Json.Arr(Json.Obj(d, "message"), "content");
                    if (content == null) return;
                    foreach (var o in content)
                    {
                        var c = o as Dictionary<string, object>;
                        var ct = Json.Str(c, "type");
                        if (ct == "text") { var t = (Json.Str(c, "text") ?? "").Trim(); if (t.Length > 0) emit(new Step("say", t)); }
                        else if (ct == "thinking") { var t = (Json.Str(c, "thinking") ?? "").Trim(); if (t.Length > 0) emit(new Step("think", t)); }
                        else if (ct == "tool_use")
                        {
                            var step = ClaudeTool(Json.Str(c, "name"), Json.Obj(c, "input"));
                            var id = Json.Str(c, "id");
                            if (!string.IsNullOrEmpty(id)) st.Tools[id] = step;
                            emit(step);
                        }
                    }
                }
                else if (type == "user")
                {
                    // 操作的结果：补到对应那一步的细节里，展开时能看到
                    var content = Json.Arr(Json.Obj(d, "message"), "content");
                    if (content == null) return;
                    foreach (var o in content)
                    {
                        var c = o as Dictionary<string, object>;
                        if (Json.Str(c, "type") != "tool_result") continue;
                        Step step; object raw;
                        var id = Json.Str(c, "tool_use_id");
                        if (id == null || !st.Tools.TryGetValue(id, out step) || !c.TryGetValue("content", out raw)) continue;
                        var text = ResultText(raw).Trim();
                        if (text.Length > 0) step.Detail = (step.Detail ?? "") + "\n\n结果：\n" + Clip(text, 4000);
                    }
                }
                else if (type == "result")
                {
                    st.Final = Json.Str(d, "result") ?? "";
                    st.SessionId = Json.Str(d, "session_id") ?? st.SessionId;
                    st.IsError = Json.Bool(d, "is_error") || (Json.Str(d, "subtype") ?? "success") != "success";
                    if (st.IsError) { st.ErrorText = st.Final.Length > 0 ? st.Final : (Json.Str(d, "subtype") ?? "出错"); st.Keep(st.ErrorText); }
                    var u = Json.Obj(d, "usage");
                    if (u != null)
                    {
                        st.In = Num(u, "input_tokens") + Num(u, "cache_creation_input_tokens") + Num(u, "cache_read_input_tokens"); st.Out = Num(u, "output_tokens");
                        st.Usage = UsageText(st.In, st.Out);
                    }
                }
                else if (type == "rate_limit_event") st.AddLimit(Json.Obj(d, "rate_limit_info"));
            }
            else if (st.Agent == "codex")
            {
                if (type == "thread.started") { st.SessionId = Json.Str(d, "thread_id") ?? ""; emit(new Step("info", "已连接 Codex")); }
                else if (type == "item.completed")
                {
                    var item = Json.Obj(d, "item");
                    var it = Json.Str(item, "type") ?? "";
                    if (it == "agent_message") { var t = (Json.Str(item, "text") ?? "").Trim(); if (t.Length > 0) { st.Final = t; emit(new Step("say", t)); } }
                    else if (it == "reasoning") { var t = (Json.Str(item, "text") ?? "").Trim(); if (t.Length > 0) emit(new Step("think", t)); }
                    else if (it == "command_execution")
                    {
                        var cmd = Json.Str(item, "command") ?? "";
                        var outText = (Json.Str(item, "aggregated_output") ?? "").Trim();
                        var code = Json.Str(item, "exit_code");
                        emit(new Step("tool", "运行命令 " + ShortCommand(cmd),
                            cmd + (outText.Length > 0 ? "\n\n结果：\n" + Clip(outText, 4000) : "") + (string.IsNullOrEmpty(code) || code == "0" ? "" : "\n\n退出码 " + code)));
                    }
                    else if (it == "file_change")
                    {
                        var changes = Json.Arr(item, "changes");
                        var names = new List<string>(); var lines = new StringBuilder();
                        if (changes != null)
                            foreach (var o in changes)
                            {
                                var c = o as Dictionary<string, object>;
                                var p = Json.Str(c, "path") ?? "";
                                if (p.Length == 0) continue;
                                names.Add(Path.GetFileName(p));
                                lines.AppendLine((Json.Str(c, "kind") ?? "修改") + "  " + p);
                            }
                        emit(new Step("tool", "编辑 " + (names.Count > 0 ? OneLine(string.Join("、", names.ToArray()), 100) : "文件"), lines.ToString().Trim()));
                    }
                    else if (it == "mcp_tool_call") emit(new Step("tool", "调用工具 " + (Json.Str(item, "server") ?? "") + "/" + (Json.Str(item, "tool") ?? "")));
                    else if (it == "web_search") emit(new Step("tool", "联网搜索 " + OneLine(Json.Str(item, "query"), 90)));
                    else if (it == "todo_list") emit(new Step("tool", "更新任务清单"));
                    else if (it == "error")
                    {
                        // 这类是提示性质的（比如实验功能的提醒），不代表任务失败
                        var m = Json.Str(item, "message") ?? "";
                        st.Keep(m);
                        if (m.IndexOf("Under-development", StringComparison.OrdinalIgnoreCase) < 0) emit(new Step("info", OneLine(m, 200)));
                    }
                }
                else if (type == "turn.completed")
                {
                    var u = Json.Obj(d, "usage");
                    if (u != null) { st.In = Num(u, "input_tokens"); st.Out = Num(u, "output_tokens"); st.Usage = UsageText(st.In, st.Out); }
                }
                else if (type == "turn.failed" || type == "error")
                {
                    var m = Json.Str(Json.Obj(d, "error"), "message") ?? Json.Str(d, "message") ?? line;
                    st.ErrorText = m; st.Keep("error: " + m);
                    if (type == "turn.failed") st.IsError = true;
                }
            }
            else // gemini
            {
                if (type == "init") { st.Model = Json.Str(d, "model") ?? ""; emit(new Step("info", "已连接" + (st.Model.Length > 0 ? "，模型 " + st.Model : ""))); }
                else if (type == "message")
                {
                    if (Json.Str(d, "role") != "assistant") return;
                    var t = Json.Str(d, "content") ?? "";
                    st.Final = (st.Final ?? "") + t;
                    st.Buf.Append(t);
                    if (!Json.Bool(d, "delta")) FlushBuf(st, emit);
                }
                else if (type == "tool_use")
                {
                    FlushBuf(st, emit);
                    var input = Json.Obj(d, "parameters");
                    emit(new Step("tool", (Json.Str(d, "tool_name") ?? Json.Str(d, "name") ?? "操作") + " "
                        + OneLine(Json.Str(input, "file_path") ?? Json.Str(input, "path") ?? Json.Str(input, "command") ?? Json.Str(input, "pattern") ?? "", 100),
                        input != null ? Clip(Json.Save(input), 2000) : null));
                }
                else if (type == "error")
                {
                    FlushBuf(st, emit);
                    st.IsError = true;
                    st.ErrorText = Json.Str(d, "message") ?? Json.Str(d, "error") ?? line;
                    st.Keep(st.ErrorText);
                }
                else if (type == "result")
                {
                    FlushBuf(st, emit);
                    var status = Json.Str(d, "status") ?? "success";
                    if (status != "success") { st.IsError = true; if (st.ErrorText == null) st.ErrorText = Json.Str(d, "error") ?? status; }
                    long total = Num(Json.Obj(d, "stats"), "total_tokens");
                    if (total > 0) st.Usage = total.ToString("N0") + " tokens";
                }
            }
        }

        // ---------- 真实运行一次 ----------

        // 启动官方命令行，把提示词从标准输入送进去，一步一步读回它做了什么。
        // perm: readonly（只读分析）| edit（可改文件）| full（完全自动，可执行命令）
        // resume: 上一轮的会话编号，接着那一轮继续；plain: 用普通输出模式（“测试连接”用，能看到 Codex 自己报告的模型）
        public static Task<RunResult> Run(AppConfig cfg, Account acc, string cwd, string prompt, string perm,
                                          Action<Step> onStep, CancellationToken ct, string resume = null, bool plain = false)
        {
            return Task.Run(() =>
            {
                var res = new RunResult();
                string exe = Find(acc.Agent, cfg);
                if (exe == null)
                {
                    res.Error = "没有找到 " + DisplayName(acc.Agent) + " 的命令行程序。请先安装它，或在“设置”里填写程序路径。";
                    return res;
                }
                if (!Directory.Exists(cwd)) { res.Error = "项目文件夹不存在：" + cwd; return res; }

                var args = new List<string>();
                string outFile = null;
                bool hasModel = !string.IsNullOrEmpty(acc.Model), hasEffort = !string.IsNullOrEmpty(acc.Effort);
                if (acc.Agent == "claude")
                {
                    args.Add("-p"); args.Add("--output-format"); args.Add("stream-json"); args.Add("--verbose");
                    args.Add("--permission-mode");
                    args.Add(perm == "full" ? "bypassPermissions" : perm == "edit" ? "acceptEdits" : "dontAsk");
                    if (hasModel) { args.Add("--model"); args.Add(acc.Model); }
                    if (hasEffort) { args.Add("--effort"); args.Add(acc.Effort); }
                    if (!string.IsNullOrEmpty(resume)) { args.Add("--resume"); args.Add(resume); }
                }
                else if (acc.Agent == "codex")
                {
                    outFile = Path.Combine(Path.GetTempPath(), "ac-" + Guid.NewGuid().ToString("N") + ".txt");
                    string sandbox = perm == "edit" ? "workspace-write" : "read-only";
                    bool resuming = !plain && !string.IsNullOrEmpty(resume);
                    args.Add("exec");
                    if (resuming) args.Add("resume");
                    if (!plain) args.Add("--json");
                    args.Add("--skip-git-repo-check");
                    if (!resuming) { args.Add("--color"); args.Add("never"); args.Add("-C"); args.Add(cwd); }
                    if (perm == "full") args.Add("--dangerously-bypass-approvals-and-sandbox");
                    else if (resuming) { args.Add("-c"); args.Add("sandbox_mode=" + sandbox); }
                    else { args.Add("-s"); args.Add(sandbox); }
                    if (hasModel) { args.Add("-m"); args.Add(acc.Model); }
                    if (hasEffort) { args.Add("-c"); args.Add("model_reasoning_effort=" + acc.Effort); }
                    args.Add("-o"); args.Add(outFile);
                    if (resuming) args.Add(resume);
                    args.Add("-");
                }
                else
                {
                    args.Add("--output-format"); args.Add("stream-json");
                    args.Add("--approval-mode");
                    args.Add(perm == "full" ? "yolo" : perm == "edit" ? "auto_edit" : "default");
                    if (hasModel) { args.Add("-m"); args.Add(acc.Model); }
                }

                var psi = Start(exe, args);
                psi.WorkingDirectory = cwd;
                psi.CreateNoWindow = true;
                psi.RedirectStandardInput = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
                try { ApplyProfile(acc, psi.Environment); }
                catch (Exception ex) { res.Error = "准备账号目录失败：" + ex.Message; return res; }

                var st = new Parse(); st.Agent = acc.Agent; st.Plain = plain;
                // 有的工具会把提示词原样回显出来。回显的内容不能算进“错误信息”，
                // 否则目标里恰好写了“限额”之类的词，普通失败就会被误判成触发限额。
                foreach (var pl in prompt.Split('\n')) { var x = pl.Trim(); if (x.Length > 0) st.Echo.Add(x); }
                Action<Step> emit = s => { try { onStep(s); } catch { } };
                bool showStderr = plain || acc.Agent != "codex";   // Codex 的错误输出里大多是与任务无关的杂项日志
                var p = new Process(); p.StartInfo = psi;
                p.OutputDataReceived += (s, e) =>
                {
                    if (e.Data == null) return;
                    try { Translate(st, e.Data, emit); } catch (Exception ex) { Store.LogError("解析输出", ex); }
                };
                p.ErrorDataReceived += (s, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    if (st.Agent == "codex" && plain) ScanCodex(st, e.Data);
                    st.Keep(e.Data);
                    if (showStderr) emit(new Step("info", e.Data));
                };

                try { p.Start(); }
                catch (Exception ex) { res.Error = "启动 " + DisplayName(acc.Agent) + " 失败：" + ex.Message; return res; }
                lock (Live) Live.Add(p);

                // 先开始读输出，再写提示词：否则提示词很长而对方先输出一大段时，两边会互相等住
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                try
                {
                    var bytes = new UTF8Encoding(false).GetBytes(prompt);
                    p.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
                    p.StandardInput.BaseStream.Flush();
                    p.StandardInput.Close();
                }
                catch { }

                var deadline = DateTime.Now.AddMinutes(cfg.TimeoutMinutes);
                bool timedOut = false;
                while (!p.WaitForExit(300))
                {
                    if (ct.IsCancellationRequested) { res.Cancelled = true; KillTree(p); break; }
                    if (DateTime.Now > deadline) { timedOut = true; KillTree(p); break; }
                }
                // 等输出读完；子进程偶尔会留下后台进程占着管道，所以只等有限时间
                try { Task.Run(() => p.WaitForExit()).Wait(4000); } catch { }
                try { res.ExitCode = p.HasExited ? p.ExitCode : -1; } catch { res.ExitCode = -1; }
                lock (Live) Live.Remove(p);
                try { p.Dispose(); } catch { }
                try { FlushBuf(st, emit); } catch { }

                string tail; lock (st.Tail) { tail = st.Tail.ToString().Trim(); }
                if (acc.Agent == "codex")
                {
                    try
                    {
                        if (File.Exists(outFile))
                        {
                            var last = File.ReadAllText(outFile, Encoding.UTF8).Trim();
                            File.Delete(outFile);
                            if (last.Length > 0) st.Final = last;
                        }
                    }
                    catch { }
                }

                res.SessionId = st.SessionId;
                res.Usage = st.Usage;
                res.TokensIn = st.In; res.TokensOut = st.Out;
                res.Limits = st.Limits;
                if (st.Model.Length > 0) res.Model = st.Model + (st.Effort.Length > 0 ? "（推理强度 " + st.Effort + "）" : "");
                else if (hasModel) res.Model = acc.Model + (hasEffort ? "（推理强度 " + acc.Effort + "）" : "");
                if (res.Cancelled) { res.Error = "已停止"; return res; }
                if (timedOut) { res.Error = "超过 " + cfg.TimeoutMinutes + " 分钟未完成，已终止。"; return res; }

                res.Text = (st.Final ?? "").Trim();
                res.Ok = res.ExitCode == 0 && !st.IsError && res.Text.Length > 0;
                if (!res.Ok)
                {
                    res.Error = !string.IsNullOrEmpty(st.ErrorText) ? st.ErrorText
                              : tail.Length > 0 ? (tail.Length > 1500 ? tail.Substring(tail.Length - 1500) : tail)
                              : "程序退出码 " + res.ExitCode + "，没有返回内容。";
                    res.RateLimited = LooksRateLimited(st.ErrorText) || ErrorLinesLimited(tail);
                    // 套餐限额用完：用命令行报告的那一项限额和重置时间，说清楚是哪个限额、什么时候恢复
                    var full = res.Limits.Find(l => l.Percent >= 100);
                    if (res.RateLimited && full != null)
                        res.Error = "这个账号的" + full.Name + "已经用完" + (full.Reset.Length > 0 ? "，" + full.Reset : "") + "。（原始信息：" + OneLine(res.Error, 160) + "）";
                    if (BlockedRx.IsMatch(res.Error))
                    {
                        res.RateLimited = false;   // 这是网络被拒，不是账号限额，换账号也没用
                        res.Error = "网络被拒绝：从这台电脑直接访问不了 " + DisplayName(acc.Agent) + " 的服务（通常是所在地区不支持）。请先打开系统代理再试，当前"
                                  + NetworkNote() + "。（原始信息：" + OneLine(res.Error, 200) + "）";
                    }
                    else if (NetRx.IsMatch(res.Error))
                    {
                        res.RateLimited = false;   // 连不上服务，和账号限额无关
                        res.Error = "网络连接失败：现在连不上 " + DisplayName(acc.Agent) + " 的服务。当前" + NetworkNote()
                                  + "，请确认代理或 VPN 正常后再试。（原始信息：" + OneLine(res.Error, 160) + "）";
                    }
                    else if (!res.RateLimited && LoginRx.IsMatch(res.Error))
                        res.Error = "这个账号还没有登录，或登录已过期。请在左侧菜单里点这个账号，再点“登录”。（原始信息：" + res.Error.Trim() + "）";
                }
                return res;
            });
        }

        // ---------- 查询类的短命令：登录状态、模型目录、退出登录 ----------

        class ToolOut { public int Exit = -1; public string Out = "", Err = ""; public bool TimedOut; }

        static ToolOut Tool(string exe, List<string> args, Account acc, int timeoutMs)
        {
            var o = new ToolOut();
            var psi = Start(exe, args);
            psi.CreateNoWindow = true;
            psi.RedirectStandardInput = true; psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8; psi.StandardErrorEncoding = Encoding.UTF8;
            psi.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            ApplyProfile(acc, psi.Environment);
            var so = new StringBuilder(); var se = new StringBuilder();
            using (var p = new Process())
            {
                p.StartInfo = psi;
                p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (so) so.AppendLine(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (se) se.AppendLine(e.Data); };
                p.Start();
                try { p.StandardInput.Close(); } catch { }
                p.BeginOutputReadLine(); p.BeginErrorReadLine();
                if (!p.WaitForExit(timeoutMs)) { o.TimedOut = true; KillTree(p); }
                try { Task.Run(() => p.WaitForExit()).Wait(3000); } catch { }
                try { if (p.HasExited) o.Exit = p.ExitCode; } catch { }
            }
            lock (so) o.Out = so.ToString();
            lock (se) o.Err = se.ToString();
            return o;
        }

        // Codex 的命令行只报告“已登录”，不报告是哪个账号。账号邮箱只存在它自己的登录文件里，
        // 所以这里读那个文件，只取出身份信息里的邮箱和套餐用于显示：不读取、不保存、不传出其中的令牌。
        static void CodexIdentity(Account acc, LoginInfo info)
        {
            try
            {
                string home = acc.UseDefaultLogin || string.IsNullOrEmpty(acc.ProfileDir)
                    ? (Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"))
                    : acc.ProfileDir;
                var file = Path.Combine(home, "auth.json");
                if (!File.Exists(file)) return;
                var idToken = Json.Str(Json.Obj(Json.Parse(File.ReadAllText(file, Encoding.UTF8)), "tokens"), "id_token");
                if (string.IsNullOrEmpty(idToken)) return;
                var parts = idToken.Split('.');
                if (parts.Length < 2) return;
                string b64 = parts[1].Replace('-', '+').Replace('_', '/');
                while (b64.Length % 4 != 0) b64 += "=";
                var claims = Json.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
                var email = Json.Str(claims, "email");
                if (!string.IsNullOrEmpty(email)) info.Identity = email;
                var plan = Json.Str(Json.Obj(claims, "https://api.openai.com/auth"), "chatgpt_plan_type");
                if (!string.IsNullOrEmpty(plan)) info.Plan = plan;
            }
            catch { }
        }

        // 账号是否已登录、登录的是谁
        public static Task<LoginInfo> Status(AppConfig cfg, Account acc)
        {
            return Task.Run(() =>
            {
                var info = new LoginInfo();
                string exe = Find(acc.Agent, cfg);
                if (exe == null) { info.State = "nocli"; return info; }
                try
                {
                    if (acc.Agent == "claude")
                    {
                        var o = Tool(exe, new List<string> { "auth", "status", "--json" }, acc, 25000);
                        var d = Json.Extract(o.Out);
                        if (d == null) return info;
                        info.State = Json.Bool(d, "loggedIn") ? "in" : "out";
                        if (info.State != "in") return info;
                        string org = Json.Str(d, "orgName") ?? Json.Str(d, "organizationName") ?? "";
                        info.Identity = Json.Str(d, "email") ?? Json.Str(d, "emailAddress") ?? "";
                        if (info.Identity.Length == 0) info.Identity = org.Length > 0 ? org : "登录方式 " + (Json.Str(d, "authMethod") ?? "未知");
                        info.Plan = Json.Str(d, "subscriptionType") ?? Json.Str(d, "plan") ?? "";
                        if (info.Plan.Length == 0 && org.Length > 0 && org != info.Identity) info.Plan = org;
                    }
                    else if (acc.Agent == "codex")
                    {
                        var o = Tool(exe, new List<string> { "login", "status" }, acc, 25000);
                        var text = (o.Out + " " + o.Err).Trim();
                        bool notIn = text.IndexOf("Not logged in", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (!notIn && o.Exit == 0 && text.IndexOf("Logged in", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            info.State = "in";
                            info.Identity = text.IndexOf("API key", StringComparison.OrdinalIgnoreCase) >= 0 ? "API key" : "ChatGPT 账号";
                            CodexIdentity(acc, info);
                        }
                        else if (notIn || o.Exit != 0) info.State = "out";
                    }
                    // Gemini CLI 没有查询登录状态的命令，保持“未知”，由“测试连接”来确认
                }
                catch (Exception ex) { Store.LogError("查询登录状态", ex); }
                return info;
            });
        }

        static readonly Dictionary<string, List<ModelInfo>> ModelCache = new Dictionary<string, List<ModelInfo>>();

        // 可选的模型。Codex 的列表来自它自己的模型目录；Claude 用命令行认识的别名；Gemini 没有可查询的目录。
        public static Task<List<ModelInfo>> Models(AppConfig cfg, Account acc)
        {
            return Task.Run(() =>
            {
                string key = acc.Agent + "|" + (acc.UseDefaultLogin ? "" : acc.ProfileDir);
                lock (ModelCache) { List<ModelInfo> hit; if (ModelCache.TryGetValue(key, out hit)) return hit; }
                var list = new List<ModelInfo>();
                try
                {
                    if (acc.Agent == "claude")
                    {
                        // 具体的模型名：是 Claude 命令行自己把 fable / opus / sonnet / haiku 这几个别名解析出来的结果
                        var known = new[] { "claude-fable-5-2|Fable 5.2", "claude-opus-5-5|Opus 5.5", "claude-sonnet-5-5|Sonnet 5.5", "claude-haiku-5-5|Haiku 5.5" };
                        foreach (var item in known)
                        {
                            var kv = item.Split('|');
                            var m = new ModelInfo(); m.Id = kv[0]; m.Name = kv[1];
                            m.Efforts.AddRange(new[] { "low", "medium", "high", "xhigh", "max" });
                            list.Add(m);
                        }
                    }
                    else if (acc.Agent == "codex")
                    {
                        string exe = Find("codex", cfg);
                        if (exe != null)
                        {
                            var o = Tool(exe, new List<string> { "debug", "models" }, acc, 40000);
                            object root = Json.Any(o.Out);
                            var asDict = root as Dictionary<string, object>;
                            var arr = asDict != null ? Json.Arr(asDict, "models") : root as IList;
                            if (arr != null)
                                foreach (var x in arr)
                                {
                                    var d = x as Dictionary<string, object>;
                                    if (d == null || (Json.Str(d, "visibility") ?? "list") != "list") continue;
                                    var m = new ModelInfo(); m.Id = Json.Str(d, "slug") ?? ""; m.Name = Json.Str(d, "display_name") ?? m.Id;
                                    var levels = Json.Arr(d, "supported_reasoning_levels");
                                    if (levels != null)
                                        foreach (var lv in levels)
                                        {
                                            var ld = lv as Dictionary<string, object>;
                                            var eff = ld != null ? Json.Str(ld, "effort") : Convert.ToString(lv);
                                            if (!string.IsNullOrEmpty(eff)) m.Efforts.Add(eff);
                                        }
                                    if (m.Id.Length > 0) list.Add(m);
                                }
                        }
                    }
                }
                catch (Exception ex) { Store.LogError("读取模型目录", ex); }
                if (list.Count > 0 || acc.Agent == "gemini") lock (ModelCache) ModelCache[key] = list;
                return list;
            });
        }

        // 退出登录：交给官方命令行自己清除它的登录。返回 null 表示成功，否则是说明。
        public static Task<string> Logout(AppConfig cfg, Account acc)
        {
            return Task.Run(() =>
            {
                string exe = Find(acc.Agent, cfg);
                if (exe == null) return "没有找到 " + DisplayName(acc.Agent) + " 的命令行程序。";
                if (acc.Agent == "gemini") return "Gemini CLI 没有退出登录的命令。需要换账号时，删除这个账号再新建一个。";
                try
                {
                    var args = acc.Agent == "claude" ? new List<string> { "auth", "logout" } : new List<string> { "logout" };
                    var o = Tool(exe, args, acc, 30000);
                    return o.TimedOut ? "退出登录超时，请稍后再试。" : null;
                }
                catch (Exception ex) { return "退出登录失败：" + ex.Message; }
            });
        }

        // 打开一个命令行窗口，让用户自己走官方登录流程。返回 null 表示已打开，否则是错误信息。
        public static string OpenLogin(AppConfig cfg, Account acc)
        {
            string exe = Find(acc.Agent, cfg);
            if (exe == null) return "没有找到 " + DisplayName(acc.Agent) + " 的命令行程序。请先安装它，或在“设置”里填写程序路径。";
            string sub = acc.Agent == "claude" ? " auth login" : acc.Agent == "codex" ? " login" : "";
            string title = Regex.Replace("登录 " + acc.Display, "[&|<>^\"%]", " ");
            var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe");
            psi.Arguments = "/d /s /k \"title " + title + " & \"" + exe + "\"" + sub + "\"";
            psi.UseShellExecute = false;
            psi.CreateNoWindow = false;
            psi.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            try
            {
                ApplyProfile(acc, psi.Environment);
                Process.Start(psi);
                return null;
            }
            catch (Exception ex) { return "打开登录窗口失败：" + ex.Message; }
        }
    }
}
