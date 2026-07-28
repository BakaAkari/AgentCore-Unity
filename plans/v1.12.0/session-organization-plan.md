# v1.12.0 — Session Organization（tag + 归档 + 自动命名升级）

**状态**: DRAFT
**创建**: 2026-07-28
**目标平台**: ROLE_A(WIN) 主开发，Mac 侧同步产物验证

---

## 需求背景

用户询问「session 分类/归档/文件夹」功能。经过多轮讨论收敛为核心痛点：
**session 列表越用越长，视觉噪音干扰认知密度**。

关键设计决策（用户拍板）：
1. **LLM 只负责命名**，不负责分类。tag 由用户手动决定。
2. **自动命名触发**：每轮用户对话完成后 debounce 触发，LLM 判断是否需要更新（返回 `KEEP` 或新标题）。
3. **每个 session 只有一个 tag**（单 tag，不是多 tag）。
4. **模型配置**：复用现有 model config 体系，加一个"辅助任务模型"下拉，默认=当前 session 模型。
5. **老 session 处理**：agent 后台补名（不是补 tag，因为 tag 是手动的）。
6. **不做时间衰减自动归档**。归档纯手动。
7. **分组**：主列表按 tag 分组，归档区按时间分组。

---

## 现状盘点（摸底已完成）

| 模块 | 文件 | 状态 |
|---|---|---|
| Session 数据模型 | `Editor/Session/SessionData.cs` | 已有 `Id/Title/CreatedAt/UpdatedAt/MessageCount`，用 Newtonsoft `[JsonProperty]`，加字段用 `NullValueHandling.Ignore` 向后兼容零成本 |
| Session 存储 | `Editor/Session/SessionStorage.cs` | `Save/Load/ListSessions/Delete`。`ListSessions` 用 JObject 轻量解析（只读摘要字段），加 tag/archived 字段要同步这里 |
| Session Summary | `Editor/Session/SessionStorage.cs:240` | `SessionSummary { Id, Title, UpdatedAt, MessageCount }` |
| Session 管理器 | `Editor/Session/SessionManager.cs` | 单例，有 `GetSessionList()`、`RenameSession(id, title)`、`CurrentSessionId` |
| **自动命名服务（已有！）** | `Editor/Session/SessionAutoTitleService.cs` | `GenerateTitleAsync(sessionId)` 已实现，复用 `CompressionLLMClientFactory`。当前只被"右键→自动重命名"手动触发 |
| UI 会话面板 | `Editor/UI/ChatWindow.Sessions.cs` | 平铺 ListView，右键菜单已含"自动重命名/重命名/导出/删除" |
| Agent 状态事件 | `Editor/Core/AgentLoop.Events.cs` + `MessageTypes.cs` | 有 `AgentEvent.StateChanged`，从非 Idle → Idle 即一轮结束 |

**重要发现**：`SessionAutoTitleService` 已经完备存在——这次功能大量复用它，只需要把"手动触发"改成"每轮 debounce 自动触发" + 加"KEEP 判断" prompt。

---

## 数据模型变更

`SessionData` 加两个字段：

```csharp
/// <summary>用户手动打的 tag（单 tag，null=未分类）</summary>
[JsonProperty("tag", NullValueHandling = NullValueHandling.Ignore)]
public string Tag { get; set; }

/// <summary>是否已归档</summary>
[JsonProperty("archived", NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
public bool Archived { get; set; }
```

`SessionSummary` 同步加两个字段，`SessionStorage.ListSessions()` 里从 JObject 读出来。

**向后兼容性**：老 JSON 没这两个字段 → 反序列化后 `Tag=null`（未分类）、`Archived=false`（不归档）。零迁移成本。

---

## 自动命名升级（v1.12 核心之一）

### 触发链路

1. `AgentLoop.StateChanged` 事件：`Working/Streaming/... → Idle` 时视为"一轮完成"
2. `ChatWindow`（或新起一个 `SessionAutoTitleController` 类）订阅事件，标记 `_pendingAutoTitle = true`
3. Debounce：Idle 后启动一个 3 秒延迟 timer；期间若再次进入非 Idle，取消 timer；到期则触发
4. 调 `SessionAutoTitleService.GenerateTitleAsync(currentSessionId)`（异步，不阻塞 UI）
5. 结果处理：
   - 返回 `KEEP` → 丢弃，不改标题
   - 返回新标题 → `SessionManager.RenameSession(id, newTitle)` + 刷新列表

### Prompt 升级（在 SessionAutoTitleService 里加分支）

现有 prompt 只做"生成新标题"，新增一个 mode：

```csharp
public static async Task<string> GenerateTitleAsync(
    string sessionId,
    string currentTitle = null,        // 传入当前标题
    bool allowKeep = false,             // 允许返回 KEEP
    CancellationToken ct = default)
```

`allowKeep=true` 时 prompt 追加：
> 当前标题：「{currentTitle}」。若当前标题仍能准确概括最近对话主题，直接输出 `KEEP` 三个字符；否则输出新标题。

返回 `KEEP`（或 `"KEEP"`、`keep` 等大小写变体）→ 返回特殊标记 `KEEP_TITLE`（常量），调用方识别后不改。

**边界情况**：
- 手动触发（右键→自动重命名）：`allowKeep=false`，走原逻辑，永远生成新标题
- 自动 debounce 触发：`allowKeep=true`，可 KEEP

### 用户手动改过标题的保护

`SessionData` 加个字段：

```csharp
[JsonProperty("title_manually_set", NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
public bool TitleManuallySet { get; set; }
```

用户走"重命名"（`BeginRenameSession` → commitRename → `RenameSession`）时置 `true`。自动 debounce 触发时先检查该 flag，`true` 则跳过——**尊重用户意图**。

但用户手动触发"自动重命名"（右键菜单）应该允许覆盖，需要清 flag。

---

## 老 session 后台补名

启动时（`ChatWindow.OnEnable` 或 `SessionManager` 初始化）跑一个后台协程：

```csharp
foreach (var summary in GetSessionList()) {
    if (summary.Title == DefaultTitle || string.IsNullOrEmpty(summary.Title)) {
        // 后台补名，每次间隔 2 秒避免 rate limit
        await Task.Delay(2000);
        var newTitle = await SessionAutoTitleService.GenerateTitleAsync(summary.Id);
        if (!string.IsNullOrEmpty(newTitle)) {
            SessionManager.Instance.RenameSession(summary.Id, newTitle);
        }
    }
}
```

无 UI 阻塞，无进度条。用户开着插件几分钟后自动补完。

---

## UI 侧改动

### 主列表分组重构（`ChatWindow.Sessions.cs` → `RefreshSessionList`）

当前逻辑：`foreach session → CreateSessionItem`。

新逻辑：
1. `GetSessionList()` 拿到全部 summary
2. 分成两组：`active = summaries.Where(s => !s.Archived)` / `archived = summaries.Where(s => s.Archived)`
3. `active` 再按 `s.Tag` 分组，`null` 归入 "未分类" 组；组内按 `UpdatedAt` 倒序
4. 组头用 `Foldout`（UI Toolkit 原生），默认展开
5. 组头显示 `▼ TAG_NAME (count)`
6. `archived` 组用一个总的 Foldout，默认**折叠**；组内按时间分组（今天/本周/更早）

### 右键菜单新增项

`ShowSessionContextMenu` 里加：

```
- 归档 / 取消归档（根据当前状态）
- 设置 tag / 修改 tag / 移除 tag
- （分隔线）
- （原有的 自动重命名 / 重命名 / 导出 / 删除）
```

"设置 tag" 弹一个下拉菜单：
- 已有 tag 列表（从所有 session 的 Tag 字段去重收集）
- 分隔线
- "新建 tag..." → 弹小输入框

**没有 tag 编辑器 / 颜色 / 图标**这些复杂度——单 tag + 自由文本，最简。

### CSS 微调

`session-item` 前面加个 tag chip（灰色小 pill）显示当前 tag，方便识别。

---

## 模型配置

**决策变更**：用户明确要求"设置里model选择器就直接复用现有的model config体系，只是服务于不同的功能而已"。

现有 model config 体系需要盘点（下一步在实施 Phase 1 时做）——找到主对话模型配置的位置，新增一个"辅助任务模型"字段，默认继承主模型。

`SessionAutoTitleService` / `CompressionLLMClientFactory` 里，把"辅助任务模型"传入即可。

**注意**：这一部分改动可能比预期大，因为要涉及 Settings UI + 配置持久化。如果发现复杂，Phase 1.5 先做"沿用 compression 模型配置"作为过渡（compression 已有独立模型配置），后续再做"辅助任务模型"细分。

---

## 分阶段实施

### Phase 1：数据模型 + 存储层（低风险）
- `SessionData` 加 `Tag/Archived/TitleManuallySet` 字段
- `SessionSummary` 同步 + `ListSessions` JObject 解析
- `SessionManager` 加 `SetTag(id, tag)` / `ArchiveSession(id)` / `UnarchiveSession(id)` API
- 编译通过，向后兼容测试（老 session JSON 读取正常）

### Phase 2：自动命名 debounce（中风险）
- `SessionAutoTitleService.GenerateTitleAsync` 加 `allowKeep` 参数
- Prompt 升级支持 `KEEP` 返回
- 新起 `SessionAutoTitleController`（订阅 AgentLoop.StateChanged，管理 debounce timer）
- `ChatWindow` 初始化时挂载 controller
- 手动改标题时置 `TitleManuallySet = true`

### Phase 3：UI 分组 + 右键菜单（UI 密集）
- `RefreshSessionList` 重构成分组渲染
- 右键菜单新增 tag / 归档项
- CSS 加 tag chip 样式
- 归档区 Foldout（默认折叠）

### Phase 4：老 session 后台补名 + 设置模型（可选）
- 启动时后台协程扫描 default title 会话
- Settings 加"辅助任务模型"字段（如果时间充裕）

### Phase 5：测试 + 文档
- Windows 平台验证清单（用户实测为主）
- README 版本徽章更新到 v1.12.0
- plans/README.md 加行

---

## 风险 & 未知项

1. **debounce timer 在 EditorApplication 里的靠谱实现**：Unity Editor 没标准 debounce，用 `EditorApplication.update` + 时间戳可行，需要实测。
2. **老 session 反序列化**：`Archived` 用 `DefaultValueHandling.Ignore` 是关键，否则老 JSON 反序列化后可能变成 `Archived=false` 但显式写入。测试验证。
3. **LLM 返回 `KEEP` 的稳定性**：模型可能返回 "KEEP" / "keep" / "保持" / 直接把当前标题吐回来。SanitizeTitle 里加强判断：`raw.Trim().Equals("KEEP", OrdinalIgnoreCase)` 或 `raw.Trim() == currentTitle` 都视为 KEEP。
4. **辅助任务模型配置**：本次不一定要做，风险不确定。Phase 4 里做完整评估。
5. **Foldout 状态持久化**：用户折叠某个 tag 组后，切换会话再回来应该记住状态。用 `SessionState` 或 `EditorPrefs`。

---

## 明确不做的事

- ❌ 多 tag / tag 颜色 / tag 图标 / tag 层级
- ❌ 真文件夹（树形结构 + 拖拽）
- ❌ 时间衰减自动归档
- ❌ 全文搜索（未来可加，本次不做）
- ❌ tag 归一化 / 近义词合并（先看软约束够不够）
- ❌ session 内容分类 LLM（只做命名，不做归类）
