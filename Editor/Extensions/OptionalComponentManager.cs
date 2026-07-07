using System;
using System.Collections.Generic;
using System.Linq;
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
