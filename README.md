# AgentCore Unity

> Unity Editor 内置 AI Agent 插件 — 让 LLM 在真实 Unity Editor 工作流中规划、执行、观察与修正。

AgentCore Unity 是一个 Editor-only UPM package。它不是通用代码 Agent 的替代品，而是面向 Unity 项目的原生执行层：把模型推理、Unity Editor 状态、工具调用、项目知识、版本控制、代码索引与验证反馈连接成可治理的闭环。

## 当前状态

- **Package**: `com.agentcore.unity`
- **Version**: `1.2.1`
- **Unity**: `2021.3+`
- **Assembly**: `AgentCore.Editor`，Editor-only，主程序集不引用用户项目程序集
- **Distribution**: UPM package
- **Status**: Phase 6 已验收；治理层 G.1~G.3、后台增量索引、ThinkingDrawer / reasoning 可观测性、Request Enrichment 已完成；CompletionGate / Evidence Pipeline / Plugin / MCP 仍在后续规划中

## 核心能力

### Agent Loop

- OpenAI-compatible Chat Completions 工具调用循环
- 多轮 tool call：LLM 可规划、调用工具、读取结果并继续执行
- Fallback routing、自动编译检查、Console 错误捕获与工具结果回灌
- Domain Reload 恢复：脚本修改触发重编译后恢复会话、pending tool calls、assistant content、reasoning / planning trace 状态

### Unity 原生工具系统

- 基于 `[AgentTool]` + `IAgentTool` 的反射自动发现
- 当前源码中约 **51 个 AgentTool 声明**，覆盖场景、对象、组件、脚本、Prefab、资源、材质、Shader、导入设置、UI、相机、物理、光照、音频、Timeline、Cinemachine、ProBuilder、构建、测试、清理、优化、文件系统、Memory、LightRAG、Indexing、VCS 等能力域
- 工具执行统一经过 schema 校验、Dispatcher 分发、主线程调度与异常包装

### Tool Governance

- `ToolRiskPolicy` / `ToolCapability` / `ToolExecutionRisk` / `ToolPolicyDecision` 风险基础设施
- `ToolPathRiskResolver` + `WorkspacePathPolicy`：根据目标路径所属 Workspace Root 评估风险
- `ToolCallDispatcher` 已在工具执行前接入路径风险与策略决策
- 当前策略是 VCS-friendly 宽松默认：Blocked workspace root 会阻断；delete/remove/destroy 类 action 需要确认；其他非删除操作默认放行
- `execute_code` 默认降权为 Restricted 工具

### Lazy Tool Discovery / ActiveToolScope

- 工具可见性分为 `AlwaysVisible` / `OnDemand` / `Restricted`
- LLM 默认只看到核心工具与 `request_tools`
- `request_tools` 元工具支持列出和激活按需工具分类，降低工具 schema tax 和误选工具风险
- Settings 支持整体关闭 tool scoping，回退到旧的全量非 Restricted 暴露模式

### Workspace / VCS

- WorkspaceRoot / UnityRoot / Scope Root 建模，适配大型商业 Unity 项目、SVN 工作副本、多根目录结构
- Workspace path policy 区分 editable project code、shared code、workspace package、commercial plugin、custom plugin、engine code、tooling code、generated code、read-only reference 等角色
- 可选 VCS 组件通过 `AGENTCORE_VCS` 启用，支持 Git / SVN / Perforce 的状态、diff、log、同步、提交等工作流

### Code Indexing

- 可选 Indexing 组件通过 `AGENTCORE_INDEXING` 启用
- Roslyn 符号索引，支持符号搜索、全文搜索、依赖查询、用法查询、符号上下文聚合
- SQLite 优先，JSONL fallback
- 后台静默 + 增量索引：AssetPostprocessor 记录 dirty paths，`BackgroundIndexService` 合并、去抖、后台执行 targeted incremental indexing
- `search_code` 可查询索引状态、dirty 数量、失败信息和 session pause 状态

### Context / Memory / Knowledge

- Bootstrap 链：`SOUL(+SOUL.ext) → TOOLS → PROJECT(auto) → PROJECT.md(user)`
- Conversation compression 与 tool result compression
- Context usage UI
- Mem0 semantic memory 与 LightRAG knowledge base
- Code Index 按任务召回相关代码证据，而不是一次性读取整个仓库

### Reasoning Observability

- `ThinkingDrawer`：assistant turn 的 reasoning / planning trace 抽屉，默认折叠
- `AssistantTurnView`：固定 assistant turn 布局为 ThinkingDrawer → ToolCallGroup → MessageBubble
- 双来源 reasoning 抽取：provider structured reasoning 字段 + `---THINKING---` / `---ACTION---` visible planning trace
- reasoning / raw assistant content 仅持久化到 UI/session/archive，不进入后续 LLM `_messages`
- `RequestEnrichment` 在 JSON 请求层注入 `stream_options`、`reasoning` 与用户自定义 `extraRequestBody`，用于触发 OpenRouter 等代理返回 reasoning content

## 架构概览

```text
com.agentcore.unity/
├── package.json
├── AGENTS.md
├── CHANGELOG.md
├── README.md
├── Editor/
│   ├── AgentCore.Editor.asmdef          # 主 Editor-only 程序集
│   ├── Bootstrap/                       # SOUL / TOOLS / PROJECT bootstrap
│   ├── Config/                          # Settings, secure key storage, settings pages
│   ├── Core/                            # AgentLoop partials, state machine, Domain Reload, compression
│   ├── Extensions/                      # Hub / Settings / Status contribution host
│   ├── Indexing/                        # 可选 Code Indexing 组件（AGENTCORE_INDEXING）
│   ├── LLM/                             # OpenAI-compatible client, streaming parser, request enrichment
│   ├── Session/                         # Session storage, export, auto memory strategy
│   ├── Tools/                           # Tool registry, dispatcher, native/cloud/filesystem tools, safety
│   ├── UI/                              # Chat window, hub, assistant turn views, UI components
│   ├── VCS/                             # 可选 VCS 组件（AGENTCORE_VCS）
│   ├── Workspace/                       # Workspace root resolution, path service, path safety
│   └── Utils/
└── plans/                               # Roadmap, design docs, ADRs, feature plans
```

## 技术栈

| 层级 | 技术 |
|------|------|
| UI | Unity UI Toolkit / IMGUI Settings Provider |
| Agent 核心 | C# 9.0, async/await, OpenAI-compatible tool calling |
| LLM 通信 | OpenAI-compatible API，Request Enrichment，streaming parser |
| 工具系统 | `[AgentTool]` 自动发现，ToolRegistry，ToolCallDispatcher |
| 治理 | Tool Risk Policy，WorkspacePathPolicy，ActiveToolScope |
| 代码索引 | Roslyn，SQLite / JSONL，后台增量索引 |
| 知识系统 | Mem0，LightRAG，PROJECT.md，Code Index |
| 版本控制 | Git / SVN / Perforce 可选组件 |
| 包格式 | Unity Package Manager |

## 当前开发路线

已完成：

- Phase 1~6：核心 Agent Loop、原生工具系统、Domain Reload、会话管理、Memory / RAG、Workspace、VCS、Code Index、Settings shell、Phase 6 实战验收
- 治理层 G.1~G.3：Tool Risk Policy / WorkspacePathPolicy 接入、ExecuteCodeTool 降权、Lazy Tool Discovery / ActiveToolScope
- Phase 7 §3.1：后台静默 + 增量索引
- Phase 7 §3.2：Chat UI / ThinkingDrawer reasoning 可观测性
- v1.2.1：Request Enrichment 修复 reasoning 触发

后续重点：

- G.4：ContextWindowManager / Bootstrap 预算收口
- G.5：CompletionGate + Operation Journal
- G.6：Evidence Pipeline / Planner-Executor-Verifier 分层
- Phase 7：Plugin / Extension 系统、UPM 发布流程、文档站、示例项目
- Phase 8：MCP Server 对外互操作

详细方向以 [`plans/ROADMAP.md`](plans/ROADMAP.md) 为准；设计约束见 [`plans/llm-agent-architecture-remediation-plan.md`](plans/llm-agent-architecture-remediation-plan.md)。

## 开发约束

- 所有源码位于 `Editor/`，主程序集为 Editor-only
- 主程序集不得引用用户项目程序集或可选组件程序集
- 新工具必须使用 `[AgentTool]` + `IAgentTool` 自动注册，并声明合适的 risk / capability / visibility
- 新增高风险执行能力、MCP、Plugin、文件写入自动化或默认工具暴露扩大前，必须先对齐治理层约束
- 文档和架构规则以 `AGENTS.md`、`plans/ROADMAP.md` 和实际源码为准

## License

Internal use only.
