# ADR-19: 主线程阻塞消除重构 —— 让 Unity Editor 在 LLM 请求期间保持响应

> **状态**: ~~Draft~~ → **Superseded (2026-07-11)**
> **决策人**: 项目 PO / 唯一用户
> **触发**: 用户报告"Unity 每次对话都弹 Hold on 对话框，UI 完全冻结"，1.5.x 起就有的顽疾
> **前置阅读**: [`AgentLoop.cs:344-489`](../Editor/Core/AgentLoop.cs) `SendMessageAsync` / [`WorkspaceSnapshotBuilder.cs`](../Editor/Core/WorkspaceSnapshotBuilder.cs) / [`AsyncHelper.cs`](../Editor/Utils/AsyncHelper.cs)
> **影响文档**: 不推翻现有 ADR；作为独立架构级修复。
> **相关**: [1.6.2 PendingIndicator](../CHANGELOG.md) 是 UI 层缓解，本 ADR 是根本修复

---

## 0. Spike 结论 —— 本 ADR 已被实测推翻（2026-07-11）

> **以下 §1-§12 的原设计基于未经验证的假设，spike 实测后全部否定。保留原文供历史追溯，但切勿据此实施。**

### 0.1 实测数据（[事实]）

| 原假设 | 实测值 | 结论 |
|--------|--------|------|
| `SendMessageAsync` 同步段 1.1-4.3s | **39ms** | ❌ 否定 |
| `WorkspaceSnapshotBuilder.Build` 500-2000ms | **32ms** | ❌ 否定 |
| 瓶颈在前段同步代码 | 瓶颈在**流式解析循环** | ❌ 否定 |
| 需要跨模块后台化重构 | 只需 SSE 解析循环加 yield | ❌ 否定 |

### 0.2 真实根因

`StreamingResponseParser.ParseStreamAsync` 的 `while` 循环（`ReadLineAsync + ParseChunkJson + onChunk`）全在主线程同步执行。长回复时数百 chunk × ~25ms/chunk，主线程连续占用 >500ms，触发 Unity 内置 Hold on 保护。短回复（1 chunk）不触发。

### 0.3 实际采用的修复（v1.6.3，已实现）

- `StreamingResponseParser.ParseStreamAsync` 每 `YieldEveryNChunks = 8` 个 chunk `await Task.Yield()`
- 8 chunk × ~25ms ≈ 200ms 让步一次，远低于 500ms Hold on 阈值
- v1.6.4 改进为 `YieldBudgetMs = 200` 时间预算制（替代固定 N chunk）
- `SendMessageAsync` 同步准备段后追加一次 `await Task.Yield()` 保证 PendingIndicator 渲染

### 0.4 教训

- **证据优先于设计**：原 ADR 在未做 spike 的情况下规划了 4 Phase / 6-9h 重构，实际只需 4 行代码
- §1.2 的阻塞链时间估算全部来自代码阅读推断，无一行实测数据
- 未来性能类 ADR 必须先有 spike 数据再规划方案

### 0.5 与原 ADR 的关系

- §1-§12 原文保留，不删除，供历史追溯
- §4 的"重构策略"（Snapshot Coalescing + Yield Point）中，只有 **Yield Point** 概念被实际采用，但应用位置从 `SendMessageAsync` 前段改到了 `StreamingResponseParser` 循环
- §5 的"分阶段实施计划"（Phase 1-4）**全部未执行**，也不需要执行
- §10 的五个决策点**全部 moot**

---

## 1. 问题陈述（原文保留，假设已被 §0 推翻）

### 1.1 现象（[事实]）

用户每次触发 LLM 对话，Unity Editor 弹出 "Hold on" 对话框：
```
Hold on
UnitySynchronization.ExecuteTasks
Waiting for Unity's code to finish executing.
```

含义：**Unity 主线程被同步任务阻塞超过阈值**（约 500ms），编辑器界面完全冻结。所有 UI Toolkit 更新（包括 1.6.2 刚做的 PendingIndicator / ThinkingDrawer preview）**都无法渲染**，因为主线程本身在跑同步业务代码。

### 1.2 阻塞链证据（[事实]）

从 `OnSendClicked` 到第一次真正 async I/O 让出主线程之间的**同步代码路径**：

```
[UI 线程]
  OnSendClicked (Input.cs:16)
    → AsyncHelper.RunAsync(() => SendMessageAsync(text))
        └→ async void，第一 await 前同步执行
    → [UI 线程] SendMessageAsync (AgentLoop.cs:344)
        ├─ ChatMessage.User + ConversationTurn 添加            [< 5ms]
        ├─ SessionManager.MarkDirty                             [< 10ms]
        ├─ SetCurrentSelfChallengeTurnId                        [< 5ms]
        ├─ PrepareSelfChallengeDataForNewTurn                   [50-200ms]  ← 可后台
        ├─ BuildNodeAInstructionForCurrentTurn                  [10-50ms]   ← 可后台
        ├─ [首轮] WorkspaceSnapshotBuilder.Build                [500-2000ms] ← ★主要嫌疑，Unity API 主线程
        ├─ [首轮] Deferred Context 拼接                          [< 10ms]
        ├─ SyncSkillMessages (SkillRegistry.EnsureScanned 首次)  [200-500ms] ← 可后台（纯磁盘 IO）
        ├─ BuildToolDefinitions                                 [50-200ms]  ← 可后台（内存计算）
        └─ RunToolCallLoopAsync
            └─ [UI 线程] JSON 序列化 messages（~10-100KB）       [50-300ms]  ← 可后台
            └─ HttpClient.SendAsync (首轮 TLS)                    [首轮 200-1000ms]
                └─ await 让出线程 ★UI 到这里才有机会渲染
                
[总同步耗时：首轮 1.1-4.3秒 / 后续 320-1250ms]
```

**结论**：UI 层的 PendingIndicator 等无法显示，因为**它们的渲染指令排在主线程队列里，主线程还在同步业务代码里跑**。

### 1.3 为什么 async void RunAsync 不能救

```csharp
public static async void RunAsync(Func<Task> asyncFunc, ...)
{
    try { await asyncFunc(); } catch ...
}
```

`await asyncFunc()` **不会立即让出线程**。C# 编译器把它编译成状态机：
1. 同步执行 `asyncFunc()` 直到内部第一个真正 async 操作
2. 如果 `asyncFunc` 前段全是 CPU 密集/同步 IO，主线程被吃掉，控制权直到 first-real-await 才让出

`AsyncHelper.RunAsync` 只解决"异常吞掉"和"async void 隔离"问题，**不解决主线程阻塞**。

---

## 2. 设计约束（Non-Negotiable）

### 2.1 [ADR-17] 极简哲学

- 不引入新用户可见开关
- 内部性能优化不该暴露给用户
- 现有 API 契约（`SendMessageAsync` 签名 / event 顺序）不能破坏

### 2.2 Unity 主线程规则（技术约束）

**必须主线程执行**（无法后台）：
- `EditorSceneManager.*`、`AssetDatabase.*`、`EditorApplication.*`、`CompilationPipeline.*`
- 所有 UI Toolkit 元素操作
- `Application.dataPath` 访问（首次会触发 Unity 初始化）

**可后台执行**：
- 纯 CPU 计算（JSON 序列化 / prompt 拼装 / regex 匹配）
- 纯磁盘 IO（`File.ReadAllText` 读非 Unity 管理文件如 `.agents/skills/*.md`）
- `HttpClient.SendAsync` / `Task.Delay` 等已 async 的 API

### 2.3 不破坏并发正确性

- Skill / Session / Compression 等状态字段**只在主线程读写**（现有约定）
- 后台线程结果**必须 marshal 回主线程**再修改 state
- 使用 `TaskScheduler.FromCurrentSynchronizationContext()` 或 `EditorApplication.delayCall` 桥接

---

## 3. 目标 & 非目标

### 目标

- **首轮 SendMessageAsync 从"发送到 UI 有反应"< 100ms**（当前 1.1-4.3s）
- **后续轮次 < 50ms**
- **Hold on 对话框不再出现**
- 保持事件顺序契约（StateChanged / StreamToken / AssistantMessage 顺序不变）
- 0 破坏现有功能

### 非目标

- 不优化 LLM 请求本身的网络耗时（不可控）
- 不优化 tool 执行的耗时（那是各自 tool 的责任）
- 不做 background indexing 相关重构（`BackgroundIndexService` 已有独立系统）
- 不做 UI 层重构（PendingIndicator 等已在 1.6.2 完成）

---

## 4. 重构策略：Snapshot Coalescing + Yield Point 双管齐下

### 4.1 核心思路

将 `SendMessageAsync` 前段的同步工作拆为 3 类，用不同策略处理：

| 类别 | 内容 | 策略 |
|------|------|------|
| **A. 快速主线程** | 参数校验 / 消息添加 / State 更新 | 保持主线程，加起来 <20ms |
| **B. 可后台的 CPU/磁盘** | SkillRegistry / BuildToolDefinitions / BuildNodeAInstruction / JSON 序列化 | `Task.Run` 后台执行，marshal 回主线程组装 |
| **C. 必须主线程但耗时** | WorkspaceSnapshotBuilder / PrepareSelfChallengeDataForNewTurn | 首轮预加载 + 缓存；每轮之间 `Task.Yield()` 让 UI 渲染一帧 |

### 4.2 关键设计模式：Yield Point + Background Precompute

**Yield Point 模式**：
```csharp
public async Task SendMessageAsync(string userMessage)
{
    // A: 快速主线程操作（<20ms）
    ValidateInput(userMessage);
    AddUserMessageToHistory(userMessage);
    
    // Yield 1: 让 UI 立刻渲染 PendingIndicator
    await Task.Yield();
    
    // C: 主线程但耗时的操作（拆分并夹 yield）
    await BuildCurrentTurnContextAsync(); // 内部会分段 yield
    
    // B: 后台并行准备（可与主线程 UI 渲染并行）
    var toolDefsTask = Task.Run(BuildToolDefinitions);
    var syncSkillsTask = Task.Run(() => SyncSkillMessages());
    
    await Task.WhenAll(toolDefsTask, syncSkillsTask);
    var toolDefs = toolDefsTask.Result;
    
    // 后台完成后主线程组装最终 messages 并发起 LLM 请求
    await RunToolCallLoopAsync(assistantTurn, toolDefs, ct);
}
```

### 4.3 关键子任务拆分

#### 4.3.1 WorkspaceSnapshotBuilder 主线程但可分段

**现状**：一次性调用 `Build()`，内部 9 个步骤全同步串行 → 500-2000ms 阻塞
**改造**：
- 拆为 `BuildAsync()`，每 100ms 逻辑单元后 `await Task.Yield()`
- 相邻 yield 之间不超过 100ms → UI 每 100ms 有渲染机会 → 不再触发 Hold on
- 每个 Unity API 调用组之间加 yield（scene / play mode / compilation / active object / log entries 5 组）

#### 4.3.2 SkillRegistry 后台化

**现状**：`EnsureScanned` 首次调用同步读 53 个 .md 文件 → 200-500ms 阻塞
**改造**：
- 新增 `EnsureScannedAsync()` — 内部 `await Task.Run(() => ScanDirectorySynchronous())`
- 保留旧 `EnsureScanned()` 作为向后兼容
- `SyncSkillMessages` 改造为 `SyncSkillMessagesAsync()`（load skill content 也走 Task.Run）

#### 4.3.3 PrepareSelfChallengeDataForNewTurn 后台化

**现状**：完全同步（Node A skip rules / regex 匹配 / SelfChallengeData 构造）
**改造**：主体走 `Task.Run`，最后 `SelfChallenge.` 状态字段的写入 marshal 回主线程

#### 4.3.4 BuildToolDefinitions 后台化

**现状**：`ToolRegistry.Instance` + `AgentCoreSettings.instance` 都是 ScriptableSingleton，可能触发 Unity API
**验证需要**：`ScriptableSingleton.instance` 首次访问是否主线程 only？—— [推断] 是。**只做后续轮次的 Task.Run，首轮仍主线程**

#### 4.3.5 JSON 序列化后台化

**现状**：`Newtonsoft.JsonConvert.SerializeObject(messages)` 在 HttpClient.SendAsync 前主线程执行
**改造**：在 LLM Client 层加 `await Task.Run(() => SerializeObject(...))`

---

## 5. 分阶段实施计划

### Phase 1：Yield Point 注入（1-2 小时，风险低）

**目标**：立刻消除 Hold on，UI 层能正常渲染

**改动**：
- [`AgentLoop.cs:365`](../Editor/Core/AgentLoop.cs) `SendMessageAsync` 参数校验/消息添加之后加 `await Task.Yield()`
- [`WorkspaceSnapshotBuilder.cs`](../Editor/Core/WorkspaceSnapshotBuilder.cs) 拆 `Build()` 为 `BuildAsync()`，5 个 API 组之间各加一次 `await Task.Yield()`
- [`AgentLoop.cs:451`](../Editor/Core/AgentLoop.cs) 调用改为 `await WorkspaceSnapshotBuilder.BuildAsync()`

**验证**：
- Unity 编译无错
- 打开 AgentCore，发送消息，观察是否仍弹 Hold on
- 预期：Hold on 消失或频率大幅降低

**回退**：单个 apply_diff 覆盖，可随时 revert

### Phase 2：后台化 SkillRegistry + SelfChallenge（2-3 小时，风险中）

**目标**：进一步降低主线程压力

**改动**：
- 新增 `SkillRegistry.EnsureScannedAsync()` / `SkillRegistry.GetContentAsync()`
- 修改 [`AgentLoop.SkillContext.cs`](../Editor/Core/AgentLoop.SkillContext.cs) `SyncSkillMessages` → `SyncSkillMessagesAsync`
- 修改 [`AgentLoop.cs:465`](../Editor/Core/AgentLoop.cs) 调用点 `await SyncSkillMessagesAsync()`
- SelfChallenge 主体计算走 `Task.Run`，最后 `SetCurrentSelfChallengeTurnId` 等 state 写入回主线程

**验证**：
- Skill list/load/unload/reload 全流程仍工作（回归测试）
- Node A 反问机制仍正常触发
- 无并发写入 `_currentSelfChallengeData` 等 state 字段

**风险**：SelfChallenge 状态字段访问需要小心 —— **必须保证主线程独占**

### Phase 3：BuildToolDefinitions + JSON 序列化后台化（2-3 小时，风险中）

**目标**：把最后的同步"大件"移走

**改动**：
- 修改 [`BuildToolDefinitions`](../Editor/Core/AgentLoop.Tools.cs) 返回 `Task<List<ToolDefinition>>`
- 修改 [`OpenAICompatibleClient.ChatCompletionStreamAsync`](../Editor/LLM/OpenAICompatibleClient.cs) 序列化前加 `await Task.Run`
- 前置检查 `ScriptableSingleton.instance` 首次访问是否 main-thread only（若是，首轮主线程，后续 Task.Run）

### Phase 4：AsyncHelper.RunAsync 加 Task.Yield() 保底（30 分钟，风险极低）

**目标**：即便未来引入新的同步代码，也能保证 UI 一次渲染机会

**改动**：
- [`AsyncHelper.cs:42`](../Editor/Utils/AsyncHelper.cs) 在 `await asyncFunc();` 之前加 `await Task.Yield();`

**理由**：所有走 `AsyncHelper.RunAsync` 的调用都受益，未来防御新增卡顿代码

---

## 6. 详细的每一步改动清单

### Phase 1 详细改动

#### 6.1.1 AgentLoop.cs SendMessageAsync 入口 yield

```diff
public async Task SendMessageAsync(string userMessage)
{
    // 1. 参数校验（快，<5ms）
    if (string.IsNullOrWhiteSpace(userMessage)) throw ...;
    if (!_isInitialized) throw ...;
    
+   // Yield 让 UI 层渲染 PendingIndicator 至少一帧
+   await Task.Yield();
    
    // 后续原逻辑...
}
```

#### 6.1.2 WorkspaceSnapshotBuilder 加 BuildAsync

**新增方法**：`public static async Task<string> BuildAsync()`

拆分 `Build()` 内部逻辑为 5 段，段间 `await Task.Yield()`：
- 段 1：Scene（EditorSceneManager.GetActiveScene）
- 段 2：Play Mode（EditorApplication.isPlaying）  
- 段 3：Compilation status（CompilationPipeline.GetAssemblies）
- 段 4：Active object（Selection + AssetDatabase.GetAssetPath）
- 段 5：Log entries（反射 UnityEditor.LogEntries）

保留 `Build()` 同步版本作为向后兼容（DomainReload restore 可能仍需同步版）。

#### 6.1.3 AgentLoop.cs 调用改造

```diff
if (IsFirstUserMessage())
{
    // ... Deferred Context 同步（快）
    
-   var snapshot = WorkspaceSnapshotBuilder.Build();
+   var snapshot = await WorkspaceSnapshotBuilder.BuildAsync();
    if (!string.IsNullOrEmpty(snapshot))
        _messages.Insert(_messages.Count - 1, ChatMessage.System(snapshot));
}
```

### Phase 2 详细改动

#### 6.2.1 SkillRegistry 加异步接口

```csharp
public async Task<IReadOnlyList<SkillMetadata>> GetAllAsync()
{
    await EnsureScannedAsync();
    lock (_lock) { return _skills.Values.OrderBy(...).ToList(); }
}

public async Task<string> GetContentAsync(string name)
{
    var meta = await TryGetAsync(name);
    if (meta == null) return null;
    return await Task.Run(() => File.ReadAllText(meta.FilePath))
        .ContinueWith(t => SkillFrontmatterParser.Parse(t.Result).Body);
}

private async Task EnsureScannedAsync()
{
    if (_isScanned) return;
    // 磁盘扫描完全后台化
    await Task.Run(() => { /* 原 EnsureScanned 逻辑 */ });
}
```

**关键点**：**必须加 lock**（原来单线程读写没问题，现在会跨线程）。

#### 6.2.2 SyncSkillMessages 异步化

```csharp
private async Task SyncSkillMessagesAsync()
{
    if (_skillScopeState == null) return;
    
    // 计算差集（读 skill state，主线程）
    var (toRemove, toAdd) = ComputeSkillDiff();
    
    // 后台读所有需要新加的 skill content
    var contents = new Dictionary<string, string>();
    foreach (var name in toAdd)
    {
        var content = await SkillRegistry.Instance.GetContentAsync(name);
        contents[name] = content;
    }
    
    // 回主线程组装 messages（保证 _messages 单线程访问）
    foreach (var idx in toRemove.OrderByDescending(x => x))
        _messages.RemoveAt(idx);
    
    foreach (var kv in contents)
    {
        var msg = SkillContentBuilder.Build(kv.Key, kv.Value);
        _messages.Insert(insertIndex, ChatMessage.System(msg));
    }
}
```

#### 6.2.3 PrepareSelfChallengeDataForNewTurn 后台化

```csharp
private async Task<SelfChallengeData> PrepareSelfChallengeDataForNewTurnAsync(string userMessage)
{
    // Skip rules（regex 匹配，CPU）+ SelfChallengeData 构造 → Task.Run
    return await Task.Run(() => PrepareSelfChallengeDataForNewTurnSynchronous(userMessage));
}
```

### Phase 3 详细改动

#### 6.3.1 BuildToolDefinitions 后台化

```csharp
private async Task<List<ToolDefinition>> BuildToolDefinitionsAsync()
{
    return await Task.Run(BuildToolDefinitionsSynchronous);
}
```

**风险**：`ToolRegistry.Instance.GetAllToolMetadata` 访问 ScriptableSingleton —— [验证] 首次调用是否主线程 only？如果是，首轮不能 Task.Run。

#### 6.3.2 JSON 序列化后台化

在 [`OpenAICompatibleClient.cs:110`](../Editor/LLM/OpenAICompatibleClient.cs) 前：
```csharp
var json = await Task.Run(() => JsonConvert.SerializeObject(request));
var httpRequest = HttpClientFactory.CreateRequest(...);
httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");
await client.SendAsync(httpRequest, ...);
```

### Phase 4 详细改动

```diff
public static async void RunAsync(Func<Task> asyncFunc, Action<Exception> onError = null)
{
    try
    {
+       // 保底：让 UI 至少渲染一帧再开始执行
+       await Task.Yield();
        await asyncFunc();
    }
    ...
}
```

---

## 7. 并发正确性证明

### 7.1 状态字段访问审计

现有必须"主线程独占"的字段：
- `_messages`（LLM 消息历史）
- `_conversationTurns`（UI 轮次）
- `_currentSelfChallengeData`（Self-Challenge 状态）
- `_skillScopeState`（Skill 加载状态）
- `_toolScopeState`（Tool 激活状态）

### 7.2 后台化的操作只做**无副作用的计算**

Phase 2/3 中所有 `Task.Run` 的内容都符合：
- **读**：只读 skill 磁盘文件 / 只读 ToolRegistry / 只读 settings
- **不写任何跨线程共享的字段**
- **返回值 marshal 回主线程再修改 state**

Unity 提供的 `TaskScheduler.FromCurrentSynchronizationContext()`（或 async/await 的默认捕获 context 行为）保证 `await` 后续代码回到主线程。

### 7.3 Session / Compression 一致性

- Session persistence 由 `AutoSave` 走 fire-and-forget，本 ADR 不动
- Compression 触发点在 tool loop 内，不受本 ADR 影响

---

## 8. 风险清单 & 缓解

| 风险 | 严重度 | 缓解 |
|------|--------|------|
| **Task.Yield 后 SynchronizationContext 丢失，代码跑到线程池** | 🔴 高 | Unity Editor 有 `UnitySynchronizationContext`（正是 Hold on 那个 stack trace 里的），Task.Yield 后仍回 Unity 主线程。**验证方法**：加 `Debug.Log(Thread.CurrentThread.ManagedThreadId)` 前后对比。若失效，改用 `EditorApplication.delayCall` 显式桥接 |
| **Task.Run 后 await 回到主线程时 UI 已经变化，state 不一致** | 🟡 中 | 所有 `await Task.Run(...)` 后立刻检查 `_isDisposed` / `_currentCts.IsCancellationRequested`。Cancel 路径不动 state |
| **WorkspaceSnapshotBuilder 分段执行期间 scene 已切换/编译已开始** | 🟢 低 | Snapshot 本来就是"一瞬间的快照"，中途变化不阻塞正确性。加日志记录 "snapshot in progress: scene changed"（可选） |
| **SkillRegistry lock 竞争** | 🟢 低 | 加 `_lock` 后所有读写走同一 lock。目前使用频率低（每轮 1-2 次），性能可接受 |
| **ScriptableSingleton.instance 首次访问在后台线程崩溃** | 🟡 中 | Phase 3 实施前先加 unit test：`Task.Run(() => AgentCoreSettings.instance)` 验证。若确实主线程 only，首轮走主线程 + fallback 记录 |
| **Node A / Node B 状态机因异步化出现新的竞争** | 🔴 高 | Phase 2 详细 review `AgentLoop.SelfChallenge.cs`：所有 `SetState(WaitingForClarification/ReviewingAnswer)` 必须在主线程；`SelfChallenge` 状态字段的写入必须在主线程 |
| **Domain Reload 期间正在跑的 Task.Run 被强制中断** | 🟡 中 | 现有 [`AgentLoop.DomainReload.cs`](../Editor/Core/AgentLoop.DomainReload.cs) 有 `beforeAssemblyReload` 事件；后台 Task 应加 `CancellationToken` 参数，Domain Reload 前取消 |
| **测试覆盖不足，回归风险高** | 🔴 高 | 每个 Phase 完成后**必须实机验证**（不只是编译）：至少测 5 个场景 — 简单对话 / 工具调用 / Node A 反问 / Skill load / Compression 触发 |

---

## 9. 验证方法（每个 Phase 完成后必做）

### 9.1 定量验证

**加临时性能日志**（可在 Phase 4 结束后删除）：
```csharp
// AgentLoop.cs SendMessageAsync 入口
var sw = Stopwatch.StartNew();
// ... 各关键点后：
Debug.Log($"[Perf] SendMessageAsync until first-real-await: {sw.ElapsedMilliseconds}ms");
```

**通过标准**：
- Phase 1 后：主线程连续同步 < 300ms（无 Hold on）
- Phase 2 后：< 150ms
- Phase 3 后：< 100ms

### 9.2 定性验证

**必测场景**：
1. **简单文本对话** — 发送"你好"，观察是否弹 Hold on，PendingIndicator 是否显示
2. **首轮工具调用** — 发送"列出场景根对象"，观察 tool 调用期间 UI 是否响应
3. **Node A 反问触发** — 发送模糊需求"帮我优化一下"，观察 LLM 反问过程中 UI 是否响应
4. **Skill load 场景** — 发送"帮我做个 Cinemachine 相机"，观察 skill 加载过程 UI 是否响应
5. **长会话 Compression 触发** — 30+ 轮对话后触发 Compression，观察 UI 是否响应
6. **Domain Reload during dialog** — 对话中修改 C# 触发 Domain Reload，观察是否正确恢复

### 9.3 回归验证

**必测所有已有功能**：
- 会话切换 / 保存 / 重置
- Node A / Node B Self-Challenge
- Skill load / unload / reload
- Tool call with confirmation
- Error handling / retry
- VCS Banner

---

## 10. 具体决策点（需你逐条确认）

### D1: 分几个 tarball 发布？

- **D1-a** 每个 Phase 独立 tarball（1.6.3 / 1.6.4 / 1.6.5 / 1.6.6）— 便于回滚，但用户测试疲劳
- **D1-b** 全部 4 个 Phase 合并为 1.7.0 一次性发布 — 一次性完整体验，但风险集中
- **D1-c** Phase 1+4 合并为 1.6.3（低风险），Phase 2+3 合并为 1.7.0（中风险）

**我的推荐**：**D1-c**。Phase 1+4 只加 yield，风险极低但立即消除 Hold on；Phase 2+3 涉及并发，需要更长测试期，独立版本便于观察。

### D2: SelfChallenge 状态字段的并发保护级别？

- **D2-a** 只在实际后台化的方法里加 `EnsureMainThread()` 断言 — 轻量
- **D2-b** 所有 SelfChallenge 状态字段的 setter 全部加 `EnsureMainThread()` 断言 — 严格但改动大
- **D2-c** 用 `ConcurrentDictionary` / `Interlocked` 等 lock-free 结构改造 — 最重

**我的推荐**：**D2-a**。80/20 原则，先只保护实际改动的边界，未来若发现新竞争再扩

### D3: 后台化的 tool call loop 是否也在本 ADR 范围？

- **D3-a** 只做 SendMessageAsync 前段，tool loop 保持现状（工具调用本身可能有主线程需求）
- **D3-b** tool loop 也改造，每次 tool 执行前 yield

**我的推荐**：**D3-a**。tool loop 每次都过 UI 层（Confirmation panel / tool card 显示），已经有天然的 yield 点。tool 内部性能是各自 tool 的责任

### D4: 失败重试策略？

- **D4-a** 后台任务失败时降级到同步（保证不阻塞流程）— 稳
- **D4-b** 后台任务失败时明确报错让用户重试 — 严

**我的推荐**：**D4-a**。SkillRegistry / SelfChallenge 磁盘 IO 失败极少，降级同步保证功能可用

### D5: 是否加 profile 日志？

- **D5-a** 只在 Debug builds 加 `[Perf]` 日志，Release 版删除
- **D5-b** 保留所有 Debug.Log 便于诊断
- **D5-c** 完全不加日志（依赖用户报告）

**我的推荐**：**D5-b**。AgentCore 不区分 Debug/Release（Editor-only），保留日志便于未来诊断，但用 `[Perf]` 前缀便于用户过滤

---

## 11. 未决问题（待验证后确认）

- [ ] `Task.Yield()` 后 `SynchronizationContext.Current` 是否仍是 Unity Editor 的？需要写一个 Unity console 里的小 test 验证
- [ ] `ScriptableSingleton<T>.instance` 首次访问是否 main-thread only？需查 Unity 文档 + 实测
- [ ] `AssetDatabase.LoadAssetAtPath` 在 `Task.Run` 里调用是否会抛异常？（用于 skill.md 加载 fallback）
- [ ] 长会话下 Compression 触发是否也是主线程阻塞？（本 ADR 未覆盖，但需要评估是否要独立处理）

---

## 12. 版本规划

- **1.6.2** ← 当前（PendingIndicator UI 层缓解，但主线程阻塞仍存在）
- **1.6.3** ← Phase 1 + 4 合并（Yield Point + AsyncHelper Yield 保底）—— 立即消除 Hold on
- **1.7.0** ← Phase 2 + 3（后台化重构）—— 主线程 < 100ms

---

## 13. 与已有 ADR 的关系

- **ADR-17（极简哲学）**：本 ADR 严格遵守 —— 0 用户可见开关，内部优化对外无感
- **ADR-18（Skill 加载）**：Phase 2 会改 SkillRegistry 接口，需要保持向后兼容（旧 sync 接口保留）
- **不推翻任何决策**

---

> **下一步**：等你 §10 五个决策点确认，我进入 Phase 1 实现。任意决策 override 都会调整对应 Phase 的实施策略。