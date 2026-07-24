using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.L10n
{
    /// <summary>
    /// AgentCore 编辑器 UI 语言管理器 (Editor-only 单例).
    /// </summary>
    /// <remarks>
    /// <para>
    /// 职责: 维护当前 UI 语言, 持久化到 <see cref="EditorPrefs"/> (全局, 跨项目共享),
    /// 发布 <see cref="LanguageChanged"/> 事件通知 UI 层热刷新.
    /// </para>
    /// <para>
    /// 设计原则:
    /// - 语言是"用户偏好"而非"项目属性", 因此存 EditorPrefs 而不是 ProjectSettings.
    /// - 只本地化"用户直接看到的 UI 文本", 不本地化 LLM 系统提示 / 工具错误 / 日志.
    /// - LLM 回复语言默认跟随 UI 语言, 可在设置里独立覆盖.
    /// </para>
    /// </remarks>
    public static class LanguageManager
    {
        /// <summary>EditorPrefs 键名, 存当前语言 code.</summary>
        private const string PrefsKey = "AgentCore.L10n.Language";

        /// <summary>EditorPrefs 键名, 存 LLM 回复是否跟随 UI 语言.</summary>
        private const string LlmFollowUiPrefsKey = "AgentCore.L10n.LlmFollowUi";

        /// <summary>默认语言 (首次启动或读取失败时使用).</summary>
        public const string DefaultLanguage = "en-US";

        /// <summary>默认 LLM 语言跟随策略: 跟随 UI 语言.</summary>
        public const bool DefaultLlmFollowUi = true;

        /// <summary>支持的语言列表 (顺序 = UI 下拉展示顺序).</summary>
        public static readonly IReadOnlyList<LanguageOption> SupportedLanguages = new List<LanguageOption>
        {
            new LanguageOption("en-US", "English"),
            new LanguageOption("zh-CN", "简体中文"),
        };

        private static string _currentLanguage;

        /// <summary>
        /// 语言变更事件. 所有需要热刷新的 EditorWindow 应订阅并重建 UI.
        /// </summary>
        public static event Action<string> LanguageChanged;

        /// <summary>当前语言 code (如 "en-US", "zh-CN").</summary>
        public static string CurrentLanguage
        {
            get
            {
                if (_currentLanguage == null)
                {
                    _currentLanguage = EditorPrefs.GetString(PrefsKey, DefaultLanguage);
                    if (!IsSupported(_currentLanguage))
                    {
                        _currentLanguage = DefaultLanguage;
                    }
                }
                return _currentLanguage;
            }
        }

        /// <summary>是否为中文语言 (供 LLM 语言指令使用).</summary>
        public static bool IsChinese => CurrentLanguage != null && CurrentLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 切换语言. 变化时持久化到 EditorPrefs 并触发 <see cref="LanguageChanged"/>.
        /// </summary>
        /// <param name="languageCode">目标语言 code, 必须在 <see cref="SupportedLanguages"/> 里.</param>
        public static void SetLanguage(string languageCode)
        {
            if (string.IsNullOrEmpty(languageCode) || !IsSupported(languageCode))
            {
                return;
            }

            if (CurrentLanguage == languageCode)
            {
                return;
            }

            _currentLanguage = languageCode;
            EditorPrefs.SetString(PrefsKey, languageCode);

            // 重新加载语言包
            LanguageResourceLoader.Reload();

            // 广播事件, 让所有窗口重建 UI
            try
            {
                LanguageChanged?.Invoke(languageCode);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        /// <summary>判断语言 code 是否受支持.</summary>
        public static bool IsSupported(string languageCode)
        {
            if (string.IsNullOrEmpty(languageCode)) return false;
            foreach (var opt in SupportedLanguages)
            {
                if (string.Equals(opt.Code, languageCode, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>获取语言 code 对应的显示名 (下拉菜单展示).</summary>
        public static string GetDisplayName(string languageCode)
        {
            foreach (var opt in SupportedLanguages)
            {
                if (string.Equals(opt.Code, languageCode, StringComparison.OrdinalIgnoreCase))
                    return opt.DisplayName;
            }
            return languageCode;
        }

        /// <summary>
        /// LLM 回复是否跟随 UI 语言 (默认 true).
        /// </summary>
        /// <remarks>
        /// 开启时, AgentLoop 组装 system prompt 时会追加"用相同语言回复"的指令.
        /// 关闭时, 由模型根据用户输入语言自行判断.
        /// </remarks>
        public static bool LlmFollowUiLanguage
        {
            get => EditorPrefs.GetBool(LlmFollowUiPrefsKey, DefaultLlmFollowUi);
            set
            {
                if (LlmFollowUiLanguage == value) return;
                EditorPrefs.SetBool(LlmFollowUiPrefsKey, value);
            }
        }

        /// <summary>
        /// 根据当前语言与 <see cref="LlmFollowUiLanguage"/> 设置, 生成用于注入到 LLM system prompt 的语言指令.
        /// 关闭跟随时返回空串.
        /// </summary>
        public static string GetLlmLanguageInstruction()
        {
            if (!LlmFollowUiLanguage) return string.Empty;

            return IsChinese
                ? "Reply to the user in Simplified Chinese (简体中文). Keep code, identifiers, file paths, error messages, and log output in their original form."
                : "Reply to the user in English. Keep code, identifiers, file paths, error messages, and log output in their original form.";
        }
    }

    /// <summary>语言选项 (code + 展示名).</summary>
    public readonly struct LanguageOption
    {
        public readonly string Code;
        public readonly string DisplayName;

        public LanguageOption(string code, string displayName)
        {
            Code = code;
            DisplayName = displayName;
        }
    }
}
