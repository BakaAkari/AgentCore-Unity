namespace AgentCore.Editor.Components.Indexing.Models
{
    /// <summary>
    /// v1.4.0 — Per-root indexing lifecycle state.
    /// <para>
    /// Tracked in memory on <see cref="IndexRoot"/> and persisted to <c>IIndexStore</c> metadata KV
    /// (key format: <c>root:{rootId}:state</c>) so it survives Domain Reload.
    /// </para>
    /// </summary>
    public enum IndexRootState
    {
        /// <summary>Root has never been indexed. Search calls will not return symbols from it.</summary>
        NotIndexed,

        /// <summary>Root is currently being indexed (full or incremental).</summary>
        Indexing,

        /// <summary>Root has been fully indexed and is up-to-date. Search returns complete results.</summary>
        Ready,

        /// <summary>
        /// Root was indexed previously but has dirty files pending re-index. Search may return
        /// stale results.
        /// </summary>
        Stale,

        /// <summary>Last index run failed. See <c>LastIndexError</c> for details.</summary>
        Failed,

        /// <summary>Root exists but is disabled by user or role/scope policy.</summary>
        Disabled
    }
}
