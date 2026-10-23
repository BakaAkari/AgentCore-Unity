using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Tools.Infrastructure;
using AgentCore.Editor.Tools.Safety;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Tools.Native.Meta
{
    /// <summary>
    /// Execute multiple tool calls in sequence as a batch operation for efficiency.
    /// Supports stop-on-error and transaction (undo rollback) modes.
    /// <para>
    /// transaction 回滚只调用 <c>Undo.RevertAllDownToGroup</c>，只能撤销记录在 Unity Undo 系统中的修改
    /// （场景 / GameObject / 组件等）。文件写入、资产创建/删除、脚本修改、包安装、设置修改等
    /// 不经过 Undo 的副作用不会被回滚，结果中会逐条列出"可能未回滚"的子操作。
    /// </para>
    /// </summary>
    [AgentTool("batch_execute",
        Description = "Execute multiple tool calls in a single request for efficiency. " +
            "Pass an array of {tool, args} objects. Each operation executes sequentially. " +
            "Options: stop_on_error (default true) halts on first failure; transaction (default false) groups all operations in one Undo group and, if any operation fails, reverts the changes recorded in Unity's Undo system (scene/GameObject/component edits). " +
            "Rollback is NOT atomic: file writes, asset creation/deletion, script edits, package installs and settings changes are not reverted; such operations are listed in the result under not_reverted. " +
            "Use when: 2+ similar operations on different targets (e.g. set_transform on 5 objects, add component to 3 objects). " +
            "NOT for: operations that depend on the result of a previous step (use sequential single calls instead). " +
            "Returns: array of {index, tool, status (success/partial/error/skipped), output|error} for each operation, plus summary counts. " +
            "If any operation failed, was partial or was skipped, the batch is reported as partial (or failed when nothing succeeded), never as a full success.",
        Category = "Meta",
        RequiresMainThread = true,
        RiskLevel = ToolRiskLevel.High,
        Capabilities = ToolCapability.BatchExecute,
        ReadOnlyActions = new[] { AllSubOperationsReadOnlySentinel })]
    public class BatchExecuteTool : IAgentTool
    {
        /// <summary>
        /// 哨兵 action 值，供 <see cref="AgentCore.Editor.Tools.ToolCallDispatcher"/> 使用。
        /// <para>
        /// batch_execute 本身没有 "action" 参数（用 operations 数组代替），无法像其他工具那样
        /// 靠单个 action 名判定是否只读。Dispatcher 会动态检查 operations 数组中每个子操作
        /// 是否命中各自工具自己声明的 ReadOnlyActions 白名单——若全部只读，则把有效 action
        /// 解析为本哨兵值，命中本工具声明的 ReadOnlyActions，从而跳过 High 风险确认弹窗。
        /// 不硬编码任何具体子工具/action 名单，新增只读子工具只需自己正确声明
        /// ReadOnlyActions 即可被自动识别。
        /// </para>
        /// </summary>
        public const string AllSubOperationsReadOnlySentinel = "__all_sub_operations_read_only__";

        /// <summary>Default max chars per sub-tool output before truncation (from 500 in v1.10.0, raised in v1.10.1).</summary>
        private const int DefaultMaxOutputChars = 8000;

        /// <summary>Hard upper bound to prevent runaway context bloat, even when caller passes a huge number.</summary>
        private const int MaxAllowedOutputChars = 50000;

        #region Schema

        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""operations"": {
                    ""type"": ""array"",
                    ""description"": ""List of tool operations to execute sequentially"",
                    ""items"": {
                        ""type"": ""object"",
                        ""properties"": {
                            ""tool"": {
                                ""type"": ""string"",
                                ""description"": ""Tool name to invoke""
                            },
                            ""args"": {
                                ""type"": ""object"",
                                ""description"": ""Arguments to pass to the tool""
                            }
                        },
                        ""required"": [""tool""]
                    }
                },
                ""stop_on_error"": {
                    ""type"": ""boolean"",
                    ""description"": ""Stop executing on first error (default: true)""
                },
                ""transaction"": {
                    ""type"": ""boolean"",
                    ""description"": ""If true and any operation fails, revert changes recorded in Unity's Undo system (scene/GameObject/component edits). File writes, asset creation/deletion, script edits, package installs and settings changes are NOT reverted (default: false)""
                },
                ""max_output_chars_per_op"": {
                    ""type"": ""integer"",
                    ""description"": ""Max chars per sub-tool output before truncation. Default 8000 (v1.10.1). Set to -1 to disable truncation (bounded by 50000 hard cap). Use higher values when sub-tool outputs are structured JSON (collision matrices, assembly lists, physics stats).""
                }
            },
            ""required"": [""operations""]
        }");

        #endregion

        public ToolMetadata Metadata => new ToolMetadata(
            name: "batch_execute",
            description: "Execute multiple tool calls in sequence as a batch operation for efficiency",
            category: "meta",
            parametersSchema: _parametersSchema,
            requiresMainThread: true
        );

        public async Task<ToolResult> ExecuteAsync(JObject parameters, CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();

            try
            {
                var operations = ToolHelpers.GetOptionalArray(parameters, "operations");
                if (operations == null || operations.Count == 0)
                {
                    return ToolResponse.Fail("Parameter 'operations' is required and must be a non-empty array.")
                        .ToToolResult(sw.Elapsed.TotalMilliseconds);
                }

                var stopOnError = ToolHelpers.GetOptionalBool(parameters, "stop_on_error", true);
                var transaction = ToolHelpers.GetOptionalBool(parameters, "transaction", false);

                var maxOutputChars = ToolHelpers.GetOptionalInt(parameters, "max_output_chars_per_op", DefaultMaxOutputChars);
                if (maxOutputChars < 0) maxOutputChars = MaxAllowedOutputChars;
                if (maxOutputChars > MaxAllowedOutputChars) maxOutputChars = MaxAllowedOutputChars;

                // Get tool registry
                var registry = ToolRegistry.Instance;

                // Set up undo group for transaction mode
                int undoGroup = -1;
                if (transaction)
                {
                    Undo.IncrementCurrentGroup();
                    undoGroup = Undo.GetCurrentGroup();
                    Undo.SetCurrentGroupName("AgentCore: Batch Execute");
                }

                var results = new JArray();
                int successCount = 0;
                int partialCount = 0;
                int failCount = 0;
                int skippedCount = 0;
                // 实际调用过工具的子操作（含失败的：失败的子操作也可能已产生部分副作用），用于回滚说明
                var executedOps = new List<(int Index, string ToolName, IAgentTool Tool, JObject Args)>();
                bool stopped = false;

                for (int i = 0; i < operations.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (stopped)
                    {
                        skippedCount++;
                        results.Add(new JObject
                        {
                            ["index"] = i,
                            ["status"] = "skipped",
                            ["reason"] = "Execution stopped due to previous error"
                        });
                        continue;
                    }

                    var op = operations[i] as JObject;
                    if (op == null)
                    {
                        failCount++;
                        results.Add(new JObject
                        {
                            ["index"] = i,
                            ["status"] = "error",
                            ["error"] = "Invalid operation format: expected a JSON object"
                        });
                        if (stopOnError)
                        {
                            stopped = true;
                        }
                        continue;
                    }

                    var toolName = op["tool"]?.ToString();
                    if (string.IsNullOrEmpty(toolName))
                    {
                        failCount++;
                        results.Add(new JObject
                        {
                            ["index"] = i,
                            ["status"] = "error",
                            ["error"] = "Missing 'tool' field in operation"
                        });
                        if (stopOnError)
                        {
                            stopped = true;
                        }
                        continue;
                    }

                    // Prevent recursive batch_execute calls
                    if (toolName == "batch_execute")
                    {
                        failCount++;
                        results.Add(new JObject
                        {
                            ["index"] = i,
                            ["tool"] = toolName,
                            ["status"] = "error",
                            ["error"] = "Recursive batch_execute calls are not allowed"
                        });
                        if (stopOnError)
                        {
                            stopped = true;
                        }
                        continue;
                    }

                    var tool = registry.GetTool(toolName);
                    if (tool == null)
                    {
                        failCount++;
                        results.Add(new JObject
                        {
                            ["index"] = i,
                            ["tool"] = toolName,
                            ["status"] = "error",
                            ["error"] = $"Tool '{toolName}' not found in registry"
                        });
                        if (stopOnError)
                        {
                            stopped = true;
                        }
                        continue;
                    }

                    var toolArgs = op["args"] as JObject ?? new JObject();

                    // Execute the tool
                    var opSw = Stopwatch.StartNew();
                    executedOps.Add((i, toolName, tool, toolArgs));
                    try
                    {
                        var result = await tool.ExecuteAsync(toolArgs, cancellationToken);
                        opSw.Stop();

                        if (result.Success && result.IsPartial)
                        {
                            // 子操作只部分成功：不计入成功，单独计数；已产生的修改保留，与以前一样不触发 stop_on_error
                            partialCount++;
                            results.Add(new JObject
                            {
                                ["index"] = i,
                                ["tool"] = toolName,
                                ["status"] = "partial",
                                ["execution_time_ms"] = Math.Round(opSw.Elapsed.TotalMilliseconds, 2),
                                ["output"] = TruncateOutput(result.Output, maxOutputChars)
                            });
                        }
                        else if (result.Success)
                        {
                            successCount++;
                            results.Add(new JObject
                            {
                                ["index"] = i,
                                ["tool"] = toolName,
                                ["status"] = "success",
                                ["execution_time_ms"] = Math.Round(opSw.Elapsed.TotalMilliseconds, 2),
                                ["output"] = TruncateOutput(result.Output, maxOutputChars)
                            });
                        }
                        else
                        {
                            failCount++;
                            results.Add(new JObject
                            {
                                ["index"] = i,
                                ["tool"] = toolName,
                                ["status"] = "error",
                                ["execution_time_ms"] = Math.Round(opSw.Elapsed.TotalMilliseconds, 2),
                                ["error"] = result.Error ?? "Unknown error"
                            });
                            if (stopOnError)
                            {
                                stopped = true;
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw; // Re-throw cancellation
                    }
                    catch (Exception ex)
                    {
                        opSw.Stop();
                        failCount++;
                        results.Add(new JObject
                        {
                            ["index"] = i,
                            ["tool"] = toolName,
                            ["status"] = "error",
                            ["execution_time_ms"] = Math.Round(opSw.Elapsed.TotalMilliseconds, 2),
                            ["error"] = $"Exception: {ex.Message}"
                        });
                        if (stopOnError)
                        {
                            stopped = true;
                        }
                    }
                }

                // Handle transaction rollback（触发条件与以前一致：transaction 且有失败）
                bool rolledBack = false;
                JArray notReverted = null;
                if (transaction && failCount > 0 && undoGroup >= 0)
                {
                    Undo.RevertAllDownToGroup(undoGroup);
                    rolledBack = true;
                    // Undo 回滚管不到的副作用：逐条列出可能未回滚的子操作
                    notReverted = CollectPossiblyNotReverted(executedOps);
                    foreach (var r in results)
                    {
                        var status = r["status"]?.ToString();
                        if (status == "success" || status == "partial")
                            r["rolled_back"] = "undo_tracked_changes_only";
                    }
                }

                sw.Stop();

                var summary = new JObject
                {
                    ["total_operations"] = operations.Count,
                    ["success_count"] = successCount,
                    ["partial_count"] = partialCount,
                    ["fail_count"] = failCount,
                    ["skipped_count"] = skippedCount,
                    ["transaction_mode"] = transaction,
                    ["rolled_back"] = rolledBack,
                    ["total_execution_time_ms"] = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                    ["results"] = results
                };
                if (rolledBack)
                {
                    summary["rollback_scope"] = "Only changes recorded in Unity's Undo system were reverted (Undo.RevertAllDownToGroup).";
                    summary["not_reverted"] = notReverted;
                }

                if (failCount == 0 && partialCount == 0 && skippedCount == 0)
                {
                    return ToolResponse.OkWithData(summary,
                            $"Batch execution completed successfully. All {successCount} operations succeeded.")
                        .ToToolResult(sw.Elapsed.TotalMilliseconds);
                }

                // 有失败 / 部分成功 / 跳过：逐项回报，不再整体报成功
                var items = BuildItems(results, rolledBack);
                var message = BuildOutcomeMessage(successCount, partialCount, failCount, skippedCount, transaction, rolledBack, notReverted);
                return ToolResponse.FromItems(message, items, summary)
                    .ToToolResult(sw.Elapsed.TotalMilliseconds);
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                return ToolResponse.Fail("Batch execution was cancelled.")
                    .ToToolResult(sw.Elapsed.TotalMilliseconds);
            }
            catch (ArgumentException ex)
            {
                sw.Stop();
                return ToolResponse.Fail(ex.Message)
                    .ToToolResult(sw.Elapsed.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                sw.Stop();
                return ToolResponse.Fail($"Unexpected error in batch execution: {ex.Message}")
                    .ToToolResult(sw.Elapsed.TotalMilliseconds);
            }
        }

        #region Helpers

        /// <summary>
        /// 不经过 Undo、回滚无法撤销的能力位（写文件 / 删资产 / 改资产 / 改脚本 / 装包 / 构建 /
        /// 改设置 / 改 AgentCore 配置 / 版本控制写入 / 执行任意代码 / 网络请求）。
        /// </summary>
        private const ToolCapability NonUndoableCapabilities =
            ToolCapability.WriteProjectFiles | ToolCapability.DeleteProjectFiles | ToolCapability.ModifyAssets |
            ToolCapability.ModifyScripts | ToolCapability.InstallPackages | ToolCapability.BuildPlayer |
            ToolCapability.ModifyProjectSettings | ToolCapability.ModifyAgentConfig | ToolCapability.VersionControlWrite |
            ToolCapability.ExecuteCode | ToolCapability.NetworkAccess;

        /// <summary>
        /// 列出已执行、且工具声明了不经过 Undo 的能力（非只读 action）的子操作——这些子操作的
        /// 文件写入 / 资产创建等可能没有被 Undo 回滚。按工具声明判断，属于保守估计（"可能"）。
        /// </summary>
        private static JArray CollectPossiblyNotReverted(List<(int Index, string ToolName, IAgentTool Tool, JObject Args)> executedOps)
        {
            var list = new JArray();
            foreach (var op in executedOps)
            {
                var metadata = op.Tool?.Metadata;
                if (metadata == null) continue;

                var action = op.Args?["action"]?.Type == JTokenType.String ? op.Args["action"].Value<string>() : null;
                if (!string.IsNullOrEmpty(action) && metadata.IsReadOnlyAction(action)) continue;

                var caps = metadata.Capabilities & NonUndoableCapabilities;
                if (caps == 0) continue;

                var entry = new JObject
                {
                    ["index"] = op.Index,
                    ["tool"] = op.ToolName,
                    ["capabilities"] = caps.ToString()
                };
                if (!string.IsNullOrEmpty(action)) entry["action"] = action;

                // 尽量列出涉及的路径（解析失败不影响回报）
                try
                {
                    ToolPathRiskResolver.Resolve(op.Args, metadata, out var targets);
                    if (targets != null && targets.Count > 0) entry["paths"] = new JArray(targets);
                }
                catch (Exception)
                {
                    // 路径解析只是辅助信息，失败时忽略
                }

                list.Add(entry);
            }
            return list;
        }

        /// <summary>
        /// 把每个子操作结果转换为逐项结果。回滚后，原本成功的子操作也标为未生效（其 Undo 可追踪部分已撤销）。
        /// </summary>
        private static List<ToolItemResult> BuildItems(JArray results, bool rolledBack)
        {
            var items = new List<ToolItemResult>();
            foreach (var token in results)
            {
                var r = token as JObject;
                if (r == null) continue;
                var target = $"#{r["index"]}" + (r["tool"] != null ? $" {r["tool"]}" : string.Empty);
                switch (r["status"]?.ToString())
                {
                    case "success":
                        items.Add(rolledBack
                            ? ToolItemResult.Failed(target, "succeeded, then rolled back (Undo-tracked changes only)")
                            : ToolItemResult.Succeeded(target));
                        break;
                    case "partial":
                        items.Add(ToolItemResult.Failed(target, rolledBack
                            ? "only partially succeeded, then rolled back (Undo-tracked changes only)"
                            : "only partially succeeded; see its output in results"));
                        break;
                    case "skipped":
                        items.Add(ToolItemResult.Failed(target, "skipped (not executed) because an earlier operation failed and stop_on_error=true"));
                        break;
                    default:
                        items.Add(ToolItemResult.Failed(target, r["error"]?.ToString() ?? "Unknown error"));
                        break;
                }
            }
            return items;
        }

        /// <summary>
        /// 生成如实描述批次结果与回滚范围的摘要文本。
        /// </summary>
        private static string BuildOutcomeMessage(int successCount, int partialCount, int failCount, int skippedCount,
            bool transaction, bool rolledBack, JArray notReverted)
        {
            var message = $"Batch execution did not fully succeed: {successCount} succeeded, {partialCount} partial, {failCount} failed, {skippedCount} skipped.";

            if (rolledBack)
            {
                message += " Transaction rollback reverted only changes recorded in Unity's Undo system (scene/GameObject/component edits).";
                if (notReverted != null && notReverted.Count > 0)
                {
                    var parts = new List<string>();
                    foreach (var e in notReverted)
                    {
                        var label = $"#{e["index"]} {e["tool"]}" + (e["action"] != null ? $".{e["action"]}" : string.Empty);
                        if (e["paths"] is JArray paths && paths.Count > 0)
                            label += $" [{string.Join(", ", paths)}]";
                        parts.Add(label);
                    }
                    message += " These executed operations may have written files, created/deleted assets or changed settings that were NOT reverted: " +
                               string.Join("; ", parts) + ".";
                }
                else
                {
                    message += " No executed operation declares non-Undo side effects (file/asset/script/settings writes).";
                }
            }
            else if (transaction)
            {
                message += " No rollback was performed (no operation failed); changes from executed operations are kept.";
            }
            else
            {
                message += " No rollback was performed (transaction=false); changes from executed operations are kept.";
            }

            return message;
        }

        /// <summary>
        /// Truncate output string to avoid oversized responses.
        /// When truncation happens, appends an explicit hint so the LLM knows to call the tool directly if it needs the full output.
        /// </summary>
        private static string TruncateOutput(string output, int maxLength)
        {
            if (string.IsNullOrEmpty(output)) return output;
            if (output.Length <= maxLength) return output;
            var omitted = output.Length - maxLength;
            return output.Substring(0, maxLength) +
                $"\n...(truncated by batch_execute: {omitted} chars omitted. " +
                $"Call this tool directly outside batch_execute, or increase max_output_chars_per_op to see full output.)";
        }

        #endregion
    }
}
