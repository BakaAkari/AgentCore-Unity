# AgentCore Unity 开发路线图 (Roadmap)

> **版本**: v0.4.8 | **制定日期**: 2026-05-09 | **状态**: 工具增强已完成 (v0.4.8)
> **定位**: 本文件是 AgentCore 后续开发的**主导方向文档**，优先级高于分散的专项计划。
>
> **与现有计划的关系**:
> - `rag-feature-completion-plan.md` → 纳入本路线图的 **Phase 5.2 (RAG 补齐)**
> - `agentcore-workspace-hub-execution-plan.md` → Hub 骨架部分已实施，剩余 MemoryPanel 等内容纳入 **Phase 6.2**
> - **代码事实优先**: 当本文件与实际源码不一致时，以 `Editor/` 下当前代码为准，并立即修正文档状态。

---

## 0. 如何使用本路线图

### 0.1 本路线图的使用流程

本文件是**方向层**文档（参见 `AGENTS.md` §12.3 文档层级）。使用流程如下：

1. **用户提出需求** → 明确要做什么、优先级、边界
2. **AI 评估需求** → 对照本路线图确定属于哪个 Phase / 任务
3. **文档细化** → 如需新功能，先写/更新 `plans/xxx-feature-plan.md` 详细设计
4. **用户对齐确认** → 确认设计文档（参见 `AGENTS.md` §12.4 编码前对齐确认清单）
5. **代码实现** → 按确认后的设计编码
6. **版本同步** → 更新 `package.json` + `CHANGELOG.md` + 本文件任务状态
7. **用户测试验收** → 按 `AGENTS.md` §12.6 的轮次定义执行

### 0.2 任务状态标记

本文件中所有任务使用以下状态标记：

| 标记 | 含义 |
|------|------|
| `[ ]` | 未开始 |
| `[-]` | 设计中（文档对齐阶段） |
| `[>]` | 开发中 |
| `[~]` | 测试中（用户验收阶段） |
| `[x]` | 已完成 |
| `[!]` | 阻塞/暂停（依赖外部条件或用户决策） |

### 0.3 当前项目快照 (v0.4.8)

| 维度 | 状态 |
|------|------|
| **版本** | 0.4.8 (2026-05-13) |
| **工具数量** | 44 个工具，340+ actions；Native 工具以 `Editor/Tools/Native/` 实际源码为准 |
| **核心架构** | AgentLoop (partial 9 文件) + ChatWindow (partial 9 文件) + ToolAutoDiscovery + DomainReload 恢复 + Schema 预校验 — 稳定 |
| **UI 框架** | UI Toolkit Hub 架构 (Chat/Knowledge/Memory) — MemoryPanel UI 已接入 |
| **云端服务** | Mem0 + LightRAG 基础连接 — 可用 |
| **测试覆盖** | 5 个测试文件，90+ test cases (ToolResponse, JsonHelper, TokenCounter, ToolHelpers, SchemaValidation) |
| **归档参考** | `_archive/Unity-Skills/` 含 554 个 skills 可供迁移 |

### 0.4 已完成的 Phase 体系（历史归档）

- [x] Phase 1: 能对话 (v0.1.0)
- [x] Phase 2: 能做事 + 原生工具骨架 (v0.2.0)
- [x] Phase 2.5: 原生工具系统替代 unity-mcp (v0.3.0)
- [x] Phase 3: 能记忆 + 会话管理 (v0.3.1)
- [x] Phase 4: 更好用 — UX 打磨 + 工具补齐 (v0.3.2 ~ v0.3.7)

---

## 1. 战略目标

```
短期 (0.4.x): 稳基础 → 补能力 → 强 RAG
中期 (0.5.x): 智能化 → 深场景 → 提体验
长期 (0.6.x+): 生态化 → 可扩展 → 产品化
```

| 阶段 | 版本 | 核心目标 | 关键成果 |
|------|------|---------|---------|
| **Phase 5** | 0.4.x | 夯实基础、补齐 RAG、清理文档债 | 有测试 + RAG 完整 + 代码质量提升 + 文档与代码一致 |
| **Phase 6** | 0.5.x | 智能化增强、场景深化 | 工具推荐 + MemoryPanel + 代码审查 |
| **Phase 7** | 0.6.x+ | 生态与分发 | 文档站 + 示例项目 + 插件市场就绪 |

---

## 2. Phase 5 — 夯实基础 (v0.4.x)

**主题**: 技术债清偿、核心能力补齐、质量基线建立

### 2.1 P0 — 测试与架构债务

> **目标**: 建立可维护的代码基线，防止核心文件持续膨胀

| # | 任务 | 说明 | 预估工作量 | 状态 |
|---|------|------|-----------|------|
| 5.1.1 | **创建 `AgentCore.Tests.Editor`** | 新增测试 asmdef，引入 Unity Test Framework | 中 | [x] |
| 5.1.2 | **测试 `ToolHelpers`** | 覆盖参数解析、Vector/Color 解析、GameObject 查找 | 低 | [x] |
| 5.1.3 | **测试 `ToolResponse` / `ToolResult`** | 覆盖 Ok/OkWithData/Fail/ToToolResult | 低 | [x] |
| 5.1.4 | **测试 `JsonHelper`** | 覆盖序列化/反序列化/安全解析/安全取值 | 低 | [x] |
| 5.1.5 | **测试 `TokenCounter`** | 验证中英文 token 估算准确性 | 低 | [x] |
| 5.1.6 | **拆分 `AgentLoop.cs`** | 将 `RunToolCallLoopAsync` 提取到 `AgentLoop.Runner.cs`，将记忆召回提取到 `AgentLoop.Memory.cs`，共拆为 9 个 partial 文件 | 中 | [x] |
| 5.1.7 | **拆分 `ChatWindow.cs`** | 将 ChatWindow 拆为 9 个 partial 文件 (主文件 + Input/Events/Messages/DomainReload/Restore/Hub/Sessions/Tools/UIHelpers)，从 2135 行降至主文件 ~290 行 | 中 | [x] |
| 5.1.8 | **JSON Schema 参数校验** | 在 `ToolCallDispatcher` 中基于 `ParametersSchema` 预校验 LLM 传入的参数，提前返回参数错误 | 中 | [x] |

### 2.2 P1 — RAG 功能补齐

> **目标**: LightRAG 从"能连接"到"完整可用"
> **关联文档**: `rag-feature-completion-plan.md` (修订版 v2)

| # | 任务 | 说明 | 关联计划 | 状态 |
|---|------|------|---------|------|
| 5.2.1 | **LightRAG 文档列表** | `LightRAGClient.GetDocumentsAsync()` + `KnowledgeBasePanel` 文档列表 UI + `manage_knowledge.list_documents` action | RAG-Doc-1 | [x] |
| 5.2.2 | **LightRAG 文档删除** | `LightRAGClient.DeleteDocumentAsync()` + UI 删除按钮 + `manage_knowledge.delete_document` action | RAG-Doc-1 | [x] |
| 5.2.3 | **track_id 轮询进度** | `LightRAGClient.TrackStatusAsync()` + `IndexFileAsync` 返回 `LightRAGIndexResult` + UI 进度轮询 | RAG-Doc-2 | [x] |
| 5.2.4 | **LightRAG 批量索引** | `index_folder` / `index_project_docs` action（扫描 README.md、docs/、plans/、Assets/Docs/） | Phase RAG-3 | [x] |
| 5.2.5 | **查询体验强化** | `query` 支持 `top_k` 参数；查询结果展示来源文档名称；更新 SOUL.md 明确知识库使用场景 | Phase RAG-4 | [x] |

### 2.3 P2 — 能力补齐（以代码事实校准）

> **目标**: 不盲目按旧计划补工具；先以实际源码审计已有 actions，再只补真正缺失且通用的能力。
> **参考源**: `_archive/Unity-Skills/SkillsForUnity/Editor/Skills/`
> **审计日期**: 2026-05-12；结论以 `Editor/Tools/Native/` 当前实现为准。

| # | 任务 | 说明 | 来源参考 | 状态 |
|---|------|------|---------|------|
| 5.3.1 | **ManageXRTool** | 项目当前不涉及 VR/AR/MR；XR 工具冻结，仅用户明确需要时解冻 | `XRSkills.cs` (22 skills) | [!] |
| 5.3.2 | **ManageTestTool 增强** | 当前已有 `list_tests`、`run_tests`、`get_results`、`create_test`；已补 `cancel`、`create_test_fixture` | `TestSkills.cs` | [x] |
| 5.3.3 | **ManageCinemachineTool 深度** | 已有 20 actions，包含 FreeLook、StateDriven、ClearShot、Sequencer、DollyTrack、Impulse、BlendList 等能力 | `CinemachineSkills.cs` | [x] |
| 5.3.4 | **ManageUIToolkitTool 增强** | 已有 20 actions，包含 UXML/USS 创建编辑、UIDocument、PanelSettings、binding、EditorWindow 模板、自定义元素模板等能力 | `UIToolkitSkills.cs` | [x] |
| 5.3.5 | **ManageMaterialTool 增强** | 当前已有 11 actions；已补 `batch_set_properties`、`list_materials`、`get_shader_info`（含 Shader Graph 识别） | `MaterialSkills.cs` | [x] |
| 5.3.6 | **文档状态校准** | 审计 `plans/` 中旧计划，标注已完成、已并入 ROADMAP、冻结或仍有效，避免过时计划误导开发 | 当前源码 + plans | [x] |

### 2.4 Phase 5 里程碑

```
v0.4.0 — 测试框架 + AgentLoop/ChatWindow 拆分
v0.4.1 — LightRAG 文档列表/删除/进度轮询
v0.4.2 — LightRAG 批量索引 + 查询强化
v0.4.3 — 稳定性优先：测试框架与基础测试
v0.4.4 — JSON Schema 参数预校验
v0.4.5 — AgentLoop partial 拆分
v0.4.6 — ChatWindow partial 拆分
v0.4.7 — 文档状态校准（plans/ 全量审计 + ROADMAP 修正 + ADR-3）
```


---

## 3. Phase 6 — 智能化与体验 (v0.5.x)

**主题**: AI 驱动的智能化功能、用户工作流体验优化

### 3.1 P0 — 智能增强

| # | 任务 | 说明 | 优先级 | 状态 |
|---|------|------|--------|------|
| 6.1.1 | **SmartToolRecommender** | 基于对话上下文自动推荐可用工具 | P0 | [ ] |
| 6.1.2 | **代码审查模式** | `review_code` action：分析脚本质量、性能、风格问题 | P0 | [ ] |
| 6.1.3 | **响应式建议** | LLM 响应末尾附带"下一步建议"（如"是否需要运行测试？"） | P0 | [ ] |

### 3.2 P1 — 场景深化

| # | 任务 | 说明 | 优先级 | 状态 |
|---|------|------|--------|------|
| 6.2.1 | **MemoryPanel UI** | Hub 架构 Memory 模块的可视化面板：mem0 状态、用户创建、手动添加、搜索、列表刷新、删除 | P1 | [x] |
| 6.2.2 | **高级记忆策略** | 上下文压缩（LongContext vs MemoryRecall 的智能选择）、记忆重要性评分 | P1 | [ ] |
| 6.2.3 | **文件变更 Diff 视图** | 代码修改的 side-by-side 对比视图（简化版） | P1 | [ ] |
| 6.2.4 | **主题系统** | 深色/浅色主题切换（不追求极致美观，追求可用性） | P1 | [ ] |

### 3.3 P2 — 集成扩展

| # | 任务 | 说明 | 优先级 | 状态 |
|---|------|------|--------|------|
| 6.3.1 | **Google Sheets 集成** | Spreadsheet 读写工具（用于批量数据配置、本地化表管理） | P2 | [ ] |
| 6.3.2 | **AI 驱动的测试生成** | 基于场景描述自动生成 PlayMode/EditMode 测试 | P2 | [ ] |
| 6.3.3 | **快捷键自定义** | 用户可自定义聊天窗口快捷键 | P2 | [ ] |

### 3.4 Phase 6 里程碑

```
v0.5.0 — SmartToolRecommender + 代码审查模式 + 响应式建议
v0.5.1 — MemoryPanel UI + 高级记忆策略
v0.5.2 — 文件变更 Diff 视图 + 主题系统
v0.5.3 — Google Sheets 集成
v0.5.4 — AI 测试生成 + 快捷键自定义
```

---

## 4. Phase 7 — 生态与产品化 (v0.6.x+)

**主题**: 从开发工具转变为可分发、可扩展的产品

### 4.1 任务清单

| # | 任务 | 说明 | 优先级 | 状态 |
|---|------|------|--------|------|
| 7.1.1 | **UPM 发布流程** | 自动化打包、版本标签、发布检查清单 | P0 | [ ] |
| 7.1.2 | **文档网站** | 使用 Docusaurus/VitePress 搭建静态文档站（托管于 GitHub Pages） | P1 | [ ] |
| 7.1.3 | **示例项目** | 完整示例：3D 平台跳跃游戏从零开发（演示 AgentCore 全部能力） | P1 | [ ] |
| 7.1.4 | **Plugin/Extension 系统** | 允许用户自定义工具脚本并动态加载（Editor 级别热插拔） | P2 | [ ] |
| 7.1.5 | **多 LLM 后端** | 支持 Claude、Gemini、本地 Ollama 等（统一接口） | P2 | [ ] |
| 7.1.6 | **Unity Asset Store 提交** | 整理元数据、截图、描述文案，完成 Asset Store 提交 | P2 | [ ] |

### 4.2 Phase 7 里程碑

```
v0.6.0 — UPM 发布流程自动化
v0.6.1 — 文档网站上线
v0.6.2 — 示例项目 v1 发布
v0.6.3 — Plugin 系统 + 多 LLM 后端
v0.6.4 — Asset Store 提交
```

---

## 5. ADR (Architecture Decision Records)

> **规则**: 每个关键决策在此记录，说明决策内容、原因、替代方案（如果放弃）。

### ADR-1: 不实现 Markdown 渲染

**状态**: `已决策 — 放弃`
**日期**: 2026-05-09

- **决策**: 不在 UI 中实现 Markdown 渲染（斜体/粗体/链接样式化）
- **原因**: 
  1. 当前 `SmartMessageBuilder` 已处理代码块和列表，基本可读
  2. UI Toolkit 的富文本支持有限，自定义渲染成本高
  3. 用户当前接受纯文本 + 代码块高亮的体验
  4. 可降级为 CodeReview 模式下的 diff 高亮（Phase 6）
- **替代方案**: 如果未来需要，考虑引入第三方 UI Toolkit Markdown 渲染器

### ADR-2: XR 工具暂不实现

**状态**: `已决策 — 暂时冻结`
**日期**: 2026-05-09

- **决策**: XR 工具（ManageXRTool）不纳入 Phase 5 的 P0/P1 任务，列为 P2
- **原因**:
  1. `_archive/Unity-Skills/` 中的 XR 能力需要大量适配（Unity XR 模块 API 差异大）
  2. 当前用户基数可能以传统 PC/Console 开发为主
  3. 可以作为 Phase 5.3.1 或后续需求驱动实现
- **触发条件**: 用户明确需要 XR 开发支持时，解冻该任务

### ADR-3: 文档状态必须以代码事实校准

**状态**: `已决策 — 代码事实优先`
**日期**: 2026-05-12

- **决策**: ROADMAP 和专项计划中的工具状态必须以实际源码为准；当计划写着“未开发”但源码已经实现时，立即更新文档或标注为历史归档。
- **原因**:
  1. 已发现 `ManageCinemachineTool` 和 `ManageUIToolkitTool` 实际已具备深度能力，但 ROADMAP 仍将其列为未开发。
  2. 过时计划会导致重复开发、错误优先级和错误版本规划。
  3. 工具系统当前已有 44 个工具，必须避免依赖记忆或旧文档判断能力边界。
- **执行规则**:
  1. 规划工具开发前，先读取对应工具源码的 `ParametersSchema` 和 `switch action` 分发。
  2. 旧计划文档不删除，统一标注“已实现/已并入 ROADMAP/历史参考/冻结”。
  3. ROADMAP 是方向层唯一入口；专项计划只作为设计或历史参考。

### ADR-4: AgentLoop.cs 拆分策略

**状态**: `已决策 — 采用部分类拆分`
**日期**: 2026-05-09

- **决策**: 使用 C# `partial class` 拆分 `AgentLoop.cs`，而非提取到独立类
- **原因**:
  1. `partial class` 保持所有实例字段和方法的访问权限不变
  2. 减少 Domain Reload 恢复逻辑的耦合风险
  3. 不需要修改 `ChatWindow` 中引用 `AgentLoop` 的所有代码
- **文件规划**:
  - `AgentLoop.cs` — 核心状态机 + 公共接口
  - `AgentLoop.Runner.cs` — `RunToolCallLoopAsync` + `ExecuteToolCallsAsync`
  - `AgentLoop.MemoryRecall.cs` — 记忆召回 + 上下文构建
  - `AgentLoop.LLMCall.cs` — `CallLLMStreamAsync` + 流解析

---


## 6. 风险评估与缓解

| 风险 | 可能性 | 影响 | 缓解措施 |
|------|--------|------|---------|
| AgentLoop 拆分引入 Domain Reload 回归 | 中 | 高 | 拆分后必须进行 Domain Reload 中断恢复测试（`AGENTS.md` §8.3） |
| LightRAG 批量索引导致编辑器卡顿 | 中 | 中 | 索引过程异步执行 + UI 显示进度条 + 可取消 |
| 测试框架引入后增加构建时间 | 低 | 低 | 测试 asmdef 设置 `includePlatforms: ["Editor"]`，不影响构建 |
| SmartToolRecommender 推荐不准确 | 中 | 中 | 从规则匹配开始，逐步引入 LLM 辅助判断；提供"忽略推荐"按钮 |
| 示例项目维护成本过高 | 低 | 低 | 示例项目独立仓库，AgentCore 作为 UPM 依赖引入 |

---

## 7. 维护规则

### 7.1 与代码同步更新

本文件作为方向层文档，每次代码变更后必须同步更新以下内容：

1. **任务状态**: 完成任务将 `[ ]` 改为 `[x]`，开发中将 `[ ]` 改为 `[>`]
2. **版本号**: Phase 5 完成后整体更新为 v0.4.x，以此类推
3. **新增 ADR**: 如有架构决策变更，在此新增 ADR 条目
4. **历史归档**: 已完成的 Phase 内容可移至文档底部"历史归档"区，保持当前页面聚焦

### 7.2 版本号与里程碑绑定

```
v0.3.7 → Phase 4 终点，Phase 5 起点
v0.4.0 → Phase 5.1 (测试+架构)
v0.4.1 → Phase 5.2 (RAG 补齐)
v0.5.0 → Phase 6.1 (智能增强)
v0.6.0 → Phase 7.1 (UPM 发布)
```

### 7.3 季度审视

每完成一个 Phase 后，重新审视本路线图：
- 哪些任务比预期更快？可以合并后续 Phase 的任务
- 哪些任务比预期更慢？分析原因，调整后续计划
- 用户反馈中出现了哪些未预见的需求？评估是否纳入当前 Phase

### 7.4 文档状态索引

| 文档 | 当前状态 | 后续使用规则 |
|------|----------|--------------|
| `ROADMAP.md` | 主导方向文档 | 唯一规划入口，必须与代码状态同步 |
| `ARCHITECTURE.md` | 架构参考 | 保留，但阶段状态以 ROADMAP 为准 |
| `phase1-plan.md` | 历史归档 | Phase 1 已完成，仅作历史参考 |
| `phase2-plan.md` | 历史归档 | Phase 2 已完成，仅作历史参考 |
| `phase2.5-native-tools-plan.md` | 历史归档 | 原生工具迁移已完成，工具清单以 `Editor/Tools/Native/` 为准 |
| `phase3-plan.md` | 历史归档 | Memory/Session/RAG 基础已完成，仅作设计参考 |
| `phase4-plan.md` | 历史归档 | Phase 4 已完成 |
| `stability-first-plan.md` | 已落地/被拆分执行 | v0.4.3~v0.4.6 已覆盖主要内容 |
| `json-schema-validation-plan.md` | 已完成 | v0.4.4 已实现，仅作设计参考 |
| `agentloop-split-plan.md` | 已完成 | v0.4.5 已实现 |
| `chatwindow-split-plan.md` | 已完成 | v0.4.6 已实现 |
| `rag-feature-completion-plan.md` | 已完成 | Phase 5.2 已完成，后续 RAG 新需求另开计划 |
| `agentcore-workspace-hub-execution-plan.md` | 部分已落地 | Hub/Knowledge/Memory 已接入，剩余事项以 ROADMAP 为准 |
| `memory-panel-ui-plan.md` | 已完成 | MemoryPanel UI 已接入，仅作设计参考 |
| `domain-reload-resilience.md` | 已落地 | DomainReloadState/恢复链路已实现，修改核心恢复逻辑前参考 |
| `file-change-tracking-plan.md` | 部分已落地/仍有效 | 文件变更追踪已有基础，Diff 视图仍属 Phase 6.2.3 |
| `mem0-settings-optimization.md` | 候选优化 | 需要再次对照当前 Settings/Client 代码后决定是否执行 |
| `mem0-vs-openmemory-analysis.md` | 运维参考 | 仅用于部署选型和故障分析 |

---

## 8. 下一步行动建议

当前（v0.4.6）最推荐的三个切入点：

| 推荐度 | 任务 | 原因 |
|--------|------|------|
| ⭐⭐⭐ | **5.3.6 文档状态校准** | 当前首要风险是旧计划误导开发，必须先统一文档事实源 |
| ⭐⭐ | **5.3.2 ManageTestTool 小增强** | 范围小且通用：补 `cancel` 与 `create_test_fixture` |
| ⭐⭐ | **5.3.5 ManageMaterialTool 小增强** | 范围小且通用：补批量属性设置、材质列表、Shader Graph 识别说明 |

---

> **本文档由 AI 协助制定，经用户 review 确认后生效。**
> 任何修改请遵循 `AGENTS.md` 第 12 章的开发流程规范。

