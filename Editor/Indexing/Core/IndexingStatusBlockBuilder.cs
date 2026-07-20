using System;
using System.Collections.Generic;
using System.Text;
using AgentCore.Editor.Components.Indexing.Models;
using AgentCore.Editor.Core;
using UnityEditor;
using UnityEngine;
using AgentCore.Editor.Utils;

namespace AgentCore.Editor.Components.Indexing.Core
{
    /// <summary>
    /// v1.4.0 — Builds the "Index Status" block that decorates the first-turn workspace snapshot.
    /// <para>
    /// Registered as <see cref="WorkspaceSnapshotHooks.IndexStatusBlockProvider"/> at editor
    /// startup via <see cref="RegisterHook"/>. When the Indexing component is not compiled
    /// (<c>AGENTCORE_INDEXING</c> undefined), the hook remains unregistered and the snapshot
    /// contains no Index Status block — this preserves the AGENTS.md §3.4 decoupling
    /// (main assembly does not reverse-reference component assemblies).
    /// </para>
    /// <para>
    /// Design constraint: this builder runs synchronously on the Unity main thread during
    /// system prompt injection, so it must NOT touch any async / I/O paths. It uses only:
    /// </para>
    /// <list type="bullet">
    ///   <item><see cref="IndexingStatusBus.Current"/> — in-memory snapshot, no I/O</item>
    ///   <item><see cref="IndexRootResolver"/> — pure computation over registered providers</item>
    ///   <item>Static <see cref="IndexRoot"/> metadata (Role, ScopeType, Priority, DisplayName)</item>
    /// </list>
    /// <para>
    /// Per-root live state (Ready / Stale / Failed with counts) requires <c>IIndexStore</c>
    /// access, which is async. LLM must call <c>search_code::status</c> or
    /// <c>search_code::diagnose</c> to fetch that (pull model, documented in SOUL.md §5).
    /// </para>
    /// </summary>
    [InitializeOnLoad]
    public static class IndexingStatusBlockBuilder
    {
        static IndexingStatusBlockBuilder()
        {
            RegisterHook();
        }

        /// <summary>
        /// Register this builder as the <see cref="WorkspaceSnapshotHooks.IndexStatusBlockProvider"/>.
        /// Idempotent — safe to call multiple times.
        /// </summary>
        public static void RegisterHook()
        {
            WorkspaceSnapshotHooks.IndexStatusBlockProvider = Build;
        }

        /// <summary>
        /// Build the Index Status markdown block. Returns null when indexing is disabled or
        /// no meaningful data is available (keeps the snapshot lean).
        /// </summary>
        public static string Build()
        {
            try
            {
                var snapshot = IndexingStatusBus.Current;

                // If the service is disabled and there are no roots to report, skip entirely.
                var roots = ResolveRootsSafe();
                if (snapshot == null && (roots == null || roots.Count == 0))
                {
                    return null;
                }

                var sb = new StringBuilder();
                sb.AppendLine("## Index Status");
                sb.AppendLine();

                // Background service snapshot
                sb.Append("Background: ").Append(snapshot?.State.ToString() ?? "Unknown");
                if (snapshot != null)
                {
                    if (snapshot.LastSuccessAt.HasValue)
                    {
                        sb.Append($" (last success: {snapshot.LastSuccessAt.Value:yyyy-MM-dd HH:mm:ss}Z)");
                    }
                    else
                    {
                        sb.Append(" (never run)");
                    }
                }
                sb.AppendLine();

                if (snapshot != null && snapshot.DirtyFileCount > 0)
                {
                    sb.AppendLine($"Dirty files pending: {snapshot.DirtyFileCount}");
                }

                if (!string.IsNullOrEmpty(snapshot?.ReasonPaused))
                {
                    sb.AppendLine($"Paused: {snapshot.ReasonPaused}");
                }

                if (!string.IsNullOrEmpty(snapshot?.LastError))
                {
                    sb.AppendLine($"Last error: {Truncate(snapshot.LastError, 120)}");
                }

                sb.AppendLine();

                // Roots categorized by scheduling priority (proxy for "is it participating in auto index").
                // Per-root Ready/Stale/Failed state requires I/O and is intentionally omitted;
                // instruct LLM to fetch via search_code::status when it needs specifics.
                if (roots != null && roots.Count > 0)
                {
                    var participating = new List<IndexRoot>();
                    var onDemand = new List<IndexRoot>();
                    foreach (var r in roots)
                    {
                        if (r == null || !r.IsEnabled) continue;
                        if (r.Priority == IndexRootPriority.OnDemand) onDemand.Add(r);
                        else participating.Add(r);
                    }

                    if (participating.Count > 0)
                    {
                        sb.AppendLine("Roots participating in background index:");
                        AppendRootBullets(sb, participating, max: 15);
                        sb.AppendLine();
                    }

                    if (onDemand.Count > 0)
                    {
                        sb.AppendLine("On-demand roots (not auto-indexed; call search_code::index_scope to enable):");
                        AppendRootBullets(sb, onDemand, max: 10);
                        sb.AppendLine();
                    }
                }

                sb.AppendLine("For live per-root state (Ready/Stale/Failed with counts), call search_code::status or search_code::diagnose.");

                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore] IndexingStatusBlockBuilder failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Resolve current roots without touching any store I/O.
        /// Returns null on failure (silent degradation).
        /// </summary>
        private static IReadOnlyList<IndexRoot> ResolveRootsSafe()
        {
            try
            {
                var workspace = IndexWorkspaceResolver.ResolveFromCurrent();
                var resolver = new IndexRootResolver();
                return resolver.Resolve(workspace);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Append a bounded list of root bullets to <paramref name="sb"/>.
        /// </summary>
        private static void AppendRootBullets(StringBuilder sb, IReadOnlyList<IndexRoot> roots, int max)
        {
            var shown = Math.Min(roots.Count, max);
            for (int i = 0; i < shown; i++)
            {
                var r = roots[i];
                var display = r.DisplayName ?? r.RootPath ?? "(unnamed)";
                var scope = r.ScopeType.ToString();
                var role = r.Role.ToString();
                sb.Append("- ").Append(display).Append(" (").Append(scope).Append("/").Append(role).AppendLine(")");
            }

            if (roots.Count > max)
            {
                sb.AppendLine($"- ... and {roots.Count - max} more");
            }
        }

        private static string Truncate(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= maxLen) return s;
            return s.Substring(0, maxLen) + "…";
        }
    }
}
