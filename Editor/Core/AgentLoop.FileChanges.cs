using System;
using UnityEngine;
using AgentCore.Editor.Utils;

namespace AgentCore.Editor.Core
{
    public partial class AgentLoop
    {
        /// <summary>
        /// 尝试从 <see cref="DomainReloadState"/> 恢复文件变更追踪数据。
        /// 在 Domain Reload 后的会话恢复时调用。
        /// </summary>
        private void TryRestoreFileChangeTracker()
        {
            try
            {
                var reloadState = DomainReloadState.instance;
                var json = reloadState.FileChangeRecordsJson;

                if (string.IsNullOrEmpty(json))
                    return;

                if (_fileChangeTracker == null)
                {
                    _fileChangeTracker = new FileChangeTracker();
                }

                int restoredCount = _fileChangeTracker.RestoreFromJson(json);
                if (restoredCount > 0)
                {
                    AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] FileChangeTracker restored {restoredCount} records from DomainReloadState.");
                }

                // 清除已恢复的数据，避免下次 LoadSession 重复恢复
                reloadState.ClearFileChangeRecords();
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore] Failed to restore FileChangeTracker: {ex.Message}");
            }
        }

        /// <summary>
        /// 发送文件变更更新事件到 UI 层。
        /// 在 Domain Reload 恢复后由 <see cref="ChatWindow"/> 调用，
        /// 确保 UI 面板显示恢复的文件变更数据。
        /// </summary>
        public void EmitFileChangesUpdatedEvent()
        {
            if (_fileChangeTracker == null || !_fileChangeTracker.HasChanges)
                return;

            var summaries = _fileChangeTracker.GetSummaries();
            if (summaries.Count > 0)
            {
                EmitEvent(AgentEvent.FileChangesUpdated(summaries));
            }
        }
    }
}
