using AgentCore.Editor.Extensions;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI.Toolbar
{
    /// <summary>
    /// Toolbar status contribution mounting the <see cref="ProviderProfileSelector"/> (v1.13.0).
    /// </summary>
    /// <remarks>
    /// Auto-discovered by <c>AgentCoreExtensionRegistry.Statuses</c> and mounted by
    /// <c>ChatWindow.Hub.MountToolbarStatusContributions</c> — no manual wiring in ChatWindow.
    /// Order 100 places it ahead of the code-indexing chip (Order 350); the language selector is
    /// hard-mounted separately (not a contribution), so it does not compete for ordering here.
    /// </remarks>
    public sealed class ProviderProfileStatusContribution : IAgentCoreStatusContribution
    {
        /// <inheritdoc />
        public string Id => "provider-profile-selector";

        /// <inheritdoc />
        public int Order => 100;

        /// <inheritdoc />
        public VisualElement CreateStatusElement()
            => new ProviderProfileSelector();
    }
}
