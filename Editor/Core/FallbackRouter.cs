using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Config;
using AgentCore.Editor.LLM;
using UnityEngine;

namespace AgentCore.Editor.Core
{
    /// <summary>
    /// 降级路由器 - 当主端点失败时自动重试
    /// 当前实现为简单的重试逻辑，未来可扩展为多端点路由
    /// </summary>
    public class FallbackRouter
    {
        private readonly AgentCoreSettings _settings;

        /// <summary>
        /// 最大重试次数
        /// </summary>
        public int MaxRetries { get; set; } = 2;

        /// <summary>
        /// 重试间隔（毫秒）
        /// </summary>
        public int RetryDelayMs { get; set; } = 1000;

        /// <summary>
        /// 上次错误信息
        /// </summary>
        public string LastError { get; private set; }

        public FallbackRouter(AgentCoreSettings settings)
        {
            _settings = settings;
        }

        /// <summary>
        /// 带重试的流式请求执行。
        /// 适配 ILLMClient.ChatCompletionStreamAsync 的签名。
        /// </summary>
        /// <param name="client">LLM 客户端</param>
        /// <param name="messages">对话消息列表</param>
        /// <param name="onChunk">流式 chunk 回调</param>
        /// <param name="tools">工具定义列表（可选）</param>
        /// <param name="ct">取消令牌</param>
        /// <param name="onStatusUpdate">状态更新回调（可选）</param>
        /// <returns>完整的 assistant ChatMessage</returns>
        public async Task<ChatMessage> ExecuteStreamWithRetryAsync(
            ILLMClient client,
            List<ChatMessage> messages,
            Action<StreamChunk> onChunk,
            List<ToolDefinition> tools = null,
            CancellationToken ct = default,
            Action<string> onStatusUpdate = null)
        {
            Exception lastException = null;

            for (int attempt = 0; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    if (attempt > 0)
                    {
                        onStatusUpdate?.Invoke($"Retry attempt {attempt}/{MaxRetries}...");
                        Debug.Log($"[AgentCore] Retry attempt {attempt}/{MaxRetries}");
                        await Task.Delay(RetryDelayMs * attempt, ct); // 递增延迟
                    }

                    var result = await client.ChatCompletionStreamAsync(messages, onChunk, tools, ct);
                    LastError = null;
                    return result;
                }
                catch (TaskCanceledException)
                {
                    // 用户取消，不重试
                    throw;
                }
                catch (OperationCanceledException)
                {
                    // 用户取消，不重试
                    throw;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    LastError = ex.Message;
                    Debug.LogWarning($"[AgentCore] LLM request failed (attempt {attempt + 1}): {ex.Message}");

                    // 判断是否值得重试
                    if (!IsRetryableError(ex))
                    {
                        Debug.LogError($"[AgentCore] Non-retryable error, giving up: {ex.Message}");
                        break;
                    }
                }
            }

            // 所有重试都失败了
            throw new Exception(
                $"LLM request failed after {MaxRetries + 1} attempts. Last error: {lastException?.Message}",
                lastException);
        }

        /// <summary>
        /// 带重试的非流式请求执行。
        /// 适配 ILLMClient.ChatCompletionAsync 的签名。
        /// </summary>
        /// <param name="client">LLM 客户端</param>
        /// <param name="messages">对话消息列表</param>
        /// <param name="tools">工具定义列表（可选）</param>
        /// <param name="ct">取消令牌</param>
        /// <param name="onStatusUpdate">状态更新回调（可选）</param>
        /// <returns>完整的 ChatCompletionResponse</returns>
        public async Task<ChatCompletionResponse> ExecuteWithRetryAsync(
            ILLMClient client,
            List<ChatMessage> messages,
            List<ToolDefinition> tools = null,
            CancellationToken ct = default,
            Action<string> onStatusUpdate = null)
        {
            Exception lastException = null;

            for (int attempt = 0; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    if (attempt > 0)
                    {
                        onStatusUpdate?.Invoke($"Retry attempt {attempt}/{MaxRetries}...");
                        Debug.Log($"[AgentCore] Retry attempt {attempt}/{MaxRetries}");
                        await Task.Delay(RetryDelayMs * attempt, ct);
                    }

                    return await client.ChatCompletionAsync(messages, tools, ct);
                }
                catch (TaskCanceledException)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    LastError = ex.Message;
                    Debug.LogWarning($"[AgentCore] LLM request failed (attempt {attempt + 1}): {ex.Message}");

                    if (!IsRetryableError(ex))
                    {
                        break;
                    }
                }
            }

            throw new Exception(
                $"LLM request failed after {MaxRetries + 1} attempts. Last error: {lastException?.Message}",
                lastException);
        }

        /// <summary>
        /// 判断错误是否可重试
        /// </summary>
        private bool IsRetryableError(Exception ex)
        {
            var message = ex.Message.ToLowerInvariant();

            // 网络超时、连接错误 -> 可重试
            if (message.Contains("timeout") || message.Contains("timed out"))
                return true;
            if (message.Contains("connection") && (message.Contains("refused") || message.Contains("reset")))
                return true;
            if (message.Contains("502") || message.Contains("503") || message.Contains("504"))
                return true;
            if (message.Contains("rate limit") || message.Contains("429"))
                return true;

            // 认证错误、参数错误 -> 不可重试
            if (message.Contains("401") || message.Contains("403") || message.Contains("unauthorized"))
                return false;
            if (message.Contains("400") || message.Contains("bad request"))
                return false;
            if (message.Contains("404") || message.Contains("not found"))
                return false;

            // 默认可重试
            return true;
        }
    }
}
