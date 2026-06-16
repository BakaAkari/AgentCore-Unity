# Code Indexing 后台静默 + 增量化设计

> 状态: DRAFT (设计阶段, 未编码)
> 目标版本: **v1.1.0**（Phase 7 §3.1，从 Phase 6 派生；详见 ROADMAP §3.1 与 ADR-11）
> 关联: `Editor/Indexing/Core/CodebaseIndexer.cs`, `Editor/Indexing/UI/IndexingPanel.cs`

---

## 1. 现状盘点

| 维度 | 当前实现 | 是否满足"后台 + 增量"需求 |
|------|----------|--------------------------|
| 增量算法 | `RunIncrementalIndexAsync` 已存在, 基于 `LastModified` ticks 比对 | 算法 OK, 但仍要 `ScanAllFiles` 全盘 walk, 大仓库慢 |
| 异步执行 | `IndexingPanel.RunIndexInternalAsync` 已是 fire-and-forget | OK, 但只在用户手动点击 Full / Incremental 时触发 |
| 进度回调 | `IndexingProgress` + `IndexingPhase` 已就绪 | OK, 是状态总线的天然基础 |
| 取消支持 | `CancellationToken` 已贯穿全链路 | OK |
| 主线程依赖 | `IndexWorkspaceResolver.ResolveFromCurrent` 必须主线程 | 需要在调度层先在主线程取快照, 再下放后台 |
| 存储并发 | SQLite (Editor/Plugins/Sqlite) | 需启用 WAL, 让查询(`search_code`)和写入并存 |
| 触发源 | 仅手动 (UI 按钮 + LLM 工具 `search_code` 中的 `index_full`/`index_incremental`) | 缺自动触发, 这是核心缺口 |
| Domain Reload | 未参与索引调度 | 需要 reload 后能自动恢复 dirty set 与重新启动后台 |
| 失败回退 | 增量在 version 不匹配 / 无 full index 时降级为全量 (CodebaseIndexer L240-244) | OK |
| UI 阻断 | 当前 UI 是模态阻塞按钮变文 "Indexing..." | 需要静默推送状态, 不再用按钮 disable 表达"忙" |

**核心结论**: 引擎层增量算法已存在; 真正缺的是 **触发层 + 调度层 + 静默通知层**, 不需要重写 `CodebaseIndexer`。

---

## 2. 设计目标 (按优先级)

1. **不阻断用户交互**: 后台索引绝不可冻结 Editor 主线程; 不可 disable Chat 输入。
2. **不重复劳动**: 1 秒内连发的 N 次文件变更, 合并为 1 次索引任务。
3. **不丢更新**: 编辑中产生的脏文件, 即使在 Domain Reload 中断后也必须被记住并继续处理。
4. **失败可恢复**: 单文件解析失败不应中止整个增量任务; 整任务失败应自动退避并降级 (incremental → full)。
5. **可观测**: 用户能在 IndexingPanel 看到 "Idle / N files pending / Running x/y / Failed" 等状态。
6. **可关闭**: 提供 `Auto-Index` 开关, 默认开启; 进入 PlayMode 或主动关闭时立刻让步。
7. **零额外依赖**: 全部基于 Unity Editor API + 现有 SQLite/Roslyn 栈。

---

## 3. 触发源 (Trigger Sources)

利用 Unity Editor 已有的事件钩子, 无需 FileSystemWatcher (后者在 Windows 上和 Unity 的 import pipeline 有竞争, 不稳)。

| Trigger | API | 何时触发 | 提供的信息 | 推荐用途 |
|---------|-----|----------|-----------|---------|
| **Asset import** | `AssetPostprocessor.OnPostprocessAllAssets(imported, deleted, moved, movedFrom)` | 每次 Unity 完成 import (含 git pull 后批量 reimport) | 精确 path 列表 + 删除 + 重命名 | **主信号源** |
| **Compilation done** | `CompilationPipeline.assemblyCompilationFinished` | 单个 asmdef 编译完成 | asmdef 名 + 输出 dll 路径 | 触发 Roslyn 重新解析该 asmdef 下的 .cs |
| **Domain reload bookend** | `[InitializeOnLoadMethod]` + `AssemblyReloadEvents.beforeAssemblyReload` | reload 前后 | 无 | 持久化 dirty set, reload 后恢复调度 |
| **Editor idle tick** | `EditorApplication.update` | 每帧 | 无 | 调度循环的心跳 (debounce 计时) |
| **Yield gates** | `EditorApplication.isCompiling`, `isUpdating`, `isPlaying` | 查询属性 | bool | 决定是否暂缓后台任务 |
| **VCS hook** (可选) | 现有 VCS 组件的 pull/update 完成事件 | git pull / svn update 后 | 改动文件清单 | 锦上添花, 避免等待 Unity import 完成 |

**关键洞察**: `OnPostprocessAllAssets` 是 Unity 官方推荐的"我变了哪些文件"事件源, Unity 已经替我们做完了 import 同步 + 元数据更新, 比 FileSystemWatcher 干净得多。git pull 后, Unity 会自动批量触发它。

---

## 4. 三层调度模型

```
┌──────────────────────────────────────────────────────────────────┐
│ Layer 1: DirtyTracker  (主线程, 同步, 无锁竞争)                   │
│   ├─ 由 OnPostprocessAllAssets / CompilationPipeline 喂入         │
│   ├─ 用 HashSet<string> 去重, 区分 dirty / deleted                │
│   └─ 持久化到 Library/agentcore-indexing-dirty.json (reload 安全) │
└──────────────────────────────────────────────────────────────────┘
                              ↓ 累积
┌──────────────────────────────────────────────────────────────────┐
│ Layer 2: CoalescingScheduler  (主线程, EditorApplication.update)  │
│   ├─ Debounce: 静默 N 秒 (默认 2s) 才触发, 防止 import 风暴      │
│   ├─ Yield Gate: isCompiling / isPlaying / isUpdating → 推迟      │
│   ├─ Backoff: 上次失败 → 指数退避 (5s → 30s → 120s, 上限 3 次)    │
│   └─ 任务唯一: 已有任务在跑就跳过, 不并发                          │
└──────────────────────────────────────────────────────────────────┘
                              ↓ 触发
┌──────────────────────────────────────────────────────────────────┐
│ Layer 3: BackgroundIndexService  (Task.Run + 主线程快照)          │
│   ├─ 主线程拿 workspace snapshot (Resolve 必须主线程)             │
│   ├─ Snapshot dirty set, 立刻清空 tracker (允许新变更继续累积)    │
│   ├─ Task.Run → 调用 CodebaseIndexer.RunTargetedIncrementalAsync  │
│   ├─ 每文件之间 await Task.Yield() 让主线程喘息                   │
│   ├─ 进度通过 StatusBus 推送到 UI                                  │
│   └─ 完成 → 持久化 dirty set 中已成功的文件清除                    │
└──────────────────────────────────────────────────────────────────┘
```

### 4.1 关键新组件

| 组件 | 位置 | 类型 | 主要职责 |
|------|------|------|----------|
| `IndexingDirtyTracker` | `Editor/Indexing/Core/` | 静态 + `[InitializeOnLoadMethod]` | 收集脏路径, 持久化, reload 恢复 |
| `IndexingAssetWatcher` | `Editor/Indexing/Core/` | `AssetPostprocessor` 子类 | 把 imported/deleted/moved 喂给 tracker |
| `BackgroundIndexService` | `Editor/Indexing/Core/` | 静态 + `[InitializeOnLoadMethod]` | 调度主循环, 唯一任务执行点 |
| `IndexingStatusBus` | `Editor/Indexing/Core/` | 静态 + `event` | 发布 Idle/Pending/Running/Failed 状态 |
| `IndexingAutoSettings` | `Editor/Indexing/Config/` | 嵌入 `AgentCoreSettings` 或 `IndexingSettings` | Auto 开关, debounce 秒数, 退避策略 |

### 4.2 关键新引擎方法

`CodebaseIndexer` 上新增 (不替换现有方法):

```csharp
// 接收外部已知的脏文件 / 删除文件清单, 跳过 ScanAllFiles 全盘 walk
public async Task<IndexingProgress> RunTargetedIncrementalAsync(
    IReadOnlyCollection<string> dirtyAbsolutePaths,
    IReadOnlyCollection<string> deletedAbsolutePaths,
    Action<IndexingProgress> onProgress = null,
    CancellationToken ct = default);
```

复用现有 `IndexFileAsync` / `DeleteFileAsync` 内部逻辑, 仅替换"发现变更"步骤为外部传入。

---

## 5. 静默通知模型 (Status Bus + UI)

### 5.1 状态机

```
        ┌─────────┐  dirty++       ┌─────────┐  schedule  ┌─────────┐
        │  Idle   │───────────────▶│ Pending │───────────▶│ Running │
        └─────────┘                └─────────┘            └─────────┘
             ▲                          │ cancel              │
             │                          ▼                     │
             │                     ┌─────────┐                │
             └─────done────────────│ Failed  │◀───error───────┘
                                   └─────────┘
                                       │ retry-backoff
                                       └────▶ Pending
```

### 5.2 IndexingStatusBus (新增)

```csharp
public enum IndexingBackgroundState { Idle, Pending, Running, Failed, Disabled }

public sealed class IndexingStatusSnapshot
{
    public IndexingBackgroundState State;
    public int DirtyFileCount;     // 累积待处理
    public int ProcessedFiles;     // 当前任务已完成
    public int TotalFiles;         // 当前任务总数
    public string CurrentFile;     // 当前正在解析的文件 (可空)
    public string LastError;       // 最后一次失败原因
    public DateTime? LastSuccessAt;
    public int ConsecutiveFailures;
}

public static class IndexingStatusBus
{
    public static event Action<IndexingStatusSnapshot> StatusChanged;
    public static IndexingStatusSnapshot Current { get; }
    internal static void Publish(IndexingStatusSnapshot s);
}
```

### 5.3 UI 表现 (静默优先)

**IndexingPanel (深度面板)**:
- 顶部新增一行 "Auto-Index: [On/Off]" + "Status: Running 12/47" 的静默徽标
- "Full Index" / "Incremental" / "Clear" 按钮保留, 但不再 disable; 改为允许用户**插队**手动触发
- 新增 "Pause Auto-Index" 临时关停 (本次会话有效)

**ChatWindow Hub** (主入口轻量提示):
- 在状态栏 (或会话头部) 加一个小 chip:
  - Idle → 不显示
  - Pending/Running → 灰色 "Indexing N files..." (鼠标 hover 显示进度详情)
  - Failed → 黄色 "Index failed - retry in 30s"
- 永不弹 Modal, 永不 disable Chat 输入

**Console (低优先级日志)**:
- Info: "Indexed 42 files in 1.2s" (仅在 Verbose 模式)
- Warning: "Indexing failed for 3 files (skipped)"
- Error: "Background indexing service stopped after 3 consecutive failures, fall back to manual"

### 5.4 与 LLM 工具的协同

- `search_code` 工具读取走 SQLite, 启用 WAL 后**与后台写入并发安全**, 不需要等待。
- `search_code(action='status')` 返回 `IndexingStatusBus.Current`, LLM 可主动判断"索引正在更新中, 结果可能稍旧"。
- 现有 `index_full` / `index_incremental` 两个 LLM action 保留, 作为"用户/LLM 强制立即"的逃生口。

---

## 6. 失败处理与降级

| 失败场景 | 处理策略 |
|----------|---------|
| 单文件 Roslyn 解析失败 | 记到 `IndexedFile.HasErrors = true`, 跳过该文件, 任务整体继续 |
| SQLite 写入失败 (锁/磁盘满) | 任务退出 → Failed 状态; backoff 5s → 30s → 120s; 3 次失败 → 进入 Disabled, 等待用户手动 Retry |
| Workspace 解析失败 (无 Unity root) | 标记 Disabled, 用户切换 workspace 后通过 `WorkspaceContextService` 事件唤醒 |
| Index version 不匹配 | 现有逻辑: 自动降级为 full; backoff 一次后台执行 |
| Domain Reload 中断任务 | beforeAssemblyReload → CTS.Cancel + 回写 dirty set; reload 后 InitializeOnLoad 自动恢复 Pending |
| PlayMode 进入 | 立即 Cancel 当前任务, 状态切 Disabled; 退出 PlayMode 自动恢复 |
| 用户手动 Full Index 期间产生新 dirty | 全量任务完成后, dirty set 不清; 调度器看到非空再触发 incremental 即可 |
| 同 workspace 多个 ChatWindow | StatusBus 是进程内单例, 多窗口共享; 调度器进程内单任务保证 |

---

## 7. 性能预期与节流策略

### 7.1 单文件成本估算 (基于现有 RoslynSymbolExtractor)

| 文件规模 | 解析时间 (中位) | 备注 |
|----------|----------------|------|
| < 200 行 | 5-15 ms | 99% 的工程文件 |
| 200-1000 行 | 20-80 ms | |
| 1000-3000 行 | 100-300 ms | 大型 Manager 类 |
| > 3000 行 | 500ms+ | 应警告并允许跳过 |

### 7.2 节流参数 (默认值, 可配)

| 参数 | 默认 | 说明 |
|------|------|------|
| `QuietDelayMs` | 2000 | dirty 进入后, 等待静默 N ms 才触发任务 |
| `MaxBatchFiles` | 200 | 单次任务最多处理多少文件; 超出分批多轮 |
| `YieldEveryNFiles` | 5 | 每解析 N 个文件 await Task.Yield 一次 |
| `MaxConsecutiveFailures` | 3 | 连续失败上限, 触发 Disabled |
| `BackoffSeconds` | [5, 30, 120] | 失败后退避序列 |
| `MaxFileSizeKB` | 1024 | 超大文件直接 skip + warning |
| `RespectIsCompiling` | true | 编译中暂缓任务 |
| `RespectIsPlaying` | true | PlayMode 中暂缓任务 |

### 7.3 git pull 后的典型流程模拟

```
T+0.0s   git pull (用户在 IDE 外完成, 改动 350 个文件)
T+0.0s   用户切回 Unity 窗口 → Unity 自动 refresh asset database
T+0.5s   Unity 完成 import, 触发 OnPostprocessAllAssets(imported=350)
T+0.5s   DirtyTracker 收到 350 paths, 持久化到 Library/agentcore-indexing-dirty.json
T+0.5s   Scheduler 看到 dirty>0, 启动 quiet timer (2s)
T+0.6s   编译触发 (CompilationPipeline) → Scheduler 等待 isCompiling=false
T+3.2s   编译完成, isCompiling=false
T+5.2s   quiet timer 到期 (假设无新 dirty), Scheduler 触发 BackgroundIndexService
T+5.3s   主线程取 workspace snapshot (~5ms), Task.Run 进入后台
T+5.3s+  后台增量: 350 个文件分 2 批 (200+150), 每文件 await 一次
         总耗时 ~ 350 * 25ms = 8.75s, 每 5 文件 yield 一次, UI 不卡
T+14s    任务完成, dirty set 清空, StatusBus → Idle
```

期间用户的 Chat 输入、search_code 调用全程可用 (SQLite WAL 读路径不被阻塞)。

---

## 8. 配置项 (新增到 Settings)

`AgentCoreSettings` 或独立的 `IndexingSettings` 中新增:

```csharp
[Serializable]
public class IndexingAutoSettings
{
    public bool AutoIndexEnabled = true;
    public int QuietDelayMs = 2000;
    public int MaxBatchFiles = 200;
    public int YieldEveryNFiles = 5;
    public int MaxFileSizeKB = 1024;
    public bool RespectIsCompiling = true;
    public bool RespectIsPlaying = true;
    public int MaxConsecutiveFailures = 3;
    public bool VerboseLogging = false;
}
```

在 `IndexingSettingsContribution` 中暴露 UI (Indexing 组件已经有 settings page, 直接扩展即可)。

---

## 9. 风险与权衡

| 风险 | 评估 | 缓解 |
|------|------|------|
| `OnPostprocessAllAssets` 与 Unity import 同帧, 路径可能尚未稳定 | 低 | 在 Postprocess 内只入队, 实际索引由 Scheduler 后台执行, 此时文件已稳定 |
| 大型项目 (>10k 文件) 全量首次索引仍很慢 | 中 | 全量任务允许后台执行, 同样走 Scheduler; 进入后台前提示用户耗时预期 |
| SQLite 在并发读写时锁竞争 | 中 | 启用 WAL, 验证现有 `IndexStoreFactory` 配置; 实测读延迟 |
| 主线程 workspace 解析耗时 | 低 | 资源缓存 + 仅在任务启动时取一次快照 |
| dirty set 持久化文件损坏 | 低 | reload 时若反序列化失败, 静默重置为空集 + 触发一次 incremental 兜底 |
| 与现有手动 Full/Incremental 按钮重叠 | 低 | 保留按钮作为"立即触发"逃生口; 共用同一调度器, 不并发 |
| 用户在 PlayMode 中点 Run Index | 低 | 调度器查 isPlaying=true → 立即排队但不执行, 退出 PlayMode 后自动开跑 |
| 跨 Domain Reload 状态丢失 | 高 (如不处理) | dirty set + last status 都需要持久化到 `Library/`; 已纳入设计 |
| Scheduler 与 LLM `index_full` 工具竞争 | 低 | 工具调用直接进入同一 BackgroundIndexService 队列, 不并发 |
| 资源监控开销 (每帧 update tick) | 极低 | tick 仅做 `if (dirty.Count == 0) return;`, 平均 < 1µs |

---

## 10. 分阶段交付计划

### Phase A — 引擎补强 (无 UI 变化)

| 任务 | 文件 | 验收 |
|------|------|------|
| A1 | 新增 `RunTargetedIncrementalAsync` | `CodebaseIndexer.cs` | 单测/手动: 传入 N 个文件, 仅这 N 个被处理 |
| A2 | 启用 SQLite WAL 模式 | `IndexStoreFactory.cs` | 写入期间能并发读出旧记录 |
| A3 | `RoslynSymbolExtractor` 增加 MaxFileSizeKB skip | `RoslynSymbolExtractor.cs` | 超大文件 warning + skip, 不抛 |

### Phase B — 调度核心

| 任务 | 文件 (新增) | 验收 |
|------|-------------|------|
| B1 | `IndexingDirtyTracker` (含持久化) | `Editor/Indexing/Core/IndexingDirtyTracker.cs` | reload 后 dirty set 完整恢复 |
| B2 | `IndexingAssetWatcher` (AssetPostprocessor) | `Editor/Indexing/Core/IndexingAssetWatcher.cs` | 改一个 .cs, dirty set +1 |
| B3 | `IndexingStatusBus` | `Editor/Indexing/Core/IndexingStatusBus.cs` | 事件订阅工作 |
| B4 | `BackgroundIndexService` (含 yield gate / backoff) | `Editor/Indexing/Core/BackgroundIndexService.cs` | git pull 模拟: dirty 350 → 后台完成, UI 不卡 |
| B5 | `IndexingAutoSettings` + 注入 `AgentCoreSettings` 版本迁移 | `Editor/Indexing/Config/IndexingAutoSettings.cs` | 旧设置加载兼容 |

### Phase C — UI 静默化

| 任务 | 文件 | 验收 |
|------|------|------|
| C1 | IndexingPanel 顶部 status badge + Auto-Index toggle | `Editor/Indexing/UI/IndexingPanel.cs` | 状态实时刷新, 不阻塞 |
| C2 | ChatWindow Hub 状态 chip | `Editor/UI/ChatWindow.Hub.cs` | Idle 隐藏, Pending/Running 显示, hover 详情 |
| C3 | IndexingSettingsContribution 暴露 Auto 配置 | 现有 IndexingSettings 入口 | 修改设置即时生效 |

### Phase D — 工具协同 (可选, 低优先级)

| 任务 | 文件 | 验收 |
|------|------|------|
| D1 | `search_code` 增加 `status` action | `Editor/Indexing/Tools/SearchCodeTool.cs` | LLM 可查询当前状态 |
| D2 | VCS 组件 pull 完成事件 → 立即触发 dirty flush | VCS 组件 | 减少 import 等待时间 |

**建议交付顺序**: A → B → C; D 单独排期。Phase A+B 已可用 (无 UI), Phase C 让用户感知。

---

## 11. 编码前对齐确认清单 (用户确认 2026-06-15)

| # | 决策点 | 最终决策 | 来源 |
|---|--------|---------|------|
| **Q1** | 默认是否开启 Auto-Index? | ✅ **开启** | 用户明示 |
| **Q5** | ChatWindow 状态 chip 位置? | ✅ **会话头部右侧** | 用户明示 |
| **Q9** | 版本号? | ✅ **v1.1.0 (Minor)** — 从 v0.10.0 重定位（v1.0.0 已为 Phase 6 验收完成里程碑，本设计派生为 Phase 7 §3.1） | 用户明示 + ADR-11 调整 |
| **Q10** | SOUL.md / TOOLS.md.template 是否更新? | ✅ **更新** (告诉 LLM 索引可能后台更新中) | 用户明示 |
| Q2 | QuietDelayMs 默认值? | ⚪ **2000ms** (沿用建议默认, §7.2) | 待用户确认 |
| Q3 | 单批 MaxBatchFiles 上限? | ⚪ **200** (沿用建议默认, §7.2) | 待用户确认 |
| Q4 | PlayMode 中是否暂缓索引? | ⚪ **暂缓** (沿用建议默认, §7.2 RespectIsPlaying=true) | 待用户确认 |
| Q6 | 大文件 (>1MB) 处理策略? | ⚪ **skip + warning** (沿用建议默认, §7.2 MaxFileSizeKB=1024) | 待用户确认 |
| Q7 | 连续失败 3 次后处置? | ⚪ **Disabled, 等用户手动 Retry** (沿用建议默认, §6) | 待用户确认 |
| Q8 | 是否需要 D1 (search_code 增加 status action)? | ⚪ **需要** (与 Q10 SOUL/TOOLS 更新强相关; LLM 需此 action 才能感知后台状态) | 待用户确认 |

> 标注 ⚪ 的项目沿用 §7.2 / §6 默认建议; 如需调整请在 Phase A 启动前提出。
> 注: Q8 和 Q10 强相关 — Q10 决定告诉 LLM "索引可能在后台更新中", Q8 提供让 LLM 自己查询状态的能力。两者搭配才完整, 建议保持 Q8=需要。

### 11.1 SOUL.md 增补点 (待编码时落地, 不在本轮实施)

在 SOUL.md 适当 section (建议归入"工具使用规范"或新增"代码搜索"小节) 增补:

- 当用户刚完成 git pull / svn update / 大量文件改动后, 索引可能正处于**后台增量更新**中, 短期内 (数秒到数十秒) `search_code` 结果**可能稍旧**
- 如果用户问"为什么找不到我新加的类 / 我新写的方法", LLM 应:
  1. 先用 `search_code(action='status')` 查询当前索引状态
  2. 若 state 为 `Pending` / `Running`, 主动告知用户"索引正在更新中 (N/M files)", 建议稍候重试
  3. 若 state 为 `Failed`, 告知用户失败原因并建议手动 Retry
  4. 若 state 为 `Disabled` (连续失败), 建议用户手动触发 `index_incremental`
- 对话中**不要**主动建议用户"重启 Editor 解决", 索引系统设计为自愈

### 11.2 TOOLS.md.template 增补点

在 `search_code` 工具说明中:

- **工具描述顶部**新增一句: "代码索引为**后台异步增量**模式, 默认在文件变更后 ~2s 自动更新; 后台索引中读路径不阻塞, 可继续查询 (基于上次成功的快照)"
- **新增 action: `status`** —
  - 返回字段: `state` (Idle/Pending/Running/Failed/Disabled), `dirtyFileCount`, `processedFiles`, `totalFiles`, `currentFile`, `lastError`, `lastSuccessAt`, `consecutiveFailures`
  - 用途: 让 LLM 在用户问"为什么搜不到"时主动判断索引是否落后
- **现有 action `index_full` / `index_incremental`** 描述补充: "强制立即索引 (逃生口); 通常**不需要主动调用**, 后台调度会自动处理"
- 强调对用户的影响: 后台索引**不会阻塞 Chat 输入、不会弹 Modal**; 仅在 ChatWindow 会话头部右侧显示状态 chip

---

## 12. 关键回答 (针对用户原始问题)

> "code indexing能做成后台静默式且增量式的设计吗?"

**完全可以, 而且代价不大**:

1. **增量算法已经存在** (`RunIncrementalIndexAsync`), 只需补一个 `RunTargetedIncrementalAsync` 跳过全盘 walk。
2. **后台执行已经具备** (现有 fire-and-forget Task), 只需把"用户点按钮"改为"OnPostprocessAllAssets 触发"。
3. **不阻塞 UI 的关键** = SQLite 启用 WAL + Task.Run 后台 + 每 N 文件 await Task.Yield, 这三件事都是 Unity Editor 已支持的。
4. **核心新增** = 三层调度 (DirtyTracker / Coalescing Scheduler / BackgroundIndexService) + 状态总线 + 静默 UI。

> "更新一次就 indexing 一次且完全阻断用户交互, 这样的设计就非常不合理了"

**完全同意**, 当前设计确实是同步式手动触发, 只适合"首次建立索引"场景, 不适合"开发中持续维护"。本设计把它从"手动 + 阻塞"重构为"自动 + 静默 + 可中断", 用户感知层面只在状态栏看到一个 chip, 不会再被任何操作打断。

> "你有什么想法吗?"

核心想法已落到上文 Section 3-7。两个最重要的差异点:

1. **不要用 FileSystemWatcher**, 用 Unity 自带的 `OnPostprocessAllAssets` —— 这是 Unity 已经替我们做完了 import 同步、路径规范化、过滤元文件等繁重工作的事件源, FileSystemWatcher 在这里只会带来竞争和误报。
2. **dirty set 必须持久化**, 否则 Domain Reload (脚本编译触发) 会让你刚记下的 350 个待处理文件全部丢失, 用户感知就是"明明改了代码但 search_code 找不到"。这是 Unity Editor 里所有"持续后台任务"都必须迈过的坎。

请确认 §11 的对齐清单后再进入编码阶段。
