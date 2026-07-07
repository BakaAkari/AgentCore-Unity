using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

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
                    "Git / SVN / Perforce tools and Hub panel.",
                    VcsDefine,
                    IsVcsEnabled()),
                new OptionalComponentInfo(
                    "indexing",
                    "Code Indexing",
                    "[Experimental] Roslyn-based C# symbol index for the search_code tool. Background indexing may impact Editor responsiveness on large projects; enable with caution.",
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
            SetDefine(VcsDefine, enabled);
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
            SetDefine(IndexingDefine, enabled);
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

                // Already checked and applied for this project — do nothing (preserves user's
                // current state whether they left it enabled or later toggled it off/on).
                if (EditorPrefs.GetBool(checkedKey, false))
                    return;

                if (!IsVcsEnabled())
                {
                    SetVcsEnabled(true);
                    Debug.Log($"[AgentCore] VCS auto-enabled for this project (project key: {projectKey})");
                }

                EditorPrefs.SetBool(checkedKey, true);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AgentCore] EnsureVcsDefaultForCurrentProject failed: {ex.Message}");
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
                Debug.LogWarning($"[AgentCore] RecordVcsUserIntent failed: {ex.Message}");
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

        private static void SetDefine(string define, bool enabled)
        {
            bool anyChanged = false;

            foreach (BuildTargetGroup group in Enum.GetValues(typeof(BuildTargetGroup)))
            {
                if (!IsValidBuildTargetGroup(group))
                    continue;

                var defines = GetDefines(group);
                bool changed = enabled ? defines.Add(define) : defines.Remove(define);
                if (!changed)
                    continue;

                try
                {
                    PlayerSettings.SetScriptingDefineSymbolsForGroup(group, string.Join(";", defines));
                    anyChanged = true;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[AgentCore] Failed to set scripting define '{define}' for build target group {group}: {ex.Message}");
                }
            }

            if (!anyChanged)
                return;

            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            CompilationPipeline.RequestScriptCompilation();
            AgentCoreExtensionRegistry.Refresh();
        }

        private static HashSet<string> GetDefines(BuildTargetGroup group)
        {
            try
            {
                var raw = PlayerSettings.GetScriptingDefineSymbolsForGroup(group);
                return new HashSet<string>(
                    raw.Split(';')
                        .Select(value => value.Trim())
                        .Where(value => !string.IsNullOrEmpty(value)));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AgentCore] Failed to read scripting defines for build target group {group}: {ex.Message}");
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
