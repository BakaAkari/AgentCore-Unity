# G06 — Selection API 深化

> **领域**: Workflow / P1
> **状态**: 起草
> **审计日期**: 2026-07-23

## 1. 场景推演

- **S1**: Agent 帮用户"选中场景所有带 Rigidbody 的 GameObject 方便批量改属性". 需能按查询条件精准写选区.
- **S2**: 用户手动选了 5 个 prefab, 问 agent "把选中的东西列出来". Agent 应能:  一次拉当前 selection (含 GameObject + Asset + ScriptableObject), 返回结构化列表.
- **S3**: 用户在 Project 窗口选 folder, 想让 agent 处理该 folder 下所有 asset. Agent 应能: 拉 `Selection.activeContext` 拿到当前上下文 folder path.
- **S4**: 多物体编辑场景: 用户选了 3 个 GameObject, 想让 agent 对所有选中项加同一个组件. Agent 应能: 拉 `Selection.gameObjects` (只 GameObject) 遍历, 加组件时用 `AddComponent` 循环 + `Undo.RegisterCompleteObjectUndo`.
- **S5**: 用户想"记住当前 selection, 做完某操作后恢复". Agent 应能: get_selection 拿到 instanceIDs 列表存起来 → 后续 set_selection 传回同一批 instanceIDs 恢复.

## 2. Unity API 表

**Selection static class** (`UnityEditor.Selection`):

| 属性 / 方法 | 用途 |
|---|---|
| `Selection.activeObject` | 单一 active 对象 (GameObject / Asset / SO / etc.) |
| `Selection.activeGameObject` | 只在 selection 是 GameObject 时非 null |
| `Selection.activeTransform` | 便利: activeGameObject?.transform |
| `Selection.objects` | 完整多选数组 (Object[]) |
| `Selection.gameObjects` | 过滤为 GameObject[] |
| `Selection.transforms` | 过滤为 Transform[] |
| `Selection.instanceIDs` | int[] 底层 ID, 稳定跨 domain reload |
| `Selection.assetGUIDs` | Project 窗口选中的 asset GUID (S3 场景关键) |
| `Selection.activeContext` | 当前上下文对象 (通常是 folder / scene root) |
| `Selection.selectionChanged` | 事件, agent 一般不用订阅 |
| `Selection.selectCount` | int, 数量 |
| `Selection.SetActiveObjectWithContext(obj, ctx)` | 设置 active + context |

**Undo 契约**:
- Selection 变更**不入 Undo 栈** (Unity 官方语义, Ctrl+Z 不会撤销 selection 变更). 无需 `Undo.RecordObject`.

**Reflection**: 不需要, 全 public.

## 3. 现有覆盖诊断

`manage_editor` 现有 (来自 [`inventory.json`](/tmp/inventory.json)):

- `get_selection` — 已存
- `set_selection` — 已存

grep [`ManageEditorTool.cs`](../../Editor/Tools/Native/Meta/ManageEditorTool.cs) 关键行 (审计时抓到):
```
291: Selection.activeGameObject
302: Selection.activeObject
320: Selection.objects
506: Selection.activeObject = resolved[0]
510: Selection.objects = resolved.ToArray()
```

**当前实现推测** (需读源码全文确认, 但从 grep 可看到):
- `get_selection` 返 activeGameObject / activeObject / objects
- `set_selection` 支持传 identifier 数组, 解析后写 `Selection.objects`

**缺什么** (SHALLOW):
- **无** `instanceIDs` 输出 (S5 恢复 selection 需要)
- **无** `assetGUIDs` 输出 (S3 Project 窗口 folder 选择)
- **无** `activeContext` 输出/写入
- **无** `selectCount` / 便利属性 (可以从 objects.Length 拿, 但语义化更好)
- **可能无**过滤查询 (S1 场景, "选中所有带 Rigidbody 的" 需要 `set_selection_by_query`)

**根因分类**: `SHALLOW_TOOL`. `get_selection` / `set_selection` 存在但只覆盖最基础的 GameObject 选择. Asset / SO / context / 查询式选择缺失.

## 4. 建议 action 接口

**归属工具**: `manage_editor` (深化), **不新建**.

### 4.1 增强 `get_selection` 返回值
```json
// 请求: 无参
// 返回 (扩展):
{
  "active_object": { "instance_id": 12345, "type": "GameObject", "name": "Player", "path": "Scene/Root/Player" },
  "active_context": { "instance_id": 67890, "type": "DefaultAsset", "asset_path": "Assets/Prefabs/" },
  "select_count": 3,
  "objects": [
    { "instance_id": ..., "type": "GameObject", ..., "hierarchy_path": "..." },
    { "instance_id": ..., "type": "Material", ..., "asset_path": "..." }
  ],
  "game_objects": [ /* 过滤后, 只 GameObject */ ],
  "asset_guids": ["abc123...", "def456..."],
  "instance_ids": [12345, 67890, 99999]
}
```

### 4.2 增强 `set_selection` 参数
```json
{
  "action": "set_selection",
  "mode": "replace" | "add" | "remove",       // 新增: 增量选择
  "identifiers": [                              // 现有: 支持路径 / instance_id / asset guid
    { "kind": "gameobject_path", "value": "Scene/Root/Enemy" },
    { "kind": "instance_id", "value": 12345 },
    { "kind": "asset_guid", "value": "abc123..." }
  ],
  "active_context": { "kind": "asset_path", "value": "Assets/Prefabs/" }  // 可选
}
```

### 4.3 新增 `set_selection_by_query`
```json
{
  "action": "set_selection_by_query",
  "query": {
    "scope": "scene" | "project",
    "component_type": "Rigidbody",        // scene: 有该组件的 GameObject
    "asset_filter": "t:Prefab l:MyLabel", // project: AssetDatabase.FindAssets 语法
    "include_inactive": true
  }
}
```
底层: `FindObjectsOfType<T>(true)` 或 `AssetDatabase.FindAssets` → 组 Selection.objects.

### 4.4 新增 `push_selection` / `pop_selection` (选做)
栈式保存恢复. Session 内的暂存 API, 存到 `SessionState`. 用于 S5 场景更省心的 workflow, 但可选. **建议先不做**, 让 agent 自己 get + set 手工存.

## 5. 前置依赖

- **Undo**: 不涉及 (Selection 不入 Undo)
- **Version Defines**: 不需要
- **反射**: 不需要
- **Play Mode**: Selection 在两种模式都可用, 无约束
- **性能**: FindObjectsOfType 大场景 (10K GameObject) 需 ≈ 50-100 ms, 单次可接受; 但 `set_selection_by_query` 频繁调用会拖累主线程, 需在 Description 里提示 agent "查询后缓存 instanceIDs, 不要循环调用"

## 6. 投入估算

**乐观估**: 1 天.

- 半天: 读 `HandleGetSelection` / `HandleSetSelection` 全文, 判定 shallow 具体缺项; 扩返回字段
- 半天: `set_selection_by_query` 实现 + 结构化返回 + SOUL description 更新

**风险点**:
- 向后兼容: `get_selection` 返回结构从"扁平字符串"变"结构化对象"会破坏现有 agent workflow. 建议**新旧字段并存**, 老字段标记 deprecated 在 SOUL 里引导 agent 改用新字段, 一到两版后再删.

## 7. 优先级建议

**P1 高**. Selection 是**几乎所有 GameObject / Asset 操作的入口**, 深化能力后 agent 描述用户意图更精准 (e.g. "帮我处理选中的" → 一次 get_selection 全信息). 与 G07 (CompilationPipeline) 并列 v1.9.0 工作流层最高价值项.
