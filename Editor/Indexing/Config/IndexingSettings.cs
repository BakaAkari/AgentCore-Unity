using UnityEditor;

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
                    Save(true);
                }

                return AutoSettings;
            }
        }

        /// <summary>
        /// Saves indexing settings to disk.
        /// </summary>
        public void SaveSettings()
        {
            Save(true);
        }
    }
}
