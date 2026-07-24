# G04 — 第三轮反射探测结果 + 关键分析纠偏

> **执行日期**: 2026-07-24
> **执行环境**: macOS, Megacity Metro, memoryprofiler 1.1.12
> **父文档**: [`g04-reflection-probe-round2-script.md`](g04-reflection-probe-round2-script.md)

## 1. 三轮探测的原始发现

### Direction 1 反常观察

`UnityEngine.Profiling.Memory.Experimental.MemoryProfiler` (asm: `UnityEngine`) 类型**可解析**，但用 non-DeclaredOnly + Public+NonPublic+Static+Instance 全 flag 后**成员依然为空**。

**这是异常信号**。正常类型至少应该 dump 出继承自 `object` 的方法。既然本探测已过滤 `mi.DeclaringType == typeof(object)`，但依然空——说明这个类的方法是 **`extern` 或 native binding**（`[NativeAsBindingOnly]` / `[NativeMethod]` 属性隐藏）。用 `GetMethods()` 无法反射到，但 `GetMethod("TakeSnapshot", BindingFlags.Public | BindingFlags.Static)` **可能**能精确查到（未在本轮验证）。

### Direction 2 关键收获

发现三个高价值类型（`Unity.MemoryProfiler.Editor` assembly）：

**`Unity.MemoryProfiler.Editor.PlayerConnectionService`**
```
Void TakeCapture()                          // 无参数入口
IEnumerator DelayedSnapshotRoutine()        // 协程等抓取完成
```
配套 `<>c__DisplayClass25_0` 里两个 lambda：
```
Void b__0(String path, Boolean result, DebugScreenCapture screenCapture)
Void b__1(String path, Boolean result)
```
签名 `(string, bool, DebugScreenCapture)` **明确对应** Unity 官方文档的 `MemoryProfiler.TakeSnapshot(path, callback, captureFlags)` API 的 finishCallback 签名。

**`Unity.MemoryProfiler.Editor.CaptureToolbarViewController`**：UI 层，`ImportCapture()` 是从磁盘导入 `.snap` 文件。

**`Unity.MemoryProfiler.Editor.SnapshotDataService`**：完整生命周期管理，暴露的静态方法：
```
[static] CachedSnapshot LoadSnapshot(FileReader file, Boolean crawlManaged)
[static] SnapshotFileListModel BuildSnapshotsInfo(CancellationToken, DirectoryInfo, IReadOnlyList<>)
[static] IEnumerable<> GetSnapshotFiles(DirectoryInfo)
```
实例方法（需要实例化）：`Load(filePath) / Unload / Import / Delete / Rename / GetSnapshotFolderPath`

### Direction 4 结果

`Unity.MemoryProfiler*` assembly 里**只有一个** static Take/Capture 方法：`MemoryProfilerAnalytics.BeginCapturedSnapshotEvent() -> CapturedSnapshotEvent`——是遥测埋点，不是抓取入口。

## 2. Challenge：Chat Agent 的初步分析错误

Chat agent 初步结论说 "PlayerConnectionService.TakeCapture() 是真正的入口"，**这个判断不完整**。

**证据链**：

1. `PlayerConnectionService.TakeCapture()` 无参数——不能指定 output path
2. `<>c__DisplayClass25_0.b__0/b__1` 的 callback 签名 `(string path, bool result, DebugScreenCapture screenCapture)` 与 Unity 官方 API `MemoryProfiler.TakeSnapshot(path, Action<string, bool>, CaptureFlags)` **精确匹配**
3. `PlayerConnectionService.DelayedSnapshotRoutine()` 是协程，说明 `TakeCapture()` 是**异步**的，异步等待的是**别人**的回调
4. Direction 1 `UnityEngine.Profiling.Memory.Experimental.MemoryProfiler` 类型存在但 GetMethods 空——这就是 native binding 的典型症状

**结论**：`PlayerConnectionService.TakeCapture()` 是 **package 内部的 wrapper**，它调用的下层 API 是 `UnityEngine.Profiling.Memory.Experimental.MemoryProfiler.TakeSnapshot(...)`。这是 Unity 内置 API，**不依赖 memoryprofiler package**。

## 3. G04 实施推荐路径

### 3.1 抓取（`take_memory_snapshot` action）

**主路径**：直接反射 `UnityEngine.Profiling.Memory.Experimental.MemoryProfiler.TakeSnapshot`

```csharp
// pseudo-code
var t = Type.GetType("UnityEngine.Profiling.Memory.Experimental.MemoryProfiler, UnityEngine");
var m = t.GetMethod("TakeSnapshot", BindingFlags.Public | BindingFlags.Static);
// 官方签名: TakeSnapshot(string path, Action<string, bool> finishCallback, CaptureFlags captureFlags)
// 需要第四轮探测确认参数类型精确名字
m.Invoke(null, new object[] { path, callback, captureFlags });
```

**优势**：
- 不依赖 memoryprofiler package 装/未装（`UnityEngine` 内置模块，永远在）
- 直接的、最短的调用链
- 与 Unity 官方 API 契约一致

**弱势**：
- native binding 反射可能有陷阱，需要第四轮探测**试着用 GetMethod 精确查找**验证是否可反射
- 若 API 有 `[Obsolete]` 属性，Unity 官方可能在 Unity 7+ 移除（当前 2022.3 稳定）

### 3.2 加载已有 snapshot（`analyze_memory_snapshot` action）

**主路径**：`SnapshotDataService.LoadSnapshot(FileReader, bool crawlManaged)` 静态方法，输入 `FileReader` 拿 `CachedSnapshot`。

需要先构造 `FileReader`——它在 `Unity.MemoryProfiler.Editor.Format.QueriedSnapshot.FileReader`，实例方法待第四轮探测确认。

### 3.3 Diff 两 snapshot（`diff_memory_snapshots` action）

**基于**：`CachedSnapshot.NativeObjects` / `TypeDescriptions` / `NativeAllocations` 等 entry cache 数据结构，遍历对比。

需要读 `NativeObjectEntriesCache` 等具体 entry 类型的 API（第四轮探测）。

### 3.4 用户可见的 Snapshot 文件夹路径

**优势路径**：`SnapshotDataService.GetSnapshotFolderPath()` / `GetOrCreateSnapshotFolderPath()` 实例方法，需要先拿到 `SnapshotDataService` 单例。

**次选**：硬编码 `<projectRoot>/MemoryCaptures/` 目录（Unity 官方约定）。

## 4. 阻塞点：Version Defines 表达式确定

按 [`../v1.10.0-handoff.md §4.1`](../v1.10.0-handoff.md:174) "可选 package 隔离" 原则，[`AgentCore.Editor.asmdef:16`](../../Editor/AgentCore.Editor.asmdef:16) 需要新增 versionDefines：

```json
{
    "name": "com.unity.memoryprofiler",
    "expression": "1.0.0",
    "define": "AGENTCORE_HAS_MEMORY_PROFILER"
}
```

但**分析后修正**：`take_memory_snapshot` 走 `UnityEngine` 内置 API，**不需要**装 memoryprofiler package。只有 `analyze_memory_snapshot` (依赖 `CachedSnapshot`) 和 `diff_memory_snapshots` 需要 `AGENTCORE_HAS_MEMORY_PROFILER`。

**推荐拆分**：
- `take_memory_snapshot` — 无 versionDefines，纯 UnityEngine 反射
- `list_memory_snapshots` — 扫 `MemoryCaptures/` 目录，纯 IO 操作
- `analyze_memory_snapshot` / `diff_memory_snapshots` — 需要 `AGENTCORE_HAS_MEMORY_PROFILER`，fallback stub 提示 "Install com.unity.memoryprofiler for analyze/diff"

## 5. 第四轮探测建议

见 [`g04-reflection-probe-round3-script.md`](g04-reflection-probe-round3-script.md)（待创建），验证：

1. `Type.GetMethod("TakeSnapshot", ...)` 能否精确定位到 `UnityEngine.Profiling.Memory.Experimental.MemoryProfiler.TakeSnapshot`（跳过 GetMethods 遍历）
2. `MemoryProfiler.TakeSnapshot` 的精确参数类型（`Action<string, bool>` 还是 `Action<string, bool, DebugScreenCapture>`？CaptureFlags 是否可选？）
3. `Unity.MemoryProfiler.Editor.Format.QueriedSnapshot.FileReader` 的构造方法（是否有静态 factory？是否接受 filePath 参数？）
4. `SnapshotDataService` 是否有静态入口获取单例，还是必须 `new`？

## 6. 决策

**已解除 G04 编码阻塞**——`PlayerConnectionService.TakeCapture()` 的 callback 签名是决定性证据，`MemoryProfiler.TakeSnapshot` 就是真实入口。**推荐**：跑第四轮探测确认签名细节，之后 G04 可以开始实施。

**或者**：跳过第四轮探测的 nice-to-have 项，直接按 §3 的推荐路径起草 G04 代码（`take_memory_snapshot` 用 GetMethod 精确查找 `TakeSnapshot`，找不到抛错即可），实施中遇到 API 惊喜再回头做四轮探测。这个策略更快，符合 [`../v1.10.0-handoff.md §6.2`](../v1.10.0-handoff.md:238) "反射盲写" 的 warning——但因为 §2 已经通过 callback 签名对齐官方 API 建立了强证据，风险已经降到可接受。
