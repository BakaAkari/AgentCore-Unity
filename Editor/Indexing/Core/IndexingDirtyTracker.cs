using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgentCore.Editor.Components.Indexing.Config;
using AgentCore.Editor.Utils;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Components.Indexing.Core
{
    /// <summary>
    /// Tracks changed and deleted source paths for background incremental indexing.
    /// </summary>
    [InitializeOnLoad]
    public static class IndexingDirtyTracker
    {
        private const string PersistRelativePath = "Library/agentcore-indexing-dirty.json";
        private static readonly object _gate = new object();
        private static readonly HashSet<string> _changedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _deletedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool _loaded;

        static IndexingDirtyTracker()
        {
            Load();
        }

        /// <summary>
        /// Raised when the dirty path set changes.
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
                lock (_gate)
                {
                    return _changedPaths.Count + _deletedPaths.Count;
                }
            }
        }

        /// <summary>
        /// Gets whether there are paths waiting to be indexed.
        /// </summary>
        public static bool HasDirtyPaths => Count > 0;

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
        /// Gets a snapshot of pending dirty paths without removing them.
        /// </summary>
        public static IndexingDirtySnapshot Snapshot(int maxChangedPaths = int.MaxValue)
        {
            EnsureLoaded();
            lock (_gate)
            {
                return new IndexingDirtySnapshot
                {
                    ChangedPaths = _changedPaths.Take(Math.Max(0, maxChangedPaths)).ToList(),
                    DeletedPaths = _deletedPaths.ToList()
                };
            }
        }

        /// <summary>
        /// Removes successfully processed paths from the pending dirty set.
        /// </summary>
        public static void MarkProcessed(IEnumerable<string> changedPaths, IEnumerable<string> deletedPaths)
        {
            EnsureLoaded();
            var changed = NormalizePaths(changedPaths);
            var deleted = NormalizePaths(deletedPaths);
            var changedAny = false;

            lock (_gate)
            {
                foreach (var path in changed)
                {
                    changedAny |= _changedPaths.Remove(path);
                }

                foreach (var path in deleted)
                {
                    changedAny |= _deletedPaths.Remove(path);
                }
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
            var changed = false;
            lock (_gate)
            {
                changed = _changedPaths.Count > 0 || _deletedPaths.Count > 0;
                _changedPaths.Clear();
                _deletedPaths.Clear();
            }

            if (changed)
            {
                Save();
                NotifyChanged();
            }
        }

        /// <summary>
        /// Forces the current dirty set to be written to disk.
        /// </summary>
        public static void Save()
        {
            EnsureLoaded();
            IndexingDirtySnapshot snapshot;
            lock (_gate)
            {
                snapshot = new IndexingDirtySnapshot
                {
                    ChangedPaths = _changedPaths.ToList(),
                    DeletedPaths = _deletedPaths.ToList()
                };
            }

            try
            {
                var path = GetPersistPath();
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(path, JsonHelper.Serialize(snapshot, pretty: true));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AgentCore] IndexingDirtyTracker.Save failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Reloads the dirty set from persisted state.
        /// </summary>
        public static void Load()
        {
            lock (_gate)
            {
                _changedPaths.Clear();
                _deletedPaths.Clear();
                _loaded = true;
            }

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

                lock (_gate)
                {
                    foreach (var changedPath in NormalizePaths(snapshot.ChangedPaths))
                    {
                        _changedPaths.Add(changedPath);
                        _deletedPaths.Remove(changedPath);
                    }

                    foreach (var deletedPath in NormalizePaths(snapshot.DeletedPaths))
                    {
                        _deletedPaths.Add(deletedPath);
                        _changedPaths.Remove(deletedPath);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AgentCore] IndexingDirtyTracker.Load failed, dirty set reset: {ex.Message}");
                lock (_gate)
                {
                    _changedPaths.Clear();
                    _deletedPaths.Clear();
                }
            }
        }

        private static void Add(IEnumerable<string> paths, bool deleted)
        {
            EnsureLoaded();
            var normalizedPaths = NormalizePaths(paths);
            var changed = false;
            var addedInThisBatch = 0;

            lock (_gate)
            {
                foreach (var path in normalizedPaths)
                {
                    if (deleted)
                    {
                        if (_deletedPaths.Add(path))
                        {
                            changed = true;
                            addedInThisBatch++;
                        }

                        if (_changedPaths.Remove(path))
                        {
                            changed = true;
                        }
                    }
                    else
                    {
                        if (File.Exists(path))
                        {
                            if (_changedPaths.Add(path))
                            {
                                changed = true;
                                addedInThisBatch++;
                            }
                        }

                        if (_deletedPaths.Remove(path))
                        {
                            changed = true;
                        }
                    }
                }
            }

            if (changed)
            {
                Save();
                NotifyChanged();

                // v1.4.0 — Burst detection: notify BackgroundIndexService to pause when a single
                // batch marks a large number of files dirty (branch switch, formatting sweep, etc.).
                TryNotifyBurst(addedInThisBatch);
            }
        }

        /// <summary>
        /// v1.4.0 — When a single Add() call adds more than the configured threshold, tell
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
                Debug.LogWarning($"[AgentCore] IndexingDirtyTracker burst notify failed: {ex.Message}");
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
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..")).Replace('\\', '/').TrimEnd('/');
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
        /// Total pending path count.
        /// </summary>
        public int Count => (ChangedPaths?.Count ?? 0) + (DeletedPaths?.Count ?? 0);
    }
}
