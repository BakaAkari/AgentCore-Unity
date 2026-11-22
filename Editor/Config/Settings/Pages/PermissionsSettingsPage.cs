using System;
using System.Collections.Generic;
using AgentCore.Editor.Tools.Safety;
using AgentCore.Editor.L10n;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Config.Settings.Pages
{
    /// <summary>
    /// 权限规则设置页（P2）：列出 / 新增 / 编辑 / 删除 / 启停 allow · ask · deny 规则。
    /// <para>
    /// 编辑在内存工作副本上进行，点击"Save"才写用户文件（UserSettings）；
    /// 遗留项目规则文件（ProjectSettings）仅在已存在时只读展示，"Disable locally"立即写入用户文件，绝不修改项目文件。
    /// </para>
    /// </summary>
    public sealed class PermissionsSettingsPage : IAgentCoreSettingsPage
    {
        private static readonly string[] EffectOptions = { "allow", "ask", "deny" };

        /// <summary>编辑中的一行（仅用户规则）。</summary>
        private sealed class RowState
        {
            public PermissionRule Rule;
            public PermissionRule Original;
            public bool IsNew;
            public bool Deleted;

            public bool IsDirty =>
                IsNew ? !Deleted : (Deleted || !RuleEquals(Rule, Original));
        }

        private readonly List<RowState> _rows = new List<RowState>();
        private bool _loaded;
        private string _saveError;

        // "测试一次调用"
        private string _testTool = string.Empty;
        private string _testAction = string.Empty;
        private string _testPath = string.Empty;

        /// <inheritdoc />
        public string Id => "permissions";

        /// <inheritdoc />
        public string Title => Loc.Tr("settings.permissions.title", "Permissions");

        /// <inheritdoc />
        public string Description => Loc.Tr(
            "settings.permissions.description",
            "Configure allow · ask · deny rules by tool / action / path to fine-tune tool confirmation.");

        /// <inheritdoc />
        public int Order => 450;

        /// <inheritdoc />
        public void OnActivate(AgentCoreSettingsContext context)
        {
            ReloadRows();
        }

        /// <inheritdoc />
        public void OnDeactivate(AgentCoreSettingsContext context) { }

        /// <inheritdoc />
        public void Draw(AgentCoreSettingsContext context)
        {
            if (!_loaded)
                ReloadRows();

            DrawOverviewCard(context);
            EditorGUILayout.Space(8);
            DrawRulesCard(context);
            EditorGUILayout.Space(8);
            DrawTestCard(context);
        }

        // ── 概览 ──

        private static void DrawOverviewCard(AgentCoreSettingsContext context)
        {
            var store = PermissionRulesStore.Default;
            context.Ui.DrawCard(
                Loc.Tr("settings.permissions.overview.title", "How rules work"),
                null,
                () =>
                {
                    context.Ui.DrawHelpText(Loc.Tr(
                        "settings.permissions.overview.precedence",
                        "Precedence: deny > ask > allow (any matching deny rejects, then ask, then allow). User rules and legacy project rules are evaluated together with the same precedence; when no rule matches, behavior is identical to having no rules."));
                    context.Ui.DrawHelpText(Loc.Tr(
                        "settings.permissions.overview.effects",
                        "allow: skip confirmation (including high-impact operations such as delete / execute code). ask: always prompt; Trust Low/Med will not auto-approve. deny: refuse to run and return an ordinary tool failure to the model."));
                    context.Ui.DrawHelpText(Loc.Tr(
                        "settings.permissions.overview.yolo",
                        "YOLO (All) sessions ignore all rules, including deny. batch_execute matches each sub-operation individually."));
                    context.Ui.DrawHelpText(Loc.Tr(
                        "settings.permissions.overview.syntax",
                        "Tool: exact name or * wildcard; shorthand such as manage_asset.delete / manage_script.* is allowed. Path: project-relative with forward slashes, ** supported (e.g. Assets/Plugins/**). For deny/ask any matching path takes effect; allow requires all paths to match."));

                    EditorGUILayout.Space(4);
                    DrawFileRow(context, Loc.Tr("settings.permissions.file.user", "User"), store.UserFilePath, store.UserLoadError);
                    if (store.ProjectFileExists)
                        DrawFileRow(context, Loc.Tr("settings.permissions.file.projectLegacy", "Project (legacy, read-only)"), store.ProjectFilePath, store.ProjectLoadError);
                });
        }

        private static void DrawFileRow(AgentCoreSettingsContext context, string label, string path, string error)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, path, EditorStyles.miniLabel);
            using (new EditorGUI.DisabledScope(!System.IO.File.Exists(path)))
            {
                if (GUILayout.Button(Loc.Tr("settings.permissions.reveal", "Show"), GUILayout.MinWidth(60)))
                    EditorUtility.RevealInFinder(path);
            }
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(error))
            {
                context.Ui.DrawStatusLabel(
                    string.Format(Loc.Tr(
                        "settings.permissions.loadError",
                        "Failed to parse file; its rules are ignored: {0}"), error),
                    SettingsStatusLevel.Error,
                    miniLabel: true);
            }
        }

        // ── 规则列表 ──

        private void DrawRulesCard(AgentCoreSettingsContext context)
        {
            context.Ui.DrawCard(
                Loc.Tr("settings.permissions.rules.title", "Rules"),
                Loc.Tr(
                    "settings.permissions.rules.description",
                    "Rules are stored only in local UserSettings/AgentCore/PermissionRules.json and are never written to ProjectSettings."),
                () =>
                {
                    DrawUserRuleSection(context);
                    if (PermissionRulesStore.Default.ProjectFileExists)
                    {
                        EditorGUILayout.Space(6);
                        DrawLegacyProjectSection(context);
                    }
                    EditorGUILayout.Space(6);
                    DrawSaveBar(context);
                });
        }

        private void DrawUserRuleSection(AgentCoreSettingsContext context)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                Loc.Tr("settings.permissions.userRules", "User rules"),
                EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Loc.Tr("settings.permissions.add", "+ Add rule"), GUILayout.MinWidth(100)))
            {
                var rule = new PermissionRule
                {
                    id = PermissionRule.NewId(),
                    effect = "ask",
                    tool = "*",
                    enabled = true
                };
                _rows.Add(new RowState
                {
                    Rule = rule,
                    Original = rule.Clone(),
                    IsNew = true
                });
            }
            EditorGUILayout.EndHorizontal();

            bool any = false;
            foreach (var row in _rows)
            {
                if (row.Deleted)
                    continue;
                any = true;
                DrawRuleRow(row);
            }

            if (!any)
            {
                context.Ui.DrawHelpText(Loc.Tr("settings.permissions.empty", "(no rules)"));
            }
        }

        private void DrawLegacyProjectSection(AgentCoreSettingsContext context)
        {
            var store = PermissionRulesStore.Default;
            EditorGUILayout.LabelField(
                Loc.Tr("settings.permissions.projectRules", "Project rules (legacy, read-only)"),
                EditorStyles.boldLabel);
            context.Ui.DrawHelpText(Loc.Tr(
                "settings.permissions.projectRulesHint",
                "Legacy ProjectSettings/AgentCore/PermissionRules.json detected: its rules apply read-only and can be disabled locally; AgentCore never modifies that file."));

            var rules = store.GetRules(PermissionRuleScope.Project);
            if (rules.Count == 0)
            {
                context.Ui.DrawHelpText(Loc.Tr("settings.permissions.empty", "(no rules)"));
                return;
            }

            foreach (var rule in rules)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.BeginHorizontal();
                var effect = rule.TryGetEffect(out var e) ? PermissionRule.EffectToString(e) : "?";
                EditorGUILayout.LabelField(
                    (rule.enabled ? string.Empty : "[off] ") + effect + "  " + rule.DescribeMatcher());
                GUILayout.FlexibleSpace();
                if (!string.IsNullOrEmpty(rule.id))
                {
                    bool locallyDisabled = store.IsProjectRuleDisabledLocally(rule.id);
                    bool newLocallyDisabled = EditorGUILayout.ToggleLeft(
                        new GUIContent(
                            Loc.Tr("settings.permissions.disableLocally", "Disable locally"),
                            Loc.Tr(
                                "settings.permissions.disableLocallyTooltip",
                                "Only affects you (written to the user file); the project rules file is not modified")),
                        locallyDisabled,
                        GUILayout.MinWidth(110), GUILayout.ExpandWidth(false));
                    if (newLocallyDisabled != locallyDisabled)
                    {
                        var id = rule.id;
                        TryRun(() => store.SetProjectRuleDisabledLocally(id, newLocallyDisabled));
                    }
                }
                EditorGUILayout.EndHorizontal();
                if (!string.IsNullOrWhiteSpace(rule.note))
                    EditorGUILayout.LabelField(rule.note.Trim(), EditorStyles.miniLabel);
                EditorGUILayout.EndVertical();
            }
        }

        private void DrawRuleRow(RowState row)
        {
            var rule = row.Rule;
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            rule.enabled = EditorGUILayout.ToggleLeft(
                Loc.Tr("settings.permissions.enabled", "Enabled"),
                rule.enabled,
                GUILayout.MinWidth(70), GUILayout.ExpandWidth(false));

            int effectIndex = Array.IndexOf(EffectOptions, (rule.effect ?? string.Empty).Trim().ToLowerInvariant());
            if (effectIndex < 0) effectIndex = 1;
            int newEffect = EditorGUILayout.Popup(effectIndex, EffectOptions, GUILayout.Width(70));
            if (newEffect != effectIndex || !rule.TryGetEffect(out _))
                rule.effect = EffectOptions[newEffect];

            GUILayout.FlexibleSpace();

            if (GUILayout.Button(Loc.Tr("settings.permissions.delete", "Delete"), GUILayout.MinWidth(60)))
            {
                // 只做标记（遍历中不能删列表元素）；未保存的新行被标记后直接不再显示、也不会写盘
                row.Deleted = true;
            }
            EditorGUILayout.EndHorizontal();

            rule.tool = EditorGUILayout.TextField(
                new GUIContent(
                    Loc.Tr("settings.permissions.tool", "Tool"),
                    Loc.Tr("settings.permissions.toolTooltip", "Tool name, * wildcard supported; tool.action shorthand also works")),
                rule.tool ?? string.Empty);
            rule.action = EditorGUILayout.TextField(
                new GUIContent(
                    Loc.Tr("settings.permissions.action", "Action"),
                    Loc.Tr("settings.permissions.actionTooltip", "Optional; empty means any action, * wildcard supported")),
                rule.action ?? string.Empty);
            rule.path = EditorGUILayout.TextField(
                new GUIContent(
                    Loc.Tr("settings.permissions.path", "Path"),
                    Loc.Tr("settings.permissions.pathTooltip", "Optional; project-relative path glob, e.g. Assets/Plugins/**")),
                rule.path ?? string.Empty);
            rule.note = EditorGUILayout.TextField(
                Loc.Tr("settings.permissions.note", "Note"),
                rule.note ?? string.Empty);

            if (row.IsDirty)
            {
                EditorGUILayout.LabelField(Loc.Tr("settings.permissions.unsaved", "(unsaved)"), EditorStyles.miniLabel);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawSaveBar(AgentCoreSettingsContext context)
        {
            bool dirty = false;
            foreach (var row in _rows)
            {
                if (row.IsDirty)
                {
                    dirty = true;
                    break;
                }
            }

            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(!dirty))
            {
                if (GUILayout.Button(Loc.Tr("settings.permissions.save", "Save"), GUILayout.MinWidth(90)))
                    SaveRows();
                if (GUILayout.Button(Loc.Tr("settings.permissions.revert", "Revert changes"), GUILayout.MinWidth(110)))
                    ReloadRows();
            }

            if (GUILayout.Button(Loc.Tr("settings.permissions.reload", "Reload from disk"), GUILayout.MinWidth(120)))
            {
                PermissionRulesStore.Default.Reload();
                ReloadRows();
            }
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_saveError))
                context.Ui.DrawStatusLabel(_saveError, SettingsStatusLevel.Error, miniLabel: true);
        }

        // ── 测试一次调用 ──

        private void DrawTestCard(AgentCoreSettingsContext context)
        {
            context.Ui.DrawCard(
                Loc.Tr("settings.permissions.test.title", "Test a call"),
                Loc.Tr(
                    "settings.permissions.test.description",
                    "Enter a tool name / action / path to see how the current (saved) rules would handle that call."),
                () =>
                {
                    _testTool = EditorGUILayout.TextField(Loc.Tr("settings.permissions.tool", "Tool"), _testTool ?? string.Empty);
                    _testAction = EditorGUILayout.TextField(Loc.Tr("settings.permissions.action", "Action"), _testAction ?? string.Empty);
                    _testPath = EditorGUILayout.TextField(Loc.Tr("settings.permissions.path", "Path"), _testPath ?? string.Empty);

                    if (string.IsNullOrWhiteSpace(_testTool))
                        return;

                    var paths = new List<string>();
                    var normalized = PermissionRuleMatcher.NormalizePath(_testPath, SafePath.ProjectRoot);
                    if (!string.IsNullOrEmpty(normalized))
                        paths.Add(normalized);

                    var match = PermissionRuleMatcher.Evaluate(
                        PermissionRulesStore.Default.GetActiveRules(),
                        _testTool.Trim(),
                        _testAction?.Trim() ?? string.Empty,
                        paths);

                    if (!match.HasMatch)
                    {
                        context.Ui.DrawStatusLabel(
                            Loc.Tr("settings.permissions.test.none", "No rule matched: handled by the default risk policy."),
                            SettingsStatusLevel.None,
                            miniLabel: true);
                        return;
                    }

                    var level = match.Effect == PermissionRuleEffect.Deny
                        ? SettingsStatusLevel.Error
                        : match.Effect == PermissionRuleEffect.Ask ? SettingsStatusLevel.Warning : SettingsStatusLevel.Success;
                    context.Ui.DrawStatusLabel(match.Describe(), level, miniLabel: true);
                });
        }

        // ── 数据 ──

        private void ReloadRows()
        {
            _rows.Clear();
            _saveError = null;
            foreach (var rule in PermissionRulesStore.Default.GetRules(PermissionRuleScope.User))
            {
                if (string.IsNullOrEmpty(rule.id))
                    rule.id = PermissionRule.NewId();
                _rows.Add(new RowState
                {
                    Rule = rule,
                    Original = rule.Clone()
                });
            }
            _loaded = true;
        }

        private void SaveRows()
        {
            var store = PermissionRulesStore.Default;
            _saveError = null;
            foreach (var row in _rows)
            {
                if (!row.IsDirty)
                    continue;

                var r = row;
                bool ok = TryRun(() =>
                {
                    if (r.Deleted)
                    {
                        if (!r.IsNew)
                            store.RemoveRule(PermissionRuleScope.User, r.Original.id);
                    }
                    else if (r.IsNew)
                    {
                        store.AddRule(PermissionRuleScope.User, Sanitize(r.Rule));
                    }
                    else if (!store.UpdateRule(PermissionRuleScope.User, Sanitize(r.Rule)))
                    {
                        // 文件在外部被改动、原规则已不存在：按新增处理
                        store.AddRule(PermissionRuleScope.User, Sanitize(r.Rule));
                    }
                });
                if (!ok)
                    break;
            }

            if (string.IsNullOrEmpty(_saveError))
                ReloadRows();
        }

        private static PermissionRule Sanitize(PermissionRule rule)
        {
            var copy = rule.Clone();
            copy.tool = NullIfBlank(copy.tool) ?? "*";
            copy.action = NullIfBlank(copy.action);
            copy.path = NullIfBlank(copy.path);
            copy.note = NullIfBlank(copy.note);
            return copy;
        }

        private static string NullIfBlank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        private bool TryRun(Action action)
        {
            try
            {
                action();
                return true;
            }
            catch (Exception ex)
            {
                _saveError = string.Format(
                    Loc.Tr("settings.permissions.saveError", "Save failed: {0}"),
                    ex.Message);
                return false;
            }
        }

        private static bool RuleEquals(PermissionRule a, PermissionRule b)
        {
            if (a == null || b == null)
                return a == b;
            return a.enabled == b.enabled
                && string.Equals(a.effect ?? string.Empty, b.effect ?? string.Empty, StringComparison.Ordinal)
                && string.Equals(a.tool ?? string.Empty, b.tool ?? string.Empty, StringComparison.Ordinal)
                && string.Equals(a.action ?? string.Empty, b.action ?? string.Empty, StringComparison.Ordinal)
                && string.Equals(a.path ?? string.Empty, b.path ?? string.Empty, StringComparison.Ordinal)
                && string.Equals(a.note ?? string.Empty, b.note ?? string.Empty, StringComparison.Ordinal);
        }
    }
}
