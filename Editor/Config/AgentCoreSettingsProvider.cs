using System;
using System.Net.Http;
using System.Threading.Tasks;
using AgentCore.Editor.Cloud;
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
        private AgentCoreSettings _settings;
        private string _apiKeyDisplay = "";
        private string _connectionTestResult = "";
        private bool _isTesting = false;

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

            _settings.llmModel = EditorGUILayout.TextField(
                new GUIContent("Model", "LLM 模型名称"),
                _settings.llmModel);

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

            EditorGUI.indentLevel--;
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

            // User ID
            _settings.userId = EditorGUILayout.TextField(
                new GUIContent("User ID", "用户 ID（用于 mem0 记忆隔离）"),
                _settings.userId);

            // User ID 检测/创建按钮
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(EditorGUI.indentLevel * 15 + EditorGUIUtility.labelWidth + 2);

            GUI.enabled = !_isCheckingUserId && !_isCreatingUserId
                          && !string.IsNullOrEmpty(_settings.userId)
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
            EditorGUILayout.LabelField("Version", "0.3.0 (Phase 3)");
            EditorGUILayout.LabelField("Unity Agent Plugin", "通过自然语言对话驱动 Unity 开发工作流");
            EditorGUI.indentLevel--;
        }

        // ─────────────────────────────────────────────
        //  连接测试方法
        // ─────────────────────────────────────────────

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
                        _settings.userId
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
                        _settings.userId
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
                        _settings.userId
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
