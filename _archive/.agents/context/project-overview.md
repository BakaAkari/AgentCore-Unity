# LLM AI Toolkit — 项目全貌

> **last_updated**: 2026-04-07
>
> AI Agent 在开始任何任务前**必须先读取本文件**。
> 如果本文件的 `last_updated` 超过 30 天，或文件清单与实际不符，请先更新本文件。

---

## 1. 项目定位

**LLM AI Toolkit** 是一个面向团队的 AI 基础设施分发包，目标是让任何 LLM（或人类）都能通过阅读文档 + 运行脚本，在 Windows + WSL2 环境中一键部署完整的 AI 记忆与知识库服务。

核心交付物：
- **RagMem 服务栈**：mem0（记忆）+ LightRAG（知识库）+ pgvector（向量数据库）
- **MCP Server**：ragmem-mcp，让 AI 客户端通过 MCP 协议访问记忆和知识库
- **Unity Agent Rules**：部署到 Unity 项目的 AI 工作规则和技能（`unity-agent-rules/`）
- **Unity MCP Setup**：Unity MCP 安装工具、离线包、文档（`unity-mcp-setup/`）

---

## 2. 技术栈

| 层级 | 技术 | 版本/说明 |
|------|------|-----------|
| 容器编排 | Docker Compose | 在 WSL2 Ubuntu-24.04 中运行 |
| 记忆服务 | mem0 | 自定义镜像 `mem0-server:latest`，含 6 个补丁 |
| 知识库 | LightRAG | `ghcr.io/hkuds/lightrag:latest` |
| 向量数据库 | pgvector | `ankane/pgvector:v0.5.1` |
| MCP Server | ragmem-mcp | Python 3.10+，FastMCP，通过 uvx 安装 |
| LLM 网关 | LiteLLM | 外部服务，通过 `LITELLM_BASE_URL` 配置 |
| Embedding | 云端 OpenAI 兼容 API | 通过 `EMBEDDING_HOST` 配置，固定使用 openai provider |
| 部署平台 | Windows + WSL2 | `.bat` 脚本调用 `wsl -d Ubuntu-24.04` |

---

## 3. 文件清单（可分发文件）

> 以下文件包含在 `build-dist.bat` 生成的分发包中。

### 3.1 根目录

| 文件 | 用途 |
|------|------|
| `DEPLOY.md` | 用户部署指南（LLM 自动部署 + 手动部署） |
| `MCP-MANUAL-CONNECT.md` | 手动连接 MCP 服务到任意 AI 终端的指南 |
| `build-dist.bat` | 分发打包脚本 |
| `clean-ragmem.bat` | WSL2 环境清理脚本 |

### 3.2 local-ragmem/

| 文件 | 用途 |
|------|------|
| `prepare-images.sh` | Docker 镜像构建（6 个补丁） |
| `prepare-images.bat` | Windows 包装器，调用 WSL 执行 .sh |
| `.gitattributes` | 确保 .sh 文件使用 LF 换行 |
| `.gitignore` | 忽略 images/*.tar |

### 3.3 local-ragmem/mcp-server/

| 文件 | 用途 |
|------|------|
| `pyproject.toml` | Python 包定义（ragmem-mcp） |
| `README.md` | MCP Server 说明 |
| `src/ragmem_mcp/__init__.py` | 包初始化 |
| `src/ragmem_mcp/server.py` | MCP 工具定义（12 个工具） |
| `src/ragmem_mcp/mem0_client.py` | mem0 HTTP 客户端 |
| `src/ragmem_mcp/lightrag_client.py` | LightRAG HTTP 客户端 |

### 3.4 local-ragmem/stack/

| 文件 | 用途 |
|------|------|
| `deploy.bat` | 一键部署（6 阶段，424 行） |
| `docker-compose.yml` | 3 服务编排 |
| `.env.example` | 环境变量模板 |
| `start.sh` | Linux 启动脚本 |
| `setup-environment.bat` | 环境预检 |
| `update-config.bat` | 推送配置到 WSL2 |
| `README.md` | Stack 说明 |
| `mem0/config.yaml` | mem0 配置 |
| `mem0/main_override.py` | mem0 主程序覆盖（备用） |
| `mem0/entrypoint.sh` | mem0 入口脚本（备用） |

### 3.5 unity-agent-rules/

| 文件 | 用途 |
|------|------|
| `AGENTS.md` | Unity 项目 AI 规则（部署到 Unity 项目） |
| `README.md` | 使用说明 |
| `.agents/` | AI 技能和上下文模板 |
| `.vscode/mcp.json` | VS Code MCP 配置模板 |
| `tools/deploy-agent-rules.ps1` | Agent Rules 部署脚本 |
| `tools/generate-snapshot.ps1` | 项目快照生成脚本 |

### 3.6 unity-mcp-setup/

| 文件 | 用途 |
|------|------|
| `README.md` | MCP 安装说明 |
| `packages/*.tgz` | Unity MCP 包（离线安装用） |
| `packages/pypi-cache/*.whl` | Python 依赖缓存（离线安装用） |
| `tools/install-unity-mcp.ps1` | Unity MCP 安装脚本 |
| `tools/package-unity-mcp.ps1` | Unity MCP 打包脚本 |
| `tools/cache-unity-mcp-bridge.ps1` | Python 依赖缓存脚本 |
| `tools/configure-opencode-mcp.ps1` | OpenCode MCP 配置脚本 |
| `tools/unity-mcp-config.json` | MCP 配置模板 |
| `docs/*.md` | 部署指南和研究文档 |

### 3.7 开发专用文件（不包含在分发包中）

| 文件/目录 | 用途 |
|-----------|------|
| `AGENTS.md` | AI Agent 根规则 |
| `.agents/` | AI 技能和上下文 |
| `DEPLOYMENT-REVIEW.md` | 部署审查 |
| `DEPLOYMENT-REPORT.md` | 部署实录 |
| `.vscode/` | VS Code 配置 |
| `.ruff_cache/` | Ruff 缓存 |

---

## 4. Docker 服务架构

```
┌─ Windows ──────────────────────────────────────────┐
│  AI Client (Roo Code / Cursor / Claude / etc.)     │
│    ├── Unity MCP (stdio via uvx) → Unity Editor    │
│    └── ragmem MCP (stdio via wsl) ─┐               │
│                                     │               │
├─ WSL2 Ubuntu-24.04 ───────────────│───────────────┤
│     ragmem-mcp (Python/uvx)        │               │
│       ├── memory_* → HTTP → mem0 (:18910)          │
│       └── rag_*    → HTTP → LightRAG (:18920)      │
│                                                     │
│     Docker: mem0 + LightRAG + pgvector (:18930)     │
└─────────────────────────────────────────────────────┘
```

> 两个 MCP 服务都使用 stdio 模式，AI 终端自动启动和管理进程。
> Unity MCP 通过 socket 端口 6400 与 Unity 编辑器通信（自动，无需配置）。

---

## 5. 环境变量传递链

```
.env.example → docker-compose.yml → 容器内环境变量

关键变量：
  LITELLM_BASE_URL     → mem0 (LLM_BASE_URL), LightRAG (LLM_BINDING_HOST)
  LLM_MODEL            → mem0, LightRAG (LLM_MODEL)
  EMBEDDING_HOST       → mem0 (openai_base_url), LightRAG (EMBEDDING_BINDING_HOST)
  EMBEDDING_API_KEY    → mem0 (api_key), LightRAG (EMBEDDING_BINDING_API_KEY)
  EMBEDDING_MODEL      → mem0 (EMBEDDING_MODEL), LightRAG (EMBEDDING_MODEL)
  EMBEDDING_DIM        → mem0 (EMBEDDING_DIM), LightRAG (EMBEDDING_DIM)
  POSTGRES_*           → pgvector, mem0, LightRAG
```

---

## 6. prepare-images.sh 补丁清单

| 补丁 | 目标 | 说明 |
|------|------|------|
| Patch 1/6 | mem0 Dockerfile | 添加 psycopg[binary] 依赖 |
| Patch 2/6 | mem0 Dockerfile | 移除 graph_store 相关代码 |
| Patch 3/6 | mem0 Dockerfile | 创建 /app/data 目录 |
| Patch 4/6 | mem0 main.py | DEFAULT_CONFIG 参数化（云端 embedding，openai provider） |
| Patch 5/6 | mem0 Dockerfile | 移除 openai.py 中的 store 参数 |
| Patch 6/6 | mem0 Dockerfile | 移除 base.py 中的 top_p 参数 |

---

## 7. 自动化脚本依赖关系

```
build-dist.bat ──→ 依赖所有可分发文件的路径
clean-ragmem.bat ──→ 依赖 Docker 容器名、镜像名、卷名
deploy.bat ──→ 依赖 WSL2 配置、Docker 镜像、MCP 包路径
update-config.bat ──→ 依赖配置文件路径
prepare-images.sh ──→ 依赖补丁列表、基础镜像
```

>  任何文件增删改名都可能需要同步更新这些脚本。
> 详见 `AGENTS.md` §3 和 `.agents/skills/script-sync/SKILL.md`。

---

## 8. 已知限制

- mem0 镜像基于官方 `mem0ai/mem0:latest`，补丁通过 Dockerfile RUN 层注入
- LightRAG 使用官方镜像，不做修改
- pgvector HNSW 索引不支持超过 2000 维的向量（Patch 4 已处理）
- Embedding 固定使用 openai provider（通过 `EMBEDDING_HOST` 指向任意 OpenAI 兼容 API）
- MCP Server 通过 stdio 模式运行，需要 WSL2 中安装 Python + uv

---

## 9. 本文件的维护规则

> 本文件是 AI Agent 的"项目记忆"。如果它过时了，下一个 AI 会基于错误信息做决策。

### 何时更新本文件

| 触发条件 | 需要更新的章节 |
|----------|---------------|
| 新增/删除/重命名源文件 | §3 文件清单 |
| 新增/修改 Docker 补丁 | §6 补丁清单 |
| 新增/修改环境变量 | §5 环境变量传递链 |
| 新增/修改 Docker 服务 | §4 架构图 |
| 新增 MCP 工具 | §3.3 工具数量 |
| 发现新的已知限制 | §8 已知限制 |
| 任何变更 | 顶部 `last_updated` 日期 |

### 如何验证本文件是否过期

```
1. 检查 last_updated 是否超过 30 天
2. 对比 §3 文件清单与实际 `ls -R` 输出
3. 对比 §6 补丁清单与 prepare-images.sh 中的实际补丁
4. 对比 §5 环境变量与 .env.example 中的实际变量
5. 如果任何一项不一致 → 先更新本文件，再开始任务
```

### 更新原则

- 只更新变化的章节，不重写整个文件
- 更新后必须刷新 `last_updated` 日期
- Agent 自有文件，无需用户确认即可更新
