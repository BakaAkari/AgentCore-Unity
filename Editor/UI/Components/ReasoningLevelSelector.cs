using System;
using System.Collections.Generic;
using AgentCore.Editor.Config;
using AgentCore.Editor.Core;
using AgentCore.Editor.L10n;
using AgentCore.Editor.LLM;
using UnityEngine;
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
    /// v1.22.0: 扩展至完整 8 档（Auto / Off / Minimal / Low / Medium / High / XHigh / Max），并接入
    /// <see cref="ReasoningParamMapper.ResolveEffectivePlan"/> 进行模型能力感知：不支持的档位灰显禁用、
    /// 选中高档切换到低上限模型时胶囊显示降档反馈（如 <c>Max → High</c>），且绝不覆写会话原始意图。
    /// </para>
    /// </summary>
    public sealed class ReasoningLevelSelector : VisualElement
    {
        /// <summary>规范 8 档持久化字符串，顺序与 <see cref="ReasoningParamMapper.ReasoningLevel"/> 一致。</summary>
        internal static readonly string[] LevelValues =
        {
            "auto", "off", "minimal", "low", "medium", "high", "xhigh", "max"
        };

        /// <summary>胶囊显示状态快照（纯函数计算，便于单测验证）。</summary>
        internal sealed class CapsuleState
        {
            public ReasoningParamMapper.ReasoningLevel SelectedLevel;
            public ReasoningParamMapper.ReasoningLevel EffectiveLevel;
            public bool WasClamped;
            public bool IsDisplayClamped;
            public string ClampReason;
            public string CapsuleText;
            public string Tooltip;
            public ReasoningResolution Resolution;
        }

        /// <summary>弹出列表状态快照（纯函数计算，便于单测验证）。</summary>
        internal sealed class PopupState
        {
            public List<ChoicePopup.Item> Items;
            public ReasoningResolution Resolution;
        }

        private readonly Label _prefix;
        private readonly Label _value;
        private AgentLoop _agentLoop;
        private string _overrideRaw;
        private int _index;

        /// <summary>初始化思考强度胶囊按钮组件。</summary>
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
            RegisterCallback<AttachToPanelEvent>(OnAttach);
            RegisterCallback<DetachFromPanelEvent>(OnDetach);
            RefreshDisplayNames();
        }

        private void OnAttach(AttachToPanelEvent evt)
        {
            AgentCoreProviderProfiles.OnProfilesChanged += OnProfilesChanged;
            LanguageManager.LanguageChanged += OnLanguageChanged;
            RefreshDisplayNames();
        }

        private void OnDetach(DetachFromPanelEvent evt)
        {
            AgentCoreProviderProfiles.OnProfilesChanged -= OnProfilesChanged;
            LanguageManager.LanguageChanged -= OnLanguageChanged;
        }

        private void OnProfilesChanged() => RefreshDisplayNames();

        private void OnLanguageChanged(string language) => RefreshDisplayNames();

        internal static string[] DisplayNames() => new[]
        {
            Loc.Tr("chat.reasoningLevel.auto", "Auto"),
            Loc.Tr("chat.reasoningLevel.off", "Off"),
            Loc.Tr("chat.reasoningLevel.minimal", "Min"),
            Loc.Tr("chat.reasoningLevel.low", "Low"),
            Loc.Tr("chat.reasoningLevel.medium", "Med"),
            Loc.Tr("chat.reasoningLevel.high", "High"),
            Loc.Tr("chat.reasoningLevel.xhigh", "XHigh"),
            Loc.Tr("chat.reasoningLevel.max", "Max"),
        };

        internal static string[] Hints() => new[]
        {
            Loc.Tr("chat.reasoningLevel.autoHint", "Model default"),
            Loc.Tr("chat.reasoningLevel.offHint", "Fastest, no thinking"),
            Loc.Tr("chat.reasoningLevel.minimalHint", "Minimal"),
            Loc.Tr("chat.reasoningLevel.lowHint", "Quick"),
            Loc.Tr("chat.reasoningLevel.mediumHint", "Balanced"),
            Loc.Tr("chat.reasoningLevel.highHint", "Deep"),
            Loc.Tr("chat.reasoningLevel.xhighHint", "Deeper"),
            Loc.Tr("chat.reasoningLevel.maxHint", "Deepest, slower"),
        };

        internal static string GetShortLabel(ReasoningParamMapper.ReasoningLevel level)
        {
            var names = DisplayNames();
            int idx = (int)level;
            return idx >= 0 && idx < names.Length ? names[idx] : names[0];
        }

        /// <summary>
        /// 纯函数：根据会话覆盖值、全局/Profile 默认配置与当前模型能力计算胶囊展示状态。
        /// 绝不修改调用方持有的原始会话覆盖值。
        /// </summary>
        internal static CapsuleState ResolveCapsuleState(
            string overrideRaw,
            string defaultEffort,
            bool defaultReasoningEnabled,
            string endpoint,
            string modelName,
            ModelProfile profile = null,
            string dialect = null)
        {
            var overrideLevel = ReasoningParamMapper.ParseLevel(overrideRaw);
            var plan = ReasoningParamMapper.ResolveEffectivePlan(
                overrideLevel, defaultEffort, defaultReasoningEnabled, endpoint, modelName, profile, dialect);

            string selectedShort = GetShortLabel(overrideLevel);
            string effectiveShort = GetShortLabel(plan.EffectiveLevel);
            string baseTooltip = Loc.Tr(
                "chat.reasoningLevel.tooltip",
                "Reasoning intensity (applies to this session, saved with it)");

            bool displayClamped = overrideLevel != ReasoningParamMapper.ReasoningLevel.Auto && plan.WasClamped;
            string capsuleText;
            string resolvedTooltip;

            if (displayClamped)
            {
                capsuleText = selectedShort + " → " + effectiveShort;
                resolvedTooltip = baseTooltip + "\n" + Loc.Tr(
                    "chat.reasoningLevel.clampedTooltip",
                    "Selected {0}, auto-adapted to {1} for current model ({2}). Your session selection is preserved.",
                    selectedShort,
                    effectiveShort,
                    FormatClampReason(plan.ClampReason));
            }
            else if (overrideLevel == ReasoningParamMapper.ReasoningLevel.Auto)
            {
                capsuleText = GetShortLabel(ReasoningParamMapper.ReasoningLevel.Auto);
                resolvedTooltip = baseTooltip + "\n" + Loc.Tr(
                    "chat.reasoningLevel.autoEffectiveTooltip",
                    "Following model/profile default (effective: {0}).",
                    effectiveShort);
            }
            else
            {
                capsuleText = selectedShort;
                resolvedTooltip = baseTooltip;
            }

            return new CapsuleState
            {
                SelectedLevel = overrideLevel,
                EffectiveLevel = plan.EffectiveLevel,
                WasClamped = plan.WasClamped,
                IsDisplayClamped = displayClamped,
                ClampReason = plan.ClampReason,
                CapsuleText = capsuleText,
                Tooltip = resolvedTooltip,
                Resolution = plan
            };
        }

        /// <summary>
        /// 纯函数：构建 8 档弹出列表条目与禁用状态。
        /// 不支持的档位设 <see cref="ChoicePopup.Item.Disabled"/> = true；用户当前所选档位保留 ✓ 标记。
        /// </summary>
        internal static PopupState BuildPopupState(
            string overrideRaw,
            string defaultEffort,
            bool defaultReasoningEnabled,
            string endpoint,
            string modelName,
            ModelProfile profile = null,
            string dialect = null)
        {
            var overrideLevel = ReasoningParamMapper.ParseLevel(overrideRaw);
            var plan = ReasoningParamMapper.ResolveEffectivePlan(
                overrideLevel, defaultEffort, defaultReasoningEnabled, endpoint, modelName, profile, dialect);
            var resolvedProfile = profile ?? ModelRegistry.Lookup(modelName);

            var names = DisplayNames();
            var hints = Hints();
            var items = new List<ChoicePopup.Item>(LevelValues.Length);

            for (int i = 0; i < LevelValues.Length; i++)
            {
                var tier = (ReasoningParamMapper.ReasoningLevel)i;
                bool supported = plan.IsTierSupported(tier);
                string hint;
                if (supported)
                {
                    hint = hints[i];
                }
                else if (tier == ReasoningParamMapper.ReasoningLevel.Off
                    && resolvedProfile != null
                    && resolvedProfile.Thinking == ThinkingMode.AlwaysOn)
                {
                    hint = Loc.Tr("chat.reasoningLevel.alwaysOnHint", "Always on for this model");
                }
                else
                {
                    hint = Loc.Tr("chat.reasoningLevel.unsupportedHint", "Not supported by model");
                }

                items.Add(new ChoicePopup.Item
                {
                    Label = names[i],
                    Hint = hint,
                    Selected = tier == overrideLevel,
                    Disabled = !supported
                });
            }

            return new PopupState
            {
                Items = items,
                Resolution = plan
            };
        }

        /// <summary>语言、模型或配置切换后刷新胶囊显示文本与 tooltip（不改变当前会话选中的等级）。</summary>
        public void RefreshDisplayNames()
        {
            ReadCurrentModelContext(
                out var defaultEffort,
                out var defaultReasoningEnabled,
                out var endpoint,
                out var modelName,
                out var dialect);

            string overrideRaw = _agentLoop != null ? _agentLoop.ReasoningEffortOverride : _overrideRaw;
            var state = ResolveCapsuleState(
                overrideRaw, defaultEffort, defaultReasoningEnabled, endpoint, modelName, null, dialect);

            _index = (int)state.SelectedLevel;
            _prefix.text = Loc.Tr("chat.reasoningLevel.label", "Thinking");
            _value.text = state.CapsuleText;
            tooltip = state.Tooltip;
            EnableInClassList("is-clamped", state.IsDisplayClamped);
        }

        /// <summary>
        /// 从指定 AgentLoop 读取当前会话的覆盖值，同步显示（不触发变更）。
        /// 会话切换 / 新建会话 / 首次挂载时调用。
        /// </summary>
        /// <param name="agentLoop">当前活跃的 AgentLoop 实例；为 null 时重置为 Auto。</param>
        public void SyncFromAgentLoop(AgentLoop agentLoop)
        {
            _agentLoop = agentLoop;
            _overrideRaw = agentLoop?.ReasoningEffortOverride;
            _index = (int)ReasoningParamMapper.ParseLevel(_overrideRaw);
            RefreshDisplayNames();
        }

        private void ShowPopup()
        {
            ReadCurrentModelContext(
                out var defaultEffort,
                out var defaultReasoningEnabled,
                out var endpoint,
                out var modelName,
                out var dialect);

            string overrideRaw = _agentLoop != null ? _agentLoop.ReasoningEffortOverride : _overrideRaw;
            var popupState = BuildPopupState(
                overrideRaw, defaultEffort, defaultReasoningEnabled, endpoint, modelName, null, dialect);

            AddToClassList("is-open");
            UnityEditor.PopupWindow.Show(worldBound, new ChoicePopup(
                Loc.Tr("chat.reasoningLevel.title", "Thinking level"),
                Loc.Tr("chat.reasoningLevel.subtitle", "Applies to this session, saved with it"),
                popupState.Items,
                Select,
                Mathf.Max(260f, worldBound.width),
                () => RemoveFromClassList("is-open")));
        }

        private void Select(int index)
        {
            if (_agentLoop == null || index < 0 || index >= LevelValues.Length || index == _index) return;
            // 落地失败（无活跃会话等）时不改显示，避免 UI 与实际生效状态不一致
            if (_agentLoop.SetReasoningEffortOverride(LevelValues[index]))
            {
                _index = index;
                _overrideRaw = _agentLoop.ReasoningEffortOverride;
            }
            RefreshDisplayNames();
        }

        private static string FormatClampReason(string clampReason)
        {
            switch (clampReason)
            {
                case "model_cap":
                    return Loc.Tr("chat.reasoningLevel.clampReason.modelCap", "model maximum tier");
                case "model_floor":
                    return Loc.Tr("chat.reasoningLevel.clampReason.modelFloor", "model minimum tier");
                case "always_on":
                    return Loc.Tr("chat.reasoningLevel.clampReason.alwaysOn", "thinking always enabled");
                case "runtime_learned":
                    return Loc.Tr("chat.reasoningLevel.clampReason.runtimeLearned", "endpoint runtime constraint");
                default:
                    return Loc.Tr("chat.reasoningLevel.clampReason.modelCap", "model maximum tier");
            }
        }

        private static void ReadCurrentModelContext(
            out string defaultEffort,
            out bool defaultReasoningEnabled,
            out string endpoint,
            out string modelName,
            out string dialect)
        {
            var active = ActiveModelConfig.ActiveProfile;
            if (active != null)
            {
                endpoint = active.endpoint;
                modelName = AgentCoreProviderProfiles.instance.GetEffectiveModelName(active);
                dialect = ActiveModelConfig.ResolveDialect(active);
                defaultEffort = ActiveModelConfig.ReasoningEffort;
                defaultReasoningEnabled = ActiveModelConfig.EnableReasoningOutput;
            }
            else
            {
                endpoint = null;
                modelName = null;
                dialect = null;
                var settings = AgentCoreSettings.instance;
                defaultEffort = settings != null ? settings.reasoningEffort : string.Empty;
                defaultReasoningEnabled = settings == null || settings.enableReasoningOutput;
            }
        }
    }
}
