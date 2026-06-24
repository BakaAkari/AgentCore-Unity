using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace AgentCore.Editor.Components.Indexing.Core
{
    /// <summary>
    /// Bridges Unity asset import notifications into the indexing dirty tracker.
    /// </summary>
    public sealed class IndexingAssetWatcher : AssetPostprocessor
    {
        private static readonly string[] SourceExtensions =
        {
            ".cs",
            ".asmdef",
            ".asmref",
            ".json",
            ".uxml",
            ".uss",
            ".shader",
            ".compute",
            ".cginc",
            ".hlsl",
            ".glsl"
        };

        /// <summary>
        /// Receives Unity asset import events and records changed/deleted source paths for background indexing.
        /// </summary>
        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            var changed = CollectIndexablePaths(importedAssets)
                .Concat(CollectIndexablePaths(movedAssets))
                .ToList();

            var deleted = CollectIndexablePaths(deletedAssets)
                .Concat(CollectIndexablePaths(movedFromAssetPaths))
                .ToList();

            if (changed.Count > 0)
            {
                IndexingDirtyTracker.AddChanged(changed);
            }

            if (deleted.Count > 0)
            {
                IndexingDirtyTracker.AddDeleted(deleted);
            }
        }

        private static IEnumerable<string> CollectIndexablePaths(IEnumerable<string> paths)
        {
            if (paths == null)
            {
                yield break;
            }

            foreach (var path in paths)
            {
                if (IsIndexablePath(path))
                {
                    yield return path;
                }
            }
        }

        private static bool IsIndexablePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                && !path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return SourceExtensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
        }
    }
}
