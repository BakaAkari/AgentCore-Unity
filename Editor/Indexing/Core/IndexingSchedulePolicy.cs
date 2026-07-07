using AgentCore.Editor.Components.Indexing.Models;

namespace AgentCore.Editor.Components.Indexing.Core
{
    /// <summary>
    /// v1.4.0 — Assigns an <see cref="IndexRootPriority"/> to each <see cref="IndexRoot"/>
    /// based on its <see cref="IndexRootRole"/> and <see cref="IndexScopeType"/>.
    /// <para>
    /// Consumed by <c>BackgroundIndexService</c> to decide whether a root participates in the
    /// automatic incremental loop. <see cref="IndexRootPriority.OnDemand"/> roots are excluded
    /// unless the user or LLM explicitly invokes <c>search_code::index_scope</c>.
    /// </para>
    /// <para>
    /// Design note: role takes precedence over scope type because role encodes editability
    /// (which drives change frequency), while scope type is orthogonal (naming category).
    /// </para>
    /// </summary>
    public static class IndexingSchedulePolicy
    {
        /// <summary>
        /// Resolve the scheduling priority for a single root.
        /// </summary>
        /// <param name="root">Non-null <see cref="IndexRoot"/>.</param>
        /// <returns>
        /// - <see cref="IndexRootPriority.Foreground"/> for <c>EditableProjectCode</c> / <c>SharedCode</c><br/>
        /// - <see cref="IndexRootPriority.Background"/> for <c>WorkspacePackage</c> / <c>ToolingCode</c> / <c>CustomPlugin</c><br/>
        /// - <see cref="IndexRootPriority.OnDemand"/> for <c>CommercialPlugin</c> / <c>EngineCode</c> / <c>GeneratedCode</c> / <c>ReadOnlyReference</c>
        /// </returns>
        public static IndexRootPriority ResolvePriority(IndexRoot root)
        {
            if (root == null)
            {
                return IndexRootPriority.Background;
            }

            switch (root.Role)
            {
                case IndexRootRole.EditableProjectCode:
                case IndexRootRole.SharedCode:
                    return IndexRootPriority.Foreground;

                case IndexRootRole.WorkspacePackage:
                case IndexRootRole.ToolingCode:
                case IndexRootRole.CustomPlugin:
                    return IndexRootPriority.Background;

                case IndexRootRole.CommercialPlugin:
                case IndexRootRole.EngineCode:
                case IndexRootRole.GeneratedCode:
                case IndexRootRole.ReadOnlyReference:
                    return IndexRootPriority.OnDemand;

                default:
                    // Unknown role — conservative default keeps automatic loop simple.
                    return IndexRootPriority.Background;
            }
        }

        /// <summary>
        /// Whether a root should participate in the automatic background incremental loop.
        /// <c>OnDemand</c> roots are skipped; user/LLM must explicitly invoke index_scope.
        /// </summary>
        public static bool ParticipatesInBackgroundLoop(IndexRoot root)
        {
            return ResolvePriority(root) != IndexRootPriority.OnDemand;
        }
    }
}
