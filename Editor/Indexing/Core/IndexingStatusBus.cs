using System;

namespace AgentCore.Editor.Components.Indexing.Core
{
    /// <summary>
    /// Represents the current state of the background indexing service.
    /// </summary>
    public enum IndexingBackgroundState
    {
        Idle,
        Pending,
        Running,
        Failed,
        Disabled
    }

    /// <summary>
    /// Snapshot of the background indexing status exposed to UI and tools.
    /// </summary>
    public sealed class IndexingStatusSnapshot
    {
        /// <summary>
        /// Current background indexing state.
        /// </summary>
        public IndexingBackgroundState State;

        /// <summary>
        /// Number of accumulated dirty files waiting to be processed.
        /// </summary>
        public int DirtyFileCount;

        /// <summary>
        /// Number of files processed by the current task.
        /// </summary>
        public int ProcessedFiles;

        /// <summary>
        /// Total files scheduled for the current task.
        /// </summary>
        public int TotalFiles;

        /// <summary>
        /// Current file being indexed, or null when no file is active.
        /// </summary>
        public string CurrentFile;

        /// <summary>
        /// Last background indexing failure reason, or null when none is known.
        /// </summary>
        public string LastError;

        /// <summary>
        /// Last successful background indexing completion time.
        /// </summary>
        public DateTime? LastSuccessAt;

        /// <summary>
        /// Number of consecutive background indexing failures.
        /// </summary>
        public int ConsecutiveFailures;

        /// <summary>
        /// Creates a copy so subscribers cannot mutate shared status state.
        /// </summary>
        public IndexingStatusSnapshot Clone()
        {
            return new IndexingStatusSnapshot
            {
                State = State,
                DirtyFileCount = DirtyFileCount,
                ProcessedFiles = ProcessedFiles,
                TotalFiles = TotalFiles,
                CurrentFile = CurrentFile,
                LastError = LastError,
                LastSuccessAt = LastSuccessAt,
                ConsecutiveFailures = ConsecutiveFailures
            };
        }
    }

    /// <summary>
    /// Process-local event bus for background indexing status updates.
    /// </summary>
    public static class IndexingStatusBus
    {
        private static IndexingStatusSnapshot _current = new IndexingStatusSnapshot
        {
            State = IndexingBackgroundState.Idle
        };

        /// <summary>
        /// Raised whenever the background indexing status changes.
        /// </summary>
        public static event Action<IndexingStatusSnapshot> StatusChanged;

        /// <summary>
        /// Gets the latest background indexing status snapshot.
        /// </summary>
        public static IndexingStatusSnapshot Current => _current.Clone();

        /// <summary>
        /// Publishes a new background indexing status snapshot.
        /// </summary>
        internal static void Publish(IndexingStatusSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            _current = snapshot.Clone();
            StatusChanged?.Invoke(_current.Clone());
        }
    }
}
