# 部署流程技能

> 管理 `deploy.bat`、`docker-compose.yml`、`.env.example` 和 `DEPLOY.md` 的部署流程。

---

## 1. 目的

部署流程是本项目的核心交付能力。`deploy.bat` 实现了从零开始的一键部署，`DEPLOY.md` 提供了 LLM 自动部署和人工手动部署两种路径。本技能定义了如何安全地修改部署相关文件。

---

## 2. 适用范围

- 修改 `deploy.bat` 部署流程
- 修改 `docker-compose.yml` 服务编排
- 修改 `.env.example` 环境变量
- 修改 `update-config.bat` 配置推送
- 修改 `DEPLOY.md` 部署文档

---

## 3. 触发场景

| 场景 | 影响的文件 |
|------|-----------|
| 新增 Docker 服务 | `docker-compose.yml`, `deploy.bat`, `DEPLOY.md` |
| 修改环境变量 | `.env.example`, `docker-compose.yml`, `DEPLOY.md` |
| 修改部署目录结构 | `deploy.bat`, `update-config.bat` |
| 修改 MCP Server 安装方式 | `deploy.bat`, `DEPLOY.md` |
| 修改健康检查逻辑 | `docker-compose.yml`, `deploy.bat` |

---

## 4. 核心原则

### 4.1 deploy.bat 6 阶段结构

```
Phase 0: 移除 Zone Identifier 标记（Windows 安全）
Phase 1: 环境检查
  [1.1] WSL2 是否安装
  [1.2] Ubuntu-24.04 是否安装
  [1.3] Docker 是否安装
  [1.4] Docker 是否运行
Phase 2: 复制文件到 WSL2
  - stack/ 目录 → ~/ragmem/
  - mcp-server/ 目录 → ~/ragmem/mcp-server/
Phase 3: 加载 Docker 镜像
  - 从 images/*.tar 加载
  - 如果没有 .tar，尝试 docker pull
Phase 4: 启动服务
  - 复制 .env.example → .env（如果不存在）
  - docker compose up -d
  - 等待健康检查通过
Phase 4.5: 安装 Python + uv + ragmem MCP
  - 检查/安装 Python 3.10+
  - 检查/安装 uv
  - uvx 安装 ragmem-mcp
Phase 5: 输出访问信息
  - 服务 URL
  - MCP 客户端配置（Roo + OpenCode）
```

### 4.2 环境变量传递链

```
.env.example（模板）
    ↓ deploy.bat 复制为 .env
.env（实际配置）
    ↓ docker-compose.yml 读取
docker-compose.yml
    ↓ ${VAR:-default} 传递
容器环境变量
```

**关键规则**：
- `.env.example` 中的每个变量都必须有注释说明
- `docker-compose.yml` 中必须为每个变量提供 `:-default` 默认值
- 默认值应选择最通用的选项（如 `openai` 而非 `ollama`）
- 新增变量时，必须同时更新 `.env.example` 和 `docker-compose.yml`

### 4.3 docker-compose.yml 服务约定

```yaml
services:
  服务名:
    image: 镜像名:标签
    container_name: ragmem-服务名    # 可选，便于识别
    ports:
      - "${HOST_PORT:-默认端口}:容器端口"
    environment:
      - VAR=${VAR:-default}
    volumes:
      - 卷名:/容器路径
    depends_on:
      依赖服务:
        condition: service_healthy
    healthcheck:
      test: ["CMD", ...]
      interval: 10s
      timeout: 5s
      retries: 5

volumes:
  卷名:
    name: ragmem-卷名    # 使用 ragmem- 前缀便于清理
```

### 4.4 DEPLOY.md 双路径结构

```markdown
# 部署指南

## 方式一：LLM 自动部署（推荐）
  → 给 AI 的 prompt，让 AI 读取文档并执行部署

## 方式二：手动部署
  ### 前置条件
  ### 步骤 1: 构建镜像
  ### 步骤 2: 运行部署
  ### 步骤 3: 配置 MCP 客户端
  ### 步骤 4: 验证

## 环境变量说明
## 故障排除
```

修改部署流程时，**两种路径都必须更新**。

---

## 5. WSL2 部署约束

### 5.1 路径转换

```
Windows: D:\Works\Party Animals\LLM AI\local-ragmem\stack\
WSL2:    /mnt/d/Works/Party Animals/LLM AI/local-ragmem/stack/
部署目标: ~/ragmem/
```

### 5.2 网络拓扑

```
Windows 宿主机
  ├── Ollama (localhost:11434)
  ├── LiteLLM (172.16.x.x:8000)
  └── WSL2
      ├── Docker
      │   ├── mem0 → host.docker.internal:11434 (Ollama)
      │   ├── mem0 → host.docker.internal:8000 (LiteLLM)
      │   ├── LightRAG → host.docker.internal:11434
      │   └── pgvector (内部网络)
      └── ragmem-mcp (stdio) → localhost:8080 (mem0)
                               → localhost:9621 (LightRAG)
```

### 5.3 Docker 容器访问宿主机

- 使用 `host.docker.internal` 而非 `localhost`
- WSL2 中的 `localhost` 可以访问 Docker 端口映射
- Windows 中的 `localhost` 通过 WSL2 端口转发访问

---

## 6. 健康检查

当前服务的健康检查：

| 服务 | 检查方式 | 端点 |
|------|---------|------|
| pgvector | `pg_isready` | - |
| mem0 | HTTP GET | `/docs` |
| LightRAG | HTTP GET | `/health` |

新增服务时必须配置健康检查，并在 `deploy.bat` Phase 4 中添加等待逻辑。

---

## 7. 关联技能

- `script-sync` — 部署结构变更后必须检查所有脚本
- `docker-image-build` — 镜像变更影响 Phase 3
- `env-cleanup` — 服务变更影响清理脚本
- `mcp-server-dev` — MCP 安装方式变更影响 Phase 4.5
