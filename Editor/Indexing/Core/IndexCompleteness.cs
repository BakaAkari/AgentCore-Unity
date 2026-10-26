using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Components.Indexing.Models;
using AgentCore.Editor.Utils;

namespace AgentCore.Editor.Components.Indexing.Core
{
    /// <summary>
    /// 当前索引的完整性快照（search_code 的 index_warning 与 diagnose 使用）。
    /// 查询 0 结果时用它区分"确实不存在"与"索引没覆盖到"，避免误导模型。
    /// </summary>
    public sealed class IndexCompleteness
    {
        public int TotalFiles;
        public int TotalSymbols;
        public DateTime? LastFullIndexAt;
        public List<string> NotReadyRoots = new List<string>();
        public bool BackgroundRunning;
        public string HealthWarning;

        /// <summary>索引是否可能缺数据（存储为空 / 无全量 / 有已启用 root 非 Ready / 后台正在跑 / 存储损坏）。</summary>
        public bool Incomplete =>
            TotalSymbols == 0 || !LastFullIndexAt.HasValue || NotReadyRoots.Count > 0 || BackgroundRunning || HealthWarning != null;

        /// <summary>人类可读的索引状态说明（给模型看）。</summary>
        public string Describe()
        {
            var parts = new List<string>();
            if (HealthWarning != null) parts.Add(HealthWarning);
            if (TotalSymbols == 0)
                parts.Add($"索引存储为空（{TotalFiles} 个文件，0 个符号）");
            if (!LastFullIndexAt.HasValue)
                parts.Add("尚未完成过全量索引（last_full_index_at 缺失），结果可能只覆盖部分 root");
            if (NotReadyRoots.Count > 0)
                parts.Add($"{NotReadyRoots.Count} 个已启用 root 非 Ready（{string.Join("，", NotReadyRoots.Take(5))}{(NotReadyRoots.Count > 5 ? " 等" : string.Empty)}）");
            if (BackgroundRunning)
                parts.Add("后台索引正在运行，新改动可能尚未入库");
            if (parts.Count == 0)
                return $"索引完整（{TotalFiles} 个文件，{TotalSymbols} 个符号，全量索引于 {LastFullIndexAt:O}）";
            return "索引不完整：" + string.Join("；", parts) + "。可调用 search_code::diagnose 查看详情";
        }

        /// <summary>
        /// 0 结果或索引不完整时返回状态说明；结果非空且索引完整时只返回存储健康警告（通常为 null）。
        /// </summary>
        public string WarningFor(int resultCount)
        {
            return resultCount == 0 || Incomplete ? Describe() : HealthWarning;
        }

        /// <summary>存储健康警告（有损坏行 / 需要重建时返回说明，健康时为 null）。</summary>
        public static string GetHealthWarning(IIndexStore store)
        {
            if (store is IIndexHealth health && (health.RequiresRebuild || health.CorruptLineCount > 0))
                return health.HealthMessage ?? $"索引存储有 {health.CorruptLineCount} 行损坏，结果可能不完整。";
            return null;
        }

        /// <summary>
        /// 读取索引完整性：统计、last_full_index_at、已启用 root 的状态、存储健康。尽力而为，读取失败的项按缺失处理。
        /// </summary>
        /// <param name="backgroundRunning">后台索引是否正在运行（由调用方从 IndexingStatusBus 读取）。</param>
        public static async Task<IndexCompleteness> ReadAsync(IIndexStore store, int workspaceId, bool backgroundRunning, CancellationToken ct = default)
        {
            var result = new IndexCompleteness
            {
                HealthWarning = GetHealthWarning(store),
                BackgroundRunning = backgroundRunning,
            };

            try
            {
                var stats = await store.GetStatsAsync(workspaceId, ct);
                result.TotalFiles = stats?.TotalFiles ?? 0;
                result.TotalSymbols = stats?.TotalSymbols ?? 0;

                // A 组可能新增"全量进行中"标记；这里只认 last_full_index_at，缺失即视为无全量
                var timestamps = await IndexRunMetadata.ReadAsync(store, workspaceId, ct);
                result.LastFullIndexAt = timestamps?.LastFullIndexAt;

                var stateStore = new IndexRootStateStore(store, workspaceId);
                foreach (var root in await store.GetRootsAsync(workspaceId, ct))
                {
                    if (root == null || !root.IsEnabled) continue;
                    var status = await stateStore.LoadAsync(root.Id, ct);
                    if (status.State != IndexRootState.Ready)
                        result.NotReadyRoots.Add($"{root.DisplayName ?? root.RootPath}: {status.State}");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore] index completeness read failed: {ex.Message}");
            }

            return result;
        }
    }
}
