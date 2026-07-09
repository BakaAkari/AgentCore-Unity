# AgentCore 上下文压缩系统设计方案

> **文档状态**: 设计方案 | **版本**: v1.0 | **日期**: 2026-05-13  
> **目标版本**: v0.5.0 | **优先级**: P0（最高优先级）  
> **关联文档**: [`ai-coding-assistants-analysis.md`](ai-coding-assistants-analysis.md) — 竞品分析与技术选型

---

## 1. 问题定义

### 1.1 当前痛点

**场景重现**：
```
用户: "帮我重构 PlayerController"
  → Agent 调用 read_file 读取 PlayerController.cs (500 行 ≈ 1500 tokens)
  → Agent 调用 list_gameobjects 获取场景对象 (200 个 ≈ 800 tokens)
  → Agent 调用 get_component_info 分析组件 (10 个组件 ≈ 600 tokens)
  → 当前上下文: ~3000 tokens

用户: "再优化一下移动逻辑"
  → 上一轮的所有工具结果仍在上下文中
  → 新的工具调用继续追加
  → 5 轮对话后上下文可能达到 20K+ tokens
  → 接近 Claude 的有效上下文限制（实际可用约 100K tokens）
```

**核心问题**：
1. **工具结果冗余**：大型工具结果（如文件内容、列表）包含大量不必要的细节
2. **历史对话累积**：中间步骤的完整对话不需要永久保留
3. **无压缩机制**：当前只有滑动窗口截断，会丢失重要上下文
4. **性能下降**：长上下文导致 LLM 响应变慢、成本增加、质量下降

### 1.2 目标与非目标

**目标**：
-  支持 20+ 轮对话而不超出上下文限制
-  保留关键信息，压缩冗余内容
-  用户无感知的自动压缩
-  可视化上下文使用情况

**非目标**：
-  不追求 100% 信息保留（允许合理的信息损失）
-  不支持无限长对话（仍有上限，但大幅提升）
-  不实现复杂的语义理解（依赖 LLM 摘要）

---

## 2. 架构设计

### 2.1 系统架构图

```
┌─────────────────────────────────────────────────────────────┐
│                      AgentLoop                              │
│                                                             │
│  ┌──────────────────────────────────────────────────────┐  │
│  │         ContextCompressionManager                    │  │
│  │  ┌────────────────┐  ┌────────────────┐            │  │
│  │  │ToolResult      │  │Conversation    │            │  │
│  │  │Compressor      │  │Compressor      │            │  │
│  │  └────────────────┘  └────────────────┘            │  │
│  │           │                   │                      │  │
│  │           └───────┬───────────┘                      │  │
│  │                   ▼                                  │  │
│  │         ┌────────────────────┐                       │  │
│  │         │ContextBudget       │                       │  │
│  │         │Manager             │                       │  │
│  │         └────────────────────┘                       │  │
│  └──────────────────────────────────────────────────────┘  │
│                          │                                  │
│                          ▼                                  │
│              ┌──────────────────────┐                       │
│              │ LLM Client           │                       │
│              └──────────────────────┘                       │
└─────────────────────────────────────────────────────────────┘
```

### 2.2 核心组件

| 组件 | 职责 | 输入 | 输出 |
|------|------|------|------|
| **ToolResultCompressor** | 压缩大型工具结果 | ToolResult | 压缩后的 ToolResult |
| **ConversationCompressor** | 压缩对话历史 | List\<Message\> | 压缩后的 List\<Message\> |
| **ContextBudgetManager** | 管理 token 预算 | 当前上下文 | 是否需要压缩 + 预算分配 |
| **CompressionStrategy** | 压缩策略接口 | 原始内容 | 压缩后内容 |

---

## 3. 详细设计

### 3.1 ToolResultCompressor — 工具结果压缩

#### 3.1.1 压缩触发条件

```csharp
public class ToolResultCompressor
{
    private const int CompressionThreshold = 1000; // tokens
    private const int TargetTokens = 200;          // 压缩目标
    
    public async Task<ToolResult> CompressIfNeeded(ToolResult result, CancellationToken ct)
    {
        int tokens = TokenCounter.EstimateTokens(result.Content);
        
        if (tokens <= CompressionThreshold)
            return result; // 小结果不压缩
        
        // 调用 LLM 生成摘要
        string summary = await SummarizeWithLLM(result, TargetTokens, ct);
        
        return new ToolResult
        {
            ToolName = result.ToolName,
            Content = summary,
            IsSuccess = result.IsSuccess,
            ExecutionTime = result.ExecutionTime,
            Metadata = new Dictionary<string, object>
            {
                ["original_tokens"] = tokens,
                ["compressed_tokens"] = TokenCounter.EstimateTokens(summary),
                ["compression_ratio"] = (double)TokenCounter.EstimateTokens(summary) / tokens,
                ["is_compressed"] = true
            }
        };
    }
    
    private async Task<string> SummarizeWithLLM(ToolResult result, int targetTokens, CancellationToken ct)
    {
        var prompt = $@"Summarize the following tool execution result in {targetTokens} tokens or less.
Focus on the most important information that would be useful for continuing the conversation.

Tool: {result.ToolName}
Original content:
{result.Content}

Summary:";

        // 使用 Claude Haiku（成本低、速度快）
        var response = await _llmClient.CallAsync(new List<Message>
        {
            new Message { Role = "user", Content = prompt }
        }, maxTokens: targetTokens, ct);
        
        return response.Content;
    }
}
```

#### 3.1.2 压缩策略

| 工具类型 | 压缩策略 | 示例 |
|---------|---------|------|
| **文件读取** | 保留类签名、方法签名、关键注释；省略方法体 | `read_file` → 只保留 public API |
| **列表查询** | 保留前 N 项 + 总数统计 | `list_gameobjects` → 前 10 个 + "共 200 个" |
| **组件信息** | 保留类型和关键属性；省略默认值 | `get_component_info` → 只保留非默认属性 |
| **执行结果** | 保留成功/失败状态 + 错误信息 | `execute_command` → 只保留 exit code + stderr |

### 3.2 ConversationCompressor — 对话历史压缩

#### 3.2.1 压缩策略接口

```csharp
public interface ICompressionStrategy
{
    Task<List<Message>> CompressAsync(List<Message> messages, int targetTokens, CancellationToken ct);
    string Name { get; }
}
```

#### 3.2.2 策略 1: 滑动窗口（当前已有）

```csharp
public class SlidingWindowStrategy : ICompressionStrategy
{
    public string Name => "SlidingWindow";
    
    public Task<List<Message>> CompressAsync(List<Message> messages, int targetTokens, CancellationToken ct)
    {
        var result = new List<Message>();
        int currentTokens = 0;
        
        // 从最新消息开始，逆向添加
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            int messageTokens = TokenCounter.EstimateTokens(messages[i].Content);
            if (currentTokens + messageTokens > targetTokens)
                break;
            
            result.Insert(0, messages[i]);
            currentTokens += messageTokens;
        }
        
        return Task.FromResult(result);
    }
}
```

#### 3.2.3 策略 2: 摘要压缩（新增）

```csharp
public class SummaryCompressionStrategy : ICompressionStrategy
{
    public string Name => "Summary";
    private const int RecentRounds = 5; // 保留最近 5 轮完整对话
    
    public async Task<List<Message>> CompressAsync(List<Message> messages, int targetTokens, CancellationToken ct)
    {
        // 保留最近 N 轮完整对话
        int recentMessageCount = RecentRounds * 2; // 每轮 = user + assistant
        var recentMessages = messages.TakeLast(recentMessageCount).ToList();
        var olderMessages = messages.SkipLast(recentMessageCount).ToList();
        
        if (olderMessages.Count == 0)
            return recentMessages;
        
        // 对更早的消息生成摘要
        string summary = await SummarizeConversation(olderMessages, ct);
        
        var result = new List<Message>
        {
            new Message 
            { 
                Role = "system", 
                Content = $"[Earlier conversation summary]\n{summary}\n[End of summary]" 
            }
        };
        result.AddRange(recentMessages);
        
        return result;
    }
    
    private async Task<string> SummarizeConversation(List<Message> messages, CancellationToken ct)
    {
        var conversationText = string.Join("\n\n", messages.Select(m => $"{m.Role}: {m.Content}"));
        
        var prompt = $@"Summarize the following conversation in 500 tokens or less.
Focus on:
1. What the user wanted to accomplish
2. What actions were taken
3. What problems were encountered
4. What was the final outcome

Conversation:
{conversationText}

Summary:";

        var response = await _llmClient.CallAsync(new List<Message>
        {
            new Message { Role = "user", Content = prompt }
        }, maxTokens: 500, ct);
        
        return response.Content;
    }
}
```

#### 3.2.4 策略 3: 语义聚类（Phase 6.1）

```csharp
public class SemanticClusteringStrategy : ICompressionStrategy
{
    public string Name => "SemanticClustering";
    
    public async Task<List<Message>> CompressAsync(List<Message> messages, int targetTokens, CancellationToken ct)
    {
        // 1. 对每条消息生成嵌入向量
        var embeddings = await GenerateEmbeddings(messages, ct);
        
        // 2. 使用 K-means 聚类（按主题分组）
        var clusters = ClusterMessages(embeddings, numClusters: 5);
        
        // 3. 每个聚类保留一条代表性消息
        var representatives = SelectRepresentatives(clusters);
        
        // 4. 保留最近的完整对话
        var recentMessages = messages.TakeLast(10).ToList();
        
        return representatives.Concat(recentMessages).ToList();
    }
}
```

### 3.3 ContextBudgetManager — 上下文预算管理

#### 3.3.1 Token 预算分配

```csharp
public class ContextBudgetManager
{
    // Claude Opus 上下文窗口
    private const int MaxContextTokens = 200000;
    
    // 预算分配
    private const int ReservedForResponse = 4096;      // 为响应预留
    private const int SystemPromptBudget = 3000;       // System Prompt（SOUL + TOOLS + PROJECT）
    private const int ToolDefinitionsBudget = 2000;    // 工具定义
    private const int SafetyMargin = 1000;             // 安全边际
    
    public int AvailableForHistory => MaxContextTokens 
        - ReservedForResponse 
        - SystemPromptBudget 
        - ToolDefinitionsBudget 
        - SafetyMargin;
    
    public ContextBudget CalculateBudget(List<Message> messages)
    {
        int systemPromptTokens = EstimateSystemPromptTokens();
        int toolDefinitionsTokens = EstimateToolDefinitionsTokens();
        int historyTokens = messages.Sum(m => TokenCounter.EstimateTokens(m.Content));
        int totalTokens = systemPromptTokens + toolDefinitionsTokens + historyTokens;
        
        return new ContextBudget
        {
            MaxTokens = MaxContextTokens,
            SystemPromptTokens = systemPromptTokens,
            ToolDefinitionsTokens = toolDefinitionsTokens,
            HistoryTokens = historyTokens,
            TotalUsed = totalTokens,
            Available = MaxContextTokens - totalTokens - ReservedForResponse,
            NeedsCompression = totalTokens > (MaxContextTokens - ReservedForResponse - SafetyMargin),
            CompressionUrgency = CalculateUrgency(totalTokens)
        };
    }
    
    private CompressionUrgency CalculateUrgency(int totalTokens)
    {
        double usage = (double)totalTokens / MaxContextTokens;
        
        if (usage < 0.5) return CompressionUrgency.None;
        if (usage < 0.7) return CompressionUrgency.Low;
        if (usage < 0.85) return CompressionUrgency.Medium;
        return CompressionUrgency.High;
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
    public bool NeedsCompression { get; set; }
    public CompressionUrgency CompressionUrgency { get; set; }
}

public enum CompressionUrgency
{
    None,    // < 50% 使用
    Low,     // 50-70% 使用
    Medium,  // 70-85% 使用
    High     // > 85% 使用
}
```

#### 3.3.2 自动压缩触发

```csharp
public class ContextCompressionManager
{
    private readonly ToolResultCompressor _toolCompressor;
    private readonly ConversationCompressor _conversationCompressor;
    private readonly ContextBudgetManager _budgetManager;
    
    public async Task<List<Message>> PrepareContextForLLM(
        List<Message> rawHistory, 
        CancellationToken ct)
    {
        // 1. 计算当前预算
        var budget = _budgetManager.CalculateBudget(rawHistory);
        
        // 2. 如果不需要压缩，直接返回
        if (!budget.NeedsCompression)
            return rawHistory;
        
        // 3. 根据紧急程度选择压缩策略
        ICompressionStrategy strategy = budget.CompressionUrgency switch
        {
            CompressionUrgency.Low => new SlidingWindowStrategy(),
            CompressionUrgency.Medium => new SummaryCompressionStrategy(),
            CompressionUrgency.High => new SummaryCompressionStrategy(), // Phase 6.1 可升级为 SemanticClustering
            _ => new SlidingWindowStrategy()
        };
        
        // 4. 执行压缩
        var compressed = await strategy.CompressAsync(rawHistory, budget.Available, ct);
        
        // 5. 记录压缩统计
        LogCompressionStats(rawHistory, compressed, strategy.Name);
        
        return compressed;
    }
    
    private void LogCompressionStats(List<Message> original, List<Message> compressed, string strategyName)
    {
        int originalTokens = original.Sum(m => TokenCounter.EstimateTokens(m.Content));
        int compressedTokens = compressed.Sum(m => TokenCounter.EstimateTokens(m.Content));
        double ratio = (double)compressedTokens / originalTokens;
        
        Debug.Log($"[ContextCompression] Strategy: {strategyName}, " +
                  $"Original: {originalTokens} tokens, " +
                  $"Compressed: {compressedTokens} tokens, " +
                  $"Ratio: {ratio:P1}");
    }
}
```

---

## 4. UI 可视化

### 4.1 上下文使用指示器

在 Chat 窗口顶部添加上下文使用条：

```
┌─────────────────────────────────────────────────────────┐
│  AgentCore Chat                                         │
│  ┌───────────────────────────────────────────────────┐  │
│  │ Context: ████████░░░░░░░░░░ 45% (90K/200K tokens)│  │
│  │ [System: 3K] [Tools: 2K] [History: 85K]          │  │
│  └───────────────────────────────────────────────────┘  │
│                                                         │
│  [User message input...]                                │
└─────────────────────────────────────────────────────────┘
```

**颜色编码**：
- 绿色（< 50%）：正常
- 黄色（50-70%）：注意
- 橙色（70-85%）：警告
- 红色（> 85%）：紧急

### 4.2 压缩通知

当自动压缩发生时，在消息流中插入通知卡片：

```
┌─────────────────────────────────────────────────────────┐
│   Context Compression Applied                         │
│  Strategy: Summary Compression                          │
│  Compressed 15 messages (45K tokens) → 1 summary (500)  │
│  Compression ratio: 98.9%                               │
│  [View Details] [Disable Auto-Compression]              │
└─────────────────────────────────────────────────────────┘
```

### 4.3 Settings 配置

在 `AgentCoreSettings` 中添加压缩配置：

```csharp
[Header("Context Compression")]
public bool EnableAutoCompression = true;
public CompressionStrategy DefaultStrategy = CompressionStrategy.Summary;
public int CompressionThreshold = 1000; // tokens
public int RecentRoundsToKeep = 5;
public bool ShowCompressionNotifications = true;
```

---

## 5. 实施计划

### 5.1 Phase 1 — 工具结果压缩（v0.5.0-alpha.1）

**目标**：解决最紧急的问题 — 大型工具结果

**任务清单**：
- [ ] 实现 `ToolResultCompressor`
- [ ] 集成到 `AgentLoop.Tools.cs` 的 `ExecuteToolCallsAsync`
- [ ] 添加压缩统计日志
- [ ] 单元测试（压缩前后 token 数对比）

**验收标准**：
- `read_file` 返回 500 行文件时，压缩到 < 200 tokens
- `list_gameobjects` 返回 200 个对象时，压缩到 < 150 tokens
- 压缩不影响对话质量（手动测试）

### 5.2 Phase 2 — 对话历史压缩（v0.5.0-alpha.2）

**目标**：支持长对话

**任务清单**：
- [ ] 实现 `ICompressionStrategy` 接口
- [ ] 实现 `SlidingWindowStrategy`（重构现有逻辑）
- [ ] 实现 `SummaryCompressionStrategy`
- [ ] 实现 `ConversationCompressor`
- [ ] 集成到 `AgentLoop.LLM.cs` 的 `CallLLMStreamAsync`
- [ ] 单元测试（摘要质量评估）

**验收标准**：
- 20 轮对话后仍能正常工作
- 摘要保留关键信息（用户目标、执行结果）
- 压缩比 > 80%

### 5.3 Phase 3 — 上下文预算管理（v0.5.0-alpha.3）

**目标**：智能触发压缩

**任务清单**：
- [ ] 实现 `ContextBudgetManager`
- [ ] 实现 `ContextCompressionManager`
- [ ] 集成到 `AgentLoop` 主循环
- [ ] 添加压缩策略选择逻辑
- [ ] 单元测试（预算计算准确性）

**验收标准**：
- 上下文使用率 < 85% 时不触发压缩
- 上下文使用率 > 85% 时自动压缩
- 压缩后上下文使用率降至 < 70%

### 5.4 Phase 4 — UI 可视化（v0.5.0-beta.1）

**目标**：用户可见的压缩状态

**任务清单**：
- [ ] 实现上下文使用指示器（UI Toolkit）
- [ ] 实现压缩通知卡片
- [ ] 添加 Settings 配置项
- [ ] 添加"查看详情"弹窗（显示压缩前后对比）
- [ ] UI 测试（不同压缩状态的视觉效果）

**验收标准**：
- 上下文使用条实时更新
- 压缩发生时显示通知
- 用户可以禁用自动压缩

### 5.5 Phase 5 — 优化与测试（v0.5.0-rc.1）

**目标**：性能优化和稳定性

**任务清单**：
- [ ] 性能测试（压缩耗时 < 500ms）
- [ ] 成本分析（LLM 摘要调用成本）
- [ ] 边界测试（极长对话、极大工具结果）
- [ ] 用户测试（收集反馈）
- [ ] 文档更新（SOUL.md 中说明压缩机制）

**验收标准**：
- 压缩不影响用户体验（无明显延迟）
- 压缩成本 < 原始 LLM 调用成本的 5%
- 无崩溃、无数据丢失

---

## 6. 风险评估

| 风险 | 可能性 | 影响 | 缓解措施 |
|------|--------|------|---------|
| **摘要质量差** | 中 | 高 | 使用 Claude Haiku（质量高）；提供详细的摘要 prompt；保留最近 N 轮完整对话 |
| **压缩成本高** | 低 | 中 | 使用 Haiku（成本低）；只压缩超过阈值的内容；缓存摘要结果 |
| **压缩延迟** | 中 | 中 | 异步压缩；显示加载指示器；优化 prompt 长度 |
| **信息丢失** | 高 | 中 | 保留最近对话；提供"查看原始内容"选项；用户可禁用压缩 |
| **Domain Reload 兼容性** | 低 | 高 | 压缩状态存储到 `DomainReloadState`；恢复时重新计算预算 |

---

## 7. 成功指标

| 指标 | 当前 | 目标 (v0.5.0) | 测量方法 |
|------|------|---------------|---------|
| **最大对话轮数** | ~10 轮 | 20+ 轮 | 手动测试长对话 |
| **上下文使用率** | 经常 > 90% | 保持 < 85% | 监控日志 |
| **压缩比** | N/A | > 80% | 压缩前后 token 数对比 |
| **压缩延迟** | N/A | < 500ms | 性能测试 |
| **用户满意度** | N/A | > 4/5 | 用户反馈调查 |

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

~~- Architect Mode：保留更多设计讨论~~
~~- Review Mode：保留更多代码片段~~
~~- Debug Mode：保留更多错误信息~~

**更新 (2026-05-18)**: 模式系统已废弃（ADR-5）。上下文策略统一由 Agent 根据对话内容自动调整。

### 8.4 压缩质量评估

- 使用 LLM 评估摘要质量
- 自动调整压缩参数
- A/B 测试不同压缩策略

---

## 9. 竞品方案对比与技术选型

> 基于对 Cline、Roo Code、Cursor 的深入研究，本章节分析主流 AI 编码助手的上下文管理策略。

### 9.1 竞品上下文管理策略总览

| 产品 | 上下文窗口 | 核心策略 | 技术亮点 | 局限性 |
|------|-----------|---------|---------|--------|
| **Cursor** | 200K (Claude Opus) | 动态上下文发现 + Prompt Caching | • 从静态上下文转向动态拉取<br>• 使用 Anthropic Prompt Caching（90% 成本降低）<br>• 工具驱动的上下文获取 | • 依赖模型能力选择上下文<br>• 需要高质量的工具设计 |
| **Cline** | 200K (多模型支持) | Plan & Act 模式 + Checkpoints | • 双模式分离（Plan 探索 / Act 执行）<br>• Checkpoint 系统支持回滚<br>• 模块化 Skills 系统 | • 模式切换需要用户干预<br>• 上下文压缩机制不明确 |
| **Roo Code** | 200K (多模型支持) | ~~Mode 系统~~ + 自动批准 | • ~~5 种专业化 Mode（Architect/Code/Ask/Debug/Review）~~<br>• Orchestrator 协调多 Mode<br>• "Trade tokens for quality" 理念 | • 已于 2026-05-15 关闭<br>• 具体压缩实现未公开 |
| **AgentCore** | 200K (Claude Sonnet) | **LLM 摘要 + 预算管理** | • **工具结果摘要**（>1000 tokens 自动压缩）<br>• **对话历史压缩**（保留最近 N 轮）<br>• **动态预算分配**<br>• **无模式系统**（自主智能体） | • **已实现 v0.5.0 ~ v0.5.2**<br>• 聚焦自主能力而非模式切换 |
| **AgentCore** | 200K (Claude Opus) | **工具结果压缩 + 对话摘要**（本设计） | • 针对 Unity Editor 场景优化<br>• 双层压缩（工具 + 对话）<br>• 预算管理 + 可视化 | • 初版实现，待验证效果 |

### 9.2 Cursor 的动态上下文策略

#### 9.2.1 核心理念转变

Cursor 在 2024 年末到 2026 年初经历了重大架构演进：

**早期（2024）**：
- 静态上下文工程：预先加载文件夹布局、语义匹配代码片段、用户附加文件
- 大量护栏：自动修正文件读取、限制工具调用次数、强制注入 lint 错误
- 上下文窗口被大量静态信息占据

**现在（2026）**：
- **动态上下文发现**：模型自主决定何时拉取额外信息
- **工具驱动**：通过工具调用动态获取上下文（过去对话、活跃终端、相关工具）
- **最小静态上下文**：只保留操作系统、git 状态、当前文件等核心信息

> "We've adapted to increasing model capability by knocking down guardrails and providing more dynamic context."
> — Cursor Blog: [Continually improving agent harness](https://www.cursor.com/blog/continually-improving-agent-harness)

#### 9.2.2 Prompt Caching 技术

Cursor 大量使用 **Anthropic Prompt Caching**：

| 使用场景 | 缓存内容 | 延迟降低 | 成本降低 |
|---------|---------|---------|---------|
| **Chat with a book** (100K tokens) | 完整书籍内容 | -79% (11.5s → 2.4s) | -90% |
| **Many-shot prompting** (10K tokens) | 示例集合 | -31% (1.6s → 1.1s) | -86% |
| **Multi-turn conversation** (10 turns) | 长 System Prompt | -75% (~10s → ~2.5s) | -53% |

**定价模型**：
- Cache Write: 基础价格 × 1.25（写入缓存）
- Cache Read: 基础价格 × 0.1（读取缓存）
- 适用于频繁重复的大块上下文（System Prompt、工具定义、代码库摘要）

**AgentCore 可借鉴点**：
-  System Prompt（SOUL + TOOLS + PROJECT）适合缓存
-  工具定义（~2000 tokens）适合缓存
-  对话历史不适合缓存（每轮都变化）

#### 9.2.3 Composer 2 的 Token 效率

Cursor 的 Composer 2 模型在 CursorBench 上展示了卓越的 token 效率：

- **质量提升**：CursorBench 得分从 38.0 (Composer 1) → 61.3 (Composer 2)
- **成本优化**：$0.50/M input, $2.50/M output（比 Claude Opus 便宜 6-30 倍）
- **Fast 变体**：$1.50/M input, $7.50/M output（速度更快，成本仍低于其他快速模型）

**关键洞察**：
> "Better AI models enable more ambitious work"
> — 更好的模型可以处理更长的上下文，减少对压缩的依赖

### 9.3 Cline 的 Plan & Act 模式

#### 9.3.1 双模式架构

Cline 通过 **Plan Mode** 和 **Act Mode** 分离探索与执行：

```
Plan Mode（探索阶段）
  → 探索代码库
  → 提出澄清问题
  → 制定策略
  → 不执行实际修改

Act Mode（执行阶段）
  → 执行计划
  → 编辑文件
  → 运行命令
  → 每步需要用户批准
```

**上下文管理优势**：
- Plan Mode 可以"浪费" tokens 探索，不影响 Act Mode 的上下文预算
- 两个模式的上下文相对独立，避免交叉污染
- 用户可以在 Plan Mode 中多次迭代，确认后再进入 Act Mode

#### 9.3.2 Checkpoint 系统

Cline 的 Checkpoint 机制：
- 每次重要操作前自动创建 checkpoint
- 用户可以回滚代码变更，但**保留对话历史**
- 支持"实验性修改"而不担心破坏代码

**与上下文压缩的关系**：
- Checkpoint 不压缩对话历史，而是通过分支管理避免上下文污染
- 类似 Git 的分支策略，而非线性压缩

### 9.4 Roo Code 的 Mode 系统

#### 9.4.1 专业化 Mode

Roo Code 提供 5 种内置 Mode + 自定义 Mode：

| Mode | 职责 | 工具权限 | 上下文偏好 |
|------|------|---------|-----------|
| **Code** | 日常编码、编辑、文件操作 | 完整文件系统 + 终端 | 保留代码片段 |
| **Architect** | 系统设计、规范、迁移计划 | 只读文件系统 | 保留设计讨论 |
| **Ask** | 快速问答、解释、文档 | 只读 | 保留问题和答案 |
| **Debug** | 问题追踪、日志分析、根因定位 | 完整 + 调试工具 | 保留错误信息 |
| **Review** | 代码审查、质量检查 | 只读 + 静态分析 | 保留审查意见 |

**Orchestrator Mode**：
- 协调多个 Mode 协作
- 将大任务分解为子任务，分配给不同 Mode
- 类似 Multi-Agent 系统

#### 9.4.2 "Trade Tokens for Quality" 理念

Roo Code 的核心哲学：
> "Don't skimp on tokens: expensive state-of-the-art models with lots of tokens will almost always beat cheap models using few tokens."

**对 AgentCore 的启示**：
-  不要过度压缩，保留关键信息
-  使用高质量模型（Claude Opus）进行摘要
-  但仍需压缩机制，因为 Unity Editor 场景的工具结果特别大

### 9.5 技术选型对比

#### 9.5.1 压缩策略对比

| 策略 | 优势 | 劣势 | 适用场景 | AgentCore 采用 |
|------|------|------|---------|---------------|
| **滑动窗口** | 简单、快速、无成本 | 丢失早期上下文 | 短对话 |  Phase 1 |
| **LLM 摘要** | 保留语义、质量高 | 有成本、有延迟 | 长对话 |  Phase 2 |
| **语义聚类** | 智能分组、保留多样性 | 复杂、需要嵌入 | 跨主题对话 |  Phase 6.1 |
| **Prompt Caching** | 大幅降低成本和延迟 | 只适用于静态内容 | System Prompt |  Phase 2 |
| **动态上下文** | 按需加载、节省 tokens | 依赖模型能力 | 代码库探索 |  Phase 6.2 |

#### 9.5.2 LLM 选型对比

| 模型 | 输入价格 | 输出价格 | 速度 | 质量 | AgentCore 用途 |
|------|---------|---------|------|------|---------------|
| **Claude Opus** | $15/M | $75/M | 慢 | 最高 | 主对话 LLM |
| **Claude Sonnet 3.5** | $3/M | $15/M | 中 | 高 | 备选主 LLM |
| **Claude Haiku** | $0.25/M | $1.25/M | 快 | 中 | **压缩摘要**  |
| **Cursor Composer 2** | $0.50/M | $2.50/M | 中 | 高 | 不可用（闭源） |
| **GPT-4o** | $2.50/M | $10/M | 快 | 高 | 备选 |

**AgentCore 选择 Claude Haiku 用于压缩的原因**：
1. **成本低**：$0.25/M input，压缩 1000 tokens 只需 $0.00025
2. **速度快**：适合实时压缩，不阻塞用户体验
3. **质量足够**：摘要任务不需要 Opus 级别的推理能力
4. **同厂商**：与主 LLM 同为 Anthropic，API 兼容性好

#### 9.5.3 存储方案对比

| 方案 | 优势 | 劣势 | 竞品使用 | AgentCore 选择 |
|------|------|------|---------|---------------|
| **SQLite** | 本地、快速、零依赖 | 语义搜索弱 | Cursor（早期） |  Phase 1 |
| **Qdrant** | 语义搜索强、可扩展 | 需要外部服务 | Roo Code |  Phase 6.2（可选） |
| **LightRAG** | 已集成、支持语义搜索 | 功能有限 | - |  Phase 2（记忆系统） |
| **Chroma** | 开源、易用 | 需要 Python | - |  不适合 Unity |

**AgentCore 策略**：
- Phase 1-2：使用现有 `SessionStorage`（JSON 文件）+ `LightRAG`（记忆系统）
- Phase 6.2：如果需要高级语义搜索，考虑集成 Qdrant（可选）

### 9.6 AgentCore 的差异化策略

基于竞品分析，AgentCore 的独特定位：

| 维度 | 竞品通用做法 | AgentCore 差异化 |
|------|-------------|-----------------|
| **场景** | 通用代码编辑 | **Unity Editor 专用** |
| **工具结果** | 文本为主（代码、终端输出） | **大量结构化数据**（GameObject 列表、组件信息、材质属性） |
| **压缩重点** | 对话历史 | **工具结果 + 对话历史双重压缩** |
| **UI 集成** | 独立窗口 | **Editor 内嵌 + 可视化预算管理** |
| **Domain Reload** | 不存在（VSCode 无此问题） | **必须处理编译中断** |

**核心优势**：
1. **针对性优化**：专门为 Unity 工具结果设计压缩策略
2. **双层压缩**：工具结果 + 对话历史分别压缩，效果更好
3. **可视化**：实时显示上下文使用情况，用户可控
4. **Domain Reload 安全**：压缩状态可恢复

---

## 10. 参考资源

### 10.1 官方文档

- [Anthropic Prompt Caching](https://www.anthropic.com/news/prompt-caching) — 成本降低 90%，延迟降低 85%
- [Anthropic Context Window Best Practices](https://docs.anthropic.com/claude/docs/context-window)
- [OpenAI Token Optimization](https://platform.openai.com/docs/guides/prompt-engineering/strategy-split-complex-tasks-into-simpler-subtasks)

### 10.2 竞品技术博客

- [Cursor: Continually improving agent harness](https://www.cursor.com/blog/continually-improving-agent-harness) — 动态上下文演进
- [Cursor: Composer 2](https://www.cursor.com/blog/composer-2) — Token 效率优化
- [Cursor: CursorBench](https://www.cursor.com/blog/cursorbench) — 评估方法论
- [Cursor: Bootstrapping Composer with autoinstall](https://www.cursor.com/blog/bootstrapping-composer-with-autoinstall) — 环境设置自动化
- [Cline Documentation](https://docs.cline.bot/) — Plan & Act 模式、Checkpoints
- [Roo Code Documentation](https://docs.roocode.com/) — Mode 系统、Orchestrator

### 10.3 开源资源

- [LangChain ConversationSummaryMemory](https://python.langchain.com/docs/modules/memory/types/summary) — 对话摘要参考实现
- [Anthropic Cookbook: Prompt Caching](https://github.com/anthropics/anthropic-cookbook) — 实战示例

---

## 11. 附录

### 10.1 压缩示例

**原始工具结果**（1500 tokens）：
```json
{
  "tool": "read_file",
  "content": "using System;\nusing UnityEngine;\n\npublic class PlayerController : MonoBehaviour\n{\n    [SerializeField] private float moveSpeed = 5f;\n    [SerializeField] private float jumpForce = 10f;\n    private Rigidbody2D rb;\n    private bool isGrounded;\n    \n    void Start()\n    {\n        rb = GetComponent<Rigidbody2D>();\n    }\n    \n    void Update()\n    {\n        float horizontal = Input.GetAxis(\"Horizontal\");\n        rb.velocity = new Vector2(horizontal * moveSpeed, rb.velocity.y);\n        \n        if (Input.GetButtonDown(\"Jump\") && isGrounded)\n        {\n            rb.AddForce(Vector2.up * jumpForce, ForceMode2D.Impulse);\n        }\n    }\n    \n    void OnCollisionEnter2D(Collision2D collision)\n    {        if (collision.gameObject.CompareTag(\"Ground\"))\n        {\n            isGrounded = true;\n        }\n    }\n    \n    void OnCollisionExit2D(Collision2D collision)\n    {\n        if (collision.gameObject.CompareTag(\"Ground\"))\n        {\n            isGrounded = false;\n        }\n    }\n}\n"
}
```

**压缩后**（180 tokens）：
```json
{
  "tool": "read_file",
  "content": "[Compressed Summary]\nPlayerController.cs: MonoBehaviour with 2D movement and jump mechanics.\n\nKey components:\n- Fields: moveSpeed (5f), jumpForce (10f), Rigidbody2D, isGrounded flag\n- Update(): Handles horizontal movement via Input.GetAxis, jump on button press when grounded\n- Collision detection: Sets isGrounded based on \"Ground\" tag\n\nImplementation uses Rigidbody2D.velocity for movement and AddForce for jumping.\n[Original: 1500 tokens, Compressed: 180 tokens, Ratio: 88%]"
}
```

### 10.2 对话历史压缩示例

**原始对话**（10 轮，8000 tokens）：
```
User: 帮我创建一个 PlayerController
Assistant: [创建文件...]
User: 添加跳跃功能
Assistant: [修改文件...]
User: 跳跃力度太小了
Assistant: [调整参数...]
...（省略 6 轮）
```

**压缩后**（500 tokens）：
```
[Earlier conversation summary]
User requested creation of PlayerController with movement and jump mechanics.
Initial implementation completed with moveSpeed=5f and jumpForce=10f.
User reported jump force too weak, adjusted to 15f.
Collision detection added for ground check using "Ground" tag.
Final implementation tested and working correctly.
[End of summary]

[Recent 5 rounds kept in full...]
```

---

**文档维护**: 本文件应在 v0.5.0 开发过程中持续更新，记录实际实施中的发现和调整。
