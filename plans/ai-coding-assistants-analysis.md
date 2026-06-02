# AI 编码助手竞品分析：Cline、Roo Code、Cursor 与 AgentCore 对比

> **文档状态**: 调研分析 | **日期**: 2026-05-13
> **目标**: 分析主流 AI 编码助手的架构、能力和设计模式，为 AgentCore Phase 6 提供方向参考
>
> **企业级适配说明（2026-06-02）**: 本文档是竞品调研参考，不是当前代码索引实施方案。文中早期提到的 `Assets/` / `Assets/Scripts/` 扫描路径仅代表标准 Unity 项目示例；当前 AgentCore 企业基准已调整为 **SVN 工作副本根 = WorkspaceRoot**、**Unity 工程目录 = UnityRoot 子根**、地图/模式/工具/资源/插件目录 = WorkspaceRoot 下的 Scope Root。代码索引实施必须以 [`codebase-indexing-phase1-plan.md`](codebase-indexing-phase1-plan.md) 为准。

---

## 1. 竞品概览

### 1.1 产品定位对比

| 产品 | 定位 | 开源状态 | 主要平台 | 核心特点 |
|------|------|---------|---------|---------|
| **Cline** | 自主 AI 编码代理 | 开源 (Apache 2.0) | VS Code, JetBrains, CLI, Kanban | 多表面支持、MCP 集成、多代理团队 |
| **Roo Code** | AI 编码套件 | 开源 (Apache 2.0) | VS Code Extension + Cloud Agents | 模式系统、云端协作、已停止服务 (2026-05-15) |
| **Cursor** | AI 优先的代码编辑器 | 闭源商业 | 独立编辑器 + CLI + 集成 | 自主代理、并行执行、企业级 |
| **AgentCore** | Unity Editor 内嵌 AI Agent | 开源 (Apache 2.0) | Unity Editor (Editor-only) | Unity 专用、工具系统、Domain Reload 恢复 |

### 1.2 架构层次对比

```
Cline:          SDK → CLI / VS Code Extension / JetBrains / Kanban
Roo Code:       Extension (本地) + Cloud Agents (云端协作)
Cursor:         独立编辑器 + Agent 运行时 + 多模型路由
AgentCore:      Unity Editor 插件 + AgentLoop + 工具系统
```

---

## 2. 核心能力矩阵

### 2.1 功能对比表

| 能力维度 | Cline | Roo Code | Cursor | AgentCore | 优先级建议 |
|---------|-------|----------|--------|-----------|-----------|
| **代码库索引** |  (隐式) |  (明确提及) |  (核心能力) |  |  高 |
| **上下文压缩** |  |  |  |  |  高 |
| **多模型支持** |  (10+ 提供商) |  (数十个) |  (5+ 前沿模型) |  (OpenAI 兼容) |  已有 |
| **MCP 服务器** |  (原生支持) |  |  |  |  中 |
| **工具调用系统** |  (插件 + MCP) |  |  |  (原生工具 44 个) |  已有 |
| **模式系统** |  (Plan/Act) |  (Code/Architect/Ask/Debug/Custom) |  (自主度滑块) |  **已废弃** (ADR-5) | ~~~~ **不实现** |
| **多代理协作** |  (团队系统) |  (Orchestrator) |  (并行代理) |  |  低 |
| **代码审查** |  |  |  |  |  中 |
| **自动批准** |  |  |  |  |  中 |
| **规则系统** |  (.clinerules) |  |  |  |  高 |
| **会话管理** |  |  |  |  |  已有 |
| **记忆系统** |  |  |  |  (Mem0) |  已有 |
| **CI/CD 集成** |  (Headless CLI) |  (Cloud Agents) |  |  |  低 |
| **消息平台集成** |  (Slack/Telegram/Discord) |  |  |  |  低 |

---

## 3. 关键技术深度分析

### 3.1 代码库索引 (Codebase Indexing)

#### 3.1.1 Cursor 的实现模式（推测）

```
项目加载时:
  → 扫描文件树 (AST 解析)
  → 提取符号 (类、方法、变量)
  → 构建引用图 (调用关系、依赖关系)
  → 向量化嵌入 (语义搜索)
  → 存储到本地索引数据库

查询时:
  → 混合检索 (关键词 + 语义相似度)
  → 上下文排序 (相关性评分)
  → 动态注入到 LLM 上下文
```

**关键技术栈**:
- **AST 解析**: Tree-sitter (多语言支持)
- **向量数据库**: Chroma / FAISS / Qdrant
- **嵌入模型**: OpenAI `text-embedding-3-small` / Voyage AI
- **增量更新**: 文件监听 + 差异化重索引

#### 3.1.2 Roo Code 的索引策略

从文档描述推测：
- **项目结构理解**: 自动识别项目类型（React/Django/Unity 等）
- **符号级索引**: 函数签名、类定义、接口
- **语义搜索**: "找到所有处理用户认证的代码"
- **上下文感知**: 根据当前文件自动加载相关依赖

#### 3.1.3 对 AgentCore 的启示

**Unity 项目的特殊性**:
```csharp
// Unity 项目索引需要理解的特殊结构
- .asmdef 程序集边界
- ScriptableObject 资产引用
- Scene 文件中的 GameObject 引用
- Prefab 嵌套结构
- Shader/Material 依赖链
- Package 依赖 (UPM)
```

**推荐实现路径（历史建议，已被 v0.9.0 WorkspaceRoot 方案替代）**:
1. **Phase 1 — 文件级索引**（早期设想）
   - 早期只扫描 `Assets/` 和 `Packages/` 的建议已不再作为实施基准
   - 当前应扫描 SVN WorkspaceRoot 下已启用的 UnityRoot 与 Scope Root
   - 提取 C# 文件的类名、命名空间、方法签名
   - 使用 Roslyn 进行 AST 解析
   - 存储到 SQLite 或兼容本地索引后端

2. **Phase 2 — 语义搜索** (v0.5.1)
   - 集成 LightRAG 或独立向量数据库
   - 对代码片段进行嵌入
   - 支持自然语言查询："找到所有使用 Addressables 的脚本"

3. **Phase 3 — 引用图** (v0.5.2)
   - 构建类型依赖图
   - 追踪 MonoBehaviour 组件引用
   - 分析 Scene/Prefab 中的脚本挂载关系

---

### 3.2 上下文压缩 (Context Compression)

#### 3.2.1 Roo Code 的上下文管理策略

**问题**: LLM 上下文窗口有限（Claude Opus 200K tokens，但实际有效利用率低）

**Roo 的解决方案**（从文档推测）:
```
1. 智能文件选择
   - 只加载与当前任务相关的文件
   - 基于依赖图自动扩展相关文件
   - 排除测试文件、生成代码、node_modules

2. 内容摘要
   - 对大文件只保留签名和注释
   - 折叠不相关的函数体
   - 保留接口定义，省略实现细节

3. 分层上下文
   - Level 1: 当前文件完整内容
   - Level 2: 直接依赖的类型签名
   - Level 3: 项目结构概览
   - Level 4: 外部库文档摘要

4. 动态上下文窗口
   - 根据任务复杂度调整上下文大小
   - 简单任务（修改单个函数）→ 小上下文
   - 复杂任务（重构模块）→ 大上下文
```

#### 3.2.2 Cline 的 Plan/Act 模式

```
Plan Mode (探索阶段):
  - 允许读取大量文件
  - 构建任务执行计划
  - 不执行实际修改
  - 输出: 结构化的任务分解

Act Mode (执行阶段):
  - 聚焦到具体文件
  - 上下文窗口缩小到必要范围
  - 每个 action 独立审批
  - 输出: 具体的代码变更
```

**关键洞察**: **模式切换本质上是上下文管理策略的切换**

#### 3.2.3 对 AgentCore 的启示

**当前问题**:
- AgentCore 当前没有上下文压缩机制
- 每次对话都携带完整历史（受 `ContextWindowManager` 限制）
- 工具调用结果可能包含大量冗余信息

**推荐实现**:
```csharp
// 新增 ContextCompressionStrategy
public interface IContextCompressionStrategy
{
    List<Message> Compress(List<Message> history, int targetTokens);
}

// 实现 1: 滑动窗口 (当前已有)
public class SlidingWindowStrategy : IContextCompressionStrategy { }

// 实现 2: 摘要压缩
public class SummaryCompressionStrategy : IContextCompressionStrategy
{
    // 对旧消息调用 LLM 生成摘要
    // 保留最近 N 轮完整对话
}

// 实现 3: 语义聚类
public class SemanticClusteringStrategy : IContextCompressionStrategy
{
    // 将相似主题的消息聚合
    // 只保留每个主题的代表性消息
}
```

---

### 3.3 模式系统 (Mode System)

#### 3.3.1 Roo Code 的模式架构

| 模式 | System Prompt 特点 | 工具权限 | 典型用例 |
|------|-------------------|---------|---------|
| **Code Mode** | "你是一个实用的编码助手" | 文件读写、命令执行 | 日常开发、Bug 修复 |
| **Architect Mode** | "你是一个系统架构师" | 只读文件、创建设计文档 | 技术方案、重构规划 |
| **Ask Mode** | "你是一个技术顾问" | 只读文件、搜索 | 代码解释、技术问答 |
| **Debug Mode** | "你是一个调试专家" | 读写文件、运行测试、查看日志 | 问题诊断、根因分析 |
| **Custom Mode** | 用户自定义 | 用户配置 | 团队特定工作流 |

**核心设计原则**:
1. **角色专注**: 每个模式有明确的职责边界
2. **权限隔离**: 不同模式的工具访问权限不同
3. **提示词优化**: 针对任务类型定制 System Prompt
4. **用户可扩展**: 支持自定义模式

#### 3.3.2 Cline 的 Plan/Act 切换

```
用户请求: "重构用户认证模块"

Plan Mode:
  1. 读取 auth/ 目录下所有文件
  2. 分析当前架构
  3. 提出重构方案
  4. 列出需要修改的文件清单
  5. 询问用户确认

用户确认后 → 切换到 Act Mode

Act Mode:
  1. 逐文件执行修改
  2. 每次修改前展示 diff
  3. 等待用户批准
  4. 执行下一个文件
```

**关键洞察**: **Plan/Act 是一种"先思考后行动"的工作流模式**

#### 3.3.3 Cursor 的自主度滑块

```
自主度级别:
  Level 0: Tab 补全 (最低自主)
  Level 1: Cmd+K 定向编辑
  Level 2: 对话式协作
  Level 3: 全自主代理 (最高自主)
```

**设计哲学**: 用户根据任务复杂度和信任程度动态调整 AI 的自主权限

#### 3.3.4 对 AgentCore 的启示

**当前状态**:
- AgentCore 只有一个"对话模式"
- 所有工具调用都需要用户确认（类似 Cline 的 Act Mode）
- 没有"规划模式"或"只读模式"

**推荐设计**:

```csharp
// 新增模式系统
public enum AgentMode
{
    Chat,       // 当前默认模式：对话 + 工具调用
    Architect,  // 规划模式：只读 + 生成设计文档
    Debug,      // 调试模式：增强错误分析 + 日志查看
    Review,     // 审查模式：代码质量分析 + 建议
    Execute     // 执行模式：自动批准工具调用（高风险）
}

// 模式配置
public class ModeConfig
{
    public string SystemPromptOverride { get; set; }
    public List<string> AllowedTools { get; set; }  // 工具白名单
    public bool AutoApproveTools { get; set; }
    public int MaxToolRounds { get; set; }
}
```

**实现路径**:
1. **v0.5.0**: 新增 Architect Mode（只读 + 规划）
2. **v0.5.1**: 新增 Review Mode（代码审查）
3. **v0.5.2**: 新增 Debug Mode（调试增强）
4. **v0.5.3**: 支持用户自定义模式（Custom Mode）

---

### 3.4 规则系统 (Rules System)

#### 3.4.1 Cline 的 `.clinerules` 文件

```markdown
# .clinerules 示例

## Coding Standards
- Use TypeScript strict mode
- Prefer functional components over class components
- All async functions must have error handling

## Architecture
- Follow Clean Architecture principles
- Keep business logic separate from UI
- Use dependency injection for services

## Testing
- Write unit tests for all business logic
- Use Jest for testing
- Aim for 80% code coverage

## Deployment
- Run `npm run build` before committing
- Ensure all tests pass
- Update CHANGELOG.md
```

**关键特性**:
- **项目级配置**: 放在项目根目录，自动加载
- **Markdown 格式**: 易读易写
- **自动注入**: 规则自动添加到 System Prompt
- **跨工具兼容**: 支持导入 Cursor/Windsurf 格式

#### 3.4.2 对 AgentCore 的启示

**当前状态**:
- AgentCore 有 `SOUL.md`（全局角色定义）
- 有 `TOOLS.md.template`（工具使用指南）
- 没有项目级规则系统

**推荐设计**:

```markdown
# .agentcore/rules.md (项目级规则)

## Unity 项目约定
- 所有 MonoBehaviour 脚本必须放在 Assets/Scripts/ 下
- 使用 Addressables 管理资源引用
- 禁止在 Update() 中使用 GameObject.Find()

## 代码风格
- 使用 C# 9.0 特性
- 私有字段使用 _camelCase
- 公共属性使用 PascalCase

## 测试要求
- 所有公共 API 必须有单元测试
- 使用 Unity Test Framework
- 测试覆盖率目标 70%

## 工具使用偏好
- 优先使用 manage_scene 而非手动操作 Hierarchy
- 创建 Prefab 时自动添加到 Resources/ 或 Addressables
```

**实现路径**:
1. **v0.5.0**: 支持读取 `.agentcore/rules.md`
2. **v0.5.1**: 规则自动注入到 System Prompt
3. **v0.5.2**: 支持多文件规则（按模块拆分）
4. **v0.5.3**: 规则验证工具（检查代码是否符合规则）

---

### 3.5 MCP 服务器集成

#### 3.5.1 Cline 的 MCP 架构

```
Cline Agent
  ↓
MCP Client (内置)
  ↓
MCP Server 1: Filesystem
MCP Server 2: Database (PostgreSQL)
MCP Server 3: GitHub API
MCP Server 4: Slack
MCP Server 5: Custom Tools
```

**关键优势**:
- **标准化协议**: 所有外部工具通过 MCP 统一接入
- **社区生态**: 可复用社区已有的 MCP 服务器
- **动态加载**: 运行时添加/移除 MCP 服务器
- **权限隔离**: MCP 服务器独立进程，安全性更高

#### 3.5.2 对 AgentCore 的启示

**当前状态**:
- AgentCore 有原生工具系统（44 个工具，340+ actions）
- 有云端工具（LightRAG, Mem0）
- 没有 MCP 集成

**是否需要 MCP？**

**优势**:
- 可复用社区 MCP 服务器（数据库、API、云服务）
- 扩展性更强（用户可自行添加工具）
- 与 Cline/Roo 生态兼容

**劣势**:
- 增加架构复杂度
- Unity Editor 环境的进程管理限制
- 当前原生工具系统已经足够强大

**建议**: **Phase 7 (v0.6.x) 再考虑 MCP 集成，当前优先完善原生工具和代码库索引**

---

## 4. AgentCore 的差异化优势

### 4.1 当前独特优势

| 优势 | 说明 | 竞品对比 |
|------|------|---------|
| **Unity 深度集成** | 直接访问 Unity Editor API，无需外部工具 | Cline/Roo 需要通过命令行间接操作 |
| **Domain Reload 恢复** | 脚本编译后自动恢复对话状态 | 通用编辑器无此问题 |
| **工具自动发现** | 通过 `[AgentTool]` 特性自动注册工具 | Cline 需要手动注册 MCP 服务器 |
| **零外部依赖** | 除 Newtonsoft.Json 外无其他依赖 | Cline 依赖 Node.js 生态 |
| **Editor-only 隔离** | 不污染 Runtime 代码 | N/A |

### 4.2 当前劣势与改进方向

| 劣势 | 影响 | 改进方向 (Phase 6) |
|------|------|-------------------|
| **无代码库索引** | 无法回答"哪些脚本使用了 Addressables？" |  高优先级 |
| **无上下文压缩** | 长对话后性能下降 | ~~ 高优先级~~ **已完成 v0.5.0** |
| ~~**无模式系统**~~ | ~~所有任务使用相同的 System Prompt~~ | ~~ 高优先级~~ **已废弃 (ADR-5)** |
| **无规则系统** | 无法定制项目级编码规范 |  中优先级 |
| **无代码审查** | 无法主动分析代码质量 |  中优先级 |
| **无自动批准** | 所有工具调用都需要确认 |  低优先级（安全考虑）|

---

## 5. Phase 6 路线图建议（基于竞品分析）

### 5.1 重新规划的 Phase 6 优先级

```
Phase 6.0 — 代码库索引与理解 (v0.5.0)
  ├─ 6.0.1 文件级索引（Roslyn AST 解析）
  ├─ 6.0.2 符号检索（类、方法、字段）
  ├─ 6.0.3 依赖图构建（类型引用、程序集边界）
  └─ 6.0.4 自然语言查询接口

Phase 6.0 — 上下文压缩与管理 (v0.5.0 ~ v0.5.2) **已完成**
  ├─ 6.0.1 工具结果摘要 
  ├─ 6.0.2 对话历史压缩 
  ├─ 6.0.3 上下文预算管理 
  └─ 6.0.4 压缩策略可视化 

~~Phase 6.1 — 模式系统~~ **已废弃 (ADR-5)**
  ├─ ~~6.1.1 Architect Mode~~
  ├─ ~~6.1.2 Review Mode~~
  ├─ ~~6.1.3 模式切换 UI~~
  └─ ~~6.1.4 模式特定上下文策略~~

Phase 6.2 — 规则系统与智能推荐 (v0.5.2)
  ├─ 6.2.1 .agentcore/rules.md 支持
  ├─ 6.2.2 规则自动注入
  ├─ 6.2.3 SmartToolRecommender（基于上下文推荐工具）
  └─ 6.2.4 响应式建议（"下一步建议"）

Phase 6.3 — 代码审查与质量分析 (v0.5.3)
  ├─ 6.3.1 代码质量评分
  ├─ 6.3.2 性能问题检测
  ├─ 6.3.3 Unity 最佳实践检查
  └─ 6.3.4 自动修复建议
```

### 5.2 技术栈选型建议

| 模块 | 推荐技术 | 理由 |
|------|---------|------|
| **AST 解析** | Roslyn (Microsoft.CodeAnalysis) | C# 官方编译器 API，Unity 已内置 |
| **向量数据库** | LightRAG (已集成) | 复用现有基础设施 |
| **本地索引存储** | SQLite (System.Data.SQLite) | 轻量、无需外部服务 |
| **上下文压缩** | LLM 摘要 (Claude Haiku) | 成本低、速度快 |
| **规则解析** | Markdown Parser (Markdig) | 轻量、易扩展 |

---

## 6. 实施建议

### 6.1 短期行动（v0.5.0 - 1 个月）

1. **代码库索引 MVP**
   - 早期 `Assets/Scripts/` 扫描建议已被 SVN WorkspaceRoot + Scope Root 方案替代
   - 扫描 WorkspaceRoot 下已启用 Scope Root 中的 `.cs` 文件
   - 提取类名、命名空间、方法签名
   - 存储到 SQLite 数据库
   - 新增工具 `search_codebase` action

~~2. **Architect Mode 原型**~~ **已废弃 (ADR-5)**
   - ~~新增 `AgentMode.Architect` 枚举~~
   - 限制工具权限（只读文件、创建 Markdown）
   - 定制 System Prompt（强调规划和设计）

3. **规则系统基础**
   - 支持读取 `.agentcore/rules.md`
   - 规则内容注入到 System Prompt

### 6.2 中期目标（v0.5.1 - v0.5.2 - 2 个月）

1. **语义搜索**
   - 集成 LightRAG 进行代码嵌入
   - 支持自然语言查询："找到所有处理网络请求的脚本"

2. **上下文压缩**
   - 实现 `SummaryCompressionStrategy`
   - 对超过 10 轮的对话自动压缩

~~3. **Review Mode**~~ **已废弃 (ADR-5)**
   - ~~代码质量分析工具~~
   - Unity 最佳实践检查

### 6.3 长期愿景（v0.5.3+ - 3+ 个月）

1. **引用图可视化**
   - 在 UI 中展示类型依赖关系
   - 支持"查找所有引用"

2. **自动批准模式**
   - 用户可选择信任某些工具
   - 高风险操作仍需确认

3. **MCP 集成**（可选）
   - 支持加载社区 MCP 服务器
   - 扩展到 Unity 外部工具

---

## 7. 风险评估

| 风险 | 可能性 | 影响 | 缓解措施 |
|------|--------|------|---------|
| Roslyn 在 Unity 中的兼容性问题 | 中 | 高 | 先在测试项目中验证，使用 Unity 内置的 Roslyn 版本 |
| 代码库索引性能问题（大项目） | 高 | 中 | 增量索引 + 后台线程 + 进度提示 |
| 上下文压缩导致信息丢失 | 中 | 中 | 保留最近 N 轮完整对话，只压缩旧消息 |  已实现 |
| ~~模式系统增加用户学习成本~~ | ~~低~~ | ~~低~~ | ~~默认使用 Chat Mode，高级用户可选择其他模式~~ | **已废弃 (ADR-5)** |
| 规则系统被滥用（规则过于复杂） | 低 | 低 | 提供规则模板和最佳实践文档 | 待实现 |

---

## 8. 总结与建议

### 8.1 核心洞察

1. **代码库索引是基础能力**  
   Cursor、Roo、Cline 都将其作为核心功能，AgentCore 必须补齐。

~~2. **模式系统提升专业性**~~ **已废弃 (ADR-5)**
   ~~不同任务需要不同的 AI 行为模式，单一模式无法满足所有场景。~~
   
   **更新**: AgentCore 是自主智能体，应根据上下文自动适应，而非手动切换模式。

3. **上下文压缩是性能关键**  
   长对话后的性能下降是所有 AI 助手的共同问题，必须主动管理上下文。

4. **规则系统增强可控性**  
   项目级规则让 AI 更符合团队规范，减少返工。

### 8.2 AgentCore 的独特定位

**不要成为"Unity 版的 Cline"**，而是：

```
AgentCore = Unity 深度集成 + 代码库理解 + 工具生态 + 自主智能体
```

**差异化方向**:
- **Unity 专家**: 深度理解 Unity 项目结构（Scene/Prefab/Addressables）
- **工具优先**: 通过工具系统而非直接文件操作
- **Editor 原生**: 无需外部进程，完全集成在 Unity Editor 中

### 8.3 下一步行动

1. **立即开始**: 代码库索引 MVP（v0.5.3）
2. **并行设计**: ~~Architect Mode 和规则系统~~ 规则系统和工具推荐
3. **持续迭代**: 根据用户反馈调整优先级

**更新 (2026-05-18)**: 模式系统已废弃（ADR-5），聚焦于增强 Agent 的自主能力。

---

## 9. 参考资源

- [Cline GitHub](https://github.com/cline/cline)
- [Roo Code 文档](https://docs.roocode.com/)
- [Cursor 官网](https://www.cursor.com/)
- [MCP 协议](https://github.com/modelcontextprotocol)
- [Roslyn API 文档](https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/)

---

**文档维护**: 本文件应在 Phase 6 开发过程中持续更新，记录实际实施中的发现和调整。
