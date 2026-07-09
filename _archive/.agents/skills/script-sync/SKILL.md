# 自动化脚本同步技能

> **核心技能** — 确保自动化脚本永远适配最新的项目结构。
> 这是本项目区别于普通项目的核心特征。

---

## 1. 目的

本项目的自动化脚本（`build-dist.bat`、`clean-ragmem.bat`、`deploy.bat` 等）是其他 LLM 自主部署的关键依赖。如果脚本与项目结构不一致，部署将失败。

本技能定义了**何时**、**如何**检查和更新这些脚本。

---

## 2. 适用范围

- 任何涉及文件增删改名的变更
- 任何涉及 Docker 服务/镜像/卷的变更
- 任何涉及环境变量的变更
- 任何涉及部署目录结构的变更

---

## 3. 触发场景

> AI 在完成任何代码变更后，**必须**对照此表检查是否需要同步脚本。

### 3.1 变更 → 脚本影响矩阵

| 变更类型 | build-dist.bat | clean-ragmem.bat | deploy.bat | update-config.bat | prepare-images.sh |
|----------|:-:|:-:|:-:|:-:|:-:|
| 新增/删除/重命名源文件 |  | - | - | - | - |
| 新增/删除 Docker 服务 | - |  |  | - | - |
| 修改 Docker 镜像名/标签 | - |  |  | - | - |
| 修改 docker-compose.yml 卷名 | - |  | - | - | - |
| 新增环境变量 | - | - | - | - | - |
| 修改 MCP Server 包结构 |  | - |  | - | - |
| 新增 prepare-images.sh 补丁 | - | - | - | - |  |
| 修改部署目录结构 | - | - |  |  | - |
| 新增/修改配置文件 | - | - | - |  | - |

> 标记  表示该脚本**可能**需要更新，AI 必须检查确认。

### 3.2 环境变量变更的特殊处理

环境变量变更不直接影响脚本，但需要确保**传递链完整**：

```
.env.example（定义） → docker-compose.yml（传递） → 容器内（使用）
```

检查清单：
1. `.env.example` 中是否有新变量的定义和注释？
2. `docker-compose.yml` 中是否通过 `${VAR:-default}` 传递？
3. 如果变量影响多个服务，所有服务是否都收到了？
4. `DEPLOY.md` 中是否说明了新变量的用途？

---

## 4. 核心原则

### 4.1 先检查，再修改

```
1. 完成主要代码变更
2. 对照 §3.1 矩阵，列出所有可能受影响的脚本
3. 逐个读取脚本，确认是否需要更新
4. 向用户展示变更计划（列出每个脚本的具体修改）
5. 获得确认后批量修改
```

### 4.2 保持脚本风格一致

每个脚本有自己的风格约定：

| 脚本 | 步骤格式 | 进度输出 | 错误处理 |
|------|---------|---------|---------|
| `build-dist.bat` | `[N/M]` | `echo OK` | 静默跳过 |
| `clean-ragmem.bat` | `[N/M]` | `OK - 描述` / `SKIP - 原因` | 容错继续 |
| `deploy.bat` | `[Phase N]` + `[N.M]` | `[OK]` / `[ERROR]` | 分阶段检查 |
| `update-config.bat` | `[N/M]` | `OK` | 简单输出 |
| `prepare-images.sh` | `Patch N/M` | `echo` 进度 | `set -euo pipefail` |

### 4.3 不改变脚本的总体结构

- 新增文件 → 在对应的 copy 区块中追加行
- 删除文件 → 移除对应的 copy 行
- 新增 Docker 服务 → 在清理/部署脚本中追加对应的处理
- 更新步骤计数 → 修改 `[N/M]` 中的 M

### 4.4 同步更新项目上下文

完成脚本同步后，检查 `.agents/context/project-overview.md` 是否需要更新：
- 文件清单是否仍然准确？
- 补丁清单是否需要更新？
- 环境变量传递链是否有变化？

---

## 5. 各脚本详细结构

### 5.1 build-dist.bat 结构

```
Phase 1: DEPLOY.md（根文件）
Phase 2: local-ragmem/（核心目录）
  ├── mcp-server/（源码）
  ├── stack/（部署栈）
  ├── prepare-images.sh/bat（镜像构建）
  └── images/*.tar（Docker 镜像）
Phase 3: unity-agent-rules/（Agent Rules）
  ├── 根文件（AGENTS.md, README.md, .gitignore）
  ├── .agents/（xcopy 递归）
  ├── .vscode/mcp.json
  └── tools/（deploy-agent-rules.ps1, generate-snapshot.ps1）
Phase 4: unity-mcp-setup/（MCP 安装工具）
  ├── README.md
  ├── packages/*.tgz + pypi-cache/*.whl
  ├── tools/*.ps1 + .json
  └── docs/*.md
Phase 5: 验证
Phase 6: 汇总
```

**新增文件时的操作**：
1. 确定文件属于哪个 Phase
2. 在对应区块中添加 `copy /Y` 或 `mkdir` 命令
3. 如果需要新建子目录，先 `mkdir`
4. 更新步骤计数（如果新增了 Phase）

**关键约束**：
- `.agents/` 目录**不包含**在分发包中
- `DEPLOYMENT-REVIEW.md` 和 `DEPLOYMENT-REPORT.md` **不包含**在分发包中

### 5.2 clean-ragmem.bat 结构

```
Step 1: 停止 ragmem 容器（docker compose down）
Step 2: 删除数据卷（ragmem-mem0-data, ragmem-pgvector-data + 匿名卷）
Step 3: 删除 Docker 镜像（mem0-server, lightrag, pgvector）
Step 4: 删除 ~/ragmem 部署目录
Step 5: 删除 ~/agent-memory-stack（旧遗留）
Step 6: 清理 uvx ragmem MCP 缓存
Step 7: 验证环境
```

**新增 Docker 服务时的操作**：
1. Step 2: 添加新卷名到 `docker volume rm` 命令
2. Step 3: 添加新镜像到 `docker rmi` 命令
3. Step 7: 更新验证输出的预期描述

**关键约束**：
- 必须保留 searxng、openclaw 等非 ragmem 服务
- 使用 `2>/dev/null` 容错，不因缺失资源而失败

### 5.3 deploy.bat 结构

```
Phase 0: 移除 Zone Identifier 标记
Phase 1: 环境检查（WSL2, Ubuntu, Docker）
Phase 2: 复制文件到 WSL2（stack + mcp-server）
Phase 3: 加载 Docker 镜像
Phase 4: 启动服务（docker compose up）
Phase 4.5: 安装 Python + uv + ragmem MCP
Phase 5: 输出访问信息 + MCP 客户端配置
```

**修改部署结构时的操作**：
1. Phase 2: 更新文件复制命令
2. Phase 3: 更新镜像加载命令
3. Phase 5: 更新输出的配置信息

### 5.4 prepare-images.sh 补丁系统

```
补丁格式：
  echo "--- Patch N/M: 描述 ---"
  # 具体的 sed/python3 -c/heredoc 操作

规则：
  - 新增补丁追加到末尾
  - 更新所有补丁的总数 M（如 Patch 1/7 → Patch 1/8）
  - 更新文件末尾的 Summary 注释
  - 不修改已有补丁的编号或内容
```

---

## 6. 检查清单模板

完成代码变更后，AI 应在心中运行以下检查：

```markdown
## 脚本同步检查

### 本次变更涉及的文件
- [ ] 列出所有新增/删除/重命名/修改的文件

### build-dist.bat
- [ ] 是否有新的可分发文件需要添加？
- [ ] 是否有文件被删除需要移除？
- [ ] 步骤计数是否需要更新？

### clean-ragmem.bat
- [ ] 是否有新的 Docker 服务/镜像/卷？
- [ ] 是否有服务被移除？
- [ ] 步骤计数是否需要更新？

### deploy.bat
- [ ] 部署目录结构是否有变化？
- [ ] Docker 镜像列表是否有变化？
- [ ] MCP Server 安装方式是否有变化？

### update-config.bat
- [ ] 配置文件路径是否有变化？

### prepare-images.sh
- [ ] 是否需要新增补丁？
- [ ] 补丁总数是否需要更新？

### .agents/context/project-overview.md
- [ ] 文件清单是否需要更新？
- [ ] 补丁清单是否需要更新？
- [ ] 环境变量传递链是否有变化？

### DEPLOY.md
- [ ] 部署指南是否需要更新？

### Agent 自维护（参见 AGENTS.md §3.4）
- [ ] project-overview.md §3 文件清单是否需要更新？
- [ ] project-overview.md §5 环境变量传递链是否有变化？
- [ ] project-overview.md §6 补丁清单是否需要更新？
- [ ] 相关 SKILL.md 中的数据表格是否需要更新？
- [ ] skills/README.md 技能索引是否需要更新？
- [ ] AGENTS.md §7 技能路由表是否需要更新？
- [ ] project-overview.md 的 last_updated 日期是否需要刷新？
```

---

## 7. 示例场景

### 场景 A：给 MCP Server 新增一个 Python 模块

```
变更：新增 local-ragmem/mcp-server/src/ragmem_mcp/utils.py

需要同步的脚本：
1. build-dist.bat — 在 mcp-server 区块添加 copy 命令

需要自维护的 Agent 文件：
2. project-overview.md §3.3 — 更新文件清单

不需要同步：
- clean-ragmem.bat（不涉及 Docker）
- deploy.bat（mcp-server 整目录复制，自动包含）
- prepare-images.sh（不涉及镜像构建）
```

### 场景 B：新增一个 Docker 服务（如 Redis）

```
变更：docker-compose.yml 新增 redis 服务

需要同步的脚本：
1. clean-ragmem.bat — Step 2 添加卷，Step 3 添加镜像
2. deploy.bat — Phase 3 添加镜像加载
3. prepare-images.sh — 如果需要自定义镜像，新增补丁
4. build-dist.bat — 如果有新的配置文件

需要自维护的 Agent 文件：
5. project-overview.md §4 — 更新架构图
6. project-overview.md §3 — 更新文件清单
7. env-cleanup/SKILL.md §5 — 更新 Docker 资源命名表
8. deployment/SKILL.md §6 — 更新健康检查表

不需要同步：
- update-config.bat（除非有新配置文件）
```

### 场景 C：重命名环境变量

```
变更：.env.example 中 LITELLM_BASE_URL → LLM_GATEWAY_URL

需要同步的脚本：
1. docker-compose.yml — 更新所有引用
2. prepare-images.sh — 如果 Patch 4 引用了该变量

需要同步的文档：
3. DEPLOY.md — 更新文档中的变量说明

需要自维护的 Agent 文件：
4. project-overview.md §5 — 更新环境变量传递链
5. deployment/SKILL.md §4.2 — 更新传递链说明

不需要同步：
- build-dist.bat（不涉及文件增删）
- clean-ragmem.bat（不涉及 Docker 资源名）
```

### 场景 D：新增一个 MCP 工具

```
变更：server.py 新增 @mcp.tool() 函数

需要同步的脚本：
（通常无需同步脚本，除非新增了 Python 模块）

需要自维护的 Agent 文件：
1. project-overview.md §3.3 — 更新工具数量
2. mcp-server-dev/SKILL.md §4.3 — 更新工具清单表
```

---

## 8. 关联技能

- `docker-image-build` — 新增补丁时触发本技能
- `dist-packaging` — 文件变更时触发本技能
- `env-cleanup` — 服务变更时触发本技能
- `deployment` — 部署结构变更时触发本技能
- `mcp-server-dev` — 包结构变更时触发本技能
