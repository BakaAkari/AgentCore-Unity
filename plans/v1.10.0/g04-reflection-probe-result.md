# G04 — MemoryProfiler 反射探测结果

> **首次执行**: 2026-07-24 (Windows, `D:/Unity Project/unity-agent`, memoryprofiler 未装)
> **二次执行**: 2026-07-24 (macOS, Megacity Metro, **memoryprofiler 1.1.12 已装**)
> **父规划**: [`g04-reflection-probe-script.md`](g04-reflection-probe-script.md)
> **下游文档**: [`g04-reflection-probe-round2-script.md`](g04-reflection-probe-round2-script.md)（TakeSnapshot 入口定位）

## 1. 关键结论

**Megacity Metro 装了 `com.unity.memoryprofiler` 1.1.12**（[`Packages/manifest.json:17`](../../../../Packages/manifest.json:17)），二次探测在其上跑，结果如下：

**已确认**：
- `Unity.MemoryProfiler.Editor.CachedSnapshot` (asm: `Unity.MemoryProfiler.Editor`) **可解析**，暴露完整数据模型（20+ 属性 / 30+ 字段），足够支撑 `analyze_memory_snapshot` 和 `diff_memory_snapshots` action
- `Unity.MemoryProfiler.Editor.MemoryProfilerWindow` **可解析**，但只有 UI 方法，无 capture 入口
- 3 个 assembly 已加载：`Unity.MemoryProfiler` / `Unity.MemoryProfiler.Editor` / `Unity.MemoryProfiler.Editor.MemoryProfilerModule`

**未确认（阻塞 G04 编码）**：
- **`TakeSnapshot` 方法未定位**。规划期望的 `Unity.MemoryProfiler.Editor.MemoryProfilerModuleBridge` NULL（类名漂移），`UnityEngine.Profiling.Memory.Experimental.MemoryProfiler` 首轮探针没 dump 出成员（可能因为 `DeclaredOnly` flag 只看 declared 层，成员在基类里）
- **抓取入口可能藏在这些地方之一**：
  - `Unity.MemoryProfiler` runtime assembly（未被首轮探测覆盖）
  - `SnapshotDataService`（`MemoryProfilerWindow.SnapshotDataService` 属性类型）
  - `UnityEngine.Profiling.Memory.Experimental.MemoryProfiler`（去掉 DeclaredOnly 后可能有 `TakeSnapshot` 静态方法）

## 2. 探测明细

### 2.1 类型解析对照表（首轮 vs 二轮）

| 类型（typeName, assembly hint） | 首轮（无 package） | 二轮（memoryprofiler 1.1.12） |
|---|---|---|
| `Unity.MemoryProfiler.Editor.CachedSnapshot, Unity.MemoryProfiler.Editor` | NULL | **✅ Resolved** |
| `Unity.MemoryProfiler.Editor.MemoryProfilerModuleBridge, Unity.MemoryProfiler.Editor` | NULL | NULL（类名漂移） |
| `Unity.MemoryProfiler.Editor.MemoryProfilerWindow, Unity.MemoryProfiler.Editor` | NULL | **✅ Resolved** |
| `UnityEngine.Profiling.Memory.Experimental.MemoryProfiler, UnityEngine.Profiling.MemoryProfiler.Module` | NULL | NULL |
| `UnityEngine.Profiling.Memory.Experimental.MemoryProfiler, UnityEngine` | ✅ Resolved (无成员) | ✅ Resolved (无成员，DeclaredOnly 过滤) |
| `UnityEditor.Profiling.Memory.Experimental.PackedMemorySnapshot, UnityEditor.Profiling.MemoryProfiler.Module` | NULL | NULL |
| `UnityEditor.Profiling.Memory.Experimental.PackedMemorySnapshot, UnityEditor` | ✅ Resolved (asm: UnityEditor.CoreModule) | ✅ Resolved (相同) |

### 2.2 二轮已解析类型（memoryprofiler 1.1.12 真实签名）

**`Unity.MemoryProfiler.Editor.CachedSnapshot`** (asm: `Unity.MemoryProfiler.Editor`)

关键方法：
```
Void SetUnityVersionSpecificFlags()
Boolean UnityVersionHasPrefabRootInfo()
Void CacheNativeAllocationNames()
IEnumerator<T> PostProcess(Boolean crawlManaged)
Int64 ManagedObjectIndexToUnifiedObjectIndex(Int64 i)
Int64 NativeObjectIndexToUnifiedObjectIndex(Int64 i)
Int32 UnifiedObjectIndexToManagedObjectIndex(Int64 i)
Int32 UnifiedObjectIndexToNativeObjectIndex(Int64 i)
NativeAllocationOrRegionSearchResult FindNativeAllocationOrRegion(UInt64 pointer, out SourceIndex, out SourceIndex, [out String, [bool]])  // 3 overloads
Void Dispose()
[static] Void ConvertDynamicArrayByteBufferToManagedArray<T>(DynamicArray<T> nativeEntryBuffer, out T[] elements)
```

关键属性（get-only 除非标注）：
```
Boolean Valid, HasConnectionOverhaul, HasTargetAndMemoryInfo, HasMemoryLabelSizesAndGCHeapTypes
Boolean HasSceneRootsAndAssetbundles, HasGfxResourceReferencesAndAllocators
Boolean HasNativeObjectMetaData, HasSystemMemoryRegionsInfo, HasSystemMemoryResidentPages
Boolean HasEntityIDAs8ByteStructs, HasPrefabRootInfo
SnapshotMetrics LastMetrics {get/set}
ManagedData CrawledData {get/set}
MetaData MetaData {get/set}
DateTime TimeStamp {get/set}
ref VirtualMachineInformation VirtualMachineInformation {get}
String FullPath {get}
Boolean UseDeviceMemoryForGraphics {get}
Int32 PostProcessStepCountWithCrawler {get}
Int32 PostProcessStepCountWithoutCrawler {get}
```

关键 entry cache 字段（用于 analyze/diff）：
```
NativeAllocationSites, TypeDescriptions, NativeTypes, NativeRootReferences,
NativeObjects, NativeMemoryRegions, NativeMemoryLabels, NativeCallstackSymbols,
NativeAllocations, ManagedStacks, ManagedHeapSections, GcHandles, FieldDescriptions,
Connections, SortedNativeRegionsEntries, SortedManagedObjects, SortedNativeAllocations,
SortedNativeObjects, SceneRoots, NativeAllocators, NativeGfxResourceReferences,
SystemMemoryRegions, SystemMemoryResidentPages, EntriesMemoryMap,
ProcessedNativeRoots, RootAndImpactInfo
```

静态常量：`InvalidItemName / UnrootedItemName / UnknownMemlabelName / RootName`

**`Unity.MemoryProfiler.Editor.MemoryProfilerWindow`** (asm: `Unity.MemoryProfiler.Editor`)
```
[static] Void ShowWindow()
Void OnEnable() / OnDisable() / OnInitContainerGUI() / Init() / CreateGUI()
P PlayerConnectionService PlayerConnectionService {get}
P SnapshotDataService SnapshotDataService {get}          // ← 候选 TakeSnapshot 藏身处
P MemoryProfilerViewController ProfilerViewController {get}
```

**`UnityEditor.Profiling.Memory.Experimental.PackedMemorySnapshot`** (asm: `UnityEditor.CoreModule`) — 与首轮探测结果一致，内置 legacy API

**静态方法**：
```
PackedMemorySnapshot Load(String path)
Boolean Convert(PackedMemorySnapshot snapshot, String writePath)
Void Save(PackedMemorySnapshot snapshot, String writePath)
Int32 ReadIntFromByteArray(Byte[] array, Int32 offset, Int32& value)
Int32 ReadStringFromByteArray(Byte[] array, Int32 offset, Int32 stringLength, String& value)
```

**实例方法**：
```
Void BuildEntries()
MemorySnapshotFileReader GetReader()
Void Dispose() / Dispose(Boolean disposing) / Finalize()
```

**核心属性（14 个 entries 类型）**：
```
ConnectionEntries          connections
FieldDescriptionEntries    fieldDescriptions
GCHandleEntries            gcHandles
ManagedMemorySectionEntries managedHeapSections
ManagedMemorySectionEntries managedStacks
NativeAllocationEntries    nativeAllocations
NativeAllocationSiteEntries nativeAllocationSites
NativeCallstackSymbolEntries nativeCallstackSymbols
NativeMemoryLabelEntries   nativeMemoryLabels
NativeMemoryRegionEntries  nativeMemoryRegions
NativeObjectEntries        nativeObjects
NativeRootReferenceEntries nativeRootReferences
NativeTypeEntries          nativeTypes
TypeDescriptionEntries     typeDescriptions
```

**元数据属性（6 个 read-only）**：
```
UInt32 version
MemorySnapshotMetadata metadata
String filePath
DateTime recordDate
CaptureFlags captureFlags
VirtualMachineInformation virtualMachineInformation
```

### 2.3 关键 gap：TakeSnapshot 入口未定位

首轮 + 二轮探测都**没有**在任何已 dump 类型里发现 `TakeSnapshot(...)` 方法：

- `PackedMemorySnapshot` (UnityEditor.CoreModule) 只有 `Load / Save / Convert`，**无 TakeSnapshot**
- `CachedSnapshot` (Unity.MemoryProfiler.Editor) 只有 `PostProcess / Dispose` 等处理方法，**无 TakeSnapshot**
- `MemoryProfilerWindow` 只有 UI 方法
- `MemoryProfilerModuleBridge` 类名不存在
- `UnityEngine.Profiling.Memory.Experimental.MemoryProfiler` 类型可解析但成员未 dump 出（`DeclaredOnly` flag 可能过滤掉了继承成员，或该类型成员被 `[Obsolete]` 隐藏）

**需要三轮探测**才能定位真实 capture 入口。候选方向：
1. 去掉 `DeclaredOnly` 重探 `UnityEngine.Profiling.Memory.Experimental.MemoryProfiler`（Unity 官方文档确实有 `MemoryProfiler.TakeSnapshot(path, finishCallback)` 签名，但可能被 flag 过滤）
2. 探 `SnapshotDataService` 类型（`MemoryProfilerWindow.SnapshotDataService` 属性）
3. 遍历 `Unity.MemoryProfiler` runtime assembly 所有公开类型

见 [`g04-reflection-probe-round2-script.md`](g04-reflection-probe-round2-script.md)。

## 3. 决策矩阵（已确认路径 A，本节保留供未来参考）

| 路径 | 覆盖 v1.10.0 规划 §3.4 期望 | 用户成本 | 未来风险 |
|---|---|---|---|
| ~~**A**~~ **已选**: 用户装 `com.unity.memoryprofiler` package，重跑探针拿现代 API 签名 | ✅ CachedSnapshot 已确认；⚠️ TakeSnapshot 入口需三轮探测 | 一次 package install（Megacity Metro 已装） | Package 版本 API 稳定性历史良好，低 |
| ~~**B**~~: 只用内置 legacy Experimental API | ⚠️ 只能读，不能抓 snapshot | 无 | Experimental namespace Unity 未来可能移除，中高 |
| ~~**C**~~: G04 挪出 v1.10.0 到 v1.11.0 | ❌ 不覆盖 | 无 | v1.10.0 缩水到 5 项 |

## 4. 后续步骤

1. **[阻塞 G04 编码] 跑第三轮探测**（[`g04-reflection-probe-round2-script.md`](g04-reflection-probe-round2-script.md)），定位 `TakeSnapshot` 入口
2. **[结果落地] 输出 g04-reflection-probe-round2-result.md**，包含真实抓取 API 签名
3. **[实施 G04]** 按真实签名写 [`../v1.9.0-candidate-matrix/G04-memory-profiler.md`](../v1.9.0-candidate-matrix/G04-memory-profiler.md) §4 的 4 个 action，用 asmdef `versionDefines` `AGENTCORE_HAS_MEMORY_PROFILER`（触发表达式 `com.unity.memoryprofiler: 1.0.0`）+ fallback stub

## 5. 探测执行备注

- **macOS + Megacity Metro** 环境的 AgentCore Chat **execute_code 直接可用**（激活 Scripting 类别后），比 Windows 环境体验更好
- 首轮探针在无 package 环境跑出的 `PackedMemorySnapshot` 结果在 Megacity Metro 上依然可复现（`UnityEditor.CoreModule` 内置），说明它与 memoryprofiler package 独立
- CS1685 warning（`System.Runtime.CompilerServices.DynamicAttribute` 多重定义）与本探测无关，是 Mono.CSharp 环境的已知 assembly 冲突
