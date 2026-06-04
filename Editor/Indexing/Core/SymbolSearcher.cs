using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Components.Indexing.Models;
using AgentCore.Editor.Components.Indexing.Query;

namespace AgentCore.Editor.Components.Indexing.Core
{
    /// <summary>
    /// 符号搜索器，提供对已建立索引的代码库的多维度查询能力。
    ///
    /// 支持的查询维度：
    ///   - 符号名称（精确 / 包含 / 正则）
    ///   - 符号类型（class / method / property 等）
    ///   - 命名空间前缀
    ///   - Scope 类型 / Scope 名称
    ///   - Root 角色 / Root ID
    ///   - 只读标志
    ///
    /// 线程安全：所有公共方法均为 async，可在任意线程调用。
    /// </summary>
    public sealed class SymbolSearcher
    {
        // ── 常量 ────────────────────────────────────────────────────────────────

        /// <summary>单次查询最大返回结果数。</summary>
        public const int MaxResultLimit = 200;

        /// <summary>默认返回结果数。</summary>
        public const int DefaultLimit = 50;

        // ── 字段 ────────────────────────────────────────────────────────────────

        private readonly IIndexStore _store;

        // ── 构造函数 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 创建符号搜索器。
        /// </summary>
        /// <param name="store">索引存储后端（调用方负责生命周期管理）。</param>
        public SymbolSearcher(IIndexStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        // ── 公共 API ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 按查询条件搜索符号。
        /// </summary>
        /// <param name="query">查询参数。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>匹配的符号列表（已按相关性排序）。</returns>
        public async Task<IReadOnlyList<SymbolInfo>> SearchAsync(
            SearchQuery query,
            CancellationToken ct = default)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));

            // 规范化 limit
            query.Limit = Math.Min(Math.Max(1, query.Limit), MaxResultLimit);

            var workspace = IndexWorkspaceResolver.ResolveFromCurrent();
            var stored = await _store.GetWorkspaceByFingerprintAsync(workspace.Fingerprint, ct);
            if (stored == null)
                return Array.Empty<SymbolInfo>();

            var results = await _store.SearchSymbolsAsync(stored.Id, query, ct);
            return results;
        }

        /// <summary>
        /// 按文件路径获取该文件的所有符号。
        /// </summary>
        /// <param name="filePath">文件绝对路径（或相对于 WorkspaceRoot 的路径）。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>该文件的符号列表。</returns>
        public async Task<IReadOnlyList<SymbolInfo>> GetSymbolsByFileAsync(
            string filePath,
            CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(filePath))
                return Array.Empty<SymbolInfo>();

            var workspace = IndexWorkspaceResolver.ResolveFromCurrent();
            var stored = await _store.GetWorkspaceByFingerprintAsync(workspace.Fingerprint, ct);
            if (stored == null)
                return Array.Empty<SymbolInfo>();

            // 规范化路径
            var normalizedPath = NormalizePath(filePath, workspace.WorkspaceRoot);

            var file = await _store.GetFileByPathAsync(stored.Id, normalizedPath, ct);
            if (file == null)
                return Array.Empty<SymbolInfo>();

            return await _store.GetSymbolsByFileAsync(file.Id, ct);
        }

        /// <summary>
        /// 列出当前 workspace 下的所有命名空间。
        /// </summary>
        /// <param name="scopeType">可选 Scope 类型过滤。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>命名空间条目列表（按命名空间名称排序）。</returns>
        public async Task<IReadOnlyList<NamespaceEntry>> ListNamespacesAsync(
            IndexScopeType? scopeType = null,
            CancellationToken ct = default)
        {
            var workspace = IndexWorkspaceResolver.ResolveFromCurrent();
            var stored = await _store.GetWorkspaceByFingerprintAsync(workspace.Fingerprint, ct);
            if (stored == null)
                return Array.Empty<NamespaceEntry>();

            return await _store.GetNamespacesAsync(stored.Id, scopeType, ct);
        }

        /// <summary>
        /// 获取当前 workspace 的索引统计信息。
        /// </summary>
        /// <param name="ct">取消令牌。</param>
        /// <returns>统计信息，如果尚未建立索引则返回 null。</returns>
        public async Task<IndexingStats> GetStatsAsync(CancellationToken ct = default)
        {
            var workspace = IndexWorkspaceResolver.ResolveFromCurrent();
            var stored = await _store.GetWorkspaceByFingerprintAsync(workspace.Fingerprint, ct);
            if (stored == null)
                return null;

            return await _store.GetStatsAsync(stored.Id, ct);
        }

        /// <summary>
        /// 获取当前 workspace 下所有已索引的 Root 列表。
        /// </summary>
        /// <param name="ct">取消令牌。</param>
        /// <returns>Root 列表。</returns>
        public async Task<IReadOnlyList<IndexRoot>> GetRootsAsync(CancellationToken ct = default)
        {
            var workspace = IndexWorkspaceResolver.ResolveFromCurrent();
            var stored = await _store.GetWorkspaceByFingerprintAsync(workspace.Fingerprint, ct);
            if (stored == null)
                return Array.Empty<IndexRoot>();

            return await _store.GetRootsAsync(stored.Id, ct);
        }

        /// <summary>
        /// 获取指定 Root 下的所有已索引文件列表。
        /// </summary>
        /// <param name="rootId">Root 数据库 ID。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>文件列表。</returns>
        public async Task<IReadOnlyList<IndexedFile>> GetFilesForRootAsync(
            int rootId,
            CancellationToken ct = default)
        {
            return await _store.GetFilesForRootAsync(rootId, ct);
        }

        /// <summary>
        /// 按完全限定名精确查找符号。
        /// </summary>
        /// <param name="fullName">完全限定名（如 "MyNamespace.MyClass.MyMethod"）。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>匹配的符号列表（可能有多个，如 partial class）。</returns>
        public async Task<IReadOnlyList<SymbolInfo>> FindByFullNameAsync(
            string fullName,
            CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(fullName))
                return Array.Empty<SymbolInfo>();

            var query = new SearchQuery
            {
                Query = fullName,
                Fuzzy = false,
                Regex = false,
                Limit = MaxResultLimit,
            };

            var results = await SearchAsync(query, ct);

            // 精确匹配 FullName
            return results
                .Where(s => string.Equals(s.FullName, fullName, StringComparison.Ordinal))
                .ToList();
        }

        /// <summary>
        /// 按符号名称（不含命名空间）搜索，支持模糊匹配。
        /// </summary>
        /// <param name="name">符号名称（部分匹配）。</param>
        /// <param name="symbolType">可选符号类型过滤。</param>
        /// <param name="limit">返回数量上限。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>匹配的符号列表。</returns>
        public async Task<IReadOnlyList<SymbolInfo>> FindByNameAsync(
            string name,
            string symbolType = null,
            int limit = DefaultLimit,
            CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(name))
                return Array.Empty<SymbolInfo>();

            var query = new SearchQuery
            {
                Query = name,
                SymbolType = symbolType,
                Fuzzy = true,
                Limit = Math.Min(limit, MaxResultLimit),
            };

            return await SearchAsync(query, ct);
        }

        /// <summary>
        /// 列出指定命名空间下的所有直接成员符号（类型级别）。
        /// </summary>
        /// <param name="namespaceName">命名空间全名（精确匹配）。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>该命名空间下的类型符号列表（class / interface / struct / enum / delegate）。</returns>
        public async Task<IReadOnlyList<SymbolInfo>> ListTypesInNamespaceAsync(
            string namespaceName,
            CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(namespaceName))
                return Array.Empty<SymbolInfo>();

            var query = new SearchQuery
            {
                Namespace = namespaceName,
                Fuzzy = false,
                Limit = MaxResultLimit,
            };

            var results = await SearchAsync(query, ct);

            // 只返回类型级别符号
            var typeSymbolTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "class", "interface", "struct", "enum", "delegate"
            };

            return results
                .Where(s => typeSymbolTypes.Contains(s.SymbolType ?? string.Empty) &&
                            string.Equals(s.Namespace, namespaceName, StringComparison.Ordinal))
                .ToList();
        }

        /// <summary>
        /// 列出指定类型的所有成员符号（方法、属性、字段等）。
        /// </summary>
        /// <param name="typeFullName">类型完全限定名（如 "MyNamespace.MyClass"）。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>该类型的成员符号列表。</returns>
        public async Task<IReadOnlyList<SymbolInfo>> ListMembersOfTypeAsync(
            string typeFullName,
            CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(typeFullName))
                return Array.Empty<SymbolInfo>();

            var workspace = IndexWorkspaceResolver.ResolveFromCurrent();
            var stored = await _store.GetWorkspaceByFingerprintAsync(workspace.Fingerprint, ct);
            if (stored == null)
                return Array.Empty<SymbolInfo>();

            // 先找到类型所在文件
            var typeQuery = new SearchQuery
            {
                Query = typeFullName,
                Fuzzy = false,
                Limit = 10,
            };
            var typeResults = await _store.SearchSymbolsAsync(stored.Id, typeQuery, ct);
            var typeSymbol = typeResults.FirstOrDefault(s =>
                string.Equals(s.FullName, typeFullName, StringComparison.Ordinal) &&
                IsTypeSymbol(s.SymbolType));

            if (typeSymbol == null)
                return Array.Empty<SymbolInfo>();

            // 获取同文件的所有符号，过滤出该类型的成员
            var fileSymbols = await _store.GetSymbolsByFileAsync(typeSymbol.FileId, ct);

            // 成员符号：非类型级别，且 FullName 以 typeFullName. 开头
            var memberPrefix = typeFullName + ".";
            return fileSymbols
                .Where(s => !IsTypeSymbol(s.SymbolType) &&
                            (s.FullName?.StartsWith(memberPrefix, StringComparison.Ordinal) == true))
                .ToList();
        }

        /// <summary>
        /// 检查索引是否已建立（存在 workspace 记录）。
        /// </summary>
        /// <param name="ct">取消令牌。</param>
        /// <returns>true 表示已建立索引。</returns>
        public async Task<bool> IsIndexAvailableAsync(CancellationToken ct = default)
        {
            try
            {
                var workspace = IndexWorkspaceResolver.ResolveFromCurrent();
                var stored = await _store.GetWorkspaceByFingerprintAsync(workspace.Fingerprint, ct);
                return stored != null;
            }
            catch
            {
                return false;
            }
        }

        // ── 私有方法 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 规范化文件路径：如果是相对路径，则相对于 WorkspaceRoot 解析为绝对路径。
        /// </summary>
        private static string NormalizePath(string filePath, string workspaceRoot)
        {
            if (string.IsNullOrEmpty(filePath))
                return filePath;

            // 已是绝对路径
            if (Path.IsPathRooted(filePath))
                return filePath.Replace('\\', '/');

            // 相对路径：相对于 WorkspaceRoot
            if (!string.IsNullOrEmpty(workspaceRoot))
            {
                var combined = Path.Combine(workspaceRoot, filePath);
                return combined.Replace('\\', '/');
            }

            return filePath.Replace('\\', '/');
        }

        /// <summary>
        /// 判断符号类型是否为类型级别（class / interface / struct / enum / delegate）。
        /// </summary>
        private static bool IsTypeSymbol(string symbolType)
        {
            if (string.IsNullOrEmpty(symbolType)) return false;
            switch (symbolType.ToLowerInvariant())
            {
                case "class":
                case "interface":
                case "struct":
                case "enum":
                case "delegate":
                    return true;
                default:
                    return false;
            }
        }
    }
}
