# VCS 可选组件重构执行文档

> **文档版本**: v1.2
> **创建日期**: 2026-05-21
> **更新日期**: 2026-05-22
> **状态**: 已实现（v0.6.0）
> **目标版本**: v0.6.0
> **关联计划**: `plans/version-control-integration-plan.md`  
> **核心目标**: 将当前内置并默认显示的 VCS 功能重构为 AgentCore 的可选组件，默认不启用，由用户在 AgentCore Settings 中手动启用或禁用。

---

## 1. 背景与问题

当前 VCS 功能已经实现为 AgentCore 主包的一部分，包含：

- VCS Tool：`version_control`
- VCS UI：`VersionControlPanel`
- Git / SVN / Perforce 适配器
- Hub 左侧导航中的 VCS 入口
- Settings / 文档 / 工具提示中的 VCS 说明

这种实现方式适合快速集成功能，但不适合长期产品化：

1. **默认功能过重**：不是所有用户都需要 Git / SVN / Perforce 集成。
2. **主包边界模糊**：VCS 是一个完整子系统，长期放在主程序集里会增加维护成本。
3. **无法选择性启用**：用户不能在 Settings 中明确决定是否启用 VCS。
4. **无法演进为组件体系**：未来 Build、CI、Profiler、QA 等功能也可能需要同样的可选组件机制。
5. **硬编码耦合**：当前 Hub 和 Settings 中存在直接 VCS 入口，不利于卸载、禁用和动态扩展。

因此需要将 VCS 重构为 **AgentCore Optional Component**。

---

## 2. 重构目标

### 2.1 用户层目标

默认安装 AgentCore 后：

- 不显示 VCS Hub 入口。
- 不注册 `version_control` 工具。
- Agent 不会主动调用 VCS 能力。
- Settings 中仅显示“可选组件”入口，提示 VCS 可启用。

用户在 Settings 中启用 VCS 后：

- Unity 触发脚本重新编译。
- AgentCore 左侧 Hub 出现 VCS 入口。
- `version_control` 工具进入 ToolRegistry。
- Agent 可以使用 VCS 查询和操作能力。
- Settings 中显示 VCS 组件状态和相关配置。

用户禁用 VCS 后：

- Unity 触发脚本重新编译。
- VCS Hub 入口消失。
- `version_control` 工具不再注册。
- 既有历史会话仍可安全显示旧 tool call 文本，不因工具缺失报错。

### 2.2 架构层目标

- 主包成为“平台 + 扩展宿主”。
- VCS 成为“内置可选组件”。
- 主程序集不直接引用 VCS 类型。
- Hub 模块从硬编码枚举迁移为动态注册。
- Settings 页面支持扩展区块。
- Tool 自动发现仅发现当前已编译程序集中的工具。
- 可选组件启用状态由 Unity scripting define symbols 控制。

### 2.3 非目标

本次重构不做以下内容：

- 不把 VCS 拆成独立售卖 UPM 包。
- 不实现许可证或付费授权系统。
- 不第一阶段实现真实 `.unitypackage` 或 `.tgz` 解压导入。
- 不重写 Git / SVN / Perforce 的业务逻辑。
- 不改变现有 VCS actions 的语义。
- 不改变已有 VCS CLI 调用策略。

---

## 3. 推荐方案

采用 **define-gated 内置组件** 方案。

核心思路：

1. VCS 文件仍随 AgentCore 源码一起分发。
2. VCS 放入独立 Editor asmdef。
3. VCS asmdef 使用 define constraint：`AGENTCORE_VCS`。
4. 默认没有 `AGENTCORE_VCS`，因此 VCS 不参与编译。
5. Settings 中点击“启用 VCS 组件”后添加 `AGENTCORE_VCS`。
6. Unity 重新编译后 VCS 程序集加载。
7. VCS 通过扩展注册机制向主包贡献 Panel / Settings / Tool。

此方案相比真实解压文件更稳定：

- 不污染用户 `Assets/` 目录。
- 不修改 `Packages/manifest.json`。
- 不需要处理导入冲突。
- 不需要处理用户改动导入文件后的升级冲突。
- 可快速验证组件化架构。

---

## 4. 目标目录结构

重构后建议目录：

```text
Editor/
├── AgentCore.Editor.asmdef
├── Extensions/
│   ├── AgentCoreExtensionRegistry.cs
│   ├── AgentCoreExtensionAutoDiscovery.cs
│   ├── IAgentCorePanelContribution.cs
│   ├── IAgentCoreSettingsContribution.cs
│   └── OptionalComponentManager.cs
├── UI/
│   ├── Components/
│   │   ├── HubRail.cs
│   │   ├── KnowledgeBasePanel.cs
│   │   └── MemoryPanel.cs
│   └── ChatWindow.Hub.cs
└── Components/
    └── VCS/
        ├── AgentCore.VCS.Editor.asmdef
        ├── Config/
        │   └── VcsSettings.cs
        ├── UI/
        │   ├── VersionControlPanel.cs
        │   └── VersionControlPanel.uss
        ├── Tools/
        │   ├── VersionControlTool.cs
        │   ├── IVcsAdapter.cs
        │   ├── GitAdapter.cs
        │   ├── SvnAdapter.cs
        │   ├── PerforceAdapter.cs
        │   └── VcsCommandExecutor.cs
        ├── VcsPanelContribution.cs
        └── VcsSettingsContribution.cs
```

说明：

- `Editor/Extensions/` 属于主包平台层。
- `Editor/Components/VCS/` 属于可选组件层。
- VCS 组件程序集引用主程序集。
- 主程序集不能反向引用 VCS 程序集。

---

## 5. asmdef 设计

### 5.1 主程序集

现有主程序集保持：

```json
{
  "name": "AgentCore.Editor",
  "rootNamespace": "AgentCore.Editor",
  "references": [],
  "includePlatforms": [
    "Editor"
  ],
  "autoReferenced": true
}
```

### 5.2 VCS 程序集

新增：

```json
{
  "name": "AgentCore.VCS.Editor",
  "rootNamespace": "AgentCore.Editor.Components.VCS",
  "references": [
    "AgentCore.Editor"
  ],
  "includePlatforms": [
    "Editor"
  ],
  "excludePlatforms": [],
  "allowUnsafeCode": false,
  "overrideReferences": false,
  "precompiledReferences": [],
  "autoReferenced": true,
  "defineConstraints": [
    "AGENTCORE_VCS"
  ],
  "versionDefines": [],
  "noEngineReferences": false
}
```

约束：

- VCS 可引用 AgentCore 主程序集。
- AgentCore 主程序集不得引用 VCS 程序集。
- VCS 类型不能出现在主包源码的强类型引用中。

---

## 6. 扩展注册机制

### 6.1 Panel Contribution

新增接口：

```csharp
using UnityEngine.UIElements;

namespace AgentCore.Editor.Extensions
{
    public interface IAgentCorePanelContribution
    {
        string Id { get; }
        string Label { get; }
        string Tooltip { get; }
        int Order { get; }
        VisualElement CreatePanel();
    }
}
```

用途：

- Chat / Knowledge / Memory / VCS 都可以以 contribution 形式注册。
- HubRail 根据 contribution 动态生成按钮。
- ChatWindow 根据 contribution 创建或切换 panel。

### 6.2 Settings Contribution

新增接口：

```csharp
namespace AgentCore.Editor.Extensions
{
    public interface IAgentCoreSettingsContribution
    {
        string Id { get; }
        string Title { get; }
        string Description { get; }
        int Order { get; }
        void DrawGUI();
    }
}
```

用途：

- 主 SettingsProvider 渲染固定核心设置。
- 已启用组件可以追加自己的设置区块。
- VCS settings 不写死在 `AgentCoreSettingsProvider` 中。

### 6.3 Extension Registry

新增注册表：

```csharp
namespace AgentCore.Editor.Extensions
{
    public static class AgentCoreExtensionRegistry
    {
        public static IReadOnlyList<IAgentCorePanelContribution> Panels { get; }
        public static IReadOnlyList<IAgentCoreSettingsContribution> Settings { get; }

        public static void Refresh();
    }
}
```

实现策略：

- 通过反射扫描所有已加载 Editor 程序集。
- 查找非 abstract、非 generic、有无参构造函数的 contribution 类型。
- 实例化并排序。
- 捕获异常，避免单个扩展失败影响主窗口。

---

## 7. Optional Component Manager

新增：

```csharp
namespace AgentCore.Editor.Extensions
{
    public static class OptionalComponentManager
    {
        public const string VcsDefine = "AGENTCORE_VCS";

        public static bool IsVcsEnabled();
        public static void SetVcsEnabled(bool enabled);
        public static IReadOnlyList<OptionalComponentInfo> GetComponents();
    }
}
```

职责：

- 检查当前 active BuildTargetGroup 的 scripting define symbols。
- 添加或移除 `AGENTCORE_VCS`。
- 提醒用户会触发 Unity 重编译。
- 处理不同 Unity 版本下 `PlayerSettings` define API 差异。
- 明确第一阶段只保证当前 active BuildTargetGroup 生效；如需跨平台一致启用，可在后续增强为同步 Standalone / Android / iOS 等常见 BuildTargetGroup。

可选组件信息：

```csharp
public sealed class OptionalComponentInfo
{
    public string Id;
    public string DisplayName;
    public string Description;
    public string DefineSymbol;
    public bool Enabled;
    public bool RequiresReload;
}
```

---

## 8. 当前硬编码拆除点

### 8.1 HubRail

当前问题：

- `HubModule` enum 包含固定模块。
- VCS 是 enum 成员。
- Hub 按钮通过固定代码添加。

目标：

- 使用 string module id 替代 enum。
- HubRail 接收 panel contribution 列表。
- 按 contribution.Order 排序生成按钮。

建议 module id：

```text
chat
knowledge
memory
vcs
```

### 8.2 ChatWindow.Hub

当前问题：

- 可能通过 enum switch 切换模块。
- 可能直接创建 `VersionControlPanel`。

目标：

- 通过 `Dictionary<string, VisualElement>` 管理动态 panel。
- 根据 contribution.CreatePanel() 延迟创建 panel。
- 不出现任何 VCS 类型引用。

### 8.3 AgentCoreSettingsProvider

当前问题：

- Settings 布局是固定式大方法。
- 没有 Optional Components 分区。
- 没有组件 settings contribution 渲染点。

目标：

- 新增 `DrawOptionalComponentsSection()`。
- 新增 `DrawExtensionSettingsSection()`。
- VCS 未启用时，仅显示“启用 VCS 组件”。
- VCS 已启用时，显示 VCS contribution 提供的设置。

### 8.4 ChatWindow.uxml

当前问题：

- UXML 中存在 `version-control-panel` 固定容器。
- 即使 VCS 被禁用，静态 UXML 仍会残留 VCS 结构。

目标：

- 移除 `version-control-panel` 固定容器。
- 主窗口仅保留通用动态 panel 宿主容器。
- 所有可选 panel 由 contribution 在运行时创建和挂载。

### 8.5 VCS 文件迁移

需要迁移的现有文件类别：

```text
Editor/Tools/Native/VersionControl/*
Editor/UI/Components/VersionControlPanel.cs
Editor/UI/Components/VersionControlPanel.uss
```

迁移到：

```text
Editor/Components/VCS/Tools/*
Editor/Components/VCS/UI/*
```

命名空间建议改为：

```csharp
AgentCore.Editor.Components.VCS
AgentCore.Editor.Components.VCS.Tools
AgentCore.Editor.Components.VCS.UI
AgentCore.Editor.Components.VCS.Config
```

---

## 9. 分阶段执行计划

### Phase 1：新增扩展基础设施

目标：在不改变现有 UI 行为的前提下加入扩展机制。

任务：

- 新增 `Editor/Extensions/IAgentCorePanelContribution.cs`。
- 新增 `Editor/Extensions/IAgentCoreSettingsContribution.cs`。
- 新增 `Editor/Extensions/AgentCoreExtensionRegistry.cs`。
- 新增内置 Chat / Knowledge / Memory panel contribution。
- 保持 VCS 暂时仍按旧方式运行。

验收：

- 编译通过。
- Chat / Knowledge / Memory 正常显示。
- 现有 VCS 功能不受影响。

### Phase 2：Hub 动态化

目标：移除 HubRail 对固定 enum 的依赖。

任务：

- 将 `HubModule` 替换为 string module id 或 `AgentCorePanelDescriptor`。
- HubRail 动态生成按钮。
- ChatWindow.Hub 改为通过 contribution 切换 panel。
- 迁移 EditorPrefs 存储值为 module id。

验收：

- Chat / Knowledge / Memory 可切换。
- 上次打开模块能恢复。
- 不再需要 enum 才能新增模块。

### Phase 3：Settings 增加可选组件入口

目标：增加 Optional Components UI，但暂时不迁移 VCS。

任务：

- 新增 `OptionalComponentManager`。
- SettingsProvider 增加 `Optional Components` 分区。
- 增加 VCS 状态显示。
- 增加启用 / 禁用按钮，但初期可以只显示状态或执行 define 修改。

验收：

- Settings 能显示 VCS 组件状态。
- 点击启用 / 禁用能正确修改 define symbols。
- 操作前有确认提示。

### Phase 4：VCS 迁移到独立 asmdef

目标：让 VCS 编译受 `AGENTCORE_VCS` 控制。

任务：

- [x] 新建 `Editor/VCS/AgentCore.VCS.Editor.asmdef`。
- [x] 移动 VCS Tool / Adapter / UI 文件。
- [x] 调整命名空间和 using。
- [x] 确保 VCS asmdef 引用 `AgentCore.Editor`。
- [x] 确保主程序集不引用任何 VCS 类型。
- [x] 将 VCS USS 加载责任移入 VCS 组件内部，主包不得再加载 `VersionControlPanel.uss`。

验收：

- 没有 `AGENTCORE_VCS` 时编译通过。
- 没有 `AGENTCORE_VCS` 时 VCS 不显示、不注册工具。
- 有 `AGENTCORE_VCS` 时编译通过。
- 有 `AGENTCORE_VCS` 时 VCS 显示并可用。

### Phase 5：VCS contribution 接入

目标：VCS 启用后通过扩展机制接入主界面和 Settings。

任务：

- [x] 新增 `VersionControlPanelContribution`。
- [x] 新增 `VcsSettingsContribution`。
- [x] 新增 `VcsSettings`。
- [x] 确认 `VersionControlTool` 仅在 VCS asmdef 编译后被 ToolAutoDiscovery 自动发现。

验收：

- 启用 VCS 后 Hub 出现 VCS。
- 点击 VCS 可打开 VCS 面板。
- Settings 出现 VCS 设置。
- ToolRegistry 包含 `version_control`。

### Phase 6：禁用与历史兼容

目标：禁用 VCS 后不破坏历史数据。

任务：

- [x] 禁用后重新编译时 VCS panel contribution 不再存在，Hub 动态模块回退到 Chat。
- [x] 历史 session 中的 VCS tool call 仍可显示为普通历史记录。
- [x] 如果 LLM 尝试调用不存在的 `version_control`，返回清晰 Unknown tool 错误；正常情况下不提供该 tool definition。
- [x] ToolAutoDiscovery 每次发现前重建 ToolRegistry，禁用后不会残留旧工具实例。

验收：

- 禁用 VCS 后重启 Unity 不报错。
- 旧会话可打开。
- Tool 列表不含 `version_control`。

### Phase 7：文档与版本同步

目标：完成版本文档更新。

任务：

- [x] 更新 `CHANGELOG.md`。
- [x] 更新 `package.json` 版本。
- [x] 更新 `plans/ROADMAP.md`。
- [x] 如引入新的扩展开发规范，更新 `AGENTS.md`。

验收：

- 版本号、变更日志、路线图一致。
- 文档中明确 VCS 是可选组件。

---

## 10. 验收标准

### 10.1 默认状态

- [ ] 新安装 AgentCore 后，Hub 不显示 VCS。
- [ ] ToolRegistry 不包含 `version_control`。
- [ ] Agent 不在 system prompt / tool definitions 中看到 VCS 工具。
- [ ] Settings 显示 VCS 为未启用。

### 10.2 启用流程

- [ ] 点击启用前显示确认弹窗，说明将触发脚本重编译。
- [ ] 点击启用后添加 `AGENTCORE_VCS`。
- [ ] Unity 重新编译后 VCS 程序集加载。
- [ ] Hub 出现 VCS 按钮。
- [ ] VCS Panel 可打开。
- [ ] ToolRegistry 包含 `version_control`。
- [ ] Settings 显示 VCS 已启用。

### 10.3 禁用流程

- [ ] 点击禁用前显示确认弹窗，说明 VCS 功能将隐藏并触发重编译。
- [ ] 点击禁用后移除 `AGENTCORE_VCS`。
- [ ] Unity 重新编译后 VCS 程序集不加载。
- [ ] Hub 不再显示 VCS。
- [ ] ToolRegistry 不包含 `version_control`。
- [ ] 历史会话可正常显示。

### 10.4 回归验证

- [ ] Chat 模块正常。
- [ ] Knowledge 模块正常。
- [ ] Memory 模块正常。
- [ ] Settings 页面正常。
- [ ] Domain Reload 恢复正常。
- [ ] ToolAutoDiscovery 正常。
- [ ] 无编译错误、无新增 Console 错误。

---

## 11. 风险与应对

| 风险 | 影响 | 应对 |
|------|------|------|
| asmdef 拆分后引用缺失 | 编译失败 | 分阶段迁移，先保留旧路径验证，再移动文件 |
| 主包残留 VCS 强类型引用 | 禁用 VCS 后编译失败 | 使用全文搜索 `VersionControl` / `Vcs` 排查 |
| define symbol 修改触发全项目重编译 | 用户等待时间增加 | Settings 操作前明确提示 |
| Hub 动态化影响现有模块恢复 | 打开窗口异常或恢复错模块 | module id 默认回退到 `chat` |
| 禁用后旧会话存在 VCS tool call | 历史显示异常 | tool call 历史只作为文本展示，不依赖工具类 |
| ToolAutoDiscovery 缓存旧工具 | 禁用后仍显示 VCS | 每次 Discover 前清空或重建 ToolRegistry，禁止增量叠加旧工具实例 |
| VCS USS 路径变化 | UI 样式丢失 | 使用组件内稳定资源路径，加载失败时降级显示；主包不得加载 VCS USS |
| UXML 残留 VCS 容器 | 默认禁用状态仍暴露 VCS 结构 | 移除 `version-control-panel` 固定容器，改为动态 panel 宿主 |
| define symbol 仅作用于当前平台 | 切换 BuildTargetGroup 后 VCS 启用状态不一致 | 第一阶段明确仅作用于 active BuildTargetGroup；后续可增加多平台同步 |

---

## 12. 搜索检查清单

重构过程中需要反复搜索以下关键字：

```text
VersionControlPanel
VersionControlTool
VersionControl
Vcs
version_control
version-control-panel
HubModule
AGENTCORE_VCS
```

检查目标：

- 主程序集内不能有 VCS 类型强引用。
- VCS 程序集内可以引用主程序集扩展接口。
- Tool name 只在 VCS 组件内定义。
- UI 按钮由 contribution 动态生成。

---

## 13. 版本策略

建议版本：

- 如果只是完成架构重构，无新增用户功能：Patch 版本，例如 `0.5.5 -> 0.5.6`。
- 如果同时引入通用 Optional Components 框架：Minor 版本，例如 `0.5.5 -> 0.6.0`。

考虑到 Optional Components 是平台能力，建议作为 Minor 版本处理：

```text
0.5.5 -> 0.6.0
```

必须同步更新：

- `package.json`
- `CHANGELOG.md`
- `plans/ROADMAP.md`
- 如扩展开发规范固化，则更新 `AGENTS.md`

---

## 14. 后续演进

第一阶段完成 define-gated VCS 组件后，可以继续演进为真正的组件导入机制：

### 14.1 内置 `.unitypackage` 导入

- 将 VCS 打包为 `.unitypackage`。
- Settings 点击导入后调用 `AssetDatabase.ImportPackage()`。
- 适合用户需要可见、可编辑组件源码的场景。

### 14.2 内置 UPM `.tgz` 导入

- 将 VCS 打包为 `com.agentcore.unity.vcs.tgz`。
- Settings 点击导入后解压到 `Packages/com.agentcore.unity.vcs/`。
- 适合更干净的包级组件管理。

### 14.3 通用组件市场

未来可将以下功能也组件化：

- Build / CI 组件
- Profiler 分析组件
- QA / 测试组件
- Cloud 发布组件
- Issue Tracker 组件
- Asset Audit 组件

---

## 15. 编码前确认清单

正式开始实现前，需要用户确认：

| 确认项 | 建议值 |
|--------|--------|
| 本次目标 | 将 VCS 重构为默认禁用、Settings 可启用的内置可选组件 |
| 技术方案 | define-gated 独立 asmdef + 动态 Panel/Settings contribution |
| 目标版本 | `0.6.0` |
| 是否真实解压文件 | 第一阶段不做，仅做启用 / 禁用 define |
| 是否保留 VCS 源码随包分发 | 保留，但默认不编译 |
| 是否改动 Git/SVN/Perforce 逻辑 | 不改动，仅迁移和接入 |
| 是否更新 AGENTS.md | 如果扩展机制成为规范，则更新 |

---

## 16. 推荐执行顺序摘要

```text
1. 新增 Extensions 基础接口和 Registry
2. 将 Hub 模块动态化
3. 将内置 Chat / Knowledge / Memory 接成 contribution
4. Settings 增加 Optional Components 区域
5. 新增 OptionalComponentManager 控制 AGENTCORE_VCS
6. 新建 AgentCore.VCS.Editor.asmdef
7. 迁移 VCS Tool / Adapter / UI 文件
8. 新增 VcsPanelContribution / VcsSettingsContribution
9. 验证默认禁用状态
10. 验证启用流程
11. 验证禁用流程
12. 更新版本和文档
```

---

## 17. 当前结论

VCS 可选组件化是可行且推荐的重构方向。

本计划推荐先采用 `AGENTCORE_VCS` define-gated 独立程序集方案，在保证稳定性的前提下实现“默认不启用、用户在 Settings 中启用后出现”的产品体验。真实解压或导入组件包可以作为后续阶段，在 Optional Components 框架稳定后再实现。
