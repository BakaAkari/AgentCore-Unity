# Prompt 层精读（只读）— 压缩 / Memory / AskUser / PROJECT.md

> 日期：2026-08-11
> 性质：**只读分析**，未改任何提示词/行为。基于 v1.14.13（deepseek 逃逸已落地）。
> 范围：`CompressionPrompts.cs`、`AgentLoop.Memory.cs` + `AutoMemoryStrategy.cs`、`AgentLoop.AskUser.cs`、`BootstrapContext.cs` + `ProjectContextCollector.cs`

---

## 结论先行

这四个区域**整体健康，均不是过度思考/决策/工具调用的驱动源**。过度思考的直接根因（Self-Challenge → 意图挑战叠加 native thinking）已由 v1.14.13 的 deepseek 逃逸处理，这四个区域没有新的高价值改动点。

**建议：不在这些区域做行为改动，先等 Unity 实测 v1.14.13 的 3 个信号**（是否还过度 ask_user/解释、reasoning 质量、工具调用正确性），再决定是否要碰 SOUL。

---

## 一、压缩（CompressionPrompts.cs / ConversationCompressor.cs）

**状态**：结构干净。占位符 `{0}/{1}/{2}` 在 `ConversationCompressor.cs:343-344` 用 `string.Format` 正确装配（已核实，非死代码）。

**值得肯定的设计**：
- `ConversationCompressionSystem` rule 3：`Remove: ... intermediate reasoning` —— 摘要主动**去掉中间推理链**，与"防止推理模型把冗长思考带进下轮"的目标一致，抑制过度思考蔓延。
- rule 6/K：`Never fabricate information`，防摘要污染原始事实。

**观察点（均低风险，无需现在动）**：
- tool-result 压缩与对话压缩分开，语义清晰。
- 压缩本身是独立 LLM 调用；对推理模型，摘要"去推理链"理论上可能迫使主模型重推导 → 但这是**需要实测**的行为假设，非代码缺陷。当前规则方向正确，先不介入。

## 二、Memory

**a) 召回注入（AgentLoop.Memory.cs `FormatMemoriesAsContext`）**
- 以 `[历史记忆 - ...]` 前缀 + `[请参考以上记忆辅助回答，但以当前对话上下文为准]` 收尾。**软引用、明确以当前对话为准** —— 合理的记忆注入口吻，不驱动过度思考。
- 上限 `MemoryContextMaxChars`，截断有序。

**b) 自动记忆提取（AutoMemoryStrategy.cs `ExtractionPrompt`）**
- 这是做得**很好**的一段：明确"忽略临时操作细节"、"最多 5 条"、"无可记内容返回空数组" —— 主动防记忆膨胀/污染。
- 小瑕疵（低）：示例硬编码 Unity（URP/Unity 2022.3），对纯 Unity 场景无害，但对多供应商/非 Unity 上下文是无意义特化。不值得为它改。

## 三、AskUser（AgentLoop.AskUser.cs）

**状态**：这是消息泵机制，非提示词文件。唯一提示级文本是唤醒续接：
```
[针对你刚才通过 ask_user 提出的问题："..."] 我{方式}：{answer} 请据此继续之前的任务。
```
功能正确（R1 定案：不补第二个 tool result，改为追加 user 消息）。**不过度**。过度 ask_user 由 SOUL §2.1/§2.2 的措辞驱动（已在前轮报告分析），**属 SOUL 范畴 → 等你 Unity 实测后决定**，不在本轮只读范围落地。

## 四、PROJECT.md（BootstrapContext.cs + ProjectContextCollector.cs）

**状态**：**架构优秀** —— PROJECT 自动信息 + PROJECT.md 用户内容都是 **deferred（首轮注入）**，不常驻 system prompt（`CompileSystemPrompt` 只含 SOUL+SOUL.ext+TOOLS 协调）。这本身就是对"上下文过大"的结构性缓解，与 SOUL §5 引用 PROJECT.md 的方式一致。

**观察点（低风险）**：
- `ProjectContextCollector.Collect()` 任一异常会被捕并注入 `[WARN] 项目信息收集部分失败: {ex}` 到上下文 —— 瞬时失败变成持久上下文噪音（仅降级路径，正常时无）。
- 目录树 depth=2 / 包列表等有规模和缓存上限（heavy scan 5min 缓存），不会 token 爆炸。

---

## 建议

1. **本轮无行为改动**。四个区域健康，没有高价值的提示词冗余/缺陷值得现在动。
2. **把改动优先级留给 SOUL（§0 对推理模型弱化、§2.13 速查表）**，但那必须建立在 Unity 实测基线之上，与你当前正在做的 v1.14.13 验证**串行**执行，避免无法归因。
3. 你在 Unity 实测 v1.14.13 时重点看 3 个信号；若逃逸后仍有过度 ask_user/解释，再回来看 SOUL §0/§2，而不是这四个区域。
