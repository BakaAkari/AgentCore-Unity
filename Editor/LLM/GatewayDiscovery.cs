using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Config;
using AgentCore.Editor.Utils;
using AgentCore.Editor.LLM.Anthropic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentCore.Editor.LLM
{
    /// <summary>v1.17.0: Verify unknown gateways using synthetic generation, never project context.</summary>
    public static class GatewayDiscovery
    {
        private static readonly SemaphoreSlim Gate = new(1, 1);
        private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false });
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ProviderProfile> Verified = new();

        public static string Identity(ProviderProfile p) => p == null ? "" :
            string.Join("\n", p.endpoint?.Trim().TrimEnd('/'), p.modelName, p.apiFormat, p.authScheme, p.dialect);

        public static bool IsVerified(ProviderProfile p) => p != null && p.verifiedConnection == Identity(p)
            && (p.verifiedFormat == ApiFormats.OpenAI || p.verifiedFormat == ApiFormats.Anthropic)
            && (p.verifiedAuth == AuthSchemes.Bearer || p.verifiedAuth == AuthSchemes.XApiKey);

        private static string CredentialHash(string key)
        {
            using var sha = SHA256.Create();
            return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(key ?? "")));
        }

        public static bool IsKnownEndpoint(ProviderProfile p)
        {
            if (!Uri.TryCreate(p?.endpoint, UriKind.Absolute, out var uri)) return false;
            return new[] { "api.anthropic.com", "api.openai.com", "api.deepseek.com", "openrouter.ai" }
                .Contains(uri.Host.ToLowerInvariant());
        }

        public static async Task<ProviderProfile> ResolveAsync(ProviderProfile profile, string key, CancellationToken ct, bool force = false)
        {
            if (profile == null) throw new InvalidOperationException("No model profile configured.");
            var p = JsonConvert.DeserializeObject<ProviderProfile>(JsonConvert.SerializeObject(profile));
            var identity = Identity(p);
            var credential = CredentialHash(key);
            if (!force && IsVerified(p) && p.verifiedCredential == credential) return p;
            if (p.verifiedCredential != credential) p.verifiedConnection = null;
            if (!force && IsKnownEndpoint(p)) return p;
            if (string.IsNullOrWhiteSpace(p.modelName)) throw new InvalidOperationException("Select a model before verifying the connection.");
            if (!Uri.TryCreate(p.endpoint, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                throw new InvalidOperationException("Enter a valid HTTP(S) endpoint.");
            var cacheKey = p.id + "\n" + identity + "\n" + credential;
            await Gate.WaitAsync(ct);
            try
            {
                if (!force && Verified.TryGetValue(cacheKey, out var remembered))
                {
                    CopyVerification(remembered, p);
                    return p;
                }
                bool manualFormat = p.apiFormat == ApiFormats.OpenAI || p.apiFormat == ApiFormats.Anthropic;
                bool manualAuth = p.authScheme == AuthSchemes.Bearer || p.authScheme == AuthSchemes.XApiKey;
                bool preferAnthropic = (p.modelName ?? "").IndexOf("claude", StringComparison.OrdinalIgnoreCase) >= 0
                    || ActiveModelConfig.ResolveApiFormat(p) == ApiFormats.Anthropic;
                var formats = manualFormat ? new[] { p.apiFormat } : preferAnthropic
                    ? new[] { ApiFormats.Anthropic, ApiFormats.OpenAI } : new[] { ApiFormats.OpenAI, ApiFormats.Anthropic };
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(60));
                foreach (var format in formats)
                {
                    var auths = manualAuth ? new[] { p.authScheme } : format == ApiFormats.Anthropic && uri.Host == "api.anthropic.com"
                        ? new[] { AuthSchemes.XApiKey, AuthSchemes.Bearer } : new[] { AuthSchemes.Bearer, AuthSchemes.XApiKey };
                    foreach (var auth in auths)
                    {
                        var body = new JObject { ["model"] = p.modelName, ["stream"] = false,
                            ["messages"] = new JArray(new JObject { ["role"] = "user", ["content"] = "Connection check. Reply OK." }) };
                        if (format == ApiFormats.Anthropic) body["max_tokens"] = 128;
                        else body[(p.modelName.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase) || p.modelName.StartsWith("o", StringComparison.OrdinalIgnoreCase)) ? "max_completion_tokens" : "max_tokens"] = 128;
                        var url = format == ApiFormats.Anthropic ? AnthropicMessagesClient.BuildMessagesUrl(p.endpoint)
                            : OpenAIChatTransporter.BuildChatCompletionsUrl(p.endpoint);
                        using var request = new HttpRequestMessage(HttpMethod.Post, url);
                        foreach (var header in OpenAIChatTransporter.BuildAuthHeaders(key, auth == AuthSchemes.XApiKey))
                            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                        if (format == ApiFormats.Anthropic) request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicMessagesClient.ApiVersion);
                        request.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
                        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                        attempt.CancelAfter(TimeSpan.FromSeconds(15));
                        using var response = await Client.SendAsync(request, attempt.Token);
                        if (!response.IsSuccessStatusCode)
                        {
                            int status = (int)response.StatusCode;
                            if (status == 429 || status >= 500) throw new HttpRequestException($"Connection not verified: service returned HTTP {status}. Retry later.");
                            continue;
                        }
                        JObject json;
                        try { json = JObject.Parse(await response.Content.ReadAsStringAsync()); }
                        catch (JsonException) { continue; }
                        if (!IsGenerationResponse(json, format)) continue;
                        p.connectionFailed = false;
                        p.verifiedConnection = identity; p.verifiedCredential = credential;
                        p.verifiedFormat = format; p.verifiedAuth = auth;
                        var usage = format == ApiFormats.Anthropic ? LlmUsage.FromAnthropic(json["usage"] as JObject, p.modelName)
                            : LlmUsage.FromOpenAI(json["usage"] as JObject, p.modelName);
                        p.cacheMetricsKnown = usage?.CacheMetricsKnown == true;
                        p.cacheHitObserved = usage?.CacheRead > 0;
                        Verified[cacheKey] = p;
                        Persist(p);
                        return p;
                    }
                }
                throw new InvalidOperationException("Connection not verified. Check the endpoint, model and API key, or select API Format manually.");
            }
            finally { Gate.Release(); }
        }

        internal static bool IsGenerationResponse(JObject body, string format) => body?["error"] == null &&
            (format == ApiFormats.Anthropic
                ? body?.Value<string>("type") == "message" && body.Value<string>("role") == "assistant" && body["content"] is JArray
                : body?["choices"] is JArray choices && choices.Count > 0 && choices[0]["message"] is JObject message && message.Value<string>("role") == "assistant");

        private static void CopyVerification(ProviderProfile from, ProviderProfile to)
        {
            to.verifiedConnection = from.verifiedConnection; to.verifiedCredential = from.verifiedCredential;
            to.verifiedFormat = from.verifiedFormat; to.verifiedAuth = from.verifiedAuth;
            to.connectionFailed = from.connectionFailed;
            to.cacheMetricsKnown = from.cacheMetricsKnown; to.cacheHitObserved = from.cacheHitObserved;
        }

        private static void Persist(ProviderProfile p)
        {
            AsyncHelper.RunOnMainThread(() =>
            {
                var store = AgentCoreProviderProfiles.instance;
                var current = store.FindById(p.id);
                if (current == null || Identity(current) != p.verifiedConnection
                    || CredentialHash(SecureKeyStorage.GetProfileApiKey(p.id)) != p.verifiedCredential) return;
                store.UpdateProfile(p.id, target => CopyVerification(p, target));
            });
        }

        public static void ObserveFailure(ProviderProfile profile)
        {
            if (profile == null) return;
            var id = profile.id;
            var identity = Identity(profile);
            AsyncHelper.RunOnMainThread(() =>
            {
                var store = AgentCoreProviderProfiles.instance;
                var current = store.FindById(id);
                if (current != null && Identity(current) == identity)
                    store.UpdateProfile(id, p => p.connectionFailed = true);
            });
        }

        public static void ObserveUsage(ProviderProfile profile, LlmUsage usage)
        {
            if (profile == null) return;
            var p = JsonConvert.DeserializeObject<ProviderProfile>(JsonConvert.SerializeObject(profile));
            if (!IsVerified(p))
            {
                p.verifiedFormat = ActiveModelConfig.ResolveApiFormat(profile);
                p.verifiedAuth = ActiveModelConfig.ResolveAuthScheme(profile);
                p.verifiedConnection = Identity(profile);
                p.verifiedCredential = CredentialHash(ActiveModelConfig.ApiKey);
            }
            p.connectionFailed = false;
            p.cacheMetricsKnown = usage?.CacheMetricsKnown == true;
            p.cacheHitObserved = usage?.CacheRead > 0;
            Verified[p.id + "\n" + Identity(p) + "\n" + p.verifiedCredential] = p;
            Persist(p);
        }
    }
}
