using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AgentCore.Editor.Components.Indexing.Config;
using AgentCore.Editor.Utils;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Components.Indexing.Core
{
    /// <summary>
    /// Tracks changed and deleted source paths for background incremental indexing.
    /// <para>
    /// Paths live in a <see cref="VersionedDirtySet"/>: every event bumps the path's version, and
    /// <see cref="MarkProcessed(IndexingDirtySnapshot)"/> only removes a path whose version still
    /// matches the snapshot, so edits that land while a batch is being indexed are kept for the
    /// next pass. Events are debounced (~500 ms) before persisting / notifying listeners.
    /// </para>
    /// </summary>
    [InitializeOnLoad]
    public static class IndexingDirtyTracker
    {
        private const string PersistRelativePath = "Library/agentcore-indexing-dirty.json";

        /// <summary>Quiet period after the last event before the dirty set is saved and listeners notified.</summary>
        public const int DebounceMilliseconds = 500;

        private static readonly object _gate = new object();
        private static readonly object _saveGate = new object();
        private static readonly VersionedDirtySet _set = new VersionedDirtySet();
        private static readonly Stopwatch _sinceLastEvent = new Stopwatch();
        private static bool _flushPending;
        private static int _pendingAddedCount;
        private static bool _loaded;
        private static string _projectRoot;

        static IndexingDirtyTracker()
        {
            Load();
            EditorApplication.update += FlushIfQuiet;
            AssemblyReloadEvents.beforeAssemblyReload += FlushNow;
            EditorApplication.quitting += FlushNow;
        }

        /// <summary>
        /// Raised when the dirty path set changes (after the debounce window).
        /// </summary>
        public static event Action DirtyChanged;

        /// <summary>
        /// Gets the number of pending changed and deleted paths.
        /// </summary>
        public static int Count
        {
            get
            {
                EnsureLoaded();
                return _set.Count;
            }
        }

        /// <summary>
        /// Gets whether there are paths waiting to be indexed.
        /// </summary>
        public static bool HasDirtyPaths => Count > 0;

        /// <summary>
        /// Gets whether events arrived within the debounce window and have not been flushed yet.
        /// Background indexing waits until the burst of events settles.
        /// </summary>
        public static bool IsSettling
        {
            get
            {
                lock (_gate)
                {
                    return _flushPending;
                }
            }
        }

        /// <summary>
        /// Adds changed paths to the pending dirty set.
        /// </summary>
        public static void AddChanged(IEnumerable<string> paths)
        {
            Add(paths, deleted: false);
        }

        /// <summary>
        /// Adds deleted paths to the pending dirty set.
        /// </summary>
        public static void AddDeleted(IEnumerable<string> paths)
        {
            Add(paths, deleted: true);
        }

        /// <summary>
        /// Gets a snapshot of pending dirty paths without removing them. Oldest events come first.
        /// Each path is classified as changed or deleted by whether it exists on disk right now;
        /// the captured versions are used by <see cref="MarkProcessed(IndexingDirtySnapshot)"/>.
        /// </summary>
        public static IndexingDirtySnapshot Snapshot(int maxChangedPaths = int.MaxValue)
        {
            EnsureLoaded();
            var limit = Math.Max(0, maxChangedPaths);
            var snapshot = new IndexingDirtySnapshot
            {
                Versions = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            };

            foreach (var entry in _set.Snapshot())
            {
                if (File.Exists(entry.Key))
                {
                    if (snapshot.ChangedPaths.Count >= limit)
                    {
                        continue;
                    }

                    snapshot.ChangedPaths.Add(entry.Key);
                }
                else
                {
                    snapshot.DeletedPaths.Add(entry.Key);
                }

                snapshot.Versions[entry.Key] = entry.Value;
            }

            return snapshot;
        }

        /// <summary>
        /// Removes the paths of a processed snapshot, but only those whose version has not changed
        /// since the snapshot was taken (compare-and-remove). Paths edited mid-index stay dirty.
        /// </summary>
        public static void MarkProcessed(IndexingDirtySnapshot snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            EnsureLoaded();
            if (snapshot.Versions == null)
            {
                MarkProcessed(snapshot.ChangedPaths, snapshot.DeletedPaths);
                return;
            }

            var removed = _set.RemoveProcessed(snapshot.Versions);
            if (removed > 0)
            {
                Save();
                NotifyChanged();
            }
        }

        /// <summary>
        /// Removes paths from the pending dirty set unconditionally (no version check).
        /// Prefer <see cref="MarkProcessed(IndexingDirtySnapshot)"/>, which does not lose concurrent edits.
        /// </summary>
        public static void MarkProcessed(IEnumerable<string> changedPaths, IEnumerable<string> deletedPaths)
        {
            EnsureLoaded();
            var changedAny = false;
            foreach (var path in NormalizePaths(changedPaths).Concat(NormalizePaths(deletedPaths)))
            {
                changedAny |= _set.RemoveUnconditionally(path);
            }

            if (changedAny)
            {
                Save();
                NotifyChanged();
            }
        }

        /// <summary>
        /// Clears all pending dirty paths.
        /// </summary>
        public static void Clear()
        {
            EnsureLoaded();
            var changed = _set.Count > 0;
            _set.Clear();

            if (changed)
            {
                Save();
                NotifyChanged();
            }
        }

        /// <summary>
        /// Forces the current dirty set to be written to disk.
        /// The persisted format (ChangedPaths / DeletedPaths) is unchanged; versions are per-session.
        /// </summary>
        public static void Save()
        {
            EnsureLoaded();
            var snapshot = new IndexingDirtySnapshot();
            foreach (var path in _set.Paths())
            {
                if (File.Exists(path))
                {
                    snapshot.ChangedPaths.Add(path);
                }
                else
                {
                    snapshot.DeletedPaths.Add(path);
                }
            }

            try
            {
                var path = GetPersistPath();
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Save can be reached from the main thread and from tool threads; serialize the file write.
                lock (_saveGate)
                {
                    AtomicFile.WriteAllText(path, JsonHelper.Serialize(snapshot, pretty: true));
                }
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore] IndexingDirtyTracker.Save failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Reloads the dirty set from persisted state.
        /// </summary>
        public static void Load()
        {
            _set.Clear();
            _loaded = true;

            try
            {
                var path = GetPersistPath();
                if (!File.Exists(path))
                {
                    return;
                }

                var json = File.ReadAllText(path);
                var snapshot = JsonHelper.Deserialize<IndexingDirtySnapshot>(json);
                if (snapshot == null)
                {
                    return;
                }

                foreach (var dirtyPath in NormalizePaths(snapshot.ChangedPaths).Concat(NormalizePaths(snapshot.DeletedPaths)))
                {
                    _set.Mark(dirtyPath);
                }
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore] IndexingDirtyTracker.Load failed, dirty set reset: {ex.Message}");
                _set.Clear();
            }
        }

        private static void Add(IEnumerable<string> paths, bool deleted)
        {
            EnsureLoaded();
            var normalizedPaths = NormalizePaths(paths);
            var marked = 0;
            var added = 0;

            foreach (var path in normalizedPaths)
            {
                // A "changed" event for a file that no longer exists is noise unless the path is
                // already tracked (then the new version makes the next pass treat it as deleted).
                if (!deleted && !File.Exists(path) && !_set.Contains(path))
                {
                    continue;
                }

                _set.Mark(path, out var isNew);
                marked++;
                if (isNew)
                {
                    added++;
                }
            }

            if (marked == 0)
            {
                return;
            }

            // Debounce: persist / notify / burst-detect once the events settle.
            lock (_gate)
            {
                _flushPending = true;
                _pendingAddedCount += added;
                _sinceLastEvent.Restart();
            }
        }

        private static void FlushIfQuiet()
        {
            lock (_gate)
            {
                if (!_flushPending || _sinceLastEvent.ElapsedMilliseconds < DebounceMilliseconds)
                {
                    return;
                }
            }

            FlushNow();
        }

        private static void FlushNow()
        {
            int addedCount;
            lock (_gate)
            {
                if (!_flushPending)
                {
                    return;
                }

                _flushPending = false;
                addedCount = _pendingAddedCount;
                _pendingAddedCount = 0;
                _sinceLastEvent.Reset();
            }

            Save();
            NotifyChanged();

            // v1.4.0 — Burst detection: notify BackgroundIndexService to pause when the coalesced
            // events mark a large number of files dirty (branch switch, formatting sweep, etc.).
            TryNotifyBurst(addedCount);
        }

        /// <summary>
        /// v1.4.0 — When one debounced batch adds more than the configured threshold, tell
        /// <see cref="BackgroundIndexService"/> to enter burst backoff so the Editor can settle.
        /// Silent no-op when settings are unavailable or threshold is 0.
        /// </summary>
        private static void TryNotifyBurst(int addedInThisBatch)
        {
            if (addedInThisBatch <= 0)
            {
                return;
            }

            try
            {
                var settings = IndexingSettings.instance?.EffectiveAutoSettings;
                if (settings == null || settings.BurstThreshold <= 0)
                {
                    return;
                }

                if (addedInThisBatch >= settings.BurstThreshold)
                {
                    BackgroundIndexService.NotifyBurstDetected(addedInThisBatch, settings.BurstBackoffSeconds);
                }
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore] IndexingDirtyTracker burst notify failed: {ex.Message}");
            }
        }

        private static void EnsureLoaded()
        {
            if (_loaded)
            {
                return;
            }

            Load();
        }

        private static List<string> NormalizePaths(IEnumerable<string> paths)
        {
            if (paths == null)
            {
                return new List<string>();
            }

            var result = new List<string>();
            foreach (var path in paths)
            {
                var normalized = NormalizePath(path);
                if (!string.IsNullOrEmpty(normalized))
                {
                    result.Add(normalized);
                }
            }

            return result;
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var trimmed = path.Trim().Replace('\\', '/');
            if (trimmed.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || string.Equals(trimmed, "Assets", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = Path.Combine(GetProjectRoot(), trimmed).Replace('\\', '/');
            }
            else if (!Path.IsPathRooted(trimmed))
            {
                trimmed = Path.GetFullPath(Path.Combine(GetProjectRoot(), trimmed)).Replace('\\', '/');
            }
            else
            {
                trimmed = Path.GetFullPath(trimmed).Replace('\\', '/');
            }

            return trimmed;
        }

        private static string GetPersistPath()
        {
            return Path.Combine(GetProjectRoot(), PersistRelativePath).Replace('\\', '/');
        }

        private static string GetProjectRoot()
        {
            // Cached on first use (the [InitializeOnLoad] constructor runs on the main thread), so
            // AddChanged from tool threads does not touch Application.dataPath off the main thread.
            return _projectRoot ??= Path.GetFullPath(Path.Combine(Application.dataPath, "..")).Replace('\\', '/').TrimEnd('/');
        }

        private static void NotifyChanged()
        {
            DirtyChanged?.Invoke();
        }
    }

    /// <summary>
    /// Serializable snapshot of pending background indexing paths.
    /// </summary>
    [Serializable]
    public sealed class IndexingDirtySnapshot
    {
        /// <summary>
        /// Absolute paths for changed files.
        /// </summary>
        public List<string> ChangedPaths = new List<string>();

        /// <summary>
        /// Absolute paths for deleted files.
        /// </summary>
        public List<string> DeletedPaths = new List<string>();

        /// <summary>
        /// Version of every path in this snapshot at the time it was taken (not persisted).
        /// Used for compare-and-remove in <see cref="IndexingDirtyTracker.MarkProcessed(IndexingDirtySnapshot)"/>.
        /// </summary>
        [JsonIgnore]
        [NonSerialized]
        public Dictionary<string, long> Versions;

        /// <summary>
        /// Total pending path count.
        /// </summary>
        public int Count => (ChangedPaths?.Count ?? 0) + (DeletedPaths?.Count ?? 0);
    }
}
