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
    /// 所有调用方拿到的都是 <see cref="IndexService"/> 持有的同一个共享句柄（写操作串行、Dispose 为空操作），
    /// 不再各自创建后端实例。真正的后端只由 <see cref="CreateBackend"/> 在 IndexService 内部创建一次。
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
        /// 获取当前 WorkspaceRoot 的共享索引存储。
        /// </summary>
        /// <returns>共享 IIndexStore 句柄（Dispose 为空操作，可照常 using）。若 WorkspaceRoot 无法解析则返回 null。</returns>
        public static IIndexStore CreateFromCurrent()
        {
            return IndexService.Instance.GetStoreForCurrent();
        }

        /// <summary>
        /// 获取指定 WorkspaceRoot 的共享索引存储。
        /// </summary>
        /// <param name="workspaceRoot">Workspace 根目录绝对路径。</param>
        /// <returns>共享 IIndexStore 句柄（Dispose 为空操作，可照常 using）。</returns>
        public static IIndexStore Create(string workspaceRoot)
        {
            return IndexService.Instance.GetStore(workspaceRoot);
        }

        /// <summary>
        /// 索引数据目录绝对路径。
        /// </summary>
        public static string GetIndexDir(string workspaceRoot)
        {
            return Path.GetFullPath(Path.Combine(workspaceRoot, IndexSubDir.Replace('/', Path.DirectorySeparatorChar)));
        }

        /// <summary>
        /// 创建真正的后端实例。仅供 <see cref="IndexService"/> 调用，其他调用方请使用 <see cref="Create"/>。
        /// 优先使用 SQLite 后端；失败时降级为 Jsonl 后端。
        /// </summary>
        internal static IIndexStore CreateBackend(string workspaceRoot)
        {
            if (string.IsNullOrEmpty(workspaceRoot))
                throw new ArgumentNullException(nameof(workspaceRoot));

            var indexDir = GetIndexDir(workspaceRoot);

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
