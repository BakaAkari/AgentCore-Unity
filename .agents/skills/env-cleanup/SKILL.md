# 环境清理技能

> 管理 `clean-ragmem.bat` 的清理逻辑，确保能完整清除 ragmem 环境而不影响其他服务。

---

## 1. 目的

`clean-ragmem.bat` 用于在 WSL2 中完整清除 ragmem 部署环境，以便从零开始重新部署。它必须精确清理 ragmem 相关资源，同时保留 searxng、openclaw 等其他服务。

---

## 2. 适用范围

- 新增或删除 Docker 服务
- 修改 Docker 镜像名称或标签
- 修改 Docker Compose 卷名
- 修改部署目录路径
- 新增需要清理的缓存或数据

---

## 3. 触发场景

| 场景 | 操作 |
|------|------|
| docker-compose.yml 新增服务 | 添加镜像和卷的清理命令 |
| docker-compose.yml 删除服务 | 移除对应的清理命令 |
| 修改镜像名/标签 | 更新 `docker rmi` 命令 |
| 修改卷名 | 更新 `docker volume rm` 命令 |
| 新增部署目录 | 添加 `rm -rf` 命令 |
| 新增缓存路径 | 添加清理命令 |

---

## 4. 核心原则

### 4.1 clean-ragmem.bat 当前结构

```
Step 1: docker compose down（停止容器）
Step 2: docker volume rm（删除数据卷）
  - ragmem-mem0-data
  - ragmem-pgvector-data
  - 匿名卷（64 位 hex ID）
Step 3: docker rmi（删除镜像）
  - mem0-server:latest
  - ghcr.io/hkuds/lightrag:latest
  - ankane/pgvector:v0.5.1
  - docker image prune -f
Step 4: rm -rf ~/ragmem（部署目录）
Step 5: rm -rf ~/agent-memory-stack（旧遗留）
Step 6: 清理 uvx ragmem MCP 缓存
Step 7: 验证环境
```

### 4.2 安全清理原则

- **只清理 ragmem 相关资源**，不使用 `docker system prune`
- 每个 `docker` 命令都用 `2>/dev/null` 容错
- 验证步骤明确列出预期状态（"should only show searxng"）
- 使用 `wsl -d Ubuntu-24.04` 确保在正确的 WSL 发行版中执行

### 4.3 新增 Docker 服务时的操作

```batch
REM Step 2: 添加新卷
wsl -d Ubuntu-24.04 -- bash -c "docker volume rm ragmem-mem0-data ragmem-pgvector-data 新卷名 2>/dev/null; ..."

REM Step 3: 添加新镜像
wsl -d Ubuntu-24.04 -- bash -c "docker rmi mem0-server:latest ... 新镜像名:标签 2>/dev/null; ..."
```

### 4.4 步骤计数

- 当前为 `[1/7]` 到 `[7/7]`
- 新增清理步骤时更新总数
- 验证步骤始终是最后一步

---

## 5. Docker 资源命名约定

| 资源类型 | 命名来源 | 当前值 |
|---------|---------|--------|
| 容器名 | docker-compose.yml `container_name` | 由 compose 项目名自动生成 |
| 卷名 | docker-compose.yml `volumes` | `ragmem-mem0-data`, `ragmem-pgvector-data` |
| 镜像名 | docker-compose.yml `image` | `mem0-server:latest`, `ghcr.io/hkuds/lightrag:latest`, `ankane/pgvector:v0.5.1` |
| Compose 项目名 | 目录名 | `ragmem`（来自 `~/ragmem`） |

> 修改 `docker-compose.yml` 中的卷名或镜像名时，必须同步更新 `clean-ragmem.bat`。

---

## 6. 验证步骤维护

Step 7 的验证输出应反映当前的预期状态：

```batch
REM 容器：应该只显示非 ragmem 容器
echo   --- Docker containers (should only show searxng) ---

REM 镜像：不应包含 ragmem 相关镜像
echo   --- Docker images (should NOT contain mem0/lightrag/pgvector) ---

REM 卷：应该为空或只有非 ragmem 卷
echo   --- Docker volumes (should be empty or only non-ragmem) ---

REM 部署目录：不应存在
echo   --- ~/ragmem should NOT exist ---
```

如果新增了 Docker 服务，验证描述中的镜像名列表也需要更新。

---

## 7. 关联技能

- `script-sync` — 本技能是 script-sync 的子集，专注于 clean-ragmem.bat
- `docker-image-build` — 镜像名/标签变更时触发
- `deployment` — 部署目录变更时触发
