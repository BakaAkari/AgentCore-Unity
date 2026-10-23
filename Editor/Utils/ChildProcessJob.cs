using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace AgentCore.Editor.Utils
{
    /// <summary>
    /// 子进程树收容：编辑器退出 / 崩溃时连带结束子进程，超时 / 取消时结束整棵进程树。
    /// <para>
    /// Windows 上维护一个进程级 Job Object（<c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>），
    /// 子进程启动后立即加入；子进程此后派生的孙进程（git credential helper、ssh、git-remote-https、
    /// svn 的 ssh 隧道等）自动继承同一个 Job。编辑器进程结束（正常退出或崩溃）时内核关闭句柄，
    /// Job 中所有进程随之被结束，不会残留。
    /// </para>
    /// <para>
    /// 域重载：静态字段在重载后被重置，重载后的首次 <see cref="TryAssign"/> 会新建一个 Job。
    /// 旧 Job 句柄刻意不关闭（也不用 SafeHandle 包装，避免域卸载时被终结器关闭）：关闭会立即杀掉
    /// 重载前仍在运行的子进程，例如正在 push 的 git 会被腰斩并留下 index.lock。这些进程让它们自然结束，
    /// 旧句柄直到编辑器退出才由内核回收，届时 KILL_ON_JOB_CLOSE 照常生效。代价是每次重载泄漏一个句柄。
    /// </para>
    /// <para>
    /// Job 设置了 <c>JOB_OBJECT_LIMIT_BREAKAWAY_OK</c>：显式以 CREATE_BREAKAWAY_FROM_JOB 启动的程序
    /// （例如浏览器、长期驻留的守护进程）仍可脱离 Job，不受编辑器退出影响。
    /// </para>
    /// <para>
    /// 纯清理设施：任何失败都只降级为“不收容”，从不抛异常、从不阻止子进程启动。
    /// 以 UseShellExecute 打开的外部 GUI 工具 / 文档（TortoiseSVN 对话框、Git GUI、文本编辑器）
    /// 不应加入 Job，否则关闭编辑器会连带关掉用户的窗口。
    /// </para>
    /// </summary>
    public static class ChildProcessJob
    {
        private const int KillTreeHelperTimeoutMs = 5000;

        private static readonly object JobLock = new object();
        private static IntPtr _jobHandle = IntPtr.Zero;
        private static bool _jobCreationFailed;
        private static bool _assignFailureLogged;

        private static readonly MethodInfo KillEntireTreeMethod = FindKillEntireTreeMethod();

        /// <summary>当前平台是否为 Windows。</summary>
        public static bool IsWindows => Environment.OSVersion.Platform == PlatformID.Win32NT;

        /// <summary>
        /// 把刚启动的子进程加入进程级 Job。非 Windows、进程已退出或任何 Win32 调用失败时返回 false，从不抛异常。
        /// 失败只记录一次 Info 日志。
        /// </summary>
        public static bool TryAssign(Process process)
        {
            if (process == null || !IsWindows)
                return false;

            try
            {
                var job = GetOrCreateJob();
                if (job == IntPtr.Zero)
                    return false;

                if (AssignProcessToJobObject(job, process.Handle))
                    return true;

                LogAssignFailureOnce($"AssignProcessToJobObject failed (Win32 error {Marshal.GetLastWin32Error()}).");
                return false;
            }
            catch (Exception ex)
            {
                LogAssignFailureOnce(ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 结束进程及其所有后代进程，从不抛异常。
        /// <para>
        /// 运行时提供 <c>Process.Kill(bool entireProcessTree)</c> 时直接使用（Unity 2022.3 的 Mono 没有）；
        /// 否则先记下后代 PID，Windows 上用 <c>taskkill /T /F</c> 结束整棵树，macOS / Linux 上向后代发 SIGTERM，
        /// 然后结束根进程，最后补杀仍存活的后代（父进程先退出而成为孤儿的孙进程 taskkill /T 找不到）。
        /// </para>
        /// </summary>
        public static void KillTree(Process process)
        {
            if (process == null)
                return;

            try
            {
                if (process.HasExited)
                    return;
            }
            catch
            {
                return;
            }

            if (KillEntireTreeMethod != null)
            {
                try
                {
                    KillEntireTreeMethod.Invoke(process, new object[] { true });
                    return;
                }
                catch
                {
                    // 回退到手动方式
                }
            }

            int rootPid;
            try { rootPid = process.Id; }
            catch { return; }

            var descendants = GetDescendantPids(rootPid);

            if (IsWindows)
            {
                RunHelper("taskkill", $"/T /F /PID {rootPid}");
            }
            else if (descendants.Count > 0)
            {
                RunHelper("kill", "-TERM " + string.Join(" ", descendants));
            }

            try
            {
                if (!process.HasExited)
                    process.Kill();
            }
            catch
            {
                // 已退出
            }

            foreach (var pid in descendants)
                KillPidIfAlive(pid);
        }

        /// <summary>
        /// 枚举 <paramref name="rootPid"/> 的所有后代进程 PID（广度优先，不含根）。失败时返回已收集到的部分，从不抛异常。
        /// Windows 用 ToolHelp 快照并以启动时间过滤 PID 复用；macOS / Linux 用 <c>pgrep -P</c>。
        /// </summary>
        internal static List<int> GetDescendantPids(int rootPid)
        {
            var result = new List<int>();
            try
            {
                if (IsWindows)
                    CollectDescendantsWindows(rootPid, result);
                else
                    CollectDescendantsUnix(rootPid, result);
            }
            catch
            {
                // 尽力而为
            }
            return result;
        }

        // ── Windows ──────────────────────────────────────────────────────────

        private static IntPtr GetOrCreateJob()
        {
            lock (JobLock)
            {
                if (_jobHandle != IntPtr.Zero || _jobCreationFailed)
                    return _jobHandle;

                var job = CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero)
                {
                    _jobCreationFailed = true;
                    LogAssignFailureOnce($"CreateJobObject failed (Win32 error {Marshal.GetLastWin32Error()}).");
                    return IntPtr.Zero;
                }

                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_BREAKAWAY_OK;

                var length = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
                var buffer = Marshal.AllocHGlobal(length);
                try
                {
                    Marshal.StructureToPtr(info, buffer, false);
                    if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)length))
                    {
                        var error = Marshal.GetLastWin32Error();
                        CloseHandle(job);
                        _jobCreationFailed = true;
                        LogAssignFailureOnce($"SetInformationJobObject failed (Win32 error {error}).");
                        return IntPtr.Zero;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }

                _jobHandle = job;
                return _jobHandle;
            }
        }

        private static void CollectDescendantsWindows(int rootPid, List<int> result)
        {
            var parentOf = new Dictionary<int, int>();
            var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot == IntPtr.Zero || snapshot == InvalidHandleValue)
                return;

            try
            {
                var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32W)) };
                if (!Process32FirstW(snapshot, ref entry))
                    return;
                do
                {
                    parentOf[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
                } while (Process32NextW(snapshot, ref entry));
            }
            finally
            {
                CloseHandle(snapshot);
            }

            var childrenOf = new Dictionary<int, List<int>>();
            foreach (var pair in parentOf)
            {
                if (pair.Key == pair.Value)
                    continue;
                if (!childrenOf.TryGetValue(pair.Value, out var list))
                    childrenOf[pair.Value] = list = new List<int>();
                list.Add(pair.Key);
            }

            var startTimes = new Dictionary<int, DateTime?>();
            var visited = new HashSet<int> { rootPid };
            var queue = new Queue<int>();
            queue.Enqueue(rootPid);
            while (queue.Count > 0)
            {
                var parent = queue.Dequeue();
                if (!childrenOf.TryGetValue(parent, out var children))
                    continue;

                var parentStart = GetStartTime(parent, startTimes);
                foreach (var child in children)
                {
                    if (!visited.Add(child))
                        continue;

                    // Windows 的父 PID 只是记录值：父进程退出后 PID 可能被复用，早于“父进程”启动的不是真正的子进程
                    var childStart = GetStartTime(child, startTimes);
                    if (parentStart.HasValue && childStart.HasValue && childStart.Value < parentStart.Value)
                        continue;

                    result.Add(child);
                    queue.Enqueue(child);
                }
            }
        }

        private static DateTime? GetStartTime(int pid, Dictionary<int, DateTime?> cache)
        {
            if (cache.TryGetValue(pid, out var cached))
                return cached;

            DateTime? value = null;
            try
            {
                using var p = Process.GetProcessById(pid);
                value = p.StartTime;
            }
            catch
            {
                // 无权限或已退出：不做过滤
            }
            cache[pid] = value;
            return value;
        }

        // ── macOS / Linux ────────────────────────────────────────────────────

        private static void CollectDescendantsUnix(int rootPid, List<int> result)
        {
            var visited = new HashSet<int> { rootPid };
            var queue = new Queue<int>();
            queue.Enqueue(rootPid);
            while (queue.Count > 0)
            {
                var parent = queue.Dequeue();
                var output = RunHelperCapture("pgrep", $"-P {parent}");
                if (string.IsNullOrEmpty(output))
                    continue;

                foreach (var line in output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (int.TryParse(line.Trim(), out var child) && visited.Add(child))
                    {
                        result.Add(child);
                        queue.Enqueue(child);
                    }
                }
            }
        }

        // ── 公共辅助 ─────────────────────────────────────────────────────────

        private static void KillPidIfAlive(int pid)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (!p.HasExited)
                    p.Kill();
            }
            catch
            {
                // 已退出或无权限
            }
        }

        private static void RunHelper(string fileName, string arguments)
        {
            try
            {
                using var helper = Process.Start(new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                helper?.WaitForExit(KillTreeHelperTimeoutMs);
            }
            catch
            {
                // 工具不存在或启动失败：由调用方的后续步骤兜底
            }
        }

        private static string RunHelperCapture(string fileName, string arguments)
        {
            try
            {
                using var helper = Process.Start(new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                });
                if (helper == null)
                    return null;
                var readTask = helper.StandardOutput.ReadToEndAsync();
                if (!readTask.Wait(KillTreeHelperTimeoutMs))
                {
                    try { helper.Kill(); } catch { /* 已退出 */ }
                    return null;
                }
                helper.WaitForExit(KillTreeHelperTimeoutMs);
                return readTask.Result;
            }
            catch
            {
                return null;
            }
        }

        private static MethodInfo FindKillEntireTreeMethod()
        {
            try
            {
                return typeof(Process).GetMethod("Kill", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(bool) }, null);
            }
            catch
            {
                return null;
            }
        }

        private static void LogAssignFailureOnce(string detail)
        {
            if (_assignFailureLogged)
                return;
            _assignFailureLogged = true;
            try
            {
                AgentCoreLog.Info($"[ChildProcessJob] Child processes will not be contained by a Job Object: {detail}");
            }
            catch
            {
                // 日志不可用（例如非主线程早期）时忽略
            }
        }

        // ── P/Invoke ─────────────────────────────────────────────────────────

        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JOB_OBJECT_LIMIT_BREAKAWAY_OK = 0x00000800;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
        private const uint TH32CS_SNAPPROCESS = 0x00000002;
        private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32W
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public UIntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);
    }
}
