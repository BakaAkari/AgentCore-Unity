using System;
using System.Collections.Generic;
using UnityEditor;

namespace AgentCore.Editor.Tools.Safety
{
    /// <summary>
    /// 编辑器级的会话信任状态（Trust Low/Med、YOLO），所有工具调用来源共用：内嵌 Chat、MCP 等。
    /// <para>
    /// 以 <see cref="SessionState"/> 持久化：跨 Domain Reload 保留，Editor 重启归零（与 v1.6.5 起 Chat 的行为一致）。
    /// 加载在主线程完成（<see cref="EnsureLoaded"/>，由 <c>InitializeOnLoadMethod</c> 在每次域加载后调用）；
    /// 读取（<see cref="IsYoloActive"/> / <see cref="IsTrusted"/>）线程安全，可在后台线程调用。
    /// </para>
    /// </summary>
    public static class ToolTrustState
    {
        /// <summary>SessionState 键（沿用 Chat 原来的键，升级后已有的信任状态不丢）。</summary>
        internal const string SessionStateKey = "AgentCore.SessionTrustScopes";

        private static readonly object Gate = new object();
        private static readonly HashSet<ToolConfirmationTrustScope> Scopes = new HashSet<ToolConfirmationTrustScope>();
        private static bool _loaded;

        /// <summary>持久化读取，测试可替换。</summary>
        internal static Func<string> LoadRaw = () => SessionState.GetString(SessionStateKey, string.Empty);

        /// <summary>持久化写入（空字符串表示清除），测试可替换。</summary>
        internal static Action<string> SaveRaw = raw =>
        {
            if (string.IsNullOrEmpty(raw))
                SessionState.EraseString(SessionStateKey);
            else
                SessionState.SetString(SessionStateKey, raw);
        };

        /// <summary>信任状态变化（在调用 <see cref="Add"/> / <see cref="Clear"/> 的线程上触发，通常是主线程）。</summary>
        public static event Action Changed;

        [InitializeOnLoadMethod]
        private static void InitializeOnLoad()
        {
            EnsureLoaded();
        }

        /// <summary>从持久化存储加载一次（主线程）。重复调用无副作用。</summary>
        public static void EnsureLoaded()
        {
            lock (Gate)
            {
                if (_loaded)
                    return;
                Scopes.Clear();
                foreach (var scope in Parse(LoadRaw?.Invoke()))
                    Scopes.Add(scope);
                _loaded = true;
            }
        }

        /// <summary>当前是否 YOLO（所有工具直通，含 deny 规则）。</summary>
        public static bool IsYoloActive
        {
            get
            {
                lock (Gate)
                    return Scopes.Contains(ToolConfirmationTrustScope.SessionAll);
            }
        }

        /// <summary>当前激活的信任范围快照。</summary>
        public static IReadOnlyCollection<ToolConfirmationTrustScope> Snapshot()
        {
            lock (Gate)
                return new List<ToolConfirmationTrustScope>(Scopes);
        }

        /// <summary>该请求是否已被某个激活的信任范围直通（规则见 <see cref="ToolConfirmationTrust.IsTrusted"/>）。</summary>
        public static bool IsTrusted(ToolConfirmationRequest request)
        {
            HashSet<ToolConfirmationTrustScope> copy;
            lock (Gate)
                copy = new HashSet<ToolConfirmationTrustScope>(Scopes);
            return ToolConfirmationTrust.IsTrusted(request, copy);
        }

        /// <summary>激活一个信任范围并持久化（主线程）。</summary>
        public static void Add(ToolConfirmationTrustScope scope)
        {
            bool changed;
            string raw;
            lock (Gate)
            {
                _loaded = true;
                changed = Scopes.Add(scope);
                raw = Serialize(Scopes);
            }
            if (!changed)
                return;
            SaveRaw?.Invoke(raw);
            Changed?.Invoke();
        }

        /// <summary>清除全部信任范围并持久化（主线程）。</summary>
        public static void Clear()
        {
            bool changed;
            lock (Gate)
            {
                _loaded = true;
                changed = Scopes.Count > 0;
                Scopes.Clear();
            }
            SaveRaw?.Invoke(string.Empty);
            if (changed)
                Changed?.Invoke();
        }

        internal static IEnumerable<ToolConfirmationTrustScope> Parse(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                yield break;
            foreach (var token in raw.Split(','))
            {
                if (Enum.TryParse<ToolConfirmationTrustScope>(token.Trim(), out var scope))
                    yield return scope;
            }
        }

        private static string Serialize(HashSet<ToolConfirmationTrustScope> scopes) =>
            scopes.Count == 0 ? string.Empty : string.Join(",", scopes);

        /// <summary>测试用：丢弃内存状态，下次 <see cref="EnsureLoaded"/> 重新加载。</summary>
        internal static void ResetForTests()
        {
            lock (Gate)
            {
                Scopes.Clear();
                _loaded = false;
            }
        }
    }

    /// <summary>
    /// 读 <see cref="ToolTrustState"/> 的 YOLO 状态，供不经确认 UI 的 dispatcher 判断"YOLO 绕过 deny 规则"。
    /// </summary>
    public sealed class SharedToolSessionTrustState : IToolSessionTrustState
    {
        public bool IsYoloActive => ToolTrustState.IsYoloActive;
    }
}
