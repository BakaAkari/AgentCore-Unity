# Claude-Mem 项目分析报告

> **项目**: [thedotmack/claude-mem](https://github.com/thedotmack/claude-mem) — 为 Claude Code 构建的持久化记忆压缩系统
>
> **版本**: v12.1.0 (npm) | **许可证**: AGPL-3.0 | **语言**: TypeScript (44,158 行)
>
> **分析日期**: 2026-04-13

---

## 一句话总结

Claude-Mem 是一个 **Claude Code 插件**，通过 5 个生命周期 Hook 自动捕获工具使用观察、用 Claude Agent SDK 生成语义摘要、存入 SQLite + ChromaDB，并在新会话启动时自动注入相关上下文，实现**跨会话持久化记忆**。

---

## 核心架构

```
┌─────────────────────────────────────────────────────────────┐
│                    Claude Code / Gemini CLI                  │
│                                                             │
│  ┌──────────┐  ┌──────────┐  ┌──────────┐  ┌────────────┐  │
│  │ Session  │→ │ Prompt   │→ │ PostTool │→ │ Session    │  │
│  │ Start    │  │ Submit   │  │ Use      │  │ End        │  │
│  └────┬─────┘  └────┬─────┘  └────┬─────┘  └─────┬──────┘  │
│       │              │             │              │          │
└───────┼──────────────┼─────────────┼──────────────┼──────────┘
        │              │             │              │
        ▼              ▼             ▼              ▼
┌─────────────────────────────────────────────────────────────┐
│              Worker Service (Express, port 37777)            │
│                                                             │
│  ┌──────────┐  ┌──────────┐  ┌──────────┐  ┌────────────┐  │
│  │ SDK      │  │ Search   │  │ Session  │  │ Settings   │  │
│  │ Agent    │  │ Manager  │  │ Manager  │  │ Manager    │  │
│  └────┬─────┘  └────┬─────┘  └──────────┘  └────────────┘  │
│       │              │                                       │
│       ▼              ▼                                       │
│  ┌──────────┐  ┌──────────────────────────┐                 │
│  │ Claude   │  │ Search Orchestrator      │                 │
│  │ Agent    │  │ ├─ SQLite Strategy       │                 │
│  │ SDK      │  │ ├─ Chroma Strategy       │                 │
│  │ (观察+   │  │ └─ Hybrid Strategy       │                 │
│  │  摘要)   │  └──────────────────────────┘                 │
│  └────┬─────┘                                               │
│       │                                                     │
│       ▼                                                     │
│  ┌──────────────────────────────────────┐                   │
│  │         Storage Layer                │                   │
│  │  ┌──────────┐    ┌───────────────┐   │                   │
│  │  │ SQLite   │    │ ChromaDB      │   │                   │
│  │  │ (FTS5)   │    │ (向量嵌入)    │   │                   │
│  │  │ ~/.claude│    │ via MCP       │   │                   │
│  │  │ -mem/    │    │ stdio         │   │                   │
│  │  └──────────┘    └───────────────┘   │                   │
│  └──────────────────────────────────────┘                   │
│                                                             │
│  ┌──────────────────────────────────────┐                   │
│  │  Web Viewer UI (React, port 37777)   │                   │
│  │  实时记忆流 + 搜索 + 设置            │                   │
│  └──────────────────────────────────────┘                   │
└─────────────────────────────────────────────────────────────┘
```

---

## 核心组件详解

### 1. 生命周期 Hook 系统（5 个 Hook + 1 个预检脚本）

| Hook | 触发时机 | 职责 |
|------|---------|------|
| **SessionStart** | 会话开始 | 注入历史上下文到 `CLAUDE.md`（`<claude-mem-context>` 标签） |
| **UserPromptSubmit** | 用户发送消息 | 记录用户提示，转发给 Worker |
| **PostToolUse** | 工具调用完成 | 捕获工具使用结果（文件读写、命令执行等） |
| **Stop** | Claude 停止响应 | 触发摘要生成 |
| **SessionEnd** | 会话结束 | 最终摘要 + 清理 |
| **Smart Install** | 预检脚本 | 缓存依赖检查，确保 Bun/uv 已安装 |

**Hook 退出码策略**：
- `Exit 0` — 成功或优雅关闭
- `Exit 1` — 非阻塞错误（stderr 显示给用户，继续执行）
- `Exit 2` — 阻塞错误（stderr 交给 Claude 处理）

### 2. Worker Service（Express HTTP API）

运行在 **port 37777**，由 Bun 管理进程生命周期。

**HTTP 路由模块**：

| 路由模块 | 端点示例 | 职责 |
|---------|---------|------|
| `SearchRoutes` | `/api/search`, `/api/timeline` | 统一搜索 + 时间线 |
| `MemoryRoutes` | `/api/memory/*` | 记忆 CRUD |
| `SessionRoutes` | `/api/sessions/*` | 会话管理 |
| `SettingsRoutes` | `/api/settings/*` | 配置管理 |
| `DataRoutes` | `/api/data/*` | 数据导入导出 |
| `LogsRoutes` | `/api/logs/*` | 日志查看 |
| `CorpusRoutes` | `/api/corpus/*` | 知识语料库 |
| `ViewerRoutes` | `/` (根路径) | React Web UI |

### 3. SDKAgent — AI 处理核心

- 通过 `@anthropic-ai/claude-agent-sdk` 生成 Claude 子进程
- **事件驱动**（非轮询）的查询循环
- 生成两类输出：
  - **Observations（观察）**：工具使用的结构化记录（标题、事实、概念、文件列表）
  - **Summaries（摘要）**：会话级语义总结（请求、调查、学到、完成、下一步）
- **安全隔离**：禁用所有工具（Bash/Read/Write/Edit/Grep 等），纯观察者角色

### 4. 存储层

#### SQLite（主存储）
- 路径：`~/.claude-mem/claude-mem.db`
- 使用 **FTS5** 全文搜索
- 表结构：`sessions` / `observations` / `summaries` / `prompts` / `timeline`
- 支持迁移系统

#### ChromaDB（向量搜索）
- 通过 **MCP stdio 协议**与 `chroma-mcp` 通信
- 自动同步 SQLite 中的 observations 和 summaries
- 提供语义相似度搜索

### 5. 搜索系统（3 层渐进式披露）

```
Layer 1: search()          → 紧凑索引 (~50-100 tokens/条)
Layer 2: timeline()        → 时间线上下文
Layer 3: get_observations() → 完整详情 (~500-1000 tokens/条)
                             ≈ 10x token 节省
```

**搜索策略**：
- `SQLiteSearchStrategy` — FTS5 关键词搜索
- `ChromaSearchStrategy` — 向量语义搜索
- `HybridSearchStrategy` — 混合策略（自动降级）

### 6. 上下文注入机制

- 在 `CLAUDE.md` 或 `AGENTS.md` 中注入 `<claude-mem-context>` 标签
- 包含最近活动的紧凑表格（ID、时间、类型、标题、token 估算）
- 会话启动时自动更新，保留标签外的原有内容

### 7. 插件技能系统（7 个 Skills）

| 技能 | 职责 |
|------|------|
| `mem-search` | 自然语言搜索历史记忆 |
| `make-plan` | 创建分阶段实施计划 |
| `do` | 使用子代理执行分阶段计划 |
| `smart-explore` | 智能代码探索 |
| `knowledge-agent` | 知识库代理 |
| `timeline-report` | 时间线报告生成 |
| `version-bump` | 版本号管理 |

---

## 技术栈

| 层级 | 技术 |
|------|------|
| **语言** | TypeScript (ESM) |
| **运行时** | Node.js ≥18 + Bun（进程管理 + 测试） |
| **AI SDK** | `@anthropic-ai/claude-agent-sdk` ^0.1.76 |
| **MCP** | `@modelcontextprotocol/sdk` ^1.25.1 |
| **HTTP** | Express 4.x |
| **数据库** | SQLite3 (FTS5) + ChromaDB (via MCP) |
| **前端** | React 18 (内嵌 Web Viewer) |
| **构建** | esbuild |
| **代码分析** | tree-sitter (20+ 语言语法) |
| **模板** | Handlebars |
| **Python 工具** | uv（自动安装，用于 Chroma） |

---

## 支持的平台

| 平台 | 集成方式 |
|------|---------|
| **Claude Code** | 原生插件（Hook + Marketplace） |
| **Gemini CLI** | `npx claude-mem install --ide gemini-cli` |
| **Cursor** | `npm run cursor:install` |
| **OpenClaw Gateway** | `curl -fsSL https://install.cmem.ai/openclaw.sh \| bash` |
| **OpenCode** | 集成插件 (`src/integrations/opencode-plugin/`) |

---

## 数据流

```
用户在 Claude Code 中操作
        │
        ▼
  Hook 捕获事件 ──POST──→ Worker (port 37777)
        │                       │
        │                       ▼
        │               SDKAgent 生成观察/摘要
        │                       │
        │              ┌────────┴────────┐
        │              ▼                 ▼
        │         SQLite 存储      ChromaDB 同步
        │              │                 │
        │              └────────┬────────┘
        │                       │
  下次 SessionStart             │
        │                       │
        ▼                       ▼
  注入上下文到 CLAUDE.md ←── 查询最近活动
```

---

## 隐私与安全

- **`<private>` 标签**：用户可用 `<private>content</private>` 标记敏感内容，在 Hook 层（边缘处理）即被剥离，不进入 Worker/数据库
- **本地存储**：所有数据存储在 `~/.claude-mem/`，不上传到云端
- **观察者隔离**：SDKAgent 禁用所有工具，只能观察不能操作
- **环境隔离**：`env-sanitizer.ts` 清理传递给 SDK 的环境变量

---

## 商业模式

- **开源核心**（AGPL-3.0）：Worker API、搜索、存储、Web Viewer 全部开源
- **Pro 功能**（计划中）：增强 UI（Memory Stream）、高级过滤、时间线回放
- **Pro 架构**：通过 license 验证门控，不修改核心端点，扩展而非替换
- **$CMEM Token**：Solana 社区代币，由第三方创建，作者官方认可

---

## 与 RagMem 的对比

| 维度 | claude-mem | RagMem (本项目) |
|------|-----------|----------------|
| **定位** | Claude Code 插件（个人开发者） | 团队级 AI 基础设施 |
| **记忆类型** | 会话观察 + 摘要（自动捕获） | mem0 对话记忆 + LightRAG 知识库 |
| **存储** | SQLite + ChromaDB（本地） | PostgreSQL + pgvector（Docker） |
| **搜索** | FTS5 + 向量混合 | mem0 语义搜索 + LightRAG 图谱查询 |
| **接入方式** | Claude Code Hook 自动注入 | MCP Server（stdio 协议） |
| **部署** | `npx claude-mem install`（零配置） | Docker Compose（需 WSL2） |
| **AI 处理** | Claude Agent SDK（内置） | 依赖外部 LLM API |
| **适用场景** | 个人开发者跨会话记忆 | 团队知识管理 + 多 Agent 共享 |
| **许可证** | AGPL-3.0 | 内部工具 |

---

## 关键设计亮点

1. **渐进式披露（Progressive Disclosure）**：3 层搜索策略，先索引后详情，节省 ~10x token
2. **事件驱动架构**：SDKAgent 使用 generator 模式，非轮询
3. **边缘隐私处理**：`<private>` 标签在 Hook 层即被剥离
4. **自动依赖管理**：Smart Install 预检脚本自动安装 Bun/uv
5. **上下文注入**：通过 `<claude-mem-context>` 标签无损注入 CLAUDE.md
6. **混合搜索降级**：Chroma 不可用时自动降级到 SQLite FTS5
7. **多平台适配**：同一套核心支持 Claude Code / Gemini CLI / Cursor / OpenClaw

---

## 项目规模

| 指标 | 数值 |
|------|------|
| TypeScript 源码 | 44,158 行 |
| npm 依赖 | 12 个 runtime + 22 个 dev |
| HTTP API 端点 | ~20+ 个 |
| 插件技能 | 7 个 |
| 生命周期 Hook | 5 个 + 1 预检 |
| 搜索策略 | 3 种（SQLite / Chroma / Hybrid） |
| MCP 工具 | 3 个（search / timeline / get_observations） |
| 支持语言语法 | 20+ 种（tree-sitter） |
| i18n 翻译 | 30+ 种语言 |
