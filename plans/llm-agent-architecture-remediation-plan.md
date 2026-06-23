# AgentCore LLM/Agent 架构修复准则

> 本文档是 [`com.agentcore.unity`](package.json:1) 后续 LLM/Agent 架构修复与代码改造的最终准则文档。
>
> 输入来源：
> - [`plans/llm_guide_v4_3.agent.final.md`](plans/llm_guide_v4_3.agent.final.md:1) 的最新 LLM/Agent 工程规则。
> - 本轮对 [`Editor/Core`](Editor/Core)、[`Editor/Tools`](Editor/Tools)、[`Editor/Bootstrap`](Editor/Bootstrap)、[`Editor/Session`](Editor/Session)、[`Editor/Workspace`](Editor/Workspace)、[`Editor/VCS`](Editor/VCS) 等核心源码的静态审阅。
> - 两份外部 LLM 审查报告的问题清单与改造建议。
>
> 验证状态：本轮为静态架构审查，未运行 Unity Editor 编译、集成测试或 PlayMode/EditMode 测试。后续进入代码实现时，必须按本文档的 Phase 顺序执行。
>
> 与 ROADMAP 的关系：本文档不是 Phase 7 或 Phase 8 之外的第三个产品模块，而是后续 Phase 7（对内扩展/索引体验）与 Phase 8（MCP 对外互操作）的前置治理层。新增工具、扩大默认工具暴露、Plugin、MCP、文件写入自动化或代码执行能力变更，必须先满足本文档 Phase 1 的安全收口要求。

---

## 1. 核心结论

[`com.agentcore.unity`](package.json:1) 的方向正确，已经不是“套一层 ChatGPT”的玩具架构。项目已具备 Agent Loop、工具系统、Unity 主线程调度、Domain Reload 恢复、Session、Memory、RAG/Knowledge、Indexing、Workspace、VCS、Optional Components 等基础能力。

但按 [`plans/llm_guide_v4_3.agent.final.md`](plans/llm_guide_v4_3.agent.final.md:1) 的最新 Agent 工程标准，当前仍偏“全量工具暴露 + 长系统提示词 + 单 Agent 自治执行”。下一阶段修复重点不是继续堆工具，而是：

1. 收紧工具权限，建立统一 Tool Risk Policy。
2. 强制接入 WorkspacePathPolicy，避免高危路径被静默修改。
3. 降低工具 schema 与系统提示词造成的上下文污染。
4. 建立主动检索、Evidence Pipeline、Pinned Facts 与 Task Ledger。
5. 建立 CompletionGate，禁止 Agent 未验证就宣称完成。
6. 为 Domain Reload 后的高危操作补幂等与 Operation Journal。
7. 从单 Agent 执行升级到 Planner / Executor / Verifier / Finalizer 分层。

关键判断：当前项目最大风险不是 Agent 不够聪明，而是 Agent 已经太有能力，但边界还不够硬。后续开发必须先做 Phase 1 的安全收口，否则继续增加工具或扩大自治能力，会把当前的上下文污染、误选工具和越权执行风险进一步放大。

---

## 2. 总体评分

| 维度 | 评分 | 判断 |
|---|---:|---|
| 总体架构成熟度 | 7 / 10 | 基础完整，可继续演进。 |
| LLM/Agent 现代规则符合度 | 6.5 / 10 | 有上下文管理和工具循环，但缺少渐进披露、子 Agent 隔离和主动证据管线。 |
| 安全与权限边界 | 5 / 10 | 最大短板。高危工具能力强，但统一风险控制不足。 |
| 上下文工程 | 6.5 / 10 | 有裁剪和压缩，但还不是完整 Context Engineering。 |
| Unity Editor 适配 | 8 / 10 | Domain Reload、主线程调度、Editor-only asmdef、Optional Components 方向正确。 |

核心风险链路：全量工具暴露增加上下文税和误选概率；长系统提示词加剧注意力稀释；高危工具缺少统一权限门禁；单 Agent 多轮自治执行容易产生目标漂移、工具重试循环和静默失败。

---

## 3. 三份审查结果的交叉校验

### 3.1 高度一致的结论

| 问题 | 本轮源码复核 | 外部审查补充 | 最终判断 |
|---|---|---|---|
| 工具全量暴露 | [`AgentLoop.Tools.BuildToolDefinitions()`](Editor/Core/AgentLoop.Tools.cs:27) 每轮调用 [`ToolDefinitionBuilder.BuildAllEnabled()`](Editor/Tools/ToolDefinitionBuilder.cs:127)。 | 约 50 个 [`AgentToolAttribute`](Editor/Tools/Infrastructure/AgentToolAttribute.cs:10) 工具与大量 actions 需要 lazy discovery。 | P0，必须改。 |
| 已有局部构建能力但未作为主路径 | [`ToolDefinitionBuilder.BuildByCategory()`](Editor/Tools/ToolDefinitionBuilder.cs:180) 与 [`ToolDefinitionBuilder.BuildByNames()`](Editor/Tools/ToolDefinitionBuilder.cs:216) 已存在。 | 可直接作为渐进披露改造入口。 | P0，优先复用。 |
| 系统提示词过重 | [`SOUL.md`](Editor/Bootstrap/Resources/SOUL.md:1) 与 [`BootstrapLoader.GenerateActiveToolsList()`](Editor/Bootstrap/BootstrapLoader.cs:158) 形成重复工具说明。 | Bootstrap 会拼接 SOUL、SOUL.ext、TOOLS、PROJECT，且工具清单可能重复。 | P0/P1，必须拆分。 |
| 压缩策略风险 | [`ToolResultCompressor.CompressIfNeededAsync()`](Editor/Core/Compression/ToolResultCompressor.cs:65) 和 [`ConversationCompressor`](Editor/Core/Compression/ConversationCompressor.cs:35) 可回退主模型压缩。 | fallback truncate 与主模型自压缩会丢 evidence。 | P0/P1，必须修。 |
| 执行型工具边界不足 | [`ExecuteCodeTool`](Editor/Tools/Native/Scripting/ExecuteCodeTool.cs:1) 反射能力强；[`ManageFileTool`](Editor/Tools/FileSystem/ManageFileTool.cs:1) 写删移动缺少统一确认。 | [`WorkspacePathPolicy`](Editor/Workspace/Safety/WorkspacePathPolicy.cs:1) 未强制接入高危写操作。 | P0，最高优先级。 |
| FallbackRouter 不是真路由 | [`FallbackRouter`](Editor/Core/FallbackRouter.cs:16) 当前主要是同 client retry。 | 缺少模型 cascade、no-tools retry、compression model route。 | P2。 |
| Domain Reload 幂等风险 | [`DomainReloadState`](Editor/Core/DomainReloadState.cs:1) 能恢复语义状态。 | 高危工具部分执行后缺 operation journal 与 idempotency。 | P1。 |
| 完成前验证不足 | 已有 Console、编译、测试工具基础。 | 缺少按变更类型强制触发的 CompletionGate。 | P1。 |

### 3.2 本文档相对上一版新增/强化点

1. 增加总体评分与文档定位，明确本文档是最终修复准则。
2. 增加“配置项疑似未完全生效”审查项：包括 `fallbackRoutingEnabled`、`maxConsecutiveErrors`、`autoCompileCheck`、`autoConsoleCapture` 等配置需在 Phase 1/2 中核查实际运行路径。
3. 增加首批可直接创建的任务清单，便于下一轮开发拆任务。
4. 增加证据索引，避免后续实现时重新争论问题来源。
5. 强化 Phase 顺序：Phase 1 安全收口必须先于继续增加工具或扩大自治能力。

---

## 4. 已有基础能力与保留原则

### 4.1 Agent Loop 基础扎实

已有完整链路：用户输入 → LLM 流式响应 → tool calls → dispatcher → 工具执行 → tool result 回填 → 多轮循环。

关键文件：

- [`AgentLoop`](Editor/Core/AgentLoop.cs:42)
- [`AgentLoop.Runner`](Editor/Core/AgentLoop.Runner.cs:13)
- [`AgentLoop.Tools`](Editor/Core/AgentLoop.Tools.cs:17)
- [`ToolCallDispatcher`](Editor/Tools/ToolCallDispatcher.cs:112)

保留原则：不重写 Agent Loop 主体，先在现有主循环前后增加 policy、capability scope、verification gate。

### 4.2 Unity Editor 特化能力强

Unity Agent 的特殊点是脚本修改会触发 Domain Reload。项目已有 [`DomainReloadState`](Editor/Core/DomainReloadState.cs:1)、UI 恢复、pending tool calls、conversation history、file changes 等恢复机制。

保留原则：Domain Reload 恢复机制继续沿用，但高危工具必须补 Operation Journal 与幂等状态，不再只靠语义恢复。

### 4.3 工具体系覆盖广

工具通过 [`ToolAutoDiscovery`](Editor/Tools/Infrastructure/ToolAutoDiscovery.cs:10) 自动发现，覆盖 Scene、GameObject、Asset、Script、Prefab、UI、Material、Animation、Build、Package、VCS、FileSystem、Memory、Knowledge、Indexing 等能力。

保留原则：自动发现机制保留；工具暴露策略从“注册多少暴露多少”改为“注册全量、暴露按需、执行受控”。

### 4.4 上下文与压缩基础可复用

已有：

- [`ContextWindowManager`](Editor/Core/ContextWindowManager.cs:20)
- [`ConversationCompressor`](Editor/Core/Compression/ConversationCompressor.cs:35)
- [`ToolResultCompressor`](Editor/Core/Compression/ToolResultCompressor.cs:31)
- [`ContextBudgetInfo`](Editor/Core/ContextBudgetInfo.cs:7)
- [`TokenCounter`](Editor/Core/TokenCounter.cs:18)

保留原则：不废弃这些组件，但从“裁剪/压缩器”升级为 ContextAssembler 的一部分，增加 Task Ledger、Pinned Facts、Evidence References。

---

## 5. P0：Tool Risk Policy 与 Workspace 强制安全层

### 5.1 当前问题

当前工具元数据主要包括 name、description、category、parameters schema、requires main thread。虽然 [`AgentToolAttribute`](Editor/Tools/Infrastructure/AgentToolAttribute.cs:10) 有 `MayModifyScripts`，但它只服务编译等待，无法表达文件写入、包安装、网络访问、构建、代码执行、删除等风险。

高危工具与高危 action 包括：

| 工具 | 当前风险 | 修复要求 |
|---|---|---|
| [`ManageFileTool`](Editor/Tools/FileSystem/ManageFileTool.cs:24) | 项目根内写、删、移、复制缺少统一确认。 | 接入 [`WorkspacePathPolicy`](Editor/Workspace/Safety/WorkspacePathPolicy.cs:1)；delete/write/move/copy 需要确认；敏感路径拒绝。 |
| [`ExecuteCodeTool`](Editor/Tools/Native/Scripting/ExecuteCodeTool.cs:19) | 反射 + System.IO 能力过强，黑名单可绕过。 | 默认禁用或改只读 allowlist；副作用调用必须确认。 |
| `manage_package` | install/remove 会修改依赖，无统一确认；需核查是否存在主线程阻塞等待。 | 确认门禁；改 Editor update polling；安装前提示来源与 manifest diff。 |
| `manage_build` | build/set_target/set_scenes 属于高副作用操作。 | 构建和切平台需确认；输出路径经过 policy。 |
| `batch_execute` | 可批量调用高危工具，事务无法覆盖文件、包、VCS、外部服务副作用。 | 必须复用 [`ToolCallDispatcher.DispatchAsync()`](Editor/Tools/ToolCallDispatcher.cs:163) policy；批处理中任一高危 action 触发确认。 |
| `version_control` | 写操作会影响仓库状态。 | 沿用现有确认机制，并纳入统一 policy。 |

### 5.2 新增模型

新增路径：

- [`ToolRiskLevel`](Editor/Tools/Safety/ToolRiskLevel.cs)
- [`ToolCapability`](Editor/Tools/Safety/ToolCapability.cs)
- [`ToolExecutionRisk`](Editor/Tools/Safety/ToolExecutionRisk.cs)
- [`ToolPolicyDecision`](Editor/Tools/Safety/ToolPolicyDecision.cs)
- [`ToolRiskPolicy`](Editor/Tools/Safety/ToolRiskPolicy.cs)
- [`ToolConfirmationRequest`](Editor/Tools/Safety/ToolConfirmationRequest.cs)

风险等级：

| 等级 | 含义 |
|---|---|
| ReadOnly | 只读查询，无外部副作用。 |
| Low | 低风险 Editor 修改，可 Undo。 |
| Medium | 修改场景/资源/脚本但可控。 |
| High | 修改关键文件、ProjectSettings、Packages、Build 设置。 |
| Destructive | 删除、覆盖、移动、回滚、不可逆操作。 |
| External | 网络、VCS push、包安装、外部服务调用。 |
| CodeExecution | 执行任意代码或反射调用。 |

能力标签：

| Capability | 含义 |
|---|---|
| ReadProject | 读取项目文件或 Editor 状态。 |
| WriteProjectFiles | 写入项目文件。 |
| DeleteProjectFiles | 删除或移动项目文件。 |
| ModifyScene | 修改场景对象。 |
| ModifyAssets | 修改资源或 prefab。 |
| ModifyScripts | 修改 C# 脚本，可能触发编译。 |
| ExecuteCode | 执行代码、表达式或反射。 |
| InstallPackages | 修改 Package 依赖。 |
| BuildPlayer | 构建 Player 或切换构建目标。 |
| NetworkAccess | 访问外部网络或云服务。 |
| VersionControlWrite | VCS stage/commit/revert/push/update 等写操作。 |
| BatchExecute | 批量组合工具调用。 |

### 5.3 元数据扩展

扩展 [`ToolMetadata`](Editor/Tools/IAgentTool.cs:1) 与 [`AgentToolAttribute`](Editor/Tools/Infrastructure/AgentToolAttribute.cs:10)，增加：

- RiskLevel
- Capabilities
- RequiresConfirmation
- SupportsRollback
- RequiresIdempotency
- PathPolicyMode
- ExternalSideEffect

兼容策略：

1. 未声明风险的工具默认按 Medium 处理。
2. `MayModifyScripts=true` 自动增加 ModifyScripts。
3. FileSystem 工具默认增加 ReadProject / WriteProjectFiles。
4. `execute_code` 默认 CodeExecution + RequiresConfirmation。
5. `batch_execute` 默认 BatchExecute，并继承子工具最高风险。

### 5.4 Dispatcher 前置策略

[`ToolCallDispatcher.DispatchAsync()`](Editor/Tools/ToolCallDispatcher.cs:163) 执行前必须：

1. 解析工具 metadata。
2. 解析 action 风险。
3. 提取路径参数并调用 [`WorkspacePathPolicy`](Editor/Workspace/Safety/WorkspacePathPolicy.cs:1)。
4. 合并工具风险、action 风险、路径风险、当前 capability scope。
5. 若 Blocked，直接返回失败，不执行工具。
6. 若 RequiresConfirmation 且未确认，返回结构化 confirmation request，不执行工具。
7. 若高危但允许执行，写入 Operation Journal。
8. 执行后记录结果、hash、状态。

### 5.5 高危 action 默认确认清单

必须 confirmed=true 或 UI 二次确认：

- `manage_file.write_file`
- `manage_file.delete`
- `manage_file.move`
- `manage_file.copy` 目标覆盖时
- `manage_script.write/create/delete/add_method/add_field`
- `execute_code.evaluate`
- `manage_package.install/remove/update`
- `manage_build.build/set_target/player_settings`
- `version_control.commit/revert/push/update/cleanup/resolve/remove`
- `batch_execute` 中包含任意高危 action
- 任意写入 ProjectSettings、Packages、.git、Library、UserSettings、Temp、Logs 的操作

### 5.6 WorkspacePathPolicy 强制接入

[`WorkspaceOperationRisk`](Editor/Workspace/Safety/WorkspaceOperationRisk.cs:6) 已有 Safe / LowRisk / MediumRisk / HighRisk / Blocked，但必须从设置页展示升级为运行时强制策略。

| 路径/角色 | 默认策略 |
|---|---|
| EditableProjectCode | 可写，脚本写入需编译 gate。 |
| SharedCode | 可写但提示影响范围。 |
| WorkspacePackage | 可写但需 scope 明确。 |
| CustomPlugin | 中风险确认。 |
| CommercialPlugin | 高风险确认。 |
| EngineCode | 高风险或只读。 |
| ToolingCode | 中风险确认。 |
| GeneratedCode | 默认禁写。 |
| ReadOnlyReference | 禁写。 |
| .git | 禁写。 |
| Library / Temp / obj / Logs | 默认禁写或只读。 |
| ProjectSettings | 高风险确认。 |
| Packages/manifest.json | 高风险确认。 |

路径判断必须使用 canonical full path + separator containment，不能只靠简单 StartsWith。

---

## 6. P0：限制 execute_code 能力

### 6.1 当前问题

[`ExecuteCodeTool.ContainsDangerousPattern()`](Editor/Tools/Native/Scripting/ExecuteCodeTool.cs:469) 使用字符串黑名单，无法可靠防御拼接、反射、Unicode、间接调用。

[`ExecuteCodeTool.EvaluateExpression()`](Editor/Tools/Native/Scripting/ExecuteCodeTool.cs:164) 的反射执行能力过强，相当于给 LLM 一把 Unity Editor 超级钥匙。

### 6.2 改造方案

拆分为两个工具：

| 工具 | 暴露策略 | 能力 |
|---|---|---|
| `query_editor_state` | 默认暴露。 | 只读、预定义查询、无反射副作用。 |
| `execute_editor_code` | 默认不暴露。 | 高危能力域，必须用户显式启用，每次确认，记录 Operation Journal。 |

### 6.3 白名单模式

允许只读查询：

- UnityEditor Selection 查询。
- AssetDatabase 只读查询。
- EditorSceneManager 当前场景只读信息。
- GameObject/Component 只读属性读取。

禁止默认执行：

- System.IO 写删移动。
- Process / Reflection.Emit。
- Assembly load。
- EditorPrefs 写入。
- PackageManager 修改。
- BuildPipeline 执行。
- 任意反射调用非白名单方法。

---

## 7. P0/P1：Lazy Tool Discovery 与 Capability Scope

### 7.1 当前问题

[`AgentLoop.Tools.BuildToolDefinitions()`](Editor/Core/AgentLoop.Tools.cs:27) 每轮全量注入工具定义。工具越多，LLM 注意力越差，选错工具概率越高，成本越高。

### 7.2 目标

默认每轮只暴露少量 meta tools，根据任务意图逐步加载能力域。注册仍然全量，暴露改为按需。

### 7.3 初始工具集

建议初始只暴露：

- `list_tool_categories`
- `search_tools`
- `describe_tool`
- `request_tool_access`
- `read_console`
- `manage_file.read/list/search` 的只读子集

### 7.4 能力域

| 能力域 | 工具示例 |
|---|---|
| Scene | manage_scene, find_gameobjects, manage_gameobject, manage_component |
| Asset | manage_asset, manage_material, import tools |
| Scripting | manage_script, read_console, manage_test |
| FileSystem | manage_file read-only / write-enabled |
| Knowledge | manage_knowledge, search_code, memory |
| VCS | version_control |
| Build | manage_build, manage_package |
| UI | manage_ui, manage_ui_toolkit |
| Specialized | terrain, navmesh, animation, timeline, probuilder |

高危能力域默认不可自动激活，必须由用户确认。

### 7.5 分类大小写统一

所有工具分类统一 PascalCase，并在注册与禁用判断时 normalize。

需要修复示例：

- `extended` → `Extended`
- `meta` → `Meta`
- `specialized` → `Specialized`

禁用分类匹配必须使用 OrdinalIgnoreCase 或 normalized key，避免“用户以为禁用但实际仍暴露”。

---

## 8. P1：System Prompt 分层预算

### 8.1 当前问题

[`SOUL.md`](Editor/Bootstrap/Resources/SOUL.md:1) 过重，且混合身份、工具、组件、工作流等内容。

[`BootstrapLoader.GenerateActiveToolsList()`](Editor/Bootstrap/BootstrapLoader.cs:158) 又生成全量工具清单，重复消耗上下文。

### 8.2 分层方案

| 层级 | 内容 | 策略 |
|---|---|---|
| Core SOUL | 身份、基本安全、执行纪律、Unity Editor 特有约束。 | 常驻，控制在 80-120 行，不包含工具详表。 |
| Tool Guides | 具体工具说明、action 说明、例子。 | 按 capability 动态注入。 |
| Project Context | 项目规则、Unity 设置、目录摘要。 | 预算受限，按任务检索注入。 |
| Memory / Knowledge | 记忆与 RAG 证据。 | 稳定块 + 去抖，不每轮无差别重写。 |
| Component Guides | Workspace / VCS / Indexing 等组件规则。 | 仅组件启用且任务相关时注入。 |

### 8.3 预算降级顺序

当超预算时：

1. 裁剪 inactive tool guide。
2. 裁剪 auto project context。
3. 摘要历史消息。
4. 摘要 memory。
5. 保留当前用户消息。
6. 保留核心规则。

禁止因 system prompt 过大挤掉当前用户消息。

---

## 9. P1：Context Engineering 与 Evidence Pipeline

### 9.1 当前问题

当前上下文管理偏裁剪/压缩。RAG 主要作为工具暴露，依赖模型自己决定是否调用，稳定性不足。

### 9.2 新增组件

新增：

- [`TaskLedger`](Editor/Core/Context/TaskLedger.cs)
- [`PinnedFact`](Editor/Core/Context/PinnedFact.cs)
- [`EvidenceItem`](Editor/Core/Context/EvidenceItem.cs)
- [`EvidencePipeline`](Editor/Core/Context/EvidencePipeline.cs)
- [`ContextAssembler`](Editor/Core/Context/ContextAssembler.cs)
- [`KnowledgeRecallPolicy`](Editor/Core/Context/KnowledgeRecallPolicy.cs)
- [`FailureDiagnosis`](Editor/Core/Context/FailureDiagnosis.cs)

### 9.3 结构化状态职责

| 组件 | 职责 | 示例字段 |
|---|---|---|
| TaskLedger | 记录当前任务目标、步骤、完成状态、阻塞项。 | goal、non_goals、steps、current_step、acceptance_criteria。 |
| PinnedFacts | 保存不能被压缩丢失的关键事实。 | Unity version、workspace root、用户硬约束、已确认设计。 |
| EvidenceReferences | 保存 RAG/索引/文件读取证据。 | source_path、block_id、snippet、hash、timestamp。 |
| FailureDiagnosis | 宿主侧结构化失败原因，而不是让模型自我解释。 | tool、error_type、retryable、suggested_next_action。 |

### 9.4 Evidence Pipeline 触发条件

触发条件：

- 用户询问项目实现、架构、历史决策。
- 用户问“项目里有没有”。
- 修改代码前需要查接口。
- 回答 Unity API、包版本、工具用法。
- 长会话上下文不足。
- LLM 准备跨模块修改。

输出结构：

- evidence id
- source path
- line range
- snippet
- score
- timestamp
- freshness

最终回答和计划必须引用 evidence id；若无 evidence，则明确标记未验证，不允许编造。

### 9.5 Memory 去抖

[`AgentLoop.Memory.RemoveOldMemoryMessages()`](Editor/Core/AgentLoop.Memory.cs:18) 与 [`AgentLoop.Memory.InjectMemoryContext()`](Editor/Core/AgentLoop.Memory.cs:121) 改为稳定 memory block：

- 同一 query top-k 未变化则不重注入。
- memory block 固定位置。
- 长期记忆必须有 source conversation id 或 evidence id。
- LLM 抽取记忆前需要事实校验或用户确认高价值记忆。

### 9.6 压缩策略修复

禁止默认用主模型压缩主模型输出。

调整：

- 独立 compression client 未配置时，优先保留原文摘要引用，不做 LLM 重写压缩。
- 工具结果压缩保留 raw result id，可展开。
- fallback truncate 不再简单头尾截断，应保留结构化字段、错误、路径、行号、diff、summary。

---

## 10. P1：CompletionGate 完成前验收门

### 10.1 当前问题

当前 Agent 能执行工具，但缺少完成前统一验收状态机。LLM 可能在未编译、未测试、未检查 Console、未展示 diff 的情况下说“完成”。

### 10.2 新增组件

新增：

- [`CompletionGate`](Editor/Core/Verification/CompletionGate.cs)
- [`CompletionRequirement`](Editor/Core/Verification/CompletionRequirement.cs)
- [`VerificationResult`](Editor/Core/Verification/VerificationResult.cs)
- [`VerificationPolicy`](Editor/Core/Verification/VerificationPolicy.cs)

### 10.3 验收规则

| 变更类型 | 必须验证 | 未通过时最终状态 |
|---|---|---|
| 脚本修改 | Unity 编译通过，Console 无新增 error。 | 只能说“已修改，编译未通过/未验证”。 |
| 测试相关修改 | 运行相关 tests 或明确标记未运行。 | 不能宣称测试通过。 |
| 资源/场景修改 | 文件变更摘要、缺失引用检查、必要时 scene validation。 | 需要列出待人工检查项。 |
| Package 修改 | 依赖解析完成，manifest diff 明确。 | 需要提示依赖风险。 |
| 文件删除/移动 | 输出 diff summary，并确认无 orphan 引用。 | 不能宣称无影响。 |
| VCS 写操作 | status/diff 输出，用户确认。 | 禁止自动提交或回滚。 |
| 外部服务调用 | 数据外发提示。 | 标记外部副作用。 |

Final response 必须基于 gate 状态：

- verified：允许说完成。
- partially verified：说明未验证项。
- blocked：不能说完成，只能说明阻塞。

---

## 11. P1：Domain Reload 幂等与 Operation Journal

### 11.1 当前问题

Domain Reload 恢复目前偏语义恢复。若工具在 reload 前部分执行，恢复后 LLM 可能重试，造成重复写入、重复创建、包 manifest 冲突或文件状态不一致。

### 11.2 新增组件

新增：

- [`ToolOperationJournal`](Editor/Core/Operations/ToolOperationJournal.cs)
- [`ToolOperationRecord`](Editor/Core/Operations/ToolOperationRecord.cs)
- [`ToolOperationStatus`](Editor/Core/Operations/ToolOperationStatus.cs)
- [`OperationSnapshot`](Editor/Core/Operations/OperationSnapshot.cs)

### 11.3 记录字段

| 字段 | 含义 |
|---|---|
| tool_call_id | LLM tool call id。 |
| tool | 工具名。 |
| action | action 名。 |
| targets | 目标路径或 Unity 对象。 |
| before_hash | 执行前 hash / snapshot。 |
| after_hash | 执行后 hash / snapshot。 |
| started_at | 开始时间。 |
| completed_at | 完成时间。 |
| interrupted_at | 中断时间。 |
| status | NotStarted / Started / PartiallyApplied / Completed / Failed / Unknown。 |
| rollback_supported | 是否支持回滚。 |
| rollback_hint | 回滚提示或 diff。 |

### 11.4 恢复策略

Domain Reload 后：

- Completed：不重试，只回填结果。
- NotStarted：可重试。
- PartiallyApplied：停止自动继续，要求人工确认。
- Unknown：停止自动继续，要求人工确认。
- Failed：可让 LLM 选择修复，但不能重复 destructive action。

---

## 12. P2：Planner / Executor / Verifier / Finalizer 分层

### 12.1 当前问题

当前是单 Agent loop + 多工具。长任务容易发生目标漂移、上下文漂移、静默失败。

### 12.2 分层职责

| 角色 | 权限 | 输出 |
|---|---|---|
| Planner | 只读，无写工具。 | 计划、风险、验收标准、所需工具集合。 |
| Executor | 按批准能力执行，受 ToolPolicy 限制。 | 操作结果、文件变更、失败信息。 |
| Verifier | 只读 + 测试/编译/分析工具。 | 验证报告、是否允许 final answer。 |
| Finalizer | 无工具或只读。 | 面向用户的最终摘要。 |

### 12.3 交接格式

每个阶段交接必须结构化：

- objective
- constraints
- evidence ids
- allowed tools
- risk level
- expected outputs
- verification requirements
- unresolved questions

复杂任务再升级为独立子 Agent 上下文。

---

## 13. P2：真正的模型路由与成本工程

### 13.1 当前问题

[`FallbackRouter`](Editor/Core/FallbackRouter.cs:16) 目前主要是 retry，不是真正模型路由。

### 13.2 路由策略

| 场景 | 路由策略 |
|---|---|
| 普通聊天/解释 | 低成本模型，少工具或无工具。 |
| 工具规划 | 中等模型，注入有限 capability 工具。 |
| 架构设计/安全审查 | 高推理模型，禁用写工具。 |
| 压缩摘要 | 独立压缩模型，失败时不污染原文证据。 |
| 工具调用失败 | no-tools 诊断或缩小上下文重试，而不是同端点盲目 retry。 |
| 超窗 | 先 ContextAssembler 降级，不直接请求。 |

### 13.3 配置项核查

下一轮实现前需核查以下配置是否真正进入运行路径：

- `fallbackRoutingEnabled`
- `maxConsecutiveErrors`
- `autoCompileCheck`
- `autoConsoleCapture`
- `maxContextTokens`
- `useSeparateCompressionLLM`
- tool category disabled / individual tool disabled

若设置项存在但运行时未生效，应优先修复或移除误导性设置。

---

## 14. 文档一致性修复

文档错误会进入 Agent 上下文，直接诱导错误开发。

必须核对并修复：

1. [`README.md`](README.md:1) 与 [`package.json`](package.json:1) 版本号不一致。
2. [`README.md`](README.md:1) 中 Bootstrap 层级若仍提到 MEMORY.md / USER.md，应与 [`AGENTS.md`](AGENTS.md:1) 中“已废弃”规则统一。
3. 工具数量不要写死，改为“动态发现”或由构建脚本生成。
4. [`AGENTS.md`](AGENTS.md:1) 中“零外部引用”应表述为“主程序集不引用用户项目程序集；外部 UPM 依赖以 package manifest 为准”。
5. 分类命名规范补入 [`AGENTS.md`](AGENTS.md:1)。
6. 后续任何版本号变更必须同步 [`package.json`](package.json:1)、[`CHANGELOG.md`](CHANGELOG.md:1)、[`plans/ROADMAP.md`](plans/ROADMAP.md:1)。

---

## 15. 实施路线

### Phase 1：安全收口

目标：防止 Agent 误删、乱写、乱装包、乱执行代码。

主要任务：

1. 新增 ToolRiskPolicy、ToolRiskLevel、ToolCapability。
2. 扩展 ToolMetadata / AgentToolAttribute。
3. [`ToolCallDispatcher.DispatchAsync()`](Editor/Tools/ToolCallDispatcher.cs:163) 增加统一 ToolPolicy 检查。
4. [`ManageFileTool`](Editor/Tools/FileSystem/ManageFileTool.cs:1) 接入 [`WorkspacePathPolicy`](Editor/Workspace/Safety/WorkspacePathPolicy.cs:1)。
5. [`ExecuteCodeTool`](Editor/Tools/Native/Scripting/ExecuteCodeTool.cs:1) 默认禁用或拆分只读/执行工具。
6. manage_package / manage_build / batch_execute 接入确认策略。
7. 修复工具分类大小写不一致。
8. 核查关键配置项是否真正生效。
9. 修正文档一致性。

完成标准：

- 高危操作不再可静默执行。
- 禁用分类可靠生效。
- Blocked 路径写入直接拒绝。
- batch_execute 不能绕过单工具策略。
- Safe Mode 下 FileSystem/Scripting 高危写入不可用。
- Console 无新增编译错误。

### Phase 2：上下文减负

目标：减少 Context Rot、MCP Tax、工具误选。

主要任务：

1. 主循环从 BuildAllEnabled 迁移到 capability-based tool exposure。
2. 增加 tool discovery 元工具。
3. 拆分 Core SOUL 与动态 Tool Guides。
4. 修复 unknown model 默认 128k 和 system prompt 超预算策略。
5. UI budget 与实际发送 budget 统一。
6. Memory block 去抖。
7. Compression raw reference 与独立 compression model 策略。

完成标准：

- 普通请求不再注入全量工具和长工具说明。
- 默认请求工具数不超过 8。
- 高危工具默认不暴露。
- 当前用户消息不会因 system prompt 超预算被裁掉。

### Phase 3：执行闭环

目标：Agent 不再自称完成。

主要任务：

1. TaskLedger。
2. PinnedFacts。
3. EvidenceReferences。
4. FailureDiagnosis。
5. CompletionGate。
6. compile/test/console/diff gate。
7. Tool Operation Journal。
8. Domain Reload 幂等恢复。

完成标准：

- 脚本修改后未编译通过不能说完成。
- 部分执行的工具操作 reload 后不会自动重试 destructive action。
- 删除/移动文件必须展示影响摘要。
- Verifier 未通过时 Finalizer 不得输出完成。

### Phase 4：RAG 与索引增强

目标：项目事实回答可追溯。

主要任务：

1. LightRAG 自动召回策略。
2. search_code / indexing 结果 evidence 化。
3. 回答项目事实时强制引用 evidence id。
4. 无 evidence 时明确标记未验证。

完成标准：

- RAG 触发场景能返回 evidence id。
- 项目事实回答可追溯到文件、行号、hash 或知识库条目。

### Phase 5：模型路由与多 Agent

目标：复杂任务有角色隔离与成本路由。

主要任务：

1. 真正 FallbackRouter / ModelRouter。
2. 压缩模型 route。
3. Planner / Executor / Verifier / Finalizer 分层。
4. 长任务独立子上下文。

完成标准：

- 复杂任务有结构化计划。
- 每个执行步骤有 evidence 和 verification requirement。
- 高风险计划使用高推理模型或进入人工确认。

---

## 16. 首批可直接创建的任务清单

- [ ] 修复工具分类大小写不一致，并将禁用逻辑改为大小写不敏感。
- [ ] 新增 ToolRiskLevel / ToolCapability / RequiresConfirmation 元数据。
- [ ] [`ToolCallDispatcher.DispatchAsync()`](Editor/Tools/ToolCallDispatcher.cs:163) 增加统一 ToolPolicy 检查。
- [ ] [`ManageFileTool`](Editor/Tools/FileSystem/ManageFileTool.cs:1) 接入 [`WorkspacePathPolicy`](Editor/Workspace/Safety/WorkspacePathPolicy.cs:1)，delete/write/move/copy 加确认。
- [ ] [`ExecuteCodeTool`](Editor/Tools/Native/Scripting/ExecuteCodeTool.cs:1) 改为默认禁用或只读 allowlist。
- [ ] batch_execute 改为复用 [`ToolCallDispatcher.DispatchAsync()`](Editor/Tools/ToolCallDispatcher.cs:163)，不直接执行 tool.ExecuteAsync。
- [ ] 主循环从 [`ToolDefinitionBuilder.BuildAllEnabled()`](Editor/Tools/ToolDefinitionBuilder.cs:127) 迁移到 capability-based tool exposure。
- [ ] [`BootstrapLoader`](Editor/Bootstrap/BootstrapLoader.cs:24) 拆分 Core SOUL 与动态 Tool Guides。
- [ ] [`ContextWindowManager`](Editor/Core/ContextWindowManager.cs:20) 修复 unknown model 默认 128k 和 system prompt 超预算策略。
- [ ] 新增 CompletionGate：脚本修改后必须编译验证，测试相关修改后必须运行测试或明确未运行。
- [ ] LightRAG 增加自动召回策略与 evidence id。
- [ ] Domain Reload 高危工具增加 Operation Journal 与幂等判断。
- [ ] 核查 `fallbackRoutingEnabled`、`maxConsecutiveErrors`、`autoCompileCheck`、`autoConsoleCapture` 是否真实生效。
- [ ] 修复 [`README.md`](README.md:1)、[`AGENTS.md`](AGENTS.md:1)、[`package.json`](package.json:1) 的版本、工具数量、Bootstrap 层级描述不一致。

---

## 17. 当前审查证据索引

| 证据点 | 文件 |
|---|---|
| 工具全量构建主路径 | [`AgentLoop.Tools`](Editor/Core/AgentLoop.Tools.cs:27)、[`ToolDefinitionBuilder.BuildAllEnabled()`](Editor/Tools/ToolDefinitionBuilder.cs:127) |
| 已有按分类/名称构建能力 | [`ToolDefinitionBuilder.BuildByCategory()`](Editor/Tools/ToolDefinitionBuilder.cs:180)、[`ToolDefinitionBuilder.BuildByNames()`](Editor/Tools/ToolDefinitionBuilder.cs:216) |
| 系统提示词拼接 | [`BootstrapLoader`](Editor/Bootstrap/BootstrapLoader.cs:24)、[`SOUL.md`](Editor/Bootstrap/Resources/SOUL.md:1) |
| 工具清单生成 | [`BootstrapLoader.GenerateActiveToolsList()`](Editor/Bootstrap/BootstrapLoader.cs:158) |
| 上下文裁剪 | [`ContextWindowManager`](Editor/Core/ContextWindowManager.cs:20) |
| 对话压缩 | [`ConversationCompressor`](Editor/Core/Compression/ConversationCompressor.cs:35) |
| 工具结果压缩 | [`ToolResultCompressor`](Editor/Core/Compression/ToolResultCompressor.cs:31) |
| 文件系统工具 | [`ManageFileTool`](Editor/Tools/FileSystem/ManageFileTool.cs:1) |
| 代码执行工具 | [`ExecuteCodeTool`](Editor/Tools/Native/Scripting/ExecuteCodeTool.cs:1) |
| VCS 确认机制 | [`VersionControlTool`](Editor/VCS/Tools/VersionControlTool.cs:15) |
| Workspace 安全模型 | [`WorkspacePathPolicy`](Editor/Workspace/Safety/WorkspacePathPolicy.cs:1)、[`WorkspaceOperationRisk`](Editor/Workspace/Safety/WorkspaceOperationRisk.cs:6) |
| 模型重试路由 | [`FallbackRouter`](Editor/Core/FallbackRouter.cs:16) |
| 自动记忆 | [`AutoMemoryStrategy`](Editor/Session/AutoMemoryStrategy.cs:35)、[`AgentLoop.Memory`](Editor/Core/AgentLoop.Memory.cs:12) |
| Domain Reload 状态 | [`DomainReloadState`](Editor/Core/DomainReloadState.cs:1) |
| 工具自动发现 | [`ToolAutoDiscovery`](Editor/Tools/Infrastructure/ToolAutoDiscovery.cs:10) |
| 工具元数据属性 | [`AgentToolAttribute`](Editor/Tools/Infrastructure/AgentToolAttribute.cs:10)、[`ToolMetadata`](Editor/Tools/IAgentTool.cs:1) |

---

## 18. 关键接口草案与接入点

> 本节用于减少后续实现歧义。接口名称是准则级草案，实际编码时可按项目风格微调，但语义边界不得削弱。

### 18.1 ToolPolicyDecision

建议新增 [`ToolPolicyDecision`](Editor/Tools/Safety/ToolPolicyDecision.cs)：

```csharp
public enum ToolPolicyDecisionKind
{
    Allow,
    Deny,
    RequireConfirmation,
    RequireCapabilityGrant
}

public sealed class ToolPolicyDecision
{
    public ToolPolicyDecisionKind Kind { get; }
    public ToolRiskLevel RiskLevel { get; }
    public IReadOnlyList<ToolCapability> RequiredCapabilities { get; }
    public string Reason { get; }
    public ToolConfirmationRequest ConfirmationRequest { get; }
}
```

接入点：[`ToolCallDispatcher.DispatchAsync()`](Editor/Tools/ToolCallDispatcher.cs:163) 必须在调用工具前执行 policy：

1. `Deny`：直接返回失败工具结果，不进入工具实现。
2. `RequireConfirmation`：返回结构化确认请求，不执行工具。
3. `RequireCapabilityGrant`：提示需要启用能力域，不执行工具。
4. `Allow`：写入 Operation Journal 后继续执行。

### 18.2 ActiveToolScope

建议新增 [`ActiveToolScope`](Editor/Core/Context/ActiveToolScope.cs)，由 [`AgentLoop.Tools`](Editor/Core/AgentLoop.Tools.cs:17) 持有或通过会话上下文持有：

```csharp
public sealed class ActiveToolScope
{
    public IReadOnlySet<string> EnabledToolNames { get; }
    public IReadOnlySet<string> EnabledCategories { get; }
    public IReadOnlySet<ToolCapability> GrantedCapabilities { get; }
    public bool IsHighRiskCapabilityGranted { get; }
}
```

运行规则：

1. 新会话默认只启用 meta discovery + 安全只读工具。
2. 用户明确授权后才加入高危 capability。
3. Domain Reload 后必须恢复 scope，但高危临时授权默认失效，除非用户设置了持久授权。
4. [`ToolDefinitionBuilder.BuildAllEnabled()`](Editor/Tools/ToolDefinitionBuilder.cs:127) 不再作为主路径；主路径改为 scope 驱动的 BuildByNames / BuildByCategory。

### 18.3 CompletionGate.Evaluate()

建议新增 [`CompletionGate`](Editor/Core/Verification/CompletionGate.cs)，在 [`AgentLoop.Runner.HandleFinalResponse()`](Editor/Core/AgentLoop.Runner.cs:196) 之前执行：

```csharp
public sealed class CompletionGate
{
    public VerificationResult Evaluate(
        IReadOnlyList<ToolOperationRecord> operations,
        IReadOnlyList<FileChangeRecord> fileChanges,
        ContextBudgetInfo contextBudget);
}
```

接入规则：

1. LLM 生成 final answer 前，宿主先根据本轮 operation/file changes 生成 verification requirements。
2. 若 `VerificationResult` 为 blocked，禁止输出“完成”，改为输出阻塞原因。
3. 若 partially verified，最终回答必须列出未验证项。
4. 若 verified，才允许输出完成摘要。

### 18.4 OperationJournal

建议新增 [`ToolOperationJournal`](Editor/Core/Operations/ToolOperationJournal.cs)，由 [`ToolCallDispatcher`](Editor/Tools/ToolCallDispatcher.cs:112) 统一写入，不允许各工具各自实现孤立日志：

```csharp
public interface IToolOperationJournal
{
    ToolOperationRecord Begin(ToolCall toolCall, ToolExecutionRisk risk);
    void MarkCompleted(string operationId, OperationSnapshot afterSnapshot);
    void MarkFailed(string operationId, string error);
    void MarkInterrupted(string operationId);
    IReadOnlyList<ToolOperationRecord> GetUnresolvedOperations();
}
```

Domain Reload 恢复时，[`DomainReloadState`](Editor/Core/DomainReloadState.cs:1) 必须能引用 unresolved operations。任何 `PartiallyApplied` 或 `Unknown` 的 destructive operation 都不得自动重试。

### 18.5 ContextAssembler

建议新增 [`ContextAssembler`](Editor/Core/Context/ContextAssembler.cs)，替代“各模块各自往 messages 里塞内容”的模式：

```csharp
public sealed class ContextAssembler
{
    public AssembledContext Build(
        ChatMessage currentUserMessage,
        ActiveToolScope activeToolScope,
        TaskLedger taskLedger,
        IReadOnlyList<PinnedFact> pinnedFacts,
        IReadOnlyList<EvidenceItem> evidence,
        int tokenBudget);
}
```

接入规则：

1. 当前用户消息与 Core SOUL 永远优先保留。
2. 工具指南只按 ActiveToolScope 注入。
3. Memory 与 RAG 通过 Evidence Pipeline 注入，不再每轮无差别重写。
4. 超预算时先降级 tool guide / project context，再摘要历史，禁止丢当前用户消息。

---

## 19. 固定优先级

最终优先级固定如下：

1. Tool Risk Policy + WorkspacePathPolicy 强制接入。
2. ExecuteCodeTool 降权/拆分。
3. 工具分类大小写与禁用逻辑修复。
4. Lazy Tool Discovery / Capability Scope。
5. SOUL.md / Bootstrap 分层预算。
6. ContextWindowManager 预算策略修复。
7. CompletionGate。
8. Operation Journal + Domain Reload 幂等。
9. Evidence Pipeline / 主动 RAG。
10. Planner / Executor / Verifier / Finalizer 分层。
11. 真正 Model Router。
12. 文档一致性修复。

执行原则：后续代码修改必须先做 Phase 1 的安全收口。任何新增工具、扩大默认工具暴露、增强 Agent 自治能力的需求，都必须等 Phase 1 完成后再评估。