# G05 — ManagePhysicsTool Shallow 精确 Gap 清单

> **审计日期**: 2026-07-24
> **审计范围**: [`ManagePhysicsTool.cs`](../../Editor/Tools/Native/Specialized/ManagePhysicsTool.cs) 1-800 行全文
> **父规划**: [`../v1.9.0-candidate-matrix/G05-physics-debugger.md`](../v1.9.0-candidate-matrix/G05-physics-debugger.md)
> **目的**: v1.10.0 步骤 4 (G05 深化) 实施前，锁死"新增什么 / 扩展什么 / 保留什么"，避免破坏 v1.8.1+ 已发布 tarball 用户手中的 caller 兼容性。

## 1. 现状概览

现有 10 个 action，与查询相关的只有 3 个：

| Action | 位置 | 现状签名 |
|---|---|---|
| `raycast` | [`ManagePhysicsTool.cs:469`](../../Editor/Tools/Native/Specialized/ManagePhysicsTool.cs:469) | `origin` + `direction` + `max_distance` + `layer_mask` (单 layer 名) |
| `overlap_test` | [`ManagePhysicsTool.cs:685`](../../Editor/Tools/Native/Specialized/ManagePhysicsTool.cs:685) | `position` + `shape` (sphere/box) + `radius`/`size` + `layer_mask` |
| `get_settings` / `set_settings` | [`ManagePhysicsTool.cs:202`](../../Editor/Tools/Native/Specialized/ManagePhysicsTool.cs:202) | 全局 Physics 参数 (gravity / iterations / threshold) |

## 2. 精确 shallow 清单

### 2.1 `raycast` 现有 vs 规划 diff

| 字段 | 现有 | 规划 (§4.1) | Gap |
|---|---|---|---|
| `origin` | ✅ Vector3 | ✅ | 无 |
| `direction` | ✅ Vector3, 内部 `.normalized` | ✅ | 无 |
| `max_distance` | ✅ float, 默认 Infinity | ✅ | 无 |
| `layer_mask` | ⚠️ **仅支持单个 layer 名字符串**，内部 `1 << layer` ([`ManagePhysicsTool.cs:488`](../../Editor/Tools/Native/Specialized/ManagePhysicsTool.cs:488)) | int mask (bitmask) 或多 layer 名数组 | **必须扩展**：接受 int 或 string[]，向后兼容单字符串 |
| `mode` | ❌ 只 `Physics.Raycast` 单命中 | `single` / `all` (RaycastAll) | **必须新增** |
| `query_trigger_interaction` | ❌ 完全无 | 3 枚举值 | **必须新增**，默认 `use_global` |
| `dimension` | ❌ 只 3D，Physics2D 完全无 | `3d` / `2d` | **必须新增**（S5 场景刚需） |

**返回字段现状**：命中时返回 `hit / hitPoint / hitNormal / hitDistance / hitCollider / hitColliderType / hitInstanceId` (line 500-516)。
- `mode=all` 时返回 `hits: [ {...}, {...} ]` 数组
- `dimension=2d` 时 `hitCollider2D` 需带 `RaycastHit2D.centroid / fraction`（2D 独有）

### 2.2 `overlap_test` 现有 vs 规划 diff

| 字段 | 现有 | 规划 (§4.2) | Gap |
|---|---|---|---|
| `position` | ✅ Vector3 | ✅ `center` (**命名不一致**) | ⚠️ 决定：**保留 `position`，新增 `center` 别名**，两者互认，避免破坏兼容 |
| `shape` | ✅ sphere/box | ✅ sphere/box/**capsule** | **必须扩展**：新增 `capsule` |
| `radius` | ✅ sphere 用 | ✅ sphere/capsule 用 | 无 |
| `size` | ✅ box 用作 halfExtents | ✅ `half_extents` (**命名不一致**) | ⚠️ 保留 `size`，新增 `half_extents` 别名 |
| `orientation` | ❌ box 用 `Quaternion.identity` hardcode ([`ManagePhysicsTool.cs:720`](../../Editor/Tools/Native/Specialized/ManagePhysicsTool.cs:720)) | Quaternion (xyzw) | **必须新增**：OverlapBox 支持旋转 |
| `height` | ⚠️ 仅 CapsuleCollider add 用，overlap_test 未接 | 需要 capsule 用 | **随 shape=capsule 新增** |
| `point0` / `point1` | ❌ | OverlapCapsule 用 (两端点) | **必须新增**（capsule 语义） |
| `layer_mask` | ⚠️ 同 raycast，单字符串 | 见 raycast | **同步扩展** |
| `dimension` | ❌ 只 3D | `3d` / `2d` | **必须新增** |
| `query_trigger_interaction` | ❌ | 3 枚举 | **必须新增** |

### 2.3 全新 action

**`list_scene_physics_stats`** ([`G05-physics-debugger.md`](../v1.9.0-candidate-matrix/G05-physics-debugger.md:89)):
- 输入：无
- 输出：`{ rigidbody_count, static_collider_count, kinematic_collider_count, trigger_count, per_layer_object_counts: { layerName: count, ... }, physics_2d: { ... } }`
- 实现：遍历 `UnityEngine.Object.FindObjectsOfType<Rigidbody>() / Collider>()`, 分类计数
- 性能：10k GameObject 场景约 30-80ms，同步执行可接受

**`get_collision_matrix`** ([`G05-physics-debugger.md`](../v1.9.0-candidate-matrix/G05-physics-debugger.md:96)):
- 输入：可选 `dimension` (默认 `3d`)
- 输出：`{ layers: [{ index, name }], matrix: [[bool, ...], ...] }` (32×32)
- 实现：`Physics.GetIgnoreLayerCollision(i, j)` 遍历
- 注意：LayerMask.LayerToName 可能返回空字符串（未命名 layer），保留 index

## 3. 兼容性约束（红线）

1. **`layer_mask` 字段类型**：现在是 string (单 layer 名)。JSON schema 改为 `oneOf: [string, integer, array<string>]` 而不是替换类型；handler 里分支处理，旧 caller 传单字符串继续工作。
2. **`overlap_test.position` vs 规划 `center`**：**保留 `position` 为主字段**（现有 caller 已在用），`center` 作为等价别名，两者同时提供时报错。
3. **`overlap_test.size` 语义**：现有代码明确写作 halfExtents ([`ManagePhysicsTool.cs:717`](../../Editor/Tools/Native/Specialized/ManagePhysicsTool.cs:717) 注释 `overlap box half-extents`)，schema description 也说明。**不要**在 v1.10.0 修改语义。新增 `half_extents` 别名对齐规划术语。
4. **返回字段并存**：`mode=all` 时同时返回 `hit`（首次命中，兼容旧 caller）+ `hits`（数组，新字段）。参考 [`../v1.10.0-handoff.md`](../v1.10.0-handoff.md) §6.6 G06 新旧并存策略。

## 4. 实施顺序（G05 半天到 1 天）

1. **上午 0.5 天**：
   - `raycast`：新增 `mode` / `dimension` / `query_trigger_interaction`；扩展 `layer_mask` 为 oneOf；`mode=all` 走 `Physics.RaycastAll` 返回 `hits` 数组
   - `overlap_test`：新增 `capsule` shape + `orientation` + `half_extents` 别名 + `dimension` + `query_trigger_interaction`
2. **下午 0.5 天**：
   - 新增 `list_scene_physics_stats`
   - 新增 `get_collision_matrix`
   - SOUL 描述更新（[`AgentToolAttribute.Description`](../../Editor/Tools/Native/Specialized/ManagePhysicsTool.cs:19)）
   - Smoke test：Megacity Metro 场景 raycast(all) + overlap(capsule) + collision_matrix

## 5. 遗留 gap（本次不做，v1.11 备选）

- **SphereCast / CapsuleCast** (`Physics.SphereCast/CapsuleCast`)：sweep 查询，G05 规划表列出但 §4 接口未覆盖，v1.11 考虑
- **CheckSphere / CheckBox** (布尔快速查询)：功能被 overlap_test 覆盖，价值低，永久不做
- **PhysicsScene multi-scene 支持**：Megacity Metro 是单主场景，需求罕见，DEFER

## 6. Verification Checklist

- [ ] `manage_physics.raycast(mode=single)` 结果与 v1.9.2 tarball 完全一致（字段名与数值）
- [ ] `manage_physics.raycast(mode=all)` 返回 `hits: []` 数组
- [ ] `manage_physics.raycast(dimension=2d)` 在纯 3D 场景返回 `hit=false, hits=[]` 且不抛异常
- [ ] `manage_physics.overlap_test(shape=capsule, point0, point1, radius)` 返回列表
- [ ] `manage_physics.overlap_test(position=..., orientation=quat)` 旋转 box 命中差异可观察
- [ ] `manage_physics.list_scene_physics_stats` Megacity Metro 场景 <200ms
- [ ] `manage_physics.get_collision_matrix` 返回 32 层完整矩阵
- [ ] `manage_physics.layer_mask` 三种输入（string / int / string[]）都工作
- [ ] `node tools/tool-inventory.cjs` 显示 12 action (10 + 2 新增)
- [ ] CHANGELOG.md v1.9.6 段落
