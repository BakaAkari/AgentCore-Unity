using System;
using System.IO;
using System.Text;
using AgentCore.Editor.Utils;
using UnityEngine;

namespace AgentCore.Editor.Session
{
    /// <summary>
    /// 会话导出器 — 将 <see cref="SessionData"/> 导出为 Markdown 或 JSON 文件。
    /// <para>
    /// 支持两种导出格式：
    /// <list type="bullet">
    ///   <item><b>Markdown</b>: 人类可读格式，包含角色标签、时间戳、工具调用摘要</item>
    ///   <item><b>JSON</b>: 完整数据格式，可用于导入或分析</item>
    /// </list>
    /// </para>
    /// </summary>
    public static class SessionExporter
    {
        /// <summary>
        /// 导出格式枚举。
        /// </summary>
        public enum ExportFormat
        {
            /// <summary>Markdown 格式（.md）</summary>
            Markdown,

            /// <summary>JSON 格式（.json）</summary>
            Json
        }

        /// <summary>
        /// 将会话数据导出为 Markdown 字符串。
        /// </summary>
        /// <param name="session">会话数据</param>
        /// <returns>Markdown 格式的字符串</returns>
        /// <exception cref="ArgumentNullException">session 为 null 时抛出</exception>
        public static string ExportToMarkdown(SessionData session)
        {
            if (session == null)
                throw new ArgumentNullException(nameof(session));

            var sb = new StringBuilder();

            // 标题和元信息
            sb.AppendLine("# AgentCore 对话记录");
            sb.AppendLine();
            sb.AppendLine($"**会话**: {EscapeMarkdown(session.Title ?? "未命名会话")}");
            sb.AppendLine($"**创建时间**: {session.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"**最后更新**: {session.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"**消息数**: {session.MessageCount}");
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();

            // 导出对话轮次
            if (session.Turns != null && session.Turns.Count > 0)
            {
                foreach (var turn in session.Turns)
                {
                    ExportTurn(sb, turn);
                }
            }
            else if (session.Messages != null && session.Messages.Count > 0)
            {
                // 如果没有 Turns 数据，回退到 Messages
                foreach (var msg in session.Messages)
                {
                    ExportMessage(sb, msg);
                }
            }
            else
            {
                sb.AppendLine("*(空会话)*");
            }

            // 页脚
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine($"*导出时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss} | 由 AgentCore 生成*");

            return sb.ToString();
        }

        /// <summary>
        /// 将会话数据导出为 JSON 字符串。
        /// </summary>
        /// <param name="session">会话数据</param>
        /// <returns>格式化的 JSON 字符串</returns>
        /// <exception cref="ArgumentNullException">session 为 null 时抛出</exception>
        public static string ExportToJson(SessionData session)
        {
            if (session == null)
                throw new ArgumentNullException(nameof(session));

            return JsonHelper.Serialize(session, pretty: true);
        }

        /// <summary>
        /// 将会话数据导出到文件。
        /// </summary>
        /// <param name="session">会话数据</param>
        /// <param name="filePath">目标文件路径</param>
        /// <param name="format">导出格式</param>
        /// <returns>是否导出成功</returns>
        public static bool ExportToFile(SessionData session, string filePath, ExportFormat format)
        {
            if (session == null || string.IsNullOrEmpty(filePath))
                return false;

            try
            {
                string content = format switch
                {
                    ExportFormat.Markdown => ExportToMarkdown(session),
                    ExportFormat.Json => ExportToJson(session),
                    _ => throw new ArgumentOutOfRangeException(nameof(format))
                };

                // 确保目录存在
                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(filePath, content, Encoding.UTF8);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AgentCore] Failed to export session: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 获取导出文件的默认文件名。
        /// </summary>
        /// <param name="session">会话数据</param>
        /// <param name="format">导出格式</param>
        /// <returns>建议的文件名</returns>
        public static string GetDefaultFileName(SessionData session, ExportFormat format)
        {
            var title = session?.Title ?? "conversation";
            // 清理文件名中的非法字符
            var safeName = SanitizeFileName(title);
            var date = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var ext = format == ExportFormat.Markdown ? "md" : "json";
            return $"AgentCore_{safeName}_{date}.{ext}";
        }

        #region 私有方法

        /// <summary>
        /// 导出单个对话轮次为 Markdown。
        /// </summary>
        private static void ExportTurn(StringBuilder sb, SerializableConversationTurn turn)
        {
            if (turn == null) return;

            // 跳过 system 消息
            if (turn.Role == "system") return;

            var roleName = GetRoleDisplayName(turn.Role);
            var time = turn.Timestamp.ToLocalTime().ToString("HH:mm");

            sb.AppendLine($"## {roleName} ({time})");
            sb.AppendLine();

            if (!string.IsNullOrEmpty(turn.Content))
            {
                sb.AppendLine(turn.Content.Trim());
                sb.AppendLine();
            }

            // 导出工具调用信息
            if (turn.ToolCalls != null && turn.ToolCalls.Count > 0)
            {
                foreach (var tc in turn.ToolCalls)
                {
                    var status = tc.Success ? "成功" : "失败";
                    var timeMs = tc.ExecutionTimeMs > 0 ? $" ({tc.ExecutionTimeMs:F0}ms)" : "";
                    sb.AppendLine($"> **工具调用**: `{tc.ToolName}` — {status}{timeMs}");
                }
                sb.AppendLine();
            }
        }

        /// <summary>
        /// 导出单条消息为 Markdown（回退模式，当 Turns 不可用时使用）。
        /// </summary>
        private static void ExportMessage(StringBuilder sb, SerializableChatMessage msg)
        {
            if (msg == null) return;

            // 跳过 system 和 tool 消息
            if (msg.Role == "system" || msg.Role == "tool") return;

            var roleName = GetRoleDisplayName(msg.Role);
            sb.AppendLine($"## {roleName}");
            sb.AppendLine();

            if (!string.IsNullOrEmpty(msg.Content))
            {
                sb.AppendLine(msg.Content.Trim());
                sb.AppendLine();
            }

            // 如果 assistant 消息有 tool_calls，简要列出
            if (msg.ToolCalls != null && msg.ToolCalls.Count > 0)
            {
                foreach (var tc in msg.ToolCalls)
                {
                    sb.AppendLine($"> **工具调用**: `{tc.FunctionName}`");
                }
                sb.AppendLine();
            }
        }

        /// <summary>
        /// 获取角色的中文显示名称。
        /// </summary>
        private static string GetRoleDisplayName(string role)
        {
            return role switch
            {
                "user" => "\U0001F464 用户",
                "assistant" => "\U0001F916 助手",
                "system" => "\U00002699 系统",
                _ => role
            };
        }

        /// <summary>
        /// 转义 Markdown 特殊字符。
        /// </summary>
        private static string EscapeMarkdown(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            // 只转义可能破坏 Markdown 结构的字符
            return text.Replace("|", "\\|");
        }

        /// <summary>
        /// 清理文件名中的非法字符。
        /// </summary>
        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "conversation";

            var invalidChars = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
            {
                if (Array.IndexOf(invalidChars, c) < 0 && c != ' ')
                {
                    sb.Append(c);
                }
                else if (c == ' ')
                {
                    sb.Append('_');
                }
            }

            var result = sb.ToString();
            // 限制长度
            if (result.Length > 50)
            {
                result = result.Substring(0, 50);
            }

            return string.IsNullOrEmpty(result) ? "conversation" : result;
        }

        #endregion
    }
}
