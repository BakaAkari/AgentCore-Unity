# AgentCore 计划文档导航

> **最后更新**: 2026-07-09 | **当前版本**: v1.5.0-alpha2（Phase 9 Self-Challenge 已上线 + ADR-17 极简哲学落地）| **下一目标**: v1.5.0 GA（4 周 kill criteria 实测）+ Phase 8 MCP Server 对外互操作 + 产品化分发 | **关键规则**: SVN 工作副本根 = AgentCore WorkspaceRoot

本目录包含 AgentCore Unity 插件的规划、设计和架构文档。

---

## 活跃文档（当前开发指导）

| 文档 | 用途 | 状态 |
|------|------|------|
| [**ROADMAP.md**](ROADMAP.md) | **主导方向文档** — Phase 6~9 路线图、任务清单、ADR 记录（ADR-1 ~ ADR-17） | 活跃维护 |
| [**adr-17-minimalism.md**](adr-17-minimalism.md) | **产品哲学基线** — 极简即开即用：默认最优、一件事一开关、术语白话、Advanced foldout、ServiceCard 模式；推翻 v0.10 §3.4/§5/§7.1 用户可控可观测优先 | 已定稿（2026-07-09） |
| [**prompt-layer-hallucination-hardening-plan.md**](prompt-layer-hallucination-hardening-plan.md) | **Phase 9 Self-Challenge 上游设计** — Node A/B 双节点 prompt 层幻觉护栏 v0.10 定稿；ADR-17 已推翻 §3.4/§5/§7.1（Statistics 面板 / 首周引导 / 多用户配置字段） | 已实施 v1.5.0-alpha1/alpha2；ADR-17 部分推翻 |
| [**llm-agent-architecture-remediation-plan.md**](llm-agent-architecture-remediation-plan.md) | **LLM/Agent 架构安全治理准则** — Tool Risk Policy / WorkspacePathPolicy / Lazy Tool Discovery / CompletionGate / Operation Journal；Phase 7/8 前置依据 | 活跃维护，长期治理约束 |
| [**mcp-server-feasibility.md**](mcp-server-feasibility.md) | **MCP Server 可行性方案（Phase 8 §3.x）** — 外部 IDE / CLI / Agent 平台通过 MCP 协议调用 Unity 工具；对应 ROADMAP 8.1.1 ~ 8.1.7（见 ADR-13）；治理层 G.1~G.3 前置已满足 | 设计基线完成，待启动 |
| [**enterprise-unity-workflow-requirements.md**](enterprise-unity-workflow-requirements.md) | **企业级 Unity 项目适配需求基准** — 大规模地图/模式/资源包/SVN 分线工作流；后续代码索引、VCS、RAG、Memory、工具系统等功能设计的上游依据 | 需求基准，持续参考 |
| [**agent-design-frontier-redesign-2026.md**](agent-design-frontier-redesign-2026.md) | **Agent 前沿架构参考** — Claude Code / Augment Code 等 2026 前沿实践对比，长期演进方向 | 参考基线，长期演进 |

---

## 归档文档（历史参考）

已完成、已废弃或已实施的一次性文档已移至 [`_archive/`](_archive/) 目录，按类型分类。

### Phase 计划（已完成）

已归档至 [`_archive/phases/`](_archive/phases/)：

| 文档 | 完成版本 | 说明 |
|------|---------|------|
| [phase1-plan.md](_archive/phases/phase1-plan.md) | v0.1.0 | Phase 1: 能对话 — LLM 集成、Bootstrap、Chat UI |
| [phase2-plan.md](_archive/phases/phase2-plan.md) | v0.2.0 | Phase 2: 能做事 — Tool Calling（unity-mcp 桥接，已废弃） |
| [phase2.5-native-tools-plan.md](_archive/phases/phase2.5-native-tools-plan.md) | v0.3.0 | Phase 2.5: 原生工具迁移 — 脱离 unity-mcp 依赖 |
| [phase3-plan.md](_archive/phases/phase3-plan.md) | v0.3.1 | Phase 3: 能记忆 — Memory、Session、Mem0/LightRAG |
| [phase4-plan.md](_archive/phases/phase4-plan.md) | v0.3.2~v0.3.7 | Phase 4: 更好用 — UX 打磨、快捷键、工具管理 |

### 重构计划（已完成）

已归档至 [`_archive/refactoring/`](_archive/refactoring/)：

| 文档 | 完成版本 | 说明 |
|------|---------|------|
| [stability-first-plan.md](_archive/refactoring/stability-first-plan.md) | v0.4.3~v0.4.6 | 稳定性优先路线 — 测试框架、Schema 校验、文件拆分 |
| [json-schema-validation-plan.md](_archive/refactoring/json-schema-validation-plan.md) | v0.4.4 | JSON Schema 参数预校验 — ToolParameterValidator |
| [agentloop-split-plan.md](_archive/refactoring/agentloop-split-plan.md) | v0.4.5 | AgentLoop partial 拆分 — 9 个文件 |
| [chatwindow-split-plan.md](_archive/refactoring/chatwindow-split-plan.md) | v0.4.6 | ChatWindow partial 拆分 — 9 个文件 |
| [vcs-optional-component-refactor-plan.md](_archive/refactoring/vcs-optional-component-refactor-plan.md) | v0.6.0 | VCS 可选组件化 — define-gated 内置组件 |
| [settings-page-architecture-refactor-plan.md](_archive/refactoring/settings-page-architecture-refactor-plan.md) | v0.6.1 | Settings 页面架构重构 — Settings shell + section registry |

### 功能计划（已完成）

已归档至 [`_archive/features/`](_archive/features/)：

| 文档 | 完成版本 | 说明 |
|------|---------|------|
| [self-challenge-stage-plan.md](_archive/features/self-challenge-stage-plan.md) | v1.4.9~v1.5.0-alpha1 | Phase 9 Self-Challenge 分阶段实施蓝图（Stage 1-9 已实施，Stage 10 由 ADR-17 推翻） |
| [self-challenge-implementation-report.md](_archive/features/self-challenge-implementation-report.md) | v1.5.0-alpha1 | Self-Challenge 完整实施报告 |
| [indexing-background-incremental-design.md](_archive/features/indexing-background-incremental-design.md) | v1.1.0 | 后台静默 + 增量索引 — DirtyTracker / CoalescingScheduler / BackgroundIndexService / IndexingStatusBus |
| [indexing-scope-layered-and-status-awareness-design.md](_archive/features/indexing-scope-layered-and-status-awareness-design.md) | v1.4.0 | 索引 Scope 层次化 + 状态感知 |
| [thinking-drawer-design.md](_archive/features/thinking-drawer-design.md) | v1.2.0 | ThinkingDrawer — LLM reasoning / planning trace 双源可观测 |
| [rules-system-plan.md](_archive/features/rules-system-plan.md) | ~~废弃~~ | 规则系统（已废弃，与 PROJECT.md 功能重叠，见 ADR-10） |
| [rag-feature-completion-plan.md](_archive/features/rag-feature-completion-plan.md) | Phase 5.2 | RAG 功能补齐 — LightRAG 文档管理、批量索引 |
| [memory-panel-ui-plan.md](_archive/features/memory-panel-ui-plan.md) | v0.4.2 | MemoryPanel UI — 记忆可视化管理 |
| [file-change-tracking-plan.md](_archive/features/file-change-tracking-plan.md) | v0.4.x | 文件变更追踪 — FileChangeTracker |
| [agentcore-workspace-hub-execution-plan.md](_archive/features/agentcore-workspace-hub-execution-plan.md) | v0.4.x | 单主窗口 Hub 架构 — Chat/Knowledge/Memory 模块 |
| [context-compression-system-plan.md](_archive/features/context-compression-system-plan.md) | v0.5.0 | 上下文压缩系统设计 |
| [context-compression-implementation.md](_archive/features/context-compression-implementation.md) | v0.5.0 | 上下文压缩实施文档 |
| [context-visualization-plan.md](_archive/features/context-visualization-plan.md) | v0.5.2 | 上下文压缩可视化 — ContextUsagePanel |
| [version-control-integration-plan.md](_archive/features/version-control-integration-plan.md) | v0.5.4~v0.5.5 | 版本控制集成 — Git/SVN/Perforce 查询与操作 |
| [workspace-foundation-v0.9.0-p0-plan.md](_archive/features/workspace-foundation-v0.9.0-p0-plan.md) | v0.9.0 | Workspace 基础设施 P0 |
| [codebase-indexing-phase1-plan.md](_archive/features/codebase-indexing-phase1-plan.md) | v0.9.1 | 代码库索引 Phase 1 |
| [codebase-indexing-phase2-plan.md](_archive/features/codebase-indexing-phase2-plan.md) | v0.9.3 | 代码库索引 Phase 2 — SQLite 迁移 + 依赖图 + FTS5 |
| [vcs-treeview-refactor-plan.md](_archive/features/vcs-treeview-refactor-plan.md) | ~~废弃~~ | VCS Panel TreeView（已废弃，改为扁平列表） |

### 技术分析（参考文档）

已归档至 [`_archive/analysis/`](_archive/analysis/)：

| 文档 | 类型 | 说明 |
|------|------|------|
| [PROJECT-ANALYSIS.md](_archive/analysis/PROJECT-ANALYSIS.md) | 覆盖度分析 | AgentCore vs Unity Skills 能力对比（2026-05-07，历史快照） |
| [minimalism-audit-report.md](_archive/analysis/minimalism-audit-report.md) | 极简哲学审计 | Settings 面板 + 6 页面 + 12 个 plans 文档全产品审计（结论已固化到 ADR-17） |
| [domain-reload-resilience.md](_archive/analysis/domain-reload-resilience.md) | 机制分析 | Domain Reload 恢复方案 — DomainReloadState 设计背景 |
| [mem0-vs-openmemory-analysis.md](_archive/analysis/mem0-vs-openmemory-analysis.md) | 技术选型 | mem0 Server vs OpenMemory MCP 部署对比 |
| [mem0-settings-optimization.md](_archive/analysis/mem0-settings-optimization.md) | UX 优化 | Memory Service 设置界面优化方案 |
| [context-compression-llm-analysis.md](_archive/analysis/context-compression-llm-analysis.md) | 技术选型 | 上下文压缩 LLM 分离式/统一式选型分析 |
| [ai-coding-assistants-analysis.md](_archive/analysis/ai-coding-assistants-analysis.md) | 竞品分析 | Cursor / Cline / Roo Code / OpenCode / Hermes 对比 |
| [ARCHITECTURE.md](_archive/analysis/ARCHITECTURE.md) | 架构参考 | 系统架构总览 v0.4.8（历史参考） |
| [enterprise-agentcore-implementation-audit.md](_archive/analysis/enterprise-agentcore-implementation-audit.md) | 适配审计 | 已实现功能企业级适配审计 |
| [teamcity-svn-unity-build-quality-plan.md](_archive/analysis/teamcity-svn-unity-build-quality-plan.md) | 外部方案 | Unity + SVN + TeamCity 大型项目构建质量治理草案 |

---

## 文档使用指南

### 对于开发者

1. **开始新功能前** → 查阅 [ROADMAP.md](ROADMAP.md) 确认任务优先级和范围
2. **涉及 Settings / UI / 用户可见字段时** → 优先阅读 [adr-17-minimalism.md](adr-17-minimalism.md)，坚持极简哲学
3. **涉及工具暴露、自动执行、文件写入、MCP 或 Agent 自治增强时** → 优先阅读 [llm-agent-architecture-remediation-plan.md](llm-agent-architecture-remediation-plan.md)，并先满足治理层前置条件
4. **涉及企业级 Unity 项目、代码索引、VCS、RAG、Memory 或文件工具边界时** → 优先阅读 [enterprise-unity-workflow-requirements.md](enterprise-unity-workflow-requirements.md)
5. **查找历史决策** → 在 [`_archive/`](_archive/) 中搜索相关计划文档

### 对于 AI 助手

1. **优先参考活跃文档** — ROADMAP 是当前开发主导文档；ADR-17 是产品哲学基线
2. **极简哲学优先** — 涉及 Settings / UI / 用户可见字段时，必须先对齐 ADR-17；不主动新增可配置项
3. **治理层优先** — 涉及工具扩展、MCP、文件写入或自治增强时，必须先对齐 `llm-agent-architecture-remediation-plan.md`
4. **WorkspaceRoot 规则优先** — 默认以 SVN 工作副本根作为 AgentCore WorkspaceRoot；UnityRoot 只是 WorkspaceRoot 下的 Unity 工程子根
5. **代码事实优先** — 当文档与实际代码不一致时，以 `Editor/` 下的源码为准
6. **归档文档仅作历史参考** — 不要基于归档文档推断当前功能状态

### 文档维护规则

- **新增功能计划** → 在 `plans/` 顶层创建 `xxx-feature-plan.md`
- **功能完成后** → 在文档顶部标注状态并 `git mv` 至 `_archive/features/`
- **ROADMAP 更新** → 每次版本发布后同步更新任务状态
- **本 README 更新** → 归档/新增文档时同步更新活跃/归档表格

---

## 相关文档

- [**AGENTS.md**](../AGENTS.md) — LLM 开发规范（编码规则、工具开发、流程管理）
- [**CHANGELOG.md**](../CHANGELOG.md) — 版本变更日志
- [**package.json**](../package.json) — 当前版本号和依赖

---

> **维护原则**: 保持顶层目录清爽，历史文档及时归档，活跃文档持续更新。
