namespace AgentCore.Editor.Components.Indexing.Models
{
    /// <summary>
    /// v1.4.0 — Scheduling priority for a single <see cref="IndexRoot"/>.
    /// <para>
    /// Assigned by <c>IndexingSchedulePolicy</c> based on <see cref="IndexRootRole"/>.
    /// Consumed by <c>BackgroundIndexService</c> to decide whether a root participates in the
    /// automatic incremental loop.
    /// </para>
    /// </summary>
    public enum IndexRootPriority
    {
        /// <summary>
        /// Editable project code that changes constantly. Included in every background
        /// incremental pass and prioritized ahead of Background roots.
        /// </summary>
        Foreground,

        /// <summary>
        /// Workspace-owned code that changes occasionally (custom plugins, tooling, packages).
        /// Included in background passes but processed after Foreground roots.
        /// </summary>
        Background,

        /// <summary>
        /// Read-only or rarely-changing code (commercial plugins, engine, generated code).
        /// Skipped by the automatic incremental loop. Only indexed when the user or LLM
        /// explicitly invokes <c>search_code::index_scope</c> or <c>search_code::index_full</c>.
        /// </summary>
        OnDemand
    }
}
