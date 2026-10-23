using System.Collections.Generic;
using AgentCore.Editor.Core;
using AgentCore.Editor.L10n;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI.Components
{
    /// <summary>
    /// v1.14.10: 思考强度快捷切换（chat 面板输入区，类似 Codex/Claude Code 的思考强度开关）。
    /// <para>
    /// v1.17.0: 由 Unity PopupField 改为与 <see cref="ModelSelector"/> 同款的胶囊按钮（"思考" 前缀 + 当前档位 + 箭头），
    /// 点击弹出同款自绘面板 <see cref="ChoicePopup"/>（每档附说明、当前档 ✓、键盘可选），样式见 ModelSelector.uss。
    /// </para>
    /// <para>
    /// 与模型选择的关键区别：思考强度覆盖是<b>会话级</b>状态（随 <see cref="AgentLoop.ReasoningEffortOverride"/>，每个
    /// 会话可以有不同的选择），AgentLoop 实例本身会随会话切换/重建而变化，不适合用长期事件订阅
    /// ——外部（<c>ChatWindow</c>）在会话切换 / 新建会话完成后显式调用 <see cref="SyncFromAgentLoop"/>
    /// 同步显示值，选择变更时调用 <see cref="AgentLoop.SetReasoningEffortOverride"/> 落地。
    /// </para>
    /// </summary>
    public sealed class ReasoningLevelSelector : VisualElement
    {
        /// <summary>持久化用的等级字符串，与 <see cref="LLM.ReasoningParamMapper.ParseLevel"/> 保持同一取值集合。</summary>
        private static readonly string[] LevelValues = { "auto", "off", "low", "medium", "high" };

        private readonly Label _prefix;
        private readonly Label _value;
        private AgentLoop _agentLoop;
        private int _index;

        public ReasoningLevelSelector()
        {
            AddToClassList("agentcore-model-selector");
            AddToClassList("agentcore-reasoning-level-selector");
            var uss = ModelSelector.LoadStyleSheet();
            if (uss != null) styleSheets.Add(uss);

            _prefix = new Label();
            _prefix.AddToClassList("agentcore-model-selector__prefix");
            _value = new Label();
            _value.AddToClassList("agentcore-model-selector__label");
            var arrow = new VisualElement();
            arrow.AddToClassList("unity-base-popup-field__arrow");
            arrow.AddToClassList("agentcore-model-selector__arrow");
            Add(_prefix);
            Add(_value);
            Add(arrow);

            this.AddManipulator(new Clickable(ShowPopup));
            RefreshDisplayNames();
        }

        private static string[] DisplayNames() => new[]
        {
            Loc.Tr("chat.reasoningLevel.auto", "Auto"),
            Loc.Tr("chat.reasoningLevel.off", "Off"),
            Loc.Tr("chat.reasoningLevel.low", "Low"),
            Loc.Tr("chat.reasoningLevel.medium", "Med"),
            Loc.Tr("chat.reasoningLevel.high", "High"),
        };

        private static string[] Hints() => new[]
        {
            Loc.Tr("chat.reasoningLevel.autoHint", "Model default"),
            Loc.Tr("chat.reasoningLevel.offHint", "Fastest, no thinking"),
            Loc.Tr("chat.reasoningLevel.lowHint", "Quick"),
            Loc.Tr("chat.reasoningLevel.mediumHint", "Balanced"),
            Loc.Tr("chat.reasoningLevel.highHint", "Deepest, slower"),
        };

        /// <summary>语言切换后刷新显示文本（不改变当前选中的等级）。</summary>
        public void RefreshDisplayNames()
        {
            _prefix.text = Loc.Tr("chat.reasoningLevel.label", "Thinking");
            _value.text = DisplayNames()[_index];
            tooltip = Loc.Tr("chat.reasoningLevel.tooltip", "思考强度（本会话生效，随会话保存）");
        }

        /// <summary>
        /// 从指定 AgentLoop 读取当前会话的覆盖值，同步显示（不触发变更）。
        /// 会话切换 / 新建会话 / 首次挂载时调用。
        /// </summary>
        /// <param name="agentLoop">当前活跃的 AgentLoop 实例；为 null 时重置为 Auto。</param>
        public void SyncFromAgentLoop(AgentLoop agentLoop)
        {
            _agentLoop = agentLoop;

            string level = agentLoop?.ReasoningEffortOverride;
            int index = System.Array.IndexOf(LevelValues, string.IsNullOrEmpty(level) ? "auto" : level.ToLowerInvariant());
            _index = index < 0 ? 0 : index;
            RefreshDisplayNames();
        }

        private void ShowPopup()
        {
            var names = DisplayNames();
            var hints = Hints();
            var items = new List<ChoicePopup.Item>();
            for (int i = 0; i < names.Length; i++)
                items.Add(new ChoicePopup.Item { Label = names[i], Hint = hints[i], Selected = i == _index });

            AddToClassList("is-open");
            UnityEditor.PopupWindow.Show(worldBound, new ChoicePopup(
                Loc.Tr("chat.reasoningLevel.title", "Thinking level"),
                Loc.Tr("chat.reasoningLevel.subtitle", "Applies to this session, saved with it"),
                items, Select, UnityEngine.Mathf.Max(240f, worldBound.width),
                () => RemoveFromClassList("is-open")));
        }

        private void Select(int index)
        {
            if (_agentLoop == null || index < 0 || index >= LevelValues.Length || index == _index) return;
            // 落地失败（无活跃会话等）时不改显示，避免 UI 与实际生效状态不一致
            if (_agentLoop.SetReasoningEffortOverride(LevelValues[index]))
                _index = index;
            RefreshDisplayNames();
        }
    }
}
