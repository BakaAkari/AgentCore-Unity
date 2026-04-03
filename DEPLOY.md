# 部署流程

## 🤖 LLM 自动部署（推荐）

> **适用场景**：用户在 AI 客户端（Roo Code / Cursor / Copilot 等）中让 LLM 代理自动完成部署。
> LLM 读取本文档后，按以下流程逐步执行，用户只需确认和搬运文件。

### 前提

- 沙盘（sandbox）机器：Windows 10/11，已启用 WSL2，已安装 Docker
- 联网机器（可以是同一台，也可以是另一台）：用于拉取 Docker 镜像和 Unity MCP 包
- Unity 2021.3+ 项目已在沙盘机器上打开

### 阶段 A：联网环境准备（沙盘外）

LLM 在联网机器上执行以下命令，或引导用户执行：

```powershell
# 1. 拉取并导出 Docker 镜像（约 2-3 GB）
cd local-ragmem
.\prepare-images.bat
# 产出: local-ragmem/images/*.tar

# 2. 打包 Unity MCP 离线安装包
cd ..\unity-mcp-setup\tools
.\package-unity-mcp.ps1
# 产出: unity-mcp-setup/packages/com.coplaydev.unity-mcp-*.tgz

# 3. 缓存 Unity MCP Python Bridge（离线 uvx 所需）
.\cache-unity-mcp-bridge.ps1
# 产出: unity-mcp-setup/packages/pypi-cache/*.whl（约 74 个 wheel，~25 MB）
```

阶段 A 完成后，LLM 应**停止执行**并提示用户进行手动文件拷贝。

> **⚠️ 用户手动操作**：请将本项目的**整个根目录**（包含 `local-ragmem/`、`unity-agent-rules/`、`unity-mcp-setup/`、`DEPLOY.md` 等所有文件）复制到沙盘机器的任意目录中。
>
> 复制完成后，在沙盘内的 AI 客户端中打开该目录，然后告诉 LLM：**"请按照 DEPLOY.md 继续执行阶段 B 部署"**。

### 阶段 B：沙盘内部署

> **LLM 入口**：当用户在沙盘内调用 LLM 并指向本文件时，从这里开始执行。
> LLM 应先确认当前工作目录包含 `local-ragmem/`、`unity-agent-rules/` 和 `unity-mcp-setup/` 子目录。

LLM 在沙盘机器上按顺序执行：

**B1. 配置 LiteLLM 环境变量**

LLM 按以下顺序收集配置信息并写入 `.env`：

1. **读取默认值**：读取 `local-ragmem/stack/.env.example` 获取所有配置项的默认值
2. **询问 LiteLLM 连接信息**：
   - LiteLLM 代理地址（默认值来自 `.env.example` 的 `LITELLM_BASE_URL`）
   - LiteLLM API Key（默认值来自 `.env.example` 的 `LITELLM_API_KEY`）
3. **自动发现 LLM 模型**：用上一步获取的 URL + Key 调用 LiteLLM API 获取可用模型列表
   ```powershell
   # LLM 执行此命令获取可用模型列表（每行一个模型名）
   powershell -NoProfile -ExecutionPolicy Bypass -File local-ragmem\stack\select-llm-model.ps1 -BaseUrl "用户提供的URL" -ApiKey "用户提供的Key" -ListOnly
   ```
   - **成功**（退出码 0）：将返回的模型列表作为选项展示给用户，让用户选择
   - **失败**（退出码 1，API 不可达）：降级为手动输入，使用 `.env.example` 中的 `LLM_MODEL` 默认值
4. **询问 Embedding 配置**：provider、模型名、维度等（默认值来自 `.env.example`）
5. **询问 Unity 项目路径**（用于后续 B4 安装 Unity MCP）
6. **确认并写入 `.env`**：
   ```powershell
   cd local-ragmem\stack
   copy .env.example .env
   # LLM 将用户确认的所有配置值写入 .env
   ```

> **关键**：LLM 模型选择必须在获取 URL + Key 之后、写入 `.env` 之前完成。
> 这样用户可以从实际可用的模型列表中选择，而不是盲猜模型名称。

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

**B3.5. 部署 Agent Rules 到 Unity 项目**

```powershell
.\unity-agent-rules\tools\deploy-agent-rules.ps1 -ProjectPath "D:\你的Unity项目路径" -Force
```

> 此步骤将 AGENTS.md、.agents/ skills 和 generate-snapshot.ps1 复制到 Unity 项目根目录。
> 部署后建议运行 `.\tools\generate-snapshot.ps1` 生成项目索引。

**B4. 安装 Unity MCP（离线）**

```powershell
.\unity-mcp-setup\tools\install-unity-mcp.ps1 -Local -ProjectPath "D:\你的Unity项目路径"
```

> LLM 应先用 `-Check` 检查是否已安装，再决定是否执行安装。
> 安装后 Unity MCP 会自动运行，无需手动启动服务。

**B4.5. 安装 Unity MCP Python Bridge（离线）**

```powershell
# 获取 pypi-cache 的绝对路径
$cacheDir = (Resolve-Path ".\unity-mcp-setup\packages\pypi-cache").Path
uv tool install mcpforunityserver --find-links $cacheDir --no-index --offline --force
```

> 此步骤从阶段 A 预缓存的 wheel 文件离线安装 `mcpforunityserver`（Unity MCP 的 Python stdio 桥接进程）。
> 安装后 `uvx --from mcpforunityserver mcp-for-unity` 即可直接运行，无需联网。
> 如果报错 Python ABI 不匹配（如 `cp312` vs `cp313`），需要在联网机器上用 `-PythonVersion` 参数重新缓存：
> ```powershell
> .\unity-mcp-setup\tools\cache-unity-mcp-bridge.ps1 -PythonVersion 3.13 -Clean
> ```

**B5. 配置 AI 客户端 MCP（自动检测）**

LLM 应检测系统中已安装的 AI IDE/CLI，然后针对每个工具配置对应的 MCP 设置：

**检测逻辑**：

| 工具 | 检测方法 | 配置文件路径 |
|------|---------|-------------|
| **Claude Desktop** | 检查 `%APPDATA%\Claude\claude_desktop_config.json` 是否存在 | `%APPDATA%\Claude\claude_desktop_config.json` |
| **Cursor** | 检查 `%USERPROFILE%\.cursor\mcp.json` 是否存在 | `%USERPROFILE%\.cursor\mcp.json` |
| **Roo Code** | 检查 `%APPDATA%\Code\User\globalStorage\rooveterinaryinc.roo-cline\settings\mcp_settings.json` 是否存在 | `%APPDATA%\Code\User\globalStorage\rooveterinaryinc.roo-cline\settings\mcp_settings.json` |
| **OpenCode** | 检查 `opencode` CLI 命令是否可用，或 `%USERPROFILE%\.config\opencode\opencode.json` 是否存在 | `%USERPROFILE%\.config\opencode\opencode.json`（全局） |
| **Claude Code** | 检查 `claude` CLI 命令是否可用 | CLI 命令或 `~/.claude/settings.json` |

**配置模板**（LLM 根据检测结果自动写入）：

<details>
<summary>Claude Desktop / Cursor</summary>

```json
{
  "mcpServers": {
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
}
```

> ⚠️ 如果 `uvx` 不在系统 PATH 中，需要将 `"command"` 改为完整路径，例如 `"C:/Users/你的用户名/.local/bin/uvx.exe"`。
</details>

<details>
<summary>OpenCode</summary>

推荐使用项目自带的自动配置脚本（PowerShell）：

```powershell
cd unity-mcp-setup\tools
.\configure-opencode-mcp.ps1
```

该脚本会自动检测并安全合并配置到 `%USERPROFILE%\.config\opencode\opencode.json`，保留已有的其他 MCP 设置。

如需移除这两个 MCP 条目：

```powershell
.\configure-opencode-mcp.ps1 -Remove
```

> 全局配置文件路径：`%USERPROFILE%\.config\opencode\opencode.json`（Linux/macOS: `~/.config/opencode/opencode.json`）
> 也可放在项目根目录的 `opencode.json` 中（项目级）。
> **注意**：OpenCode 的 MCP schema 与 Cursor/Claude Desktop 不同，`local` 类型的 `command` 必须是数组格式。手动编辑容易出错，建议优先使用上面的脚本。
</details>

<details>
<summary>Roo Code</summary>

```json
{
  "mcpServers": {
    "unityMCP": {
      "command": "uvx",
      "args": ["--from", "mcpforunityserver", "mcp-for-unity", "--transport", "stdio"],
      "disabled": false
    },
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
> ⚠️ 如果 `uvx` 不在系统 PATH 中，需要将 `"command"` 改为完整路径。
</details>

<details>
<summary>Claude Code（CLI）</summary>

```bash
claude mcp add unityMCP -- uvx --from mcpforunityserver mcp-for-unity --transport stdio
claude mcp add ragmem --transport stdio --command wsl --args "-d,Ubuntu-24.04,--,bash,-c,source ~/.local/bin/env 2>/dev/null; MEM0_URL=http://localhost:18910 LIGHTRAG_URL=http://localhost:18920 uvx --from ~/ragmem/mcp-server ragmem-mcp-server"
```
</details>

**LLM 执行流程**：
1. 依次检测上述工具的配置文件/命令是否存在
2. 对于检测到的每个工具，读取现有配置（如果存在）
3. 合并新的 `unityMCP` 和 `ragmem` 配置（保留现有其他 MCP servers）
4. 写回配置文件
5. 提示用户重启对应的 IDE/CLI 使配置生效

> **注意**：如果检测到多个工具，LLM 应配置所有检测到的工具，而不是只配置一个。

**B6. 端到端验证**

LLM 依次调用以下 MCP 工具验证全链路：

1. `ragmem_health` → 确认 mem0 + LightRAG 均可达
2. `memory_add` → 写入测试记忆 → `memory_search` → 查回验证
3. `manage_scene`（get_hierarchy）→ 确认 Unity MCP 连通

> 全部通过后，部署完成。LLM 应告知用户部署结果和可用的工具列表。

### LLM 自动部署流程图

```
联网机器（LLM 阶段 A）            沙盘机器（LLM 阶段 B）
──────────────────               ──────────────────
prepare-images.bat ──┐
package-unity-mcp.ps1 ┤
cache-unity-mcp-bridge.ps1 ┤
                      │  LLM 停止，提示用户操作
                      ├── 用户手动拷贝整个项目目录 ──→ 沙盘任意目录
                      │   用户在沙盘内打开项目，调用 LLM
                      │
                      │           setup-environment.bat (配置 .env)
                      │           deploy.bat (一键部署后端)
                      │           install-unity-mcp.ps1 -Local (安装 Unity MCP 编辑器插件)
                      │           uv tool install ... --offline (安装 Python Bridge)
                      │           检测 IDE/CLI → 自动配置 MCP
                      │           ragmem_health / manage_scene (验证)
                      │
                      └───────── ✅ 部署完成
```

---

## 手动部署

> 以下是手动逐步部署的详细说明，适合需要精细控制每个步骤的场景。

### 前置条件

- Windows 10/11，已启用 WSL2
- Docker Desktop 或 WSL2 内 Docker Engine
- Unity 2021.3+（已打开目标项目）
- PowerShell 5.1+

### 端口表

| 服务 | 端口 | 说明 |
|------|------|------|
| Unity MCP Socket | 6400 | Unity 编辑器内部通信（自动，无需配置） |
| mem0 | 18910 | 记忆存储 API |
| LightRAG | 18920 | 知识库 RAG API |
| pgvector | 18930 | PostgreSQL 向量数据库（内部） |

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

### 三、安装 Unity MCP

**在线：**

```powershell
.\unity-mcp-setup\tools\install-unity-mcp.ps1 -ProjectPath "D:\你的Unity项目"
```

**离线（沙箱）：**

```powershell
# 联网机器上先打包
.\unity-mcp-setup\tools\package-unity-mcp.ps1

# 沙箱机器上安装
.\unity-mcp-setup\tools\install-unity-mcp.ps1 -Local -ProjectPath "D:\你的Unity项目"
```

安装后 Unity MCP 会自动运行，无需手动启动服务。

### 四、配置 AI 客户端

根据你使用的 AI 客户端，将 `unityMCP` 和 `ragmem` 两个 MCP server 配置写入对应的配置文件。各客户端的配置文件路径和 JSON 格式详见上方 **B5. 配置 AI 客户端 MCP（自动检测）** 中的检测表和配置模板。

**OpenCode 示例**（自动配置脚本）：

```powershell
cd unity-mcp-setup\tools
.\configure-opencode-mcp.ps1
```

> 该脚本会自动将 `unityMCP`（local stdio）和 `ragmem`（local stdio）注册到 `%USERPROFILE%\.config\opencode\opencode.json`，并保留已有配置。

> 如果你使用的 AI 终端不在上述列表中，请参考 **MCP-MANUAL-CONNECT.md** 手动配置。

### 五、验证全链路

1. AI 客户端中调用 `ragmem_health` → 应返回 mem0 + LightRAG 状态
2. 调用 `memory_add` 写入一条记忆 → 调用 `memory_search` 查回
3. 调用任意 Unity MCP 工具（如 `manage_scene` get_hierarchy）→ 应返回场景数据

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
