using System;
using System.Collections.Generic;
using System.Linq;

namespace AgentCore.Editor.Core
{
    /// <summary>
    /// 外部调用来源（MCP 客户端等）的工具调用记录：Chat 窗口的活动条与“外部活动”窗口据此显示外部 agent 正在做什么、做过什么。
    /// 线程安全；<see cref="Changed"/> 可能在任意线程触发，UI 订阅方需自行切回主线程。只保留最近 <see cref="Capacity"/> 条。
    /// </summary>
    public static class ExternalToolActivity
    {
        public const int Capacity = 200;

        public sealed class Record
        {
            public string Id;
            public string Source;
            public string Tool;
            public string Action;
            public string ArgumentsSummary;
            public DateTime StartedAt;
            public DateTime? FinishedAt;

            /// <summary>running / ok / partial / error / cancelled</summary>
            public string Status = "running";
            public string ResultPreview;

            public bool IsRunning => FinishedAt == null;

            public double ElapsedSeconds => ((FinishedAt ?? DateTime.Now) - StartedAt).TotalSeconds;
        }

        private static readonly object Gate = new object();
        private static readonly LinkedList<Record> Records = new LinkedList<Record>();

        /// <summary>有调用开始或结束（任意线程）。</summary>
        public static event Action Changed;

        public static Record Begin(string source, string tool, string action, string argumentsSummary)
        {
            var record = new Record
            {
                Id = Guid.NewGuid().ToString("N"),
                Source = string.IsNullOrEmpty(source) ? "external client" : source,
                Tool = tool,
                Action = action,
                ArgumentsSummary = argumentsSummary,
                StartedAt = DateTime.Now,
            };
            lock (Gate)
            {
                Records.AddFirst(record);
                while (Records.Count > Capacity)
                    Records.RemoveLast();
            }
            Raise();
            return record;
        }

        public static void End(Record record, string status, string resultPreview)
        {
            if (record == null)
                return;
            lock (Gate)
            {
                record.Status = string.IsNullOrEmpty(status) ? "ok" : status;
                record.ResultPreview = resultPreview;
                record.FinishedAt = DateTime.Now;
            }
            Raise();
        }

        /// <summary>最近的记录（新的在前）。</summary>
        public static IReadOnlyList<Record> Snapshot()
        {
            lock (Gate)
                return Records.ToList();
        }

        public static IReadOnlyList<Record> Running()
        {
            lock (Gate)
                return Records.Where(r => r.IsRunning).ToList();
        }

        public static void Clear()
        {
            lock (Gate)
                Records.Clear();
            Raise();
        }

        private static void Raise()
        {
            try
            {
                Changed?.Invoke();
            }
            catch (Exception)
            {
                // 订阅方的异常不影响调用
            }
        }
    }
}
