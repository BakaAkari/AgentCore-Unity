# AgentCore MCP Server 设计

> 状态：**已定稿（2026-11-13）**，M1 待实施
> 取代：[`mcp-server-feasibility.md`](mcp-server-feasibility.md) 中的进程模型（编辑器内 HttpListener）与协议选型（HTTP+SSE）。可行性文档其余部分（工具映射、模块划分思路）仍可参考。
> 关联：[`ROADMAP.md`](ROADMAP.md) 主线第 3 项；`AGENTS.md` §3.4 可选组件模式

## 1. 目标与约束

- 让外部 MCP 客户端调用 AgentCore 的全部 Unity 能力。团队主要客户端为 **Claude Code 与 Codex**，M1 只验证这两个。
- 目标工程是 Unity 2022.3、没有任何现成 Unity MCP；所有 MCP 能力由 AgentCore 自己提供。
- 不另起一套安全策略：MCP 只是工具调用的另一个来源，走与内嵌 Chat 相同的 `ToolCallDispatcher`。
- 遵守现有原则：审查级别由用户选择、YOLO 不阻断；不打包二进制（ADR-17、P2 决定）；默认最优、少暴露选项。

## 2. 已定决策

| 决策 | 结论 | 理由 |
|------|------|------|
| 进程模型 | **relay（编辑器外）+ McpBridge（编辑器内）** | Domain Reload 会卸载编辑器内的一切，包括监听端口；relay 活过重载，能缓存、排队、挂起请求。Unity 官方已弃用编辑器内 MCP 服务改为外部 CLI，社区方案的协议端也都在编辑器外 |
| relay 运行方式 | **Unity 自带 Mono（`Editor/Data/MonoBleedingEdge/bin/mono.exe`）**；relay 源码随包分发，启用时用 Unity 自带 Roslyn（`Editor/Data/NetCoreRuntime/dotnet.exe` + `Editor/Data/DotNetSdkRoslyn/csc.dll`）编译到 `Library/AgentCore/Mcp/` | 包里不放二进制，团队不用装任何东西。已在 Unity 2022.3.50f1 上验证：Roslyn 以 Mono 4.7.1 引用集编译出的 stdio 程序能被自带 mono.exe 正常运行 |
| 协议层 | **自写 JSON-RPC + MCP 协议层，放在 relay** | 需要同时支持新旧两代协议且运行在 Mono 上；官方 C# SDK 依赖较新的运行库，不保证能在 Mono 上用 |
| 协议版本 | M1：2025-06-18、2025-11-25（`initialize` 握手）；M2：2026-07-28（无状态、`server/discover`、`_meta`、MRTR、`subscriptions/listen`） | 客户端升级节奏不一，过渡期两代并存 |
| 客户端传输 | M1：stdio（每个客户端会话由客户端拉起一个 relay）；M3：可选本机 Streamable HTTP | Claude Code、Codex 对 stdio 支持最好；stdio 连接不受编辑器重载影响 |
| 默认审查级别 | **跟随 AgentCore 当前级别**（与 Chat 共用信任状态，含 YOLO）；M2 加“信任客户端”选项 | 用户决定 |
| 确认位置 | M1：Unity 内确认卡片；M2：客户端支持 elicitation 时可改到客户端 | 先复用现有 UI |
| 反向（AgentCore 作为 MCP 客户端） | 搁置到 MCP Server M2 之后 | 先把服务端做稳 |

## 3. 架构

```
Claude Code / Codex ──stdio──> relay.exe (Unity Mono, 每会话一个)
                                   │  MCP 协议、列表缓存、排队、挂起
                                   │  127.0.0.1 TCP + 令牌，换行分隔 JSON 的内部 RPC
                                   ▼
                     Unity Editor: McpBridge (AgentCore.MCP.Editor, define AGENTCORE_MCP)
                                   │
                                   ▼
                     ToolCallDispatcher（现有：校验 / 策略 / 权限规则 / 检查点 / 主线程）
                                   │
                                   ▼
                     ToolRegistry 中的原生工具
```

### 3.1 编辑器侧：`AgentCore.MCP.Editor`

- 独立 asmdef，`defineConstraints: ["AGENTCORE_MCP"]`，引用 `AgentCore.Editor`；按 §3.4 在 Tools & Extensions 页以卡片形式启用，默认关闭。
- **McpBridge**：后台线程监听 `127.0.0.1`，端口与令牌首次生成后存入 `SessionState`，重载前后不变；编辑器重启时重新生成。
- **发现文件** `Library/AgentCore/Mcp/endpoint.json`：`{ port, token, pid, projectPath, unityVersion, bridgeProtocol, state }`，`state` 为 `ready` / `reloading`。写入用原子写；编辑器退出时删除。
- **调用执行**：每个 `tools/call` 构造一个 `ToolCall` 交给专用 `ToolCallDispatcher` 实例（注入 MCP 确认提供器）；结果映射为 MCP content。
- **列表**：`tools/list` 来自 `ToolRegistry`，排除内部元工具（`request_tools`、`ask_user`、会话 / 技能作用域类工具，清单见实现）；忽略 OnDemand 可见性，全部暴露；按名称排序（规范建议确定性顺序，利于客户端缓存）。

### 3.2 relay：`Editor/MCP/Relay~/`（源码目录，Unity 不导入）

- 目录以 `~` 结尾，Unity 不编译、不生成 `.meta`；随 UPM 包分发。
- 启用组件或包版本变化时，McpBridge 用自带 Roslyn 编译到 `Library/AgentCore/Mcp/relay-<源码哈希>.exe`；源码不变则复用。
- 启动参数：`--project <工程路径>`。按工程路径读发现文件，找到对应编辑器；多个编辑器同时打开时不会串到别的工程。
- 依赖：仅 Mono 4.7.1 引用集与 Newtonsoft.Json（引用包内已有的 dll）。

### 3.3 内部 RPC（relay ↔ McpBridge）

- 每行一个 JSON 对象。relay 首包为 `hello { token, relayVersion }`，令牌不符立即断开。
- 方法：`listTools`（返回列表与 schema 哈希）、`callTool { id, name, arguments, client }`、`cancel { id }`；bridge 推送：`progress { id, message }`、`result { id, ... }`、`reloading`、`ready { schemaHash }`。
- 内部 RPC 版本号写在发现文件里；relay 与 bridge 版本不一致时，relay 返回明确错误提示重新生成配置，不做兼容。

## 4. 中断与恢复

| 场景 | relay | McpBridge |
|------|-------|-----------|
| 调用触发编译 / Domain Reload | 请求保持挂起；有 `progressToken` 时发“Unity 正在编译” | `beforeAssemblyReload`：把执行中的调用（id、工具名、开始时间）写入 `SessionState`，发 `reloading`；重载后经 `CompilationWatcher` 取编译结果，作为该调用的结果推回 |
| 重载期间的新请求 | 列表类请求用缓存回答；`tools/call` 入队，恢复后按序转发；超过 120 秒（内部常量）返回 `isError` 结果“Unity 重载超时，可重试” | — |
| 重载后工具集合变化 | 比较 `schemaHash`，变化时发 `notifications/tools/list_changed` | 发 `ready { schemaHash }` |
| 编辑器未运行 / 崩溃 | 发现文件不存在或 PID 不在：列表用 `Library/AgentCore/Mcp/tools-cache.json` 回答；调用返回 `isError`“工程 X 的 Unity 编辑器未运行” | 每次 `listTools` 成功后由 relay 写缓存文件 |
| 编辑器失焦降频 | — | 收到调用后主动唤醒主线程派发（`EditorApplication.QueuePlayerLoopUpdate` 等）。**M1 先测量**真实工程中失焦时的派发延迟，再决定是否需要更多手段 |
| 主线程被模态框 / 导入阻塞 | 持续发进度 | 调用在主线程队列排队 |
| 客户端取消 | 未开始的出队；已开始的转发 `cancel` | 取消对应调用的 `CancellationToken` |
| 与内嵌 Chat 并发 | — | 写操作经同一主线程队列串行；Chat 窗口显示“外部客户端正在操作” |

规范依据：2026-07-28 起 Streamable HTTP 断流不再续传，客户端必须重发；stdio + relay 的组合让客户端连接本身不断。

## 5. 安全边界

| 层 | 做法 |
|----|------|
| 网络 | bridge 只绑 `127.0.0.1`，令牌认证；令牌只存在 `SessionState` 与工程 `Library/` 下的发现文件里。M3 HTTP 端点同样只绑本机，校验 `Origin`，要求令牌 |
| 策略 | 全部调用经 `ToolCallDispatcher`：参数校验、Play Mode 预检、`ToolRiskPolicy`、allow / ask / deny 规则、检查点 |
| 审查级别 | 跟随 AgentCore 当前信任状态（与 Chat 共用，含 YOLO）。现有信任状态保存在 `ChatWindow.Confirmation`，需先抽成独立服务，Chat 窗口关闭时也能读到 |
| 确认 | Unity 内确认卡片，标明来源客户端（`clientInfo.name`）；Chat 窗口未打开时自动打开 |
| 审计 | 每次调用写一条记录（时间、客户端、工具、参数摘要、结果状态、耗时）到 `Library/AgentCore/Mcp/audit.jsonl`；Chat 中的“外部会话”视图放到 M2 |
| 机密 | MCP 结果不额外暴露机密；`manage_prefs` 的机密隔离照旧 |

## 6. MCP 能力映射

| 能力 | 阶段 | 说明 |
|------|------|------|
| `tools/list`、`tools/call` | M1 | 结果：`ToolResult.Output` → text content；`IsPartial` 与失败 → `isError` 规则见实现；截图类工具的图片 → image content |
| `notifications/progress`、`notifications/cancelled` | M1 | |
| `notifications/tools/list_changed` | M1 | 仅旧协议；新协议的 `subscriptions/listen` 在 M2 |
| `ping` | M1 | 旧协议需要；2026-07-28 已移除 |
| 2026-07-28 无状态协议 | M2 | |
| Resources、Prompts（Skills） | M2 | |
| Elicitation 确认通道、“信任客户端”级别 | M2 | |
| Tasks 扩展、本机 Streamable HTTP | M3 | |
| Sampling、Roots、Logging | 不做 | 2026-07-28 已弃用 |

## 7. 设置与接入

- Tools & Extensions 页的 MCP 卡片：启用开关、状态（bridge 是否就绪、已连接 relay 数、最近调用）、“复制 Claude Code 命令”和“复制 Codex 配置”两个按钮。不暴露端口、超时等内部参数。
- Claude Code：`claude mcp add agentcore -- "<mono.exe>" "<relay.exe>" --project "<工程路径>"`。
- Codex：`~/.codex/config.toml` 中 `[mcp_servers.agentcore]`，`command` 为 mono.exe，`args` 为 relay 与工程路径。
- 生成的配置包含 Unity 编辑器路径；换 Unity 版本后需重新复制（卡片上检测到路径变化时提示）。

## 8. 验收（M1）

1. MCP Inspector 对 relay 做一致性检查通过（2025-06-18 与 2025-11-25）。
2. Claude Code、Codex 各在真实工程跑一轮任务：列出工具、读场景、改一个 prefab、写一个脚本（触发编译与 Domain Reload，调用返回编译结果）。
3. 编辑器未开时两个客户端都能启动并给出明确提示。
4. 审查级别为默认时写操作在 Unity 内弹确认；切到 YOLO 后不再弹。
5. 测得失焦状态下的主线程派发延迟并记录。

## 9. 风险

| 风险 | 应对 |
|------|------|
| 失焦降频导致调用慢 | M1 测量；不行再考虑 bridge 自带调度（`EditorApplication.update` 之外的唤醒手段） |
| 不同 Unity 版本自带 Mono / Roslyn 路径或引用集不同 | 编译与启动路径由 `EditorApplication.applicationContentsPath` 推导；在 2022.3 与 6000.x 各验证一次 |
| relay 编译失败 | 卡片显示编译错误；不影响内嵌 Chat |
| 客户端的 MCP 工具调用超时短于编译耗时 | 发进度通知；记录 Claude Code、Codex 的超时配置项写进接入说明 |
