# ADR: Self-Challenge 韧性重构方案

文档日期: 2026-07-09
目标: 系统性修复 Self-Challenge 机制在生产环境的 prompt / 状态机 / UI 三层失效,使 correction retry 不空转、WaitingForClarification 可交互、Node B 不跨轮竞争
范围: AgentCore.Editor Core + UI 层(Prompt / State Machine / UI Interaction 三层一致性设计)

---

## 1. 结论

### 推荐方案

三层并行重构,共享一个设计原则 — **"状态边界即数据边界"**:每个 Self-Challenge 子阶段(Node A / Node A Continuation / Node B)的生命周期必须由显式状态封装,字段生命周期与状态绑定,禁止跨阶段共享可变字段。

| 层 | 改动 | 核心文件 |
|----|------|----------|
| A. Prompt | correction retry instruction 增加硬 marker 包裹约束 + 最小骨架示例 | `IntentChallengePromptBuilder` / `AnswerChallengePromptBuilder` |
| B1. 状态机 | 统一 send gate(Idle || WaitingForClarification);Node B fire-and-forget 改为可追踪生命周期,消除 `_currentSelfChallengeData` 跨轮覆盖 | `ChatWindow.Input` / `AgentLoop.Runner` / `AgentLoop.SelfChallenge` |
| B2. UI 交互 | WaitingForClarification 状态下渲染 clarification option 卡片(数据源 = `SelfChallengeData.Interpretations`);卡片点击 = 直接发送该 interpretation;用户手动输入 = 销毁卡片、接纳用户文本走 Continuation | `AssistantTurnView` / `ChatWindow.SelfChallenge` / 新组件 `ClarificationOptionCard` |

### 不推荐

- 仅修 prompt(只解 A,B1/B2 仍失效)
- 仅对齐 send gate(只解 B1 入口,Node B 竞争未解)
- 将 Node B 改为同步阻塞(违反 §1.3.2 异步设计契约,且 REVISE 重生成会阻塞用户)

### 风险点

- Node B 生命周期管理引入新状态 `ReviewingAnswer`,需与 Domain Reload 恢复逻辑对齐
- clarification 卡片数据源在 Node A 结构校验 fallback 时可能为空,需兜底降级为纯文本提示

---

## 2. 背景 / 现状

### 2.1 已修复(本轮 Session)

原始 bug:Node A skip 路径未激活 extractor,LLM 因上文学习泄漏 `<intent_challenge>` 块到 UI 气泡。
修复:extractor 常驻化(Full + Continuation 双实例),与 `_nodeAEnabledThisTurn` 解耦,剥离始终生效,结构校验/事件仍按模式 gate。
状态:已合入,日志未再见 challenge 块渲染到气泡。

### 2.2 未修复(本 ADR 目标)

从生产日志发现三类次生失效:

#### 失效 A — Correction Retry 空转

日志证据:
```
Node A retry attempt 1/2: FinalizeContent state = None
Node A correction retry exhausted after 2 attempts
Node B attempt 0/1/2: extractor state = None
Node B correction retry exhausted after 3 attempts
```

根因(事实,基于 [`IntentChallengePromptBuilder.BuildCorrectionRetryInstruction`](../Editor/Core/SelfChallenge/IntentChallengePromptBuilder.cs:212) 与 [`AnswerChallengePromptBuilder.BuildCorrectionRetryInstruction`](../Editor/Core/SelfChallenge/AnswerChallengePromptBuilder.cs:109) 源码):

- Node A retry prompt 仅有 "ensuring all 5 Steps + `<consistency_correction>` block are present" 的描述性约束,**未要求输出必须以 `<intent_challenge>` 开头、`</intent_challenge>` 结尾**
- LLM 理解为"重写内容但不强制 marker 包裹" → 输出散文 → [`FinalizeContent`](../Editor/Core/SelfChallenge/IntentChallengeStreamExtractor.cs:128) 找不到 open marker → `State = None`(非 Completed)→ 所有 retry `continue` → 耗尽 fallback
- Node B 同构:`BuildCorrectionRetryInstruction` 要求 "Ensure all 4 Steps present" 但未硬性要求 `<answer_challenge>` 包裹

#### 失效 B1 — Send Gate 失配 + Node B 跨轮竞争

日志证据:
```
Cannot send message while agent is busy
```

根因(事实):

- UI gate [`ChatWindow.Input.OnSendClicked`](../Editor/UI/ChatWindow.Input.cs:27):`if (_agentLoop.CurrentState != AgentState.Idle) return;` — 仅允许 Idle
- Core gate [`AgentLoop.SendMessageAsync`](../Editor/Core/AgentLoop.cs:357):`if (CurrentState != Idle && CurrentState != WaitingForClarification) throw;` — 允许 Idle || WaitingForClarification
- 两层 gate 不一致:Node A 结论为 Combo1/Combo2 时进入 WaitingForClarification,Core 允许用户回复,UI 拦截 → 用户无法回答澄清问题

额外竞争(事实,基于 [`TriggerNodeBAsync`](../Editor/Core/AgentLoop.Runner.cs:406) + [`PrepareSelfChallengeDataForNewTurn`](../Editor/Core/AgentLoop.SelfChallenge.cs:121)):

- `HandleFinalResponse` 执行 `_ = TriggerNodeBAsync(...)` fire-and-forget,随后 `SetState(Idle)`
- Node B 后台 Task 内 `InvokeNodeBAsync` 读写实例字段 `_currentSelfChallengeData`(line 540-541: `if (_currentSelfChallengeData == null) _currentSelfChallengeData = new SelfChallengeData();`)
- 若 Node B 未完成时用户发新消息,`PrepareSelfChallengeDataForNewTurn` 执行 `_currentSelfChallengeData = new SelfChallengeData()`(line 121)覆盖字段
- Node B 后台 Task 后续 `assistantTurn.SelfChallenge = _currentSelfChallengeData`(line 618)将**新轮数据**挂到**旧轮 turn** — SelfChallengeCard 显示错位
- 工程判断:此竞争窗口小(Node B 通常秒级完成),但一旦命中即数据污染,且难复现

#### 失效 B2 — WaitingForClarification 无交互入口

现状:进入 WaitingForClarification 后,UI 仅显示 assistant 气泡中的 `[CLARIFICATION NEEDED]` 文本,无结构化选项卡片。用户需自行阅读 Interpretation 1/2/3 并手动输入。

根因(事实):[`AssistantTurnView`](../Editor/UI/Components/AssistantTurnView.cs:40) 仅有 `SelfChallengeCard` 挂载点,无 clarification option 卡片;[`ChatWindow.SelfChallenge.HandleSelfChallengeEvent`](../Editor/UI/ChatWindow.SelfChallenge.cs:16) 仅响应 `IntentChallengeCompleted` / `AnswerChallengeCompleted`,无 `ClarificationRequested` 事件分发。

---

## 3. 方案设计

### 3.1 A 层:Prompt 硬约束

#### Node A Correction Retry

修改 [`IntentChallengePromptBuilder.BuildCorrectionRetryInstruction`](../Editor/Core/SelfChallenge/IntentChallengePromptBuilder.cs:212),追加硬约束段:

```
[HARD CONSTRAINT]
Your entire output must be a single block wrapped in markers:
  <intent_challenge> ... </intent_challenge>   (Full mode)
  <intent_challenge_continuation> ... </intent_challenge_continuation>   (Continuation mode)

- Do NOT output any prose, explanation, or acknowledgment before the opening marker.
- Do NOT output any text after the closing marker.
- The opening marker must be the first non-whitespace character of your response.
- The closing marker must be the last non-whitespace character of your response.

[MINIMAL SKELETON]
<intent_challenge>
## Step 1: Interpretations
- Interpretation 1: ...
- Interpretation 2: ...
- Interpretation 3: ...
## Step 2: 找出歧义信号
- ...
## Step 3: 选定工作解读
Chosen interpretation: Interpretation N
关键假设: ...
## Step 4: 澄清决策
- A = yes/no
- B = severe/minor
- C = destructive/safe
- D = inferred/verbatim
- [ ] 命中组合 1 / [ ] 命中组合 2 / [ ] 都不命中
## Step 5: Self-Consistency Check
<consistency_correction>
- 一致性 1: PASS/FAIL
- 一致性 2: PASS/FAIL
- 一致性 3: PASS/FAIL
- 一致性 4: PASS/FAIL
[Consistent] / Corrected judgement: ...
</consistency_correction>
</intent_challenge>
```

Continuation 模式提供对应 3-cont/4-cont/5-cont 骨架。

#### Node B Correction Retry

修改 [`AnswerChallengePromptBuilder.BuildCorrectionRetryInstruction`](../Editor/Core/SelfChallenge/AnswerChallengePromptBuilder.cs:109),同构追加:

```
[HARD CONSTRAINT]
Your entire output must be a single block wrapped in:
  <answer_challenge> ... </answer_challenge>

- Opening marker = first non-whitespace character.
- Closing marker = last non-whitespace character.
- No prose outside markers.
```

#### 设计权衡

- 骨架示例增加 prompt token(~150 tokens),但 retry 本身是低频路径,成本可接受
- 硬约束措辞借鉴 v0.10 设计文档已验证的 marker 约束风格,与 Node A 主 instruction 一致
- 不引入 JSON mode / function calling — Self-Challenge 块是半结构化 Markdown,JSON 化会破坏 LLM 推理质量

### 3.2 B1 层:状态机统一 + Node B 生命周期

#### 3.2.1 统一 Send Gate

修改 [`ChatWindow.Input.OnSendClicked`](../Editor/UI/ChatWindow.Input.cs:27):

```csharp
// Before:
if (_agentLoop.CurrentState != AgentState.Idle) { ... return; }

// After:
if (_agentLoop.CurrentState != AgentState.Idle &&
    _agentLoop.CurrentState != AgentState.WaitingForClarification)
{
    Debug.LogWarning("[AgentCore] Cannot send message while agent is busy.");
    return;
}
```

与 Core gate 完全对齐。`OnInputFieldKeyDown` / `OnWindowKeyDown` 中的 Escape 取消判断保持 `!= Idle`(WaitingForClarification 下 Escape 不取消,因无正在进行的操作可取消)。

#### 3.2.2 Node B 生命周期管理

**问题**:fire-and-forget + 共享可变字段 = 跨轮覆盖。

**方案**:引入 Node B 专用状态 + 字段隔离。

新增状态(扩展 [`AgentState`](../Editor/Core/MessageTypes.cs:18)):

```csharp
/// <summary>Node B(Answer Self-Challenge)异步审查进行中。该状态下拒绝新 SendMessageAsync。</summary>
ReviewingAnswer,
```

修改 [`HandleFinalResponse`](../Editor/Core/AgentLoop.Runner.cs:373):

```csharp
// Before:
if (!entersClarification)
{
    if (settings.selfChallengeEnabled)
        _ = TriggerNodeBAsync(assistantMessage, assistantTurn);
}

// After:
if (!entersClarification && settings.selfChallengeEnabled)
{
    SetState(AgentState.ReviewingAnswer);
    _ = TriggerNodeBAsync(assistantMessage, assistantTurn).ContinueWith(t =>
    {
        // Node B 完成(无论成功/失败/skip)后回到 Idle
        if (CurrentState == AgentState.ReviewingAnswer)
            SetState(AgentState.Idle);
    }, TaskScheduler.Default);
}
```

修改 [`AgentLoop.SendMessageAsync`](../Editor/Core/AgentLoop.cs:357) gate:

```csharp
// Before:
if (CurrentState != AgentState.Idle && CurrentState != AgentState.WaitingForClarification)

// After:
if (CurrentState != AgentState.Idle &&
    CurrentState != AgentState.WaitingForClarification &&
    CurrentState != AgentState.ReviewingAnswer)
// ReviewingAnswer 仍走 throw 分支 — 拒绝并发新轮
```

**字段隔离**:`TriggerNodeBAsync` / `InvokeNodeBAsync` 内不再读写实例字段 `_currentSelfChallengeData`,改为通过参数传递 turn-bound 副本:

```csharp
private async Task TriggerNodeBAsync(ChatMessage assistantMessage, ConversationTurn assistantTurn)
{
    // 捕获 turn-bound 快照,隔离后续轮次对 _currentSelfChallengeData 的覆盖
    var turnBoundData = assistantTurn?.SelfChallenge ?? new SelfChallengeData();
    var draftContent = assistantTurn?.Content ?? string.Empty;
    // ... 后续 InvokeNodeBAsync 使用 turnBoundData,不碰实例字段
}
```

[`InvokeNodeBAsync`](../Editor/Core/AgentLoop.SelfChallenge.cs:533) 签名调整:增加 `SelfChallengeData turnBoundData` 参数,移除 `if (_currentSelfChallengeData == null) _currentSelfChallengeData = new()` 的字段回填逻辑。

#### 3.2.3 Domain Reload 对齐

`ReviewingAnswer` 状态需在 [`AgentLoop.DomainReload`](../Editor/Core/AgentLoop.DomainReload.cs) 的恢复逻辑中处理:

- Domain Reload 发生在 ReviewingAnswer 时,恢复后状态降级为 Idle(接受 Node B 未完成的损失,因 Node B 结果仅用于 REVISE 重生成,未完成则接受原 draft)
- 需在 DomainReload 恢复路径增加 `case AgentState.ReviewingAnswer: SetState(Idle); break;`

工程判断:Domain Reload 期间 Node B Task 已丢失(进程级重载),无法恢复,降级为接受原 draft 是安全策略 — 与当前 Node B 失败时的 fallback 一致。

### 3.3 B2 层:Clarification Option 卡片

#### 3.3.1 数据源

主数据源:[`SelfChallengeData.Interpretations`](../Editor/Core/MessageTypes.cs)(`List<string>`,Node A Full 模式下由 [`IntentChallengeParser.ExtractInterpretations`](../Editor/Core/SelfChallenge/IntentChallengeParser.cs:340) 填充)。

兜底:Node A 结构校验失败 + retry fallback 时 `Interpretations` 可能为空。此时卡片降级为纯文本提示:"助手需要澄清,请在下方输入框回复",不渲染选项按钮。

#### 3.3.2 事件分发

新增事件类型(扩展 [`AgentEventType`](../Editor/Core/MessageTypes.cs:54)):

```csharp
/// <summary>Node A 结论为 Combo1/Combo2,进入 WaitingForClarification。Payload 携带澄清选项。</summary>
ClarificationRequested,
```

新增 AgentEvent 工厂方法 + Payload 类型:

```csharp
public class ClarificationPayload
{
    public string TurnId { get; set; }
    public string ClarificationMessage { get; set; }  // [CLARIFICATION NEEDED] 后的文本
    public List<string> Options { get; set; }          // Interpretation 1/2/3 文本
}

public static AgentEvent ClarificationRequested(ClarificationPayload payload)
    => new AgentEvent(AgentEventType.ClarificationRequested, content: payload.ClarificationMessage,
                      turnId: payload.TurnId, /* payload via new field */);
```

在 [`HandleNodeAConclusionForFinalResponse`](../Editor/Core/AgentLoop.SelfChallenge.cs:408) 进入 WaitingForClarification 前 emit:

```csharp
SetState(AgentState.WaitingForClarification);
EmitEvent(AgentEvent.ClarificationRequested(new ClarificationPayload
{
    TurnId = _currentSelfChallengeTurnId ?? assistantTurn?.Id,
    ClarificationMessage = _pendingClarificationMessage,
    Options = _currentSelfChallengeData?.Interpretations ?? new List<string>()
}));
```

#### 3.3.3 UI 组件

新增 `Packages/com.agentcore/Editor/UI/Components/ClarificationOptionCard.cs`:

```csharp
public class ClarificationOptionCard : VisualElement
{
    public event Action<string> OptionSelected;  // 参数 = 选中的 interpretation 文本

    public ClarificationOptionCard(string messageId, string message, List<string> options)
    {
        // 渲染:标题"需要澄清" + message 文本 + 每个 option 一个 Button
        // Button.click += () => OptionSelected?.Invoke(optionText);
    }
}
```

[`AssistantTurnView`](../Editor/UI/Components/AssistantTurnView.cs) 增加 `_clarificationSlot`(位于 `_bubbleSlot` 之后,作为气泡下方的交互区)与 `EnsureClarificationCard` 方法。

[`ChatWindow.SelfChallenge.HandleSelfChallengeEvent`](../Editor/UI/ChatWindow.SelfChallenge.cs:16) 扩展分发:

```csharp
case AgentEventType.ClarificationRequested:
    var card = turnView.EnsureClarificationCard(evt.TurnId, payload.ClarificationMessage, payload.Options);
    card.OptionSelected += OnClarificationOptionSelected;
    break;
```

#### 3.3.4 交互语义

| 用户动作 | 行为 |
|----------|------|
| 点击 Option Button | 以该 interpretation 文本作为 user message,调用 `SendMessageAsync(optionText)`,走 Continuation |
| 输入框手动输入 + 发送 | 正常 `OnSendClicked` 流程;卡片在新轮 assistant turn 创建时自然失效(旧 turn view 不再更新) |

关键:两条路径都进入 [`SendMessageAsync`](../Editor/Core/AgentLoop.cs:341),`PrepareSelfChallengeDataForNewTurn` 检测 `isContinuation = CurrentState == WaitingForClarification` = true,走 Continuation 模式。用户手动输入"接纳为选项继续执行"的语义由 Continuation 路径天然支持 — Continuation prompt 不强制要求用户回复必须是某个 Interpretation,而是让 Node A 重新校验。

#### 3.3.5 卡片销毁

- 进入 WaitingForClarification 时渲染卡片
- Continuation 轮次完成后(`ClearPendingClarificationIfNeeded` 清状态),卡片保持显示在旧 turn 上(作为历史记录),但 Button 禁用(`SetEnabled(false)`)
- 新轮 assistant turn 不再有卡片

---

## 4. 风险与考虑

### 4.1 ReviewingAnswer 状态扩展的影响面

| 影响点 | 性质 | 处理 |
|--------|------|------|
| `AgentState` 枚举 | 新增值,非破坏 | 所有 switch 需检查 default 分支 |
| `SendMessageAsync` gate | 收紧(拒绝 ReviewingAnswer) | 符合预期 — Node B 在途不应并发新轮 |
| Domain Reload 恢复 | 需新增 case | 降级为 Idle,接受 Node B 损失 |
| UI 状态显示 | 需更新状态栏文案 | 新增"审查回答中..."提示 |
| Session 持久化 | 状态需可序列化 | 枚举值自动可序列化,但 ReviewingAnswer 恢复后应降级为 Idle(不应持久化跨会话) |

工程判断:新增状态是必要代价 — fire-and-forget 的替代方案是同步阻塞(违反异步契约)或引入 `_pendingNodeBTask` 字段追踪(增加隐式状态机)。显式状态更符合 v0.10 设计文档的状态机风格。

### 4.2 Clarification 数据源空兜底

Node A retry fallback 后 `Interpretations` 为空时的行为:
- 卡片降级为纯文本提示,不渲染 Button
- 用户仍可手动输入(WaitingForClarification gate 已开)
- 不阻塞流程,仅损失"快捷选项"体验

### 4.3 Prompt 骨架示例的 token 成本

- Node A retry 骨架 ~150 tokens,Node B retry 骨架 ~100 tokens
- retry 是低频路径(仅结构校验失败时触发),最大 2-3 次/轮
- 相比 retry 空转浪费的完整 LLM 调用成本,骨架成本可忽略

### 4.4 未覆盖项(明确标注)

- **Node B BLOCK verdict 的验证循环**:`TriggerNodeBAsync` 中 BLOCK 仅 `Debug.LogWarning`,v1.5.0-alpha 未实施。本 ADR 不覆盖,留给 v1.5.0-beta。
- **ClarificationPayload 传递机制**:当前 `AgentEvent` 无专用 Payload 字段,需新增或复用 `SelfChallenge` 字段。实现时需确认 `AgentEvent` 扩展方式(新增字段 vs 复用)。
- **`ClearPendingClarificationIfNeeded` 调用时机**:搜索未找到显式调用点,疑似 Continuation 完成后隐式清理。实现时需确认,若未调用则 `_pendingClarification*` 字段残留(但不影响功能,因 `isContinuation` 判断基于 `CurrentState`)。

---

## 5. 实施步骤

按依赖顺序,每步可独立验证:

### Step 1: A 层 Prompt 修复(零依赖,可先行)

1. 修改 `IntentChallengePromptBuilder.BuildCorrectionRetryInstruction`:追加 HARD CONSTRAINT + 骨架示例
2. 修改 `AnswerChallengePromptBuilder.BuildCorrectionRetryInstruction`:同构
3. 验证:构造故意残缺的 Node A 输出 → 触发 retry → 确认 retry 输出以 marker 开头/结尾 → `FinalizeContent.State = Completed`

### Step 2: B1 层 Send Gate 对齐(零依赖)

1. 修改 `ChatWindow.Input.OnSendClicked`:gate 扩展为 `Idle || WaitingForClarification`
2. 验证:进入 WaitingForClarification 后,输入框可发送(不再 "Cannot send message while agent is busy")

### Step 3: B1 层 Node B 生命周期(依赖 AgentState 扩展)

1. `MessageTypes.cs` 新增 `AgentState.ReviewingAnswer`
2. `AgentLoop.Runner.HandleFinalResponse`:Node B 触发前 `SetState(ReviewingAnswer)`,`ContinueWith` 回 Idle
3. `AgentLoop.SendMessageAsync` gate:增加 `ReviewingAnswer` 到拒绝列表
4. `AgentLoop.SelfChallenge.InvokeNodeBAsync`:签名增加 `SelfChallengeData turnBoundData`,移除实例字段读写
5. `AgentLoop.DomainReload`:新增 `ReviewingAnswer` 恢复 case(降级 Idle)
6. 验证:Node B 在途时尝试发消息 → 被拒(状态非 Idle/WaitingForClarification);Node B 完成后可发

### Step 4: B2 层 Clarification 卡片(依赖 Step 2)

1. `MessageTypes.cs` 新增 `AgentEventType.ClarificationRequested` + `ClarificationPayload` + 工厂方法
2. `AgentLoop.SelfChallenge.HandleNodeAConclusionForFinalResponse`:进入 WaitingForClarification 前 emit 事件
3. 新增 `ClarificationOptionCard` 组件
4. `AssistantTurnView`:增加 `_clarificationSlot` + `EnsureClarificationCard`
5. `ChatWindow.SelfChallenge.HandleSelfChallengeEvent`:新增 `ClarificationRequested` 分发
6. `ChatWindow`:实现 `OnClarificationOptionSelected(string optionText)` → `SendMessageAsync(optionText)`
7. 验证:Node A 结论 Combo1 → 卡片渲染 Interpretation 1/2/3 → 点击 Option → Continuation 执行;手动输入 → Continuation 执行

### Step 5: 集成验证

1. 完整对话流:用户消息 → Node A → Combo1 → 卡片 → 用户点 Option → Continuation → Node B → 完成
2. 异常流:Node A retry fallback(Interpretations 空)→ 卡片降级文本 → 用户手动输入 → Continuation
3. 竞争流:Node B 在途 → 用户尝试发消息 → 被拒 → Node B 完成 → 可发

---

## 6. 验证方法

| 验证项 | 方法 | 预期 |
|--------|------|------|
| A: retry marker 包裹 | 构造残缺 Node A 输出,触发 retry,检查 retry 输出 | 首尾为 marker,`FinalizeContent.State = Completed` |
| B1: send gate | WaitingForClarification 下输入框发送 | 不再 "Cannot send" 警告,消息进入 Continuation |
| B1: Node B 隔离 | Node B 在途发新消息 | 被拒(ReviewingAnswer);Node B 完成后旧 turn SelfChallengeCard 数据正确 |
| B2: 卡片渲染 | Node A Combo1 结论 | assistant 气泡下方出现 Option 卡片,列 Interpretation 1/2/3 |
| B2: 点击发送 | 点击 Option Button | 该 interpretation 文本作为 user message 发送,Continuation 执行 |
| B2: 手动覆盖 | 卡片显示时手动输入发送 | 用户文本走 Continuation,卡片在新轮后失效 |
| Domain Reload | ReviewingAnswer 下触发 Domain Reload | 恢复后状态 = Idle,不卡死 |

---

## 7. 参考

- 设计文档 v0.10 §1.2.4(Node A 结构校验)、§1.3.2(Node B 异步)、§0.4(REVISE 重生成不复审)
- ADR-17(minimalism,AllowClarificationQuestions 常量化)
- 本轮 Session 已修复:extractor 常驻化(`AgentLoop.SelfChallenge.cs` field + Reset + ProcessToken 三段)
- 生产日志(用户提供):retry state=None / Cannot send while busy / Node B exhausted

---

## 附录:事实 vs 推断标注

| 结论 | 依据类别 |
|------|----------|
| retry prompt 未要求 marker 包裹 | 事实(源码 line 212-240) |
| FinalizeContent state=None 因无 open marker | 事实(`IntentChallengeStreamExtractor.FinalizeContent` 逻辑) |
| UI gate ≠ Core gate | 事实(源码对比) |
| Node B fire-and-forget 共享 `_currentSelfChallengeData` | 事实(源码 line 406 + 540-541) |
| 跨轮覆盖导致数据错位 | 推断(基于字段读写时序分析,未复现但逻辑成立) |
| Interpretations 在 Full 模式填充 | 事实(parser line 278-284) |
| Interpretations 在 retry fallback 时可能为空 | 推断(fallback 路径不保证 parser 成功,但 `_currentSelfChallengeData` 仍被 emit) |
| Domain Reload 期间 Node B Task 丢失 | 事实(进程级重载,Task 无法跨进程恢复) |
