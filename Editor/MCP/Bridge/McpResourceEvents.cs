using System;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Components.MCP.Bridge
{
    /// <summary>
    /// 资源变化事件：Console 有新日志、场景层级变化、工程文件变化时，向已连接的 relay 推送
    /// <c>event { kind: "resourceUpdated", uri }</c>，由 relay 转成 <c>notifications/resources/updated</c> 发给订阅了该资源的客户端。
    /// 同一 URI 至多每 <see cref="Throttle"/> 推送一次（日志刷屏时不淹没客户端）。
    /// </summary>
    public static class McpResourceEvents
    {
        public static readonly TimeSpan Throttle = TimeSpan.FromSeconds(1);

        private static readonly object Gate = new object();
        private static readonly HashSet<string> Dirty = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, DateTime> LastSent = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private static Action<JObject> _broadcast;
        private static Timer _timer;

        /// <summary>开始监听（主线程）。<paramref name="broadcast"/> 把消息发给所有已连接的 relay。</summary>
        public static void Start(Action<JObject> broadcast)
        {
            _broadcast = broadcast;
            Application.logMessageReceivedThreaded -= OnLog;
            Application.logMessageReceivedThreaded += OnLog;
            EditorApplication.hierarchyChanged -= OnHierarchyChanged;
            EditorApplication.hierarchyChanged += OnHierarchyChanged;
            EditorApplication.projectChanged -= OnProjectChanged;
            EditorApplication.projectChanged += OnProjectChanged;
            _timer?.Dispose();
            _timer = new Timer(_ => Flush(), null, 250, 250);
        }

        public static void Stop()
        {
            Application.logMessageReceivedThreaded -= OnLog;
            EditorApplication.hierarchyChanged -= OnHierarchyChanged;
            EditorApplication.projectChanged -= OnProjectChanged;
            _timer?.Dispose();
            _timer = null;
            _broadcast = null;
        }

        private static void OnLog(string condition, string stackTrace, LogType type) => MarkDirty(McpResourceCatalog.ConsoleUri);

        private static void OnHierarchyChanged() => MarkDirty(McpResourceCatalog.SceneHierarchyUri);

        private static void OnProjectChanged()
        {
            MarkDirty(McpResourceCatalog.AssetUriPrefix + "*");
            MarkDirty(McpResourceCatalog.VcsStatusUri);
        }

        public static void MarkDirty(string uri)
        {
            lock (Gate)
                Dirty.Add(uri);
        }

        /// <summary>把到期的脏 URI 推送出去（定时器线程；测试可直接调用）。</summary>
        public static void Flush()
        {
            var broadcast = _broadcast;
            if (broadcast == null)
                return;

            var due = new List<string>();
            var now = DateTime.UtcNow;
            lock (Gate)
            {
                foreach (var uri in Dirty)
                {
                    if (!LastSent.TryGetValue(uri, out var last) || now - last >= Throttle)
                        due.Add(uri);
                }
                foreach (var uri in due)
                {
                    Dirty.Remove(uri);
                    LastSent[uri] = now;
                }
            }

            foreach (var uri in due)
            {
                try
                {
                    broadcast(new JObject { ["type"] = "event", ["kind"] = "resourceUpdated", ["uri"] = uri });
                }
                catch (Exception)
                {
                    // 连接正在断开：丢弃
                }
            }
        }

        /// <summary>测试用：清空状态。</summary>
        internal static void ResetForTests()
        {
            lock (Gate)
            {
                Dirty.Clear();
                LastSent.Clear();
            }
        }
    }
}
