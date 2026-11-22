using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;

namespace AgentCore.Editor.Tools.Safety
{
    /// <summary>
    /// 权限规则的效果（P2 用户可配置规则）。
    /// <para>优先级：<see cref="Deny"/> &gt; <see cref="Ask"/> &gt; <see cref="Allow"/>。</para>
    /// </summary>
    public enum PermissionRuleEffect
    {
        /// <summary>直接放行，跳过确认（含底线操作——这是用户的显式选择）。</summary>
        Allow = 0,

        /// <summary>强制弹出确认（即便是只读 / 低风险工具）；Trust Low/Med 不会自动放行。</summary>
        Ask = 1,

        /// <summary>拒绝执行，以普通工具失败返回给 LLM。YOLO 下不生效。</summary>
        Deny = 2
    }

    /// <summary>规则来源（决定存储文件）。</summary>
    public enum PermissionRuleScope
    {
        /// <summary>每用户规则：<c>UserSettings/AgentCore/PermissionRules.json</c>（不进版本库）。</summary>
        User = 0,

        /// <summary>遗留项目规则：<c>ProjectSettings/AgentCore/PermissionRules.json</c>（仅在已存在时只读加载，可本地停用，不可写）。</summary>
        Project = 1
    }

    /// <summary>
    /// 一条 allow / ask / deny 权限规则（JSON 持久化形态）。
    /// <para>
    /// 匹配器：工具名（精确或 <c>*</c> / <c>?</c> 通配）+ 可选 action（同样支持通配）+
    /// 可选路径 glob（项目相对路径、正斜杠，支持 <c>**</c>）。<c>tool</c> 也可写成
    /// <c>manage_asset.delete</c> / <c>manage_script.*</c> 简写，此时 <c>action</c> 留空。
    /// </para>
    /// </summary>
    [Serializable]
    public sealed class PermissionRule
    {
        /// <summary>稳定 id（用于本地停用项目规则、UI 编辑）。</summary>
        public string id;

        /// <summary>效果：<c>allow</c> / <c>ask</c> / <c>deny</c>（大小写不敏感）。无法识别的值使规则失效（不拦截）。</summary>
        public string effect;

        /// <summary>工具名，精确或通配；可带 <c>.action</c> 简写。为空视为 <c>*</c>。</summary>
        public string tool;

        /// <summary>可选 action（通配）；为空表示任意 action。</summary>
        public string action;

        /// <summary>可选路径 glob（项目相对、正斜杠，支持 <c>**</c>）；为空表示不限路径。</summary>
        public string path;

        /// <summary>可选备注（展示在确认框 / 拒绝消息里）。</summary>
        public string note;

        /// <summary>是否启用。</summary>
        public bool enabled = true;

        /// <summary>生成新的短 id。</summary>
        public static string NewId() => Guid.NewGuid().ToString("N").Substring(0, 12);

        /// <summary>解析 <see cref="effect"/>；无法识别返回 false。</summary>
        public bool TryGetEffect(out PermissionRuleEffect result)
        {
            switch ((effect ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "allow":
                    result = PermissionRuleEffect.Allow;
                    return true;
                case "ask":
                    result = PermissionRuleEffect.Ask;
                    return true;
                case "deny":
                    result = PermissionRuleEffect.Deny;
                    return true;
                default:
                    result = PermissionRuleEffect.Allow;
                    return false;
            }
        }

        /// <summary>效果枚举 → JSON 字符串。</summary>
        public static string EffectToString(PermissionRuleEffect value)
        {
            switch (value)
            {
                case PermissionRuleEffect.Deny: return "deny";
                case PermissionRuleEffect.Ask: return "ask";
                default: return "allow";
            }
        }

        /// <summary>
        /// 拆出实际参与匹配的工具 / action 模式：<c>tool</c> 带 <c>.</c> 且 <c>action</c> 为空时按第一个点拆分。
        /// </summary>
        public void GetToolAndActionPatterns(out string toolPattern, out string actionPattern)
        {
            toolPattern = (tool ?? string.Empty).Trim();
            actionPattern = (action ?? string.Empty).Trim();
            if (actionPattern.Length == 0)
            {
                int dot = toolPattern.IndexOf('.');
                if (dot >= 0)
                {
                    actionPattern = toolPattern.Substring(dot + 1);
                    toolPattern = toolPattern.Substring(0, dot);
                }
            }

            if (toolPattern.Length == 0)
                toolPattern = "*";
        }

        /// <summary>人类可读的匹配器描述，如 <c>manage_asset.delete @ Assets/Plugins/**</c>。</summary>
        public string DescribeMatcher()
        {
            GetToolAndActionPatterns(out var t, out var a);
            var sb = new StringBuilder(t);
            if (a.Length > 0)
                sb.Append('.').Append(a);
            if (!string.IsNullOrWhiteSpace(path))
                sb.Append(" @ ").Append(path.Trim());
            return sb.ToString();
        }

        /// <summary>深拷贝。</summary>
        public PermissionRule Clone()
        {
            return new PermissionRule
            {
                id = id,
                effect = effect,
                tool = tool,
                action = action,
                path = path,
                note = note,
                enabled = enabled
            };
        }
    }

    /// <summary>
    /// 一条带来源的规则（评估时把用户 / 项目两份文件合并成同一列表）。
    /// </summary>
    public sealed class ScopedPermissionRule
    {
        public PermissionRule Rule { get; }
        public PermissionRuleScope Scope { get; }

        public ScopedPermissionRule(PermissionRule rule, PermissionRuleScope scope)
        {
            Rule = rule ?? throw new ArgumentNullException(nameof(rule));
            Scope = scope;
        }
    }

    /// <summary>
    /// 规则评估结果：命中的最高优先级规则。未命中任何规则时 <see cref="Rule"/> 为 null。
    /// </summary>
    public sealed class PermissionRuleMatch
    {
        /// <summary>未命中。</summary>
        public static readonly PermissionRuleMatch None = new PermissionRuleMatch(null, PermissionRuleScope.User, PermissionRuleEffect.Allow);

        public PermissionRule Rule { get; }
        public PermissionRuleScope Scope { get; }
        public PermissionRuleEffect Effect { get; }

        /// <summary>是否命中了规则。</summary>
        public bool HasMatch => Rule != null;

        public PermissionRuleMatch(PermissionRule rule, PermissionRuleScope scope, PermissionRuleEffect effect)
        {
            Rule = rule;
            Scope = scope;
            Effect = effect;
        }

        /// <summary>
        /// 描述命中的规则，如 <c>deny rule 'manage_asset.delete @ Assets/Plugins/**' (user, id=abc) — note</c>。
        /// </summary>
        public string Describe()
        {
            if (Rule == null)
                return "(no rule)";
            var sb = new StringBuilder();
            sb.Append(PermissionRule.EffectToString(Effect))
              .Append(" rule '").Append(Rule.DescribeMatcher()).Append("' (")
              .Append(Scope == PermissionRuleScope.Project ? "project" : "user");
            if (!string.IsNullOrEmpty(Rule.id))
                sb.Append(", id=").Append(Rule.id);
            sb.Append(')');
            if (!string.IsNullOrWhiteSpace(Rule.note))
                sb.Append(" — ").Append(Rule.note.Trim());
            return sb.ToString();
        }
    }

    /// <summary>
    /// 规则文件内容（用户文件与项目文件同一结构；<see cref="disabledProjectRules"/> 只在用户文件中使用）。
    /// </summary>
    public sealed class PermissionRulesDocument
    {
        public int version = 1;

        public List<PermissionRule> rules = new List<PermissionRule>();

        /// <summary>本用户在本地停用的项目规则 id（仅用户文件）。</summary>
        public List<string> disabledProjectRules = new List<string>();

        public string ToJson()
        {
            var settings = new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore };
            return JsonConvert.SerializeObject(this, Formatting.Indented, settings).Replace("\r\n", "\n") + "\n";
        }

        /// <summary>解析 JSON；空内容返回空文档，格式错误抛 <see cref="JsonException"/>。</summary>
        public static PermissionRulesDocument FromJson(string json)
        {
            var doc = string.IsNullOrWhiteSpace(json)
                ? null
                : JsonConvert.DeserializeObject<PermissionRulesDocument>(json);
            doc ??= new PermissionRulesDocument();
            doc.rules ??= new List<PermissionRule>();
            doc.rules.RemoveAll(r => r == null);
            doc.disabledProjectRules ??= new List<string>();
            return doc;
        }
    }

    /// <summary>
    /// 规则来源（可注入，便于测试）。
    /// </summary>
    public interface IPermissionRuleSource
    {
        /// <summary>
        /// 返回当前生效的规则（已排除停用规则与本地停用的项目规则）。无规则时返回空列表。
        /// </summary>
        IReadOnlyList<ScopedPermissionRule> GetActiveRules();
    }

    /// <summary>
    /// 会话信任状态（由 UI 层持有，Dispatcher 只读）。
    /// <para>
    /// YOLO 状态存在于 ChatWindow 的会话信任集合中；Dispatcher 通过本接口查询，
    /// 以便在 YOLO 下跳过 deny 规则（YOLO = 零审查、零拦截）。
    /// </para>
    /// </summary>
    public interface IToolSessionTrustState
    {
        /// <summary>当前会话是否处于 YOLO（<see cref="ToolConfirmationTrustScope.SessionAll"/>）。</summary>
        bool IsYoloActive { get; }
    }
}
