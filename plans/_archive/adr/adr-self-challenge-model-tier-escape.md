# ADR: Self-Challenge 模型分级逃逸(方向修正)

文档日期: 2026-07-09
目标: 将 Self-Challenge 从"核心机制"回归为"补偿性机制",高级模型完全逃逸依赖 native thinking,bug 修复降级为低性能模型路径局部补丁,支持热插拔
范围: AgentCore.Editor Core + Config + UI
状态: **废弃** [`adr-self-challenge-resilience-refactor.md`](adr-self-challenge-resilience-refactor.md)(原方案在错误方向加码)

---

## 1. 结论

### 设计定位修正

**Self-Challenge 是补偿性机制,不是核心机制。**

原设计意图(来自 [`AgentCoreSettings.selfChallengeEnabled`](../Editor/Config/AgentCoreSettings.cs:72) Tooltip):"Node A challenges intent + Node B reviews draft; +10~50% tokens per turn" — 自知是成本,为低性能模型无法回看自身内容而补偿。

高级模型(Claude Opus / GPT-o 系列 / DeepSeek-R)具备 native extended thinking,自挑战的显式 5 步结构化校验会与 native reasoning 重复,导致:
- thinking 重复消耗(native reasoning + 显式 Step 1-5 双轨)
- context 累积(Node A instruction 注入 system message)
- token 快速消耗(Node B reviewer 独立 LLM 调用)
- LLM 认知混淆(强制走 5 步 vs 自由思考)
- 上下文污染(challenge 块虽剥离,但 retry 独立小会话仍消耗)

### 推荐方案

**模型分级逃逸 + 局部补丁 + 热插拔**,三层独立但协同:

| 层 | 改动 | 性质 |
|----|------|------|
| L1. 模型能力探测 | 新增 `ModelCapabilityDetector`,基于 [`ContextWindowManager.ModelPrefixMap`](../Editor/Core/ContextWindowManager.cs:29) 同构前缀表探测 native reasoning 能力 | 新基础设施 |
| L2. 自挑战逃逸门 | `PrepareSelfChallengeDataForNewTurn` / `HandleFinalResponse` 增加逃逸判定:高级模型跳过 Node A + Node B,依赖 native thinking | 核心逻辑 |
| L3. 热插拔开关 | 新增 `selfChallengeEscapeEnabled`(默认 true) + 运行时实时生效,不重启 | 配置层 |
| L4. 局部补丁 | 原 A/B1/B2 bug 修复**仅作用于低性能模型路径**,降级为补丁而非重构 | 补丁 |

### 不推荐

- ~~原 ADR 三层重构~~(在错误方向加码,强化自挑战为核心机制)
- 高级模型保留 Node B 作为兜底(Node B 与 native thinking 仍重复,且 Node B reviewer 独立调用本身就是 token 消耗)
- 保留自挑战但改轻量模式(轻量提示仍注入 context,且 LLM 认知混淆风险仍在)

### 风险点

- 模型能力探测基于前缀匹配,未知模型/新模型需手动更新表
- 高级模型逃逸后无工程侧结构化校验,完全信任 native thinking — 若 native reasoning 质量不足则无兜底
- 热插拔需处理在途 Node B Task 的状态一致性

---

## 2. 背景 / 现状

### 2.1 原方案错误(本次方向修正触发)

[`adr-self-challenge-resilience-refactor.md`](adr-self-challenge-resilience-refactor.md) 提出"prompt / 状态机 / UI 三层重构强化自挑战",方向错误:
- 将补偿性机制当作核心机制加固
- 未考虑高级模型 native thinking 与自挑战的重复消耗
- ADR-17 极简哲学下 `selfChallengeEnabled` 默认 true + 无模型分级 = 对所有模型强制自挑战

用户质疑(原话):"自挑战设计为了在一定程度上解决低性能模型执行相对复杂的任务时无法回看自己的内容而设计的,如果自挑战设计架构用在高级模型,会不会让 thinking 重复消耗, context 大量累积, token 快速消耗, LLM 认知混淆, 上下文污染这类的问题"

### 2.2 现有架构事实

| 事实 | 依据 |
|------|------|
| 自挑战无模型逃逸路径 | [`AgentLoop.SelfChallenge.PrepareSelfChallengeDataForNewTurn`](../Editor/Core/AgentLoop.SelfChallenge.cs:118) 仅检查 `selfChallengeEnabled`,无模型分级 |
| native reasoning 通路存在 | [`RequestEnrichment.InjectReasoning`](../Editor/LLM/RequestEnrichment.cs:79) + [`enableReasoningOutput`](../Editor/Config/AgentCoreSettings.cs:188) / `reasoningEffort` |
| 模型分级基础设施存在 | [`ContextWindowManager.ModelPrefixMap`](../Editor/Core/ContextWindowManager.cs:29) 前缀匹配表 |
| 自挑战默认全开 | [`AgentCoreSettings.selfChallengeEnabled`](../Editor/Config/AgentCoreSettings.cs:72) = true,ADR-17 "一开全开" |
| 已知 bug 仍需修(低性能模型路径) | A(retry prompt)/B1(send gate)/B2(clarification 卡片) |

### 2.3 高级模型重复消耗的机制分析(工程推断)

| 重复类型 | 机制 |
|----------|------|
| thinking 重复 | native extended thinking(Claude Opus / GPT-o 内部 reasoning)+ Node A 显式 Step 1-5 Interpretation/Ambiguity/Chosen/Step4/Step5 = 双轨意图校验 |
| context 累积 | [`BuildNodeAInstructionForCurrentTurn`](../Editor/Core/AgentLoop.SelfChallenge.cs:163) 注入 system message(~数百 tokens),每轮累积 |
| token 消耗 | Node B reviewer 独立 `ChatCompletionAsync` 调用([`InvokeNodeBAsync`](../Editor/Core/AgentLoop.SelfChallenge.cs:533)),含完整对话历史 + draft |
| 认知混淆 | 高级模型 native reasoning 是自由形式,强制 `<intent_challenge>` 5 步结构化会打断其自然推理流 |
| 上下文污染 | challenge 块主历史已剥离,但 retry 独立小会话([`TryNodeACorrectionRetryAsync`](../Editor/Core/AgentLoop.SelfChallenge.cs:320))仍携带旧 Node A output + user query |

标注:thinking 重复 / context 累积 / token 消耗为事实(基于源码路径分析);认知混淆 / 上下文污染为工程推断(基于 LLM 行为一般规律,未在该项目实测)。

---

## 3. 方案设计

### 3.1 L1:模型能力探测

新增 `Packages/com.agentcore/Editor/Core/ModelCapabilityDetector.cs`:

```csharp
namespace AgentCore.Editor.Core
{
    /// <summary>
    /// 模型能力探测器 — 基于 ContextWindowManager.ModelPrefixMap 同构前缀表,
    /// 探测当前 LLM 是否具备 native reasoning(extended thinking)能力。
    /// 用于 Self-Challenge 逃逸判定。
    /// </summary>
    public static class ModelCapabilityDetector
    {
        /// <summary>
        /// 具备 native reasoning 的模型前缀表。
        /// 命中即视为高级模型,Self-Challenge 应逃逸。
        /// </summary>
        private static readonly string[] NativeReasoningPrefixes =
        {
            "claude-opus",       // Claude Opus 全系
            "claude-3-opus",
            "claude-sonnet-4",   // Claude Sonnet 4+(具备 extended thinking)
            "o1-",               // OpenAI o1
            "o3-",               // OpenAI o3
            "o4-",               // OpenAI o4
            "gpt-5",             // GPT-5
            "deepseek-r",        // DeepSeek R 系列(推理模型)
            "gemini-2.5",        // Gemini 2.5 Pro(具备 thinking)
        };

        /// <summary>
        /// 判定模型是否具备 native reasoning 能力。
        /// </summary>
        public static bool HasNativeReasoning(string modelName)
        {
            if (string.IsNullOrEmpty(modelName)) return false;
            foreach (var prefix in NativeReasoningPrefixes)
            {
                if (modelName.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
```

设计权衡:
- 复用 [`ContextWindowManager`](../Editor/Core/ContextWindowManager.cs) 的前缀匹配模式,保持架构一致性
- 表与 `ModelPrefixMap` 分离 — 能力探测与 token 上限是正交维度,避免耦合
- 未知模型默认 `false`(保守,不逃逸,走自挑战)— 安全优先

### 3.2 L2:自挑战逃逸门

修改 [`AgentLoop.SelfChallenge.PrepareSelfChallengeDataForNewTurn`](../Editor/Core/AgentLoop.SelfChallenge.cs:118):

```csharp
internal SelfChallengeData PrepareSelfChallengeDataForNewTurn(string userMessage)
{
    var settings = AgentCoreSettings.instance;
    _currentSelfChallengeData = new SelfChallengeData();
    _nodeAEnabledThisTurn = false;
    _nodeATriggerContinuation = false;

    // 总开关关闭 → 不启用
    if (!settings.selfChallengeEnabled)
    {
        _currentSelfChallengeData = null;
        return null;
    }

    // [新增] 模型分级逃逸:高级模型 + 逃逸开关开启 → 跳过 Node A
    if (settings.selfChallengeEscapeEnabled &&
        ModelCapabilityDetector.HasNativeReasoning(settings.llmModel))
    {
        _currentSelfChallengeData.NodeATriggered = false;
        _currentSelfChallengeData.NodeASkipReason = "model_has_native_reasoning";
        _nodeAEnabledThisTurn = false;
        Debug.Log($"[AgentCore][SelfChallenge] Node A escaped: model '{settings.llmModel}' has native reasoning.");
        return _currentSelfChallengeData;
    }

    // ... 原有 Continuation / Skip / 启用逻辑不变
}
```

修改 [`AgentLoop.Runner.HandleFinalResponse`](../Editor/Core/AgentLoop.Runner.cs:373) Node B 触发:

```csharp
if (!entersClarification)
{
    var settings = AgentCoreSettings.instance;
    // [新增] Node B 同样受逃逸门控制
    bool nodeBShouldRun = settings.selfChallengeEnabled &&
        !(settings.selfChallengeEscapeEnabled &&
          ModelCapabilityDetector.HasNativeReasoning(settings.llmModel));

    if (nodeBShouldRun)
    {
        SetState(AgentState.ReviewingAnswer);  // 见 L4 补丁
        _ = TriggerNodeBAsync(assistantMessage, assistantTurn)
            .ContinueWith(_ => { if (CurrentState == AgentState.ReviewingAnswer) SetState(AgentState.Idle); },
                          TaskScheduler.Default);
    }
}
```

设计权衡:
- 逃逸判定在 `PrepareSelfChallengeDataForNewTurn` 与 `HandleFinalResponse` 两处独立执行,而非缓存到字段 — 避免热插拔时字段过期
- 高级模型逃逸后完全不进入 Node A / Node B,`SelfChallengeData` 仅记录 skip reason 供 UI 显示"已跳过(模型具备原生推理)"
- 逃逸后 `BuildNodeAInstructionForCurrentTurn` 返回 null(因 `_nodeAEnabledThisTurn = false`)— 不注入 instruction,零 context 累积

### 3.3 L3:热插拔开关

新增配置字段 [`AgentCoreSettings`](../Editor/Config/AgentCoreSettings.cs):

```csharp
[Tooltip("Enable model-tier escape — advanced models with native reasoning skip Self-Challenge to avoid duplicate thinking cost")]
public bool selfChallengeEscapeEnabled = true;
```

热插拔语义:
- 字段为普通 `public bool`,Unity Inspector 修改即生效(无需重新 Initialize)
- [`AgentCoreSettings.SaveSettings`](../Editor/Config/AgentCoreSettings.cs) 已在 Inspector 变更时调用
- 逃逸判定在每轮 `PrepareSelfChallengeDataForNewTurn` / `HandleFinalResponse` 实时读取 `settings.selfChallengeEscapeEnabled` — 不缓存
- 在途 Node B Task 不受热插拔影响(已启动的 Task 自然完成),下一轮按新配置判定

UI 暴露(对齐 ADR-17 极简哲学):
- [`ModelAgentSettingsPage`](../Editor/Config/Settings/Pages/ModelAgentSettingsPage.cs) Self-Challenge 卡片下新增 1 个 toggle:"高级模型自动跳过(推荐)"
- 默认 true,文案白话化,不暴露 "Node A / native reasoning" 等工程术语
- 当 `selfChallengeEnabled = false` 时,此 toggle 灰显(总开关关闭时逃逸无意义)

### 3.4 L4:局部补丁(仅低性能模型路径)

原 ADR 的 A/B1/B2 修复保留,但**降级为局部补丁**,仅在自挑战未逃逸时生效:

#### 补丁 A:retry prompt 硬约束

修改 [`IntentChallengePromptBuilder.BuildCorrectionRetryInstruction`](../Editor/Core/SelfChallenge/IntentChallengePromptBuilder.cs:212) 与 [`AnswerChallengePromptBuilder.BuildCorrectionRetryInstruction`](../Editor/Core/SelfChallenge/AnswerChallengePromptBuilder.cs:109),追加 HARD CONSTRAINT + 骨架示例(内容同原 ADR §3.1,此处不重复)。

生效条件:仅当 Node A / Node B 实际执行时(即未逃逸)才触发 retry。高级模型逃逸后根本不进入 retry 路径。

#### 补丁 B1:send gate 对齐 + Node B 生命周期

**send gate 对齐**:修改 [`ChatWindow.Input.OnSendClicked`](../Editor/UI/ChatWindow.Input.cs:27),gate 扩展为 `Idle || WaitingForClarification`。

生效条件:WaitingForClarification 仅由 Node A Combo1/Combo2 进入,高级模型逃逸 Node A 后不会进入此状态 — 补丁对高级模型无副作用,仅修复低性能模型路径。

**Node B 生命周期**:新增 `AgentState.ReviewingAnswer`,`HandleFinalResponse` 中 Node B 触发前 `SetState(ReviewingAnswer)`,完成时回 Idle;`InvokeNodeBAsync` 签名增加 `turnBoundData` 参数隔离实例字段。

生效条件:Node B 仅在未逃逸时触发,`ReviewingAnswer` 状态对高级模型永不出现。

#### 补丁 B2:clarification 卡片

新增 `ClarificationRequested` 事件 + `ClarificationOptionCard` 组件,数据源 = [`SelfChallengeData.Interpretations`](../Editor/Core/MessageTypes.cs)。

生效条件:clarification 卡片仅在 Node A 进入 WaitingForClarification 时渲染,高级模型逃逸 Node A 后不触发 — 卡片对高级模型永不出现。

### 3.5 逃逸后的行为对比

| 场景 | 低性能模型(自挑战启用) | 高级模型(逃逸启用) |
|------|------------------------|---------------------|
| Node A | 执行 5 步结构化校验 | 跳过,依赖 native thinking |
| Node A retry | 补丁 A 硬约束生效 | 不触发(未执行 Node A) |
| Node B | reviewer 独立调用 | 跳过 |
| WaitingForClarification | 可能进入(Node A Combo1/2) | 不进入(无 Node A) |
| clarification 卡片 | 可能渲染 | 不渲染 |
| ReviewingAnswer 状态 | 可能进入(Node B 在途) | 不进入 |
| context 注入 | Node A instruction 注入 | 零注入 |
| token 消耗 | +10~50%(Tooltip 声明) | 零额外(仅 native thinking) |

---

## 4. 风险与考虑

### 4.1 模型能力探测的准确性

- 前缀表基于已知模型系列,新模型(如未来 Claude 4 Opus / GPT-6)需手动添加
- 未知模型默认 `false`(不逃逸)— 保守策略,可能对新的高级模型仍走自挑战(用户可手动关闭 `selfChallengeEnabled`)
- 替代方案:运行时探测 reasoning_capability API — 复杂度过高,前缀表足够当前需求

### 4.2 高级模型无工程侧兜底

- 逃逸后完全信任 native thinking,无结构化校验
- 若 native reasoning 质量不足(如模型未充分激活 extended thinking),无工程侧补救
- 缓解:`enableReasoningOutput` + `reasoningEffort` 应在高级模型下默认启用(当前 HideInInspector + 默认空串,需评估是否调整默认值 — 留作后续 iteration)

### 4.3 热插拔一致性

- `selfChallengeEscapeEnabled` 实时生效,但 `llmModel` 变更后下一轮自动按新模型判定
- 在途 Node B Task 不受影响(自然完成),不会中断
- Domain Reload 后配置从持久化加载,状态一致

### 4.4 与 ADR-17 极简哲学的对齐

- 新增 1 个 toggle(`selfChallengeEscapeEnabled`)— ADR-17 允许"一件事一个开关",逃逸与总开关是正交控制,符合
- UI 文案白话化("高级模型自动跳过"),不暴露工程术语
- 默认 true,80% 用户(用高级模型)无需操作即获益

### 4.5 未覆盖项(明确标注)

- `enableReasoningOutput` / `reasoningEffort` 默认值调整:高级模型逃逸后应确保 native reasoning 实际激活,当前默认空串可能未触发。需后续评估,本 ADR 不覆盖。
- 模型能力探测的自动化扩展(如读取 API 返回的 capability 字段):前缀表足够当前,自动化留作后续。
- 高级模型下是否需要极轻量的"意图确认"提示(非结构化):当前方案是完全跳过,轻量提示作为可能的中间态未纳入。

---

## 5. 实施步骤

按依赖顺序,每步可独立验证:

### Step 1:L1 模型能力探测器(零依赖,可先行)

1. 新增 `ModelCapabilityDetector.cs`
2. 验证:单元测试前缀匹配(claude-opus → true / gpt-4o → false / unknown → false)

### Step 2:L3 热插拔开关(依赖 Settings)

1. `AgentCoreSettings.cs` 新增 `selfChallengeEscapeEnabled = true`
2. `ModelAgentSettingsPage.cs` Self-Challenge 卡片新增 toggle
3. 验证:Inspector 切换 toggle,无需重启即生效

### Step 3:L2 逃逸门(依赖 Step 1 + 2)

1. `AgentLoop.SelfChallenge.PrepareSelfChallengeDataForNewTurn`:增加逃逸判定
2. `AgentLoop.Runner.HandleFinalResponse`:Node B 触发增加逃逸判定
3. 验证:配置 claude-opus 模型 → Node A skip reason = "model_has_native_reasoning" → 不注入 instruction → Node B 不触发

### Step 4:L4 局部补丁(与 Step 3 并行,零依赖)

1. 补丁 A:`IntentChallengePromptBuilder` / `AnswerChallengePromptBuilder` retry prompt 硬约束
2. 补丁 B1:`ChatWindow.Input` send gate + `AgentState.ReviewingAnswer` + `InvokeNodeBAsync` 字段隔离
3. 补丁 B2:`ClarificationRequested` 事件 + `ClarificationOptionCard` 组件
4. 验证:配置低性能模型(如 deepseek-v3)→ 自挑战正常执行 → retry 硬约束生效 → WaitingForClarification 可发送 → clarification 卡片渲染

### Step 5:集成验证

1. 高级模型(claude-opus)+ 逃逸开启:完整对话,零自挑战 token 消耗,无 challenge 块,无卡片
2. 高级模型 + 逃逸关闭(用户手动):自挑战正常执行(用户显式选择承担成本)
3. 低性能模型(deepseek-v3)+ 逃逸开启:自挑战正常执行(模型未命中逃逸表)
4. 热插拔:对话中途切换 `selfChallengeEscapeEnabled`,下一轮按新配置执行

---

## 6. 验证方法

| 验证项 | 方法 | 预期 |
|--------|------|------|
| L1: 探测准确性 | 前缀匹配单元测试 | claude-opus / o3- / gpt-5 / deepseek-r → true;gpt-4o / qwen- / unknown → false |
| L2: 逃逸生效 | claude-opus 对话,检查日志 | "Node A escaped: model has native reasoning",无 Node A instruction 注入 |
| L2: Node B 逃逸 | claude-opus 对话,检查日志 | 无 Node B reviewer 调用,无 ReviewingAnswer 状态 |
| L3: 热插拔 | 对话中切换 toggle | 下一轮立即按新配置,无需重启 |
| L3: UI 暴露 | Inspector 查看 | Self-Challenge 卡片下有"高级模型自动跳过"toggle,默认开 |
| L4-A: retry 硬约束 | 低性能模型 + 构造残缺 Node A | retry 输出首尾为 marker,`FinalizeContent.State = Completed` |
| L4-B1: send gate | 低性能模型 WaitingForClarification | 输入框可发送 |
| L4-B2: 卡片 | 低性能模型 Node A Combo1 | clarification 卡片渲染 Interpretation 选项 |
| 逃逸后无副作用 | 高级模型完整对话 | 无 challenge 块、无卡片、无 ReviewingAnswer、无 retry |

---

## 7. 参考

- [`adr-17-minimalism.md`](adr-17-minimalism.md) — 极简哲学,"一件事一个开关"允许逃逸 toggle
- [`ContextWindowManager.cs`](../Editor/Core/ContextWindowManager.cs) — 模型前缀匹配基础设施(复用模式)
- [`RequestEnrichment.cs`](../Editor/LLM/RequestEnrichment.cs) — native reasoning 通路
- [`AgentCoreSettings.cs`](../Editor/Config/AgentCoreSettings.cs) — `selfChallengeEnabled` / `enableReasoningOutput` / `reasoningEffort`
- [废弃] [`adr-self-challenge-resilience-refactor.md`](adr-self-challenge-resilience-refactor.md) — 原错误方向方案

---

## 8. 挑战自查

**方向修正合理性**:
- 用户质疑成立 — 自挑战 Tooltip 自声明 "+10~50% tokens",自知是成本,非核心机制
- 原方案在"强化自挑战"加码,与 native thinking 重复,方向错误
- 本方案将自挑战回归补偿性定位,高级模型逃逸,符合原设计意图

**可能被质疑**:
- 模型能力探测前缀表需手动维护 — 但 `ContextWindowManager.ModelPrefixMap` 已是同构先例,维护成本可接受
- 高级模型无工程侧兜底 — 但 native thinking 本身就是兜底,工程侧再校验是重复
- 逃逸后 `SelfChallengeCard` UI 不再显示 — 对高级模型用户而言,不显示 = 无成本,符合预期

**与原方案的关系**:
- 原 ADR 的 A/B1/B2 修复技术内容保留,降级为 L4 补丁
- 原 ADR 的"三层一致性重构"框架废弃 — 不再强化自挑战,而是收缩其作用域
- 本 ADR 不删除原 ADR 文件,标注"废弃"以保留决策历史

---

## 附录:事实 vs 推断标注

| 结论 | 依据类别 |
|------|----------|
| 自挑战 Tooltip 声明 +10~50% tokens | 事实(源码 line 71) |
| 自挑战无模型逃逸路径 | 事实(源码 line 118-156) |
| native reasoning 通路存在 | 事实(RequestEnrichment line 79) |
| ContextWindowManager 前缀匹配可复用 | 事实(源码 line 29-73) |
| 高级模型 thinking 重复消耗 | 事实(native reasoning + Node A 5 步双轨,基于源码路径) |
| context 累积 / token 消耗 | 事实(instruction 注入 + Node B 独立调用,基于源码) |
| LLM 认知混淆 | 推断(基于 LLM 行为一般规律,未在该项目实测) |
| 上下文污染 | 推断(retry 独立小会话携带旧 output,但主历史已剥离,污染程度未实测) |
| 未知模型默认不逃逸为安全策略 | 工程判断 |
