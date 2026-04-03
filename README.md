# LLM AI Toolkit

面向游戏团队的 AI 基础设施工具包 —— 一键部署 **记忆系统 + 知识库 + Unity MCP**，让 AI 助手在沙盘环境中也能高效工作。

## 它能做什么

| 能力 | 说明 |
|------|------|
| 🧠 **持久记忆** | 基于 mem0，AI 跨会话记住项目决策和用户偏好 |
| 📚 **知识库 RAG** | 基于 LightRAG，索引项目文档后 AI 可精准检索 |
| 🎮 **Unity 编辑器控制** | 基于 Unity MCP，AI 直接操作场景、资产、脚本 |
| 📦 **离线部署** | 全部组件支持沙盘/内网环境离线安装 |

## 项目结构

```
LLM AI Toolkit/
├── local-ragmem/          # RagMem 后端（Docker 部署）
│   ├── mcp-server/        #   MCP Server（Python，stdio 模式）
│   ├── stack/             #   Docker Compose 编排 + 部署脚本
│   └── prepare-images.sh  #   Docker 镜像构建（含补丁）
├── unity-agent-rules/     # Unity 项目 AI 规则和技能
├── unity-mcp-setup/       # Unity MCP 安装工具 + 离线包
├── DEPLOY.md              # 完整部署指南
└── build-dist.bat         # 分发包打包脚本
```

## 快速开始

### 前提条件

- Windows 10/11 + WSL2 + Docker
- Unity 2021.3+
- AI 客户端（Roo Code / Cursor / Claude Desktop 等任一）

### 部署（推荐让 AI 自动执行）

在 AI 客户端中打开本项目，告诉 AI：

> **"请按照 DEPLOY.md 执行部署"**

AI 会自动完成全部配置。

### 手动部署（3 步）

**1. 部署 RagMem 后端**

```bat
cd local-ragmem\stack
deploy.bat
```

**2. 安装 Unity MCP**

```powershell
.\unity-mcp-setup\tools\install-unity-mcp.ps1 -Local -ProjectPath "D:\你的Unity项目"
```

**3. 配置 AI 客户端 MCP**

将 `unityMCP` 和 `ragmem` 两个 MCP server 写入你的 AI 客户端配置。详见 [DEPLOY.md](DEPLOY.md) 中的配置模板。

## 服务端口

| 服务 | 端口 | 说明 |
|------|------|------|
| mem0 | 18910 | 记忆存储 API |
| LightRAG | 18920 | 知识库 RAG API |
| pgvector | 18930 | PostgreSQL 向量数据库（内部） |
| Unity MCP | 6400 | Unity 编辑器通信（自动） |

## 日常操作

```bat
REM 启动服务
wsl -d Ubuntu-24.04 --cd ~/ragmem -- docker compose up -d

REM 停止服务
wsl -d Ubuntu-24.04 --cd ~/ragmem -- docker compose down

REM 查看日志
wsl -d Ubuntu-24.04 --cd ~/ragmem -- docker compose logs -f

REM 更新配置后重启
cd local-ragmem\stack
update-config.bat
```

## 验证部署

在 AI 客户端中依次测试：

1. **记忆系统** — 调用 `ragmem_health`，确认 mem0 + LightRAG 可达
2. **Unity MCP** — 调用 `manage_scene`（get_hierarchy），确认返回场景数据

## 文档

- [DEPLOY.md](DEPLOY.md) — 完整部署流程（LLM 自动 / 手动）
- [MCP-MANUAL-CONNECT.md](MCP-MANUAL-CONNECT.md) — MCP 手动连接指南
- [unity-mcp-setup/docs/](unity-mcp-setup/docs/) — Unity MCP 部署文档

## License

Internal use only.
