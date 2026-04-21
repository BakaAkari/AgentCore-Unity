using System.Net.Http;
using System.Threading.Tasks;
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

        private AgentCoreSettingsProvider(string path, SettingsScope scope)
            : base(path, scope) { }

        [SettingsProvider]
        public static SettingsProvider CreateSettingsProvider()
        {
            return new AgentCoreSettingsProvider("Project/AgentCore", SettingsScope.Project)
            {
                label = "AgentCore",
                keywords = new[] { "agent", "ai", "llm", "chat", "agentcore" }
            };
        }

        public override void OnActivate(string searchContext, VisualElement rootElement)
        {
            _settings = AgentCoreSettings.instance;
            _apiKeyDisplay = SecureKeyStorage.HasLLMApiKey() ? "••••••••••••" : "(not set)";
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

            // === Memory Service (Phase 3 预留) ===
            DrawMemoryServiceSection();

            EditorGUILayout.Space(10);

            // === Knowledge Base (Phase 3 预留) ===
            DrawKnowledgeBaseSection();

            EditorGUILayout.Space(20);

            // === About ===
            DrawAboutSection();
        }

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

            _settings.contextWindowTokens = EditorGUILayout.IntField(
                new GUIContent("Context Window (tokens)", "上下文窗口 token 上限"),
                _settings.contextWindowTokens);
            _settings.contextWindowTokens = Mathf.Clamp(_settings.contextWindowTokens, 1000, 200000);

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

        private void DrawMemoryServiceSection()
        {
            EditorGUILayout.LabelField("Memory Service - mem0 (Phase 3)", EditorStyles.boldLabel);

            EditorGUI.indentLevel++;
            GUI.enabled = false;
            EditorGUILayout.Toggle("Enabled", _settings.mem0Enabled);
            EditorGUILayout.TextField("Endpoint", _settings.mem0Endpoint);
            GUI.enabled = true;
            EditorGUILayout.HelpBox("mem0 记忆服务将在 Phase 3 中实现。", MessageType.Info);
            EditorGUI.indentLevel--;
        }

        private void DrawKnowledgeBaseSection()
        {
            EditorGUILayout.LabelField("Knowledge Base - LightRAG (Phase 3)", EditorStyles.boldLabel);

            EditorGUI.indentLevel++;
            GUI.enabled = false;
            EditorGUILayout.Toggle("Enabled", _settings.lightragEnabled);
            EditorGUILayout.TextField("Endpoint", _settings.lightragEndpoint);
            GUI.enabled = true;
            EditorGUILayout.HelpBox("LightRAG 知识库将在 Phase 3 中实现。", MessageType.Info);
            EditorGUI.indentLevel--;
        }

        private void DrawAboutSection()
        {
            EditorGUILayout.LabelField("About", EditorStyles.boldLabel);

            EditorGUI.indentLevel++;
            EditorGUILayout.LabelField("Version", "0.1.0 (Phase 1)");
            EditorGUILayout.LabelField("Unity Agent Plugin", "通过自然语言对话驱动 Unity 开发工作流");
            EditorGUI.indentLevel--;
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
                catch (System.Exception ex)
                {
                    AsyncHelper.RunOnMainThread(() =>
                    {
                        _connectionTestResult = $"[FAIL] {ex.Message}";
                        _isTesting = false;
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
