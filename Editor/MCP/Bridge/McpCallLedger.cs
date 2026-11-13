using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentCore.Editor.Components.MCP.Bridge
{
    /// <summary>
    /// 跟踪每个 relay 的调用，保证结果送达同一个 relay —— 即使中途连接断开、甚至跨越 Domain Reload。
    /// <para>
    /// 结果交付：relay 当前连接可用时直接发送，否则放进待取区；relay 重连后发 <c>resume { ids }</c> 取回。
    /// Domain Reload 前 <see cref="Serialize"/> 把未完成的调用写成结果（执行中被打断 → 错误说明；等待编译 → 原结果 + 编译成功说明），
    /// 重载后 <see cref="Restore"/> 放回待取区。不依赖 Unity API，可单测。
    /// </para>
    /// </summary>
    public sealed class McpCallLedger
    {
        public enum Phase
        {
            Executing,
            AwaitingCompile,
            Done
        }

        private sealed class Entry
        {
            public string RelayId;
            public string Id;
            public string Name;
            public Phase Phase;
            public JObject Message;
            public DateTime UpdatedAt;
            public CancellationTokenSource Cts;
        }

        /// <summary>待取结果的保留时间：relay 不再回来（客户端关闭）时最终丢弃。</summary>
        public static TimeSpan RetainResults = TimeSpan.FromMinutes(10);

        private readonly object _gate = new object();
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>();
        private readonly Dictionary<string, McpBridgeConnection> _connections = new Dictionary<string, McpBridgeConnection>();

        private static string Key(string relayId, string id) => relayId + "/" + id;

        public void Attach(McpBridgeConnection connection)
        {
            if (connection?.RelayId == null)
                return;
            lock (_gate)
                _connections[connection.RelayId] = connection;
        }

        public void Detach(McpBridgeConnection connection)
        {
            if (connection?.RelayId == null)
                return;
            lock (_gate)
            {
                if (_connections.TryGetValue(connection.RelayId, out var current) && ReferenceEquals(current, connection))
                    _connections.Remove(connection.RelayId);
            }
        }

        /// <summary>登记一次调用；返回其取消令牌。同一 relay 重复的 id 视为新调用（覆盖旧记录）。</summary>
        public CancellationToken Begin(string relayId, string id, string name)
        {
            var entry = new Entry
            {
                RelayId = relayId,
                Id = id,
                Name = name,
                Phase = Phase.Executing,
                UpdatedAt = DateTime.UtcNow,
                Cts = new CancellationTokenSource()
            };
            lock (_gate)
                _entries[Key(relayId, id)] = entry;
            return entry.Cts.Token;
        }

        /// <summary>工具已返回、正在等待它触发的编译：先记下结果，重载打断等待时用它答复。</summary>
        public void AwaitCompile(string relayId, string id, JObject heldMessage)
        {
            lock (_gate)
            {
                if (!_entries.TryGetValue(Key(relayId, id), out var entry))
                    return;
                entry.Phase = Phase.AwaitingCompile;
                entry.Message = heldMessage;
                entry.UpdatedAt = DateTime.UtcNow;
            }
        }

        public void Cancel(string relayId, string id)
        {
            CancellationTokenSource cts = null;
            lock (_gate)
            {
                if (_entries.TryGetValue(Key(relayId, id), out var entry))
                {
                    cts = entry.Cts;
                    _entries.Remove(Key(relayId, id));
                }
            }
            try { cts?.Cancel(); } catch (ObjectDisposedException) { /* finished */ }
        }

        /// <summary>调用完成：送到该 relay 的当前连接；不可用时留待 resume。已被取消的调用不再答复。</summary>
        public void Complete(string relayId, string id, JObject message)
        {
            McpBridgeConnection connection;
            lock (_gate)
            {
                var key = Key(relayId, id);
                if (!_entries.TryGetValue(key, out var entry))
                    return; // 已取消
                entry.Phase = Phase.Done;
                entry.Message = message;
                entry.UpdatedAt = DateTime.UtcNow;
                entry.Cts?.Dispose();
                entry.Cts = null;
                _connections.TryGetValue(relayId, out connection);
            }

            if (connection != null && !connection.IsClosed && connection.Send(message))
            {
                lock (_gate)
                    _entries.Remove(Key(relayId, id));
            }
        }

        /// <summary>
        /// relay 重连后取回 <paramref name="ids"/> 的结果：已完成的立即发送，仍在执行的完成后发送；
        /// 不认识的 id 用一条 <c>unknownCalls</c> 告诉 relay（断开前没送达 bridge，或编辑器重启过 —— 由 relay 按编辑器 PID 区分）。
        /// </summary>
        public void Resume(McpBridgeConnection connection, IEnumerable<string> ids)
        {
            Attach(connection);
            var unknown = new JArray();
            foreach (var id in ids ?? Enumerable.Empty<string>())
            {
                JObject message = null;
                var known = false;
                lock (_gate)
                {
                    if (_entries.TryGetValue(Key(connection.RelayId, id), out var entry))
                    {
                        known = true;
                        if (entry.Phase == Phase.Done)
                        {
                            message = entry.Message;
                            _entries.Remove(Key(connection.RelayId, id));
                        }
                    }
                }

                if (message != null)
                    connection.Send(message);
                else if (!known)
                    unknown.Add(id);
            }

            if (unknown.Count > 0)
                connection.Send(new JObject { ["type"] = McpBridgeProtocol.UnknownCalls, ["ids"] = unknown });
        }

        /// <summary>Domain Reload 前：所有未交付的调用转成结果写出（JSON）。</summary>
        public string Serialize()
        {
            var array = new JArray();
            var cutoff = DateTime.UtcNow - RetainResults;
            lock (_gate)
            {
                foreach (var entry in _entries.Values)
                {
                    if (entry.UpdatedAt < cutoff)
                        continue;
                    JObject message;
                    switch (entry.Phase)
                    {
                        case Phase.Executing:
                            message = ErrorMessage(entry.Id,
                                $"Unity reloaded scripts while '{entry.Name}' was running, so the call was interrupted before it returned. " +
                                "Check the project state before retrying.");
                            break;
                        case Phase.AwaitingCompile:
                            message = (JObject)entry.Message.DeepClone();
                            message["text"] = (string)message["text"] +
                                              "\n\nUnity compiled the scripts and reloaded successfully. Use read_console to check for warnings.";
                            break;
                        default:
                            message = entry.Message;
                            break;
                    }
                    array.Add(new JObject { ["relayId"] = entry.RelayId, ["id"] = entry.Id, ["name"] = entry.Name, ["message"] = message });
                }
            }
            return array.ToString(Formatting.None);
        }

        /// <summary>重载后：把 <see cref="Serialize"/> 写出的结果放回待取区。</summary>
        public void Restore(string json)
        {
            if (string.IsNullOrEmpty(json))
                return;
            JArray array;
            try
            {
                array = JArray.Parse(json);
            }
            catch (JsonException)
            {
                return;
            }

            lock (_gate)
            {
                foreach (var item in array.OfType<JObject>())
                {
                    var relayId = (string)item["relayId"];
                    var id = (string)item["id"];
                    if (relayId == null || id == null || !(item["message"] is JObject message))
                        continue;
                    _entries[Key(relayId, id)] = new Entry
                    {
                        RelayId = relayId,
                        Id = id,
                        Name = (string)item["name"],
                        Phase = Phase.Done,
                        Message = message,
                        UpdatedAt = DateTime.UtcNow
                    };
                }
            }
        }

        /// <summary>丢弃过期的待取结果。</summary>
        public void Prune()
        {
            var cutoff = DateTime.UtcNow - RetainResults;
            lock (_gate)
            {
                foreach (var key in _entries.Where(p => p.Value.Phase == Phase.Done && p.Value.UpdatedAt < cutoff).Select(p => p.Key).ToList())
                    _entries.Remove(key);
            }
        }

        public int PendingCount
        {
            get { lock (_gate) return _entries.Count; }
        }

        public static JObject ErrorMessage(string id, string text) => new JObject
        {
            ["type"] = McpBridgeProtocol.Result,
            ["id"] = id,
            ["status"] = McpBridgeProtocol.StatusError,
            ["text"] = text,
            ["images"] = new JArray()
        };
    }
}
