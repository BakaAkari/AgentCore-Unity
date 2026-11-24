using System;
using System.Globalization;
using UnityEditor;
using UnityEngine;

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
        /// v1.4.0 — Scheduled next-run time when background service is paused (e.g. burst backoff).
        /// Null when no pause is active.
        /// </summary>
        public DateTime? NextRunAt;

        /// <summary>
        /// v1.4.0 — Human-readable reason for the current pause (e.g. "burst detected: 800 files")
        /// or null when the service is not paused.
        /// </summary>
        public string ReasonPaused;

        /// <summary>
        /// Whether two snapshots would look the same to subscribers. NextRunAt is compared to the
        /// second because it is recomputed from the editor clock on every tick.
        /// </summary>
        public bool IsEquivalentTo(IndexingStatusSnapshot other)
        {
            return other != null
                   && State == other.State
                   && DirtyFileCount == other.DirtyFileCount
                   && ProcessedFiles == other.ProcessedFiles
                   && TotalFiles == other.TotalFiles
                   && CurrentFile == other.CurrentFile
                   && LastError == other.LastError
                   && LastSuccessAt == other.LastSuccessAt
                   && ConsecutiveFailures == other.ConsecutiveFailures
                   && NextRunAt.HasValue == other.NextRunAt.HasValue
                   && (!NextRunAt.HasValue || Math.Abs((NextRunAt.Value - other.NextRunAt.Value).TotalSeconds) < 1)
                   && ReasonPaused == other.ReasonPaused;
        }

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
                ConsecutiveFailures = ConsecutiveFailures,
                NextRunAt = NextRunAt,
                ReasonPaused = ReasonPaused
            };
        }
    }

    /// <summary>
    /// Process-local event bus for background indexing status updates.
    /// Persists the latest status snapshot to <see cref="SessionState"/> across Domain Reloads
    /// and stores the durable last-success timestamp in project-scoped <see cref="EditorPrefs"/>.
    /// </summary>
    public static class IndexingStatusBus
    {
        public const string StatusSessionKey = "AgentCore.Indexing.StatusBus.Snapshot";
        private const string DurableLastSuccessPrefKeyPrefix = "AgentCore.Indexing.StatusBus.LastSuccessUtc.";

        [Serializable]
        private sealed class StatusSnapshotDto
        {
            public int SchemaVersion;
            public int StateValue;
            public int DirtyFileCount;
            public int ProcessedFiles;
            public int TotalFiles;
            public string CurrentFile;
            public string LastError;
            public long LastSuccessAtTicks;
            public int ConsecutiveFailures;
            public long NextRunAtTicks;
            public string ReasonPaused;
        }

        private static IndexingStatusSnapshot _current = new IndexingStatusSnapshot
        {
            State = IndexingBackgroundState.Idle
        };

        static IndexingStatusBus()
        {
            RestoreFromStorage();
        }

        /// <summary>
        /// Raised whenever the background indexing status changes.
        /// </summary>
        public static event Action<IndexingStatusSnapshot> StatusChanged;

        /// <summary>
        /// Gets the latest background indexing status snapshot.
        /// </summary>
        public static IndexingStatusSnapshot Current => _current.Clone();

        /// <summary>
        /// Publishes a new background indexing status snapshot. Snapshots equivalent to the current one
        /// are dropped, so callers may publish every editor tick without flooding subscribers.
        /// </summary>
        internal static void Publish(IndexingStatusSnapshot snapshot)
        {
            if (snapshot == null || snapshot.IsEquivalentTo(_current))
            {
                return;
            }

            _current = snapshot.Clone();
            PersistToStorage(_current);
            StatusChanged?.Invoke(_current.Clone());
        }

        /// <summary>
        /// Computes a deterministic project-scoped EditorPrefs key for durable last-success metadata.
        /// </summary>
        public static string GetDurableLastSuccessPrefKey(string projectPath = null)
        {
            string source;
            try
            {
                source = string.IsNullOrWhiteSpace(projectPath) ? Application.dataPath : projectPath;
            }
            catch
            {
                source = projectPath ?? "default";
            }

            return DurableLastSuccessPrefKeyPrefix + ComputeStableHash(source);
        }

        /// <summary>
        /// Serializes a status snapshot to JSON for SessionState persistence.
        /// </summary>
        public static string SerializeSnapshot(IndexingStatusSnapshot snapshot)
        {
            if (snapshot == null)
                return null;

            var dto = new StatusSnapshotDto
            {
                SchemaVersion = 1,
                StateValue = (int)snapshot.State,
                DirtyFileCount = Math.Max(0, snapshot.DirtyFileCount),
                ProcessedFiles = Math.Max(0, snapshot.ProcessedFiles),
                TotalFiles = Math.Max(0, snapshot.TotalFiles),
                CurrentFile = snapshot.CurrentFile,
                LastError = snapshot.LastError,
                LastSuccessAtTicks = snapshot.LastSuccessAt.HasValue ? snapshot.LastSuccessAt.Value.ToUniversalTime().Ticks : 0L,
                ConsecutiveFailures = Math.Max(0, snapshot.ConsecutiveFailures),
                NextRunAtTicks = snapshot.NextRunAt.HasValue ? snapshot.NextRunAt.Value.ToUniversalTime().Ticks : 0L,
                ReasonPaused = snapshot.ReasonPaused
            };

            return JsonUtility.ToJson(dto);
        }

        /// <summary>
        /// Deserializes a status snapshot from JSON. Returns null on empty/corrupted JSON.
        /// When <paramref name="sanitizeReloadState"/> is true, an interrupted <see cref="IndexingBackgroundState.Running"/>
        /// state is safely downgraded to <see cref="IndexingBackgroundState.Pending"/> or <see cref="IndexingBackgroundState.Idle"/>
        /// while preserving metrics, last success time, and last error.
        /// </summary>
        public static IndexingStatusSnapshot DeserializeSnapshot(string json, bool sanitizeReloadState = true)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                var dto = JsonUtility.FromJson<StatusSnapshotDto>(json);
                if (dto == null || dto.SchemaVersion <= 0)
                    return null;

                var state = Enum.IsDefined(typeof(IndexingBackgroundState), dto.StateValue)
                    ? (IndexingBackgroundState)dto.StateValue
                    : IndexingBackgroundState.Idle;

                var snapshot = new IndexingStatusSnapshot
                {
                    State = state,
                    DirtyFileCount = Math.Max(0, dto.DirtyFileCount),
                    ProcessedFiles = Math.Max(0, dto.ProcessedFiles),
                    TotalFiles = Math.Max(0, dto.TotalFiles),
                    CurrentFile = NullIfEmpty(dto.CurrentFile),
                    LastError = NullIfEmpty(dto.LastError),
                    LastSuccessAt = ParseUtcTicks(dto.LastSuccessAtTicks),
                    ConsecutiveFailures = Math.Max(0, dto.ConsecutiveFailures),
                    NextRunAt = ParseUtcTicks(dto.NextRunAtTicks),
                    ReasonPaused = NullIfEmpty(dto.ReasonPaused)
                };

                if (sanitizeReloadState)
                    SanitizeForReload(snapshot);

                return snapshot;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Saves a snapshot into SessionState; erases the key when null.
        /// </summary>
        public static void SaveSnapshotToSession(IndexingStatusSnapshot snapshot, string sessionKey = StatusSessionKey)
        {
            if (string.IsNullOrEmpty(sessionKey))
                return;

            try
            {
                var json = SerializeSnapshot(snapshot);
                if (string.IsNullOrEmpty(json))
                    SessionState.EraseString(sessionKey);
                else
                    SessionState.SetString(sessionKey, json);
            }
            catch
            {
                // Ignore SessionState errors if called outside main thread.
            }
        }

        /// <summary>
        /// Loads and deserializes a snapshot from SessionState.
        /// </summary>
        public static IndexingStatusSnapshot LoadSnapshotFromSession(string sessionKey = StatusSessionKey, bool sanitizeReloadState = true)
        {
            if (string.IsNullOrEmpty(sessionKey))
                return null;

            try
            {
                return DeserializeSnapshot(SessionState.GetString(sessionKey, string.Empty), sanitizeReloadState);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Persists the durable last-success UTC timestamp into project-scoped EditorPrefs.
        /// </summary>
        public static void SaveDurableLastSuccessUtc(DateTime? utcTime, string prefKey = null)
        {
            var key = string.IsNullOrEmpty(prefKey) ? GetDurableLastSuccessPrefKey() : prefKey;
            try
            {
                if (utcTime.HasValue)
                    EditorPrefs.SetString(key, utcTime.Value.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
                else
                    EditorPrefs.DeleteKey(key);
            }
            catch
            {
                // Ignore EditorPrefs errors outside main thread.
            }
        }

        /// <summary>
        /// Reads the durable last-success UTC timestamp from project-scoped EditorPrefs.
        /// </summary>
        public static DateTime? LoadDurableLastSuccessUtc(string prefKey = null)
        {
            var key = string.IsNullOrEmpty(prefKey) ? GetDurableLastSuccessPrefKey() : prefKey;
            try
            {
                var raw = EditorPrefs.GetString(key, string.Empty);
                if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
                    return ParseUtcTicks(ticks);
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Hydrates <see cref="Current"/> from SessionState and durable EditorPrefs (used on Domain Reload and in tests).
        /// </summary>
        public static void RestoreFromStorage(string sessionKey = StatusSessionKey, string durablePrefKey = null)
        {
            var restored = LoadSnapshotFromSession(sessionKey, sanitizeReloadState: true)
                           ?? new IndexingStatusSnapshot { State = IndexingBackgroundState.Idle };

            if (!restored.LastSuccessAt.HasValue)
            {
                restored.LastSuccessAt = LoadDurableLastSuccessUtc(durablePrefKey);
            }

            _current = restored;
        }

        /// <summary>
        /// Resets in-memory state to default Idle without erasing production storage unless keys are passed.
        /// </summary>
        public static void ResetInMemoryForTests()
        {
            _current = new IndexingStatusSnapshot { State = IndexingBackgroundState.Idle };
        }

        private static void PersistToStorage(IndexingStatusSnapshot snapshot)
        {
            if (snapshot == null)
                return;

            SaveSnapshotToSession(snapshot, StatusSessionKey);
            if (snapshot.LastSuccessAt.HasValue)
                SaveDurableLastSuccessUtc(snapshot.LastSuccessAt, GetDurableLastSuccessPrefKey());
        }

        private static void SanitizeForReload(IndexingStatusSnapshot snapshot)
        {
            if (snapshot == null)
                return;

            if (snapshot.State == IndexingBackgroundState.Running)
            {
                snapshot.State = snapshot.DirtyFileCount > 0
                    ? IndexingBackgroundState.Pending
                    : IndexingBackgroundState.Idle;
                snapshot.CurrentFile = null;
            }

            if (snapshot.NextRunAt.HasValue && snapshot.NextRunAt.Value <= DateTime.UtcNow)
            {
                snapshot.NextRunAt = null;
                snapshot.ReasonPaused = null;
            }
        }

        private static DateTime? ParseUtcTicks(long ticks)
        {
            if (ticks <= 0 || ticks > DateTime.MaxValue.Ticks)
                return null;
            return new DateTime(ticks, DateTimeKind.Utc);
        }

        private static string NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

        private static string ComputeStableHash(string text)
        {
            var normalized = (text ?? string.Empty).Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
            ulong hash = 14695981039346656037UL;
            for (int i = 0; i < normalized.Length; i++)
            {
                hash ^= normalized[i];
                hash *= 1099511628211UL;
            }
            return hash.ToString("x16", CultureInfo.InvariantCulture);
        }
    }
}
