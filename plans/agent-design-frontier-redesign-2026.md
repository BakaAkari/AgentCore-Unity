# AgentCore Agent 设计前沿重构方案

> **基准时间**: 2026 年 6 月  
> **目标**: 将 AgentCore 的 Agent 架构从"2023 规则墙"模式升级为 2026 前沿水平  
> **原则**: Prompt 层优先，代码逻辑仅在 Prompt 无法解决时介入  
> **状态**: 方案阶段，待用户确认后执行

---

## 1. 参考基线

| 来源 | 核心贡献 |
|------|---------|
| **Dive into Claude Code** (arxiv 2604.14228, 2026.04) | 5 层压缩管线、7 模式权限系统、Subagent 委托、Append-only Session、13 条设计原则 |
| **Anthropic Context Engineering** (2025.09) | 最小高信号 token 集、Context Rot 概念、Just-in-time 检索、渐进式披露 |
| **Augment Code 11 Techniques** (2025.12) | 完整世界图景、组件一致性、用户视角对齐、工具结果自解释错误、Prompt Cache 意识 |

---

## 2. AgentCore vs Claude Code 2026 — 差距矩阵

| 维度 | Claude Code 2026 | AgentCore 现状 | 差距等级 |
|------|-----------------|---------------|---------|
| **System Prompt 结构** | ~1200 tokens，极精简，行为由工具定义驱动 | ~5000+ tokens，规则墙，§1-§10 大段文本 | **严重** |
| **上下文管理** | 5 层分级压缩（Auto-compact → Summarize → Ultra-compact → Manual → Reset） | 单层 LLM 摘要压缩 | **严重** |
| **权限系统** | 7 模式（Plan/Auto-edit/Full-auto 等）+ ML 分类器实时判定 | 简单 RiskLevel 枚举 + 单次确认弹窗 | **中等** |
| **Subagent 委托** | 独立上下文 subagent 处理文件搜索/复杂分析 | 无 subagent，所有工具平铺在单一循环 | **中等** |
| **工具描述质量** | 每个工具 description 包含完整使用场景、边界条件、返回格式 | 大部分工具 description 1-2 句话，信息密度低 | **严重** |
| **Session 存储** | Append-only，支持 fork/branch/resume | JSON 全量覆写 | **低** |
| **扩展机制** | MCP + Plugins + Skills + Hooks 四层 | ToolAutoDiscovery + Optional Components 两层 | **低**（已有规划） |
| **Memory 集成** | 上下文内 scratchpad + 外部 memory service | mem0 外部服务，搜索质量依赖用户消息原文 | **中等** |
| **Prompt Cache** | 严格保证 system prompt 前缀不变，动态内容放末尾 | 每次重新编译 system prompt，tools 列表动态变化 | **中等** |

---

## 3. 九项具体变更

### 3.1 [P0] SOUL.md 重写 — 从规则墙到行为锚点

**现状问题**:
- §1-§10 合计 ~3000 tokens 的规则文本
- 大量规则是"不要做 X"的否定句式 — LLM 对否定指令遵循率天然低于肯定指令
- 规则之间有冗余（§3 工具使用规则与 §7 代码操作规则有重叠）
- "Think-then-Act" 格式描述占用 ~300 tokens，但 reasoning API 已经原生支持

**目标**: ≤1200 tokens 的行为锚点文档

**重写原则**:

| 原则 | 说明 |
|------|------|
| 肯定句优先 | "执行 X" 而非 "不要做 Y" |
| 行为锚点而非规则枚举 | 描述"你是什么"而非"你不能做什么" |
| 工具即规则 | 权限、安全约束移入工具 schema 的 description 字段 |
| 零冗余 | 每条信息只出现一处 |

**重写后结构草案**:

```markdown
# Identity
你是 AgentCore — Unity Editor 内嵌的开发助手。你通过工具操作 Unity Editor，通过对话辅助开发者。

# Operating Contract
1. 每次行动前明确目标和预期影响
2. 修改文件前读取当前状态
3. 不确定时使用工具验证，而非猜测
4. 工具执行失败时报告原因并提供替代方案
5. 用户未要求时不主动修改代码

# Communication
- 中文为主，代码/API 名称保持英文
- 结论先行，细节按需展开
- 代码块标注语言和文件路径

# Context Awareness
- 你的工具列表定义了你的能力边界
- PROJECT.md 描述当前项目约定
- 会话历史中的 [MEMORY] 标记包含跨会话记忆
```

**Token 预算**: ~400 tokens（对比当前 ~3000 tokens，压缩 87%）

---

### 3.2 [P0] TOOLS.md.template 重写 — 从使用手册到行为触发器

**现状问题**:
- 当前 TOOLS.md.template 是面向人类的操作手册格式
- 包含大量 "你可以使用..." "示例..." 等冗余引导
- 工具使用规则已经内嵌在各工具的 JSON Schema description 中，重复声明

**目标**: 仅保留工具无法自描述的跨工具协作模式

**重写后结构**:

```markdown
# Tool Coordination Patterns

## File Modification Protocol
read_file → 确认当前内容 → write_file/apply_diff → 验证编译

## Script Modification (triggers Domain Reload)
标记 MayModifyScripts 的工具执行后，等待编译完成再继续

## Search Strategy
search_code (索引) → read_file (精确) → 逐步缩小范围

## Batch Operations
单次消息中相关操作合并执行，减少往返轮次
```

**Token 预算**: ~200 tokens（对比当前 ~800 tokens，压缩 75%）

---

### 3.3 [P0] 条件化 Section 注入 — Just-in-time Context

**现状问题**:
- 所有 context 在首条消息就全量注入 system prompt
- 用户问"帮我改个 UI"时，VCS 规则、Indexing 规则、Cloud 工具说明全部浪费 token

**方案**: `BootstrapLoader` 改为分层注入

```
Layer 0 (Always): SOUL.md (~400 tokens) — 永远注入
Layer 1 (Always): Active Tools List (~200 tokens) — 当前可用工具名+单行描述
Layer 2 (On-demand): Tool Coordination Patterns — 仅当工具调用进入第 2 轮时注入
Layer 3 (On-demand): PROJECT context — 仅当涉及项目级操作时注入
```

**代码变更范围**:
- `BootstrapContext.cs` — 支持 section 延迟注入
- `AgentLoop.Runner.cs` — 在 `RunToolCallLoopAsync` 第 2 轮前检查是否需要注入 Layer 2
- `BootstrapLoader.cs` — 拆分加载逻辑为 `LoadCore()` + `LoadOnDemand(trigger)`

**预期收益**: 首条消息 system prompt 从 ~5000 tokens 降至 ~800 tokens，后续按需增长

---

### 3.4 [P0] 工具 Description 质量提升

**现状问题**:
- 多数工具 description 仅 1-2 句话
- 缺少返回格式说明、边界条件、典型错误
- LLM 因信息不足频繁误用工具或传错参数

**标准格式**（参考 Claude Code 2026）:

```
[一句话功能] + [适用场景] + [不适用场景] + [返回格式概要] + [常见陷阱]
```

**示例** — `manage_gameobject` 工具:

```
管理场景中的 GameObject：创建、删除、查找、修改层级关系。
适用：需要操作当前打开场景中的对象时。
不适用：操作 Prefab Asset（使用 manage_prefab）、操作未打开的场景。
返回：JSON {success, data: {name, path, components[]}}。
注意：删除操作不可撤销，层级路径使用 "/" 分隔。
```

**执行方式**: 逐个审查所有工具的 `[AgentTool]` Description 字段，按标准格式补全

---

### 3.5 [P1] 5 层压缩管线替换单层压缩

**现状问题**:
- `ConversationCompressor` 仅一种策略：选取中间段 → LLM 摘要 → 替换
- 压缩后上下文质量急剧下降（摘要丢失工具调用细节）
- 无法区分"可丢弃的探索性对话"和"必须保留的决策点"

**目标架构**（对齐 Claude Code 5 层模型）:

```
Level 1: Auto-compact
  触发: token 使用达 70%
  策略: 移除工具调用的原始输出，仅保留 [Tool: name → result_summary]

Level 2: Summarize  
  触发: token 使用达 85%
  策略: 将连续的 user-assistant 对话段落压缩为摘要段

Level 3: Ultra-compact
  触发: token 使用达 92%
  策略: 仅保留 system prompt + 最近 3 轮 + 所有 Level 1/2 摘要的再摘要

Level 4: Context Reset + Memory Persist
  触发: token 使用达 97%
  策略: 清空历史，将关键决策写入 mem0，新对话从 memory recall 开始

Level 5: Hard Truncate (现有逻辑)
  触发: 超过模型物理限制
  策略: 保留 system + 最近 N 条
```

**代码变更范围**:
- `ConversationCompressor.cs` — 重构为多级策略
- `ContextWindowManager.cs` — 新增 token 使用率阈值判断
- `AgentLoop.Runner.cs` — 每轮循环前调用压缩检查

---

### 3.6 [P1] 首条消息自动注入 — 消除"冷启动"

**现状问题**:
- 用户第一条消息发出时，Agent 对项目一无所知
- `ProjectContextCollector` 的信息在 system prompt 中但信息密度低
- 首轮工具调用经常是 Agent 在"摸索环境"而非执行用户意图

**方案**: 在用户首条消息后、LLM 调用前，自动注入一条隐藏的 assistant 消息

```json
{
  "role": "assistant",
  "content": "[WORKSPACE_SNAPSHOT]\n项目: {name} | Unity {version} | 场景: {active_scene}\n最近修改: {recent_files}\n当前编译状态: {clean/errors}\n可用工具: {tool_count} 个\n[/WORKSPACE_SNAPSHOT]"
}
```

**实现位置**: `AgentLoop.SendMessageAsync()` 在 memory recall 之后、`RunToolCallLoopAsync` 之前插入

**预期收益**: 减少首轮"盲目探索"工具调用，Agent 直接进入任务执行

---

### 3.7 [P1] Think-then-Act 与 Reasoning API 去冲突

**现状问题**:
- SOUL.md 中有 `Think-then-Act` 格式要求（Agent 先输出思考再行动）
- `RequestEnrichment.cs` 的 `InjectReasoning()` 同时启用了 API 级 reasoning
- 两者同时存在导致 Agent 双重思考：API reasoning tokens + 输出中的 `<think>` 块
- 浪费 token 且思考过程分裂在两处，不利于调试

**方案**:

| 场景 | 行为 |
|------|------|
| 模型支持 reasoning API（如 Claude, o-series） | 移除 SOUL.md 中的 Think-then-Act 要求，完全依赖 API reasoning |
| 模型不支持 reasoning API（如 GPT-4o） | 保留 SOUL.md 中的思考格式要求，不注入 reasoning 参数 |

**代码变更**: 
- `RequestEnrichment.cs` — 根据模型能力决定是否注入
- `BootstrapLoader.cs` — 根据模型能力决定是否包含 Think-then-Act section
- 新增 `ModelCapabilities` 枚举/接口（或扩展现有 `ILLMClient`）

---

### 3.8 [P2] 信任升级机制 — Progressive Trust

**现状问题**:
- 每次高风险操作都弹确认窗口，用户疲劳后盲目点确认
- 无"本会话内已信任此工具"的升级路径
- 安全性和效率的平衡点固定，无法适应不同用户

**方案**（对齐 Claude Code 7 模式简化版）:

```
Mode 1: Supervised (默认)
  - 所有 write/delete 操作需确认
  - 确认后记录 (tool, action) 对

Mode 2: Trusted (会话内升级)
  - 同一 (tool, action) 被确认 3 次后自动信任
  - 信任范围: 仅当前会话
  - 用户可随时通过命令降级

Mode 3: Auto (用户显式开启)
  - 所有操作自动执行
  - 仅记录操作日志供事后审查
```

**代码变更范围**:
- `ToolRiskPolicy.cs` — 新增信任状态查询
- 新增 `SessionTrustState.cs` — 会话级信任记录
- `ChatWindow.Confirmation.cs` — 确认弹窗增加"本会话信任此操作"选项

---

### 3.9 [P2] Prompt Cache 稳定性保证

**现状问题**:
- 每次 `BootstrapContext.CompileSystemPrompt()` 的输出不保证前缀稳定
- 工具列表顺序可能因 `ToolAutoDiscovery` 扫描顺序变化
- 导致 API 端 prompt cache 命中率低，增加延迟和成本

**方案**:
1. 工具列表按 `(Category, Name)` 确定性排序（已部分实现，需验证）
2. System prompt 编译结果缓存，仅在工具集变化时重新生成
3. 动态内容（PROJECT context、Memory）放入 user message 而非 system prompt

**代码变更**:
- `BootstrapContext.cs` — 增加编译结果缓存 + 变更检测
- `BootstrapLoader.cs` — PROJECT/Memory 内容改为返回独立字符串，由 `AgentLoop` 注入 user message

---

## 4. 执行优先级与依赖关系

```
P0 (立即执行，仅改 Prompt 层):
  3.1 SOUL.md 重写
  3.2 TOOLS.md.template 重写  
  3.4 工具 Description 提升
  ──────────────────────────
  以上三项互不依赖，可并行

P1 (需要代码变更):
  3.3 条件化注入 ← 依赖 3.1/3.2 完成（需知最终 token 预算）
  3.6 首条消息注入 ← 独立
  3.7 Reasoning 去冲突 ← 依赖 3.1 完成（移除 Think-then-Act）
  3.5 5 层压缩 ← 独立，工作量最大

P2 (体验优化):
  3.8 信任升级 ← 独立
  3.9 Prompt Cache ← 依赖 3.3 完成
```

---

## 5. 验证方法

| 变更项 | 验证方式 |
|--------|---------|
| 3.1 SOUL.md 重写 | A/B 对比：相同 10 条指令，对比新旧 SOUL 下的工具调用准确率和回复质量 |
| 3.2 TOOLS.md 重写 | 统计工具误用率（错误参数/错误工具选择）的前后变化 |
| 3.3 条件化注入 | 测量首条消息响应延迟（token 减少 → 延迟降低） |
| 3.4 Description 提升 | 统计"Unknown action"和参数解析错误的出现频率 |
| 3.5 5 层压缩 | 长对话（>50 轮）后的信息召回准确率 |
| 3.6 首条消息注入 | 统计首轮工具调用中"探索性调用"vs"执行性调用"的比例 |
| 3.7 Reasoning 去冲突 | Token 消耗量对比（应减少 15-30%） |
| 3.8 信任升级 | 用户确认弹窗触发次数/会话 |
| 3.9 Prompt Cache | API 端 cache hit ratio（需 provider 支持） |

---

## 6. 核心设计原则总结

从 Claude Code 2026 论文中提炼的 13 条设计原则，与 AgentCore 的对应关系：

| # | 原则 | AgentCore 现状 | 行动 |
|---|------|---------------|------|
| 1 | **最小 System Prompt** — 只放不变的身份锚点 | 违反（规则墙） | 3.1 修复 |
| 2 | **工具即能力边界** — 工具 schema 定义 Agent 能做什么 | 部分遵守 | 3.4 强化 |
| 3 | **Context 是稀缺资源** — 每个 token 都有成本 | 违反（全量注入） | 3.3 修复 |
| 4 | **渐进式信任** — 从保守开始，按行为升级 | 缺失 | 3.8 新增 |
| 5 | **压缩不丢决策** — 多级压缩保留关键节点 | 违反（单层摘要） | 3.5 修复 |
| 6 | **冷启动消除** — Agent 首次响应就有环境感知 | 部分（PROJECT 注入） | 3.6 强化 |
| 7 | **单一思考路径** — 避免双重 reasoning 消耗 | 违反 | 3.7 修复 |
| 8 | **Prompt Cache 友好** — 稳定前缀最大化缓存 | 缺失 | 3.9 新增 |
| 9 | **工具结果自解释** — 错误信息包含修复建议 | 部分遵守 | 3.4 覆盖 |
| 10 | **Append-only 思维** — 会话数据不可变追加 | 部分（JSON 覆写） | 暂不动 |
| 11 | **Subagent 委托** — 复杂子任务独立上下文 | 缺失 | 暂不动（Phase 8） |
| 12 | **用户视角对齐** — 输出格式匹配用户预期 | 依赖 SOUL.md | 3.1 覆盖 |
| 13 | **可观测性** — 思考过程对用户透明 | 已有 ThinkingDrawer | 已满足 |

---

## 7. 风险与权衡

| 风险 | 影响 | 缓解措施 |
|------|------|---------|
| SOUL.md 大幅缩减后 Agent 行为漂移 | 回复质量下降、工具使用不当 | 渐进式缩减：先移除冗余 → 观测 → 再压缩 |
| 条件化注入时机判断不准 | 缺少必要 context 导致错误决策 | 保守策略：宁可多注入，不遗漏关键信息 |
| 5 层压缩实现复杂度高 | 开发周期长、引入新 bug | 先实现 Level 1（工具输出压缩），再逐步添加 |
| 工具 Description 过长 | 增加 tool definitions token 占用 | 设定单工具 description ≤200 tokens 硬限制 |
| Prompt Cache 依赖 API provider | 非所有 provider 支持 cache | 作为优化项，不作为功能依赖 |

---

## 8. 不在本方案范围内

以下能力已在 ROADMAP Phase 7/8 规划中，本方案不重复规划：

- MCP Server 对外互操作
- Plugin 系统
- Subagent 委托架构
- 多模型并行/路由
- 自主 Agent Loop（无人值守长时间运行）

---

## 附录 A: 参考论文摘要

### Dive into Claude Code (arxiv 2604.14228v1, 2026-04)

对 Claude Code v2.1.88 的逆向工程分析，揭示了以下架构细节：

**5 层上下文压缩管线**:
1. Auto-compact: 自动在 token 接近上限时触发
2. Summarize: LLM 生成对话摘要替换原文
3. Ultra-compact: 极致压缩，仅保留决策节点
4. Manual compact: 用户触发的手动压缩
5. Context reset: 完全重置，依赖外部 memory

**7 模式权限系统**:
- Plan Mode: 只读，不修改文件
- Code Mode: 可写代码文件
- Auto-edit Mode: 自动应用建议的修改
- Full-auto Mode: 完全自主执行
- 每种模式有独立的工具白名单和确认策略
- ML 分类器实时判定操作风险等级

**13 设计原则**（源自 5 个人类价值）:
- 安全性: 最小权限、渐进信任、可撤销
- 效率: 最小 token、缓存友好、批量操作
- 透明度: 可观测思考、操作日志、错误自解释
- 适应性: 用户偏好学习、项目规约发现、工具能力扩展
- 可靠性: 确定性排序、幂等操作、优雅降级

---

## 附录 B: 当前 SOUL.md Token 消耗分析

| Section | 预估 Tokens | 信息密度评级 | 建议 |
|---------|------------|-------------|------|
| §1 身份定义 | ~200 | 高 | 保留，压缩措辞 |
| §2 核心原则 | ~300 | 中 | 合并到身份定义 |
| §3 工具使用规则 | ~500 | 低（与工具 schema 重复） | 移除，由工具 schema 承载 |
| §4 代码操作规范 | ~400 | 低（与 §3 重叠） | 移除，由工具 description 承载 |
| §5 沟通风格 | ~200 | 中 | 压缩为 3 条规则 |
| §6 安全约束 | ~300 | 中（与 ToolRiskPolicy 重复） | 移除，由代码强制 |
| §7 Unity 特定知识 | ~400 | 低（PROJECT context 覆盖） | 移除，由 PROJECT 注入 |
| §8 记忆系统说明 | ~200 | 低（实现已自动化） | 移除 |
| §9 Think-then-Act | ~300 | 低（与 reasoning API 冲突） | 条件化移除 |
| §10 杂项规则 | ~200 | 极低 | 移除 |
| **合计** | **~3000** | — | **目标: ≤400** |

---

*文档版本: v1.0 | 生成时间: 2026-06-29*
