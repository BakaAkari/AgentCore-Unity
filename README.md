# AgentCore Unity

> Unity Editor 内置 AI Agent 插件 — 通过自然语言对话驱动 Unity 开发工作流

## 概述

AgentCore Unity 是一个 Unity Editor 插件，提供类 ChatGPT 的对话窗口，让开发者通过自然语言与 AI Agent 交互，完成场景搭建、代码编写、资源管理等 Unity 开发任务。

### 核心特性

- 🧠 **智能 Agent Loop** — "Loop until final answer" 模式，自主规划和执行多步任务
- 🔧 **原生工具系统** — 20+ 内置原生工具，覆盖场景、脚本、资产、物理、UI 等 Unity 核心功能
- 🔄 **自主纠错能力** — 错误即信息、自动编译检查、Console 错误捕获、Fallback 路由
- 💬 **多会话管理** — 标签页式多会话，支持历史记录和上下文恢复
- 📝 **Bootstrap Files** — SOUL/TOOLS/PROJECT/MEMORY/USER 五层系统提示词
- 🔌 **UPM 包分发** — 标准 Unity Package Manager 安装
- 🔍 **工具自动发现** — 基于 `[AgentTool]` 属性的反射自动注册机制

## 架构

详见 [plans/ARCHITECTURE.md](plans/ARCHITECTURE.md)

## 技术栈

| 层级 | 技术 |
|------|------|
| UI | Unity UI Toolkit (UXML/USS) |
| Agent 核心 | C# (.NET Standard 2.1) |
| LLM 通信 | OpenAI-compatible API via LiteLLM |
| 记忆系统 | mem0 (语义记忆) + LightRAG (知识库) |
| Unity 工具 | 原生 C# 工具（反射自动发现 + ToolRegistry） |
| 包格式 | UPM (Unity Package Manager) |

## 原生工具列表

### Core — 场景与对象操作
| 工具 | 说明 |
|------|------|
| `manage_scene` | 场景 CRUD 操作 |
| `manage_gameobject` | GameObject 创建/修改/删除 |
| `manage_component` | 组件添加/移除/属性设置 |
| `find_gameobjects` | 按名称/标签/层/组件搜索 |

### Meta — 编辑器控制
| 工具 | 说明 |
|------|------|
| `manage_editor` | 编辑器状态控制（Play/Pause/Stop） |
| `execute_menu_item` | 执行菜单项 |
| `batch_execute` | 批量执行多个工具调用 |

### Scripting — 代码与预制体
| 工具 | 说明 |
|------|------|
| `manage_script` | C# 脚本 CRUD |
| `execute_code` | 在编辑器中执行 C# 代码 |
| `manage_prefab` | 预制体操作 |

### Specialized — 专业领域
| 工具 | 说明 |
|------|------|
| `manage_physics` | 物理系统管理 |
| `manage_lighting` | 光照与烘焙 |
| `manage_graphics` | 渲染与后处理 |
| `manage_audio` | 音频系统管理 |
| `manage_ui` | UI 系统管理 |

### Utility — 资产与材质
| 工具 | 说明 |
|------|------|
| `manage_asset` | 资产搜索/创建/导入 |
| `manage_material` | 材质属性设置 |
| `manage_shader` | Shader 管理 |
| `manage_animation` | 动画系统管理 |

### Extended — 扩展功能
| 工具 | 说明 |
|------|------|
| `manage_build` | 构建管理 |
| `manage_input` | 输入系统管理 |
| `manage_navmesh` | 导航网格管理 |
| `manage_profiler` | 性能分析 |
| `manage_tags_layers` | 标签与层管理 |

## 开发状态

🚧 **开发中** — Phase 2.5: 原生工具系统

## 目录结构

```
com.agentcore.unity/
├── Editor/
│   ├── AgentCore.Editor.asmdef
│   ├── Bootstrap/              # 启动引导与系统提示词
│   │   └── Resources/          # SOUL.md, TOOLS.md.template
│   ├── Config/                 # 设置与密钥存储
│   ├── Core/                   # Agent Loop、消息类型、错误处理
│   ├── LLM/                    # LLM 客户端与流式解析
│   ├── Tools/                  # 工具系统
│   │   ├── Infrastructure/     # 工具基础设施（属性、自动发现、辅助方法）
│   │   └── Native/             # 原生工具实现
│   │       ├── Core/           # 场景/对象/组件操作
│   │       ├── Meta/           # 编辑器控制
│   │       ├── Scripting/      # 脚本/代码/预制体
│   │       ├── Specialized/    # 物理/光照/图形/音频/UI
│   │       ├── Utility/        # 资产/材质/Shader/动画
│   │       └── Extended/       # 构建/输入/导航/性能分析
│   ├── UI/                     # Chat Window UI
│   └── Utils/                  # 通用工具类
├── plans/                      # 设计文档
├── package.json
└── README.md
```

## License

Internal use only.
