using System;
using System.Collections.Generic;
using System.IO;
using AgentCore.Editor.Config;
using AgentCore.Editor.Workspace;
using UnityEngine;

namespace AgentCore.Editor.Bootstrap
{
    /// <summary>
    /// 规则文件加载器。
    /// 负责从两个层级加载 rules.md 文件，并按优先级合并为注入 System Prompt 的规则内容。
    ///
    /// 规则文件层级（优先级从低到高）：
    /// 层1 — WorkspaceRoot 层：{WorkspaceRoot}/.agentcore/rules.md
    ///       团队共享规则，建议提交到 VCS。适合跨 Unity 项目的团队约定。
    /// 层2 — UnityRoot 层：{UnityRoot}/AgentCore/rules.md
    ///       项目级规则，建议提交到 VCS。适合单个 Unity 项目的特定约定。
    ///
    /// 两层规则均存在时，全部注入（层1 在前，层2 在后），不互相覆盖。
    /// </summary>
    public class RulesLoader
    {
        /// <summary>
        /// 规则条目，包含来源层级和内容。
        /// </summary>
        public class RulesEntry
        {
            /// <summary>规则来源层级标识（"workspace" 或 "project"）</summary>
            public string Layer { get; set; }

            /// <summary>规则文件的完整路径</summary>
            public string FilePath { get; set; }

            /// <summary>规则文件内容</summary>
            public string Content { get; set; }
        }

        /// <summary>
        /// 加载所有层级的规则文件。
        /// 如果 rulesEnabled 为 false，返回空列表。
        /// </summary>
        /// <returns>按层级顺序排列的规则条目列表（层1 在前，层2 在后）</returns>
        public List<RulesEntry> Load()
        {
            var settings = AgentCoreSettings.instance;
            var result = new List<RulesEntry>();

            if (!settings.rulesEnabled)
            {
                Debug.Log("[AgentCore] Rules system disabled, skipping rules loading.");
                return result;
            }

            // 获取 Workspace 上下文
            var workspaceContext = WorkspaceContextService.GetCurrent();

            // 层1 — WorkspaceRoot 层
            var workspaceEntry = TryLoadWorkspaceRules(workspaceContext);
            if (workspaceEntry != null)
            {
                result.Add(workspaceEntry);
                Debug.Log($"[AgentCore] Loaded workspace rules ({workspaceEntry.Content.Length} chars) from: {workspaceEntry.FilePath}");
            }

            // 层2 — UnityRoot 层
            var projectEntry = TryLoadProjectRules(workspaceContext);
            if (projectEntry != null)
            {
                result.Add(projectEntry);
                Debug.Log($"[AgentCore] Loaded project rules ({projectEntry.Content.Length} chars) from: {projectEntry.FilePath}");
            }

            return result;
        }

        /// <summary>
        /// 尝试加载 WorkspaceRoot 层规则文件。
        /// 路径：{WorkspaceRoot}/.agentcore/rules.md
        /// </summary>
        private static RulesEntry TryLoadWorkspaceRules(WorkspaceContext workspaceContext)
        {
            string workspaceRoot = null;

            // 优先使用 WorkspaceContext 中的 WorkspaceRoot
            if (workspaceContext != null && workspaceContext.IsValid)
            {
                workspaceRoot = workspaceContext.WorkspaceRoot;
            }

            // 回退：使用 UnityRoot（即 Application.dataPath 的父目录）
            if (string.IsNullOrEmpty(workspaceRoot))
            {
                workspaceRoot = Directory.GetParent(Application.dataPath)?.FullName;
            }

            if (string.IsNullOrEmpty(workspaceRoot))
                return null;

            var filePath = Path.Combine(workspaceRoot, ".agentcore", "rules.md");
            return TryLoadRulesFile(filePath, "workspace");
        }

        /// <summary>
        /// 尝试加载 UnityRoot 层规则文件。
        /// 路径：{UnityRoot}/AgentCore/rules.md
        /// </summary>
        private static RulesEntry TryLoadProjectRules(WorkspaceContext workspaceContext)
        {
            string unityRoot = null;

            // 优先使用 WorkspaceContext 中的 UnityRoot
            if (workspaceContext != null && workspaceContext.IsValid)
            {
                unityRoot = workspaceContext.UnityRoot;
            }

            // 回退：使用 Application.dataPath 的父目录
            if (string.IsNullOrEmpty(unityRoot))
            {
                unityRoot = Directory.GetParent(Application.dataPath)?.FullName;
            }

            if (string.IsNullOrEmpty(unityRoot))
                return null;

            var filePath = Path.Combine(unityRoot, "AgentCore", "rules.md");
            return TryLoadRulesFile(filePath, "project");
        }

        /// <summary>
        /// 尝试从指定路径加载规则文件。
        /// 如果文件不存在或内容为空/仅模板，返回 null。
        /// </summary>
        private static RulesEntry TryLoadRulesFile(string filePath, string layer)
        {
            if (!File.Exists(filePath))
                return null;

            try
            {
                var content = File.ReadAllText(filePath);
                if (string.IsNullOrWhiteSpace(content) || IsTemplateOnly(content))
                    return null;

                return new RulesEntry
                {
                    Layer = layer,
                    FilePath = filePath,
                    Content = content
                };
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AgentCore] Failed to load rules file ({layer}): {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 检查文件内容是否只包含模板注释（没有实际内容）。
        /// </summary>
        private static bool IsTemplateOnly(string content)
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

        // ─── 静态辅助方法（供 Settings UI 和 Tool 调用）─────────────────────────

        /// <summary>
        /// 获取 WorkspaceRoot 层规则文件的路径。
        /// 如果文件存在，返回实际路径；否则返回默认路径（不保证存在）。
        /// </summary>
        public static string GetWorkspaceRulesPath()
        {
            var workspaceContext = WorkspaceContextService.GetCurrent();
            string workspaceRoot = null;

            if (workspaceContext != null && workspaceContext.IsValid)
                workspaceRoot = workspaceContext.WorkspaceRoot;

            if (string.IsNullOrEmpty(workspaceRoot))
                workspaceRoot = Directory.GetParent(Application.dataPath)?.FullName;

            if (string.IsNullOrEmpty(workspaceRoot))
                return null;

            return Path.Combine(workspaceRoot, ".agentcore", "rules.md");
        }

        /// <summary>
        /// 获取 UnityRoot 层规则文件的路径。
        /// 如果文件存在，返回实际路径；否则返回默认路径（不保证存在）。
        /// </summary>
        public static string GetProjectRulesPath()
        {
            var workspaceContext = WorkspaceContextService.GetCurrent();
            string unityRoot = null;

            if (workspaceContext != null && workspaceContext.IsValid)
                unityRoot = workspaceContext.UnityRoot;

            if (string.IsNullOrEmpty(unityRoot))
                unityRoot = Directory.GetParent(Application.dataPath)?.FullName;

            if (string.IsNullOrEmpty(unityRoot))
                return null;

            return Path.Combine(unityRoot, "AgentCore", "rules.md");
        }

        /// <summary>
        /// 生成 rules.md 的初始模板内容。
        /// </summary>
        /// <param name="layer">层级标识（"workspace" 或 "project"）</param>
        /// <returns>模板内容字符串</returns>
        public static string GenerateRulesTemplate(string layer)
        {
            if (layer == "workspace")
            {
                return
                    "# Workspace Rules\n" +
                    "<!--\n" +
                    "  此文件定义跨项目的团队规则，适用于整个 Workspace（VCS 工作副本）。\n" +
                    "  建议提交到 VCS（Git/SVN/Perforce）以便团队共享。\n" +
                    "  路径：{WorkspaceRoot}/.agentcore/rules.md\n\n" +
                    "  适合放在这里的内容：\n" +
                    "  - 跨项目的团队编码规范\n" +
                    "  - 团队工作流约定（如 PR 流程、分支命名）\n" +
                    "  - 禁止使用的 API 或框架（团队级别）\n" +
                    "  - 安全与合规要求\n\n" +
                    "  示例：\n" +
                    "  - 所有代码必须通过 Code Review 后才能合并\n" +
                    "  - 禁止在代码中硬编码 IP 地址或密钥\n" +
                    "  - 提交信息必须包含 Jira 任务编号\n" +
                    "-->\n\n";
            }

            // layer == "project"
            return
                "# Project Rules\n" +
                "<!--\n" +
                "  此文件定义当前 Unity 项目的特定规则。\n" +
                "  建议提交到 VCS（Git/SVN/Perforce）以便团队共享。\n" +
                "  路径：{UnityRoot}/AgentCore/rules.md\n\n" +
                "  适合放在这里的内容：\n" +
                "  - 项目特定的架构约定\n" +
                "  - 禁止使用的 Unity API 或包（项目级别）\n" +
                "  - 项目特定的命名规范\n" +
                "  - 性能预算约束\n\n" +
                "  示例：\n" +
                "  - 本项目使用 Addressables，禁止使用 Resources.Load\n" +
                "  - 所有 MonoBehaviour 必须放在 Assets/Scripts/ 目录下\n" +
                "  - 禁止在 Update() 中使用 GetComponent\n" +
                "-->\n\n";
        }
    }
}
