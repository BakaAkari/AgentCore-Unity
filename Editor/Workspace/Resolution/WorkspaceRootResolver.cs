using AgentCore.Editor.Config;
using UnityEngine;

namespace AgentCore.Editor.Workspace.Resolution
{
    /// <summary>
    /// 从 UnityRoot 向上识别 AgentCore WorkspaceRoot。
    /// 优先级：
    ///   1. 用户 Settings 显式配置的 WorkspaceRootOverride
    ///   2. svn info 解析的 Working Copy Root Path
    ///   3. .svn 目录向上探测（SVN 命令不可用时的 fallback）
    ///   4. 回退到 UnityRoot，标记 FallbackToUnityRoot
    /// </summary>
    public static class WorkspaceRootResolver
    {
        /// <summary>
        /// 解析 WorkspaceRoot 及相关 VCS 元数据。
        /// </summary>
        /// <param name="unityRoot">规范化正斜杠的 UnityRoot 绝对路径。</param>
        /// <returns>
        /// (workspaceRoot, status, vcsInfo)：
        ///   workspaceRoot — 规范化正斜杠的 WorkspaceRoot 绝对路径；
        ///   status        — 解析状态；
        ///   vcsInfo       — SVN 元数据（可能为降级状态）。
        /// </returns>
        public static (string workspaceRoot, WorkspaceResolutionStatus status, WorkspaceVcsInfo vcsInfo)
            Resolve(string unityRoot)
        {
            if (string.IsNullOrEmpty(unityRoot))
            {
                var empty = new WorkspaceVcsInfo { Type = WorkspaceVcsType.None, ErrorMessage = "UnityRoot is null or empty" };
                return (null, WorkspaceResolutionStatus.Error, empty);
            }

            // ADR-17 极简: workspaceRootOverride 字段已删除, 只做自动检测
            //   若未来需要企业用户手动覆盖, 通过环境变量 AGENTCORE_WORKSPACE_ROOT 传递

            // 优先级 1: SVN 解析(svn info 或 .svn 探测)
            var svnInfo = SvnWorkspaceInfoResolver.Resolve(unityRoot);

            if (!string.IsNullOrEmpty(svnInfo.RootPath))
            {
                return (svnInfo.RootPath, WorkspaceResolutionStatus.Resolved, svnInfo);
            }

            // 优先级 4：回退到 UnityRoot
            AgentCore.Editor.Utils.AgentCoreLog.Info("[AgentCore] WorkspaceRootResolver: no SVN working copy found, falling back to UnityRoot.");
            var fallbackVcs = new WorkspaceVcsInfo
            {
                Type = WorkspaceVcsType.None,
                IsCommandAvailable = svnInfo.IsCommandAvailable,
                ErrorMessage = svnInfo.ErrorMessage ?? "No SVN working copy detected"
            };
            return (unityRoot, WorkspaceResolutionStatus.FallbackToUnityRoot, fallbackVcs);
        }
    }
}
