using System;
using System.Collections.Generic;
using System.IO;
using System.Management;

namespace AC
{
    // 登录向导背后的文件夹操作。
    //
    // 为什么需要它：Claude 桌面版用 Google 登录时，登录结果只会送回“默认位置”的那一份 Claude，
    // 多开出来的窗口收不到。所以新账号要在默认位置登录，登完再把那份数据搬去当新窗口：
    //   1. 把你现在这份数据改个名放到一边（不删除）
    //   2. Claude 以空白状态打开，你用 Google 登录新账号
    //   3. 把新登录产生的数据搬去当新窗口，再把原来那份改回原名
    // 全程只做“改名”，不删除任何东西；中途出任何问题都能把原来那份改回去。
    public static class SlotSwap
    {
        const string ParkedSuffix = ".kms-parked";
        public static string DataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Claude-Data");

        static List<string> Dirs(string pattern)
        {
            var list = new List<string>();
            try { if (Directory.Exists(DataRoot)) list.AddRange(Directory.GetDirectories(DataRoot, pattern)); } catch { }
            return list;
        }

        // 放在一边的那份（向导进行中才有）
        public static string ParkedDir()
        {
            var found = Dirs("claude_*" + ParkedSuffix);
            return found.Count > 0 ? found[0] : null;
        }

        // 默认位置：%LOCALAPPDATA%\Claude-Data\claude_<编号>
        public static string DefaultDir()
        {
            var parked = ParkedDir();
            if (parked != null) return parked.Substring(0, parked.Length - ParkedSuffix.Length);
            foreach (var d in Dirs("claude_*"))
                if (!d.EndsWith(ParkedSuffix, StringComparison.OrdinalIgnoreCase)) return d;
            return null;
        }

        // 新窗口的数据放在同一个盘的同一层，这样搬动只是改名，瞬间完成
        public static string WindowDir(string id) { return Path.Combine(DataRoot, "kms-" + id); }

        // 还有没有 Claude 在运行（应用本身，或它自带的命令行）。运行中不能挪它的数据。
        public static int ClaudeProcessCount()
        {
            int n = 0;
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT ExecutablePath FROM Win32_Process WHERE Name='claude.exe'"))
                    foreach (ManagementObject o in s.Get())
                    {
                        var path = Convert.ToString(o["ExecutablePath"] ?? "");
                        if (path.IndexOf("\\WindowsApps\\Claude_", StringComparison.OrdinalIgnoreCase) >= 0
                            || path.StartsWith(DataRoot, StringComparison.OrdinalIgnoreCase)) n++;
                    }
            }
            catch (Exception ex) { Store.LogError("查询 Claude 进程", ex); }
            return n;
        }

        // 第 1 步：把现在这份数据放到一边。返回 null 表示成功，否则是说明。
        public static string Park()
        {
            try
            {
                if (ParkedDir() != null) return "上一次的登录向导还没有结束，请先完成或放弃它。";
                var def = DefaultDir();
                if (def == null || !Directory.Exists(def)) return "没有找到 Claude 的数据文件夹（" + DataRoot + "）。";
                Directory.Move(def, def + ParkedSuffix);
                return null;
            }
            catch (Exception ex) { return "没能挪开现在的数据（多半是还有 Claude 在运行）：" + ex.Message; }
        }

        // 第 3 步：把新登录产生的数据搬去当新窗口，再把原来那份放回来。
        public static string Finish(string targetDir)
        {
            var parked = ParkedDir();
            if (parked == null) return "没有进行中的登录向导。";
            var def = parked.Substring(0, parked.Length - ParkedSuffix.Length);
            try
            {
                if (!Directory.Exists(def))
                {
                    Directory.Move(parked, def);   // 没有产生新数据：直接恢复原样
                    return "默认位置是空的，说明刚才没有登录成功。已经把原来的数据恢复好了，可以重新来一次。";
                }
                if (Directory.Exists(targetDir)) Directory.Move(targetDir, targetDir + ".old-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                Directory.Move(def, targetDir);
            }
            catch (Exception ex)
            {
                return "没能把新登录的数据搬过去：" + ex.Message + "\n原来的数据还在 " + parked + "，没有丢。关掉所有 Claude 后再点一次“登录向导”可以继续。";
            }
            try { Directory.Move(parked, def); }
            catch (Exception ex) { return "新窗口已经就绪，但原来的数据没能改回原名：" + ex.Message + "\n它还在 " + parked + "。关掉所有 Claude 后再打开 KMS多控，会自动恢复。"; }
            return null;
        }

        // 放弃：把原来那份改回去。刚才登录产生的数据（如果有）改名留着，不删除。
        public static string Restore()
        {
            var parked = ParkedDir();
            if (parked == null) return null;
            var def = parked.Substring(0, parked.Length - ParkedSuffix.Length);
            try
            {
                if (Directory.Exists(def)) Directory.Move(def, Path.Combine(DataRoot, "kms-abandoned-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
                Directory.Move(parked, def);
                return null;
            }
            catch (Exception ex) { return "没能恢复原来的数据：" + ex.Message + "\n它还在 " + parked + "。请先关掉所有 Claude 窗口再试。"; }
        }
    }
}
