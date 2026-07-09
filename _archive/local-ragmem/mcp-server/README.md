# RagMem MCP Server

为 AI Agent 提供 **mem0（跨会话记忆）** 和 **LightRAG（知识库 RAG）** 的 MCP 工具接口。

## 前置条件

- Python 3.10+
- [uv](https://docs.astral.sh/uv/) 包管理器
- RagMem 服务已部署并运行（参见 `../stack/README.md`）

## 安装与运行

### 方式 A：uvx 直接运行（推荐）

```bash
# 从本地源码安装并运行
uvx --from ./mcp-server ragmem-mcp-server
```

### 方式 B：开发模式

```bash
cd mcp-server
uv pip install -e .
ragmem-mcp-server
```

## MCP 客户端配置

### 沙箱环境（推荐 — 通过 WSL2 运行）

在沙箱环境中，Windows 无法直接访问 WSL2 容器的 localhost 端口。
ragmem MCP Server 必须运行在 WSL2 内部，通过 `wsl` 命令启动。

**Roo Code / Cursor** — `.vscode/mcp.json`：

```json
{
  "servers": {
    "ragmem": {
      "command": "wsl",
      "args": [
        "-d", "Ubuntu-24.04", "--",
        "bash", "-c",
        "source ~/.local/bin/env 2>/dev/null; MEM0_URL=http://localhost:18910 LIGHTRAG_URL=http://localhost:18920 uvx --from ~/ragmem/mcp-server ragmem-mcp-server"
      ]
    }
  }
}
```

**Claude Desktop** — `claude_desktop_config.json`：

```json
{
  "mcpServers": {
    "ragmem": {
      "command": "wsl",
      "args": [
        "-d", "Ubuntu-24.04", "--",
        "bash", "-c",
        "source ~/.local/bin/env 2>/dev/null; MEM0_URL=http://localhost:18910 LIGHTRAG_URL=http://localhost:18920 uvx --from ~/ragmem/mcp-server ragmem-mcp-server"
      ]
    }
  }
}
```

> `source ~/.local/bin/env` 确保 uv/uvx 在 PATH 中（uv 安装器的默认行为）。
> `deploy.bat` 会自动将 mcp-server 源码复制到 WSL2 的 `~/ragmem/mcp-server`。

### 普通环境（Windows 可直接访问 WSL2 端口）

如果不在沙箱内，Windows → WSL2 的 localhost 端口转发通常可用，
MCP Server 可以直接在 Windows 侧运行。

**Roo Code / Cursor** — `.vscode/mcp.json`：

```json
{
  "servers": {
    "ragmem": {
      "command": "uvx",
      "args": ["--from", "<path-to-mcp-server>", "ragmem-mcp-server"],
      "env": {
        "MEM0_URL": "http://localhost:18910",
        "LIGHTRAG_URL": "http://localhost:18920"
      }
    }
  }
}
```

> 将 `<path-to-mcp-server>` 替换为 `mcp-server` 目录的实际路径。

**Claude Desktop** — `claude_desktop_config.json`：

```json
{
  "mcpServers": {
    "ragmem": {
      "command": "uvx",
      "args": ["--from", "<path-to-mcp-server>", "ragmem-mcp-server"],
      "env": {
        "MEM0_URL": "http://localhost:18910",
        "LIGHTRAG_URL": "http://localhost:18920"
      }
    }
  }
}
```

## 环境变量

| 变量 | 默认值 | 说明 |
|------|--------|------|
| `MEM0_URL` | `http://localhost:18910` | mem0 服务地址 |
| `LIGHTRAG_URL` | `http://localhost:18920` | LightRAG 服务地址 |
| `RAGMEM_USER_ID` | `default` | mem0 默认 user_id |
| `RAGMEM_AGENT_ID` | _(空)_ | mem0 可选 agent_id |

## 提供的 Tools

### 记忆（mem0）

| Tool | 说明 |
|------|------|
| `memory_add` | 存储一条记忆（决策、偏好、上下文等） |
| `memory_search` | 语义搜索相关记忆 |
| `memory_list` | 列出用户所有记忆 |
| `memory_delete` | 删除指定记忆 |

### 知识库（LightRAG）

| Tool | 说明 |
|------|------|
| `rag_index_text` | 索引文本到知识库 |
| `rag_index_file` | 索引文件到知识库 |
| `rag_query` | 查询知识库（支持 naive/local/global/hybrid 模式） |
| `rag_list_documents` | 列出已索引文档 |

### 工具

| Tool | 说明 |
|------|------|
| `ragmem_health` | 检查所有服务健康状态 |

## 使用示例

Agent 可以这样使用这些工具：

```
# 存储记忆
memory_add(content="用户偏好使用 UniTask 而非 Coroutine 做异步操作")

# 搜索记忆
memory_search(query="异步编程偏好")

# 索引文档到知识库
rag_index_text(text="项目使用 URP 渲染管线，Unity 2022.3 LTS 版本")

# 查询知识库
rag_query(query="项目使用什么渲染管线？")

# 健康检查
ragmem_health()
```

## 架构

**沙箱环境**（推荐）— MCP Server 运行在 WSL2 内，与 Docker 容器同网络：

```
┌─ Windows ──────────────────────────────────────────┐
│  Agent (Roo Code / Cursor / Claude)                │
│    ├── Unity MCP (stdio via uvx) → Unity Editor    │
│    └── ragmem MCP (stdio via wsl)                  │
│              │                                     │
├─ WSL2 ─────│─────────────────────────────────────┤
│         ragmem-mcp (Python/uvx)                    │
│           ├── memory_* → HTTP → mem0 (:18910)      │
│           └── rag_*    → HTTP → LightRAG (:18920)  │
└────────────────────────────────────────────────────┘
```

**普通环境** — MCP Server 直接在 Windows 运行：

```
Agent (Roo Code / Cursor / Claude)
  └── ragmem-mcp (stdio, Windows Python)
        ├── memory_* tools → HTTP → mem0 (localhost:18910)
        └── rag_* tools    → HTTP → LightRAG (localhost:18920)
```

MCP Server 本身是一个轻量 Python 进程，通过 HTTP 转发请求到已部署的 mem0 和 LightRAG 服务。
在沙箱环境中，由于 Windows 无法直接访问 WSL2 容器端口，MCP Server 必须运行在 WSL2 内部。
