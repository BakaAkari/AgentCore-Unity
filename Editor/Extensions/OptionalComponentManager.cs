using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using AgentCore.Editor.Utils;

namespace AgentCore.Editor.Extensions
{
    /// <summary>
    /// Provides enablement helpers for AgentCore optional components.
    /// </summary>
    public static class OptionalComponentManager
    {
        /// <summary>
        /// Scripting define symbol used to enable the Version Control optional component.
        /// </summary>
        public const string VcsDefine = "AGENTCORE_VCS";

        /// <summary>
        /// Scripting define symbol used to enable the Code Indexing optional component.
        /// </summary>
        public const string IndexingDefine = "AGENTCORE_INDEXING";

        // ─────────────────────────────────────────────────────────────────────
        // Project-level enablement tracking (v1.4.3)
        //
        // Background: AgentCoreSettings is a ScriptableSingleton stored in Unity's
        // global PreferencesFolder, which means it is SHARED across all Unity projects
        // on the same machine. Consequently, the settingsVersion-based migration
        // strategy for "auto-enable VCS on first install" only runs ONCE per machine,
        // not once per project. Users who install AgentCore into a second project see
        // Settings.asset already at CurrentVersion — the migration is skipped — and
        // the AGENTCORE_VCS define (which lives in the per-project PlayerSettings) is
        // never written for that new project.
        //
        // Fix: track "has this specific project been checked for default enablement"
        // and "has the user explicitly disabled VCS in this project" via EditorPrefs,
        // keyed by a stable hash of the project root path.
        // ─────────────────────────────────────────────────────────────────────

        private const string VcsDefaultCheckedKeyPrefix = "AgentCore.VcsDefaultChecked.";
        private const string VcsUserDisabledKeyPrefix = "AgentCore.VcsUserDisabled.";

        /// <summary>
        /// Gets all optional components known to AgentCore.
        /// </summary>
        /// <returns>Optional component metadata list.</returns>
        public static IReadOnlyList<OptionalComponentInfo> GetComponents()
        {
            return new[]
            {
                new OptionalComponentInfo(
                    "vcs",
                    "Version Control",
                    "Git / SVN / Perforce panel in Chat window.",
                    VcsDefine,
                    IsVcsEnabled()),
                new OptionalComponentInfo(
                    "indexing",
                    "Code Indexing (experimental)",
                    "Roslyn-based C# symbol index for search_code tool. May impact Editor performance on large projects.",
                    IndexingDefine,
                    IsIndexingEnabled())
            };
        }

        /// <summary>
        /// Checks whether the Version Control optional component is enabled for the active build target group.
        /// </summary>
        /// <returns>True if the AGENTCORE_VCS define is present.</returns>
        public static bool IsVcsEnabled()
        {
            return HasDefine(VcsDefine);
        }

        /// <summary>
        /// Enables or disables the Version Control optional component across all build target groups.
        /// </summary>
        /// <param name="enabled">Whether VCS should be enabled.</param>
        public static void SetVcsEnabled(bool enabled)
        {
            SetDefine(VcsDefine, enabled, userInitiated: true);
        }

        /// <summary>
        /// 自动默认启用 VCS（首次加载 / 域重载路径使用）。
        /// 与 <see cref="SetVcsEnabled"/> 一样写入 define 并请求编译，但不调用
        /// <c>AssetDatabase.SaveAssets()</c>、不强制 Refresh——启动阶段不应顺带保存用户项目里其它未保存的资产。
        /// define 已存在的分组完全不写。
        /// </summary>
        internal static void EnableVcsByDefault()
        {
            SetDefine(VcsDefine, true, userInitiated: false);
        }

        /// <summary>
        /// Checks whether the Code Indexing optional component is enabled for the active build target group.
        /// </summary>
        /// <returns>True if the AGENTCORE_INDEXING define is present.</returns>
        public static bool IsIndexingEnabled()
        {
            return HasDefine(IndexingDefine);
        }

        /// <summary>
        /// Enables or disables the Code Indexing optional component across all build target groups.
        /// </summary>
        /// <param name="enabled">Whether Code Indexing should be enabled.</param>
        public static void SetIndexingEnabled(bool enabled)
        {
            SetDefine(IndexingDefine, enabled, userInitiated: true);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Project-level VCS default enablement (v1.4.3)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Ensures VCS is enabled by default for the current Unity project, unless the user has
        /// explicitly disabled it in this project. Idempotent: safe to call every Editor startup.
        /// </summary>
        /// <remarks>
        /// This exists because <see cref="AgentCoreSettings"/> is stored in Unity's global
        /// PreferencesFolder and therefore its settingsVersion-based migration only runs once per
        /// machine, not once per project. This helper checks the current project independently.
        /// </remarks>
        public static void EnsureVcsDefaultForCurrentProject()
        {
            try
            {
                var projectKey = ComputeCurrentProjectKey();
                var checkedKey = VcsDefaultCheckedKeyPrefix + projectKey;
                var userDisabledKey = VcsUserDisabledKeyPrefix + projectKey;

                // User has explicitly disabled VCS in this project — respect that decision.
                if (EditorPrefs.GetBool(userDisabledKey, false))
                    return;

                // Fast path: already enabled AND already flagged as checked — nothing to do.
                // NOTE (v1.4.4): The previous implementation set the "checked" flag unconditionally
                // even when SetVcsEnabled() failed silently (e.g. PlayerSettings not ready during
                // early Editor bootstrap). That created a deadlock: the flag prevented all future
                // retries, so VCS stayed disabled forever. Fix: only set the flag AFTER
                // IsVcsEnabled() returns true, which confirms the define was actually written.
                if (IsVcsEnabled())
                {
                    if (!EditorPrefs.GetBool(checkedKey, false))
                        EditorPrefs.SetBool(checkedKey, true);
                    return;
                }

                // VCS is not enabled yet AND user has not disabled it → apply default.
                // 走自动路径：只在 define 缺失的分组写入，不调用 AssetDatabase.SaveAssets()。
                EnableVcsByDefault();

                // Verify the write succeeded before marking the project as "checked".
                // If PlayerSettings.SetScriptingDefineSymbolsForGroup silently failed (rare but
                // possible during Editor startup races), we intentionally leave the flag unset
                // so the next Editor startup retries automatically.
                if (IsVcsEnabled())
                {
                    EditorPrefs.SetBool(checkedKey, true);
                    AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] VCS auto-enabled for this project (project key: {projectKey})");
                }
                else
                {
                    AgentCoreLog.Warning($"[AgentCore] VCS auto-enable attempted but define not present after write; will retry on next Editor startup (project key: {projectKey})");
                }
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore] EnsureVcsDefaultForCurrentProject failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Records the user's intent when they manually toggle VCS via the Settings UI, so the
        /// project-level auto-enable machinery does not later override their choice.
        /// </summary>
        /// <param name="enabled">The value the user just set VCS to.</param>
        public static void RecordVcsUserIntent(bool enabled)
        {
            try
            {
                var projectKey = ComputeCurrentProjectKey();
                var userDisabledKey = VcsUserDisabledKeyPrefix + projectKey;
                var checkedKey = VcsDefaultCheckedKeyPrefix + projectKey;

                if (enabled)
                {
                    // User re-enabled VCS — clear any "user disabled" marker.
                    EditorPrefs.DeleteKey(userDisabledKey);
                }
                else
                {
                    // User explicitly disabled — mark so we never re-enable automatically.
                    EditorPrefs.SetBool(userDisabledKey, true);
                }

                // Either way, treat this project as checked so auto-enable never runs again.
                EditorPrefs.SetBool(checkedKey, true);
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore] RecordVcsUserIntent failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Computes a stable, short key derived from the current Unity project root path.
        /// Uses SHA256 truncated to 16 hex chars — collision risk between typical developer
        /// project counts is negligible and the EditorPrefs key length stays manageable.
        /// </summary>
        private static string ComputeCurrentProjectKey()
        {
            // Application.dataPath ends with "/Assets"; strip it to get the project root.
            var dataPath = Application.dataPath ?? string.Empty;
            var projectRoot = string.IsNullOrEmpty(dataPath)
                ? "(unknown-project)"
                : System.IO.Path.GetDirectoryName(dataPath) ?? dataPath;

            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(projectRoot));
                var sb = new StringBuilder(16);
                for (int i = 0; i < 8; i++)
                    sb.Append(bytes[i].ToString("x2"));
                return sb.ToString();
            }
        }

        private static bool HasDefine(string define)
        {
            var defines = GetDefines(EditorUserBuildSettings.selectedBuildTargetGroup);
            return defines.Contains(define);
        }

        /// <summary>
        /// 在 define 列表字符串中加入 / 移除一个符号，保持其余符号原有顺序。
        /// 已是目标状态时原样返回 <paramref name="raw"/>（调用方据此判断是否需要写入）。
        /// </summary>
        internal static string ApplyDefine(string raw, string define, bool enabled)
        {
            raw ??= string.Empty;
            var items = raw.Split(';')
                .Select(value => value.Trim())
                .Where(value => !string.IsNullOrEmpty(value))
                .ToList();

            if (items.Contains(define) == enabled)
                return raw;

            if (enabled)
                items.Add(define);
            else
                items.RemoveAll(value => value == define);
            return string.Join(";", items);
        }

        private static void SetDefine(string define, bool enabled, bool userInitiated)
        {
            bool anyChanged = false;

            foreach (BuildTargetGroup group in Enum.GetValues(typeof(BuildTargetGroup)))
            {
                if (!IsValidBuildTargetGroup(group))
                    continue;

                // 先比较再写：已是目标状态的分组不调用 SetScriptingDefineSymbols，
                // 避免无意义地标脏 ProjectSettings、或因 HashSet 重排其它 define 的顺序。
                string raw;
                try
                {
                    raw = ScriptingDefineHelper.GetDefines(group) ?? string.Empty;
                }
                catch (Exception ex)
                {
                    AgentCoreLog.Warning($"[AgentCore] Failed to read scripting defines for build target group {group}: {ex.Message}");
                    continue;
                }

                var updated = ApplyDefine(raw, define, enabled);
                if (string.Equals(updated, raw, StringComparison.Ordinal))
                    continue;

                try
                {
                    ScriptingDefineHelper.SetDefines(group, updated);
                    anyChanged = true;
                }
                catch (Exception ex)
                {
                    AgentCoreLog.Warning($"[AgentCore] Failed to set scripting define '{define}' for build target group {group}: {ex.Message}");
                }
            }

            if (!anyChanged)
                return;

            // ─── v1.4.7 fix: 强制 flush PlayerSettings 后再触发编译 ───────────────
            // 问题历史：v1.4.6 及之前的实现直接调用 CompilationPipeline.RequestScriptCompilation()，
            // 但 Unity 2021.3~2022.3 上 PlayerSettings.SetScriptingDefineSymbolsForGroup 的写入是
            // **延迟持久化的**——只打 dirty flag，实际序列化到 ProjectSettings.asset 要等
            // Editor 的下一个 idle tick / focus lost 事件才发生。所以 RequestScriptCompilation
            // 立即触发时，CompilationPipeline 读到的仍是旧 defines，编译请求被内部去重丢弃。
            // 用户反馈：勾选/取消 VCS 或 Indexing 后必须切窗口再切回来才会触发脚本编译，正是
            // 因为 focus lost 事件强制 flush 了 PlayerSettings，Unity 自己检测到 defines 变了才编译。
            //
            // 修复：显式调用 PlayerSettings.SaveSettings 立即持久化 defines（Unity 2020.1+）。
            // 该 API 会强制把当前 PlayerSettings 内存状态序列化到 ProjectSettings/ProjectSettings.asset，
            // 保证 CompilationPipeline 后续读取到最新 defines。
            // 备用/兼容：也调用 AssetDatabase.SaveAssets() 作为通用 flush 兜底，覆盖任意 Unity 版本。
            //
            // 顺序至关重要：
            //   1. SaveAssets / SaveSettings — flush PlayerSettings 到磁盘（关键）
            //   2. Refresh — 让 AssetDatabase 重新扫描（部分场景下需要）
            //   3. RequestScriptCompilation — 现在管线能读到最新 defines，会真正触发编译
            //   4. Registry.Refresh — 让扩展注册表基于新状态刷新（不影响编译）
            //
            // 自动默认启用路径（首次加载 / 域重载，userInitiated=false）跳过 SaveAssets 与 Refresh：
            // SaveAssets 会把用户项目里所有未保存的资产一并写盘，启动阶段不应有这种副作用；
            // SaveSettings 已足够让编译管线读到新 define。
            try
            {
                // PlayerSettings.SaveSettings() 存在于 Unity 2020.1+；用反射调用以兼容更旧版本。
                // 若不存在，AssetDatabase.SaveAssets() 一样能覆盖大多数情形。
                var saveSettingsMethod = typeof(PlayerSettings).GetMethod(
                    "SaveSettings",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                saveSettingsMethod?.Invoke(null, null);
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore] PlayerSettings.SaveSettings() invocation failed: {ex.Message} (continuing with AssetDatabase.SaveAssets fallback)");
            }

            if (userInitiated)
            {
                try
                {
                    // 通用兜底：SaveAssets 会 flush 所有 dirty 的 ScriptableObject / ProjectSettings 到磁盘。
                    AssetDatabase.SaveAssets();
                }
                catch (Exception ex)
                {
                    AgentCoreLog.Warning($"[AgentCore] AssetDatabase.SaveAssets() failed after define change: {ex.Message}");
                }

                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            }

            CompilationPipeline.RequestScriptCompilation();
            AgentCoreExtensionRegistry.Refresh();
        }

        private static HashSet<string> GetDefines(BuildTargetGroup group)
        {
            try
            {
                var raw = ScriptingDefineHelper.GetDefines(group);
                return new HashSet<string>(
                    raw.Split(';')
                        .Select(value => value.Trim())
                        .Where(value => !string.IsNullOrEmpty(value)));
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore] Failed to read scripting defines for build target group {group}: {ex.Message}");
                return new HashSet<string>();
            }
        }

        private static bool IsValidBuildTargetGroup(BuildTargetGroup group)
        {
            // Unknown is not a real build target group.
            // Editor-only tools rely on the active platform group; writing defines to all
            // valid platform groups ensures component state survives build target switches.
            return group != BuildTargetGroup.Unknown;
        }
    }

    /// <summary>
    /// Describes an AgentCore optional component.
    /// </summary>
    public sealed class OptionalComponentInfo
    {
        /// <summary>
        /// Creates optional component metadata.
        /// </summary>
        /// <param name="id">Stable component identifier.</param>
        /// <param name="displayName">Human-readable component name.</param>
        /// <param name="description">Component description.</param>
        /// <param name="defineSymbol">Scripting define symbol controlling this component.</param>
        /// <param name="enabled">Current enablement state.</param>
        public OptionalComponentInfo(string id, string displayName, string description, string defineSymbol, bool enabled)
        {
            Id = id;
            DisplayName = displayName;
            Description = description;
            DefineSymbol = defineSymbol;
            Enabled = enabled;
        }

        /// <summary>
        /// Gets the stable component identifier.
        /// </summary>
        public string Id { get; }

        /// <summary>
        /// Gets the human-readable component name.
        /// </summary>
        public string DisplayName { get; }

        /// <summary>
        /// Gets the component description.
        /// </summary>
        public string Description { get; }

        /// <summary>
        /// Gets the scripting define symbol controlling this component.
        /// </summary>
        public string DefineSymbol { get; }

        /// <summary>
        /// Gets whether this component is currently enabled.
        /// </summary>
        public bool Enabled { get; }
    }
}
