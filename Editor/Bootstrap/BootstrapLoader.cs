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
    /// 1. SOUL.md — 内置角色定义（包内资源）
    /// 2. TOOLS.md — 工具使用指南（从模板生成）
    /// 3. PROJECT.md — 项目上下文（自动收集）
    /// 4. MEMORY.md — 本地知识文件（用户可编辑，优先项目根目录，其次 AgentCore/ 子目录）
    /// 5. USER.md — 用户偏好（用户可编辑，优先项目根目录，其次 AgentCore/ 子目录）
    /// </summary>
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

            // 1. SOUL.md — 内置角色定义
            context.Soul = LoadEmbeddedResource("SOUL.md");
            if (string.IsNullOrEmpty(context.Soul))
            {
                Debug.LogWarning("[AgentCore] SOUL.md not found, using default.");
                context.Soul = "你是一个 Unity 开发助手。请用中文回复。";
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

            // 4. MEMORY.md — 用户本地知识文件
            context.Memory = LoadUserFile("MEMORY.md");
            if (!string.IsNullOrEmpty(context.Memory))
            {
                Debug.Log($"[AgentCore] Loaded MEMORY.md ({context.Memory.Length} chars)");
            }

            // 5. USER.md — 用户偏好
            context.User = LoadUserFile("USER.md");
            if (!string.IsNullOrEmpty(context.User))
            {
                Debug.Log($"[AgentCore] Loaded USER.md ({context.User.Length} chars)");
            }

            var tokenEstimate = context.EstimateTokenCount();
            Debug.Log($"[AgentCore] Bootstrap loaded: ~{tokenEstimate} tokens " +
                      $"(SOUL={!string.IsNullOrEmpty(context.Soul)}, " +
                      $"TOOLS={!string.IsNullOrEmpty(context.Tools)}, " +
                      $"PROJECT={!string.IsNullOrEmpty(context.Project)}, " +
                      $"MEMORY={!string.IsNullOrEmpty(context.Memory)}, " +
                      $"USER={!string.IsNullOrEmpty(context.User)})");

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
        /// 如果 ToolRegistry 中没有注册任何工具（例如 Phase 1 兼容模式或初始化顺序问题），
        /// 占位符将被替换为"暂无可用工具"的提示。
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
        /// <para>
        /// 如果 ToolRegistry 为空（尚未注册任何工具），返回提示文本。
        /// 这确保了与 Phase 1 的向后兼容性，以及在 ToolRegistry 尚未初始化时的优雅降级。
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

            // 无工具注册时的降级处理
            if (allMetadata == null || allMetadata.Count == 0)
            {
                Debug.Log("[AgentCore] ToolRegistry is empty, using fallback tools list placeholder.");
                return "> 暂无已注册的可用工具。工具将在系统完全初始化后可用。";
            }

            var sb = new StringBuilder();

            // 按分类分组
            var grouped = allMetadata
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
                    // 截断过长的描述，避免表格过宽
                    var description = TruncateDescription(meta.Description, 80);
                    sb.AppendLine($"| `{meta.Name}` | {description} |");
                }

                sb.AppendLine();
            }

            var totalCount = allMetadata.Count;
            sb.AppendLine($"*共 {totalCount} 个可用工具*");

            Debug.Log($"[AgentCore] Generated active tools list: {totalCount} tools in {grouped.Count()} categories");

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
                // 原生工具分类（Phase 2.5）
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
                // 扩展分类（向后兼容）
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
                // 原生工具分类排序（Phase 2.5）
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
                // 扩展分类（向后兼容）
                "filesystem" => 31,
                "cloud" => 32,
                "custom" => 33,
                "default" => 99,
                _ => 50
            };
        }

        /// <summary>
        /// 截断工具描述文本，避免 Markdown 表格过宽。
        /// <para>
        /// 如果描述超过指定长度，将在最近的空格处截断并添加省略号。
        /// 同时移除描述中的换行符，确保表格格式正确。
        /// </para>
        /// </summary>
        /// <param name="description">原始描述文本</param>
        /// <param name="maxLength">最大字符数</param>
        /// <returns>截断后的描述文本</returns>
        private static string TruncateDescription(string description, int maxLength)
        {
            if (string.IsNullOrEmpty(description))
                return "(无描述)";

            // 移除换行符，确保表格格式正确
            var singleLine = description.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");

            // 移除 Markdown 表格中的管道符，避免破坏表格结构
            singleLine = singleLine.Replace("|", "\\|");

            if (singleLine.Length <= maxLength)
                return singleLine;

            // 在最近的空格处截断
            var truncated = singleLine.Substring(0, maxLength);
            var lastSpace = truncated.LastIndexOf(' ');
            if (lastSpace > maxLength / 2)
            {
                truncated = truncated.Substring(0, lastSpace);
            }

            return truncated + "…";
        }

        /// <summary>
        /// 加载用户可编辑的文件（MEMORY.md 或 USER.md）。
        /// 按优先级查找：
        /// 1. 项目根目录（Application.dataPath 的父目录）
        /// 2. 项目根目录下的 AgentCore/ 子目录
        /// </summary>
        private string LoadUserFile(string fileName)
        {
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (projectRoot == null) return null;

            // 优先查找项目根目录
            var rootPath = Path.Combine(projectRoot, fileName);
            // 其次查找 AgentCore/ 子目录
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
                // 跳过空文件或只有模板注释的文件
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
        /// <param name="fileName">文件名（如 MEMORY.md 或 USER.md）</param>
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
        /// <param name="fileName">文件名（如 MEMORY.md 或 USER.md）</param>
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
        /// 检查文件内容是否只包含模板注释（没有实际内容）。
        /// </summary>
        private bool IsTemplateOnly(string content)
        {
            var lines = content.Split('\n');
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                // 跳过空行、标题行、HTML 注释行
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
