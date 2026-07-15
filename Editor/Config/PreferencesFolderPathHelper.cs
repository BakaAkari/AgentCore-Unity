using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AgentCore.Editor.Utils;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace AgentCore.Editor.Config
{
    /// <summary>
    /// Ensures the target directory for a Unity <c>ScriptableSingleton</c> stored under
    /// <see cref="FilePathAttribute.Location.PreferencesFolder"/> exists before saving.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unity's <c>SaveToSerializedFileAndForget</c> internally performs a "Move temp → target"
    /// operation. When the target file's parent directory does not exist, the Move fails with
    /// "系统找不到指定的路径 / The system cannot find the path specified", leaving the Editor
    /// stuck behind a popup that reappears even after clicking "Try Again".
    /// </para>
    /// <para>
    /// <b>Path resolution</b> — Unity uses an <b>internal</b> version number for the preferences
    /// folder (e.g. <c>Editor-5.x</c> for Unity 5 through Unity 2021, <c>Editor-6.x</c> for
    /// Unity 6+). This is <b>not</b> the marketing version — <c>Application.unityVersion</c>
    /// reports <c>2021.3</c> but the folder is <c>Editor-5.x</c>. The helper resolves the path
    /// via reflection (primary) and a directory scan fallback that never guesses the version.
    /// </para>
    /// </remarks>
    [InitializeOnLoad]
    public static class PreferencesFolderPathHelper
    {
        /// <summary>
        /// The AgentCore subdirectory used by every AgentCore <c>ScriptableSingleton</c>.
        /// Must stay in sync with the <c>[FilePath("AgentCore/...", PreferencesFolder)]</c>
        /// attribute values on the singleton classes.
        /// </summary>
        public const string AgentCoreSubdirectory = "AgentCore";

        private static string _cachedPreferencesFolder;

        static PreferencesFolderPathHelper()
        {
            EnsureAgentCoreDirectory();
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            // Re-ensure after first frame — covers the gap between assembly load
            // and the first ScriptableSingleton save (which fires via delayCall).
            EditorApplication.delayCall += () => EnsureAgentCoreDirectory();
        }

        /// <summary>
        /// Called at the start of Domain Unload, before Unity's internal
        /// ScriptableSingleton auto-save. Re-checks the directory in case it
        /// was deleted externally or never existed (upgrade scenario).
        /// </summary>
        private static void OnBeforeAssemblyReload()
        {
            EnsureAgentCoreDirectory();
        }

        /// <summary>
        /// Ensures that <c>{PreferencesFolder}/AgentCore/</c> exists on disk.
        /// Safe to call repeatedly. Always checks <see cref="Directory.Exists"/>
        /// — no caching, because the directory can be deleted by Unity's own
        /// cleanup, antivirus software, or external processes between calls.
        /// </summary>
        public static bool EnsureAgentCoreDirectory()
        {
            try
            {
                var prefRoot = GetPreferencesFolder();
                if (string.IsNullOrEmpty(prefRoot))
                {
                    AgentCoreLog.Warning("[PreferencesFolderPathHelper] Could not resolve preferences folder.");
                    return false;
                }

                var target = Path.Combine(prefRoot, AgentCoreSubdirectory);
                if (!Directory.Exists(target))
                {
                    Directory.CreateDirectory(target);
                    AgentCoreLog.Info($"[PreferencesFolderPathHelper] Created directory: {target}");
                }

                return true;
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[PreferencesFolderPathHelper] Failed to ensure directory: {ex.Message}");
                return false;
            }
        }

        // ──────────────────────────────────────────────
        //  Path resolution
        // ──────────────────────────────────────────────

        /// <summary>
        /// Resolves Unity's Editor preferences folder path (the parent of the AgentCore subdirectory).
        /// Tries reflection first, then scans the Unity preferences root for existing Editor-*.x folders.
        /// </summary>
        private static string GetPreferencesFolder()
        {
            if (!string.IsNullOrEmpty(_cachedPreferencesFolder))
                return _cachedPreferencesFolder;

            _cachedPreferencesFolder = ResolveByReflection()
                ?? ResolveByDirectoryScan()
                ?? ResolveByHardcodedFallback()
                ?? string.Empty;

            if (string.IsNullOrEmpty(_cachedPreferencesFolder))
            {
                AgentCoreLog.Warning("[PreferencesFolderPathHelper] All path resolution methods failed.");
            }

            return _cachedPreferencesFolder;
        }

        /// <summary>
        /// Reflection into <c>UnityEditorInternal.InternalEditorUtility</c> — the same API
        /// Unity's <c>FilePathAttribute</c> uses internally. Tries multiple member signatures
        /// to cover different Unity versions.
        /// </summary>
        private static string ResolveByReflection()
        {
            const BindingFlags flags =
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

            var type = typeof(InternalEditorUtility);

            // Try as static property (most common).
            foreach (var name in new[] { "unityPreferencesFolder", "preferencesFolder" })
            {
                var prop = type.GetProperty(name, flags);
                if (prop != null)
                {
                    try
                    {
                        var value = prop.GetValue(null) as string;
                        if (!string.IsNullOrEmpty(value))
                        {
                            AgentCoreLog.Info($"[PreferencesFolderPathHelper] Resolved via reflection ({name}): {value}");
                            return value;
                        }
                    }
                    catch { /* try next */ }
                }
            }

            // Try as static method (some Unity versions expose it as a method).
            foreach (var name in new[] { "unityPreferencesFolder", "preferencesFolder", "GetPreferencesFolder" })
            {
                var method = type.GetMethod(name, flags);
                if (method != null && method.GetParameters().Length == 0)
                {
                    try
                    {
                        var value = method.Invoke(null, null) as string;
                        if (!string.IsNullOrEmpty(value))
                        {
                            AgentCoreLog.Info($"[PreferencesFolderPathHelper] Resolved via reflection method ({name}): {value}");
                            return value;
                        }
                    }
                    catch { /* try next */ }
                }
            }

            return null;
        }

        /// <summary>
        /// Scans the Unity preferences root directory (<c>%APPDATA%/Unity/</c> on Windows,
        /// <c>~/Library/Preferences/Unity/</c> on macOS) for existing <c>Editor-*.x</c>
        /// folders. Prefers the folder matching the current Unity version's internal
        /// number (5 for Unity 5-2022, 6 for Unity 6+); falls back to the most recently
        /// modified folder if the expected one is not found.
        /// </summary>
        private static string ResolveByDirectoryScan()
        {
            string unityRoot = GetUnityPreferencesRoot();
            if (string.IsNullOrEmpty(unityRoot) || !Directory.Exists(unityRoot))
                return null;

            // Determine the current Unity version's internal preferences folder number.
            // Unity 5 through 2022 → "5", Unity 6 (6000+) → "6".
            int major;
            if (!int.TryParse(Application.unityVersion.Split('.')[0], out major))
                major = 5;
            string expectedEditorDir = major >= 6000 ? "Editor-6.x" : "Editor-5.x";

            DirectoryInfo selected = null;
            try
            {
                var dirs = new DirectoryInfo(unityRoot).GetDirectories("Editor-*.x");

                // Prefer the directory matching the current Unity version.
                foreach (var dir in dirs)
                {
                    if (dir.Name == expectedEditorDir)
                    {
                        selected = dir;
                        break;
                    }
                }

                // Fall back to most recently modified (covers unusual Unity versions).
                if (selected == null)
                {
                    foreach (var dir in dirs)
                    {
                        if (selected == null || dir.LastWriteTimeUtc > selected.LastWriteTimeUtc)
                            selected = dir;
                    }
                }
            }
            catch
            {
                return null;
            }

            if (selected == null)
                return null;

            var result = Path.Combine(selected.FullName, "Preferences");
            AgentCoreLog.Info($"[PreferencesFolderPathHelper] Resolved via directory scan ({selected.Name}): {result}");
            return result;
        }

        /// <summary>
        /// Returns the Unity preferences root (the directory containing <c>Editor-*.x</c> folders).
        /// </summary>
        private static string GetUnityPreferencesRoot()
        {
#if UNITY_EDITOR_WIN
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return string.IsNullOrEmpty(appData) ? null : Path.Combine(appData, "Unity");
#elif UNITY_EDITOR_OSX
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrEmpty(home) ? null : Path.Combine(home, "Library", "Preferences", "Unity");
#else
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrEmpty(home) ? null : Path.Combine(home, ".config", "unity3d");
#endif
        }

        /// <summary>
        /// Final fallback when reflection and directory scan both fail (e.g. fresh Unity install
        /// where <c>Editor-*.x</c> has not been created yet). Unity uses a fixed internal version
        /// number for the preferences folder that does NOT match the marketing version:
        /// <list type="bullet">
        /// <item> Unity 5.x through 2022.x → <c>Editor-5.x</c> </item>
        /// <item> Unity 6.x (6000+) → <c>Editor-6.x</c> </item>
        /// </list>
        /// We determine the internal number via <see cref="Application.unityVersion"/> major version:
        /// versions ≥ 6000 use <c>6</c>; otherwise <c>5</c>. This is empirical knowledge derived
        /// from Unity's own <c>FilePathAttribute</c> source and verified against installed versions.
        /// </summary>
        private static string ResolveByHardcodedFallback()
        {
            string unityRoot = GetUnityPreferencesRoot();
            if (string.IsNullOrEmpty(unityRoot))
                return null;

            // Application.unityVersion reports the marketing version (e.g. "2022.3.50f1", "6000.0.10f1").
            // Unity's internal preferences folder version number:
            //   2022.x and earlier → "5"   (Editor-5.x)
            //   6000.x and later   → "6"   (Editor-6.x)
            int major;
            if (!int.TryParse(Application.unityVersion.Split('.')[0], out major))
                major = 5;  // safe default — covers all versions through 2022

            string editorVersion = major >= 6000 ? "6" : "5";
            string editorDir = $"Editor-{editorVersion}.x";

            var result = Path.Combine(unityRoot, editorDir, "Preferences");
            AgentCoreLog.Info($"[PreferencesFolderPathHelper] Resolved via hardcoded fallback: {result}");
            return result;
        }
    }
}
