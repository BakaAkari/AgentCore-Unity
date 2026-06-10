#if AGENTCORE_SQLITE
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Components.Indexing.Models;
using AgentCore.Editor.Components.Indexing.Query;
using Mono.Data.Sqlite;
using Newtonsoft.Json;

namespace AgentCore.Editor.Components.Indexing.Core
{
    /// <summary>
    /// SQLite 存储后端（Phase 2 默认实现）。
    /// 使用 Unity Editor 内置的 Mono.Data.Sqlite，无需额外 DLL。
    /// 支持 FTS5 全文搜索（symbols_fts 虚拟表）和依赖图查询。
    ///
    /// 线程安全：所有写操作通过 <see cref="_writeLock"/> 串行化；读操作可并发。
    /// </summary>
    public sealed class SqliteIndexStore : IIndexStore
    {
        // ── 常量 ────────────────────────────────────────────────────────────────

        /// <summary>当前 Schema 版本，用于迁移检测。</summary>
        private const int SchemaVersion = 1;

        /// <inheritdoc/>
        public string BackendType => "sqlite";

        // ── 字段 ────────────────────────────────────────────────────────────────

        private readonly string _dbPath;
        private readonly object _writeLock = new object();

        // ── 构造函数 ────────────────────────────────────────────────────────────

        /// <summary>
        /// 创建 SQLite 存储实例。
        /// </summary>
        /// <param name="dbPath">SQLite 数据库文件的绝对路径（不存在时自动创建）。</param>
        public SqliteIndexStore(string dbPath)
        {
            if (string.IsNullOrEmpty(dbPath))
                throw new ArgumentNullException(nameof(dbPath));

            _dbPath = dbPath;
            InitializeSchema();
        }

        // ── Schema 初始化 ───────────────────────────────────────────────────────

        /// <summary>创建或迁移数据库 Schema。</summary>
        private void InitializeSchema()
        {
            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();
            try
            {
                // 开启 WAL 模式（提升并发读性能）
                ExecuteNonQuery(conn, "PRAGMA journal_mode=WAL;");
                ExecuteNonQuery(conn, "PRAGMA foreign_keys=ON;");
                ExecuteNonQuery(conn, "PRAGMA synchronous=NORMAL;");

                // 检查 schema_version
                ExecuteNonQuery(conn, @"
                    CREATE TABLE IF NOT EXISTS schema_info (
                        key   TEXT PRIMARY KEY,
                        value TEXT
                    );");

                var currentVersion = GetScalarString(conn,
                    "SELECT value FROM schema_info WHERE key='version'");

                if (currentVersion == null)
                {
                    // 首次创建：建立完整 Schema
                    CreateFullSchema(conn);
                    ExecuteNonQuery(conn,
                        $"INSERT OR REPLACE INTO schema_info(key,value) VALUES('version','{SchemaVersion}')");
                }
                // 未来版本迁移在此扩展

                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        /// <summary>创建完整数据库 Schema（首次初始化）。</summary>
        private static void CreateFullSchema(SqliteConnection conn)
        {
            // workspaces
            ExecuteNonQuery(conn, @"
                CREATE TABLE IF NOT EXISTS workspaces (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    fingerprint     TEXT    NOT NULL UNIQUE,
                    workspace_root  TEXT,
                    display_name    TEXT,
                    vcs_type        TEXT,
                    vcs_root_path   TEXT,
                    vcs_url         TEXT,
                    repository_root TEXT,
                    branch_id       TEXT,
                    revision        TEXT
                );");

            // roots
            ExecuteNonQuery(conn, @"
                CREATE TABLE IF NOT EXISTS roots (
                    id                       INTEGER PRIMARY KEY AUTOINCREMENT,
                    workspace_id             INTEGER NOT NULL REFERENCES workspaces(id),
                    root_path                TEXT    NOT NULL,
                    relative_to_workspace    TEXT,
                    display_name             TEXT,
                    scope_type               TEXT,
                    scope_name               TEXT,
                    role                     TEXT,
                    read_only                INTEGER,
                    is_enabled               INTEGER,
                    is_default_search_scope  INTEGER,
                    provider_id              TEXT,
                    UNIQUE(workspace_id, root_path)
                );");

            // files
            ExecuteNonQuery(conn, @"
                CREATE TABLE IF NOT EXISTS files (
                    id            INTEGER PRIMARY KEY AUTOINCREMENT,
                    workspace_id  INTEGER NOT NULL REFERENCES workspaces(id),
                    root_id       INTEGER NOT NULL REFERENCES roots(id),
                    file_path     TEXT    NOT NULL,
                    relative_to_root TEXT,
                    content_hash  TEXT,
                    last_modified INTEGER,
                    last_indexed  INTEGER,
                    file_size     INTEGER,
                    has_errors    INTEGER,
                    error_message TEXT,
                    symbol_count  INTEGER,
                    UNIQUE(workspace_id, file_path)
                );");

            // symbols
            ExecuteNonQuery(conn, @"
                CREATE TABLE IF NOT EXISTS symbols (
                    id               INTEGER PRIMARY KEY AUTOINCREMENT,
                    file_id          INTEGER NOT NULL REFERENCES files(id),
                    workspace_id     INTEGER NOT NULL,
                    root_id          INTEGER NOT NULL,
                    scope_type       TEXT,
                    scope_name       TEXT,
                    branch_id        TEXT,
                    name             TEXT    NOT NULL,
                    full_name        TEXT,
                    namespace        TEXT,
                    symbol_type      TEXT,
                    access_modifier  TEXT,
                    is_static        INTEGER,
                    is_abstract      INTEGER,
                    is_partial       INTEGER,
                    base_type        TEXT,
                    interfaces       TEXT,
                    line_start       INTEGER,
                    line_end         INTEGER,
                    signature        TEXT,
                    attributes       TEXT
                );");

            // symbol_dependencies
            ExecuteNonQuery(conn, @"
                CREATE TABLE IF NOT EXISTS symbol_dependencies (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    workspace_id    INTEGER NOT NULL,
                    from_file_id    INTEGER NOT NULL REFERENCES files(id),
                    from_symbol_id  INTEGER,
                    to_type_name    TEXT    NOT NULL,
                    to_symbol_id    INTEGER,
                    dependency_kind TEXT    NOT NULL,
                    source_line     INTEGER
                );");

            // metadata
            ExecuteNonQuery(conn, @"
                CREATE TABLE IF NOT EXISTS metadata (
                    workspace_id INTEGER NOT NULL,
                    key          TEXT    NOT NULL,
                    value        TEXT,
                    PRIMARY KEY(workspace_id, key)
                );");

            // 索引
            ExecuteNonQuery(conn, "CREATE INDEX IF NOT EXISTS idx_symbols_workspace ON symbols(workspace_id);");
            ExecuteNonQuery(conn, "CREATE INDEX IF NOT EXISTS idx_symbols_name ON symbols(name);");
            ExecuteNonQuery(conn, "CREATE INDEX IF NOT EXISTS idx_symbols_full_name ON symbols(full_name);");
            ExecuteNonQuery(conn, "CREATE INDEX IF NOT EXISTS idx_symbols_file ON symbols(file_id);");
            ExecuteNonQuery(conn, "CREATE INDEX IF NOT EXISTS idx_files_workspace ON files(workspace_id);");
            ExecuteNonQuery(conn, "CREATE INDEX IF NOT EXISTS idx_files_root ON files(root_id);");
            ExecuteNonQuery(conn, "CREATE INDEX IF NOT EXISTS idx_deps_from_file ON symbol_dependencies(from_file_id);");
            ExecuteNonQuery(conn, "CREATE INDEX IF NOT EXISTS idx_deps_from_symbol ON symbol_dependencies(from_symbol_id);");
            ExecuteNonQuery(conn, "CREATE INDEX IF NOT EXISTS idx_deps_to_type ON symbol_dependencies(to_type_name);");
            ExecuteNonQuery(conn, "CREATE INDEX IF NOT EXISTS idx_deps_to_symbol ON symbol_dependencies(to_symbol_id);");
            ExecuteNonQuery(conn, "CREATE INDEX IF NOT EXISTS idx_deps_workspace ON symbol_dependencies(workspace_id);");

            // FTS5 虚拟表（content= 模式，不重复存储数据）
            ExecuteNonQuery(conn, @"
                CREATE VIRTUAL TABLE IF NOT EXISTS symbols_fts USING fts5(
                    name,
                    full_name,
                    namespace,
                    signature,
                    content='symbols',
                    content_rowid='id'
                );");

            // FTS5 同步触发器
            ExecuteNonQuery(conn, @"
                CREATE TRIGGER IF NOT EXISTS symbols_ai AFTER INSERT ON symbols BEGIN
                    INSERT INTO symbols_fts(rowid, name, full_name, namespace, signature)
                    VALUES (new.id, new.name, new.full_name, new.namespace, new.signature);
                END;");

            ExecuteNonQuery(conn, @"
                CREATE TRIGGER IF NOT EXISTS symbols_ad AFTER DELETE ON symbols BEGIN
                    INSERT INTO symbols_fts(symbols_fts, rowid, name, full_name, namespace, signature)
                    VALUES ('delete', old.id, old.name, old.full_name, old.namespace, old.signature);
                END;");

            ExecuteNonQuery(conn, @"
                CREATE TRIGGER IF NOT EXISTS symbols_au AFTER UPDATE ON symbols BEGIN
                    INSERT INTO symbols_fts(symbols_fts, rowid, name, full_name, namespace, signature)
                    VALUES ('delete', old.id, old.name, old.full_name, old.namespace, old.signature);
                    INSERT INTO symbols_fts(rowid, name, full_name, namespace, signature)
                    VALUES (new.id, new.name, new.full_name, new.namespace, new.signature);
                END;");
        }

        // ── Workspace ───────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public Task<int> UpsertWorkspaceAsync(IndexWorkspace workspace, CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                lock (_writeLock)
                {
                    using var conn = OpenConnection();
                    ExecuteNonQuery(conn, @"
                        INSERT INTO workspaces(fingerprint, workspace_root, display_name, vcs_type,
                            vcs_root_path, vcs_url, repository_root, branch_id, revision)
                        VALUES(@fp, @wr, @dn, @vt, @vrp, @vu, @rr, @bi, @rv)
                        ON CONFLICT(fingerprint) DO UPDATE SET
                            workspace_root  = excluded.workspace_root,
                            display_name    = excluded.display_name,
                            vcs_type        = excluded.vcs_type,
                            vcs_root_path   = excluded.vcs_root_path,
                            vcs_url         = excluded.vcs_url,
                            repository_root = excluded.repository_root,
                            branch_id       = excluded.branch_id,
                            revision        = excluded.revision;",
                        ("@fp",  workspace.Fingerprint),
                        ("@wr",  workspace.WorkspaceRoot),
                        ("@dn",  workspace.DisplayName),
                        ("@vt",  workspace.VcsType),
                        ("@vrp", workspace.VcsRootPath),
                        ("@vu",  workspace.VcsUrl),
                        ("@rr",  workspace.RepositoryRoot),
                        ("@bi",  workspace.BranchId),
                        ("@rv",  workspace.Revision));

                    var id = GetScalarInt(conn,
                        "SELECT id FROM workspaces WHERE fingerprint=@fp",
                        ("@fp", workspace.Fingerprint));
                    return id;
                }
            }, ct);
        }

        /// <inheritdoc/>
        public Task<IndexWorkspace> GetWorkspaceByFingerprintAsync(string fingerprint, CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT * FROM workspaces WHERE fingerprint=@fp";
                cmd.Parameters.AddWithValue("@fp", fingerprint);
                using var reader = cmd.ExecuteReader();
                if (!reader.Read()) return null;
                return ReadWorkspace(reader);
            }, ct);
        }

        // ── Roots ───────────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public Task<int> UpsertRootAsync(int workspaceId, IndexRoot root, CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                lock (_writeLock)
                {
                    using var conn = OpenConnection();
                    ExecuteNonQuery(conn, @"
                        INSERT INTO roots(workspace_id, root_path, relative_to_workspace, display_name,
                            scope_type, scope_name, role, read_only, is_enabled, is_default_search_scope, provider_id)
                        VALUES(@wid, @rp, @rtw, @dn, @st, @sn, @ro, @readonly, @ie, @idss, @pid)
                        ON CONFLICT(workspace_id, root_path) DO UPDATE SET
                            relative_to_workspace   = excluded.relative_to_workspace,
                            display_name            = excluded.display_name,
                            scope_type              = excluded.scope_type,
                            scope_name              = excluded.scope_name,
                            role                    = excluded.role,
                            read_only               = excluded.read_only,
                            is_enabled              = excluded.is_enabled,
                            is_default_search_scope = excluded.is_default_search_scope,
                            provider_id             = excluded.provider_id;",
                        ("@wid",    workspaceId),
                        ("@rp",     root.RootPath),
                        ("@rtw",    root.RelativeToWorkspace),
                        ("@dn",     root.DisplayName),
                        ("@st",     root.ScopeType.ToString()),
                        ("@sn",     root.ScopeName),
                        ("@ro",     root.Role.ToString()),
                        ("@readonly", root.ReadOnly ? 1 : 0),
                        ("@ie",     root.IsEnabled ? 1 : 0),
                        ("@idss",   root.IsDefaultSearchScope ? 1 : 0),
                        ("@pid",    root.ProviderId));

                    return GetScalarInt(conn,
                        "SELECT id FROM roots WHERE workspace_id=@wid AND root_path=@rp",
                        ("@wid", workspaceId), ("@rp", root.RootPath));
                }
            }, ct);
        }

        /// <inheritdoc/>
        public Task<IReadOnlyList<IndexRoot>> GetRootsAsync(int workspaceId, CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT * FROM roots WHERE workspace_id=@wid";
                cmd.Parameters.AddWithValue("@wid", workspaceId);
                var list = new List<IndexRoot>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) list.Add(ReadRoot(reader));
                return (IReadOnlyList<IndexRoot>)list;
            }, ct);
        }

        // ── Files ───────────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public Task<int> UpsertFileAsync(int workspaceId, int rootId, IndexedFile file, CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                lock (_writeLock)
                {
                    using var conn = OpenConnection();
                    var normalizedPath = NormalizePath(file.FilePath);
                    ExecuteNonQuery(conn, @"
                        INSERT INTO files(workspace_id, root_id, file_path, relative_to_root,
                            content_hash, last_modified, last_indexed, file_size, has_errors, error_message, symbol_count)
                        VALUES(@wid, @rid, @fp, @rtr, @ch, @lm, @li, @fs, @he, @em, @sc)
                        ON CONFLICT(workspace_id, file_path) DO UPDATE SET
                            root_id         = excluded.root_id,
                            relative_to_root= excluded.relative_to_root,
                            content_hash    = excluded.content_hash,
                            last_modified   = excluded.last_modified,
                            last_indexed    = excluded.last_indexed,
                            file_size       = excluded.file_size,
                            has_errors      = excluded.has_errors,
                            error_message   = excluded.error_message,
                            symbol_count    = excluded.symbol_count;",
                        ("@wid", workspaceId),
                        ("@rid", rootId),
                        ("@fp",  normalizedPath),
                        ("@rtr", file.RelativeToRoot),
                        ("@ch",  file.ContentHash),
                        ("@lm",  file.LastModified),
                        ("@li",  file.LastIndexed),
                        ("@fs",  file.FileSize),
                        ("@he",  file.HasErrors ? 1 : 0),
                        ("@em",  file.ErrorMessage),
                        ("@sc",  file.SymbolCount));

                    return GetScalarInt(conn,
                        "SELECT id FROM files WHERE workspace_id=@wid AND file_path=@fp",
                        ("@wid", workspaceId), ("@fp", normalizedPath));
                }
            }, ct);
        }

        /// <inheritdoc/>
        public Task DeleteFileAsync(int fileId, CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                lock (_writeLock)
                {
                    using var conn = OpenConnection();
                    ExecuteNonQuery(conn, "DELETE FROM files WHERE id=@id", ("@id", fileId));
                }
            }, ct);
        }

        /// <inheritdoc/>
        public Task<IReadOnlyList<IndexedFile>> GetFilesForRootAsync(int rootId, CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT * FROM files WHERE root_id=@rid";
                cmd.Parameters.AddWithValue("@rid", rootId);
                var list = new List<IndexedFile>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) list.Add(ReadFile(reader));
                return (IReadOnlyList<IndexedFile>)list;
            }, ct);
        }

        /// <inheritdoc/>
        public Task<IndexedFile> GetFileByPathAsync(int workspaceId, string filePath, CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT * FROM files WHERE workspace_id=@wid AND file_path=@fp";
                cmd.Parameters.AddWithValue("@wid", workspaceId);
                cmd.Parameters.AddWithValue("@fp", NormalizePath(filePath));
                using var reader = cmd.ExecuteReader();
                return reader.Read() ? ReadFile(reader) : null;
            }, ct);
        }

        // ── Symbols ─────────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public Task BulkInsertSymbolsAsync(IEnumerable<SymbolInfo> symbols, CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                var list = symbols?.ToList();
                if (list == null || list.Count == 0) return;

                lock (_writeLock)
                {
                    using var conn = OpenConnection();
                    using var tx = conn.BeginTransaction();
                    try
                    {
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = @"
                            INSERT INTO symbols(file_id, workspace_id, root_id, scope_type, scope_name,
                                branch_id, name, full_name, namespace, symbol_type, access_modifier,
                                is_static, is_abstract, is_partial, base_type, interfaces,
                                line_start, line_end, signature, attributes)
                            VALUES(@fid, @wid, @rid, @st, @sn, @bi, @nm, @fn, @ns, @syt, @am,
                                @isstatic, @isabstract, @ispartial, @bt, @ifaces, @ls, @le, @sig, @attrs)";

                        foreach (var sym in list)
                        {
                            cmd.Parameters.Clear();
                            cmd.Parameters.AddWithValue("@fid",       sym.FileId);
                            cmd.Parameters.AddWithValue("@wid",       sym.WorkspaceId);
                            cmd.Parameters.AddWithValue("@rid",       sym.RootId);
                            cmd.Parameters.AddWithValue("@st",        sym.ScopeType.ToString());
                            cmd.Parameters.AddWithValue("@sn",        sym.ScopeName);
                            cmd.Parameters.AddWithValue("@bi",        sym.BranchId);
                            cmd.Parameters.AddWithValue("@nm",        sym.Name);
                            cmd.Parameters.AddWithValue("@fn",        sym.FullName);
                            cmd.Parameters.AddWithValue("@ns",        sym.Namespace);
                            cmd.Parameters.AddWithValue("@syt",       sym.SymbolType);
                            cmd.Parameters.AddWithValue("@am",        sym.AccessModifier);
                            cmd.Parameters.AddWithValue("@isstatic",  sym.IsStatic ? 1 : 0);
                            cmd.Parameters.AddWithValue("@isabstract",sym.IsAbstract ? 1 : 0);
                            cmd.Parameters.AddWithValue("@ispartial", sym.IsPartial ? 1 : 0);
                            cmd.Parameters.AddWithValue("@bt",        sym.BaseType);
                            cmd.Parameters.AddWithValue("@ifaces",    sym.Interfaces != null
                                ? JsonConvert.SerializeObject(sym.Interfaces) : null);
                            cmd.Parameters.AddWithValue("@ls",        sym.LineStart);
                            cmd.Parameters.AddWithValue("@le",        sym.LineEnd);
                            cmd.Parameters.AddWithValue("@sig",       sym.Signature);
                            cmd.Parameters.AddWithValue("@attrs",     sym.Attributes != null
                                ? JsonConvert.SerializeObject(sym.Attributes) : null);
                            cmd.ExecuteNonQuery();

                            // 回填 Id（触发器已同步 FTS5）
                            sym.Id = (int)GetScalarLong(conn, "SELECT last_insert_rowid()");
                        }
                        tx.Commit();
                    }
                    catch
                    {
                        tx.Rollback();
                        throw;
                    }
                }
            }, ct);
        }

        /// <inheritdoc/>
        public Task DeleteSymbolsByFileAsync(int fileId, CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                lock (_writeLock)
                {
                    using var conn = OpenConnection();
                    ExecuteNonQuery(conn, "DELETE FROM symbols WHERE file_id=@fid", ("@fid", fileId));
                }
            }, ct);
        }

        /// <inheritdoc/>
        public Task<IReadOnlyList<SymbolInfo>> SearchSymbolsAsync(
            int workspaceId, SearchQuery query, CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                using var conn = OpenConnection();
                var sb = new StringBuilder("SELECT s.* FROM symbols s WHERE s.workspace_id=@wid");
                var parms = new List<(string, object)> { ("@wid", workspaceId) };

                if (!string.IsNullOrWhiteSpace(query.NamePattern))
                {
                    sb.Append(" AND (s.name LIKE @np OR s.full_name LIKE @np)");
                    parms.Add(("@np", $"%{query.NamePattern}%"));
                }
                if (!string.IsNullOrWhiteSpace(query.Namespace))
                {
                    sb.Append(" AND s.namespace LIKE @ns");
                    parms.Add(("@ns", $"{query.Namespace}%"));
                }
                if (!string.IsNullOrWhiteSpace(query.SymbolType))
                {
                    sb.Append(" AND s.symbol_type=@syt");
                    parms.Add(("@syt", query.SymbolType));
                }
                if (query.ScopeType.HasValue)
                {
                    sb.Append(" AND s.scope_type=@st");
                    parms.Add(("@st", query.ScopeType.Value.ToString()));
                }
                if (!string.IsNullOrWhiteSpace(query.ScopeName))
                {
                    sb.Append(" AND s.scope_name=@sn");
                    parms.Add(("@sn", query.ScopeName));
                }

                sb.Append(" ORDER BY s.name LIMIT @lim");
                parms.Add(("@lim", query.MaxResults > 0 ? query.MaxResults : 100));

                using var cmd = conn.CreateCommand();
                cmd.CommandText = sb.ToString();
                foreach (var (name, val) in parms)
                    cmd.Parameters.AddWithValue(name, val ?? (object)DBNull.Value);

                var list = new List<SymbolInfo>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) list.Add(ReadSymbol(reader));
                return (IReadOnlyList<SymbolInfo>)list;
            }, ct);
        }

        /// <inheritdoc/>
        public Task<IReadOnlyList<SymbolInfo>> GetSymbolsByFileAsync(int fileId, CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT * FROM symbols WHERE file_id=@fid ORDER BY line_start";
                cmd.Parameters.AddWithValue("@fid", fileId);
                var list = new List<SymbolInfo>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) list.Add(ReadSymbol(reader));
                return (IReadOnlyList<SymbolInfo>)list;
            }, ct);
        }

        /// <inheritdoc/>
        public Task<IReadOnlyList<NamespaceEntry>> GetNamespacesAsync(
            int workspaceId, IndexScopeType? scopeType = null, CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                using var conn = OpenConnection();
                var sb = new StringBuilder(@"
                    SELECT s.namespace, s.scope_type, s.scope_name,
                           COUNT(DISTINCT s.file_id) AS file_count,
                           COUNT(*) AS symbol_count
                    FROM symbols s
                    WHERE s.workspace_id=@wid AND s.namespace IS NOT NULL AND s.namespace != ''");
                var parms = new List<(string, object)> { ("@wid", workspaceId) };

                if (scopeType.HasValue)
                {
                    sb.Append(" AND s.scope_type=@st");
                    parms.Add(("@st", scopeType.Value.ToString()));
                }

                sb.Append(" GROUP BY s.namespace, s.scope_type, s.scope_name ORDER BY s.namespace");

                using var cmd = conn.CreateCommand();
                cmd.CommandText = sb.ToString();
                foreach (var (name, val) in parms)
                    cmd.Parameters.AddWithValue(name, val ?? (object)DBNull.Value);

                var list = new List<NamespaceEntry>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    list.Add(new NamespaceEntry
                    {
                        Namespace   = reader.GetString(0),
                        ScopeType   = ParseEnum<IndexScopeType>(reader.IsDBNull(1) ? null : reader.GetString(1)),
                        ScopeName   = reader.IsDBNull(2) ? null : reader.GetString(2),
                        FileCount   = reader.GetInt32(3),
                        SymbolCount = reader.GetInt32(4),
                    });
                }
                return (IReadOnlyList<NamespaceEntry>)list;
            }, ct);
        }

        // ── Stats ───────────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public Task<IndexingStats> GetStatsAsync(int workspaceId, CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                using var conn = OpenConnection();
                var stats = new IndexingStats { WorkspaceId = workspaceId };

                stats.TotalFiles   = GetScalarInt(conn,
                    "SELECT COUNT(*) FROM files WHERE workspace_id=@wid", ("@wid", workspaceId));
                stats.TotalSymbols = GetScalarInt(conn,
                    "SELECT COUNT(*) FROM symbols WHERE workspace_id=@wid", ("@wid", workspaceId));
                stats.ErrorFiles   = GetScalarInt(conn,
                    "SELECT COUNT(*) FROM files WHERE workspace_id=@wid AND has_errors=1", ("@wid", workspaceId));

                // 按 scope_type 分组统计
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT r.scope_type, COUNT(DISTINCT f.id), COUNT(s.id)
                    FROM roots r
                    LEFT JOIN files f ON f.root_id=r.id
                    LEFT JOIN symbols s ON s.root_id=r.id
                    WHERE r.workspace_id=@wid
                    GROUP BY r.scope_type";
                cmd.Parameters.AddWithValue("@wid", workspaceId);
                var byScope = new Dictionary<string, (int files, int symbols)>();
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var scope = reader.IsDBNull(0) ? "unknown" : reader.GetString(0);
                        byScope[scope] = (reader.GetInt32(1), reader.GetInt32(2));
                    }
                }
                stats.FilesByScope   = byScope.ToDictionary(k => k.Key, v => v.Value.files);
                stats.SymbolsByScope = byScope.ToDictionary(k => k.Key, v => v.Value.symbols);

                return stats;
            }, ct);
        }

        /// <inheritdoc/>
        public Task ClearWorkspaceIndexAsync(int workspaceId, CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                lock (_writeLock)
                {
                    using var conn = OpenConnection();
                    using var tx = conn.BeginTransaction();
                    try
                    {
                        // 删除顺序：依赖 → 符号 → 文件（保留 workspace 和 root 元数据）
                        ExecuteNonQuery(conn,
                            "DELETE FROM symbol_dependencies WHERE workspace_id=@wid", ("@wid", workspaceId));
                        ExecuteNonQuery(conn,
                            "DELETE FROM symbols WHERE workspace_id=@wid", ("@wid", workspaceId));
                        ExecuteNonQuery(conn,
                            "DELETE FROM files WHERE workspace_id=@wid", ("@wid", workspaceId));
                        tx.Commit();
                    }
                    catch
                    {
                        tx.Rollback();
                        throw;
                    }
                }
            }, ct);
        }

        // ── Metadata ────────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public Task SetMetadataAsync(int workspaceId, string key, string value, CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                lock (_writeLock)
                {
                    using var conn = OpenConnection();
                    ExecuteNonQuery(conn, @"
                        INSERT INTO metadata(workspace_id, key, value) VALUES(@wid, @k, @v)
                        ON CONFLICT(workspace_id, key) DO UPDATE SET value=excluded.value;",
                        ("@wid", workspaceId), ("@k", key), ("@v", value));
                }
            }, ct);
        }

        /// <inheritdoc/>
        public Task<string> GetMetadataAsync(int workspaceId, string key, CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                using var conn = OpenConnection();
                return GetScalarString(conn,
                    "SELECT value FROM metadata WHERE workspace_id=@wid AND key=@k",
                    ("@wid", workspaceId), ("@k", key));
            }, ct);
        }

        // ── Dependencies ────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public Task BulkInsertDependenciesAsync(IEnumerable<SymbolDependency> deps, CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                var list = deps?.ToList();
                if (list == null || list.Count == 0) return;

                lock (_writeLock)
                {
                    using var conn = OpenConnection();
                    using var tx = conn.BeginTransaction();
                    try
                    {
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = @"
                            INSERT INTO symbol_dependencies(workspace_id, from_file_id, from_symbol_id,
                                to_type_name, to_symbol_id, dependency_kind, source_line)
                            VALUES(@wid, @ffid, @fsid, @ttn, @tsid, @dk, @sl)";

                        foreach (var dep in list)
                        {
                            cmd.Parameters.Clear();
                            cmd.Parameters.AddWithValue("@wid",  dep.WorkspaceId);
                            cmd.Parameters.AddWithValue("@ffid", dep.FromFileId);
                            cmd.Parameters.AddWithValue("@fsid", dep.FromSymbolId.HasValue
                                ? (object)dep.FromSymbolId.Value : DBNull.Value);
                            cmd.Parameters.AddWithValue("@ttn",  dep.ToTypeName);
                            cmd.Parameters.AddWithValue("@tsid", dep.ToSymbolId.HasValue
                                ? (object)dep.ToSymbolId.Value : DBNull.Value);
                            cmd.Parameters.AddWithValue("@dk",   dep.DependencyKind);
                            cmd.Parameters.AddWithValue("@sl",   dep.SourceLine);
                            cmd.ExecuteNonQuery();
                        }
                        tx.Commit();
                    }
                    catch
                    {
                        tx.Rollback();
                        throw;
                    }
                }
            }, ct);
        }

        /// <inheritdoc/>
        public Task DeleteDependenciesByFileAsync(int fileId, CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                lock (_writeLock)
                {
                    using var conn = OpenConnection();
                    ExecuteNonQuery(conn,
                        "DELETE FROM symbol_dependencies WHERE from_file_id=@fid", ("@fid", fileId));
                }
            }, ct);
        }

        /// <inheritdoc/>
        public Task<IReadOnlyList<SymbolDependency>> GetDependenciesAsync(
            int workspaceId, int fileId, int? symbolId = null, CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();

                if (symbolId.HasValue)
                {
                    cmd.CommandText = @"
                        SELECT * FROM symbol_dependencies
                        WHERE workspace_id=@wid AND from_file_id=@fid AND from_symbol_id=@sid
                        ORDER BY source_line";
                    cmd.Parameters.AddWithValue("@wid", workspaceId);
                    cmd.Parameters.AddWithValue("@fid", fileId);
                    cmd.Parameters.AddWithValue("@sid", symbolId.Value);
                }
                else
                {
                    cmd.CommandText = @"
                        SELECT * FROM symbol_dependencies
                        WHERE workspace_id=@wid AND from_file_id=@fid
                        ORDER BY source_line";
                    cmd.Parameters.AddWithValue("@wid", workspaceId);
                    cmd.Parameters.AddWithValue("@fid", fileId);
                }

                var list = new List<SymbolDependency>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) list.Add(ReadDependency(reader));
                return (IReadOnlyList<SymbolDependency>)list;
            }, ct);
        }

        /// <inheritdoc/>
        public Task<IReadOnlyList<SymbolDependency>> FindUsagesAsync(
            int workspaceId, string typeName, CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT * FROM symbol_dependencies
                    WHERE workspace_id=@wid AND to_type_name=@ttn
                    ORDER BY from_file_id, source_line";
                cmd.Parameters.AddWithValue("@wid", workspaceId);
                cmd.Parameters.AddWithValue("@ttn", typeName);

                var list = new List<SymbolDependency>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) list.Add(ReadDependency(reader));
                return (IReadOnlyList<SymbolDependency>)list;
            }, ct);
        }

        // ── Full-Text Search ────────────────────────────────────────────────────

        /// <inheritdoc/>
        public Task<IReadOnlyList<SymbolInfo>> SearchSymbolsByTextAsync(
            int workspaceId, string text, int maxResults = 50, CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    return (IReadOnlyList<SymbolInfo>)new List<SymbolInfo>();
                }

                using var conn = OpenConnection();

                // 尝试 FTS5 查询；如果 FTS5 不可用（旧 SQLite），降级为 LIKE
                try
                {
                    using var cmd = conn.CreateCommand();
                    // FTS5 MATCH 语法：支持前缀搜索（term*）
                    var ftsQuery = BuildFtsQuery(text);
                    cmd.CommandText = @"
                        SELECT s.* FROM symbols s
                        INNER JOIN symbols_fts fts ON fts.rowid = s.id
                        WHERE s.workspace_id=@wid AND symbols_fts MATCH @q
                        ORDER BY rank
                        LIMIT @lim";
                    cmd.Parameters.AddWithValue("@wid", workspaceId);
                    cmd.Parameters.AddWithValue("@q",   ftsQuery);
                    cmd.Parameters.AddWithValue("@lim", maxResults);

                    var list = new List<SymbolInfo>();
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read()) list.Add(ReadSymbol(reader));
                    return (IReadOnlyList<SymbolInfo>)list;
                }
                catch (SqliteException)
                {
                    // FTS5 不可用，降级为 LIKE 模糊匹配
                    return FallbackLikeSearch(conn, workspaceId, text, maxResults);
                }
            }, ct);
        }

        // ── IDisposable ─────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public void Dispose()
        {
            // 连接按需打开/关闭，无需额外清理
        }

        // ── 私有辅助方法 ────────────────────────────────────────────────────────

        /// <summary>打开一个新的 SQLite 连接。</summary>
        private SqliteConnection OpenConnection()
        {
            var conn = new SqliteConnection($"Data Source={_dbPath};Version=3;");
            conn.Open();
            // 每次连接都开启外键约束
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA foreign_keys=ON;";
            cmd.ExecuteNonQuery();
            return conn;
        }

        /// <summary>执行无返回值的 SQL 命令。</summary>
        private static void ExecuteNonQuery(SqliteConnection conn, string sql,
            params (string name, object value)[] parms)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, val) in parms)
                cmd.Parameters.AddWithValue(name, val ?? (object)DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        /// <summary>执行返回单个整数的 SQL 查询。</summary>
        private static int GetScalarInt(SqliteConnection conn, string sql,
            params (string name, object value)[] parms)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, val) in parms)
                cmd.Parameters.AddWithValue(name, val ?? (object)DBNull.Value);
            var result = cmd.ExecuteScalar();
            return result == null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
        }

        /// <summary>执行返回单个字符串的 SQL 查询。</summary>
        private static string GetScalarString(SqliteConnection conn, string sql,
            params (string name, object value)[] parms)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, val) in parms)
                cmd.Parameters.AddWithValue(name, val ?? (object)DBNull.Value);
            var result = cmd.ExecuteScalar();
            return result == null || result == DBNull.Value ? null : result.ToString();
        }

        /// <summary>执行返回单个 long 的 SQL 查询（用于 last_insert_rowid() 等）。</summary>
        private static long GetScalarLong(SqliteConnection conn, string sql,
            params (string name, object value)[] parms)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, val) in parms)
                cmd.Parameters.AddWithValue(name, val ?? (object)DBNull.Value);
            var result = cmd.ExecuteScalar();
            return result == null || result == DBNull.Value ? 0L : Convert.ToInt64(result);
        }

        /// <summary>将文件路径规范化（反斜杠 → 正斜杠）。</summary>
        private static string NormalizePath(string path) => path?.Replace('\\', '/');

        /// <summary>构建 FTS5 MATCH 查询字符串（支持多词前缀搜索）。</summary>
        private static string BuildFtsQuery(string text)
        {
            // 将输入拆分为词，每个词加 * 前缀匹配
            var tokens = text.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) return text;
            // FTS5 前缀搜索：term* 匹配所有以 term 开头的词
            return string.Join(" ", tokens.Select(t => EscapeFtsToken(t) + "*"));
        }

        /// <summary>转义 FTS5 特殊字符。</summary>
        private static string EscapeFtsToken(string token)
        {
            // FTS5 特殊字符：" ^ * : 等，用双引号包裹
            if (token.Any(c => "\"^*:()".Contains(c)))
                return $"\"{token.Replace("\"", "\"\"")}\"";
            return token;
        }

        /// <summary>FTS5 不可用时的降级 LIKE 搜索。</summary>
        private static IReadOnlyList<SymbolInfo> FallbackLikeSearch(
            SqliteConnection conn, int workspaceId, string text, int maxResults)
        {
            var lower = $"%{text}%";
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT * FROM symbols
                WHERE workspace_id=@wid AND (
                    name LIKE @q OR full_name LIKE @q OR namespace LIKE @q OR signature LIKE @q
                )
                ORDER BY name LIMIT @lim";
            cmd.Parameters.AddWithValue("@wid", workspaceId);
            cmd.Parameters.AddWithValue("@q",   lower);
            cmd.Parameters.AddWithValue("@lim", maxResults);

            var list = new List<SymbolInfo>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) list.Add(ReadSymbol(reader));
            return list;
        }

        // ── Reader 辅助方法 ─────────────────────────────────────────────────────

        private static IndexWorkspace ReadWorkspace(SqliteDataReader r) => new IndexWorkspace
        {
            Id              = r.GetInt32(r.GetOrdinal("id")),
            Fingerprint     = GetString(r, "fingerprint"),
            WorkspaceRoot   = GetString(r, "workspace_root"),
            DisplayName     = GetString(r, "display_name"),
            VcsType         = GetString(r, "vcs_type"),
            VcsRootPath     = GetString(r, "vcs_root_path"),
            VcsUrl          = GetString(r, "vcs_url"),
            RepositoryRoot  = GetString(r, "repository_root"),
            BranchId        = GetString(r, "branch_id"),
            Revision        = GetString(r, "revision"),
        };

        private static IndexRoot ReadRoot(SqliteDataReader r) => new IndexRoot
        {
            Id                    = r.GetInt32(r.GetOrdinal("id")),
            WorkspaceId           = r.GetInt32(r.GetOrdinal("workspace_id")),
            RootPath              = GetString(r, "root_path"),
            RelativeToWorkspace   = GetString(r, "relative_to_workspace"),
            DisplayName           = GetString(r, "display_name"),
            ScopeType             = ParseEnum<IndexScopeType>(GetString(r, "scope_type")),
            ScopeName             = GetString(r, "scope_name"),
            Role                  = ParseEnum<IndexRootRole>(GetString(r, "role")),
            ReadOnly              = GetInt(r, "read_only") == 1,
            IsEnabled             = GetInt(r, "is_enabled") == 1,
            IsDefaultSearchScope  = GetInt(r, "is_default_search_scope") == 1,
            ProviderId            = GetString(r, "provider_id"),
        };

        private static IndexedFile ReadFile(SqliteDataReader r) => new IndexedFile
        {
            Id              = r.GetInt32(r.GetOrdinal("id")),
            WorkspaceId     = r.GetInt32(r.GetOrdinal("workspace_id")),
            RootId          = r.GetInt32(r.GetOrdinal("root_id")),
            FilePath        = GetString(r, "file_path"),
            RelativeToRoot  = GetString(r, "relative_to_root"),
            ContentHash     = GetString(r, "content_hash"),
            LastModified    = GetLong(r, "last_modified"),
            LastIndexed     = GetLong(r, "last_indexed"),
            FileSize        = GetLong(r, "file_size"),
            HasErrors       = GetInt(r, "has_errors") == 1,
            ErrorMessage    = GetString(r, "error_message"),
            SymbolCount     = GetInt(r, "symbol_count"),
        };

        private static SymbolInfo ReadSymbol(SqliteDataReader r)
        {
            var sym = new SymbolInfo
            {
                Id              = r.GetInt32(r.GetOrdinal("id")),
                FileId          = r.GetInt32(r.GetOrdinal("file_id")),
                WorkspaceId     = r.GetInt32(r.GetOrdinal("workspace_id")),
                RootId          = r.GetInt32(r.GetOrdinal("root_id")),
                ScopeName       = GetString(r, "scope_name"),
                BranchId        = GetString(r, "branch_id"),
                Name            = GetString(r, "name"),
                FullName        = GetString(r, "full_name"),
                Namespace       = GetString(r, "namespace"),
                SymbolType      = GetString(r, "symbol_type"),
                AccessModifier  = GetString(r, "access_modifier"),
                IsStatic        = GetInt(r, "is_static") == 1,
                IsAbstract      = GetInt(r, "is_abstract") == 1,
                IsPartial       = GetInt(r, "is_partial") == 1,
                BaseType        = GetString(r, "base_type"),
                LineStart       = GetInt(r, "line_start"),
                LineEnd         = GetInt(r, "line_end"),
                Signature       = GetString(r, "signature"),
            };

            var scopeTypeStr = GetString(r, "scope_type");
            if (!string.IsNullOrEmpty(scopeTypeStr))
                sym.ScopeType = ParseEnum<IndexScopeType>(scopeTypeStr);

            var ifacesJson = GetString(r, "interfaces");
            if (!string.IsNullOrEmpty(ifacesJson))
                sym.Interfaces = JsonConvert.DeserializeObject<string[]>(ifacesJson);

            var attrsJson = GetString(r, "attributes");
            if (!string.IsNullOrEmpty(attrsJson))
                sym.Attributes = JsonConvert.DeserializeObject<string[]>(attrsJson);

            return sym;
        }

        private static SymbolDependency ReadDependency(SqliteDataReader r) => new SymbolDependency
        {
            Id             = r.GetInt32(r.GetOrdinal("id")),
            WorkspaceId    = r.GetInt32(r.GetOrdinal("workspace_id")),
            FromFileId     = r.GetInt32(r.GetOrdinal("from_file_id")),
            FromSymbolId   = r.IsDBNull(r.GetOrdinal("from_symbol_id"))
                ? (int?)null : r.GetInt32(r.GetOrdinal("from_symbol_id")),
            ToTypeName     = GetString(r, "to_type_name"),
            ToSymbolId     = r.IsDBNull(r.GetOrdinal("to_symbol_id"))
                ? (int?)null : r.GetInt32(r.GetOrdinal("to_symbol_id")),
            DependencyKind = GetString(r, "dependency_kind"),
            SourceLine     = GetInt(r, "source_line"),
        };

        // ── 通用 Reader 工具 ────────────────────────────────────────────────────

        private static string GetString(SqliteDataReader r, string col)
        {
            var ord = r.GetOrdinal(col);
            return r.IsDBNull(ord) ? null : r.GetString(ord);
        }

        private static int GetInt(SqliteDataReader r, string col)
        {
            var ord = r.GetOrdinal(col);
            return r.IsDBNull(ord) ? 0 : r.GetInt32(ord);
        }

        private static long GetLong(SqliteDataReader r, string col)
        {
            var ord = r.GetOrdinal(col);
            return r.IsDBNull(ord) ? 0L : r.GetInt64(ord);
        }

        private static T ParseEnum<T>(string value) where T : struct
        {
            if (string.IsNullOrEmpty(value)) return default;
            return Enum.TryParse<T>(value, true, out var result) ? result : default;
        }
    }
}
#endif // AGENTCORE_SQLITE
