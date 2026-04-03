# RagMem

mem0 (跨会话记忆) + LightRAG (知识库/RAG) + pgvector (向量数据库)

离线部署包，适用于 Windows 10/11 沙盘环境。所有 Docker 镜像预构建为 tar 文件，无需网络即可部署。

## 架构

```
┌─────────────────────────────────────────────────────┐
│  WSL2 Ubuntu-24.04 (Docker Engine)                  │
│                                                     │
│  ┌──────────────┐  ┌──────────────┐                 │
│  │  mem0-server  │  │   LightRAG   │                │
│  │  :18910→8000  │  │  :18920→9621 │                │
│  │  (FastAPI)    │  │  (FastAPI)   │                │
│  └──────┬───────┘  └──────────────┘                 │
│         │                                           │
│  ┌──────▼───────┐                                   │
│  │   pgvector    │  LiteLLM (沙盘内 LLM 网关)       │
│  │  :18930       │  ← OpenAI-compatible API →       │
│  │  (PostgreSQL) │                                  │
│  └──────────────┘                                   │
└─────────────────────────────────────────────────────┘
```

## 前置条件

部署脚本会自动检测并安装缺失的组件，但首次安装需要：

| 组件 | 说明 | 首次安装需要 |
|------|------|-------------|
| WSL2 | Windows Subsystem for Linux 2 | 管理员权限 + 可能需要重启 |
| Ubuntu-24.04 | WSL2 Linux 发行版 | 管理员权限 + 网络（~600MB） |
| Docker Engine | 容器运行时 | 网络（~300MB） |
| Docker Compose | 多容器编排 | 网络（随 Docker 安装） |

> **沙盘内无网络？** 请让管理员在沙盘外先运行 `setup-environment.bat` 准备好环境，
> 或确保沙盘镜像已预装 WSL2 + Ubuntu-24.04 + Docker。

### 环境准备（仅首次）

**方式 A：自动安装（推荐，需要网络）**

```cmd
REM 右键 → 以管理员身份运行
setup-environment.bat
```

**方式 B：deploy.bat 自动检测**

`deploy.bat` 内置了环境检测，首次运行时会自动尝试安装缺失组件。
以管理员身份运行即可。

## 快速开始

### 1. 配置环境变量

```bash
cp .env.example .env
nano .env  # 填入 LiteLLM 端点和 API Key
```

必填项：
- `LITELLM_BASE_URL` — 沙盘内 LiteLLM 网关地址
- `LITELLM_API_KEY` — API 密钥

### 2. 部署

**方式 A：在沙盘 cmd 中运行（推荐）**

```cmd
REM 首次运行以管理员身份执行（安装环境）
REM 后续运行普通用户即可
deploy.bat
```

脚本自动完成：环境检测 → 文件复制 → 镜像加载 → 服务启动

**方式 B：在 WSL2 中直接运行**

```bash
cd ~/ragmem
chmod +x start.sh
./start.sh
```

### 3. 验证

```bash
# 在 WSL2 中
docker compose ps                     # 三个服务都应显示 (healthy)
curl http://localhost:18910/docs      # mem0（Swagger UI）
curl http://localhost:18920/health    # LightRAG
docker exec ragmem-pgvector pg_isready -U mem0  # pgvector
```

从 Windows cmd / PowerShell 直接访问（mem0 和 LightRAG 绑定 0.0.0.0）：

```cmd
curl http://localhost:18910/docs
curl http://localhost:18920/health
```

## 服务端口

| 服务 | 宿主端口 | 容器端口 | 绑定地址 | 用途 |
|------|----------|----------|----------|------|
| mem0 API | 18910 | 8000 | `0.0.0.0` | 跨会话记忆存储与检索 |
| LightRAG | 18920 | 9621 | `0.0.0.0` | 文档索引与 RAG 查询 |
| pgvector | 18930 | 5432 | `127.0.0.1` | PostgreSQL + 向量扩展（仅内部） |

> **mem0 和 LightRAG** 绑定 `0.0.0.0`，可从 Windows 宿主机通过 `http://localhost:端口` 直接访问。
> **pgvector** 仅绑定 `127.0.0.1`，只能从 Docker 内部网络访问（mem0 通过容器名 `pgvector` 连接）。

### ⚠️ 沙箱环境网络限制

在沙箱（Sandbox）环境中，**Windows 无法通过 localhost 访问 WSL2 容器端口**。
这意味着：

- ❌ Windows 侧的 `curl http://localhost:18910` 会失败
- ✅ WSL2 内部的 `curl http://localhost:18910` 正常工作

**影响**：ragmem MCP Server 必须运行在 WSL2 内部（而非 Windows 侧），
才能通过 localhost 访问 mem0 和 LightRAG。`deploy.bat` 会自动在 WSL2 中安装
Python + uv 并部署 MCP Server 源码。

详见 `../mcp-server/README.md` 中的 MCP 客户端配置说明。

> **普通 Windows 开发机**（非沙箱）通常不受此限制，WSL2 端口转发默认可用。

## Docker 镜像

| 镜像 | 来源 | 说明 |
|------|------|------|
| `mem0-server:latest` | 本地构建 | 从 [mem0 仓库](https://github.com/mem0ai/mem0) `server/` 目录构建 |
| `ankane/pgvector:v0.5.1` | Docker Hub | PostgreSQL 16 + pgvector 向量扩展 |
| `ghcr.io/hkuds/lightrag:latest` | GitHub Container Registry | LightRAG 官方镜像 |

> mem0 没有官方预构建的 linux/amd64 镜像，必须从源码构建。
> 构建脚本：`ragmem/prepare-images.sh`

## 常用操作

```bash
# 查看服务状态
docker compose ps

# 查看日志
docker compose logs -f
docker compose logs -f mem0      # 只看 mem0
docker compose logs -f lightrag  # 只看 LightRAG
docker compose logs -f pgvector  # 只看 pgvector

# 重启服务
docker compose restart

# 停止服务
docker compose down

# 停止并清除数据（包括 pgvector 数据卷）
docker compose down -v
```

## 数据备份与恢复

### 备份

```bash
# 备份 pgvector（mem0 记忆数据）
wsl -d Ubuntu-24.04 --cd ~ -- docker exec ragmem-pgvector pg_dump -U mem0 mem0 > mem0-backup.sql

# 备份 LightRAG 索引数据
wsl -d Ubuntu-24.04 --cd ~ -- tar czf lightrag-backup.tar.gz -C ~/ragmem lightrag/data/

# 备份 mem0 SQLite 历史记录
wsl -d Ubuntu-24.04 --cd ~ -- docker cp ragmem-mem0:/app/data/history.db ./mem0-history-backup.db
```

### 恢复

```bash
# 恢复 pgvector
type mem0-backup.sql | wsl -d Ubuntu-24.04 --cd ~ -- docker exec -i ragmem-pgvector psql -U mem0 mem0

# 恢复 LightRAG
wsl -d Ubuntu-24.04 --cd ~ -- tar xzf lightrag-backup.tar.gz -C ~/ragmem/

# 恢复 mem0 历史记录
wsl -d Ubuntu-24.04 --cd ~ -- docker cp ./mem0-history-backup.db ragmem-mem0:/app/data/history.db
wsl -d Ubuntu-24.04 --cd ~/ragmem -- docker compose restart mem0
```

> **提示**：`docker compose down -v` 会删除 pgvector 的 Docker named volume，执行前请先备份。
> LightRAG 使用 bind mount（`lightrag/data/`），不受 `-v` 影响。

## mem0 API 示例

```bash
# 添加记忆
curl -X POST http://localhost:18910/v1/memories/ \
  -H "Content-Type: application/json" \
  -d '{
    "messages": [{"role": "user", "content": "I prefer UniTask over coroutines"}],
    "user_id": "dev-001"
  }'

# 搜索记忆
curl -X POST http://localhost:18910/v1/memories/search/ \
  -H "Content-Type: application/json" \
  -d '{
    "query": "async programming preference",
    "user_id": "dev-001"
  }'

# 获取所有记忆
curl http://localhost:18910/v1/memories/?user_id=dev-001
```

## LightRAG API 示例

```bash
# 索引文档
curl -X POST http://localhost:18920/documents/text \
  -H "Content-Type: application/json" \
  -d '{
    "text": "The Unity project uses URP with UniTask for async operations."
  }'

# 查询
curl -X POST http://localhost:18920/query \
  -H "Content-Type: application/json" \
  -d '{
    "query": "What rendering pipeline does the project use?",
    "mode": "hybrid"
  }'
```

## 数据持久化

| 存储 | 类型 | 内容 |
|------|------|------|
| `ragmem-pgvector-data` (Docker volume) | Named Volume | mem0 向量数据 + 元数据 |
| `lightrag/data/` | Bind Mount | LightRAG 索引数据 |
| `lightrag/documents/` | Bind Mount | 待索引文档 |

## mem0 技术说明

mem0 服务器基于 [mem0 仓库](https://github.com/mem0ai/mem0) `server/` 目录构建，
构建时通过 `prepare-images.sh` 自动应用以下 patch：

1. **`psycopg[binary,pool]`** — python:3.12-slim 缺少 libpq，需要预编译二进制
2. **移除 `graph_store`** — 原始代码硬编码 Neo4j 作为图存储，会拉入大量依赖（langchain-neo4j, rank-bm25 等）。
   由于默认不部署 Neo4j，构建时移除此配置，镜像从 ~490MB 降至 297MB
3. **`/app/data` 目录** — SQLite history.db 持久化路径

构建后的 mem0 使用：

- **向量存储**: pgvector（PostgreSQL + vector 扩展）— 不可更换
- **LLM**: OpenAI-compatible API（通过 LiteLLM 路由）
- **Embedder**: OpenAI-compatible API（通过 LiteLLM 路由）
- **历史记录**: SQLite（`/app/data/history.db`，通过 Docker volume 持久化）

> **注意**: mem0 没有 `/health` 端点。healthcheck 使用 `/docs`（Swagger UI）。

配置完全通过环境变量完成，不使用 config.yaml 文件。

## 故障排查

### 服务启动失败

```bash
# 查看详细日志
docker compose logs --tail=50 <service-name>

# 检查容器状态
docker compose ps -a
```

### mem0 连接 pgvector 失败

```bash
# 检查 pgvector 是否就绪
docker exec ragmem-pgvector pg_isready -U mem0

# 检查 pgvector 日志
docker compose logs pgvector

# 手动测试连接
docker exec -it ragmem-pgvector psql -U mem0 -d mem0 -c "SELECT 1;"
```

### LiteLLM 连接失败

```bash
# 测试 LiteLLM 端点
curl -s http://your-litellm-endpoint:port/health

# 检查 .env 配置
cat .env | grep LITELLM
```

### 磁盘空间不足

```bash
# 检查 Docker 磁盘使用
docker system df

# 清理未使用的资源
docker system prune -f
```

### Neo4j 启用（可选）

如需启用图记忆功能：

1. 编辑 `docker-compose.yml`，取消 `neo4j` 服务和 `neo4j-data` 卷的注释
2. 在 `.env` 中设置 `NEO4J_PASSWORD`
3. 在 mem0 服务的 environment 中添加：
   ```yaml
   NEO4J_URI: bolt://neo4j:7687
   NEO4J_USERNAME: neo4j
   NEO4J_PASSWORD: ${NEO4J_PASSWORD:-neo4jpass}
   ```
4. 重新部署：`docker compose up -d`
