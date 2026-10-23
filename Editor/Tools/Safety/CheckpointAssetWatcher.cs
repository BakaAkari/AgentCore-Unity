using UnityEditor;

namespace AgentCore.Editor.Tools.Safety
{
    /// <summary>
    /// 把工具执行期间经 AssetDatabase 产生、但没被预捕获的变化补记到当前检查点：
    /// 新建的资产（回退时移除）、移动（回退时移回）、无原内容的修改 / 删除（标为不可恢复，回退时如实报告）。
    /// </summary>
    internal sealed class CheckpointAssetWatcher : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
        {
            if (importedAssets.Length == 0 && deletedAssets.Length == 0 && movedAssets.Length == 0)
                return;

            try
            {
                CheckpointStore.RecordAssetChanges(importedAssets, deletedAssets, movedAssets, movedFromAssetPaths);
            }
            catch (System.Exception ex)
            {
                AgentCore.Editor.Utils.AgentCoreLog.Warning($"[AgentCore.Checkpoint] Failed to record asset changes: {ex.Message}");
            }
        }
    }
}
