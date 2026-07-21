# AgentCore Unity

> Unity Editor 内置 AI Agent 插件 — 让 LLM 在真实 Unity Editor 工作流中规划、执行、观察与修正。

AgentCore Unity 是一个 Editor-only UPM package。它不是通用代码 Agent 的替代品，而是面向 Unity 项目的原生执行层：把模型推理、Unity Editor 状态、工具调用、项目知识、版本控制、代码索引与验证反馈连接成可治理的闭环。

## 当前状态

- **Package**: `com.agentcore.unity`
- **Version**: `1.7.19`
- **Unity**: `2021.3+`
- **Assembly**: `AgentCore.Editor`，Editor-only，主程序集不引用用户项目程序集
- **Distribution**: UPM package
- **Code Scale**: 288 个 .cs 文件，约 97K 行代码，51 个原生工具
- **Status**: Phase 1~6 已验收；治理层 G.1~G.3 完成；Phase 7 §3.1/§3.2 完成；Phase 9 Self-Challenge 收尾（GLM-5.2 替代 Qwen 后 LLM 自身能力填上缺口）；v1.6.x 系列交付 Context Ingest、YOLO 信任模式、日志分级、PendingIndicator、SSE yield 优化、消息引用栏、Play Mode preflight、自适应 LLM 配置、统一 LLM 管道、气泡溢出修复、流式 UI 性能优化；v1.7.0 Settings v20 死字段清理 + VCS 模块修复（进程泄漏/事件泄漏/日志风暴/设置极简化/多 VCS 支持）；v1.7.1 修复新装路径不存在 + VCS 远端检查开机触发 + CS0162 编译警告；v1.7.2 minimalism / self-challenge 历史文档整理归档（无代码变化）；v1.7.3 修复 beforeAssemblyReload 回调（时机层面）；v1.7.4 修复路径解析根因（Unity 内部版本号 vs 营销版本号不匹配，三级路径解析）；v1.7.5 补全硬编码兜底 + ScriptingDefineSymbols 版本兼容（Unity 2023+/6 的 ForGroup API 废弃迁移）；v1.7.6 修复 v1.7.5 遗漏的 using 指令（CS0103）；v1.7.7~1.7.8 Preferences 目录 Move 弹窗根因修复（第五层）+ CS0103 打包遗漏修正；v1.7.9 HelpBubble 浮窗根因修复 + 对话泡泡溢出/窗口缩窄卡死修复 + UI 交互视觉审查 P0/P1（IME Enter 误发送、GetContextBudget 高频遍历、流式视觉跳变方案C、ToolCallCard 超长结果性能、色板统一 AgentCoreColors）；v1.7.10 UI 收尾（引用栏 chip 截断修复、HelpBubble 快捷键补齐、HubRail 图标化带文字回退、虚拟列表加载更多滚动补偿；P3 项经核实判定无需改动）；v1.7.14 ask_user 中途提问工具（挂起-唤醒完整态：Agent 遇方向岔路主动调 ask_user 提问→独立选项面板阻断→loop 截断退出不空等→用户点选项/自由文本应答后唤醒 LLM 继续；跨 domain reload 存活；复刻 WaitingCompilation 范式，不碰 SelfChallenge）；v1.7.15 修复工具确认面板不弹（信任 scope 无感知残留：破坏性工具被策略正确判为 RequireConfirmation 却直接执行，根因是 YOLO/Trust Low-Med 会话信任经 SessionState 持久化后新建 session 未清 → IsToolConfirmationTrusted 跳过面板；策略判定链全程正确。修复将信任生命周期绑定对话 session：OnNewSessionClicked + SwitchToSession 新增 ClearPendingToolConfirmations，新建/切换对话即失效，Domain Reload 保留，Editor 重启归零）；v1.7.16 工具确认流程重构（风险分级 ToolRiskLevel × 能力位 ToolCapability 联合决策，判定粒度落到 action 级，修复多 action 混合工具能力位连坐）+ set_selection 多选/InstanceID/同名全选增强 + set_active_batch per-item active 修复（静默给错结果）+ VCS 体验改造（删除 SceneView 顶部黄色警示条、Update 去确认框、左侧导航按钮按 VCS 类型显示 SVN/GIT/P4 且远端有更新时变黄，新增通用 HubNavBadgeBus 角标总线）；v1.7.17 修复 GameObject 工具链无法操作 inactive 对象（FindObjectsByType 默认 Exclude 导致禁用后不可再启用，三处查找补 FindObjectsInactive.Include）；v1.7.18 修复输入框 Shift+Enter 触发全选（Unity 内建 TextField default action 拦不住，经 DIAG 实证）——换行键位改为 Ctrl+Enter、发送保持 Enter 且保留 IME 上屏守卫；v1.7.19 VCS 面板 View Diff 按钮改为呼出外部图形 diff 工具（不再写 Console，避免受日志开关影响/被淹没），目前仅维护 SVN，Git/P4 按钮禁用；后续重点为自演化知识系统 + Phase 8 MCP Server + 产品化分发

## 核心能力

### Agent Loop

- OpenAI-compatible Chat Completions 工具调用循环
- 多轮 tool call：LLM 可规划、调用工具、读取结果并继续执行
- Fallback routing、自动编译检查（内部常量）、Console 错误捕获与工具结果回灌
- Domain Reload 恢复：脚本修改触发重编译后恢复会话、pending tool calls、assistant content、reasoning / planning trace 状态

### Unity 原生工具系统

- 基于 `[AgentTool]` + `IAgentTool` 的反射自动发现
- 当前源码中约 **51 个 AgentTool 声明**，覆盖场景、对象、组件、脚本、Prefab、资源、材质、Shader、导入设置、UI、相机、物理、光照、音频、Timeline、Cinemachine、ProBuilder、构建、测试、清理、优化、文件系统、Memory、LightRAG、Indexing、VCS 等能力域
- 工具执行统一经过 schema 校验、Dispatcher 分发、主线程调度与异常包装

### Tool Governance

- `ToolRiskPolicy` / `ToolCapability` / `ToolExecutionRisk` / `ToolPolicyDecision` 风险基础设施
- `ToolPathRiskResolver` + `WorkspacePathPolicy`：根据目标路径所属 Workspace Root 评估风险
- `ToolCallDispatcher` 已在工具执行前接入路径风险与策略决策
- `PlayModePreflight`：Play Mode 中禁止 write 类工具调用
- 当前策略是 VCS-friendly 宽松默认：Blocked workspace root 会阻断；delete/remove/destroy 类 action 需要确认；其他非删除操作默认放行
- `execute_code` 默认降权为 Restricted 工具

### Tool Confirmation Trust Scope

- YOLO 模式：3 按钮布局（Deny / Trust Low-Med for Session / YOLO All）
- 信任 scope 通过 `UnityEditor.SessionState` 持久化，跨 Domain Reload 保留
- `SessionLowMediumRisk`：本会话内所有 ReadOnly/Low/Medium 风险工具直通
- `SessionAll`：本会话内所有工具无条件直通（真正 YOLO）

### Context Ingest

- 全局快捷键 `Ctrl+Shift+X` 作为通用查询入口
- 任意 Unity 窗口聚焦时都可触发，自动采集相关上下文注入 ChatWindow 输入框
- 路由优先级：Console → Project asset → Hierarchy/Scene GO → 任意 EditorWindow（反射 + UI Toolkit Pick）
- 分级采样策略：单选/多选/大 Scene 自动降级，token 硬上限 15000 字符

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
- `AssistantTurnView`：多轮 assistant turn 布局，每轮独立 ThinkingDrawer + ToolCallGroup + 分隔线
- 双来源 reasoning 抽取：provider structured reasoning 字段 + `---THINKING---` / `---ACTION---` visible planning trace
- reasoning / raw assistant content 仅持久化到 UI/session/archive，不进入后续 LLM `_messages`
- `RequestEnrichment` 在 JSON 请求层注入 `stream_options`、`reasoning` 与用户自定义 `extraRequestBody`，用于触发 OpenRouter 等代理返回 reasoning content

### Chat UX

- `PendingIndicator`：点击发送后消息流内显示占位气泡 + 3 点动画，覆盖 LLM 首响应前空窗期
- 折叠面板活跃度指示器：ThinkingDrawer 尾部 60 字符实时预览 + ToolCallGroup running 工具名 + active-pulse 边框
- 流式回复时用户可上翻 + "跳到最新"浮动按钮
- 输入框内容过多可滚动（max-height 260px）
- `MessageReferenceBar`：assistant 消息中的资源/GameObject 引用渲染为 chip 按钮可点击跳转
- SSE Yield 策略：按时间预算（200ms）让步主线程，避免 Hold on 对话框同时不损失吐字速度

### Logging

- `AgentCoreLog` 静态封装，5 档日志级别：Silent / Error / Warning / Info / Debug
- 默认 Info 级（关键业务事件可见，高频热点跳过），用户可在 Settings 中热切换到 Debug 级排查问题

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
- Phase 9 alpha：Self-Challenge 双节点 prompt 层幻觉护栏 + ADR-17 极简哲学
- v1.6.x：Context Ingest（Ctrl+Shift+X）、YOLO 信任模式、日志分级、PendingIndicator、SSE yield 优化、消息引用栏、Play Mode preflight、ThinkingDrawer 独立展开按钮、多轮思考窗口、文件删除视觉反馈、GLM-5.2 reasoning 参数适配、自适应 LLM 配置（ModelCapabilityProbe）、统一 LLM 管道（消灭 CompressionLLMClient）、气泡溢出修复（flex-shrink + overflow + 双向 height sync）、流式 UI 性能优化（三层帧节流 + ConcurrentQueue + 4000 字符窗口）
- v1.7.1：修复新装路径不存在（`[InitializeOnLoad]` 静态构造函数）+ VCS 远端检查开机触发（`_lastCheckedUtc` 初始化为 `UtcNow`）+ 3 个 CS0162 编译警告清理（`const true` 死守卫）+ SessionStorage 日志降级
- v1.7.0：Settings v20 死字段清理（12 字段删除 + disabledTools 默认值修正 + Model Info 显示 effective tokens + SecureKeyStorage Compression LLM 死方法删除 + workspaceAutoDetectEnabled 假 toggle 删除）、VCS 模块修复（VcsDetector Process using 防泄漏 + VersionControlPanel DetachFromPanelEvent 防事件泄漏 + VcsProjectWindowIntegration 全量重写删 Debug.Log 风暴 + 多 VCS 支持 + VcsSettings 8→2 极简化 + VcsRemoteStatusMonitor 删重复 RepaintAll）
- v1.7.6：修复 v1.7.5 遗漏的 using 指令（CS0103 编译错误）
- v1.7.5：补全 Preferences 路径硬编码兜底 + ScriptingDefineSymbols 版本兼容（Unity 2023+/6 的 ForGroup API 废弃迁移）
- v1.7.4：修复 Preferences 目录路径解析根因（Unity 内部版本号 `Editor-5.x` vs 营销版本号 `2021` 不匹配 → 三级路径解析：反射 + 目录扫描 + 硬编码兜底）
- v1.7.3：修复 beforeAssemblyReload 回调（时机层面，Domain Unload 开始时确保目录存在）
- v1.7.2：minimalism / self-challenge 历史文档整理归档（`plan...[truncated]

后续重点：

- Phase 9 GA：4 周 kill criteria 实测窗口 + alpha3 兜底完善
- Phase 8：MCP Server 对外互操作（McpServerHost / McpToolBridge / 风险分级 / Settings UI / 多 IDE 配置）
- Phase 7 §3.4：产品化与分发（UPM 发布流程、文档站、示例项目、Asset Store）

详细方向以 [`plans/ROADMAP.md`](plans/ROADMAP.md) 为准；设计约束见 [`plans/llm-agent-architecture-remediation-plan.md`](plans/llm-agent-architecture-remediation-plan.md)。

## 开发约束

- 所有源码位于 `Editor/`，主程序集为 Editor-only
- 主程序集不得引用用户项目程序集或可选组件程序集
- 新工具必须使用 `[AgentTool]` + `IAgentTool` 自动注册，并声明合适的 risk / capability / visibility
- 新增高风险执行能力、MCP、Plugin、文件写入自动化或默认工具暴露扩大前，必须先对齐治理层约束
- 文档和架构规则以 `AGENTS.md`、`plans/ROADMAP.md` 和实际源码为准

## License

Internal use only.
