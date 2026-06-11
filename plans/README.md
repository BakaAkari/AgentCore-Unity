# AgentCore 计划文档导航

> **最后更新**: 2026-06-11 | **当前版本**: v0.9.5 | **关键规则**: SVN 工作副本根 = AgentCore WorkspaceRoot

本目录包含 AgentCore Unity 插件的规划、设计和架构文档。

---

## 活跃文档（当前开发指导）

| 文档 | 用途 | 状态 |
|------|------|------|
| [**ROADMAP.md**](ROADMAP.md) | **主导方向文档** — 定义 Phase 6-7 的开发路线图、任务清单和 ADR | 活跃维护 |
| [**rules-system-plan.md**](rules-system-plan.md) | **规则系统设计方案（v0.9.6）** — `.agentcore/rules.md` 分层规则文件支持 + 自动注入 System Prompt；对应 ROADMAP 6.4.1 + 6.4.2 | 待确认，编码前对齐 |
| [**enterprise-unity-workflow-requirements.md**](enterprise-unity-workflow-requirements.md) | **企业级 Unity 项目适配需求基准** — 记录大规模地图/模式/资源包/SVN 分线工作流；后续代码索引、VCS、RAG、Memory、工具系统等功能设计的上游依据 | 需求基准，持续参考 |

---

## 归档文档（历史参考）

已完成的计划文档已移至 [`_archive/`](_archive/) 目录，按类型分类：

### Phase 计划（已完成）

所有 Phase 1-5 的详细实施计划已归档至 [`_archive/phases/`](_archive/phases/)：

| 文档 | 完成版本 | 说明 |
|------|---------|------|
| [phase1-plan.md](_archive/phases/phase1-plan.md) | v0.1.0 | Phase 1: 能对话 — LLM 集成、Bootstrap、Chat UI |
| [phase2-plan.md](_archive/phases/phase2-plan.md) | v0.2.0 | Phase 2: 能做事 — Tool Calling（unity-mcp 桥接，已废弃） |
| [phase2.5-native-tools-plan.md](_archive/phases/phase2.5-native-tools-plan.md) | v0.3.0 | Phase 2.5: 原生工具迁移 — 脱离 unity-mcp 依赖 |
| [phase3-plan.md](_archive/phases/phase3-plan.md) | v0.3.1 | Phase 3: 能记忆 — Memory、Session、Mem0/LightRAG |
| [phase4-plan.md](_archive/phases/phase4-plan.md) | v0.3.2~v0.3.7 | Phase 4: 更好用 — UX 打磨、快捷键、工具管理 |

### 重构计划（已完成）

稳定性优先阶段的重构计划已归档至 [`_archive/refactoring/`](_archive/refactoring/)：

| 文档 | 完成版本 | 说明 |
|------|---------|------|
| [stability-first-plan.md](_archive/refactoring/stability-first-plan.md) | v0.4.3~v0.4.6 | 稳定性优先路线 — 测试框架、Schema 校验、文件拆分 |
| [json-schema-validation-plan.md](_archive/refactoring/json-schema-validation-plan.md) | v0.4.4 | JSON Schema 参数预校验 — ToolParameterValidator |
| [agentloop-split-plan.md](_archive/refactoring/agentloop-split-plan.md) | v0.4.5 | AgentLoop partial 拆分 — 9 个文件 |
| [chatwindow-split-plan.md](_archive/refactoring/chatwindow-split-plan.md) | v0.4.6 | ChatWindow partial 拆分 — 9 个文件 |
| [vcs-optional-component-refactor-plan.md](_archive/refactoring/vcs-optional-component-refactor-plan.md) | v0.6.0 | VCS 可选组件化 — define-gated 内置组件 |
| [settings-page-architecture-refactor-plan.md](_archive/refactoring/settings-page-architecture-refactor-plan.md) | v0.6.1 | Settings 页面架构重构 — Settings shell + section registry |
| [bootstrap-refactor-plan.md](_archive/refactoring/bootstrap-refactor-plan.md) | v0.8.x | Bootstrap 链路重构 — SOUL/TOOLS/PROJECT 三层架构 |

### 功能计划（已完成）

已落地的功能设计文档已归档至 [`_archive/features/`](_archive/features/)：

| 文档 | 完成版本 | 说明 |
|------|---------|------|
| [rag-feature-completion-plan.md](_archive/features/rag-feature-completion-plan.md) | Phase 5.2 | RAG 功能补齐 — LightRAG 文档管理、批量索引 |
| [memory-panel-ui-plan.md](_archive/features/memory-panel-ui-plan.md) | v0.4.2 | MemoryPanel UI — 记忆可视化管理 |
| [file-change-tracking-plan.md](_archive/features/file-change-tracking-plan.md) | v0.4.x | 文件变更追踪 — FileChangeTracker |
| [agentcore-workspace-hub-execution-plan.md](_archive/features/agentcore-workspace-hub-execution-plan.md) | v0.4.x | 单主窗口 Hub 架构 — Chat/Knowledge/Memory 模块 |
| [context-compression-system-plan.md](_archive/features/context-compression-system-plan.md) | v0.5.0 | 上下文压缩系统设计 — 工具结果压缩、对话压缩、预算管理 |
| [context-compression-implementation.md](_archive/features/context-compression-implementation.md) | v0.5.0 | 上下文压缩实施文档 — 开发执行手册 |
| [context-visualization-plan.md](_archive/features/context-visualization-plan.md) | v0.5.2 | 上下文压缩可视化 — ContextUsagePanel 设计 |
| [version-control-integration-plan.md](_archive/features/version-control-integration-plan.md) | v0.5.4~v0.5.5 | 版本控制集成 — Git/SVN/Perforce 查询与操作 |
| [workspace-foundation-v0.9.0-p0-plan.md](_archive/features/workspace-foundation-v0.9.0-p0-plan.md) | v0.9.0 | Workspace 基础设施 P0 — WorkspaceContext / Resolver / Service / Config / Safety / Settings |
| [codebase-indexing-phase1-plan.md](_archive/features/codebase-indexing-phase1-plan.md) | v0.9.1 | 代码库索引 Phase 1 — 文件级索引 + 符号检索，单层 SQLite 架构 |
| [codebase-indexing-phase2-plan.md](_archive/features/codebase-indexing-phase2-plan.md) | v0.9.3 | 代码库索引 Phase 2 — SQLite 迁移 + 依赖图构建 + FTS5 全文搜索 |
| [vcs-treeview-refactor-plan.md](_archive/features/vcs-treeview-refactor-plan.md) | ~~废弃~~ | VCS Panel TreeView 重构方案（已废弃，改为扁平列表，v0.9.3 完成） |

### 技术分析（参考文档）

技术选型和架构分析文档已归档至 [`_archive/analysis/`](_archive/analysis/)：

| 文档 | 类型 | 说明 |
|------|------|------|
| [domain-reload-resilience.md](_archive/analysis/domain-reload-resilience.md) | 机制分析 | Domain Reload 恢复方案 — DomainReloadState 设计背景 |
| [mem0-vs-openmemory-analysis.md](_archive/analysis/mem0-vs-openmemory-analysis.md) | 技术选型 | mem0 Server vs OpenMemory MCP 部署对比 |
| [mem0-settings-optimization.md](_archive/analysis/mem0-settings-optimization.md) | UX 优化 | Memory Service 设置界面优化方案 |
| [context-compression-llm-analysis.md](_archive/analysis/context-compression-llm-analysis.md) | 技术选型 | 上下文压缩 LLM 分离式/统一式选型分析 |
| [ai-coding-assistants-analysis.md](_archive/analysis/ai-coding-assistants-analysis.md) | 竞品分析 | Cursor/Cline/Roo Code/OpenCode/Hermes 对比；已确认采用 Roo Code 符号索引路线 |
| [ARCHITECTURE.md](_archive/analysis/ARCHITECTURE.md) | 架构参考 | 系统架构总览 v0.4.8（历史参考，企业级适配见 ROADMAP §0.3） |
| [enterprise-agentcore-implementation-audit.md](_archive/analysis/enterprise-agentcore-implementation-audit.md) | 适配审计 | 已实现功能企业级适配审计（结论已固化到 ROADMAP §0.3） |
| [teamcity-svn-unity-build-quality-plan.md](_archive/analysis/teamcity-svn-unity-build-quality-plan.md) | 外部方案 | Unity + SVN + TeamCity 大型项目构建质量治理草案 |

---

## 文档使用指南

### 对于开发者

1. **开始新功能前** → 查阅 [ROADMAP.md](ROADMAP.md) 确认任务优先级和范围
2. **涉及企业级 Unity 项目、代码索引、VCS、RAG、Memory 或文件工具边界时** → 优先阅读 [enterprise-unity-workflow-requirements.md](enterprise-unity-workflow-requirements.md)
3. **查找历史决策** → 在 [`_archive/`](_archive/) 中搜索相关计划文档

### 对于 AI 助手

1. **优先参考活跃文档** — ROADMAP 是当前开发主导文档
2. **WorkspaceRoot 规则优先** — 默认以 SVN 工作副本根作为 AgentCore WorkspaceRoot；UnityRoot 只是 WorkspaceRoot 下的 Unity 工程子根
3. **代码事实优先** — 当文档与实际代码不一致时，以 `Editor/` 下的源码为准
4. **归档文档仅作历史参考** — 不要基于归档文档推断当前功能状态

### 文档维护规则

- **新增功能计划** → 在 `plans/` 顶层创建 `xxx-feature-plan.md`
- **功能完成后** → 在文档顶部标注状态并移至 `_archive/features/`
- **ROADMAP 更新** → 每次版本发布后同步更新任务状态

---

## 相关文档

- [**AGENTS.md**](../AGENTS.md) — LLM 开发规范（编码规则、工具开发、流程管理）
- [**CHANGELOG.md**](../CHANGELOG.md) — 版本变更日志
- [**package.json**](../package.json) — 当前版本号和依赖

---

> **维护原则**: 保持顶层目录清爽，历史文档及时归档，活跃文档持续更新。
