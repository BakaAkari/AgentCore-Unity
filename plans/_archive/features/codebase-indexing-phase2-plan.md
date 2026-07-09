# Codebase Indexing Phase 2 — SQLite 迁移 + 依赖图构建

**版本目标**: v0.9.3
**状态**: ✅ 已确认，进入实现阶段
**日期**: 2026-06-04（确认: 2026-06-08）
**前置条件**: v0.9.1 文件级索引 + 符号检索已完成；`IIndexStore` 接口完全抽象

---

## 1. 目标与范围

### 1.1 本次要做什么

| # | 目标 | 说明 |
|---|------|------|
| 1 | **SQLite 存储迁移** | 新增 `SqliteIndexStore` 实现 `IIndexStore`，替换 `JsonlIndexStore` 作为默认后端 |
| 2 | **排除目录补全** | 补充 `Build/`, `Builds/`, `Logs/`, `.svn/`, `.git/` 到默认 `ExcludePatterns` |
| 3 | **依赖图构建** | 分析 C# 类型引用关系，存储到 SQLite；支持正向/反向查询 |
| 4 | **`search_code` 工具扩展** | 新增 5 个 action：`get_dependencies`、`find_usages`、`get_dependency_graph`、`get_symbol_context`、`search_text` |
| 5 | **`IIndexStore` 接口扩展** | 新增依赖图相关存储接口 |
| 6 | **FTS5 全文搜索预留** | SQLite schema 中新增 `symbols_fts` 虚拟表，支持 `search_text` action 全文模糊搜索 |

### 1.2 本次不做什么（范围边界）

- ❌ 不引入向量数据库（Qdrant 等）
- ❌ 不做语义相似度搜索（Embedding）
- ❌ 不做 SemanticModel 级深层分析（保持 SyntaxTree 级）
- ❌ 不做 Unity 资源依赖（.prefab/.asset 引用关系）— 推迟到 Phase 3
- ❌ 不做跨 WorkspaceRoot 的依赖分析 — 推迟到 Phase 3
- ❌ 不修改 `JsonlIndexStore`（保留作为降级/测试后端）

---

## 2. 架构设计

### 2.1 整体架构图

```
Editor/Indexing/
├── Core/
│   ├── IIndexStore.cs              ← 扩展依赖图接口（新增 4 个方法）
│   ├── JsonlIndexStore.cs          ← 保留，降级/测试用
│   ├── SqliteIndexStore.cs         ← 新增，默认后端
│   ├── IndexStoreFactory.cs        ← 新增，根据配置创建存储实例
│   ├── CodebaseIndexer.cs          ← 修改：集成依赖图提取
│   ├── DependencyExtractor.cs      ← 新增，SyntaxTree 级依赖提取
│   └── RoslynSymbolExtractor.cs    ← 不变
├── Models/
│   ├── SymbolDependency.cs         ← 新增，依赖关系数据模型
│   └── ...（现有模型不变）
└── Tools/
    └── SearchCodeTool.cs           ← 修改：新增 3 个 action
```

### 2.2 SQLite 数据库 Schema

```sql
-- 现有表（从 JSONL 迁移，结构不变）
CREATE TABLE workspaces (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    fingerprint TEXT    NOT NULL UNIQUE,
    workspace_root TEXT,
    display_name TEXT,
    vcs_type    TEXT,
    vcs_root_path TEXT,
    vcs_url     TEXT,
    repository_root TEXT,
    branch_id   TEXT,
    revision    TEXT
);

CREATE TABLE roots (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    workspace_id INTEGER NOT NULL REFERENCES workspaces(id),
    root_path    TEXT    NOT NULL,
    relative_to_workspace TEXT,
    display_name TEXT,
    scope_type   TEXT,
    scope_name   TEXT,
    role         TEXT,
    read_only    INTEGER,
    is_enabled   INTEGER,
    is_default_search_scope INTEGER,
    provider_id  TEXT,
    UNIQUE(workspace_id, root_path)
);

CREATE TABLE files (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    workspace_id INTEGER NOT NULL REFERENCES workspaces(id),
    root_id      INTEGER NOT NULL REFERENCES roots(id),
    file_path    TEXT    NOT NULL,
    relative_to_root TEXT,
    content_hash TEXT,
    last_modified INTEGER,
    last_indexed  INTEGER,
    file_size    INTEGER,
    has_errors   INTEGER,
    error_message TEXT,
    symbol_count INTEGER,
    UNIQUE(workspace_id, file_path)
);

CREATE TABLE symbols (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    file_id      INTEGER NOT NULL REFERENCES files(id),
    workspace_id INTEGER NOT NULL,
    root_id      INTEGER NOT NULL,
    scope_type   TEXT,
    scope_name   TEXT,
    branch_id    TEXT,
    name         TEXT    NOT NULL,
    full_name    TEXT,
    namespace    TEXT,
    symbol_type  TEXT,
    access_modifier TEXT,
    is_static    INTEGER,
    is_abstract  INTEGER,
    is_partial   INTEGER,
    base_type    TEXT,
    interfaces   TEXT,   -- JSON 数组
    line_start   INTEGER,
    line_end     INTEGER,
    signature    TEXT,
    attributes   TEXT    -- JSON 数组
);

-- 新增：依赖关系表（Phase 2 核心）
CREATE TABLE symbol_dependencies (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    workspace_id    INTEGER NOT NULL,
    from_file_id    INTEGER NOT NULL REFERENCES files(id),
    from_symbol_id  INTEGER,          -- 可为 NULL（文件级依赖）
    to_type_name    TEXT    NOT NULL, -- 被引用的类型全名（可能未在本 workspace 索引）
    to_symbol_id    INTEGER,          -- 解析后的目标符号 ID（可为 NULL，表示外部类型）
    dependency_kind TEXT    NOT NULL, -- inheritance / interface_impl / field_type / method_param / method_return / local_var / attribute / generic_arg
    source_line     INTEGER
);

-- 索引（查询性能关键）
CREATE INDEX idx_symbols_workspace ON symbols(workspace_id);
CREATE INDEX idx_symbols_name ON symbols(name);
CREATE INDEX idx_symbols_full_name ON symbols(full_name);
CREATE INDEX idx_symbols_file ON symbols(file_id);
CREATE INDEX idx_files_workspace ON files(workspace_id);
CREATE INDEX idx_files_root ON files(root_id);
CREATE INDEX idx_deps_from_file ON symbol_dependencies(from_file_id);
CREATE INDEX idx_deps_from_symbol ON symbol_dependencies(from_symbol_id);
CREATE INDEX idx_deps_to_type ON symbol_dependencies(to_type_name);
CREATE INDEX idx_deps_to_symbol ON symbol_dependencies(to_symbol_id);
CREATE INDEX idx_deps_workspace ON symbol_dependencies(workspace_id);

-- FTS5 全文搜索虚拟表（借鉴 codedb word/trigram index 思路）
-- 对 symbols 表的 name、full_name、namespace、signature 做全文索引
-- content= 模式：FTS 表不存储原始数据，查询时回查 symbols 表
CREATE VIRTUAL TABLE symbols_fts USING fts5(
    name,
    full_name,
    namespace,
    signature,
    content='symbols',
    content_rowid='id'
);

-- FTS5 触发器：保持 symbols_fts 与 symbols 表同步
CREATE TRIGGER symbols_ai AFTER INSERT ON symbols BEGIN
    INSERT INTO symbols_fts(rowid, name, full_name, namespace, signature)
    VALUES (new.id, new.name, new.full_name, new.namespace, new.signature);
END;
CREATE TRIGGER symbols_ad AFTER DELETE ON symbols BEGIN
    INSERT INTO symbols_fts(symbols_fts, rowid, name, full_name, namespace, signature)
    VALUES ('delete', old.id, old.name, old.full_name, old.namespace, old.signature);
END;
CREATE TRIGGER symbols_au AFTER UPDATE ON symbols BEGIN
    INSERT INTO symbols_fts(symbols_fts, rowid, name, full_name, namespace, signature)
    VALUES ('delete', old.id, old.name, old.full_name, old.namespace, old.signature);
    INSERT INTO symbols_fts(rowid, name, full_name, namespace, signature)
    VALUES (new.id, new.name, new.full_name, new.namespace, new.signature);
END;

-- 元数据表
CREATE TABLE metadata (
    workspace_id INTEGER NOT NULL,
    key          TEXT    NOT NULL,
    value        TEXT,
    PRIMARY KEY(workspace_id, key)
);
```

### 2.3 依赖关系类型（`dependency_kind`）

SyntaxTree 级可提取的依赖类型：

| Kind | 说明 | 示例 |
|------|------|------|
| `inheritance` | 继承基类 | `class A : B` |
| `interface_impl` | 实现接口 | `class A : IB` |
| `field_type` | 字段类型引用 | `private PlayerController _player;` |
| `method_param` | 方法参数类型 | `void Foo(PlayerController p)` |
| `method_return` | 方法返回类型 | `PlayerController GetPlayer()` |
| `attribute` | Attribute 引用 | `[SerializeField]` |
| `generic_arg` | 泛型参数 | `List<PlayerController>` |
| `using_directive` | using 引用（命名空间级） | `using UnityEngine;` |

> **不提取**：方法体内局部变量类型、lambda、匿名类型（与 `RoslynSymbolExtractor` 保持一致）

---

## 3. 新增组件详细设计

### 3.1 `DependencyExtractor`（新增）

**位置**: `Editor/Indexing/Core/DependencyExtractor.cs`  
**职责**: 从已解析的 SyntaxTree 中提取类型依赖关系

```csharp
namespace AgentCore.Editor.Components.Indexing.Core
{
    /// <summary>
    /// 从 C# SyntaxTree 中提取类型依赖关系（SyntaxTree 级，不使用 SemanticModel）。
    /// 提取的依赖类型：继承、接口实现、字段类型、方法参数/返回类型、Attribute、泛型参数。
    /// </summary>
    public static class DependencyExtractor
    {
        public static IReadOnlyList<SymbolDependency> ExtractFromFile(
            string filePath,
            int fileId,
            int workspaceId,
            SyntaxTree syntaxTree);
    }
}
```

**关键逻辑**：
1. 遍历所有 `TypeDeclarationSyntax`（class/interface/struct）
2. 提取 `BaseList`（继承 + 接口实现）
3. 遍历 `FieldDeclarationSyntax` 提取字段类型
4. 遍历 `MethodDeclarationSyntax` 提取参数类型和返回类型
5. 遍历 `AttributeListSyntax` 提取 Attribute 引用
6. 处理泛型类型（`GenericNameSyntax`）展开泛型参数
7. 过滤掉 C# 内置类型（`string`, `int`, `bool`, `void` 等）和 `System.*` 基础类型

### 3.2 `SymbolDependency`（新增模型）

**位置**: `Editor/Indexing/Models/SymbolDependency.cs`

```csharp
namespace AgentCore.Editor.Components.Indexing.Models
{
    public sealed class SymbolDependency
    {
        public int Id { get; set; }
        public int WorkspaceId { get; set; }
        public int FromFileId { get; set; }
        public int? FromSymbolId { get; set; }   // null = 文件级依赖
        public string ToTypeName { get; set; }   // 被引用类型的简名或全名
        public int? ToSymbolId { get; set; }     // 解析后的目标 ID（null = 外部类型）
        public string DependencyKind { get; set; }
        public int SourceLine { get; set; }
    }
}
```

### 3.3 `IIndexStore` 扩展（新增 4 个方法）

在现有 `IIndexStore` 接口末尾新增：

```csharp
// ── Dependencies ───────────────────────────────────────────────────────────

/// <summary>批量插入依赖关系记录。</summary>
Task BulkInsertDependenciesAsync(IEnumerable<SymbolDependency> deps, CancellationToken ct = default);

/// <summary>删除指定文件的所有依赖关系记录。</summary>
Task DeleteDependenciesByFileAsync(int fileId, CancellationToken ct = default);

/// <summary>
/// 查询指定符号/文件的正向依赖（该符号引用了哪些类型）。
/// </summary>
Task<IReadOnlyList<SymbolDependency>> GetDependenciesAsync(
    int workspaceId, int fileId, int? symbolId = null, CancellationToken ct = default);

/// <summary>
/// 查询指定类型名称的反向依赖（哪些符号/文件引用了该类型）。
/// </summary>
Task<IReadOnlyList<SymbolDependency>> FindUsagesAsync(
    int workspaceId, string typeName, CancellationToken ct = default);
```

### 3.4 `SqliteIndexStore`（新增）

**位置**: `Editor/Indexing/Core/SqliteIndexStore.cs`  
**依赖**: `sqlite-net-pcl`（通过 NuGet 引入，纯 C# 实现）

**关键设计决策**：
- 使用 `sqlite-net-pcl` 的 `SQLiteAsyncConnection`，所有操作异步
- 数据库文件路径：`{Application.persistentDataPath}/AgentCore/index.db`（与 JSONL 存储目录一致）
- 首次打开时自动执行 `CREATE TABLE IF NOT EXISTS`
- 版本号变更时执行 `DROP TABLE + CREATE TABLE`（全量重建，与现有逻辑一致）
- `Dispose()` 时关闭连接

**`IndexStoreFactory`（新增）**：

```csharp
/// <summary>
/// 根据配置创建 IIndexStore 实例。
/// 默认使用 SQLite；如果 SQLite 初始化失败，自动降级到 JSONL。
/// </summary>
public static class IndexStoreFactory
{
    public static IIndexStore Create(string dbDir)
    {
        try
        {
            var store = new SqliteIndexStore(dbDir);
            store.EnsureInitialized(); // 验证 SQLite 可用
            return store;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[IndexStoreFactory] SQLite unavailable, falling back to JSONL: {ex.Message}");
            return new JsonlIndexStore(dbDir);
        }
    }
}
```

> **降级策略**：如果 SQLite 初始化失败（极少数情况），自动降级到 JSONL，保证开箱即用。

### 3.5 `search_code` 工具新增 3 个 action

在 [`SearchCodeTool`](Editor/Indexing/Tools/SearchCodeTool.cs) 中新增：

| Action | 参数 | 返回 | 说明 |
|--------|------|------|------|
| `get_dependencies` | `file_path` 或 `symbol_name` + `scope_name`（可选） | 依赖列表 | 查询某文件/符号引用了哪些类型 |
| `find_usages` | `type_name`（必填）+ `scope_name`（可选） | 引用位置列表 | 查询某类型被哪些文件/符号引用 |
| `get_dependency_graph` | `scope_name`（必填）+ `max_depth`（默认 2） | 图摘要 JSON | 获取指定 Scope 的依赖关系摘要 |
| `get_symbol_context` | `symbol_name`（必填）+ `scope_name`（可选） | 完整上下文包 | 一次返回：符号定义 + 正向依赖 + 反向引用 + 同文件符号列表（借鉴 codedb context tool） |
| `search_text` | `query`（必填）+ `scope_name`（可选）+ `max_results`（默认 20） | 符号列表 | FTS5 全文模糊搜索符号名/签名/命名空间（补充精确搜索的盲区） |

**`get_dependencies` 返回示例**：
```json
{
  "file": "Assets/Scripts/Battle/PlayerController.cs",
  "symbol": "PlayerController",
  "dependencies": [
    { "to_type": "MonoBehaviour", "kind": "inheritance", "line": 8 },
    { "to_type": "IAttackable", "kind": "interface_impl", "line": 8 },
    { "to_type": "WeaponSystem", "kind": "field_type", "line": 15 },
    { "to_type": "HealthComponent", "kind": "field_type", "line": 16 }
  ]
}
```

**`find_usages` 返回示例**：
```json
{
  "type_name": "PlayerController",
  "usages": [
    { "file": "Battle/GameManager.cs", "symbol": "GameManager", "kind": "field_type", "line": 23 },
    { "file": "Battle/CameraFollow.cs", "symbol": "CameraFollow", "kind": "field_type", "line": 11 }
  ],
  "total": 2
}
```

**`get_symbol_context` 返回示例**（借鉴 codedb context tool，一次调用获取完整上下文）：
```json
{
  "symbol": {
    "name": "PlayerController",
    "full_name": "Battle.PlayerController",
    "namespace": "Battle",
    "symbol_type": "class",
    "file": "Assets/Scripts/Battle/PlayerController.cs",
    "line_start": 8,
    "signature": "public class PlayerController : MonoBehaviour, IAttackable"
  },
  "dependencies": [
    { "to_type": "MonoBehaviour", "kind": "inheritance", "line": 8 },
    { "to_type": "IAttackable", "kind": "interface_impl", "line": 8 },
    { "to_type": "WeaponSystem", "kind": "field_type", "line": 15 }
  ],
  "usages": [
    { "file": "Battle/GameManager.cs", "symbol": "GameManager", "kind": "field_type", "line": 23 },
    { "file": "Battle/CameraFollow.cs", "symbol": "CameraFollow", "kind": "field_type", "line": 11 }
  ],
  "sibling_symbols": [
    { "name": "PlayerState", "symbol_type": "enum", "line_start": 3 },
    { "name": "PlayerController", "symbol_type": "class", "line_start": 8 }
  ]
}
```

**`search_text` 返回示例**（FTS5 全文模糊搜索）：
```json
{
  "query": "attack damage",
  "results": [
    { "name": "AttackDamageCalculator", "full_name": "Battle.AttackDamageCalculator", "symbol_type": "class", "file": "Battle/AttackDamageCalculator.cs", "rank": 0.95 },
    { "name": "CalculateDamage", "full_name": "Battle.AttackDamageCalculator.CalculateDamage", "symbol_type": "method", "file": "Battle/AttackDamageCalculator.cs", "rank": 0.87 }
  ],
  "total": 2
}
```

---

## 4. 排除目录补全

修改 [`IndexRoot.ExcludePatterns`](Editor/Indexing/Models/IndexRoot.cs:44) 默认值：

```csharp
// 修改前
public List<string> ExcludePatterns { get; set; } = new List<string>
{
    "bin/", "obj/", "Library/", "Temp/", "Generated/"
};

// 修改后
public List<string> ExcludePatterns { get; set; } = new List<string>
{
    // Unity 构建/缓存目录
    "Library/", "Temp/", "Build/", "Builds/", "Logs/",
    // C# 构建目录
    "bin/", "obj/",
    // 生成代码
    "Generated/",
    // VCS 元数据
    ".svn/", ".git/",
}
```

---

## 5. NuGet 依赖引入方案

### 5.1 `sqlite-net-pcl` 引入方式

由于 Unity 没有官方的 `com.unity.nuget.sqlite-net-pcl` 包，采用以下方案：

**方案：将 `sqlite-net-pcl` DLL 直接内嵌到包中**

1. 从 NuGet 下载 `sqlite-net-pcl`（当前版本 1.9.172）
2. 解压获取 `SQLite-net.dll`（纯 C# 程序集，无 native 依赖）
3. 放置到 `Editor/Plugins/sqlite-net-pcl/SQLite-net.dll`
4. 配置 `.meta` 文件：`Editor` 平台 only，`Any CPU`

**为什么这样可行**：
- `sqlite-net-pcl` 是纯 C# 实现，底层通过 P/Invoke 调用系统 SQLite
- Windows 10+ 自带 `winsqlite3.dll`，macOS 自带 `libsqlite3.dylib`
- Unity Editor 运行在 Windows/macOS，系统 SQLite 始终可用
- 无需用户安装任何额外软件

**目录结构**：
```
Editor/
└── Plugins/
    └── sqlite-net-pcl/
        ├── SQLite-net.dll
        └── SQLite-net.dll.meta   ← Editor only, Any CPU
```

### 5.2 `package.json` 变更

`package.json` 无需修改（DLL 直接内嵌，不通过 UPM 依赖声明）。

---

## 6. CodebaseIndexer 修改

在 [`CodebaseIndexer.IndexFileAsync`](Editor/Indexing/Core/CodebaseIndexer.cs:592) 中集成依赖提取：

```
IndexFileAsync(filePath)
  → RoslynSymbolExtractor.ExtractFromFile()  ← 现有，提取符号
  → DependencyExtractor.ExtractFromFile()    ← 新增，提取依赖
  → store.UpsertFileAsync()                  ← 现有
  → store.BulkInsertSymbolsAsync()           ← 现有
  → store.BulkInsertDependenciesAsync()      ← 新增
```

增量索引时，在删除旧符号的同时删除旧依赖：
```
DeleteSymbolsByFileAsync(fileId)      ← 现有
DeleteDependenciesByFileAsync(fileId) ← 新增
```

---

## 7. 数据迁移策略

**从 JSONL 到 SQLite 的迁移**：

- **不做自动迁移**：JSONL 数据直接废弃，首次使用 SQLite 后端时触发全量重建
- **触发条件**：`IndexStoreFactory.Create()` 检测到 SQLite 后端可用时，`CurrentIndexVersion` 递增（`"1"` → `"2"`），`CodebaseIndexer` 检测到版本变更后自动执行全量重建
- **用户感知**：Settings 页面显示"索引已升级，正在重建..."进度提示

---

## 8. 涉及文件清单

### 新增文件

| 文件 | 说明 |
|------|------|
| `Editor/Indexing/Core/SqliteIndexStore.cs` | SQLite 存储后端实现 |
| `Editor/Indexing/Core/IndexStoreFactory.cs` | 存储后端工厂（含降级逻辑） |
| `Editor/Indexing/Core/DependencyExtractor.cs` | SyntaxTree 级依赖提取 |
| `Editor/Indexing/Models/SymbolDependency.cs` | 依赖关系数据模型 |
| `Editor/Plugins/sqlite-net-pcl/SQLite-net.dll` | sqlite-net-pcl 程序集 |
| `Editor/Plugins/sqlite-net-pcl/SQLite-net.dll.meta` | DLL 导入配置 |

### 修改文件

| 文件 | 修改内容 |
|------|---------|
| `Editor/Indexing/Core/IIndexStore.cs` | 新增 4 个依赖图接口方法 |
| `Editor/Indexing/Core/CodebaseIndexer.cs` | 集成 `DependencyExtractor`；使用 `IndexStoreFactory` |
| `Editor/Indexing/Models/IndexRoot.cs` | 补全默认 `ExcludePatterns` |
| `Editor/Indexing/Tools/SearchCodeTool.cs` | 新增 3 个 action + 更新 Schema |
| `Editor/Bootstrap/Resources/TOOLS.md.template` | 更新 `search_code` 工具说明 |
| `CHANGELOG.md` | 新增 v0.9.3 条目 |
| `package.json` | 版本号 `0.9.2` → `0.9.3` |
| `plans/ROADMAP.md` | 标记 6.2.3 为完成，更新里程碑 |

### 不修改文件

| 文件 | 原因 |
|------|------|
| `Editor/Indexing/Core/JsonlIndexStore.cs` | 保留作为降级后端 |
| `Editor/Indexing/Core/RoslynSymbolExtractor.cs` | 不变，依赖提取独立实现 |
| `Editor/Indexing/Core/SymbolSearcher.cs` | 不变 |
| `Editor/Indexing/UI/` | 不变（进度显示逻辑不变） |

---

## 9. 版本号与变更日志

**版本**: `0.9.2` → `0.9.3`

**CHANGELOG 草稿**：

```markdown
## [0.9.3] - 2026-XX-XX

### Added
- 代码索引 SQLite 存储后端（SqliteIndexStore），替代 JSONL 作为默认后端；JSONL 保留作为降级后端
- 依赖图构建：CodebaseIndexer 现在提取 C# 类型依赖关系（继承、接口实现、字段类型、方法参数/返回类型、Attribute、泛型参数）
- search_code 工具新增 5 个 action：get_dependencies（正向依赖查询）、find_usages（反向引用查询）、get_dependency_graph（Scope 依赖图摘要）、get_symbol_context（一次获取符号完整上下文）、search_text（FTS5 全文模糊搜索）
- IndexStoreFactory：SQLite 不可用时自动降级到 JSONL，保证开箱即用
- SQLite FTS5 全文搜索虚拟表（symbols_fts），支持对符号名/签名/命名空间做全文模糊搜索

### Changed
- IndexRoot 默认排除目录补全：新增 Build/、Builds/、Logs/、.svn/、.git/
- 索引版本号升级（1 → 2），首次使用新版本时自动触发全量重建
```

---

## 10. 验收标准（测试 Checklist）

### Round 1 — Happy Path

- [ ] 安装插件后，Settings 页面显示"SQLite"存储后端标识
- [ ] 执行 `search_code index_full`，索引完成后 `get_stats` 显示文件数和符号数
- [ ] `search_code get_dependencies` 对 `PlayerController` 返回正确的依赖列表
- [ ] `search_code find_usages` 对常用类型返回正确的引用位置
- [ ] `search_code get_dependency_graph` 对某个 Scope 返回依赖摘要
- [ ] `search_code get_symbol_context` 对某个类一次返回：定义 + 依赖 + 被引用 + 同文件符号
- [ ] `search_code search_text` 用模糊关键词（如 "attack damage"）返回相关符号列表

### Round 2 — 边界与容错

- [ ] `Build/` 目录下的 .cs 文件不被索引
- [ ] `.svn/` 目录下的文件不被索引
- [ ] SQLite 初始化失败时自动降级到 JSONL（可通过删除 DLL 模拟）
- [ ] `find_usages` 查询不存在的类型返回空列表而非报错
- [ ] 超大文件（>1MB）被跳过，不影响其他文件索引

### Round 3 — 核心链路

- [ ] Domain Reload 后索引数据仍然可查（SQLite 文件持久化）
- [ ] 增量索引后依赖关系正确更新（修改文件后旧依赖被删除，新依赖被插入）
- [ ] 全量重建后依赖图数据完整

### Round 4 — 实际场景

- [ ] 在真实企业 Unity 项目中执行全量索引，耗时在可接受范围内
- [ ] `find_usages` 帮助 LLM 正确识别某类型的所有使用位置
- [ ] `get_dependency_graph` 帮助 LLM 理解某 Scope 的架构依赖关系

---

## 11. 风险评估

| 风险 | 可能性 | 影响 | 缓解措施 |
|------|--------|------|---------|
| sqlite-net-pcl DLL 与 Unity 版本冲突 | 低 | 高 | `IndexStoreFactory` 自动降级到 JSONL |
| 依赖提取误报（将内置类型识别为依赖） | 中 | 低 | 内置类型过滤列表（`string`, `int`, `bool`, `void`, `object` 等）|
| 大型项目依赖图数据量过大 | 中 | 中 | `get_dependency_graph` 限制返回深度（`max_depth` 参数）|
| 增量索引时依赖关系未正确清理 | 低 | 中 | 单元测试覆盖增量索引的依赖清理逻辑 |

---

## 12. 实现顺序建议

按以下顺序实现，每步可独立验证：

```
Step 1:  新增 SymbolDependency 模型
Step 2:  扩展 IIndexStore 接口（新增 4 个方法）
Step 3:  在 JsonlIndexStore 中实现新接口（stub 实现，返回空列表）
Step 4:  实现 DependencyExtractor（SyntaxTree 级提取）
Step 5:  修改 CodebaseIndexer 集成 DependencyExtractor
Step 6:  实现 SqliteIndexStore（完整实现所有 IIndexStore 方法，含 FTS5 虚拟表 + 触发器）
Step 7:  实现 IndexStoreFactory（含降级逻辑）
Step 8:  修改 CodebaseIndexer 使用 IndexStoreFactory
Step 9:  扩展 SearchCodeTool（新增 5 个 action：get_dependencies、find_usages、get_dependency_graph、get_symbol_context、search_text）
Step 10: 补全 IndexRoot.ExcludePatterns
Step 11: 更新 TOOLS.md.template
Step 12: 更新 CHANGELOG + package.json + ROADMAP
```

---

*文档版本: v1.1 | 已确认，进入实现阶段（2026-06-08）*
*v1.1 变更：新增 get_symbol_context action（借鉴 codedb context tool）、search_text action（FTS5 全文搜索）、FTS5 虚拟表 schema*
