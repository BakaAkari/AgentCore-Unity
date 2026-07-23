# AgentCore Unity — 开发迁移接手文档 (v1.8.0 → v1.9.0)

> **交付日期**: 2026-07-23
> **当前版本**: v1.8.0 (commit `eed0c3d`, tag `v1.8.0` 已推 GitHub)
> **接手人**: 另一台开发设备上的专业 coding agent
> **上下文**: v1.8.0 能力覆盖 P0 7 项已一次性收官, v1.9.0 转入 P1/P2 深化 + MCP-over-tools

---

## 0. 快速上手（新 agent 请先读这里）

**第一件事**: 读完本文档。**第二件事**: 按下面顺序读文档:

| 顺序 | 文档 | 用途 |
|---|---|---|
| 1 | `AGENTS.md` | LLM 开发规范, 目录结构, 工具开发模板, 命名规范, ToolResponse/JsonHelper 使用规范 |
| 2 | `plans/ROADMAP.md` §3.w.1 v1.8.0 收尾 + §3.w Phase 10 | 能力覆盖 milestone 的完整上下文 |
| 3 | `plans/capability-coverage-audit.md` | v1.8.0 立项时用的审计方法论 (A×B 双轴, 缺口分类, agent 可发现性) — 做 v1.9.0 P1/P2 时必读 |
| 4 | `plans/adversarial-coverage-audit.md` | Undo/mutating-side 对抗式审计方法论 (v1.7.29 沉淀) |
| 5 | `plans/agentcore-execute-code-constraints.md` | AgentCore execute_code 工具的 Mono.CSharp.Evaluator 硬约束 — 反射探测 internal API 时必读 |
| 6 | `CHANGELOG.md` v1.8.0 章节 | 本版实际交付的每一条改动 |
| 7 | `Editor/Bootstrap/Resources/SOUL.md` | Agent 行为准则, 尤其 §2.10 execute_code 使用契约 + §2.11 Profiler 引导 |
| 8 | `plans/perf-issue-agent-streaming-blocks-editor.md` | v1.8.0 期间发现的 agent 流式输出阻塞 Editor 主线程 issue (228ms/帧, 4.4FPS), v1.9.0 优化候选 |

**第三件事**: 装环境 (§4)。**第四件事**: 从 v1.9.0 待办清单 (§2) 挑一项开工。

---

## 1. 已解决 (Resolved / Delivered in v1.8.0)

### 1.1 能力覆盖 P0 7 项 (全部编译 + 运行时验证)

| 项 | 缺口类型 | 落地 | 验证方式 |
|---|---|---|---|
| **G27** | DISCOVERABILITY | `manage_profiler` Visibility=AlwaysVisible + SOUL §2.11 Profiler use hints 引导 | Agent 视野内可直接调用, 无需 activate |
| **G01** | SHALLOW | `manage_profiler` 新增 `list_available_stats` / `sample_recorder` | 真实数据: 187 stats 枚举成功, Render 分类过滤正确; sample_recorder Draw Calls 采样 60 帧 min/max/mean 全部有数 |
| **G02** | NO_TOOL | `manage_profiler` 新增 `get_frame_range` / `read_frame` (HierarchyFrameDataView) | 219 帧 buffer 命中, Main/Render 双线程切换成功, EditorLoop 228ms 定位到 agent 流式阻塞问题 |
| **G03** | NO_TOOL | `manage_profiler` 新增 `list_draw_events` / `get_draw_event` / `disable_frame_debugger` (反射 `UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility`) | 编译 + 反射链路验证: SetEnabled(true, 0) 成功, count 返回, structured hint 完整; 真实 event 采集受环境 blocker (§3.4) 未回头验证 |
| **G10** | NO_TOOL | `manage_graphics` 新增 `volume_list` / `volume_get` / `volume_set` (反射 `Volume`/`VolumeProfile`/`VolumeComponent`/`VolumeParameter<T>`) | Version Defines `AGENTCORE_HAS_SRP_CORE` 隔离 SRP 依赖; built-in fallback stub 通过; **SRP 环境真实链路未验证** (§3.2) |
| **G17** | NO_TOOL | `manage_asset` 新增 `find_references` (AssetDatabase.GetDependencies 反向扫描 + filter + recursive) | 89 asset 全量 301ms; filter="t:Prefab t:Scene" 5 asset 1ms (300× 提速); recursive 243ms |
| **G18** | NO_TOOL | `scene_analysis` 新增 `find_references_in_scene` (SerializedObject walker + sub-asset) | 精准命中: `Assets/RoadAssets/Generated/mat_curb.mat` → `Road / RoadBuilder.curbMaterial` (自定义脚本序列化字段, 非标准 MeshRenderer.m_Materials) |

### 1.2 副产物 Bug hotfix

| Bug | 描述 | 修复 |
|---|---|---|
| **#1** | `manage_camera action=render_to_texture` ImageConversion 引用错误 | 补 Editor 引用, 可产出 PNG |
| **#2** | `execute_code` 编辑器脚本环境 `Texture2D.EncodeToPNG()` 引用失败 | ExecuteCodeTool ReferenceAssembly 补齐 |

### 1.3 反射盲写 pitfall 修复 (G02 隐藏依赖)

`ProfilerDriver.enabled` (Editor 帧缓冲开关) 与 `Profiler.enabled` (运行时采样开关) 是**两个独立开关**。此前 `start_recording` 只开后者导致 `read_frame` 一直返 0 帧。修复: 同步开两个 + `ProfilerDriver.profileEditor=true`; `get_frame_range` 加分层 hint (driver / profiler / 等帧) 定位到底哪个没开。

### 1.4 已固化的方法论

| 文档 | 位置 | 说明 |
|---|---|---|
| Capability coverage audit | `plans/capability-coverage-audit.md` | A×B 双轴 (菜单 × API 命名空间), 5 类根因分类 (NO_TOOL/SHALLOW/DISCOVERABILITY/COMPOSITION/NOT_A_GAP), 首扫必须 grep 已存工具避免 v1.7.29 那样的"回答不能→事后发现存在"翻车 |
| Adversarial coverage audit | `plans/adversarial-coverage-audit.md` | Mutating-side (Undo) 对抗式复查, 正则必须扫 `ToolHelpers.RecordUndo` 包装 API 而不只是 `Undo.RecordObject` 裸调用 |
| Execute code constraints | `plans/agentcore-execute-code-constraints.md` | Mono.CSharp.Evaluator 硬约束: 无 using / 无 top-level return / 无 C# 8+ 语法 / `Object` 歧义 / Scripting 类别 activation gate / 反射探测两阶段模式 (dnfile 找 assembly + Editor 运行时 dump 成员) |

---

## 2. 待解决 (v1.9.0 Backlog)

### 2.1 P1 能力覆盖缺口 11 项

从 `capability-coverage-audit.md` 首扫的 44 项候选里筛出:

| 缺口 ID | 领域 | 描述 | 建议落地 |
|---|---|---|---|
| G04 | Profiler | MemoryProfiler snapshot/diff | 反射 `Unity.MemoryProfiler` (需检测包是否安装, Version Defines 隔离) |
| G05 | Profiler | PhysicsDebugger 数据 | `Physics2D.OverlapAreaAll` / `Physics.OverlapSphereNonAlloc` 反射诊断 |
| G06 | Workflow | Selection API 深化 (`Selection.instanceIDs` 读写, activeContext) | 现有 `manage_editor.set_selection` 扩展 |
| G07 | Workflow | CompilationPipeline (LogAssemblyCompilations, RequestScriptCompilation) | 新 action 或独立工具 |
| G08 | Workflow | EditorPrefs / PlayerPrefs 读写 | 独立工具 `manage_prefs` (敏感, 需 Undo 白名单) |
| G09 | Workflow | SceneView 相机 pivot/size 读写 | 现有 `manage_camera` 加 scene-view action |
| G11 | Rendering | Occlusion Culling bake | 反射 `StaticOcclusionCulling` |
| G12 | Rendering | Lightmapping GI 深度 (bake / clear / progress) | `Lightmapping.BakeAsync` 反射 |
| G13 | Rendering | Sprite Editor slice / meta 编辑 | `TextureImporter.spritesheet` + SpriteMetaData |
| G14 | Asset | Presets 读写 / 应用 | `UnityEditor.Presets.Preset.Apply` |
| G15 | Meta | Unity Search API (`SearchService.Request`) | 现有 asset search 深化 |

**排序建议**: G04 (MemoryProfiler) 用户最可能先要; G06/G07 是工作流通用能力提升覆盖面最大; G13 (Sprite Editor) 若有 2D 场景需求优先。

### 2.2 P2 能力覆盖缺口 10 项

详见 `plans/capability-coverage-audit.md` 完整清单。Addressables / Localization / Netcode / XR / Video / TMP / IMGUI Debugger 等, 大部分是可选 package 反射, 需 Version Defines 隔离。

### 2.3 Phase 8 — MCP-over-tools (v1.9.0 或 v1.10.0)

**目标**: AgentCore 47 工具通过 MCP (Model Context Protocol) 对外暴露, 让 Claude Desktop / OpenAI Assistants / 其他 MCP client 能调用 Unity Editor 操作。

**关键设计点** (详见 `plans/ROADMAP.md` §3.x):
- Transport: stdio 或 http+SSE
- Auth: 本机 token, 不对外
- Schema: 从 `AgentToolAttribute` + JSON Schema 自动生成 MCP tool manifest
- Editor lifecycle: MCP server 挂在 Editor 主线程, domain reload 时优雅重连

**未决问题**:
- MCP client 处于 Play Mode 阻塞状态时如何反馈 (现有 ToolResponse "Play Mode 中禁止 write" 逻辑复用)
- Windows / macOS / Linux 三平台 stdio 兼容
- MCP server 是常驻还是 lazy start

### 2.4 v1.8.0 期间发现的其他 issue

| Issue | 位置 | 描述 | 建议 |
|---|---|---|---|
| Agent 流式输出阻塞 Editor 主线程 | `plans/perf-issue-agent-streaming-blocks-editor.md` | EditorLoop 228ms/帧 = 4.4 FPS, 主线程被 Chat UI Repaint + LLM 流式 token 拆包吃光 | 三条候选路径 (异步 flush / 累积 flush + timer / 静默 + 状态栏), 详见文档 |
| Chat 面板占主线程导致 FrameDebugger 采不到 event | (同上) | G03 测试时 GameView 根本没渲染场景, Render Thread 全是 UIToolkit UI | 上面性能优化解决后 G03 才能真回头 |

---

## 3. 遗留 (Not Verified / Environmental Constraints)

### 3.1 G03 FrameDebugger 真实 event 采集未回头验证

**状态**: 反射链路完全打通 (SetEnabled 成功, count 属性正确返回, 全 hint 结构化), 但**没有拿到真实 draw event 数据**, 因为测试时 GameView 没在渲染场景 (§2.4 主线程被 Chat 阻塞)。

**风险等级**: 中。SetEnabled 成功 + count 属性响应说明反射签名 100% 对; 但 `GetFrameEventData(int)` / `GetFrameEvents()` 具体返回结构在真实场景下是否需要额外反射步骤 (例如 `FrameDebuggerEventData` 是 struct 需 boxing) 未验证。

**验证路径**: 关掉 Chat 面板 (或异步化流式) 后进 Play Mode, GameView 有一个 3D 相机在渲染, 调 `list_draw_events` 期望 count > 0, 再 `get_draw_event event_index=0` 看返回结构是否完整。

### 3.2 G10 SRP 真实环境未验证

**状态**: built-in 项目 fallback stub 已验证 (返回结构化错误 "SRP not detected — install URP or HDRP..."), Version Defines `AGENTCORE_HAS_SRP_CORE` 正确切换编译分支。**但 URP/HDRP 实际项目没跑过**。

**风险等级**: 中。反射目标类型 (`Volume`/`VolumeProfile`/`VolumeComponent`/`VolumeParameter<T>`) 在 SRP core 里是公开 API 版本稳定, 但 `SerializeVolumeComponent` 里 `ValueToJToken` / `CoerceJTokenToType` 对复杂类型 (ColorParameter, ClampedFloatParameter, MinFloatParameter etc) 的转换是否 100% 覆盖所有 URP/HDRP built-in VolumeComponent 未穷举。

**验证路径**: 在有 URP 的项目里跑 `volume_list` (期望列出 Bloom/Tonemapping/Vignette 等), `volume_get name=<Volume 名>` (期望完整 JSON), `volume_set` 改一个 float 参数 (期望反射写入 + Undo 记录)。

### 3.3 v1.9.0 前置技术债

| 债 | 位置 | 说明 |
|---|---|---|
| Agent 流式阻塞 | 见 §2.4 | 影响所有 Play Mode + 长响应场景, 优先级高 |
| `patch` 工具对 `\r\n` 行结尾锚点不可靠 | 开发工具 issue | 用 execute_code 字节级 replace 绕过, 未来考虑升级 patch |
| Windows path 带空格的 `search_files` / `read_file` 有偶发 KeyError | 开发工具 issue | 用 terminal grep / cat 绕过 |

### 3.4 v1.8.0 未做的事 (明确 defer)

- 未修改任何 Editor UI 布局
- 未动 Optional Components 系统
- 未动 Chat / LLM 管道 (v1.6.x 已稳)
- 未做 MCP (延后到 Phase 8)
- 未做 Test Framework 集成 (P2)

---

## 4. 环境依赖

### 4.1 Unity Editor 版本

> **两个不同语义, 不要混淆**

| 维度 | 值 | 来源 | 含义 |
|---|---|---|---|
| **项目实际锁定的 Editor 版本** | `2022.3.50f1` | [`ProjectSettings/ProjectVersion.txt`](../../../ProjectSettings/ProjectVersion.txt) | 打开本仓库工程的 Unity Editor 版本, 也是 v1.8.0 反射/内部 API 唯一验证过的版本 |
| **UPM 包声明的最低兼容 Editor** | `2021.3.0f1` | [`package.json`](../package.json) `"unity": "2021.3"` + `"unityRelease": "0f1"` | 作为 UPM tarball 被安装到外部项目时, Unity Package Manager 拒绝低于此版本 |

- **新 agent 装环境请用 `2022.3.50f1`**, 不要用 2021.3。UPM 的最低声明只是分发面, 不是开发面。
- **理由**: G03 反射 `UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility` 是 internal API, 签名在不同 Unity 版本会漂移 (2019/2020/2021/2022/2023 之间已改过多次)。v1.8.0 只在 2022.3.50f1 验证过。
- **升级本工程到 6000.x 时**: 需重跑 §4.4 反射探测脚本, 检查所有 `Type.GetType("UnityEditorInternal.FrameDebuggerInternal.*")` 是否仍解析成功; `AGENTCORE_HAS_SRP_CORE` version defines 表达式也可能需要更新。
- **若要放宽 UPM 最低支持面**: 修 `package.json` 的 `unity` / `unityRelease` 前必须确认最低版本上所有反射目标 (FrameDebuggerInternal / Volume / ProfilerDriver 等) 仍可用, 否则会给下游用户埋雷。

### 4.2 项目依赖

- Newtonsoft.Json (via Unity Package Manager `com.unity.nuget.newtonsoft-json`)
- 无 URP/HDRP 硬依赖 (Version Defines 隔离)
- Mono.CSharp (ExecuteCodeTool 用, 由 Unity 内置)

### 4.3 测试项目

- 路径: `D:/Unity Project/unity-agent/` (本机绝对路径, 换设备后可能不同)
- Render pipeline: **built-in** (无 URP/HDRP), G10 SRP 真实环境需换项目验证 (§3.2)
- 场景内已知 asset: `Assets/RoadAssets/Generated/mat_curb.mat` 被 `Road / RoadBuilder.curbMaterial` 引用 (G18 精准命中测试数据)

### 4.4 关键反射探测脚本 (换 Unity 版本必跑)

**用途**: 检查 `UnityEditorInternal.FrameDebuggerInternal.*` internal API 是否在新版 Unity 存在及签名是否兼容。

**执行方式**: 在 AgentCore Chat 里作为 `execute_code code=<paste-below>` 运行, 拿到输出对照 v1.8.0 的期望结构。

```csharp
var sb = new System.Text.StringBuilder();
string[] typeNames = new string[] {
    "UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility, UnityEditor.CoreModule",
    "UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerEvent, UnityEditor.CoreModule",
    "UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerEventData, UnityEditor.CoreModule",
    "UnityEditorInternal.FrameDebuggerInternal.FrameEventType, UnityEditor.CoreModule",
};
var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly;
foreach (var tn in typeNames) {
    var t = System.Type.GetType(tn);
    if (t == null) { sb.AppendLine("NULL: " + tn); continue; }
    sb.AppendLine("=========== " + t.FullName + " ===========");
    foreach (var mi in t.GetMethods(flags)) if (!mi.IsSpecialName) sb.AppendLine("  " + mi.Name + "(" + mi.GetParameters().Length + " args) -> " + mi.ReturnType.Name);
    foreach (var pi in t.GetProperties(flags)) sb.AppendLine("  prop " + pi.PropertyType.Name + " " + pi.Name);
}
UnityEngine.Debug.Log(sb.ToString());
sb.ToString()
```

**v1.8.0 期望输出关键字**: `SetEnabled(2 args)`, `GetFrameEventData(1 args)`, `GetFrameEvents(0 args)`, `count`, `limit`, `locallySupported`, `eventsHash`, `eventDataHash`。任一缺失需重写 G03 反射逻辑。

---

## 5. Git 状态

### 5.1 当前 commit / tag

- **HEAD**: `eed0c3d` (v1.8.0 一次性提交所有改动 + 3 handoff 文档补 commit — 见 §5.3)
- **Tag**: `v1.8.0` 已推 GitHub
- **Remote**: `git@github.com:BakaAkari/agentcore-unity.git`
- **Branch**: `main`

### 5.2 UPM 包分发

- **新包**: `com.agentcore.unity-1.8.0.tgz` (4.8 MB, 705 files) — 项目根目录
- **旧包归档**: `Archive/com.agentcore.unity-1.7.29.tgz` (+ v1.7.28/27/20/19/... 完整历史)
- **安装到新项目**: `Window > Package Manager > + > Install package from tarball > 选 com.agentcore.unity-1.8.0.tgz`

### 5.3 本次 handoff commit 变更

- 新增 `plans/HANDOFF-v1.8.0-to-v1.9.0.md` (本文档)
- 新增 `plans/capability-coverage-audit.md` (从 skill 拷贝, 项目内可访问版本)
- 新增 `plans/adversarial-coverage-audit.md` (同上)
- 新增 `plans/agentcore-execute-code-constraints.md` (同上)
- 更新 `AGENTS.md` 顶部加入 handoff 入口指引

### 5.4 换机器后的 git 初始化

```bash
cd /path/to/workspace
git clone git@github.com:BakaAkari/agentcore-unity.git
# 或 HTTPS
git clone https://github.com/BakaAkari/agentcore-unity.git

cd agentcore-unity
git log --oneline -5  # 应看到 handoff commit 是最新的
git tag -l | grep v1.8  # 应有 v1.8.0
```

**如果新设备 git 需要重新认证**: 用户已有 GitHub 认证工作流, 参考记忆里 "GitHub repo=BakaAkari/agentcore-unity. Git 已认证可 push"。

---

## 6. 关键技术决策上下文 (新 agent 必知)

### 6.1 SOUL.md 是 agent 行为准则文件, 不是普通文档

**位置**: `Editor/Bootstrap/Resources/SOUL.md`
**加载**: `SoulReader` 启动时读取, 拼装到系统 prompt
**关键约束**: 修改 SOUL 必须保持标号 1~12 结构稳定 (v1.7.28 就是新增引导时刻意选择"§2.10 内追加"而非新条款编号)
**v1.8.0 新增**: §2.11 Profiler 引导 (G27 discoverability 修复的必要条款)

### 6.2 工具确认流程是分级的, 不是一刀切

- `ToolRiskLevel` × `ToolCapability` 联合决策 (v1.7.16 重构)
- 判定粒度在 **action 级**, 不是 tool 级
- 新增 action 时必须在 `AgentToolAttribute` 明确 risk / capability 标注

### 6.3 Undo 契约是硬约束, 不是可选项

- 任何 GameObject/Component 修改必须前置 `Undo.RecordObject(target, "...")`
- 新 GameObject 后立即 `Undo.RegisterCreatedObjectUndo`
- 批量走 `Undo.SetCurrentGroupName` + `Undo.CollapseUndoOperations`
- 使用 `ToolHelpers.RecordUndo` / `ToolHelpers.RegisterCreatedObject` 包装 API 是首选 (v1.7.29 审计已证明)
- 跳过 Undo = 静默破坏 Ctrl+Z 契约 = 输出错误 (不是小瑕疵)
- Intentionally non-undoable 白名单: `manage_asset delete/move/rename`, `manage_build`, `manage_package`, `manage_script`, `manage_scene save-create` — 这些必须在执行前警告用户

### 6.4 execute_code 是通用能力的顶点, 不是备选

- SOUL §2.10 明确: 一次性批量/查询/自动化优先 execute_code:run, 不要创建 .cs 脚本
- 反射探测 internal API 走 execute_code (两阶段: dnfile 找 assembly + Editor 运行时 dump 成员)
- 硬约束见 `plans/agentcore-execute-code-constraints.md`
- v1.7.21~v1.7.25 五连弹已删除 20 个可用 execute_code 覆盖的特化 action

### 6.5 Version Defines 是 SRP / 可选 package 依赖的正确隔离方式

- **不要**在 asmdef 里加 `com.unity.render-pipelines.core` 到 references (硬依赖 = built-in 项目直接编不过)
- **要**用 asmdef `versionDefines` 条目: 包名 + expression `0.0.0` + define `AGENTCORE_HAS_SRP_CORE`
- 代码全段包 `#if AGENTCORE_HAS_SRP_CORE ... #else fallback stub ... #endif`
- v1.8.0 G10 首次示范, v1.9.0 P2 里 Addressables/Localization/XR 全走同一模式

### 6.6 反射盲写是主要风险源

- v1.7.26/27 (execute_code 反射 Evaluator 5 轮失败) + v1.8.0 G02 (双开关) 都是同类翻车
- 修复模式: **两阶段探测** — (1) 磁盘 dnfile 找 assembly + 类型存在性, (2) Editor 运行时 GetMembers dump 完整签名
- Cache resolved reflection 到 static field, 但保留 error state 让后续调用短路失败 (不要每次都重跑反射)

---

## 7. 快速故障排查

| 症状 | 可能原因 | 处理 |
|---|---|---|
| `read_frame` 返回 0 帧 | ProfilerDriver.enabled 或 profileEditor 未开 | 调 `get_frame_range` 看双开关状态 |
| `volume_list` 返回 "SRP not detected" | 项目未装 URP/HDRP, 或 asmdef versionDefines 表达式不匹配 | 检查 `AgentCore.Editor.asmdef` versionDefines, 检查 Package Manager 是否装了 SRP core |
| `list_draw_events` 返回 0 events | GameView 没渲染 (被 Chat 挡住 or 主线程阻塞) or 未进 Play Mode | 先手动打开 Frame Debugger 窗口看是否有 event, 排除环境问题 |
| `find_references` 慢 | 全库扫描无 filter | 用 `filter="t:Prefab t:Scene"` 缩小候选面 (300× 提速) |
| `find_references_in_scene` 漏检 | 目标 asset 是运行时动态生成 (不在序列化数据里) | 工具设计限制, 需另加运行时追踪 (v1.9.0+ 未排期) |
| execute_code 报 `error CS0234: The type or namespace name 'X' does not exist` | RunUsings 或 ReferenceAssembly 未包含目标程序集 | 反射目标程序集的 typeof().Assembly, 加到 ExecuteCodeTool.cs ReferenceAssembly 列表 |
| `patch` 工具报错锚点找不到 | 文件含 `\r\n` 且 patch 内容混用 `\n` | 用 execute_code 字节级 `str.replace` 绕过 |

---

## 8. 联系 & 交接确认

- **原开发者**: Akari (via AgentCore + 米塔 agent)
- **GitHub**: [BakaAkari/agentcore-unity](https://github.com/BakaAkari/agentcore-unity)
- **接手确认清单**:
  - [ ] 新设备 clone repo 成功, `git log` 看到 handoff commit
  - [ ] 用 Package Manager 从 tarball 安装 `com.agentcore.unity-1.8.0.tgz` 成功
  - [ ] Unity 打开测试项目, AgentCore Chat 窗口能打开
  - [ ] `execute_code` 简单 smoke test (`1+1` 返回 2) 通过
  - [ ] 读完本文档 §0 列出的 8 份文档
  - [ ] 跑过 §4.4 反射探测脚本 (若换了 Unity 版本)

**祝新 agent 开发顺利。v1.9.0 加油。**
