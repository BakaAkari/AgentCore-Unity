using System;
using System.Collections.Generic;
using System.IO;
using AgentCore.Editor.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentCore.Editor.McpClient
{
    /// <summary>
    /// 一个外部 MCP 服务器的配置。格式与 Claude Desktop / Claude Code 的 <c>mcpServers</c> 相同，可以直接粘贴：
    /// <code>
    /// { "mcpServers": {
    ///     "jira":  { "command": "npx", "args": ["-y", "mcp-atlassian"], "env": { "JIRA_URL": "..." } },
    ///     "docs":  { "type": "http", "url": "https://example.com/mcp", "headers": { "Authorization": "Bearer ..." } },
    ///     "off":   { "command": "...", "disabled": true }
    /// } }
    /// </code>
    /// </summary>
    public sealed class McpServerConfig
    {
        public string Name;
        public string Command;
        public List<string> Args = new List<string>();
        public Dictionary<string, string> Env = new Dictionary<string, string>();
        public string Cwd;
        public string Url;
        public Dictionary<string, string> Headers = new Dictionary<string, string>();
        public bool Disabled;

        /// <summary>来源：user（个人，UserSettings）或 project（遗留 ProjectSettings 文件，只读）。</summary>
        public string Scope;

        public bool IsHttp => !string.IsNullOrEmpty(Url);

        public string Describe() => IsHttp ? Url : (Command + (Args.Count > 0 ? " " + string.Join(" ", Args) : string.Empty));
    }

    /// <summary>
    /// 读取外部 MCP 服务器配置：个人级 <c>UserSettings/AgentCore/McpServers.json</c>（唯一可写，同名时优先）；
    /// 遗留工程级 <c>ProjectSettings/AgentCore/McpServers.json</c> 仅在已存在时只读合并，AgentCore 永不创建或写入。
    /// </summary>
    public static class McpServerConfigStore
    {
        public const string ProjectRelativePath = "ProjectSettings/AgentCore/McpServers.json";
        public const string UserRelativePath = "UserSettings/AgentCore/McpServers.json";

        public static string ProjectRoot => Path.GetFullPath(Directory.GetCurrentDirectory());

        public static string ProjectFile => Path.Combine(ProjectRoot, ProjectRelativePath);

        public static string UserFile => Path.Combine(ProjectRoot, UserRelativePath);

        /// <summary>读取并合并两个配置文件；文件格式错误时记录在 <paramref name="errors"/>，不抛异常。</summary>
        public static List<McpServerConfig> Load(List<string> errors = null)
        {
            var merged = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal);
            foreach (var config in Parse(ProjectFile, "project", errors))
                merged[config.Name] = config;
            foreach (var config in Parse(UserFile, "user", errors))
                merged[config.Name] = config;
            return new List<McpServerConfig>(merged.Values);
        }

        public static List<McpServerConfig> Parse(string path, string scope, List<string> errors)
        {
            var result = new List<McpServerConfig>();
            if (!File.Exists(path))
                return result;
            try
            {
                return ParseJson(File.ReadAllText(path), scope);
            }
            catch (Exception ex) when (ex is JsonException || ex is IOException || ex is InvalidCastException)
            {
                errors?.Add($"{path}: {ex.Message}");
                AgentCoreLog.Warning($"[AgentCore][MCP client] Invalid config {path}: {ex.Message}");
                return result;
            }
        }

        public static List<McpServerConfig> ParseJson(string json, string scope)
        {
            var result = new List<McpServerConfig>();
            var root = JObject.Parse(json);
            if (!(root["mcpServers"] is JObject servers))
                return result;
            foreach (var pair in servers)
            {
                if (!(pair.Value is JObject entry))
                    continue;
                var config = new McpServerConfig
                {
                    Name = pair.Key,
                    Command = (string)entry["command"],
                    Cwd = (string)entry["cwd"],
                    Url = (string)entry["url"],
                    Disabled = (bool?)entry["disabled"] == true || (bool?)entry["enabled"] == false,
                    Scope = scope,
                };
                if (entry["args"] is JArray args)
                {
                    foreach (var arg in args)
                        config.Args.Add((string)arg);
                }
                if (entry["env"] is JObject env)
                {
                    foreach (var e in env)
                        config.Env[e.Key] = (string)e.Value;
                }
                if (entry["headers"] is JObject headers)
                {
                    foreach (var h in headers)
                        config.Headers[h.Key] = (string)h.Value;
                }
                if (string.IsNullOrEmpty(config.Command) && string.IsNullOrEmpty(config.Url))
                    continue;
                result.Add(config);
            }
            return result;
        }

        /// <summary>个人配置文件不存在时创建一个空配置，返回路径（设置页“打开配置”使用）。遗留工程级文件永不创建。</summary>
        public static string EnsureUserFile()
        {
            var path = UserFile;
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, "{\n  \"mcpServers\": {\n  }\n}\n");
            }
            return path;
        }
    }
}
