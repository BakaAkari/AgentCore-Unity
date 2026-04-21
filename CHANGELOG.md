# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.0] - Unreleased

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
  - **Core 工具（4 个）** — 场景与对象操作
    - `manage_scene` — 场景 CRUD（创建/加载/保存/获取层级）
    - `manage_gameobject` — GameObject 创建/修改/删除/复制
    - `manage_component` — 组件添加/移除/属性设置
    - `find_gameobjects` — 按名称/标签/层/组件类型搜索
  - **Meta 工具（3 个）** — 编辑器控制
    - `manage_editor` — 编辑器状态控制（Play/Pause/Stop/标签/层）
    - `execute_menu_item` — 执行 Unity 菜单项
    - `batch_execute` — 批量执行多个工具调用
  - **Scripting 工具（3 个）** — 代码与预制体
    - `manage_script` — C# 脚本创建/读取/删除
    - `execute_code` — 在编辑器中执行 C# 代码片段
    - `manage_prefab` — 预制体信息/层级/内容修改
  - **Specialized 工具（5 个）** — 专业领域
    - `manage_physics` — 物理设置/碰撞矩阵/射线检测/力
    - `manage_lighting` — 光照/烘焙/探针/环境设置
    - `manage_graphics` — 渲染管线/后处理/Volume
    - `manage_audio` — 音频源/监听器/混音器
    - `manage_ui` — UI Toolkit 文档/样式/面板
  - **Utility 工具（4 个）** — 资产与材质
    - `manage_asset` — 资产搜索/创建/导入/移动
    - `manage_material` — 材质创建/属性设置/分配
    - `manage_shader` — Shader CRUD
    - `manage_animation` — 动画控制器/剪辑/状态机
  - **Extended 工具（5 个）** — 扩展功能
    - `manage_build` — 构建设置/平台切换/触发构建
    - `manage_input` — 输入系统/Action Map 管理
    - `manage_navmesh` — 导航网格烘焙/代理/障碍物
    - `manage_profiler` — 性能分析/帧计时/内存
    - `manage_tags_layers` — 标签与层的增删管理

### Removed
- Phase 2.5: 完全移除 unity-mcp 外部依赖
  - 不再依赖 `com.coplaydev.unity-mcp` 包
  - 不再需要 Python MCP Server 桥接层
  - 所有 Unity 操作通过原生 C# 工具直接执行
