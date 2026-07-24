# G04 — MemoryProfiler 反射探测脚本

> **日期**: 2026-07-24
> **执行者**: 用户（需要在 Unity Editor 里 AgentCore Chat 通过 `execute_code` 跑）
> **依据**: [`../v1.10.0-handoff.md`](../v1.10.0-handoff.md) §3.4
> **父规划**: [`../v1.9.0-candidate-matrix/G04-memory-profiler.md`](../v1.9.0-candidate-matrix/G04-memory-profiler.md)

## 目的

在动手写 G04 tool handler 之前，探明 `com.unity.memoryprofiler` package 的实际暴露的类型与方法签名。**如果探测失败** (类型 NULL / package 未装)，G04 挪出 v1.10.0 范围，避免历史事故重演（v1.7.26/27 反射盲写、v1.8.0 G02 双开关翻车）。

## 执行步骤

1. 打开 Unity Editor（Megacity Metro 项目）
2. 打开 AgentCore Chat 窗口 (`Ctrl+Shift+Q` / `Cmd+Shift+Q`)
3. 输入以下自然语言指令让 agent 帮跑：

   > 用 execute_code 跑下面这段 C#，把完整 stdout 输出粘贴回来。

4. 复制以下 C# 到对话，让 agent 走 `execute_code` 工具执行
5. 输出保存到本目录同名 `g04-reflection-probe-result.md`

## 探测脚本

```csharp
var sb = new System.Text.StringBuilder();
sb.AppendLine("=== Unity Version: " + UnityEngine.Application.unityVersion + " ===");
sb.AppendLine("=== Package probe: com.unity.memoryprofiler ===");

// 探测所有可能的 assembly 名与类型 (Unity 版本漂移覆盖)
string[] typeNames = new string[] {
    "Unity.MemoryProfiler.Editor.CachedSnapshot, Unity.MemoryProfiler.Editor",
    "Unity.MemoryProfiler.Editor.MemoryProfilerModuleBridge, Unity.MemoryProfiler.Editor",
    "Unity.MemoryProfiler.Editor.MemoryProfilerWindow, Unity.MemoryProfiler.Editor",
    "UnityEngine.Profiling.Memory.Experimental.MemoryProfiler, UnityEngine.Profiling.MemoryProfiler.Module",
    "UnityEngine.Profiling.Memory.Experimental.MemoryProfiler, UnityEngine",
    "UnityEditor.Profiling.Memory.Experimental.PackedMemorySnapshot, UnityEditor.Profiling.MemoryProfiler.Module",
    "UnityEditor.Profiling.Memory.Experimental.PackedMemorySnapshot, UnityEditor",
};

var flags = System.Reflection.BindingFlags.Public
          | System.Reflection.BindingFlags.NonPublic
          | System.Reflection.BindingFlags.Static
          | System.Reflection.BindingFlags.Instance
          | System.Reflection.BindingFlags.DeclaredOnly;

foreach (var tn in typeNames)
{
    var t = System.Type.GetType(tn);
    if (t == null)
    {
        sb.AppendLine("NULL: " + tn);
        continue;
    }
    sb.AppendLine("=========== " + t.FullName + " (asm: " + t.Assembly.GetName().Name + ") ===========");

    foreach (var mi in t.GetMethods(flags))
    {
        if (mi.IsSpecialName) continue;
        var ps = mi.GetParameters();
        var paramStr = string.Join(", ", System.Array.ConvertAll(ps, p => p.ParameterType.Name + " " + p.Name));
        sb.AppendLine("  M " + (mi.IsStatic ? "[static] " : "") + mi.ReturnType.Name + " " + mi.Name + "(" + paramStr + ")");
    }

    foreach (var pi in t.GetProperties(flags))
    {
        sb.AppendLine("  P " + pi.PropertyType.Name + " " + pi.Name +
                      (pi.CanRead ? " {get" + (pi.CanWrite ? "/set" : "") + "}" : ""));
    }

    foreach (var fi in t.GetFields(flags))
    {
        sb.AppendLine("  F " + (fi.IsStatic ? "[static] " : "") + fi.FieldType.Name + " " + fi.Name);
    }
}

// 补测：所有已加载 assembly 名，找 MemoryProfiler 相关
sb.AppendLine("=========== Assemblies matching 'MemoryProfiler' ===========");
foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
{
    var name = asm.GetName().Name;
    if (name.IndexOf("MemoryProfiler", System.StringComparison.OrdinalIgnoreCase) >= 0)
        sb.AppendLine("  ASM: " + asm.GetName().FullName);
}

UnityEngine.Debug.Log(sb.ToString());
return sb.ToString();
```

## 期望输出

**成功情形**：至少一条 `Unity.MemoryProfiler.Editor.CachedSnapshot` 或等价类型 non-NULL，且包含类似 `TakeSnapshot / Load / SaveSnapshot` 的方法名与签名。

**失败情形**（G04 挪 v1.11.0）：
- 所有 typeNames NULL → package 未装（`Package Manager > Memory Profiler > Install` 后重试）
- 类型解析成功但方法签名与 [`G04-memory-profiler.md`](../v1.9.0-candidate-matrix/G04-memory-profiler.md) 假设完全不匹配 → 需要重新设计 tool 接口，超出 v1.10.0 时间盒

## 决策分支

| 输出结果 | 决策 |
|---|---|
| `CachedSnapshot` non-NULL + `TakeSnapshot(...)` 方法存在 | ✅ G04 保留在 v1.10.0 步骤 7，按 [`G04-memory-profiler.md`](../v1.9.0-candidate-matrix/G04-memory-profiler.md) 实施 |
| `CachedSnapshot` non-NULL 但方法签名漂移 | ⚠️ 用实际签名更新规划，估工 +1 天 |
| 全 NULL | ❌ G04 挪 v1.11.0，v1.10.0 只做 G05-G09 五项 |

## 安全备注

- 探测脚本纯反射读取，**零副作用**（不 TakeSnapshot 不写文件）
- 输出可能很长（几百行），若 execute_code 输出截断，用 `sb.ToString().Substring(0, ...)` 分段读
