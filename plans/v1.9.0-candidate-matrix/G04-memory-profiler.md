# G04 — MemoryProfiler snapshot / diff

> **领域**: Profiler / P1
> **状态**: 起草
> **审计日期**: 2026-07-23

## 1. 场景推演

- **S1**: 用户报"运行到某个关卡后内存暴涨到 3 GB, 找出是谁在吃". Agent 应能: 抓一份 memory snapshot → 按 category (Native/Managed/GfxDriver/Audio/Video) 汇总 → 找 Top-N 分配点 (类型 / 大小 / 引用来源).
- **S2**: 用户报"关卡卸载后内存不掉, 疑似泄漏". Agent 应能: 关卡加载前抓 snapshot A → 加载卸载后抓 snapshot B → diff 出仅在 B 存在 (未回收) 的对象, 按类型分组, 定位泄漏源.
- **S3**: 用户报"Texture 内存占用 800 MB 想减到 500 MB, 该压哪些". Agent 应能: 从 snapshot 中枚举所有 Texture2D/RenderTexture, 按大小排序, 关联到 asset path (哪张贴图) 和引用它的组件.
- **S4**: 用户报"Domain reload 后 static 字段还残留, 排查 root". Agent 应能: 拉 managed heap type=X 全实例 + 每个实例的 GC root chain (被谁引用).
- **S5**: 用户不装 MemoryProfiler package. Agent 应能: 优雅降级返回 "MemoryProfiler package not detected — install `com.unity.memoryprofiler` via Package Manager".

## 2. Unity API 表

**主要 API** (通过 `com.unity.memoryprofiler` 包提供):

| API | 命名空间 | 用途 |
|---|---|---|
| `MemoryProfiler.TakeSnapshot(path, callback, captureFlags)` | `UnityEngine.Profiling.Memory.Experimental` (旧) / `Unity.MemoryProfiler.EditorUI` (新) | 抓 snapshot 到 .snap 文件 |
| `CaptureFlags` | 同上 | ManagedObjects / NativeObjects / NativeAllocations / NativeAllocationSites / NativeStackTraces |
| `PackedMemorySnapshot.Load(path)` | `UnityEditor.Profiling.Memory.Experimental` | 读取 .snap |
| `CachedSnapshot` (Unity.MemoryProfiler internal) | `Unity.MemoryProfiler.Editor` | 高层查询 API, 但**主要 API 是 internal** |

**关键坑**:
- MemoryProfiler package 有**两代 API**: `UnityEngine.Profiling.Memory.Experimental` (Unity 2018+ 内置) 和 `Unity.MemoryProfiler.Editor` (com.unity.memoryprofiler 独立包, ≥ 1.0.0). 前者只能 snapshot, 后者有 diff / GC root chain 查询. **v1.9.0 目标应对齐 com.unity.memoryprofiler 1.1+ (Megacity Metro 已装 1.1.1)**.
- 大部分高级查询 API 是 **internal**, 需**反射** (同 G03 FrameDebugger 模式).
- Snapshot 抓取是**异步**, `TakeSnapshot(path, callback)` 回调返回, callback 里能拿到 `PackedMemorySnapshot` 对象.

**Version Defines 需求**: 加入 asmdef `versionDefines`:
```json
{ "name": "com.unity.memoryprofiler", "expression": "1.0.0", "define": "AGENTCORE_HAS_MEMORY_PROFILER" }
```
代码分支同 G10 SRP 模式.

## 3. 现有覆盖诊断

`grep -E "MemoryProfiler|Memory\.Experimental" Editor/` 结果:

- [`ManageProfilerTool.HandleGetMemory`](../../Editor/Tools/Native/Extended/ManageProfilerTool.cs): 只用 `Profiler.GetTotalAllocatedMemoryLong()` 等**运行时聚合 API**, 得到 Native/Managed/GfxDriver 三个数值. **不涉及** MemoryProfiler package.
- 47 工具中**零处** `Unity.MemoryProfiler` 或 `MemoryProfiler.TakeSnapshot`.

**根因分类**: `NO_TOOL` — 完全没有 snapshot 级别的能力.

**与 G01/G02 的边界**: G01 (`sample_recorder`) / G02 (`read_frame`) 走 ProfilerRecorder / ProfilerDriver, 面向 CPU 时序分析. G04 走 MemoryProfiler, 面向内存对象图分析. 两条独立线, 不要合并到一个 action.

## 4. 建议 action 接口

**归属工具**: `manage_profiler` (深化, 与 G01/G02 保持同工具; 因为都是 "Profiler 家族" 语义).

**新增 4 个 action**:

### 4.1 `take_memory_snapshot`
```json
{
  "action": "take_memory_snapshot",
  "path": "Assets/../MemorySnapshots/snap_20260723.snap",  // 可选, 默认 Temp/
  "capture_flags": ["ManagedObjects", "NativeObjects", "NativeAllocations"],  // 可选默认全
  "screenshot": false  // 是否额外抓 GameView 截图 (MemoryProfiler 支持)
}
```
返回: `{ path, size_bytes, capture_flags, unity_version, captured_at }`.
**同步/异步**: TakeSnapshot 是异步, 但用户视角应等到 callback 完成才返 result. Handler 内部 `EditorApplication.update` 或 `Task.CompletionSource` 等待 callback, 超时 30s. Play Mode 中调用**不阻塞主线程** (MemoryProfiler 抓取是 native side, 主线程会短暂 stall 但不 dead-lock).

### 4.2 `list_memory_snapshots`
```json
{ "action": "list_memory_snapshots", "directory": "Assets/../MemorySnapshots/" }
```
返回: `[{ path, size_bytes, unity_version, captured_at }]`. 用于 diff 前找目标 snapshot.

### 4.3 `analyze_memory_snapshot`
```json
{
  "action": "analyze_memory_snapshot",
  "path": "Assets/../MemorySnapshots/snap_20260723.snap",
  "group_by": "type" | "category" | "asset_path",  // 默认 category
  "top_n": 50,
  "filter_min_size_kb": 100  // 只看 ≥ 100 KB 的
}
```
返回: `[{ group_key, count, total_bytes, top_instances: [{ address, type, size, name?, asset_path? }] }]`. 反射 `Unity.MemoryProfiler.Editor.CachedSnapshot` 提取 native object / managed object 表.

### 4.4 `diff_memory_snapshots`
```json
{
  "action": "diff_memory_snapshots",
  "before": "Assets/../snap_before.snap",
  "after": "Assets/../snap_after.snap",
  "group_by": "type" | "asset_path",
  "direction": "only_in_after" | "only_in_before" | "size_delta"  // 泄漏排查用 only_in_after
}
```
返回: `[{ group_key, count_delta, size_delta_bytes, sample_instances: [...] }]`. 核心泄漏定位能力.

## 5. 前置依赖

- **可选 package**: `com.unity.memoryprofiler ≥ 1.0.0`. Version Defines `AGENTCORE_HAS_MEMORY_PROFILER` 隔离.
- **反射**: `Unity.MemoryProfiler.Editor.CachedSnapshot` 及其查询 API. 需先跑一次反射探测脚本 (类似 [`HANDOFF §4.4`](../HANDOFF-v1.8.0-to-v1.9.0.md)) 落地 assembly 名 + 关键方法签名. 风险: 该 package 版本间 internal API 会漂移, 需锁定 1.1.x.
- **Undo**: 不涉及 (只读操作).
- **Play Mode**: 抓 snapshot 允许 Play Mode 中调用 (实际排查场景就是运行中). Analyze / diff 是纯离线 .snap 解析, 与 Play Mode 无关.
- **磁盘占用**: 大项目一份 snapshot 可达 500 MB - 2 GB. 默认存放 Temp/ 而非 Assets/, 避免污染工程. 需要 SOUL 提示 agent "老 snapshot 用完请手动删".

## 6. 投入估算

**保守估**: 2-3 天.

- 半天: 反射探测 + CachedSnapshot API 通路验证
- 半天: `take_memory_snapshot` 实现 + 异步 callback 桥接
- 半天: `list_memory_snapshots` + `analyze_memory_snapshot`
- 半天到一天: `diff_memory_snapshots` (最复杂, 需按 address 对齐两份 snapshot 的对象表, 常见做法是 Dictionary<ulong, MemoryObject>)
- 半天: 结构化返回 + SOUL 引导 + version defines fallback stub + 单元测试

**风险点**:
- MemoryProfiler package internal API 版本漂移 (mitigation: 锁 `1.0.0` 为最低, 反射断言签名, 失败输出结构化 hint)
- Snapshot 抓取时机: Play Mode 中 vs Edit Mode 中差异. Play Mode 快照包含运行时对象, Edit Mode 只有 Editor 侧. 需在 action Description 里明写.

## 7. 优先级建议

**P1 高**. 场景 S1/S2/S3 都是用户实际排查中"必需能力", 没有 execute_code workaround (MemoryProfiler API 太复杂, execute_code 单块无法完成). 是 v1.9.0 头号候选.
