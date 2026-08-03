# AgentCore 计划文档导航

> **最后更新**: 2026-08-02 | **当前版本**: v1.14.1 | **下一目标**: 待规划 | **关键规则**: SVN 工作副本根 = AgentCore WorkspaceRoot

本目录包含 AgentCore Unity 插件的规划、设计和架构文档。

---

## 活跃文档（当前开发指导）

| 文档 | 用途 | 状态 |
|------|------|------|
| [**ROADMAP.md**](ROADMAP.md) | **主导方向文档** — Phase 6~9 路线图、任务清单、ADR 记录 | 活跃维护 |
| [**v1.13.0/provider-profile-plan.md**](v1.13.0/provider-profile-plan.md) | **v1.13.0 Provider Profile 方案** — 多 Provider 配置管理设计文档 | ✅ 已实施 (v1.13.0) |
| [**v1.10.0/smoke-test-findings.md**](v1.10.0/smoke-test-findings.md) | **v1.10.x Windows 测试发现汇总** — 21 bugs (A~U)，全部已修或驳回 | ✅ 已完成 |
| [**v1.10.0-system-capability-assessment.md**](v1.10.0-system-capability-assessment.md) | **v1.10.0 系统能力评估** — 92→89/100 (Windows 测试后下调)，包含 Part 9 修订 | ⚠️ 2026-07-27 修订 |
| [**v1.10.0-adversarial-audit.md**](v1.10.0-adversarial-audit.md) | **v1.10.0 对抗性闭环校验** — 95%→85% (Windows 测试后下调)，包含附录 C 修订 | ⚠️ 2026-07-27 修订 |
| [**v1.10.0-handoff.md**](v1.10.0-handoff.md) | **v1.10.0 开发交接文档** — 6 个 P1 工具增强 (G04/G05/G06/G07/G08/G09) | ✅ 已发布 |
| [**adversarial-coverage-audit.md**](adversarial-coverage-audit.md) | **对抗性审计方法论** — 三步矩阵校验流程 (覆盖/根因/版本)，**需补 Step 4~7** (Windows 教训) | 方法论基线 |
| [**capability-coverage-audit.md**](capability-coverage-audit.md) | **能力覆盖面审计方法** — A 轴 (Unity 菜单) + B 轴 (API 命名空间) | 方法论基线 |
| [**adr-17-minimalism.md**](adr-17-minimalism.md) | **产品哲学基线** — 极简即开即用：默认最优、一件事一开关、术语白话 | 已定稿 |
| [**adr-18-skill-loading-mechanism.md**](adr-18-skill-loading-mechanism.md) | **Skill 加载机制 ADR** — 运行时按需检索，Tier 2 自演化知识 | Draft，待实施 |
| [**agent-prompt-guidelines.md**](agent-prompt-guidelines.md) | **Agent Prompt 层通用准则** — LLM-based agent system prompt 基础框架 | 活跃维护 |
| [**llm-agent-architecture-remediation-plan.md**](llm-agent-architecture-remediation-plan.md) | **LLM/Agent 架构安全治理准则** — Tool Risk Policy / WorkspacePathPolicy / Lazy Tool Discovery；Phase 7/8 前置依据 | 活跃维护，长期治理约束 |
| [**mcp-server-feasibility.md**](mcp-server-feasibility.md) | **MCP Server 可行性方案（Phase 8）** — 外部 IDE / CLI 通过 MCP 调用 Unity 工具 | 设计基线完成，待启动 |

### 版本历史（最近）

| 版本 | 发布日期 | 主要变更 | 文档 |
|------|---------|---------|------|
| **v1.13.0** | 2026-07-31 | Provider Profile 系统 + Error-driven Request Pruning + RequestPruningRegistry EditorPrefs 持久化 + tool_call.id 跨供应商清洗 | [CHANGELOG.md](../CHANGELOG.md#1130---2026-07-31) |
| **v1.12.0-alpha.7** | 2026-07-29 | Prompt-code 一致性 + 死代码清理 + Category 统一 | [CHANGELOG.md](../CHANGELOG.md#1120-alpha7---2026-07-29) |
| **v1.12.0-alpha.1~6** | 2026-07-28~29 | Session Organization — tag registry / 归档区 / 自动命名 debounce / 移除 Silent mode | [CHANGELOG.md](../CHANGELOG.md) |
| **v1.10.6** | 2026-07-28 | v1.11 hardening 阶段 C — Bug T/U/V/B/D | [CHANGELOG.md](../CHANGELOG.md#1106---2026-07-28) |
| **v1.10.0~v1.10.5** | 2026-07-24~28 | G04 MemoryProfiler + P1 工具增强 + v1.11 hardening A/B | [v1.10.0-handoff.md](v1.10.0-handoff.md) |

---

## 归档文档（历史参考）

已完成、已废弃或已实施的一次性文档已移至 [`_archive/`](_archive/) 目录，按类型分类。

### ADR（已实施/已废弃）

已归档至 [`_archive/adr/`](_archive/adr/)：

| 文档 | 说明 |
|------|------|
| [adr-19-main-thread-unblocking.md](_archive/adr/adr-19-main-thread-unblocking.md) | 主线程阻塞消除重构 — 已 Superseded |
| [adr-self-challenge-model-tier-escape.md](_archive/adr/adr-self-challenge-model-tier-escape.md) | Self-Challenge 模型分级逃逸 — Phase 9 已完成 |
| [adr-self-challenge-resilience-refactor.md](_archive/adr/adr-self-challenge-resilience-refactor.md) | Self-Challenge 韧性重构 — 被 model-tier-escape 废弃 |

### 设计文档（历史决策档案）

已归档至 [`_archive/design/`](_archive/design/)：

| 文档 | 说明 |
|------|------|
| [prompt-layer-hallucination-hardening-plan.md](_archive/design/prompt-layer-hallucination-hardening-plan.md) | Phase 9 Self-Challenge 上游设计 — v1.5.0 已实施，ADR-17 部分推翻 |
| [agent-design-frontier-redesign-2026.md](_archive/design/agent-design-frontier-redesign-2026.md) | Agent 前沿架构参考 — 2026-06 设计基线，已实现 |
| [enterprise-unity-workflow-requirements.md](_archive/design/enterprise-unity-workflow-requirements.md) | 企业级 Unity 项目适配需求基准 — 2026-06，已实现 |

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
| [self-challenge-stage-plan.md](_archive/features/self-challenge-stage-plan.md) | v1.4.9~v1.5.0-alpha1 | Phase 9 Self-Challenge 分阶段实施蓝图 |
| [self-challenge-implementation-report.md](_archive/features/self-challenge-implementation-report.md) | v1.5.0-alpha1 | Self-Challenge 完整实施报告 |
| [indexing-background-incremental-design.md](_archive/features/indexing-background-incremental-design.md) | v1.1.0 | 后台静默 + 增量索引 |
| [indexing-scope-layered-and-status-awareness-design.md](_archive/features/indexing-scope-layered-and-status-awareness-design.md) | v1.4.0 | 索引 Scope 层次化 + 状态感知 |
| [thinking-drawer-design.md](_archive/features/thinking-drawer-design.md) | v1.2.0 | ThinkingDrawer — reasoning / planning trace 可观测 |
| [rules-system-plan.md](_archive/features/rules-system-plan.md) | ~~废弃~~ | 规则系统（与 PROJECT.md 功能重叠） |
| [rag-feature-completion-plan.md](_archive/features/rag-feature-completion-plan.md) | Phase 5.2 | RAG 功能补齐 |
| [memory-panel-ui-plan.md](_archive/features/memory-panel-ui-plan.md) | v0.4.2 | MemoryPanel UI |
| [file-change-tracking-plan.md](_archive/features/file-change-tracking-plan.md) | v0.4.x | 文件变更追踪 |
| [agentcore-workspace-hub-execution-plan.md](_archive/features/agentcore-workspace-hub-execution-plan.md) | v0.4.x | 单主窗口 Hub 架构 |
| [context-compression-system-plan.md](_archive/features/context-compression-system-plan.md) | v0.5.0 | 上下文压缩系统设计 |
| [context-compression-implementation.md](_archive/features/context-compression-implementation.md) | v0.5.0 | 上下文压缩实施文档 |
| [context-visualization-plan.md](_archive/features/context-visualization-plan.md) | v0.5.2 | 上下文压缩可视化 |
| [version-control-integration-plan.md](_archive/features/version-control-integration-plan.md) | v0.5.4~v0.5.5 | 版本控制集成 |
| [codebase-indexing-phase2-plan.md](_archive/features/codebase-indexing-phase2-plan.md) | v0.9.3 | 代码库索引 Phase 2 — SQLite 迁移 + 依赖图 + FTS5 |
| [vcs-treeview-refactor-plan.md](_archive/features/vcs-treeview-refactor-plan.md) | ~~废弃~~ | VCS Panel TreeView（改为扁平列表） |

### 技术分析（参考文档）

已归档至 [`_archive/analysis/`](_archive/analysis/)：

| 文档 | 类型 | 说明 |
|------|------|------|
| [PROJECT-ANALYSIS.md](_archive/analysis/PROJECT-ANALYSIS.md) | 覆盖度分析 | AgentCore vs Unity Skills 能力对比（2026-05-07） |
| [minimalism-audit-report.md](_archive/analysis/minimalism-audit-report.md) | 极简哲学审计 | 全产品审计（结论已固化到 ADR-17） |
| [domain-reload-resilience.md](_archive/analysis/domain-reload-resilience.md) | 机制分析 | Domain Reload 恢复方案 |
| [mem0-vs-openmemory-analysis.md](_archive/analysis/mem0-vs-openmemory-analysis.md) | 技术选型 | mem0 Server vs OpenMemory MCP |
| [mem0-settings-optimization.md](_archive/analysis/mem0-settings-optimization.md) | UX 优化 | Memory Service 设置界面优化 |
| [context-compression-llm-analysis.md](_archive/analysis/context-compression-llm-analysis.md) | 技术选型 | 上下文压缩 LLM 分离式/统一式选型 |

---

## 文档使用指南

### 对于开发者

1. **开始新功能前** → 查阅 [ROADMAP.md](ROADMAP.md) 确认任务优先级和范围
2. **涉及 Settings / UI / 用户可见字段时** → 优先阅读 [adr-17-minimalism.md](adr-17-minimalism.md)，坚持极简哲学
3. **涉及工具暴露、自动执行、文件写入、MCP 或 Agent 自治增强时** → 优先阅读 [llm-agent-architecture-remediation-plan.md](llm-agent-architecture-remediation-plan.md)
4. **查找历史决策** → 在 [`_archive/`](_archive/) 中搜索相关计划文档

### 对于 AI 助手

1. **优先参考活跃文档** — ROADMAP 是当前开发主导文档；ADR-17 是产品哲学基线
2. **极简哲学优先** — 涉及 Settings / UI / 用户可见字段时，必须先对齐 ADR-17
3. **治理层优先** — 涉及工具扩展、MCP、文件写入或自治增强时，必须先对齐 `llm-agent-architecture-remediation-plan.md`
4. **代码事实优先** — 当文档与实际代码不一致时，以 `Editor/` 下的源码为准
5. **归档文档仅作历史参考** — 不要基于归档文档推断当前功能状态

### 文档维护规则

- **新增功能计划** → 在 `plans/` 顶层创建 `xxx-feature-plan.md`
- **功能完成后** → 在文档顶部标注状态并移至 `_archive/` 对应子目录
- **ROADMAP 更新** → 每次版本发布后同步更新任务状态
- **本 README 更新** → 归档/新增文档时同步更新活跃/归档表格

---

## 相关文档

- [**AGENTS.md**](../AGENTS.md) — LLM 开发规范（编码规则、工具开发、流程管理）
- [**CHANGELOG.md**](../CHANGELOG.md) — 版本变更日志
- [**package.json**](../package.json) — 当前版本号和依赖

---

> **维护原则**: 保持顶层目录清爽，历史文档及时归档，活跃文档持续更新。
