using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AgentCore.Editor.Tools.Infrastructure;
using AgentCore.Editor.Utils;
using Newtonsoft.Json;
using UnityEditor;

namespace AgentCore.Editor.Tools.Safety
{
    /// <summary>
    /// 按用户轮次记录 Agent 写文件前的原始内容，用于"回退到这条消息之前"。
    /// <para>
    /// 每个用户轮次一份清单（Library/AgentCore/Checkpoints/&lt;session&gt;/&lt;turn&gt;/manifest.json），
    /// 只在工具执行期间捕获：同一轮内每个路径只保留第一次写入前的内容（blob + .meta），
    /// 原本不存在的路径记为 Created，回退时移回回收站。捕获来源有三处：
    /// 分发器在执行前按工具参数中的路径预捕获、<see cref="AtomicFile"/> 写入前回调、
    /// <see cref="CheckpointAssetWatcher"/> 补记经 AssetDatabase 产生却没捕获到原内容的变化（标为不可恢复）。
    /// </para>
    /// <para>
    /// 这是给用户的额外撤销手段，不拦截任何操作：捕获失败只记日志，工具照常执行。
    /// 项目本身仍以 VCS 为准，回退只覆盖 Agent 写过的文件。
    /// </para>
    /// </summary>
    [InitializeOnLoad]
    internal static partial class CheckpointStore
    {
        private const string LogPrefix = "[AgentCore.Checkpoint] ";
        private const string ActiveTurnKey = "AgentCore_Checkpoint_ActiveTurn";

        /// <summary>单个文件超过该大小不备份，只标为不可恢复。</summary>
        private const long MaxFileBytes = 100L * 1024 * 1024;

        /// <summary>目录目标递归备份的上限，超出则跳过（由资产监视补记）。</summary>
        private const int MaxDirectoryFiles = 200;

        private const int MaxRetainedTurns = 100;
        private static readonly TimeSpan MaxRetainedAge = TimeSpan.FromDays(30);

        /// <summary>工具结束后仍归属该工具的导入回调窗口（Refresh 触发的导入可能晚一两帧）。</summary>
        private static readonly TimeSpan PostToolGrace = TimeSpan.FromSeconds(5);

        /// <summary>不备份的目录前缀（Unity 生成物与 AgentCore 自身数据）。</summary>
        private static readonly string[] ExcludedPrefixes =
        {
            "Library/", "Temp/", "Logs/", "obj/", "UserSettings/", "ProjectSettings/AgentCore", ".git/", ".svn/", ".vs/", ".idea/"
        };

        private static readonly object Gate = new object();
        private static readonly int MainThreadId;

        /// <summary>启动时缓存（Application.dataPath 只能在主线程读，写入回调可能来自后台线程）。</summary>
        private static readonly string ProjectRoot;

        private static Manifest _manifest;
        private static ToolScope _activeTool;
        private static DateTime _lastToolEndUtc = DateTime.MinValue;
        private static bool _rewinding;

        /// <summary>当前轮次（"session|turn"），SessionState 的内存副本：后台线程的写入回调不能读 SessionState。</summary>
        private static string _activeTurn;

        public enum EntryKind
        {
            /// <summary>执行前已存在，Blob 中是原内容。</summary>
            Modified,
            /// <summary>执行前不存在，回退时移除。</summary>
            Created,
            /// <summary>资产从 MovedFrom 移动到 Path，回退时移回。</summary>
            Moved,
            /// <summary>发生了变化但没有拿到原内容（过大、经 API 修改未预捕获、已被删除）。</summary>
            Unrecoverable
        }

        public class Entry
        {
            public string Path;
            public EntryKind Kind;
            public string Blob;
            public string MetaBlob;
            public string MovedFrom;
            public string Reason;
            public string Tool;
            public DateTime CapturedUtc;
            /// <summary>工具执行后文件是否存在 / 大小 / 修改时间，用于回退时识别用户之后的改动。</summary>
            public bool AfterRecorded;
            public bool AfterExists;
            public long AfterSize;
            public long AfterTicks;
        }

        public class Manifest
        {
            public string SessionId;
            public string TurnId;
            public DateTime CreatedUtc;
            /// <summary>本轮第一个工具调用开始时的 Undo 组；-1 表示本轮没有主线程工具。</summary>
            public int FirstUndoGroup = -1;
            public List<Entry> Entries = new List<Entry>();

            [JsonIgnore] public string Directory;
        }

        /// <summary>会话内用户轮次的先后顺序（只有产生过写入的轮次才有清单）。</summary>
        private class TurnIndex
        {
            public List<TurnRecord> Turns = new List<TurnRecord>();
        }

        private class TurnRecord
        {
            public string TurnId;
            public DateTime StartedUtc;
        }

        static CheckpointStore()
        {
            MainThreadId = Thread.CurrentThread.ManagedThreadId;
            ProjectRoot = PathUtils.ProjectRoot;
            _activeTurn = SessionState.GetString(ActiveTurnKey, string.Empty);
            AtomicFile.BeforeWrite += path => CaptureBeforeWrite(path);
        }

        public static string RootDirectory => Path.Combine(ProjectRoot, "Library", "AgentCore", "Checkpoints");

        private static bool IsMainThread => Thread.CurrentThread.ManagedThreadId == MainThreadId;

        #region 轮次与工具调用

        /// <summary>开始一个用户轮次：之后的工具写入都记到这一轮。域重载后仍有效。</summary>
        public static void BeginTurn(string sessionId, string turnId)
        {
            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(turnId))
                return;

            lock (Gate)
            {
                _manifest = null;
                _activeTurn = sessionId + "|" + turnId;
                SessionState.SetString(ActiveTurnKey, _activeTurn);
            }

            try
            {
                var index = LoadIndex(sessionId);
                if (!index.Turns.Exists(t => t.TurnId == turnId))
                {
                    index.Turns.Add(new TurnRecord { TurnId = turnId, StartedUtc = DateTime.UtcNow });
                    SaveIndex(sessionId, index);
                }
                Prune();
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"{LogPrefix}Failed to record turn start: {ex.Message}");
            }
        }

        /// <summary>
        /// 开始一次工具调用：预捕获参数里的写入目标，主线程上开一个命名的 Undo 组。
        /// 没有活动轮次时返回 null（<c>using (null)</c> 合法）。
        /// </summary>
        public static IDisposable BeginToolCall(string toolName, IEnumerable<string> writeTargets)
        {
            if (!TryGetActiveTurn(out _, out _))
                return null;

            var scope = new ToolScope(toolName);
            lock (Gate)
                _activeTool = scope;

            if (IsMainThread)
            {
                try
                {
                    Undo.IncrementCurrentGroup();
                    Undo.SetCurrentGroupName("AgentCore: " + toolName);
                    var group = Undo.GetCurrentGroup();
                    lock (Gate)
                    {
                        var manifest = GetOrCreateManifest(persist: false);
                        if (manifest != null && manifest.FirstUndoGroup < 0)
                        {
                            manifest.FirstUndoGroup = group;
                            SaveManifest(manifest);
                        }
                    }
                }
                catch (Exception ex)
                {
                    AgentCoreLog.Warning($"{LogPrefix}Failed to open undo group for '{toolName}': {ex.Message}");
                }
            }

            if (writeTargets != null)
            {
                foreach (var target in writeTargets)
                    Capture(target, fromParameters: true);
            }

            return scope;
        }

        private sealed class ToolScope : IDisposable
        {
            public readonly string ToolName;
            public readonly DateTime StartedUtc = DateTime.UtcNow;
            private bool _disposed;

            public ToolScope(string toolName)
            {
                ToolName = toolName;
            }

            public void Dispose()
            {
                if (_disposed)
                    return;
                _disposed = true;

                lock (Gate)
                {
                    if (_activeTool == this)
                        _activeTool = null;
                    _lastToolEndUtc = DateTime.UtcNow;
                    RefreshAfterStamps();
                }
            }
        }

        #endregion

        #region 捕获

        /// <summary><see cref="AtomicFile"/> 写入前回调：工具执行期间才捕获。</summary>
        private static void CaptureBeforeWrite(string fullPath)
        {
            lock (Gate)
            {
                if (_activeTool == null)
                    return;
            }
            Capture(fullPath, fromParameters: false);
        }

        /// <summary>捕获一个路径（文件或小目录）写入前的状态；同一轮内已捕获过的路径忽略。</summary>
        public static void Capture(string path, bool fromParameters = false)
        {
            try
            {
                var rel = ToProjectRelative(path);
                if (rel == null)
                    return;

                var full = Path.Combine(ProjectRoot, rel);
                if (Directory.Exists(full))
                {
                    CaptureDirectory(full);
                    return;
                }

                // 参数里的路径不一定是文件（GameObject 路径、资源名等）：不存在的路径只在常见资产根下记为 Created
                if (fromParameters && !File.Exists(full) && !IsAssetRoot(rel))
                    return;

                lock (Gate)
                    CaptureFileLocked(rel, full);
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"{LogPrefix}Capture failed for '{path}': {ex.Message}");
            }
        }

        private static void CaptureDirectory(string fullDir)
        {
            var files = new List<string>();
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(fullDir, "*", SearchOption.AllDirectories))
            {
                if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                    continue;
                total += new FileInfo(file).Length;
                files.Add(file);
                if (files.Count > MaxDirectoryFiles || total > MaxFileBytes)
                {
                    AgentCoreLog.Info($"{LogPrefix}Directory too large to back up, skipped: {fullDir}");
                    return;
                }
            }

            lock (Gate)
            {
                foreach (var file in files)
                {
                    var rel = ToProjectRelative(file);
                    if (rel != null)
                        CaptureFileLocked(rel, file);
                }
            }
        }

        private static void CaptureFileLocked(string rel, string full)
        {
            var manifest = GetOrCreateManifest(persist: true);
            if (manifest == null || manifest.Entries.Exists(e => SamePath(e.Path, rel)))
                return;

            var entry = new Entry
            {
                Path = rel,
                Tool = _activeTool?.ToolName,
                CapturedUtc = DateTime.UtcNow
            };

            if (!File.Exists(full))
            {
                entry.Kind = EntryKind.Created;
            }
            else if (new FileInfo(full).Length > MaxFileBytes)
            {
                entry.Kind = EntryKind.Unrecoverable;
                entry.Reason = "file larger than 100 MB was not backed up";
            }
            else
            {
                var blobDir = Path.Combine(manifest.Directory, "blobs");
                Directory.CreateDirectory(blobDir);
                var id = Guid.NewGuid().ToString("N");
                entry.Kind = EntryKind.Modified;
                entry.Blob = id;
                File.Copy(full, Path.Combine(blobDir, id), overwrite: true);
                if (File.Exists(full + ".meta"))
                {
                    entry.MetaBlob = id + ".meta";
                    File.Copy(full + ".meta", Path.Combine(blobDir, entry.MetaBlob), overwrite: true);
                }
            }

            manifest.Entries.Add(entry);
            SaveManifest(manifest);
        }

        /// <summary>
        /// 资产监视补记：工具执行期间（或刚结束）AssetDatabase 报告的变化里没有预捕获的路径。
        /// </summary>
        internal static void RecordAssetChanges(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            lock (Gate)
            {
                if (_rewinding || (_activeTool == null && DateTime.UtcNow - _lastToolEndUtc > PostToolGrace))
                    return;

                var manifest = GetOrCreateManifest(persist: true);
                if (manifest == null)
                    return;

                var toolName = _activeTool?.ToolName;
                var toolStart = _activeTool?.StartedUtc ?? _lastToolEndUtc - PostToolGrace;
                var changed = false;

                for (int i = 0; i < moved.Length; i++)
                {
                    if (manifest.Entries.Exists(e => SamePath(e.Path, moved[i])))
                        continue;
                    manifest.Entries.Add(new Entry
                    {
                        Path = moved[i], Kind = EntryKind.Moved, MovedFrom = movedFrom[i],
                        Tool = toolName, CapturedUtc = DateTime.UtcNow
                    });
                    changed = true;
                }

                foreach (var path in imported)
                {
                    if (ToProjectRelative(path) == null || manifest.Entries.Exists(e => SamePath(e.Path, path) || SamePath(e.MovedFrom, path)))
                        continue;
                    if (Array.IndexOf(moved, path) >= 0)
                        continue;

                    var full = Path.Combine(ProjectRoot, path);
                    if (Directory.Exists(full))
                        continue;
                    var created = File.Exists(full) && File.GetCreationTimeUtc(full) >= toolStart.AddSeconds(-2);
                    manifest.Entries.Add(new Entry
                    {
                        Path = path,
                        Kind = created ? EntryKind.Created : EntryKind.Unrecoverable,
                        Reason = created ? null : "modified through the AssetDatabase without a prior backup",
                        Tool = toolName,
                        CapturedUtc = toolStart
                    });
                    changed = true;
                }

                foreach (var path in deleted)
                {
                    if (ToProjectRelative(path) == null || manifest.Entries.Exists(e => SamePath(e.Path, path) || SamePath(e.MovedFrom, path)))
                        continue;
                    manifest.Entries.Add(new Entry
                    {
                        Path = path, Kind = EntryKind.Unrecoverable,
                        Reason = "deleted without a prior backup (check the OS recycle bin or VCS)",
                        Tool = toolName, CapturedUtc = DateTime.UtcNow
                    });
                    changed = true;
                }

                if (changed)
                {
                    if (_activeTool == null)
                        RefreshAfterStamps();
                    else
                        SaveManifest(manifest);
                }
            }
        }

        /// <summary>记录本轮所有条目当前的文件状态，作为"Agent 写完后"的基准。</summary>
        private static void RefreshAfterStamps()
        {
            var manifest = _manifest;
            if (manifest == null || manifest.Entries.Count == 0)
                return;

            foreach (var entry in manifest.Entries)
            {
                var full = Path.Combine(ProjectRoot, entry.Path);
                Stamp(full, out entry.AfterExists, out entry.AfterSize, out entry.AfterTicks);
                entry.AfterRecorded = true;
            }
            SaveManifest(manifest);
        }

        private static void Stamp(string full, out bool exists, out long size, out long ticks)
        {
            exists = File.Exists(full);
            size = exists ? new FileInfo(full).Length : 0;
            ticks = exists ? File.GetLastWriteTimeUtc(full).Ticks : 0;
        }

        #endregion

        #region 存储

        private static bool TryGetActiveTurn(out string sessionId, out string turnId)
        {
            sessionId = turnId = null;
            var raw = _activeTurn ?? string.Empty;
            var sep = raw.IndexOf('|');
            if (sep <= 0 || sep == raw.Length - 1)
                return false;
            sessionId = raw.Substring(0, sep);
            turnId = raw.Substring(sep + 1);
            return true;
        }

        /// <summary>取当前轮的清单；<paramref name="persist"/> 为 true 时在首次需要时建目录。</summary>
        private static Manifest GetOrCreateManifest(bool persist)
        {
            if (!TryGetActiveTurn(out var sessionId, out var turnId))
                return null;

            if (_manifest != null && _manifest.SessionId == sessionId && _manifest.TurnId == turnId)
                return _manifest;

            var dir = TurnDirectory(sessionId, turnId);
            _manifest = LoadManifest(dir) ?? new Manifest
            {
                SessionId = sessionId,
                TurnId = turnId,
                CreatedUtc = DateTime.UtcNow,
                Directory = dir
            };
            if (persist && !File.Exists(Path.Combine(dir, "manifest.json")))
                SaveManifest(_manifest);
            return _manifest;
        }

        private static string SessionDirectory(string sessionId) => Path.Combine(RootDirectory, SanitizeName(sessionId));

        private static string TurnDirectory(string sessionId, string turnId) =>
            Path.Combine(SessionDirectory(sessionId), SanitizeName(turnId));

        private static Manifest LoadManifest(string dir)
        {
            var file = Path.Combine(dir, "manifest.json");
            if (!File.Exists(file))
                return null;
            try
            {
                var manifest = JsonConvert.DeserializeObject<Manifest>(File.ReadAllText(file));
                if (manifest != null)
                    manifest.Directory = dir;
                return manifest;
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"{LogPrefix}Unreadable checkpoint manifest {file}: {ex.Message}");
                return null;
            }
        }

        private static void SaveManifest(Manifest manifest)
        {
            Directory.CreateDirectory(manifest.Directory);
            WriteJson(Path.Combine(manifest.Directory, "manifest.json"), manifest);
        }

        private static TurnIndex LoadIndex(string sessionId)
        {
            var file = Path.Combine(SessionDirectory(sessionId), "turns.json");
            try
            {
                if (File.Exists(file))
                    return JsonConvert.DeserializeObject<TurnIndex>(File.ReadAllText(file)) ?? new TurnIndex();
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"{LogPrefix}Unreadable checkpoint index {file}: {ex.Message}");
            }
            return new TurnIndex();
        }

        private static void SaveIndex(string sessionId, TurnIndex index)
        {
            var dir = SessionDirectory(sessionId);
            Directory.CreateDirectory(dir);
            WriteJson(Path.Combine(dir, "turns.json"), index);
        }

        /// <summary>
        /// 直接写临时文件再替换，不经 <see cref="AtomicFile"/>（避免触发自身的写入前回调）。
        /// 清单在 Library 下，不受版本管理，也不会被导入。
        /// </summary>
        private static void WriteJson(string file, object value)
        {
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(value, Formatting.Indented));
            if (File.Exists(file))
                File.Replace(tmp, file, null, ignoreMetadataErrors: true);
            else
                File.Move(tmp, file);
        }

        /// <summary>只保留最近 <see cref="MaxRetainedTurns"/> 个轮次、且不超过 30 天的检查点。</summary>
        private static void Prune()
        {
            if (!Directory.Exists(RootDirectory))
                return;

            var turns = new List<DirectoryInfo>();
            foreach (var sessionDir in new DirectoryInfo(RootDirectory).GetDirectories())
                turns.AddRange(sessionDir.GetDirectories());

            var cutoff = DateTime.UtcNow - MaxRetainedAge;
            var ordered = turns.OrderByDescending(d => d.LastWriteTimeUtc).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                if (i < MaxRetainedTurns && ordered[i].LastWriteTimeUtc >= cutoff)
                    continue;
                try
                {
                    ordered[i].Delete(recursive: true);
                }
                catch (Exception ex)
                {
                    AgentCoreLog.Warning($"{LogPrefix}Failed to prune {ordered[i].FullName}: {ex.Message}");
                }
            }
        }

        #endregion

        #region 路径

        /// <summary>项目内、非排除目录下的路径转为项目相对路径（正斜杠）；否则返回 null。</summary>
        internal static string ToProjectRelative(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            string full;
            try
            {
                full = PathUtils.ToUnityPath(Path.GetFullPath(Path.Combine(ProjectRoot, path)));
            }
            catch (Exception)
            {
                return null;
            }

            var root = ProjectRoot + "/";
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return null;

            var rel = full.Substring(root.Length).TrimEnd('/');
            if (rel.Length == 0)
                return null;
            foreach (var prefix in ExcludedPrefixes)
            {
                if (rel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                    (rel + "/").Equals(prefix, StringComparison.OrdinalIgnoreCase))
                    return null;
            }
            return rel;
        }

        private static bool IsAssetRoot(string rel) =>
            rel.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
            rel.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase) ||
            rel.StartsWith("ProjectSettings/", StringComparison.OrdinalIgnoreCase);

        private static bool SamePath(string a, string b) =>
            a != null && b != null && string.Equals(a.Replace('\\', '/'), b.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

        private static string SanitizeName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        #endregion
    }
}
