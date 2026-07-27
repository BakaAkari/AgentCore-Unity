# Windows P0 遗留验证 Checklist — v1.10.0 收尾

**产出时间**: 2026-07-27
**测试环境**: Windows 10 (SH-PUXINGMING) + Unity 2022.3.50f1 + Megacity Metro (URP 14.0.11)
**AgentCore 版本**: v1.10.1 (commit `31ffcfa`)
**测试者**: Akari (Unity Editor 内 Chat 触发)
**判定者**: Hermes / 米塔 (在飞书对话里读证据判定)

---

## 使用说明

1. 在 Unity Editor 里打开 AgentCore Chat 窗口
2. 逐条把下面的 **Prompt** 粘进 Chat，等它跑完
3. 把**工具调用的返回 JSON**（或工具卡片截图 / Console stack trace）贴回飞书对话
4. 每条独立测试，某条失败不影响后续
5. 本清单**包含写操作**，会改变 selection / scene view / prefs / 场景对象。**建议在 Megacity Metro 的空场景或临时 backup 场景上跑**，避免污染主场景。
6. `manage_prefs` 的所有写测试用 test-only 前缀 `agentcore_test_` 便于事后清理

## 已知良性差异（不算 bug）

- `set_selection_by_query` 在场景里没有匹配对象时 `selectCount:0` → 正常
- `manage_prefs delete_all` 无 `confirm_delete_all:true` 时返回 error → 正常（保护生效）

---

## 【W1】manage_editor.set_selection_by_query — Scene scope

**目的**: 验证 G06 的新 `set_selection_by_query` 在 scene scope 下按 component_type 批量选中。

### 准备

在 Hierarchy 里把当前选中清空（点空处）。

### Prompt

```
用 manage_editor.set_selection_by_query 在 scope=scene 下选中所有带 Camera 组件的对象，mode=replace。跑完把返回 JSON 完整贴给我。
```

### 期望

- 返回结构含 `selectCount`, `instanceIDs`, `activeGameObject`
- Megacity Metro 场景一般至少有 Main Camera 一个，可能更多（如 UI Camera）
- Unity Hierarchy 里应该能看到相应对象被高亮

### 关注点

- `selectCount` 是否 > 0
- 返回的 `instanceIDs` 数组长度是否 == `selectCount`
- 有没有 `activeContext` 字段

---

## 【W2】manage_editor.set_selection_by_query — mode=add 增量

**目的**: 验证 `mode=add` 追加选择而不清空原有选择。

### 准备

【W1】刚做完，Hierarchy 里已有 Camera 被选中。

### Prompt

```
现在用 manage_editor.set_selection_by_query 在 scope=scene 下追加选中所有带 Light 组件的对象，mode=add。跑完把返回 JSON 完整贴给我。
```

### 期望

- 新的 `selectCount` = 原 Camera 数 + Light 数
- 之前的 Camera 仍在 selection 里
- `instanceIDs` 数组包含两类对象

### 关注点

- **验证 `mode=add` 是否真的追加而非替换**（这是 G06 的核心新语义）

---

## 【W3】manage_editor.set_selection_by_query — Project scope + asset_filter

**目的**: 验证 project scope 下按 asset filter 选中资源。

### Prompt

```
用 manage_editor.set_selection_by_query 在 scope=project 下选中所有 t:Material 类型的资源，mode=replace，限制 max_count=5。跑完把返回 JSON 完整贴给我。
```

### 期望

- 返回 `assetGUIDs` 数组（不是 instanceIDs，因为是资源）
- `selectCount` <= 5（Megacity Metro 有大量 .mat 资源）
- Unity Project 窗口应该定位到这些资源

### 关注点

- `assetGUIDs` 字段是否存在且非空（这是 G06 新增字段）
- 是否有 `activeContext` = "project"

---

## 【W4】manage_camera.set_scene_view — Quaternion 输入

**目的**: 验证 G09 的 SceneView 直接控制，quaternion 输入形式。

### 准备

先跑 `manage_camera.get_scene_view` 记下当前 pivot 值（用于事后还原）。

### Prompt (2 步一起发)

```
分两步：
1. 先跑 manage_camera.get_scene_view 记录当前状态
2. 然后跑 manage_camera.set_scene_view 参数：pivot={x:0, y:10, z:0}, size:20, rotation={x:0, y:0, z:0, w:1}, orthographic:false
把两步的返回 JSON 都贴给我。
```

### 期望

- Step 1 返回含 pivot / size / rotation (quaternion) / rotation_euler / orthographic
- Step 2 后 SceneView **视角明显变化**（用户目视）
- Step 2 返回结果含变更后的字段值

### 关注点

- **Quaternion `{0,0,0,1}` 是否被识别为 identity（不该被归零处理成 `{0,0,0,0}`）**
- Repaint 是否自动触发（用户能看到 SceneView 立即变化）

---

## 【W5】manage_camera.set_scene_view — Euler 输入 + 2D 切换

**目的**: 验证 rotation 用 Euler 三值输入 + in2DMode 切换。

### Prompt

```
跑 manage_camera.set_scene_view 参数：pivot={x:5, y:5, z:5}, size:15, rotation={x:30, y:45, z:0}, in2DMode:true。跑完贴 JSON。
```

### 期望

- `rotation` 三字段无 `w` 时应识别为 Euler 度数
- SceneView 切换到 2D 模式（顶视图正交）
- 返回结果 rotation 换算后的 quaternion 值

### 关注点

- **无 `w` 的 rotation 是否正确识别为 Euler 而非 Quaternion**（这是 G09 的关键分支逻辑）
- `in2DMode:true` 是否立即生效

### 事后还原

跑 `manage_camera.set_scene_view` 传【W4】step 1 记下的原 pivot / size 恢复。

---

## 【W6】manage_physics.raycast — mode=all + dimension=2d

**目的**: 验证 G05 的 raycast 2D 支持 + all 模式（v1.9.5 新增）。

### Prompt

```
先跑 manage_physics.raycast，参数：origin={x:0, y:100, z:0}, direction={x:0, y:-1, z:0}, distance:200, mode=all, dimension=2d。把 JSON 完整贴给我。
```

### 期望

- **不报错**（哪怕场景里 2D 物理为空，正确行为是返回 `hits: []` 而非崩溃）
- 返回结构含 `mode`, `dimension`, `hits` 数组
- Megacity Metro 是 3D 项目，2D 一般为空，属正常

### 关注点

- **2D 分支是否根本没走通**（若报 "unknown dimension" 或类似错，就是 G05 落地缺陷）
- 有没有 layer_mask 参数支持（不用传，但看返回结构里有没有）

---

## 【W7】manage_physics.raycast — 3D 正常打场景

**目的**: 验证 3D raycast 主路径。

### 准备

Megacity Metro 场景应该有地面 mesh collider。

### Prompt

```
manage_physics.raycast，参数：origin={x:0, y:1000, z:0}, direction={x:0, y:-1, z:0}, distance:2000, mode=first, dimension=3d, layer_mask=-1。JSON 贴给我。
```

### 期望

- 有 hit（因为往下打得远，肯定命中场景）
- `hits[0]` 含 `point`, `normal`, `distance`, `collider`, `gameObject`

### 关注点

- 是否正确命中 Megacity Metro 主地形
- `mode=first` 是否只返回一个 hit

---

## 【W8】manage_physics.list_scene_physics_stats

**目的**: 验证 G05 新增的场景 physics 统计。

### Prompt

```
跑 manage_physics.list_scene_physics_stats。完整 JSON 贴给我。
```

### 期望

- 返回 rigidbodyCount / colliderCount / triggerCount 等聚合
- Megacity Metro 是大场景，数字应该 > 100

### 关注点

- 数字是否合理（若返回 0 而场景明显有物理对象，就是统计逻辑 bug）

---

## 【W9】manage_prefs — set/get/delete 循环

**目的**: 验证 G08 的 pref 读写 + High risk 二次确认。

### Prompt

```
跑一个 pref 生命周期完整测试：
1. manage_prefs set store=editor key=agentcore_test_key value_string="hello_world"
2. manage_prefs get store=editor key=agentcore_test_key
3. manage_prefs delete store=editor key=agentcore_test_key
4. manage_prefs has store=editor key=agentcore_test_key
四步的返回 JSON 都贴给我。set/delete 应该触发工具确认面板，我会点确认。
```

### 期望

- Step 1: 返回 success=true，注意**是否弹出确认面板**（因为 RiskLevel=High）
- Step 2: 返回 value=`"hello_world"`
- Step 3: 返回 success=true
- Step 4: 返回 has=false

### 关注点

- **确认面板是否正确弹出**（High risk 的核心保护）
- has/get 是否走 ReadOnlyActions 快速路径（**不该弹确认**）
- summary 是否明确写 "This action is NOT undoable"

---

## 【W10】manage_prefs.delete_all — 双保险验证

**目的**: 验证 delete_all 无 `confirm_delete_all:true` 时被拒绝。

### Prompt

```
跑 manage_prefs delete_all store=editor（**不加** confirm_delete_all）。看它是不是拒绝我。
```

### 期望

- 返回 error，明确说需要 `confirm_delete_all:true`
- **绝对不能**真的把 EditorPrefs 清空

### 关注点

- **这是最重要的安全测试**：如果这条失败，我们就有一个能一键抹掉用户所有 EditorPrefs 的工具在网上跑

**⚠ 请在 Editor 内部先手动 EditorPrefs.SetString/GetString 存几个测试值，方便验证一旦真的 delete_all 生效可以观察后果。但不要跑真删除。**

---

## 【W11】manage_memory_profiler — 抓 snap + list + analyze 完整循环

**目的**: 验证 G04 真实工作流（v1.10.0 的核心新工具）。

### 准备

1. Unity Package Manager 里**装 `com.unity.memoryprofiler`** 包（analyze/diff 需要，take/list 不需要）
2. 进入 Play Mode（或 Edit Mode 也行，snap 都能抓）

### Prompt (分步)

```
第一步：manage_memory_profiler take_memory_snapshot path=MemoryCaptures/agentcore_test_before.snap wait_seconds=60
（等它抓完，snap 文件生成通常 5-30 秒）
返回 JSON 贴给我。
```

**等这条完成后再发下一条**：

```
第二步：manage_memory_profiler list_memory_snapshots
JSON 贴给我，应该能看到 agentcore_test_before.snap
```

```
第三步：manage_memory_profiler analyze_memory_snapshot path=MemoryCaptures/agentcore_test_before.snap top_n=20
JSON 贴给我
```

### 期望

- Step 1: 返回 `path` 指向真实生成的 .snap 文件（**注意验证 v1.10.1 的 path normalize 修复：应该是正斜杠 `MemoryCaptures/...` 而非反斜杠 `MemoryCaptures\\...`**）
- Step 2: `folder` 字段用正斜杠 + `snapshots[].path` 用正斜杠
- Step 3: 返回 `entry_counts` 含 nativeObjects / typeDescriptions / gcHandles 等 14 类计数

### 关注点

- **v1.10.1 的 B2 修复是否生效**：全部 path 字段应该是正斜杠
- Snap 文件是否真的落到磁盘
- analyze 是否需要 memoryprofiler package（若没装应 graceful fail）

### 事后清理

删除 `MemoryCaptures/agentcore_test_before.snap`

---

## 【R1】G03 FrameDebugger 真实数据采集验证（**遗留**）

**背景**: v1.10.0 handoff §2.5 明确 G03 反射链路 OK，但真实 draw event 采集未回头。

### 准备

1. 进入 Play Mode（场景必须在渲染）
2. 建议开 Silent Mode（v1.8.7+），释放主线程给 GameView

### Prompt

```
分步：
1. 跑 manage_profiler list_draw_events (让它自己 enable frame debugger)
2. 等它返回 event 列表（count > 0）
3. 挑第一个 event ID，跑 manage_profiler get_draw_event event_id=<那个ID>
4. 最后跑 manage_profiler disable_frame_debugger

四步 JSON 全贴给我。
```

### 期望

- Step 1: `count` > 0（真实 URP 场景有几百 draw events）
- Step 2 → Step 3: `get_draw_event` 返回 shader / material / mesh / passName 等具体信息
- Step 4: 关闭成功

### 关注点

- **count 若为 0 → 反射链路虽通但采集失败**（记录环境细节：SessionMode、Play Mode 状态、URP 版本）
- Step 3 若返回 null / empty → FrameDebuggerUtility.GetDrawCallInfo 反射失败
- 记下 count 数字，作为 Megacity Metro URP 基准

### 若失败

不阻塞其他，如实报告，我记录到 v1.11 backlog。

---

## 【R2】G10 URP volume_list/get/set 真实验证（**遗留**）

**背景**: v1.10.0 handoff §2.6 明确 G10 Built-in fallback OK，URP/HDRP 项目未跑。Megacity Metro 是 URP 项目正好补上。

### 准备

Megacity Metro 场景里应该有 Volume（全局 post-process，通常在 Environment/Global Volume 之类）。若没有，先手动放一个 Global Volume + 分配一个 VolumeProfile。

### Prompt (分步)

```
第一步：manage_graphics volume_list
JSON 贴给我，应该看到 Global Volume 之类
```

**注意 `volume_list` 里挑一个 volumeId 或 path**：

```
第二步：manage_graphics volume_get volume_id=<上一步的ID> 或 path=<hierarchy path>
JSON 贴给我，应该看到 profile 里的 override（如 Bloom / Tonemapping / ColorAdjustments）
```

```
第三步（低风险写操作，改一个不会毁项目的字段）：
manage_graphics volume_set volume_id=<ID> component=Bloom property=intensity value=0.5
JSON 贴给我
```

### 期望

- Step 1: `volumes` 数组 > 0（Megacity Metro URP 肯定有）
- Step 2: `overrides` 里列出 profile 里已启用的组件 + 各自参数
- Step 3: `changed=true`，Scene View 里能看到 Bloom 强度变化

### 关注点

- **是否触发 `AGENTCORE_HAS_SRP_CORE` 反射路径**（fallback stub 应该不生效）
- 反射能否命中 URP 的 Bloom 组件
- 修改是否真的在 Scene 里可见

### 事后还原

Step 3 后跑 `manage_graphics volume_set` 把 intensity 改回原始值（Step 2 记下）。

---

## 判定汇总模板

跑完全部（或部分）后，把结果按下面格式扔给我：

```
【W1】✅ PASS / ❌ FAIL — 简述
【W2】...
...
【R1】...
【R2】...

意外发现：
- ...
```

我会：
1. 逐条判定
2. 记录任何真实 bug 到 v1.10.2 backlog（或立即修）
3. 更新 ROADMAP §3.w.1 遗留问题 checklist
4. 汇总一份"v1.10.0 收尾完成报告"

---

## 附录 A — 若某工具完全跑不通该怎么办

- **报错 "Unknown action"** → schema 里 action 名不对，贴报错文本，我查 audit 文档对照
- **报错 "Invalid arguments"** → 参数结构问题，贴报错，我给正确格式
- **返回 `success:false` 但无 error** → 底层反射静默失败，贴完整 JSON，我查代码定位
- **Editor 弹 stack trace** → 贴 Console 完整栈，我判断是不是 v1.10.1 patch 引入的
- **工具卡死超过 60 秒** → 停掉 (点 Cancel)，贴当时的状态，可能是 Silent Mode 相关

---

**开工提示**: 全部 13 条大概 30-45 分钟能跑完。若时间紧，优先级顺序：**R1 R2 > W10 (安全) > W9 W11 (v1.10 核心新工具) > 其他**。
