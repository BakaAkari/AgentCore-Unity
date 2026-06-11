# AgentCore Unity 开发路线图 (Roadmap)

> **版本**: v0.9.6 | **更新日期**: 2026-06-11 | **状态**: 执行中（当前 v0.9.6）
> **定位**: 本文件是 AgentCore 后续开发的**主导方向文档**，优先级高于分散的专项计划。

---

## 0. 如何使用本路线图

### 0.1 使用流程

1. **用户提出需求** → 明确要做什么、优先级、边界
2. **AI 评估需求** → 对照本路线图确定属于哪个 Phase / 任务
3. **文档细化** → 如需新功能，先写/更新 `plans/xxx-feature-plan.md` 详细设计
4. **用户对齐确认** → 确认设计文档（参见 `AGENTS.md` §12.4 编码前对齐确认清单）
5. **代码实现** → 按确认后的设计编码
6. **版本同步** → 更新 `package.json` + `CHANGELOG.md` + 本文件任务状态
7. **用户测试验收** → 按 `AGENTS.md` §12.6 的轮次定义执行

### 0.2 任务状态标记

| 标记 | 含义 |
|------|------|
| `[ ]` | 未开始 |
| `[-]` | 设计中（文档对齐阶段） |
| `[>]` | 开发中 |
| `[~]` | 测试中（用户验收阶段） |
| `[x]` | 已完成 |
| `[!]` | 阻塞/暂停（依赖外部条件或用户决策） |

### 0.3 企业级 Unity 项目适配基准

自 2026-06-02 起，AgentCore 后续功能设计需按 `enterprise-unity-workflow-requirements.md` 中记录的大型商业 Unity 项目场景进行校准。已确认的基础设计规则是：

> **SVN 工作副本根 = AgentCore WorkspaceRoot；Unity 工程目录 = WorkspaceRoot 下的 UnityRoot 子根；地图、模式、工具、资源、插件等目录 = WorkspaceRoot 下的 Scope Root。**

凡涉及文件、资源、索引、记忆、知识库、VCS 操作和工具调用的功能，不得再默认只以标准 `Assets/` 目录或 Unity 项目根为 AgentCore 全局边界。

### 0.4 当前项目快照 (v0.9.6)

| 维度 | 状态 |
|------|------|
| **版本** | 0.9.6 (2026-06-11) |
| **核心架构** | AgentLoop (partial 9 文件) + ChatWindow (partial 9 文件) + ToolAutoDiscovery 重建注册表 + DomainReload 恢复 + Schema 预校验 — 稳定 |
| **Bootstrap 链** | SOUL(+SOUL.ext) → TOOLS → PROJECT(auto) → PROJECT.md(user) → Rules(workspace+project) — 已完整 |
| **Workspace Config** | `manage_workspace_config` 工具 — Agent 可在 Chat 中读写 PROJECT.md / SOUL.ext.md / rules.md（两层） |
| **规则系统** | `RulesLoader` 两层加载（`.agentcore/rules.md` + `AgentCore/rules.md`）；自动注入 System Prompt；Settings UI 支持启用/禁用和文件管理 |
| **UI 框架** | UI Toolkit 动态 Hub 架构；Project Settings 使用 Dashboard + 6 Pages 顶部 Tab 导航 |
| **云端服务** | Mem0 + LightRAG 基础连接 — 可用 |
| **VCS 组件** | Working Copy Status 扁平列表 + 多选右键菜单；Chat 工具 `version_control` 支持 Git/SVN/Perforce（`AGENTCORE_VCS` 控制）；SOUL.md §15 主动调用规则已就绪 |
| **Indexing 组件** | Roslyn 符号索引（JSONL 后端，`#if AGENTCORE_SQLITE` 可选 SQLite）+ `search_code` 工具 15 个 action（`AGENTCORE_INDEXING` 控制）；Full Index 修复已验证（298 files, 6453 symbols）；SOUL.md §14 主动调用规则已就绪 |
| **Agent 主动性** | SOUL.md §13（Workspace Config）+ §14（代码索引）+ §15（VCS）主动调用规则全部就绪 |
| **测试覆盖** | 5 个测试文件，90+ test cases |

### 0.5 已完成的历史 Phase

| Phase | 版本 | 主题 |
|-------|------|------|
| Phase 1 | v0.1.0 | 能对话 — LLM 集成、Bootstrap、Chat UI |
| Phase 2 | v0.2.0 | 能做事 — Tool Calling（unity-mcp 桥接，已废弃） |
| Phase 2.5 | v0.3.0 | 原生工具系统替代 unity-mcp |
| Phase 3 | v0.3.1 | 能记忆 — 会话管理、mem0、LightRAG |
| Phase 4 | v0.3.2~v0.3.7 | 更好用 — UX 打磨、工具补齐 |
| Phase 5 | v0.4.0~v0.9.2 | 夯实基础 — 测试框架、RAG 补齐、架构拆分、上下文压缩、VCS 组件、Settings 重构、Workspace 基础设施、代码索引 Phase 1 |

> 详细历史计划见 `_archive/` 目录。

---

## 1. 战略目标

```
当前 (0.9.x): 代码库理解 → 规则系统 → 智能推荐
中期 (1.0.x):  生态化 → 可扩展 → 产品化
```

| 阶段 | 版本 | 核心目标 | 关键成果 |
|------|------|---------|---------|
| **Phase 6** | 0.9.x ~ 1.0.x | 智能化增强、场景深化 | 依赖图 + 规则系统 + VCS TreeView + 工具推荐 |
| **Phase 7** | 1.0.x+ | 生态与分发 | 文档站 + 示例项目 + 插件市场就绪 |

---

## 2. Phase 6 — 智能化与体验 (v0.9.x ~ 1.0.x)

**主题**: 上下文管理、代码库理解、规则系统 — 基于企业级 Unity 项目适配基准

### 2.1 P0 — 代码库索引深化（最高优先级）

> **前置条件**: WorkspaceRoot / UnityRoot / Scope 基础设施已完成（v0.9.0）；文件级索引 + 符号检索已完成（v0.9.1）。
> **架构决策**: 完全本地化单层架构（SQLite 符号索引），放弃向量数据库，放弃骨架文档。

| # | 任务 | 说明 | 状态 |
|---|------|------|------|
| 6.2.1 | **文件级索引（Layer 1）** | Roslyn 解析 WorkspaceRoot 下 C# 文件，提取类名/命名空间/方法签名；JSONL 本地存储 | [x] v0.9.1 |
| 6.2.2 | **符号检索** | `search_code` 工具 10 个 action，支持 Scope/Root/Role/Branch 过滤 | [x] v0.9.1 |
| 6.2.3 | **SQLite 迁移 + 依赖图构建** | SQLite 替代 JSONL 作为默认后端；SyntaxTree 级 C# 类型依赖提取；`search_code` 新增 5 个 action（get_dependencies、find_usages、get_symbol_context、search_text、get_backend_info）；FTS5 全文搜索；`IndexStoreFactory` 自动降级 | [x] v0.9.3 |
| 6.2.4 | **Full Index Bug 修复** | `CodebaseIndexer` 重建 workspace 时遗漏 `UnityRoot` 字段导致 0 files/0 symbols；修复后验证：298 files, 6453 symbols | [x] v0.9.5 |
| 6.2.5 | **Agent 主动调用规则（SOUL.md §14）** | `search_code` 对话开始协议、强制预查场景、搜索策略、索引新鲜度规则；`TOOLS.md.template` 补充对话开始工作流 | [x] v0.9.5 |

### 2.2 P0 — VCS Panel 体验提升

> **架构决策**: TreeView 重构已放弃（树形结构导致用户需要多次展开折叠，且无法有效体现文件路径）。改为扁平列表按完整相对路径排序，等价于目录结构展开后的自然顺序。
> ~~**关联文档**: [`vcs-treeview-refactor-plan.md`](vcs-treeview-refactor-plan.md)~~ （已废弃）

| # | 任务 | 说明 | 状态 |
|---|------|------|------|
| 6.3.1 | **VCS Panel 扁平列表按路径排序** | Working Copy Status 扁平列表按完整相对路径（`/` 分隔符）排序，等价于目录结构展开后的自然顺序；`SortStatusFiles()` 已实现 | [x] v0.9.3 |
| 6.3.2 | **Agent 主动调用规则（SOUL.md §15）** | `version_control` 主动只读查询、自然语言→action 映射、写操作确认规则、VCS 类型感知（Git/SVN/Perforce） | [x] v0.9.5 |

### 2.3 P1 — 规则系统（已完成）

| # | 任务 | 说明 | 状态 |
|---|------|------|------|
| 6.4.1 | **.agentcore/rules.md 支持** | 优先读取 WorkspaceRoot 下的规则文件（编码规范、架构约定、测试要求），兼容 UnityRoot 局部规则 | [x] v0.9.6 |
| 6.4.2 | **规则自动注入** | 规则内容自动添加到 System Prompt；支持按 WorkspaceRoot、Scope、Root 分层注入 | [x] v0.9.6 |
| 6.4.3 | ~~**SmartToolRecommender**~~ | ~~基于对话上下文和当前任务推荐可用工具；UI 显示推荐理由~~ | [!] 已废弃（见 ADR-9） |
| 6.4.4 | ~~**响应式建议**~~ | ~~LLM 响应末尾附带"下一步建议"（如"是否需要运行测试？"）~~ | [!] 已废弃（见 ADR-9） |

### 2.4 P2 — 体验优化（低优先级）

| # | 任务 | 说明 | 状态 |
|---|------|------|------|
| 6.5.1 | **文件变更 Diff 视图** | 代码修改的 side-by-side 对比视图（简化版） | [ ] |
| 6.5.2 | **主题系统** | 深色/浅色主题切换 | [ ] |
| 6.5.3 | **快捷键自定义** | 用户可自定义聊天窗口快捷键 | [ ] |

### 2.5 Phase 6 里程碑

```
v0.9.3 — 代码库索引 Phase 2（依赖图构建）+ VCS Panel 扁平列表按路径排序 ✅
v0.9.4 — Indexing/VCS Settings UI 修复 + SQLite 兼容性修复 ✅
v0.9.5 — Full Index Bug 修复（验证通过）+ Agent 主动调用规则（SOUL.md §14/§15）✅
v0.9.6 — 规则系统（.agentcore/rules.md + 分层注入）✅
v0.9.7 — 完整功能测试验收（Round 1~4）
v1.0.0 — Phase 6 完成里程碑（体验优化 + 稳定性验收）
```

---

## 3. Phase 7 — 生态与产品化 (v1.0.x+)

**主题**: 从开发工具转变为可分发、可扩展的产品

| # | 任务 | 说明 | 优先级 | 状态 |
|---|------|------|--------|------|
| 7.1.1 | **UPM 发布流程** | 自动化打包、版本标签、发布检查清单 | P0 | [ ] |
| 7.1.2 | **文档网站** | 使用 Docusaurus/VitePress 搭建静态文档站（托管于 GitHub Pages） | P1 | [ ] |
| 7.1.3 | **示例项目** | 完整示例：3D 平台跳跃游戏从零开发（演示 AgentCore 全部能力） | P1 | [ ] |
| 7.1.4 | **Plugin/Extension 系统** | 允许用户自定义工具脚本并动态加载（Editor 级别热插拔） | P2 | [ ] |
| 7.1.5 | **多 LLM 后端** | 支持 Claude、Gemini、本地 Ollama 等（统一接口） | P2 | [ ] |
| 7.1.6 | **Unity Asset Store 提交** | 整理元数据、截图、描述文案，完成 Asset Store 提交 | P2 | [ ] |

---

## 4. ADR (Architecture Decision Records)

### ADR-1: 不实现 Markdown 渲染

**状态**: `已决策 — 放弃` | **日期**: 2026-05-09

- **决策**: 不在 UI 中实现 Markdown 渲染（斜体/粗体/链接样式化）
- **原因**: UI Toolkit 富文本支持有限，自定义渲染成本高；当前 `SmartMessageBuilder` 已处理代码块和列表，基本可读
- **替代方案**: 如果未来需要，考虑引入第三方 UI Toolkit Markdown 渲染器

### ADR-2: XR 工具暂不实现

**状态**: `已决策 — 暂时冻结` | **日期**: 2026-05-09

- **决策**: XR 工具（ManageXRTool）冻结，仅用户明确需要时解冻
- **原因**: Unity XR 模块 API 差异大，当前用户基数以传统 PC/Console 开发为主

### ADR-3: 文档状态必须以代码事实校准

**状态**: `已决策 — 代码事实优先` | **日期**: 2026-05-12

- **决策**: ROADMAP 和专项计划中的工具状态必须以实际源码为准
- **执行规则**: 规划工具开发前先读取对应工具源码；旧计划文档统一归档不删除；ROADMAP 是方向层唯一入口

### ADR-4: AgentLoop.cs 拆分策略

**状态**: `已决策 — 采用部分类拆分` | **日期**: 2026-05-09

- **决策**: 使用 C# `partial class` 拆分 `AgentLoop.cs`（共 9 个 partial 文件）
- **原因**: 保持所有实例字段和方法的访问权限不变，减少 Domain Reload 恢复逻辑的耦合风险

### ADR-5: 拒绝模式系统 — AgentCore 是自主智能体

**状态**: `已决策 — 废弃模式系统` | **日期**: 2026-05-18

- **决策**: 废弃"模式系统"（Architect Mode / Review Mode / 模式切换 UI）
- **核心理念**: AgentCore 是智能体（Agent），不是 IDE；应根据对话上下文自动识别用户需求，自主选择合适的行为模式
- **替代方案**: 情境感知增强 + 工具推荐系统 + 响应式建议

### ADR-6: Settings Provider Shell — 设置页禁止回到 God Object

**状态**: `已决策 — 采用 Settings shell + section registry` | **日期**: 2026-05-22

- **决策**: `AgentCoreSettingsProvider` 只承担 Settings shell 职责，业务设置必须迁移到独立 `IAgentCoreSettingsSection`
- **执行规则**: 新增设置项必须归属到明确 section；foldout/异步状态放入 `AgentCoreSettingsState`；连接型设置复用统一模式

### ADR-7: 代码索引采用完全本地化单层架构

**状态**: `已决策 — 单层 SQLite 架构` | **日期**: 2026-06-03

- **决策**: 放弃向量数据库，放弃骨架文档（workspace-skeleton.md），只保留 SQLite 符号索引（Layer 1）
- **原因**: `search_code` 工具按需检索比静态骨架文档更精准、更省 token；骨架文档会随代码变化快速过时；向量数据库引入额外依赖和运维成本
- **影响**: Bootstrap 链简化为 `SOUL → TOOLS → PROJECT(auto) → PROJECT.md(user)`；`BootstrapContext.Skeleton` 属性已删除

### ADR-9: 废弃智能推荐系统（SmartToolRecommender + 响应式建议）

**状态**: `已决策 — 废弃` | **日期**: 2026-06-11

- **决策**: 废弃 6.4.3 SmartToolRecommender 和 6.4.4 响应式建议两个功能
- **核心理由**: Agent 对项目的理解、设计方向和当前开发阶段，永远不如用户明确。基于上下文的主动建议在实践中会产生大量"钻牛角尖"式的无止尽优化建议，浪费 token，干扰用户的实际工作节奏
- **替代方案**: 无。用户主导对话方向，Agent 专注执行用户明确提出的任务
- **影响**: 6.4.3 和 6.4.4 标记为 `[!] 已废弃`；v0.9.7 里程碑改为完整功能测试验收

### ADR-8: Agent 主动调用规则内嵌于 SOUL.md

**状态**: `已决策 — 采用 SOUL.md 内嵌规则` | **日期**: 2026-06-10

- **决策**: `search_code` 和 `version_control` 的主动调用规则直接写入 `SOUL.md`（§14、§15），而不是通过代码逻辑或 Settings 配置控制
- **原因**: SOUL.md 是 LLM 行为的唯一权威来源；规则写在 SOUL.md 中可以被 LLM 直接理解和执行，无需额外的代码分发机制；与 §11（记忆）、§12（知识库）、§13（Workspace Config）的主动调用规则保持一致的模式
- **影响**:
  - `SOUL.md §2` 补充"索引优先"原则
  - `SOUL.md §4` 反幻觉表新增 `search_code` 和 `version_control` 正确名称
  - `SOUL.md §14` 新增代码索引主动调用规则（对话开始协议 + 6 个强制预查场景 + 搜索策略 + 索引新鲜度）
  - `SOUL.md §15` 新增 VCS 主动调用规则（主动只读查询 + 自然语言映射 + 写操作确认 + VCS 类型感知）
  - `TOOLS.md.template` `search_code` 章节补充对话开始工作流

---

## 5. 风险评估

| 风险 | 可能性 | 影响 | 缓解措施 |
|------|--------|------|---------|
| 依赖图构建导致索引时间过长 | 中 | 中 | 异步后台执行 + 增量更新 + 可取消 |
| VCS TreeView 在大型 WorkspaceRoot 下性能问题 | 中 | 中 | 虚拟化列表 + 懒加载子节点 |
| SmartToolRecommender 推荐不准确 | 中 | 中 | 从规则匹配开始，逐步引入 LLM 辅助判断；提供"忽略推荐"按钮 |
| 示例项目维护成本过高 | 低 | 低 | 示例项目独立仓库，AgentCore 作为 UPM 依赖引入 |

---

## 6. 文档状态索引

| 文档 | 状态 | 位置 |
|------|------|------|
| [`README.md`](README.md) | 文档导航 | `plans/` 顶层 |
| [`ROADMAP.md`](ROADMAP.md) | **主导方向文档** | `plans/` 顶层 |
| [`enterprise-unity-workflow-requirements.md`](enterprise-unity-workflow-requirements.md) | 企业级 Unity 项目适配需求基准，后续任务上游依据 | `plans/` 顶层 |
| [`vcs-treeview-refactor-plan.md`](_archive/features/vcs-treeview-refactor-plan.md) | ~~已废弃~~ — TreeView 方案废弃，改为扁平列表（v0.9.3 完成），已归档 | `_archive/features/` |
| [`codebase-indexing-phase2-plan.md`](_archive/features/codebase-indexing-phase2-plan.md) | 已完成（v0.9.3）— SQLite 迁移 + 依赖图 + FTS5，已归档 | `_archive/features/` |
| **其他已完成计划** | 历史归档 | [`_archive/features/`](_archive/features/) |
| **重构计划** | 历史归档 | [`_archive/refactoring/`](_archive/refactoring/) |
| **Phase 计划** | 历史归档 | [`_archive/phases/`](_archive/phases/) |
| **技术分析** | 历史归档 | [`_archive/analysis/`](_archive/analysis/) |

**归档文档使用规则**：
- 归档文档仅作历史参考，不作为当前开发依据
- 当前功能状态以 `Editor/` 实际源码为准
- 新功能计划在 `plans/` 顶层创建，完成后移至 `_archive/`

---

## 7. 下一步行动建议

| 推荐度 | 任务 | 原因 |
|--------|------|------|
| 🔥 | **v0.9.7 完整功能测试验收** | v0.9.6 规则系统完成，整体功能已趋于完整，需要系统性 Round 1~4 测试验收，确保核心链路稳定 |
| 💡 | **6.5.1 文件变更 Diff 视图** | 测试通过后，VCS 工具已完善，Diff 视图可提升代码审查体验 |
| 💡 | **6.5.2/6.5.3 主题 + 快捷键** | 体验优化，测试通过后按需推进 |

---

## 8. 维护规则

1. **任务状态同步**: 完成任务将 `[ ]` 改为 `[x]`，开发中改为 `[>]`
2. **版本号绑定**: 每次版本发布后同步更新里程碑状态
3. **新增 ADR**: 如有架构决策变更，在 §4 新增 ADR 条目
4. **季度审视**: 每完成一个 Phase 后重新审视路线图，调整优先级

---

> **本文档由 AI 协助制定，经用户 review 确认后生效。**
> 任何修改请遵循 `AGENTS.md` 第 12 章的开发流程规范。
