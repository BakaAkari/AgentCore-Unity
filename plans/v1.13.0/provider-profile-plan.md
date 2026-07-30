# v1.13.0 Provider Profile 管理 — 方案文档

**目标**：支持保存多个 LLM Provider 配置（baseUrl + apiKey + modelName + 附加参数），UI 一键切换，取代目前"每次改配置都要复制粘贴"的痛点。

**状态**：草案 — 待用户审阅
**Owner**：Hermes（方案）+ Claude Code（实现）
**依赖**：v1.12.0-alpha.7 分支（已合入 main 之前的开发分支）

---

## 1. 现状（探勘结果，事实性）

### 1.1 目前 LLM 配置分散在两处
- **`AgentCoreSettings.instance`**（ScriptableSingleton，`ProjectSettings/AgentCoreSettings.asset`）
  - `llmEndpoint` (默认 `http://172.16.248.60:8000/v1`)
  - `llmModel` (默认 `glm-5.2`)
  - `temperature`, `maxTokens`, `reasoningEffort`, `reasoningMaxTokens`, `extraRequestBody`
  - `enableReasoningOutput`
- **`SecureKeyStorage`**（EditorPrefs，OS 级注册表/plist）
  - `AgentCore_LLM_ApiKey` — LLM API Key
  - `AgentCore_Mem0_ApiKey` — Mem0 API Key
  - `AgentCore_LightRAG_ApiKey` — LightRAG API Key
  - 已经**不进 git**，符合密钥安全要求

### 1.2 读取路径
- `Editor/LLM/OpenAICompatibleClient.cs` L33/94 — 从 `SecureKeyStorage.GetLLMApiKey()` 取 key
- 从 `AgentCoreSettings.instance` 取 endpoint/model 的调用点约 15 处（AgentLoop / Runner / SelfChallenge / Compression 等）
- **`FallbackRouter`** 存在（AgentLoop.cs:314）— 这是**已有的多 provider fallback 机制**，需要在方案中考虑与 Profile 的关系

### 1.3 UI 设置页
- `ModelAgentSettingsPage`（338 行）— 目前展示 endpoint/model/apiKey 的编辑 UI
- `ApiKeyDisplayKey = "model-agent.apiKeyDisplay"` — apiKey 显示/隐藏切换（明文查看时会存到 EditorPrefs）
- 使用 `AgentCoreSettingsUi` 框架统一渲染

### 1.4 版本管理
- `AgentCoreSettings.CurrentVersion = 21` — 已有版本号，`ApplySettingsMigrations` 应处理版本迁移

---

## 2. 关键决策（这份文档只锁死这些）

### 2.1 数据模型

```csharp
[Serializable]
public class ProviderProfile
{
    public string id;              // GUID，主键，永不变
    public string displayName;     // "本地 GLM-5.2" / "OpenAI GPT-5" 等，用户可编辑
    public string endpoint;        // base URL，例 http://172.16.248.60:8000/v1
    public string modelName;       // 例 glm-5.2
    // apiKey 不在此结构中 — 见 §2.2
    
    // === 可选字段（缺失走全局默认或 provider 默认）===
    public bool overrideTemperature;
    public float temperature;
    public bool overrideMaxTokens;
    public int maxTokens;
    public bool overrideReasoning;
    public string reasoningEffort;      // "low"/"medium"/"high"/""
    public int reasoningMaxTokens;
    public bool enableReasoningOutput;
    public string extraRequestBody;     // 追加 JSON

    // === 元数据 ===
    public string providerHint;    // "openai" / "glm" / "claude" / "gemini" / "" — 仅提示用户，不影响逻辑（当前所有 provider 都走 OpenAI-compatible endpoint）
    public string notes;           // 用户备注
    public long createdAtUnixMs;
    public long lastUsedAtUnixMs;
}
```

**理由**：
- **`id` 用 GUID 而非 displayName 作主键**：允许用户改 displayName 不破坏 activeProfileId 引用
- **`overrideXxx` 布尔位**：Profile 只覆盖"用户明确设置"的字段，其他 fallthrough 到 `AgentCoreSettings` 全局默认。避免每次新建 Profile 都要填一堆无关字段。
- **`providerHint` 只是提示**：目前所有 endpoint 走 OpenAI-compat，将来若要接 Claude 原生 API 再扩枚举

### 2.2 apiKey 存储 — **继续走 EditorPrefs，按 profileId 分键**

```
EditorPrefs key: AgentCore_ProfileKey_<profileId>
```

**理由**：
- **不进 git** — 与现有 `SecureKeyStorage` 一致，保持既定安全边界
- **跨项目不共享** — 一个 profile 在不同项目要重填 key，符合"每个 Unity 项目独立环境"的常规心智（也可后续用配置项切换到 `%APPDATA%`，但初版不做）
- **`SecureKeyStorage` 扩展 API**：新增 `GetProfileApiKey(string profileId)` / `SetProfileApiKey(string profileId, string key)` / `DeleteProfileApiKey(string profileId)`
- **不重用 `AgentCore_LLM_ApiKey`** — 保留它，作为"未启用 Profile 时的旧路径"，供迁移期兼容

### 2.3 Profile 列表存储 — **独立 ScriptableSingleton 文件**

```
ProjectSettings/AgentCoreProviderProfiles.asset
```

**内容**：
```csharp
public class AgentCoreProviderProfiles : ScriptableSingleton<AgentCoreProviderProfiles>
{
    public int schemaVersion = 1;
    public List<ProviderProfile> profiles = new();
    public string activeProfileId;  // 空 = 用 AgentCoreSettings 旧字段
}
```

**理由**：
- **独立 asset 而非嵌入 AgentCoreSettings**：Profile 列表可能变长，独立文件让 diff 更清晰，也不污染主 Settings
- **进 git**：**endpoint + modelName 会被同事看到**，这是 feature 不是 bug — 团队可以约定共用 Profile 列表（不含 key），每人本地填自己的 key
- **`activeProfileId` 存在 Profile 文件里** — 项目级共享。若要"每个开发者用不同 Profile"，可以后续加一个 `EditorPrefs` 覆盖层（不在初版范围）

### 2.4 迁移策略 — **零破坏**

**规则**：
1. **v1.13.0 首次启动**：`AgentCoreProviderProfiles.asset` 不存在 → 创建空文件 → `activeProfileId = ""` → **所有读取路径 fallthrough 到旧 `AgentCoreSettings.llmEndpoint/llmModel + SecureKeyStorage.GetLLMApiKey()`**。用户完全无感。
2. **用户首次打开 "Provider Profiles" 设置页**：如果 profiles 列表为空 + `AgentCoreSettings.llmEndpoint` 有值 → **弹窗询问**"检测到已有配置，是否创建 'Default' Profile？"（不弹窗默认不做，避免自动动用户数据）
3. **用户切换到某 Profile 后**：读取路径改为 `activeProfileId → Profile 字段 → fallthrough 到 AgentCoreSettings`
4. **旧字段永不删除**：`AgentCoreSettings.llmEndpoint/llmModel` 保留，作为 fallback。未来某版本（v2.0？）再考虑移除，需要一个 deprecation 周期

**读取解析优先级**（新的统一入口 `ActiveModelConfig`）：
```
endpoint  = ActiveProfile?.endpoint ?? AgentCoreSettings.llmEndpoint
modelName = ActiveProfile?.modelName ?? AgentCoreSettings.llmModel
apiKey    = ActiveProfile != null ? SecureKeyStorage.GetProfileApiKey(profileId) : SecureKeyStorage.GetLLMApiKey()
temperature = ActiveProfile?.overrideTemperature == true ? ActiveProfile.temperature : AgentCoreSettings.temperature
...
```

**关键约束**：**AgentLoop / OpenAICompatibleClient 等所有 15 个调用点都改为读取 `ActiveModelConfig`**，而不是继续读裸 `AgentCoreSettings.llmEndpoint`。这是本次改动的**核心机械工作量**。

### 2.5 与 FallbackRouter 的关系

**决策**：**本期不动 FallbackRouter**。
- FallbackRouter 目前基于 `AgentCoreSettings` 单一配置构造 fallback 链
- 后续可以加"Profile 内配置 fallback 顺序"，但**不在 v1.13.0 范围**
- v1.13.0 只做"当前 active model"的切换；FallbackRouter 读的仍是通过 `ActiveModelConfig` 解析出的 active endpoint

**未决议问题（记录，未来解决）**：
- 是否让每个 Profile 独立配置 fallback 目标 Profile ID 列表？（复杂度高，先不做）
- 切换 Profile 时是否 reset FallbackRouter 状态？（初版：是，简单起见）

---

## 3. UI 设计（细节交给 Claude Code）

**Hermes 只锁死的关键点**：
- **Settings 里新增一页 "Provider Profiles"**（不是塞进 Model Agent 页）
  - 增删改 Profile，每个 Profile 一个可折叠面板
  - apiKey 输入框有显示/隐藏 toggle（复用 `ApiKeyDisplayKey` 模式）
  - 每个 Profile 有 "Set as Active" 按钮
- **ChatWindow 顶部工具栏**（左上角，session 切换旁边）**新增下拉**
  - 显示当前 active Profile 的 displayName
  - 下拉展开列出所有 Profile
  - 底部"Manage Profiles..."入口跳到 Settings
- **切换语义**：立即生效，下一次 LLM 请求用新配置。**不打断正在进行的对话**（正在跑的请求用旧配置完成）。
- **无 Profile 时**：下拉显示 "Legacy Config (Model Agent Settings)"，仍能正常工作

**具体交互细节（颜色、间距、图标、L10n key 命名等）由 Claude Code 决定**。

---

## 4. Task 分解（给 Claude Code 的 batch）

### Batch A — 数据层（无 UI，不影响运行时）
- 新增 `Editor/Config/ProviderProfile.cs`
- 新增 `Editor/Config/AgentCoreProviderProfiles.cs`（ScriptableSingleton）
- 新增 `Editor/Config/ActiveModelConfig.cs`（静态解析器，返回 endpoint/model/apiKey/temperature/... 的当前有效值）
- 扩展 `SecureKeyStorage` 加 profile-scoped API
- **零改运行时**：所有旧调用点仍用 `AgentCoreSettings.instance.llmEndpoint`

### Batch B — 运行时切换（15 个读取点改为 ActiveModelConfig）
- `OpenAICompatibleClient.cs` (2 处)
- `AgentLoop.cs` / `AgentLoop.LLM.cs` / `AgentLoop.Runner.cs` / `AgentLoop.SelfChallenge.cs` 里所有 `AgentCoreSettings.instance.llmEndpoint / llmModel` 读取
- `ConversationCompressor.cs` / `ToolResultCompressor.cs`
- `FallbackRouter` 内部保持不变，但从外部注入的 endpoint 改成 `ActiveModelConfig.Endpoint`
- **验证**：无 Profile 时行为与之前完全一致（回归测试）

### Batch C — UI 层
- 新增 `Editor/Config/Settings/Pages/ProviderProfilesSettingsPage.cs`
- ChatWindow 工具栏下拉（UI Toolkit）
- L10n（复用 `ContextMemorySettingsPage` 中 apiKeyDisplay 模式）
- 迁移弹窗（"检测到已有配置，创建 Default Profile？"）

### Batch D — 版本迁移 + 文档
- `AgentCoreSettings.CurrentVersion` bump 到 22
- `ApplySettingsMigrations` 加 v21→v22 步骤（仅确保新 asset 存在，不动数据）
- CHANGELOG
- 更新 README / SOUL.md（如需要）

---

## 5. 验收标准

**Batch A**：编译通过，`ActiveModelConfig.Endpoint` 在无 Profile 时返回 `AgentCoreSettings.llmEndpoint` 值
**Batch B**：完整回归——无 Profile 时新旧行为**逐字段一致**（endpoint/model/apiKey/temperature/reasoning）；用户可正常发消息
**Batch C**：创建 Profile → 填入不同 endpoint+key+model → 从工具栏切换 → 发消息，Console 或抓包验证走了新 endpoint
**Batch D**：v1.12.0 → v1.13.0 首次启动无报错，无数据损失

---

## 6. 未决问题（暂搁置，需用户拍板才做）

- (Q1) apiKey 是否需要加密（DPAPI/Keychain）而非明文 EditorPrefs？——**默认不做**，与现有 SecureKeyStorage 保持一致
- (Q2) Profile 是否支持导入/导出 JSON？——**初版不做**，用户手动编辑 asset 文件也行
- (Q3) 是否为 Auxiliary Model（Mem0 embed / LightRAG）也做 Profile？——**初版不做**，那些 endpoint 变化频率低
- (Q4) 是否允许"临时 Profile"（不保存到列表，只本次会话生效）？——**初版不做**

---

## 7. 风险

- **R1**：15 个读取点改动不完全 → 部分请求用旧配置，部分用新配置。**缓解**：Batch B 完成后必须做完整 grep 验证（`AgentCoreSettings.instance.llmEndpoint\|.llmModel` 应只在 `ActiveModelConfig.cs` 和 UI 层出现）
- **R2**：apiKey EditorPrefs 键膨胀（每个 profile 一个键）→ 删 profile 时忘记清 key。**缓解**：`AgentCoreProviderProfiles.RemoveProfile()` 必须调 `SecureKeyStorage.DeleteProfileApiKey()`
- **R3**：Profile 文件被 git 追踪，团队 pull 后 activeProfileId 指向别人的 profile 但本地没有那个 key → 请求失败。**缓解**：`ActiveModelConfig` 检测到 apiKey 为空时给明确错误提示（不静默 fallthrough 到 legacy config）
