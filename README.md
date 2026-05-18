# AgentCore Unity

> Unity Editor 内置 AI Agent 插件 — 通过自然语言对话驱动 Unity 开发工作流

## 概述

AgentCore Unity 是一个 Unity Editor 插件，提供类 ChatGPT 的对话窗口，让开发者通过自然语言与 AI Agent 交互，完成场景搭建、代码编写、资源管理等 Unity 开发任务。

### 核心特性

- 🧠 **智能 Agent Loop** — "Loop until final answer" 模式，自主规划和执行多步任务
- 🔧 **40+ 内置工具** — 覆盖场景、脚本、资产、物理、UI、地形、相机、动画、构建等 Unity 核心功能，共 335+ 个 actions
- 🔄 **自主纠错能力** — 错误即信息、自动编译检查、Console 错误捕获、Fallback 路由
- 💬 **多会话管理** — 标签页式多会话，支持历史记录和上下文恢复
- 📝 **Bootstrap Files** — SOUL/TOOLS/PROJECT/MEMORY/USER 五层系统提示词
- 🔌 **UPM 包分发** — 标准 Unity Package Manager 安装
- 🔍 **工具自动发现** — 基于 `[AgentTool]` 属性的反射自动注册机制
- 🧩 **Domain Reload 恢复** — 脚本修改触发重编译后自动恢复对话上下文
- 📊 **文件变更追踪** — 实时追踪工具调用产生的文件变更，可视化展示增减行数
- 🎛️ **工具启用/禁用** — Settings 面板中按分类或单个工具控制启用状态

## 架构

详见 [plans/ARCHITECTURE.md](plans/ARCHITECTURE.md)

## 技术栈

| 层级 | 技术 |
|------|------|
| UI | Unity UI Toolkit (UXML/USS) |
| Agent 核心 | C# 9.0 (.NET Standard 2.1) |
| LLM 通信 | OpenAI-compatible API via LiteLLM |
| 记忆系统 | Mem0 (语义记忆) + LightRAG (知识库) |
| Unity 工具 | 原生 C# 工具（反射自动发现 + ToolRegistry） |
| 包格式 | UPM (Unity Package Manager) |

## 工具列表 (44 个工具, 335+ actions)

### Core — 场景与对象操作 (5 个工具)
| 工具 | Actions | 说明 |
|------|---------|------|
| `manage_scene` | 15 | 场景 CRUD、打开/保存/合并、构建场景管理 |
| `manage_gameobject` | 12 | GameObject 创建/修改/删除/复制，含批量操作和网格排列 |
| `manage_component` | 11 | 组件添加/移除/属性设置，含批量操作和组件复制 |
| `find_gameobjects` | — | 按名称/标签/层/组件搜索 GameObject |
| `scene_analysis` | 10 | 场景分析：健康检查、组件统计、热点分析、依赖分析、性能提示 |

### Meta — 编辑器控制 (3 个工具)
| 工具 | Actions | 说明 |
|------|---------|------|
| `manage_editor` | 8 | 编辑器状态控制（Play/Pause/Stop）、选择、项目设置 |
| `execute_menu_item` | 3 | 执行/列出/验证 Unity 菜单项 |
| `batch_execute` | — | 批量执行多个工具调用，支持事务模式 |

### Scripting — 代码与数据 (4 个工具)
| 工具 | Actions | 说明 |
|------|---------|------|
| `manage_script` | 10 | C# 脚本 CRUD、分析、查找引用、添加方法/字段 |
| `execute_code` | 1 | 在编辑器中执行任意 C# 表达式 |
| `manage_prefab` | 6 | 预制体创建/实例化/解包/应用/还原 |
| `manage_scriptable_object` | 10 | ScriptableObject CRUD、JSON 导入导出、批量设置 |

### Specialized — 专业领域 (11 个工具)
| 工具 | Actions | 说明 |
|------|---------|------|
| `manage_physics` | 10 | 物理系统：刚体/碰撞体/关节/射线检测/重叠测试 |
| `manage_lighting` | 6 | 光照创建/修改、烘焙、光照贴图设置 |
| `manage_graphics` | 5 | 渲染设置、质量设置管理 |
| `manage_audio` | 7 | 音频源管理、播放控制、音频设置 |
| `manage_ui` | 9 | uGUI 系统：Canvas/元素创建、布局、组件添加 |
| `manage_camera` | 9 | 相机创建/配置、对齐视图、渲染到纹理 |
| `manage_cinemachine` | 10 | Cinemachine 虚拟相机、目标设置、Body/Aim/Noise 配置 |
| `manage_event` | 8 | UnityEvent 监听器管理、事件调用 |
| `manage_terrain` | 10 | 地形创建、高度编辑、Perlin 噪声、纹理绘制、树木种植 |
| `manage_timeline` | 9 | Timeline 创建、轨道/Clip 管理、播放控制 |
| `manage_probuilder` | 10 | ProBuilder 建模：形状创建、材质设置、网格操作 |

### Utility — 资产与资源 (8 个工具)
| 工具 | Actions | 说明 |
|------|---------|------|
| `manage_asset` | 8 | 资产搜索/创建/删除/移动/复制/导入/依赖分析 |
| `manage_material` | 11 | 材质创建/属性设置/Shader 切换/关键字管理 |
| `manage_shader` | 8 | Shader 列表/信息/搜索/关键字/属性查询 |
| `manage_animation` | 9 | 动画控制器信息、参数管理、层权重、Clip 创建 |
| `manage_asset_import` | 9 | 资产导入器设置、批量重导入、标签管理 |
| `manage_model_import` | 10 | 3D 模型导入设置、网格/材质/动画/Rig 信息 |
| `manage_texture_import` | 10 | 纹理导入设置、平台设置、Sprite 设置 |
| `read_console` | 5 | Unity Console 日志读取：错误/警告/全部/计数/清除 |

### Extended — 扩展功能 (9 个工具)
| 工具 | Actions | 说明 |
|------|---------|------|
| `manage_build` | 10 | 构建设置、目标平台、场景列表、Player 设置 |
| `manage_input` | 5 | 输入轴管理、按键模拟 |
| `manage_navmesh` | 9 | 导航网格烘焙/清除、Agent/Obstacle 添加 |
| `manage_profiler` | 5 | 性能分析：统计/内存/渲染/录制 |
| `manage_tags_layers` | 9 | 标签/层/排序层管理 |
| `manage_package` | 9 | UPM 包列表/搜索/安装/移除/版本查询 |
| `manage_test` | 7 | 测试列表/运行/结果/创建 |
| `optimization` | 10 | 场景分析、纹理/网格/音频优化、LOD 组 |
| `cleaner` | 10 | 查找未使用/重复资源、缺失引用、空文件夹 |
| `smart_operations` | 7 | 对齐/分布/吸附/随机化/替换/按条件选择 |

### Cloud — 云端服务 (2 个工具)
| 工具 | Actions | 说明 |
|------|---------|------|
| `manage_memory` | 4 | Mem0 语义记忆：添加/搜索/列出/删除 |
| `manage_knowledge` | 2 | LightRAG 知识库：查询/索引 |

### FileSystem — 文件操作 (1 个工具)
| 工具 | Actions | 说明 |
|------|---------|------|
| `manage_file` | 9 | 通用文件操作：读写/列目录/搜索内容/复制/移动/删除 |

## 开发状态

🚧 **开发中** — v0.5.1 (Phase 5: 夯实基础 — 测试框架 + RAG 补齐)

### 已完成的阶段
- ✅ Phase 1: 核心架构（Agent Loop、LLM 客户端、会话管理）
- ✅ Phase 2: 基础工具系统（场景/对象/组件/脚本/资产）
- ✅ Phase 2.5: 原生工具扩展（物理/光照/UI/音频/构建/导航等）
- ✅ Phase 3: Domain Reload 恢复、上下文窗口管理、Fallback 路由
- ✅ Phase 4: UI 增强（Markdown 格式化、错误重试、工具管理、文件变更追踪）

## 目录结构

```
com.agentcore.unity/
├── Editor/
│   ├── AgentCore.Editor.asmdef
│   ├── Bootstrap/              # 启动引导与系统提示词
│   │   └── Resources/          # SOUL.md, TOOLS.md.template
│   ├── Config/                 # 设置、密钥存储
│   ├── Core/                   # Agent Loop、状态机、编译监控、文件追踪
│   ├── LLM/                    # LLM 客户端与流式解析
│   ├── Session/                # 会话管理、自动记忆
│   ├── Tools/                  # 工具系统
│   │   ├── Infrastructure/     # 工具基础设施（属性、自动发现）
│   │   ├── Native/             # 原生工具实现
│   │   │   ├── Core/           # 场景/对象/组件/场景分析
│   │   │   ├── Meta/           # 编辑器控制/批量执行/菜单项
│   │   │   ├── Scripting/      # 脚本/代码执行/预制体/ScriptableObject
│   │   │   ├── Specialized/    # 物理/光照/图形/音频/UI/相机/地形/Timeline/Cinemachine/ProBuilder/事件
│   │   │   ├── Utility/        # 资产/材质/Shader/动画/导入器/Console
│   │   │   └── Extended/       # 构建/输入/导航/性能/标签层/包管理/测试/优化/清理/智能操作
│   │   ├── Cloud/              # 云端工具（Mem0 记忆、LightRAG 知识库）
│   │   └── FileSystem/         # 文件系统工具
│   ├── UI/                     # Chat Window UI
│   │   └── Components/         # UI 组件（消息气泡、文件变更面板）
│   └── Utils/                  # 通用工具类
├── plans/                      # 设计文档
├── package.json
└── README.md
```

## License

Internal use only.
