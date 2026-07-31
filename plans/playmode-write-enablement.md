# Playmode Write 工具解禁方案

**提案版本**: v0.1-draft  
**提案日期**: 2026-07-30  
**当前状态**: 风险分析阶段  
**决策人**: 用户确认后执行

---

## 一、背景与现状

### 1.1 当前策略（[`PlayModePreflight.cs:8-16`](../Editor/Tools/Safety/PlayModePreflight.cs)）

```
所有 write 类工具在 Play Mode 中一律 Block，Read 类不受影响。
原因：Play Mode 下修改磁盘文件与运行时状态不一致，
  可能导致修改不生效、退出 Play Mode 时状态混乱、Scene 序列化冲突。
```

**Block 判定逻辑**：
- `Capabilities` 命中以下任一位 → 默认 Block（v1.11+ 支持 `ReadOnlyActions` 白名单粒度放行）：
  ```
  WriteProjectFiles | DeleteProjectFiles | ModifyScene | ModifyAssets | 
  ModifyScripts | ExecuteCode | InstallPackages | BuildPlayer | 
  VersionControlWrite | ModifyProjectSettings | ModifyAgentConfig | BatchExecute
  ```

### 1.2 用户诉求

**核心需求**：在 Playmode 中可以执行 write 类工具，快速定位和修复运行时问题，而不需要频繁退出 Playmode → 猜测改动 → 重新进入 Playmode 的循环。

**预期收益**：
- Agent 在 Playmode 中通过 `read_console` 看到报错堆栈 → 立即用 `manage_script` 修复代码 → 验证修复效果（需配合热重载或下一轮 Play）
- 减少人工手动介入和等待时间
- 提高 AI Agent 的闭环诊断-修复效率

---

## 二、风险矩阵（按工具能力分类）

### 2.1 **ModifyScripts** (高风险)

**影响工具**: [`ManageScriptTool`](../Editor/Tools/Native/Scripting/ManageScriptTool.cs)

| 风险点 | 严重程度 | 触发条件 | 后果 |
|--------|---------|---------|------|
| **A. 修改不立即生效** | ⚠️⚠️⚠️ 高 | Playmode 中改 .cs 文件 | 磁盘文件已改，但运行时仍用旧编译版本；Agent 误以为已生效，继续基于错误假设决策 |
| **B. 触发 Domain Reload** | ⚠️⚠️ 中 | Unity 检测到 .cs 修改 | Playmode 自动重启（如果 Auto Refresh 开启），当前运行状态丢失；如果关闭则无反应 |
| **C. 编译错误** | ⚠️⚠️⚠️ 高 | Agent 写出语法错误代码 | Unity 退出 Playmode + 编译失败，项目进入红色状态，后续所有操作 block；需人工修复 |
| **D. 引用断裂** | ⚠️⚠️ 中 | 改接口/字段名 | 其他文件/场景中的序列化引用失效，退出 Playmode 时报 Missing Script/Field |

**Unity 底层机制**：
- Playmode 使用的是**进入 Play 时的编译快照**，`.cs` 改动不会影响当前运行时
- `AssetDatabase.Refresh()` 可触发重新编译，但会强制退出 Playmode
- 热重载（Roslyn）在 Unity 2022+ 有限支持，但改字段/类结构仍会触发 Domain Reload

### 2.2 **ModifyScene** (中高风险)

**影响工具**: [`ManageSceneTool`](../Editor/Tools/Native/Core/ManageSceneTool.cs), [`ManageGameObjectTool`](../Editor/Tools/Native/Core/ManageGameObjectTool.cs), [`ManageComponentTool`](../Editor/Tools/Native/Core/ManageComponentTool.cs), [`ManagePrefabTool`](../Editor/Tools/Native/Scripting/ManagePrefabTool.cs), [`OptimizationTool`](../Editor/Tools/Native/Extended/OptimizationTool.cs), [`WorkflowTool`](../Editor/Tools/Native/Meta/WorkflowTool.cs), [`CleanerTool`](../Editor/Tools/Native/Extended/CleanerTool.cs)

| 风险点 | 严重程度 | 触发条件 | 后果 |
|--------|---------|---------|------|
| **A. 磁盘/运行时状态分叉** | ⚠️⚠️⚠️ 高 | Playmode 中保存场景 | 磁盘 .unity 文件保存的是**运行时修改后的状态**（临时对象、运行时生成数据），退出 Playmode 时脏数据被持久化 |
| **B. 序列化冲突** | ⚠️⚠️⚠️ 高 | 退出 Playmode | Unity 尝试反序列化磁盘场景 + 还原 Playmode 前状态 → YAML 冲突、丢失修改、报错 |
| **C. Prefab 引用断裂** | ⚠️⚠️ 中 | Playmode 中改 Prefab 实例 | Prefab override 状态混乱，退出后可能丢失连接 |
| **D. Undo 栈损坏** | ⚠️⚠️ 中 | Playmode 中执行 Undo.RecordObject | Playmode 结束时 Undo 历史被清空，Agent 操作无法撤销 |

**Unity 底层机制**：
- Playmode 开始时，Unity 创建场景快照，运行时修改**不影响磁盘**（除非显式保存）
- `EditorSceneManager.SaveScene()` 会把**运行时状态写入磁盘** → 这是风险 A 的根源
- 退出 Playmode 时，Unity 从快照恢复，但如果磁盘已被写脏则冲突

### 2.3 **ModifyAssets** (中风险)

**影响工具**: [`ManageAssetTool`](../Editor/Tools/Native/Utility/ManageAssetTool.cs), [`ManageScriptableObjectTool`](../Editor/Tools/Native/Scripting/ManageScriptableObjectTool.cs)

| 风险点 | 严重程度 | 触发条件 | 后果 |
|--------|---------|---------|------|
| **A. Asset 序列化状态不一致** | ⚠️⚠️ 中 | Playmode 中修改 ScriptableObject 并保存 | 磁盘 asset 被改，但运行时仍用旧实例；退出时可能丢失运行时修改 |
| **B. AssetDatabase 操作触发 Refresh** | ⚠️⚠️ 中 | 创建/删除/移动 asset | 可能触发 Domain Reload，强制退出 Playmode |
| **C. 引用丢失** | ⚠️⚠️ 中 | 删除被场景中对象引用的 asset | 运行时报 Missing Reference，退出后场景引用断裂 |

### 2.4 **WriteProjectFiles** / **DeleteProjectFiles** (中风险)

**影响工具**: [`ManageScriptTool`](../Editor/Tools/Native/Scripting/ManageScriptTool.cs), [`ManageMemoryProfilerTool`](../Editor/Tools/Native/Extended/ManageMemoryProfilerTool.cs)

| 风险点 | 严重程度 | 触发条件 | 后果 |
|--------|---------|---------|------|
| **A. 删除正在运行的脚本** | ⚠️⚠️⚠️ 高 | 删除当前 Playmode 中活跃对象的脚本文件 | 退出 Playmode 时 Unity 报 Missing Script，场景中对象组件丢失 |
| **B. 误删非代码文件** | ⚠️⚠️ 中 | Agent 误判删除配置/资源文件 | 数据丢失，可能无法恢复（如果没 VCS） |

### 2.5 **ExecuteCode** (极高风险)

**影响工具**: [`ExecuteCodeTool`](../Editor/Tools/Native/Scripting/ExecuteCodeTool.cs), [`ExecuteMenuItemTool`](../Editor/Tools/Native/Meta/ExecuteMenuItemTool.cs), [`ManageTestTool`](../Editor/Tools/Native/Extended/ManageTestTool.cs)

| 风险点 | 严重程度 | 触发条件 | 后果 |
|--------|---------|---------|------|
| **A. 运行时状态污染** | ⚠️⚠️⚠️ 高 | Playmode 中执行 C# 代码修改静态字段/单例 | 全局状态被改，影响后续测试/运行；退出 Playmode 时可能残留脏数据 |
| **B. 线程竞态** | ⚠️⚠️ 中 | 代码启动异步任务 | Playmode 结束时任务未完成，可能在 Editor 模式回调，触发异常 |
| **C. 无限循环/崩溃** | ⚠️⚠️⚠️ 高 | Agent 生成错误代码 | 卡死 Unity 主线程，需强制杀进程 |

### 2.6 **ModifyProjectSettings** (中风险)

**影响工具**: [`ManageEditorTool`](../Editor/Tools/Native/Meta/ManageEditorTool.cs), [`ManagePrefsTool`](../Editor/Tools/Native/Meta/ManagePrefsTool.cs), [`ManageBuildTool`](../Editor/Tools/Native/Extended/ManageBuildTool.cs)

| 风险点 | 严重程度 | 触发条件 | 后果 |
|--------|---------|---------|------|
| **A. 渲染管线切换** | ⚠️⚠️⚠️ 高 | Playmode 中改 Graphics Settings | 可能触发 Domain Reload，或导致渲染错误；退出后全局设置被永久修改 |
| **B. 输入系统切换** | ⚠️⚠️ 中 | 改 Input System 设置 | Playmode 中输入失效；退出后需重新配置 |

### 2.7 **InstallPackages** / **BuildPlayer** (高风险)

**影响工具**: [`ManagePackageTool`](../Editor/Tools/Native/Extended/ManagePackageTool.cs), [`ManageBuildTool`](../Editor/Tools/Native/Extended/ManageBuildTool.cs)

| 风险点 | 严重程度 | 触发条件 | 后果 |
|--------|---------|---------|------|
| **A. 强制 Domain Reload** | ⚠️⚠️⚠️ 高 | 安装/卸载 Package | 必然退出 Playmode，当前运行状态丢失 |
| **B. 依赖冲突** | ⚠️⚠️ 中 | 安装不兼容包 | 项目编译失败，需手动回滚 |
| **C. Build 锁定资源** | ⚠️⚠️ 中 | Playmode 中触发 Build | Build 过程锁定文件，可能与 Playmode 冲突，导致 Build 失败或 Playmode 卡死 |

### 2.8 **BatchExecute** (极高风险)

**影响工具**: [`BatchExecuteTool`](../Editor/Tools/Native/Meta/BatchExecuteTool.cs), [`WorkflowTool`](../Editor/Tools/Native/Meta/WorkflowTool.cs)

| 风险点 | 严重程度 | 触发条件 | 后果 |
|--------|---------|---------|------|
| **A. 级联失败** | ⚠️⚠️⚠️ 高 | 批量操作中某一步失败 | 项目处于部分修改状态，难以恢复；批量删除可能造成大面积数据丢失 |
| **B. 不可撤销** | ⚠️⚠️⚠️ 高 | 批量删除文件 | Undo 栈无法撤销批量文件操作 |

### 2.9 **ModifyAgentConfig** (低风险)

**影响工具**: [`ManageWorkspaceConfigTool`](../Editor/Tools/Native/Bootstrap/ManageWorkspaceConfigTool.cs)

| 风险点 | 严重程度 | 触发条件 | 后果 |
|--------|---------|---------|------|
| **A. 配置错误** | ⚠️ 低 | 改错 `.agentcore` 配置 | Agent 行为异常，但不影响项目文件 |

---

## 三、配套防护措施设计

### 3.1 核心防护策略

#### A. **自动快照系统**（必须）

**设计**：
```csharp
// 新增 PlaymodeSnapshotManager.cs
public static class PlaymodeSnapshotManager
{
    private static string _snapshotPath;
    private static List<string> _modifiedFiles = new();
    
    // 在首次 Playmode write 操作前自动触发
    public static void EnsureSnapshot()
    {
        if (_snapshotPath != null) return; // 已创建
        
        _snapshotPath = Path.Combine(".agentcore", "playmode-snapshots", 
            $"{DateTime.Now:yyyyMMdd-HHmmss}");
        
        // 快照当前场景
        var activeScene = SceneManager.GetActiveScene();
        if (activeScene.isDirty || activeScene.path != "")
        {
            var scenePath = Path.Combine(_snapshotPath, "scene.backup");
            File.Copy(activeScene.path, scenePath);
        }
        
        // 记录当前打开场景列表
        SaveOpenScenesList();
        
        AgentCoreLog.Info($"[PlaymodeSnapshot] Created at {_snapshotPath}");
    }
    
    // 记录每次修改的文件
    public static void TrackModifiedFile(string filePath)
    {
        if (!_modifiedFiles.Contains(filePath))
        {
            // 备份原始文件
            var backupPath = Path.Combine(_snapshotPath, "files", filePath);
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath));
            File.Copy(filePath, backupPath, overwrite: true);
            
            _modifiedFiles.Add(filePath);
        }
    }
    
    // 退出 Playmode 时提供回滚选项
    [InitializeOnLoadMethod]
    private static void RegisterPlaymodeCallback()
    {
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    }
    
    private static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.EnteredEditMode && _snapshotPath != null)
        {
            // 显示对话框
            var result = EditorUtility.DisplayDialogComplex(
                "Playmode 期间文件已修改",
                $"检测到 {_modifiedFiles.Count} 个文件在 Playmode 中被修改。\n\n" +
                "保留修改：继续使用修改后的文件\n" +
                "回滚全部：恢复到 Playmode 前的状态\n" +
                "查看差异：打开快照目录对比",
                "保留修改", "回滚全部", "查看差异");
            
            if (result == 1) // 回滚
            {
                RollbackAllChanges();
            }
            else if (result == 2) // 查看
            {
                EditorUtility.RevealInFinder(_snapshotPath);
            }
            
            // 清理
            _modifiedFiles.Clear();
            _snapshotPath = null;
        }
    }
    
    private static void RollbackAllChanges()
    {
        foreach (var file in _modifiedFiles)
        {
            var backupPath = Path.Combine(_snapshotPath, "files", file);
            if (File.Exists(backupPath))
            {
                File.Copy(backupPath, file, overwrite: true);
            }
        }
        AssetDatabase.Refresh();
        AgentCoreLog.Info($"[PlaymodeSnapshot] Rolled back {_modifiedFiles.Count} files");
    }
}
```

**集成点**：在 [`ToolCallDispatcher.cs:256`](../Editor/Tools/ToolCallDispatcher.cs) Play Mode preflight 检查处：
```csharp
// 3.5 Play Mode preflight 改为：
if (EditorApplication.isPlaying && (tool.Metadata.Capabilities & WriteCapabilities) != 0)
{
    // 新策略：允许执行，但先创建快照
    PlaymodeSnapshotManager.EnsureSnapshot();
    
    // 记录警告日志
    AgentCoreLog.Warning($"[Playmode Write] Tool '{toolName}' executing in Play Mode. Snapshot created.");
}
```

#### B. **强制确认弹窗**（第一次 write）

在 Playmode 中首次执行 write 操作时，向用户显示：
```
⚠️ 你即将在 Playmode 中修改项目文件

风险提示：
• 修改的代码不会立即生效（需退出 Playmode 重新编译）
• 场景修改可能与运行时状态冲突
• 已自动创建快照，退出 Playmode 时可选择回滚

建议：如果不确定，请先退出 Playmode。

[ ] 本次会话不再提示

[继续]  [退出 Playmode 后执行]  [取消]
```

#### C. **实时差异预览**

在 AgentCore 窗口中新增 "Playmode Changes" 面板：
- 实时显示已修改的文件列表
- 每个文件旁显示 [查看差异] [回滚此文件] 按钮
- 提供 "回滚全部" 快捷按钮

#### D. **工具级风险提示注入**

在每个 write 工具的执行结果中，自动追加风险提示：
```json
{
  "success": true,
  "path": "Assets/Scripts/PlayerController.cs",
  "changes": "Modified Move() method",
  "_playmode_warning": "⚠️ 此修改在 Playmode 中执行，代码修改不会立即生效。退出 Playmode 后将重新编译。快照已保存，可在退出时选择回滚。"
}
```

这样 LLM 可以在上下文中看到警告，理解当前状态。

#### E. **Scene 保存拦截**

特殊处理 `EditorSceneManager.SaveScene()` 调用：
```csharp
// 在 ManageSceneTool 中，save_scene action 增加检查：
if (EditorApplication.isPlaying)
{
    var confirm = EditorUtility.DisplayDialog(
        "危险操作：Playmode 中保存场景",
        "警告：你正在保存运行时场景状态，这会将临时对象和运行时数据持久化到磁盘。\n\n" +
        "这通常不是你想要的。建议退出 Playmode 后再保存。\n\n" +
        "确定要继续吗？",
        "强制保存（不建议）", "取消");
    
    if (!confirm)
        return ToolResponse.Fail("User cancelled scene save in Playmode.");
}
```

#### F. **编译错误自动回滚**

监听编译状态，如果 Playmode write 导致编译失败：
```csharp
// 在 PlaymodeSnapshotManager 中增加：
private static void OnCompilationFinished(object obj)
{
    if (EditorUtility.scriptCompilationFailed && _snapshotPath != null)
    {
        var rollback = EditorUtility.DisplayDialog(
            "编译失败",
            "Playmode 中的修改导致编译错误。\n\n" +
            "自动回滚到修改前的状态？",
            "自动回滚", "手动修复");
        
        if (rollback)
        {
            RollbackAllChanges();
        }
    }
}
```

### 3.2 日志与可观测性

#### A. 专用日志分类
在 [`AgentCoreLog`](../Editor/Utils/AgentCoreLog.cs) 中新增：
```csharp
public static void PlaymodeWrite(string toolName, string action, string target)
{
    var msg = $"[PLAYMODE-WRITE] {toolName}.{action} → {target}";
    Debug.LogWarning(msg);
    // 同时写入 .agentcore/logs/playmode-writes.log
}
```

#### B. 会话摘要
在退出 Playmode 时生成报告：
```
Playmode Write Session Summary
==============================
Duration: 5m 32s
Modified Files: 3
  • Assets/Scripts/PlayerController.cs (manage_script.write)
  • Assets/Scenes/Main.unity (manage_scene.save_scene)
  • Assets/Data/Config.asset (manage_scriptable_object.modify)

Risks Encountered:
  • 1 compilation error (auto-rolled back)
  • 1 scene save warning (user cancelled)

Snapshot: .agentcore/playmode-snapshots/20260730-092015
```

### 3.3 分级放开策略（可选）

如果不想一次性全部解禁，可以分阶段：

**Phase 1（低风险）**：放开以下能力
- `ModifyAssets`（仅 ScriptableObject 修改）
- `WriteProjectFiles`（仅非 .cs 文件）
- `ModifyProjectSettings`（仅 EditorPrefs）

**Phase 2（中风险）**：放开
- `ModifyScene`（带强制快照）
- `ModifyScripts`（仅 append/insert，禁止 delete/replace）

**Phase 3（高风险）**：全部放开
- `ExecuteCode`（带沙箱）
- `BatchExecute`（逐个确认）

实现方式：新增配置项
```json
// .agentcore/config.json
{
  "playmode_write_policy": {
    "enabled": true,
    "allowed_capabilities": [
      "ModifyAssets",
      "WriteProjectFiles"
    ],
    "require_confirmation_per_session": true,
    "auto_snapshot": true,
    "auto_rollback_on_compile_error": true
  }
}
```

---

## 四、实施方案

### 4.1 代码修改清单

#### A. 移除 Playmode Block（最小改动）

**文件**: [`PlayModePreflight.cs`](../Editor/Tools/Safety/PlayModePreflight.cs)

**选项 1：完全移除检查**
```csharp
public static bool IsBlockedInPlayMode(ToolMetadata metadata, string action, out string reason)
{
    reason = null;
    return false; // 不再 block
}
```

**选项 2：改为警告模式（推荐）**
```csharp
public static bool IsBlockedInPlayMode(ToolMetadata metadata, string action, out string reason)
{
    reason = null;
    
    if (!EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isPlaying)
        return false;
    
    if (metadata == null || (metadata.Capabilities & WriteCapabilities) == 0)
        return false;
    
    if (metadata.IsReadOnlyAction(action))
        return false;
    
    // 新逻辑：不 block，但触发快照和警告
    PlaymodeSnapshotManager.EnsureSnapshot();
    AgentCoreLog.PlaymodeWrite(metadata.Name, action, "(preparing)");
    
    // 检查配置是否需要确认
    if (ShouldRequestConfirmation())
    {
        var config = PlaymodeWriteConfig.Load();
        if (!config.ConfirmedForThisSession)
        {
            // 显示确认对话框（阻塞主线程）
            var confirmed = ShowPlaymodeWriteConfirmation();
            if (!confirmed)
            {
                reason = "User declined to execute write operation in Play Mode.";
                return true; // 用户拒绝
            }
            config.ConfirmedForThisSession = true;
        }
    }
    
    return false; // 放行
}
```

#### B. 新增快照管理器

**新文件**: `Editor/Tools/Safety/PlaymodeSnapshotManager.cs`（见 3.1.A 完整实现）

#### C. 在 ToolCallDispatcher 中集成

**文件**: [`ToolCallDispatcher.cs:256-270`](../Editor/Tools/ToolCallDispatcher.cs)

```csharp
// 当前代码：
if (Safety.PlayModePreflight.IsBlockedInPlayMode(tool.Metadata, preflightAction, out var playModeReason))
{
    // 返回失败
}

// 改为：
// PlayModePreflight 内部已处理快照和确认，这里不再 block
Safety.PlayModePreflight.IsBlockedInPlayMode(tool.Metadata, preflightAction, out var playModeReason);
// 即使返回 false（放行），reason 中可能包含警告信息
```

#### D. 工具结果注入警告

**文件**: [`ToolCallDispatcher.cs`](../Editor/Tools/ToolCallDispatcher.cs) 执行成功后：
```csharp
// 在工具成功执行后（line ~350 附近）：
if (EditorApplication.isPlaying && (tool.Metadata.Capabilities & WriteCapabilities) != 0)
{
    // 在返回结果中注入警告
    if (result.Output is JObject jObj)
    {
        jObj["_playmode_warning"] = 
            "⚠️ This operation was executed in Play Mode. " +
            "Code changes will not take effect until Domain Reload. " +
            "Scene changes may conflict with runtime state. " +
            "Snapshot saved; you can rollback on exiting Play Mode.";
    }
}
```

### 4.2 UI 扩展

#### A. AgentCore 窗口新增 Tab

在主窗口中添加 "Playmode Changes" tab：
- 显示当前快照路径
- 列出已修改文件（带 diff 链接）
- "Rollback All" / "Keep All" 按钮
- 状态指示器（绿色 = 无修改，黄色 = 有修改，红色 = 编译失败）

#### B. Console 集成

在 Unity Console 中，Playmode write 日志使用特殊颜色（橙色 Warning）和前缀 `[PLAYMODE-WRITE]`，便于用户识别。

### 4.3 测试计划

#### A. 单元测试

新增 `PlaymodeSnapshotManagerTests.cs`：
- 测试快照创建
- 测试文件跟踪
- 测试回滚逻辑
- 测试多文件批量操作

#### B. 集成测试场景

创建 `Tests/PlayMode/PlaymodeWriteIntegrationTests.cs`：
1. 进入 Playmode
2. 调用 `manage_script.write` 修改文件
3. 验证快照已创建
4. 退出 Playmode
5. 验证弹出回滚对话框
6. 执行回滚，验证文件恢复

#### C. 手动测试 Checklist

```
[ ] Playmode 中修改代码 → 退出 → 回滚 → 文件恢复
[ ] Playmode 中保存场景 → 退出 → 回滚 → 场景恢复
[ ] Playmode 中触发编译错误 → 自动回滚 → 编译通过
[ ] Playmode 中批量删除文件 → 退出 → 回滚 → 文件恢复
[ ] Playmode 中修改 ScriptableObject → 退出 → 保留修改 → 数据持久化
[ ] 多次进出 Playmode → 快照正确清理，无泄漏
```

### 4.4 文档更新

#### A. 更新 AGENTS.md

在 "Capabilities" 章节补充：
```markdown
## Playmode Write 支持

从 v1.12.0 开始，AgentCore 允许在 Play Mode 中执行 write 类工具。

### 风险与防护
- **自动快照**：首次 write 操作前自动创建快照
- **退出时回滚**：可选择恢复到 Playmode 前状态
- **编译错误回滚**：如果修改导致编译失败，自动回滚

### 最佳实践
- 优先使用 Read 工具在 Playmode 中诊断问题
- 确认问题原因后，再执行 write 操作
- 退出 Playmode 后检查快照对话框，决定保留或回滚
- 对于大规模修改，建议退出 Playmode 后执行

详见：[Playmode Write 指南](playmode-write-guide.md)
```

#### B. 新增专题文档

**文件**: `Packages/com.agentcore.unity/Documentation~/playmode-write-guide.md`
- Playmode write 工作原理
- 风险详解
- 快照与回滚使用方法
- 常见问题 FAQ

### 4.5 发布时间表

**v1.12.0-alpha**（2-3 周）
- [ ] 实现快照系统
- [ ] 移除 Playmode block（改为警告模式）
- [ ] 基础 UI（快照路径显示）
- [ ] 单元测试

**v1.12.0-beta**（4-6 周）
- [ ] 完整 UI（Playmode Changes tab）
- [ ] 编译错误自动回滚
- [ ] 集成测试
- [ ] 文档完善

**v1.12.0-stable**（8-10 周）
- [ ] 社区测试反馈
- [ ] 修复已知 bug
- [ ] 性能优化（快照大小控制）

---

## 五、决策矩阵

### 5.1 Go / No-Go 判定

| 评估维度 | 分数 (1-5) | 说明 |
|---------|-----------|------|
| **技术可行性** | ⭐⭐⭐⭐ 4/5 | 快照/回滚机制成熟，Unity API 支持充分；难点在编译错误检测时机 |
| **用户价值** | ⭐⭐⭐⭐⭐ 5/5 | 显著提升 Agent 诊断-修复闭环效率，减少人工介入 |
| **风险可控性** | ⭐⭐⭐ 3/5 | 快照可缓解大部分风险，但用户误操作（不回滚）仍可能造成数据损坏 |
| **实现成本** | ⭐⭐⭐ 3/5 | 需新增 ~500 行代码（快照管理器 + UI），测试工作量中等 |
| **维护负担** | ⭐⭐⭐ 3/5 | 需持续处理快照清理、大文件优化、边缘 case |

**总分**: 18/25 (72%)

**建议**: **有条件推进**，但需要：
1. ✅ 默认开启自动快照（强制，不可关闭）
2. ✅ 首次使用显示风险教育弹窗
3. ✅ 在 Console 中持续显示 Playmode write 警告
4. ⚠️ v1.12.0-alpha 阶段仅向有经验用户开放（通过配置项）
5. ⚠️ 收集至少 20 个真实用户案例后再进入 stable

### 5.2 备选方案

如果决定不完全解禁，可以采用**中间路线**：

#### 方案 A：只读模式增强
- 保持 Playmode write block
- 增强 Read 工具：`manage_script.analyze_runtime_error`（解析堆栈 + 定位代码 + 给出修复建议）
- 增强 `read_console.suggest_fix`（基于 log 生成修复代码，但不执行）
- Agent 在 Playmode 中收集诊断信息 → 退出 Playmode → 执行修复 → 重新进入 Playmode 验证

**优点**: 零风险，不改现有安全策略  
**缺点**: 仍需人工 "退出-改-重进" 循环

#### 方案 B：虚拟 Playmode 沙箱
- Playmode 中 write 操作不写磁盘，写入内存临时文件系统
- 退出 Playmode 时提示 "发现 X 个修改，是否应用到磁盘？"
- 用户确认后一次性写入

**优点**: 完全隔离，Playmode 中实验不影响磁盘  
**缺点**: 需要虚拟文件系统层（复杂度高）

---

## 六、后续工作（如果通过）

### 6.1 第一阶段（核心实现）
- [ ] 实现 `PlaymodeSnapshotManager`
- [ ] 修改 `PlayModePreflight` 为警告模式
- [ ] 集成到 `ToolCallDispatcher`
- [ ] 单元测试覆盖

### 6.2 第二阶段（用户体验）
- [ ] AgentCore 窗口 UI 扩展
- [ ] 确认对话框设计与实现
- [ ] 日志分类与可视化
- [ ] 编译错误自动回滚

### 6.3 第三阶段（稳定与优化）
- [ ] 大文件快照优化（增量备份）
- [ ] 快照自动清理（保留最近 N 个）
- [ ] 性能测试（1000+ 文件项目）
- [ ] 用户反馈收集与迭代

---

## 七、结论与建议

### 结论

1. **移除 Playmode write 禁令在技术上可行**，但需要完善的快照/回滚机制作为安全网
2. **风险主要集中在用户误操作**（不回滚）和**编译错误**（需自动检测）
3. **用户价值明确**：显著提升 Agent 在 Playmode 中的诊断-修复闭环效率
4. **实现成本适中**：核心逻辑 ~500 行，UI + 测试 ~1000 行

### 建议

**推荐实施路径**：
1. ✅ **v1.12.0-alpha**：快照系统 + 移除 block + 基础测试（仅内部测试）
2. ✅ **v1.12.0-beta**：完整 UI + 自动回滚 + 文档（开放给社区早期采用者）
3. ⚠️ **收集反馈**：至少 20 个真实案例，评估数据损坏风险
4. ✅ **v1.12.0-stable**：根据反馈优化，正式发布

**如果用户不接受风险**，采用**方案 A（只读模式增强）**作为备选。

---

**决策点**：是否推进此方案？
- [ ] 批准推进（进入实施阶段）
- [ ] 采用备选方案 A（只读增强）
- [ ] 采用备选方案 B（虚拟沙箱）
- [ ] 维持现状（不做改动）

请反馈决策结果，以便进入下一阶段。
