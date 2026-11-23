using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace AgentCore.Editor.LLM
{
    /// <summary>
    /// 一次 reasoning 解析的结果（v1.22.0）。供请求管线与 UI 共用：
    /// UI 只需读 <see cref="EffectiveLevel"/> / <see cref="WasClamped"/> / <see cref="ClampReason"/>
    /// 决定下拉菜单显示与提示，不必重复实现模型能力判定。
    /// </summary>
    public sealed class ReasoningResolution
    {
        private readonly ModelProfile _profile;
        private readonly ReasoningParamMapper.ReasoningLevel? _learnedCap;
        private readonly IReadOnlyCollection<ReasoningParamMapper.ReasoningLevel> _learnedBanned;

        internal ReasoningResolution(
            ReasoningParamMapper.ReasoningLevel requestedLevel,
            ReasoningParamMapper.ReasoningLevel effectiveLevel,
            bool wasClamped,
            string clampReason,
            bool isDisabled,
            int budgetTokens,
            ModelProfile profile,
            ReasoningParamMapper.ReasoningLevel? learnedCap,
            IReadOnlyCollection<ReasoningParamMapper.ReasoningLevel> learnedBanned)
        {
            RequestedLevel = requestedLevel;
            EffectiveLevel = effectiveLevel;
            WasClamped = wasClamped;
            ClampReason = clampReason;
            IsDisabled = isDisabled;
            BudgetTokens = budgetTokens;
            _profile = profile;
            _learnedCap = learnedCap;
            _learnedBanned = learnedBanned;
        }

        /// <summary>用户/默认设置请求的等级（Auto 表示"跟随默认，不显式发 effort"）。</summary>
        public ReasoningParamMapper.ReasoningLevel RequestedLevel { get; }

        /// <summary>按模型能力 + 运行时学习约束吸附后实际发送的等级。</summary>
        public ReasoningParamMapper.ReasoningLevel EffectiveLevel { get; }

        /// <summary>是否因能力/学习约束发生了吸附。</summary>
        public bool WasClamped { get; }

        /// <summary>
        /// 吸附原因：<c>model_cap</c> / <c>model_floor</c> / <c>always_on</c> / <c>runtime_learned</c>；
        /// 未吸附为 null。
        /// </summary>
        public string ClampReason { get; }

        /// <summary>本次解析是否等价于"关闭思考"（显式 Off，或默认关闭且无覆盖）。</summary>
        public bool IsDisabled { get; }

        /// <summary>有效等级对应的 thinking 预算（Off = 0，Auto = 调用方默认预算）。</summary>
        public int BudgetTokens { get; }

        /// <summary>按本次解析已知的模型能力 + 运行时约束判断某档是否可用（UI 灰显用）。</summary>
        public bool IsTierSupported(ReasoningParamMapper.ReasoningLevel level)
            => ReasoningParamMapper.IsTierSupported(_profile, level, _learnedCap, _learnedBanned);

        /// <summary>仅按模型档案判断某档是否可用（不知道 endpoint 时的 UI 降级路径）。</summary>
        public static bool IsTierSupported(ModelProfile profile, ReasoningParamMapper.ReasoningLevel level)
            => ReasoningParamMapper.IsTierSupported(profile, level, null, null);
    }

    /// <summary>
    /// v1.14.10: Reasoning 等级 → 具体供应商协议字段的映射层；v1.22.0 起同时承担
    /// "等级 → 模型能力吸附"的统一解析（<see cref="ResolveEffectivePlan"/>）。
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
        /// <para>顺序即强度顺序：Auto / Off 之后自弱到强，比较时使用 <c>(int)</c>。</para>
        /// </summary>
        public enum ReasoningLevel
        {
            /// <summary>不覆盖，跟随全局/Profile 默认设置（<see cref="Config.ActiveModelConfig.ReasoningEffort"/>）。</summary>
            Auto = 0,
            /// <summary>显式关闭思考（部分供应商可关，部分供应商 reasoning 常开无法通过参数关闭）。</summary>
            Off = 1,
            /// <summary>最低思考预算（OpenAI gpt-5 系列 / Gemini 的 minimal 档）。</summary>
            Minimal = 2,
            Low = 3,
            Medium = 4,
            High = 5,
            /// <summary>高于 high 的扩展档（Anthropic Opus 4.7+ / GPT-5.1+）。</summary>
            XHigh = 6,
            /// <summary>最高档（<c>ultra</c> 别名）。</summary>
            Max = 7
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

        /// <summary>Auto 且无预算时的默认 thinking 预算。</summary>
        private const int DefaultThinkingBudget = 4096;

        /// <summary>Anthropic thinking 预算的协议下限。</summary>
        private const int MinimumThinkingBudget = 1024;

        private static readonly ModelProfile UnknownProfile = new() { Family = ModelFamily.Unknown };

        /// <summary>
        /// 判定当前 endpoint + modelName 属于哪个供应商特征分组。
        /// </summary>
        /// <param name="endpoint">当前 profile 的 base URL（保留参数以便未来按部署方式判定；当前主要靠模型档案）。</param>
        /// <param name="modelName">当前模型名。</param>
        private static ProviderGroup DetectProviderGroup(string endpoint, string modelName)
        {
            if (string.IsNullOrEmpty(modelName))
                return ProviderGroup.OpenRouterCompatible;

            // v1.22.0: 注册表的声明式协议优先（用户覆盖文件可改写单个模型的协议形态）。
            var registered = ModelRegistry.Lookup(modelName);
            switch (registered.ReasoningProtocol)
            {
                case ReasoningProtocols.OpenAIEffort: return ProviderGroup.OpenAINative;
                case ReasoningProtocols.AnthropicThinking: return ProviderGroup.AnthropicNative;
                case ReasoningProtocols.VllmReasoning: return ProviderGroup.VllmHosted;
                case ReasoningProtocols.OpenRouterReasoning: return ProviderGroup.OpenRouterCompatible;
            }

            // v1.17.0: 只有 budget_tokens 旧形态的 Claude 才发 thinking.budget_tokens；
            // 4.6+ 的 Claude 发 budget_tokens 会 400，经 OpenAI 兼容网关时改走 reasoning.effort（由网关翻译）。
            if (registered.Family == ModelFamily.Anthropic)
            {
                return registered.Thinking == ThinkingMode.BudgetTokens
                    ? ProviderGroup.AnthropicNative
                    : ProviderGroup.OpenRouterCompatible;
            }

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
        /// <param name="level">抽象 reasoning 等级。<see cref="ReasoningLevel.Auto"/> 时不做任何注入。</param>
        /// <param name="endpoint">当前 profile 的 base URL。</param>
        /// <param name="modelName">当前模型名。</param>
        /// <param name="maxTierCapOverride">调用方已知的额外上限（null = 查询运行时学习约束）。</param>
        /// <param name="reasoningMaxTokens">reasoning.max_tokens（0 = 不发送，仅 OpenRouter 风格分组使用）。</param>
        public static void ApplyLevel(
            JObject body,
            ReasoningLevel level,
            string endpoint,
            string modelName,
            ReasoningLevel? maxTierCapOverride = null,
            int reasoningMaxTokens = 0)
        {
            if (body == null || level == ReasoningLevel.Auto) return;

            var profile = ModelRegistry.Lookup(modelName);
            var constraint = RequestPruningRegistry.GetReasoningConstraint(endpoint, modelName);
            var effective = ClampForApply(
                level, profile, maxTierCapOverride ?? constraint.MaxAllowedTier, constraint.BannedTiers);

            if (effective == ReasoningLevel.Auto) return;

            switch (DetectProviderGroup(endpoint, modelName))
            {
                case ProviderGroup.OpenAINative:
                    ApplyOpenAINative(body, effective);
                    break;
                case ProviderGroup.AnthropicNative:
                    ApplyAnthropicNative(body, effective);
                    break;
                case ProviderGroup.VllmHosted:
                    ApplyVllmHosted(body, effective);
                    break;
                case ProviderGroup.OpenRouterCompatible:
                default:
                    ApplyOpenRouterCompatible(body, effective, reasoningMaxTokens);
                    break;
            }
        }

        /// <summary>
        /// 把一次解析结果直接写入请求体（等价于 <see cref="ApplyLevel"/> + <see cref="ReasoningResolution.EffectiveLevel"/>）。
        /// </summary>
        public static void ApplyResolvedLevel(
            JObject body, ReasoningResolution plan, string endpoint, string modelName, int reasoningMaxTokens = 0)
        {
            if (plan == null) return;
            ApplyLevel(body, plan.EffectiveLevel, endpoint, modelName, null, reasoningMaxTokens);
        }

        private static ReasoningLevel ClampForApply(
            ReasoningLevel level,
            ModelProfile profile,
            ReasoningLevel? learnedCap,
            IReadOnlyCollection<ReasoningLevel> learnedBanned)
        {
            if (level == ReasoningLevel.Auto) return ReasoningLevel.Auto;

            if (level == ReasoningLevel.Off)
                return profile != null && profile.Thinking == ThinkingMode.AlwaysOn
                    ? ReasoningLevel.Low
                    : ReasoningLevel.Off;

            var floor = MinSupported(profile);
            var cap = MaxSupported(profile);
            var effective = level;
            if (Rank(effective) < Rank(floor)) effective = floor;
            if (Rank(effective) > Rank(cap)) effective = cap;
            if (learnedCap.HasValue && Rank(effective) > Rank(learnedCap.Value)) effective = learnedCap.Value;
            return SnapPastBanned(effective, learnedBanned, cap, floor);
        }

        private static void ApplyOpenAINative(JObject body, ReasoningLevel level)
        {
            // OpenAI 原生协议目前没有公开的"关闭 reasoning"参数——o 系列/GPT-5 的 reasoning
            // 是模型固有行为，无法通过请求字段关闭。Off 时不注入 reasoning_effort 字段
            // （既不能真正关闭，注入一个厂商不认识的值只会徒增出错风险）。
            if (level == ReasoningLevel.Off) return;

            body["reasoning_effort"] = ToEffortString(level);
        }

        private static void ApplyAnthropicNative(JObject body, ReasoningLevel level)
        {
            if (level == ReasoningLevel.Off)
            {
                // Anthropic 的 thinking 字段本身就是"不传 = 不启用"，没有显式 disabled 状态需要发送。
                body.Remove("thinking");
                return;
            }

            body["thinking"] = new JObject
            {
                ["type"] = "enabled",
                ["budget_tokens"] = GetBudgetTokens(level)
            };
        }

        private static void ApplyVllmHosted(JObject body, ReasoningLevel level)
        {
            // v1.14.10 根因（见 RequestEnrichment.InjectReasoning 同一处修复注释）：vLLM 的
            // DeepSeek V4 reasoning parser 默认不拆分  thinking 标签，必须显式传
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

            body["reasoning"] = new JObject { ["effort"] = ToEffortString(level) };
        }

        private static void ApplyOpenRouterCompatible(JObject body, ReasoningLevel level, int reasoningMaxTokens)
        {
            if (level == ReasoningLevel.Off)
            {
                // OpenRouter 协议里 effort="none" 是官方支持的显式关闭值（与"不传该字段"不同，
                // 后者对某些模型会沿用模型自身默认的常开行为）。
                body["reasoning"] = new JObject { ["effort"] = "none" };
                return;
            }

            var reasoning = new JObject { ["effort"] = ToEffortString(level) };
            if (reasoningMaxTokens > 0) reasoning["max_tokens"] = reasoningMaxTokens;
            body["reasoning"] = reasoning;
        }

        /// <summary>
        /// 等级 → 协议 effort 字符串（同时作为持久化/UI 的规范名）。
        /// </summary>
        public static string ToEffortString(ReasoningLevel level) => level switch
        {
            ReasoningLevel.Auto => "auto",
            ReasoningLevel.Off => "off",
            ReasoningLevel.Minimal => "minimal",
            ReasoningLevel.Low => "low",
            ReasoningLevel.Medium => "medium",
            ReasoningLevel.High => "high",
            ReasoningLevel.XHigh => "xhigh",
            ReasoningLevel.Max => "max",
            _ => "auto"
        };

        /// <summary>等级 → 持久化字符串（与 <see cref="ToEffortString"/> 同一规范名）。</summary>
        public static string LevelToString(ReasoningLevel level) => ToEffortString(level);

        /// <summary>
        /// 统一 thinking 预算表。Off = 0；Auto = <paramref name="defaultBudget"/>。
        /// </summary>
        public static int GetBudgetTokens(ReasoningLevel level, int defaultBudget = DefaultThinkingBudget)
        {
            switch (level)
            {
                case ReasoningLevel.Off: return 0;
                case ReasoningLevel.Minimal: return 1024;
                case ReasoningLevel.Low: return 2048;
                case ReasoningLevel.Medium: return 8192;
                case ReasoningLevel.High: return 16384;
                case ReasoningLevel.XHigh: return 24576;
                case ReasoningLevel.Max: return 32768;
                default: return defaultBudget;
            }
        }

        /// <summary>
        /// 解析持久化字符串（"auto"/"off"/"minimal"/"low"/"medium"/"high"/"xhigh"/"max"，
        /// <c>"ultra"</c> 为 Max 别名）。null/空/未知值视为 Auto（安全默认）。
        /// </summary>
        public static ReasoningLevel ParseLevel(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return ReasoningLevel.Auto;

            switch (value.Trim().ToLowerInvariant())
            {
                case "off":
                case "none":
                case "disabled":
                    return ReasoningLevel.Off;
                case "minimal":
                    return ReasoningLevel.Minimal;
                case "low":
                    return ReasoningLevel.Low;
                case "medium":
                    return ReasoningLevel.Medium;
                case "high":
                    return ReasoningLevel.High;
                case "xhigh":
                case "x-high":
                case "x_high":
                    return ReasoningLevel.XHigh;
                case "max":
                case "ultra":
                    return ReasoningLevel.Max;
                case "auto":
                default:
                    return ReasoningLevel.Auto;
            }
        }

        /// <summary>
        /// 只解析"用户/默认设置请求的等级"，不查模型能力：
        /// 覆盖优先；否则解析默认 effort；空 effort 时按默认开关决定 Auto（保留空 effort 的
        /// "reasoning: {}" 兼容行为）或 Off。
        /// </summary>
        public static ReasoningLevel ResolveRequestedLevel(
            ReasoningLevel overrideLevel, string defaultEffort, bool defaultReasoningEnabled)
        {
            if (overrideLevel != ReasoningLevel.Auto) return overrideLevel;

            var parsed = ParseLevel(defaultEffort);
            if (parsed != ReasoningLevel.Auto) return parsed;

            return defaultReasoningEnabled ? ReasoningLevel.Auto : ReasoningLevel.Off;
        }

        /// <summary>
        /// 统一解析：请求等级 + 模型档案能力 + 运行时学习约束 → 实际发送计划。
        /// <para>
        /// 吸附规则：模型上限向下取（Max → XHigh → High）、模型下限向上取（Minimal → Low，绝不掉进 Off）、
        /// AlwaysOn 模型请求 Off 时映射到 Low、Unknown 模型乐观放行（除非运行时学习到上限/禁用档）。
        /// </para>
        /// </summary>
        /// <param name="overrideLevel">会话覆盖等级（Auto = 不覆盖）。</param>
        /// <param name="defaultEffort">全局/Profile 默认 effort 字符串。</param>
        /// <param name="defaultReasoningEnabled">默认是否请求 reasoning 输出。</param>
        /// <param name="endpoint">当前 endpoint（用于运行时学习约束）。</param>
        /// <param name="modelName">当前模型名。</param>
        /// <param name="profile">模型档案（null 时按模型名查注册表）。</param>
        /// <param name="dialect">当前方言（保留参数，供未来按方言细分）。</param>
        public static ReasoningResolution ResolveEffectivePlan(
            ReasoningLevel overrideLevel,
            string defaultEffort,
            bool defaultReasoningEnabled,
            string endpoint,
            string modelName,
            ModelProfile profile = null,
            string dialect = null)
        {
            var requested = ResolveRequestedLevel(overrideLevel, defaultEffort, defaultReasoningEnabled);
            profile = profile ?? ModelRegistry.Lookup(modelName);
            var constraint = RequestPruningRegistry.GetReasoningConstraint(endpoint, modelName);
            return ResolvePlan(requested, profile, constraint.MaxAllowedTier, constraint.BannedTiers);
        }

        /// <summary>
        /// 纯函数解析（无 endpoint / 注册表依赖），供测试与已持有约束的调用方复用。
        /// </summary>
        internal static ReasoningResolution ResolvePlan(
            ReasoningLevel requested,
            ModelProfile profile,
            ReasoningLevel? learnedCap,
            IReadOnlyCollection<ReasoningLevel> learnedBanned,
            int defaultBudget = DefaultThinkingBudget)
        {
            profile = profile ?? UnknownProfile;
            var effective = requested;
            bool clamped = false;
            string reason = null;

            if (requested != ReasoningLevel.Auto && requested != ReasoningLevel.Off)
            {
                var floor = MinSupported(profile);
                if (Rank(effective) < Rank(floor))
                {
                    effective = floor;
                    clamped = true;
                    reason = "model_floor";
                }

                var cap = MaxSupported(profile);
                if (Rank(effective) > Rank(cap))
                {
                    effective = cap;
                    clamped = true;
                    reason = "model_cap";
                }

                if (learnedCap.HasValue && Rank(effective) > Rank(learnedCap.Value))
                {
                    effective = learnedCap.Value;
                    clamped = true;
                    reason = "runtime_learned";
                }

                var snapped = SnapPastBanned(effective, learnedBanned, cap, floor);
                if (snapped != effective)
                {
                    effective = snapped;
                    clamped = true;
                    reason = reason ?? "runtime_learned";
                }
            }
            else if (requested == ReasoningLevel.Off && profile.Thinking == ThinkingMode.AlwaysOn)
            {
                effective = ReasoningLevel.Low;
                clamped = true;
                reason = "always_on";
            }

            bool disabled = effective == ReasoningLevel.Off
                            || (profile.Family == ModelFamily.Anthropic && profile.Thinking == ThinkingMode.None);

            return new ReasoningResolution(
                requested, effective, clamped, reason, disabled,
                GetBudgetTokens(effective, defaultBudget), profile, learnedCap, learnedBanned);
        }

        /// <summary>某档在该模型（+ 运行时约束）下是否可用。</summary>
        internal static bool IsTierSupported(
            ModelProfile profile,
            ReasoningLevel level,
            ReasoningLevel? learnedCap,
            IReadOnlyCollection<ReasoningLevel> learnedBanned)
        {
            if (level == ReasoningLevel.Auto) return true;
            if (profile == null) return true;

            if (level == ReasoningLevel.Off)
                return profile.Thinking != ThinkingMode.AlwaysOn;

            var floor = MinSupported(profile);
            var cap = MaxSupported(profile);
            if (learnedCap.HasValue && Rank(learnedCap.Value) < Rank(cap)) cap = learnedCap.Value;

            if (Rank(level) < Rank(floor) || Rank(level) > Rank(cap)) return false;
            if (learnedBanned != null)
            {
                foreach (var banned in learnedBanned)
                    if (banned == level) return false;
            }
            return true;
        }

        /// <summary>模型支持的最高档（Unknown 家族乐观放行到 Max）。</summary>
        private static ReasoningLevel MaxSupported(ModelProfile profile)
        {
            if (profile == null) return ReasoningLevel.Max;

            if (!string.IsNullOrWhiteSpace(profile.MaxEffort))
            {
                var declared = ParseLevel(profile.MaxEffort);
                if (declared != ReasoningLevel.Auto) return declared;
            }

            if (profile.Family == ModelFamily.Unknown) return ReasoningLevel.Max;

            if (profile.Family == ModelFamily.Anthropic)
            {
                // Anthropic 的 output_config.effort 最高只到 xhigh（没有 max 档），
                // 因此未显式声明 MaxEffort 时上限为 XHigh，Max 请求向下吸附。
                if (profile.Thinking == ThinkingMode.None) return ReasoningLevel.Off;
                return profile.SupportsXHigh ? ReasoningLevel.XHigh : ReasoningLevel.High;
            }

            // 已知非 Anthropic 家族但未声明上限：按 xhigh 支持位保守推断。
            return profile.SupportsXHigh ? ReasoningLevel.Max : ReasoningLevel.High;
        }

        /// <summary>模型支持的最低主动档（默认 Off，即允许关闭）。</summary>
        private static ReasoningLevel MinSupported(ModelProfile profile)
        {
            if (profile != null && !string.IsNullOrWhiteSpace(profile.MinEffort))
            {
                var declared = ParseLevel(profile.MinEffort);
                if (declared != ReasoningLevel.Auto) return declared;
            }
            return ReasoningLevel.Off;
        }

        /// <summary>运行时学习禁用某档时的最近有效吸附：Minimal 被禁向上，其余向下再兜底向上。</summary>
        private static ReasoningLevel SnapPastBanned(
            ReasoningLevel level,
            IReadOnlyCollection<ReasoningLevel> banned,
            ReasoningLevel cap,
            ReasoningLevel floor)
        {
            if (banned == null || banned.Count == 0 || !Contains(banned, level)) return level;

            if (level == ReasoningLevel.Minimal)
            {
                for (var candidate = ReasoningLevel.Low; Rank(candidate) <= Rank(cap); candidate = Next(candidate))
                    if (!Contains(banned, candidate)) return candidate;
                return level;
            }

            var lowerBound = Rank(floor) > Rank(ReasoningLevel.Minimal) ? floor : ReasoningLevel.Minimal;
            for (var candidate = Prev(level); Rank(candidate) >= Rank(lowerBound); candidate = Prev(candidate))
                if (!Contains(banned, candidate)) return candidate;

            for (var candidate = ReasoningLevel.Minimal; Rank(candidate) <= Rank(cap); candidate = Next(candidate))
                if (!Contains(banned, candidate)) return candidate;

            return level;
        }

        private static bool Contains(IReadOnlyCollection<ReasoningLevel> set, ReasoningLevel level)
        {
            foreach (var item in set)
                if (item == level) return true;
            return false;
        }

        private static int Rank(ReasoningLevel level) => (int)level;

        private static ReasoningLevel Next(ReasoningLevel level)
            => Rank(level) >= Rank(ReasoningLevel.Max) ? ReasoningLevel.Max : (ReasoningLevel)(Rank(level) + 1);

        private static ReasoningLevel Prev(ReasoningLevel level)
            => Rank(level) <= Rank(ReasoningLevel.Auto) ? ReasoningLevel.Auto : (ReasoningLevel)(Rank(level) - 1);
    }
}
