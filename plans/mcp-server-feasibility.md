# AgentCore as MCP Server — 可行性方案

> 状态：方案评估 / 待用户确认进入 ROADMAP
> 创建日期：2026-06-15
> 关联：[`AGENTS.md`](../AGENTS.md:1) §3.4 Optional Components 模式

---

## 0. TL;DR

**结论：完全可行，难度中等，强烈推荐做。**

把 AgentCore 的 Tool 能力从 ChatWindow 解耦，通过 MCP 协议暴露为标准服务层，让外部 IDE / CLI / Agent Chat Platform（Cursor / Cline / Claude Desktop / Continue / Roo Code 等）都能调用 AgentCore 的 Unity 工具能力。

- **协议**：使用 [Model Context Protocol (MCP)](https://modelcontextprotocol.io/)，事实标准，主流 LLM 客户端已原生支持
- **形态**：双形态共存 — 内嵌 ChatWindow（现有）+ MCP Server（新增可选组件）
- **改造**：核心架构不变，[`ToolRegistry`](../Editor/Tools/ToolRegistry.cs:1) 与 [`ToolCallDispatcher`](../Editor/Tools/ToolCallDispatcher.cs:1) 共享，零重复代码
- **MVP 工作量**：约 8-10 个工作日
- **完整版（含 Resources/Prompts/SSE）**：约 3-4 周

---

## 1. 背景与目标

### 1.1 用户诉求

让外部 IDE / CLI / Agent Chat Platform 能够通用调用 AgentCore 的 Unity 编辑能力，**不破坏用户的惯用工具**。AgentCore 同时具备两种身份：

1. **Chat Platform**：作为 Unity 内嵌的对话式 Agent（现有 [`ChatWindow`](../Editor/UI/ChatWindow.cs:1)）
2. **Unity LLM Tools Core**：作为底层工具服务层，被外部 LLM 客户端消费

### 1.2 核心判断

这正是 [MCP](https://modelcontextprotocol.io/) 解决的问题。Anthropic 在 2024-11 开源 MCP，目前 Claude Desktop / Cursor / Cline / Roo Code / Continue / Zed 都已原生支持，已成事实标准。

**不要自己发明协议**。直接采用 MCP 即可获得现成生态。

### 1.3 AgentCore 现有架构的天然优势

AgentCore 的工具系统几乎与 MCP 是 1:1 映射：

| AgentCore 现有 | MCP 概念 | 映射成本 |
|---------------|---------|---------|
| `[AgentTool]` 特性 + [`IAgentTool`](../Editor/Tools/Native.meta:1) 接口 | MCP Tool 定义 | 零 |
| [`ToolRegistry`](../Editor/Tools/ToolRegistry.cs:1) 自动发现 | Tool 列表枚举 | 零 |
| [`ToolCallDispatcher`](../Editor/Tools/ToolCallDispatcher.cs:1) | Tool 调用分发 | 零 |
| [`ToolDefinitionBuilder`](../Editor/Tools/ToolDefinitionBuilder.cs:1) JSON Schema | MCP `inputSchema` | 复用 |
| `RequiresMainThread` 主线程调度 | （MCP 无概念，由实现决定） | 复用 |
| [`WorkspacePathPolicy`](../Editor/Workspace/Safety/WorkspacePathPolicy.cs:1) 安全策略 | （MCP 无概念，由实现决定） | 复用 |

**结论**：MCP 适配层只是把 ToolRegistry 的现有能力换一个协议外壳，核心业务逻辑零改动。

---

## 2. MCP 协议要点速览

> 完整规范：https://spec.modelcontextprotocol.io/

### 2.1 通信协议

- **JSON-RPC 2.0** 编解码
- 三种 Transport：
  - **stdio**：子进程模型（适合一次性 CLI，**不适合 Unity 长驻进程**）
  - **HTTP + SSE**：HTTP POST 请求 + Server-Sent Events 流式响应（MCP 2024-11 版本主推）
  - **Streamable HTTP**：单一 HTTP 端点，支持流式 + 非流式（MCP 2025-03 新版，未来主流）

### 2.2 核心 Methods

| Method | 用途 | 必需性 |
|--------|------|--------|
| `initialize` | 协商协议版本与能力 | 必需 |
| `tools/list` | 列出可用工具 | MVP 必需 |
| `tools/call` | 调用工具 | MVP 必需 |
| `resources/list` + `resources/read` | 资源（文件/Asset）暴露 | 推荐 |
| `prompts/list` + `prompts/get` | Prompt 模板暴露 | 推荐 |
| `sampling/createMessage` | 反向调用客户端 LLM | 高级 |
| `notifications/tools/list_changed` | 工具列表变更通知 | 推荐（Domain Reload 必需） |
| `notifications/cancelled` | 取消正在执行的请求 | 推荐 |

### 2.3 AgentCore 选型

- **Transport：HTTP + SSE 主用，Streamable HTTP 优先实现**
  - Unity 是长驻进程，stdio 子进程模型不匹配
  - HTTP 让外部工具配置极简：`{ "url": "http://localhost:7321/mcp" }`
  - 支持多客户端同时连接（一个 Unity 同时被 Cursor + Claude Desktop 用）
  - Domain Reload 时连接可自动重连，外部工具无需重启

- **协议版本**：实现 2025-03 版（Streamable HTTP），向下兼容 2024-11 版（HTTP+SSE）

---

## 3. 架构设计

### 3.1 整体架构

```
┌──────────────────────────────────────────────────────────────┐
│                    Unity Editor 进程                          │
│                                                              │
│  ┌────────────────────────────────────────────────────────┐  │
│  │            AgentCore.Editor (主程序集，现有)             │  │
│  │                                                        │  │
│  │   ┌──────────────┐    ┌──────────────┐                 │  │
│  │   │ ChatWindow   │    │  AgentLoop   │                 │  │
│  │   └──────┬───────┘    └──────┬───────┘                 │  │
│  │          └────────────────────┤                        │  │
│  │                               ▼                        │  │
│  │   ┌──────────────────────────────────────────────────┐ │  │
│  │   │  ToolRegistry / ToolCallDispatcher (共享核心)    │ │  │
│  │   │  + WorkspaceContextService                       │ │  │
│  │   │  + WorkspacePathPolicy / OperationRisk           │ │  │
│  │   │  + ToolDefinitionBuilder                         │ │  │
│  │   └──────────────────────┬───────────────────────────┘ │  │
│  └──────────────────────────┼─────────────────────────────┘  │
│                             │                                │
│  ┌──────────────────────────▼─────────────────────────────┐  │
│  │       AgentCore.MCP.Editor (新增可选组件)               │  │
│  │       define: AGENTCORE_MCP                            │  │
│  │                                                        │  │
│  │   ┌──────────────────────────────────────────────────┐ │  │
│  │   │  McpServer                                       │ │  │
│  │   │  ├─ HttpListener (绑定 127.0.0.1:7321)            │ │  │
│  │   │  ├─ JSON-RPC 2.0 编解码                          │ │  │
│  │   │  ├─ McpProtocolHandler                           │ │  │
│  │   │  │   ├─ initialize                               │ │  │
│  │   │  │   ├─ tools/list   ← 桥接 ToolRegistry         │ │  │
│  │   │  │   ├─ tools/call   ← 桥接 ToolCallDispatcher    │ │  │
│  │   │  │   ├─ resources/*  ← 包装 Workspace/Asset       │ │  │
│  │   │  │   └─ prompts/*    ← 包装 Bootstrap 各层        │ │  │
│  │   │  └─ McpStreamingResponse (SSE/Streamable HTTP)   │ │  │
│  │   └──────────────────────────────────────────────────┘ │  │
│  │                                                        │  │
│  │   ┌──────────────────────────────────────────────────┐ │  │
│  │   │  McpToolFilter / McpAuditLog                     │ │  │
│  │   │  ├─ 工具白名单（独立于内嵌 Chat 的暴露范围）       │ │  │
│  │   │  ├─ 风险分级（默认仅 Low-Risk）                  │ │  │
│  │   │  └─ 调用审计（来源、时间、参数）                  │ │  │
│  │   └──────────────────────────────────────────────────┘ │  │
│  │                                                        │  │
│  │   ┌──────────────────────────────────────────────────┐ │  │
│  │   │  McpSettingsContribution (UI 贡献)                │ │  │
│  │   │  ├─ 开关 / 端口 / Bind Host                       │ │  │
│  │   │  ├─ 工具白名单 UI                                  │ │  │
│  │   │  ├─ 一键复制 IDE 配置 JSON（多模板）               │ │  │
│  │   │  └─ 状态指示器 / 客户端列表                        │ │  │
│  │   └──────────────────────────────────────────────────┘ │  │
│  └────────────────────────────────────────────────────────┘  │
└────────────────────────────┬─────────────────────────────────┘
                             │ HTTP / SSE on 127.0.0.1:7321
            ┌────────┬───────┼────────┬─────────────┐
            ▼        ▼       ▼        ▼             ▼
         Cursor   Cline   Claude   Continue    自定义 CLI
                          Desktop                Agent
```

### 3.2 模块职责

| 模块 | 职责 | 文件位置 |
|------|------|----------|
| `McpServer` | HttpListener 生命周期 / 端口绑定 / 启停 | `Editor/MCP/Server/McpServer.cs` |
| `McpProtocolHandler` | JSON-RPC 路由分发 / 协议握手 | `Editor/MCP/Protocol/McpProtocolHandler.cs` |
| `McpToolBridge` | ToolRegistry → MCP Schema / ToolCallDispatcher 桥接 | `Editor/MCP/Bridges/McpToolBridge.cs` |
| `McpResourceBridge` | Workspace/Asset/Scene → MCP Resource | `Editor/MCP/Bridges/McpResourceBridge.cs` |
| `McpPromptBridge` | SOUL/TOOLS/PROJECT → MCP Prompt | `Editor/MCP/Bridges/McpPromptBridge.cs` |
| `McpSettingsContribution` | Settings 页 UI | `Editor/MCP/UI/McpSettingsContribution.cs` |
| `McpAuditLog` | 调用审计 | `Editor/MCP/Audit/McpAuditLog.cs` |

### 3.3 关键设计原则

1. **独立可选组件**（遵循 [`AGENTS.md`](../AGENTS.md:1) §3.4）
   - 独立 asmdef `AgentCore.MCP.Editor`，引用 `AgentCore.Editor`
   - scripting define `AGENTCORE_MCP` 控制编译
   - 通过 `IAgentCorePanelContribution` 在 Hub 暴露状态卡片
   - 通过 `IAgentCoreSettingsContribution` 暴露配置 UI
   - **默认禁用**，用户主动启用

2. **共享核心，零重复**
   - 直接读取 `ToolRegistry.Instance` 已注册工具
   - 工具调用桥接到 [`ToolCallDispatcher.ExecuteAsync`](../Editor/Tools/ToolCallDispatcher.cs:1)
   - 不重写工具，不维护第二份注册表

3. **独立工具白名单**
   - MCP 暴露的工具集 ≠ 内嵌 Chat 暴露的工具集（安全边界不同）
   - 默认只暴露 Low-Risk 工具（read-only / Console / Workspace 信息查询）
   - 写工具（`apply_diff` / `write_to_file` / Scene 修改）需用户在 Settings 显式启用
   - 风险分级复用 [`WorkspaceOperationRisk`](../Editor/Workspace/Safety/WorkspaceOperationRisk.cs:1)

4. **审计先行**
   - 所有 MCP 来源的工具调用记录到 `McpAuditLog`
   - 字段：时间戳 / 客户端标识 / 工具名 / 参数摘要 / 结果状态 / 耗时
   - 用户可在 Settings 页查看、清空、导出

---

## 4. 实施路径（分 Phase）

### 4.1 Phase 1 — MVP（约 8-10 工作日）

**目标**：让 Cursor / Cline / Claude Desktop 能列出并调用 AgentCore 的 Native 工具。

**交付清单**：

- 新增 `Editor/MCP/` 目录与 `AgentCore.MCP.Editor.asmdef`
- scripting define `AGENTCORE_MCP` 注册到 `OptionalComponentManager`
- `McpServer` — 基于 `HttpListener`，绑定 `127.0.0.1:7321`
- `McpProtocolHandler` 实现三个核心方法：
  - `initialize`（协议握手 / 能力声明）
  - `tools/list`（从 `ToolRegistry.Instance` 转换为 MCP Schema）
  - `tools/call`（桥接到 `ToolCallDispatcher.ExecuteAsync`）
- 主线程调度：MCP 请求线程 → `EditorApplication.delayCall` 切回主线程（`RequiresMainThread=true` 时）
- `McpSettingsContribution`：开关 / 端口 / 工具白名单 / 一键复制 IDE 配置 JSON
- `McpAuditLog` 最小实现（环形缓冲 + UI 列表）

**验收标准**：

- [ ] 在 Cursor 配置 `mcp.json`：`{ "mcpServers": { "agentcore": { "url": "http://localhost:7321/mcp" } } }`
- [ ] Cursor 重启后，`tools/list` 能列出 AgentCore 所有 Native 工具
- [ ] 在 Cursor 中说"读取 Console 错误"，能成功调用 AgentCore 工具并返回结果
- [ ] 端口被占用时 Settings 给出清晰错误提示，不让 Unity 崩溃
- [ ] Domain Reload 后 Server 自动重启，外部客户端自动重连
- [ ] 关闭组件后端口立即释放，工具不再暴露

### 4.2 Phase 2 — 完整能力（约 1-2 周）

**目标**：达到与官方 MCP server 同等的能力广度。

**交付清单**：

- `resources/list` + `resources/read`：暴露 Scene/Prefab/Asset 元信息
  - URI 设计：`unity://scene/{path}`、`unity://asset/{guid}`、`unity://workspace/{root}/{relative}`
  - 复用 [`WorkspacePathPolicy`](../Editor/Workspace/Safety/WorkspacePathPolicy.cs:1) 校验访问范围
- `prompts/list` + `prompts/get`：暴露 SOUL/TOOLS/PROJECT 作为可引用 prompt template
  - 让外部 IDE 可以把"AgentCore 视角"注入到自己的对话中
- SSE / Streamable HTTP：长工具调用进度推送（如批量索引、长 LLM 调用）
- 错误码标准化（MCP error codes：`-32600` Invalid Request / `-32601` Method Not Found / 自定义业务错误码 +32000~+32099）
- Domain Reload 优雅处理：
  - reload 前发送 `notifications/cancelled` 给所有正在执行的请求
  - reload 前主动 `Close()` HttpListener
  - reload 后 `[InitializeOnLoadMethod]` 自动重启
- 客户端连接管理：列出当前已连接的客户端、强制断开、按客户端粒度审计

**验收标准**：

- [ ] Cursor 中可读取 Scene 列表与某 Scene 的 GameObject 树
- [ ] Cursor 中可引用 SOUL.md 内容作为 system prompt
- [ ] 调用一个慢工具（如全量索引），SSE 持续推送进度直到完成
- [ ] 改一个脚本触发 Domain Reload，Cursor 不报错，重连后能继续工作

### 4.3 Phase 3 — 高级能力（按需，约 1-2 周）

| 特性 | 说明 | 优先级 |
|------|------|--------|
| **Sampling** | 反向调用 AgentCore 配置的 LLM，外部 IDE 共享同一套 API Key | 中 |
| **Workspace Roots** | 主动通告 Unity 项目根、VCS 根、Package 根 | 中 |
| **Auth Token** | localhost 通常不需要，但局域网共享场景必需 | 低（仅企业版） |
| **TLS / mTLS** | 局域网或反向代理场景 | 低 |
| **多 workspace 隔离** | 一台机器多个 Unity 实例，端口与名字空间隔离 | 中 |
| **OpenTelemetry 集成** | 工具调用 trace / metrics 接入企业可观测体系 | 低（企业版） |

---

## 5. 兼容项与工作量评估

### 5.1 必做（MVP 核心）

| 项目 | 工作量 | 难度 | 备注 |
|------|--------|------|------|
| HTTP/JSON-RPC 服务器 | 2-3 天 | 低 | `HttpListener` + `Newtonsoft.Json`，零新依赖 |
| MCP 协议适配 | 2-3 天 | 低 | 协议简单，schema 已有 |
| 工具 Schema 转换 | 1 天 | 极低 | 复用 [`ToolDefinitionBuilder`](../Editor/Tools/ToolDefinitionBuilder.cs:1) |
| 主线程调度桥接 | 1-2 天 | 中 | 复用现有 `RequiresMainThread` 机制 |
| Settings UI + 配置导出 | 1 天 | 低 | 遵循 [`AGENTS.md`](../AGENTS.md:1) §10.1 settings section 规范 |
| 启停生命周期 / Domain Reload | 1 天 | 中 | `EditorApplication.quitting` + `[InitializeOnLoad]` |

**MVP 总工作量：8-10 个工作日**

### 5.2 推荐（体验提升）

| 项目 | 工作量 | 难度 |
|------|--------|------|
| SSE / Streamable HTTP | 2 天 | 中 |
| Resources 抽象（Scene/Asset URI） | 2 天 | 中 |
| Prompts 抽象（SOUL/TOOLS/PROJECT） | 1 天 | 低 |
| 多 IDE 配置模板（Cursor/Cline/Claude Desktop） | 1 天 | 低 |
| 端口冲突自动检测 + 候选端口 | 0.5 天 | 极低 |

### 5.3 可选（高级）

| 项目 | 工作量 | 难度 |
|------|--------|------|
| Sampling 反向 LLM 调用 | 3-5 天 | 高 |
| Auth Token | 1 天 | 低 |
| TLS | 2 天 | 中 |
| 多 workspace 隔离 | 2-3 天 | 中 |

### 5.4 对外兼容性矩阵

| 客户端 | 协议要求 | AgentCore MVP 是否兼容 |
|--------|----------|----------------------|
| Cursor | HTTP + SSE / Streamable HTTP | ✅ MVP 即兼容 |
| Cline / Roo Code | HTTP + SSE | ✅ MVP 即兼容 |
| Claude Desktop | stdio（默认） | ⚠️ 需用户配置 stdio→HTTP 桥接，或我们提供桥接 CLI |
| Continue | HTTP + SSE | ✅ MVP 即兼容 |
| Zed | stdio | ⚠️ 同上 |
| 自定义 CLI / Agent | 任意 | ✅ HTTP 端点完全开放 |

**对 stdio-only 客户端的应对**：
- Phase 2 提供一个独立的 Node.js / Python CLI（< 100 行）作为 stdio→HTTP 适配器
- 用户在 Claude Desktop 配置：`"command": "npx", "args": ["@agentcore/mcp-bridge", "http://localhost:7321/mcp"]`

---

## 6. 核心难点与风险

### 6.1 难点 1：主线程同步

**问题**：MCP 请求来自 `HttpListener` 后台线程，但 Unity API（`Selection`、`AssetDatabase`、Scene 操作）必须在主线程。

**应对**：
- 复用现有 `[AgentTool(RequiresMainThread = true)]` 机制
- [`ToolCallDispatcher`](../Editor/Tools/ToolCallDispatcher.cs:1) 已经处理过线程切换，MCP 入口只需调用 dispatcher，**不要绕过**
- 不要在 MCP 路由层直接访问 Unity API，所有 Unity 状态访问都通过工具

**风险**：低。已有方案直接复用。

### 6.2 难点 2：Domain Reload

**问题**：用户改脚本 → Unity 重编 → 静态状态全丢 → 外部 IDE 的 SSE 长连接断开 → 客户端可能阻塞或报错。

**应对**：
- 借鉴 [`AgentLoop.DomainReload.cs`](../Editor/Core/AgentLoop.DomainReload.cs:1) 模式
- `AssemblyReloadEvents.beforeAssemblyReload`：
  - 发送 `notifications/cancelled` 给所有进行中的请求
  - 主动 `Close()` HttpListener，让客户端立刻收到 EOF（比超时友好）
- `[InitializeOnLoadMethod]`：reload 完成后自动重启 Server
- Cursor / Cline / Claude Desktop 都有自动重连机制，体验无缝
- Settings 提供"Domain Reload 容忍度"开关：默认开启自动重启

**风险**：中。需要在 Phase 1 就把 Domain Reload 流程跑通。

### 6.3 难点 3：工具与 Workspace 安全

**问题**：外部 IDE 调用工具时，没有 ChatWindow 的人工确认环节，写操作直接落盘。如果用户的 Cursor 被某个恶意 prompt 注入，可能导致数据丢失。

**应对（多层防御）**：

1. **风险分级**（最重要）
   - 复用 [`WorkspaceOperationRisk`](../Editor/Workspace/Safety/WorkspaceOperationRisk.cs:1)
   - MCP 默认只暴露 `Low` 级别工具（read-only / Console / Workspace 信息查询）
   - `Medium` / `High` / `Critical` 工具需用户在 Settings 显式打勾启用，并在 UI 上加风险提示

2. **路径白名单**
   - 复用 [`WorkspacePathPolicy`](../Editor/Workspace/Safety/WorkspacePathPolicy.cs:1)
   - 写操作严格限制在 `Assets/` / 用户配置的允许范围
   - 拒绝写入 `ProjectSettings/` / `Packages/` / 项目根之外的路径

3. **审计日志**
   - 所有 MCP 调用必记录（即使失败）
   - UI 实时可见，用户可一键查看"过去 5 分钟谁调用了什么"

4. **会话级配额**（推荐）
   - 同一客户端 1 分钟内最多 N 次写操作
   - 超过阈值自动暂停，需用户在 UI 解除

5. **本地绑定**
   - 默认 `127.0.0.1`，不暴露到局域网
   - 局域网共享需在 Settings 显式切换并设置 Auth Token

**风险**：高（如果不做防御）/ 中（按上述方案做了之后）。**这是 MVP 必须包含的安全基线，不能延后到 Phase 2**。

### 6.4 难点 4：双形态状态污染

**问题**：内嵌 Chat 和 MCP 共享同一个 ToolRegistry，会不会互相干扰？例如：
- ChatWindow 调用了 `read_console`，MCP 也调用 `read_console`，两边读到的是同一份还是不同份？
- [`FileChangeTracker`](../Editor/Core/FileChangeTracker.cs:1) 的快照会不会被搅乱？

**应对**：
- ToolRegistry 是无状态注册中心（确认 [`ToolRegistry.cs`](../Editor/Tools/ToolRegistry.cs:1) 实现）
- 工具实例本身在每次调用时创建独立上下文（参数、Stopwatch、ToolResponse）
- [`ConsoleErrorCapture`](../Editor/Core/ConsoleErrorCapture.cs:1) / [`FileChangeTracker`](../Editor/Core/FileChangeTracker.cs:1) 是全局快照，但语义本来就是"读取最新状态"，不存在污染
- **真正需要注意的是 [`AgentLoop`](../Editor/Core/AgentLoop.cs:1) 内部的对话历史 / [`ContextWindowManager`](../Editor/Core/ContextWindowManager.cs:1)** — 这些是 ChatWindow 私有状态，MCP 不应触达
- MCP 桥接层应**只调用 ToolRegistry/ToolCallDispatcher**，不触达 AgentLoop / SessionManager / ContextWindowManager

**风险**：低。架构边界清晰即可避免。

### 6.5 难点 5：工具命名空间冲突

**问题**：外部 IDE 可能同时连接多个 MCP server（AgentCore + filesystem + git + ...），如果工具名重名（如 `read_file`），会冲突。

**应对**：
- MCP 客户端通常会自动加 server 前缀（如 `agentcore.read_file`）
- AgentCore 侧不需要做特殊处理
- 但 AgentCore 工具命名建议带 Unity 语义前缀（如 `unity_read_console` 而非 `read_console`）以减少认知歧义

**风险**：极低。

---

## 7. 生态对比与依据

| 项目 | 类型 | 启示 |
|------|------|------|
| 多个社区 `unity-mcp-server` 实现（GitHub） | Unity → MCP | 证明可行，但实现质量参差，多数仅暴露少量基础工具 |
| Blender MCP（社区项目） | Blender → MCP | 同形态 DCC 工具 + AI 集成成熟先例 |
| 官方 `mcp-server-filesystem` / `mcp-server-git` | Node.js 实现 | 协议参考实现，可对照 |
| Claude Desktop / Cursor / Cline / Roo Code | MCP 客户端 | 用户群已就位，无需教育市场 |

**AgentCore 相对社区方案的差异化优势**：
- 现有 `[AgentTool]` + `ToolAutoDiscovery` 比社区方案更成熟
- 已有 [`WorkspaceContextService`](../Editor/Workspace/WorkspaceContextService.cs:1) / [`WorkspacePathPolicy`](../Editor/Workspace/Safety/WorkspacePathPolicy.cs:1) 安全基础设施
- 已有 Compression / Indexing / Memory 等长链路能力
- Domain Reload 优雅恢复（社区方案普遍未解决）
- 双形态共存（内嵌 Chat + MCP），用户可按场景选择

---

## 8. 与现有规范的对齐

### 8.1 遵循 [`AGENTS.md`](../AGENTS.md:1) §3.4 Optional Components 模式

- ✅ 独立 Editor asmdef `AgentCore.MCP.Editor`
- ✅ scripting define `AGENTCORE_MCP` 控制编译
- ✅ 主程序集不强引用组件类型
- ✅ 通过 `IAgentCorePanelContribution` 在 Hub 暴露
- ✅ 通过 `IAgentCoreSettingsContribution` 暴露 Settings
- ✅ 工具仍使用 `[AgentTool]` + `IAgentTool`，由 `ToolAutoDiscovery` 注册
- ✅ 禁用组件后 ToolRegistry 重建，不残留

### 8.2 遵循 [`AGENTS.md`](../AGENTS.md:1) §10.1 Settings 页面规范

- ✅ MCP 设置归属为独立 `IAgentCoreSettingsSection`（`Id = "mcp"`）
- ✅ 复用 `AgentCoreSettingsUi` helper 构建 UI
- ✅ Enabled / Endpoint / Test Connection / Result / Advanced Options 一致结构
- ✅ 设置项变更走 `AgentCoreSettingsState`，不在 Provider 持有状态

### 8.3 遵循 [`AGENTS.md`](../AGENTS.md:1) §12 开发流程

- 本文档即"设计层"产物
- 用户 Review 确认后进入"代码实现"阶段
- 代码实现前必须先满足 [`llm-agent-architecture-remediation-plan.md`](llm-agent-architecture-remediation-plan.md:1) 的治理层 G.1/G.2/G.3：Tool Risk Policy + WorkspacePathPolicy 强制接入、ExecuteCodeTool 降权/拆分、Lazy Tool Discovery / ActiveToolScope
- MCP Phase 1（最小可用）完成 → 升级 Minor 版本（基线 v1.0.0，预计 → `v1.x.0`，与 Phase 7 在产品规划上并行；实现受治理层安全前置约束）
- 同步更新 `package.json` / `CHANGELOG.md` / `ROADMAP.md`（参见 AGENTS.md §12.5）

### 8.4 编码硬规则（参考 [`AGENTS.md`](../AGENTS.md:1) §7）

- 仅 Editor，无 Runtime 引用
- HTTP 调用使用 [`HttpClientFactory`](../Editor/Utils/HttpClientFactory.cs:1) 模式（虽然这里是 Server 端，但客户端测试连接复用）
- JSON 处理统一走 [`JsonHelper`](../Editor/Utils/JsonHelper.cs:1)
- 工具调用一律走 [`ToolCallDispatcher`](../Editor/Tools/ToolCallDispatcher.cs:1)，不绕过
- MCP `tools/list` 不能默认暴露全部内部工具，必须经 ActiveToolScope / Capability Scope 过滤
- MCP `tools/call` 必须复用统一 Tool Risk Policy、WorkspacePathPolicy、确认策略和 Operation Journal，不得在桥接层重新实现一套安全逻辑
- 错误处理：`HttpListener` 异常不能拖垮 Unity，需顶层 try-catch + 日志

---

## 9. 与 ROADMAP 的关系

已正式登记为独立 Phase，参见 [`plans/ROADMAP.md`](ROADMAP.md:1) §3.x：

- **Phase 名称**：`Phase 8 — MCP 对外互操作（对外）`
- **与 Phase 7 关系**：Phase 7（Plugin / 后台索引）= **对内扩展**；Phase 8（MCP）= **对外暴露**。两者在产品规划上并行；MCP 实现必须先满足治理层安全前置条件。详见 ADR-13 / ADR-14。
- **依赖**：当前的 Tools / Workspace / Optional Components 基础设施已就绪（v1.0.0 验收完成）；实现前还依赖治理层 G.1/G.2/G.3。
- **不依赖**：Indexing 后台增量、Plugin 系统、Enterprise Workflow（独立可单跑，但不得绕过治理层）
- **任务条目**：8.1.1 ~ 8.1.7（McpServerHost / McpToolBridge / 风险分级 / Settings UI / 多 IDE 配置 / 测试 / 文档），见 ROADMAP §3.x
- **触发条件**：用户对外部 IDE / CLI / Agent chat 平台调用 Unity 工具的需求达到稳定优先级时启动；不与 v1.0.0 验收阻塞

---

## 10. 决策建议与下一步

### 10.1 强烈推荐做的理由

1. **战略价值**：从"Unity 内嵌工具"升级为"Unity AI 基础设施"，让 AgentCore 的工具能力可以被任意 LLM 客户端复用，扩大用户基数
2. **不破坏现有体验**：可选组件，默认禁用，老用户无感知
3. **借势生态**：MCP 已成事实标准，搭车比自建协议成本低 1-2 个数量级
4. **架构契合度极高**：AgentCore 的工具抽象与 MCP 几乎 1:1 映射，工作量集中在协议外壳
5. **企业场景刚需**：企业内统一 Agent 平台 + Unity 项目是高频组合，MCP 是标准接入方式

### 10.2 可能的反对意见与回应

| 反对 | 回应 |
|------|------|
| "用户已经有 ChatWindow 了，为什么还要 MCP？" | 用户的 IDE 不是 Unity；让 AgentCore 嵌入用户的工作流而不是让用户来 Unity |
| "MCP 协议还在演进，现在做会不会过时？" | 2024-11 / 2025-03 两版协议已经稳定，主流客户端都已实现，不会推倒重来 |
| "安全风险？" | 默认仅 Low-Risk 工具 + 路径白名单 + 审计 + 本地绑定，比 ChatWindow 更严格 |
| "工作量会不会失控？" | MVP 8-10 天，按 Phase 推进，每个 Phase 独立交付价值 |
| "维护成本？" | 协议层稳定，工具桥接是机械映射，长期维护成本低 |

### 10.3 推荐的执行顺序

1. **第 1 步：用户 Review 本文档**，确认 MCP 仍作为 Phase 8 保留，但实现受治理层约束
2. **第 2 步：完成治理层前置设计/实现**，至少包括 Tool Risk Policy、WorkspacePathPolicy 强制接入、ExecuteCodeTool 降权/拆分、ActiveToolScope
3. **第 3 步：补充 MCP 设计文档**（用户确认大方向后），细化：
   - `McpProtocolHandler` 的 method 路由表
   - 工具风险分级表（哪些工具默认 Low / Medium / High）
   - MCP `tools/list` 的 Capability Scope 过滤规则
   - Settings UI 草图
   - 多 IDE 配置 JSON 模板
4. **第 4 步：Phase 1 实现**（8-10 天）
5. **第 5 步：用户测试**（4 轮，按 [`AGENTS.md`](../AGENTS.md:1) §12.6）
6. **第 6 步：版本号同步更新**（package.json / CHANGELOG / ROADMAP）
7. **第 7 步：评估是否进入 Phase 2**

### 10.4 如果暂不实施，至少应做的事

- 在 ROADMAP 中保留 Phase 8 条目（已登记于 §3.x，状态 [ ] 待启动）
- 不删除本文档，作为未来启动 Phase 8 时的设计基线
- 当前迭代避免引入与 MCP 设计冲突的工具命名（如不要新建 `read_file_v2` 之类与 MCP 标准工具名冲突的）
- Phase 7（Plugin）开发时保留对外接口隔离层，避免内部扩展 API 与未来 MCP 工具桥接代码耦合

---

## 11. 附录 A：参考资料

- MCP 官方规范：https://spec.modelcontextprotocol.io/
- MCP 官方 SDK（TypeScript）：https://github.com/modelcontextprotocol/typescript-sdk
- MCP 官方 SDK（Python）：https://github.com/modelcontextprotocol/python-sdk
- Cursor MCP 文档：https://docs.cursor.com/context/model-context-protocol
- Cline MCP 文档：https://github.com/cline/cline/wiki
- 社区 unity-mcp-server 实现合集（GitHub 搜索 "unity-mcp"）

## 12. 附录 B：术语对照

| 术语 | 含义 |
|------|------|
| MCP | Model Context Protocol，Anthropic 主导的 LLM 工具/资源交互标准 |
| stdio Transport | 通过子进程标准输入输出通信 |
| SSE | Server-Sent Events，HTTP 长连流式推送 |
| Streamable HTTP | MCP 2025-03 新版传输，单端点支持流式与非流式 |
| Sampling | MCP 客户端反向调用 server 暴露的 LLM 能力 |
| Workspace Roots | MCP server 通告自己关心的目录范围 |

---

> 维护原则：本文档是"是否做"与"怎么做"的设计基线。
> 实施时，每个 Phase 完成后回填实际工时与偏差，作为后续 Phase 估算的校正依据。
