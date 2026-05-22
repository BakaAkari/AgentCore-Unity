# Skill: 修改 Bootstrap / SOUL

> 当需要修改 Bootstrap 系统（SOUL.md、TOOLS.md.template、BootstrapLoader、ProjectContextCollector）时，加载此 Skill。
> Bootstrap 系统决定了 AI Agent 的行为基础。

---

## 风险等级： 中

Bootstrap 修改影响：
- AI Agent 的角色定义和行为模式（SOUL.md）
- 工具使用指南（TOOLS.md.template）
- 项目上下文收集（ProjectContextCollector）
- System Prompt 的编译和 Token 消耗

---

## 前置检查

**修改前必须先阅读相关文件，理解当前实现：**

1. 阅读 `Editor/Bootstrap/BootstrapLoader.cs` — 了解加载顺序和模板处理逻辑
2. 阅读 `Editor/Bootstrap/BootstrapContext.cs` — 了解 System Prompt 编译方式
3. 根据修改目标，阅读对应的资源文件

---

## Bootstrap 加载顺序

```
BootstrapLoader.Load()
  → 1. SOUL.md          — 角色定义（内置资源）
  → 2. TOOLS.md.template — 工具指南（模板 + 动态工具列表）
  → 3. PROJECT           — 项目上下文（ProjectContextCollector 自动收集）
  → 4. MEMORY.md         — 用户本地知识（可选，用户可编辑）
  → 5. USER.md           — 用户偏好（可选，用户可编辑）

BootstrapContext.CompileSystemPrompt()
  → 按顺序拼接所有内容，用 "---" 分隔
```

> **注意**: 加载顺序和拼接方式请以实际代码为准。阅读 `BootstrapLoader.cs` 确认最新实现。

---

## 修改 SOUL.md

### 文件位置

`Editor/Bootstrap/Resources/SOUL.md`

### 修改前

先完整阅读 `SOUL.md`，了解当前的 section 结构和内容。

### 修改规则

1. **每个 section 独立** — 不要交叉引用其他 section
2. **规则要具体可执行** — 不要抽象描述，要给出明确的行为指令
3. **新增规则放在最相关的 section** — 不要随意创建新 section
4. **不要删除现有规则** — 除非确认已过时
5. **注意 Token 消耗** — SOUL.md 是 System Prompt 的主体，过长会挤占对话空间
6. **中文编写** — SOUL.md 使用中文

### 示例：添加新规则

```markdown
## §N 对应的 Section 标题

... 现有规则 ...

### 新增规则标题
- 规则描述（具体、可执行）
- 正确做法示例
- 错误做法示例（如果需要）
```

---

## 修改 TOOLS.md.template

### 文件位置

`Editor/Bootstrap/Resources/TOOLS.md.template`

### 修改前

先完整阅读 `TOOLS.md.template`，了解当前结构和模板变量。

### 模板变量

阅读 `BootstrapLoader.cs` 中的模板处理逻辑，了解支持的变量（如 `{{ACTIVE_TOOLS_LIST}}`）。

### 修改规则

1. **新增工具后更新** — 在对应 section 添加使用说明
2. **添加 Tool Selection Guide 条目** — 帮助 LLM 选择正确的工具
3. **保持简洁** — 每个工具的说明不超过 3-5 行
4. **面向 LLM** — 描述要让 LLM 理解何时使用、如何使用

### 示例：为新工具添加指南

```markdown
### <Section Name>
- Use `<tool_name>` to [功能描述].
  - `action1`: [说明]
  - `action2`: [说明]

### Tool Selection Guide
- **Want to [用户意图]?** → `<tool_name>` (action: `<action>`)
```

---

## 修改 ProjectContextCollector

### 文件位置

`Editor/Bootstrap/ProjectContextCollector.cs`

### 修改前

先完整阅读 `ProjectContextCollector.cs`，了解当前收集的内容和方式。

### 修改规则

1. **不要收集敏感信息** — 不收集 API Key、用户路径等
2. **控制输出大小** — 目录树有深度限制，包列表有数量限制
3. **异常安全** — 任何收集步骤失败不应影响其他步骤
4. **性能意识** — 不要扫描 Library/ 或大型目录

---

## 修改 BootstrapLoader

### 文件位置

`Editor/Bootstrap/BootstrapLoader.cs`

### 修改规则

1. **加载顺序不可变** — 先阅读代码确认当前顺序，保持不变
2. **每步独立** — 任何一步失败不应影响其他步骤
3. **用户文件搜索路径** — 阅读代码了解当前的搜索路径逻辑
4. **模板检测** — 了解 `IsTemplateOnly()` 的检测逻辑

---

## 修改后验证

- [ ] System Prompt 正确编译（在 Chat 窗口中验证 Agent 行为）
- [ ] Token 估算合理（不超过模型上下文窗口的 30%）
- [ ] 新增规则不与现有规则冲突
- [ ] 模板变量正确替换
- [ ] ProjectContextCollector 不收集敏感信息

---

## 相关文件发现

修改 Bootstrap 时，列出 `Editor/Bootstrap/` 目录了解所有相关文件，包括 `Resources/` 子目录中的资源文件。
