# Skill: UI 开发 (modify-ui)

> 修改或扩展 AgentCore 的 UI 系统（ChatWindow、消息气泡、工具调用卡片等）。

---

## 前置检查

**修改前必须先阅读相关文件，理解当前实现：**

1. 阅读 `Editor/UI/ChatWindow.cs` — 主窗口逻辑和事件处理
2. 列出 `Editor/UI/Components/` 目录 — 了解现有 UI 组件
3. 阅读 `Editor/Core/MessageTypes.cs` — 了解 `AgentEventType` 和 `AgentEvent` 定义
4. 根据修改目标，阅读对应的 `.uss` 和 `.uxml` 文件

> **关键原则**: UI Toolkit 用于 ChatWindow 和组件，IMGUI 仅用于 Settings Provider。以实际代码为准。

---

## UI 架构概览

```
ChatWindow (EditorWindow)
├── 使用 UI Toolkit (VisualElement)
├── 布局/样式: 同目录下的 .uxml/.uss 文件
├── 事件处理: HandleAgentEvent(AgentEvent)
│
├── 消息区域
│   ├── 用户/助手消息气泡
│   ├── 工具调用分组和卡片
│   └── Domain Reload 通知
│
├── 输入区域
│   ├── 文本输入框
│   └── 发送/取消按钮
│
└── 侧边栏 (会话列表)
```

> **注意**: 以上是概览。具体的组件名称和结构请阅读实际代码确认。

---

## 关键模式

### 1. 事件驱动 UI 更新

UI **不直接**调用 AgentLoop 的内部方法。所有更新通过事件：

```
AgentLoop → EmitEvent(AgentEvent) → ChatWindow.HandleAgentEvent(evt)
```

**规则**: 如果需要新的 UI 更新类型：
1. 在 `MessageTypes.cs` 的 `AgentEventType` 枚举中添加值
2. 在 `AgentEvent` 中添加工厂方法
3. 在 `AgentLoop` 中发射事件
4. 在 `ChatWindow.HandleAgentEvent` 中处理

### 2. UI Toolkit 组件模式

阅读 `Editor/UI/Components/` 下的现有组件，学习当前项目的组件模式。通常包括：
- USS class 常量定义
- 构造函数中创建 DOM 结构
- 公共方法更新状态

### 3. 样式规范 (USS)

```css
/* 命名规范: BEM-like */
.my-component { }
.my-component__header { }
.my-component__content { }
.my-component--active { }    /* 修饰符 */

/* 颜色使用 Unity 内置变量 */
.my-component {
    background-color: var(--unity-colors-default-background);
    border-color: var(--unity-colors-default-border);
    color: var(--unity-colors-default-text);
}
```

---

## 常见修改场景

### 场景 A: 添加新的 UI 组件

1. 在 `Editor/UI/Components/` 创建组件类（继承 `VisualElement`）
2. （可选）创建对应的 `.uss` 和 `.uxml` 文件
3. 在 `ChatWindow.cs` 中使用组件
4. 通过 `HandleAgentEvent` 更新组件状态

### 场景 B: 修改现有组件样式

1. 找到对应的 `.uss` 文件
2. 如果需要新的 DOM 结构，修改组件的 `.cs` 文件
3. 阅读现有样式了解命名规范后再修改

### 场景 C: 添加新的事件类型

参考上面"事件驱动 UI 更新"部分的 4 步流程。

### 场景 D: 修改 Settings Provider (IMGUI)

`AgentCoreSettingsProvider` 使用 IMGUI（因为 `SettingsProvider.OnGUI` 是 IMGUI 接口）。参考 `add-settings` skill。

---

## 检查清单

- [ ] 使用 **UI Toolkit** (VisualElement)，不使用 IMGUI（Settings Provider 除外）
- [ ] USS class 命名遵循现有组件的 BEM-like 规范
- [ ] 颜色使用 Unity CSS 变量，兼容暗色/亮色主题
- [ ] 新组件放在 `Editor/UI/Components/` 目录
- [ ] UI 更新通过 `AgentEvent` 事件驱动，不直接调用 AgentLoop
- [ ] 不在 UI 组件中执行耗时操作（保持 UI 响应）
- [ ] 新增事件类型在 `AgentEventType` 枚举中注册
- [ ] 编译通过，ChatWindow 正常打开和使用

---

## 禁止事项

- **禁止** 在工具类中直接引用 `ChatWindow` 或任何 UI 组件
- **禁止** 在 UI 组件中直接调用 `AgentLoop` 的内部方法（通过公共 API）
- **禁止** 使用 `EditorGUILayout` 在 UI Toolkit 组件中（混用 IMGUI 和 UI Toolkit）
- **禁止** 硬编码颜色值（使用 USS 变量或 Unity 内置变量）
- **禁止** 在 `HandleAgentEvent` 中执行异步操作（事件处理应同步完成）

---

## 如何找到参考实现

不要依赖固定的文件列表。按以下方式发现参考：

1. **现有组件**: 列出 `Editor/UI/Components/` 目录，选择最相似的组件阅读
2. **样式模式**: 阅读同目录下的 `.uss` 文件了解命名和样式规范
3. **事件处理**: 在 `ChatWindow.cs` 中搜索 `HandleAgentEvent` 了解事件分发模式
4. **布局结构**: 阅读 `.uxml` 文件了解 DOM 结构
