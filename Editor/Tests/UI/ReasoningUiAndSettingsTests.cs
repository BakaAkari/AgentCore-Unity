using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgentCore.Editor.Config.Settings.Pages;
using AgentCore.Editor.L10n;
using AgentCore.Editor.LLM;
using AgentCore.Editor.UI.Components;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace AgentCore.Tests.Editor.UI
{
    /// <summary>
    /// v1.22.0 多级思考强度 UI 选择器、弹窗禁用项导航、设置页选项与 L10n 键对称性单元测试（离线 EditMode）。
    /// </summary>
    [TestFixture]
    public class ReasoningUiAndSettingsTests
    {
        /// <summary>每次测试前清理运行时剪枝注册表，避免学习到的约束跨用例泄漏。</summary>
        [SetUp]
        public void SetUp() => RequestPruningRegistry.ClearAll();

        /// <summary>每次测试后清理运行时剪枝注册表。</summary>
        [TearDown]
        public void TearDown() => RequestPruningRegistry.ClearAll();

        #region ReasoningLevelSelector — 8 档顺序与标签

        /// <summary>校验 ReasoningLevelSelector 的 8 档完整顺序、名称数组与短标签解析一致。</summary>
        [Test]
        public void ReasoningLevelSelector_AllEightTiers_OrderAndLabels()
        {
            var expected = new[] { "auto", "off", "minimal", "low", "medium", "high", "xhigh", "max" };
            CollectionAssert.AreEqual(expected, ReasoningLevelSelector.LevelValues);

            var displayNames = ReasoningLevelSelector.DisplayNames();
            var hints = ReasoningLevelSelector.Hints();
            Assert.AreEqual(8, displayNames.Length);
            Assert.AreEqual(8, hints.Length);

            for (int i = 0; i < expected.Length; i++)
            {
                var tier = (ReasoningParamMapper.ReasoningLevel)i;
                Assert.AreEqual(tier, ReasoningParamMapper.ParseLevel(ReasoningLevelSelector.LevelValues[i]));
                Assert.IsFalse(string.IsNullOrWhiteSpace(displayNames[i]), $"DisplayName[{i}] should not be empty.");
                Assert.IsFalse(string.IsNullOrWhiteSpace(hints[i]), $"Hint[{i}] should not be empty.");
                Assert.IsFalse(string.IsNullOrWhiteSpace(ReasoningLevelSelector.GetShortLabel(tier)), $"ShortLabel({tier}) should not be empty.");
            }
        }

        #endregion

        #region ReasoningLevelSelector — 模型能力禁用与非破坏性夹断展示

        /// <summary>gpt-5 禁用 XHigh/Max；选择 max 时显示降档胶囊文本且保留原选中的勾选状态。</summary>
        [Test]
        public void ReasoningLevelSelector_Gpt5_DisablesXHighAndMax_ClampsDisplayWithoutMutatingOverride()
        {
            const string endpoint = "https://api.openai.com/v1";
            const string model = "gpt-5";
            const string originalOverride = "max";

            var popup = ReasoningLevelSelector.BuildPopupState(
                originalOverride, "medium", true, endpoint, model);

            Assert.AreEqual(8, popup.Items.Count);
            Assert.IsFalse(popup.Items[(int)ReasoningParamMapper.ReasoningLevel.Auto].Disabled);
            Assert.IsFalse(popup.Items[(int)ReasoningParamMapper.ReasoningLevel.Off].Disabled);
            Assert.IsFalse(popup.Items[(int)ReasoningParamMapper.ReasoningLevel.Minimal].Disabled);
            Assert.IsFalse(popup.Items[(int)ReasoningParamMapper.ReasoningLevel.Low].Disabled);
            Assert.IsFalse(popup.Items[(int)ReasoningParamMapper.ReasoningLevel.Medium].Disabled);
            Assert.IsFalse(popup.Items[(int)ReasoningParamMapper.ReasoningLevel.High].Disabled);
            Assert.IsTrue(popup.Items[(int)ReasoningParamMapper.ReasoningLevel.XHigh].Disabled);
            Assert.IsTrue(popup.Items[(int)ReasoningParamMapper.ReasoningLevel.Max].Disabled);

            // 用户原选中的 Max 档即使在当前模型下被禁用，仍保留勾选标记以免丢失会话上下文
            Assert.IsTrue(popup.Items[(int)ReasoningParamMapper.ReasoningLevel.Max].Selected);
            Assert.IsFalse(popup.Items[(int)ReasoningParamMapper.ReasoningLevel.High].Selected);

            var capsule = ReasoningLevelSelector.ResolveCapsuleState(
                originalOverride, "medium", true, endpoint, model);

            Assert.AreEqual(ReasoningParamMapper.ReasoningLevel.Max, capsule.SelectedLevel);
            Assert.AreEqual(ReasoningParamMapper.ReasoningLevel.High, capsule.EffectiveLevel);
            Assert.IsTrue(capsule.WasClamped);
            Assert.IsTrue(capsule.IsDisplayClamped);
            Assert.AreEqual("model_cap", capsule.ClampReason);

            string expectedCapsule = $"{ReasoningLevelSelector.GetShortLabel(ReasoningParamMapper.ReasoningLevel.Max)} \u2192 {ReasoningLevelSelector.GetShortLabel(ReasoningParamMapper.ReasoningLevel.High)}";
            Assert.AreEqual(expectedCapsule, capsule.CapsuleText);
            Assert.IsFalse(string.IsNullOrWhiteSpace(capsule.Tooltip));
            StringAssert.Contains(ReasoningLevelSelector.GetShortLabel(ReasoningParamMapper.ReasoningLevel.High), capsule.Tooltip);
        }

        /// <summary>deepseek-chat 禁用 Minimal/XHigh/Max，保留 Auto/Off/Low/Medium/High。</summary>
        [Test]
        public void ReasoningLevelSelector_DeepSeekChat_DisablesMinimalXHighAndMax()
        {
            var popup = ReasoningLevelSelector.BuildPopupState(
                "auto", "medium", true, "https://api.deepseek.com/v1", "deepseek-chat");

            Assert.AreEqual(8, popup.Items.Count);
            Assert.IsFalse(popup.Items[(int)ReasoningParamMapper.ReasoningLevel.Auto].Disabled);
            Assert.IsFalse(popup.Items[(int)ReasoningParamMapper.ReasoningLevel.Off].Disabled);
            Assert.IsTrue(popup.Items[(int)ReasoningParamMapper.ReasoningLevel.Minimal].Disabled);
            Assert.IsFalse(popup.Items[(int)ReasoningParamMapper.ReasoningLevel.Low].Disabled);
            Assert.IsFalse(popup.Items[(int)ReasoningParamMapper.ReasoningLevel.Medium].Disabled);
            Assert.IsFalse(popup.Items[(int)ReasoningParamMapper.ReasoningLevel.High].Disabled);
            Assert.IsTrue(popup.Items[(int)ReasoningParamMapper.ReasoningLevel.XHigh].Disabled);
            Assert.IsTrue(popup.Items[(int)ReasoningParamMapper.ReasoningLevel.Max].Disabled);
        }

        /// <summary>claude-mythos-5（AlwaysOn）禁用 Off；用户选 Off 时夹断显示为 Off → Low。</summary>
        [Test]
        public void ReasoningLevelSelector_ClaudeMythos5_AlwaysOn_DisablesOffAndClampsToLow()
        {
            const string endpoint = "https://api.anthropic.com/v1";
            const string model = "claude-mythos-5";

            var popup = ReasoningLevelSelector.BuildPopupState(
                "off", "high", true, endpoint, model, null, "anthropic");

            Assert.IsTrue(popup.Items[(int)ReasoningParamMapper.ReasoningLevel.Off].Disabled);
            Assert.IsTrue(popup.Items[(int)ReasoningParamMapper.ReasoningLevel.Off].Selected);
            Assert.IsFalse(popup.Items[(int)ReasoningParamMapper.ReasoningLevel.XHigh].Disabled);
            Assert.IsTrue(popup.Items[(int)ReasoningParamMapper.ReasoningLevel.Max].Disabled);

            var capsule = ReasoningLevelSelector.ResolveCapsuleState(
                "off", "high", true, endpoint, model, null, "anthropic");

            Assert.AreEqual(ReasoningParamMapper.ReasoningLevel.Off, capsule.SelectedLevel);
            Assert.AreEqual(ReasoningParamMapper.ReasoningLevel.Low, capsule.EffectiveLevel);
            Assert.IsTrue(capsule.WasClamped);
            Assert.IsTrue(capsule.IsDisplayClamped);
            Assert.AreEqual("always_on", capsule.ClampReason);
            Assert.AreEqual(
                $"{ReasoningLevelSelector.GetShortLabel(ReasoningParamMapper.ReasoningLevel.Off)} \u2192 {ReasoningLevelSelector.GetShortLabel(ReasoningParamMapper.ReasoningLevel.Low)}",
                capsule.CapsuleText);
        }

        /// <summary>未知自定义模型乐观放行全部 8 档，不禁用且不夹断。</summary>
        [Test]
        public void ReasoningLevelSelector_UnknownCustomModel_EnablesAllEightTiers()
        {
            var popup = ReasoningLevelSelector.BuildPopupState(
                "max", "medium", true, "https://custom.local/v1", "unknown-custom-model");

            Assert.AreEqual(8, popup.Items.Count);
            for (int i = 0; i < popup.Items.Count; i++)
            {
                Assert.IsFalse(popup.Items[i].Disabled, $"Tier {i} should be enabled for unknown model.");
            }

            var capsule = ReasoningLevelSelector.ResolveCapsuleState(
                "max", "medium", true, "https://custom.local/v1", "unknown-custom-model");
            Assert.IsFalse(capsule.WasClamped);
            Assert.IsFalse(capsule.IsDisplayClamped);
            Assert.AreEqual(ReasoningLevelSelector.GetShortLabel(ReasoningParamMapper.ReasoningLevel.Max), capsule.CapsuleText);
        }

        /// <summary>Auto 模式下胶囊始终显示 Auto（不标为夹断态），tooltip 附带实际生效说明。</summary>
        [Test]
        public void ReasoningLevelSelector_AutoOverride_DisplaysAutoWithEffectiveTooltip()
        {
            var capsule = ReasoningLevelSelector.ResolveCapsuleState(
                "auto", "max", true, "https://api.openai.com/v1", "gpt-5");

            Assert.AreEqual(ReasoningParamMapper.ReasoningLevel.Auto, capsule.SelectedLevel);
            Assert.AreEqual(ReasoningParamMapper.ReasoningLevel.High, capsule.EffectiveLevel);
            Assert.IsFalse(capsule.IsDisplayClamped);
            Assert.AreEqual(ReasoningLevelSelector.GetShortLabel(ReasoningParamMapper.ReasoningLevel.Auto), capsule.CapsuleText);
            StringAssert.Contains(ReasoningLevelSelector.GetShortLabel(ReasoningParamMapper.ReasoningLevel.High), capsule.Tooltip);
        }

        /// <summary>运行时自学习上限生效时，弹窗禁用超限档位并按 runtime_learned 展示降档。</summary>
        [Test]
        public void ReasoningLevelSelector_RuntimeLearnedCap_DisablesHigherTiersAndShowsClamp()
        {
            const string endpoint = "https://proxy.example.com/v1";
            const string model = "unknown-custom-model";
            RequestPruningRegistry.RegisterReasoningConstraint(endpoint, model, "low");

            var popup = ReasoningLevelSelector.BuildPopupState("high", "medium", true, endpoint, model);
            Assert.IsFalse(popup.Items[(int)ReasoningParamMapper.ReasoningLevel.Low].Disabled);
            Assert.IsTrue(popup.Items[(int)ReasoningParamMapper.ReasoningLevel.Medium].Disabled);
            Assert.IsTrue(popup.Items[(int)ReasoningParamMapper.ReasoningLevel.High].Disabled);
            Assert.IsTrue(popup.Items[(int)ReasoningParamMapper.ReasoningLevel.Max].Disabled);

            var capsule = ReasoningLevelSelector.ResolveCapsuleState("high", "medium", true, endpoint, model);
            Assert.IsTrue(capsule.IsDisplayClamped);
            Assert.AreEqual("runtime_learned", capsule.ClampReason);
            Assert.AreEqual(ReasoningParamMapper.ReasoningLevel.Low, capsule.EffectiveLevel);
        }

        #endregion

        #region ChoicePopup — 禁用项拦截与键盘跳过

        /// <summary>ChoicePopup.TryPick 必须拦截禁用项与越界索引，仅对启用项触发回调。</summary>
        [Test]
        public void ChoicePopup_TryPick_BlocksDisabledAndOutOfRangeItems()
        {
            var items = new List<ChoicePopup.Item>
            {
                new ChoicePopup.Item { Label = "Auto", Disabled = false },
                new ChoicePopup.Item { Label = "Disabled", Disabled = true },
                new ChoicePopup.Item { Label = "High", Disabled = false }
            };

            var picked = new List<int>();
            var popup = new ChoicePopup("Title", "Sub", items, idx => picked.Add(idx), 260f);

            Assert.IsFalse(ChoicePopup.CanPick(items, -1));
            Assert.IsFalse(ChoicePopup.CanPick(items, 1));
            Assert.IsFalse(ChoicePopup.CanPick(items, 3));
            Assert.IsTrue(ChoicePopup.CanPick(items, 0));
            Assert.IsTrue(ChoicePopup.CanPick(items, 2));

            Assert.IsFalse(popup.TryPick(-1));
            Assert.IsFalse(popup.TryPick(1));
            Assert.IsFalse(popup.TryPick(99));
            Assert.AreEqual(0, picked.Count);

            Assert.IsTrue(popup.TryPick(2));
            CollectionAssert.AreEqual(new[] { 2 }, picked);
        }

        /// <summary>ChoicePopup.FindNextEnabledIndex 键盘上下导航应跳过禁用项并正确环绕。</summary>
        [Test]
        public void ChoicePopup_FindNextEnabledIndex_SkipsDisabledAndWraps()
        {
            var items = new List<ChoicePopup.Item>
            {
                new ChoicePopup.Item { Label = "0", Disabled = true },
                new ChoicePopup.Item { Label = "1", Disabled = false },
                new ChoicePopup.Item { Label = "2", Disabled = true },
                new ChoicePopup.Item { Label = "3", Disabled = true },
                new ChoicePopup.Item { Label = "4", Disabled = false },
                new ChoicePopup.Item { Label = "5", Disabled = true }
            };

            // 未高亮(-1)时：向下找首个可用(1)，向上找末个可用(4)
            Assert.AreEqual(1, ChoicePopup.FindNextEnabledIndex(items, -1, +1));
            Assert.AreEqual(4, ChoicePopup.FindNextEnabledIndex(items, -1, -1));

            // 向下跳过 2、3 命中 4；再向下跳过 5、0 环绕回 1
            Assert.AreEqual(4, ChoicePopup.FindNextEnabledIndex(items, 1, +1));
            Assert.AreEqual(1, ChoicePopup.FindNextEnabledIndex(items, 4, +1));

            // 向上跳过 3、2 命中 1；再向上跳过 0、5 环绕回 4
            Assert.AreEqual(1, ChoicePopup.FindNextEnabledIndex(items, 4, -1));
            Assert.AreEqual(4, ChoicePopup.FindNextEnabledIndex(items, 1, -1));
        }

        /// <summary>ChoicePopup.FindNextEnabledIndex 在全禁用或空列表时安全返回 -1。</summary>
        [Test]
        public void ChoicePopup_FindNextEnabledIndex_AllDisabledOrEmpty_ReturnsMinusOne()
        {
            var allDisabled = new List<ChoicePopup.Item>
            {
                new ChoicePopup.Item { Label = "A", Disabled = true },
                new ChoicePopup.Item { Label = "B", Disabled = true }
            };

            Assert.AreEqual(-1, ChoicePopup.FindNextEnabledIndex(allDisabled, -1, +1));
            Assert.AreEqual(-1, ChoicePopup.FindNextEnabledIndex(allDisabled, 0, +1));
            Assert.AreEqual(-1, ChoicePopup.FindNextEnabledIndex(allDisabled, 1, -1));
            Assert.AreEqual(-1, ChoicePopup.FindNextEnabledIndex(new List<ChoicePopup.Item>(), -1, +1));
            Assert.AreEqual(-1, ChoicePopup.FindNextEnabledIndex(null, -1, +1));
        }

        #endregion

        #region ModelAgentSettingsPage — 档位选项一致性与夹断提示

        /// <summary>ModelAgentSettingsPage.ProfileEffortValues 与 ReasoningParamMapper 全部档位严格对齐。</summary>
        [Test]
        public void ModelAgentSettingsPage_ProfileEffortOptions_AlignWithReasoningParamMapper()
        {
            var values = ModelAgentSettingsPage.ProfileEffortValues;
            var labels = ModelAgentSettingsPage.GetProfileEffortLabels();

            Assert.AreEqual(8, values.Length);
            Assert.AreEqual(values.Length, labels.Length);
            Assert.AreEqual("", values[0]);

            for (int i = 1; i < values.Length; i++)
            {
                var expectedTier = (ReasoningParamMapper.ReasoningLevel)i;
                Assert.AreEqual(ReasoningParamMapper.ToEffortString(expectedTier), values[i]);
                Assert.IsFalse(string.IsNullOrWhiteSpace(labels[i]));
            }
            Assert.IsFalse(string.IsNullOrWhiteSpace(labels[0]));
        }

        /// <summary>ResolveProfileEffortIndex 兼容空值、大小写及历史别名 ultra → max。</summary>
        [TestCase("", 0)]
        [TestCase(null, 0)]
        [TestCase("auto", 0)]
        [TestCase("unknown_legacy", 0)]
        [TestCase("off", 1)]
        [TestCase("minimal", 2)]
        [TestCase("low", 3)]
        [TestCase("medium", 4)]
        [TestCase("high", 5)]
        [TestCase("xhigh", 6)]
        [TestCase("X-High", 6)]
        [TestCase("max", 7)]
        [TestCase("MAX", 7)]
        [TestCase("ultra", 7)]
        [TestCase("ULTRA", 7)]
        public void ModelAgentSettingsPage_ResolveProfileEffortIndex_HandlesAliasesAndCase(string raw, int expectedIndex)
        {
            Assert.AreEqual(expectedIndex, ModelAgentSettingsPage.ResolveProfileEffortIndex(raw));
        }

        /// <summary>GetProfileEffortClampHint 仅在已填模型且发生夹断时返回提示，不阻断合法或未知模型配置。</summary>
        [Test]
        public void ModelAgentSettingsPage_GetProfileEffortClampHint_ReportsOnlyWhenClamped()
        {
            string clampedHint = ModelAgentSettingsPage.GetProfileEffortClampHint(
                "https://api.openai.com/v1", "gpt-5", "max", true);
            Assert.IsFalse(string.IsNullOrWhiteSpace(clampedHint));
            StringAssert.Contains("gpt-5", clampedHint);
            StringAssert.Contains("high", clampedHint);

            Assert.IsNull(ModelAgentSettingsPage.GetProfileEffortClampHint(
                "https://api.openai.com/v1", "gpt-5", "high", true));
            Assert.IsNull(ModelAgentSettingsPage.GetProfileEffortClampHint(
                "https://api.openai.com/v1", "gpt-5", "", true));
            Assert.IsNull(ModelAgentSettingsPage.GetProfileEffortClampHint(
                "https://custom.local/v1", "unknown-custom-model", "max", true));
        }

        #endregion

        #region L10n — en-US / zh-CN 键集合对称性与新键非空校验

        /// <summary>en-US.json 与 zh-CN.json 键集合必须 100% 一致，且本次新增思考强度相关键全部存在非空。</summary>
        [Test]
        public void L10nResources_EnUsAndZhCn_HaveIdenticalKeysAndReasoningKeysPresent()
        {
            var en = LoadL10nJson("en-US");
            var zh = LoadL10nJson("zh-CN");

            var enKeys = new HashSet<string>(en.Properties().Select(p => p.Name), StringComparer.Ordinal);
            var zhKeys = new HashSet<string>(zh.Properties().Select(p => p.Name), StringComparer.Ordinal);

            var onlyInEn = enKeys.Except(zhKeys).OrderBy(k => k, StringComparer.Ordinal).ToArray();
            var onlyInZh = zhKeys.Except(enKeys).OrderBy(k => k, StringComparer.Ordinal).ToArray();

            Assert.IsEmpty(onlyInEn, "Keys present in en-US.json but missing in zh-CN.json: " + string.Join(", ", onlyInEn));
            Assert.IsEmpty(onlyInZh, "Keys present in zh-CN.json but missing in en-US.json: " + string.Join(", ", onlyInZh));

            var requiredKeys = new[]
            {
                "chat.reasoningLevel.label",
                "chat.reasoningLevel.title",
                "chat.reasoningLevel.subtitle",
                "chat.reasoningLevel.tooltip",
                "chat.reasoningLevel.auto",
                "chat.reasoningLevel.off",
                "chat.reasoningLevel.minimal",
                "chat.reasoningLevel.low",
                "chat.reasoningLevel.medium",
                "chat.reasoningLevel.high",
                "chat.reasoningLevel.xhigh",
                "chat.reasoningLevel.max",
                "chat.reasoningLevel.autoHint",
                "chat.reasoningLevel.offHint",
                "chat.reasoningLevel.minimalHint",
                "chat.reasoningLevel.lowHint",
                "chat.reasoningLevel.mediumHint",
                "chat.reasoningLevel.highHint",
                "chat.reasoningLevel.xhighHint",
                "chat.reasoningLevel.maxHint",
                "chat.reasoningLevel.unsupportedHint",
                "chat.reasoningLevel.alwaysOnHint",
                "chat.reasoningLevel.clampedTooltip",
                "chat.reasoningLevel.autoEffectiveTooltip",
                "chat.reasoningLevel.clampReason.modelCap",
                "chat.reasoningLevel.clampReason.modelFloor",
                "chat.reasoningLevel.clampReason.alwaysOn",
                "chat.reasoningLevel.clampReason.runtimeLearned",
                "settings.model.reasoningEffort.auto",
                "settings.model.reasoningEffort.off",
                "settings.model.reasoningEffort.minimal",
                "settings.model.reasoningEffort.low",
                "settings.model.reasoningEffort.medium",
                "settings.model.reasoningEffort.high",
                "settings.model.reasoningEffort.xhigh",
                "settings.model.reasoningEffort.max",
                "settings.model.reasoningEffort.clampedHint"
            };

            foreach (var key in requiredKeys)
            {
                Assert.IsTrue(en.TryGetValue(key, out var enVal) && enVal.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string)enVal),
                    $"Missing or empty key in en-US.json: {key}");
                Assert.IsTrue(zh.TryGetValue(key, out var zhVal) && zhVal.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string)zhVal),
                    $"Missing or empty key in zh-CN.json: {key}");
            }
        }

        private static JObject LoadL10nJson(string languageCode)
        {
            var candidates = new List<string>();

            var pkgInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(LanguageManager).Assembly);
            if (pkgInfo != null && !string.IsNullOrWhiteSpace(pkgInfo.resolvedPath))
            {
                candidates.Add(Path.Combine(pkgInfo.resolvedPath, "Editor", "L10n", "Resources", languageCode + ".json"));
            }

            candidates.Add(Path.Combine("Packages", "com.agentcore.unity", "Editor", "L10n", "Resources", languageCode + ".json"));
            candidates.Add(Path.Combine("Editor", "L10n", "Resources", languageCode + ".json"));

            foreach (var path in candidates)
            {
                if (File.Exists(path))
                {
                    return JObject.Parse(File.ReadAllText(path));
                }
            }

            Assert.Fail($"Unable to locate L10n resource '{languageCode}.json'. Tried: {string.Join(" | ", candidates)}");
            return null;
        }

        #endregion
    }
}
