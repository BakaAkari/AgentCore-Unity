# 上下文压缩策略可视化 — 详细设计文档

> **任务编号**: 6.0.4  
> **版本**: v0.5.2  
> **状态**: 设计中  
> **创建日期**: 2026-05-18  
> **关联**: ROADMAP.md Phase 6.0 — 上下文压缩与管理

---

## 1. 背景与目标

### 1.1 背景

v0.5.0 已实现上下文压缩系统的核心功能：
-  **工具结果压缩** (`ToolResultCompressor`) — 大型工具输出自动摘要
-  **对话历史压缩** (`ConversationCompressor`) — 旧对话段落摘要化
-  **上下文预算管理** (`ContextWindowManager`) — 动态 token 预算分配
-  **压缩统计** (`CompressionMetrics`) — 追踪压缩效果

**问题**：用户无法感知压缩系统的运行状态，缺乏透明度。

### 1.2 目标

实现**压缩策略可视化面板**，让用户能够：
1. **实时查看**上下文使用情况（已用 tokens / 总量 / 使用率）
2. **了解压缩状态**（哪些消息被压缩、压缩比、节省的 tokens）
3. **查看预算分配**（System Prompt / 历史 / 工具结果 / 响应预留）
4. **监控压缩统计**（成功/失败次数、总节省量）

### 1.3 非目标

-  不实现压缩策略的**手动配置**（已有 Settings 面板）
-  不实现消息级别的**详细压缩日志**（避免 UI 过载）
-  不实现压缩历史的**持久化存储**（仅当前会话）

---

## 2. 现有系统分析

### 2.1 核心组件

| 组件 | 位置 | 职责 | 可访问数据 |
|------|------|------|-----------|
| `CompressionMetrics` | `Editor/Core/Compression/` | 压缩统计追踪 | 工具/对话压缩次数、token 节省量、压缩比 |
| `ToolResultCompressor` | `Editor/Core/Compression/` | 工具结果压缩 | 阈值、目标 tokens、压缩成功/失败 |
| `ConversationCompressor` | `Editor/Core/Compression/` | 对话历史压缩 | 触发阈值、保留消息数、摘要消息 |
| `ContextWindowManager` | `Editor/Core/` | 上下文窗口管理 | 模型最大 tokens、截断策略 |
| `TokenCounter` | `Editor/Core/` | Token 估算 | 消息/对话 token 数估算 |
| `AgentLoop` | `Editor/Core/` | 核心对话循环 | 持有 `_compressionMetrics` 实例 |

### 2.2 数据流

```
用户发送消息
  ↓
AgentLoop.SendMessageAsync()
  ↓
[记忆召回] → 注入记忆上下文
  ↓
RunToolCallLoopAsync()
  ↓
  ├─→ CallLLMStreamAsync()
  │     ↓
  │   ConversationCompressor.CompressIfNeededAsync()  ← 检查上下文使用率
  │     ↓
  │   ContextWindowManager.TrimToFit()  ← 兜底截断
  │     ↓
  │   LLM API 调用
  │
  └─→ ExecuteToolCallsAsync()
        ↓
      ToolResultCompressor.CompressIfNeededAsync()  ← 压缩工具结果
        ↓
      添加到消息历史
```

### 2.3 关键发现

1. **`CompressionMetrics` 已存在** — 所有压缩统计数据已被追踪
2. **`AgentLoop._compressionMetrics` 是私有字段** — 需要暴露为公开属性
3. **压缩发生在 LLM 调用前** — 可在 UI 中实时更新
4. **无现有事件通知** — 需要新增事件或轮询机制

---

## 3. UI 设计方案

### 3.1 面板位置

**方案 A：独立面板（推荐）**
- 位置：Chat 窗口顶部，输入框上方，文件变更面板下方
- 样式：可折叠卡片，默认折叠
- 触发：点击标题栏展开/折叠
- 优点：不干扰主对话区域，信息密度可控

**方案 B：侧边栏模块**
- 位置：Hub 侧边栏新增 "Context" 模块
- 优点：与 Knowledge/Memory 并列，逻辑清晰
- 缺点：需要切换模块才能查看，不够实时

**选择**：**方案 A**（独立面板），理由：
- 上下文状态与当前对话强相关，应在主视图中可见
- 可折叠设计不占用过多空间
- 与文件变更面板（`FileChangeSummaryPanel`）设计一致

### 3.2 面板布局

```
┌─────────────────────────────────────────────────────────┐
│ ▼ 上下文使用情况  [12.5K / 200K tokens (6.3%)]   正常 │  ← 标题栏（可点击折叠）
├─────────────────────────────────────────────────────────┤
│ ┌─ Token 预算分配 ─────────────────────────────────┐   │
│ │ System Prompt:    3.2K  ████░░░░░░░░░░░░░░░░  16% │   │
│ │ 对话历史:         7.8K  ██████████░░░░░░░░░░  39% │   │
│ │ 工具结果:         1.5K  ██░░░░░░░░░░░░░░░░░░   8% │   │
│ │ 响应预留:         8.0K  ████████░░░░░░░░░░░░  40% │   │  ← 进度条可视化
│ └──────────────────────────────────────────────────┘   │
│                                                         │
│ ┌─ 压缩统计 ───────────────────────────────────────┐   │
│ │ 工具结果: 3 次压缩, 0 次失败, 2 次跳过           │   │
│ │   节省: 4.2K tokens (压缩比: 18%)                │   │
│ │ 对话历史: 1 次压缩, 0 次失败                     │   │
│ │   节省: 2.8K tokens (5 条消息被摘要)             │   │
│ │ 总计: 节省 7.0K tokens                           │   │  ← 统计数据
│ └──────────────────────────────────────────────────┘   │
│                                                         │
│ ┌─ 压缩状态 ───────────────────────────────────────┐   │
│ │  对话历史已压缩 (第 3-12 轮 → 摘要)             │   │
│ │  工具结果已压缩 (manage_script.read_file)       │   │  ← 最近压缩操作
│ └──────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────┘
```

### 3.3 状态指示器

标题栏右侧显示上下文健康状态：

| 使用率 | 图标 | 颜色 | 文本 |
|--------|------|------|------|
| < 50% |  | 绿色 | 正常 |
| 50-70% |  | 黄色 | 接近上限 |
| 70-90% |  | 橙色 | 压缩中 |
| > 90% |  | 红色 | 接近满载 |

### 3.4 交互行为

1. **默认状态**：折叠，仅显示标题栏（一行）
2. **点击标题栏**：展开/折叠面板
3. **自动展开**：当使用率 > 70% 时自动展开（首次）
4. **实时更新**：每次 LLM 调用后更新数据
5. **会话切换**：切换会话时重置统计数据

---

## 4. 技术实现方案

### 4.1 数据访问

#### 4.1.1 暴露 `CompressionMetrics`

**修改文件**: `Editor/Core/AgentLoop.cs`

```csharp
// 在 #region 公开属性 中新增：

/// <summary>
/// 压缩统计指标（只读），供 UI 层显示压缩状态。
/// </summary>
public CompressionMetrics CompressionMetrics => _compressionMetrics;
```

#### 4.1.2 计算 Token 预算分配

**新增方法**: `Editor/Core/AgentLoop.cs`

```csharp
/// <summary>
/// 获取当前上下文的 token 预算分配详情。
/// </summary>
/// <returns>预算分配信息</returns>
public ContextBudgetInfo GetContextBudget()
{
    if (_messages.Count == 0)
        return new ContextBudgetInfo();

    var settings = AgentCoreSettings.instance;
    int maxTokens = ContextWindowManager.GetModelMaxTokens(settings.llmModel);
    int reserveTokens = settings.reserveResponseTokens;
    int availableTokens = maxTokens - reserveTokens;

    // 计算各部分 token 数
    int systemTokens = 0;
    int conversationTokens = 0;
    int toolResultTokens = 0;

    foreach (var msg in _messages)
    {
        int msgTokens = TokenCounter.EstimateMessageTokens(msg);
        if (msg.Role == "system")
            systemTokens += msgTokens;
        else if (msg.Role == "tool")
            toolResultTokens += msgTokens;
        else
            conversationTokens += msgTokens;
    }

    int totalUsed = systemTokens + conversationTokens + toolResultTokens;

    return new ContextBudgetInfo
    {
        MaxTokens = maxTokens,
        AvailableTokens = availableTokens,
        ReserveTokens = reserveTokens,
        SystemTokens = systemTokens,
        ConversationTokens = conversationTokens,
        ToolResultTokens = toolResultTokens,
        TotalUsed = totalUsed,
        UsageRatio = (float)totalUsed / maxTokens
    };
}
```

#### 4.1.3 新增数据结构

**新增文件**: `Editor/Core/ContextBudgetInfo.cs`

```csharp
namespace AgentCore.Editor.Core
{
    /// <summary>
    /// 上下文预算分配信息 — 供 UI 层显示 token 使用情况。
    /// </summary>
    public struct ContextBudgetInfo
    {
        /// <summary>模型最大 token 数</summary>
        public int MaxTokens;

        /// <summary>可用 token 数（最大 - 预留）</summary>
        public int AvailableTokens;

        /// <summary>为响应预留的 token 数</summary>
        public int ReserveTokens;

        /// <summary>System Prompt 占用的 token 数</summary>
        public int SystemTokens;

        /// <summary>对话历史占用的 token 数</summary>
        public int ConversationTokens;

        /// <summary>工具结果占用的 token 数</summary>
        public int ToolResultTokens;

        /// <summary>总已用 token 数</summary>
        public int TotalUsed;

        /// <summary>使用率（0.0 ~ 1.0）</summary>
        public float UsageRatio;
    }
}
```

### 4.2 UI 组件实现

#### 4.2.1 组件结构

**新增文件**: `Editor/UI/Components/ContextUsagePanel.cs`

```csharp
using UnityEngine;
using UnityEngine.UIElements;
using AgentCore.Editor.Core;
using AgentCore.Editor.Core.Compression;

namespace AgentCore.Editor.UI.Components
{
    /// <summary>
    /// 上下文使用情况面板 — 显示 token 使用、压缩统计和预算分配。
    /// </summary>
    public class ContextUsagePanel : VisualElement
    {
        private readonly AgentLoop _agentLoop;

        // UI 元素
        private VisualElement _headerContainer;
        private Label _titleLabel;
        private Label _statusLabel;
        private VisualElement _contentContainer;
        private VisualElement _budgetSection;
        private VisualElement _metricsSection;
        private VisualElement _statusSection;

        private bool _isExpanded = false;

        public ContextUsagePanel(AgentLoop agentLoop)
        {
            _agentLoop = agentLoop;
            AddToClassList("context-usage-panel");
            BuildUI();
        }

        private void BuildUI()
        {
            // 标题栏（可点击折叠）
            _headerContainer = new VisualElement();
            _headerContainer.AddToClassList("context-usage-header");
            _headerContainer.RegisterCallback<ClickEvent>(OnHeaderClicked);

            _titleLabel = new Label(" 上下文使用情况");
            _titleLabel.AddToClassList("context-usage-title");
            _headerContainer.Add(_titleLabel);

            _statusLabel = new Label();
            _statusLabel.AddToClassList("context-usage-status");
            _headerContainer.Add(_statusLabel);

            Add(_headerContainer);

            // 内容区域（默认隐藏）
            _contentContainer = new VisualElement();
            _contentContainer.AddToClassList("context-usage-content");
            _contentContainer.style.display = DisplayStyle.None;

            _budgetSection = CreateBudgetSection();
            _metricsSection = CreateMetricsSection();
            _statusSection = CreateStatusSection();

            _contentContainer.Add(_budgetSection);
            _contentContainer.Add(_metricsSection);
            _contentContainer.Add(_statusSection);

            Add(_contentContainer);
        }

        private VisualElement CreateBudgetSection()
        {
            var section = new VisualElement();
            section.AddToClassList("context-section");

            var title = new Label("Token 预算分配");
            title.AddToClassList("context-section-title");
            section.Add(title);

            // 预算条目将在 UpdateBudget() 中动态创建
            return section;
        }

        private VisualElement CreateMetricsSection()
        {
            var section = new VisualElement();
            section.AddToClassList("context-section");

            var title = new Label("压缩统计");
            title.AddToClassList("context-section-title");
            section.Add(title);

            // 统计数据将在 UpdateMetrics() 中动态创建
            return section;
        }

        private VisualElement CreateStatusSection()
        {
            var section = new VisualElement();
            section.AddToClassList("context-section");

            var title = new Label("压缩状态");
            title.AddToClassList("context-section-title");
            section.Add(title);

            // 状态信息将在 UpdateStatus() 中动态创建
            return section;
        }

        private void OnHeaderClicked(ClickEvent evt)
        {
            _isExpanded = !_isExpanded;
            _contentContainer.style.display = _isExpanded ? DisplayStyle.Flex : DisplayStyle.None;
            _titleLabel.text = _isExpanded ? "▼ 上下文使用情况" : " 上下文使用情况";
        }

        /// <summary>
        /// 更新面板数据（由 ChatWindow 在 LLM 调用后调用）。
        /// </summary>
        public void UpdateData()
        {
            var budget = _agentLoop.GetContextBudget();
            var metrics = _agentLoop.CompressionMetrics;

            UpdateHeader(budget);
            UpdateBudget(budget);
            UpdateMetrics(metrics);
            UpdateStatus(metrics);

            // 自动展开逻辑：使用率 > 70% 时首次自动展开
            if (!_isExpanded && budget.UsageRatio > 0.7f)
            {
                _isExpanded = true;
                _contentContainer.style.display = DisplayStyle.Flex;
                _titleLabel.text = "▼ 上下文使用情况";
            }
        }

        private void UpdateHeader(ContextBudgetInfo budget)
        {
            // 格式化：[12.5K / 200K tokens (6.3%)]
            string tokenText = $"[{FormatTokens(budget.TotalUsed)} / {FormatTokens(budget.MaxTokens)} tokens ({budget.UsageRatio:P1})]";
            _titleLabel.text = (_isExpanded ? "▼ " : " ") + $"上下文使用情况  {tokenText}";

            // 状态指示器
            string statusText;
            string statusClass;
            if (budget.UsageRatio < 0.5f)
            {
                statusText = " 正常";
                statusClass = "status-normal";
            }
            else if (budget.UsageRatio < 0.7f)
            {
                statusText = " 接近上限";
                statusClass = "status-warning";
            }
            else if (budget.UsageRatio < 0.9f)
            {
                statusText = " 压缩中";
                statusClass = "status-compressing";
            }
            else
            {
                statusText = " 接近满载";
                statusClass = "status-critical";
            }

            _statusLabel.text = statusText;
            _statusLabel.ClearClassList();
            _statusLabel.AddToClassList("context-usage-status");
            _statusLabel.AddToClassList(statusClass);
        }

        private void UpdateBudget(ContextBudgetInfo budget)
        {
            // 清除旧内容（保留标题）
            while (_budgetSection.childCount > 1)
                _budgetSection.RemoveAt(1);

            AddBudgetBar("System Prompt", budget.SystemTokens, budget.MaxTokens);
            AddBudgetBar("对话历史", budget.ConversationTokens, budget.MaxTokens);
            AddBudgetBar("工具结果", budget.ToolResultTokens, budget.MaxTokens);
            AddBudgetBar("响应预留", budget.ReserveTokens, budget.MaxTokens);
        }

        private void AddBudgetBar(string label, int tokens, int maxTokens)
        {
            var row = new VisualElement();
            row.AddToClassList("budget-row");

            var labelElement = new Label($"{label}:");
            labelElement.AddToClassList("budget-label");
            row.Add(labelElement);

            var valueElement = new Label(FormatTokens(tokens));
            valueElement.AddToClassList("budget-value");
            row.Add(valueElement);

            var progressBar = new VisualElement();
            progressBar.AddToClassList("budget-progress-bar");
            float ratio = maxTokens > 0 ? (float)tokens / maxTokens : 0f;
            progressBar.style.width = Length.Percent(ratio * 100);
            row.Add(progressBar);

            var percentLabel = new Label($"{ratio:P0}");
            percentLabel.AddToClassList("budget-percent");
            row.Add(percentLabel);

            _budgetSection.Add(row);
        }

        private void UpdateMetrics(CompressionMetrics metrics)
        {
            // 清除旧内容（保留标题）
            while (_metricsSection.childCount > 1)
                _metricsSection.RemoveAt(1);

            // 工具结果统计
            var toolLine1 = new Label($"工具结果: {metrics.ToolResultCompressionSuccessCount} 次压缩, " +
                                      $"{metrics.ToolResultCompressionFailureCount} 次失败, " +
                                      $"{metrics.ToolResultCompressionSkippedCount} 次跳过");
            toolLine1.AddToClassList("metrics-line");
            _metricsSection.Add(toolLine1);

            if (metrics.ToolResultTokensSaved > 0)
            {
                float toolRatio = metrics.ToolResultOriginalTokens > 0
                    ? 1f - (float)(metrics.ToolResultOriginalTokens - metrics.ToolResultTokensSaved) / metrics.ToolResultOriginalTokens
                    : 0f;
                var toolLine2 = new Label($"  节省: {FormatTokens(metrics.ToolResultTokensSaved)} tokens (压缩比: {toolRatio:P0})");
                toolLine2.AddToClassList("metrics-line-indent");
                _metricsSection.Add(toolLine2);
            }

            // 对话历史统计
            var convLine1 = new Label($"对话历史: {metrics.ConversationCompressionSuccessCount} 次压缩, " +
                                      $"{metrics.ConversationCompressionFailureCount} 次失败");
            convLine1.AddToClassList("metrics-line");
            _metricsSection.Add(convLine1);

            if (metrics.ConversationTokensSaved > 0)
            {
                var convLine2 = new Label($"  节省: {FormatTokens(metrics.ConversationTokensSaved)} tokens " +
                                          $"({metrics.ConversationMessagesCompressed} 条消息被摘要)");
                convLine2.AddToClassList("metrics-line-indent");
                _metricsSection.Add(convLine2);
            }

            // 总计
            if (metrics.TotalTokensSaved > 0)
            {
                var totalLine = new Label($"总计: 节省 {FormatTokens(metrics.TotalTokensSaved)} tokens");
                totalLine.AddToClassList("metrics-line-total");
                _metricsSection.Add(totalLine);
            }
        }

        private void UpdateStatus(CompressionMetrics metrics)
        {
            // 清除旧内容（保留标题）
            while (_statusSection.childCount > 1)
                _statusSection.RemoveAt(1);

            // 显示最近的压缩操作
            if (metrics.ConversationCompressionSuccessCount > 0)
            {
                var line = new Label($" 对话历史已压缩 ({metrics.ConversationMessagesCompressed} 条消息 → 摘要)");
                line.AddToClassList("status-line");
                _statusSection.Add(line);
            }

            if (metrics.ToolResultCompressionSuccessCount > 0)
            {
                var line = new Label($" 工具结果已压缩 ({metrics.ToolResultCompressionSuccessCount} 次)");
                line.AddToClassList("status-line");
                _statusSection.Add(line);
            }

            if (metrics.TotalCompressionCount == 0)
            {
                var line = new Label("暂无压缩操作");
                line.AddToClassList("status-line-empty");
                _statusSection.Add(line);
            }
        }

        /// <summary>
        /// 重置面板（会话切换时调用）。
        /// </summary>
        public void Reset()
        {
            _isExpanded = false;
            _contentContainer.style.display = DisplayStyle.None;
            _titleLabel.text = " 上下文使用情况";
            _statusLabel.text = "";
        }

        private static string FormatTokens(int tokens)
        {
            if (tokens >= 1000)
                return $"{tokens / 1000.0:F1}K";
            return tokens.ToString();
        }
    }
}
```

#### 4.2.2 样式定义

**新增文件**: `Editor/UI/Components/ContextUsagePanel.uss`

```css
/* 上下文使用面板 */
.context-usage-panel {
    margin-top: 8px;
    margin-bottom: 8px;
    background-color: rgba(0, 0, 0, 0.1);
    border-radius: 4px;
    border-width: 1px;
    border-color: rgba(255, 255, 255, 0.1);
}

/* 标题栏 */
.context-usage-header {
    flex-direction: row;
    justify-content: space-between;
    align-items: center;
    padding: 8px 12px;
    cursor: link;
}

.context-usage-header:hover {
    background-color: rgba(255, 255, 255, 0.05);
}

.context-usage-title {
    font-size: 12px;
    -unity-font-style: bold;
    color: rgba(255, 255, 255, 0.9);
}

.context-usage-status {
    font-size: 11px;
    padding: 2px 8px;
    border-radius: 3px;
}

.status-normal {
    color: #4CAF50;
    background-color: rgba(76, 175, 80, 0.2);
}

.status-warning {
    color: #FFC107;
    background-color: rgba(255, 193, 7, 0.2);
}

.status-compressing {
    color: #FF9800;
    background-color: rgba(255, 152, 0, 0.2);
}

.status-critical {
    color: #F44336;
    background-color: rgba(244, 67, 54, 0.2);
}

/* 内容区域 */
.context-usage-content {
    padding: 8px 12px 12px 12px;
}

/* 分区 */
.context-section {
    margin-bottom: 12px;
}

.context-section-title {
    font-size: 11px;
    -unity-font-style: bold;
    color: rgba(255, 255, 255, 0.7);
    margin-bottom: 6px;
}

/* 预算条目 */
.budget-row {
    flex-direction: row;
    align-items: center;
    margin-bottom: 4px;
}

.budget-label {
    font-size: 10px;
    color: rgba(255, 255, 255, 0.7);
    width: 100px;
}

.budget-value {
    font-size: 10px;
    color: rgba(255, 255, 255, 0.9);
    width: 50px;
    -unity-text-align: middle-right;
}

.budget-progress-bar {
    flex-grow: 1;
    height: 8px;
    background-color: #2196F3;
    border-radius: 4px;
    margin-left: 8px;
    margin-right: 8px;
}

.budget-percent {
    font-size: 10px;
    color: rgba(255, 255, 255, 0.6);
    width: 40px;
    -unity-text-align: middle-right;
}

/* 统计行 */
.metrics-line {
    font-size: 10px;
    color: rgba(255, 255, 255, 0.8);
    margin-bottom: 2px;
}

.metrics-line-indent {
    font-size: 10px;
    color: rgba(255, 255, 255, 0.6);
    margin-left: 12px;
    margin-bottom: 4px;
}

.metrics-line-total {
    font-size: 10px;
    -unity-font-style: bold;
    color: #4CAF50;
    margin-top: 4px;
}

/* 状态行 */
.status-line {
    font-size: 10px;
    color: rgba(255, 255, 255, 0.8);
    margin-bottom: 2px;
}

.status-line-empty {
    font-size: 10px;
    color: rgba(255, 255, 255, 0.5);
    font-style: italic;
}
```

### 4.3 集成到 ChatWindow

**修改文件**: `Editor/UI/ChatWindow.cs`

```csharp
// 在字段区域新增：
private ContextUsagePanel _contextUsagePanel;

// 在 CreateGUI() 中，文件变更面板之后添加：
_contextUsagePanel = new ContextUsagePanel(_agentLoop);
_inputContainer.parent.Insert(_inputContainer.parent.IndexOf(_inputContainer), _contextUsagePanel);

// 加载样式表
var contextUsageStyleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(
    "Packages/com.agentcore.unity/Editor/UI/Components/ContextUsagePanel.uss");
if (contextUsageStyleSheet != null)
    rootVisualElement.styleSheets.Add(contextUsageStyleSheet);
```

**修改文件**: `Editor/UI/ChatWindow.Events.cs`

```csharp
// 在 HandleAgentEvent() 中，AgentEventType.MessageComplete 分支添加：
case AgentEventType.MessageComplete:
    // ... 现有代码 ...
    _contextUsagePanel?.UpdateData();  // 更新上下文面板
    break;
```

**修改文件**: `Editor/UI/ChatWindow.Sessions.cs`

```csharp
// 在 SwitchToSession() 中，成功切换后添加：
_contextUsagePanel?.Reset();
_contextUsagePanel?.UpdateData();
```

---

## 5. 实现步骤

### 5.1 Phase 1: 数据层（1-2 小时）

1.  分析现有压缩系统代码
2. [ ] 在 `AgentLoop.cs` 中暴露 `CompressionMetrics` 属性
3. [ ] 在 `AgentLoop.cs` 中新增 `GetContextBudget()` 方法
4. [ ] 新增 `ContextBudgetInfo.cs` 数据结构
5. [ ] 编译验证，确保无错误

### 5.2 Phase 2: UI 组件（2-3 小时）

6. [ ] 创建 `ContextUsagePanel.cs` 组件
7. [ ] 创建 `ContextUsagePanel.uss` 样式表
8. [ ] 实现折叠/展开交互
9. [ ] 实现数据更新逻辑
10. [ ] 实现自动展开逻辑

### 5.3 Phase 3: 集成（1 小时）

11. [ ] 在 `ChatWindow.cs` 中集成面板
12. [ ] 在 `ChatWindow.Events.cs` 中添加更新调用
13. [ ] 在 `ChatWindow.Sessions.cs` 中添加重置调用
14. [ ] 测试面板显示和交互

### 5.4 Phase 4: 测试与优化（1-2 小时）

15. [ ] 测试不同上下文使用率下的显示
16. [ ] 测试压缩触发后的数据更新
17. [ ] 测试会话切换时的重置
18. [ ] 测试折叠/展开交互
19. [ ] 性能测试（确保不影响 LLM 调用速度）

### 5.5 Phase 5: 文档更新（30 分钟）

20. [ ] 更新 `package.json` 版本号 → v0.5.2
21. [ ] 更新 `CHANGELOG.md` 添加 v0.5.2 条目
22. [ ] 更新 `ROADMAP.md` 标记任务 6.0.4 为 `[x]`

---

## 6. 验收标准

### 6.1 功能验收

- [ ] 面板默认折叠，点击标题栏可展开/折叠
- [ ] 标题栏显示当前 token 使用情况（已用/总量/百分比）
- [ ] 标题栏显示健康状态指示器（正常/接近上限/压缩中/接近满载）
- [ ] Token 预算分配区域显示 4 个进度条（System/对话/工具/预留）
- [ ] 压缩统计区域显示工具和对话的压缩次数、节省量
- [ ] 压缩状态区域显示最近的压缩操作
- [ ] 使用率 > 70% 时首次自动展开
- [ ] 会话切换时重置面板状态

### 6.2 性能验收

- [ ] 面板更新不阻塞 LLM 调用（< 10ms）
- [ ] 面板渲染不影响消息滚动流畅度
- [ ] 内存占用 < 1MB

### 6.3 兼容性验收

- [ ] Unity 2021.3+ 正常显示
- [ ] 深色主题下样式正常
- [ ] 窗口缩放时布局不错乱

---

## 7. 风险与缓解

| 风险 | 可能性 | 影响 | 缓解措施 |
|------|--------|------|---------|
| `CompressionMetrics` 数据不准确 | 低 | 中 | 已在 v0.5.0 验证，数据追踪完整 |
| UI 更新频率过高影响性能 | 中 | 低 | 仅在 MessageComplete 事件时更新，不轮询 |
| 面板占用过多空间 | 低 | 低 | 默认折叠，用户可控 |
| 样式在不同 Unity 版本不一致 | 中 | 低 | 使用基础 USS 属性，避免新特性 |

---

## 8. 后续优化方向（Phase 6+）

以下功能**不在 v0.5.2 范围内**，可作为后续迭代方向：

1. **压缩历史记录** — 显示最近 10 次压缩操作的详细日志
2. **手动触发压缩** — 提供"立即压缩"按钮
3. **压缩策略配置** — 在面板中直接调整阈值（当前需要去 Settings）
4. **导出压缩报告** — 生成 Markdown 格式的压缩统计报告
5. **压缩效果可视化** — 显示压缩前后的消息对比

---

## 9. 参考资料

- [`Editor/Core/Compression/CompressionMetrics.cs`](../Editor/Core/Compression/CompressionMetrics.cs) — 压缩统计数据结构
- [`Editor/Core/ContextWindowManager.cs`](../Editor/Core/ContextWindowManager.cs) — 上下文窗口管理
- [`Editor/UI/Components/FileChangeSummaryPanel.cs`](../Editor/UI/Components/FileChangeSummaryPanel.cs) — 类似的可折叠面板实现
- [ROADMAP.md](ROADMAP.md) — Phase 6.0 上下文压缩与管理

---

> **文档状态**: 待用户确认  
> **下一步**: 用户 Review 并确认设计方案后，进入代码实现阶段
