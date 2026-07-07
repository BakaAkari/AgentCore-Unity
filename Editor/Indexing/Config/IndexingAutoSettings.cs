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
        /// Larger values reduce Editor stutter after bulk changes (e.g. package import, branch switch).
        /// </summary>
        public int QuietDelayMs = 15000;

        /// <summary>
        /// Maximum number of changed files processed in a single background batch.
        /// Smaller batches keep individual indexing passes short and Editor responsive.
        /// </summary>
        public int MaxBatchFiles = 50;

        /// <summary>
        /// Number of files processed before yielding cooperatively.
        /// Lower values give Unity more opportunities to process UI/input events.
        /// </summary>
        public int YieldEveryNFiles = 1;

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

        /// <summary>
        /// v1.4.0 — Threshold for burst detection. When a single Add() call marks more than this
        /// many files dirty (e.g. branch switch, code formatting sweep, generator run), the
        /// background service pauses for <see cref="BurstBackoffSeconds"/> to avoid Editor stutter.
        /// Set to 0 to disable burst detection.
        /// </summary>
        public int BurstThreshold = 500;

        /// <summary>
        /// v1.4.0 — Pause duration in seconds after a burst is detected. Also applies as a floor
        /// for the next auto-run so the Editor gets time to settle.
        /// </summary>
        public int BurstBackoffSeconds = 60;
    }
}
