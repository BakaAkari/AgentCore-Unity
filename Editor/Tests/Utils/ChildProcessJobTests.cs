using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;
using AgentCore.Editor.Utils;

namespace AgentCore.Tests.Editor.Utils
{
    /// <summary>
    /// ChildProcessJob 子进程树收容的单元测试。会真实启动 cmd/ping 或 sh/sleep 子进程。
    /// </summary>
    [TestFixture]
    public class ChildProcessJobTests
    {
        private readonly List<Process> _started = new List<Process>();

        [TearDown]
        public void TearDown()
        {
            foreach (var p in _started)
            {
                try { ChildProcessJob.KillTree(p); } catch { /* 忽略 */ }
                p.Dispose();
            }
            _started.Clear();
        }

        [Test]
        public void KillTree_NullOrExitedProcess_DoesNotThrow()
        {
            Assert.DoesNotThrow(() => ChildProcessJob.KillTree(null));

            var p = StartTree(quick: true);
            Assert.IsTrue(p.WaitForExit(10000), "quick process did not exit");
            Assert.DoesNotThrow(() => ChildProcessJob.KillTree(p));
        }

        [Test]
        public void TryAssign_Null_ReturnsFalse()
        {
            Assert.IsFalse(ChildProcessJob.TryAssign(null));
        }

        [Test]
        public void TryAssign_OnWindows_Succeeds()
        {
            if (!ChildProcessJob.IsWindows)
                Assert.Ignore("Job Object 仅 Windows 可用");

            var p = StartTree(quick: false);
            Assert.IsTrue(ChildProcessJob.TryAssign(p));
        }

        [Test]
        public void TryAssign_OnNonWindows_ReturnsFalseWithoutThrowing()
        {
            if (ChildProcessJob.IsWindows)
                Assert.Ignore("仅验证非 Windows 平台的降级行为");

            var p = StartTree(quick: false);
            Assert.IsFalse(ChildProcessJob.TryAssign(p));
        }

        [Test]
        public void KillTree_ProcessWithChild_KillsWholeTree()
        {
            var root = StartTree(quick: false);
            ChildProcessJob.TryAssign(root);

            var descendants = WaitForDescendants(root.Id, TimeSpan.FromSeconds(10));
            Assert.IsNotEmpty(descendants, "shell did not spawn a child within 10s");

            ChildProcessJob.KillTree(root);

            Assert.IsTrue(root.WaitForExit(5000), "root process still running");
            foreach (var pid in descendants)
                Assert.IsTrue(WaitForPidExit(pid, TimeSpan.FromSeconds(5)), $"descendant {pid} still running");
        }

        [Test]
        public void GetDescendantPids_UnknownPid_ReturnsEmpty()
        {
            // PID 0 在 Windows 是 Idle、在 Unix 是 swapper/调度器；用一个不太可能存在的大 PID
            Assert.IsEmpty(ChildProcessJob.GetDescendantPids(int.MaxValue - 7));
        }

        // ── 辅助 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 启动一个会再派生子进程的 shell：Windows 用 cmd + ping，其他平台用 sh + sleep
        /// （"; true" 防止 sh 直接 exec 成 sleep）。
        /// </summary>
        private Process StartTree(bool quick)
        {
            var info = ChildProcessJob.IsWindows
                ? new ProcessStartInfo("cmd.exe", quick ? "/c ping -n 1 127.0.0.1" : "/c ping -n 30 127.0.0.1")
                : new ProcessStartInfo("/bin/sh", quick ? "-c \"sleep 0; true\"" : "-c \"sleep 30; true\"");
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;

            var p = Process.Start(info);
            Assert.IsNotNull(p);
            p.StandardOutput.ReadToEndAsync();
            _started.Add(p);
            return p;
        }

        private static List<int> WaitForDescendants(int pid, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                var list = ChildProcessJob.GetDescendantPids(pid);
                if (list.Count > 0 || DateTime.UtcNow > deadline)
                    return list;
                Thread.Sleep(100);
            }
        }

        private static bool WaitForPidExit(int pid, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (!IsAlive(pid))
                    return true;
                Thread.Sleep(100);
            }
            return !IsAlive(pid);
        }

        private static bool IsAlive(int pid)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                return !p.HasExited;
            }
            catch
            {
                return false;
            }
        }
    }
}
