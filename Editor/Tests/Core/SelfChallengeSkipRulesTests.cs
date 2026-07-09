using AgentCore.Editor.Core.SelfChallenge;
using NUnit.Framework;

namespace AgentCore.Tests.Editor.Core
{
    /// <summary>
    /// v1.4.9 骨架版本：Node A skip 规则（R1 + R3）单元测试。
    /// 依据设计文档 v0.9 §1.2.1 精简版立场：只做纯格式识别，R2/R4/R5 已取消。
    /// </summary>
    [TestFixture]
    public class SelfChallengeSkipRulesTests
    {
        // ─── R1: 短消息 ─────────────────────────────────────────

        [Test]
        public void R1_NullMessage_ShouldSkip()
        {
            Assert.IsTrue(SelfChallengeSkipRules.ShouldSkip(null, out var reason));
            Assert.AreEqual(SelfChallengeConfig.SkipReasonR1Short, reason);
        }

        [Test]
        public void R1_EmptyMessage_ShouldSkip()
        {
            Assert.IsTrue(SelfChallengeSkipRules.ShouldSkip(string.Empty, out var reason));
            Assert.AreEqual(SelfChallengeConfig.SkipReasonR1Short, reason);
        }

        [Test]
        public void R1_WhitespaceOnly_ShouldSkip()
        {
            Assert.IsTrue(SelfChallengeSkipRules.ShouldSkip("   \n\t  ", out var reason));
            Assert.AreEqual(SelfChallengeConfig.SkipReasonR1Short, reason);
        }

        [Test]
        public void R1_ShortConfirmation_ChineseShouldSkip()
        {
            // "好的" = 2 字符
            Assert.IsTrue(SelfChallengeSkipRules.ShouldSkip("好的", out var reason));
            Assert.AreEqual(SelfChallengeConfig.SkipReasonR1Short, reason);
        }

        [Test]
        public void R1_ShortConfirmation_EnglishShouldSkip()
        {
            // "OK, continue" = 11 字符（去空白后 = 11）
            Assert.IsTrue(SelfChallengeSkipRules.ShouldSkip("OK, continue", out var reason));
            Assert.AreEqual(SelfChallengeConfig.SkipReasonR1Short, reason);
        }

        [Test]
        public void R1_ExactlyFifteenChars_ShouldSkip()
        {
            // "帮我看看当前场景" = 8 字符（中文）
            Assert.IsTrue(SelfChallengeSkipRules.ShouldSkip("帮我看看当前场景", out var reason));
            Assert.AreEqual(SelfChallengeConfig.SkipReasonR1Short, reason);
        }

        [Test]
        public void R1_ExactlyFifteenChars_Boundary_ShouldSkip()
        {
            // 15 个字符（阈值边界包含）
            var msg = new string('x', 15);
            Assert.IsTrue(SelfChallengeSkipRules.ShouldSkip(msg, out var reason));
            Assert.AreEqual(SelfChallengeConfig.SkipReasonR1Short, reason);
        }

        [Test]
        public void R1_SixteenChars_ShouldNotSkip()
        {
            // 16 个字符（超出阈值一位）
            var msg = new string('x', 16);
            Assert.IsFalse(SelfChallengeSkipRules.ShouldSkip(msg, out var reason));
            Assert.IsNull(reason);
        }

        [Test]
        public void R1_LongMessage_ShouldNotSkip()
        {
            var msg = "帮我获取当前场景中选中 GameObject 的所有 material 引用";
            Assert.IsFalse(SelfChallengeSkipRules.ShouldSkip(msg, out var reason));
            Assert.IsNull(reason);
        }

        [Test]
        public void R1_WhitespaceInMiddle_CountedWithoutWhitespace()
        {
            // "a b c d e f g h i j k l m n o p" — 16 个字母 + 15 个空格；去空白后 16 字符 > 15
            var msg = "a b c d e f g h i j k l m n o p";
            Assert.IsFalse(SelfChallengeSkipRules.ShouldSkip(msg, out _));
        }

        [Test]
        public void R1_WhitespaceInMiddle_AtBoundary_ShouldSkip()
        {
            // "a b c d e f g h i j k l m n o" — 15 个字母 + 14 个空格；去空白后 15 字符 <= 15
            var msg = "a b c d e f g h i j k l m n o";
            Assert.IsTrue(SelfChallengeSkipRules.ShouldSkip(msg, out var reason));
            Assert.AreEqual(SelfChallengeConfig.SkipReasonR1Short, reason);
        }

        // ─── R3: 纯 URL ──────────────────────────────────────────

        [Test]
        public void R3_PureHttpsUrl_ShouldSkip()
        {
            Assert.IsTrue(SelfChallengeSkipRules.ShouldSkip("https://example.com/path?q=1", out var reason));
            Assert.AreEqual(SelfChallengeConfig.SkipReasonR3Url, reason);
        }

        [Test]
        public void R3_PureHttpUrl_ShouldSkip()
        {
            Assert.IsTrue(SelfChallengeSkipRules.ShouldSkip("http://localhost:8080/api", out var reason));
            Assert.AreEqual(SelfChallengeConfig.SkipReasonR3Url, reason);
        }

        [Test]
        public void R3_UrlWithLeadingTrailingWhitespace_ShouldSkip()
        {
            Assert.IsTrue(SelfChallengeSkipRules.ShouldSkip("  https://example.com  ", out var reason));
            Assert.AreEqual(SelfChallengeConfig.SkipReasonR3Url, reason);
        }

        [Test]
        public void R3_UrlWithPrecedingText_ShouldNotMatchR3()
        {
            // "看看这个 https://example.com" 不是纯 URL，R3 不命中；但 R1 也不命中（长度 > 15）
            var msg = "请看看这个链接内容 https://example.com/foo/bar/baz";
            Assert.IsFalse(SelfChallengeSkipRules.ShouldSkip(msg, out _));
        }

        [Test]
        public void R3_TwoUrls_ShouldNotMatchR3()
        {
            var msg = "https://a.com https://b.com";
            Assert.IsFalse(SelfChallengeSkipRules.IsPureUrl(msg));
        }

        [Test]
        public void R3_UrlWithoutScheme_ShouldNotSkip()
        {
            // "www.example.com" 无 scheme → R3 不命中
            var msg = "www.example.com/very-long-path-here";
            Assert.IsFalse(SelfChallengeSkipRules.IsPureUrl(msg));
        }

        // ─── R1 优先于 R3 的边界 ─────────────────────────────

        [Test]
        public void ShortUrl_R1TakesPriority()
        {
            // "http://a.b" = 10 字符 → R1 命中（长度 <= 15），R3 也命中；
            // ShouldSkip 按 R1 → R3 顺序检查，返回 R1 原因。
            Assert.IsTrue(SelfChallengeSkipRules.ShouldSkip("http://a.b", out var reason));
            Assert.AreEqual(SelfChallengeConfig.SkipReasonR1Short, reason);
        }

        // ─── 静态辅助方法直接测试 ───────────────────────────

        [Test]
        public void IsShortMessage_NullReturnsTrue()
        {
            Assert.IsTrue(SelfChallengeSkipRules.IsShortMessage(null));
        }

        [Test]
        public void IsPureUrl_NullReturnsFalse()
        {
            Assert.IsFalse(SelfChallengeSkipRules.IsPureUrl(null));
        }
    }
}
