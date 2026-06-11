using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Bootstrap;
using AgentCore.Editor.Tools.Infrastructure;
using Newtonsoft.Json.Linq;
using UnityEngine;
using AgentCore.Editor.Workspace;

namespace AgentCore.Editor.Tools.Native.Bootstrap
{
    /// <summary>
    /// Tool for reading and writing workspace configuration files (PROJECT.md and SOUL.ext.md).
    /// These files are injected into the System Prompt at the start of each conversation.
    /// </summary>
    [AgentTool("manage_workspace_config",
        Description = "Read and write workspace configuration files that are injected into the System Prompt. " +
                      "PROJECT.md stores project conventions and personal preferences. " +
                      "SOUL.ext.md stores append-only Agent behavior rule extensions. " +
                      "rules.md (two layers: workspace-level and project-level) stores structured rules injected at the end of the System Prompt. " +
                      "Use this tool when the user wants to update project conventions, record preferences, modify Agent behavior rules, or manage project/workspace rules. " +
                      "Changes take effect in the NEXT conversation (Bootstrap loads at conversation start).",
        Category = "Bootstrap",
        RequiresMainThread = true,
        MayModifyScripts = false)]
    public class ManageWorkspaceConfigTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""read_project_config"", ""write_project_config"", ""read_soul_extension"", ""write_soul_extension"", ""get_config_paths"", ""read_rules"", ""write_rules"", ""get_rules_paths""],
                    ""description"": ""Action to perform. read_project_config/write_project_config: operate on PROJECT.md. read_soul_extension/write_soul_extension: operate on SOUL.ext.md. get_config_paths: return current file paths and existence status. read_rules/write_rules: operate on rules.md (requires 'layer' param: 'workspace' or 'project'). get_rules_paths: return rules file paths and existence status for both layers.""
                },
                ""content"": {
                    ""type"": ""string"",
                    ""description"": ""Full file content for write_project_config, write_soul_extension, or write_rules. Always read first, then write the complete updated content.""
                },
                ""layer"": {
                    ""type"": ""string"",
                    ""enum"": [""workspace"", ""project""],
                    ""description"": ""Rules layer for read_rules and write_rules. 'workspace' = {WorkspaceRoot}/.agentcore/rules.md (team-wide rules). 'project' = {UnityRoot}/AgentCore/rules.md (project-specific rules).""
                }
            },
            ""required"": [""action""]
        }");

        /// <summary>
        /// Tool metadata for registration and LLM tool definition.
        /// </summary>
        public ToolMetadata Metadata => new ToolMetadata(
            name: "manage_workspace_config",
            description: "Read and write workspace configuration files that are injected into the System Prompt. " +
                         "PROJECT.md stores project conventions and personal preferences. " +
                         "SOUL.ext.md stores append-only Agent behavior rule extensions. " +
                         "rules.md (two layers: workspace-level and project-level) stores structured rules injected at the end of the System Prompt. " +
                         "Use this tool when the user wants to update project conventions, record preferences, modify Agent behavior rules, or manage project/workspace rules. " +
                         "Changes take effect in the NEXT conversation (Bootstrap loads at conversation start).",
            category: "Bootstrap",
            parametersSchema: _parametersSchema,
            requiresMainThread: true
        );

        /// <summary>
        /// Execute the workspace config operation specified by the action parameter.
        /// </summary>
        public Task<ToolResult> ExecuteAsync(JObject parameters, CancellationToken cancellationToken = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ToolResponse response;

            try
            {
                var action = ToolHelpers.GetRequiredString(parameters, "action").ToLowerInvariant();

                switch (action)
                {
                    case "read_project_config":
                        response = HandleReadFile("PROJECT.md");
                        break;
                    case "write_project_config":
                        response = HandleWriteFile("PROJECT.md", parameters);
                        break;
                    case "read_soul_extension":
                        response = HandleReadFile("SOUL.ext.md");
                        break;
                    case "write_soul_extension":
                        response = HandleWriteFile("SOUL.ext.md", parameters);
                        break;
                    case "get_config_paths":
                        response = HandleGetConfigPaths();
                        break;
                    case "read_rules":
                        response = HandleReadRules(parameters);
                        break;
                    case "write_rules":
                        response = HandleWriteRules(parameters);
                        break;
                    case "get_rules_paths":
                        response = HandleGetRulesPaths();
                        break;
                    default:
                        response = ToolResponse.Fail(
                            $"Unknown action: '{action}'. Valid actions: read_project_config, write_project_config, read_soul_extension, write_soul_extension, get_config_paths, read_rules, write_rules, get_rules_paths");
                        break;
                }
            }
            catch (Exception ex)
            {
                response = ToolResponse.Fail($"Error: {ex.Message}");
            }

            sw.Stop();
            return Task.FromResult(response.ToToolResult(sw.Elapsed.TotalMilliseconds));
        }

        /// <summary>
        /// Read a workspace config file (PROJECT.md or SOUL.ext.md).
        /// Returns the file content, or a template hint if the file does not exist yet.
        /// </summary>
        private static ToolResponse HandleReadFile(string fileName)
        {
            var filePath = BootstrapLoader.FindUserFilePath(fileName);

            if (filePath == null || !File.Exists(filePath))
            {
                var defaultPath = BootstrapLoader.GetDefaultUserFilePath(fileName);
                var template = BootstrapLoader.GenerateUserFileTemplate(fileName);

                var notFoundData = new JObject
                {
                    ["file_name"] = fileName,
                    ["exists"] = false,
                    ["default_path"] = defaultPath,
                    ["template"] = template
                };

                return ToolResponse.OkWithData(notFoundData,
                    $"{fileName} does not exist yet. Default path: {defaultPath}. Use write action to create it.");
            }

            var content = File.ReadAllText(filePath);
            var lineCount = content.Split('\n').Length;

            var data = new JObject
            {
                ["file_name"] = fileName,
                ["exists"] = true,
                ["path"] = filePath,
                ["line_count"] = lineCount,
                ["content"] = content
            };

            return ToolResponse.OkWithData(data, $"Read {fileName} ({lineCount} lines) from: {filePath}");
        }

        /// <summary>
        /// Write full content to a workspace config file (PROJECT.md or SOUL.ext.md).
        /// Creates the file and parent directory if they do not exist.
        /// </summary>
        private static ToolResponse HandleWriteFile(string fileName, JObject parameters)
        {
            var content = ToolHelpers.GetRequiredString(parameters, "content");

            var filePath = BootstrapLoader.GetDefaultUserFilePath(fileName);
            if (filePath == null)
                return ToolResponse.Fail("Cannot determine project root path.");

            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            bool isNew = !File.Exists(filePath);
            File.WriteAllText(filePath, content, new System.Text.UTF8Encoding(false));

            var lineCount = content.Split('\n').Length;
            var data = new JObject
            {
                ["file_name"] = fileName,
                ["path"] = filePath,
                ["lines_written"] = lineCount,
                ["created"] = isNew
            };

            var verb = isNew ? "Created" : "Updated";
            return ToolResponse.OkWithData(data,
                $"{verb} {fileName} ({lineCount} lines) at: {filePath}. Changes take effect in the next conversation.");
        }

        /// <summary>
        /// Return the current paths and existence status of both config files.
        /// </summary>
        private static ToolResponse HandleGetConfigPaths()
        {
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? "(unknown)";

            var projectMdPath = BootstrapLoader.FindUserFilePath("PROJECT.md");
            var projectMdDefault = BootstrapLoader.GetDefaultUserFilePath("PROJECT.md");
            var soulExtPath = BootstrapLoader.FindUserFilePath("SOUL.ext.md");
            var soulExtDefault = BootstrapLoader.GetDefaultUserFilePath("SOUL.ext.md");

            var data = new JObject
            {
                ["project_root"] = projectRoot,
                ["PROJECT.md"] = new JObject
                {
                    ["exists"] = projectMdPath != null,
                    ["current_path"] = projectMdPath ?? "(not found)",
                    ["default_path"] = projectMdDefault
                },
                ["SOUL.ext.md"] = new JObject
                {
                    ["exists"] = soulExtPath != null,
                    ["current_path"] = soulExtPath ?? "(not found)",
                    ["default_path"] = soulExtDefault
                }
            };

            return ToolResponse.OkWithData(data, "Workspace config file paths retrieved.");
        }

        /// <summary>
        /// Read a rules.md file from the specified layer (workspace or project).
        /// Returns the file content, or a template hint if the file does not exist yet.
        /// </summary>
        private static ToolResponse HandleReadRules(JObject parameters)
        {
            var layer = ToolHelpers.GetRequiredString(parameters, "layer").ToLowerInvariant();
            if (layer != "workspace" && layer != "project")
                return ToolResponse.Fail($"Invalid layer: '{layer}'. Must be 'workspace' or 'project'.");

            var filePath = layer == "workspace"
                ? RulesLoader.GetWorkspaceRulesPath()
                : RulesLoader.GetProjectRulesPath();

            if (filePath == null)
                return ToolResponse.Fail("Cannot determine rules file path.");

            if (!File.Exists(filePath))
            {
                var template = RulesLoader.GenerateRulesTemplate(layer);
                var notFoundData = new JObject
                {
                    ["layer"] = layer,
                    ["exists"] = false,
                    ["default_path"] = filePath,
                    ["template"] = template
                };
                return ToolResponse.OkWithData(notFoundData,
                    $"rules.md ({layer} layer) does not exist yet. Default path: {filePath}. Use write_rules to create it.");
            }

            var content = File.ReadAllText(filePath);
            var lineCount = content.Split('\n').Length;

            var data = new JObject
            {
                ["layer"] = layer,
                ["exists"] = true,
                ["path"] = filePath,
                ["line_count"] = lineCount,
                ["content"] = content
            };

            return ToolResponse.OkWithData(data, $"Read rules.md ({layer} layer, {lineCount} lines) from: {filePath}");
        }

        /// <summary>
        /// Write full content to a rules.md file in the specified layer (workspace or project).
        /// Creates the file and parent directory if they do not exist.
        /// </summary>
        private static ToolResponse HandleWriteRules(JObject parameters)
        {
            var layer = ToolHelpers.GetRequiredString(parameters, "layer").ToLowerInvariant();
            if (layer != "workspace" && layer != "project")
                return ToolResponse.Fail($"Invalid layer: '{layer}'. Must be 'workspace' or 'project'.");

            var content = ToolHelpers.GetRequiredString(parameters, "content");

            var filePath = layer == "workspace"
                ? RulesLoader.GetWorkspaceRulesPath()
                : RulesLoader.GetProjectRulesPath();

            if (filePath == null)
                return ToolResponse.Fail("Cannot determine rules file path.");

            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            bool isNew = !File.Exists(filePath);
            File.WriteAllText(filePath, content, new System.Text.UTF8Encoding(false));

            var lineCount = content.Split('\n').Length;
            var data = new JObject
            {
                ["layer"] = layer,
                ["path"] = filePath,
                ["lines_written"] = lineCount,
                ["created"] = isNew
            };

            var verb = isNew ? "Created" : "Updated";
            return ToolResponse.OkWithData(data,
                $"{verb} rules.md ({layer} layer, {lineCount} lines) at: {filePath}. Changes take effect in the next conversation.");
        }

        /// <summary>
        /// Return the current paths and existence status of both rules.md layers.
        /// </summary>
        private static ToolResponse HandleGetRulesPaths()
        {
            var workspaceContext = WorkspaceContextService.GetCurrent();
            var workspaceRulesPath = RulesLoader.GetWorkspaceRulesPath();
            var projectRulesPath = RulesLoader.GetProjectRulesPath();

            string workspaceRoot = "(unknown)";
            string unityRoot = "(unknown)";

            if (workspaceContext != null && workspaceContext.IsValid)
            {
                workspaceRoot = workspaceContext.WorkspaceRoot ?? workspaceRoot;
                unityRoot = workspaceContext.UnityRoot ?? unityRoot;
            }
            else
            {
                var parent = Directory.GetParent(Application.dataPath)?.FullName;
                if (parent != null)
                {
                    workspaceRoot = parent;
                    unityRoot = parent;
                }
            }

            var data = new JObject
            {
                ["workspace_root"] = workspaceRoot,
                ["unity_root"] = unityRoot,
                ["workspace_rules"] = new JObject
                {
                    ["layer"] = "workspace",
                    ["exists"] = workspaceRulesPath != null && File.Exists(workspaceRulesPath),
                    ["path"] = workspaceRulesPath ?? "(cannot determine)",
                    ["description"] = "Team-wide rules shared across all Unity projects in the workspace"
                },
                ["project_rules"] = new JObject
                {
                    ["layer"] = "project",
                    ["exists"] = projectRulesPath != null && File.Exists(projectRulesPath),
                    ["path"] = projectRulesPath ?? "(cannot determine)",
                    ["description"] = "Project-specific rules for this Unity project"
                }
            };

            return ToolResponse.OkWithData(data, "Rules file paths retrieved.");
        }
    }
}
