using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AgentCore.Editor.Tools.Infrastructure;
using AgentCore.Editor.Utils;
using NUnit.Framework;

namespace AgentCore.Tests.Editor.Infrastructure
{
    /// <summary>
    /// ripgrep 后端的单元测试：--json 输出解析（喂入预制输出，不需要 rg）、参数构建、定位逻辑；
    /// 末尾少量集成测试需要本机安装 rg，缺失时 Assert.Ignore。
    /// </summary>
    [TestFixture]
    public class RgSearchTests
    {
        /// <summary>把单引号写法的 JSON 转成真正的 JSON，便于在 C# 字符串里书写 rg 输出。</summary>
        private static string J(string singleQuoted) => singleQuoted.Replace('\'', '"');

        private static void Feed(RgJsonParser parser, params string[] lines)
        {
            foreach (var line in lines)
                parser.ProcessLine(J(line));
            parser.Complete();
        }

        private static string Begin(string path) => "{'type':'begin','data':{'path':{'text':'" + path + "'}}}";

        private static string End(string path) => "{'type':'end','data':{'path':{'text':'" + path + "'},'binary_offset':null,'stats':{'searches':1,'searches_with_match':1,'bytes_searched':10,'matched_lines':1,'matches':1}}}";

        private static string Match(string path, int line, string text, int start, int end, string matchText)
            => "{'type':'match','data':{'path':{'text':'" + path + "'},'lines':{'text':'" + text + "\\n'},'line_number':" + line +
               ",'absolute_offset':0,'submatches':[{'match':{'text':'" + matchText + "'},'start':" + start + ",'end':" + end + "}]}}";

        private static string Context(string path, int line, string text)
            => "{'type':'context','data':{'path':{'text':'" + path + "'},'lines':{'text':'" + text + "\\n'},'line_number':" + line + ",'absolute_offset':0,'submatches':[]}}";

        private static string Summary(int searches, int matchedLines)
            => "{'data':{'elapsed_total':{'human':'0.01s','nanos':1,'secs':0},'stats':{'bytes_printed':1,'bytes_searched':49,'elapsed':{'human':'0s','nanos':1,'secs':0},'matched_lines':" + matchedLines +
               ",'matches':" + matchedLines + ",'searches':" + searches + ",'searches_with_match':1}},'type':'summary'}";

        #region RgJsonParser

        [Test]
        public void Parser_BasicMatch_PathNormalizedAndColumnOneBased()
        {
            var parser = new RgJsonParser(maxResults: 10, contextLines: 0);

            Feed(parser,
                Begin(".\\\\Assets\\\\a.cs"),
                Match(".\\\\Assets\\\\a.cs", 2, "foo Hello", 4, 9, "Hello"),
                End(".\\\\Assets\\\\a.cs"),
                Summary(3, 1));

            Assert.AreEqual(1, parser.Matches.Count);
            var m = parser.Matches[0];
            Assert.AreEqual("Assets/a.cs", m.Path);
            Assert.AreEqual(2, m.Line);
            Assert.AreEqual(5, m.Column);
            Assert.AreEqual("Hello", m.Match);
            Assert.AreEqual("foo Hello", m.LineText);
            Assert.AreEqual(">>>     2 | foo Hello", m.Context);
            Assert.AreEqual(1, parser.TotalMatches);
            Assert.AreEqual(1, parser.FilesMatched);
            Assert.AreEqual(3, parser.FilesSearched);
            Assert.IsTrue(parser.SawSummary);
            Assert.IsFalse(parser.OverLimit);
        }

        [Test]
        public void Parser_Context_SharedLinesUsedForAdjacentMatches()
        {
            var parser = new RgJsonParser(maxResults: 10, contextLines: 1);

            Feed(parser,
                Begin("ctx.txt"),
                Context("ctx.txt", 1, "l1"),
                Match("ctx.txt", 2, "l2 hello", 3, 8, "hello"),
                Context("ctx.txt", 3, "l3"),
                Match("ctx.txt", 4, "l4 hello", 3, 8, "hello"),
                Context("ctx.txt", 5, "l5"),
                End("ctx.txt"));

            Assert.AreEqual(2, parser.Matches.Count);
            Assert.AreEqual("        1 | l1\n>>>     2 | l2 hello\n        3 | l3", parser.Matches[0].Context);
            Assert.AreEqual("        3 | l3\n>>>     4 | l4 hello\n        5 | l5", parser.Matches[1].Context);
        }

        [Test]
        public void Parser_MultiByteText_ColumnCountsUtf16Chars()
        {
            var parser = new RgJsonParser(maxResults: 10, contextLines: 0);

            // "中文 hello"：中、文各 3 字节 + 空格 1 字节 → 字节偏移 7，对应第 4 个字符
            Feed(parser, Begin("uni.txt"), Match("uni.txt", 1, "中文 hello", 7, 12, "hello"), End("uni.txt"));

            Assert.AreEqual(4, parser.Matches[0].Column);
        }

        [Test]
        public void Parser_BytesField_DecodedWithoutThrowing()
        {
            var parser = new RgJsonParser(maxResults: 10, contextLines: 0);

            // rg 对非 UTF-8 行输出 {"bytes": base64}；"caf\xE9 hello\n"
            Feed(parser,
                Begin("latin.txt"),
                "{'type':'match','data':{'path':{'text':'latin.txt'},'lines':{'bytes':'Y2Fm6SBoZWxsbwo='},'line_number':1,'absolute_offset':0,'submatches':[{'match':{'text':'hello'},'start':5,'end':10}]}}",
                End("latin.txt"));

            var m = parser.Matches.Single();
            Assert.AreEqual(6, m.Column);
            Assert.AreEqual("hello", m.Match);
            StringAssert.EndsWith(" hello", m.LineText);
        }

        [Test]
        public void Parser_NonUtf8Path_BytesPathDecoded()
        {
            var parser = new RgJsonParser(maxResults: 10, contextLines: 0);
            var pathB64 = Convert.ToBase64String(new byte[] { (byte)'d', (byte)'i', (byte)'r', (byte)'/', 0xFF, (byte)'.', (byte)'t' });

            Feed(parser,
                "{'type':'begin','data':{'path':{'bytes':'" + pathB64 + "'}}}",
                "{'type':'match','data':{'path':{'bytes':'" + pathB64 + "'},'lines':{'text':'x\\n'},'line_number':1,'absolute_offset':0,'submatches':[{'match':{'text':'x'},'start':0,'end':1}]}}");

            StringAssert.StartsWith("dir/", parser.Matches.Single().Path);
        }

        [Test]
        public void Parser_CrLfLine_LineEndingTrimmed()
        {
            var parser = new RgJsonParser(maxResults: 10, contextLines: 0);

            Feed(parser, Begin("a.txt"),
                "{'type':'match','data':{'path':{'text':'a.txt'},'lines':{'text':'foo\\r\\n'},'line_number':1,'absolute_offset':0,'submatches':[{'match':{'text':'foo'},'start':0,'end':3}]}}");

            Assert.AreEqual("foo", parser.Matches.Single().LineText);
        }

        [Test]
        public void Parser_OverLimit_KeepsCountingButCollectsOnlyMax()
        {
            var parser = new RgJsonParser(maxResults: 2, contextLines: 0, keepCountingAfterLimit: true);
            var lines = new List<string> { Begin("a.txt") };
            for (int i = 1; i <= 5; i++)
                lines.Add(Match("a.txt", i, "hit", 0, 3, "hit"));
            lines.Add(End("a.txt"));
            lines.Add(Begin("b.txt"));
            lines.Add(Match("b.txt", 1, "hit", 0, 3, "hit"));
            lines.Add(End("b.txt"));
            lines.Add(Summary(9, 6));

            Feed(parser, lines.ToArray());

            Assert.AreEqual(2, parser.Matches.Count);
            Assert.AreEqual(6, parser.TotalMatches);
            Assert.AreEqual(2, parser.FilesMatched);
            Assert.AreEqual(9, parser.FilesSearched);
            Assert.IsTrue(parser.OverLimit);
            Assert.IsFalse(parser.IsDone);
            Assert.IsTrue(parser.SawSummary);
        }

        [Test]
        public void Parser_OverLimitWithoutCounting_DoneAfterLastContextRead()
        {
            var parser = new RgJsonParser(maxResults: 1, contextLines: 1, keepCountingAfterLimit: false);

            parser.ProcessLine(J(Begin("a.txt")));
            parser.ProcessLine(J(Match("a.txt", 1, "hit one", 0, 3, "hit")));
            Assert.IsFalse(parser.IsDone);
            parser.ProcessLine(J(Match("a.txt", 2, "hit two", 0, 3, "hit")));
            // 第 2 行仍在第 1 条匹配的下文范围内：先不结束，等越过范围
            Assert.IsFalse(parser.IsDone);
            parser.ProcessLine(J(Context("a.txt", 3, "tail")));
            Assert.IsTrue(parser.IsDone);
            parser.Complete();

            Assert.AreEqual(1, parser.Matches.Count);
            Assert.AreEqual(">>>     1 | hit one\n        2 | hit two", parser.Matches[0].Context);
            Assert.AreEqual(2, parser.TotalMatches);
        }

        [Test]
        public void Parser_CountCap_StopsAndFlags()
        {
            var parser = new RgJsonParser(maxResults: 1, contextLines: 0, keepCountingAfterLimit: true, maxCountedMatches: 3);
            parser.ProcessLine(J(Begin("a.txt")));
            for (int i = 1; i <= 5; i++)
                parser.ProcessLine(J(Match("a.txt", i, "hit", 0, 3, "hit")));
            parser.Complete();

            Assert.IsTrue(parser.CountCapReached);
            Assert.IsTrue(parser.IsDone);
            Assert.AreEqual(3, parser.TotalMatches);
            Assert.AreEqual(1, parser.Matches.Count);
        }

        [Test]
        public void Parser_MalformedLines_IgnoredAndCounted()
        {
            var parser = new RgJsonParser(maxResults: 10, contextLines: 0);

            parser.ProcessLine("not json");
            parser.ProcessLine("{\"foo\":1}");
            parser.ProcessLine("");
            parser.Complete();

            Assert.AreEqual(2, parser.MalformedLines);
            Assert.IsFalse(parser.SawAnyMessage);
            Assert.AreEqual(0, parser.Matches.Count);
        }

        [Test]
        public void Parser_LongLine_Clipped()
        {
            var parser = new RgJsonParser(maxResults: 10, contextLines: 0, maxLineChars: 10);
            var longText = new string('x', 50) + "hit";

            Feed(parser, Begin("a.txt"), Match("a.txt", 1, longText, 50, 53, "hit"));

            var m = parser.Matches.Single();
            Assert.AreEqual(51, m.Column);
            StringAssert.StartsWith(new string('x', 10) + "…[+43 chars]", m.LineText);
        }

        [Test]
        public void NormalizePath_StripsDotSlashAndBackslashes()
        {
            Assert.AreEqual("Assets/x.cs", RgJsonParser.NormalizePath(".\\Assets\\x.cs"));
            Assert.AreEqual("Assets/Sub/obj/y.cs", RgJsonParser.NormalizePath("Assets/Sub/obj\\y.cs"));
            Assert.AreEqual(string.Empty, RgJsonParser.NormalizePath(null));
        }

        #endregion

        #region BuildArguments

        [Test]
        public void BuildArguments_AlwaysJsonNoConfigAndPatternBeforeSeparator()
        {
            var args = RgSearch.BuildArguments(new RgSearchOptions { Pattern = "-rf --version" });

            CollectionAssert.Contains(args, "--json");
            CollectionAssert.Contains(args, "--no-config");
            var e = args.IndexOf("-e");
            var sep = args.IndexOf("--");
            Assert.GreaterOrEqual(e, 0);
            Assert.AreEqual("-rf --version", args[e + 1]);
            Assert.Greater(sep, e);
            Assert.AreEqual(".", args.Last());
            CollectionAssert.Contains(args, "--ignore-case");
            CollectionAssert.DoesNotContain(args, "--fixed-strings");
            CollectionAssert.DoesNotContain(args, "--hidden");
        }

        [Test]
        public void BuildArguments_MapsOptions()
        {
            var args = RgSearch.BuildArguments(new RgSearchOptions
            {
                Pattern = "x",
                FixedStrings = true,
                CaseSensitive = true,
                IncludeHidden = true,
                RespectIgnoreFiles = false,
                MaxDepth = 1,
                MaxFileSizeBytes = 1024,
                MaxColumns = 300,
                ContextLines = 2,
                IncludeGlobs = { "*.cs", " " },
                ExcludeGlobs = { "obj/", "!*.prefab" },
                Paths = { "Assets", "-weird dir" },
            });

            CollectionAssert.Contains(args, "--fixed-strings");
            CollectionAssert.Contains(args, "--case-sensitive");
            CollectionAssert.Contains(args, "--hidden");
            CollectionAssert.Contains(args, "--no-ignore");
            Assert.AreEqual("1", args[args.IndexOf("--max-depth") + 1]);
            Assert.AreEqual("1024", args[args.IndexOf("--max-filesize") + 1]);
            Assert.AreEqual("300", args[args.IndexOf("--max-columns") + 1]);
            Assert.AreEqual("2", args[args.IndexOf("--context") + 1]);

            var globs = args.Select((a, i) => (a, i)).Where(t => t.a == "--glob").Select(t => args[t.i + 1]).ToList();
            CollectionAssert.AreEqual(new[] { "*.cs", "!obj/", "!*.prefab" }, globs);

            var sep = args.IndexOf("--");
            CollectionAssert.AreEqual(new[] { "Assets", "-weird dir" }, args.Skip(sep + 1).ToList());
        }

        [Test]
        public void BuildArguments_JoinedCommandLine_RoundTripsQuotesAndBackslashes()
        {
            var args = RgSearch.BuildArguments(new RgSearchOptions { Pattern = "a \"b\" c\\", Paths = { "dir with space\\" } });

            var windows = ProcessArgs.Join(args, windows: true);
            StringAssert.Contains("\"a \\\"b\\\" c\\\\\"", windows);
            StringAssert.Contains("\"dir with space\\\\\"", windows);

            var posix = ProcessArgs.Join(args, windows: false);
            StringAssert.Contains("'a \"b\" c\\'", posix);
        }

        [Test]
        public void IsRegexError_DetectsRustRegexParseErrors()
        {
            const string stderr = "rg: regex parse error:\n    (?:(?<=a)b)\n       ^^^^\nerror: look-around, including look-ahead and look-behind, is not supported\n\nConsider enabling PCRE2 with the --pcre2 flag, which can handle backreferences\nand look-around.";

            Assert.IsTrue(RgSearch.IsRegexError(stderr));
            Assert.IsFalse(RgSearch.IsRegexError("rg: nope: The system cannot find the file specified. (os error 2)"));
            Assert.IsFalse(RgSearch.IsRegexError(null));

            var summary = RgSearch.SummarizeError(stderr, 2);
            StringAssert.Contains("look-around", summary);
            StringAssert.DoesNotContain("--pcre2", summary);
        }

        #endregion

        #region RgLocator

        [Test]
        public void ParseVersion_ReadsRipgrepFirstLine()
        {
            Assert.AreEqual("15.1.0", RgLocator.ParseVersion("ripgrep 15.1.0 (rev af60c2de9d)\n\nfeatures:+pcre2\n"));
            Assert.AreEqual("13.0.0", RgLocator.ParseVersion("ripgrep 13.0.0\r\n-SIMD -AVX"));
            Assert.IsNull(RgLocator.ParseVersion("grep (GNU grep) 3.11"));
            Assert.IsNull(RgLocator.ParseVersion(""));
        }

        [Test]
        public void EnumerateCandidates_OrderCustomBundledPath()
        {
            var candidates = RgLocator.EnumerateCandidates(
                customPath: "C:\\tools\\rg.exe",
                packageRoot: "C:\\pkg",
                pathEnv: "C:\\a;C:\\b;;C:\\a",
                windows: true).ToList();

            Assert.AreEqual("custom", candidates[0].Source);
            Assert.AreEqual("C:\\tools\\rg.exe", candidates[0].Path);

            var firstBundled = candidates.FindIndex(c => c.Source == "bundled");
            var firstPath = candidates.FindIndex(c => c.Source == "PATH");
            Assert.Greater(firstBundled, 0);
            Assert.Greater(firstPath, firstBundled);
            StringAssert.Contains(Path.Combine("Editor", "Plugins", "rg"), candidates[firstBundled].Path);

            // 重复的 PATH 目录只出现一次
            var pathEntries = candidates.Where(c => c.Source == "PATH").Select(c => c.Path).ToList();
            CollectionAssert.AllItemsAreUnique(pathEntries);
            Assert.AreEqual(2, pathEntries.Count);
            Assert.IsTrue(pathEntries.All(p => p.EndsWith("rg.exe")));
        }

        [Test]
        public void EnumerateCandidates_NoCustomPath_StartsWithBundledOrPath()
        {
            var candidates = RgLocator.EnumerateCandidates(null, null, "/usr/bin", windows: false).ToList();

            Assert.AreEqual("PATH", candidates[0].Source);
            Assert.AreEqual(Path.Combine("/usr/bin", "rg"), candidates[0].Path);
        }

        [Test]
        public void ResolveShim_ReadsScoopShimTarget()
        {
            var dir = Path.Combine(Path.GetTempPath(), "agentcore-rg-shim-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var shimExe = Path.Combine(dir, "rg.exe");
                var real = Path.Combine(dir, "real-rg.exe");
                File.WriteAllText(shimExe, "");
                File.WriteAllText(real, "");
                File.WriteAllText(Path.Combine(dir, "rg.shim"), "path = \"" + real + "\"\n");

                Assert.AreEqual(real, RgLocator.ResolveShim(shimExe));
                Assert.AreEqual(real, RgLocator.ResolveShim(real));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        #endregion

        #region 集成（需要 rg）

        private static string RequireRg()
        {
            try { RgLocator.EnsureProbedAsync().Wait(15000); }
            catch { /* 探测失败按不可用处理 */ }
            if (!RgLocator.TryGetExecutable(out var path, 0))
                Assert.Ignore("ripgrep (rg) is not installed; skipping integration test.");
            return path;
        }

        private static string CreateTempTree()
        {
            var root = Path.Combine(Path.GetTempPath(), "agentcore-rg-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "Assets", "Sub", "obj"));
            File.WriteAllText(Path.Combine(root, "Assets", "a.cs"), "hello world\nfoo Hello\nbar\n");
            File.WriteAllText(Path.Combine(root, "Assets", "b.txt"), "say hello\n");
            File.WriteAllText(Path.Combine(root, "Assets", "Sub", "obj", "gen.cs"), "hello generated\n");
            return root;
        }

        [Test]
        public void Run_RealRg_FindsMatchesWithGlobsAndExclusions()
        {
            var rg = RequireRg();
            var root = CreateTempTree();
            try
            {
                var result = RgSearch.Run(rg, new RgSearchOptions
                {
                    Pattern = "hello",
                    WorkingDirectory = root,
                    IncludeGlobs = { "*.cs" },
                    ExcludeGlobs = { "obj/" },
                    Paths = { "Assets" },
                    ContextLines = 1,
                }, CancellationToken.None);

                Assert.AreEqual(RgSearchStatus.Ok, result.Status, result.ErrorMessage);
                Assert.AreEqual(2, result.TotalMatches);
                Assert.IsTrue(result.Matches.All(m => m.Path == "Assets/a.cs"));
                Assert.AreEqual(new[] { 1, 2 }, result.Matches.Select(m => m.Line).ToArray());
                Assert.IsFalse(result.Truncated);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Test]
        public void Run_RealRg_LookAroundReportsRegexUnsupported()
        {
            var rg = RequireRg();
            var root = CreateTempTree();
            try
            {
                var result = RgSearch.Run(rg, new RgSearchOptions { Pattern = "(?<=foo )Hello", WorkingDirectory = root }, CancellationToken.None);

                Assert.AreEqual(RgSearchStatus.RegexUnsupported, result.Status);
                StringAssert.Contains("look-around", result.ErrorMessage);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Test]
        public void Run_RealRg_MaxResultsTruncatesButCounts()
        {
            var rg = RequireRg();
            var root = CreateTempTree();
            try
            {
                var result = RgSearch.Run(rg, new RgSearchOptions
                {
                    Pattern = "hello",
                    FixedStrings = true,
                    WorkingDirectory = root,
                    MaxResults = 1,
                }, CancellationToken.None);

                Assert.AreEqual(RgSearchStatus.Ok, result.Status, result.ErrorMessage);
                Assert.AreEqual(1, result.Matches.Count);
                Assert.AreEqual(4, result.TotalMatches);
                Assert.IsTrue(result.Truncated);
                Assert.AreEqual("max_results", result.TruncatedReason);
                Assert.IsFalse(result.TotalIsLowerBound);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        #endregion
    }
}
