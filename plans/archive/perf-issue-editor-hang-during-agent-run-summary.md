# AgentCore Chat 流式期 Editor 主线程卡顿 — 定位与治理进度总结

> **文档目的**: 供第三方分析型 agent / 外部 reviewer 交叉校验。列出所有已实测数据、已尝试方案、当前工作假设、待验证/待决策项。
> **产出日期**: 2026-07-23
> **相关代码路径**: `Packages/com.agentcore.unity/`
> **原始 profile 文件**: 会话根目录下 `AgentCore_帮我用_manage_profiler_抓一次流式期最卡帧的..._*.json`
> **发起触发**: 用户报告 "AgentCore Chat 流式输出期 Editor 变得极度卡顿, 帧率跌到个位数, 场景视图/Inspector 全部拖影, 鼠标响应延迟". 稳态 (agent 空闲期) 帧率正常约 172 FPS.

---

## 1. 已确认事实

### 1.1 现象

Chat **正在流式输出** 期间, Unity Editor 出现严重卡顿; 停止输出后立即恢复正常. 场景/Inspector/Console 均受影响, 说明卡顿在主线程而非某单一窗口渲染层.

### 1.2 环境

- Unity Editor `2022.3.50f1` (`ProjectSettings/ProjectVersion.txt`)
- macOS (原始开发在 Windows, 已迁移到 macOS)
- 测试项目: Megacity Metro (大项目, URP, ECS/Entities, 场景较重)
- 插件目录: `Packages/com.agentcore.unity/`, 版本迭代自 v1.8.1 → v1.8.7 (v1.8.7 未推 origin)

### 1.3 关键 Profile 数据 (多轮采样)

用户在 Chat 里通过 `manage_profiler action=read_frame frame_index=<worst> depth=4 max_markers=8 min_ms=1.0` 采集 (v1.8.4 新增的递归 depth 参数). 单位: ms.

| 版本 | 帧时间 | GC/帧 | ExecuteTasks self | UpdateSceneIfNeeded | 备注 |
|---|---|---|---|---|---|
| v1.8.0 原始 (旧记录) | 228 | 132 KB | 未展开 | 未采集 | 早期 note, 采样条件不明 |
| v1.8.4 前 (真数据) | 601 | **7.9 MB** | 256 (self) | 未采样 | 首次深挖 |
| v1.8.5 (content flush 到 Done) | 496 | 300 KB | 212 | 269 | GC 降 25× 但帧时间只降 17% |
| v1.8.6 (reasoning flush 到 Done, 本地未推) | 829 | 0.6 MB | 495 | 318 | **反而变糟** — 集中 flush 单次大? |
| **v1.8.7 (全关 4 个 UI 定时动画, 本地未推)** | **552** | **410 KB** | **314** | **223** | GC 恢复趋势, 但 ExecuteTasks 顽固 |

**关键观察**:

- **GC**: 7.9 MB → 410 KB, 降幅 19× — 说明我的 content/reasoning batching + 关动画有效, 每 chunk 触发的 UI DOM 重建/事件对象分配大部分已消除
- **主线程**: `EditorLoop → Application.Tick → UnitySynchronizationContext.ExecuteTasks` 是稳定顽固瓶颈, 各版本 self 200-495 ms 波动. **ExecuteTasks self_ms 极高意味着大量 pending continuation / callback 在单帧内被 flush**
- **UpdateSceneIfNeeded 223-318 ms**: 大项目场景本身重, 但 agent 空闲期 (172 FPS = 5.8 ms/帧) 说明它不是"总是这么重", 而是 stream 期被其他因素**共同**推高
- `EditorLoop.self_ms` 稳定 ~0.2 ms = 99.96%+ 时间都在子调用, EditorLoop 本身无罪

### 1.4 已探明的信号源 (schedule.Execute.Every 定时任务, v1.8.7 已全部关闭)

流式期 UIToolkit `schedule.Execute(...).Every(N)` 定时任务清单:

| 组件 | 文件 | 间隔 | v1.8.7 状态 |
|---|---|---|---|
| PendingIndicator._dotAnim | `Editor/UI/Components/PendingIndicator.cs:69` | 400 ms | **已注释** |
| ThinkingDrawer._timer | `Editor/UI/Components/ThinkingDrawer.cs:313` | 250 ms | **已注释** |
| StreamingTextElement._cursorBlink | `Editor/UI/Components/StreamingTextElement.cs:1247` | 530 ms | **已注释** |
| AgentStatusLine PulseTick | `Editor/UI/Components/AgentStatusLine.cs:78` | PulseIntervalMs | **已注释** |
| StreamingTextElement.FlushPending | 同上文件, `1059` | 16 ms | **仍在** (每 chunk 触发一次, 累积 flush) |

**其他相关 UI 定时点** (审计: 都是"一次性 StartingIn" 而非 "Every", 不属于流式期高频源, 但仍存在):

| 位置 | 行为 |
|---|---|
| MessageBubble 层高同步 | `schedule.Execute(SyncBubbleContentHeight).StartingIn(16 / 64)` — 消息布局收敛后触发 |
| MessageBubble copy 按钮 reset | `StartingIn(1200)` — 复制按钮文字 1.2s 后恢复 |
| ChatWindow scroll to bottom | 多处 `schedule.Execute(...)` 短延迟异步 |

### 1.5 已探明的事件通道

- `AgentLoop.Events.EmitEvent(AgentEvent)` → `AsyncHelper.RunOnMainThread(Action)` → `ConcurrentQueue<Action>` → `EditorApplication.update += DrainMainThreadQueue` → **每帧最多 256 个 Action** (v1.6.5 的 batching, 已 batched)
- 从后台 SSE 线程写事件到主线程队列, `AsyncHelper` 侧已经 batched 得对, **不是这里的问题**

### 1.6 已排除的方向

| 假设 | 排除理由 |
|---|---|
| `FlushIntervalMs 16ms → 120ms` 降频率 | v1.8.2 实测无效, 已回滚 |
| markdown 重解析导致 GC | GC 从 7.9 MB → 410 KB 说明大幅缓解, 但主线程仍卡, **不是主因** |
| SSE `await` continuation 每 chunk marshal 到主线程 | 无法排除但 v1.8.5+v1.8.6 关掉 UI 侧 emit 后 ExecuteTasks 仍高, 说明其他 continuation 源存在 |

---

## 2. 当前工作假设 (待第三方校验)

### 2.1 假设 A: 多轮工具触发密集期 UI 更新事件累积

**证据**:
- Profile 是在 "500 字长故事" 场景采集 — 单轮 assistant 回复, 但期间可能夹带多个 tool_call started / completed 事件
- 每个 `ToolCallStarted / Completed / Failed` → `EmitEvent` → `RunOnMainThread` → 一次 UI 重建 (MessageBubble 追加 ToolCallCard 等)
- 短时间内 N 个 tool call 事件 flush 到主线程 → 一次 EditorApplication.update 里 DrainMainThreadQueue 跑掉几十个 Action → 每个 Action 内部触发 VisualElement 重建 → SC continuation 累积

**如果假设 A 成立, 修复方向**:
- 把 `ToolCallStarted / Completed` 事件累积起来, tick 一次批量渲染 (类似 v1.8.5 的 content batching)

### 2.2 假设 B: Editor 主线程 SynchronizationContext 队列有别处高频源

**证据**:
- UnitySynchronizationContext.ExecuteTasks self_ms 稳定 200-500 ms 说明**每帧 Unity 主线程 SC 队列都有大量 pending**
- 但我们已经排除 UI 事件通道 (AsyncHelper) 和 UI 动画 (schedule.Execute.Every)
- **剩余可能源**:
  1. `SSE ReadLineAsync().await` — HTTP 后台读取, `await` 恢复时默认走 SynchronizationContext.Current = 主线程 SC
  2. `SelfChallenge extractor` 或其他 async/await 链
  3. Unity 内部 Editor 系统本身 (Analyzer / Asset import worker 反馈 / Domain reload 检查等)

**验证方法**:
- 需要 `read_frame` 支持 depth=6+ 或类似能看到 continuation 具体 delegate 类型
- 当前 Unity Profiler marker 只到 `UnityEngine.CoreModule.dll!UnityEngine::UnitySynchronizationContext.ExecuteTasks() [Invoke]`, 看不到闭包内容
- 或者用 `ProfilerRecorder` sample 一些细粒度 stat, 或用 Deep Profile 抓栈 (Unity 官方功能, 但开销大)

**如果假设 B 成立, 修复方向**:
- `ParseStreamAsync` 所有 `await` 加 `ConfigureAwait(false)` — 让 continuation 回线程池, 完全断主线程 SC 队列
- **风险**: 需要审查跨线程调用的**共享状态**线程安全性 (assistantTurn.Content/Reasoning `+=` race, AgentCoreLog.Debug 是不是 thread-safe, SelfChallenge extractors 状态字段)

### 2.3 假设 C: UpdateSceneIfNeeded 223-318 ms 是 Megacity Metro 项目自身场景重

**证据**:
- 空闲期 `UpdateScene` 没有出现在 top marker (总帧 5.79 ms / 172 FPS)
- 但流式期 `UpdateScene 223+ ms` 稳定出现
- 假设是 EditorApplication.update 每次 tick 都触发 SceneView.Repaint → SceneView 里的 ECS/Entities baking / URP volume 更新 / Traffic 系统 tick
- **agent 侧任何"通知 UI 有变化"的事件都会间接触发 SceneView 重绘 (Repaint All Views)**

**如果假设 C 成立**:
- 需要在**空项目**里复现测试 (证据要求) — 卡顿是否消失
- 或者需要在 Chat 事件通道加"不广播 SceneView.Repaint"的判断

---

## 3. 已实施改动清单 (按版本)

### v1.8.1 (已推 origin)

无关性能, 只是迁移收敛:
- 修 CS0246 (`ManageGraphicsTool` asmdef 缺 SRP Core reference)
- Roslyn DLLs 加入 git 追踪 (`.gitignore` `!Editor/Plugins/Roslyn/*.dll` 白名单)
- Editor/Tests/ 从 tarball 排除 (消除 `AgentCore.Tests.Editor` Cecil warning)
- verify-meta.cjs / verify-tarball.cjs (Node.js 版护栏取代 PowerShell)
- GitHub Actions tag-triggered release workflow

### v1.8.2 → v1.8.3 (已推, 已回滚)

**尝试**: `StreamingTextElement.FlushIntervalMs` 16 ms → 120 ms 降频.
**结果**: 用户实测**卡顿无变化**. **已在 v1.8.3 回滚**.
**教训**: rebuild 频率不是瓶颈 — 单次 rebuild 成本也够慢.

### v1.8.4 (已推 origin)

给 `manage_profiler action=read_frame` 加 `depth` (默认 1, max 5) 和 `min_ms` (默认 0) 参数, 支持递归展开子 marker.
**用途**: 让用户能拉到 EditorLoop 子节点数据. 这是**工具增强**, 无功能变更.

### v1.8.5 (已推 origin)

`AgentLoop.LLM.HandleContentToken` 累积 content token 到 `_pendingStreamContent` (StringBuilder), 不再每 token `EmitEvent(StreamToken)`. 由 `StreamChunkType.Done` / `.Error` 时通过 `FlushAccumulatedContentIfAny` 一次性 emit.
**HTTP stream / tool call / reasoning 抽取完全不变**.
**结果**: GC 7.9 MB → 300 KB, 帧时间 601 → 496 ms.

### v1.8.6 (本地未推)

同 v1.8.5 模式, 对 reasoning token 也批量化: `_pendingStreamReasoning` (StringBuilder + Source), Done/Error 时一次 flush.
**结果**: GC 300 KB → 0.6 MB (略回升), 帧时间 496 → 829 ms (**反而变糟**).
**推测原因**: 集中在 Done 一次性 emit 大字符串, UI 一次 render 长 markdown 更贵.

### v1.8.7 (本地未推)

全关 4 个 `schedule.Execute(...).Every(N)` UI 定时动画:
- PendingIndicator 3 点动画 (400 ms)
- ThinkingDrawer 标题定时刷新 (250 ms)
- StreamingTextElement 光标闪烁 (530 ms)
- AgentStatusLine 呼吸脉冲

**结果**: GC 0.6 MB → 410 KB, 帧时间 829 → 552 ms, ExecuteTasks 495 → 314 ms, UpdateScene 318 → 223 ms.
**说明动画确实是次要贡献者但不是主因**.

---

## 4. 当前决策点 (待用户拍板)

### 方案 X (用户已倾向, 但风险大)

用户诉求: **"UI 静默不影响 Scene view 但让用户知道 agent 在运行"**.

具体实现:
1. `ChatWindow.titleContent.text` 每 3 秒 tick 一次显示 `AgentCore ● Working [12s]` (共享一个 `Every(3000)` timer)
2. Chat 底部一个静态 label (改造现有 `AgentStatusLine`) 只在**状态转换**时 (Streaming / ToolCall / Done) 改文字
3. 完全移除 `StreamToken` / `ReasoningToken` UI 事件, 只在 Done 时一次性重建 bubble
4. **风险**: 修改 `ChatWindow.cs` 主类 + AgentEvent 系统改造 + AgentStatusLine 重构 = 一次相对重的 UI 重构

### 分步替代方案 Y

**最小可测版本**: 只改 `titleContent.text` 每 3 秒 tick 一次, 其他不动. 测完再决定要不要继续.

### 关于是否推 v1.8.5-7 到 origin

- v1.8.5 **已在 origin**
- v1.8.6 / v1.8.7 **本地磁盘, 未 commit 未 push** — 因为 v1.8.6 无效被 v1.8.7 覆盖, v1.8.7 只有 GC 明显改善但主线程仍卡
- **要么合并成一个 v1.8.6 patch 推**, 要么等 v1.8.8 一并推

---

## 5. 供第三方 agent 校验的可疑点

请重点验证:

### 5.1 卡顿根因判断是否正确?

- 现有证据是否足以判定 "**UI 事件密集 → SynchronizationContext 队列 flush**" 是主因?
- 有没有可能是**Megacity Metro 项目本身**触发的 SceneView.Repaint (ECS baking / URP volume update / Traffic system tick), agent 只是"每帧都通知 UI 需要重绘一次"就导致场景反复 tick?
- 换一个**空 Unity 项目**测同样场景 (发长回复问题, 抓 Frame), 数据长啥样?

### 5.2 v1.8.6 反而变糟是不是巧合?

- 是不是采样噪声? 需要多帧平均?
- 还是 "集中在 Done 一次性 emit 长 reasoning + content" 单次 rebuild 真的更贵?

### 5.3 SSE `await ReadLineAsync()` 是否 marshal 到主线程 SC?

- Unity Editor 的默认 `SynchronizationContext.Current` 是不是 `UnitySynchronizationContext` (主线程)?
- 如果是, 那 SSE reader 每 chunk 完成一次都会 post continuation 到主线程 SC —— 但这**是不是** ExecuteTasks 314 ms 的主要贡献者?
- 有没有办法用 Editor.log / Debug.Log 打时间戳验证具体的调用线程?

### 5.4 方案 X 是否是正确产品方向?

- 用户实际诉求 = "知道 agent 在运行" — 是不是有更简单的方案:
  - 直接用 `Progress.Report(...)` (Unity 官方 async progress API)?
  - 或者只显示 spinner 在 EditorApplication.playmodeStateChanged event 里?
- 关掉逐字流式 + 关 4 个 UI 动画后, "AgentCore Working [12s]" 是不是过头? 还是恰到好处?

### 5.5 我漏掉了哪些 continuation 源?

已排查:
- ✓ 4 个 UI 定时动画 (关掉)
- ✓ AsyncHelper 主线程队列 (已 batched)
- ✓ content/reasoning token UI 事件 (关掉逐字)

未排查/可能:
- ? SSE `ReadLineAsync` 每 chunk continuation
- ? SelfChallenge extractor 的 async chain (是否有 Task.Run / await)
- ? Editor 内部系统 tick (AssetDatabase / Compilation / etc.)
- ? MessageBubble.SyncBubbleContentHeight GeometryChangedEvent 是不是流式期反复触发
- ? ChatWindow.Messages 里其他 schedule.Execute 一次性任务是不是短时间内堆积

---

## 6. 建议第三方 agent 输出

请从以下角度反馈:

1. **假设优先级**: A/B/C 三个假设按证据强度排序, 哪个更可能是主因?
2. **验证方案**: 有什么额外证据可以采集 (Deep Profile, ProfilerRecorder sample, 空项目对比, StackTrace)?
3. **方案 X 是否过重**: 有没有更轻的产品方案能达到"用户感知 agent 在运行"?
4. **是否有 ExecuteTasks 314 ms 的可疑源** 我遗漏了?
5. **v1.8.6 反而变糟的原因** 你怎么看?

---

## 7. 附件 (可读取的 Profile 数据)

- `AgentCore_帮我用_manage_profiler_抓一次流式期最卡帧的..._20260723_173257.json` — v1.8.4 前深挖数据, 601 ms/帧 7.9 MB GC
- `AgentCore_帮我用_manage_profiler_抓一次流式期最卡帧的..._20260723_183721.json` — v1.8.6 装了 829 ms/帧 0.6 MB GC
- `AgentCore_帮我用_manage_profiler_抓一次流式期最卡帧的..._20260723_190028.json` — v1.8.7 装了 552 ms/帧 410 KB GC (最新)

---

## 8. 相关代码入口 (方便 reviewer 直接跳)

- `Editor/Core/AgentLoop.LLM.cs` — LLM 调用链, `HandleContentToken` / `AppendReasoningToken` / `OnStreamChunkReceived` / `FlushAccumulatedContentIfAny`
- `Editor/Core/AgentLoop.Events.cs` — `EmitEvent → AsyncHelper.RunOnMainThread`
- `Editor/LLM/OpenAICompatibleClient.cs` — `ChatCompletionStreamAsync` HTTP stream 入口
- `Editor/LLM/StreamingResponseParser.cs` — SSE parse 循环, `YieldBudgetMs=200`, `await ReadLineAsync` 关键 await 点
- `Editor/Utils/AsyncHelper.cs` — 主线程队列 batching (v1.6.5), MaxPerFrame=256
- `Editor/UI/Components/StreamingTextElement.cs` — 流式渲染主类, `FlushPending`, `RenderTextAsBlocks`, `ShowCursor/HideCursor`
- `Editor/UI/Components/MessageBubble.cs` — 消息气泡, `AppendStreamToken`, `FinalizeContent`, `SyncBubbleContentHeight`
- `Editor/UI/Components/PendingIndicator.cs` / `ThinkingDrawer.cs` / `AgentStatusLine.cs` — 已关掉动画的三个组件
