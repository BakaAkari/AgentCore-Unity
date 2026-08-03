using System;

namespace AgentCore.Editor.Config
{
    /// <summary>
    /// 单个 LLM Provider 配置（v1.13.0）。
    /// <para>
    /// 保存一套完整的连接参数（endpoint + modelName + 可选覆盖参数），
    /// 供用户在多个 provider 之间一键切换。apiKey <b>不</b>存于此结构中，
    /// 而是按 <see cref="id"/> 分键存于 <see cref="SecureKeyStorage"/>（EditorPrefs，不进 git）。
    /// </para>
    /// <para>
    /// 覆盖语义：<c>overrideXxx</c> 布尔位为 true 时才读对应值，否则 fallthrough 到
    /// <see cref="AgentCoreSettings"/> 全局默认。<see cref="extraRequestBody"/> 无 override 位——
    /// 空字符串即视为 fallthrough。解析逻辑统一在 <c>ActiveModelConfig</c> 中实现。
    /// </para>
    /// </summary>
    [Serializable]
    public class ProviderProfile
    {
        /// <summary>GUID 主键，永不变。允许用户改 <see cref="displayName"/> 而不破坏 activeProfileId 引用。</summary>
        public string id;

        /// <summary>用户可编辑的显示名，例 "本地 GLM-5.2" / "OpenAI GPT-5"。</summary>
        public string displayName;

        /// <summary>base URL（OpenAI-compatible），例 http://172.16.248.201:34567/v1 。</summary>
        public string endpoint;

        /// <summary>模型名，例 glm-5.2 。</summary>
        public string modelName;

        // === 可选覆盖字段（override 位为 false 时 fallthrough 到全局默认）===

        /// <summary>是否覆盖 temperature。</summary>
        public bool overrideTemperature;

        /// <summary>覆盖值：采样温度。仅 <see cref="overrideTemperature"/> 为 true 时生效。</summary>
        public float temperature;

        /// <summary>是否覆盖 maxTokens。</summary>
        public bool overrideMaxTokens;

        /// <summary>覆盖值：content 部分 max_tokens。仅 <see cref="overrideMaxTokens"/> 为 true 时生效。</summary>
        public int maxTokens;

        /// <summary>是否覆盖 reasoning 相关三项。</summary>
        public bool overrideReasoning;

        /// <summary>覆盖值：reasoning effort（"low"/"medium"/"high"/""）。仅 <see cref="overrideReasoning"/> 为 true 时生效。</summary>
        public string reasoningEffort;

        /// <summary>覆盖值：reasoning token 预算。仅 <see cref="overrideReasoning"/> 为 true 时生效。</summary>
        public int reasoningMaxTokens;

        /// <summary>覆盖值：是否请求 reasoning 输出。仅 <see cref="overrideReasoning"/> 为 true 时生效。</summary>
        public bool enableReasoningOutput;

        /// <summary>追加到请求体的 JSON。空字符串 = 不追加（fallthrough 到全局默认），无需 override 位。</summary>
        public string extraRequestBody;

        // === 元数据 ===

        /// <summary>创建时间（Unix 毫秒）。</summary>
        public long createdAtUnixMs;

        /// <summary>最近一次被设为 active 的时间（Unix 毫秒）。</summary>
        public long lastUsedAtUnixMs;

        /// <summary>
        /// 工厂方法：生成一个新 profile，分配 GUID id 与创建时间戳。
        /// 其余字段留默认（override 位全 false，走全局 fallthrough）。
        /// </summary>
        public static ProviderProfile Create(string displayName)
        {
            return new ProviderProfile
            {
                id = Guid.NewGuid().ToString(),
                displayName = displayName ?? "",
                endpoint = "",
                modelName = "",
                reasoningEffort = "",
                extraRequestBody = "",
                createdAtUnixMs = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                lastUsedAtUnixMs = 0
            };
        }
    }
}
