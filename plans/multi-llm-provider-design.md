# 多 LLM 供应商兼容与 Prompt Cache 设计方案

> 状态：设计草案（2026-10-22），基于 v1.16.0 代码现状 + 业界调研
> 范围：LLM 通信层（`Editor/LLM`）、AgentLoop 消息管线、Provider 配置、用量/成本统计
> 目标：Claude（Anthropic 原生）、OpenAI、DeepSeek、自建 vLLM、OpenRouter/LiteLLM/NewAPI 网关都能正确工作，**且每家都吃到各自最优的缓存折扣**

---

## 0. 结论摘要

1. **现状**：插件只有一套 OpenAI Chat Completions 协议实现。用 Claude 时不发 `cache_control`，消息内容是纯字符串，没地方挂缓存标记 → Claude 缓存命中率≈0，输入全价（命中价约 0.1×）。
2. **不只是 Claude 的问题**：消息历史有多处"回头改写"（记忆每轮重插到 system 后、TrimToFit 滑窗丢头、工具列表跨轮变化、REVISE 发 `tools:null`），这些对 DeepSeek / OpenAI / vLLM 的自动前缀缓存同样有害。
3. **更基础的问题**：插件目前**拿不到真实用量**——流式最后那个 usage chunk（`choices` 为空）被解析器直接丢弃；`Usage` 类也不认识任何缓存字段。没有数据就无法验证任何优化。
4. **另一个兼容性硬伤**：reasoning/thinking 内容从不写回 `_messages`。Claude 开 thinking + 工具调用时必须原样回传带签名的 thinking 块；DeepSeek 思考模式带 tools 时也要求回传 `reasoning_content`。
5. **业界共识架构**：一个**自有的规范消息模型** + **按协议（而非按厂商）划分的适配器** + **模型能力/价格注册表** + **按供应商选择的缓存策略** + **归一化 usage 与成本统计**。Cline、Roo Code、Vercel AI SDK、LiteLLM、OpenHands 都是这个形状。
6. **推荐路线**：P0 先补可观测性（真实用量 + 命中率）→ P1 前缀稳定化（立刻让 DeepSeek/vLLM 受益）→ P2 Anthropic 原生协议 + 缓存 → P3 注册表/成本/模型路由。

---

## 1. 现有架构分析

### 1.1 分层与调用关系

```
ChatWindow ──new OpenAICompatibleClient()──► AgentLoop(ILLMClient)
                                              │
   SendMessageAsync ─► 组装 _messages（插入多种 system 消息）
                     ─► BuildToolDefinitions()（每个用户回合一次）
                     ─► RunToolCallLoopAsync
                          └► CallLLMStreamAsync
                               ├ ConversationCompressor（原地改 _messages）
                               ├ ContextWindowManager.TrimToFit（浅拷贝 + 丢最旧）
                               └ FallbackRouter（同一 client 重试 2 次）
                                    └ OpenAICompatibleClient
                                         ├ SanitizeMessageToolCalls（原地改共享对象）
                                         ├ JsonHelper 序列化 ChatCompletionRequest
                                         ├ RequestEnrichment（JObject：stream_options / reasoning / ExtraRequestBody）
                                         ├ RequestPruningRegistry（按 400 报错学习禁用字段）
                                         ├ OpenAIChatTransporter → POST {endpoint}/chat/completions（仅 Bearer）
                                         └ StreamingResponseParser（SSE，只看 choices[0]）

旁路调用（同样是 OpenAICompatibleClient、同一 active profile）：
  ConversationCompressor / ToolResultCompressor / SessionAutoTitleService /
  AutoMemoryStrategy / SelfChallenge Node A & Node B
独立实现：VisionLLMClient（静态类，手拼 JObject，非 ILLMClient）
```

### 1.2 设计方法上的优点（可保留）

| 优点 | 位置 |
|---|---|
| AgentLoop 只依赖 `ILLMClient` 接口，构造注入 | `LLM/ILLMClient.cs`、`Core/AgentLoop.cs:235` |
| 传输层已独立（`OpenAIChatTransporter`），主模型与 vision 共用 | `LLM/OpenAIChatTransporter.cs` |
| `StreamChunk` 回调类型本身与协议无关（Content/Reasoning/ToolCallDelta/Done/Error） | `LLM/ChatCompletionModels.cs` |
| Enrichment / Pruning 在 JSON 层操作，对"兼容各种奇怪网关"很实用 | `LLM/RequestEnrichment.cs`、`LLM/RequestPruningRegistry.cs` |
| Provider Profile 已有 per-profile 覆盖（温度/max_tokens/reasoning/extraRequestBody） | `Config/ProviderProfile.cs`、`Config/ActiveModelConfig.cs` |
| 语言指令放 system[0] 且会话内不改写——已有"保 KV cache"意识 | CHANGELOG 相关条目 |

### 1.3 与多协议/缓存冲突的设计点

**A. 接口契约是 OpenAI 线格式**
- `ILLMClient` 的入参是 `List<ChatMessage>`（OpenAI DTO），返回 `ChatCompletionResponse`，调用方直接读 `Choices[0].Message.Content`。
- `ChatMessage.Content` 是 `string`：没有内容块、没有 reasoning/签名、没有 cache 标记。
- 会话持久化 `SerializableChatMessage`（`Session/SessionData.cs:232-299`）也是这个形状。

**B. 协议细节散落在协议层之外**
- 鉴权：`HttpClientFactory.CreateRequest` 只支持 Bearer（Anthropic 需 `x-api-key` + `anthropic-version`）。
- URL 硬编码 `/chat/completions`、`/models`。
- `ToolCallBuilder` 依赖 OpenAI 的 `delta.index`。
- `ReasoningParamMapper` 把 Anthropic 的 `thinking` 字段塞进 OpenAI 请求体——只有网关透传才有效。
- 多处假定 `messages[0]` 是唯一的 system prompt（`ContextWindowManager:161`、`ConversationCompressor:173`、`AgentLoop:971`）。

**C. 对话中间大量 system 消息**（一个回合 5–10 条，长会话无上限）

| 来源 | 插入位置 | 生命周期 | 缓存影响 |
|---|---|---|---|
| 记忆召回 `AgentLoop.Memory.cs:122` | system 段之后（历史最前面） | **每个用户回合删掉重插** | 🔴 整段历史每轮失效 |
| Deferred context / Workspace 快照 `AgentLoop.cs:664,675` | 首条 user 之前 | 首轮一次，常驻 | 🟢 |
| Skill 内容 `AgentLoop.SkillContext.cs:151` | 最新 user 之前；卸载时删除 | 常驻 | 🟡 卸载时中段失效 |
| Node A 指令 `AgentLoop.cs:619` | user 之后 | 累积 | 🟢（追加） |
| 空回复恢复 / 工具失败 / 重复调用警告 / 预算总结 / REVISE 反馈 `AgentLoop.Runner.cs` | 追加 | 常驻 | 🟢（追加） |
| Domain reload 恢复 `AgentLoop.DomainReload.cs` | 追加 | 常驻 | 🟢 |
| 压缩摘要 `ConversationCompressor.cs:379` | 替换被压缩区间 | 常驻 | 🟡 压缩当轮全失效（可接受） |

对 OpenAI 系：中段 system 合法。对 Anthropic：传统上只有顶层 `system` 参数，网关转换时通常把它们**提升合并到顶部** → 每加一条都会改写前缀，比不加缓存还糟（还多付写入费）。

**D. 前缀被改写的其它位置**

| 问题 | 位置 |
|---|---|
| TrimToFit 每轮按预算丢最旧消息（滑窗） | `Core/AgentLoop.LLM.cs:76` |
| `SanitizeMessageToolCalls` 原地修改共享 `ChatMessage` 对象 | `OpenAICompatibleClient` |
| 工具顺序来自反射 `GetTypes()` 顺序，未显式排序 | `Tools/Infrastructure/ToolAutoDiscovery.cs:62` |
| `request_tools` 激活类别 → 下一用户回合工具列表变化 | `Tools/Native/Meta/RequestToolsTool.cs:195` |
| Node B REVISE 发 `tools: null` | `Core/AgentLoop.Runner.cs:604` |

**E. 可观测性缺失**
- `StreamingResponseParser.ParseChunkJson`（172-224）在 `choices` 为空时直接返回 → 流式 usage 被丢弃。
- `Usage` 只有 `prompt/completion/total_tokens`。
- `TokenCounter` 是启发式估算（4 字符/token、CJK×2），预算、压缩触发、UI 全靠它。
- 没有成本显示、没有命中率。

**F. 其它已发现的 bug（顺带）**
- `AgentCoreSettings.GetEffectiveMaxTokens`（494-512）读的是全局设置而非 `ActiveModelConfig` → profile 的 max_tokens/reasoning 覆盖在计算 `max_tokens` 时被忽略。
- `AutoMemoryStrategy`（`Session/AutoMemoryStrategy.cs:246`）只发一条 system 消息、无 user 消息 → Anthropic 会拒绝。
- `CompressionTemperature=0.1f` 声明未使用。
- `FallbackRouter` 名不副实：只是同一 client 重试；流式失败重试时 UI 已收到的 token 不会回滚。

### 1.4 仓库内已有但未落地的相关规划

- `plans/_archive/design/agent-design-frontier-redesign-2026.md` §3.9 "Prompt Cache 稳定性"：工具按 (Category, Name) 确定性排序、system prompt 冻结、动态内容移入 user 消息。
- `plans/_archive/features/context-compression-implementation.md`：Phase-4 `PromptCachingManager`（Anthropic `cache_control`），未实现。
- `plans/v1.10.0-adversarial-audit.md:540-657`：OpenAI/Anthropic/GLM/DeepSeek 兼容测试矩阵，checkbox 未勾。

本方案吸收并取代上述条目。

---

## 2. 业界调研

> 来源见文末。⚠️ 标记为调研中未能从一手文档核实的项。

### 2.1 各家缓存机制

| 供应商 | 是否需显式开启 | 最小长度 | TTL | 写入价 | 读取价 | usage 命中字段 |
|---|---|---|---|---|---|---|
| **Anthropic 原生** | 是：`cache_control` 块级标记（≤4 个），或顶层 `cache_control` 自动缓存 | 512–4096（随模型） | 5m（默认）/ 1h；读取续期 | 1.25×（5m）/ 2×（1h） | ≈0.1× | `cache_read_input_tokens`、`cache_creation_input_tokens` |
| **OpenAI**（GPT-5.6 前） | 自动；`prompt_cache_key` 影响路由 | 1024 | 内存 5–10m / 24h | 无 | 按模型折扣 | `prompt_tokens_details.cached_tokens` |
| **OpenAI**（GPT-5.6+） | 默认 implicit，可 explicit `prompt_cache_breakpoint` | 1024 | 30m / 12h | 1.25× / 2.5× | 0.1× | 同上 + `cache_write_tokens` |
| **DeepSeek** | 自动（磁盘缓存，按"前缀单元"完整匹配） | — | 数小时–数天 | 1× | ≈0.1× | `prompt_cache_hit_tokens` / `prompt_cache_miss_tokens` |
| **Gemini** | 隐式自动；显式 `cachedContents` | 2048–4096 | ⚠️ 隐式约 3–5m | 隐式无 | ⚠️ ≈0.25× | `usageMetadata.cachedContentTokenCount` |
| **vLLM / SGLang** | 服务端开关 `--enable-prefix-caching` ⚠️默认值待查 | 1 个完整 block | LRU | GPU 时间 | 主要收益是首 token 延迟 | 需 `--enable-prompt-tokens-details` 才回 `prompt_tokens_details.cached_tokens` |

Anthropic 要点：
- 渲染顺序 **tools → system → messages**；前缀任何字节变化都使其后的缓存失效。
- 改工具定义/换模型 → 全部失效；改 system → system+messages 失效；改 thinking 配置 → messages 失效。
- 每个断点向前最多回溯约 20 个块；单轮追加块很多时需要中间断点。
- 推荐的 agent 循环模式：静态 system 末尾一个显式断点 + 顶层自动缓存覆盖尾部。
- 低于最小长度不缓存也不报错——要靠 usage 字段确认。

**关键警告**：
- 给 Gemini 发 `cache_control`，LiteLLM/OpenRouter 会把它转成**显式缓存**（收存储费）并停用隐式缓存。不要对 Gemini 发标记。
- OpenAI GPT-5.6+ 缓存写入开始收 1.25×，一次性请求开缓存反而更贵。

### 2.2 Claude 走 OpenAI 兼容路径的情况

| 路径 | 缓存 | Thinking 回传 | 结论 |
|---|---|---|---|
| Anthropic 官方 OpenAI 兼容端点 | ⚠️ 据了解不支持 prompt caching | `extra_body.thinking` | 不推荐用于 agent |
| OpenRouter | ✅ 透传内容块上的 `cache_control`，也支持顶层自动缓存；有会话粘性路由 | 需回传 `reasoning_details` | 可用，但消息需改成多段内容 |
| LiteLLM | ✅ 透传；`cache_control_injection_points` 或 `enable_anthropic_prompt_caching` 服务端自动注入 | 需回传 `thinking_blocks`（含 signature），否则 400 | 可用；服务端注入方式零客户端改动 |
| NewAPI（one-api 分支） | ❌ OpenAI→Claude 转换器只保留 type+text，**丢弃 `cache_control`**；计费侧能识别 Claude 缓存字段 | — | 必须走它的 Claude 原生 `/v1/messages` 透传 ⚠️部署相关 |

**结论：Claude 应走原生 `/v1/messages`。** 网关路径各家行为不一，只能作为补充。

### 2.3 主流 agent / 框架的多供应商架构

| 项目 | 规范消息模型 | 适配方式 | 缓存策略 | 能力/价格注册 |
|---|---|---|---|---|
| **Vercel AI SDK** | 自有 LanguageModel spec | 每家一个 provider 包；厂商特有参数放 `providerOptions.{anthropic,...}`（可挂到请求/消息/part） | `providerOptions.anthropic.cacheControl` | usage 归一为 `cacheReadTokens/cacheWriteTokens` |
| **Cline**（新版基于 AI SDK） | AI SDK | 路由规则 + reasoning codecs | Anthropic 类：最后一条 user 的最后一个文本 part；OpenAI 兼容路由保持多段内容以挂 `cache_control`；Bedrock 用 `cachePoint` | model catalog / capabilities |
| **Roo Code** | Anthropic `MessageParam` | `ApiHandler` 工厂（~30 家） | Anthropic：system + 最后两条 user；Gemini：system + 每第 10 条 user | `ModelInfo`：上下文、价格（含 cacheWrites/cacheReads）、supportsPromptCache、reasoning 能力 |
| **Continue** | 自有 | 每家 LLM 类 | `cacheSystemMessage` / `cacheConversation`，最后两条 user | — |
| **OpenHands** | LiteLLM | LiteLLM | system 拆成**静态块（缓存）+ 动态块（不缓存）**；第二断点放最后一条 user/tool；太短则自动去掉缓存重试 | LiteLLM 价格表 |
| **Aider** | 自有 | LiteLLM | 顺序固定：system → 只读文件 → repo map → 可编辑文件；`--cache-keepalive-pings` | `--weak-model` 做摘要/commit message |

共同模式：
1. **一个自有规范消息模型**（内容块 + 推理块 + 缓存提示），不直接用某家线格式当内部模型。
2. **按协议写适配器**，同协议不同厂商用方言开关（dialect flags）。
3. **能力/价格注册表**，常以 LiteLLM 的 `model_prices_and_context_window.json` 为种子。
4. **缓存策略按供应商选择**：显式标记型（Anthropic）打断点；自动型（OpenAI/DeepSeek/vLLM）只要求前缀稳定；Gemini 不发标记。
5. **推理内容原样保存、只回放给同一家**（签名不可伪造/改写）。

### 2.4 提高缓存命中率的通用纪律

1. **按稳定性排序**：tools（确定性排序与序列化）→ 冻结的 system → 静态项目上下文 → 只追加的历史 → 动态状态放最后一块。
2. **只追加**：不回头编辑、重排、删除历史（包括事后裁剪 tool 输出、删除旧提醒）。每轮提醒用"再追加一条"代替"删旧插新"。
3. **会话内固定**：工具集、模型、thinking/effort 配置。模式切换用消息表达，而不是换工具集。
4. **确定性序列化**：工具 schema 键序固定、数字用 invariant culture、tool_call arguments 保存原始 JSON 字符串而不是反序列化再序列化。
5. **压缩**：低频、大步（摘要一整段旧历史，保留开头 N 条 + 最近尾部），不做每轮滑窗；压缩后形成新基线。摘要请求最好用同一模型 + 同一前缀，末尾追加"请总结"指令，以读到主会话缓存。
6. **TTL**：工具轮之间 < 5 分钟用 5m；用户停顿较长可用 1h 或保活请求。
7. **监控**：每次调用记录归一化的 `cacheRead / cacheWrite / uncached`；命中率骤降时，对比相邻两次请求体（去掉缓存标记）找出第一处变化。

### 2.5 模型路由

- 小任务（标题、短摘要、commit message）用便宜模型。**但注意缓存按模型隔离**：大历史的压缩摘要用主模型（读主会话缓存）可能更便宜。
- Fallback 链（OpenRouter `models[]`、LiteLLM router）：换模型 = 全量缓存重建、thinking 块作废，应记录为一次缓存重置。
- 先试同模型降低 effort，再考虑换小模型（同模型共享缓存命名空间）。

---

## 3. 差距分析

| 维度 | 业界做法 | AgentCore 现状 | 差距 |
|---|---|---|---|
| 内部消息模型 | 自有、内容块化、含推理块与缓存提示 | OpenAI DTO，字符串内容 | 🔴 |
| 协议适配 | 按协议适配器 + 方言 | 单一 OpenAI 实现，厂商差异靠字段注入与 400 学习 | 🔴 |
| Claude 缓存 | 原生 API + 断点策略 | 无 | 🔴 |
| 推理内容回传 | 原样保存、同家回放 | 不进历史 | 🔴（Claude thinking+工具会失败） |
| 前缀稳定性 | 只追加、确定性排序 | 记忆重插、滑窗、工具列表跨轮变化 | 🟡 |
| system 消息 | 静态在顶、动态在尾；按协议渲染 | 十余种来源散落中段 | 🟡 |
| 用量与成本 | 归一化 usage + 价格表 + 命中率 | 流式 usage 被丢弃；仅估算 | 🔴 |
| 能力注册 | 数据驱动注册表 | 模型名前缀硬编码（3 处各自一套） | 🟡 |
| 路由/降级 | 主模型 + 小模型 + fallback | 全部同一 profile；FallbackRouter 仅重试 | 🟡 |

---

## 4. 目标架构

### 4.1 总览

```
AgentLoop / 旁路调用方
   │  LlmRequest { Conversation(规范消息), Tools, Options(reasoning, maxTokens, purpose) }
   ▼
ILLMClient（协议无关）──► LlmClient（唯一实现，组合以下部件）
                             ├ ModelProfile（注册表解析：能力、价格、缓存模式）
                             ├ ICacheStrategy（按 profile 选）：在规范消息上打 CacheHint
                             ├ IChatProtocol（按 profile.apiFormat 选）
                             │    ├ OpenAIChatProtocol + Dialect（OpenAI / DeepSeek / vLLM / OpenRouter / LiteLLM / NewAPI）
                             │    ├ AnthropicMessagesProtocol
                             │    └（后续）GeminiProtocol / OpenAIResponsesProtocol
                             ├ IAuthScheme（Bearer / x-api-key+anthropic-version / 自定义 header）
                             ├ Transport（HttpClient，SSE 行读取，通用）
                             └ UsageNormalizer → LlmUsage → UsageLedger（成本、命中率、UI）
   ▲
   │  LlmResult { Message(规范), Usage(归一), FinishReason, ProviderRaw }
```

原则：**AgentLoop 永远只说"规范消息"**；线格式只存在于 `IChatProtocol` 实现内部。

### 4.2 规范消息模型

在现有 `ChatMessage` 上**渐进扩展**，而不是一次性替换（降低对会话持久化、压缩、Sanitization 等十几处调用点的冲击）：

```csharp
public enum MessageKind { Normal, OperatorNote }   // OperatorNote = 现在那些"中段 system 消息"

public class ChatMessage
{
    public string Role { get; set; }                   // system / user / assistant / tool（保持不变）
    public MessageKind Kind { get; set; }              // 新增：区分真 system prompt 与运行时注记
    public string Content { get; set; }                // 保留：纯文本快捷路径（兼容旧代码与旧会话）
    public List<ContentPart> Parts { get; set; }       // 新增：非空时以 Parts 为准
    public List<ToolCall> ToolCalls { get; set; }      // 保持；ToolCall.Function.Arguments 保存原始 JSON 字符串
    public string ToolCallId { get; set; }
    public List<ReasoningBlock> Reasoning { get; set; } // 新增：随 assistant 消息保存
    public CacheHint Cache { get; set; }               // 新增：由 CacheStrategy 在发送前临时标记，不持久化
}

public abstract class ContentPart { }   // TextPart / ImagePart / ...
public class ReasoningBlock
{
    public string ProviderTag;    // "anthropic" / "deepseek" / "openrouter" ...
    public string ModelFamily;    // 只回放给同一家族
    public string Text;
    public string Signature;      // Anthropic thinking 签名
    public bool Redacted;         // redacted_thinking
    public string OpaqueJson;     // 无法结构化的原样块（如 OpenRouter reasoning_details）
}
public class CacheHint { public string Ttl; }   // "5m" / "1h"
```

`SerializableChatMessage` 加 `kind`、`parts`、`reasoning` 三个可选字段，旧会话文件仍可读。

### 4.3 中段 system 消息的统一处理（OperatorNote）

把第 1.3 C 节里除"真 system prompt"外的所有插入改为 `Kind = OperatorNote`，并按协议渲染：

| 协议 | 渲染方式 |
|---|---|
| OpenAI 系 | 原样 `role:"system"`（现状行为，不变） |
| Anthropic | 合并到**相邻的 user 回合**：作为 user 消息末尾的一个 text block（若紧跟 tool_result，则附在该 tool_result 所在 user 消息末尾）；用 `<system-note>…</system-note>` 包裹以保语义。⚠️ 新模型支持 `messages` 中的 `role:"system"`，可作为能力开关在注册表里启用 |

同时修正插入位置以保前缀稳定：
- **记忆召回**：改为"追加到本轮 user 消息之后/之前（尾部）"，**不再删除旧的**——旧召回留在历史中原位（或只在压缩时清理）。
- **Skill 卸载**：不删历史中的旧 skill 消息，改为追加一条"skill X 已卸载，忽略其指令"的 OperatorNote。
- 真 system prompt（index 0）保持冻结；如需区分静态/动态，参考 OpenHands 拆成"静态块（缓存）+ 动态块（不缓存）"。

### 4.4 协议适配器

```csharp
public interface IChatProtocol
{
    HttpRequestMessage BuildRequest(LlmRequest req, ModelProfile model, ProviderEndpoint ep);
    IAsyncEnumerable<StreamChunk> ParseStream(Stream sse, StreamAccumulator acc, CancellationToken ct);
    LlmResult ParseResponse(string json);
    LlmUsage NormalizeUsage(JObject usage);
}
```

**OpenAIChatProtocol**：现有 `OpenAICompatibleClient` + `RequestEnrichment` + `RequestPruningRegistry` + `ReasoningParamMapper` 的逻辑整体搬入，按 `Dialect` 分支：

| 方言开关 | 含义 |
|---|---|
| `reasoningParam` | `reasoning_effort` / `reasoning{}` / `chat_template_kwargs` / `extra_body.thinking` |
| `reasoningEcho` | 回传字段：无 / `reasoning_content`（DeepSeek）/ `reasoning_details`（OpenRouter）/ `thinking_blocks`（LiteLLM） |
| `cacheControlOnParts` | 是否把 CacheHint 渲染为内容块上的 `cache_control`（OpenRouter/LiteLLM 走 Claude 时） |
| `promptCacheKey` | 是否发 `prompt_cache_key`（OpenAI；值 = hash(项目+会话)） |
| `usageMap` | 命中字段映射 |

**AnthropicMessagesProtocol**（新写）：
- `POST {endpoint}/v1/messages`，头 `x-api-key` + `anthropic-version`。
- 首条 system → 顶层 `system`（text block 数组，静态块末尾挂 `cache_control`）。
- `tools` 转为 `{name, description, input_schema}`；`tool_calls` → `tool_use` 块；`tool` 消息 → user 消息里的 `tool_result` 块（同一轮多个结果合并进同一条 user 消息）。
- 保证 user/assistant 严格交替；OperatorNote 按 4.3 合并。
- assistant 消息先回放同家族的 `thinking`/`redacted_thinking` 块（带签名），再 text、tool_use。
- 流式解析按 SSE `event:` 类型：`message_start`（含 usage）/ `content_block_start` / `content_block_delta`（`text_delta`、`input_json_delta`、`thinking_delta`、`signature_delta`）/ `content_block_stop` / `message_delta`（stop_reason、最终 usage）。工具参数按 content block index 累积。
- `thinking: {type:"enabled", budget_tokens}` 由 ReasoningLevel 映射（沿用现有档位表）；新模型若注册表标记支持 effort 参数则改用 effort。
- 空 user 场景（如 `AutoMemoryStrategy` 只有 system）由适配器补一条最小 user 消息，或调用方修正。

**StreamChunk 不变**——UI 层与 AgentLoop 的流式回调零改动。

### 4.5 缓存策略

```csharp
public interface ICacheStrategy { void Apply(LlmRequest req, ModelProfile model); }
```

| 策略 | 适用 | 行为 |
|---|---|---|
| `AnthropicBreakpoints` | Anthropic 原生 | ① 静态 system 末尾一个断点（覆盖 tools+system）；② 支持时用顶层自动 `cache_control` 覆盖尾部；不支持时在**最后两条 user/tool_result 回合**末尾打断点（Cline/Roo 做法）；③ 单轮追加块数 > ~15 时插中间断点；总数 ≤ 4 |
| `PartsCacheControl` | OpenRouter / LiteLLM 走 Claude | 同上逻辑，但渲染为 OpenAI 格式内容块上的 `cache_control` |
| `PromptCacheKey` | OpenAI | 发 `prompt_cache_key`；其余靠前缀稳定 |
| `PrefixOnly` | DeepSeek / vLLM / Gemini / 未知 | 不发任何标记，只依赖前缀稳定纪律 |

TTL 默认 5m；当检测到用户长时间未发言（下次请求距上次 > 5 分钟的比例高）时可在注册表或设置中切 1h。

### 4.6 前缀稳定化（对所有供应商生效）

| 改动 | 位置 |
|---|---|
| 工具列表按 (Category, Name) 确定性排序；JSON schema 序列化键序固定 | `ToolRegistry` / `ToolDefinitionBuilder` |
| 会话内工具集只增不减：`request_tools` 激活的类别在**当前回合**即生效（修复承诺与实际不符），且后续回合保持；禁用工具时用 OperatorNote 告知而非移除（或接受一次缓存失效并记录） | `ToolScopeResolver`、`RequestToolsTool` |
| Node B REVISE 保持原工具列表（不再发 `tools:null`） | `AgentLoop.Runner.cs:604` |
| TrimToFit 从"每轮滑窗"改为"仅在超限时整体压缩一次"，压缩后形成新基线 | `ContextWindowManager`、`AgentLoop.LLM.cs` |
| `SanitizeMessageToolCalls` 在入历史时执行一次并幂等，发送路径不再原地改 | `OpenAICompatibleClient` → 移到 AgentLoop 写入历史处 |
| 记忆召回、Skill 卸载改为追加式（见 4.3） | `AgentLoop.Memory.cs`、`AgentLoop.SkillContext.cs` |

### 4.7 归一化用量与成本

```csharp
public struct LlmUsage
{
    public int InputUncached;   // 未命中输入
    public int CacheRead;
    public int CacheWrite;      // 可再按 TTL 拆分
    public int Output;
    public int Reasoning;
    public int TotalInput => InputUncached + CacheRead + CacheWrite;
}
```

映射时注意重复计算：Anthropic 的 `input_tokens` **不含**缓存部分；OpenAI/DeepSeek 的 `prompt_tokens` **包含**缓存部分。

- 修复 `StreamingResponseParser`：`choices` 为空但有 `usage` 的 chunk 要解析出来（新增 `StreamChunk.Usage` 类型或在 Done 中携带）。
- `UsageLedger`：按会话累计 token、命中率、成本（价格来自注册表）；在 ChatWindow 显示（例："本会话 输入 1.2M（命中 87%）· 输出 34k · ≈ $0.42"）。
- 预算/压缩触发：有真实 usage 时以上一轮真实 `TotalInput` + 新增估算为准，`TokenCounter` 只做兜底。

### 4.8 配置模型

**ProviderProfile 新增**：

| 字段 | 说明 |
|---|---|
| `apiFormat` | `OpenAIChat`（默认，兼容旧 profile）/ `AnthropicMessages` |
| `dialect` | `Auto` / `OpenAI` / `DeepSeek` / `Vllm` / `OpenRouter` / `LiteLLM` / `NewAPI` / `Custom`；Auto 按 endpoint 主机名 + 模型名推断 |
| `cacheTtl` | `Auto` / `5m` / `1h` |
| `authScheme` | 由 apiFormat 推导，允许覆盖（自定义 header 名） |

**ModelProfile 注册表**（新增，数据驱动）：
- 内置 `ModelRegistry.json`（随包发布），可被 `ProjectSettings/AgentCoreModelOverrides.json` 覆盖。
- 字段：`contextWindow`、`maxOutput`、价格（input / output / cacheRead / cacheWrite5m / cacheWrite1h）、`minCacheTokens`、`cacheMode`、`reasoning`（none / effort / budget）、`supportsVision`、`supportsMidConversationSystem`、`supportsAutoCache`。
- 匹配规则：精确名 → 前缀 → 默认；取代 `ContextWindowManager`、`ModelCapabilityDetector`、`ReasoningParamMapper` 三处各自的前缀硬编码表。
- 种子数据可从 LiteLLM `model_prices_and_context_window.json` 摘取；`/models` 探测（vLLM `max_model_len`）继续作为运行时覆盖。

### 4.9 模型路由

- 设置项：`mainProfile`、`utilityProfile`（可空 = 同主模型）。
- 标题生成、短工具结果压缩 → utilityProfile；**会话历史压缩默认走主模型**（复用主会话缓存，且通常质量更好），可配置。
- `FallbackRouter` 拆为两层：`RetryPolicy`（同 profile 重试，保留现状）+ 可选 `fallbackProfiles[]`；切换时在 UI 与日志中标明"缓存将重建"。
- `VisionLLMClient` 并入 `ILLMClient`（purpose = Vision），复用协议与鉴权。

---

## 5. 分阶段实施

### P0 可观测性（小，先做）
- [ ] 修复流式 usage chunk 被丢弃
- [ ] `Usage` 增加各家缓存字段，归一化为 `LlmUsage`
- [ ] 每次调用日志：`in_uncached / cache_read / cache_write / out / hit%`
- [ ] ChatWindow 显示会话累计用量与命中率（成本可后置到 P3）
- [ ] 顺带修 `GetEffectiveMaxTokens` 读全局设置的 bug

**验收**：DeepSeek / vLLM（需服务端 `--enable-prompt-tokens-details`）下能看到真实命中率；记录当前基线。

### P1 前缀稳定化（所有供应商立刻受益）
- [ ] 工具确定性排序 + 会话内工具集只增不减 + REVISE 保留 tools
- [ ] 记忆召回、Skill 卸载改为追加式
- [ ] 中段 system 消息标记 `Kind = OperatorNote`（OpenAI 系渲染不变）
- [ ] TrimToFit 滑窗 → 超限整体压缩
- [ ] Sanitize 移到入历史时、幂等
- [ ] **前缀回归测试**：对同一会话，第 N+1 轮序列化后的请求体（去缓存标记）必须以第 N 轮请求体为前缀（压缩轮除外）

**验收**：DeepSeek 多轮工具会话命中率显著高于 P0 基线（目标 > 80%，需实测）。

### P2 Anthropic 原生协议 + 缓存
- [ ] `ChatMessage` 扩展 `Parts / Reasoning / Kind`；会话持久化 schema 向后兼容
- [ ] 抽出 `IChatProtocol`、`IAuthScheme`；`OpenAICompatibleClient` 重构为 `OpenAIChatProtocol`
- [ ] 实现 `AnthropicMessagesProtocol`（请求构建、event SSE 解析、thinking 回放、OperatorNote 合并）
- [ ] `AnthropicBreakpoints` 缓存策略
- [ ] DeepSeek 方言：带 tools 时回传 `reasoning_content`
- [ ] ProviderProfile 增加 `apiFormat`；Settings UI 增加选择
- [ ] 兼容矩阵测试（补完 v1.10.0 审计里那条 checkbox）：OpenAI / Claude 原生 / DeepSeek / vLLM / OpenRouter-Claude × {纯文本, 工具循环, thinking+工具, 压缩后续聊, domain reload 恢复}

**验收**：Claude 多轮工具会话 `cache_read_input_tokens` 占输入 > 80%；thinking + 工具循环不报 400。

### P3 注册表、成本、路由
- [ ] `ModelRegistry.json` + 覆盖文件；替换三处前缀表
- [ ] 成本计算与 UI
- [ ] `utilityProfile`、fallback 链、Vision 并入
- [ ] OpenRouter / LiteLLM 方言的 `cache_control` on parts、`prompt_cache_key`

### P4（可选）
- Gemini 原生协议、OpenAI Responses API、Anthropic 服务端 compaction、1h TTL 自适应、保活请求。

---

### 实施记录（v1.17.0，与上文设计的偏差）

- **协议适配层形态**：未引入 `IChatProtocol` + 统一 `LlmRequest`，而是在现有 `ILLMClient` 之上做客户端级切换——`RoutingLLMClient` 按 `ActiveModelConfig.ApiFormat` 每次调用选择 `AnthropicMessagesClient`（新）或 `OpenAICompatibleClient`（原有）。理由：`ILLMClient` / `StreamChunk` 已是稳定边界，客户端级切换改动面最小，AgentLoop / UI 零改动。Anthropic 侧拆为纯函数 `AnthropicMessageMapper`（请求映射）+ `AnthropicStreamAccumulator`（事件累积），可脱离网络单测。
- **OpenAI 方言**：只实现了与缓存/正确性直接相关的三项——`reasoning_content` 回传（DeepSeek）、`prompt_cache_key`（OpenAI）、`cache_control` 内容块（OpenRouter/LiteLLM + Claude），位于 `OpenAIDialectAdapter`。`reasoning_details` / `thinking_blocks` 回传未做（OpenRouter/LiteLLM 下 Claude 的 thinking 不回放，只影响推理连续性，不影响缓存）。
- **OperatorNote**：记忆召回与 skill 卸载提示标记为 `MessageKind.OperatorNote`；Anthropic 路径包成 `<system-reminder>` 并入相邻 user 消息，OpenAI 路径仍以 system 消息发送（多数兼容服务端接受中段 system）。
- **截断水位线**：`ContextWindowManager.TrimToFit` 超限时截到可用预算 70%，用 `ConditionalWeakTable<历史列表, 锚点消息>` 记住截断点，后续轮次沿用直到再次超限；压缩改写历史使锚点消失时自动回退为重新截断，无需显式 reset。
- **Fallback 链**：在 `FallbackRouter` 重试耗尽后按 profile 的 `fallbackProfileIds` 顺序用 `ActiveModelConfig.UseProfile`（AsyncLocal）临时切换 profile 重发，不改变用户选择的 active profile。
- **视觉**：Vision 配置无 `apiFormat` 字段，Anthropic 端点按 host 自动识别。
- **按需工具不再改动工具列表**：`AgentLoop.BuildToolDefinitions` 每轮声明稳定的工具全集（`ToolScopeResolver.ResolveDeclaredTools`），OnDemand（scoping 开启时）与 Restricted 标记 `ToolDefinition.DeferLoading`。每次 LLM 调用前把作用域内已可见、历史中尚未启用的延迟工具追加为一条 `MessageKind.ToolChange` 消息（`Tools now available: a, b`，随会话持久化）。注册表 `supports_tool_changes` 的 Claude 模型：全集带 `defer_loading` 发送，ToolChange 渲染为紧邻下一条 assistant 之前的 `tool_addition` 系统消息（beta `mid-conversation-tool-changes-2026-07-01`），tools 与已缓存前缀均不变；端点 400 拒绝时本进程内记住并退回。其他情况（OpenAI 兼容、Sonnet 5、Haiku 等）只发送已启用工具，ToolChange 不上线。截断丢掉的 ToolChange 合并插回保留段首条消息之后；历史里调用过但无启用记录的延迟工具（被压缩掉）在调用前补启用。副作用修复：`request_tools` 激活的分类此前要到下一条用户消息才可见，现在同一轮工具循环的下一次调用即可用。
- **未做**：Gemini 原生协议、Responses API、1h TTL 自适应、保活请求（均属 P4）。

## 6. 风险与待验证项

| 项 | 说明 |
|---|---|
| ⚠️ Anthropic OpenAI 兼容端点是否支持缓存 | 调研时文档不可达；本方案不依赖它 |
| ⚠️ NewAPI 原生 Claude 透传是否保留 `cache_control` | 取决于部署版本；如团队要经 NewAPI 访问 Claude，需实测 |
| ⚠️ vLLM 是否默认开启 prefix caching | 检查内网实例启动参数；同时开 `--enable-prompt-tokens-details` 以便 P0 观测 |
| ⚠️ Anthropic 中段 `role:"system"`、tools 增减 beta 等新特性 | 均为模型/beta 门控，作为注册表能力开关，默认关闭 |
| 会话持久化 schema 变更 | 新字段可选；旧版本插件读新会话文件会丢 reasoning（可接受） |
| 追加式改造使历史变长 | 由压缩兜底；需观察压缩触发频率 |
| Anthropic 最小缓存长度 | 低于阈值静默不缓存；system prompt 本身通常已超过，问题不大 |

---

## 7. 参考

- Anthropic Prompt Caching：https://platform.claude.com/docs/en/build-with-claude/prompt-caching
- OpenAI Prompt Caching：https://developers.openai.com/api/docs/guides/prompt-caching
- DeepSeek KV Cache：https://api-docs.deepseek.com/guides/kv_cache
- DeepSeek Thinking Mode：https://api-docs.deepseek.com/guides/thinking_mode
- Gemini Caching：https://ai.google.dev/gemini-api/docs/caching
- vLLM Prefix Caching：https://docs.vllm.ai/en/latest/design/prefix_caching.html
- OpenRouter Prompt Caching：https://openrouter.ai/docs/guides/best-practices/prompt-caching
- OpenRouter Reasoning Tokens：https://openrouter.ai/docs/guides/best-practices/reasoning-tokens
- LiteLLM Prompt Caching：https://docs.litellm.ai/docs/completion/prompt_caching
- LiteLLM Reasoning Content：https://docs.litellm.ai/docs/reasoning_content
- LiteLLM 价格/能力表：https://github.com/BerriAI/litellm/blob/main/model_prices_and_context_window.json
- NewAPI OpenAI→Claude 转换：https://github.com/QuantumNous/new-api/blob/main/relaykit/relayconvert/internal/oai_chat/to_claude_messages_req.go
- Vercel AI SDK Providers：https://ai-sdk.dev/docs/foundations/providers-and-models
- Cline LLM SDK：https://github.com/cline/cline/tree/main/sdk/packages/llms
- Roo Code 缓存策略：https://github.com/RooCodeInc/Roo-Code/blob/main/src/api/transform/caching/anthropic.ts
- Continue Anthropic：https://github.com/continuedev/continue/blob/main/core/llm/llms/Anthropic.ts
- OpenHands SDK LLM：https://github.com/OpenHands/software-agent-sdk/blob/main/openhands-sdk/openhands/sdk/llm/llm.py
- Aider Caching：https://aider.chat/docs/usage/caching.html
- Zed Anthropic client：https://github.com/zed-industries/zed/blob/main/crates/anthropic/src/anthropic.rs
