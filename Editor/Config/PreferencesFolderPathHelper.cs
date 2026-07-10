using System;
using System.IO;
using System.Reflection;
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
    /// operation. When the target file's parent directory (e.g. <c>%APPDATA%/Unity/Editor-5.x/Preferences/AgentCore/</c>)
    /// does not exist, the Move fails with "系统找不到指定的路径 / The system cannot find the path specified",
    /// which leaves the Editor stuck and the user forced to Force Quit. This has been observed on
    /// fresh installs where no plugin has ever written into the shared Preferences folder.
    /// </para>
    /// <para>
    /// This helper resolves the preferences folder using (in order of preference):
    /// <list type="number">
    ///   <item><c>UnityEditorInternal.InternalEditorUtility.unityPreferencesFolder</c> via reflection (internal API)</item>
    ///   <item>Fallback: <c>%APPDATA%/Unity/Editor-{unityMajorVersion}.x/Preferences</c> on Windows</item>
    ///   <item>Fallback: <c>~/Library/Preferences/Unity/Editor-{unityMajorVersion}.x/Preferences</c> on macOS</item>
    ///   <item>Fallback: <c>~/.config/unity3d/Preferences</c> on Linux</item>
    /// </list>
    /// Failure to create the directory is logged as a warning but not thrown; callers should
    /// still wrap the subsequent <c>Save</c> in try/catch so a corrupt preferences root cannot
    /// prevent the Editor from running.
    /// </para>
    /// </remarks>
    public static class PreferencesFolderPathHelper
    {
        /// <summary>
        /// The AgentCore subdirectory used by every AgentCore <c>ScriptableSingleton</c>.
        /// Must stay in sync with the <c>[FilePath("AgentCore/...", PreferencesFolder)]</c>
        /// attribute values on the singleton classes.
        /// </summary>
        public const string AgentCoreSubdirectory = "AgentCore";

        private static string _cachedPreferencesFolder;
        private static bool _cachedDirEnsured;

        /// <summary>
        /// Ensures that <c>{PreferencesFolder}/AgentCore/</c> exists on disk.
        /// Safe to call repeatedly; result is cached after first successful creation.
        /// </summary>
        /// <returns><c>true</c> when the directory exists (either already or after creation); <c>false</c> when it could not be created.</returns>
        public static bool EnsureAgentCoreDirectory()
        {
            if (_cachedDirEnsured)
            {
                return true;
            }

            try
            {
                var prefRoot = GetPreferencesFolder();
                if (string.IsNullOrEmpty(prefRoot))
                {
                    return false;
                }

                var target = Path.Combine(prefRoot, AgentCoreSubdirectory);
                if (!Directory.Exists(target))
                {
                    Directory.CreateDirectory(target);
                }

                _cachedDirEnsured = true;
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AgentCore] Failed to ensure preferences folder for AgentCore singletons: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Resolves Unity's Editor preferences folder path (the parent of the AgentCore subdirectory).
        /// </summary>
        private static string GetPreferencesFolder()
        {
            if (!string.IsNullOrEmpty(_cachedPreferencesFolder))
            {
                return _cachedPreferencesFolder;
            }

            // Preferred: reflection into UnityEditorInternal.InternalEditorUtility.unityPreferencesFolder.
            // This mirrors the internal path Unity uses to compose ScriptableSingleton files with
            // PreferencesFolder location, guaranteeing we ensure the exact directory Unity will Move into.
            try
            {
                var prop = typeof(InternalEditorUtility).GetProperty(
                    "unityPreferencesFolder",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (prop != null)
                {
                    var value = prop.GetValue(null) as string;
                    if (!string.IsNullOrEmpty(value))
                    {
                        _cachedPreferencesFolder = value;
                        return _cachedPreferencesFolder;
                    }
                }
            }
            catch
            {
                // fall through to platform fallback
            }

            _cachedPreferencesFolder = BuildFallbackPreferencesFolder();
            return _cachedPreferencesFolder;
        }

        private static string BuildFallbackPreferencesFolder()
        {
            var majorVersion = ExtractMajorVersion(Application.unityVersion);
            var editorSegment = $"Editor-{majorVersion}.x";

#if UNITY_EDITOR_WIN
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(appData)) return string.Empty;
            return Path.Combine(appData, "Unity", editorSegment, "Preferences");
#elif UNITY_EDITOR_OSX
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home)) return string.Empty;
            return Path.Combine(home, "Library", "Preferences", "Unity", editorSegment, "Preferences");
#else
            // Linux
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home)) return string.Empty;
            return Path.Combine(home, ".config", "unity3d", "Preferences");
#endif
        }

        /// <summary>
        /// Extracts the major version component from a Unity version string (e.g. "2022.3.15f1" → "2022",
        /// "6000.0.10f1" → "6000"). Falls back to "unknown" when parsing fails.
        /// </summary>
        private static string ExtractMajorVersion(string unityVersion)
        {
            if (string.IsNullOrEmpty(unityVersion)) return "unknown";
            var dotIndex = unityVersion.IndexOf('.');
            return dotIndex > 0 ? unityVersion.Substring(0, dotIndex) : unityVersion;
        }
    }
}
