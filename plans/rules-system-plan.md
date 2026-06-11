# 规则系统设计方案 (v0.9.6)

> **状态**: 待确认 | **目标版本**: v0.9.6 | **对应 ROADMAP**: §2.3 任务 6.4.1 + 6.4.2
>
> **范围**: `.agentcore/rules.md` 文件支持 + 规则内容自动注入 System Prompt + 分层注入机制
>
> **不在本次范围内**: SmartToolRecommender（6.4.3）、响应式建议（6.4.4）

---

## 1. 背景与动机

### 1.1 现有 Bootstrap 链

当前 Bootstrap 加载顺序（`BootstrapContext.CompileSystemPrompt()`）：

```
SOUL.md (内置不可变)
  + SOUL.ext.md (用户行为规则扩展，可选)
  + TOOLS.md (自动生成工具列表)
  + PROJECT.md (自动收集项目信息)
  + PROJECT.md (用户可编辑，项目约定 + 个人偏好)
```

### 1.2 现有用户文件体系

| 文件 | 路径 | 用途 | VCS |
|------|------|------|-----|
| `PROJECT.md` | `<projectRoot>/AgentCore/PROJECT.md` | 项目约定 + 个人偏好 | 建议提交 |
| `SOUL.ext.md` | `<projectRoot>/AgentCore/SOUL.ext.md` | Agent 行为规则扩展 | 建议提交 |

### 1.3 问题与需求

**现有 PROJECT.md 的局限**：
- 是一个单一文件，无法按模块/目录分层管理规则
- 不支持"只在特定目录下生效"的局部规则
- 团队成员无法为不同子系统维护独立的编码规范

**规则系统的目标**：
- 支持在 WorkspaceRoot 级别定义全局规则（`.agentcore/rules.md`）
- 支持在 UnityRoot 级别定义 Unity 工程规则（`AgentCore/rules.md`）
- 支持在 ScopeRoot 级别定义局部规则（`<scope>/.agentcore/rules.md`）
- 所有规则自动注入 System Prompt，Agent 无需手动读取

---

## 2. 设计方案

### 2.1 规则文件位置与优先级

规则系统采用**三层分层注入**，从外到内优先级递增：

```
WorkspaceRoot/
├── .agentcore/
│   └── rules.md          ← 层 1: Workspace 全局规则（最低优先级）
│
├── unity/                ← UnityRoot（相对路径示例）
│   └── AgentCore/
│       └── rules.md      ← 层 2: Unity 工程规则（中优先级）
│
├── gamemodes/            ← ScopeRoot 示例
│   └── .agentcore/
│       └── rules.md      ← 层 3: Scope 局部规则（最高优先级）
│
└── tools/                ← 另一个 ScopeRoot
    └── .agentcore/
        └── rules.md      ← 层 3: Scope 局部规则
```

**v0.9.6 实现范围**：
- **层 1**（WorkspaceRoot 全局规则）：**必须实现**
- **层 2**（UnityRoot 规则）：**必须实现**
- **层 3**（ScopeRoot 局部规则）：**可选，按需实现**（本次 v0.9.6 暂不实现，留 v0.9.7+）

> **决策依据**：层 1 + 层 2 覆盖 90% 的使用场景，层 3 复杂度高（需要感知当前操作的 Scope），延后实现。

### 2.2 规则文件格式

规则文件为标准 Markdown，无特殊格式要求：

```markdown
# 项目编码规范

## 命名约定
- 类名使用 PascalCase
- 私有字段使用 _camelCase 前缀
- 接口名以 I 开头

## 架构约定
- 禁止在 Runtime 代码中使用 UnityEditor API
- 所有 MonoBehaviour 必须有对应的 Editor 脚本
- 使用 Addressables 管理资源，禁止 Resources.Load

## 工作流约定
- 修改脚本后必须等待编译通过再继续
- 提交前运行所有 Unit Tests
```

**格式约束**：
- 纯 Markdown，无特殊语法
- 文件大小建议 < 50KB（超过时 Bootstrap token 预算压力大）
- 注释行（`<!--` ... `-->`）会被保留（不过滤，与 PROJECT.md 不同）

### 2.3 Bootstrap 链扩展

在现有 Bootstrap 链中，规则内容注入在 PROJECT.md(用户) 之后：

```
SOUL.md (内置不可变)
  + SOUL.ext.md (用户行为规则扩展，可选)
  + TOOLS.md (自动生成工具列表)
  + PROJECT.md (自动收集项目信息)
  + PROJECT.md (用户可编辑，项目约定 + 个人偏好)
  + RULES (新增) ← WorkspaceRoot rules.md + UnityRoot rules.md，分层注入
```

**注入格式**（在 System Prompt 中）：

```
---

## 项目规则（来自 .agentcore/rules.md）

[WorkspaceRoot 规则内容]

---

## Unity 工程规则（来自 AgentCore/rules.md）

[UnityRoot 规则内容]
```

### 2.4 Settings 控制

在 `AgentCoreSettings` 中新增：

```csharp
// --- 规则系统配置 ---
[Header("Rules System")]
[Tooltip("启用规则系统（自动加载 .agentcore/rules.md 并注入 System Prompt）")]
public bool rulesEnabled = true;
```

**不需要**更多细粒度控制（如"禁用某层规则"），保持简单。

---

## 3. 实现细节

### 3.1 新增文件

| 文件 | 说明 |
|------|------|
| `Editor/Bootstrap/RulesLoader.cs` | 规则文件加载器，负责发现和读取各层规则文件 |

**不新增**独立的 `RulesContext` 类，直接在 `BootstrapContext` 中添加 `Rules` 属性。

### 3.2 RulesLoader 设计

```csharp
namespace AgentCore.Editor.Bootstrap
{
    /// <summary>
    /// 规则文件加载器。
    /// 按层级发现并加载 .agentcore/rules.md 文件，供 Bootstrap 注入 System Prompt。
    /// </summary>
    public class RulesLoader
    {
        /// <summary>
        /// 加载所有层级的规则文件。
        /// 返回按层级排序的规则内容列表（WorkspaceRoot → UnityRoot）。
        /// </summary>
        public List<RulesEntry> LoadAll(WorkspaceContext workspace)
        {
            // 1. WorkspaceRoot/.agentcore/rules.md
            // 2. UnityRoot/AgentCore/rules.md
            // 返回非空的条目列表
        }
    }

    /// <summary>单个规则文件的内容和元数据。</summary>
    public class RulesEntry
    {
        public string Label { get; set; }    // 用于 System Prompt 标题，如 "项目规则"
        public string FilePath { get; set; } // 绝对路径（用于日志）
        public string Content { get; set; } // 文件内容
    }
}
```

### 3.3 BootstrapContext 扩展

在 [`BootstrapContext`](Editor/Bootstrap/BootstrapContext.cs) 中新增：

```csharp
/// <summary>
/// RULES — 分层规则文件内容（WorkspaceRoot + UnityRoot）
/// 注入在 PROJECT.md(用户) 之后
/// </summary>
public List<RulesEntry> Rules { get; set; } = new List<RulesEntry>();
```

在 `CompileSystemPrompt()` 中追加：

```csharp
// 4. RULES — 分层规则注入
foreach (var rule in Rules)
{
    if (!string.IsNullOrWhiteSpace(rule.Content))
    {
        sb.AppendLine("\n---\n");
        sb.AppendLine($"## {rule.Label}\n");
        sb.AppendLine(rule.Content);
    }
}
```

### 3.4 BootstrapLoader 扩展

在 [`BootstrapLoader.Load()`](Editor/Bootstrap/BootstrapLoader.cs:34) 中，在加载 PROJECT.md(用户) 之后添加：

```csharp
// 4. RULES — 分层规则注入（可选）
if (settings.rulesEnabled)
{
    var workspaceContext = WorkspaceContextService.Instance.GetContext();
    if (workspaceContext != null && workspaceContext.IsValid)
    {
        var rulesLoader = new RulesLoader();
        context.Rules = rulesLoader.LoadAll(workspaceContext);
        var rulesCount = context.Rules.Count(r => !string.IsNullOrWhiteSpace(r.Content));
        if (rulesCount > 0)
        {
            Debug.Log($"[AgentCore] Loaded {rulesCount} rules file(s)");
        }
    }
}
```

### 3.5 规则文件路径解析

| 层级 | 查找路径 | 标题 |
|------|---------|------|
| WorkspaceRoot | `{WorkspaceRoot}/.agentcore/rules.md` | `项目规则（来自 .agentcore/rules.md）` |
| UnityRoot | `{UnityRoot}/AgentCore/rules.md` | `Unity 工程规则（来自 AgentCore/rules.md）` |

**路径查找逻辑**：
- 如果 WorkspaceRoot == UnityRoot（无 SVN 场景），只加载一次（避免重复）
- 文件不存在时静默跳过（不报错）
- 文件存在但为空时跳过

### 3.6 AgentCoreSettings 版本迁移

```csharp
// v8 -> v9: 新增规则系统字段（使用声明时默认值，无需额外迁移）
if (settingsVersion < 9)
{
    Debug.Log("[AgentCore] Settings migrated v8→v9: rules system settings initialized");
}
```

`CurrentVersion` 从 8 升级到 9。

---

## 4. Settings UI

在 `ContextSettingsSection`（或 `AgentSettingsSection`）中新增规则系统开关：

```
[Rules System]
[x] Enable Rules System
    Automatically loads .agentcore/rules.md from WorkspaceRoot and AgentCore/rules.md
    from UnityRoot, injecting them into the System Prompt.

    WorkspaceRoot rules: <path or "Not found">
    UnityRoot rules:     <path or "Not found">

    [Open WorkspaceRoot rules.md]  [Open UnityRoot rules.md]
```

**UI 行为**：
- 显示当前规则文件的实际路径（或"未找到"）
- 提供"打开"按钮（用 `EditorUtility.OpenWithDefaultApp` 打开文件）
- 如果文件不存在，"打开"按钮改为"创建"（写入模板内容后打开）

---

## 5. manage_workspace_config 工具扩展

现有 `manage_workspace_config` 工具支持 `read_project_config` / `write_project_config` / `read_soul_extension` / `write_soul_extension`。

**新增 actions**：

| Action | 说明 |
|--------|------|
| `read_rules` | 读取指定层级的规则文件内容 |
| `write_rules` | 写入指定层级的规则文件内容 |
| `get_rules_paths` | 获取各层级规则文件的路径和存在状态 |

**参数设计**：

```json
{
  "action": "read_rules",
  "scope": "workspace"  // "workspace" | "unity"（默认 "workspace"）
}

{
  "action": "write_rules",
  "scope": "workspace",
  "content": "# 项目规则\n..."
}

{
  "action": "get_rules_paths"
  // 返回: { workspace: { path: "...", exists: true }, unity: { path: "...", exists: false } }
}
```

**SOUL.md §13 更新**：在 `manage_workspace_config` 的说明中补充规则文件的使用场景。

---

## 6. SOUL.md 更新

在 §13（Workspace Configuration Management）中补充规则文件说明：

```markdown
**RULES.md** — 项目编码规范与架构约定（分层规则文件）。
- WorkspaceRoot 层：`<WorkspaceRoot>/.agentcore/rules.md` — 全局规则，适用于整个 Workspace
- UnityRoot 层：`<UnityRoot>/AgentCore/rules.md` — Unity 工程规则，适用于 Unity 项目
- 规则内容在每次对话开始时自动注入 System Prompt
- 使用 `manage_workspace_config` (action: `read_rules` / `write_rules`) 读写规则文件
```

**何时主动写入规则**：
- 用户说"把这个约定加到项目规则里" → `write_rules`（scope: workspace）
- 用户说"把这个 Unity 规范加到工程规则里" → `write_rules`（scope: unity）
- 用户说"查看当前规则" → `read_rules`

---

## 7. 涉及文件清单

### 新增文件

| 文件 | 说明 |
|------|------|
| `Editor/Bootstrap/RulesLoader.cs` | 规则文件加载器 |
| `Editor/Bootstrap/RulesLoader.cs.meta` | Unity meta 文件 |

### 修改文件

| 文件 | 修改内容 |
|------|---------|
| `Editor/Bootstrap/BootstrapContext.cs` | 新增 `Rules` 属性；`CompileSystemPrompt()` 追加规则注入 |
| `Editor/Bootstrap/BootstrapLoader.cs` | `Load()` 方法中调用 `RulesLoader` |
| `Editor/Config/AgentCoreSettings.cs` | 新增 `rulesEnabled` 字段；`CurrentVersion` 8→9；`MigrateSettings()` 新增 v9 迁移 |
| `Editor/Bootstrap/Resources/SOUL.md` | §13 补充规则文件说明和使用场景 |
| `Editor/Bootstrap/Resources/TOOLS.md.template` | 更新 `manage_workspace_config` 工具说明，补充 rules actions |
| `Editor/Tools/Native/Workspace/ManageWorkspaceConfigTool.cs` | 新增 `read_rules`、`write_rules`、`get_rules_paths` actions |
| `Editor/Config/Settings/Sections/ContextSettingsSection.cs` | 新增规则系统 UI（开关 + 路径显示 + 打开/创建按钮） |
| `plans/ROADMAP.md` | 更新 6.4.1、6.4.2 状态为进行中；v0.9.6 里程碑描述 |

---

## 8. 版本号

**目标版本**: `v0.9.6`（Minor 升级，新增功能）

**CHANGELOG 草稿**：

```markdown
## [0.9.6] - 2026-06-XX

### Added
- 规则系统：支持 `.agentcore/rules.md`（WorkspaceRoot 全局规则）和 `AgentCore/rules.md`（UnityRoot 工程规则）自动注入 System Prompt
- `manage_workspace_config` 工具新增 `read_rules`、`write_rules`、`get_rules_paths` actions
- Settings > Context 页面新增规则系统开关和规则文件路径显示
- SOUL.md §13 补充规则文件使用指南

### Changed
- Bootstrap 链扩展：在 PROJECT.md(用户) 之后追加分层规则注入
- AgentCoreSettings 版本升级至 v9
```

---

## 9. 验收标准

### Round 1 — Happy Path

- [ ] 在 `WorkspaceRoot/.agentcore/rules.md` 创建规则文件，重启 AgentCore，System Prompt 中包含规则内容
- [ ] 在 `UnityRoot/AgentCore/rules.md` 创建规则文件，重启 AgentCore，System Prompt 中包含两层规则
- [ ] `manage_workspace_config` (action: `get_rules_paths`) 返回正确的路径和存在状态
- [ ] `manage_workspace_config` (action: `read_rules`, scope: workspace) 返回规则内容
- [ ] `manage_workspace_config` (action: `write_rules`) 写入后文件内容正确

### Round 2 — 边界与容错

- [ ] 规则文件不存在时，Bootstrap 正常加载，无错误日志
- [ ] 规则文件为空时，不注入空内容到 System Prompt
- [ ] WorkspaceRoot == UnityRoot 时，只加载一次规则（不重复注入）
- [ ] `rulesEnabled = false` 时，规则文件不被加载
- [ ] 规则文件超大（> 100KB）时，加载成功但 Bootstrap 日志中有 token 预算警告

### Round 3 — 核心链路

- [ ] Domain Reload 后，规则内容在新会话中正确注入
- [ ] Settings 页面的"打开"按钮能正确打开规则文件
- [ ] Settings 页面的"创建"按钮能创建模板文件并打开
- [ ] 规则文件路径在 Settings 中正确显示（存在/不存在状态）

### Round 4 — 实际场景

- [ ] 在规则文件中写入"禁止使用 Resources.Load"，Agent 在对话中自动遵守该规则
- [ ] 修改规则文件后，下一次对话（新 Bootstrap 加载）中规则更新生效

---

## 10. 风险点

| 风险 | 影响 | 缓解措施 |
|------|------|---------|
| WorkspaceContext 未初始化时 RulesLoader 被调用 | Bootstrap 加载失败 | 在 `rulesLoader.LoadAll()` 前检查 `workspaceContext.IsValid`，失败时静默跳过 |
| 规则文件过大导致 token 超限 | System Prompt 超出模型上下文窗口 | 加载时记录 token 估算，超过 2000 tokens 时输出 Warning 日志 |
| WorkspaceRoot == UnityRoot 时重复注入 | System Prompt 冗余 | `RulesLoader` 中比较路径，相同时只加载一次 |
| `manage_workspace_config` 工具 actions 增多导致 schema 复杂 | LLM 调用错误率上升 | 保持 action 名称语义清晰，在 TOOLS.md.template 中补充示例 |

---

## 11. 不做什么（范围边界）

- **不实现 ScopeRoot 层规则**（层 3）：复杂度高，延后到 v0.9.7+
- **不实现规则优先级覆盖**：各层规则全部注入，不做合并/去重
- **不实现规则文件的 include/import 语法**：保持简单的单文件格式
- **不实现规则的条件激活**（如"只在修改 UI 代码时激活"）：超出本次范围
- **不修改 SOUL.md §1-§10 的核心行为约束**：规则系统只追加，不替换

---

> **下一步**: 用户确认本设计文档后，开始编码实现。
> **关联任务**: ROADMAP 6.4.1（.agentcore/rules.md 支持）+ 6.4.2（规则自动注入）
