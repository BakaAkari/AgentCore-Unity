# LLM AI Toolkit — 完整项目分析报告

> **分析日期**: 2026-04-20
> **分析目的**: 为完全重构为 Unity Agent 插件提供全面的项目理解基础
> **项目路径**: `d:/Works/Party Animals/agentcore-unity`

---

## 1. 项目总览

### 1.1 项目定位

**LLM AI Toolkit** 是一个面向团队的 **AI 基础设施分发包**，核心目标是：

> 让任何 LLM（或人类）都能通过阅读文档 + 运行脚本，在 Windows + WSL2 环境中一键部署完整的 AI 记忆与知识库服务。

项目名称在不同上下文中有不同称呼：
- 工作区名称：`agentcore-unity`
- 分发包名称：`LLM AI Toolkit`
- 内部代号：`RagMem`（核心服务栈）

### 1.2 核心公式

```
Agent = LLM + AGENTS.md(Rules) + Skills(专业能力) + MCP(工具) 
        + Hooks(自动化) + Memory(持久化) + RAG(知识)
```

当前覆盖率约 **40%**（Rules + Skills + 部分 MCP + 部分 Context），主要缺失 Memory 和执行约束机制。

### 1.3 三层架构

```
┌─────────────────────────────────────────────────────┐
│  Layer 3: 部署工具层                                  │
│  deploy.bat / build-dist.bat / FULLY_DEPLOY.bat      │
│  clean-ragmem.bat / prepare-images.sh                │
├─────────────────────────────────────────────────────┤
│  Layer 2: 接入层（MCP Server）                        │
│  ragmem-mcp (Python/FastMCP, stdio 模式)             │
│  9 个 MCP 工具: memory_* + rag_* + ragmem_health     │
├─────────────────────────────────────────────────────┤
│  Layer 1: 核心服务层                                  │
│  mem0 (记忆) + LightRAG (知识库) + pgvector (向量DB)  │
│  Docker Compose 编排，运行在 WSL2 Ubuntu-24.04        │
└─────────────────────────────────────────────────────┘
```

---

## 2. 子系统详细分析

### 2.1 子系统 A：local-ragmem（RagMem 核心服务栈）

**路径**: `local-ragmem/`
**职责**: AI 记忆与知识库的后端服务 + MCP 接入层

#### 2.1.1 MCP Server (`local-ragmem/mcp-server/`)

| 文件 | 行数 | 职责 |
|------|------|------|
| `server.py` | 264 | MCP 工具定义入口，9 个 `@mcp.tool()` 函数 |
| `mem0_client.py` | 127 | mem0 异步 HTTP 客户端（httpx） |
| `lightrag_client.py` | 118 | LightRAG 异步 HTTP 客户端（httpx） |
| `pyproject.toml` | 22 | Python 包定义，入口点 `ragmem-mcp-server` |
| `__init__.py` | - | 包初始化 |

**技术栈**:
- Python 3.10+，FastMCP >= 2.0.0，httpx >= 0.27.0
- stdio 模式运行，通过 `uvx` 安装和启动
- 环境变量配置：`MEM0_URL`, `LIGHTRAG_URL`, `RAGMEM_USER_ID`, `RAGMEM_AGENT_ID`

**9 个 MCP 工具**:

| 工具名 | 后端 | 功能 |
|--------|------|------|
| `memory_add` | mem0 | 存储记忆 |
| `memory_search` | mem0 | 语义搜索记忆 |
| `memory_list` | mem0 | 列出用户记忆 |
| `memory_delete` | mem0 | 删除记忆 |
| `rag_index_text` | LightRAG | 索引文本到知识库 |
| `rag_index_file` | LightRAG | 索引文件到知识库 |
| `rag_query` | LightRAG | 查询知识库（支持 naive/local/global/hybrid 模式） |
| `rag_list_documents` | LightRAG | 列出已索引文档 |
| `ragmem_health` | 两者 | 健康检查 |

#### 2.1.2 Docker Stack (`local-ragmem/stack/`)

| 文件 | 行数 | 职责 |
|------|------|------|
| `docker-compose.yml` | 180 | 3 服务编排 |
| `.env.example` | 72 | 环境变量模板 |
| `deploy.bat` | 448 | 一键部署（6 阶段） |
| `start.sh` | - | Linux 启动脚本 |
| `update-config.bat` | - | 推送配置到 WSL2 |
| `setup-environment.bat` | - | 环境预检 |
| `select-llm-model.ps1` | - | LLM 模型选择 |
| `mem0/main_override.py` | 347 | mem0 自定义 FastAPI 服务器 |
| `mem0/config.yaml` | - | mem0 配置 |
| `mem0/entrypoint.sh` | - | mem0 入口脚本 |

**3 个 Docker 服务**:

| 服务 | 镜像 | 端口 | 说明 |
|------|------|------|------|
| pgvector | `ankane/pgvector:v0.5.1` | 18930 | PostgreSQL + 向量扩展 |
| mem0 | `mem0-server:latest`（自定义） | 18910 | 记忆服务，含 6 个补丁 |
| lightrag | `ghcr.io/hkuds/lightrag:latest` | 18920 | 知识库服务 |

**环境变量传递链**:
```
.env.example → docker-compose.yml → 容器内环境变量
关键变量：LITELLM_BASE_URL, LLM_MODEL, EMBEDDING_HOST, EMBEDDING_MODEL, EMBEDDING_DIM, POSTGRES_*
```

#### 2.1.3 镜像构建 (`local-ragmem/prepare-images.sh`)

**371 行** Shell 脚本，3 个阶段：

1. **Phase 1**: 克隆 mem0 仓库，应用 6 个补丁，构建 `mem0-server:latest`
2. **Phase 2**: 拉取 pgvector 和 LightRAG 预构建镜像
3. **Phase 3**: 导出所有镜像为 `.tar` 文件

**6 个补丁**:

| 补丁 | 目标 | 说明 |
|------|------|------|
| Patch 1/6 | requirements.txt | 添加 `psycopg[binary,pool]` 依赖 |
| Patch 2/6 | main.py | 移除 `graph_store` 相关代码（禁用 neo4j） |
| Patch 3/6 | Dockerfile | 创建 `/app/data` 目录 |
| Patch 4/6 | main.py | DEFAULT_CONFIG 参数化（LLM_MODEL, EMBEDDING_HOST, EMBEDDING_DIM 等） |
| Patch 5/6 | Dockerfile | 移除 `openai.py` 中的 `store` 参数（兼容非 OpenAI 后端） |
| Patch 6/6 | Dockerfile | 移除 `base.py` 中的 `top_p` 参数（兼容 Anthropic） |

---

### 2.2 子系统 B：unity-agent-rules（Unity AI 工作规则）

**路径**: `unity-agent-rules/`
**职责**: 部署到 Unity 项目的 AI 行为规则和专业技能

#### 2.2.1 核心文件

| 文件 | 行数 | 职责 |
|------|------|------|
| `AGENTS.md` | 812 | Unity 项目 AI 根规则（部署到 Unity 项目根目录） |
| `README.md` | - | 使用说明 |
| `tools/deploy-agent-rules.ps1` | 192 | 部署脚本（复制到 Unity 项目） |
| `tools/generate-snapshot.ps1` | - | 项目快照自动生成脚本 |

#### 2.2.2 AGENTS.md 结构（812 行）

| 章节 | 内容 |
|------|------|
| §1 工作原则 | 先理解上下文再行动、目标导向最小变更、可审计可复现可回退 |
| §2 Unity 硬性规则 | 项目结构、.meta 文件、Editor/Runtime 分离、不隐式升级、文件权限边界 |
| §3 目录组织 | Assets/ 标准结构 |
| §4 开发规范 | 脚本、性能、场景、asmdef、包管理、测试 |
| §5 分析规范 | 结论先行、面向实施 |
| §6 文档规则 | 事实与推断标注 |
| §7 .agents 技能与上下文系统 | 8 个技能路由表、上下文文件、MCP 自动检测 |
| §8 元规则 | 5 条 LLM 决策框架 |

**文件权限边界（两阶段模型）**:
- **初始化阶段**: Unity 项目文件只读
- **工作阶段**: 可操作但需用户确认

#### 2.2.3 技能系统（8 个 Skills）

| 技能 | 路径 | 行数 | 职责 |
|------|------|------|------|
| unity-runtime-dev | `.agents/skills/unity-runtime-dev/` | 253 | 运行时代码开发规范 |
| unity-editor-tooling | `.agents/skills/unity-editor-tooling/` | 111 | 编辑器工具开发规范 |
| unity-package-dev | `.agents/skills/unity-package-dev/` | 102 | UPM 包开发规范 |
| unity-patterns | `.agents/skills/unity-patterns/` | - | 设计模式指南 |
| unity-blueprints | `.agents/skills/unity-blueprints/` | - | 蓝图/架构模板 |
| unity-scene-contracts | `.agents/skills/unity-scene-contracts/` | - | 场景契约规范 |
| unity-performance-analysis | `.agents/skills/unity-performance-analysis/` | - | 性能分析指南 |
| unity-documentation | `.agents/skills/unity-documentation/` | - | 文档编写规范 |

**技能分类**:
- **开发规范层**: runtime-dev, editor-tooling, package-dev
- **设计决策层**: patterns, blueprints, scene-contracts
- **分析文档层**: performance-analysis, documentation

#### 2.2.4 上下文系统

| 文件 | 职责 | 生成方式 |
|------|------|----------|
| `project-overview.md` | 技术栈声明（模板，需填充） | `generate-snapshot.ps1` 自动生成 |
| `architecture-snapshot.md` | 代码架构快照（含示例数据） | `generate-snapshot.ps1` 自动生成 |

**设计理念**: 采用**预生成静态快照**而非动态扫描，避免大型 Unity 项目（数万文件）的扫描开销和 token 浪费。

---

### 2.3 子系统 C：unity-mcp-setup（Unity MCP 安装工具）

**路径**: `unity-mcp-setup/`
**职责**: Unity MCP 包的安装、打包、离线部署工具集

#### 2.3.1 工具脚本

| 文件 | 职责 |
|------|------|
| `tools/install-unity-mcp.ps1` | Unity MCP 安装（支持在线/离线/嵌入式） |
| `tools/package-unity-mcp.ps1` | 打包 .tgz（在有网络的机器上执行） |
| `tools/cache-unity-mcp-bridge.ps1` | 缓存 Python 依赖（pypi-cache） |
| `tools/configure-opencode-mcp.ps1` | 配置 OpenCode MCP |
| `tools/unity-mcp-config.json` | 版本映射配置 |

#### 2.3.2 离线包

| 包 | 版本 | 适用 Unity |
|-----|------|-----------|
| `com.coplaydev.unity-mcp-9.5.3.tgz` | v9.5.3 | Unity 2021.3 ~ 2023.x（推荐） |
| `com.coplaydev.unity-mcp-9.6.2.tgz` | v9.6.2 | Unity 6000.0+（Unity 6） |
| `pypi-cache/*.whl` | 多个 | Python 依赖离线缓存（~60 个 wheel） |

**已知问题**: v9.6.x 在 Unity < 6000.0 上编译错误（`BuildReport.SummarizeErrors()` API 不存在）。

#### 2.3.3 文档

| 文件 | 行数 | 内容 |
|------|------|------|
| `docs/unity-mcp-deployment-guide.md` | 665 | 完整部署指南（架构、安装、客户端配置） |
| `docs/agent-enhancement-research.md` | 328 | Agent 增强方法研究（6 种方法评估、差距分析） |

#### 2.3.4 Unity MCP 架构

```
Unity Editor (C# Plugin, port 6400)
    ↕ Socket
Python MCP Server (mcpforunityserver, FastMCP)
    ↕ stdio
AI Client (Claude / Cursor / Roo Code / etc.)
```

- Unity MCP 包安装后自动启动，无需手动操作
- 提供 36+ MCP Tools / 25+ Resources
- 通过 `uvx --from mcpforunityserver mcp-for-unity --transport stdio` 启动

---

### 2.4 自动化脚本系统

#### 2.4.1 脚本清单

| 脚本 | 行数 | 职责 | 运行环境 |
|------|------|------|----------|
| `build-dist.bat` | 185 | 分发打包（生成 timestamped zip） | Windows |
| `clean-ragmem.bat` | 85 | WSL2 环境清理（7 步） | Windows → WSL2 |
| `FULLY_DEPLOY.bat` | 532 | OpenCode CLI 自动安装 + 部署 | Windows |
| `local-ragmem/stack/deploy.bat` | 448 | 一键部署（6 阶段） | Windows → WSL2 |
| `local-ragmem/stack/update-config.bat` | - | 推送配置到 WSL2 | Windows → WSL2 |
| `local-ragmem/prepare-images.sh` | 371 | Docker 镜像构建 | Linux/WSL2 |
| `local-ragmem/prepare-images.bat` | - | Windows 包装器 | Windows → WSL2 |

#### 2.4.2 deploy.bat 6 阶段

| 阶段 | 内容 |
|------|------|
| Phase 1 | 检查/安装 WSL2、Ubuntu-24.04、Docker Engine、Docker Compose |
| Phase 2 | 复制文件到 WSL2（通过 pipe） |
| Phase 2.5 | 确认 LLM 模型配置 |
| Phase 3 | 加载 Docker 镜像（从 .tar） |
| Phase 4 | 启动服务（docker compose） |
| Phase 4.5 | 安装 Python + uv + ragmem MCP Server |
| Phase 5 | 输出访问信息和 MCP 配置 |

#### 2.4.3 clean-ragmem.bat 7 步

1. 停止容器
2. 删除卷
3. 删除镜像
4. 删除 `~/ragmem`
5. 删除旧 `~/agent-memory-stack`
6. 清除 uvx 缓存
7. 验证

#### 2.4.4 FULLY_DEPLOY.bat

532 行的 OpenCode CLI 自动安装器，检测多种安装方式（Scoop/Chocolatey/npm/Git Bash/WSL2/PowerShell 二进制下载），配置 Recreate Provider 后启动 OpenCode 执行 DEPLOY.md。

---

### 2.5 .agents 技能与上下文系统

项目包含**两套独立的 .agents 系统**：

#### 2.5.1 根目录 .agents（Toolkit 自身）

```
.agents/
├── context/
│   └── project-overview.md      ← 项目全貌快照（231 行）
└── skills/
    ├── README.md
    ├── deployment/SKILL.md       ← 部署流程技能
    ├── dist-packaging/SKILL.md   ← 分发打包技能
    ├── docker-image-build/SKILL.md ← Docker 镜像构建技能
    ├── env-cleanup/SKILL.md      ← 环境清理技能
    ├── mcp-server-dev/SKILL.md   ← MCP Server 开发技能（218 行）
    └── script-sync/SKILL.md      ← 自动化脚本同步技能（核心）
```

**6 个技能**，面向 Toolkit 自身的开发和维护。

#### 2.5.2 unity-agent-rules/.agents（Unity 项目）

```
unity-agent-rules/.agents/
├── context/
│   ├── project-overview.md       ← 技术栈声明模板（90 行）
│   └── architecture-snapshot.md  ← 架构快照示例（153 行）
└── skills/
    ├── README.md
    ├── unity-blueprints/SKILL.md
    ├── unity-documentation/SKILL.md
    ├── unity-editor-tooling/SKILL.md (111 行)
    ├── unity-package-dev/SKILL.md (102 行)
    ├── unity-patterns/SKILL.md
    ├── unity-performance-analysis/SKILL.md
    ├── unity-runtime-dev/SKILL.md (253 行)
    └── unity-scene-contracts/SKILL.md
```

**8 个技能**，面向 Unity 项目的开发规范。

---

## 3. 模块依赖关系图

```
                    ┌─────────────────────┐
                    │   build-dist.bat    │ ← 打包所有子系统
                    └─────────┬───────────┘
                              │ 引用
          ┌───────────────────┼───────────────────┐
          │                   │                   │
          ▼                   ▼                   ▼
┌─────────────────┐ ┌─────────────────┐ ┌─────────────────┐
│  local-ragmem/  │ │ unity-agent-    │ │ unity-mcp-      │
│                 │ │ rules/          │ │ setup/           │
│ ┌─────────────┐ │ │                 │ │                 │
│ │ mcp-server/ │ │ │ AGENTS.md       │ │ packages/*.tgz  │
│ │ (Python)    │ │ │ .agents/skills/ │ │ pypi-cache/     │
│ └──────┬──────┘ │ │ tools/          │ │ tools/          │
│        │ HTTP   │ │                 │ │ docs/           │
│ ┌──────▼──────┐ │ └────────┬────────┘ └────────┬────────┘
│ │ stack/      │ │          │                    │
│ │ (Docker)    │ │          │ deploy-agent-      │ install-unity-
│ │ mem0        │ │          │ rules.ps1          │ mcp.ps1
│ │ LightRAG   │ │          ▼                    ▼
│ │ pgvector    │ │   ┌──────────────────────────────┐
│ └─────────────┘ │   │     Unity 项目 (目标)         │
└─────────────────┘   │  AGENTS.md + .agents/ + MCP   │
                      └──────────────────────────────┘
```

**关键依赖**:
- `unity-agent-rules` 和 `unity-mcp-setup` 是**独立模块**，部署到 Unity 项目
- `local-ragmem` 是**独立服务栈**，运行在 WSL2 Docker 中
- 三者通过 MCP 协议在运行时连接（AI 客户端同时连接 Unity MCP 和 ragmem MCP）

---

## 4. 技术栈汇总

| 层级 | 技术 | 用途 |
|------|------|------|
| **Python** | 3.10+, FastMCP, httpx, uvx | MCP Server |
| **Docker** | Compose, WSL2 Ubuntu-24.04 | 服务编排 |
| **Shell** | Bash (prepare-images.sh) | 镜像构建 |
| **Batch** | Windows .bat | 部署自动化 |
| **PowerShell** | .ps1 | Unity 工具脚本 |
| **Markdown** | AGENTS.md, SKILL.md | AI 规则和技能 |
| **JSON** | unity-mcp-config.json, mcp.json | 配置 |
| **YAML** | docker-compose.yml, config.yaml | 服务配置 |
| **C#** | Unity Editor Plugin (第三方) | Unity MCP |

---

## 5. 面向重构的关键洞察

### 5.1 可复用的核心资产

| 资产 | 价值 | 重构建议 |
|------|------|----------|
| **AGENTS.md 规则体系** (812 行) | ⭐⭐⭐⭐⭐ | 直接复用，作为插件的 AI 行为规范 |
| **8 个 Unity Skills** | ⭐⭐⭐⭐⭐ | 直接复用，作为插件的专业能力模块 |
| **上下文系统设计** (project-overview + architecture-snapshot) | ⭐⭐⭐⭐ | 复用设计理念，改为插件内自动生成 |
| **generate-snapshot.ps1** | ⭐⭐⭐⭐ | 改写为 C# Editor 工具 |
| **deploy-agent-rules.ps1** | ⭐⭐⭐ | 改为插件安装时自动执行 |
| **MCP 工具定义模式** | ⭐⭐⭐ | 参考其工具设计，但需重新实现 |
| **Agent 增强研究文档** | ⭐⭐⭐⭐ | 作为插件设计的理论基础 |

### 5.2 可丢弃的部分

| 部分 | 原因 |
|------|------|
| Docker Stack (mem0/LightRAG/pgvector) | 插件不应依赖 Docker/WSL2 |
| 所有 .bat 部署脚本 | 插件通过 UPM 安装，不需要 |
| prepare-images.sh + 6 个补丁 | Docker 镜像构建不再需要 |
| FULLY_DEPLOY.bat (OpenCode 安装器) | 不再需要 |
| pypi-cache/*.whl | Python 依赖不再需要 |
| .env.example / docker-compose.yml | Docker 配置不再需要 |

### 5.3 需要重新设计的部分

| 部分 | 当前实现 | 插件化方向 |
|------|----------|-----------|
| **记忆服务** | Docker 容器 (mem0 + pgvector) | 嵌入式 SQLite/本地存储 或 云端 API |
| **知识库** | Docker 容器 (LightRAG) | 本地向量搜索 或 云端 RAG API |
| **MCP Server** | Python stdio 进程 | C# 原生实现 或 嵌入式 Python |
| **规则加载** | 文件系统读取 AGENTS.md | Editor Window UI + ScriptableObject |
| **技能路由** | 手动查阅路由表 | 自动检测任务类型并加载 |
| **上下文生成** | PowerShell 脚本 | C# Editor 工具，一键生成 |
| **MCP 客户端配置** | 手动编辑 JSON | Editor Window 一键配置 |

### 5.4 项目的核心价值主张

从研究文档 (`agent-enhancement-research.md`) 中提炼的关键洞察：

1. **"问题不是 LLM 不知道规则，而是知道但不看/不遵守"** — 需要执行约束机制
2. **Session Memory 是最大缺口** — 每次新会话都"失忆"
3. **AGENTS.md 已成为事实上的行业标准** — OpenCode、GitHub Copilot、Roo Code、Windsurf 都原生支持
4. **两层架构设计**: Session Memory（会话记忆）+ Mandatory Constraint Loading（强制约束加载）

### 5.5 Unity Agent 插件的潜在架构

基于分析，重构后的 Unity Agent 插件可能包含：

```
com.yourcompany.agent-core/
├── package.json
├── Runtime/
│   ├── Memory/              ← 本地记忆存储（替代 mem0 Docker）
│   ├── RAG/                 ← 本地知识检索（替代 LightRAG Docker）
│   └── Config/              ← 运行时配置
├── Editor/
│   ├── AgentWindow.cs       ← 主 Editor Window
│   ├── Rules/               ← AGENTS.md 规则管理
│   ├── Skills/              ← 技能系统管理
│   ├── Context/             ← 上下文自动生成（替代 generate-snapshot.ps1）
│   ├── MCP/                 ← MCP 配置管理（替代手动 JSON 编辑）
│   └── Setup/               ← 一键安装配置
├── .agents/                 ← 技能文件（直接复用）
│   ├── skills/              ← 8 个 Unity Skills
│   └── context/             ← 上下文模板
├── Documentation~/
└── CHANGELOG.md
```

---

## 6. 文件统计

### 6.1 代码量统计

| 类别 | 文件数 | 总行数（估算） |
|------|--------|---------------|
| Python 源码 | 4 | ~530 |
| Batch 脚本 | 6 | ~1,700 |
| Shell 脚本 | 2 | ~400 |
| PowerShell 脚本 | 6 | ~600 |
| Markdown 文档 | 15+ | ~4,500 |
| YAML/JSON 配置 | 5 | ~350 |
| **总计** | **~38** | **~8,080** |

### 6.2 离线包大小

| 类别 | 文件数 | 说明 |
|------|--------|------|
| Unity MCP .tgz | 2 | v9.5.3 + v9.6.2 |
| Python wheels | ~60 | pypi-cache 离线依赖 |
| Docker .tar | 3（构建时生成） | mem0 + pgvector + lightrag |

---

## 7. 总结

### 7.1 项目本质

这个项目本质上是一个 **"AI Agent 基础设施分发包"**，它解决的核心问题是：

> 如何让团队中的每个 AI 助手（LLM）都能拥有持久记忆、知识库访问、Unity 编辑器操作能力，以及一致的行为规范？

### 7.2 重构为 Unity 插件的核心挑战

1. **去 Docker 化**: 将 mem0 + LightRAG + pgvector 的功能内化为 Unity 插件的本地实现
2. **去 WSL2 化**: 所有功能必须在 Windows 原生环境运行
3. **去 Python 化**: MCP Server 需要用 C# 重新实现，或找到嵌入方案
4. **保留规则体系**: AGENTS.md + Skills 是最有价值的资产，必须完整保留
5. **UI 化**: 将命令行操作转化为 Editor Window 可视化操作
6. **UPM 标准化**: 遵循 Unity Package Manager 规范打包分发

### 7.3 建议的重构优先级

| 优先级 | 模块 | 说明 |
|--------|------|------|
| P0 | 规则 + 技能系统 | 直接复用，是插件的灵魂 |
| P0 | 上下文自动生成 | 将 PS1 改写为 C# Editor 工具 |
| P1 | MCP 配置管理 | Editor Window 一键配置 |
| P1 | 本地记忆存储 | 替代 mem0 Docker，使用 SQLite 或 ScriptableObject |
| P2 | 本地知识检索 | 替代 LightRAG Docker |
| P2 | Session Memory | 解决 LLM "失忆"问题 |
| P3 | 完整 MCP Server | C# 原生 MCP 实现 |
