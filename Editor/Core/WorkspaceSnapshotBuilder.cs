using System;
using System.Text;
using AgentCore.Editor.Tools;
using AgentCore.Editor.Tools.Infrastructure;
using AgentCore.Editor.Workspace;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AgentCore.Editor.Core
{
    /// <summary>
    /// 构建 Workspace 运行时快照，用于会话首轮注入。
    /// <para>
    /// 快照包含当前 Editor 运行时动态状态（活跃场景、编译状态、Selection、VCS 分支等），
    /// 与 Bootstrap 中的静态 PROJECT 信息互补，消除 Agent 首轮"冷启动"盲目探索。
    /// </para>
    /// </summary>
    public static class WorkspaceSnapshotBuilder
    {
        /// <summary>
        /// 快照标记前缀，用于后续识别和移除。
        /// </summary>
        public const string SnapshotMarker = "[WORKSPACE_SNAPSHOT]";

        /// <summary>
        /// 快照标记后缀。
        /// </summary>
        public const string SnapshotMarkerEnd = "[/WORKSPACE_SNAPSHOT]";

        /// <summary>
        /// 构建当前 Workspace 运行时快照字符串。
        /// 仅包含动态信息（与 Bootstrap PROJECT section 静态信息不重复）。
        /// </summary>
        /// <returns>格式化的快照字符串，可直接注入为 system 消息内容。</returns>
        public static string Build()
        {
            var sb = new StringBuilder();
            sb.AppendLine(SnapshotMarker);

            try
            {
                // 1. 活跃场景
                var activeScene = EditorSceneManager.GetActiveScene();
                var sceneName = activeScene.IsValid() ? activeScene.name : "(none)";
                var scenePath = activeScene.IsValid() ? activeScene.path : "";
                sb.AppendLine($"活跃场景: {sceneName}" + (string.IsNullOrEmpty(scenePath) ? "" : $" ({scenePath})"));

                // 2. 编译状态
                sb.AppendLine($"编译状态: {GetCompilationStatus()}");

                // 3. Play Mode 状态
                if (EditorApplication.isPlaying)
                {
                    sb.AppendLine("Play Mode: 运行中");
                }

                // 4. 当前选中对象
                var selection = GetSelectionSummary();
                if (!string.IsNullOrEmpty(selection))
                {
                    sb.AppendLine($"当前选中: {selection}");
                }

                // 5. VCS 分支（如有）
                var branch = GetVcsBranch();
                if (!string.IsNullOrEmpty(branch))
                {
                    sb.AppendLine($"VCS 分支: {branch}");
                }

                // 6. 工具可用性摘要
                sb.AppendLine($"可用工具: {GetToolSummary()}");

                // 7. v1.4.0 — Optional component contributions (Indexing 组件在启用时通过
                //   WorkspaceSnapshotHooks.IndexStatusBlockProvider 注入 "Index Status" 块；
                //   组件未编译时 provider 为 null，snapshot 中不出现该块)
                try
                {
                    var indexProvider = WorkspaceSnapshotHooks.IndexStatusBlockProvider;
                    if (indexProvider != null)
                    {
                        var indexBlock = indexProvider();
                        if (!string.IsNullOrEmpty(indexBlock))
                        {
                            sb.AppendLine();
                            sb.AppendLine(indexBlock);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[AgentCore] IndexStatusBlockProvider failed: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"(快照部分收集失败: {ex.Message})");
                Debug.LogWarning($"[AgentCore] WorkspaceSnapshotBuilder error: {ex.Message}");
            }

            sb.Append(SnapshotMarkerEnd);
            return sb.ToString();
        }

        /// <summary>
        /// 获取当前编译状态的简要描述。
        /// </summary>
        private static string GetCompilationStatus()
        {
            if (EditorApplication.isCompiling)
                return "编译中";

            // 检查是否有编译错误（通过 Unity 内部 LogEntry 计数）
            int errorCount = 0;
            int warningCount = 0;

            try
            {
                // 使用 CompilationPipeline 的 assembly 状态判断
                var assemblies = UnityEditor.Compilation.CompilationPipeline.GetAssemblies();
                if (assemblies != null && assemblies.Length > 0)
                {
                    // 如果能获取到 assemblies 且不在编译中，通常说明编译已完成
                    // 检查 Console 中的错误数
                    LogEntryCount(out errorCount, out warningCount);
                }
            }
            catch
            {
                // 静默降级
            }

            if (errorCount > 0)
                return $"有错误 ({errorCount} errors, {warningCount} warnings)";

            if (warningCount > 0)
                return $"通过 ({warningCount} warnings)";

            return "通过";
        }

        /// <summary>
        /// 通过反射读取 Unity Console 的错误/警告计数。
        /// </summary>
        private static void LogEntryCount(out int errors, out int warnings)
        {
            errors = 0;
            warnings = 0;

            try
            {
                var logEntriesType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.LogEntries");
                if (logEntriesType == null) return;

                // GetCountsByType 返回 void，通过 out 参数获取计数
                var getCountMethod = logEntriesType.GetMethod("GetCountsByType",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);

                if (getCountMethod != null)
                {
                    var parameters = new object[] { 0, 0, 0 };
                    getCountMethod.Invoke(null, parameters);
                    errors = (int)parameters[0];
                    warnings = (int)parameters[1];
                }
            }
            catch
            {
                // 反射失败静默降级
            }
        }

        /// <summary>
        /// 获取当前 Selection 的简要描述。
        /// </summary>
        private static string GetSelectionSummary()
        {
            var activeGo = Selection.activeGameObject;
            if (activeGo != null)
            {
                var path = GetGameObjectPath(activeGo);
                return $"GameObject \"{path}\"";
            }

            var activeObj = Selection.activeObject;
            if (activeObj != null)
            {
                var assetPath = AssetDatabase.GetAssetPath(activeObj);
                if (!string.IsNullOrEmpty(assetPath))
                    return $"{activeObj.GetType().Name} \"{assetPath}\"";
                return $"{activeObj.GetType().Name} \"{activeObj.name}\"";
            }

            return null;
        }

        /// <summary>
        /// 获取 VCS 分支名（通过 WorkspaceContextService）。
        /// </summary>
        private static string GetVcsBranch()
        {
            try
            {
                var ctx = WorkspaceContextService.GetCurrent();
                if (ctx != null && ctx.IsValid && ctx.Vcs != null &&
                    ctx.Vcs.Type != WorkspaceVcsType.None &&
                    !string.IsNullOrEmpty(ctx.Vcs.BranchId))
                {
                    return ctx.Vcs.BranchId;
                }
            }
            catch
            {
                // 静默降级
            }

            return null;
        }

        /// <summary>
        /// 获取工具可用性摘要。
        /// </summary>
        private static string GetToolSummary()
        {
            try
            {
                var registry = ToolRegistry.Instance;
                int totalCount = registry.Count;
                var allTools = registry.GetAllTools();

                int alwaysVisible = 0;
                int onDemand = 0;

                foreach (var tool in allTools)
                {
                    if (tool.Metadata.Visibility == ToolVisibility.AlwaysVisible)
                        alwaysVisible++;
                    else if (tool.Metadata.Visibility == ToolVisibility.OnDemand)
                        onDemand++;
                }

                return $"{totalCount} 个 ({alwaysVisible} 始终可用, {onDemand} 按需激活)";
            }
            catch
            {
                return "(信息不可用)";
            }
        }

        /// <summary>
        /// 获取 GameObject 的层级路径。
        /// </summary>
        private static string GetGameObjectPath(GameObject go)
        {
            var path = go.name;
            var parent = go.transform.parent;
            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }
    }
}
