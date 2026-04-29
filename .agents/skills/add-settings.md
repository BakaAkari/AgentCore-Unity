# Skill: 新增设置项 (add-settings)

> 向 `AgentCoreSettings` 添加新的配置字段，并在 Settings Provider UI 中展示。

---

## 前置检查

**修改前必须先阅读以下文件，理解当前实现：**

1. 阅读 `Editor/Config/AgentCoreSettings.cs` — 了解现有字段、版本号、迁移逻辑
2. 阅读 `Editor/Config/AgentCoreSettingsProvider.cs` — 了解现有 UI section 结构
3. 如果涉及敏感数据，阅读 `Editor/Config/SecureKeyStorage.cs` — 了解安全存储方式

> **关键原则**: 以实际代码中的 `CurrentVersion` 值和现有字段模式为准，不要假设版本号。

---

## 涉及文件

| 文件 | 作用 | 修改类型 |
|------|------|----------|
| `Editor/Config/AgentCoreSettings.cs` | 设置数据存储 | 添加字段 + 迁移逻辑 |
| `Editor/Config/AgentCoreSettingsProvider.cs` | Settings UI | 添加 UI 绘制代码 |
| `Editor/Config/SecureKeyStorage.cs` | 敏感数据存储 | 仅当新字段是 API Key 时修改 |

---

## 步骤

### Step 1: 在 AgentCoreSettings 中添加字段

遵循现有字段的模式：

1. 添加 `[SerializeField]` 私有字段（带合理默认值）
2. 添加公共属性，setter 中调用 `Save(true)`
3. 如果是 API Key 等敏感数据，使用 `SecureKeyStorage` 而非 `[SerializeField]`

### Step 2: 递增版本号并添加迁移逻辑

1. 找到 `CurrentVersion` 常量，递增 1
2. 在 `MigrateSettings()` 方法中添加新版本的迁移逻辑
3. 迁移逻辑中设置新字段的默认值

### Step 3: 在 SettingsProvider 中添加 UI

1. 在 `OnGUI` 方法中添加新 section 的调用
2. 实现 `Draw<Feature>Section()` 方法
3. 使用 IMGUI 控件（`EditorGUILayout.*`）— Settings Provider 使用 IMGUI，不是 UI Toolkit

### Step 4: 如果工具需要读取新设置

在工具或客户端中通过 `AgentCoreSettings.instance` 访问，未配置时返回清晰的错误提示。

---

## 设置字段类型对照表

| 数据类型 | SerializeField 类型 | IMGUI 控件 | 注意事项 |
|----------|---------------------|------------|----------|
| 文本 | `string` | `EditorGUILayout.TextField` | |
| 密码/Key | 不存储在 Settings | `EditorGUILayout.PasswordField` | 使用 `SecureKeyStorage` |
| 布尔 | `bool` | `EditorGUILayout.Toggle` | |
| 整数 | `int` | `EditorGUILayout.IntField` 或 `IntSlider` | |
| 浮点 | `float` | `EditorGUILayout.FloatField` 或 `Slider` | |
| 枚举 | `MyEnum` | `EditorGUILayout.EnumPopup` | 枚举需要 `[Serializable]` |
| 下拉选择 | `string` 或 `int` | `EditorGUILayout.Popup` | |

---

## 检查清单

- [ ] 字段有 `[SerializeField]` 标记（敏感数据除外）
- [ ] 字段有合理的默认值
- [ ] 公共属性的 setter 调用 `Save(true)`
- [ ] `CurrentVersion` 已递增（基于实际代码中的当前值）
- [ ] `MigrateSettings()` 中有新版本的迁移逻辑
- [ ] SettingsProvider 中有对应的 UI 绘制
- [ ] 敏感数据使用 `SecureKeyStorage` 而非直接存储
- [ ] UI 控件有 `GUIContent` 提供 tooltip 说明
- [ ] 依赖此设置的功能在设置为空时有清晰的错误提示
- [ ] 编译通过，Settings 面板正常显示

---

## 常见错误

| 错误 | 原因 | 修复 |
|------|------|------|
| 设置重启后丢失 | 忘记 `Save(true)` | 在 setter 中调用 `Save(true)` |
| 旧用户升级后字段为空 | 缺少迁移逻辑 | 在 `MigrateSettings()` 中设置默认值 |
| API Key 明文存储 | 直接用 `[SerializeField]` | 改用 `SecureKeyStorage` |
| Settings 面板不显示 | 方法未在 `OnGUI` 中调用 | 在 `OnGUI` 中添加 `DrawXxxSection()` 调用 |
| 编辑时 UI 闪烁 | 每帧都调用 `Save` | 只在值变化时保存（`if (newVal != oldVal)`） |

---

## 如何找到参考实现

不要依赖固定的文件列表。按以下方式发现参考：

1. **现有设置字段**: 阅读 `AgentCoreSettings.cs`，观察现有字段的声明和属性模式
2. **迁移逻辑**: 在 `MigrateSettings()` 中查看现有的版本迁移示例
3. **UI 绘制**: 在 `AgentCoreSettingsProvider.cs` 中搜索 `Draw*Section` 方法，选择最相似的参考
4. **安全存储**: 阅读 `SecureKeyStorage.cs` 了解 `GetKey`/`SetKey` 的用法
