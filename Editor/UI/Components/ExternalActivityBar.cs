using System;
using System.Linq;
using AgentCore.Editor.Core;
using AgentCore.Editor.Utils;
using UnityEngine;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI.Components
{
    /// <summary>
    /// Chat 窗口里的外部活动条：外部 MCP 客户端正在执行工具时显示“claude-code 正在执行 manage_scene …”，
    /// 结束后保留 <see cref="LingerSeconds"/> 秒显示最近一次结果；没有外部活动时隐藏。点击“活动记录”打开外部活动窗口。
    /// </summary>
    public sealed class ExternalActivityBar : VisualElement
    {
        private const double LingerSeconds = 15;

        private readonly Label _label;
        private IVisualElementScheduledItem _ticker;

        public ExternalActivityBar()
        {
            name = "external-activity-bar";
            style.flexDirection = FlexDirection.Row;
            style.alignItems = Align.Center;
            style.paddingLeft = 8;
            style.paddingRight = 4;
            style.paddingTop = 2;
            style.paddingBottom = 2;
            style.display = DisplayStyle.None;

            _label = new Label { name = "external-activity-label" };
            _label.style.flexGrow = 1;
            _label.style.flexShrink = 1;
            _label.style.overflow = Overflow.Hidden;
            _label.style.textOverflow = TextOverflow.Ellipsis;
            _label.style.whiteSpace = WhiteSpace.NoWrap;
            _label.style.fontSize = 11;
            _label.style.color = new Color(0.55f, 0.75f, 0.95f);
            Add(_label);

            var open = new Button(ExternalActivityWindow.ShowWindow) { text = "活动记录" };
            open.tooltip = "查看外部客户端（MCP）的调用记录";
            open.style.fontSize = 10;
            Add(open);

            RegisterCallback<AttachToPanelEvent>(_ =>
            {
                ExternalToolActivity.Changed += OnChanged;
                Refresh();
            });
            RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                ExternalToolActivity.Changed -= OnChanged;
                _ticker?.Pause();
            });
        }

        private void OnChanged() => MainThread.Post(Refresh);

        private void Refresh()
        {
            var records = ExternalToolActivity.Snapshot();
            var running = records.Where(r => r.IsRunning).ToList();
            string text = null;

            if (running.Count > 0)
            {
                var first = running.Last(); // 最早开始的那个
                text = $"● {first.Source} 正在执行 {Describe(first)}（{first.ElapsedSeconds:0}s）";
                if (running.Count > 1)
                    text += $"，另有 {running.Count - 1} 个调用";
            }
            else
            {
                var last = records.FirstOrDefault();
                if (last?.FinishedAt != null && (DateTime.Now - last.FinishedAt.Value).TotalSeconds < LingerSeconds)
                    text = $"{last.Source} 完成 {Describe(last)}：{StatusText(last.Status)}";
            }

            if (text == null)
            {
                style.display = DisplayStyle.None;
                _ticker?.Pause();
                return;
            }

            _label.text = text;
            style.display = DisplayStyle.Flex;
            if (_ticker == null)
                _ticker = schedule.Execute(Refresh).Every(1000);
            else
                _ticker.Resume();
        }

        private static string Describe(ExternalToolActivity.Record record) =>
            record.Tool + (string.IsNullOrEmpty(record.Action) ? string.Empty : " / " + record.Action);

        private static string StatusText(string status)
        {
            switch (status)
            {
                case "ok": return "成功";
                case "partial": return "部分完成";
                case "cancelled": return "已取消";
                default: return "失败";
            }
        }
    }
}
