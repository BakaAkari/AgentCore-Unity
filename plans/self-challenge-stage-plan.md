# Self-Challenge 分阶段实施计划

> **文档日期**: 2026-07-08(v1.4.9 骨架完成后)
> **状态**: Stage 1 已交付, 本文档为 Stage 2 蓝图 + Stage 3-8 大纲
> **上游依据**: [`prompt-layer-hallucination-hardening-plan.md`](prompt-layer-hallucination-hardening-plan.md) v0.10 定稿 + [`ROADMAP.md`](ROADMAP.md) §3.y Phase 9
> **本文档目的**: 把 ROADMAP Phase 9 的 13 项子任务拆成**可增量交付、可独立验收**的 Stage, 每个 Stage 有明确的输入/输出/验收点/回滚点

---

## 1. 阶段划分总览

| Stage | ROADMAP 任务 | 主题 | 预估 | 验收阻塞? |
|---|---|---|---|---|
| **Stage 1** ✅ | 骨架 | SelfChallengeConfig / Data / SkipRules + Settings 骨架 + v16→v17 迁移 + 22 单元测试 | 已交付(v1.4.9) | — |
| **Stage 2** | 9.1.1 (前半) | Node A prompt 注入 + 流式抽取器 + 结构校验 | 3 人日 | 可独立验收(仅 Node A 生效, Node B 仍 skip) |
| **Stage 3** | 9.1.1 (后半) + 9.1.9 | Correction retry(独立小会话) + fallback + 结构验证测试 | 1.5 人日 | 可独立验收 |
| **Stage 4** | 9.1.3 | WaitingForClarification 状态机 + 主对话历史清理(v0.10 §0.6) | 1.5 人日 | 可独立验收 |
| **Stage 5** | 9.1.5 | Continuation 模式(v0.9 §1.2.5) | 1.5 人日 | 依赖 Stage 4 |
| **Stage 6** | 9.1.2 | Node B (AnswerChallengeReviewer 独立 LLM 调用 + 抽取器 + Verdict) | 4 人日 | 依赖 Stage 2 |
| **Stage 7** | 9.1.4 | Node B REVISE draft 重生成 + v0.10 §0.4 单次不复审 | 1 人日 | 依赖 Stage 6 |
| **Stage 8** | 9.1.7 | UI: SelfChallengeCard + AssistantTurnView 集成 + Verdict 徽标 | 3 人日 | 依赖 Stage 6 |
| **Stage 9** | 9.1.6 + 9.1.8 | Domain Reload 兜底(v0.10 §0.5) + 主历史清理(v0.10 §0.6) | 1 人日 | 依赖 Stage 4/6 |
| **Stage 10** | 9.1.10 + 9.2.1 + 9.2.2 | 首周引导 + Statistics 面板 | 2 人日 | 收尾 |

**总量**: 约 19 人日(与 ROADMAP v0.10 §0.7 估算 17-20 人日一致)

**Stage 2-10 每阶段完结后**都可以做一次独立的 human-in-the-loop 验收, 阶段间**不会发生级联 API 破坏**因为 SelfChallengeData schema 在 Stage 1 已定型。

---

## 2. Stage 2 详细实施蓝图 (Node A prompt 注入 + 抽取 + 结构校验)

### 2.1 目标

**交付**: 用户消息进入 AgentLoop 后, 除非命中 R1/R3 skip, 否则在首次 LLM 调用前追加 Node A prompt; LLM 输出中的 `<intent_challenge>` 块被流式抽取并结构化解析; 校验通过则填充 [`SelfChallengeData`](../Editor/Core/SelfChallenge/SelfChallengeData.cs) 的 Node A 字段。

**不做**:
- correction retry (Stage 3)
- WaitingForClarification 状态机 (Stage 4)
- Continuation 模式 (Stage 5)
- Node B (Stage 6+)
- UI 卡片 (Stage 8)

**关键约束(设计文档立场)**:
- 工程侧**只做结构校验**, 语义完全交给 LLM
- Node A **不新增 LLM 调用**, 在首次调用的 messages 里追加 system-level instruction
- 每一轮用户 message 都触发(§1.2.1, v0.7 起, 不只是首次)

### 2.2 输入前提

- [x] Stage 1 骨架已交付(SelfChallengeConfig / Data / SkipRules 就位)
- [x] `SelfChallengeConfig` 已提升为 public (骨架修复 1)
- [ ] `AgentCoreSettings.intentChallengeEnabled` 需要在开发时手动设为 true 才能触发(骨架默认 false)

### 2.3 新增文件

#### 2.3.1 [`Editor/Core/SelfChallenge/IntentChallengePromptBuilder.cs`](../Editor/Core/SelfChallenge/IntentChallengePromptBuilder.cs)

**职责**: 生成 Node A 的 system-level instruction prompt, 严格按照 [`prompt-layer-hallucination-hardening-plan.md`](prompt-layer-hallucination-hardening-plan.md) §1.2.2 模板。

**接口**:
```csharp
public static class IntentChallengePromptBuilder
{
    /// <summary>
    /// 构造 Node A 完整模式 prompt (v0.9 §1.2.2)。返回追加在 user message 之后的 system-level instruction。
    /// </summary>
    public static string BuildFullNodeAInstruction();

    /// <summary>
    /// 未来 Stage 5 使用: Continuation 模式 prompt (v0.9 §1.2.5)。
    /// </summary>
    // public static string BuildContinuationInstruction(...);  // Stage 5
}
```

**实现要点**:
- prompt 文本硬编码为 `const string`, 与设计文档 §1.2.2 完全一致(Step 1-5 + `<consistency_correction>` 块)
- 使用 [`SelfChallengeConfig.NodeAOpenMarker`](../Editor/Core/SelfChallenge/SelfChallengeConfig.cs) / `NodeACloseMarker` / `ConsistencyCorrectionOpenMarker` / `ConsistencyCorrectionCloseMarker` 常量, **不允许硬编码字符串**
- 使用 [`SelfChallengeConfig.MinInterpretationCount`](../Editor/Core/SelfChallenge/SelfChallengeConfig.cs) / `MinInterpretationLength` 生成"至少 3 个 Interpretation, 每个至少 20 字符"的文本

**测试**:
- 单元测试: `IntentChallengePromptBuilderTests.BuildFullNodeAInstruction_ContainsAllMarkers` — 断言 prompt 里含所有 5 个 Step 标题、`<intent_challenge>` 开闭 marker、`<consistency_correction>` 开闭 marker、3 / 20 数字

#### 2.3.2 [`Editor/Core/SelfChallenge/IntentChallengeStreamExtractor.cs`](../Editor/Core/SelfChallenge/IntentChallengeStreamExtractor.cs)

**职责**: 流式抽取 assistant 输出里的 `<intent_challenge>...</intent_challenge>` 块。骨架**复用** [`VisiblePlanningTraceExtractor`](../Editor/Core/VisiblePlanningTraceExtractor.cs) 的状态机模式。

**关键差异**:
- VisiblePlanningTraceExtractor 用 `---THINKING---` / `---ACTION---` 两个 marker, 分离 reasoning 与 visible
- IntentChallengeStreamExtractor 用 `<intent_challenge>` / `</intent_challenge>` 一对配对 XML tag, **抽取块内容, 允许 tag 前后有其他 visible 文本**

**接口**:
```csharp
public class IntentChallengeStreamExtractor
{
    public IntentChallengeExtractorState State { get; }
    public string ExtractedBlock { get; }  // 完整的 <intent_challenge>...</intent_challenge> 原文(含 tag)

    public void Reset();
    public void RestoreState(IntentChallengeExtractorState state, string partialBlock);

    /// <summary>处理一个流式 token, 返回可对外可见的 visible delta(tag 内内容不 visible, tag 外内容 visible)。</summary>
    public IntentChallengeDelta Append(string token);

    /// <summary>非流式最终抽取, 用于历史消息处理。</summary>
    public static IntentChallengeFinalResult FinalizeContent(string rawContent);
}

public enum IntentChallengeExtractorState
{
    None,        // 尚未看到 opening tag
    Buffering,   // 看到 opening tag, 正在缓冲直到 closing tag
    Completed,   // 已抽取到完整块
    Invalid      // 结构异常(例如嵌套 tag / 未闭合)
}
```

**测试**:
- `IntentChallengeStreamExtractorTests`: 至少 15 个用例覆盖
  - 完整块流式接收(逐字符 / 逐段 / 一次性)
  - Tag 前后有 visible 文本
  - Tag 未闭合(输入结束时仍在 Buffering 状态)
  - 嵌套 tag(应视为 Invalid)
  - 多个 `<intent_challenge>` 块(只取第一个)
  - Reset / RestoreState 语义

#### 2.3.3 [`Editor/Core/SelfChallenge/IntentChallengeParser.cs`](../Editor/Core/SelfChallenge/IntentChallengeParser.cs)

**职责**: 对已完整抽取的 `<intent_challenge>` 块做**结构校验 + 语义字段填充**(注意: **只做结构校验**, 不做语义判断)。

**接口**:
```csharp
public static class IntentChallengeParser
{
    /// <summary>
    /// 解析并结构校验一段完整的 <intent_challenge>...</intent_challenge> 原文。
    /// </summary>
    /// <param name="rawBlock">含开闭 tag 的完整块</param>
    /// <param name="data">要填充的 SelfChallengeData(仅 Node A 相关字段)</param>
    /// <returns>校验结果; Success=true 时 data 已被填充; Success=false 时含 issues 列表</returns>
    public static IntentChallengeParseResult Parse(string rawBlock, SelfChallengeData data);
}

public readonly struct IntentChallengeParseResult
{
    public bool Success { get; }
    public IReadOnlyList<string> Issues { get; }   // 用于 correction retry 的 issue 列表
    public string CorrectionPromptSection { get; } // 用于 Stage 3 correction prompt 的 issue 段落

    public static IntentChallengeParseResult Ok();
    public static IntentChallengeParseResult Fail(IReadOnlyList<string> issues);
}
```

**结构校验清单**(严格对齐设计文档 §1.2.4):
| # | 校验项 | 失败 issue 文本模板 |
|---|---|---|
| S1 | 至少 3 个 `Interpretation N:` 行 | `Missing at least 3 substantive Interpretations (found {count}, minimum 3 required)` |
| S2 | 每个 Interpretation 内容 ≥ 20 字符 | `Interpretation {N} too short ({len} chars, minimum 20 required)` |
| S3 | 含 `Step 2:` 段落 | `Missing "Step 2: 找出歧义信号" section` |
| S4 | 含 `Step 3:` 且含 `关键假设:` 子段 | `Missing "Step 3: 选定工作解读" section or "关键假设:" subsection` |
| S5 | 含 `Step 4:` 且四个维度 A/B/C/D 均给出取值 | `Missing dimension {A/B/C/D} judgement in Step 4` |
| S6 | 含 `Step 5:` 段落 | `Missing "Step 5: Self-Consistency Check" section` |
| S7 | 含 `<consistency_correction>` 块 | `Missing <consistency_correction> block` |
| S8 | `<consistency_correction>` 内含 4 条 PASS/FAIL 判定 | `<consistency_correction> block missing PASS/FAIL judgements` |
| S9 | 最终结论存在(命中组合 X / 都不命中 / 或 [Consistent]) | `Missing final Step 4 conclusion in <consistency_correction>` |

**填充字段清单**:
- `data.NodeATriggered = true`
- `data.IsNodeAContinuation = false`
- `data.NodeAOutput = rawBlock`
- `data.Interpretations` = 抽取的 Interpretation 文本列表
- `data.AmbiguitySignals` = 抽取的歧义词列表
- `data.ChosenInterpretation` = Step 3 chosen 文本
- `data.KeyAssumptions` = Step 3 关键假设列表
- `data.Step4A/B/C/D` = 枚举值
- `data.InferredWords` = Step 4 D=inferred 时的推断词列表
- `data.Step4Conclusion` = Combo1 / Combo2 / DirectExecute
- `data.Step5Verdict` = Consistent / Corrected
- `data.Step5CorrectedJudgement` = 当 verdict=Corrected 时的文本
- `data.TriggeredClarification` = (Step4Conclusion != DirectExecute)

**注意**: 
- Regex 模式全部使用 `RegexOptions.Compiled` 静态字段
- **中文/英文文档头**都支持(Step 2 可能是"Step 2: 找出歧义信号"或"Step 2: Find Ambiguity Signals"; prompt 是中文, 但 LLM 可能因模型语言习惯输出英文)

**测试**:
- `IntentChallengeParserTests`: 覆盖每个 S1-S9 的 pass/fail 用例
- **Golden test**: 准备 3 份人工构造的 valid 完整块样本, 断言 Parse 后 SelfChallengeData 字段完整填充
- **Fuzz test**: 从 valid 样本删除任一 Step, 断言正确报告缺失 issue

#### 2.3.4 [`Editor/Tests/Core/SelfChallenge/IntentChallengePromptBuilderTests.cs`](../Editor/Tests/Core/SelfChallenge/IntentChallengePromptBuilderTests.cs)

见 2.3.1 测试要求。

#### 2.3.5 [`Editor/Tests/Core/SelfChallenge/IntentChallengeStreamExtractorTests.cs`](../Editor/Tests/Core/SelfChallenge/IntentChallengeStreamExtractorTests.cs)

见 2.3.2 测试要求。

#### 2.3.6 [`Editor/Tests/Core/SelfChallenge/IntentChallengeParserTests.cs`](../Editor/Tests/Core/SelfChallenge/IntentChallengeParserTests.cs)

见 2.3.3 测试要求。

### 2.4 修改文件

#### 2.4.1 [`Editor/Core/AgentLoop.LLM.cs`](../Editor/Core/AgentLoop.LLM.cs) — Node A prompt 注入点

**位置**: `CallLLMStreamAsync` 方法的开始(第 26-80 行), **在 `TrimToFit` 之前**追加 Node A instruction 到最后一条 user message。

**注入策略**:
```csharp
// 伪代码
if (settings.intentChallengeEnabled && !settings.legacySelfChallengeDisabled)
{
    var lastUserMessage = _messages.LastOrDefault(m => m.Role == ChatRole.User);
    if (lastUserMessage != null && ShouldTriggerNodeA(lastUserMessage))
    {
        // 检查 skip 规则
        if (SelfChallengeSkipRules.ShouldSkip(lastUserMessage.Content, out var skipReason))
        {
            // 记录 skip, 不注入 prompt
            RecordNodeASkip(assistantTurn, skipReason);
        }
        else
        {
            // 注入 Node A instruction
            var instruction = IntentChallengePromptBuilder.BuildFullNodeAInstruction();
            // 追加到 messagesSnapshot 里最后一条 user message 的 content 尾部
            // 或作为独立 system message 追加(需权衡, 见 2.5 决策点 D1)
        }
    }
}
```

**关键决策点 D1**: instruction 是**追加到 user message 尾部** 还是 **作为独立 system message**?
- **设计文档 §1.2.2 明示**: "在首次给 LLM 发送用户 message 的同一个请求里追加一段 system-level instruction"
- **实施建议**: 作为独立的 system message 追加, 位置**紧跟在** user message 之后; 这样保留了 user 原文, 便于 UI 展示且不污染消息历史
- **需在 Stage 2 编码时最终决定, 记入 ADR**

**关键决策点 D2**: `ShouldTriggerNodeA` 判定 — 除了 skip rules, 是否还有别的条件?
- **设计文档 §1.2.1 明示**: "每一条用户 message 都触发 Node A"
- 但 `WaitingForClarification` 状态下走 Continuation 模式(Stage 5 才做), **Stage 2 阶段不区分**, 一律走完整 Node A
- **需在 Stage 2 编码时明确 log 一次决策**

**Stream 处理**: 
- `CallLLMStreamAsync` 已有 `_visiblePlanningTraceExtractor.Reset()`; 新增 `_intentChallengeStreamExtractor.Reset()` 平行调用
- `OnStreamChunkReceived` 里对每个 content token, 先过 IntentChallengeStreamExtractor, 再过 VisiblePlanningTraceExtractor(**Node A 块不应算入 visible planning trace**)
- 完成后, 若 IntentChallengeStreamExtractor.State == Completed, 调用 IntentChallengeParser 并填充 data

**Node A 与 visible / reasoning / tool_call 冲突分析**:
- 如果 LLM 先输出 Node A 块, 再输出 `---THINKING---` 段, 再输出 tool_calls — 这三者需要**顺序处理, 互不干扰**
- 推荐处理顺序: **IntentChallengeStreamExtractor 优先** → 完成后 token 才交给 VisiblePlanningTraceExtractor
- **风险**: 如果 LLM 在 `<intent_challenge>` 块内部使用了 `---THINKING---` 字面量, 会被误抽取。设计文档 §1.2.2 规定 Node A prompt 里禁止其他 reasoning 输出, 因此这个风险**低但存在**, Stage 2 用一份 fixture 测试确认。

#### 2.4.2 [`Editor/Core/AgentLoop.cs`](../Editor/Core/AgentLoop.cs) 或 [`AgentLoop.Runner.cs`](../Editor/Core/AgentLoop.Runner.cs) — 私有字段声明

- 新增 `private readonly IntentChallengeStreamExtractor _intentChallengeStreamExtractor = new();`
- 若 AgentLoop 主构造在 [`AgentLoop.cs`](../Editor/Core/AgentLoop.cs), 在那里声明
- 若字段需要 domain reload 恢复, 在 [`AgentLoop.DomainReload.cs`](../Editor/Core/AgentLoop.DomainReload.cs) 补 restore 逻辑

#### 2.4.3 [`Editor/Session/SessionData.cs`](../Editor/Session/SessionData.cs) — 无需修改

Stage 1 已完成 `ConversationTurn.SelfChallenge` 字段挂载; Stage 2 直接 populate 即可, 无 schema 改动。

#### 2.4.4 [`Editor/Core/MessageTypes.cs`](../Editor/Core/MessageTypes.cs) — 触发 AgentEvent 发送

- Node A 抽取 + 结构校验成功后, 触发 `AgentEvent.IntentChallengeCompleted(data, turnId)`
- Skip 场景也触发 `IntentChallengeCompleted`(设计文档要求"无论 skip 还是完整执行都触发", 供 UI/Statistics 消费)

### 2.5 关键决策点(ADR-16 补充)

| ID | 决策项 | 建议 | 依据 |
|---|---|---|---|
| D1 | instruction 追加位置 | 独立 system message 追加, 紧跟 user message 后 | 设计文档 §1.2.2 "system-level instruction"; 保留原 user 消息完整性 |
| D2 | 触发时机 | 每轮 user message 都触发, Stage 2 不区分 WaitingForClarification | 设计文档 §1.2.1; Stage 4 才引入状态机 |
| D3 | Extractor 与 VisiblePlanningTraceExtractor 处理顺序 | IntentChallenge 优先 | Node A 块理论上永远在最前面 |
| D4 | Node A skip 时是否发 AgentEvent | 发, 但填充 data.NodeATriggered=false + NodeASkipReason | 设计文档 §3.2, UI 需要知道"这轮 skip 了" |
| D5 | 结构校验失败时的 Stage 2 处理 | 记录 issues 但**不 retry**(Stage 3 才做), 直接接受 assistant 消息 | Stage 2 只交付基础管线, retry 是 Stage 3 主题 |

### 2.6 验收标准(Stage 2 交付)

#### L0 编译健康
- [ ] 无编译错误
- [ ] `AgentCore.Editor` 程序集大小增量 < 30 KB

#### L1 单元测试
- [ ] `IntentChallengePromptBuilderTests` ≥ 3 用例, 全绿
- [ ] `IntentChallengeStreamExtractorTests` ≥ 15 用例, 全绿
- [ ] `IntentChallengeParserTests` ≥ 20 用例(每 S1-S9 至少 pass/fail 各 1), 全绿
- [ ] 骨架期的 `SelfChallengeSkipRulesTests` 22 用例仍全绿(回归)

#### L2 集成行为(Editor 内手工/自动)
- [ ] 打开 `AgentCoreSettings` → `intentChallengeEnabled = true`
- [ ] 发一条 20+ 字符 non-URL 消息(例: "帮我获取选中 GameObject 的所有 material 引用")
- [ ] 观察 Console 日志或 breakpoint: `IntentChallengePromptBuilder.BuildFullNodeAInstruction()` 被调用
- [ ] LLM 请求日志里能看到 messages 数组末尾追加了 Node A instruction
- [ ] LLM 返回后, `<intent_challenge>` 块被正确抽取
- [ ] `SelfChallengeData.NodeATriggered = true`, `NodeAOutput` 非空, `Interpretations.Count >= 3`
- [ ] 若 LLM 输出结构不完整, Console 有 Warning 列出具体 issues, 但不 retry(Stage 3 才做)
- [ ] session JSON 里现在能看到 `"self_challenge": {...}` 字段

#### L3 Skip 场景回归
- [ ] 发短消息(如"好"), 观察日志: `NodeATriggered = false`, `NodeASkipReason = "R1_short"`
- [ ] 发纯 URL(`https://example.com`), 观察日志: `NodeATriggered = false`, `NodeASkipReason = "R3_url"`
- [ ] session JSON 里 skip 场景的 turn 也应能看到 `self_challenge` 字段(但 NodeATriggered=false)

#### L4 负面验证
- [ ] `intentChallengeEnabled = false` 时, Node A prompt **不注入**, 行为完全等同 v1.4.9(通过日志/session JSON 验证)
- [ ] `legacySelfChallengeDisabled = true` 时, 即使 `intentChallengeEnabled = true` 也不注入
- [ ] 现有的 tool_call loop 行为不受影响; `RunToolCallLoopAsync` 循环刹车、连续失败检测等逻辑不变
- [ ] `VisiblePlanningTraceExtractor` 处理 `---THINKING---` 的行为不受污染

#### L5 v0.10 §0.6 前置(不阻塞 Stage 2 交付但需 Stage 9 完成)
- [ ] Node A 块**已经出现在** assistant message 的 content 里, 参与后续 LLM 请求 — **Stage 2 允许**这一临时行为
- [ ] Stage 9 会补齐"从主历史剥离 Node A 块"逻辑, 届时验收此项

### 2.7 回滚点

Stage 2 全部修改集中在:
- 新增 3 个源文件 + 3 个测试文件(不影响其他模块)
- [`AgentLoop.LLM.cs`](../Editor/Core/AgentLoop.LLM.cs) 少量插入(可用 feature flag 一键关闭)
- [`AgentLoop.cs`](../Editor/Core/AgentLoop.cs) 一个字段声明

**回滚策略**: 若上线后发现 Node A prompt 引起模型不稳定, 直接把 `intentChallengeEnabled` 默认改回 false + git revert Stage 2 commit 即可, **无 schema 破坏**。

### 2.8 已知遗留(转交后续 Stage)

| 遗留项 | 转交 Stage |
|---|---|
| Node A 结构校验失败 → correction retry | Stage 3 |
| Node A Step 4 结论 = 反问 → WaitingForClarification 状态 | Stage 4 |
| Node A Continuation 模式(WaitingForClarification 下用户回复) | Stage 5 |
| Node A 块从主对话历史剥离(v0.10 §0.6) | Stage 9 |
| Node A domain reload 中断(v0.10 §0.5) | Stage 9 |

---

## 3. Stage 3-10 大纲清单

### 3.1 Stage 3: Correction Retry + Fallback (ROADMAP 9.1.1 后半)

**触发**: Stage 2 的 `IntentChallengeParser` 返回 `Success=false`。

**核心机制(设计文档 §11.5 + v0.10 §0.1)**:
- 独立小会话: `[原始 user query, LLM 之前的错误输出, correction 指令]`
- 最多 `answerChallengeMaxRetries` 次(默认 2, 从 [`AgentCoreSettings`](../Editor/Config/AgentCoreSettings.cs) 读)
- Exhausted fallback: 接受"尽力解析"结果 + Statistics 记录 + 不 block 主任务

**v0.10 §0.1 关键决策**: 结构校验失败时**从主会话历史中回退, 不让残缺内容进主历史**; retry 独立小会话生效则把新 assistant message(clean 后)写入主历史; retry exhausted 则从主历史丢弃这轮的 assistant message, 直接接受 tool_calls(如有)按 clean 后写入。

**新增**:
- `NodeACorrectionRetryClient.cs` — 独立 LLM 调用, 组装 correction messages
- `NodeAFallbackHandler.cs` — exhausted 时的 fallback 逻辑

**修改**:
- [`AgentLoop.LLM.cs`](../Editor/Core/AgentLoop.LLM.cs) — 在 IntentChallengeParser 返回失败后调用 retry client
- `SelfChallengeData.NodeARetryCount` 字段 Stage 1 已就位, 直接填充

**测试**: retry 循环单元测试 + fallback 数据填充测试

### 3.2 Stage 4: WaitingForClarification 状态机 (ROADMAP 9.1.3)

**核心**:
- [`AgentState`](../Editor/Core/MessageTypes.cs) 新增 `WaitingForClarification` 枚举值
- Node A Step 4 结论 = 反问 → `SetState(AgentState.WaitingForClarification)`
- [`ToolCallDispatcher`](../Editor/Tools/ToolCallDispatcher.cs) 在此状态下**拒绝分发**任何 tool call
- [`SessionData`](../Editor/Session/SessionData.cs) / [`DomainReloadState`](../Editor/Core/DomainReloadState.cs) 序列化状态
- ChatWindow status bar 显示 "Agent is waiting for your clarification..."
- 输入框 auto-focus + placeholder 改为"请回答上方澄清问题..."(v0.9 §3.6.5)

**验收**: LLM 输出 `[CLARIFICATION NEEDED]` 反问后, tool 不会被执行; Domain reload 后状态保持; 用户回复后状态自动清除。

### 3.3 Stage 5: Continuation 模式 (ROADMAP 9.1.5)

**触发**: `WaitingForClarification` 状态下用户回复且未命中 R1/R3 skip。

**关键差异**(设计文档 §1.2.5):
- prompt marker: `<intent_challenge_continuation>` / `</intent_challenge_continuation>`(骨架已定义)
- 只做 Step 3-cont / 4-cont / 5-cont(省略 Step 1 Interpretation 重建, 引用上一轮 Node A 结果)
- 精简版 Consistency check 只做 3 条(不是 4 条)
- **边界情况 1**: LLM 输出 `[TOPIC CHANGE DETECTED]` → 降级为完整 Node A

**新增**:
- `IntentChallengeContinuationPromptBuilder.cs`
- `IntentChallengeContinuationParser.cs`(可复用 IntentChallengeParser 大部分逻辑, 通过 mode 参数区分)

**修改**:
- [`AgentLoop.LLM.cs`](../Editor/Core/AgentLoop.LLM.cs) — 判断是否进入 Continuation, 分派不同的 prompt builder
- `SelfChallengeData.IsNodeAContinuation` / `PreviousTurnNodeAId` 字段 Stage 1 已就位

**测试**: Continuation vs 完整 Node A 分支单元测试 + TOPIC CHANGE 降级测试

### 3.4 Stage 6: Node B (ROADMAP 9.1.2)

**核心**: 独立 LLM 调用做 reviewer role, 输出 `<answer_challenge>` 块。

**新增**:
- `AnswerChallengePromptBuilder.cs` — 组装 reviewer prompt(设计文档 §1.3.3)
- `AnswerChallengeReviewer.cs` — 独立 LLM 调用, 传入压缩后的完整对话历史 + draft + intent_challenge 块
- `AnswerChallengeStreamExtractor.cs` — 抽取 `<answer_challenge>...</answer_challenge>` 块
- `AnswerChallengeParser.cs` — 结构校验:
  - Step 1 至少 1 个 assumption verification
  - Step 2 至少 3 个 Counter-Example, 每个含至少 1 个 `<draft-quote>` tag
  - 每个 `<draft-quote>` ≥ 8 字符
  - 每个 `<draft-quote>` 内容作为 substring 存在于 draft
  - Step 3 完整
  - Step 4 verdict = PASS / REVISE / BLOCK 之一

**修改**:
- [`AgentLoop.Runner.cs`](../Editor/Core/AgentLoop.Runner.cs) — `HandleFinalResponse` 之前触发 Node B(如果启用且未 skip); skip 条件(设计文档 §1.3.1):
  - Response ≤ 50 字
  - Response 是纯问题(`[CLARIFICATION NEEDED]` 类)
- Verdict 处理分支:
  - PASS → draft 直通
  - REVISE → 重新生成 final response(Stage 7)
  - BLOCK → 回 tool loop 做 verification(**Stage 6 允许简化为 REVISE + log**, Stage 7 完善)

**关键约束**:
- Node B 是**额外 LLM 调用**, +1200~2000 token
- Node B **不再使用独立会话上下文**(v0.7 修正), 带**压缩后的完整对话历史 + 强角色扮演**
- v0.10 §0.6: 主历史里去除历史 `<intent_challenge>` 块避免累积

**测试**:
- `AnswerChallengeStreamExtractorTests` ≥ 12 用例
- `AnswerChallengeParserTests` ≥ 25 用例(重点覆盖 draft-quote substring 校验)
- Reviewer LLM 调用集成测试(mock LLM client)

### 3.5 Stage 7: REVISE draft 重生成 + v0.10 §0.4 单次不复审 (ROADMAP 9.1.4)

**核心**:
- Node B verdict = REVISE 时, 把 Reviewer 提出的 issues 作为 feedback 注入, 让 LLM 重新生成 final response
- 重新生成的 draft **不再过 Node B**(v0.10 §0.4 明确, 避免成本翻倍)
- `SelfChallengeData.DraftRegenerated = true` 供 UI 展示 `[~] REVISED` 徽标(Stage 8)

**修改**:
- [`AgentLoop.Runner.cs`](../Editor/Core/AgentLoop.Runner.cs) — REVISE 分支复用现有 tool_call_loop 机制的 CallLLMStreamAsync(不算新一轮 tool call round)
- 复用 Stage 6 的 skip 条件在 REVISE 之后**恒为 skip Node B**(v0.10 §0.4 单次不复审)

### 3.6 Stage 8: UI 层 SelfChallengeCard (ROADMAP 9.1.7)

**新增**:
- `Editor/UI/Components/SelfChallengeCard.cs` — 主组件, 复用 [`ToolCallCard`](../Editor/UI/Components/ToolCallCard.cs) 视觉语言
- Header: Verdict 徽标(`[v]` / `[~]` / `[!]` / `[?]` / `[.]`) + 简短摘要 + Copy 按钮 + 折叠箭头
- Body: Node A 4 Step + Node B 4 Step 两个子区域, 只读 TextField + ScrollView
- 事件监听 `IntentChallengeCompleted` / `AnswerChallengeCompleted` 更新卡片

**修改**:
- `AssistantTurnView.cs` — 新增 `SetSelfChallengeCard(card)` 方法, 挂载到 `_selfChallengeSlot`(位置在 ThinkingDrawer 和 ToolCallGroup 之间)
- [`ChatWindow.cs`](../Editor/UI/ChatWindow.cs) 与 `ChatWindow.Events.cs` — 事件订阅 + Domain Reload 后 `RebuildMessageBubbles` 里从 `SelfChallengeData` 重建卡片
- 自动折叠/展开策略(§3.5.5):
  - PASS → 折叠
  - REVISED / BLOCKED → 展开
  - `WaitingForClarification` → 展开
  - 用户手动切换 → 记住选择

**关键约束**:
- **不使用 emoji**(SDF 字体渲染为方块), 用 `[v]` / `[~]` / `[!]` / `[.]` / `[?]` ASCII
- Copy / ScrollView / TextField 事件冒泡 StopPropagation

### 3.7 Stage 9: Domain Reload 兜底 + 主历史清理 (v0.10 §0.5 + §0.6, ROADMAP 9.1.6 + 9.1.8)

**§0.5 domain reload 兜底**:
- Node B in-flight 时 domain reload → Node B 结果丢弃, draft 作为兜底写入主历史
- 复用现有 `_lastAssistantContent` 字段族, 不新增 InterruptPhase
- `SelfChallengeData.NodeBSkipReason = "domain_reload_interrupt"`

**§0.6 主历史清理**:
- assistant message 写入主对话历史前, **剥离** `<intent_challenge>` / `<intent_challenge_continuation>` / `<answer_challenge>` 块
- 完整 challenge 块只保留在 `SelfChallengeData`(供 UI + Statistics + SessionData 序列化)
- 复用现有 `PrepareAssistantMessageForHistory` 的"清洗后再写入历史"哲学
- 追责链: Node B 需引用 Node A 的关键假设时, `AnswerChallengePromptBuilder` 从 `SelfChallengeData` 读取, 而非主历史

**关键**: 此 Stage 是**回归验证密集期**, 需确保:
- 长对话(10+ 轮)Token 不无谓膨胀
- Session JSON 完整保留 challenge 块(UI 可重建)
- 主历史里不再有 challenge 块痕迹

### 3.8 Stage 10: 首周引导 + Statistics 面板 (ROADMAP 9.1.10 + 9.2.1 + 9.2.2)

**首周引导(§5.5)**:
- ChatWindow 首次打开 tooltip("请留意每条回复上方的自省卡片, 建议前 5~10 次对话时展开看看")
- `selfChallengeCardCountForcedExpansion` 字段(骨架已就位, 默认 5)驱动 SelfChallengeCard 强制展开
- README/CHANGELOG 补充"如何判断 Self-Challenge 是否生效"段落

**Statistics 面板(§11.6)**:
- 新增 `Editor/Core/SelfChallenge/SelfChallengeStatistics.cs` — ScriptableSingleton, 累计最近 200 次 self-challenge
- 3 个 Key Metrics: Node B PASS 占比 / 反问触发占比 / retry exhausted 占比
- Health badge 三态(OK / Warning / Failure)基于 §5.4 五项健康阈值
- 详细数据折叠区 + Export CSV / Clear All / Refresh
- 位置: `Editor/Config/Settings/Pages/UiDiagnosticsSettingsPage.cs` 新增卡片

---

## 4. 阶段间依赖图

```
Stage 1 (骨架) ✅
    ↓
Stage 2 (Node A prompt/抽取/结构校验)
    ↓                              ↘
Stage 3 (Correction retry)      Stage 6 (Node B)
    ↓                              ↓
Stage 4 (WaitingForClarification) Stage 7 (REVISE 重生成)
    ↓                              ↓
Stage 5 (Continuation)          Stage 8 (UI 卡片)
    ↓                              ↓
                Stage 9 (Domain reload + 主历史清理)
                    ↓
                Stage 10 (首周引导 + Statistics)
                    ↓
                v1.5.0-alpha1 发布
                    ↓
                4 周 kill criteria 实测窗口(v0.9 §5.4)
```

**并行机会**:
- Stage 2 完成后, Stage 3(retry) 与 Stage 6(Node B) 可并行
- Stage 8(UI) 只依赖 Stage 6 数据字段就绪, 不依赖 Stage 6 逻辑完全稳定

**串行硬约束**:
- Stage 4 → Stage 5(Continuation 依赖 WaitingForClarification 状态)
- Stage 6 → Stage 7 → Stage 8 是 Node B 完整链条
- Stage 9 应在**所有前置 Stage 完成后**做最终 wiring, 避免中途反复重写主历史清理逻辑

---

## 5. 阶段验收流程规范

**每个 Stage 交付后**执行以下**同一套验收流程**:

### 5.1 L0 编译健康(必过)
- Unity Console 零编译错误
- Package Manager 无 warning
- 程序集大小增量控制在合理范围

### 5.2 L1 单元测试(必过)
- 该 Stage 新增测试全绿
- **骨架期 22 个 SelfChallengeSkipRulesTests 用例始终全绿**(回归底线)
- 所有历史 SelfChallenge 相关测试全绿

### 5.3 L2 集成行为(必过)
- 该 Stage 的核心用户可感行为可在 Unity 编辑器中触发
- Console 有对应的日志/事件
- session JSON 有对应字段填充

### 5.4 L3 负面回归(必过)
- 关闭该 Stage 的 feature flag → 行为完全退化到上一 Stage
- 无副作用泄漏到其他模块

### 5.5 L4 阶段特有验证(视 Stage 而定)
详见每 Stage 的"验收标准"章节。

### 5.6 L5 Human-in-the-Loop 决策点
- 每 Stage 交付后 open a checkpoint, 由用户决定是否进入下一 Stage
- 若发现问题, 回滚该 Stage(每 Stage 都保证有独立 commit + feature flag)

---

## 6. Stage 2 编码前对齐清单(AGENTS.md §12.4 要求)

在开始 Stage 2 编码前**必须逐项确认**:

- [x] **分阶段交付方案**: Stage 2 明确边界(Node A prompt + 抽取 + 结构校验), 不含 retry / 状态机 / Continuation / Node B / UI
- [x] **每阶段版本号**: Stage 2 目标 v1.5.0-alpha1(与 Stage 3-5 打包发布); Stage 6+ 进入 v1.5.0-alpha2/3; v1.5.0 GA 需完成 Stage 10
- [x] **各阶段验收标准**: 见本文档 §2.6(Stage 2) + §5(通用)
- [ ] **首阶段 500 行代码上限拆分**: Stage 2 预估:
  - `IntentChallengePromptBuilder.cs` ~ 100 行(主要是常量 prompt 文本)
  - `IntentChallengeStreamExtractor.cs` ~ 200 行(复用 VisiblePlanningTraceExtractor 模式)
  - `IntentChallengeParser.cs` ~ 250 行(结构校验 + 字段填充)
  - 三个测试文件合计 ~ 400 行
  - [`AgentLoop.LLM.cs`](../Editor/Core/AgentLoop.LLM.cs) 修改 ~ 40 行
  - **总计 ~ 990 行代码**(其中 ~ 400 行测试, ~ 590 行生产代码)
  - **超过 500 行硬上限**, 建议**再拆一次**:
    - **Stage 2a**: PromptBuilder + StreamExtractor + 相应测试(~ 500 行)
    - **Stage 2b**: Parser + AgentLoop.LLM.cs 挂接 + 相应测试(~ 490 行)
  - 每 Sub-Stage 交付后单独 checkpoint

- [ ] **ADR 记录**: 决策点 D1-D5(见 §2.5)应在 Stage 2 编码前**用户确认**或**通过默认值实施 + 记录 ADR-17**

---

## 7. 风险登记与预防

| 风险 | 影响 | 概率 | 缓解 |
|---|---|---|---|
| Node A prompt 引起模型能力抖动(Qwen 3 VL 类) | 输出结构不合规导致 retry 频繁 | 中 | Stage 3 fallback 兜底; Statistics 面板监控; `intentChallengeEnabled` 默认 false 给用户选择 |
| IntentChallengeParser 中英文 header 兼容遗漏 | 结构校验误报 | 低 | Stage 2 golden fixture 覆盖中英文各 3 份样本 |
| VisiblePlanningTraceExtractor 与 IntentChallengeStreamExtractor 冲突 | reasoning/visible 内容错乱 | 低 | 处理顺序: IntentChallenge 优先; Stage 2 golden fixture 覆盖 THINKING 与 intent_challenge 混合场景 |
| Node A 每轮触发导致长对话 token 无谓膨胀 | Token 成本增加 | 高(Stage 9 前) | Stage 9 主历史清理机制上线前, `intentChallengeEnabled` 建议只在**短对话 / 前几轮** 打开 |
| Stage 顺序被压缩导致 Stage 9 主历史清理被遗漏 | 长对话 token 爆炸 | 中 | Stage 9 提前列入 checklist; Stage 6 交付时 Statistics 面板必须暴露"Node A 累计 token 占比"指标 |
| 用户在 Stage 6 前尝试通过手动启用 `answerChallengeEnabled` 触发 Node B | Runtime NRE 或异常 | 低 | Stage 2-5 期间, 若检测到 `answerChallengeEnabled=true` 但对应类未实现, 在 [`AgentCoreSettings.OnValidate`](../Editor/Config/AgentCoreSettings.cs) 强制关闭并 Debug.LogWarning |

---

## 8. 参考

- [`prompt-layer-hallucination-hardening-plan.md`](prompt-layer-hallucination-hardening-plan.md) — v0.10 定稿设计
- [`ROADMAP.md`](ROADMAP.md) §3.y Phase 9 + ADR-16
- [`../CHANGELOG.md`](../CHANGELOG.md) — v1.4.9 骨架交付记录
- [`../AGENTS.md`](../AGENTS.md) §12.4 — 编码前对齐清单
- [`../Editor/Core/SelfChallenge/`](../Editor/Core/SelfChallenge/) — Stage 1 骨架代码
- [`../Editor/Tests/Core/SelfChallengeSkipRulesTests.cs`](../Editor/Tests/Core/SelfChallengeSkipRulesTests.cs) — Stage 1 单元测试
- [`../Editor/Core/VisiblePlanningTraceExtractor.cs`](../Editor/Core/VisiblePlanningTraceExtractor.cs) — Stage 2 抽取器实现参考
- [`../Editor/Core/AgentLoop.LLM.cs`](../Editor/Core/AgentLoop.LLM.cs) — Stage 2 Node A 注入点
- [`../Editor/Core/AgentLoop.Runner.cs`](../Editor/Core/AgentLoop.Runner.cs) — Stage 6 Node B 注入点(`HandleFinalResponse`)
- [`../Editor/Config/AgentCoreSettings.cs`](../Editor/Config/AgentCoreSettings.cs) — Phase 9 骨架配置字段 + v16→v17 迁移
- [`../Editor/Session/SessionData.cs`](../Editor/Session/SessionData.cs) — SelfChallengeData 挂载点

---

> **本文档维护策略**: 每完成一个 Stage 更新一次, Stage 交付后把该 Stage 章节标记为 `[已交付 v1.5.0-alphaN, YYYY-MM-DD]` 并记录实际人日 vs 预估的偏差。若发现设计文档 v0.10 有需要修订的边界, 追加到 v0.11 或本文档的"文档遗留清理清单"。