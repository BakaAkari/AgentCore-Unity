# AgentCore Unity — 快速上手指南

> Unity Editor 内置 AI Agent 插件，通过自然语言对话驱动 Unity 开发工作流。

## 安装

1. Unity Editor → Window → Package Manager
2. 左上角 `+` → **Add package from tarball...**
3. 选择 `com.agentcore.unity-1.2.2.tgz`
4. 等待导入完成

**要求**: Unity 2021.3+

## 核心功能

| 能力域 | 说明 |
|--------|------|
| AI 对话 | 在 Editor 内与 LLM 对话，支持多轮工具调用 |
| 场景操作 | 创建/修改 GameObject、组件、材质、Prefab |
| 脚本编写 | 生成/修改 C# 脚本，自动编译检查 |
| 资源管理 | 导入设置、资源查找、清理优化 |
| 构建测试 | 触发构建、运行测试、性能分析 |
| 版本控制 | Git / SVN / Perforce 状态查看与操作（可选） |
| 代码索引 | Roslyn 符号搜索、依赖分析（可选） |
| 知识记忆 | 自动记忆关键决策，跨会话保留上下文 |

## 配置步骤

### 1. 打开设置

Edit → Project Settings → AgentCore

### 2. 配置 LLM 连接（必需）

| 设置项 | 说明 | 示例 |
|--------|------|------|
| API Endpoint | OpenAI 兼容 API 地址 | `https://api.openai.com/v1` 或 `https://openrouter.ai/api/v1` |
| API Key | 对应服务的密钥 | `sk-...` |
| Model | 模型标识 | `gpt-4o`、`claude-sonnet-4-20250514`、`deepseek-chat` |

支持任何 OpenAI 兼容 API（OpenAI、OpenRouter、Azure、本地 Ollama 等）。

### 3. 打开 Chat 窗口

Window → AgentCore → Chat

输入自然语言指令即可开始交互。

### 4. 可选配置

| 功能 | 启用方式 | 说明 |
|------|---------|------|
| VCS 组件 | Settings → Extensions → 启用 VCS | 添加 `AGENTCORE_VCS` 到 Scripting Define Symbols |
| Code Indexing | Settings → Extensions → 启用 Indexing | 添加 `AGENTCORE_INDEXING` 到 Scripting Define Symbols |
| Mem0 Memory | Settings → Memory → 配置 Endpoint + Key | 跨会话语义记忆 |
| LightRAG | Settings → Knowledge → 配置 Endpoint + Key | 项目知识库 RAG |
| Conversation Compression | Settings → Context → 启用 | 长对话自动压缩，节省 token |

## 使用示例

```
> 在场景中创建一个 Cube，添加 Rigidbody 组件

> 帮我写一个玩家移动脚本，支持 WASD 和跳跃

> 查找所有未使用的材质并列出来

> 把 Player prefab 的 Health 字段从 100 改为 150

> 当前场景有哪些编译错误？帮我修复

> 搜索项目中所有继承 MonoBehaviour 的类
```

## 注意事项

- 所有操作在 Editor 内执行，不影响运行时代码
- 修改脚本后会自动触发编译，Agent 会等待编译完成再继续
- 高风险操作（删除文件、执行代码）会弹出确认对话框
- API Key 存储在本机，不随项目同步
- 建议在项目根目录创建 `PROJECT.md` 描述项目约定，Agent 会自动读取

## 故障排查

| 问题 | 解决方案 |
|------|---------|
| Chat 窗口无响应 | 检查 API Endpoint 和 Key 是否正确配置 |
| 工具调用报错 | 查看 Unity Console 的错误日志 |
| 编译后会话丢失 | 正常情况下会自动恢复，如未恢复可尝试重新打开 Chat 窗口 |
| VCS/Indexing 工具不可见 | 确认已在 Project Settings 中启用对应组件并添加了 Scripting Define |
