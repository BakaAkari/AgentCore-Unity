using System.Collections.Generic;
using AgentCore.Editor.Core;
using AgentCore.Editor.L10n;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI.Components
{
    /// <summary>
    /// v1.14.10: 思考强度快捷切换下拉（chat 面板输入区，类似 Codex/Claude Code 的思考强度开关）。
    /// <para>
    /// 与 <see cref="AgentCore.Editor.L10n.UI.LanguageSelector"/>（同一 PopupField 包装模式）的
    /// 关键区别：语言是全局单一状态，靠 <c>LanguageManager.LanguageChanged</c> 事件驱动同步；
    /// 思考强度覆盖是<b>会话级</b>状态（随 <see cref="AgentLoop.ReasoningEffortOverride"/>，每个
    /// 会话可以有不同的选择），AgentLoop 实例本身会随会话切换/重建而变化，不适合用长期事件订阅
    /// ——外部（<c>ChatWindow</c>）在会话切换 / 新建会话完成后显式调用 <see cref="SyncFromAgentLoop"/>
    /// 同步显示值，选择变更时调用 <see cref="AgentLoop.SetReasoningEffortOverride"/> 落地。
    /// </para>
    /// </summary>
    public sealed class ReasoningLevelSelector : VisualElement
    {
        /// <summary>持久化用的等级字符串，与 <see cref="LLM.ReasoningParamMapper.ParseLevel"/> 保持同一取值集合。</summary>
        private static readonly string[] LevelValues = { "auto", "off", "low", "medium", "high" };

        private readonly PopupField<string> _popup;
        private readonly List<string> _displayNames;
        private bool _suppressCallback;
        private AgentLoop _agentLoop;

        public ReasoningLevelSelector()
        {
            AddToClassList("agentcore-reasoning-level-selector");

            _displayNames = BuildDisplayNames();

            _popup = new PopupField<string>(_displayNames, 0)
            {
                tooltip = Loc.Tr("chat.reasoningLevel.tooltip", "思考强度（本会话生效，随会话保存）"),
            };
            _popup.AddToClassList("agentcore-reasoning-level-selector__popup");

            // 与 LanguageSelector 同款处理：PopupField 默认预留 label 位置，隐藏掉。
            var labelEl = _popup.Q<Label>(className: "unity-base-field__label");
            if (labelEl != null)
            {
                labelEl.style.display = DisplayStyle.None;
            }

            // 尺寸/颜色/圆角等视觉样式统一交给 ChatWindow.uss 的
            // .agentcore-reasoning-level-selector(__popup) 选择器管理（与 #send-button /
            // #cancel-button 同一套设计语言：28px 高、6px 圆角、无边框、#2b2b2b 深色底），
            // 不在 C# 里写内联样式——避免两处样式来源打架，也方便统一改主题时只改一份 USS。
            _popup.RegisterValueChangedCallback(OnPopupChanged);

            Add(_popup);
        }

        /// <summary>
        /// 用当前 L10n 语言重新构建显示名称列表（语言切换时如需刷新，由外部重新调用一次
        /// <see cref="RefreshDisplayNames"/>；构造时用当前语言构建一次即可覆盖多数场景）。
        /// </summary>
        private static List<string> BuildDisplayNames()
        {
            return new List<string>
            {
                Loc.Tr("chat.reasoningLevel.auto", "Auto"),
                Loc.Tr("chat.reasoningLevel.off", "Off"),
                Loc.Tr("chat.reasoningLevel.low", "Low"),
                Loc.Tr("chat.reasoningLevel.medium", "Med"),
                Loc.Tr("chat.reasoningLevel.high", "High"),
            };
        }

        /// <summary>语言切换后刷新下拉显示文本（不改变当前选中的等级）。</summary>
        public void RefreshDisplayNames()
        {
            var newNames = BuildDisplayNames();
            for (int i = 0; i < newNames.Count && i < _displayNames.Count; i++)
            {
                _displayNames[i] = newNames[i];
            }
            // PopupField 的选项列表在构造后不会自动跟着 List 内容变化重绘，需要用
            // SetValueWithoutNotify 触发一次刷新（choices 引用未变，但显示需要重排）。
            // 简化处理：直接重新同步一次当前选中项对应的新文案。
            if (_agentLoop != null) SyncFromAgentLoop(_agentLoop);
        }

        /// <summary>
        /// 从指定 AgentLoop 读取当前会话的覆盖值，同步下拉框显示（不触发变更回调）。
        /// 会话切换 / 新建会话 / 首次挂载时调用。
        /// </summary>
        /// <param name="agentLoop">当前活跃的 AgentLoop 实例；为 null 时重置为 Auto。</param>
        public void SyncFromAgentLoop(AgentLoop agentLoop)
        {
            _agentLoop = agentLoop;

            string level = agentLoop?.ReasoningEffortOverride;
            int index = System.Array.IndexOf(LevelValues, string.IsNullOrEmpty(level) ? "auto" : level.ToLowerInvariant());
            if (index < 0) index = 0;

            var target = _displayNames[index];
            if (_popup.value == target) return;

            _suppressCallback = true;
            try
            {
                _popup.SetValueWithoutNotify(target);
            }
            finally
            {
                _suppressCallback = false;
            }
        }

        private void OnPopupChanged(ChangeEvent<string> evt)
        {
            if (_suppressCallback) return;
            if (_agentLoop == null) return;

            int index = _displayNames.IndexOf(evt.newValue);
            if (index < 0 || index >= LevelValues.Length) return;

            bool ok = _agentLoop.SetReasoningEffortOverride(LevelValues[index]);
            if (!ok)
            {
                // 落地失败（无活跃会话等）：回滚显示值，避免 UI 显示的选择与实际生效状态不一致。
                SyncFromAgentLoop(_agentLoop);
            }
        }
    }
}
