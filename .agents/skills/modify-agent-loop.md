# Skill: 修改 AgentLoop 核心

> 当需要修改 `AgentLoop.cs`（系统核心）时，加载此 Skill。
> AgentLoop 是整个 AgentCore 的心脏，修改需要极度谨慎。

---

## 风险等级： 高

AgentLoop 的修改可能影响：
- 消息处理循环
- 工具调用分发
- Domain Reload 恢复
- 流式响应处理
- 会话管理
- 记忆系统集成

---

## 前置检查

**修改前必须先完整阅读 `Editor/Core/AgentLoop.cs`，理解当前实现。**

1. **理解状态机** — 阅读 `Editor/Core/MessageTypes.cs` 中的 `AgentState` 枚举，确认当前修改涉及哪些状态
2. **确认影响范围** — 修改是否涉及：
   - [ ] 消息处理流程
   - [ ] 工具执行
   - [ ] 流式响应
   - [ ] Domain Reload（阅读 `Editor/Core/DomainReloadState.cs`）
   - [ ] 记忆系统
   - [ ] 状态管理
3. **确认 Domain Reload 影响** — 新增的状态或数据是否需要跨 Domain Reload 保留？

---

## 核心架构

### 消息处理循环

```
SendMessageAsync(userMessage)
  ├── 添加 user message 到 _messages
  ├── SearchRelevantMemories() → InjectMemoryContext()
  └── RunToolCallLoopAsync()
        ├── CallLLMStreamAsync() → 流式响应
        ├── 检查 tool_calls
        │   ├── 有 → ExecuteToolCallsAsync() → 继续循环
        │   └── 无 → HandleFinalResponse() → 结束
        └── 检查 maxToolRounds 限制
```

### Domain Reload 保存/恢复

```
OnBeforeAssemblyReload()
  → 保存到 DomainReloadState:
    - InterruptPhase (Streaming/ExecutingTool/WaitingCompilation)
    - 对话历史 (_messages)
    - 会话 ID
    - 待处理的 tool calls
    - 最后的 assistant 内容

TryResumeAfterReload()
  → 读取 DomainReloadState
  → 根据 InterruptPhase 恢复
  → TriggerResumeLLMCall()
```

> **注意**: 以上是架构概览。具体的方法名、字段名和恢复逻辑请以实际代码为准。修改前务必阅读最新的 `AgentLoop.cs`。

---

## 修改规则

### 规则 1: 事件驱动，不直接操作 UI

```csharp
//  正确 — 通过事件通知
EmitEvent(AgentEvent.StateChanged(AgentState.Streaming));
EmitEvent(AgentEvent.StreamToken(token, messageId));

//  错误 — 直接操作 UI
chatWindow.AddMessage(...);  // 禁止！
```

### 规则 2: CancellationToken 必须传递

所有 async 方法必须接受并传递 `CancellationToken`。

### 规则 3: 状态变更必须通过 SetState

```csharp
//  正确
SetState(AgentState.ExecutingTool);

//  错误
_state = AgentState.ExecutingTool;  // 不会触发事件
```

### 规则 4: 新增跨 Domain Reload 数据

如果新增的字段需要在 Domain Reload 后恢复：

1. 在 `DomainReloadState` 中添加对应字段
2. 在 `OnBeforeAssemblyReload()` 中保存
3. 在 `TryResumeAfterReload()` 中恢复
4. 在 `DomainReloadState.ClearInterruption()` 中清理

### 规则 5: 错误不吞没

```csharp
//  正确 — 捕获后通知
catch (Exception ex)
{
    EmitEvent(AgentEvent.ErrorEvent($"Tool execution failed: {ex.Message}"));
    SetState(AgentState.Error);
}

//  错误 — 吞没异常
catch (Exception ex)
{
    Debug.LogError(ex);  // 仅日志，UI 不知道出错了
}
```

---

## 修改后验证清单

### 基本验证

- [ ] 编译通过，零错误零警告
- [ ] 正常对话流程工作（发送消息 → 收到回复）
- [ ] 工具调用流程工作（LLM 调用工具 → 执行 → 返回结果 → 继续对话）
- [ ] 取消操作工作（点击取消按钮 → 停止处理）
- [ ] 错误处理工作（模拟错误 → 显示错误信息）

### Domain Reload 验证（如果修改涉及状态或数据）

- [ ] 在工具调用过程中触发 Domain Reload → 恢复后继续
- [ ] 在流式响应过程中触发 Domain Reload → 恢复后继续
- [ ] 在等待编译过程中触发 Domain Reload → 恢复后继续
- [ ] 恢复后对话上下文完整

### 会话验证

- [ ] 新会话创建正常
- [ ] 会话切换正常
- [ ] 会话恢复正常（重启 Unity 后）

---

## 常见修改场景

### 场景 1: 添加新的消息处理逻辑

找到消息处理循环的入口方法，在合适的位置插入新逻辑。注意保持循环结构不变。

### 场景 2: 修改工具执行流程

找到工具执行方法。注意 `RequiresMainThread` 的分发逻辑和错误收集。

### 场景 3: 添加新的 AgentState

1. 在 `MessageTypes.cs` 的 `AgentState` 枚举中添加
2. 在 `AgentLoop` 中处理新状态的转换
3. 在 `ChatWindow.HandleAgentEvent()` 中处理 UI 更新
4. 如果需要跨 Domain Reload，在 `DomainReloadState` 中添加支持

### 场景 4: 修改记忆系统集成

找到记忆相关方法（搜索 `Memory` 关键字）。注意记忆消息在对话历史中的位置和生命周期。

---

## 相关文件发现

修改 AgentLoop 时，通常需要同时了解以下文件（通过阅读实际代码发现）：

- `Editor/Core/` 目录下的所有文件 — 核心运行时
- `Editor/Core/MessageTypes.cs` — 状态机和事件定义
- `Editor/Core/DomainReloadState.cs` — Domain Reload 恢复状态
- `Editor/UI/ChatWindow.cs` — UI 事件处理（搜索 `HandleAgentEvent`）
