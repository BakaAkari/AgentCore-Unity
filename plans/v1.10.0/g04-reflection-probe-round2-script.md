# G04 — MemoryProfiler 反射探测第三轮（TakeSnapshot 入口定位）

> **日期**: 2026-07-24
> **执行者**: 用户，在 Megacity Metro 的 AgentCore Chat 里跑
> **父文档**: [`g04-reflection-probe-result.md §2.3`](g04-reflection-probe-result.md)
> **目的**: 定位真实的 `TakeSnapshot(...)` 入口 API，解除 G04 编码阻塞

## 背景

首轮 + 二轮探测已确认 `CachedSnapshot` 数据模型可用，但**没找到抓取新 snapshot 的入口方法**。三个候选方向需要探明：

1. `UnityEngine.Profiling.Memory.Experimental.MemoryProfiler` 用 non-DeclaredOnly flags 重探（Unity 官方文档记载 `TakeSnapshot(path, callback)` 静态方法，首轮 flag 可能过滤）
2. `Unity.MemoryProfiler.Editor.MemoryProfilerModule` assembly 里所有类型（首轮未覆盖，可能藏 bridge）
3. `SnapshotDataService`（`MemoryProfilerWindow.SnapshotDataService` 属性的类型）

## 探测脚本

**执行方式**：AgentCore Chat 里直接把下面这段丢给 agent，让它走 `execute_code` 跑，输出复制回来。

```csharp
var sb = new System.Text.StringBuilder();
sb.AppendLine("=== Round 3: TakeSnapshot entry point probe ===");
var flagsAll = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
             | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance;

System.Action<System.Type> dumpType = (t) => {
    if (t == null) { sb.AppendLine("  <NULL>"); return; }
    sb.AppendLine("  Type: " + t.FullName + " (asm: " + t.Assembly.GetName().Name + ")");
    foreach (var mi in t.GetMethods(flagsAll)) {
        if (mi.IsSpecialName) continue;
        if (mi.DeclaringType == typeof(object)) continue;
        var ps = mi.GetParameters();
        var paramStr = string.Join(", ", System.Array.ConvertAll(ps, p => p.ParameterType.Name + " " + p.Name));
        sb.AppendLine("    M " + (mi.IsStatic ? "[static] " : "") + mi.ReturnType.Name + " " + mi.Name + "(" + paramStr + ")  from " + mi.DeclaringType.Name);
    }
    foreach (var evt in t.GetEvents(flagsAll)) {
        sb.AppendLine("    E " + evt.EventHandlerType.Name + " " + evt.Name);
    }
    foreach (var nt in t.GetNestedTypes(flagsAll)) {
        sb.AppendLine("    N " + nt.Name);
    }
};

// ─── Direction 1: UnityEngine.Profiling.Memory.Experimental.MemoryProfiler (all inheritance) ───
sb.AppendLine("\n### Direction 1: UnityEngine.Profiling.Memory.Experimental.MemoryProfiler ###");
dumpType(System.Type.GetType("UnityEngine.Profiling.Memory.Experimental.MemoryProfiler, UnityEngine"));

// ─── Direction 2: Sweep all types in memoryprofiler assemblies matching 'Snapshot' or 'Capture' ───
sb.AppendLine("\n### Direction 2: All Unity.MemoryProfiler* types matching 'Snapshot|Capture|Take|Bridge|Service' ###");
foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies()) {
    var asmName = asm.GetName().Name;
    if (asmName.IndexOf("MemoryProfiler", System.StringComparison.OrdinalIgnoreCase) < 0) continue;
    sb.AppendLine("--- Assembly: " + asmName + " ---");
    System.Type[] types;
    try { types = asm.GetTypes(); }
    catch (System.Reflection.ReflectionTypeLoadException ex) { types = ex.Types; }
    foreach (var t in types) {
        if (t == null) continue;
        var name = t.FullName ?? t.Name;
        if (System.Text.RegularExpressions.Regex.IsMatch(name, "Snapshot|Capture|Take|Bridge|Service|MemoryProfilerModule", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) {
            sb.AppendLine("  [candidate] " + name);
            foreach (var mi in t.GetMethods(flagsAll)) {
                if (mi.IsSpecialName) continue;
                if (mi.DeclaringType != t) continue;
                var lname = mi.Name.ToLowerInvariant();
                if (lname.Contains("take") || lname.Contains("capture") || lname.Contains("snapshot")) {
                    var ps = mi.GetParameters();
                    var paramStr = string.Join(", ", System.Array.ConvertAll(ps, p => p.ParameterType.Name + " " + p.Name));
                    sb.AppendLine("    M " + (mi.IsStatic ? "[static] " : "") + mi.ReturnType.Name + " " + mi.Name + "(" + paramStr + ")");
                }
            }
        }
    }
}

// ─── Direction 3: Full dump of SnapshotDataService if we can find it ───
sb.AppendLine("\n### Direction 3: SnapshotDataService full dump ###");
System.Type sdsType = null;
foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies()) {
    if (asm.GetName().Name.IndexOf("MemoryProfiler", System.StringComparison.OrdinalIgnoreCase) < 0) continue;
    System.Type[] types;
    try { types = asm.GetTypes(); } catch (System.Reflection.ReflectionTypeLoadException ex) { types = ex.Types; }
    foreach (var t in types) {
        if (t != null && t.Name == "SnapshotDataService") { sdsType = t; break; }
    }
    if (sdsType != null) break;
}
dumpType(sdsType);

// ─── Direction 4: List all static methods in Unity.MemoryProfiler* named-like Take/Capture ───
sb.AppendLine("\n### Direction 4: All [static] methods across Unity.MemoryProfiler* asms matching 'Take|Capture' ###");
foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies()) {
    var asmName = asm.GetName().Name;
    if (asmName.IndexOf("MemoryProfiler", System.StringComparison.OrdinalIgnoreCase) < 0) continue;
    System.Type[] types;
    try { types = asm.GetTypes(); } catch (System.Reflection.ReflectionTypeLoadException ex) { types = ex.Types; }
    foreach (var t in types) {
        if (t == null) continue;
        foreach (var mi in t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)) {
            if (mi.IsSpecialName) continue;
            if (mi.DeclaringType != t) continue;
            var lname = mi.Name.ToLowerInvariant();
            if (lname.Contains("take") || lname.Contains("capture")) {
                var ps = mi.GetParameters();
                var paramStr = string.Join(", ", System.Array.ConvertAll(ps, p => p.ParameterType.Name + " " + p.Name));
                sb.AppendLine("  [static] " + t.FullName + "." + mi.Name + "(" + paramStr + ") -> " + mi.ReturnType.Name);
            }
        }
    }
}

UnityEngine.Debug.Log(sb.ToString());
sb.ToString()
```

## 期望输出

**关键成功信号**：
- Direction 1 dump 出 `TakeSnapshot(String path, ...)` 静态方法 → 用官方 legacy API 抓取
- Direction 2/4 找到类似 `Unity.MemoryProfiler.Editor.XxxService.TakeCapture` 或 `MemoryProfilerModuleBridge` 的替代类型 → 用 package 内部 bridge 抓取
- Direction 3 dump 出 `SnapshotDataService` 内的 `TakeCapture / RequestSnapshot / StartCapture` 类方法 → 走 window service 层

**输出保存**：粘回来后我落地到 `g04-reflection-probe-round2-result.md`，然后写 G04 实施方案。

## 安全备注

- 探测仍是纯反射，无副作用
- 若某 assembly 触发 `ReflectionTypeLoadException`，脚本已捕获用 `ex.Types` 兜底
- CS1685 warning 可忽略（`System.Runtime.CompilerServices.DynamicAttribute` 多重定义是 Mono.CSharp 环境的已知冲突，不影响反射结果）
