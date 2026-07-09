# Self-Challenge 完整实施报告 v1.5.0-alpha1

> **日期**: 2026-07-08 深夜实施完成
> **状态**: 全量代码已交付, **未在 Unity 编辑器内验证编译通过**, 存在需明早人工修复的风险点
> **完成范围**: Stage 2 (Node A) + Stage 3 (Correction retry) + Stage 4 (WaitingForClarification) + Stage 5 (Continuation) + Stage 6 (Node B) + Stage 7 (REVISE) + Stage 8 (UI Card) + Stage 9 (主历史清理)
> **未完成**: Stage 10 (Statistics 面板 UI + 首周引导 tooltip)
> **符合的用户决策**: 妥协 C(全量含 UI, 接受明早需 2-4 小时改错)

---

## 1. 新增文件清单

### 生产代码 (SelfChallenge 核心)

| 文件 | 职责 | 行数 |
|---|---|---|
| [`Packages/com.agentcore/Editor/Core/SelfChallenge/IntentChallengePromptBuilder.cs`](../Editor/Core/SelfChallenge/IntentChallengePromptBuilder.cs) | Node A prompt 生成(完整 + Continuation + 结构 retry) | ~180 |
| [`Packages/com.agentcore/Editor/Core/SelfChallenge/IntentChallengeStreamExtractor.cs`](../Editor/Core/SelfChallenge/IntentChallengeStreamExtractor.cs) | Node A 流式抽取器, 支持 Full/Continuation 双模式 | ~250 |
| [`Packages/com.agentcore/Editor/Core/SelfChallenge/IntentChallengeParser.cs`](../Editor/Core/SelfChallenge/IntentChallengeParser.cs) | Node A 结构校验 + 字段填充 | ~430 |
| [`Packages/com.agentcore/Editor/Core/SelfChallenge/AnswerChallengePromptBuilder.cs`](../Editor/Core/SelfChallenge/AnswerChallengePromptBuilder.cs) | Node B Reviewer prompt + REVISE feedback + retry prompt | ~130 |
| [`Packages/com.agentcore/Editor/Core/SelfChallenge/AnswerChallengeStreamExtractor.cs`](../Editor/Core/SelfChallenge/AnswerChallengeStreamExtractor.cs) | Node B 流式抽取器 | ~150 |
| [`Packages/com.agentcore/Editor/Core/SelfChallenge/AnswerChallengeParser.cs`](../Editor/Core/SelfChallenge/AnswerChallengeParser.cs) | Node B 结构校验 + draft-quote substring 校验 + verdict 判定 | ~230 |
| [`Packages/com.agentcore/Editor/Core/AgentLoop.SelfChallenge.cs`](../Editor/Core/AgentLoop.SelfChallenge.cs) | AgentLoop partial: SelfChallenge 集成层(字段/触发/retry/Node B invoke/主历史清理) | ~590 |
| [`Packages/com.agentcore/Editor/UI/Components/SelfChallengeCard.cs`](../Editor/UI/Components/SelfChallengeCard.cs) | UI 卡片: Verdict 徽标 + 摘要 + 可展开完整块 | ~300 |
| [`Packages/com.agentcore/Editor/UI/ChatWindow.SelfChallenge.cs`](../Editor/UI/ChatWindow.SelfChallenge.cs) | ChatWindow partial: SelfChallenge 事件 → UI card 路由 | ~40 |

**总计新增**: 约 2300 行

### 计划文档

- [`Packages/com.agentcore/plans/self-challenge-stage-plan.md`](self-challenge-stage-plan.md) — Stage 拆分蓝图
- [`Packages/com.agentcore/plans/self-challenge-implementation-report.md`](self-challenge-implementation-report.md) — 本文档

---

## 2. 修改文件清单

| 文件 | 变更 |
|---|---|
| [`Packages/manifest.json`](../../manifest.json) | 增加 `com.unity.test-framework@1.1.33` + `testables: ["com.agentcore.unity"]` |
| [`Packages/com.agentcore/Editor/AssemblyInfo.cs`](../Editor/AssemblyInfo.cs) | 新增 `InternalsVisibleTo("AgentCore.Tests.Editor")` |
| [`Packages/com.agentcore/Editor/UI/ChatWindow.cs`](../Editor/UI/ChatWindow.cs) | 快捷键 `%#a` → `%#q` (Ctrl+Shift+Q) |
| [`Packages/com.agentcore/Editor/Core/SelfChallenge/SelfChallengeConfig.cs`](../Editor/Core/SelfChallenge/SelfChallengeConfig.cs) | `internal` → `public` 可见性提升 |
| [`Packages/com.agentcore/Editor/Core/MessageTypes.cs`](../Editor/Core/MessageTypes.cs) | 新增 `AgentState.WaitingForClarification` 枚举值 |
| [`Packages/com.agentcore/Editor/Core/AgentLoop.cs`](../Editor/Core/AgentLoop.cs) | `SendMessageAsync`: 允许 WaitingForClarification 入口 + Node A instruction 注入 |
| [`Packages/com.agentcore/Editor/Core/AgentLoop.LLM.cs`](../Editor/Core/AgentLoop.LLM.cs) | `HandleContentToken` 前置 SelfChallenge extractor; `CallLLMStreamAsync` 加入 Node A correction retry |
| [`Packages/com.agentcore/Editor/Core/AgentLoop.Runner.cs`](../Editor/Core/AgentLoop.Runner.cs) | `PrepareAssistantMessageForHistory` 剥离 challenge 块; `HandleFinalResponse` 触发 Node A 分派与 Node B; 新增 REVISE 重生成 |
| [`Packages/com.agentcore/Editor/UI/Components/AssistantTurnView.cs`](../Editor/UI/Components/AssistantTurnView.cs) | 新增 `_selfChallengeSlot` + `EnsureSelfChallengeCard(id)` |
| [`Packages/com.agentcore/Editor/UI/ChatWindow.Events.cs`](../Editor/UI/ChatWindow.Events.cs) | 增加 SelfChallenge 事件 case + `WaitingForClarification` state case |

---

## 3. 使用方式(明早测试步骤)

### 3.1 开启功能

1. 打开 Unity, 让 Domain Reload 完成
2. `Edit > Preferences > AgentCore` 或类似位置的 `AgentCoreSettings`
3. 找到 `Self-Challenge (Phase 9 — experimental)` 分组
4. 勾选:
   - `intentChallengeEnabled = true`
   - `answerChallengeEnabled = true` (可选; 会产生额外 LLM 调用)
   - `allowAgentClarificationQuestions = true`
   - 保持 `legacySelfChallengeDisabled = false`
5. 保存

### 3.2 触发验证

**测试场景 1: 明确 query, Node A 直接执行**
- 发送: "帮我获取当前场景中选中 GameObject 的第一个 material 的 shader 名称"
- 预期: Node A 完成后 `Step 4 Conclusion = DirectExecute`, 正常执行 tool 调用
- UI: 每个 assistant turn 上方出现 SelfChallengeCard(默认折叠), Verdict 徽标绿色 `[v] Intent OK`

**测试场景 2: 模糊 query, Node A 反问**
- 发送: "删除老资源"
- 预期: LLM 输出 `[CLARIFICATION NEEDED]` 反问, Agent 进入 WaitingForClarification 状态
- UI: SelfChallengeCard 自动展开, Verdict 徽标蓝色 `[?] Awaiting Clarification`
- 状态: 输入框标签变为"等待你的澄清..."
- 后续: 你回复澄清后, 走 Continuation 模式(Step 3-cont / 4-cont / 5-cont)

**测试场景 3: 短消息 skip**
- 发送: "好的"
- 预期: R1 skip 生效, 无 Node A prompt 注入, 行为等同 v1.4.9

**测试场景 4: 纯 URL skip**
- 发送: "https://example.com"
- 预期: R3 skip 生效

**测试场景 5: Node B (需开启 answerChallengeEnabled)**
- 发送: "解释 Unity 中 MonoBehaviour 的生命周期"(触发一个 >50 字符的 non-tool-call 回复)
- 预期: draft 生成后触发 Node B Reviewer 独立 LLM 调用; SelfChallengeCard 出现 `[v] PASS` / `[~] REVISED` / `[!] BLOCKED` 徽标
- REVISE 场景: assistant message 内容会被替换(重新生成), SelfChallengeCard 显示 `[regenerated]` 标记

### 3.3 关闭功能验证行为退化

1. 把 `legacySelfChallengeDisabled = true` 或 `intentChallengeEnabled = false` 都清掉
2. 行为应完全等同 v1.4.9(无 Node A prompt 注入, 无 SelfChallengeCard 渲染)

---

## 4. 已知风险 & 明早自查清单

### 4.1 极大概率出现的编译错误

- [ ] **命名空间引用缺失**: 我的新代码 include 了 `AgentCore.Editor.Core.SelfChallenge` 和 `AgentCore.Editor.Config`, 若有旧代码没引用可能报错 → 在报错文件顶部加 `using`
- [ ] **`ChatMessage.Role` 大小写**: 我用 `m.Role == "user"`, 现有代码若用小写 `"user"` 则匹配, 若有大写要小心 → 现有 [`AgentLoop.cs:615`](../Editor/Core/AgentLoop.cs:615) 用小写 `"system"`, 保持一致
- [ ] **`ChatCompletionResponse.Message.Content`**: 已确认字段存在
- [ ] **UI Toolkit 版本差异**: `TextField.multiline`, `TextField.isReadOnly`, `TextField.SetValueWithoutNotify` 应在 Unity 2021.3+ 有效, 若报错在 [`SelfChallengeCard.cs`](../Editor/UI/Components/SelfChallengeCard.cs) 里替换

### 4.2 潜在运行时问题

- [ ] **Node A prompt 会导致模型对短查询也进行 self-challenge**: 若 skip rules 命中 → 不注入; 但若 LLM 特别喜欢自主开始 self-challenge, 也不影响流程
- [ ] **`<intent_challenge>` marker 冲突**: 若 LLM 因为看到该 marker 而**在 challenge 块外重复输出**, VisiblePlanningTraceExtractor 会照常处理剩余部分, 但用户体验会有奇怪残留
- [ ] **Node B fire-and-forget**: `TriggerNodeBAsync` 是异步不阻塞, `AssistantMessage` 事件已发出 → UI 更新可能会**滞后于** SelfChallenge 事件到达, 或者相反, UI 需要正确处理"card 先出现, message 后到"的乱序
- [ ] **REVISE 分支替换主历史 assistant message**: 我用 `_messages[i] = newMessage`, 若消息压缩逻辑并发操作可能出问题
- [ ] **中英文正则兼容**: IntentChallengeParser 的 `Step2HeaderRegex` 等支持了中英文双写, 但**LLM 可能用简写或 emoji**, 需要观察实际输出决定是否放宽正则

### 4.3 未完成的部分(明早/后续 iteration)

- [ ] **Stage 10 Statistics 面板**: 未实施; 但 Debug.Log 每次都会输出 SelfChallenge 摘要, 你可以从 Console 观察
- [ ] **Stage 10 首周引导 tooltip**: 未实施
- [ ] **Correction retry exhausted 时的 UI 徽标警告**: 数据字段 (`NodeARetryCount`) 已填充, 但 UI 未渲染"黄色警告徽标"
- [ ] **Domain Reload 恢复 SelfChallengeCard**: `SessionData.SelfChallenge` 已通过 Newtonsoft.Json 序列化, 但 Domain Reload 后需要 `RebuildMessageBubbles` 里的代码显式调用 `EnsureSelfChallengeCard + SetData`, 我**未做此路径**, 需要明早补
- [ ] **WaitingForClarification 状态未写入 DomainReloadState**: v0.10 §0.5 相关的兜底未做完整; 若在澄清等待中发生 domain reload, 可能状态丢失
- [ ] **单元测试**: 未新增单元测试(骨架 22 个 SelfChallengeSkipRulesTests 仍有效); 明早如果决定加, 建议先测 `IntentChallengeParser.Parse` 的 pass/fail 场景 + `IntentChallengeStreamExtractor` 的流式抽取

### 4.4 需要设计确认的边界

- [ ] **Node B 是否总是与 Node A 联动**: 当前实现是**独立开关**(`intentChallengeEnabled` 和 `answerChallengeEnabled`)。若你希望必须先启用 Node A 才能启用 Node B, 需加校验
- [ ] **Node A `<consistency_correction>` 若含 nested XML tag**: 抽取器可能误判为嵌套; 目前用 `IndexOf(openMarker, offset)` 检测嵌套, 但**如果 LLM 在 correction 里引用了 `<intent_challenge>`**, 会误判为 Invalid → 需要观察

---

## 5. 快速回滚方案

如果明早发现代码不可用, 有 3 种回滚路径:

### A. Nuclear option — 完全回滚
```bash
cd Packages/com.agentcore
git checkout HEAD -- Editor/
```
恢复到 v1.4.9 骨架, 只保留骨架修复 1 (SelfChallengeConfig public) 和快捷键修改。

### B. 只回滚 UI, 保留 backend
删除:
- [`Editor/UI/Components/SelfChallengeCard.cs`](../Editor/UI/Components/SelfChallengeCard.cs)
- [`Editor/UI/ChatWindow.SelfChallenge.cs`](../Editor/UI/ChatWindow.SelfChallenge.cs)
- 回退 [`AssistantTurnView.cs`](../Editor/UI/Components/AssistantTurnView.cs) 的修改
- 回退 [`ChatWindow.Events.cs`](../Editor/UI/ChatWindow.Events.cs) 的 SelfChallenge case

Backend 通过 Debug.Log 依然可观察。

### C. Feature flag 关闭
不改代码, 只在 AgentCoreSettings 里:
- `intentChallengeEnabled = false`
- `answerChallengeEnabled = false`
- `legacySelfChallengeDisabled = true`(硬关闭)
行为等同 v1.4.9。

---

## 6. 阶段完成映射

| Stage | ROADMAP 任务 | 完成情况 |
|---|---|---|
| Stage 2 (Node A) | 9.1.1 前半 | ✅ prompt + 流式抽取 + 结构校验 + 字段填充 |
| Stage 3 (Correction retry) | 9.1.1 后半 | ✅ 独立小会话 retry, exhausted fallback |
| Stage 4 (WaitingForClarification) | 9.1.3 | ⚠️ 状态机 + RunToolCallLoopAsync 拦截 ✅; DomainReloadState 序列化未做 |
| Stage 5 (Continuation) | 9.1.5 | ✅ prompt + 抽取 + parser + [TOPIC CHANGE DETECTED] 降级 |
| Stage 6 (Node B) | 9.1.2 | ✅ reviewer prompt + 独立 LLM 调用 + 抽取 + draft-quote substring 校验 + verdict |
| Stage 7 (REVISE 重生成) | 9.1.4 | ✅ feedback prompt + 主历史替换 + v0.10 §0.4 单次不复审 |
| Stage 8 (UI Card) | 9.1.7 | ⚠️ 极简版可用; 无 Statistics; 无首周强制展开 |
| Stage 9 (主历史清理) | 9.1.6 + 9.1.8 | ✅ `StripChallengeBlocks` 在 `PrepareAssistantMessageForHistory` 中; ⚠️ Domain reload 兜底未做 |
| Stage 10 (Statistics/引导) | 9.1.10 + 9.2.1-2 | ❌ 未实施 |

---

## 7. 我 hand-off 给你的诚实评估

**做得好的**:
- 骨架修复 + Stage 计划文档写得比较扎实
- 遵守 AGENTS.md 的"结构校验只做纯格式, 语义交给 LLM"立场
- SelfChallenge partial 集中在一个文件, 便于回退和 review
- 主历史清理(v0.10 §0.6)在 Stage 6 之前就做好, 避免了长对话 token 累积

**做得不够好**:
- **未实际编译一次**, 可能有编译错误; 明早预估 30 分钟到 2 小时改错
- Node B 是 fire-and-forget, UI 事件顺序可能有 race condition
- 单元测试**没写**任何新的; 只依赖现有 22 个 skip rules 测试保底
- Domain reload 兜底(v0.10 §0.5)只做了一半

**需要你决策**:
- Statistics 面板要不要做, 什么时候做
- 首周引导 tooltip 要不要做
- Node A 与 Node B 是否需要联动开关

---

## 8. 参考文档

- [`prompt-layer-hallucination-hardening-plan.md`](prompt-layer-hallucination-hardening-plan.md) v0.10 — 唯一权威设计文档
- [`self-challenge-stage-plan.md`](self-challenge-stage-plan.md) — 我今晚生成的 Stage 拆分蓝图
- [`ROADMAP.md`](ROADMAP.md) §3.y Phase 9 — 官方任务表
- [`../CHANGELOG.md`](../CHANGELOG.md) v1.4.9 — 骨架交付记录; 明早请追加 v1.5.0-alpha1 条目
