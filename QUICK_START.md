# AgentCore Unity — 使用指南

在 Unity 编辑器里和 AI 直接对话，让它帮你搭场景、写代码、查问题、整理项目。AgentCore 是一个带思维链的 Agent，像跟同事说话一样把需求告诉它，它自己拆解、动手、修正、交付。

**四步上手**

1. 通过 UPM 添加插件
2. 在 `Edit > Project Settings > AgentCore` 确认连接
3. 按 `Ctrl+Shift+Q`（macOS `Cmd+Shift+Q`）打开 AgentCore
4. 直接描述你的需求

---

## 1. 插件安装

通过 Unity Package Manager 集成，需要 Unity 2021.3 LTS 及以上。

1. 打开 `Window > Package Manager`
2. 点左上角 `+` → **Add package from tarball...**
3. 选择当前 `com.agentcore.unity-<version>.tgz`（当前版本以 [`package.json`](package.json) 的 `version` 字段为准）
4. 等待导入完成

装完后菜单会多出 `Window > AgentCore`。

> 也支持 UPM Git URL 安装：`Add package from git URL...`，粘贴仓库地址（企业内网地址向项目 TA 或工具组同事索取）。两种方式二选一即可，功能等价。

---

## 2. 模型配置

打开 `Edit > Project Settings > AgentCore`，第一个分页 **Model & Agent**。

配置的单位是 **Provider Profile**：每个 profile 保存一套完整连接参数（Endpoint + Model + 可选覆盖参数），可以建多个 profile 在不同供应商之间一键切换，当前生效的那个叫 Active Profile。

首次打开会自动创建一个指向企业内网本地部署模型的默认 profile，通常无需调整，点 **Test Connection** 验证连通即可。

**设置项字段**

| 字段 | 说明 |
|------|------|
| Display Name | Profile 显示名，自己起一个方便识别的名字 |
| Endpoint | OpenAI 兼容 API 接入地址 |
| API Key | 点 **Set API Key** 在弹窗里输入，界面不明文显示；存于本机，不随项目同步 |
| Model | 下拉选择；点 **Refresh** 从服务端拉取模型列表；服务端不支持列表接口时会退化为手动填名字的文本框 |
| Override Temperature | 关闭时使用全局默认；打开后可用滑块调节，范围 0.0–2.0 |
| Override MaxTokens | 关闭时使用全局默认；打开后手动填写单次最大输出 token 数 |
| Override Reasoning | 关闭时使用全局默认；打开后可设 Reasoning Effort（low/medium/high）、Reasoning MaxTokens、是否输出 reasoning 内容 |
| Extra Request Body | 追加到请求体的自定义 JSON，留空则不追加 |

**切换 / 新增 Provider**

- 点 **Add Profile** 新建一个空 profile，按上表填好后点 **Set as Active** 切换生效。
- 点已有 profile 的 **Duplicate** 可以复制一份改动，不影响原 profile。
- 填好新 profile 的 Endpoint / API Key / Model 后先点 **Test Connection** 验证，通过后新建会话即用该 profile。

---

## 3. 基础使用

**打开 AgentCore**

菜单 `Window > AgentCore`，或按 `Ctrl+Shift+Q`（macOS `Cmd+Shift+Q`）。

**描述你的需求**

像跟同事说话一样告诉它要做什么。它会自己分析、拆解、动手，不用你按模板填工单。

**它会自己做的事**

- 理解意图并拆解步骤
- 分析场景、资源、代码上下文
- 不确定时主动提问
- 高风险操作先跟你确认
- 编译出错自己读 Console、自己修

有额外约束顺嘴提一句就行，比如：

> "帮我搭个角色控制器"
>
> "排查这个 Prefab 为什么报 Missing Reference"
>
> "扫一遍 Assets 目录看看有没有重复资源"

**会话级信任**

确认弹窗右侧有两档快捷放行：

- **Trust Low/Med** — 本会话内 ReadOnly / Low / Medium 风险的工具直接放行，High 风险和破坏性操作仍会弹窗确认。
- **YOLO (All)** — 本会话所有工具直通，包括删除、推送、编译等破坏性操作。谨慎使用。

---

## 4. 常见使用场景

### 搭一个可运行的角色原型

> 在当前测试场景里搭一个可操作的第三人称角色原型，资源统一放 `Assets/Prototype/`，别动现有场景和项目输入设置。

它会怎么做：分析场景 → 创建角色/相机/地面 → 生成控制脚本 → 编译验证 → 修正 Console 错误 → 输出变更摘要。

### 定位并修复跨资源问题

> 排查当前场景里角色受击后动画不播放的问题。先只读诊断、给根因和最小修改方案，确认后再动手改。

它会做的事：沿 角色对象 → Animator → AnimationClip → Controller → 脚本调用 → 事件绑定 → 资源导入设置 逐层排查，再做最小修改 + 全量验证。

### 整理项目结构

> 扫一遍 Assets 目录，看有没有重复资源、命名不一致、孤立资源、超大资源。只输出整理方案，别动文件。

它会输出：项目地图、依赖关系、问题分级、整理计划、变更边界五份结构化报告，每条结论附资源路径或代码符号作为证据。

### 批量修改并验证

> 找 `Assets/Game/Prefabs/Enemies/` 下所有敌人 Prefab，统计 Collider、Rigidbody、NavMeshAgent、Layer、Tag 配置。先出差异清单，再只修复不符合规范的；保留序列化引用，用 Undo；改完重新扫描、跑 Editor Tests。

它会怎么做：批量发现 → 状态统计 → 规则比对 → 风险确认 → 批量修改 → 重新扫描 → 测试执行 → 结果汇总。

---

## 5. 常用快捷键

| 快捷键 | 功能 |
|--------|------|
| `Ctrl+Shift+Q`（macOS `Cmd+Shift+Q`） | 打开 AgentCore 窗口 |
| `Ctrl+Shift+X`（macOS `Cmd+Shift+X`） | 全局上下文注入 — 选中任意 Hierarchy / Project / Console 物体或面板后按此键，自动采集上下文注入聊天 |
| `Enter` | 发送消息 |
| `Ctrl+Enter` | 输入框内换行 |
| `Escape` | 取消当前任务 |
| `Ctrl+N` | 新建会话 |
| `Ctrl+Shift+E` | 导出当前会话 |

---

## 6. 使用建议

- 复杂任务先要求 Agent 做只读分析，再授权实际修改。
- 有明确的禁区（目录、文件、组件）就直接告诉它。
- 修改场景、脚本、资源前先保存项目并建立备份点（或确保有版本控制）。
- 删除文件、执行代码、批量修改等高风险操作，核对确认面板后再授权；不确定就先用 Trust Low/Med 而非 YOLO。
- 多个相似任务交给它一次性跑完，效率远高于你一条条发。
- 建议在项目根目录创建 `PROJECT.md` 描述项目约定，Agent 会自动读取。

**最佳实践**：让 Agent 走完「分析 → 操作 → 编译/测试 → 读取结果 → 修正 → 交付摘要」完整闭环，而不是只执行一个孤立操作。

---

## 故障排查

| 问题 | 解决方案 |
|------|---------|
| Chat 窗口无响应 | 检查 Provider Profile 的 Endpoint 和 API Key 是否正确配置，点 Test Connection 复核 |
| 工具调用报错 | 查看 Unity Console 的错误日志 |
| 编译后会话丢失 | 正常情况下会自动恢复，如未恢复可尝试重新打开 AgentCore 窗口 |
| VCS / Indexing 工具不可见 | 确认已在 Project Settings 的 Tools & Extensions 分页中启用对应组件 |
