using System;
using Newtonsoft.Json.Linq;

namespace AgentCore.Editor.LLM
{
    /// <summary>
    /// v1.14.10: Reasoning 等级 → 具体供应商协议字段的映射层。
    /// <para>
    /// 背景：Codex/Claude Code 能把"思考强度切换"做成一个简单下拉菜单，是因为它们各自只对接
    /// 单一供应商——Claude Code 只认 Anthropic 的 <c>thinking: {budget_tokens}</c>，Codex 只认
    /// OpenAI 的 <c>reasoning_effort</c>，UI 选项与协议字段是一对一直通，没有兼容性问题。
    /// AgentCore 是通用多供应商平台，同一个"思考强度"UI 选项在不同供应商/部署方式下需要完全
    /// 不同的协议字段组合才能生效——这是本会话已经实测验证过的事实（见下方分组注释）。
    /// </para>
    /// <para>
    /// 设计原则：UI/会话层只暴露与供应商无关的抽象等级（<see cref="ReasoningLevel"/>），具体怎么
    /// 组装成请求体字段，由本类按"供应商特征分组"决定——不是按具体模型名一个个特判（那样每出
    /// 一个新模型就要加代码），是复用 <see cref="AgentCore.Editor.Core.ModelCapabilityDetector"/>
    /// 同款前缀匹配模式，把模型/部署方式归到几个大类，未匹配到任何已知特征时落到安全的默认分组。
    /// </para>
    /// </summary>
    public static class ReasoningParamMapper
    {
        /// <summary>
        /// 与供应商无关的抽象 reasoning 等级。UI 下拉菜单/会话覆盖只操作这个枚举，
        /// 不直接接触任何供应商专属字段名或数值。
        /// </summary>
        public enum ReasoningLevel
        {
            /// <summary>不覆盖，跟随全局/Profile 默认设置（<see cref="Config.ActiveModelConfig.ReasoningEffort"/>）。</summary>
            Auto,
            /// <summary>显式关闭思考（部分供应商可关，部分供应商 reasoning 常开无法通过参数关闭）。</summary>
            Off,
            Low,
            Medium,
            High
        }

        /// <summary>
        /// 供应商特征分组。用于选择"等级 → 具体字段"的映射规则，与
        /// <see cref="AgentCore.Editor.Core.ModelCapabilityDetector"/> 判断"是否有 native reasoning"
        /// 是两个正交维度——那边回答"要不要跳过 Self-Challenge"，这里回答"reasoning 参数长什么样"。
        /// </summary>
        private enum ProviderGroup
        {
            /// <summary>OpenAI 原生 reasoning 模型（o1/o3/o4/gpt-5 系列）：字段 reasoning_effort。</summary>
            OpenAINative,
            /// <summary>Anthropic 原生（Claude 3 Opus / Sonnet 4+ 等具备 extended thinking 的模型）：字段 thinking.budget_tokens。</summary>
            AnthropicNative,
            /// <summary>vLLM 托管的开源模型（部署方式判定，非模型名判定）：需要 reasoning.effort + chat_template_kwargs.thinking 双字段同时发。</summary>
            VllmHosted,
            /// <summary>OpenRouter / 其他 OpenAI 兼容代理（默认兜底分组，覆盖 GLM 等已验证生效的现状路径）：字段 reasoning.effort + reasoning.max_tokens。</summary>
            OpenRouterCompatible
        }

        /// <summary>OpenAI 原生 reasoning 模型前缀（与 ModelCapabilityDetector 保持同源认知，未来两表如需调整应同步）。</summary>
        private static readonly string[] OpenAINativePrefixes = { "o1-", "o3-", "o4-", "gpt-5" };

        /// <summary>Anthropic 原生具备 extended thinking 的模型前缀。</summary>
        private static readonly string[] AnthropicNativePrefixes = { "claude-opus", "claude-3-opus", "claude-sonnet-4" };

        /// <summary>
        /// Anthropic thinking.budget_tokens 的等级→数值对照表。
        /// <para>
        /// 注意（如实说明，不假装这是官方标准）：Anthropic 官方 API 没有公开"低/中/高"这种预设档位，
        /// 只接受一个具体的 token 预算整数。这里的三个数值是工程侧经验取值，不是查文档得到的确定值，
        /// 未来根据实际使用效果可能需要调整——不是精确科学，是一个合理的起点。
        /// </para>
        /// </summary>
        private const int AnthropicBudgetLow = 2048;
        private const int AnthropicBudgetMedium = 8192;
        private const int AnthropicBudgetHigh = 24576;

        /// <summary>
        /// 判定当前 endpoint + modelName 属于哪个供应商特征分组。
        /// </summary>
        /// <param name="endpoint">当前 profile 的 base URL（用于识别自建 vLLM 部署——不是所有
        /// vLLM 部署都在 URL 里带"vllm"字样，因此这里采用"不匹配任何原生厂商特征 + 非公有云
        /// 知名代理域名"的排除法判定，而不是要求 URL 必须包含固定关键词）。</param>
        /// <param name="modelName">当前模型名。</param>
        private static ProviderGroup DetectProviderGroup(string endpoint, string modelName)
        {
            if (string.IsNullOrEmpty(modelName))
                return ProviderGroup.OpenRouterCompatible;

            foreach (var prefix in OpenAINativePrefixes)
            {
                if (modelName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return ProviderGroup.OpenAINative;
            }

            foreach (var prefix in AnthropicNativePrefixes)
            {
                if (modelName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return ProviderGroup.AnthropicNative;
            }

            // vLLM 托管判定：已知会用到 vLLM 专属 chat_template_kwargs.thinking 开关的模型家族
            // （目前实测确认的：DeepSeek V4 系列）。这不是"部署方式"的可靠判定（同一个模型也可能
            // 换用别的引擎部署），但目前没有更好的信号来源（Endpoint URL 本身不可靠，用户可以用
            // 任意域名反代），先用模型名前缀兜底——命中即认为"可能需要这个开关"，不命中的模型即使
            // 真的是 vLLM 部署，也不会因为多发一个未知字段报错（RequestPruningRegistry 会学习剔除），
            // 代价可接受。
            if (modelName.StartsWith("deepseek-v4", StringComparison.OrdinalIgnoreCase) ||
                modelName.StartsWith("DeepSeek-V4", StringComparison.OrdinalIgnoreCase))
            {
                return ProviderGroup.VllmHosted;
            }

            // 未匹配任何已知特征：落到现状行为（OpenRouter 风格 reasoning.effort），这是本类
            // 修改前 AgentCore 唯一支持的形状，保证新模型接入时不会因为"没人写映射规则"而彻底
            // 无法使用 reasoning 功能——退化到已知能工作的现状，不是报错或空转。
            return ProviderGroup.OpenRouterCompatible;
        }

        /// <summary>
        /// 把抽象等级注入到请求 JSON body 里，按当前 endpoint/modelName 的供应商特征选择具体字段。
        /// </summary>
        /// <param name="body">请求 JSON body（会被原地修改）。</param>
        /// <param name="level">抽象 reasoning 等级。<see cref="ReasoningLevel.Auto"/> 时不做任何注入
        /// （调用方应在 Auto 时改用全局默认 effort 字符串走原有 <see cref="RequestEnrichment"/> 路径，
        /// 本方法只处理"用户明确选择了一个具体等级"的情况）。</param>
        /// <param name="endpoint">当前 profile 的 base URL。</param>
        /// <param name="modelName">当前模型名。</param>
        public static void ApplyLevel(JObject body, ReasoningLevel level, string endpoint, string modelName)
        {
            if (level == ReasoningLevel.Auto) return;

            var group = DetectProviderGroup(endpoint, modelName);

            switch (group)
            {
                case ProviderGroup.OpenAINative:
                    ApplyOpenAINative(body, level);
                    break;
                case ProviderGroup.AnthropicNative:
                    ApplyAnthropicNative(body, level);
                    break;
                case ProviderGroup.VllmHosted:
                    ApplyVllmHosted(body, level);
                    break;
                case ProviderGroup.OpenRouterCompatible:
                default:
                    ApplyOpenRouterCompatible(body, level);
                    break;
            }
        }

        private static void ApplyOpenAINative(JObject body, ReasoningLevel level)
        {
            // OpenAI 原生协议目前没有公开的"关闭 reasoning"参数——o 系列/GPT-5 的 reasoning
            // 是模型固有行为，无法通过请求字段关闭。Off 时不注入 reasoning_effort 字段
            // （既不能真正关闭，注入一个厂商不认识的值只会徒增出错风险）。
            if (level == ReasoningLevel.Off) return;

            body["reasoning_effort"] = LevelToEffortString(level);
        }

        private static void ApplyAnthropicNative(JObject body, ReasoningLevel level)
        {
            if (level == ReasoningLevel.Off)
            {
                // Anthropic 的 thinking 字段本身就是"不传 = 不启用"，没有显式 disabled 状态需要发送。
                body.Remove("thinking");
                return;
            }

            int budgetTokens = level switch
            {
                ReasoningLevel.Low => AnthropicBudgetLow,
                ReasoningLevel.Medium => AnthropicBudgetMedium,
                ReasoningLevel.High => AnthropicBudgetHigh,
                _ => AnthropicBudgetMedium
            };

            body["thinking"] = new JObject
            {
                ["type"] = "enabled",
                ["budget_tokens"] = budgetTokens
            };
        }

        private static void ApplyVllmHosted(JObject body, ReasoningLevel level)
        {
            // v1.14.10 根因（见 RequestEnrichment.InjectReasoning 同一处修复注释）：vLLM 的
            // DeepSeek V4 reasoning parser 默认不拆分 <think> 标签，必须显式传
            // chat_template_kwargs.thinking 布尔开关。Off 时设为 false（明确关闭，不是不传——
            // 不传会沿用服务端默认值，而默认值本身就是"不拆分"，语义上等同于 off，但显式传 false
            // 更清楚地表达用户意图，也不依赖服务端默认值不变）。
            bool thinkingEnabled = level != ReasoningLevel.Off;

            if (body["chat_template_kwargs"] is JObject existingKwargs)
            {
                existingKwargs["thinking"] = thinkingEnabled;
            }
            else
            {
                body["chat_template_kwargs"] = new JObject { ["thinking"] = thinkingEnabled };
            }

            if (level == ReasoningLevel.Off)
            {
                body.Remove("reasoning");
                return;
            }

            body["reasoning"] = new JObject { ["effort"] = LevelToEffortString(level) };
        }

        private static void ApplyOpenRouterCompatible(JObject body, ReasoningLevel level)
        {
            if (level == ReasoningLevel.Off)
            {
                // OpenRouter 协议里 effort="none" 是官方支持的显式关闭值（与"不传该字段"不同，
                // 后者对某些模型会沿用模型自身默认的常开行为）。
                body["reasoning"] = new JObject { ["effort"] = "none" };
                return;
            }

            body["reasoning"] = new JObject { ["effort"] = LevelToEffortString(level) };
        }

        private static string LevelToEffortString(ReasoningLevel level) => level switch
        {
            ReasoningLevel.Low => "low",
            ReasoningLevel.Medium => "medium",
            ReasoningLevel.High => "high",
            _ => "medium"
        };

        /// <summary>
        /// 把持久化用的字符串（"auto"/"off"/"low"/"medium"/"high"，与
        /// <see cref="Session.SessionData.ReasoningEffortOverride"/> 现有存储格式保持一致，
        /// 不引入新的序列化形状）解析为 <see cref="ReasoningLevel"/>。未识别的值视为 Auto
        /// （安全默认——不会因为脏数据/未来新增值而导致请求异常）。
        /// </summary>
        public static ReasoningLevel ParseLevel(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return ReasoningLevel.Auto;

            switch (value.Trim().ToLowerInvariant())
            {
                case "off": return ReasoningLevel.Off;
                case "low": return ReasoningLevel.Low;
                case "medium": return ReasoningLevel.Medium;
                case "high": return ReasoningLevel.High;
                case "auto":
                default:
                    return ReasoningLevel.Auto;
            }
        }
    }
}
