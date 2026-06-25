using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

namespace AgentCore.Editor.LLM
{
    /// <summary>
    /// Extracts provider-specific structured reasoning deltas from OpenAI-compatible streaming chunks.
    /// </summary>
    public static class ReasoningFieldExtractor
    {
        private static readonly string[] ReasoningFieldNames =
        {
            "reasoning_content",
            "reasoning",
            "thinking",
            "thought",
            "reasoning_text"
        };

        /// <summary>
        /// Extracts reasoning text from the first choice delta in a raw streaming chunk.
        /// </summary>
        /// <param name="chunkJson">Raw streaming chunk JSON.</param>
        /// <returns>Combined structured reasoning text, or empty string when absent.</returns>
        public static string ExtractFromChunk(JObject chunkJson)
        {
            if (chunkJson == null) return string.Empty;

            var delta = chunkJson["choices"]?[0]?["delta"] as JObject;
            if (delta == null) return string.Empty;

            var parts = new List<string>();
            foreach (var fieldName in ReasoningFieldNames)
            {
                AppendTokenText(parts, delta[fieldName]);
            }

            AppendReasoningFromContentBlocks(parts, delta["content"]);
            return string.Concat(parts);
        }

        private static void AppendReasoningFromContentBlocks(List<string> parts, JToken contentToken)
        {
            if (parts == null || contentToken == null || contentToken.Type != JTokenType.Array)
                return;

            foreach (var blockToken in contentToken.Children())
            {
                var block = blockToken as JObject;
                if (block == null)
                    continue;

                var type = block.Value<string>("type");
                if (type != "thinking" && type != "reasoning")
                    continue;

                AppendTokenText(parts, block["text"]);
                AppendTokenText(parts, block["content"]);
            }
        }

        private static void AppendTokenText(List<string> parts, JToken token)
        {
            if (parts == null || token == null || token.Type == JTokenType.Null)
                return;

            switch (token.Type)
            {
                case JTokenType.String:
                    var text = token.Value<string>();
                    if (!string.IsNullOrEmpty(text))
                        parts.Add(text);
                    break;
                case JTokenType.Array:
                    var sb = new StringBuilder();
                    foreach (var child in token.Children())
                    {
                        if (child.Type == JTokenType.String)
                            sb.Append(child.Value<string>());
                    }
                    if (sb.Length > 0)
                        parts.Add(sb.ToString());
                    break;
            }
        }
    }
}
