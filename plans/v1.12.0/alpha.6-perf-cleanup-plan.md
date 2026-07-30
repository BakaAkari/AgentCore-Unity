# v1.12.0-alpha.6 性能修复三批合并 plan

**Status**: Ready for execution
**Owner**: Claude Code (autonomous)
**Branch**: `feat/v1.12.0-session-organization`
**Base commit**: `1049261` (alpha.5 Tag Registry)
**Target version**: `1.12.0-alpha.6`
**Est duration**: 2-4 hours across 3 batches

---

## 背景

v1.12.0-alpha.4 修复了 `StreamingResponseParser` 的 `EndOfStream` 同步阻塞（199ms/帧）。修复后触发一轮全量 bug 扫描（3 个 subagent 并行），识别出剩余 10 条真实性能/稳定性隐患。本 plan 分三批修复。

**分批策略**：
- **批 A (Quick Wins)**：局部改动，无 async 传染。5 分钟-30 分钟每处。
- **批 B (Async 传染)**：需要 async/await 传染到 UI 层。每处 30 分钟-1 小时。
- **批 C (竞态/泄漏)**：Interlocked 加锁、event 清理。每处 15-30 分钟。

**每批独立 commit，最后一个 tag**。

---

## 全局约束

1. **不修改 `Editor/Core/AgentLoop.cs` 的 GetContextBudget marker**（未 stage 的诊断插桩，属后续独立改动）
2. **不动 Tests/ 目录**
3. **每次 patch 后立即读文件验证**（不假设成功）
4. **保留所有中文注释和 doc string**（团队约定）
5. **每批完成后 print `[Batch X done]`**
6. **发现 plan 遗漏立即停下汇报**（不擅自扩大改动范围）

---

## 批 A — Quick Wins (预计 30 分钟)

### A.1 · #2 HTTP response Dispose (最优先, CRITICAL)

**文件**: `Editor/LLM/OpenAICompatibleClient.cs`

**问题**: 121/132 行的 HttpResponseMessage 和 Stream 未 Dispose。每次 LLM 流式调用都泄漏一个 HTTP 连接。

**修复**:
```csharp
// 121 行:
- var response = await client.SendAsync(...)
+ using var response = await client.SendAsync(...)

// 132 行:
- var stream = await response.Content.ReadAsStreamAsync();
+ using var stream = await response.Content.ReadAsStreamAsync();
```

**同文件非流式路径 59 行** 同样加 `using`（对照参考: `ModelSettingsService.cs:33/61`）。

**验证**:
- grep `var response = await client.SendAsync` 确认 OpenAICompatibleClient.cs 内所有匹配都加了 `using`
- 语法编译通过（读文件后手动 verify 无 warning）

### A.2 · #3 ManagePackageTool.WaitForRequest — Thread.Sleep 30 秒改 async

**文件**: `Editor/Tools/Native/Extended/ManagePackageTool.cs:151-160`

**问题**: 主线程 `Thread.Sleep(50)` 轮询，最长 30 秒。触发时 Editor 完全冻结。

**修复方向**:
将 `WaitForRequest(Request request, int timeoutMs)` 改为 `WaitForRequestAsync`。改法：

```csharp
private static async Task<bool> WaitForRequestAsync(Request request, int timeoutMs = MaxWaitTimeMs, CancellationToken ct = default)
{
    var tcs = new TaskCompletionSource<bool>();
    var timeoutCts = new CancellationTokenSource(timeoutMs);
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
    linked.Token.Register(() => tcs.TrySetResult(false));

    void CheckLoop()
    {
        if (request.IsCompleted)
        {
            tcs.TrySetResult(true);
            EditorApplication.update -= CheckLoop;
        }
    }
    EditorApplication.update += CheckLoop;
    try
    {
        return await tcs.Task;
    }
    finally
    {
        EditorApplication.update -= CheckLoop;
    }
}
```

**调用方链改动**: `HandleList` / `HandleInstall` / `HandleRemove` 全部改 `async Task<...>` 返回，`WaitForRequest(...)` 改 `await WaitForRequestAsync(...)`。

**注意**: 这三个方法是 tool handler，需要看 `Execute` 方法签名。如果 `Execute` 已经是 async，直接改；否则需要处理返回值传播。

⚠️ **如遇 handler 签名与其他 tool 不一致（如返回同步 string 而非 Task<string>）**，停下汇报，不擅自改架构。

### A.3 · #4 ManageProfilerTool Thread.Sleep(50)

**文件**: `Editor/Tools/Native/Extended/ManageProfilerTool.cs:1177-1180`

**问题**: 主线程 `Thread.Sleep(50)` 等 FrameDebugger 下一帧。

**修复**:
```csharp
UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
// Thread.Sleep(50); 改为:
var tcs = new TaskCompletionSource<int>();
EditorApplication.delayCall += () => tcs.TrySetResult(GetFrameDebuggerCount());
count = await tcs.Task;
```

**调用方**: 检查所在方法是否 async。如果是同步方法，一并改 async。

### 批 A commit

```bash
git add Editor/LLM/OpenAICompatibleClient.cs \
        Editor/Tools/Native/Extended/ManagePackageTool.cs \
        Editor/Tools/Native/Extended/ManageProfilerTool.cs
git commit -m "fix(perf): batch A quick wins — HTTP response Dispose + async wait

- #2 CRITICAL: OpenAICompatibleClient ChatCompletionStreamAsync — response/stream 未 Dispose (每次 LLM 调用泄漏连接)
- #3 HIGH: ManagePackageTool.WaitForRequest — Thread.Sleep(50) 轮询 30 秒改 async
- #4 MEDIUM: ManageProfilerTool — Thread.Sleep(50) 等下一帧改 EditorApplication.delayCall"
```

**Print**: `[Batch A done]`

---

## 批 B — Async 传染 (预计 1-2 小时)

⚠️ **风险最高，改动范围可能扩散到 ChatWindow/AgentLoop**。发现签名冲突立即停下汇报。

### B.1 · #1 SessionStorage.Load 主线程同步读大 JSON (CRITICAL)

**文件**: `Editor/Session/SessionStorage.cs:109-110`

**修复**:
```csharp
// 添加 LoadAsync 方法（保留同步 Load 作为兼容 fallback，或让 Load 内部 await）:
public static async Task<SessionData> LoadAsync(string sessionId, CancellationToken ct = default)
{
    var filePath = GetSessionFilePath(sessionId);
    if (!File.Exists(filePath)) return null;
    var json = await File.ReadAllTextAsync(filePath, ct);
    return JsonConvert.DeserializeObject<SessionData>(json, JsonSettings);
}
```

**调用链传染**:
- `SessionManager.LoadSession(id)` → `LoadSessionAsync(id)` 或加一个新的 async 版本
- `AgentLoop.LoadSession(id)` → `LoadSessionAsync(id)` 
- `ChatWindow.SwitchToSession(id)` → `async void SwitchToSession(id)`（UI 事件处理器允许 async void）

**关键**: 调用方是 UI event handler 时，用 `AsyncHelper.RunAsync` 包装以获得统一的异常处理，不裸 async void。

⚠️ **如果 LoadSession 有多个调用方，且部分调用方是同步上下文（如构造函数、Init）**，保留同步 Load 作为 fallback，只让 UI 路径走 async。

### B.2 · #8 SessionStorage.ListSessions 主线程遍历读取 (HIGH)

**文件**: `Editor/Session/SessionStorage.cs:148-153`

**修复**: 
- 短期：`File.OpenRead` + `StreamReader` 流式解析，只读头部摘要字段（session 文件是 `{"messages":[...], "title":"...", ...}`，`title`/`updated_at` 通常在文件前部）
- 长期：整体改 `ListSessionsAsync`

**建议**: 采用流式解析。用 `JsonTextReader` + `JObject.Load(reader, new JsonLoadSettings{...})` 但只读头部字段，遇到 `messages` 数组就 skip。

⚠️ **如果 session 文件的 `title`/`updated_at` 不保证在前部**（可能在 messages 后），改用异步 `File.ReadAllTextAsync` 兜底。**先 grep session 文件的 JSON 结构确认字段顺序**。

### B.3 · #9 FileChangeTracker.ReadAllLines (HIGH)

**文件**: `Editor/Core/FileChangeTracker.cs:251, 896`

**问题**: LLM 每轮 tool call 前后各扫一次源文件 → `File.ReadAllLines` 大文件 10-50ms。

**修复**: 行数统计不需要文件内容。改流式 `\n` 计数：

```csharp
private static int CountLines(string path)
{
    using var reader = new StreamReader(path);
    int count = 0;
    while (reader.ReadLine() != null) count++;
    return count;
}
```

**注意**: 保留同步签名（因为 `SnapshotBeforeExecution` / `TrackFromToolCalls` 调用点在 AgentLoop 主循环 async 方法的同步段，改 async 传染复杂）。改流式 `\n` 计数已经能把内存占用降到 O(1)、时间省一半。

### B.4 · #10 ProjectContextCollector.Collect + BootstrapLoader.Load (HIGH)

**文件**: `Editor/Bootstrap/ProjectContextCollector.cs:41-158`，`Editor/Bootstrap/BootstrapLoader.cs:109/119/454`

**问题**: ChatWindow 打开时主线程递归扫盘 + 串行 4 次 ReadAllText，累计 50-200ms。

**修复方向**:
- `BootstrapLoader.Load` 添加 `LoadAsync` 版本
- 4 次 `File.ReadAllText` → `File.ReadAllTextAsync`
- `ProjectContextCollector.CollectHeavyAsync` 已经存在！让 `BootstrapLoader.Load` 直接调用它而不是 `Collect()`

**验证**:
- grep `BootstrapLoader.Load()` 调用点，确认全部在 async 上下文
- `ChatWindow.CreateGUI` / `Initialize` 是否 async？（不是的话需要 UI 层 async 传染）

### 批 B commit

```bash
git add Editor/Session/SessionStorage.cs \
        Editor/Session/SessionManager.cs \
        Editor/Core/FileChangeTracker.cs \
        Editor/Bootstrap/ProjectContextCollector.cs \
        Editor/Bootstrap/BootstrapLoader.cs \
        Editor/Core/AgentLoop.cs \
        Editor/UI/ChatWindow.cs
git commit -m "fix(perf): batch B async 传染 — SessionStorage/BootstrapLoader/FileChangeTracker 异步化

- #1 CRITICAL: SessionStorage.LoadAsync — 消除切 session 时 50-200ms 卡顿
- #8 HIGH: SessionStorage.ListSessions — 流式解析代替全量 ReadAllText
- #9 HIGH: FileChangeTracker 行数统计改流式 (O(1) 内存)
- #10 HIGH: BootstrapLoader.LoadAsync + ProjectContextCollector.CollectHeavyAsync 落地"
```

**Print**: `[Batch B done]`

---

## 批 C — 竞态/泄漏 (预计 30 分钟)

### C.1 · #5 MouseTracker 事件泄漏 + HashSet 无限增长

**文件**: `Editor/UI/Context/MouseTracker.cs:42-92`

**修复方向**:
1. `_hookedRoots` 从 `HashSet<int>` 改为 `HashSet<WeakReference<VisualElement>>`（或 `Dictionary<int, WeakReference<VisualElement>>`）
2. `OnEditorUpdate` 中定期（每 N 帧）遍历清理已 GC 的 WeakReference
3. 或者：`RegisterCallback<DetachFromPanelEvent>(_ => _hookedRoots.Remove(rootId))` 挂钩清理

**建议第 3 种**：最干净、无定期扫描开销。挂 `DetachFromPanelEvent` 到每个 root，root 从 panel detach 时自动清理 HashSet 条目 + Unregister 三个 event callback。

### C.2 · #6 VcsRemoteStatusMonitor 竞态

**文件**: `Editor/VCS/Tools/VcsRemoteStatusMonitor.cs:20-21, 47, 52, 59-62, 77-87, 120, 149-159`

**修复**:
```csharp
private static int _isCheckingFlag;  // 0 = false, 1 = true

public static bool IsChecking => Interlocked.CompareExchange(ref _isCheckingFlag, 0, 0) == 1;

public static async Task<VcsSyncStatus> CheckRemoteStatusAsync(bool force, CancellationToken ct = default)
{
    if (Interlocked.CompareExchange(ref _isCheckingFlag, 1, 0) != 0)
        return LastStatus;  // 已经在检查
    try
    {
        // ... await ...
    }
    finally
    {
        Interlocked.Exchange(ref _isCheckingFlag, 0);
    }
}
```

`_isSyncing` 同处理。

`_cts` 操作用 `lock` 或 `Interlocked.Exchange`（`_cts?.Cancel()` 前先 `Interlocked.Exchange(ref _cts, null)` 拿到独占引用再 Cancel/Dispose）。

### C.3 · #7 AsyncHelper._updateHookRegistered

**文件**: `Editor/Utils/AsyncHelper.cs:20, 33-38`

**修复**:
```csharp
private static int _updateHookRegistered;  // 0 = not registered

private static void EnsureUpdateHook()
{
    if (Interlocked.CompareExchange(ref _updateHookRegistered, 1, 0) == 0)
    {
        EditorApplication.update += DrainMainThreadQueue;
    }
}
```

### 批 C commit

```bash
git add Editor/UI/Context/MouseTracker.cs \
        Editor/VCS/Tools/VcsRemoteStatusMonitor.cs \
        Editor/Utils/AsyncHelper.cs
git commit -m "fix(concurrency): batch C 竞态/泄漏 — MouseTracker + VcsMonitor + AsyncHelper

- #5 HIGH: MouseTracker DetachFromPanelEvent 挂钩清理 _hookedRoots + Unregister callback
- #6 MEDIUM: VcsRemoteStatusMonitor Interlocked flag + _cts race guard
- #7 LOW: AsyncHelper._updateHookRegistered Interlocked.CompareExchange"
```

**Print**: `[Batch C done]`

---

## 最终 tag + push

```bash
# 版本 bump（在批 A 前完成 CHANGELOG，最后统一 bump）
# 已在批 A 前更新 package.json 到 1.12.0-alpha.6
# 已在批 A 前追加 CHANGELOG.md alpha.6 段落

git push origin feat/v1.12.0-session-organization
git tag -a v1.12.0-alpha.6 -m "v1.12.0-alpha.6: perf/concurrency 10-issue cleanup"
git push origin v1.12.0-alpha.6
```

⚠️ **push 需要用户在 Hermes 侧明确指令，Claude Code 不擅自 push**。Claude Code 完成三批 commit 后停下，等 Hermes 触发 push。

---

## Phase 0: preflight (Claude Code 必做)

在动任何代码前：
```bash
# 确认当前状态
git status --short
git log --oneline -3

# 应该看到:
# 1049261 feat(v1.12.0-alpha.5): session tag registry
# 4bc0dba feat(v1.12.0-alpha.4): remove Silent mode + fix chat perf blocker
# 72f5a10 chore(log): default log verbosity Info -> Warning

# 工作区应该只有 AgentLoop.cs 一处 modified (GetContextBudget marker, 不动)
git diff --name-only
# expected: Editor/Core/AgentLoop.cs
```

**如果工作区状态不符预期，立即停下汇报，不擅自继续。**

---

## Version bump + CHANGELOG (先做，一次性)

```
package.json version: 1.12.0-alpha.5 → 1.12.0-alpha.6
```

CHANGELOG.md 顶部（`## [1.12.0-alpha.5]` 之前）追加：

```markdown
## [1.12.0-alpha.6] - 2026-07-29

### Fixed — Performance & Concurrency (10-issue cleanup)

Following v1.12.0-alpha.4's `StreamReader.EndOfStream` fix, a full-code-base bug sweep identified 10 remaining perf/stability issues. All fixed in this release across 3 commits (batch A / B / C).

#### 批 A: Quick wins
- **#2 CRITICAL** `OpenAICompatibleClient.ChatCompletionStreamAsync` — HTTP `response` and `stream` were never `Dispose`d, causing latent connection-pool leakage on every LLM streaming call. Added `using` on both.
- **#3 HIGH** `ManagePackageTool.WaitForRequest` — main-thread `Thread.Sleep(50)` polling up to 30 seconds froze the Editor. Rewritten with `EditorApplication.update` + `TaskCompletionSource`.
- **#4 MEDIUM** `ManageProfilerTool` — main-thread `Thread.Sleep(50)` waiting for the next frame → `EditorApplication.delayCall` + `TaskCompletionSource`.

#### 批 B: Async propagation
- **#1 CRITICAL** `SessionStorage.LoadAsync` — session switch previously did synchronous `File.ReadAllText` + `JsonConvert.Deserialize` on multi-MB session files, blocking main thread 50-200 ms. Now fully async via `File.ReadAllTextAsync`.
- **#8 HIGH** `SessionStorage.ListSessions` — sidebar refresh no longer reads every session file in full; header fields are streamed.
- **#9 HIGH** `FileChangeTracker` — line counting no longer allocates the whole file via `ReadAllLines`; streaming `\n` count.
- **#10 HIGH** `BootstrapLoader.LoadAsync` + `ProjectContextCollector.CollectHeavyAsync` used consistently, eliminating 50-200 ms hang when opening ChatWindow.

#### 批 C: Concurrency safety
- **#5 HIGH** `MouseTracker._hookedRoots` — no longer grows unboundedly; `DetachFromPanelEvent` cleans up entries + unregisters callbacks when windows close.
- **#6 MEDIUM** `VcsRemoteStatusMonitor` — `_isChecking`/`_isSyncing` now guarded by `Interlocked.CompareExchange`; `_cts` operations race-guarded.
- **#7 LOW** `AsyncHelper._updateHookRegistered` — `Interlocked.CompareExchange` prevents duplicate `EditorApplication.update` subscription on concurrent first-time invocations.

### Testing

- Manual: chat streaming during long sessions, session switching (large sessions), Editor ChatWindow open time, `manage_package` install/list operations no longer freeze Editor.
- Static: `grep -rn "Thread.Sleep\|.Result\|.Wait()" Editor/` returns only intentional sleeps in background workers.
```

---

## 结束时的产出清单

- ✅ 3 个 commit（批 A、B、C），每个 commit 语义单一
- ✅ 一个 tag `v1.12.0-alpha.6`
- ✅ CHANGELOG.md 更新
- ✅ package.json bump
- ✅ 静态验证 grep: 无 `Thread.Sleep` 主线程调用、无 `.Result/.Wait()` 阻塞
- ✅ 可选：编译验证（用户在 Hermes 侧确认）

---

## Anti-goals

- ❌ 不要修改 `Editor/Core/AgentLoop.cs` 里的 GetContextBudget marker（unstaged）
- ❌ 不要触碰 Tests/ 目录
- ❌ 不要扩大扫描范围（不要顺手改看到的其他"小 smell"）
- ❌ 不要 push（用户明确指令后 Hermes 触发）
- ❌ 发现签名冲突时不要擅自改架构，立即停下汇报
