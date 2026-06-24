using AgentCore.Editor.Extensions;
using UnityEngine.UIElements;

namespace AgentCore.Editor.Components.Indexing.UI
{
    /// <summary>
    /// AgentCore toolbar status contribution for background code indexing.
    /// </summary>
    public sealed class IndexingStatusContribution : IAgentCoreStatusContribution
    {
        /// <inheritdoc />
        public string Id => "code-indexing-status";

        /// <inheritdoc />
        public int Order => 350;

        /// <inheritdoc />
        public VisualElement CreateStatusElement()
        {
            return new IndexingStatusChip();
        }
    }
}
