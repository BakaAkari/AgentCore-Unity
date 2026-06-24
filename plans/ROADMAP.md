# AgentCore Unity 开发路线图 (Roadmap)

> **版本**: v1.1.0 | **更新日期**: 2026-06-24 | **状态**: Phase 6 验收完成（v1.0.0）；治理层 G.1~G.3 全面完成（v1.1.0，Tool Risk Policy + WorkspacePathPolicy 强制接入 + ExecuteCode 降权 + ActiveToolScope 渐进暴露），Phase 7（索引体验深化）与 Phase 8（MCP 对外互操作）仍为待开发产品模块
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

### 0.4 当前项目快照 (v1.1.0)

| 维度 | 状态 |
|------|------|
| **版本** | 1.1.0 (2026-06-24) — 治理层 G.1~G.3 全面完成（Tool Risk Policy + WorkspacePathPolicy 强制接入 + ExecuteCode 降权 + ActiveToolScope 渐进暴露） |
| **核心架构** | AgentLoop (partial 9 文件) + ChatWindow (partial 9 文件) + ToolAutoDiscovery 重建注册表 + DomainReload 恢复 + Schema 预校验 + ToolScopeResolver 渐进暴露 — 稳定 |
| **Bootstrap 链** | SOUL(+SOUL.ext) → TOOLS → PROJECT(auto) → PROJECT.md(user) — 已完整（Rules System 已废弃，见 ADR-10） |
| **Workspace Config** | `manage_workspace_config` 工具 — Agent 可在 Chat 中读写 PROJECT.md / SOUL.ext.md |
| **UI 框架** | UI Toolkit 动态 Hub 架构；Project Settings 使用 Dashboard + 6 Pages 顶部 Tab 导航；Tools & Extensions 页采用 Per-Component 自包含卡片布局 |
| **云端服务** | Mem0 + LightRAG 基础连接 — 可用（OnDemand 可见性） |
| **VCS 组件** | Working Copy Status 扁平列表 + 多选右键菜单；Chat 工具 `version_control` 支持 Git/SVN/Perforce（`AGENTCORE_VCS` 控制，OnDemand 可见性）；SOUL.md §15 主动调用规则已就绪 |
| **Indexing 组件** | Roslyn 符号索引（JSONL 默认，可选 SQLite）+ `search_code` 工具 15 个 action（`AGENTCORE_INDEXING` 控制，OnDemand 可见性）；Full Index 已验证（298 files, 6453 symbols）；SOUL.md §14 主动调用规则已就绪；**当前为同步阻塞触发，Phase 7 将改造为后台静默 + 增量索引** |
| **Agent 主动性** | SOUL.md §13（Workspace Config）+ §14（代码索引）+ §15（VCS）主动调用规则全部就绪 |
| **上下文参数** | reserveResponseTokens=32K、ContextWindowManager 默认 128K（适配现代大 context LLM） |
| **工具暴露策略** | ActiveToolScope 三级可见性：核心工具 AlwaysVisible（~15 个）、按需工具 OnDemand（~27 个）、受限工具 Restricted（1 个）；LLM 通过 `request_tools` 元工具按需激活 |
| **测试覆盖** | 5 个测试文件 / 90+ test cases + 用户使用过程的实战验收（见 ADR-11） |
| **Phase 6 验收** | 完成 — 见 ADR-11 |
| **治理层进度** | G.1~G.3 全面完成（v1.1.0）；G.4 / G.5 / G.6 待开始 |

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
治理层 (1.0.x):    LLM/Agent 架构安全收口（Tool Risk Policy / WorkspacePathPolicy 强制接入 / Lazy Tool Discovery / CompletionGate）
派生 (1.0.x+):    后台静默 + 增量索引（v1.1.0）→ 兼容用户原本 IDE/CLI 习惯（MCP）
中期 (1.x):        Phase 7 内部扩展生态（Plugin/插件） + Phase 8 对外互操作（MCP Server）
```

| 阶段 | 版本 | 定位 | 核心目标 | 关键成果 | 状态 |
|------|------|------|---------|---------|------|
| **Phase 6** | 0.9.x ~ 1.0.0 | 智能化与体验 | 索引深化、VCS Panel、Settings shell、Per-Component 卡片 | 见 §0.4 / §0.5 | [x] 已完成 |
| **治理层** | 1.0.x | LLM/Agent 架构安全收口（**前置约束**） | Tool Risk Policy、WorkspacePathPolicy 强制接入、ExecuteCodeTool 降权、Lazy Tool Discovery、CompletionGate、Operation Journal | 为 Phase 7/8 的工具扩展与对外暴露提供硬边界 | [-] 设计中 |
| **Phase 7** | 1.0.x ~ 1.x | 内部扩展与索引体验深化（**对内**） | 后台静默 + 增量索引（v1.1.0）、Plugin/Extension 系统、UPM 发布 / 文档站 / 示例项目 / Asset Store | 索引零感知 + 用户可自定义工具 + 可分发产品 | [-] 设计中 |
| **Phase 8** | 与 Phase 7 平行 | MCP 对外互操作（**对外**） | 通过 MCP 协议向外部 IDE / CLI / Agent 平台暴露 AgentCore 工具集，兼容用户既有开发习惯 | AgentCore MCP Server（stdio + HTTP）+ 安全策略 + 配套示例 | [-] 设计中 |

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
| G.4 | **ContextWindowManager / Bootstrap 预算收口** | 降低长驻 prompt 与工具 schema 对上下文的挤占；避免 Context Rot 和 Lost-in-the-Middle | P1 | [ ] |
| G.5 | **CompletionGate + Operation Journal** | 最终回复前检查工具执行、文件变更、错误和未完成高风险操作；Domain Reload 后可恢复/审计 | P1 | [ ] |
| G.6 | **Evidence Pipeline / Planner-Executor-Verifier 分层** | 对复杂任务引入证据缓存、任务账本和验证层，减少多步误差累积 | P2 | [ ] |

**执行规则**: 新增工具、Plugin、MCP、文件写操作扩大化、自动执行能力增强等任务，必须先完成 G.1；涉及外部调用方的 Phase 8 至少需要 G.1/G.2/G.3 作为实现前置条件。

---

## 3. Phase 7 — 内部扩展与索引体验深化（对内 / v1.0.x ~ v1.x）

**主题**: 从"功能完整的开发工具"演化为"可分发、可扩展、零感知"的产品。Phase 7 聚焦**对内**——索引体验、扩展机制、产品化分发，全部围绕 AgentCore 自身。MCP 对外互操作单独走 §3.x Phase 8。

### 3.1 P0 — 后台静默 + 增量索引（v1.1.0，从 Phase 6 派生）

> **触发原因**: v1.0.0 实战验收发现现有索引为同步阻塞式触发，影响 Editor 响应；用户提出需要静默 + 增量形式。
> **设计文档**: [`indexing-background-incremental-design.md`](indexing-background-incremental-design.md)
> **范围**: 仅改造现有索引体验，不引入新存储/新协议。
> **治理约束**: Phase 7 §3.1 可与治理层并行设计，但实现时不得扩大默认工具暴露或绕过 `llm-agent-architecture-remediation-plan.md` 的安全策略。

| # | 任务 | 说明 | 状态 |
|---|------|------|------|
| 7.1.1 | **AssetPostprocessor 主触发源** | `OnPostprocessAllAssets` 替代当前同步触发；imported / deleted / moved 全覆盖 | [ ] |
| 7.1.2 | **DirtyTracker 持久化** | `Library/agentcore-indexing-dirty.json` 跨 Domain Reload 保留脏文件队列 | [ ] |
| 7.1.3 | **CoalescingScheduler** | 合并 + 去抖 + yield gate；避免短时间内重复全量扫描 | [ ] |
| 7.1.4 | **BackgroundIndexService** | `Task.Run` 后台执行，每 N 文件 yield，不阻塞 Editor 主线程 | [ ] |
| 7.1.5 | **CodebaseIndexer.RunTargetedIncrementalAsync** | 跳过 ScanAllFiles，按 dirty/deleted 集合定向更新 SQLite | [ ] |
| 7.1.6 | **IndexingStatusBus + Hub Badge** | 状态枚举 Idle/Pending/Running/Failed/Disabled；Hub 会话头部右侧 ChipBadge 静默呈现 | [ ] |
| 7.1.7 | **SOUL.md §14 / TOOLS.md.template 增补** | LLM 感知"索引可能正在后台更新"的规则，避免在 Pending 状态强行依赖陈旧结果 | [ ] |

### 3.2 P1 — Plugin / Extension 系统（对内扩展）

> **定位**: 允许用户在不修改 AgentCore 源码的前提下自定义工具脚本并动态加载（Editor 级别）。
> **与 MCP 的边界**: Plugin = **对内**（用户在 Unity 项目内扩展 AgentCore 行为）；MCP = **对外**（外部 IDE/CLI 调用 AgentCore）；两者不互相替代。

| # | 任务 | 说明 | 优先级 | 状态 |
|---|------|------|--------|------|
| 7.2.1 | **Plugin 加载契约** | 复用现有 `[AgentTool]` + `IAgentTool` + `ToolAutoDiscovery`；定义"用户工具程序集"扫描规则与隔离策略 | P1 | [ ] |
| 7.2.2 | **Plugin 设置面板** | Settings 中列出已发现的用户工具，支持启用/禁用、查看元数据 | P1 | [ ] |
| 7.2.3 | **示例 Plugin 模板** | 提供 Hello World 级别的用户工具模板仓库 | P2 | [ ] |

### 3.3 P1 — 产品化与分发

| # | 任务 | 说明 | 优先级 | 状态 |
|---|------|------|--------|------|
| 7.3.1 | **UPM 发布流程** | 自动化打包、版本标签、发布检查清单 | P0 | [ ] |
| 7.3.2 | **文档网站** | 使用 Docusaurus/VitePress 搭建静态文档站（托管于 GitHub Pages） | P1 | [ ] |
| 7.3.3 | **示例项目** | 完整示例：3D 平台跳跃游戏从零开发（演示 AgentCore 全部能力） | P1 | [ ] |
| 7.3.4 | **多 LLM 后端** | 支持 Claude、Gemini、本地 Ollama 等（统一接口） | P2 | [ ] |
| 7.3.5 | **Unity Asset Store 提交** | 整理元数据、截图、描述文案，完成 Asset Store 提交 | P2 | [ ] |

---

## 3.x Phase 8 — MCP 对外互操作（对外 / 与 Phase 7 平行）

**主题**: 通过 [Model Context Protocol](https://modelcontextprotocol.io) 把 AgentCore 已有的工具集（Native / Cloud / FileSystem / Indexing / VCS）暴露给外部 IDE / CLI / Agent 平台，**兼容用户原本的开发习惯**。
**触发原因**: v1.0.0 验收过程中识别——用户希望在不离开自己惯用的 IDE/CLI 工作流的前提下使用 AgentCore 能力。
**与 Phase 7 的边界**: Phase 7 = 对内（Plugin / 索引 / 分发），Phase 8 = 对外（MCP Server）；两个 Phase 在产品规划上平行推进，但 Phase 8 的实现必须先满足治理层 G.1/G.2/G.3 的安全前置条件。
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

### ADR-13: MCP Server 设为独立 Phase 8，与 Plugin 系统形成"对外/对内"对照

**状态**: `已决策 — 独立 Phase 平行推进` | **日期**: 2026-06-16

- **决策**: 将 MCP（Model Context Protocol）Server 能力提升为独立的 **Phase 8**，与 Phase 7（内部扩展与索引体验深化）平行推进，而非作为 Phase 7 内的一个子任务
- **核心理由**:
  - **对外/对内边界清晰**: Plugin / Extension 系统 = 用户在 Unity 项目内扩展 AgentCore（**对内**）；MCP Server = 把 AgentCore 工具暴露给外部 IDE / CLI / Agent 平台（**对外**）。两者解决的是不同方向的扩展性问题，不互相替代
  - **触发原因不同**: Phase 7 §3.1 后台索引派生于"v1.0.0 实战验收识别的性能优化项"；Phase 8 派生于"用户希望兼容自己原本的 IDE/CLI 工作流"。两个需求独立产生，应独立编排
  - **风险特征不同**: MCP 涉及跨进程协议、安全边界（写操作 / Workspace 边界）、客户端兼容性矩阵；与 Phase 7 内部任务的风险栈完全不同，混在一起会污染优先级判断
  - **可平行**: MCP 适配层主要是对 `IAgentTool` / `ToolAutoDiscovery` 的桥接，对 Phase 7 的索引改造代码无强耦合；两条线可在产品规划上平行推进，但 MCP 实现不得绕过治理层的工具风险策略、能力授权和 Workspace 边界
- **影响**:
  - ROADMAP §1 战略目标新增 Phase 8 行；§3 拆为 Phase 7（§3.1 ~ §3.3）+ Phase 8（§3.x 独立章节）
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
| Plugin / Extension 系统引入用户工具崩溃 Editor | 低 | 中 | 复用 `ToolAutoDiscovery` 的反射隔离 + 异常包装；Settings 提供"一键禁用所有用户工具"开关 |
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
| [`mcp-server-feasibility.md`](mcp-server-feasibility.md) | **Phase 8 §3.x** MCP 对外互操作可行性分析与初步设计；实现受治理层 G.1/G.2/G.3 约束 | `plans/` 顶层 |
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

> v1.0.0 已发布并通过用户实战验收。Phase 7/8 仍是后续两个产品模块，但下一步先执行 LLM/Agent 架构安全收口，避免在工具数量和对外暴露继续增长后再补安全边界。

| 优先级 | 任务 | 原因 |
|--------|------|------|
| P0 | **治理层 G.1 Tool Risk Policy + WorkspacePathPolicy 强制接入** | 后续所有新增工具、Plugin、MCP 和文件写操作扩大化都依赖统一风险策略；这是继续扩展 Agent 能力前的硬前置 |
| P0 | **治理层 G.2/G.3 ExecuteCodeTool 降权 + Lazy Tool Discovery** | 直接降低高风险执行、工具 schema tax、误选工具和上下文污染风险；也是 MCP 对外暴露前置条件 |
| P1 | **Phase 7 §3.1 后台静默 + 增量索引（v1.1.0）** | v1.0.0 实战验收最直接的体验痛点；可与治理层并行设计，但实现不得扩大默认工具暴露 |
| P1 | **Phase 8 §3.x MCP Server 协议骨架（8.1.1 ~ 8.1.4）** | 对外互操作需求成立；产品规划可与 Phase 7 平行，但编码需先满足治理层 G.1/G.2/G.3 |
| P2 | **Phase 7 §3.3.1 UPM 发布流程（自动化打包）** | v1.0.0 已是稳定里程碑，发布流程可沉淀为脚本，但不应抢占安全收口优先级 |

---

## 8. 维护规则

1. **任务状态同步**: 完成任务将 `[ ]` 改为 `[x]`，开发中改为 `[>]`
2. **版本号绑定**: 每次版本发布后同步更新里程碑状态
3. **新增 ADR**: 如有架构决策变更，在 §4 新增 ADR 条目
4. **季度审视**: 每完成一个 Phase 后重新审视路线图，调整优先级

---

> **本文档由 AI 协助制定，经用户 review 确认后生效。**
> 任何修改请遵循 `AGENTS.md` 第 12 章的开发流程规范。
