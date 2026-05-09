# AgentCore RAG 功能补齐与强化设计

> 状态：草案，用于迭代最终 RAG/知识库系统效果。
> 目标版本：0.4.x 起逐步落地。
> 相关现状代码：`LightRAGClient`、`LightRAGTool`、`AgentCoreSettingsProvider`、`ManageFileTool`、`FileChangeTracker`。

---

## 1. 背景与问题

AgentCore 已经具备 LightRAG 云服务接入能力，但目前更像“底层能力已经接上，产品闭环还没完成”。

当前能力：

- `LightRAGClient` 支持：
  - 查询知识库。
  - 索引文本。
  - 上传单个文件。
  - 健康检查。
- `manage_knowledge` 工具支持：
  - `query`
  - `index_text`
- Settings 界面支持：
  - LightRAG Enabled。
  - Endpoint。
  - API Key。
  - Test Connection。

主要缺口：

1. 用户没有明确入口把文档交给 LightRAG。
2. Agent 工具没有暴露 `index_file`，底层 `IndexFileAsync` 没被使用。
3. 没有“索引哪些内容、何时索引、索引状态如何”的管理视图。
4. 没有项目文档索引策略，也没有默认排除规则。
5. 没有面向 Agent 对话自动检索的明确流程。
6. 还没有把 RAG 与未来代码索引能力划清边界。

---

## 2. 最终效果定义

最终目标不是简单加一个上传按钮，而是让 AgentCore 具备稳定、可控、可解释的“项目知识库”能力。

### 2.1 用户视角

用户应能在 Settings 或 Chat 中完成以下操作：

1. 配置 LightRAG 服务。
2. 测试连接并看到明确反馈。
3. 手动选择一个文档并索引。
4. 手动选择一个目录并批量索引。
5. 一键索引项目文档。
6. 查看最近索引结果：成功数、失败数、跳过数、耗时、失败原因。
7. 在 Chat 中要求 Agent 查询知识库。
8. Agent 在合适时主动查询知识库，并把检索结果用于回答或操作。

### 2.2 Agent 视角

Agent 应具备以下能力：

1. 判断问题是否需要查询知识库。
2. 调用 `manage_knowledge(query)` 检索项目知识。
3. 在用户要求“记住这份文档 / 索引这个文件 / 索引项目文档”时调用索引工具。
4. 清楚区分：
   - mem0：用户偏好、长期记忆、历史决策。
   - LightRAG：项目知识库、文档资料、设计说明。
   - 文件搜索/代码索引：当前项目文件与代码结构。

### 2.3 系统视角

系统应具备以下属性：

1. 可控：默认不自动上传用户项目内容。
2. 可见：索引行为有进度和结果反馈。
3. 可恢复：批量索引失败不会影响 Agent 正常运行。
4. 可扩展：后续可以接入代码索引，但不把文档 RAG 和代码索引混成一个不可维护系统。
5. 可解释：查询结果应尽量返回来源片段或文档来源。

---

## 3. 范围边界

### 3.1 本 RAG 系统负责什么

RAG 系统主要负责“项目知识文档”的索引与查询。

适合索引：

- `README.md`
- `CHANGELOG.md`
- `docs/**/*.md`
- `plans/**/*.md`
- 设计文档
- 需求文档
- API 使用说明
- 团队规范
- 玩法设计文档
- `.txt`、`.json`、`.yaml` 等轻量文本配置说明

### 3.2 暂不负责什么

以下内容不应在第一阶段强行纳入 LightRAG：

- 大规模 C# 代码语义索引。
- Prefab / Scene YAML 深度解析。
- Binary 资产内容。
- 自动监听所有文件变更并上传。
- 替代 `manage_file(search_content)` 的精确搜索。

### 3.3 与代码索引的边界

LightRAG 适合“知识文档问答”；代码索引适合“代码结构理解”。

后续应形成三层检索：

| 层级 | 目标 | 示例 |
|------|------|------|
| 精确搜索 | 找文件内容、配置、字符串 | 正则搜 `PlayerController` |
| 符号/代码索引 | 找类、方法、引用关系 | 找 `InventoryService` 的调用方 |
| LightRAG | 理解文档和设计背景 | 查询“战斗系统设计原则” |

---

## 4. 功能设计

### 4.1 产品入口设计：独立 Knowledge Base 窗口

原则：Settings 只负责插件配置，不承载“上传文档 / 批量索引 / 查询测试”这类业务 action。

因此，添加文档给 RAG 不放在 `Project Settings > AgentCore`。建议新增独立入口：

```text
Window/AgentCore/Knowledge Base
```

窗口类建议：

```text
Editor/UI/KnowledgeBaseWindow.cs
```

这个窗口是 AgentCore 的知识库管理台，负责执行与 LightRAG 相关的用户操作。它读取 Settings 中的 LightRAG 配置，但不在窗口里重复编辑主要配置项；配置缺失时，提供 `Open Settings` 跳转。

不建议只做一个菜单按钮直接弹文件选择器。原因：

1. 用户需要看到 LightRAG 当前是否启用、Endpoint 是什么、连接是否正常。
2. 用户需要看到索引过程和最后结果。
3. 后续会扩展批量索引、目录索引、拖拽文件、查询测试、索引历史。
4. 单次文件选择弹窗无法承载完整反馈，也不利于后续扩展。

所以最终设计应是一个独立 GUI 窗口，而不是 Settings 中的 action，也不是一次性菜单命令。

### 4.2 Knowledge Base Window 目标 UI

建议 UI：

```text
Window/AgentCore/Knowledge Base

┌─────────────────────────────────────────────────────────┐
│ AgentCore Knowledge Base                                │
├─────────────────────────────────────────────────────────┤
│ Status                                                  │
│   LightRAG: Enabled / Disabled                          │
│   Endpoint: http://localhost:9621                       │
│   Connection: Unknown / Connected / Failed              │
│   [Test Connection] [Open Settings]                     │
├─────────────────────────────────────────────────────────┤
│ Add Knowledge                                           │
│   [Index Document...]                                   │
│   [Index Folder...]                                     │
│   [Index Project Docs]                                  │
│                                                         │
│   Drop files here to index                              │
├─────────────────────────────────────────────────────────┤
│ Query Test                                              │
│   Query: [________________________________________]      │
│   Mode: [hybrid ▼]   [Query]                            │
│   Result preview...                                     │
├─────────────────────────────────────────────────────────┤
│ Last Index Result                                       │
│   12 indexed, 1 failed, 4 skipped, 8.2s                 │
│   - OK docs/Combat.md                                   │
│   - FAIL docs/LargeSpec.pdf: unsupported / too large    │
└─────────────────────────────────────────────────────────┘
```

第一阶段只做：

- 新菜单：`Window/AgentCore/Knowledge Base`
- 独立 `KnowledgeBaseWindow`
- 状态区：Enabled / Endpoint / Test Connection / Open Settings
- `Index Document...`
- Last result

第二阶段再做：

- `Index Project Docs`
- 拖拽文件索引
- Query Test

第三阶段再做：

- `Index Folder...`
- Include / Exclude patterns 展示
- Max file size MB 展示
- 索引历史列表

### 4.3 Settings 界面职责

Settings 只保留配置项：

- LightRAG Enabled
- Endpoint
- API Key
- Test Connection
- Include patterns
- Exclude patterns
- Max file size MB
- Max batch files
- Auto query policy

Settings 不放这些 action：

- `Index Document...`
- `Index Folder...`
- `Index Project Docs`
- Query Test
- 索引历史操作

Settings 可以放一个轻量跳转按钮：

```text
[Open Knowledge Base Window]
```

这个按钮只是导航入口，不直接执行索引动作，可以接受。

### 4.4 Chat 工具补齐

扩展 `manage_knowledge` actions：

当前：

- `query`
- `index_text`

建议新增：

- `index_file`
- `index_folder`
- `index_project_docs`
- `get_status`

#### 4.4.1 `index_file`

用途：让 Agent 将项目内某个文档上传到 LightRAG。

参数：

```json
{
  "action": "index_file",
  "path": "Assets/Docs/CombatDesign.md"
}
```

规则：

- path 必须在项目根目录下。
- 禁止上传 `Library`、`Temp`、`Obj`、`.git` 下文件。
- 默认限制文件大小，例如 2MB。
- 失败时返回明确原因。

#### 4.4.2 `index_folder`

用途：批量索引某个目录下的文档。

参数：

```json
{
  "action": "index_folder",
  "path": "docs",
  "recursive": true,
  "include_patterns": "*.md,*.txt,*.json,*.yaml,*.yml",
  "max_files": 100
}
```

规则：

- 默认 recursive = true。
- 默认 max_files = 100。
- 超出数量时停止并返回提示。
- 每个文件单独失败，不中断整个批次。

#### 4.4.3 `index_project_docs`

用途：一键索引项目常见文档。

默认扫描：

- `README.md`
- `CHANGELOG.md`
- `AGENTS.md`
- `docs/`
- `plans/`
- `Assets/Docs/`
- `Assets/Documentation/`

不默认扫描：

- 全 `Assets/`
- 全 `Packages/`
- `Library/`
- `ProjectSettings/`

#### 4.4.4 `get_status`

用途：查询 LightRAG 服务健康状态和最近索引摘要。

第一阶段可以只返回服务健康状态；后续再接索引历史。

### 4.5 自动查询策略

第一阶段不建议做“每轮自动 RAG 查询”。原因：

- 会增加延迟。
- 会增加 LightRAG 服务压力。
- 检索质量不稳定时可能污染上下文。

建议第一阶段策略：

1. 用户明确提到“知识库 / 文档 / 设计 / 规范 / 记忆中的文档 / 查一下文档”时，Agent 优先调用 `manage_knowledge(query)`。
2. 用户要求实现功能但信息不足时，Agent 可以先查询知识库。
3. 用户问当前代码/文件内容时，不使用 LightRAG，优先使用文件搜索和代码工具。

第二阶段可增加可选设置：

```text
Auto Query Knowledge Base: Off / Conservative / Aggressive
```

默认：`Conservative` 或 `Off`。

---

## 5. 数据与状态设计

### 5.1 Settings 字段建议

新增字段：

```csharp
public string lightragIncludePatterns = "*.md,*.txt,*.json,*.yaml,*.yml";
public string lightragExcludePatterns = "Library/**,Temp/**,Obj/**,Build/**,.git/**,*.meta";
public int lightragMaxFileSizeMb = 2;
public int lightragMaxBatchFiles = 100;
public bool lightragAutoQueryEnabled = false;
```

如果需要显示最近结果，可增加非关键持久化字段：

```csharp
public string lightragLastIndexSummary = "";
public string lightragLastIndexTimeUtc = "";
```

注意：新增字段需要递增 `AgentCoreSettings.CurrentVersion` 并补迁移逻辑。

### 5.2 索引结果模型

建议新增内部数据结构：

```csharp
public class KnowledgeIndexResult
{
    public int Total;
    public int Indexed;
    public int Failed;
    public int Skipped;
    public double ElapsedMs;
    public List<KnowledgeIndexItemResult> Items;
}

public class KnowledgeIndexItemResult
{
    public string Path;
    public string Status;
    public string Message;
}
```

---

## 6. 安全与隐私规则

RAG 上传文档是敏感操作，必须显式可控。

硬规则：

1. 默认不自动上传任何文件。
2. Knowledge Base Window 中点击批量索引前，显示确认对话框。
3. Chat 工具索引文件时，只允许项目根目录内路径。
4. 默认排除：
   - `.git/`
   - `Library/`
   - `Temp/`
   - `Obj/`
   - `Build/`
   - `Logs/`
   - `UserSettings/`
   - `.meta`
   - 常见二进制格式
5. 默认限制单文件大小。
6. 不上传 API Key、EditorPrefs、本地凭据文件。

建议默认禁止扩展名：

```text
.png,.jpg,.jpeg,.psd,.fbx,.blend,.wav,.mp3,.mp4,.dll,.exe,.zip,.tgz,.unitypackage
```

---

## 7. 实现阶段规划

### Phase RAG-1：补齐最小闭环

目标：用户可以从独立 Knowledge Base Window 上传单个文档；Agent 可以索引单个文件。

任务：

1. `LightRAGTool` 新增 `index_file` action。
2. 新增 `Window/AgentCore/Knowledge Base` 菜单。
3. 新增 `KnowledgeBaseWindow`，包含状态区、`Index Document...`、Last result。
4. 调用现有 `LightRAGClient.IndexFileAsync`。
5. Settings 的 LightRAG 区域只保留配置，可加 `Open Knowledge Base Window` 导航按钮。
6. 补充 `TOOLS.md.template` 中 `manage_knowledge` 说明。

验收：

- Knowledge Base Window 里选择 `.md` 文件后可以上传成功。
- Chat 中可调用 `manage_knowledge(index_file)` 上传项目文档。
- 服务未配置、文件不存在、文件过大时返回友好错误。

### Phase RAG-2：项目文档批量索引

目标：一键索引常见项目文档。

任务：

1. 新增文档扫描辅助类，例如 `KnowledgeDocumentScanner`。
2. 支持默认扫描 `README.md`、`CHANGELOG.md`、`docs/`、`plans/`、`Assets/Docs/`。
3. Knowledge Base Window 新增 `Index Project Docs`。
4. 工具新增 `index_project_docs`。
5. 批量结果结构化返回。

验收：

- 能显示 indexed / failed / skipped。
- 单个文件失败不影响其他文件。
- 默认排除规则生效。

### Phase RAG-3：索引配置与目录索引

目标：用户可以控制索引范围。

任务：

1. 新增 include/exclude patterns 设置。
2. 新增 max file size / max batch files 设置。
3. Knowledge Base Window 新增 `Index Folder...`。
4. 工具新增 `index_folder`。
5. 批量索引增加确认对话框。

验收：

- 用户可以索引指定目录。
- 大文件和排除文件不会上传。
- 超出 max files 时给出明确提示。

### Phase RAG-4：查询体验强化

目标：让 Agent 更稳定地使用知识库。

任务：

1. 更新 `SOUL.md` / `TOOLS.md.template`，明确何时使用知识库。
2. `manage_knowledge(query)` 支持 `top_k` 参数。
3. 查询结果展示来源信息。
4. 可选增加 `Auto Query Knowledge Base` 设置。

验收：

- 用户问“根据项目文档”时，Agent 会先查 LightRAG。
- 用户问代码现状时，Agent 不误用 LightRAG 替代文件搜索。

### Phase RAG-5：与代码索引协同

目标：形成文档 RAG + 代码索引的清晰分工。

任务：

1. 设计本地符号索引。
2. 查询路由：文档问题走 LightRAG，代码结构问题走代码索引，文本精确问题走文件搜索。
3. 在 UI 中拆分：Knowledge Base 与 Code Index。

验收：

- 不把所有内容都塞进 LightRAG。
- Agent 能解释检索来源类型。

---

## 8. 推荐第一轮实现范围

为了避免一次做大，第一轮只建议做：

1. `manage_knowledge.index_file`
2. 新增 `Window/AgentCore/Knowledge Base`
3. `KnowledgeBaseWindow`: `Index Document...` + Last result
4. 基础安全检查：项目内路径、文件存在、大小限制、排除目录
5. 更新工具说明

暂不做：

- 批量目录索引
- 自动查询
- 索引历史列表
- 删除知识库文档
- 代码索引

---

## 9. 待讨论问题

1. LightRAG 服务是否支持删除文档、列出文档？如果支持，是否需要 UI 暴露？
2. 上传文件是否要保留原路径作为 metadata？当前 `IndexFileAsync` 只是 multipart 上传，需要确认服务端是否保存文件名和来源。
3. 是否允许上传 `ProjectSettings` 里的文本配置？默认建议不允许，除非用户明确选择。
4. 是否需要本地保存“已索引文件 hash”，避免重复上传？第一阶段可不做，第二/三阶段再做。
5. 是否需要支持 Markdown 分块预处理？如果 LightRAG 服务端已处理，则 Unity 端不做。
6. `Index Project Docs` 是否应该默认包含 `AGENTS.md`？它可能包含开发规则，通常有价值，但也可能包含内部策略，需要用户确认。

---

## 10. 成功标准

RAG 功能补齐后，应该达到：

1. 用户能明确知道 LightRAG 是做什么的。
2. 用户能在 Knowledge Base Window 中把文档交给 LightRAG。
3. Agent 能在 Chat 中索引指定文档。
4. Agent 能查询知识库并返回带来源的结果。
5. 系统不会在用户不知情的情况下上传项目内容。
6. 失败时用户能知道是连接问题、文件问题、服务问题还是限制规则导致。
7. 后续代码索引可以独立演进，不被 LightRAG 设计绑死。
