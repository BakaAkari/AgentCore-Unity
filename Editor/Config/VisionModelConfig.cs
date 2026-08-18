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
        // v1.15.0: 视觉模型在「从未配置」（三字段全为初始状态）时，自动填入默认内网视觉端点，
        // 并异步 fetch 模型列表取第一个作为 visionModel（仿 AgentCoreProviderProfiles 的
        // TryAutoSelectFirstModelAsync）。用户一旦改过任一字段即不再自动覆盖（可控、不强制）。
        // 该地址仅企业内网可达，与主模型默认 8000 同一内网 vLLM 栈。
        internal const string DefaultEndpoint = "http://172.16.248.60:8001/v1";

        /// <summary>
        /// 视觉默认模型名（v1.15.x 规范化）。首次自动填充时优先从服务端模型列表选取匹配本名的模型
        /// （忽略大小写，避免服务端返回小写别名）并写入 visionModel，用户无需进设置刷新。
        /// </summary>
        internal const string DefaultModel = "GLM-4.6V-Flash";

        /// <summary>视觉模型是否启用（总开关）。未启用 = vision_analyze 工具不暴露给 agent。</summary>
        public static bool IsEnabled
            => AgentCoreSettings.instance != null && AgentCoreSettings.instance.visionEnabled;

        /// <summary>
        /// 用户是否已「显式配置过」视觉模型——任一字段（enabled / endpoint / model）脱离初始状态即视为已配置。
        /// 判定依据：一旦用户动过任何一项，自动默认填充就不再覆盖（对齐主模型「profile 存在即不重建」的幂等精神）。
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
        /// 视觉「从未配置」时自动填充默认值：填默认 endpoint，并异步 fetch 模型列表取第一个填入 modelName。
        /// <para>
        /// 对齐主模型 <see cref="AgentCoreProviderProfiles.EnsureDefaultProfileWithAutoModelAsync"/> 的幂等设计：
        /// 仅当 <see cref="IsUserConfigured"/> 为 false（三字段全初始）时才动手。用户改过任何一项 → 直接返回，
        /// 永不覆盖用户已有配置。静默失败：fetch 失败只记 Warning，不抛异常、不阻塞初始化。
        /// </para>
        /// <para>
        /// 专供运行时初始化路径（<c>AgentLoop.InitializeAsync</c>）调用；Settings 面板的视觉卡片自带
        /// Refresh Models / 手动输入，不重复触发本方法以避免重复 fetch。
        /// </para>
        /// </summary>
        public static async Task EnsureDefaultWithAutoModelAsync()
        {
            // 用户已配置过任何一项 → 不覆盖（幂等）。
            if (IsUserConfigured)
                return;

            var settings = AgentCoreSettings.instance;
            if (settings == null)
                return;

            // 默认值直接写死为规范大写（GLM-4.6V-Flash），不依赖服务端列表/大小写，避免产生小写错值。
            // 填默认 endpoint（同步，主线程安全：字段直接写 + SaveSettings）。
            // enabled 保持 false（不强制启用），只补 endpoint/model，由用户决定是否开启视觉——「不强制」。
            bool changed = false;
            if (string.IsNullOrWhiteSpace(settings.visionEndpoint))
            {
                settings.visionEndpoint = DefaultEndpoint;
                changed = true;
            }
            if (string.IsNullOrWhiteSpace(settings.visionModel))
            {
                settings.visionModel = DefaultModel;
                changed = true;
            }
            if (changed)
            {
                settings.SaveSettings();
                AgentCoreLog.Info($"[AgentCore] Vision: auto-filled default endpoint {DefaultEndpoint} + model {DefaultModel} (never configured).");
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
