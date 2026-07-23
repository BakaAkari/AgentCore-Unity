# AgentCore Package Development Rules

> 本文件定义了 `com.agentcore.unity` 包的 LLM 开发规范。
> 面向使用 AI 辅助开发此项目的开发者，确保代码修改的一致性、稳定性和健壮性。
>
> **优先级**: 本文件 > 上级 `AGENTS.md` (Unity workspace rules)。冲突时以本文件为准。
>
> **关键原则**: 本文件描述的是**架构模式和规则**，不是文件清单。
> 项目在持续开发中，具体文件列表和代码细节以实际代码为准。

---

## 🚀 新 Agent / 接手开发者 — 请先读这里

**当前版本**: v1.8.0 (2026-07-23, tag `v1.8.0` on GitHub)

如果你是**第一次接手本项目**或**换设备开发**，按以下顺序读文档：

| 顺序 | 文档 | 用途 |
|---|---|---|
| **0** | **`plans/HANDOFF-v1.8.0-to-v1.9.0.md`** | **接手指南**（已解决/待解决/遗留 issue + 环境/git 状态 + 8 份必读入口） |
| 1 | 本文件 (AGENTS.md) | LLM 开发规范, 目录结构, 工具开发模板 |
| 2 | `plans/ROADMAP.md` §3.w.1 v1.8.0 收尾 | 能力覆盖 milestone 完整上下文 |
| 3 | `plans/capability-coverage-audit.md` | v1.8.0 立项审计方法论（v1.9.0 P1/P2 必读）|
| 4 | `plans/adversarial-coverage-audit.md` | Undo/mutating-side 对抗式审计 |
| 5 | `plans/agentcore-execute-code-constraints.md` | execute_code 反射探测硬约束 |
| 6 | `CHANGELOG.md` v1.8.0 章节 | 本版每一条改动 |
| 7 | `Editor/Bootstrap/Resources/SOUL.md` | Agent 行为准则（尤其 §2.10 + §2.11）|
| 8 | `plans/perf-issue-agent-streaming-blocks-editor.md` | 已知 Editor 主线程阻塞 issue |

**做任何开发前**：确认你已完成 HANDOFF §8 交接确认清单。

---

## 1. 项目概览

| 属性 | 值 |
|------|-----|
| 包名 | `com.agentcore.unity` |
| Unity 最低版本 | 2021.3+ |
| 依赖 | `com.unity.nuget.newtonsoft-json` (通过 UPM) |
| asmdef | `AgentCore.Editor` (Editor-only, 零外部引用) |
| 语言 | C# 9.0 (Unity 2022.3 支持范围) |

> 版本号和具体依赖版本请查阅 `package.json`。

### 1.1 核心定位

AgentCore 是一个 **Unity Editor 内嵌的 AI Agent 插件**，通过 Chat 窗口与 LLM 交互，使用工具系统操作 Unity Editor。

### 1.2 架构隔离原则

```
AgentCore.Editor.asmdef
├── includePlatforms: ["Editor"]    ← 仅 Editor 平台
├── references: []                  ← 零外部程序集引用
└── autoReferenced: true            ← 自动被 Editor 代码引用
```

**关键约束**:
- 所有代码必须在 `Editor/` 目录下
- 不得引用任何外部 asmdef（包括用户项目的程序集）
- 不得使用 Runtime-only API（如 `Application.isPlaying` 在 Editor 中可用，但 `SceneManager.LoadScene` 的行为不同）
- 唯一的外部依赖是 `Newtonsoft.Json`（通过 UPM 包引用）

---

## 2. 目录结构与命名规范

### 2.1 目录布局模式

> 以下是目录的**功能分区**，不是完整文件列表。实际文件请通过 `list_files` 工具查看。

```
com.agentcore.unity/
├── package.json                    # UPM 包清单
├── AGENTS.md                       # 本文件 — LLM 开发规范
├── CHANGELOG.md                    # 版本变更日志
├── Editor/                         # 所有源代码（Editor-only）
│   ├── AgentCore.Editor.asmdef     # 主程序集定义
│   ├── Extensions/                 # 扩展宿主与可选组件管理
│   ├── Bootstrap/                  # 启动引导系统
│   │   └── Resources/              #   内嵌资源 (SOUL.md, TOOLS.md.template 等)
│   ├── Config/                     # 配置系统 (Settings, SecureKeyStorage 等)
│   ├── Core/                       # 核心运行时 (AgentLoop partial 9 文件, 状态机, 编译监控等)
│   ├── Indexing/                   # 代码索引可选组件（受 AGENTCORE_INDEXING 控制）
│   │   ├── Config/                 #   组件描述符
│   │   ├── Core/                   #   索引引擎 (CodebaseIndexer, RoslynSymbolExtractor 等)
│   │   ├── Models/                 #   数据模型 (IndexWorkspace, IndexRoot, SymbolInfo 等)
│   │   ├── Query/                  #   查询模型 (SearchQuery)
│   │   ├── Roots/                  #   根目录 Provider (Unity/VCS/Workspace/User/Package)
│   │   ├── Tools/                  #   search_code 工具
│   │   └── UI/                     #   IndexingSettingsPage + IndexingSettingsContribution
│   ├── LLM/                        # LLM 客户端 (接口, OpenAI兼容, 流解析等)
│   ├── Session/                    # 会话管理 (存储, 序列化, 自动记忆等)
│   ├── Tools/                      # 工具系统（核心扩展点）
│   │   ├── Infrastructure/         #   工具基础设施 (特性, 自动发现, 辅助方法等)
│   │   ├── Native/                 #   原生工具（Unity API）— 按功能分子目录
│   │   ├── Cloud/                  #   云端工具（HTTP API）
│   │   └── FileSystem/             #   文件系统工具
│   ├── UI/                         # 用户界面
│   │   ├── Components/             #   UI 组件 (ThinkingDrawer, MessageBubble, ToolCallGroup, AssistantTurnView, PendingIndicator, MessageReferenceBar, FileChangeSummaryPanel, SelfChallengeCard 等)
│   │   ├── Context/               #   Context Ingest 模块 (Ctrl+Shift+X 通用查询入口)
│   │   ├── ChatWindow.*.cs         #   ChatWindow partial 文件 (Events/Messages/Tools/Input/Confirmation/Sessions/Restore/SelfChallenge/ContextIngest/DomainReload/PendingIndicator)
│   │   ├── ChatWindow.cs/.uxml/.uss #   主窗口 + 样式
│   │   └── Hub/                   #   Hub 面板
│   ├── VCS/                        # 内置可选 VCS 组件（受 AGENTCORE_VCS 控制）
│   ├── Workspace/                  # Workspace 基础设施（v0.9.0）
│   │   ├── Config/                 #   WorkspaceConfig / WorkspaceConfigStorage
│   │   ├── Resolution/             #   WorkspaceRootResolver / ScopeRootResolver 等
│   │   └── Safety/                 #   WorkspacePathPolicy / WorkspaceOperationRisk
│   └── Utils/                      # 通用工具 (AgentCoreLog, JsonHelper, HttpClientFactory, AsyncHelper 等)
└── plans/                          # 设计文档（仅参考）
```

### 2.2 如何了解当前文件结构

修改前，先通过以下方式了解实际结构：

1. `list_files("Editor/", recursive=true)` — 查看完整文件列表
2. 阅读目标目录下的现有文件 — 学习当前模式
3. 查看 `Editor/Tools/Native/` 的子目录 — 了解工具分类体系

### 2.3 命名规范

| 类型 | 规范 | 示例格式 |
|------|------|----------|
| 命名空间 | `AgentCore.Editor.<Module>` | `AgentCore.Editor.Tools.Native.<Category>` |
| 工具类 | `<功能>Tool` | `Manage<X>Tool`, `Read<X>Tool` |
| 客户端类 | `<服务>Client` | `<Service>Client` |
| 接口 | `I<名称>` | `IAgentTool`, `ILLMClient` |
| 设置类 | `<模块>Settings` | `AgentCoreSettings` |
| 数据类 | `<名称>Data` / `<名称>Info` | `SessionData`, `ErrorInfo` |
| 枚举 | PascalCase | `AgentState`, `InterruptPhase` |
| 私有字段 | `_camelCase` | `_reflectionInitialized` |
| 静态只读 | `_camelCase` 或 `PascalCase` | `_parametersSchema`, `ResourcesPath` |

### 2.4 新文件放置规则

| 文件类型 | 目录 |
|----------|------|
| 新的原生工具 | `Editor/Tools/Native/<Category>/` |
| 新的云端工具 | `Editor/Tools/Cloud/` |
| 新的云端客户端 | `Editor/Tools/Cloud/` |
| 可选组件 | `Editor/<ComponentName>/` + 独立 Editor asmdef |
| 可选组件工具 | `Editor/<ComponentName>/Tools/` |
| 可选组件 UI | `Editor/<ComponentName>/UI/` |
| 可选组件配置 | `Editor/<ComponentName>/Config/` |
| 核心逻辑 | `Editor/Core/` |
| LLM 相关 | `Editor/LLM/` |
| UI 组件 | `Editor/UI/Components/` |
| 通用工具 | `Editor/Utils/` |
| 配置相关 | `Editor/Config/` |
| 启动引导 | `Editor/Bootstrap/` |
| 会话相关 | `Editor/Session/` |

---

## 3. 核心架构模式

> 以下流程图描述的是**架构设计模式**。具体方法名和实现细节以实际代码为准。
> 修改核心系统前，务必先完整阅读相关源文件。

### 3.1 系统启动流程

```
ChatWindow.CreateGUI()
  → InitializeAgentLoop()
    → 创建 LLM 客户端
    → 创建 AgentLoop
      → agentLoop.Initialize()
        → ToolAutoDiscovery — 自动扫描并注册所有 [AgentTool] 工具
        → BootstrapLoader — 加载系统提示词各组件
        → BootstrapContext — 编译最终 System Prompt
        → 初始化 ToolCallDispatcher, CompilationWatcher, ConsoleErrorCapture, SessionManager
  → TryRestoreSession()
```

### 3.2 消息处理循环

```
用户输入 → SendMessageAsync(userMessage)
  → 搜索相关记忆 → 注入记忆上下文
  → RunToolCallLoopAsync()
    → 调用 LLM (流式) → 获取响应
    → 如果有 tool_calls:
      → ExecuteToolCallsAsync()
        → ToolCallDispatcher 分发执行
          → RequiresMainThread? → 主线程执行 : 异步执行
        → 构建工具结果消息
      → 继续循环（最多 maxToolRounds 轮）
    → 如果无 tool_calls:
      → HandleFinalResponse() → 结束
```

### 3.3 Domain Reload 恢复机制

```
脚本修改 → 触发 Domain Reload
  → OnBeforeAssemblyReload()
    → 保存 DomainReloadState (InterruptPhase, 待处理的 tool calls, 对话历史)
  → Unity 重新编译 → 所有静态状态丢失
  → ChatWindow.CreateGUI() 重新执行
    → TryRestoreSession()
      → AgentLoop.TryResumeAfterReload()
        → 读取 DomainReloadState
        → 根据 InterruptPhase 恢复到对应阶段
        → TriggerResumeLLMCall()
```

> 要了解 `InterruptPhase` 的所有枚举值，请阅读 `Editor/Core/DomainReloadState.cs`。
> 要了解 `AgentState` 的所有状态，请阅读 `Editor/Core/MessageTypes.cs`。

---

## 3.4 Optional Components 扩展机制

AgentCore 支持内置可选组件模式：组件源码随包分发，但通过独立 Editor asmdef 与 scripting define 控制是否参与编译。

**当前模式**:
- 主程序集 `AgentCore.Editor` 提供 `Editor/Extensions/` 宿主接口与动态发现。
- 可选组件使用独立 asmdef，例如 `AgentCore.VCS.Editor`。
- 可选组件 asmdef 必须引用 `AgentCore.Editor`，主程序集不得反向引用可选组件程序集。
- 可选组件通过 contribution 接入 Hub / Settings，不允许主窗口或主设置页强类型引用组件类型。
- 可选组件工具仍使用 `[AgentTool]` + `IAgentTool`，只在组件程序集被编译后由 `ToolAutoDiscovery` 注册。

**VCS 组件约定**:
- 启用 define: `AGENTCORE_VCS`
- 程序集: `AgentCore.VCS.Editor`
- 目录: `Editor/VCS/`
- 命名空间: `AgentCore.Editor.Components.VCS.*`

新增可选组件时必须遵守：
1. 新建独立 Editor asmdef，并用明确的 define constraint 控制编译。
2. 在主 Settings 中只通过 `OptionalComponentManager` 暴露启用/禁用入口。
3. Hub 入口通过 `IAgentCorePanelContribution` 动态贡献。
4. 设置 UI 通过 `IAgentCoreSettingsContribution` 动态贡献。
5. 主程序集不得出现任何组件类型强引用。
6. 禁用组件后，`ToolAutoDiscovery` 必须重建 `ToolRegistry`，不得残留旧工具实例。

---

## 4. 工具开发规范（最重要的扩展点）

### 4.1 工具类型分类

| 类型 | 目录 | RequiresMainThread | 特征 |
|------|------|--------------------|------|
| **Native 工具** | `Editor/Tools/Native/` | `true` | 直接调用 Unity API |
| **Cloud 工具** | `Editor/Tools/Cloud/` | `false` | HTTP 调用外部服务 |
| **FileSystem 工具** | `Editor/Tools/FileSystem/` | `false` | 文件系统操作 |

### 4.2 Native 工具开发模板

> **重要**: 编写新工具前，先阅读 `Editor/Tools/Native/` 下同分类的现有工具，以实际代码模式为准。
> 以下模板仅展示结构骨架，具体 using 语句和 API 用法以现有代码为准。

```csharp
namespace AgentCore.Editor.Tools.Native.<Category>
{
    [AgentTool("<tool_name>",
        Description = "面向 LLM 的工具描述 — 要清晰说明能做什么",
        Category = "<Category>",
        RequiresMainThread = true,
        MayModifyScripts = false)]  // 如果会修改脚本文件则设为 true
    public class <ToolName>Tool : IAgentTool
    {
        // 1. 参数 Schema — JSON Schema 格式
        private static readonly JObject _parametersSchema = JObject.Parse(@"{ ... }");

        // 2. Metadata — 必须与 [AgentTool] 特性一致
        public ToolMetadata Metadata => new ToolMetadata( ... );

        // 3. 执行入口 — 统一的 action 分发模式
        public Task<ToolResult> ExecuteAsync(JObject parameters, CancellationToken cancellationToken = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ToolResponse response;
            try
            {
                var action = ToolHelpers.GetRequiredString(parameters, "action").ToLowerInvariant();
                switch (action)
                {
                    case "action1": response = HandleAction1(parameters); break;
                    // ...
                    default: response = ToolResponse.Fail($"Unknown action: {action}"); break;
                }
            }
            catch (Exception ex) { response = ToolResponse.Fail($"Error: {ex.Message}"); }
            sw.Stop();
            return Task.FromResult(response.ToToolResult(sw.Elapsed.TotalMilliseconds));
        }

        private ToolResponse HandleAction1(JObject parameters) { ... }
    }
}
```

### 4.3 Cloud 工具开发模板

> **重要**: 先阅读 `Editor/Tools/Cloud/` 下的现有 `*Client.cs` + `*Tool.cs` 配对，学习实际模式。

```csharp
namespace AgentCore.Editor.Tools.Cloud
{
    [AgentTool("<tool_name>",
        Description = "云端工具描述",
        Category = "<Category>",
        RequiresMainThread = false)]  // Cloud 工具不需要主线程
    public class <ToolName>Tool : IAgentTool
    {
        // Cloud 工具使用 async/await
        public async Task<ToolResult> ExecuteAsync(JObject parameters, CancellationToken cancellationToken = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var client = <Service>Client.FromSettings();
                if (client == null)
                    return ToolResponse.Fail("服务未配置").ToToolResult(0);

                // action 分发 ...
                sw.Stop();
                return response.ToToolResult(sw.Elapsed.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                sw.Stop();
                return ToolResponse.Fail($"Error: {ex.Message}").ToToolResult(sw.Elapsed.TotalMilliseconds);
            }
        }
    }
}
```

### 4.4 工具开发检查清单

开发新工具时，必须逐项确认：

- [ ] **[AgentTool] 特性** — Name, Description, Category, RequiresMainThread, MayModifyScripts 全部正确
- [ ] **风险声明（G.1）** — `RiskLevel`、`Capabilities`、`RequiresConfirmation` 三个属性已按工具实际能力显式声明（未声明则默认 `Medium` / `None` / `false`）；高危工具（写文件、删除、执行代码、VCS 写入、安装包、构建）必须显式声明，不得依赖默认值
- [ ] **Metadata 属性** — 与 [AgentTool] 特性的值完全一致
- [ ] **_parametersSchema** — JSON Schema 格式正确，required 字段完整
- [ ] **命名空间** — `AgentCore.Editor.Tools.<Type>.<Category>` 格式
- [ ] **文件位置** — 放在正确的 `Editor/Tools/<Type>/<Category>/` 目录
- [ ] **ToolHelpers 使用** — 参数解析使用 `ToolHelpers` 中的方法（先阅读该文件了解可用方法）
- [ ] **ToolResponse 返回** — 成功用 `ToolResponse.Ok/OkWithData`，失败用 `ToolResponse.Fail`
- [ ] **Stopwatch 计时** — 每个 ExecuteAsync 都要计时并传给 `ToToolResult`
- [ ] **异常处理** — 外层 try-catch 捕获所有异常，返回 `ToolResponse.Fail`
- [ ] **action 分发** — switch 语句 + default 分支返回有效 action 列表
- [ ] **Description 质量** — 面向 LLM 的描述要清晰、具体，说明能做什么和不能做什么
- [ ] **无需手动注册** — `ToolAutoDiscovery` 会自动扫描并注册，不要手动修改任何注册代码
- [ ] **TOOLS.md.template 更新** — 如果工具面向用户重要，在模板中添加使用指南

### 4.5 工具自动发现机制

```
ToolAutoDiscovery.DiscoverAndRegisterAll()
  → 扫描所有已加载的程序集
  → 查找标记了 [AgentTool] 的类
  → 验证类实现了 IAgentTool 接口
  → 通过 Activator.CreateInstance() 创建实例
  → 注册到 ToolRegistry.Instance
```

**关键规则**:
- 工具类必须有**无参构造函数**（或使用默认构造函数）
- 工具类不能是 abstract 或 generic
- 一个类只能有一个 `[AgentTool]` 特性
- 工具名称（Name）必须全局唯一
- `ToolAutoDiscovery` 每次发现前会重建 `ToolRegistry`，以保证可选组件启用/禁用后的工具列表与当前已编译程序集一致

> 详细实现请阅读 `Editor/Tools/Infrastructure/ToolAutoDiscovery.cs`。

### 4.6 参数解析规范

使用 `ToolHelpers` 中的方法（具体可用方法请阅读 `Editor/Tools/Infrastructure/ToolHelpers.cs`）：

```csharp
// 必需参数 — 缺失时抛出异常
string action = ToolHelpers.GetRequiredString(parameters, "action");

// 可选参数 — 缺失时返回默认值
string search = ToolHelpers.GetOptionalString(parameters, "search");
int maxEntries = ToolHelpers.GetOptionalInt(parameters, "max_entries", 50);
bool verbose = ToolHelpers.GetOptionalBool(parameters, "verbose", false);

// 枚举参数
MyEnum mode = ToolHelpers.GetRequiredEnum<MyEnum>(parameters, "mode");

// Unity 类型 — 具体支持的类型请查阅 ToolHelpers.cs
Vector3 pos = ToolHelpers.ParseVector3(parameters["position"]);
GameObject go = ToolHelpers.FindGameObject(nameOrPath);
```

---

## 5. 关键基础设施规范

> 以下展示的是 API 使用**模式**，不是完整 API 列表。
> 具体可用的属性和方法，请阅读对应源文件。

### 5.1 ToolResponse 使用规范

```csharp
// 成功 — 无数据
ToolResponse.Ok("操作完成")

// 成功 — 带数据（自动序列化为 JSON）
ToolResponse.OkWithData(new { count = 5, items = list }, "找到 5 个结果")

// 失败
ToolResponse.Fail("找不到 GameObject: Player")

// 转换为 ToolResult（传入执行时间）
response.ToToolResult(sw.Elapsed.TotalMilliseconds)
```

> 完整 API 请阅读 `Editor/Tools/Infrastructure/ToolResponse.cs`。

### 5.2 AgentCoreSettings 访问模式

```csharp
// 获取设置实例（ScriptableSingleton，全局唯一）
var settings = AgentCoreSettings.instance;

// 访问属性 — 具体有哪些属性请阅读 AgentCoreSettings.cs
// 常见模式: settings.<propertyName>

// 保存设置
settings.SaveSettings();
```

> **重要**: 不要假设 Settings 有哪些字段。修改前先阅读 `Editor/Config/AgentCoreSettings.cs` 了解当前所有字段。

### 5.3 HttpClientFactory 使用规范

```csharp
// 获取共享 HttpClient（带连接池）
var client = HttpClientFactory.GetClient();

// 创建带认证的请求
var request = HttpClientFactory.CreateRequest(HttpMethod.Post, url, apiKey);
request.Content = new StringContent(json, Encoding.UTF8, "application/json");

var response = await client.SendAsync(request, ct);
```

> 完整 API 请阅读 `Editor/Utils/HttpClientFactory.cs`。

### 5.4 JsonHelper 使用规范

```csharp
// 序列化 / 反序列化
string json = JsonHelper.Serialize(obj, pretty: true);
MyType obj = JsonHelper.Deserialize<MyType>(json);

// 安全解析（失败返回 null）
JObject jobj = JsonHelper.ParseObject(json);
JArray jarr = JsonHelper.ParseArray(json);

// 安全取值
string val = JsonHelper.GetString(jobj, "key", "default");
int num = JsonHelper.GetInt(jobj, "key", 0);
bool flag = JsonHelper.GetBool(jobj, "key", false);
```

> 完整 API 请阅读 `Editor/Utils/JsonHelper.cs`。

---

## 6. 修改核心系统的规则

### 6.1 AgentLoop.cs 修改规则

`AgentLoop.cs` 是系统核心（文件较大），修改时必须遵守：

> **修改前必须先完整阅读 `Editor/Core/AgentLoop.cs`**，理解当前实现。

1. **理解状态机** — 阅读 `Editor/Core/MessageTypes.cs` 中的 `AgentState` 枚举了解所有状态
2. **事件驱动** — 通过 `EmitEvent(AgentEvent)` 通知 UI，不要直接操作 UI
3. **Domain Reload 安全** — 任何新增的状态或数据，如果需要跨 Domain Reload 保留，必须：
   - 在 `OnBeforeAssemblyReload()` 中保存到 `DomainReloadState`
   - 在 `TryResumeAfterReload()` 中恢复
4. **CancellationToken 传递** — 所有 async 方法必须接受并传递 `CancellationToken`
5. **错误不吞没** — 捕获异常后必须通过 `EmitEvent(AgentEvent.ErrorEvent(...))` 通知

### 6.2 Bootstrap 系统修改规则

Bootstrap 加载顺序是固定的：`SOUL(+SOUL.ext) → TOOLS → PROJECT(自动) → PROJECT.md(用户)`

> 修改前先阅读 `Editor/Bootstrap/BootstrapLoader.cs` 了解完整加载逻辑。

**各层职责与约束**：

| 层 | 文件 | 来源 | 可变性 | 说明 |
|----|------|------|--------|------|
| 1 | `SOUL.md` | 包内嵌入 | **不可变** | AI 核心行为约束，§1-§10 永远不被用户覆盖 |
| 1+ | `SOUL.ext.md` | 用户创建（可选） | 用户可编辑 | 追加模式，不替换 SOUL.md；建议 VCS 提交 |
| 2 | `TOOLS.md` | 自动生成 | 自动 | 从 TOOLS.md.template + ToolRegistry 生成 |
| 3 | `PROJECT.md` | 自动收集 | 自动 | ProjectContextCollector 收集，不含敏感信息 |
| 3+ | `PROJECT.md`（用户） | 用户创建 | 用户可编辑 | 项目约定 + 个人偏好，建议 VCS 提交 |

**关键规则**：
- **SOUL.md 不可变** — 禁止在代码中修改 SOUL.md 的加载逻辑使其可被用户替换；用户扩展只能通过 SOUL.ext.md 追加
- **MEMORY.md / USER.md 已废弃** — 这两个文件已被 PROJECT.md（用户层）取代，代码中不得再引用或加载这两个文件名
- **TOOLS.md.template** — 工具使用指南，新增工具后应更新此文件
- **ProjectContextCollector** — 自动收集项目信息，不要收集敏感信息（API Key、用户路径等）

> 详细设计见 `plans/_archive/refactoring/bootstrap-refactor-plan.md`。

### 6.3 UI 修改规则

- 使用 **UI Toolkit** (VisualElement)，不使用 IMGUI（除了 Settings Provider）
- 主窗口通过事件处理方法响应 `AgentEvent`
- UI 组件在 `Editor/UI/Components/` 目录
- 样式在 `.uss` 文件中，布局在 `.uxml` 文件中
- Settings Provider 使用 IMGUI（因为 `SettingsProvider` 的 `OnGUI` 是 IMGUI 接口）

> 修改 UI 前，先阅读 `Editor/UI/` 下的现有文件了解组件结构和样式模式。

### 6.4 Session 系统修改规则

- `SessionData` 是可序列化的，所有字段必须标记 `[Serializable]`
- `SessionStorage` 使用 JSON 文件存储
- `AutoMemoryStrategy` 在会话结束时自动提取记忆，依赖 LLM 调用
- 修改 `SessionData` 结构时注意向后兼容（旧会话文件仍需可加载）

> 修改前先阅读 `Editor/Session/` 下的所有文件了解当前数据模型。

### 6.5 v1.6.x 架构模式补充

#### AssistantTurnView 多轮布局

`AssistantTurnView` 不再是固定的 `ThinkingDrawer → ToolCallGroup → MessageBubble` 布局，而是支持多轮 section：

```
[RoundsContainer]
  ├── RoundSection 1 (ThinkingDrawer + ToolSlot)
  ├── Separator (第 2 轮起)
  ├── RoundSection 2 (ThinkingDrawer + ToolSlot)
  ├── ...
[SelfChallengeSlot]
[BubbleSlot]
```

- `BeginNewRound()` 创建新轮次区域（独立 ThinkingDrawer + ToolSlot）
- `ThinkingDrawer` 属性返回最新轮次的 drawer
- `HandleLoopRoundStarted` 在 `evt.CurrentRound > 1` 时调用 `BeginNewRound()`
- 会话恢复 (`RestoreThinking`) 把所有 reasoning 放入第一轮 drawer

#### AgentCoreLog 日志规范

- 所有流程日志使用 `AgentCoreLog.Info(msg)` 或 `AgentCoreLog.Debug(msg)`
- `AgentCoreLog` 从 `AgentCoreSettings.instance.logLevel` 读取级别
- Debug 级用于高频热点（每 token/event/chunk），Info 级用于会话/turn 级事件
- 唯一例外：`AgentCoreSettings.cs` 的 bootstrap 日志使用原生 `Debug.Log`（避免 static ctor 期反向依赖）

#### Tool Confirmation Trust Scope

工具确认已从 per-call 确认改为 session-level 信任 scope：

- `SessionLowMediumRisk`：本会话内所有 ReadOnly/Low/Medium 风险工具直通
- `SessionAll`（YOLO）：本会话内所有工具无条件直通
- 信任 scope 通过 `UnityEditor.SessionState` 持久化，跨 Domain Reload 保留
- ChatWindow 的 `_sessionTrustScopes` 字段初始化必须在 `CreateGUI` 中显式调用 `LoadSessionTrustScopesFromState()`（Unity 硬要求：ScriptableObject 构造器中不得调用 SessionState API）

#### Context Ingest

- 全局快捷键 `Ctrl+Shift+X` 触发 `ContextIngestRouter`
- 路由优先级：Console → Project asset → Hierarchy/Scene GO → 任意 EditorWindow
- 6 个 Collector 位于 `Editor/UI/Context/`，各自负责特定上下文源
- 注入内容追加到 ChatWindow 输入框（不清空已有输入）

#### 自适应 LLM 配置

- `ModelCapabilityProbe`：启动时异步调用 `/v1/models` 探测 `max_model_len`，内存缓存不持久化，失败 fallback 到 `ContextWindowManager.ModelPrefixMap`
- `ApplyAdaptiveDefaults()`：`reserveResponseTokens = max_model_len × 4%`（clamp 4096~65536）；`maxTokens clamped to reserveResponseTokens`
- `GetEffectiveMaxTokens(contentMaxTokens?)`：reasoning 启用时返回 `maxTokens + reasoningMaxTokens`；压缩器可传 `contentMaxTokens` 获得独立 content 预算
- `temperature` / `maxTokens` 标记 `[HideInInspector]`，Settings 面板 Generation 卡片替换为 Model Info 卡片

#### 统一 LLM 管道

- 所有 LLM 调用（主循环、对话压缩、工具结果压缩、SelfChallenge、AutoMemory）走同一条管道：`OpenAICompatibleClient` → `RequestEnrichment` → `GetEffectiveMaxTokens`
- `CompressionLLMClient` 已删除，`CompressionLLMClientFactory` 返回 `OpenAICompatibleClient` 实例
- 压缩器传 `contentMaxTokens: 512` 获得独立 content 预算（不与主循环共享 maxTokens）
- `FallbackRouter`：非流式路径检测空内容并重试；流式路径不重试（reasoning chunks 已发送到 UI，重试会重复输出）
- 压缩请求预算守卫：`budget = modelMaxTokens - effectiveMaxTokens(512) - systemPromptTokens - 200`；预算 ≤0 跳过压缩；文本超预算截断到 80%

#### 流式 UI 性能优化

流式输出时每个 token 都会触发 UI 更新，高频 token（~50/sec）会淹没主线程。三层帧节流：

1. **AsyncHelper 批处理**：`EditorApplication.delayCall`（每 token 一个闭包）替换为 `ConcurrentQueue<Action>` + `EditorApplication.update` 每帧 drain（max 256/frame），零闭包分配
2. **StreamingTextElement / ThinkingDrawer 16ms flush**：token 累积到 `StringBuilder`，每帧只跑一次 `FilterStreaming + Label.text` 赋值，不每 token 触发 UI relayout
3. **流式文本窗口**：流式阶段 `Label.text` 只显示尾部 4000 字符（`...\n` 前缀），避免超长文本 O(n) layout；最终化时 `SetFinalText` 切到 block 模式渲染全量内容
4. **StringBuilder 替代字符串拼接**：`MessageBubble._lastFullContent` 从 `string +=` 改为 `StringBuilder.Append`
5. **ScrollToBottom 节流**：从 per-token 路径移除，改为 `ThrottledScrollToBottom`（flag-gated 100ms schedule）

#### 气泡溢出修复

- `#content-label`：`flex-shrink:0` → `flex-shrink:1 + overflow:hidden`，长连续字符不再溢出气泡背景
- `MessageReferenceBar` chip：`flex-shrink:0 + NoWrap` → `flex-shrink:1 + maxWidth:100% + whiteSpace:Normal + textOverflow:Ellipsis`，长文件名自动截断
- `SyncBubbleContentHeight`：从单向（只增不减）改为双向（`Mathf.Abs(diff) > 1`），block 模式重新排版后 minHeight 跟随实际高度收缩

### 6.6 v1.7.0 架构模式补充

#### Settings v20 死字段清理

- **原则**：零引用字段 = 死代码，必须删除而非保留。`[HideInInspector]` 不是永久墓地——版本迁移时清理。
- **迁移模式**：`CurrentVersion` bump → migration block 中删除旧字段 → `ResetToDefaults()` 同步更新 → 消费侧 grep 验证零残留
- **假 toggle 检测**：UI 暴露开关但 Service 层不检查该字段 = 假 toggle。`workspaceAutoDetectEnabled` 在 `WorkspaceContextService` 中零引用，用户可关但不生效，必须删除 UI + 字段
- **disabledTools 默认值**：默认值引用不存在的工具名（`["execute_code"]`）= 配置漂移。改为空列表
- **Model Info 显示值**：UI 显示 `settings.maxTokens`（8192）但 API 实际收到 `GetEffectiveMaxTokens()`（10240 = 8192 + 2048）= 显示不一致。改为显示 effective 值，reasoning 启用时分两行

#### VCS 模块修复模式

- **Process 生命周期**：`new Process()` 必须包裹 `using`——即使 `WaitForExit` 返回后，Process 对象的句柄仍需显式释放
- **UIEventsPanel 生命周期**：UI Toolkit 面板订阅 `EditorApplication.update` 或外部事件后，必须在 `DetachFromPanelEvent` 中 `Dispose()`——面板关闭不等于 GC 立即回收
- **Debug.Log 风暴**：`MenuItem` 的 `validate` 方法每次右键菜单展开都会调用全部条目。validate 方法中**禁止** `Debug.Log`，只做轻量 bool 判断
- **SceneView.RepaintAll 去重**：事件发布者不应直接调用 `SceneView.RepaintAll()`——订阅者自行决定是否重绘。`VcsSceneViewUpdateBanner` 已订阅 `StatusChanged` 并自行 `RepaintAll`，发布者重复调用 = 双倍开销
- **设置极简化**：可选组件的设置项应区分"用户运行时受益于调整"与"内部最佳默认值"。VCS 的 6 个操作参数（检查间隔/刷新间隔/banner 开关等）改为 `const`，只保留 `AutoRefreshOnOpen` + `MaxCommitEntries` 两个用户可见设置
- **多 VCS 支持**：Project 窗口右键菜单不应硬编码单一 VCS 工具路径（`TortoiseProc.exe`）。统一走 `VcsExternalToolLauncher.TryStartProcess` + switch 表达式按 `VcsType` 分发

---

### 6.7 v1.7.1 架构模式补充

#### Preferences 目录竞态修复

- `PreferencesFolderPathHelper` 加 `[InitializeOnLoad]` + 静态构造函数
- Assembly 加载时立即创建 `%APPDATA%/Unity/Editor-x.x/Preferences/AgentCore/` 目录
- 修复根因：此前 `EnsureAgentCoreDirectory()` 只在 `SafeSave()` 内调用，被两层 `delayCall` 延迟；Unity `ScriptableSingleton` auto-save 在目录不存在时触发 `Move temp → target` 失败，导致"系统找不到指定的路径" + Editor 卡死
- 现在目录创建时机：assembly load → `[InitializeOnLoad]` static ctor → 立即创建 → 早于任何 ScriptableSingleton auto-save

#### VCS 远端检查开机触发修复

- `VcsRemoteStatusMonitor._lastCheckedUtc` 从 `DateTime.MinValue` 改为 `DateTime.UtcNow`
- 修复根因：MinValue 距今 ~2000 年，首个 `EditorApplication.update` tick 即通过 15 分钟间隔检查，导致打开项目时立即执行远端查询 + SceneView 横幅出现

#### CS0162 编译警告清理 + SessionStorage 日志降级

- 3 个 `const true` 死守卫删除（`SceneViewUpdateBannerEnabled` / `PeriodicRemoteStatusCheckEnabled` / `AutoRefreshCommitListEnabled`）及其对应 const 声明
- `SessionStorage.Load` 的 "Session file not found" 从 `LogWarning` 降级为 `AgentCoreLog.Info`（新装无历史 session 是正常状态）

### 6.8 v1.7.3 架构模式补充

#### 老项目升级安装 Preferences 目录 Move 弹窗修复

- `PreferencesFolderPathHelper` 静态构造函数新增 `AssemblyReloadEvents.beforeAssemblyReload` 回调注册
- `OnBeforeAssemblyReload`：重置 `_cachedDirEnsured = false` → 重新 `EnsureAgentCoreDirectory()`
- 修复根因：v1.7.1 的 `[InitializeOnLoad]` 在 assembly load 时创建目录（覆盖新装场景），但老项目升级时旧版遗留的 pending `ScriptableSingleton` auto-save 在 Domain Unload 尾声触发 `Move temp → target`，此时目录可能尚不存在
- `beforeAssemblyReload` 在 Unity auto-save **之前**执行，确保目录已创建
- 3 个受影响 singleton：`AgentCoreSettings` / `DomainReloadState` / `IndexingSettings`，均用 `FilePathAttribute.Location.PreferencesFolder`

### 6.9 v1.7.5 架构模式补充

#### Preferences 目录路径解析 — 三级兜底

- **根因**：Unity preferences 目录用内部版本号命名（Unity 2021/2022 = `Editor-5.x`），旧 fallback 用 `Application.unityVersion` 提取营销版本号（`2021`），算出 `Editor-2021.x`，路径不匹配
- **三级解析**：① 反射 `InternalEditorUtility.unityPreferencesFolder`（property + method 多签名）→ ② 目录扫描 `%APPDATA%/Unity/Editor-*.x`（取最近修改）→ ③ 硬编码兜底（major ≥ 6000 → `Editor-6.x`，否则 `Editor-5.x`）
- **时机保护**：`AssemblyReloadEvents.beforeAssemblyReload` 回调在 Domain Unload 开始时重置缓存 + 重新确保目录

#### ScriptingDefineSymbols 版本兼容

- **问题**：`PlayerSettings.GetScriptingDefineSymbolsForGroup` / `SetScriptingDefineSymbolsForGroup` 在 Unity 2023.1+ 标记 `[Obsolete]`，Unity 6000.5 已确认生成废弃警告，未来版本可能移除
- **修复**：`ScriptingDefineHelper` 集中封装版本切换
  - `#if UNITY_2023_1_OR_NEWER` → `NamedBuildTarget.FromBuildTargetGroup` + 新 API
  - `#else` → 保留 `ForGroup` API 兼容 Unity 2021.3-2022.3
  - 4 个调用点（OptionalComponentManager × 2 + ReadConsoleTool × 2）统一走 helper
- **旧 fallback 清理**：删除 `ExtractMajorVersion` 和 `BuildFallbackPreferencesFolder`；新增 `AgentCoreLog.Info` 诊断日志记录解析方式和最终路径

### 6.10 v1.7.9 架构模式补充 — UI 交互与视觉审查（P0/P1）

#### IME 输入法 Enter 误发送守卫

- **根因**：Unity UI Toolkit 的 `KeyDownEvent` 无法区分"IME 候选框确认选词的 Enter"与"提交消息的 Enter"。中文/日文/韩文输入法组字期间按 Enter 会被误判为发送，导致半句话被发出。
- **修复**：`IsImeComposing()` 基于 `UnityEngine.Input.compositionString.Length > 0` 判断是否处于组字态；Enter 发送分支加此守卫，组字期间不发送。

#### GetContextBudget 高频遍历移出流式路径

- **根因**：`UpdateContextUsagePanel()` 挂在每个 AgentEvent（含 StreamToken/ReasoningToken 高频事件）末尾，且 `AgentLoop.GetContextBudget()` 每次 `foreach` 遍历整个消息历史估算 token。流式吐字时形成 O(N token × M 消息) 无谓重算（token 数在流式中根本不变）。
- **修复**：新增 `EventAffectsContextBudget(eventType)`，仅在 AssistantMessage/StateChanged/ToolCallCompleted/ToolCallFailed/Error 后刷新面板。ContextUsagePanel.UpdateDisplay 本身只改 text/style 不重建 DOM，故瓶颈在遍历而非渲染。

#### 流式视觉跳变根治 — 统一渲染路径（方案C）

- **根因**：流式阶段用 `FilterStreaming` 显示纯文本（表格 `|a|b|`、代码块裸文本），`SetFinalText` 最终化瞬间切 block 富渲染 → 布局跳变。这是"流式纯文本 / 最终 block"二元硬切换的固有缺陷。
- **修复**：流式与最终化统一走 `RenderTextAsBlocks()` 同一 block 渲染路径 —— 代码块/表格在流式阶段即为深色框/网格，最终化只是用全量文本重渲一次，无模式切换即无跳变。
- **关键辅助**：`CloseDanglingCodeFence()` 检测围栏 ``` 奇偶，流式期为未闭合代码块补一个闭合围栏，使"正在输入的代码块"也能以深色框显示而非等闭合才突然出现。
- **约束保留**：仍用 4000 字符尾部窗口 + 16ms 节流控制 DOM 规模；光标改挂 block 容器末尾（`_blockContainer.Clear()` 会移除光标，每帧重加回末尾）。
- **权衡**：流式每帧重建尾部 block DOM，若尾部窗口含大表格可能比旧单 Label 略重；4000+16ms 约束下判定可接受，如实测掉帧可加"仅结构变化才重建"优化。

#### 色板单一真源 — AgentCoreColors

- **根因**：语义色散落 USS + 多组件 C# 硬编码，同一"成功绿"有三个值（USS #5cb85c / ToolCallCard #4CAF50 / ContextUsagePanel Color(0.2,0.8,0.3)）。
- **修复**：新增 `Editor/UI/AgentCoreColors.cs` 作 C# 单一真源；ToolCallCard / ContextUsagePanel 引用之；ChatWindow.uss 强调蓝 #4A90D9 统一为 #4a86c8。
- **注意**：Unity USS 的 `var()`/`:root` 自定义变量在本项目**未启用**（避免未验证的运行时依赖），USS 侧直接写十六进制字面值，靠注释约定与 C# 真源镜像同步。有意的层次色（气泡填充蓝 #3a5f8a、error 明暗红 #ff7777/#e05555）明确保留不并入语义色。

#### ToolCallCard 超长结果显示截断

- **根因**：只读 multiline TextField 非虚拟化，承载数万字符（读大文件/大 JSON）在展开时构建全量文本网格 → 卡顿。
- **修复**：显示截断到 `DetailsDisplayLimit = 8000` 字符并追加提示，完整原文保留在 `_detailsRaw` 供"复制"按钮取用（复制不受影响）。

### 6.11 v1.7.14 架构模式补充 — ask_user 中途提问（挂起-唤醒完整态）

#### 设计目标与哲学

- **目标**：Agent 遇到影响实现方向的岔路口（多种合理方案、需求歧义、继续做必须基于假设）时，主动停下向用户提问、等用户拍板后再继续，而非凭猜测往可能错误方向无限执行。防跑偏刚需。
- **交互契约**：没人回答就卡住——**不推进、不拒绝、不超时**，界面保持阻断；LLM 直接截断结束当前轮释放；用户事后（可能只是没在窗口/没看到）通过后再唤醒 LLM 继续。
- **完全不碰 SelfChallenge**：走全新独立 `AgentState.WaitingForUserInput` + 独立 partial `AgentLoop.AskUser.cs`。SelfChallenge 是给低性能 LLM 的临时模块（准非维护），不绑定。

#### 复刻 WaitingCompilation 范式（关键：不发明新机制）

现有 `WaitingCompilation`（loop 等 Unity 编译完成再继续）就是"挂起-等外部事件-唤醒"的成熟范本，ask_user 照此复刻：

| WaitingCompilation | ask_user |
|---|---|
| 工具触发编译 → WaitingForCompilation | ask_user 调用 → WaitingForUserInput |
| loop 挂起等编译事件 | loop **截断退出**等用户应答 |
| CompilationFinished → ResumeFromWaitingCompilation | 用户应答 → ResumeFromUserInput |
| TriggerResumeLLMCall | TriggerResumeLLMCall（**复用同一个**） |

#### 数据流（纯函数工具 + loop 层接管 UI）

1. **AskUserTool 是纯函数**：只解析 question/options，返回带 `ToolResult.IsAwaitingUserInput=true`（+ AskUserQuestion/AskUserOptions）的结果。不接触 UI、不阻塞、不 await。通过 `[AgentTool]` 特性 + ToolAutoDiscovery 自动发现注册（无参构造）。
2. **ExecuteToolCallsAsync（Tools.cs）检测标志**：遍历 results 命中 `IsAwaitingUserInput` → `RecordPendingUserQuery(toolCallId, q, opts)`（记录 + 持久化）+ `OnUserQueryRaised?.Invoke(...)` 通知 UI。占位 tool_result（"正在等待应答"）由 BuildToolMessagesWithCompressionAsync 照常写入，保证历史合法（一个 tool_call 恰好一个 result）。
3. **Runner.cs 截断**：ExecuteToolCallsAsync 后检测 `_pendingUserInputToolCallId != null` → `SetState(WaitingForUserInput)` → `return`（干净退出循环，不进下一轮 LLM 调用，不空等）。
4. **UI 应答唤醒**：ChatWindow.AskUser.cs 订阅 `OnUserQueryRaised`，渲染选项面板（无超时·永久阻断）。用户点选项 / 「我自己描述」自由文本 → `ResumeFromUserInput(answer)`：因占位 result 已存在（不能补第二个），改为**追加一条 user 消息**携带答案 → `TriggerResumeLLMCall()` 唤醒（SanitizeMessageHistory 清配对 + 新 assistant turn，不要求末条特定 role）。

#### 跨 domain reload 存活

- `DomainReloadState` 加 3 字段（`_pendingAskUserToolCallId/Question/Options`）+ `SavePendingAskUser`/`ClearPendingAskUser`/`HasPendingAskUser`。RecordPendingUserQuery 时存盘，应答/放弃时清盘。
- `OnBeforeAssemblyReload`：`WaitingForUserInput` 视同**干净挂起**（与 Idle 同路径，仅保存会话，**不标记 `_wasInterrupted`**）——因历史已完整合法，无 pending tool_call 需补 result。
- reload 后 `ChatWindow.CreateGUI` 在 TryRestoreSession 之后调 `TryRestorePendingAskUser` → `AgentLoop.RestorePendingUserInputFromReload`（恢复内存标志 + SetState）→ 重建面板。

#### 关键陷阱

- **占位 result 不可避免**：BuildToolMessagesWithCompressionAsync 对所有 result 无差别写 ChatMessage.Tool。故唤醒**不能补第二个 result**（双 result 非法），必须走追加 user 消息路线。占位文本要明确告知 LLM"真正答案在随后的 user 消息"。
- **时序竞态（已排除）**：OnUserQueryRaised 在 ExecuteToolCallsAsync 内触发，此时 loop 尚未走到 Runner 的 SetState。潜在竞态由 `EditorApplication.delayCall` 排除——HandleUserQueryRaised 把 ShowUserQuery 推迟到下一编辑器 tick，loop 剩余同步代码（含截断）在当前调用栈内跑完，面板渲染必然晚于截断。**delayCall 是关键防线，勿去除。**
- **UI 无 emoji**：选项按钮/提示文本严禁 emoji（SDF 字体渲染成方块，SOUL §3）。

## 7. 编码硬规则

### 7.1 禁止事项

- **禁止** 引用 `UnityEngine.dll` 中的 Runtime-only 类型到工具逻辑中
- **禁止** 在工具的 `ExecuteAsync` 中使用 `Thread.Sleep` 或同步阻塞
- **禁止** 在 `RequiresMainThread = true` 的工具中启动新线程
- **禁止** 硬编码 API Key、URL 或用户特定路径
- **禁止** 使用 `Debug.Log` 进行正常流程日志（使用 `AgentCoreLog.Info/Debug` 替代；仅 `Debug.LogWarning` / `Debug.LogError` 保留用于错误和警告）
- **禁止** 修改 `ToolAutoDiscovery` 的扫描逻辑来手动注册工具
- **禁止** 在工具中直接访问 `ChatWindow` 或其他 UI 组件

### 7.2 必须事项

- **必须** 所有公共方法都有 XML 文档注释
- **必须** 所有工具都有完整的异常处理
- **必须** 使用 `ToolHelpers` 解析参数（不要手动解析 JObject）
- **必须** 使用 `ToolResponse` 构建返回值（不要手动构建 JSON）
- **必须** 新工具通过 `[AgentTool]` + `IAgentTool` 自动注册
- **必须** Cloud 工具的客户端支持 `FromSettings()` 工厂方法
- **必须** 修改 `AgentLoop` 后验证 Domain Reload 恢复仍然正常

### 7.3 C# 语言限制

Unity 2022.3 支持 C# 9.0，但以下特性应**谨慎使用或避免**：

```
 可用: record types, pattern matching, nullable reference types, 
         target-typed new, init-only setters
 谨慎: global using (可能影响 asmdef 隔离)
 不可用: C# 10+ 特性 (file-scoped namespaces 在某些 Unity 版本不稳定)
```

### 7.4 异步编程规范

```csharp
//  正确 — async 方法传递 CancellationToken
public async Task<ToolResult> ExecuteAsync(JObject parameters, CancellationToken ct)
{
    var result = await client.QueryAsync(query, ct);
    return ToolResponse.OkWithData(result).ToToolResult(0);
}

//  正确 — Native 工具同步执行，包装为 Task
public Task<ToolResult> ExecuteAsync(JObject parameters, CancellationToken ct)
{
    var response = HandleSync(parameters);
    return Task.FromResult(response.ToToolResult(0));
}

//  错误 — 不要用 .Result 或 .Wait() 阻塞
public Task<ToolResult> ExecuteAsync(JObject parameters, CancellationToken ct)
{
    var result = client.QueryAsync(query, ct).Result;  // 死锁风险！
    ...
}

//  错误 — 不要忽略 CancellationToken
public async Task<ToolResult> ExecuteAsync(JObject parameters, CancellationToken ct)
{
    var result = await client.QueryAsync(query);  // 缺少 ct！
    ...
}
```

---

## 8. 测试与验证

### 8.1 编译验证

每次修改后必须确认：

1. `AgentCore.Editor` 程序集编译通过（零错误零警告）
2. 不影响用户项目的编译（因为 asmdef 隔离，通常不会）
3. Unity Console 无新增错误

### 8.2 工具验证

新增工具后验证：

1. 工具出现在 `ToolRegistry` 中（可通过 `execute_code` 检查 `ToolRegistry.Instance.Count`）
2. 工具的 `Metadata` 正确（name, description, category, parametersSchema）
3. 各 action 正常工作（手动测试或通过 Chat 窗口测试）
4. 错误参数返回清晰的错误信息（不是异常堆栈）
5. `TOOLS.md.template` 已更新（如果工具面向用户重要）

### 8.3 Domain Reload 验证

修改核心系统后验证：

1. 在 Chat 中发送消息触发工具调用
2. 工具调用过程中修改一个脚本文件（触发 Domain Reload）
3. Domain Reload 完成后，对话应自动恢复
4. 恢复后的对话上下文完整，工具调用结果正确

---

## 9. SOUL.md 修改规范

`SOUL.md` 是 AI Agent 的角色定义（位于 `Editor/Bootstrap/Resources/SOUL.md`）。

> **修改前必须先完整阅读 SOUL.md**，了解当前所有 section 和规则。
> 不要假设 section 编号或内容 — 以实际文件为准。

修改原则：
- 每个 section 独立，不要交叉引用
- 规则要具体可执行，不要抽象描述
- 新增规则放在最相关的 section 中
- 不要删除现有规则，除非确认已过时
- 修改需谨慎，影响所有对话行为

---

## 10. 版本迁移规范

`AgentCoreSettings` 有版本迁移系统。

> **修改前先阅读 `Editor/Config/AgentCoreSettings.cs`**，找到 `CurrentVersion` 常量和 `MigrateSettings()` 方法，了解当前版本号和已有迁移逻辑。

添加新设置字段时：
1. 在 `AgentCoreSettings` 中添加字段（带合理默认值）
2. 递增 `CurrentVersion`（基于实际代码中的当前值）
3. 在 `MigrateSettings()` 中添加迁移逻辑
4. 在对应 `IAgentCoreSettingsPage` 中添加 UI，不得直接修改 `AgentCoreSettingsProvider` 绘制业务设置

### 10.1 Settings 页面开发规则

AgentCore Project Settings 采用 **shell + top-tab pages + cards** 架构（自 v1.0.0 起）。修改设置页时必须遵守：

**核心架构**：
- `AgentCoreSettingsProvider` 是外壳（约 230 行），维护有序 `IAgentCoreSettingsPage[]`，绘制顶部 Tab 导航并分发到当前 page。
- 每个 `IAgentCoreSettingsPage`（当前 6 个：Dashboard / Model & Agent / Context & Memory / Tools & Extensions / Workspace / UI & Diagnostics）负责一屏的业务设置。
- Page 内部使用 `AgentCoreSettingsUi.DrawCard(...)` 或 `AgentCoreSettingsUi.DrawServiceCard(...)` 组织内容，垂直堆叠，每张 card 一个业务子领域。

**约束**：
- **Provider 只做外壳** — `AgentCoreSettingsProvider` 只能负责初始化 `AgentCoreSettingsContext`、构建 page 列表、绘制顶部 Tab、分发到当前 page；禁止新增业务设置 UI、业务 async action 或 page 私有状态字段。
- **设置项必须有归属** — 新增设置项必须归属到一个现有 `IAgentCoreSettingsPage` 的具体 card，或先新增明确的 page；不得新增无归属的顶层 foldout / card。
- **Page 元数据稳定** — 新增 page 必须定义稳定 `Id`、`Title`、`Description`、`Order`。built-in pages 使用 order 100~600；optional component pages 使用 600+。
- **UI 状态集中管理** — foldout、异步运行标记、异步结果等临时状态必须存放在 `AgentCoreSettingsState`，不得回到 Provider / Page 字段。foldout 默认状态优先使用 `FoldoutDefaults` 常量（`ServiceConfig` / `Advanced` / `ReadOnlyInfo` / `ToolCategory`）以保证跨 page 一致性。
- **共享 UI Helper** — API key 行、状态文本、普通 card、服务卡、状态徽标等通用 IMGUI 片段优先复用 `AgentCoreSettingsUi`，避免各 page 重复实现。
- **连接型设置统一模式** — LLM、mem0、LightRAG、Compression LLM 以及后续可选云服务应通过 `AgentCoreSettingsUi.DrawServiceCard(...)` 使用统一的 "Enabled 开关 + 启用后展开明细字段（Endpoint / API Key / Test / Advanced Options）" 结构；禁止让默认关闭的可选服务在 disabled 状态下也把所有字段全展开。
- **Optional Components 职责边界** — 组件启用/禁用归 `ToolsExtensionsSettingsPage` 内的 Optional Components 卡片管理；descriptor 必须位于主程序集可编译代码中，不能强引用被 define gate 的组件程序集类型。
- **Extension Settings 兼容约束** — 当前扩展设置通过 `IAgentCoreSettingsContribution` 挂载在 Tools & Extensions page 内的对应组件卡；新增扩展设置不得绕过 page shell 直接修改 Provider。未来引入 TargetPageId / TargetComponentId V2 接口时，必须迁移到明确挂载点，禁止恢复"Extension Settings 垃圾桶"。
- **Tools 只控制暴露** — Tools & Extensions page 的 Tool Visibility card 只控制 LLM 可见工具、category disable 和 individual tool disable，不负责 optional component 编译/启用。
- **Provider 行数约束** — Provider 应保持 200-300 行左右；如果新增功能导致 Provider 增长，应拆分到 page/service/helper。

> **历史说明**：v1.0.0 之前曾使用左侧导航 + `IAgentCoreSettingsSection` + `AgentCoreSettingsRegistry` 的架构；自迁移到顶部 Tab + Page 后，Section/Registry 已于 v1.4.2 被删除。若在代码中看到相关命名的历史文档，请以本节的 Page/Card 描述为准。

---

## 11. Skills 路由表

> 开发 AgentCore 时，根据任务类型加载对应的 Skill 文件。
> 每个 Skill 都遵循"先发现现有模式，再编写新代码"的原则。

| 任务场景 | Skill | 路径 |
|----------|-------|------|
| 新增 Native 工具 | `add-native-tool` | `.agents/skills/add-native-tool.md` |
| 新增 Cloud 工具 | `add-cloud-tool` | `.agents/skills/add-cloud-tool.md` |
| 修改 AgentLoop 核心 | `modify-agent-loop` | `.agents/skills/modify-agent-loop.md` |
| 修改 Bootstrap/SOUL | `modify-bootstrap` | `.agents/skills/modify-bootstrap.md` |
| 新增设置项 | `add-settings` | `.agents/skills/add-settings.md` |
| UI 开发 | `modify-ui` | `.agents/skills/modify-ui.md` |

---


---

## 12. 开发流程与版本管理规范

> 本章节定义 AgentCore 项目的**开发协作流程**和**版本管理规则**。
> 适用于 AI 辅助开发场景：用户（Product Owner）提供需求，AI（Developer）执行实现。
> **核心原则**：文档对齐优先于代码实现，版本可追溯优先于开发速度。

### 12.1 开发流程概览

```
用户提出需求/方向
    ↓
AI 更新 ROADMAP / 编写详细设计文档
    ↓
用户 Review & 确认文档（完全对齐）
    ↓
AI 执行代码实现
    ↓
用户测试功能 & 提交 Bug 清单
    ↓
AI 修复 Bug / 用户确认通过
    ↓
同步更新：package.json 版本号 + CHANGELOG + ROADMAP 状态
    ↓
进入下一个需求循环
```

**关键规则**：
- **禁止跳过文档对齐直接编码**。大功能必须经用户确认设计文档后方可实现。
- **禁止一次性提交大量未验证代码**。按功能模块分批实现、分批测试。
- **禁止修改版本号不同步文档**。版本号变更必须伴随 CHANGELOG 和 ROADMAP 更新。
- **禁止绕过 LLM/Agent 治理层扩展能力**。新增工具、扩大默认工具暴露、MCP、Plugin、文件写入自动化或代码执行能力变更，必须先对齐 `plans/llm-agent-architecture-remediation-plan.md`。

### 12.2 版本号管理规则（SemVer）

AgentCore 遵循 [Semantic Versioning](https://semver.org/)：`MAJOR.MINOR.PATCH`

| 升级类型 | 触发条件 | 示例 |
|----------|---------|------|
| **Patch `+1`** | Bug 修复、小优化、文档修正、工具 action 的小修补 | `0.5.1` → `0.5.2` |
| **Minor `+1`** | 新增功能、新工具、UI 增强、能力补齐 | `0.5.1` → `0.6.0` |
| **Major `+1`** | 破坏性架构变更、移除/重命名公开 API、asmdef 结构变更 | `0.x.x` → `1.0.0` |

**特殊规则**：
- `0.x.x` 阶段（当前）：Minor 升级表示一个 Phase 的完成或重大功能集合的交付
- 版本号**三处同步更新**：`package.json` → `CHANGELOG.md` → `ROADMAP.md`
- 不允许出现"代码已改、版本未动"的状态

### 12.3 文档层级与职责

| 层级 | 文件 | 维护者 | 更新时机 | 作用 |
|------|------|--------|---------|------|
| **方向层** | `plans/ROADMAP.md` | AI + 用户共同 | 需求变更、Phase 完成 | 长期规划，用户在此阶段干预方向 |
| **治理层** | `plans/llm-agent-architecture-remediation-plan.md` | AI + 用户共同 | 工具边界、自治能力、MCP/Plugin、上下文治理变更前 | LLM/Agent 架构安全收口准则；Phase 7/8 的前置约束 |
| **设计层** | `plans/xxx-feature-plan.md` | AI | 功能编码前 | 单个功能的详细设计方案，用户对齐后确认；不得与治理层冲突 |
| **规范层** | `AGENTS.md` | AI | 架构规则变更 | 编码硬约束，所有代码必须遵守 |
| **变更层** | `CHANGELOG.md` | AI | 每次版本发布 | 用户可见的变更记录，按 SemVer 分组 |
| **规范层** | `README.md` | AI | 重大功能交付 | 项目对外描述，保持简洁 |

### 12.4 编码前对齐确认清单

每次进入"代码实现"阶段前，AI 必须向用户提供以下信息，**用户确认后方可编码**：

| 确认项 | 必须提供的内容 |
|--------|---------------|
| **目标功能** | 本次要实现什么，范围边界在哪里（不做什么是关键） |
| **涉及文件** | 会新建哪些文件、修改哪些现有文件 |
| **版本号** | 本次升级到什么版本（基于 SemVer 规则） |
| **变更日志草稿** | CHANGELOG 条目预览（Added/Changed/Fixed） |
| **验收标准** | 功能完成后的测试 checklist（至少 3 条） |
| **风险点** | 可能引入回归的地方、需要特别测试的场景 |
| **文档影响** | 是否需要更新 SOUL.md / TOOLS.md.template / AGENTS.md / ROADMAP / remediation plan |

**例外**：纯 Bug 修复（单行修改、参数修正等）可跳过对齐，但仍需更新 CHANGELOG。

### 12.5 版本号同步更新规则

每次版本号变更时，以下文件**必须同步更新**（缺一不可）：

1. **`package.json`** — 修改 `"version"` 字段
2. **`CHANGELOG.md`** — 在顶部新增版本节，记录变更内容
3. **`ROADMAP.md`** — 将对应任务标记为 `[x]`，更新里程碑状态
4. **（如适用）`AGENTS.md`** — 如果本次变更引入了新的架构规则或编码约束
5. **（如适用）`README.md`** — 如果新增了用户可见的核心功能

**CHANGELOG 格式要求**：

```markdown
## [x.y.z] - YYYY-MM-DD

### Added
- 新增功能/工具描述

### Changed
- 行为变更描述

### Fixed
- Bug 修复描述
```

### 12.6 测试验收轮次定义

每个功能的交付必须经过以下验收轮次（由用户执行）：

| 轮次 | 目标 | 测试重点 |
|------|------|---------|
| **Round 1** | Happy Path | 功能基本可用，正常参数下的主流程通过 |
| **Round 2** | 边界与容错 | 空输入、错误参数、服务不可用、权限不足 |
| **Round 3** | 核心链路 | Domain Reload、会话切换、多轮工具调用、取消操作 |
| **Round 4** | 实际场景 | 用户在真实项目中的使用场景跑一遍 |

**Bug 修复流程**：
1. 用户给出编号 Bug 清单（`Bug-1`, `Bug-2`...）
2. AI 逐条修复
3. 每轮修复后更新 Patch 版本号（如 `0.5.1` → `0.5.1-hotfix-1`，最终合并为 `0.5.2`）
4. 用户重新测试验证

### 12.7 编码过程中的沟通规则

| 场景 | AI 行为 |
|------|---------|
| 实现过程中发现设计盲区 | 立即暂停编码，向用户说明情况并请求决策 |
| 发现与现有代码冲突的模式 | 优先遵循 AGENTS.md 规范，冲突时提请用户裁决 |
| 需要修改已确认的设计 | 必须重新走"对齐确认"流程，不得擅自变更范围 |
| 遇到 Unity 版本相关 API 不确定 | 使用 `execute_code` 验证或明确标注 `[inferred — verify]` |
| 完成代码实现 | 主动汇报变更文件清单、版本号、待测试要点 |

### 12.8 补充禁止事项

以下行为在开发流程中**严格禁止**：

- **禁止** 跳过文档对齐确认直接编写功能代码
- **禁止** 单次提交超过 500 行未经验证的代码变更
- **禁止** 修改版本号时遗漏 CHANGELOG 或 ROADMAP 的同步更新
- **禁止** 在 Bug 修复轮次中混入新功能开发
- **禁止** 未经用户确认删除或重命名现有工具/文件
- **禁止** 使用未经项目验证的第三方依赖或 NuGet 包
- **禁止** 在完成 Tool Risk Policy + WorkspacePathPolicy 强制接入前，把内部工具通过 MCP/Plugin/默认工具列表扩大暴露

---

> 维护原则：本文件描述的是**架构模式和规则**，不是文件清单。
> 项目演进时，更新规则和模式描述，但不要维护具体的文件列表。
> 让 LLM 通过工具动态发现实际代码结构。
> **开发流程变更时，同步更新本章节（§12）和 `plans/ROADMAP.md` 中的流程说明。**

