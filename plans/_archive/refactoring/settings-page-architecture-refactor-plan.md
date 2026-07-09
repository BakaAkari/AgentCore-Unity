# AgentCore Settings 页面架构重构计划

> **文档版本**: v1.1
> **创建日期**: 2026-05-22
> **状态**: Implemented in v0.6.1（部分 V2 扩展点延期）
> **目标版本**: v0.6.1
> **范围**: Project Settings > AgentCore 设置页架构重构  
> **核心目标**: 将当前单文件 God Settings Provider 重构为可扩展、可维护、可约束的 Settings Hub + Section Contribution System，防止未来功能继续污染设置页。
>
> **实施说明（2026-05-22）**: v0.6.1 已完成 Provider shell 化、section registry、主要业务 section 迁移、Tools / Extensions 迁移和规则固化。`TargetSectionId` / `TargetComponentId` settings contribution V2 与 formal optional component descriptor registry 暂未引入，当前保留 `OptionalComponentManager` + `IAgentCoreSettingsContribution` 兼容路径。

---

## 1. 背景与问题

当前 AgentCore 设置页主要由 `Editor/Config/AgentCoreSettingsProvider.cs` 承担。随着功能增加，它已经从一个简单的配置面板演变成 2000+ 行的 God Object。

它当前同时负责：

- LLM endpoint、API key、model fetch、connection test。
- Agent 行为配置。
- Bootstrap / USER.md / MEMORY.md 文件管理。
- mem0 长期记忆服务配置和连接测试。
- LightRAG 知识库配置和连接测试。
- Context compression 配置。
- Optional Components 管理。
- Extension Settings 动态绘制。
- Tool Management 工具暴露控制。
- Appearance UI 偏好。
- About / Diagnostics。
- 多个异步测试状态和 UI 临时状态。

这导致设置页出现以下结构性问题。

### 1.1 信息架构失控

现有顶层分组混合了不同层级的概念：

- `Setup` 是首次配置路径。
- `Agent` 是运行行为。
- `Context & Memory` 同时包含本地 prompt 上下文、mem0 外部服务、LightRAG 外部服务。
- `Optional Components` 是组件级编译/加载开关。
- `Extension Settings` 是扩展设置垃圾桶。
- `Tools` 是 LLM 工具暴露控制。
- `Appearance` 是 UI 偏好。
- `About & Diagnostics` 是维护入口。

这些分组既不按用户任务组织，也不按扩展边界组织。

### 1.2 设置页持续污染

每新增一个功能，目前最自然的做法就是继续向 `AgentCoreSettingsProvider` 添加：

- 一个或多个私有 UI 状态字段。
- 一个 draw 方法。
- 一个或多个 async action 方法。
- 一个 foldout 字段。
- `OnGUI()` 中的一段调用。

这会使 Provider 继续膨胀，且没有架构层约束阻止污染。

### 1.3 Optional Component 和 Extension Settings 的归属不清

VCS 可选组件重构后，出现了两个用户视角上应该合并但代码上分离的区域：

- Optional Components：启用/禁用 VCS。
- Extension Settings：配置 VCS 行为。

这会导致未来每个扩展都把自己的配置丢进 `Extension Settings`，形成新的垃圾桶区域。

### 1.4 Tool Management 视觉权重过高

工具管理是高级功能，但当前作为顶层长列表出现，容易干扰首次配置路径。

从用户心智上应区分：

- Component：是否编译/加载某个能力包。
- Tool Exposure：已加载能力中，哪些 tool definition 暴露给 LLM。

### 1.5 IMGUI 仍可用，但必须有更严格的结构

Unity `SettingsProvider.OnGUI` 天然使用 IMGUI，因此本次重构不强制切换 UI Toolkit。关键不是换 UI 技术，而是建立可扩展设置架构。

---

## 2. 目标

本次重构目标不是“把现有控件换个顺序”，而是建立一套长期可维护的设置页架构。

### 2.1 架构目标

- `AgentCoreSettingsProvider` 只作为 Settings Hub 外壳，不再承载具体业务配置绘制。
- 每个设置页面/分区由独立 section 类负责。
- 新增功能必须通过 settings section / contribution 接入，而不是直接修改 Provider。
- Optional Component 的启用、状态、组件设置必须在同一组件卡片中呈现。
- Extension settings 必须声明挂载目标，禁止形成新的 “Extension Settings” 垃圾桶。
- 设置页支持左侧导航 + 右侧内容，避免无限 foldout 页面。
- 连接型配置使用统一交互模式。
- 所有 section 拥有稳定 ID、标题、描述、排序和可见性规则。

### 2.2 用户体验目标

- 新用户只需关注 `General` 和 `Model` 即可完成基础配置。
- 高级配置按功能域明确归类。
- VCS 等可选组件在一个卡片中完成启用、说明、状态提示和组件设置。
- 工具暴露策略独立成页，不污染组件管理。
- 诊断和维护操作集中到低频区域。
- 页面不再随着功能增长不断纵向膨胀。

### 2.3 维护目标

- Provider 文件控制在 200-300 行左右。
- 每个 section 文件只处理一个领域。
- 新增设置项不需要修改 Provider 主体。
- 新增可选组件不需要修改 Provider 主体。
- 新增扩展设置必须声明 `TargetSectionId` / `TargetComponentId`。
- 在 `AGENTS.md` 中固化规则，防止后续继续污染。

---

## 3. 非目标

本次重构不解决以下问题：

- 不重构 `AgentCoreSettings` 的持久化模型，除非迁移过程中确有必要。
- 不更换 `SettingsProvider` 为自定义 EditorWindow。
- 不强制将设置页从 IMGUI 改为 UI Toolkit。
- 不改变 LLM、mem0、LightRAG、VCS 的业务逻辑。
- 不移除现有设置项。
- 不改变已保存设置的语义。
- 不在同一次重构中新增大型功能。

---

## 4. 设计原则

### 4.1 Settings Provider Shell 原则

`AgentCoreSettingsProvider` 只负责：

1. 初始化 settings context。
2. 发现 settings sections。
3. 绘制 Settings Hub 外壳。
4. 处理左侧导航。
5. 绘制当前选中 section。
6. 处理全局保存和生命周期。

禁止在 Provider 中新增业务功能绘制代码。

### 4.2 Section Ownership 原则

每个设置项必须归属于一个明确 section。

示例：

- LLM endpoint 属于 `model`。
- mem0 endpoint 属于 `memory`。
- LightRAG endpoint 属于 `knowledge`。
- Compression threshold 属于 `context-management`。
- VCS auto refresh 属于 `extensions` 下的 `vcs` component card。
- Tool preset 属于 `tools`。

### 4.3 Extension Mount Point 原则

扩展设置不允许作为孤立顶层区块显示。每个扩展设置必须声明挂载点：

- `TargetSectionId`
- 可选 `TargetComponentId`

VCS 示例：

- `TargetSectionId = "extensions"`
- `TargetComponentId = "vcs"`

### 4.4 Optional Component Descriptor 原则

禁用组件时组件程序集不会编译，因此可选组件 descriptor 必须由主程序集提供，且不能强引用组件类型。

例如 VCS descriptor 可以在主程序集里描述：

- `Id = "vcs"`
- `DisplayName = "Version Control"`
- `DefineSymbol = "AGENTCORE_VCS"`
- `Description = "Git / SVN / Perforce integration"`

但不能引用 `VersionControlPanel`、`VersionControlTool`、`VcsSettingsContribution` 等 VCS 程序集类型。

### 4.5 统一连接块原则

所有连接型设置统一使用以下交互模式：

1. Enabled。
2. Endpoint。
3. API Key。
4. Test Connection。
5. Result。
6. Advanced Options。

适用对象：

- Main LLM。
- mem0。
- LightRAG。
- Compression LLM。
- 后续 MCP / cloud services。

### 4.6 默认低噪音原则

默认只展示最关键内容。

- `General` 和 `Model` 默认可见/优先。
- 高级数值调参放入 advanced foldout 或独立 section。
- 工具详细列表默认不展开。
- 诊断操作不出现在核心配置路径中。

---

## 5. 最终信息架构

设置页采用左侧导航 + 右侧内容。

```text
AgentCore Settings
┌─────────────────────────┬────────────────────────────────────────────┐
│ General                 │ 当前选中 section 内容                      │
│ Model                   │                                            │
│ Agent                   │                                            │
│ Context                 │                                            │
│ Memory                  │                                            │
│ Knowledge               │                                            │
│ Context Management      │                                            │
│ Extensions              │                                            │
│ Tools                   │                                            │
│ Interface               │                                            │
│ Diagnostics             │                                            │
└─────────────────────────┴────────────────────────────────────────────┘
```

### 5.1 `general`

用途：总体状态和入口。

内容：

- Package name。
- Package version。
- LLM status summary。
- Enabled services summary。
- Enabled optional components summary。
- Registered / enabled tools summary。
- Open AgentCore Window。
- Quick links。

### 5.2 `model`

用途：主 LLM 接入。

内容：

- API endpoint。
- API key。
- Model fetch / select。
- Test connection。
- Temperature。
- Max output tokens。

建议：

- 默认展开基础连接配置。
- Generation 参数可放在 `Advanced Generation` 子块。

### 5.3 `agent`

用途：Agent 运行行为和恢复策略。

内容：

- Auto compile check。
- Auto console capture。
- Fallback routing。
- Max tool call rounds。
- Max consecutive errors。

### 5.4 `context`

用途：本地上下文和启动提示词组成。

内容：

- Bootstrap enabled。
- Auto project context。
- USER.md。
- MEMORY.md。
- 后续 `.agentcore/rules.md`。
- 后续 project context collector 开关。

### 5.5 `memory`

用途：长期记忆服务。

内容：

- Long-term Memory enabled。
- Provider: mem0。
- Endpoint。
- API key。
- Test connection。
- Effective user ID。
- Check ID。
- Create ID。
- Auto memory enabled。
- Auto memory min turns。

### 5.6 `knowledge`

用途：外部知识库 / RAG。

内容：

- Knowledge Base enabled。
- Provider: LightRAG。
- Endpoint。
- API key。
- Test connection。
- 后续 document indexing / code indexing 入口。

### 5.7 `context-management`

用途：上下文预算与压缩策略。

内容：

- Max context tokens。
- Reserve response tokens。
- Compression enabled。
- Tool result compression threshold。
- Tool result target tokens。
- Conversation compression trigger ratio。
- Separate compression LLM。
- Compression LLM endpoint / model / API key。

### 5.8 `extensions`

用途：组件级能力包管理。

内容：

- Optional Component cards。
- 每个 component 的 enable/disable 开关。
- Define symbol 状态。
- Reload / recompilation 状态提示。
- 挂载到 component 的 extension settings。

VCS card 示例：

```text
Version Control
Git / SVN / Perforce integration.
Status: Enabled
Define: AGENTCORE_VCS
[ ] Enabled

Settings
- Refresh repository state when opening VCS panel
- Default commit entries
```

### 5.9 `tools`

用途：控制 LLM 可见工具。

内容：

- Registered tools summary。
- Presets: Recommended / Safe / Full / Custom。
- Category toggles。
- Search tools。
- Individual tool toggles。

说明：

- Tools 页面只管理已加载工具是否暴露给 LLM。
- Optional Component 是否加载由 `extensions` 控制。

### 5.10 `interface`

用途：界面偏好。

内容：

- Streaming enabled。
- Show tool call details。
- 后续 Theme。
- 后续 Diff view preferences。
- 后续 keyboard shortcuts。

### 5.11 `diagnostics`

用途：诊断和维护。

内容：

- Test all connections。
- Open USER.md / MEMORY.md。
- Open package folder。
- Show settings storage path。
- Reset UI state。
- Export diagnostics。
- About 信息。

---

## 6. 目标代码结构

新增目录：

```text
Editor/Config/Settings/
  AgentCoreSettingsProvider.cs
  AgentCoreSettingsContext.cs
  AgentCoreSettingsState.cs
  AgentCoreSettingsRegistry.cs
  AgentCoreSettingsUi.cs
  IAgentCoreSettingsSection.cs
  SettingsSectionBase.cs
  IAgentCoreOptionalComponent.cs
  OptionalComponentRegistry.cs
  OptionalComponentDescriptorBase.cs
  Contributions/
    IAgentCoreSettingsContribution.cs
  Components/
    VcsOptionalComponentDescriptor.cs
  Sections/
    GeneralSettingsSection.cs
    ModelSettingsSection.cs
    AgentSettingsSection.cs
    ContextSettingsSection.cs
    MemorySettingsSection.cs
    KnowledgeSettingsSection.cs
    ContextManagementSettingsSection.cs
    ExtensionsSettingsSection.cs
    ToolsSettingsSection.cs
    InterfaceSettingsSection.cs
    DiagnosticsSettingsSection.cs
```

迁移后，旧文件：

```text
Editor/Config/AgentCoreSettingsProvider.cs
```

可以有两种处理方式：

1. 保留路径，内容改为 partial shell，并逐步迁移。
2. 移动到 `Editor/Config/Settings/AgentCoreSettingsProvider.cs`，保留 `.meta` 或接受 GUID 变化。

建议优先选择方式 1，降低 Unity `.meta` 和 SettingsProvider 入口风险；待稳定后再考虑移动。

---

## 7. 核心接口设计

### 7.1 `IAgentCoreSettingsSection`

```csharp
namespace AgentCore.Editor.Config.Settings
{
    public interface IAgentCoreSettingsSection
    {
        string Id { get; }
        string Title { get; }
        string Description { get; }
        string Category { get; }
        int Order { get; }
        bool IsVisible(AgentCoreSettingsContext context);
        void OnActivate(AgentCoreSettingsContext context);
        void OnDeactivate(AgentCoreSettingsContext context);
        void Draw(AgentCoreSettingsContext context);
    }
}
```

约束：

- `Id` 必须稳定，不随显示名变化。
- `Order` 控制导航排序。
- `Draw()` 内只能绘制本 section 拥有的设置。
- section 不应该直接访问其他 section 的 UI 状态。

### 7.2 `SettingsSectionBase`

```csharp
public abstract class SettingsSectionBase : IAgentCoreSettingsSection
{
    public abstract string Id { get; }
    public abstract string Title { get; }
    public virtual string Description => string.Empty;
    public virtual string Category => "General";
    public virtual int Order => 0;
    public virtual bool IsVisible(AgentCoreSettingsContext context) => true;
    public virtual void OnActivate(AgentCoreSettingsContext context) { }
    public virtual void OnDeactivate(AgentCoreSettingsContext context) { }
    public abstract void Draw(AgentCoreSettingsContext context);
}
```

### 7.3 `AgentCoreSettingsContext`

```csharp
public sealed class AgentCoreSettingsContext
{
    public AgentCoreSettings Settings { get; }
    public AgentCoreSettingsState State { get; }
    public AgentCoreSettingsUi Ui { get; }
    public AgentCoreSettingsServices Services { get; }
}
```

职责：

- 向 section 提供统一入口。
- 避免 section 直接依赖 Provider。
- 避免状态字段散落。

### 7.4 `AgentCoreSettingsState`

```csharp
public sealed class AgentCoreSettingsState
{
    public string SelectedSectionId { get; set; }
    public Dictionary<string, bool> Foldouts { get; }
    public Dictionary<string, string> StatusMessages { get; }
    public HashSet<string> RunningTasks { get; }
}
```

职责：

- 保存 IMGUI 临时状态。
- 保存异步任务状态。
- 保存 foldout 状态。
- 用 section-scoped key 避免字段爆炸。

Key 命名建议：

```text
model.fetchModels
model.testConnection
memory.testConnection
memory.checkUserId
extensions.vcs
```

### 7.5 `AgentCoreSettingsUi`

```csharp
public sealed class AgentCoreSettingsUi
{
    public bool DrawSectionHeader(string title, string description, bool expanded);
    public void DrawHelpText(string text);
    public void DrawStatus(string text, SettingsStatusLevel level);
    public void DrawCard(string title, string description, Action drawContent);
    public void DrawConnectionBlock(SettingsConnectionBlock block);
    public void DrawApiKeyRow(SettingsApiKeyRow row);
}
```

职责：

- 统一视觉风格。
- 统一 card / help / status / API key 行。
- 减少 section 内重复 IMGUI 代码。

### 7.6 `IAgentCoreOptionalComponent`

```csharp
public interface IAgentCoreOptionalComponent
{
    string Id { get; }
    string Title { get; }
    string Description { get; }
    string DefineSymbol { get; }
    int Order { get; }
    bool RequiresScriptReload { get; }
    bool IsEnabled();
    void SetEnabled(bool enabled);
}
```

规则：

- Descriptor 必须位于主程序集或始终可编译程序集。
- Descriptor 不得引用可选组件程序集中的类型。
- `SetEnabled()` 可以封装 define 修改、asset refresh、script compilation request。

### 7.7 `IAgentCoreSettingsContribution` 升级版

当前接口需要升级为带挂载点的模型。

```csharp
public interface IAgentCoreSettingsContribution
{
    string Id { get; }
    string Title { get; }
    string Description { get; }
    string TargetSectionId { get; }
    string TargetComponentId { get; }
    int Order { get; }
    bool IsVisible(AgentCoreSettingsContext context);
    void Draw(AgentCoreSettingsContext context);
}
```

兼容策略见第 11 节。

---

## 8. Registry 设计

### 8.1 `AgentCoreSettingsRegistry`

职责：

- 注册内置 sections。
- 发现外部 settings section contributions。
- 排序。
- 去重。
- 提供只读 section 列表。

```csharp
public static class AgentCoreSettingsRegistry
{
    public static IReadOnlyList<IAgentCoreSettingsSection> Sections { get; }
    public static void Refresh();
}
```

初期可以手动注册内置 section，降低反射风险：

```csharp
Register(new GeneralSettingsSection());
Register(new ModelSettingsSection());
...
```

后续可支持 `[AgentCoreSettingsSection]` attribute 自动发现。

### 8.2 `OptionalComponentRegistry`

职责：

- 注册内置 optional component descriptor。
- 未来支持包级 descriptor contribution。
- 提供组件状态给 `ExtensionsSettingsSection`。

```csharp
public static class OptionalComponentRegistry
{
    public static IReadOnlyList<IAgentCoreOptionalComponent> Components { get; }
    public static IAgentCoreOptionalComponent GetById(string id);
    public static void Refresh();
}
```

### 8.3 Extension settings 聚合

`ExtensionsSettingsSection` 绘制流程：

1. 读取 `OptionalComponentRegistry.Components`。
2. 按 component order 绘制 component card。
3. 对每个 component，查询 `IAgentCoreSettingsContribution` 中：
   - `TargetSectionId == "extensions"`
   - `TargetComponentId == component.Id`
4. 如果组件启用且 contribution 可见，则在 card 内绘制 settings。
5. 无 component target 的 extension settings 不直接显示，必须归属明确 section。

---

## 9. Provider Shell 设计

目标 Provider 结构：

```csharp
public class AgentCoreSettingsProvider : SettingsProvider
{
    private AgentCoreSettingsContext _context;
    private IReadOnlyList<IAgentCoreSettingsSection> _sections;

    public override void OnActivate(string searchContext, VisualElement rootElement)
    {
        _context = AgentCoreSettingsContext.Create();
        AgentCoreSettingsRegistry.Refresh();
        OptionalComponentRegistry.Refresh();
        _sections = AgentCoreSettingsRegistry.Sections;
    }

    public override void OnGUI(string searchContext)
    {
        EnsureContext();
        DrawHeader();
        DrawHubLayout();
    }

    private void DrawHubLayout()
    {
        EditorGUILayout.BeginHorizontal();
        DrawNavigation();
        DrawSelectedSection();
        EditorGUILayout.EndHorizontal();
    }
}
```

Provider 不应包含：

- LLM connection test 逻辑。
- mem0 connection test 逻辑。
- LightRAG connection test 逻辑。
- VCS 设置绘制逻辑。
- Tool category loop。
- USER.md / MEMORY.md 文件创建逻辑。

这些逻辑应迁入 section 或 service。

---

## 10. 服务层设计

为避免 section 继续膨胀，可将异步操作放入 services。

建议新增：

```text
Editor/Config/Settings/Services/
  ModelSettingsService.cs
  MemorySettingsService.cs
  KnowledgeSettingsService.cs
  ContextFileService.cs
  DiagnosticsSettingsService.cs
```

### 10.1 `ModelSettingsService`

职责：

- Fetch available models。
- Test LLM connection。
- 管理 LLM API key display state。

### 10.2 `MemorySettingsService`

职责：

- Test mem0 connection。
- Check user ID。
- Create user ID。
- 管理 mem0 connection cache。

### 10.3 `KnowledgeSettingsService`

职责：

- Test LightRAG connection。
- 可扩展到健康检查。

### 10.4 `ContextFileService`

职责：

- 查找 USER.md / MEMORY.md。
- 创建模板。
- 打开文件。
- Reveal in finder。

### 10.5 `DiagnosticsSettingsService`

职责：

- Test all connections。
- Export diagnostics。
- Reset UI state。

---

## 11. 兼容策略

### 11.1 保留旧接口短期兼容

当前已有：

```text
Editor/Extensions/IAgentCoreSettingsContribution.cs
```

可以采用两阶段升级：

#### 阶段 A：新增 V2 接口

新增：

```csharp
public interface IAgentCoreSettingsContributionV2 : IAgentCoreSettingsContribution
{
    string TargetSectionId { get; }
    string TargetComponentId { get; }
    bool IsVisible(AgentCoreSettingsContext context);
    void Draw(AgentCoreSettingsContext context);
}
```

旧 `DrawGUI()` 仍可运行，但会被标记为 legacy。

#### 阶段 B：迁移完成后替换旧接口

待所有内置 contribution 迁移后，再删除 legacy path。

### 11.2 VCS 迁移

当前 VCS 有：

```text
Editor/VCS/Config/VcsSettingsContribution.cs
```

迁移后应：

- 实现新的 settings contribution 接口。
- 声明：
  - `TargetSectionId = "extensions"`
  - `TargetComponentId = "vcs"`
- 只绘制 VCS 自有设置。
- 不负责组件启用/禁用。

### 11.3 OptionalComponentManager 兼容

当前：

```text
Editor/Extensions/OptionalComponentManager.cs
```

迁移后可以降级为底层 define helper：

- `HasDefine(string define)`
- `SetDefine(string define, bool enabled)`
- `RequestScriptReload()`

组件列表改由 `OptionalComponentRegistry` 提供。

---

## 12. 迁移阶段

### Phase 0：冻结设计与补充规范

- [ ] 创建本设计文档。
- [ ] 用户 Review 并确认最终 section 列表。
- [ ] 确认目标版本号。
- [ ] 确认是否保留 Provider 文件路径。
- [ ] 确认是否先走 V2 interface 兼容路线。

### Phase 1：搭建 Settings Core 架构

新增：

- [ ] `AgentCoreSettingsContext`
- [ ] `AgentCoreSettingsState`
- [ ] `AgentCoreSettingsUi`
- [ ] `IAgentCoreSettingsSection`
- [ ] `SettingsSectionBase`
- [ ] `AgentCoreSettingsRegistry`

改造：

- [ ] `AgentCoreSettingsProvider` 变为 shell。
- [ ] 仍可临时调用旧 draw 方法，确保功能不中断。

验收：

- [ ] Settings 页面可打开。
- [ ] 左侧导航可显示 section。
- [ ] 至少一个 section 可绘制。
- [ ] Unity 编译无错误。

### Phase 2：迁移低风险 section

迁移：

- [ ] `GeneralSettingsSection`
- [ ] `InterfaceSettingsSection`
- [ ] `DiagnosticsSettingsSection` 基础 about 信息

验收：

- [ ] Provider 中删除对应旧 draw 方法或停止调用。
- [ ] 页面功能等价。
- [ ] 旧设置值不丢失。

### Phase 3：迁移 Model section

迁移：

- [x] LLM endpoint。
- [x] LLM API key。
- [x] Model fetch。
- [x] Test connection。
- [x] Temperature。
- [x] Max output tokens。

新增：

- [x] `ModelSettingsService`

验收：

- [~] API key 设置/清除正常。
- [~] Fetch models 正常。
- [~] Test connection 正常。
- [~] 设置保存正常。

### Phase 4：迁移 Agent 与 Context Management

迁移：

- [x] `AgentSettingsSection`
- [x] `ContextManagementSettingsSection`

内容：

- [x] Auto compile check。
- [x] Auto console capture。
- [x] Fallback routing。
- [x] Runtime limits。
- [x] Compression settings。
- [x] Separate compression LLM。

验收：

- [~] Runtime 设置保存正常。
- [~] Compression 设置保存正常。
- [~] Compression API key 设置/清除正常。

### Phase 5：迁移 Context / Memory / Knowledge

迁移：

- [x] `ContextSettingsSection`
- [x] `MemorySettingsSection`
- [x] `KnowledgeSettingsSection`

新增 services：

- [ ] `ContextFileService`（未独立抽取；逻辑保留在 section/helper 中）
- [ ] `MemorySettingsService`（未独立抽取；当前 section 直接复用现有 client/setting 逻辑）
- [ ] `KnowledgeSettingsService`（未独立抽取；当前 section 直接复用现有 client/setting 逻辑）

验收：

- [~] USER.md / MEMORY.md create/edit/show 正常。
- [~] mem0 test/check/create ID 正常。
- [~] LightRAG test 正常。
- [~] 服务启用状态保存正常。

### Phase 6：重构 Optional Components / Extensions

新增：

- [ ] `IAgentCoreOptionalComponent`（延期：当前继续使用主程序集 `OptionalComponentManager` descriptor 数据）
- [ ] `OptionalComponentRegistry`（延期：当前继续使用 `OptionalComponentManager.GetComponents()`）
- [ ] `OptionalComponentDescriptorBase`（延期）
- [ ] `VcsOptionalComponentDescriptor`（延期：VCS descriptor 信息由 `OptionalComponentManager` 提供）
- [ ] settings contribution V2 或升级版接口（延期：当前兼容 `IAgentCoreSettingsContribution`）

迁移：

- [x] `ExtensionsSettingsSection`
- [x] `VcsSettingsContribution`

验收：

- [x] VCS 可在 Extensions 页面启用/禁用。
- [x] 启用/禁用会触发 Unity script recompilation。
- [~] VCS settings 出现在 Extensions section 的 mounted settings 区域（同 section；card 内嵌 V2 延期）。
- [x] 禁用 VCS 后仍能显示 VCS component descriptor。
- [x] 主程序集不强引用 VCS 类型。

### Phase 7：迁移 Tools section

迁移：

- [x] `ToolsSettingsSection`
- [x] Tool presets。
- [x] Category toggles。
- [x] Individual tool toggles。
- [ ] 搜索/过滤（延期：非本轮必要项）。

验收：

- [x] Tool registry 未初始化时显示明确提示。
- [~] Safe / Full preset 正常。
- [~] Category disable 正常。
- [~] Individual tool disable 正常。
- [~] 已禁用 optional component 的工具不会残留。

### Phase 8：删除旧 Provider 业务逻辑

清理：

- [x] 删除 Provider 中旧 draw 方法。
- [x] 删除 Provider 中旧 UI 状态字段。
- [x] 删除旧 `DrawExtensionSettingsSection()`。
- [x] 删除旧 `DrawOptionalComponentsSection()` 或转移到 section。
- [x] 删除旧 `DrawToolManagementSection()` 或转移到 section。

验收：

- [x] Provider 只保留 shell 逻辑。
- [x] Provider 行数控制在 200-300 行左右。
- [x] 全项目无未使用旧方法。

### Phase 9：文档和规则固化

更新：

- [x] `AGENTS.md` 增加 Settings 页面开发规则。
- [x] `CHANGELOG.md` 增加版本记录。
- [x] `plans/ROADMAP.md` 更新里程碑。
- [ ] 如有必要更新 `plans/ARCHITECTURE.md`（本轮未更新；ROADMAP ADR + 本计划已覆盖）。

---

## 13. AGENTS.md 规则草案

应增加以下规则：

```md
## Settings 页面开发规则

- 禁止在 AgentCoreSettingsProvider 中直接新增业务设置 UI。
- 新增设置项必须归属到一个 IAgentCoreSettingsSection。
- 新增 settings section 必须定义稳定 Id、Title、Description、Order。
- 新增可选组件必须提供 IAgentCoreOptionalComponent descriptor。
- 可选组件 descriptor 必须在禁用组件时仍可编译，不得引用组件程序集类型。
- 新增扩展设置必须声明 TargetSectionId；如属于某个组件，还必须声明 TargetComponentId。
- 禁止新增无归属的 Extension Settings 顶层区块。
- 连接型设置必须使用统一 Connection Block 模式。
- Tool Exposure 只控制 LLM 可见工具，不负责组件启用/禁用。
- Provider 只能承担 shell / navigation / section dispatch 职责。
```

---

## 14. 风险与应对

### 14.1 风险：一次性改动过大

应对：

- 分 phase 迁移。
- 每个 phase 保持 Unity 可编译。
- Provider 可临时 shell + legacy draw 混合运行。

### 14.2 风险：IMGUI 导航状态复杂

应对：

- 使用 `AgentCoreSettingsState.SelectedSectionId` 管理当前 section。
- 每个 section 的 foldout 使用 scoped key，不再新增 Provider 字段。

### 14.3 风险：extension contribution 接口升级破坏 VCS

应对：

- 先新增 V2 接口兼容。
- VCS 迁移后再移除 legacy。

### 14.4 风险：禁用 VCS 后 descriptor 消失

应对：

- VCS descriptor 放主程序集。
- VCS settings contribution 仍放 VCS 程序集。
- 禁用状态下只显示 enable card，不显示 VCS settings。

### 14.5 风险：服务测试异步状态丢失

应对：

- 异步状态统一放 `AgentCoreSettingsState`。
- 结果按 key 保存。
- section redraw 不依赖局部变量。

---

## 15. 验收标准

### 15.1 架构验收

- [x] `AgentCoreSettingsProvider` 不直接绘制具体业务设置。
- [x] 新增设置 section 不需要修改 Provider 主体。
- [x] 新增可选组件 descriptor 不需要修改 Provider 主体。
- [ ] Extension settings 必须有明确挂载点。（延期到 settings contribution V2）
- [~] 不存在独立的 `Extension Settings` 垃圾桶区域。（已收敛到 Extensions section，V2 card mount 延期）
- [~] VCS 启用和 VCS 设置在同一 component card 中。（同 section 已完成，card 内嵌延期）
- [x] Tool Exposure 独立于 Optional Components。

### 15.2 用户体验验收

- [x] 设置页采用左侧导航 + 右侧内容。
- [x] 新用户能快速找到 Model 配置。
- [x] Memory / Knowledge / Context 不再混成一个不可理解的大区。
- [x] VCS 组件启用/禁用路径清晰。
- [~] Advanced settings 不干扰基础配置。
- [~] 连接测试反馈位置统一。

### 15.3 功能回归验收

- [ ] LLM API key set/clear 正常。
- [ ] LLM model fetch 正常。
- [ ] LLM test connection 正常。
- [ ] mem0 test/check/create ID 正常。
- [ ] LightRAG test connection 正常。
- [ ] USER.md / MEMORY.md create/edit/show 正常。
- [ ] Compression settings 保存正常。
- [ ] Optional VCS enable/disable 正常触发编译。
- [ ] VCS disabled 后 Hub 不显示 VCS。
- [ ] VCS disabled 后 `version_control` tool 不注册。
- [ ] Tool presets 和 tool toggles 正常。

### 15.4 编译验收

- [ ] `AgentCore.Editor` 编译通过。
- [ ] `AgentCore.VCS.Editor` 在 `AGENTCORE_VCS` 启用时编译通过。
- [ ] `AGENTCORE_VCS` 禁用时主设置页仍能显示 VCS component descriptor。
- [ ] Unity Console 无新增错误。

---

## 16. 版本结论

本次作为 `v0.6.1` 交付。

原因：

- 已完成设置页架构和体验重构，但没有引入新的用户功能域。
- 没有破坏现有设置持久化语义。
- settings contribution V2 与 formal optional component descriptor registry 延期，因此不升级到 `v0.7.0`。
- `v0.6.1` 作为 v0.6.0 Optional Components 后的架构稳固版本更符合 SemVer。

---

## 17. 推荐执行策略

建议不要直接编码，而是按以下顺序推进：

1. 用户 review 本文档，确认 section 列表和目标版本。
2. 根据反馈修订本文档。
3. 冻结 settings section / optional component / contribution 接口设计。
4. Phase 1 先搭 shell，不迁移全部业务。
5. 每迁移 1-2 个 section 就进行一次 Unity 编译验证。
6. 最后才删除旧 Provider 业务逻辑。
7. 更新 `AGENTS.md`，把规则固化为项目规范。

---

## 18. 最终目标状态

重构完成后，AgentCore Settings 应该具备以下特征：

- 主 Provider 是稳定 shell，不随功能膨胀。
- 每个功能设置都有明确 ownership。
- 可选组件有 descriptor，不依赖组件程序集是否编译。
- 扩展设置按挂载点进入对应 section/card。
- VCS 这类组件不会再制造设置页割裂。
- 未来新增 Code Index、Rules、Theme、MCP、Diff View、Shortcuts 时，只需新增 section 或 contribution，不会污染已有 Provider。

这不是一次 UI 美化，而是 AgentCore 可扩展配置系统的基础设施重构。
