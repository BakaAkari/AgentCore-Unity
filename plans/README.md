# AgentCore 计划文档导航

> **最后更新**: 2026-05-13 | **当前版本**: v0.4.8

本目录包含 AgentCore Unity 插件的规划、设计和架构文档。

---

## 📚 活跃文档（当前开发指导）

| 文档 | 用途 | 状态 |
|------|------|------|
| [**ROADMAP.md**](ROADMAP.md) | **主导方向文档** — 定义 Phase 5-7 的开发路线图和任务清单 | ✅ 活跃维护（v0.4.8） |
| [**ARCHITECTURE.md**](ARCHITECTURE.md) | **系统架构总览** — 核心设计决策、架构图、模块职责 | ✅ 活跃维护（v0.4.8） |
| [**ai-coding-assistants-analysis.md**](ai-coding-assistants-analysis.md) | AI 编码助手生态分析 — Cursor/Cline/Windsurf 等工具对比 | 📊 参考文档 |

---

## 📦 归档文档（历史参考）

已完成的计划文档已移至 [`_archive/`](_archive/) 目录，按类型分类：

### 🏗️ Phase 计划（已完成）

所有 Phase 1-4 的详细实施计划已归档至 [`_archive/phases/`](_archive/phases/)：

| 文档 | 完成版本 | 说明 |
|------|---------|------|
| [phase1-plan.md](_archive/phases/phase1-plan.md) | v0.1.0 | Phase 1: 能对话 — LLM 集成、Bootstrap、Chat UI |
| [phase2-plan.md](_archive/phases/phase2-plan.md) | v0.2.0 | Phase 2: 能做事 — Tool Calling（unity-mcp 桥接，已废弃） |
| [phase2.5-native-tools-plan.md](_archive/phases/phase2.5-native-tools-plan.md) | v0.3.0 | Phase 2.5: 原生工具迁移 — 脱离 unity-mcp 依赖 |
| [phase3-plan.md](_archive/phases/phase3-plan.md) | v0.3.1 | Phase 3: 能记忆 — Memory、Session、Mem0/LightRAG |
| [phase4-plan.md](_archive/phases/phase4-plan.md) | v0.3.2~v0.3.7 | Phase 4: 更好用 — UX 打磨、快捷键、工具管理 |

### 🔧 重构计划（已完成）

稳定性优先阶段的重构计划已归档至 [`_archive/refactoring/`](_archive/refactoring/)：

| 文档 | 完成版本 | 说明 |
|------|---------|------|
| [stability-first-plan.md](_archive/refactoring/stability-first-plan.md) | v0.4.3~v0.4.6 | 稳定性优先路线 — 测试框架、Schema 校验、文件拆分 |
| [json-schema-validation-plan.md](_archive/refactoring/json-schema-validation-plan.md) | v0.4.4 | JSON Schema 参数预校验 — ToolParameterValidator |
| [agentloop-split-plan.md](_archive/refactoring/agentloop-split-plan.md) | v0.4.5 | AgentLoop partial 拆分 — 9 个文件 |
| [chatwindow-split-plan.md](_archive/refactoring/chatwindow-split-plan.md) | v0.4.6 | ChatWindow partial 拆分 — 9 个文件 |

### ✨ 功能计划（已完成）

已落地的功能设计文档已归档至 [`_archive/features/`](_archive/features/)：

| 文档 | 完成版本 | 说明 |
|------|---------|------|
| [rag-feature-completion-plan.md](_archive/features/rag-feature-completion-plan.md) | Phase 5.2 | RAG 功能补齐 — LightRAG 文档管理、批量索引 |
| [memory-panel-ui-plan.md](_archive/features/memory-panel-ui-plan.md) | v0.4.2 | MemoryPanel UI — 记忆可视化管理 |
| [file-change-tracking-plan.md](_archive/features/file-change-tracking-plan.md) | v0.4.x | 文件变更追踪 — FileChangeTracker |
| [agentcore-workspace-hub-execution-plan.md](_archive/features/agentcore-workspace-hub-execution-plan.md) | v0.4.x | 单主窗口 Hub 架构 — Chat/Knowledge/Memory 模块 |

### 🔍 技术分析（参考文档）

技术选型和架构分析文档已归档至 [`_archive/analysis/`](_archive/analysis/)：

| 文档 | 类型 | 说明 |
|------|------|------|
| [domain-reload-resilience.md](_archive/analysis/domain-reload-resilience.md) | 机制分析 | Domain Reload 恢复方案 — DomainReloadState 设计背景 |
| [mem0-vs-openmemory-analysis.md](_archive/analysis/mem0-vs-openmemory-analysis.md) | 技术选型 | mem0 Server vs OpenMemory MCP 部署对比 |
| [mem0-settings-optimization.md](_archive/analysis/mem0-settings-optimization.md) | UX 优化 | Memory Service 设置界面优化方案 |

---

## 🎯 文档使用指南

### 对于开发者

1. **开始新功能前** → 查阅 [ROADMAP.md](ROADMAP.md) 确认任务优先级和范围
2. **理解系统架构** → 阅读 [ARCHITECTURE.md](ARCHITECTURE.md) 了解核心设计
3. **查找历史决策** → 在 [`_archive/`](_archive/) 中搜索相关计划文档

### 对于 AI 助手

1. **优先参考活跃文档** — ROADMAP 和 ARCHITECTURE 是当前开发的主导文档
2. **代码事实优先** — 当文档与实际代码不一致时，以 `Editor/` 下的源码为准
3. **归档文档仅作历史参考** — 不要基于归档文档推断当前功能状态

### 文档维护规则

- **新增功能计划** → 在 `plans/` 顶层创建 `xxx-feature-plan.md`
- **功能完成后** → 在文档顶部标注状态并移至 `_archive/features/`
- **ROADMAP 更新** → 每次版本发布后同步更新任务状态
- **ARCHITECTURE 更新** → 重大架构变更后同步更新版本号和架构图

---

## 📖 相关文档

- [**AGENTS.md**](../AGENTS.md) — LLM 开发规范（编码规则、工具开发、流程管理）
- [**CHANGELOG.md**](../CHANGELOG.md) — 版本变更日志
- [**package.json**](../package.json) — 当前版本号和依赖

---

> **维护原则**: 保持顶层目录清爽，历史文档及时归档，活跃文档持续更新。
