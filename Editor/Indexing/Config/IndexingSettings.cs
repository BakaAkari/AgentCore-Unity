using System;
using AgentCore.Editor.Config;
using AgentCore.Editor.Utils;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Components.Indexing.Config
{
    /// <summary>
    /// Persistent settings for the optional indexing component.
    /// </summary>
    [FilePath("AgentCore/IndexingSettings.asset", FilePathAttribute.Location.PreferencesFolder)]
    public sealed class IndexingSettings : ScriptableSingleton<IndexingSettings>
    {
        /// <summary>
        /// Background auto-indexing configuration.
        /// </summary>
        public IndexingAutoSettings AutoSettings = new IndexingAutoSettings();

        /// <summary>
        /// Gets a non-null background auto-indexing configuration instance.
        /// </summary>
        public IndexingAutoSettings EffectiveAutoSettings
        {
            get
            {
                if (AutoSettings == null)
                {
                    AutoSettings = new IndexingAutoSettings();
                    SafeSave(true);
                }

                return AutoSettings;
            }
        }

        /// <summary>
        /// Saves indexing settings to disk.
        /// </summary>
        public void SaveSettings()
        {
            SafeSave(true);
        }

        /// <summary>
        /// Safe wrapper around <see cref="ScriptableSingleton{T}.Save(bool)"/> that ensures the
        /// shared AgentCore preferences directory exists before writing. See
        /// <see cref="PreferencesFolderPathHelper"/> for details.
        /// </summary>
        internal void SafeSave(bool saveAsText)
        {
            if (!PreferencesFolderPathHelper.EnsureAgentCoreDirectory())
            {
                AgentCoreLog.Warning("[AgentCore] Skipping IndexingSettings.Save — preferences directory not available.");
                return;
            }
            try
            {
                Save(saveAsText);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AgentCore] IndexingSettings.Save failed: {ex.Message}");
            }
        }
    }
}
