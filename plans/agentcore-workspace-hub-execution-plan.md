# AgentCore 单主窗口 Hub 执行方案

> 状态：草案 v2，架构决策已稳定，可进入实施阶段。
> 核心决策：AgentCore 只保留一个主工作窗口，Hub 固定 Chat / Knowledge / Memory 三个模块，Diagnostics 和 Tools 保留在 Settings。
> 相关文件：`Editor/UI/ChatWindow.cs`、`Editor/UI/ChatWindow.uxml`、`Editor/UI/ChatWindow.uss`、`Editor/UI/Components/`、`Editor/Config/AgentCoreSettingsProvider.cs`。

---

## 1. 背景

目标主入口统一为 `Window/AgentCore`，直接打开 AgentCore Workspace。

随着 RAG、mem0、工具管理、诊断、未来代码索引等能力加入，如果每个能力都新增一个窗口，会出现：

```text
Window/AgentCore/Knowledge Base
Window/AgentCore/Memory
Window/AgentCore/Tools
Window/AgentCore/Diagnostics
Window/AgentCore/Code Index
```

这会带来几个问题：

1. 用户注意力被多个窗口分散。
2. 功能之间难以共享上下文。
3. AgentCore 从“统一 Agent 产品”退化成“一组零散 Editor 工具”。
4. 菜单入口越来越多，长期不可维护。

因此，本方案将 AgentCore 设计为一个单主窗口工作台：

```text
Window/AgentCore  →  AgentCore Workspace
```

ChatWindow 既承担 Chat 功能，也承担功能 Hub 的容器职责。

---

## 2. 目标

### 2.1 产品目标

1. 用户只需要打开一个 AgentCore 主窗口。
2. Chat 仍然是核心工作流。
3. RAG、Memory、Tools、Diagnostics、未来 Code Index 都在主窗口内作为 Hub 模块出现。
4. Settings 只负责配置，不负责业务 action。
5. 功能 action 和状态反馈集中在 Hub，不散落在 Settings 或多个窗口。

### 2.2 工程目标

1. 不把所有逻辑继续塞进 `ChatWindow.cs`。
2. 通过 `Editor/UI/Components/` 下的独立组件实现各 Hub 模块。
3. `ChatWindow` 只做装配、导航和事件路由。
4. 每个 Hub Panel 可以独立开发、测试、替换。
5. 第一阶段尽量少动 AgentLoop 和核心会话逻辑。

---

## 3. 非目标

第一阶段不做：

1. 不重写整个 ChatWindow UI。
2. 不引入多窗口架构。
3. 不把 RAG action 放进 Settings。
4. 不做完整代码索引。
5. 不做复杂 dashboard。
6. 不改变现有 Chat 发送消息、会话恢复、Domain Reload 逻辑。

---

## 4. 信息架构

### 4.1 主窗口定位

主窗口名称统一为 “AgentCore”。

菜单入口目标：

```text
Window/AgentCore
```

内部标题可显示：

```text
AgentCore
```

不保留旧的 Chat 子菜单入口，所有文档、菜单和用户引导统一使用 `Window/AgentCore`。

### 4.2 主窗口布局

采用**双层左侧栏 + 主内容区**三列布局：

```text
┌──────────────────────────────────────────────────────────────────┐
│ AgentCore                                                         │
├────────┬──────────────────┬───────────────────────────────────────┤
│  Hub   │  Context Sidebar │  Main Content                         │
│  Rail  │                  │                                       │
│  ~52px │  ~200px          │  flex-grow                            │
│        │                  │                                       │
│  Chat  │  <- Chat 模块时  │  对话消息流                           │
│        │    显示会话列表  │                                       │
│        │                  │                                       │
│  Know  │  <- Knowledge 时 │  Knowledge Base Panel                 │
│        │    隐藏或显示    │                                       │
│        │    最近索引摘要  │                                       │
│        │                  │                                       │
│  Mem   │  <- Memory 时    │  Memory Panel                         │
│        │    显示记忆列表  │                                       │
│        │                  ├───────────────────────────────────────┤
│  ----  │                  │  Chat Input（仅 Chat 模块激活时显示） │
│  Set   │                  │                                       │
└────────┴──────────────────┴───────────────────────────────────────┘
```

**三列说明：**

| 列 | 名称 | 宽度 | 内容 |
|----|------|------|------|
| 左一 | Hub Rail | ~52px（固定） | 模块导航，始终可见 |
| 左二 | Context Sidebar | ~200px（可折叠） | 随激活模块动态切换内容 |
| 右 | Main Content | flex-grow | 当前模块的主工作区 |

**切换逻辑：** 点击 Hub Rail 中的模块，Context Sidebar 和 Main Content 同时切换为该模块对应的内容。

**Context Sidebar 随模块变化规则：**

| 激活模块 | Context Sidebar 内容 | Main Content 内容 |
|----------|----------------------|-------------------|
| Chat | 会话列表（现有 session list 迁移至此） | 对话消息流 + Chat Input |
| Knowledge | 隐藏，或显示最近索引摘要 | Knowledge Base Panel |
| Memory | 记忆条目列表 | Memory Panel |

**Hub Rail 布局（共 3 个模块导航 + 1 个固定按钮）：**

每个模块导航项包含短标签，激活时高亮。点击已激活项可折叠 Context Sidebar（节省空间）。

```text
Chat          <- 模块导航，顶部对齐
Know          <- 模块导航
Mem           <- 模块导航
──────        <- 分隔线，flex-grow 撑开
Settings      <- 固定在底部，点击打开 AgentCore Settings 页面
```

Settings 按钮固定在 Hub Rail 底部，不参与模块激活逻辑，点击后调用 `SettingsService.OpenProjectSettings("AgentCore")` 打开设置页面。

**Diagnostics 和 Tools 不作为 Hub 模块**，保留在 Settings 界面中管理。

**与现有会话列表的兼容方案：**

当前 `ChatWindow` 左侧已有会话列表（session list）。迁移策略：

1. 会话列表整体移入 Context Sidebar，作为 Chat 模块的上下文内容。
2. Hub Rail 激活 Chat 时，Context Sidebar 显示会话列表，行为与现在完全一致。
3. 切换到其他模块时，Context Sidebar 内容替换，会话列表暂时隐藏（不销毁）。
4. 切回 Chat 时，会话列表恢复，滚动位置保持。

### 4.3 Hub 模块

Hub 固定三个模块：

| 模块 | 作用 | 第一阶段是否实现 |
|------|------|------------------|
| Chat | 对话、会话列表、工具调用展示 | 已有，保留 |
| Knowledge | LightRAG 文档索引与查询 | 是 |
| Memory | mem0 记忆查看与管理 | 后续 |

不作为 Hub 模块（保留在 Settings）：

- Diagnostics：连接测试、状态检查 → Settings > About & Diagnostics
- Tools：工具启用/禁用与预设 → Settings > Tool Management

---

## 5. UI 组件拆分

### 5.1 新增组件建议

```text
Editor/UI/Components/HubRail.cs
Editor/UI/Components/KnowledgeBasePanel.cs
```

后续扩展：

```text
Editor/UI/Components/MemoryPanel.cs
```

### 5.2 `ChatWindow` 职责

`ChatWindow` 保留职责：

1. 创建主窗口。
2. 初始化 AgentLoop。
3. 管理会话恢复。
4. 装配主布局。
5. 响应 Hub 导航切换。
6. 将 AgentEvent 分发给 Chat UI 组件。

`ChatWindow` 不应继续新增大量业务逻辑，例如：

- LightRAG 文件上传细节。
- mem0 用户管理细节。
- 工具分类管理细节。
- 代码索引扫描细节。

这些逻辑应该在独立 Panel 或 service/helper 中。

### 5.3 `KnowledgeBasePanel` 职责

第一阶段职责：

1. 显示 LightRAG Enabled 状态。
2. 显示 Endpoint。
3. 提供 `Test Connection`。
4. 提供 `Open Settings`。
5. 提供 `Index Document...`。
6. 显示 Last Index Result。

第二阶段职责：

1. `Index Project Docs`。
2. 文件拖拽索引。
3. Query Test。

第三阶段职责：

1. `Index Folder...`。
2. 批量索引进度。
3. 索引历史。

---

## 6. Knowledge Panel 详细设计

### 6.1 第一阶段 UI

```text
Knowledge Base

Status
  LightRAG: Enabled / Disabled
  Endpoint: http://localhost:9621
  Connection: Unknown / Connected / Failed
  [Test Connection] [Open Settings]

Add Knowledge
  [Index Document...]

Last Index Result
  No document indexed yet.
```

### 6.2 索引单文档流程

流程：

```text
用户点击 Index Document...
  → EditorUtility.OpenFilePanel
  → 校验文件路径、大小、扩展名、排除目录
  → 如果文件在项目外，显示确认或拒绝
  → 调用 LightRAGClient.IndexFileAsync
  → 显示结果
```

建议第一阶段只允许项目根目录内文件，避免隐私风险。

### 6.3 状态反馈

Panel 内维护状态：

```text
ConnectionStatus: Unknown / Testing / Connected / Failed
IndexStatus: Idle / Indexing / Success / Failed
LastIndexSummary: string
LastIndexItems: list
```

这些 UI 状态第一阶段可以不持久化。

---

## 7. Settings 职责边界

Settings 保留配置：

- LLM Endpoint / API Key / Model。
- mem0 Enabled / Endpoint / API Key。
- LightRAG Enabled / Endpoint / API Key。
- RAG include/exclude patterns。
- RAG max file size / max batch files。
- Auto query policy。
- Agent 行为参数。
- UI 偏好。

Settings 不承载 action：

- 不上传文档。
- 不批量索引。
- 不做 Query Test。
- 不展示索引历史。

Settings 可以提供导航：

```text
[Open AgentCore]
```

或：

```text
[Open Knowledge Panel]
```

点击后打开 ChatWindow 并切换到 Knowledge Hub。

---

## 8. 菜单策略

### 8.1 第一阶段

新增/调整主入口：

```text
Window/AgentCore
```

该入口直接打开 AgentCore Workspace。

不新增：

```text
Window/AgentCore/Knowledge Base
Window/AgentCore/Memory
Window/AgentCore/Tools
```

### 8.2 禁止新增或保留的入口

不保留 Chat 子菜单入口。

不建议新增：

```text
Window/AgentCore/Open
```

因为 `Window/AgentCore` 本身已经足够明确，且应作为唯一主入口。

---

## 9. 技术实现阶段

### Phase Hub-1：主窗口 Hub 骨架

目标：不改变 Chat 核心行为的前提下，引入三列 Hub 容器，并将现有会话列表迁移至 Context Sidebar。

任务：

1. 调整 `ChatWindow.uxml`，实现三列布局：Hub Rail（~52px）+ Context Sidebar（~200px）+ Main Content（flex-grow）。
2. 新增 `HubRail` 组件：模块导航（Chat / Knowledge），激活项高亮；底部固定 Settings 按钮。
3. Hub Rail 底部 Settings 按钮调用 `SettingsService.OpenProjectSettings("AgentCore")` 打开设置页面。
4. 将现有会话列表从 `ChatWindow` 左侧迁移至 Context Sidebar，作为 Chat 模块的上下文内容。
5. Context Sidebar 支持按激活模块动态切换内容（初期：Chat → 会话列表，Knowledge → 隐藏）。
6. 默认激活 Chat 模块，Context Sidebar 显示会话列表，行为与现在完全一致。
7. Chat Input 仅在 Chat 模块激活时显示。
8. 记住上次激活的模块和 Context Sidebar 展开/折叠状态（持久化到 `EditorPrefs`）。

验收：

- 打开 `Window/AgentCore` 后进入 AgentCore Workspace，默认显示 Chat 模块。
- Hub Rail 显示 Chat 和 Knowledge 两个导航项，Chat 默认高亮。
- Hub Rail 底部显示 Settings 按钮，点击后打开 AgentCore 设置页面。
- Context Sidebar 在 Chat 模块时显示会话列表，行为与现在一致（滚动、选中状态保持）。
- 切换到 Knowledge 模块时，Context Sidebar 隐藏，Chat Input 隐藏。
- 切回 Chat 后会话列表恢复，滚动位置保持。
- 点击已激活的 Hub Rail 项可折叠/展开 Context Sidebar。

### Phase Hub-2：KnowledgeBasePanel 最小闭环

目标：用户能在主窗口内把单个文档交给 LightRAG。

任务：

1. 新增 `KnowledgeBasePanel`。
2. 显示 LightRAG 配置状态。
3. 实现 `Test Connection`。
4. 实现 `Open Settings`。
5. 实现 `Index Document...`。
6. 调用 `LightRAGClient.IndexFileAsync`。
7. 显示 Last Index Result。

验收：

- LightRAG 未启用时有明确提示。
- Endpoint 未配置时可以跳 Settings。
- 选择 `.md` 文件可上传。
- 上传失败显示原因。
- 不需要展开 Settings 也能看到索引结果。

### Phase Hub-3：`manage_knowledge.index_file`

目标：Agent 可以在 Chat 中索引项目内文档。

任务：

1. `LightRAGTool` 增加 `index_file` action。
2. 增加文件路径校验。
3. 增加大小限制。
4. 增加排除目录。
5. 更新工具 schema 和描述。
6. 更新 `TOOLS.md.template`。

验收：

- Agent 可调用 `manage_knowledge(index_file)`。
- 非项目路径被拒绝。
- 文件不存在返回友好错误。
- 文件过大返回友好错误。

### Phase Hub-4：Knowledge 批量索引

目标：支持项目文档批量索引。

任务：

1. 新增 `KnowledgeDocumentScanner`。
2. 支持默认扫描：`README.md`、`CHANGELOG.md`、`docs/`、`plans/`、`Assets/Docs/`。
3. Knowledge Panel 增加 `Index Project Docs`。
4. 工具增加 `index_project_docs`。
5. 批量结果结构化展示。

验收：

- 显示 indexed / failed / skipped。
- 单文件失败不中断批次。
- 默认排除规则生效。

### Phase Hub-5：MemoryPanel

目标：将 mem0 记忆管理纳入主窗口 Memory 模块。

任务：

1. 新增 `MemoryPanel`。
2. 显示 mem0 Enabled 状态与 Endpoint。
3. 实现记忆条目列表（Context Sidebar）。
4. 实现记忆搜索。
5. 实现记忆删除。
6. 实现 `Open Settings` 跳转。

验收：

- mem0 未启用时有明确提示。
- 记忆列表可滚动浏览。
- 可按关键词搜索记忆。
- 删除操作有确认提示。

---

## 10. RAG 与 Chat 联动

Knowledge Panel 不应只是管理界面，还要和 Chat 工作流联动。

第一阶段可做轻联动：

1. 索引成功后显示按钮：

```text
[Ask Agent about this document]
```

点击后自动切回 Chat，并填入：

```text
请基于刚刚索引的文档，总结关键内容和可执行建议。
```

第二阶段可做：

1. Query Test 结果可插入 Chat。
2. Chat 中拖入文档时提示是否索引。
3. Agent 回答中展示来源时可跳到 Knowledge Panel。

---

## 11. 风险与约束

### 11.1 ChatWindow 继续膨胀

风险：把 Hub 全写进 `ChatWindow.cs`。

约束：每个 Hub 必须独立组件化，`ChatWindow` 只装配。

### 11.2 主窗口信息过载

风险：用户只想聊天，却看到太多功能按钮。

约束：Hub Rail 极窄（~52px），不占主要视觉空间；默认激活 Chat 模块；Context Sidebar 可折叠；功能面板不抢焦点。

### 11.3 Settings 与 Hub 职责混淆

风险：为了方便又把 action 放回 Settings。

约束：Settings 只配置，Hub 做 action。

### 11.4 RAG 上传隐私风险

风险：用户误上传敏感项目文件。

约束：第一阶段只允许显式选择单文件；默认限制项目根目录内；批量索引必须确认。

---

## 12. 文件变更建议

第一阶段可能修改/新增：

```text
Editor/UI/ChatWindow.cs
Editor/UI/ChatWindow.uxml
Editor/UI/ChatWindow.uss
Editor/UI/Components/HubRail.cs
Editor/UI/Components/KnowledgeBasePanel.cs
Editor/Tools/Cloud/LightRAGTool.cs
Editor/Bootstrap/Resources/TOOLS.md.template
```

暂不修改：

```text
Editor/Core/AgentLoop.cs
Editor/Session/*
```

除非 Chat 联动需要新增公开方法。

---

## 13. 推荐第一轮落地范围

第一轮只做最小闭环：

1. 将主菜单入口调整为 `Window/AgentCore`。
2. ChatWindow 内新增 Hub 骨架（三列布局：Hub Rail + Context Sidebar + Main Content）。
3. Hub Rail 中只启用 Chat 和 Knowledge 两个模块。
4. KnowledgeBasePanel 支持：
   - 显示 LightRAG 状态。
   - Test Connection。
   - Open Settings。
   - Index Document。
   - Last result。
5. `manage_knowledge.index_file`。
6. 工具说明更新。

不做：

1. 批量目录索引。
2. Query Test。
3. MemoryPanel（Phase Hub-5）。
4. 自动 RAG 查询。

---

## 14. 成功标准

第一轮成功标准：

1. 用户仍然从同一个 AgentCore 主窗口工作。
2. 用户不需要去 Settings 执行索引 action。
3. 用户能在 Knowledge 模块中上传文档到 LightRAG。
4. 上传结果在 Knowledge 模块中清晰显示。
5. Chat 功能不回退，会话列表在 Context Sidebar 中正常工作。
6. `ChatWindow.cs` 没有明显继续膨胀，RAG UI 逻辑位于 `KnowledgeBasePanel`。
7. Agent 可以通过工具索引项目内文件。

---

## 15. 已决策问题

以下问题已在方案迭代中明确决策，不再作为待迭代项：

| 问题 | 决策 |
|------|------|
| Hub 导航入口形式 | Hub Rail（左侧极窄栏，~52px），不用顶部 tab |
| 会话列表与 Hub 如何共存 | 会话列表迁移至 Context Sidebar，作为 Chat 模块的上下文内容 |
| Context Sidebar 是否需要 | 是，作为第二列，随激活模块动态切换内容 |
| 会话列表是否属于 Hub Chat 子功能 | 是，Chat 模块激活时 Context Sidebar 显示会话列表 |
| Hub Rail 默认状态 | 始终可见，不可折叠；Context Sidebar 可折叠 |
| Hub 模块数量 | 固定 3 个：Chat、Knowledge、Memory |
| Diagnostics 和 Tools 是否作为 Hub 模块 | 否，保留在 Settings 界面 |
| Code Index 是否纳入 Hub | 否，功能未实现，不在当前方案范围内 |
| Settings 入口位置 | Hub Rail 底部固定按钮，点击打开 AgentCore 设置页面 |
| 顶部标题栏是否保留 Settings 按钮 | 否，统一由 Hub Rail 底部 Settings 按钮承担 |

---

## 16. 待迭代问题

1. Knowledge Panel 的拖拽文件是否第一轮就做？
2. Settings 中是否需要 `Open AgentCore` 导航按钮？
3. 是否需要更新 README、SOUL、TOOLS 或截图文档中所有旧菜单路径引用？
4. Context Sidebar 折叠后，Hub Rail 是否显示 tooltip 提示当前模块名？
