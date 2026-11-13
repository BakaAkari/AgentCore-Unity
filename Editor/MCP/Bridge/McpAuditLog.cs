using System;
using System.IO;
using System.Text;
using AgentCore.Editor.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentCore.Editor.Components.MCP.Bridge
{
    /// <summary>
    /// MCP 调用审计：每次调用一行 JSON 追加到 <c>Library/AgentCore/Mcp/audit.jsonl</c>。超过 5 MB 时轮换为 <c>audit.1.jsonl</c>。
    /// 审计失败只记警告，不影响调用。
    /// </summary>
    public static class McpAuditLog
    {
        private const long MaxBytes = 5L * 1024 * 1024;
        private const int MaxArgumentChars = 500;
        private static readonly object Gate = new object();

        /// <summary>测试可替换的写入路径。</summary>
        internal static Func<string> PathProvider = () => McpPaths.AuditFile;

        public static void Append(string client, string tool, JObject arguments, string status, double elapsedMs, string error = null)
        {
            var entry = new JObject
            {
                ["time"] = DateTime.Now.ToString("o"),
                ["client"] = client,
                ["tool"] = tool,
                ["action"] = (string)arguments?["action"],
                ["arguments"] = Truncate(arguments?.ToString(Formatting.None)),
                ["status"] = status,
                ["elapsedMs"] = Math.Round(elapsedMs, 1),
            };
            if (!string.IsNullOrEmpty(error))
                entry["error"] = Truncate(error);

            try
            {
                var path = PathProvider();
                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > MaxBytes)
                    {
                        var rotated = Path.Combine(info.DirectoryName ?? string.Empty, "audit.1.jsonl");
                        if (File.Exists(rotated))
                            File.Delete(rotated);
                        File.Move(path, rotated);
                    }
                    File.AppendAllText(path, entry.ToString(Formatting.None) + "\n", new UTF8Encoding(false));
                }
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore][MCP] Audit log write failed: {ex.Message}");
            }
        }

        private static string Truncate(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= MaxArgumentChars)
                return value;
            return value.Substring(0, MaxArgumentChars) + "...";
        }
    }
}
