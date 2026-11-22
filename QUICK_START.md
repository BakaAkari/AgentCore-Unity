# AgentCore Unity — 快速上手

在 Unity 编辑器里和 AI 直接对话，让它帮你搭场景、写代码、查问题、整理项目。默认连接企业内网本地部署模型；能访问该网络时可直接使用，否则先按第 6 节配置可达的模型服务。

**三步上手**

1. 通过 UPM 添加插件
2. 按 `Ctrl+Shift+Q`（macOS `Cmd+Shift+Q`）打开 AgentCore
3. 直接描述你的需求

---

## 1. 安装

需要 Unity 2021.3 LTS 及以上。

1. `Window > Package Manager` → 左上角 `+`
2. **Add package from tarball...**，选择 `com.agentcore.unity-<version>.tgz`（版本号见 [`package.json`](package.json)）
   或 **Add package from git URL...**，粘贴仓库地址（企业内网地址向项目 TA 或工具组同事索取）
3. 等待导入完成

装完后菜单多出 `Window > AgentCore`，默认连接企业内网模型；网络不可达时需要配置自己的 Provider Profile。

---

## 2. 基础使用

打开 `Window > AgentCore`（或 `Ctrl+Shift+Q`），像跟同事说话一样描述需求。它会自己理解意图、拆解步骤、分析上下文、动手执行；不确定时主动提问，高风险操作先跟你确认，编译出错自己读 Console 自己修。有额外约束顺嘴提一句就行：

> "帮我搭个角色控制器"
> "排查这个 Prefab 为什么报 Missing Reference"
> "扫一遍 Assets 目录看看有没有重复资源"

**会话级信任**：确认弹窗支持快捷放行——**Trust Low/Med**（只放行允许该信任范围、且不含写入/执行/网络等副作用能力的 ReadOnly/Low/Medium 请求；`ask` 规则仍需确认）、**YOLO (All)**（跳过确认及权限规则中的 ask/deny）。信任状态在同一编辑器内由 Chat 与 MCP 共用，跨脚本重载保留，重启编辑器清除。YOLO 不跳过参数校验、Play Mode 预检及工具自身约束；工具确认卡片 120 秒未处理会自动拒绝。

---

## 3. 常见场景

**搭原型**
> 在当前测试场景里搭一个可操作的第三人称角色原型，资源放 `Assets/Prototype/`，别动现有场景和输入设置。

**排查问题**
> 排查角色受击后动画不播放的问题。先只读诊断、给根因和方案，确认后再改。

**整理项目**
> 扫一遍 Assets，看有没有重复资源、孤立资源、超大资源。只出方案，别动文件。

**批量处理**
> 找 `Assets/Game/Prefabs/Enemies/` 下所有敌人 Prefab，统计组件配置。先出差异清单，再修复不合规范的，保留引用用 Undo，改完重新扫描跑测试。

四个例子的共同套路：**分析 → 操作 → 编译/测试 → 修正 → 交付摘要**。任务描述越具体（范围、禁区、验证方式），执行越准。

---

## 4. 常用快捷键

| 快捷键 | 功能 |
|--------|------|
| `Ctrl+Shift+Q` | 打开 AgentCore 窗口 |
| `Ctrl+Shift+X` | 全局上下文注入（选中物体/面板后按，自动采集上下文） |
| `Enter` | 发送消息 |
| `Ctrl+Enter` | 输入框内换行 |
| `Escape` | 取消当前任务 |
| `Ctrl+N` | 新建会话 |
| `Ctrl+Shift+E` | 导出当前会话 |

macOS 用 `Cmd` 代替 `Ctrl`。

---

## 5. 使用建议

- 复杂任务先要只读分析，确认方案后再授权修改。
- 有明确禁区（目录/文件/组件）直接说。
- 改动前确保有版本控制或备份点。
- 高风险操作核对确认面板再放行；不确定就用 Trust Low/Med，别直接 YOLO。
- 相似任务一次性交给它批量跑，别一条条发。
- 在 `UserSettings/AgentCore/PROJECT.md` 写项目约定（个人本地，不进版本库），Agent 会自动读取；该文件不存在时才只读回退旧位置（工程根目录 `PROJECT.md`、`AgentCore/PROJECT.md`），AgentCore 不会改写旧位置。`SOUL.ext.md` 按相同路径规则追加行为约定。当前不会自动把 `AGENTS.md` / `CLAUDE.md` 作为项目说明加载。

---

## 6. 高级设置：切换模型 Provider

默认使用企业内网本地模型，**以下内容仅在需要切换到其他供应商/模型时才需要看**。

打开 `Edit > Preferences > AgentCore`（macOS：`Unity > Settings > AgentCore`），进 **Model & Agent** 分页。配置单位是 **Provider Profile**：一个 profile 保存一套完整连接参数，可建多个在不同供应商间切换，当前生效的叫 Active Profile。

| 字段 | 说明 |
|------|------|
| Display Name | Profile 显示名 |
| Endpoint | 所选协议的 API 地址（支持 OpenAI 兼容 / 原生 Anthropic） |
| API Key | 点 **Set API Key** 弹窗输入，不明文显示，存本机不随项目同步 |
| Model | 下拉选择；点 **Refresh** 拉取模型列表；不支持列表接口时退化为文本框 |
| Override Temperature | 关闭用全局默认；打开后滑块调节 0.0–2.0 |
| Override MaxTokens | 关闭用全局默认；打开后手填单次最大输出 token |
| Override Reasoning | 关闭用全局默认；打开后可设 effort（low/medium/high）、reasoning MaxTokens、是否输出 reasoning |
| Extra Request Body | 追加到请求体的自定义 JSON，留空不追加 |

操作：**Add Profile** 新建 → 填好字段 → **Test Connection** 验证 → **Set as Active** 切换生效。**Duplicate** 可复制现有 profile 改动，不影响原 profile。

---

## 7. 在 Claude Code / Codex 里使用 AgentCore（MCP）

让终端里的 Claude Code 或 Codex 直接调用 AgentCore 的 Unity 工具（场景、预制体、脚本、SVN 等），不需要另装任何东西。

1. 打开 `Edit > Preferences > AgentCore > Tools & Extensions`，启用 **MCP Server**（即时运行时生效，不修改 `ProjectSettings/` 或脚本定义宏）。
2. 卡片状态显示 **Ready** 后：
   - **Claude Code**：点 **Copy Claude Code command**，在终端里粘贴执行一次。
   - **Codex**：点 **Copy Codex config**，粘贴到 `~/.codex/config.toml`（已有 `[mcp_servers.agentcore]` 时替换它）。
3. 重启 Claude Code / Codex。Unity 需要保持打开。

说明：

- **审查**：卡片的 Review level 默认“Follow AgentCore chat”——跟随 AgentCore Chat 当前的设置（包括 YOLO），权限规则对外部客户端同样生效；改为“Trust MCP clients”后外部调用绕过 Unity 的确认与 ask/deny 权限规则（是否在客户端询问取决于客户端配置，Unity 不保证它会询问）。
- **在哪确认**：Confirm in 默认是 Unity 的 AgentCore 窗口（卡片标明来自哪个客户端，窗口没开会自动打开）；改为“MCP client when supported”后，支持的客户端会在自己的界面里问你，不支持的仍回到 Unity。
- **资源与提示**：客户端还能读取工程上下文、场景层级、Console、版本控制状态和工程内文本文件（`unity://...`），以及把 AgentCore 的 Skills 当作 prompt 使用；订阅了的资源变化时会收到通知。
- **改脚本**：调用会等 Unity 编译完再返回，有编译错误时错误附在结果里；编译期间新的调用排队，Unity 恢复后自动执行。支持 Tasks 扩展的客户端会把超过 3 秒的调用转为可轮询的任务。
- **记录**：每次调用写进工程的 `Library/AgentCore/Mcp/audit.jsonl`；Chat 窗口底部显示外部客户端正在做什么，`Window > AgentCore External Activity` 可查看最近的调用和结果。
- **协议**：同时支持 2026-07-28 新协议与 2025 系列旧协议，客户端升级不用改配置。
- 换了 Unity 版本后卡片会提示，重新复制一次配置即可。
- 编译较慢的大工程：Codex 配置已把工具超时设为 10 分钟；Claude Code 可设环境变量 `MCP_TOOL_TIMEOUT`（毫秒）调大。
- **只能填 URL 的客户端**：卡片的 Local HTTP endpoint 打开后提供 `http://127.0.0.1:<端口>/mcp`（需要卡片给出的访问令牌，只接受本机连接），复制对应的 HTTP 配置即可；它运行在独立进程里，脚本重载不影响。

---

## 8. 在 AgentCore Chat 里使用外部 MCP 服务器

让 AgentCore 自己的 Chat 调用外部 MCP 服务器（Jira、Figma、内部文档检索等）的工具。

1. `Preferences > AgentCore > Tools & Extensions` 的 **External MCP servers** 卡片里点 **Open personal config**。
2. 按 Claude Desktop / Claude Code 相同的格式填写，可以直接粘贴：

   ```json
   {
     "mcpServers": {
       "everything": { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-everything"] },
       "docs": { "type": "http", "url": "https://example.com/mcp", "headers": { "Authorization": "Bearer ..." } }
     }
   }
   ```

3. 点 **Reload**，卡片显示每个服务器的连接状态和工具数。

需要自行安装服务器运行环境（示例中的 `npx` 需要 Node.js/npm）。配置位于 `UserSettings/AgentCore/McpServers.json`（个人本地，不进版本库）；工程中已存在的旧 `ProjectSettings/AgentCore/McpServers.json` 仅只读合并（同名以个人配置为准），AgentCore 不会创建或修改它。当前接入范围是工具，不支持外部服务器的 OAuth 登录、resources 或 prompts。

服务器的工具以 `mcp__<服务器>__<工具>` 出现在 Chat 中（按需激活，分类 `MCP: <服务器>`），和 AgentCore 自带工具走同一套审查与权限规则；服务器标为只读的工具默认按 ReadOnly 处理，其余按 High 处理；最终是否确认仍取决于权限规则和当前审查级别。脚本重载后会自动重连。

---

## 故障排查

| 问题 | 解决方案 |
|------|---------|
| Chat 窗口无响应 | 检查 Provider Profile 的 Endpoint 和 API Key，点 Test Connection 复核 |
| 工具调用报错 | 查看 Unity Console 错误日志 |
| 编译后会话丢失 | 通常自动恢复，未恢复可重新打开 AgentCore 窗口 |
| VCS / Indexing 工具不可见 | 确认在 Preferences > AgentCore 的 Tools & Extensions 分页启用了对应组件 |
| Claude Code / Codex 提示 Unity 编辑器未运行 | 先在 Unity 中打开该工程，并确认 MCP Server 组件已启用、卡片状态为 Ready |
| 客户端提示令牌无效或需要重新复制配置 | 在 MCP Server 卡片重新复制配置并替换客户端里的旧配置 |
