# Perf Issue — Agent 流式输出阻塞 Editor 主线程

**发现时间**: 2026-07-22
**发现方式**: v1.8.0 G02 `read_frame` 首个真实调用返回结果
**优先级**: v1.8.0 全 P0 收官后统一优化（不阻塞当前发版）
**当前状态**: 已确认现象，需进一步定位具体阻塞点

---

## 现象

在 AgentCore Chat 面板**正在流式输出文本**时，Unity Editor 变得**极度卡顿**：帧率跌到 4.4 FPS，主线程单帧 228 ms。用户直接体感：Scene 视图、Inspector、Console 全部拖影，鼠标交互延迟明显。

## 证据

**测试场景**: Play Mode 已进入，Profiler 已启用，`read_frame` 抓取"用户正等 Agent 流式输出结果"时刻的最后一帧。

**帧 #379 (Main Thread) 数据**（原始来源：`doc_1a6f7775a6d4_manage_editor_action=play_mode..._20260722_185930.json`）:

| 指标 | 值 |
|------|-----|
| 帧时间 | **228.52 ms** |
| FPS | **4.4 FPS** |

**Top 根级 marker**:

| Marker | total_ms | self_ms | calls | gc_alloc |
|--------|----------|---------|-------|----------|
| EditorLoop | 228.41 | 0.008 | 1 | 132.0 KB |
| Profiler.FlushCounters | 0.088 | 0.0004 | 1 | 0 B |
| Profiler.CollectEditorStats | 0.002 | 0.0005 | 1 | 0 B |

**关键判读**:
- `EditorLoop` 占帧时间的 **99.96%**（228.41 / 228.52）
- `EditorLoop.self_ms=0.008` — 它自己不干活，**228 ms 全在它的子调用里**
- 每帧 GC 分配 **132 KB** — 存在大量对象分配（可能是 UI 字符串重建 / RichText 重解析 / UIElement 重建）

**对照**: 同一 Editor 会话中，`sample_recorder` 采样 Main Thread 时（Agent 空闲期），中位数是 **1.09 ms/帧**（详见 `doc_416333c0a34a` 测试结果）。

**结论**: 卡顿是 Agent 流式输出期专属现象，稳态下 Editor 主线程正常。

---

## 归因推断（未验证）

228 ms 的具体分布尚未定位（`read_frame` 只给根级 marker，需递归展开 `EditorLoop` 子节点，或临时开更细的 hierarchy view）。合理的候选嫌疑：

1. **LLM 流式增量的主线程回调频率过高**
   - 每收一个 SSE chunk 就在主线程调 `Repaint()` / `MarkDirtyRepaint()`
   - 若 chunk 频率 = 20-50/s，且每次重绘要重新渲染整段 Markdown/RichText，主线程持续被占

2. **RichText / Markdown 每次重新解析全文**
   - 假如渲染逻辑是"每次增量 append 后重新 parse 全文 → 生成 IMGUI/UIElement 树"，长文本下每次 parse 都是 O(N)
   - 与 GC 132 KB/帧 吻合（RichText tag 匹配、TextGenerator 分配、UIElement 池子扩容都会分配）

3. **主线程同步落盘**
   - 若每收一次增量都 `File.AppendAllText` 或 `EditorPrefs.SetString` 写状态，磁盘同步 IO 阻塞主线程

4. **Domain Reload 检查器 / TypeCache 反射**
   - 流式期间若触发任何反射（工具名解析、Assembly 扫描）在主线程同步跑，会周期性咬帧

**排序（信心）**: (1) + (2) 最可能，(3) 次之，(4) 不太可能但不能排除。

---

## 修复方向（等 v1.8.0 收官后设计+实施）

### 方案 A — 流式输出改**异步/后台**（推荐主方向）

思路：把"接收 token → 渲染"从主线程解耦。

- LLM 增量 chunk 进入 `ConcurrentQueue`
- 主线程 EditorApplication.update 里限速消费（例如每 100 ms flush 一次，或 chunk 累积到 200 字符再 flush）
- UI 只在 flush 时刻 Repaint，一次绘完累积文本

**收益**: 帧率恢复；用户感知延迟从"实时逐字"变"每 100 ms 一段"，仍是流式体验，Editor 不卡。

**成本**: 需重构现有 streaming pipeline，改主线程调度点。

### 方案 B — 流式**静默**，只保留状态栏 UI 指示

思路：Chat 面板流式期间不刷新完整消息内容，只在状态栏（HubRail 底部或类似位置）显示"Agent 正在思考…" + 简短最新一句。完成后一次性 render 完整 markdown。

**收益**: 主线程消耗趋近于零。实现简单。

**成本**: 失去"逐字流式"体验，用户看不到中间进度。**（用户在原始诉求里明确接受这条：'chat状态栏有UI提示用户Agent正在工作就行'）**

### 方案 C — 组合方案（个人倾向）

- **状态栏轻量指示** = 主展示（低频更新，例如 200 ms 一次的省略号动画 + token 计数）
- **Chat 面板**流式仍开，但**限速+累积 flush**（A 方案的机制）
- 用户可在 Settings 里选：**极速静默**（B） / **平衡**（A+C） / **实时流式**（当前，用户接受卡顿）

### 方案 D — 保底：降低单次 Repaint 成本

如果 A/B/C 都还没做，作为过渡：
- Markdown 解析改**增量/惰性**（只 parse 新 append 的部分，缓存已 parse 的段）
- Repaint 加防抖（20 ms 内合并多次调用）

---

## 待定位的具体阻塞点（真正开始修之前跑一次）

1. **递归展开 EditorLoop 子节点** — 用 `read_frame` 拉一次 Agent 流式期的完整 hierarchy（或临时把 `HierarchyFrameDataView.GetItemChildren` 递归到叶子），看 228 ms 里 UI Repaint / Markdown parse / File IO / 反射 各占多少
2. **搜代码里的 `Repaint()` / `MarkDirtyRepaint()` 调用点** — 特别是 Chat 面板 View 侧
3. **搜 LLM streaming callback 调度点** — 确认是不是每 chunk 都主线程 sync 调 UI
4. **GC 132 KB/帧 来源** — Profiler `GC.Alloc` marker 展开找主要分配者

---

## 相关能力洞察（另存）

**G02 `read_frame` 只返回根级 marker 是 API 局限**（`GetItemChildren(rootId)` 只给一层子节点）。要看 EditorLoop 内部分布，需要**递归展开**。可以作为 v1.9.0 或 v1.8.x 的 `read_frame` 增强 param：

```
manage_profiler action=read_frame frame_index=-1 depth=3 max_markers=50
```

递归实现要点：
- 递归 `GetItemChildren` 直到 depth 或叶
- 用 stack + 排序 keep top-N
- 输出成树形（含 depth 字段）

不在当前修复范围，记这里备忘。

---

## 时间线

- 2026-07-22 18:59: G02 `read_frame` 首次成功返回帧数据，暴露 228 ms/帧
- 2026-07-22 19:xx: 记录本文档，标记为 v1.8.0 后统一优化
- TBD: v1.8.0 全 P0 收官后（G02 完成、G10/G17/G18/G03 完成、发版），开始设计方案 A/B/C
