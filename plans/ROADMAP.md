# AgentCore Unity 开发路线图 (Roadmap)

> **版本**: v1.6.5 | **更新日期**: 2026-07-14 | **状态**:
> Phase 6 验收完成（v1.0.0）；治理层 G.1~G.3 全面完成（v1.1.0）；Phase 7 §3.1 后台增量索引 + §3.2 ThinkingDrawer 可观测性完成（v1.2.0）；Request Enrichment 修复 reasoning 触发（v1.2.1）；v1.3.x 系列稳定性修复；v1.4.0 索引 Scope 层次化；v1.4.1~v1.4.9 VCS 组件修复链 + Phase 9 骨架；v1.5.0-alpha1/2 Phase 9 Self-Challenge 核心 + ADR-17 极简哲学；v1.5.0-alpha4~alpha5 model-tier escape + GLM-5.2 适配；v1.5.6~v1.5.7 稳定性修复；**v1.6.x 系列产品化体验冲刺**（Context Ingest / YOLO 信任模式 / 日志分级 / PendingIndicator / SSE yield 优化 / 消息引用栏 / Play Mode preflight / 多轮思考窗口 / 文件删除视觉反馈 / GLM-5.2 reasoning 参数适配）
> **定位**: 本文件是 AgentCore 后续开发的**主导方向文档**，优先级高于分散的专项计划。

---

## 0. 如何使用本路线图

### 0.1 使用流程

1. **用户提出需求** → 明确要做什么、优先级、边界
2. **AI 评估需求** → 对照本路线图确定属于哪个 Phase / 任务
3. **文档细化** → 如需新功能，先写/更新 `plans/xxx-feature-plan.md` 详细设计；涉及工具暴露、自治能力、MCP 或高风险操作时，必须同时对齐 `llm-agent-architecture-remediation-plan.md`
4. **用户对齐确认** → 确认设计文档（参见 `AGENTS.md` §12.4 编码前对齐确认清单）
5. **代码实现** → 按确认后的设计编码
6. **版本同步** → 更新 `package.json` + `CHANGELOG.md` + 本文件任务状态
7. **用户测试验收** → 按 `AGENTS.md` §12.6 的轮次定义执行

### 0.2 任务状态标记

| 标记 | 含义 |
|------|------|
| `[ ]` | 未开始 |
| `[-]` | 设计中（文档对齐阶段） |
| `[>]` | 开发中 |
| `[~]` | 测试中（用户验收阶段） |
| `[x]` | 已完成 |
| `[!]` | 阻塞/暂停（依赖外部条件或用户决策） |

### 0.3 企业级 Unity 项目适配基准

自 2026-06-02 起，AgentCore 后续功能设计需按 `enterprise-unity-workflow-requirements.md` 中记录的大型商业 Unity 项目场景进行校准。已确认的基础设计规则是：

> **SVN 工作副本根 = AgentCore WorkspaceRoot；Unity 工程目录 = WorkspaceRoot 下的 UnityRoot 子根；地图、模式、工具、资源、插件等目录 = WorkspaceRoot 下的 Scope Root。**

凡涉及文件、资源、索引、记忆、知识库、VCS 操作和工具调用的功能，不得再默认只以标准 `Assets/` 目录或 Unity 项目根为 AgentCore 全局边界。

### 0.4 当前项目快照 (v1.6.5)

| 维度 | 状态 |
|------|------|
| **版本** | 1.6.5 (2026-07-14) — v1.6.x 系列产品化体验冲刺：Context Ingest、YOLO 信任模式、日志分级、PendingIndicator、SSE yield 优化、消息引用栏、Play Mode preflight、多轮思考窗口、文件删除视觉反馈、GLM-5.2 reasoning 参数适配 |
| **代码规模** | 288 个 .cs 文件，约 97K 行代码，51 个原生工具 |
| **核心架构** | AgentLoop (partial 9 文件) + ChatWindow (partial 9 文件) + ToolAutoDiscovery 重建注册表 + DomainReload 恢复 + Schema 预校验 + ToolScopeResolver 渐进暴露 — 稳定 |
| **Bootstrap 链** | SOUL(+SOUL.ext) → TOOLS → PROJECT(auto) → PROJECT.md(user) — 已完整（Rules System 已废弃，见 ADR-10） |
| **Workspace Config** | `manage_workspace_config` 工具 — Agent 可在 Chat 中读写 PROJECT.md / SOUL.ext.md |
| **UI 框架** | UI Toolkit 动态 Hub 架构；Chat 使用 AssistantTurnView 多轮布局（每轮独立 ThinkingDrawer → ToolCallGroup → 分隔线 → 下一轮 → SelfChallengeCard → MessageBubble）；Project Settings 使用 Dashboard + 5 Pages 顶部 Tab 导航；Tools & Extensions 页采用 Per-Component 自包含卡片布局 |
| **Chat UX** | PendingIndicator 占位气泡 + 折叠面板活跃度指示器（ThinkingDrawer 预览 + ToolCallGroup running 工具名 + active-pulse）+ 流式上翻 + "跳到最新"浮动按钮 + 输入框滚动 + MessageReferenceBar chip 引用栏 + SSE yield 时间预算优化 |
| **Context Ingest** | 全局快捷键 Ctrl+Shift+X 通用查询入口；6 个 Collector（Selection/Asset/Console/Scene/FocusedWindow/MouseTracker）；路由优先级：Console → Project → Hierarchy/Scene → 任意 EditorWindow；分级采样 + 15000 字符截断 |
| **工具确认** | YOLO 模式 3 按钮布局（Deny / Trust Low-Med / YOLO All）；SessionState 持久化跨 Domain Reload；PlayModePreflight Play Mode 禁止 write 类工具 |
| **日志分级** | AgentCoreLog 5 档（Silent/Error/Warning/Info/Debug）；默认 Info 级，Debug 级 30 处热点被跳过；Settings 中可热切换 |
| **云端服务** | Mem0 + LightRAG 基础连接 — 可用（OnDemand 可见性） |
| **VCS 组件** | Working Copy Status 扁平列表 + 多选右键菜单；Chat 工具 `version_control` 支持 Git/SVN/Perforce（`AGENTCORE_VCS` 控制，OnDemand 可见性）；SOUL.md §15 主动调用规则已就绪 |
| **Indexing 组件** | Roslyn 符号索引（JSONL 默认，可选 SQLite）+ `search_code` 工具 15 个 action（`AGENTCORE_INDEXING` 控制，OnDemand 可见性）；后台静默 + 增量索引；per-root 状态层次化；**标记为实验性，需手动在 Extensions 设置中开启** |
| **Agent 主动性** | SOUL.md §13（Workspace Config）+ §14（代码索引）+ §15（VCS）主动调用规则全部就绪 |
| **上下文参数** | reserveResponseTokens=32K、ContextWindowManager GLM-5.2 映射=200K（匹配部署版 max_model_len）；对话压缩 70% 阈值；工具结果压缩 >2000 tokens 触发 |
| **Reasoning 参数** | maxTokens=8192, reasoningMaxTokens=2048, reasoningEffort="low"（GLM-5.2 适配）；reasoning native 不可关闭但可通过参数限制思考量 |
| **工具暴露策略** | ActiveToolScope 三级可见性：核心工具 AlwaysVisible（~15 个）、按需工具 OnDemand（~27 个）、受限工具 Restricted（1 个）；LLM 通过 `request_tools` 元工具按需激活 |
| **Reasoning 可观测性** | ThinkingDrawer 默认折叠 + 尾部 60 字符预览；多轮独立思考窗口；provider 结构化 reasoning 与 `---THINKING---` / `---ACTION---` 双来源抽取；`RawAssistantContent` 仅持久化到 UI/session/archive，不进入 `_messages`；Request Enrichment 自动注入 `reasoning` 参数 |
| **测试覆盖** | 5 个测试文件 / 90+ test cases + 用户使用过程的实战验收（见 ADR-11） |
| **Phase 6 验收** | 完成 — 见 ADR-11 |
| **治理层进度** | G.1~G.3 全面完成（v1.1.0）；G.4~G.6 已归档（经分析评估为非必要，见 §2.x 说明） |

### 0.5 已完成的历史 Phase

| Phase | 版本 | 主题 | 状态 |
|-------|------|------|------|
| Phase 1 | v0.1.0 | 能对话 — LLM 集成、Bootstrap、Chat UI | [x] |
| Phase 2 | v0.2.0 | 能做事 — Tool Calling（unity-mcp 桥接，已废弃） | [x] |
| Phase 2.5 | v0.3.0 | 原生工具系统替代 unity-mcp | [x] |
| Phase 3 | v0.3.1 | 能记忆 — 会话管理、mem0、LightRAG | [x] |
| Phase 4 | v0.3.2~v0.3.7 | 更好用 — UX 打磨、工具补齐 | [x] |
| Phase 5 | v0.4.0~v0.9.2 | 夯实基础 — 测试框架、RAG 补齐、架构拆分、上下文压缩、VCS 组件、Settings 重构、Workspace 基础设施、代码索引 Phase 1 | [x] |
| Phase 6 | v0.9.3~v1.0.0 | 智能化与体验 — 索引深化、VCS Panel、规则系统（已废弃，见 ADR-10）、Settings shell 化、Per-Component 设置卡片 | [x] |

> 详细历史计划见 `_archive/` 目录。

---

## 1. 战略目标

```
已完成 (≤ 1.0.0): 代码库理解 → Workspace 基础设施 → VCS 主动调用 → 索引主动调用 → Settings shell 化 → Phase 6 验收
治理层 (1.0.x):    LLM/Agent 架构安全收口（Tool Risk Policy / WorkspacePathPolicy 强制接入 / Lazy Tool Discovery）— G.1~G.3 完成
派生 (1.0.x+):    后台静默 + 增量索引（v1.1.0）→ Chat UI / ThinkingDrawer 可观测性（v1.2.0）→ Request Enrichment 修复 reasoning 触发（v1.2.1）→ 兼容用户原本 IDE/CLI 习惯（MCP）
中期 (1.x):        Phase 8 对外互操作（MCP Server）+ 产品化分发（UPM / 文档站 / 示例 / Asset Store）
质量加固 (1.5.x):  Phase 9 Prompt 层幻觉护栏（Self-Challenge 双节点机制，带 4 周 kill criteria 实测决定去留）
```

| 阶段 | 版本 | 定位 | 核心目标 | 关键成果 | 状态 |
|------|------|------|---------|---------|------|
| **Phase 6** | 0.9.x ~ 1.0.0 | 智能化与体验 | 索引深化、VCS Panel、Settings shell、Per-Component 卡片 | 见 §0.4 / §0.5 | [x] 已完成 |
| **治理层** | 1.0.x | LLM/Agent 架构安全收口（**前置约束**） | Tool Risk Policy、WorkspacePathPolicy 强制接入、ExecuteCodeTool 降权、Lazy Tool Discovery | G.1~G.3 完成；G.4~G.6 归档（经评估非必要） | [x] 核心完成 |
| **Phase 7** | 1.0.x ~ 1.x | 索引体验深化、Chat 可观测性与产品化（**对内**） | 后台静默 + 增量索引（v1.1.0）、Chat UI / ThinkingDrawer（v1.2.0）、Request Enrichment（v1.2.1）、UPM 发布 / 文档站 / 示例项目 / Asset Store | 索引零感知 + reasoning 可审计 + 可分发产品 | [>] §3.1/§3.2 完成，§3.4 产品化待启动 |
| **Phase 8** | 与 Phase 7 平行 | MCP 对外互操作（**对外**） | 通过 MCP 协议向外部 IDE / CLI / Agent 平台暴露 AgentCore 工具集，兼容用户既有开发习惯 | AgentCore MCP Server（stdio + HTTP）+ 安全策略 + 配套示例 | [-] 设计中（治理前置 G.1~G.3 已满足） |
| **Phase 9** | 1.5.x | Prompt 层幻觉护栏（**质量加固**） | Self-Challenge 双节点机制：Node A（读需求时挑战对用户意图的理解）+ Node B（输出前独立 reviewer 审视 draft）；带 §5.4 kill criteria 4 周实测窗口，异常即回滚；**ADR-17 推翻 §5 Statistics 面板 / §5.5 首周引导 tooltip** | v1.5.0-alpha1~alpha5 核心+escape+GLM适配；v1.6.x 产品化体验冲刺完成；GA 待 alpha3 兜底 + 4 周 kill criteria 验证 | [>] 核心已发布，GA 待观察窗口 |

---

## 2. Phase 6 — 智能化与体验 (v0.9.x ~ 1.0.x)

**主题**: 上下文管理、代码库理解、规则系统 — 基于企业级 Unity 项目适配基准

### 2.1 P0 — 代码库索引深化（已完成范围）

> **前置条件**: WorkspaceRoot / UnityRoot / Scope 基础设施已完成（v0.9.0）；文件级索引 + 符号检索已完成（v0.9.1）。
> **架构决策**: 完全本地化单层架构（SQLite 符号索引），放弃向量数据库，放弃骨架文档。
> **派生事项**: 6.2.6（后台静默 + 增量索引）已迁移至 Phase 7（v1.1.0），不再属于 Phase 6 验收范围。

| # | 任务 | 说明 | 状态 |
|---|------|------|------|
| 6.2.1 | **文件级索引（Layer 1）** | Roslyn 解析 WorkspaceRoot 下 C# 文件，提取类名/命名空间/方法签名；JSONL 本地存储 | [x] v0.9.1 |
| 6.2.2 | **符号检索** | `search_code` 工具 10 个 action，支持 Scope/Root/Role/Branch 过滤 | [x] v0.9.1 |
| 6.2.3 | **SQLite 迁移 + 依赖图构建** | SQLite 替代 JSONL 作为默认后端；SyntaxTree 级 C# 类型依赖提取；`search_code` 新增 5 个 action（get_dependencies、find_usages、get_symbol_context、search_text、get_backend_info）；FTS5 全文搜索；`IndexStoreFactory` 自动降级 | [x] v0.9.3 |
| 6.2.4 | **Full Index Bug 修复** | `CodebaseIndexer` 重建 workspace 时遗漏 `UnityRoot` 字段导致 0 files/0 symbols；修复后验证：298 files, 6453 symbols | [x] v0.9.5 |
| 6.2.5 | **Agent 主动调用规则（SOUL.md §14）** | `search_code` 对话开始协议、强制预查场景、搜索策略、索引新鲜度规则；`TOOLS.md.template` 补充对话开始工作流 | [x] v0.9.5 |
| 6.2.6 | ~~**后台静默 + 增量索引**~~ | ~~Phase 6 内任务~~ — 在 v1.0.0 验收过程中识别为后续优化项，迁移至 **Phase 7（v1.1.0）**，详见 §3.1 与 ADR-11 / ADR-13；设计文档 [`indexing-background-incremental-design.md`](indexing-background-incremental-design.md) | [>] 已迁移至 Phase 7 |

### 2.2 P0 — VCS Panel 体验提升

> **架构决策**: TreeView 重构已放弃（树形结构导致用户需要多次展开折叠，且无法有效体现文件路径）。改为扁平列表按完整相对路径排序，等价于目录结构展开后的自然顺序。
> ~~**关联文档**: [`vcs-treeview-refactor-plan.md`](vcs-treeview-refactor-plan.md)~~ （已废弃）

| # | 任务 | 说明 | 状态 |
|---|------|------|------|
| 6.3.1 | **VCS Panel 扁平列表按路径排序** | Working Copy Status 扁平列表按完整相对路径（`/` 分隔符）排序，等价于目录结构展开后的自然顺序；`SortStatusFiles()` 已实现 | [x] v0.9.3 |
| 6.3.2 | **Agent 主动调用规则（SOUL.md §15）** | `version_control` 主动只读查询、自然语言→action 映射、写操作确认规则、VCS 类型感知（Git/SVN/Perforce） | [x] v0.9.5 |

### 2.3 P1 — 规则系统（已废弃）

| # | 任务 | 说明 | 状态 |
|---|------|------|------|
| 6.4.1 | ~~**.agentcore/rules.md 支持**~~ | ~~优先读取 WorkspaceRoot 下的规则文件（编码规范、架构约定、测试要求），兼容 UnityRoot 局部规则~~ | [!] 已废弃（见 ADR-10） |
| 6.4.2 | ~~**规则自动注入**~~ | ~~规则内容自动添加到 System Prompt；支持按 WorkspaceRoot、Scope、Root 分层注入~~ | [!] 已废弃（见 ADR-10） |
| 6.4.3 | ~~**SmartToolRecommender**~~ | ~~基于对话上下文和当前任务推荐可用工具；UI 显示推荐理由~~ | [!] 已废弃（见 ADR-9） |
| 6.4.4 | ~~**响应式建议**~~ | ~~LLM 响应末尾附带"下一步建议"（如"是否需要运行测试？"）~~ | [!] 已废弃（见 ADR-9） |

### 2.4 P2 — 体验优化（已闭环）

| # | 任务 | 说明 | 状态 |
|---|------|------|------|
| 6.5.1 | **文件变更 Diff 视图（外部委托）** | 不在 Editor 内自建 side-by-side 视图；改为调用宿主 VCS 的原生 diff 工具（TortoiseSVN / P4V / `git difftool`），由 `version_control` 工具提供 `open_diff` action 触发；详见 ADR-12 | [x] v1.0.0（外部委托方案） |
| 6.5.2 | **主题系统** | 深色/浅色主题切换 — 评估后判定为低 ROI，不纳入 Phase 6/7 范围；如未来需要再单独评估 | [!] 不纳入 |
| 6.5.3 | **快捷键自定义** | 用户可自定义聊天窗口快捷键 — 同上，低 ROI，不纳入 Phase 6/7 范围 | [!] 不纳入 |

### 2.5 Phase 6 里程碑

```
v0.9.3 — 代码库索引 Phase 2（依赖图构建）+ VCS Panel 扁平列表按路径排序 ✅
v0.9.4 — Indexing/VCS Settings UI 修复 + SQLite 兼容性修复 ✅
v0.9.5 — Full Index Bug 修复（验证通过）+ Agent 主动调用规则（SOUL.md §14/§15）✅
v0.9.6 — 规则系统（.agentcore/rules.md + 分层注入）✅
v0.9.7 — 废弃 Rules System（与 PROJECT.md 功能重叠，见 ADR-10）✅
v0.9.8/0.9.9 — Settings shell + Per-Component 卡片、Workspace 基础设施收尾、Indexing/VCS 主动调用规则验证 ✅
v1.0.0 — Phase 6 完成里程碑（用户实战验收通过；6.5.1 以外部 diff 委托方案闭环；6.2.6 后台静默 + 增量索引识别为派生项 → Phase 7）✅
```

---

## 2.x LLM/Agent 架构安全收口（Phase 7/8 前置治理层）

**主题**: 在继续增加工具、扩大自治能力或对外暴露工具前，先把 AgentCore 的工具边界、上下文预算、验证闭环和执行审计收紧。该治理层不是第三个产品模块，而是 Phase 7 / Phase 8 的前置架构约束。

**准则文档**: [`llm-agent-architecture-remediation-plan.md`](llm-agent-architecture-remediation-plan.md)

| # | 任务 | 说明 | 优先级 | 状态 |
|---|------|------|--------|------|
| G.1 | **Tool Risk Policy + WorkspacePathPolicy 强制接入** | 所有工具调用统一经过风险分级、能力授权、路径边界和确认策略；禁止工具各自绕过安全策略。拆分为 G.1.a 元数据基础设施 / G.1.b 策略评估器 / G.1.c Dispatcher 接入 / G.1.d 高危工具按 Category 细化 / G.1.e WorkspacePathPolicy 强制执行 | P0 | [x] 完成 v1.0.3 — G.1.a~G.1.e 全部落地 |
| G.2 | **ExecuteCodeTool 降权/拆分** | 默认禁用或拆分为只读查询与高风险执行；高风险执行必须显式授权和审计 | P0 | [x] 完成 v1.1.0 — 默认禁用 + Restricted 可见性 + 迁移保留旧用户设置 |
| G.3 | **Lazy Tool Discovery / ActiveToolScope** | 不再每轮默认暴露全部工具 schema；按任务阶段、类别和能力范围渐进暴露 | P0 | [x] 完成 v1.1.0 — ToolVisibility 三级 + ToolScopeState/Resolver + request_tools 元工具 + 全量工具标注 |
| G.4 | ~~**ContextWindowManager / Bootstrap 预算收口**~~ | ~~降低长驻 prompt 与工具 schema 对上下文的挤占~~。经分析 G.3 Lazy Discovery 已解决 85% 工具 schema tax 问题；BootstrapLoader 的全量工具列表是 G.3 发现机制的 catalog，属于必要组件；ContextWindowManager 已有 TrimToFit + ConversationCompressor 双重保障 | — | [!] 归档（G.3 已覆盖核心需求） |
| G.5 | ~~**CompletionGate + Operation Journal**~~ | ~~最终回复前检查工具执行、文件变更、错误和未完成高风险操作~~。经分析 SOUL.md §2 rule 4 已在 prompt 层强制执行"Write → Compile → Check → Fix → Recompile"验证循环；Operation Journal 过度工程化，无实际用户需求推动 | — | [!] 归档（SOUL.md prompt 层已覆盖） |
| G.6 | ~~**Evidence Pipeline / Planner-Executor-Verifier 分层**~~ | ~~对复杂任务引入证据缓存、任务账本和验证层~~。经分析属于架构宇航员式设计，当前 AgentLoop 的简单 tool loop + SOUL.md 行为约束足以应对现有场景；引入四层分离会打破现有稳定架构 | — | [!] 归档（过早优化，无实际需求） |

**执行规则**: 新增工具、MCP、文件写操作扩大化、自动执行能力增强等任务，必须先完成 G.1；涉及外部调用方的 Phase 8 至少需要 G.1/G.2/G.3 作为实现前置条件（已满足）。

> **G.4~G.6 归档决策说明（2026-06-29）**: 经对现有代码深入分析，G.4/G.5/G.6 三项均已被既有机制充分覆盖或属于过度设计。G.3 的 ToolScopeResolver 将 API tool schema 从 50+ 降至 8-15 个（解决 G.4 核心问题）；SOUL.md 的编译验证循环规则在 prompt 层实现了 G.5 的核心意图；G.6 的四层架构在当前产品阶段无实际驱动力。相关设计文档 `llm-agent-architecture-remediation-plan.md` §8-§12 保留作为历史参考，不再作为实现约束。

---

## 3. Phase 7 — 索引体验深化、Chat 可观测性与产品化（对内 / v1.0.x ~ v1.x）

**主题**: 从"功能完整的开发工具"演化为"可分发、零感知、可审计"的产品。Phase 7 聚焦**对内**——索引体验、Chat reasoning 可观测性、产品化分发，全部围绕 AgentCore 自身。MCP 对外互操作单独走 §3.x Phase 8。

### 3.1 P0 — 后台静默 + 增量索引（v1.1.0，从 Phase 6 派生）

> **触发原因**: v1.0.0 实战验收发现现有索引为同步阻塞式触发，影响 Editor 响应；用户提出需要静默 + 增量形式。
> **设计文档**: [`indexing-background-incremental-design.md`](indexing-background-incremental-design.md)
> **范围**: 仅改造现有索引体验，不引入新存储/新协议。
> **治理约束**: Phase 7 §3.1 可与治理层并行设计，但实现时不得扩大默认工具暴露或绕过 `llm-agent-architecture-remediation-plan.md` 的安全策略。

| # | 任务 | 说明 | 状态 |
|---|------|------|------|
| 7.1.1 | **AssetPostprocessor 主触发源** | `OnPostprocessAllAssets` 替代当前同步触发；imported / deleted / moved 全覆盖 | [x] |
| 7.1.2 | **DirtyTracker 持久化** | `Library/agentcore-indexing-dirty.json` 跨 Domain Reload 保留脏文件队列 | [x] |
| 7.1.3 | **CoalescingScheduler** | 合并 + 去抖 + yield gate；避免短时间内重复全量扫描 | [x] |
| 7.1.4 | **BackgroundIndexService** | `Task.Run` 后台执行，每 N 文件 yield，不阻塞 Editor 主线程 | [x] |
| 7.1.5 | **CodebaseIndexer.RunTargetedIncrementalAsync** | 跳过 ScanAllFiles，按 dirty/deleted 集合定向更新 SQLite | [x] |
| 7.1.6 | **IndexingStatusBus + Hub Badge** | 状态枚举 Idle/Pending/Running/Failed/Disabled；Hub 会话头部右侧 ChipBadge 静默呈现 | [x] |
| 7.1.7 | **SOUL.md §14 / TOOLS.md.template 增补** | LLM 感知"索引可能正在后台更新"的规则，避免在 Pending 状态强行依赖陈旧结果 | [x] |

#### 3.1.1 索引 Scope 层次化与可观测性（v1.4.0）

> **触发原因**: v1.3.x 用户实战反馈"动态 index 索引代码库会让整个系统变得特别卡"；实际根因是所有 root 一视同仁进入自动增量循环，且 LLM 无法感知 per-root 索引状态。
> **设计文档**: [`indexing-scope-layered-and-status-awareness-design.md`](indexing-scope-layered-and-status-awareness-design.md)
> **范围**: 沿用 §3.1 的 SQLite 单层架构（不新增存储 / 不新增顶层工具 / 不引入骨架文档）；per-root 状态走 `IIndexStore` metadata KV，无 schema 迁移。
> **治理约束**: 严格遵守 ADR-7（无骨架文档）、ADR-8（Agent 行为规则内嵌于 SOUL.md）与 `llm-agent-architecture-remediation-plan.md`（不扩大默认工具暴露）。

| # | 任务 | 说明 | 状态 |
|---|------|------|------|
| 7.1.1.1 | **IndexRoot 运行时字段** | `IndexState / LastIndexedAt / LastIndexError / IndexedFileCount / IndexedSymbolCount / Priority` 6 个字段；`IndexRootState` / `IndexRootPriority` 枚举 | [x] v1.4.0 |
| 7.1.1.2 | **IndexingSchedulePolicy** | 按 `IndexRootRole` 三档划分 Foreground / Background / OnDemand；OnDemand 跳过自动增量循环 | [x] v1.4.0 |
| 7.1.1.3 | **IndexRootStateStore** | per-root 状态持久化（`IIndexStore.SetMetadataAsync` KV, key = `root:{rootId}:*`），零 schema 变更 | [x] v1.4.0 |
| 7.1.1.4 | **BackgroundIndexService Priority 过滤 + 状态更新** | RunOnceAsync 按 Priority 分流脏文件；索引前后 mark `Indexing → Ready / Failed`；OnDemand 路径直接 mark processed | [x] v1.4.0 |
| 7.1.1.5 | **IndexingDirtyTracker Burst Detection** | 单批 500+ 文件（可配置）触发 60s backoff，snapshot 携带 `NextRunAt / ReasonPaused` | [x] v1.4.0 |
| 7.1.1.6 | **search_code::diagnose / list_root_states / mark_stale** | 三个新 action：诊断根因 / 列出 per-root 状态 / 强制标脏重建；status action 附带 per_root_state | [x] v1.4.0 |
| 7.1.1.7 | **WorkspaceSnapshotBuilder + IndexingStatusBlockBuilder** | 会话首轮 snapshot 追加 "Index Status" 块（同步、零 I/O，仅 IndexingStatusBus 全局状态 + roots 静态元数据） | [x] v1.4.0 |
| 7.1.1.8 | **SOUL.md §4 Context Awareness 追加** | LLM 看到 "Index Status" 块时的行为规则；搜索落空先 diagnose 再下结论 | [x] v1.4.0 |
| 7.1.1.9 | **IndexingPanel Roots Overview 折叠区** | Editor UI 只读展示 root 列表 + Priority 分类摘要；交互仍走 `search_code` action | [x] v1.4.0 |
| 7.1.1.10 | **ProjectContextCollector Fast/Heavy 拆分** | `CollectFast` / `CollectHeavyAsync` 基础设施；Unity API 主线程预取 + 后台磁盘扫描；预留给未来 UI/Panel 消费 | [x] v1.4.0 |

### 3.2 P0 — Chat UI / ThinkingDrawer 可观测性（v1.2.0）

> **触发原因**: 多模型切换测试发现 provider 结构化 reasoning 字段不统一，且 Claude / GPT 类模型可能通过上下文规则输出 `---THINKING---` / `---ACTION---` 可见规划 trace；原 Chat UI 会把流式中间内容与最终回复混在同一气泡，无法稳定审计 LLM 决策过程。
> **设计文档**: [`thinking-drawer-design.md`](thinking-drawer-design.md)
> **范围**: 仅改造 Chat UI 与 LLM 响应解析/持久化链路；不新增外部协议、不扩大工具暴露、不把 reasoning 注入后续 LLM 上下文。

| # | 任务 | 说明 | 状态 |
|---|------|------|------|
| 7.2.1 | **ThinkingDrawer UI** | 默认折叠；标题显示 `思考中 · Ns` / `思考完成 · Xs`；展开时 set text，折叠时清空 label | [x] v1.2.0 |
| 7.2.2 | **AssistantTurnView 固定顺序** | 每个 assistant turn 固定为 ThinkingDrawer → ToolCallGroup → MessageBubble，历史会话重建保持同序 | [x] v1.2.0 |
| 7.2.3 | **Structured Reasoning 抽取** | 从 provider 原始 SSE JSON 中自适应读取 `reasoning_content` / `reasoning` / `thinking` / `thought` / `reasoning_text` / reasoning content block | [x] v1.2.0 |
| 7.2.4 | **Visible Planning Trace 抽取** | 默认开启；严格识别内容开头 `---THINKING---` 与 `---ACTION---`；代码块/引用/不完整 marker 不抽取 | [x] v1.2.0 |
| 7.2.5 | **LLM 上下文隔离** | `RawAssistantContent` 持久化到 ConversationTurn / Session / DomainReloadState；写入 `_messages` 前只保留清洗后的 assistant content | [x] v1.2.0 |
| 7.2.6 | **Domain Reload 恢复兼容** | Streaming 中断时恢复 reasoning/raw/planning state 到 UI/session，LLM 历史仅注入清洗后的可见内容 | [x] v1.2.0 |
| 7.2.7 | **Request Enrichment — reasoning 触发** | 请求序列化层注入 `reasoning` 参数 + `stream_options`；Settings UI 支持 effort/max_tokens/extra body 配置；修复 ThinkingDrawer 端到端数据链路 | [x] v1.2.1 |

### 3.3 ~~P1 — Plugin / Extension 系统（对内扩展）~~ [已归档 — 见 ADR-15]

> **归档原因**: 经用户决策（2026-06-29），Plugin 系统不再作为开发目标。现有 `[AgentTool]` + `IAgentTool` + `ToolAutoDiscovery` 机制已天然支持用户在自己项目中通过 Editor asmdef 添加自定义工具（只需标注 `[AgentTool]` 即可被自动发现），无需额外的 Plugin 加载框架。MCP Server 将承担"外部扩展"的角色，覆盖原本 Plugin 的大部分使用场景。

| # | 任务 | 说明 | 优先级 | 状态 |
|---|------|------|--------|------|
| ~~7.3.1~~ | ~~**Plugin 加载契约**~~ | ~~复用现有 `[AgentTool]` + `IAgentTool` + `ToolAutoDiscovery`；定义"用户工具程序集"扫描规则与隔离策略~~ | — | [!] 归档 |
| ~~7.3.2~~ | ~~**Plugin 设置面板**~~ | ~~Settings 中列出已发现的用户工具，支持启用/禁用、查看元数据~~ | — | [!] 归档 |
| ~~7.3.3~~ | ~~**示例 Plugin 模板**~~ | ~~提供 Hello World 级别的用户工具模板仓库~~ | — | [!] 归档 |

### 3.4 P1 — 产品化与分发

| # | 任务 | 说明 | 优先级 | 状态 |
|---|------|------|--------|------|
| 7.4.1 | **UPM 发布流程** | 自动化打包、版本标签、发布检查清单 | P0 | [ ] |
| 7.4.2 | **文档网站** | 使用 Docusaurus/VitePress 搭建静态文档站（托管于 GitHub Pages） | P1 | [ ] |
| 7.4.3 | **示例项目** | 完整示例：3D 平台跳跃游戏从零开发（演示 AgentCore 全部能力） | P1 | [ ] |
| 7.4.4 | **多 LLM 后端** | 支持 Claude、Gemini、本地 Ollama 等（统一接口） | P2 | [ ] |
| 7.4.5 | **Unity Asset Store 提交** | 整理元数据、截图、描述文案，完成 Asset Store 提交 | P2 | [ ] |

---

## 3.x Phase 8 — MCP 对外互操作（对外 / 与 Phase 7 平行）

**主题**: 通过 [Model Context Protocol](https://modelcontextprotocol.io) 把 AgentCore 已有的工具集（Native / Cloud / FileSystem / Indexing / VCS）暴露给外部 IDE / CLI / Agent 平台，**兼容用户原本的开发习惯**。
**触发原因**: v1.0.0 验收过程中识别——用户希望在不离开自己惯用的 IDE/CLI 工作流的前提下使用 AgentCore 能力。
**与 Phase 7 的边界**: Phase 7 = 对内（索引 / 可观测性 / 产品化分发），Phase 8 = 对外（MCP Server）；两个 Phase 在产品规划上平行推进，Phase 8 的治理前置条件 G.1/G.2/G.3 已满足。
**设计文档**: [`mcp-server-feasibility.md`](mcp-server-feasibility.md)（可行性分析与初步设计）
**架构决策**: 详见 ADR-13（MCP 独立 Phase + 对外暴露定位）。

| # | 任务 | 说明 | 优先级 | 状态 |
|---|------|------|--------|------|
| 8.1.1 | **MCP Server 协议骨架** | JSON-RPC 2.0 + initialize/tools/list/tools/call 三件套；先支持 stdio 传输 | P0 | [-] 设计中 |
| 8.1.2 | **AgentCore Tool ↔ MCP Tool 适配层** | `IAgentTool` → MCP `tools/list` schema 映射；`ExecuteAsync` → `tools/call` 桥接；保留 `RequiresMainThread` / 风险等级语义 | P0 | [ ] |
| 8.1.3 | **生命周期与进程模型** | Editor 内 host vs. 独立进程 host 二选一决策；Domain Reload 期间的请求处理策略 | P0 | [ ] |
| 8.1.4 | **安全策略** | Workspace 边界校验、写操作确认、敏感工具白名单；与现有 `WorkspacePathPolicy` 对齐 | P0 | [ ] |
| 8.1.5 | **HTTP / SSE / Streamable HTTP 传输** | 在 stdio 稳定后扩展远程传输，便于与远端 IDE/Agent 平台对接 | P1 | [ ] |
| 8.1.6 | **客户端兼容性验证** | 至少覆盖 Claude Desktop / Cursor / Continue / 自定义 CLI 四类典型客户端 | P1 | [ ] |
| 8.1.7 | **配套文档与示例** | "如何在 X 客户端中接入 AgentCore MCP Server"系列教程；nuget/npm 配套包（如需要） | P1 | [ ] |

---

## 3.y Phase 9 — Prompt 层幻觉护栏（质量加固 / v1.5.x）

**主题**: 通过在两个通用节点强制注入 self-challenge，激活 LLM 已有但被动的元认知能力，降低"结构漂亮但语义粒度不匹配"型幻觉。**质量加固而非新能力**——不涉及新工具、新协议、新对外暴露，纯 prompt + 输出解析 + UI 呈现。
**触发原因**: 多模型（尤其 Qwen 3 VL 等 mid-tier LLM）在 Unity 工作流中反复出现"看似完整的部分答案"型幻觉（原案例："帮我获取选中 object 的 material" → 只返回第一个材质但用户可能期望全部）。SOUL.md 规则已到语义层限制，需要工程侧引入结构化 self-review 补充。
**设计文档**: [`prompt-layer-hallucination-hardening-plan.md`](prompt-layer-hallucination-hardening-plan.md)（v0.10 定稿）
**架构决策**: 详见 ADR-16（Self-Challenge 定位为独立 Phase + 带 kill criteria 实验性发布）
**治理约束**: 不属于治理层 G 系列——Self-Challenge 是 prompt 输出结构化机制，不涉及工具风险策略、能力授权或 Workspace 边界。与 G.5（已归档）的区别在于 G.5 曾试图引入 Operation Journal 架构层组件，Phase 9 完全在现有 AgentLoop 内做增强，不新增架构层。

### 3.y.1 P0 — 核心机制（v1.5.0-alpha1 已交付）

| # | 任务 | 说明 | 预估 | 状态 |
|---|------|------|------|------|
| 9.1.1 | **Node A Intent Self-Challenge 核心机制** | `<intent_challenge>` prompt 模板（5 Step 含 Continuation 模式）+ [`IntentChallengeStreamExtractor`](../Editor/Core/SelfChallenge/IntentChallengeStreamExtractor.cs)（复用 [`VisiblePlanningTraceExtractor`](../Editor/Core/VisiblePlanningTraceExtractor.cs) 骨架）+ [`IntentChallengeParser`](../Editor/Core/SelfChallenge/IntentChallengeParser.cs) 9 项结构校验 + 独立小会话 correction retry | 6 人日 | [x] v1.5.0-alpha1 |
| 9.1.2 | **Node B Answer Self-Challenge 核心机制** | Reviewer prompt 模板 + `AnswerChallengeReviewer` 独立 LLM 调用 + 压缩历史（最近 3 轮 + Node A 关键假设）+ [`AnswerChallengeParser`](../Editor/Core/SelfChallenge/AnswerChallengeParser.cs)（`<draft-quote>` substring 校验）+ 三 Verdict 处理 | 4 人日 | [x] v1.5.0-alpha1 |
| 9.1.3 | **Waiting-for-Clarification 状态机** | [`AgentState.WaitingForClarification`](../Editor/Core/MessageTypes.cs) 枚举 + [`ToolCallDispatcher`](../Editor/Tools/ToolCallDispatcher.cs) 拒绝分发 + [`SessionData`](../Editor/Session/SessionData.cs) / [`DomainReloadState`](../Editor/Core/DomainReloadState.cs) 序列化 + ChatWindow 反问消息状态标签 | 2 人日 | [x] v1.5.0-alpha1 |
| 9.1.4 | **主历史清理规则** | [`AgentLoop.SelfChallenge.cs`](../Editor/Core/AgentLoop.SelfChallenge.cs) 的 `StripChallengeBlocks`：写入 `_messages` 时剥离 `<intent_challenge>` / `<answer_challenge>` / `<intent_challenge_continuation>` 三种块 | 0.5 人日 | [x] v1.5.0-alpha1 |
| 9.1.5 | **强制终止路径 skip Node B** | [`RunToolCallLoopAsync`](../Editor/Core/AgentLoop.Runner.cs) 4 条强制终止路径（单工具连败 / 全失败 / 同目标重复 / 轮次-Token 上限）skip Node B；避免 BLOCK 与循环刹车死锁 | 0.3 人日 | [x] v1.5.0-alpha1 |
| 9.1.6 | **REVISE 单次不复审 + Domain Reload 放行 draft** | Node B REVISE draft 重新生成后直接 `HandleFinalResponse` 不再进 Node B；Reviewer 调用中 domain reload 恢复时放行原 draft | 0.5 人日 | [~] v1.5.0-alpha1（REVISE 已做，Domain Reload 兜底部分实现待 alpha3） |
| 9.1.7 | **SelfChallengeData 序列化 + AgentEvent** | `SelfChallengeData` 完整 schema 挂到 [`SerializableConversationTurn`](../Editor/Session/SessionData.cs)；新增 `IntentChallengeCompleted` / `AnswerChallengeCompleted` / `AnswerChallengeRegenerating` / `AnswerChallengeRegenerated` 事件 | 1 人日 | [x] v1.5.0-alpha1 + alpha2 UI 恢复修复 |
| 9.1.8 | **SelfChallengeCard UI（默认折叠）** | Verdict 徽标（`[v]` / `[~]` / `[!]` / `[?]`）+ 中文 Verdict 标签（通过/已修正/已阻止/等待澄清/意图明确/未触发）+ 复用 [`ToolCallCard`](../Editor/UI/Components/ToolCallCard.cs) 复制按钮模式 + 异常自动展开 | 4 人日 | [x] v1.5.0-alpha1 |
| 9.1.9 | ~~**AgentCoreSettings 配置项**~~ | ~~5 个用户可见字段~~ — **ADR-17 推翻，改为 1 个 `selfChallengeEnabled` 总开关 + 4 个内部常量（[`SelfChallengeConfig`](../Editor/Core/SelfChallenge/SelfChallengeConfig.cs)）** | 0.5 人日 | [x] v1.5.0-alpha1（按 ADR-17 精简版实施） |
| 9.1.10 | ~~**首周引导条款**~~ | ~~一次性 tooltip + 前 5 次强制展开~~ — **ADR-17 推翻**：用户直接观察 Verdict 徽标即可自然感知，不建 tooltip 层 | 0.9 人日 | [x] ADR-17 永不实施 |

### 3.y.2 ~~P1 — Statistics 面板 + 4 周 kill criteria（v1.5.0 上线后）~~ — 已被 ADR-17 部分推翻

> **ADR-17 决议（2026-07-09）**: Statistics 面板 UI 永不实施；4 周 kill criteria 保留但通过用户直接对话反馈判定，不建可视化面板。

| # | 任务 | 说明 | 预估 | 状态 |
|---|------|------|------|------|
| 9.2.1 | ~~**SelfChallengeStatistics 数据层**~~ | ~~ScriptableSingleton 累计 + 3 个 Key Metrics + Health badge~~ | 1 人日 | [x] ADR-17 永不实施 |
| 9.2.2 | ~~**UiDiagnosticsSettingsPage 卡片**~~ | ~~"Self-Challenge Statistics" 卡片（3 个 Key Metrics + Health badge + Export CSV）~~ | 1 人日 | [x] ADR-17 永不实施 |
| 9.2.3 | **4 周窗口 formal review** | v1.5.0 GA 上线后 4 周内基于用户对话反馈判定；异常触发 retrospective 考虑回滚到"仅 UI 展示"轻量方案 | 用户决策 | [ ] v1.5.0 GA 后启动 |

### 3.y.3 Phase 9 里程碑（实际交付）

```
v1.4.9        — Self-Challenge 骨架（SelfChallengeData / SkipRules / Config + 22 单元测试）
v1.5.0-alpha1 — 完整核心机制（Node A + Node B + Waiting for Clarification + Continuation + UI Card）
              + ADR-17 极简即开即用哲学全面落地（Settings 精简 25+ 字段隐藏 + 9 字段删除）
v1.5.0-alpha2 — Session 反序列化后 SelfChallengeCard UI 恢复修复
v1.5.0-alpha4 — model-tier escape (L1-L4-B1) — 高级模型逃逸 Node B
v1.5.0-alpha5 — Settings 分页精简(6→5) + GLM-5.2 全链路适配
v1.5.6~v1.5.7 — 稳定性修复（PreferencesFolder Save hang + offline uninstall）
v1.6.0~v1.6.5 — 产品化体验冲刺（详见 §3.z）
v1.5.0-alpha3 (未定期) — Domain Reload 兜底完整实施 / BLOCK verdict 回 tool loop 完整实施 / Node A/B 单元测试
v1.5.0-beta   — pre-GA 稳定性冲刺 + P1-11 PROJECT.md 模板按钮 + P2-12 AGENTS.md 极简规则沉淀
v1.5.0 GA     — 4 周 kill criteria 实测窗口开启
v1.5.z / v1.6.0 — 4 周 review 结果决定：保留 / 局部调整 / 回滚
```

**实际工作量**: v1.4.9 骨架 ~1.5 人日 + v1.5.0-alpha1 全量核心 ~15 人日 + alpha2 修复 ~0.5 人日 ≈ **17 人日**（与 v0.10 §0.7 估算 17-20 人日一致）

**Kill switch**: `selfChallengeEnabled = false` 一键回到 v1.4.9 骨架前行为。

---

## 3.z v1.6.x — 产品化体验冲刺 (v1.6.0 ~ v1.6.5)

**主题**: 在 Phase 9 alpha 稳定运行的基础上，集中解决用户实战反馈的 UX 感知、工具确认效率、日志噪声和 LLM 适配问题。这一系列不属于任何特定 Phase，是独立的产品化体验冲刺。

**触发原因**: 用户在使用 GLM-5.2 + AgentCore 进行真实 Unity 开发时，反馈了一系列体验问题：点击发送后 UI 无反应、思考过程不可见、工具确认流程繁琐、日志狂刷导致卡顿、流式吐字速度变慢、消息引用无法跳转等。

| # | 版本 | 任务 | 说明 | 状态 |
|---|------|------|------|------|
| Z.1 | v1.6.2 | **PendingIndicator 占位气泡** | 点击发送后消息流内显示灰色气泡 + 3 点动画，覆盖 LLM 首响应前 5-30s 空窗期 | [x] |
| Z.2 | v1.6.2 | **折叠面板活跃度指示器** | ThinkingDrawer 尾部 60 字符实时预览 + ToolCallGroup running 工具名 + active-pulse 蓝色边框 | [x] |
| Z.3 | v1.6.3 | **SSE Yield 时间预算优化** | 从"每 N chunk yield"改为"每 200ms yield"，消除 Hold on 对话框同时不损失吐字速度 | [x] |
| Z.4 | v1.6.4 | **Context Ingest (Ctrl+Shift+X)** | 全局快捷键通用查询入口；6 个 Collector + 路由优先级 + 分级采样 + 15000 字符截断 | [x] |
| Z.5 | v1.6.4 | **ThinkingDrawer 独立展开按钮** | 静态 Arrow Label → 独立 Button（▶/▼），不受 header 拖拽干扰 | [x] |
| Z.6 | v1.6.4 | **输入框滚动 + 流式上翻 + 跳到最新** | 输入框 max-height 260px + ScrollView；流式回复时可上翻 + 右下角浮动按钮 | [x] |
| Z.7 | v1.6.4 | **MessageReferenceBar** | assistant 消息中 `` `Assets/Foo.cs:42` `` 和 `[GameObject: Cube]` 渲染为 chip 按钮可点击跳转 | [x] |
| Z.8 | v1.6.4 | **PlayModePreflight** | Play Mode 中禁止 write 类工具调用（ToolCapability 位标志判定） | [x] |
| Z.9 | v1.6.5 | **日志分级 (AgentCoreLog)** | 5 档 LogLevel + AgentCoreLog 静态封装；默认 Info 级跳过 30 处 Debug 热点；Settings 中可热切换 | [x] |
| Z.10 | v1.6.5 | **YOLO 信任模式** | 3 按钮（Deny / Trust Low-Med / YOLO All）；SessionState 持久化跨 Domain Reload；破坏性移除 Once/SessionExactTarget | [x] |
| Z.11 | v1.6.5+ | **多轮思考窗口** | AssistantTurnView 重写为多轮架构，每轮独立 ThinkingDrawer + 分隔线；HandleLoopRoundStarted 第 2 轮起调 BeginNewRound | [x] |
| Z.12 | v1.6.5+ | **文件删除视觉反馈** | FileChangeSummaryPanel 删除文件路径变红 + "(已删除)" 后缀；PingFileInProject 不再 warn | [x] |
| Z.13 | v1.6.5+ | **GLM-5.2 reasoning 参数适配** | AgentCoreSettings: maxTokens 65536→8192, reasoningEffort→"low", reasoningMaxTokens 0→2048; settingsVersion 18→19 迁移 | [x] |

---

## 4. ADR (Architecture Decision Records)

### ADR-1: 不实现 Markdown 渲染

**状态**: `已决策 — 放弃` | **日期**: 2026-05-09

- **决策**: 不在 UI 中实现 Markdown 渲染（斜体/粗体/链接样式化）
- **原因**: UI Toolkit 富文本支持有限，自定义渲染成本高；当前 `SmartMessageBuilder` 已处理代码块和列表，基本可读
- **替代方案**: 如果未来需要，考虑引入第三方 UI Toolkit Markdown 渲染器

### ADR-2: XR 工具暂不实现

**状态**: `已决策 — 暂时冻结` | **日期**: 2026-05-09

- **决策**: XR 工具（ManageXRTool）冻结，仅用户明确需要时解冻
- **原因**: Unity XR 模块 API 差异大，当前用户基数以传统 PC/Console 开发为主

### ADR-3: 文档状态必须以代码事实校准

**状态**: `已决策 — 代码事实优先` | **日期**: 2026-05-12

- **决策**: ROADMAP 和专项计划中的工具状态必须以实际源码为准
- **执行规则**: 规划工具开发前先读取对应工具源码；旧计划文档统一归档不删除；ROADMAP 是方向层唯一入口

### ADR-4: AgentLoop.cs 拆分策略

**状态**: `已决策 — 采用部分类拆分` | **日期**: 2026-05-09

- **决策**: 使用 C# `partial class` 拆分 `AgentLoop.cs`（共 9 个 partial 文件）
- **原因**: 保持所有实例字段和方法的访问权限不变，减少 Domain Reload 恢复逻辑的耦合风险

### ADR-5: 拒绝模式系统 — AgentCore 是自主智能体

**状态**: `已决策 — 废弃模式系统` | **日期**: 2026-05-18

- **决策**: 废弃"模式系统"（Architect Mode / Review Mode / 模式切换 UI）
- **核心理念**: AgentCore 是智能体（Agent），不是 IDE；应根据对话上下文自动识别用户需求，自主选择合适的行为模式
- **替代方案**: 情境感知增强 + 工具推荐系统 + 响应式建议

### ADR-6: Settings Provider Shell — 设置页禁止回到 God Object

**状态**: `已决策 — 采用 Settings shell + section registry` | **日期**: 2026-05-22

- **决策**: `AgentCoreSettingsProvider` 只承担 Settings shell 职责，业务设置必须迁移到独立 `IAgentCoreSettingsSection`
- **执行规则**: 新增设置项必须归属到明确 section；foldout/异步状态放入 `AgentCoreSettingsState`；连接型设置复用统一模式

### ADR-7: 代码索引采用完全本地化单层架构

**状态**: `已决策 — 单层 SQLite 架构` | **日期**: 2026-06-03

- **决策**: 放弃向量数据库，放弃骨架文档（workspace-skeleton.md），只保留 SQLite 符号索引（Layer 1）
- **原因**: `search_code` 工具按需检索比静态骨架文档更精准、更省 token；骨架文档会随代码变化快速过时；向量数据库引入额外依赖和运维成本
- **影响**: Bootstrap 链简化为 `SOUL → TOOLS → PROJECT(auto) → PROJECT.md(user)`；`BootstrapContext.Skeleton` 属性已删除

### ADR-9: 废弃智能推荐系统（SmartToolRecommender + 响应式建议）

**状态**: `已决策 — 废弃` | **日期**: 2026-06-11

- **决策**: 废弃 6.4.3 SmartToolRecommender 和 6.4.4 响应式建议两个功能
- **核心理由**: Agent 对项目的理解、设计方向和当前开发阶段，永远不如用户明确。基于上下文的主动建议在实践中会产生大量"钻牛角尖"式的无止尽优化建议，浪费 token，干扰用户的实际工作节奏
- **替代方案**: 无。用户主导对话方向，Agent 专注执行用户明确提出的任务
- **影响**: 6.4.3 和 6.4.4 标记为 `[!] 已废弃`；v0.9.7 里程碑改为完整功能测试验收

### ADR-10: 废弃 Rules System — PROJECT.md 已足够

**状态**: `已决策 — 废弃` | **日期**: 2026-06-12

- **决策**: 完全移除 Rules System（`RulesLoader.cs`、`rulesEnabled` 设置、`read_rules`/`write_rules`/`get_rules_paths` 工具 action、Settings UI 卡片、SOUL.md §13 相关内容）
- **原因**: Rules System（`rules.md`）与 PROJECT.md 功能高度重叠——两者都是"项目约定/编码规范"注入 System Prompt。维护两套机制增加了用户认知负担，且 rules.md 的"结构化规则"定位在实践中并未带来额外价值。PROJECT.md 已经足够满足所有规则注入需求。
- **影响**:
  - 删除 `Editor/Bootstrap/RulesLoader.cs`
  - `BootstrapContext` 移除 `Rules` 属性，`CompileSystemPrompt()` 移除规则注入块
  - `BootstrapLoader` 移除 `RulesLoader.Load()` 调用
  - `AgentCoreSettings` 移除 `rulesEnabled` 字段，版本号 9 → 8（回退，因 v9 专为此字段引入）
  - `ManageWorkspaceConfigTool` 移除 `read_rules`/`write_rules`/`get_rules_paths` actions
  - `ContextMemorySettingsPage` 和 `ContextSettingsSection` 移除 Rules System UI 卡片
  - `SOUL.md §13` 移除 rules.md 相关说明、读写时机、决策表条目
  - `TOOLS.md.template` 移除 rules actions 说明和 Tool Selection Guide 条目

### ADR-8: Agent 主动调用规则内嵌于 SOUL.md

**状态**: `已决策 — 采用 SOUL.md 内嵌规则` | **日期**: 2026-06-10

- **决策**: `search_code` 和 `version_control` 的主动调用规则直接写入 `SOUL.md`（§14、§15），而不是通过代码逻辑或 Settings 配置控制
- **原因**: SOUL.md 是 LLM 行为的唯一权威来源；规则写在 SOUL.md 中可以被 LLM 直接理解和执行，无需额外的代码分发机制；与 §11（记忆）、§12（知识库）、§13（Workspace Config）的主动调用规则保持一致的模式
- **影响**:
  - `SOUL.md §2` 补充"索引优先"原则
  - `SOUL.md §4` 反幻觉表新增 `search_code` 和 `version_control` 正确名称
  - `SOUL.md §14` 新增代码索引主动调用规则（对话开始协议 + 6 个强制预查场景 + 搜索策略 + 索引新鲜度）
  - `SOUL.md §15` 新增 VCS 主动调用规则（主动只读查询 + 自然语言映射 + 写操作确认 + VCS 类型感知）
  - `TOOLS.md.template` `search_code` 章节补充对话开始工作流

### ADR-11: v1.0.0 验收以"用户实战使用"为准，而非新增 QA 流程

**状态**: `已决策 — 采用实战验收` | **日期**: 2026-06-16

- **决策**: Phase 6 收尾不再走"专门一轮 Round 1~4 全量回归测试"流程，改以**用户在真实项目中持续使用 v0.9.x ~ v0.9.9** 累积的实战反馈作为 v1.0.0 的验收依据
- **原因**:
  - v0.9.x 系列累计 9 个补丁版本，每个版本均经过用户在真实 Unity 项目中的使用验证；新增 QA 轮次的边际收益已经很低
  - 用户作为唯一最终用户兼 PO，对功能完整度和稳定性有第一手判断
  - 实战反馈已经识别出真正需要的优化方向（后台静默 + 增量索引、MCP 对外互操作），这些进入 v1.0.0 之后的派生 Phase
- **影响**:
  - Phase 6 §2.5 里程碑表更新为"v1.0.0 — Phase 6 完成里程碑（用户实战验收通过）"
  - 6.5.1（Diff 视图）改为外部委托方案闭环（见 ADR-12）
  - 6.5.2/6.5.3（主题、快捷键）评估后判定为低 ROI，不纳入 Phase 6/7 范围
  - 6.2.6（后台静默 + 增量索引）从 Phase 6 派生为 Phase 7 §3.1（v1.1.0）
- **不影响**: 后续 Phase 7 / Phase 8 的具体功能仍然遵循 `AGENTS.md` §12.6 的 Round 1~4 验收流程

### ADR-12: 文件变更 Diff 视图采用外部 VCS 工具委托方案

**状态**: `已决策 — 委托外部 diff 工具` | **日期**: 2026-06-16

- **决策**: 6.5.1 文件变更 Diff 视图**不在 Editor 内自建 side-by-side 视图**；改为由 `version_control` 工具新增 `open_diff` action，委托宿主 VCS 的原生 diff 工具呈现：
  - SVN → 调用 TortoiseSVN `TortoiseProc.exe /command:diff` 或等效平台命令
  - Perforce → 调用 P4V / `p4 diff2` / `p4 diff` 命令
  - Git → 调用 `git difftool` / `git diff`（用户已配置的外部 diff tool）
- **原因**:
  - 用户已经在真实工作流中熟悉了自己 VCS 客户端的 diff 体验（TortoiseSVN / P4V / VS Code Diff 等），自建视图会形成"另一个需要切换的工具"
  - 自建 side-by-side 视图意味着重新实现行级 diff 算法、语法高亮、Unity 资源 diff 兼容（.unity / .prefab / .asset 的 YAML diff 等），工程成本远超收益
  - AgentCore 的核心定位是"Editor 内 AI Agent + 工具调用"，不是"VCS 客户端"；与 ADR-1（不实现 Markdown 渲染）属于同类决策（不重复造已有生态的轮子）
- **影响**:
  - `version_control` 工具新增 `open_diff` action（参数：path、revision1、revision2 或 working-vs-head）
  - `VcsSettings` 新增 diff tool 偏好配置（auto / external command / VS Code 等），与现有 `WorkspaceVcsType` 联动
  - SOUL.md §15 增补"用户要求查看变更"时优先调用 `open_diff` 而非 `read_file` 重读
  - 文档站补充"如何配置外部 diff 工具"指南
- **拒绝替代方案**:
  - "在 Editor 内自建简化版 side-by-side 视图" — 与 ADR-12 主决策直接冲突
  - "嵌入 monaco-diff / VS Code diff webview" — Editor 不支持 webview，且引入额外依赖

### ADR-13: MCP Server 设为独立 Phase 8，对外互操作独立编排

**状态**: `已决策 — 独立 Phase 平行推进` | **日期**: 2026-06-16 | **更新**: 2026-06-29（Plugin 归档后简化描述）

- **决策**: 将 MCP（Model Context Protocol）Server 能力提升为独立的 **Phase 8**，与 Phase 7（索引体验与产品化）平行推进，而非作为 Phase 7 内的一个子任务
- **核心理由**:
  - **对外/对内边界清晰**: Phase 7 = 对内（索引 / 可观测性 / 产品化分发）；MCP Server = 对外（把 AgentCore 工具暴露给外部 IDE / CLI / Agent 平台）
  - **触发原因不同**: Phase 7 §3.1 后台索引派生于"v1.0.0 实战验收识别的性能优化项"；Phase 8 派生于"用户希望兼容自己原本的 IDE/CLI 工作流"。两个需求独立产生，应独立编排
  - **风险特征不同**: MCP 涉及跨进程协议、安全边界（写操作 / Workspace 边界）、客户端兼容性矩阵；与 Phase 7 内部任务的风险栈完全不同，混在一起会污染优先级判断
  - **可平行**: MCP 适配层主要是对 `IAgentTool` / `ToolAutoDiscovery` 的桥接，对 Phase 7 的索引改造代码无强耦合；两条线可在产品规划上平行推进，但 MCP 实现不得绕过治理层的工具风险策略、能力授权和 Workspace 边界
- **影响**:
  - ROADMAP §1 战略目标新增 Phase 8 行；§3 拆为 Phase 7（§3.1 ~ §3.4）+ Phase 8（§3.x 独立章节）
  - `mcp-server-feasibility.md` §9 ROADMAP 关系章节明确"独立 Phase 8"
  - 风险评估（§5）新增 MCP 跨进程安全 / 客户端兼容性两条风险
- **拒绝替代方案**:
  - "把 MCP Server 作为 Phase 7 的 7.x 子任务" — 边界不清，会被 Phase 7 的产品化任务（UPM / 文档站）挤压优先级
  - "v1.0.0 之前直接合入 Phase 6" — Phase 6 已通过实战验收完成，回灌新协议层会破坏验收基线

### ADR-14: LLM/Agent 架构修复准则作为 Phase 7/8 前置治理层

**状态**: `已决策 — 先安全收口再扩展能力` | **日期**: 2026-06-23

- **决策**: 将 [`llm-agent-architecture-remediation-plan.md`](llm-agent-architecture-remediation-plan.md) 设为 Phase 7/8 之前必须对齐的架构治理准则。它不是独立产品 Phase，而是所有后续工具扩展、自动化执行、Plugin 与 MCP 对外暴露的前置约束。
- **核心理由**:
  - 当前 AgentCore 已具备文件、代码执行、VCS、索引和云端工具能力，继续扩展前必须先建立统一 Tool Risk Policy、WorkspacePathPolicy 强制接入、能力授权和审计闭环。
  - MCP 会把内部工具暴露给外部 IDE/CLI/Agent 平台，若未先收紧工具边界，会放大 prompt injection、越权写入和误选工具风险。
  - Lazy Tool Discovery 与 CompletionGate 是降低工具 schema tax、上下文污染和静默失败的基础设施，不应在工具数量继续增长后再补。
- **影响**:
  - §1 战略目标新增治理层。
  - §2.x 新增 LLM/Agent 架构安全收口任务表。
  - Phase 7/8 保持产品模块定位，但实现顺序受治理层约束。
  - §7 下一步行动建议改为优先执行治理层 G.1。

### ADR-16: Self-Challenge 定位为独立 Phase 9 + 带 kill criteria 实验性发布

**状态**: `已决策 — 独立质量加固 Phase，4 周实测决定去留` | **日期**: 2026-07-08

- **决策**: 把 Prompt 层幻觉护栏（Self-Challenge 双节点机制）设为独立的 **Phase 9**，与 Phase 7 / Phase 8 平行，定位为"质量加固"而非"新能力"；v1.5.0 上线时**带 §5.4 4 周 kill criteria**——上线后 4 周内基于 Statistics 面板 5 项健康阈值做 formal review，异常即回滚或降级
- **核心理由**:
  - **不属于治理层 G 系列**: G.4~G.6 已归档，理由是"架构宇航员式设计"。Self-Challenge 完全在现有 AgentLoop 内做 prompt 输出结构化增强，不新增架构层（无 Operation Journal / Planner-Executor-Verifier 分层），与 G.5/G.6 的归档理由不冲突
  - **不属于 Phase 7 / Phase 8**: Phase 7 = 对内产品化（索引 / UI / UPM），Phase 8 = 对外互操作（MCP）；Self-Challenge 既不改产品分发也不涉及对外协议，独立编排避免优先级污染
  - **必须带 kill switch**: 方案设计文档 §5.4 明确承认 R7/R16/R17 三条根本性风险无法在设计阶段消除（LLM 追责链只能抓 LLM 意识到的假设 / self-challenge 在 Unity Agent 场景无直接证据证明有效 / SOUL 里更严格的规则已经不生效凭什么相信新加的会生效）。**只能靠上线数据判定**，不接受"设计确信"作为交付依据
  - **成本代价可接受**: v0.10 修订估算 17~20 人日，Token 增量 +10~50% 但对短对话稀释后可接受；提供 `legacySelfChallengeDisabled` 关闭开关兜底
- **影响**:
  - §1 战略目标表新增 Phase 9 行
  - §3.y 新增 Phase 9 完整任务表（9.1.1 ~ 9.2.3）
  - §5 风险评估新增"Self-Challenge 对弱模型结构化输出合规能力依赖 / rubber-stamp / 用户感知变慢"三条风险
  - §6 文档索引新增 `prompt-layer-hallucination-hardening-plan.md` 条目
  - §7 下一步行动优先级不受 Self-Challenge 影响（MCP 与产品化仍为 P0/P1，Self-Challenge 待用户决策是否进入 P0 队列）
- **拒绝替代方案**:
  - "把 Self-Challenge 作为治理层 G.7" — 违背 G.4~G.6 归档时确立的"治理层不做 prompt 加固，只做架构级安全约束"边界
  - "作为 Phase 7 §3.5 内的一个子任务" — 与 Phase 7 产品化任务在优先级和风险栈上没有关联，混编会打乱两者节奏
  - "直接合入 v1.4.x patch" — 17~20 人日规模不属于 patch，且带 kill switch 的实验性发布应该走 Minor 版本以便回滚

### ADR-15: 归档 Plugin 系统 — MCP + 现有 ToolAutoDiscovery 已覆盖需求

**状态**: `已决策 — 归档不实现` | **日期**: 2026-06-29

- **决策**: 将 Phase 7 §3.3 Plugin / Extension 系统从开发计划中归档，不再作为开发目标
- **核心理由**:
  - **现有机制已满足**: `[AgentTool]` + `IAgentTool` + `ToolAutoDiscovery` 天然支持用户在自己项目中通过 Editor asmdef 添加自定义工具（标注 `[AgentTool]` 即可被自动发现注册），无需额外 Plugin 框架
  - **MCP 覆盖外部扩展**: MCP Server（Phase 8）将允许外部 IDE/CLI/Agent 平台调用 AgentCore 工具集，覆盖了 Plugin 系统原本想解决的"从外部扩展 AgentCore 能力"的场景
  - **ROI 不足**: Plugin 系统需要额外的加载契约设计、隔离策略、设置面板和文档，但实际需求方（用户自己）已经可以直接通过 asmdef 实现同等效果
- **影响**:
  - §3.3 任务标记为 `[!] 归档`
  - Phase 7 描述从"内部扩展、索引体验深化与 Chat 可观测性"简化为"索引体验深化、Chat 可观测性与产品化"
  - §1 战略目标表移除 Plugin 相关描述
  - §5 风险评估移除 Plugin 崩溃 Editor 风险条目
  - ADR-13 标题简化（移除"与 Plugin 系统形成对照"措辞）
- **不影响**:
  - 现有 `OptionalComponentManager` / `IAgentCorePanelContribution` / `IAgentCoreSettingsContribution` 扩展机制保持不变（这些是内置可选组件用的，不是用户 Plugin 用的）
  - 用户仍可通过 Editor asmdef + `[AgentTool]` 自行添加工具（这是框架天然能力，不需要"Plugin 系统"）

### ADR-17: 极简即开即用哲学 (Minimalism Everywhere)

**状态**: `已决策 — 全面落地` | **日期**: 2026-07-09 | **详见**: [`adr-17-minimalism.md`](adr-17-minimalism.md)

- **决策**: 全产品采纳"用户装了就能用，零配置默认最优，只有必要时才暴露选项"的极简哲学，作为产品设计基线
- **触发**: Self-Challenge 从 6 个用户字段简化到 1 个总开关（方案乙）后，用户明确要求将此哲学贯彻到全产品
- **5 条实施规则**（写入 AGENTS.md 顶层）:
  1. **默认最优，不问用户**: 若 80% 用户会选同一个值，就写死为默认，**不给 UI 选项**
  2. **一件事一个开关**: 抽出总开关，内部细节写死
  3. **术语必须白话**: 严禁 "Node A" / "Fallback Routing" / "Token Budget" 等工程术语出现在**用户可见字段名**
  4. **高级功能有 Advanced foldout**: 默认折叠 + 明确警告标签
  5. **可选服务用 ServiceCard 模式**: 服务默认关闭，用户明确开启后才展开配置
- **推翻的设计文档条款**:
  - **v0.10 §3.4 (5+ 用户 Self-Challenge 字段)**: 改为 1 个 `selfChallengeEnabled` 总开关 + 4 个内部常量（[`SelfChallengeConfig`](../Editor/Core/SelfChallenge/SelfChallengeConfig.cs)）
  - **v0.10 §5 (Statistics 面板)**: 彻底删除，永不实施
  - **v0.10 §5.4 (4 周 kill criteria 可视化 UI)**: 保留 kill criteria 概念，但通过用户直接对话反馈判断，不建 UI
  - **v0.9 §5.5 (首周引导 tooltip)**: 彻底删除；Verdict 徽标本身就是可观测反馈
- **v1.5.0-alpha1 落地内容**:
  - **AgentCoreSettings 字段清理**: 删除 9 字段、[HideInInspector] 25+ 字段、v17→v18 迁移
  - **5 个 Settings Pages 精简**: Model & Agent / Context & Memory / UI & Diagnostics / Workspace / Tools & Extensions
  - **VCS Settings Contribution 精简**
  - **SelfChallengeConfig 4 常量**: `NodeARetryMax` / `NodeBRetryMax` / `AllowClarificationQuestions` / `CardForcedExpansionCount`
- **语言策略**（ADR-17 定稿后追加决议）:
  - **Settings 界面**: 英文（避免字体缺失导致乱码；配置项本身信息密度需要）
  - **ChatWindow / SelfChallengeCard / AgentEvent 状态标签**: 中文（面向最终用户对话体验）
- **相关文档**: [`plans/minimalism-audit-report.md`](_archive/analysis/minimalism-audit-report.md)（审计报告，已归档）；[`plans/adr-17-minimalism.md`](adr-17-minimalism.md)（决策记录，活跃）
- **不影响**:
  - 治理层 G.1~G.3 边界策略（Tool Risk Policy 等仍生效）
  - Self-Challenge 双节点核心机制本身（只影响用户可见字段与 UI 呈现）
  - Legacy Mode 关闭能力（`selfChallengeEnabled = false` 一键回退）
- **拒绝替代方案**:
  - "保留用户可控性优先" — v0.10 §3.4 已被推翻，因为多数用户不需要为技术细节做决策
  - "改用中文简化 Settings" — 中文导致字体缺失设备显示错误；改为英文简练短述
  - "留 Advanced 折叠区暴露 25+ 内部字段" — 违反规则 1；直接 [HideInInspector]

---

## 5. 风险评估

| 风险 | 可能性 | 影响 | 缓解措施 |
|------|--------|------|---------|
| 后台增量索引在大型 WorkspaceRoot 下出现脏文件队列堆积 | 中 | 中 | DirtyTracker 持久化 + CoalescingScheduler 去抖 + yield gate；提供"强制 Full Index"兜底入口 |
| 后台索引在 Domain Reload 期间状态丢失 | 中 | 中 | DirtyTracker 持久化到 `Library/agentcore-indexing-dirty.json`，跨 Domain Reload 保留 |
| 工具能力继续扩张但缺少统一风险策略 | 高 | 高 | 先落地 Tool Risk Policy、ToolCapability、ActiveToolScope，并在 `ToolCallDispatcher` 强制执行 |
| 高风险文件/代码执行绕过 Workspace 边界或确认流程 | 中 | 高 | 所有写入、删除、移动、复制、执行代码经 `WorkspacePathPolicy` 与确认策略；`ExecuteCodeTool` 降权/拆分 |
| 工具 schema 与长驻 prompt 过大导致上下文污染和误选工具 | 高 | 中 | Lazy Tool Discovery、Bootstrap 预算收口、ContextWindowManager 策略修复 |
| MCP Server 跨进程暴露增加攻击面 | 中 | 高 | 默认仅 stdio + 本机 loopback；HTTP 传输延后；实现前先完成治理层 G.1/G.2/G.3，并与 `WorkspacePathPolicy` 对齐写操作边界 |
| MCP 协议演进导致客户端兼容性问题 | 中 | 中 | 遵循 MCP 官方版本协商；至少覆盖 Claude Desktop / Cursor / Continue / 自定义 CLI 四类客户端验证 |
| ~~Plugin / Extension 系统引入用户工具崩溃 Editor~~ | — | — | 已归档（见 ADR-15）；用户通过 asmdef 自定义工具仍受 `ToolAutoDiscovery` 异常包装保护 |
| Phase 9 弱模型无法稳定输出结构化 `<intent_challenge>` / `<answer_challenge>` 块 | 高 | 中 | correction retry 独立小会话 2 次上限 + retry exhausted 后 fallback 放行主任务；Statistics 面板暴露"结构校验失败率"作为 §5.4 kill criteria 5 项之一（>30% 判定 prompt 失效） |
| Phase 9 Node B rubber-stamp（LLM 认自己写的对） | 中 | 高 | 5 道防线（角色扮演 + 强制格式 + 结构校验 + 假设显式化 + 空转检测）；Statistics 暴露 Verdict 分布（>95% PASS 判定 rubber-stamp）；4 周窗口不达标即回滚 |
| Phase 9 用户感知延迟增加（Node B 额外一次 LLM 调用 +1~3s） | 高 | 中 | Skip 规则（≤15 字符 / 纯 URL）覆盖简短消息；强制终止路径 skip Node B；REVISE 单次不复审；提供 `legacySelfChallengeDisabled` 关闭开关；Statistics 暴露"用户手动关闭比例"作为 kill criteria |
| Phase 9 Token 成本增量 +10~50% | 中 | 中 | 通过压缩后的历史 + Node B 只带最近 3 轮 + 独立 retry 会话不带主历史控制上限；同 kill criteria 5 项监控 |
| 示例项目维护成本过高 | 低 | 低 | 示例项目独立仓库，AgentCore 作为 UPM 依赖引入 |

---

## 6. 文档状态索引

| 文档 | 状态 | 位置 |
|------|------|------|
| [`README.md`](README.md) | 文档导航 | `plans/` 顶层 |
| [`ROADMAP.md`](ROADMAP.md) | **主导方向文档** | `plans/` 顶层 |
| [`enterprise-unity-workflow-requirements.md`](enterprise-unity-workflow-requirements.md) | 企业级 Unity 项目适配需求基准，后续任务上游依据 | `plans/` 顶层 |
| [`llm-agent-architecture-remediation-plan.md`](llm-agent-architecture-remediation-plan.md) | **治理层** LLM/Agent 架构安全收口最终准则；Phase 7/8 工具扩展与 MCP 前置约束 | `plans/` 顶层 |
| [`indexing-background-incremental-design.md`](indexing-background-incremental-design.md) | **Phase 7 §3.1** 后台静默 + 增量索引详细设计（v1.1.0 上游依据） | `plans/` 顶层 |
| [`indexing-scope-layered-and-status-awareness-design.md`](indexing-scope-layered-and-status-awareness-design.md) | **Phase 7 §3.1.1** Scope 层次化索引 + LLM 状态感知详细设计（v1.4.0 上游依据） | `plans/` 顶层 |
| [`mcp-server-feasibility.md`](mcp-server-feasibility.md) | **Phase 8 §3.x** MCP 对外互操作可行性分析与初步设计；实现受治理层 G.1/G.2/G.3 约束 | `plans/` 顶层 |
| [`prompt-layer-hallucination-hardening-plan.md`](prompt-layer-hallucination-hardening-plan.md) | **Phase 9 §3.y** Prompt 层幻觉护栏详细设计（v0.10 定稿，v1.5.0 上游依据）；带 §5.4 4 周 kill criteria 实测决定去留 | `plans/` 顶层 |
| [`vcs-treeview-refactor-plan.md`](_archive/features/vcs-treeview-refactor-plan.md) | ~~已废弃~~ — TreeView 方案废弃，改为扁平列表（v0.9.3 完成），已归档 | `_archive/features/` |
| [`codebase-indexing-phase2-plan.md`](_archive/features/codebase-indexing-phase2-plan.md) | 已完成（v0.9.3）— SQLite 迁移 + 依赖图 + FTS5，已归档 | `_archive/features/` |
| **其他已完成计划** | 历史归档 | [`_archive/features/`](_archive/features/) |
| **重构计划** | 历史归档 | [`_archive/refactoring/`](_archive/refactoring/) |
| **Phase 计划** | 历史归档 | [`_archive/phases/`](_archive/phases/) |
| **技术分析** | 历史归档 | [`_archive/analysis/`](_archive/analysis/) |

**归档文档使用规则**：
- 归档文档仅作历史参考，不作为当前开发依据
- 当前功能状态以 `Editor/` 实际源码为准
- 新功能计划在 `plans/` 顶层创建，完成后移至 `_archive/`

---

## 7. 下一步行动建议

> v1.2.1 已发布。治理层核心（G.1~G.3）已完成，Phase 7 §3.1/§3.2 已交付。后续两个开发方向：MCP Server（Phase 8）和产品化分发（Phase 7 §3.4）。

| 优先级 | 任务 | 原因 |
|--------|------|------|
| P0 | **Phase 8 §3.x MCP Server 协议骨架（8.1.1 ~ 8.1.4）** | 对外互操作核心需求；治理前置 G.1/G.2/G.3 已满足，可直接启动设计与实现 |
| P0 (待用户决策) | **Phase 9 §3.y Self-Challenge 核心机制（9.1.1 ~ 9.1.10）** | 设计文档 v0.10 已定稿；用户已登记进 ROADMAP。**开工前必须按 AGENTS.md §12.4 编码前对齐清单逐项确认**：分阶段交付方案 / 每阶段版本号 / 各阶段验收标准 / 首阶段 500 行代码上限拆分。不建议一次性推 17~20 人日 |
| P1 | **Phase 8 §3.x MCP Server 传输与兼容性（8.1.5 ~ 8.1.7）** | stdio 稳定后扩展 HTTP/SSE 传输；覆盖 Claude Desktop / Cursor / Continue / CLI 四类客户端 |
| P1 | **Phase 7 §3.4 产品化 — UPM 发布流程（7.4.1）** | v1.2.1 已是稳定产品，发布流程可沉淀为自动化脚本 |
| P2 | **Phase 7 §3.4 产品化 — 文档站 + 示例项目（7.4.2 ~ 7.4.5）** | 降低新用户上手门槛；可与 MCP 开发并行推进 |

---

## 8. 维护规则

1. **任务状态同步**: 完成任务将 `[ ]` 改为 `[x]`，开发中改为 `[>]`
2. **版本号绑定**: 每次版本发布后同步更新里程碑状态
3. **新增 ADR**: 如有架构决策变更，在 §4 新增 ADR 条目
4. **季度审视**: 每完成一个 Phase 后重新审视路线图，调整优先级

---

> **本文档由 AI 协助制定，经用户 review 确认后生效。**
> 任何修改请遵循 `AGENTS.md` 第 12 章的开发流程规范。
