using System;

namespace AgentCore.Editor.Components.Indexing.Config
{
    /// <summary>
    /// Configures automatic background indexing behavior for the indexing component.
    /// </summary>
    [Serializable]
    public class IndexingAutoSettings
    {
        /// <summary>
        /// Whether background auto-indexing is enabled.
        /// </summary>
        public bool AutoIndexEnabled = true;

        /// <summary>
        /// Quiet delay in milliseconds before a dirty set is processed.
        /// </summary>
        public int QuietDelayMs = 2000;

        /// <summary>
        /// Maximum number of changed files processed in a single background batch.
        /// </summary>
        public int MaxBatchFiles = 200;

        /// <summary>
        /// Number of files processed before yielding cooperatively.
        /// </summary>
        public int YieldEveryNFiles = 5;

        /// <summary>
        /// Maximum source file size in kilobytes before indexing skips the file.
        /// </summary>
        public int MaxFileSizeKB = 1024;

        /// <summary>
        /// Whether background indexing should pause while Unity is compiling.
        /// </summary>
        public bool RespectIsCompiling = true;

        /// <summary>
        /// Whether background indexing should pause while Unity is in Play Mode.
        /// </summary>
        public bool RespectIsPlaying = true;

        /// <summary>
        /// Maximum consecutive background failures before auto-indexing is disabled.
        /// </summary>
        public int MaxConsecutiveFailures = 3;

        /// <summary>
        /// Whether background indexing should emit verbose diagnostic logs.
        /// </summary>
        public bool VerboseLogging = false;
    }
}
