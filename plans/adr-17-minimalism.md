# ADR-17: 极简即开即用哲学(Minimalism Everywhere)

> **状态**: 已定稿(2026-07-09)
> **决策人**: 项目 PO / 唯一用户
> **触发**: Self-Challenge 极简化(6 字段 → 1 字段)后, 用户明确要求将此哲学贯彻到全产品
> **影响文档**: 推翻 [`_archive/design/prompt-layer-hallucination-hardening-plan.md`](_archive/design/prompt-layer-hallucination-hardening-plan.md) v0.10 §3.4/§5/§7.1; 影响所有 Settings 相关设计文档

---

## 1. 核心哲学基线

**用户与 Agent 对话就能知道对不对**, 不需要暴露工程内部细节让用户"参与决策"。

### 5 条实施规则(写入 AGENTS.md 顶层)

1. **默认最优, 不问用户**: 若 80% 用户会选同一个值, 就写死为默认, **不给 UI 选项**
2. **一件事一个开关**: 一个功能不能有 6 个开关; 抽出总开关, 内部细节写死
3. **术语必须白话**: 严禁 "Node A" / "Fallback Routing" / "Token Budget" 等工程术语出现在**用户可见字段名**
4. **高级功能有 Advanced foldout**: 极少数用户真的需要, 默认折叠 + 明确警告标签
5. **可选服务用 ServiceCard 模式**: 服务默认关闭, 用户明确开启后才展开配置(现有 `DrawServiceCard(mem0/LightRAG)` 是范例)

---

## 2. 已锁定的具体决策

### 2.1 推翻的设计文档条款

- **v0.10 §3.4 (5+ 用户 Self-Challenge 字段)**: 推翻, 改为 1 个 `selfChallengeEnabled` 总开关
- **v0.10 §5 (Statistics 面板)**: 彻底删除, 不再实施
- **v0.10 §5.4 (4 周 kill criteria)**: 通过用户直接对话反馈判断, 不建 UI
- **v0.10 §7.1 (kill switch legacySelfChallengeDisabled)**: 彻底删除, 用总开关等效
- **v0.9 §5.5 (首周引导)**: 不做首次 tooltip, 不做卡片强制展开

### 2.2 AgentCoreSettings 字段清理清单

**彻底删除的字段**:
- `legacySelfChallengeDisabled`
- `intentChallengeEnabled`(内部字段, SelfChallenge 只用 `selfChallengeEnabled`)
- `answerChallengeEnabled`(同上)
- `answerChallengeMaxRetries`(常量化到 SelfChallengeConfig)
- `allowAgentClarificationQuestions`(常量化到 SelfChallengeConfig)
- `selfChallengeCardCountForcedExpansion`(常量化)
- `workspaceRootOverride`
- `unityRootRelativePathOverride`
- `userId`(deprecated, 使用系统生成)

**隐藏(HideInInspector)但保留数据兼容**:
- `maxToolCallRounds`(200 是硬安全网, 用户不需要看)
- `maxTokenBudget`(0=unlimited, 极工程化)
- `fallbackRoutingEnabled`(默认 true)
- `autoCompileCheck`(默认 true)
- `autoConsoleCapture`(默认 true)
- `maxConsecutiveErrors`, `toolFailWarningThreshold`, `toolFailBlockThreshold`, `allToolsFailBlockThreshold`
- `bootstrapEnabled`, `autoProjectContext`
- `maxContextTokens`, `reserveResponseTokens`
- `toolResultCompressionThreshold`, `toolResultTargetTokens`
- `conversationCompressionTrigger`
- `useSeparateCompressionLLM`, `compressionLLMEndpoint`, `compressionLLMModel`
- `streamingEnabled`, `showToolCallDetails`
- `toolScopingEnabled`
- `enableReasoningOutput`, `reasoningEffort`, `reasoningMaxTokens`, `extraRequestBody`
- `workspaceAutoDetectEnabled`(默认 true)
- `workspaceConfigVersion`(internal)
- `autoMemoryEnabled`, `autoMemoryMinTurns`
- `disabledToolCategories`, `disabledTools`(工具列表 UI 保持, 但字段内部化)

**保留 UI 暴露的字段(极简后剩 ~10 个)**:
- `llmEndpoint`, `llmModel`, `temperature`, `maxTokens` (基本 LLM)
- `selfChallengeEnabled` (Self-Challenge 总开关)
- `mem0Enabled` + endpoint(在新 External Enhancements 抽屉里)
- `lightragEnabled` + endpoint(同)
- `compressionEnabled`(总开关, 内部参数写死)
- `mem0Endpoint` / `lightragEndpoint`(仅在启用后显示)

### 2.3 UI 页面改造

#### Model & Agent 页面
- **Model Connection** 卡片: 保留 Endpoint / API Key / Model
- **Model = auto** 时显示 "→ (实际选中: xxx)" miniLabel
- **Generation** 卡片: 保留 Temperature / Max Tokens(用户明确要 Temperature 不折叠)
- **Agent Runtime** 卡片: 完全删除(Max Tool Rounds / Token Budget / Fallback Routing 全隐藏)
- **Self Correction** 卡片: 完全删除(Auto Compile / Console Capture / Max Errors 全隐藏)
- **Self-Challenge** 卡片: 保留 1 个 `Enable Self-Challenge` toggle(已完成)

#### Context & Memory 页面
- **Context Sources** 卡片: 完全删除
- **Context Budget** 卡片: 完全删除
- **Compression** 卡片: 简化为 1 个 `Enable Compression` toggle
- **Separate Compression LLM** 卡片: 完全删除
- **Memory Service (mem0)** 卡片: **移到 Tools & Extensions 页面的 External Enhancements 抽屉**
- **Knowledge Base (LightRAG)** 卡片: **同上, 移到 External Enhancements**

#### UI & Diagnostics 页面
- **Chat UI** 卡片: 完全删除(Streaming / Show Tool Call Details)
- **Diagnostics** 卡片: 保留
- **Maintenance** 卡片: 保留
- **底部新增版本号显示**(如 "AgentCore v1.5.0-alpha1")

#### Workspace 页面
- **Detection Actions** 卡片: 保留
- **Workspace Overview** 卡片: 保留(只读展示)
- **Workspace Root Override / Unity Root Relative Path**: 删除

#### Tools & Extensions 页面
- **工具列表 UI**: 保持现状
- **新增 "External Enhancements" 抽屉**: 默认折叠, 承载:
  - `Memory Service (mem0)` — 文案改为 "跨会话长期记忆"
  - `Knowledge Base (LightRAG)` — 文案改为 "项目文档搜索增强"

#### Dashboard 页面
- 保持现状(导航型内容)
- 底部添加版本号显示

### 2.4 Chat 窗口相关

- **ChatWindow 4 层结构**(ThinkingDrawer / SelfChallengeCard / ToolCallGroup / MessageBubble): **保持现状**, 折叠策略已足够精简
- **SelfChallengeCard 术语中文化**: 对齐 [`ChatWindow.Events.cs`](../Editor/UI/ChatWindow.Events.cs) 状态标签风格
  - "Interpretation" → "可能的理解"
  - "Chosen" → "选定的理解"
  - "Ambiguity Signal" → "歧义信号"(保留)
  - "Verdict PASS/REVISE/BLOCK" → "通过 / 修正过 / 阻止"
  - "Awaiting Clarification" → "等待你的澄清"
  - "Intent OK" → "意图明确"
  - "Skipped" → "已跳过"
- **Statistics 面板 UI**: 彻底不做

### 2.5 PROJECT.md 相关

- **保留提示**: QUICK_START.md 继续提到"建议在项目根目录创建 PROJECT.md"
- **新增"自动生成 PROJECT.md 模板"按钮**: 位置放 Workspace 页面或 Dashboard
- **Agent 权限**: Agent 通过 `manage_file` 工具**可以完全创建/编辑/重写** PROJECT.md, 无权限边界

### 2.6 默认值 & 内部常量

- **LLM Endpoint 默认**: 保持 `http://172.16.248.60:8000/v1`(项目组内网, 不改)
- **快捷键**: `Ctrl+Shift+Q`(不改)
- **Debug 日志级别**: 默认关闭

### 2.7 Session / Log 相关

- **Session 恢复失败**: **保持 warning, 不 silent**; 文案**保持技术化**(`[AgentCore] SessionStorage: Session file not found: <path>`)
- **CHANGELOG.md**: 保持技术化(面向开发者)
- **README.md**: 未来简化(明确边界后)

### 2.8 高风险操作

- **确认对话框**: **保留**, 高风险操作明确用户知情
- Agent 完全自主, 但用户对破坏性操作保留最终否决权

### 2.9 QUICK_START.md 清理

- **移除 §4 Scripting Define Symbols 提示**(`AGENTCORE_VCS`/`AGENTCORE_INDEXING`): 用户在 Settings 里 toggle 即可, 自动加符号
- 保留 §注意事项中的 "PROJECT.md 建议"

---

## 3. 未回答的边界问题(记录, 后续 iteration 处理)

- **QUICK_START.md 里的"如何获取 API Key"**: 用户明确不加链接, 但未来若有客服文档可指向
- **首次打开 ChatWindow 引导**: 不加(用户明确)
- **未来 Advanced foldout 允许否**: 用户没明确, 但 Temperature 保留主 UI 意味着**部分工程字段可以主 UI 保留**, 不是绝对零暴露

---

## 4. 明早执行清单(有序)

### P0(核心, 3-4 小时)

1. `AgentCoreSettings.cs`:
   - 删除 9 个字段(`legacySelfChallengeDisabled`, `intentChallengeEnabled`, `answerChallengeEnabled`, `answerChallengeMaxRetries`, `allowAgentClarificationQuestions`, `selfChallengeCardCountForcedExpansion`, `workspaceRootOverride`, `unityRootRelativePathOverride`, `userId`)
   - 相关 [Header] / [Tooltip] 注释一并清理
   - 保留字段全部加 `[HideInInspector]`(除 §2.2 UI 暴露列表)
   - v17→v18 migration: 迁移旧数据, 清理 orphan 字段
2. `SelfChallengeConfig.cs`: 增加常量:
   - `NodeARetryMax = 2`
   - `AllowClarificationQuestions = true`
   - `CardForcedExpansionCount = 5`
3. `AgentLoop.SelfChallenge.cs` / `AgentLoop.Runner.cs`:
   - 清理所有对已删除字段的引用, 改用 `SelfChallengeConfig.*` 常量
   - 删除注释里的 `legacySelfChallengeDisabled` 提及
4. `ModelAgentSettingsPage.cs`: 删除 Agent Runtime + Self Correction 卡片; Model 添加 "→ (实际选中: xxx)" miniLabel
5. `ContextMemorySettingsPage.cs`: 删除 Context Sources / Context Budget / Compression LLM 卡片; 简化 Compression 到 1 toggle; **移除 Memory Service 和 Knowledge Base 卡片**
6. `UiDiagnosticsSettingsPage.cs`: 删除 Chat UI 卡片; 添加版本号显示到 Maintenance 卡片底部
7. `WorkspaceSettingsPage.cs`: 删除 Override 字段渲染
8. `ToolsExtensionsSettingsPage.cs`: 新增 "External Enhancements" 抽屉, 承载 mem0 和 LightRAG ServiceCard(文案白话化)

### P1(UI 细化, 2-3 小时)

9. `SelfChallengeCard.cs`: 术语中文化(§2.4 列表)
10. `AgentEvent` / `AgentState` 相关文案对齐 ChatWindow 状态标签风格
11. Dashboard 页面添加版本号显示
12. Workspace 页面 / Dashboard 添加"自动生成 PROJECT.md 模板"按钮

### P2(文档, 1 小时)

13. `QUICK_START.md`: 移除 §4 Scripting Define 提示
14. `AGENTS.md`: 加入 5 条极简哲学规则(§1)
15. `ROADMAP.md`: 加入 ADR-17 摘要, 引用本文档
16. `CHANGELOG.md`: 记 v1.5.0-alpha1 变更(推翻 v0.10 部分立场, 简化 Settings, Statistics 未做)
17. 归档 [`self-challenge-implementation-report.md`](self-challenge-implementation-report.md) Stage 10 部分

### 编译自查

- 所有对已删除字段的引用应换成常量或删除
- Grep `legacySelfChallengeDisabled` / `intentChallengeEnabled` / `answerChallengeEnabled` / `workspaceRootOverride` / `unityRootRelativePathOverride` / `userId` 应全部无结果(除 migration 代码)
- Grep `Statistics` / `selfChallengeCard` 相关未引用代码可清理

---

## 5. 参考

- [`_archive/analysis/minimalism-audit-report.md`](_archive/analysis/minimalism-audit-report.md) — 触发本 ADR 的全产品审计
- [`_archive/design/prompt-layer-hallucination-hardening-plan.md`](_archive/design/prompt-layer-hallucination-hardening-plan.md) — 被推翻的上游设计文档 v0.10（已归档）
- [`_archive/features/self-challenge-implementation-report.md`](_archive/features/self-challenge-implementation-report.md) — Self-Challenge 极简化实施记录
- [`ROADMAP.md`](ROADMAP.md) ADR-10 — 规则系统废弃案例, 类似哲学决策

---

## 6. 挑战自查

**做得好**:
- 所有决策已锁定, 明早无二义性
- 分了 P0/P1/P2, 优先级清晰
- 保留了 Session 恢复 warning + 高风险确认 + CHANGELOG 技术化 = 极简哲学不搞过头, 保留必要透明度

**可能被质疑**:
- 删除 9 个字段 + 20+ [HideInInspector] 会**大幅缩小 Settings 面板**, 老用户(如你自己)可能会**找不到某个原本的 setting**
- ADR 记录的是**当下决策**, 未来如果用户量增长, 可能需要**引入 Advanced foldout 或 CLI 配置文件**才能满足专业用户

**明早开工前需要你确认的最后一件事**: 是否**先备份现有分支/tag**? 一次删 30+ 字段 + 5 个 UI 页面重构, **回滚成本比 iteration 高**, 建议 `git tag pre-adr-17` 或类似标记。
