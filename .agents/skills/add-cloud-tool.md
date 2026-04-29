# Skill: 新增 Cloud 工具

> 当需要为 AgentCore 添加一个通过 HTTP 调用外部服务的云端工具时，加载此 Skill。
> Cloud 工具由两部分组成：**Client（HTTP 客户端）** + **Tool（工具包装）**。

---

## 前置检查

1. 确认外部服务的 API 文档和端点
2. 确认认证方式（API Key / Bearer Token / 无认证）
3. 确认是否需要在 `AgentCoreSettings` 中添加配置项（参考 `add-settings` skill）
4. 确认工具名称（snake_case，全局唯一）

---

## 发现现有模式

**在编写新工具前，必须先阅读现有 Cloud 工具来学习当前项目的实际模式：**

1. 列出 `Editor/Tools/Cloud/` 目录，了解现有的 Client 和 Tool 文件
2. 选择一对 Client + Tool 文件完整阅读（通常以 `*Client.cs` + `*Tool.cs` 成对出现）
3. 阅读 `Editor/Utils/HttpClientFactory.cs` — 了解 HTTP 客户端工厂的用法
4. 阅读 `Editor/Config/AgentCoreSettings.cs` — 了解现有设置字段的模式
5. 阅读 `Editor/Tools/Infrastructure/` 下的基础设施文件（同 Native 工具）

> **关键原则**: 以实际代码为准。如果现有 Cloud 工具的模式与本文档模板不同，以现有代码为准。

---

## 步骤

### Step 1: 创建 Client 类

文件路径：`Editor/Tools/Cloud/<Service>Client.cs`

命名空间：参考现有 Client 的命名空间（可能是 `AgentCore.Editor.Cloud` 或 `AgentCore.Editor.Tools.Cloud`）

核心要素：
- 构造函数接收 `baseUrl` 和可选的 `apiKey`
- **必须有 `FromSettings()` 静态工厂方法** — 从 `AgentCoreSettings` 读取配置，未配置时返回 null
- **必须有 `TestConnectionAsync()` 方法** — 用于 Settings UI 中的连接测试
- 使用 `HttpClientFactory.GetClient()` 获取共享 HttpClient
- 响应数据模型标记 `[Serializable]`
- 所有 async 方法传递 `CancellationToken`

### Step 2: 创建 Tool 类

文件路径：`Editor/Tools/Cloud/<Service>Tool.cs`

命名空间：`AgentCore.Editor.Tools.Cloud`

核心要素：
- `RequiresMainThread = false` — Cloud 工具不需要主线程
- 使用 `async/await`（不是 `Task.FromResult`）
- 在 `ExecuteAsync` 开头通过 `FromSettings()` 获取 Client，null 时返回清晰错误
- 所有 async 方法传递 `CancellationToken`

### Step 3: 添加设置项（如果需要）

参考 `add-settings` skill，在 `AgentCoreSettings` 中添加：
- `<service>BaseUrl` — 服务地址
- `<service>ApiKey` — API Key（如果需要认证，使用 `SecureKeyStorage`）

### Step 4: 关键检查点

| 检查项 | 要求 |
|--------|------|
| `RequiresMainThread` | **必须为 `false`** — Cloud 工具不需要主线程 |
| `FromSettings()` | Client 必须支持此工厂方法 |
| 未配置检查 | `FromSettings()` 返回 null 时返回清晰的错误信息 |
| `CancellationToken` | 所有 async 方法必须传递 ct |
| `HttpClientFactory` | 使用共享 HttpClient，不要 `new HttpClient()` |
| 异常处理 | 网络错误返回用户友好的错误信息 |
| 数据模型 | 响应类标记 `[Serializable]` |

### Step 5: 编译验证

1. 确认编译通过
2. 确认工具出现在 `ToolRegistry` 中
3. 测试连接正常时的行为
4. 测试连接失败时的错误信息

---

## Cloud vs Native 关键区别

| 方面 | Native 工具 | Cloud 工具 |
|------|------------|-----------|
| `RequiresMainThread` | `true` | `false` |
| 执行方式 | 同步 + `Task.FromResult` | `async/await` |
| 依赖 | Unity API | HTTP Client |
| 配置 | 通常无需额外配置 | 需要 URL/Key 设置 |
| 错误处理 | Unity 异常 | 网络异常 + HTTP 状态码 |
| 命名空间 | `AgentCore.Editor.Tools.Native.<Cat>` | `AgentCore.Editor.Tools.Cloud` |
| Client 类 | 无 | 必须有独立的 Client 类 |

---

## 如何找到参考实现

不要依赖固定的文件列表。按以下方式发现参考：

1. **现有 Cloud 工具**: 列出 `Editor/Tools/Cloud/` 目录，找到 `*Client.cs` + `*Tool.cs` 配对
2. **Settings 集成**: 阅读 `AgentCoreSettings.cs`，搜索现有的 `BaseUrl` 和 `ApiKey` 字段
3. **HTTP 模式**: 阅读 `HttpClientFactory.cs` 了解请求创建方式
4. **连接测试 UI**: 阅读 `AgentCoreSettingsProvider.cs`，搜索 `TestConnection` 方法了解 UI 集成模式
