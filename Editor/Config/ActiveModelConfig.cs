using System;
using System.Threading;

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

        private static readonly AsyncLocal<ProviderProfile> ScopedProfile = new();

        /// <summary>
        /// 当前 active profile；无 active 返回 null。
        /// v1.17.0: 若当前异步上下文处于 <see cref="UseProfile"/> 作用域内，返回作用域 profile
        /// （utility profile / fallback 链用它让同一个客户端临时改用另一套连接参数，而不改全局 active）。
        /// </summary>
        public static ProviderProfile ActiveProfile
            => ScopedProfile.Value ?? AgentCoreProviderProfiles.instance.GetActive();

        /// <summary>是否存在 active profile（供 UI 检测状态；运行时读 Endpoint/ModelName 无 profile 会抛异常）。</summary>
        public static bool IsUsingProfile
            => ActiveProfile != null;

        /// <summary>
        /// 在当前异步调用链内临时把 <see cref="ActiveProfile"/> 替换为 <paramref name="profile"/>（v1.17.0）。
        /// 基于 <see cref="AsyncLocal{T}"/>，不影响其他并发调用链；<c>Dispose</c> 恢复原值。
        /// <paramref name="profile"/> 为 null 时不做任何替换。
        /// </summary>
        public static IDisposable UseProfile(ProviderProfile profile)
        {
            var previous = ScopedProfile.Value;
            if (profile != null) ScopedProfile.Value = profile;
            return new Scope(previous);
        }

        private sealed class Scope : IDisposable
        {
            private readonly ProviderProfile _previous;
            private bool _disposed;
            public Scope(ProviderProfile previous) { _previous = previous; }
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                ScopedProfile.Value = _previous;
            }
        }

        /// <summary>
        /// 解析后的线协议（<see cref="ApiFormats.OpenAI"/> 或 <see cref="ApiFormats.Anthropic"/>，v1.17.0）。
        /// <c>auto</c>：优先使用已验证协议，已知地址按主机识别；未知网关在 RoutingLLMClient 发请求前探测。
        /// </summary>
        public static string ApiFormat => ResolveApiFormat(ActiveProfile);

        /// <summary>解析后的 OpenAI 方言（v1.17.0），见 <see cref="ResolveDialect"/>。</summary>
        public static string Dialect => ResolveDialect(ActiveProfile);

        /// <summary>Anthropic 缓存 TTL 是否为 1 小时（v1.17.0）。</summary>
        public static bool CacheTtl1h
            => string.Equals(ActiveProfile?.cacheTtl?.Trim(), "1h", StringComparison.OrdinalIgnoreCase);

        /// <summary>解析后的鉴权方案（<see cref="AuthSchemes.Bearer"/> 或 <see cref="AuthSchemes.XApiKey"/>，v1.17.0）。</summary>
        public static string AuthScheme => ResolveAuthScheme(ActiveProfile);

        /// <summary>纯函数：按 profile 字段 + endpoint 推断线协议。</summary>
        public static string ResolveApiFormat(ProviderProfile p)
        {
            var v = p?.apiFormat?.Trim().ToLowerInvariant();
            if (v == ApiFormats.OpenAI || v == ApiFormats.Anthropic) return v;
            if (AgentCore.Editor.LLM.GatewayDiscovery.IsVerified(p)) return p.verifiedFormat;
            return HostContains(p?.endpoint, "api.anthropic.com") ? ApiFormats.Anthropic : ApiFormats.OpenAI;
        }

        /// <summary>纯函数：按 profile 字段 + endpoint 主机推断 OpenAI 方言。</summary>
        public static string ResolveDialect(ProviderProfile p)
        {
            var v = p?.dialect?.Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(v) && v != OpenAIDialects.Auto && Array.IndexOf(OpenAIDialects.All, v) >= 0) return v;
            var ep = p?.endpoint;
            if (HostContains(ep, "api.openai.com")) return OpenAIDialects.OpenAI;
            if (HostContains(ep, "deepseek.com")) return OpenAIDialects.DeepSeek;
            if (HostContains(ep, "openrouter.ai")) return OpenAIDialects.OpenRouter;
            return OpenAIDialects.Generic;
        }

        /// <summary>纯函数：解析鉴权方案（auto 时 anthropic 原生协议用 x-api-key）。</summary>
        public static string ResolveAuthScheme(ProviderProfile p)
        {
            var v = p?.authScheme?.Trim().ToLowerInvariant();
            if (v == AuthSchemes.Bearer || v == AuthSchemes.XApiKey) return v;
            if (AgentCore.Editor.LLM.GatewayDiscovery.IsVerified(p)) return p.verifiedAuth;
            return ResolveApiFormat(p) == ApiFormats.Anthropic ? AuthSchemes.XApiKey : AuthSchemes.Bearer;
        }

        private static bool HostContains(string endpoint, string fragment)
        {
            if (string.IsNullOrEmpty(endpoint)) return false;
            if (Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri))
                return uri.Host.Equals(fragment, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + fragment, StringComparison.OrdinalIgnoreCase);
            return false;
        }

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
