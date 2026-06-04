namespace AgentCore.Editor.Components.Indexing.Config
{
    /// <summary>
    /// Provides static metadata for the optional Code Indexing component.
    /// This descriptor lives inside the <c>AgentCore.Indexing.Editor</c> assembly
    /// (gated by <c>AGENTCORE_INDEXING</c>) and is referenced by
    /// <see cref="AgentCore.Editor.Extensions.OptionalComponentManager"/> to expose
    /// the component in the Extensions settings page.
    /// </summary>
    public static class IndexingComponentDescriptor
    {
        /// <summary>
        /// Stable component identifier used in <see cref="AgentCore.Editor.Extensions.OptionalComponentInfo"/>.
        /// </summary>
        public const string ComponentId = "indexing";

        /// <summary>
        /// Human-readable display name shown in the Extensions settings card.
        /// </summary>
        public const string DisplayName = "Code Indexing";

        /// <summary>
        /// Short description shown below the component name in the Extensions settings card.
        /// </summary>
        public const string Description =
            "Roslyn-based C# symbol index for the search_code tool. " +
            "Enables fast symbol lookup, namespace browsing, and incremental re-indexing " +
            "across all workspace roots.";
    }
}
