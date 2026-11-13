using System.Text;

namespace AgentCore.Editor.Components.MCP.Config
{
    /// <summary>
    /// 生成 MCP 客户端配置：Claude Code 的 <c>claude mcp add</c> 命令与 Codex 的 <c>config.toml</c> 片段。
    /// 服务器名固定为 <see cref="ServerName"/>；relay 由 Unity 自带 Mono 运行，<c>--project</c> 指向本 Unity 工程。
    /// </summary>
    public static class McpClientConfig
    {
        public const string ServerName = "agentcore";

        /// <summary>
        /// Claude Code：用户级（任意目录下启动 Claude Code 都可用）。路径一律加双引号，cmd / PowerShell / bash 中都能直接粘贴执行。
        /// </summary>
        public static string ClaudeCodeCommand(string monoPath, string relayPath, string projectPath)
        {
            return $"claude mcp add --scope user {ServerName} -- {Quote(monoPath)} {Quote(relayPath)} --project {Quote(projectPath)}";
        }

        /// <summary>
        /// Codex：<c>~/.codex/config.toml</c> 片段。工具超时放宽到 10 分钟（构建、跑测试、等待编译都可能较慢）。
        /// </summary>
        public static string CodexToml(string monoPath, string relayPath, string projectPath)
        {
            var sb = new StringBuilder();
            sb.Append("[mcp_servers.").Append(ServerName).Append("]\n");
            sb.Append("command = ").Append(TomlString(monoPath)).Append('\n');
            sb.Append("args = [").Append(TomlString(relayPath)).Append(", \"--project\", ").Append(TomlString(projectPath)).Append("]\n");
            sb.Append("startup_timeout_sec = 60\n");
            sb.Append("tool_timeout_sec = 600\n");
            return sb.ToString();
        }

        private static string Quote(string value) => "\"" + (value ?? string.Empty) + "\"";

        /// <summary>Windows 路径优先用 TOML 字面量字符串（单引号，反斜杠不转义）；含单引号时退回基本字符串并转义。</summary>
        public static string TomlString(string value)
        {
            value ??= string.Empty;
            if (value.IndexOf('\'') < 0 && value.IndexOf('\n') < 0)
                return "'" + value + "'";
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
    }
}
