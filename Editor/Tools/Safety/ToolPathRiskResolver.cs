using System;
using System.Collections.Generic;
using System.IO;
using AgentCore.Editor.Tools.Infrastructure;
using AgentCore.Editor.Workspace;
using AgentCore.Editor.Workspace.Safety;
using Newtonsoft.Json.Linq;

namespace AgentCore.Editor.Tools.Safety
{
    /// <summary>
    /// 从工具调用参数中嗅探路径，解析对应的 <see cref="WorkspaceOperationRisk"/>。
    /// <para>
    /// G.1.e — 将 WorkspacePathPolicy 真正接入 ToolRiskPolicy 评估链。
    /// 逻辑：提取所有路径类参数 → 解析为绝对路径 → 查找 WorkspaceRootInfo → 
    /// 通过 WorkspacePathPolicy.GetRisk() 获取风险等级 → 返回最坏情况。
    /// </para>
    /// </summary>
    public static class ToolPathRiskResolver
    {
        /// <summary>
        /// 已知的路径参数名（小写）。
        /// 工具参数中出现这些 key 时，会被视为文件/目录路径进行风险评估。
        /// </summary>
        private static readonly HashSet<string> PathParameterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "path",
            "file_path",
            "filepath",
            "source_path",
            "destination",
            "destination_path",
            "target_path",
            "directory",
            "folder_path",
            "script_path",
            "asset_path",
            "scene_path",
            "prefab_path",
            "output_path",
            "source",
            "target",
        };

        /// <summary>
        /// 需要写入能力才进行路径风险评估的能力掩码。
        /// 如果工具不具备这些能力中的任何一项，则路径风险直接返回 Safe（只读操作无需路径风险升级）。
        /// </summary>
        private const ToolCapability WriteMask =
            ToolCapability.WriteProjectFiles |
            ToolCapability.DeleteProjectFiles |
            ToolCapability.ModifyScripts |
            ToolCapability.ModifyAssets;

        /// <summary>
        /// 解析工具参数中的路径风险。
        /// </summary>
        /// <param name="parameters">工具调用的 JSON 参数对象。</param>
        /// <param name="metadata">工具元数据（用于判断是否具备写能力）。</param>
        /// <param name="resolvedTargets">输出：所有成功解析到 WorkspaceRoot 的路径列表（用于日志/确认对话框）。</param>
        /// <returns>所有路径中的最高风险等级；无路径或上下文不可用时返回 <see cref="WorkspaceOperationRisk.Safe"/>。</returns>
        public static WorkspaceOperationRisk Resolve(
            JObject parameters,
            ToolMetadata metadata,
            out List<string> resolvedTargets)
        {
            resolvedTargets = null;

            // 快速路径：工具不具备写入能力 → 路径风险无意义
            if (metadata == null || (metadata.Capabilities & WriteMask) == 0)
                return WorkspaceOperationRisk.Safe;

            if (parameters == null || !parameters.HasValues)
                return WorkspaceOperationRisk.Safe;

            // 检查 WorkspaceContext 是否可用
            var ctx = WorkspaceContextService.GetCurrent();
            if (ctx == null)
                return WorkspaceOperationRisk.Safe;

            var worstRisk = WorkspaceOperationRisk.Safe;
            List<string> targets = null;

            foreach (var property in parameters.Properties())
            {
                if (!PathParameterNames.Contains(property.Name))
                    continue;

                var pathValue = ExtractPathString(property.Value);
                if (string.IsNullOrWhiteSpace(pathValue))
                    continue;

                var absolutePath = ResolveToAbsolute(pathValue, ctx);
                if (string.IsNullOrEmpty(absolutePath))
                    continue;

                var rootInfo = WorkspacePathService.TryGetRootInfo(absolutePath);
                if (rootInfo == null)
                    continue;

                var risk = WorkspacePathPolicy.GetRisk(rootInfo.Role);

                if (targets == null)
                    targets = new List<string>();
                targets.Add(pathValue);

                if (risk > worstRisk)
                    worstRisk = risk;
            }

            resolvedTargets = targets;
            return worstRisk;
        }

        /// <summary>
        /// 不需要 targets 输出的简化重载。
        /// </summary>
        public static WorkspaceOperationRisk Resolve(JObject parameters, ToolMetadata metadata)
        {
            return Resolve(parameters, metadata, out _);
        }

        // ------------------------------------------------------------------
        // 内部辅助
        // ------------------------------------------------------------------

        /// <summary>
        /// 从 JToken 中提取字符串路径值。支持简单字符串和数组中的第一个字符串。
        /// </summary>
        private static string ExtractPathString(JToken token)
        {
            if (token == null) return null;

            switch (token.Type)
            {
                case JTokenType.String:
                    return token.Value<string>();

                case JTokenType.Array:
                {
                    // 对数组取第一个字符串元素作为代表（保守策略：后续可改为取最坏）
                    var arr = (JArray)token;
                    foreach (var item in arr)
                    {
                        if (item.Type == JTokenType.String)
                            return item.Value<string>();
                    }
                    return null;
                }

                default:
                    return null;
            }
        }

        /// <summary>
        /// 将工具参数中的路径解析为绝对路径。
        /// 支持：绝对路径直通、Assets/ 开头的 Unity 路径、相对路径（基于 WorkspaceRoot）。
        /// </summary>
        private static string ResolveToAbsolute(string pathValue, WorkspaceContext ctx)
        {
            // 已经是绝对路径
            if (Path.IsPathRooted(pathValue))
                return pathValue;

            // Unity Asset 路径（Assets/... 或 Packages/...）
            if (pathValue.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                pathValue.StartsWith("Assets\\", StringComparison.OrdinalIgnoreCase) ||
                pathValue.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase) ||
                pathValue.StartsWith("Packages\\", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(ctx.UnityRoot))
                    return Path.Combine(ctx.UnityRoot, pathValue);
            }

            // 普通相对路径 — 基于 WorkspaceRoot
            if (!string.IsNullOrEmpty(ctx.WorkspaceRoot))
                return Path.Combine(ctx.WorkspaceRoot, pathValue);

            return null;
        }
    }
}
