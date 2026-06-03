# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.8.2] - 2026-06-03

### Added
- **`manage_workspace_config` 工具** — 专用于读写 `PROJECT.md` 和 `SOUL.ext.md` 的工具，Agent 可在 Chat 中主动分析项目并更新配置文件。
  - `read_project_config` / `write_project_config` — 读写 PROJECT.md（项目约定 + 个人偏好）
  - `read_soul_extension` / `write_soul_extension` — 读写 SOUL.ext.md（Agent 行为规则扩展）
  - `get_config_paths` — 查询两个配置文件的当前路径和存在状态
  - 写入时自动创建 `AgentCore/` 目录，路径解析与 Bootstrap 加载逻辑完全一致
  - 变更在下次对话开始时生效（Bootstrap 在对话启动时加载）

### Changed
- **SOUL.md 新增 §13 Workspace Configuration Management** — 明确告知 Agent 何时主动读写 PROJECT.md / SOUL.ext.md，以及与 `manage_memory` / `manage_knowledge` 的决策边界。
- **TOOLS.md.template 新增 Workspace Configuration 章节** — 包含 `manage_workspace_config` 完整使用指南和 Tool Selection Guide 条目。
- **§4 Anti-Hallucination 表格** — 新增 `manage_workspace_config` 正确名称及常见幻觉名称。

## [0.8.1] - 2026-06-03

### Changed
- **Bootstrap 链重构：MEMORY.md / USER.md → PROJECT.md / SOUL.ext.md**
  - 移除旧的 `MEMORY.md`（记忆文件）和 `USER.md`（用户偏好文件）加载逻辑，统一合并为 `PROJECT.md`（用户可编辑层）。
  - 新增 `SOUL.ext.md` 支持 — 追加模式扩展 SOUL.md 行为约束，不替换核心 SOUL；建议提交到 VCS。
  - 新增 `SKELETON.md` 加载支持 — 从 `Library/AgentCore/workspace-skeleton.md` 读取代码库骨架（由代码索引功能生成，不提交 VCS）。
  - `BootstrapContext` 字段更新：移除 `Memory`、`User`，新增 `SoulExtension`、`Workspace`、`Skeleton`。
  - `BootstrapLoader.GenerateUserFileTemplate()` 集中化 — 原散落在 4 个 UI 文件中的模板生成逻辑统一到 `BootstrapLoader` 公共静态方法。
  - 新增 `Editor/Bootstrap/Resources/PROJECT.md.template` — 包含 `## Project Conventions`（团队约定）和 `## Personal Preferences`（个人偏好）两个 section。
  - Settings 页面中"User Files"卡片更名为"Project Files"，按钮更新为 PROJECT.md / SOUL.ext.md。

### Removed
- **`MEMORY.md` 和 `USER.md` 文件支持已移除** — 如有现存文件，内容请迁移至 `PROJECT.md`（`## Personal Preferences` section）。

## [0.8.0] - 2026-06-02

### Added
- **VCS Working Copy Status 扁平列表重构**
  - 将 Working Copy Status 从 TreeView 改为 SVN 风格扁平列表，每行显示状态徽章 + 相对路径，视觉更清晰。
  - 支持单选、Ctrl 多选、Shift 范围选，选中行高亮。
  - 右键菜单根据文件状态动态显示可用操作（Add / Revert / Resolve / Commit / View Diff / Show Log / Blame / Copy Path 等）。
  - 多选时右键菜单聚合为批量操作（Commit Selected / Revert Selected / Stage Selected / Copy Paths 等）。
- **VCS 面板顶部 Cleanup Project 按钮**
  - 新增 "Cleanup Project" 按钮，支持一键触发 SVN cleanup / Git gc / P4 reconcile。
  - 编译或资产导入期间自动禁用按钮，防止误操作。
  - 优先尝试打开外部工具（TortoiseSVN / SourceTree 等），不可用时回退到内置命令行执行。
- **VCS Chat 工具能力大幅扩展（`version_control` tool）**
  - 新增 `get_file_log` action — 查询单个文件的提交历史（SVN `svn log`、Git `git log --follow`、P4 `p4 filelog`）。
  - 新增 `cleanup` action — 清理工作副本锁定/临时文件（SVN `svn cleanup`、Git `git gc --auto`、P4 `p4 reconcile`）。
  - 新增 `commit_files` action — 提交指定文件列表（支持 SVN / Git / P4，需 `confirmed=true` 二次确认）。
  - 新增 `resolve_files` action — 标记冲突文件为已解决（SVN `svn resolve --accept working`、Git `git add`、P4 `p4 resolve`，需确认）。
  - 新增 `ignore_file` action — 将指定文件加入忽略规则（SVN `svn:ignore` property、Git `.gitignore`，需确认）。
  - 新增 `ignore_folder` action — 将指定目录加入忽略规则（需确认）。
  - 新增 `ignore_extension` action — 将指定文件扩展名加入忽略规则（需确认）。
  - 新增 `remove_files` action — 从版本控制中删除文件（SVN `svn delete`、Git `git rm`、P4 `p4 delete`，需确认）。
  - 所有写操作统一使用 `confirmed=true` 二次确认机制，防止 Agent 误操作。

### Changed
- **VCS 面板布局优化** — 修复小窗口高度下各区块压缩/溢出问题，状态列表区域改为 `flex-grow` 自适应，避免内容被截断。
- **`version_control` tool `RequiresMainThread`** — 由 `false` 改为 `true`，因 `cleanup` action 需要访问 `EditorApplication.isCompiling`。

## [0.7.0] - 2026-05-28

### Added
- **Settings UI 重构为 Dashboard + 4 Pages**
  - 新增 `IAgentCoreSettingsPage` 接口，与旧 `IAgentCoreSettingsSection` 独立，避免耦合。
  - 新增 `DashboardSettingsPage` — 状态总览（LLM / Memory / Knowledge / VCS 徽章）、Quick Actions、Package Info。
  - 新增 `ModelAgentSettingsPage` — Model Connection（Endpoint / API Key / Model / Fetch / Test）、Generation（Temperature / Max Tokens）、Agent Runtime（Max Tool Rounds / Fallback Routing）、Self Correction（Auto Compile / Auto Console / Max Consecutive Errors）。
  - 新增 `ContextMemorySettingsPage` — Context Sources（Bootstrap + User Files）、Context Budget + Compression 双列卡片、Memory Service（mem0）+ Knowledge Base（LightRAG）双列卡片、Separate Compression LLM foldout。
  - 新增 `ToolsExtensionsSettingsPage` — Capability Overview、Tool Visibility（Presets + Category/Individual toggles）、Optional Components、Version Control 独立卡片、Extension Settings contribution 支持。
  - 新增 `UiDiagnosticsSettingsPage` — Chat UI（Streaming / Show Tool Details）、Diagnostics（Test LLM / mem0 / LightRAG / Refresh Registry / Open Logs）、Maintenance（Reset Settings / Clear Secure Keys / Open MEMORY.md / USER.md）。
  - 新增 `AgentCoreSettings.ResetToDefaults()` 方法，支持一键恢复所有设置为默认值。

### Changed
- **`AgentCoreSettingsProvider` 重写** — 从左侧导航 + Section dispatch 改为顶部 Tab 导航 + Page dispatch，仅保留 shell 职责。
- **旧 Section 系统退役** — 保留 `Editor/Config/Settings/Sections/` 目录代码短期不动，但 Provider 不再引用。旧 `AgentCoreSettingsRegistry` 和 `IAgentCoreSettingsSection` 进入维护模式。
- **VCS 设置提升为独立卡片** — Version Control 设置从 Extension Settings contribution foldout 提升为 Tools & Extensions 页面的一级卡片。
- **Agent 参数平铺化** — `maxToolCallRounds` 和 `maxConsecutiveErrors` 从 Advanced Limits foldout 提升为一级字段。

### Fixed
- 无行为修复（纯 UI 重构）。

## [0.6.1] - 2026-05-22

### Added
- **Settings 页面架构重构 (Settings Hub + Section System)**
  - 新增模块化 settings section 架构，将 AgentCore 设置页拆分为 General、Model、Agent、Context、Memory、Knowledge、Context Management、Extensions、Tools、Interface、Diagnostics 等独立 section。
  - 新增 settings shell / context / transient state / shared IMGUI helper / section registry，新增设置功能无需继续污染 `AgentCoreSettingsProvider`。
  - 新增 `ModelSettingsService`，将模型拉取与连接测试逻辑从 Provider 中抽离。

### Changed
- **AgentCoreSettingsProvider Shell 化** — Provider 仅保留 settings 初始化、左侧导航和 section dispatch，不再直接绘制业务设置。
- **Settings 信息架构重组** — Project Settings > AgentCore 改为左侧导航 + 右侧内容布局，避免单页无限 foldout 膨胀。
- **Extensions 设置归属收敛** — Optional Components 与启用组件贡献的 settings 统一由 Extensions section 管理。
- **Tools 设置独立化** — Tool exposure preset、category toggle、individual tool toggle 迁移到 Tools section，并与 Optional Components 启用/禁用职责解耦。

## [0.6.0] - 2026-05-21

### Added
- **VCS 可选组件化 (Optional Component Framework)**
  - 新增 `Editor/Extensions/` 扩展宿主机制，支持 Hub Panel 与 Settings Contribution 动态发现。
  - 新增 Optional Components 设置入口，用户可通过 `AGENTCORE_VCS` scripting define 启用或禁用 VCS 组件。
  - 新增 `AgentCore.VCS.Editor` 独立 Editor 程序集，VCS 仅在启用 `AGENTCORE_VCS` 后参与编译。
  - 新增 VCS 设置贡献区块，支持控制面板打开时自动刷新与默认提交历史数量。

### Changed
- **VCS 默认禁用** — 新安装 AgentCore 后不再默认显示 VCS Hub 入口，也不会默认注册 `version_control` 工具。
- **Hub 动态化** — Chat / Knowledge / Memory / VCS 统一通过动态 Panel contribution 接入，主窗口不再强引用 VCS 类型。
- **ToolAutoDiscovery 重建化** — 每次发现工具前重建 `ToolRegistry`，避免可选组件禁用后残留旧工具实例。
- **VCS 目录迁移** — VCS Tool、Adapter、Panel 与样式文件迁移至 `Editor/VCS/` 组件目录。

## [0.5.5] - 2026-05-21

### Added
- **版本控制集成 (Version Control Integration) - Phase 2 完整实现**
  - Phase 1 补齐 5 个高级查询 actions：
    - `get_blame` — 获取文件逐行归属信息（支持 Git/SVN/Perforce）
    - `get_commit_info` — 获取 Git 提交详细信息（作者、日期、变更文件）
    - `get_client_info` — 获取 Perforce 客户端工作区信息
    - `get_changelist` — 获取 Perforce 变更列表详情
    - `get_info` — 获取 SVN 仓库/文件详细信息
  - Phase 2 通用写操作 actions（所有 VCS 支持）：
    - `stage_files` — 暂存文件（Git: add, P4: edit/add, SVN: add）
    - `unstage_files` — 取消暂存（Git: reset HEAD, P4/SVN: revert）
    - `commit` — 提交变更（Git: commit, P4: submit, SVN: commit）
    - `revert_files` — 还原文件修改（所有 VCS）
    - `sync` — 同步远程（Git: pull, P4: sync, SVN: update）
  - Phase 2 Git 特有操作：
    - `create_branch` — 创建新分支
    - `switch_branch` — 切换分支
    - `stash` — 暂存当前修改
    - `stash_pop` — 恢复暂存的修改
  - Phase 2 VCS 别名映射：
    - `checkout_files` → Perforce edit/add
    - `submit` → Perforce submit
    - `update` → SVN update
    - `commit_svn` → SVN commit
    - `revert_svn` → SVN revert
    - `add_files` → SVN add
  - **用户确认机制** — 所有写操作首次调用返回预览，需 `confirmed=true` 才执行
  - `IVcsAdapter` 接口扩展 — 新增 6 个写操作方法
  - 新增数据类：`VcsBlameResult`, `VcsBlameLine`, `VcsOperationResult`, `VcsCommitDetail`, `VcsSvnInfo`, `VcsPerforceClientInfo`, `VcsPerforceChangelist`
  - `VersionControlPanel` UI 增强：
    - 操作按钮区域（Stage All, Unstage All, Commit, Sync, Revert All）
    - Git 特有操作区域（Create Branch, Switch Branch, Stash, Stash Pop）
    - 提交消息输入框
    - 分支名输入框
    - 文件选择复选框（支持选择性操作）
    - 按 VCS 类型自适应按钮标签（Git/SVN/Perforce 各有对应术语）
    - 危险操作确认对话框（Revert 前弹出确认）
    - 操作成功消息自动消失（5 秒）

### Changed
- `VersionControlTool` — 从 7 个 actions 扩展到 26 个 actions
- `GitAdapter` — 从 6 个方法扩展到 15 个方法（含 4 个 Git 特有操作）
- `SvnAdapter` — 从 6 个方法扩展到 11 个方法（含 SVN info 查询）
- `PerforceAdapter` — 从 6 个方法扩展到 11 个方法（含 client/changelist 查询）
- `VersionControlPanel.uss` — 新增操作按钮样式（primary/danger/operation 三种风格）

## [0.5.4] - 2026-05-21

### Added
- **版本控制集成 (Version Control Integration) - Phase 1**
  - 新增 `Editor/Tools/Native/VersionControl/` 模块 — 多 VCS 支持（SVN > Perforce > Git 优先级）
  - `VcsDetector` — 自动检测项目使用的版本控制系统
  - `IVcsAdapter` 接口 — 统一的 VCS 操作抽象层
  - `GitAdapter` — Git 版本控制适配器（只读查询操作）
  - `SvnAdapter` — SVN 版本控制适配器（只读查询操作）
  - `PerforceAdapter` — Perforce 版本控制适配器（只读查询操作）
  - `VcsCommandExecutor` — 统一的命令行执行器（超时控制、输出捕获）
  - `VersionControlTool` — Agent 工具，支持 7 个 actions：
    - `detect_vcs` — 检测 VCS 类型和可用性
    - `get_status` — 获取工作区状态（已修改文件列表）
    - `get_branch` — 获取当前分支/工作区信息
    - `get_log` — 获取提交历史（最多 100 条）
    - `get_diff` — 获取文件差异
    - `get_remote` — 获取远程仓库信息
    - `get_tags` — 获取标签/标记列表
  - `VersionControlPanel` UI 组件 — 独立的版本控制面板
    - 实时显示 VCS 类型、分支、版本号
    - 工作区状态列表（按状态分组）
    - 最近提交历史（最多 10 条）
    - Refresh 按钮手动刷新数据
    - View Diff 按钮查看差异（输出到 Console）
  - Hub Rail 新增 "VCS" 模块按钮
  - ChatWindow 集成 VersionControl 模块（与 Chat/Knowledge/Memory 同级）

### Changed
- `HubModule` 枚举 — 新增 `VersionControl` 模块
- `ChatWindow.Hub.cs` — 更新模块切换逻辑支持 VersionControl 面板
- `ChatWindow.uxml` — 新增 `#version-control-panel` 容器

## [0.5.3] - 2026-05-19

### Deprecated
- **模式系统 (Mode System) 废弃** — ADR-5 决策
  - AgentCore 定位为自主智能体，不需要手动模式切换
  - Agent 可根据需求自动识别环境并调用相应能力
  - 原计划的 Phase 6.1 模式系统任务已标记为 `[DEPRECATED]`

### Changed
- `plans/ROADMAP.md` — 新增 ADR-5 记录模式系统废弃决策
- `plans/ROADMAP.md` — 重新规划 v0.5.3+ 里程碑，移除模式系统相关任务

## [0.5.2] - 2026-05-18

### Added
- **上下文使用情况可视化 (Context Usage Visualization)**
  - 新增 `ContextUsagePanel` UI 组件 — 实时显示 token 使用情况和压缩统计
  - 新增 `ContextBudgetInfo` 数据结构 — 封装上下文预算和压缩指标
  - `AgentLoop.GetContextBudget()` — 暴露上下文预算信息供 UI 查询
  - 压缩统计持久化 — 支持 Domain Reload 后恢复压缩数据
  - 按会话统计 — 压缩数据随会话保存，支持历史查看

### Fixed
- **manage_knowledge 工具参数兼容性** — `query` action 现在同时支持 `"query"` 和 `"content"` 参数名，修复 LLM 参数名不匹配导致的连续失败
- **ContextUsagePanel UI 布局** — 添加 `flex-shrink: 0` 防止面板被消息滚动视图挤压
- **空回复兜底处理** — 当达到工具调用上限后 LLM 返回空内容时，显示"[系统提示] 助手未返回任何内容。"而非空白消息

### Changed
- `ChatWindow` — 集成 `ContextUsagePanel`，每次 LLM 调用后自动更新显示
- `CompressionMetrics` — 新增 `RestoreFromPersistence()` 方法支持 Domain Reload 恢复
- `SessionData` — 新增 `SerializableCompressionMetrics` 支持压缩数据序列化
- `DomainReloadState` — 新增压缩指标持久化方法

## [0.5.1] - 2026-05-14

### Fixed
- **Tool Call Arguments 合法性修复** — 新增 `SanitizeToolArguments()` 方法，修复 LLM 生成的无效 JSON arguments（如 Windows 路径中的未转义反斜杠 `\U`, `\P`），防止 vLLM 等服务端在 `json.loads()` 时返回 HTTP 400 错误
- **FallbackRouter 错误消息准确性** — 非重试错误（如 HTTP 400）现在正确报告实际尝试次数（"failed after 1 attempt"），而非误导性的 "failed after 3 attempts"
- **项目路径标准化** — `ProjectContextCollector.GetProjectPath()` 返回正斜杠格式路径，避免 system prompt 中的反斜杠路径"教会"模型生成无效 JSON

## [0.5.0] - 2026-05-14

### Added
- **上下文压缩系统 (Context Compression System)**
  - 新增 `Editor/Core/Compression/` 模块 — 智能压缩替代简单截断
  - `ToolResultCompressor` — 自动压缩超过阈值（默认 1000 tokens）的工具输出为 ~200 tokens 摘要
  - `ConversationCompressor` — 当上下文使用率超过 70% 时，将旧对话段压缩为摘要
  - `CompressionLLMClientFactory` — 支持独立的压缩 LLM（如 Claude Haiku），降低成本
  - `CompressionMetrics` — 追踪压缩统计（token 节省量、压缩比、成功/失败次数）
  - `CompressionPrompts` — 压缩专用 Prompt 模板
  - 优雅降级：压缩 LLM 失败时自动回退到 head+tail 截断策略
  - Settings 版本迁移 v5→v6，新增 7 个压缩配置字段
  - `SecureKeyStorage` 新增压缩 LLM API Key 安全存储
  - Settings Provider 新增 "Context Compression" 配置面板

### Changed
- `AgentLoop.LLM.cs` — 在 `TrimToFit` 之前调用 `ConversationCompressor`（智能压缩优先于暴力截断）
- `AgentLoop.Tools.cs` — 工具结果添加到消息历史前通过 `ToolResultCompressor` 压缩
- `AgentLoop.cs` — 初始化时创建压缩系统组件

## [0.4.8] - 2026-05-13

### Added
- **ManageTestTool 增强** (5.3.2)
  - 新增 `cancel` action — 通过反射调用 TestRunnerApi 取消正在运行的测试
  - 新增 `create_test_fixture` action — 生成完整测试 Fixture 模板（含 OneTimeSetUp/TearDown、SetUp/TearDown、命名空间、描述注释），支持 EditMode/PlayMode
- **ManageMaterialTool 增强** (5.3.5)
  - 新增 `batch_set_properties` action — 批量设置材质属性（best-effort 策略，逐条设置并汇报成功/失败）
  - 新增 `list_materials` action — 按文件夹和/或 Shader 过滤列出项目中的材质资产
  - 新增 `get_shader_info` action — 获取 Shader 详细信息（属性列表、关键字、是否 Shader Graph 资产）

## [0.4.7] - 2026-05-13

### Changed
- **文档状态校准（Documentation Status Alignment）**
  - 全面审计 44 个 Native 工具（335+ actions）的实际代码功能
  - 修正 ROADMAP.md Phase 5.3：ManageCinemachineTool (20 actions) 和 ManageUIToolkitTool (20 actions) 标记为已完成
  - ManageXRTool 标记为 `[!]` 冻结（项目不涉及 VR/AR/MR）
  - 16 份 plans/ 文档全部添加状态标注（历史归档 / 已完成 / 部分落地）
  - 新增 ADR-3：「文档状态必须以代码事实校准」
  - ROADMAP §7.4 新增「文档状态索引」，列出所有计划文档的当前状态
  - ROADMAP §8「下一步行动建议」更新为 v0.4.6 后的实际优先级

### Fixed
- 修正 ROADMAP 中多处"未开始"标记与实际代码已完成的不一致
- ADR 编号修正为连续序列（ADR-1, ADR-2, ADR-3）

## [0.4.6] - 2026-05-12

### Changed
- **ChatWindow partial class 拆分**
  - 将 2135 行的单体 `ChatWindow.cs` 拆分为 9 个 partial 文件（1 主文件 + 8 分区文件）
  - `ChatWindow.cs` — 主文件：常量、字段、静态缓存、菜单入口、CreateGUI、OnDestroy、InitializeAgentLoop
  - `ChatWindow.Input.cs` — 用户输入：发送、取消、输入框快捷键、窗口快捷键
  - `ChatWindow.Events.cs` — 事件处理：HandleAgentEvent、UpdateUIState
  - `ChatWindow.Messages.cs` — 消息 UI：气泡创建、流式追加、错误显示、重试、重建、滚动
  - `ChatWindow.DomainReload.cs` — Domain Reload 通知卡片：创建、详情行、状态更新
  - `ChatWindow.Restore.cs` — 会话恢复：TryRestoreSession、EnsureSessionExists
  - `ChatWindow.Hub.cs` — Hub 模块切换：模块面板可见性、Knowledge ask-agent、侧边栏
  - `ChatWindow.Sessions.cs` — 会话管理：列表刷新、切换、新建、重命名、删除、导出、相对时间
  - `ChatWindow.Tools.cs` — 工具调用 UI：分组管理、卡片状态、轮次分隔线
  - `ChatWindow.UIHelpers.cs` — UI 辅助：状态标签、发送按钮、取消按钮
  - 纯机械移动，零行为变更，所有字段保留在主文件中供 partial 共享

## [0.4.5] - 2026-05-12

### Changed
- **AgentLoop partial class 拆分**
  - 将 2086 行的单体 `AgentLoop.cs` 拆分为 9 个 partial 文件（1 主文件 + 8 分区文件）
  - `AgentLoop.cs` — 主文件：事件、属性、字段、构造函数、Initialize、SendMessage、Cancel、Reset、LoadSession、Dispose
  - `AgentLoop.FileChanges.cs` — 文件变更追踪恢复与事件发射
  - `AgentLoop.Events.cs` — SetState、EmitEvent
  - `AgentLoop.Memory.cs` — 记忆召回：搜索、格式化、注入
  - `AgentLoop.LLM.cs` — LLM 流式调用与 chunk 回调
  - `AgentLoop.Tools.cs` — 工具定义构建、工具执行、结果消息构建
  - `AgentLoop.Runner.cs` — RunToolCallLoop 主循环、最终响应处理、失败计数
  - `AgentLoop.DomainReload.cs` — Domain Reload 保存与恢复全流程
  - `AgentLoop.Sanitization.cs` — 消息历史清理（tool_use/tool_result 配对修复）
  - 纯机械移动，零行为变更，所有字段保留在主文件中供 partial 共享

## [0.4.4] - 2026-05-12

### Added
- **JSON Schema 参数预校验**
  - 新增 `ToolParameterValidator` (`Editor/Tools/Infrastructure/ToolParameterValidator.cs`)
  - 支持 JSON Schema 子集：`required`、`properties`、`type` (string/integer/number/boolean/array/object)、`enum`
  - 在 `ToolCallDispatcher.DispatchAsync` 中于工具执行前自动校验参数
  - 校验失败时直接返回 `ToolResult.Fail`，不调用具体工具
  - 空 schema 或无 properties 时保持宽松，允许执行
  - 未声明的额外字段默认允许
- **Schema 校验测试**
  - 新增 `ToolCallDispatcherSchemaValidationTests` (18 cases)
  - 覆盖 required 缺失、类型错误、enum 不匹配、合法参数、空 schema 等场景

### Changed
- `ToolCallDispatcher.DispatchAsync` 新增第 3 步 schema 校验（原第 3 步"执行工具"变为第 4 步）

## [0.4.3] - 2026-05-12

### Added
- **测试基础设施**
  - 新增 `AgentCore.Tests.Editor` 测试程序集 (`Editor/Tests/AgentCore.Tests.Editor.asmdef`)
  - 基于 Unity Test Framework (NUnit)，Editor-only，与主程序集隔离
- **核心单元测试**
  - `ToolResponseTests` — 覆盖 `ToolResponse.Ok/OkWithData/Fail/ToJson/ToToolResult` 及 `ToolResult` 全路径 (20 cases)
  - `JsonHelperTests` — 覆盖 `Serialize/Deserialize/ParseObject/ParseArray/GetString/GetInt/GetBool` 含异常与边界 (16 cases)
  - `TokenCounterTests` — 覆盖 `EstimateTokens/EstimateMessageTokens/EstimateConversationTokens` 含 CJK 与混合文本 (14 cases)
  - `ToolHelpersTests` — 覆盖参数解析、枚举解析、Vector3/Color/Quaternion 解析与序列化 (22 cases)

### Changed
- 无运行时行为变更，本版本仅新增测试代码

## [0.4.2] - 2026-05-11

### Added
- **MemoryPanel UI**
  - Hub 的 Memory 模块新增可视化管理面板，支持 mem0 状态查看、连接测试和用户创建
  - 支持手动添加长期记忆、搜索记忆、刷新记忆列表和删除记忆
  - 新增记忆列表条目展示内容、创建时间、更新时间、状态和搜索相关度

### Changed
- Memory 模块从占位页面升级为可操作面板，并接入 `ChatWindow` 生命周期以在模块切换和窗口关闭时取消非必要请求

## [0.3.8] - 2026-05-09

### Added
- **知识库查询增强**
  - `manage_knowledge` 的 `query` action 新增 `top_k` 参数（默认 5，范围 1~50）
  - 查询结果中每条 source 新增 `document_name` 字段，显示来源文档名
- **知识库批量索引**
  - 新增 `index_folder` action：批量索引指定文件夹中的所有支持类型文件
  - 新增 `index_project_docs` action：一键自动索引 README.md、docs/、plans/、Assets/Docs/、Assets/Documentation/
  - KnowledgeBasePanel UI 新增 `[索引项目文档]` 按钮，提供一键索引入口
- **知识库索引进度查询**
  - 新增 `check_index_status` action：通过 `track_id` 查询异步索引进度
- **SOUL.md 知识库引导**
  - 新增 §12 知识库检索，明确 LLM 何时应查询/索引知识库，以及与记忆系统的区别

### Changed
- `manage_knowledge` 工具描述和参数 Schema 同步更新，覆盖全部 8 个 action
- TOOLS.md.template 知识库检索章节重写，包含完整工作流建议

## [0.3.7] - 2026-05-08

### Changed
- **Settings 界面重组**
  - 顶部新增 AgentCore 状态概览，集中显示 LLM、mem0、LightRAG 与工具启用状态
  - 将设置按用户工作流重排为 Setup、Agent、Context & Memory、Tools、Appearance、About & Diagnostics
  - mem0 与 LightRAG 改为可折叠卡片，默认降低可选云服务对主配置流程的干扰
  - Agent 高级 token/错误上限参数移动到 Advanced Limits 折叠区
  - About 区域移除过时 Phase 文案，改为显示包名与实际版本

### Added
- **Settings 诊断操作**
  - 新增 Diagnostics 区域，可快速测试 LLM、mem0、LightRAG 连接
  - 新增快速打开或创建 `MEMORY.md` / `USER.md` 的入口
  - Tool Management 新增安全模式与完整模式预设

### Fixed
- 统一 LLM、mem0、LightRAG 的连接状态显示逻辑，避免不同区域使用不一致的颜色和字符串判断

## [0.3.6] - 2026-05-07

### Added
- **ManageUIToolkitTool** — 全新 UI Toolkit 工具（`manage_ui_toolkit`），20 个 actions
  - 创建/编辑 UXML 文件：`create_uxml`, `add_element`, `remove_element`, `set_attribute`, `validate_uxml`
  - 创建/编辑 USS 文件：`create_uss`, `set_style`, `add_class`, `remove_class`
  - 查询与列举：`query_element`, `list_elements`, `get_uxml_content`, `get_uss_content`, `list_assets`, `list_ui_documents`
  - 运行时配置：`create_panel_settings`, `configure_ui_document`
  - 代码模板生成：`create_editor_window_template`, `create_custom_element_template`
  - 数据绑定：`add_binding`
  - 使用 `System.Xml.XmlDocument` 操作 UXML，直接使用 `UnityEngine.UIElements` 类型（无需反射）

- **ManageCinemachineTool 增强**（`manage_cinemachine`，29% → ~65%）
  - 新增 10 个 actions：`create_freelook`, `configure_freelook_orbits`, `create_state_driven`, `add_state_camera`, `create_clearshot`, `create_sequencer`, `add_sequencer_entry`, `create_dolly_track`, `configure_impulse`, `set_blend_list`
  - 支持 FreeLook 三轨道配置（top/mid/bot 半径和高度）
  - 支持 StateDriven 相机与 Animator 状态绑定
  - 支持 ClearShot、Sequencer、Dolly Track、Impulse 和 BlendList 相机类型
  - 所有新 handler 通过反射兼容 Cinemachine 2.x 和 3.x

- **ManageUITool 增强**（`manage_ui`，35% → ~65%）
  - 新增 9 个 actions：`align_elements`, `distribute_elements`, `delete_element`, `duplicate_element`, `set_text`, `set_image`, `set_interactable`, `reorder_element`, `find_element`
  - `set_text` 同时支持 `UnityEngine.UI.Text` 和 `TMPro.TextMeshProUGUI`（通过反射）
  - `align_elements` / `distribute_elements` 支持 X/Y 轴对齐和均匀分布
  - 更新描述以明确区分 legacy uGUI（`manage_ui`）和 UI Toolkit（`manage_ui_toolkit`）

- **ValidationTool** — 全新场景验证工具（`validation`），10 个 actions
  - `check_missing_references` — 使用 `SerializedObject` 迭代器检测丢失的对象引用
  - `check_duplicate_names` — 检测场景中重名的 GameObject
  - `check_empty_gameobjects` — 检测只有 Transform 且无子对象的空 GameObject
  - `check_missing_components` — 检测 null 组件槽（已删除的脚本）
  - `check_layer_tags` — 验证 Layer 索引和 Tag 有效性
  - `check_performance` — 检测高三角面数（>50K）、过多实时灯光（>4）、多摄像机（>3）等性能问题
  - `check_prefab_integrity` — 使用 `PrefabUtility` 检测断开/丢失的 Prefab 连接
  - `check_audio` — 检测 AudioSource 缺失 Clip、零音量、无 Clip 时 PlayOnAwake 等问题
  - `validate_scene` — 运行所有检查并汇总结果
  - `validate_project` — 检查 Build Settings、缺失场景文件、损坏脚本、PlayerSettings
  - 返回结构化 `ValidationIssue` 对象，包含 severity/category/path/message/fix_hint

- **ReadConsoleTool 增强**（`read_console`，50% → ~80%）
  - 新增 5 个 actions：`get_system_info`, `get_assembly_info`, `get_scripting_defines`, `set_scripting_define`, `get_log_file`
  - `get_system_info` — 返回 Unity 版本、OS、处理器、内存、图形设备、脚本后端、渲染管线等完整系统信息
  - `get_assembly_info` — 列出所有已加载程序集，支持名称过滤
  - `get_scripting_defines` — 获取指定 Build Target Group 的 Scripting Define Symbols
  - `set_scripting_define` — 添加或移除 Scripting Define Symbol（自动触发重编译）
  - `get_log_file` — 读取 Unity Editor 日志文件末尾 N 行，支持文本过滤，跨平台路径（Windows/macOS/Linux）

- **ManageProBuilderTool 增强**（`manage_probuilder`，45% → ~75%）
  - 新增 8 个 actions：`get_faces`, `extrude_faces`, `delete_faces`, `bevel_edges`, `bridge_edges`, `weld_vertices`, `set_uv_projection`, `triangulate`
  - `subdivide` 现在尝试 ProBuilder API，失败时回退到手动三角形四分法
  - 所有新 actions 优先使用 ProBuilder 反射 API，不可用时提供 Unity Mesh API 回退实现
  - UV 投影支持 planar/box/spherical/cylindrical 四种模式
  - `weld_vertices` 支持按距离阈值合并顶点
  - 新增辅助方法：`GetFacesData`, `GetFaceObjects`, `GetAllFaceObjects`, `GetEdgeObjects`
  - 新增 Mesh 回退方法：`SubdivideMeshFallback`, `DeleteMeshFacesFallback`, `WeldMeshVerticesFallback`, `GenerateUVsFallback`

- **WorkflowTool** — 全新工作流自动化工具（`workflow`），15 个 actions
  - 批量操作：`batch_rename`（支持 `{index}`, `{name}`, `{parent}` 占位符和格式化索引）, `batch_set_tag`, `batch_set_layer`, `batch_set_active`, `batch_set_static`
  - 查找替换：`find_replace_name`（支持纯文本和正则表达式）
  - 收集查询：`collect_by_component`, `collect_by_tag`, `collect_by_layer`
  - 层级操作：`snapshot_hierarchy`（导出场景树为 JSON）, `batch_move_to_parent`
  - 组件操作：`batch_add_component`, `batch_remove_component`
  - 统计分析：`count_objects`（按 tag/layer/component 统计）, `list_scenes`（列出所有场景）
  - 所有修改操作支持 `dry_run` 预览模式（不实际执行，仅返回将要发生的变更）
  - 所有修改操作支持 Undo（通过 `ToolHelpers.RecordUndo`）

## [0.3.5] - 2026-05-07

### Added
- **窗口级键盘快捷键**（Phase 4.3）
  - 快捷键现在在整个 ChatWindow 范围内有效，不再要求输入框必须聚焦
  - `Escape` — 取消当前 Agent 操作（全局有效，之前仅输入框聚焦时有效）
  - `Ctrl+N` — 新建会话（全局有效）
  - `Ctrl+Shift+E` — 导出当前会话（全局有效）
  - `Ctrl+/` 或 `Ctrl+?` — 新增：聚焦输入框（方便从消息区域快速回到输入）
  - 输入框内的快捷键行为不变（`Enter` 发送、`Shift+Enter` 换行）
  - 通过在 `rootVisualElement` 上注册 `KeyDownEvent` 实现窗口级监听

## [0.3.4] - 2026-05-07

### Added
- **LLM Model 发现式下拉菜单**（Settings 面板增强）
  - Settings 面板 LLM Configuration 区域新增 "Fetch" 按钮
  - 点击 Fetch 后自动向 `{endpoint}/models` 发起 HTTP GET 请求，获取服务器可用模型列表
  - 获取成功后在 Model 字段旁显示 Popup 下拉菜单，支持一键选择模型
  - 支持 OpenAI 标准 `/v1/models` 响应格式（`{"object":"list","data":[{"id":"..."}]}`）
  - 模型列表按字母排序，方便查找
  - Fetch 状态实时反馈：绿色 `[OK] 找到 N 个模型` / 红色 `[FAIL] 错误信息`
  - Fetch 与 Test Connection 按钮互斥，防止并发请求

### Changed
- **默认参数优化（针对 Claude 系列模型）**
  - `llmModel` 默认值：`"deepseek-chat"` → `"claude-sonnet-4-5"`
  - `maxTokens` 默认值：`4096` → `16000`（Claude 3.5/4 系列支持最大 16K 输出）
  - `reserveResponseTokens` 默认值：`2000` → `8000`（为长代码输出预留足够空间）
  - `AgentCoreSettings` 版本迁移升级至 v5（已有用户若仍使用旧默认值则自动迁移，自定义值不受影响）

## [0.3.3] - 2026-05-07

### Added
- **文件变更追踪与展示面板**（Phase 4.5）
  - `FileChangeTracker` — 追踪当前会话中所有工具调用产生的文件变更
    - 支持追踪 `manage_script`、`manage_file`、`manage_asset` 三类工具的文件操作
    - 执行前快照文件行数，执行后对比计算增减行数（`+N -N`）
    - 自动识别变更类型：新建（Created）、修改（Modified）、删除（Deleted）、移动（Moved）、复制（Copied）
    - 同一文件多次修改自动合并为一条摘要
    - **Domain Reload 持久化**：文件变更记录跨 Domain Reload 保留
      - `SerializeToJson()` / `RestoreFromJson()` — 序列化/反序列化变更记录
      - 在 `OnBeforeAssemblyReload` 中自动保存到 `DomainReloadState`
      - 在会话恢复时自动从 `DomainReloadState` 恢复
  - `FileChangeSummaryPanel` — 输入栏上方的可折叠文件变更汇总面板
    - 头部显示"此对话中已更改 N 个文件" + 总增减行数统计
    - 每行显示变更类型图标（彩色）、文件路径、增减行数
    - 单击文件行：在 Project 窗口中高亮定位（`EditorGUIUtility.PingObject`）
    - 双击文件行：在 IDE 中打开文件（`AssetDatabase.OpenAsset`）
    - 无变更时自动隐藏，有变更时自动显示
    - 会话切换/重置时自动清空
    - Domain Reload 后自动恢复显示
  - `AgentEventType.FileChangesUpdated` — 新增文件变更更新事件类型
  - `AgentEvent.FileChangesUpdated()` — 新增文件变更事件工厂方法
  - `AgentLoop.FileTracker` — 公开属性供 UI 层访问文件变更追踪器
  - `AgentLoop.EmitFileChangesUpdatedEvent()` — 公开方法供 UI 层在会话恢复后触发文件变更面板更新
  - `DomainReloadState.SaveFileChangeRecords()` / `ClearFileChangeRecords()` — 文件变更数据的持久化管理

## [0.3.2] - 2026-05-06

### Added
- **轻量级 Markdown 格式化**（Phase 4.1）
  - `ContentFilter.FormatMarkdown()` — 将 Markdown 语法转换为可读的纯文本格式（不使用任何 Rich Text 标签）
  - 标题格式化：`# H1` → `═══ H1 ═══`，`## H2` → `── H2 ──`，`### H3` → `【H3】`，`#### H4` → `▸ H4`
  - 表格格式化：解析 `| col | col |` 语法，生成对齐的纯文本表格（带 box-drawing 字符）
  - 粗体/斜体：`**text**` → `text`，`*text*` → `text`（直接去除标记符号）
  - 列表格式化：`- item` → `  · item`，`1. item` → `  1) item`
  - 代码块：保持内容不变，添加 `──── lang ────` 装饰分隔线
  - 引用块：`> text` → `  │ text`
  - 水平线：`---` → `────────────────────`
  - 内联代码：`` `code` `` → `[code]`
  - 链接：`[text](url)` → `text (url)`
  - CJK 字符宽度感知的表格列对齐
  - 集成到 `FilterStreaming()` 和 `FilterCompleted()` 双管线，流式输出和最终化时均自动格式化
  - 修复 `MessageBubble.FinalizeContent()` 双重过滤问题
- **工具启用/禁用管理**（Phase 4.4）
  - `AgentCoreSettings` 新增 `disabledToolCategories` 和 `disabledTools` 列表
  - `ToolDefinitionBuilder.BuildAllEnabled()` — 构建工具定义时自动过滤禁用工具
  - `AgentLoop.BuildToolDefinitions()` 使用过滤后的工具列表，减少 token 消耗
  - `BootstrapLoader.GenerateActiveToolsList()` 仅展示启用的工具
  - Settings 面板新增 **Tool Management** 区域：
    - 按分类折叠显示所有已注册工具
    - 支持按分类整体启用/禁用
    - 支持单个工具启用/禁用
    - 全部启用/全部禁用快捷按钮
    - 实时显示启用/禁用工具数量统计
- **错误重试 UI**（Phase 4.2）
  - 错误消息气泡底部显示「 重试」按钮
  - 点击重试按钮自动重新发送上一条用户消息
  - 重试按钮点击后自动禁用防止重复操作
  - `MessageBubble.AddRetryButton()` — 支持为错误气泡添加重试回调
- **结构化错误展示**（Phase 4.2 增强）
  - `ErrorDetail` 类 — 结构化错误信息，包含错误分类、异常类型、HTTP 状态码、堆栈摘要
  - `AgentEvent.ErrorEvent(Exception, string)` — 新增携带异常对象的错误事件重载
  - 错误气泡显示格式化的详细错误信息（错误类别、HTTP 状态码描述、异常类型、内部错误、上下文）
  - `MessageBubble.AddExpandableDetail()` — 可展开/折叠的堆栈信息区域
  - 错误自动分类：认证失败、网络错误、请求超时、速率限制、服务端错误、模型错误等
  - HTTP 状态码自动提取和中文描述（401/403/429/500/502/503 等）

### Changed
- `AgentCoreSettings` 版本迁移升级至 v4（初始化工具管理列表）
- `ToolDefinitionBuilder` 新增 `using AgentCore.Editor.Config` 依赖
- `BootstrapLoader` 工具列表生成逻辑增加禁用工具过滤和统计
- 错误气泡样式增强 — 左侧红色边框、更深背景色、更高对比度文字
- `AgentLoop` 错误事件传递完整异常对象（LLM 请求、Domain Reload 恢复）
- `ChatWindow.ShowError()` 支持 `ErrorDetail` 参数，展示结构化错误信息

## [0.3.1] - 2026-05-06

### Added
- **FileSystem 工具** — `manage_file`：通用文件系统操作工具
  - 支持 9 种操作：`read_file`, `write_file`, `list_directory`, `search_content`, `file_info`, `delete`, `copy`, `move`, `create_directory`
  - 支持所有文件类型（json, xml, yaml, txt, md, shader 等），不限于 .cs 文件
  - 支持项目根目录下所有路径（Assets/, Packages/, ProjectSettings/ 等）
  - 正则表达式内容搜索（类似 grep），带上下文行显示
  - 文件读取支持行范围（offset/limit）和行号显示
  - 路径安全检查，防止目录遍历攻击
  - 自动处理 Unity .meta 文件（删除/移动时同步处理）
  - 补充 `manage_script`（仅 C#）和 `manage_asset`（仅 AssetDatabase）的能力空白
- TOOLS.md.template 添加 FileSystem 操作指南和工具选择指南更新

### Fixed
- LightRAG 客户端兼容 LightRAG Server v1.4.15 API 变更
  - Health API 状态值从 `"ok"` 改为同时兼容 `"ok"` 和 `"healthy"`
  - Health API 版本字段从 `version` 改为优先使用 `core_version`（兼容旧版 `version`）
  - Query API 来源字段从 `sources` 改为优先使用 `references`（兼容旧版 `sources`）
  - 文件上传 API 路径从 `/documents/file` 修正为 `/documents/upload`
  - 默认端点端口从 `18920` 修正为 `9621`
- 会话标题不自动生成的 bug — 新会话始终显示"新会话"而不根据首条消息生成标题

## [0.3.0] - 2026-05-06

### Added
- Phase 1: 核心骨架 + Bootstrap Files
  - UPM 包结构
  - LLM 客户端（OpenAI 兼容 API + SSE 流式）
  - Bootstrap Files 系统（SOUL/TOOLS/PROJECT/MEMORY/USER）
  - Agent Loop 基础版（单轮对话）
  - Chat Window 基础 UI（UI Toolkit）

- Phase 2: 工具系统基础架构
  - `IAgentTool` 接口定义
  - `ToolRegistry` 工具注册中心
  - `ToolCallDispatcher` 工具调用分发器
  - `ToolDefinitionBuilder` 工具定义构建器（生成 OpenAI function calling schema）
  - `ToolResult` 标准化返回类型

- Phase 2.5: 原生工具系统（完全移除 unity-mcp 依赖）
  - **工具基础设施**
    - `AgentToolAttribute` — 工具标记属性（名称、分类、主线程要求等）
    - `ToolAutoDiscovery` — 基于反射的工具自动发现与注册机制
    - `ToolHelpers` — 参数解析、GameObject 查找、Vector/Color 解析等辅助方法
    - `ToolResponse` — 标准化 JSON 响应格式（Ok/OkWithData/Fail）
  - **Core 工具（5 个）** — 场景与对象操作
    - `manage_scene` — 场景 CRUD（创建/加载/保存/获取层级）
    - `manage_gameobject` — GameObject 创建/修改/删除/复制
    - `manage_component` — 组件添加/移除/属性设置
    - `find_gameobjects` — 按名称/标签/层/组件类型搜索
    - `scene_analysis` — 场景分析（层级统计、性能热点、依赖关系）
  - **Meta 工具（3 个）** — 编辑器控制
    - `manage_editor` — 编辑器状态控制（Play/Pause/Stop/标签/层）
    - `execute_menu_item` — 执行 Unity 菜单项
    - `batch_execute` — 批量执行多个工具调用
  - **Scripting 工具（4 个）** — 代码与预制体
    - `manage_script` — C# 脚本创建/读取/删除
    - `execute_code` — 在编辑器中执行 C# 代码片段
    - `manage_prefab` — 预制体信息/层级/内容修改
    - `manage_scriptable_object` — ScriptableObject 资产创建/读取/修改
  - **Specialized 工具（11 个）** — 专业领域
    - `manage_physics` — 物理设置/碰撞矩阵/射线检测/力
    - `manage_lighting` — 光照/烘焙/探针/环境设置
    - `manage_graphics` — 渲染管线/后处理/Volume
    - `manage_audio` — 音频源/监听器/混音器
    - `manage_ui` — UI Toolkit 文档/样式/面板
    - `manage_camera` — 相机设置/视口/渲染目标
    - `manage_cinemachine` — Cinemachine 虚拟相机/轨道（可选包，反射调用）
    - `manage_event` — UnityEvent 检查/绑定/触发
    - `manage_probuilder` — ProBuilder 网格编辑（可选包，反射调用）
    - `manage_terrain` — 地形创建/高度图/纹理/树木/细节
    - `manage_timeline` — Timeline 轨道/剪辑/信号（可选包，反射调用）
  - **Utility 工具（8 个）** — 资产与材质
    - `manage_asset` — 资产搜索/创建/导入/移动
    - `manage_material` — 材质创建/属性设置/分配
    - `manage_shader` — Shader CRUD
    - `manage_animation` — 动画控制器/剪辑/状态机
    - `read_console` — 读取 Unity Console 日志/错误/警告
    - `manage_asset_import` — 资产导入设置（通用）
    - `manage_model_import` — 模型导入设置（FBX/OBJ 等）
    - `manage_texture_import` — 纹理导入设置（压缩/尺寸/格式）
  - **Extended 工具（10 个）** — 扩展功能
    - `manage_build` — 构建设置/平台切换/触发构建
    - `manage_input` — 输入系统/Action Map 管理
    - `manage_navmesh` — 导航网格烘焙/代理/障碍物
    - `manage_profiler` — 性能分析/帧计时/内存
    - `manage_tags_layers` — 标签与层的增删管理
    - `manage_package` — UPM 包安装/查询/移除
    - `manage_test` — 测试运行/列表/模板创建（Test Framework）
    - `cleaner` — 清理缺失引用/未使用资产/空 GameObject
    - `optimization` — 性能优化建议/批量优化操作
    - `smart_operations` — 智能批量操作（对齐/分布/替换/重命名）

- Phase 3: 云端工具与会话管理
  - **mem0 记忆服务**
    - `Mem0Client` — mem0 REST API 客户端（连接测试、记忆 CRUD、用户管理）
    - `Mem0Tool`（`manage_memory`）— 记忆管理工具（search/add/list/delete）
    - `AutoMemoryStrategy` — 会话结束时自动提取关键信息存入 mem0
  - **LightRAG 知识库**
    - `LightRAGClient` — LightRAG REST API 客户端（查询、索引、健康检查）
    - `LightRAGTool`（`manage_knowledge`）— 知识库管理工具（query/index_text）
  - **会话管理**
    - `SessionManager` / `SessionStorage` / `SessionData` — 会话持久化与恢复
    - `SessionExporter` — 会话导出（Markdown / JSON）
    - 多会话侧边栏 — 会话列表、切换、重命名、删除
  - **上下文窗口管理**
    - `ContextWindowManager` — 基于 token 计数的滑动窗口截断
    - `TokenCounter` — 消息 token 估算
  - **核心增强**
    - `FallbackRouter` — LLM 请求自动重试（可重试错误判断）
    - `CompilationWatcher` — 编译监控与 Domain Reload 恢复
    - `ConsoleErrorCapture` — Console 错误自动捕获
    - `DomainReloadState` — Domain Reload 状态持久化与恢复
    - `ErrorInfoCollector` — 错误信息收集与格式化
  - **Settings 面板**
    - LLM 连接配置与测试
    - mem0 服务配置与连接测试
    - LightRAG 服务配置与连接测试
    - Agent 行为参数（maxToolCallRounds、上下文窗口等）
    - Bootstrap 文件管理（MEMORY.md / USER.md 创建与打开）
    - UI 偏好设置

### Removed
- Phase 2.5: 完全移除 unity-mcp 外部依赖
  - 不再依赖 `com.coplaydev.unity-mcp` 包
  - 不再需要 Python MCP Server 桥接层
  - 所有 Unity 操作通过原生 C# 工具直接执行
