using System.Collections.Generic;
using NUnit.Framework;
using AgentCore.Editor.Core;
using AgentCore.Editor.LLM;

namespace AgentCore.Tests.Editor.Core
{
    /// <summary>
    /// TokenCounter 的单元测试。
    /// </summary>
    [TestFixture]
    public class TokenCounterTests
    {
        #region EstimateTokens — 文本估算

        [Test]
        public void EstimateTokens_NullInput_ReturnsZero()
        {
            Assert.AreEqual(0, TokenCounter.EstimateTokens(null));
        }

        [Test]
        public void EstimateTokens_EmptyString_ReturnsZero()
        {
            Assert.AreEqual(0, TokenCounter.EstimateTokens(""));
        }

        [Test]
        public void EstimateTokens_ShortEnglish_ReturnsAtLeastOne()
        {
            var tokens = TokenCounter.EstimateTokens("hi");
            Assert.GreaterOrEqual(tokens, 1);
        }

        [Test]
        public void EstimateTokens_LongerEnglish_ApproximatelyOnePerFourChars()
        {
            // "hello world" = 11 chars, expect ~3 tokens (11/4 rounded up)
            var tokens = TokenCounter.EstimateTokens("hello world");
            Assert.GreaterOrEqual(tokens, 2);
            Assert.LessOrEqual(tokens, 5);
        }

        [Test]
        public void EstimateTokens_CJKCharacters_HigherWeight()
        {
            // 5 CJK characters should produce more tokens than 5 ASCII characters
            var cjkTokens = TokenCounter.EstimateTokens("你好世界啊");
            var asciiTokens = TokenCounter.EstimateTokens("abcde");

            Assert.Greater(cjkTokens, asciiTokens);
        }

        [Test]
        public void EstimateTokens_CJK_EachCharAboutTwoTokens()
        {
            // 3 CJK chars → ~6 tokens
            var tokens = TokenCounter.EstimateTokens("你好吗");
            Assert.AreEqual(6, tokens);
        }

        [Test]
        public void EstimateTokens_MixedContent_CombinesBothRules()
        {
            // "Hello你好" = 5 ASCII + 2 CJK
            // Expected: (5+3)/4 + 2*2 = 2 + 4 = 6
            var tokens = TokenCounter.EstimateTokens("Hello你好");
            Assert.GreaterOrEqual(tokens, 5);
            Assert.LessOrEqual(tokens, 7);
        }

        #endregion

        #region EstimateMessageTokens — 消息估算

        [Test]
        public void EstimateMessageTokens_NullMessage_ReturnsZero()
        {
            Assert.AreEqual(0, TokenCounter.EstimateMessageTokens(null));
        }

        [Test]
        public void EstimateMessageTokens_EmptyContent_ReturnsOverheadOnly()
        {
            var msg = ChatMessage.User("");
            var tokens = TokenCounter.EstimateMessageTokens(msg);

            // Should include message overhead (4) but no content tokens
            Assert.AreEqual(4, tokens);
        }

        [Test]
        public void EstimateMessageTokens_WithContent_IncludesOverhead()
        {
            var msg = ChatMessage.User("hello world");
            var tokens = TokenCounter.EstimateMessageTokens(msg);

            // overhead (4) + content tokens (>0)
            Assert.Greater(tokens, 4);
        }

        [Test]
        public void EstimateMessageTokens_WithToolCalls_IncludesToolCallTokens()
        {
            var msg = new ChatMessage
            {
                Role = "assistant",
                Content = "I'll help",
                ToolCalls = new List<ToolCall>
                {
                    new ToolCall
                    {
                        Id = "call_123",
                        Type = "function",
                        Function = new FunctionCall
                        {
                            Name = "manage_script",
                            Arguments = "{\"action\":\"create\",\"path\":\"test.cs\"}"
                        }
                    }
                }
            };

            var tokens = TokenCounter.EstimateMessageTokens(msg);

            // Should be more than just content + overhead
            var contentOnlyMsg = ChatMessage.Assistant("I'll help");
            var contentOnlyTokens = TokenCounter.EstimateMessageTokens(contentOnlyMsg);

            Assert.Greater(tokens, contentOnlyTokens);
        }

        [Test]
        public void EstimateMessageTokens_ToolMessage_IncludesToolCallId()
        {
            var msg = ChatMessage.Tool("call_abc123", "result content");
            var tokens = TokenCounter.EstimateMessageTokens(msg);

            // overhead + content + tool_call_id
            Assert.Greater(tokens, 4);
        }

        #endregion

        #region EstimateConversationTokens — 对话估算

        [Test]
        public void EstimateConversationTokens_NullList_ReturnsZero()
        {
            Assert.AreEqual(0, TokenCounter.EstimateConversationTokens(null));
        }

        [Test]
        public void EstimateConversationTokens_EmptyList_ReturnsZero()
        {
            Assert.AreEqual(0, TokenCounter.EstimateConversationTokens(new List<ChatMessage>()));
        }

        [Test]
        public void EstimateConversationTokens_MultipleMessages_SumsWithOverhead()
        {
            var messages = new List<ChatMessage>
            {
                ChatMessage.System("You are a helpful assistant."),
                ChatMessage.User("Hello"),
                ChatMessage.Assistant("Hi there!")
            };

            var total = TokenCounter.EstimateConversationTokens(messages);

            // Should be > sum of individual messages (has conversation overhead of 3)
            Assert.Greater(total, 0);

            // Verify it includes the conversation overhead (3)
            int sumIndividual = 0;
            foreach (var msg in messages)
            {
                sumIndividual += TokenCounter.EstimateMessageTokens(msg);
            }
            Assert.AreEqual(sumIndividual + 3, total);
        }

        #endregion
    }
}
