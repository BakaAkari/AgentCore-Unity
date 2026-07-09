# AgentCore 极简即开即用哲学 — 全产品审计报告

> **日期**: 2026-07-08
> **审计边界**: AgentCoreSettings + 6 个 Settings Pages + 12 个 plans 设计文档 + QUICK_START.md
> **哲学基准**: 用户装了就能用, 零配置默认最优行为, 只有必要时才暴露选项
> **上游触发**: SelfChallenge 从 6 字段简化到 1 字段(方案乙), 用户希望将此哲学贯彻全产品

---

## 1. 核心结论

**AgentCore 目前的 Settings 面板严重违反极简哲学**, 主要问题有 4 类:

| 问题类型 | 数量 | 严重度 |
|---|---|---|
| 用户永远不需要看到的工程细节 | 15+ 字段 | 🔴 |
| 双开关冗余(总开关+分开关) | 3 处 | 🟡 |
| 只有资深用户才用得上的高级参数 | 8 字段 | 🟡 |
| Advanced feature 默认关闭却必要 | 3 处 | 🔴 |

**Self-Challenge 之外, 还有 25+ 个 UI 字段应当移入内部或彻底移除**, 现有 Settings 面板对新用户构成"配置地狱", 与"打开就能用"的产品定位严重脱节。

---

## 2. Settings 面板字段全景审计

### 2.1 Model & Agent 页面 (5 个卡片, 约 12 个字段)

| 字段 | 位置 | 极简哲学诊断 | 建议 |
|---|---|---|---|
| **Endpoint** | Model Connection | ✅ 必要 | 保留 |
| **API Key** | Model Connection | ✅ 必要 | 保留 |
| **Model** | Model Connection | ✅ 必要(可 auto-fetch) | 保留但优化 auto |
| **Temperature** (0-2) | Generation | 🟡 高级参数 | **默认收起**, 高级 foldout |
| **Max Tokens** (16000) | Generation | 🟡 高级参数 | **默认收起** |
| **Max Tool Rounds** (200) | Agent Runtime | 🔴 用户理解不了 | **隐藏**, 内部常量 |
| **Token Budget** (0=unlimited) | Agent Runtime | 🔴 极工程化 | **隐藏** |
| **Fallback Routing** | Agent Runtime | 🔴 术语难懂 | **隐藏**, 默认开 |
| **Auto Compile Check** | Self Correction | 🔴 默认应开, 不用问用户 | **隐藏** |
| **Auto Console Capture** | Self Correction | 🔴 同上 | **隐藏** |
| **Max Consecutive Errors** | Self Correction | 🔴 极工程化 | **隐藏**, 内部常量 |
| **Enable Self-Challenge** ✅ | Self-Challenge | ✅ 已做极简化 | 保留 |

**Model & Agent 减字段**: 12 → **4 (保留 4 个 essential)**, 其他放 Advanced foldout 或内部。

### 2.2 Context & Memory 页面 (4-5 个卡片, 约 15 个字段)

| 字段 | 位置 | 诊断 | 建议 |
|---|---|---|---|
| **Bootstrap Files** | Context Sources | 🟡 术语难懂 | **隐藏**, 默认开 |
| **Auto Project Context** | Context Sources | 🟡 同上 | **隐藏**, 默认开 |
| **Max Context Tokens** (0=auto) | Context Budget | 🔴 auto 已足够 | **隐藏** |
| **Reserve Response Tokens** | Context Budget | 🔴 极工程化 | **隐藏** |
| **Enable Compression** | Compression | 🟡 高级 | **隐藏**, 默认开 |
| **Threshold** (2000) | Compression → Tool Result | 🔴 极工程化 | **隐藏** |
| **Target** (500) | Compression → Tool Result | 🔴 极工程化 | **隐藏** |
| **Trigger Ratio** (0.7) | Compression → Conversation | 🔴 极工程化 | **隐藏** |
| **Separate Compression LLM** | Compression LLM | 🔴 极小众高级功能 | **隐藏或收进 foldout** |
| Compression LLM Endpoint/Model/Key | Compression LLM | 同上 | **隐藏** |
| **Memory Service (mem0)** | Memory Service | ✅ 用户需要选 | 保留(可选服务) |
| **mem0 Endpoint/Key** | Memory Service | ✅ mem0 开启时需要 | 保留 |
| **Auto Memory (advanced)** foldout | Memory Service | ✅ 已经 foldout 了 | 保留(良好设计) |
| **Auto Memory Min Turns** | Auto Memory | 🔴 极工程化 | **隐藏** |
| **Knowledge Base (LightRAG)** | Knowledge Base | ✅ 用户需要选 | 保留 |
| **LightRAG Endpoint/Key** | Knowledge Base | ✅ 同上 | 保留 |

**Context & Memory 减字段**: 15+ → **6 (保留 mem0 + LightRAG 服务卡片 + endpoint/key)**, 其他内部化。

### 2.3 UI & Diagnostics 页面 (3 个卡片)

| 字段 | 诊断 | 建议 |
|---|---|---|
| **Streaming Output** | 🟡 默认开就行 | **隐藏** |
| **Show Tool Call Details** | 🟡 默认开就行 | **隐藏** |
| Diagnostics 卡片 | ✅ Test Connection 按钮有用 | 保留 |
| Maintenance 卡片(Reset settings/Clear keys) | ✅ 有用 | 保留 |

**UI & Diagnostics 减字段**: 2 → **0**, 只保留操作卡片。

### 2.4 Workspace 页面

| 字段 | 诊断 | 建议 |
|---|---|---|
| **Auto-Detect on Startup** | 🟡 默认开就行 | **隐藏** |
| **Workspace Root Override** | 🟡 高级 | **收进 Advanced foldout** |
| **Unity Root Relative Path** | 🟡 高级 | 同上 |
| Detection Actions 卡片 | ✅ 手动重探测很有用 | 保留 |
| Workspace Overview 卡片(只读) | ✅ 信息展示 | 保留 |

**Workspace 减字段**: 3 → **1 (仅 Detection 触发按钮)**, override 字段收进 Advanced。

### 2.5 Tools & Extensions 页面

| 内容 | 诊断 | 建议 |
|---|---|---|
| 工具分类勾选列表 | 🔴 极工程化 | **隐藏整个 UI**, 内部管理; 用户不需要选哪些工具启用 |
| 可选组件启用(VCS/Indexing) | 🟡 只有部分用户需要 | 保留但简化描述 |

**Tools & Extensions 建议**: 只保留 VCS/Indexing 两个可选组件开关, 移除单工具级 UI。

### 2.6 Dashboard 页面

Dashboard 目前展示 Setup Status + Quick Actions, 都是**导航型内容**, 符合极简哲学, **保留**。

---

## 3. AgentCoreSettings 底层字段清理

除去 UI 层, `AgentCoreSettings.cs` 有 ~40 个字段。审计每个字段:

### 3.1 应当保留 public 且暴露 UI 的(约 10 个)

- `llmEndpoint`, `llmModel`, `temperature` (基本 LLM 配置)
- `mem0Enabled`, `mem0Endpoint`
- `lightragEnabled`, `lightragEndpoint`
- `selfChallengeEnabled` ✅ 已做
- `workspaceAutoDetectEnabled`(可折叠)

### 3.2 应当 [HideInInspector] 且写死为最优值(约 15 个)

- `maxTokens`, `maxToolCallRounds`, `maxTokenBudget`
- `maxContextTokens`, `reserveResponseTokens`
- `autoCompileCheck`, `autoConsoleCapture`, `fallbackRoutingEnabled`
- `maxConsecutiveErrors`, `toolFailWarningThreshold`, `toolFailBlockThreshold`, `allToolsFailBlockThreshold`
- `bootstrapEnabled`, `autoProjectContext`
- `compressionEnabled`, `toolResultCompressionThreshold`, `toolResultTargetTokens`, `conversationCompressionTrigger`
- `streamingEnabled`, `showToolCallDetails`
- `toolScopingEnabled`
- 已完成: 6 个 Self-Challenge 内部字段

### 3.3 应当彻底移除或迁移到内部常量(约 10 个)

- `disabledToolCategories`, `disabledTools`(除 execute_code) — **工具粒度管理不应该给用户看**
- `useSeparateCompressionLLM` + 3 个相关字段 — **极小众功能, 移到高级配置文件或直接删除**
- `enableReasoningOutput`, `reasoningEffort`, `reasoningMaxTokens`, `extraRequestBody` — **技术性极强, 应放 provider adapter 内部**
- `workspaceConfigVersion` — **内部使用, 从字段变成 private**
- `settingsVersion` — 已经内部化 ✅
- `userId` — **已 deprecated**(设计文档说明总使用系统生成), 直接删除

---

## 4. 设计文档层面的哲学冲突

### 4.1 高度冲突的文档

| 文档 | 冲突程度 | 具体问题 |
|---|---|---|
| [`prompt-layer-hallucination-hardening-plan.md`](prompt-layer-hallucination-hardening-plan.md) v0.10 §3.4 / §7.1 | 🔴 严重 | 强制要求 5+ 用户字段 + kill switch + Statistics 面板; 已被本次 Self-Challenge 极简化推翻 |
| [`indexing-scope-layered-and-status-awareness-design.md`](indexing-scope-layered-and-status-awareness-design.md) | 🟡 中等 | 引入 WorkspaceRoot/UnityRoot/ScopeRoot 三层概念 + Role 分类, 用户视角过度复杂 |
| [`rules-system-plan.md`](rules-system-plan.md) | ✅ 已废弃 | 已按 ADR-10 移除, 认识到功能重叠 |
| [`enterprise-unity-workflow-requirements.md`](enterprise-unity-workflow-requirements.md) | 🟢 低 | 需求文档, 描述真实痛点; **不冲突** |
| [`ROADMAP.md`](ROADMAP.md) | 🟡 中等 | ADR-16 明确 Self-Challenge kill switch 与 4 周实测数据窗口 → 与极简哲学部分冲突 |
| [`agent-design-frontier-redesign-2026.md`](agent-design-frontier-redesign-2026.md) | 未细查 | 待评估 |

### 4.2 保持不冲突的文档

- 索引 phase1/phase2 计划(功能已交付, 主要是内部实现)
- MCP feasibility(对外协议, 不影响用户设置)
- Self-Challenge stage-plan / implementation-report(本次工作产出)

---

## 5. 用户面向文档评估

### 5.1 QUICK_START.md 评估

**优势**:
- 4 步安装 + 3 步配置极简
- 使用示例贴近实际(自然语言 6 例)
- 表格化功能清单一目了然

**问题**:
- §4 "可选配置" 表格暴露了 5 项高级功能; 应折叠到"如果你需要 X, 参考 §5"式的引导
- 提到 `Scripting Define Symbols`(`AGENTCORE_VCS`/`AGENTCORE_INDEXING`) — **典型工程细节**, 应完全隐藏(通过 Settings 一键 toggle 自动加符号即可)

### 5.2 SOUL.md 评估

SOUL.md 是 LLM 系统提示, **不面向用户**, 无需极简化。当前内容合理。

### 5.3 README.md / CHANGELOG.md

未详细审计, 但根据 QUICK_START.md 的模式推测:
- README 应聚焦"打开 Package Manager 装了就能用"
- CHANGELOG 面向开发者, 保持工程化 OK

---

## 6. 优先级建议(按用户感知度排序)

### P0 - 立刻应做(用户第一次打开 Settings 就能感受到)

1. **Model & Agent 页面**: 隐藏 8 个技术字段(Max Tool Rounds / Token Budget / Fallback Routing / Auto Compile Check / Auto Console Capture / Max Consecutive Errors + Generation 里的 Temperature/Max Tokens 收进 Advanced foldout)
2. **Context & Memory 页面**: 隐藏 Bootstrap Files / Auto Project Context / 所有 Compression 内部参数(仅保留总开关) / Auto Memory Min Turns
3. **UI & Diagnostics 页面**: 完全隐藏 Streaming Output / Show Tool Call Details 两个 toggle
4. **QUICK_START.md**: 移除 §4 可选配置里的 Scripting Define 提示, 改成"在 Settings 里点开关即可"

### P1 - 短期内应做(提升配置流畅度)

5. **Tools & Extensions 页面**: 移除工具级勾选列表, 只留 VCS/Indexing 可选组件开关 + `execute_code` 全局禁用(默认已经是)
6. **AgentCoreSettings 内部**: 移除 deprecated `userId` 字段
7. **Workspace 页面**: `workspaceRootOverride` / `unityRootRelativePathOverride` 收进 Advanced foldout

### P2 - 中长期(重塑架构)

8. **Compression 参数**: 全部改成"启用/禁用"总开关 + 内部参数写死; 移除 useSeparateCompressionLLM(小众功能)
9. **Request Enrichment 字段**: enableReasoningOutput / reasoningEffort / reasoningMaxTokens / extraRequestBody 全部移到 provider adapter 层, 不作为用户设置
10. **设计文档 v0.10 §3.4 / §7.1**: 显式记 ADR: "本项目采用极简即开即用哲学, 上游设计文档中要求 6+ 用户字段的部分不采纳; 用一个总开关等效实现"

### P3 - 未来演进

11. **建立 Advanced foldout 统一模式**: 每个页面底部有一个"Advanced (工程用)"折叠区, 收纳所有 hidden 字段, 通过双击版本号触发显示(类似 Unity 的隐藏调试面板)
12. **Config schema 版本化**: 未来引入 JSON schema 校验 + 迁移工具, 避免用户手工编辑埋雷

---

## 7. 极简哲学的实施规则(建议写入 AGENTS.md)

未来做**任何用户设置**决策时, 用以下 5 条规则判定:

### 规则 1: 默认最优, 不问用户

> 任何字段, 如果**80% 用户会选同一个值**, 就写死为默认值, 不给 UI 选项。

例: `autoCompileCheck` → 100% 用户想开 → 隐藏。

### 规则 2: 一件事一个开关

> 一个功能不能有 6 个开关。若逻辑上多参数, 抽象出一个总开关, 内部细节写死。

例: Self-Challenge 6 字段 → 1 字段 ✅

### 规则 3: 术语必须白话

> 面向用户的字段名和 tooltip 严禁出现工程术语(如 "Node A", "Fallback Routing", "Token Budget", "Reserve Response Tokens")。

术语替换范例:
- "Token Budget" → 删除, 内部管理
- "Fallback Routing" → 删除, 内部管理
- "Reserve Response Tokens" → 删除, 从 model info 自动推断
- "Node A / Node B" → "Self-Challenge"(已做)

### 规则 4: 高级功能有藏起来的地方

> 极少数用户真的需要的高级功能, 用 Advanced foldout 收纳, 默认折叠且带明确警告标签。

### 规则 5: 可选服务用 ServiceCard 模式

> 现有 `ContextMemorySettingsPage.DrawServiceCard(mem0/LightRAG)` 模式是好的 — 服务默认关闭, 用户明确开启后才展开配置。**保持此模式**, 但内部实现的 endpoint 参数等只在开启后显示。

---

## 8. 挑战 & 风险(诚实说)

### 8.1 与已定稿设计文档的冲突

设计文档 v0.10 的 §3.4 / §7.1 / §5(Statistics 面板) 都是**用户可控性 / 可观测性优先**的架构, 与极简哲学**根本对立**。如果全面推行极简:

- **kill criteria(4 周实测)** 需要重新设计 — 不能靠 `legacySelfChallengeDisabled` toggle, 得靠内部 Statistics 判断 + AgentCore 团队远程决策(但你是唯一用户, 所以团队 = 你)
- **Statistics 面板** 若不给用户看, 就失去可观测性 — 但你的哲学显然是"用户被动感知, 不主动配置"; 那 Statistics 应改成**内部日志导出**给开发团队分析, 不作为 UI
- **advanced 用户诉求** — 会有一天用户说"我想自定义 X"; 你需要提前决定是**坚决拒绝**还是**开 Advanced foldout**

### 8.2 一致性挑战

如果只做 Self-Challenge 极简, 不做全产品, 反而**加深不一致**: 用户会问"为什么 Self-Challenge 只有一个开关, 但 Compression 有 4 个?"。**建议成套推行, 或写清楚 ADR 说明例外**。

### 8.3 迁移成本

- **代码层**: 每个 hidden 字段仍需在 AgentCoreSettings 保留, `[HideInInspector]` + 默认值, 但不动业务代码引用点; 迁移成本极低
- **UI 层**: 需要重构 5 个 Settings Pages 的 Draw 方法, 每个页面约 30 行代码调整; 估约 4-6 小时
- **文档层**: QUICK_START / README / SOUL.md 需要重写"如何配置"章节; 估约 2 小时
- **设计文档 ADR**: 需要在 ROADMAP.md 记 ADR-17 "极简即开即用哲学基准" + 明确推翻 v0.10 部分内容; 估约 1 小时

**总迁移成本**: 约 1 个工作日, **值得**。

### 8.4 未回答的问题(需你决策)

- **kill switch 的替代方案**: `legacySelfChallengeDisabled` 我保留在 [HideInInspector] 内部, 但你的哲学是"用总开关关闭"; 那 legacy 字段可以直接删除, 只保留 `selfChallengeEnabled`。**要不要删除**?
- **Advanced foldout 允不允许**: 有些用户(比如企业用户)可能真的需要调 Temperature、Max Tokens; 是**坚决不给**还是**用 foldout 折起来**?
- **移除 disabledTools UI 的时机**: 现有工具列表 UI 用户可能已经习惯; 移除后无法再手动禁用某个工具(除非工具本身有 `[HighRisk]` 标注)。**是否有回滚需求**?

---

## 9. 建议行动路线

**如果你认同以上审计**, 建议按以下步骤推进:

1. **今晚不做**代码层改动(避免连夜爆炸)
2. **明早决策** §6 的 P0/P1/P2 优先级列表 + §8.4 三个未回答问题
3. **明天 1-2 小时**: 记录 ADR-17("极简即开即用哲学基准") 到 [`ROADMAP.md`](ROADMAP.md) + [`AGENTS.md`](../AGENTS.md), 明确推翻的设计文档条款
4. **明天 3-4 小时**: 按 P0 清单改造 Model & Agent + Context & Memory + UI & Diagnostics 页面
5. **后续 iteration**: P1 → P2 逐步推进; 用户面向文档同步更新

---

## 10. 我的诚实评估

**做得好**:
- Self-Challenge 极简化已经**验证了这个哲学可行**, 且用户体验明显改善
- ContextMemory 里的 `DrawServiceCard(mem0/LightRAG)` 模式**其实就是极简哲学的实践**, 只是没有贯彻到其他地方

**做得不够好的地方**:
- 当前 Settings 面板**积累了 40+ 字段**, 大部分是**过去 phase 迭代时"给用户一个选项"的惯性思维**, 而不是真的用户需要
- 部分设计文档(v0.10, 索引 scope layered)倾向于**工程完整性优先**, 与用户视角有冲突
- **没有一个 ADR 或 AGENTS.md 条款**明确"最少字段暴露"的哲学基线; 每个 phase 交付时都是各自为政

**需要你决策的核心问题**:
- **推行范围**: 是**只做 Self-Challenge + 少量 P0** 还是**全产品统一改造**?
- **底线**: 是否**完全不允许 Advanced foldout**? 极简哲学的绝对形式是 zero foldout, 但可能与真实需求冲突
- **移除风险**: 有些用户可能已经在 Settings 里改过参数; 隐藏字段会导致他们的自定义值"丢失"到 UI 之外(实际数据仍在, 只是看不到)

---

## 参考

- [`prompt-layer-hallucination-hardening-plan.md`](prompt-layer-hallucination-hardening-plan.md) — 触发本审计的上游文档
- [`self-challenge-implementation-report.md`](self-challenge-implementation-report.md) — Self-Challenge 极简化范例
- [`ROADMAP.md`](ROADMAP.md) ADR-10 — 规则系统废弃案例, 类似哲学决策
- [`../