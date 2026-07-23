# Adversarial Coverage Audit：发布前的系统性问题主动排查

用户会周期性追问："这个系统还缺哪些模块的覆盖？"、"已有覆盖范围会不会再触发之前反馈的问题？"、"什么时候升 minor 版本？"——这不是单点 bug 追查，是**架构级的对抗式校验**。这类问题不能靠"我感觉应该没问题"来答，必须走**三步矩阵校验流程**，用数据说话。

## 触发场景

- 用户在多轮修复后追问"这个问题类别在别处会不会重演"
- 用户问"从一致性原理再分析一次系统缺哪些能力覆盖"
- 用户问"该升 minor 版本了吗 / 什么时候升 v1.8.0"
- 你自己发现某类根因反复出现，怀疑是系统性缺陷而非单点问题

这类问题的共同特征：**用户已经不满足于"这次修好了"，要求你证明"这个类别的问题不会再出现"**。

## 三步校验流程（每步都要有可 grep 的实证，不允许凭直觉答）

### Step 1 — 覆盖矩阵枚举（工具 × 领域）

**目标**：把系统的可能覆盖面**量化**成矩阵，找出洞。

方法（AgentCore 实例）：
```bash
# 枚举所有原生工具（Attribute 反射源）
grep -rn --include='*.cs' -E "AgentTool\(" Editor/Tools/Native/
# 或用 execute_code 走 os.walk + re.finditer(r'AgentTool\("([^"]+)"', src)
```

对齐到 Unity Editor 常用模块（35 个左右）：GameObject/Component 域、场景/资源域、渲染/图形域、音频/物理/动画域、编辑器 UI/流程、项目/构建域、输入/测试/优化、3D 建模/地形、NavMesh/AI/事件、校验/元/工作流、VCS/工作区。

**输出格式**：三列表——(a) Unity 模块名 (b) 是否有专门工具 (c) 是否能被通用工具（如 `execute_code`）兜底。三列都是 ❌ 才是真缺口，(c)=✅ 的算"覆盖"。

**AgentCore 实证**（v1.7.28 时点）：47 个 [AgentTool] × ~35 个模块 → 覆盖 ~85%（30 个专用+兜底），余 5 个（Undo/Post-Processing/RenderFeature/ProjectSettings 其他项/Mesh 编辑）全部能被 `execute_code:run` 兜底。**结论：覆盖率不是问题**。

### Step 2 — 历史根因×现存工具的对抗式复查

**目标**：把用户过去反馈过的每一类错误的根因**提取出来**，再问"这个根因在别处会不会重演"。

方法：
1. 列出历史用户报告的错误链（本次是"创建 .cs 走菜单项"的 5 步失败链）
2. 对每一步错误提取**根因分类**（不是错误字符串，是根因的抽象）：
   - R1 = "创建资源后立即用" 时序陷阱
   - R2 = action 型工具的 default case 是否列出可用 action
   - R3 = 修改对象忘记 Undo.RecordObject
   - R4 = 反射类型未在 ReferenceAssembly 里
   - R5 = Play Mode / Editor 状态耦合
3. 每个 R 都要有**可 grep 验证的判据**（不是"我觉得没问题"）
4. 分类结果：**GOOD** / **WARN_ONLY_UNKNOWN** / **SILENT** / **UNCLEAR** / **NO_DEFAULT**——只有 GOOD 算通过

**R2 实证**（用 execute_code 精准扫描）：
```python
# 找每个工具的 switch(action) 块，找 default: 分支
# 判 default 分支后面的字符串是否含 "Valid actions" / "Available actions" / "Expected one of"
# 结果：44/44 全绿 → R2 已由 v1.7.14~v1.7.25 五连弹扫干净
```

**R1/R3 实证**（grep SOUL.md 是否已有引导）：
```bash
grep -cE "AssetDatabase\.Refresh|delayCall|Undo\.RecordObject" SOUL.md
# 若为 0 → 引导层缺失，用户在别处会重演旧错误
```

**AgentCore 实证**：R2 已全绿；R1 + R3 完全没提 → 唯一真实的系统性缺陷两处，都在 SOUL 引导层，不在工具层。v1.7.28 修复。

### Step 3 — Minor Version Bump 时机判断

见 `release-and-bulk-migration.md` 里的 "Minor Version Bump 时机判断" 章节。**不重复展开**，但对抗式校验的最后一步必须触及这个问题——用户几乎每次追问系统性问题时都会跟着问"该升 minor 了吗"。

## 关键输出格式（用户偏好）

用户对这类对抗式校验的**输出偏好**已多次验证：

1. **表格 + 明确的 ✅/❌ 判定**，不用长段自然语言堆砌
2. **每个断言都有可 grep 的实证**（"47 工具"要能被反查到，"44/44 全绿"要有扫描脚本）
3. **区分"覆盖率不是问题"和"引导层是问题"**——不要把工具缺失和引导缺失混为一谈
4. **给"未做的事（留白）"清单**，说明哪些低优先级项被主动跳过、为什么（用户明确排斥"堆砌工具"的方向）
5. **v1.8.0 时机建议要给具体锚点**（"Phase 8 MCP Server 首版"），不是"看情况"

## 常见误判（避免）

- **误判 1**：靠 "我印象里 action 型工具都改过了" 答 R2。必须实际 grep。本次先粗糙正则误判为"28 个需人工检查"，精准正则重扫才得 44/44。
- **误判 2**：把 "工具数量少" 当缺陷。47 个原生工具已经过密，用户明确偏好"通用能力优先于特化堆砌"（memory 里的持久偏好）。不要建议加更多特化工具作为答案。
- **误判 3**：把"用户可见 API 变化"当 minor bump 信号。`execute_code` 从 `action=evaluate/run` 变成无 action 是 agent-facing 破坏性变更，但**用户看不到**——用 patch 版号消化正确。真 minor bump 信号见发版技能对应章节。
- **误判 4**：SOUL.md 缺失引导时新增独立条款编号（如加个 §2.13）。**在既有条款内追加子条款更好**，条款编号 1~12 保持稳定，避免打断其他文档对 §2.10 的引用。

## 完整流程模板（可复用）

```
Step 1: 覆盖矩阵
  1a. grep -rn AgentTool 枚举工具
  1b. 对齐 Unity Editor 常用模块清单
  1c. 输出三列表(专用/兜底/未覆盖)
  1d. 结论: 覆盖率 X%, 真缺口 N 个

Step 2: 对抗式根因复查
  2a. 列历史错误的根因分类 R1..Rn
  2b. 每个 R 定义可 grep 验证的判据
  2c. execute_code 精准扫描, 输出 GOOD/WARN/SILENT 分类
  2d. 结论: 已解决的 vs 剩余系统性缺陷

Step 3: v1.8.0 时机
  3a. 引用 release-and-bulk-migration.md 判据
  3b. 给具体锚点(Phase N / MCP Server / 里程碑命名)
  3c. 结论: patch bump 还是 minor bump, 为什么

Step 4: 交付
  4a. 表格化, ✅/❌ 判定明确
  4b. 每个断言引可 grep 实证
  4c. 未做的事(留白)清单 + 原因
  4d. 询问是否立即做修复补丁(通常是 SOUL 层小 patch, 零代码)
```

## Undo 审计的三次修正教训（v1.7.29 实证，正则单一信号是 false positive 陷阱）

用户在 v1.7.28 后追问"目前所有 agent 操作都能 undo 吗?"—— 触发 47 工具全量 Undo 审计。**第一次扫描判 8 个 mut_no_undo 工具，最终真需改的只有 5 处**。差在正则识别的等价信号数：

**v1 只搜 `Undo.RecordObject` 裸调用** → 34 mutating 工具里 8 个报"缺" → **全部 false positive**：这些工具走 `ToolHelpers.RecordUndo(target, name)` / `ToolHelpers.RegisterCreatedObject(obj, name)` 包装 API（项目内自建的 Undo 便利函数），被单一 grep 漏掉。

**v2 补正则含 `ToolHelpers.RecordUndo` + `ToolHelpers.RegisterCreatedObject`** → 23/34 已合规，剩 4 个 importer 工具 + ManageAssetTool 疑似真缺。

**v3 再补 `SerializedObject.ApplyModifiedProperties`** → ManageInputTool 归入已合规（Unity 官方契约：`SerializedObject.ApplyModifiedProperties()` 自动记录 Undo，`InputManager.asset` 编辑经 Ctrl+Z 完整可回）。最终真需改 = 5 handlers × 4 文件。

**教训**：`Undo` 审计的正则**至少要含四个等价信号**才不产生 false positive：
1. `Undo.RecordObject`（裸调用）
2. `Undo.RegisterCreatedObjectUndo`（裸调用）
3. `ToolHelpers.RecordUndo` / `ToolHelpers.RegisterCreatedObject`（项目内包装 API，须逐项目枚举）
4. `SerializedObject.ApplyModifiedProperties`（Unity 官方契约：自动记录 Undo，无需显式调用）

**方法固化**：审计脚本 grep pattern：
```python
UNDO_SIGNALS = r"(Undo\.RecordObject|Undo\.RegisterCreatedObjectUndo|Undo\.RegisterCompleteObjectUndo|ToolHelpers\.RecordUndo|ToolHelpers\.RegisterCreatedObject|SerializedObject.*ApplyModifiedProperties|\.ApplyModifiedProperties\(\))"
```

**其他排除项**：`ExecuteCodeTool` 里的 `File.Delete`/`File.Move` 是**危险 API 黑名单字符串**（用户代码执行前的 match 拒绝表），不是实际 mutation，审计时应主动排除黑名单 / 拒绝列表类字符串引用。判据：找 `.cs` 里的 `"File.Delete"` 是字符串字面量而非方法调用。

## Read-Side 覆盖率审计（v1.7.29 新识别的盲区）

**本轮翻车实证**：用户问"agent 帮我抓帧看运行时性能阻点该怎么用"，我第一反应回答"AgentCore 当前工具集不能直接抓帧"—— **错的**。`manage_profiler` 存在于 `Editor/Tools/Native/Extended/ManageProfilerTool.cs`，覆盖 5 个 action（`get_stats` / `get_memory` / `start_recording` / `stop_recording` / `get_rendering_stats`）。我作为 agent 都没意识到自己有这个工具。

**根因分类**：既有的 mutation-focused 审计只查"会不会破坏 Undo 契约的写操作"，**只读诊断工具（Profiler、Physics Debugger、Frame Debugger、Memory Profiler）从来没被扫过**。这是 audit 方法本身的盲区。

**新增审计流程 — Read-Side 单独一轮**：
1. `grep AgentTool` 枚举全部 tool_name（**必须先跑，回答"AgentCore 能不能做 X" 前的强制前置**）
2. 对每个 tool 读 `Capabilities` / `Visibility` / `Description`；`Capabilities=ReadProject`+ `Visibility=OnDemand` 是**隐身工具**，agent 平时可能意识不到自己有它
3. 与 Unity Analysis / 只读 API 面（`UnityStats` / `ProfilerRecorder` / `ProfilerDriver` / `FrameDebuggerUtility` / `Physics.OverlapSphere*` / `SceneView.Frame*`）交叉
4. 每个只读工具评价三档：**存在且够用** / **存在但浅（缺 action 或缺 API 深度）** / **不存在**

**Agent 可发现性 = 隐性缺陷维度**：即使工具存在，若 `Visibility=OnDemand` 且工具 Description 关键词未触发用户提问的语义（如 `manage_profiler` Description 里没写 "抓帧/frame capture/性能阻点/bottleneck"），主 agent 会漏掉工具。修法有二：
- **短期**：在 SOUL / 引导层加"性能诊断"章节明列 `manage_profiler` 用法
- **长期**：改工具 Description 关键词覆盖用户常用中文/英文提问词

## Bugs in `os.walk` 双层 for + break 覆写（重复踩过）

审计脚本用 `os.walk(root)` + 内层 `for f in files` + `break` 想跳出去时，**内层 break 只跳出内 for，外层 for root, dirs, files 继续迭代，会持续覆写 found_file 变量**——结果所有 lookup 都被最后一次 root 的匹配（或找不到）覆盖，误报 FILE_NOT_FOUND。

**修法**：不用双 for + break，改**建全量 tool→file 映射 dict**（一次 os.walk 建索引），后续查询走 dict lookup。本轮再次踩到，SKILL 已记录但脚本模板还没固化。

## 关联

- 本方法论产出的修复通常是 **SOUL 引导层小 patch**（零代码变更），配套发版流程走 `release-and-bulk-migration.md` 的 checklist，风险等级零
- SOUL.md 已 9KB+，加子条款不加条款编号可避免文档引用打断
- 覆盖矩阵扫描脚本可保留在 `scripts/` 下作可复用工具（本次未固化脚本，值得后续沉淀）
