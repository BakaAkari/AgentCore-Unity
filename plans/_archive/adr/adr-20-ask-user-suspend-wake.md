# ADR-20: ask_user 中途提问 —— 挂起-唤醒完整态

> **状态**: **Accepted (2026-07-20)** — 随 v1.7.14 实现
> **决策人**: 项目 PO / 唯一用户
> **触发**: 需要 Agent 在实现方向出现岔路（多方案权衡 / 需求歧义 / 继续须假设）时主动停下向用户提问，而非凭猜测往可能错误方向无限执行
> **前置阅读**: [`AgentLoop.AskUser.cs`](../../../Editor/Core/AgentLoop.AskUser.cs) / [`AskUserTool.cs`](../../../Editor/Tools/Interaction/AskUserTool.cs) / [`ChatWindow.AskUser.cs`](../../../Editor/UI/ChatWindow.AskUser.cs) / [`AgentLoop.DomainReload.cs`](../../../Editor/Core/AgentLoop.DomainReload.cs) ResumeFromWaitingCompilation
> **相关**: 完全独立于 SelfChallenge（ADR-self-challenge-*，准非维护模块）

---

## 1. Context（背景）

Agent 执行任务时会遇到**影响实现方向的岔路口**：存在多种合理方案且各有权衡、需求本身有歧义、或继续做就必须基于某个假设。低质量的处理是"凭幻觉猜一个方向往下无限执行"，一旦猜错代价高（大量返工）。

需要一个机制让 Agent 主动停下、把决策权交还用户、等用户拍板后再继续。

### 用户定义的交互契约（硬约束）

> "没人回答就卡住不能推进也不能拒绝，保持界面阻断状态，LLM 可以直接截断结束，如果用户通过以后再唤醒 LLM 继续执行，因为可能用户只是没在当前窗口或者没看到而已。"

拆解为四条：
1. **不推进、不拒绝、不超时** —— 面板永久阻断直到用户应答（区别于工具确认窗口的 120s 超时自动拒绝）
2. **LLM 截断结束** —— 不同步空等（不占资源、能存档、能跨 domain reload）
3. **用户事后唤醒** —— 用户可能几分钟/几小时后才看到，应答后重新触发 loop 继续
4. **不碰 SelfChallenge** —— 走全新独立机制

## 2. Decision（决策）

把 Agent 等待从**同步阻塞**（await 挂在 TCS 上）重构为**异步挂起 + 唤醒**三段式，并**复刻现有 `WaitingCompilation` 成熟范式**（不发明新机制）。

### 2.1 WaitingCompilation 同构映射

| WaitingCompilation（现有范本） | ask_user（本 ADR） |
|---|---|
| 工具触发编译 → WaitingForCompilation | ask_user 调用 → WaitingForUserInput |
| loop 挂起等编译事件 | loop **截断退出**等用户应答 |
| CompilationFinished → ResumeFromWaitingCompilation | 用户应答 → ResumeFromUserInput |
| TriggerResumeLLMCall | TriggerResumeLLMCall（**复用同一个**） |

### 2.2 数据流

1. **AskUserTool 是纯函数**：解析 question/options，返回带 `ToolResult.IsAwaitingUserInput=true`（+ AskUserQuestion/AskUserOptions）的结果。不接触 UI、不阻塞、不 await。`[AgentTool]` 特性 + ToolAutoDiscovery 自动发现注册。
2. **ExecuteToolCallsAsync（Tools.cs）检测标志**：命中 → `RecordPendingUserQuery(toolCallId,q,opts)`（记录 + 持久化）+ `OnUserQueryRaised?.Invoke`。占位 tool_result 由 BuildToolMessagesWithCompressionAsync 照常写入（历史合法）。
3. **Runner.cs 截断**：检测 `_pendingUserInputToolCallId != null` → `SetState(WaitingForUserInput)` → `return`（干净退出，不空等）。
4. **UI 应答唤醒**：ChatWindow.AskUser.cs 订阅 `OnUserQueryRaised`，渲染选项面板（无超时·永久阻断）。用户点选项 / 自由文本 → `ResumeFromUserInput(answer)` → 追加 user 消息 + `TriggerResumeLLMCall()`。

### 2.3 跨 domain reload 存活

- `DomainReloadState` 加 3 字段 + Save/Clear/Has。RecordPendingUserQuery 存盘，应答/放弃清盘。
- `OnBeforeAssemblyReload`：WaitingForUserInput 视同**干净挂起**（与 Idle 同路径，不标记 `_wasInterrupted`）。
- reload 后 `TryRestorePendingAskUser` → `RestorePendingUserInputFromReload` → 重建面板。

## 3. Consequences（后果与关键权衡）

### 3.1 占位 result 不可避免（R1 定案）

BuildToolMessagesWithCompressionAsync 对所有 result 无差别写 `ChatMessage.Tool(id, content)`，所以 ask_user 的占位 result 一定被写入 —— 无法避免也不必避免（保证一个 tool_call 恰好一个 result 的历史合法性）。因此唤醒**不能补第二个 result**（双 result 非法），改为**追加一条 user 消息**携带答案。占位文本明确告知 LLM"真正答案在随后的 user 消息"。

### 3.2 时序竞态（R4 已排除）

OnUserQueryRaised 在 ExecuteToolCallsAsync 内触发，此时 loop 尚未走到 Runner 的 SetState。潜在竞态由 `EditorApplication.delayCall` 排除：HandleUserQueryRaised 把 ShowUserQuery 推迟到下一编辑器 tick，loop 剩余同步代码（含截断）在当前调用栈内跑完，面板渲染必然晚于截断。**delayCall 是关键防线，勿去除。**

### 3.3 无 emoji

选项按钮/提示文本严禁 emoji（SDF 字体渲染成方块，SOUL §3）。

## 4. 涉及文件

- 新增：`AgentLoop.AskUser.cs`（字段/事件 OnUserQueryRaised/RecordPendingUserQuery/ResumeFromUserInput/AbandonPendingUserInput/RestorePendingUserInputFromReload）、`ChatWindow.AskUser.cs`（事件驱动重写，无 TCS/超时）、`AskUserTool.cs`（纯函数）
- 改动：`IAgentTool.cs`（ToolResult 加 3 字段）、`AgentLoop.Tools.cs`（检测标志）、`AgentLoop.Runner.cs`（截断 return）、`AgentLoop.DomainReload.cs`（WaitingForUserInput 干净挂起）、`DomainReloadState.cs`（3 字段 + Save/Clear/Has）、`ChatWindow.cs`（订阅/退订 + TryRestorePendingAskUser）、`ChatWindow.Input.cs`（自由文本拦截）、`MessageTypes.cs`（AgentState.WaitingForUserInput）、`SOUL.md`（§2 中途收束方向引导）
- 删除：`AskUserRequest.cs`（旧同步模型死代码）
