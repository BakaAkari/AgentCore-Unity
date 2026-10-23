using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Config;
using AgentCore.Editor.LLM.Anthropic;

namespace AgentCore.Editor.LLM
{
    /// <summary>
    /// 按当前 profile 的线协议分派的 <see cref="ILLMClient"/>（v1.17.0）。
    /// <para>
    /// 每次调用时读取 <see cref="ActiveModelConfig.ApiFormat"/>：<c>anthropic</c> → <see cref="AnthropicMessagesClient"/>，
    /// 其余 → <see cref="OpenAICompatibleClient"/>。因为是"每次调用"决策，用户在设置里切换 profile 或
    /// <see cref="ActiveModelConfig.UseProfile"/> 作用域生效后无需重建客户端。所有原先 <c>new OpenAICompatibleClient()</c>
    /// 的调用点改为构造本类。
    /// </para>
    /// </summary>
    public class RoutingLLMClient : ILLMClient
    {
        private readonly ILLMClient _openAI;
        private readonly ILLMClient _anthropic;

        private readonly bool _discover;
        public RoutingLLMClient() : this(new OpenAICompatibleClient(), new AnthropicMessagesClient()) { _discover = true; }

        /// <summary>测试用：注入两个底层客户端。</summary>
        public RoutingLLMClient(ILLMClient openAI, ILLMClient anthropic)
        {
            _openAI = openAI ?? throw new ArgumentNullException(nameof(openAI));
            _anthropic = anthropic ?? throw new ArgumentNullException(nameof(anthropic));
        }

        /// <summary>当前应使用的底层客户端。</summary>
        public ILLMClient Current => ActiveModelConfig.ApiFormat == ApiFormats.Anthropic ? _anthropic : _openAI;

        public async Task<ChatCompletionResponse> ChatCompletionAsync(
            List<ChatMessage> messages, List<ToolDefinition> tools = null, CancellationToken ct = default,
            int? contentMaxTokens = null, string reasoningLevelOverride = null)
        {
            var profile = ActiveModelConfig.ActiveProfile;
            try
            {
                var resolved = _discover ? await GatewayDiscovery.ResolveAsync(profile, ActiveModelConfig.ApiKey, ct) : profile;
                using (ActiveModelConfig.UseProfile(resolved))
                    return await Current.ChatCompletionAsync(messages, tools, ct, contentMaxTokens, reasoningLevelOverride);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                GatewayDiscovery.ObserveFailure(profile);
                throw;
            }
        }

        public async Task<ChatMessage> ChatCompletionStreamAsync(
            List<ChatMessage> messages, Action<StreamChunk> onChunk, List<ToolDefinition> tools = null,
            CancellationToken ct = default, int? contentMaxTokens = null, string reasoningLevelOverride = null)
        {
            var profile = ActiveModelConfig.ActiveProfile;
            try
            {
                var resolved = _discover ? await GatewayDiscovery.ResolveAsync(profile, ActiveModelConfig.ApiKey, ct) : profile;
                using (ActiveModelConfig.UseProfile(resolved))
                    return await Current.ChatCompletionStreamAsync(messages, onChunk, tools, ct, contentMaxTokens, reasoningLevelOverride);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                GatewayDiscovery.ObserveFailure(profile);
                throw;
            }
        }
    }

    /// <summary>
    /// 把每次调用固定到指定 profile 的 <see cref="ILLMClient"/> 装饰器（v1.17.0）。
    /// 用于 utility profile（压缩 / 标题 / 记忆提取走便宜模型）与 fallback 链。
    /// profile 为 null 时透明透传（等价于直接使用内层客户端）。
    /// </summary>
    public class ProfileBoundLLMClient : ILLMClient
    {
        private readonly ILLMClient _inner;
        private readonly Func<ProviderProfile> _profileProvider;

        /// <param name="inner">内层客户端（通常是 <see cref="RoutingLLMClient"/>）</param>
        /// <param name="profileProvider">每次调用时求值（允许用户改设置后即时生效）；返回 null 表示不绑定</param>
        public ProfileBoundLLMClient(ILLMClient inner, Func<ProviderProfile> profileProvider)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _profileProvider = profileProvider ?? (() => null);
        }

        public async Task<ChatCompletionResponse> ChatCompletionAsync(
            List<ChatMessage> messages, List<ToolDefinition> tools = null, CancellationToken ct = default,
            int? contentMaxTokens = null, string reasoningLevelOverride = null)
        {
            using (ActiveModelConfig.UseProfile(_profileProvider()))
            {
                return await _inner.ChatCompletionAsync(messages, tools, ct, contentMaxTokens, reasoningLevelOverride);
            }
        }

        public async Task<ChatMessage> ChatCompletionStreamAsync(
            List<ChatMessage> messages, Action<StreamChunk> onChunk, List<ToolDefinition> tools = null,
            CancellationToken ct = default, int? contentMaxTokens = null, string reasoningLevelOverride = null)
        {
            using (ActiveModelConfig.UseProfile(_profileProvider()))
            {
                return await _inner.ChatCompletionStreamAsync(messages, onChunk, tools, ct, contentMaxTokens, reasoningLevelOverride);
            }
        }
    }
}
