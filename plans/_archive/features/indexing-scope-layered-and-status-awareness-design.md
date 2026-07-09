# Indexing Scope Layered + Status Awareness Design (v1.4.0)

> **目标版本**: 1.3.8 → 1.4.0
> **决策依据**: 用户在 chat 中反馈"动态 index 索引代码库会让整个系统变得特别卡"，选择 Plan B 全量方案（B1 + B2 一次交付）。
> **前置约束**: 严格遵守 `AGENTS.md` §7 编码硬规则 + `ADR-7` 单层本地索引架构 + `plans/llm-agent-architecture-remediation-plan.md` 治理层约束。
> **文档目的**: 用户对齐设计后再进入编码，避免"改完发现盲点又推倒重来"。

---

## 0. TL;DR

| 项 | 内容 |
|----|------|
| 卡顿根因（假设） | (a) 首次全量索引 + (b) `ProjectContextCollector.CollectExtended` 主线程阻塞 + (c) 分支切换/大规模脚本变更时脏文件洪水 |
| Phase B1 交付 | 消除主线程阻塞 + 引入 burst backoff + 新增 `search_code::diagnose` 让用户/LLM 能自证卡顿来源 |
| Phase B2 交付 | Scope 层次化索引（per-root 状态 + 优先级调度）+ LLM 索引状态感知（deferred context 注入） |
| 关键约束 | (1) 不破坏 `IIndexStore` schema（走 metadata KV）(2) 不破坏 SOUL.md 主结构（仅 §4 微增一条规则）(3) 不引入新工具，只在 `search_code` 内扩展 action |
| 版本策略 | 一次性升到 1.4.0（Minor），非破坏性；旧会话/旧索引文件 100% 向后兼容 |

---

## 1. 现状与卡顿根因分析

### 1.1 现有链路（已确认事实）

```
[启动/AssetPostprocessor 变更]
    ↓
BackgroundIndexService (InitializeOnLoad, EditorApplication.update 驱动)
    ↓
IndexingDirtyTracker.Snapshot(MaxBatchFiles)          ← 脏文件全量装载入内存
    ↓
CodebaseIndexer.RunTargetedIncrementalAsync(paths...) ← 已按文件路径过滤
    ↓
Roslyn 解析 → SymbolInfo → IIndexStore.BulkInsert
```

**当前已经做对的**（不要重复造轮子）：
- 增量已按脏文件走 `RunTargetedIncrementalAsync`，不是每次全量。
- Roslyn 提取器在 Task 上跑，不阻塞主线程（除文件 I/O）。
- `IndexingStatusBus` 已经 publish `Idle/Pending/Running/Failed/Disabled`。
- `IndexRootResolver.Resolve(workspace)` 已能按 `IIndexRootProvider` 聚合出多根。
- `IIndexStore` 已有 `SetMetadataAsync/GetMetadataAsync` per-workspace KV 存储。

### 1.2 卡顿的三个高概率来源（B1 要解决的）

**A. 首次全量索引**（`CodebaseIndexer.RunFullIndexAsync`）
- 首次启动或 `clear_index` 后，扫描 + Roslyn 全量解析所有 `.cs`。
- 大型 Unity 项目 5k–50k 个 cs 文件，即使不阻塞主线程，磁盘/CPU 也会被打满。
- **当前状态**: 已有实现，但没有"分片让步"和"低优先级"控制。

**B. `ProjectContextCollector.CollectExtended` 主线程阻塞**（[`Editor/Bootstrap/ProjectContextCollector.cs`](Editor/Bootstrap/ProjectContextCollector.cs:141)）
- Bootstrap 首轮编译 System Prompt 时被同步调用。
- 内部调用：
  - `GetProjectStats()` — 扫描 `Assets/Scripts/**` 统计文件数、行数（磁盘 I/O）
  - `GetNamespaceDistribution()` — 二次扫描聚合命名空间
  - `GetCustomTagsAndLayers()` — `TagManager.asset` 反序列化
  - `GetDirectoryTree()` — 递归目录树
- **当前状态**: 全部在主线程同步跑，首次打开 Chat 窗口时用户感知明显。

**C. 脏文件洪水**（分支切换、代码格式化批处理、生成器脚本大规模改动）
- `IndexingDirtyTracker.Add` 只做去重，不做频率控制。
- 500 个 cs 文件一次性 mark dirty → 后台立即拉起大批处理 → Editor Update 卡顿。
- **当前状态**: 无 backoff，无 burst 检测。

### 1.3 Scope 层次化缺失的问题（B2 要解决的）

**当前 IndexRoot 只记录静态元数据**（[`Editor/Indexing/Models/IndexRoot.cs`](Editor/Indexing/Models/IndexRoot.cs:8)）：
- `Id / WorkspaceId / RootPath / ScopeType / ScopeName / Role / ReadOnly / IsEnabled / IncludePatterns / ExcludePatterns / IsDefaultSearchScope`
- **没有**：`IndexState / LastIndexedAt / LastIndexError / IndexedFileCount / IndexedSymbolCount / Priority`

**导致的问题**：
- 用户无法在设置页看到"哪个 root 已索引 / 哪个还未索引 / 哪个失败"。
- LLM 调用 `search_code::status` 只能看到 workspace 全局状态，看不到 per-root。
- 无法按 Role/ScopeType 差异化调度（例如 `EditableProjectCode` 应前台立刻索引，`CommercialPlugin` 应完全按需）。

**LLM 感知缺失**：
- LLM 每次调用 `search_code::search_symbol` 都可能踩空（因为对应 root 还没索引完），且它不知道踩空的原因。
- 需要一个"轻量的、每轮注入"的状态块告诉 LLM：当前哪些 root 可搜索、哪些还在索引、大约还要多久。

---

## 2. 设计原则（Non-Negotiable）

1. **不破坏 IIndexStore 契约** — 所有 per-root 状态走 metadata KV（`root:{rootId}:*`），JSONL 和 SQLite 后端无需修改 schema。
2. **不新增顶层工具** — LLM 视角只有一个 `search_code` 工具，通过 action 扩展。
3. **不修改 SOUL.md 主体** — 只在 §4 Context Awareness 追加一条规则，规则语言中立、可预测。
4. **主线程零阻塞** — `CollectHeavyAsync` 走 `Task.Run`，首次未就绪则 fallback 到骨架版本。
5. **向后兼容** — 旧的 dirty tracker JSON、旧的 SOUL.ext.md、旧的 session 文件必须继续工作。
6. **可诊断** — 用户遇到卡顿时能通过一条命令自证根因，不需要 AI 猜测。
7. **可观测** — Chat 头部 + Settings 页 + Chat System Prompt 三处一致地反映索引状态。

---

## 3. Phase B1 — 卡顿根因消除

### 3.1 B1.1 ProjectContextCollector 拆分

**文件**: `Editor/Bootstrap/ProjectContextCollector.cs`

**改造**：把 `CollectExtended()` 拆成 fast + heavy 两条路径。

```csharp
public static class ProjectContextCollector
{
    // 现有 Collect() 保留（快速版，已经足够轻）
    public static string Collect();

    // 新增：快速返回骨架；不做磁盘扫描、不做 namespace 聚合
    public static string CollectFast();

    // 新增：后台执行完整版；带缓存（同一 workspace + 5 分钟内直接返回缓存）
    public static Task<string> CollectHeavyAsync(CancellationToken ct = default);

    // 现有 CollectExtended() 保留但内部改为：
    //   if (缓存有效) return 缓存；
    //   else return CollectFast() 并触发 CollectHeavyAsync 后台预热。
    public static string CollectExtended();
}
```

**缓存键**：`workspace.Fingerprint + Editor 会话 Guid`（跨 Domain Reload 会失效，可接受）。
**缓存位置**：`Library/agentcore-project-context.cache`（同 dirty tracker 目录）。

**BootstrapLoader.Load 首轮调用改造**：
- 首轮 `CompileSystemPrompt` 只用 `Collect() + CollectFast()`。
- `CollectHeavyAsync` 结果通过 `BootstrapContext.CompileDeferredContext` 在**下一轮**注入（等同 workspace snapshot 现有机制）。

**风险**: PROJECT.md 用户版本仍然同步读取（磁盘 I/O 小，可接受）。

### 3.2 B1.2 IndexingDirtyTracker Burst Detection

**文件**: `Editor/Indexing/Core/IndexingDirtyTracker.cs`

**改造**：在 `Add(paths, deleted)` 中检测突发写入，并对 BackgroundIndexService 传播 backoff 提示。

```csharp
private const int BurstThreshold = 500;               // 单批 500+ 视为 burst
private const double BurstBackoffSeconds = 60;        // burst 后暂停 60s

// Add 内部：
//   if (batchCount >= BurstThreshold) {
//       _burstDetectedAt = DateTime.UtcNow;
//       BackgroundIndexService.NotifyBurstDetected(batchCount, BurstBackoffSeconds);
//   }
```

**BackgroundIndexService 侧**：
- 新增 `_burstBackoffUntil : DateTime?`
- `OnEditorUpdate` 时 `if (now < _burstBackoffUntil) return;`
- Publish 状态时新增 `IndexingBackgroundState.BackoffDueToBurst`（枚举扩展）或在现有 `Idle` 快照中带 `NextRunAt` 字段。

**决策点**：**枚举扩展 vs 快照字段**。
- 我倾向"快照字段"—— 不动枚举，`IndexingStatusSnapshot` 增加 `DateTime? NextRunAt` 和 `string ReasonPaused`。
- 好处：不破坏订阅方（Settings UI、UI 头部）现有 switch。

### 3.3 B1.3 search_code 新增 `diagnose` action

**文件**: `Editor/Indexing/Tools/SearchCodeTool.cs`

**新增 action**: `diagnose`

**返回内容**（结构化 JSON，方便 LLM 直接引用）：
```json
{
  "background_service": {
    "state": "Idle|Pending|Running|Failed|Disabled",
    "dirty_file_count": 0,
    "processed_files": 0,
    "total_files": 0,
    "current_file": null,
    "last_error": null,
    "last_success_at": "2026-07-07T02:30:00Z",
    "consecutive_failures": 0,
    "next_run_at": null,
    "reason_paused": null
  },
  "workspace": {
    "fingerprint": "abc123",
    "root_path": "d:/proj",
    "resolved_roots": 5
  },
  "roots": [
    {
      "root_id": 1,
      "display_name": "Assets/Scripts",
      "scope_type": "Project",
      "role": "EditableProjectCode",
      "index_state": "Ready|Indexing|Stale|NotIndexed|Failed",
      "last_indexed_at": "2026-07-07T02:29:15Z",
      "indexed_file_count": 1284,
      "indexed_symbol_count": 8763,
      "priority": "Foreground",
      "last_error": null
    }
  ],
  "advice": [
    "所有 root 均为 Ready 状态，索引健康。",
    "如仍感卡顿，请检查 ProjectContextCollector 缓存（Library/agentcore-project-context.cache）是否存在。"
  ]
}
```

**advice 生成逻辑**（决策树）：
- 有 root 处于 `Indexing` → 提示"后台索引中，预计 N 分钟"
- 有 root 处于 `Failed` → 提示"root {name} 索引失败，请查看 last_error"
- `dirty_file_count > 500` → 提示"存在大量脏文件，建议暂停 Agent 直到索引完成"
- `next_run_at != null` → 提示"burst backoff 中，将在 {t} 恢复"
- 全部 Ready → 提示"索引健康"

---

## 4. Phase B2 — Scope 层次化 + LLM 感知

### 4.1 B2.1 IndexRoot 状态字段扩展

**文件**: `Editor/Indexing/Models/IndexRoot.cs`

**新增字段（POCO，可选，默认值安全）**：

```csharp
public IndexRootState IndexState { get; set; } = IndexRootState.NotIndexed;
public DateTime? LastIndexedAt { get; set; }
public string LastIndexError { get; set; }
public int IndexedFileCount { get; set; }
public int IndexedSymbolCount { get; set; }
public IndexRootPriority Priority { get; set; } = IndexRootPriority.Background;
```

**新枚举**：

```csharp
// Editor/Indexing/Models/IndexRootState.cs
public enum IndexRootState
{
    NotIndexed,   // 从未索引
    Indexing,     // 正在索引（含增量）
    Ready,        // 完全就绪
    Stale,        // 有脏文件但未处理
    Failed,       // 上次索引失败
    Disabled      // Role/Config 禁用
}

// Editor/Indexing/Models/IndexRootPriority.cs
public enum IndexRootPriority
{
    Foreground,  // 立即前台索引（EditableProjectCode/SharedCode）
    Background,  // 后台闲时索引（WorkspacePackage/ToolingCode）
    OnDemand     // 仅按需触发（CommercialPlugin/EngineCode/GeneratedCode）
}
```

**关键决策**：这些字段**不写入 IIndexStore schema**。IndexRoot 存活于内存，重启后由：
1. `IndexRootResolver.Resolve` 重新聚合静态元数据
2. `IndexRootStateStore`（新组件）从 metadata KV 加载动态状态

### 4.2 B2.2 IndexingSchedulePolicy

**新文件**: `Editor/Indexing/Core/IndexingSchedulePolicy.cs`

**职责**：根据 `IndexRoot.Role + ScopeType` 决定 `Priority`；BackgroundIndexService 消费 Priority 排序脏文件。

```csharp
public static class IndexingSchedulePolicy
{
    public static IndexRootPriority ResolvePriority(IndexRoot root)
    {
        return root.Role switch
        {
            IndexRootRole.EditableProjectCode => IndexRootPriority.Foreground,
            IndexRootRole.SharedCode           => IndexRootPriority.Foreground,
            IndexRootRole.WorkspacePackage     => IndexRootPriority.Background,
            IndexRootRole.ToolingCode          => IndexRootPriority.Background,
            IndexRootRole.CustomPlugin         => IndexRootPriority.Background,
            IndexRootRole.CommercialPlugin     => IndexRootPriority.OnDemand,
            IndexRootRole.EngineCode           => IndexRootPriority.OnDemand,
            IndexRootRole.GeneratedCode        => IndexRootPriority.OnDemand,
            IndexRootRole.ReadOnlyReference    => IndexRootPriority.OnDemand,
            _                                   => IndexRootPriority.Background
        };
    }
}
```

**BackgroundIndexService 消费**：
- `RunOnceAsync` 之前先按 dirty path 归属的 root 分组，剔除 `OnDemand` 的 root（除非用户显式 `index_scope`）。
- Foreground 组优先处理，Background 组次之。

**决策点**：**是否让 `OnDemand` root 完全跳过 background service？**
- 是。`OnDemand` root 只有以下情况被索引：
  1. 用户在 Settings 页点"Index this root"
  2. LLM 调用 `search_code::index_scope { scope_type: "Plugin" }`
  3. LLM 调用 `search_code::index_full`（全量强制）
- Rationale：这些 root 是 CommercialPlugin/Engine，通常几个月不变，没必要跟着增量循环跑。

### 4.3 B2.3 IndexRootStateStore

**新文件**: `Editor/Indexing/Core/IndexRootStateStore.cs`

**职责**：per-root 动态状态的持久化与查询，走 `IIndexStore.SetMetadataAsync/GetMetadataAsync`。

```csharp
public sealed class IndexRootStateStore
{
    private readonly IIndexStore _store;
    private readonly int _workspaceId;

    public async Task<IndexRootStatus> LoadAsync(int rootId, CancellationToken ct);
    public async Task SaveAsync(int rootId, IndexRootStatus status, CancellationToken ct);

    // metadata key 约定：
    //   root:{rootId}:state          → "NotIndexed|Indexing|Ready|Stale|Failed|Disabled"
    //   root:{rootId}:last_indexed_at → ISO8601
    //   root:{rootId}:last_error     → string
    //   root:{rootId}:file_count     → int
    //   root:{rootId}:symbol_count   → int
}

public sealed class IndexRootStatus
{
    public IndexRootState State;
    public DateTime? LastIndexedAt;
    public string LastError;
    public int FileCount;
    public int SymbolCount;
}
```

**CodebaseIndexer 侧集成**：
- `RunTargetedIncrementalAsync` 开始时把涉及的 root 标记为 `Indexing`。
- 每 root 处理完毕 → 标记 `Ready` + 更新 file/symbol count + 时间戳。
- 异常 → 标记 `Failed` + 保存 error message。
- `IndexingDirtyTracker` 有该 root 待处理但当前非 `Indexing` → 标记 `Stale`。

### 4.4 B2.4 SearchCodeTool 增强

**文件**: `Editor/Indexing/Tools/SearchCodeTool.cs`

**改造 `HandleStatus`**（[line 260](Editor/Indexing/Tools/SearchCodeTool.cs:260)）：
- 保留现有 workspace 全局状态字段
- 新增 `per_root_state`：数组，每个 root 一行

**新增 action `mark_stale`**：
- 参数：`root_id` 或 `scope_type` 或 `scope_name`
- 语义：强制把匹配的 root 标记为 `Stale`，下轮 background service 触发时会重新索引该 root 下的所有文件（即 `dirty := all files in root`）
- 用途：LLM/用户发现某个 root 索引不完整时的自愈手段

**新增 action `list_root_states`**：
- 无参数
- 返回：所有 root 的完整状态（priority、state、counts、last_indexed_at）
- 与 `diagnose` 的区别：`diagnose` 面向"我卡了怎么办"，`list_root_states` 面向"哪些代码可搜索"

### 4.5 B2.5 LLM 索引状态感知

> **[编码前校准]** 原设计假设 deferred context 每轮注入，实际代码 [`AgentLoop.cs:415-440`](Editor/Core/AgentLoop.cs:415) 中 `_deferredContext` 与 `WorkspaceSnapshotBuilder.Build()` 都**只在会话首轮**注入。为对齐既有架构、避免 token 膨胀，改为下述方案。

**新文件**: `Editor/Bootstrap/IndexingStatusBlockBuilder.cs`

**职责**：生成一段轻量索引状态文本，供 `WorkspaceSnapshotBuilder.Build()` 追加到 workspace snapshot 尾部。

**输出示例**（Markdown，控制在 400 tokens 以内）：

```markdown
## Index Status

Background: Ready (last success: 2026-07-07 02:29:15Z)
Dirty files pending: 0

Ready roots (searchable now):
- Assets/Scripts (Project/EditableProjectCode) — 1284 files, 8763 symbols
- Assets/Shared (Shared/SharedCode) — 210 files, 1533 symbols

Not-indexed roots (call search_code::index_scope to enable on demand):
- Packages/com.thirdparty.plugin (Plugin/CommercialPlugin)
- Assets/Generated (Generated/GeneratedCode)

Note: For real-time status during a running conversation, call search_code::status or search_code::diagnose.
```

**注入时机（校准后）**：
- 每会话首轮：作为 `WorkspaceSnapshotBuilder.Build()` 输出的一部分注入（与 workspace snapshot 同批）。
- 会话中后续轮次：**不重复注入**。LLM 若需最新状态，主动调用 `search_code::status` / `diagnose`（pull 模型）。
- Domain Reload 后新会话首轮：正常注入。

**Rationale**：
1. 既有架构已明确"首轮 snapshot + 后续 pull"是 workspace-related 上下文的标准范式（见 [`WorkspaceSnapshotBuilder.Build`](Editor/Core/WorkspaceSnapshotBuilder.cs:36) 与 [`AgentLoop.cs:426`](Editor/Core/AgentLoop.cs:426)）。
2. 每轮注入会累加 token 成本；即使做 diff 检测，也需要在 `AgentLoop` 加新分支，破坏架构一致性。
3. LLM 在感知过一次索引状态后，若发现搜索不到应调用 `diagnose` / `mark_stale` / `index_scope`，这本身就是我们希望它掌握的行为闭环。

**SOUL.md §4 追加规则**（不重写 §4，只加一段）：
```
- 当 workspace snapshot 中出现 "Index Status" 块时，遵循以下规则：
  1. 在 Ready roots 内检索优先，命中概率最高
  2. 若目标疑似在 Not-indexed roots 中，先调用 search_code::index_scope 触发索引再检索
  3. 若搜索多次落空或怀疑索引过期，调用 search_code::status 或 search_code::diagnose 获取最新状态；必要时 mark_stale + index_scope 强制重建
```

### 4.6 B2.6 Settings 页显示

**文件**: `Editor/Indexing/UI/IndexingSettingsPage.cs`

**改造**：
- 现有 workspace 全局状态区不动
- 新增 "Roots" 折叠面板，列出每个 root 的：DisplayName / Role / State（用彩色 badge）/ FileCount / LastIndexedAt / 按钮 `Reindex` `Mark Stale`

**目的**：让用户看到"哪些代码可搜索"，与 LLM 视角对齐。

---

## 5. 数据流总览

```
[用户改代码]
   ↓
[AssetPostprocessor → IndexingDirtyTracker.AddChanged]
   ↓ (burst detection)
[BackgroundIndexService.OnEditorUpdate]
   ↓ (按 Priority + Role 过滤)
[CodebaseIndexer.RunTargetedIncrementalAsync]
   ↓ (per-root)
   ├─→ IndexRootStateStore.SaveAsync(rootId, Indexing)
   ├─→ Roslyn 解析 → IIndexStore.BulkInsertSymbolsAsync
   └─→ IndexRootStateStore.SaveAsync(rootId, Ready|Failed)
   ↓
[IndexingStatusBus.Publish(snapshot)]
   ↓                                    ↓
[UI 头部/Settings 页刷新]         [IndexingStatusBlockBuilder]
                                        ↓
                                [BootstrapContext.AppendDeferredContext]
                                        ↓
                                  [下一轮 LLM System Prompt]
```

---

## 6. 交付计划（B1 + B2 一次交付到 1.4.0）

### 6.1 编码顺序（严格按此顺序，每一步独立可测）

**Step 1 — B1.1 Collector 拆分**
- 拆 `Editor/Bootstrap/ProjectContextCollector.cs` 为 Collect / CollectFast / CollectHeavyAsync
- `Editor/Bootstrap/BootstrapLoader.cs` 首轮改走 Fast + 触发 HeavyAsync 预热
- `Editor/Bootstrap/BootstrapContext.cs` 的 `CompileDeferredContext` 拼入 Heavy 结果
- 验收：Chat 首次打开延迟明显下降

**Step 2 — B1.2 Burst Detection**
- `IndexingStatusSnapshot` 新增 `NextRunAt / ReasonPaused` 字段（非破坏）
- `IndexingDirtyTracker.Add` 内部检测 burst
- `BackgroundIndexService` 消费 backoff
- 验收：一次性 500+ 文件 mark dirty → snapshot 出现 ReasonPaused

**Step 3 — B1.3 diagnose action**
- `SearchCodeTool` 新增 `HandleDiagnoseAsync`
- 更新 `_parametersSchema` action enum
- 更新 `TOOLS.md.template`
- 验收：`search_code { action: "diagnose" }` 返回完整诊断 JSON

**Step 4 — B2.1 IndexRoot 状态字段**
- `IndexRoot.cs` 添加 6 个字段
- 新建 `IndexRootState.cs / IndexRootPriority.cs`
- 验收：编译通过，旧代码不破

**Step 5 — B2.2 SchedulePolicy**
- 新建 `IndexingSchedulePolicy.cs`
- `IndexRootResolver.Resolve` 后调用 `ResolvePriority` 填充
- 验收：debug log 中输出各 root 的 Priority

**Step 6 — B2.3 IndexRootStateStore**
- 新建 `IndexRootStateStore.cs`（基于 IIndexStore metadata KV）
- `CodebaseIndexer` 在 targeted/full 索引全流程接入 state 更新
- `BackgroundIndexService` 按 Priority 过滤/排序 dirty paths
- 验收：`list_root_states` 返回真实的 file/symbol count

**Step 7 — B2.4 SearchCodeTool 增强**
- `HandleStatus` 追加 `per_root_state`
- 新增 `HandleMarkStaleAsync` / `HandleListRootStatesAsync`
- 更新 `_parametersSchema`
- 验收：三个 action 端到端可调

**Step 8 — B2.5 LLM 感知块**
- 新建 `IndexingStatusBlockBuilder.cs`
- `BootstrapContext.CompileDeferredContext` 拼入 status block（第 2 轮起）
- `SOUL.md` §4 追加一条规则
- 验收：Chat 第 2 轮系统提示词出现 Index Status 块

**Step 9 — B2.6 Settings UI**
- `IndexingSettingsPage.cs` 新增 Roots 面板
- 验收：Settings 页显示每个 root 的状态 badge + Reindex/MarkStale 按钮可用

### 6.2 涉及文件清单（新建 5 / 修改 11）

**新建**：
1. `Editor/Indexing/Models/IndexRootState.cs`
2. `Editor/Indexing/Models/IndexRootPriority.cs`
3. `Editor/Indexing/Core/IndexingSchedulePolicy.cs`
4. `Editor/Indexing/Core/IndexRootStateStore.cs`
5. `Editor/Bootstrap/IndexingStatusBlockBuilder.cs`

**修改**：
1. `Editor/Bootstrap/ProjectContextCollector.cs` — 拆 fast/heavy
2. `Editor/Bootstrap/BootstrapLoader.cs` — 首轮走 fast
3. `Editor/Bootstrap/BootstrapContext.cs` — deferred 拼入 status block + heavy context
4. `Editor/Bootstrap/Resources/SOUL.md` — §4 追加一条
5. `Editor/Bootstrap/Resources/TOOLS.md.template` — search_code 新 action 说明
6. `Editor/Indexing/Core/IndexingStatusBus.cs` — snapshot 加两个字段
7. `Editor/Indexing/Core/IndexingDirtyTracker.cs` — burst 检测
8. `Editor/Indexing/Core/BackgroundIndexService.cs` — 消费 backoff + Priority 排序
9. `Editor/Indexing/Core/CodebaseIndexer.cs` — 接入 IndexRootStateStore
10. `Editor/Indexing/Models/IndexRoot.cs` — 6 个新字段
11. `Editor/Indexing/Tools/SearchCodeTool.cs` — 3 个新 action
12. `Editor/Indexing/UI/IndexingSettingsPage.cs` — Roots 面板

（清单以实施为准；有偏差时同步更新本文档）

---

## 7. 版本号同步（AGENTS.md §12.5）

**必须同步修改的三处**：

| 文件 | 修改内容 |
|------|---------|
| `package.json` | `"version": "1.3.8"` → `"version": "1.4.0"` |
| `CHANGELOG.md` | 顶部新增 `## [1.4.0]` 节，Added/Changed/Fixed 分组 |
| `plans/ROADMAP.md` | Phase 7 §3.1 后新增子节 §3.1.x "索引可观测性与 Scope 层次化 (v1.4.0)"，标注完成状态 |

**§6.2 文件清单校准**：`Editor/Core/WorkspaceSnapshotBuilder.cs` 从"仅供参考"变为"修改"文件；不再修改 `Editor/Bootstrap/BootstrapContext.cs`（Index Status 走 workspace snapshot 路径）。

**CHANGELOG 草稿**：

```markdown
## [1.4.0] - 2026-XX-XX

### Added
- `search_code::diagnose` — 一键诊断索引状态与卡顿根因
- `search_code::list_root_states` — 列出所有索引 root 的动态状态
- `search_code::mark_stale` — 强制标记 root 为脏，触发重新索引
- LLM System Prompt 每轮注入 "Index Status" 块，感知 Ready/Indexing/NotIndexed roots
- IndexRoot 新增 State/LastIndexedAt/FileCount/SymbolCount/Priority 等运行时字段
- IndexingSchedulePolicy 按 Role 映射 Foreground/Background/OnDemand 三级优先级
- Settings 页 Indexing 面板新增 Roots 列表

### Changed
- ProjectContextCollector 拆分为 Collect/CollectFast/CollectHeavyAsync，主线程零阻塞
- BootstrapLoader 首轮 System Prompt 只用 Fast 版本，Heavy 版本走 deferred context
- IndexingDirtyTracker 引入 burst detection，500+ 文件突发变更触发 60s backoff
- IndexingStatusSnapshot 新增 NextRunAt / ReasonPaused 字段
- CodebaseIndexer 全流程同步 per-root 状态到 metadata KV
- BackgroundIndexService 按 Priority 过滤，OnDemand root 不参与自动增量

### Fixed
- 首次打开 Chat 窗口时的主线程卡顿（源自 ProjectContextCollector.CollectExtended）
- 分支切换 / 批量脚本变更时的索引洪水导致 Editor Update 卡顿
```

---

## 8. 验收标准（4 轮，用户执行）

### Round 1 — Happy Path
- [ ] 打开 Chat 窗口，感知延迟明显下降
- [ ] `search_code::status` 返回包含 per_root_state 数组
- [ ] `search_code::diagnose` 返回结构化 JSON，advice 字段为可读中文
- [ ] Settings → Indexing → Roots 面板显示所有 root
- [ ] Chat 首轮 workspace snapshot 中出现 "Index Status" 块；后续轮次不重复

### Round 2 — 边界与容错
- [ ] `search_code::diagnose` 在索引未初始化时返回 `background_service.state == Disabled`
- [ ] `search_code::mark_stale { root_id: 99999 }` 返回清晰错误，不抛异常
- [ ] `search_code::list_root_states` 在无 root 时返回空数组
- [ ] 一次性触发 800 cs 文件修改 → snapshot 出现 `ReasonPaused: burst`，60s 内不启动索引
- [ ] `CollectHeavyAsync` 被取消（关闭 Editor） → 不影响下次启动

### Round 3 — 核心链路
- [ ] Domain Reload 后 IndexRoot 状态从 metadata KV 正确恢复
- [ ] Settings 页点 Reindex → root 状态 Indexing → Ready，counts 更新
- [ ] `search_code::mark_stale { scope_type: "Package" }` 后所有 Package root 进入 Stale
- [ ] `OnDemand` root 不会被 background service 自动触发（连续观察 5 分钟）

### Round 4 — 实际场景
- [ ] 在真实企业 Unity 项目（>5k cs 文件）中跑通完整对话流程
- [ ] `diagnose` 的 advice 能准确指出卡顿来源
- [ ] LLM 在 Index Status 提示后主动调用 `index_scope` 补索引
- [ ] 分支切换后卡顿感明显缓解（前后对比）

---

## 9. 风险与缓解

| 风险 | 概率 | 影响 | 缓解 |
|------|------|------|------|
| Index Status 块过大导致首轮 snapshot 膨胀 | 中 | 首轮 token 成本上升 | 控制在 400 tokens 内；roots 超过 20 个时只列前 10 + "and N more" |
| CollectHeavyAsync 缓存 stale | 低 | 项目信息不准 | 5 分钟 TTL + fingerprint 校验；用户可清除缓存文件 |
| metadata KV 读取频率过高 | 低 | I/O 压力 | IndexRootStateStore 内部缓存，仅写时刷新 |
| Burst threshold 误伤正常改动 | 中 | 索引延迟 | 阈值可配置（默认 500），Settings 页可调 |
| SOUL.md §4 追加规则被误解 | 低 | LLM 行为异常 | 规则具体、举例；Round 3 专门测试 |
| OnDemand root 用户搜不到 | 中 | 体验倒退 | Index Status 块明确列 Not-indexed roots + 引导 index_scope |
| JSONL 后端 metadata KV 性能不足 | 低 | SQLite 无问题 | v1.4.0 仍以 JSONL 兜底，后续走 ADR-7 SQLite 迁移 |

---

## 10. 与既有 ADR 的一致性检查

- **ADR-7（拒绝骨架文档）**：不引入任何 .md 骨架，per-root 状态走 metadata KV + 内存对象。✓
- **ADR-8（Agent 主动调用规则内嵌于 SOUL.md）**：§4 追加"看到 Index Status 块时的行为规则"。✓
- **ADR-14（LLM/Agent 架构修复准则前置）**：不新增顶层工具，只扩展 action；不涉及文件写入 / 代码执行 / MCP。✓
- **AGENTS.md §7 硬规则**：ExecuteAsync 全部异步 + CancellationToken；ToolResponse 规范；无硬编码。✓
- **AGENTS.md §12（开发流程）**：文档对齐 → 用户确认 → 编码分步 → 验收 → 版本同步。✓

---

## 11. 后续路径（不属于 1.4.0，仅记录方向）

- **v1.4.x**：Roots 面板增加"预估索引耗时"、"文件模式匹配预览"
- **v1.5.0**：`search_code::search_semantic`（基于符号 embedding 的语义检索），依赖本次 Scope 层次化基础
- **Phase 8 MCP**：diagnose/status 结构可直接作为 MCP resource 暴露给外部客户端

---

## 12. 用户 Review Checklist（进入编码前必须确认）

请用户明确以下 6 个决策点：

1. **Priority=OnDemand 的 root 是否完全跳过后台索引？** — 倾向 Yes（§4.2）
2. ~~**Index Status 块从第几轮开始注入？**~~ — 已校准为"首轮 snapshot 一次性注入 + 后续 LLM pull"，与既有 WorkspaceSnapshotBuilder 路径统一
3. **Burst threshold 是否需要暴露到 Settings？** — 倾向 Yes，默认 500
4. **Roots 面板是否需要批量操作（Reindex All / Mark All Stale）？** — 建议 v1.4.0 只做单 root，批量留 v1.4.x
5. **`mark_stale` 是否需要用户二次确认？** — 倾向不需要（低风险、可撤销）
6. **SOUL.md §4 追加的三条规则措辞** — 见 §4.5，是否需要调整语言

用户对以上 6 个决策点全部确认后，进入 Step 1 编码。