using System;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.UI
{
    internal class SessionTagInputDialog : EditorWindow
    {
        private string _value = "";
        private Action<string> _onConfirm;
        private string _prompt = "";

        public static void Show(string title, string prompt, Action<string> onConfirm)
        {
            var win = CreateInstance<SessionTagInputDialog>();
            win.titleContent = new GUIContent(title);
            win._onConfirm = onConfirm;
            win._prompt = prompt;
            var w = 320f; var h = 110f;
            var res = Screen.currentResolution;
            win.position = new Rect((res.width - w) / 2f, (res.height - h) / 2f, w, h);
            win.ShowUtility();
        }

        void OnGUI()
        {
            EditorGUILayout.LabelField(_prompt);
            GUI.SetNextControlName("SessionTagInput");
            _value = EditorGUILayout.TextField(_value);
            EditorGUI.FocusTextInControl("SessionTagInput");
            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Cancel", GUILayout.Width(80))) Close();
                if (GUILayout.Button("OK", GUILayout.Width(80)))
                {
                    var trimmed = string.IsNullOrWhiteSpace(_value) ? null : _value.Trim();
                    _onConfirm?.Invoke(trimmed);
                    Close();
                }
            }

            // Handle Enter/Escape
            var e = Event.current;
            if (e.type == EventType.KeyDown)
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                {
                    var trimmed = string.IsNullOrWhiteSpace(_value) ? null : _value.Trim();
                    _onConfirm?.Invoke(trimmed);
                    Close();
                    e.Use();
                }
                else if (e.keyCode == KeyCode.Escape)
                {
                    Close();
                    e.Use();
                }
            }
        }
    }
}
