using System;
using AgentCore.Editor.Core;
using AgentCore.Editor.Utils;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.UI
{
    /// <summary>
    /// 外部活动：MCP 客户端（Claude Code、Codex 等）在本编辑器里调用了哪些工具、结果如何。
    /// 数据来自 <see cref="ExternalToolActivity"/>（内存中最近 200 条）；完整记录见工程的 Library/AgentCore/Mcp/audit.jsonl。
    /// </summary>
    public sealed class ExternalActivityWindow : EditorWindow
    {
        private Vector2 _listScroll;
        private Vector2 _detailScroll;
        private string _selectedId;
        private bool _subscribed;

        [MenuItem("Window/AgentCore External Activity")]
        public static void ShowWindow()
        {
            var window = GetWindow<ExternalActivityWindow>();
            window.titleContent = new GUIContent("AgentCore External Activity");
            window.minSize = new Vector2(420, 240);
            window.Show();
        }

        private void OnEnable()
        {
            if (_subscribed)
                return;
            ExternalToolActivity.Changed += OnChanged;
            _subscribed = true;
        }

        private void OnDisable()
        {
            ExternalToolActivity.Changed -= OnChanged;
            _subscribed = false;
        }

        private void OnChanged() => MainThread.Post(Repaint);

        private void OnGUI()
        {
            var records = ExternalToolActivity.Snapshot();

            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                var running = 0;
                foreach (var r in records)
                {
                    if (r.IsRunning)
                        running++;
                }
                GUILayout.Label(running > 0 ? $"{running} running · {records.Count} recent" : $"{records.Count} recent calls", EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Open audit log", EditorStyles.toolbarButton))
                {
                    var path = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "Library", "AgentCore", "Mcp", "audit.jsonl");
                    if (System.IO.File.Exists(path))
                        EditorUtility.RevealInFinder(path);
                    else
                        ShowNotification(new GUIContent("No audit log yet"));
                }
                if (GUILayout.Button("Clear", EditorStyles.toolbarButton))
                {
                    ExternalToolActivity.Clear();
                    _selectedId = null;
                }
            }

            if (records.Count == 0)
            {
                EditorGUILayout.HelpBox("No calls from external clients yet. Enable MCP Server in Project Settings > AgentCore > Tools & Extensions and connect Claude Code or Codex.", MessageType.Info);
                return;
            }

            _listScroll = EditorGUILayout.BeginScrollView(_listScroll, GUILayout.ExpandHeight(true));
            ExternalToolActivity.Record selected = null;
            foreach (var record in records)
            {
                if (record.Id == _selectedId)
                    selected = record;
                var label = $"{record.StartedAt:HH:mm:ss}  {StatusMark(record)}  {record.Source}  →  {record.Tool}" +
                            (string.IsNullOrEmpty(record.Action) ? string.Empty : " / " + record.Action) +
                            $"   ({record.ElapsedSeconds:0.0}s)";
                var style = record.Id == _selectedId ? EditorStyles.boldLabel : EditorStyles.label;
                if (GUILayout.Button(label, style))
                    _selectedId = record.Id;
            }
            EditorGUILayout.EndScrollView();

            if (selected != null)
            {
                EditorGUILayout.Space();
                _detailScroll = EditorGUILayout.BeginScrollView(_detailScroll, GUILayout.Height(140));
                EditorGUILayout.LabelField("Arguments", EditorStyles.boldLabel);
                EditorGUILayout.SelectableLabel(selected.ArgumentsSummary ?? string.Empty, EditorStyles.wordWrappedLabel, GUILayout.MinHeight(30));
                EditorGUILayout.LabelField("Result", EditorStyles.boldLabel);
                EditorGUILayout.SelectableLabel(selected.IsRunning ? "(running)" : selected.ResultPreview ?? string.Empty, EditorStyles.wordWrappedLabel, GUILayout.MinHeight(50));
                EditorGUILayout.EndScrollView();
            }

            // 有调用在执行时保持计时刷新
            foreach (var record in records)
            {
                if (record.IsRunning)
                {
                    Repaint();
                    break;
                }
            }
        }

        private static string StatusMark(ExternalToolActivity.Record record)
        {
            switch (record.Status)
            {
                case "running": return "…";
                case "ok": return "✓";
                case "partial": return "◐";
                case "cancelled": return "⊘";
                default: return "✗";
            }
        }
    }
}
