using System;
using System.IO;
using AgentCore.Editor.Components.Indexing.Models;
using AgentCore.Editor.Workspace;
using AgentCore.Editor.Utils;

namespace AgentCore.Editor.Components.Indexing.Core
{
    /// <summary>
    /// IIndexStore 工厂类。
    ///
    /// 默认使用 <see cref="JsonlIndexStore"/> 后端。
    /// 若定义了 AGENTCORE_SQLITE scripting define，则优先尝试 SQLite 后端，失败时降级为 JSONL。
    ///
    /// Jsonl 路径：{workspaceRoot}/.agentcore/index/
    /// SQLite 路径（仅 AGENTCORE_SQLITE）：{workspaceRoot}/.agentcore/index/codebase.db
    /// </summary>
    public static class IndexStoreFactory
    {
        // ── 常量 ────────────────────────────────────────────────────────────────

        /// <summary>索引数据目录（相对于 WorkspaceRoot）。</summary>
        public const string IndexSubDir = ".agentcore/index";

        /// <summary>SQLite 数据库文件名。</summary>
        public const string SqliteFileName = "codebase.db";

        // ── 公共 API ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 根据当前 WorkspaceRoot 创建 IIndexStore 实例。
        /// 优先使用 SQLite 后端；失败时降级为 Jsonl 后端。
        /// </summary>
        /// <returns>IIndexStore 实例，调用方负责 Dispose。若 WorkspaceRoot 无法解析则返回 null。</returns>
        public static IIndexStore CreateFromCurrent()
        {
            try
            {
                var workspace = IndexWorkspaceResolver.ResolveFromCurrent();
                if (string.IsNullOrEmpty(workspace?.WorkspaceRoot))
                    return null;

                return Create(workspace.WorkspaceRoot);
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[IndexStoreFactory] ResolveFromCurrent failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 根据指定 WorkspaceRoot 创建 IIndexStore 实例。
        /// 优先使用 SQLite 后端；失败时降级为 Jsonl 后端。
        /// </summary>
        /// <param name="workspaceRoot">Workspace 根目录绝对路径。</param>
        /// <returns>IIndexStore 实例，调用方负责 Dispose。</returns>
        public static IIndexStore Create(string workspaceRoot)
        {
            if (string.IsNullOrEmpty(workspaceRoot))
                throw new ArgumentNullException(nameof(workspaceRoot));

            var indexDir = Path.Combine(workspaceRoot, IndexSubDir.Replace('/', Path.DirectorySeparatorChar));

            // 确保目录存在
            try
            {
                Directory.CreateDirectory(indexDir);
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[IndexStoreFactory] Cannot create index dir '{indexDir}': {ex.Message}. Falling back to JsonlIndexStore.");
                return new JsonlIndexStore(indexDir);
            }

#if AGENTCORE_SQLITE
            // 尝试创建 SQLite 后端
            var dbPath = Path.Combine(indexDir, SqliteFileName);
            try
            {
                var store = new SqliteIndexStore(dbPath);
                AgentCoreLog.Info($"[IndexStoreFactory] Using SQLite backend: {dbPath}");
                return store;
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning(
                    $"[IndexStoreFactory] SQLite init failed ({ex.GetType().Name}: {ex.Message}). " +
                    $"Falling back to JsonlIndexStore at '{indexDir}'.");
                return new JsonlIndexStore(indexDir);
            }
#else
            // SQLite 后端未启用（需要 AGENTCORE_SQLITE define），使用 JSONL 后端
            return new JsonlIndexStore(indexDir);
#endif
        }

        /// <summary>
        /// 获取当前 Workspace 的 SQLite 数据库文件路径（不保证文件存在）。
        /// </summary>
        /// <param name="workspaceRoot">Workspace 根目录绝对路径。</param>
        /// <returns>数据库文件绝对路径。</returns>
        public static string GetDbPath(string workspaceRoot)
        {
            var indexDir = Path.Combine(workspaceRoot, IndexSubDir.Replace('/', Path.DirectorySeparatorChar));
            return Path.Combine(indexDir, SqliteFileName);
        }
    }
}
