# v1.10.0 P0 遗留冒烟 — 测试发现汇总（滚动更新）

**起始**: 2026-07-27
**测试者**: Akari (Unity Editor)
**判定者**: Hermes / 米塔

---

## W1 — set_selection_by_query scope=scene component=Camera mode=replace

**结果**: ✅ **PASS**

- 命中 `MainCamera` at `PersistentObjects/HybridCameraManager/MainCamera`
- 场景: `Assets/Scenes/Main.unity`
- `resolved_count=1`, `truncated=false`
- 路径全部正斜杠（v1.10.1 B2 修复间接印证）

**观察**:
- 默认 `include_inactive=True`（合理默认）
- 字段命名 `resolved_count` / `resolved[]` 与 v1.9.3 CHANGELOG 提到的 `selectCount` / `instanceIDs` **不一致** → 记 audit 文档待对齐

---

## W2 — set_selection_by_query scope=scene component=Light mode=add

**结果**: ✅ **PASS**（用户目视 Hierarchy 确认 MainCamera + Light 同时高亮）

- 命中 500 Light（触发 `max_results` 上限）
- 场景切换到 `Assets/Scenes/Main/Level.unity`（Megacity Metro 是 additive multi-scene）
- Hierarchy 里 MainCamera 与 Light 同时高亮 → mode=add 语义生效

**发现的问题（3 项，未阻塞 W2 PASS，记 v1.10.2 backlog）**:

### Bug A — set_selection_by_query 返回结构缺"最终 Selection 状态"（可观测性）

**现象**: `resolved_count` 只反映**本次 query 命中数**，不反映**最终 Selection 里对象总数**。

**后果**: agent 无法从返回值判定 `mode=add` 是真追加还是被静默 fallback 成 replace。W2 这次靠**用户目视 Hierarchy** 才判定，纯 agent 场景是盲区。

**建议修法**:
- 返回结构加 `final_selection_count`（Selection.count 快照）
- 或 `pre_existing_count` + `newly_added_count`
- 或至少 `mode_applied: "add"` 明示

**优先级**: P1（影响 agent 自动化闭环判定）

### Bug B — max_results 触发上限时无分页机制

**现象**: `truncated: true` 但没有 `offset` / `cursor` / `total_matches`，agent 无法拿到剩余匹配对象。

**后果**:
- 大场景下（如 Megacity Metro 500+ Light）agent 不知道**总共有多少**
- 无法通过多次调用拿全量

**建议修法**:
- 加 `total_matches` 字段（哪怕不返回详情也让 agent 知道规模）
- 加 `offset` / `limit` 参数支持分页

**优先级**: P1（大场景可用性）

### Bug C — Tool response compression 需审计

**现象**: W2 的 tool result 被 AgentCore 预处理成 `[compressed]` markdown 摘要而非原始 JSON。instance_id 从精确列表压成范围（如 `~-144168 to -142956`）。

**后果**:
- LLM 拿到的不是原始工具数据
- 若下游做 instance_id 精确操作会 plausible reconstruction，违反 SOUL 诚实性

**当前防护**: 本次 LLM 主动诚实说明"我拿到的并不是完整原始 JSON"→ **SOUL 层引导对了**。

**建议**: 审计 compression 触发条件与阈值，确保：
1. 压缩前完整数据保留在**日志/工具卡片**里可查
2. LLM prompt 强化"看到 [compressed] 标记 → 必须告知用户 + 拒绝做基于精确值的下游操作"（当前 SOUL 层已隐性做到，但需明文规则）

**优先级**: P2（观察项，不是紧急 bug）

---

## 待测（W3-W11 + R1-R2）

- W3: set_selection_by_query scope=project asset_filter
- W4: set_scene_view Quaternion 输入
- W5: set_scene_view Euler 输入 + in2DMode
- W6: raycast mode=all dim=2d
- W7: raycast 3D 打场景
- W8: list_scene_physics_stats
- W9: manage_prefs set/get/delete 循环
- W10: manage_prefs delete_all 双保险（**安全关键**）
- W11: manage_memory_profiler take+list+analyze（**顺路验证 v1.10.1 B2 path normalize**）
- R1: G03 FrameDebugger 真实 event 采集
- R2: G10 URP volume_list/get/set 真实数据

---

## W3 — set_selection_by_query scope=project asset_filter='t:Material' max_count=5

**结果**: ⚠️ **PARTIAL PASS**（工具跑通，但发现 1 个真 bug）

- `resolved_count: 5`, `truncated: true`（触发 max_count 上限）
- 返回结构含 `asset_path` + `asset_guid` + `instance_id` + `type` — 比 scene scope 多 asset 字段
- 路径正斜杠 ✓

### 🔴 Bug D — asset_filter='t:Material' 泄漏 Shader（G06 隐性设计缺陷）

**现象**: 5 个结果里有 2 个 `.shadergraph` 文件，`type` 字段明确是 `Shader` 而非 `Material`。

具体命中：
| # | name | type |
|---|---|---|
| 1 | Shader Graphs/FullscreenShadowCap | **Shader** ❌ |
| 4 | Shader Graphs/ShadowDebug | **Shader** ❌ |

**根因** (`ManageEditorTool.cs` line 850-861):
- `AssetDatabase.FindAssets("t:Material")` 按 **sub-asset type** 匹配 → `.shadergraph` 内含 Material sub-asset，被命中
- `LoadAssetAtPath<UnityEngine.Object>(path)` 加载 **主 asset**（`Shader`）
- 结果：命中依据是 Material sub-asset，返回给用户的却是 Shader 主 asset

**这是 v1.9.3 G06 代码的实现选择，非 Unity API bug**。

**修法（两选一）**:

**方案 A（严格）**: 若 asset_filter 含 `t:Type`，用 `AssetDatabase.LoadAllAssetsAtPath` 拿全部 sub-asset 再按类型二次过滤，返回真实匹配的 sub-asset。

**方案 B（透明）**: 保留当前主 asset 加载，返回结构加 `matched_by_subasset: true` + `subasset_type: "Material"` 字段告诉 agent"你要的 Material 藏在这个 Shader 里"。

推荐 **方案 A**，因为语义上用户/agent 期望 `t:Material` 就是拿 Material，透明化让 agent 处理复杂度不划算。

**优先级**: P1（`t:` filter 的语义准确性影响所有 asset 类型查询）

---

## W4 — manage_camera.get_scene_view + set_scene_view (Quaternion 输入)

**结果**: ❌ **FAIL**（get_scene_view PASS，但 set_scene_view 完全无法工作）

### get_scene_view 侧 ✅ PASS

返回完整结构：
- pivot: `(-5.36, 50.19, -37.95)`
- size: `10.0`
- rotation (quaternion): `{0.0204, 0.9728, 0.0948, -0.2101}` — 有效值，非 identity 非 zero
- rotation_euler: `{348.86, 204.37, 0.0}`
- orthographic / in_2d_mode / camera_position / camera_forward / scene_view_title 全部返回

**观察**: 用户当前 SceneView 视角在场景里比较偏（pivot 距离原点 ~63 米），说明是真实读取的当前状态。

### set_scene_view 侧 ❌ 完全失败

**错误链**:
1. `[Error] Parameter 'pivot' expected object but got string.`（首次）
2. `[Error] Parameter 'pivot' expected object but got string.`（LLM 自愈重试）
3. `[Error] Parameter 'size' expected number but got string.`（换写法后）
4. LLM 3 次失败后主动切 `execute_code` 兜底成功（SOUL §2.10 REPL 回退生效）

### 🔴 Bug E — 复合类型参数（object / number）到达工具层时被降为 string（**P0 阻断**）

**根因假设**（未定案，需 3 层排查）:

- **A**: LLM 客户端序列化层 —— object 字段被 stringify 成 `"{x:0,y:10,z:0}"` 而非 `{"x":0,"y":10,"z":0}`
- **B**: AgentCore 请求解析层 —— 从 API 收到 arguments 后未做 nested object 二次解析
- **C**: Schema 校验层过严 —— `ToolParameterValidator.cs:158-163` 严格要求 `JTokenType.Object`

**排查方向**:
1. 加临时 DIAG log 打印 `parameters` JObject 完整结构（`parameters.ToString()`）
2. 对比 W1 的 `query={"scope":"scene","component_type":"..."}` 结构 —— 那是 object 参数**成功**的例子
3. 用 execute_code 直接构造 payload 走 tool executor 看是否 OK

**W3 反证**: 同一工具 W3 里 `manage_editor.set_selection_by_query` 的 `query` 参数是 object 且成功了。**说明 object 参数在其他工具里能通过** → 问题可能只在 `manage_camera` 的 schema 定义或参数抽取逻辑上。

**优先级**: **P0** —— v1.9.6 G09 SceneView 相机能力**当前完全不可用**（LLM 只能靠 execute_code 兜底，违反工具化承诺）。**v1.10.2 必须修**。

### 🔴 Bug F — 会话导出机制吞掉 tool_call arguments（可观测性）

**现象**: 导出 JSON 里 `tool_calls[].function.name = "?"` + `arguments = ""` — 具体 tool 名和参数**都被抹掉**。

**后果**:
- 用户/开发者复盘 bug 时看不到 LLM 传参
- 类似 W4 这种参数序列化 bug 无法从导出定位
- 违反可观测性原则

**优先级**: **P1** —— 影响 bug 定位效率，不阻断功能。

### 🟡 附带发现

- **manage_camera 需手动激活 Specialized 类别** —— LLM 每次都要先跑 `activate_categories(["specialized"])` 才能用 G09。若 SOUL/工具描述里 tool discovery hints 没标注这点，会重复出现"激活工具类别→再调用"的两步流程，UX 磨损。
- LLM 用 execute_code 时**3 次 CS0201/CS0246** 才写对代码（第一次 if-else 分支内字符串字面量当语句 / 第二次 SerializationWrapper 未定义 / 第三次同 CS0201）。SOUL §2.10 REPL 语法引导可加"if-else 分支不能只有字符串字面量"这类具体反例。**优先级 P2**（引导层增强）。

### ✅ 副产物验证

- SOUL §2.10 的 execute_code REPL 回退规则**生效了** —— LLM 主动切换到 execute_code 兜底，虽然多次编译错但最终完成用户目标
- LLM 主动承认"改用 execute_code 完成等价操作"—— 诚实性原则起作用

**用户请目视验证**: SceneView 视角**现在是否真的变了**？（pivot 应该在 (0, 10, 0)，size=20，视角对准原点上方 10 米处）


---

## 📋 追加：v1.10.2 之后 W6-W10 实测发现（2026-07-27）

### 🔴 Bug P — High-risk 确认面板在参数校验之前触发（UX + 安全流程颠倒）

**W9 复现**：`manage_prefs set` 第 1/2 次调用因参数不合法（少 `value_type` / `value`）返回 error，但**用户依然被弹了 2 次确认面板**——每次都要手动点"确认"放行，之后才走到 Validator 报错。

**问题**：不合法的 tool call **根本不应该走到"要不要 undoable"层面**。用户被无意义地骚扰，且形成"确认面板疲劳" → 未来真危险操作用户可能不看直接点确认。

**根因猜测**：`ToolCallDispatcher` 的执行顺序是 `Confirm UI → Validator → Handler`，应该改为 `Validator → Confirm UI → Handler`。

**优先级**：**P1** — v1.11 修。

---

### 🔴 Bug Q — `manage_prefs` 只读 action 也弹确认面板（缺 per-action RiskLevel）

**W9 复现**：`get` / `has` 是纯读，checklist 明确要求走 ReadOnlyActions 快速路径**不弹面板**。实际两个都弹了。

**根因猜测**：`ManagePrefsTool` 的 RiskLevel 声明是**工具级**（整个工具 High），不是 **action 级**。set/delete 需要 High，get/has 应识别为 ReadOnly 但被工具级 override。

**修法方向**：查工具是否声明 per-action ReadOnly 列表；没有就补。这个问题**可能不止 `manage_prefs` 一家有**——所有多 action 工具都要审查。

**优先级**：**P1** — v1.11 修。

---

### 🟡 Bug R — Tool schema 里必填参数不明确导致 LLM 猜错

**W9 现象**：LLM（GLM）连续 2 次猜错 `manage_prefs.set` 参数结构：
- 第 1 次传 `value_string="hello_world"`（猜错，schema 是 `value` + `value_type`）
- 第 2 次补了 `value_type` 但依然叫 `value_string`

**归属**：这**部分是 checklist prompt 写错**（我给用户的 W9 prompt 用了 `value_string`），但也暴露 tool schema description 层面**没有明确列出必填参数结构**——即便 human agent 都会猜错。

**修法方向**：schema description 里对 `set` action 明确写 "requires: `value_type` (string|int|float|bool) + `value` (typed value matching value_type)"。

**优先级**：**P2** — v1.11 顺路。

---

### 🚨 GLM tool routing 结构性问题（Bug G' 家族证据加强）

**W6/W7/W8 三连踩**：GLM 每次都在 `manage_p*` 工具族误调另一个（`manage_physics` ↔ `manage_profiler`），需要 GLM 自我纠错第 2 次才对上。W8 一次误调 `manage_profiler.list_stats` 无 filter → **返回 822088 chars / ~205540 tokens**（Bug C 严重实证）。

**W10 反例**：GLM **拒绝执行 `manage_prefs.delete_all`** 调用（LLM 层安全对齐）—— 这次是"太保守"而非"名字混淆"，但同源：**GLM tool routing 不稳定**。

**归属**：**不是 AgentCore bug**——是 GLM 自身问题。AgentCore 侧 mitigation：
- 高频误击工具（如 `manage_profiler.list_stats`）加**硬 cap**（默认 limit=100 + filter required）—— 防 Bug C 复合放大
- Tool description 加 disambiguation hint（"NOT to be confused with manage_profiler"）
- SOUL 加规则"调 physics/profiler 前先 `request_tools.list` 确认"

**优先级**：**P1**（AgentCore 侧 mitigation），非 P0（GLM 自身修不了）。

---

### 🔴 Bug C 升级 — Response compression 策略太宽松（实证）

**W8 实证**：GLM 误调 `manage_profiler.list_stats` 无 filter，返回带 `[compressed]` prefix 但**依然 822088 chars / 20 万 tokens**——**一次误调用几乎吞掉整个 context window**。

**这是根源 3**（AgentCore 侧设计问题，本轮全局审视识别）。修法方向：
1. `manage_profiler.list_stats` 无 filter 时**强制** limit=100 或强制 filter required（工具级修复）
2. `[compressed]` 后应该 truncate 到 hard cap（≤5000 chars） + hint 建议 filter（框架级修复）

**优先级**：**P1** — v1.11 专项修（用户已决策"v1.11 专项修根源 3"）。

---

## 🎯 v1.10.x 之后战略动作：Checklist Prompt 层系统治理

**用户明示（2026-07-27）**：所有代码 bug（v1.11）修完之后，要针对**修过后的工具真实行为**，把 W1-W11/R1/R2 的 checklist prompt **重写一遍**。

**当前 prompt 层暴露的系统性问题**：
1. **参数名不对**：Bug L (`mode=first` 应 `single`) / N (`distance` 应 `max_distance`) / O (raycast expected 字段过时) / R (`value_string` 应 `value`+`value_type`)
2. **触发方式不够强**：Bug G' 家族需要"必须使用 XXX 工具"的强 prompt 才不被 GLM 幻觉/混淆
3. **意图澄清不足**：W10 GLM 直接拒绝 tool_call，需要"这是安全 gate 测试语境"前置声明
4. **默认 prompt 没排除 execute_code fallback**：LLM 遇到工具报错倾向走 execute_code 绕过被测组件

**动作时机**：v1.11 代码修完 → 独立一次治理（可以是 v1.12 或独立文档更新，不与代码修复混合）。

**目标**：让下一轮 smoke test 起点是干净的 prompt，不再让 checklist prompt 本身诱导测试失败。

---

## 📊 v1.10.x 测试进度快照（截至 W10 前）

| Test | 状态 | 备注 |
|---|---|---|
| W1-W5 | ✅ PASS | Bug E fix 实战验证通过 |
| W6-W7 | ✅ FUNCTIONAL PASS | Bug G' 家族每次踩到但 GLM 自我纠错 |
| W8 | ✅ PASS | Bug G' + Bug C 复合放大证据 |
| W9 | ✅ PASS | Bug P + Q + R 新发现 |
| W10 | ✅ PASS | 强化 prompt 后 gate 正常工作，缺 confirm_delete_all 时 tool 层直接 error 拒绝，EditorPrefs 未受影响 |
| W11 | ⏳ 未测 | |
| R1 | ⚠️ INCONCLUSIVE | Bug H + I 阻断 |
| R2 | ⚠️ PARTIAL | Bug K fix 已修，未回归验证 |

**已修 (v1.10.2)**：Bug E（Validator 层 coercion）+ Bug K（Handler 层 coercion）

**v1.11 backlog（按用户决策优先级）**：
- 根源 3：Response 压缩（Bug C）
- 根源 4 高频：Bug F（导出吞 arguments）/ Bug D（sub-asset 命中）/ Bug B（无分页）/ Bug H（Play Mode 写拦截）
- 新增 W9：Bug P（确认面板 vs 校验顺序）/ Bug Q（per-action RiskLevel）
