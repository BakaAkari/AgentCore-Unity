using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using AgentCore.Editor.Bootstrap;
using AgentCore.Editor.Cloud;
using AgentCore.Editor.Tools;
using AgentCore.Editor.Utils;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace AgentCore.Editor.Config
{
    /// <summary>
    /// AgentCore 的 Project Settings 面板。
    /// 通过 Edit > Project Settings > AgentCore 访问。
    /// </summary>
    public class AgentCoreSettingsProvider : SettingsProvider
    {
        /// <summary>
        /// 从 package.json 读取的版本号缓存。
        /// 避免每帧重复读取文件。
        /// </summary>
        private static string _cachedVersion;

        private AgentCoreSettings _settings;
        private string _apiKeyDisplay = "";
        private string _connectionTestResult = "";
        private bool _isTesting = false;

        // --- 模型发现状态 ---
        private List<string> _availableModels = new List<string>();
        private bool _isFetchingModels = false;
        private string _fetchModelsResult = "";
        private bool _showModelDropdown = false;

        // --- 状态级别枚举（C3: 结构化状态消息） ---
        private enum StatusLevel { None, Success, Warning, Error, Loading }

        // --- mem0 连接测试状态 ---
        private string _mem0ApiKeyDisplay = "";
        private string _mem0TestResult = "";
        private StatusLevel _mem0TestStatus = StatusLevel.None;
        private bool _isMem0Testing = false;

        // --- mem0 连接缓存（C2） ---
        private bool? _mem0ConnectionValid = null;
        private DateTime _lastConnectionTest = DateTime.MinValue;
        private string _lastTestedEndpoint = "";
        private const int ConnectionCacheSeconds = 60;

        // --- mem0 User ID 检测/创建状态 ---
        private string _userIdCheckResult = "";
        private StatusLevel _userIdCheckStatus = StatusLevel.None;
        private bool _isCheckingUserId = false;
        private bool _isCreatingUserId = false;

        // --- LightRAG 连接测试状态 ---
        private string _lightragApiKeyDisplay = "";
        private string _lightragTestResult = "";
        private bool _isLightRAGTesting = false;

        // --- 工具管理 UI 状态 ---
        private bool _toolManagementFoldout = false;
        private Dictionary<string, bool> _categoryFoldouts = new Dictionary<string, bool>();

        private AgentCoreSettingsProvider(string path, SettingsScope scope)
            : base(path, scope) { }

        [SettingsProvider]
        public static SettingsProvider CreateSettingsProvider()
        {
            return new AgentCoreSettingsProvider("Project/AgentCore", SettingsScope.Project)
            {
                label = "AgentCore",
                keywords = new[] { "agent", "ai", "llm", "chat", "agentcore", "mem0", "lightrag" }
            };
        }

        public override void OnActivate(string searchContext, VisualElement rootElement)
        {
            _settings = AgentCoreSettings.instance;
            _apiKeyDisplay = SecureKeyStorage.HasLLMApiKey() ? "••••••••••••" : "(not set)";
            _mem0ApiKeyDisplay = SecureKeyStorage.HasMem0ApiKey() ? "••••••••••••" : "(not set)";
            _lightragApiKeyDisplay = SecureKeyStorage.HasLightRAGApiKey() ? "••••••••••••" : "(not set)";
        }

        public override void OnGUI(string searchContext)
        {
            if (_settings == null)
            {
                _settings = AgentCoreSettings.instance;
            }

            EditorGUILayout.Space(10);

            // === LLM Configuration ===
            DrawLLMSection();

            EditorGUILayout.Space(10);

            // === Agent Behavior ===
            DrawAgentBehaviorSection();

            EditorGUILayout.Space(10);

            // === Tool Management ===
            DrawToolManagementSection();

            EditorGUILayout.Space(10);

            // === Bootstrap Files ===
            DrawBootstrapSection();

            EditorGUILayout.Space(10);

            // === UI Preferences ===
            DrawUIPreferencesSection();

            EditorGUILayout.Space(10);

            // === Memory Service (mem0) ===
            DrawMemoryServiceSection();

            EditorGUILayout.Space(10);

            // === Knowledge Base (LightRAG) ===
            DrawKnowledgeBaseSection();

            EditorGUILayout.Space(20);

            // === About ===
            DrawAboutSection();
        }

        // ─────────────────────────────────────────────
        //  状态颜色辅助方法（C3: 结构化状态消息）
        // ─────────────────────────────────────────────

        /// <summary>
        /// 根据状态级别返回对应的颜色。
        /// </summary>
        private static Color GetStatusColor(StatusLevel level)
        {
            switch (level)
            {
                case StatusLevel.Success: return new Color(0.2f, 0.8f, 0.2f);
                case StatusLevel.Warning: return new Color(1f, 0.6f, 0f);
                case StatusLevel.Error:   return Color.red;
                case StatusLevel.Loading: return Color.gray;
                default:                  return EditorStyles.label.normal.textColor;
            }
        }

        /// <summary>
        /// 绘制带颜色的状态标签。
        /// </summary>
        private static void DrawStatusLabel(string text, StatusLevel level, bool miniLabel = false)
        {
            var baseStyle = miniLabel ? EditorStyles.miniLabel : EditorStyles.label;
            var style = new GUIStyle(baseStyle) { wordWrap = true };
            style.normal.textColor = GetStatusColor(level);
            EditorGUILayout.LabelField(text, style);
        }

        // ─────────────────────────────────────────────
        //  连接缓存辅助方法（C2）
        // ─────────────────────────────────────────────

        /// <summary>
        /// 检查连接缓存是否仍然有效。
        /// 当 Endpoint URL 变化时自动失效。
        /// </summary>
        private bool IsConnectionCacheValid()
        {
            return _mem0ConnectionValid.HasValue
                && _lastTestedEndpoint == _settings.mem0Endpoint
                && (DateTime.Now - _lastConnectionTest).TotalSeconds < ConnectionCacheSeconds;
        }

        /// <summary>
        /// 更新连接缓存。
        /// </summary>
        private void UpdateConnectionCache(bool success)
        {
            _mem0ConnectionValid = success;
            _lastConnectionTest = DateTime.Now;
            _lastTestedEndpoint = _settings.mem0Endpoint;
        }

        /// <summary>
        /// 清除连接缓存。
        /// </summary>
        private void InvalidateConnectionCache()
        {
            _mem0ConnectionValid = null;
        }

        // ─────────────────────────────────────────────
        //  UI 绘制方法
        // ─────────────────────────────────────────────

        private void DrawLLMSection()
        {
            EditorGUILayout.LabelField("LLM Configuration", EditorStyles.boldLabel);

            EditorGUI.indentLevel++;

            EditorGUI.BeginChangeCheck();

            _settings.llmEndpoint = EditorGUILayout.TextField(
                new GUIContent("API Endpoint", "OpenAI 兼容 API 端点地址"),
                _settings.llmEndpoint);

            // API Key 特殊处理 — 不直接显示明文
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(new GUIContent("API Key", "LLM 服务的 API Key"));
            EditorGUILayout.LabelField(_apiKeyDisplay, GUILayout.Width(120));

            if (GUILayout.Button("Set", GUILayout.Width(40)))
            {
                var newKey = EditorInputDialog.Show("Set API Key", "Enter your LLM API Key:", "");
                if (newKey != null)
                {
                    SecureKeyStorage.SetLLMApiKey(newKey);
                    _apiKeyDisplay = string.IsNullOrEmpty(newKey) ? "(not set)" : "••••••••••••";
                }
            }

            if (GUILayout.Button("Clear", GUILayout.Width(50)))
            {
                SecureKeyStorage.SetLLMApiKey("");
                _apiKeyDisplay = "(not set)";
            }

            EditorGUILayout.EndHorizontal();

            // Model 字段：Label + Popup下拉菜单 + Fetch 按钮（水平对齐）
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(new GUIContent("Model", "LLM 模型名称（点击 Fetch 从服务器获取列表，然后从下拉菜单选择）"));

            if (_availableModels != null && _availableModels.Count > 0)
            {
                // 有模型列表时显示 Popup
                int currentIndex = _availableModels.IndexOf(_settings.llmModel);
                if (currentIndex < 0) currentIndex = 0;
                var modelArray = _availableModels.ToArray();
                int newIndex = EditorGUILayout.Popup(currentIndex, modelArray);
                if (newIndex != currentIndex || _settings.llmModel != _availableModels[newIndex])
                {
                    _settings.llmModel = _availableModels[newIndex];
                    _settings.SaveSettings();
                }
            }
            else
            {
                // 未获取列表时显示当前模型名（只读标签）
                EditorGUILayout.LabelField(_settings.llmModel);
            }

            GUI.enabled = !_isFetchingModels && !_isTesting;
            if (GUILayout.Button(_isFetchingModels ? "..." : "Fetch", GUILayout.Width(50)))
            {
                FetchAvailableModels();
            }
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();

            // 显示 Fetch 结果状态
            if (!string.IsNullOrEmpty(_fetchModelsResult))
            {
                var isError = _fetchModelsResult.StartsWith("[FAIL]");
                var style = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true };
                style.normal.textColor = isError ? Color.red : new Color(0.2f, 0.8f, 0.2f);
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(EditorGUIUtility.labelWidth + 2);
                EditorGUILayout.LabelField(_fetchModelsResult, style);
                EditorGUILayout.EndHorizontal();
            }

            _settings.temperature = EditorGUILayout.Slider(
                new GUIContent("Temperature", "生成温度 (0.0-2.0)"),
                _settings.temperature, 0f, 2f);

            _settings.maxTokens = EditorGUILayout.IntField(
                new GUIContent("Max Tokens", "最大输出 token 数"),
                _settings.maxTokens);
            _settings.maxTokens = Mathf.Clamp(_settings.maxTokens, 1, 128000);

            if (EditorGUI.EndChangeCheck())
            {
                _settings.SaveSettings();
            }

            // Test Connection 按钮
            EditorGUILayout.Space(5);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(EditorGUI.indentLevel * 15);

            GUI.enabled = !_isTesting;
            if (GUILayout.Button(_isTesting ? "Testing..." : "Test Connection", GUILayout.Width(120)))
            {
                TestLLMConnection();
            }
            GUI.enabled = true;

            if (!string.IsNullOrEmpty(_connectionTestResult))
            {
                var style = new GUIStyle(EditorStyles.label);
                style.normal.textColor = _connectionTestResult.StartsWith("[OK]") ? new Color(0.2f, 0.8f, 0.2f) : Color.red;
                EditorGUILayout.LabelField(_connectionTestResult, style);
            }

            EditorGUILayout.EndHorizontal();

            EditorGUI.indentLevel--;
        }

        private void DrawAgentBehaviorSection()
        {
            EditorGUILayout.LabelField("Agent Behavior", EditorStyles.boldLabel);

            EditorGUI.indentLevel++;
            EditorGUI.BeginChangeCheck();

            _settings.maxToolCallRounds = EditorGUILayout.IntSlider(
                new GUIContent("Max Tool Rounds", "最大工具调用轮次（防止无限循环）"),
                _settings.maxToolCallRounds, 1, 50);

            _settings.maxContextTokens = EditorGUILayout.IntField(
                new GUIContent("Max Context Tokens", "上下文窗口 token 上限（0 = 自动根据模型推断）"),
                _settings.maxContextTokens);
            _settings.maxContextTokens = Mathf.Max(_settings.maxContextTokens, 0);

            _settings.reserveResponseTokens = EditorGUILayout.IntField(
                new GUIContent("Reserve Response Tokens", "为 AI 回复预留的 token 数"),
                _settings.reserveResponseTokens);
            _settings.reserveResponseTokens = Mathf.Clamp(_settings.reserveResponseTokens, 500, 16000);

            _settings.autoCompileCheck = EditorGUILayout.Toggle(
                new GUIContent("Auto Compile Check", "脚本修改后自动编译检查"),
                _settings.autoCompileCheck);

            _settings.autoConsoleCapture = EditorGUILayout.Toggle(
                new GUIContent("Auto Console Capture", "每轮工具执行后自动捕获 Console 错误"),
                _settings.autoConsoleCapture);

            _settings.fallbackRoutingEnabled = EditorGUILayout.Toggle(
                new GUIContent("Fallback Routing", "启用工具失败恢复策略路由"),
                _settings.fallbackRoutingEnabled);

            _settings.maxConsecutiveErrors = EditorGUILayout.IntSlider(
                new GUIContent("Max Consecutive Errors", "连续错误上限"),
                _settings.maxConsecutiveErrors, 1, 20);

            if (EditorGUI.EndChangeCheck())
            {
                _settings.SaveSettings();
            }

            EditorGUI.indentLevel--;
        }

        private void DrawBootstrapSection()
        {
            EditorGUILayout.LabelField("Bootstrap Files", EditorStyles.boldLabel);

            EditorGUI.indentLevel++;
            EditorGUI.BeginChangeCheck();

            _settings.bootstrapEnabled = EditorGUILayout.Toggle(
                new GUIContent("Enabled", "启用 Bootstrap Files 系统"),
                _settings.bootstrapEnabled);

            _settings.autoProjectContext = EditorGUILayout.Toggle(
                new GUIContent("Auto Project Context", "自动收集项目上下文信息"),
                _settings.autoProjectContext);

            if (EditorGUI.EndChangeCheck())
            {
                _settings.SaveSettings();
            }

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("User Files", EditorStyles.miniLabel);

            DrawUserFileRow("MEMORY.md", "本地知识文件 — Agent 可参考的项目知识和上下文");
            DrawUserFileRow("USER.md", "用户偏好文件 — 定义 Agent 的行为偏好和规则");

            EditorGUI.indentLevel--;
        }

        /// <summary>
        /// 绘制单个用户文件的编辑行（状态 + 创建/编辑按钮）。
        /// </summary>
        /// <param name="fileName">文件名（如 MEMORY.md）</param>
        /// <param name="description">文件描述</param>
        private void DrawUserFileRow(string fileName, string description)
        {
            var filePath = BootstrapLoader.FindUserFilePath(fileName);
            var exists = filePath != null;

            EditorGUILayout.BeginHorizontal();

            // 状态图标 + 文件名
            var statusIcon = exists ? "✓" : "✗";
            var statusColor = exists ? "green" : "grey";
            var label = $"{statusIcon} {fileName}";
            EditorGUILayout.LabelField(new GUIContent(label, description), GUILayout.Width(140));

            if (exists)
            {
                // 显示相对路径
                var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? "";
                var relativePath = filePath.StartsWith(projectRoot)
                    ? filePath.Substring(projectRoot.Length + 1).Replace('\\', '/')
                    : filePath;
                EditorGUILayout.LabelField(relativePath, EditorStyles.miniLabel);

                // 编辑按钮
                if (GUILayout.Button("Edit", GUILayout.Width(50)))
                {
                    // 使用系统默认编辑器打开
                    System.Diagnostics.Process.Start(filePath);
                }

                // 在 Explorer 中显示
                if (GUILayout.Button("Show", GUILayout.Width(50)))
                {
                    EditorUtility.RevealInFinder(filePath);
                }
            }
            else
            {
                EditorGUILayout.LabelField("(未创建)", EditorStyles.miniLabel);

                // 创建按钮
                if (GUILayout.Button("Create", GUILayout.Width(60)))
                {
                    CreateUserFile(fileName);
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// 创建用户文件（MEMORY.md 或 USER.md），包含模板内容。
        /// </summary>
        /// <param name="fileName">文件名</param>
        private static void CreateUserFile(string fileName)
        {
            var filePath = BootstrapLoader.GetDefaultUserFilePath(fileName);
            if (filePath == null)
            {
                Debug.LogError("[AgentCore] Cannot determine project root directory.");
                return;
            }

            // 确保目录存在
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // 生成模板内容
            var template = GenerateUserFileTemplate(fileName);

            try
            {
                File.WriteAllText(filePath, template, System.Text.Encoding.UTF8);
                Debug.Log($"[AgentCore] Created {fileName} at: {filePath}");
                AssetDatabase.Refresh();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AgentCore] Failed to create {fileName}: {ex.Message}");
            }
        }

        /// <summary>
        /// 生成用户文件的模板内容。
        /// MEMORY.md 会自动填充当前项目的基础信息（通过 ProjectContextCollector）。
        /// USER.md 生成带引导注释的空模板。
        /// </summary>
        /// <param name="fileName">文件名</param>
        /// <returns>模板 Markdown 内容</returns>
        private static string GenerateUserFileTemplate(string fileName)
        {
            if (fileName == "MEMORY.md")
            {
                return GenerateMemoryTemplate();
            }

            return GenerateUserTemplate();
        }

        /// <summary>
        /// 生成 USER.md 模板 — 通用 Unity 开发者 Agent 行为预设。
        /// 提供开箱即用的默认配置，用户可根据需要调整。
        /// </summary>
        private static string GenerateUserTemplate()
        {
            return @"# USER.md — Agent 行为预设

## 语言与沟通

- 使用中文回复，技术术语保留英文原文（如 GameObject、Component、Prefab）
- 回复简洁直接，先给结论再解释原因
- 代码注释使用中文，XML 文档注释使用英文
- 遇到歧义时主动确认，不要猜测用户意图

## 代码风格

- 命名规范：PascalCase（类/方法/属性）、camelCase（局部变量/参数）、_camelCase（私有字段）
- 使用 4 空格缩进，大括号换行（Allman 风格）
- 优先使用 `var` 关键字（类型明显时）
- 字符串拼接优先使用 `$""""` 插值，复杂拼接使用 `StringBuilder`
- 集合优先使用 `List<T>` 和 `Dictionary<TKey, TValue>`
- 异步方法后缀 `Async`，返回 `Task` 或 `Task<T>`
- 每个公共成员都要有 XML 文档注释

## Unity 开发偏好

- 新建脚本默认使用 MonoBehaviour 模板，放在 `Assets/Scripts/` 下
- 组件通信优先级：直接引用 > 事件/委托 > SendMessage（避免使用 SendMessage）
- 序列化字段使用 `[SerializeField]` 而非 public，配合 `[Header]` 和 `[Tooltip]` 分组
- Inspector 友好：使用 `[Range]`、`[TextArea]`、`[Space]` 等特性提升编辑体验
- 预制体工作流：修改后及时 Apply，保持 Override 最小化
- 场景操作后主动保存场景
- 资产命名：PascalCase，前缀表示类型（如 `Mat_Stone`、`Tex_Wood_Diffuse`、`Prefab_Player`）

## 性能意识

- 避免在 Update/FixedUpdate 中使用 Find/GetComponent，缓存引用
- 字符串比较使用 CompareTag() 而非 == tag
- 物理检测优先使用 NonAlloc 版本（如 Physics.RaycastNonAlloc）
- 大量对象操作时考虑对象池模式
- 协程中避免每帧 new WaitForSeconds，缓存 YieldInstruction

## 工作流偏好

- 修改脚本后自动刷新并检查编译错误
- 创建 GameObject 后设置合理的默认 Transform（位置归零、缩放为 1）
- 批量操作优先使用 batch_execute
- 操作前先用 find_gameobjects 或 manage_asset(search) 确认目标存在
- 遇到编译错误时，先用 read_console(get_errors) 获取完整错误信息
";
        }

        /// <summary>
        /// 生成 MEMORY.md 模板 — 自动收集项目信息 + 通用 Unity 开发知识预设。
        /// 提供 Agent 理解 Unity 项目所需的基础知识框架。
        /// </summary>
        private static string GenerateMemoryTemplate()
        {
            var sb = new System.Text.StringBuilder();

            sb.AppendLine("# MEMORY.md — 项目知识库");
            sb.AppendLine();

            // ===== 第一部分：自动收集的项目信息 =====
            sb.AppendLine("## 1. 项目基础信息");
            sb.AppendLine();
            sb.AppendLine("> 以下信息由 AgentCore 自动收集，可手动补充修正。");
            sb.AppendLine();
            try
            {
                var projectInfo = ProjectContextCollector.CollectExtended();
                if (!string.IsNullOrEmpty(projectInfo))
                {
                    sb.AppendLine(projectInfo);
                }
                else
                {
                    sb.AppendLine("<!-- 项目信息收集失败，请手动填写 -->");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"<!-- 项目信息收集失败: {ex.Message} -->");
                Debug.LogWarning($"[AgentCore] Failed to collect project info for MEMORY.md: {ex.Message}");
            }

            sb.AppendLine();

            // ===== 第二部分：项目描述（用户填写） =====
            sb.AppendLine("## 2. 项目概述");
            sb.AppendLine();
            sb.AppendLine("<!-- 在此描述你的项目，Agent 会据此理解项目背景 -->");
            sb.AppendLine("<!-- 例如：");
            sb.AppendLine("本项目是一款 3D 第三人称动作游戏，使用 URP 渲染管线。");
            sb.AppendLine("核心玩法包括战斗系统、技能系统和关卡探索。");
            sb.AppendLine("目标平台为 PC 和移动端。");
            sb.AppendLine("-->");
            sb.AppendLine();

            // ===== 第三部分：Unity 通用知识预设 =====
            sb.AppendLine("## 3. Unity 核心概念速查");
            sb.AppendLine();
            sb.AppendLine("### 生命周期执行顺序");
            sb.AppendLine("```");
            sb.AppendLine("Awake → OnEnable → Start → FixedUpdate → Update → LateUpdate → OnDisable → OnDestroy");
            sb.AppendLine("```");
            sb.AppendLine("- `Awake`: 对象初始化，不依赖其他对象的设置放这里");
            sb.AppendLine("- `Start`: 依赖其他对象的初始化放这里（所有 Awake 执行完后才调用）");
            sb.AppendLine("- `FixedUpdate`: 物理相关逻辑（固定时间步长，默认 0.02s）");
            sb.AppendLine("- `Update`: 每帧逻辑（输入处理、非物理移动等）");
            sb.AppendLine("- `LateUpdate`: 相机跟随、动画后处理等");
            sb.AppendLine();

            sb.AppendLine("### 常用组件速查");
            sb.AppendLine();
            sb.AppendLine("| 需求 | 组件 | 关键属性 |");
            sb.AppendLine("|------|------|----------|");
            sb.AppendLine("| 物理运动 | Rigidbody | mass, useGravity, isKinematic, constraints |");
            sb.AppendLine("| 碰撞检测 | BoxCollider / SphereCollider / CapsuleCollider | isTrigger, center, size |");
            sb.AppendLine("| 3D 渲染 | MeshRenderer + MeshFilter | materials, shadowCastingMode |");
            sb.AppendLine("| 2D 渲染 | SpriteRenderer | sprite, color, sortingOrder |");
            sb.AppendLine("| 音频播放 | AudioSource | clip, volume, loop, playOnAwake, spatialBlend |");
            sb.AppendLine("| 粒子效果 | ParticleSystem | startLifetime, startSpeed, maxParticles |");
            sb.AppendLine("| UI 文本 | TextMeshProUGUI | text, fontSize, color, alignment |");
            sb.AppendLine("| UI 按钮 | Button (+ Image) | onClick, interactable, transition |");
            sb.AppendLine("| UI 布局 | VerticalLayoutGroup / HorizontalLayoutGroup / GridLayoutGroup | spacing, padding, childAlignment |");
            sb.AppendLine("| 动画 | Animator | runtimeAnimatorController, parameters |");
            sb.AppendLine("| 导航 | NavMeshAgent | speed, stoppingDistance, destination |");
            sb.AppendLine("| 光照 | Light | type, intensity, color, range, shadows |");
            sb.AppendLine();

            sb.AppendLine("### 常见设计模式");
            sb.AppendLine();
            sb.AppendLine("- **单例模式**: 管理器类（AudioManager, GameManager）使用 `DontDestroyOnLoad` 跨场景持久化");
            sb.AppendLine("- **观察者模式**: 使用 C# event/Action 或 UnityEvent 解耦组件通信");
            sb.AppendLine("- **状态机**: 角色控制器、AI 行为、UI 流程管理");
            sb.AppendLine("- **对象池**: 频繁创建/销毁的对象（子弹、特效、敌人）使用池化复用");
            sb.AppendLine("- **ScriptableObject**: 数据驱动设计（配置表、技能数据、物品数据）");
            sb.AppendLine("- **命令模式**: 撤销/重做系统、输入缓冲");
            sb.AppendLine();

            sb.AppendLine("### 常见陷阱与解决方案");
            sb.AppendLine();
            sb.AppendLine("| 陷阱 | 解决方案 |");
            sb.AppendLine("|------|----------|");
            sb.AppendLine("| Update 中 GetComponent 每帧调用 | 在 Awake/Start 中缓存引用 |");
            sb.AppendLine("| string 比较 tag | 使用 CompareTag(\"tag\") |");
            sb.AppendLine("| Find 系列方法性能差 | 用序列化引用或事件系统替代 |");
            sb.AppendLine("| 协程中 new WaitForSeconds | 缓存 YieldInstruction 实例 |");
            sb.AppendLine("| Destroy 后立即访问 | 使用 null 检查或延迟到下一帧 |");
            sb.AppendLine("| 浮点数精度比较 | 使用 Mathf.Approximately() |");
            sb.AppendLine("| 跨场景引用丢失 | 使用 DontDestroyOnLoad 或 ScriptableObject |");
            sb.AppendLine("| Prefab 修改未保存 | 修改后调用 PrefabUtility.SavePrefabAsset |");
            sb.AppendLine("| 物理和渲染帧率不同步 | 物理逻辑放 FixedUpdate，视觉插值放 Update |");
            sb.AppendLine("| UI 事件穿透 | 使用 EventSystem.IsPointerOverGameObject() 检查 |");
            sb.AppendLine();

            sb.AppendLine("### 项目目录约定");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine("Assets/");
            sb.AppendLine("  Scripts/          # C# 脚本");
            sb.AppendLine("    Editor/         # 编辑器扩展脚本");
            sb.AppendLine("  Scenes/           # 场景文件");
            sb.AppendLine("  Prefabs/          # 预制体");
            sb.AppendLine("  Materials/        # 材质");
            sb.AppendLine("  Textures/         # 纹理");
            sb.AppendLine("  Models/           # 3D 模型");
            sb.AppendLine("  Animations/       # 动画资产");
            sb.AppendLine("  Audio/            # 音频文件");
            sb.AppendLine("  UI/               # UI 资产（图集、字体等）");
            sb.AppendLine("  Resources/        # 运行时动态加载资源（谨慎使用）");
            sb.AppendLine("  StreamingAssets/  # 原始文件流式加载");
            sb.AppendLine("  Plugins/          # 第三方插件");
            sb.AppendLine("  ScriptableObjects/ # SO 数据资产");
            sb.AppendLine("```");
            sb.AppendLine();

            // ===== 第四部分：架构说明（用户填写） =====
            sb.AppendLine("## 4. 项目架构");
            sb.AppendLine();
            sb.AppendLine("<!-- 在此描述项目的架构设计，Agent 会遵循这些约定 -->");
            sb.AppendLine("<!-- 例如：");
            sb.AppendLine("- 使用 MVC 架构分离数据、逻辑和表现");
            sb.AppendLine("- GameManager 单例管理全局状态");
            sb.AppendLine("- 事件系统使用 ScriptableObject 事件通道");
            sb.AppendLine("- UI 使用 MVVM 模式，ViewModel 继承 ScriptableObject");
            sb.AppendLine("-->");
            sb.AppendLine();

            // ===== 第五部分：开发规范（用户填写） =====
            sb.AppendLine("## 5. 开发规范");
            sb.AppendLine();
            sb.AppendLine("<!-- 在此描述团队的编码规范和约定，Agent 会严格遵循 -->");
            sb.AppendLine("<!-- 例如：");
            sb.AppendLine("- 所有 MonoBehaviour 必须有命名空间");
            sb.AppendLine("- 公共方法必须有 XML 文档注释");
            sb.AppendLine("- 禁止使用 GameObject.Find，必须通过序列化引用或依赖注入");
            sb.AppendLine("- 所有配置数据使用 ScriptableObject，不硬编码");
            sb.AppendLine("-->");
            sb.AppendLine();

            // ===== 第六部分：已知问题（用户填写） =====
            sb.AppendLine("## 6. 已知问题与注意事项");
            sb.AppendLine();
            sb.AppendLine("<!-- 记录项目中的已知问题、技术债务或特殊注意事项 -->");
            sb.AppendLine("<!-- Agent 在操作相关区域时会参考这些信息 -->");
            sb.AppendLine("<!-- 例如：");
            sb.AppendLine("- PlayerController.cs 中的移动逻辑需要重构，目前耦合了输入和物理");
            sb.AppendLine("- 旧版 UI 系统（UGUI）正在迁移到 UI Toolkit，新 UI 请使用 UI Toolkit");
            sb.AppendLine("- 第三方插件 XYZ 的 API 在 Unity 2022 中有兼容性问题");
            sb.AppendLine("-->");
            sb.AppendLine();

            return sb.ToString();
        }

        /// <summary>
        /// 绘制工具管理区域。
        /// 按分类显示所有已注册工具，支持按分类或单个工具启用/禁用。
        /// 禁用的工具不会发送给 LLM，从而减少 token 消耗并聚焦 Agent 能力。
        /// </summary>
        private void DrawToolManagementSection()
        {
            _toolManagementFoldout = EditorGUILayout.Foldout(_toolManagementFoldout, "Tool Management", true, EditorStyles.foldoutHeader);
            if (!_toolManagementFoldout) return;

            EditorGUI.indentLevel++;

            // 获取所有已注册工具
            var allTools = ToolRegistry.Instance.GetAllTools();
            if (allTools == null || allTools.Count == 0)
            {
                EditorGUILayout.HelpBox("暂无已注册的工具。工具将在 AgentLoop 初始化后可用。", MessageType.Info);
                EditorGUI.indentLevel--;
                return;
            }

            // 统计信息
            var totalCount = allTools.Count;
            var disabledToolCount = _settings.disabledTools?.Count ?? 0;
            var disabledCategoryCount = _settings.disabledToolCategories?.Count ?? 0;

            // 按分类分组
            var grouped = allTools
                .GroupBy(t => t.Metadata?.Category ?? "default")
                .OrderBy(g => g.Key)
                .ToList();

            var enabledCount = allTools.Count(t =>
                t.Metadata != null && !_settings.IsToolDisabled(t.Metadata.Name, t.Metadata.Category));

            EditorGUILayout.LabelField(
                $"已注册 {totalCount} 个工具，{enabledCount} 个启用，{totalCount - enabledCount} 个禁用",
                EditorStyles.miniLabel);

            EditorGUILayout.Space(3);

            // 快捷操作按钮
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(EditorGUI.indentLevel * 15);
            if (GUILayout.Button("全部启用", GUILayout.Width(80)))
            {
                _settings.disabledToolCategories.Clear();
                _settings.disabledTools.Clear();
                _settings.SaveSettings();
            }
            if (GUILayout.Button("全部禁用", GUILayout.Width(80)))
            {
                _settings.disabledToolCategories.Clear();
                _settings.disabledTools.Clear();
                foreach (var tool in allTools)
                {
                    if (tool.Metadata != null && !_settings.disabledTools.Contains(tool.Metadata.Name))
                        _settings.disabledTools.Add(tool.Metadata.Name);
                }
                _settings.SaveSettings();
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(3);

            // 按分类绘制工具列表
            foreach (var group in grouped)
            {
                var category = group.Key;
                var toolsInCategory = group.OrderBy(t => t.Metadata?.Name).ToList();

                if (!_categoryFoldouts.ContainsKey(category))
                    _categoryFoldouts[category] = false;

                // 检查分类是否被整体禁用
                bool categoryDisabled = _settings.disabledToolCategories != null &&
                                        _settings.disabledToolCategories.Contains(category);

                // 计算分类内启用的工具数
                int enabledInCategory = toolsInCategory.Count(t =>
                    t.Metadata != null && !_settings.IsToolDisabled(t.Metadata.Name, t.Metadata.Category));

                // 分类标题行
                EditorGUILayout.BeginHorizontal();

                _categoryFoldouts[category] = EditorGUILayout.Foldout(
                    _categoryFoldouts[category],
                    $"{category} ({enabledInCategory}/{toolsInCategory.Count})",
                    true);

                // 分类级别的启用/禁用 toggle
                EditorGUI.BeginChangeCheck();
                bool categoryEnabled = !categoryDisabled;
                categoryEnabled = EditorGUILayout.Toggle(categoryEnabled, GUILayout.Width(20));
                if (EditorGUI.EndChangeCheck())
                {
                    if (categoryEnabled)
                    {
                        // 启用分类：从禁用列表移除
                        _settings.disabledToolCategories.Remove(category);
                    }
                    else
                    {
                        // 禁用分类：添加到禁用列表
                        if (!_settings.disabledToolCategories.Contains(category))
                            _settings.disabledToolCategories.Add(category);
                    }
                    _settings.SaveSettings();
                }

                EditorGUILayout.EndHorizontal();

                // 展开时显示分类内的各个工具
                if (_categoryFoldouts[category])
                {
                    EditorGUI.indentLevel++;

                    if (categoryDisabled)
                    {
                        EditorGUILayout.HelpBox("此分类已被整体禁用。启用分类后可单独管理各工具。", MessageType.Info);
                    }

                    GUI.enabled = !categoryDisabled;

                    foreach (var tool in toolsInCategory)
                    {
                        var meta = tool.Metadata;
                        if (meta == null) continue;

                        bool toolDisabled = _settings.disabledTools != null &&
                                            _settings.disabledTools.Contains(meta.Name);

                        EditorGUI.BeginChangeCheck();
                        bool toolEnabled = !toolDisabled;
                        toolEnabled = EditorGUILayout.ToggleLeft(
                            new GUIContent(meta.Name, TruncateForTooltip(meta.Description, 200)),
                            toolEnabled);

                        if (EditorGUI.EndChangeCheck())
                        {
                            if (toolEnabled)
                            {
                                _settings.disabledTools.Remove(meta.Name);
                            }
                            else
                            {
                                if (!_settings.disabledTools.Contains(meta.Name))
                                    _settings.disabledTools.Add(meta.Name);
                            }
                            _settings.SaveSettings();
                        }
                    }

                    GUI.enabled = true;
                    EditorGUI.indentLevel--;
                }
            }

            EditorGUI.indentLevel--;
        }

        /// <summary>
        /// 截断文本用于 Tooltip 显示。
        /// </summary>
        /// <param name="text">原始文本</param>
        /// <param name="maxLength">最大长度</param>
        /// <returns>截断后的文本</returns>
        private static string TruncateForTooltip(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (text.Length <= maxLength) return text;
            return text.Substring(0, maxLength) + "...";
        }

        private void DrawUIPreferencesSection()
        {
            EditorGUILayout.LabelField("UI Preferences", EditorStyles.boldLabel);

            EditorGUI.indentLevel++;
            EditorGUI.BeginChangeCheck();

            _settings.streamingEnabled = EditorGUILayout.Toggle(
                new GUIContent("Streaming", "启用流式输出（逐字显示）"),
                _settings.streamingEnabled);

            _settings.showToolCallDetails = EditorGUILayout.Toggle(
                new GUIContent("Show Tool Details", "显示工具调用详情"),
                _settings.showToolCallDetails);

            if (EditorGUI.EndChangeCheck())
            {
                _settings.SaveSettings();
            }

            EditorGUI.indentLevel--;
        }

        /// <summary>
        /// 绘制 Memory Service - mem0 配置区域。
        /// B1: 重新组织 UI 布局，按操作顺序排列：
        ///   1. Enabled 开关
        ///   2. 服务连接（Endpoint + API Key + 测试连接）
        ///   3. 用户管理（User ID + 检测/创建）
        ///   4. 自动记忆设置
        /// </summary>
        private void DrawMemoryServiceSection()
        {
            EditorGUILayout.LabelField("Memory Service - mem0", EditorStyles.boldLabel);

            EditorGUI.indentLevel++;

            // 检测 Endpoint 变化以清除连接缓存
            var previousEndpoint = _lastTestedEndpoint;

            EditorGUI.BeginChangeCheck();

            _settings.mem0Enabled = EditorGUILayout.Toggle(
                new GUIContent("Enabled", "启用 mem0 记忆服务"),
                _settings.mem0Enabled);

            // ── 服务连接 ──
            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField("服务连接", EditorStyles.miniBoldLabel);

            _settings.mem0Endpoint = EditorGUILayout.TextField(
                new GUIContent("Endpoint URL", "mem0 服务端点地址"),
                _settings.mem0Endpoint);

            // Endpoint 变化时清除连接缓存
            if (_settings.mem0Endpoint != previousEndpoint && !string.IsNullOrEmpty(previousEndpoint))
            {
                InvalidateConnectionCache();
                _mem0TestResult = "";
                _mem0TestStatus = StatusLevel.None;
            }

            // mem0 API Key — 密码模式
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(new GUIContent("API Key", "mem0 服务的 API Key"));
            EditorGUILayout.LabelField(_mem0ApiKeyDisplay, GUILayout.Width(120));

            if (GUILayout.Button("Set", GUILayout.Width(40)))
            {
                var newKey = EditorInputDialog.Show("Set mem0 API Key", "Enter your mem0 API Key:", "");
                if (newKey != null)
                {
                    SecureKeyStorage.SetMem0ApiKey(newKey);
                    _mem0ApiKeyDisplay = string.IsNullOrEmpty(newKey) ? "(not set)" : "••••••••••••";
                    InvalidateConnectionCache(); // API Key 变化也清除缓存
                }
            }

            if (GUILayout.Button("Clear", GUILayout.Width(50)))
            {
                SecureKeyStorage.SetMem0ApiKey("");
                _mem0ApiKeyDisplay = "(not set)";
                InvalidateConnectionCache();
            }

            EditorGUILayout.EndHorizontal();

            // 测试连接按钮（紧跟在 Endpoint + API Key 之后）
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(EditorGUI.indentLevel * 15);

            GUI.enabled = !_isMem0Testing;
            if (GUILayout.Button(_isMem0Testing ? "测试中..." : "测试连接", GUILayout.Width(120)))
            {
                TestMem0Connection();
            }
            GUI.enabled = true;

            if (!string.IsNullOrEmpty(_mem0TestResult))
            {
                DrawStatusLabel(_mem0TestResult, _mem0TestStatus);
            }

            EditorGUILayout.EndHorizontal();

            // ── 用户管理 ──
            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField("用户管理", EditorStyles.miniBoldLabel);

            // User ID — 只读显示系统自动生成的 ID
            GUI.enabled = false;
            EditorGUILayout.TextField(
                new GUIContent("User ID", "系统自动生成的唯一用户标识（用于 mem0 记忆隔离）"),
                _settings.EffectiveUserId);
            GUI.enabled = true;

            // User ID 检测/创建按钮
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(EditorGUI.indentLevel * 15 + EditorGUIUtility.labelWidth + 2);

            GUI.enabled = !_isCheckingUserId && !_isCreatingUserId
                          && !string.IsNullOrWhiteSpace(_settings.mem0Endpoint);
            if (GUILayout.Button(_isCheckingUserId ? "检测中..." : "检测 ID", GUILayout.Width(80)))
            {
                CheckUserIdExists();
            }

            if (GUILayout.Button(_isCreatingUserId ? "创建中..." : "创建 ID", GUILayout.Width(80)))
            {
                CreateUserId();
            }
            GUI.enabled = true;

            EditorGUILayout.EndHorizontal();

            // 显示检测/创建结果
            if (!string.IsNullOrEmpty(_userIdCheckResult))
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(EditorGUI.indentLevel * 15 + EditorGUIUtility.labelWidth + 2);
                DrawStatusLabel(_userIdCheckResult, _userIdCheckStatus, miniLabel: true);
                EditorGUILayout.EndHorizontal();
            }

            // ── 自动记忆 ──
            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField("自动记忆", EditorStyles.miniBoldLabel);

            _settings.autoMemoryEnabled = EditorGUILayout.Toggle(
                new GUIContent("Auto Memory", "会话结束时自动提取关键信息存入 mem0"),
                _settings.autoMemoryEnabled);

            _settings.autoMemoryMinTurns = EditorGUILayout.IntSlider(
                new GUIContent("Min Turns", "触发自动记忆的最小用户对话轮次"),
                _settings.autoMemoryMinTurns, 1, 20);

            if (EditorGUI.EndChangeCheck())
            {
                _settings.SaveSettings();
            }

            EditorGUI.indentLevel--;
        }

        private void DrawKnowledgeBaseSection()
        {
            EditorGUILayout.LabelField("Knowledge Base - LightRAG", EditorStyles.boldLabel);

            EditorGUI.indentLevel++;
            EditorGUI.BeginChangeCheck();

            _settings.lightragEnabled = EditorGUILayout.Toggle(
                new GUIContent("Enabled", "启用 LightRAG 知识库"),
                _settings.lightragEnabled);

            _settings.lightragEndpoint = EditorGUILayout.TextField(
                new GUIContent("Endpoint URL", "LightRAG 服务端点地址"),
                _settings.lightragEndpoint);

            // LightRAG API Key — 密码模式
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(new GUIContent("API Key", "LightRAG 服务的 API Key"));
            EditorGUILayout.LabelField(_lightragApiKeyDisplay, GUILayout.Width(120));

            if (GUILayout.Button("Set", GUILayout.Width(40)))
            {
                var newKey = EditorInputDialog.Show("Set LightRAG API Key", "Enter your LightRAG API Key:", "");
                if (newKey != null)
                {
                    SecureKeyStorage.SetLightRAGApiKey(newKey);
                    _lightragApiKeyDisplay = string.IsNullOrEmpty(newKey) ? "(not set)" : "••••••••••••";
                }
            }

            if (GUILayout.Button("Clear", GUILayout.Width(50)))
            {
                SecureKeyStorage.SetLightRAGApiKey("");
                _lightragApiKeyDisplay = "(not set)";
            }

            EditorGUILayout.EndHorizontal();

            if (EditorGUI.EndChangeCheck())
            {
                _settings.SaveSettings();
            }

            // Test Connection 按钮
            EditorGUILayout.Space(5);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(EditorGUI.indentLevel * 15);

            GUI.enabled = !_isLightRAGTesting;
            if (GUILayout.Button(_isLightRAGTesting ? "测试中..." : "测试连接", GUILayout.Width(120)))
            {
                TestLightRAGConnection();
            }
            GUI.enabled = true;

            if (!string.IsNullOrEmpty(_lightragTestResult))
            {
                var style = new GUIStyle(EditorStyles.label);
                style.normal.textColor = _lightragTestResult.StartsWith("连接成功")
                    ? new Color(0.2f, 0.8f, 0.2f)
                    : Color.red;
                EditorGUILayout.LabelField(_lightragTestResult, style);
            }

            EditorGUILayout.EndHorizontal();

            EditorGUI.indentLevel--;
        }

        private void DrawAboutSection()
        {
            EditorGUILayout.LabelField("About", EditorStyles.boldLabel);

            EditorGUI.indentLevel++;
            EditorGUILayout.LabelField("Version", $"{GetPackageVersion()} (Phase 3)");
            EditorGUILayout.LabelField("Unity Agent Plugin", "通过自然语言对话驱动 Unity 开发工作流");
            EditorGUI.indentLevel--;
        }

        /// <summary>
        /// 从 UPM PackageInfo 或 package.json 读取版本号。
        /// 结果会被缓存，避免每帧重复读取。
        /// </summary>
        /// <returns>版本号字符串，如 "0.3.1"；读取失败时返回 "unknown"</returns>
        private static string GetPackageVersion()
        {
            if (!string.IsNullOrEmpty(_cachedVersion))
                return _cachedVersion;

            try
            {
                // 方式 1: 通过 UPM PackageInfo API（最可靠）
                var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(
                    typeof(AgentCoreSettingsProvider).Assembly);
                if (packageInfo != null)
                {
                    _cachedVersion = packageInfo.version;
                    return _cachedVersion;
                }

                // 方式 2: 回退 — 直接读取 package.json 文件
                var packagePath = "Packages/com.agentcore.unity/package.json";
                if (File.Exists(packagePath))
                {
                    var json = File.ReadAllText(packagePath);
                    var jobj = JsonHelper.ParseObject(json);
                    _cachedVersion = JsonHelper.GetString(jobj, "version", "unknown");
                    return _cachedVersion;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AgentCore] Failed to read package version: {ex.Message}");
            }

            _cachedVersion = "unknown";
            return _cachedVersion;
        }

        // ─────────────────────────────────────────────
        //  连接测试方法
        // ─────────────────────────────────────────────

        /// <summary>
        /// 从 LLM 服务的 /v1/models 端点获取可用模型列表。
        /// 获取成功后填充 _availableModels，供下拉菜单使用。
        /// </summary>
        private void FetchAvailableModels()
        {
            _isFetchingModels = true;
            _fetchModelsResult = "";

            AsyncHelper.RunAsync(async () =>
            {
                try
                {
                    var client = HttpClientFactory.GetClient();
                    var url = $"{_settings.llmEndpoint.TrimEnd('/')}/models";
                    var apiKey = SecureKeyStorage.GetLLMApiKey();

                    using var request = HttpClientFactory.CreateRequest(HttpMethod.Get, url, apiKey);
                    var response = await client.SendAsync(request);

                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        var models = ParseModelsFromJson(json);

                        AsyncHelper.RunOnMainThread(() =>
                        {
                            _availableModels = models;
                            _fetchModelsResult = models.Count > 0
                                ? $"[OK] 找到 {models.Count} 个模型"
                                : "[OK] 无可用模型";
                            _isFetchingModels = false;
                        });
                    }
                    else
                    {
                        AsyncHelper.RunOnMainThread(() =>
                        {
                            _fetchModelsResult = $"[FAIL] HTTP {(int)response.StatusCode}: {response.ReasonPhrase}";
                            _isFetchingModels = false;
                        });
                    }
                }
                catch (Exception ex)
                {
                    AsyncHelper.RunOnMainThread(() =>
                    {
                        _fetchModelsResult = $"[FAIL] {ex.Message}";
                        _isFetchingModels = false;
                    });
                }
            });
        }

        /// <summary>
        /// 解析 OpenAI /v1/models 响应 JSON，提取模型 ID 列表。
        /// 响应格式：{"object":"list","data":[{"id":"model-name",...},...]}
        /// </summary>
        private static List<string> ParseModelsFromJson(string json)
        {
            var models = new List<string>();
            try
            {
                var jobj = JsonHelper.ParseObject(json);
                if (jobj == null) return models;

                var data = jobj["data"] as Newtonsoft.Json.Linq.JArray;
                if (data == null) return models;

                foreach (var item in data)
                {
                    var id = item["id"]?.ToString();
                    if (!string.IsNullOrEmpty(id))
                        models.Add(id);
                }

                // 按字母排序，方便查找
                models.Sort(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AgentCore] Failed to parse models JSON: {ex.Message}");
            }
            return models;
        }

        private void TestLLMConnection()
        {
            _isTesting = true;
            _connectionTestResult = "";

            AsyncHelper.RunAsync(async () =>
            {
                try
                {
                    var client = HttpClientFactory.GetClient();
                    var url = $"{_settings.llmEndpoint.TrimEnd('/')}/models";
                    var apiKey = SecureKeyStorage.GetLLMApiKey();

                    using var request = HttpClientFactory.CreateRequest(HttpMethod.Get, url, apiKey);
                    var response = await client.SendAsync(request);

                    AsyncHelper.RunOnMainThread(() =>
                    {
                        if (response.IsSuccessStatusCode)
                        {
                            _connectionTestResult = "[OK] Connected";
                        }
                        else
                        {
                            _connectionTestResult = $"[FAIL] HTTP {(int)response.StatusCode}: {response.ReasonPhrase}";
                        }
                        _isTesting = false;
                    });
                }
                catch (Exception ex)
                {
                    AsyncHelper.RunOnMainThread(() =>
                    {
                        _connectionTestResult = $"[FAIL] {ex.Message}";
                        _isTesting = false;
                    });
                }
            });
        }

        private void TestMem0Connection()
        {
            _isMem0Testing = true;
            _mem0TestResult = "测试中...";
            _mem0TestStatus = StatusLevel.Loading;

            AsyncHelper.RunAsync(async () =>
            {
                try
                {
                    var client = new Mem0Client(
                        _settings.mem0Endpoint,
                        SecureKeyStorage.GetMem0ApiKey(),
                        _settings.EffectiveUserId
                    );

                    var (success, message) = await client.TestConnectionAsync();

                    AsyncHelper.RunOnMainThread(() =>
                    {
                        _mem0TestResult = message;
                        _mem0TestStatus = success ? StatusLevel.Success : StatusLevel.Error;
                        UpdateConnectionCache(success);
                        _isMem0Testing = false;
                    });
                }
                catch (Exception ex)
                {
                    AsyncHelper.RunOnMainThread(() =>
                    {
                        _mem0TestResult = $"连接失败: {ex.Message}";
                        _mem0TestStatus = StatusLevel.Error;
                        UpdateConnectionCache(false);
                        _isMem0Testing = false;
                    });
                }
            });
        }

        /// <summary>
        /// A3: 前置连通性检查。
        /// 在执行 CheckUserIdExists / CreateUserId 前先验证连接。
        /// 使用缓存避免重复检查。
        /// </summary>
        /// <param name="client">Mem0Client 实例</param>
        /// <returns>连接是否可用</returns>
        private async Task<bool> EnsureConnectionAsync(Mem0Client client)
        {
            // 如果缓存有效且连接成功，直接返回
            if (IsConnectionCacheValid() && _mem0ConnectionValid == true)
            {
                return true;
            }

            // 执行连通性检查
            var (success, message) = await client.TestConnectionAsync();

            AsyncHelper.RunOnMainThread(() =>
            {
                UpdateConnectionCache(success);
                if (success)
                {
                    _mem0TestResult = message;
                    _mem0TestStatus = StatusLevel.Success;
                }
                else
                {
                    _mem0TestResult = message;
                    _mem0TestStatus = StatusLevel.Error;
                }
            });

            return success;
        }

        private void CheckUserIdExists()
        {
            _isCheckingUserId = true;
            _userIdCheckResult = "检测中...";
            _userIdCheckStatus = StatusLevel.Loading;

            AsyncHelper.RunAsync(async () =>
            {
                try
                {
                    var client = new Mem0Client(
                        _settings.mem0Endpoint,
                        SecureKeyStorage.GetMem0ApiKey(),
                        _settings.EffectiveUserId
                    );

                    // A3: 前置连通性检查
                    var connected = await EnsureConnectionAsync(client);
                    if (!connected)
                    {
                        AsyncHelper.RunOnMainThread(() =>
                        {
                            _userIdCheckResult = "⚠ 无法连接到 mem0 服务，请先确认 Endpoint 正确并点击「测试连接」";
                            _userIdCheckStatus = StatusLevel.Error;
                            _isCheckingUserId = false;
                        });
                        return;
                    }

                    var (exists, message, status) = await client.CheckUserExistsAsync();

                    AsyncHelper.RunOnMainThread(() =>
                    {
                        _userIdCheckResult = message;
                        switch (status)
                        {
                            case Mem0ConnectionStatus.Connected:
                                _userIdCheckStatus = StatusLevel.Success;
                                break;
                            case Mem0ConnectionStatus.UserNotFound:
                                _userIdCheckStatus = StatusLevel.Warning;
                                break;
                            default:
                                _userIdCheckStatus = StatusLevel.Error;
                                break;
                        }
                        _isCheckingUserId = false;
                    });
                }
                catch (Exception ex)
                {
                    AsyncHelper.RunOnMainThread(() =>
                    {
                        _userIdCheckResult = $"检测失败: {ex.Message}";
                        _userIdCheckStatus = StatusLevel.Error;
                        _isCheckingUserId = false;
                    });
                }
            });
        }

        private void CreateUserId()
        {
            _isCreatingUserId = true;
            _userIdCheckResult = "正在创建用户...";
            _userIdCheckStatus = StatusLevel.Loading;

            AsyncHelper.RunAsync(async () =>
            {
                try
                {
                    var client = new Mem0Client(
                        _settings.mem0Endpoint,
                        SecureKeyStorage.GetMem0ApiKey(),
                        _settings.EffectiveUserId
                    );

                    // A3: 前置连通性检查
                    var connected = await EnsureConnectionAsync(client);
                    if (!connected)
                    {
                        AsyncHelper.RunOnMainThread(() =>
                        {
                            _userIdCheckResult = "⚠ 无法连接到 mem0 服务，请先确认 Endpoint 正确并点击「测试连接」";
                            _userIdCheckStatus = StatusLevel.Error;
                            _isCreatingUserId = false;
                        });
                        return;
                    }

                    // C1: 使用新的 CreateUserAsync（优先 REST，回退 MCP SSE）
                    var (success, message) = await client.CreateUserAsync();

                    AsyncHelper.RunOnMainThread(() =>
                    {
                        _userIdCheckResult = message;
                        _userIdCheckStatus = success ? StatusLevel.Success : StatusLevel.Error;
                        _isCreatingUserId = false;
                    });
                }
                catch (Exception ex)
                {
                    AsyncHelper.RunOnMainThread(() =>
                    {
                        _userIdCheckResult = $"创建失败: {ex.Message}";
                        _userIdCheckStatus = StatusLevel.Error;
                        _isCreatingUserId = false;
                    });
                }
            });
        }

        private void TestLightRAGConnection()
        {
            _isLightRAGTesting = true;
            _lightragTestResult = "";

            AsyncHelper.RunAsync(async () =>
            {
                try
                {
                    var client = new LightRAGClient(
                        _settings.lightragEndpoint,
                        SecureKeyStorage.GetLightRAGApiKey()
                    );

                    var success = await client.TestConnectionAsync();

                    AsyncHelper.RunOnMainThread(() =>
                    {
                        _lightragTestResult = success ? "连接成功" : "连接失败: 服务未响应或不健康";
                        _isLightRAGTesting = false;
                    });
                }
                catch (Exception ex)
                {
                    AsyncHelper.RunOnMainThread(() =>
                    {
                        _lightragTestResult = $"连接失败: {ex.Message}";
                        _isLightRAGTesting = false;
                    });
                }
            });
        }
    }

    /// <summary>
    /// 简单的 Editor 输入对话框。
    /// 用于安全输入 API Key 等敏感信息。
    /// </summary>
    public class EditorInputDialog : EditorWindow
    {
        private string _title;
        private string _message;
        private string _inputValue;
        private bool _confirmed;
        private bool _closed;
        private static string _result;

        /// <summary>
        /// 显示输入对话框并返回用户输入。
        /// 如果用户取消则返回 null。
        /// </summary>
        public static string Show(string title, string message, string defaultValue = "")
        {
            _result = null;
            var window = CreateInstance<EditorInputDialog>();
            window._title = title;
            window._message = message;
            window._inputValue = defaultValue;
            window.titleContent = new GUIContent(title);
            window.minSize = new Vector2(350, 130);
            window.maxSize = new Vector2(350, 130);
            window.ShowModalUtility();
            return _result;
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField(_message);
            EditorGUILayout.Space(5);

            _inputValue = EditorGUILayout.PasswordField(_inputValue);

            EditorGUILayout.Space(10);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            if (GUILayout.Button("OK", GUILayout.Width(80)))
            {
                _result = _inputValue;
                Close();
            }

            if (GUILayout.Button("Cancel", GUILayout.Width(80)))
            {
                _result = null;
                Close();
            }

            EditorGUILayout.EndHorizontal();
        }
    }
}
