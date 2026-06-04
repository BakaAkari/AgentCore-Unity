using System;
using System.Collections.Generic;

namespace AgentCore.Editor.Workspace
{
    /// <summary>
    /// AgentCore Workspace 上下文快照。
    /// 由 WorkspaceContextService 生成并缓存，供所有模块消费。
    /// 包含 WorkspaceRoot、UnityRoot、VCS 元数据、Scope Roots 和 Fingerprint。
    /// </summary>
    [Serializable]
    public sealed class WorkspaceContext
    {
        /// <summary>
        /// Workspace 根目录绝对路径（规范化正斜杠）。
        /// SVN 场景下为 SVN 工作副本根；无 SVN 时 fallback 到 UnityRoot。
        /// </summary>
        public string WorkspaceRoot { get; set; }

        /// <summary>
        /// Unity 工程根目录绝对路径（规范化正斜杠）。
        /// 即包含 Assets/ 目录的目录。
        /// </summary>
        public string UnityRoot { get; set; }

        /// <summary>
        /// UnityRoot 相对于 WorkspaceRoot 的路径（规范化正斜杠）。
        /// 例如 "unity"、"project/unity"。
        /// FallbackToUnityRoot 时为空字符串 ""。
        /// </summary>
        public string UnityRootRelativePath { get; set; }

        /// <summary>
        /// Workspace 指纹（SHA256 短 hash，16 位十六进制）。
        /// 用于后续 Session/Memory/RAG/Indexing 数据库隔离。
        /// 输入变化（WorkspaceRoot、BranchId、Scope 配置等）时 hash 变化。
        /// </summary>
        public string Fingerprint { get; set; }

        /// <summary>VCS 元数据快照。</summary>
        public WorkspaceVcsInfo Vcs { get; set; }

        /// <summary>
        /// WorkspaceRoot 下所有已发现（含已禁用）的子根列表。
        /// 消费方应过滤 IsEnabled = true 的条目。
        /// </summary>
        public List<WorkspaceRootInfo> Roots { get; set; } = new List<WorkspaceRootInfo>();

        /// <summary>解析状态。</summary>
        public WorkspaceResolutionStatus Status { get; set; }

        /// <summary>解析失败或降级时的错误/警告信息。</summary>
        public string ErrorMessage { get; set; }

        /// <summary>上下文生成时间（UTC）。</summary>
        public DateTime ResolvedAt { get; set; }

        /// <summary>
        /// 获取所有已启用的子根列表（IsEnabled = true）。
        /// </summary>
        public IReadOnlyList<WorkspaceRootInfo> EnabledRoots
        {
            get
            {
                var result = new List<WorkspaceRootInfo>();
                if (Roots == null) return result;
                foreach (var root in Roots)
                {
                    if (root.IsEnabled)
                        result.Add(root);
                }
                return result;
            }
        }

        /// <summary>
        /// 是否为有效的 Workspace 上下文（Status 不为 Error 且 WorkspaceRoot 非空）。
        /// </summary>
        public bool IsValid =>
            Status != WorkspaceResolutionStatus.Error &&
            Status != WorkspaceResolutionStatus.NotResolved &&
            !string.IsNullOrEmpty(WorkspaceRoot);

        /// <summary>
        /// 创建一个表示解析失败的 WorkspaceContext。
        /// </summary>
        public static WorkspaceContext CreateError(string errorMessage) => new WorkspaceContext
        {
            Status = WorkspaceResolutionStatus.Error,
            ErrorMessage = errorMessage,
            ResolvedAt = DateTime.UtcNow
        };
    }
}
