# ThinkingDrawer 设计文档

> 将 LLM reasoning/thinking 内容从最终回复中分离，以默认折叠的"思考抽屉"形式展示。
> 用户需要时展开查看 LLM 的思考和决策逻辑，用于诊断问题。

---

## 1. 需求背景

### 1.1 当前问题

- `assistantTurn.Content` 混合了 reasoning 和 final answer
- `HandleFinalResponse()` 用 `assistantMessage.Content` 覆盖整个 `assistantTurn.Content`，导致流式阶段显示的思考内容被最终回复替代
- 用户无法回溯 LLM 的决策过程，难以诊断错误

### 1.2 目标

- 思考链保留在 UI 中，默认折叠，供需要时查看
- 最终回复独立显示为标准对话气泡
- 不影响不支持 reasoning 的模型（透明降级）

---

## 2. 设计概览

### 2.1 UI 布局

```
┌─ 用户消息气泡 ───────────────────────────┐
│ "帮我创建一个 Player 预制体"              │
└──────────────────────────────────────────┘

┌─ ThinkingDrawer (折叠) ──────────────────┐
│ [▸] 思考完成 · 3.2s                      │
└──────────────────────────────────────────┘

┌─ ToolCallGroup (折叠) ───────────────────┐
│ [▸] 已使用 2 个工具 · 全部成功            │
└──────────────────────────────────────────┘

┌─ 助手最终回复气泡 ───────────────────────┐
│ 已成功创建 Player 预制体，包含...         │
└──────────────────────────────────────────┘
```

### 2.2 状态流转

| 阶段 | ThinkingDrawer 状态 | 标题显示 |
|------|---------------------|----------|
| 收到第一个 Structured Reasoning token | 创建并显示（折叠） | "思考中 · 0s"（读秒递增） |
| 普通 content 开头检测到合法 `---THINKING---` | 创建并显示（折叠），进入 Visible Planning Trace buffer | "思考中 · 0s"（读秒递增） |
| 继续收到 reasoning/planning token | 保持折叠，内部累积文本 | "思考中 · 1s" → "思考中 · 2s" → ... |
| Structured Reasoning 后开始收到有效 ContentToken / ToolCallDelta | 标记 reasoning 完成 | "思考完成 · {duration}s" |
| Visible Planning Trace 检测到 `---ACTION---` 且 action 内容开始输出 | 标记 planning trace 完成 | "思考完成 · {duration}s" |
| 用户点击展开 | 展开，set text 到 label | 同上 |
| 用户点击折叠 | 折叠，清空 label | 同上 |
| 多轮循环再次收到 reasoning/planning trace | 追加内容，恢复读秒 | "思考中 · Ns"（继续递增） |

---

## 3. 协议层变更

### 3.1 DeltaContent 处理策略

文件：`Editor/LLM/ChatCompletionModels.cs`

不建议只在 `DeltaContent` 上增加单个 `ReasoningContent` 字段来解决兼容性，因为不同转发层可能使用 `reasoning`、`thinking`、content block 等格式。协议层采用两级策略：

1. `StreamingResponseParser` 先从 raw `JObject` 调用 `ReasoningFieldExtractor`，提取所有已知 structured reasoning 字段。
2. 现有 `DeltaContent` 继续承载 `role`、`content`、`tool_calls` 等标准字段，避免把 provider-specific 字段散落到模型类。

如实现时为了 DeepSeek/Qwen 调试需要保留 `ReasoningContent` 属性，也只能作为兼容字段，不能作为唯一判断来源。

### 3.2 StreamChunkType 扩展

文件：`Editor/LLM/ChatCompletionModels.cs`

```csharp
public enum StreamChunkType
{
    ContentToken,
    ReasoningToken,    // 新增
    ToolCallDelta,
    Done,
    Error
}
```

### 3.3 StreamChunk 扩展

```csharp
public class StreamChunk
{
    // ... 现有字段 ...

    /// <summary>Reasoning token（Type == ReasoningToken 时有值）</summary>
    public string ReasoningContent { get; set; }

    // 新增工厂方法
    public static StreamChunk Reasoning(string content) =>
        new() { Type = StreamChunkType.ReasoningToken, ReasoningContent = content };
}
```

### 3.4 StreamingResponseParser 修改

文件：`Editor/LLM/StreamingResponseParser.cs`

`ParseChunkJson()` 不能只依赖强类型 `DeltaContent.ReasoningContent`，否则未知字段会在反序列化阶段被丢弃。设计改为先用原始 `JObject` 提取 reasoning，再进入现有强类型解析。

```csharp
var raw = JObject.Parse(json);
var rawDelta = raw["choices"]?[0]?["delta"] as JObject;
var reasoning = ReasoningFieldExtractor.Extract(rawDelta);
if (!string.IsNullOrEmpty(reasoning))
{
    onChunk?.Invoke(StreamChunk.Reasoning(reasoning));
}

// 后续保留现有强类型解析：content、tool_calls、finish_reason
var chunk = JsonHelper.Deserialize<ChatCompletionChunk>(json);
```

注意：不能用 `return` 短路 reasoning，因为某些 provider 可能在同一个 delta 中同时返回 reasoning 字段和 `content`（虽然罕见）。reasoning 必须优先于普通 `content` 提取，避免 thinking block 被混入最终回复气泡。

#### 3.4.1 ReasoningFieldExtractor

新增内部 extractor，集中处理不同 provider 的字段差异。

```csharp
internal static class ReasoningFieldExtractor
{
    public static string Extract(JObject delta)
    {
        // 字符串字段优先级：
        // reasoning_content > reasoning > thinking > thought > reasoning_text
        // 数组 content block：type == thinking / reasoning
        // 未命中返回 null，透明降级。
    }
}
```

字段兼容优先级：

| 优先级 | 字段格式 | 说明 |
|--------|----------|------|
| 1 | `delta.reasoning_content` | DeepSeek / Qwen 常见格式 |
| 2 | `delta.reasoning` | 部分 OpenAI-compatible provider 可能使用 |
| 3 | `delta.thinking` | Anthropic-style 转发层可能使用 |
| 4 | `delta.thought` | 少数代理层可能使用 |
| 5 | `delta.reasoning_text` | 兜底兼容字段 |
| 6 | `delta.content[]` 中 `type == "thinking"` / `"reasoning"` | Anthropic content block 或混合转发结构 |

### 3.5 Provider 兼容性矩阵（预期）

| Provider | 可解析 reasoning 字段 | 预期可用 |
|----------|----------------------|----------|
| DeepSeek R1 | `delta.reasoning_content` | 有 |
| Qwen 3 (thinking mode) | `delta.reasoning_content` | 有 |
| Claude (one-api/new-api 转发) | 取决于转发层是否暴露 `thinking` / content block | 可能有 |
| GPT-5.5 / GPT-4o | 取决于 provider 是否暴露 reasoning 字段 | 默认无，暴露后可兼容 |
| `<think>` 标签嵌入普通 content | 无结构化字段 | 暂不支持（V2 考虑） |

### 3.6 可见规划文本与结构化 reasoning 的边界

Claude Opus 4.6 在 AgentCore 中可能会按系统规则输出类似 `---THINKING---` / `---ACTION---` 的文本格式。它属于 **普通 assistant content**，不是 provider API 的结构化 reasoning 字段，但可以作为 ThinkingDrawer 的第二来源：可见规划链（Visible Planning Trace）。

ThinkingDrawer 支持双来源：

| 来源 | 数据位置 | 示例 | 处理方式 |
|------|----------|------|----------|
| Structured Reasoning | API 结构化字段 | `delta.reasoning_content` / `delta.thinking` | 直接进入 ThinkingDrawer，不进入最终回复气泡 |
| Visible Planning Trace | 普通 `content` | `---THINKING--- ... ---ACTION---` | 由 `VisiblePlanningTraceExtractor` 严格抽取，进入 ThinkingDrawer；最终回复显示 `---ACTION---` 后内容 |

边界规则：

- `ReasoningFieldExtractor` 负责 API-level structured reasoning。
- `VisiblePlanningTraceExtractor` 负责 prompt-level visible planning trace。
- 可见规划链可以作为思维链保留，但不能和结构化 reasoning 混为同一协议字段。
- 默认只抽取明确位于回复开头、同时包含 `---THINKING---` 和 `---ACTION---` 的规划块。
- 普通解释文本、代码示例、用户引用中的 `---THINKING---` 不应被抽取。
- 如果未来支持 `<think>...</think>` 或其他内嵌格式，应作为可配置兼容策略，并需要严格过滤，避免误删用户可见回复。

### 3.7 Provider 实际测试记录

> 测试方式：通过与当前 AgentCore 相同的 OpenAI 兼容接口调用各模型，观察流式响应行为。
> 测试环境：用户本地 Unity Editor + one-api/new-api 转发层。
> 测试范围已收敛为四个模型：Claude Opus 4.6、Qwen3-VL、Claude Sonnet 4.5、GPT-5.5。

| # | 模型 | 测试日期 | 流式响应 | Tool Calling | 多轮上下文 | Structured Reasoning | Visible Planning Trace | ThinkingDrawer 可用 | 备注 |
|---|------|---------|---------|-------------|-----------|----------------------|------------------------|--------------------|----|
| 1 | Claude Opus 4.6 | 2025-06-25 | 正常 | 正常 | 正常 | **未观察到** | **可用（规则输出）** | 可用（Visible Planning Trace） | 当前 one-api/new-api 转发层未暴露结构化 reasoning 字段；但 AgentCore 规则可让模型在普通 content 中输出 `---THINKING---` / `---ACTION---`，可由 `VisiblePlanningTraceExtractor` 抽取 |
| 2 | Qwen3-VL-235B-A22B-Instruct-FP8 | 2025-06-25 | 正常 | 正常 | 正常 | **`delta.reasoning_content`** | 可选 | 可用（Structured Reasoning） | 结构化 reasoning 字段正常输出，优先使用 `ReasoningFieldExtractor` |
| 3 | Claude Sonnet 4.5 | 2025-06-25 | 正常 | 正常 | 正常 | **未观察到** | **可用（规则输出）** | 可用（Visible Planning Trace） | 与 Opus 相同，结构化字段取决于转发层；普通 content 中的规划块可作为第二来源 |
| 4 | GPT-5.5 | 2025-06-25 | 正常 | 正常 | 正常 | **未观察到** | **可用（规则输出）** | 可用（Visible Planning Trace） | 复测未观察到结构化 reasoning 字段；若按 AgentCore 规则输出 `---THINKING---` / `---ACTION---`，可作为可见规划链抽取 |

结论：ThinkingDrawer 应采用双来源设计。Qwen3-VL 走 Structured Reasoning；Claude Opus / Claude Sonnet / GPT-5.5 在当前转发层下没有结构化 reasoning，但可以通过 Visible Planning Trace 保留 AgentCore 规则输出的可见规划链。结构化字段优先；可见规划链作为兼容路径。

---

## 4. 数据层变更

### 4.1 ConversationTurn 扩展

文件：`Editor/Core/MessageTypes.cs`

`ConversationTurn` 需要同时支持结构化 reasoning 和可见规划链。`Content` 始终代表最终气泡显示内容，不能包含已抽取的 thinking/planning trace。

```csharp
public enum ThinkingTraceSource
{
    None,
    StructuredReasoning,
    VisiblePlanningTrace,
    Mixed
}

public enum VisiblePlanningTraceState
{
    None,
    Buffering,
    Completed,
    Invalid
}

public class ConversationTurn
{
    // ... 现有字段 ...

    /// <summary>最终回复气泡内容；不得包含已抽取的 thinking/planning trace。</summary>
    public string Content { get; set; }

    /// <summary>完整思考链内容；不进入 _messages，不发送给 LLM。</summary>
    public string Reasoning { get; set; }

    /// <summary>思考链来源。</summary>
    public ThinkingTraceSource ReasoningSource { get; set; }

    /// <summary>推理耗时（毫秒）。</summary>
    public double ReasoningDurationMs { get; set; }

    /// <summary>原始 assistant content，仅用于审计/恢复，不进入 LLM 上下文。</summary>
    public string RawAssistantContent { get; set; }

    /// <summary>可见规划链解析状态，用于流式与 Domain Reload 恢复。</summary>
    public VisiblePlanningTraceState PlanningTraceState { get; set; }
}
```

### 4.2 内容分离规则

- `Reasoning` 不计入 `_messages`，只用于 UI、Session、导出和恢复。
- `Content` 只保存最终可见回复，作为 `MessageBubble` 内容。
- `_messages` 中的 assistant message 只能写入清洗后的 `Content`，不能写入 `RawAssistantContent`。
- `RawAssistantContent` 只保留原始输出，便于调试和会话审计；默认不展示给用户。
- `ReasoningSource` 用于区分 Structured Reasoning、Visible Planning Trace 或 Mixed，避免恢复时丢失来源语义。
- 旧会话文件缺少新增字段时：`Reasoning = null`、`ReasoningSource = None`、`PlanningTraceState = None`，不显示 ThinkingDrawer。

### 4.3 Session 序列化字段

文件：`Editor/Session/SessionData.cs`

`SerializableConversationTurn` 需要新增并向后兼容以下字段：

```csharp
[JsonProperty("reasoning", NullValueHandling = NullValueHandling.Ignore)]
public string Reasoning { get; set; }

[JsonProperty("reasoning_source", NullValueHandling = NullValueHandling.Ignore)]
public string ReasoningSource { get; set; }

[JsonProperty("reasoning_duration_ms", DefaultValueHandling = DefaultValueHandling.Ignore)]
public double ReasoningDurationMs { get; set; }

[JsonProperty("raw_assistant_content", NullValueHandling = NullValueHandling.Ignore)]
public string RawAssistantContent { get; set; }

[JsonProperty("planning_trace_state", NullValueHandling = NullValueHandling.Ignore)]
public string PlanningTraceState { get; set; }
```

序列化约束：

- `FromConversationTurn()` 保存 reasoning/source/duration/raw/state。
- `ToConversationTurn()` 恢复新增字段；未知枚举值降级为 `None` 或 `Invalid`。
- `raw_assistant_content` 可为空；不要为了兼容旧会话把 `Content` 复制成 raw。

### 4.4 DomainReloadState 扩展

文件：`Editor/Core/DomainReloadState.cs`

Domain Reload 必须能恢复正在流式输出的 thinking/planning 状态，不能只保存 partial assistant content。

```csharp
/// <summary>最后一条 assistant 的 partial reasoning 内容。</summary>
[SerializeField] private string _lastAssistantReasoning;

/// <summary>最后一条 assistant 的 reasoning 来源。</summary>
[SerializeField] private string _lastAssistantReasoningSource;

/// <summary>Visible Planning Trace 的流式解析 buffer。</summary>
[SerializeField] private string _visiblePlanningTraceBuffer;

/// <summary>Visible Planning Trace 解析状态。</summary>
[SerializeField] private string _visiblePlanningTraceState;

/// <summary>Reasoning 已累计耗时。</summary>
[SerializeField] private double _lastAssistantReasoningDurationMs;
```

恢复规则：

- 如果 `VisiblePlanningTraceState == Buffering`，恢复后继续 buffer，不把半截 thinking 写入最终气泡。
- 如果 `Reasoning` 非空但未 completed，恢复 ThinkingDrawer 为“思考中”，继续读秒或从已累计时长继续。
- 如果 `Reasoning` 已 completed，恢复为“思考完成 · Xs”。
- 如果状态无法解析，保守策略是原样显示 content，不抽取，避免误删最终回复。

---

## 5. Core 层变更

### 5.1 双来源处理总览

Core 层需要同时处理两条数据流：

| 来源 | 输入 | 输出到 ThinkingDrawer | 输出到 MessageBubble / `_messages` |
|------|------|----------------------|------------------------------------|
| Structured Reasoning | `StreamChunkType.ReasoningToken` | `chunk.ReasoningContent` | 不写入 |
| Visible Planning Trace | `StreamChunkType.ContentToken` | `---THINKING---` 与 `---ACTION---` 之间内容 | `---ACTION---` 后内容 |

处理优先级：

1. 先处理 Structured Reasoning。
2. 再处理普通 content 中的 Visible Planning Trace。
3. 如果两者同时存在，Structured Reasoning 优先作为 drawer 内容；Visible Planning Trace 默认只用于清洗最终 content，避免重复显示。

### 5.2 Structured Reasoning 流程

文件：`Editor/Core/AgentLoop.LLM.cs`

```csharp
case StreamChunkType.ReasoningToken:
    AppendReasoning(
        assistantTurn,
        chunk.ReasoningContent,
        ThinkingTraceSource.StructuredReasoning,
        currentRound);
    break;
```

`AppendReasoning()` 负责：

- 首次收到 reasoning 时记录开始时间。
- 多轮 reasoning 自动插入轮次分隔符。
- 设置 `assistantTurn.ReasoningSource`。
- 追加到 `assistantTurn.Reasoning`。
- 发射 `AgentEvent.ReasoningToken(...)`。

### 5.3 Visible Planning Trace 流程

文件：`Editor/Core/AgentLoop.LLM.cs`

普通 `ContentToken` 不能直接追加到 `assistantTurn.Content`，需要先经过 `VisiblePlanningTraceExtractor`：

```csharp
case StreamChunkType.ContentToken:
    var result = _visiblePlanningTraceExtractor.ProcessToken(chunk.Content);

    if (!string.IsNullOrEmpty(result.ReasoningDelta))
    {
        AppendReasoning(
            assistantTurn,
            result.ReasoningDelta,
            ThinkingTraceSource.VisiblePlanningTrace,
            currentRound);
    }

    if (result.JustCompleted)
    {
        CompleteReasoningIfNeeded(assistantTurn);
    }

    if (!string.IsNullOrEmpty(result.ContentDelta))
    {
        assistantTurn.Content += result.ContentDelta;
        EmitEvent(AgentEvent.StreamToken(result.ContentDelta, assistantTurn.Id));
    }
    break;
```

`VisiblePlanningTraceExtractor` 必须是保守状态机：

| 状态 | 含义 | 行为 |
|------|------|------|
| `None` | 未检测到 marker | 正常透传 content |
| `Buffering` | 检测到开头 `---THINKING---`，等待 `---ACTION---` | 暂存 token，不写入 bubble |
| `Completed` | 已完成抽取 | `---ACTION---` 后内容透传 |
| `Invalid` | marker 不合法或疑似代码/引用 | 原样透传，停止抽取 |

严格抽取规则：

- 只抽取 assistant 回复开头的规划块，允许前置空白。
- 必须同时包含 `---THINKING---` 和 `---ACTION---`。
- `---ACTION---` 后内容为空时保持 `Buffering`，等待后续 token。
- marker 位于 Markdown code fence 内时不抽取。
- marker 出现在解释文本、引用、代码示例中时不抽取。
- 解析不确定时进入 `Invalid`，原样透传，优先保护最终回复。

### 5.4 Reasoning 完成检测

Structured Reasoning 完成时机：

- 收到第一个有效 `ContentToken`。
- 收到 `ToolCallDelta`。
- 收到 `Done`。

Visible Planning Trace 完成时机：

- `VisiblePlanningTraceExtractor` 检测到完整 `---THINKING--- ... ---ACTION---`。
- 或流结束时仍处于 `Buffering`，则标记 invalid 并原样回填到 content。

```csharp
private void CompleteReasoningIfNeeded(ConversationTurn assistantTurn)
{
    if (_reasoningCompleted || string.IsNullOrEmpty(assistantTurn.Reasoning)) return;

    _reasoningCompleted = true;
    assistantTurn.ReasoningDurationMs = _reasoningStopwatch.Elapsed.TotalMilliseconds;
    EmitEvent(AgentEvent.ReasoningCompleted(assistantTurn.ReasoningDurationMs, assistantTurn.Id));
}
```

### 5.5 HandleFinalResponse 修改

文件：`Editor/Core/AgentLoop.Runner.cs`

`HandleFinalResponse()` 必须保证最终写入 `_messages` 的 assistant content 已清洗：

```csharp
private void HandleFinalResponse(ChatMessage assistantMessage, ConversationTurn assistantTurn)
{
    assistantTurn.IsStreaming = false;

    var finalContent = assistantMessage?.Content ?? assistantTurn.Content;
    var cleaned = _visiblePlanningTraceExtractor.FinalizeContent(finalContent);

    if (!string.IsNullOrEmpty(cleaned.Reasoning) && string.IsNullOrEmpty(assistantTurn.Reasoning))
    {
        assistantTurn.Reasoning = cleaned.Reasoning;
        assistantTurn.ReasoningSource = ThinkingTraceSource.VisiblePlanningTrace;
    }

    assistantTurn.RawAssistantContent = finalContent;
    assistantTurn.Content = cleaned.Content;

    CompleteReasoningIfNeeded(assistantTurn);

    if (string.IsNullOrEmpty(assistantTurn.Content))
    {
        assistantTurn.Content = "[系统提示] 助手未返回任何内容。";
    }

    _messages.Add(ChatMessage.Assistant(assistantTurn.Content));
    EmitEvent(AgentEvent.AssistantMessage(assistantTurn.Content, assistantTurn.Id));
}
```

约束：

- 不得把 `RawAssistantContent` 写入 `_messages`。
- 不得把 `Reasoning` 拼回 `Content`。
- 若解析失败，优先保留原始 content 到最终气泡，而不是误删。

---

## 6. 事件层变更

### 6.1 新增 AgentEventType

```csharp
/// <summary>收到一个 reasoning/thinking token。</summary>
ReasoningToken,

/// <summary>Reasoning 阶段结束。</summary>
ReasoningCompleted,
```

### 6.2 新增 AgentEvent 工厂方法

```csharp
public static AgentEvent ReasoningToken(
    string content,
    string messageId,
    ThinkingTraceSource source) => ...

public static AgentEvent ReasoningCompleted(
    double durationMs,
    string messageId,
    ThinkingTraceSource source) => ...
```

### 6.3 AgentEvent 新增属性

```csharp
/// <summary>Reasoning 耗时毫秒（ReasoningCompleted 时有值）。</summary>
public double ReasoningDurationMs { get; }

/// <summary>ThinkingDrawer 内容来源。</summary>
public ThinkingTraceSource ReasoningSource { get; }
```

---

## 7. UI 层变更

### 7.1 ThinkingDrawer 组件

文件：`Editor/UI/Components/ThinkingDrawer.cs`

#### 职责

- 显示折叠/展开的 reasoning/planning trace 内容区域。
- 标题栏显示读秒计时（思考中） / 完成时长（思考完成）。
- 可选显示来源标签：Structured / Planning。
- 惰性渲染：折叠时清空 label，展开时 set text。

#### 结构

```
ThinkingDrawer : VisualElement
├── _header : VisualElement (可点击)
│   ├── _arrowLabel : Label ("▸" / "▾")
│   ├── _titleLabel : Label ("思考中 · Ns" / "思考完成 · Xs")
│   └── _sourceLabel : Label ("Structured" / "Planning")
└── _content : VisualElement (display: None 默认)
    └── _textLabel : Label (reasoning 文本)
```

#### 关键行为

```csharp
public class ThinkingDrawer : VisualElement
{
    private string _fullReasoning = "";
    private bool _isExpanded = false;
    private bool _isComplete = false;
    private IVisualElementScheduledItem _timerAnimation;
    private int _elapsedSeconds = 0;

    /// <summary>追加 reasoning 文本（流式，不更新 UI label）。</summary>
    public void AppendReasoning(string token, ThinkingTraceSource source)
    {
        _fullReasoning += token;
        SetSource(source);
        StartTimerIfNeeded();
    }

    /// <summary>标记 reasoning 完成，停止计时。</summary>
    public void MarkCompleted(double durationMs)
    {
        _isComplete = true;
        StopTimer();
        _titleLabel.text = $"思考完成 · {durationMs / 1000.0:F1}s";
    }

    /// <summary>恢复已完成状态（会话恢复时使用）。</summary>
    public void SetRestoredContent(string reasoning, double durationMs, ThinkingTraceSource source)
    {
        _fullReasoning = reasoning ?? "";
        _isComplete = true;
        SetSource(source);
        _titleLabel.text = $"思考完成 · {durationMs / 1000.0:F1}s";
    }
}
```

### 7.2 AssistantTurnView 容器

当前 `MessageListManager.AddItem()` 只支持追加。为了稳定保证顺序，不建议在全局消息列表中事后插入 ThinkingDrawer。

新增 assistant 回合容器：

```csharp
public class AssistantTurnView : VisualElement
{
    public ThinkingDrawer ThinkingDrawer { get; }
    public ToolCallGroup ToolGroup { get; }
    public MessageBubble MessageBubble { get; }
}
```

容器内部顺序固定：

```
1. ThinkingDrawer
2. ToolCallGroup
3. MessageBubble
```

规则：

- `ChatWindow` 每个 assistant turn 只向 `MessageListManager` 添加一个 `AssistantTurnView`。
- ThinkingDrawer、ToolCallGroup、MessageBubble 都挂在该容器内部。
- `EnsureAssistantBubbleExists()` 不再直接向根列表追加 bubble，而是确保当前 `AssistantTurnView` 存在。
- `RebuildMessageBubbles()` 恢复 assistant turn 时按容器内部顺序重建。

### 7.3 ChatWindow.Events.cs 变更

```csharp
case AgentEventType.ReasoningToken:
    HandleReasoningToken(evt.Content, evt.MessageId, evt.ReasoningSource);
    break;

case AgentEventType.ReasoningCompleted:
    HandleReasoningCompleted(evt.ReasoningDurationMs, evt.MessageId);
    break;
```

### 7.4 ChatWindow.Messages.cs 变更

```csharp
private readonly Dictionary<string, AssistantTurnView> _assistantTurnViews = new();

private AssistantTurnView GetOrCreateAssistantTurnView(string messageId)
{
    if (_assistantTurnViews.TryGetValue(messageId, out var view)) return view;

    view = new AssistantTurnView(messageId);
    _assistantTurnViews[messageId] = view;
    _messageListManager?.AddItem(view);
    return view;
}

private void HandleReasoningToken(string token, string messageId, ThinkingTraceSource source)
{
    if (string.IsNullOrEmpty(messageId)) return;

    var view = GetOrCreateAssistantTurnView(messageId);
    view.ThinkingDrawer.AppendReasoning(token, source);
}

private void HandleReasoningCompleted(double durationMs, string messageId)
{
    if (!_assistantTurnViews.TryGetValue(messageId, out var view)) return;
    view.ThinkingDrawer.MarkCompleted(durationMs);
}
```

### 7.5 RebuildMessageBubbles 中的恢复

```csharp
else if (turn.Role == "assistant")
{
    var view = GetOrCreateAssistantTurnView(turn.Id);

    if (!string.IsNullOrEmpty(turn.Reasoning))
    {
        view.ThinkingDrawer.SetRestoredContent(
            turn.Reasoning,
            turn.ReasoningDurationMs,
            turn.ReasoningSource);
    }

    view.RestoreToolCalls(turn.ToolCalls);
    view.RestoreMessageBubble(turn.Content);
}
```

---

## 8. 视觉层级与排序

一个完整 assistant 回合的 UI 元素排列顺序（从上到下）：

```
1. ThinkingDrawer  （视觉权重最低，浅灰、小字、默认折叠）
2. ToolCallGroup   （中等权重，深色卡片、默认折叠）
3. MessageBubble   （最高权重，标准对话气泡样式）
```

实现约束：

- 该顺序由 `AssistantTurnView` 容器保证，不依赖 `MessageListManager` 的插入能力。
- 会话恢复、Domain Reload 恢复、正常流式输出必须使用同一容器路径。
- 不允许在同一 assistant turn 中把 drawer、tool group、bubble 分散添加到根列表。

---

## 9. 降级与冲突策略

### 9.1 无 reasoning / planning trace

当 provider 不返回 structured reasoning，且普通 content 中没有合法 visible planning marker 时：

- 不创建 ThinkingDrawer。
- ContentToken 正常进入 MessageBubble。
- `_messages` 行为与当前一致。
- 性能开销仅为轻量 marker 检测。

### 9.2 有 Visible Planning Trace

当普通 content 中存在合法 `---THINKING--- ... ---ACTION---`：

- thinking 段进入 ThinkingDrawer。
- action 段进入 MessageBubble 和 `_messages`。
- marker 本身不显示在最终回复气泡中。

### 9.3 marker 不完整或不可信

以下场景不抽取，原样显示：

- 只有 `---THINKING---`，流结束仍没有 `---ACTION---`。
- marker 位于 Markdown code fence 中。
- marker 出现在引用、说明文本、代码示例中。
- parser 无法确定 marker 是否为真实规划块。

### 9.4 Structured Reasoning 与 Visible Planning Trace 同时存在

- Structured Reasoning 优先作为 ThinkingDrawer 内容。
- Visible Planning Trace 默认只用于清洗最终 content，避免重复展示。
- 如果两者内容不同且需要保留，`ReasoningSource = Mixed`，drawer 内用来源分隔符区分。

---

## 10. 涉及文件清单

### 新建文件

| 文件 | 说明 |
|------|------|
| `Editor/LLM/ReasoningFieldExtractor.cs` | Structured Reasoning 字段兼容提取 |
| `Editor/Core/VisiblePlanningTraceExtractor.cs` | 可见规划链流式解析与最终清洗 |
| `Editor/UI/Components/ThinkingDrawer.cs` | ThinkingDrawer UI 组件 |
| `Editor/UI/Components/AssistantTurnView.cs` | assistant 回合容器，保证 drawer/tool/bubble 顺序 |
| `Editor/UI/Components/ThinkingDrawer.uss` | ThinkingDrawer 样式（可选，也可合并到 `ChatWindow.uss`） |

### 修改文件

| 文件 | 变更内容 |
|------|----------|
| `Editor/LLM/ChatCompletionModels.cs` | `StreamChunkType` 加 `ReasoningToken`；`StreamChunk` 加 `ReasoningContent` 与工厂方法 |
| `Editor/LLM/StreamingResponseParser.cs` | raw `JObject` 调用 `ReasoningFieldExtractor`，再执行现有强类型解析 |
| `Editor/LLM/OpenAICompatibleClient.cs` | 流式累积时支持 reasoning 与 cleaned final content，不把 planning trace 写回最终 message |
| `Editor/Core/MessageTypes.cs` | `ConversationTurn` 增加 reasoning/source/duration/raw/state；`AgentEventType` 和 `AgentEvent` 增加 reasoning 事件 |
| `Editor/Core/AgentLoop.cs` | 新增 reasoning 状态、stopwatch、visible planning extractor 字段 |
| `Editor/Core/AgentLoop.LLM.cs` | 处理 `ReasoningToken` 与 content token 中的 visible planning trace |
| `Editor/Core/AgentLoop.Runner.cs` | `HandleFinalResponse` 写入 cleaned content；避免 raw/reasoning 进入 `_messages` |
| `Editor/Core/AgentLoop.DomainReload.cs` | 保存/恢复 reasoning、source、planning buffer、planning state |
| `Editor/Core/DomainReloadState.cs` | 增加 partial reasoning/source/buffer/state/duration 字段 |
| `Editor/Session/SessionData.cs` | `SerializableConversationTurn` 序列化新增 reasoning 相关字段 |
| `Editor/UI/ChatWindow.Events.cs` | 处理 `ReasoningToken` / `ReasoningCompleted` 事件 |
| `Editor/UI/ChatWindow.Messages.cs` | 使用 `AssistantTurnView` 管理 drawer/tool/bubble；重建恢复顺序 |
| `Editor/UI/Components/VirtualizedMessageListView.cs` | 不强制修改；若不使用 `AssistantTurnView`，才需要插入/重排能力 |
| `Editor/UI/ChatWindow.uss` | ThinkingDrawer / AssistantTurnView 样式 |

---

## 11. 版本号

本次变更为新增功能（UI 增强），按 SemVer 应升 Minor 版本。具体版本号在实现前对齐确认。

---

## 12. 验收标准

1. 使用 Qwen3-VL 发送会触发 structured reasoning 的消息，ThinkingDrawer 正确显示并读秒。
2. reasoning 结束后计时停止，标题显示 `思考完成 · Xs`。
3. 点击展开可查看完整 reasoning 文本，折叠后清空 label，再展开内容不丢失。
4. Claude Opus / Claude Sonnet 输出 `---THINKING--- ... ---ACTION---` 时，ThinkingDrawer 显示 thinking 段，最终气泡只显示 action 段。
5. GPT-5.5 若输出合法 visible planning marker，则显示 ThinkingDrawer；若没有 marker，则不显示 ThinkingDrawer。
6. Markdown code fence 内的 `---THINKING---` / `---ACTION---` 不被抽取。
7. marker 不完整时不抽取，原始内容进入最终气泡，避免误删。
8. 多轮工具调用后，reasoning/planning trace 内容正确追加并带轮次分隔。
9. Domain Reload 后 ThinkingDrawer 正确恢复；如果 reload 发生在 planning block 中间，不污染最终气泡。
10. 会话切换后 ThinkingDrawer、ToolCallGroup、MessageBubble 按固定顺序重建。
11. `_messages` 中的 assistant content 不包含 structured reasoning 或 visible planning thinking 段。
12. 性能：折叠状态下 reasoning 文本不在 DOM 中渲染。
