using AgentCore.Editor.UI.Context;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI
{
    /// <summary>
    /// Context Ingest 入口（快捷键 Ctrl+Shift+X）。
    ///
    /// 行为：
    /// 1. 全局快捷键触发时，读取当前 Unity 焦点 + Selection 状态
    /// 2. 路由到最合适的 Collector（Selection / Asset / Console / Scene）
    /// 3. 采集结果格式化为 markdown 块
    /// 4. 追加到 ChatWindow 输入框光标位置（不清除已有输入）
    /// 5. 如 ChatWindow 未打开则自动打开并聚焦输入框
    ///
    /// 快捷键区别：
    ///   Ctrl+Shift+Q → 打开 ChatWindow（<see cref="ShowWindow"/>）
    ///   Ctrl+Shift+E → 导出当前会话（在 ChatWindow 内响应）
    ///   Ctrl+Shift+X → Context 注入（全局，本方法）
    /// </summary>
    public partial class ChatWindow
    {
        /// <summary>
        /// 全局 Context Ingest 快捷键入口。
        /// Unity <see cref="ShortcutAttribute"/> 使快捷键在任意 EditorWindow 焦点时都能触发。
        /// </summary>
        [Shortcut("AgentCore/Ingest Context", KeyCode.X, ShortcutModifiers.Shift | ShortcutModifiers.Action)]
        private static void IngestContextShortcut()
        {
            ContextIngestEntry.Invoke();
        }
    }

    /// <summary>
    /// Context Ingest 的实际执行逻辑，独立于 ChatWindow 静态入口，方便测试与复用。
    /// </summary>
    internal static class ContextIngestEntry
    {
        /// <summary>
        /// 执行 Context 注入流程。
        /// 主线程调用（Unity ShortcutManager 保证）。
        /// </summary>
        public static void Invoke()
        {
            // 1. 采集
            ContextIngestResult result;
            try
            {
                result = ContextIngestRouter.Route();
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[AgentCore] Context ingest collect failed: {ex.Message}");
                return;
            }

            if (result == null || result.IsEmpty)
            {
                // 完全无上下文 → 静默（避免噪音）
                // 但依然把窗口显示出来，方便用户接下来手打
                EnsureChatWindow(focusInput: true);
                return;
            }

            // 2. 格式化
            var markdown = ContextIngestFormatter.Format(result);

            // 3. 确保窗口打开并拿到实例
            var window = EnsureChatWindow(focusInput: false);
            if (window == null)
            {
                Debug.LogWarning("[AgentCore] Context ingest: failed to open ChatWindow.");
                return;
            }

            // 4. 注入
            window.AppendToInputField(markdown);

            // 5. Warning toast（在 Console 中提示，避免破坏输入焦点）
            if (!string.IsNullOrEmpty(result.Warning))
            {
                AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore][ContextIngest] {result.Warning}");
            }
            else
            {
                AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore][ContextIngest] Injected \"{result.Label}\" (~{result.EstimatedTokens} tokens).");
            }
        }

        /// <summary>
        /// 确保 ChatWindow 存在并显示。
        /// 已打开 → 聚焦；未打开 → 创建。
        /// </summary>
        private static ChatWindow EnsureChatWindow(bool focusInput)
        {
            // 尝试查找已存在的实例（避免重复创建）
            var existing = Resources.FindObjectsOfTypeAll<ChatWindow>();
            ChatWindow window;
            if (existing != null && existing.Length > 0)
            {
                window = existing[0];
                window.Show();
                window.Focus();
            }
            else
            {
                window = EditorWindow.GetWindow<ChatWindow>();
                window.titleContent = new GUIContent(
                    "AgentCore",
                    EditorGUIUtility.IconContent("d_console.infoicon.sml").image);
                window.Show();
                window.Focus();
            }

            if (focusInput)
            {
                window.FocusInputField();
            }
            return window;
        }
    }
}
