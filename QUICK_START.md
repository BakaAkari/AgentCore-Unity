# AgentCore Unity — 快速上手

在 Unity 编辑器里和 AI 直接对话，让它帮你搭场景、写代码、查问题、整理项目。默认已适配企业内网本地部署模型，装完即可用，无需额外配置。

**三步上手**

1. 通过 UPM 添加插件
2. 按 `Ctrl+Shift+Q`（macOS `Cmd+Shift+Q`）打开 AgentCore
3. 直接描述你的需求

---

## 1. 安装

需要 Unity 2021.3 LTS 及以上。

1. `Window > Package Manager` → 左上角 `+`
2. **Add package from tarball...**，选择 `com.agentcore.unity-<version>.tgz`（版本号见 [`package.json`](package.json)）
   或 **Add package from git URL...**，粘贴仓库地址（企业内网地址向项目 TA 或工具组同事索取）
3. 等待导入完成

装完后菜单多出 `Window > AgentCore`，默认连接企业内网模型，无需手动配置即可直接使用。

---

## 2. 基础使用

打开 `Window > AgentCore`（或 `Ctrl+Shift+Q`），像跟同事说话一样描述需求。它会自己理解意图、拆解步骤、分析上下文、动手执行；不确定时主动提问，高风险操作先跟你确认，编译出错自己读 Console 自己修。有额外约束顺嘴提一句就行：

> "帮我搭个角色控制器"
> "排查这个 Prefab 为什么报 Missing Reference"
> "扫一遍 Assets 目录看看有没有重复资源"

**会话级信任**：确认弹窗支持快捷放行——**Trust Low/Med**（ReadOnly/Low/Medium 风险工具直通，High 风险仍弹窗）、**YOLO (All)**（本会话所有操作直通，含删除/推送等破坏性操作，慎用）。

---

## 3. 常见场景

**搭原型**
> 在当前测试场景里搭一个可操作的第三人称角色原型，资源放 `Assets/Prototype/`，别动现有场景和输入设置。

**排查问题**
> 排查角色受击后动画不播放的问题。先只读诊断、给根因和方案，确认后再改。

**整理项目**
> 扫一遍 Assets，看有没有重复资源、孤立资源、超大资源。只出方案，别动文件。

**批量处理**
> 找 `Assets/Game/Prefabs/Enemies/` 下所有敌人 Prefab，统计组件配置。先出差异清单，再修复不合规范的，保留引用用 Undo，改完重新扫描跑测试。

四个例子的共同套路：**分析 → 操作 → 编译/测试 → 修正 → 交付摘要**。任务描述越具体（范围、禁区、验证方式），执行越准。

---

## 4. 常用快捷键

| 快捷键 | 功能 |
|--------|------|
| `Ctrl+Shift+Q` | 打开 AgentCore 窗口 |
| `Ctrl+Shift+X` | 全局上下文注入（选中物体/面板后按，自动采集上下文） |
| `Enter` | 发送消息 |
| `Ctrl+Enter` | 输入框内换行 |
| `Escape` | 取消当前任务 |
| `Ctrl+N` | 新建会话 |
| `Ctrl+Shift+E` | 导出当前会话 |

macOS 用 `Cmd` 代替 `Ctrl`。

---

## 5. 使用建议

- 复杂任务先要只读分析，确认方案后再授权修改。
- 有明确禁区（目录/文件/组件）直接说。
- 改动前确保有版本控制或备份点。
- 高风险操作核对确认面板再放行；不确定就用 Trust Low/Med，别直接 YOLO。
- 相似任务一次性交给它批量跑，别一条条发。
- 项目根目录放一份 `PROJECT.md` 写项目约定，Agent 会自动读取。

---

## 6. 高级设置：切换模型 Provider

默认使用企业内网本地模型，**以下内容仅在需要切换到其他供应商/模型时才需要看**。

打开 `Edit > Project Settings > AgentCore`，进 **Model & Agent** 分页。配置单位是 **Provider Profile**：一个 profile 保存一套完整连接参数，可建多个在不同供应商间切换，当前生效的叫 Active Profile。

| 字段 | 说明 |
|------|------|
| Display Name | Profile 显示名 |
| Endpoint | OpenAI 兼容 API 地址 |
| API Key | 点 **Set API Key** 弹窗输入，不明文显示，存本机不随项目同步 |
| Model | 下拉选择；点 **Refresh** 拉取模型列表；不支持列表接口时退化为文本框 |
| Override Temperature | 关闭用全局默认；打开后滑块调节 0.0–2.0 |
| Override MaxTokens | 关闭用全局默认；打开后手填单次最大输出 token |
| Override Reasoning | 关闭用全局默认；打开后可设 effort（low/medium/high）、reasoning MaxTokens、是否输出 reasoning |
| Extra Request Body | 追加到请求体的自定义 JSON，留空不追加 |

操作：**Add Profile** 新建 → 填好字段 → **Test Connection** 验证 → **Set as Active** 切换生效。**Duplicate** 可复制现有 profile 改动，不影响原 profile。

---

## 故障排查

| 问题 | 解决方案 |
|------|---------|
| Chat 窗口无响应 | 检查 Provider Profile 的 Endpoint 和 API Key，点 Test Connection 复核 |
| 工具调用报错 | 查看 Unity Console 错误日志 |
| 编译后会话丢失 | 通常自动恢复，未恢复可重新打开 AgentCore 窗口 |
| VCS / Indexing 工具不可见 | 确认在 Project Settings 的 Tools & Extensions 分页启用了对应组件 |
