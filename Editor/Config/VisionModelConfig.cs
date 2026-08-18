using System;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Config.Settings;
using AgentCore.Editor.Utils;

namespace AgentCore.Editor.Config
{
    /// <summary>
    /// 视觉模型配置的统一解析入口（v1.15.0）。
    /// <para>
    /// 视觉模型是<b>固定的单一配置</b>（不可热切换），与主模型的 multi-profile 体系完全隔离：
    /// endpoint / model 存于 <see cref="AgentCoreSettings"/>（3 个字段），apiKey 存于
    /// <see cref="SecureKeyStorage"/>（固定单键 <c>VISION_API_KEY</c>）。
    /// </para>
    /// <para>
    /// 与主模型 <see cref="ActiveModelConfig"/> 的区别：这里<b>不抛异常</b>，未配置时返回空串 / false，
    /// 由调用方（vision_analyze 工具）做 fail-closed 判断（未配置 → 明确提示配置，而非静默失败）。
    /// 主模型未配置是有 profile 体系的硬错误；视觉模型未配置是可选的软状态。
    /// </para>
    /// </summary>
    public static class VisionModelConfig
    {
        // ── Vision 默认配置硬编码值（对齐主模型 DefaultProfile* 的「唯一真源」写法）──
        // v1.15.0: 视觉模型在「从未配置」时自动填入默认内网视觉端点，并异步 fetch 模型列表
        // 取第一个作为 visionModel（仿 AgentCoreProviderProfiles 的 TryAutoSelectFirstModelAsync）。
        // 该地址仅企业内网可达，与主模型默认 8000 同一内网 vLLM 栈。
        // 模型名不写死：由 baseURL 的 /models 列表取第一个 id 原样使用（单一真源=服务端列表）。
        internal const string DefaultEndpoint = "http://172.16.248.60:8001/v1";

        /// <summary>视觉模型是否启用（总开关）。未启用 = vision_analyze 工具不暴露给 agent。</summary>
        public static bool IsEnabled
            => AgentCoreSettings.instance != null && AgentCoreSettings.instance.visionEnabled;

        /// <summary>
        /// 用户是否已「显式配置过」视觉模型——任一字段（enabled / endpoint / model）脱离初始状态即视为已配置。
        /// <para>
        /// 判定依据：一旦用户动过任何一项，自动默认填充就不再覆盖（对齐主模型「profile 存在即不重建」的幂等精神）。
        /// <b>v1.15.2 起仅用于把关 endpoint 的自动填充</b>（空 endpoint 且从未配置过才填默认）；
        /// model 的自动选取不归此 gate 管——model 只由「为空」触发，与 enabled 无关（否则启用视觉会阻断自动选模型）。
        /// </para>
        /// </summary>
        public static bool IsUserConfigured
            => AgentCoreSettings.instance != null
               && (AgentCoreSettings.instance.visionEnabled
                   || !string.IsNullOrWhiteSpace(AgentCoreSettings.instance.visionEndpoint)
                   || !string.IsNullOrWhiteSpace(AgentCoreSettings.instance.visionModel));

        /// <summary>视觉模型是否已完整配置（启用 + endpoint + model 非空）。apiKey 可空（部分本地服务无需 key）。</summary>
        public static bool IsConfigured
            => IsEnabled
               && !string.IsNullOrWhiteSpace(Endpoint)
               && !string.IsNullOrWhiteSpace(ModelName);

        /// <summary>视觉模型 endpoint（base URL）。未启用/未配置返回空串。</summary>
        public static string Endpoint
            => AgentCoreSettings.instance?.visionEndpoint?.Trim() ?? "";

        /// <summary>视觉模型名。未启用/未配置返回空串。</summary>
        public static string ModelName
            => AgentCoreSettings.instance?.visionModel?.Trim() ?? "";

        /// <summary>视觉模型 API Key（固定单键，与主模型 profile key 隔离）。可为空（本地无鉴权服务）。</summary>
        public static string ApiKey
            => SecureKeyStorage.GetVisionApiKey();

        /// <summary>
        /// 视觉「model 未配置」时自动填充默认值：填默认 endpoint（若空），并异步 fetch
        /// baseURL 的可用模型列表，<b>取第一个</b>填入 visionModel。用户要的是"取服务端第一个",
        /// 不写死任何模型名。
        /// <para>
        /// 幂等设计：仅当 <see cref="ModelName"/> 为空时才 fetch（填上后下次不再重复）；endpoint
        /// 仅在为空时填默认。用户已填的 model/endpoint 永不覆盖。
        /// </para>
        /// <para>
        /// 注意（v1.15.2 修复的坑）：旧版用 <see cref="IsUserConfigured"/> 做 gate，而该判定含
        /// <c>visionEnabled</c> —— 用户一旦勾选 Enable，IsUserConfigured 变 true → 整个自动 fetch 被
        /// 跳过 → 视觉启用时 model 永远空白。现改为<b>仅以 model 为空为触发条件</b>，与 enabled 无关：
        /// 视觉启用后若 model 空，仍会取第一个补上。
        /// </para>
        /// <para>
        /// 专供运行时初始化路径（<c>AgentLoop.InitializeAsync</c>）与设置页 Enable 切换时调用；
        /// Settings 面板的视觉卡片自带 Refresh Models / 手动输入，不重复触发本方法以避免重复 fetch。
        /// </para>
        /// </summary>
        public static async Task EnsureDefaultWithAutoModelAsync()
        {
            var settings = AgentCoreSettings.instance;
            if (settings == null)
                return;

            // endpoint 为空且用户从未配置过 endpoint → 填默认内网视觉端点（不强制启用）。
            // 注意：这里 endpoint 的"是否覆盖"用 IsUserConfigured 判定，避免覆盖用户自定义 endpoint；
            // 但 model 的自动选取不归此 gate 管（见下，model 只由"为空"触发）。
            if (string.IsNullOrWhiteSpace(settings.visionEndpoint) && !IsUserConfigured)
            {
                settings.visionEndpoint = DefaultEndpoint;
                settings.SaveSettings();
                AgentCoreLog.Info($"[AgentCore] Vision: auto-filled default endpoint {DefaultEndpoint} (never configured).");
            }

            // model 为空 → fetch baseURL 列表取第一个（核心需求；与 enabled 无关）。
            if (string.IsNullOrWhiteSpace(settings.visionModel))
            {
                await TryAutoSelectFirstVisionModelAsync();
            }
        }

        /// <summary>
        /// 异步 fetch 指定 endpoint 的可用模型列表，取第一个填入 visionModel（仅当仍为空时）。
        /// <para>
        /// 选取规则：取 <c>models[0]</c>，原样使用，不做任何大小写改写——以服务端 /models 列表为单一真源
        /// （vLLM 的 model 名匹配大小写敏感，列表返回的 id 即服务端能接受的名字）。
        /// 对齐主模型 <see cref="AgentCoreProviderProfiles"/> 的 TryAutoSelectFirstModelAsync 模式。
        /// </para>
        /// 静默失败：fetch 失败只记 Warning，不抛异常、不阻塞调用方。
        /// </summary>
        private static async Task TryAutoSelectFirstVisionModelAsync()
        {
            try
            {
                var settings = AgentCoreSettings.instance;
                if (settings == null)
                    return;

                var endpoint = settings.visionEndpoint?.Trim();
                if (string.IsNullOrWhiteSpace(endpoint))
                    return;

                var service = new ModelSettingsService();
                var models = await service.FetchModelsAsync(endpoint, SecureKeyStorage.GetVisionApiKey());
                if (models == null || models.Count == 0)
                {
                    AgentCoreLog.Info("[AgentCore] Vision: endpoint reachable but returned no models; model left empty.");
                    return;
                }

                // 选取 = 服务端 /models 列表第一个 id，原样使用，不做任何大小写改写。
                // （vLLM 的 model 名匹配是大小写敏感的；/models 返回的 id 就是服务端能接受的名字，
                //  信它即黄金标准。统一小写/规范化写死均会制造 404，主模型 DS 一直这么走从未出错。）
                string picked = models[0];

                AsyncHelper.RunOnMainThread(() =>
                {
                    var current = AgentCoreSettings.instance;
                    // 仅当仍为空时写入；若等待期间用户已手动填了 model，则不覆盖。
                    if (current != null && string.IsNullOrWhiteSpace(current.visionModel))
                    {
                        current.visionModel = picked;
                        current.SaveSettings();
                        // modelsList 在插值表达式外先算好，避免在插值字符串里嵌套带双引号的字符串字面量
                        // （C# 插值表达式内引号不按外层字符串转义规则，提取变量最清晰可靠）。
                        string modelsList = string.Join(", ", models);
                        AgentCoreLog.Info($"[AgentCore] Vision: auto-selected model '{picked}' at {endpoint} (list: [{modelsList}]).");
                    }
                });
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore] Vision auto-select first model failed: {ex.Message}");
            }
        }

        /// <summary>用户友好的当前视觉模型配置描述，用于 UI / 日志。</summary>
        public static string Describe()
        {
            if (!IsEnabled)
                return "(vision disabled)";
            if (string.IsNullOrEmpty(ModelName))
                return $"(vision enabled, model unset) {Endpoint}";
            return $"{ModelName} @ {Endpoint}";
        }
    }
}
