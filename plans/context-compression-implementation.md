# AgentCore 上下文压缩系统实施文档

> **文档类型**: 技术实施指南 | **版本**: v1.0 | **日期**: 2026-05-14  
> **目标版本**: v0.5.0 | **优先级**: P0（最高优先级）  
> **前置文档**: [`context-compression-system-plan.md`](context-compression-system-plan.md) — 系统设计方案

---

## 文档目的

本文档是 **AgentCore 上下文压缩系统的开发执行手册**，面向实际编码实现。基于竞品调研（Cursor、Cline、Roo Code）提炼的核心技术，结合 AgentCore 的 Unity Editor 场景特点，提供可直接执行的实施方案。

---

## 1. 竞品上下文压缩技术精炼

### 1.1 Cursor 的核心技术

#### 技术 1: Prompt Caching（Anthropic）

**原理**：
- 将频繁重复的大块上下文（System Prompt、工具定义）缓存到 Anthropic 服务器
- 后续请求只需传输 cache_id，大幅降低成本和延迟

**性能数据**：
| 场景 | 缓存内容 | 延迟降低 | 成本降低 |
|------|---------|---------|---------|
| 100K tokens 静态内容 | 完整文档 | -79% | -90% |
| 10K tokens 示例集 | Few-shot examples | -31% | -86% |
| 长 System Prompt | SOUL + TOOLS | -75% | -53% |

**定价**：
- Cache Write: 基础价格 × 1.25
- Cache Read: 基础价格 × 0.1

**AgentCore 应用**：
```csharp
// Phase 2 实现
public class PromptCachingManager
{
    // 缓存 System Prompt（SOUL + TOOLS + PROJECT）
    private string _cachedSystemPromptId;
    
    public async Task<ChatCompletionRequest> PrepareRequest(List<Message> history)
    {
        // 1. 检查 System Prompt 是否变化
        string currentSystemPrompt = BuildSystemPrompt();
        
        if (_cachedSystemPromptId == null || SystemPromptChanged())
        {
            // 2. 标记为可缓存（Anthropic API 支持）
            return new ChatCompletionRequest
            {
                System = new SystemMessage 
                { 
                    Content = currentSystemPrompt,
                    CacheControl = new CacheControl { Type = "ephemeral" } // 缓存 5 分钟
                },
                Messages = history
            };
        }
        else
        {
            // 3. 使用缓存（自动，无需额外代码）
            return new ChatCompletionRequest
            {
                System = new SystemMessage { Content = currentSystemPrompt },
                Messages = history
            };
        }
    }
}
```

**实施要点**：
- ✅ 缓存 System Prompt（~3000 tokens）
- ✅ 缓存工具定义（~2000 tokens）
- ❌ 不缓存对话历史（每轮都变化）
- ⚠️ 缓存有效期 5 分钟，需要处理过期

---

#### 技术 2: 动态上下文发现

**原理**：
- 不预先加载所有上下文，而是让模型通过工具调用按需获取
- 减少静态上下文占用，提高 token 利用率

**Cursor 的演进**：
```
早期（2024）:
  System Prompt: 5K tokens
  + 文件夹布局: 2K tokens
  + 语义匹配代码: 10K tokens
  + 用户附加文件: 20K tokens
  = 总计 37K tokens（静态）

现在（2026）:
  System Prompt: 3K tokens
  + 当前文件: 1K tokens
  + Git 状态: 0.5K tokens
  = 总计 4.5K tokens（静态）
  
  其他上下文通过工具动态获取:
  - read_file
  - search_codebase
  - get_git_diff
```

**AgentCore 应用**：
```csharp
// 当前已有的动态上下文工具
// Editor/Tools/Native/Core/SceneAnalysisTool.cs
// Editor/Tools/FileSystem/ManageFileTool.cs
// Editor/Tools/Native/Scripting/ManageScriptTool.cs

// Phase 6.2 增强：添加更多动态上下文工具
// - search_scripts: 语义搜索脚本
// - get_dependencies: 获取依赖关系
// - find_references: 查找引用
```

**实施要点**：
- ✅ 保持 System Prompt 精简（< 3000 tokens）
- ✅ 不在 System Prompt 中包含项目文件列表
- ✅ 通过工具让 Agent 自主决定需要什么上下文
- ⚠️ 需要高质量的工具设计和描述

---

### 1.2 通用压缩技术

#### 技术 3: LLM 摘要压缩

**原理**：
- 使用便宜快速的 LLM（如 Claude Haiku）对长内容生成摘要
- 保留语义信息，大幅减少 token 数

**成本分析**：
```
Claude Haiku 定价: $0.25/M input, $1.25/M output

压缩 1000 tokens 的工具结果:
  Input: 1000 tokens × $0.25/M = $0.00025
  Output: 200 tokens × $1.25/M = $0.00025
  总成本: $0.0005

对比主 LLM 调用（Claude Opus）:
  1000 tokens × $15/M = $0.015
  
压缩成本占比: 0.0005 / 0.015 = 3.3%
```

**AgentCore 实现**：
```csharp
// Editor/Core/Compression/ToolResultCompressor.cs
public class ToolResultCompressor
{
    private readonly ILLMClient _haikuClient; // Claude Haiku
    private const int CompressionThreshold = 1000;
    private const int TargetTokens = 200;
    
    public async Task<ToolResult> CompressIfNeeded(ToolResult result, CancellationToken ct)
    {
        int tokens = TokenCounter.EstimateTokens(result.Content);
        
        if (tokens <= CompressionThreshold)
            return result; // 小结果不压缩
        
        // 使用 Haiku 生成摘要
        string summary = await SummarizeWithHaiku(result, ct);
        
        return new ToolResult
        {
            ToolName = result.ToolName,
            Content = summary,
            IsSuccess = result.IsSuccess,
            Metadata = new Dictionary<string, object>
            {
                ["original_tokens"] = tokens,
                ["compressed_tokens"] = TokenCounter.EstimateTokens(summary),
                ["is_compressed"] = true
            }
        };
    }
    
    private async Task<string> SummarizeWithHaiku(ToolResult result, CancellationToken ct)
    {
        // 针对不同工具类型使用不同的摘要 prompt
        string prompt = result.ToolName switch
        {
            "read_file" => BuildFileReadSummaryPrompt(result),
            "list_gameobjects" => BuildListSummaryPrompt(result),
            "get_component_info" => BuildComponentSummaryPrompt(result),
            _ => BuildGenericSummaryPrompt(result)
        };
        
        var response = await _haikuClient.CallAsync(
            new List<Message> { new Message { Role = "user", Content = prompt } },
            maxTokens: TargetTokens,
            ct
        );
        
        return response.Content;
    }
    
    private string BuildFileReadSummaryPrompt(ToolResult result)
    {
        return $@"Summarize this C# script in {TargetTokens} tokens or less.
Focus on:
1. Class name and inheritance
2. Public fields and properties
3. Key methods (name + purpose, no implementation details)
4. Important comments or TODOs

Original content:
{result.Content}

Summary (focus on API surface, omit implementation):";
    }
    
    private string BuildListSummaryPrompt(ToolResult result)
    {
        return $@"Summarize this GameObject list in {TargetTokens} tokens or less.
Format: 'Found X objects: [first 5 names], ... (Y more)'
Include total count and representative samples.

Original list:
{result.Content}

Summary:";
    }
}
```

**实施要点**：
- ✅ 使用 Claude Haiku（成本低、速度快）
- ✅ 针对不同工具类型定制摘要 prompt
- ✅ 保留关键信息（类名、方法签名、总数统计）
- ✅ 在 Metadata 中记录压缩比
- ⚠️ 摘要质量需要人工验证

---

#### 技术 4: 滑动窗口 + 摘要混合策略

**原理**：
- 保留最近 N 轮完整对话（滑动窗口）
- 对更早的对话生成摘要
- 平衡信息保留和 token 节省

**实现**：
```csharp
// Editor/Core/Compression/ConversationCompressor.cs
public class ConversationCompressor
{
    private const int RecentRoundsToKeep = 5; // 保留最近 5 轮
    private readonly ILLMClient _haikuClient;
    
    public async Task<List<Message>> CompressHistory(
        List<Message> allMessages, 
        int targetTokens,
        CancellationToken ct)
    {
        // 1. 分离最近对话和历史对话
        int recentMessageCount = RecentRoundsToKeep * 2; // 每轮 = user + assistant
        var recentMessages = allMessages.TakeLast(recentMessageCount).ToList();
        var olderMessages = allMessages.SkipLast(recentMessageCount).ToList();
        
        if (olderMessages.Count == 0)
            return recentMessages; // 没有历史，直接返回
        
        // 2. 对历史对话生成摘要
        string summary = await SummarizeConversation(olderMessages, ct);
        
        // 3. 构建压缩后的消息列表
        var compressed = new List<Message>
        {
            new Message 
            { 
                Role = "system", 
                Content = $@"[Earlier Conversation Summary]
{summary}

The above is a summary of earlier conversation. Recent messages follow below.
[End of Summary]"
            }
        };
        compressed.AddRange(recentMessages);
        
        return compressed;
    }
    
    private async Task<string> SummarizeConversation(
        List<Message> messages, 
        CancellationToken ct)
    {
        var conversationText = string.Join("\n\n", 
            messages.Select(m => $"{m.Role.ToUpper()}: {m.Content}"));
        
        var prompt = $@"Summarize the following conversation in 500 tokens or less.

Focus on:
1. User's goals and requests
2. Actions taken by the assistant (file edits, tool calls)
3. Problems encountered and how they were resolved
4. Current state of the work

Conversation:
{conversationText}

Summary (be concise but preserve key decisions):";

        var response = await _haikuClient.CallAsync(
            new List<Message> { new Message { Role = "user", Content = prompt } },
            maxTokens: 500,
            ct
        );
        
        return response.Content;
    }
}
```

**实施要点**：
- ✅ 保留最近 5 轮完整对话（用户可配置）
- ✅ 摘要插入为 system 消息（不影响对话流）
- ✅ 摘要 prompt 强调保留关键决策
- ⚠️ 摘要长度需要控制（< 500 tokens）

---

#### 技术 5: 上下文预算管理

**原理**：
- 动态计算当前上下文使用情况
- 根据使用率选择压缩策略
- 避免超出上下文限制

**实现**：
```csharp
// Editor/Core/Compression/ContextBudgetManager.cs
public class ContextBudgetManager
{
    // Claude Opus 上下文窗口
    private const int MaxContextTokens = 200000;
    
    // 预算分配
    private const int ReservedForResponse = 4096;
    private const int SystemPromptBudget = 3000;
    private const int ToolDefinitionsBudget = 2000;
    private const int SafetyMargin = 1000;
    
    public ContextBudget CalculateBudget(
        string systemPrompt,
        List<ToolDefinition> tools,
        List<Message> history)
    {
        int systemTokens = TokenCounter.EstimateTokens(systemPrompt);
        int toolsTokens = tools.Sum(t => TokenCounter.EstimateTokens(t.ToJson()));
        int historyTokens = history.Sum(m => TokenCounter.EstimateTokens(m.Content));
        int totalUsed = systemTokens + toolsTokens + historyTokens;
        
        return new ContextBudget
        {
            MaxTokens = MaxContextTokens,
            SystemPromptTokens = systemTokens,
            ToolDefinitionsTokens = toolsTokens,
            HistoryTokens = historyTokens,
            TotalUsed = totalUsed,
            Available = MaxContextTokens - totalUsed - ReservedForResponse,
            UsagePercentage = (double)totalUsed / MaxContextTokens,
            NeedsCompression = totalUsed > (MaxContextTokens - ReservedForResponse - SafetyMargin),
            CompressionUrgency = CalculateUrgency(totalUsed)
        };
    }
    
    private CompressionUrgency CalculateUrgency(int totalTokens)
    {
        double usage = (double)totalTokens / MaxContextTokens;
        
        return usage switch
        {
            < 0.5 => CompressionUrgency.None,    // < 50%: 不压缩
            < 0.7 => CompressionUrgency.Low,     // 50-70%: 轻度压缩（滑动窗口）
            < 0.85 => CompressionUrgency.Medium, // 70-85%: 中度压缩（摘要）
            _ => CompressionUrgency.High         // > 85%: 重度压缩（摘要 + 工具结果）
        };
    }
}

public class ContextBudget
{
    public int MaxTokens { get; set; }
    public int SystemPromptTokens { get; set; }
    public int ToolDefinitionsTokens { get; set; }
    public int HistoryTokens { get; set; }
    public int TotalUsed { get; set; }
    public int Available { get; set; }
    public double UsagePercentage { get; set; }
    public bool NeedsCompression { get; set; }
    public CompressionUrgency CompressionUrgency { get; set; }
}

public enum CompressionUrgency
{
    None,    // < 50%
    Low,     // 50-70%
    Medium,  // 70-85%
    High     // > 85%
}
```

**实施要点**：
- ✅ 实时计算上下文使用率
- ✅ 根据紧急程度选择压缩策略
- ✅ 预留响应空间（4096 tokens）
- ✅ 安全边际（1000 tokens）

---

## 2. AgentCore 实施方案

### 2.1 文件结构

```
Editor/
├── Core/
│   ├── Compression/                          # 新增目录
│   │   ├── ContextBudgetManager.cs          # 预算管理
│   │   ├── ContextCompressionManager.cs     # 压缩协调器
│   │   ├── ToolResultCompressor.cs          # 工具结果压缩
│   │   ├── ConversationCompressor.cs        # 对话历史压缩
│   │   ├── ICompressionStrategy.cs          # 压缩策略接口
│   │   ├── SlidingWindowStrategy.cs         # 滑动窗口策略
│   │   ├── SummaryCompressionStrategy.cs    # 摘要压缩策略
│   │   └── PromptCachingManager.cs          # Prompt Caching（Phase 2）
│   ├── AgentLoop.cs                          # 修改：集成压缩
│   ├── AgentLoop.LLM.cs                      # 修改：调用压缩
│   └── AgentLoop.Tools.cs                    # 修改：工具结果压缩
├── Config/
│   └── AgentCoreSettings.cs                  # 修改：添加压缩配置
└── UI/
    ├── ChatWindow.cs                          # 修改：显示上下文指示器
    └── Components/
        └── ContextUsageIndicator.cs          # 新增：上下文使用可视化
```

### 2.2 实施阶段

#### Phase 1: 工具结果压缩（v0.5.0-alpha.1）

**目标**：解决最紧急的问题 — 大型工具结果占用过多 tokens

**实施步骤**：

1. **创建 ToolResultCompressor.cs**
```csharp
namespace AgentCore.Editor.Core.Compression
{
    public class ToolResultCompressor
    {
        private readonly ILLMClient _haikuClient;
        private const int CompressionThreshold = 1000;
        private const int TargetTokens = 200;
        
        public ToolResultCompressor(ILLMClient haikuClient)
        {
            _haikuClient = haikuClient;
        }
        
        public async Task<ToolResult> CompressIfNeeded(ToolResult result, CancellationToken ct)
        {
            // 实现见上文 "技术 3"
        }
    }
}
```

2. **修改 AgentLoop.Tools.cs**
```csharp
// 在 ExecuteToolCallsAsync 中添加压缩逻辑
private async Task<List<Message>> ExecuteToolCallsAsync(
    List<ToolCall> toolCalls, 
    CancellationToken ct)
{
    var results = new List<Message>();
    
    foreach (var toolCall in toolCalls)
    {
        var result = await _toolDispatcher.ExecuteAsync(toolCall, ct);
        
        // 新增：压缩大型工具结果
        if (_settings.EnableToolResultCompression)
        {
            result = await _toolResultCompressor.CompressIfNeeded(result, ct);
        }
        
        results.Add(new Message
        {
            Role = "tool",
            Content = result.Content,
            ToolCallId = toolCall.Id
        });
    }
    
    return results;
}
```

3. **添加配置项到 AgentCoreSettings.cs**
```csharp
[Header("Context Compression")]
public bool EnableToolResultCompression = true;
public int ToolCompressionThreshold = 1000; // tokens
public int ToolCompressionTargetTokens = 200;
```

4. **单元测试**
```csharp
// Editor/Tests/Core/ToolResultCompressorTests.cs
[Test]
public async Task CompressLargeFileRead()
{
    var largeContent = GenerateLargeScript(500); // 500 行 ≈ 1500 tokens
    var result = new ToolResult
    {
        ToolName = "read_file",
        Content = largeContent,
        IsSuccess = true
    };
    
    var compressed = await _compressor.CompressIfNeeded(result, CancellationToken.None);
    
    int originalTokens = TokenCounter.EstimateTokens(result.Content);
    int compressedTokens = TokenCounter.EstimateTokens(compressed.Content);
    
    Assert.Greater(originalTokens, 1000);
    Assert.Less(compressedTokens, 300);
    Assert.Greater((double)compressedTokens / originalTokens, 0.8); // 压缩比 > 80%
}
```

**验收标准**：
- ✅ `read_file` 返回 500 行文件时，压缩到 < 200 tokens
- ✅ `list_gameobjects` 返回 200 个对象时，压缩到 < 150 tokens
- ✅ 压缩不影响对话质量（手动测试 5 个场景）
- ✅ 压缩延迟 < 500ms

---

#### Phase 2: 对话历史压缩（v0.5.0-alpha.2）

**目标**：支持 20+ 轮长对话

**实施步骤**：

1. **创建压缩策略接口**
```csharp
// Editor/Core/Compression/ICompressionStrategy.cs
public interface ICompressionStrategy
{
    Task<List<Message>> CompressAsync(
        List<Message> messages, 
        int targetTokens, 
        CancellationToken ct);
    
    string Name { get; }
}
```

2. **实现滑动窗口策略**
```csharp
// Editor/Core/Compression/SlidingWindowStrategy.cs
public class SlidingWindowStrategy : ICompressionStrategy
{
    public string Name => "SlidingWindow";
    
    public Task<List<Message>> CompressAsync(
        List<Message> messages, 
        int targetTokens, 
        CancellationToken ct)
    {
        // 实现见上文 "技术 4"
    }
}
```

3. **实现摘要压缩策略**
```csharp
// Editor/Core/Compression/SummaryCompressionStrategy.cs
public class SummaryCompressionStrategy : ICompressionStrategy
{
    private readonly ILLMClient _haikuClient;
    private const int RecentRoundsToKeep = 5;
    
    public string Name => "Summary";
    
    public async Task<List<Message>> CompressAsync(
        List<Message> messages, 
        int targetTokens, 
        CancellationToken ct)
    {
        // 实现见上文 "技术 4"
    }
}
```

4. **创建 ConversationCompressor**
```csharp
// Editor/Core/Compression/ConversationCompressor.cs
public class ConversationCompressor
{
    private readonly Dictionary<string, ICompressionStrategy> _strategies;
    
    public ConversationCompressor(ILLMClient haikuClient)
    {
        _strategies = new Dictionary<string, ICompressionStrategy>
        {
            ["sliding_window"] = new SlidingWindowStrategy(),
            ["summary"] = new SummaryCompressionStrategy(haikuClient)
        };
    }
    
    public async Task<List<Message>> CompressAsync(
        List<Message> messages,
        string strategyName,
        int targetTokens,
        CancellationToken ct)
    {
        if (!_strategies.TryGetValue(strategyName, out var strategy))
            strategy = _strategies["sliding_window"]; // 默认策略
        
        return await strategy.CompressAsync(messages, targetTokens, ct);
    }
}
```

5. **修改 AgentLoop.LLM.cs**
```csharp
private async Task<string> CallLLMStreamAsync(
    List<Message> history, 
    CancellationToken ct)
{
    // 新增：压缩对话历史
    if (_settings.EnableConversationCompression)
    {
        var budget = _budgetManager.CalculateBudget(_systemPrompt, _tools, history);
        
        if (budget.NeedsCompression)
        {
            string strategy = budget.CompressionUrgency switch
            {
                CompressionUrgency.Low => "sliding_window",
                CompressionUrgency.Medium => "summary",
                CompressionUrgency.High => "summary",
                _ => "sliding_window"
            };
            
            history = await _conversationCompressor.CompressAsync(
                history, 
                strategy, 
                budget.Available, 
                ct);
            
            // 发送压缩通知事件
            EmitEvent(AgentEvent.CompressionApplied(strategy, budget));
        }
    }
    
    // 原有的 LLM 调用逻辑
    var response = await _llmClient.CallStreamAsync(history, ct);
    return response;
}
```

**验收标准**：
- ✅ 20 轮对话后仍能正常工作
- ✅ 摘要保留关键信息（用户目标、执行结果、错误信息）
- ✅ 压缩比 > 80%
- ✅ 压缩后对话质量不下降（人工评估）

---

#### Phase 3: 上下文预算管理（v0.5.0-alpha.3）

**目标**：智能触发压缩

**实施步骤**：

1. **创建 ContextBudgetManager.cs**（见上文 "技术 5"）

2. **创建 ContextCompressionManager.cs**
```csharp
// Editor/Core/Compression/ContextCompressionManager.cs
public class ContextCompressionManager
{
    private readonly ToolResultCompressor _toolCompressor;
    private readonly ConversationCompressor _conversationCompressor;
    private readonly ContextBudgetManager _budgetManager;
    
    public async Task<CompressionResult> PrepareContextForLLM(
        string systemPrompt,
        List<ToolDefinition> tools,
        List<Message> rawHistory,
        CancellationToken ct)
    {
        // 1. 计算当前预算
        var budget = _budgetManager.CalculateBudget(systemPrompt, tools, rawHistory);
        
        // 2. 如果不需要压缩，直接返回
        if (!budget.NeedsCompression)
        {
            return new CompressionResult
            {
                Messages = rawHistory,
                Budget = budget,
                WasCompressed = false
            };
        }
        
        // 3. 根据紧急程度选择压缩策略
        string strategy = budget.CompressionUrgency switch
        {
            CompressionUrgency.Low => "sliding_window",
            CompressionUrgency.Medium => "summary",
            CompressionUrgency.High => "summary",
            _ => "sliding_window"
        };
        
        // 4. 执行压缩
        var compressed = await _conversationCompressor.CompressAsync(
            rawHistory, 
            strategy, 
            budget.Available, 
            ct);
        
        // 5. 重新计算预算
        var newBudget = _budgetManager.CalculateBudget(systemPrompt, tools, compressed);
        
        return new CompressionResult
        {
            Messages = compressed,
            Budget = newBudget,
            WasCompressed = true,
            Strategy = strategy,
            OriginalTokens = budget.HistoryTokens,
            CompressedTokens = newBudget.HistoryTokens
        };
    }
}

public class CompressionResult
{
    public List<Message> Messages { get; set; }
    public ContextBudget Budget { get; set; }
    public bool WasCompressed { get; set; }
    public string Strategy { get; set; }
    public int OriginalTokens { get; set; }
    public int CompressedTokens { get; set; }
}
```

3. **集成到 AgentLoop**
```csharp
// Editor/Core/AgentLoop.cs
public class AgentLoop
{
    private ContextCompressionManager _compressionManager;
    
    public void Initialize()
    {
        // 初始化压缩管理器
        var haikuClient = CreateHaikuClient();
        _compressionManager = new ContextCompressionManager(
            new ToolResultCompressor(haikuClient),
            new ConversationCompressor(haikuClient),
            new ContextBudgetManager()
        );
    }
    
    private ILLMClient CreateHaikuClient()
    {
        return new OpenAICompatibleClient(
            apiKey: _settings.AnthropicApiKey,
            baseUrl: "https://api.anthropic.com/v1",
            model: "claude-3-haiku-20240307"
        );
    }
}
```

**验收标准**：
- ✅ 上下文使用率 < 85% 时不触发压缩
- ✅ 上下文使用率 > 85% 时自动压缩
- ✅ 压缩后上下文使用率降至 < 70%
- ✅ 压缩策略选择正确（Low → 滑动窗口，Medium/High → 摘要）

---

#### Phase 4: Prompt Caching（v0.5.0-beta.1）

**目标**：降低成本和延迟

**实施步骤**：

1. **创建 PromptCachingManager.cs**（见上文 "技术 1"）

2. **修改 OpenAICompatibleClient.cs**
```csharp
// Editor/LLM/OpenAICompatibleClient.cs
public class OpenAICompatibleClient : ILLMClient
{
    public async Task<ChatCompletionResponse> CallAsync(
        List<Message> messages,
        int maxTokens,
        CancellationToken ct)
    {
        var request = new
        {
            model = _model,
            messages = messages.Select(m => new
            {
                role = m.Role,
                content = m.Content,
                // Anthropic Prompt Caching 支持
                cache_control = m.CacheControl != null ? new
                {
                    type = m.CacheControl.Type
                } : null
            }),
            max_tokens = maxTokens
        };
        
        // 发送请求...
    }
}
```

3. **修改 Message 类**
```csharp
// Editor/Core/MessageTypes.cs
public class Message
{
    public string Role { get; set; }
    public string Content { get; set; }
    public string ToolCallId { get; set; }
    public List<ToolCall> ToolCalls { get; set; }
    
    // 新增：Prompt Caching 支持
    public CacheControl CacheControl { get; set; }
}

public class CacheControl
{
    public string Type { get; set; } // "ephemeral"
}
```

4. **在 AgentLoop 中启用缓存**
```csharp
private async Task<string> CallLLMStreamAsync(
    List<Message> history, 
    CancellationToken ct)
{
    // 标记 System Prompt 为可缓存
    var systemMessage = new Message
    {
        Role = "system",
        Content = _systemPrompt,
        CacheControl = new CacheControl { Type = "ephemeral" }
    };
    
    var allMessages = new List<Message> { systemMessage };
    allMessages.AddRange(history);
    
    var response = await _llmClient.CallStreamAsync(allMessages, ct);
    return response;
}
```

**验收标准**：
- ✅ System Prompt 被缓存（查看 API 响应头）
- ✅ 缓存命中时延迟降低 > 50%
- ✅ 缓存命中时成本降低 > 50%
- ✅ 缓存过期后自动重建

---

#### Phase 5: UI 可视化（v0.5.0-beta.2）

**目标**：用户可见的压缩状态

**实施步骤**：

1. **创建 ContextUsageIndicator.cs**
```csharp
// Editor/UI/Components/ContextUsageIndicator.cs
public class ContextUsageIndicator : VisualElement
{
    private ProgressBar _usageBar;
    private Label _usageLabel;
    private Label _detailsLabel;
    
    public ContextUsageIndicator()
    {
        AddToClassList("context-usage-indicator");
        
        _usageBar = new ProgressBar();
        _usageBar.title = "Context Usage";
        Add(_usageBar);
        
        _usageLabel = new Label();
        Add(_usageLabel);
        
        _detailsLabel = new Label();
        _detailsLabel.AddToClassList("details");
        Add(_detailsLabel);
    }
    
    public void UpdateBudget(ContextBudget budget)
    {
        float percentage = (float)budget.UsagePercentage;
        _usageBar.value = percentage * 100;
        
        // 颜色编码
        _usageBar.RemoveFromClassList("normal");
        _usageBar.RemoveFromClassList("warning");
        _usageBar.RemoveFromClassList("danger");
        
        if (percentage < 0.5f)
            _usageBar.AddToClassList("normal");
        else if (percentage < 0.85f)
            _usageBar.AddToClassList("warning");
        else
            _usageBar.AddToClassList("danger");
        
        _usageLabel.text = $"{budget.TotalUsed:N0} / {budget.MaxTokens:N0} tokens ({percentage:P0})";
        _detailsLabel.text = $"System: {budget.SystemPromptTokens:N0} | Tools: {budget.ToolDefinitionsTokens:N0} | History: {budget.HistoryTokens:N0}";
    }
}
```

2. **修改 ChatWindow.cs**
```csharp
// Editor/UI/ChatWindow.cs
public class ChatWindow : EditorWindow
{
    private ContextUsageIndicator _contextIndicator;
    
    private void CreateGUI()
    {
        // 添加上下文指示器
        _contextIndicator = new ContextUsageIndicator();
        rootVisualElement.Add(_contextIndicator);
        
        // 其他 UI 元素...
    }
    
    private void OnContextBudgetUpdated(ContextBudget budget)
    {
        _contextIndicator.UpdateBudget(budget);
    }
    
    private void OnCompressionApplied(CompressionResult result)
    {
        // 显示压缩通知
        var notification = new VisualElement();
        notification.AddToClassList("compression-notification");
        
        var icon = new Label("ℹ️");
        notification.Add(icon);
        
        var message = new Label($"Context compressed using {result.Strategy} strategy");
        notification.Add(message);
        
        var stats = new Label($"{result.OriginalTokens:N0} → {result.CompressedTokens:N0} tokens ({(1 - (double)result.CompressedTokens / result.OriginalTokens):P1} reduction)");
        notification.Add(stats);
        
        _messageContainer.Add(notification);
    }
}
```

3. **添加 USS 样式**
```css
/* Editor/UI/ChatWindow.uss */
.context-usage-indicator {
    padding: 8px;
    background-color: rgba(0, 0, 0, 0.1);
    border-bottom: 1px solid rgba(0, 0, 0, 0.2);
}

.context-usage-indicator .unity-progress-bar.normal {
    --unity-progress-bar-fill-color: #4CAF50;
}

.context-usage-indicator .unity-progress-bar.warning {
    --unity-progress-bar-fill-color: #FF9800;
}

.context-usage-indicator .unity-progress-bar.danger {
    --unity-progress-bar-fill-color: #F44336;
}

.compression-notification {
    padding: 12px;
    margin: 8px 0;
    background-color: rgba(33, 150, 243, 0.1);
    border-left: 4px solid #2196F3;
    flex-direction: row;
    align-items: center;
}
```

**验收标准**：
- ✅ 上下文使用条实时更新
- ✅ 颜色编码正确（绿/黄/橙/红）
- ✅ 压缩发生时显示通知
- ✅ 通知包含压缩策略和统计信息

---

## 3. 测试计划

### 3.1 单元测试

```csharp
// Editor/Tests/Core/Compression/
├── ToolResultCompressorTests.cs
│   ├── CompressLargeFileRead()
│   ├── CompressLargeList()
│   ├── SkipSmallResults()
│   └── PreserveKeyInformation()
├── ConversationCompressorTests.cs
│   ├── SlidingWindowStrategy_KeepsRecentMessages()
│   ├── SummaryStrategy_PreservesKeyDecisions()
│   └── SummaryStrategy_CompressesOlderMessages()
├── ContextBudgetManagerTests.cs
│   ├── CalculateBudget_Accurate()
│   ├── CompressionUrgency_CorrectThresholds()
│   └── AvailableTokens_CorrectCalculation()
└── ContextCompressionManagerTests.cs
    ├── PrepareContext_NoCompressionWhenUnderThreshold()
    ├── PrepareContext_SelectsCorrectStrategy()
    └── PrepareContext_ReducesTokensEffectively()
```

### 3.2 集成测试

**场景 1: 长对话测试**
```
1. 启动 AgentCore
2. 进行 25 轮对话（每轮包含工具调用）
3. 验证：
   - 对话仍能正常进行
   - 上下文使用率 < 90%
   - 至少触发 2 次压缩
   - 压缩后对话质量不下降
```

**场景 2: 大型工具结果测试**
```
1. 调用 read_file 读取 1000 行脚本
2. 验证：
   - 工具结果被压缩
   - 压缩后 < 300 tokens
   - 摘要包含类名、方法签名
   - Agent 仍能理解文件内容
```

**场景 3: Domain Reload 测试**
```
1. 进行 10 轮对话（触发压缩）
2. 修改脚本触发 Domain Reload
3. 验证：
   - 对话历史恢复
   - 压缩状态恢复
   - 上下文预算正确
```

### 3.3 性能测试

| 指标 | 目标 | 测量方法 |
|------|------|---------|
| 工具结果压缩延迟 | < 500ms | Stopwatch 计时 |
| 对话历史压缩延迟 | < 1000ms | Stopwatch 计时 |
| 压缩成本占比 | < 5% | 累计 Haiku 调用成本 / 主 LLM 成本 |
| 压缩比 | > 80% | (原始 tokens - 压缩后 tokens) / 原始 tokens |
| 最大对话轮数 | 20+ 轮 | 手动测试 |

---

## 4. 配置项

### 4.1 AgentCoreSettings 新增字段

```csharp
[Header("Context Compression")]
[Tooltip("Enable automatic context compression")]
public bool EnableContextCompression = true;

[Tooltip("Enable tool result compression")]
public bool EnableToolResultCompression = true;

[Tooltip("Tool result compression threshold (tokens)")]
public int ToolCompressionThreshold = 1000;

[Tooltip("Tool result compression target (tokens)")]
public int ToolCompressionTargetTokens = 200;

[Tooltip("Enable conversation history compression")]
public bool EnableConversationCompression = true;

[Tooltip("Number of recent conversation rounds to keep in full")]
public int RecentRoundsToKeep = 5;

[Tooltip("Show compression notifications in chat")]
public bool ShowCompressionNotifications = true;

[Tooltip("Enable Anthropic Prompt Caching")]
public bool EnablePromptCaching = true;

[Tooltip("Compression strategy (SlidingWindow, Summary, Auto)")]
public CompressionStrategyType DefaultCompressionStrategy = CompressionStrategyType.Auto;

public enum CompressionStrategyType
{
    SlidingWindow,
    Summary,
    Auto // 根据上下文使用率自动选择
}
```

---

## 5. 风险与缓解

| 风险 | 影响 | 缓解措施 |
|------|------|---------|
| **摘要质量差** | 高 | • 使用 Claude Haiku（质量高）<br>• 针对不同工具类型定制 prompt<br>• 保留最近 N 轮完整对话<br>• 人工验证摘要质量 |
| **压缩成本高** | 中 | • 使用 Haiku（$0.25/M）<br>• 只压缩超过阈值的内容<br>• 监控成本占比（< 5%） |
| **压缩延迟** | 中 | • 异步压缩<br>• 显示加载指示器<br>• 优化 prompt 长度<br>• 缓存摘要结果 |
| **信息丢失** | 中 | • 保留最近对话<br>• 提供"查看原始内容"选项<br>• 用户可禁用压缩 |
| **Prompt Caching 失效** | 低 | • 缓存过期后自动重建<br>• 降级到无缓存模式 |
| **Domain Reload 兼容性** | 低 | • 压缩状态存储到 `DomainReloadState`<br>• 恢复时重新计算预算 |

---

## 6. 成功指标

| 指标 | 当前 | 目标 (v0.5.0) | 测量方法 |
|------|------|---------------|---------|
| **最大对话轮数** | ~10 轮 | 20+ 轮 | 手动测试长对话 |
| **上下文使用率** | 经常 > 90% | 保持 < 85% | 监控日志 |
| **压缩比** | N/A | > 80% | 压缩前后 token 数对比 |
| **压缩延迟** | N/A | < 500ms（工具）<br>< 1000ms（对话） | 性能测试 |
| **压缩成本占比** | N/A | < 5% | 成本监控 |
| **用户满意度** | N/A | > 4/5 | 用户反馈调查 |

---

## 7. 参考实现

### 7.1 Anthropic Prompt Caching 示例

```python
# Python 示例（参考）
import anthropic

client = anthropic.Anthropic()

response = client.messages.create(
    model="claude-3-5-sonnet-20241022",
    max_tokens=1024,
    system=[
        {
            "type": "text",
            "text": "You are an AI assistant for Unity development...",
        },
        {
            "type": "text", 
            "text": "Here are the available tools: ...",
            "cache_control": {"type": "ephemeral"}  # 缓存工具定义
        }
    ],
    messages=[
        {"role": "user", "content": "Help me create a PlayerController"}
    ]
)
```

### 7.2 LangChain ConversationSummaryMemory 参考

```python
# Python 示例（参考）
from langchain.memory import ConversationSummaryMemory
from langchain.llms import Anthropic

memory = ConversationSummaryMemory(
    llm=Anthropic(model="claude-3-haiku-20240307"),
    max_token_limit=500
)

# 自动摘要旧对话
memory.save_context(
    {"input": "User's question"},
    {"output": "Assistant's response"}
)

# 获取压缩后的历史
compressed_history = memory.load_memory_variables({})
```

---

## 8. 后续优化方向（Phase 6.1+）

### 8.1 语义聚类压缩

- 使用嵌入向量对消息进行语义聚类
- 每个聚类保留一条代表性消息
- 适用于跨多个主题的长对话

### 8.2 增量压缩

- 不是每次都重新压缩整个历史
- 只压缩新增的消息
- 缓存已压缩的摘要

### 8.3 模式特定压缩策略

- Architect Mode：保留更多设计讨论
- Review Mode：保留更多代码片段
- Debug Mode：保留更多错误信息

### 8.4 压缩质量评估

- 使用 LLM 评估摘要质量
- 自动调整压缩参数
- A/B 测试不同压缩策略

---

## 9. 附录

### 9.1 Token 估算公式

```csharp
// 基于 tiktoken 的估算（cl100k_base）
public static class TokenCounter
{
    private const double CharsPerToken = 4.0; // 平均值
    
    public static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        
        // 简单估算：字符数 / 4
        // 实际实现应使用 tiktoken 库
        return (int)Math.Ceiling(text.Length / CharsPerToken);
    }
}
```

### 9.2 压缩 Prompt 模板

**文件读取摘要**：
```
Summarize this C# script in 200 tokens or less.

Focus on:
1. Class name and inheritance
2. Public fields and properties (name + type)
3. Key methods (name + parameters + return type, NO implementation)
4. Important comments or TODOs

Omit:
- Private methods
- Method implementations
- Using statements
- Namespace declarations

Original content:
{file_content}

Summary (API surface only):
```

**列表摘要**：
```
Summarize this list in 150 tokens or less.

Format: "Found {total_count} items: {first_5_names}, ... ({remaining_count} more)"

Original list:
{list_content}

Summary:
```

**对话摘要**：
```
Summarize the following conversation in 500 tokens or less.

Focus on:
1. User's goals and requests
2. Actions taken by the assistant (file edits, tool calls)
3. Problems encountered and how they were resolved
4. Current state of the work

Omit:
- Detailed code snippets
- Verbose explanations
- Repeated information

Conversation:
{conversation_text}

Summary (key decisions and outcomes):
```

---

**文档维护**: 本文件应在 v0.5.0 开发过程中持续更新，记录实际实施中的发现和调整。
