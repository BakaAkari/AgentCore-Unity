using System;
using System.IO;
using System.Linq;
using AgentCore.Editor.LLM;
using NUnit.Framework;

namespace AgentCore.Tests.Editor.LLM
{
    /// <summary>
    /// 持久化 LLM 用量存储（<see cref="UsageHistoryStore"/>）与账本（<see cref="UsageLedger"/>）的单元测试：
    /// 覆盖跨重载恢复、1d/7d/30d/90d 滚动窗口、90 天裁剪、按模型拆解、long 防溢出、
    /// ResetSession 与 ClearHistory 隔离、损坏 JSON 容错以及 BindSession 语义。
    /// </summary>
    [TestFixture]
    public class UsageHistoryTests
    {
        private string _tempDir;
        private string _historyPath;
        private DateTime _now;

        [SetUp]
        public void SetUp()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "AgentCore_UsageHistoryTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _historyPath = Path.Combine(_tempDir, "UsageHistory.json");
            _now = new DateTime(2026, 11, 24, 14, 30, 0, DateTimeKind.Local);

            UsageLedger.ConfigureForTesting(() => _historyPath, () => _now);
        }

        [TearDown]
        public void TearDown()
        {
            UsageLedger.ResetTestingOverrides();
            if (!string.IsNullOrEmpty(_tempDir) && Directory.Exists(_tempDir))
            {
                try { Directory.Delete(_tempDir, true); } catch { /* ignore temp cleanup failure */ }
            }
        }

        private static LlmUsage MakeUsage(
            string model,
            int inputUncached,
            int cacheRead = 0,
            int cacheWrite = 0,
            int output = 0,
            int reasoning = 0,
            bool cacheKnown = true)
        {
            return new LlmUsage
            {
                Model = model,
                InputUncached = inputUncached,
                CacheRead = cacheRead,
                CacheWrite = cacheWrite,
                Output = output,
                Reasoning = reasoning,
                Calls = 1,
                CacheMetricsKnown = cacheKnown,
            };
        }

        [Test]
        public void Record_PersistsToDisk_AndSurvivesSimulatedDomainReload()
        {
            var changedCount = 0;
            var recordedCount = 0;
            void OnChanged() => changedCount++;
            void OnRecorded(LlmUsage _) => recordedCount++;

            UsageLedger.OnChanged += OnChanged;
            UsageLedger.OnUsageRecorded += OnRecorded;
            try
            {
                var first = MakeUsage("claude-sonnet-4-6", inputUncached: 1000, cacheRead: 4000, cacheWrite: 500, output: 300, reasoning: 100);
                var second = MakeUsage("claude-sonnet-4-6", inputUncached: 2000, cacheRead: 6000, cacheWrite: 0, output: 700, reasoning: 200);

                UsageLedger.Record(first, "test");
                UsageLedger.Record(second, "test");

                Assert.IsTrue(File.Exists(_historyPath), "UsageHistory.json should be persisted on Record");
                Assert.AreEqual(_historyPath, UsageLedger.HistoryFilePath);
                Assert.AreEqual(2, changedCount);
                Assert.AreEqual(2, recordedCount);

                var costBeforeReload = UsageLedger.SessionCostUsd;
                Assert.Greater(costBeforeReload, 0.0);
                Assert.IsFalse(UsageLedger.SessionCostPartial);

                // 模拟 Domain Reload / Editor 重启：从磁盘重新加载
                UsageLedger.ReloadFromDisk();

                var restoredSession = UsageLedger.Session;
                Assert.AreEqual(2, restoredSession.Calls);
                Assert.AreEqual(3000, restoredSession.InputUncached);
                Assert.AreEqual(10000, restoredSession.CacheRead);
                Assert.AreEqual(500, restoredSession.CacheWrite);
                Assert.AreEqual(13500, restoredSession.TotalInput);
                Assert.AreEqual(1000, restoredSession.Output);
                Assert.AreEqual(300, restoredSession.Reasoning);
                Assert.IsTrue(restoredSession.CacheMetricsKnown);
                Assert.AreEqual(costBeforeReload, UsageLedger.SessionCostUsd, 1e-9);
                Assert.IsFalse(UsageLedger.SessionCostPartial);

                var restoredLast = UsageLedger.Last;
                Assert.IsNotNull(restoredLast);
                Assert.AreEqual("claude-sonnet-4-6", restoredLast.Model);
                Assert.AreEqual(2000, restoredLast.InputUncached);
                Assert.AreEqual(6000, restoredLast.CacheRead);
                Assert.AreEqual(700, restoredLast.Output);

                var sessionSummary = UsageLedger.GetWindowSummary(UsageWindow.Session);
                Assert.AreEqual(2L, sessionSummary.CallCount);
                Assert.AreEqual(13500L, sessionSummary.InputTokens);
                Assert.AreEqual(1000L, sessionSummary.OutputTokens);
                Assert.AreEqual(14500L, sessionSummary.TotalTokens);
                Assert.AreEqual(1, sessionSummary.Models.Count);
                Assert.AreEqual("claude-sonnet-4-6", sessionSummary.Models[0].Model);
            }
            finally
            {
                UsageLedger.OnChanged -= OnChanged;
                UsageLedger.OnUsageRecorded -= OnRecorded;
            }
        }

        [Test]
        public void RollingWindows_1d_7d_30d_90d_And90DayPruning_FollowExactInclusiveBoundaries()
        {
            var baseToday = _now;
            // 从最旧到最新按真实时间顺序记录，覆盖各窗口边界与 90 天裁剪边界
            var offsets = new[] { -100, -90, -89, -30, -29, -7, -6, -3, 0 };
            foreach (var offset in offsets)
            {
                _now = baseToday.AddDays(offset);
                UsageLedger.Record(MakeUsage("claude-sonnet-4-6", inputUncached: 100, output: 50), "test");
            }

            _now = baseToday;
            UsageLedger.ReloadFromDisk();

            // Today: [0] => 1 day, 1 call, 150 tokens
            var today = UsageLedger.GetWindowSummary(UsageWindow.Today);
            Assert.AreEqual(UsageWindow.Today, today.Window);
            Assert.AreEqual(1, today.WindowDays);
            Assert.AreEqual(1, today.ActiveDays);
            Assert.AreEqual(1L, today.CallCount);
            Assert.AreEqual(150L, today.TotalTokens);

            // Days7: [-6 .. 0] => {0, -3, -6} = 3 days
            var days7 = UsageLedger.GetWindowSummary(UsageWindow.Days7);
            Assert.AreEqual(7, days7.WindowDays);
            Assert.AreEqual(3, days7.ActiveDays);
            Assert.AreEqual(3L, days7.CallCount);
            Assert.AreEqual(450L, days7.TotalTokens);

            // Days30: [-29 .. 0] => {0, -3, -6, -7, -29} = 5 days
            var days30 = UsageLedger.GetWindowSummary(UsageWindow.Days30);
            Assert.AreEqual(30, days30.WindowDays);
            Assert.AreEqual(5, days30.ActiveDays);
            Assert.AreEqual(5L, days30.CallCount);
            Assert.AreEqual(750L, days30.TotalTokens);

            // Days90: [-89 .. 0] => {0, -3, -6, -7, -29, -30, -89} = 7 days (-90 与 -100 已被裁剪)
            var days90 = UsageLedger.GetWindowSummary(UsageWindow.Days90);
            Assert.AreEqual(90, days90.WindowDays);
            Assert.AreEqual(7, days90.ActiveDays);
            Assert.AreEqual(7L, days90.CallCount);
            Assert.AreEqual(1050L, days90.TotalTokens);

            // 验证磁盘上已裁剪掉 -90 和 -100 的桶，并保留 -89
            var raw = UsageHistoryStore.Load();
            Assert.AreEqual(7, raw.Days.Count);
            Assert.AreEqual(UsageHistoryStore.FormatLocalDate(baseToday.AddDays(-89)), raw.Days.First().Date);
            Assert.AreEqual(UsageHistoryStore.FormatLocalDate(baseToday), raw.Days.Last().Date);
        }

        [Test]
        public void ModelBreakdown_AggregatesAcrossDays_AndSortsByTotalTokensDescThenCostDesc()
        {
            var baseToday = _now;

            _now = baseToday.AddDays(-2);
            // 高 token、低价模型（haiku）：total = 5000
            UsageLedger.Record(MakeUsage("claude-haiku-4-5", inputUncached: 4000, output: 1000));
            // 同为 total = 2000，但 opus 单价更高
            UsageLedger.Record(MakeUsage("claude-opus-4-6", inputUncached: 1500, output: 500));

            _now = baseToday;
            // 同为 total = 2000，sonnet 单价低于 opus
            UsageLedger.Record(MakeUsage("claude-sonnet-4-6", inputUncached: 1500, output: 500));
            // haiku 今日再追加 1000 tokens -> 合计 6000
            UsageLedger.Record(MakeUsage("claude-haiku-4-5", inputUncached: 800, output: 200));

            var summary = UsageLedger.GetWindowSummary(UsageWindow.Days7);
            Assert.AreEqual(3, summary.Models.Count);

            // 1. haiku (6000 tokens)
            Assert.AreEqual("claude-haiku-4-5", summary.Models[0].Model);
            Assert.AreEqual(2L, summary.Models[0].CallCount);
            Assert.AreEqual(6000L, summary.Models[0].TotalTokens);

            // 2. opus (2000 tokens, higher cost than sonnet)
            Assert.AreEqual("claude-opus-4-6", summary.Models[1].Model);
            Assert.AreEqual(2000L, summary.Models[1].TotalTokens);

            // 3. sonnet (2000 tokens, lower cost than opus)
            Assert.AreEqual("claude-sonnet-4-6", summary.Models[2].Model);
            Assert.AreEqual(2000L, summary.Models[2].TotalTokens);
            Assert.Greater(summary.Models[1].CostUsd, summary.Models[2].CostUsd);
        }

        [Test]
        public void LongTokenAccumulation_ExceedsIntMaxValue_WithoutOverflow()
        {
            // 单次调用 total = 1,500,000,000；连续 3 次 = 4,500,000,000 (> int.MaxValue = 2,147,483,647)
            for (var i = 0; i < 3; i++)
            {
                UsageLedger.Record(MakeUsage(
                    "claude-sonnet-4-6",
                    inputUncached: 600_000_000,
                    cacheRead: 500_000_000,
                    cacheWrite: 0,
                    output: 400_000_000));
            }

            UsageLedger.ReloadFromDisk();

            var today = UsageLedger.GetWindowSummary(UsageWindow.Today);
            Assert.AreEqual(3L, today.CallCount);
            Assert.AreEqual(3_300_000_000L, today.InputTokens);
            Assert.AreEqual(1_500_000_000L, today.CacheReadTokens);
            Assert.AreEqual(1_200_000_000L, today.OutputTokens);
            Assert.AreEqual(4_500_000_000L, today.TotalTokens);

            var session = UsageLedger.GetWindowSummary(UsageWindow.Session);
            Assert.AreEqual(4_500_000_000L, session.TotalTokens);
            Assert.Greater(UsageLedger.Session.Calls, 0);
            Assert.Greater(UsageLedger.Session.Output, 0);
        }

        [Test]
        public void ResetSession_ClearsOnlySessionCounters_PreservesHistoricalBuckets()
        {
            var baseToday = _now;

            _now = baseToday.AddDays(-2);
            UsageLedger.Record(MakeUsage("claude-sonnet-4-6", inputUncached: 1000, output: 200));

            _now = baseToday;
            UsageLedger.Record(MakeUsage("claude-sonnet-4-6", inputUncached: 2000, output: 400));

            UsageLedger.ResetSession();

            Assert.AreEqual(0, UsageLedger.Session.Calls);
            Assert.IsNull(UsageLedger.Last);
            Assert.AreEqual(0.0, UsageLedger.SessionCostUsd, 1e-9);
            Assert.AreEqual(0L, UsageLedger.GetWindowSummary(UsageWindow.Session).CallCount);

            // 历史桶必须完好保留，且跨 Reload 依然存在
            UsageLedger.ReloadFromDisk();
            Assert.AreEqual(0L, UsageLedger.GetWindowSummary(UsageWindow.Session).CallCount);
            Assert.AreEqual(1L, UsageLedger.GetWindowSummary(UsageWindow.Today).CallCount);
            Assert.AreEqual(2400L, UsageLedger.GetWindowSummary(UsageWindow.Today).TotalTokens);
            Assert.AreEqual(2L, UsageLedger.GetWindowSummary(UsageWindow.Days7).CallCount);
            Assert.AreEqual(3600L, UsageLedger.GetWindowSummary(UsageWindow.Days7).TotalTokens);
        }

        [Test]
        public void ClearHistory_ClearsSessionAndAllDailyBuckets_AndPersists()
        {
            var baseToday = _now;

            _now = baseToday.AddDays(-5);
            UsageLedger.Record(MakeUsage("claude-sonnet-4-6", inputUncached: 500, output: 100));
            _now = baseToday;
            UsageLedger.Record(MakeUsage("claude-sonnet-4-6", inputUncached: 800, output: 200));

            UsageLedger.ClearHistory();

            Assert.AreEqual(0L, UsageLedger.GetWindowSummary(UsageWindow.Session).CallCount);
            Assert.AreEqual(0L, UsageLedger.GetWindowSummary(UsageWindow.Today).CallCount);
            Assert.AreEqual(0L, UsageLedger.GetWindowSummary(UsageWindow.Days90).CallCount);

            UsageLedger.ReloadFromDisk();
            Assert.AreEqual(0L, UsageLedger.GetWindowSummary(UsageWindow.Session).CallCount);
            Assert.AreEqual(0L, UsageLedger.GetWindowSummary(UsageWindow.Days90).CallCount);
        }

        [Test]
        public void CorruptedJsonFile_RecoversGracefullyWithoutThrowing()
        {
            File.WriteAllText(_historyPath, "{ this is not valid json !!!");

            Assert.DoesNotThrow(() => UsageLedger.ReloadFromDisk());
            Assert.AreEqual(0, UsageLedger.Session.Calls);
            Assert.AreEqual(0L, UsageLedger.GetWindowSummary(UsageWindow.Days90).CallCount);
            Assert.IsTrue(File.Exists(_historyPath + ".corrupt"), "Corrupted file should be backed up before fresh start");

            // 恢复后可正常继续记录并持久化
            UsageLedger.Record(MakeUsage("claude-sonnet-4-6", inputUncached: 300, output: 100));
            UsageLedger.ReloadFromDisk();
            Assert.AreEqual(1L, UsageLedger.GetWindowSummary(UsageWindow.Today).CallCount);
            Assert.AreEqual(400L, UsageLedger.GetWindowSummary(UsageWindow.Today).TotalTokens);
        }

        [Test]
        public void BindSession_PreservesOnSameId_AndResetsOnlyWhenRequestedOnChange()
        {
            UsageLedger.BindSession("session-alpha");
            UsageLedger.Record(MakeUsage("claude-sonnet-4-6", inputUncached: 1000, output: 200));
            UsageLedger.Record(MakeUsage("claude-sonnet-4-6", inputUncached: 500, output: 100));

            // 模拟 Domain Reload / Editor 重启后再次绑定同一会话 ID：不得清零
            UsageLedger.ReloadFromDisk();
            UsageLedger.BindSession("session-alpha", resetIfChanged: true);
            Assert.AreEqual("session-alpha", UsageLedger.CurrentSessionId);
            Assert.AreEqual(2L, UsageLedger.GetWindowSummary(UsageWindow.Session).CallCount);
            Assert.AreEqual(1800L, UsageLedger.GetWindowSummary(UsageWindow.Session).TotalTokens);

            // 切换到新会话但 resetIfChanged=false（默认）：更新 ID 且保留计数
            UsageLedger.BindSession("session-beta");
            Assert.AreEqual("session-beta", UsageLedger.CurrentSessionId);
            Assert.AreEqual(2L, UsageLedger.GetWindowSummary(UsageWindow.Session).CallCount);

            // 切换到新会话且 resetIfChanged=true：重置会话计数，但保留每日历史
            UsageLedger.BindSession("session-gamma", resetIfChanged: true);
            Assert.AreEqual("session-gamma", UsageLedger.CurrentSessionId);
            Assert.AreEqual(0L, UsageLedger.GetWindowSummary(UsageWindow.Session).CallCount);
            Assert.AreEqual(2L, UsageLedger.GetWindowSummary(UsageWindow.Today).CallCount);
        }

        [Test]
        public void FormatWindowSummary_FormatsEmptyAndNonEmptyWindows()
        {
            var emptyText = UsageLedger.FormatWindowSummary(UsageWindow.Today);
            Assert.IsNotEmpty(emptyText);

            UsageLedger.Record(MakeUsage("claude-sonnet-4-6", inputUncached: 1000, cacheRead: 3000, output: 500));
            var nonEmptyText = UsageLedger.FormatWindowSummary(UsageWindow.Today);
            Assert.AreNotEqual(emptyText, nonEmptyText);
            StringAssert.Contains("4.0K", nonEmptyText);
            StringAssert.Contains("75.0%", nonEmptyText);
        }
    }
}
