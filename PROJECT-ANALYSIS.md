# AgentCore vs Unity Skills — 能力覆盖度分析报告

> 生成时间: 2026-05-07 (修订版 — 纠正 2026-04-29 初版的数据错误)
> 分析范围: `_archive/Unity-Skills/SkillsForUnity/Editor/Skills/` vs `Editor/Tools/`

---

## 1. 总体概览

| 维度 | Unity Skills (归档) | AgentCore (当前) | 覆盖率 |
|------|-------------------|-----------------|--------|
| **技能/工具文件数** | 41 个 `*Skills.cs` | 42 个 `*Tool.cs` | ~102% |
| **独立技能/Action 数** | **554** 个 `[UnitySkill]` | **335** 个 actions | **~60%** |
| **功能分类覆盖** | 41 个分类 | 36 个分类 (含独有) | **~88%** |
| **完全缺失的分类** | — | 5 个 | — |

###  初版报告修正说明

初版报告（2026-04-29）声称覆盖率约 23%（~70 actions / 300+ skills），这是**严重低估**：

1. **Unity Skills 数量被低估**: 实际有 **554** 个 `[UnitySkill]`，不是 "300+"
2. **AgentCore Actions 被严重低估**: 实际有 **335** 个 actions，不是 "~70"
3. **初版是在项目早期编写的**，之后大量工具被新增和扩展（如 Terrain、Cinemachine、Timeline、ScriptableObject、ProBuilder、SmartOperations、Optimization、Cleaner、Event 等工具都是后来添加的）

**修正后的覆盖率: 335/554 ≈ 60%**（按 action 数量计），**分类覆盖率约 88%**。

### 架构差异说明

- **Unity Skills**: HTTP Server 模式，每个 skill 是独立的静态方法，通过 `[UnitySkill]` 注册，粒度极细（每个操作一个 skill）
- **AgentCore**: LLM Tool Calling 模式，每个工具是一个类，通过 `action` 参数分发，粒度较粗（一个工具包含多个 action）

---

## 2. 逐分类对比分析

###  已覆盖且基本对齐（17 个分类）

| Unity Skills | Skills数 | AgentCore Tool | Actions数 | 覆盖评估 |
|-------------|----------|----------------|-----------|----------|
| `AnimatorSkills` | 10 | `ManageAnimationTool` | 9 |  **90%** — 新增 get_layers, set_layer_weight, create_animation_clip |
| `AssetImportSkills` | 11 | `ManageAssetImportTool` | 9 |  **82%** |
| `AssetSkills` | 11 | `ManageAssetTool` | 8 |  **73%** — 有 get_dependencies 加分项 |
| `CameraSkills` | 11 | `ManageCameraTool` | 9 |  **82%** — 新增 render_to_texture |
| `CleanerSkills` | 10 | `CleanerTool` | 10 |  **100%** — 完全覆盖 |
| `ComponentSkills` | 10 | `ManageComponentTool` | 11 |  **110%** — 超越，含 batch 操作 |
| `EditorSkills` | 12 | `ManageEditorTool` + `ExecuteMenuItemTool` + `ManageTagsLayersTool` | 8+3+9=20 |  **167%** — 大幅超越 |
| `EventSkills` | 10 | `ManageEventTool` | 8 |  **80%** |
| `ModelSkills` | 10 | `ManageModelImportTool` | 10 |  **100%** — 完全覆盖 |
| `OptimizationSkills` | 10 | `OptimizationTool` | 10 |  **100%** — 完全覆盖 |
| `PackageSkills` | 11 | `ManagePackageTool` | 9 |  **82%** |
| `PhysicsSkills` | 12 | `ManagePhysicsTool` | 10 |  **83%** — 新增 overlap_test, configure_collision |
| `SceneSkills` | 10 | `ManageSceneTool` | 15 |  **150%** — 大幅超越 |
| `ScriptableObjectSkills` | 10 | `ManageScriptableObjectTool` | 10 |  **100%** — 完全覆盖 |
| `ScriptSkills` | 12 | `ManageScriptTool` | 10 |  **83%** — 新增 analyze, find_references, add_method, add_field |
| `TerrainSkills` | 10 | `ManageTerrainTool` | 10 |  **100%** — 完全覆盖 |
| `TextureSkills` | 10 | `ManageTextureImportTool` | 10 |  **100%** — 完全覆盖 |

###  已覆盖但深度不足（14 个分类）

| Unity Skills | Skills数 | AgentCore Tool | Actions数 | 覆盖评估 | 主要缺失 |
|-------------|----------|----------------|-----------|----------|----------|
| `AudioSkills` | 10 | `ManageAudioTool` | 7 |  **70%** | mixer 创建、clip 搜索 |
| `CinemachineSkills` | 34 | `ManageCinemachineTool` | 10 |  **29%** | FreeLook、StateDriven、ClearShot、Sequencer、扩展、脉冲等高级功能 |
| `ConsoleSkills` | 10 | `ReadConsoleTool` | 5 |  **50%** | 缺少 debug 系统信息 |
| `GameObjectSkills` | 18 | `ManageGameObjectTool` | 12 |  **67%** | 部分 batch 操作 |
| `LightSkills` | 10 | `ManageLightingTool` | 6 |  **60%** | probe 管理 |
| `MaterialSkills` | 21 | `ManageMaterialTool` | 11 |  **52%** | 细粒度属性 batch 设置 |
| `NavMeshSkills` | 10 | `ManageNavMeshTool` | 6 |  **60%** | calculate_path、sample_position |
| `PerceptionSkills` | 18 | `SceneAnalysisTool` | 10 |  **56%** | 场景 Diff、契约验证 |
| `PrefabSkills` | 11 | `ManagePrefabTool` | 6 |  **55%** | create_variant、find_instances |
| `ProBuilderSkills` | 22 | `ManageProBuilderTool` | 10 |  **45%** | 挤出、倒角、UV 投影等高级建模 |
| `ProfilerSkills` | 10 | `ManageProfilerTool` | 5 |  **50%** | 细粒度内存分析 |
| `ProjectSkills` | 11 | `ManageBuildTool` | 6 |  **55%** | 部分项目设置 |
| `ShaderSkills` | 11 | `ManageShaderTool` | 8 |  **73%** | create、delete |
| `SmartSkills` | 10 | `SmartOperationsTool` | 7 |  **70%** | 部分高级操作 |
| `TestSkills` | 11 | `ManageTestTool` | 4 |  **36%** | cancel、create_test_fixture |
| `TimelineSkills` | 12 | `ManageTimelineTool` | 9 |  **75%** | Signal 轨道、高级 clip 管理 |
| `UISkills` | 26 | `ManageUITool` | 9 |  **35%** | layout、align、distribute、anchor 预设 |

###  完全缺失的分类（5 个）

| Unity Skills | Skills数 | 重要性 | 说明 |
|-------------|----------|--------|------|
| `DebugSkills` | 10 | **中** | 系统信息、assembly 信息、defines 管理 |
| `UIToolkitSkills` | 25 | **中** | UI Toolkit (新一代 UI 系统) |
| `ValidationSkills` | 10 | **中** | 场景验证、质量保证 |
| `WorkflowSkills` | 23 | **低** | 工作流自动化 |
| `XRSkills` | 22 | **低-中** | VR/AR 开发（特定项目需要） |

**缺失分类合计**: 90 个 skills (占 Unity Skills 总数的 16%)

###  AgentCore 独有能力（Unity Skills 没有的）

| AgentCore Tool | Actions数 | 说明 |
|---------------|-----------|------|
| `ManageFileTool` | 9 | 文件系统操作（读写、搜索、复制等） |
| `ManageGraphicsTool` | 5 | 渲染设置、质量设置管理 |
| `ManageInputTool` | 5 | 输入轴管理 |
| `ExecuteCodeTool` | 1 | **万能后门** — 执行任意 C# 表达式 |
| `Mem0Tool` | 4 | AI 记忆系统 |
| `LightRAGTool` | 2 | 知识库检索 |
| `BatchExecuteTool` | — | 批量执行多个工具调用 |

**独有能力合计**: 26+ actions

---

## 3. 关键差距分析

### 3.1 最大的深度差距

即使在已覆盖的分类中，以下工具的深度差距最大：

| 工具 | AgentCore | Unity Skills | 差距 | 关键缺失 |
|------|-----------|-------------|------|----------|
| `ManageCinemachineTool` | 10 | 34 | **-71%** | FreeLook、StateDriven、ClearShot、Sequencer、扩展、脉冲 |
| `ManageUITool` | 9 | 26 | **-65%** | layout、align、distribute、anchor 预设、组件添加 |
| `ManageProBuilderTool` | 10 | 22 | **-55%** | 挤出、倒角、UV 投影等高级建模 |
| `ManageMaterialTool` | 11 | 21 | **-48%** | 细粒度属性 batch 设置 |
| `ManageTestTool` | 4 | 11 | **-64%** | cancel、create_test_fixture |

### 3.2 完全缺失但重要的能力

| 缺失 | 影响 | 优先级 |
|------|------|--------|
| **UIToolkit** | 新一代 UI 系统，Unity 官方推荐方向 | P1 |
| **Validation** | 场景验证、质量保证 | P2 |
| **Debug 系统信息** | assembly 信息、defines 管理 | P2 |
| **Workflow** | 工作流自动化 | P3 |
| **XR** | VR/AR 开发 | P3 (按需) |

### 3.3 AgentCore 的结构性优势

尽管 action 数量少于 Unity Skills，AgentCore 有以下结构性优势：

1. **`execute_code` 万能后门**: 任何缺失的功能都可以通过执行 C# 代码临时弥补
2. **`execute_menu_item`**: 可以执行任何 Unity 菜单项，间接访问大量功能
3. **`batch_execute`**: 可以在一次调用中执行多个工具
4. **Cloud 工具**: Mem0 记忆 + LightRAG 知识库，Unity Skills 完全没有
5. **文件系统操作**: `ManageFileTool` 提供完整的文件操作能力
6. **图形管线控制**: `ManageGraphicsTool` 提供渲染/质量设置管理

---

## 4. 覆盖率总结

### 按数量计算

| 指标 | 值 |
|------|-----|
| AgentCore actions | 335 |
| Unity Skills 总数 | 554 |
| **数量覆盖率** | **60.5%** |

### 按分类计算

| 指标 | 值 |
|------|-----|
| Unity Skills 分类数 | 41 |
| AgentCore 已覆盖分类 | 31 (含部分覆盖) |
| AgentCore 独有分类 | 5 |
| 完全缺失分类 | 5 |
| **分类覆盖率** | **76%** (31/41) |
| **含独有分类的总分类覆盖** | **88%** (36/41+5) |

### 按功能等效计算（考虑 execute_code 弥补）

如果考虑 `execute_code` 可以临时弥补缺失功能，**理论覆盖率接近 100%**，但实际可靠性和效率不如专用工具。

---

## 5. 改进建议

### 5.1 P1 — 深度增强（现有工具扩展）

优先增强深度差距最大的工具：

1. **`ManageCinemachineTool`** — 补充 FreeLook、StateDriven 等高级相机模式 (+24 actions)
2. **`ManageUITool`** — 补充 layout、align、distribute 等操作 (+17 actions)
3. **`ManageProBuilderTool`** — 补充挤出、倒角、UV 投影等 (+12 actions)

### 5.2 P2 — 新增缺失分类

4. **新增 `ManageUIToolkitTool`** — UI Toolkit 操作 (~25 actions)
5. **新增 `ValidationTool`** — 场景验证 (~10 actions)
6. **增强 `ReadConsoleTool`** — 补充 Debug 系统信息 (+5 actions)

### 5.3 P3 — 按需补充

7. **新增 `WorkflowTool`** — 工作流自动化 (按需)
8. **新增 `ManageXRTool`** — XR 开发 (按需)

### 5.4 架构建议

- **优先利用 `execute_code`**: 在专用工具开发完成前，可以在 SOUL.md 或 TOOLS.md 中添加常用 Unity API 的使用指南，让 LLM 通过 `execute_code` 临时弥补缺失
- **参考 Unity Skills 的实现**: `_archive/Unity-Skills/` 中的代码可以直接作为参考，但需要适配 AgentCore 的工具架构（`IAgentTool` + `[AgentTool]` + action 分发模式）
- **Perception 能力已基本补齐**: `SceneAnalysisTool` 已覆盖大部分场景分析需求

---

## 6. 结论

**AgentCore 当前对 Unity 的操作能力覆盖了 Unity Skills 约 60% 的功能点（335/554 actions），分类覆盖率约 76%（31/41 分类）。**

相比初版报告声称的 23%，实际覆盖率高出近 3 倍。这是因为初版报告是在项目早期编写的，之后大量工具被新增和扩展。

最大的深度差距在 **Cinemachine**（29%）、**UI**（35%）和 **ProBuilder**（45%）。完全缺失的分类有 5 个（UIToolkit、Validation、Debug、Workflow、XR），但其中 Workflow 和 XR 属于低优先级。

AgentCore 的结构性优势（`execute_code`、`execute_menu_item`、Cloud 工具、文件系统操作）在一定程度上弥补了功能点数量的差距，使得实际可用能力高于 60% 的数字所暗示的水平。
