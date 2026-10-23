using System;
using System.Text.RegularExpressions;
using AgentCore.Editor.Utils;
using NUnit.Framework;

namespace AgentCore.Tests.Editor.Utils
{
    /// <summary>
    /// SafeRegex 长度上限、无效模式与匹配超时的单元测试。
    /// </summary>
    [TestFixture]
    public class SafeRegexTests
    {
        /// <summary>典型的灾难性回溯：嵌套量词 + 末尾不匹配。</summary>
        internal const string CatastrophicPattern = "^(a+)+$";

        internal static string CatastrophicInput => new string('a', 40) + "!";

        [Test]
        public void Create_PatternOverLimit_ThrowsArgumentExceptionWithReason()
        {
            var pattern = new string('a', SafeRegex.MaxPatternLength + 1);

            var ex = Assert.Throws<ArgumentException>(() => SafeRegex.Create(pattern, ignoreCase: false));
            StringAssert.Contains(SafeRegex.MaxPatternLength.ToString(), ex.Message);
        }

        [Test]
        public void Create_PatternAtLimit_Succeeds()
        {
            var pattern = new string('a', SafeRegex.MaxPatternLength);

            Assert.DoesNotThrow(() => SafeRegex.Create(pattern, ignoreCase: false));
        }

        [Test]
        public void Create_NullPattern_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => SafeRegex.Create(null, ignoreCase: false));
        }

        [Test]
        public void Create_CaseSensitive_DoesNotSetIgnoreCase()
        {
            var rx = SafeRegex.Create("abc", ignoreCase: false);

            Assert.IsTrue((rx.Options & RegexOptions.IgnoreCase) == 0);
            Assert.IsFalse(rx.IsMatch("ABC"));
        }

        [Test]
        public void Create_InvalidPattern_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => SafeRegex.Create("(unclosed", ignoreCase: true));
        }

        [Test]
        public void Create_SetsTimeoutAndCultureInvariant()
        {
            var rx = SafeRegex.Create("abc", ignoreCase: true);

            Assert.AreEqual(SafeRegex.MatchTimeout, rx.MatchTimeout);
            Assert.IsTrue((rx.Options & RegexOptions.CultureInvariant) != 0);
            Assert.IsTrue((rx.Options & RegexOptions.IgnoreCase) != 0);
        }

        [Test]
        public void CatastrophicPattern_ThrowsTimeout_InsteadOfReturningNoMatch()
        {
            var rx = SafeRegex.Create(CatastrophicPattern, ignoreCase: false);

            Assert.Throws<RegexMatchTimeoutException>(() => rx.IsMatch(CatastrophicInput));
        }

        [Test]
        public void DescribeTimeout_IsMarkedAsError()
        {
            var message = SafeRegex.DescribeTimeout(CatastrophicPattern, "search_code");

            StringAssert.StartsWith("regex_timeout", message);
            StringAssert.Contains(CatastrophicPattern, message);
        }
    }
}
