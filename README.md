# AgentCore Unity

> Unity Editor 内置 AI Agent 插件 — 通过自然语言对话驱动 Unity 开发工作流

## 概述

AgentCore Unity 是一个 Unity Editor 插件，提供类 ChatGPT 的对话窗口，让开发者通过自然语言与 AI Agent 交互，完成场景搭建、代码编写、资源管理等 Unity 开发任务。

### 核心特性

- 🧠 **智能 Agent Loop** — "Loop until final answer" 模式，自主规划和执行多步任务
- 🔧 **双层工具系统** — 自研工具（mem0/LightRAG/文件系统）+ unity-mcp 36+ 工具
- 🔄 **自主纠错能力** — 错误即信息、自动编译检查、Console 错误捕获、Fallback 路由
- 💬 **多会话管理** — 标签页式多会话，支持历史记录和上下文恢复
- 📝 **Bootstrap Files** — SOUL/TOOLS/PROJECT/MEMORY/USER 五层系统提示词
- 🔌 **UPM 包分发** — 标准 Unity Package Manager 安装

## 架构

详见 [plans/ARCHITECTURE.md](plans/ARCHITECTURE.md)

## 技术栈

| 层级 | 技术 |
|------|------|
| UI | Unity UI Toolkit (UXML/USS) |
| Agent 核心 | C# (.NET Standard 2.1) |
| LLM 通信 | OpenAI-compatible API via LiteLLM |
| 记忆系统 | mem0 (语义记忆) + LightRAG (知识库) |
| Unity 工具 | CoplayDev/unity-mcp CommandRegistry |
| 包格式 | UPM (Unity Package Manager) |

## 开发状态

🚧 **开发中** — Phase 1: 核心骨架 + Bootstrap Files

## 目录结构

```
agentcore-unity/
├── plans/                    # 设计文档
│   └── ARCHITECTURE.md       # 完整架构设计
├── PROJECT-ANALYSIS.md       # 项目分析报告
├── _archive/                 # 旧项目归档（参考用）
├── .gitignore
└── README.md
```

> 开发启动后，UPM 包源码将放在 `Packages/com.agentcore.unity/` 下

## License

Internal use only.
