using System.Collections.Generic;

namespace AgentCore.Editor.Config.Settings
{
    /// <summary>
    /// Stores transient IMGUI state for the AgentCore settings hub.
    /// </summary>
    public sealed class AgentCoreSettingsState
    {
        /// <summary>
        /// Creates a settings state instance.
        /// </summary>
        public AgentCoreSettingsState()
        {
            Foldouts = new Dictionary<string, bool>();
            StatusMessages = new Dictionary<string, string>();
            StatusLevels = new Dictionary<string, SettingsStatusLevel>();
            RunningTasks = new HashSet<string>();
            StringValues = new Dictionary<string, string>();
        }

        /// <summary>
        /// Gets or sets the selected settings section identifier.
        /// </summary>
        public string SelectedSectionId { get; set; } = "general";

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
            Foldouts[key] = value;
        }

        /// <summary>
        /// Sets a status message and level.
        /// </summary>
        /// <param name="key">The scoped status key.</param>
        /// <param name="message">The status message.</param>
        /// <param name="level">The status level.</param>
        public void SetStatus(string key, string message, SettingsStatusLevel level)
        {
            StatusMessages[key] = message ?? string.Empty;
            StatusLevels[key] = level;
        }

        /// <summary>
        /// Clears a status entry.
        /// </summary>
        /// <param name="key">The scoped status key.</param>
        public void ClearStatus(string key)
        {
            StatusMessages.Remove(key);
            StatusLevels.Remove(key);
        }

        /// <summary>
        /// Gets a status message.
        /// </summary>
        /// <param name="key">The scoped status key.</param>
        /// <returns>The status message, or an empty string when absent.</returns>
        public string GetStatusMessage(string key)
        {
            return StatusMessages.TryGetValue(key, out var message) ? message : string.Empty;
        }

        /// <summary>
        /// Gets a status level.
        /// </summary>
        /// <param name="key">The scoped status key.</param>
        /// <returns>The status level, or None when absent.</returns>
        public SettingsStatusLevel GetStatusLevel(string key)
        {
            return StatusLevels.TryGetValue(key, out var level) ? level : SettingsStatusLevel.None;
        }
    }
}
