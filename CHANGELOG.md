# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
  - 错误消息气泡底部显示「🔄 重试」按钮
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
