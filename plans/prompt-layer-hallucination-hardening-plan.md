# Prompt Layer Hallucination Hardening Plan — v0.10 Codebase Alignment

> **状态**: v0.10 修订，对齐 v1.4.8 现有代码后可开工
>
> **v0.9 → v0.10 变更**：对照 [`AgentLoop.LLM.cs`](../Editor/Core/AgentLoop.LLM.cs) / [`AgentLoop.Runner.cs`](../Editor/Core/AgentLoop.Runner.cs) / [`DomainReloadState`](../Editor/Core/DomainReloadState.cs) / [`VisiblePlanningTraceExtractor`](../Editor/Core/VisiblePlanningTraceExtractor.cs) 交叉核对，收口 5 个必须在文档层解决的实施决策。不改核心机制，仅补齐工程边界。修订全部集中在下方 **§0 v0.10 收口决策**；其余章节保留 v0.9 内容不变，实施时以 §0 为准。
>
> **v0.7 → v0.8 变更**：审计 v0.7 发现 7 处 gap + 3 处冲突，v0.8 补齐（但引入回归风险）
>
> **v0.8 → v0.9 变更**：二次校验发现 **v0.8 在补 gap 时悄悄把关键词穷举逻辑塞了回来**（违反 v0.4 起明确的"不穷举"立场）。**v0.9 彻底清除这些回归**：
> 1. **Canary Probes 完全取消**（原 §11.7 整节删除）
>    - 原理由：早期检测 rubber-stamp
>    - 取消理由：P1/P2/P4 本质是关键词穷举（分词/破坏性动词/未完成关键词），违反核心立场；P5 fake correction 有反向污染风险；且**UI Verdict 徽标已提供首日观测能力**（Self-Challenge Card 全绿用户会自己怀疑）
>    - 替代：§5 补"首周引导条款"—— 用户可以直接观察 UI 判断
> 2. **Skip 白名单 R5 清理，只保留 R1（长度）+ R3（URL）**
>    - 原理由：识别"简单确认句"跳过 Node A
>    - 取消理由：确认词白名单本质是穷举
>    - 替代：短确认句由 R1（≤15 字符）自然覆盖；即使不 skip 也走 Continuation 模式，成本可接受
> 3. **顺带完成 G3/G5/G1/C3/G4 的小修正**（enum 替代 string / 独立小会话 retry / 弹性化压缩规则 / 补 Continuation 边界 / 补 AnswerChallengeRegenerated 事件）
>
> **v0.9 核心立场校准**：**工程侧只做三件事**——(1) 结构校验（block 存在、格式对不对）、(2) 状态管理（AgentState 流转）、(3) UI 呈现（Self-Challenge Card + Statistics）。**任何需要"识别用户意图 / 判断词语归属 / 分类动词性质"的逻辑都是语义判断，一律交给 LLM**。
>
> **v0.4 → v0.5 变更**：v1.5.0 包含用户可观测 UI（默认折叠）
>
> **v0.5 → v0.6 变更**：Node A Step 4 反转举证责任 + 工程侧一致性校验 + write 硬约束 + WaitingForClarification 状态
>
> **v0.6 → v0.7 变更（本次批判性审视后大改）**：v0.6 的**工程侧一致性校验（4 条正则/关键词规则）本质上是回到 v0.3 的穷举法**——违反了 v0.4 起就明确的"用户需求不可枚举"立场。v0.7 五项修正：
> 1. **Step 4 反问逻辑从"任一不满足即反问"改为"组合触发"** —— 大幅减少过度反问
>    - v0.6：4 个条件任一不满足即反问
>    - v0.7：**歧义 + 差异严重** 或 **破坏性 + 有推断** 才反问
> 2. **一致性校验从工程侧关键词改为 LLM 自我 check** —— 新增 Step 5，让 LLM 自己审视 Step 1-4 是否前后一致，输出 `<consistency_correction>` 块；工程侧只做**结构校验**（block 存在），不做**语义校验**（关键词匹配）
> 3. **Node B 带压缩后的完整对话历史** —— 取消 v0.6 的独立上下文选项；角色扮演靠 prompt 强化而非上下文隔离；避免 Reviewer 因缺少主对话 context 只能做语法层批评
> 4. **Node A 每轮 user message 都触发** —— v0.6 只在首轮；v0.7 覆盖用户追加需求的场景，只对短确认句（"是的"、"继续"、"好的"）skip
> 5. **Counter-Example 引用改为 `<draft-quote>` 结构化标记** —— 移除 v0.6 的 substring + 字符长度校验；工程侧只校验是否存在带 quote 标记的引用块，信息量由 LLM 自 review 或用户观察 UI 主观评价
>
> **v0.7 核心立场**：**能让 LLM 做的语义判断绝不放在工程侧**。工程侧只做三件事：结构校验（有没有输出该块）、状态管理（WaitingForClarification）、UI 呈现（Self-Challenge Card）。
> **创建**: 2026-07-08 (v1.4.8 后续)
> **优先级**: 高
>
> **修订记录（学习轨迹，不作为最终方案）**:
> - v0.1: 三层护栏 + "元认知能力弱"假设 → 被证否
> - v0.2: 引入 completeness 字段 + Tier 分层 + UNITY_TRAPS.md → 走错方向
> - v0.3: 五层护栏 + 语义工具清单 + Intent Guardian 关键词表 → **仍是穷举法，被用户否定**
> - **v0.4 (本文档)**: 抛弃所有穷举式解法，转向单一通用机制 —— **Self-Challenge**
>
> **本文档的核心立场**（用户明确要求）：
> - 用户需求**不可预测、不可标注**
> - 任何"提前枚举可能场景 / 编内置关键词表 / 建陷阱知识库"的方案**都是错的方向**
> - 唯一正确的路径是让 LLM 在**通用的两个节点**做真正的自我挑战

---

## 0. v0.10 收口决策（实施时以本节为准）

对照 v1.4.8 实际代码，v0.9 有 5 处工程边界文档未覆盖。本节以**最简方案**定案，不做架构重构。

### 0.1 Node A 输出的流式抽取（对齐 VisiblePlanningTrace 抽取器）

**问题**：Node A 在同一 LLM 请求内输出 `<intent_challenge>` 块，[`OnStreamChunkReceived()`](../Editor/Core/AgentLoop.LLM.cs:96) 会把 content token 实时渲染到 UI 气泡；不做抽取，challenge 块直接进入用户可见的消息。

**方案**：**复用 [`VisiblePlanningTraceExtractor`](../Editor/Core/VisiblePlanningTraceExtractor.cs) 的 marker 抽取模式**，新增 `IntentChallengeStreamExtractor`（同一份代码骨架，只换 marker）。marker 用 XML tag `<intent_challenge>` / `</intent_challenge>`（Node A）与 `<intent_challenge_continuation>` / `</intent_challenge_continuation>`（Continuation）。

**流式处理规则**（与 planning trace 对齐）：
- 未命中起始 marker 前，content token 按现有逻辑正常渲染到气泡
- 命中起始 marker 后进入 buffering，buffer 内容送到 SelfChallengeCard 的实时预览而不是气泡（复用 `ReasoningToken` 事件通道，`ReasoningSource` 新增枚举值 `IntentChallenge`）
- 命中结束 marker 后 buffer 完整移交给 `IntentChallengeParser` 做结构校验
- 主气泡最终显示的 `Content` = challenge 块**之外**的 assistant 内容（通常应为空；若非空说明模型违反了"完成 challenge 前禁止调工具"的 prompt 约束，见 §0.2）

**Node B 不需要流式抽取**：Node B 是独立 LLM 调用，draft 已生成、review 输出不进入用户气泡，直接完整解析。

**工作量修正**：新增约 **2 人日**（复用现有抽取器骨架，主要成本在事件管线接线和边界测试）。

### 0.2 challenge 块残缺 / 与 tool_calls 并存的处理

**问题**：模型可能在同一响应里输出残缺 challenge 块 + tool_calls；correction retry 用独立小会话重新生成 block 时，主会话已拿到的 tool_calls 如何处理未定义。

**方案**：**challenge 结构校验失败即丢弃本轮 tool_calls，进入独立 retry**。

具体规则：
1. 流式结束后先做结构校验。若结构失败：
   - 本轮 `assistantMessage.ToolCalls` **不写入 `_messages`**，也不 dispatch 到 [`ToolCallDispatcher`](../Editor/Tools/ToolCallDispatcher.cs)
   - 本轮 `assistantMessage.Content` 中的残缺 challenge 块**也不写入 `_messages`**（避免污染后续压缩 / 上下文）
   - 走 §11.5 的独立小会话 retry，最多 `answerChallengeMaxRetries` 次
2. Retry 成功 → 用 retry 产物的 challenge 块 + 决策结论覆盖本轮结果，主历史里补一条精简的 `ChatMessage.Assistant(challengeBlock)`（不含 tool_calls），然后：
   - Step 4 结论 = 直接执行 → 下一轮 LLM 调用会重新产生 tool_calls（Node A prompt 里的"完成 challenge 后开始调工具"由 LLM 自然接续；一次多花一轮 LLM 往返换清洁历史）
   - Step 4 结论 = 反问 → 进入 `WaitingForClarification`，正常流程
3. Retry 全部失败（exhausted）→ §11.5 fallback：接受 v0.9 定义的"尽力解析"结果 + Statistics 记录，主历史照常写入 assistant message（含 tool_calls），不阻塞用户任务

**理由**：v0.9 §11.5 已经把 retry 定为独立小会话，本决策只是把"主会话侧的清理规则"补齐——**结构失败时宁可重跑一轮，也不让残缺内容进主历史**。这与现有 `PrepareAssistantMessageForHistory` 的"清洗后再写入历史"哲学一致。

### 0.3 强制终止路径 skip Node B

**问题**：[`RunToolCallLoopAsync()`](../Editor/Core/AgentLoop.Runner.cs:31) 有 4 条强制终止路径（单工具连败 block / 全失败 block / 同目标重复 block / 轮次/Token 上限）都会走 `HandleFinalResponse`。若这些"被迫总结"触发 Node B 且 verdict = BLOCK（语义是"回 tool loop"）→ 死锁。

**方案**：**强制终止路径产生的 final response 无条件 skip Node B**。

具体实施：
- `HandleFinalResponse` 增加 `bool skipAnswerChallenge` 参数（默认 false）
- 4 条强制终止路径调用时传 `skipAnswerChallenge: true`
- Node B 触发判定第一条：`if (skipAnswerChallenge) return draft;`
- SelfChallengeData 里 `nodeBSkipReason = "forced_termination"`；Statistics 单独归类，不计入 PASS/REVISE/BLOCK 分布，避免污染 §5.4 健康阈值判定

**理由**：强制终止的总结**本来就不是模型的自然结论**，是工程侧硬压出来的降级答复，让 Reviewer 挑战它没有意义，而且 BLOCK verdict 与"已经强制退出"直接矛盾。这是护栏优先级问题，循环刹车（既有护栏）优先于 self-challenge（新护栏）。

### 0.4 REVISE 循环上限

**问题**：`answerChallengeMaxRetries` 只管 Node B 结构校验重试。REVISE verdict 触发 draft 重新生成后，新 draft 是否再过 Node B？v0.9 未定义。

**方案**：**REVISE 后重新生成的 draft 不再过 Node B，直接输出**。

具体实施：
- Node B 第一次 verdict = REVISE → 发送 `AnswerChallengeRegenerating` 事件，用 `reviseIssues` 作为 system feedback 重新调 LLM 生成新 draft
- 新 draft 生成完成 → 发送 `AnswerChallengeRegenerated` 事件，**直接走 `HandleFinalResponse` 输出给用户，不再进 Node B**
- SelfChallengeData 里 `draftRegenerated = true` 明确记录本次是"修正后输出"

**理由**：
- REVISE 循环本质是"模型审查模型自己刚改的东西"——一致性偏差最严重的场景，反复循环极可能永远 REVISE 或永远 PASS，没有收敛保证
- 一次 REVISE 已经把 Reviewer 提出的问题作为 feedback 强制注入，改写后的 draft 已经比原始 draft 严格；再审查 ROI 极低但成本翻倍
- UI 层 Verdict 徽标显示 `[~] REVISED`（v0.9 §3.5.3 已有）已经足够向用户说明"本轮 draft 被修正过"
- 用户可通过后续对话继续追问，这是自然的 human-in-the-loop 兜底

**BLOCK verdict 不受影响**：BLOCK 语义是"必须先做验证性 tool call"，回到 tool loop 后新一轮的 final response 会再过 Node B（那是一个新的 draft，不是同一个 draft 的修正）。

### 0.5 Node B 在飞时的 Domain Reload

**问题**：[`InterruptPhase`](../Editor/Core/DomainReloadState.cs:11) 只有 Streaming/ExecutingTool/WaitingCompilation。Node B reviewer 调用中发生 domain reload（draft 已生成、review 未完成）无恢复策略。

**方案**：**Node B in-flight 时的 domain reload 直接放行原始 draft，不做恢复**。

具体实施：
- **不新增 InterruptPhase**（避免为一个低频路径引入枚举 + 状态机分支）
- Node B 开始前 draft 已经完整生成（v0.9 §1.3.2 明确设计），domain reload 前 draft 已在内存中即将被消费
- 在 `AnswerChallengeReviewer.ReviewAsync` 内部，把当前 draft 作为**兜底数据**写入 [`DomainReloadState`](../Editor/Core/DomainReloadState.cs)（复用现有 `_lastAssistantContent` 字段族即可，不新增字段）
- Reload 恢复时若 `_lastAssistantContent` 是完整 assistant 文本且未包含 challenge block → 视为"Reviewer 未完成"，直接调用 `HandleFinalResponse(draft, skipAnswerChallenge: true)` 输出
- SelfChallengeData 里 `nodeBSkipReason = "domain_reload_interrupt"`

**理由**：
- Node B 是**额外一次 LLM 调用**（v0.9 §4.2 明确），本质是可选的加固层。丢失一次 Review 对最终 draft 内容**没有损坏**（draft 已经完整生成），只是失去了本轮的额外挑战
- 重跑 Review 的替代方案需要新增 InterruptPhase + `DomainReloadState` 字段 + 状态机分支 + `TryResumeAfterReload` 分支 + UI 恢复逻辑，工作量约 2 人日，收益是"低频场景下多做一次挑战"——**性价比不成立**
- Domain reload 本身对用户可见（Chat 窗口会显示恢复过程），用户看到 Verdict 徽标显示 `interrupted` 状态即可理解

### 0.6 主历史里旧 `<intent_challenge>` 块的清理

**问题**：v0.9 §11.1 只规定 Node B 压缩时丢弃旧 `<answer_challenge>`，但主会话历史里**每轮都会累积 `<intent_challenge>` 块**（v0.7 起 Node A 每轮触发）。10 轮对话后主历史里有 10 个 challenge 块，全部参与后续 LLM 请求，token 无谓膨胀。

**方案**：**在 [`AgentLoop.Sanitization.cs`](../Editor/Core/AgentLoop.Sanitization.cs) 新增清理规则：写入 `_messages` 时剥离 challenge 块**，只保留本轮 assistant 的实际内容。

具体实施：
- `PrepareAssistantMessageForHistory` 已经在做类似清洗（剥离 visible planning trace），扩展为同时剥离 `<intent_challenge>...</intent_challenge>` 和 `<intent_challenge_continuation>...</intent_challenge_continuation>`
- 完整 challenge 块**只保留在** `SelfChallengeData`（供 UI 卡片渲染 + Statistics）和 SessionData 序列化，**不进入** LLM 上下文历史
- 追责链不受影响：Node B 需要引用 Node A 关键假设时，`AnswerChallengeReviewer.BuildReviewerMessages` 从 `SelfChallengeData` 读取 challenge 块拼接到 reviewer prompt（v0.9 §11.1 已经这样设计，本决策只是明确"主历史不带、reviewer prompt 从旁路带"）

**理由**：与现有清洗哲学一致（planning trace 也是"UI 层保留 / LLM 历史不带"），无新概念。

### 0.7 v0.10 工作量重估

| 项目 | v0.9 估算 | v0.10 修正 |
|------|----------|-----------|
| 核心机制（Parser / Reviewer / Prompt） | 6~8 人日 | 6~8 人日（不变） |
| 强制反问用户 + WaitingForClarification | 2 人日 | 2 人日（不变） |
| 用户可观测 UI（SelfChallengeCard） | 3~4 人日 | 3~4 人日（不变） |
| **§0.1 流式抽取（新增）** | 未估 | **+2 人日** |
| **§0.2 主历史清理规则（新增）** | 未估 | **+0.5 人日** |
| **§0.3 强制终止 skip Node B（新增）** | 未估 | **+0.3 人日** |
| **§0.4 REVISE 单次不复审（新增）** | 未估 | **+0 人日**（本来就是简化） |
| **§0.5 domain reload 放行 draft（新增）** | 未估 | **+0.5 人日**（仅补 skip 分支） |
| **§0.6 主历史清理（新增）** | 未估 | **+0.5 人日** |
| Statistics 面板 | 2~3 人日 | 2~3 人日（不变） |
| **合计** | **14~16 人日** | **17~20 人日** |

比 v0.9 增加约 3~4 人日，代价换来 5 个边界决策清晰、可直接编码。

### 0.8 v0.10 文档遗留清理清单（低优）

以下 v0.9 内容与本次修订不一致，实施时按此清单对齐：

- **§4.1** "Node A 只在首次调用触发"改为"每轮触发"（v0.7 起决策，v0.9 §4.1 漏改）
- **§11.2** `MessageTurn` 类不存在，改为 [`ConversationTurn`](../Editor/Core/MessageTypes.cs:600) / `SerializableConversationTurn`；`selfChallenge` 字段按现有 `SerializableConversationTurn` 序列化模式添加
- **§11.8 清单**：删除"Re-run canary probes 按钮"（§11.7 已取消）；SkipRules 从 R1-R5 改为 R1+R3（v0.9 §1.2.1 已取消 R2/R4/R5）
- **§11.2 `SelfChallengeData` 注释**里的 `nodeASkipReason` 值列表从 5 条缩为 2 条：`"R1_short"` / `"R3_url"` / `null`；新增 `"forced_termination"` / `"domain_reload_interrupt"`（§0.3 / §0.5）
- **§3.4 配置项**：`answerChallengeMaxRetries` 语义明确为"Node B 结构校验重试上限"，不包含 REVISE 重生成次数（REVISE 固定 1 次，§0.4）

---

## 0-legacy. v0.9 核心洞察与假设（保留供追溯，不作为实施依据）

> 以下小节编号 0.1 / 0.2 / 0.3 属于 v0.9 遗留，与 §0 中的 v0.10 收口决策**编号无关**。实施时请以 §0 为准，本节仅用于理解 v0.4→v0.9 立场演化。

### 0-legacy.1 从 v0.3 学到的两件事（不能忘的教训）

**教训 1：幻觉的触发点是"看似完整的部分答案"**
- 工具返回结构漂亮 + 字段齐全 → LLM 认为答案正确
- 但语义粒度不匹配用户提问的语义粒度
- 用户提问的**真实语义空间**是**开放、多样、不可枚举的**

**教训 2：LLM 有能力读懂 SOUL、能事后引用 §1 归因，但决策时不 actively invoke**
- 参见图 3 的 LLM 自省——它能精确指出"违反了 §1.1 / §1.2"
- 但这是被用户追问后才做的
- 说明 self-review 能力**存在**，只是**没有在正确的时机自动触发**

### 0-legacy.2 v0.4 的唯一策略

**在两个明确的时间点，工程侧强制注入 self-challenge，激活 LLM 已有但被动的元认知能力。**

- **节点 A**：**读取用户需求时** —— 在开始规划 tool call 之前
- **节点 B**：**输出最终答案前** —— 在没有 tool call 的 assistant final response 之前

**关键点**：
- **没有穷举**：不预测用户会问什么、不预测什么工具输出会不完整、不预测什么 Unity 陷阱
- **通用机制**：任何领域、任何模型、任何 query 都统一处理
- **只在两个节点触发**：不是每轮都问，避免 token 浪费和干扰有效推理
- **依赖 LLM 已有能力**：图 3 证明 LLM 有 self-review 能力，只是被动

### 0-legacy.3 关键设计难题（本方案的核心工程挑战）

**难题 1：如何避免 self-challenge 沦为 rubber-stamp？**

如果 challenge prompt 只是问"你的回答对吗？"，LLM 大概率会说"对"——这是 alignment tax 里最经典的 sycophancy 表现。

**必须解决**：让 LLM 真的进入"批判性审视"模式，而不是"确认自己正确"模式。

**难题 2：如何让 self-challenge 输出可验证？**

如果 LLM 输出"我 review 过了，没问题"这种模糊回答，工程侧根本没法判断它是真 review 还是 rubber-stamp。

**必须解决**：challenge 输出必须是**结构化的、可解析的、包含反例的**。

**难题 3：如何让 self-challenge 只运行必要次数？**

节点 A（读需求时）如果每轮都触发，会拖累前沿模型的正常速度。
节点 B（输出前）如果每次输出都触发，token 成本 +30~50%。

**必须解决**：识别"什么样的 query 需要 A"、"什么样的 draft response 需要 B"，其他直通。

---

## 1. Self-Challenge 机制核心设计

### 1.1 两个节点的定位

```
用户 message
    ↓
┌─ Node A: Intent Self-Challenge (读需求时) ───────────┐
│ 强制 LLM 挑战自己对用户需求的第一层理解              │
│ 输出结构化"我理解的诉求"，比对多种解读               │
└─────────────────────────────────────────────────────┘
    ↓
Tool Call Loop (正常执行 - 循环刹车等既有护栏继续)
    ↓
LLM 生成 final response draft
    ↓
┌─ Node B: Answer Self-Challenge (输出前) ─────────────┐
│ 强制 LLM 扮演对抗性 reviewer 审视自己的 draft        │
│ 输出结构化"发现的问题"，如需修正则重新生成           │
└─────────────────────────────────────────────────────┘
    ↓
最终回复给用户
```

### 1.2 节点 A：Intent Self-Challenge（读需求时）

#### 1.2.1 触发条件（v0.8 零 gap 版：完整 Skip 规则 + 语义边界澄清）

**每一条用户 message 都触发 Node A**（v0.6 只在首轮触发，v0.7 起修正为覆盖多轮追加需求）。

**理由**：用户在多轮对话中经常追加新需求（例如 Turn 2 说"再看一下 collider"、Turn 3 说"另外把 rigidbody 也 dump 一下"）。这些追加需求同样可能有语义歧义。

##### Skip 判定规则（v0.9 精简版，纯格式识别，零语义判断）

**v0.9 核心立场**：Skip 判定**只做纯格式识别**，不涉及任何语义/词义/意图分类。v0.8 曾包含的 R2 代码块 / R4 堆栈跟踪 / R5 确认词白名单**全部取消**（它们含关键词穷举成分）。

具体 Skip 规则（**只有 2 条**）：

| # | 规则 | 判定方法 | 备注 |
|---|------|---------|------|
| R1 | 消息**去除所有空白后 ≤ 15 个 Unicode 字符** | `msg.Trim().Where(c => !char.IsWhiteSpace(c)).Count() <= 15` | 中英文一视同仁；覆盖"好的/是的/继续"等短确认句 |
| R3 | 消息是**纯 URL** | 匹配 `^\s*https?://\S+\s*$` | 单个 URL |

**任一规则命中即 Skip**。**没有例外情况**：即使处于 `WaitingForClarification` 状态，只要满足 R1/R3 也 skip（用户回复"是的"→ R1 命中 → skip → 直接进入 tool loop，因为 self-challenge 上一轮已经问过了）。

**v0.8 曾有的规则为何 v0.9 取消**：

- **R2（纯代码块）**：判断"消息是不是纯代码块"需要判断"这个代码块之外还有没有意图相关内容"——涉及语义分析。**取消后**：粘贴代码块的消息仍走完整 Node A，Interpretations 会由 LLM 自己决定是分析代码还是执行代码，符合"让 LLM 做语义判断"立场
- **R4（堆栈跟踪）**：`Exception` / `at [A-Z]\w+\.` 是关键词穷举，且不覆盖 Rust panic / Python traceback。**取消后**：粘贴错误 log 的消息仍走完整 Node A
- **R5（确认词白名单）**：`是的`/`好`/`OK` 等等永远列不完，本质关键词穷举。**取消后**：用户回复"是的"如果 ≤15 字符会被 R1 覆盖；超过 15 字符走完整 Node A，成本可接受

##### 冲突 1 解决方案：v0.9 承认"Skip 判定就是简化的语义边界"，通过极简化规避争议

**v0.8 曾试图**用"轻度启发式 vs 语义判断"的术语区分规避悖论——**v0.9 承认这是文字游戏**。真实立场应该是：

- **Skip 判定不可避免地有一点语义边界**（"消息看起来像不需要挑战的内容"这个判断本身就是弱语义）
- 但 v0.9 通过**极简化规则**让这个边界最小化：**只做 2 条纯格式规则**（长度 / URL）
- 更复杂的 skip 判断（"用户是不是在追加新需求"、"用户是不是在澄清"、"用户是不是在跳话题"）**全部交给 Node A / Continuation 模式让 LLM 判断**

**核心原则**：**当无法在纯格式与弱语义之间划清线时，选择极简化而非扩充规则**。这是 v0.9 与 v0.8 的根本立场差异。

##### WaitingForClarification 状态下的特殊处理

处于 `WaitingForClarification` 状态时用户回复的消息，如果不被 R1/R3 skip，则走 Node A **Continuation 模式**（不是完整 Node A）。详见 §1.2.5。

#### 1.2.2 注入位置

在**首次给 LLM 发送用户 message 的同一个请求里**追加一段 system-level instruction（不是新增一轮 LLM 调用，避免翻倍成本）：

```
用户消息：{原始 user message}

[SYSTEM INSTRUCTION — 你必须按以下顺序输出，全部完成后才能开始调用工具]

<intent_challenge>
在开始规划任何工具调用前，你必须先完成一次"需求自我挑战"。禁止跳过、禁止简写。

## Step 1: 提出至少 3 种对用户需求的**不同解读**

列出用户可能想问的不同事情。每种解读必须**结构上有差异**，不能是同一件事的不同措辞。

Interpretation 1: [具体、可操作]
Interpretation 2: [不同粒度 / 不同数量 / 不同范围]
Interpretation 3: [如果用户提问模糊，最"错"的解读会是什么？]

## Step 2: 找出**歧义信号**

用户提问中哪些词是模糊的？（举例：数量词、指代词、量词、隐含单/复数、时间范围、空间范围）
- 歧义词 1: "{词}" — 可能指 A 或 B
- 歧义词 2: ...

## Step 3: 选定工作解读

我选定的解读是：Interpretation X
选择理由：{基于什么假设 / 上下文 / 之前的对话}
关键假设：{如果这个假设错了，答案会完全不同的 1~2 个点}

## Step 4: 澄清决策（v0.7 组合触发，减少过度反问）

**判断以下四个维度，每个必须明确回答**：

- 维度 A（歧义）: Step 2 里是否列出了歧义词？
  - `A=yes`（有歧义） / `A=no`（零歧义）
- 维度 B（差异严重度）: Interpretation 3（最"错"解读）与 chosen 解读的**行为差异**是否严重？
  - 严重定义：会造成不可逆修改、误导后续多轮 tool call、或用户会需要重新操作
  - `B=severe`（差异严重） / `B=minor`（差异不严重）
- 维度 C（破坏性）: 我要执行的操作是否**破坏性**？
  - **破坏性**：删除 / 覆盖 / 覆写 / 批量修改现有 / 无法撤销的操作
  - **非破坏性**：新建 / 追加 / 只读查询 / 可撤销的修改
  - **由你根据 chosen interpretation 自行判断**（不是工程侧穷举动词列表）
  - `C=destructive` / `C=safe`
- 维度 D（推断词）: chosen interpretation 里的**关键名词/动词/形容词**是否都来自用户 query？
  - 不来自 query（是你推断出来的）的词汇，**明确列出**
  - `D=inferred` + 推断的词列表 / `D=verbatim`（零推断）

**反问触发条件（组合逻辑，v0.7 关键放宽）**：

出现以下任一组合**才**反问用户，其他情况直接执行：

- **组合 1**：`A=yes` **且** `B=severe`（有歧义 且 差异严重）
- **组合 2**：`C=destructive` **且** `D=inferred`（破坏性 且 有推断）
- 其他情况：直接执行

**放宽后的具体效果对比**：

| 场景 | v0.6 决策 | v0.7 决策 |
|------|----------|----------|
| 用户："查看当前所有相机"（歧义: "相机"，差异: 主 vs 全部，非破坏） | 反问（条件 A 不满足） | **直接执行**（因为 B=severe？不一定，A=yes 但 B 需要判断）|
| 用户："新建一个 Player GameObject"（非破坏, 零歧义, 零推断） | 反问（条件 C 不满足，因为是 write） | **直接执行**（C=safe，不触发反问）|
| 用户："删除老资源"（破坏 + "老"是推断） | 反问（条件 C+D 不满足） | **反问**（组合 2 命中） |
| 用户："删除 Assets/xxx.fbx"（破坏, 零歧义, 零推断） | 反问（条件 C 不满足） | **直接执行**（C=destructive 但 D=verbatim，组合 2 不命中） |

**明确写出结论**：

- 我的判断: A={yes/no}, B={severe/minor}, C={destructive/safe}, D={inferred/verbatim}
  - 如果 D=inferred，推断的词是: [列表]
- 触发反问？
  - [ ] 命中组合 1（A=yes 且 B=severe）
  - [ ] 命中组合 2（C=destructive 且 D=inferred）
  - [ ] 都不命中，直接执行

**如果决定反问，输出格式**：

```
[CLARIFICATION NEEDED]
我理解你想 {chosen interpretation 简述}，但存在以下需要你确认的地方：

{根据命中组合说明具体原因：组合 1 → 说清歧义与差异；组合 2 → 说清破坏性 + 推断词}

请确认你想要的是：
1. {Interpretation 1 简述}
2. {Interpretation 2 简述}
3. {Interpretation 3 简述}（或"其他，请直接说明"）
```

**注意**：反问 message 是本轮唯一输出，**不能再调用任何工具**。等用户回复后进入下一轮。

## Step 5: Self-Consistency Check（v0.7 新增，替代 v0.6 工程侧关键词校验）

在完成 Step 1-4 之后，你必须**回头审视自己刚写的输出**，验证以下四条自一致性：

- **一致性 1**（歧义 vs A）：你在 Step 2 是否列出了歧义词？如果有，A 应该 = yes；如果没有，A 应该 = no。你在 Step 4 的 A 判断是否与此一致？
- **一致性 2**（破坏性 vs C）：读你的 chosen interpretation，它描述的操作在语义上是否属于"删除/覆盖/覆写/批量修改现有"？如果是，C 应该 = destructive。你在 Step 4 的 C 判断是否与此一致？
- **一致性 3**（推断词 vs D）：读你的 chosen interpretation，其中的**关键名词/动词/形容词是否都能在用户原始 query 里找到**？如果不能，D 应该 = inferred 且必须列出推断的词。你在 Step 4 的 D 判断是否与此一致？
- **一致性 4**（结论）：根据你自己的 A/B/C/D 判断，套用组合 1/组合 2 逻辑，Step 4 的结论是否正确？

**输出格式**（无论是否有不一致都必须输出这个块）：

```
<consistency_correction>
Consistency check:
- 一致性 1: [PASS / FAIL — 具体不一致点]
- 一致性 2: [PASS / FAIL — 具体不一致点]
- 一致性 3: [PASS / FAIL — 具体不一致点]
- 一致性 4: [PASS / FAIL — 具体不一致点]

如果有 FAIL:
Corrected judgement:
- A={yes/no}, B={severe/minor}, C={destructive/safe}, D={inferred/verbatim}
- 新的 Step 4 结论: [命中组合 X / 都不命中，直接执行]

如果全部 PASS:
[Consistent]
</consistency_correction>
```

**为什么把一致性校验交给你（LLM）而不是工程侧**：

- 工程侧用关键词/正则做语义判断本质上是穷举法，无法覆盖用户表达的多样性
- 你（LLM）有语义理解能力，判断"chosen 里的词是否来自 query"、"操作是否破坏性"比工程侧更准确
- 你的自校验被工程侧解析后作为**最终决策依据**——如果 Step 5 输出 corrected judgement，工程侧以 corrected 为准

**禁止**：
- 跳过 Step 5（Step 1-4 输出但没有 `<consistency_correction>` 块 → 工程侧结构校验失败 → correction retry）
- 在 corrected judgement 里逃避（例如原本 A=yes 修正为 A=no 但没解释理由）
</intent_challenge>

完成上述 5 个 Step 后：
- 若 Step 5 修正后的结论是"都不命中，直接执行"→ 开始调工具
- 若 Step 5 修正后的结论是"命中组合 X"→ 直接输出 `[CLARIFICATION NEEDED]` 反问，禁止调工具
```

#### 1.2.3 关键设计要点

**要点 1：强制"至少 3 种解读"**
- 这个约束是防止 rubber-stamp 的核心
- LLM 不能只写 "Interpretation 1: 用户想问 X" 就跳过
- 必须写出至少 3 种**结构上有差异**的解读——**这个格式约束本身激活了批判性思维**

**要点 2：显式识别"歧义信号"**
- 强制 LLM 说出用户 query 中的哪些词是模糊的
- 如果用户 query 里没有歧义词（例如"删除 Assets/Models/Player.fbx"）—— LLM 会写 "无歧义信号"—— 这时它可以直接进入 Interpretation 单一解读并快速通过
- 如果 query 含歧义词——LLM **被迫直面**它

**要点 3：Step 3 的"关键假设"是防错锚点**
- 逼 LLM 把默认假设**显式化**
- 一旦假设显式化，工具返回结果时更容易识别"假设不成立"信号

**要点 4：Step 4 反转举证责任（v0.6 关键强化）**
- 图 1 的悲剧根源之一是 LLM **从不问用户澄清**
- v0.5 的 Step 4 只给了 LLM"三选一"的选项 —— LLM 大概率因 helpfulness bias 选"用户提问明确"，绕过反问
- **v0.6 反转举证责任**：默认必须反问；只有 LLM 能证明"4 个条件全部满足"才可以直接执行
- 每个条件必须显式回答，不能跳过（工程侧会校验）
- **write 类硬约束**：条件 C 独立成一条 —— 任何涉及修改文件、GameObject、Assets、PlayerSettings 的操作都必须反问，除非 query 里的关键词零推断（条件 D 也满足）

#### 1.2.4 输出可验证性（v0.7 大幅精简 — 工程侧只做结构校验）

**v0.7 立场重大转变**：v0.6 的"两轮 sanity check（结构 + 4 条关键词一致性）"里，第二轮本质是**工程侧穷举关键词判断语义** —— 违反了 v0.4 起的"不做穷举"立场。v0.7 彻底摒弃工程侧语义校验，**一致性判断完全交给 LLM**（Step 5 已完成这个工作）。

**工程侧只做一件事：结构校验**

解析 `<intent_challenge>` 块，检查以下**结构性**要求（不涉及任何语义关键词匹配）：

- 是否包含至少 3 个 `Interpretation N:` 行？
- 每个 Interpretation 是否至少 20 个字符（防止 "同上" / "略" 之类偷懒）？
- 是否包含 `Step 2: 找出歧义信号` 段落？
- 是否包含 `Step 3: 选定工作解读` 且含 `关键假设:` 子段落？
- 是否包含 `Step 4: 澄清决策` 且四个维度（A/B/C/D）均给出取值？
- 是否包含 `Step 5:` 段落？
- 是否包含 `<consistency_correction>` 块，且含 `Consistency check:` 头部与 4 条 PASS/FAIL 判定？
- 是否包含最终结论（Step 5 修正后的结论：命中组合 X / 都不命中）？

**语义判断（v0.7 全部由 LLM 自己完成）**：

- 歧义词是否存在 → **Step 2 由 LLM 列出，Step 4 A 由 LLM 判断，Step 5 一致性 1 由 LLM 交叉验证**（工程侧不参与）
- 操作是否破坏性 → **Step 4 C 由 LLM 判断，Step 5 一致性 2 由 LLM 交叉验证**（工程侧不参与）
- 关键词是否推断 → **Step 4 D 由 LLM 判断并列出推断词，Step 5 一致性 3 由 LLM 交叉验证**（工程侧不参与）
- 结论正确性 → **Step 5 一致性 4 由 LLM 交叉验证**（工程侧不参与）

**工程侧的最终决策依据**：`<consistency_correction>` 块里的 **corrected judgement 或 `[Consistent]` 标记**。

- 若输出 `[Consistent]` → 以 Step 4 原结论为准
- 若输出 `Corrected judgement` → 以 corrected 为准（LLM 自己发现 Step 1-4 里有不一致并修正）

**结构校验失败时的 correction retry**：

```
[SYSTEM] Your intent_challenge block failed structural validation:
- Missing at least 3 substantive Interpretations (each must be >= 20 chars)
- Missing Step 5 or `<consistency_correction>` block
- Step 5 does not include all 4 PASS/FAIL judgements

Redo the intent_challenge, ensuring all 5 Steps + `<consistency_correction>` block are present.
Do not skip any Step and do not add prose outside these structured sections.
```

**为什么 v0.7 转向 LLM 自校验**：

1. **v0.6 的 4 条关键词一致性规则悄悄回到穷举** —— write 类动词列表（写/修/删/创/write/modify/delete/...）永远列不完；推断词判断依赖分词，中文更难；违反了"不做穷举"立场
2. **LLM 的语义能力比工程侧正则强得多** —— 判断"chosen 里的词是否来自 query"、"操作是否破坏性"，LLM 天然做得比工程侧准
3. **保留追责链和验证性** —— LLM 输出 `<consistency_correction>` 块**明确写出**它的一致性判断，用户/后续 Node B 都能看到 LLM 是否真做了自检还是敷衍
4. **符合 v0.4 立场** —— 工程侧只做**结构规范**（有没有输出该块、格式对不对），语义完全由 LLM 承担

**保留的对抗 rubber-stamp 手段**：

- **结构约束**（≥3 个 Interpretation / ≥20 字符 / 5 个 Step 完整 / `<consistency_correction>` 必须存在）—— 通过格式约束逼 LLM 真正产生内容
- **Step 5 自校验**（LLM 必须输出 4 条 PASS/FAIL 判定 + corrected judgement 或 `[Consistent]`）—— LLM 无法只写 "OK" 糊弄
- **Node B 层的独立视角审查**（见 §1.3）—— 主对话之外还有一次挑战
- **Statistics 面板 rubber-stamp 空转检测**（见 §5）—— 事后诊断
- **`<consistency_correction>` UI 可见性**（Self-Challenge Card 展示，见 §3.5）—— 用户能直观看到 LLM 是否真的做了自检

#### 1.2.5 Continuation 模式（v0.8 新增，冲突 3 解决）

**触发条件**：Agent 处于 `WaitingForClarification` 状态时收到用户回复。

**核心差异**：不做完整 5 Step Node A，而是**接续上一轮 Node A**的 Interpretation 列表，只做**新增的 3 个 Step**（Step 3-5 的精简版），显著降低 token 成本与延迟。

##### Continuation Prompt 模板

```
用户上一轮的原始 message: {previousUserMessage}
Agent 上一轮的 Node A 输出（Interpretations + 歧义信号 + Step 4 判断）:
<previous_intent_challenge>
{完整 previous <intent_challenge> block}
</previous_intent_challenge>

Agent 上一轮的反问 message: {previousClarificationMessage}

用户本轮的回复: {currentUserMessage}

[SYSTEM INSTRUCTION — Continuation 模式，仅需要完成以下 3 个精简 Step，禁止重做 Step 1/2]

<intent_challenge_continuation>

## Step 3-cont: 根据用户回复更新 chosen interpretation

用户回复解决了上一轮的哪些歧义？（明确对应到上一轮 <previous_intent_challenge> 里的 Step 2 歧义词）
- 上一轮歧义词 1: "{词}" → 用户回复中的解答: "{引用}" → 现在明确为: {具体含义}
- ...

更新后的 chosen interpretation: {具体、可操作}
更新后的关键假设: {新的假设，如果这个假设错答案会完全不同}

## Step 4-cont: 澄清决策（v0.7 组合触发逻辑，不变）

根据更新后的 chosen 判断维度 A/B/C/D，套用组合逻辑：
- A: yes/no
- B: severe/minor
- C: destructive/safe
- D: inferred/verbatim（引用用户**本轮回复 + 上一轮 message**里的关键词判断）

结论:
- [ ] 命中组合 1（A=yes 且 B=severe）→ 继续反问
- [ ] 命中组合 2（C=destructive 且 D=inferred）→ 继续反问
- [ ] 都不命中 → 开始调工具

## Step 5-cont: Self-Consistency Check（精简版，仅校验 Step 3-cont/4-cont）

<consistency_correction>
- 一致性 1（用户回复是否真的解决了 Step 2 里的歧义？）: PASS / FAIL
- 一致性 2（chosen 是否与用户回复一致？）: PASS / FAIL
- 一致性 3（D 判断是否与用户本轮+上一轮 message 里的实际词汇一致？）: PASS / FAIL

如果有 FAIL: Corrected judgement
如果全部 PASS: [Consistent]
</consistency_correction>

</intent_challenge_continuation>
```

##### Continuation 与完整 Node A 的差异对比

| 维度 | 完整 Node A（首次或新话题） | Continuation（澄清回复） |
|------|--------------------------|------------------------|
| Step 1 (Interpretations) | 至少 3 种，每个 ≥20 字符 | **省略** |
| Step 2 (歧义信号) | 完整识别 | **省略**（引用上一轮的 Step 2） |
| Step 3 (Chosen) | 独立选定 | 基于用户回复**更新** |
| Step 4 (澄清决策) | 完整 4 维度判断 | 相同 4 维度判断（组合逻辑不变） |
| Step 5 (自校验) | 4 条 PASS/FAIL | 3 条 PASS/FAIL（少一条一致性 1'） |
| Token 成本 | 800~1300 additional | **约 400~700 additional**（省 40%） |
| XML tag | `<intent_challenge>` | `<intent_challenge_continuation>` |

##### 判断"是否为 Continuation 模式"的工程侧规则

工程侧根据以下条件之一判定进入 Continuation：

1. Agent 状态是 `WaitingForClarification`（**主要判据**）
2. 上一轮 Session 里有 `<intent_challenge>` 块且 Step 4 结论是"反问"

若这两个条件都不成立 → 走完整 Node A。

##### Continuation 判定的边界情况

**边界情况 1**：用户在 `WaitingForClarification` 状态下发送**完全无关的新话题**（例如 Agent 问"是删除时间还是引用？"，用户回："算了，先看看当前场景有哪些相机"）

**处理**：
- 工程侧无法**语义判断**用户回复是否与上一轮反问相关（这是语义判断）
- 让 LLM 在 Continuation Step 3-cont 里自己判断：如果用户回复**与上一轮 Interpretations 完全无关**，Step 3-cont 里明确输出 `[TOPIC CHANGE DETECTED]`，然后**降级为完整 Node A**
- 工程侧检测到 `[TOPIC CHANGE DETECTED]` 标记后，**清空 WaitingForClarification 状态，重新发起完整 Node A**（不算 Continuation retry，计入正常触发）

**边界情况 2**：Continuation 输出后 Step 4-cont 结论仍是"反问" → 再次进入 `WaitingForClarification`，下轮再走 Continuation

**边界情况 3**：Continuation 结构校验失败 → correction retry（同完整 Node A 的处理，见 §1.2.6）

### 1.3 节点 B：Answer Self-Challenge（输出前）

**v0.7 关键修正**：Node B 不再使用独立会话上下文（`useIsolatedReviewerContext=true` 被移除）。改为**带压缩后的完整对话历史 + prompt 强角色扮演**。理由：独立上下文让 Reviewer 缺少主对话建立的 context（用户之前的偏好、历史决策），只能做**语法层批评**（"count 缺失"），做不了**语义层批评**（"这个项目所有材质都在 Prefab 里，你怎么知道就 1 个？"）。

**风险：认知一致性偏差** —— Reviewer 拿到主历史后可能倾向于"这是我说的"。**对策**：不通过上下文隔离，通过 **prompt 强角色扮演**（见 §1.3.3）—— 明确要求 LLM 扮演"从不相信 Agent 的第三方 skeptical reviewer"。

#### 1.3.1 触发条件

**默认对所有 non-tool-call assistant final response 触发**，但有以下 skip 条件：

- Response 长度 **≤50 字**（"好的"、"完成了"、"下一步做什么？" 这类简短回复）
- Response 是纯问题（Agent 反过来问用户）—— `[CLARIFICATION NEEDED]` 类消息不做 Node B（它本来就不是最终答复）
- 上一轮已经触发过 Node B 且用户明确接受（比如用户说"这样就行"）

Skip 判定放在 `AgentLoop.Runner.cs` 的 final response 处理段。

#### 1.3.2 注入位置

**这里必须做一次额外的 LLM 调用**，因为 LLM 已经生成了 draft response —— 需要给它 draft + 用户原始 query + 相关 tool results，让它扮演 reviewer 重新审视。

伪代码：

```csharp
// AgentLoop.Runner.cs - final response 处理段
if (isNonToolCallFinalResponse && !shouldSkipAnswerChallenge(draft))
{
    var draftResponse = draft;

    // 组装 reviewer prompt (见 §1.3.3)
    var reviewMessages = BuildReviewerMessages(
        userQuery: originalUserQuery,
        toolResultsUsed: recentToolResults,
        draftAnswer: draftResponse,
        intentChallengeBlock: nodeAOutput  // 引用 Node A 里的"关键假设"
    );

    var reviewResult = await llm.ChatAsync(reviewMessages);
    // reviewResult 是结构化的 <answer_challenge> 块（见 §1.3.3）

    // 解析 reviewResult，判断是否需要重新生成
    var challenge = ParseAnswerChallenge(reviewResult);
    if (challenge.hasCriticalIssues)
    {
        // 用发现的问题作为反馈，让 LLM 重新生成 final response
        return RegenerateFinalResponse(draft, challenge.criticalIssues);
    }
    else
    {
        return draftResponse;  // draft 通过审查，直接返回
    }
}
```

#### 1.3.3 Reviewer Prompt 模板

这是**整个方案里最关键的一段 prompt** —— 它必须让 LLM 真的进入"对抗性 reviewer"心态，而不是 rubber-stamp。

设计原则（每一条都是防 rubber-stamp 的）：

1. **不问"对不对"，而是"找问题"** —— 明确任务是找错
2. **强制角色扮演** —— 让 LLM 扮演一个"从不相信 Agent 的挑剔审查员"
3. **强制列出反例** —— 至少列出 3 个"如果这样，答案就错"的场景
4. **绑定 Node A 的关键假设** —— 逐一对照假设是否被 tool result 验证
5. **禁止说"看起来没问题"** —— 输出必须是具体、可指向 draft 某一处的

模板：

```
<user_query>
{原始用户 query}
</user_query>

<my_intent_challenge>
{Node A 里输出的完整 intent_challenge 块，尤其是"关键假设"部分}
</my_intent_challenge>

<tool_results_used>
{recent tool call ID + summary，让 reviewer 知道 draft 基于哪些证据}
</tool_results_used>

<draft_answer>
{LLM 刚生成的 draft final response}
</draft_answer>

---

[SYSTEM INSTRUCTION — 你现在的角色是 skeptical reviewer，任务不是确认 draft 正确，而是找出至少 3 个可能被质疑的地方]

## 角色

你是一个 skeptical reviewer。你**默认认为 <draft_answer> 里有错误**，你的任务是找出这些错误。**从不假设 draft 是对的**。你**不是 Agent 本人**，你是一个第三方审查员，被雇来找 Agent 的问题。

## 强制输出格式（禁止跳过、禁止简写）

<answer_challenge>

## Step 1: Assumption Verification

对照 <my_intent_challenge> 里 Step 3 声明的"关键假设"，逐一核对：

- 假设 1: {原文引用} → 在 <tool_results_used> 中的证据是: {引用具体 tool call ID + 数据} → 假设**是 / 否**成立
- 假设 2: ...

如果**任一假设**没有在 tool_results_used 里被明确验证，标记为 **UNVERIFIED**。

## Step 2: Counter-Examples (至少 3 个)（v0.7 使用结构化 `<draft-quote>` 标记）

假设 <draft_answer> 是错的。**在什么情况下它会错**？至少给出 3 个具体场景。

**每个 Counter-Example 必须包含至少一个 `<draft-quote>...</draft-quote>` 标记**，标记里是**从 draft 里逐字复制**的原文引用（工程侧会校验 quote 内容确实存在于 draft）：

- Counter-Example 1: 如果 {某个具体条件}，那么 draft 里说的 <draft-quote>逐字复制的原文</draft-quote> 就是错的，因为 {原因}
- Counter-Example 2: 如果 {某个具体条件}，那么 draft 里说的 <draft-quote>另一处原文</draft-quote> 就是错的，因为 {原因}
- Counter-Example 3: 如果 {某个具体条件}，那么 draft 里说的 <draft-quote>另一处原文</draft-quote> 就是错的，因为 {原因}

**禁止**：
- 用 "如果数据不准确" / "如果我理解错了" / "如果参数错了" 这类**通用无信息的假设**
- 引用**通用单词**（如 `<draft-quote>material</draft-quote>` / `<draft-quote>the</draft-quote>` / `<draft-quote>Unity</draft-quote>` 等 <8 个字符的短通用词）
- 引用**不指向具体断言**的内容（只引用小标题、只引用列表项编号）

**引用长度要求**：`<draft-quote>` 内容应至少包含一个**完整语义单元**（例如一句话、一个字段值、一处具体结论），信息量由你自己判断——工程侧只做长度下限和 substring 匹配校验，语义充分性靠你的自 review。

## Step 3: Completeness Check

用户 query 里问的每一件事，draft 都覆盖了吗？

- User 问的 Part 1: "{引用 query 一部分}" → draft 覆盖情况: **完整 / 部分 / 未提及**
- User 问的 Part 2: ...

## Step 4: Verdict

在完成 Step 1~3 之后，做出结论：

- [ ] **PASS**：所有假设已验证，无 counter-example 站得住脚，全部完整覆盖。draft 可以发送。
- [ ] **REVISE**：发现至少一处需要修正。列出必须修正的问题：
    - Issue 1: {具体问题}，修正方向: {具体建议}
    - ...
- [ ] **BLOCK**：发现关键假设未验证，必须先做验证性 tool call，不能直接回复用户。列出需要做的验证：
    - Verification needed 1: {具体做什么}
    - ...

</answer_challenge>

**禁止**：
- 写"draft 看起来没问题" / "已经足够完整" 这类模糊评价
- 跳过任何 Step
- Counter-Example 用"如果错了"这种不指向 draft 具体一句话的说法
- Verdict 不做选择（必须勾选 PASS/REVISE/BLOCK 其中一个）
```

#### 1.3.4 输出可验证性（v0.7 精简为纯结构校验）

**v0.7 立场**：与 §1.2.4 相同——工程侧只做结构校验，语义评估交给 LLM 和用户 UI 观察。

工程侧解析 `<answer_challenge>` 块，做以下**结构性**校验：

- Step 1 是否至少列出了 1 个 assumption verification？
- Step 2 是否至少 3 个 Counter-Example，且每个都包含**至少一个 `<draft-quote>...</draft-quote>` 标记**？
- 每个 `<draft-quote>` 内容是否 **≥8 个字符**（防止只引用通用短词）？
- 每个 `<draft-quote>` 内容是否作为 substring **确实存在于 draft**（工程侧简单字符串包含性检查）？
- Step 3 是否完整？
- Step 4 是否勾选了 PASS/REVISE/BLOCK 中的一个？

**信息量判断**（"Counter-Example 是不是言之有物"）—— **不由工程侧判断**，由：
- LLM 自己（reviewer 角色里的自 review 循环 —— 不过这里 LLM 是同一个模型，不完美但比工程侧关键词强）
- 用户观察 UI（Self-Challenge Card 展示，用户能直接看到 Counter-Example 是不是言之有物）
- 空转检测（§5 Statistics 面板显示 Verdict 分布）

任一结构校验失败 → correction retry 最多 2 次；2 次都失败 → 默认 REVISE 并强制 LLM 重新生成 draft。

#### 1.3.5 三种 Verdict 的处理

- **PASS**: draft 直通用户
- **REVISE**: 把 challenge 的 "Issues to fix" 作为 system feedback 注入，让 LLM **重新生成** final response（复用现有 tool_call_loop 机制，不算新一轮 tool call round）
- **BLOCK**: 把 "Verification needed" 转化为一个 forced tool call 建议，让 LLM 继续 tool loop（回到 tool_call_loop 里）

---

## 2. 对抗 Rubber-Stamp 的深度设计

### 2.1 五道防线

设计 self-challenge 最难的地方在于 **让 LLM 真的进入 skeptical mode，而不是 rubber-stamp**。以下五道防线协同工作：

#### 防线 1: 角色扮演隔离

Reviewer prompt 里显式说 "你**不是** Agent 本人，你是一个第三方审查员"。研究表明角色分离能显著降低 sycophancy（"Sycophancy in LLMs"，2024 年多个 arxiv 论文都验证过；但我不能引用具体链接，我没有网络查询）。

**风险坦承**：某些强对齐模型（尤其是 Claude 系列）即使角色扮演也倾向于 conservative——可能过度触发 REVISE。这是可接受的偏保守，比 rubber-stamp 好。

#### 防线 2: 强制格式约束

- 至少 3 个 Interpretation / Counter-Example
- 每项 ≥20 字符
- 必须**引用**（quote）draft 或 query 的具体字符串

**关键**：格式约束不是形式主义，它**逼 LLM 真的产生批判性内容**。如果没有格式约束，LLM 会写 "counter-example 1: 数据可能不对" 这种无信息量的话。

#### 防线 3: 工程侧结构校验

工程层解析 `<intent_challenge>` 和 `<answer_challenge>` 块，做结构 check：
- Interpretation 数量、长度、diversity 检查
- Counter-Example 是否**引用**了 draft 的字符串（工程侧 substring 匹配）
- Verdict 是否勾选

不通过 → correction retry。

#### 防线 3.5: LLM 自我一致性校验（v0.7 重构 —— v0.6 的工程侧关键词一致性被彻底移除）

**v0.6 曾经的做法**（**已废弃**）：工程侧用 4 条关键词/正则规则做一致性校验（歧义词一致性 / write 类动词一致性 / 推断词一致性 / 结论一致性）。

**为什么 v0.7 废弃**：
- 关键词/正则本质上是穷举（write 类动词永远列不完；推断词判断依赖分词，中文更难）
- 违反 v0.4 起明确的"用户需求不可枚举"立场
- 语义判断本来就是 LLM 的强项，不该外包给工程侧正则

**v0.7 做法**：**Step 5 让 LLM 自己做一致性校验**。LLM 输出 `<consistency_correction>` 块，含 4 条 PASS/FAIL 判定 + corrected judgement 或 `[Consistent]` 标记。工程侧只校验**这个块存在且格式对**，不做任何语义分析。

**这是新的护栏形式**：不再是"工程侧发现矛盾并 retry"，而是"**LLM 自己发现矛盾并修正**"。工程侧的作用是**逼 LLM 必须完成这个自检**（Step 5 缺失 → 结构校验失败 → correction retry）。

**为什么这仍然有效**：
- 强制的结构位置（Step 5 必须存在）逼 LLM 走完这个思考流程
- 4 条 PASS/FAIL 判定的强制格式约束逼 LLM 输出具体判断而不是笼统"OK"
- corrected judgement 强制格式让 LLM 无法只写 `[Consistent]` 糊弄（如果它写 `[Consistent]` 但用户/UI 检查后发现明显矛盾，通过 Statistics rubber-stamp 检测暴露）

**关于 Qwen 3 VL 类模型的坦诚**：Step 5 依赖模型有自我一致性判断能力。**如果模型完全没有这种能力**，Step 5 会退化为 rubber-stamp（LLM 全部写 PASS）。此时防线 5（空转检测）会通过统计发现异常。这是**已知的模型能力上限**，v0.7 通过"用户可观测 UI + Statistics"让上限暴露，而不是靠更多工程侧关键词兜底。

#### 防线 4: 假设显式化 + 追责

Node A 强制 LLM 输出"关键假设"，Node B 强制 reviewer 引用 Node A 的关键假设逐一 verify。这形成**问责链**：LLM 在 Node A 说过的假设，Node B 必须交代它是不是被验证了。

#### 防线 5: 空转检测（可选，v1.6.0+）

工程侧记录 Node B 的 Verdict 分布：
- 如果某个模型 Verdict 分布是 100% PASS —— 大概率在 rubber-stamp
- 如果分布合理（例如 60% PASS / 30% REVISE / 10% BLOCK）—— 正常工作
- 如果分布过度保守（100% BLOCK）—— 过度触发

这类统计能在 Settings 面板可见，供用户诊断护栏是否真的生效。

### 2.2 关于"LLM 会不会串通自己"的诚实思考（v0.7 立场调整）

**风险**：Node A 里 LLM 挑了 Interpretation 2 并声明"关键假设 X"，Node B 里作为 reviewer 又回来看，它**认识到自己是同一个 LLM**，可能倾向于确认自己 Node A 的选择正确。

**v0.7 缓解方式**：
1. **Reviewer prompt 明确要求"第三方视角"** —— 用角色扮演对抗认知一致性偏差
2. **Reviewer prompt 明确要求"默认认为 draft 有错"** —— 直接翻转默认立场
3. **Node B 带压缩后的完整对话历史**（v0.7 修正，见 §1.3）—— 让 Reviewer 有主对话的 context 做语义层批评，而不是只做语法层批评

**v0.7 立场重要变化**：v0.6 曾经建议 "Node B 用独立的 messages 上下文" —— 这个选项 v0.7 **已移除**。原因：独立上下文让 Reviewer 缺少主对话建立的 context（用户之前的偏好、之前几轮的决策），只能做**语法层批评**（"count 缺失"），做不了**语义层批评**（"这个项目所有材质都在 Prefab 里，你怎么知道就 1 个？"）。**用 prompt 强角色扮演对抗一致性偏差，而不是靠上下文隔离**。

**风险 residual**：即使有以上防线，同一个模型的 Node B 仍会比"另一个模型"更宽容。这是**基础限制**，需要接受。**但比无 self-challenge 一定好**。真正的外部视角需要用**独立小模型作为 Reviewer**（v1.7.0+ 可选，见 §7.3），v1.5.0 不做。

### 2.3 未来优化路径（本方案不做，仅记录）

- 用**不同模型**做 Node B（例如小模型做 Node A、大模型做 Node B，或反过来）—— 引入真正的"外部视角"
- 让**用户可以随时打开 Node B 输出**审查 Agent 的 self-review —— 增加透明度和信任

---

## 3. 与现有 AgentLoop 的集成

### 3.1 影响的文件

预计只涉及 3~4 个文件的修改：

- **[`AgentLoop.LLM.cs`](../Editor/Core/AgentLoop.LLM.cs)**：Node A 的注入点（在首次给 LLM 发用户消息前追加 `<intent_challenge>` 指令）
- **[`AgentLoop.Runner.cs`](../Editor/Core/AgentLoop.Runner.cs)**：Node B 的注入点（final response 前触发 reviewer 调用 + 处理 Verdict）
- **新建 `Editor/Core/SelfChallenge/`**：
  - `IntentChallengeParser.cs` —— 解析 `<intent_challenge>` 块，做结构校验；不通过则生成 correction prompt
  - `AnswerChallengeReviewer.cs` —— 组装 reviewer messages 调用 LLM，解析 `<answer_challenge>` 块
  - `AnswerChallengeParser.cs` —— 解析 `<answer_challenge>` 块，做 counter-example 引用校验
  - `SelfChallengeConfig.cs` —— 配置项（是否启用、skip 条件、correction retry 次数）
- **[`AgentCoreSettings`](../Editor/Config/AgentCoreSettings.cs)**：新增开关配置字段
- **[`ChatWindow.cs`](../Editor/UI/ChatWindow.cs)** 与 [`ChatWindow.Events.cs`](../Editor/UI/ChatWindow.Events.cs)：UI 层展示 self-challenge 过程（可选，用户能看到 Agent 的自省，增强信任）

### 3.2 事件流

新增两个 AgentEvent：

- `IntentChallengeCompleted`：Node A 完成，携带解析后的 interpretations / 关键假设 / 用户是否需要澄清
- `AnswerChallengeCompleted`：Node B 完成，携带 Verdict / issues / verifications

`ChatWindow` 可以监听这两个事件，在 UI 里展示"Agent 是如何思考的"折叠区域（默认折叠，展开可看完整 self-challenge 过程），提高透明度并让用户诊断问题。

### 3.3 与既有护栏的关系

**保留的既有护栏**（v0.4 不改动）：
- [`AgentLoop.Runner.cs`](../Editor/Core/AgentLoop.Runner.cs:51) 的循环刹车（warning=4/block=7 → 建议调至 warning=2/block=4）
- [`CompilationWatcher`](../Editor/Core/CompilationWatcher.cs) 编译错误捕获
- Domain Reload 恢复机制

**取消的既有方案（v0.3 中提过但不做）**：
- ❌ 语义工具清单（analyze_material_setup 等 13 个）
- ❌ Intent Guardian 关键词表
- ❌ UNITY_TRAPS.md 陷阱知识库
- ❌ `completeness` 字段规范
- ❌ LLM Capability Tier 分层
- ❌ Task Decomposition Pre-Injection（被 Node A 替代）
- ❌ Mini-Reflection Trigger（被 Node B 替代）

**已经做且不冲突的**（保留）：
- ToolResponse 顶层结构规范化（success/action/target/data/error/warnings/next_hints）—— 但**没有 completeness 字段**
- 路径规范统一（PathNormalizer）—— 这不是穷举，是通用的输入规范化
- 消除歧义占位符（`(Generic)` → 结构化 omitted 标记）—— 这不是穷举，是通用的序列化规范

### 3.4 配置项

新增 [`AgentCoreSettings`](../Editor/Config/AgentCoreSettings.cs) 字段：

```csharp
[Header("Self-Challenge")]
[Tooltip("启用 Node A（读需求时的自我挑战）")]
public bool intentChallengeEnabled = true;

[Tooltip("启用 Node B（输出前的自我挑战）")]
public bool answerChallengeEnabled = true;

[Tooltip("Node B 结构校验失败后的 correction retry 上限")]
[Range(0, 3)]
public int answerChallengeMaxRetries = 2;

[Tooltip("允许 Agent 在 Node A 判定需求模糊时主动向用户提问澄清")]
public bool allowAgentClarificationQuestions = true;

[Tooltip("Legacy Mode: 完全禁用 self-challenge，回到 v1.4.8 行为（用于 A/B 对比）")]
public bool legacySelfChallengeDisabled = false;
```

**v0.7 移除的配置项**：`useIsolatedReviewerContext` —— v0.6 曾提供"Node B 用独立会话上下文"选项，v0.7 已废弃（见 §1.3 与 §2.2）。Node B **始终**带压缩后的主对话历史。

**默认**：两个 Node 都开启、`allowAgentClarificationQuestions=true`（允许 Agent 主动问澄清）、`answerChallengeMaxRetries=2`。

### 3.5 用户可观测 UI 设计（v0.5 v1.5.0 强制包含）

#### 3.5.1 设计原则

用户明确要求 v1.5.0 就要有可观测 UI，**默认折叠**。设计遵循以下原则：

1. **默认零打扰**：卡片默认折叠，只显示一行摘要，不侵占聊天流的视野
2. **异常自动展开**：Verdict 不是 PASS（即 REVISE / BLOCK）时自动展开——因为这些情况下 Agent 的行为被修正过，用户应该看到为什么
3. **视觉与 ToolCallGroup 一致**：复用 v1.4.8 建立的卡片视觉语言（深色背景 / 左边框强调 / 折叠箭头 / 复制按钮）
4. **可复制**：整段 self-challenge 文本可以选择、可以 Ctrl+C，也提供顶部"复制"按钮（复用 [`ToolCallCard`](../Editor/UI/Components/ToolCallCard.cs:1) 的 v1.4.8 模式）
5. **域重载安全**：Self-Challenge 内容随 Session 序列化，Domain Reload 后 UI 能重建

#### 3.5.2 卡片布局

在每一轮 assistant turn 里，卡片的位置在 `ThinkingDrawer` 和 `ToolCallGroup` 之间：

```
[User Message]
   ↓
[Assistant Turn]
├── ThinkingDrawer (reasoning field)
├── SelfChallengeCard (v1.5.0 新增) ← 本节设计对象
│   ├── Header (Verdict Badge + Summary + Copy + Toggle Arrow)
│   └── Body (default hidden)
│       ├── Node A: Intent Challenge (4 Steps)
│       └── Node B: Answer Challenge (4 Steps)
├── ToolCallGroup
└── MessageBubble (final response)
```

#### 3.5.3 卡片头部（Header，折叠状态时唯一可见的部分）

单行布局：

```
[Verdict Icon] [Verdict Text]  Intent: 3 interpretations · Reviewer: 3 counter-examples  [Copy] [Arrow]
     |                |                    |                                                 |      |
     |                |                    |                                                 |      └── 折叠/展开箭头
     |                |                    |                                                 └── 复制按钮（复用 v1.4.8 模式）
     |                |                    └── 简短统计（数字化摘要）
     |                └── "PASS" / "REVISED" / "BLOCKED" 大字
     └── [v] / [~] / [!] / [.] 状态图标
```

**Verdict 徽标状态**（借用 v1.4.8 [`ToolCallCard`](../Editor/UI/Components/ToolCallCard.cs:1) 的 ASCII 图标语言）：

| 状态 | 图标 | 颜色 | 说明 |
|------|------|------|------|
| **PASS** | `[v]` | 绿色 (`#4CAF50`) | draft 通过审查 |
| **REVISED** | `[~]` | 橙色 (`#F29C12`) | draft 被修正后输出 |
| **BLOCKED** | `[!]` | 红色 (`#F44336`) | draft 被阻止，回到 tool loop |
| **RUNNING** | `[.]` | 蓝色 (`#4A90D9`) | 正在做 self-challenge（Node B 调用中） |
| **SKIPPED** | 灰色 dot | 灰色 | 该轮 skip（消息太短等），卡片可以完全不显示以节省空间 |

**简短统计示例**：
- `Intent: 3 interpretations · Reviewer: 3 counter-examples`
- `Intent: 3 interpretations · Reviewer: 2 issues found`（REVISED 时）
- `Intent: needs clarification · No reviewer yet`（Node A 判定需要澄清）

#### 3.5.4 卡片主体（Body，默认隐藏）

Body 分两个子区域：Node A 和 Node B。每个子区域内的内容用**只读 TextField + ScrollView**（复用 v1.4.8 建立的可选择/可复制模式），最高 240px 高度出滚动条。

**Node A 子区域**：

```
┌─ Node A: Intent Challenge ──────────────────────────┐
│ Step 1: Interpretations (3)                         │
│   1. [Interpretation 内容]                          │
│   2. [Interpretation 内容]                          │
│   3. [Interpretation 内容]                          │
│                                                     │
│ Step 2: Ambiguity Signals                           │
│   - "material" — could mean array or single item    │
│                                                     │
│ Step 3: Chosen Interpretation                       │
│   Selected: Interpretation 1                        │
│   Reasoning: [...]                                  │
│   Key assumptions:                                  │
│     - [...]                                         │
│                                                     │
│ Step 4: Decision                                    │
│   → Proceed with Interpretation 1                   │
│   (or: → Ask user for clarification)                │
└─────────────────────────────────────────────────────┘
```

**Node B 子区域**：

```
┌─ Node B: Answer Challenge (Reviewer) ───────────────┐
│ Step 1: Assumption Verification                     │
│   - Assumption 1: [...] → VERIFIED via {tool_call_id}│
│   - Assumption 2: [...] → UNVERIFIED                │
│                                                     │
│ Step 2: Counter-Examples (3)                        │
│   1. If X, then draft's "..." is wrong because Y    │
│   2. ...                                            │
│   3. ...                                            │
│                                                     │
│ Step 3: Completeness Check                          │
│   - Part 1: [...] → COMPLETE                        │
│   - Part 2: [...] → PARTIAL                         │
│                                                     │
│ Step 4: Verdict                                     │
│   [~] REVISED                                       │
│   Issues to fix:                                    │
│     - [...]                                         │
└─────────────────────────────────────────────────────┘
```

#### 3.5.5 自动展开/折叠策略

同 v1.4.8 [`ToolCallCard`](../Editor/UI/Components/ToolCallCard.cs:266) 的策略：

- **Verdict = PASS**: 保持默认**折叠**（不打扰）
- **Verdict = REVISED / BLOCKED**: **自动展开**（用户应该看到为什么被修正/阻止）
- **Verdict = RUNNING**: 展开以显示进度（"正在自我审查..."）
- **用户手动切换过**（点击箭头或点击 header）: 记住用户选择，后续不再自动改变（同 `_userToggled` 逻辑）

#### 3.5.6 会话恢复（Domain Reload 安全）

- Self-Challenge 内容随 `SessionData` 序列化
- 新增 `SelfChallengeData` 类：`{intentChallengeText, answerChallengeText, verdict, statistics}`
- 挂载到 `MessageTurn` 上（每个 assistant turn 有一个 SelfChallengeData）
- Domain Reload 后 `ChatWindow.RebuildMessageBubbles()` 里读取并重建 `SelfChallengeCard`

#### 3.5.7 与既有 UI 组件的关系

| 组件 | 位置关系 | 复用点 |
|------|---------|--------|
| [`ToolCallCard`](../Editor/UI/Components/ToolCallCard.cs:1) | 兄弟组件 | 复用颜色常量、图标语言、复制按钮模式、只读 TextField + ScrollView、事件冒泡 StopPropagation |
| `ToolCallGroup` | Self-Challenge 卡片在其上方 | 无直接交互 |
| `ThinkingDrawer` | Self-Challenge 卡片在其下方 | 无直接交互 |
| `AssistantTurnView` | 父容器 | 新增 `SetSelfChallengeCard(card)` 方法，在 `_selfChallengeSlot` 挂载卡片 |

#### 3.5.8 关键实现细节（防止未来实施时踩坑）

- **不使用 emoji**：图标用 `[v]` / `[~]` / `[!]` / `[.]` 纯 ASCII（Unity SDF 字体渲染 emoji 是方块）
- **Copy 按钮点击 `StopPropagation`**：避免误触卡片折叠切换
- **ScrollView / TextField 点击 `StopPropagation`**：同上
- **卡片状态存 `_userToggled` 标志**：用户手动切换后禁用自动折叠逻辑

#### 3.5.9 UI 部分工作量估算

- `SelfChallengeCard.cs` 组件：**2 人日**
- `AssistantTurnView.cs` 扩展：**0.5 人日**
- 序列化（`SelfChallengeData` + `SessionData` 扩展）：**0.5 人日**
- Domain Reload 恢复：**0.5 人日**
- 集成与视觉打磨：**0.5 人日**
- **合计约 3~4 人日**（含在 §7.1 v1.5.0 范围内）

### 3.6 Waiting-for-Clarification 会话状态（v0.6 新增）

当 Node A Step 4 结论为"反问用户"时，Agent **必须**进入 `WaitingForClarification` 会话状态。这个状态有严格的行为约束：

#### 3.6.1 状态定义

新增枚举值到 [`AgentState`](../Editor/Core/MessageTypes.cs)：

```csharp
public enum AgentState
{
    Idle,
    Streaming,
    ExecutingTool,
    WaitingForClarification,  // v0.6 新增
    // ...
}
```

#### 3.6.2 状态转换

```
Idle
  ↓ (用户发消息)
Streaming (LLM 生成 <intent_challenge>)
  ↓
  ├─ Step 4 结论 = 反问用户 → WaitingForClarification
  │                            ↓ (用户新消息)
  │                          Streaming (重新走 Node A，但跳过完整 Interpretation 重构)
  │
  └─ Step 4 结论 = 全部满足 → ExecutingTool (正常 tool loop)
```

#### 3.6.3 状态行为约束

**当 Agent 处于 `WaitingForClarification` 状态**：

1. **禁止任何 tool call**：即使 LLM 输出 tool_calls，工程侧 [`AgentLoop.Runner.cs`](../Editor/Core/AgentLoop.Runner.cs) 会拒绝分发；如发生视为 bug 并 warn
2. **LLM 的输出必须是** `[CLARIFICATION NEEDED]` 开头的反问 message（工程侧校验）
3. **UI 明确显示状态**：ChatWindow 底部 status bar 显示 "Agent is waiting for your clarification..."（黄色/橙色 accent）
4. **保持消息焦点在输入框**：用户回车即发送新消息
5. **保存到 `SessionData`**：Domain Reload 后能恢复到 WaitingForClarification 状态，不会误进入正常 tool loop

#### 3.6.4 用户下一步回复的处理

用户回复澄清后：

- Agent 状态 `WaitingForClarification` → `Streaming`
- Node A **重新触发**，但为了避免重复冗长：
  - Interpretations 部分**可以简化**：LLM 只需说明"根据用户澄清，chosen interpretation 现在是 X"
  - Step 2 歧义词部分：应该重新识别，因为用户澄清可能只解决了一个歧义，还有其他
  - Step 4 条件重新评估
- 如果新一轮 Node A 仍判断"必须反问" → 再次进入 `WaitingForClarification`
- **v0.7 立场调整**：**不再强制"连续 N 轮反问硬上限"**。用户明确要求"用户不回复就卡住就卡住"—— 反问循环是设计上允许的行为，工程侧不介入。如果 Agent 真的反复反问，说明用户 query 确实模糊到无法执行，用户会主动换更清晰的描述或放弃这次对话——都是合理终止。

#### 3.6.5 UI 展示细节

**Chat 窗口专属样式**（反问消息用不同视觉风格，让用户一眼看出这是需要回答的问题而不是最终答复）：

- Assistant 消息头部图标改为 `[?]` 而不是 `[assistant]`
- 消息左边框改为**黄色/橙色** accent（区别于普通答复的默认色）
- 消息尾部加一段 miniLabel："Agent 正在等待你的澄清 · Input focused"

**输入框状态**：

- 自动 focus 到输入框（无需用户点击）
- Placeholder 改为："请回答上方澄清问题..."（提醒用户）

**Self-Challenge Card 展示**：

- 当 Agent 处于 `WaitingForClarification` 时，Self-Challenge Card 的 Verdict 徽标显示 `[?]` 蓝色，text = "Awaiting Clarification"
- 卡片**自动展开**（用户应该看到为什么 Agent 决定反问）

#### 3.6.6 与既有系统的交互

**与 Domain Reload**：
- `WaitingForClarification` 状态写入 [`DomainReloadState`](../Editor/Core/DomainReloadState.cs)
- Reload 后 [`AgentLoop.TryResumeAfterReload`](../Editor/Core/AgentLoop.DomainReload.cs) 恢复到该状态，不会误进入 tool loop

**与循环刹车**：
- 反问 message 不计入循环刹车的 tool call 计数（反问本来就没调工具）
- v0.7 无"连续反问硬上限" —— 只要用户还在回复，Agent 就继续 Node A 循环

**与 Evidence Gate**：
- `[CLARIFICATION NEEDED]` 开头的消息**跳过** Evidence Gate（Layer 5）—— 因为它本来就不是"最终答复"

#### 3.6.7 工作量估算

- `AgentState.WaitingForClarification` 枚举 + 状态机分支：**0.5 人日**
- 工程侧禁止 tool call 校验：**0.3 人日**
- `SessionData` / `DomainReloadState` 序列化：**0.5 人日**
- ChatWindow UI 专属样式：**0.5 人日**
- 集成与打磨：**0.2 人日**
- **合计约 2 人日**（新增到 v1.5.0）

---

## 4. Token 与延迟成本估算

### 4.1 Node A 成本

- **无额外 LLM 调用**（追加在首次 user message 请求里）
- 追加的指令 prompt 约 **500~800 tokens**（system-level）
- LLM 需要输出 `<intent_challenge>` 块，约 **300~500 tokens** 额外输出
- **净成本**：每次会话首次调用 +800~1300 tokens；后续轮次无额外成本

### 4.2 Node B 成本

- **需要一次额外的 LLM 调用**（reviewer 调用）
- reviewer prompt 约 **1200~2000 tokens**（含 draft + query + tool_results + intent_challenge）
- reviewer 输出的 `<answer_challenge>` 块约 **500~800 tokens**
- **净成本**：每次 final response +1700~2800 tokens

若发生 REVISE（约 30% 概率估计），还需重新生成 final response，再 **+ 500~1500 tokens**。

### 4.3 总体估算

**典型对话 token 增量**：
- 短对话（1~2 轮 tool call）：+30~50% token
- 长对话（10+ 轮 tool call）：+10~20% token（因为 self-challenge 成本被 tool 调用的成本稀释）

**延迟增量**：
- Node A：无额外延迟（同请求）
- Node B：每次 final response 多一次 LLM 往返，约 **1~3 秒**（取决于模型响应速度）

**这个成本值得吗？**
- 如果 self-challenge 能让 Qwen 3 VL 从"7 次调用 + 错误答案"变成"3 次调用 + 正确答案"—— **净收益**
- 如果只是让前沿模型稍微更严谨—— **成本敏感用户可能关闭**，通过 Legacy Mode
- 我承认这是**用 token 换准确率**的取舍，用户可以选择

---

## 5. 可验证性：如何知道 Self-Challenge 真的生效？

**核心问题**：如果 self-challenge 只是 rubber-stamp，我们看不出来。需要工程侧的**可观测机制**。

### 5.1 强制指标（v1.5.0 必须实现）

在 Settings 里新增"Self-Challenge Statistics"面板，显示（最近 N 次对话）：

- **Node A 触发次数** / 跳过次数
- **Node A 输出中 Interpretations 的平均差异度**（用 Levenshtein 距离粗测；如果 3 个 Interpretation 距离都 <30 字符，说明模型在敷衍）
- **Node A 输出的"需要澄清"占比**（正常应该有 5~15%；如果 0% —— 说明模型从不认为需要澄清，可疑）
- **Node B 触发次数** / 跳过次数
- **Node B Verdict 分布**（PASS / REVISE / BLOCK 各占比）
  - 正常分布：PASS 50~70% / REVISE 25~40% / BLOCK 5~15%
  - 100% PASS —— rubber-stamp 信号
  - 100% BLOCK —— 过度保守信号
- **Node B counter-example 引用有效率**（工程侧解析 counter-example 里引用的字符串，看是否在 draft 中真实出现；<80% 说明模型在编造引用）

### 5.2 用户可观测（v1.6.0）

- ChatWindow UI 展示每轮 Self-Challenge 的折叠区域（默认折叠、可展开）
- 用户可以直观看到 Agent 是如何自我挑战的
- 用户发现 rubber-stamp 时可以给反馈

### 5.3 A/B 测试能力（v1.7.0）

- 设置里的 `Legacy Mode` 开关允许用户临时关闭 self-challenge，对同一 query 做对比
- 用户可以自己确认收益是否成立

### 5.4 v1.5.0 上线后 4 周内的验证条款（v0.7 新增）

**为什么需要这个条款**：v0.6 → v0.7 的批判性审视里，我承认了几条**根本性风险**无法在设计阶段消除，只能靠上线数据验证：

- **R7**（追责链只能抓 LLM 意识到的假设）—— 保留，随 LLM 能力提升自动改善
- **R16**（"self-challenge 提升准确度"在 Unity Agent 场景无直接证据）—— 只能实测
- **R17**（SOUL 里更严格的规则已经不生效了，凭什么相信新加的会生效？）—— 只能实测

因此 v1.5.0 上线后 **4 周内的实测数据决定方案的最终去留**。

**数据收集要求**：Statistics 面板从 v1.5.0 起就收集以下数据（用户可导出）：

- Node A 触发次数 / skip 次数
- Node A 输出的 Interpretations 平均差异度（Levenshtein 距离）
- Node A "需要反问"的触发占比
- Node A `<consistency_correction>` 中 corrected vs `[Consistent]` 比例
- Node B 触发次数 / skip 次数
- Node B Verdict 分布（PASS / REVISE / BLOCK）
- Node B counter-example 引用有效率（`<draft-quote>` 内容 substring 命中率）
- 结构校验失败率与 correction retry 次数
- 用户是否手动关闭 Self-Challenge 开关

**判定标准（4 周后 review）**：

| 指标 | 健康阈值 | 异常处理 |
|------|---------|---------|
| Node B Verdict = PASS 占比 | 40~80% | >95% 视为 rubber-stamp，需要调整 prompt 或引入独立 Reviewer 模型 |
| Node A 反问触发占比 | 5~20% | >30% 视为过度反问，需要放宽 Step 4 触发条件；<2% 视为绕过反问，需要收紧 |
| 结构校验失败率 | <10% | >30% 视为格式指令失效，需要简化 prompt 或增加示例 |
| 用户手动关闭开关比例 | <15% | >30% 视为体验拖累超过收益，考虑重构或回滚 |
| `[Consistent]` 占比 | 60~90% | 100% 视为一致性 check 全部 rubber-stamp；<40% 视为 LLM 过度自纠 |

**处理规则**：

- 全部指标健康 → 维持 v1.5.0 方案，进入 v1.6.0 补充功能
- 1~2 项异常 → 局部调整（prompt 微调、参数放宽），保留方案
- 3+ 项异常 → 触发 **retrospective**：重新审视方案是否适合当前主流模型，考虑回退到"仅 UI 展示 + 保留 SOUL 规则"的轻量方案

**为什么写进方案**：让"实测决定"从口头承诺变成**成文条款**，避免后续讨论时被"我觉得应该"绕过。上线 4 周后必须做一次 formal review，不能默认延续。

### 5.5 首周引导条款（v0.9 新增，替代取消的 Canary Probes）

**背景**：v0.8 曾用 Canary Probes 解决"首日 rubber-stamp 检测盲区"。v0.9 取消 Canary Probes（原因见 §11.7），改用**用户引导 + UI 天然可见性**替代。

**核心思路**：让用户在最初 5~10 次交互期间**主动观察 Self-Challenge UI**，通过人眼判断 self-challenge 是否真正生效——这比工程侧关键词检测**更可靠**（用户知道自己刚才问的是什么、Agent 是不是敷衍）。

**v1.5.0 上线时必须交付的用户引导**：

1. **首次启动 tooltip**：Chat 窗口首次打开时（session 全新），在窗口顶部显示一次性提示条：
   > "AgentCore v1.5.0 新增 Self-Challenge 机制。请留意每条回复上方的自省卡片（默认折叠）。展开后可看到 LLM 是如何理解你的需求、审视自己的答案的。**建议前 5~10 次对话时展开看看**，判断 self-challenge 是否有效。"
   
2. **README/CHANGELOG 明确提示**：v1.5.0 发布 note 里包含一段"如何判断 Self-Challenge 是否生效"：
   - 如果 Verdict 徽标**几乎全是 `[v] PASS`（绿色）**，且 Node A 里的 3 个 Interpretation 看起来非常相似 → 疑似 rubber-stamp，考虑关闭或换 LLM
   - 如果 Verdict 里有一定比例 `[~] REVISED`（橙色）或 `[!] BLOCKED`（红色）→ 机制正常工作
   - 如果 Agent 频繁反问澄清（`[?]` 蓝色） → 首日可能过度反问，观察 3-5 次后如果仍频繁，考虑放宽（但过度反问优于错误答案）
   
3. **Self-Challenge Card 默认可见性调整**：v1.5.0 上线**前 5 次对话**，卡片**默认展开**（不折叠）——强制用户注意到它的存在。第 6 次起恢复"默认折叠、异常自动展开"策略。
   - 实施：`AgentCoreSettings` 新增 `selfChallengeCardCountForcedExpansion = 5`，每完成一次 Node A + Node B 就递减，减到 0 后转回默认策略。

4. **Settings 面板显眼位置放"如何验证 Self-Challenge 是否生效"链接**：跳转到 §5.5 或独立文档，包含实操 checklist。

**成本估算**：
- 首次启动 tooltip：0.3 人日
- README/CHANGELOG 描述：0.2 人日（文档，非代码）
- 前 5 次强制展开：0.3 人日
- Settings 链接：0.1 人日
- **合计约 0.9 人日**（相比 Canary Probes 的 1.5 人日节省 0.6 人日，且不违反不穷举立场）

**为什么这更好**：
- **完全依赖用户观察，不引入穷举**
- **成本更低**（0.9 vs 1.5 人日）
- **更透明**（用户主动看到问题 vs 工程侧偷偷检测）
- **教育用户**（顺路让用户理解 self-challenge 机制，未来更好使用）

---

## 6. 已识别的风险与缓解

| 风险 | 严重度 | 缓解 |
|------|-------|------|
| Node B rubber-stamp（LLM 认自己写的对） | 高 | 5 道防线（角色扮演隔离 / 格式约束 / 结构校验 / 假设显式化 / 空转检测） |
| 强对齐模型（Claude）过度触发 REVISE | 中 | 可接受的偏保守；提供关闭开关 |
| Token 成本 +10~50% | 中 | 提供 Legacy Mode；对短消息自动 skip |
| Node B 延迟 1~3 秒影响体验 | 中 | UI 显示"Agent 正在自我审查..." 状态，让用户明确知道在做什么 |
| Node A 首次请求 prompt 长（+800~1300 token）压缩了历史 context | 中 | 明确 Node A 只在首次 user message 触发；后续轮次不重复 |
| 结构校验失败陷入死循环 | 低 | `answerChallengeMaxRetries` 硬限 3 次；超过则接受 draft 或 fallback |
| 用户不理解为什么 Agent 变慢了 | 中 | UI 明确显示 self-challenge 状态；文档说明；提供关闭开关 |

---

## 7. 实施路线与版本规划

### 7.1 v1.5.0 — Self-Challenge 核心机制 + 用户可观测 UI + 强制反问用户（v0.9 最终版）

**v0.9 立场校准**：v1.5.0 工作量随 v0.8 → v0.9 变化：
- **移除**：Canary Probes 相关（-1.5 人日）
- **移除**：R2/R4/R5 skip 规则实现（-0.3 人日）
- **新增**：v0.9 §5.5 首周引导（+0.9 人日）
- **净变化**：约 -0.9 人日
- **v0.9 v1.5.0 总工作量**：约 **14-16 人日**（v0.8 曾估 12-14 人日 → 加上首周引导 + enum + retry 独立会话等小改动的实际扩展）

**范围**（约 12~14 人日）：

**核心机制部分**（约 6~8 人日）：
- [ ] 新建 `Editor/Core/SelfChallenge/` 目录（4 个 C# 文件）
- [ ] Node A：`IntentChallenge` prompt 模板 + 注入点 + 解析 + 结构校验
- [ ] Node B：`AnswerChallenge` reviewer prompt 模板 + 独立会话调用 + 解析 + 结构校验
- [ ] Verdict 处理逻辑（PASS/REVISE/BLOCK 三分支）
- [ ] `AgentCoreSettings` 添加 self-challenge 配置项
- [ ] `AgentLoop.LLM.cs` / `AgentLoop.Runner.cs` 集成
- [ ] AgentEvent 事件流（`IntentChallengeCompleted` / `AnswerChallengeCompleted` / `AnswerChallengeRegenerating`）

**强制反问用户部分**（约 2 人日，v0.6 新增到 v1.5.0）：
- [ ] Node A Step 4 反转举证责任 prompt 模板（详见 §1.2.2）
- [ ] `IntentChallengeParser` 增加**四条一致性校验规则**（详见 §1.2.4）
- [ ] `AgentState.WaitingForClarification` 枚举与状态机分支（详见 §3.6）
- [ ] 处于 WaitingForClarification 时禁止 tool call 的工程校验（详见 §3.6.3）
- [ ] `SessionData` / `DomainReloadState` 序列化 WaitingForClarification 状态
- [ ] ChatWindow UI：反问消息的专属样式（`[?]` 图标 + 黄色/橙色 accent + status bar 提示 + 输入框 auto-focus）

**用户可观测 UI 部分**（约 3~4 人日，v0.5 新增到 v1.5.0）：
- [ ] 新建 `Editor/UI/Components/SelfChallengeCard.cs`（**默认折叠**的自省卡片，详见 §3.5 UI 设计）
- [ ] ChatWindow 集成：Self-Challenge 卡片插入到 assistant turn 的 ThinkingDrawer 与 ToolCallGroup 之间
- [ ] Node A 卡片内容：Interpretations / 歧义信号 / 关键假设 / 4 个条件判断 / 决策
- [ ] Node B 卡片内容：Verdict 徽标 / Counter-Examples / Assumption Verification / 完整性检查
- [ ] Verdict 顶部徽标（`[v] PASS` 绿色 / `[~] REVISED` 橙色 / `[!] BLOCKED` 红色 / `[?] Awaiting` 蓝色）
- [ ] Session 序列化：Self-Challenge 卡片数据随会话保存，Domain Reload 恢复
- [ ] "复制"按钮（复用 v1.4.8 ToolCallCard 的模式）

**验收标准**：

**功能**：
1. 复现原案例："帮我获取场景中选中 object 的 material"
2. **期望**：Node A 列出 "所有材质" vs "第一个材质" 至少 2 种解读；Node B 若 draft 只说 1 个材质，触发 REVISE 或 BLOCK
3. **期望**：Qwen 3 VL 的错误答案在 Node B 被拦截并修正
4. **无穷举**：整套机制不含任何"材质"、"prefab"专属逻辑；对**任何** query 通用

**UI**：
5. 每个 assistant turn 有 Self-Challenge 卡片，**默认折叠**，只显示一行状态摘要（Verdict 徽标 + 简短统计）
6. 展开后能完整看到 Node A 的 4 个 Step + Node B 的 4 个 Step
7. Verdict = REVISE / BLOCK 时卡片**自动展开**（发生行为改变，用户应该看到）
8. Verdict = PASS 时卡片**保持默认折叠**（不打扰）
9. 关闭 Self-Challenge 开关后卡片不显示；开关状态持久化到 EditorPrefs

### 7.2 v1.6.0 — Statistics 面板 + 观测优化（v0.5 精简）

**范围**（约 2~3 人日）：

- [ ] Settings 面板 "Self-Challenge Statistics"：Verdict 分布图、Interpretations 差异度、counter-example 引用有效率
- [ ] Verdict 分布异常时的告警（100% PASS / 100% BLOCK → 卡片头部显示警告徽标）
- [ ] UI 卡片补充"reviewer 视角"标签，帮助用户理解 Node B 不是 Agent 本人

### 7.3 v1.7.0+ — 外部审查与优化（可选，v0.5 保持不动）

- [ ] 支持独立小模型做 Reviewer（引入真正外部视角）
- [ ] A/B 测试模式（对同一 query 用两种模式跑一遍对比）
- [ ] 用户反馈驱动的 prompt 微调（收集"用户认为 Verdict 错误"的样本）

---

## 8. 变更前 review 清单（用户确认后进入实施）

- [ ] 认可"抛弃所有穷举式解法，只做 Self-Challenge"的方向
- [ ] 认可两个节点（Node A 读需求时 + Node B 输出前）的定位
- [ ] 认可"强制 3+ Interpretation / 3+ Counter-Example + 引用校验"的对抗 rubber-stamp 设计
- [ ] 认可 Node B 需要额外一次 LLM 调用（token 成本 +10~50%）
- [ ] 认可 v1.5.0 只做核心机制、不做 UI 展示（v1.6.0 补）
- [ ] 认可"允许 Agent 主动问澄清"的行为改变（Node A Step 4）
- [ ] 认可 v0.7 取消 `useIsolatedReviewerContext`，Node B 始终带压缩历史
- [ ] 认可 v0.7 取消"连续反问硬上限"（用户不回复就卡住）
- [ ] 认可 v0.7 Step 5 LLM 自我一致性校验替代 v0.6 工程侧关键词一致性
- [ ] 认可 Counter-Example 用 `<draft-quote>` 结构化标记而非 substring 关键词校验
- [ ] 明确 Settings UI 里 Self-Challenge 是否默认开启（建议 true，允许 Legacy Mode 关闭）

Review 通过后，本文件转为"实施中"，v1.5.0 开工。

---

## 9. 核心洞察 TL;DR

1. **v0.3 及以前所有穷举式方案都被否定**：语义工具清单、Intent Guardian 关键词表、UNITY_TRAPS.md、`completeness` 字段普及、Tier 分层——**全部取消**。理由：用户需求本质不可预测、不可标注。

2. **v0.4 唯一策略：Self-Challenge**：在两个通用节点（读需求时 + 输出前）强制 LLM 做真正的自我挑战，激活它已有但被动的元认知能力。

3. **对抗 Rubber-Stamp 是核心工程挑战**：靠五道防线协同——
   - 角色扮演隔离（"你是第三方 skeptical reviewer，默认认为 draft 有错"）
   - 强制格式约束（至少 3 个 Interpretation / 3 个 Counter-Example，每个 ≥20 字符）
   - 工程侧结构校验（不通过则 correction retry）
   - 假设显式化 + 追责（Node A 说的假设，Node B 必须逐一验证）
   - 空转检测（Verdict 分布异常时报警）

4. **通用性优先于覆盖精度**：这个方案不能保证 100% 覆盖所有场景（比不上 v0.3 语义工具在**已覆盖场景**里的 100% 准确），但覆盖**所有场景**（陌生场景、长尾问题、模糊 query 都能触发）。

5. **可验证性内建**：Settings 里的 Self-Challenge Statistics 能让用户判断 self-challenge 是否真的生效，不是黑箱。

6. **成本 vs 收益**：+10~50% token / +1~3 秒延迟，换来 Qwen 3 VL 从"7 次调用错误答案"到"3 次调用正确答案"的行为改变。用户可通过 Legacy Mode 关闭。

7. **诚实局限声明**：
   - LLM 认自己写的对（认知一致性偏差）是**基础限制**，五道防线只能缓解不能消除
   - 强对齐模型可能过度触发 REVISE
   - 短消息 skip 判定是简单启发式，不完美但够用
   - 我**没有网络查询工具**，方案设计中提到的"论文验证"等我无法引用具体来源
   - 方案的最终有效性**只能通过 v1.5.0 实测验证**——建议先做 POC 再决定是否推向所有用户

---

## 附录 A: 完整 Prompt 模板（用于工程侧硬编码）

**A.1 Node A Intent Challenge 追加指令**：见 §1.2.2（完整段落）
**A.2 Node B Reviewer Prompt 模板**：见 §1.3.3（完整段落）
**A.3 Node A Continuation 模板**：见 §1.2.5（完整段落，v0.8 新增）
**A.4 Correction Retry 模板集合**：见 §11.5（v0.8 新增，含 Node A/B/Continuation 三种）

**这些 prompt 是本方案的核心资产**。实施时**逐字硬编码到 C# 常量**，且做好版本管理（未来调优时保留 diff 历史）。

---

## 11. 实施细节完整清单（v0.8 新增 —— 零 gap 保证）

本章节收录 v0.7 审计时发现的 7 处 gap + 3 处冲突的**完整解决方案**。**每一小节都对应一个明确的实施对象**（C# 类 / 方法 / prompt 模板 / UI 组件），实施者可直接按此清单编码。

### 11.1 Gap 1: Node B 组装压缩历史的具体规则

**位置**：`Editor/Core/SelfChallenge/AnswerChallengeReviewer.cs` 里的 `BuildReviewerMessages` 方法。

**输入**：
- `userQuery` — 用户最新一条 message 的完整原文
- `draftAnswer` — LLM 刚生成的 final response draft
- `intentChallengeBlock` — 本轮 Node A 输出的完整 `<intent_challenge>` 或 `<intent_challenge_continuation>` 块
- `mainConversation` — 到当前 turn 为止的完整对话历史

**输出**：`List<ChatMessage>` — 发给 LLM 的 reviewer 会话消息序列。

**组装规则**（**严格按此顺序**）：

```
messages[0] = {
  role: "system",
  content: [Reviewer 角色 system prompt，见 §1.3.3 完整模板]
}

messages[1..N] = 压缩后的主对话历史（含 tool results）
  实施原则（不写死具体阈值，实施时依据 ConversationCompressor 现有 API 调整）：
  - **复用现有 ConversationCompressor**（`Editor/Core/Compression/ConversationCompressor.cs`）
  - **强制保留**（不参与压缩）：
    - 最近若干轮完整对话（v1.5.0 首版：最近 3 轮 assistant/user/tool_result；后续根据实测调整）
    - 本轮 assistant 的 <intent_challenge> 块所在的 assistant message（Node B 需要引用它做假设验证）
  - **强制丢弃**：之前所有轮次的 <answer_challenge> 块（Reviewer 看到之前的 REVIEW 结果会有锚定偏差；这是有意为之的信息裁剪）
  - **一般压缩策略**：其他消息走 ConversationCompressor 现有策略；目标是让 messages[1..N] 总 token 数控制在 reviewer 上下文预算内（v1.5.0 建议 2000~3000 tokens，实施时根据 LLM context window 弹性调整）
  - **实施时的开放决策**（不在方案里写死）：
    - "最近 3 轮完整保留"是首版设定，可能实测偏少（比如需要 5 轮）或偏多（3 轮已够）；实施时容易调整
    - "丢弃之前 answer_challenge"是首版决策，若实测发现丢弃后 Reviewer 反复质疑用户已确认的事项，v1.6.0 可考虑改为"保留但用简短摘要替代"

messages[N+1] = {
  role: "user",  // 注意：用 user 角色包装 review 请求，让 LLM 认为这是新用户消息而非 continuation
  content: [
    "<user_query>{userQuery}</user_query>",
    "<my_intent_challenge>{intentChallengeBlock}</my_intent_challenge>",
    "<draft_answer>{draftAnswer}</draft_answer>",
    "",
    "[REVIEWER TASK - see system prompt for full instructions]"
  ]
}
```

**关键决策解释**：

- **为什么保留最近 3 轮**：Reviewer 需要"用户之前说过什么"这类 immediate context 来做语义批评
- **为什么丢弃之前的 `<answer_challenge>` 块**：如果 Reviewer 看到"上一轮 Node B 判 PASS"，会倾向于本轮也判 PASS（认知锚定偏差）
- **为什么保留本轮 `<intent_challenge>`**：Node B Step 1 需要引用 Node A 的关键假设做 verify
- **为什么用 user 角色包装 review 请求**：让 LLM 认为是"新用户提问"而非 continuation，激活 fresh reasoning 而非延续之前的 stance

**边界情况**：
- 主对话为空（v1.5.0 首次会话首个 message）：`messages[1..N]` 为空，Reviewer 只有 system + user
- 压缩后仍超 4000 tokens：进一步截断最早的历史，保证总 tokens < 4000（Reviewer 上下文预算）

### 11.2 Gap 3: SelfChallengeData 完整 Schema

**位置**：`Editor/Session/SelfChallengeData.cs`（新建文件）。

**完整 C# 定义**：

```csharp
using System;
using System.Collections.Generic;
using UnityEngine;

namespace AgentCore.Editor.Session
{
    /// <summary>
    /// 单个 assistant turn 的 self-challenge 全量数据。
    /// 挂载到 MessageTurn，序列化到 SessionData 里。
    /// </summary>
    [Serializable]
    public class SelfChallengeData
    {
        // ─── Node A (Intent Self-Challenge) ─────────────────────────
        
        /// <summary>Node A 是否触发（false 表示 skip）</summary>
        public bool nodeATriggered;
        
        /// <summary>Skip 时的原因（"R1_short" / "R2_codeblock" / "R3_url" / "R4_stacktrace" / "R5_confirmation" / null 表示未 skip）</summary>
        public string nodeASkipReason;
        
        /// <summary>是否为 Continuation 模式（true = Continuation, false = 完整 Node A）</summary>
        public bool isNodeAContinuation;
        
        /// <summary>Node A 完整输出的 <intent_challenge> 或 <intent_challenge_continuation> 块原文</summary>
        public string nodeAOutput;
        
        /// <summary>解析后的 Interpretation 列表（Continuation 模式为空）</summary>
        public List<string> interpretations = new List<string>();
        
        /// <summary>解析后的歧义词列表</summary>
        public List<string> ambiguitySignals = new List<string>();
        
        /// <summary>选定的 chosen interpretation 文本</summary>
        public string chosenInterpretation;
        
        /// <summary>关键假设列表</summary>
        public List<string> keyAssumptions = new List<string>();
        
        /// <summary>Step 4 判断的四维度（v0.9：改用 enum 替代 string，防止拼写错误）</summary>
        public Step4Ambiguity step4A;      // Yes / No
        public Step4Severity step4B;        // Severe / Minor
        public Step4OperationRisk step4C;   // Destructive / Safe
        public Step4Attribution step4D;     // Inferred / Verbatim
        
        /// <summary>当 step4D == Inferred 时的推断词列表（Verbatim 时为空）</summary>
        public List<string> inferredWords = new List<string>();
        
        /// <summary>Step 4 结论</summary>
        public Step4Conclusion step4Conclusion; // Combo1 / Combo2 / DirectExecute
        
        /// <summary>Step 5 verdict</summary>
        public Step5Verdict step5Verdict;  // Consistent / Corrected
        
        /// <summary>Step 5 corrected judgement 文本（仅当 step5Verdict == Corrected 时非空）</summary>
        public string step5CorrectedJudgement;
        
        /// <summary>Node A 触发的 correction retry 次数</summary>
        public int nodeARetryCount;
        
        /// <summary>Node A 是否最终导致 Agent 进入 WaitingForClarification（等价于 step4Conclusion != DirectExecute）</summary>
        public bool triggeredClarification;
        
        /// <summary>v0.9：移除 clarificationMessage 字段（信息可从 nodeAOutput 里解析出来，避免重复存储）</summary>
        
        /// <summary>v0.9 新增：Continuation 模式下引用的上一轮 turn ID（非 Continuation 时为 null）</summary>
        public string previousTurnNodeAId;
        
        
        // ─── Node B (Answer Self-Challenge) ─────────────────────────
        
        /// <summary>Node B 是否触发（false 表示 skip）</summary>
        public bool nodeBTriggered;
        
        /// <summary>Skip 时的原因</summary>
        public string nodeBSkipReason;
        
        /// <summary>Node B 完整输出的 <answer_challenge> 块原文</summary>
        public string nodeBOutput;
        
        /// <summary>Verdict（v0.9：改用 enum）</summary>
        public NodeBVerdict nodeBVerdict;
        
        /// <summary>Counter-Example 数量（应 >= 3）</summary>
        public int counterExampleCount;
        
        /// <summary>Counter-Example 里所有 <draft-quote>...</draft-quote> 里的引用内容</summary>
        public List<string> counterExampleQuotes = new List<string>();
        
        /// <summary>REVISE 时的 issues 列表</summary>
        public List<string> reviseIssues = new List<string>();
        
        /// <summary>BLOCK 时的 verifications 列表</summary>
        public List<string> blockVerifications = new List<string>();
        
        /// <summary>Node B 触发的 correction retry 次数</summary>
        public int nodeBRetryCount;
        
        /// <summary>REVISE 时是否触发了 draft 重新生成</summary>
        public bool draftRegenerated;
        
        
        // ─── Metadata ────────────────────────────────────────────────
        
        /// <summary>本轮 self-challenge 的总耗时（ms）</summary>
        public long totalDurationMs;
        
        /// <summary>本轮 self-challenge 消耗的总 tokens（input + output 估算）</summary>
        public int totalTokensEstimate;
        
        /// <summary>本轮 self-challenge 的 timestamp</summary>
        public long timestampUnix;
    }
    
    // ─── v0.9 新增：类型安全的 enum 定义 ────────────────────────────
    
    public enum Step4Ambiguity   { Yes, No }
    public enum Step4Severity    { Severe, Minor }
    public enum Step4OperationRisk { Destructive, Safe }
    public enum Step4Attribution { Inferred, Verbatim }
    public enum Step4Conclusion  { Combo1, Combo2, DirectExecute }
    public enum Step5Verdict     { Consistent, Corrected }
    public enum NodeBVerdict     { PASS, REVISE, BLOCK }
}
```

**挂载点**：`Editor/Session/SessionData.cs` 里的 `MessageTurn` 类新增字段：

```csharp
[SerializeField]
public SelfChallengeData selfChallenge;  // nullable, null 表示该 turn 未触发 self-challenge
```

**版本兼容**：旧 session（v1.4.x 的 SessionData）不含 `selfChallenge` 字段，反序列化时该字段为 null。UI 层遇到 null 直接不渲染 SelfChallengeCard。**无需版本迁移**。

### 11.3 Gap 4: 3 个 AgentEvent 完整定义

**位置**：`Editor/Core/MessageTypes.cs` 里的 `AgentEventType` 枚举 + `AgentEvent` 类。

**新增枚举值**：

```csharp
public enum AgentEventType
{
    // ... existing ...
    
    /// <summary>Node A 完成（无论是 skip 还是完整执行），携带 SelfChallengeData（部分填充，Node A 部分）</summary>
    IntentChallengeCompleted,
    
    /// <summary>Node B 完成（无论是 skip 还是完整执行），携带 SelfChallengeData（Node B 部分填充完毕）</summary>
    AnswerChallengeCompleted,
    
    /// <summary>Node B Verdict = REVISE 后触发 draft 重新生成（UI 显示"正在修正..."）</summary>
    AnswerChallengeRegenerating,
    
    /// <summary>v0.9 新增：draft 重新生成完成后触发（AnswerChallengeRegenerating 的收尾）；触发后 UI 恢复正常显示</summary>
    AnswerChallengeRegenerated,
}
```

**Payload 字段**（挂在 `AgentEvent` 类上，可选字段用 null 表示不适用）：

```csharp
public class AgentEvent
{
    // ... existing fields ...
    
    /// <summary>Self-Challenge 数据（仅 IntentChallengeCompleted / AnswerChallengeCompleted / AnswerChallengeRegenerating 事件有值）</summary>
    public SelfChallengeData SelfChallenge;
    
    /// <summary>关联的 turn ID（用于 UI 定位到具体的 SelfChallengeCard）</summary>
    public string TurnId;
}
```

**事件发出时机**：

| 事件 | 触发点 | Payload 内容 |
|------|--------|-------------|
| `IntentChallengeCompleted` | Node A 完成解析（含 correction retry 全部结束后），无论 triggerd = true 还是 skip | `SelfChallenge` 里 Node A 相关字段全部填充；Node B 字段为默认值 |
| `AnswerChallengeCompleted` | Node B 完成解析（含 correction retry 全部结束后） | `SelfChallenge` 里 Node A + Node B 字段全部填充完毕 |
| `AnswerChallengeRegenerating` | Node B Verdict = REVISE 且 draft 重新生成开始时 | `SelfChallenge` 里 `nodeBVerdict == REVISE` 且 `reviseIssues` 有值；`draftRegenerated == false` |
| `AnswerChallengeRegenerated` | draft 重新生成 LLM 调用完成后 | `SelfChallenge` 里 `draftRegenerated == true`；新的 draft 已可展示 |

**UI 层监听**（`ChatWindow.Events.cs` 里）：

```csharp
case AgentEventType.IntentChallengeCompleted:
    HandleIntentChallengeCompleted(evt);  // 创建或更新 SelfChallengeCard 的 Node A 部分
    break;

case AgentEventType.AnswerChallengeCompleted:
    HandleAnswerChallengeCompleted(evt);  // 更新 SelfChallengeCard 的 Node B 部分 + 根据 Verdict 决定是否自动展开
    break;

case AgentEventType.AnswerChallengeRegenerating:
    HandleAnswerChallengeRegenerating(evt);  // SelfChallengeCard Verdict 徽标改为 "正在修正..." 状态
    break;
```

### 11.4 Gap 6: SelfChallengeCard 的 `_userToggled` 恢复策略

**决策**：`_userToggled` **不持久化**，Domain Reload / Session 重新打开后**重置为 false**。

**理由**：
- `_userToggled` 是**当次 UI session 的临时状态**，代表"用户在这次浏览时手动折叠/展开过"
- Domain Reload 后用户是**重新浏览**卡片，让默认展开策略（Verdict = REVISE/BLOCK 自动展开）重新生效更符合直觉
- 如果持久化，用户上一次手动折叠后再次打开 Editor 看到 REVISED 卡片却是折叠的，可能错过重要信息

**实施**：`SelfChallengeData` 里**不含** `userToggled` 字段。UI 层 `SelfChallengeCard` 每次从 Session 重建时 `_userToggled = false`，展开状态完全由 `Verdict` 决定。

### 11.5 Gap 5: Correction Retry 完整 Prompt 模板集合

**位置**：`Editor/Core/SelfChallenge/IntentChallengeParser.cs` / `AnswerChallengeParser.cs` 里的 correction prompt 常量。

**共通实现策略（v0.9 修正）**：

- Correction retry **使用独立小会话**（v0.9 修正，v0.8 的"追加式 retry"有风险）：
  - retry 时新建一个短会话上下文：`[原始 user query, LLM 之前的错误输出, correction 指令]`
  - **不带主对话历史**（避免主对话被 retry 干扰、避免 LLM 混合旧内容 + 新内容输出多个 block）
  - LLM 响应就是新的 `<intent_challenge>` / `<answer_challenge>` block
- 最多重试 `answerChallengeMaxRetries` 次（默认 2）
- **Exhausted fallback（v0.9 明确）**：超过 retry 上限后：
  - 接受当前不完美的 output（**尽力解析能解析的部分**）
  - `SelfChallengeData` 里设置 `nodeARetryCount` 或 `nodeBRetryCount` 等于 maxRetries + 1（记录"exhausted"事实）
  - **UI**：Self-Challenge Card 顶部显示黄色警告徽标 "Structural validation failed after N retries"
  - **Statistics**：`SelfChallengeStatistics.RecordFallback(fallbackType)`
  - **行为不 block**：Agent 继续正常执行 —— 结构校验失败不应该阻塞用户任务；只是失去了 self-challenge 的一部分保护

**Retry Template T1 - Node A 结构校验失败**：

```
[SYSTEM] Your <intent_challenge> block failed structural validation:

Detected issues:
{issue_list}
  例如：
  - Missing Step 5 or <consistency_correction> block
  - Only 2 Interpretations found (minimum 3 required)
  - Interpretation 2 is shorter than 20 characters

Regenerate the FULL <intent_challenge> block with all 5 Steps + <consistency_correction>.
Do not skip any Step. Do not add prose outside these structured sections.
```

**Retry Template T2 - Node A Continuation 结构校验失败**：

```
[SYSTEM] Your <intent_challenge_continuation> block failed structural validation:

Detected issues:
{issue_list}

Regenerate the FULL <intent_challenge_continuation> block with Step 3-cont, Step 4-cont, Step 5-cont.
```

**Retry Template T3 - Node B 结构校验失败**：

```
[SYSTEM] Your <answer_challenge> block failed structural validation:

Detected issues:
{issue_list}
  可能的示例（v0.9 移除"generic term"语义判断，仅保留纯结构 issue）：
  - Only 2 Counter-Examples found (minimum 3 required)
  - Counter-Example 1 does not contain any <draft-quote>...</draft-quote> tag
  - The <draft-quote> content in Counter-Example 2 has only 5 characters (minimum 8 required)
  - The <draft-quote> content in Counter-Example 3 ("submesh count 42") does not exist as substring in draft_answer

Regenerate the FULL <answer_challenge> block ensuring all 4 Steps are present, each Counter-Example contains at least one <draft-quote> tag with content >= 8 characters that appears verbatim in draft_answer, and Verdict is one of PASS/REVISE/BLOCK.
```

**Retry 逻辑（伪代码）**：

```csharp
async Task<TChallenge> ParseWithRetry<TChallenge>(string llmOutput, int maxRetries)
{
    for (int attempt = 0; attempt <= maxRetries; attempt++)
    {
        var (parsed, issues) = TryParse(llmOutput);
        if (issues.Count == 0)
            return parsed;
        
        if (attempt == maxRetries)
        {
            // Fallback: accept the imperfect output but mark it
            parsed.MarkFallback(issues);
            EmitTelemetry("challenge_retry_exhausted", issues);
            return parsed;
        }
        
        // Compose correction prompt (T1/T2/T3 based on challenge type)
        var correctionPrompt = ComposeCorrection(issues);
        
        // Append to current LLM session and retry
        llmOutput = await llmClient.ContinueAsync(correctionPrompt);
    }
    throw new InvalidOperationException("unreachable");
}
```

### 11.6 Gap 7: Statistics 面板 UI 初版设计（v0.9 精简版）

**v0.9 立场**：v0.8 的 Statistics UI 塞了太多指标，用户根本不会仔细看。v0.9 简化为 **3 个关键指标 + 1 个 Health badge**，详细数据放折叠区域。

**位置**：`Editor/Config/Settings/Pages/UiDiagnosticsSettingsPage.cs` 里新增卡片 "Self-Challenge Statistics"。

**指标存储**：`Editor/Core/SelfChallenge/SelfChallengeStatistics.cs`（新建）—— ScriptableSingleton，累计最近 200 次 self-challenge 的数据；用户可点击"Clear"重置。

**UI 布局（v0.9 精简）**：

```
┌─ Self-Challenge Statistics ─────────────────────────────┐
│                                                         │
│ Sample size: 47 turns (of last 200 max)                 │
│ Overall health: [ ● OK ]   [Details ▼]                  │
│                                                         │
│ ─── 3 Key Metrics ──────────────────────────────────    │
│                                                         │
│ Node B PASS ratio:       68.4%    [healthy: 40-80%]     │
│ Clarification triggered: 14.3%    [healthy: 5-20%]      │
│ Correction retry exhausted: 0%    [healthy: <5%]        │
│                                                         │
│ [ Refresh ]   [ Clear All ]   [ Export CSV ]           │
└─────────────────────────────────────────────────────────┘

展开 [Details ▼] 后显示完整数据（Node A / Node B / Consistency 分布 / retry 分布 / draft 重新生成率等），
按需查看，不占用户主视野。
```

**Health badge 状态**：
- `● OK` 绿色 —— 所有 5 项健康阈值（§5.4）都通过
- `▲ WARNING` 黄色 —— 1~2 项异常
- `✕ FAILURE` 红色 —— 3+ 项异常（触发 §5.4 retrospective）

**为什么这么设计**：
- **首屏零信息压力**：3 个指标能一眼看懂；有问题时 Health badge 直接告诉用户
- **详细数据按需展开**：不打扰不需要的用户，但深度用户可以看
- **不做 Canary hit 相关展示**：v0.8 曾计划显示 `rubberStampHits` / `quoteEvasionHits` / `lazyCorrectionHits`——v0.9 全部取消（§11.7）

**SelfChallengeStatistics 类字段（v0.9 精简版）**：

```csharp
[Serializable]
public class SelfChallengeStatistics : ScriptableSingleton<SelfChallengeStatistics>
{
    // 存储最近 200 次 self-challenge 的原始数据
    [SerializeField]
    private List<SelfChallengeSummary> recentSamples = new();
    
    // v0.9：移除 rubberStampHits / quoteEvasionHits / lazyCorrectionHits（Canary 相关）
    // v0.9：移除 canaryProbesTriggered 计数
    
    public int SampleSize => recentSamples.Count;
    
    // 3 个 Key Metrics 的实时计算 property
    public double NodeBPassRatio { get; }
    public double ClarificationTriggerRatio { get; }
    public double RetryExhaustedRatio { get; }
    
    // Overall Health（基于 §5.4 的 5 项阈值判定）
    public HealthStatus OverallHealth { get; }
    
    public void RecordSelfChallenge(SelfChallengeData data);
    public void RecordFallback(FallbackType type);  // Retry exhausted 计数
    public void Clear();
    public string ExportCsv();
}

public enum HealthStatus { OK, Warning, Failure }
public enum FallbackType { NodeAStructural, NodeBStructural, NodeAContinuationStructural }
```

**Export CSV 格式**：每行一次 self-challenge，字段包括：`timestamp / turn_id / node_a_triggered / step5_verdict / node_b_triggered / verdict / node_a_retry_count / node_b_retry_count / fallback_type`。

**Refresh**：手动重算最近 200 turn 的聚合指标（默认每次 self-challenge 完成后自动更新）。

**Clear**：清空 `SelfChallengeStatistics` 历史（不会影响 SessionData 里已有的 SelfChallengeData，只是把 aggregate 计数重置）。

### 11.7 冲突 2 处理：Canary Probes 完全取消（v0.9 立场调整）

**v0.8 曾提出的 Canary Probes 机制在 v0.9 完全取消**。理由：

1. **P1/P2/P4 本质是关键词穷举**：
   - P1 检测"chosen 是否含 query 里没有的词" → 分词 + 词表对比
   - P2 检测"用户 query 是否含破坏性动词" → 动词穷举
   - P4 检测"draft 是否含 todo / TBD" → 关键词穷举
   - 这与 v0.4 起明确的"不穷举"立场直接冲突
2. **P5 fake correction 有反向污染风险**：如果 LLM 在训练中反复遇到"correction 是假的"，可能降低真实 correction 的可信度，影响 Node A/B 的核心机制
3. **UI Verdict 徽标已经提供首日观测能力**：Self-Challenge Card 的 `[v] PASS` / `[~] REVISED` / `[!] BLOCKED` 徽标非常显眼，用户在最初几次对话就能看到"是不是全部 PASS 变绿"这类信号，比工程侧关键词检测**更直观、更可靠**
4. **Canary 天花板本来就低**：v0.8 自己承认"只能检测明显 rubber-stamp"——**天花板低 + 违反立场 + UI 有替代 = 应该取消**

**首日体验问题的 v0.9 替代方案**：详见 §5.5 "首周引导条款"。核心思路 —— 通过**用户引导文档 + Self-Challenge Card 默认可见**代替 Canary Probes 的机器化检测。

**保留的观测机制**（v0.5/v0.6/v0.7 已有，不受本次取消影响）：

- Self-Challenge Card UI（默认折叠、异常自动展开、Verdict 徽标可见）
- Statistics 面板（Verdict 分布、interpretations 差异度、[Consistent] 比例、correction retry 数）
- 4 周验证条款 §5.4（5 项健康阈值判定）

**Canary Probes 相关字段/UI 全部清除**：

- `SelfChallengeStatistics` 中的 `rubberStampHits` / `quoteEvasionHits` / `lazyCorrectionHits` **删除**
- Settings UI 里 "Re-run canary probes" 按钮 **删除**
- Self-Challenge Card 顶部"黄色警告徽标"逻辑 **改为**：由 §5.4 的健康阈值触发（阈值超标即警告），不再由 canary hit 触发

### 11.8 v0.9 零 gap 交付清单

以下清单是**开发者按此开发即可覆盖全部实施细节**的检查表。开发时逐项完成后勾选。

**核心机制层**：
- [ ] `Editor/Core/SelfChallenge/IntentChallengeParser.cs` — Node A 输出解析 + 结构校验（含 Continuation 模式支持）
- [ ] `Editor/Core/SelfChallenge/IntentChallengeParser.cs` 里 `<consistency_correction>` 块解析（PASS/FAIL 判定 + corrected judgement）
- [ ] `Editor/Core/SelfChallenge/AnswerChallengeReviewer.cs` — Node B 组装压缩历史（详细规则见 §11.1）+ 独立 LLM 调用
- [ ] `Editor/Core/SelfChallenge/AnswerChallengeParser.cs` — Node B 输出解析 + `<draft-quote>` 长度与 substring 校验
- [ ] `Editor/Core/SelfChallenge/SelfChallengeSkipRules.cs` — 5 条 Skip 规则（R1-R5）unit test 覆盖
- [ ] Prompt 模板常量：`NodeAPromptTemplate` / `NodeAContinuationTemplate` / `NodeBReviewerTemplate` / `Correction_T1_T2_T3` 全部按 §11.5 硬编码
- [ ] Correction retry 循环：最多 `answerChallengeMaxRetries` 次；exhausted 时 fallback + 遥测

**数据层**：
- [ ] `Editor/Session/SelfChallengeData.cs` — 完整 schema（§11.2）
- [ ] `Editor/Session/SessionData.cs` 里 `MessageTurn` 新增 `selfChallenge` 字段
- [ ] 版本兼容验证：旧 session 反序列化 selfChallenge = null，UI 不渲染

**事件层**：
- [ ] `Editor/Core/MessageTypes.cs` 新增 3 个 AgentEventType（§11.3）
- [ ] `AgentEvent` 新增 `SelfChallenge` + `TurnId` 字段
- [ ] `AgentLoop.LLM.cs` 在 Node A 完成后 emit `IntentChallengeCompleted`
- [ ] `AgentLoop.Runner.cs` 在 Node B 完成后 emit `AnswerChallengeCompleted` / regenerate 前 emit `AnswerChallengeRegenerating`

**UI 层**：
- [ ] `Editor/UI/Components/SelfChallengeCard.cs` — 卡片组件（§3.5 完整设计）
- [ ] `AssistantTurnView.cs` 新增 `SetSelfChallengeCard(card)` 方法与 `_selfChallengeSlot` 挂载点
- [ ] `ChatWindow.Events.cs` 三个 event handler
- [ ] `ChatWindow.Messages.cs` 从 SessionData 重建 SelfChallengeCard（Domain Reload / Session 恢复）
- [ ] `SelfChallengeCard._userToggled` **不持久化**（§11.4）
- [ ] 反问 message 的专属 UI 样式（§3.6.5）

**状态机层**：
- [ ] `AgentState.WaitingForClarification` 枚举值 + 状态机分支
- [ ] `SessionData` / `DomainReloadState` 序列化状态
- [ ] Continuation 模式判定（§1.2.5）+ Continuation prompt 组装

**Statistics 层**：
- [ ] `Editor/Core/SelfChallenge/SelfChallengeStatistics.cs` — 累计最近 200 次数据的 ScriptableSingleton
- [ ] `SelfChallengeStatistics.RecordFallback(FallbackType)` 记录 correction retry exhausted
- [ ] Health badge 三态（OK/Warning/Failure）基于 §5.4 五项健康阈值实时计算
- [ ] Self-Challenge Card 顶部警告：仅由 §5.4 健康阈值超标触发（不再由 canary hit 触发）
- [ ] **v0.9 新增（Canary Probes 替代）**：首周引导（首次启动 tooltip + 前 5 次卡片强制展开 + README 引导文档）

**Settings 层**：
- [ ] `AgentCoreSettings` 添加 6 个新字段（§3.4，含 `legacySelfChallengeDisabled`）
- [ ] `UiDiagnosticsSettingsPage` 新增 "Self-Challenge Statistics" 卡片（§11.6 完整布局）
- [ ] "Re-run canary probes" 按钮

**测试与验证层**：
- [ ] `Editor/Tests/SelfChallenge/` 目录：Parser / SkipRules / ConsistencyCheck 全部 unit test
- [ ] `AnswerChallengeReviewer` 压缩历史组装的 integration test
- [ ] 手工回归：跑 §5.4 的 5 项健康阈值 sanity check（首次上线不做 4 周窗口，只做单次快速验证）

---

## 12. v0.8 变更前 review 清单（最终版）

**v0.7 已确认（v0.8 保留）的决策**：
- [x] 抛弃所有穷举式解法，只做 Self-Challenge
- [x] 两个节点（Node A 读需求时 + Node B 输出前）
- [x] 强制"3+ Interpretation / 3+ Counter-Example" 对抗 rubber-stamp
- [x] Node B 需要额外一次 LLM 调用（token 成本 +10~50%）
- [x] Node A Step 4 组合触发逻辑（歧义+严重 / 破坏+推断）
- [x] Step 5 LLM 自校验替代工程侧关键词校验
- [x] Node B 带压缩历史（取消 `useIsolatedReviewerContext`）
- [x] Node A 每轮 user message 都触发
- [x] Counter-Example `<draft-quote>` 结构化标记
- [x] Waiting-for-Clarification 无硬上限

**v0.8 新增决策（需 review 确认）**：
- [ ] 认可 Skip 判定 5 条规则（R1-R5）为"轻度启发式"，与"语义判断"边界清晰
- [ ] 认可 Continuation 模式（3 个精简 Step）覆盖澄清回复场景
- [ ] 认可 `SelfChallengeData` 完整 schema（约 25 个字段）作为序列化基线
- [ ] 认可 3 个 AgentEvent 定义 + payload
- [ ] 认可 Correction Retry 只做**追加式 retry**（不新开会话），最多 2 次
- [ ] 认可 `_userToggled` **不持久化**（Domain Reload 后重置）
- [ ] 认可 Canary Probes 5 类首日检测机制 + 3 hits 阈值告警
- [ ] 认可 Statistics 面板 UI 初版布局

**审计完成后 v0.8 状态**：**零 gap，可开工**。

Review 通过后本文件 v0.8 转为"实施中"，v1.5.0 正式开工。