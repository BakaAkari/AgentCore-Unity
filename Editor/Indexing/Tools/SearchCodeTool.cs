using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Components.Indexing.Core;
using AgentCore.Editor.Components.Indexing.Models;
using AgentCore.Editor.Components.Indexing.Query;
using AgentCore.Editor.Components.Indexing.Roots;
using AgentCore.Editor.Tools;
using AgentCore.Editor.Tools.Infrastructure;
using Newtonsoft.Json.Linq;

namespace AgentCore.Editor.Components.Indexing.Tools
{
    /// <summary>
    /// 代码库索引与符号搜索工具。
    ///
    /// 支持的 action：
    ///   resolve_workspace  — 获取当前 WorkspaceRoot、UnityRoot、fingerprint、VCS、root 摘要
    ///   list_roots         — 列出当前索引根目录和 Scope
    ///   index_full         — 全量索引所有 enabled roots
    ///   index_scope        — 只索引指定 Scope 或 Root
    ///   index_incremental  — 增量索引
    ///   search_symbol      — 按符号搜索，支持 Scope 过滤
    ///   list_namespaces    — 按 Scope 列出命名空间
    ///   get_file_symbols   — 获取某文件内的符号
    ///   get_stats          — 获取索引统计
    ///   clear_index        — 清除当前 Workspace 的索引
    /// </summary>
    [AgentTool("search_code",
        Description = "代码库索引与符号搜索工具。支持解析 SVN WorkspaceRoot、全量/增量索引 C# 符号、按名称/类型/命名空间/Scope 搜索符号、全文搜索（FTS5）、依赖图查询（get_dependencies/find_usages）、符号上下文聚合（get_symbol_context）、列出命名空间、获取文件符号列表和索引统计。索引数据本地存储（SQLite），不提交 VCS。",
        Category = "Indexing",
        Visibility = ToolVisibility.OnDemand,
        RequiresMainThread = false,
        MayModifyScripts = false)]
    public sealed class SearchCodeTool : IAgentTool
    {
        // ── Schema ──────────────────────────────────────────────────────────────

        private static readonly JObject _parametersSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""action"": {
      ""type"": ""string"",
      ""enum"": [""resolve_workspace"", ""list_roots"", ""index_full"", ""index_scope"", ""index_incremental"", ""search_symbol"", ""search_text"", ""list_namespaces"", ""get_file_symbols"", ""get_stats"", ""clear_index"", ""get_dependencies"", ""find_usages"", ""get_symbol_context"", ""get_backend_info""],
      ""description"": ""要执行的操作""
    },
    ""query"": {
      ""type"": ""string"",
      ""description"": ""[search_symbol / search_text] 搜索关键词（符号名称或完全限定名的一部分）""
    },
    ""symbol_type"": {
      ""type"": ""string"",
      ""enum"": [""class"", ""interface"", ""struct"", ""enum"", ""method"", ""property"", ""field"", ""event"", ""constructor"", ""delegate""],
      ""description"": ""[search_symbol] 符号类型过滤（不填则不过滤）""
    },
    ""scope_type"": {
      ""type"": ""string"",
      ""enum"": [""Project"", ""Map"", ""Mode"", ""Package"", ""Shared"", ""UI"", ""Localization"", ""Engine"", ""Plugin"", ""Tools"", ""Generated"", ""Unknown""],
      ""description"": ""[search_symbol / list_namespaces / index_scope] Scope 类型过滤""
    },
    ""scope_name"": {
      ""type"": ""string"",
      ""description"": ""[search_symbol / index_scope] Scope 名称过滤（大小写不敏感）""
    },
    ""root_id"": {
      ""type"": ""integer"",
      ""description"": ""[search_symbol / index_scope] Root ID 过滤（0 表示不过滤）""
    },
    ""role"": {
      ""type"": ""string"",
      ""enum"": [""EditableProjectCode"", ""SharedCode"", ""WorkspacePackage"", ""CommercialPlugin"", ""CustomPlugin"", ""EngineCode"", ""ToolingCode"", ""GeneratedCode"", ""ReadOnlyReference""],
      ""description"": ""[search_symbol] Root 角色过滤""
    },
    ""include_plugins"": {
      ""type"": ""boolean"",
      ""description"": ""[search_symbol] 是否包含 Plugin 类型的 Root（默认 false）""
    },
    ""include_engine"": {
      ""type"": ""boolean"",
      ""description"": ""[search_symbol] 是否包含 Engine 类型的 Root（默认 true）""
    },
    ""include_generated"": {
      ""type"": ""boolean"",
      ""description"": ""[search_symbol] 是否包含 Generated 类型的 Root（默认 false）""
    },
    ""read_only"": {
      ""type"": ""boolean"",
      ""description"": ""[search_symbol] 只读过滤（不填则不过滤）""
    },
    ""fuzzy"": {
      ""type"": ""boolean"",
      ""description"": ""[search_symbol] 是否启用模糊匹配（默认 true）""
    },
    ""regex"": {
      ""type"": ""boolean"",
      ""description"": ""[search_symbol] 是否使用正则表达式匹配（默认 false）""
    },
    ""limit"": {
      ""type"": ""integer"",
      ""description"": ""[search_symbol / search_text / get_dependencies / find_usages] 返回结果数量上限（默认 50，最大 200）""
    },
    ""namespace"": {
      ""type"": ""string"",
      ""description"": ""[search_symbol] 命名空间前缀过滤""
    },
    ""file_path"": {
      ""type"": ""string"",
      ""description"": ""[get_file_symbols / get_dependencies] 文件绝对路径或相对于 WorkspaceRoot 的路径""
    },
    ""symbol_id"": {
      ""type"": ""integer"",
      ""description"": ""[get_dependencies / get_symbol_context] 符号数据库 ID（来自 search_symbol 结果）""
    },
    ""type_name"": {
      ""type"": ""string"",
      ""description"": ""[find_usages] 要查找被引用的类型名称（简名或全名均可）""
    },
    ""full_name"": {
      ""type"": ""string"",
      ""description"": ""[get_symbol_context] 符号完全限定名（如 'MyNamespace.MyClass.MyMethod'）""
    },
    ""include_dependencies"": {
      ""type"": ""boolean"",
      ""description"": ""[get_symbol_context] 是否包含该符号的依赖关系（默认 true）""
    },
    ""include_usages"": {
      ""type"": ""boolean"",
      ""description"": ""[get_symbol_context] 是否包含引用该符号的其他符号（默认 true）""
    }
  },
  ""required"": [""action""]
}");

        // ── Metadata ─────────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public ToolMetadata Metadata => new ToolMetadata(
            name: "search_code",
            description: "代码库索引与符号搜索工具。支持解析 SVN WorkspaceRoot、全量/增量索引 C# 符号、按名称/类型/命名空间/Scope 搜索符号、全文搜索（FTS5）、依赖图查询（get_dependencies/find_usages）、符号上下文聚合（get_symbol_context）、列出命名空间、获取文件符号列表和索引统计。索引数据本地存储（SQLite），不提交 VCS。",
            category: "Indexing",
            parametersSchema: _parametersSchema,
            requiresMainThread: false
        );

        // ── ExecuteAsync ─────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public async Task<ToolResult> ExecuteAsync(
            JObject parameters,
            CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();
            ToolResponse response;

            try
            {
                var action = ToolHelpers.GetRequiredString(parameters, "action").ToLowerInvariant();

                switch (action)
                {
                    case "resolve_workspace":
                        response = HandleResolveWorkspace();
                        break;

                    case "list_roots":
                        response = await HandleListRootsAsync(cancellationToken);
                        break;

                    case "index_full":
                        response = await HandleIndexFullAsync(cancellationToken);
                        break;

                    case "index_scope":
                        response = await HandleIndexScopeAsync(parameters, cancellationToken);
                        break;

                    case "index_incremental":
                        response = await HandleIndexIncrementalAsync(cancellationToken);
                        break;

                    case "search_symbol":
                        response = await HandleSearchSymbolAsync(parameters, cancellationToken);
                        break;

                    case "list_namespaces":
                        response = await HandleListNamespacesAsync(parameters, cancellationToken);
                        break;

                    case "get_file_symbols":
                        response = await HandleGetFileSymbolsAsync(parameters, cancellationToken);
                        break;

                    case "get_stats":
                        response = await HandleGetStatsAsync(cancellationToken);
                        break;

                    case "clear_index":
                        response = await HandleClearIndexAsync(cancellationToken);
                        break;

                    case "search_text":
                        response = await HandleSearchTextAsync(parameters, cancellationToken);
                        break;

                    case "get_dependencies":
                        response = await HandleGetDependenciesAsync(parameters, cancellationToken);
                        break;

                    case "find_usages":
                        response = await HandleFindUsagesAsync(parameters, cancellationToken);
                        break;

                    case "get_symbol_context":
                        response = await HandleGetSymbolContextAsync(parameters, cancellationToken);
                        break;

                    case "get_backend_info":
                        response = await HandleGetBackendInfoAsync(cancellationToken);
                        break;

                    default:
                        response = ToolResponse.Fail(
                            $"Unknown action: '{action}'. Valid actions: resolve_workspace, list_roots, index_full, index_scope, index_incremental, search_symbol, search_text, list_namespaces, get_file_symbols, get_stats, clear_index, get_dependencies, find_usages, get_symbol_context, get_backend_info");
                        break;
                }
            }
            catch (Exception ex)
            {
                response = ToolResponse.Fail($"search_code error: {ex.Message}");
            }

            sw.Stop();
            return response.ToToolResult(sw.Elapsed.TotalMilliseconds);
        }

        // ── Action Handlers ──────────────────────────────────────────────────────

        /// <summary>
        /// resolve_workspace — 获取当前 WorkspaceRoot、UnityRoot、fingerprint、VCS、root 摘要。
        /// </summary>
        private static ToolResponse HandleResolveWorkspace()
        {
            try
            {
                var workspace = IndexWorkspaceResolver.ResolveFromCurrent();
                var rootResolver = new IndexRootResolver();
                var roots = rootResolver.Resolve(workspace);

                var rootSummaries = roots.Select(r => new
                {
                    id = r.Id,
                    root_path = r.RootPath,
                    display_name = r.DisplayName,
                    scope_type = r.ScopeType.ToString(),
                    scope_name = r.ScopeName,
                    role = r.Role.ToString(),
                    read_only = r.ReadOnly,
                    is_enabled = r.IsEnabled,
                    is_default_search_scope = r.IsDefaultSearchScope,
                    provider_id = r.ProviderId,
                }).ToList();

                return ToolResponse.OkWithData(new
                {
                    fingerprint = workspace.Fingerprint,
                    workspace_root = workspace.WorkspaceRoot,
                    unity_root = workspace.WorkspaceRoot, // WorkspaceRoot 包含 UnityRoot
                    display_name = workspace.DisplayName,
                    vcs_type = workspace.VcsType,
                    vcs_root_path = workspace.VcsRootPath,
                    vcs_url = workspace.VcsUrl,
                    repository_root = workspace.RepositoryRoot,
                    branch_id = workspace.BranchId,
                    revision = workspace.Revision,
                    discovered_roots_count = roots.Count,
                    enabled_roots_count = roots.Count(r => r.IsEnabled),
                    roots = rootSummaries,
                }, $"Workspace 解析成功，发现 {roots.Count} 个 Root（{roots.Count(r => r.IsEnabled)} 个已启用）");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Workspace 解析失败: {ex.Message}");
            }
        }

        /// <summary>
        /// list_roots — 列出当前已索引的 Root 列表（来自存储）。
        /// </summary>
        private static async Task<ToolResponse> HandleListRootsAsync(CancellationToken ct)
        {
            using var store = CreateStore();
            if (store == null)
                return ToolResponse.Fail("索引存储初始化失败，请检查 WorkspaceRoot 配置。");

            var searcher = new SymbolSearcher(store);
            var roots = await searcher.GetRootsAsync(ct);

            if (roots.Count == 0)
            {
                // 降级：从 Provider 动态发现
                var workspace = IndexWorkspaceResolver.ResolveFromCurrent();
                var rootResolver = new IndexRootResolver();
                var discovered = rootResolver.Resolve(workspace);

                return ToolResponse.OkWithData(new
                {
                    source = "discovered",
                    note = "尚未建立索引，以下为动态发现的 Root（未持久化）",
                    roots = discovered.Select(r => new
                    {
                        root_path = r.RootPath,
                        display_name = r.DisplayName,
                        scope_type = r.ScopeType.ToString(),
                        scope_name = r.ScopeName,
                        role = r.Role.ToString(),
                        read_only = r.ReadOnly,
                        is_enabled = r.IsEnabled,
                        provider_id = r.ProviderId,
                    }).ToList(),
                }, $"发现 {discovered.Count} 个 Root（尚未索引）");
            }

            return ToolResponse.OkWithData(new
            {
                source = "indexed",
                roots = roots.Select(r => new
                {
                    id = r.Id,
                    root_path = r.RootPath,
                    display_name = r.DisplayName,
                    scope_type = r.ScopeType.ToString(),
                    scope_name = r.ScopeName,
                    role = r.Role.ToString(),
                    read_only = r.ReadOnly,
                    is_enabled = r.IsEnabled,
                    is_default_search_scope = r.IsDefaultSearchScope,
                    provider_id = r.ProviderId,
                }).ToList(),
            }, $"共 {roots.Count} 个已索引 Root");
        }

        /// <summary>
        /// index_full — 全量索引所有 enabled roots。
        /// </summary>
        private static async Task<ToolResponse> HandleIndexFullAsync(CancellationToken ct)
        {
            using var store = CreateStore();
            if (store == null)
                return ToolResponse.Fail("索引存储初始化失败，请检查 WorkspaceRoot 配置。");

            var indexer = new CodebaseIndexer(store);
            var progress = await indexer.RunFullIndexAsync(null, ct);

            if (!progress.IsSuccess)
                return ToolResponse.Fail($"全量索引失败: {progress.ErrorMessage}");

            return ToolResponse.OkWithData(new
            {
                phase = progress.Phase.ToString(),
                processed_files = progress.ProcessedFiles,
                total_files = progress.TotalFiles,
                extracted_symbols = progress.ExtractedSymbols,
                error_files = progress.ErrorFiles,
                skipped_files = progress.SkippedFiles,
                elapsed_seconds = Math.Round(progress.ElapsedSeconds, 2),
            }, $"全量索引完成：处理 {progress.ProcessedFiles} 个文件，提取 {progress.ExtractedSymbols} 个符号，耗时 {progress.ElapsedSeconds:F1}s");
        }

        /// <summary>
        /// index_scope — 只索引指定 Scope 或 Root。
        /// </summary>
        private static async Task<ToolResponse> HandleIndexScopeAsync(JObject parameters, CancellationToken ct)
        {
            var scopeTypeStr = ToolHelpers.GetOptionalString(parameters, "scope_type");
            var scopeName = ToolHelpers.GetOptionalString(parameters, "scope_name");
            var rootId = ToolHelpers.GetOptionalInt(parameters, "root_id", 0);

            if (string.IsNullOrEmpty(scopeTypeStr) && string.IsNullOrEmpty(scopeName) && rootId == 0)
                return ToolResponse.Fail("index_scope 需要至少提供 scope_type、scope_name 或 root_id 之一。");

            using var store = CreateStore();
            if (store == null)
                return ToolResponse.Fail("索引存储初始化失败，请检查 WorkspaceRoot 配置。");

            // 解析 scope_type
            IndexScopeType? scopeType = null;
            if (!string.IsNullOrEmpty(scopeTypeStr) &&
                Enum.TryParse<IndexScopeType>(scopeTypeStr, true, out var parsedScope))
            {
                scopeType = parsedScope;
            }

            // 获取所有 Root，过滤出目标 Root
            var workspace = IndexWorkspaceResolver.ResolveFromCurrent();
            var workspaceId = await store.UpsertWorkspaceAsync(workspace, ct);
            workspace = new IndexWorkspace
            {
                Id = workspaceId,
                Fingerprint = workspace.Fingerprint,
                WorkspaceRoot = workspace.WorkspaceRoot,
                DisplayName = workspace.DisplayName,
                VcsType = workspace.VcsType,
                VcsRootPath = workspace.VcsRootPath,
                VcsUrl = workspace.VcsUrl,
                RepositoryRoot = workspace.RepositoryRoot,
                BranchId = workspace.BranchId,
                Revision = workspace.Revision,
            };

            var rootResolver = new IndexRootResolver();
            var allRoots = rootResolver.Resolve(workspace);

            // 过滤目标 Root
            var targetRoots = allRoots.Where(r =>
            {
                if (!r.IsEnabled) return false;
                if (rootId > 0 && r.Id != rootId) return false;
                if (scopeType.HasValue && r.ScopeType != scopeType.Value) return false;
                if (!string.IsNullOrEmpty(scopeName) &&
                    !string.Equals(r.ScopeName, scopeName, StringComparison.OrdinalIgnoreCase)) return false;
                return true;
            }).ToList();

            if (targetRoots.Count == 0)
                return ToolResponse.Fail($"未找到匹配的 Root（scope_type={scopeTypeStr}, scope_name={scopeName}, root_id={rootId}）");

            // 创建只包含目标 Root 的 Resolver
            var filteredResolver = new IndexRootResolver(
                new IIndexRootProvider[] { new FixedRootProvider(targetRoots) });

            var indexer = new CodebaseIndexer(store, filteredResolver);
            var progress = await indexer.RunFullIndexAsync(null, ct);

            if (!progress.IsSuccess)
                return ToolResponse.Fail($"Scope 索引失败: {progress.ErrorMessage}");

            return ToolResponse.OkWithData(new
            {
                indexed_roots = targetRoots.Count,
                processed_files = progress.ProcessedFiles,
                extracted_symbols = progress.ExtractedSymbols,
                error_files = progress.ErrorFiles,
                elapsed_seconds = Math.Round(progress.ElapsedSeconds, 2),
            }, $"Scope 索引完成：{targetRoots.Count} 个 Root，{progress.ProcessedFiles} 个文件，{progress.ExtractedSymbols} 个符号");
        }

        /// <summary>
        /// index_incremental — 增量索引。
        /// </summary>
        private static async Task<ToolResponse> HandleIndexIncrementalAsync(CancellationToken ct)
        {
            using var store = CreateStore();
            if (store == null)
                return ToolResponse.Fail("索引存储初始化失败，请检查 WorkspaceRoot 配置。");

            var indexer = new CodebaseIndexer(store);
            var progress = await indexer.RunIncrementalIndexAsync(null, ct);

            if (!progress.IsSuccess)
                return ToolResponse.Fail($"增量索引失败: {progress.ErrorMessage}");

            return ToolResponse.OkWithData(new
            {
                phase = progress.Phase.ToString(),
                processed_files = progress.ProcessedFiles,
                total_files = progress.TotalFiles,
                extracted_symbols = progress.ExtractedSymbols,
                error_files = progress.ErrorFiles,
                skipped_files = progress.SkippedFiles,
                elapsed_seconds = Math.Round(progress.ElapsedSeconds, 2),
            }, $"增量索引完成：更新 {progress.ProcessedFiles} 个文件，提取 {progress.ExtractedSymbols} 个符号，耗时 {progress.ElapsedSeconds:F1}s");
        }

        /// <summary>
        /// search_symbol — 按符号搜索，支持 Scope 过滤。
        /// </summary>
        private static async Task<ToolResponse> HandleSearchSymbolAsync(JObject parameters, CancellationToken ct)
        {
            var queryStr = ToolHelpers.GetOptionalString(parameters, "query");
            if (string.IsNullOrEmpty(queryStr))
                return ToolResponse.Fail("search_symbol 需要提供 query 参数。");

            // 构建 SearchQuery
            var query = new SearchQuery
            {
                Query = queryStr,
                SymbolType = ToolHelpers.GetOptionalString(parameters, "symbol_type"),
                ScopeName = ToolHelpers.GetOptionalString(parameters, "scope_name"),
                RootId = ToolHelpers.GetOptionalInt(parameters, "root_id", 0),
                IncludePlugins = ToolHelpers.GetOptionalBool(parameters, "include_plugins", false),
                IncludeEngine = ToolHelpers.GetOptionalBool(parameters, "include_engine", true),
                IncludeGenerated = ToolHelpers.GetOptionalBool(parameters, "include_generated", false),
                Fuzzy = ToolHelpers.GetOptionalBool(parameters, "fuzzy", true),
                Regex = ToolHelpers.GetOptionalBool(parameters, "regex", false),
                Limit = ToolHelpers.GetOptionalInt(parameters, "limit", 50),
                Namespace = ToolHelpers.GetOptionalString(parameters, "namespace"),
            };

            // scope_type
            var scopeTypeStr = ToolHelpers.GetOptionalString(parameters, "scope_type");
            if (!string.IsNullOrEmpty(scopeTypeStr) &&
                Enum.TryParse<IndexScopeType>(scopeTypeStr, true, out var scopeType))
            {
                query.ScopeType = scopeType;
            }

            // role
            var roleStr = ToolHelpers.GetOptionalString(parameters, "role");
            if (!string.IsNullOrEmpty(roleStr) &&
                Enum.TryParse<IndexRootRole>(roleStr, true, out var role))
            {
                query.Role = role;
            }

            // read_only（三值：null / true / false）
            var readOnlyToken = parameters["read_only"];
            if (readOnlyToken != null && readOnlyToken.Type != JTokenType.Null)
                query.ReadOnly = readOnlyToken.Value<bool>();

            using var store = CreateStore();
            if (store == null)
                return ToolResponse.Fail("索引存储初始化失败，请先执行 index_full 建立索引。");

            var searcher = new SymbolSearcher(store);

            if (!await searcher.IsIndexAvailableAsync(ct))
                return ToolResponse.Fail("尚未建立索引，请先执行 index_full 或 index_incremental。");

            var results = await searcher.SearchAsync(query, ct);

            // 获取 workspace 信息用于结果上下文
            var workspace = IndexWorkspaceResolver.ResolveFromCurrent();

            var resultItems = results.Select(s => BuildSymbolResult(s, workspace)).ToList();

            return ToolResponse.OkWithData(new
            {
                workspace_fingerprint = workspace.Fingerprint,
                workspace_root = workspace.WorkspaceRoot,
                branch_id = workspace.BranchId,
                query = queryStr,
                total = resultItems.Count,
                results = resultItems,
            }, $"找到 {resultItems.Count} 个符号");
        }

        /// <summary>
        /// list_namespaces — 按 Scope 列出命名空间。
        /// </summary>
        private static async Task<ToolResponse> HandleListNamespacesAsync(JObject parameters, CancellationToken ct)
        {
            IndexScopeType? scopeType = null;
            var scopeTypeStr = ToolHelpers.GetOptionalString(parameters, "scope_type");
            if (!string.IsNullOrEmpty(scopeTypeStr) &&
                Enum.TryParse<IndexScopeType>(scopeTypeStr, true, out var parsed))
            {
                scopeType = parsed;
            }

            using var store = CreateStore();
            if (store == null)
                return ToolResponse.Fail("索引存储初始化失败，请先执行 index_full 建立索引。");

            var searcher = new SymbolSearcher(store);

            if (!await searcher.IsIndexAvailableAsync(ct))
                return ToolResponse.Fail("尚未建立索引，请先执行 index_full 或 index_incremental。");

            var namespaces = await searcher.ListNamespacesAsync(scopeType, ct);

            return ToolResponse.OkWithData(new
            {
                scope_type_filter = scopeTypeStr,
                total = namespaces.Count,
                namespaces = namespaces.Select(n => new
                {
                    @namespace = n.Namespace,
                    scope_type = n.ScopeType.ToString(),
                    scope_name = n.ScopeName,
                    file_count = n.FileCount,
                    symbol_count = n.SymbolCount,
                }).ToList(),
            }, $"共 {namespaces.Count} 个命名空间");
        }

        /// <summary>
        /// get_file_symbols — 获取某文件内的符号。
        /// </summary>
        private static async Task<ToolResponse> HandleGetFileSymbolsAsync(JObject parameters, CancellationToken ct)
        {
            var filePath = ToolHelpers.GetRequiredString(parameters, "file_path");

            using var store = CreateStore();
            if (store == null)
                return ToolResponse.Fail("索引存储初始化失败，请先执行 index_full 建立索引。");

            var searcher = new SymbolSearcher(store);

            if (!await searcher.IsIndexAvailableAsync(ct))
                return ToolResponse.Fail("尚未建立索引，请先执行 index_full 或 index_incremental。");

            var workspace = IndexWorkspaceResolver.ResolveFromCurrent();
            var symbols = await searcher.GetSymbolsByFileAsync(filePath, ct);

            if (symbols.Count == 0)
                return ToolResponse.OkWithData(new
                {
                    file_path = filePath,
                    total = 0,
                    symbols = new object[0],
                }, $"文件 '{filePath}' 中未找到符号（可能尚未索引或文件不存在）");

            return ToolResponse.OkWithData(new
            {
                file_path = filePath,
                total = symbols.Count,
                symbols = symbols.Select(s => BuildSymbolResult(s, workspace)).ToList(),
            }, $"文件 '{filePath}' 中共 {symbols.Count} 个符号");
        }

        /// <summary>
        /// get_stats — 获取索引统计。
        /// </summary>
        private static async Task<ToolResponse> HandleGetStatsAsync(CancellationToken ct)
        {
            using var store = CreateStore();
            if (store == null)
                return ToolResponse.Fail("索引存储初始化失败，请检查 WorkspaceRoot 配置。");

            var indexer = new CodebaseIndexer(store);
            var stats = await indexer.GetStatsAsync(ct);

            if (stats == null)
                return ToolResponse.OkWithData(new
                {
                    indexed = false,
                    message = "尚未建立索引，请执行 index_full 或 index_incremental。",
                }, "尚未建立索引");

            var lastFull = await indexer.GetLastFullIndexTimeAsync(ct);
            var lastIncremental = await indexer.GetLastIncrementalIndexTimeAsync(ct);

            return ToolResponse.OkWithData(new
            {
                indexed = true,
                total_files = stats.TotalFiles,
                total_symbols = stats.TotalSymbols,
                total_roots = stats.TotalRoots,
                error_files = stats.ErrorFiles,
                last_full_index_at = lastFull?.ToString("O"),
                last_incremental_at = lastIncremental?.ToString("O"),
                scope_breakdown = stats.FilesByScope != null
                    ? stats.FilesByScope.Select(kvp => new
                    {
                        scope = kvp.Key,
                        file_count = kvp.Value,
                    }).ToList()
                    : null,
            }, $"索引统计：{stats.TotalFiles} 个文件，{stats.TotalSymbols} 个符号，{stats.TotalRoots} 个 Root");
        }

        /// <summary>
        /// clear_index — 清除当前 Workspace 的索引。
        /// </summary>
        private static async Task<ToolResponse> HandleClearIndexAsync(CancellationToken ct)
        {
            using var store = CreateStore();
            if (store == null)
                return ToolResponse.Fail("索引存储初始化失败，请检查 WorkspaceRoot 配置。");

            var workspace = IndexWorkspaceResolver.ResolveFromCurrent();
            var stored = await store.GetWorkspaceByFingerprintAsync(workspace.Fingerprint, ct);

            if (stored == null)
                return ToolResponse.Ok("当前 Workspace 尚无索引数据，无需清除。");

            await store.ClearWorkspaceIndexAsync(stored.Id, ct);

            return ToolResponse.Ok($"已清除 Workspace '{workspace.DisplayName}' 的索引数据（fingerprint: {workspace.Fingerprint}）");
        }

        /// <summary>
        /// search_text — FTS5 全文搜索符号名称（比 search_symbol 更快，适合模糊关键词）。
        /// </summary>
        private static async Task<ToolResponse> HandleSearchTextAsync(JObject parameters, CancellationToken ct)
        {
            var text = ToolHelpers.GetOptionalString(parameters, "query");
            if (string.IsNullOrEmpty(text))
                return ToolResponse.Fail("search_text 需要提供 query 参数。");

            var limit = Math.Min(ToolHelpers.GetOptionalInt(parameters, "limit", 50), 200);

            using var store = CreateStore();
            if (store == null)
                return ToolResponse.Fail("索引存储初始化失败，请先执行 index_full 建立索引。");

            var workspace = IndexWorkspaceResolver.ResolveFromCurrent();
            var stored = await store.GetWorkspaceByFingerprintAsync(workspace.Fingerprint, ct);
            if (stored == null)
                return ToolResponse.Fail("尚未建立索引，请先执行 index_full 或 index_incremental。");

            var results = await store.SearchSymbolsByTextAsync(stored.Id, text, limit, ct);

            var resultItems = results.Select(s => BuildSymbolResult(s, workspace)).ToList();
            return ToolResponse.OkWithData(new
            {
                query = text,
                backend = store.BackendType,
                total = resultItems.Count,
                results = resultItems,
            }, $"全文搜索 '{text}' 找到 {resultItems.Count} 个符号");
        }

        /// <summary>
        /// get_dependencies — 获取某文件或符号的出向依赖（该文件/符号引用了哪些类型）。
        /// </summary>
        private static async Task<ToolResponse> HandleGetDependenciesAsync(JObject parameters, CancellationToken ct)
        {
            var filePath = ToolHelpers.GetOptionalString(parameters, "file_path");
            var symbolId = ToolHelpers.GetOptionalInt(parameters, "symbol_id", 0);

            if (string.IsNullOrEmpty(filePath) && symbolId == 0)
                return ToolResponse.Fail("get_dependencies 需要提供 file_path 或 symbol_id 之一。");

            using var store = CreateStore();
            if (store == null)
                return ToolResponse.Fail("索引存储初始化失败，请先执行 index_full 建立索引。");

            var workspace = IndexWorkspaceResolver.ResolveFromCurrent();
            var stored = await store.GetWorkspaceByFingerprintAsync(workspace.Fingerprint, ct);
            if (stored == null)
                return ToolResponse.Fail("尚未建立索引，请先执行 index_full 或 index_incremental。");

            // 解析 fileId
            int fileId = 0;
            if (!string.IsNullOrEmpty(filePath))
            {
                // 支持相对路径
                var absPath = filePath;
                if (!System.IO.Path.IsPathRooted(absPath) && !string.IsNullOrEmpty(workspace.WorkspaceRoot))
                    absPath = System.IO.Path.Combine(workspace.WorkspaceRoot, filePath);

                var indexedFile = await store.GetFileByPathAsync(stored.Id, absPath, ct);
                if (indexedFile == null)
                    return ToolResponse.Fail($"文件 '{filePath}' 未在索引中找到，请先执行索引。");
                fileId = indexedFile.Id;
            }

            int? symId = symbolId > 0 ? (int?)symbolId : null;
            var deps = await store.GetDependenciesAsync(stored.Id, fileId, symId, ct);

            var limit = Math.Min(ToolHelpers.GetOptionalInt(parameters, "limit", 200), 500);
            var items = deps.Take(limit).Select(d => new
            {
                from_file_id = d.FromFileId,
                from_symbol_id = d.FromSymbolId,
                to_type_name = d.ToTypeName,
                to_symbol_id = d.ToSymbolId,
                dependency_kind = d.DependencyKind,
                source_line = d.SourceLine,
            }).ToList();

            return ToolResponse.OkWithData(new
            {
                file_path = filePath,
                symbol_id = symbolId > 0 ? symbolId : (int?)null,
                total = deps.Count,
                shown = items.Count,
                dependencies = items,
            }, $"找到 {deps.Count} 条依赖关系");
        }

        /// <summary>
        /// find_usages — 查找哪些文件/符号引用了指定类型名称（入向依赖）。
        /// </summary>
        private static async Task<ToolResponse> HandleFindUsagesAsync(JObject parameters, CancellationToken ct)
        {
            var typeName = ToolHelpers.GetOptionalString(parameters, "type_name");
            if (string.IsNullOrEmpty(typeName))
                return ToolResponse.Fail("find_usages 需要提供 type_name 参数。");

            using var store = CreateStore();
            if (store == null)
                return ToolResponse.Fail("索引存储初始化失败，请先执行 index_full 建立索引。");

            var workspace = IndexWorkspaceResolver.ResolveFromCurrent();
            var stored = await store.GetWorkspaceByFingerprintAsync(workspace.Fingerprint, ct);
            if (stored == null)
                return ToolResponse.Fail("尚未建立索引，请先执行 index_full 或 index_incremental。");

            var usages = await store.FindUsagesAsync(stored.Id, typeName, ct);

            var limit = Math.Min(ToolHelpers.GetOptionalInt(parameters, "limit", 100), 500);
            var items = usages.Take(limit).Select(d => new
            {
                from_file_id = d.FromFileId,
                from_symbol_id = d.FromSymbolId,
                to_type_name = d.ToTypeName,
                to_symbol_id = d.ToSymbolId,
                dependency_kind = d.DependencyKind,
                source_line = d.SourceLine,
            }).ToList();

            return ToolResponse.OkWithData(new
            {
                type_name = typeName,
                total = usages.Count,
                shown = items.Count,
                usages = items,
            }, $"类型 '{typeName}' 被 {usages.Count} 处引用");
        }

        /// <summary>
        /// get_symbol_context — 聚合符号的完整上下文：符号信息 + 同文件符号 + 出向依赖 + 入向引用。
        /// </summary>
        private static async Task<ToolResponse> HandleGetSymbolContextAsync(JObject parameters, CancellationToken ct)
        {
            var fullName = ToolHelpers.GetOptionalString(parameters, "full_name");
            var symbolId = ToolHelpers.GetOptionalInt(parameters, "symbol_id", 0);

            if (string.IsNullOrEmpty(fullName) && symbolId == 0)
                return ToolResponse.Fail("get_symbol_context 需要提供 full_name 或 symbol_id 之一。");

            var includeDeps = ToolHelpers.GetOptionalBool(parameters, "include_dependencies", true);
            var includeUsages = ToolHelpers.GetOptionalBool(parameters, "include_usages", true);

            using var store = CreateStore();
            if (store == null)
                return ToolResponse.Fail("索引存储初始化失败，请先执行 index_full 建立索引。");

            var workspace = IndexWorkspaceResolver.ResolveFromCurrent();
            var stored = await store.GetWorkspaceByFingerprintAsync(workspace.Fingerprint, ct);
            if (stored == null)
                return ToolResponse.Fail("尚未建立索引，请先执行 index_full 或 index_incremental。");

            // 通过 full_name 查找符号
            SymbolInfo targetSymbol = null;
            if (!string.IsNullOrEmpty(fullName))
            {
                var searcher = new SymbolSearcher(store);
                var query = new AgentCore.Editor.Components.Indexing.Query.SearchQuery
                {
                    Query = fullName,
                    Fuzzy = false,
                    Limit = 5,
                };
                var candidates = await searcher.SearchAsync(query, ct);
                targetSymbol = candidates.FirstOrDefault(s =>
                    string.Equals(s.FullName, fullName, StringComparison.Ordinal));
                if (targetSymbol == null)
                    targetSymbol = candidates.FirstOrDefault();
            }
            else if (symbolId > 0)
            {
                // 通过 file symbols 查找（需要先找到文件）
                // 降级：通过 FTS 搜索 symbol_id 对应的符号
                var searcher = new SymbolSearcher(store);
                var allByFile = await store.SearchSymbolsByTextAsync(stored.Id, "", 1, ct);
                // 直接通过 GetSymbolsByFileAsync 无法按 ID 查，改用 search_text 降级
                // 实际上 get_symbol_context 主要通过 full_name 使用，symbol_id 路径作为辅助
            }

            if (targetSymbol == null && symbolId == 0)
                return ToolResponse.Fail($"未找到符号 '{fullName}'，请先确认符号名称或执行索引。");

            object symbolResult = targetSymbol != null ? BuildSymbolResult(targetSymbol, workspace) : null;

            // 获取同文件符号
            object[] fileSymbols = null;
            if (targetSymbol != null)
            {
                var fileSymList = await store.GetSymbolsByFileAsync(targetSymbol.FileId, ct);
                fileSymbols = fileSymList
                    .Where(s => s.Id != targetSymbol.Id)
                    .Select(s => (object)new
                    {
                        id = s.Id,
                        name = s.Name,
                        full_name = s.FullName,
                        symbol_type = s.SymbolType,
                        line_number = s.LineNumber,
                    })
                    .ToArray();
            }

            // 获取出向依赖
            object[] depsResult = null;
            if (includeDeps && targetSymbol != null)
            {
                var deps = await store.GetDependenciesAsync(stored.Id, targetSymbol.FileId, targetSymbol.Id, ct);
                depsResult = deps.Select(d => (object)new
                {
                    to_type_name = d.ToTypeName,
                    to_symbol_id = d.ToSymbolId,
                    dependency_kind = d.DependencyKind,
                    source_line = d.SourceLine,
                }).ToArray();
            }

            // 获取入向引用（谁引用了这个类型）
            object[] usagesResult = null;
            if (includeUsages && targetSymbol != null)
            {
                var name = targetSymbol.Name;
                var usages = await store.FindUsagesAsync(stored.Id, name, ct);
                usagesResult = usages.Take(50).Select(d => (object)new
                {
                    from_file_id = d.FromFileId,
                    from_symbol_id = d.FromSymbolId,
                    dependency_kind = d.DependencyKind,
                    source_line = d.SourceLine,
                }).ToArray();
            }

            return ToolResponse.OkWithData(new
            {
                symbol = symbolResult,
                file_symbols_count = fileSymbols?.Length ?? 0,
                file_symbols = fileSymbols,
                dependencies_count = depsResult?.Length ?? 0,
                dependencies = depsResult,
                usages_count = usagesResult?.Length ?? 0,
                usages = usagesResult,
            }, $"符号上下文：{targetSymbol?.FullName ?? fullName}，{depsResult?.Length ?? 0} 条依赖，{usagesResult?.Length ?? 0} 处引用");
        }

        /// <summary>
        /// get_backend_info — 获取当前索引后端信息（SQLite / Jsonl，数据库路径等）。
        /// </summary>
        private static async Task<ToolResponse> HandleGetBackendInfoAsync(CancellationToken ct)
        {
            using var store = CreateStore();
            if (store == null)
                return ToolResponse.Fail("索引存储初始化失败，请检查 WorkspaceRoot 配置。");

            var workspace = IndexWorkspaceResolver.ResolveFromCurrent();
            var stored = await store.GetWorkspaceByFingerprintAsync(workspace.Fingerprint, ct);

            string dbPath = null;
            if (!string.IsNullOrEmpty(workspace?.WorkspaceRoot))
                dbPath = IndexStoreFactory.GetDbPath(workspace.WorkspaceRoot);

            return ToolResponse.OkWithData(new
            {
                backend_type = store.BackendType,
                workspace_root = workspace?.WorkspaceRoot,
                db_path = dbPath,
                db_exists = dbPath != null && System.IO.File.Exists(dbPath),
                workspace_indexed = stored != null,
                workspace_fingerprint = workspace?.Fingerprint,
            }, $"当前索引后端：{store.BackendType}");
        }

        // ── 私有辅助方法 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 创建 IIndexStore 实例（优先 SQLite，降级 Jsonl）。
        /// 如果 WorkspaceRoot 无法解析则返回 null。
        /// </summary>
        private static IIndexStore CreateStore()
        {
            return IndexStoreFactory.CreateFromCurrent();
        }

        /// <summary>
        /// 将 SymbolInfo 转换为 LLM 友好的结果对象。
        /// </summary>
        private static object BuildSymbolResult(SymbolInfo s, IndexWorkspace workspace)
        {
            // 计算相对于 WorkspaceRoot 的路径
            string workspaceRelativePath = null;
            if (!string.IsNullOrEmpty(s.FilePath) && !string.IsNullOrEmpty(workspace.WorkspaceRoot))
            {
                var normalizedRoot = workspace.WorkspaceRoot.Replace('\\', '/').TrimEnd('/') + '/';
                var normalizedFile = s.FilePath.Replace('\\', '/');
                if (normalizedFile.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                    workspaceRelativePath = normalizedFile.Substring(normalizedRoot.Length);
            }

            return new
            {
                name = s.Name,
                full_name = s.FullName,
                symbol_type = s.SymbolType,
                @namespace = s.Namespace,
                file_path = s.FilePath,
                workspace_relative_path = workspaceRelativePath,
                line_number = s.LineNumber,
                accessibility = s.Accessibility,
                is_static = s.IsStatic,
                is_abstract = s.IsAbstract,
                is_partial = s.IsPartial,
                is_virtual = s.IsVirtual,
                is_override = s.IsOverride,
                is_readonly = s.IsReadOnly,
                is_const = s.IsConst,
                return_type = s.ReturnType,
                parameters = s.Parameters,
                base_types = s.BaseTypes,
                generic_params = s.GenericParams,
                declaration_snippet = s.DeclarationSnippet,
                scope_type = s.ScopeType.ToString(),
                scope_name = s.ScopeName,
                role = s.Role.ToString(),
                read_only = s.ReadOnly,
                branch_id = workspace.BranchId,
            };
        }

        // ── 内部辅助类 ───────────────────────────────────────────────────────────

        /// <summary>
        /// 固定 Root 列表的 Provider（用于 index_scope 的过滤索引）。
        /// </summary>
        private sealed class FixedRootProvider : IIndexRootProvider
        {
            private readonly IReadOnlyList<IndexRoot> _roots;

            public FixedRootProvider(IReadOnlyList<IndexRoot> roots)
            {
                _roots = roots;
            }

            public string ProviderId => "fixed";
            public int Priority => 0;

            public IReadOnlyList<IndexRoot> DiscoverRoots(IndexWorkspace workspace)
                => _roots;
        }
    }
}
