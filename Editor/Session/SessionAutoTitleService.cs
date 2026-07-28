using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Core.Compression;
using AgentCore.Editor.LLM;
using AgentCore.Editor.Utils;

namespace AgentCore.Editor.Session
{
    /// <summary>
    /// 会话自动重命名服务 — 基于会话最近上下文调用 LLM 生成能反映当前主话题的简短标题。
    /// <para>
    /// 解决痛点：默认标题取用户第一条消息前 30 字符且永不更新，
    /// 当会话话题漂移后标题仍停留在最初话题，无法反映当前内容。
    /// </para>
    /// <para>
    /// 复用统一 LLM 管道（<see cref="CompressionLLMClientFactory"/> 同款：OpenAICompatibleClient +
    /// RequestEnrichment + GetEffectiveMaxTokens），非流式、低温度、小 token 预算。
    /// </para>
    /// </summary>
    public static class SessionAutoTitleService
    {
        private const string LogPrefix = "[SessionAutoTitle] ";

        /// <summary>
        /// 特殊哨兵值：当 <c>allowKeep=true</c> 且 LLM 判定当前标题仍能准确概括最近话题时返回。
        /// 调用方识别此值后应「保留当前标题、不做任何修改」。
        /// </summary>
        public const string KEEP_TITLE_SENTINEL = "__KEEP_TITLE__";

        /// <summary>参与标题生成的最近对话消息条数上限（user+assistant，跳过 system/tool）。</summary>
        public const int RecentMessageWindow = 12;

        /// <summary>单条消息纳入上下文时的字符截断长度（避免超长消息挤占预算）。</summary>
        private const int PerMessageCharCap = 500;

        /// <summary>标题 content 的 token 预算（标题很短，给足冗余）。</summary>
        private const int TitleMaxTokens = 64;

        /// <summary>生成标题的最大字符数（超出截断）。</summary>
        private const int TitleCharCap = 24;

        /// <summary>
        /// 为指定会话生成一个反映最近上下文主话题的标题。
        /// </summary>
        /// <param name="sessionId">目标会话 ID。</param>
        /// <param name="currentTitle">
        /// 当前标题。仅在 <paramref name="allowKeep"/>=true 时用于让 LLM 判断是否需要更新。
        /// </param>
        /// <param name="allowKeep">
        /// 是否允许 LLM 返回「保留当前标题」。true 时 prompt 追加 KEEP 判定分支，
        /// 命中则返回 <see cref="KEEP_TITLE_SENTINEL"/>。
        /// 手动触发（右键→自动重命名）保持 false，走原逻辑永远生成新标题。
        /// </param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>
        /// 生成的标题；命中 KEEP 判定时返回 <see cref="KEEP_TITLE_SENTINEL"/>；
        /// 失败或无足够上下文时返回 null。
        /// </returns>
        public static async Task<string> GenerateTitleAsync(
            string sessionId,
            string currentTitle = null,
            bool allowKeep = false,
            CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                AgentCoreLog.Warning($"{LogPrefix}Cannot generate title for empty session id.");
                return null;
            }

            // 1. 读取会话消息（支持任意会话，不限当前活动会话）
            List<ChatMessage> messages;
            try
            {
                var session = SessionStorage.Load(sessionId);
                if (session == null)
                {
                    AgentCoreLog.Warning($"{LogPrefix}Session not found: {sessionId}");
                    return null;
                }
                messages = session.ToMessages();
            }
            catch (Exception ex)
            {
                AgentCoreLog.Error($"{LogPrefix}Failed to load session {sessionId}: {ex.Message}");
                return null;
            }

            // 2. 取最近 N 条有意义的 user/assistant 消息作为上下文
            var contextText = BuildRecentContext(messages);
            if (string.IsNullOrWhiteSpace(contextText))
            {
                AgentCoreLog.Warning($"{LogPrefix}No usable conversation content in session {sessionId}.");
                return null;
            }

            // 3. 调用 LLM 生成标题（复用压缩管道同款客户端）
            try
            {
                var client = CompressionLLMClientFactory.CreateCompressionClient();
                if (client == null)
                {
                    AgentCoreLog.Warning($"{LogPrefix}LLM client unavailable.");
                    return null;
                }

                var systemPrompt =
                    "你是一个会话标题生成器。根据给定的对话内容，生成一个能准确概括**当前主要话题**的简短中文标题。" +
                    "要求：(1) 不超过 " + TitleCharCap + " 个字；(2) 只输出标题本身，不要引号、不要标点结尾、不要任何解释或前缀；" +
                    "(3) 若对话涉及多个话题，以最近的话题为主。";

                // allowKeep=true 且有当前标题时，追加 KEEP 判定分支。
                var useKeep = allowKeep && !string.IsNullOrWhiteSpace(currentTitle);
                if (useKeep)
                {
                    systemPrompt +=
                        $"\n当前标题：「{currentTitle}」。若当前标题仍能准确概括最近对话主题，直接输出 KEEP 三个字符即可；" +
                        "否则输出新标题（不含引号、不含标点、不含前缀）。";
                }

                var prompt = new List<ChatMessage>
                {
                    ChatMessage.System(systemPrompt),
                    ChatMessage.User("对话内容：\n\n" + contextText + "\n\n请生成标题：")
                };

                var response = await client.ChatCompletionAsync(
                    prompt, null, ct, contentMaxTokens: TitleMaxTokens);

                if (response?.Choices != null && response.Choices.Count > 0)
                {
                    var raw = response.Choices[0].Message?.Content;

                    // KEEP 判定（仅在 allowKeep 模式下生效）：
                    // (1) 模型显式返回 "KEEP"（大小写不敏感）；
                    // (2) 模型把当前标题原样吐回来（视为「无需更新」）。
                    if (useKeep && !string.IsNullOrWhiteSpace(raw))
                    {
                        var trimmed = raw.Trim();
                        if (trimmed.Equals("KEEP", StringComparison.OrdinalIgnoreCase)
                            || trimmed == currentTitle.Trim())
                        {
                            AgentCoreLog.Info($"{LogPrefix}Title kept for {sessionId} (LLM judged current title still accurate).");
                            return KEEP_TITLE_SENTINEL;
                        }
                    }

                    var title = SanitizeTitle(raw);
                    if (!string.IsNullOrWhiteSpace(title))
                    {
                        AgentCoreLog.Info($"{LogPrefix}Generated title for {sessionId}: \"{title}\"");
                        return title;
                    }
                }

                AgentCoreLog.Warning($"{LogPrefix}LLM returned empty title for session {sessionId}.");
                return null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AgentCoreLog.Error($"{LogPrefix}Title generation failed for {sessionId}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 从消息列表中提取最近 N 条 user/assistant 消息，拼成上下文文本。
        /// 跳过 system/tool 消息与空内容；超长单条消息截断。
        /// </summary>
        private static string BuildRecentContext(List<ChatMessage> messages)
        {
            if (messages == null || messages.Count == 0) return null;

            // 只保留 user/assistant 且有文本内容的消息
            var meaningful = messages
                .Where(m => (m.Role == "user" || m.Role == "assistant")
                            && !string.IsNullOrWhiteSpace(m.Content))
                .ToList();

            if (meaningful.Count == 0) return null;

            // 取最近 RecentMessageWindow 条
            var recent = meaningful.Count > RecentMessageWindow
                ? meaningful.GetRange(meaningful.Count - RecentMessageWindow, RecentMessageWindow)
                : meaningful;

            var sb = new StringBuilder();
            foreach (var msg in recent)
            {
                var roleLabel = msg.Role == "user" ? "用户" : "助手";
                var content = msg.Content.Replace("\r", "").Trim();
                if (content.Length > PerMessageCharCap)
                {
                    content = content.Substring(0, PerMessageCharCap) + "…";
                }
                sb.AppendLine($"[{roleLabel}]: {content}");
            }

            return sb.ToString();
        }

        /// <summary>
        /// 清洗 LLM 返回的标题：去除首尾引号/空白/换行，截断到长度上限。
        /// </summary>
        private static string SanitizeTitle(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            var title = raw.Trim();

            // 取第一行（防止模型输出多行）
            var newlineIdx = title.IndexOfAny(new[] { '\n', '\r' });
            if (newlineIdx >= 0)
            {
                title = title.Substring(0, newlineIdx).Trim();
            }

            // 去除包裹的引号（中英文）
            title = title.Trim('"', '\'', '“', '”', '‘', '’', '「', '」', '『', '』', ' ', '　');

            // 去除常见前缀（模型偶尔加）
            foreach (var prefix in new[] { "标题：", "标题:", "Title:", "会话标题：", "会话标题:" })
            {
                if (title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    title = title.Substring(prefix.Length).Trim();
                }
            }

            title = title.Trim('"', '\'', '“', '”', '「', '」', ' ', '　');

            if (string.IsNullOrWhiteSpace(title)) return null;

            if (title.Length > TitleCharCap)
            {
                title = title.Substring(0, TitleCharCap);
            }

            return title;
        }
    }
}
