using System;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Extensions
{
    /// <summary>
    /// Dedicated bootstrap for AgentCore optional component defaults. Runs independently of
    /// <see cref="Config.AgentCoreSettings"/> so that default-enablement (currently: VCS) works
    /// even in edge cases where the Settings singleton's static constructor is delayed, throws,
    /// or races with early Editor startup.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Historical context (v1.4.3 → v1.4.4): the auto-enable logic used to live inside
    /// <c>AgentCoreSettings</c>'s static constructor. Two failure modes were observed on fresh
    /// installs:
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///     <c>PlayerSettings.SetScriptingDefineSymbolsForGroup</c> can silently fail during the
    ///     first Editor bootstrap after package import, because the compilation pipeline is still
    ///     initializing. The previous code marked the project as "checked" unconditionally, which
    ///     locked the state permanently.
    ///   </description></item>
    ///   <item><description>
    ///     <c>ScriptableSingleton&lt;AgentCoreSettings&gt;.instance</c> access order depends on
    ///     which Editor code loads first. A user-reported case showed the singleton path not
    ///     firing at all on a brand-new project until the user opened the AgentCore window.
    ///   </description></item>
    /// </list>
    /// <para>
    /// Fix strategy: use <see cref="InitializeOnLoadMethodAttribute"/> to guarantee this runs
    /// after every Editor startup / domain reload; defer the actual write via
    /// <see cref="EditorApplication.delayCall"/> so Unity's PlayerSettings service is ready;
    /// and let <see cref="OptionalComponentManager.EnsureVcsDefaultForCurrentProject"/> itself
    /// be the single source of truth for idempotency + retry (only marks the project as
    /// "checked" when the define is verified present).
    /// </para>
    /// </remarks>
    internal static class OptionalComponentDefaultsBootstrap
    {
        /// <summary>
        /// Invoked automatically by Unity after every domain reload. Schedules the default
        /// enablement check to run on the next editor update tick, avoiding early-startup races.
        /// </summary>
        [InitializeOnLoadMethod]
        private static void Bootstrap()
        {
            // Defer to the next tick so PlayerSettings is fully initialized before we attempt to
            // write scripting defines. Running synchronously here can hit the exact race we are
            // trying to fix.
            EditorApplication.delayCall += ApplyDefaults;
        }

        /// <summary>
        /// Runs the default-enablement check exactly once per delayCall dispatch.
        /// Idempotent — safe to invoke multiple times per session.
        /// </summary>
        private static void ApplyDefaults()
        {
            try
            {
                // VCS: default enabled for every new project unless the user has explicitly
                // disabled it. EnsureVcsDefaultForCurrentProject() internally uses EditorPrefs
                // to remember per-project state and only marks the project as "checked" after
                // verifying the define was actually written.
                OptionalComponentManager.EnsureVcsDefaultForCurrentProject();

                // Code Indexing: intentionally NOT auto-enabled. It is experimental and can
                // impact Editor responsiveness on large projects. Users must opt-in via
                // Project Settings > AgentCore > Tools & Extensions.
                // (Left as a documented no-op so future readers understand the asymmetry.)
            }
            catch (Exception ex)
            {
                // Never let bootstrap exceptions escape — they would poison Editor startup for
                // the entire session. Log loudly instead so the user can report the issue.
                Debug.LogError($"[AgentCore] OptionalComponentDefaultsBootstrap failed: {ex.Message}\n{ex.StackTrace}");
            }
        }
    }
}
