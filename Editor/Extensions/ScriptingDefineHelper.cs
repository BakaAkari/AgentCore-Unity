using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Extensions
{
    /// <summary>
    /// Version-safe wrapper for reading and writing scripting define symbols.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unity 2023.1+ marked <c>PlayerSettings.GetScriptingDefineSymbolsForGroup</c> and
    /// <c>PlayerSettings.SetScriptingDefineSymbolsForGroup</c> as <c>[Obsolete]</c>,
    /// directing callers to the <c>NamedBuildTarget</c>-based overloads. The old API
    /// still functions (with warnings) through Unity 6, but will eventually be removed.
    /// </para>
    /// <para>
    /// This helper centralises the <c>#if</c> version switch so call sites stay clean.
    /// The new API path (<c>UNITY_2023_1_OR_NEWER</c>) converts <see cref="BuildTargetGroup"/>
    /// to <c>NamedBuildTarget</c> via <c>NamedBuildTarget.FromBuildTargetGroup</c>,
    /// available since Unity 2022.2.
    /// </para>
    /// </remarks>
    internal static class ScriptingDefineHelper
    {
        /// <summary>
        /// Reads the scripting define symbols for the specified build target group.
        /// </summary>
        public static string GetDefines(BuildTargetGroup group)
        {
#if UNITY_2023_1_OR_NEWER
            var namedTarget = UnityEditor.Build.NamedBuildTarget.FromBuildTargetGroup(group);
            return PlayerSettings.GetScriptingDefineSymbols(namedTarget);
#else
            return PlayerSettings.GetScriptingDefineSymbolsForGroup(group);
#endif
        }

        /// <summary>
        /// Writes the scripting define symbols for the specified build target group.
        /// </summary>
        public static void SetDefines(BuildTargetGroup group, string defines)
        {
#if UNITY_2023_1_OR_NEWER
            var namedTarget = UnityEditor.Build.NamedBuildTarget.FromBuildTargetGroup(group);
            PlayerSettings.SetScriptingDefineSymbols(namedTarget, defines);
#else
            PlayerSettings.SetScriptingDefineSymbolsForGroup(group, defines);
#endif
        }
    }
}
