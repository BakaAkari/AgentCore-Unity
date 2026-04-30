# AgentCore vs Unity Skills — 能力覆盖度分析报告

> 生成时间: 2026-04-29
> 分析范围: `_archive/Unity-Skills/SkillsForUnity/Editor/Skills/` vs `Editor/Tools/Native/`

---

## 1. 总体概览

| 维度 | Unity Skills (归档) | AgentCore (当前) | 覆盖率 |
|------|-------------------|-----------------|--------|
| **技能/工具文件数** | 40+ Skill 文件 | 25 Tool 文件 | — |
| **独立技能/Action 数** | 300+ 个 `[UnitySkill]` | ~70 个 actions | ~23% |
| **功能分类数** | 40 个分类 | 18 个分类 | ~45% |
| **代码总行数** | ~20,000+ 行 | ~8,000+ 行 | ~40% |

### 架构差异说明

- **Unity Skills**: HTTP Server 模式，每个 skill 是独立的静态方法，通过 `[UnitySkill]` 注册，粒度极细（每个操作一个 skill）
- **AgentCore**: LLM Tool Calling 模式，每个工具是一个类，通过 `action` 参数分发，粒度较粗（一个工具包含多个 action）

这意味着 AgentCore 的 25 个工具文件理论上可以通过增加 action 来覆盖更多功能，但当前 action 数量远少于 Unity Skills 的独立技能数。

---

## 2. 逐分类对比分析

### ✅ 已覆盖（基本对齐）

| 分类 | Unity Skills | AgentCore Tool | 覆盖评估 |
|------|-------------|----------------|----------|
| **GameObject CRUD** | `GameObjectSkills` (18 skills: create, rename, delete, find, set_transform, duplicate, set_parent, get_info, set_active + batch 版本) | `manage_gameobject` (7 actions: create, delete, get_info, modify, set_transform, set_parent, duplicate) + `find_gameobjects` | ⚠️ **80%** — 缺少 batch 操作 (create_batch, delete_batch, rename_batch, set_transform_batch, set_active_batch, set_layer_batch, set_tag_batch, set_parent_batch) |
| **Component 管理** | `ComponentSkills` (11 skills: add, remove, list, set_property, get_properties, copy, set_enabled + batch 版本) | `manage_component` (6 actions: add, remove, get, list, modify, set_enabled) | ⚠️ **75%** — 缺少 batch 操作和 copy |
| **Scene 管理** | `SceneSkills` (10 skills: create, load, save, get_info, get_hierarchy, screenshot, get_loaded, unload, set_active, find_objects) | `manage_scene` (7 actions: list, get_hierarchy, get_active, create, open, save, set_active) | ⚠️ **70%** — 缺少 screenshot、unload、find_objects |
| **Material 管理** | `MaterialSkills` (21 skills: create, assign, duplicate, set_color/texture/float/int/vector/emission/keyword/render_queue/shader/gi_flags, get_properties/keywords + batch 版本) | `manage_material` (6 actions: create, get_info, set_property, set_shader, list_properties, assign) | ⚠️ **60%** — 缺少 duplicate、batch 操作、细粒度属性设置 |
| **Lighting** | `LightSkills` (10 skills: create, set_properties, get_info, find_all, set_enabled, add_probe_group, add_reflection_probe, get_lightmap_settings + batch 版本) | `manage_lighting` (6 actions: create, modify, get_info, list, bake, get_lightmap_settings) | ✅ **85%** — 基本覆盖，缺少 probe 管理和 batch |
| **Physics** | `PhysicsSkills` (12 skills: raycast, check_overlap, get/set_gravity, raycast_all, spherecast, boxcast, overlap_box, create/set_material, get/set_layer_collision) | `manage_physics` (6 actions: get_settings, set_settings, add_rigidbody, add_collider, add_joint, raycast) | ⚠️ **50%** — 缺少 spherecast、boxcast、overlap、physics material、layer collision 管理 |
| **Audio** | `AudioSkills` (10 skills: get/set_settings, find_clips, get_clip_info, add_source, get/set_source_properties, find_sources, create_mixer) | `manage_audio` (7 actions: add_source, modify_source, play, stop, get_info, list, get_settings) | ⚠️ **65%** — 缺少 mixer 创建、clip 搜索、batch 设置 |
| **Prefab** | `PrefabSkills` (11 skills: create, instantiate, apply, unpack, get_overrides, revert_overrides, apply_overrides, create_variant, find_instances, set_property + batch) | `manage_prefab` (6 actions: create, instantiate, get_info, unpack, apply, revert) | ⚠️ **60%** — 缺少 create_variant、find_instances、get/apply/revert_overrides 细粒度控制 |
| **Script 管理** | `ScriptSkills` (12 skills: create, read, delete, find_in_file, append, replace, list, get_info, rename, move, get_compile_feedback + batch) | `manage_script` (6 actions: read, write, create, delete, list, get_info) | ⚠️ **65%** — 缺少 find_in_file、append、replace、rename、move |
| **Asset 管理** | `AssetSkills` (11 skills: import, delete, move, duplicate, find, create_folder, refresh, get_info + batch 版本) | `manage_asset` (8 actions: search, get_info, create_folder, delete, move, copy, import, get_dependencies) | ✅ **85%** — 较好覆盖，有 get_dependencies 是加分项 |
| **Animation** | `AnimatorSkills` (10 skills: create_controller, add_parameter, get/set_parameter, play, get_info, assign_controller, list_states, add_state, add_transition) | `manage_animation` (6 actions: list_clips, get_clip_info, list_parameters, set_parameter, get_controller_info, list_animator_states) | ⚠️ **50%** — 缺少 create_controller、add_state、add_transition、assign_controller |
| **Shader** | `ShaderSkills` (11 skills: create, read, list, get_properties, find, delete, check_errors, get_keywords, get_variant_count, create_urp, set_global_keyword) | `manage_shader` (4 actions: list, get_info, find, list_keywords) | ⚠️ **40%** — 缺少 create、delete、check_errors、create_urp |
| **NavMesh** | `NavMeshSkills` (10 skills: bake, clear, calculate_path, add_agent, set_agent, add_obstacle, set_obstacle, sample_position, set_area_cost, get_settings) | `manage_navmesh` (6 actions: bake, clear, get_settings, add_agent, add_obstacle, set_area) | ⚠️ **60%** — 缺少 calculate_path、sample_position、set_agent/obstacle 修改 |
| **Build** | `ProjectSkills` (11 skills) | `manage_build` (6 actions: get_settings, set_target, get_scenes, set_scenes, build, get_player_settings) | ✅ **80%** — 基本覆盖构建流程 |
| **Editor 控制** | `EditorSkills` (12 skills: play, stop, pause, select, get_selection, undo, redo, get_state, execute_menu, get_tags, get_layers, get_context) | `manage_editor` (8 actions) + `execute_menu_item` (3 actions) + `manage_tags_layers` (9 actions) | ✅ **90%** — 覆盖较好 |
| **Console** | `ConsoleSkills` (10 skills) + `DebugSkills` (10 skills) | `read_console` (5 actions) | ⚠️ **40%** — 缺少 debug 系统信息、assembly 信息、defines 管理 |
| **Profiler** | `ProfilerSkills` (10 skills: get_stats, get_memory, get_runtime/texture/mesh/material/audio_memory, get_object_count, get_rendering_stats, get_asset_bundle_stats) | `manage_profiler` (5 actions: get_stats, get_memory, start/stop_recording, get_rendering_stats) | ⚠️ **50%** — 缺少细粒度内存分析 |
| **UI (uGUI)** | `UISkills` (26+ skills: create_canvas/panel/button/text/image/inputfield/slider/toggle/dropdown/scrollview/rawimage/scrollbar, set_text/image/anchor/rect, layout_children, align/distribute_selected, add_layout_element/canvas_group/mask/outline, configure_selectable + batch) | `manage_ui` (5 actions: create_canvas, create_element, modify_element, get_info, list) | ⚠️ **35%** — 严重不足，缺少大量 UI 操作 |

### ❌ 完全缺失的分类

| 分类 | Unity Skills 能力 | 技能数 | 重要性 | 说明 |
|------|-------------------|--------|--------|------|
| **🔴 Terrain** | `TerrainSkills`: 创建地形、获取/设置高度、添加山丘、Perlin 噪声生成、平滑、展平、绘制纹理 | 10 | **高** | 地形编辑是 3D 游戏开发核心功能 |
| **🔴 Cinemachine** | `CinemachineSkills`: 虚拟相机创建/配置、目标设置、Body/Aim/Noise 组件、Brain 管理、混合、脉冲、扩展、FreeLook、StateDriven、ClearShot、Sequencer 等 | 30+ | **高** | 专业相机系统，现代 Unity 项目标配 |
| **🔴 Timeline** | `TimelineSkills`: 创建 Timeline、添加 Audio/Animation/Activation/Control/Signal 轨道、管理 Clip、播放控制、绑定 | 12 | **高** | 过场动画和序列编辑核心 |
| **🔴 Perception/分析** | `PerceptionSkills`: 场景组件统计、热点分析、健康检查、契约验证、技术栈检测、场景分析/摘要、层级描述、脚本分析、空间查询、材质概览、场景上下文、导出报告、依赖分析、脚本依赖图、Tag/Layer 统计、性能提示、场景 Diff | 18 | **极高** | AI Agent 的"眼睛"——理解场景和项目的核心能力 |
| **🔴 Smart 操作** | `SmartSkills`: 智能场景查询、场景布局、引用绑定、空间查询、对齐到地面、分布、网格吸附、随机化变换、替换对象、按组件选择 | 10 | **高** | 高级编辑操作，提升 AI 效率 |
| **🔴 Optimization** | `OptimizationSkills`: 纹理优化、网格压缩、场景分析、大资源查找、静态标记、音频压缩、重复材质查找、过度绘制分析、LOD 组 | 10 | **高** | 性能优化是专业开发必需 |
| **🔴 Cleaner** | `CleanerSkills`: 查找未使用资源、重复资源、缺失引用、删除资源、获取资源使用情况、空文件夹、大资源、修复缺失脚本、依赖树 | 10 | **中高** | 项目清理和维护 |
| **🔴 Event 系统** | `EventSkills`: 获取/添加/移除监听器、调用事件、清除监听器、设置监听器状态、列出事件、批量添加、复制监听器 | 10 | **中** | UnityEvent 管理 |
| **🔴 ScriptableObject** | `ScriptableObjectSkills`: 创建、获取/设置属性、列出类型、复制、批量设置、删除、查找、导出/导入 JSON | 10 | **中高** | 数据驱动开发核心 |
| **🔴 Texture 管理** | `TextureSkills`: 获取/设置导入设置、批量设置、查找资源、获取信息、设置类型、平台设置、Sprite 设置、按尺寸查找 | 10 | **中** | 纹理资源管理 |
| **🔴 Model 管理** | `ModelSkills`: 获取/设置导入设置、批量设置、查找模型、网格信息、材质信息、动画信息、动画切片、Rig 信息/设置 | 10 | **中** | 3D 模型资源管理 |
| **🔴 Asset Import** | `AssetImportSkills`: 重新导入、批量重新导入、纹理/模型/音频/Sprite 导入设置、获取导入设置、标签管理 | 12 | **中** | 资源导入管线控制 |
| **🔴 Package 管理** | `PackageSkills`: 列出/检查/安装/移除/刷新包、安装 Cinemachine/Splines、搜索、获取依赖/版本 | 11 | **中** | UPM 包管理 |
| **🔴 ProBuilder** | `ProBuilderSkills`: 创建形状、挤出面/边、删除/合并/翻转面、倒角、细分、焊接顶点、设置材质、获取信息、中心枢轴、UV 投影、批量创建、移动/设置顶点、合并网格 | 25+ | **中** | 编辑器内 3D 建模 |
| **🔴 XR** | `XRSkills`: XR 设置检查、Rig 创建、交互管理器、事件系统、射线/直接/插槽交互器、抓取/简单交互物、传送、连续移动/转向、UI Canvas XR 兼容、触觉反馈、交互事件、交互层 | 20+ | **低-中** | VR/AR 开发（特定项目需要） |
| **🔴 UI Toolkit** | `UIToolkitSkills`: UI Toolkit 相关操作 | 未统计 | **中** | 新一代 UI 系统 |
| **🔴 Validation** | `ValidationSkills`: 场景验证相关 | 未统计 | **中** | 质量保证 |
| **🔴 Workflow** | `WorkflowSkills`: 工作流管理 | 未统计 | **低** | 工作流自动化 |
| **🔴 Test** | `TestSkills`: 测试运行/结果/列表/取消/创建测试 | 11 | **中** | 自动化测试 |
| **🔴 Batch 系统** | `BatchSkills`: 批量查询/预览/执行、作业管理、修复缺失脚本、标准化命名、替换材质、验证场景对象、清理临时对象 | 21 | **高** | 批量操作效率 |

---

## 3. 关键差距分析

### 3.1 最严重的缺失（建议优先补充）

#### 🔴 P0 — Perception/场景分析能力（影响 AI 理解力）

这是 **最关键的缺失**。Unity Skills 的 `PerceptionSkills` 有 3056 行代码、18 个技能，提供了：

- **场景健康检查**: 缺失脚本、缺失引用、重复名称、空节点、深层级
- **技术栈检测**: 渲染管线、UI 路线、输入系统、主要包
- **场景分析/摘要**: 对象计数、组件统计、层级深度
- **依赖分析**: 对象依赖图、脚本依赖图、影响分析
- **空间查询**: 按半径查找对象
- **性能提示**: 诊断场景性能问题
- **场景 Diff**: 对比场景快照变化

**没有这些能力，AI Agent 就像"盲人"——只能操作但不能理解场景。**

AgentCore 当前只有 `manage_scene.get_hierarchy` 提供基本的层级信息，远远不够。

#### 🔴 P0 — Batch 操作能力（影响 AI 效率）

Unity Skills 几乎每个分类都有 batch 版本（`_batch` 后缀），而 AgentCore 虽然有 `batch_execute` 工具，但各个工具本身不支持批量参数。这意味着：

- 创建 10 个 GameObject 需要 10 次工具调用（而不是 1 次）
- 设置 20 个对象的属性需要 20 次调用
- 每次调用都有 LLM 往返延迟

#### 🔴 P1 — Terrain 地形编辑

3D 游戏开发的核心功能，完全缺失。

#### 🔴 P1 — Cinemachine 相机系统

现代 Unity 项目的标配相机解决方案，30+ 技能完全缺失。

#### 🔴 P1 — Timeline 时间线

过场动画和序列编辑的核心工具，完全缺失。

#### 🔴 P1 — ScriptableObject 管理

数据驱动开发的核心，完全缺失。

### 3.2 中等优先级缺失

| 缺失 | 影响 |
|------|------|
| **Smart 操作** | 对齐、分布、网格吸附等高级编辑操作 |
| **Optimization** | 性能优化分析和自动修复 |
| **Cleaner** | 项目清理和维护 |
| **Event 系统** | UnityEvent 管理 |
| **Package 管理** | UPM 包安装/管理 |
| **Test 系统** | 自动化测试运行 |

### 3.3 现有工具的深度不足

即使在已覆盖的分类中，AgentCore 的 action 数量也普遍只有 Unity Skills 的 50-70%：

| 工具 | AgentCore Actions | Unity Skills 等效 | 缺失的关键能力 |
|------|------------------|-------------------|---------------|
| `manage_ui` | 5 | 26+ | 缺少 layout、align、distribute、anchor 预设、组件添加 |
| `manage_physics` | 6 | 12 | 缺少 spherecast、boxcast、overlap、physics material |
| `manage_animation` | 6 | 10 | 缺少 create_controller、add_state/transition |
| `manage_shader` | 4 | 11 | 缺少 create、delete、check_errors |
| `manage_material` | 6 | 21 | 缺少 batch、细粒度属性设置 |

---

## 4. AgentCore 的独有优势

尽管覆盖度不足，AgentCore 有一些 Unity Skills 没有的能力：

| 能力 | 说明 |
|------|------|
| **execute_code** | 可以执行任意 C# 表达式，理论上可以弥补任何缺失的工具 |
| **execute_menu_item** | 可以执行任何 Unity 菜单项，间接访问大量功能 |
| **Cloud 工具** | Mem0 记忆系统 + LightRAG 知识库，Unity Skills 没有 |
| **batch_execute** | 批量执行多个工具调用（但不如原生 batch 高效） |
| **manage_asset.get_dependencies** | 资源依赖分析，Unity Skills 的 AssetSkills 没有 |
| **manage_editor.set_project_setting** | 项目设置修改能力 |

**特别是 `execute_code` 工具**，它是一个"万能后门"——任何 Unity Skills 有而 AgentCore 没有的功能，理论上都可以通过 `execute_code` 来实现。但这要求 LLM 知道正确的 Unity API，且执行效率和可靠性不如专用工具。

---

## 5. 改进建议

### 5.1 短期（P0 — 立即行动）

1. **新增 `PerceptionTool`** — 从 `PerceptionSkills` 移植核心分析能力
   - `scene_analyze`: 综合场景分析
   - `scene_health_check`: 场景健康检查
   - `scene_summarize`: 场景摘要
   - `project_stack_detect`: 技术栈检测
   - `scene_dependency_analyze`: 依赖分析
   - `scene_performance_hints`: 性能提示

2. **增强现有工具的 batch 支持** — 在 `manage_gameobject`、`manage_component` 等工具中添加 batch action

### 5.2 中期（P1 — 1-2 周）

3. **新增 `ManageTerrainTool`** — 地形编辑
4. **新增 `ManageCinemachineTool`** — Cinemachine 相机
5. **新增 `ManageTimelineTool`** — Timeline 编辑
6. **新增 `ManageScriptableObjectTool`** — ScriptableObject CRUD
7. **新增 `ManagePackageTool`** — UPM 包管理

### 5.3 长期（P2 — 按需）

8. **新增 `SmartOperationsTool`** — 智能编辑操作
9. **新增 `OptimizationTool`** — 性能优化
10. **新增 `CleanerTool`** — 项目清理
11. **新增 `ManageEventTool`** — UnityEvent 管理
12. **新增 `ManageTestTool`** — 自动化测试
13. **增强 `manage_ui`** — 补充缺失的 UI 操作
14. **增强 `manage_physics`** — 补充高级物理查询
15. **新增 `ManageProBuilderTool`** — ProBuilder 建模（按需）
16. **新增 `ManageXRTool`** — XR 开发（按需）

### 5.4 架构建议

- **优先利用 `execute_code`**: 在专用工具开发完成前，可以在 SOUL.md 或 TOOLS.md 中添加常用 Unity API 的使用指南，让 LLM 通过 `execute_code` 临时弥补缺失
- **参考 Unity Skills 的实现**: `_archive/Unity-Skills/` 中的代码可以直接作为参考，但需要适配 AgentCore 的工具架构（`IAgentTool` + `[AgentTool]` + action 分发模式）
- **Perception 能力最优先**: 这是 AI Agent 区别于简单工具集的关键——理解场景的能力比操作场景的能力更重要

---

## 6. 结论

**AgentCore 当前对 Unity 的操作能力覆盖了 Unity Skills 约 23% 的功能点，在已覆盖的分类中平均达到 60-70% 的深度。**

最关键的缺失是 **Perception/场景分析能力**（AI 的"眼睛"）和 **Batch 操作**（AI 的"效率"）。其次是 Terrain、Cinemachine、Timeline、ScriptableObject 等专业领域工具。

好消息是 `execute_code` 工具提供了一个万能后门，可以在专用工具开发完成前临时弥补大部分缺失。但长期来看，专用工具的可靠性、效率和 LLM 友好度远优于通用代码执行。

建议按 P0 → P1 → P2 的优先级逐步补充，首先聚焦于 Perception 能力和 Batch 支持。
