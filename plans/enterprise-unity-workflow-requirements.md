# 企业级 Unity 项目架构与 AgentCore 适配需求基准

> **版本**: v1.1  
> **制定日期**: 2026-06-02  
> **修订日期**: 2026-06-02  
> **状态**: 需求基准记录，已按“SVN 工作副本根 = AgentCore Workspace Root”校准  
> **适用范围**: AgentCore Unity 插件的代码索引、VCS、上下文、RAG、工具系统、Settings、资源与文案支持等后续设计

---

## 1. 文档目的

本文件记录当前目标用户项目的真实开发模式与约束，用作 AgentCore 后续功能设计的基础输入。

此前 AgentCore 的许多设计默认接近标准 Unity 项目：

- 代码与资源主要位于 `Assets/`。
- 包依赖主要位于 `Packages/`。
- VCS 工作副本以 Unity 项目根为中心。
- Agent 查询与操作对象主要是当前 Unity 工程可见内容。

但目标项目并非标准中小型 Unity 项目，而是大型商业项目。它的关键差异不是“资源包在任意外部目录”，而是：

> **SVN 分线工作副本根包含 Unity 工程、地图/模式目录、工具目录和资源目录；Unity 工程只是 Workspace 内的一个子目录。**

因此，AgentCore 后续能力必须从“标准 Unity Editor 助手”调整为：

> **以 SVN 工作副本根为 Workspace Root 的 Workspace-aware Unity Agent 插件**

AgentCore 应理解当前开发者所在的 SVN 工作副本、分线、UnityRoot、地图/模式 Scope、资源包目录、插件目录、权限和协作边界。

---

## 2. 已确认的 Workspace 结构规则

### 2.1 核心结论

已确认的基础规则：

1. **AgentCore Workspace Root = 当前 SVN 分线工作副本根目录。**
2. **UnityRoot = Workspace Root 下包含 Unity 工程的子目录。**
3. **Unity `Assets/` = UnityRoot 下的 Unity 资源目录，不是 AgentCore 的全局边界。**
4. **地图、模式、工具、部分资源包目录位于同一个 SVN Workspace Root 内，但可能不在 UnityRoot 或 Unity `Assets/` 内。**
5. **所谓“外部资源包”在本项目语境中不是任意外部路径，也不是默认独立 SVN 工作副本；它通常是同一个 SVN 工作副本中的 Workspace 子目录，只是位于 Unity `Assets/` 外。**

### 2.2 典型目录示例

```text
svn/project/branch/
├── unity/
│   ├── Assets/
│   ├── Packages/
│   └── ProjectSettings/
├── gamemodes/
├── maps/
├── ui/
├── localization/
├── tools/
├── plugins/
└── shared/
```

在该结构中：

| 概念 | 示例 | 说明 |
|---|---|---|
| WorkspaceRoot | `svn/project/branch/` | AgentCore 所有能力的默认安全边界和上下文根 |
| UnityRoot | `svn/project/branch/unity/` | Unity Editor、AssetDatabase、Scene、Prefab、BuildSettings 等能力的操作根 |
| Unity Assets Root | `svn/project/branch/unity/Assets/` | Unity 可导入资源目录 |
| Scope Root | `svn/project/branch/gamemodes/` | 地图/模式等业务目录，可被索引、搜索、VCS 分组和文件工具访问 |
| Tools Root | `svn/project/branch/tools/` | 项目工具、生成器、辅助脚本等目录 |

### 2.3 边界原则

后续所有功能必须明确区分两类边界：

- **Workspace 边界**：文件系统、代码索引、RAG 文件索引、Memory/Session 隔离、VCS 状态、规则文件、工具安全策略的默认边界。
- **UnityRoot 边界**：AssetDatabase、Scene、Prefab、BuildSettings、ProjectSettings、Unity Package 解析等 Unity Editor 原生能力的边界。

不得再把 UnityRoot 或 `Assets/` 当作 AgentCore 的整体 Workspace Root。

---

## 3. 项目开发组织模式

### 3.1 团队规模与角色

目标项目由多个并行团队协作开发，包括但不限于：

- 功能代码开发人员。
- UI / 美术代码开发人员。
- 美术资产开发人员。
- 文案与本地化开发人员。
- 地图开发人员。
- 模式/玩法开发人员。
- 基础逻辑和功能模块维护人员。
- 自定义引擎或底层扩展维护人员。
- 商业插件与自制插件集成维护人员。

这意味着 AgentCore 不能只按“程序代码”理解项目，还要识别不同团队的工作边界和产物类型。

### 3.2 地图/模式中心制

项目开发采用“地图/模式中心制”：

- 负责某个地图或模式的开发人员，主要工作在该地图/模式目录中。
- 地图/模式目录通常是 WorkspaceRoot 下的功能子目录，而不一定在 Unity `Assets/` 下。
- 地图/模式目录是开发者日常上下文的核心。
- 地图/模式会引用大量基础逻辑代码、公共模块、UI 代码、配置、文案和资源。
- 一个开发任务通常不是“修改某个类”，而是“围绕某张地图或某个模式完成一组代码、资源、配置、文案的协同修改”。

因此，AgentCore 需要将地图/模式视为一等上下文对象，而不是普通文件夹。

### 3.3 基础逻辑与功能模块引用

地图/模式目录通常不是完整闭环，它会依赖：

- 基础 Gameplay 逻辑。
- 公共 UI 模块。
- 网络、战斗、道具、任务、寻路等功能模块。
- 项目公共工具库。
- 自定义引擎扩展。
- 商业插件或自制插件。

因此，AgentCore 的索引、搜索、RAG、VCS、影响范围分析都必须支持“当前地图/模式 + 依赖模块”的组合上下文。

---

## 4. 资源与代码布局特点

### 4.1 非标准 Unity Assets 路径

项目中存在大量资源包和业务目录，这些内容不一定处于 Unity `Assets/` 目录中，但通常处于同一个 SVN Workspace Root 内。

这些目录可能包括：

- 与 UnityRoot 平级的 `gamemodes/`、`maps/`、`ui/`、`localization/`。
- 与 UnityRoot 平级的 `tools/`、`shared/`、`plugins/`。
- 资源包系统按需同步或显隐的 Workspace 子目录。
- 当前开发分线中已经同步但未被 Unity AssetDatabase 直接导入的目录。

因此，AgentCore 不应假设 `Assets/` 是唯一有效项目内容根。正确规则是：

> **默认可见范围是 SVN Workspace Root；Unity 原生资源能力再额外限制到 UnityRoot/Assets。**

### 4.2 资源包按地图/模式拆分

由于项目美术资产庞大，资源需要按照地图/模式拆分。

常见情况：

- 开发者在当前 SVN 分线工作副本中只同步或启用当前负责地图/模式需要的资源目录。
- 不同开发者本地 Workspace 子目录可见性或资源包组合不同。
- 当前 Unity 工程可见内容不代表完整项目内容。
- 某些代码或配置引用的资源包在当前开发者本地可能不存在、未启用或未被 Unity 导入。

因此，AgentCore 需要区分：

- 当前 WorkspaceRoot 下已存在且已启用的资源/模式目录。
- 当前分线中理论存在但本地未同步或未启用的资源/模式目录。
- 当前任务需要的资源/模式目录。
- 只读资源目录。
- 可编辑资源目录。

### 4.3 美术资产、文案与 UI 代码

项目内容不仅包含 C# 代码，还包括：

- Prefab、Scene、Material、Texture、Model、Animation 等美术资产。
- UI prefab、UI 逻辑、UI 配置。
- 文案表、本地化表、剧情文本、配置表。
- 资源包 manifest 或元数据。

Phase 1 的代码索引不应直接深度解析所有这些内容，但 AgentCore 的长期设计必须预留这些领域的索引和检索能力。

---

## 5. VCS 与分线模式

### 5.1 多 SVN 分线

项目存在多个发行版本和迭代版本，每个版本可能有独立 SVN 开发分线。

这意味着：

- 同一个相对路径在不同 SVN 分线可能对应不同代码。
- 同一个类名在不同 SVN 分线可能实现不同。
- 开发者切换分线后，旧索引、旧记忆、旧上下文可能失效。
- WorkspaceRoot 的 SVN URL、revision、branch 标识是 AgentCore 隔离上下文的核心依据。

AgentCore 需要以 WorkspaceRoot/Branch 为边界隔离索引、记忆和上下文。

### 5.2 单 SVN 工作副本内的多业务 Root

已确认的基线不是“主工程 + 多个独立资源包 SVN 工作副本”，而是：

> **每次拉取不同 SVN 分线仓库时，会同步主 Unity 工程和同分线下的资源/模式/工具目录；这些目录共同位于同一个 SVN 工作副本根下。**

因此，一个完整工作环境通常由一个 SVN WorkspaceRoot 和多个逻辑 Root 组成：

- UnityRoot：Unity 工程子目录。
- Map/Mode Root：地图/模式目录。
- UI Root：UI 资源和 UI 代码目录。
- Localization Root：文案、本地化、配置目录。
- Shared Root：公共资源和基础逻辑目录。
- Tools Root：内部工具和生成器目录。
- Plugin/Engine Root：商业插件、自制插件、自定义引擎扩展目录。

AgentCore 的 VCS 能力首先应识别 SVN WorkspaceRoot，然后按 Workspace 子 Root / Scope 分组展示和操作状态。

### 5.3 多 VCS Root 的定位

多 VCS Root 仍可作为高级/兼容场景保留，例如未来某些商业插件、SDK 或特殊资源目录确实来自独立工作副本。但它不是当前企业需求的默认基线。

设计优先级应为：

1. P0：正确识别 SVN WorkspaceRoot。
2. P0：从 WorkspaceRoot 派生 UnityRoot 和业务 Scope Root。
3. P0：VCS 状态按 Workspace 子 Root / Scope 分组。
4. P1/P2：在确有需要时支持额外授权的外部 VCS Root。

### 5.4 VCS 操作安全边界

在多人协作和多分线环境下，AgentCore 做 VCS 操作时必须明确：

- 当前操作属于哪个 WorkspaceRoot。
- 当前操作属于哪个 SVN 分线。
- 当前操作涉及哪个 Workspace 子 Root / Scope。
- 当前文件是否属于当前开发者负责 Scope。
- 当前文件是否只读或不建议修改。
- 是否涉及插件、引擎、生成代码或高风险共享模块。

提交、还原、清理、解决冲突等操作必须支持 Workspace 子 Root 和 Scope 过滤，避免跨目录误操作。

---

## 6. AgentCore 需要理解的核心概念

### 6.1 WorkspaceRoot

WorkspaceRoot 表示当前 AgentCore 操作的 SVN 分线工作副本根目录。

WorkspaceRoot 应包含：

- 本地绝对路径。
- VCS 类型，当前基线主要为 SVN。
- SVN URL、repository root、revision、branch 标识。
- UnityRoot 相对路径。
- 当前已发现或已配置的 Workspace 子 Root 列表。
- 当前开发 Scope。
- Settings 中配置的 include/exclude 规则。
- Workspace Fingerprint。

### 6.2 UnityRoot

UnityRoot 表示 WorkspaceRoot 内的 Unity 工程目录。

UnityRoot 用于：

- AssetDatabase 路径解析。
- Scene/Prefab/BuildSettings/ProjectSettings 操作。
- Unity `Assets/` 与 `Packages/` 的扫描。
- Unity Editor 原生工具安全边界。

UnityRoot 不是 AgentCore 的全局 Workspace 边界。

### 6.3 Scope

Scope 表示业务上下文和协作边界。

建议 Scope 类型：

| Scope | 含义 |
|---|---|
| Project | Unity 主工程或主项目代码 |
| Map | 地图目录或地图资源目录 |
| Mode | 玩法模式目录或模式资源目录 |
| Package | Workspace 内按需同步或启用的资源包目录 |
| Shared | 公共基础逻辑或公共资源 |
| UI | UI / 美术代码 / UI 资源 |
| Localization | 文案和本地化相关内容 |
| Engine | 自定义引擎或底层扩展 |
| Plugin | 商业插件或自制插件 |
| Tools | 内部工具、生成器、构建脚本 |
| Generated | 生成代码或生成资源 |
| Unknown | 未归类内容 |

### 6.4 Root

Root 表示 WorkspaceRoot 下的一个实际文件系统目录。

Root 的基线规则：

- Root 通常是 WorkspaceRoot 的子目录。
- UnityRoot 是特殊 Root。
- Unity `Assets/`、`Packages/` 可作为 UnityRoot 内的子 Root。
- 地图、模式、工具、资源包、插件、文案目录是 Workspace 子 Root。
- WorkspaceRoot 外部的 Root 只作为例外情况，必须显式授权并标记风险。

Root 需要携带：

- 本地路径。
- 相对 WorkspaceRoot 的路径。
- Scope 类型和名称。
- Role。
- VCS 信息。
- 资源包信息。
- 是否只读。
- include/exclude 规则。

### 6.5 Role

Role 表示该 Root 或文件在 AgentCore 中的安全策略。

建议 Role：

| Role | 默认策略 |
|---|---|
| EditableProjectCode | 可搜索，可建议修改 |
| SharedCode | 可搜索，修改需谨慎 |
| WorkspacePackage | 可搜索，修改取决于配置 |
| CommercialPlugin | 可搜索但默认降权，不建议修改 |
| CustomPlugin | 可搜索，修改需谨慎 |
| EngineCode | 可搜索，修改需谨慎 |
| ToolingCode | 可搜索，修改需确认 |
| GeneratedCode | 默认排除，不建议修改 |
| ReadOnlyReference | 仅作参考，不建议修改 |

---

## 7. 对现有 AgentCore 功能的影响

### 7.1 代码索引

代码索引必须从标准目录扫描升级为以 SVN WorkspaceRoot 为基础的多 Root、多 Scope 索引系统。

关键要求：

- 支持 WorkspaceRoot 下非 Unity `Assets/` 路径。
- 支持 UnityRoot 与 WorkspaceRoot 的明确区分。
- 支持当前地图/模式上下文。
- 支持 Workspace 子资源目录。
- 支持 SVN 分线隔离。
- 搜索结果必须返回 scope、root、role、branch、read-only 信息。

#### 7.1.1 后台静默 + 增量化要求（2026-06-15 新增）

企业级 Unity 项目的代码量庞大，每次 SVN/git 同步都会带来批量文件变更。如果索引采用同步阻塞式触发，开发者每次 pull 后都会面临数十秒到数分钟的 UI 阻断，体感上索引能力反而成了负担。因此索引必须满足：

- **后台执行**：索引任务在 ThreadPool/`Task.Run` 上跑，不占用 Unity 主线程帧时间。
- **增量优先**：基于上游 VCS pull 或 `AssetPostprocessor` 提供的 dirty 文件集精确重新索引，而不是每次都全量扫描。
- **静默呈现**：UI 层不打开模态、不抢占焦点，仅以 Hub 头部 ChipBadge / IndexingPanel 状态徽章和 Console 日志体现进度。
- **失败可恢复**：单文件失败不影响其他文件；连续失败超阈值后自动 Disabled，并向用户提示。
- **跨 Domain Reload**：dirty 集合持久化到 `Library/agentcore-indexing-dirty.json`，编译后自动续跑，不丢失变更。

详细方案见 [`indexing-background-incremental-design.md`](indexing-background-incremental-design.md)（对应 ROADMAP §3.1 / Phase 7，目标版本 v1.1.0；原 6.2.6 在 v1.0.0 验收时识别为后续优化项，已派生至 Phase 7）。

### 7.2 VCS 组件

现有 VCS 组件需要从“Unity 项目根 VCS”升级为“SVN WorkspaceRoot VCS”。

关键要求：

- 能从 UnityRoot 向上识别 SVN WorkspaceRoot。
- 能按 Workspace 子 Root / Scope 查看状态。
- Commit、Revert、Cleanup 等操作必须限定 Workspace 子 Root / Scope。
- 工作区状态需要区分 UnityRoot、地图/模式、资源、插件、工具目录。
- 多 VCS Root 支持仅作为扩展兼容项，不应成为 P0 设计前提。

### 7.3 Memory 与上下文

长期记忆和会话上下文必须考虑 WorkspaceRoot 和 Branch。

关键要求：

- 不同 SVN 分线的记忆不能无条件混用。
- 地图/模式相关记忆应绑定 scope。
- 插件/引擎相关经验应标记 role。
- 当前任务上下文应优先注入当前 scope。

### 7.4 RAG / Knowledge

LightRAG 知识库不应只有一个全局项目知识库，也不应只允许 UnityRoot 内文件。

关键要求：

- 支持按 workspace/scope 分类知识。
- 支持索引 WorkspaceRoot 内的文档、代码、配置和说明文件。
- 支持只查询当前地图/模式或相关 Shared 模块。
- 商业插件文档和项目代码文档应分层。
- 文案、配置、资源包 manifest 后续应有独立知识类型。

### 7.5 FileSystem 工具

文件读写工具必须理解 WorkspaceRoot、Root 和安全边界。

关键要求：

- 默认安全边界应从 Unity 项目根升级为授权 WorkspaceRoot。
- 可访问同一 SVN WorkspaceRoot 下、但不在 UnityRoot/Assets 内的目录。
- WorkspaceRoot 外部路径需要显式授权。
- Generated、Plugin、Engine、ReadOnlyReference 默认禁止写入或需要强确认。

### 7.6 Unity Native 工具

Unity 场景、Prefab、资源工具需要保持 UnityRoot/AssetDatabase 边界，并逐步适配 Scope 元数据。

关键要求：

- 查询资产时应能限定当前地图/模式或资源包对应的 Unity 资源范围。
- 对未拉取或未启用资源包的引用需要给出提示。
- 资源修改应明确发生在哪个 Workspace Root、UnityRoot、Scope。
- Unity Native 工具不应直接假装可以操作未被 Unity 导入的 Workspace 子目录。

### 7.7 Settings

Settings 需要从普通配置页升级为 workspace-aware 配置中心。

关键要求：

- 显示当前 SVN WorkspaceRoot。
- 显示 UnityRoot 相对路径与验证状态。
- 配置当前开发 Scope。
- 配置 Workspace 子 Root 与 Scope/Role 规则。
- 配置路径规则、只读规则、插件规则、Generated 排除规则。
- 显示当前 workspace fingerprint。
- 显示当前已识别 Workspace 子 Root 和资源/模式目录。

### 7.8 Agent 行为准则

AgentCore 的系统提示词和工具策略需要增加大型项目安全规则。

关键要求：

- 修改前先确认当前 WorkspaceRoot 与 Scope。
- 对插件、引擎、生成代码保持保守。
- 对跨 Scope 操作进行显式确认。
- 对 VCS 提交、还原、删除等操作必须二次确认。
- 对缺失资源包要提示用户同步或启用，而不是假设不存在。

---

## 8. 设计需求总表

| 需求 | 优先级 | 影响模块 |
|---|---|---|
| SVN WorkspaceRoot 识别 | P0 | Workspace, VCS, Bootstrap, Settings |
| UnityRoot 与 WorkspaceRoot 分离 | P0 | Native Tools, FileSystem, Indexing, RAG |
| Workspace 子 Root 支持 | P0 | Indexing, VCS, FileSystem, Settings |
| Scope 建模 | P0 | Indexing, RAG, Memory, Tools, Settings |
| Workspace Fingerprint | P0 | Indexing, Memory, RAG, VCS |
| SVN 分线隔离 | P0 | Indexing, VCS, Memory |
| 插件/引擎/生成代码安全策略 | P0 | Tools, Agent Prompt, Indexing |
| 资源包系统 Adapter | P1 | Indexing, Settings, VCS |
| 地图/模式自动识别 | P1 | Indexing, RAG, Settings |
| Workspace 子 Root VCS 分组 | P1 | VCS |
| Scope-aware RAG | P1 | LightRAG, Memory |
| 额外外部 VCS Root 支持 | P2 | VCS, Settings |
| 资源引用图 | P2 | Native Tools, Indexing |
| 文案/配置索引 | P2 | RAG, Indexing |
| 美术资产索引 | P2 | Native Tools, RAG |

---

## 9. 推荐总体路线

### Phase A：需求基线与架构校准

- 固化本文件为后续功能设计基准。
- 重评估当前 ROADMAP 中 Phase 6 以后的所有任务。
- 将 AgentCore 的定位调整为 Workspace-aware。

### Phase B：WorkspaceRoot / UnityRoot / Scope 基础设施

- 新增 WorkspaceRootResolver。
- 新增 UnityRootResolver。
- 新增 Scope/Root 数据模型。
- Settings 支持 SVN WorkspaceRoot、UnityRoot、Workspace 子 Root 与当前 Scope。
- VCS 基础信息可提供给其他模块。

### Phase C：VCS WorkspaceRoot 适配

- 从 UnityRoot 向上识别 SVN WorkspaceRoot。
- VCS 状态按 Workspace 子 Root / Scope 分组。
- 操作限定 Workspace 子 Root / Scope。
- Commit/Revert/Cleanup 更安全。

### Phase D：代码索引 Phase 1

- WorkspaceRoot 下多 Root C# 符号索引。
- Workspace fingerprint 分库。
- Scope-aware search_code 工具。

### Phase E：RAG / Memory Scope 化

- 知识库按 workspace/scope 分类。
- 记忆按 branch/scope 标记。
- 上下文注入优先当前 scope。

### Phase F：资源与文案扩展

- 资源包 Adapter。
- Scene/Prefab/Addressables 引用图。
- 文案/配置表索引。

---

## 10. 后续所有功能设计的检查清单

新增或修改 AgentCore 功能前，应检查：

- [ ] 是否把 UnityRoot 或 `Assets/` 误当作 AgentCore WorkspaceRoot？如果是，必须修正。
- [ ] 是否需要知道当前 SVN WorkspaceRoot？
- [ ] 是否需要知道 UnityRoot 相对路径？
- [ ] 是否需要知道当前地图/模式？
- [ ] 是否需要区分当前 SVN 分线？
- [ ] 是否可能操作 WorkspaceRoot 下但 UnityRoot 外的目录？
- [ ] 是否可能误改商业插件、自定义引擎或生成代码？
- [ ] 是否需要将结果绑定 workspace/scope？
- [ ] 是否需要在 Settings 中暴露 Workspace 子 Root / Scope 配置？
- [ ] 是否需要二次确认或只读保护？
- [ ] 是否需要处理资源包未同步或未启用的情况？
- [ ] 是否需要与现有资源包系统预留 Adapter？

---

## 11. 当前未知项

以下信息仍需后续补充：

1. WorkspaceRoot 下 UnityRoot 的稳定相对路径是否固定为 `unity/`。
2. 地图/模式目录是否有稳定路径命名规则。
3. 资源包系统是否有可调用 C# API。
4. 资源包系统是否有 manifest 或本地配置文件。
5. 每个资源包是否能标识所属地图/模式。
6. SVN 分线信息是否可稳定通过 `svn info` 获取。
7. 商业插件、自制插件、引擎代码的路径规则。
8. 文案与配置表的文件格式和目录规则。
9. UI/美术代码与普通功能代码的目录划分规则。
10. WorkspaceRoot 外是否存在少量必须授权访问的特殊目录。

---

## 12. 关键结论

AgentCore 可以适配该开发环境，但必须避免继续以标准 Unity 项目为前提设计功能。

最重要的架构调整是：

> **以 SVN 工作副本根作为 AgentCore WorkspaceRoot，以 Unity 工程目录作为 UnityRoot 子根，并以 Workspace 子 Root / Scope 承载地图、模式、资源、工具、插件和共享模块。**

后续所有能力都应围绕 WorkspaceRoot、UnityRoot、Scope、Root、Role、Branch 六个概念展开。

这六个概念将成为 AgentCore 面向大型商业 Unity 项目的核心上下文模型。
