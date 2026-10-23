using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

namespace AgentCore.Editor.Tools.Infrastructure
{
    /// <summary>
    /// 一条 ripgrep 匹配（按行：同一行多处命中只算一条，与 .NET 实现的 "matching lines" 口径一致）。
    /// </summary>
    public sealed class RgMatch
    {
        /// <summary>相对 rg 工作目录的路径，统一为 '/' 分隔，去掉开头的 "./"。</summary>
        public string Path;

        /// <summary>1-based 行号。</summary>
        public int Line;

        /// <summary>1-based 列号（UTF-16 字符位置，与 .NET Regex.Match.Index + 1 同口径）。</summary>
        public int Column;

        /// <summary>该行第一处命中的文本。</summary>
        public string Match;

        /// <summary>命中行文本（去掉行尾换行，超长时截断）。</summary>
        public string LineText;

        /// <summary>
        /// 上下文块，格式与 .NET 实现相同：命中行以 "&gt;&gt;&gt;" 开头，其余行以三个空格开头，
        /// 后接右对齐 5 位行号与 " | "。未请求上下文时只包含命中行。
        /// </summary>
        public string Context;
    }

    /// <summary>
    /// 流式解析 <c>rg --json</c> 输出（每行一条 begin / match / context / end / summary 消息）。
    /// <para>
    /// - 文本字段可能是 <c>{"text": "..."}</c>，也可能是非 UTF-8 内容的 <c>{"bytes": "&lt;base64&gt;"}</c>，两种都处理；
    ///   路径同理（非 UTF-8 文件名按 UTF-8 宽松解码）。<br/>
    /// - submatch 的 start / end 是字节偏移，换算成 UTF-16 列号。<br/>
    /// - 收满 <see cref="MaxResults"/> 条后，仍等当前文件把最后一条匹配的下文读完再停止收集；之后只做廉价计数
    ///   （按行前缀识别 match 消息，不解析 JSON），以便 totalMatches 保持准确。<br/>
    /// - 不调用任何 Unity API，可在任意线程使用。
    /// </para>
    /// </summary>
    public sealed class RgJsonParser
    {
        private const string MatchPrefix = "{\"type\":\"match\"";
        private const string BeginPrefix = "{\"type\":\"begin\"";

        /// <summary>收集的匹配条数上限。</summary>
        public int MaxResults { get; }

        /// <summary>每条匹配附带的上下文行数（与传给 rg 的 -C 相同）。</summary>
        public int ContextLines { get; }

        /// <summary>超过上限后是否继续计数（false 时 <see cref="IsDone"/> 会尽早变为 true，调用方可结束 rg）。</summary>
        public bool KeepCountingAfterLimit { get; }

        /// <summary>计数上限：达到后 <see cref="IsDone"/> 为 true，总数标记为下界。</summary>
        public int MaxCountedMatches { get; }

        /// <summary>单行文本最大长度，超出部分截断并附注（0 = 不截断）。</summary>
        public int MaxLineChars { get; }

        /// <summary>已收集的匹配（按 rg 输出顺序）。</summary>
        public List<RgMatch> Matches { get; } = new List<RgMatch>();

        /// <summary>匹配行总数（含超过上限只计数的部分）。</summary>
        public int TotalMatches { get; private set; }

        /// <summary>有匹配的文件数（begin 消息数）。</summary>
        public int FilesMatched { get; private set; }

        /// <summary>summary 中的 searches（已搜索文件数）；未收到 summary 时为 -1。</summary>
        public int FilesSearched { get; private set; } = -1;

        /// <summary>summary 中的 bytes_searched；未收到 summary 时为 -1。</summary>
        public long BytesSearched { get; private set; } = -1;

        /// <summary>是否收到了 summary（rg 正常跑完）。</summary>
        public bool SawSummary { get; private set; }

        /// <summary>是否解析到任何一条合法的 rg JSON 消息。</summary>
        public bool SawAnyMessage { get; private set; }

        /// <summary>无法解析的行数（非 JSON / 结构不符），不影响其他结果。</summary>
        public int MalformedLines { get; private set; }

        /// <summary>因达到 <see cref="MaxCountedMatches"/> 停止计数。</summary>
        public bool CountCapReached { get; private set; }

        /// <summary>
        /// 解析器已拿到调用方需要的全部内容，可以提前结束 rg：
        /// 不继续计数且已收满（含最后一条的下文），或达到计数上限。
        /// </summary>
        public bool IsDone => CountCapReached || (_countOnly && !KeepCountingAfterLimit);

        /// <summary>收集已满（TotalMatches 超过 MaxResults）。</summary>
        public bool OverLimit => TotalMatches > MaxResults;

        private readonly Func<string, string> _pathMapper;

        // 当前文件的待定匹配与行缓存（用于拼上下文）
        private string _currentPath;
        private readonly List<RgMatch> _pending = new List<RgMatch>();
        private readonly Dictionary<long, string> _lineCache = new Dictionary<long, string>();
        private bool _countOnly;
        private long _lastCollectedLine = -1;

        public RgJsonParser(int maxResults, int contextLines, bool keepCountingAfterLimit = true,
            int maxCountedMatches = 1000000, int maxLineChars = 2000, Func<string, string> pathMapper = null)
        {
            MaxResults = Math.Max(1, maxResults);
            ContextLines = Math.Max(0, contextLines);
            KeepCountingAfterLimit = keepCountingAfterLimit;
            // 计数上限至少比收集上限多 1：保证收满的最后一条一定被收集
            MaxCountedMatches = Math.Max(MaxResults + 1, maxCountedMatches);
            MaxLineChars = Math.Max(0, maxLineChars);
            _pathMapper = pathMapper ?? NormalizePath;
        }

        /// <summary>
        /// 处理 rg stdout 的一行。空行 / 非 JSON 行计入 <see cref="MalformedLines"/> 后忽略。
        /// </summary>
        public void ProcessLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line) || CountCapReached)
                return;

            if (_countOnly)
            {
                // 廉价计数：rg 的 begin / match 消息总以 {"type":"..." 开头；summary 以 {"data": 开头，需要完整解析
                if (line.StartsWith(MatchPrefix, StringComparison.Ordinal))
                {
                    SawAnyMessage = true;
                    CountMatch();
                    return;
                }
                if (line.StartsWith(BeginPrefix, StringComparison.Ordinal))
                {
                    SawAnyMessage = true;
                    FilesMatched++;
                    return;
                }
                if (line.IndexOf("\"summary\"", StringComparison.Ordinal) < 0)
                    return;
            }

            JObject msg;
            try
            {
                msg = JObject.Parse(line);
            }
            catch (Exception)
            {
                MalformedLines++;
                return;
            }

            var type = (string)msg["type"];
            var data = msg["data"] as JObject;
            if (type == null || data == null)
            {
                MalformedLines++;
                return;
            }

            SawAnyMessage = true;
            switch (type)
            {
                case "begin":
                    FlushFile();
                    FilesMatched++;
                    _lastCollectedLine = -1;
                    _currentPath = _pathMapper(DecodeField(data["path"]) ?? string.Empty);
                    break;

                case "match":
                    HandleMatch(data);
                    break;

                case "context":
                    HandleContext(data);
                    break;

                case "end":
                    FlushFile();
                    if (OverLimit)
                        _countOnly = true;
                    break;

                case "summary":
                    FlushFile();
                    SawSummary = true;
                    if (data["stats"] is JObject stats)
                    {
                        FilesSearched = (int?)stats["searches"] ?? -1;
                        BytesSearched = (long?)stats["bytes_searched"] ?? -1;
                    }
                    break;
            }
        }

        /// <summary>
        /// 输出结束（rg 退出或被结束）：把当前文件中待定的匹配落入 <see cref="Matches"/>。
        /// </summary>
        public void Complete()
        {
            FlushFile();
        }

        private void HandleMatch(JObject data)
        {
            var lineNumber = (long?)data["line_number"] ?? 0;
            var path = data["path"] != null ? _pathMapper(DecodeField(data["path"]) ?? string.Empty) : _currentPath;
            if (_currentPath == null)
                _currentPath = path;

            CountMatch();

            var lineBytes = DecodeBytes(data["lines"]);
            var lineText = TrimLineEnd(Utf8(lineBytes));

            if (!OverLimit)
            {
                int column = 1;
                string matchText = string.Empty;
                if (data["submatches"] is JArray subs && subs.Count > 0 && subs[0] is JObject first)
                {
                    var start = (int?)first["start"] ?? 0;
                    column = ByteOffsetToCharIndex(lineBytes, start) + 1;
                    matchText = Utf8(DecodeBytes(first["match"]));
                }

                _pending.Add(new RgMatch
                {
                    Path = path,
                    Line = (int)lineNumber,
                    Column = column,
                    Match = matchText,
                    LineText = Clip(lineText),
                });
                _lastCollectedLine = lineNumber;
            }

            CacheLine(lineNumber, lineText);
            if (CountCapReached)
            {
                // 达到计数上限：用已缓存的上下文收尾，之后不再处理任何行
                FlushFile();
                return;
            }
            CheckDrained(lineNumber);
        }

        private void HandleContext(JObject data)
        {
            var lineNumber = (long?)data["line_number"] ?? 0;
            CacheLine(lineNumber, TrimLineEnd(Utf8(DecodeBytes(data["lines"]))));
            CheckDrained(lineNumber);
        }

        private void CacheLine(long lineNumber, string text)
        {
            if (ContextLines > 0 && lineNumber > 0)
                _lineCache[lineNumber] = text;
        }

        /// <summary>收满后，当前行已越过最后一条收集匹配的下文范围：转入只计数模式。</summary>
        private void CheckDrained(long lineNumber)
        {
            if (!_countOnly && OverLimit && lineNumber > _lastCollectedLine + ContextLines)
            {
                FlushFile();
                _countOnly = true;
            }
        }

        private void CountMatch()
        {
            TotalMatches++;
            if (TotalMatches >= MaxCountedMatches)
                CountCapReached = true;
        }

        private void FlushFile()
        {
            foreach (var m in _pending)
            {
                m.Context = BuildContext(m);
                Matches.Add(m);
            }
            _pending.Clear();
            _lineCache.Clear();
            _currentPath = null;
        }

        private string BuildContext(RgMatch m)
        {
            var sb = new StringBuilder();
            for (long j = m.Line - ContextLines; j <= m.Line + ContextLines; j++)
            {
                string text;
                if (j == m.Line)
                    text = m.LineText;
                else if (!_lineCache.TryGetValue(j, out text))
                    continue;
                else
                    text = Clip(text);

                if (sb.Length > 0)
                    sb.Append('\n');
                var prefix = j == m.Line ? ">>>" : "   ";
                sb.Append(prefix).Append(' ').Append(j.ToString().PadLeft(5)).Append(" | ").Append(text);
            }
            return sb.ToString().TrimEnd();
        }

        private string Clip(string text)
        {
            if (text == null)
                return string.Empty;
            if (MaxLineChars <= 0 || text.Length <= MaxLineChars)
                return text;
            return text.Substring(0, MaxLineChars) + $"…[+{text.Length - MaxLineChars} chars]";
        }

        // ── 字段解码 ──

        /// <summary>
        /// rg 的 "arbitrary data" 字段：<c>{"text": "..."}</c> 或 <c>{"bytes": "base64"}</c>。返回 UTF-8 字节。
        /// </summary>
        internal static byte[] DecodeBytes(JToken field)
        {
            if (!(field is JObject obj))
                return Array.Empty<byte>();

            var text = obj["text"];
            if (text != null && text.Type == JTokenType.String)
                return Encoding.UTF8.GetBytes((string)text);

            var bytes = obj["bytes"];
            if (bytes != null && bytes.Type == JTokenType.String)
            {
                try
                {
                    return Convert.FromBase64String((string)bytes);
                }
                catch (FormatException)
                {
                    return Array.Empty<byte>();
                }
            }
            return Array.Empty<byte>();
        }

        /// <summary>解码为字符串（非 UTF-8 字节以替换字符显示）。</summary>
        internal static string DecodeField(JToken field)
        {
            if (!(field is JObject))
                return null;
            return Utf8(DecodeBytes(field));
        }

        private static string Utf8(byte[] bytes)
        {
            return bytes == null || bytes.Length == 0 ? string.Empty : Encoding.UTF8.GetString(bytes);
        }

        /// <summary>字节偏移 → UTF-16 字符下标（0-based）。越界时夹到行尾。</summary>
        internal static int ByteOffsetToCharIndex(byte[] lineBytes, int byteOffset)
        {
            if (lineBytes == null || byteOffset <= 0)
                return 0;
            if (byteOffset > lineBytes.Length)
                byteOffset = lineBytes.Length;
            return Encoding.UTF8.GetCharCount(lineBytes, 0, byteOffset);
        }

        private static string TrimLineEnd(string text)
        {
            return text?.TrimEnd('\r', '\n') ?? string.Empty;
        }

        /// <summary>
        /// 默认路径规范化：反斜杠转 '/'，去掉开头的 "./"。
        /// </summary>
        public static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return string.Empty;
            var p = path.Replace('\\', '/');
            while (p.StartsWith("./", StringComparison.Ordinal))
                p = p.Substring(2);
            return p;
        }
    }
}
