using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Bootstrap;
using AgentCore.Editor.Skills;
using AgentCore.Editor.Tools;
using AgentCore.Editor.Utils;
using Newtonsoft.Json.Linq;

namespace AgentCore.Editor.Components.MCP.Bridge
{
    /// <summary>
    /// MCP Resources：把工程上下文、场景层级、Console、版本控制状态和工程内文本文件暴露为只读资源。
    /// 读取复用只读工具（经同一个 dispatcher，审计照常记录），不另写一套 Unity 访问代码。
    /// </summary>
    public static class McpResourceCatalog
    {
        public const string ProjectContextUri = "unity://project/context";
        public const string SceneHierarchyUri = "unity://scene/hierarchy";
        public const string ConsoleUri = "unity://console";
        public const string VcsStatusUri = "unity://vcs/status";
        public const string AssetUriPrefix = "unity://asset/";
        public const string AssetUriTemplate = "unity://asset/{+path}";

        /// <summary>资源列表（主线程：检查可选工具是否存在）。</summary>
        public static JObject List()
        {
            var resources = new JArray
            {
                Resource(ProjectContextUri, "project-context", "Project context",
                    "Unity version, render pipeline, packages, scenes and scripting setup of this project (Markdown).", "text/markdown"),
                Resource(SceneHierarchyUri, "scene-hierarchy", "Scene hierarchy",
                    "GameObject hierarchy of the open scenes (JSON).", "application/json"),
                Resource(ConsoleUri, "console", "Console",
                    "Recent Unity Console errors, warnings and logs (JSON).", "application/json"),
            };
            if (ToolRegistry.Instance.HasTool("version_control"))
            {
                resources.Add(Resource(VcsStatusUri, "vcs-status", "Version control status",
                    "Working copy status: modified, added, deleted, conflicted and locked files (JSON).", "application/json"));
            }

            var templates = new JArray
            {
                new JObject
                {
                    ["uriTemplate"] = AssetUriTemplate,
                    ["name"] = "asset",
                    ["title"] = "Project file",
                    ["description"] = "A text file inside the Unity project, by path relative to the project root, e.g. unity://asset/Assets/Scripts/Player.cs or unity://asset/Assets/Prefabs/Enemy.prefab (Unity YAML).",
                    ["mimeType"] = "text/plain",
                }
            };

            return new JObject { ["resources"] = resources, ["resourceTemplates"] = templates };
        }

        private static JObject Resource(string uri, string name, string title, string description, string mimeType) => new JObject
        {
            ["uri"] = uri,
            ["name"] = name,
            ["title"] = title,
            ["description"] = description,
            ["mimeType"] = mimeType,
        };

        /// <summary>
        /// 读取资源。成功返回 <c>{ contents: [ { uri, mimeType, text } ] }</c>；URI 无效或读取失败抛 <see cref="McpRequestException"/>。
        /// </summary>
        public static async Task<JObject> ReadAsync(string uri, string client, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(uri))
                throw new McpRequestException(-32602, "Missing resource uri.");

            string text;
            string mimeType;
            switch (uri)
            {
                case ProjectContextUri:
                    text = await MainThread.RunAsync(() => ProjectContextCollector.Collect()).ConfigureAwait(false);
                    mimeType = "text/markdown";
                    break;
                case SceneHierarchyUri:
                    text = await ReadViaToolAsync("manage_scene", new JObject { ["action"] = "get_hierarchy" }, client, ct).ConfigureAwait(false);
                    mimeType = "application/json";
                    break;
                case ConsoleUri:
                    text = await ReadViaToolAsync("read_console", new JObject { ["action"] = "get_all" }, client, ct).ConfigureAwait(false);
                    mimeType = "application/json";
                    break;
                case VcsStatusUri:
                    text = await ReadViaToolAsync("version_control", new JObject { ["action"] = "get_status" }, client, ct).ConfigureAwait(false);
                    mimeType = "application/json";
                    break;
                default:
                    if (!uri.StartsWith(AssetUriPrefix, StringComparison.Ordinal))
                        throw new McpRequestException(-32602, $"Resource not found: {uri}");
                    var path = Uri.UnescapeDataString(uri.Substring(AssetUriPrefix.Length)).Trim('/');
                    if (path.Length == 0)
                        throw new McpRequestException(-32602, $"Resource not found: {uri}");
                    text = await ReadViaToolAsync("manage_file", new JObject { ["action"] = "read_file", ["path"] = path, ["line_numbers"] = false }, client, ct).ConfigureAwait(false);
                    mimeType = "text/plain";
                    text = ExtractFileContent(text);
                    break;
            }

            return new JObject
            {
                ["contents"] = new JArray
                {
                    new JObject { ["uri"] = uri, ["mimeType"] = mimeType, ["text"] = text ?? string.Empty }
                }
            };
        }

        private static async Task<string> ReadViaToolAsync(string tool, JObject arguments, string client, CancellationToken ct)
        {
            if (!ToolRegistry.Instance.HasTool(tool))
                throw new McpRequestException(-32602, $"Resource unavailable: the '{tool}' tool is not enabled in this project.");
            var outcome = await McpToolExecutor.ExecuteAsync(tool, arguments, client, ct).ConfigureAwait(false);
            if (outcome.NotStarted)
                throw new McpRequestException(-32603, "Unity is reloading scripts; retry shortly.");
            if (outcome.Status == McpBridgeProtocol.StatusError)
                throw new McpRequestException(-32602, outcome.Text);
            return outcome.Text;
        }

        /// <summary>manage_file read_file 的结果是 JSON 包装；资源内容只要文件正文。</summary>
        internal static string ExtractFileContent(string toolOutput)
        {
            try
            {
                var json = JObject.Parse(toolOutput);
                var content = json["data"]?["content"] ?? json["content"];
                if (content != null && content.Type == JTokenType.String)
                    return (string)content;
            }
            catch (Exception)
            {
                // 不是 JSON：原样返回
            }
            return toolOutput;
        }
    }

    /// <summary>MCP Prompts：AgentCore 的 Skills（内置与工程自定义），每个 skill 一个 prompt。</summary>
    public static class McpPromptCatalog
    {
        public static JObject List()
        {
            var prompts = new JArray();
            foreach (var skill in SkillRegistry.Instance.GetAll().OrderBy(s => s.Name, StringComparer.Ordinal))
            {
                prompts.Add(new JObject
                {
                    ["name"] = skill.Name,
                    ["title"] = skill.Name,
                    ["description"] = skill.Description ?? string.Empty,
                    ["arguments"] = new JArray
                    {
                        new JObject
                        {
                            ["name"] = "task",
                            ["description"] = "What you want to do with this skill (optional; appended after the skill instructions).",
                            ["required"] = false,
                        }
                    }
                });
            }
            return new JObject { ["prompts"] = prompts };
        }

        public static JObject Get(string name, JObject arguments)
        {
            var skill = string.IsNullOrEmpty(name) ? null : SkillRegistry.Instance.TryGet(name);
            if (skill == null)
                throw new McpRequestException(-32602, $"Unknown prompt: {name}");
            var content = SkillRegistry.Instance.GetContent(name);
            if (content == null)
                throw new McpRequestException(-32603, $"Could not read skill '{name}'.");

            var task = (string)arguments?["task"];
            var text = string.IsNullOrWhiteSpace(task)
                ? content
                : content.TrimEnd() + "\n\n---\n\nTask: " + task.Trim();

            return new JObject
            {
                ["description"] = skill.Description ?? string.Empty,
                ["messages"] = new JArray
                {
                    new JObject
                    {
                        ["role"] = "user",
                        ["content"] = new JObject { ["type"] = "text", ["text"] = text }
                    }
                }
            };
        }
    }

    /// <summary>bridge 处理 request 时的协议错误（JSON-RPC code + message），由 relay 原样转成 MCP 错误响应。</summary>
    public sealed class McpRequestException : Exception
    {
        public McpRequestException(int code, string message) : base(message)
        {
            Code = code;
        }

        public int Code { get; }
    }
}
