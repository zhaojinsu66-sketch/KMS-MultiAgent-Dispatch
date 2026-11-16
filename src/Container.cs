using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace AC
{
    // 进程看守：盯住一个正在运行的程序。它之后每启动一个新进程，这里都会立刻收到通知
    // （用的是 Windows 的“作业对象”，不注入、不修改被盯的程序）。
    public sealed class ProcWatch : IDisposable
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateJobObject(IntPtr attrs, string name);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateIoCompletionPort(IntPtr file, IntPtr existing, UIntPtr key, uint threads);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobPort info, int size);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetQueuedCompletionStatus(IntPtr port, out uint bytes, out UIntPtr key, out IntPtr overlapped, uint ms);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool TerminateProcess(IntPtr h, uint code);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
        [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr h, int infoClass, IntPtr buffer, int length, out int returned);

        [StructLayout(LayoutKind.Sequential)] struct JobPort { public IntPtr Key; public IntPtr Port; }

        [DllImport("ntdll.dll")] static extern int NtSuspendProcess(IntPtr h);

        const uint QueryLimited = 0x1000, VmRead = 0x0010, Terminate = 0x0001, SetQuota = 0x0100, SuspendResume = 0x0800;
        static readonly IntPtr Invalid = new IntPtr(-1);
        public static int HoldMs = 1500;   // 被接手的进程先暂停这么久再结束

        IntPtr _job = IntPtr.Zero, _port = IntPtr.Zero;
        Thread _thread;
        volatile bool _stop;

        public int RootPid { get; private set; }

        // 新进程出现时调用（在后台线程上）：进程号、它的上一级进程号、程序路径、完整启动参数。
        // 返回 true 表示“这个进程由 KMS 接手”，看守会立刻结束它。
        public Func<int, int, string, string, bool> OnProcess;

        // 盯住进程 pid。返回 null 表示没盯上，原因写在 error 里。
        public static ProcWatch Attach(int pid, Func<int, int, string, string, bool> onProcess, out string error)
        {
            error = null;
            var w = new ProcWatch();
            w.RootPid = pid; w.OnProcess = onProcess;
            IntPtr h = IntPtr.Zero;
            try
            {
                w._job = CreateJobObject(IntPtr.Zero, null);
                if (w._job == IntPtr.Zero) { error = "CreateJobObject " + Marshal.GetLastWin32Error(); w.Dispose(); return null; }
                w._port = CreateIoCompletionPort(Invalid, IntPtr.Zero, UIntPtr.Zero, 1);
                if (w._port == IntPtr.Zero) { error = "CreateIoCompletionPort " + Marshal.GetLastWin32Error(); w.Dispose(); return null; }
                var jp = new JobPort(); jp.Key = new IntPtr(1); jp.Port = w._port;
                if (!SetInformationJobObject(w._job, 7, ref jp, Marshal.SizeOf(typeof(JobPort)))) { error = "SetInformationJobObject " + Marshal.GetLastWin32Error(); w.Dispose(); return null; }
                h = OpenProcess(SetQuota | Terminate | QueryLimited, false, pid);
                if (h == IntPtr.Zero) { error = "OpenProcess " + Marshal.GetLastWin32Error(); w.Dispose(); return null; }
                if (!AssignProcessToJobObject(w._job, h)) { error = "AssignProcessToJobObject " + Marshal.GetLastWin32Error(); w.Dispose(); return null; }
            }
            finally { if (h != IntPtr.Zero) CloseHandle(h); }
            w._thread = new Thread(w.Loop); w._thread.IsBackground = true; w._thread.Name = "ProcWatch " + pid;
            w._thread.Start();
            return w;
        }

        public bool RootAlive
        {
            get { try { var p = Process.GetProcessById(RootPid); return !p.HasExited; } catch { return false; } }
        }

        void Loop()
        {
            while (!_stop)
            {
                uint msg; UIntPtr key; IntPtr ov;
                if (!GetQueuedCompletionStatus(_port, out msg, out key, out ov, 500)) continue;   // 超时：回去看看要不要停
                if (msg != 6) continue;                                                             // 6 = 作业里新启动了一个进程
                try { Handle((int)ov.ToInt64()); } catch (Exception ex) { Store.LogError("进程看守", ex); }
            }
        }

        void Handle(int pid)
        {
            if (pid == RootPid) return;
            var cb = OnProcess;
            if (cb == null) return;
            IntPtr h = OpenProcess(QueryLimited | VmRead | Terminate | SuspendResume, false, pid);
            if (h == IntPtr.Zero) h = OpenProcess(QueryLimited | Terminate | SuspendResume, false, pid);
            if (h == IntPtr.Zero) return;
            bool taken = false;
            try
            {
                string image = ImageOf(h);
                int parent = ParentOf(h);
                string cmd = null;
                // 进程刚出生的一瞬间启动参数可能还读不到，稍等几次
                for (int i = 0; i < 40; i++) { cmd = CommandLineOf(h); if (!string.IsNullOrEmpty(cmd)) break; Thread.Sleep(5); }
                if (cb(pid, parent, image ?? "", cmd ?? ""))
                {
                    // 不能当场结束：发起它的程序这时还没“启动完”这一步，当场结束会让对方以为启动失败而弹出报错框。
                    // 所以先让它原地暂停（什么都还没来得及做），等对方那一步走完，再把它结束掉。
                    taken = true;
                    NtSuspendProcess(h);
                    var handle = h;
                    var t = new Thread(() => { Thread.Sleep(HoldMs); TerminateProcess(handle, 0); CloseHandle(handle); });
                    t.IsBackground = true; t.Start();
                }
            }
            finally { if (!taken) CloseHandle(h); }
        }

        static string ImageOf(IntPtr h)
        {
            var sb = new StringBuilder(1024); int n = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString(0, n) : null;
        }

        static int ParentOf(IntPtr h)
        {
            int size = IntPtr.Size * 6, got;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                if (NtQueryInformationProcess(h, 0, buf, size, out got) != 0) return 0;
                return (int)Marshal.ReadIntPtr(buf, IntPtr.Size * 5).ToInt64();
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        static string CommandLineOf(IntPtr h)
        {
            int len;
            NtQueryInformationProcess(h, 60, IntPtr.Zero, 0, out len);
            if (len <= 0 || len > 1 << 20) return null;
            IntPtr buf = Marshal.AllocHGlobal(len);
            try
            {
                if (NtQueryInformationProcess(h, 60, buf, len, out len) != 0) return null;
                int chars = (ushort)Marshal.ReadInt16(buf) / 2;
                IntPtr text = Marshal.ReadIntPtr(buf, IntPtr.Size);
                return chars == 0 || text == IntPtr.Zero ? "" : Marshal.PtrToStringUni(text, chars);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        // 下面三个给“不在作业里”的进程用：按进程号查程序路径、查启动参数、暂停后结束
        public static string ImagePath(int pid)
        {
            IntPtr h = OpenProcess(QueryLimited, false, pid);
            if (h == IntPtr.Zero) return null;
            try { return ImageOf(h); } finally { CloseHandle(h); }
        }

        public static string CommandLine(int pid)
        {
            IntPtr h = OpenProcess(QueryLimited | VmRead, false, pid);
            if (h == IntPtr.Zero) h = OpenProcess(QueryLimited, false, pid);
            if (h == IntPtr.Zero) return null;
            try { return CommandLineOf(h); } finally { CloseHandle(h); }
        }

        public static bool Hold(int pid)
        {
            IntPtr h = OpenProcess(Terminate | SuspendResume, false, pid);
            if (h == IntPtr.Zero) return false;
            NtSuspendProcess(h);
            var t = new Thread(() => { Thread.Sleep(HoldMs); TerminateProcess(h, 0); CloseHandle(h); });
            t.IsBackground = true; t.Start();
            return true;
        }

        public void Dispose()
        {
            _stop = true;
            if (_thread != null && _thread != Thread.CurrentThread) { try { _thread.Join(1500); } catch { } }
            if (_port != IntPtr.Zero) { CloseHandle(_port); _port = IntPtr.Zero; }
            if (_job != IntPtr.Zero) { CloseHandle(_job); _job = IntPtr.Zero; }   // 只是不再盯着，被盯的程序照常运行
        }
    }
}
