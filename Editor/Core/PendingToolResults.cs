using System;
using System.Collections.Generic;
using AgentCore.Editor.LLM;
using AgentCore.Editor.Tools;
using Newtonsoft.Json;
using UnityEditor;

namespace AgentCore.Editor.Core
{
    /// <summary>
    /// 跨域重载暂存本批工具调用的执行进度与结果（SessionState，域重载后仍在，关闭编辑器即清空）。
    /// <para>
    /// 工具结果要等整批执行完、再等编译结束后才写入历史；编译成功触发的域重载恰好发生在这段等待里，
    /// 原先的结果全部丢失，恢复时只能告诉模型"结果未知，请重试"——写文件、追加方法这类非幂等操作
    /// 就会被重复执行。这里在每个调用开始与结束时落一条记录，恢复时用真实结果回填；
    /// 只开始未结束的调用明确告诉模型"副作用可能已生效，先检查再决定是否重试"。
    /// </para>
    /// </summary>
    internal static class PendingToolResults
    {
        private const string StateKey = "AgentCore_PendingToolResults";

        /// <summary>单条结果的保存上限，避免巨型输出撑大 SessionState。</summary>
        private const int MaxContentChars = 200_000;

        public enum CallStatus
        {
            Started,
            Completed
        }

        public class Entry
        {
            public string ToolCallId;
            public string ToolName;
            public CallStatus Status;
            public bool Success;
            public string Content;
        }

        private class Store
        {
            public string SessionId;
            public List<Entry> Entries = new List<Entry>();
        }

        /// <summary>开始一批调用：覆盖旧记录，每个调用先记为 Started。</summary>
        public static void Begin(string sessionId, IReadOnlyList<ToolCall> toolCalls)
        {
            var store = new Store { SessionId = sessionId };
            if (toolCalls != null)
            {
                foreach (var call in toolCalls)
                {
                    if (call == null || string.IsNullOrEmpty(call.Id))
                        continue;
                    store.Entries.Add(new Entry
                    {
                        ToolCallId = call.Id,
                        ToolName = call.Function?.Name,
                        Status = CallStatus.Started
                    });
                }
            }
            Write(store);
        }

        /// <summary>一个调用执行完毕：记录发给模型的结果内容。</summary>
        public static void Complete(ToolCallResult result)
        {
            var id = result?.ToolCall?.Id;
            if (string.IsNullOrEmpty(id))
                return;

            var store = Read();
            if (store == null)
                return;

            var entry = store.Entries.Find(e => e.ToolCallId == id);
            if (entry == null)
                return;

            var content = result.Result?.GetContentForLLM() ?? string.Empty;
            if (content.Length > MaxContentChars)
                content = content.Substring(0, MaxContentChars) + "\n[AgentCore] Result truncated during domain reload recovery.";

            entry.Status = CallStatus.Completed;
            entry.Success = result.Result?.Success ?? false;
            entry.Content = content;
            Write(store);
        }

        /// <summary>本批是否所有调用都已执行完（只差等待编译）。</summary>
        public static bool AllCompleted(string sessionId)
        {
            var store = Read();
            return store != null && store.SessionId == sessionId && store.Entries.Count > 0 &&
                   store.Entries.TrueForAll(e => e.Status == CallStatus.Completed);
        }

        /// <summary>取出属于该会话的记录并清空；会话不匹配时返回空表。</summary>
        public static Dictionary<string, Entry> Take(string sessionId)
        {
            var store = Read();
            Clear();

            var map = new Dictionary<string, Entry>();
            if (store == null || store.SessionId != sessionId)
                return map;
            foreach (var entry in store.Entries)
                map[entry.ToolCallId] = entry;
            return map;
        }

        public static void Clear()
        {
            SessionState.EraseString(StateKey);
        }

        private static Store Read()
        {
            var json = SessionState.GetString(StateKey, string.Empty);
            if (string.IsNullOrEmpty(json))
                return null;
            try
            {
                return JsonConvert.DeserializeObject<Store>(json);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void Write(Store store)
        {
            SessionState.SetString(StateKey, JsonConvert.SerializeObject(store));
        }
    }
}
