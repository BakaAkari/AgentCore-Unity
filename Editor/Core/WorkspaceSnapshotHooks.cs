using System;

namespace AgentCore.Editor.Core
{
    /// <summary>
    /// v1.4.0 — Extension slots for <see cref="WorkspaceSnapshotBuilder"/>.
    /// <para>
    /// Optional components (Indexing / VCS / …) register callbacks here so their contribution
    /// can be composed into the first-turn workspace snapshot without the main assembly
    /// referencing component-specific types. This preserves the AGENTS.md §3.4 rule:
    /// "main assembly does not reverse-reference optional component assemblies".
    /// </para>
    /// <para>
    /// Contract:
    /// </para>
    /// <list type="bullet">
    ///   <item>Providers must return a synchronous string (no awaits, no blocking I/O).</item>
    ///   <item>Return <c>null</c> or empty string to skip contribution.</item>
    ///   <item>Exceptions are caught by the caller and logged as warnings.</item>
    ///   <item>Providers should be registered during static initialization (e.g. from an
    ///   <c>[InitializeOnLoad]</c> class inside the component assembly).</item>
    /// </list>
    /// </summary>
    public static class WorkspaceSnapshotHooks
    {
        /// <summary>
        /// Provider callback for the "Index Status" block. Registered by the Indexing
        /// component (when <c>AGENTCORE_INDEXING</c> is defined); otherwise null.
        /// </summary>
        public static Func<string> IndexStatusBlockProvider { get; set; }
    }
}
