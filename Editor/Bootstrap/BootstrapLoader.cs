using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AgentCore.Editor.Config;
using AgentCore.Editor.Tools;
using UnityEngine;

namespace AgentCore.Editor.Bootstrap
{
    /// <summary>
    /// Bootstrap Files 加载器。
    /// 负责加载所有 Bootstrap 文件并编译为完整的 System Prompt。
    ///
    /// 加载顺序：
    /// 1. SOUL.md — 内置角色定义（包内资源，不可变）
    /// 1+. SOUL.ext.md — 用户行为规则扩展（可选，追加到 SOUL）
    /// 2. TOOLS.md — 工具使用指南（从模板生成）
    /// 3. PROJECT.md — 项目上下文（自动收集）
    /// 3+. PROJECT.md（用户） — 项目约定与个人偏好（用户可编辑，建议 VCS 提交）
    /// </summary>
    // ReSharper disable once ClassNeverInstantiated.Global
    public class BootstrapLoader
    {
        /// <summary>
        /// 内置资源文件的目录路径（相对于包根目录）。
        /// </summary>
        private static readonly string ResourcesPath = Path.Combine(
            "Packages", "com.agentcore.unity", "Editor", "Bootstrap", "Resources");

        /// <summary>
        /// 加载所有 Bootstrap Files 并返回上下文对象。
        /// </summary>
        public BootstrapContext Load()
        {
            var settings = AgentCoreSettings.instance;
            var context = new BootstrapContext();

            if (!settings.bootstrapEnabled)
            {
                Debug.Log("[AgentCore] Bootstrap Files disabled, using minimal system prompt.");
                context.Soul = "你是一个 Unity 开发助手。请用中文回复。";
                return context;
            }

            // 1. SOUL.md — 内置角色定义（不可变）
            context.Soul = LoadEmbeddedResource("SOUL.md");
            if (string.IsNullOrEmpty(context.Soul))
            {
                Debug.LogWarning("[AgentCore] SOUL.md not found, using default.");
                context.Soul = "你是一个 Unity 开发助手。请用中文回复。";
            }

            // 1+. SOUL.ext.md — 用户行为规则扩展（可选，追加到 SOUL）
            context.SoulExtension = LoadUserFile("SOUL.ext.md");
            if (!string.IsNullOrEmpty(context.SoulExtension))
            {
                Debug.Log($"[AgentCore] Loaded SOUL.ext.md ({context.SoulExtension.Length} chars)");
            }

            // 2. TOOLS.md — 从模板生成
            context.Tools = LoadToolsGuide();

            // 3. PROJECT.md — 自动收集项目信息
            if (settings.autoProjectContext)
            {
                try
                {
                    context.Project = ProjectContextCollector.Collect();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[AgentCore] Failed to collect project context: {ex.Message}");
                    context.Project = "(项目信息收集失败)";
                }
            }

            // 3+. PROJECT.md（用户） — 项目约定与个人偏好
            context.Workspace = LoadUserFile("PROJECT.md");
            if (!string.IsNullOrEmpty(context.Workspace))
            {
                Debug.Log($"[AgentCore] Loaded PROJECT.md ({context.Workspace.Length} chars)");
            }

            var tokenEstimate = context.EstimateTokenCount();
            Debug.Log($"[AgentCore] Bootstrap loaded: ~{tokenEstimate} tokens " +
                      $"(SOUL={!string.IsNullOrEmpty(context.Soul)}, " +
                      $"SOUL.ext={!string.IsNullOrEmpty(context.SoulExtension)}, " +
                      $"TOOLS={!string.IsNullOrEmpty(context.Tools)}, " +
                      $"PROJECT={!string.IsNullOrEmpty(context.Project)}, " +
                      $"WORKSPACE={!string.IsNullOrEmpty(context.Workspace)})");

            return context;
        }

        /// <summary>
        /// 加载包内嵌入的资源文件。
        /// </summary>
        private string LoadEmbeddedResource(string fileName)
        {
            // 方式 1：通过文件系统直接读取（UPM 包内文件）
            var packagePath = Path.GetFullPath(Path.Combine(ResourcesPath, fileName));
            if (File.Exists(packagePath))
            {
                return File.ReadAllText(packagePath);
            }

            // 方式 2：尝试相对于 Application.dataPath 的路径
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (projectRoot != null)
            {
                var altPath = Path.Combine(projectRoot, ResourcesPath, fileName);
                if (File.Exists(altPath))
                {
                    return File.ReadAllText(altPath);
                }
            }

            Debug.LogWarning($"[AgentCore] Embedded resource not found: {fileName}");
            return null;
        }

        /// <summary>
        /// 加载工具使用指南（从模板生成）。
        /// <para>
        /// 加载 TOOLS.md.template 模板文件，并将 <c>{{ACTIVE_TOOLS_LIST}}</c> 占位符
        /// 替换为 <see cref="ToolRegistry"/> 中实际注册的工具列表。
        /// </para>
        /// <para>
        /// 工具列表按分类分组，以 Markdown 表格形式呈现。
        /// 如果 ToolRegistry 中没有注册任何工具，占位符将被替换为"暂无可用工具"的提示。
        /// </para>
        /// </summary>
        private string LoadToolsGuide()
        {
            var template = LoadEmbeddedResource("TOOLS.md.template");
            if (string.IsNullOrEmpty(template))
            {
                return null;
            }

            // 从 ToolRegistry 动态生成工具列表
            var toolsList = GenerateActiveToolsList();
            template = template.Replace("{{ACTIVE_TOOLS_LIST}}", toolsList);

            return template;
        }

        /// <summary>
        /// 从 <see cref="ToolRegistry"/> 动态生成可用工具列表的 Markdown 文本。
        /// <para>
        /// 工具按 <see cref="ToolMetadata.Category"/> 分组，每组以 Markdown 表格形式展示，
        /// 包含工具名称和描述。分类名称作为三级标题显示。
        /// </para>
        /// </summary>
        /// <returns>格式化的工具列表 Markdown 文本</returns>
        private string GenerateActiveToolsList()
        {
            IReadOnlyList<ToolMetadata> allMetadata;

            try
            {
                allMetadata = ToolRegistry.Instance.GetAllToolMetadata();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AgentCore] Failed to get tool metadata from ToolRegistry: {ex.Message}");
                return "> [WARNING] 工具列表获取失败，请检查 ToolRegistry 初始化状态。";
            }

            if (allMetadata == null || allMetadata.Count == 0)
            {
                Debug.Log("[AgentCore] ToolRegistry is empty, using fallback tools list placeholder.");
                return "> 暂无已注册的可用工具。工具将在系统完全初始化后可用。";
            }

            // 过滤掉被禁用的工具
            var settings = AgentCoreSettings.instance;
            var enabledMetadata = allMetadata
                .Where(m => !settings.IsToolDisabled(m.Name, m.Category))
                .ToList();
            var disabledCount = allMetadata.Count - enabledMetadata.Count;

            var sb = new StringBuilder();

            // 按分类分组（仅启用的工具）
            var grouped = enabledMetadata
                .GroupBy(m => m.Category ?? "default")
                .OrderBy(g => GetCategorySortOrder(g.Key))
                .ThenBy(g => g.Key);

            foreach (var group in grouped)
            {
                var categoryName = GetCategoryDisplayName(group.Key);
                var toolCount = group.Count();

                sb.AppendLine($"### {categoryName}（{toolCount} 个）");
                sb.AppendLine();
                sb.AppendLine("| 工具名称 | 描述 |");
                sb.AppendLine("|---------|------|");

                foreach (var meta in group.OrderBy(m => m.Name))
                {
                    var description = TruncateDescription(meta.Description, 80);
                    sb.AppendLine($"| `{meta.Name}` | {description} |");
                }

                sb.AppendLine();
            }

            var totalCount = enabledMetadata.Count;
            if (disabledCount > 0)
            {
                sb.AppendLine($"*共 {totalCount} 个可用工具（{disabledCount} 个已禁用）*");
            }
            else
            {
                sb.AppendLine($"*共 {totalCount} 个可用工具*");
            }

            Debug.Log($"[AgentCore] Generated active tools list: {totalCount} tools in {grouped.Count()} categories" +
                      (disabledCount > 0 ? $" ({disabledCount} disabled)" : ""));

            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// 获取分类的显示名称。
        /// 将内部分类标识符转换为用户友好的中文名称。
        /// </summary>
        /// <param name="category">分类标识符</param>
        /// <returns>分类的显示名称</returns>
        private static string GetCategoryDisplayName(string category)
        {
            return category switch
            {
                "Scene" => "场景管理",
                "GameObject" => "游戏对象操作",
                "Script" => "脚本管理",
                "Asset" => "资源管理",
                "Editor" => "编辑器操作",
                "Physics" => "物理系统",
                "Graphics" => "图形与渲染",
                "UI" => "用户界面",
                "Audio" => "音频系统",
                "Animation" => "动画系统",
                "Lighting" => "光照系统",
                "Build" => "构建管理",
                "Profiler" => "性能分析",
                "Navigation" => "导航系统",
                "Input" => "输入系统",
                "Material" => "材质管理",
                "Shader" => "着色器管理",
                "Prefab" => "预制体管理",
                "Meta" => "元操作",
                "Utility" => "实用工具",
                "General" => "通用工具",
                "filesystem" => "文件操作",
                "cloud" => "云端服务",
                "custom" => "自定义工具",
                "default" => "其他工具",
                _ => category
            };
        }

        /// <summary>
        /// 获取分类的排序权重。
        /// 数值越小排序越靠前，确保重要分类优先展示。
        /// </summary>
        /// <param name="category">分类标识符</param>
        /// <returns>排序权重值</returns>
        private static int GetCategorySortOrder(string category)
        {
            return category switch
            {
                "Scene" => 0,
                "GameObject" => 1,
                "Script" => 2,
                "Prefab" => 3,
                "Asset" => 4,
                "Material" => 5,
                "Shader" => 6,
                "Editor" => 7,
                "Meta" => 8,
                "Physics" => 10,
                "Graphics" => 11,
                "Lighting" => 12,
                "UI" => 13,
                "Audio" => 14,
                "Animation" => 15,
                "Navigation" => 16,
                "Input" => 17,
                "Build" => 18,
                "Profiler" => 19,
                "Utility" => 20,
                "General" => 21,
                "filesystem" => 31,
                "cloud" => 32,
                "custom" => 33,
                "default" => 99,
                _ => 50
            };
        }

        /// <summary>
        /// 截断工具描述文本，避免 Markdown 表格过宽。
        /// </summary>
        /// <param name="description">原始描述文本</param>
        /// <param name="maxLength">最大字符数</param>
        /// <returns>截断后的描述文本</returns>
        private static string TruncateDescription(string description, int maxLength)
        {
            if (string.IsNullOrEmpty(description))
                return "(无描述)";

            var singleLine = description.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");
            singleLine = singleLine.Replace("|", "\\|");

            if (singleLine.Length <= maxLength)
                return singleLine;

            var truncated = singleLine.Substring(0, maxLength);
            var lastSpace = truncated.LastIndexOf(' ');
            if (lastSpace > maxLength / 2)
            {
                truncated = truncated.Substring(0, lastSpace);
            }

            return truncated + "…";
        }

        /// <summary>
        /// 加载用户可编辑的文件（PROJECT.md、SOUL.ext.md 等）。
        /// 按优先级查找：
        /// 1. 项目根目录（Application.dataPath 的父目录）
        /// 2. 项目根目录下的 AgentCore/ 子目录
        /// </summary>
        private string LoadUserFile(string fileName)
        {
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (projectRoot == null) return null;

            var rootPath = Path.Combine(projectRoot, fileName);
            var agentCorePath = Path.Combine(projectRoot, "AgentCore", fileName);

            string filePath = null;
            if (File.Exists(rootPath))
            {
                filePath = rootPath;
            }
            else if (File.Exists(agentCorePath))
            {
                filePath = agentCorePath;
            }

            if (filePath == null)
            {
                return null;
            }

            try
            {
                var content = File.ReadAllText(filePath);
                if (string.IsNullOrWhiteSpace(content) || IsTemplateOnly(content))
                {
                    return null;
                }
                return content;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AgentCore] Failed to load {fileName}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 查找用户文件的实际路径。
        /// 按优先级查找：项目根目录 → AgentCore/ 子目录。
        /// 如果文件不存在，返回 null。
        /// </summary>
        /// <param name="fileName">文件名（如 PROJECT.md 或 SOUL.ext.md）</param>
        /// <returns>文件的完整路径，或 null（如果不存在）</returns>
        public static string FindUserFilePath(string fileName)
        {
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (projectRoot == null) return null;

            var rootPath = Path.Combine(projectRoot, fileName);
            if (File.Exists(rootPath)) return rootPath;

            var agentCorePath = Path.Combine(projectRoot, "AgentCore", fileName);
            if (File.Exists(agentCorePath)) return agentCorePath;

            return null;
        }

        /// <summary>
        /// 获取用户文件的默认创建路径（项目根目录下的 AgentCore/ 子目录）。
        /// 如果文件已存在，返回已存在的路径；否则返回默认路径。
        /// </summary>
        /// <param name="fileName">文件名（如 PROJECT.md 或 SOUL.ext.md）</param>
        /// <returns>文件的完整路径</returns>
        public static string GetDefaultUserFilePath(string fileName)
        {
            var existing = FindUserFilePath(fileName);
            if (existing != null) return existing;

            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (projectRoot == null) return null;

            return Path.Combine(projectRoot, "AgentCore", fileName);
        }

        /// <summary>
        /// 生成用户文件的初始模板内容。
        /// 集中管理所有用户可编辑文件的模板，供 Settings UI 调用。
        /// </summary>
        /// <param name="fileName">文件名（PROJECT.md 或 SOUL.ext.md）</param>
        /// <returns>模板内容字符串</returns>
        public static string GenerateUserFileTemplate(string fileName)
        {
            if (fileName == "PROJECT.md")
            {
                return
                    "# AgentCore Project Configuration\n" +
                    "<!--\n" +
                    "  此文件由 AgentCore 生成，供团队维护。\n" +
                    "  建议提交到 VCS（Git/SVN/Perforce）以便团队共享。\n" +
                    "-->\n\n" +
                    "## Project Conventions\n" +
                    "<!--\n" +
                    "  团队约定：命名规范、架构决策、禁止事项、工作流程等。\n" +
                    "  这里的内容会注入到每次对话的 System Prompt 中。\n\n" +
                    "  示例：\n" +
                    "  - 本项目使用 Mirror 网络框架，禁止使用 UNET\n" +
                    "  - 资源管理使用 Addressables，禁止使用 Resources.Load\n" +
                    "  - 命名规范：类名 PascalCase，私有字段 _camelCase\n" +
                    "-->\n\n\n" +
                    "## Personal Preferences\n" +
                    "<!--\n" +
                    "  个人偏好：回复语言、代码风格偏好、工作习惯等。\n" +
                    "  建议不提交到 VCS（在 .gitignore 中排除此 section 或整个文件）。\n\n" +
                    "  示例：\n" +
                    "  - 请用英文回复\n" +
                    "  - 代码注释使用中文\n" +
                    "  - 每次修改前先展示 diff\n" +
                    "-->\n\n";
            }

            if (fileName == "SOUL.ext.md")
            {
                return
                    "# SOUL.ext.md — Agent 行为规则扩展\n" +
                    "<!--\n" +
                    "  此文件追加到内置 SOUL.md 之后，不替换内置规则。\n" +
                    "  适合添加项目特定的 Agent 行为约束。\n" +
                    "  建议提交到 VCS（团队共享的行为规则扩展）。\n\n" +
                    "  适合放在这里的内容：\n" +
                    "  - 追加新的 Unity Hard Rules（如禁止使用特定 API）\n" +
                    "  - 追加工具使用约束\n" +
                    "  - 追加项目特定的格式约束\n\n" +
                    "  不适合放在这里的内容（请放 PROJECT.md）：\n" +
                    "  - 项目技术栈约定\n" +
                    "  - 个人偏好\n" +
                    "-->\n\n";
            }

            return string.Empty;
        }

        /// <summary>
        /// 检查文件内容是否只包含模板注释（没有实际内容）。
        /// </summary>
        private bool IsTemplateOnly(string content)
        {
            var lines = content.Split('\n');
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;
                if (trimmed.StartsWith("#")) continue;
                if (trimmed.StartsWith("<!--")) continue;
                if (trimmed.StartsWith("-->")) continue;
                if (trimmed.StartsWith(">")) continue;

                // 找到非注释/非标题的实际内容
                return false;
            }
            return true;
        }
    }
}
