# v1.9.0 Candidate Matrix — 工具覆盖面 P1/P2 深度审计

> **状态**: 起草中 (2026-07-23)
> **产出目标**: 对 v1.8.1 后待办的 P1 11 项 + P2 10 项每项建审计卡片, 便于用户拍板 v1.9.0 具体范围
> **前置资料**:
> - [`plans/HANDOFF-v1.8.0-to-v1.9.0.md §2.1-2.2`](../HANDOFF-v1.8.0-to-v1.9.0.md) — 缺口清单
> - [`plans/capability-coverage-audit.md`](../capability-coverage-audit.md) — 审计方法论 (四步实证)
> - [`tools/tool-inventory.cjs`](../../tools/tool-inventory.cjs) — 47 工具全景抽取脚本 (--json 出 inventory)

## 每张卡片结构 (对照 capability-coverage-audit §四步实证 + §分级模板)

1. **场景推演** — 3-5 个真实使用场景, 用户视角描述
2. **Unity API 表** — 涉及的完整命名空间和 API 表 (含反射/可选 package 依赖标注)
3. **现有覆盖诊断** — grep 47 工具/408 actions 的相关调用, 判定根因分类:
    - `NO_TOOL` — 完全没工具
    - `SHALLOW_TOOL` — 有工具但覆盖 API 一小部分
    - `DISCOVERABILITY` — 工具存在但 agent 找不到
    - `COMPOSITION` — 单个工具够用, 组合工作流不流畅
    - `NOT_A_GAP` — execute_code 已能通用干
4. **建议 action 接口** — action 名 + 参数 schema 建议 + 归属工具决策 (深化现有 vs 新建)
5. **前置依赖** — Version Defines / 反射 / Undo 契约 / Play Mode 约束
6. **投入估算** — 单位: 半天/一天/多天

## 卡片索引

| ID | 领域 | 描述 | 卡片文件 | 状态 |
|---|---|---|---|---|
| G04 | Profiler | MemoryProfiler snapshot/diff | [`G04-memory-profiler.md`](G04-memory-profiler.md) | [-] draft |
| G05 | Profiler | PhysicsDebugger 数据 | [`G05-physics-debugger.md`](G05-physics-debugger.md) | [-] draft |
| G06 | Workflow | Selection API 深化 | [`G06-selection-deep.md`](G06-selection-deep.md) | [-] draft |
| G07 | Workflow | CompilationPipeline | [`G07-compilation-pipeline.md`](G07-compilation-pipeline.md) | [ ] pending |
| G08 | Workflow | EditorPrefs / PlayerPrefs | [`G08-prefs.md`](G08-prefs.md) | [ ] pending |
| G09 | Workflow | SceneView 相机 pivot/size | [`G09-sceneview-camera.md`](G09-sceneview-camera.md) | [ ] pending |
| G11 | Rendering | Occlusion Culling bake | [`G11-occlusion-culling.md`](G11-occlusion-culling.md) | [ ] pending |
| G12 | Rendering | Lightmapping GI 深度 | [`G12-lightmapping.md`](G12-lightmapping.md) | [ ] pending |
| G13 | Rendering | Sprite Editor slice/meta | [`G13-sprite-editor.md`](G13-sprite-editor.md) | [ ] pending |
| G14 | Asset | Presets 读写 / 应用 | [`G14-presets.md`](G14-presets.md) | [ ] pending |
| G15 | Meta | Unity Search API | [`G15-search-service.md`](G15-search-service.md) | [ ] pending |
| P2-a | Package (opt) | Addressables | [`P2-a-addressables.md`](P2-a-addressables.md) | [ ] pending |
| P2-b | Package (opt) | Localization | [`P2-b-localization.md`](P2-b-localization.md) | [ ] pending |
| P2-c | Package (opt) | Netcode | [`P2-c-netcode.md`](P2-c-netcode.md) | [ ] pending |
| P2-d | Package (opt) | XR | [`P2-d-xr.md`](P2-d-xr.md) | [ ] pending |
| P2-e | Package (opt) | Video | [`P2-e-video.md`](P2-e-video.md) | [ ] pending |
| P2-f | Package (opt) | TextMeshPro | [`P2-f-tmp.md`](P2-f-tmp.md) | [ ] pending |
| P2-g | Debugger | IMGUI Debugger | [`P2-g-imgui-debugger.md`](P2-g-imgui-debugger.md) | [ ] pending |
| P2-h | Rendering | Shader variant | [`P2-h-shader-variant.md`](P2-h-shader-variant.md) | [ ] pending |
| P2-i | Meta | RevealInFinder | [`P2-i-reveal-in-finder.md`](P2-i-reveal-in-finder.md) | [ ] pending |
| P2-j | Asset | .unitypackage export | [`P2-j-unitypackage-export.md`](P2-j-unitypackage-export.md) | [ ] pending |

> **注**: P2 具体 10 项名单是从 [`HANDOFF §2.2`](../HANDOFF-v1.8.0-to-v1.9.0.md#22-p2-能力覆盖缺口-10-项) "Addressables/Localization/Netcode/XR/Video/TMP/IMGUI Debugger 等"扩展出来的候选, 实际写卡片时可能合并/拆分/替换.

## 拍板前置

审计完成后按 [`capability-coverage-audit §分级模板`](../capability-coverage-audit.md) 输出:
- **P0_CRITICAL_ANALYSIS** / **P0_CRITICAL_WORKFLOW** — 建议进入 v1.9.0 主体
- **P1_MEDIUM_XXX** — 备选, 视 v1.9.0 时间预算加入
- **P2_EVALUATE** — 建议顺延到 v1.10.0+
- **NOT_A_GAP_OR_LOW** — 明确排除, 记录理由

## 变更历史

- 2026-07-23 首次起草, 已完成: (待更新)
