using System;
using AgentCore.Editor.Components.Indexing.Core;
using UnityEngine.UIElements;

namespace AgentCore.Editor.Components.Indexing.UI
{
    /// <summary>
    /// Lightweight toolbar chip that mirrors background indexing status.
    /// </summary>
    public sealed class IndexingStatusChip : Label, IDisposable
    {
        private const string RootClass = "agentcore-status-chip";
        private const string PendingClass = "agentcore-status-chip--pending";
        private const string RunningClass = "agentcore-status-chip--running";
        private const string FailedClass = "agentcore-status-chip--failed";
        private const string DisabledClass = "agentcore-status-chip--disabled";

        /// <summary>
        /// Creates an indexing toolbar status chip and subscribes to status updates.
        /// </summary>
        public IndexingStatusChip()
        {
            AddToClassList(RootClass);
            IndexingStatusBus.StatusChanged += OnStatusChanged;
            Apply(IndexingStatusBus.Current);
        }

        /// <summary>
        /// Releases event subscriptions held by this chip.
        /// </summary>
        public void Dispose()
        {
            IndexingStatusBus.StatusChanged -= OnStatusChanged;
        }

        private void OnStatusChanged(IndexingStatusSnapshot snapshot)
        {
            schedule.Execute(() => Apply(snapshot));
        }

        private void Apply(IndexingStatusSnapshot snapshot)
        {
            RemoveFromClassList(PendingClass);
            RemoveFromClassList(RunningClass);
            RemoveFromClassList(FailedClass);
            RemoveFromClassList(DisabledClass);

            if (snapshot == null || snapshot.State == IndexingBackgroundState.Idle)
            {
                style.display = DisplayStyle.None;
                text = string.Empty;
                tooltip = string.Empty;
                return;
            }

            style.display = DisplayStyle.Flex;
            tooltip = snapshot.LastError ?? string.Empty;

            switch (snapshot.State)
            {
                case IndexingBackgroundState.Pending:
                    text = snapshot.DirtyFileCount > 0
                        ? $"Indexing pending: {snapshot.DirtyFileCount}"
                        : "Indexing pending";
                    AddToClassList(PendingClass);
                    break;

                case IndexingBackgroundState.Running:
                    text = snapshot.TotalFiles > 0
                        ? $"Indexing {snapshot.ProcessedFiles}/{snapshot.TotalFiles}"
                        : "Indexing...";
                    AddToClassList(RunningClass);
                    break;

                case IndexingBackgroundState.Failed:
                    text = "Indexing failed";
                    AddToClassList(FailedClass);
                    break;

                case IndexingBackgroundState.Disabled:
                    text = "Indexing disabled";
                    AddToClassList(DisabledClass);
                    break;
            }
        }
    }
}
