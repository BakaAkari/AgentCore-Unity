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
