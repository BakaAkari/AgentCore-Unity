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
    /// </summary>
    [AgentTool("batch_execute",
        Description = "Execute multiple tool calls in a single request for efficiency. " +
            "Pass an array of {tool, args} objects. Each operation executes sequentially. " +
            "Options: stop_on_error (default true) halts on first failure; transaction (default false) wraps all in Undo group for atomic rollback. " +
            "Use when: 2+ similar operations on different targets (e.g. set_transform on 5 objects, add component to 3 objects). " +
            "NOT for: operations that depend on the result of a previous step (use sequential single calls instead). " +
            "Returns: array of {tool, success, result} for each operation, plus summary counts.",
        Category = "Meta",
        RequiresMainThread = true,
        RiskLevel = ToolRiskLevel.High,
        Capabilities = ToolCapability.BatchExecute)]
    public class BatchExecuteTool : IAgentTool
    {
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
                    ""description"": ""If true and any operation fails, undo all executed operations (default: false)""
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
                int failCount = 0;
                int skippedCount = 0;
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
                    try
                    {
                        var result = await tool.ExecuteAsync(toolArgs, cancellationToken);
                        opSw.Stop();

                        if (result.Success)
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

                // Handle transaction rollback
                bool rolledBack = false;
                if (transaction && failCount > 0 && undoGroup >= 0)
                {
                    Undo.RevertAllDownToGroup(undoGroup);
                    rolledBack = true;
                }

                sw.Stop();

                var summary = new JObject
                {
                    ["total_operations"] = operations.Count,
                    ["success_count"] = successCount,
                    ["fail_count"] = failCount,
                    ["skipped_count"] = skippedCount,
                    ["transaction_mode"] = transaction,
                    ["rolled_back"] = rolledBack,
                    ["total_execution_time_ms"] = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                    ["results"] = results
                };

                if (failCount > 0)
                {
                    string message = rolledBack
                        ? $"Batch execution completed with errors. {failCount}/{operations.Count} failed. All changes rolled back."
                        : $"Batch execution completed with errors. {successCount} succeeded, {failCount} failed, {skippedCount} skipped.";
                    return ToolResponse.OkWithData(summary, message)
                        .ToToolResult(sw.Elapsed.TotalMilliseconds);
                }

                return ToolResponse.OkWithData(summary,
                        $"Batch execution completed successfully. All {successCount} operations succeeded.")
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
