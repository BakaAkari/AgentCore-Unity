using System;
using System.Collections.Generic;
using AgentCore.Editor.Tools.Safety;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Config.Settings.Pages
{
    /// <summary>
    /// 权限规则设置页（P2）：列出 / 新增 / 编辑 / 删除 / 启停 allow · ask · deny 规则，区分用户与项目来源。
    /// <para>
    /// 编辑在内存工作副本上进行，点击"保存"才写盘（只写有变化的文件）；
    /// 项目规则的"本地停用"是个人选择，立即写入用户文件，不改团队文件。
    /// </para>
    /// </summary>
    public sealed class PermissionsSettingsPage : IAgentCoreSettingsPage
    {
        private static readonly string[] EffectOptions = { "allow", "ask", "deny" };
        private static readonly string[] ScopeOptions = { "User", "Project" };

        /// <summary>编辑中的一行。</summary>
        private sealed class RowState
        {
            public PermissionRule Rule;
            public PermissionRule Original;
            public PermissionRuleScope OriginalScope;
            public PermissionRuleScope Scope;
            public bool IsNew;
            public bool Deleted;

            public bool IsDirty =>
                IsNew ? !Deleted : (Deleted || Scope != OriginalScope || !RuleEquals(Rule, Original));
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
        public string Title => AgentCore.Editor.L10n.Loc.Tr("settings.permissions.title", "Permissions");

        /// <inheritdoc />
        public string Description => AgentCore.Editor.L10n.Loc.Tr(
            "settings.permissions.description",
            "按工具 / action / 路径配置 allow · ask · deny 规则，细化工具确认行为。");

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
                AgentCore.Editor.L10n.Loc.Tr("settings.permissions.overview.title", "How rules work"),
                null,
                () =>
                {
                    context.Ui.DrawHelpText(AgentCore.Editor.L10n.Loc.Tr(
                        "settings.permissions.overview.precedence",
                        "优先级 deny > ask > allow（任一 deny 命中即拒绝，其次 ask，最后 allow）。用户规则与项目规则一起按同一优先级评估；未命中任何规则时行为与没有规则完全一致。"));
                    context.Ui.DrawHelpText(AgentCore.Editor.L10n.Loc.Tr(
                        "settings.permissions.overview.effects",
                        "allow：跳过确认（含删除 / 执行代码等高影响操作）。ask：总是弹确认，Trust Low/Med 不会自动放行。deny：拒绝执行，作为普通工具失败返回给模型。"));
                    context.Ui.DrawHelpText(AgentCore.Editor.L10n.Loc.Tr(
                        "settings.permissions.overview.yolo",
                        "YOLO（All）会话忽略全部规则，包括 deny。batch_execute 按子操作逐条匹配。"));
                    context.Ui.DrawHelpText(AgentCore.Editor.L10n.Loc.Tr(
                        "settings.permissions.overview.syntax",
                        "工具：精确名或 * 通配，可写 manage_asset.delete / manage_script.* 简写；路径：项目相对、正斜杠，支持 **（如 Assets/Plugins/**）。deny/ask 任一路径命中即生效，allow 需全部路径命中。"));

                    EditorGUILayout.Space(4);
                    DrawFileRow(context, "User", store.UserFilePath, store.UserLoadError);
                    DrawFileRow(context, "Project", store.ProjectFilePath, store.ProjectLoadError);
                });
        }

        private static void DrawFileRow(AgentCoreSettingsContext context, string label, string path, string error)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, path, EditorStyles.miniLabel);
            using (new EditorGUI.DisabledScope(!System.IO.File.Exists(path)))
            {
                if (GUILayout.Button(AgentCore.Editor.L10n.Loc.Tr("settings.permissions.reveal", "显示"), GUILayout.Width(60)))
                    EditorUtility.RevealInFinder(path);
            }
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(error))
            {
                context.Ui.DrawStatusLabel(
                    string.Format(AgentCore.Editor.L10n.Loc.Tr(
                        "settings.permissions.loadError",
                        "文件解析失败，其中的规则被忽略：{0}"), error),
                    SettingsStatusLevel.Error,
                    miniLabel: true);
            }
        }

        // ── 规则列表 ──

        private void DrawRulesCard(AgentCoreSettingsContext context)
        {
            context.Ui.DrawCard(
                AgentCore.Editor.L10n.Loc.Tr("settings.permissions.rules.title", "Rules"),
                AgentCore.Editor.L10n.Loc.Tr(
                    "settings.permissions.rules.description",
                    "User 规则只属于你（UserSettings，不进版本库）；Project 规则是团队默认（ProjectSettings，提交到版本库），可在本地单独停用。"),
                () =>
                {
                    DrawRuleSection(context, PermissionRuleScope.User);
                    EditorGUILayout.Space(6);
                    DrawRuleSection(context, PermissionRuleScope.Project);
                    EditorGUILayout.Space(6);
                    DrawSaveBar(context);
                });
        }

        private void DrawRuleSection(AgentCoreSettingsContext context, PermissionRuleScope scope)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                scope == PermissionRuleScope.User
                    ? AgentCore.Editor.L10n.Loc.Tr("settings.permissions.userRules", "User rules")
                    : AgentCore.Editor.L10n.Loc.Tr("settings.permissions.projectRules", "Project rules"),
                EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(AgentCore.Editor.L10n.Loc.Tr("settings.permissions.add", "+ 添加规则"), GUILayout.Width(100)))
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
                    OriginalScope = scope,
                    Scope = scope,
                    IsNew = true
                });
            }
            EditorGUILayout.EndHorizontal();

            bool any = false;
            foreach (var row in _rows)
            {
                if (row.Scope != scope || row.Deleted)
                    continue;
                any = true;
                DrawRuleRow(row);
            }

            if (!any)
            {
                context.Ui.DrawHelpText(AgentCore.Editor.L10n.Loc.Tr("settings.permissions.empty", "（无规则）"));
            }
        }

        private void DrawRuleRow(RowState row)
        {
            var rule = row.Rule;
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            rule.enabled = EditorGUILayout.ToggleLeft(
                AgentCore.Editor.L10n.Loc.Tr("settings.permissions.enabled", "启用"),
                rule.enabled,
                GUILayout.Width(60));

            int effectIndex = Array.IndexOf(EffectOptions, (rule.effect ?? string.Empty).Trim().ToLowerInvariant());
            if (effectIndex < 0) effectIndex = 1;
            int newEffect = EditorGUILayout.Popup(effectIndex, EffectOptions, GUILayout.Width(70));
            if (newEffect != effectIndex || !rule.TryGetEffect(out _))
                rule.effect = EffectOptions[newEffect];

            int scopeIndex = (int)row.Scope;
            int newScope = EditorGUILayout.Popup(scopeIndex, ScopeOptions, GUILayout.Width(80));
            if (newScope != scopeIndex)
                row.Scope = (PermissionRuleScope)newScope;

            GUILayout.FlexibleSpace();

            if (row.OriginalScope == PermissionRuleScope.Project && !row.IsNew && !string.IsNullOrEmpty(rule.id))
            {
                var store = PermissionRulesStore.Default;
                bool locallyDisabled = store.IsProjectRuleDisabledLocally(rule.id);
                bool newLocallyDisabled = EditorGUILayout.ToggleLeft(
                    new GUIContent(
                        AgentCore.Editor.L10n.Loc.Tr("settings.permissions.disableLocally", "本地停用"),
                        AgentCore.Editor.L10n.Loc.Tr(
                            "settings.permissions.disableLocallyTooltip",
                            "仅对你生效（写入用户文件），不修改团队共享的项目规则")),
                    locallyDisabled,
                    GUILayout.Width(80));
                if (newLocallyDisabled != locallyDisabled)
                {
                    TryRun(() => store.SetProjectRuleDisabledLocally(rule.id, newLocallyDisabled));
                }
            }

            if (GUILayout.Button(AgentCore.Editor.L10n.Loc.Tr("settings.permissions.delete", "删除"), GUILayout.Width(50)))
            {
                // 只做标记（遍历中不能删列表元素）；未保存的新行被标记后直接不再显示、也不会写盘
                row.Deleted = true;
            }
            EditorGUILayout.EndHorizontal();

            rule.tool = EditorGUILayout.TextField(
                new GUIContent(
                    AgentCore.Editor.L10n.Loc.Tr("settings.permissions.tool", "工具"),
                    AgentCore.Editor.L10n.Loc.Tr("settings.permissions.toolTooltip", "工具名，支持 * 通配；也可写 tool.action 简写")),
                rule.tool ?? string.Empty);
            rule.action = EditorGUILayout.TextField(
                new GUIContent(
                    AgentCore.Editor.L10n.Loc.Tr("settings.permissions.action", "Action"),
                    AgentCore.Editor.L10n.Loc.Tr("settings.permissions.actionTooltip", "可选；为空表示任意 action，支持 * 通配")),
                rule.action ?? string.Empty);
            rule.path = EditorGUILayout.TextField(
                new GUIContent(
                    AgentCore.Editor.L10n.Loc.Tr("settings.permissions.path", "路径"),
                    AgentCore.Editor.L10n.Loc.Tr("settings.permissions.pathTooltip", "可选；项目相对路径 glob，如 Assets/Plugins/**")),
                rule.path ?? string.Empty);
            rule.note = EditorGUILayout.TextField(
                AgentCore.Editor.L10n.Loc.Tr("settings.permissions.note", "备注"),
                rule.note ?? string.Empty);

            if (row.IsDirty)
            {
                EditorGUILayout.LabelField(AgentCore.Editor.L10n.Loc.Tr("settings.permissions.unsaved", "（未保存）"), EditorStyles.miniLabel);
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
                if (GUILayout.Button(AgentCore.Editor.L10n.Loc.Tr("settings.permissions.save", "保存"), GUILayout.Width(90)))
                    SaveRows();
                if (GUILayout.Button(AgentCore.Editor.L10n.Loc.Tr("settings.permissions.revert", "撤销修改"), GUILayout.Width(90)))
                    ReloadRows();
            }

            if (GUILayout.Button(AgentCore.Editor.L10n.Loc.Tr("settings.permissions.reload", "从磁盘重新加载"), GUILayout.Width(120)))
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
                AgentCore.Editor.L10n.Loc.Tr("settings.permissions.test.title", "Test a call"),
                AgentCore.Editor.L10n.Loc.Tr(
                    "settings.permissions.test.description",
                    "输入工具名 / action / 路径，查看当前（已保存的）规则会如何处理这次调用。"),
                () =>
                {
                    _testTool = EditorGUILayout.TextField(AgentCore.Editor.L10n.Loc.Tr("settings.permissions.tool", "工具"), _testTool ?? string.Empty);
                    _testAction = EditorGUILayout.TextField(AgentCore.Editor.L10n.Loc.Tr("settings.permissions.action", "Action"), _testAction ?? string.Empty);
                    _testPath = EditorGUILayout.TextField(AgentCore.Editor.L10n.Loc.Tr("settings.permissions.path", "路径"), _testPath ?? string.Empty);

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
                            AgentCore.Editor.L10n.Loc.Tr("settings.permissions.test.none", "未命中规则：按默认风险策略处理。"),
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
            var store = PermissionRulesStore.Default;
            foreach (var scope in new[] { PermissionRuleScope.User, PermissionRuleScope.Project })
            {
                foreach (var rule in store.GetRules(scope))
                {
                    if (string.IsNullOrEmpty(rule.id))
                        rule.id = PermissionRule.NewId();
                    _rows.Add(new RowState
                    {
                        Rule = rule,
                        Original = rule.Clone(),
                        OriginalScope = scope,
                        Scope = scope
                    });
                }
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
                            store.RemoveRule(r.OriginalScope, r.Original.id);
                    }
                    else if (r.IsNew)
                    {
                        store.AddRule(r.Scope, Sanitize(r.Rule));
                    }
                    else if (!store.UpdateRule(r.OriginalScope, Sanitize(r.Rule), r.Scope))
                    {
                        // 文件在外部被改动、原规则已不存在：按新增处理
                        store.AddRule(r.Scope, Sanitize(r.Rule));
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
                    AgentCore.Editor.L10n.Loc.Tr("settings.permissions.saveError", "保存失败：{0}"),
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
