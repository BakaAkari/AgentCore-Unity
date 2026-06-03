# Bootstrap 链路重构设计方案

**版本**: v1.0
**状态**: 已实现
**目标版本**: v0.9.x（与代码库索引功能同期或之后）
**关联文档**: [codebase-indexing-phase1-plan.md](codebase-indexing-phase1-plan.md)

---

## 0. 背景与动机

### 0.1 当前 Bootstrap 链路（5层，待废弃）

```
SOUL.md (内置) → TOOLS.md (自动生成) → PROJECT.md (自动收集)
    → MEMORY.md (用户可编辑) → USER.md (用户可编辑)
```

当前实现见 [`BootstrapLoader.cs`](../Editor/Bootstrap/BootstrapLoader.cs) 和 [`BootstrapContext.cs`](../Editor/Bootstrap/BootstrapContext.cs)。

### 0.2 现有设计的问题

**问题 1：MEMORY.md 职责冗余**

AgentCore 已有两套动态知识系统：
- **mem0 (OpenMemory)**：`AutoMemoryStrategy` 自动从对话中提取记忆，存入向量数据库，每次对话开始时自动注入相关记忆
- **LightRAG (RAGLight)**：文档知识库，通过 `manage_knowledge` 工具手动管理，支持语义检索

MEMORY.md 的"项目知识积累"职责已被上述两套系统完全覆盖，继续保留只会造成：
- 内容重复（用户需要在 MEMORY.md 和 mem0/LightRAG 之间手动同步）
- 每次对话都全量注入（不管内容是否相关），浪费 token

**问题 2：VCS 冲突风险**

MEMORY.md 和 USER.md 使用完全相同的路径解析逻辑，但两者的 VCS 共享需求截然不同：

| 文件 | 内容性质 | 应该 VCS 提交？ |
|------|---------|----------------|
| MEMORY.md | 项目知识、团队约定 | 应该（团队共享） |
| USER.md | 个人偏好、工作习惯 | 不应该（个人私有） |

当前代码没有任何 VCS 策略区分，团队成员如果都提交了 MEMORY.md 会产生合并冲突，如果都提交了 USER.md 会互相覆盖个人偏好。

**问题 3：链路层次过多，职责边界模糊**

5层链路中 MEMORY.md 和 USER.md 的职责边界模糊，用户经常不清楚"这个信息应该写在哪个文件"。

### 0.3 重构目标

1. **简化链路**：5层 → 3层（+可选扩展层）
2. **消除冗余**：彻底移除 MEMORY.md 和 USER.md，合并为单一 PROJECT.md
3. **明确 VCS 策略**：PROJECT.md 明确建议 VCS 提交，个人偏好写在 PROJECT.md 内的独立 section
4. **保护 SOUL 不可变性**：SOUL.md 的核心行为约束永远不被用户覆盖
5. **为代码骨架注入预留位置**：SKELETON.md 作为可选第4层

---

## 1. 目标架构（3层 + 可选扩展）

### 1.1 新 Bootstrap 链路

```
SOUL.md (内置不可变)
    → TOOLS.md (自动生成)
        → PROJECT.md (用户可编辑，团队共享，建议 VCS 提交)
            → [SKELETON.md] (自动生成，代码骨架，可选，来自代码索引功能)
```

**可选扩展层**：
```
SOUL.md + [SOUL.ext.md] (用户追加，可选，项目特定行为规则扩展)
```

### 1.2 各层职责

| 层 | 文件 | 来源 | VCS | 职责 |
|----|------|------|-----|------|
| 1 | `SOUL.md` | 包内嵌入 | 不适用 | Agent 核心行为约束（不可变） |
| 1+ | `SOUL.ext.md` | 用户创建（可选） | 建议提交 | 项目特定行为规则扩展（追加模式） |
| 2 | `TOOLS.md` | 自动生成 | 不适用 | 工具列表与使用指南 |
| 3 | `PROJECT.md` | 用户创建 | **建议提交** | 项目约定 + 个人偏好（合并） |
| 4 | `SKELETON.md` | 自动生成（可选） | 不提交 | 代码库骨架（来自代码索引） |

### 1.3 废弃的层（直接删除，不保留兼容）

| 废弃文件 | 替代方案 |
|---------|---------|
| `MEMORY.md` | mem0（动态记忆）+ LightRAG（文档知识库） |
| `USER.md` | 合并入 `PROJECT.md` 的 `## Personal Preferences` section |

---

## 2. PROJECT.md 详细设计

### 2.1 文件定位

- **路径优先级**：
  1. `{UnityProjectRoot}/PROJECT.md`
  2. `{UnityProjectRoot}/AgentCore/PROJECT.md`
- **默认创建路径**：`{UnityProjectRoot}/AgentCore/PROJECT.md`
- **VCS 建议**：提交到版本控制（团队共享）

### 2.2 文件结构模板

```markdown
# AgentCore Project Configuration
<!--
  此文件由 AgentCore 生成，供团队维护。
  建议提交到 VCS（Git/SVN/Perforce）以便团队共享。
  
  个人偏好（Personal Preferences section）可选择不提交：
  在 .gitignore / .p4ignore / svn:ignore 中排除此文件，
  或仅将 Project Conventions section 的内容提交。
-->

## Project Conventions
<!--
  团队约定：命名规范、架构决策、禁止事项、工作流程等。
  这里的内容会注入到每次对话的 System Prompt 中。
  
  示例：
  - 本项目使用 Mirror 网络框架，禁止使用 UNET
  - 资源管理使用 Addressables，禁止使用 Resources.Load
  - 命名规范：类名 PascalCase，私有字段 _camelCase
-->


## Personal Preferences
<!--
  个人偏好：回复语言、代码风格偏好、工作习惯等。
  建议不提交到 VCS（在 .gitignore 中排除）。
  
  示例：
  - 请用英文回复
  - 代码注释使用中文
  - 每次修改前先展示 diff
-->

```

---

## 3. SOUL.ext.md 扩展机制（可选）

### 3.1 设计原则

SOUL.ext.md 采用**追加模式**，不替换内置 SOUL.md：

```
System Prompt 中 Soul 部分 = SOUL.md(内置) + "\n\n" + SOUL.ext.md(用户追加，如果存在)
```

这样：
- 内置 SOUL 的 §1-§10（Anti-Hallucination、禁止 emoji 等）永远不会被破坏
- 用户可以追加项目特定规则（如"本项目禁止使用 Addressables"）

### 3.2 适用场景

SOUL.ext.md 适合以下内容（PROJECT.md 不适合的）：
- 追加新的 Unity Hard Rules（§3 扩展）
- 追加新的工具使用约束（§4 扩展）
- 追加项目特定的格式约束（§10 扩展）

**不适合**放在 SOUL.ext.md 的内容：
- 项目技术栈约定 → 放 PROJECT.md
- 个人偏好 → 放 PROJECT.md 的 Personal Preferences section
- 动态知识 → 放 mem0 或 LightRAG

### 3.3 文件路径

- `{UnityProjectRoot}/AgentCore/SOUL.ext.md`
- VCS 建议：提交（团队共享的行为规则扩展）

---

## 4. SKELETON.md 注入位置

代码骨架（来自代码索引功能，详见 [codebase-indexing-phase1-plan.md §16.5](codebase-indexing-phase1-plan.md)）注入为第4层：

```
SOUL → TOOLS → PROJECT → SKELETON(可选，自动生成)
```

**注入条件**：
- 代码索引功能已启用（`settings.codeIndexingEnabled`）
- `Library/AgentCore/workspace-skeleton.md` 文件存在
- 文件大小在 token 预算内（默认上限 2000 tokens）

---

## 5. 代码变更清单

### 5.1 BootstrapContext.cs — 完整重写

**移除字段**：
- `Memory` (string) — MEMORY.md 内容（直接删除）
- `User` (string) — USER.md 内容（直接删除）

**新增字段**：
- `Workspace` (string) — PROJECT.md 内容
- `SoulExtension` (string) — SOUL.ext.md 内容（可选，追加到 Soul）
- `Skeleton` (string) — SKELETON.md 内容（可选，代码骨架）

**新的 `CompileSystemPrompt()` 编译顺序**：

```csharp
public string CompileSystemPrompt()
{
    var sb = new StringBuilder();

    // 1. SOUL — 角色定义（必须）
    if (!string.IsNullOrEmpty(Soul))
    {
        sb.AppendLine(Soul);
        // 1+. SOUL 扩展（可选，追加）
        if (!string.IsNullOrEmpty(SoulExtension))
        {
            sb.AppendLine();
            sb.AppendLine(SoulExtension);
        }
    }

    // 2. TOOLS — 工具指南（必须）
    if (!string.IsNullOrEmpty(Tools))
    {
        sb.AppendLine("\n---\n");
        sb.AppendLine(Tools);
    }

    // 3. PROJECT — 项目上下文（自动生成）
    if (!string.IsNullOrEmpty(Project))
    {
        sb.AppendLine("\n---\n");
        sb.AppendLine("## 当前项目信息\n");
        sb.AppendLine(Project);
    }

    // 3+. WORKSPACE — 项目配置（用户可编辑）
    if (!string.IsNullOrEmpty(Workspace))
    {
        sb.AppendLine("\n---\n");
        sb.AppendLine("## 项目配置（来自 PROJECT.md）\n");
        sb.AppendLine(Workspace);
    }

    // 4. SKELETON — 代码库骨架（可选）
    if (!string.IsNullOrEmpty(Skeleton))
    {
        sb.AppendLine("\n---\n");
        sb.AppendLine("## 代码库骨架（自动生成）\n");
        sb.AppendLine(Skeleton);
    }

    return sb.ToString();
}
```

### 5.2 BootstrapLoader.cs — 修改 Load() 方法

**移除**：
- `LoadUserFile("MEMORY.md")` 调用及相关日志
- `LoadUserFile("USER.md")` 调用及相关日志
- `context.Memory` 赋值
- `context.User` 赋值

**新增**：
- `LoadUserFile("PROJECT.md")` → `context.Workspace`
- `LoadUserFile("SOUL.ext.md")` → `context.SoulExtension`（可选）
- `LoadSkeletonFile()` → `context.Skeleton`（可选，从 `Library/AgentCore/workspace-skeleton.md` 读取）

**修改类注释**：
```csharp
/// 加载顺序：
/// 1. SOUL.md — 内置角色定义（包内资源，不可变）
/// 1+. SOUL.ext.md — 用户行为规则扩展（可选，追加到 SOUL）
/// 2. TOOLS.md — 工具使用指南（从模板生成）
/// 3. PROJECT.md — 项目约定与个人偏好（用户可编辑，建议 VCS 提交）
/// 4. SKELETON.md — 代码库骨架（自动生成，可选，不提交 VCS）
```

**修改 Bootstrap 日志**：
```csharp
Debug.Log($"[AgentCore] Bootstrap loaded: ~{tokenEstimate} tokens " +
          $"(SOUL={!string.IsNullOrEmpty(context.Soul)}, " +
          $"SOUL.ext={!string.IsNullOrEmpty(context.SoulExtension)}, " +
          $"TOOLS={!string.IsNullOrEmpty(context.Tools)}, " +
          $"PROJECT={!string.IsNullOrEmpty(context.Project)}, " +
          $"WORKSPACE={!string.IsNullOrEmpty(context.Workspace)}, " +
          $"SKELETON={!string.IsNullOrEmpty(context.Skeleton)})");
```

### 5.3 Settings UI 变更（4 个文件）

搜索发现 MEMORY.md/USER.md 的 UI 逻辑在以下 **4 个文件**中重复实现，全部需要修改：

#### 5.3.1 `ContextSettingsSection.cs`（主要入口）

**`Draw()` 方法 — "User Files" card 修改**：
- card 标题改为 `"Project Files"`
- card 描述改为 `"User-editable files included in the bootstrap prompt. PROJECT.md is recommended for VCS commit; SOUL.ext.md extends agent behavior rules."`
- 移除 `DrawUserFileRow("MEMORY.md", ...)` 调用
- 移除 `DrawUserFileRow("USER.md", ...)` 调用
- 新增 `DrawUserFileRow("PROJECT.md", "项目约定与个人偏好 — 团队共享，建议 VCS 提交")` 调用
- 新增 `DrawUserFileRow("SOUL.ext.md", "Agent 行为规则扩展 — 追加到内置 SOUL，建议 VCS 提交")` 调用

**`GenerateUserFileTemplate()` 方法修改**：
- 移除 `"MEMORY.md"` 分支
- 移除默认分支（USER.md 旧模板）
- 新增 `"PROJECT.md"` 分支：返回含 `## Project Conventions` 和 `## Personal Preferences` 两个 section 的模板
- 新增 `"SOUL.ext.md"` 分支：返回含追加规则示例的模板

#### 5.3.2 `ContextMemorySettingsPage.cs`（Context & Memory 页面）

**"Context Sources" card 修改**：
- 移除 `DrawUserFileRow("MEMORY.md", ...)` 调用（第61行）
- 移除 `DrawUserFileRow("USER.md", ...)` 调用（第62行）
- 新增 `DrawUserFileRow("PROJECT.md", ...)` 和 `DrawUserFileRow("SOUL.ext.md", ...)` 调用
- `GenerateUserFileTemplate()` 同 §5.3.1 修改

#### 5.3.3 `DiagnosticsSettingsSection.cs`（诊断页面）

**"User Context Files" card 修改**：
- card 标题改为 `"Project Context Files"`
- 移除 `"Open MEMORY.md"` 按钮（第54行）
- 移除 `"Open USER.md"` 按钮（第59行）
- 新增 `"Open PROJECT.md"` 按钮
- 新增 `"Open SOUL.ext.md"` 按钮（可选，标注为 Advanced）
- `GenerateUserFileTemplate()` 同 §5.3.1 修改

#### 5.3.4 `UiDiagnosticsSettingsPage.cs`（UI 诊断页面）

**"Context Files" 区域修改**（第265-277行）：
- 移除 `"Open MEMORY.md"` 按钮（第268行）
- 移除 `"Open USER.md"` 按钮（第273行）
- 新增 `"Open PROJECT.md"` 按钮
- 新增 `"Open SOUL.ext.md"` 按钮
- `GenerateUserFileTemplate()` 同 §5.3.1 修改

#### 5.3.5 重复代码处理策略

`GenerateUserFileTemplate()` 在 4 个文件中完全重复。重构时将其**移入 `BootstrapLoader`** 作为 public static 方法，4 个 UI 文件改为调用 `BootstrapLoader.GenerateUserFileTemplate(fileName)`，消除重复。

### 5.4 BootstrapLoader.cs — 公共静态方法更新

除 §5.2 的 `Load()` 方法修改外，以下 public static 方法也需要更新：

**`FindUserFilePath(string fileName)` 修改**：
- 支持新文件名：`"PROJECT.md"`、`"SOUL.ext.md"`
- 移除对 `"MEMORY.md"`、`"USER.md"` 的路径解析逻辑（直接删除，不保留）

**`GetDefaultUserFilePath(string fileName)` 修改**：
- 同上，支持新文件名，移除旧文件名

**新增 `GenerateUserFileTemplate(string fileName)` public static 方法**：
- 从 4 个 UI 文件中提取，集中到 `BootstrapLoader`
- `"PROJECT.md"` 分支：返回含 `## Project Conventions` 和 `## Personal Preferences` 的模板
- `"SOUL.ext.md"` 分支：返回含追加规则示例的模板
- 默认分支：返回空字符串（不再有 MEMORY.md/USER.md 分支）

### 5.5 ProjectContextCollector.cs — 注释更新

**第62行注释修改**：
```csharp
// 旧注释（删除）：
/// 收集扩展项目信息，用于 MEMORY.md 初始化。

// 新注释：
/// 收集扩展项目信息，用于 PROJECT.md 自动生成和 Bootstrap 上下文注入。
```

### 5.6 新增文件

| 文件 | 说明 |
|------|------|
| `Editor/Bootstrap/Resources/PROJECT.md.template` | PROJECT.md 的初始模板（用于首次创建） |

### 5.7 不需要修改的文件

- `Editor/Bootstrap/Resources/SOUL.md` — 不变
- `Editor/Bootstrap/Resources/TOOLS.md.template` — 不变
- `Editor/Core/AgentLoop.cs` — 不变（Bootstrap 加载在 `Initialize()` 中，接口不变）
- `Editor/Session/AutoMemoryStrategy.cs` — 不变（mem0 独立运作）

---

## 6. System Prompt 结构对比

### 当前结构（5层，废弃）

```
[SOUL.md 内容]
---
[TOOLS.md 内容]
---
## 当前项目信息
[PROJECT.md 自动收集内容]
---
## 项目知识（来自 MEMORY.md）
[MEMORY.md 内容]
---
## 用户偏好（来自 USER.md）
[USER.md 内容]
```

### 重构后结构（3层 + 可选）

```
[SOUL.md 内容]
[SOUL.ext.md 内容，如果存在]
---
[TOOLS.md 内容]
---
## 当前项目信息
[PROJECT.md 自动收集内容]
---
## 项目配置（来自 PROJECT.md）
[PROJECT.md 用户内容]
---
## 代码库骨架（自动生成）
[SKELETON.md 内容，如果存在且代码索引已启用]
```

---

## 7. 验收标准

### Round 1 — Happy Path
- [ ] 创建 PROJECT.md 后，内容正确注入到 System Prompt
- [ ] 没有 PROJECT.md 时，Workspace 为空，System Prompt 不包含该 section
- [ ] Bootstrap 日志正确显示新的层名称（WORKSPACE 替代 MEMORY/USER）

### Round 2 — 边界与容错
- [ ] PROJECT.md 为空文件时，不注入任何内容
- [ ] PROJECT.md 只有模板注释时，`IsTemplateOnly()` 正确识别并跳过
- [ ] SOUL.ext.md 不存在时，不影响正常启动
- [ ] SOUL.ext.md 存在时，内容追加到 SOUL.md 之后（不替换）

### Round 3 — 代码骨架集成（代码索引功能完成后）
- [ ] SKELETON.md 存在时，正确注入为第4层
- [ ] SKELETON.md 超过 token 预算时，自动截断并记录警告
- [ ] 代码索引功能禁用时，SKELETON.md 不注入

---

## 8. 与其他设计文档的关系

| 文档 | 关系 |
|------|------|
| [codebase-indexing-phase1-plan.md §16.5](codebase-indexing-phase1-plan.md) | SKELETON.md 注入位置由本文档定义（第4层） |
| [ROADMAP.md §3.2](ROADMAP.md) | 代码库索引功能，SKELETON.md 的来源 |
| [AGENTS.md §6.2](../AGENTS.md) | Bootstrap 系统修改规则，已同步更新为新链路 |
