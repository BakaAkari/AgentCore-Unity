using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AgentCore.Editor.UI.Context
{
    /// <summary>
    /// 采集当前打开的 Scene 的 Hierarchy 摘要。
    /// 大 Scene 场景采用分层采样策略：
    ///   &lt; 100 GO         → 完整 tree
    ///   100 - 1000 GO      → 前 3 层 + 每层最多 50 GO
    ///   1000 - 10000 GO    → 前 2 层 + 每层最多 20 GO
    ///   &gt; 10000 GO      → 拒绝注入，返回警告
    /// </summary>
    public static class SceneContextCollector
    {
        public static ContextIngestResult Collect()
        {
            var scenes = new List<Scene>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.isLoaded) scenes.Add(s);
            }

            if (scenes.Count == 0)
                return ContextIngestResult.OkWithWarning(
                    "Scene (empty)",
                    "(no loaded scenes)",
                    "No scenes are currently loaded.");

            // 先统计每个 Scene 的 GO 总数
            var perSceneCounts = new int[scenes.Count];
            int totalGoCount = 0;
            for (int i = 0; i < scenes.Count; i++)
            {
                perSceneCounts[i] = CountGameObjects(scenes[i]);
                totalGoCount += perSceneCounts[i];
            }

            var label = scenes.Count == 1
                ? $"Scene: {scenes[0].name}"
                : $"Scenes: {scenes[0].name} (+{scenes.Count - 1} more)";

            // 超大场景拒绝注入
            if (totalGoCount > ContextIngestLimits.SceneLargeThreshold)
            {
                return ContextIngestResult.OkWithWarning(
                    label,
                    $"(scene too large: {totalGoCount} GameObjects total)",
                    $"Scene has {totalGoCount} GameObjects (> {ContextIngestLimits.SceneLargeThreshold}). " +
                    "Select specific GameObjects in Hierarchy and press the shortcut again for precise context.",
                    truncated: true);
            }

            // 采样策略
            SamplingLevel level;
            if (totalGoCount < ContextIngestLimits.SceneFullDumpThreshold) level = SamplingLevel.Full;
            else if (totalGoCount < ContextIngestLimits.SceneModerateThreshold) level = SamplingLevel.Moderate;
            else level = SamplingLevel.Large;

            var sb = new StringBuilder(2048);
            sb.Append("Total scenes: ").Append(scenes.Count)
              .Append(" | Total GameObjects: ").Append(totalGoCount)
              .Append(" | Sampling: ").Append(level).Append('\n');

            for (int i = 0; i < scenes.Count; i++)
            {
                sb.Append("\n### Scene: ").Append(scenes[i].name)
                  .Append(" (").Append(perSceneCounts[i]).Append(" GameObjects)\n");

                var roots = scenes[i].GetRootGameObjects();
                var maxDepth = level == SamplingLevel.Full ? int.MaxValue
                             : level == SamplingLevel.Moderate ? 3
                             : 2;
                var childrenPerLevel = level == SamplingLevel.Full ? int.MaxValue
                                     : level == SamplingLevel.Moderate ? ContextIngestLimits.SceneModerateChildrenPerLevel
                                     : ContextIngestLimits.SceneLargeChildrenPerLevel;

                foreach (var root in roots)
                {
                    AppendTree(sb, root.transform, 0, maxDepth, childrenPerLevel);
                }
            }

            string warning = null;
            if (level != SamplingLevel.Full)
            {
                warning = $"Scene sampled at level '{level}' due to size ({totalGoCount} GameObjects). " +
                          "For precise inspection, select specific GameObjects and re-press the shortcut.";
            }

            return warning == null
                ? ContextIngestResult.Ok(label, sb.ToString())
                : ContextIngestResult.OkWithWarning(label, sb.ToString(), warning, truncated: true);
        }

        private enum SamplingLevel { Full, Moderate, Large }

        // ---------- Hierarchy 遍历 ----------

        private static void AppendTree(StringBuilder sb, Transform node, int depth, int maxDepth, int childrenLimit)
        {
            if (node == null) return;

            for (int i = 0; i < depth; i++) sb.Append("  ");
            sb.Append("- ").Append(node.name);

            // 附加根组件类型（除了 Transform）
            var extraComps = node.GetComponents<Component>();
            if (extraComps != null && extraComps.Length > 1)
            {
                sb.Append(" [");
                bool first = true;
                for (int i = 0; i < extraComps.Length; i++)
                {
                    if (extraComps[i] == null || extraComps[i] is Transform) continue;
                    if (!first) sb.Append(", ");
                    sb.Append(extraComps[i].GetType().Name);
                    first = false;
                }
                sb.Append(']');
            }
            sb.Append('\n');

            if (depth + 1 > maxDepth) return;

            int childCount = node.childCount;
            int limit = System.Math.Min(childCount, childrenLimit);
            for (int i = 0; i < limit; i++)
            {
                AppendTree(sb, node.GetChild(i), depth + 1, maxDepth, childrenLimit);
            }
            if (childCount > limit)
            {
                for (int i = 0; i < depth + 1; i++) sb.Append("  ");
                sb.Append("... (").Append(childCount - limit).Append(" more children)\n");
            }
        }

        // ---------- 统计 ----------

        private static int CountGameObjects(Scene scene)
        {
            int count = 0;
            var roots = scene.GetRootGameObjects();
            foreach (var root in roots)
            {
                count += CountRecursive(root.transform);
            }
            return count;
        }

        private static int CountRecursive(Transform t)
        {
            int c = 1;
            for (int i = 0; i < t.childCount; i++)
            {
                c += CountRecursive(t.GetChild(i));
            }
            return c;
        }
    }
}
