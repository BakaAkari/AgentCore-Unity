﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AgentCore.Editor.Tools.Infrastructure;
using AgentCore.Editor.Utils;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Extensions
{
    /// <summary>
    /// Provides runtime enablement for AgentCore optional components.
    /// </summary>
    /// <remarks>
    /// Component assemblies always compile; enablement is a per-user, per-project EditorPrefs flag
    /// checked at runtime. Toggling never touches ProjectSettings and never requests a recompile.
    /// </remarks>
    public static class OptionalComponentManager
    {
        /// <summary>Component id of the Version Control optional component.</summary>
        public const string VcsComponentId = "vcs";

        /// <summary>Component id of the Code Indexing optional component.</summary>
        public const string IndexingComponentId = "indexing";

        /// <summary>Component id of the MCP Server optional component.</summary>
        public const string McpComponentId = "mcp";

        /// <summary>Legacy scripting define that used to gate the Version Control component.</summary>
        public const string VcsDefine = "AGENTCORE_VCS";

        /// <summary>Legacy scripting define that used to gate the Code Indexing component.</summary>
        public const string IndexingDefine = "AGENTCORE_INDEXING";

        /// <summary>Legacy scripting define that used to gate the MCP Server component.</summary>
        public const string McpDefine = "AGENTCORE_MCP";

        private const string VcsPrefsPrefix = "AgentCore.Component.Vcs.";
        private const string IndexingPrefsPrefix = "AgentCore.Component.Indexing.";
        private const string McpPrefsPrefix = "AgentCore.Component.Mcp.";

        // Pre-WP2 per-project marker written when the user turned VCS off in Settings.
        private const string LegacyVcsUserDisabledKeyPrefix = "AgentCore.VcsUserDisabled.";

        private static readonly string[] LegacyDefines = { VcsDefine, IndexingDefine, McpDefine };

        private static readonly object _lock = new object();
        private static readonly Dictionary<string, bool> _states = new Dictionary<string, bool>(StringComparer.Ordinal);
        private static string _projectKey;
        private static bool? _legacyDefinesCache;

        /// <summary>
        /// Raised on the main thread after a component was enabled or disabled, once the tool registry
        /// and extension registry have been rebuilt. Arguments: component id, new enabled state.
        /// </summary>
        public static event Action<string, bool> ComponentStateChanged;

        // EditorPrefs and Application.dataPath are main-thread only; warm the cache so background
        // callers (index workers, MCP transport) read the snapshot instead.
        [InitializeOnLoadMethod]
        private static void WarmUp()
        {
            try
            {
                IsVcsEnabled();
                IsIndexingEnabled();
                IsMcpEnabled();
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore] Optional component state warm-up failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets all optional components known to AgentCore.
        /// </summary>
        /// <returns>Optional component metadata list.</returns>
        public static IReadOnlyList<OptionalComponentInfo> GetComponents()
        {
            return new[]
            {
                new OptionalComponentInfo(
                    VcsComponentId,
                    "Version Control",
                    "Git / SVN / Perforce panel in Chat window.",
                    VcsDefine,
                    IsVcsEnabled()),
                new OptionalComponentInfo(
                    IndexingComponentId,
                    "Code Indexing (experimental)",
                    "Roslyn-based C# symbol index for search_code tool. May impact Editor performance on large projects.",
                    IndexingDefine,
                    IsIndexingEnabled()),
                new OptionalComponentInfo(
                    McpComponentId,
                    "MCP Server",
                    "Let external AI clients (Claude Code, Codex) use AgentCore's Unity tools over MCP. Same permission rules and review level as the Chat window.",
                    McpDefine,
                    IsMcpEnabled())
            };
        }

        /// <summary>
        /// Maps an optional component assembly name to its component id.
        /// </summary>
        /// <param name="assemblyName">Simple assembly name.</param>
        /// <returns>The component id, or null when the assembly is not an optional component.</returns>
        public static string GetComponentIdForAssembly(string assemblyName)
        {
            switch (assemblyName)
            {
                case "AgentCore.VCS.Editor": return VcsComponentId;
                case "AgentCore.Indexing.Editor": return IndexingComponentId;
                case "AgentCore.MCP.Editor": return McpComponentId;
                default: return null;
            }
        }

        /// <summary>
        /// Returns false only when <paramref name="assembly"/> belongs to a disabled optional component.
        /// </summary>
        /// <param name="assembly">Assembly to check.</param>
        /// <returns>True when contributions from the assembly may be registered.</returns>
        public static bool IsAssemblyEnabled(Assembly assembly)
        {
            var componentId = GetComponentIdForAssembly(assembly?.GetName().Name);
            return componentId == null || IsComponentEnabled(componentId);
        }

        /// <summary>
        /// Checks whether an optional component is enabled for the current project.
        /// </summary>
        /// <param name="componentId">One of <see cref="VcsComponentId"/>, <see cref="IndexingComponentId"/>, <see cref="McpComponentId"/>.</param>
        /// <returns>True when enabled. Unknown ids return false.</returns>
        public static bool IsComponentEnabled(string componentId)
        {
            if (GetPrefsPrefix(componentId) == null)
                return false;

            lock (_lock)
            {
                if (_states.TryGetValue(componentId, out var cached))
                    return cached;
            }

            var loaded = LoadState(componentId);
            lock (_lock)
            {
                _states[componentId] = loaded;
            }
            return loaded;
        }

        /// <summary>
        /// Enables or disables an optional component for the current project. Writes EditorPrefs only,
        /// rebuilds the tool and extension registries, then raises <see cref="ComponentStateChanged"/>.
        /// Must be called on the main thread.
        /// </summary>
        /// <param name="componentId">Component id.</param>
        /// <param name="enabled">Desired state.</param>
        public static void SetComponentEnabled(string componentId, bool enabled)
        {
            var prefix = GetPrefsPrefix(componentId);
            if (prefix == null)
                throw new ArgumentException($"Unknown optional component '{componentId}'.", nameof(componentId));

            if (IsComponentEnabled(componentId) == enabled)
                return;

            EditorPrefs.SetBool(prefix + GetProjectKey(), enabled);
            lock (_lock)
            {
                _states[componentId] = enabled;
            }

            AgentCoreLog.Info($"[AgentCore] Optional component '{componentId}' {(enabled ? "enabled" : "disabled")}");

            ToolAutoDiscovery.Reset();
            ToolAutoDiscovery.DiscoverAndRegisterAll();
            AgentCoreExtensionRegistry.Refresh();

            var handlers = ComponentStateChanged;
            if (handlers == null)
                return;

            foreach (Action<string, bool> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(componentId, enabled);
                }
                catch (Exception ex)
                {
                    AgentCoreLog.Warning($"[AgentCore] ComponentStateChanged handler failed for '{componentId}': {ex.Message}");
                }
            }
        }

        /// <summary>Checks whether the Version Control optional component is enabled.</summary>
        public static bool IsVcsEnabled() => IsComponentEnabled(VcsComponentId);

        /// <summary>Enables or disables the Version Control optional component.</summary>
        public static void SetVcsEnabled(bool enabled) => SetComponentEnabled(VcsComponentId, enabled);

        /// <summary>Checks whether the Code Indexing optional component is enabled.</summary>
        public static bool IsIndexingEnabled() => IsComponentEnabled(IndexingComponentId);

        /// <summary>Enables or disables the Code Indexing optional component.</summary>
        public static void SetIndexingEnabled(bool enabled) => SetComponentEnabled(IndexingComponentId, enabled);

        /// <summary>Checks whether the MCP Server optional component is enabled.</summary>
        public static bool IsMcpEnabled() => IsComponentEnabled(McpComponentId);

        /// <summary>Enables or disables the MCP Server optional component.</summary>
        public static void SetMcpEnabled(bool enabled) => SetComponentEnabled(McpComponentId, enabled);

        // ─── Legacy scripting define cleanup (manual only) ───────────────────

        /// <summary>
        /// Checks whether any valid build target group still carries a pre-WP2 component define.
        /// Read-only; the result is cached until the next domain reload or
        /// <see cref="RemoveLegacyScriptingDefines"/>.
        /// </summary>
        /// <returns>True if AGENTCORE_VCS / AGENTCORE_INDEXING / AGENTCORE_MCP is present anywhere.</returns>
        public static bool HasLegacyScriptingDefines()
        {
            if (_legacyDefinesCache.HasValue)
                return _legacyDefinesCache.Value;

            var found = false;
            foreach (var group in GetValidBuildTargetGroups())
            {
                if (!TryReadDefines(group, out var raw))
                    continue;
                if (!ReferenceEquals(RemoveLegacyDefines(raw), raw))
                {
                    found = true;
                    break;
                }
            }

            _legacyDefinesCache = found;
            return found;
        }

        /// <summary>
        /// Removes only the three legacy component defines from every valid build target group and
        /// saves ProjectSettings. Never called automatically; intended for the explicit Settings button.
        /// </summary>
        /// <returns>Number of build target groups that were modified.</returns>
        public static int RemoveLegacyScriptingDefines()
        {
            var changed = 0;
            foreach (var group in GetValidBuildTargetGroups())
            {
                if (!TryReadDefines(group, out var raw))
                    continue;

                var updated = RemoveLegacyDefines(raw);
                if (ReferenceEquals(updated, raw))
                    continue;

                try
                {
                    ScriptingDefineHelper.SetDefines(group, updated);
                    changed++;
                }
                catch (Exception ex)
                {
                    AgentCoreLog.Warning($"[AgentCore] Failed to remove legacy defines for build target group {group}: {ex.Message}");
                }
            }

            if (changed > 0)
            {
                // PlayerSettings.SaveSettings is not public on every supported Unity version.
                try
                {
                    typeof(PlayerSettings)
                        .GetMethod("SaveSettings", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, Type.EmptyTypes, null)
                        ?.Invoke(null, null);
                }
                catch (Exception ex)
                {
                    AgentCoreLog.Warning($"[AgentCore] PlayerSettings.SaveSettings() failed after legacy define cleanup: {ex.Message}");
                }
                AgentCoreLog.Info($"[AgentCore] Removed legacy optional component defines from {changed} build target group(s).");
            }

            _legacyDefinesCache = null;
            return changed;
        }

        /// <summary>
        /// Removes the legacy component defines from a define list, keeping the order of the rest.
        /// Returns <paramref name="raw"/> itself when there is nothing to remove.
        /// </summary>
        internal static string RemoveLegacyDefines(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return raw;

            var items = raw.Split(';')
                .Select(value => value.Trim())
                .Where(value => !string.IsNullOrEmpty(value))
                .ToList();

            if (!items.Any(value => LegacyDefines.Contains(value)))
                return raw;

            return string.Join(";", items.Where(value => !LegacyDefines.Contains(value)));
        }

        // ─── State persistence ───────────────────────────────────────────────

        /// <summary>Gets the per-project EditorPrefs key for a component, or null for unknown ids.</summary>
        internal static string GetPrefsKey(string componentId, string projectKey)
        {
            var prefix = GetPrefsPrefix(componentId);
            return prefix == null ? null : prefix + projectKey;
        }

        /// <summary>Default state for a project that has no stored value and no legacy state.</summary>
        internal static bool GetDefaultEnabled(string componentId) =>
            string.Equals(componentId, VcsComponentId, StringComparison.Ordinal);

        /// <summary>
        /// Initial state for a project without a stored EditorPrefs value. Carries pre-WP2 state over:
        /// VCS stays off if the user had turned it off; Indexing/MCP stay on if their define was set.
        /// </summary>
        internal static bool ResolveInitialState(string componentId, bool legacyDefinePresent, bool legacyVcsUserDisabled)
        {
            if (string.Equals(componentId, VcsComponentId, StringComparison.Ordinal))
                return !legacyVcsUserDisabled;
            return legacyDefinePresent || GetDefaultEnabled(componentId);
        }

        /// <summary>
        /// Computes a stable key from a Unity <c>Application.dataPath</c> value: SHA256 of the project
        /// root truncated to 16 hex chars.
        /// </summary>
        internal static string ComputeProjectKey(string dataPath)
        {
            // Application.dataPath ends with "/Assets"; strip it to get the project root.
            dataPath ??= string.Empty;
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

        /// <summary>Computes the stable key of the currently open Unity project.</summary>
        internal static string ComputeCurrentProjectKey() => ComputeProjectKey(Application.dataPath);

        private static string GetProjectKey()
        {
            lock (_lock)
            {
                if (_projectKey != null)
                    return _projectKey;
            }

            var key = ComputeCurrentProjectKey();
            lock (_lock)
            {
                _projectKey = key;
            }
            return key;
        }

        private static string GetPrefsPrefix(string componentId)
        {
            switch (componentId)
            {
                case VcsComponentId: return VcsPrefsPrefix;
                case IndexingComponentId: return IndexingPrefsPrefix;
                case McpComponentId: return McpPrefsPrefix;
                default: return null;
            }
        }

        private static bool LoadState(string componentId)
        {
            var projectKey = GetProjectKey();
            var key = GetPrefsKey(componentId, projectKey);
            if (EditorPrefs.HasKey(key))
                return EditorPrefs.GetBool(key, GetDefaultEnabled(componentId));

            var initial = ResolveInitialState(
                componentId,
                HasLegacyDefineOnActiveGroup(componentId),
                EditorPrefs.GetBool(LegacyVcsUserDisabledKeyPrefix + projectKey, false));
            EditorPrefs.SetBool(key, initial);
            return initial;
        }

        private static bool HasLegacyDefineOnActiveGroup(string componentId)
        {
            string define;
            switch (componentId)
            {
                case VcsComponentId: define = VcsDefine; break;
                case IndexingComponentId: define = IndexingDefine; break;
                case McpComponentId: define = McpDefine; break;
                default: return false;
            }

            if (!TryReadDefines(EditorUserBuildSettings.selectedBuildTargetGroup, out var raw) || string.IsNullOrEmpty(raw))
                return false;
            return raw.Split(';').Any(value => string.Equals(value.Trim(), define, StringComparison.Ordinal));
        }

        private static bool TryReadDefines(BuildTargetGroup group, out string raw)
        {
            try
            {
                raw = ScriptingDefineHelper.GetDefines(group) ?? string.Empty;
                return true;
            }
            catch (Exception)
            {
                // Unsupported groups throw from NamedBuildTarget; they cannot carry defines anyway.
                raw = null;
                return false;
            }
        }

        /// <summary>Build target groups that can carry scripting defines (non-Unknown, non-obsolete).</summary>
        internal static IEnumerable<BuildTargetGroup> GetValidBuildTargetGroups()
        {
            var seen = new HashSet<BuildTargetGroup>();
            foreach (var field in typeof(BuildTargetGroup).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field.IsDefined(typeof(ObsoleteAttribute), false))
                    continue;
                var group = (BuildTargetGroup)field.GetValue(null);
                if (group == BuildTargetGroup.Unknown || !seen.Add(group))
                    continue;
                yield return group;
            }
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
        /// <param name="defineSymbol">Legacy scripting define symbol that used to gate this component.</param>
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
        /// Gets the legacy scripting define symbol that used to gate this component. Only used for
        /// detecting and cleaning up pre-WP2 ProjectSettings; it no longer controls compilation.
        /// </summary>
        public string DefineSymbol { get; }

        /// <summary>
        /// Gets whether this component is currently enabled.
        /// </summary>
        public bool Enabled { get; }
    }
}
