# `LLM AI Toolkit` Workspace Rules File

> **版本**: 1.0.0 | **最后更新**: 2026-04-02
>
> 本文件是 AI Agent 在此工作区操作的**根规则**。
> 任何 AI（Roo、OpenCode、Cursor 等）在此工作区执行任务前**必须先读取本文件**。

---

## 目录用途

本工作区是 **LLM AI Toolkit** — 一个面向团队的 AI 基础设施分发包，包含：

| 子系统 | 路径 | 说明 |
|--------|------|------|
| RagMem 服务栈 | `local-ragmem/` | mem0 + LightRAG + pgvector Docker 部署 |
| MCP Server | `local-ragmem/mcp-server/` | ragmem-mcp Python 包（stdio 模式） |
| Docker 镜像构建 | `local-ragmem/prepare-images.sh` | 带 7 个补丁的镜像构建脚本 |
| Unity Agent Rules | `unity-agent-rules/` | 部署到 Unity 项目的 AGENTS.md + skills |
| Unity MCP Setup | `unity-mcp-setup/` | Unity MCP 安装工具、离线包、文档 |
| 部署自动化 | `local-ragmem/stack/deploy.bat` | 一键部署到 WSL2 |
| 分发打包 | `build-dist.bat` | 构建可分发的 zip 包 |
| 环境清理 | `clean-ragmem.bat` | 清理 WSL2 中的 ragmem 环境 |
| 配置更新 | `local-ragmem/stack/update-config.bat` | 推送配置到 WSL2 并重启 |

---

## 1. 工作原则

### 1.1 先理解上下文，再行动

- **必读文件**：开始任何任务前，先读取 `.agents/context/project-overview.md` 了解项目全貌
- **必读技能**：根据任务类型，查阅 §7 技能路由表，加载对应的 Skill 文件
- 如果上下文文件不存在或过期（>30 天），先执行上下文更新再开始工作

### 1.2 目标导向，最小变更

- 只修改与任务直接相关的文件
- 不做"顺手"重构，除非用户明确要求
- 每次变更必须能清晰说明"为什么改"和"改了什么"

### 1.3 可审计、可复现、可回退

- 所有自动化脚本的修改必须保留注释说明变更原因
- Docker 镜像补丁使用编号系统（Patch N/M），新增补丁追加编号
- 配置变更通过环境变量控制，不硬编码

---

## 2. 硬性规则

### 2.1 尊重项目结构

```
LLM AI/
├── AGENTS.md                    ← 本文件（AI 根规则）
├── .agents/                     ← AI 技能和上下文
├── DEPLOY.md                    ← 用户部署指南
├── build-dist.bat               ← 分发打包脚本
├── clean-ragmem.bat             ← 环境清理脚本
├── local-ragmem/                ← RagMem 核心
│   ├── prepare-images.sh/bat    ← Docker 镜像构建
│   ├── mcp-server/              ← MCP Server 源码
│   └── stack/                   ← Docker Compose 部署栈
│       ├── deploy.bat           ← 一键部署
│       ├── docker-compose.yml   ← 服务编排
│       ├── .env.example         ← 环境变量模板
│       └── mem0/                ← mem0 自定义文件
├── unity-agent-rules/           ← Unity Agent Rules（独立子项目）
└── unity-mcp-setup/             ← Unity MCP 安装工具（独立子项目）
```

### 2.2 不破坏分发包完整性

- `build-dist.bat` 定义了分发包包含的所有文件
- **新增任何可分发文件时，必须同步更新 `build-dist.bat`**
- 删除或重命名文件时，同样必须更新 `build-dist.bat`
- `.agents/` 目录**不包含**在分发包中（它是开发工具，不是用户交付物）

### 2.3 Docker 补丁编号系统

- `prepare-images.sh` 中的补丁使用 `Patch N/M` 格式
- 新增补丁必须追加到末尾，更新总数 M
- 不得修改已有补丁的编号
- 每个补丁必须有注释说明其目的

### 2.4 环境变量优先于硬编码

- 所有可配置项必须通过 `.env.example` 中的环境变量控制
- `docker-compose.yml` 中使用 `${VAR:-default}` 语法提供默认值
- 默认值应选择最通用的选项（如 `openai` 作为 embedder provider 默认值）

### 2.5 WSL2 部署约束

- 所有 shell 脚本必须使用 LF 换行符（`.gitattributes` 已配置）
- Windows `.bat` 脚本通过 `wsl -d Ubuntu-24.04` 调用 Linux 命令
- 路径转换：Windows `D:\path` → WSL `/mnt/d/path`
- Docker 容器访问宿主机服务使用 `host.docker.internal`

### 2.6 Unity 子项目是独立模块

本工作区包含两个 Unity 相关的独立子项目：

| 子项目 | 路径 | 职责 | 有独立 AGENTS.md |
|--------|------|------|------------------|
| Agent Rules | `unity-agent-rules/` | 部署到 Unity 项目的 AI 规则和技能 |  |
| MCP Setup | `unity-mcp-setup/` | Unity MCP 安装工具、离线包、文档 |  |

- `unity-agent-rules/` 有自己的 `AGENTS.md` 和 `.agents/` 目录，修改其内容时遵循其自身的规则
- `unity-mcp-setup/` 是纯工具集合，无独立规则文件，遵循本文件的规则
- 本文件的规则不覆盖 `unity-agent-rules/AGENTS.md` 的规则

### 2.7 文件修改权限边界

#### Agent 自有文件（始终可读写）

以下文件/目录由 AI Agent 管理，可自由修改：

- `.agents/` — 所有技能和上下文文件
- `DEPLOYMENT-REVIEW.md` — 部署审查文档
- `DEPLOYMENT-REPORT.md` — 部署报告

#### 项目文件（需确认后修改）

以下文件影响项目功能，修改前必须向用户确认：

- `local-ragmem/` — 所有源码和配置
- `build-dist.bat` — 分发打包脚本
- `clean-ragmem.bat` — 环境清理脚本
- `DEPLOY.md` — 用户部署指南
- `unity-agent-rules/` — Unity Agent Rules
- `unity-mcp-setup/` — Unity MCP 安装工具

#### 决策标准

| 场景 | 行为 |
|------|------|
| 用户明确要求修改某文件 | 直接修改 |
| 任务隐含需要修改项目文件 | 先说明原因，等待确认 |
| 只需更新 `.agents/` | 直接修改 |
| 自动化脚本需要同步更新 | 列出所有需要变更的脚本，等待确认后批量修改 |

---

## 3. 自动化脚本同步机制

> **核心理念**：自动化脚本必须永远适配最新的项目结构。

### 3.1 脚本清单与职责

| 脚本 | 职责 | 关键依赖 |
|------|------|----------|
| `build-dist.bat` | 打包分发文件 | 所有可分发文件的路径 |
| `clean-ragmem.bat` | 清理 WSL2 环境 | Docker 容器名、镜像名、卷名、目录路径 |
| `local-ragmem/stack/deploy.bat` | 一键部署 | WSL2 配置、Docker 镜像、MCP 包路径 |
| `local-ragmem/stack/update-config.bat` | 推送配置 | 配置文件路径 |
| `local-ragmem/prepare-images.sh` | 构建镜像 | 补丁列表、依赖包 |

### 3.2 变更触发规则

当以下变更发生时，AI **必须**检查并提议更新相关脚本：

| 变更类型 | 影响的脚本 | 检查项 |
|----------|-----------|--------|
| 新增/删除/重命名源文件 | `build-dist.bat` | 文件是否需要包含在分发包中 |
| 新增/删除 Docker 服务 | `clean-ragmem.bat`, `deploy.bat` | 容器名、镜像名、卷名 |
| 修改 Docker 镜像名/标签 | `clean-ragmem.bat`, `deploy.bat` | 镜像引用一致性 |
| 修改 `docker-compose.yml` 卷名 | `clean-ragmem.bat` | 卷清理命令 |
| 新增环境变量 | `.env.example`, `docker-compose.yml` | 变量传递链完整性 |
| 修改 MCP Server 包结构 | `deploy.bat`, `build-dist.bat` | 安装路径、包名 |
| 新增 `prepare-images.sh` 补丁 | `prepare-images.sh` 自身 | 补丁编号、总数 |
| 修改部署目录结构 | `deploy.bat`, `update-config.bat` | WSL2 路径引用 |

### 3.3 同步检查流程

```
1. 完成主要代码变更
2. 运行心理检查清单（§3.2 的触发规则表）
3. 列出所有需要同步更新的脚本
4. 向用户展示变更计划
5. 获得确认后批量更新
6. 更新 .agents/context/project-overview.md 中的文件清单
```

### 3.4 Agent 自维护规则

> **核心理念**：Agent 的文档、技能和上下文必须与项目保持同步，否则下一个 AI 会基于过时信息做出错误决策。

#### 3.4.1 自维护触发条件

| 变更类型 | 需要更新的 Agent 文件 |
|----------|---------------------|
| 新增/删除/重命名源文件 | `project-overview.md` §3 文件清单 |
| 新增/修改 Docker 补丁 | `project-overview.md` §6 补丁清单，`docker-image-build/SKILL.md` §4.1 补丁表 |
| 新增/修改环境变量 | `project-overview.md` §5 环境变量传递链 |
| 新增/修改 Docker 服务 | `project-overview.md` §4 架构图，`env-cleanup/SKILL.md` §5 资源表 |
| 新增 MCP 工具 | `project-overview.md` §3.3 工具数量，`mcp-server-dev/SKILL.md` §4.3 工具清单 |
| 新增自动化脚本 | `AGENTS.md` §3.1 脚本清单，`script-sync/SKILL.md` §3.1 矩阵 |
| 新增技能 | `AGENTS.md` §7.1 目录结构 + §7.2 路由表，`skills/README.md` |
| 修改部署流程 | `deployment/SKILL.md` §4.1 阶段结构 |

#### 3.4.2 自维护执行流程

```
1. 完成代码变更 + 脚本同步（§3.3）
2. 对照 §3.4.1 触发条件表，列出需要更新的 Agent 文件
3. 逐个更新（Agent 自有文件无需用户确认）
4. 更新 project-overview.md 的 last_updated 日期
```

#### 3.4.3 上下文过期检测

每次任务开始时，AI **必须**检查：

1. `project-overview.md` 的 `last_updated` 是否超过 30 天
2. `project-overview.md` §3 文件清单是否与实际文件结构一致
3. 如果不一致，**先更新上下文，再开始任务**

#### 3.4.4 技能演进规则

- 新增技能时：创建 `skills/新技能名/SKILL.md`，更新 `skills/README.md` 和 `AGENTS.md` §7
- 技能内容过时时：直接更新 SKILL.md 中的具体数据（如补丁清单、工具清单）
- 技能不再适用时：删除目录，更新索引和路由表
- 每个技能的"关联技能"章节必须保持双向一致

---

## 4. 代码规范

### 4.1 Python（MCP Server）

- 使用 Python 3.10+ 语法（`X | Y` 类型联合、`match` 语句）
- 异步优先：MCP 工具函数使用 `async def`
- 类型注解：所有公开函数必须有完整类型注解
- 错误处理：捕获具体异常，返回结构化错误信息
- 代码格式：遵循 ruff 配置

### 4.2 Shell 脚本（prepare-images.sh）

- 使用 `set -euo pipefail` 严格模式
- 每个步骤有 `echo` 进度输出
- 补丁使用 heredoc + `sed`/`python3 -c` 注入
- 关键操作前检查前置条件

### 4.3 Batch 脚本（.bat）

- 使用 `setlocal enabledelayedexpansion`
- 步骤编号格式：`[N/M]` 或 `[Phase N]`
- 每步有成功/失败输出
- 通过 `wsl -d Ubuntu-24.04` 调用 Linux 命令
- 使用 `>nul 2>nul` 或 `2>/dev/null` 抑制噪音输出

### 4.4 Docker Compose

- 使用 `${VAR:-default}` 环境变量替换
- 服务间依赖使用 `depends_on` + `condition: service_healthy`
- 健康检查使用合理的 interval/timeout/retries

### 4.5 Markdown 文档

- 使用中文撰写面向用户的文档
- 技术术语保留英文（Docker、WSL2、MCP 等）
- 代码块标注语言类型
- 表格对齐

---

## 5. 分析与研究任务标准

### 5.1 结论先行

- 先给出结论/建议，再展开分析
- 明确区分"已确认的事实"和"工程判断"

### 5.2 面向实施

- 分析结果必须包含可执行的下一步
- 如果发现问题，同时提供修复方案
- 使用 P0/P1/P2 优先级分类

### 5.3 部署问题分类

| 优先级 | 定义 | 处理方式 |
|--------|------|----------|
| P0 | 阻断性 — 不修复则部署必然失败 | 立即修复，固化到镜像 |
| P1 | 流程改进 — 不修复需人工介入 | 计划修复 |
| P2 | 稳健性 — 不影响功能但影响体验 | 有空时修复 |

---

## 6. 文档编写规则

### 6.1 文档目标

- 让**不了解项目的 LLM** 能通过阅读文档自主完成部署
- 让**团队成员**能通过文档理解架构决策

### 6.2 默认写作结构

```markdown
# 标题

> 一句话摘要

## 背景/目的
## 具体内容
## 注意事项/已知限制
```

### 6.3 事实与推断标注

-  已验证的事实直接陈述
-  工程判断加 `[工程判断]` 标签
-  未验证的假设加 `[待验证]` 标签

---

## 7. `.agents` 技能与上下文系统

### 7.1 目录结构

```
.agents/
├── context/
│   └── project-overview.md      ← 项目全貌快照（AI 必读）
├── skills/
│   ├── README.md                ← 技能索引
│   ├── docker-image-build/
│   │   └── SKILL.md             ← Docker 镜像构建技能
│   ├── dist-packaging/
│   │   └── SKILL.md             ← 分发打包技能
│   ├── env-cleanup/
│   │   └── SKILL.md             ← 环境清理技能
│   ├── deployment/
│   │   └── SKILL.md             ← 部署流程技能
│   ├── mcp-server-dev/
│   │   └── SKILL.md             ← MCP Server 开发技能
│   └── script-sync/
│       └── SKILL.md             ← 自动化脚本同步技能（核心）
```

### 7.2 技能路由表（必读）

| 任务场景 | 必须加载的 Skill | 说明 |
|----------|-----------------|------|
| 修改 Docker 镜像构建流程 | `docker-image-build` | 补丁系统、镜像标签 |
| 打包分发包 | `dist-packaging` | 文件清单、版本号 |
| 清理部署环境 | `env-cleanup` | 容器/镜像/卷/目录清理 |
| 部署或修改部署流程 | `deployment` | WSL2、Docker Compose |
| 开发 MCP Server 功能 | `mcp-server-dev` | Python、FastMCP、客户端 |
| **任何涉及文件增删改名的变更** | `script-sync` | **自动化脚本同步** |

>  `script-sync` 是最常被触发的技能。几乎所有代码变更都应检查是否需要同步自动化脚本。

### 7.3 项目上下文（必读 + 需维护）

- `.agents/context/project-overview.md` 包含项目的技术栈、文件清单、架构快照
- 当项目结构发生重大变更时，AI 应主动更新此文件
- 上下文文件超过 30 天未更新时，AI 应在任务开始前先更新

### 7.4 上下文有效性判断

| 条件 | 判断 |
|------|------|
| `project-overview.md` 不存在 | 无效 — 需要创建 |
| 文件中的 `last_updated` 超过 30 天 | 可能过期 — 建议更新 |
| 文件中列出的文件路径与实际不符 | 无效 — 需要更新 |
| 文件存在且内容与实际一致 | 有效 — 直接使用 |

---

## 8. 元规则

### 元规则 1：不要假装知道

- 如果不确定某个配置的当前值，先读取文件确认
- 如果不确定某个变更的影响范围，先分析再行动

### 元规则 2：优先兼容当前状态

- 不要假设用户已经执行了某个步骤
- 默认值应该让系统在最常见的配置下工作

### 元规则 3：先解决问题，再追求完美

- Bug 修复优先于重构
- 可工作的方案优先于优雅的方案

### 元规则 4：文档是正式交付物

- 文档变更与代码变更同等重要
- 修改了功能就必须更新对应文档

### 元规则 5：自动化脚本是项目的"免疫系统"

- 自动化脚本的正确性直接决定了其他 LLM 能否自主部署
- 每次项目变更后，都要确保自动化脚本仍然正确
- 这是本项目区别于普通项目的**核心特征**

---

## 9. 当前适用性说明

- 本规则文件适用于 `LLM AI Toolkit` 工作区
- `unity-agent-rules/` 子目录有独立的 `AGENTS.md`，在该子目录内工作时以其规则为准
- `unity-mcp-setup/` 无独立规则文件，遵循本文件的规则
- 本规则会随项目演进持续更新
