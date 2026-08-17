using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Utils;

namespace AgentCore.Editor.LLM
{
    /// <summary>
    /// OpenAI 兼容 Chat Completions 的底层 HTTP 发送端（v1.15.0 抽象）。
    /// <para>
    /// 极小职责：只负责「把一段已序列化好的 JSON body，POST 到指定 url，并返回原始响应」，
    /// 供<b>多个调用方</b>复用，且<b>不绑定任何配置来源</b>（url / apiKey / body 均由调用方显式传入）。
    /// </para>
    /// <para>
    /// 复用方：
    /// <list type="bullet">
    /// <item><b>主模型</b> <c>OpenAICompatibleClient</c> —— 委托其非流式/流式 POST（本类 url 参数与主模型调用点一致，
    ///     均为完整 /chat/completions url），自身保留 RequestEnrichment(reasoning)/Pruning/tool 清洗等主模型专属策略。</item>
    /// <item><b>视觉模型</b> <c>VisionLLMClient</c> —— 构建多模态 body 后经本类 POST + 解析，独立连接参数。</item>
    /// </list>
    /// 本类刻意<b>不</b>包含：RequestEnrichment(reasoning注入)、RequestPruningRegistry(400自学)、
    /// SanitizeMessageToolCalls(tool清洗)、多模态 body 构建 —— 这些都是各调用方的策略层，不是「发送」职责。
    /// </para>
    /// </summary>
    public static class OpenAIChatTransporter
    {
        /// <summary>
        /// 发送非流式 Chat Completions 请求。
        /// </summary>
        /// <param name="url">完整请求 url（如 <c>http://host/v1/chat/completions</c>）</param>
        /// <param name="apiKey">Bearer 鉴权 key（可为空）</param>
        /// <param name="jsonBody">已序列化好的 JSON body</param>
        /// <param name="ct">取消令牌；调用方负责 Dispose 返回的 response</param>
        /// <returns>(response, responseBody) —— 调用方负责 Dispose response</returns>
        public static async Task<(HttpResponseMessage response, string responseBody)> PostAsync(
            string url, string apiKey, string jsonBody, CancellationToken ct)
        {
            var client = HttpClientFactory.GetClient();
            using var httpRequest = HttpClientFactory.CreateRequest(HttpMethod.Post, url, apiKey);
            httpRequest.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            var response = await client.SendAsync(httpRequest, ct);
            var responseBody = await response.Content.ReadAsStringAsync();
            return (response, responseBody);
        }

        /// <summary>
        /// 发送流式 Chat Completions 请求（ResponseHeadersRead 模式，供调用方逐 chunk 读流）。
        /// </summary>
        /// <param name="url">完整请求 url</param>
        /// <param name="apiKey">Bearer 鉴权 key（可为空）</param>
        /// <param name="jsonBody">已序列化好的 JSON body</param>
        /// <param name="ct">取消令牌；调用方负责 Dispose 返回的 response</param>
        public static async Task<HttpResponseMessage> PostStreamAsync(
            string url, string apiKey, string jsonBody, CancellationToken ct)
        {
            var client = HttpClientFactory.GetClient();
            using var httpRequest = HttpClientFactory.CreateRequest(HttpMethod.Post, url, apiKey);
            httpRequest.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            return await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        }

        /// <summary>
        /// 由 base URL 拼接 /chat/completions 完整 url（去除尾部 '/'）。
        /// </summary>
        public static string BuildChatCompletionsUrl(string endpoint)
            => endpoint.TrimEnd('/') + "/chat/completions";
    }
}
