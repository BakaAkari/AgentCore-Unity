using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Compilation;

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
                    IsVcsEnabled())
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
        /// Enables or disables the Version Control optional component for the active build target group.
        /// </summary>
        /// <param name="enabled">Whether VCS should be enabled.</param>
        public static void SetVcsEnabled(bool enabled)
        {
            SetDefine(VcsDefine, enabled);
        }

        private static bool HasDefine(string define)
        {
            var defines = GetDefines();
            return defines.Contains(define);
        }

        private static void SetDefine(string define, bool enabled)
        {
            var defines = GetDefines();
            bool changed;

            if (enabled)
            {
                changed = defines.Add(define);
            }
            else
            {
                changed = defines.Remove(define);
            }

            if (!changed)
                return;

            PlayerSettings.SetScriptingDefineSymbolsForGroup(EditorUserBuildSettings.selectedBuildTargetGroup, string.Join(";", defines));
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            CompilationPipeline.RequestScriptCompilation();
            AgentCoreExtensionRegistry.Refresh();
        }

        private static HashSet<string> GetDefines()
        {
            var raw = PlayerSettings.GetScriptingDefineSymbolsForGroup(EditorUserBuildSettings.selectedBuildTargetGroup);
            return new HashSet<string>(
                raw.Split(';')
                    .Select(value => value.Trim())
                    .Where(value => !string.IsNullOrEmpty(value)));
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
