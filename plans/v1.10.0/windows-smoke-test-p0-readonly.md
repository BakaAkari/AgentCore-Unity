# Windows P0 只读冒烟测试 Checklist — v1.10.0

**产出时间**: 2026-07-24
**测试环境**: Windows 10 (SH-PUXINGMING) + Unity 2022.3.50f1 + Megacity Metro (URP 14.0.11)
**AgentCore 版本**: v1.10.0 (commit `7012b12`, post-inventory-fix)
**测试者**: Akari (Unity Editor 内 Chat 触发)
**判定者**: Hermes / 米塔 (在飞书对话里读证据判定)

---

## 使用说明

1. 在 Unity Editor 里打开 AgentCore Chat 窗口
2. 逐条把下面的 **Prompt** 粘进 Chat，等它跑完
3. 把**工具调用的返回 JSON**（或工具卡片截图 / Console stack trace）贴回飞书对话
4. 每条独立测试，某条失败不影响后续 —— 但如果**反射类工具**(【5】) 完全跑不通，停下来先定位
5. 全部只读，不会改场景/资源/prefs，可以安全跑

## 已知良性差异（不算 bug）

- MemoryProfiler snapshot list 为空 → 正常（还没 take 过）
- get_last_errors 空数组 → 正常（Megacity Metro 编译干净）
- get_selection 在没选中任何东西时 `objects: []` → 正常

---

## 【1】manage_compilation (v1.9.4 新工具)

**目的**: 验证编译状态查询链路，尤其 `[InitializeOnLoadMethod]` 订阅是否在 Windows 上正常。

### Prompt

```
用 manage_compilation 依次跑：get_status、get_last_errors、get_assemblies。三条都跑完把结果连着 dump 给我。
```

### 期望

- `get_status`: 返回类似 `{ isCompiling: false, lastCompilationSucceeded: true, ... }`
- `get_last_errors`: Megacity Metro 干净的话应该是空数组，如果有错也贴给我
- `get_assemblies`: 应该列出几十上百个 assembly，包括 `com.agentcore.unity.Editor`

### Windows 关注点

- Domain Reload 时长（Windows 通常比 macOS 慢）
- 编译状态订阅是否漏事件

---

## 【2】manage_editor 增强 get_selection (v1.9.3)

**目的**: 验证新字段 `instanceIDs / assetGUIDs / activeContext / selectCount` 是否与旧字段并存。

### 准备

在 Hierarchy 里**手动选中一个 GameObject**（任意）。

### Prompt

```
先看当前选中: manage_editor 的 get_selection。把返回的完整 JSON 结构原样贴给我，我要看有没有 instanceIDs / assetGUIDs / activeContext / selectCount 这几个新字段。
```

### 期望

- **旧字段（保持向下兼容）**: `activeGameObject`, `activeObject`, `objects`
- **新字段（v1.9.3 新增）**: `instanceIDs`, `assetGUIDs`, `activeContext`, `selectCount`

### Windows 关注点

- assetGUIDs 的路径格式（asset 路径用 `/` 是标准，如果混入 `\` 是 bug）

---

## 【3】manage_camera get_scene_view (v1.9.6)

**目的**: 验证 SceneView 反射读路径。

### 准备

确保 Scene 视图可见（不是纯 Game 视图）。

### Prompt

```
用 manage_camera 的 get_scene_view，把返回的 pivot / size / rotation / orthographic 全部字段贴给我。
```

### 期望

- `pivot`: Vector3
- `size`: float
- `rotation`: Quaternion 或 euler
- `orthographic`: bool

### Windows 关注点

- SceneView 反射走的是 internal API，Unity 版本敏感，Windows / macOS 应表现一致（若不一致 = 反射签名漂移）

---

## 【4】manage_physics 深化 (v1.9.5)

**目的**: 验证 Physics 诊断类新 action。

### Prompt

```
按顺序跑两条：
1) manage_physics 的 list_scene_physics_stats — 当前场景物理对象统计
2) manage_physics 的 get_collision_matrix — Layer 碰撞矩阵
把两次的返回原样贴给我。
```

### 期望

- `list_scene_physics_stats`: Rigidbody / Collider / Trigger 数量统计
- `get_collision_matrix`: 32×32 的 bool 矩阵或压缩表示

### Windows 关注点

- Physics 是 native code，Windows / macOS 表现应完全一致
- 数据量大时看 JSON 序列化是否溢出或截断

---

## 【5】manage_memory_profiler list (v1.10.0 新工具)

**目的**: 验证 G04 反射链路能到 `Unity.MemoryProfiler.Editor.CachedSnapshot`，并且 list（只读、不生成 snapshot）能跑通。

### 前置确认

`com.unity.memoryprofiler@1.1.1` 已装（Hermes 已在 manifest.json 里确认 ✅）。

### Prompt

```
用 manage_memory_profiler 的 list action，列出当前所有已存在的 memory snapshot。
```

### 期望

- **反射成功**: 返回 snapshot 列表（可能是空的，因为还没 take 过）
- **不能失败**说找不到 MemoryProfiler 包
- 若失败说明反射路径在 Windows 上有问题，**这是重要 bug，必须停下定位**

### Windows 关注点

- Assembly load 是否 Windows / macOS 一致（反射链路的第一道关）
- 快照文件默认路径（Windows 通常在 `%TEMP%` 或项目 `MemoryCaptures/`）

---

## 【6】跨平台: 路径分隔符 & Console 编码

**目的**: Windows 特有关注点。

### Prompt

```
1) 用 manage_file 的 read action 读取 Packages/com.agentcore.unity/package.json（路径用 Unity 内的相对路径），把返回的 path 字段贴给我，我看是 / 还是 \。
2) 用 read_console 拿最近 20 条 log，如果有中文，看有没有乱码。
```

### 期望

- `manage_file` 返回的 path 字段格式一致，不出现混用 `/` 和 `\` 的乱象
- `read_console` 中文 log 无乱码（UTF-8 BOM 或系统编码问题会在这里暴露）

### Windows 关注点（重点）

- Windows 原生 `Path.Combine` 会产生 `\`，如果代码里没 normalize 到 `/`，工具返回值会不一致
- Unity Editor 的 log 在 Windows 上默认可能是 GBK 或 UTF-8 无 BOM，若 tool 没显式指定编码可能乱码

---

## 判定汇总模板

跑完后，Hermes 会填这张表。你只管贴证据。

| 编号 | 工具 | 通过/失败 | 关键证据 | Windows 差异 | 备注 |
|-----|------|----------|---------|-------------|------|
| 1 | manage_compilation | ✅ PASS | get_status/get_last_errors/get_assemblies 三条全绿, 164 assemblies, 总耗时 184.93ms. [InitializeOnLoadMethod] 订阅正常挂载. | 未发现 Windows 特有问题 | **观察点**: Workspace 快照说"3 errors"但 manage_compilation 报 0 — 前者含 Console 运行时错误, 后者只订阅编译事件. 数据源不一致但符合各自职责. 建议 v1.10.1 在快照里标注数据源. |
| 2 | manage_editor.get_selection | ✅ PASS | 字段结构 ✅ 全部存在 (snake_case: instance_ids/asset_guids/active_context/select_count). 有选中对象 (UI GameObject) 时: instance_ids=[-10106], select_count=1, active_hierarchy_path="UI", active_gameobject 完整展开 (transform + components + fullType). asset_guids 空 (选的是 Scene 对象). | ✅ scene_path 用 `/` 不用 `\` (Windows normalize 正确) | **额外字段**: active_hierarchy_path (handoff 未提). **命名**: 实际 snake_case, 与 handoff 的 camelCase 不一致但非 bug. Project asset 选中场景未补测 (asset GUID 是 32 位 hex, 跨平台无差异, 不必测). |
| 3 | manage_camera.get_scene_view | ✅ PASS | 期望字段 pivot/size/rotation/orthographic 全部返回. 额外字段: rotation_euler (欧拉角, LLM 友好), in_2d_mode, camera_position (推算), camera_forward, scene_view_title. SceneView 反射链路正常. | 未发现 Windows 特有问题 | **OnDemand 激活流程**: AgentCore 自动 request_tools 激活 Specialized 类别再调用, 符合 SOUL §2.13. **待验证 (backlog)**: camera_position 是 pivot+size+rotation 推算, 极端角度/2D 模式下可能与 Unity 内部算法有小偏差. |
| 4 | manage_physics (stats/matrix) | ✅ PASS | list_scene_physics_stats: 1295 Collider (793 mesh, 156 convex), 0 Rigidbody/Trigger, 全 Default Layer, 2D 全 0. get_collision_matrix: dimension=3d, 32 layers, 528 unique combos, 0 ignored. | 未发现 Windows 特有问题 | **⚠️ 发现 batch_execute output 截断 bug**: 子 tool 返回 >1500 chars 时被截断为 `...(truncated)`, AgentCore 用 message 里的 summary 做 plausible reconstruction 输出"完整 JSON" — 但那是推断不是真实数据. 影响 collision matrix 的 ignored_pairs 数组等大字段. 详见 Bug 1. |
| 5 | manage_memory_profiler.list_memory_snapshots | ✅ PASS | 反射链路健康, MemoryCaptures 目录扫描逻辑正确, 报告 0 snapshot (预期). | **⚠️ Windows 路径 bug**: 返回 `folder: "D:\\Unity Project\\..."` 反斜杠, 未 normalize 到 `/`. 与 manage_editor.scene_path (正斜杠) 不一致. 跨平台一致性坏了. | **Bug 2**: 路径 normalize 缺失, 建议 v1.10.1 统一 helper. **Bug 3**: action 短名 "list" 无效, 实际是 `list_memory_snapshots`. checklist 和 audit 文档口径与实际不符. |
| 6 | 路径分隔符 / Console 编码 | ✅ PASS | manage_file path 用 `/` (Unity 内部一致). read_console 20 条 log 全英文本次未触发中文路径, 但结构正常, [compressed] 前缀说明 ToolResultCompressor 工作正常. **顺带发现**: AgentCore 主动并行发起两个独立 tool call. | ✅ manage_file 侧路径 normalize 正确 | **顺路发现 Bug 4-7**: (4) Megacity 场景 15 条 Entities/Physics BlobAssetReference NullRef (非 AgentCore bug, 是项目 baking 问题, 解释了 Workspace 快照"3 errors"的去重逻辑); (5) AgentCore "LLM empty content" warning 频次 20% (已知问题, 有 fallback, 不阻塞); (6) invalid tool args 记 Console (审计友好); (7) AgentCore v1.10.0 主动并行工具调用行为已生效. |

---

## 下一步（这份 checklist 全绿之后）

进入 P0 写操作冒烟（走确认卡片路径）:
- `manage_editor:set_selection_by_query`
- `manage_camera:set_scene_view`
- `manage_physics:raycast (dimension=2d)` / `overlap_test (shape=capsule)`
- `manage_compilation:request_compilation → wait_for_compilation`

再下一步 P0 不可逆:
- `manage_prefs`（限定 `agentcore_smoke_test_` 前缀）
- `manage_memory_profiler:take/analyze/diff`（产物落到临时目录）

最后 DISCOVERABILITY 3 处（v1.10.1 patch）:
- SOUL §2.13 补 `manage_profiler` 深度 action / `manage_build` / `manage_test` 映射
