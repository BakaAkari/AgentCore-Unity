# Skill: 新增 Native 工具

> 当需要为 AgentCore 添加一个直接调用 Unity API 的新工具时，加载此 Skill。

---

## 前置检查

1. 确认工具名称（snake_case，全局唯一）
2. 确认工具分类 — 先扫描 `Editor/Tools/Native/` 下的子目录，了解现有分类
3. 确认是否修改脚本（`MayModifyScripts`）
4. 确认目标目录：`Editor/Tools/Native/<Category>/`

---

## 发现现有模式

**在编写新工具前，必须先阅读现有工具来学习当前项目的实际模式：**

1. 列出 `Editor/Tools/Native/` 下所有子目录，了解分类体系
2. 在目标分类目录中选择一个现有工具文件，完整阅读其实现
3. 阅读 `Editor/Tools/Infrastructure/` 下的基础设施文件：
   - `AgentToolAttribute.cs` — `[AgentTool]` 特性定义
   - `ToolHelpers.cs` — 参数解析辅助方法
   - `ToolResponse.cs` — 统一响应格式
4. 阅读 `Editor/Tools/IAgentTool.cs` — 工具接口和 `ToolMetadata` / `ToolResult` 定义

> **关键原则**: 以实际代码为准，不要依赖本文档中的模板。如果现有代码的模式与本文档不同，以现有代码为准。

---

## 步骤

### Step 1: 创建工具文件

文件路径：`Editor/Tools/Native/<Category>/<ToolName>Tool.cs`

命名空间：`AgentCore.Editor.Tools.Native.<Category>`

### Step 2: 实现标准结构

遵循从现有工具中观察到的模式，核心结构包括：

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Tools.Infrastructure;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Tools.Native.<Category>
{
    /// <summary>
    /// [工具功能描述]
    /// </summary>
    [AgentTool("<tool_name>",
        Description = "[面向 LLM 的描述 — 清晰说明能做什么]",
        Category = "<Category>",
        RequiresMainThread = true,
        MayModifyScripts = false)]
    public class <ToolName>Tool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""action1"", ""action2""],
                    ""description"": ""要执行的操作""
                }
            },
            ""required"": [""action""]
        }");

        public ToolMetadata Metadata => new ToolMetadata(
            name: "<tool_name>",
            description: "[与 AgentTool 特性完全一致的描述]",
            category: "<Category>",
            parametersSchema: _parametersSchema,
            requiresMainThread: true
        );

        public Task<ToolResult> ExecuteAsync(JObject parameters, CancellationToken cancellationToken = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ToolResponse response;

            try
            {
                var action = ToolHelpers.GetRequiredString(parameters, "action").ToLowerInvariant();

                switch (action)
                {
                    case "action1":
                        response = HandleAction1(parameters);
                        break;
                    default:
                        response = ToolResponse.Fail($"Unknown action: {action}. Valid: action1, action2");
                        break;
                }
            }
            catch (Exception ex)
            {
                response = ToolResponse.Fail($"Error: {ex.Message}");
            }

            sw.Stop();
            return Task.FromResult(response.ToToolResult(sw.Elapsed.TotalMilliseconds));
        }

        private ToolResponse HandleAction1(JObject parameters)
        {
            // 使用 ToolHelpers 解析参数
            // 调用 Unity API
            // 返回 ToolResponse.Ok / OkWithData / Fail
            return ToolResponse.Ok("操作完成");
        }
    }
}
```

### Step 3: 关键检查点

| 检查项 | 要求 |
|--------|------|
| `[AgentTool]` 特性 | Name, Description, Category, RequiresMainThread, MayModifyScripts 全部正确 |
| `Metadata` 属性 | 与 `[AgentTool]` 特性的值**完全一致** |
| `_parametersSchema` | JSON Schema 格式正确，`required` 字段完整 |
| 命名空间 | `AgentCore.Editor.Tools.Native.<Category>` |
| 参数解析 | 使用 `ToolHelpers` 中的方法（先阅读 `ToolHelpers.cs` 了解可用方法） |
| 返回值 | 使用 `ToolResponse.Ok` / `OkWithData` / `Fail` |
| 计时 | `Stopwatch` 计时并传给 `ToToolResult` |
| 异常处理 | 外层 try-catch，返回 `ToolResponse.Fail` |
| default 分支 | 列出所有有效 action |
| 无参构造函数 | 不要定义带参数的构造函数（`ToolAutoDiscovery` 需要） |

### Step 4: 更新 TOOLS.md.template（如果工具面向用户重要）

阅读 `Editor/Bootstrap/Resources/TOOLS.md.template`，在对应 section 中添加工具使用说明。

### Step 5: 编译验证

1. 确认 `AgentCore.Editor` 程序集编译通过
2. Unity Console 无新增错误
3. 工具自动出现在 `ToolRegistry` 中（无需手动注册）

---

## 常见错误

| 错误 | 原因 | 修复 |
|------|------|------|
| 工具未被发现 | 缺少 `[AgentTool]` 特性或未实现 `IAgentTool` | 检查特性和接口 |
| Metadata 不匹配 | `[AgentTool]` 和 `Metadata` 属性值不一致 | 保持完全一致 |
| 参数解析失败 | 未使用 `ToolHelpers` | 替换为 `ToolHelpers` 方法 |
| 主线程异常 | 在非主线程调用 Unity API | 确保 `RequiresMainThread = true` |
| JSON Schema 无效 | `_parametersSchema` 格式错误 | 验证 JSON 格式 |

---

## 如何找到参考实现

不要依赖固定的文件列表。按以下方式发现参考：

1. **同分类工具**: 列出 `Editor/Tools/Native/<目标Category>/` 目录，选择最相似的工具阅读
2. **复杂参数处理**: 搜索 `ToolHelpers.Parse` 或 `GetOptionalObject` 的使用
3. **反射访问内部 API**: 搜索 `System.Reflection` 的使用
4. **安全检查模式**: 搜索 `ContainsDangerousPattern` 或类似安全检查
5. **标准 CRUD 模式**: 选择任意包含 create/delete/modify action 的工具
