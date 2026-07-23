# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.8.1] - 2026-07-23

### Context

**跨设备迁移暴露的三处历史坑一次性收敛的 patch 版本。** v1.8.0 在原开发机 (Windows) 上打包/运行完全正常, 迁移到 macOS 新设备并重新 clone 后触发三处编译/打包/结构问题, 表面症状不同但根因都是"旧环境掩盖了配置错位":

1. `com.agentcore.unity` clone 后 [`Editor/Plugins/Roslyn/`](Editor/Plugins/Roslyn) 目录只有 5 个 `.meta`, 5 个 DLL 丢失 → `RoslynSymbolExtractor.cs` 引用 `Microsoft.CodeAnalysis.*` 编译失败
2. URP 项目下 `manage_graphics` 的 volume_* 三 action 报 `CS0246: VolumeProfile / VolumeComponent could not be found`
3. macOS `npm pack` 时 PowerShell 打包钩子 (`prepack`/`postpack` → `.ps1`) 因 `powershell` 命令不存在直接失败

### Fixed — 三处配置/代码 bug 定位

- **Roslyn DLL 从未被 git 追踪** ([`.gitignore`](.gitignore) 第 50 行): `*.dll` 后跟 `!Packages/**/*.dll` 白名单, 但插件仓库根不是 `Packages/…` 路径, 反选规则永远不匹配, 导致 5 个 DLL 无条件被忽略. 旧开发机磁盘上 DLL 存在是因手动放入 + 从未 clone 过全新副本, 新机器 clone 后**必然缺 DLL**. 修复: 加显式白名单 `!Editor/Plugins/Roslyn/*.dll`, 5 个 DLL 现纳入 git track (增加约 9 MB, 一劳永逸).
- **SRP core asmdef 引用缺失** ([`Editor/AgentCore.Editor.asmdef`](Editor/AgentCore.Editor.asmdef)): v1.8.0 G10 用 `versionDefines` 定义 `AGENTCORE_HAS_SRP_CORE` 触发 `#if` 编译分支, 但 asmdef `references` 空数组, 编译器保留了 `#if` 内代码却看不到 `UnityEngine.Rendering.Volume*` 类型 (定义在 `Unity.RenderPipelines.Core.Runtime.dll`). 修复: `references` 加 `"Unity.RenderPipelines.Core.Runtime"`. built-in 项目下 Unity 会为不存在的引用输出一条 warning 但不阻断编译, 符合"SRP 未装则降级"的既定设计意图.
- **测试代码泄漏进 UPM tarball** ([`.npmignore`](.npmignore)): [`Editor/Tests/`](Editor/Tests) 下的 `ToolHelpers`/`JsonHelper`/`TokenCounter` 等 6 个测试类通过 asmdef `defineConstraints=UNITY_INCLUDE_TESTS` 隔离编译, 但 tarball 打包时未排除. 终端用户装 tgz 后 Cecil 后处理扫描 `Library/ScriptAssemblies/` 找不到 `AgentCore.Tests.Editor.dll` → 一条 "Failed to resolve assembly" warning (无害但污染 Console). 修复: `.npmignore` 加 `Editor/Tests/` 排除, 打包体积略减 (~21 个文件), 用户零信息损失.

### Changed — 打包工具链跨平台 (Node.js port)

- **新增 [`tools/verify-meta.cjs`](tools/verify-meta.cjs)** 和 **[`tools/verify-tarball.cjs`](tools/verify-tarball.cjs)**: PowerShell 打包护栏的 Node.js 等价实现. 零外部依赖 (只用内置 `fs`/`path`/`child_process`), 跨 macOS/Linux/Windows 通用. 保留 v1.4.5 (missing .meta 事故) 和 v1.4.6 (`.npmignore` 无锚点 glob 事故) 的完整防护语义, 参数/退出码/输出结构与 `.ps1` 版本一致.
- **[`package.json`](package.json) scripts 迁移**: `prepack`/`postpack` 指向 `.cjs`, 原 `.ps1` 入口保留为 `verify-meta:ps1` / `verify-tarball:ps1`. macOS/Linux 上 `npm pack` 现可直接跑, 不再需要 `--ignore-scripts` 绕过钩子.

### Changed — Docs

- [`plans/HANDOFF-v1.8.0-to-v1.9.0.md`](plans/HANDOFF-v1.8.0-to-v1.9.0.md) §4.1 显式区分两个 Unity 版本语义: 项目锁定 Editor (`2022.3.50f1`, 来源 `ProjectSettings/ProjectVersion.txt`, 反射唯一验证版本) vs UPM 声明最低兼容 Editor (`2021.3.0f1`, 来源 `package.json`, tarball 分发准入门槛). 新 agent 装环境用锁定版本, 放宽 UPM 最低支持面必须回归所有反射目标.
- 补 4 个 `plans/*.md.meta` (Unity 首次导入自动生成, v1.8.0 handoff commit 时机不对导致漏 commit).

### Migration notes

- **v1.8.0 → v1.8.1 无兼容性破坏**: 所有 API 签名不变, tarball 结构不变 (除去掉 `Editor/Tests/`).
- **新设备接手**: `git clone` 后现在会直接拿到 Roslyn DLL, 不再需要从旧 tgz 抽出. 若历史上手动放过 DLL, 新版 `.gitignore` 白名单让其可 track, 建议 `git add Editor/Plugins/Roslyn/*.dll` 显式提交.
- **打包环境**: macOS/Linux 直接 `npm pack` 即可, `--ignore-scripts` 不再需要. Windows 上 `npm pack` 走 `.cjs` (需 Node.js), 若坚持 PS 版可 `npm run verify-meta:ps1` / `npm run verify-tarball:ps1`.

## [1.8.0] - 2026-07-23

### Context

**能力覆盖面缺口补齐 milestone。** v1.7.x 系列以 SOUL / Undo / 安全护栏收官后，转入"能不能做 X"的实际覆盖问题。A×B 双轴（工具× action）全量审计发现 28 项真缺口（P0=7 / P1=11 / P2=10），本版本按路径 A 一次性完成全部 P0 7 项 + 2 个副产物 Bug hotfix。MCP-over-tools 延后至 v1.9.0。

**审计成果详见** `skills/software-development/agentcore-development/references/capability-coverage-audit.md`。

### Added — P0 能力覆盖缺口 7 项

**Profiler 三件套（G01/G02/G03）** — `manage_profiler` 从"只能读 aggregate stats"升级为完整 Profiler 数据链路：

- **`list_available_stats`** (G01a): 枚举 `ProfilerRecorderHandle.GetAvailable()` 所有可采样统计名，支持 `category` 过滤（Render/Scripts/Memory/Gui/Physics/Animation/Ai/Audio/Video/Particles/Vr/FileIO/Internal）。之前 agent 想采样但不知合法 stat 名的死循环解决。
- **`sample_recorder`** (G01b): 用 `ProfilerRecorder` 对指定 stat 做时序采样，`frame_count` 帧 × `capacity` 环形缓冲；返回 min/max/mean/last + 每帧数组。EditorApplication.update 驱动，Play Mode 或重绘中的 Editor 下产出有意义值。
- **`get_frame_range`** (G02a): 返回 `ProfilerDriver.[firstFrameIndex, lastFrameIndex]` + `profiler_enabled` / `profiler_driver_enabled` 双开关状态，用于诊断"为什么 read_frame 返回空"。
- **`read_frame`** (G02b): 通过 `ProfilerDriver.GetHierarchyFrameDataView` 读取任一历史帧的 marker hierarchy，支持 `thread_index` 切换（Main / Render / Job Worker）+ 按 self-time 排序返回 top-N marker。**发版实测**：EditorLoop=228ms/帧 = 4.4 FPS，识别到 agent 流式输出阻塞主线程的问题（详见 `plans/perf-issue-agent-streaming-blocks-editor.md`，v1.8.0 后续版本优化）。
- **`list_draw_events`** / **`get_draw_event`** / **`disable_frame_debugger`** (G03): 反射 `UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility`（Unity 2022.3.x internal API）枚举 GPU 帧事件。`list_draw_events` 支持 `enable_if_needed` 自动开启 FrameDebugger（要 Play Mode）；`get_draw_event` 返回单事件详情（shader/pass/keywords、顶点/索引/实例数、blend/depth/raster/stencil 全套 pipeline state、render target、mesh 信息、batch break cause、compute shader、ray tracing）；`disable_frame_debugger` 退出调试模式恢复 GameView。反射链路已验证（`SetEnabled` 成功、count 正确返回、结构化 hint 提示环境依赖）。

**能力覆盖 P0（G10/G17/G18）** — 三项之前完全缺失的 asset↔scene 查询：

- **`manage_graphics` 新增 `volume_list` / `volume_get` / `volume_set`** (G10): 通过反射 `Volume` / `VolumeProfile` / `VolumeComponent` / `VolumeParameter<T>` 读写 URP/HDRP 后处理效果。**Version Defines 隔离 SRP 依赖** — `AgentCore.Editor.asmdef` 新增 `versionDefines` 条目：包 `com.unity.render-pipelines.core` expression `0.0.0` → 定义宏 `AGENTCORE_HAS_SRP_CORE`。SRP 未安装时 fallback stub 返回结构化错误（"SRP not detected — install URP or HDRP via Window > Package Manager, then recompile"），零硬依赖。built-in 项目实测 fallback 通过；SRP 环境真实链路留待有 URP 的项目验证。
- **`manage_asset` 新增 `find_references`** (G17): `AssetDatabase.GetDependencies` 反向扫描 —— 遍历项目所有 asset 找出谁引用了目标资产。支持 `filter`（透传 `AssetDatabase.FindAssets` 语法，如 `t:Prefab t:Scene` 缩减扫描面 300×提速）+ `recursive`（间接依赖）。实测：全量 89 asset 301ms，filter 后 5 asset 1ms，recursive 243ms。`get_dependencies`（正向）已存不重复。
- **`scene_analysis` 新增 `find_references_in_scene`** (G18): 跨 asset↔scene 边界的场景引用查询。输入 asset path，遍历 loaded scenes 每个 GameObject 每个 Component 的 `SerializedObject` walker，命中 `ObjectReference` 属性值等于目标 asset（含 sub-asset）时记录 `GameObject / Component / property / propertyDisplay / referencedSubAsset / isMainAsset / gameObjectActive`。实测精准命中：`Assets/RoadAssets/Generated/mat_curb.mat` → `Road / RoadBuilder.curbMaterial`（不是标准 MeshRenderer.m_Materials，说明能穿透**自定义脚本序列化字段**）。

### Fixed — 副产物 Bug hotfix 2 项

- **`manage_camera action=render_to_texture`**（Bug#1）: 之前调用时报 ImageConversion 引用错误。修复后可产出 PNG 到指定路径。
- **`execute_code` ImageConversion**（Bug#2）: 编辑器脚本环境下 `Texture2D.EncodeToPNG()` 引用失败。已修复运行时程序集引用。

### DISCOVERABILITY — G27 SOUL 引导

- `manage_profiler` 增加 SOUL `§2.11 Profiler use hints` 明确"什么场景用哪个 action"引导，Extended 类别下的 `AlwaysVisible` visibility 让 agent 一眼看到工具。之前 agent 想诊断性能却因 Profiler 在 OnDemand 类别看不见 → G27 修复。

### Fixed — 反射盲写 pitfall（G02 隐藏依赖）

`ProfilerDriver.enabled`（Editor 帧缓冲开关）与 `Profiler.enabled`（运行时采样开关）是**两个独立开关**。`start_recording` 之前只开 `Profiler.enabled` 导致 `read_frame` 一直返回 0 帧。修复：同时开两个 + `ProfilerDriver.profileEditor=true` 保证 Editor 时段帧被采集；`stop_recording` 对称关闭。`get_frame_range` 的 hint 分层诊断（driver / profiler / 等帧）指出具体哪个开关没开。

### Changed — Docs

- SOUL.md §2.11 新增（G27 Profiler 引导）
- `plans/perf-issue-agent-streaming-blocks-editor.md` 记录 v1.8.0 期间发现的 agent 流式输出阻塞 Editor 主线程问题（228ms/帧 = 4.4 FPS 现象），归因、三条优化路径（异步/累积 flush / 静默 + 状态栏 / 组合），留待后续版本优化，不阻塞本次发版
- `plans/ROADMAP.md` Phase 10 v1.8.0 收尾章节

### Skills — 新增（`agentcore-development` 技能配套）

- `references/agentcore-execute-code-constraints.md` — 记录 `execute_code` 工具的 Mono.CSharp.Evaluator 硬约束（无 using 指令、无 top-level return、无 C# 8+ 语法、`Object` 歧义、Scripting 类别 activation gate、反射探测两阶段模式）
- 若干 references 补齐（详见技能目录）

### Deferred to v1.9.0

- P1 11 项 + P2 10 项能力覆盖缺口
- MCP-over-tools 集成
- Agent 流式输出主线程优化

## [1.7.29] - 2026-07-22

### Context

v1.7.28 SOUL §2.10 R3 引导条款落地后，用户追问"目前所有 agent 操作都能 undo 吗?" — 触发对抗式 Undo 全量审计。

第一轮扫描（只搜 `Undo.RecordObject`/`Undo.RegisterCreatedObjectUndo` 裸调用）判定 34 个 mutating 工具里有 8 个 "mut_no_undo"，属于 false positive：`ToolHelpers.RecordUndo` / `ToolHelpers.RegisterCreatedObject` 包装 API 未被识别。第二轮补正正则（增加 `ToolHelpers.RecordUndo`/`ToolHelpers.RegisterCreatedObject` + `SerializedObject.ApplyModifiedProperties` 三个 Unity 官方等价信号）后 23/34 已合规。真正缺 Undo 的 5 处此版一次性补齐。

审计还发现 `ExecuteCodeTool` 的 `File.Delete`/`File.Move` 是**危险 API 黑名单字符串**，是拒绝执行前的匹配用途，不是实际 mutation — 排除，不需要 Undo。

### Change

**代码层 Undo 补齐 (5 handlers × 4 文件):**

- `ManageAssetTool.HandleDelete`: `AssetDatabase.DeleteAsset` → `AssetDatabase.MoveAssetToTrash`。硬删（不进回收站，用户无法自恢复）→ 移入 OS 回收站（用户可从回收站恢复）。**是本次最严重的可逆性修复**。AgentTool Description 里 delete 语义同步更新为 "move asset to OS recycle bin — recoverable"。
- `ManageAssetTool.HandleCreateFolder`: `AssetDatabase.CreateFolder` 后追加 `Undo.RegisterCreatedObjectUndo(newFolder, "Create Folder")`，Ctrl+Z 可撤销创建。
- `ManageAssetTool.HandleMove` / `HandleCopy`: `Undo.RegisterCompleteObjectUndo` 前置录制 + 响应 payload 追加 `reverseHint` 字段引导 agent 反向调用（Move 是无法通过 Ctrl+Z 撤销的原子操作，asset 路径在 Unity 语义里等于身份）。
- `ManageAssetImportTool.HandleSetLabels`: `AssetDatabase.SetLabels` 前 `Undo.RecordObject(asset, "Set Labels on {path}")`。
- `ManageAssetImportTool.HandleSetBundle`: 修改 `importer.assetBundleName` / `assetBundleVariant` 前 `Undo.RecordObject(importer, "Set AssetBundle on {path}")`。
- `ManageTextureImportTool` 全部 5 个 mutation handlers (`HandleSetSettings` / `HandleSetSettingsBatch` / `HandleSetType` / `HandleSetPlatformSettings` / `HandleSetSpriteSettings`)：`.SaveAndReimport()` 前统一前置 `Undo.RecordObject(importer, "Set TextureImporter Settings on {path}")`。批量版加 `(batch)` 后缀区分。
- `ManageModelImportTool` 全部 4 个 mutation handlers (`HandleSetSettings` / `HandleSetSettingsBatch` / `HandleSetAnimationClips` / `HandleSetRig`)：同上，前置 `Undo.RecordObject(importer, ...)`。

**引导层 non-undoable 白名单 (SOUL §2.10 第 10 条追加):**

明确告知 agent 以下操作 **Ctrl+Z 不可逆**，执行前必须提醒用户 + 优先用工具 dry-run/preview：`manage_asset delete` (走 OS 回收站，Editor Undo 不可回复)、`manage_asset move`/`rename` (响应含 reverseHint，反向调用 = 撤销)、`manage_build` (磁盘构建产物)、`manage_package` (Package Manager 状态)、`manage_script` 文件写入 (磁盘 .cs，回滚靠 VCS)、`manage_scene save`/`create` (场景文件 I/O)。

### 未做的事(留白)

- **`ManageInputTool`**: `SerializedObject.ApplyModifiedProperties()` 官方契约自动记录 Undo，`InputManager.asset` 修改经 Ctrl+Z 可完整撤销 — 无需改。
- **`ExecuteCodeTool`**: `File.Delete`/`File.Move` 是危险 API 黑名单字符串，不是实际调用 — 无需改；用户在 execute_code 里写自己的代码时的 Undo 责任由 SOUL §2.10 引导条款兜底。
- **Batch group boundary**: 未引入 `Undo.SetCurrentGroupName + CollapseUndoOperations` 把批量 handler 折叠成单个 Undo step。当前批量操作 Ctrl+Z 会逐项回滚，语义正确但按键次数多。后续若用户反馈按键次数烦人再加。
- **v1.8.0 范围变更**: 用户明确"MCP拓展不是 1.8.0 的内容，能力覆盖面缺陷才是"。Phase 8 MCP Server 延后到 v1.9.0+ 或后续版本；v1.8.0 主题变更为**能力覆盖缺口补齐**（触发缺口：agent 无法直接抓帧和分析运行时性能阻点；后续需系统性排查 47 工具 vs Unity 官方顶层菜单 × UnityEditor 命名空间双轴矩阵）。详见 ROADMAP §3.x 更新。

### Verified

- 用户 Unity 端编译验证：0 errors 0 warnings（用户 2026-07-22 确认"编译完成没有警告报错"）。
- Undo 审计三次修正：v1 只扫 `Undo.RecordObject` 裸调用 → 8 false positive；v2 扩正则含 `ToolHelpers.RecordUndo`/`RegisterCreatedObject` 包装 → 23/34 合规；v3 补 `SerializedObject.ApplyModifiedProperties()` 契约识别 → `ManageInputTool` 也归入已合规。最终真需改 = 5 handlers × 4 文件。审计方法教训写入 `references/adversarial-coverage-audit.md`（扫描必须含三个等价信号，避免只扫 `Undo.RecordObject` 的 false positive）。
- Diff 覆盖矩阵：34 mutating 工具 → 23 已合规 (`ToolHelpers.RecordUndo` 包装 or `SerializedObject.ApplyModifiedProperties`) + 5 已在此版补齐 (`Undo.RecordObject` 前置) + 5 属**语义非 Undo**（`manage_asset delete/move`/`manage_build`/`manage_package`/`manage_script`/`manage_scene save-create`，SOUL §2.10 白名单引导）+ 1 排除项 (`ExecuteCodeTool` 黑名单字符串) = 34 项账目对齐。
- 最严重可逆性修复：`ManageAssetTool.HandleDelete` 硬删→OS 回收站。


## [1.7.28] - 2026-07-22

### Context

用户第 6 轮追问"从一致性原理出发再次分析系统缺哪些 Unity 工具模块的覆盖 + 什么时候升到 1.8.0"触发的对抗式校验。校验维度:
- 用 grep 枚举 47 个 [AgentTool] 属性,对齐 Unity Editor ~35 个常用模块,量化覆盖率 ~85%,余 5 模块(Undo/Post-Processing/RenderFeature/ProjectSettings 其他项/Mesh 编辑)全部能被 execute_code 兜底。
- 逐个复查历史用户报告"创建 .cs 走菜单项"错误链的 4 类根因 (menu_path 缺失/Domain Reload 时序/action 枚举错/单表达式反射器) 是否在其他工具重演。
- R2 (action 型 dispatcher default case 是否列出可用 action) 精准正则扫全部 44 个 action-型工具 → 44/44 全绿(v1.7.14~v1.7.25 五连弹已扫干净)。
- 对抗式定位到唯一两处真实的系统性缺陷:R1 跨调用资源时序 + R3 Undo 缺失。均在 SOUL 引导层,不在工具层。

### Change

- **SOUL §2.10 第 10 条追加 "Undo contract" 子条款** (R3 修复):任何对 scene GameObject/Component 的修改必须前置 `Undo.RecordObject(target, "operation name")`; `new GameObject(...)` 之后必须紧跟 `Undo.RegisterCreatedObjectUndo`; 批量走 `Undo.SetCurrentGroupName + Undo.CollapseUndoOperations`。跳过 Undo 视为静默破坏 Ctrl+Z 契约的输出错误,不是小瑕疵。
- **SOUL §2.10 第 10 条追加 "Cross-call resource timing" 子条款** (R1 修复):在一次调用内创建/移动 asset,下一次调用按路径引用它是 racy 的——必须在**同一 call** 里显式 `AssetDatabase.SaveAssets(); AssetDatabase.Refresh();` 之后路径才可查询。若新写的 script/shader 类型需要反射(如 `AddComponent<NewClass>()`),必须 Domain Reload,单个 execute_code 块内做不到——拆分工作流:写完即返回让用户等重编译,或调用 `EditorUtility.RequestScriptReload()` 并把下一次调用当作 post-reload 处理。Prefab 编写用 `PrefabUtility.SaveAsPrefabAsset(instance, path)`,不要自造 `File.WriteAllText`。
- **零代码变更**:纯引导层升级,不改任何 .cs。条款编号 1~12 保持稳定(在第 10 条内追加子条款,不新增第 13 条),避免打断其他文档对 §2.10 的引用。

### 未做的事(留白)

- **R4 (Timeline/Cinemachine 类型 CS0246)**:两个模块有独立工具(`manage_timeline` / `manage_cinemachine`),agent 走独立工具不需要绕 execute_code。除非用户反馈,不追加 ReferenceAssembly。
- **R5 (Play Mode / Editor 状态耦合)**:罕见,SOUL §4 已有部分 Unity 引擎语义警告,后续按需追加。
- **v1.8.0 不 bump**:与用户共识,v1.8.0 应锚定 Phase 8 MCP Server 对外可交付首版,而非累积 patch 数量。v1.7.x 系列继续做内部完善收尾。

### Verified

- 覆盖矩阵扫描:47 个原生工具 vs ~35 个 Unity Editor 常用模块 → 覆盖 30 (~85%),余 5 execute_code 全覆盖。
- R2 精准扫描:44 个 action 型工具的 dispatcher default case,44/44 均包含 "Valid actions: ..." 或等价 hint(GOOD 分类 44,WARN_ONLY_UNKNOWN 0,SILENT 0,UNCLEAR 0)。
- SOUL.md 语法校验:1~12 条编号连续,§2/§3/§4/§5 章节结构保持,总长 9152→~11500 chars。
- 无代码 diff,`AgentCore.Editor.dll` 不受影响,老 dll 与新 SOUL 兼容运行(SOUL 是 Resources 里的引导资源,Bootstrap 每次会话开头读取,更新即生效不需 recompile)。



## [1.7.27] - 2026-07-22

### Context

v1.7.26 完全重写了 ExecuteCodeTool 走 Mono.CSharp.Evaluator，但发布前的 smoke 测试暴露 **5 轮反复失败**：Case A `var x = 40; x + 2` 期望返回 42，Case B `Debug.Log("SMOKE_B");` 期望 output 含 `[Log] SMOKE_B`，Case C `__result = "legacy";` 期望编译失败 CS0103。5 轮迭代每次换一个新错误（CS0433 → CS0246 → CS0234 → CS0234 → 双通道 log 重复），我一直以为是**程序集引用配置错**，反复换 ReferenceAssembly 组合都修不好。

对抗性根因（Claude Opus 换手后的全量分析）：**不是程序集问题，是错误分类器 bug**。

- v1.7.26 首次用 `StreamReportPrinter` 拿到完整 Mono.CSharp 诊断后，实现了 error/warning 分离逻辑。但 Unity 2022.3 的 Mono2x 环境下，每个 CS1685 warning（mscorlib 类型重复定义，无害噪音）都跟一行续行 `"C:\...\System.Core.dll (Location of the symbol related to previous warning)"`——这行**既不含 `error CS`，也不含 `warning CS`**，前置 safe-default 分支把它当 error → 所有求值都被判 FAIL，即使程序集配置对了也无效。
- 我 5 轮迭代都在改 ReferenceAssembly 列表（删掉、facade 换 CoreModule、只留 3 个 spike 引用），但**从未质疑分类器逻辑**。每次修完看到"CS0433/CS0246/CS0234"就以为是新错误，实际上大部分错误信号都是 CS1685 warning 的续行被误判 —— 表象在换，根因不动。
- 用户在第 5 轮暴怒后换 Claude Opus 4-7 重新分析，读了完整 554 行源码后定位到分类逻辑，翻转 safe-default——只有匹配 `\berror\s+CS\d+\b` 的行才归 error，其他一律归 warning。三 case 立刻全 PASS。

### Change

- **错误分类器修复（本次核心）**：`HandleRun` 里的 sink 解析改用严格正则 `\berror\s+CS\d+\b` 白名单匹配 error，续行 / `(Location of the symbol...)` / 未知格式一律归 warning。以后即使 Mono.CSharp 换版本输出未知格式的诊断，也不会误判成 error 阻塞成功。
- **恢复 v1.7.0 全部 10 个默认 usings**：v1.7.26 因误判把 usings 缩到 4 个（`UnityEngine / UnityEditor / System.Linq / System.Collections.Generic`）作为"最小实证配置"。分类器修好后 5 轮里所有 CS0433/CS0246/CS0234 的表象自动消失，把 usings 恢复到完整清单：`System / System.IO / System.Text / System.Text.RegularExpressions / System.Linq / System.Collections.Generic / UnityEngine / UnityEngine.SceneManagement / UnityEditor / UnityEditor.SceneManagement`。同步扩 `EnvironmentHint`。
- **补 `UnityEditor.SceneManagement` 程序集引用**：`EditorSceneManager` 类型不在 `UnityEditor.CoreModule.dll`，独立在 `UnityEditor.SceneManagerModule.dll`。`ConfigureEvaluator` 新增 `typeof(EditorSceneManager).Assembly` 引用，用 `HashSet<Assembly>` 去重避免同 dll 二次引用产生的 CS0433。其他命名空间的程序集分布：GameObject/Scene 同在 UnityEngine.CoreModule，Enumerable 在 System.Core，System.IO/Text/Regex 在 mscorlib+System（GetDefaultReferences 已覆盖）。
- **双通道 Debug.Log 捕获去重**：v1.7.26 同时订阅 `Application.logMessageReceived` + `logMessageReceivedThreaded`，Editor 主线程日志被两条通道各触发一次导致 `output` 数组出现两条相同 `[Log] X`。改为**只订阅 `logMessageReceivedThreaded`**（Threaded 变体覆盖所有线程含主线程，是完整覆盖）。
- **Evaluate 剩余字符串归入 compileError**：Mono.CSharp `Evaluate(string, out object, out bool)` 返回非空字符串表示尾部无法解析（未闭合括号/未终结字符串等语法错误），v1.7.26 把它写进 `runtimeError`——本质上是**编译期失败**，不是运行时异常。改为独立变量 `parseTailError`，最终合并进 `errorLines` 走 `compileError` 分支，响应分类与"编译错误必须以失败返回"契约一致。
- **XML doc 同步现状**：类头注释还在描述"Console.Error 重定向"（v1.7.26 首版策略），已改用 StreamReportPrinter+StringWriter。同步更新契约声明为 v1.7.27。

### 搭车杂项（工作区累积的独立小改动，一并 commit）

- **AgentCoreSettings v20→v21 迁移**：v20 迁移声称"清理 disabledTools 默认值"但实际没执行 Clear，导致部分历史 settings 仍在 disabledTools 里挂着硬编码的 `execute_code`——ToolRegistry 过滤掉该工具后 SOUL §2.10 引导的能力实际不可用。v21 迁移精准移除 `execute_code` 单项（不清空整个 disabledTools 以尊重用户其它禁用意图）。
- **LightRAGClient query 超时 30s→180s**：`/query` 触发后端 LLM 推理 + 图谱/向量检索，冷启动/刚索引完首次查询实测持续超时；`PostAsync` 新增可选 `timeoutSeconds` 参数，查询走 180s 独立超时，其他快操作（索引/文档管理/健康检查）保持 30s。
- **Mem0Client add 自动注册用户**：OpenMemory 对未注册的 user_id 返回 404 "User not found"，用户需手动去服务端注册。新增内部实现 `AddMemoryAsync(..., bool allowAutoRegister, ...)`：add 失败时若识别为 user not found 错误，自动调 `CreateUserAsync` 隐式注册后重试一次；`allowAutoRegister=false` 阻止无限递归。

### Verification

三 case smoke test（Unity 2022.3.50f1 + Mono2x）全 PASS：

| Case | 代码 | 实测 |
|---|---|---|
| A | `var x = 40; x + 2` | `success:true / result:42 / resultType:System.Int32`；warnings 里仅两条 CS1685（DynamicAttribute + Expression）|
| B | `Debug.Log("SMOKE_B");` | `success:true / resultType:void`；`output:["[Log] SMOKE_B"]`（**单条不重复**）|
| C | `__result = "legacy";` | `success:false`；`compileError` 含 `error CS0103: The name '__result' does not exist in the current context` |

响应里 `EnvironmentHint` 显示 10 个命名空间清单，实证 usings 恢复且未触发新的引用冲突。静态验证：括号平衡 `{} 58/58`、`() 198/198`、`[] 44/44`；`logMessageReceived` 无 Threaded 后缀变体归零（单一订阅+单一解绑各 1 次）；`'\r', '\n'` char 字面量正确（patch 工具历史坏损已字节级修复）。

## [1.7.26] - 2026-07-22

### Context

v1.7.21~v1.7.25 五连弹推动"通用能力优先于特化堆砌"落地后，用户实测 `execute_code(action="run")` 却发现一个更基础的问题：**任何写法都返回 `success: true / result: null / resultType: void`**，包括最简的 `__result = "hello";`，甚至 `Debug.Log("...")` 的输出也没进 Unity Console（0 entries）。

对抗性自检根因（不是修修补补）：
- v1.7.0 起用反射 `new Evaluator(CompilerContext)` 构造出的评估器是"裸编译器"，**没有 InteractiveBase REPL 全局作用域绑定**——对未声明变量做 `__result = value` 赋值，C# 编译器视为编译错误，不是 REPL 特殊语义。
- Mono.CSharp 的 `ConsoleReportPrinter` 把编译错误写到进程 `Console.Error`（stderr），**既不进 Unity Console 也不进 `Application.logMessageReceived`**，被工具静默吞掉。
- `Run(code)` 调用因编译失败什么都没做，随后 `Evaluate("__result")` 找不到变量 → `resultSet=false` → 返回 "code executed but no value was returned"。
- 结果：**失败被伪装成成功**——agent 拿到 `success:true / result:null` 无法判断"是没赋值"还是"编译错了"，撞墙无解，只能退回创建 `.cs` 脚本的老路（正是 v1.7.21 想根治的）。

v1.7.0 CHANGELOG 声称 "spike PASS"，但 spike 阶段大概率用了 `var __result = ...;` 这种带类型声明的写法，或走 `Evaluate(single-expression)` 单表达式路径，落到 tool 里的 `Run + Evaluate("__result")` 两段式端到端 smoke 从未真正过。

### Change

**ExecuteCodeTool 完全重写**：契约、返回值语义、错误捕获、schema、agent 引导语全部同步换代。

- **`__result = value` 约定彻底废除**。返回值语义改为 Roslyn Scripting / IPython 风格："如果代码块最后一项是**表达式**（无结尾 `;`），它的值就是返回值；否则不返回值"。示例：
  - `var x = 40; x + 2` → 返回 42
  - `var scenes = Directory.GetFiles(...); "found " + scenes.Length` → 返回字符串
  - `Debug.Log("ok");` → 不返回值（正常成功，不是失败）
- **`action` 参数删除**：`execute_code` 从此只有一个入口，参数只剩 `code` + 可选 `context`。旧 schema 里的 `action="evaluate"` 分支完全删除（约 -300 行：`HandleEvaluate` / `EvaluateExpression` / `ResolveType` / `IsAllowedType` / `ParseMethodArguments` / `SplitArguments` / `ConvertArgument` / `AllowedNamespaces` 常量）。传 `action="run"` 静默兼容一个版本周期以缓解迁移；传其他 action 值直接失败并附环境提示。
- **编译错误从"静默吞掉"改为"失败并附错"**：`Console.SetError(new StringWriter())` 在 Evaluate 调用前后重定向 stderr，拿回 Mono.CSharp `ConsoleReportPrinter` 写的完整诊断，作为 `data.compileError` 字段返回 + `ToolResponse.Fail(...)` 而不是 `OkWithData`——agent 撞墙时能立即看到 "line N: The name 'foo' does not exist" 这类真错，不再有"success:true / result:null / 没日志"的黑盒。
- **`Run + Evaluate("__result")` 两段式改为单次 `Evaluate(string, out object, out bool)`**：Mono.CSharp 的 3 参 Evaluate 本身就吃多语句块 + 末尾表达式（这是 Interactive C# `csharp` 命令行的运作方式），一次调用同时得到 result 和 result_set，语义更贴 REPL，也避开了两段式里"第二段编译第一段的变量名字符串"这种脆弱依赖。
- **Debug.Log 捕获保留**：`Application.logMessageReceived` 侦听 + 收集到 `data.output`，与错误捕获并存（同时捕获日志和 stderr 编译错误）。
- **Evaluator 反射构造保留**：不引入 asmdef 依赖（这条历史决策是对的），只是把 `ConsoleReportPrinter` 的行为通过 `Console.SetError` 重定向捕获，无需 Reflection.Emit 生子类。
- **SOUL §2.10 同步重写**：把"assign the final value to `__result`"整段改成"the last expression is the return value"，给三个示例（返回 42 / 返回 string / 不返回），明确声明"legacy `__result` pattern removed in v1.7.26"；命名空间清单和"不要写 .cs 脚本"部分保留。
- **工具 Description / schema.description / RunAvailableNamespacesHint 常量**全部更新为新语义（新常量名 `EnvironmentHint`）。

### Verification

- 静态：括号平衡 `{}` 57/57、`()` 179/179；legacy 标识符 (`__result` / `AllowedNamespaces` / `HandleEvaluate` / `EvaluateExpression` / `ResolveType` / `IsAllowedType` / `ParseMethodArguments` / `SplitArguments` / `ConvertArgument` / `RunAvailableNamespacesHint` / `RunUsings`) 残留 0 或 2（仅在废除说明注释里），新增关键点 (`ConfigureEvaluator` / `EnvironmentHint` / `DefaultUsings` / `Console.SetError` / `errorCapture` / `compileError`) 引用完整。
- 用户端到端 3-case smoke（编译后）：
  - Case A（返回表达式）：`var x = 40; x + 2` → 期望 `result=42, resultType=Int32`
  - Case B（纯语句无返回）：`Debug.Log("SMOKE_B");` → 期望 `resultType=void`、`output` 里能看到 `[Log] SMOKE_B`
  - Case C（编译错误）：`__result = "legacy";` → 期望 **失败**、`compileError` 里含"`__result` does not exist"或类似诊断，不再是 "success:null"

### Files changed

- `Editor/Tools/Native/Scripting/ExecuteCodeTool.cs`（完全重写，764→~430 行）
- `Editor/Bootstrap/Resources/SOUL.md`（§2.10 整段重写）
- `package.json`（1.7.25 → 1.7.26）
- `CHANGELOG.md`（新增本条）
- `README.md`（版本号 + 段落追加）
- `plans/ROADMAP.md`（版本号 + 段落追加）

### Notes

Version bump 到 **1.7.26** 而非 2.0.0：`execute_code` 对外契约变了（`action` 参数、`__result` 约定废除），但该工具是 `Visibility=Restricted`、仅由 agent 通过 SOUL 引导使用，无外部脚本 API 依赖，SOUL 已同步；从用户视角是 agent 行为改善而非 breaking change。若未来加入外部 MCP 暴露，视届时情况再考虑 2.0。

## [1.7.25] - 2026-07-21

### Context
v1.7.21~v1.7.24 已从 agent 引导层（SOUL §2 第 10 条）+ 能力层（execute_code:run 的 RunUsings 扩至 10 命名空间 + SerializedProperty 嵌套 propertyPath 可见性）+ 存量代码层（manage_gameobject 删 sort_children/arrange_grid、workflow 15→3 action）三个方向执行了「通用能力优先于特化堆砌」原则。SmartOperationsTool 是最后一个仍以纯几何/纯空间批量特化 action 堆砌的工具。审计其 7 个 action：

- `align_objects`：LINQ `values.Min()/Max()/Average()` + foreach 赋值 → execute_code:run 5-8 行
- `distribute_objects`：等距间隔 + foreach 赋值 → execute_code:run 8-10 行
- `snap_to_grid`：`Mathf.Round(x / g) * g` → execute_code:run 3 行
- `align_to_ground`：`Physics.Raycast(origin, Vector3.down, out hit, Mathf.Infinity)` → execute_code:run 5 行
- `randomize_transform`：`UnityEngine.Random.Range` 三段 → execute_code:run 8 行
- `select_by_criteria`：**与 `find_gameobjects` 工具的 duplicate 功能** —— find_gameobjects schema 已有 `searchTerm/tag/layer/componentType/activeOnly` 五维过滤，完全覆盖 `name_contains/tag/layer/component` 四维；仅 `static_only` 差一维，用 `execute_code:run` 一行 `.Where(g => g.isStatic)` 补
- `replace_objects`：**保留** —— `PrefabUtility.InstantiatePrefab` + 保 `transform/parent/sibling index` + `Undo group`，agent 用 execute_code:run 现拼 15+ 行且易漏 undo/sibling index

### Removed
- **6 个 smart_operations action** 及各自 handler / 私有 helper：
  - `align_objects` / `HandleAlignObjects`
  - `distribute_objects` / `HandleDistributeObjects`
  - `snap_to_grid` / `HandleSnapToGrid`
  - `align_to_ground` / `HandleAlignToGround`
  - `randomize_transform` / `HandleRandomizeTransform`
  - `select_by_criteria` / `HandleSelectByCriteria`（重复 find_gameobjects）
- **3 个私有 helper**（replace_objects 不用）：`GetAxisValue` / `CalculateAlignTarget` / `GetGameObjectPath`
- **12 个 schema 字段**（仅供已删 action 用）：`axis` / `mode` / `grid_size` / `offset` / `layer_mask` / `position_range` / `rotation_range` / `scale_range` / `component` / `tag` / `layer` / `name_contains` / `static_only` / 参数字段 `name`

### Changed
- **`smart_operations` action 集**：7 → 1（`replace_objects`）
- **schema 属性字段**：15 → 3（`action` / `names` / `prefab_path`），`required` 三项都必填
- **default case 兜底提示**：主动列出已删除的 6 个 action + 各自替代方案（execute_code:run 具体 C# 片段 / find_gameobjects 工具）
- **工具级 Description**：改为聚焦式描述，明确告知 select_by_criteria 已被 find_gameobjects 覆盖、其他 5 个 action 应用 execute_code:run
- **类顶 XML doc**：完整列出每个删除 action 的 execute_code:run 替代代码片段

### Kept (justification)
- `replace_objects`：`PrefabUtility.InstantiatePrefab` + 保 `position/rotation/localScale/parent/siblingIndex` + `Undo.SetCurrentGroupName` + `Undo.RegisterCreatedObjectUndo` + `Undo.DestroyObjectImmediate` + `Undo.CollapseUndoOperations`。这套「原地换 prefab 且完整保留场景关系 + 单一 Undo 组」的组合，agent 用 execute_code:run 现拼易漏 undo 环节，是真正的高频语义化工程操作

### Files Modified
- `Editor/Tools/Native/Extended/SmartOperationsTool.cs`（-18276 字符 / -396 行，585 行 → 189 行 / 27524 字节 → 8650 字节，-68%）

### Verification (static)
- 栈式括号平衡（排除 verbatim string / block-comment / line-comment）：depth=0
- 保留 helper `ResolveGameObjects` 引用完整（1 定义 + 1 调用 = 2 命中）
- 保留 handler `HandleReplaceObjects` 引用完整（1 定义 + 1 dispatcher case = 2 命中）
- 删除的 6 个 handler 名 + 2 个 helper 名（`GetAxisValue` / `CalculateAlignTarget`）全项目 .cs 源码残留 0
- `GetGameObjectPath` 名字在其他工具类中有同名 private 方法（`WorkflowTool.cs` 保留的 helper），不冲突

### v1.7.21~v1.7.25 五连弹总结
| 版本 | 层次 | 动作 |
| --- | --- | --- |
| v1.7.21 | Agent 引导 + 能力 | SOUL.md §2 新增第 10 条 execute_code:run 使用指引；ExecuteCodeTool RunUsings 5→10 命名空间；新增可用命名空间提示常量 |
| v1.7.22 | 能力 | ManageComponentTool: SerializeComponentDetailed 递归展开嵌套 SerializedProperty，key 用 propertyPath；HandleModify FindProperty null 时提示先 get 看嵌套字段名 |
| v1.7.23 | 存量代码 | ManageGameObjectTool: 删 sort_children + arrange_grid（-138 行） |
| v1.7.24 | 存量代码 | WorkflowTool: 15→3 action（-744 行 / -60%） |
| v1.7.25 | 存量代码 | SmartOperationsTool: 7→1 action（-396 行 / -68%） |
| 累计 | — | 工具内 action 精简 20 项 + 内部 helper 12 项；代码 -1278 行；工具总数仍 51 |

## [1.7.24] - 2026-07-21

### Context
用户在 v1.7.20 附近对 workflow 工具堆砌批量 action 明显不满 —— "一个能跑任意 C# 的 execute_code > N 个专用工具"、"用户明确质疑 sort_children 这类特化工具让普适性变窄"（USER 记忆）。v1.7.21 SOUL 引导 + v1.7.22 propertyPath + v1.7.23 sort_children/arrange_grid 删除后，WorkflowTool 剩下的 12 个批量/收集 action 全部可用 execute_code:run 一段 3-8 行 C# 完整覆盖：
- `batch_set_tag`: `foreach (var n in names) FindGameObject(n).tag = "T";`
- `batch_set_layer`: `foreach (var n in names) FindGameObject(n).layer = LayerMask.NameToLayer("L");`
- `batch_set_active`: `foreach (var n in names) FindGameObject(n).SetActive(true);`
- `batch_set_static`: `foreach (var n in names) GameObjectUtility.SetStaticEditorFlags(FindGameObject(n), StaticEditorFlags.BatchingStatic|StaticEditorFlags.NavigationStatic);`
- `collect_by_component`: `Object.FindObjectsByType<Rigidbody>(FindObjectsSortMode.None).Select(c => c.name).ToArray()`
- `collect_by_tag`: `GameObject.FindGameObjectsWithTag("Enemy").Select(g => g.name).ToArray()`
- `collect_by_layer`: `Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None).Where(g => g.layer == LayerMask.NameToLayer("UI")).Select(g => g.name).ToArray()`
- `batch_add_component`: `foreach (var n in names) FindGameObject(n).AddComponent<Rigidbody>();`
- `batch_remove_component`: `foreach (var n in names) Object.DestroyImmediate(FindGameObject(n).GetComponent<Rigidbody>());`
- `batch_move_to_parent`: `var p = FindGameObject("Parent").transform; foreach (var n in names) FindGameObject(n).transform.SetParent(p);`
- `count_objects`: `Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None).Length`
- `list_scenes`: `AssetDatabase.FindAssets("t:Scene").Select(g => AssetDatabase.GUIDToAssetPath(g)).ToArray()`

保留的 3 个 action 各有 execute_code:run 无法优雅表达的独特价值。

### Removed
- **12 个 workflow action** 及各自 handler / 私有 helper：
  - `batch_set_tag` / `HandleBatchSetTag` / `IsValidTag`
  - `batch_set_layer` / `HandleBatchSetLayer` / `ResolveLayer`
  - `batch_set_active` / `HandleBatchSetActive`
  - `batch_set_static` / `HandleBatchSetStatic` / `ResolveStaticFlags` 及其 `#pragma warning disable/restore CS0618`（legacy NavMesh 兼容包裹）
  - `collect_by_component` / `HandleCollectByComponent`
  - `collect_by_tag` / `HandleCollectByTag`
  - `collect_by_layer` / `HandleCollectByLayer`
  - `batch_add_component` / `HandleBatchAddComponent`
  - `batch_remove_component` / `HandleBatchRemoveComponent`
  - `batch_move_to_parent` / `HandleBatchMoveToParent`
  - `count_objects` / `HandleCountObjects`
  - `list_scenes` / `HandleListScenes`
- **schema 6 个仅供已删 action 用的字段**：`tag` / `layer` / `active` / `static_flags` / `component_type` / `parent_name`

### Changed
- **`workflow` action 集**：15 → 3（`batch_rename` / `find_replace_name` / `snapshot_hierarchy`）
- **schema 属性字段**：14 → 10
- **`ReadOnlyActions`**：`{ count_objects, list_scenes, snapshot_hierarchy, collect_by_component, collect_by_layer, collect_by_tag }` → `{ snapshot_hierarchy }`
- **`Capabilities`**：`ModifyScene | ModifyAssets | BatchExecute` → `ModifyScene | BatchExecute`（删除 `ModifyAssets`，剩余三个 action 都不改资产）
- **default case 兜底提示**：主动列出已删除的 12 个 action + 指引改用 `execute_code:run`
- **工具级 Description**：改为聚焦式描述 + 明确告知"其他批量操作请用 execute_code:run"

### Kept (justification)
- `batch_rename`：占位符 pattern 语义 `{index}` / `{name}` / `{parent}` / `{index:00}` 字符串模板 + regex 替换 + 顺序编号，用 execute_code:run 每次现写 15+ 行不划算
- `find_replace_name`：场景全量遍历 + regex 或 plain-text find-replace + dry_run 预览 + include_inactive + search_root 范围限定，是高频语义化操作
- `snapshot_hierarchy`：递归深度限制 + 结构化 JSON 树输出（agent 一次消化整场景结构），比 execute_code:run 现拼 tree 更友好

### Files Modified
- `Editor/Tools/Native/Meta/WorkflowTool.cs`（-29424 字符 / -744 行，1211 行 → 467 行 / 49116 字节 → 19692 字节，-60%）

### Verification (static)
- 栈式括号平衡（排除 verbatim string / block-comment / line-comment 干扰）：depth=0
- 5 个保留 helper（`GetTargetGameObjects` / `GetAllGameObjects` / `CollectAllChildren` / `BuildHierarchyNode` / `GetGameObjectPath`）引用全部命中定义，无 dangling 调用
- 删除的 12 个 handler 名 + 3 个 helper 名（`ResolveLayer` / `IsValidTag` / `ResolveStaticFlags`）全项目 grep 残留 0
- 工具总数仍 51（未删工具本体，仅精简工具内 action 12 项）

## [1.7.23] - 2026-07-21

### Context
用户在 v1.7.16/v1.7.20 期间反复质疑：`sort_children`（v1.7.16 附加）和 `arrange_grid` 是否让工具普适性变窄？v1.7.21 用 SOUL 引导 + v1.7.22 用 propertyPath 修复后，`execute_code:run` 已能在 5-15 行 C# 内完整覆盖这两个 action 的功能：
- 排序: `go.transform.Cast<Transform>().OrderBy(c=>c.name).ToList().ForEach((c,i)=>c.SetSiblingIndex(i));`（LINQ 一行版；含 undo 则 ~5 行）
- 网格: `foreach var i in Enumerable.Range(...) { targets[i].transform.position = ... }`（~8 行）

它们没有 execute_code:run 无法表达的独特语义，属于纯几何/纯排序特化工具堆砌，删除是"通用能力优先"原则的直接落地。

### Removed
- **`manage_gameobject.sort_children` action** 及所有关联代码：
  - `HandleSortChildren` handler 方法（`~L459-507`，49 行）
  - `SortChildrenRecursive` 辅方法（`~L514-542`，29 行）
  - XML doc 块（`~L453-458`，6 行）
  - schema 里 `order` / `recursive` 两个专用参数字段
  - tool `[AgentTool]` Description 里对 sort_children 的引用（无，本身不在描述里）
  - dispatcher `case "sort_children":` 分支（3 行）
  - `Unknown action` 兜底提示的 sort_children token
- **`manage_gameobject.arrange_grid` action** 及所有关联代码：
  - `HandleArrangeGrid` handler 方法（`~L813-858`，46 行）
  - schema 里 `columns` / `spacing` / `start_position` 三个专用参数字段（14 行）
  - tool `[AgentTool]` Description 提到 arrange_grid 的一处引用
  - dispatcher `case "arrange_grid":` 分支（3 行）
  - `Unknown action` 兜底提示的 arrange_grid token

### Changed
- **`manage_gameobject` action 集**：13 → 11（`create` / `delete` / `get_info` / `modify` / `set_transform` / `set_parent` / `duplicate` / `create_batch` / `modify_batch` / `delete_batch` / `set_active_batch`）
- **工具总数**：51 不变（本弹只精简工具**内**的特化 action，不删工具本体）

### Files Modified
- `Editor/Tools/Native/Core/ManageGameObjectTool.cs`（-8153 字符，44526 → 36373 字节）

### Verification (static + cross-file)
- 括号平衡：205 `{` / 205 `}`
- 关键词全项目残留（源码 + 资源 + 除 plans/ CHANGELOG 外全扫）：
  - `sort_children`: 0
  - `SortChildren`: 0
  - `HandleSortChildren`: 0
  - `arrange_grid`: 0
  - `ArrangeGrid`: 0
  - `HandleArrangeGrid`: 0

## [1.7.22] - 2026-07-21

### Context
v1.7.21 补 SOUL 引导后自查发现另一处根因盲点：即便 agent 知道调 `execute_code:run` 或走 `manage_component.modify`，它从 `manage_component.get` 输出里看到的 `serializedProperties` **只有顶层字段名**——嵌套字段（`stats.attack`）、数组元素（`clips.Array.data[0].name`）根本不存在于 JSON 输出里，agent 无法知道能这么改。Unity 官方 `SerializedObject.FindProperty(string)` **本身已支持点分隔路径**（[官方文档](https://docs.unity3d.com/ScriptReference/SerializedObject.FindProperty.html)明确 "You can also use the property path notation"），所以修复只需在 `get` 层暴露真正的 `propertyPath`。

### Changed
- **`ManageComponentTool.SerializeComponentDetailed` 递归展开嵌套 SerializedProperty**：
  - `iterator.NextVisible(false)` → `NextVisible(true)`，第二个 `NextVisible` 也改为 `true` 使遍历真正进入结构/嵌套字段。
  - JSON key 从 `iterator.name`（叶名）改为 `iterator.propertyPath`（完整点分隔路径），agent 从 `get` 输出直接看到 `stats.attack` / `weapons.Array.data[0].damage` 这类可回传的路径。
  - 跳过合成的顶层 `Array` / `size` 包装名（它们的真实元素已用 `Array.data[N]` 路径露出，重复列纯噪声）。
  - `serializedProperties` JObject 附 `_hint` 字段说明 key 是 path notation，可直接回传 `modify` 或 `set_property_batch`。
- **`ManageComponentTool.SetPropertiesViaSerializedObject` FindProperty 未命中错误信息附提示**：从 `"Property 'X' not found on 'C'."` 扩为附上 tip 引导 agent 先跑 `get` 看真实 propertyPath 键，明确 nested 用 `stats.attack`、数组元素用 `fieldName.Array.data[N]` 的语法。

### Files Modified
- `Editor/Tools/Native/Core/ManageComponentTool.cs`
  - `SerializeComponentDetailed`（~L1146-1180）：递归 + propertyPath + Array/size 跳过 + _hint
  - `SetPropertiesViaSerializedObject`（~L753-760）：错误信息附路径语法提示

### Verification (static)
- 括号平衡：305 `{` / 305 `}`
- `iterator.propertyPath` 引用次数：1（新路径）
- `NextVisible(true)` 出现次数：2（原本 1 处顶层入口 + 现在 1 处遍历循环）
- `NextVisible(false)` 残留：0
- `_hint` 输出字段：1 处

## [1.7.21] - 2026-07-21

### Context
用户反馈：agent 在处理"按名字升序排列子物体"的用户请求时，连续四次工具调用失败——先试 `execute_menu_item` 找不到菜单，再试 `execute_code(action="execute")` 参数错误，再试 `execute_code(action="evaluate")` 多行 C# 被评估器拒绝，最终退回创建 Editor 脚本 `.cs` 文件走菜单调用，因 Domain Reload 时序问题连菜单都没注册上。用户随后追问："你刚才是单独专门的写了一个特化的工具吗？这样会不会让工具普适性和适用面变窄？"

这次 v1.7.21 正面回答该质疑：**不加特化工具，改升级通用能力。** agent 撞墙的真正根因不是缺 `sort_children`（该 action v1.7.20 已存在于 `manage_gameobject`），而是 agent 不知道 `execute_code(action="run")` 存在于 v1.7.20 中且已能跑多行 C#——SOUL 从未告诉它。本弹从"引导 + 能力 + 错误自纠"三个层面根因治理。

### Changed
- **SOUL.md §2.10 新增"临时代码优先 execute_code:run"引导条款**：明确一次性批量场景编辑、层级查询、计算类操作、Unity Editor API 探索等应直接调 `execute_code(action="run")` 传一段 C# 代码块并将最终值赋给 `__result` 变量，不要创建一次性 Editor 脚本（`.cs` 文件触发 Domain Reload、需菜单注册、污染项目）。仅当逻辑需长期复用或需要 `execute_code:run` 无法表达的特性（自定义 Attribute、EditorWindow 类、MenuItem）时才用 `manage_script`。条款末尾列出 run 模式可用的完整命名空间清单。原第 10/11 条重编号为 11/12。
- **`ExecuteCodeTool.RunUsings` 扩展从 5 个命名空间到 10 个**：新增 `System.IO` / `System.Text` / `System.Text.RegularExpressions` / `UnityEngine.SceneManagement` / `UnityEditor.SceneManagement`，覆盖高频场景（文件 IO、字符串构造、正则匹配、场景操作）。
- **`ExecuteCodeTool` 对应 `ReferenceAssembly` 注册所在程序集**：新增 5 处 `refAssembly.Invoke` 调用 —— `typeof(System.IO.File).Assembly` / `typeof(System.Text.StringBuilder).Assembly` / `typeof(System.Text.RegularExpressions.Regex).Assembly` / `typeof(UnityEngine.SceneManagement.Scene).Assembly` / `typeof(UnityEditor.SceneManagement.EditorSceneManager).Assembly`，确保 `using` 语句能解析。

### Added
- **`RunAvailableNamespacesHint` 常量 + 错误信息自纠提示**：在 `ExecuteCodeTool` 中定义一个人类可读的命名空间清单常量，包含（1）可用命名空间列表（2）`__result` 用法示例（3）Mono.CSharp 语法限制提示（不支持 async/await / 顶层 return / 部分 C# 8+ 特性如 records / switch expressions / using declarations）。run 模式失败或返回空值的两处错误分支同时把 hint 拼接到人类可读的错误消息里，并注入到 `data["hint"]` 字段——让 agent 撞墙时能自纠而非退回旧路径（创建 `.cs` → 菜单未注册 → 走 `evaluate` 又失败）。

### Files Modified
- `Editor/Bootstrap/Resources/SOUL.md`（+1 条款 / 原第 10/11 条重编号为 11/12）
- `Editor/Tools/Native/Scripting/ExecuteCodeTool.cs`（RunUsings 扩展 + 5 个 ReferenceAssembly 调用 + `RunAvailableNamespacesHint` 常量 + 2 处错误分支拼接）

### Verification (static)
- 括号平衡：110 `{` / 110 `}`
- `RunAvailableNamespacesHint` 引用次数：5（1 处常量声明 + 2 处 `data["hint"]` 赋值 + 2 处 message 拼接）
- 旧 5 命名空间 RunUsings 字符串残留：0

## [1.7.20] - 2026-07-21

### Changed
- **设置界面全英文化**：清理设置界面残留的中文 UI 文本（此前 Log Verbosity 卡片描述、Log Level 下拉 tooltip、以及 `AgentCoreSettings.logLevel` 字段的 `[Tooltip]` 特性混有中文）。完整扫描 Config/Settings 相关文件后统一翻为英文，共 3 处；代码注释里的中文（不显示在界面）保留不动。
- **HelpBubble 使用技巧面板精简**：移除快捷键区中 `Ctrl+Shift+E 导出会话`、`Enter 发送消息`、`Escape 取消操作` 三个条目；面板期望高度 540→620，配合内容减少尽量避免用户滚动（面板高度仍受当前窗口物理高度约束，极小窗口下仍可滚动）。
- **设置界面 Quick Actions 按钮宽度统一**：Quick Actions 卡片内 5 个按钮此前混用 140/150 宽度导致每行不齐，统一为常量 `ButtonWidth = 150f`（取 150 以容纳最长文案 "Refresh Tool Registry"）。

## [1.7.19] - 2026-07-21

### Changed
- **VCS 面板 View Diff 按钮改为呼出外部图形 diff 工具**：Working Copy Status 面板的 View Diff 此前把 diff 文本通过 `AgentCoreLog.Info` 写入 Unity Console —— 该输出受 Settings 日志分级开关影响（调到 Warning/Error 时被吞），且极易被 Console 其他日志淹没忽略。现改为与右键菜单 `Show Working Copy Diff` 一致，直接呼出对应 VCS 软件的图形 diff 功能（有单选文件则 diff 该文件，否则对整个工作副本做 diff），不再写 Console。删除旧的 `ShowDiffAsync` 实现。
- **View Diff 目前仅维护 SVN**：TortoiseSVN 下按钮可用（`TortoiseProc.exe /command:diff`）；Git / Perforce 的图形 diff 集成不做，这两种 VCS 下按钮禁用（`SupportsExternalFileTool("diff")` 判定），避免提供点击后无实际动作的假入口。

## [1.7.18] - 2026-07-21

### Fixed
- **输入框 Shift+Enter 触发全选 / 换行交互异常**：Unity 2022.3 多行 TextField 的内建 `KeyboardTextEditor` 通过 default action 通道处理 Shift+Enter，把它错误地当成全选（连续按会全选→再按替换掉整段文本）。事件拦截（`StopPropagation` / `StopImmediatePropagation`，甚至下沉阶段注册到内部 `unity-text-input` 子元素）都拦不住 default action —— 经 DIAG 日志实证：全选在按键 default action 阶段注入，发生在自定义 handler 执行之后的间隙，无法靠拦截根治。

### Changed
- **输入框换行键位改为 Ctrl+Enter，发送保持 Enter**：不再与内建抢 Enter/Shift+Enter（内建对这两个键的 default action 拦不住），改用内建不响应的 Ctrl+Enter 组合键自己插入换行，彻底绕开全选坑。Enter 仍为发送，且保留 IME 守卫 —— 中日韩输入法组字未提交时按 Enter 是"确认选词/上屏"，此时不发送，让候选内容上屏到输入框；组字提交后再按 Enter 才真正发送。换行插入用 `SetValueWithoutNotify` + 光标钉位（立即 + 延迟一帧两次），避免 value setter/Focus 引发的意外全选。HelpBubble 快捷键提示同步更新。

## [1.7.17] - 2026-07-21

### Fixed
- **GameObject 工具链无法操作 inactive 对象**：`ToolHelpers.FindGameObject` / `FindGameObjectsByName` 及 `SmartOperationsTool` 的场景对象查找使用 `FindObjectsByType<GameObject>(FindObjectsSortMode.None)`，该重载在 Unity 2022.3 默认 `FindObjectsInactive.Exclude`，导致 inactive 对象查不到。后果：用 `set_active_batch` 等工具禁用一个对象后，再也无法用同一工具链（`get_info` / `modify` / `set_active_batch` / `manage_component` / `smart_operations`）重新找到并启用它——启用能力闭环缺口。修复：三处查找统一补 `FindObjectsInactive.Include`。对照组 `FindGameObjectsTool`（`activeOnly:false` 时已正确传 Include）不受影响。注：`SceneAnalysisTool` / `OptimizationTool` 中按 `Renderer`/`Light` 统计渲染对象的查找保持默认 Exclude（统计活跃渲染物是正确语义，未改）。

## [1.7.16] - 2026-07-21

### Changed
- **工具确认流程重构（风险分级 + 能力位联合决策）**：确认面板"弹不弹"的判定从单一破坏性标志升级为 `ToolRiskLevel`（风险分级）× `ToolCapability`（能力位）联合决策，并以 action token 作兜底。修复了多 action 混合工具的能力位"连坐"问题——同一工具内只读 action（如 `manage_gameobject:get_info`）不再因该工具含破坏性 action 而被误判为需确认，判定粒度落到 **action 级**而非工具级。涉及 `ToolRiskPolicy`、`AgentToolAttribute`、`IAgentTool`、`ChatWindow.Confirmation` 及全部原生工具的 action 白名单标注。
- **VCS 更新操作不再弹系统确认框**：`VersionControlPanel` 内点击 Update 不再弹出"很像报错"的 `EditorUtility.DisplayDialog` 确认框，直接打开对应外部 VCS 窗口（TortoiseSVN / Git GUI / P4V）。理由：操作已收敛在 VCS 页面内，用户在此触发即代表明确知晓在操作 VCS。

### Added
- **set_selection 多选增强**：`manage_editor:set_selection` 支持多目标选择、InstanceID 定位、同名对象全选，参数兼容数组 / 逗号字符串 / 字符串化 JSON 三种形态。新增 `ToolHelpers.FindGameObjectsByName`。
- **Hub 左侧 VCS 导航按钮动态化**：新增通用 `HubNavBadgeBus`（导航角标事件总线，保留每模块最新状态供晚开窗口拉取）+ `HubRail.SetModuleLabel` / `SetModuleAlert` 通用方法。VCS 侧新增 `VcsHubNavBadgeDriver`（`InitializeOnLoad`）：按检测到的 VCS 类型把导航按钮标签动态改为 **SVN / GIT / P4**（未检测到保留默认 "VCS"）；订阅 `VcsRemoteStatusMonitor.StatusChanged`，远端有更新时按钮**变黄高亮**（`.hub-rail__button--alert`），无更新时恢复。架构上 VCS asmdef 单向依赖主 Editor，经通用总线通信，无循环依赖，其他模块可复用同一角标机制。

### Fixed
- **set_active_batch per-item `active` 被忽略**：`manage_gameobject:set_active_batch` 传 `items:[{target, active}]` 时，handler 只读顶层 `parameters["active"]` → 为 null → 回退默认 `true`，导致 `active:false` 的对象未被禁用（静默给错结果）。修复：改为逐 item 读取 `active`（缺省回退顶层 active，再回退 true），summary 改为计数式 `N succeeded, M failed`，不再假设单一 active 值。经全量排查，其余 24 个 batch handler 无同类隐患（范式 A per-item 提取正确 / 范式 B 顶层统一操作值是设计意图）。

### Removed
- **SceneView 顶部 VCS 黄色警示条**：删除 `VcsSceneViewUpdateBanner`（原在场景视图顶部绘制琥珀色更新提醒条 + 6 处 `EditorUtility.DisplayDialog`）。远端更新提示改由左侧 VCS 导航按钮变黄承担，不再遮挡场景视图。

## [1.7.15] - 2026-07-20

### Fixed
- **工具确认面板不弹（信任 scope 无感知残留）**：破坏性工具（如 `manage_gameobject:create`）被策略引擎正确判为 `RequireConfirmation`，却直接执行、不弹确认面板。根因是会话级信任 scope（YOLO / Trust Low-Med）残留——用户先前测试时点过 `YOLO (All)` 或 `Trust Low/Med`，该状态经 `SessionState` 持久化后，**新建对话 session 时未被清除**，导致 `IsToolConfirmationTrusted` 直接返回 true 跳过面板。策略判定链（`ToolRiskPolicy` → `ExtractActionFromParameters` → `RequiresDestructiveActionConfirmation`）全程正确，缺陷仅在信任生命周期。修复：`OnNewSessionClicked` 与 `SwitchToSession` 两处切换对话入口新增 `ClearPendingToolConfirmations()`，把工具信任生命周期**绑定到对话 session**——新建/切换对话即失效。Domain Reload（脚本重编译）仍保留信任（`SessionState` 持久化不变），Editor 完全重启自然归零。诊断用 `[DIAG-POLICY]` 临时日志已移除。

## [1.7.14] - 2026-07-20

### Added
- **ask_user 中途提问工具（挂起-唤醒完整态）**：Agent 遇实现方向岔路（多方案权衡 / 需求歧义 / 继续须假设）时主动调 `ask_user` 提问，独立选项面板阻断（无超时·不自动拒绝·永久等待），loop 检测 `ToolResult.IsAwaitingUserInput` 标志 → `SetState(WaitingForUserInput)` → 截断退出循环不空等；用户点选项或「我自己描述」自由文本应答后，`ResumeFromUserInput` 追加 user 消息 + `TriggerResumeLLMCall` 唤醒 LLM 继续。挂起状态存 `DomainReloadState`（3 字段）跨 domain reload 存活，reload 后 `TryRestorePendingAskUser` 重建面板。新增 `AgentLoop.AskUser.cs` + `ChatWindow.AskUser.cs` + `AskUserTool.cs`（纯函数化，自动发现注册）；`ToolResult` 加 `IsAwaitingUserInput` / `AskUserQuestion` / `AskUserOptions` 字段。复刻 WaitingCompilation 范式，完全不碰 SelfChallenge。SOUL §2 加中途收束方向引导。

### Fixed
- **助手气泡宽度被首段短内容压窄**：`AssistantTurnView` 6 个 column wrapper 容器设 `width: 100%`，每个 turn 恒定撑满对话区可用宽度，消除首段短文本气泡把后续工具卡片/长内容压窄的问题。

## [1.7.13] - 2026-07-20

### Added
- **会话自动重命名**：会话列表右键菜单新增「自动重命名」项（排在「重命名」上方），基于会话**最近 12 条上下文**调用 LLM 生成能反映当前主话题的简短标题，解决"话题漂移后标题仍停留在最初内容"的问题。复用统一 LLM 管道（`SessionAutoTitleService`，非流式、低 token 预算、≤24 字中文标题）；生成期间标题显示"正在生成标题…"，失败恢复原标题。

### Fixed
- **助手气泡宽度被首段短内容压窄**：`AssistantTurnView` 及其内部 6 个 column wrapper 容器（turn 自身 / roundsContainer / selfChallengeSlot / bubbleSlot / sectionContainer / ToolSlot）未设宽度，导致整个 turn 宽度被首段渲染内容锁成 min-content——第一段是短文本气泡时，后续工具卡片/长内容被压窄。修复：6 个 wrapper 全部设 `width: 100%`，每个 turn 恒定撑满对话区可用宽度；MessageBubble 本身不变（保持 max-width:82% + align-self:flex-start 的内容自然伸缩）。

### Docs
- **ROADMAP 同步实现现状**：§7 表头刷新至 v1.7.13；自演化知识系统 Tier 0/1/2 从"待开发"标记为已完成；新增 **ADR-19** 记录 Tier 1 由「静态拆分文档 + 手动 re-init」演进为「实时静默收集 + 缓存注入」的决策。

## [1.7.12] - 2026-07-20

### Changed
- **日志分级全量贯彻**：将 83 个文件中 329 处裸 `Debug.*` 调用统一迁移到受分级控制的 `AgentCoreLog.*`（`Debug.LogWarning`→`AgentCoreLog.Warning` 245 处 / `Debug.LogError`→`AgentCoreLog.Error` 74 处 / 低频 `Debug.Log`→`AgentCoreLog.Info` 10 处），并为 62 个文件补 `using AgentCore.Editor.Utils;`。此前这些调用绕过 Settings 的 Log Verbosity 开关永远输出，导致用户即便调到 Error/Silent 仍被 245 条 Warning 刷屏；现全部日志真正流经分级系统，开关名副其实。日志内容与格式不变，仅改调用通道。

## [1.7.11] - 2026-07-18

### Fixed
- **HubRail 图标视觉不一致**：v1.7.10 图标化后 chat 有图标、VCS 等无图标，视觉割裂。现仅 Settings 保留齿轮图标，chat/vcs 等所有模块统一回退文字标签。
- **对话文本 □ 方块**：LLM 流式输出偶发混入无法渲染的异常字符（孤立代理项、Unicode noncharacter、控制字符），TMP 字体替换成 □。在 StreamingTextElement 的 AppendText / SetFinalText 入口新增 SanitizeForDisplay 清洗，保守剔除"一定渲染不了"的字符，正常中文/emoji/符号完全不受影响。
- **ManageComponentTool 无法赋值 ObjectReference 字段**：修改组件时对象引用类型属性报 `unsupported property type 'ObjectReference'`。现支持通过 instanceId / 资源路径 / 场景对象名 / null 赋值，并自动做 GameObject↔Component 类型转换。

## [1.7.10] - 2026-07-18

### Fixed
- **MessageReferenceBar chip 截断**：引用栏 `[File]`/`[GO]` 可点击按钮被截成一半高。根因是上一版修引用溢出时给 chip 设了固定 `height:22` + `overflow:hidden` + 上下 `padding:0`，11px 字体的 Button 行高（含上下 border）超过 22px 内容区被垂直裁切。改为 `minHeight:22`（保底可点击尺寸）+ 高度由内容自然撑开 + 上下 `padding:2`；`overflow:hidden` 保留用于水平方向长路径省略号截断。

### Added
- **HelpBubble 快捷键补齐**：帮助面板"快捷键"区此前只列 3 个全局键（Ctrl+Shift+Q/X/E），遗漏输入框内高频快捷键，用户误以为不存在。补上 Enter（发送）/ Shift+Enter（换行）/ Ctrl+N（新建会话）/ Escape（取消当前操作）。
- **HubRail 图标化**：左侧窄栏（52px）模块按钮 + Settings 按钮从文字标签改为 Unity 内置编辑器图标（`ResolveBuiltinIconName` 映射已知模块 ID → 内置图标名，`TryApplyIcon` 应用）。健壮回退：图标名在当前 Unity 版本不存在 / 取到空贴图 / API 异常时静默保留原文字标签，未知/第三方扩展模块也保留文字，绝不出现空按钮。

### Changed
- **虚拟列表加载更多滚动补偿**：`MessageListManager.LoadMoreItems` 向容器顶部插入旧消息会把用户正在看的内容整体下推造成视觉跳动。现记录插入前内容高度 + 滚动值，插入后下一帧（`schedule.Execute`）把高度增量补偿到 `scroller.value`，使视口锚定内容保持原位。

### Notes
- UI 交互视觉审查 P3 项经逐一核实判定**无需改动**：ScrollToBottom 双帧延迟是"等 DOM 布局稳定"的务实方案（改动风险大于收益）；error 气泡 `align-self:stretch` 满宽是有意设计（错误信息醒目打断对话流，非对齐 bug）；键盘可访问性属增强非缺陷。

## [1.7.9] - 2026-07-17

### Added
- `AgentCoreColors.cs` — UI 统一色板 C# 单一真源（成功绿 #5cb85c / 危险红 #d9534f / 主题蓝 #4a86c8 / 警告黄 #f0ad4e 等）。此前语义色散落在 USS 与多个组件的 C# 硬编码里，同一"成功绿"曾有三个不同值（USS #5cb85c、ToolCallCard #4CAF50、ContextUsagePanel Color(0.2,0.8,0.3)）。

### Fixed — UI 交互与视觉审查（P0/P1）
- **P0 IME Enter 误发送** — 中文/日文/韩文输入法在候选框按 Enter 是"确认选词"，此前会被误判为发送消息，导致半句话被发出。新增 `IsImeComposing()`（基于 `Input.compositionString`）守卫，组字期间的 Enter 不再触发发送。
- **P0 GetContextBudget 高频遍历** — `UpdateContextUsagePanel()` 此前挂在每个 AgentEvent（含 StreamToken/ReasoningToken 高频事件）上，且 `GetContextBudget()` 每次都遍历整个消息历史估算 token。流式输出时形成 O(N token × M 消息) 无谓重算。改为仅在真正影响预算的事件（AssistantMessage/StateChanged/ToolCallCompleted/ToolCallFailed/Error）后刷新。
- **P1 流式视觉跳变（方案C）** — 此前流式阶段显示纯文本（表格是 `|a|b|`、代码块是裸文本），最终化瞬间切换 block 富渲染造成布局跳变。改为流式与最终化统一走 block 渲染路径（`RenderTextAsBlocks`），代码块/表格在流式阶段即为深色框/网格；新增 `CloseDanglingCodeFence` 补齐未闭合代码块使其能在流式期显示。仍保留 4000 字符尾部窗口 + 16ms 节流控制 DOM 规模。
- **P1 ToolCallCard 超长结果性能** — 只读 multiline TextField 非虚拟化，承载数万字符的工具结果（读大文件/大 JSON）会在展开时卡顿。显示截断到 8000 字符并追加提示，完整原文保留在 `_detailsRaw` 供"复制"按钮取用。
- **P1 色板统一** — ToolCallCard / ContextUsagePanel 的硬编码语义色统一引用 `AgentCoreColors`；ChatWindow.uss 中的强调蓝（原 #4A90D9）统一为 #4a86c8。有意的层次色（用户气泡填充蓝 #3a5f8a、error 气泡明暗红 #ff7777/#e05555）明确保留并注释说明。

### Fixed — HelpBubble & 对话泡泡（前序修复，随本版一并提交）
- HelpBubble 帮助浮窗背景透明/字体不继承 — 根因是面板挂到了 USS 作用域外的 `unity-panel-container`。新增 `ResolveMountRoot()` 定位持有 styleSheets 的 rootVisualElement，所有视觉属性 inline 兜底，补回 `EnsureCJKFont()`。
- 对话泡泡底部引用区溢出 — 引用栏容器加宽度约束（flexShrink/maxWidth/overflow），chip 改 NoWrap+Ellipsis。
- 窗口缩至极限宽度致 Unity 无响应 — 根因是 `SyncBubbleContentHeight` 在 GeometryChangedEvent 里反向写 minHeight 形成正反馈死循环。加防重入 flag + 8px 容差 + 比较上次写入目标值三重防护；MinWindowSize 宽度 360→420 作纵深防御。

## [1.7.8] - 2026-07-15

### Fixed
- DomainReloadState.cs 缺少 `using AgentCore.Editor.Utils` 导致 CS0103 编译错误（v1.7.7 打包遗漏）

## [1.7.7] - 2026-07-15

### Fixed — Preferences 目录 Move 弹窗根因修复（v1.7.1~v1.7.6 的第五层）

v1.7.6 仍复现该 bug。用户测试发现：报错时目录已存在，手动删除后点 Cancel，Unity 能顺利重建。根因有三：

1. **`_cachedDirEnsured` 缓存导致失效后不重建** — 首次创建成功后缓存为 `true`，后续调用直接返回 `true` 不检查 `Directory.Exists()`。目录被任何外部因素删除后（Unity 清理、杀毒软件、用户操作），`SafeSave()` 不再重建目录，`Save()` 的 Move 操作目标路径不存在 → 弹窗。修复：删除缓存，每次都检查 `Directory.Exists()`。

2. **`ResolveByDirectoryScan()` 可能选错 `Editor-*.x`** — 用户装多个 Unity 版本时（如 2021 + Unity 6），扫描选"最近修改"的目录可能选到错误版本。目录创建在 `Editor-6.x`，Unity 实际保存到 `Editor-5.x` → 路径不匹配。修复：优先选当前 Unity 版本对应的 `Editor-*.x`，找不到才回退到最近修改。

3. **`SafeSave()` 忽略 `EnsureAgentCoreDirectory()` 返回值** — 三处 `SafeSave()`（AgentCoreSettings / DomainReloadState / IndexingSettings）不检查返回值，目录创建失败仍继续 `Save()`。修复：检查返回值，失败则跳过保存并记录警告。

4. 新增 `EditorApplication.delayCall` 在首帧后再次确保目录——覆盖 assembly load 到首次 ScriptableSingleton save 之间的时序间隙。

5. `IndexingSettings.cs` 补充 `using AgentCore.Editor.Utils;`（新增 `AgentCoreLog` 引用需要）。

## [1.7.6] - 2026-07-15

### Fixed — v1.7.5 编译错误修复

- v1.7.5 重写 `PreferencesFolderPathHelper.cs` 时遗漏 `using AgentCore.Editor.Utils;`，导致 `AgentCoreLog` 无法解析（CS0103）
- 此修复不影响 v1.7.4~v1.7.5 的功能逻辑，仅补全缺失的 using 指令

## [1.7.5] - 2026-07-15

### Fixed — Preferences 路径三级兜底 + ScriptingDefineSymbols 版本兼容

- 新增 `ResolveByHardcodedFallback()`：当反射和目录扫描均失败时（极端新装场景），按 Unity 内部版本号规则回退（Unity 5-2022 → `Editor-5.x`，Unity 6+ → `Editor-6.x`）
- 新增 `ScriptingDefineHelper`：集中封装 `GetScriptingDefineSymbolsForGroup` / `SetScriptingDefineSymbolsForGroup` 的版本兼容切换
  - Unity 2023.1+ 标记旧 `ForGroup` API 为 `[Obsolete]`，Unity 6000.5 已确认生成废弃警告，未来版本可能移除
  - `#if UNITY_2023_1_OR_NEWER` 分支使用 `NamedBuildTarget.FromBuildTargetGroup` + 新 API
  - 旧分支保留 `ForGroup` API 兼容 Unity 2021.3-2022.3
  - 所有 4 个调用点（OptionalComponentManager × 2 + ReadConsoleTool × 2）统一走 helper

## [1.7.4] - 2026-07-15

### Fixed — Preferences 目录路径解析根因修复（v1.7.3 的补丁）

- v1.7.3 的 `beforeAssemblyReload` 回调只能保护新代码加载后的 Domain Reload，无法修复路径计算错误
- **根因**：Unity 内部 preferences 目录用内部版本号命名（Unity 2021 = `Editor-5.x`），不是营销版本号。旧 fallback 用 `Application.unityVersion` 提取营销版本号（`2021`），算出 `Editor-2021.x`，与 Unity 实际路径 `Editor-5.x` 不匹配，导致目录创建在错误位置
- **修复**：重写 `PreferencesFolderPathHelper`，三级路径解析：
  1. **反射**（primary）— 扩展为尝试 property + method，多个候选名称（`unityPreferencesFolder` / `preferencesFolder` / `GetPreferencesFolder`），覆盖不同 Unity 版本的 API 差异
  2. **目录扫描**（fallback）— 扫描 `%APPDATA%/Unity/` 下已有的 `Editor-*.x` 目录，取最近修改的，不再猜版本号
  3. 如果都没有，返回空并 warning
- 删除 `ExtractMajorVersion` 和 `BuildFallbackPreferencesFolder`（根本性错误的设计）
- 新增诊断日志：Info 级别记录最终解析路径和解析方式（reflection / scan），方便排障

## [1.7.3] - 2026-07-15

### Fixed — 老项目升级安装时 Preferences 目录 Move 弹窗

- `PreferencesFolderPathHelper` 新增 `AssemblyReloadEvents.beforeAssemblyReload` 回调，在 Domain Unload **开始**时重置缓存并重新确保 `AgentCore/` 目录存在
- 修复根因：v1.7.1 的 `[InitializeOnLoad]` 静态构造函数在 assembly load 时创建目录，但**老项目升级**场景下，旧版 AgentCore（无此 helper）遗留的 pending `ScriptableSingleton` auto-save 在 Domain Unload 尾声触发 `Move temp → target`，此时目录可能尚不存在 → Unity 弹 "Moving file failed" 弹窗
- `beforeAssemblyReload` 回调在 Unity auto-save **之前**执行，确保目录已创建，覆盖升级场景的一次性竞态

## [1.7.2] - 2026-07-15

### Documentation — minimalism / self-challenge 历史文档整理归档

- 无代码变化（Editor/ 下所有 `.cs` 均维持 1.7.1 状态）
- 文档层清理：`plans/adr-17-minimalism.md` 决策记录保留在活跃 plans/；`_archive/analysis/minimalism-audit-report.md` 与 `_archive/features/self-challenge-implementation-report.md` 作为历史依据固化在归档目录
- `plans/README.md` 归档索引条目更新，`plans/ROADMAP.md` / `AGENTS.md` / `README.md` 版本号引用同步

> 说明：本版本仅整理文档 + 归档旧 tarball，SemVer patch 递增用于产物版本对齐；没有 API / 行为变化，可从 1.7.1 直接覆盖升级，无需迁移。

## [1.7.1] - 2026-07-15

### Fixed — 新装用户路径不存在导致 Editor 卡死

- `PreferencesFolderPathHelper` 加 `[InitializeOnLoad]` + 静态构造函数，assembly 加载时立即创建 `%APPDATA%/Unity/Editor-x.x/Preferences/AgentCore/` 目录
- 修复根因：此前 `EnsureAgentCoreDirectory()` 只在 `SafeSave()` 内调用，被两层 `delayCall` 延迟；Unity 内部 `ScriptableSingleton` auto-save 在目录不存在时触发 `Move temp → target` 失败，导致"系统找不到指定的路径" + Editor 卡死

### Fixed — VCS 远端状态检查在打开项目时立即触发

- `VcsRemoteStatusMonitor._lastCheckedUtc` 从 `DateTime.MinValue` 改为 `DateTime.UtcNow`
- 修复根因：MinValue 距今 ~2000 年，首个 `EditorApplication.update` tick 即通过 15 分钟间隔检查，导致打开项目时立即执行远端查询 + SceneView 横幅出现

### Fixed — 3 个 CS0162 编译警告 + SessionStorage 日志降级

- 删除 3 个 `const true` 死守卫（`SceneViewUpdateBannerEnabled` / `PeriodicRemoteStatusCheckEnabled` / `AutoRefreshCommitListEnabled`）及其对应 const 声明
- `SessionStorage.Load` 的 "Session file not found" 从 `LogWarning` 降级为 `AgentCoreLog.Info`（新装无历史 session 是正常状态）

## [1.7.0] - 2026-07-14

### Changed — 统一 LLM 管道 + Settings 极简化重构

#### 统一 LLM 调用管道 (6588bb1)
- 删除 `CompressionLLMClient`，所有 LLM 调用（主循环/压缩/SelfChallenge）统一走 `OpenAICompatibleClient` → `RequestEnrichment`
- 消除管道碎片化，减少维护面

#### GLM-5.2 Reasoning 适配 (e37f5bc)
- 修复 GLM-5.2 native reasoning 吃满 `maxTokens` 导致内容为空
- `GetEffectiveMaxTokens()` = `maxTokens` + `reasoningMaxTokens`，reasoning 预算独立计算
- `enableReasoningOutput` + `reasoningEffort="low"` + `reasoningMaxTokens=2048` 作为默认值

#### Settings v20: 死字段清理
- 删除 12 个零引用的 `[HideInInspector]` 字段：`streamingEnabled`、`showToolCallDetails`、`fallbackRoutingEnabled`、`autoCompileCheck`、`autoConsoleCapture`、`maxConsecutiveErrors`、`workspaceAutoDetectEnabled`、`workspaceConfigVersion`、`vcsDefaultEnabled`、`useSeparateCompressionLLM`、`compressionLLMEndpoint`、`compressionLLMModel`
- 删除 `SecureKeyStorage` 中 Compression LLM API Key 的 4 个方法/常量
- `disabledTools` 默认值从 `{"execute_code"}` 改为空列表（指向不存在的工具）
- Settings version 19→20 + migration block
- 净删 75 行代码

#### UI 修复
- Workspace 页面删除 "Auto-Detect on Startup" toggle（用户可见但不控制行为的假开关）
- Model Info 卡片显示 `GetEffectiveMaxTokens()` 实际值而非 `maxTokens`，reasoning 启用时分两行显示 Content/Reasoning 明细
- Dashboard "Clear Secure Keys" 对话框文案移除已删功能的 "Compression LLM" 提及

#### 性能优化 (5045adc, 451fce9, b9b17a0)
- 流式文本窗口 StringBuilder 优化长输出
- ConcurrentQueue 批量主线程回调 + 滚动节流
- 帧节流流式 token UI 更新，消除逐 token relayout

#### UI 修复 (0e91cbd, d34d82b, 117935b)
- 气泡溢出修复：文本/chips 超出边界 + 长输出后空白
- 文件变更面板默认折叠 + 状态行更突出
- ThinkingDrawer 缺失 `using System.Text` 修复

#### 其他
- `4038f35` 防护压缩请求超出 context window
- `a940e52` 移除流式空内容重试，避免重复 reasoning 输出
- `1550493` SOUL.md 重构：补充一致性与诚实原则章节

## [1.6.5] - 2026-07-13

### Added — 日志分级基础设施 (LogLevel + AgentCoreLog)

**用户诉求**:回复阶段 [AgentCore] 前缀日志狂刷导致 Editor 卡顿 (千次级/回复)。

**变更内容**:

#### 新增 [`AgentCoreLog`](Editor/Utils/AgentCoreLog.cs) 静态封装
- `LogLevel` 枚举 5 档: `Silent` / `Error` / `Warning` / `Info` / `Debug`
- API: `AgentCoreLog.Debug/Info/Warning/Error(msg)` + `Error(msg, ex)`
- 首次访问时从 `AgentCoreSettings.instance.logLevel` 读取并缓存,通过 `Invalidate()` 支持热切换
- `RefreshCache` 使用 try/catch 兜底防 bootstrap 阶段崩溃

#### AgentCoreSettings 新增 `logLevel` 字段
- 默认 `LogLevel.Info` (关键业务事件默认可见,不含高频细节)
- 位于 [`AgentCoreSettings.cs`](Editor/Config/AgentCoreSettings.cs) `compressionEnabled` 之后

#### DashboardSettingsPage 新增 "Log Verbosity" 卡片
- 下拉菜单 (EnumPopup) 供用户选择级别
- 切换后立即调用 `AgentCoreLog.Invalidate()` 生效
- 使用 `UnityEngine.Debug.Log` (不走 AgentCoreLog) 打印切换通知,避免死循环

#### 迁移策略 (分类)
- **Debug 级** (30 处): 高频热点 — 每 token/event/chunk
  - `AgentLoop.Events.cs` State 切换 (每状态一次)
  - `AgentLoop.LLM.cs` `Stream completed` / `Received ToolCallDelta` (逐 chunk)
  - `OpenAICompatibleClient.cs` LLM request/usage/repair (每次 API 调用)
  - `Mem0Client.cs` 全部 API 详情日志 (11 处)
  - `ChatWindow.Events.cs` HandleAgentEvent (逐 event,千次级)
  - `ChatWindow.Tools.cs` tool card 生命周期钩子 (6 处)
  - `ChatWindow.Messages.cs` RebuildMessageBubbles 循环内详情 (2 处)
  - `ChatWindow.SelfChallenge.cs` 每 SelfChallenge 事件
- **Info 级** (135 处): 会话/turn/session 级事件 — 通过 PowerShell 批处理迁移
  - 保留原样默认可见,不影响卡顿
  - 涵盖 bootstrap、tool registry、session lifecycle、compression 结果、domain reload recovery、compilation status 等
- **例外**: [`AgentCoreSettings.cs`](Editor/Config/AgentCoreSettings.cs) 4 处 Migrate/Bootstrap 日志改回原生 `UnityEngine.Debug.Log`,避免 static ctor 期访问 `AgentCoreLog.RefreshCache` 反向依赖 Settings 造成 bootstrap 循环

### 影响面 & 破坏性

- **性能**: 默认 Info 级别下,Debug 级 30 处热点被完全跳过 (仅 `CurrentLevel >= LogLevel.Debug` 判断,零字符串拼接),解决"回复阶段狂刷 log 卡顿"
- **UX**: 用户可在 `Project Settings > AgentCore > Dashboard > Log Verbosity` 切换,Debug 模式仍可看到全部日志用于问题定位
- **兼容**: 除 `AgentCoreSettings.cs` bootstrap 保留原生 Debug.Log 外,插件内其他 165 处 `Debug.Log` 全部迁移至 `AgentCoreLog.Info/Debug`
- **保留**: `Debug.LogWarning` / `Debug.LogError` 未迁移 (它们是重要提示,不会造成刷屏)

### 测试重点

- [ ] 默认 Info 级:回复过程无 `State:`、`HandleAgentEvent`、`ToolCallDelta`、`LLM request` 日志
- [ ] 切到 Debug 级:上述日志全部可见
- [ ] 切到 Warning 级:仅看到 `Debug.LogWarning/LogError` (相当于 v1.6.4 之前的默认行为)
- [ ] 切到 Silent 级:Console 完全静默 (慎用)
- [ ] `Log Verbosity` 卡片切换后立即生效,无需重启

---

### Changed — 工具确认信任语义重构:引入 YOLO 模式(破坏性变更)

**用户诉求**:原有"Trust Session"按钮语义模糊 — 用户误以为是"本会话所有风险操作放行",实际是"精确目标 + 同工具 + 同 action + 同 risk 才复用"。用户明确希望改成 Kimi `/yolo` 风格的会话级信任。

**变更内容**:

#### `ToolConfirmationTrustScope` 枚举重构 (破坏性)
- **移除** `Once` — UI 不再提供"仅此一次允许"选项
- **移除** `SessionExactTarget` — 精确目标粒度实用价值低,弃用
- **新增** `SessionLowMediumRisk` = 0 — 本会话内所有 ReadOnly/Low/Medium 风险工具直通
- **新增** `SessionAll` = 1 — 本会话内所有工具无条件直通 (真正 YOLO,含 High/Destructive/External/CodeExecution)

#### 提示卡 UI:3 按钮布局
- `[Deny]` — 拒绝当前调用
- `[Trust Low/Med for Session]` — 蓝色,激活 `SessionLowMediumRisk` 信任
- `[YOLO (All)]` — 暖橙色警示,激活 `SessionAll` 信任
- 不再有 `Approve Once` — 任何非 Deny 均建立会话级信任
- 每个按钮均带 tooltip 提示覆盖范围

#### 信任判定改为 scope-based
- [`ChatWindow.Confirmation.cs`](Editor/UI/ChatWindow.Confirmation.cs): `_trustedToolConfirmations` (HashSet<string>) → `_sessionTrustScopes` (HashSet<ToolConfirmationTrustScope>)
- 删除 `BuildTrustKey` / `NormalizeTrustPart` / `CanTrustForSession` 内部方法
- 新增 `IsLowOrMediumRisk` / `IsScopeAllowed` 判定逻辑
- `IsToolConfirmationTrusted`:先检查 `SessionAll` (YOLO 全放行),再检查 `SessionLowMediumRisk` + `ToolRiskLevel` 覆盖

#### 通过 SessionState 持久化 (跨 Domain Reload)
- 用 `UnityEditor.SessionState` 存储信任 scope 集合,避免 Unity 编译脚本/进入 Play Mode 导致的 YOLO 状态丢失
- SessionState 是 Unity 内置"跨 Domain Reload 但不跨 Editor 完全重启"的存储 — 精准匹配"会话级"信任语义
- **初始化时机**:`_sessionTrustScopes` 字段声明为空 `HashSet<>`(纯 CLR,零 Unity API);
  真正的 `LoadSessionTrustScopesFromState()` 由 `CreateGUI` 显式调用(在 `InitializeToolConfirmationPanel` 之前)。
  这是 Unity 硬要求 — ScriptableObject/EditorWindow 的字段初始化器 (等价于构造器上下文)
  严禁调用 `SessionState`/`EditorPrefs`/`AssetDatabase` 等 Unity API,否则抛
  `UnityException: GetString is not allowed to be called from a ScriptableObject constructor`,
  连带导致所有其他字段初始化器 (`_hubPanels`、`_messageBubbles` 等 readonly Dictionary) 未初始化,ChatWindow 完全崩溃。
- `SaveSessionTrustScopes` 在 scope 变更/清空时写入 (加空守卫防半初始化)
- `ClearPendingToolConfirmations` 里 `_sessionTrustScopes?.Clear()` 加空守卫兜底
- 场景:开 YOLO 后修改脚本触发编译 → Reload → 提示卡下次弹出时仍自动直通(不会丢 YOLO 状态)

#### 底层默认值调整
- [`ToolRiskPolicy.cs`](Editor/Tools/Safety/ToolRiskPolicy.cs): `BuildConfirmationRequest` 提供的默认 scopes 从 `[Once, SessionExactTarget]` 改为 `[SessionLowMediumRisk, SessionAll]`
- [`ToolConfirmationRequest.cs`](Editor/Tools/Safety/ToolConfirmationRequest.cs): 构造函数默认 `AllowedTrustScopes` 同步更新

#### 样式补充
- [`ChatWindow.uss`](Editor/UI/ChatWindow.uss): 新增 `.tool-confirmation-button--yolo` (背景 `#b8621b` 暖橙,hover `#d0771f`)
- 保留 `.tool-confirmation-button--approve` 兼容其他潜在调用

### 影响面 & 破坏性

- **API 破坏**:`ToolConfirmationTrustScope.Once` / `SessionExactTarget` 被删除。任何 downstream 代码若引用这两个枚举值将编译失败(经全量搜索,包内无残留引用)。
- **UX 破坏**:用户不再有"允许这一次"选项。首次点击 `Trust Low/Med` 后,本会话内所有低/中风险工具默认放行;点击 `YOLO` 后,所有工具放行。
- **安全权衡**:纯 YOLO 模式**不设 Critical 硬顶**,delete/vcs_push/build_player 等操作也会被直通。这是用户明确要求的设计,用户完全自担风险。建议开 YOLO 前 commit 干净的 VCS 状态。
- **信任状态生命周期**:纯内存 (HashSet),ChatWindow 会话切换/清空时通过 `ClearPendingToolConfirmations` 自动清除;插件重启后状态归零。

### 测试重点

- [ ] 单个 tool call:提示卡显示 3 按钮 (`Deny` / `Trust Low/Med` / `YOLO`),各带 tooltip
- [ ] 点 `Trust Low/Med` 后,同会话内下一个 Medium 风险 tool 不再弹卡
- [ ] 点 `Trust Low/Med` 后,同会话内 High/Destructive 风险 tool **仍**弹卡
- [ ] 点 `YOLO` 后,同会话内任意风险 tool 均直通(含 delete/vcs)
- [ ] 会话清除 / ChatWindow 关闭重开后,信任状态归零,重新弹卡
- [ ] YOLO 按钮的暖橙色视觉与 Deny (红)/Trust (蓝) 明显区分

---

## [1.6.4] - 2026-07-13

### 附加 UI 修复 & D2/D3 实施（本次追加，与 D1 一起发布）

#### UI-1. Thinking Drawer 加独立"展开/折叠"按钮
- [`ThinkingDrawer.cs`](Editor/UI/Components/ThinkingDrawer.cs)：将左侧的静态 Arrow Label 替换为独立 `Button`（`▶ / ▼`）
- 用户明确要求"可视化按钮化"，此按钮响应可靠，不受 header 拖拽选中干扰
- header 空白区依然响应点击（双入口冗余）

#### UI-2. 输入框内容过多可滚动
- [`ChatWindow.uxml`](Editor/UI/ChatWindow.uxml)：`input-field` 用 `input-scroll-view` (ScrollView) 包裹
- [`ChatWindow.uss`](Editor/UI/ChatWindow.uss)：`input-area` max-height 从 140px 上调至 260px；`input-scroll-view` max-height 220px；`input-field` 移除 max-height 让内容撑高由 ScrollView 承载
- 用户注入超长 context 后可通过 ScrollView 上下翻查检查内容
- 未加"清空"按钮：Ctrl+A 全选 + Delete 已足够，避免按钮堆积

#### UI-3. 流式回复时用户可上翻 + "跳到最新"浮动按钮
- [`ChatWindow.uxml`](Editor/UI/ChatWindow.uxml)：`message-scroll-view` 包在 `message-scroll-wrapper` (relative position) 里，右下角浮动 `scroll-to-bottom-button`
- [`ChatWindow.uss`](Editor/UI/ChatWindow.uss)：新增 `#scroll-to-bottom-button` 浮动圆角按钮样式
- [`ChatWindow.Messages.cs`](Editor/UI/ChatWindow.Messages.cs)：
  - `IsScrollAtBottom()` 检测：距底部 40px 内视为"在底部"
  - `CheckUserScrolled()` 主动检测 + 更新按钮可见性
  - `OnMessageScrollValueChanged` + `WheelEvent` 监听用户上翻
  - `ScrollToBottom(force=false)` 只在**未上翻**时自动追底，`force=true` 时**强制**追底 + 重置 `_userScrolledUp`
- [`ChatWindow.Input.cs`](Editor/UI/ChatWindow.Input.cs)：`OnSendClicked` 里 `ScrollToBottom(force: true)`，用户主动发送新消息一定回到底部

#### D2. 消息内引用可点击跳转/点亮（"消息底部资源栏"方案）
- 新增 [`MessageReferenceExtractor`](Editor/UI/Components/MessageReferenceExtractor.cs)：Regex 识别
  - 反引号包裹的 `Assets/**/*.{cs,md,shader,unity,prefab,...}`（可选 `:line` 后缀）
  - `` `hierarchy: A/B/C` `` 格式的 Hierarchy 路径
  - `[GameObject: Name]` 标签
- 新增 [`MessageReferenceBar`](Editor/UI/Components/MessageReferenceBar.cs)：chip 按钮渲染
  - 📄 图标 = 资源；🎮 图标 = GameObject
  - 点击资源 → `AssetDatabase.OpenAsset(obj, line)`
  - 点击 GameObject → `Selection.activeGameObject` + `EditorGUIUtility.PingObject`
  - 找不到目标 → Console warning，不 crash
- [`MessageBubble.cs`](Editor/UI/Components/MessageBubble.cs)：新增 `_referenceBar` 字段，在 `FinalizeContent` / `SetupStaticMode` 完成后 `Rebuild(content)`
- 仅对 assistant 消息生效；无引用时 chip 栏隐藏

#### D3. Play Mode 中禁止写操作（工具层 preflight）
- 新增 [`PlayModePreflight`](Editor/Tools/Safety/PlayModePreflight.cs)：基于 `ToolCapability` 位标志判定 write 类工具
  - Write 位：`WriteProjectFiles / DeleteProjectFiles / ModifyScene / ModifyAssets / ModifyScripts / ExecuteCode / InstallPackages / BuildPlayer / VersionControlWrite / ModifyProjectSettings / ModifyAgentConfig / BatchExecute`
  - Read 类（`ReadProject / NetworkAccess-only`）不受影响
- [`ToolCallDispatcher.cs`](Editor/Tools/ToolCallDispatcher.cs) 在 Schema 校验之后、G.1 policy 之前插入检查
- Play Mode 下 write 工具执行时返回 `ToolResult.Fail("Play Mode 中禁止执行 write 类工具...")`，LLM 会转达给用户
- 无新增设置项；硬规则符合 ADR-17

### 影响面 & 破坏性（追加部分）
- 0 API 破坏；纯 UI + 工具层安全增强
- 新增 4 个文件（`PlayModePreflight` / `MessageReferenceExtractor` / `MessageReferenceBar` / 已有 D1 的 10 个）
- 修改 6 个文件（uxml/uss/ChatWindow.cs/ChatWindow.Messages.cs/ChatWindow.Input.cs/MessageBubble.cs/ThinkingDrawer.cs/ToolCallDispatcher.cs）
- 不影响会话序列化 / 消息压缩

### 测试重点（本次追加）
1. **Thinking 展开/折叠** — 点击 `▶` 按钮切换（不是点 header）
2. **输入框长内容** — 用 Ctrl+Shift+X 注入 Scene collector 长内容 → 输入框内应可上下滚动
4. **流式回复时上翻** — 让 LLM 生成长回复 → 滚滚轮上翻 → 右下角出现"↓ 跳到最新"按钮 → 按钮点击回到底部
5. **发送新消息回到底部** — 上翻状态下发送新消息 → 应强制回到底部
6. **消息引用点击** — 让 LLM 提到 `` `Assets/Foo.cs:42` `` → 应看到 chip → 点击应打开
7. **GameObject 引用** — 让 LLM 说 `[GameObject: Cube]` → 应看到 chip → 点击应在 Hierarchy 高亮
8. **Play Mode preflight** — 进入 Play Mode → 让 LLM 修改脚本 → 应收到 `Play Mode 中禁止...` 错误

---

## [1.6.4] - 2026-07-11 (D1)

### Summary
新增 **Context Ingest** 模块：全局快捷键 `Ctrl+Shift+X` 作为**通用查询入口**——用户对任何 Unity 界面元素（GameObject / Asset / Console log / Project Settings 设置项 / Package Manager 里的包 / 陌生自定义窗口）按下快捷键，都能采集相关上下文注入到 ChatWindow 输入框。支持单选/多选/大 Scene 自动降级采样，遵循 ADR-17 极简哲学（默认最佳，无设置项）。

### 核心定位
"我不认识/不知道这是什么" → 按 Ctrl+Shift+X → LLM 帮我解释

覆盖场景：
- Hierarchy / SceneView / Inspector 里选中 GameObject → 采完整组件字段快照
- Project Browser 选中 asset → 采类型专项元数据（Script/Texture/Prefab/Material）
- Console 焦点 → 采最近 error/warning
- Project Settings / Preferences 焦点 → **反射当前 SettingsProvider**（category path/label/scope）
- Package Manager 焦点 → **反射选中 package**（name/version/description）
- Animation Window 焦点 → **反射 active clip / GameObject**
- 其他任何未知窗口 → **UI Toolkit Pick 光标下 element** + 窗口元数据 + Selection 备注
- 完全无上下文 → 静默打开 ChatWindow

### Added — 全局快捷键 `Ctrl+Shift+X` Context 注入
- 用 Unity `[Shortcut("AgentCore/Ingest Context", KeyCode.X, ShortcutModifiers.Shift | ShortcutModifiers.Action)]` 注册
- **任意 Unity 窗口聚焦时都可触发**（Hierarchy / SceneView / Project / Console / Inspector 等）
- 触发时自动打开 ChatWindow（如未打开），文本追加到输入框光标位置（不清空已有输入）
- 用户可在 Unity Shortcut Manager 中改键

### Added — Context Collector 基础设施
新增独立 namespace [`AgentCore.Editor.UI.Context`](Editor/UI/Context/) 内 6 个类：
- [`ContextIngestResult`](Editor/UI/Context/ContextIngestResult.cs) — 采集结果 DTO（Label + Content + Truncated + Warning）+ 所有阈值常量集中在 `ContextIngestLimits`
- [`ContextIngestFormatter`](Editor/UI/Context/ContextIngestFormatter.cs) — 统一 markdown 格式化（`[@Label]\n\`\`\`\n{content}\n\`\`\`\n`）+ 超长自动截断
- [`ContextIngestRouter`](Editor/UI/Context/ContextIngestRouter.cs) — 根据 focusedWindow + Selection 状态路由到最合适的 Collector
- [`SelectionContextCollector`](Editor/UI/Context/SelectionContextCollector.cs) — Hierarchy 选中 GO（单/多选）+ 组件 SerializedProperty 字段快照
- [`AssetContextCollector`](Editor/UI/Context/AssetContextCollector.cs) — Project 选中 asset，类型专项元数据（Script/Texture/Prefab/Material 分别处理）
- [`ConsoleContextCollector`](Editor/UI/Context/ConsoleContextCollector.cs) — 反射访问 `UnityEditor.LogEntries` 采集最近 error/warning（独立于 ReadConsoleTool，避免重复实现，但字段签名一致）
- [`SceneContextCollector`](Editor/UI/Context/SceneContextCollector.cs) — 全 Scene Hierarchy 摘要 + 大 Scene 分层采样

### Added — 焦点窗口反射 + UI Toolkit Pick（"通用查询入口"关键实现）

新增两个类：
- [`FocusedWindowCollector`](Editor/UI/Context/FocusedWindowCollector.cs) — 处理"非已知全局 Selection 窗口"的采集：
  - Layer 1：已知专项窗口反射（`SettingsWindow.m_CurrentProvider` / `PackageManagerWindow.m_SelectedPackage` / `AnimationWindow.activeAnimationClip`）
  - Layer 2：UI Toolkit `rootVisualElement.panel.Pick(mousePos)` 采光标下 element（name + type + 父链前 5 层 + text/tooltip）
  - Layer 3：窗口元数据（title + 类型全名 + Global Selection 备注,明确标注"可能与你的问题无关"）
- [`MouseTracker`](Editor/UI/Context/MouseTracker.cs) — 持续追踪鼠标位置（Unity Shortcut 触发时 `Event.current` 为 null,必须靠预先追踪的静态字段）
  - `[InitializeOnLoadMethod]` 启动时给所有 EditorWindow 挂 MouseMoveEvent / MouseEnterEvent / PointerMoveEvent
  - 新窗口打开时通过 `EditorApplication.update` 持续扫描 + 挂钩
  - 样本 3 秒过期，防止读到陈旧位置

### 路由优先级（v1.6.4 严格版，避免"错的默认"）

```
1. Console 焦点             → ConsoleCollector
2. Project 焦点 + asset 选中 → AssetCollector
3. Hierarchy/SceneView/Inspector + GO 选中 → SelectionCollector
4. 其他 EditorWindow 焦点   → FocusedWindowCollector（反射 + Pick + 元数据）
5. 无匹配 + 全局 GO 选中    → SelectionCollector
6. 无匹配 + 全局 asset 选中 → AssetCollector
7. 最后回退                 → SceneCollector
```

关键改动 vs 旧版：**分支 4 优先于全局 Selection**。在 Project Settings / Preferences / 自定义窗口按快捷键，不再错误地采集"上次在 Hierarchy 选中的 Cube"。

### Sampling 策略（Selection / Scene 分级降级）

| 场景 | 策略 |
|------|------|
| Selection ≤ 20 GO | 完整组件字段快照 |
| Selection 20-100 GO | 名称 + 组件类型（无字段） |
| Selection > 100 GO | 只列前 100 名称 + `...(N more)` |
| Scene < 100 GO | 完整 Hierarchy tree |
| Scene 100-1000 GO | 前 3 层 + 每层最多 50 GO |
| Scene 1000-10000 GO | 前 2 层 + 每层最多 20 GO |
| Scene > 10000 GO | **拒绝注入**，提示改用精确选中 |
| Assets ≤ 20 | 完整类型专项元数据 |
| Assets > 20 | 只列路径 + 类型 |

Token 硬上限：单次注入内容 > 15000 字符时自动截断并 warn。

### 已知限制

- **IMGUI 窗口内的光标下元素无法识别**（Inspector 大部分字段、Console 内部条目、老 EditorWindow）
  - Unity IMGUI 无 hover API，Pick 只对 UI Toolkit 有效
  - IMGUI 场景下，走 Layer 3 元数据兜底
- **反射依赖 Unity 内部字段名**（`m_CurrentProvider` / `m_SelectedPackage` 等）
  - Unity 版本升级时可能改名，届时特定窗口回退到 Layer 2/3
  - 反射失败会 log 到 Console，不 crash
- **鼠标位置追踪仅覆盖 UI Toolkit 区域**
  - Editor 窗口的 IMGUI 部分不触发 MouseMoveEvent
  - 但 `Panel.Pick` 依然可用（因为 root 是 UI Toolkit）
- **Ctrl+Shift+X 触发瞬间光标未必指着"想问的"元素**
  - 用户需要主动**移动鼠标到目标元素上再按快捷键**
  - 未来考虑在 ChatWindow 里加个 log 提示"当前采集了 xxx，如非所需请调整光标"

### Added — ChatWindow 外部注入 API
[`ChatWindow.Input.cs`](Editor/UI/ChatWindow.Input.cs) 新增：
- `AppendToInputField(string text)` — 追加到光标位置，自动处理换行粘连，光标定位到注入后
- `FocusInputField()` — 供快捷键触发但无内容采集时聚焦输入框

### 路由优先级

```
1. Console 焦点 → ConsoleCollector
2. Project 焦点 + asset 选中 → AssetCollector
3. Selection.gameObjects 非空 → SelectionCollector
4. Selection.assetGUIDs 非空 → AssetCollector
5. SceneView / Hierarchy 焦点 → SceneCollector
6. 其他 → SceneCollector（最后回退）
```

### 已删除的候选设计（供留档）

- ❌ InspectorCollector — Inspector 显示对象 = Selection.activeObject，与 SelectionCollector 完全重合
- ❌ 输入框 `@` 触发（原 D1 UI 方案）— 用户明确要求全部走快捷键
- ❌ ContextChip 视觉组件 — 直接以纯文本注入，用户可手动编辑/删除

### 影响面 & 破坏性
- 0 API 破坏；仅新增文件 + 新 partial + 新快捷键
- 无新增设置项（符合 ADR-17；快捷键可在 Unity Shortcut Manager 改键）
- 快捷键冲突检查：`Ctrl+Shift+X` 未与 Unity 内置快捷键 / 本项目已有快捷键冲突
- `Ctrl+Shift+E` 保留为"导出会话"，`Ctrl+Shift+Q` 保留为"打开 ChatWindow"

### 测试重点
1. **Hierarchy 单选 GO** → 按 `Ctrl+Shift+X` → 检查输入框注入 `[@Selection: <name>]` + 组件字段
2. **Hierarchy 多选 GO**（3-5 个）→ 按快捷键 → 每个 GO 组件字段
3. **Hierarchy 大量多选**（50+）→ 只列名称 + 组件类型
4. **Hierarchy 极端多选**（100+）→ 只列前 100 名称 + warning
5. **Project 单选 script** → 注入路径 + 前 30 行预览
6. **Project 单选 texture** → 注入 TextureType/MaxSize/sRGB/尺寸
7. **Project 多选 assets**（20+）→ 只列路径 + 类型
8. **Console 焦点**（有 error）→ 注入最近 error/warning
9. **SceneView 焦点无 Selection** → Scene 摘要（含大 Scene 采样）
10. **无任何上下文**（新 EditorWindow）→ 静默但打开 ChatWindow

---

## [1.6.3] - 2026-07-11

### Summary
[1.6.2](#162---2026-07-11) 用 "每 8 chunk `Task.Yield()`" 消除了 Hold on 对话框，但用户实测**流式吐字速度明显变慢**（约 -30%）。根因：`Task.Yield` 在 Unity Editor 主线程上通过 `EditorApplication.delayCall` 恢复，恢复延迟叠加在每次 yield 上；固定 N chunk 阈值在高吞吐时过频 yield（一个 8 chunk 窗口 ≈ 200ms 数据，加上 50ms 恢复延迟 ≈ 25% 额外开销）。

### Changed — SSE Yield 策略从 "每 N chunk" 改为 "每 N 毫秒"
- [`StreamingResponseParser.cs`](Editor/LLM/StreamingResponseParser.cs)：
  - 删除 `YieldEveryNChunks = 8` 常量
  - 新增 `YieldBudgetMs = 200`：主线程连续占用满 200ms **才**让步一次
  - 用 `Stopwatch` 度量真实占用时间，让步后 `Restart()`
- **性能权衡**：
  - Unity Hold on 阈值 ≈ 500ms → 200ms 提供 2.5x 安全余量
  - 高吞吐场景（40 chunk/s）：一个 yield 窗口容纳 ~8 chunk，与 1.6.2 一致
  - 低吞吐场景（4 chunk/s）：一个 yield 窗口容纳 ~40 chunk 或整个流，几乎不 yield，吐字延迟归零
  - 短回复（~1-2 chunk）：整个流可能在 200ms 内完成，**根本不 yield**，无额外延迟
- **仍防 Hold on**：因为 200ms << 500ms，任何长回复必然被 yield 打断至少一次

### 影响面 & 破坏性
- 0 API 破坏；仅 [`StreamingResponseParser.ParseStreamAsync`](Editor/LLM/StreamingResponseParser.cs:33) 内部逻辑调整
- 无新增设置项（`YieldBudgetMs` 是内部常量，未来若 Unity 主线程行为变化可单点调优）
- 不影响 [1.6.2](#162---2026-07-11) 的 UI 感知修复（PendingIndicator / active-pulse / ThinkingDrawer preview）

---

## [1.6.2] - 2026-07-11

### Summary
解决用户反馈的**"点击发送后 UI 无反应，像卡死了"感知问题**。诊断发现：LLM 请求 in-flight 期间到 Streaming 状态之间有 5-30 秒空窗期，消息流区域完全空白；ThinkingDrawer / ToolCallGroup 默认折叠且无活动指示，用户不打开面板看不到内部正在流式更新。

对标 ChatGPT / Cursor / Windsurf / Cline / Perplexity 等 IDE 的用户感知设计（骨架屏 / 就地叙事 / 进度分解），落地两级方案：**P0 消息流内 Pending 占位气泡（覆盖点击→Thinking 空窗期）** + **P1 折叠面板活跃度指示器（ThinkingDrawer 尾部流式预览 + ToolCallGroup running 工具名 + active-pulse CSS class）**。

### Added — P0 Pending Indicator（消息流内占位气泡）
- 新增 [`Editor/UI/Components/PendingIndicator.cs`](Editor/UI/Components/PendingIndicator.cs)：
  - 独立的灰色气泡组件，末尾带 3 点循环动画（用 `IVisualElementScheduledItem` 驱动，避开 Unity UI Toolkit 缺失的 `@keyframes` 支持）
  - `SetActionText(text)` 更新描述；`Dismiss()` 停止动画并从消息列表移除
- 新增 [`Editor/UI/ChatWindow.PendingIndicator.cs`](Editor/UI/ChatWindow.PendingIndicator.cs) partial：
  - `ShowPendingIndicator(initialText)` 在消息列表末尾插入 pending 气泡
  - `UpdatePendingIndicatorAction(text)` 更新现有 pending 的文本
  - `SyncPendingIndicatorFromState(state)` 将 Agent 状态映射为动作描述（"思考中"→"调用工具中"→"回复中"→"压缩上下文"→"审阅答案"）
  - `DismissPendingIndicator()` 移除 pending 气泡
- 修改 [`ChatWindow.cs`](Editor/UI/ChatWindow.cs) 字段区：新增 `_pendingIndicator` 引用
- 修改 [`ChatWindow.Input.cs`](Editor/UI/ChatWindow.Input.cs) `OnSendClicked`：在异步发送**前**立即 `ShowPendingIndicator("思考中")`，错误回调时 `DismissPendingIndicator`
- 修改 [`ChatWindow.Events.cs`](Editor/UI/ChatWindow.Events.cs)：
  - `AssistantMessage` / `Error` 事件到达时 `DismissPendingIndicator`（真实回复已就绪，pending 完成使命）
  - `Idle` / `Thinking` / `Error` 状态变化时同步 `DismissPendingIndicator`（防止 leak）
  - 所有其他状态调用 `SyncPendingIndicatorFromState` 更新文本

### Added — P1 折叠面板活跃度指示器
- 修改 [`ThinkingDrawer.cs`](Editor/UI/Components/ThinkingDrawer.cs)：
  - 新增 `_previewLabel` — header 里的斜体尾部预览文本
  - `UpdatePreview()` — 显示 reasoning 尾部最多 60 字符（去除换行让单行不跳动，超长时用 `"..."` 前缀）
  - `AppendReasoning` 折叠状态时调用 `UpdatePreview` 让用户不展开也能看到实时内容
  - `SetExpanded` 切换 preview visibility（展开时隐藏，折叠时刷新）
  - `AppendReasoning` 首次触发时 `AddToClassList("active-pulse")`；`Complete` 时 `RemoveFromClassList("active-pulse")`
- 修改 [`ToolCallGroup.cs`](Editor/UI/Components/ToolCallGroup.cs)：
  - `UpdateSummaryText` 附加第一个 Running 工具的名字（如 `[3 个调用: 1 成功, 1 执行中: read_console, 1 等待]`）—— 折叠状态下用户也能看到当前正在跑的工具
  - `FindFirstRunningToolName()` 遍历 `_cards` 找 Status=Running 的第一个 ToolCallCard
  - `_runningCalls > 0` 时 `AddToClassList("active-pulse")`；否则移除

### Added — CSS `active-pulse` 类
- [`ChatWindow.uss`](Editor/UI/ChatWindow.uss) 追加：
  ```css
  .active-pulse {
      border-*-color: #4A90D9;  /* 蓝色边框 */
      transition-property: border-color;
      transition-duration: 0.5s;
  }
  ```
- **限制**：Unity UI Toolkit 不支持 `@keyframes`，因此不做真正的脉动动画；改用静态高对比色 + `transition-duration: 0.5s` 让 class 切换时颜色平滑变化，视觉上区分"运行中"和"完成"两种状态
- **未来方向**：若 Unity 后续版本支持 `@keyframes`，可扩展为真正脉动；当前静态色差已足够传达"活跃"信号

### 覆盖阶段矩阵

| 用户交互阶段 | 修补前 | 修补后 |
|-------------|--------|-------|
| 点击发送 → LLM 首次响应（5-30s） | 消息区空白 | **PendingIndicator** 显示"思考中..." + 3 点动画 |
| Thinking 状态（reasoning 流入） | ThinkingDrawer 折叠无提示 | **ThinkingDrawer 预览尾部 60 字符** + 蓝色边框 |
| ExecutingTool | ToolCallGroup 只显示"1 执行中" | **附加工具名** "1 执行中: read_console" + 蓝色边框 |
| Streaming | 消息气泡开始流出正文 | 无变化（已有） |
| AssistantMessage 完成 | pending 遗留（本次修复前不存在） | 明确 `DismissPendingIndicator` |
| Error | pending 遗留 | 明确 `DismissPendingIndicator` |

### 竞品对标依据（[事实]）
- **ChatGPT / Claude.ai**：三点动画 + 灰色骨架气泡 — 本方案的 PendingIndicator 采纳
- **Cursor**：消息区显示 "Thinking..." / "Running tool: X" — 本方案的 ToolCallGroup running 工具名采纳
- **Windsurf Cascade**：动作叙事（"Reading file X"）— 本方案的 SyncPendingIndicatorFromState 映射采纳
- **Cline / RooCode**：透明性 — AgentCore 原本已有 ToolCallCard，本方案在**折叠状态下**也传达进度信号

### 影响面 & 破坏性
- 0 API 破坏；仅 UI 层增强
- 新增 1 个类 + 1 个 partial + 4 个文件修改 + 1 处 USS 追加
- 未新增设置项，符合 ADR-17 极简（"用户感知"属于默认体验，不该给开关）
- Compression / 消息序列化不涉及；pending 只在内存中存在

### Fixed — 消息发送时 Unity 弹出 "Hold on / UnitySynchronization.ExecuteTasks" 对话框（ADR-19）

**症状**：LLM 流式回复期间 Unity 弹 Hold on 模态框，PendingIndicator / ThinkingDrawer / ToolCallGroup 的所有 UI 动画完全无法渲染（UI 消息泵被抢占）。

**Spike 诊断结果**（[事实]，可回退探针见 [`plans/adr-19-main-thread-unblocking.md`](plans/adr-19-main-thread-unblocking.md)）：
- `SendMessageAsync` 主线程同步段仅 39ms（原假设 1-4s，**否定**）
- `WorkspaceSnapshotBuilder.Build` 仅 32ms（原假设 500-2000ms，**否定**）
- 流式回调线程 = 主线程（thread=1, isPoolThread=False）
- 流式 chunk 速率 ≈ 38.7/s，`EmitEvent` 速率 ≈ 38.5/s
- 短回复（1 chunk）不触发 Hold on；长回复（数百 chunk × 25ms）稳定触发
- **根因**：`StreamingResponseParser.ParseStreamAsync` 的 `while` 循环 + `ReadLineAsync + ParseChunkJson + onChunk` 全在主线程同步执行，Unity 主线程 >500ms 无空闲窗口，触发内置 Hold on 保护

**修复**：
- [`StreamingResponseParser.cs`](Editor/LLM/StreamingResponseParser.cs) `ParseStreamAsync` 每 `YieldEveryNChunks = 8` 个 chunk 主动 `await Task.Yield()`，让 `UnitySynchronizationContext` 消息泵有机会刷新 UI/GUI，然后立即回到主线程继续 parse
- 8 chunk × ~25ms/chunk ≈ 200ms 让步一次，远低于 Unity 的 500ms Hold on 阈值，且不影响流式感知（token 更新仍是逐 chunk）
- [`AgentLoop.SendMessageAsync`](Editor/Core/AgentLoop.cs:344) 在同步准备段后追加一次 `await Task.Yield()`，保证 PendingIndicator 至少渲染一帧再进入 HTTP 请求

**关键设计取舍**：
- ❌ 未采用后台线程搬迁方案（ADR-19 §Plan B 全量重构）：Spike 证明真正的瓶颈只在 stream 循环，不需要跨模块重构 `SendMessageAsync` / `WorkspaceSnapshotBuilder` / `EmitEvent`
- ✅ 保留所有 API 契约，仅在 SSE 解析循环内加 4 行代码
- ✅ 遵循 "证据优先" 原则：ADR-19 原设计假设未通过 spike 验证，**及时收窄修复范围**，避免 6-9h 无效重构

**影响面**：
- 0 API 破坏；`StreamingResponseParser.ParseStreamAsync` 签名不变
- 无新增设置项（符合 ADR-17；Hold on 是 bug，不是可选项）
- 让步策略是常量 (`YieldEveryNChunks = 8`)，未来若 Unity 主线程行为变化可单点调优

---

## [1.6.1] - 2026-07-11

### Summary
配套 [1.6.0](#160---2026-07-11) 的 Skill 加载机制，加固 Prompt 层的**意图验证**和**能力发现**规则。诊断发现 [`SOUL.md`](Editor/Bootstrap/Resources/SOUL.md) 缺两条关键指令：(1) 明确禁止猜测用户意图并强制反问收束；(2) 让 LLM 知道 Skill 系统存在（虽然 tool description 已有，但 SOUL.md 层缺失导致高性能模型可能跳过 Node A 反问机制时也没有 fallback）。本次修补两处，同时顺手修复原 §1 存在的编号 bug（两个 "5."）。

### Fixed — SOUL.md §1 编号 bug
- **现象**：原 [`SOUL.md §1`](Editor/Bootstrap/Resources/SOUL.md) 存在两个编号为 5 的条目（"Minimal changes" 和 "Tools first"），后续 6/7/8 因此错位
- **修复**：重新编号为连续的 1~10，插入新增的 "Verify intent before acting" 作为 §1.1

### Added — SOUL.md §1.1 "Verify intent before acting"
- **规则内容**（永驻 system prompt）：
  > Never guess what the user means. If a request is vague, broad, or has multiple plausible interpretations, ask clarifying questions until the target is unambiguous. Confirm scope with the user before starting work that involves destructive operations, multi-file changes, or architectural decisions. Repeat clarification cycles as needed — do not proceed with self-invented assumptions. Only skip clarification when the request is fully unambiguous AND non-destructive.
- **作用面**：所有模型，包括高性能模型（Claude Opus / GPT-o 等因 L1-L4 escape 机制跳过 Node A 运行时反问的模型）
- **与 Node A 运行时机制互补**：低性能模型走 [`SelfChallenge Node A`](Editor/Core/SelfChallenge/) Combo1/Combo2 触发运行时反问；高性能模型走本 SOUL.md prompt 层约束

### Added — SOUL.md §4 "Skills are on-demand domain guidance"
- **规则内容**（永驻 system prompt Context Awareness 段）：
  > Skills are on-demand domain guidance — use `load_skill(action="list")` to discover available skill guides (workflows / conventions / checklists for animation, prefab, shader, patterns, testing, etc.); use `action="load"` when a task matches a skill's scope. Prefer loading a skill over asking the user for guidance you should already have. Skill content stays in context until unloaded.
- **理由**：ADR-18 的 D6-b 决策"不改 SOUL.md，只强化 tool description"—— 但用户明确要求 SOUL.md 层保底覆盖。改动成本极低（一行文本）+ 收益明确（LLM 无需依赖 tool description 才能发现 skill 系统），修改。**推翻 ADR-18 D6-b 决策**，改为 D6-a（SOUL.md 补路由指令）。

### 影响面 & 破坏性
- SOUL.md 从 ~50 行增至 ~55 行（+10% token 增长，可控）
- 覆盖了 [1.6.0](#160---2026-07-11) 的 Skill 系统在 SOUL.md 层的可见性缺口
- 覆盖了 Node A 逃逸机制下高性能模型对"用户意图不清"场景的行为缺陷
- 0 API/schema 破坏；仅系统提示词内容变化

### Notes — ADR-18 D6 决策更新
[`plans/adr-18-skill-loading-mechanism.md §5.2 D6`](plans/adr-18-skill-loading-mechanism.md) 原推荐 D6-b（不改 SOUL.md），因本次用户实际使用中发现 prompt 层缺口，改为 D6-a（在 SOUL.md 补 skill 触发指令 + 意图验证约束）。ADR-18 文档保留原推荐作为演进记录，实际实施走 D6-a。

---

## [1.6.0] - 2026-07-11

### Summary
两项功能一起 ship：**(1) Skill 加载机制 MVP**（ADR-18 Phase 1，突破 Bootstrap "会话开始一次性全量装配" 的架构限制，让 AgentCore 具备类 Claude Code Skills 语义）；**(2) 消息气泡一键复制按钮**（气泡右上角显示"复制"按钮，一键将 assistant / error 气泡完整 markdown 原文复制到系统剪贴板）。

已在使用者项目内 embedded 模式下实机验证通过：`list_skills` 正确返回 53 个 Unity skill，`load unity-patterns` 后 LLM 明显引用 skill 内容回答设计模式选择问题，`unload` 后清理干净。

minor 版本 bump 反映功能级新增；对现有会话零破坏。

### Added — Skill 加载机制（ADR-18 §5 / §6）
- 新增 [`Editor/Skills/`](Editor/Skills/) 目录 5 个文件：
  - [`SkillMetadata`](Editor/Skills/SkillMetadata.cs) — 元数据（不含全文，供 list 展示）
  - [`SkillContentBuilder`](Editor/Skills/SkillContentBuilder.cs) — 定义 `Marker = "# [SKILL] "` 常量 + system message 构造
  - [`SkillScopeState`](Editor/Skills/SkillScopeState.cs) — 会话级已加载 skill 集合（结构镜像 `ToolScopeState`）
  - [`SkillRegistry`](Editor/Skills/SkillRegistry.cs) — 磁盘扫描 + 缓存，全文延迟加载
  - [`SkillFrontmatterParser`](Editor/Skills/SkillFrontmatterParser.cs) — 极简 YAML frontmatter 解析（不引入 YamlDotNet 依赖）
- 新增 [`LoadSkillTool`](Editor/Tools/Native/Meta/LoadSkillTool.cs) 元工具（Category=Meta, Visibility=AlwaysVisible）：
  - `list` — 枚举所有可用 skill（含名称、描述、分类、估算 token、是否已加载）
  - `load` — 按名称加载单个 skill；重复加载返回 already_loaded
  - `list_loaded` — 查看当前已加载 skill 集合与总 token 数
  - `unload` — 卸载单个 skill（下一轮 skill message 从 `_messages` 移除）
  - `reload` — 强制刷新 registry 缓存（Phase 1 保持 unload+load 语义）
  - 软 token budget = 15000（超过时在 tool_result 附带 warning，不阻塞）
- 新增 [`Editor/Core/AgentLoop.SkillContext.cs`](Editor/Core/AgentLoop.SkillContext.cs) partial：
  - `InitializeSkillContext` — 创建 `SkillScopeState` 并注入到 `LoadSkillTool.SetScopeState`
  - `SyncSkillMessages` — 每轮 `SendMessageAsync` 发送前同步 skill message 到 `_messages`（插入最后一条 user message 前，位置与 Deferred Context 同级）
  - `ResetSkillContext` / `DisposeSkillContext` — 会话切换与销毁清理

### Fixed / Changed — 集成点
- [`AgentLoop.cs:302`](Editor/Core/AgentLoop.cs) `Initialize()` — 在 `ToolScopeState` 初始化后追加 `InitializeSkillContext()` 调用
- [`AgentLoop.cs:461`](Editor/Core/AgentLoop.cs) `SendMessageAsync` — 在构建 tool definitions 前调用 `SyncSkillMessages()`，try/catch 保证异常非阻塞
- [`AgentLoop.cs:568`](Editor/Core/AgentLoop.cs) `ResetConversation` — 追加 `ResetSkillContext()` 调用（skill message 随 `_messages.Clear()` 一并清空）
- [`AgentLoop.cs:768`](Editor/Core/AgentLoop.cs) `Dispose` — 追加 `DisposeSkillContext()` 解除事件订阅并清空 tool 引用
- [`ConversationCompressor.cs:180-191`](Editor/Core/Compression/ConversationCompressor.cs) skip-list 新增第 4 类跳过条件 `SkillContentBuilder.Marker`，保证已加载 skill 在长会话中不被压缩
- [`AgentCoreSettings.cs`](Editor/Config/AgentCoreSettings.cs) 新增 `skillsEnabled` 字段（默认 true, `[HideInInspector]`），关闭后 `load_skill` 返回错误

### Skill 文件格式（兼容 AGENTS.md §7.2 现有约定）
- 目录：`<project-root>/.agents/skills/<name>/SKILL.md`（首选）或 `<project-root>/Assets/.agents/skills/<name>/SKILL.md`（Unity 项目内覆盖）
- 支持可选 YAML frontmatter（`name` / `description` / `category` / `version`），缺失时从目录名和首个 `# 标题` 自动推断
- 现有 AGENTS.md §7.2 定义的 8 个 skill 目录（`unity-runtime-dev` / `unity-blueprints` / `unity-scene-contracts` 等）**零改动即可被 AgentCore 识别**

### Compression 契约扩展
- 现有 3 类跳过标记（`SummaryMessageMarker` / `WorkspaceSnapshotBuilder.SnapshotMarker` / `"# Available Tools"`）新增第 4 类：`SkillContentBuilder.Marker = "# [SKILL] "`
- 未来添加新的"运行时静态上下文"类别时应遵循同样模式：定义独立 marker + 在 `ConversationCompressor.FindCompressibleRange` skip-list 补一条

### Design Decisions（ADR-18 §5.2）
- **D1-a** Skill 目录沿用 `.agents/skills/`（复用 AGENTS.md §7.2 约定）
- **D2-a** 会话级生命周期，新会话清空，不做 Domain Reload 持久化（Phase 3 可选）
- **D3-a** 拒绝重复 load，`reload` 显式强制刷新
- **D4-a** 永驻 system message（不作为 tool_result 一次性返回）
- **D5-b** 软 token budget 15K（warning 不阻塞）
- **D6-b** 不改 SOUL.md，只在 `load_skill` 工具描述里强化"何时使用"信号

### Limitations（Phase 1 有意的简化）
- `reload` action 只刷新 registry 磁盘缓存，会话中的旧 skill message 内容不会自动替换 —— 用户需要 `unload` + `load` 才能看到新内容。Phase 2 再增强。
- Skill 状态不做 Domain Reload 持久化；脚本重编译后 LLM 需要根据 message history 上下文重新决定是否 `load_skill`。
- 没有 Settings UI 卡片（Phase 2 加）；`skillsEnabled` 用 `[HideInInspector]` 隐藏，默认启用。

### 影响面 & 破坏性（Skill 系统）
- 0 破坏；对未启用 skill 系统的会话完全透明
- 现有 tool schema 未变，`request_tools` / `ToolScopeState` 等基础设施保持不动
- Compression 主流程未改，仅扩展 skip-list

### Added — 消息气泡一键复制按钮
- 在 [`MessageBubble.uxml`](Editor/UI/Components/MessageBubble.uxml) header 里新增 `copy-button`
- [`MessageBubble.cs`](Editor/UI/Components/MessageBubble.cs) 新增：
  - `_lastFullContent` 字段：缓存原始 markdown 文本（未经 ContentFilter/Markdown 渲染），保证复制到剪贴板的是 markdown 源码而不是 UI 渲染后的富文本
  - `RawContent` 公开只读属性
  - `SetupCopyButton` — 只对 `assistant` / `error` 显示，`user` 隐藏（用户已知自己输入了什么）
  - `HandleCopyClicked` — 写 `EditorGUIUtility.systemCopyBuffer` + 1.2 秒 "已复制" 视觉反馈 + 异常时显示 "失败"
  - `AppendStreamToken` / `FinalizeContent` / `SetupStaticMode` / `SetupStreamingMode` 4 处更新 `_lastFullContent`
  - `CreateFallbackLayout` 也加了 copy-button 保证 UXML 加载失败场景仍可用
- [`MessageBubble.uss`](Editor/UI/Components/MessageBubble.uss) 新增 `.bubble-copy-button` 样式 + hover / active / user / error 变体
- **Focus 处理**：按钮 `focusable = false`，避免抢焦点导致文本选中丢失

### 影响面 & 破坏性（Copy 按钮）
- 0 破坏；仅扩展现有 UI；user 气泡样式无变化（按钮 display:none）
- 无新增字段/设置项，符合 ADR-17 极简哲学（一键操作，无配置需求）

---

## [1.5.8] - 2026-07-10

### Summary
紧急修复 [1.5.7](#157---2026-07-10) 中 [`VcsExternalToolLauncher.cs`](Editor/VCS/Tools/VcsExternalToolLauncher.cs) 因同时 `using System.Diagnostics;` 和 `using UnityEngine;` 导致 `Debug` 引用二义（`System.Diagnostics.Debug` vs `UnityEngine.Debug`）编译失败（CS0104）。1.5.7 tarball 已在发布后立即拉回，未进入实机验证，本 1.5.8 视为对 1.5.7 功能内容的原地补丁。

### Fixed — VcsExternalToolLauncher CS0104 编译失败
- **现象**：安装 1.5.7 后 Unity 编译报错 `error CS0104: 'Debug' is an ambiguous reference between 'System.Diagnostics.Debug' and 'UnityEngine.Debug'` 于 [`VcsExternalToolLauncher.cs:116,123`](Editor/VCS/Tools/VcsExternalToolLauncher.cs)。
- **根因**：新文件同时引入 `System.Diagnostics`（用于 `Process` / `ProcessStartInfo`）和 `UnityEngine`（用于 `Debug.Log`），两个命名空间都定义 `Debug` 类型，C# 无法自动消歧。
- **修复**：移除 `using System.Diagnostics;`，改用完全限定名 `System.Diagnostics.Process` / `System.Diagnostics.ProcessStartInfo`，让文件里的裸 `Debug` 唯一指向 `UnityEngine.Debug`。这与仓库现有约定一致（`SvnAdapter.cs` / `GitAdapter.cs` 等均已采用同一模式）。
- **影响面**：0 破坏；纯编译修复。1.5.7 的所有功能内容照旧。

---

## [1.5.7] - 2026-07-10 (yanked)

### Summary
> **⚠️ 已拉回**：该版本因 CS0104 编译失败导致装完即坏，未进入实机验证阶段即被 [1.5.8](#158---2026-07-10) 顶替。功能描述保留作为演进记录。

修复 Scene View 顶部黄色 VCS 更新提示条点击 "Update Now" 后**无任何反应**的严重体验缺陷。原实现走 `_ = VcsRemoteStatusMonitor.SyncAsync()` fire-and-forget，既不唤起外部 VCS GUI（TortoiseSVN / Git GUI / P4V），也不显示进度、结果、异常，Task 结果被完全丢弃。用户视角是"点了没反应"；Console 里出现的那段 log 只是 `EditorUtility.DisplayDialog` 自身的 stack trace，与 Sync 是否执行无关。

按外部 GUI 优先方案落地：新增 [`VcsExternalToolLauncher`](Editor/VCS/Tools/VcsExternalToolLauncher.cs) 统一启动器（SVN → TortoiseProc / Git → git-gui / Perforce → p4v），返回 `(bool, reason)` 便于调用方决策；[`VcsSceneViewUpdateBanner`](Editor/VCS/UI/VcsSceneViewUpdateBanner.cs) 点击流程重构为"外部 GUI 优先 → 找不到时回退到 CLI + 进度条 + 结果弹窗 + AssetDatabase.Refresh"。

### Fixed — SceneView VCS Banner Update 按钮无反应
- **现象**：Scene View 顶部黄色横幅出现后点击横幅，弹出"Version Control Update"确认框，点 "Update Now"，什么都没发生。TortoiseSVN 不启动，Unity 里没有进度条，没有成功/失败反馈，只有一段横幅弹窗自身的调用栈 log。
- **根因**：[`VcsSceneViewUpdateBanner.cs:52`](Editor/VCS/UI/VcsSceneViewUpdateBanner.cs) 使用 `_ = VcsRemoteStatusMonitor.SyncAsync()` fire-and-forget 直接调用内嵌 SVN CLI 子进程，Task 结果被丢弃：成功/失败/异常/冲突全部静默；且从未打算唤起外部 GUI，与用户对"Update"按钮的直觉预期（打开 TortoiseSVN 的 Update 窗口）不符。
- **修复**：
  - 新增 [`VcsExternalToolLauncher.cs`](Editor/VCS/Tools/VcsExternalToolLauncher.cs)：`TryOpenUpdateWindow(out reason)` 按当前 VCS 类型分派到对应 GUI 启动命令；`TryStartProcess` 通用 `UseShellExecute=true` 启动器；`BuildUnavailableMessage` 生成友好未安装提示。
  - 重构 [`VcsSceneViewUpdateBanner.cs`](Editor/VCS/UI/VcsSceneViewUpdateBanner.cs) 点击流程：确认对话框按钮改为 "Open Update Window"（如实描述）；确认后先调 `VcsExternalToolLauncher.TryOpenUpdateWindow`，成功则延迟触发 `VcsRemoteStatusMonitor.RequestCheck()` 让 banner 在 GUI 完成后消失；失败弹二次对话框询问是否走 CLI 回退，用户同意后 async 运行 `SyncAsync` + `EditorUtility.DisplayProgressBar` + 结果弹窗 + `AssetDatabase.Refresh()`。全流程 try/catch，进度条保证在异常路径清理。
- **影响面**：0 破坏；仅重构 Banner 一个入口点，[`VcsRemoteStatusMonitor.SyncAsync`](Editor/VCS/Tools/VcsRemoteStatusMonitor.cs) 与 [`VersionControlPanel`](Editor/VCS/UI/VersionControlPanel.cs) 现有 external-tool 私有实现保持不变。

### Notes
- 已知技术债：[`VersionControlPanel.cs:1406-1462`](Editor/VCS/UI/VersionControlPanel.cs) 里 `TryOpenExternalUpdateWindow` / `TryStartExternalProcess` 私有实现与新静态类构成局部重复。范围仅限 update-window 一个动作，暂不迁移，避免本次跨模块改动扩散；下次触及 Panel 的外部工具分支再一并收敛到 `VcsExternalToolLauncher`。
- 验证：在 Scene View 触发 banner 后点击横幅 → 应弹出新版对话框（按钮 "Open Update Window" / "Cancel"）→ 确认后 TortoiseSVN Update 窗口应弹出，Console 打印 `[Version Control] Launched TortoiseSVN update window: ...`；关闭 TortoiseSVN 后 banner 会在下一次远端状态检查时消失。若 TortoiseProc 不在 PATH，会先说明未检测到 GUI，再询问是否走 CLI 回退。

---

## [1.5.6] - 2026-07-10

### Summary
放弃 `1.5.0-alpha1 ~ 1.5.0-alpha5` 预发布序列，将 5 个 alpha 视为已消耗的补丁位，直接以 **1.5.6** 作为首个稳定版发布。汇总 alpha 段全部功能（Self-Challenge Phase 9 / ADR-17 极简哲学 / L1-L4 模型分层逃逸 / GLM-5.2 全链路适配 / Settings 分页精简 6→5），**新增 PreferencesFolder 目录不存在导致 Save 卡死 Editor 的关键修复**，并**附带面向已受影响用户的紧急离线卸载脚本 + 文档**。历史 alpha 段落保留在下方作为演进记录。

### Added — 紧急离线卸载（面向已因旧版卡死无法启动 Unity 的用户）
- 新增 [`EMERGENCY-UNINSTALL.md`](EMERGENCY-UNINSTALL.md)（**随 tarball 发布**，用户解压 tarball 或访问仓库均可看到）：分步说明在 **不打开 Unity Editor** 的前提下清理 `Packages/manifest.json` 依赖 + `Packages/com.agentcore/` embedded 目录 + `Library/PackageCache/com.agentcore.unity*/` + `Library/AgentCore/`，以及可选的 `%APPDATA%/Unity/Editor-*/Preferences/AgentCore/` 全局偏好目录。
- 新增 [`tools/emergency-uninstall.bat`](tools/emergency-uninstall.bat)（**源码仓库维护，不进 tarball**）：纯 `cmd.exe` 内置命令实现，**不依赖 PowerShell**，双击即可运行。自动 kill Unity 进程释放文件锁 → 清 embedded / PackageCache / Library/AgentCore / packages-lock.json → 可选 `/prefs` 清全局 Preferences；`manifest.json` 编辑刻意留给用户手工完成（避免脚本破坏 JSON 结构），脚本最后自动为用户打开 Notepad。支持位置参数 `"项目路径"` + 开关 `/prefs` / `/yes`。
- 背景：1.5.6 之前旧版本装完后 Unity 一启动即弹 Force Quit 卡死，用户根本无法进 Package Manager 卸载；且部分企业沙盘 / 精简 Windows 环境无 PowerShell 可用，因此采用 BAT 作为最兼容的落地形态。

### Fixed — PreferencesFolder/AgentCore 目录不存在导致 Save 失败卡死 Editor
- **现象**：全新安装时 Editor 报错 `Moving F:/.../Unity/Temp/UnityTempFile-<hash> to C:/Users/<user>/AppData/Roaming/Unity/Editor-5.x/Preferences/AgentCore/Settings.asset: 系统找不到指定的路径`，弹出 Force Quit 对话框，需强制退出。
- **根因**：AgentCore 的三个 [`ScriptableSingleton`](https://docs.unity3d.com/ScriptReference/ScriptableSingleton_1.html)（[`AgentCoreSettings`](Editor/Config/AgentCoreSettings.cs) / [`IndexingSettings`](Editor/Indexing/Config/IndexingSettings.cs) / [`DomainReloadState`](Editor/Core/DomainReloadState.cs)）均使用 `[FilePath("AgentCore/<name>.asset", PreferencesFolder)]`，写入路径位于 Unity 全局偏好目录下的 `AgentCore/` 子目录。Unity 内部 `SaveToSerializedFileAndForget` 使用 `Move(temp → target)` 语义，但**不会自动创建父目录**。全新用户从未有插件在 `Preferences/AgentCore/` 写过文件时，该子目录不存在，Move 失败并抛出对话框。
- **修复**：
  - 新增 [`PreferencesFolderPathHelper`](Editor/Config/PreferencesFolderPathHelper.cs)：通过反射优先使用 `InternalEditorUtility.unityPreferencesFolder`，回退到平台特定路径（Windows `%APPDATA%/Unity/Editor-{major}.x/Preferences`、macOS/Linux 对应位置），在首次 Save 前 `Directory.CreateDirectory` 确保 `AgentCore/` 子目录存在，结果缓存避免重复 IO。
  - 三个 Singleton 各新增 `SafeSave(bool)` 包装：先调 `EnsureAgentCoreDirectory()`，再 `Save`，异常统一 `LogWarning` 而非抛出，Editor 再无被卡死的可能。
  - 全项目 14 处 `Save(true)` 调用全部替换为 `SafeSave(true)`（[`AgentCoreSettings`](Editor/Config/AgentCoreSettings.cs) 5 处、[`IndexingSettings`](Editor/Indexing/Config/IndexingSettings.cs) 2 处、[`DomainReloadState`](Editor/Core/DomainReloadState.cs) 7 处）。
- **影响面**：0 破坏；纯防御性加固。现有用户无感（目录已存在时直接短路）；新用户从此不再触发首次安装卡死。

### Notes
- 版本号跳过 1.5.0 ~ 1.5.5：alpha1~alpha5 视为已消耗的 5 个补丁位（含未 tag 的 alpha3 占位）；本稳定版直接从 1.5.6 起步。alpha 段功能已全部并入本版；旧 alpha tarball 归档至 [`_archive/tarballs/`](_archive/tarballs)。
- 验证建议：删除 `%APPDATA%/Unity/Editor-*/Preferences/AgentCore/` 后重启 Unity，首次打开 `Project Settings > AgentCore`，确认不再弹出 Force Quit。

---

## [1.5.0-alpha4] - 2026-07-09

### Summary
Self-Challenge 模型分层逃逸机制落地 + 原始 bug 收尾修复。高级模型(Claude Opus / GPT-o / DeepSeek-R / Gemini 2.5 等具备 native reasoning)通过 L1-L4 逃逸门跳过 Node A + Node B,避免 thinking 重复消耗与上下文污染;低性能模型路径保留原 Self-Challenge,并修复 A/B1 两个次生失效。B2(clarification 卡片)推迟至 beta。

ADR: [`plans/adr-self-challenge-model-tier-escape.md`](plans/adr-self-challenge-model-tier-escape.md)

### Fixed — 原始 bug(原 v1.5.0-alpha3 tag 内容,并入本版)
- **`<intent_challenge>` 块泄漏到聊天气泡**:Node A 流式 extractor 在 Continuation 模式下未激活,导致 challenge block 原样输出到用户可见内容。
  - 修复:extractor 改为始终激活(Full / Continuation 统一),无论当前轮次模式。
  - commit: 18f9b44(原 alpha3 tag 指向此 commit,但 package.json 未同步 bump,tag 为空壳,本版正式收录)

### Added — L1-L4 模型分层逃逸
- **L1 — 模型能力检测** ([`ModelCapabilityDetector.cs`](Editor/Core/ModelCapabilityDetector.cs)):前缀匹配识别 native reasoning 模型(claude-opus / gpt-o / deepseek-r / gemini-2.5 等),返回 `HasNativeReasoning`。
- **L3 — 逃逸开关** ([`AgentCoreSettings.cs`](Editor/Config/AgentCoreSettings.cs) `selfChallengeEscapeEnabled` 字段 + [`ModelAgentSettingsPage.cs`](Editor/Config/Settings/Pages/ModelAgentSettingsPage.cs) UI toggle):默认 true;检测到高级模型时灰醒提示已自动逃逸。热插拔,实时生效。
- **L2 — Node A / Node B 逃逸门** ([`AgentLoop.SelfChallenge.cs`](Editor/Core/AgentLoop.SelfChallenge.cs)):`PrepareSelfChallengeDataForNewTurn` Node A 门 + `HandleFinalResponse` Node B 门,`escapeEnabled && detector.HasNativeReasoning` 时跳过注入与触发。
- **L4-A — retry prompt 硬约束** ([`IntentChallengePromptBuilder.cs`](Editor/Core/SelfChallenge/IntentChallengePromptBuilder.cs) + [`AnswerChallengePromptBuilder.cs`](Editor/Core/SelfChallenge/AnswerChallengePromptBuilder.cs)):Correction / Revise retry 指令强制 HARD CONSTRAINT,禁止模型再次输出 challenge block,仅输出修正内容。

### Fixed — L4-B1 Node B 生命周期与 send gate
- **send gate 阻断澄清回复** ([`ChatWindow.Input.cs`](Editor/UI/ChatWindow.Input.cs)):`OnSendClicked` 仅允许 Idle,阻断 WaitingForClarification 状态下的用户回复。修复为 `Idle || WaitingForClarification` 对齐 [`AgentLoop.SendMessageAsync`](Editor/Core/AgentLoop.cs) gate。
- **Node B fire-and-forget 状态泄漏**:Node B 异步运行期间无独立状态,用户可触发新一轮导致 turn-bound 数据串写。
  - 新增 [`AgentState.ReviewingAnswer`](Editor/Core/MessageTypes.cs):Node B 触发前 `SetState(ReviewingAnswer)`,`TriggerNodeBAsync` finally 恢复 Idle。
  - [`ChatWindow.Events.cs`](Editor/UI/ChatWindow.Events.cs) 新增 ReviewingAnswer UI 分支:状态标签"审阅答案中...",禁用发送,显示取消。
  - [`AgentLoop.Runner.cs`](Editor/Core/AgentLoop.Runner.cs) `TriggerNodeBAsync` 签名新增 `turnBoundData` 参数;`InvokeNodeBAsync` + `BuildReviewerMessages` 全部改用 turn-bound 局部变量 `nodeBData`,不再读写实例字段 `_currentSelfChallengeData`,消除跨轮覆盖。
  - [`AgentLoop.cs`](Editor/Core/AgentLoop.cs) post-loop Idle 检查新增 `ReviewingAnswer` 例外,避免被提前覆盖。
- **Cancel 安全性**:`Cancel()` 已设 Idle,`TriggerNodeBAsync` finally 仅在仍处于 ReviewingAnswer 时才恢复,无双重 SetState。DomainReload switch default → `InterruptPhase.None`,ReviewingAnswer 安全降级。

### Notes
- B2(ClarificationOptionCard 可点击澄清选项)推迟至 v1.5.0-beta;B1 已修复核心 send gate,B2 纯 UI 增强可后续迭代。
- alpha3 tag 保留为历史标记(指向 18f9b44 extractor 修复),但从未独立发布 tarball,其内容已并入 alpha4。
- 集成验证(高级模型逃逸 + 低性能模型补丁 + 热插拔)待执行。

## [1.5.0-alpha5] - 2026-07-10

### Summary
Settings 分页精简(6→5,UiDiagnostics 拆解合并)+ GLM-5.2(Z.ai 1M context reasoning model)全链路适配:加入 native reasoning 白名单逃逸 Self-Challenge,上下文窗口映射对齐 1M,默认参数面向 GLM-5.2 调优。

### Changed — GLM-5.2 全链路适配
- **L1 白名单**:[`ModelCapabilityDetector.NativeReasoningPrefixes`](Editor/Core/ModelCapabilityDetector.cs) 新增 `"glm-5"` 前缀(GLM-5 系列为 Z.ai large-scale reasoning model,含 5.2 量化变体如 W4AFP8)。`HasNativeReasoning("glm-5.2")` → true → Self-Challenge Node A/B 双 gate 跳过,依赖 native reasoning。
- **上下文窗口映射**:[`ContextWindowManager.ModelPrefixMap`](Editor/Core/ContextWindowManager.cs) 新增 4 条 GLM 条目(最长前缀优先):
  - `("glm-5.2", 1048576)` — GLM-5.2 1M context
  - `("glm-5", 202752)` — GLM-5/5.1/5-turbo/5v-turbo
  - `("glm-4", 200000)` — GLM-4.5~4.7(取上限)
  - `("glm-", 128000)` — GLM 其他保守估计
- **默认参数面向 GLM-5.2 调优**:[`AgentCoreSettings`](Editor/Config/AgentCoreSettings.cs)
  - `llmModel`:`"auto"` → `"glm-5.2"`(代理接受显式名,前缀匹配生效)
  - `maxTokens`:`16000` → `65536`(GLM-5.2 max_completion_tokens=101376,取 64K 留余量)
  - `enableReasoningOutput`:`false` → `true`(逃逸 Self-Challenge 后注入空 `reasoning:{}` 触发 reasoning_content 返回,UI thinking trace 可展示;effort/maxTokens 留空让模型默认推理)
  - `temperature=0.7` / `reserveResponseTokens=32000` 保持不变
  - ResetToDefaults 同步更新

### Changed — Settings 分页精简(6→5)
- **UiDiagnosticsSettingsPage 拆解删除**:原 6 分页(Dashboard/ModelAgent/ContextMemory/ToolsExtensions/Workspace/UiDiagnostics)中,UiDiagnostics 功能与其他页重复(Test LLM 重复 ModelAgent,Refresh Tool Registry 重复 Dashboard,About 重复 Dashboard footer,Open PROJECT.md 重复 ContextMemory),Description 撒谎(Chat UI preferences 但 DrawChatUiCard 已被 ADR-17 删除)。
- **迁移**:
  - Test mem0 / Test LightRAG → [`ContextMemorySettingsPage`](Editor/Config/Settings/Pages/ContextMemorySettingsPage.cs)(MemoryService/KnowledgeBase 卡片新增 Test 按钮)
  - Open Logs / Reset Settings / Clear Secure Keys → [`DashboardSettingsPage`](Editor/Config/Settings/Pages/DashboardSettingsPage.cs)(QuickActions 卡片扩展为 3 行)
  - 删除 UiDiagnosticsSettingsPage.cs + .meta(git rm)
- [`AgentCoreSettingsProvider.BuildPageList`](Editor/Config/AgentCoreSettingsProvider.cs) 移除 UiDiagnostics 注册。

### Notes
- B2(ClarificationOptionCard 可点击澄清选项)仍推迟至 beta。
- 集成验证(GLM-5.2 逃逸 + reasoning 注入 + 1M 窗口 + 低性能模型 Self-Challenge 回退)待执行。

## [1.5.0-alpha2] - 2026-07-09

### Fixed
- **Self-Challenge 卡片会话恢复丢失**：Domain Reload / 切换会话 / 重开 Unity 后，历史 assistant turn 的 [`SelfChallengeCard`](Editor/UI/Components/SelfChallengeCard.cs) 不再随对话内容一起重建。
  - 根因：[`ChatWindow.Messages.cs`](Editor/UI/ChatWindow.Messages.cs) 中 `RebuildMessageBubbles` 恢复 assistant turn 时未调用 [`AssistantTurnView.EnsureSelfChallengeCard`](Editor/UI/Components/AssistantTurnView.cs) — 数据层（[`SessionData.SerializableConversationTurn.SelfChallenge`](Editor/Session/SessionData.cs)）已正确读写，纯 UI 恢复路径缺口。
  - 修复：在 `RebuildMessageBubbles` 的 assistant 分支恢复 bubble 之后、恢复 ToolCallGroup 之前，接入 `EnsureSelfChallengeCard + SetData(turn.SelfChallenge)`；`turn.SelfChallenge == null` 时跳过，v1.4.x 及以前的旧会话向前兼容。
- 场景覆盖：Session 切换、Domain Reload（Unity 编译回来）、重开 Unity Editor。

### Impact
- 用户可见：进入历史会话时，之前触发过 Self-Challenge 的助手气泡上方会重新出现 Verdict 徽标 + 摘要 + 可展开完整块。
- 无破坏性变更：新会话流程 / 实时事件路由（[`ChatWindow.SelfChallenge.cs`](Editor/UI/ChatWindow.SelfChallenge.cs)）不受影响。
- 无数据迁移：v1.5.0-alpha1 已有的 session JSON 直接生效，无需重建索引。

### Notes
- 仅 1 个文件、4 行接线；数据层在 alpha1 就已就绪，属于 alpha1 收尾遗漏。
- 相关背景：[`plans/self-challenge-implementation-report.md`](plans/self-challenge-implementation-report.md) Stage 10 遗留项之一。

## [1.5.0-alpha1] - 2026-07-09

### Added — Self-Challenge (Phase 9) 完整实施
- **Node A (Intent Self-Challenge)**: 每轮用户消息时让 LLM 挑战对需求的理解 — Prompt 注入 + 流式抽取 + 结构校验 + Correction retry + fallback
  - [`IntentChallengePromptBuilder.cs`](Editor/Core/SelfChallenge/IntentChallengePromptBuilder.cs) / [`IntentChallengeStreamExtractor.cs`](Editor/Core/SelfChallenge/IntentChallengeStreamExtractor.cs) / [`IntentChallengeParser.cs`](Editor/Core/SelfChallenge/IntentChallengeParser.cs)
- **Node B (Answer Self-Challenge)**: 输出前独立 reviewer LLM 调用审视 draft — Reviewer prompt + draft-quote 校验 + Verdict (PASS/REVISE/BLOCK) 三分支处理
  - [`AnswerChallengePromptBuilder.cs`](Editor/Core/SelfChallenge/AnswerChallengePromptBuilder.cs) / [`AnswerChallengeStreamExtractor.cs`](Editor/Core/SelfChallenge/AnswerChallengeStreamExtractor.cs) / [`AnswerChallengeParser.cs`](Editor/Core/SelfChallenge/AnswerChallengeParser.cs)
- **WaitingForClarification 状态机** ([`MessageTypes.cs`](Editor/Core/MessageTypes.cs)): Node A Step 4 结论 = 反问用户时进入; Tool loop 拒绝分派工具; ChatWindow 状态标签"等待你的澄清..."
- **Continuation 模式**: Node A 反问后用户回复走精简 Step 3-cont/4-cont/5-cont; 支持 [TOPIC CHANGE DETECTED] 降级
- **主对话历史清理** (v0.10 §0.6): assistant message 写入历史前剥离 `<intent_challenge>` / `<answer_challenge>` / `<intent_challenge_continuation>` 块, 避免长对话 token 累积
- **SelfChallengeCard UI** ([`SelfChallengeCard.cs`](Editor/UI/Components/SelfChallengeCard.cs)): 挂在 ThinkingDrawer 与 ToolCallGroup 之间; Verdict 徽标(通过/已修正/已阻止/等待澄清/未触发) + 简短摘要 + 可展开完整数据; 术语已白话中文化对齐 ChatWindow 状态标签风格
- **AgentLoop 集成层** ([`AgentLoop.SelfChallenge.cs`](Editor/Core/AgentLoop.SelfChallenge.cs)): 集中承载所有 Node A/B 生命周期管理, ~590 行

### Changed — ADR-17 极简即开即用哲学落地
**推翻**: 设计文档 v0.10 §3.4/§5/§7.1 (用户可控性/可观测性优先) — 详见 [`plans/adr-17-minimalism.md`](plans/adr-17-minimalism.md)

**AgentCoreSettings 字段清理**:
- **彻底删除 9 个字段**: `intentChallengeEnabled` / `answerChallengeEnabled` / `answerChallengeMaxRetries` / `allowAgentClarificationQuestions` / `legacySelfChallengeDisabled` / `selfChallengeCardCountForcedExpansion` — 合并到统一 `selfChallengeEnabled` 总开关 + 内部常量; `workspaceRootOverride` / `unityRootRelativePathOverride` — 依赖自动检测; `userId` — 已 deprecated
- **新增 1 个字段**: `selfChallengeEnabled` (默认 true, 即开即用)
- **添加 [HideInInspector] 25+ 个字段**: `maxToolCallRounds` / `maxTokenBudget` / `fallbackRoutingEnabled` / `autoCompileCheck` / `autoConsoleCapture` / `maxConsecutiveErrors` / `toolFailWarningThreshold` / `toolFailBlockThreshold` / `allToolsFailBlockThreshold` / `bootstrapEnabled` / `autoProjectContext` / `maxContextTokens` / `reserveResponseTokens` / `toolResultCompressionThreshold` / `toolResultTargetTokens` / `conversationCompressionTrigger` / `useSeparateCompressionLLM` / `compressionLLMEndpoint` / `compressionLLMModel` / `enableReasoningOutput` / `reasoningEffort` / `reasoningMaxTokens` / `extraRequestBody` / `streamingEnabled` / `showToolCallDetails` / `workspaceAutoDetectEnabled` / `workspaceConfigVersion` / `autoMemoryEnabled` / `autoMemoryMinTurns` / `disabledToolCategories` / `disabledTools` / `toolScopingEnabled`
- **SelfChallengeConfig 新增 4 个常量**: `NodeARetryMax = 2` / `NodeBRetryMax = 2` / `AllowClarificationQuestions = true` / `CardForcedExpansionCount = 5`

**Settings 版本迁移 v17 → v18**: 清理孤儿字段, 无破坏性数据丢失。旧 session JSON 反序列化时已删除字段读到默认值忽略, 保留数据向后兼容。

**UI Settings 精简**:
- **Model & Agent 页面**: 删除 Agent Runtime 卡片(Max Tool Rounds / Token Budget / Fallback Routing) + Self Correction 卡片(Auto Compile Check / Auto Console Capture / Max Consecutive Errors); Model = "auto" 时显示实际选中模型
- **Context & Memory 页面**: 删除 Context Sources / Context Budget / Compression LLM 卡片; Compression 卡片简化为一个"启用压缩"总开关; mem0/LightRAG 文案白话化("长期记忆"/"项目知识库"); PROJECT.md/SOUL.ext.md 独立成"项目上下文文件"卡片
- **UI & Diagnostics 页面**: 删除 Chat UI 卡片; 新增"关于"卡片显示插件版本号
- **Workspace 页面**: 删除 Workspace Root Override / Unity Root Relative Path 字段, 完全依赖自动检测
- **Tools & Extensions 页面**: 保持现状(工具列表 UI 用户已习惯)

**核心逻辑改动**:
- [`WorkspaceRootResolver.cs`](Editor/Workspace/Resolution/WorkspaceRootResolver.cs): 删除对 `workspaceRootOverride` 的引用, 只做自动检测
- [`AgentLoop.SelfChallenge.cs`](Editor/Core/AgentLoop.SelfChallenge.cs): 所有对已删除字段的引用改用 `SelfChallengeConfig.NodeARetryMax` / `SelfChallengeConfig.AllowClarificationQuestions` / `SelfChallengeConfig.NodeBRetryMax` 常量

### Removed
- Statistics 面板 (v0.10 §5): 永不实施, 从 roadmap 移除
- 首周引导 tooltip (v0.9 §5.5): 永不实施
- Legacy Mode kill switch: 使用 `selfChallengeEnabled = false` 等效达成

### Notes
- **备份基线**: GitHub tag `v1.5.0-alpha1-pre-adr17` 是 ADR-17 重构前的完整状态
- **可回滚**: 关闭 `selfChallengeEnabled` 即回到 v1.4.9 骨架前行为
- **v0.10 部分推翻**: 上游设计文档 v0.10 §3.4/§5/§7.1 明确用户可控性/可观测性优先, 与本项目极简哲学冲突; ADR-17 明确本项目采纳一个总开关等效实现, 用户可观测性通过 SelfChallengeCard 被动感知实现, 不建 Statistics UI
- **相关文档**: [`plans/adr-17-minimalism.md`](plans/adr-17-minimalism.md) / [`plans/minimalism-audit-report.md`](plans/minimalism-audit-report.md) / [`plans/self-challenge-implementation-report.md`](plans/self-challenge-implementation-report.md) / [`plans/self-challenge-stage-plan.md`](plans/self-challenge-stage-plan.md)

## [1.4.9] - 2026-07-08

### Added
- **Phase 9 Self-Challenge 骨架基础设施（无用户可见行为变化）**：为 v1.5.0 计划中的 prompt 层幻觉护栏机制（[`plans/prompt-layer-hallucination-hardening-plan.md`](plans/prompt-layer-hallucination-hardening-plan.md) v0.10 定稿）铺设编译期基础设施。该机制核心（Node A / Node B 双节点 Self-Challenge、Reviewer 独立 LLM 调用、Verdict 分支处理、UI 卡片、Statistics 面板等）**将在 v1.5.0-alpha1 起分阶段交付**；v1.4.9 仅登记类型、字段、事件、配置项，无任何运行时行为改变。参见 [`plans/ROADMAP.md`](plans/ROADMAP.md) §3.y Phase 9 与 ADR-16。
- **[`Editor/Core/SelfChallenge/SelfChallengeData.cs`](Editor/Core/SelfChallenge/SelfChallengeData.cs)**：完整 schema，含 Node A 15 个字段 + Node B 10 个字段 + Metadata 3 个字段 + 7 个类型安全的 enum（`Step4Ambiguity` / `Step4Severity` / `Step4OperationRisk` / `Step4Attribution` / `Step4Conclusion` / `Step5Verdict` / `NodeBVerdict`）+ 1 个 `FallbackType` enum。JSON 序列化通过 `Newtonsoft.Json` 特性标注，全字段配 `NullValueHandling.Ignore` 保证向后兼容。
- **[`Editor/Core/SelfChallenge/SelfChallengeConfig.cs`](Editor/Core/SelfChallenge/SelfChallengeConfig.cs)**：工程侧硬编码常量集合（marker 字符串 / skip 阈值 / 结构校验最小值 / statistics 上限 / 首周引导展开次数）。与 [`AgentCoreSettings`](Editor/Config/AgentCoreSettings.cs) 中的用户配置互补——用户可改的走 Settings，marker/长度/格式类边界走 Config。
- **[`Editor/Core/SelfChallenge/SelfChallengeSkipRules.cs`](Editor/Core/SelfChallenge/SelfChallengeSkipRules.cs)**：Node A skip 判定规则实现（v0.9 §1.2.1 精简版：只保留 R1 消息长度 ≤15 字符 + R3 纯 URL；v0.8 曾包含的 R2/R4/R5 已按 v0.9 立场取消，理由是含关键词穷举违反"不做穷举"的核心立场）。R1 使用 `char.IsWhiteSpace` 去空白后按 Unicode 字符数计算，中英文一视同仁；R3 使用编译后的 `^\s*https?://\S+\s*$` 正则。API 采用 `ShouldSkip(msg, out reason)` 模式。
- **[`Editor/Tests/Core/SelfChallengeSkipRulesTests.cs`](Editor/Tests/Core/SelfChallengeSkipRulesTests.cs)**：Skip Rules 单元测试 20 case，覆盖 R1 边界（null / 空 / 全空白 / 中文短句 / 英文短句 / 空格穿插 / 恰好 15 字符 / 16 字符 / 长消息）、R3 边界（纯 URL / URL 带前后空白 / URL 前后夹杂文本 / 双 URL / 无 scheme URL）、R1 优先于 R3 的组合边界。
- **`AgentEventType` 4 个新枚举值**（[`Editor/Core/MessageTypes.cs`](Editor/Core/MessageTypes.cs)）：`IntentChallengeCompleted` / `AnswerChallengeCompleted` / `AnswerChallengeRegenerating` / `AnswerChallengeRegenerated`。骨架版本仅登记枚举与工厂方法，实际 `EmitEvent` 由后续 Stage 完成。
- **`AgentEvent` 新字段 + 4 个工厂方法**：`SelfChallenge` 字段（`SelfChallengeData` 类型）+ `TurnId` 字段；配套 4 个静态工厂方法与 3 个原 Phase 事件工厂并列。
- **`ConversationTurn.SelfChallenge` 属性 + `SerializableConversationTurn.SelfChallenge` 字段**（[`Editor/Session/SessionData.cs`](Editor/Session/SessionData.cs)）：Self-Challenge 数据随 Session JSON 序列化持久化；未参与 self-challenge 的 turn 为 `null`，UI 层遇 null 直接不渲染卡片。
- **`AgentCoreSettings` 6 个 Self-Challenge 字段**（[`Editor/Config/AgentCoreSettings.cs`](Editor/Config/AgentCoreSettings.cs)）：`intentChallengeEnabled` / `answerChallengeEnabled` / `answerChallengeMaxRetries`（Range 0-3，默认 2；仅结构校验 retry，不含 REVISE 重生成——REVISE 固定单次不复审）/ `allowAgentClarificationQuestions` / `legacySelfChallengeDisabled`（Legacy Mode 关闭开关）/ `selfChallengeCardCountForcedExpansion`（首周引导剩余展开次数，Range 0-20 默认 5）。
- **Settings 版本迁移 v16 → v17**：添加 Phase 9 骨架字段版本标记；所有字段使用声明时默认值，无实际数据迁移需求。

### Changed
- **[`Editor/Session/SessionData.cs`](Editor/Session/SessionData.cs)** 新增 `using AgentCore.Editor.Core.SelfChallenge;`；`SerializableConversationTurn.FromConversationTurn` 与 `ToConversationTurn` 增加 `SelfChallenge` 字段的传递。
- **[`Editor/Core/MessageTypes.cs`](Editor/Core/MessageTypes.cs)** 新增 `using AgentCore.Editor.Core.SelfChallenge;`；`AgentEvent` 私有构造函数增加 `selfChallenge` / `turnId` 两个可选参数。

### Notes
- **零用户可见变化**：所有新增字段默认 `false` / `null`；`intentChallengeEnabled` 与 `answerChallengeEnabled` 默认 **false**，需在 v1.5.0 之后由用户手动启用。骨架不接入任何执行路径。
- **向后兼容验证**：v1.4.x 及以前的 SessionData JSON 文件反序列化时 `SelfChallenge` 字段读到默认 `null`，`SerializableConversationTurn` 的所有已有字段序列化/反序列化行为不变；`SettingsData` 版本从 16 迁移到 17 只标版本号不修改任何已有字段。
- **未接入 Executor**：v1.4.9 **没有**创建 `IntentChallengePromptBuilder` / `IntentChallengeStreamExtractor` / `IntentChallengeParser` / `AnswerChallengeReviewer` 等执行组件。这些是 Stage 2-3（v1.5.0-alpha1/2）的范围。骨架版本仅确保后续 Stage 编码时"数据结构 + 事件通道 + 配置开关 + skip 规则"全部就位。
- **skip 规则可独立测试**：R1/R3 是纯静态方法，无外部依赖；v1.4.9 已提供 20 case 单元测试覆盖，供 Stage 2 调用方引用与回归验证。
- **v0.10 §0 收口决策已在骨架层锁定**：`SelfChallengeData.NodeASkipReason` / `NodeBSkipReason` 的字符串常量在 `SelfChallengeConfig` 中列出 6 种可能值（`R1_short` / `R3_url` / `short_response` / `pure_question` / `forced_termination` / `domain_reload_interrupt`），对应 v0.10 §0.3（强制终止 skip Node B）与 §0.5（domain reload 放行 draft）的两个新增 skip 原因；后续 Stage 只需消费这些常量。

## [1.4.8] - 2026-07-08

### Fixed
- **工具执行过程的详情内容不可选中/复制，且被静默截断，导致用户无法诊断问题**：用户反馈聊天窗口"工具执行过程"分组下的每一个工具调用卡片（[`ToolCallCard`](Editor/UI/Components/ToolCallCard.cs:1)）展开后的详情内容存在三个共生的 UX 缺陷：
  1. **详情框是 `Label`，Unity UI Toolkit 的 `Label` 默认不支持文本选择**——用户无法框选、右键或 Ctrl+C 复制任何内容
  2. **详情文本被硬编码截断到 200 字符**，之后拼一个 `"..."`，超过部分**永久丢失且不可恢复**——即使用户展开也看不到完整结果 / 错误信息 / 参数
  3. **详情容器 `maxHeight = 120` + `overflow = Hidden`**——即使内容没超过 200 字符，只要行数多也会被裁剪且没有滚动条，超出部分不可见
- **修复策略**（[`ToolCallCard.cs`](Editor/UI/Components/ToolCallCard.cs:1) + [`ChatWindow.Tools.cs`](Editor/UI/ChatWindow.Tools.cs:117) + [`ChatWindow.Messages.cs`](Editor/UI/ChatWindow.Messages.cs:310)）：
  1. **`Label` → 只读 `TextField`**：详情内容改用 `TextField { multiline = true, isReadOnly = true }`，原生支持文本选择、Ctrl+C 复制、Ctrl+A 全选。深色主题背景与边框做了显式覆盖，视觉与原 Label 一致。
  2. **详情容器包 `ScrollView`**：`ScrollView { mode = Vertical, maxHeight = 240px }` 替代原来的 `overflow = Hidden`——超过 240px 出现垂直滚动条，全部内容始终可访问，不再被裁剪。
  3. **完全移除 200 字符截断**：删除 [`ChatWindow.Tools.cs:119-122`](Editor/UI/ChatWindow.Tools.cs:117) 和 [`ChatWindow.Messages.cs:312-314`](Editor/UI/ChatWindow.Messages.cs:310) 的 `Substring(0, 200) + "..."` 逻辑，`SetDetails` 完整传入原始内容。同时移除 [`ToolCallCard`](Editor/UI/Components/ToolCallCard.cs:1) 构造函数里对 `arguments` 的 200 字符截断。
  4. **新增"复制"按钮**：卡片头部（工具名右侧、折叠箭头左侧）新增一个"复制"按钮，点击写入 `EditorGUIUtility.systemCopyBuffer`。按钮做了短暂绿色闪烁 + "已复制" 文字反馈（900ms），让用户明确知道复制成功。按钮点击事件已 `StopPropagation`，不会误触发卡片折叠切换。仅在有详情内容时显示（`DisplayStyle.None` when empty），避免 UI 噪音。
  5. **事件冒泡隔离**：`ScrollView`、`TextField`、`Button` 内的 `ClickEvent` 全部注册 `StopPropagation` 回调，确保用户在详情区选中文本、滚动、点复制按钮时不会误触卡片折叠动作。

### Impact
- **完整信息可访问**：`batch_execute` 等一次返回大量数据的工具，用户现在能看到完整的原始 JSON / 错误堆栈 / 参数 payload，不再丢失后 N 个字符。
- **可复制到剪贴板**：任何详情内容都可以通过（1）框选 + Ctrl+C 或（2）头部"复制"按钮一键复制。方便粘贴到 Issue、log 分析、外部诊断工具。
- **长内容不裁剪**：超过 240px 高的详情区会出现滚动条而不是被 overflow:hidden 吞掉。
- **无破坏性变化**：`SetDetails` 公开 API 签名不变；`ToolCallStatus` 枚举不变；折叠 / 展开的自动化行为规则保持一致。新增 `DetailsRaw` 只读属性供外部（未来的导出功能）访问原始文本。

### Added
- **[`ToolCallCard.DetailsRaw`](Editor/UI/Components/ToolCallCard.cs:1) 只读属性**：暴露当前卡片的完整原始详情文本，方便未来实现"整个 turn 一键导出"之类功能时读取。
- **卡片头部"复制"按钮**：使用 `EditorGUIUtility.systemCopyBuffer` 跨平台剪贴板 API，闪烁反馈使用 `VisualElement.schedule.Execute().StartingIn(900)` 避免依赖 Coroutine / `EditorApplication.update`。

### Notes
- **详情区最大高度选择**：`DetailsMaxHeight = 240f`（原为 120）——6~8 行常见结果可见，超过部分出滚动条。选 240 而非 unbounded 是因为多个工具卡片堆叠时太高会挤压聊天视野；用户觉得不够可以进一步展开使用完整 UI。
- **性能考虑**：`TextField` 的渲染开销略高于 `Label`，但 `ToolCallCard` 数量一般在几十以内（一个 turn 内几条），实测无感知。若未来出现超大规模工具调用序列（>500 张卡），可以考虑详情区做虚拟化，但目前不需要。
- **文本颜色配置**：`TextField` 的实际文本子元素样式由 `_detailsField.style.color` 控制，Unity 2021.3+ 会自动继承到内部的 `.unity-text-element`。若特定 Unity 版本仍显示为白色，可以额外用 USS class + `.unity-base-text-field__input > .unity-text-element` 选择器精调。
- **测试建议**：让 Agent 执行 `batch_execute` 或返回大量 JSON 的工具，展开卡片，验证：（1）能看到全部内容；（2）能框选文本；（3）Ctrl+C 复制的内容与"复制"按钮一致；（4）内容超过 240px 时出现滚动条。

## [1.4.7] - 2026-07-08

### Fixed
- **勾选/取消 Optional Component 后不会立即触发脚本编译，必须切换 Unity 窗口再切回来才编译**：用户反馈在 Project Settings → AgentCore → Tools & Extensions 中勾选或取消 VCS / Code Indexing 后，Editor 状态栏不显示编译进度，工具也不会立刻生效，必须把 Unity 窗口切走再切回（触发 focus lost → focus gained）才会开始编译。
- **根因**：[`OptionalComponentManager.SetDefine`](Editor/Extensions/OptionalComponentManager.cs:229) 在调用 `PlayerSettings.SetScriptingDefineSymbolsForGroup()` 之后立即调用 `CompilationPipeline.RequestScriptCompilation()`，但 Unity 2021.3~2022.3 上前者的写入是**延迟持久化的**——只打 dirty flag，实际序列化到 `ProjectSettings/ProjectSettings.asset` 要等 Editor 下一个 idle tick / focus lost 事件才发生。因此 `RequestScriptCompilation` 立即触发时，`CompilationPipeline` 读到的还是**旧 defines**，编译请求被内部去重丢弃。用户切窗口时 focus lost 事件强制 flush 了 PlayerSettings，Unity 自己检测到 defines 变了、这才启动编译。
- **修复策略**（[`OptionalComponentManager.cs`](Editor/Extensions/OptionalComponentManager.cs:229)）：在 `SetDefine` 检测到有变化后，按以下顺序执行：
  1. **反射调用 `PlayerSettings.SaveSettings()`**（Unity 2020.1+ API）强制立即持久化 PlayerSettings 到磁盘。用反射调用是为了兼容旧版本 Unity（若 API 不存在则跳过，走 fallback）。
  2. **调用 `AssetDatabase.SaveAssets()`** 作为通用兜底，flush 所有 dirty 的 ScriptableObject / ProjectSettings。
  3. **再调用 `AssetDatabase.Refresh(ForceUpdate)` → `CompilationPipeline.RequestScriptCompilation()`**：此时 CompilationPipeline 能读到最新 defines，会真正触发编译。
  4. **最后 `AgentCoreExtensionRegistry.Refresh()`** 让扩展注册表基于新状态刷新。
- **两个 flush 步骤都独立 try-catch**：即使反射调用或 SaveAssets 抛异常，也只 `LogWarning`，不影响后续 Refresh + RequestScriptCompilation 流程。属于渐进降级策略——最坏情况下退回到 v1.4.6 的行为（用户需要切窗口），不会更糟。

### Impact
- **VCS / Code Indexing 切换立即生效**：勾选后 Editor 状态栏立即显示编译进度，编译完成后工具在下一次会话立即可用；取消后同理，相关工具立即从 `ToolRegistry` 中移除。
- **无需额外用户操作**：不再需要"切窗口重新聚焦"这个 workaround。
- **对现有安装无副作用**：只影响 optional component 切换路径，其他所有代码路径不受影响。

### Notes
- **兼容性**：`PlayerSettings.SaveSettings()` 反射调用兼容 Unity 2019.4 到 2023.x（更旧版本 API 不存在时静默跳过，`AssetDatabase.SaveAssets()` 单独也足够 flush 大多数情形）。
- **未处理的边界情况**：如果用户在勾选后**立即关闭 Project Settings 窗口**（在 flush 完成之前），Unity 可能仍需 focus 事件才 tick。这是 Unity 内部行为，无法从插件层完全消除，但发生概率极低（<50ms 窗口）。
- **测试建议**：新装环境或删除 `Library/` 后打开工程，进入 Project Settings → AgentCore → Tools & Extensions，勾选/取消 VCS 或 Indexing，观察 Editor 状态栏应立即出现编译进度。

## [1.4.6] - 2026-07-08

### Fixed
- **VCS 组件在 1.4.5 全新安装场景依然未自动启用**（v1.4.4 修复的回归）：用户反馈全新安装 1.4.5 后 VCS 仍处于禁用状态。根因是**打包产物缺失关键 meta 文件**：`com.agentcore.unity-1.4.5.tgz` 里包含 [`OptionalComponentDefaultsBootstrap.cs`](Editor/Extensions/OptionalComponentDefaultsBootstrap.cs:1) 但**不包含** `OptionalComponentDefaultsBootstrap.cs.meta`。工作区里 `.meta` 的 `LastWriteTime` 是 `2026-07-08 11:33:50`（打包后才由 Unity 生成），而 `.tgz` 打包时该 meta 尚未存在。
- **Unity UPM 只读包行为**：Unity 在只读 UPM 包目录中遇到无 `.meta` 的 `.cs` 会**跳过编译**（无法就地生成 meta 到只读位置），因此 `OptionalComponentDefaultsBootstrap` 从未被编译进 `AgentCore.Editor` 程序集，`[InitializeOnLoadMethod]` **永远不会触发**。又因为 v1.4.4 出于职责单一化把 [`AgentCoreSettings`](Editor/Config/AgentCoreSettings.cs:24) 静态构造里对 `EnsureVcsDefaultForCurrentProject()` 的调用移除了，也没有 fallback，导致 VCS 在 1.4.5 全新安装场景下**永远不启用**。
- **修复策略**（双重保险 + 打包前后校验）：
  1. **恢复 [`AgentCoreSettings`](Editor/Config/AgentCoreSettings.cs:24) 静态构造中的 `OptionalComponentManager.EnsureVcsDefaultForCurrentProject()` 调用**作为 fallback 双写路径。`AgentCoreSettings` 是主 asmdef 核心类型，一定被编译，静态构造几乎必然被触发（用户打开 Project Settings 就会实例化），可以覆盖 `OptionalComponentDefaultsBootstrap` 因任何原因（打包错误、编译顺序、程序集裁剪等）不生效的场景。`EnsureVcsDefaultForCurrentProject` 内部通过 `EditorPrefs` 做项目级幂等标记（`AgentCore.VcsDefaultChecked.{projectHash}` / `AgentCore.VcsUserDisabled.{projectHash}`），两条路径同时触发不会造成任何副作用。
  2. **静态构造 fallback 独立 try-catch**：即使 fallback 内部抛异常也只 `LogWarning`，不影响 `MigrateSettings()` 正常执行。
- **【二次事故 & 修复】v1.4.6 首个打包版本引入了严重回归——`.npmignore` 里 `tools/` 规则缺失前导 `/`**：初始 1.4.6 tarball 内所有 `Editor/Tools/**` 都被误排除（`Native/` 99 条、`Cloud/` 8 条、`FileSystem/` 3 条、`Infrastructure/` 12 条、`Safety/` 22 条，共 ~145 个 `.cs` 文件），导致用户装机后目标项目**大规模编译失败**（`AgentCore.Editor.Tools` 命名空间整体缺失，`IAgentTool` / `ToolCallDispatcher` / `Mem0Memory` / `LightRAGDocument` 等全部找不到；`AgentCore.Indexing.Editor` 程序集也无法解析）。根因：minimatch/gitignore 语义下 `foo/`（无前导 `/`）匹配**任意深度**的 `foo/` 目录，而 Windows 文件系统大小写不敏感，`tools/` 意外匹配到了 `Editor/Tools/`。
- **修复策略（.npmignore + 打包后校验）**：
  1. `.npmignore` 中 `tools/` → `/tools/`（加前导 `/` 锚定到仓库根），同样处理 `tools.meta`。规则上方添加显式注释说明历史事故与规则语义，防止未来再犯。
  2. 新增 [`tools/verify-tarball.ps1`](tools/verify-tarball.ps1:1)：**打包后**对 `.tgz` 实际内容做结构校验，声明 26 个必须存在的路径（含最低文件数）+ 7 个必须不存在的路径。任何关键代码目录缺失或 dev-only 文件泄漏都会以 exit code 1 失败。
  3. `package.json` 新增 `postpack` 钩子：`npm pack` 后**自动**执行 `tools/verify-tarball.ps1`，把源码校验（prepack）+ 打包产物校验（postpack）串成一条完整的防线。`verify-meta.ps1` 只能查源，不能查打包出的 tarball——它是必要但不充分条件；`verify-tarball.ps1` 才能捕获 `.npmignore` glob 错配这类问题。

### Added
- **[`tools/verify-meta.ps1`](tools/verify-meta.ps1:1)**：打包前 meta 完整性校验脚本。扫描 `Editor/` 下所有 `.cs` / `.uxml` / `.uss` / `.asmdef` / `.md` / `.template` 文件及所有子目录，要求每一个都有对应 `.meta` 文件；缺失则以非 0 退出码失败并打印缺失清单。**当前工作区 263 files + 50 dirs 全部 meta 齐备**。
- **[`tools/verify-tarball.ps1`](tools/verify-tarball.ps1:1)**：打包后 tarball 结构完整性校验脚本。检查 26 个 required paths（每个含最低文件数）+ 7 个 forbidden paths（dev-only 不应泄漏的路径）。**当前 1.4.6 tarball 603 entries，全部检查通过**。
- **`package.json` `prepack` + `postpack` 钩子**：`npm pack` 会**自动**串行执行 pre-pack meta 校验 + post-pack tarball 结构校验，任一失败都会中断发布流程。同步暴露 `npm run verify-meta` / `npm run verify-tarball` 手动校验命令。
- **`.npmignore` 添加 `/tools/` 排除**（正确前导锚定形式）：仓库工具脚本不打包进用户 tarball。

### Changed
- **[`AgentCoreSettings`](Editor/Config/AgentCoreSettings.cs:24) 静态构造注释更新**：说明 v1.4.6 恢复 fallback 的理由和历史 context，以及 `EnsureVcsDefaultForCurrentProject` 的幂等性保证。
- **`.npmignore` 注释增强**：`/tools/` 规则上方添加显式警告，说明 gitignore/npmignore glob 语义与前导 `/` 的作用，防止未来再引入类似回归。

### Notes
- 已装 1.4.5 且 VCS 被永久锁在禁用状态的项目：升级到 1.4.6 后，只要用户没有主动通过 Settings 禁用过 VCS（即 `AgentCore.VcsUserDisabled.{projectHash}` 不存在），下次打开 Unity 时 fallback 路径会自动触发启用。
- 用户显式禁用的意图仍受 `AgentCore.VcsUserDisabled.{projectHash}` 保护，不会被自动重启用。
- **两条防线各自的作用范围**：
  - `verify-meta.ps1` (prepack)：捕获源码里 `.cs` 缺 `.meta` 的问题（v1.4.5 的 `OptionalComponentDefaultsBootstrap.cs.meta` 缺失就是这类）
  - `verify-tarball.ps1` (postpack)：捕获 `.npmignore` glob 错配、误排除关键目录、误泄漏 dev-only 文件（v1.4.6 首次打包 `Editor/Tools/` 整体丢失就是这类）
- **架构权衡说明**：本次修复恢复"双写幂等"违反了 v1.4.4 的"职责单一化"初衷，但换来了抗打包错误的鲁棒性。属于合理的工程权衡（单点故障 vs 双写幂等）。fallback 路径的额外成本可以忽略（一次 `EditorPrefs` 读取 + 一次 `HasDefine` 检查）。
- **教训**：任何形如 `foo/` 的 `.npmignore` 规则，**如果 `foo` 是常见目录名**（tools、src、tests、docs 等），必须加前导 `/` 锚定到仓库根。反之，如果确实想排除任意深度出现的 `node_modules/` / `__pycache__/`，才可以不加前导 `/`。

## [1.4.5] - 2026-07-08

### Fixed
- **写入/创建/删除类工具现在会真正请求用户确认**：用户反馈 Agent 未经批准即自动完成脚本创建和编辑。根因是 [`ToolRiskPolicy.Evaluate`](Editor/Tools/Safety/ToolRiskPolicy.cs:43) 存在两处设计问题：
  1. **`metadata.RequiresConfirmation` 声明形同虚设**：策略层把这个字段透传给 `ToolExecutionRisk`，但从未真正读它做决策。`ExecuteCodeTool` / `ManageBuildTool` / `ManagePackageTool` 明明显式声明了 `RequiresConfirmation = true`，也不会触发确认。
  2. **破坏性 action 检测过窄**：只识别 `delete` / `remove` / `destroy` 三个 token，且用子串匹配（`IndexOf`）。`write` / `create` / `copy` / `move` / `add_method` / `add_field` / `write_file` / `create_directory` 等实际写盘/改文件的 action 全部走 Allow 分支直通。
- **修复策略**（[`ToolRiskPolicy.cs`](Editor/Tools/Safety/ToolRiskPolicy.cs:1)）：
  1. 新增一条独立分支：`metadata.RequiresConfirmation == true` 直接返回 RequireConfirmation，让工具级声明真正生效。
  2. 破坏性 action 检测从 3 个 marker 扩展为完整的 `DestructiveActionTokens` 集合（write/create/overwrite/modify/update/replace/add_method/add_field/add/copy/move/rename/instantiate/duplicate/clone/install/uninstall/commit/push/revert/reset/checkout/merge/rebase 以及历史保留的 delete/remove/destroy）。
  3. 匹配算法从子串 `IndexOf` 改为 token-level 匹配（按 `_` 拆分 action 后逐 token 命中集合）。这样 `write_file` / `create_directory` / `add_method` 会被识别为破坏性；`read_file` / `list_directory` / `find_references` / `get_info` / `analyze` 保持只读直通。
  4. RequireConfirmation 分支的 `AllowedTrustScopes` 已包含 `SessionExactTarget`；ChatWindow 端 [`_trustedToolConfirmations`](Editor/UI/ChatWindow.Confirmation.cs:21) 缓存会让用户勾选"信任本会话相同目标"后，同一 tool+action+targets 的后续调用自动直通，避免烦扰。

### Impact
- **首次触发确认**：Agent 想改代码 / 写文件 / 复制 / 移动 / 创建目录时会在 Chat 面板底部弹出确认卡片，展示 tool 名 / action / risk / capabilities / 参数摘要 / 影响目标；用户点击 Approve 后才执行。
- **会话级免打扰**：确认卡上"信任本会话相同目标"选项复用 v1.4.0 已实现的 SessionTrust 机制，一次批准后本 ChatWindow 会话内该 tool+action+目标组合直通到会话被 Reset 或窗口关闭。
- **只读工具不受影响**：`read_file` / `list_directory` / `search_content` / `find_references` / `get_info` / `analyze` / `file_info` 等所有只读 action 保持零打扰。
- **测试兼容**：搜索确认 `Editor/Tests/` 下无代码引用 `DeleteActionMarkers` 或 `RequiresDeleteConfirmation`；`ToolCallDispatcherSchemaValidationTests` 只覆盖 schema 层，与策略层无耦合。

### Notes
- 如果新增工具希望**整个工具**都需要确认（如 `ExecuteCodeTool` / `ManageBuildTool`），继续在 `[AgentTool(..., RequiresConfirmation = true)]` 上声明。
- 如果只希望**特定 action** 需要确认，让 action 名包含 `DestructiveActionTokens` 里的任一 token，或走 delete/write/create 语义命名。
- 未来若需要按用户偏好切换"严格 / 精准"模式（例如所有 High risk 工具默认弹确认），可在 `AgentCoreSettings` 加开关，`ToolRiskPolicy.Evaluate` 已经把决策合并逻辑集中到一处，便于扩展。

## [1.4.4] - 2026-07-08

### Fixed
- **VCS 组件在新安装场景仍未自动启用（v1.4.3 后续修复）**：用户反馈全新安装插件后 VCS 依然是关闭状态。根因是 v1.4.3 的 [`OptionalComponentManager.EnsureVcsDefaultForCurrentProject`](Editor/Extensions/OptionalComponentManager.cs:119) 存在**"标记落下但 define 未写入"死锁**：`SetVcsEnabled(true)` → `PlayerSettings.SetScriptingDefineSymbolsForGroup` 在 Editor 启动早期可能静默失败（compilation pipeline 尚未就绪），但代码无论成功与否都会执行 `EditorPrefs.SetBool(checkedKey, true)`，导致下次启动因为标记已置位而永远跳过重试，VCS 被永久锁在禁用状态。
- **修复策略**：
  1. `EnsureVcsDefaultForCurrentProject` 现在**只在 `IsVcsEnabled()` 返回 true 确认 define 实际写入后**才设置 `VcsDefaultChecked.{projectPathHash}` 标记；写入失败时不设标记，下次 Editor 启动自动重试。
  2. 快速路径分离：如果 VCS 已启用，直接补齐标记后返回，避免重复 `SetScriptingDefineSymbolsForGroup` 调用（该 API 会触发 `RequestScriptCompilation`）。

### Added
- **[`OptionalComponentDefaultsBootstrap`](Editor/Extensions/OptionalComponentDefaultsBootstrap.cs:1)**：新增独立的 `[InitializeOnLoadMethod]` 引导器，独立于 `AgentCoreSettings` 静态构造运行。修复了另一条失败路径：`ScriptableSingleton<AgentCoreSettings>.instance` 的访问时机依赖于哪段 Editor 代码先加载，某些环境下静态构造未被触发时旧逻辑完全不会运行。新 Bootstrap 使用 `EditorApplication.delayCall` 推迟到下一 tick 执行，确保 `PlayerSettings` 已就绪；异常被捕获并以 `LogError` 上报，不会污染 Editor 启动流程。

### Changed
- **[`AgentCoreSettings` 静态构造](Editor/Config/AgentCoreSettings.cs:24)**：移除对 `OptionalComponentManager.EnsureVcsDefaultForCurrentProject()` 的直接调用（现由 `OptionalComponentDefaultsBootstrap` 负责）。静态构造现在只负责 `MigrateSettings()`，职责单一化。历史迁移路径（v12→v13 / v15→v16 的 `ApplyVcsDefaultEnablement`）保留不动，作为兼容层。

### Notes
- 已升级过 v1.4.3 且当前 VCS 被"死锁在禁用状态"的项目：升级到 v1.4.4 后，下次 Editor 重启会自动检测到 `checkedKey` 未设置（因为 v1.4.3 那次尝试实际失败）并重新触发启用。若手动删除 `EditorPrefs` 中的 `AgentCore.VcsDefaultChecked.*` 项，也可强制立即重试。
- 用户显式禁用 VCS 的意图仍受 `AgentCore.VcsUserDisabled.{projectPathHash}` 保护，不会被自动重启用。

## [1.4.3] - 2026-07-07

### Fixed
- **VCS 组件未自动启用（新项目场景）**：修复了在已安装过 AgentCore 的机器上，为新项目安装 AgentCore 时 VCS 可选组件未被自动启用的问题。根因是 [`AgentCoreSettings`](Editor/Config/AgentCoreSettings.cs:22) 使用 `ScriptableSingleton<T>` + `FilePathAttribute.Location.PreferencesFolder`，`Settings.asset` 存储在 Unity **全局** PreferencesFolder（跨项目共享），因此基于 `settingsVersion` 的版本迁移策略（v12→v13 / v15→v16 中的默认启用 VCS 逻辑）在每台机器上只会执行一次；用户在第二个项目安装 AgentCore 时 `Settings.asset` 已经是 CurrentVersion，迁移被跳过，`AGENTCORE_VCS` define（存于**项目级** `PlayerSettings`）从未被写入新项目。

### Added
- **`OptionalComponentManager.EnsureVcsDefaultForCurrentProject()`**：项目级 VCS 默认启用检查。每个 Unity 项目独立记录"是否已应用过默认启用"到 `EditorPrefs`（key 前缀 `AgentCore.VcsDefaultChecked.{projectPathHash}`），确保新项目安装时即使全局 `Settings.asset` 已经迁移完毕，仍会为当前项目独立触发一次 VCS 启用。项目路径通过 `SHA256(Application.dataPath)` 的前 16 hex 字符标识。
- **`OptionalComponentManager.RecordVcsUserIntent(bool enabled)`**：记录用户在 Tools & Extensions Settings 中手动切换 VCS 的意图。用户主动禁用后写入 `AgentCore.VcsUserDisabled.{projectPathHash}` 标记，之后的 `EnsureVcsDefaultForCurrentProject` 将尊重用户选择，不再自动启用。
- **[`AgentCoreSettings`](Editor/Config/AgentCoreSettings.cs:24) 静态构造器新增项目级检查触发点**：`static AgentCoreSettings()` 中的 `EditorApplication.delayCall` 除了触发 `MigrateSettings()` 外，还会调用 `OptionalComponentManager.EnsureVcsDefaultForCurrentProject()`。两者独立触发，`MigrateSettings` 负责机器级一次性迁移，`EnsureVcsDefaultForCurrentProject` 负责项目级幂等检查。
- **[`ToolsExtensionsSettingsPage.SetComponentEnabled`](Editor/Config/Settings/Pages/ToolsExtensionsSettingsPage.cs:408) 的 vcs 分支**：用户手动切换 VCS 时同步调用 `RecordVcsUserIntent(enabled)`，把用户意图持久化到 `EditorPrefs`，避免下次启动被自动重新启用。

### Changed
- 保留 [`vcsDefaultEnabled`](Editor/Config/AgentCoreSettings.cs:38) 字段和 v12→v13 / v15→v16 迁移逻辑作为历史兼容路径（不删除，避免破坏已工作场景）；新的项目级机制独立运行且幂等。

## [1.4.2] - 2026-07-07

### Changed
- **Settings UI — Context & Memory 页面重构服务卡片**：mem0（Memory Service）和 LightRAG（Knowledge Base）改为「Enabled 开关 + 启用后展开明细字段」的渐进披露模式，与 Separate Compression LLM 保持完全一致。默认关闭时只显示服务名称 + `○ Disabled` 状态徽标 + Enable 复选框，Endpoint / API Key / User ID / Auto Memory 等字段全部隐藏；启用后自动展开配置字段，Auto Memory 二级选项以 Foldout 收起（`FoldoutDefaults.Advanced`）。视觉密度对未配置服务下降约 40%。
- **`FoldoutDefaults` 常量统一 Foldout 默认策略**：新增 [`FoldoutDefaults`](Editor/Config/Settings/AgentCoreSettingsState.cs:8)（`ServiceConfig=true` / `Advanced=false` / `ReadOnlyInfo=false` / `ToolCategory=false`），所有 page 的 foldout 默认状态应通过此常量决策，避免各 page 各自决定。
- **`AgentCoreSettingsUi.DrawServiceCard()`**：新增标准化服务卡 helper，封装 "Header(Title + Status Badge) → Description → Enable Toggle → Enabled Body"。当 `enabled = false` 时 body 完全不渲染，减少空 field 视觉噪音；`enabled = true` 时状态徽标转绿并可显示 endpoint 提示。
- **`AgentCoreSettingsUi.DrawServiceStatusBadge()`**：新增紧凑 `● Enabled` / `○ Disabled` 徽标，Dashboard 与服务卡共用一套视觉语言。
- **Dashboard 合并 Package Info 到 Setup Status**：`Package` 卡片删除，包名 + 版本以灰色 miniLabel 出现在 Setup Status 卡片底部，节省一整张卡片。同时 Disabled 服务在 Setup Status 中改用 `○ Service: Disabled` 灰色前缀而非默认色，一眼可与 Enabled（绿色 `●`）区分。
- **UI & Diagnostics — Test 按钮改等宽**：三个 Test 按钮从固定 120px 改为 `MinWidth=140 / MaxWidth=180`，"Test LightRAG" 不再被截断。`Refresh Tool Registry` 与 `Open Logs` 合并到同一横行，减少一条空 layout。
- **AGENTS.md §10.1 表述与实际架构对齐**：Settings 章节从「shell + section registry」改写为「shell + top-tab pages + cards」，添加 `FoldoutDefaults` 约束、`DrawServiceCard` 强制要求，说明历史 Section/Registry 已于 v1.4.2 移除。

### Removed
- **死代码清理**：删除 `Editor/Config/Settings/AgentCoreSettingsRegistry.cs`、`IAgentCoreSettingsSection.cs`、`SettingsSectionBase.cs` 和 `Sections/` 整目录（11 个 Section 类：General/Model/Agent/Context/ContextManagement/Memory/Knowledge/Extensions/Tools/Interface/Diagnostics）。共 **14 个 .cs 文件 + 14 个 .meta**。这些类自 Provider 从 Section-based 迁移到 Page-based（v1.0.0 之前）后已不被任何代码引用（`grep AgentCoreSettingsRegistry.` 与所有 `SettingsSection` 类名的项目级搜索均只命中待删除文件内部）。
- **迁移路径明确**：若第三方 extension 之前实现了 `IAgentCoreSettingsSection`（实际上无此案例），应改为 `IAgentCoreSettingsContribution` 挂载到 Tools & Extensions page。

## [1.4.1] - 2026-07-07

### Changed
- **`package.json` 补齐仓库元数据**：填充 `documentationUrl` / `changelogUrl` / `licensesUrl` 指向 GitHub 仓库（[BakaAkari/agentcore-unity](https://github.com/BakaAkari/agentcore-unity)），新增 `repository` 与 `bugs` 字段。Unity Package Manager 中现在可以直接跳转到仓库主页、CHANGELOG、LICENSE 与 Issues。无代码变更。

## [1.4.0] - 2026-07-07

### Added
- **`search_code::diagnose`**：一键索引诊断 action，返回后台服务状态 + workspace 摘要 + 每个 root 的动态状态 + 中文可读 advice。LLM 在搜索落空时应先调用此 action 判断根因，不再"闷头再搜"。
- **`search_code::list_root_states`**：列出所有 root 的当前 IndexState (`NotIndexed / Indexing / Ready / Stale / Failed`)、Priority、file/symbol count 与 last_indexed_at。
- **`search_code::mark_stale`**：按 `root_id` / `scope_type` / `scope_name` 强制把匹配 root 标为 Stale，并把其下所有已索引文件塞回脏队列，下轮后台任务触发时会自动重新索引。
- **`search_code::status` 附带 per_root_state 数组**：现有 status action 保持向后兼容，同时携带 v1.4.0 的 per-root 状态摘要。
- **`IndexRoot` 增加 6 个运行时字段**：`IndexState / LastIndexedAt / LastIndexError / IndexedFileCount / IndexedSymbolCount / Priority`。字段落盘到 `IIndexStore` metadata KV（`root:{rootId}:*`），不修改 store schema，JSONL 与 SQLite 后端均无迁移成本。
- **`IndexingSchedulePolicy`**：按 `IndexRootRole` 三档划分调度优先级 —— `Foreground`（EditableProjectCode / SharedCode，前台优先）/ `Background`（WorkspacePackage / ToolingCode / CustomPlugin，后台闲时）/ `OnDemand`（CommercialPlugin / EngineCode / GeneratedCode / ReadOnlyReference，跳过自动增量）。
- **`IndexRootStateStore`**：per-root 状态持久化与在内存缓存的抽象，走 `IIndexStore.SetMetadataAsync / GetMetadataAsync`；提供 `LoadAsync / SaveAsync / MarkReadyAsync / MarkFailedAsync / RefreshAndMarkReadyAsync / ApplyStatesToRootsAsync` 等便捷方法。
- **`IndexingStatusBlockBuilder`**：在会话首轮 workspace snapshot 中注入 "Index Status" 块，展示后台服务全局状态 + roots 分类清单（Participating vs OnDemand）。设计上不做任何 store I/O，per-root live state 交由 LLM 主动 pull（`search_code::status/diagnose`）。
- **`IndexingPanel` 增加 "Indexing Roots" 折叠区**：Editor UI 只读展示所有 root 的 DisplayName / ScopeType / Role / Priority；Reindex/MarkStale 交互仍走 Chat 内 `search_code` action。
- **SOUL.md §4 新增一条 Context Awareness 规则**：明确当 workspace snapshot 出现 "Index Status" 块时，LLM 如何区分 Participating vs OnDemand roots，以及搜索落空时先 `diagnose` 再下结论的行为闭环。

### Changed
- **`IndexingStatusSnapshot` 新增 `NextRunAt` / `ReasonPaused` 字段**（非破坏）：Publish 时携带下次运行时间点与暂停原因，UI 与工具可一致展示 burst backoff / failure backoff / quiet delay 三种暂停语义。
- **`IndexingDirtyTracker` 引入 Burst Detection**：单次 `AddChanged/AddDeleted` 调用超过 `BurstThreshold`（默认 500）时，触发 `BackgroundIndexService.NotifyBurstDetected` 进入 `BurstBackoffSeconds`（默认 60s）暂停窗口，避免分支切换 / 代码格式化 / 生成器批量运行时 Editor Update 卡顿。
- **`IndexingAutoSettings` 新增 `BurstThreshold` / `BurstBackoffSeconds` 配置项**，可在 IndexingSettings 中调整或置 0 禁用。
- **`BackgroundIndexService.RunOnceAsync` 接入 Priority 过滤 + per-root 状态更新**：脏文件按归属 root 的 `IndexRootPriority` 分流，`OnDemand` root 的路径直接 mark processed 不参与增量；索引开始时把受影响 root 标 `Indexing`，成功时通过 `RefreshAndMarkReadyAsync` 反查 file/symbol count 并标 `Ready`，失败时标 `Failed`。
- **`IndexRootResolver.Resolve` 现在会为每个 root 填充 `Priority`**：调用 `IndexingSchedulePolicy.ResolvePriority(root)`，供 BackgroundIndexService 消费。
- **`ProjectContextCollector` 拆为 Fast/Heavy 两条路径**（预留基础设施）：新增 `CollectFast` / `CollectHeavyAsync(ct)`，后者在后台线程执行磁盘扫描 + Roslyn 命名空间聚合，Unity API 数据由主线程预取快照。缓存按 workspace fingerprint + 5min TTL 生效，多次并发调用共享同一 in-flight task。当前 BootstrapLoader 仍使用轻量的 `Collect()`；`CollectExtended` 在缓存未命中时返回 Fast 版本并触发后台预热，缓存命中时返回完整版。
- **`WorkspaceSnapshotBuilder.Build` 追加 Index Status 块**：会话首轮 system message 中携带索引状态摘要（同步、零 I/O 版本）。

### Fixed
- **修复 AgentCoreSettings.OnEnable 内 `Save(true)` 触发 Unity 警告**：`ScriptableSingleton<T>.OnEnable` 上下文中调用 `Save(true)` 会触发 `"You may not pass in objects that are already persistent"`。改为通过 `EditorApplication.delayCall` 延迟到 OnEnable 完成后执行，警告消失且不影响迁移语义。
- **修复长文本消息气泡高度不自适应（Part 1: 静态 Label 路径）**：[`MessageBubble.uss`](Editor/UI/Components/MessageBubble.uss:151) 中 `#content-label { overflow: hidden }` 会阻止 Label 请求高度增长，导致 error/user 消息（走静态 Label 路径）的长文本溢出气泡底部。移除该属性 + `flex-shrink: 0` 确保父容器不会挤压气泡尺寸。
- **改进消息气泡对动态 block 元素的 layout 支持（Part 2: assistant streaming/block 路径）**：assistant 消息（走 [`StreamingTextElement.SetFinalText`](Editor/UI/Components/StreamingTextElement.cs:970)）在 SetFinalText 后动态添加 table/list/codeblock 等 block 元素时，Unity UI Toolkit 某些版本的父容器 background 高度未能同步扩展，导致部分内容溢出 bubble 灰色矩形。修复方案：`.message-bubble` 显式声明 `flex-shrink: 0 / flex-grow: 0 / height: auto`；[`MessageBubble.FinalizeContent`](Editor/UI/Components/MessageBubble.cs:180) 与 [`SetupStaticMode`](Editor/UI/Components/MessageBubble.cs:341) 在 SetFinalText 后调用 `ForceLayoutRefresh`——立即 + GeometryChangedEvent 触发 + 延迟 50ms 三重 `MarkDirtyRepaint` 兜底，覆盖 layout pass 延迟的 UI Toolkit 版本。
- **修复 ConversationCompressor 会摘要掉首轮 Workspace Snapshot / Deferred Context**：[`ConversationCompressor.DetermineCompressRange`](Editor/Core/Compression/ConversationCompressor.cs:165) 之前只保护第 1 条主 system prompt 与已有摘要消息，未保护首轮注入的 workspace snapshot（含 v1.4.0 的 Index Status 块）与 deferred context（Active Tools List + PROJECT.md）。当对话上下文使用率超过压缩阈值（默认 70%）后，这两类"跨轮次静态上下文" system 消息会被摘要掉，导致 LLM 在后续轮次问答中无法看到 Index Status 与项目上下文。现在通过 `[WORKSPACE_SNAPSHOT]` 和 `# Available Tools` 前缀识别并跳过它们，与摘要消息一样受压缩保护。

## [1.3.8] - 2026-07-06

### Added
- **工具调用重复循环刹车**：`AgentLoop.Runner` 新增同一工具对同一目标的重复调用检测。若 LLM 在同一文件/对象上反复调用同一工具（如 `manage_script` 连续修改同一个 `.cs` 文件），第 4 次发出警告，第 7 次强制中断工具循环并要求 LLM 向用户说明当前状态与卡点，避免跑满 200 轮上限。
- **SOUL.md 增加 Repetition brake 规则**：明确指示 LLM 在同一工具对同一目标调用超过 3 次仍无进展时，必须停止并向用户报告，而不是盲目重试。

## [1.3.7] - 2026-07-06

### Fixed
- **修复 VCS 默认启用未触发**：`AgentCoreSettings` 增加 `[InitializeOnLoad]`，在 Editor 启动后期强制加载 Settings 并执行迁移；新增 `vcsDefaultEnabled` 标记和 v15→v16 迁移作为兜底，确保新安装/升级用户都能自动启用 VCS。
- **修复 Extensions 设置页 Code Indexing 警告不显示**：将实验性警告移到 Optional Components 卡片顶部、折叠区之外，确保用户打开 Extensions 页面即可看到，不受 Component Cards foldout 折叠状态影响。
- **Indexing Hub 面板增加实验性警告**：在 Index 面板顶部增加红色警告文本，提示后台索引对大型项目性能的影响。

## [1.3.6] - 2026-07-06

### Changed
- **可选组件 define 写入全部 BuildTargetGroup**：`OptionalComponentManager.SetDefine` 现在会把 `AGENTCORE_VCS` / `AGENTCORE_INDEXING` 写入所有有效的 BuildTargetGroup，而不是仅当前 `selectedBuildTargetGroup`。切换 Build Target 后组件状态不再丢失。
- **VCS 默认启用迁移更健壮**：v12→v13 迁移提取为 `ApplyVcsDefaultEnablement()`，增加 try/catch 和一次延迟重试，避免 Editor 启动早期 `PlayerSettings` 未就绪时启用失败。
- **后台索引默认参数改保守**：`QuietDelayMs` 2000 → **15000**，`MaxBatchFiles` 200 → **50**，`YieldEveryNFiles` 5 → **1**，降低大型项目保存/导入后的瞬间卡顿。

### Fixed
- **修复长 import 期间工具调用无限挂起**：`ToolCallDispatcher.ExecuteOnMainThreadAsync()` 从 `EditorApplication.delayCall` 改为 `EditorApplication.update` + 30 秒超时保护。主线程被长 import / Domain Reload / 模态对话框阻塞时，工具调用会返回 `TimeoutException` 而不是永久等待。

## [1.3.5] - 2026-07-06

### Changed
- **默认启用策略调整**：v12→v13 设置迁移现在只默认启用 VCS 可选组件，不再默认启用 Code Indexing。Code Indexing 目前为实验性功能，需要用户在 Project Settings > AgentCore > Extensions 中手动开启。
- **Code Indexing 实验性警告**：在 Extensions 设置页 Code Indexing 组件卡片和组件描述中增加警告说明，提示后台索引在大型项目中可能显著影响 Editor 响应。

### Fixed
- **清理 `CodebaseIndexer.cs` 重复 using 语句**：移除 `System.Threading` / `System.Threading.Tasks` 的重复引用，消除 CS0105 编译警告。

## [1.3.4] - 2026-07-06

### Fixed
- **修复全量代码索引可能长时间卡死主线程**：为单文件 Roslyn 解析增加 5 秒超时保护（超时时跳过并记为 ErrorFiles）；索引循环每处理 10 个文件 `await Task.Yield()` 让出主线程，避免 UI 假死。

## [1.3.3] - 2026-07-03

### Fixed
- **修复打包产物 `CS0246` 编译错误**：已分发的 `com.agentcore.unity-1.3.2.tgz` 中 `AgentCoreSettings.cs` 缺少 `using System.Net.Http;` 和 `using System.Threading.Tasks;`，导致用户通过 Unity Package Manager 安装时编译失败。重新从当前源码打包，恢复正确的 using 语句。

## [1.3.2] - 2026-07-01

### Changed
- **工具连续失败安全机制全面改进**：将原有硬编码 `maxConsecutiveFailures=3` 的一刀切阻断改为两级响应模型：
  - **Warning 级别**（默认 3 次）：注入降级提示引导 LLM 换方法，但不中断循环
  - **Block 级别**（默认 6 次）：强制中断工具循环，要求 LLM 输出纯文本总结
  - **风险等级乘数**：低风险工具（ReadOnly/Low）获得 2 倍阈值宽容度
  - **用户消息自动重置**：用户发送新消息时失败计数自然归零，无需新建会话
  - **可配置阈值**：新增 `toolFailWarningThreshold`、`toolFailBlockThreshold`、`allToolsFailBlockThreshold` 三个设置项
- **Settings 版本**: v14 → v15

## [1.3.1] - 2026-07-01

### Fixed
- **窗口 resize/会话切换后渲染格式丢失**：修复拉宽聊天窗口或切换会话后，助手消息的标题、代码块、表格等 block rendering 退化为纯文本的问题。根因是 `CreateGUI()` 在 resize 时重新触发，`RebuildMessageBubbles` 以 `isStreaming: false` 重建 assistant 气泡，走入 `SetupStaticMode` 的纯文本 `FilterCompleted` 路径。修复方式：[`MessageBubble.SetupStaticMode()`](Editor/UI/Components/MessageBubble.cs:341) 对 assistant 角色改用 `StreamingTextElement.SetFinalText()` 路径，与流式完成后的渲染一致。
- **消除 8 个编译警告**：CS0219（未使用变量）、CS0618（obsolete API）、CS0414（未使用字段）— 通过删除死代码、添加 pragma suppress、让字段实际生效等方式修复。

### Changed
- **版本号**: `1.3.0` → `1.3.1`

## [1.3.0] - 2026-07-01

### Added
- **Token Budget 模式**：新增 `maxTokenBudget` 设置项，以 token 消耗量而非固定轮次作为工具循环的主要终止条件。设为 0 表示不限制（默认值），正整数表示累计消耗达到该值后触发软着陆总结。配合已有的对话压缩系统，可实现近似无限轮次的工具调用能力。
- **Token 消耗实时显示**：ToolCallGroup 头部摘要和轮次分隔线现在会显示累计 token 消耗（如 "45.2K tokens"），帮助用户直观了解 Agent 运行成本。
- **AgentEvent.TokensUsed 属性**：`LoopRoundStarted` 事件新增 `TokensUsed` 字段，传递当前累计 token 消耗到 UI 层。

### Changed
- **maxToolCallRounds 默认值提升**：从 50 提升至 200，作为硬安全网保留；Token Budget 成为主要循环控制手段。
- **Settings 迁移 v13→v14**：已有用户的 `maxToolCallRounds` 如果 ≤50 会自动升级至 200。
- **Settings UI**：Agent Runtime 区域新增 Token Budget 输入框；Max Tool Rounds 滑动条上限调整为 200。
- **版本号**: `1.2.5` → `1.3.0`

## [1.2.5] - 2026-06-30

### Fixed
- **ScrollView 多轮对话布局异常**：修复聊天窗口在多轮对话后消息气泡被压缩或出现大面积空白的问题。根因是 ScrollView 内部 `#unity-content-container` 默认 `flex-grow: 1` 导致子元素在 Flex 布局中被拉伸/挤压。修复方式：[`ChatWindow.uss`](Editor/UI/ChatWindow.uss:329) 中 `#message-scroll-view > #unity-content-container` 设为 `flex-grow: 0; flex-shrink: 0`；[`StreamingTextElement`](Editor/UI/Components/StreamingTextElement.cs:886) 自身同步设为 `flexGrow=0, flexShrink=0`，确保消息气泡按内容高度自然排列。

### Changed
- **ChatWindow partial 拆分**：提取 [`ChatWindow.UIHelpers.cs`](Editor/UI/ChatWindow.UIHelpers.cs:1) — 包含 `UpdateStatusLabel`、`SetSendEnabled`、`SetCancelVisible` 三个 UI 辅助方法，降低主文件行数。
- **版本号**: `1.2.4` → `1.2.5`

## [1.2.4] - 2026-06-30

### Fixed
- **聊天窗口分隔符换行问题**：重写 [`StreamingTextElement`](Editor/UI/Components/StreamingTextElement.cs:817) 使用混合 block 渲染 — 流式阶段保持单 Label（性能优化），finalize 时切换为 VisualElement 块布局。水平分隔线使用 `height: 1px` + `flex-grow: 1` 的 VisualElement 替代字符（═══/───），在窄窗口下不再换行。新增 ContentBlock 数据模型（Paragraph/Heading/HorizontalRule/CodeBlock/Table/List）和 `ParseMarkdownToBlocks()` 解析器。

### Changed
- **版本号**: `1.2.3` → `1.2.4`

## [1.2.3] - 2026-06-30

### Added
- **VCS / Code Indexing 默认启用**：升级到此版本时，自动通过 Settings 迁移（v12→v13）启用 VCS 和 Code Indexing 可选组件。已手动禁用的用户不受影响（迁移仅在 `settingsVersion < 13` 时执行一次）。
- **Hub 面板动态重建**：新增 `AgentCoreSettings.OnSettingsChanged` 事件，ChatWindow Hub 订阅该事件并在 mem0/LightRAG 启用状态变化时自动重建面板，无需关闭重开窗口。

### Fixed
- **Memory/Knowledge 面板不随服务禁用消失**：[`MemoryPanelContribution.CreatePanel()`](Editor/UI/Components/MemoryPanelContribution.cs:38) 和 [`KnowledgePanelContribution.CreatePanel()`](Editor/UI/Components/KnowledgePanelContribution.cs:38) 在对应服务未启用时返回 null，Hub 跳过该面板。配合动态重建机制，禁用服务后面板立即从 Rail 中消失。
- **Reasoning 参数默认值修正**：`enableReasoningOutput` 默认值从 `true` 改为 `false`，避免 Bedrock 等不支持 reasoning 扩展的 provider 返回 HTTP 400。
- **NormalizeAssetPath 路径重复**：[`ToolHelpers.NormalizeAssetPath()`](Editor/Tools/Infrastructure/ToolHelpers.cs:378) 对 `"Assets"` 根目录不再错误拼接为 `"Assets/Assets"`，修复 `ManageAssetTool` 搜索时的 "Folder not found" 错误。

### Changed
- **Settings 版本**: `CurrentVersion` 12 → 13
- **版本号**: `1.2.2` → `1.2.3`

## [1.2.2] - 2026-06-30

### Fixed
- **LightRAGTool 门控缺失**：[`LightRAGTool`](Editor/Tools/Cloud/LightRAGTool.cs:124) 现在正确检查 `lightragEnabled` 开关，与 `Mem0Tool` 行为对齐。之前仅检查 endpoint 是否为空，导致 `lightragEnabled = false` 但 endpoint 非空时仍尝试连接。
- **Emoji 字体警告污染 Console**：扩展 [`ContentFilter.SanitizeUnsupportedEmoji()`](Editor/UI/Components/StreamingTextElement.cs:92) 覆盖 BMP 内的 Miscellaneous Symbols（U+2600-U+26FF）和 Dingbats（U+2700-U+27BF）范围，解决 ✅❌⚠ 等字符触发 "Font does not contain Unicode" 警告的问题。同时在 `FilterStreaming()` 和 [`ThinkingDrawer`](Editor/UI/Components/ThinkingDrawer.cs) 中启用 emoji 过滤，确保流式输出和 reasoning 内容也不会触发警告。

### Changed
- **可选服务默认值对齐（仅影响新安装用户）**：
  - `mem0Endpoint` 默认值从 `http://localhost:8765` 改为空字符串 — 消除"看似需要部署本地服务"的误导。
  - `lightragEndpoint` 默认值从 `http://localhost:9621` 改为空字符串 — 同上。
  - `autoMemoryEnabled` 默认值从 `true` 改为 `false` — 与 `mem0Enabled = false` 语义对齐，用户启用 mem0 后再按需开启自动记忆。
- **Settings 版本**: `CurrentVersion` 11 → 12。现有用户不触发数据迁移，保留其已配置的 endpoint 值。
- **版本号**: `1.2.1` → `1.2.2`

## [1.2.1] - 2026-06-26

### Added
- **Request Enrichment 架构**：新增 [`RequestEnrichment`](Editor/LLM/RequestEnrichment.cs:1) 静态类，在 JSON 序列化层注入 `stream_options`、`reasoning` 参数和用户自定义 `extraRequestBody`，不污染强类型 `ChatCompletionRequest` 模型。
- **Reasoning 请求触发**：默认启用 `enableReasoningOutput`，向每次 LLM 请求注入 `"reasoning": {}` 参数，触发 OpenRouter / Claude API 等代理返回 `reasoning_content` 字段（v1.2.0 的 ThinkingDrawer 依赖此字段但从未收到数据，本版本修复）。
- **Settings UI — Request Enrichment 卡片**：Model Settings 页新增 "Request Enrichment" 配置卡片，包含 Enable Reasoning Output 开关、Reasoning Effort 下拉（default/low/medium/high）、Reasoning Max Tokens、Extra Request Body (JSON) 文本区（带实时格式校验）。

### Fixed
- **ThinkingDrawer 无数据**：v1.2.0 的 ThinkingDrawer 架构完整但始终无显示 — 根因是 `OpenAICompatibleClient` 的请求体缺少 `reasoning` 参数，代理服务器（OpenRouter 等）不会在响应中返回 `reasoning_content`。现通过 `RequestEnrichment.BuildEnrichedJson()` 替换两处 `JsonHelper.Serialize()` 调用，自动注入必要参数。

### Changed
- **版本号**: `1.2.0` → `1.2.1`，标记 Request Enrichment 修复 ThinkingDrawer 数据触发。
- **AgentCoreSettings**: `CurrentVersion` 升至 11；新增 `enableReasoningOutput`（默认 true）、`reasoningEffort`、`reasoningMaxTokens`、`extraRequestBody` 四个字段。
- **OpenAICompatibleClient**: 非流式（line 47）与流式（line 105）的 `JsonHelper.Serialize(request)` 替换为 `RequestEnrichment.BuildEnrichedJson(request, settings)`。

## [1.2.0] - 2026-06-25

### Added
- **Chat UI / ThinkingDrawer**：新增默认折叠的 [`ThinkingDrawer`](Editor/UI/Components/ThinkingDrawer.cs:1)，按 assistant turn 保留 reasoning / planning trace，标题显示 `思考中 · Ns` / `思考完成 · Xs`，展开时懒加载文本、折叠时清空 label 以降低长文本 UI 成本。
- **AssistantTurnView 固定布局**：新增 [`AssistantTurnView`](Editor/UI/Components/AssistantTurnView.cs:1)，统一 assistant 轮次顺序为 ThinkingDrawer → ToolCallGroup → MessageBubble，避免 reasoning、工具调用和最终回复在消息流中错序。
- **双来源 reasoning 提取**：新增 [`ReasoningFieldExtractor`](Editor/LLM/ReasoningFieldExtractor.cs:1) 与 [`VisiblePlanningTraceExtractor`](Editor/Core/VisiblePlanningTraceExtractor.cs:1)，同时支持 provider 结构化 reasoning 字段和 `---THINKING---` / `---ACTION---` 可见规划 trace。
- **Reasoning 事件链路**：[`MessageTypes.cs`](Editor/Core/MessageTypes.cs:46) 新增 `ReasoningToken` / `ReasoningCompleted` 事件与 `ThinkingTraceSource`、`VisiblePlanningTraceState`，[`StreamingResponseParser`](Editor/LLM/StreamingResponseParser.cs:1) 在原始 SSE chunk 中自适应抽取 reasoning token。
- **会话与 Domain Reload 持久化**：`ConversationTurn` / `SessionData` / `DomainReloadState` 新增 `Reasoning`、`ReasoningSource`、`ReasoningDurationMs`、`RawAssistantContent`、`PlanningTraceState`，用于 UI/session/archive 审计。

### Changed
- **版本号**: `1.1.0` → `1.2.0`，标记 Chat UI / ThinkingDrawer 双来源 reasoning 可观测性完成。
- **LLM 上下文安全**：结构化 reasoning 不再进入 assistant final content；可见规划 trace 在写入 `_messages` 前清洗，`RawAssistantContent` 仅持久化到 `ConversationTurn` / session，不进入后续 LLM 上下文。
- **工具调用 UI 归属**：`RunToolCallLoopAsync` 先进入 Thinking 状态再发 `LoopRoundStarted`，确保 `ToolCallGroup` 绑定到当前 `AssistantTurnView`，不再降级挂到根消息列表。
- **Domain Reload Streaming 恢复**：Streaming 中断恢复时只将清洗后的 assistant 可见内容写回 `_messages`，reasoning 与 raw content 仅恢复到 UI/session 层。

### Notes
- Visible Planning Trace 默认开启，只有内容开头严格匹配 `---THINKING---` 且存在 `---ACTION---` 时才抽取；代码块/引用示例和不完整 marker 保留为普通内容，避免误删回复。
- `RawAssistantContent` 是审计/恢复字段，不属于 LLM 历史；后续压缩、恢复或导出路径不得把该字段拼回 `_messages`。

## [1.1.0] - 2026-06-24

### Added
- **Phase 7 §3.1 — 后台静默 + 增量索引**：Code Indexing 新增后台自动增量链路，包含 `AssetPostprocessor` 变更触发、`Library/agentcore-indexing-dirty.json` 脏队列持久化、静默合并调度、`BackgroundIndexService` 后台执行和 `CodebaseIndexer.RunTargetedIncrementalAsync` 定向增量更新。
- **Indexing 状态可观测性**：新增 `IndexingStatusBus`、ChatWindow 工具栏状态 Chip、Code Indexing Panel 的 Auto Index 开关与 Session Pause/Resume 控制，后台索引 Pending/Running/Failed/Disabled 状态不阻塞 Chat 输入。
- **`search_code` 状态查询**：新增 `status` action，返回后台索引状态、dirty 数量、进度、最后错误、最后成功时间、连续失败次数和 session pause 状态；`SOUL.md` 与 `TOOLS.md.template` 已更新为后台索引感知工作流。
- **治理层 G.2 — ExecuteCodeTool 降权**：`execute_code` 工具新装默认禁用（加入 `disabledTools` 列表）；标记为 `Visibility = ToolVisibility.Restricted`，即使用户手动启用也不会默认暴露给 LLM，需通过 `request_tools` 主动激活。v9→v10 迁移逻辑保留旧用户的原有配置不受影响。
- **治理层 G.3 — ActiveToolScope / Lazy Tool Discovery**：
  - 新增 [`ToolVisibility`](Editor/Tools/Infrastructure/ToolVisibility.cs:1) 三级枚举（AlwaysVisible / OnDemand / Restricted），通过 `[AgentTool]` 特性的 `Visibility` 属性声明。
  - 新增 [`ToolScopeState`](Editor/Tools/ToolScopeState.cs:1) 会话级分类激活状态管理。
  - 新增 [`ToolScopeResolver`](Editor/Tools/ToolScopeResolver.cs:1) 根据 Visibility + ScopeState + Settings 解析当前应暴露的工具列表。
  - 新增 [`request_tools`](Editor/Tools/Native/Meta/RequestToolsTool.cs:1) 元工具（AlwaysVisible），LLM 通过 `list` 查看可用分类、通过 `activate` 激活按需工具。
  - `AgentLoop.BuildToolDefinitions()` 改为通过 `ToolScopeResolver` 解析，不再暴露全部工具。
  - 全量工具分类标注：12 Specialized + 10 Extended + 7 Utility + 2 Scripting + 1 Meta + 2 Cloud + 1 VCS + 1 Indexing → OnDemand；`execute_code` → Restricted；核心工具（场景/GameObject/组件/文件/脚本/资产/控制台/Bootstrap 等）保持 AlwaysVisible。
- **Settings 开关**：`toolScopingEnabled` 字段控制整体开关，默认启用；关闭后回退到全量暴露行为。
- **内嵌工具确认 UI**：ChatWindow 新增非模态工具审批面板与确认队列，工具执行需要用户确认时不再依赖系统级阻塞弹窗。
- **短期信任授权**：删除类工具确认面板新增 Session 级短期信任，同一工具、action、目标集合在当前 ChatWindow 生命周期内可直接通过。

### Changed
- **版本号**: `1.0.3` → `1.1.0`，标记治理层 G.2 / G.3 与 Phase 7 §3.1 后台静默 + 增量索引完成（Minor 升级：新增渐进暴露能力与索引体验深化）。
- **AgentCoreSettings**: `CurrentVersion` 升至 10；新增 `toolScopingEnabled` 字段。
- **ToolMetadata**: 新增 `Visibility` 属性，`WithRiskAndVisibility()` 方法。
- **ToolAutoDiscovery**: 自动读取 `[AgentTool].Visibility` 并传递到 ToolMetadata。
- **工具确认策略**: `AgentLoop` 改为注入 `IToolConfirmationProvider`，ChatWindow 默认使用内嵌确认提供者；移除阻塞式 Dialog 确认路径。VCS 友好策略下读写与工具执行默认通过，删除类 action 仍需确认。

### Notes
- Phase 7 §3.1 保持在 v1.1.0 内交付：不扩大默认工具暴露，不引入新存储/新协议，索引数据仍为本地 SQLite/JSONL 后端。
- G.2 + G.3 合并为 v1.1.0 发布：G.2 是 G.3 的子集（execute_code 的 Restricted 可见性依赖 G.3 的 ToolVisibility 机制）。
- 工具作用域在 `ResetConversation()` 时自动重置（通过 `Initialize()` 重新创建 `ToolScopeState`）。
- 按需工具的分类名称取自 `[AgentTool].Category`（不区分大小写），LLM 可通过 `request_tools list` 查看完整分类列表及工具数量。
- 关闭 `toolScopingEnabled` 后 `ToolScopeResolver` 回退为返回所有启用工具（等效旧行为）。

## [1.0.3] - 2026-06-24

### Added
- **治理层 G.1.d — 高危工具风险声明细化**：26 个工具文件的 `[AgentTool]` 特性全部显式声明 `RiskLevel`、`Capabilities`、`RequiresConfirmation`。高危工具（`manage_script` write/create/delete、`execute_code`、`manage_build` build、`manage_package` install/remove 等）现在会触发用户确认弹窗或直接拦截。
- **治理层 G.1.e — WorkspacePathPolicy 真正接入**：新增 [`ToolPathRiskResolver`](Editor/Tools/Safety/ToolPathRiskResolver.cs:1) 静态类，在工具执行前自动从参数中嗅探路径类字段（`path`、`file_path`、`script_path`、`asset_path` 等 16 种），解析为绝对路径后通过 [`WorkspacePathService.TryGetRootInfo()`](Editor/Workspace/WorkspacePathService.cs:48) 查找所属 WorkspaceRoot，再经 [`WorkspacePathPolicy.GetRisk()`](Editor/Workspace/Safety/WorkspacePathPolicy.cs:12) 映射为 `WorkspaceOperationRisk`。返回所有路径中的最坏风险等级。
- **ToolCallDispatcher 路径风险集成**：[`ToolCallDispatcher.DispatchAsync()`](Editor/Tools/ToolCallDispatcher.cs:258) 在策略评估前调用 `ToolPathRiskResolver.Resolve()`，将真实路径风险传入 [`ToolRiskPolicy.Evaluate()`](Editor/Tools/Safety/ToolRiskPolicy.cs:51) 的完整重载（含 `pathRisk` + `targets` 参数）。写入引擎代码/商业插件/只读引用等受保护区域的工具调用现在会被升级为 RequireConfirmation 或 Block。

### Changed
- **版本号**: `1.0.2` → `1.0.3`，标记治理层 G.1 全面完成（G.1.a ~ G.1.e）。
- **ToolCallDispatcher 策略评估调用**：从短重载 `Evaluate(metadata, toolName, action, paramSummary)` 切换为完整重载 `Evaluate(metadata, pathRisk, toolName, action, paramSummary, pathTargets)`，路径风险不再默认为 `Safe`。

### Notes
- G.1 治理层（Tool Risk Policy + WorkspacePathPolicy 强制接入）全部子任务已完成。所有工具调用现在同时受工具自身风险等级 + 目标路径位置双重管控。
- `ToolPathRiskResolver` 仅对声明了写入能力（`WriteProjectFiles | DeleteProjectFiles | ModifyScripts | ModifyAssets`）的工具执行路径嗅探；只读工具快速返回 `Safe`，零性能开销。
- 当 `WorkspaceContext` 尚未初始化时（首次 Editor 启动、解析中），路径风险退化为 `Safe`（fail-open），不阻塞正常操作。

## [1.0.2] - 2026-06-24

### Added
- **治理层 G.1.c — ToolCallDispatcher 策略执行接入**：[`ToolCallDispatcher.DispatchAsync()`](Editor/Tools/ToolCallDispatcher.cs:197) 在 schema 校验通过后、工具执行前统一调用 [`ToolRiskPolicy.Evaluate()`](Editor/Tools/Safety/ToolRiskPolicy.cs:179)，根据决策结果走三条路径：Allow（直通执行）、RequireConfirmation（弹窗确认）、Block（直接拒绝）。工具行为首次受到统一治理管控。
- **IToolConfirmationProvider 接口**：新增 [`IToolConfirmationProvider`](Editor/Tools/Safety/IToolConfirmationProvider.cs:1) 抽象，定义 `RequestConfirmationAsync(ToolConfirmationRequest, CancellationToken)` → `bool` 契约；支持生产环境和测试环境的不同确认策略。
- **DialogToolConfirmationProvider**：新增 [`DialogToolConfirmationProvider`](Editor/Tools/Safety/DialogToolConfirmationProvider.cs:1)，生产环境默认实现。通过 `EditorApplication.delayCall` + `TaskCompletionSource` 将 `EditorUtility.DisplayDialog` 阻塞调用正确 marshal 到主线程；超时 60 秒自动拒绝；null provider fail-safe 为自动拒绝。
- **AutoToolConfirmationProvider**：新增 [`AutoToolConfirmationProvider`](Editor/Tools/Safety/AutoToolConfirmationProvider.cs:1)，测试/自动化场景用，构造时传入固定 `bool` 决定一律 Allow 或一律 Reject。
- **ToolCallResult.Decision 扩展**：[`ToolCallResult`](Editor/Tools/ToolCallDispatcher.cs:35) 新增可空 `ToolPolicyDecision? Decision` 属性，承载每次调用的策略评估结果，供 AgentLoop 审计事件使用。
- **AgentEventType 审计事件**：[`MessageTypes.cs`](Editor/Core/MessageTypes.cs:46) 新增 `ToolConfirmationRequested` / `ToolBlocked` 两个事件类型及对应工厂方法 [`AgentEvent.ToolConfirmationRequested()`](Editor/Core/MessageTypes.cs:402) / [`AgentEvent.ToolBlocked()`](Editor/Core/MessageTypes.cs:427)；UI 层可据此展示确认弹窗记录或阻断审计日志。
- **AgentLoop 审计事件发射**：[`AgentLoop.Tools.cs`](Editor/Core/AgentLoop.Tools.cs:66) 在工具结果循环中，对 `RequireConfirmation` / `Block` 决策主动 `EmitEvent`，Allow 静默不发（减噪）。

### Changed
- **版本号**: `1.0.1` → `1.0.2`，标记治理层 G.1.c 落地 — ToolCallDispatcher 全面接入策略评估。
- **ToolCallDispatcher 构造函数**：新增可选第二参数 `IToolConfirmationProvider provider = null`；AgentLoop 初始化时注入 `DialogToolConfirmationProvider`。
- **ToolCallDispatcher.ToString() 输出**：policy 拒绝的调用在摘要中显示 `[POLICY:RequireConfirmation-Rejected]` 或 `[POLICY:Blocked]` 标签。
- **用户拒绝反馈消息**：追加固定后缀 `" Do not retry without changing approach."`，引导 LLM 不盲目重试被拒操作。
- **ROADMAP §0.4 / §2.x**：G.1.c 标记为已完成，G.1 整体状态更新为"G.1.a/b/c 已落地 v1.0.2"。

### Notes
- 本版本 **首次激活工具调用治理管道**。所有工具调用现在统一经过风险评估。当前默认 RiskLevel 为 `Medium`（Allow），因此大部分工具行为无感知变化；G.1.d 将细化 High/Destructive/CodeExecution 声明后，相关工具才会触发确认弹窗。
- `pathRisk` 参数当前默认传 `WorkspaceOperationRisk.Safe`（真实 WorkspacePathPolicy 接入在 G.1.e）。
- 确认超时 60 秒 = 自动拒绝 + LLM 收到 Fail 结果。

## [1.0.1] - 2026-06-23

### Added
- **LLM/Agent 架构安全收口准则**：新增并纳入 [`plans/llm-agent-architecture-remediation-plan.md`](plans/llm-agent-architecture-remediation-plan.md)，作为后续工具扩展、MCP、Plugin、文件写入自动化和 Agent 自治增强的前置治理文档。
- **治理层 G.1.a — Tool Risk Metadata 基础设施**：新增 [`Editor/Tools/Safety/`](Editor/Tools/Safety/) 目录，包含 [`ToolRiskLevel`](Editor/Tools/Safety/ToolRiskLevel.cs:1)（7 级风险枚举：ReadOnly / Low / Medium / High / Destructive / External / CodeExecution）、[`ToolCapability`](Editor/Tools/Safety/ToolCapability.cs:1)（14 位 `[Flags]` 能力枚举：ReadProject / WriteProjectFiles / DeleteProjectFiles / ModifyScene / ModifyAssets / ModifyScripts / ExecuteCode / InstallPackages / BuildPlayer / NetworkAccess / VersionControlWrite / BatchExecute / ModifyProjectSettings / ModifyAgentConfig）、[`ToolExecutionRisk`](Editor/Tools/Safety/ToolExecutionRisk.cs:1)（合并 ToolRiskLevel + ToolCapability + WorkspaceOperationRisk 的 readonly struct）、[`ToolPolicyDecision`](Editor/Tools/Safety/ToolPolicyDecision.cs:1)（Allow / RequireConfirmation / Block 决策结构）、[`ToolConfirmationRequest`](Editor/Tools/Safety/ToolConfirmationRequest.cs:1)（UI 二次确认载荷）。
- **治理层 G.1.a — Attribute / Metadata 扩展**：[`AgentToolAttribute`](Editor/Tools/Infrastructure/AgentToolAttribute.cs:1) 新增 `RiskLevel`（默认 `Medium`）、`Capabilities`（默认 `None`）、`RequiresConfirmation`（默认 `false`）三个属性；[`ToolMetadata`](Editor/Tools/IAgentTool.cs:24) 增加 G.1 构造重载并保留原 5 参数构造函数与默认值，新增 `WithRisk(...)` 克隆方法用于装饰器透传，全部向后兼容现有 51 个工具实现。
- **治理层 G.1.a — RiskEnrichedTool 装饰器**：[`ToolAutoDiscovery.RegisterToolType`](Editor/Tools/Infrastructure/ToolAutoDiscovery.cs:88) 引入私有装饰器 `RiskEnrichedTool`，自动把 `[AgentTool]` 上的 RiskLevel / Capabilities / RequiresConfirmation 透传到 `IAgentTool.Metadata`，无需修改任何现有工具源码即可在 ToolRegistry 中携带风险元数据。
- **治理层 G.1.b — ToolRiskPolicy 评估器**：新增 [`Editor/Tools/Safety/ToolRiskPolicy.cs`](Editor/Tools/Safety/ToolRiskPolicy.cs:1)，纯函数式策略评估器，合并工具风险（ToolRiskLevel + ToolCapability + RequiresConfirmation 声明）与路径风险（WorkspacePathPolicy → WorkspaceOperationRisk）形成统一 ToolPolicyDecision；置信阈值为 `RiskLevel >= High` 或 `WorkspaceOperationRisk >= MediumRisk`，并对 Delete / ModifyScripts / InstallPackages / BuildPlayer / VersionControlWrite / ModifyAgentConfig 等高敏能力强制要求二次确认；`IsCodeExecution` 始终走二次确认；`WorkspaceOperationRisk.Blocked` 直接 Block。该评估器不依赖任何 UI 状态，可被 Dispatcher / Headless / 未来 MCP / 测试复用。

### Changed
- **版本号**: `1.0.0` → `1.0.1`，标记治理层 G.1.a / G.1.b 落地（基础设施 + 策略评估器），尚未接入 Dispatcher 执行链路。
- **开发方向文档同步**：更新 [`plans/ROADMAP.md`](plans/ROADMAP.md)、[`plans/README.md`](plans/README.md)、[`AGENTS.md`](AGENTS.md) 与 [`plans/mcp-server-feasibility.md`](plans/mcp-server-feasibility.md)，明确 Phase 7 / Phase 8 仍是两个待开发产品模块，但实现前必须优先完成 Tool Risk Policy、WorkspacePathPolicy 强制接入、ExecuteCodeTool 降权和 Lazy Tool Discovery 等治理层 P0 收口。
- **MCP 启动条件收紧**：Phase 8 仍保持对外互操作定位，但不再表述为无条件"互不阻塞"；编码前必须满足治理层 G.1/G.2/G.3。
- **ROADMAP §2.x 治理层进度**：G.1 标记为"实现中（G.1.a / G.1.b 已完成，G.1.c Dispatcher 接入待编译验证后启动）"。

### Notes
- 本次版本仅完成 G.1.a / G.1.b 的代码骨架与策略评估器，**尚未接入 [`ToolCallDispatcher`](Editor/Tools/ToolCallDispatcher.cs:1)**，因此目前所有工具行为与 v1.0.0 保持一致（不影响线上 Agent 行为）。
- G.1.c（Dispatcher 接入）/ G.1.d（按 Category 细化高危工具风险声明）/ G.1.e（WorkspacePathPolicy 强制执行）将在编译验证通过后逐步推进。
- 默认风险等级策略：未在 `[AgentTool]` 中显式声明 RiskLevel 的工具按 `Medium` 处理；按 Category 的精细化下调（如 ReadOnly 类工具）将在 G.1.d 阶段完成。

## [1.0.0] - 2026-06-16

> **Phase 6 验收完成里程碑**。基于 v0.9.x 系列在真实 Unity 项目中的持续实战使用作为验收依据（详见 ROADMAP.md ADR-11）。本版本以版本号、文档对齐与 Phase 7 / Phase 8 派生定位为主，并合并两处来自 v0.9.x 实战中沉淀的源码层健壮性收尾修复。后续派生项目：Phase 7（对内）后台静默 + 增量索引（v1.1.0）、Plugin / Extension 系统、产品化分发；Phase 8（对外）MCP Server 互操作。

### Changed
- **版本号**: `0.9.9` → `1.0.0`，标记 Phase 6（智能化与体验）验收完成。
- **ROADMAP.md 主导方向重排**:
  - 头部状态更新为 "Phase 6 验收完成（v1.0.0），下一阶段 Phase 7（对内）+ Phase 8（对外）平行推进"。
  - §0.4 当前项目快照升级到 v1.0.0；新增 "Phase 6 验收" 行。
  - §0.5 历史 Phase 表新增 Phase 6 行（标记 `[x]`）；所有历史 Phase 增加状态列。
  - §1 战略目标表拆分为 Phase 6（已完成）/ Phase 7（对内）/ Phase 8（对外，与 Phase 7 平行）三行。
  - §2 Phase 6 任务全部标记完成；6.2.6 后台静默 + 增量索引迁移到 Phase 7 §3.1；6.5.1 Diff 视图改为外部 VCS 工具委托方案；6.5.2/6.5.3 主题/快捷键判定为低 ROI 不纳入。
  - §3 拆为 Phase 7（§3.1 后台索引 + §3.2 Plugin / Extension + §3.3 产品化分发）和 Phase 8（§3.x MCP 对外互操作 7 项任务）。
  - §5 风险评估更新：移除已废弃的 SmartToolRecommender 风险；新增后台索引脏队列、Domain Reload 状态丢失、MCP 跨进程攻击面、MCP 协议演进、Plugin 崩溃 5 条新风险。
  - §6 文档索引新增 [`indexing-background-incremental-design.md`](plans/indexing-background-incremental-design.md) 和 [`mcp-server-feasibility.md`](plans/mcp-server-feasibility.md) 两条上游设计文档。
  - §7 下一步行动建议刷新为 Phase 7 §3.1 / Phase 8 §3.x.1~4 / Phase 7 §3.3.1 / Phase 7 §3.2 四条。
- **新增 ADR-11**: v1.0.0 验收以"用户实战使用"为准，而非新增 QA 流程。
- **新增 ADR-12**: 文件变更 Diff 视图采用外部 VCS 工具委托方案（TortoiseSVN / P4V / `git difftool`），不在 Editor 内自建 side-by-side 视图。
- **新增 ADR-13**: MCP Server 设为独立 Phase 8，与 Phase 7 形成"对外/对内"对照，平行推进。
- **`mcp-server-feasibility.md`**: §9 ROADMAP 关系章节更新为"独立 Phase 8（与 Phase 7 平行）"。
- **`StreamingTextElement.Clear()` 重命名为 `ClearText()`**: 原方法名隐藏了 `VisualElement.Clear()`（清空子元素集合），语义不同；改名以消除歧义并修复 CS0108 隐藏告警。已扫描整个 `Editor/` 下无外部调用方，重命名零破坏（仍归属 v1.0.0 的 MAJOR 升级，不需额外补丁）。

### Fixed
- **`AgentCoreSettingsUi.DrawCard`**: 用 `try/finally` 包裹 `BeginVertical`/`EndVertical`，并在内层 `try/catch` 中保护用户提供的 `drawContent` 回调。回调异常不再破坏 IMGUI layout 平衡，异常会被记录到 Console 并以 `HelpBox` 提示，避免单卡片异常污染整个 Settings 页面。

### Notes
- 本版本主要为文档收尾 + 两处 `Editor/` 源码健壮性修复；不引入新功能模块，亦不修改任何工具行为或对话流程。
- Phase 7 §3.1 后台静默 + 增量索引设计已就绪（[`indexing-background-incremental-design.md`](plans/indexing-background-incremental-design.md)），但实现不得绕过后续治理层安全规则。
- Phase 8 §3.x MCP Server 与 Phase 7 在产品规划上平行；编码前需满足治理层工具风险、Workspace 边界和能力范围前置条件。

## [0.9.9] - 2026-06-15

### Changed
- **Tools & Extensions 设置页布局重构**：把每个可选组件渲染为自包含卡片（开关 + Define + 内联设置折叠区），消除"开关与详细设置被其他组件夹在中间"的体验断裂。
  - VCS 详细设置不再单独占一张卡，改为内联在 "Optional Components → Version Control" 卡片内的 Settings 折叠区，与开关物理相邻。
  - 独立的 "Version Control" 设置卡已移除；Indexing 等未提供 contribution 的组件在卡内显示一行说明，告知"通过 AgentCore Hub 配置"。
  - 原 "Extension Settings" 卡更名为 "Other Extension Settings"，仅在存在不归属任何组件的 contribution 时才显示，否则整张卡隐藏，让页面更聚焦。
- `IAgentCoreSettingsContribution` 接口新增 `OwnerComponentId` 属性，contribution 通过返回 component id（如 `"vcs"`）声明归属，未归属时返回 null。
- `VcsSettingsContribution` 显式实现 `OwnerComponentId => "vcs"`。

### Fixed
- **`AgentCoreSettingsUi.DrawCard` 的 IMGUI layout 失衡**：内容回调（`drawContent`）抛异常会导致 `EndVertical` 不被调用，触发 Unity Console 频繁报错 `EndLayoutGroup: BeginLayoutGroup must be called first.`。现用 `try/finally` 包裹保证 `EndVertical` 始终执行，回调内异常被捕获并以 `HelpBox` 错误形式显示在卡片中，同时通过 `Debug.LogException` 输出到 Console。
- **`StreamingTextElement.Clear()` 方法隐藏基类警告（CS0108）**：`StreamingTextElement` 继承 `VisualElement`，自身的 `Clear()`（清空文本内容）与基类 `Clear()`（清空子元素）语义不同，造成意图混淆。现重命名为 `ClearText()`，明确语义并消除编译警告。该方法目前无外部调用方，重命名安全。

## [0.9.8] - 2026-06-12

### Changed
- **上下文参数默认值更新**：适配现代大 context LLM（Claude 200K / DeepSeek V3-V4 128K / Kimi 128K / GPT-4.5+ 128K）。
  - `reserveResponseTokens`：8000 → **32000**（现代 LLM 输出能力更强，预留更多回复空间）
  - `toolResultCompressionThreshold`：1000 → **2000**（避免过度压缩中等长度工具结果）
  - `toolResultTargetTokens`：200 → **500**（压缩后保留更多工具结果细节）
- **`ContextWindowManager` 模型映射表更新**：修正过时的 token 上限，新增主流模型系列。
  - `deepseek-v*` / `deepseek-r*`：64K → **128K**（DeepSeek V3/V4/R1 实际支持 128K）
  - `gpt-4` 基础版：8192 → **128K**（旧版 8K API 已停用，现行均为 128K）
  - 新增 `gpt-4.5` / `gpt-5` 系列：**128K**
  - 新增 `o1-` / `o3-` / `o4-` 系列（GPT-o 推理模型）：**200K**
  - 新增 `kimi-` / `moonshot-` 系列：**128K**
  - 新增 `llama-3` / `llama3` 系列：**128K**（Meta Llama 3.1+）
  - 新增 `mistral-` 系列：**128K**
  - 未知模型默认值维持 **128K**（现代 LLM 最低公约数）
- `AgentCoreSettings` 版本号升至 v9，旧配置自动迁移（仅迁移仍使用旧默认值的字段，不覆盖用户自定义配置）

## [0.9.7] - 2026-06-12

### Removed
- **Rules System 完全废弃**（见 ADR-10）：移除与 PROJECT.md 功能高度重叠的规则系统。
  - 删除 `RulesLoader.cs`（272 行）
  - `BootstrapContext` 移除 `Rules` 属性和规则注入逻辑
  - `BootstrapLoader` 移除 `RulesLoader.Load()` 调用
  - `AgentCoreSettings` 移除 `rulesEnabled` 字段，版本号回退 9 → 8
  - `ManageWorkspaceConfigTool` 移除 `read_rules`、`write_rules`、`get_rules_paths` 三个 action
  - `ContextMemorySettingsPage` 和 `ContextSettingsSection` 移除 Rules System UI 卡片
  - `SOUL.md §13` 移除 rules.md 相关说明
  - `TOOLS.md.template` 移除 rules actions 说明和路由条目

### Changed
- `ROADMAP.md`：6.4.1/6.4.2 标记为废弃（见 ADR-10），新增 ADR-10 决策记录

## [0.9.6] - 2026-06-11

### Added
- **规则系统（Rules System）**：新增两层规则文件支持，规则内容自动注入 System Prompt 末尾。
  - **层1 — Workspace 层**：`{WorkspaceRoot}/.agentcore/rules.md`，适合跨项目的团队规则（编码规范、安全要求、工作流约定）。
  - **层2 — Project 层**：`{UnityRoot}/AgentCore/rules.md`，适合当前 Unity 项目的特定规则（架构约定、禁用 API、命名规范）。
  - 两层规则均存在时全部注入（Workspace 层在前，Project 层在后），互不覆盖。
  - 新增 `RulesLoader.cs`：负责加载两层规则文件，提供 `GetWorkspaceRulesPath()`、`GetProjectRulesPath()`、`GenerateRulesTemplate()` 静态辅助方法。
  - `BootstrapContext` 新增 `Rules` 属性（`List<RulesEntry>`），`CompileSystemPrompt()` 在末尾追加规则注入。
  - `BootstrapLoader.Load()` 在 PROJECT.md 用户层之后调用 `RulesLoader.Load()`。
- **`manage_workspace_config` 工具新增 3 个 action**：
  - `read_rules`：读取指定层（`layer: "workspace"` 或 `"project"`）的 rules.md 内容。
  - `write_rules`：写入指定层的 rules.md 内容，自动创建目录。
  - `get_rules_paths`：返回两层规则文件的路径和存在状态。
- **Settings UI — Rules System Card**：在 Context 设置页新增 "Rules System" 卡片，显示启用开关和两层规则文件的路径/操作按钮（Edit / Show / Create）。
- **SOUL.md §13 更新**：补充 rules.md 两层设计说明、读写时机、决策表新增规则相关场景。
- **TOOLS.md.template 更新**：Workspace Configuration 节补充 `read_rules`、`write_rules`、`get_rules_paths` 说明；Tool Selection Guide 新增 5 条规则相关路由条目。

### Changed
- `AgentCoreSettings`：新增 `rulesEnabled` 字段（默认 `true`），版本号 8 → 9，新增 v8→v9 迁移日志。
- `BootstrapLoader` 日志输出新增 `RULES={count}` 字段。

### Deprecated
- **SmartToolRecommender（6.4.3）**：废弃基于上下文的工具推荐功能。决策理由：Agent 对项目设计方向和当前开发阶段的理解永远不如用户明确，主动建议在实践中会产生大量无止尽的优化建议，浪费 token，干扰用户工作节奏。（见 ADR-9）
- **响应式建议（6.4.4）**：废弃 LLM 响应末尾附带"下一步建议"功能，原因同上。（见 ADR-9）

## [0.9.5] - 2026-06-10

### Fixed
- **Full Index 始终返回 0 files / 0 symbols（根本原因）**：`CodebaseIndexer.RunFullIndexAsync` 和 `RunIncrementalIndexAsync` 在调用 `UpsertWorkspaceAsync` 获取数据库 ID 后，会重建 `IndexWorkspace` 对象以注入 `Id` 字段，但重建时遗漏了 `UnityRoot` 和 `UnityRootRelativePath` 两个字段。这导致 `UnityRootProvider.DiscoverRoots()` 检测到 `workspace.UnityRoot == null` 后直接返回空列表，`enabledRoots` 为空，文件扫描阶段无任何文件可处理，最终输出 "Done — 0 files, 0 symbols"。修复：在两处重建代码中补充 `UnityRoot = workspace.UnityRoot` 和 `UnityRootRelativePath = workspace.UnityRootRelativePath`。已验证：298 files, 6453 symbols。

### Added
- **SOUL.md §14 — 代码索引主动调用规则**：新增完整的 `search_code` 主动调用规范，包含：
  - 对话开始协议（`get_stats` → `index_incremental` → 空索引提示 → 不阻塞对话）
  - 6 个强制预查场景（修改文件前、提到类名时、"添加功能到X"时、询问架构时、重命名/删除前、编译错误时）
  - 搜索策略（5 条指导原则：fuzzy 搜索、search_text 广搜、get_symbol_context 全上下文、find_usages 影响评估、get_file_symbols 编辑前预览）
  - 索引新鲜度规则（写脚本后调用 `index_incremental`，不自动调用 `index_full`）
- **SOUL.md §15 — VCS 主动调用规则**：新增完整的 `version_control` 主动调用规范，包含：
  - 主动只读查询（破坏性操作前、批量重构前自动调用 `get_status`）
  - 7 条自然语言 → action 映射（"改了什么" → `get_status`，"看 diff" → `get_diff`，"看历史" → `get_log` 等）
  - 写操作确认规则（NEVER 自动 commit/stage/revert/push，必须 `confirmed: true`）
  - VCS 类型感知（`detect_vcs` + Git/SVN/Perforce 各自 action 列表）
- **SOUL.md §2 补充**：在"行动前先观察"原则中新增"索引优先"规则——当 Code Index 可用时，修改文件前先用 `search_symbol` 或 `get_file_symbols` 定位目标
- **SOUL.md §4 补充**：反幻觉工具名称表新增 `search_code`（防止使用 ~~code_search~~、~~symbol_search~~ 等错误名称）和 `version_control`（防止使用 ~~vcs_control~~、~~git_commit~~ 等错误名称）
- **TOOLS.md.template `search_code` 章节补充**：在工作流说明中新增"对话开始时的标准工作流"（4 步：`get_stats` → `index_incremental` → 空索引提示 → 不阻塞对话）

## [0.9.4] - 2026-06-09

### Fixed
- **IndexingSettingsPage / IndexingSettingsContribution 后端不一致**：两个 Settings UI 文件原先硬编码 `new JsonlIndexStore(workspace)`，绕过了 `IndexStoreFactory`，导致 Settings 页面的索引操作写入 JSONL 后端，而 `search_code` 工具通过 `IndexStoreFactory` 读取 SQLite 后端，两者数据完全不互通。现已统一改为 `IndexStoreFactory.Create(workspace.WorkspaceRoot)`，Settings UI 与工具层共享同一 SQLite 数据库。
- **IndexingProgress 属性名错误**：修复 `result.IndexedFiles` → `result.ProcessedFiles`、`result.IndexedSymbols` → `result.ExtractedSymbols`、`result.ElapsedMs` → `result.ElapsedSeconds * 1000`（编译错误）。
- **IndexingStats 属性名错误**：修复 `_cachedStats.TotalRoots` → `_cachedStats.EnabledRootCount`（编译错误）。
- **ClearAsync() 调用错误**：修复为正确的 `ClearWorkspaceIndexAsync(workspaceId, ct)`，并在调用前通过 `UpsertWorkspaceAsync` 获取 `workspaceId`（编译错误）。
- **GetStatsAsync() 签名错误**：修复为正确的 `GetStatsAsync(workspaceId, ct)`，并通过 `GetWorkspaceByFingerprintAsync` 先查询 workspace 记录（编译错误）。
- **ToolsExtensionsSettingsPage 缺少 indexing toggle**：`SetComponentEnabled` 方法的 switch-case 缺少 `"indexing"` 分支，导致点击 Indexing 组件的 Enabled 开关时弹出 "Unsupported Optional Component" 错误对话框。现已补充 `case "indexing": OptionalComponentManager.SetIndexingEnabled(enabled)` 分支，与 `ExtensionsSettingsSection` 保持一致。

### Changed
- Settings UI 中新增 "Index Backend" 字段，显示当前使用的存储后端（SQLite / SQLite not yet created）。
- 统计信息新增 "Backend" 和 "Parse Errors" 字段，时间戳改为本地时区显示。

## [0.9.3] - 2026-06-08

### Added
- **代码库索引 Phase 2（v0.9.3）** — SQLite 迁移 + 依赖图构建 + FTS5 全文搜索，`search_code` 工具新增 5 个 action。
  - **`SymbolDependency` 模型**（`Editor/Indexing/Models/SymbolDependency.cs`）— 表示符号间的类型依赖关系，含 `FromSymbolId`、`ToTypeName`、`Kind`、`FileId`、`Line` 字段；`DependencyKind` 静态常量类定义 8 种依赖类型：`inheritance`、`interface_impl`、`field_type`、`method_param`、`method_return`、`attribute`、`generic_arg`、`using_directive`。
  - **`DependencyExtractor`**（`Editor/Indexing/Core/DependencyExtractor.cs`）— 基于 Roslyn `SyntaxTree` 的 C# 类型依赖提取器，从类型声明、字段、属性、方法参数/返回值、特性、泛型参数、using 指令中提取依赖关系，支持泛型类型展开。
  - **`SqliteIndexStore`**（`Editor/Indexing/Core/SqliteIndexStore.cs`）— 完整的 SQLite 后端实现，替代 JSONL 作为默认存储；包含 FTS5 虚拟表（`symbols_fts`）支持全文搜索，`symbol_dependencies` 表存储依赖图；实现 `IIndexStore` 全部 20 个方法；`BackendType` 返回 `"sqlite"`。
  - **`IndexStoreFactory`**（`Editor/Indexing/Core/IndexStoreFactory.cs`）— 工厂类，优先创建 `SqliteIndexStore`（DB 路径：`{WorkspaceRoot}/.agentcore/index/codebase.db`），SQLite 不可用时自动降级为 `JsonlIndexStore`；提供 `CreateFromCurrent()`、`Create(workspaceRoot)`、`GetDbPath(workspaceRoot)` 三个静态方法。
  - **`search_code` 新增 5 个 action**（`Editor/Indexing/Tools/SearchCodeTool.cs`）：
    - `search_text` — FTS5 全文搜索，支持前缀匹配和 LIKE 降级，返回匹配符号列表
    - `get_dependencies` — 查询指定文件或符号的出向类型依赖（继承、接口实现、字段类型等）
    - `find_usages` — 查询指定类型名称的入向引用（谁依赖了这个类型）
    - `get_symbol_context` — 聚合查询：符号详情 + 同文件符号 + 出向依赖 + 入向引用，一次调用获取完整上下文
    - `get_backend_info` — 返回当前存储后端类型（sqlite/jsonl）、DB 文件路径及是否存在

### Changed
- **`CodebaseIndexer`** — 集成 `DependencyExtractor`，全量/增量索引时自动提取并存储符号依赖关系（`BulkInsertDependenciesAsync` + `DeleteDependenciesByFileAsync`）。
- **`IIndexStore` 接口** — 新增 5 个方法：`BulkInsertDependenciesAsync`、`DeleteDependenciesByFileAsync`、`GetDependenciesAsync`、`FindUsagesAsync`、`SearchSymbolsByTextAsync`；新增 `BackendType` 属性（`string`）。
- **`JsonlIndexStore`** — 实现新增的 5 个接口方法（stub 实现，返回空集合）；`BackendType` 返回 `"jsonl"`。
- **`SearchCodeTool.CreateStore()`** — 改为调用 `IndexStoreFactory.CreateFromCurrent()`，自动选择最优后端。
- **`IndexRoot.ExcludePatterns` 默认值** — 补全排除目录：新增 `Logs/`（Unity 日志）、`Build/`、`Builds/`（构建输出）、`.svn/`、`.git/`（VCS 元数据）。
- **`TOOLS.md.template`** — `search_code` 章节完整更新，新增 5 个 action 的详细说明、参数和使用场景；Tool Selection Guide 新增对应条目；存储路径说明更新为 SQLite DB 路径。

## [0.9.2] - 2026-06-03

### Added
- **Code Indexing 独立设置标签页** — 新增 `IndexingSettingsPage`（`Editor/Indexing/UI/IndexingSettingsPage.cs`），当 `AGENTCORE_INDEXING` 组件启用时，在 AgentCore Project Settings 顶部导航栏自动出现独立的 "Code Indexing" 标签页（Order = 700，位于所有内置标签页之后）。
  - **LLM Configuration 卡片** — 展示当前 LLM 端点、模型名称、温度、最大 Token 数，并提示索引本身不调用 LLM（仅供参考）。
  - **Workspace 卡片** — 展示当前工作区根目录、Unity 根目录、工作区哈希。
  - **Index Statistics 卡片** — 展示已索引文件数、符号数、根目录数、上次全量/增量索引时间，含 Refresh Stats 按钮。
  - **Index Actions 卡片** — 提供 Full Index / Incremental / Clear Index 三个操作按钮，含操作结果反馈和说明文字。
- **`IAgentCoreSettingsPage.Order` 属性** — 为 `IAgentCoreSettingsPage` 接口新增 `int Order { get; }` 属性，支持标签页排序（内置页面使用 100–600，可选组件页面使用 700+）。
- **`AgentCoreExtensionRegistry.Pages`** — 扩展注册表新增 `Pages` 属性，通过反射自动发现来自非主程序集（非 `AgentCore.Editor`）的 `IAgentCoreSettingsPage` 实现，实现可选组件标签页的零耦合动态注入。
- **`AgentCoreSettingsProvider.BuildPageList()`** — 设置 Provider 新增 `BuildPageList()` 静态方法，将内置页面与 `AgentCoreExtensionRegistry.Pages` 动态发现的页面合并，按 `Order` 排序后去重。

### Changed
- **内置设置页面 `Order` 属性** — 为 6 个内置页面补充 `Order` 实现：Dashboard(100)、Model & Agent(200)、Context & Memory(300)、Tools & Extensions(400)、Workspace(500)、UI & Diagnostics(600)。

## [0.9.1] - 2026-06-03

### Added
- **代码库索引 Phase 1（v0.9.1）** — 基于 Roslyn 的 C# 符号索引系统，通过 `search_code` 工具提供精确符号检索能力（需启用 `AGENTCORE_INDEXING` 可选组件）。
  - **数据模型层**：`IndexWorkspace`、`IndexRoot`、`IndexedFile`、`SymbolInfo`、`IndexingStats`、`IndexScopeType`、`IndexRootRole` 完整模型体系
  - **存储层**：`IIndexStore` 抽象接口 + `JsonlIndexStore` MVP 实现（JSONL 文件存储，存放于 `Library/AgentCore/Indexing/{workspaceHash}/`，不提交 VCS）
  - **根目录发现层**：`IIndexRootProvider` 接口 + 5 个 Provider 实现：
    - `UnityRootProvider` — Unity 项目根目录（Assets/Scripts）
    - `VcsWorkspaceRootProvider` — VCS 工作区根目录
    - `WorkspaceChildRootProvider` — 工作区子根目录（自动发现）
    - `UserConfiguredScopeRootProvider` — 用户配置的自定义根目录
    - `ResourcePackageMetadataProvider` — 资源包元数据根目录
    - `ExtraAuthorizedRootProvider` — 额外授权根目录
  - **解析层**：`IndexWorkspaceResolver`（从 WorkspaceContext 构建 IndexWorkspace）+ `IndexRootResolver`（聚合所有 Provider，去重排序）
  - **提取层**：`RoslynSymbolExtractor` — 基于 Microsoft.CodeAnalysis.CSharp 的语法树级符号提取，支持 class/interface/struct/enum/delegate/method/property/field/event/constructor，含 MD5 内容哈希用于增量检测
  - **索引引擎**：`CodebaseIndexer` — 全量索引（`RunFullIndexAsync`）+ 增量索引（`RunIncrementalIndexAsync`），基于 LastModified ticks 快速变更检测，支持 include/exclude 模式过滤
  - **进度追踪**：`IndexingProgress` — 索引阶段快照（Scanning/Indexing/Completed/Failed），含文件数/符号数/耗时统计
  - **查询层**：`SymbolSearcher` — 多维度查询接口（按名称/全名/命名空间/文件/类型成员），`IsIndexAvailableAsync` 可用性检查
  - **工具层**：`SearchCodeTool` — 10 个 action 的 `search_code` 工具（`resolve_workspace`、`list_roots`、`index_full`、`index_scope`、`index_incremental`、`search_symbol`、`list_namespaces`、`get_file_symbols`、`get_stats`、`clear_index`）
  - **Settings UI**：`IndexingSettingsContribution` — Extensions 设置页中的 Code Indexing 卡片（工作区信息、索引统计、全量/增量/清空操作按钮）
  - **组件描述符**：`IndexingComponentDescriptor` — 组件元数据（id: "indexing"，define: AGENTCORE_INDEXING）
- **`OptionalComponentManager`** — 新增 `IndexingDefine = "AGENTCORE_INDEXING"`、`IsIndexingEnabled()`、`SetIndexingEnabled()` 支持 Code Indexing 可选组件的启用/禁用
- **`ExtensionsSettingsSection`** — 新增 "indexing" case，支持通过 Extensions 设置页切换 Code Indexing 组件
- **`TOOLS.md.template`** — 新增 `search_code` 工具完整使用指南（工作流、所有 action 说明、使用场景、注意事项）及 Tool Selection Guide 快速条目

## [0.9.0] - 2026-06-03

### Added
- **Workspace 基础设施（v0.9.0 P0）** — 企业级 SVN 多根工作区支持，为代码索引、VCS 工具、RAG 和 Memory 提供统一的 WorkspaceRoot 边界。
  - **数据模型层**：`WorkspaceContext`、`WorkspaceVcsInfo`、`WorkspaceRootInfo` 及配套枚举（`WorkspaceScopeType`、`WorkspaceRootRole`、`WorkspaceResolutionStatus`、`WorkspaceVcsType`）
  - **解析层**：
    - `UnityRootResolver` — 从 `Application.dataPath` 解析 Unity 项目根目录
    - `SvnWorkspaceInfoResolver` — 运行 `svn info` 解析 Working Copy Root / URL / Revision / BranchId，支持 `.svn` 目录回退检测
    - `WorkspaceRootResolver` — 优先级：ManualOverride → SVN 检测 → FallbackToUnityRoot
    - `WorkspaceRootRoleResolver` — 将 `ScopeType` 映射到默认 `Role` / `IsReadOnly` / `IsGenerated`
    - `ScopeRootResolver` — 自动扫描 workspace 下的业务子根（unity/gamemodes/maps/tools/plugins/shared/engine/generated 等），合并 `workspace.json` 配置
  - **指纹层**：`WorkspaceFingerprintBuilder` — SHA256 短哈希（16 hex 字符），用于跨分支隔离索引数据库
  - **服务层**：`WorkspaceContextService` — 静态单例，`GetCurrent()` / `Refresh()` / `InvalidateCache()`，Domain Reload 安全（静态缓存自动失效）
  - **配置层**：`WorkspaceConfig` / `WorkspaceRootOverride` / `WorkspaceConfigStorage` — 读写 `.agentcore/workspace.json` 项目级配置
  - **安全层**：`WorkspaceOperationRisk` 枚举 + `WorkspacePathPolicy` — 基于 Root Role 的写操作风险策略（Safe / LowRisk / MediumRisk / HighRisk / Blocked）
  - **路径服务**：`WorkspacePathService` — `ResolveWorkspacePath` / `ResolveUnityAssetPath` / `TryGetRootInfo` / `IsInsideWorkspace` 等路径工具方法
- **Workspace Settings 页面** — Project Settings → AgentCore → Workspace 新标签页，包含：
  - Workspace Overview 卡片（WorkspaceRoot / UnityRoot / Branch / Revision / Fingerprint）
  - Detection Actions 卡片（Refresh / Invalidate Cache / Auto-Detect 开关）
  - Scope Roots 折叠面板（表格展示所有已检测子根及风险等级）
  - Manual Overrides 折叠面板（WorkspaceRoot 覆盖路径 / Unity 相对路径 / workspace.json 管理）
  - Path Safety Policy 折叠面板（Role → Risk 对照表）
- **AgentCoreSettings v8** — 新增 Workspace 配置字段：`workspaceAutoDetectEnabled`、`workspaceRootOverride`、`unityRootRelativePathOverride`、`workspaceConfigVersion`

### Changed
- **`ProjectContextCollector`** — `Collect()` 新增 "Workspace 信息" 章节，注入 WorkspaceRoot / UnityRoot / Branch / Fingerprint / Scope Roots 表格到 Bootstrap 上下文，让 LLM 感知完整工作区结构
- **`VcsDetector`（VCS 组件）** — `DetectVcs()` 优先复用 `WorkspaceContextService` 的缓存结果，避免重复执行 `svn info` 子进程；`GetVcsRootPath()` 对 SVN 返回 WorkspaceRoot（working copy root）而非 Unity 项目根目录

## [0.8.2] - 2026-06-03

### Added
- **`manage_workspace_config` 工具** — 专用于读写 `PROJECT.md` 和 `SOUL.ext.md` 的工具，Agent 可在 Chat 中主动分析项目并更新配置文件。
  - `read_project_config` / `write_project_config` — 读写 PROJECT.md（项目约定 + 个人偏好）
  - `read_soul_extension` / `write_soul_extension` — 读写 SOUL.ext.md（Agent 行为规则扩展）
  - `get_config_paths` — 查询两个配置文件的当前路径和存在状态
  - 写入时自动创建 `AgentCore/` 目录，路径解析与 Bootstrap 加载逻辑完全一致
  - 变更在下次对话开始时生效（Bootstrap 在对话启动时加载）

### Changed
- **SOUL.md 新增 §13 Workspace Configuration Management** — 明确告知 Agent 何时主动读写 PROJECT.md / SOUL.ext.md，以及与 `manage_memory` / `manage_knowledge` 的决策边界。
- **TOOLS.md.template 新增 Workspace Configuration 章节** — 包含 `manage_workspace_config` 完整使用指南和 Tool Selection Guide 条目。
- **§4 Anti-Hallucination 表格** — 新增 `manage_workspace_config` 正确名称及常见幻觉名称。

## [0.8.1] - 2026-06-03

### Changed
- **Bootstrap 链重构：MEMORY.md / USER.md → PROJECT.md / SOUL.ext.md**
  - 移除旧的 `MEMORY.md`（记忆文件）和 `USER.md`（用户偏好文件）加载逻辑，统一合并为 `PROJECT.md`（用户可编辑层）。
  - 新增 `SOUL.ext.md` 支持 — 追加模式扩展 SOUL.md 行为约束，不替换核心 SOUL；建议提交到 VCS。
  - 新增 `SKELETON.md` 加载支持 — 从 `Library/AgentCore/workspace-skeleton.md` 读取代码库骨架（由代码索引功能生成，不提交 VCS）。
  - `BootstrapContext` 字段更新：移除 `Memory`、`User`，新增 `SoulExtension`、`Workspace`、`Skeleton`。
  - `BootstrapLoader.GenerateUserFileTemplate()` 集中化 — 原散落在 4 个 UI 文件中的模板生成逻辑统一到 `BootstrapLoader` 公共静态方法。
  - 新增 `Editor/Bootstrap/Resources/PROJECT.md.template` — 包含 `## Project Conventions`（团队约定）和 `## Personal Preferences`（个人偏好）两个 section。
  - Settings 页面中"User Files"卡片更名为"Project Files"，按钮更新为 PROJECT.md / SOUL.ext.md。

### Removed
- **`MEMORY.md` 和 `USER.md` 文件支持已移除** — 如有现存文件，内容请迁移至 `PROJECT.md`（`## Personal Preferences` section）。

## [0.8.0] - 2026-06-02

### Added
- **VCS Working Copy Status 扁平列表重构**
  - 将 Working Copy Status 从 TreeView 改为 SVN 风格扁平列表，每行显示状态徽章 + 相对路径，视觉更清晰。
  - 支持单选、Ctrl 多选、Shift 范围选，选中行高亮。
  - 右键菜单根据文件状态动态显示可用操作（Add / Revert / Resolve / Commit / View Diff / Show Log / Blame / Copy Path 等）。
  - 多选时右键菜单聚合为批量操作（Commit Selected / Revert Selected / Stage Selected / Copy Paths 等）。
- **VCS 面板顶部 Cleanup Project 按钮**
  - 新增 "Cleanup Project" 按钮，支持一键触发 SVN cleanup / Git gc / P4 reconcile。
  - 编译或资产导入期间自动禁用按钮，防止误操作。
  - 优先尝试打开外部工具（TortoiseSVN / SourceTree 等），不可用时回退到内置命令行执行。
- **VCS Chat 工具能力大幅扩展（`version_control` tool）**
  - 新增 `get_file_log` action — 查询单个文件的提交历史（SVN `svn log`、Git `git log --follow`、P4 `p4 filelog`）。
  - 新增 `cleanup` action — 清理工作副本锁定/临时文件（SVN `svn cleanup`、Git `git gc --auto`、P4 `p4 reconcile`）。
  - 新增 `commit_files` action — 提交指定文件列表（支持 SVN / Git / P4，需 `confirmed=true` 二次确认）。
  - 新增 `resolve_files` action — 标记冲突文件为已解决（SVN `svn resolve --accept working`、Git `git add`、P4 `p4 resolve`，需确认）。
  - 新增 `ignore_file` action — 将指定文件加入忽略规则（SVN `svn:ignore` property、Git `.gitignore`，需确认）。
  - 新增 `ignore_folder` action — 将指定目录加入忽略规则（需确认）。
  - 新增 `ignore_extension` action — 将指定文件扩展名加入忽略规则（需确认）。
  - 新增 `remove_files` action — 从版本控制中删除文件（SVN `svn delete`、Git `git rm`、P4 `p4 delete`，需确认）。
  - 所有写操作统一使用 `confirmed=true` 二次确认机制，防止 Agent 误操作。

### Changed
- **VCS 面板布局优化** — 修复小窗口高度下各区块压缩/溢出问题，状态列表区域改为 `flex-grow` 自适应，避免内容被截断。
- **`version_control` tool `RequiresMainThread`** — 由 `false` 改为 `true`，因 `cleanup` action 需要访问 `EditorApplication.isCompiling`。

## [0.7.0] - 2026-05-28

### Added
- **Settings UI 重构为 Dashboard + 4 Pages**
  - 新增 `IAgentCoreSettingsPage` 接口，与旧 `IAgentCoreSettingsSection` 独立，避免耦合。
  - 新增 `DashboardSettingsPage` — 状态总览（LLM / Memory / Knowledge / VCS 徽章）、Quick Actions、Package Info。
  - 新增 `ModelAgentSettingsPage` — Model Connection（Endpoint / API Key / Model / Fetch / Test）、Generation（Temperature / Max Tokens）、Agent Runtime（Max Tool Rounds / Fallback Routing）、Self Correction（Auto Compile / Auto Console / Max Consecutive Errors）。
  - 新增 `ContextMemorySettingsPage` — Context Sources（Bootstrap + User Files）、Context Budget + Compression 双列卡片、Memory Service（mem0）+ Knowledge Base（LightRAG）双列卡片、Separate Compression LLM foldout。
  - 新增 `ToolsExtensionsSettingsPage` — Capability Overview、Tool Visibility（Presets + Category/Individual toggles）、Optional Components、Version Control 独立卡片、Extension Settings contribution 支持。
  - 新增 `UiDiagnosticsSettingsPage` — Chat UI（Streaming / Show Tool Details）、Diagnostics（Test LLM / mem0 / LightRAG / Refresh Registry / Open Logs）、Maintenance（Reset Settings / Clear Secure Keys / Open MEMORY.md / USER.md）。
  - 新增 `AgentCoreSettings.ResetToDefaults()` 方法，支持一键恢复所有设置为默认值。

### Changed
- **`AgentCoreSettingsProvider` 重写** — 从左侧导航 + Section dispatch 改为顶部 Tab 导航 + Page dispatch，仅保留 shell 职责。
- **旧 Section 系统退役** — 保留 `Editor/Config/Settings/Sections/` 目录代码短期不动，但 Provider 不再引用。旧 `AgentCoreSettingsRegistry` 和 `IAgentCoreSettingsSection` 进入维护模式。
- **VCS 设置提升为独立卡片** — Version Control 设置从 Extension Settings contribution foldout 提升为 Tools & Extensions 页面的一级卡片。
- **Agent 参数平铺化** — `maxToolCallRounds` 和 `maxConsecutiveErrors` 从 Advanced Limits foldout 提升为一级字段。

### Fixed
- 无行为修复（纯 UI 重构）。

## [0.6.1] - 2026-05-22

### Added
- **Settings 页面架构重构 (Settings Hub + Section System)**
  - 新增模块化 settings section 架构，将 AgentCore 设置页拆分为 General、Model、Agent、Context、Memory、Knowledge、Context Management、Extensions、Tools、Interface、Diagnostics 等独立 section。
  - 新增 settings shell / context / transient state / shared IMGUI helper / section registry，新增设置功能无需继续污染 `AgentCoreSettingsProvider`。
  - 新增 `ModelSettingsService`，将模型拉取与连接测试逻辑从 Provider 中抽离。

### Changed
- **AgentCoreSettingsProvider Shell 化** — Provider 仅保留 settings 初始化、左侧导航和 section dispatch，不再直接绘制业务设置。
- **Settings 信息架构重组** — Project Settings > AgentCore 改为左侧导航 + 右侧内容布局，避免单页无限 foldout 膨胀。
- **Extensions 设置归属收敛** — Optional Components 与启用组件贡献的 settings 统一由 Extensions section 管理。
- **Tools 设置独立化** — Tool exposure preset、category toggle、individual tool toggle 迁移到 Tools section，并与 Optional Components 启用/禁用职责解耦。

## [0.6.0] - 2026-05-21

### Added
- **VCS 可选组件化 (Optional Component Framework)**
  - 新增 `Editor/Extensions/` 扩展宿主机制，支持 Hub Panel 与 Settings Contribution 动态发现。
  - 新增 Optional Components 设置入口，用户可通过 `AGENTCORE_VCS` scripting define 启用或禁用 VCS 组件。
  - 新增 `AgentCore.VCS.Editor` 独立 Editor 程序集，VCS 仅在启用 `AGENTCORE_VCS` 后参与编译。
  - 新增 VCS 设置贡献区块，支持控制面板打开时自动刷新与默认提交历史数量。

### Changed
- **VCS 默认禁用** — 新安装 AgentCore 后不再默认显示 VCS Hub 入口，也不会默认注册 `version_control` 工具。
- **Hub 动态化** — Chat / Knowledge / Memory / VCS 统一通过动态 Panel contribution 接入，主窗口不再强引用 VCS 类型。
- **ToolAutoDiscovery 重建化** — 每次发现工具前重建 `ToolRegistry`，避免可选组件禁用后残留旧工具实例。
- **VCS 目录迁移** — VCS Tool、Adapter、Panel 与样式文件迁移至 `Editor/VCS/` 组件目录。

## [0.5.5] - 2026-05-21

### Added
- **版本控制集成 (Version Control Integration) - Phase 2 完整实现**
  - Phase 1 补齐 5 个高级查询 actions：
    - `get_blame` — 获取文件逐行归属信息（支持 Git/SVN/Perforce）
    - `get_commit_info` — 获取 Git 提交详细信息（作者、日期、变更文件）
    - `get_client_info` — 获取 Perforce 客户端工作区信息
    - `get_changelist` — 获取 Perforce 变更列表详情
    - `get_info` — 获取 SVN 仓库/文件详细信息
  - Phase 2 通用写操作 actions（所有 VCS 支持）：
    - `stage_files` — 暂存文件（Git: add, P4: edit/add, SVN: add）
    - `unstage_files` — 取消暂存（Git: reset HEAD, P4/SVN: revert）
    - `commit` — 提交变更（Git: commit, P4: submit, SVN: commit）
    - `revert_files` — 还原文件修改（所有 VCS）
    - `sync` — 同步远程（Git: pull, P4: sync, SVN: update）
  - Phase 2 Git 特有操作：
    - `create_branch` — 创建新分支
    - `switch_branch` — 切换分支
    - `stash` — 暂存当前修改
    - `stash_pop` — 恢复暂存的修改
  - Phase 2 VCS 别名映射：
    - `checkout_files` → Perforce edit/add
    - `submit` → Perforce submit
    - `update` → SVN update
    - `commit_svn` → SVN commit
    - `revert_svn` → SVN revert
    - `add_files` → SVN add
  - **用户确认机制** — 所有写操作首次调用返回预览，需 `confirmed=true` 才执行
  - `IVcsAdapter` 接口扩展 — 新增 6 个写操作方法
  - 新增数据类：`VcsBlameResult`, `VcsBlameLine`, `VcsOperationResult`, `VcsCommitDetail`, `VcsSvnInfo`, `VcsPerforceClientInfo`, `VcsPerforceChangelist`
  - `VersionControlPanel` UI 增强：
    - 操作按钮区域（Stage All, Unstage All, Commit, Sync, Revert All）
    - Git 特有操作区域（Create Branch, Switch Branch, Stash, Stash Pop）
    - 提交消息输入框
    - 分支名输入框
    - 文件选择复选框（支持选择性操作）
    - 按 VCS 类型自适应按钮标签（Git/SVN/Perforce 各有对应术语）
    - 危险操作确认对话框（Revert 前弹出确认）
    - 操作成功消息自动消失（5 秒）

### Changed
- `VersionControlTool` — 从 7 个 actions 扩展到 26 个 actions
- `GitAdapter` — 从 6 个方法扩展到 15 个方法（含 4 个 Git 特有操作）
- `SvnAdapter` — 从 6 个方法扩展到 11 个方法（含 SVN info 查询）
- `PerforceAdapter` — 从 6 个方法扩展到 11 个方法（含 client/changelist 查询）
- `VersionControlPanel.uss` — 新增操作按钮样式（primary/danger/operation 三种风格）

## [0.5.4] - 2026-05-21

### Added
- **版本控制集成 (Version Control Integration) - Phase 1**
  - 新增 `Editor/Tools/Native/VersionControl/` 模块 — 多 VCS 支持（SVN > Perforce > Git 优先级）
  - `VcsDetector` — 自动检测项目使用的版本控制系统
  - `IVcsAdapter` 接口 — 统一的 VCS 操作抽象层
  - `GitAdapter` — Git 版本控制适配器（只读查询操作）
  - `SvnAdapter` — SVN 版本控制适配器（只读查询操作）
  - `PerforceAdapter` — Perforce 版本控制适配器（只读查询操作）
  - `VcsCommandExecutor` — 统一的命令行执行器（超时控制、输出捕获）
  - `VersionControlTool` — Agent 工具，支持 7 个 actions：
    - `detect_vcs` — 检测 VCS 类型和可用性
    - `get_status` — 获取工作区状态（已修改文件列表）
    - `get_branch` — 获取当前分支/工作区信息
    - `get_log` — 获取提交历史（最多 100 条）
    - `get_diff` — 获取文件差异
    - `get_remote` — 获取远程仓库信息
    - `get_tags` — 获取标签/标记列表
  - `VersionControlPanel` UI 组件 — 独立的版本控制面板
    - 实时显示 VCS 类型、分支、版本号
    - 工作区状态列表（按状态分组）
    - 最近提交历史（最多 10 条）
    - Refresh 按钮手动刷新数据
    - View Diff 按钮查看差异（输出到 Console）
  - Hub Rail 新增 "VCS" 模块按钮
  - ChatWindow 集成 VersionControl 模块（与 Chat/Knowledge/Memory 同级）

### Changed
- `HubModule` 枚举 — 新增 `VersionControl` 模块
- `ChatWindow.Hub.cs` — 更新模块切换逻辑支持 VersionControl 面板
- `ChatWindow.uxml` — 新增 `#version-control-panel` 容器

## [0.5.3] - 2026-05-19

### Deprecated
- **模式系统 (Mode System) 废弃** — ADR-5 决策
  - AgentCore 定位为自主智能体，不需要手动模式切换
  - Agent 可根据需求自动识别环境并调用相应能力
  - 原计划的 Phase 6.1 模式系统任务已标记为 `[DEPRECATED]`

### Changed
- `plans/ROADMAP.md` — 新增 ADR-5 记录模式系统废弃决策
- `plans/ROADMAP.md` — 重新规划 v0.5.3+ 里程碑，移除模式系统相关任务

## [0.5.2] - 2026-05-18

### Added
- **上下文使用情况可视化 (Context Usage Visualization)**
  - 新增 `ContextUsagePanel` UI 组件 — 实时显示 token 使用情况和压缩统计
  - 新增 `ContextBudgetInfo` 数据结构 — 封装上下文预算和压缩指标
  - `AgentLoop.GetContextBudget()` — 暴露上下文预算信息供 UI 查询
  - 压缩统计持久化 — 支持 Domain Reload 后恢复压缩数据
  - 按会话统计 — 压缩数据随会话保存，支持历史查看

### Fixed
- **manage_knowledge 工具参数兼容性** — `query` action 现在同时支持 `"query"` 和 `"content"` 参数名，修复 LLM 参数名不匹配导致的连续失败
- **ContextUsagePanel UI 布局** — 添加 `flex-shrink: 0` 防止面板被消息滚动视图挤压
- **空回复兜底处理** — 当达到工具调用上限后 LLM 返回空内容时，显示"[系统提示] 助手未返回任何内容。"而非空白消息

### Changed
- `ChatWindow` — 集成 `ContextUsagePanel`，每次 LLM 调用后自动更新显示
- `CompressionMetrics` — 新增 `RestoreFromPersistence()` 方法支持 Domain Reload 恢复
- `SessionData` — 新增 `SerializableCompressionMetrics` 支持压缩数据序列化
- `DomainReloadState` — 新增压缩指标持久化方法

## [0.5.1] - 2026-05-14

### Fixed
- **Tool Call Arguments 合法性修复** — 新增 `SanitizeToolArguments()` 方法，修复 LLM 生成的无效 JSON arguments（如 Windows 路径中的未转义反斜杠 `\U`, `\P`），防止 vLLM 等服务端在 `json.loads()` 时返回 HTTP 400 错误
- **FallbackRouter 错误消息准确性** — 非重试错误（如 HTTP 400）现在正确报告实际尝试次数（"failed after 1 attempt"），而非误导性的 "failed after 3 attempts"
- **项目路径标准化** — `ProjectContextCollector.GetProjectPath()` 返回正斜杠格式路径，避免 system prompt 中的反斜杠路径"教会"模型生成无效 JSON

## [0.5.0] - 2026-05-14

### Added
- **上下文压缩系统 (Context Compression System)**
  - 新增 `Editor/Core/Compression/` 模块 — 智能压缩替代简单截断
  - `ToolResultCompressor` — 自动压缩超过阈值（默认 1000 tokens）的工具输出为 ~200 tokens 摘要
  - `ConversationCompressor` — 当上下文使用率超过 70% 时，将旧对话段压缩为摘要
  - `CompressionLLMClientFactory` — 支持独立的压缩 LLM（如 Claude Haiku），降低成本
  - `CompressionMetrics` — 追踪压缩统计（token 节省量、压缩比、成功/失败次数）
  - `CompressionPrompts` — 压缩专用 Prompt 模板
  - 优雅降级：压缩 LLM 失败时自动回退到 head+tail 截断策略
  - Settings 版本迁移 v5→v6，新增 7 个压缩配置字段
  - `SecureKeyStorage` 新增压缩 LLM API Key 安全存储
  - Settings Provider 新增 "Context Compression" 配置面板

### Changed
- `AgentLoop.LLM.cs` — 在 `TrimToFit` 之前调用 `ConversationCompressor`（智能压缩优先于暴力截断）
- `AgentLoop.Tools.cs` — 工具结果添加到消息历史前通过 `ToolResultCompressor` 压缩
- `AgentLoop.cs` — 初始化时创建压缩系统组件

## [0.4.8] - 2026-05-13

### Added
- **ManageTestTool 增强** (5.3.2)
  - 新增 `cancel` action — 通过反射调用 TestRunnerApi 取消正在运行的测试
  - 新增 `create_test_fixture` action — 生成完整测试 Fixture 模板（含 OneTimeSetUp/TearDown、SetUp/TearDown、命名空间、描述注释），支持 EditMode/PlayMode
- **ManageMaterialTool 增强** (5.3.5)
  - 新增 `batch_set_properties` action — 批量设置材质属性（best-effort 策略，逐条设置并汇报成功/失败）
  - 新增 `list_materials` action — 按文件夹和/或 Shader 过滤列出项目中的材质资产
  - 新增 `get_shader_info` action — 获取 Shader 详细信息（属性列表、关键字、是否 Shader Graph 资产）

## [0.4.7] - 2026-05-13

### Changed
- **文档状态校准（Documentation Status Alignment）**
  - 全面审计 44 个 Native 工具（335+ actions）的实际代码功能
  - 修正 ROADMAP.md Phase 5.3：ManageCinemachineTool (20 actions) 和 ManageUIToolkitTool (20 actions) 标记为已完成
  - ManageXRTool 标记为 `[!]` 冻结（项目不涉及 VR/AR/MR）
  - 16 份 plans/ 文档全部添加状态标注（历史归档 / 已完成 / 部分落地）
  - 新增 ADR-3：「文档状态必须以代码事实校准」
  - ROADMAP §7.4 新增「文档状态索引」，列出所有计划文档的当前状态
  - ROADMAP §8「下一步行动建议」更新为 v0.4.6 后的实际优先级

### Fixed
- 修正 ROADMAP 中多处"未开始"标记与实际代码已完成的不一致
- ADR 编号修正为连续序列（ADR-1, ADR-2, ADR-3）

## [0.4.6] - 2026-05-12

### Changed
- **ChatWindow partial class 拆分**
  - 将 2135 行的单体 `ChatWindow.cs` 拆分为 9 个 partial 文件（1 主文件 + 8 分区文件）
  - `ChatWindow.cs` — 主文件：常量、字段、静态缓存、菜单入口、CreateGUI、OnDestroy、InitializeAgentLoop
  - `ChatWindow.Input.cs` — 用户输入：发送、取消、输入框快捷键、窗口快捷键
  - `ChatWindow.Events.cs` — 事件处理：HandleAgentEvent、UpdateUIState
  - `ChatWindow.Messages.cs` — 消息 UI：气泡创建、流式追加、错误显示、重试、重建、滚动
  - `ChatWindow.DomainReload.cs` — Domain Reload 通知卡片：创建、详情行、状态更新
  - `ChatWindow.Restore.cs` — 会话恢复：TryRestoreSession、EnsureSessionExists
  - `ChatWindow.Hub.cs` — Hub 模块切换：模块面板可见性、Knowledge ask-agent、侧边栏
  - `ChatWindow.Sessions.cs` — 会话管理：列表刷新、切换、新建、重命名、删除、导出、相对时间
  - `ChatWindow.Tools.cs` — 工具调用 UI：分组管理、卡片状态、轮次分隔线
  - `ChatWindow.UIHelpers.cs` — UI 辅助：状态标签、发送按钮、取消按钮
  - 纯机械移动，零行为变更，所有字段保留在主文件中供 partial 共享

## [0.4.5] - 2026-05-12

### Changed
- **AgentLoop partial class 拆分**
  - 将 2086 行的单体 `AgentLoop.cs` 拆分为 9 个 partial 文件（1 主文件 + 8 分区文件）
  - `AgentLoop.cs` — 主文件：事件、属性、字段、构造函数、Initialize、SendMessage、Cancel、Reset、LoadSession、Dispose
  - `AgentLoop.FileChanges.cs` — 文件变更追踪恢复与事件发射
  - `AgentLoop.Events.cs` — SetState、EmitEvent
  - `AgentLoop.Memory.cs` — 记忆召回：搜索、格式化、注入
  - `AgentLoop.LLM.cs` — LLM 流式调用与 chunk 回调
  - `AgentLoop.Tools.cs` — 工具定义构建、工具执行、结果消息构建
  - `AgentLoop.Runner.cs` — RunToolCallLoop 主循环、最终响应处理、失败计数
  - `AgentLoop.DomainReload.cs` — Domain Reload 保存与恢复全流程
  - `AgentLoop.Sanitization.cs` — 消息历史清理（tool_use/tool_result 配对修复）
  - 纯机械移动，零行为变更，所有字段保留在主文件中供 partial 共享

## [0.4.4] - 2026-05-12

### Added
- **JSON Schema 参数预校验**
  - 新增 `ToolParameterValidator` (`Editor/Tools/Infrastructure/ToolParameterValidator.cs`)
  - 支持 JSON Schema 子集：`required`、`properties`、`type` (string/integer/number/boolean/array/object)、`enum`
  - 在 `ToolCallDispatcher.DispatchAsync` 中于工具执行前自动校验参数
  - 校验失败时直接返回 `ToolResult.Fail`，不调用具体工具
  - 空 schema 或无 properties 时保持宽松，允许执行
  - 未声明的额外字段默认允许
- **Schema 校验测试**
  - 新增 `ToolCallDispatcherSchemaValidationTests` (18 cases)
  - 覆盖 required 缺失、类型错误、enum 不匹配、合法参数、空 schema 等场景

### Changed
- `ToolCallDispatcher.DispatchAsync` 新增第 3 步 schema 校验（原第 3 步"执行工具"变为第 4 步）

## [0.4.3] - 2026-05-12

### Added
- **测试基础设施**
  - 新增 `AgentCore.Tests.Editor` 测试程序集 (`Editor/Tests/AgentCore.Tests.Editor.asmdef`)
  - 基于 Unity Test Framework (NUnit)，Editor-only，与主程序集隔离
- **核心单元测试**
  - `ToolResponseTests` — 覆盖 `ToolResponse.Ok/OkWithData/Fail/ToJson/ToToolResult` 及 `ToolResult` 全路径 (20 cases)
  - `JsonHelperTests` — 覆盖 `Serialize/Deserialize/ParseObject/ParseArray/GetString/GetInt/GetBool` 含异常与边界 (16 cases)
  - `TokenCounterTests` — 覆盖 `EstimateTokens/EstimateMessageTokens/EstimateConversationTokens` 含 CJK 与混合文本 (14 cases)
  - `ToolHelpersTests` — 覆盖参数解析、枚举解析、Vector3/Color/Quaternion 解析与序列化 (22 cases)

### Changed
- 无运行时行为变更，本版本仅新增测试代码

## [0.4.2] - 2026-05-11

### Added
- **MemoryPanel UI**
  - Hub 的 Memory 模块新增可视化管理面板，支持 mem0 状态查看、连接测试和用户创建
  - 支持手动添加长期记忆、搜索记忆、刷新记忆列表和删除记忆
  - 新增记忆列表条目展示内容、创建时间、更新时间、状态和搜索相关度

### Changed
- Memory 模块从占位页面升级为可操作面板，并接入 `ChatWindow` 生命周期以在模块切换和窗口关闭时取消非必要请求

## [0.3.8] - 2026-05-09

### Added
- **知识库查询增强**
  - `manage_knowledge` 的 `query` action 新增 `top_k` 参数（默认 5，范围 1~50）
  - 查询结果中每条 source 新增 `document_name` 字段，显示来源文档名
- **知识库批量索引**
  - 新增 `index_folder` action：批量索引指定文件夹中的所有支持类型文件
  - 新增 `index_project_docs` action：一键自动索引 README.md、docs/、plans/、Assets/Docs/、Assets/Documentation/
  - KnowledgeBasePanel UI 新增 `[索引项目文档]` 按钮，提供一键索引入口
- **知识库索引进度查询**
  - 新增 `check_index_status` action：通过 `track_id` 查询异步索引进度
- **SOUL.md 知识库引导**
  - 新增 §12 知识库检索，明确 LLM 何时应查询/索引知识库，以及与记忆系统的区别

### Changed
- `manage_knowledge` 工具描述和参数 Schema 同步更新，覆盖全部 8 个 action
- TOOLS.md.template 知识库检索章节重写，包含完整工作流建议

## [0.3.7] - 2026-05-08

### Changed
- **Settings 界面重组**
  - 顶部新增 AgentCore 状态概览，集中显示 LLM、mem0、LightRAG 与工具启用状态
  - 将设置按用户工作流重排为 Setup、Agent、Context & Memory、Tools、Appearance、About & Diagnostics
  - mem0 与 LightRAG 改为可折叠卡片，默认降低可选云服务对主配置流程的干扰
  - Agent 高级 token/错误上限参数移动到 Advanced Limits 折叠区
  - About 区域移除过时 Phase 文案，改为显示包名与实际版本

### Added
- **Settings 诊断操作**
  - 新增 Diagnostics 区域，可快速测试 LLM、mem0、LightRAG 连接
  - 新增快速打开或创建 `MEMORY.md` / `USER.md` 的入口
  - Tool Management 新增安全模式与完整模式预设

### Fixed
- 统一 LLM、mem0、LightRAG 的连接状态显示逻辑，避免不同区域使用不一致的颜色和字符串判断

## [0.3.6] - 2026-05-07

### Added
- **ManageUIToolkitTool** — 全新 UI Toolkit 工具（`manage_ui_toolkit`），20 个 actions
  - 创建/编辑 UXML 文件：`create_uxml`, `add_element`, `remove_element`, `set_attribute`, `validate_uxml`
  - 创建/编辑 USS 文件：`create_uss`, `set_style`, `add_class`, `remove_class`
  - 查询与列举：`query_element`, `list_elements`, `get_uxml_content`, `get_uss_content`, `list_assets`, `list_ui_documents`
  - 运行时配置：`create_panel_settings`, `configure_ui_document`
  - 代码模板生成：`create_editor_window_template`, `create_custom_element_template`
  - 数据绑定：`add_binding`
  - 使用 `System.Xml.XmlDocument` 操作 UXML，直接使用 `UnityEngine.UIElements` 类型（无需反射）

- **ManageCinemachineTool 增强**（`manage_cinemachine`，29% → ~65%）
  - 新增 10 个 actions：`create_freelook`, `configure_freelook_orbits`, `create_state_driven`, `add_state_camera`, `create_clearshot`, `create_sequencer`, `add_sequencer_entry`, `create_dolly_track`, `configure_impulse`, `set_blend_list`
  - 支持 FreeLook 三轨道配置（top/mid/bot 半径和高度）
  - 支持 StateDriven 相机与 Animator 状态绑定
  - 支持 ClearShot、Sequencer、Dolly Track、Impulse 和 BlendList 相机类型
  - 所有新 handler 通过反射兼容 Cinemachine 2.x 和 3.x

- **ManageUITool 增强**（`manage_ui`，35% → ~65%）
  - 新增 9 个 actions：`align_elements`, `distribute_elements`, `delete_element`, `duplicate_element`, `set_text`, `set_image`, `set_interactable`, `reorder_element`, `find_element`
  - `set_text` 同时支持 `UnityEngine.UI.Text` 和 `TMPro.TextMeshProUGUI`（通过反射）
  - `align_elements` / `distribute_elements` 支持 X/Y 轴对齐和均匀分布
  - 更新描述以明确区分 legacy uGUI（`manage_ui`）和 UI Toolkit（`manage_ui_toolkit`）

- **ValidationTool** — 全新场景验证工具（`validation`），10 个 actions
  - `check_missing_references` — 使用 `SerializedObject` 迭代器检测丢失的对象引用
  - `check_duplicate_names` — 检测场景中重名的 GameObject
  - `check_empty_gameobjects` — 检测只有 Transform 且无子对象的空 GameObject
  - `check_missing_components` — 检测 null 组件槽（已删除的脚本）
  - `check_layer_tags` — 验证 Layer 索引和 Tag 有效性
  - `check_performance` — 检测高三角面数（>50K）、过多实时灯光（>4）、多摄像机（>3）等性能问题
  - `check_prefab_integrity` — 使用 `PrefabUtility` 检测断开/丢失的 Prefab 连接
  - `check_audio` — 检测 AudioSource 缺失 Clip、零音量、无 Clip 时 PlayOnAwake 等问题
  - `validate_scene` — 运行所有检查并汇总结果
  - `validate_project` — 检查 Build Settings、缺失场景文件、损坏脚本、PlayerSettings
  - 返回结构化 `ValidationIssue` 对象，包含 severity/category/path/message/fix_hint

- **ReadConsoleTool 增强**（`read_console`，50% → ~80%）
  - 新增 5 个 actions：`get_system_info`, `get_assembly_info`, `get_scripting_defines`, `set_scripting_define`, `get_log_file`
  - `get_system_info` — 返回 Unity 版本、OS、处理器、内存、图形设备、脚本后端、渲染管线等完整系统信息
  - `get_assembly_info` — 列出所有已加载程序集，支持名称过滤
  - `get_scripting_defines` — 获取指定 Build Target Group 的 Scripting Define Symbols
  - `set_scripting_define` — 添加或移除 Scripting Define Symbol（自动触发重编译）
  - `get_log_file` — 读取 Unity Editor 日志文件末尾 N 行，支持文本过滤，跨平台路径（Windows/macOS/Linux）

- **ManageProBuilderTool 增强**（`manage_probuilder`，45% → ~75%）
  - 新增 8 个 actions：`get_faces`, `extrude_faces`, `delete_faces`, `bevel_edges`, `bridge_edges`, `weld_vertices`, `set_uv_projection`, `triangulate`
  - `subdivide` 现在尝试 ProBuilder API，失败时回退到手动三角形四分法
  - 所有新 actions 优先使用 ProBuilder 反射 API，不可用时提供 Unity Mesh API 回退实现
  - UV 投影支持 planar/box/spherical/cylindrical 四种模式
  - `weld_vertices` 支持按距离阈值合并顶点
  - 新增辅助方法：`GetFacesData`, `GetFaceObjects`, `GetAllFaceObjects`, `GetEdgeObjects`
  - 新增 Mesh 回退方法：`SubdivideMeshFallback`, `DeleteMeshFacesFallback`, `WeldMeshVerticesFallback`, `GenerateUVsFallback`

- **WorkflowTool** — 全新工作流自动化工具（`workflow`），15 个 actions
  - 批量操作：`batch_rename`（支持 `{index}`, `{name}`, `{parent}` 占位符和格式化索引）, `batch_set_tag`, `batch_set_layer`, `batch_set_active`, `batch_set_static`
  - 查找替换：`find_replace_name`（支持纯文本和正则表达式）
  - 收集查询：`collect_by_component`, `collect_by_tag`, `collect_by_layer`
  - 层级操作：`snapshot_hierarchy`（导出场景树为 JSON）, `batch_move_to_parent`
  - 组件操作：`batch_add_component`, `batch_remove_component`
  - 统计分析：`count_objects`（按 tag/layer/component 统计）, `list_scenes`（列出所有场景）
  - 所有修改操作支持 `dry_run` 预览模式（不实际执行，仅返回将要发生的变更）
  - 所有修改操作支持 Undo（通过 `ToolHelpers.RecordUndo`）

## [0.3.5] - 2026-05-07

### Added
- **窗口级键盘快捷键**（Phase 4.3）
  - 快捷键现在在整个 ChatWindow 范围内有效，不再要求输入框必须聚焦
  - `Escape` — 取消当前 Agent 操作（全局有效，之前仅输入框聚焦时有效）
  - `Ctrl+N` — 新建会话（全局有效）
  - `Ctrl+Shift+E` — 导出当前会话（全局有效）
  - `Ctrl+/` 或 `Ctrl+?` — 新增：聚焦输入框（方便从消息区域快速回到输入）
  - 输入框内的快捷键行为不变（`Enter` 发送、`Shift+Enter` 换行）
  - 通过在 `rootVisualElement` 上注册 `KeyDownEvent` 实现窗口级监听

## [0.3.4] - 2026-05-07

### Added
- **LLM Model 发现式下拉菜单**（Settings 面板增强）
  - Settings 面板 LLM Configuration 区域新增 "Fetch" 按钮
  - 点击 Fetch 后自动向 `{endpoint}/models` 发起 HTTP GET 请求，获取服务器可用模型列表
  - 获取成功后在 Model 字段旁显示 Popup 下拉菜单，支持一键选择模型
  - 支持 OpenAI 标准 `/v1/models` 响应格式（`{"object":"list","data":[{"id":"..."}]}`）
  - 模型列表按字母排序，方便查找
  - Fetch 状态实时反馈：绿色 `[OK] 找到 N 个模型` / 红色 `[FAIL] 错误信息`
  - Fetch 与 Test Connection 按钮互斥，防止并发请求

### Changed
- **默认参数优化（针对 Claude 系列模型）**
  - `llmModel` 默认值：`"deepseek-chat"` → `"claude-sonnet-4-5"`
  - `maxTokens` 默认值：`4096` → `16000`（Claude 3.5/4 系列支持最大 16K 输出）
  - `reserveResponseTokens` 默认值：`2000` → `8000`（为长代码输出预留足够空间）
  - `AgentCoreSettings` 版本迁移升级至 v5（已有用户若仍使用旧默认值则自动迁移，自定义值不受影响）

## [0.3.3] - 2026-05-07

### Added
- **文件变更追踪与展示面板**（Phase 4.5）
  - `FileChangeTracker` — 追踪当前会话中所有工具调用产生的文件变更
    - 支持追踪 `manage_script`、`manage_file`、`manage_asset` 三类工具的文件操作
    - 执行前快照文件行数，执行后对比计算增减行数（`+N -N`）
    - 自动识别变更类型：新建（Created）、修改（Modified）、删除（Deleted）、移动（Moved）、复制（Copied）
    - 同一文件多次修改自动合并为一条摘要
    - **Domain Reload 持久化**：文件变更记录跨 Domain Reload 保留
      - `SerializeToJson()` / `RestoreFromJson()` — 序列化/反序列化变更记录
      - 在 `OnBeforeAssemblyReload` 中自动保存到 `DomainReloadState`
      - 在会话恢复时自动从 `DomainReloadState` 恢复
  - `FileChangeSummaryPanel` — 输入栏上方的可折叠文件变更汇总面板
    - 头部显示"此对话中已更改 N 个文件" + 总增减行数统计
    - 每行显示变更类型图标（彩色）、文件路径、增减行数
    - 单击文件行：在 Project 窗口中高亮定位（`EditorGUIUtility.PingObject`）
    - 双击文件行：在 IDE 中打开文件（`AssetDatabase.OpenAsset`）
    - 无变更时自动隐藏，有变更时自动显示
    - 会话切换/重置时自动清空
    - Domain Reload 后自动恢复显示
  - `AgentEventType.FileChangesUpdated` — 新增文件变更更新事件类型
  - `AgentEvent.FileChangesUpdated()` — 新增文件变更事件工厂方法
  - `AgentLoop.FileTracker` — 公开属性供 UI 层访问文件变更追踪器
  - `AgentLoop.EmitFileChangesUpdatedEvent()` — 公开方法供 UI 层在会话恢复后触发文件变更面板更新
  - `DomainReloadState.SaveFileChangeRecords()` / `ClearFileChangeRecords()` — 文件变更数据的持久化管理

## [0.3.2] - 2026-05-06

### Added
- **轻量级 Markdown 格式化**（Phase 4.1）
  - `ContentFilter.FormatMarkdown()` — 将 Markdown 语法转换为可读的纯文本格式（不使用任何 Rich Text 标签）
  - 标题格式化：`# H1` → `═══ H1 ═══`，`## H2` → `── H2 ──`，`### H3` → `【H3】`，`#### H4` → `▸ H4`
  - 表格格式化：解析 `| col | col |` 语法，生成对齐的纯文本表格（带 box-drawing 字符）
  - 粗体/斜体：`**text**` → `text`，`*text*` → `text`（直接去除标记符号）
  - 列表格式化：`- item` → `  · item`，`1. item` → `  1) item`
  - 代码块：保持内容不变，添加 `──── lang ────` 装饰分隔线
  - 引用块：`> text` → `  │ text`
  - 水平线：`---` → `────────────────────`
  - 内联代码：`` `code` `` → `[code]`
  - 链接：`[text](url)` → `text (url)`
  - CJK 字符宽度感知的表格列对齐
  - 集成到 `FilterStreaming()` 和 `FilterCompleted()` 双管线，流式输出和最终化时均自动格式化
  - 修复 `MessageBubble.FinalizeContent()` 双重过滤问题
- **工具启用/禁用管理**（Phase 4.4）
  - `AgentCoreSettings` 新增 `disabledToolCategories` 和 `disabledTools` 列表
  - `ToolDefinitionBuilder.BuildAllEnabled()` — 构建工具定义时自动过滤禁用工具
  - `AgentLoop.BuildToolDefinitions()` 使用过滤后的工具列表，减少 token 消耗
  - `BootstrapLoader.GenerateActiveToolsList()` 仅展示启用的工具
  - Settings 面板新增 **Tool Management** 区域：
    - 按分类折叠显示所有已注册工具
    - 支持按分类整体启用/禁用
    - 支持单个工具启用/禁用
    - 全部启用/全部禁用快捷按钮
    - 实时显示启用/禁用工具数量统计
- **错误重试 UI**（Phase 4.2）
  - 错误消息气泡底部显示「 重试」按钮
  - 点击重试按钮自动重新发送上一条用户消息
  - 重试按钮点击后自动禁用防止重复操作
  - `MessageBubble.AddRetryButton()` — 支持为错误气泡添加重试回调
- **结构化错误展示**（Phase 4.2 增强）
  - `ErrorDetail` 类 — 结构化错误信息，包含错误分类、异常类型、HTTP 状态码、堆栈摘要
  - `AgentEvent.ErrorEvent(Exception, string)` — 新增携带异常对象的错误事件重载
  - 错误气泡显示格式化的详细错误信息（错误类别、HTTP 状态码描述、异常类型、内部错误、上下文）
  - `MessageBubble.AddExpandableDetail()` — 可展开/折叠的堆栈信息区域
  - 错误自动分类：认证失败、网络错误、请求超时、速率限制、服务端错误、模型错误等
  - HTTP 状态码自动提取和中文描述（401/403/429/500/502/503 等）

### Changed
- `AgentCoreSettings` 版本迁移升级至 v4（初始化工具管理列表）
- `ToolDefinitionBuilder` 新增 `using AgentCore.Editor.Config` 依赖
- `BootstrapLoader` 工具列表生成逻辑增加禁用工具过滤和统计
- 错误气泡样式增强 — 左侧红色边框、更深背景色、更高对比度文字
- `AgentLoop` 错误事件传递完整异常对象（LLM 请求、Domain Reload 恢复）
- `ChatWindow.ShowError()` 支持 `ErrorDetail` 参数，展示结构化错误信息

## [0.3.1] - 2026-05-06

### Added
- **FileSystem 工具** — `manage_file`：通用文件系统操作工具
  - 支持 9 种操作：`read_file`, `write_file`, `list_directory`, `search_content`, `file_info`, `delete`, `copy`, `move`, `create_directory`
  - 支持所有文件类型（json, xml, yaml, txt, md, shader 等），不限于 .cs 文件
  - 支持项目根目录下所有路径（Assets/, Packages/, ProjectSettings/ 等）
  - 正则表达式内容搜索（类似 grep），带上下文行显示
  - 文件读取支持行范围（offset/limit）和行号显示
  - 路径安全检查，防止目录遍历攻击
  - 自动处理 Unity .meta 文件（删除/移动时同步处理）
  - 补充 `manage_script`（仅 C#）和 `manage_asset`（仅 AssetDatabase）的能力空白
- TOOLS.md.template 添加 FileSystem 操作指南和工具选择指南更新

### Fixed
- LightRAG 客户端兼容 LightRAG Server v1.4.15 API 变更
  - Health API 状态值从 `"ok"` 改为同时兼容 `"ok"` 和 `"healthy"`
  - Health API 版本字段从 `version` 改为优先使用 `core_version`（兼容旧版 `version`）
  - Query API 来源字段从 `sources` 改为优先使用 `references`（兼容旧版 `sources`）
  - 文件上传 API 路径从 `/documents/file` 修正为 `/documents/upload`
  - 默认端点端口从 `18920` 修正为 `9621`
- 会话标题不自动生成的 bug — 新会话始终显示"新会话"而不根据首条消息生成标题

## [0.3.0] - 2026-05-06

### Added
- Phase 1: 核心骨架 + Bootstrap Files
  - UPM 包结构
  - LLM 客户端（OpenAI 兼容 API + SSE 流式）
  - Bootstrap Files 系统（SOUL/TOOLS/PROJECT/MEMORY/USER）
  - Agent Loop 基础版（单轮对话）
  - Chat Window 基础 UI（UI Toolkit）

- Phase 2: 工具系统基础架构
  - `IAgentTool` 接口定义
  - `ToolRegistry` 工具注册中心
  - `ToolCallDispatcher` 工具调用分发器
  - `ToolDefinitionBuilder` 工具定义构建器（生成 OpenAI function calling schema）
  - `ToolResult` 标准化返回类型

- Phase 2.5: 原生工具系统（完全移除 unity-mcp 依赖）
  - **工具基础设施**
    - `AgentToolAttribute` — 工具标记属性（名称、分类、主线程要求等）
    - `ToolAutoDiscovery` — 基于反射的工具自动发现与注册机制
    - `ToolHelpers` — 参数解析、GameObject 查找、Vector/Color 解析等辅助方法
    - `ToolResponse` — 标准化 JSON 响应格式（Ok/OkWithData/Fail）
  - **Core 工具（5 个）** — 场景与对象操作
    - `manage_scene` — 场景 CRUD（创建/加载/保存/获取层级）
    - `manage_gameobject` — GameObject 创建/修改/删除/复制
    - `manage_component` — 组件添加/移除/属性设置
    - `find_gameobjects` — 按名称/标签/层/组件类型搜索
    - `scene_analysis` — 场景分析（层级统计、性能热点、依赖关系）
  - **Meta 工具（3 个）** — 编辑器控制
    - `manage_editor` — 编辑器状态控制（Play/Pause/Stop/标签/层）
    - `execute_menu_item` — 执行 Unity 菜单项
    - `batch_execute` — 批量执行多个工具调用
  - **Scripting 工具（4 个）** — 代码与预制体
    - `manage_script` — C# 脚本创建/读取/删除
    - `execute_code` — 在编辑器中执行 C# 代码片段
    - `manage_prefab` — 预制体信息/层级/内容修改
    - `manage_scriptable_object` — ScriptableObject 资产创建/读取/修改
  - **Specialized 工具（11 个）** — 专业领域
    - `manage_physics` — 物理设置/碰撞矩阵/射线检测/力
    - `manage_lighting` — 光照/烘焙/探针/环境设置
    - `manage_graphics` — 渲染管线/后处理/Volume
    - `manage_audio` — 音频源/监听器/混音器
    - `manage_ui` — UI Toolkit 文档/样式/面板
    - `manage_camera` — 相机设置/视口/渲染目标
    - `manage_cinemachine` — Cinemachine 虚拟相机/轨道（可选包，反射调用）
    - `manage_event` — UnityEvent 检查/绑定/触发
    - `manage_probuilder` — ProBuilder 网格编辑（可选包，反射调用）
    - `manage_terrain` — 地形创建/高度图/纹理/树木/细节
    - `manage_timeline` — Timeline 轨道/剪辑/信号（可选包，反射调用）
  - **Utility 工具（8 个）** — 资产与材质
    - `manage_asset` — 资产搜索/创建/导入/移动
    - `manage_material` — 材质创建/属性设置/分配
    - `manage_shader` — Shader CRUD
    - `manage_animation` — 动画控制器/剪辑/状态机
    - `read_console` — 读取 Unity Console 日志/错误/警告
    - `manage_asset_import` — 资产导入设置（通用）
    - `manage_model_import` — 模型导入设置（FBX/OBJ 等）
    - `manage_texture_import` — 纹理导入设置（压缩/尺寸/格式）
  - **Extended 工具（10 个）** — 扩展功能
    - `manage_build` — 构建设置/平台切换/触发构建
    - `manage_input` — 输入系统/Action Map 管理
    - `manage_navmesh` — 导航网格烘焙/代理/障碍物
    - `manage_profiler` — 性能分析/帧计时/内存
    - `manage_tags_layers` — 标签与层的增删管理
    - `manage_package` — UPM 包安装/查询/移除
    - `manage_test` — 测试运行/列表/模板创建（Test Framework）
    - `cleaner` — 清理缺失引用/未使用资产/空 GameObject
    - `optimization` — 性能优化建议/批量优化操作
    - `smart_operations` — 智能批量操作（对齐/分布/替换/重命名）

- Phase 3: 云端工具与会话管理
  - **mem0 记忆服务**
    - `Mem0Client` — mem0 REST API 客户端（连接测试、记忆 CRUD、用户管理）
    - `Mem0Tool`（`manage_memory`）— 记忆管理工具（search/add/list/delete）
    - `AutoMemoryStrategy` — 会话结束时自动提取关键信息存入 mem0
  - **LightRAG 知识库**
    - `LightRAGClient` — LightRAG REST API 客户端（查询、索引、健康检查）
    - `LightRAGTool`（`manage_knowledge`）— 知识库管理工具（query/index_text）
  - **会话管理**
    - `SessionManager` / `SessionStorage` / `SessionData` — 会话持久化与恢复
    - `SessionExporter` — 会话导出（Markdown / JSON）
    - 多会话侧边栏 — 会话列表、切换、重命名、删除
  - **上下文窗口管理**
    - `ContextWindowManager` — 基于 token 计数的滑动窗口截断
    - `TokenCounter` — 消息 token 估算
  - **核心增强**
    - `FallbackRouter` — LLM 请求自动重试（可重试错误判断）
    - `CompilationWatcher` — 编译监控与 Domain Reload 恢复
    - `ConsoleErrorCapture` — Console 错误自动捕获
    - `DomainReloadState` — Domain Reload 状态持久化与恢复
    - `ErrorInfoCollector` — 错误信息收集与格式化
  - **Settings 面板**
    - LLM 连接配置与测试
    - mem0 服务配置与连接测试
    - LightRAG 服务配置与连接测试
    - Agent 行为参数（maxToolCallRounds、上下文窗口等）
    - Bootstrap 文件管理（MEMORY.md / USER.md 创建与打开）
    - UI 偏好设置

### Removed
- Phase 2.5: 完全移除 unity-mcp 外部依赖
  - 不再依赖 `com.coplaydev.unity-mcp` 包
  - 不再需要 Python MCP Server 桥接层
  - 所有 Unity 操作通过原生 C# 工具直接执行
