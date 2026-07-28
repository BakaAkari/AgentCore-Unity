# v1.12.0-alpha.4 Silent 模式彻底删除 + SSE 阻塞修复

**Status**: Ready for execution
**Owner**: Claude Code (autonomous)
**Branch**: `feat/v1.12.0-session-organization`
**Base commit**: `72f5a10` (log verbosity default → Warning)
**Target version**: `1.12.0-alpha.4`

---

## 0. 背景 / Why

v1.8.8 引入 `SessionMode.Silent` 的**原始理由**（`Editor/Core/SessionMode.cs:9-12` 注释）：

> "用于解决观测者效应: 用户跑 `manage_profiler` 等诊断工具时, Chat 面板的 UI 更新会通过 UnitySynchronizationContext 走 EditorLoop tick, 间接触发 Application.UpdateScene, 干扰被测量的性能数据。"

**这个诊断是错的**。2026-07-28 profiler 实测（Edit Mode，agent 回复中）：

| 观测项 | 数据 |
|---|---|
| 卡顿一帧总 CPU | **334.73ms** |
| `UnitySynchronizationContext.ExecuteTasks` (self) | **200.89ms (60%)** |
| **`StreamReader.get_EndOfStream`** | **199.73ms / 28 calls (每次 7.1ms)** |
| `ParseChunkJson` (JObject.Parse) | 19.62ms |
| `AgentCore.UI.HandleAgentEvent` | **0ms** |
| `AgentCore.UI.UpdateContextPanel` | **0ms** |
| `AgentCore.Emit.SilentBuffered` | **0.00ms / 1 call** |
| `AgentCore.Emit.Marshalled` | **未触发** |

**结论**：
1. Chat UI 更新根本不是卡顿元凶。所有 `AgentCore.UI.*` marker 全 0。
2. 真元凶 = `StreamingResponseParser.cs:53` 的 `while (!reader.EndOfStream)` — `StreamReader.EndOfStream` 属性 getter 在 `NetworkStream` 上会同步阻塞主线程 peek 一字符，SSE 慢吐字下每次调用平均 7ms，一帧内 28 次 → 199ms。
3. Silent 模式**从未真正缓解**它想解决的问题（观察者效应源头就找错了）。它引入的 buffer / gate / IsUserInteractionEvent 白名单 / SilentModeButton UI 是一整套**围绕错误认知构建的死代码**。
4. `StreamReader.EndOfStream` 已在 alpha.4 修复（一行改动，改为 `while (!ct.IsCancellationRequested)` + 依赖 `ReadLineAsync() → null` 判定 stream end），实测卡顿源头已消。

**行动**：彻底删除 SessionMode / SessionModeState / SilentModeButton / Silent buffer / gate / 白名单 / 相关 L10n / 相关 EditorPrefs。**保留 `manage_profiler` 工具族**（它是 AgentCore 分析性能的核心能力，与 Silent 无关）。

---

## 1. 变更范围（Impact Surface Analysis）

### 1.1 必删文件（2 个）

| 文件 | 行数 | 说明 |
|---|---|---|
| `Editor/Core/SessionMode.cs` | 100 | `SessionMode` enum + `SessionModeState` 静态类 + `EditorPrefsKey` |
| `Editor/UI/Components/SilentModeButton.cs` | 127 | UI Toolkit 按钮组件 + Changed 订阅 + tooltip 逻辑 |

**验证清理**：删除后 `grep -rn "SessionMode\|SessionModeState\|IsSilent\|_silentBuffer\|IsUserInteractionEvent\|SilentModeButton\|FlushSilentBuffer" Editor/ --include="*.cs"` 必须返回空。

### 1.2 必改文件（8 个）

#### A. `Editor/Core/AgentLoop.Events.cs` (大改)

**当前状态**（161 行）：
- Line 31-34, 45-46, 52-64: `EmitEvent` 里 `SessionModeState.IsSilent` gate + `_silentBuffer.Enqueue` 分支
- Line 67-92: `IsUserInteractionEvent` 白名单静态方法
- Line 94-104: `_silentBuffer` (ConcurrentQueue) + `_sessionModeSubscribed` 字段
- Line 106-115: `EnsureSessionModeSubscription`
- Line 117-128: `OnSessionModeChanged` 回调
- Line 130-159: `FlushSilentBuffer` 方法

**目标状态**：
```csharp
private void EmitEvent(AgentEvent evt)
{
    if (evt == null) return;
    using (AgentCoreProfilerMarkers.EmitMarshalled.Auto())
    {
        AsyncHelper.RunOnMainThread(() => OnAgentEvent?.Invoke(evt));
    }
}
```
删除所有 SessionMode 相关成员、方法、订阅逻辑。`EmitMarshalled` marker **保留**（它是通用 marker，重命名为 `EmitEvent` 或直接保留原名不变——见 §3 决策）。

#### B. `Editor/Core/AgentLoop.Runner.cs`

Line 322-326: 删除 `FlushSilentBuffer()` 调用 + 注释块（"turn 结束时 flush Silent buffer"）。

#### C. `Editor/UI/ChatWindow.cs`

- Line 131-132: 删除字段 `_silentModeButton`
- Line 282-291: 删除动态插入 SilentModeButton 的整块（10 行）

**关键约束**：Insert(0, ...) 到 `input-area` 是在其他 button 之前，删除后不影响 `send-button` / `cancel-button` 顺序（它们本来就在 UXML 里定义）。**不改 UXML**。

#### D. `Editor/UI/ChatWindow.Input.cs`

Line 64-69: 删除 `if (!SessionModeState.IsSilent)` 判断，`ShowPendingIndicator` 无条件调用（原本 Batched 模式的行为）。

#### E. `Editor/Utils/AgentCoreProfilerMarkers.cs`

- Line 15-17: 删除 `EmitSilentBuffered` marker（Silent 分支删了，marker 无处触发）
- Line 20 注释: 更新为纯描述 `EmitMarshalled`（无 Silent 概念）
- **保留** `EmitMarshalled`、`DrainQueue`、`UIHandleAgentEvent`、`UIUpdateContextPanel`、`GetContextBudget` 五个 marker（诊断工具集，未来仍有用）

#### F. `Editor/L10n/Resources/en-US.json`

删除三个 key：
```
"silentMode.tooltip"
"silentMode.tooltip.silent"
"silentMode.tooltip.batched"
```

#### G. `Editor/L10n/Resources/zh-CN.json`

同上，删除对应中文三个 key。

#### H. `package.json`

`"version": "1.12.0-alpha.3"` → `"1.12.0-alpha.4"`。

### 1.3 需处理的持久化数据

- `EditorPrefs` key `AgentCore.ChatWindow.SessionMode` 会残留在用户机器上。
  - **决策**：**不做兼容读取**。EditorPrefs 是 per-user local 数据，用户升级后残留 key 不影响功能（没人读它）。
  - **可选**：在 `EditorPrefsMigration` 或 startup 里静默 `DeleteKey`。**建议不做**——加一次性 migration 代码为一个已死概念清扫，得不偿失。用户重装 Unity/清缓存自然消失。

### 1.4 CHANGELOG / 文档同步

#### `CHANGELOG.md`

新增 `## [1.12.0-alpha.4]` 段落：

```markdown
## [1.12.0-alpha.4] - 2026-07-28

### Fixed
- **[Perf-Critical]** Chat 卡顿根因修复: `StreamingResponseParser.cs` 的
  SSE 消费循环从 `while (!reader.EndOfStream)` 改为
  `while (!ct.IsCancellationRequested)` + 依赖 `ReadLineAsync() == null` 判定
  stream 结束. `StreamReader.EndOfStream` 属性 getter 在 NetworkStream 上会
  同步阻塞主线程 peek 一字符, SSE 慢吐字下实测每次 ~7ms, 一帧 28 次调用累计
  199ms/帧 (334ms 一帧 60%). 修复后 agent 回复期间 Editor 主线程不再被 SSE
  循环拖住. (StreamingResponseParser.cs:53)

### Removed
- **Silent 模式彻底移除**. 该模式 v1.8.8 引入时基于错误的观察者效应认知
  (以为 Chat UI 更新干扰 profiler 数据), Profiler 实测证明 Chat UI 更新
  开销为 0, 真正卡顿源是 SSE 循环 (见上). Silent 模式从未真正缓解它想
  解决的问题, 保留是负债. 删除内容:
  - `SessionMode` enum + `SessionModeState` 静态类 (`Editor/Core/SessionMode.cs`)
  - Chat 输入栏左侧 S 按钮 (`Editor/UI/Components/SilentModeButton.cs`)
  - `AgentLoop.EmitEvent` 里的 Silent gate / `_silentBuffer` /
    `IsUserInteractionEvent` 白名单 / `FlushSilentBuffer` 方法
  - `silentMode.tooltip.*` L10n 键 (en-US / zh-CN)
  - `AgentCore.Emit.SilentBuffered` Profiler marker
  - `EditorPrefs` key `AgentCore.ChatWindow.SessionMode` 不再读写 (残留 key
    对功能无影响, 不做主动 migration)
```

#### `README.md`

- 检查是否有 "Silent mode" / "静默模式" 字样，全部移除
- Status/版本徽章更新到 alpha.4

#### `plans/README.md`

版本表加 alpha.4 行；status 描述 "Silent removal + SSE fix"

#### `plans/v1.12.0/session-organization-plan.md`

如内有 Silent 引用，删除相关段落；否则不改。

#### 归档 4 份已失效历史文档

移动到 `plans/_archive/perf-observer-effect-invalid/`：
- `plans/perf-issue-agent-streaming-blocks-editor.md`
- `plans/perf-issue-editor-hang-during-agent-run-summary.md`
- `plans/v1.8.8-session-mode-handoff.md`

在每个文件头部追加一个 `> **STATUS: SUPERSEDED**` 段落，指向本 plan：

```markdown
> **STATUS: SUPERSEDED (2026-07-28, v1.12.0-alpha.4)**
> 本文档基于"Chat UI 更新干扰 profiler 数据"这一错误诊断构建。
> 2026-07-28 profiler 实测证明真正卡顿源是 `StreamReader.EndOfStream` 同步
> 阻塞 (`StreamingResponseParser.cs:53`), 与 UI 更新无关。
> Silent 模式已在 v1.12.0-alpha.4 彻底删除。
> 参见 [`plans/v1.12.0/silent-mode-removal-plan.md`](../v1.12.0/silent-mode-removal-plan.md)。
```

`plans/_archive.meta` 存在，`plans/_archive/perf-observer-effect-invalid/` 需要新建（Unity 会自动生成 .meta，不需要手动创建 .meta 文件）。

---

## 2. 变更范围之外（Explicitly NOT touching）

- ✅ `manage_profiler` 工具族 (`Editor/Tools/ManageProfilerTool.cs` 等) — AgentCore 分析性能的核心能力，与 Silent 无关
- ✅ 其他 5 个 Profiler markers (`EmitMarshalled` / `DrainQueue` / `UIHandleAgentEvent` / `UIUpdateContextPanel` / `GetContextBudget`) — 保留作为诊断工具
- ✅ Tag Registry (Phase 3.5) 相关文件 — 未 commit 的工作区改动**保持原样**，本次 commit 不混入
- ✅ `StreamingResponseParser.cs:53` 的 SSE 修复 — **已在工作区**，本次 commit **一起提交**
- ✅ `AgentCoreProfilerMarkers.cs` 里 `ToolExec` 已在前面被移除（跨帧 marker bug），本次不再动

---

## 3. 执行前的最后决策点

### 3.1 EmitMarshalled marker 是否重命名？

现在 marker 名 `AgentCore.Emit.Marshalled` 隐含 "vs SilentBuffered" 的对偶。Silent 删了之后语义变成 "所有 EmitEvent 调用"。

**决策**：**保留原名 `EmitMarshalled` 不变**。理由：(a) 重命名要改 marker 引用+更新 Profiler 用户的过滤习惯；(b) "Marshalled" 语义仍然准确（把事件 marshal 到主线程）；(c) 减少改动面。

### 3.2 ChatWindow.OnDisable / Destroy 是否要清 SessionMode 订阅？

原代码 `AgentLoop.Events.cs` 里 `SessionModeState.Changed += OnSessionModeChanged` **只加不减**。虽然是内存泄漏隐患，但 SessionMode 被删了之后自然消失，不需要处理。

### 3.3 Tag Registry 工作区改动怎么办？

Tag Registry (SessionTagRegistry.cs + ChatWindow.Sessions.cs 局部改 + ChatWindow.uss + SessionTagInputDialog.cs + L10n) 目前**未 commit**。

**决策**：**本次 alpha.4 commit 只包含 Silent 删除 + SSE fix**。Tag Registry 单独一个 commit。理由：语义清晰、便于回滚定位、changelog 分组清楚。

执行顺序：
1. Silent 删除 + SSE fix → commit → alpha.4 tag/push（不带 Tag Registry）
2. 用户测试 alpha.4 → 无问题
3. Tag Registry commit → alpha.5

**Claude Code 执行本 plan 时**只做步骤 1。步骤 2/3 由用户+Hermes 手工推进。

---

## 4. 逐步执行清单（Claude Code 按序执行）

### Phase 0: 前置检查

- [ ] `git status` 确认工作区状态。**期望**：
  - `StreamingResponseParser.cs` (已改，SSE fix, uncommitted)
  - Tag Registry 相关文件 (uncommitted, 不 touch)
- [ ] `git log -1 --oneline` 确认 HEAD = `72f5a10 chore(log): default log verbosity Info -> Warning`
- [ ] `git branch --show-current` 确认 = `feat/v1.12.0-session-organization`
- [ ] 全局搜索基线 (记录到执行日志):
  ```
  grep -rn "SessionMode\|SessionModeState\|IsSilent\|_silentBuffer\|IsUserInteractionEvent\|SilentModeButton\|FlushSilentBuffer\|EmitSilentBuffered" Editor/ --include="*.cs"
  ```
  期望：全部落在本 plan §1.1/§1.2 列举的文件里，没有遗漏。

### Phase 1: 删除文件

- [ ] `git rm Editor/Core/SessionMode.cs`
- [ ] `git rm Editor/Core/SessionMode.cs.meta`
- [ ] `git rm Editor/UI/Components/SilentModeButton.cs`
- [ ] `git rm Editor/UI/Components/SilentModeButton.cs.meta`

### Phase 2: 修改 AgentLoop.Events.cs

将全文替换为以下（保留原文件 using 头 + namespace + `partial class AgentLoop`）：

```csharp
using System;
using AgentCore.Editor.Utils;

namespace AgentCore.Editor.Core
{
    public partial class AgentLoop
    {
        /// <summary>Agent 事件回调（供 UI 层订阅）。</summary>
        public event Action<AgentEvent> OnAgentEvent;

        /// <summary>agent 状态变化时同步触发 StateChanged 事件。</summary>
        private void SetState(AgentState newState)
        {
            if (_state == newState) return;
            _state = newState;
            EmitEvent(AgentEvent.StateChanged(newState));
        }

        /// <summary>
        /// 派发 Agent 事件到主线程订阅方。
        /// 使用 AsyncHelper.RunOnMainThread 确保事件在 Unity 主线程上触发,
        /// 因为 LLM 流式回调可能在后台线程执行.
        /// </summary>
        private void EmitEvent(AgentEvent evt)
        {
            if (evt == null) return;
            using (AgentCoreProfilerMarkers.EmitMarshalled.Auto())
            {
                AsyncHelper.RunOnMainThread(() => OnAgentEvent?.Invoke(evt));
            }
        }
    }
}
```

**关键**：
- 保留 `OnAgentEvent` 事件（是 API 契约的一部分）
- 保留 `SetState` (原文件有的，其他 partial file 可能没有——检查 AgentLoop.cs / Runner.cs)
- 删除 `System.Collections.Concurrent`、`System.Collections.Generic` using（如果只被 Silent 用）

**执行后验证**：
```
grep -n "using " Editor/Core/AgentLoop.Events.cs
```
只应看到 `using System;` 和 `using AgentCore.Editor.Utils;`。

### Phase 3: 修改 AgentLoop.Runner.cs

删除 line 322-326 的整块：
```csharp
// v1.8.8: turn 结束时 flush Silent buffer.
// Silent 模式下, 上面所有 EmitEvent 已被写入 _silentBuffer 而没 marshal 到主线程.
// FlushSilentBuffer 会把 buffer 内容 (含刚 emit 的 LoopCompleted) 逐个 RunOnMainThread,
// 一波集中送到 UI. Batched 模式下 buffer 是空的, FlushSilentBuffer 短路返回.
FlushSilentBuffer();
```

保留 `EmitEvent(AgentEvent.LoopCompleted(currentRound));` 和后面的 SessionManager.AutoSave 块。

### Phase 4: 修改 ChatWindow.cs

- 删除字段声明 (line 131-132):
  ```csharp
  /// <summary>v1.8.8: Silent 模式切换按钮 (输入栏最左侧, 动态添加)</summary>
  private Components.SilentModeButton _silentModeButton;
  ```

- 删除动态插入块 (line 282-291):
  ```csharp
  // v1.8.8: 输入栏最左侧插入 Silent 按钮 ...
  var inputAreaForSilentBtn = rootVisualElement.Q<VisualElement>("input-area");
  if (inputAreaForSilentBtn != null && _silentModeButton == null)
  {
      _silentModeButton = new Components.SilentModeButton();
      inputAreaForSilentBtn.Insert(0, _silentModeButton);
  }
  ```

**必须保留** line 281 之前的 `_scrollToBottomButton` 查询和 line 292 之后的 `_contextSidebar` 查询——只删中间 Silent 相关的 10 行块。

### Phase 5: 修改 ChatWindow.Input.cs

Line 64-69 原代码：
```csharp
// 立刻显示 pending 占位气泡（解决"点击发送 → 5-30 秒 UI 无反应"的感知问题）
// v1.8.8: Silent 模式跳过 PendingIndicator, 避免"思考中"占位气泡出现在消息列表里.
// Silent 模式下用户从 send/cancel 按钮 + 状态栏 (StateChanged 白名单) 感知 agent 在跑.
if (!SessionModeState.IsSilent)
{
    ShowPendingIndicator(AgentCore.Editor.L10n.Loc.Tr("chat.pending.thinking", "思考中"));
}
```

替换为：
```csharp
// 立刻显示 pending 占位气泡（解决"点击发送 → 5-30 秒 UI 无反应"的感知问题）
ShowPendingIndicator(AgentCore.Editor.L10n.Loc.Tr("chat.pending.thinking", "思考中"));
```

### Phase 6: 修改 AgentCoreProfilerMarkers.cs

删除 `EmitSilentBuffered` 定义（约 line 15-20，用 grep 确认精确行号）。保留 `EmitMarshalled` 定义。更新文件头部注释（如提到 "Silent vs Marshalled" 对偶要移除）。

### Phase 7: 修改 L10n 文件

`Editor/L10n/Resources/en-US.json` 删除 3 个 key（line 152-154 附近）：
```
"silentMode.tooltip": ...,
"silentMode.tooltip.silent": ...,
"silentMode.tooltip.batched": ...,
```

`Editor/L10n/Resources/zh-CN.json` 同样 3 个 key。

**注意 JSON 逗号**：删除后确保前后 key 的逗号结构合法。用 `python -m json.tool < file` 验证。

### Phase 8: package.json 版本 bump

```
"version": "1.12.0-alpha.3"  →  "1.12.0-alpha.4"
```

### Phase 9: CHANGELOG.md 追加 alpha.4 段落

按本 plan §1.4 提供的模板追加。

### Phase 10: 文档同步

- [ ] `README.md`: 全局搜 "Silent" / "静默" 移除相关描述；更新版本徽章
- [ ] `plans/README.md`: 版本表加 alpha.4 行
- [ ] `plans/v1.12.0/session-organization-plan.md`: 搜 Silent 引用移除
- [ ] 归档 3 份历史文档到 `plans/_archive/perf-observer-effect-invalid/`（含 .meta），每份文件头加 SUPERSEDED 标记

### Phase 11: 编译验证

**关键**：Claude Code 无法直接触发 Unity Editor 编译，只能通过检查语法。执行以下静态验证：

```
grep -rn "SessionMode\|SessionModeState\|IsSilent\|_silentBuffer\|IsUserInteractionEvent\|SilentModeButton\|FlushSilentBuffer\|EmitSilentBuffered\|silentMode\.tooltip" Editor/ --include="*.cs" --include="*.json"
```

**期望**：**返回空**。

如果有残留，逐个处理。

### Phase 12: Git commit + tag + push

**⚠️ 注意**：Tag Registry 相关工作区文件**不要 stage**。只 stage 本 plan 涉及的文件 + `StreamingResponseParser.cs`（SSE fix）。

```bash
git add Editor/LLM/StreamingResponseParser.cs
git add Editor/Core/AgentLoop.Events.cs
git add Editor/Core/AgentLoop.Runner.cs
git add Editor/UI/ChatWindow.cs
git add Editor/UI/ChatWindow.Input.cs
git add Editor/Utils/AgentCoreProfilerMarkers.cs
git add Editor/L10n/Resources/en-US.json
git add Editor/L10n/Resources/zh-CN.json
git add -u Editor/Core/SessionMode.cs Editor/UI/Components/SilentModeButton.cs  # 已删
git add package.json CHANGELOG.md README.md
git add plans/README.md plans/v1.12.0/silent-mode-removal-plan.md
git add plans/_archive/perf-observer-effect-invalid/
git add plans/perf-issue-agent-streaming-blocks-editor.md.meta  # 若移动产生 .meta 更新
git add plans/perf-issue-editor-hang-during-agent-run-summary.md.meta
git add plans/v1.8.8-session-mode-handoff.md.meta
```

**验证 staged diff 不包含 Tag Registry 文件**：
```
git diff --cached --name-only
```
必须不出现 `SessionTagRegistry.cs` / `SessionTagInputDialog.cs` / `ChatWindow.Sessions.cs` / `ChatWindow.uss` 相关行。

**commit message**:
```
perf!: remove Silent mode, fix SSE main-thread block (v1.12.0-alpha.4)

BREAKING: SessionMode.Silent and the "S" toggle button are removed.

Root cause of chat lag was misidentified in v1.8.8: profiler assumption
"Chat UI updates interfere with Application.UpdateScene" is false.
Actual bottleneck: StreamingResponseParser.cs used
`while (!reader.EndOfStream)` — StreamReader.EndOfStream synchronously
blocks the main thread peeking one byte from the NetworkStream. Measured
28 calls × ~7ms = 199ms of a 334ms frame during agent replies.

Fixed by switching to `while (!ct.IsCancellationRequested)` and relying
on `ReadLineAsync() == null` for stream end. Silent mode was never
addressing the real issue, so the entire abstraction (SessionMode enum,
SessionModeState, SilentModeButton, _silentBuffer, IsUserInteractionEvent
whitelist, FlushSilentBuffer) is removed.

- Editor/LLM/StreamingResponseParser.cs: SSE loop uses ReadLineAsync's
  own null-return signal instead of EndOfStream peek
- Editor/Core/SessionMode.cs: deleted
- Editor/UI/Components/SilentModeButton.cs: deleted
- Editor/Core/AgentLoop.Events.cs: EmitEvent simplified — no gate/buffer
- Editor/Core/AgentLoop.Runner.cs: FlushSilentBuffer call removed
- Editor/UI/ChatWindow.cs: dynamic SilentModeButton insertion removed
- Editor/UI/ChatWindow.Input.cs: unconditional ShowPendingIndicator
- Editor/Utils/AgentCoreProfilerMarkers.cs: EmitSilentBuffered marker
  removed (EmitMarshalled and the other 4 markers kept)
- L10n: silentMode.tooltip.* keys removed (en-US / zh-CN)
- plans/_archive/perf-observer-effect-invalid/: 3 historical docs
  superseded by plans/v1.12.0/silent-mode-removal-plan.md
```

**Tag + push**：
```bash
git tag v1.12.0-alpha.4
git push origin feat/v1.12.0-session-organization
git push origin v1.12.0-alpha.4
```

### Phase 13: 交接给用户

Claude Code 完成 Phase 0-12 后，向用户报告：
1. 所有静态检查通过（Phase 11 grep 全空）
2. Commit sha + tag 名 + branch push 状态
3. 提醒用户在 Unity Editor 侧：
   - Reload 后确认 Console 0 error
   - S 按钮消失
   - 发一条 agent 消息验证：(a) 不卡 (b) UI 正常更新 (c) PendingIndicator "思考中" 出现

---

## 5. 回滚策略

如果 Unity 侧编译出错或运行时崩溃：

```bash
git reset --hard 72f5a10
git tag -d v1.12.0-alpha.4
git push --force-with-lease origin feat/v1.12.0-session-organization
git push origin :refs/tags/v1.12.0-alpha.4  # 删除 remote tag
```

**保留 SSE fix 的 rollback 变种**（如果只删除失败但 SSE fix 想留）：
```bash
git reset --hard 72f5a10
git checkout HEAD@{1} -- Editor/LLM/StreamingResponseParser.cs
git commit -m "perf: SSE loop uses ReadLineAsync null-return (partial from alpha.4)"
```

---

## 6. 风险 / 未知

- **StreamingResponseParser 修复副作用**：ReadLineAsync 返回 null 语义在 .NET 各版本一致，但若服务端返回不完整流（无 chunked trailer），行为可能与 EndOfStream 版本略有差异。**风险等级：低**（原代码里 `if (line == null) break;` 分支已存在，说明作者预期了 null 的情况）。
- **UXML 加载副作用**：ChatWindow.cs 删除 SilentModeButton 动态插入不影响 UXML 定义的 input-area 子元素顺序。**风险等级：极低**。
- **L10n key 缺失**：若代码里还有其他地方 `Loc.Tr("silentMode.tooltip...")` 调用，删除 key 会导致回退到 fallback。已 grep 确认只有 SilentModeButton.cs 用（也被删了）。**风险等级：极低**。
- **EditorPrefs 残留**：老用户机器上残留 `AgentCore.ChatWindow.SessionMode` key。**无影响**（无代码读它）。

---

## 7. 完成标准 (Definition of Done)

- [ ] Phase 11 grep 全空
- [ ] `git diff --cached` 只包含本 plan §1.1/§1.2 + StreamingResponseParser.cs 涉及的文件
- [ ] Tag Registry 相关文件保持工作区状态未 commit
- [ ] commit sha 生成、tag `v1.12.0-alpha.4` 打上、push 成功
- [ ] CHANGELOG.md alpha.4 段落包含所有本 plan §1.4 要点
- [ ] plans/README.md 版本表最新一行 = alpha.4
- [ ] 3 份历史文档已归档到 `plans/_archive/perf-observer-effect-invalid/`

**用户侧待验证（不属于 Claude Code 完成范畴）**：
- [ ] Unity Editor Reload 0 error
- [ ] S 按钮 UI 消失
- [ ] Agent 回复期间 Editor 不再 200ms+ 卡顿（profiler 验证）
- [ ] PendingIndicator 正常工作
