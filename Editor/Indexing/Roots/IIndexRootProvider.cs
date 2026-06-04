using System.Collections.Generic;
using AgentCore.Editor.Components.Indexing.Models;

namespace AgentCore.Editor.Components.Indexing.Roots
{
    /// <summary>
    /// 索引根发现提供者接口。
    /// 每个实现负责发现一类 IndexRoot（如 UnityRoot、WorkspaceChild、UserConfigured 等）。
    /// </summary>
    public interface IIndexRootProvider
    {
        /// <summary>提供者唯一标识（用于调试和覆盖）。</summary>
        string ProviderId { get; }

        /// <summary>
        /// 优先级（数值越小越先执行）。
        /// 高优先级 Provider 的结果可被低优先级 Provider 补充，但不会被覆盖。
        /// </summary>
        int Priority { get; }

        /// <summary>
        /// 发现并返回该 Provider 负责的 IndexRoot 列表。
        /// 实现应幂等，不应有副作用。
        /// </summary>
        /// <param name="workspace">当前 IndexWorkspace 上下文。</param>
        /// <returns>发现的 IndexRoot 列表（可为空列表，不应返回 null）。</returns>
        IReadOnlyList<IndexRoot> DiscoverRoots(IndexWorkspace workspace);
    }
}
