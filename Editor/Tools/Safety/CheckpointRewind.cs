using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgentCore.Editor.Tools.Infrastructure;
using AgentCore.Editor.Utils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace AgentCore.Editor.Tools.Safety
{
    internal static partial class CheckpointStore
    {
        /// <summary>回退到某个用户轮次之前需要做的事。</summary>
        public class RewindPlan
        {
            public string SessionId;
            public string TurnId;
            /// <summary>该轮第一个工具调用的 Undo 组；-1 表示没有可撤销的编辑器内改动。</summary>
            public int FirstUndoGroup = -1;
            public readonly List<RewindItem> Items = new List<RewindItem>();
            /// <summary>按时间倒序的移动记录（回退时依次移回）。</summary>
            public readonly List<Entry> Moves = new List<Entry>();
            public readonly List<Entry> Unrecoverable = new List<Entry>();

            public bool IsEmpty => Items.Count == 0 && Moves.Count == 0 && Unrecoverable.Count == 0 && FirstUndoGroup < 0;
            public int ChangedSinceAgentCount => Items.Count(i => i.ChangedSinceAgent);
        }

        public class RewindItem
        {
            public string Path;
            /// <summary>该路径在回退起点之后第一次被捕获时的状态（Modified = 有原内容，Created = 原本不存在）。</summary>
            public Entry First;
            public string BlobDirectory;
            /// <summary>Agent 最后一次写完之后，文件又被改过（多半是用户手动修改）。</summary>
            public bool ChangedSinceAgent;
        }

        public class RewindReport
        {
            public readonly List<string> Restored = new List<string>();
            public readonly List<string> Removed = new List<string>();
            public readonly List<string> MovedBack = new List<string>();
            public readonly List<string> Skipped = new List<string>();
            public readonly List<string> Unrecoverable = new List<string>();
            public readonly List<string> Failed = new List<string>();
            public bool UndoReverted;

            public override string ToString()
            {
                var parts = new List<string>();
                if (Restored.Count > 0) parts.Add($"restored {Restored.Count}");
                if (Removed.Count > 0) parts.Add($"removed {Removed.Count} new");
                if (MovedBack.Count > 0) parts.Add($"moved back {MovedBack.Count}");
                if (Skipped.Count > 0) parts.Add($"skipped {Skipped.Count}");
                if (Unrecoverable.Count > 0) parts.Add($"unrecoverable {Unrecoverable.Count}");
                if (Failed.Count > 0) parts.Add($"failed {Failed.Count}");
                if (UndoReverted) parts.Add("editor undo reverted");
                return parts.Count == 0 ? "nothing to restore" : string.Join(", ", parts);
            }
        }

        /// <summary>该会话里是否记录过这一轮（之后）的任何改动。</summary>
        public static bool HasCheckpoint(string sessionId, string turnId)
        {
            try
            {
                return !PlanRewind(sessionId, turnId).IsEmpty;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 汇总从 <paramref name="turnId"/> 起（含）所有轮次的记录：每个路径取最早的原内容、最晚的"写后"状态。
        /// </summary>
        public static RewindPlan PlanRewind(string sessionId, string turnId)
        {
            var plan = new RewindPlan { SessionId = sessionId, TurnId = turnId };
            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(turnId))
                return plan;

            var index = LoadIndex(sessionId);
            var start = index.Turns.FindIndex(t => t.TurnId == turnId);
            if (start < 0)
                return plan;

            lock (Gate)
            {
                var items = new Dictionary<string, RewindItem>(StringComparer.OrdinalIgnoreCase);
                var latest = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
                var movedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (int t = start; t < index.Turns.Count; t++)
                {
                    var manifest = LoadManifest(TurnDirectory(sessionId, index.Turns[t].TurnId));
                    if (manifest == null)
                        continue;
                    if (plan.FirstUndoGroup < 0 && manifest.FirstUndoGroup >= 0)
                        plan.FirstUndoGroup = manifest.FirstUndoGroup;

                    foreach (var entry in manifest.Entries)
                    {
                        switch (entry.Kind)
                        {
                            case EntryKind.Moved:
                                plan.Moves.Insert(0, entry);
                                movedPaths.Add(entry.Path);
                                continue;
                            case EntryKind.Unrecoverable:
                                if (!items.ContainsKey(entry.Path) && !plan.Unrecoverable.Exists(u => SamePath(u.Path, entry.Path)))
                                    plan.Unrecoverable.Add(entry);
                                continue;
                        }

                        // 被移动过的资产按移动记录移回，不再按新位置恢复内容，避免在新位置留下副本
                        if (movedPaths.Contains(entry.Path))
                            continue;
                        if (!items.ContainsKey(entry.Path))
                            items[entry.Path] = new RewindItem { Path = entry.Path, First = entry, BlobDirectory = Path.Combine(manifest.Directory, "blobs") };
                        latest[entry.Path] = entry;
                    }
                }

                foreach (var item in items.Values)
                {
                    plan.Unrecoverable.RemoveAll(u => SamePath(u.Path, item.Path));
                    var last = latest[item.Path];
                    if (last.AfterRecorded)
                    {
                        Stamp(Path.Combine(ProjectRoot, item.Path), out var exists, out var size, out var ticks);
                        item.ChangedSinceAgent = exists != last.AfterExists || size != last.AfterSize || ticks != last.AfterTicks;
                    }
                    plan.Items.Add(item);
                }
            }

            plan.Items.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
            return plan;
        }

        /// <summary>
        /// 执行回退：撤销编辑器内改动（可选）→ 移回移动过的资产 → 恢复原内容 / 移除新建文件 → 刷新并重载受影响的已打开场景。
        /// </summary>
        /// <param name="overwriteChangedSinceAgent">Agent 写完后又被改过的文件是否也恢复（false 时跳过并报告）。</param>
        /// <param name="revertEditorUndo">是否把 Undo 历史回退到该轮第一个工具调用之前（场景内未保存的改动）。</param>
        public static RewindReport ApplyRewind(RewindPlan plan, bool overwriteChangedSinceAgent, bool revertEditorUndo)
        {
            var report = new RewindReport();
            if (plan == null)
                return report;

            lock (Gate)
                _rewinding = true;
            try
            {
                ApplyRewindCore(plan, overwriteChangedSinceAgent, revertEditorUndo, report);
            }
            finally
            {
                lock (Gate)
                {
                    _rewinding = false;
                    _lastToolEndUtc = DateTime.MinValue;
                }
            }

            DiscardFrom(plan.SessionId, plan.TurnId);
            AgentCoreLog.Info($"{LogPrefix}Rewind to turn {plan.TurnId}: {report}");
            return report;
        }

        private static void ApplyRewindCore(RewindPlan plan, bool overwriteChangedSinceAgent, bool revertEditorUndo, RewindReport report)
        {

            if (revertEditorUndo && plan.FirstUndoGroup >= 0 && Undo.GetCurrentGroup() >= plan.FirstUndoGroup)
            {
                try
                {
                    Undo.RevertAllDownToGroup(plan.FirstUndoGroup);
                    report.UndoReverted = true;
                }
                catch (Exception ex)
                {
                    report.Failed.Add($"editor undo: {ex.Message}");
                }
            }

            var root = ProjectRoot;
            var restoredScenes = new List<string>();

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var move in plan.Moves)
                {
                    try
                    {
                        if (!File.Exists(Path.Combine(root, move.Path)) && !Directory.Exists(Path.Combine(root, move.Path)))
                        {
                            report.Skipped.Add($"{move.Path} (no longer exists, not moved back)");
                            continue;
                        }
                        if (File.Exists(Path.Combine(root, move.MovedFrom)))
                        {
                            report.Skipped.Add($"{move.Path} (original location {move.MovedFrom} is occupied)");
                            continue;
                        }
                        var error = AssetDatabase.MoveAsset(move.Path, move.MovedFrom);
                        if (string.IsNullOrEmpty(error))
                            report.MovedBack.Add($"{move.Path} -> {move.MovedFrom}");
                        else
                            report.Failed.Add($"{move.Path}: {error}");
                    }
                    catch (Exception ex)
                    {
                        report.Failed.Add($"{move.Path}: {ex.Message}");
                    }
                }

                foreach (var item in plan.Items)
                {
                    if (item.ChangedSinceAgent && !overwriteChangedSinceAgent)
                    {
                        report.Skipped.Add($"{item.Path} (changed after the agent edited it)");
                        continue;
                    }

                    var full = Path.Combine(root, item.Path);
                    try
                    {
                        if (item.First.Kind == EntryKind.Modified)
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(full));
                            File.Copy(Path.Combine(item.BlobDirectory, item.First.Blob), full, overwrite: true);
                            // 原内容带着旧的修改时间，显式更新，确保 Unity 识别为变化并重新导入
                            File.SetLastWriteTimeUtc(full, DateTime.UtcNow);
                            if (!string.IsNullOrEmpty(item.First.MetaBlob))
                            {
                                File.Copy(Path.Combine(item.BlobDirectory, item.First.MetaBlob), full + ".meta", overwrite: true);
                                File.SetLastWriteTimeUtc(full + ".meta", DateTime.UtcNow);
                            }
                            report.Restored.Add(item.Path);
                            if (item.Path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                                restoredScenes.Add(item.Path);
                        }
                        else if (item.First.Kind == EntryKind.Created)
                        {
                            if (!File.Exists(full))
                                continue;
                            // 只移除确实是在捕获之后才出现的文件，防止误删同名的既有文件
                            if (File.GetCreationTimeUtc(full) < item.First.CapturedUtc.AddSeconds(-2))
                            {
                                report.Skipped.Add($"{item.Path} (existed before the agent created it)");
                                continue;
                            }
                            RemoveFile(item.Path, full);
                            report.Removed.Add(item.Path);
                        }
                    }
                    catch (Exception ex)
                    {
                        report.Failed.Add($"{item.Path}: {ex.Message}");
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            foreach (var entry in plan.Unrecoverable)
                report.Unrecoverable.Add($"{entry.Path} ({entry.Reason})");

            AssetDatabase.Refresh();
            ReloadRestoredScenes(restoredScenes, report);
        }

        /// <summary>
        /// 回退后删除该轮及之后的检查点：文件已回到该轮之前的状态，旧记录的"写后"基准已失效。
        /// </summary>
        private static void DiscardFrom(string sessionId, string turnId)
        {
            lock (Gate)
            {
                try
                {
                    var index = LoadIndex(sessionId);
                    var start = index.Turns.FindIndex(t => t.TurnId == turnId);
                    if (start < 0)
                        return;
                    foreach (var turn in index.Turns.GetRange(start, index.Turns.Count - start))
                    {
                        var dir = TurnDirectory(sessionId, turn.TurnId);
                        if (Directory.Exists(dir))
                            Directory.Delete(dir, recursive: true);
                    }
                    index.Turns.RemoveRange(start, index.Turns.Count - start);
                    SaveIndex(sessionId, index);
                    _manifest = null;
                    if (TryGetActiveTurn(out var activeSession, out _) && activeSession == sessionId)
                    {
                        _activeTurn = null;
                        SessionState.EraseString(ActiveTurnKey);
                    }
                }
                catch (Exception ex)
                {
                    AgentCoreLog.Warning($"{LogPrefix}Failed to discard checkpoints after rewind: {ex.Message}");
                }
            }
        }

        /// <summary>新建的文件移到系统回收站；不是资产或回收站不可用时直接删除（项目由版本管理兜底）。</summary>
        private static void RemoveFile(string rel, string full)
        {
            if (IsAssetRoot(rel) && !rel.StartsWith("ProjectSettings/", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(rel)) && AssetDatabase.MoveAssetToTrash(rel))
                return;

            File.Delete(full);
            if (File.Exists(full + ".meta"))
                File.Delete(full + ".meta");
        }

        /// <summary>
        /// 磁盘上的场景文件已恢复，但已打开的场景还是内存里的旧状态：重新加载整个场景布局。
        /// 其他已打开且有未保存改动的场景先询问是否保存，用户取消则只报告不重载。
        /// </summary>
        private static void ReloadRestoredScenes(List<string> restoredScenes, RewindReport report)
        {
            if (restoredScenes.Count == 0 || EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            var loaded = new List<Scene>();
            var others = new List<Scene>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded || string.IsNullOrEmpty(scene.path))
                    continue;
                if (restoredScenes.Exists(p => SamePath(p, scene.path)))
                    loaded.Add(scene);
                else if (scene.isDirty)
                    others.Add(scene);
            }
            if (loaded.Count == 0)
                return;

            if (others.Count > 0 && !EditorSceneManager.SaveModifiedScenesIfUserWantsTo(others.ToArray()))
            {
                report.Skipped.Add("open scenes were not reloaded; reopen them to see the restored files");
                return;
            }

            try
            {
                var setup = EditorSceneManager.GetSceneManagerSetup();
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                EditorSceneManager.RestoreSceneManagerSetup(setup);
            }
            catch (Exception ex)
            {
                report.Failed.Add($"scene reload: {ex.Message}");
            }
        }
    }
}
