# Playmode 运行时状态修改方案（ModifyRuntimeState 能力位）

**提案版本**: v0.1-draft  
**提案日期**: 2026-07-30  
**当前状态**: 架构设计阶段  
**决策人**: 用户确认后进入实施

---

## 一、诉求精确定义

### 1.1 用户核心诉求（澄清后版本）

> 在 Playmode 中允许 Agent 修改**运行时内存对象**（不落盘），使 Agent 能在游戏运行状态下动态调试、验证假设、试验修复方案。修改行为在退出 Playmode 时**自然消失**（Unity 原生行为），无需人工回滚。

### 1.2 与前一版方案（写磁盘）的本质区别

| 维度 | 前版方案（写磁盘） | 本方案（写内存） |
|---|---|---|
| **修改对象** | `Assets/*.cs` `.unity` `.asset` 磁盘文件 | 运行时 `GameObject.transform.position` / `Component.field` |
| **持久化** | 修改立即落盘 | 内存中生效，退出 Playmode 自动丢失 |
| **风险来源** | 磁盘/运行时分叉、崩溃后脏数据 | Agent 误操作影响当前会话（可通过重进 Playmode 清除） |
| **回滚成本** | 需 VCS/快照/复杂 checkpoint | 免费：Unity 原生 Playmode 结束就还原 |
| **参照** | 类似"直接改源码" | 类似"在 Play 中拖动 Inspector 上的字段" |

**这是 Unity 30 年成熟且合法的模式**：Play 中改 Inspector → 值临时生效 → 退出后还原。

### 1.3 目标使用场景

Agent 应能在 Playmode 中做的事：

1. **调平衡**：`vehicle.speed = 15` 试试；不行改回来 `= 10`
2. **诊断 bug**：`enemy.health = 0` 看死亡逻辑触发正常吗；`player.position = Vector3(0,100,0)` 看下坠是否死亡
3. **验证假设**：Agent 看到"NullReference on line 42"，读代码发现是 `_target == null`，直接改 `manager._target = someGO` 看能不能修
4. **实时观察**：改 `Camera.fov` `Light.intensity` 看画面变化，用于视觉调优
5. **动态生成**：`Instantiate` 一堆 enemy prefab 测压力测试
6. **移动 SO 引用**：改 `weaponConfig.damage` 试新数值（**关键：不落盘**）

Agent **不应做的事**：
- 保存场景（SaveScene）
- 保存 ScriptableObject 到磁盘（SaveAssets）
- 修改 `.cs` 源码文件（Playmode 中改代码不生效，纯浪费）
- 修改 EditorPrefs / ProjectSettings（Editor 级配置，不应受 Play 影响）

---

## 二、Unity 底层机制事实核对

### 2.1 什么"自然"就是内存操作（Playmode 结束自动清理）

Unity 的**核心行为规律**：进入 Playmode 时，Unity 对场景做序列化快照；退出时反序列化恢复。运行时对 `UnityEngine.Object` 的修改**只影响内存实例**，除非显式落盘。

**内存操作 API 列表**（这些在 Playmode 中改，退出后自动还原）：
```csharp
// GameObject / Component 直接字段访问
transform.position = new Vector3(0, 10, 0);
rigidbody.mass = 5f;
light.intensity = 2f;

// AddComponent / DestroyImmediate（作用于场景对象）
go.AddComponent<Rigidbody>();
Object.Destroy(component);

// SerializedObject.ApplyModifiedProperties(不后续调 SaveAssets)
var so = new SerializedObject(comp);
so.FindProperty("_speed").floatValue = 20;
so.ApplyModifiedProperties();  // ← 只改内存

// EditorUtility.SetDirty(不后续调 SaveAssets)
EditorUtility.SetDirty(obj);  // ← 只是标记，不落盘

// 对 asset 实例的字段修改
scriptableObject._damage = 999;  // ← 改的是内存实例
```

### 2.2 哪些 API 才真正落盘（必须拦截）

```csharp
// 显式保存场景
EditorSceneManager.SaveScene(scene);
EditorSceneManager.SaveScene(scene, path);

// 显式保存 asset
AssetDatabase.SaveAssets();
AssetDatabase.SaveAssetIfDirty(obj);

// 直接写文件
File.WriteAllText(path, content);
File.WriteAllBytes(path, bytes);

// 创建 asset（一定落盘）
AssetDatabase.CreateAsset(obj, path);

// 导入 asset（会重新处理磁盘文件）
AssetDatabase.ImportAsset(path);

// Prefab 保存
PrefabUtility.SaveAsPrefabAsset(go, path);
PrefabUtility.ApplyPrefabInstance(go, InteractionMode.AutomatedAction);
```

### 2.3 ScriptableObject 的特殊性（重点）

**关键事实**：ScriptableObject 加载后是内存实例，改字段本身**不落盘**。看 [`ManageScriptableObjectTool`](Packages/com.agentcore.unity/Editor/Tools/Native/Scripting/ManageScriptableObjectTool.cs:236) 现在的实现：

```csharp
so.ApplyModifiedProperties();     // ← 改内存（安全）
EditorUtility.SetDirty(asset);    // ← 标记（安全，不落盘）
AssetDatabase.SaveAssets();       // ← ⚠️ 这一行才落盘
```

**这正是 Playmode 里 Agent 想做的**：改 SO 内存实例试试新数值。**只要拦截最后一行 `SaveAssets`**，Playmode 里的 SO 修改就变成纯内存操作，退出 Playmode 后如果没 Save，改动自动丢失（因为磁盘文件没变，下次加载 SO 又是原始值）。

### 2.4 场景对象修改的特殊性

**关键事实**：Playmode 中改场景对象（GameObject/Component）**不会被序列化到磁盘**，除非显式调用 `SaveScene`。看 [`ManageComponentTool.HandleSet`](Packages/com.agentcore.unity/Editor/Tools/Native/Core/ManageComponentTool.cs:355)：

```csharp
var errors = SetPropertiesViaSerializedObject(component, properties);
EditorUtility.SetDirty(component);   // ← 仅标记 dirty
MarkSceneDirty(go);                  // ← 标记场景 dirty
```

`SetDirty` + `MarkSceneDirty` 只是**告诉 Unity"这个对象/场景有未保存修改"**，触发 Unity 的星号标记（Hierarchy 里 "Scene *"），但**不会自动落盘**。用户必须手动 Ctrl+S 或 Agent 主动调 `SaveScene` 才落盘。

**结论**：绝大多数场景对象修改**天然就是内存操作**，Playmode 里放行是安全的。真正危险的只有 `SaveScene` 一个 API。

---

## 三、方案设计

### 3.1 核心设计原则

**不新增能力位，改造现有 write 能力位在 Playmode 下的语义**：

```
Playmode 下 write 类工具的执行行为：
  1. 允许执行（不再 block）
  2. 但拦截"落盘" API 调用，转为无操作（NoOp）或改为"内存 dirty 标记"
  3. 在返回结果中告知 Agent："此修改仅在运行时生效，退出 Playmode 后消失"
```

**换个说法**：Playmode 下的 write 工具变成"临时 write"，语义等同于 Unity 编辑器里用户在 Play 中拖 Inspector 值。

### 3.2 API 拦截层设计

新增 `PlaymodeWriteInterceptor` 组件，作为 write 类工具调用底层 Unity API 前的中间层：

```csharp
// 新文件：Editor/Tools/Safety/PlaymodeWriteInterceptor.cs
public static class PlaymodeWriteInterceptor
{
    /// <summary>
    /// Playmode 中的 SaveAssets 调用被拦截，转为 NoOp + 记录警告。
    /// </summary>
    public static bool SaveAssets()
    {
        if (EditorApplication.isPlaying)
        {
            AgentCoreLog.PlaymodeIntercept("AssetDatabase.SaveAssets", 
                "Skipped in Playmode (runtime-only mutation).");
            return false;  // 表示未落盘
        }
        AssetDatabase.SaveAssets();
        return true;
    }
    
    public static bool SaveScene(Scene scene, string path = null)
    {
        if (EditorApplication.isPlaying)
        {
            AgentCoreLog.PlaymodeIntercept("EditorSceneManager.SaveScene",
                $"Skipped in Playmode: scene '{scene.name}' modifications are runtime-only.");
            return false;
        }
        return string.IsNullOrEmpty(path)
            ? EditorSceneManager.SaveScene(scene)
            : EditorSceneManager.SaveScene(scene, path);
    }
    
    public static bool WriteFile(string path, string content)
    {
        if (EditorApplication.isPlaying)
        {
            // .cs 源码修改在 Playmode 无意义（Domain Reload 才生效）
            if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                AgentCoreLog.PlaymodeIntercept("File.WriteAllText",
                    $"Refused in Playmode: source file '{path}' modifications require Domain Reload.");
                return false;
            }
            // 其他类型文件默认也拦截（.txt / .json / .asset 走 AssetDatabase 更安全）
            AgentCoreLog.PlaymodeIntercept("File.WriteAllText",
                $"Skipped in Playmode: file '{path}' not written to disk.");
            return false;
        }
        File.WriteAllText(path, content);
        return true;
    }
    
    public static bool CreateAsset(Object obj, string path)
    {
        if (EditorApplication.isPlaying)
        {
            AgentCoreLog.PlaymodeIntercept("AssetDatabase.CreateAsset",
                $"Refused in Playmode: cannot create asset '{path}' at runtime.");
            return false;
        }
        AssetDatabase.CreateAsset(obj, path);
        return true;
    }
    
    public static bool ImportAsset(string path, ImportAssetOptions options)
    {
        if (EditorApplication.isPlaying)
        {
            AgentCoreLog.PlaymodeIntercept("AssetDatabase.ImportAsset",
                $"Skipped in Playmode: asset '{path}' not re-imported.");
            return false;
        }
        AssetDatabase.ImportAsset(path, options);
        return true;
    }
}
```

### 3.3 工具改造清单

所有涉及"改内存 + 落盘"混合操作的工具，需要把落盘调用改为通过 `PlaymodeWriteInterceptor`：

#### A. [`ManageScriptableObjectTool.cs:236-242`](Packages/com.agentcore.unity/Editor/Tools/Native/Scripting/ManageScriptableObjectTool.cs)

**当前代码**：
```csharp
so.ApplyModifiedProperties();
EditorUtility.SetDirty(asset);
AssetDatabase.SaveAssets();  // ← 无条件落盘
```

**改造后**：
```csharp
so.ApplyModifiedProperties();
EditorUtility.SetDirty(asset);
var saved = PlaymodeWriteInterceptor.SaveAssets();
// 在返回结果中附加 _runtime_only 标记
if (!saved)
    result["_runtime_only"] = "Modification applied in memory only; not persisted to disk (Playmode).";
```

#### B. [`ManageSceneTool`](Packages/com.agentcore.unity/Editor/Tools/Native/Core/ManageSceneTool.cs) 的 SaveScene 调用

**当前**（第 302/378/387/458/502 行）：
```csharp
EditorSceneManager.SaveScene(activeScene, path);
```

**改造后**：
```csharp
var saved = PlaymodeWriteInterceptor.SaveScene(activeScene, path);
if (!saved)
    return ToolResponse.OkWithData(
        new { path, _runtime_only = true },
        "Scene changes are in-memory only. Cannot save scene in Playmode; exit Playmode first if persistence is required.");
```

#### C. [`ManageScriptTool.cs:258/296/646/717`](Packages/com.agentcore.unity/Editor/Tools/Native/Scripting/ManageScriptTool.cs) 的 File.WriteAllText

**改造后**：
```csharp
var written = PlaymodeWriteInterceptor.WriteFile(fullPath, content);
if (!written)
    return ToolResponse.Fail(
        "Cannot modify .cs source files in Playmode. " +
        "Source changes require Domain Reload, which would exit Playmode. " +
        "Exit Playmode first, then retry.");
```

**特殊说明**：`.cs` 源码在 Playmode 中修改本来就没意义，这里改为**明确拒绝**（Fail），而不是 NoOp，帮助 Agent 快速理解为什么。

#### D. [`ManageAssetTool`](Packages/com.agentcore.unity/Editor/Tools/Native/Utility/ManageAssetTool.cs) 的 CreateAsset / DeleteAsset

**改造后**：
- `CreateAsset`: Playmode 中拒绝（因为创建 asset 一定落盘）
- `DeleteAsset`: Playmode 中拒绝（磁盘删除是不可撤销的破坏操作）
- `MoveAsset`: Playmode 中拒绝
- `ImportAsset`: Playmode 中 NoOp（会导致 Domain Reload，退出 Playmode）

#### E. [`ManagePrefabTool`](Packages/com.agentcore.unity/Editor/Tools/Native/Scripting/ManagePrefabTool.cs) 的 PrefabUtility.SaveAsPrefabAsset

**改造后**：Playmode 中拒绝（Prefab 保存本质是落盘）

#### F. [`ManageBuildTool` / `ManagePackageTool`](Packages/com.agentcore.unity/Editor/Tools/Native/Extended/)

**保持完全 Block**：Build 和 Package 操作在 Playmode 中执行毫无意义且必然触发 Editor 状态混乱。

### 3.4 PlayModePreflight 改造

将当前的"一律 block"改为**分级放行**：

```csharp
// PlayModePreflight.cs 重写
public static bool IsBlockedInPlayMode(ToolMetadata metadata, string action, out string reason)
{
    reason = null;
    
    if (!EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode)
        return false;
    
    if (metadata == null || (metadata.Capabilities & WriteCapabilities) == 0)
        return false;
    
    if (metadata.IsReadOnlyAction(action))
        return false;
    
    // 新逻辑：判断该 action 是否属于"Playmode 硬禁止"列表
    if (IsHardBlockedAction(metadata, action, out var hardReason))
    {
        reason = hardReason;
        return true;
    }
    
    // 其余 write action 放行，交给工具内的 PlaymodeWriteInterceptor 拦截落盘调用
    return false;
}

/// <summary>
/// Playmode 中硬禁止的 action 列表（无论如何都不能在 Playmode 执行）。
/// </summary>
private static bool IsHardBlockedAction(ToolMetadata metadata, string action, out string reason)
{
    reason = null;
    
    // 硬禁止 Capability：Build / Package / VCS write / BatchExecute
    var hardBlockedCaps = ToolCapability.BuildPlayer 
                        | ToolCapability.InstallPackages 
                        | ToolCapability.VersionControlWrite;
    if ((metadata.Capabilities & hardBlockedCaps) != 0)
    {
        reason = $"Tool '{metadata.Name}' capability requires Editor mode. " +
                 "Build / Package / VCS write operations trigger Domain Reload and cannot run in Playmode.";
        return true;
    }
    
    // 硬禁止 action 名（跨工具通用）
    var hardBlockedActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "save_scene", "save_assets", "create_asset", "delete_asset", "move_asset",
        "save_prefab", "apply_prefab", "install", "uninstall", "build",
        "domain_reload", "recompile"
    };
    if (!string.IsNullOrEmpty(action) && hardBlockedActions.Contains(action))
    {
        reason = $"Action '{action}' is hard-blocked in Playmode " +
                 "(triggers disk write / Domain Reload / build). Exit Playmode first.";
        return true;
    }
    
    return false;
}
```

### 3.5 ExecuteCode 特殊处理

[`ExecuteCodeTool`](Packages/com.agentcore.unity/Editor/Tools/Native/Scripting/ExecuteCodeTool.cs) 是最有用也最危险的工具。用户想要"运行时动态执行 C# 代码调试"，这个能力**极其强大**（等同于游戏内 REPL）：

**方案**：Playmode 中允许 ExecuteCode，但增加**运行时守卫**：

```csharp
// ExecuteCodeTool 增强
if (EditorApplication.isPlaying)
{
    // 1. 检查代码中是否包含硬禁止 API
    var forbiddenApis = new[] { 
        "SaveAssets", "SaveScene", "SaveAsPrefabAsset", "CreateAsset", 
        "File.WriteAllText", "File.WriteAllBytes", "File.Delete",
        "EditorApplication.Exit", "AssetDatabase.ImportAsset"
    };
    foreach (var api in forbiddenApis)
    {
        if (userCode.Contains(api))
            return ToolResult.Fail(
                $"Code contains forbidden API '{api}' in Playmode. " +
                "Runtime code execution cannot persist to disk or trigger reloads.");
    }
    
    // 2. 添加执行超时（防止死循环卡死主线程）
    var timeoutMs = 5000;
    // ... 用 Task + CancellationToken 执行
    
    // 3. 结果中标注 runtime-only
    result["_runtime_only"] = true;
}
```

### 3.6 数据结构：追踪运行时修改（可选，用于 Agent 提供上下文）

为了让 Agent 知道"我在 Playmode 里做了哪些内存修改"，可以维护一个会话内变更日志：

```csharp
public static class PlaymodeChangeLog
{
    private static readonly List<PlaymodeChange> _changes = new();
    
    public static void Record(string toolName, string action, string target, string details)
    {
        if (!EditorApplication.isPlaying) return;
        _changes.Add(new PlaymodeChange {
            Timestamp = DateTime.Now,
            Tool = toolName,
            Action = action,
            Target = target,
            Details = details
        });
    }
    
    public static IReadOnlyList<PlaymodeChange> GetChangesInCurrentSession() => _changes;
    
    // 退出 Playmode 时清空
    [InitializeOnLoadMethod]
    private static void Init()
    {
        EditorApplication.playModeStateChanged += state =>
        {
            if (state == PlayModeStateChange.ExitingPlayMode)
            {
                AgentCoreLog.Info($"[Playmode] Session ended, {_changes.Count} in-memory changes discarded.");
                _changes.Clear();
            }
        };
    }
}
```

**用途**：
- Agent 通过一个新 read 工具（如 `get_playmode_changes`）查询"我这次 Playmode 里都改了什么"
- 帮助 Agent 决定"哪些改动值得退出 Playmode 后重新落盘（永久应用）"

---

## 四、边界情况处理

### 4.1 ScriptableObject 内存/磁盘不一致（重点风险）

**场景**：Agent 在 Playmode 中改了 `WeaponConfig.damage = 50`（原始磁盘值 = 10）。

**Unity 行为**：
1. Playmode 中，所有引用这个 SO 的代码看到的是 `damage = 50`
2. 退出 Playmode 时，Unity **不会主动**从磁盘重新加载 SO —— **除非**该 SO 已被序列化的场景引用且 Unity 检测到需要重新反序列化
3. **实际结果**：退出 Playmode 后，SO 在 Editor 内存中**仍然是 50**（`damage` 字段脏），Inspector 显示 50，但磁盘 `.asset` 文件里还是 10；直到用户关闭再打开 Unity、或触发 Domain Reload（改 .cs 引起编译）时，SO 才从磁盘重新加载还原为 10

**这是 Unity 原生的坑，不是我们的方案引入的**。Unity 编辑器里手动在 Play 中改 SO Inspector 值，退出后 Inspector 值也不会自动还原。

**处置策略**：
- 在返回结果中明确警告：
  ```
  "_runtime_only": true,
  "_warning": "ScriptableObject '_damage' modified in-memory. Editor Inspector will retain this value after exiting Playmode, but disk file is unchanged. To revert Editor view: reload the asset (right-click → Reimport) or restart Unity."
  ```
- 提供一个 helper 工具 `manage_scriptable_object.reload_from_disk`：Playmode 结束后 Agent/用户主动调用，强制从磁盘重载
- 记录到 `PlaymodeChangeLog`，退出 Playmode 时汇总提示用户"这些 SO 内存值与磁盘不同"

### 4.2 场景对象持久性

**场景 A：Playmode 中创建的 GameObject**
Agent 在 Play 里 `Instantiate` 出一个 enemy prefab。

**Unity 行为**：
- 这个 GameObject 只存在于内存中
- 退出 Playmode 时**自动销毁**（Unity 原生行为）
- ✅ 安全，无需额外处理

**场景 B：Playmode 中修改现有 GameObject 的 Transform**
Agent 把 `player.transform.position` 从 (0,0,0) 改到 (10,5,0)。

**Unity 行为**：
- Playmode 中位置生效
- 退出 Playmode 时，Unity 从场景快照恢复，position 回到 (0,0,0)
- ✅ 安全，Unity 原生就这样

**场景 C：Playmode 中 AddComponent**
Agent 给某个 GameObject 加了个 Rigidbody。

**Unity 行为**：
- Playmode 中 Rigidbody 生效，物理开始模拟
- 退出 Playmode 时，Rigidbody **消失**（场景快照没有它）
- ✅ 安全，但需要在结果中明确告知 Agent

### 4.3 Prefab 联动风险

**危险场景**：Agent 在 Playmode 中修改**场景中的 Prefab 实例**：

```csharp
// 场景中有一个 Player prefab 实例
var player = GameObject.Find("Player");
player.GetComponent<PlayerStats>().maxHealth = 200;  // 改的是实例
```

**Unity 行为**：
- Playmode 中修改仅作用于该实例（override）
- 退出 Playmode 时 override 消失
- ✅ 安全

**真正的坑**：Agent 调用 `PrefabUtility.ApplyPrefabInstance` 把实例修改推回 Prefab：
- 这会**修改磁盘上的 Prefab 文件**
- **必须硬禁止**：`ApplyPrefabInstance` / `SaveAsPrefabAsset` / `PrefabUtility.SavePrefabAsset` 在 Playmode 中一律拒绝

**处置**：在 [`ManagePrefabTool`](../Editor/Tools/Native/Scripting/ManagePrefabTool.cs) 中把这些 API 的调用路径全部过 `PlaymodeWriteInterceptor`，Playmode 中直接返回错误。

### 4.4 Undo 栈行为

**Unity 事实**：Playmode 中的 `Undo.RecordObject` / `Undo.AddComponent` 会正常记录到 Undo 栈，但 **Undo 栈在退出 Playmode 时自动清空**。

**含义**：
- Playmode 中 Ctrl+Z 可以撤销当前 session 的修改
- 退出 Playmode 后 Undo 栈清空，用户和 Agent 都无法"撤销 Playmode 期间的操作"（但因为修改本身就没落盘，也不需要撤销）

**Agent 的表现要求**：
- 现有工具中 `ToolHelpers.RecordUndo` 的调用**保留**，Playmode 中依然有价值（session 内可撤销）
- 在结果中不需要额外提示，因为这是 Unity 原生行为

### 4.5 EditorPrefs 与 ProjectSettings 隔离

**关键决策**：`ModifyProjectSettings` / `ModifyAgentConfig` 类工具在 Playmode 下应该**硬禁止**。

**理由**：
- EditorPrefs 是 Editor 级配置（跨项目、跨 session），改动**立即持久化**到系统注册表 / 用户配置文件
- ProjectSettings 是项目级配置，改动通常也立即落盘（除非 gitignore 排除）
- 这些不是"运行时内存"，不符合本方案的语义

**具体工具处理**：
- [`ManagePrefsTool`](../Editor/Tools/Native/Meta/ManagePrefsTool.cs)：Playmode 硬禁止所有 write action
- [`ManageEditorTool`](../Editor/Tools/Native/Meta/ManageEditorTool.cs) 的 `set_project_settings`：Playmode 硬禁止
- [`ManageBuildTool`](../Editor/Tools/Native/Extended/ManageBuildTool.cs) 的 `set_settings`：Playmode 硬禁止

### 4.6 场景对象查找问题

**Unity 事实**：Playmode 中，`GameObject.Find` / `FindObjectsOfType` 只能找到**活跃的场景对象**，不能找到 disabled 或未加载的场景对象。

**Agent 使用注意**：
- Playmode 中用 [`FindGameObjectsTool`](../Editor/Tools/Native/Core/FindGameObjectsTool.cs) 找不到 Editor 模式下能找到的某些对象是**正常**的
- 需要在工具文档中标注："Playmode 中仅返回活跃场景对象"

### 4.7 异步任务泄漏

**危险场景**：Agent 通过 `ExecuteCodeTool` 启动一个异步 Task：
```csharp
Task.Run(() => { while(true) DoStuff(); });
```

**Unity 行为**：
- Task 在后台线程运行
- 退出 Playmode 时，Task **不会自动停止**（.NET Task 不受 Unity Playmode 生命周期管理）
- Editor 模式下 Task 继续运行，可能在 Editor 崩溃前一直占用资源

**处置**：
- `ExecuteCodeTool` 在 Playmode 中执行时，**注入 CancellationToken**，绑定到 `EditorApplication.playModeStateChanged` 事件
- 退出 Playmode 时自动 cancel 所有本 session 启动的 Task
- 执行超时（默认 5 秒）强制终止

### 4.8 静态字段污染

**危险场景**：Agent 通过 ExecuteCode 修改了某个类的 static 字段：
```csharp
GameManager.Instance.score = 9999;
PlayerData.HighScore = 999999;  // ← static
```

**Unity 事实**：
- 普通 static 字段（非 `[SerializeField]`）在退出 Playmode 时**不会自动重置**
- Domain Reload 会重置（下次编译或重启 Editor），但 Domain Reload 不由 Playmode 结束触发
- Unity 提供了 "Enter Play Mode Settings" 选项（Project Settings → Editor），可关闭 Domain Reload on Play；这种情况下静态字段污染更严重

**处置**：
- 在 `PlaymodeChangeLog` 中特别标记"通过 ExecuteCode 执行的代码"，退出 Playmode 时提示："本 session 执行了 N 段自定义代码，静态字段可能被修改。如需重置，请触发 Domain Reload（Ctrl+R 或修改任一 .cs 文件）"
- 这类污染**不阻止**，因为价值大于风险，但用户需要有知情权

---

## 五、能力位设计决策

### 5.1 是否新增 `ModifyRuntimeState` 能力位？

**结论：不新增**。

**理由**：
1. 现有能力位（`ModifyScene` / `ModifyAssets` 等）语义上已经涵盖"修改对象"，只是没有区分"内存 vs 磁盘"
2. 如果新增，需要在所有工具上重新标注 —— 工作量大且容易漏
3. **真正的区分点是 action / API 调用路径**，不是工具级别的能力位

**改用**：**Action 白名单机制 + API 拦截器**。这已经是当前架构的延伸（`ReadOnlyActions` 白名单模式），扩展成 `PlaymodeHardBlockedActions` 更自然。

### 5.2 Action 分类规范

在 [`AgentToolAttribute`](../Editor/Tools/Infrastructure/AgentToolAttribute.cs) 中新增字段：

```csharp
/// <summary>
/// Actions that are hard-blocked in Playmode regardless of tool capabilities.
/// These typically involve disk writes, Domain Reload, or build operations.
/// </summary>
public string[] PlaymodeHardBlockedActions { get; set; }
```

**使用示例**：
```csharp
// ManageSceneTool
[AgentTool("manage_scene",
    Capabilities = ToolCapability.ModifyScene | ToolCapability.WriteProjectFiles,
    ReadOnlyActions = new[] { "get_active", "get_hierarchy", "list", "get_build_scenes", "list_open_scenes" },
    PlaymodeHardBlockedActions = new[] { "save_scene", "save_all_scenes", "create_scene" }
    // 其他 write action（如 unload_scene, mark_dirty）在 Playmode 中放行
)]

// ManageScriptableObjectTool
[AgentTool("manage_scriptable_object",
    Capabilities = ToolCapability.ModifyAssets | ToolCapability.DeleteProjectFiles,
    ReadOnlyActions = new[] { "get", "find", "list_types", "export_json" },
    PlaymodeHardBlockedActions = new[] { "create", "delete" }
    // "modify" action 放行，但工具内通过 PlaymodeWriteInterceptor 跳过 SaveAssets
)]
```

---

## 六、实施路径

### 6.1 分阶段实施

#### **Phase 1: 拦截器 + Preflight 分级放行**（核心）

**目标**：Playmode 中允许所有 write 类 action 执行，但落盘 API 自动降级为 NoOp。

**任务清单**：
1. 新增 `Editor/Tools/Safety/PlaymodeWriteInterceptor.cs`（详见 3.2）
2. 修改 [`PlayModePreflight.cs`](../Editor/Tools/Safety/PlayModePreflight.cs)：改为分级放行（详见 3.4）
3. 在 [`AgentToolAttribute`](../Editor/Tools/Infrastructure/AgentToolAttribute.cs) 增加 `PlaymodeHardBlockedActions` 字段
4. 修改 `ToolMetadata` 承载新字段并暴露 `IsPlaymodeHardBlocked(action)` 查询方法
5. 修改 [`ToolCallDispatcher.cs:256-270`](../Editor/Tools/ToolCallDispatcher.cs)：新的 preflight 调用逻辑
6. 更新最容易出问题的 3 个工具的落盘调用点：
   - [`ManageScriptableObjectTool`](../Editor/Tools/Native/Scripting/ManageScriptableObjectTool.cs)
   - [`ManageSceneTool`](../Editor/Tools/Native/Core/ManageSceneTool.cs)
   - [`ManageScriptTool`](../Editor/Tools/Native/Scripting/ManageScriptTool.cs)
7. 新增 `AgentCoreLog.PlaymodeIntercept` 日志分类

#### **Phase 2: 全面工具改造 + 变更日志**

**目标**：覆盖所有会落盘的工具；提供 Agent 可查询的 Playmode 变更历史。

**任务清单**：
1. 逐个改造所有 Specialized 工具中调用 `AssetDatabase.SaveAssets` / `File.WriteAllText` / `AssetDatabase.CreateAsset` 的地方，路由到 `PlaymodeWriteInterceptor`
2. [`ManagePrefabTool`](../Editor/Tools/Native/Scripting/ManagePrefabTool.cs)：Playmode 中禁止所有 Prefab 保存操作
3. [`ManageAssetTool`](../Editor/Tools/Native/Utility/ManageAssetTool.cs)：Playmode 中禁止 create/delete/move
4. [`ManagePackageTool`](../Editor/Tools/Native/Extended/ManagePackageTool.cs) / [`ManageBuildTool`](../Editor/Tools/Native/Extended/ManageBuildTool.cs)：保持完全 Block
5. 新增 `Editor/Tools/Safety/PlaymodeChangeLog.cs`（详见 3.6）
6. 新增 read 工具 `get_playmode_changes`（查询本 session 的运行时修改列表）

#### **Phase 3: ExecuteCode 强化 + UI 反馈**

**目标**：让最强大的 ExecuteCode 工具在 Playmode 中安全可用。

**任务清单**：
1. [`ExecuteCodeTool`](../Editor/Tools/Native/Scripting/ExecuteCodeTool.cs) 增加：
   - Playmode 中的 forbidden API 静态扫描（详见 3.5）
   - CancellationToken 注入 + 超时终止
   - 异步 Task 生命周期绑定到 Playmode
2. AgentCore 主窗口新增 "Runtime Changes" 面板（可选）：
   - 实时显示当前 Playmode session 的修改列表
   - "退出 Playmode 前应用到磁盘" 快捷按钮（Agent 询问后用户主动选择）
3. Console 集成：Playmode intercept 日志用特殊颜色（青色 info）+ 前缀 `[PLAYMODE-INTERCEPT]`

#### **Phase 4: 测试 + 文档**

**任务清单**：
1. 单元测试：`PlaymodeWriteInterceptorTests`（覆盖 SaveAssets/SaveScene/WriteFile 拦截）
2. 集成测试：`PlaymodeMutationIntegrationTests`（进入 Playmode → 改 SO 内存值 → 退出 → 验证磁盘未变）
3. 手动测试 checklist（见下文）
4. 更新 `🛡️ AGENTS.md`：新增 "Playmode Runtime Mutation" 章节
5. 新增专题文档 `Documentation~/playmode-runtime-mutation.md`

### 6.2 手动测试 Checklist

```
基础场景：
[ ] Playmode 中用 manage_component.modify 改 speed 值 → 生效 → 退出后场景未 dirty
[ ] Playmode 中用 manage_scriptable_object.modify 改 SO 数值 → 生效 → 退出后磁盘 .asset 未变
[ ] Playmode 中用 manage_gameobject.create 创建 GO → Play 中存在 → 退出后消失
[ ] Playmode 中用 manage_physics.add_component 加 Rigidbody → 物理生效 → 退出后消失

拦截场景：
[ ] Playmode 中 manage_scene.save_scene → 返回错误提示 → 磁盘未变
[ ] Playmode 中 manage_script.write（.cs 文件）→ 返回错误提示 → 磁盘未变
[ ] Playmode 中 manage_asset.create → 返回错误提示 → 未创建 asset
[ ] Playmode 中 manage_prefab.save → 返回错误提示 → Prefab 未变

硬禁止场景：
[ ] Playmode 中 manage_build.build → 硬 block
[ ] Playmode 中 manage_package.install → 硬 block
[ ] Playmode 中 manage_prefs.set → 硬 block

ExecuteCode 场景：
[ ] Playmode 中执行 GameObject.Find("Player").SetActive(false) → 生效
[ ] Playmode 中执行 AssetDatabase.SaveAssets() → 静态扫描拦截，返回错误
[ ] Playmode 中执行 while(true){} → 5 秒后超时终止
[ ] Playmode 中执行启动 Task 的代码 → 退出 Playmode 后 Task 被取消

变更日志：
[ ] Playmode 中做 5 次 modify → get_playmode_changes 返回 5 条记录
[ ] 退出 Playmode 后重新进入 → get_playmode_changes 返回空

边界场景：
[ ] Playmode 中改 SO 值 → 退出 → Inspector 显示改后值 → 用户 Reimport → 显示磁盘原值
[ ] Playmode 中执行代码修改 static 字段 → 退出 → 静态字段仍污染 → 修改 .cs 触发 reload → 恢复
[ ] Playmode 中触发 Domain Reload（如添加编译错误的代码）→ 优雅退出 Playmode，不崩溃
```

---

## 七、风险评估与决策矩阵

### 7.1 剩余风险清单

| 风险 | 严重程度 | 缓解措施 |
|---|---|---|
| ScriptableObject 内存值污染 Editor Inspector | ⚠️⚠️ 中 | 明确警告 + 提供 reload 工具 |
| 静态字段污染跨 Playmode session | ⚠️⚠️ 中 | 记录到变更日志 + 用户可选 Domain Reload |
| 异步 Task 泄漏到 Editor 模式 | ⚠️⚠️ 中 | CancellationToken 绑定 Playmode 生命周期 |
| ExecuteCode 中的 forbidden API 绕过（如反射） | ⚠️⚠️ 中 | 静态扫描 + 运行时监控（`AssetDatabase.SaveAssets` 调用被 Interceptor 拦截，绕不过） |
| 拦截器漏了某个落盘 API | ⚠️⚠️⚠️ 高 | Phase 2 全面审计 + 单元测试覆盖所有已知落盘 API |
| Agent 不理解"运行时 vs 落盘"语义，反复困惑 | ⚠️ 低 | 结果中的 `_runtime_only` 标记 + AGENTS.md 教育 |

### 7.2 与前一版方案对比

| 维度 | 前版（写磁盘） | 本版（写内存） |
|---|---|---|
| 用户价值 | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ |
| 技术复杂度 | ⭐⭐⭐（快照系统） | ⭐⭐（拦截器） |
| 风险 | ⭐⭐⭐⭐（磁盘损坏、编译错误、崩溃） | ⭐⭐（内存污染） |
| 回滚成本 | 需 VCS/快照 | Unity 原生免费 |
| 与 Unity 惯例吻合度 | 低（反直觉） | 高（等同 Inspector 调值） |

**结论**：本方案是**明显更好的路径**。前版方案本质上是让 Agent 具备"在 Playmode 写磁盘"这个 Unity 官方都不推荐的能力；本方案是"让 Agent 具备 Unity 用户在 Play 中拖 Inspector 值的等价能力"，符合 Unity 惯例，风险显著低于前版。

---

## 八、决策点

请就以下问题给出决策：

1. **是否推进本方案**（拦截器路线 + Action 白名单）而非前一版（快照 + 允许写磁盘）？
2. **实施粒度**：
   - A. 一次性推 Phase 1-4（几周内完成一个新 minor 版本）
   - B. 分开 alpha/beta/stable（更保守，收集用户反馈迭代）
3. **ExecuteCode 是否放开**（Phase 3）？这是最强大也最危险的能力，可以延后到 v1.13.0 单独 milestone。
4. **是否新增 `get_playmode_changes` read 工具**？用于 Agent 自我审视 session 变更历史，帮助其判断"哪些改动值得永久应用"。
5. **Runtime Changes UI 面板**（Phase 3 步骤 2）是否需要？如果 Agent 已能通过 read 工具查询，UI 可以推迟。

方案落地后，Agent 在 Playmode 中的能力将约等于"Unity 用户在