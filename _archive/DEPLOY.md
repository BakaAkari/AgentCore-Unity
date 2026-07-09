# 部署流程

##  LLM 自动部署（推荐）

> **适用场景**：用户在 AI 客户端（Roo Code / Cursor / Copilot 等）中让 LLM 代理自动完成部署。
> LLM 读取本文档后，按以下流程逐步执行，用户只需确认和搬运文件。

### 前提

- 沙盘（sandbox）机器：Windows 10/11，已启用 WSL2，已安装 Docker
- 联网机器（可以是同一台，也可以是另一台）：用于拉取 Docker 镜像

### 阶段 A：联网环境准备（沙盘外）

LLM 在联网机器上执行以下命令，或引导用户执行：

```powershell
# 1. 拉取并导出 Docker 镜像（约 2-3 GB）
cd local-ragmem
.\prepare-images.bat
# 产出: local-ragmem/images/*.tar
```

阶段 A 完成后，LLM 应**停止执行**并提示用户进行手动文件拷贝。

> ** 用户手动操作**：请将本项目的**整个根目录**（包含 `local-ragmem/`、`unity-agent-rules/`、`unity-mcp-setup/`、`DEPLOY.md` 等所有文件）复制到沙盘机器的任意目录中。
>
> 复制完成后，在沙盘内的 AI 客户端中打开该目录，然后告诉 LLM：**"请按照 DEPLOY.md 继续执行阶段 B 部署"**。

### 阶段 B：沙盘内部署

> **LLM 入口**：当用户在沙盘内调用 LLM 并指向本文件时，从这里开始执行。
> LLM 应先确认当前工作目录包含 `local-ragmem/` 子目录。

LLM 在沙盘机器上按顺序执行：

**B1. 初始化环境配置**

所有后端服务配置（LiteLLM、Embedding、数据库）已在 `.env.example` 中预设为团队标准值，无需用户修改。
LLM 只需**生成 `.env`**：直接复制模板

```powershell
cd local-ragmem\stack
copy .env.example .env
```

> **预设配置一览**（来自 `.env.example`，用户无需关心）：
>
> | 配置项 | 预设值 | 说明 |
> |--------|--------|------|
> | LiteLLM 地址 | `http://172.16.249.43:8000` | 团队内网 LLM 代理 |
> | LLM 模型 | `claude-opus-4-6` | 默认最佳模型 |
> | Embedding 地址 | `http://172.16.248.60:8001` | 团队内网 Embedding 服务 |
> | Embedding 模型 | `qwen3-embedding` (0.6B, 1024d) | 已验证兼容 pgvector HNSW |
>
> 如需自定义，可在部署完成后手动编辑 `local-ragmem/stack/.env` 并执行 `update-config.bat` 重启服务。

**B2. 一键部署 RagMem 后端**

```bat
cd local-ragmem\stack
deploy.bat
```

> `deploy.bat` 自动完成：Zone 标记清理 → WSL2/Docker 检查 → 文件拷贝到 WSL2 → 镜像加载 → 服务启动 → MCP Server 安装。

**B3. 验证后端服务**

```bat
wsl -d Ubuntu-24.04 --cd ~/ragmem -- docker compose ps
wsl -d Ubuntu-24.04 --cd ~ -- curl -s http://localhost:18910/docs
wsl -d Ubuntu-24.04 --cd ~ -- curl -s http://localhost:18920/health
```

> LLM 检查输出：3 个容器应为 `healthy`，curl 应返回有效 JSON。

**B4. 配置 AI 客户端 MCP（自动检测）**

LLM 应检测系统中已安装的 AI IDE/CLI，然后针对每个工具配置 `ragmem` MCP server：

**检测逻辑**：

| 工具 | 检测方法 | 配置文件路径 |
|------|---------|-------------|
| **Claude Desktop** | 检查 `%APPDATA%\Claude\claude_desktop_config.json` 是否存在 | `%APPDATA%\Claude\claude_desktop_config.json` |
| **Cursor** | 检查 `%USERPROFILE%\.cursor\mcp.json` 是否存在 | `%USERPROFILE%\.cursor\mcp.json` |
| **Roo Code** | 检查 `%APPDATA%\Code\User\globalStorage\rooveterinaryinc.roo-cline\settings\mcp_settings.json` 是否存在 | `%APPDATA%\Code\User\globalStorage\rooveterinaryinc.roo-cline\settings\mcp_settings.json` |
| **OpenCode** | 检查 `opencode` CLI 命令是否可用，或 `%USERPROFILE%\.config\opencode\opencode.json` 是否存在 | `%USERPROFILE%\.config\opencode\opencode.json`（全局） |
| **Claude Code** | 检查 `claude` CLI 命令是否可用 | CLI 命令或 `~/.claude/settings.json` |

**配置模板 — 仅 ragmem**（LLM 根据检测结果自动写入）：

<details>
<summary>Claude Desktop / Cursor</summary>

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
</details>

<details>
<summary>OpenCode</summary>

推荐使用项目自带的自动配置脚本（PowerShell）：

```powershell
cd unity-mcp-setup\tools
.\configure-opencode-mcp.ps1 -RagmemOnly
```

该脚本会自动检测并安全合并配置到 `%USERPROFILE%\.config\opencode\opencode.json`，保留已有的其他 MCP 设置。

如需移除 MCP 条目：

```powershell
.\configure-opencode-mcp.ps1 -Remove
```

> 全局配置文件路径：`%USERPROFILE%\.config\opencode\opencode.json`（Linux/macOS: `~/.config/opencode/opencode.json`）
> 也可放在项目根目录的 `opencode.json` 中（项目级）。
> **注意**：OpenCode 的 MCP schema 与 Cursor/Claude Desktop 不同，`local` 类型的 `command` 必须是数组格式。手动编辑容易出错，建议优先使用上面的脚本。

如果脚本不支持 `-RagmemOnly` 参数，LLM 可直接手动写入 OpenCode 配置文件，仅添加 `ragmem` 条目：

```json
{
  "mcp": {
    "ragmem": {
      "type": "local",
      "command": ["wsl", "-d", "Ubuntu-24.04", "--", "bash", "-c", "source ~/.local/bin/env 2>/dev/null; MEM0_URL=http://localhost:18910 LIGHTRAG_URL=http://localhost:18920 uvx --from ~/ragmem/mcp-server ragmem-mcp-server"]
    }
  }
}
```
</details>

<details>
<summary>Roo Code</summary>

```json
{
  "mcpServers": {
    "ragmem": {
      "command": "wsl",
      "args": [
        "-d", "Ubuntu-24.04", "--",
        "bash", "-c",
        "source ~/.local/bin/env 2>/dev/null; MEM0_URL=http://localhost:18910 LIGHTRAG_URL=http://localhost:18920 uvx --from ~/ragmem/mcp-server ragmem-mcp-server"
      ],
      "disabled": false
    }
  }
}
```

> 修改后需 `Ctrl+Shift+P` → `Reload Window` 使配置生效。
</details>

<details>
<summary>Claude Code（CLI）</summary>

```bash
claude mcp add ragmem --transport stdio --command wsl --args "-d,Ubuntu-24.04,--,bash,-c,source ~/.local/bin/env 2>/dev/null; MEM0_URL=http://localhost:18910 LIGHTRAG_URL=http://localhost:18920 uvx --from ~/ragmem/mcp-server ragmem-mcp-server"
```
</details>

**LLM 执行流程**：
1. 依次检测上述工具的配置文件/命令是否存在
2. 对于检测到的每个工具，读取现有配置（如果存在）
3. 合并新的 `ragmem` 配置（保留现有其他 MCP servers）
4. 写回配置文件
5. 提示用户重启对应的 IDE/CLI 使配置生效

> **注意**：如果检测到多个工具，LLM 应配置所有检测到的工具，而不是只配置一个。

**B5. 端到端验证**

LLM 依次调用以下 MCP 工具验证全链路：

1. `ragmem_health` → 确认 mem0 + LightRAG 均可达
2. `memory_add` → 写入测试记忆 → `memory_search` → 查回验证

> 全部通过后，RagMem 系统部署完成。LLM 应告知用户：
>
>  **RagMem 系统已部署成功！** 可用的 MCP 工具包括：`memory_add`、`memory_search`、`memory_list`、`memory_delete`、`rag_index_text`、`rag_index_file`、`rag_query`、`rag_list_documents`、`ragmem_health`。
>
>  **可选：安装 Unity MCP**
> 如果你需要在 Unity 项目中使用 AI 辅助开发（场景管理、代码生成、物理调试等），可以让 LLM 帮你安装 Unity MCP。
> 请告诉 LLM：**"请帮我安装 Unity MCP"**，并提供你的 Unity 项目路径。
> 详细步骤见本文档的 [可选：安装 Unity MCP](#可选安装-unity-mcp) 章节。

### LLM 自动部署流程图

```
联网机器（LLM 阶段 A）            沙盘机器（LLM 阶段 B）
──────────────────               ──────────────────
prepare-images.bat ──┐
                     │  LLM 停止，提示用户操作
                     ├──  用户拷贝项目目录到沙盘，打开项目，调用 LLM
                     │
                     │           copy .env.example .env (直接使用预设配置)
                     │           deploy.bat (一键部署后端)
                     │           验证后端服务 (docker compose ps + curl)
                     │           检测 IDE/CLI → 自动配置 ragmem MCP
                     │           ragmem_health + memory 读写验证
                     │
                     └─────────  RagMem 部署完成（用户仅交互 1 次）
                                 提示用户可选安装 Unity MCP
```

---

## 可选：安装 Unity MCP

> **适用场景**：用户需要在 Unity 项目中使用 AI 辅助开发。此步骤需要用户提供 Unity 项目路径，因此不包含在自动部署流程中。
> 用户可以在 RagMem 部署完成后，随时让 LLM 帮忙执行以下步骤。

### 前提

- RagMem 系统已部署完成（阶段 B 已通过验证）
- Unity 2021.3+ 项目已在沙盘机器上打开
- 用户需提供 Unity 项目的完整路径

### U1. 收集 Unity 项目路径

LLM 询问用户 Unity 项目的完整路径（例如 `D:\Projects\MyGame`）。

### U2. 部署 Agent Rules 到 Unity 项目

```powershell
.\unity-agent-rules\tools\deploy-agent-rules.ps1 -ProjectPath "D:\你的Unity项目路径" -Force
```

> 此步骤将 AGENTS.md、.agents/ skills 和 generate-snapshot.ps1 复制到 Unity 项目根目录。
> 部署后建议运行 `.\tools\generate-snapshot.ps1` 生成项目索引。

### U3. 安装 Unity MCP 包（离线）

```powershell
.\unity-mcp-setup\tools\install-unity-mcp.ps1 -Local -ProjectPath "D:\你的Unity项目路径"
```

> LLM 应先用 `-Check` 检查是否已安装，再决定是否执行安装。
> 安装后 Unity MCP 会自动运行，无需手动启动服务。

### U4. 安装 Unity MCP Python Bridge（离线）

```powershell
# 获取 pypi-cache 的绝对路径
$cacheDir = (Resolve-Path ".\unity-mcp-setup\packages\pypi-cache").Path
uv tool install mcpforunityserver --find-links $cacheDir --no-index --offline --force
```

> 此步骤从预缓存的 wheel 文件离线安装 `mcpforunityserver`（Unity MCP 的 Python stdio 桥接进程）。
> 安装后 `uvx --from mcpforunityserver mcp-for-unity` 即可直接运行，无需联网。
> 如果报错 Python ABI 不匹配（如 `cp312` vs `cp313`），需要在联网机器上用 `-PythonVersion` 参数重新缓存：
> ```powershell
> .\unity-mcp-setup\tools\cache-unity-mcp-bridge.ps1 -PythonVersion 3.13 -Clean
> ```

### U5. 配置 AI 客户端 Unity MCP

在已有的 MCP 配置中追加 `unityMCP` 条目：

<details>
<summary>Claude Desktop / Cursor / Roo Code</summary>

在已有的 `mcpServers` 中添加：

```json
{
  "unityMCP": {
    "command": "uvx",
    "args": ["--from", "mcpforunityserver", "mcp-for-unity", "--transport", "stdio"]
  }
}
```

>  如果 `uvx` 不在系统 PATH 中，需要将 `"command"` 改为完整路径，例如 `"C:/Users/你的用户名/.local/bin/uvx.exe"`。
</details>

<details>
<summary>OpenCode</summary>

```powershell
cd unity-mcp-setup\tools
.\configure-opencode-mcp.ps1
```

或手动在 `opencode.json` 的 `mcp` 中添加：

```json
{
  "unityMCP": {
    "type": "local",
    "command": ["uvx", "--from", "mcpforunityserver", "mcp-for-unity", "--transport", "stdio"]
  }
}
```
</details>

<details>
<summary>Claude Code（CLI）</summary>

```bash
claude mcp add unityMCP -- uvx --from mcpforunityserver mcp-for-unity --transport stdio
```
</details>

### U6. 验证 Unity MCP

LLM 调用 Unity MCP 工具验证连通性：

```
manage_scene（get_hierarchy）→ 确认 Unity MCP 连通，应返回场景数据
```

> 通过后，Unity MCP 安装完成。LLM 应告知用户可用的 Unity MCP 工具列表。

---

## 手动部署

> 以下是手动逐步部署的详细说明，适合需要精细控制每个步骤的场景。

### 前置条件

- Windows 10/11，已启用 WSL2
- Docker Desktop 或 WSL2 内 Docker Engine
- PowerShell 5.1+
- （可选）Unity 2021.3+（仅安装 Unity MCP 时需要）

### 端口表

| 服务 | 端口 | 说明 |
|------|------|------|
| mem0 | 18910 | 记忆存储 API |
| LightRAG | 18920 | 知识库 RAG API |
| pgvector | 18930 | PostgreSQL 向量数据库（内部） |
| Unity MCP Socket | 6400 | Unity 编辑器内部通信（可选，自动，无需配置） |

> Unity MCP 使用 stdio 模式连接，AI 终端通过 `uvx` 启动桥接进程，桥接进程通过 socket 端口 6400 与 Unity 编辑器通信。

---

### 一、准备 Docker 镜像（联网环境，仅首次）

```bat
cd local-ragmem
prepare-images.bat
```

生成 `images/*.tar`，后续拷贝到沙箱机器。

### 二、部署 RagMem 后端

```bat
cd local-ragmem\stack
deploy.bat
```

自动完成：WSL2/Docker 检查 → 文件拷贝 → 镜像加载 → 服务启动 → MCP Server 安装。

**验证：**

```bat
wsl -d Ubuntu-24.04 --cd ~/ragmem -- docker compose ps
wsl -d Ubuntu-24.04 --cd ~ -- curl -s http://localhost:18910/docs
wsl -d Ubuntu-24.04 --cd ~ -- curl -s http://localhost:18920/health
```

### 三、配置 AI 客户端

根据你使用的 AI 客户端，将 `ragmem` MCP server 配置写入对应的配置文件。各客户端的配置文件路径和 JSON 格式详见上方 **B4. 配置 AI 客户端 MCP（自动检测）** 中的检测表和配置模板。

**OpenCode 示例**（自动配置脚本）：

```powershell
cd unity-mcp-setup\tools
.\configure-opencode-mcp.ps1 -RagmemOnly
```

> 该脚本会自动将 `ragmem`（local stdio）注册到 `%USERPROFILE%\.config\opencode\opencode.json`，并保留已有配置。

> 如果你使用的 AI 终端不在上述列表中，请参考 **MCP-MANUAL-CONNECT.md** 手动配置。

### 四、验证全链路

1. AI 客户端中调用 `ragmem_health` → 应返回 mem0 + LightRAG 状态
2. 调用 `memory_add` 写入一条记忆 → 调用 `memory_search` 查回

### 五、（可选）安装 Unity MCP

如需 Unity AI 辅助开发，参照上方 [可选：安装 Unity MCP](#可选安装-unity-mcp) 章节执行 U1-U6 步骤。

### 六、日常操作

```bat
REM 启动服务
wsl -d Ubuntu-24.04 --cd ~/ragmem -- docker compose up -d

REM 停止服务
wsl -d Ubuntu-24.04 --cd ~/ragmem -- docker compose down

REM 查看日志
wsl -d Ubuntu-24.04 --cd ~/ragmem -- docker compose logs -f

REM 更新 .env 后重启
cd local-ragmem\stack
update-config.bat
```
