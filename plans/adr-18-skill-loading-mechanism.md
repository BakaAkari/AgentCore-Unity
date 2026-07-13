# ADR-18: Skill 加载机制 —— 从"编译期全量注入"到"运行时按需检索"

> **状态**: Draft (2026-07-11)
> **决策人**: 项目 PO / 唯一用户
> **触发**: 用户希望 AgentCore 具备"翻手册再答题"能力（类似 Claude Code Skills），突破当前"Bootstrap 一次性全量装配 + 无法会话内更新"的架构限制
> **影响文档**: 扩展 [`adr-17-minimalism.md`](adr-17-minimalism.md) §2.2（新增 UI 字段）、[`SOUL.md`](../Editor/Bootstrap/Resources/SOUL.md) §1（新增 skill 触发行为约束）
> **前置阅读**: [`BootstrapContext.cs`](../Editor/Bootstrap/BootstrapContext.cs) / [`BootstrapLoader.cs`](../Editor/Bootstrap/BootstrapLoader.cs) / [`RequestToolsTool.cs`](../Editor/Tools/Native/Meta/RequestToolsTool.cs) / [`ToolScopeState.cs`](../Editor/Tools/ToolScopeState.cs)

---

## 1. 问题陈述

### 1.1 用户诉求

用户希望 AgentCore 拥有 Anthropic Claude Code 风格的 "Skills" 能力：

- **知识按需加载**：模型不知道某个领域细节时，主动调 `load_skill(name)` 拉取专门指南
- **运行时可扩展**：新增 skill 不需要重启会话或改插件代码，把 Markdown 丢进 skill 目录即可
- **AGENTS.md §7.2 的路由表**（`.agents/skills/unity-blueprints`、`unity-scene-contracts` 等 8 个 Skill）**要被 AgentCore 自己遵守**，而不只是给外部 AI（Cursor / Codex）用

### 1.2 当前架构的关键限制（[事实]）

基于代码调研得出的现状：

| 层面 | 现状 | 限制 |
|------|------|------|
| **知识装配时机** | Bootstrap 在 [`AgentLoop.Initialize():226-232`](../Editor/Core/AgentLoop.cs) 一次性执行 | 会话中期无法引入新知识 |
| **知识形态** | SOUL.md + SOUL.ext.md + TOOLS.md.template（core+deferred）+ PROJECT.md（auto+user）5 类静态文件 | 无"按需加载"通道 |
| **修改生效边界** | [`ManageWorkspaceConfigTool.cs:24`](../Editor/Tools/Native/Bootstrap/ManageWorkspaceConfigTool.cs) 注释："Changes take effect in the NEXT conversation" | 用户必须重开会话 |
| **AGENTS.md 路由** | `.agents/skills/*/SKILL.md` 只对外部 AI 生效 | AgentCore 完全不知道这些文件存在 |
| **Compression 边界** | [`ConversationCompressor.cs:180-191`](../Editor/Core/Compression/ConversationCompressor.cs) 只跳过 3 种 marker | 未挂 marker 的 skill 内容会被压缩吞掉 |

### 1.3 已具备的架构基础（重要——避免重复造轮子）

代码里已经有**几乎完整的"按需激活知识"基础设施**，只是形态是 tools 而不是 skills：

| 已存在的组件 | 复用点 |
|--------------|--------|
| [`RequestToolsTool.cs`](../Editor/Tools/Native/Meta/RequestToolsTool.cs) — 元工具模式 | 直接抄骨架做 `LoadSkillTool` |
| [`ToolScopeState.cs`](../Editor/Tools/ToolScopeState.cs) — 会话级激活状态 | 复制成 `SkillScopeState` |
| [`ToolScopeResolver.cs`](../Editor/Tools/ToolScopeResolver.cs) — 分类查询 | 参考写 `SkillRegistry` |
| Deferred context 插入模式 [`AgentLoop.cs:440`](../Editor/Core/AgentLoop.cs) | 用同样的 `_messages.Insert()` 位置注入 skill |
| Bootstrap 的 `LoadUserFile` 目录搜索机制 | 复用磁盘查找路径规则 |
| `ToolCapability.ModifyAgentConfig` 能力位 | Skill 编辑操作直接归此类 |
| DomainReload 序列化基础设施 | 挂 skill 状态持久化 |

**关键洞察**：这不是"从零做一个 skill 系统"，是"把已有的 tool activation 语义扩展到 knowledge activation"。工程量比想象中小。

---

## 2. 设计约束（Non-Negotiable）

### 2.1 [ADR-17] 极简哲学（硬约束）

按 [`adr-17-minimalism.md`](adr-17-minimalism.md) §1 的 5 条规则：

1. **默认最优不问用户**：Skill 目录路径写死，不做 UI 让用户选路径
2. **一件事一个开关**：只暴露 1 个 `Enable Skill System` toggle，不做"多路径、多来源、多加载策略"
3. **术语必须白话**：UI 用"Skills"，不用"KnowledgeBase / Retrieval / RAG"
4. **有 Advanced foldout**：调试参数（token budget / skill lifetime）折叠
5. **可选服务用 ServiceCard**：Skill 系统属于 core feature，不需要 ServiceCard；但**如果**引入语义检索（本 ADR 明确拒绝，见 §4）就得走 ServiceCard

### 2.2 不破坏现有契约

- **不改** [`BootstrapContext.CompileSystemPrompt()`](../Editor/Bootstrap/BootstrapContext.cs) 签名
- **不改** [`AgentLoop.Initialize()`](../Editor/Core/AgentLoop.cs) 初始化顺序
- **不改** Compression 主流程；只是在其 skip-list 里增加一个 `SkillContentBuilder.Marker`
- **不改** 现有 tool schema；skill 是**新的元操作**，不是新的工具类别
- **不动** `.agents/skills/` 目录结构（AGENTS.md §7.2 已定义），AgentCore 主动 opt-in 读取

### 2.3 生命周期约束

- Skill 是**会话级**激活（跟 `ToolScopeState` 一致），跨会话不保留（新会话开始时若 LLM 认为需要则重新 load）
- **可选**：Domain Reload 期间应保持（否则脚本重编译会导致 skill 丢失，需要 LLM 再次 load，浪费 token）—— 这是**关键决策点 §5.2**
- **强制**：skill 内容必须挂 marker，让 Compression 跳过；但 skill 若长时间未使用也应可被"卸载"释放上下文（[推断] token 压力大时）

---

## 3. 竞品对标（[事实]）

| 系统 | Skill 加载模型 | 触发机制 | 存储位置 | 生命周期 |
|------|---------------|---------|---------|---------|
| **Anthropic Claude Code (Skills)** | Agent 主动调 `Skill()` 元操作，模型自己决定何时读手册 | 模型判断 | `.claude/skills/*.md` | 会话内单次 |
| **Cursor Rules** | 全量塞 system prompt（`.cursorrules`） | 编译期 | 项目根 | 永驻 |
| **Windsurf Rules** | 同 Cursor | 编译期 | `.windsurfrules` | 永驻 |
| **Cline `.clinerules`** | 全量塞 system prompt | 编译期 | 多路径 | 永驻 |
| **RooCode Custom Modes** | 模式切换选择性激活 | 用户手动切换模式 | Global / Workspace | 模式切换 |
| **OpenAI GPTs Actions** | 每个 GPT 的 knowledge files 全量塞 | 编译期 | GPT 配置内 | 永驻 |
| **本 ADR 提案** | Agent 主动调 `load_skill()` 元操作 | 模型判断（可加规则辅助） | `.agents/skills/*/SKILL.md` + 用户扩展路径 | 会话内 |

**推断**：Claude Code 的模型是**最贴合本 ADR 目标**的。原因：
- Cursor / Windsurf / Cline 的"全量注入"模式跟 AgentCore 现状是一样的，加了也没意义
- RooCode 的"模式切换"需要用户参与，破坏 ADR-17 §1 规则 1（默认最优不问用户）
- Claude Code 的"agent-driven"模式让模型自主决定何时"翻手册"，符合本 ADR "让 AgentCore 主动读 SKILL.md" 的目标

---

## 4. 候选方案

以下 5 个方案按**复杂度递增**排列。每个方案我都会给出：架构描述、优点、缺点、适用条件。

### 方案 A：极简 —— 只做元工具 `load_skill`，Skill 内容临时注入

**架构**：

```
新增 1 个元工具 LoadSkillTool [AlwaysVisible]:
  action: "list" / "load" / "list_loaded" / "unload"

数据流:
  LLM 判断需要 X 领域知识
    ↓
  调用 load_skill(action="list")
    ↓ 返回可用 skill 列表（从 .agents/skills/ 扫描 SKILL.md）
  LLM 挑选 skill → 调用 load_skill(action="load", name="unity-runtime-dev")
    ↓ SkillLoader 读磁盘 → 记录到 SkillScopeState → 返回内容摘要 + 全文
  LLM 收到 skill 全文（作为 tool_result 一次性返回给模型）

存储:
  SkillScopeState（会话级）: 记录已加载 skill 名称，不持久化到磁盘
  Skill 内容: 不额外持久化，作为 tool_result 存在于 conversation history 里

Compression:
  tool_result 是普通消息，走原有压缩路径
  长时间未引用的 skill 内容会被 ToolResultCompressor 压缩掉（保留摘要）
```

**优点**：
- **最小改动**：只加 1 个工具类 + 1 个 state 类 + 1 个 registry，不动 AgentLoop / Bootstrap / Compression 主流程
- 完全对齐 Claude Code 的 Skills 语义
- Token 成本可控（只有主动 load 才占上下文）
- 符合 ADR-17 极简：1 个开关 + 1 个工具，没有多余配置

**缺点**：
- Skill 内容作为 tool_result 消息，**在长会话中会被 ToolResultCompressor 压掉**（[事实]：见 [`ToolResultCompressor`](../Editor/Core/Compression/) 会压缩超过阈值的 tool_result）→ 模型可能"忘记" skill 内容需要重新 load
- 无法保证 skill 内容在会话中"永驻"直到显式 unload
- Domain Reload 后 SkillScopeState 丢失

**适用**：MVP、只想快速验证"这种交互模式能不能用"

**工程量估算**：**2-3 天**（1-2 天代码 + 半天 SOUL.md 指令补充 + 半天测试）

---

### 方案 B：Skill 挂 marker，与 Deferred Context 同级别注入

**架构**（在方案 A 基础上）：

```
新增:
  SkillContentBuilder.Marker = "# [SKILL] " (类似 WorkspaceSnapshotBuilder.SnapshotMarker)
  ConversationCompressor 的 skip-list 增加此 marker

数据流:
  load_skill 执行后:
    1. 生成 Skill 内容字符串: "# [SKILL] unity-runtime-dev\n<content>..."
    2. AgentLoop 收到激活事件 → _messages.Insert(在 user message 前) 一条 system message
    3. 返回 tool_result 简要摘要 "Loaded skill 'unity-runtime-dev' (~2300 tokens). Guidance applied."

Compression:
  ConversationCompressor 遇到 [SKILL] 前缀的 system 消息 → skip
  Skill 内容持久保留直到 unload 或会话结束

unload_skill:
  移除对应的 [SKILL] system message
  从 SkillScopeState 中移除
```

**优点**：
- Skill 内容不会被压缩吞掉，行为可预期
- 与现有 Deferred Context 机制完全对齐（同类第 4 种 skip marker）
- LLM 每轮都能看到已加载的 skill，无需依赖 conversation history 缓存

**缺点**：
- 加载 3+ 个 skill 时上下文压力显著（每个 skill 平均 2K-5K token）→ 需要 skill token budget
- 需要在 AgentLoop 里增加 skill message 管理（插入位置 / 去重 / 顺序）—— 逻辑集中在一个文件，风险可控
- 引入新的"永驻消息"类别，Compression 逻辑要扩展

**适用**：**推荐**。生产可用的正确解，是 §5.1 推荐方案的基础

**工程量估算**：**4-5 天**（3 天代码 + 1 天 Compression 边界测试 + 1 天集成测试）

---

### 方案 C：Skill 元工具 + 语义检索（RAG-style）

**架构**（在方案 B 基础上）：

```
新增语义检索能力:
  SkillIndex: 每个 SKILL.md 提取 frontmatter (name / description / triggers / when-to-use)
  用户消息进来时，语义匹配 → 建议 top-3 相关 skill
  首轮或每 N 轮注入到 Deferred Context: "Available skills based on your query: A, B, C"
  LLM 仍需显式调 load_skill 才真正加载

或者更激进:
  基于 embedding 的相似度检索（需要 embedding LLM）
  自动预加载相似度 > threshold 的 skill
```

**优点**：
- 降低 LLM "不知道有哪些 skill 可用" 的问题
- 减少 LLM "看 skill 列表 → 判断 → load" 的探索轮次

**缺点**：
- **违反 ADR-17 极简哲学**：需要 embedding 服务（新可选服务），需要 index 存储，需要 rebuild 触发
- 复杂度爆炸：SkillIndex → SkillMatcher → SkillPrefetcher → 至少 3-4 个新组件
- Embedding 需要外部 API 或本地模型（LightRAG 已经在但也是可选服务）—— 用户体验断裂
- **模型自己已经能读 SKILL 描述**：只需要 `load_skill(action="list")` 返回带 description 的列表，模型自己选。RAG 层是过度工程

**适用**：拒绝。理由——LLM 自己就有语义理解能力，让它自己在 list 结果里挑，比造 RAG 层简洁得多

---

### 方案 D：Skill 作为 Bootstrap "第三层"，运行时热重载

**架构**：

```
BootstrapContext 增加 Skills 字段:
  Skills: Dict<string, string>  // skill_name → content
  CompileSystemPrompt() 保持不变（不动 core）
  CompileDeferredContext() 保持不变
  NEW: CompileSkillsContext() → 返回当前活跃 skill 拼接内容

会话中变更:
  LLM 调 load_skill → SkillRegistry.Activate → _skillContext 更新
  下轮 LLM 调用时，_skillContext 作为独立 system message 插入
  相当于"运行时的 Deferred Context"，可增可减

Reset:
  ResetConversation() 只清 _skillContext, 不清 skill 文件
```

**优点**：
- 架构对称：SOUL / TOOLS / PROJECT / SKILLS 平级，认知负担低
- Skill 内容作为独立 message，可精确控制注入位置

**缺点**：
- **和方案 B 本质上是同一件事**——都是"在 user message 前插入 skill system message + 压缩跳过"。方案 D 只是在 BootstrapContext 里多加个字段做形式对称。**没有额外价值**
- 反而**增加认知负担**：让读者以为 skill 跟 SOUL/TOOLS/PROJECT 是同级 Bootstrap 概念，实际上 skill 的生命周期完全不同（那三个是会话开始注入且不变，skill 是会话中随时增删）
- 违反 ADR-17 极简：把"运行时动态"绑到"编译期静态"的抽象上

**适用**：拒绝。表面对称，实质不对称

---

### 方案 E：Auto-Load Skill Router（基于关键词 / 正则）

**架构**：

```
每个 SKILL.md 声明 auto-load triggers（正则表达式或关键词列表）
SkillRouter 在每轮用户消息进来时:
  1. 匹配用户消息 / conversation 最近 5 轮
  2. 命中 triggers 的 skill 自动预加载
  3. 通知 LLM "已根据你的问题预加载 X 技能"

配置:
  .agents/skills/unity-blueprints/SKILL.md
    ---
    auto_load_triggers:
      - "架构|architecture|design"
      - "prototyping|原型"
    ---
```

**优点**：
- LLM 一进来就能看到相关 skill
- 无需 LLM 主动 load，减少一轮 tool_call

**缺点**：
- **模型能力不足才需要 auto-load**：现代 LLM（Claude Opus / GPT-o / DeepSeek-R）完全能自主判断"我需要哪个 skill"—— 参照本项目 [`ModelCapabilityDetector.cs`](../Editor/Core/ModelCapabilityDetector.cs) 已经识别 native reasoning 模型
- 正则 / 关键词 trigger 是**很脆弱的 heuristic**：命中率取决于 trigger 写得好不好，容易漏、容易误判
- 违反 ADR-17 §1 规则 1（默认最优不问用户）：需要 skill 作者精心写 triggers
- 违反 first principle 精神：让规则替 LLM 做决定，反而降低模型主动性

**适用**：**部分采纳**——不做为默认，作为 Advanced foldout 里的可选辅助（对低能力 LLM 提供 auto-load 兜底）

---

## 5. 推荐方案 & 决策点

### 5.1 推荐 —— 方案 B（Skill 挂 marker，永驻直到 unload）+ 方案 A 的元工具入口

**理由（First Principles）**：

1. **用户想要 Claude Code Skills 语义** → agent-driven 模型，方案 A/B 都符合
2. **Skill 内容需要在会话中稳定可用** → 必须防止被 Compression 吞掉 → 方案 A 不够，需要方案 B 的 marker 机制
3. **不做过度工程** → 拒绝方案 C 的 RAG 层、方案 D 的伪对称抽象、方案 E 的规则驱动 auto-load
4. **利用现有基础设施** → `RequestToolsTool` / `ToolScopeState` / Deferred Context 全都可以镜像复用

### 5.2 关键决策点（需要你逐条决策）

以下 6 个决策点直接影响实现方向。**我先给出我的推荐，你可以逐条 override**。

#### D1: Skill 目录结构

**选项**：
- **D1-a** 沿用 `.agents/skills/<name>/SKILL.md`（AGENTS.md §7.2 现有约定，AgentCore 主动 opt-in）
- D1-b 新建 `AgentCore/skills/<name>.md` 目录（跟 PROJECT.md 一样放 AgentCore/ 下）
- D1-c 二者都支持（先 AgentCore/，再 .agents/skills/，前者覆盖后者）

**我的推荐**：**D1-a**。理由 —— AGENTS.md §7.2 已经把 `.agents/skills/` 定为 Unity workspace 标准，AgentCore 主动遵守能立刻复用现有 8 个 skill 文件。D1-b 会造成"两套 skill 目录"混乱。D1-c 是折中但过度设计（YAGNI）。

#### D2: Skill 生命周期

**选项**：
- **D2-a** 会话级：新会话开始时清空，LLM 自己判断是否重新 load
- D2-b Domain Reload 持久化：Reload 后自动恢复上次的 skill 集合
- D2-c 用户可持久化：Settings 里配置"默认预加载的 skill"

**我的推荐**：**D2-a**。理由 —— 会话是"一次性任务"的自然边界。持久化 skill 会破坏"每次都是 fresh state"的确定性。Domain Reload 期间 LLM 会重新审视上下文，若之前 skill 相关的 tool_call 还在 message history 里，LLM 有能力判断是否需要重新 load。**D2-b 未来可以加**（作为 Advanced 选项）。

#### D3: Skill 冲突与去重

**选项**：
- **D3-a** 同名 skill 只能 load 一次，重复调用返回"already loaded"
- D3-b 允许 reload（用于 skill 文件更新后强制刷新）
- D3-c 支持版本化：`skill@v1.0`

**我的推荐**：**D3-a + reload 参数**。action="load" 默认拒绝重复；action="reload" 显式强制刷新。D3-c 是过度设计（skill 是内部文档，不是包依赖）。

#### D4: Skill 内容注入位置

**选项**：
- **D4-a** 每轮插入到 user message 前（同 Deferred Context 位置 [`AgentLoop.cs:440`](../Editor/Core/AgentLoop.cs)），永驻直到 unload
- D4-b 只在下一轮插入一次，之后模型靠 conversation history 记忆
- D4-c 作为 tool_result 返回，走普通消息路径

**我的推荐**：**D4-a**。理由 —— 只有永驻才能保证 skill 内容在长会话中稳定可用，避免 Compression 吞掉。D4-c 是方案 A 的做法，已经论证不够可靠。D4-b 是折中但会让 skill 不稳定。

#### D5: Skill Token Budget

**选项**：
- **D5-a** 硬限制：单个 skill 上限 8K token，同时加载最多 3 个，超过报错让 LLM unload 旧的
- D5-b 软限制：软阈值 15K token 总量，超过时 warning 但不阻塞
- D5-c 不限制：完全交给 LLM 自己管理

**我的推荐**：**D5-b**。理由 —— 硬限制在 skill 层面创造额外故障点（LLM 收到 error 后可能进入死循环 unload/load）；不限制风险太大（一次 load 10 个 skill 撑爆上下文）。软阈值 + `load_skill` action="list_loaded" 让 LLM 感知负担并自主决定。所有阈值走 ADR-17 §2 内部化不 UI 化。

#### D6: SOUL.md 是否加入 Skill 触发指令

**选项**：
- **D6-a** 在 [`SOUL.md`](../Editor/Bootstrap/Resources/SOUL.md) §1 Operating Contract 加一条："遇到 X 类任务（架构 / 性能分析 / 场景装配等），先 `load_skill(action='list')` 查阅可用手册"
- D6-b 不改 SOUL.md，只在 `load_skill` 工具描述里说明使用场景，让 LLM 自主发现
- D6-c 只在 PROJECT.md 里说明，让每个项目自己决定

**我的推荐**：**D6-b + 强 tool description**。理由 —— SOUL.md §1 已经很密，加更多指令会稀释核心规则。工具描述本身就是给 LLM 看的最佳"何时用此工具"信号（本项目 `manage_gameobject` 等工具的 `USE FOR / NOT for` 模式已证明这个模式有效）。**如果观察到 LLM 用得不好**，再加 SOUL.md 明示指令（成本很低的事后补救）。

---

## 6. 推荐方案的完整设计（假设所有 D 决策按推荐值）

### 6.1 组件清单

```
Packages/com.agentcore/Editor/Skills/                         [新目录]
├── SkillMetadata.cs                                          [新] - skill 元数据（name/description/path/tokens）
├── SkillRegistry.cs                                          [新] - 扫描 .agents/skills/ 目录，缓存 SKILL.md 列表
├── SkillScopeState.cs                                        [新] - 会话级激活状态（镜像 ToolScopeState 结构）
├── SkillContentBuilder.cs                                    [新] - 构建 system message 内容，定义 Marker 常量
└── SkillFrontmatterParser.cs                                 [新] - 解析 SKILL.md 顶部 YAML frontmatter (可选)

Packages/com.agentcore/Editor/Tools/Native/Meta/
└── LoadSkillTool.cs                                          [新] - 元工具，AlwaysVisible

Packages/com.agentcore/Editor/Core/
├── AgentLoop.cs                                              [改] - Initialize() 注入 SkillScopeState 引用
├── AgentLoop.SkillContext.cs                                 [新 partial] - 管理 skill message 生命周期，防重复插入
└── Compression/ConversationCompressor.cs                     [改] - skip-list 增加 SkillContentBuilder.Marker

Packages/com.agentcore/Editor/Config/
└── AgentCoreSettings.cs                                      [改] - 新增 skillsEnabled (default true, HideInInspector)

Packages/com.agentcore/Editor/Config/Settings/Pages/
└── ContextMemorySettingsPage.cs                              [改] - 增加 "Skills" 卡片（1 个 toggle）
```

**新增 5 个类 + 1 个工具 + 1 个 partial class；改 4 个现有文件**。工程量可控。

### 6.2 数据流

```
[会话开始]
  Initialize() → 创建 SkillScopeState → 通过 LoadSkillTool.SetScopeState() 注入
  SkillRegistry 首次扫描 .agents/skills/ → 构建缓存

[LLM 主动调用]
  Assistant: tool_call load_skill(action="list")
    → 返回 [{name: "unity-runtime-dev", description: "...", token_count: 2300}, ...]

  Assistant: tool_call load_skill(action="load", name="unity-runtime-dev")
    → SkillRegistry.GetContent("unity-runtime-dev") 读磁盘
    → SkillScopeState.Activate("unity-runtime-dev")
    → 发布事件通知 AgentLoop.SkillContext 更新 pending skills
    → 返回 tool_result: "Skill loaded (2300 tokens). Total loaded: 1 skill."

[下一轮 LLM 调用前]
  AgentLoop.SendMessageAsync 内部（在 user message 添加后、发送前）:
    → 遍历 SkillScopeState.LoadedSkills
    → 移除所有 _messages 里已存在的 SkillContentBuilder.Marker 前缀消息（防重复）
    → 为每个已激活 skill 插入一条 system message: "# [SKILL] <name>\n<content>"
    → 位置：紧跟 workspace snapshot / deferred context 之后, user message 之前

[Compression 触发]
  ConversationCompressor.FindCompressibleRange:
    → 现有 skip-list: SummaryMessageMarker / SnapshotMarker / "# Available Tools"
    → NEW: 增加 SkillContentBuilder.Marker ("# [SKILL] ")
  已激活 skill 的 system message 永远被跳过

[卸载]
  Assistant: tool_call load_skill(action="unload", name="unity-runtime-dev")
    → SkillScopeState.Deactivate
    → AgentLoop.SkillContext 从 _messages 中删除对应 marker 消息
    → 下一轮上下文不再包含

[会话结束/切换]
  ResetConversation() → SkillScopeState.Reset() → 所有 skill 消息随 _messages.Clear() 一并清空
```

### 6.3 Skill 文件格式

**兼容 AGENTS.md §7.2 已有的 8 个 skill 目录**，每个 skill 目录下的 SKILL.md：

```markdown
---
name: unity-runtime-dev
description: Runtime code development — script writing, bug fixes, code review
category: development
version: 1.0
---

# Unity Runtime Development Skill

<skill 主体内容>
```

**Frontmatter 可选**（不写时从目录名和第一个 `# 标题` 自动推断）。这样 AGENTS.md §7.2 现有的 8 个 skill 目录**零改动就可以被 AgentCore 识别**。

### 6.4 Settings UI（遵守 ADR-17）

```
Context & Memory 页面 增加卡片:
  ┌──────────────────────────────────────┐
  │ Skills                               │
  │ ┌──────────────────────────────────┐ │
  │ │ [x] Enable Skill System          │ │
  │ │                                  │ │
  │ │ Available skills: 8              │ │
  │ │ Path: .agents/skills/            │ │
  │ │ [Open Skill Folder] [Reload]     │ │
  │ └──────────────────────────────────┘ │
  └──────────────────────────────────────┘
```

**只有 1 个 toggle + 2 个 action 按钮**。所有阈值 / 路径 / lifetime 内部化。

---

## 7. 分阶段实施计划

### Phase 1（MVP）— **~3 天**
- [ ] `SkillMetadata` + `SkillRegistry`（扫描 + 缓存）
- [ ] `SkillScopeState`（复制 `ToolScopeState` 改改）
- [ ] `LoadSkillTool` + `list` / `load` / `list_loaded` / `unload` / `reload` 5 个 action
- [ ] `SkillContentBuilder` + Marker 常量
- [ ] `AgentLoop.SkillContext.cs` partial 挂载注入逻辑
- [ ] `ConversationCompressor` skip-list 补 marker
- [ ] `AgentCoreSettings.skillsEnabled` 字段（HideInInspector, default true）

**交付准则**：
- LLM 能调 `load_skill(action="list")` 拿到 AGENTS.md §7.2 定义的 8 个 skill 列表
- LLM 能 load 一个 skill，下一轮问相关问题时明显参考 skill 内容
- 长会话（20+ 轮）后 skill 内容仍存在（Compression 未吃掉）
- 手动 unload 后下一轮 skill 消失

### Phase 2（生产可用）— **~2 天**
- [ ] 软 token budget（15K 阈值 warning，Debug.Log 提示）
- [ ] `LoadSkillTool` list 返回加入 `already_loaded` 状态
- [ ] SkillRegistry 支持 file watcher（skill 文件改动后 reload 缓存）
- [ ] Settings UI: `ContextMemorySettingsPage` 加卡片

**交付准则**：
- 用户改 `.agents/skills/unity-runtime-dev/SKILL.md` 后，下次 `load_skill` 拿到新内容
- Settings 里能看到 skill 数量和路径

### Phase 3（Optional，观察后再决定）—
- [ ] Domain Reload 后 skill 状态恢复（如果观察到"reload 后 LLM 重复 load 浪费 token"）
- [ ] Advanced foldout: 手动指定 default preloaded skills
- [ ] `SOUL.md` §1 加 skill 触发指令（如果观察到 LLM 用不好）

---

## 8. Challenge / 反对意见（Self-Adversarial）

### 8.1 Skill 内容常驻会不会撑爆上下文？

**问题**：3 个 skill × 3000 token = 9K token 常驻。20 轮会话下 token 压力显著。

**回答**：
- 软阈值 15K 是**推荐**；LLM 收到 warning 会主动 unload 不常用的
- 现代 LLM context 128K-1M 起步，9K 是**可接受的**（[事实]：GLM-5.2/Claude Opus 3/GPT-5o 均 200K+）
- 若真出现问题，可加 LRU 自动 unload 策略作为 Phase 3

### 8.2 为什么不直接把 SKILL.md 全塞进 SOUL.ext.md？

**问题**：既然 SOUL.ext.md 已经支持"追加行为规则"，为什么不让用户把所有 skill 内容拼进去？

**回答**：
- **Token 成本**：8 个 skill × 3K = 24K token 常驻 system prompt，每次请求都发送 → 浪费
- **不精准**：LLM 每次都看到 8 个 skill 无关内容，注意力被稀释
- **不动态**：改 SKILL.md 需要重开会话（`ManageWorkspaceConfigTool` 的老问题）
- **本 ADR 的核心价值就是解决这个问题**

### 8.3 为什么不用 MCP 协议做 Skill？

**问题**：MCP 已经支持 tools / resources / prompts 三种概念，`prompts` 就是 skill 的原生形态。为什么不直接用 MCP？

**回答**：
- **[事实]** AgentCore 已经有 tool 系统但**没有集成 MCP client**。集成 MCP 是独立的 ADR，工程量远大于 skill loader
- MCP prompts 需要 MCP server 运行时提供 → 增加进程依赖 → 违反 ADR-17 极简
- Skill 是**纯 Markdown 文件**，无进程 / 无网络 / 无依赖 → 最简形态
- 未来 AgentCore 集成 MCP client 时，可以让 MCP prompts **作为额外的 skill 来源**（SkillRegistry 抽象允许多来源）

### 8.4 如果 skill 内容有冲突（比如两个 skill 都定义了"命名约定"）？

**问题**：LLM 同时 load 两个规则冲突的 skill，会不会精神分裂？

**回答**：
- 这是 skill 内容质量问题，不是加载机制问题
- Skill 作者需要遵守 "范围明确、职责单一" 原则（AGENTS.md §7.2 已经这样组织，覆盖不同工作场景）
- 若真发生冲突，LLM 有能力识别并向用户提问 —— 这是 SOUL.md §0 "Question the premise" 的自然应用

### 8.5 为什么不做 Skill 版本控制？

**问题**：Skill 内容会变，需不需要 `skill@v1.0` 之类的版本锁定？

**回答**：
- Skill 是**项目内部文档**，跟着 VCS 一起演进
- 版本化会引入"锁定 → 更新 → 冲突解决"的复杂度，违反 YAGNI
- 用户如果真需要跨项目共享 skill，可以走 Unity Package 分发，Package 版本号就是天然的 skill 版本

---

## 9. 未决问题（需你决策）

汇总所有需要你逐条决策的点：

- [ ] **D1** Skill 目录结构（推荐 D1-a：沿用 `.agents/skills/`）
- [ ] **D2** Skill 生命周期（推荐 D2-a：会话级）
- [ ] **D3** Skill 冲突与去重（推荐 D3-a + reload 参数）
- [ ] **D4** Skill 内容注入位置（推荐 D4-a：永驻 system message）
- [ ] **D5** Skill Token Budget（推荐 D5-b：软阈值 15K）
- [ ] **D6** SOUL.md 是否加触发指令（推荐 D6-b：不改 SOUL.md，只强化工具描述）
- [ ] **方案选择**（推荐方案 B + A 元工具入口）
- [ ] **是否立即进入实现阶段** vs 先讨论几轮决策点

---

## 10. 变更影响面

**必改**：
- [`ConversationCompressor.cs:180-191`](../Editor/Core/Compression/ConversationCompressor.cs) — skip-list 增加 marker
- [`AgentLoop.cs:203-254`](../Editor/Core/AgentLoop.cs) — Initialize() 里创建 SkillScopeState 并注入到 LoadSkillTool
- [`AgentCoreSettings.cs`](../Editor/Config/AgentCoreSettings.cs) — 新增 `skillsEnabled` 字段
- [`ContextMemorySettingsPage.cs`](../Editor/Config/Settings/Pages/ContextMemorySettingsPage.cs) — 新增 Skills 卡片

**新增**：
- `Editor/Skills/` 目录 5 个文件
- `Editor/Tools/Native/Meta/LoadSkillTool.cs`
- `Editor/Core/AgentLoop.SkillContext.cs` (partial)

**不动**（重要边界）：
- `BootstrapContext` / `BootstrapLoader` — Skill 不走 Bootstrap 装配，独立生命周期
- `ToolScopeState` / `RequestToolsTool` — Skill 系统平行于 Tool activation，互不干扰
- `Session` 序列化 — Skill 状态不持久化到 SessionData
- `.agents/skills/` 目录本身 — 只读，不由 AgentCore 生成或修改

---

## 11. 版本规划

- **1.5.8** ← 当前（VCS SceneView Banner 修复）
- **1.6.0** ← Skill Phase 1 MVP（feature bump，minor 版本）
- **1.6.x** ← Phase 2 生产化
- **1.7.0** ← Phase 3 高级特性（如需要）

---

> **下一步**：等待你对 §5.2 六个决策点 + §9 方案选择的确认，然后进入实现。任意决策 override 都会调整 §6.2 数据流和 §7 阶段计划。