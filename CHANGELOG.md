# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
