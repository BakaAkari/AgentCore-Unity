# 手动连接 MCP 服务到 AI 终端

> 部署完成后，如果你使用的 AI 终端不在自动配置范围内，可以按照本文档手动添加 MCP 服务。

---

## 服务总览

部署完成后，你的机器上运行着以下 MCP 服务：

| 服务名 | 用途 | 连接方式 | 启动命令 |
|--------|------|---------|----------|
| **Unity MCP** | Unity 编辑器交互 | stdio | `uvx --from mcpforunityserver mcp-for-unity --transport stdio` |
| **RagMem** | 记忆存储 + 知识库 | stdio | 见下方 |

> 两个服务都使用 **stdio** 模式，AI 终端会自动启动和管理进程，无需手动启动。

---

## 前提条件

- **Unity MCP**：Unity 编辑器已打开目标项目，且已安装 `com.coplaydev.unity-mcp` 包（安装后自动运行，无需手动启动服务）
- **RagMem**：后端 Docker 服务已启动（mem0 + LightRAG + pgvector）
- **uvx**：已安装 [uv](https://docs.astral.sh/uv/)（`uvx` 是 uv 自带的工具运行器）

---

## Unity MCP

Unity MCP 通过 stdio 模式连接，AI 终端会自动启动一个桥接进程与 Unity 编辑器通信。

**启动命令：**

```
uvx --from mcpforunityserver mcp-for-unity --transport stdio
```

> Unity 编辑器内部通过 socket 端口 6400 与桥接进程通信，这是自动的，用户无需关心。

### 配置示例

#### JSON 格式（Claude Desktop / Cursor / Roo Code 等）

```json
{
  "unityMCP": {
    "command": "uvx",
    "args": ["--from", "mcpforunityserver", "mcp-for-unity", "--transport", "stdio"]
  }
}
```

> ⚠️ 如果 `uvx` 不在系统 PATH 中，需要使用完整路径，例如：
> - Windows: `C:/Users/你的用户名/.local/bin/uvx.exe`
> - Linux/macOS: `~/.local/bin/uvx`

#### 表单填写（Kimi / Cherry Studio 等 GUI 界面）

| 字段 | 值 |
|------|-----|
| Name | `unityMCP` |
| Transport | `stdio` |
| Command | `uvx` |
| Arguments | `--from mcpforunityserver mcp-for-unity --transport stdio` |
| Environment Variables | （留空） |

> **提示**：如果 Arguments 字段要求逐个填写（每行一个参数），则按以下顺序填入：
>
> 1. `--from`
> 2. `mcpforunityserver`
> 3. `mcp-for-unity`
> 4. `--transport`
> 5. `stdio`

#### CLI 命令（Claude Code 等命令行工具）

```bash
claude mcp add unityMCP -- uvx --from mcpforunityserver mcp-for-unity --transport stdio
```

---

## RagMem

RagMem 也是 stdio 模式，通过 WSL2 中的命令启动。

**启动命令：**

```
wsl -d Ubuntu-24.04 -- bash -c "source ~/.local/bin/env 2>/dev/null; MEM0_URL=http://localhost:18910 LIGHTRAG_URL=http://localhost:18920 uvx --from ~/ragmem/mcp-server ragmem-mcp-server"
```

### 配置示例

#### JSON 格式（Claude Desktop / Cursor / Roo Code 等）

```json
{
  "ragmem": {
    "command": "wsl",
    "args": [
      "-d", "Ubuntu-24.04", "--",
      "bash", "-c",
      "source ~/.local/bin/env 2>/dev/null; MEM0_URL=http://localhost:18910 LIGHTRAG_URL=http://localhost:18920 uvx --from ~/ragmem/mcp-server ragmem-mcp-server"
    ]
  }
}
```

#### 表单填写（Kimi / Cherry Studio 等 GUI 界面）

| 字段 | 值 |
|------|-----|
| Name | `ragmem` |
| Transport | `stdio` |
| Command | `wsl` |
| Arguments | `-d Ubuntu-24.04 -- bash -c "source ~/.local/bin/env 2>/dev/null; MEM0_URL=http://localhost:18910 LIGHTRAG_URL=http://localhost:18920 uvx --from ~/ragmem/mcp-server ragmem-mcp-server"` |
| Environment Variables | （留空，已内嵌在命令中） |

> **提示**：如果 Arguments 字段要求逐个填写（每行一个参数），则按以下顺序填入：
>
> 1. `-d`
> 2. `Ubuntu-24.04`
> 3. `--`
> 4. `bash`
> 5. `-c`
> 6. `source ~/.local/bin/env 2>/dev/null; MEM0_URL=http://localhost:18910 LIGHTRAG_URL=http://localhost:18920 uvx --from ~/ragmem/mcp-server ragmem-mcp-server`

#### CLI 命令（Claude Code 等命令行工具）

```bash
claude mcp add ragmem --transport stdio --command wsl --args "-d,Ubuntu-24.04,--,bash,-c,source ~/.local/bin/env 2>/dev/null; MEM0_URL=http://localhost:18910 LIGHTRAG_URL=http://localhost:18920 uvx --from ~/ragmem/mcp-server ragmem-mcp-server"
```

---

## 完整配置（两个服务一起）

以下是同时配置两个 MCP 服务的完整 JSON，可直接复制到配置文件的 `mcpServers` 中：

```json
{
  "unityMCP": {
    "command": "uvx",
    "args": ["--from", "mcpforunityserver", "mcp-for-unity", "--transport", "stdio"]
  },
  "ragmem": {
    "command": "wsl",
    "args": [
      "-d", "Ubuntu-24.04", "--",
      "bash", "-c",
      "source ~/.local/bin/env 2>/dev/null; MEM0_URL=http://localhost:18910 LIGHTRAG_URL=http://localhost:18920 uvx --from ~/ragmem/mcp-server ragmem-mcp-server"
    ]
  }
}
```

---

## 验证连接

添加完成后，在 AI 终端中测试以下操作：

1. **RagMem 健康检查** — 调用 `ragmem_health` 工具，应返回 mem0 和 LightRAG 均为 `ok`
2. **记忆读写** — 调用 `memory_add` 写入一条测试记忆，再用 `memory_search` 查回
3. **Unity MCP** — 调用任意 Unity 工具（如获取场景层级），应返回 Unity 场景数据

---

## 常见问题

### Unity MCP 连接失败

1. 确认 Unity 编辑器已打开，且已安装 `com.coplaydev.unity-mcp` 包
2. 确认 `uvx` 命令可用：在终端运行 `uvx --version`，如果找不到命令，需要先安装 [uv](https://docs.astral.sh/uv/)
3. 如果 `uvx` 不在 PATH 中，在配置中使用完整路径替代 `uvx`

### RagMem 连接失败

1. 确认后端服务正在运行：
   ```bat
   wsl -d Ubuntu-24.04 --cd ~/ragmem -- docker compose ps
   ```
   应看到 3 个容器（mem0、lightrag、pgvector）状态为 `healthy`。

2. 如果服务未启动：
   ```bat
   wsl -d Ubuntu-24.04 --cd ~/ragmem -- docker compose up -d
   ```

### 端口参考（内部服务）

| 端口 | 服务 | 说明 |
|------|------|------|
| 6400 | Unity MCP Socket | Unity 编辑器内部通信（自动，无需配置） |
| 18910 | mem0 | 记忆存储 API（RagMem 内部使用） |
| 18920 | LightRAG | 知识库 RAG API（RagMem 内部使用） |
| 18930 | pgvector | PostgreSQL 向量数据库（内部） |

> 所有端口都是内部使用的。AI 终端只需通过 stdio 模式连接两个 MCP 服务，它们会自动与后端通信。
