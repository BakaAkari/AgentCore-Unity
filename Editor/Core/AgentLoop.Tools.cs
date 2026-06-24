using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Core.Compression;
using AgentCore.Editor.LLM;
using AgentCore.Editor.Tools;
using AgentCore.Editor.Tools.Infrastructure;
using AgentCore.Editor.Tools.Safety;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Core
{
    public partial class AgentLoop
    {
        /// <summary>
        /// 构建当前可用的工具定义列表。
        /// <para>
        /// 从 <see cref="ToolRegistry"/> 获取所有已注册工具，
        /// 通过 <see cref="ToolDefinitionBuilder"/> 转换为 OpenAI function calling 格式。
        /// </para>
        /// </summary>
        /// <returns>工具定义列表，无工具时返回 null</returns>
        private List<ToolDefinition> BuildToolDefinitions()
        {
            try
            {
                // G.3 ActiveToolScope: 通过 ToolScopeResolver 解析当前应暴露的工具
                // 当 toolScopingEnabled=true 时，仅暴露 AlwaysVisible + 已激活的 OnDemand 分类
                // 当 toolScopingEnabled=false 时，退化为旧行为（所有非 Restricted 工具）
                var visibleMetadata = ToolScopeResolver.ResolveVisibleTools(_toolScopeState);
                var definitions = ToolDefinitionBuilder.BuildFromMetadata(visibleMetadata);

                if (definitions == null || definitions.Count == 0)
                {
                    Debug.Log("[AgentCore] No tools available, LLM will run in pure chat mode.");
                    return null;
                }

                Debug.Log($"[AgentCore] Built {definitions.Count} tool definitions for LLM (scope-resolved).");
                return definitions;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AgentCore] Failed to build tool definitions: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 执行 LLM 返回的所有工具调用，并将结果追加到消息历史。
        /// <para>
        /// 执行流程：
        /// <list type="number">
        ///   <item>为每个 tool_call 发送 <see cref="AgentEventType.ToolCallStarted"/> 事件</item>
        ///   <item>通过 <see cref="ToolCallDispatcher"/> 串行执行所有工具调用</item>
        ///   <item>为每个结果发送 <see cref="AgentEventType.ToolCallCompleted"/> 或 <see cref="AgentEventType.ToolCallFailed"/> 事件</item>
        ///   <item>将工具结果作为 <c>role="tool"</c> 消息追加到 LLM 历史</item>
        ///   <item>更新 <see cref="ConversationTurn.ToolCalls"/> 记录</item>
        /// </list>
        /// </para>
        /// </summary>
        /// <param name="toolCalls">LLM 返回的工具调用列表</param>
        /// <param name="assistantTurn">当前助手对话轮次</param>
        /// <param name="ct">取消令牌</param>
        private async Task ExecuteToolCallsAsync(
            List<ToolCall> toolCalls,
            ConversationTurn assistantTurn,
            CancellationToken ct)
        {
            // 初始化工具调用信息列表
            if (assistantTurn.ToolCalls == null)
            {
                assistantTurn.ToolCalls = new List<ToolCallInfo>();
            }

            // 为每个 tool_call 发送开始事件
            foreach (var tc in toolCalls)
            {
                var toolName = tc.Function?.Name ?? "(unknown)";
                var arguments = tc.Function?.Arguments ?? "{}";
                EmitEvent(AgentEvent.ToolCallStarted(toolName, arguments, assistantTurn.Id, tc.Id));
            }

            // Phase 4.5: 在工具执行前记录目标文件的行数快照
            _fileChangeTracker?.SnapshotBeforeExecution(toolCalls);

            // Phase 2 Step 11: 在工具执行前启动 Console 错误捕获
            _consoleCapture.StartCapture();

            // 通过 ToolCallDispatcher 串行执行所有工具调用
            var results = await _dispatcher.DispatchAllAsync(toolCalls, ct);

            // Phase 2 Step 11: 停止 Console 错误捕获
            var consoleErrors = _consoleCapture.StopCapture();

            // Phase 4.5: 追踪工具执行产生的文件变更并通知 UI
            try
            {
                _fileChangeTracker?.TrackFromToolCalls(toolCalls, results);
                if (_fileChangeTracker != null && _fileChangeTracker.HasChanges)
                {
                    EmitEvent(AgentEvent.FileChangesUpdated(_fileChangeTracker.GetSummaries()));
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AgentCore] FileChangeTracker failed (non-fatal): {ex.Message}");
            }

            // Phase 2.5: 检查是否有编译相关的工具调用
            // 优先使用 ToolResult.IsCompileRelated（工具自行标记），
            // 同时通过 [AgentTool] 属性的 MayModifyScripts 进行补充判断
            bool hasCompileRelated = false;
            foreach (var result in results)
            {
                if (result.Result.IsCompileRelated)
                {
                    hasCompileRelated = true;
                    break;
                }

                // 通过 [AgentTool] 属性判断是否可能修改脚本
                var tc = result.ToolCall;
                var parameters = ParseToolArguments(tc.Function?.Arguments);
                if (IsScriptModifyingCommand(result.ToolName, parameters))
                {
                    result.Result.IsCompileRelated = true;
                    hasCompileRelated = true;
                    break;
                }
            }

            // Phase 2 Step 11: 如果有编译相关的工具调用，等待编译完成并收集编译错误
            ErrorReport compilationReport = null;
            if (hasCompileRelated)
            {
                Debug.Log("[AgentCore] Compile-related tool detected, checking compilation status...");
                try
                {
                    // 先短暂等待让 Unity 检测到文件变化并开始编译
                    await Task.Delay(2000);

                    if (EditorApplication.isCompiling)
                    {
                        // Unity 已经在编译中，等待编译完成
                        Debug.Log("[AgentCore] Compilation detected, waiting for completion...");
                        compilationReport = await _compilationWatcher.RefreshAndWaitAsync();
                    }
                    else
                    {
                        // Unity 尚未开始编译，主动触发刷新并等待
                        // 使用较短的超时，因为如果文件确实被修改了，编译应该很快开始
                        var originalTimeout = _compilationWatcher.CompilationTimeoutSeconds;
                        _compilationWatcher.CompilationTimeoutSeconds = 15f;
                        Debug.Log("[AgentCore] No compilation in progress, triggering refresh with short timeout...");
                        compilationReport = await _compilationWatcher.RefreshAndWaitAsync();
                        _compilationWatcher.CompilationTimeoutSeconds = originalTimeout;
                    }

                    if (compilationReport != null && compilationReport.HasErrors)
                    {
                        Debug.Log($"[AgentCore] Compilation report: {compilationReport.Errors.Count} issue(s) found.");
                    }
                    else
                    {
                        Debug.Log("[AgentCore] Compilation completed with no errors.");
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[AgentCore] Compilation watch failed: {ex.Message}");
                }
            }

            // 处理每个结果
            foreach (var result in results)
            {
                // Phase 2 Step 11: 构建增强的工具结果内容（附加错误信息）
                string contentForLLM = result.Result.GetContentForLLM();
                string enhancedContent = BuildEnhancedToolContent(
                    contentForLLM,
                    result,
                    consoleErrors,
                    compilationReport
                );

                // 创建 ToolCallInfo 记录
                var callInfo = new ToolCallInfo(
                    result.ToolCall.Id,
                    result.ToolName,
                    result.ToolCall.Function?.Arguments ?? "{}"
                );

                callInfo.Success = result.Result.Success;
                callInfo.ExecutionTimeMs = result.ExecutionTimeMs;
                callInfo.EndTime = DateTime.UtcNow;
                callInfo.Result = enhancedContent;

                assistantTurn.ToolCalls.Add(callInfo);

                // G.1 治理层审计事件（D8=a: 仅 RequireConfirmation / Block）
                if (result.Decision.HasValue)
                {
                    var decision = result.Decision.Value;
                    switch (decision.Outcome)
                    {
                        case ToolPolicyOutcome.RequireConfirmation:
                            EmitEvent(AgentEvent.ToolConfirmationRequested(
                                result.ToolName,
                                result.ToolCall.Id,
                                decision,
                                assistantTurn.Id));
                            break;
                        case ToolPolicyOutcome.Block:
                            EmitEvent(AgentEvent.ToolBlocked(
                                result.ToolName,
                                result.ToolCall.Id,
                                decision,
                                assistantTurn.Id));
                            break;
                    }
                }

                // 发送工具调用结果事件
                if (result.Result.Success)
                {
                    EmitEvent(AgentEvent.ToolCallCompleted(
                        result.ToolName,
                        contentForLLM,
                        result.ExecutionTimeMs,
                        assistantTurn.Id,
                        result.ToolCall.Id
                    ));
                }
                else
                {
                    EmitEvent(AgentEvent.ToolCallFailed(
                        result.ToolName,
                        contentForLLM,
                        result.ExecutionTimeMs,
                        assistantTurn.Id,
                        result.ToolCall.Id
                    ));
                }
            }

            // Phase 2 Step 11: 构建带错误信息的 tool messages 追加到 LLM 历史
            // Phase 5: 集成工具结果压缩 — 在添加到消息历史前压缩过长的工具输出
            var toolMessages = await BuildToolMessagesWithCompressionAsync(results, consoleErrors, compilationReport, ct);
            _messages.AddRange(toolMessages);

            Debug.Log($"[AgentCore] Executed {results.Count} tool call(s), " +
                      $"added {toolMessages.Count} tool message(s) to history.");
        }

        /// <summary>
        /// 构建增强的工具结果内容，附加错误信息供 LLM 自我纠错。
        /// </summary>
        private string BuildEnhancedToolContent(
            string originalContent,
            ToolCallResult result,
            List<ErrorInfo> consoleErrors,
            ErrorReport compilationReport)
        {
            // 如果没有额外错误信息，直接返回原始内容
            bool hasConsoleErrors = consoleErrors != null && consoleErrors.Count > 0;
            bool hasCompilationErrors = compilationReport != null && compilationReport.HasErrors;

            if (!hasConsoleErrors && !hasCompilationErrors)
            {
                return originalContent;
            }

            // 只为编译相关的工具附加编译错误
            if (!result.Result.IsCompileRelated)
            {
                // 非编译相关工具只附加 console 错误
                if (!hasConsoleErrors) return originalContent;
            }

            var sb = new StringBuilder();
            sb.AppendLine(originalContent);

            // 附加 Console 错误
            if (hasConsoleErrors)
            {
                sb.AppendLine();
                sb.AppendLine("--- Console Errors Detected During Execution ---");
                int maxConsoleErrors = Math.Min(consoleErrors.Count, 5);
                for (int i = 0; i < maxConsoleErrors; i++)
                {
                    sb.Append(consoleErrors[i].FormatForLLM());
                }
                if (consoleErrors.Count > maxConsoleErrors)
                {
                    sb.AppendLine($"... and {consoleErrors.Count - maxConsoleErrors} more console errors.");
                }
            }

            // 附加编译错误（仅对编译相关工具）
            if (result.Result.IsCompileRelated && hasCompilationErrors)
            {
                sb.AppendLine();
                sb.AppendLine("--- Compilation Errors Detected ---");
                sb.AppendLine("The script modification caused compilation errors. Please fix them:");
                sb.Append(compilationReport.FormatForLLM(maxErrors: 10));
            }

            return sb.ToString();
        }

        /// <summary>
        /// 构建带错误信息的 tool messages，用于追加到 LLM 历史。
        /// 将编译错误和 console 错误合并到对应工具的结果消息中。
        /// </summary>
        private List<ChatMessage> BuildToolMessagesWithErrors(
            List<ToolCallResult> results,
            List<ErrorInfo> consoleErrors,
            ErrorReport compilationReport)
        {
            var messages = new List<ChatMessage>();

            foreach (var result in results)
            {
                string content = BuildEnhancedToolContent(
                    result.Result.GetContentForLLM(),
                    result,
                    consoleErrors,
                    compilationReport
                );

                messages.Add(ChatMessage.Tool(result.ToolCall.Id, content));
            }

            return messages;
        }

        /// <summary>
        /// 构建带错误信息和压缩的 tool messages。
        /// Phase 5: 在构建消息后对过长的工具结果进行智能压缩。
        /// </summary>
        private async Task<List<ChatMessage>> BuildToolMessagesWithCompressionAsync(
            List<ToolCallResult> results,
            List<ErrorInfo> consoleErrors,
            ErrorReport compilationReport,
            CancellationToken ct)
        {
            var messages = new List<ChatMessage>();

            foreach (var result in results)
            {
                string content = BuildEnhancedToolContent(
                    result.Result.GetContentForLLM(),
                    result,
                    consoleErrors,
                    compilationReport
                );

                // Phase 5: 尝试压缩过长的工具结果
                if (_toolResultCompressor != null)
                {
                    try
                    {
                        content = await _toolResultCompressor.CompressIfNeededAsync(
                            result.ToolName, content, ct);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[AgentCore] Tool result compression failed for '{result.ToolName}' (non-fatal): {ex.Message}");
                    }
                }

                messages.Add(ChatMessage.Tool(result.ToolCall.Id, content));
            }

            return messages;
        }

        /// <summary>
        /// 判断工具调用是否可能修改脚本文件（触发编译）。
        /// <para>
        /// Phase 2.5: 基于 <see cref="AgentToolAttribute.MayModifyScripts"/> 属性判断，
        /// 替代旧的硬编码命令名列表。对于标记了 MayModifyScripts 的工具，
        /// 进一步检查 action 参数排除只读操作（如 read、list、get_info）。
        /// </para>
        /// </summary>
        /// <param name="toolName">工具名称</param>
        /// <param name="parameters">工具参数</param>
        /// <returns>如果工具调用可能修改脚本则返回 true</returns>
        private bool IsScriptModifyingCommand(string toolName, JObject parameters)
        {
            var tool = ToolRegistry.Instance.GetTool(toolName);
            if (tool == null) return false;

            // 检查 [AgentTool] 属性的 MayModifyScripts
            var attr = tool.GetType().GetCustomAttribute<AgentToolAttribute>();
            if (attr == null || !attr.MayModifyScripts) return false;

            // 对于标记了 MayModifyScripts 的工具，进一步检查 action 参数
            // 排除只读操作（不会触发编译的操作）
            var action = parameters?["action"]?.ToString()?.ToLower();
            if (!string.IsNullOrEmpty(action))
            {
                // 这些 action 是只读操作，不会修改脚本
                if (action == "read" || action == "list" || action == "get_info" ||
                    action == "get" || action == "search" || action == "validate")
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 安全解析工具调用参数（JSON string → JObject）。
        /// </summary>
        /// <param name="arguments">JSON 格式的参数字符串</param>
        /// <returns>解析后的 JObject，解析失败时返回空 JObject</returns>
        private static JObject ParseToolArguments(string arguments)
        {
            if (string.IsNullOrWhiteSpace(arguments))
                return new JObject();

            try
            {
                return JObject.Parse(arguments);
            }
            catch
            {
                return new JObject();
            }
        }
    }
}
