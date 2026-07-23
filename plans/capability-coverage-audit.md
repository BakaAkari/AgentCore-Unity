# AgentCore 能力覆盖面审计方法（v1.8.0 立项经验）

**用途**：判断 AgentCore 47 工具是否覆盖 Unity 主要子系统，找真实缺口。**不是** Undo 审计（那是 mutating-side 的对抗式复查，见 `adversarial-coverage-audit.md`）。

---

## 触发场景

用户提出的问题揭露某个 Unity 官方模块可能没工具覆盖（如"能抓帧看阻点吗"→ Profiler；"能改后处理吗"→ VolumeProfile），或用户直接要求"排查覆盖面缺陷"。

---

## 前置铁律：先枚举工具清单，再回答"能不能做"

**本会话的重大失误**：用户问"能不能让 agent 抓帧"，我直接回答"不能"并给了三条 execute_code workaround，**然后才发现 `manage_profiler` 工具早就存在**。

**根因**：AGENT 对自己的工具集合没有完整感知，尤其 `Visibility = OnDemand` 的工具（如 manage_profiler）平时不出现在常规工具列表里。

**修正规则**：任何"AgentCore 能不能做 X"的判断之前，**必须**先枚举工具：

```python
import os, re
pkg = "D:/Unity Project/unity-agent/Packages/com.agentcore"
tools = []
for root, _, files in os.walk(os.path.join(pkg, "Editor/Tools/Native")):
    for f in files:
        if not f.endswith(".cs"): continue
        with open(os.path.join(root, f), encoding="utf-8", errors="replace") as fh:
            src = fh.read()
        m = re.search(r'\[AgentTool\("([^"]+)"', src)
        if m: tools.append(m.group(1))
```

47 个 name 里 grep 关键词（`profil`/`memory`/`frame`/`input`…），有命中就读源码判定深度，不能直接说"没有"。

---

## 审计基准双轴（用户拍板：A+B）

### A 轴：Unity 顶层菜单

`File / Edit / Assets / GameObject / Component / Window / Help` 每个子菜单展开一遍。用户视角，能一比一映射日常操作。

**重点二级菜单**：
- `Window/Analysis`（Profiler / Frame Debugger / Physics Debugger / Memory Profiler / Profile Analyzer / IMGUI Debugger / Input Debugger）— 只读诊断类，最容易被漏
- `Window/Rendering`（Lighting / Occlusion / Frame Debugger / RP Converter / Shader Graph）
- `Window/2D`（Sprite Editor / Sprite Atlas / Tile Palette）
- `Edit/Selection` / `Edit/Preferences` / `Edit/Project Settings`

### B 轴：UnityEditor / UnityEngine 命名空间

按 API 视角扫，能发现只读/反射类漏网：
- `UnityEditor.Selection` / `EditorApplication` / `EditorPrefs` / `PlayerSettings`
- `UnityEditor.Compilation.CompilationPipeline`
- `UnityEditor.SceneView` / `UnityEditor.SearchService`
- `UnityEditor.Presets` / `UnityEditor.VersionControl`
- `Unity.Profiling.ProfilerRecorder` / `UnityEditorInternal.ProfilerDriver` / `UnityEditor.FrameDebuggerUtility`（internal，可能需反射）
- `UnityEngine.Rendering.VolumeProfile` / `RenderPipelineAsset`
- `UnityEngine.InputSystem`（新 Input 包 vs legacy InputManager）
- `UnityEngine.Audio.AudioMixer`

**A 独有陷阱**：Unity Analysis 菜单全是只读工具，Undo audit 的思路完全扫不到——这是本次 v1.8.0 立项的直接触发点。

---

## 审计四步实证（避免"grep 关键词看有无"的浅审计）

grep 只能证在场，**不能证覆盖深度**。例：`manage_workspace_config` grep `ProjectSettings/PlayerSettings/QualitySettings` **全 0 命中**，但工具名字暗示它就管这些——命名与实现可能脱节，必须读源码 SwitchCase。

四步：

1. **读源码** — 每个候选工具读 `switch (action)` 全部 case + 关键 API 调用（`Undo.*` / `AssetDatabase.*` / `ProfilerRecorder.*` 等）
2. **对照 Unity 官方 API 文档** — 把工具做的事和 Unity 该模块的完整 API 表对齐
3. **场景推演** — 写 3-5 个真实使用场景（"抓 300 帧看 CPU 阻点""改 URP 后处理曝光""烘焙光照并轮询进度"…），逐个走一遍现有工具能不能干、到什么粒度
4. **产出根因标注** — 每个缺口标注根因类别，不要笼统说"缺"：
   - `NO_TOOL` — 完全没工具
   - `SHALLOW_TOOL` — 有工具但只覆盖一小部分 API（例：manage_profiler 只有单帧快照，无 ProfilerRecorder 时序采样）
   - `DISCOVERABILITY` — 工具存在但 agent 找不到（Visibility=OnDemand、描述关键词不匹配用户措辞）
   - `COMPOSITION` — 单个工具都够，但组合起来的 workflow 不流畅
   - `NOT_A_GAP` — execute_code 已能通用干，不需要专用工具

---

## 分级模板（v1.8.0 实际使用）

- **P0_CRITICAL_ANALYSIS** — 性能诊断类（ProfilerRecorder / ProfilerDriver / FrameDebugger / MemoryProfiler / PhysicsDebugger）
- **P0_CRITICAL_WORKFLOW** — 日常必用（Selection / CompilationPipeline / EditorPrefs / SceneView / 新 Input System）
- **P1_MEDIUM_RENDERING** — 渲染深化（VolumeProfile / RP 深度 / Occlusion / Lightmapping GI / Sprite Editor / AudioMixer 深度）
- **P1_MEDIUM_ASSET** — 资源深化（Presets / .unitypackage export / Multi-scene / Sibling order / BuildProfile）
- **P1_MEDIUM_META** — 元操作（VCS / Search / RevealInFinder / Find References / Shader variant）
- **P2_EVALUATE** — 可选包依赖（Addressables / Localization / Netcode / XR / Video / TMP / IMGUI Debugger）
- **NOT_A_GAP_OR_LOW** — Undo/Redo（execute_code 完备）/ Preferences GUI / Layouts

---

## 审计输出交付

**先给用户看清单，再决定路径**。用户 memory 明确要求"完整分析后再改，不要只改一部分"——不要边扫边动手改工具，扫完给出四类候选，clarify 让用户选：

- A：把 P0 十项写实（每项定 action 接口 + 优先级）
- B：先验证 N 个深化候选（可能改变 P0/P1 清单）
- C：直接开打某个最紧迫工具（回答用户当下需求）
- D：先收尾 in-flight 版本再开新版

**本会话用户选 D → 后续深度分析**：先发前一版收尾包，再开新版本深度分析。这个顺序值得保留。

---

## 关键坑

### 坑 1：Visibility=OnDemand 的工具会隐身

`manage_profiler` 就是 OnDemand。日常工具枚举可能不列出。**在审计前必须显式扫全 47 个 .cs 文件**，不要只看当前会话工具面板。

### 坑 2：Category 大小写不一致会导致误判"重复"

现状里同时存在 `Extended` 和 `extended`、`Specialized` 和 `specialized`、`Meta` 和 `meta`——这是 v1.8.0 顺手要清理的一致性问题，但不能因此以为工具重复。

### 坑 3：grep 关键词浅审计的假阴性

有的工具用 `ToolHelpers.RecordUndo` 包装 `Undo.RecordObject`——grep 原始 API 会漏。审计任何"是否覆盖 X API"时，**必须**同时 grep 包装函数名和原始 API 名。

### 坑 4：Python os.walk 的双层 for break 覆写陷阱

在 execute_code / hermes_tools 里遍历工具文件时，经典错误：

```python
# ❌ 错：内层 break 只跳出 inner，外层继续覆写 found_file
for root, dirs, files in os.walk(pkg):
    for f in files:
        if match: found_file = fp
        if found_file: break   # 只跳内层
    # 外层继续走下一个 root，found_file 被后续迭代覆写为 None
```

修法：先建映射字典（一次全扫，name→file），后续查表。不要用嵌套 break：

```python
tool_files = {}
for root, _, files in os.walk(pkg):
    for f in files:
        if not f.endswith(".cs"): continue
        with open(os.path.join(root, f), encoding="utf-8", errors="replace") as fh:
            src = fh.read()
        m = re.search(r'\[AgentTool\("([^"]+)"', src)
        if m: tool_files[m.group(1)] = (fp, src)
```

---

## 交叉引用

- Mutating 工具 Undo 覆盖率审计 → `references/adversarial-coverage-audit.md`
- 用户报告 vs 实际状态交叉验证 → `references/cross-verify-user-reports.md`
- 本文件覆盖的是 **"AgentCore 工具覆盖 Unity 模块的完整性"** 审计（read-side），adversarial-coverage-audit 覆盖的是 **"mutating 工具的 Undo/可逆性"** 审计（write-side）。两者互补，都要跑。

---

## v1.8.0 实测数据 (2026-07-22)

### 首轮 grep 表面扫描 vs A+B 双轴源码验证的差异

**首轮判断**：44 项候选缺口（P0=10 / P1=16 / P2=7 / 深化=11）
**A+B 重扫（源码 SwitchCase 交叉）**：28 项真实缺口（P0=7 / P1=10 / P2=8 / NOT_A_GAP=3）

**7 项从"缺"反转为"不缺"** —— grep 关键词假阴性的典型：

| 首轮判断 | 反转后事实 |
|---|---|
| 缺 Selection 读写 | `manage_editor:get/set_selection` 已完整 |
| 缺 Play/Pause/Step | `manage_editor:play_mode` 已覆盖 |
| 缺 Align With View | `manage_camera:align_to_view` 已覆盖 |
| 缺 Lightmapping GI 深度 | `manage_lighting:bake` + Clear/bakedGI/realtimeGI 已完整 |
| 缺 multi-scene additive | `manage_scene:list_open_scenes/merge_scenes` 已在 |
| 缺 render_texture screenshot | `manage_camera:render_to_texture` 已在（agent 都没发现） |
| `manage_workspace_config` 缺 Unity 覆盖 | 命名撞车：这是 AgentCore 自己的 workspace config 工具，不属 Unity 覆盖率 |

### 关键教训

**教训 1：Description 关键词 grep 会撒谎**。工具描述文案有 `PlayerSettings/QualitySettings` 词，但 handler 里可能只调用 `GraphicsSettings.currentRenderPipeline`。反过来也一样：`manage_camera` 描述里含 `align_to_view` 但用户/agent 都没意识到还有 `render_to_texture`。**必须读 SwitchCase 全部 handlers**。

**教训 2：命名撞车会引起假缺口**。`manage_workspace_config` 一听像"Unity Project Settings 工具"，实际是 AgentCore 自己的 workspace.md 配置读写。审计前先看 action enum 判断工具**用途归属**，再谈"覆盖 Unity 什么"。

**教训 3：Visibility=OnDemand + 好描述 = 隐身工具**。`manage_profiler` 的 Description 关键词写得极准（`performance/frame rate/FPS/memory usage`），但因为 OnDemand，agent 平时看不到它，撞到"用户想抓帧"时会当场编 execute_code 而非唤醒工具。**修法**：高频需求的工具改 AlwaysVisible；剩余 OnDemand 的高价值能力经 SOUL tool discovery hints 显式映射，让 agent 主动 `request_tools`。

### v1.8.0 P0 清单最终版（7 项，~10 人日）

1. **G27** DISCOVERABILITY — `manage_profiler` Visibility→AlwaysVisible + Description 修正 action 名 + SOUL §2.13 tool discovery hints（8 项中英词映射） **[✅ 已实施]**
2. **G01** SHALLOW → `manage_profiler` 新增 `sample_recorder` / `list_available_stats`（ProfilerRecorder 时序采样 N 帧）
3. **G02** NO_TOOL → `manage_profiler` 新增 `read_frame` / `get_frame_range`（ProfilerDriver internal API 反射）
4. **G10** NO_TOOL → `manage_graphics` 新增 `volume_get` / `volume_set`（VolumeProfile URP/HDRP 后处理）
5. **G17** NO_TOOL → `manage_asset` 新增 `find_references` / `find_dependencies`（AssetDatabase.GetDependencies 正反向）
6. **G18** NO_TOOL → `scene_analysis` 新增 `find_references_in_scene`
7. **G03** NO_TOOL → `manage_profiler` 新增 `list_draw_events` / `get_draw_event`（FrameDebuggerUtility internal 反射，**高风险 API 不稳定**）

### 分档规则修订

- **NOT_A_GAP**：`execute_code` 一两行搞定 + 无独特工程价值（如 SetSiblingIndex、Copy/Paste GameObject、Select All）→ 不加特化工具，一致性优先于便利性
- **DISCOVERABILITY**：工具存在但 agent 找不到 → 修 Visibility + SOUL hint，比新增工具优先级更高
- **SHALLOW**：工具覆盖 API 一小部分，需扩 action → 深化现有工具优先于新建
- **NO_TOOL**：真空白 → 只有这个情况才新建工具

### 未来审计的固化 SOP

1. **枚举工具 name 表**：`grep -r '\[AgentTool("` Editor/Tools/Native/*.cs
2. **提取每工具的 action enum**：从 `_parametersSchema` 的 `"action":{"enum":[...]}` 数组读，**不数 handler**（handler 参数值 case 会虚高 5-10 倍）
3. **提取每工具的 Handle* 方法名 + 关键 API 调用**（AssetDatabase.* / Undo.* / ProfilerRecorder 等等）
4. **A+B 双轴矩阵对齐**：Unity 顶层菜单 × 命名空间双轴各扫一遍，把每个单元格标 COVERED/SHALLOW/PARTIAL/NO_TOOL/NOT_A_GAP/N_A
5. **合并去重 + 根因分类**：菜单/命名空间可能反射同一个缺口，去重；每项标根因（SHALLOW/NO_TOOL/DISCOVERABILITY/COMPOSITION/NOT_A_GAP）
6. **优先级排序**：P0=用户直接触发的原始动因 + 高频高价值，P1=中频有替代，P2=低频/包依赖/延后

用户偏好：**完整分析后再改，不要只改一部分**。先出 28 项去重清单让用户选，再开工，不边扫边改。
