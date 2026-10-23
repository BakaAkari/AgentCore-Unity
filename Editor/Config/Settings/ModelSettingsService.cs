using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Utils;
using Newtonsoft.Json.Linq;

namespace AgentCore.Editor.Config.Settings
{
    /// <summary>
    /// Provides LLM model endpoint operations used by the settings UI.
    /// </summary>
    public sealed class ModelSettingsService
    {
        /// <summary>
        /// Fetches available model identifiers from an OpenAI-compatible models endpoint.
        /// </summary>
        /// <param name="endpoint">The OpenAI-compatible API base endpoint.</param>
        /// <param name="apiKey">The API key used for authorization.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The fetched model identifiers.</returns>
        public async Task<List<string>> FetchModelsAsync(string endpoint, string apiKey, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                throw new InvalidOperationException("LLM endpoint is empty.");
            }

            var client = HttpClientFactory.GetClient();
            var url = $"{endpoint.TrimEnd('/')}/models";
            using var request = HttpClientFactory.CreateRequest(HttpMethod.Get, url, apiKey);
            using var response = await client.SendAsync(request, ct);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}");
            }

            return ParseModels(json);
        }

        /// <summary>
        /// Tests whether the OpenAI-compatible models endpoint can be reached successfully.
        /// </summary>
        /// <param name="endpoint">The OpenAI-compatible API base endpoint.</param>
        /// <param name="apiKey">The API key used for authorization.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>A user-facing test message.</returns>
        public async Task<string> TestConnectionAsync(string endpoint, string apiKey, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                throw new InvalidOperationException("LLM endpoint is empty.");
            }

            var client = HttpClientFactory.GetClient();
            var url = $"{endpoint.TrimEnd('/')}/models";
            using var request = HttpClientFactory.CreateRequest(HttpMethod.Get, url, apiKey);
            using var response = await client.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}");
            }

            return "[OK] Connected";
        }

        /// <summary>v1.17.0: Discover models using the profile protocol and authentication.</summary>
        public async Task<List<string>> FetchModelsAsync(ProviderProfile profile, string apiKey, CancellationToken ct = default)
        {
            using var request = BuildModelsRequest(profile, apiKey);
            using var response = await HttpClientFactory.GetClient().SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            return ParseModels(await response.Content.ReadAsStringAsync());
        }

        /// <summary>v1.17.0: Checks model-list connectivity, not generation capability.</summary>
        public async Task<string> TestConnectionAsync(ProviderProfile profile, string apiKey, CancellationToken ct = default)
        {
            try
            {
                var verified = await AgentCore.Editor.LLM.GatewayDiscovery.ResolveAsync(profile, apiKey, ct, force: true);
                return verified.cacheMetricsKnown
                    ? (verified.cacheHitObserved ? "settings.model.connection.hit" : "settings.model.connection.miss")
                    : "settings.model.connection.unknown";
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                AgentCore.Editor.LLM.GatewayDiscovery.ObserveFailure(profile);
                throw;
            }
        }

        internal static HttpRequestMessage BuildModelsRequest(ProviderProfile profile, string apiKey)
        {
            if (profile == null || string.IsNullOrWhiteSpace(profile.endpoint))
                throw new InvalidOperationException("LLM endpoint is empty.");
            bool anthropic = ActiveModelConfig.ResolveApiFormat(profile) == ApiFormats.Anthropic;
            var endpoint = profile.endpoint.Trim().TrimEnd('/');
            var url = anthropic
                ? AgentCore.Editor.LLM.Anthropic.AnthropicMessagesClient.BuildMessagesUrl(endpoint)
                : endpoint + "/models";
            if (anthropic) url = url.Substring(0, url.Length - "messages".Length) + "models";
            var headers = AgentCore.Editor.LLM.OpenAIChatTransporter.BuildAuthHeaders(apiKey,
                ActiveModelConfig.ResolveAuthScheme(profile) == AuthSchemes.XApiKey);
            if (anthropic) headers["anthropic-version"] = AgentCore.Editor.LLM.Anthropic.AnthropicMessagesClient.ApiVersion;
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            foreach (var header in headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            return request;
        }

        private static List<string> ParseModels(string json)
        {
            var models = new List<string>();
            var jobj = JsonHelper.ParseObject(json);
            if (jobj == null)
            {
                return models;
            }

            var data = jobj["data"] as JArray;
            if (data == null)
            {
                return models;
            }

            foreach (var item in data)
            {
                var id = item["id"]?.ToString();
                if (!string.IsNullOrEmpty(id))
                {
                    models.Add(id);
                }
            }

            models.Sort(StringComparer.OrdinalIgnoreCase);
            return models;
        }
    }
}
