# G05 — PhysicsDebugger 数据（Physics 诊断查询）

> **领域**: Profiler / P1
> **状态**: 起草
> **审计日期**: 2026-07-23

## 1. 场景推演

- **S1**: 用户报"角色卡进墙里, 排查是哪个 collider 有问题". Agent 应能: 在指定坐标附近做 OverlapSphere/OverlapBox, 列出所有 collider (含 layer / trigger / GameObject), 找出可能重叠的.
- **S2**: 用户报"performance 掉帧, 疑似 Physics 频繁 raycast". Agent 应能: 查询 Physics 全局设置 (`defaultSolverIterations` / `sleepThreshold` / `bounceThreshold` / `defaultMaxAngularSpeed`) + 场景内所有活跃 Rigidbody 数量 / Collider 数量 / 各 layer 的 collision matrix.
- **S3**: 用户想验证"从相机中心发射一条 100m 射线, 打到什么". Agent 应能: `Physics.Raycast` 参数化调用, 返回命中列表 (GameObject / collider / distance / normal / hit point).
- **S4**: 用户想找"所有可能碰撞的 layer 对". Agent 应能: 遍历 `Physics.GetIgnoreLayerCollision(i, j)` 全 32×32 矩阵, 返回可视化表.
- **S5**: 2D 项目场景. Agent 应能: 对 Physics2D 做同样查询 (OverlapAreaAll / OverlapCircleAll / raycast2D).

## 2. Unity API 表

**Physics (3D)**:

| API | 用途 |
|---|---|
| `Physics.OverlapSphere / OverlapSphereNonAlloc` | 球范围查 collider |
| `Physics.OverlapBox / OverlapBoxNonAlloc` | box 范围查 |
| `Physics.OverlapCapsule / OverlapCapsuleNonAlloc` | capsule 查 |
| `Physics.Raycast / RaycastAll / RaycastNonAlloc` | 射线查 |
| `Physics.SphereCast / SphereCastAll` | sphere sweep |
| `Physics.CheckSphere / CheckBox / CheckCapsule` | 快速布尔查询 |
| `Physics.GetIgnoreLayerCollision(l1, l2)` | layer collision matrix 查 |
| `Physics.defaultSolverIterations / sleepThreshold / bounceThreshold / ...` | 全局设置 |
| `PhysicsScene.Raycast / OverlapSphere` | multi-scene 场景查询 |

**Physics2D**: 与上面对应, `Physics2D.OverlapArea / OverlapCircle / Raycast / etc.`.

**PhysicsDebugger 菜单** (`Window > Analysis > Physics Debugger`) 是 GUI 层, 无公共 API 直接调用其数据. **重要**: 它的数据本质是上面 API 的可视化, agent 不需要"打开 PhysicsDebugger 窗口拉数据", 直接用 Physics.* API 就够.

**Undo**: 只读查询, 不涉及.
**Reflection**: 不需要, Physics API 全部 public.

## 3. 现有覆盖诊断

`manage_physics` actions ([`ManagePhysicsTool.cs`](../../Editor/Tools/Native/Specialized/ManagePhysicsTool.cs)):

| Action | 场景 | 是否覆盖 G05 |
|---|---|---|
| `raycast` | S3 单条 raycast | ✓ 部分 (可能只支持单射线) |
| `overlap_test` | S1 范围查 | ✓ 部分 |
| `get_settings` / `set_settings` | S2 全局设置读写 | ✓ |
| `add_rigidbody` / `add_collider` / etc. | 创建, 不属查询 | 无关 |

**根因分类**: `SHALLOW_TOOL`. `raycast` 和 `overlap_test` 存在但可能:
- 只支持 3D 不支持 2D
- 不支持 NonAlloc 批量查询 (10000 单位场景一次 100 raycast 性能敏感)
- 不返回 layer collision matrix 全景
- 无 Rigidbody / Collider 场景计数统计 (S2 场景)

**必须读源码确认 shallow 程度** (先假设为 shallow, 需下一轮读 `ManagePhysicsTool` 全文验证).

## 4. 建议 action 接口

**归属工具**: `manage_physics` (深化) — **不新建**, 已在的 raycast/overlap_test 扩展即可.

### 4.1 扩展 `raycast`
```json
{
  "action": "raycast",
  "mode": "single" | "all",           // 新增: RaycastAll 批量
  "origin": [x, y, z],
  "direction": [x, y, z],
  "max_distance": 100.0,
  "layer_mask": -1,                    // 新增: 参数化 layer mask
  "query_trigger_interaction": "collide" | "ignore" | "use_global",
  "dimension": "3d" | "2d"             // 新增: 支持 Physics2D
}
```

### 4.2 扩展 `overlap_test`
```json
{
  "action": "overlap_test",
  "shape": "sphere" | "box" | "capsule",
  "center": [x, y, z],
  "half_extents": [x, y, z],           // box 用
  "radius": 1.0,                       // sphere / capsule
  "orientation": [x, y, z, w],         // box / capsule quat
  "layer_mask": -1,
  "dimension": "3d" | "2d"
}
```

### 4.3 新增 `list_scene_physics_stats`
```json
{ "action": "list_scene_physics_stats" }
```
返回: `{ rigidbody_count, static_collider_count, kinematic_collider_count, trigger_count, per_layer_object_counts: {...} }`. 场景性能诊断 (S2).

### 4.4 新增 `get_collision_matrix`
```json
{ "action": "get_collision_matrix" }
```
返回: `{ layers: [{ index, name }], matrix: [[bool, bool, ...]] }` 32×32 (S4).

## 5. 前置依赖

- **反射**: 不需要
- **Version Defines**: 不需要 (Physics 是 Unity core)
- **Undo**: 不涉及 (只读)
- **Play Mode**: raycast / overlap 在 Edit Mode 也能查 (Unity 会做静态碰撞测试), 与 G02 read_frame 一样两种模式都可用
- **性能**: 全 32×32 collision matrix 是 O(1024), 场景 stats 遍历所有 GameObject 是 O(n), 大场景需 `EditorApplication.delayCall` 分帧 (但 10000 GameObject 也就几十 ms, 一般不需要)

## 6. 投入估算

**乐观估**: 1 天.

- 半天: 读 `ManagePhysicsTool` 现有 raycast/overlap_test 源码, 确认 shallow 程度; 扩 `dimension` / `mode` / `layer_mask` 参数
- 半天: `list_scene_physics_stats` + `get_collision_matrix` 新增, 结构化返回, SOUL description 更新

**风险点**:
- 与 `raycast` / `overlap_test` 现有 caller 的向后兼容 (v1.8.1 tarball 已在用户手上). 新增可选参数不破坏, `mode` 默认 `single`, `dimension` 默认 `3d`.

## 7. 优先级建议

**P1 中**. 场景 S1/S3 用户实际会遇到, S2 场景性能诊断价值高但可用现有 `read_console` + `scene_analysis.performance_hints` 部分覆盖. **相对于 G04 更靠后**, 主要因为 execute_code 也能在紧急场景兜底 (Physics.Raycast 是 public API).
