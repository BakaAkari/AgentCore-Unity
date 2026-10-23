using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentCore.Editor.Tools.Safety
{
    /// <summary>
    /// 权限规则匹配器（纯逻辑，可单测）。
    /// <para>
    /// glob 语义：
    /// <list type="bullet">
    ///   <item><description>工具名 / action：<c>*</c> 匹配任意字符串，<c>?</c> 匹配单个字符，其余按字面量；大小写不敏感。</description></item>
    ///   <item><description>路径：锚定整条项目相对路径；<c>**</c> 跨目录，<c>*</c> / <c>?</c> 不跨 <c>/</c>；
    ///   <c>dir/**</c> 同时匹配 <c>dir</c> 本身；<c>**/</c> 匹配零或多级目录；大小写不敏感。</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// 路径条件：调用无路径时带路径条件的规则不命中；deny / ask 只要任一路径命中即生效，
    /// allow 要求全部路径命中（避免 "move Assets/Sandbox/x → Assets/Core/x" 被沙盒 allow 放行）。
    /// </para>
    /// </summary>
    public static class PermissionRuleMatcher
    {
        private static readonly ConcurrentDictionary<string, Regex> RegexCache = new ConcurrentDictionary<string, Regex>(StringComparer.Ordinal);

        /// <summary>
        /// glob 匹配。<paramref name="pathMode"/> 为 true 时 <c>*</c> 不跨 <c>/</c>，并支持 <c>**</c>。
        /// </summary>
        public static bool GlobMatch(string pattern, string text, bool pathMode)
        {
            if (pattern == null)
                return false;
            text ??= string.Empty;
            var regex = RegexCache.GetOrAdd((pathMode ? "p:" : "n:") + pattern, key => BuildRegex(pattern, pathMode));
            return regex.IsMatch(text);
        }

        private static Regex BuildRegex(string pattern, bool pathMode)
        {
            var sb = new StringBuilder("^");
            int i = 0;
            while (i < pattern.Length)
            {
                char c = pattern[i];
                if (c == '*')
                {
                    bool doubleStar = pathMode && i + 1 < pattern.Length && pattern[i + 1] == '*';
                    if (!doubleStar)
                    {
                        sb.Append(pathMode ? "[^/]*" : ".*");
                        i++;
                        continue;
                    }

                    // 吞掉连续的 *
                    int j = i;
                    while (j < pattern.Length && pattern[j] == '*')
                        j++;

                    bool atSegmentStart = i == 0 || pattern[i - 1] == '/';
                    bool followedBySlash = j < pattern.Length && pattern[j] == '/';
                    bool atEnd = j == pattern.Length;

                    if (atSegmentStart && followedBySlash)
                    {
                        // "**/" → 零或多级目录
                        sb.Append("(?:.*/)?");
                        i = j + 1;
                    }
                    else if (atEnd && i > 0 && pattern[i - 1] == '/' && sb.Length > 1 && sb[sb.Length - 1] == '/')
                    {
                        // "dir/**" → dir 本身或其下任意内容：回退已输出的 "/"
                        sb.Length -= 1; // 去掉 Regex.Escape("/") 输出的 "/"
                        sb.Append("(?:/.*)?");
                        i = j;
                    }
                    else
                    {
                        sb.Append(".*");
                        i = j;
                    }
                    continue;
                }

                if (c == '?')
                {
                    sb.Append(pathMode ? "[^/]" : ".");
                    i++;
                    continue;
                }

                sb.Append(Regex.Escape(c.ToString()));
                i++;
            }

            sb.Append('$');
            return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
        }

        /// <summary>
        /// 把工具参数里的路径规范为项目相对、正斜杠形式（折叠 <c>.</c> / <c>..</c>）。
        /// 项目外的绝对路径保留为规范化的绝对路径（不会命中 <c>Assets/**</c> 之类的规则）。
        /// </summary>
        /// <param name="raw">原始路径参数值。</param>
        /// <param name="projectRoot">项目根目录绝对路径（可为 null，此时不做相对化）。</param>
        public static string NormalizePath(string raw, string projectRoot)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            var p = raw.Trim().Replace('\\', '/');
            var root = string.IsNullOrEmpty(projectRoot) ? null : projectRoot.Replace('\\', '/').TrimEnd('/');

            bool isAbsolute = p.StartsWith("/", StringComparison.Ordinal) || (p.Length >= 2 && p[1] == ':');
            if (isAbsolute && root != null)
            {
                var collapsedAbs = CollapseSegments(p);
                if (collapsedAbs.Equals(root, StringComparison.OrdinalIgnoreCase))
                    return string.Empty;
                if (collapsedAbs.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
                    return collapsedAbs.Substring(root.Length + 1);
                return collapsedAbs;
            }

            return CollapseSegments(p);
        }

        private static string CollapseSegments(string path)
        {
            bool leadingSlash = path.StartsWith("/", StringComparison.Ordinal);
            var parts = path.Split('/');
            var stack = new List<string>(parts.Length);
            foreach (var part in parts)
            {
                if (part.Length == 0 || part == ".")
                    continue;
                if (part == "..")
                {
                    if (stack.Count > 0 && stack[stack.Count - 1] != "..")
                        stack.RemoveAt(stack.Count - 1);
                    else
                        stack.Add("..");
                    continue;
                }
                stack.Add(part);
            }
            var joined = string.Join("/", stack);
            return leadingSlash ? "/" + joined : joined;
        }

        /// <summary>
        /// 单条规则是否匹配本次调用（不看 enabled，不看 effect 合法性之外的东西）。
        /// </summary>
        /// <param name="rule">规则。</param>
        /// <param name="effect">规则效果（决定路径条件是"任一"还是"全部"）。</param>
        /// <param name="toolName">工具名。</param>
        /// <param name="action">action（可为空）。</param>
        /// <param name="paths">已规范化的项目相对路径（可为 null）。</param>
        public static bool Matches(PermissionRule rule, PermissionRuleEffect effect, string toolName, string action, IReadOnlyList<string> paths)
        {
            if (rule == null)
                return false;

            rule.GetToolAndActionPatterns(out var toolPattern, out var actionPattern);
            if (!GlobMatch(toolPattern, toolName ?? string.Empty, pathMode: false))
                return false;

            if (actionPattern.Length > 0 && !GlobMatch(actionPattern, action ?? string.Empty, pathMode: false))
                return false;

            var pathPattern = rule.path?.Trim();
            if (string.IsNullOrEmpty(pathPattern))
                return true;

            pathPattern = pathPattern.Replace('\\', '/');
            if (paths == null || paths.Count == 0)
                return false;

            bool any = false;
            bool all = true;
            foreach (var p in paths)
            {
                if (p == null)
                    continue;
                if (GlobMatch(pathPattern, p, pathMode: true))
                    any = true;
                else
                    all = false;
            }

            return effect == PermissionRuleEffect.Allow ? any && all : any;
        }

        /// <summary>
        /// 按 deny &gt; ask &gt; allow 评估全部规则，返回命中的最高优先级规则（同级取列表中第一条）。
        /// 无规则 / 无命中返回 <see cref="PermissionRuleMatch.None"/>。
        /// </summary>
        public static PermissionRuleMatch Evaluate(
            IReadOnlyList<ScopedPermissionRule> rules,
            string toolName,
            string action,
            IReadOnlyList<string> paths)
        {
            if (rules == null || rules.Count == 0)
                return PermissionRuleMatch.None;

            ScopedPermissionRule deny = null, ask = null, allow = null;
            foreach (var scoped in rules)
            {
                var rule = scoped?.Rule;
                if (rule == null || !rule.enabled || !rule.TryGetEffect(out var effect))
                    continue;

                switch (effect)
                {
                    case PermissionRuleEffect.Deny when deny == null:
                        if (Matches(rule, effect, toolName, action, paths)) deny = scoped;
                        break;
                    case PermissionRuleEffect.Ask when ask == null:
                        if (Matches(rule, effect, toolName, action, paths)) ask = scoped;
                        break;
                    case PermissionRuleEffect.Allow when allow == null:
                        if (Matches(rule, effect, toolName, action, paths)) allow = scoped;
                        break;
                }

                if (deny != null)
                    break;
            }

            if (deny != null)
                return new PermissionRuleMatch(deny.Rule, deny.Scope, PermissionRuleEffect.Deny);
            if (ask != null)
                return new PermissionRuleMatch(ask.Rule, ask.Scope, PermissionRuleEffect.Ask);
            if (allow != null)
                return new PermissionRuleMatch(allow.Rule, allow.Scope, PermissionRuleEffect.Allow);
            return PermissionRuleMatch.None;
        }
    }
}
