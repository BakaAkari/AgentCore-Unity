using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace AgentCore.Editor.Config.Settings
{
    /// <summary>
    /// Foldout default policy for AgentCore settings pages. Keeps default expand/collapse
    /// decisions consistent across all pages so users see the same behavior everywhere.
    /// </summary>
    public static class FoldoutDefaults
    {
        /// <summary>Primary configuration fields for an already-enabled service (endpoint / API key).
        /// Default: expanded — user just enabled the service and needs to fill it in.</summary>
        public const bool ServiceConfig = true;

        /// <summary>Advanced / secondary options (auto-memory, burst tuning, dedicated compression LLM).
        /// Default: collapsed — reduces initial visual density.</summary>
        public const bool Advanced = false;

        /// <summary>Read-only reference tables (scope roots overview, path-safety policy).
        /// Default: collapsed — only relevant during troubleshooting.</summary>
        public const bool ReadOnlyInfo = false;

        /// <summary>Tool category listings inside Tools &amp; Extensions.
        /// Default: collapsed — categories are numerous.</summary>
        public const bool ToolCategory = false;
    }

    /// <summary>
    /// Stores IMGUI state for the AgentCore settings hub, with optional persistence across Domain Reload
    /// (foldouts and transient statuses in <see cref="SessionState"/>) and Editor restarts
    /// (selected section in <see cref="EditorPrefs"/>).
    /// </summary>
    public sealed class AgentCoreSettingsState
    {
        internal const string DefaultSelectedSectionPrefKey = "AgentCore.Settings.SelectedSection";
        internal const string DefaultFoldoutsSessionKey = "AgentCore.Settings.Foldouts";
        internal const string DefaultStatusesSessionKey = "AgentCore.Settings.Statuses";

        private readonly bool _persistenceEnabled;
        private readonly string _selectedSectionPrefKey;
        private readonly string _foldoutsSessionKey;
        private readonly string _statusesSessionKey;
        private string _selectedSectionId = "dashboard";

        /// <summary>
        /// Creates an in-memory settings state instance (no EditorPrefs/SessionState side effects).
        /// </summary>
        public AgentCoreSettingsState()
            : this(false, DefaultSelectedSectionPrefKey, DefaultFoldoutsSessionKey, DefaultStatusesSessionKey)
        {
        }

        private AgentCoreSettingsState(
            bool persistenceEnabled,
            string selectedSectionPrefKey,
            string foldoutsSessionKey,
            string statusesSessionKey)
        {
            _persistenceEnabled = persistenceEnabled;
            _selectedSectionPrefKey = string.IsNullOrEmpty(selectedSectionPrefKey)
                ? DefaultSelectedSectionPrefKey
                : selectedSectionPrefKey;
            _foldoutsSessionKey = string.IsNullOrEmpty(foldoutsSessionKey)
                ? DefaultFoldoutsSessionKey
                : foldoutsSessionKey;
            _statusesSessionKey = string.IsNullOrEmpty(statusesSessionKey)
                ? DefaultStatusesSessionKey
                : statusesSessionKey;

            Foldouts = new Dictionary<string, bool>();
            StatusMessages = new Dictionary<string, string>();
            StatusLevels = new Dictionary<string, SettingsStatusLevel>();
            RunningTasks = new HashSet<string>();
            StringValues = new Dictionary<string, string>();

            if (_persistenceEnabled)
            {
                LoadFromPersistence();
            }
        }

        /// <summary>
        /// Creates a settings state instance backed by <see cref="EditorPrefs"/> (selected section)
        /// and <see cref="SessionState"/> (foldout states and transient status banners).
        /// </summary>
        public static AgentCoreSettingsState CreatePersistent()
        {
            return new AgentCoreSettingsState(
                true,
                DefaultSelectedSectionPrefKey,
                DefaultFoldoutsSessionKey,
                DefaultStatusesSessionKey);
        }

        /// <summary>
        /// Test-isolated persistent state factory using custom storage keys.
        /// </summary>
        internal static AgentCoreSettingsState CreatePersistent(
            string selectedSectionPrefKey,
            string foldoutsSessionKey,
            string statusesSessionKey)
        {
            return new AgentCoreSettingsState(
                true,
                selectedSectionPrefKey,
                foldoutsSessionKey,
                statusesSessionKey);
        }

        /// <summary>
        /// Gets or sets the selected settings section identifier.
        /// </summary>
        public string SelectedSectionId
        {
            get => _selectedSectionId;
            set
            {
                var normalized = string.IsNullOrWhiteSpace(value) ? "dashboard" : value;
                if (string.Equals(_selectedSectionId, normalized, StringComparison.Ordinal))
                    return;

                _selectedSectionId = normalized;
                if (_persistenceEnabled)
                {
                    EditorPrefs.SetString(_selectedSectionPrefKey, _selectedSectionId);
                }
            }
        }

        /// <summary>
        /// Gets section-scoped foldout states.
        /// </summary>
        public Dictionary<string, bool> Foldouts { get; }

        /// <summary>
        /// Gets section-scoped status messages.
        /// </summary>
        public Dictionary<string, string> StatusMessages { get; }

        /// <summary>
        /// Gets section-scoped status levels.
        /// </summary>
        public Dictionary<string, SettingsStatusLevel> StatusLevels { get; }

        /// <summary>
        /// Gets section-scoped running task keys.
        /// </summary>
        public HashSet<string> RunningTasks { get; }

        /// <summary>
        /// Gets section-scoped transient string values.
        /// </summary>
        public Dictionary<string, string> StringValues { get; }

        /// <summary>
        /// Gets a foldout value by key, initializing it to the supplied default when absent.
        /// </summary>
        /// <param name="key">The scoped foldout key.</param>
        /// <param name="defaultValue">The default value.</param>
        /// <returns>The current foldout value.</returns>
        public bool GetFoldout(string key, bool defaultValue = false)
        {
            if (string.IsNullOrEmpty(key))
                return defaultValue;

            if (!Foldouts.TryGetValue(key, out var value))
            {
                value = defaultValue;
                Foldouts[key] = value;
            }

            return value;
        }

        /// <summary>
        /// Sets a foldout value by key.
        /// </summary>
        /// <param name="key">The scoped foldout key.</param>
        /// <param name="value">The new value.</param>
        public void SetFoldout(string key, bool value)
        {
            if (string.IsNullOrEmpty(key))
                return;

            var changed = !Foldouts.TryGetValue(key, out var existing) || existing != value;
            Foldouts[key] = value;
            if (changed && _persistenceEnabled)
            {
                SaveFoldoutsToSession();
            }
        }

        /// <summary>
        /// Sets a status message and level.
        /// </summary>
        /// <param name="key">The scoped status key.</param>
        /// <param name="message">The status message.</param>
        /// <param name="level">The status level.</param>
        public void SetStatus(string key, string message, SettingsStatusLevel level)
        {
            if (string.IsNullOrEmpty(key))
                return;

            StatusMessages[key] = message ?? string.Empty;
            StatusLevels[key] = level;
            if (_persistenceEnabled)
            {
                SaveStatusesToSession();
            }
        }

        /// <summary>
        /// Clears a status entry.
        /// </summary>
        /// <param name="key">The scoped status key.</param>
        public void ClearStatus(string key)
        {
            if (string.IsNullOrEmpty(key))
                return;

            var removed = StatusMessages.Remove(key) | StatusLevels.Remove(key);
            if (removed && _persistenceEnabled)
            {
                SaveStatusesToSession();
            }
        }

        /// <summary>
        /// Gets a status message.
        /// </summary>
        /// <param name="key">The scoped status key.</param>
        /// <returns>The status message, or an empty string when absent.</returns>
        public string GetStatusMessage(string key)
        {
            return !string.IsNullOrEmpty(key) && StatusMessages.TryGetValue(key, out var message)
                ? message
                : string.Empty;
        }

        /// <summary>
        /// Gets a status level.
        /// </summary>
        /// <param name="key">The scoped status key.</param>
        /// <returns>The status level, or None when absent.</returns>
        public SettingsStatusLevel GetStatusLevel(string key)
        {
            return !string.IsNullOrEmpty(key) && StatusLevels.TryGetValue(key, out var level)
                ? level
                : SettingsStatusLevel.None;
        }

        private void LoadFromPersistence()
        {
            var savedSection = EditorPrefs.GetString(_selectedSectionPrefKey, "dashboard");
            _selectedSectionId = string.IsNullOrWhiteSpace(savedSection) ? "dashboard" : savedSection;

            LoadFoldoutsFromSession();
            LoadStatusesFromSession();
        }

        private void LoadFoldoutsFromSession()
        {
            var raw = SessionState.GetString(_foldoutsSessionKey, string.Empty);
            if (string.IsNullOrWhiteSpace(raw))
                return;

            try
            {
                var obj = JObject.Parse(raw);
                foreach (var prop in obj.Properties())
                {
                    if (prop.Value != null && prop.Value.Type == JTokenType.Boolean)
                    {
                        Foldouts[prop.Name] = prop.Value.Value<bool>();
                    }
                }
            }
            catch
            {
                // Corrupt SessionState JSON falls back cleanly to defaults.
            }
        }

        private void SaveFoldoutsToSession()
        {
            try
            {
                var obj = new JObject();
                foreach (var kvp in Foldouts)
                {
                    if (!string.IsNullOrEmpty(kvp.Key))
                    {
                        obj[kvp.Key] = kvp.Value;
                    }
                }
                SessionState.SetString(_foldoutsSessionKey, obj.ToString(Formatting.None));
            }
            catch
            {
                // Non-fatal persistence failure.
            }
        }

        private void LoadStatusesFromSession()
        {
            var raw = SessionState.GetString(_statusesSessionKey, string.Empty);
            if (string.IsNullOrWhiteSpace(raw))
                return;

            try
            {
                var obj = JObject.Parse(raw);
                foreach (var prop in obj.Properties())
                {
                    if (!(prop.Value is JObject entry))
                        continue;

                    var msg = entry["m"]?.Value<string>() ?? string.Empty;
                    var levelVal = entry["l"]?.Value<int>() ?? (int)SettingsStatusLevel.None;
                    if (!Enum.IsDefined(typeof(SettingsStatusLevel), levelVal))
                        continue;

                    var level = (SettingsStatusLevel)levelVal;
                    // In-flight Loading banners cannot resume across Domain Reload; skip them on restore.
                    if (level == SettingsStatusLevel.Loading)
                        continue;

                    StatusMessages[prop.Name] = msg;
                    StatusLevels[prop.Name] = level;
                }
            }
            catch
            {
                // Corrupt SessionState JSON falls back cleanly to empty statuses.
            }
        }

        private void SaveStatusesToSession()
        {
            try
            {
                var obj = new JObject();
                foreach (var kvp in StatusMessages)
                {
                    if (string.IsNullOrEmpty(kvp.Key))
                        continue;

                    var level = StatusLevels.TryGetValue(kvp.Key, out var l)
                        ? l
                        : SettingsStatusLevel.None;
                    if (level == SettingsStatusLevel.Loading)
                        continue;

                    obj[kvp.Key] = new JObject
                    {
                        ["m"] = kvp.Value ?? string.Empty,
                        ["l"] = (int)level,
                    };
                }
                SessionState.SetString(_statusesSessionKey, obj.ToString(Formatting.None));
            }
            catch
            {
                // Non-fatal persistence failure.
            }
        }
    }
}
