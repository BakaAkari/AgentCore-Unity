using System;
using System.Net.Http;
using System.Net.Http.Headers;

namespace AgentCore.Editor.Utils
{
    /// <summary>
    /// 单例 HttpClient 工厂。
    /// HttpClient 应该被复用而非每次请求创建新实例，避免 socket 泄漏。
    /// </summary>
    public static class HttpClientFactory
    {
        private static HttpClient _sharedClient;
        private static readonly object _lock = new();

        /// <summary>
        /// 默认请求超时时间（秒）。
        /// LLM 流式请求可能持续较长时间，设置较大的超时值。
        /// </summary>
        public const int DefaultTimeoutSeconds = 300;

        /// <summary>
        /// 获取共享的 HttpClient 实例。
        /// 线程安全，首次调用时创建。
        /// </summary>
        public static HttpClient GetClient()
        {
            if (_sharedClient != null) return _sharedClient;

            lock (_lock)
            {
                if (_sharedClient != null) return _sharedClient;

                _sharedClient = new HttpClient
                {
                    Timeout = TimeSpan.FromSeconds(DefaultTimeoutSeconds)
                };

                _sharedClient.DefaultRequestHeaders.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));

                return _sharedClient;
            }
        }

        /// <summary>
        /// 创建一个配置了 API Key 的请求消息。
        /// </summary>
        /// <param name="method">HTTP 方法</param>
        /// <param name="url">请求 URL</param>
        /// <param name="apiKey">API Key（可选，为空则不添加 Authorization 头）</param>
        /// <returns>配置好的 HttpRequestMessage</returns>
        public static HttpRequestMessage CreateRequest(HttpMethod method, string url, string apiKey = null)
        {
            var request = new HttpRequestMessage(method, url);

            if (!string.IsNullOrEmpty(apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            return request;
        }

        /// <summary>
        /// 重置共享客户端（用于配置变更后重新创建）。
        /// </summary>
        public static void Reset()
        {
            lock (_lock)
            {
                _sharedClient?.Dispose();
                _sharedClient = null;
            }
        }
    }
}
