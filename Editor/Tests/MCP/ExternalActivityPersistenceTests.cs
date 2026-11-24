using System;
using System.IO;
using System.Linq;
using System.Text;
using AgentCore.Editor.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace AgentCore.Tests.Editor.MCP
{
    /// <summary>
    /// <see cref="ExternalToolActivity"/> 持久化水合、90 天窗口统计、Clear/Reload 契约及与 live 记录去重的单元测试。
    /// 全部使用独立临时审计文件与可控时钟，绝不读写工程真实 <c>Library/AgentCore/Mcp/audit.jsonl</c>。
    /// </summary>
    [TestFixture]
    public class ExternalActivityPersistenceTests
    {
        private string _tempDir;
        private string _rotatedAuditPath;
        private string _auditPath;
        private DateTime _now;

        [SetUp]
        public void SetUp()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "AgentCore_ExtActivityTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _rotatedAuditPath = Path.Combine(_tempDir, "audit.1.jsonl");
            _auditPath = Path.Combine(_tempDir, "audit.jsonl");
            _now = new DateTime(2026, 11, 24, 15, 0, 0, DateTimeKind.Local);

            ExternalToolActivity.ConfigureForTesting(
                () => new[] { _rotatedAuditPath, _auditPath },
                () => _now);
        }

        [TearDown]
        public void TearDown()
        {
            ExternalToolActivity.ResetTestingOverrides();
            if (!string.IsNullOrEmpty(_tempDir) && Directory.Exists(_tempDir))
            {
                try { Directory.Delete(_tempDir, true); } catch { /* ignore temp cleanup failure */ }
            }
        }

        private static string BuildAuditLine(
            DateTime time,
            string client,
            string tool,
            string action,
            string status,
            double elapsedMs,
            string arguments = "{}",
            string error = null,
            bool formatAsUtc = false)
        {
            var timeStr = formatAsUtc
                ? time.ToUniversalTime().ToString("o")
                : time.ToString("o");

            var obj = new JObject
            {
                ["time"] = timeStr,
                ["client"] = client,
                ["tool"] = tool,
                ["action"] = action,
                ["arguments"] = arguments,
                ["status"] = status,
                ["elapsedMs"] = elapsedMs,
            };
            if (!string.IsNullOrEmpty(error))
                obj["error"] = error;
            return obj.ToString(Formatting.None);
        }

        private static void WriteLines(string path, params string[] lines)
        {
            File.WriteAllLines(path, lines, new UTF8Encoding(false));
        }

        [Test]
        public void HydratesFromRotatedAndActiveAuditLogs_InNewestFirstOrder_ToleratingMalformedLines()
        {
            WriteLines(_rotatedAuditPath,
                BuildAuditLine(_now.AddHours(-3), "claude-code", "manage_scene", "get_hierarchy", "ok", 120.5, "{\"action\":\"get_hierarchy\"}"),
                "{ truncated json line without closing brace",
                "",
                "{\"client\":\"codex\",\"status\":\"ok\"}", // missing time & tool
                BuildAuditLine(_now.AddHours(-2), "codex", "read_console", "get_count", "error", 45.0, "{\"action\":\"get_count\"}", error: "boom", formatAsUtc: true));

            WriteLines(_auditPath,
                "not json at all",
                BuildAuditLine(_now.AddMinutes(-10), "claude-code", "manage_asset", "search", "partial", 300.0, "{\"action\":\"search\"}"));

            var snapshot = ExternalToolActivity.Snapshot();
            Assert.AreEqual(3, snapshot.Count);

            // 最新的在前
            Assert.AreEqual("manage_asset", snapshot[0].Tool);
            Assert.AreEqual("search", snapshot[0].Action);
            Assert.AreEqual("partial", snapshot[0].Status);
            Assert.AreEqual("claude-code", snapshot[0].Source);
            Assert.IsFalse(snapshot[0].IsRunning);
            Assert.AreEqual(0.3, snapshot[0].ElapsedSeconds, 0.01);

            Assert.AreEqual("read_console", snapshot[1].Tool);
            Assert.AreEqual("codex", snapshot[1].Source);
            Assert.AreEqual("error", snapshot[1].Status);
            Assert.AreEqual("boom", snapshot[1].ResultPreview);

            Assert.AreEqual("manage_scene", snapshot[2].Tool);
            Assert.AreEqual("ok", snapshot[2].Status);
        }

        [Test]
        public void GetWindowStats_ComputesAccurateCountsSuccessRateDurationAndTopToolAcrossWindows()
        {
            WriteLines(_auditPath,
                // 超出 90 天窗口（-120d、-90d）应被排除
                BuildAuditLine(_now.AddDays(-120), "c", "old_tool", "run", "ok", 999),
                BuildAuditLine(_now.AddDays(-90), "c", "old_tool", "run", "ok", 999),
                // Days90 边界（-89d、-30d）
                BuildAuditLine(_now.AddDays(-89), "c", "manage_asset", "list", "ok", 100),
                BuildAuditLine(_now.AddDays(-30), "c", "manage_asset", "list", "error", 200, error: "err"),
                // Days30 边界（-29d、-7d）
                BuildAuditLine(_now.AddDays(-29), "c", "manage_scene", "get", "ok", 300),
                BuildAuditLine(_now.AddDays(-7), "c", "manage_scene", "get", "ok", 100),
                // Days7 边界（-6d）
                BuildAuditLine(_now.AddDays(-6), "c", "read_console", "get_count", "cancelled", 50),
                // Today（3 条：2 次 read_console 成功/partial，1 次 manage_scene 失败）
                BuildAuditLine(_now.AddMinutes(-30), "c", "read_console", "get_count", "ok", 100),
                BuildAuditLine(_now.AddMinutes(-20), "c", "read_console", "get_logs", "partial", 200),
                BuildAuditLine(_now.AddMinutes(-10), "c", "manage_scene", "save", "error", 300, error: "fail"));

            // Today: 3 calls (2 ok/partial, 1 error), duration = 600ms, avg = 200ms, top = read_console (2)
            var today = ExternalToolActivity.GetWindowStats(ActivityWindow.Today);
            Assert.AreEqual(ActivityWindow.Today, today.Window);
            Assert.AreEqual(3, today.TotalCalls);
            Assert.AreEqual(2, today.SucceededCalls);
            Assert.AreEqual(1, today.FailedCalls);
            Assert.AreEqual(2.0 / 3.0, today.SuccessRate, 1e-6);
            Assert.AreEqual(600L, today.TotalDurationMs);
            Assert.AreEqual(200.0, today.AverageDurationMs, 1e-6);
            Assert.AreEqual("read_console", today.TopToolName);
            Assert.AreEqual(2, today.TopToolCalls);

            // Days7: Today (3) + -6d (1) = 4 calls (2 succeeded, 2 failed), duration = 650ms, top = read_console (3)
            var days7 = ExternalToolActivity.GetWindowStats(ActivityWindow.Days7);
            Assert.AreEqual(4, days7.TotalCalls);
            Assert.AreEqual(2, days7.SucceededCalls);
            Assert.AreEqual(2, days7.FailedCalls);
            Assert.AreEqual(0.5, days7.SuccessRate, 1e-6);
            Assert.AreEqual(650L, days7.TotalDurationMs);
            Assert.AreEqual("read_console", days7.TopToolName);
            Assert.AreEqual(3, days7.TopToolCalls);

            // Days30: Days7 (4) + -7d (1) + -29d (1) = 6 calls (4 succeeded, 2 failed), top = manage_scene (3) vs read_console (3) -> tie breaks alphabetically to manage_scene
            var days30 = ExternalToolActivity.GetWindowStats(ActivityWindow.Days30);
            Assert.AreEqual(6, days30.TotalCalls);
            Assert.AreEqual(4, days30.SucceededCalls);
            Assert.AreEqual(2, days30.FailedCalls);
            Assert.AreEqual(1050L, days30.TotalDurationMs);
            Assert.AreEqual("manage_scene", days30.TopToolName);
            Assert.AreEqual(3, days30.TopToolCalls);

            // Days90: Days30 (6) + -30d (1) + -89d (1) = 8 calls (5 succeeded, 3 failed), -90d/-120d excluded
            var days90 = ExternalToolActivity.GetWindowStats(ActivityWindow.Days90);
            Assert.AreEqual(8, days90.TotalCalls);
            Assert.AreEqual(5, days90.SucceededCalls);
            Assert.AreEqual(3, days90.FailedCalls);
            Assert.AreEqual(1350L, days90.TotalDurationMs);
        }

        [Test]
        public void Clear_LeavesSnapshotEmptyWithoutDeletingAuditFile_AndReloadRestores()
        {
            WriteLines(_auditPath,
                BuildAuditLine(_now.AddMinutes(-5), "claude-code", "read_console", "get_count", "ok", 25),
                BuildAuditLine(_now.AddMinutes(-2), "claude-code", "manage_scene", "get_hierarchy", "ok", 50));

            Assert.AreEqual(2, ExternalToolActivity.Snapshot().Count);

            // Clear 清空内存列表与统计，不删除磁盘审计日志，并保持现有 McpActivityTests 契约
            ExternalToolActivity.Clear();
            Assert.AreEqual(0, ExternalToolActivity.Snapshot().Count);
            Assert.AreEqual(0, ExternalToolActivity.GetWindowStats(ActivityWindow.Days90).TotalCalls);
            Assert.IsTrue(File.Exists(_auditPath), "Clear() must NOT delete audit.jsonl on disk");

            // Clear 之后记录新的 live 调用不会触发重新水合
            var live = ExternalToolActivity.Begin("claude-code", "live_only_tool", "ping", "{}");
            ExternalToolActivity.End(live, "ok", "pong");
            Assert.AreEqual(1, ExternalToolActivity.Snapshot().Count);
            Assert.AreEqual("live_only_tool", ExternalToolActivity.Snapshot()[0].Tool);

            // 显式 ReloadFromAuditLog 恢复磁盘上的审计条目（同时保留未在日志中的 live 条目）
            ExternalToolActivity.ReloadFromAuditLog();
            Assert.AreEqual(3, ExternalToolActivity.Snapshot().Count);
            Assert.AreEqual(3, ExternalToolActivity.GetWindowStats(ActivityWindow.Today).TotalCalls);
        }

        [Test]
        public void Deduplication_PreventsDoubleCountingMatchingLiveAndAuditCalls_WhilePreservingRapidDuplicates()
        {
            var t1 = _now;
            var t2 = _now.AddMilliseconds(50);

            // 审计日志里有两次快速相同的 read_console 调用
            WriteLines(_auditPath,
                BuildAuditLine(t1, "claude-code", "read_console", "get_count", "ok", 10),
                BuildAuditLine(t2, "claude-code", "read_console", "get_count", "ok", 12));

            ExternalToolActivity.ReloadFromAuditLog();
            Assert.AreEqual(2, ExternalToolActivity.GetWindowStats(ActivityWindow.Today).TotalCalls);

            // 同一时间窗口内对应的两次 live Begin/End 调用不应被双计（应保持 2 次，而非 4 次或误折叠成 1 次）
            _now = t1.AddMilliseconds(-5);
            var r1 = ExternalToolActivity.Begin("claude-code", "read_console", "get_count", "{}");
            _now = t1.AddMilliseconds(10);
            ExternalToolActivity.End(r1, "ok", "0");

            _now = t2.AddMilliseconds(-5);
            var r2 = ExternalToolActivity.Begin("claude-code", "read_console", "get_count", "{}");
            _now = t2.AddMilliseconds(10);
            ExternalToolActivity.End(r2, "ok", "0");

            Assert.AreEqual(2, ExternalToolActivity.GetWindowStats(ActivityWindow.Today).TotalCalls);

            // 再来一次未写入审计日志的新 live 调用，应计为第 3 次
            _now = t2.AddSeconds(5);
            var r3 = ExternalToolActivity.Begin("claude-code", "manage_scene", "get_hierarchy", "{}");
            _now = t2.AddSeconds(5.1);
            ExternalToolActivity.End(r3, "ok", "scene");

            var stats = ExternalToolActivity.GetWindowStats(ActivityWindow.Today);
            Assert.AreEqual(3, stats.TotalCalls);
            Assert.AreEqual(3, stats.SucceededCalls);
            Assert.AreEqual("read_console", stats.TopToolName);
            Assert.AreEqual(2, stats.TopToolCalls);
        }

        [Test]
        public void Capacity_CapsSnapshotAt200_WhileWindowStatsCountsAllEntries()
        {
            const int totalEntries = 220;
            var lines = Enumerable.Range(0, totalEntries)
                .Select(i => BuildAuditLine(_now.AddSeconds(-totalEntries + i), "claude-code", "read_console", "get_count", "ok", 5))
                .ToArray();
            WriteLines(_auditPath, lines);

            Assert.AreEqual(ExternalToolActivity.Capacity, ExternalToolActivity.Snapshot().Count);
            Assert.AreEqual(totalEntries, ExternalToolActivity.GetWindowStats(ActivityWindow.Today).TotalCalls);
        }
    }
}
