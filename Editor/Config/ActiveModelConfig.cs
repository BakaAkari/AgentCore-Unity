using System;

namespace AgentCore.Editor.Config
{
    /// <summary>
    /// 当前有效 LLM 配置的统一解析入口（v1.13.0）。
    /// <para>
    /// 单一真源 = active <see cref="ProviderProfile"/>。endpoint / modelName / apiKey 一律取自
    /// active profile；若无 active profile 则抛 <see cref="InvalidOperationException"/>——不再有
    /// legacy fallthrough 路径。可选参数（temperature / maxTokens / reasoning* / extraRequestBody）
    /// 在 profile 未 override 时 fallthrough 到 <see cref="AgentCoreSettings"/> 全局默认（这些全局
    /// 字段仍存在，作为 profile 的默认值来源）。
    /// </para>
    /// <para>
    /// 所有属性每次访问都实时查询（不缓存），因为 settings / profiles 可能被 UI 随时修改。
    /// </para>
    /// </summary>
    public static class ActiveModelConfig
    {
        private const string NoProfileMessage =
            "No active Provider Profile. Configure one in Project Settings > AgentCore > Model & Agent.";

        /// <summary>当前 active profile；无 active 返回 null。</summary>
        public static ProviderProfile ActiveProfile
            => AgentCoreProviderProfiles.instance.GetActive();

        /// <summary>是否存在 active profile（供 UI 检测状态；运行时读 Endpoint/ModelName 无 profile 会抛异常）。</summary>
        public static bool IsUsingProfile
            => AgentCoreProviderProfiles.instance.GetActive() != null;

        /// <summary>当前 endpoint（base URL）。无 active profile 时抛 <see cref="InvalidOperationException"/>。</summary>
        public static string Endpoint
            => ActiveProfile?.endpoint ?? throw new InvalidOperationException(NoProfileMessage);

        /// <summary>当前 model name。无 active profile 时抛 <see cref="InvalidOperationException"/>。</summary>
        public static string ModelName
            => ActiveProfile?.modelName ?? throw new InvalidOperationException(NoProfileMessage);

        /// <summary>
        /// 当前 API key。有 active profile 时按 profileId 从 <see cref="SecureKeyStorage"/> 取
        /// （允许空字符串，不抛异常）；无 active profile 时抛 <see cref="InvalidOperationException"/>。
        /// </summary>
        public static string ApiKey
        {
            get
            {
                var p = ActiveProfile;
                if (p == null)
                    throw new InvalidOperationException(NoProfileMessage);
                return SecureKeyStorage.GetProfileApiKey(p.id);
            }
        }

        /// <summary>当前 temperature。Profile 未 override 则用 <see cref="AgentCoreSettings.temperature"/>。</summary>
        public static float Temperature
        {
            get
            {
                var p = ActiveProfile;
                return (p != null && p.overrideTemperature) ? p.temperature : AgentCoreSettings.instance.temperature;
            }
        }

        /// <summary>当前 maxTokens。Profile 未 override 则用 <see cref="AgentCoreSettings.maxTokens"/>。</summary>
        public static int MaxTokens
        {
            get
            {
                var p = ActiveProfile;
                return (p != null && p.overrideMaxTokens) ? p.maxTokens : AgentCoreSettings.instance.maxTokens;
            }
        }

        /// <summary>当前 reasoning effort。Profile 未 override reasoning 则用 <see cref="AgentCoreSettings.reasoningEffort"/>。</summary>
        public static string ReasoningEffort
        {
            get
            {
                var p = ActiveProfile;
                return (p != null && p.overrideReasoning) ? p.reasoningEffort : AgentCoreSettings.instance.reasoningEffort;
            }
        }

        /// <summary>当前 reasoning max tokens。Profile 未 override reasoning 则用 <see cref="AgentCoreSettings.reasoningMaxTokens"/>。</summary>
        public static int ReasoningMaxTokens
        {
            get
            {
                var p = ActiveProfile;
                return (p != null && p.overrideReasoning) ? p.reasoningMaxTokens : AgentCoreSettings.instance.reasoningMaxTokens;
            }
        }

        /// <summary>是否请求 reasoning 输出。Profile 未 override reasoning 则用 <see cref="AgentCoreSettings.enableReasoningOutput"/>。</summary>
        public static bool EnableReasoningOutput
        {
            get
            {
                var p = ActiveProfile;
                return (p != null && p.overrideReasoning) ? p.enableReasoningOutput : AgentCoreSettings.instance.enableReasoningOutput;
            }
        }

        /// <summary>当前 extraRequestBody（追加 JSON）。无 override 位——profile 提供非空值即用之，
        /// 否则 fallthrough 到 <see cref="AgentCoreSettings.extraRequestBody"/>。
        /// </summary>
        public static string ExtraRequestBody
        {
            get
            {
                var p = ActiveProfile;
                if (p != null && !string.IsNullOrEmpty(p.extraRequestBody))
                    return p.extraRequestBody;
                return AgentCoreSettings.instance.extraRequestBody;
            }
        }

        /// <summary>
        /// 用户友好的当前配置描述，用于 UI 显示。返回 active profile 的 displayName，
        /// 无 active profile 时返回 "(no active profile)"。
        /// </summary>
        public static string GetActiveDisplayName()
        {
            var p = ActiveProfile;
            if (p == null)
                return "(no active profile)";
            return string.IsNullOrEmpty(p.displayName) ? "(unnamed)" : p.displayName;
        }
    }
}
