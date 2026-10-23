using NUnit.Framework;
using Newtonsoft.Json.Linq;
using AgentCore.Editor.Tools;
using AgentCore.Editor.Tools.Infrastructure;

namespace AgentCore.Tests.Editor.Infrastructure
{
    /// <summary>
    /// ToolResponse 和 ToolResult 的单元测试。
    /// </summary>
    [TestFixture]
    public class ToolResponseTests
    {
        #region ToolResponse.Ok

        [Test]
        public void Ok_DefaultMessage_ReturnsSuccessResponse()
        {
            var response = ToolResponse.Ok();

            Assert.IsTrue(response.Success);
            Assert.AreEqual("Operation completed successfully.", response.Message);
            Assert.IsNull(response.Data);
            Assert.IsNull(response.Error);
        }

        [Test]
        public void Ok_CustomMessage_ReturnsSuccessWithMessage()
        {
            var response = ToolResponse.Ok("Custom message");

            Assert.IsTrue(response.Success);
            Assert.AreEqual("Custom message", response.Message);
        }

        #endregion

        #region ToolResponse.OkWithData

        [Test]
        public void OkWithData_AnonymousObject_SerializesToJToken()
        {
            var data = new { count = 5, name = "test" };
            var response = ToolResponse.OkWithData(data, "Found items");

            Assert.IsTrue(response.Success);
            Assert.AreEqual("Found items", response.Message);
            Assert.IsNotNull(response.Data);
            Assert.AreEqual(5, response.Data["count"].Value<int>());
            Assert.AreEqual("test", response.Data["name"].Value<string>());
        }

        [Test]
        public void OkWithData_String_WrapsAsJValue()
        {
            var response = ToolResponse.OkWithData("hello");

            Assert.IsTrue(response.Success);
            Assert.AreEqual(JTokenType.String, response.Data.Type);
            Assert.AreEqual("hello", response.Data.Value<string>());
        }

        [Test]
        public void OkWithData_JToken_PreservesDirectly()
        {
            var jobj = new JObject { ["key"] = "value" };
            var response = ToolResponse.OkWithData(jobj);

            Assert.IsTrue(response.Success);
            Assert.AreEqual("value", response.Data["key"].Value<string>());
        }

        [Test]
        public void OkWithData_NullMessage_UsesDefault()
        {
            var response = ToolResponse.OkWithData(new { x = 1 });

            Assert.AreEqual("Operation completed successfully.", response.Message);
        }

        #endregion

        #region ToolResponse.Fail

        [Test]
        public void Fail_ReturnsFailureResponse()
        {
            var response = ToolResponse.Fail("Something went wrong");

            Assert.IsFalse(response.Success);
            Assert.AreEqual("Something went wrong", response.Error);
            Assert.IsNull(response.Message);
            Assert.IsNull(response.Data);
        }

        #endregion

        #region ToolResponse.ToJson

        [Test]
        public void ToJson_SuccessResponse_ContainsSuccessAndMessage()
        {
            var response = ToolResponse.Ok("Done");
            var json = response.ToJson();
            var parsed = JObject.Parse(json);

            Assert.IsTrue(parsed["success"].Value<bool>());
            Assert.AreEqual("Done", parsed["message"].Value<string>());
            Assert.IsNull(parsed["error"]);
        }

        [Test]
        public void ToJson_FailResponse_ContainsError()
        {
            var response = ToolResponse.Fail("Error occurred");
            var json = response.ToJson();
            var parsed = JObject.Parse(json);

            Assert.IsFalse(parsed["success"].Value<bool>());
            Assert.AreEqual("Error occurred", parsed["error"].Value<string>());
            Assert.IsNull(parsed["message"]);
        }

        #endregion

        #region ToolResponse.ToToolResult

        [Test]
        public void ToToolResult_Success_CreatesOkToolResult()
        {
            var response = ToolResponse.Ok("Done");
            var result = response.ToToolResult(42.5);

            Assert.IsTrue(result.Success);
            Assert.IsNotNull(result.Output);
            Assert.IsNull(result.Error);
            Assert.AreEqual(42.5, result.ExecutionTimeMs, 0.001);
        }

        [Test]
        public void ToToolResult_Failure_CreatesFailToolResult()
        {
            var response = ToolResponse.Fail("Bad input");
            var result = response.ToToolResult(10.0);

            Assert.IsFalse(result.Success);
            Assert.IsNull(result.Output);
            Assert.AreEqual("Bad input", result.Error);
            Assert.AreEqual(10.0, result.ExecutionTimeMs, 0.001);
        }

        [Test]
        public void ToToolResult_FailureWithNullError_UsesUnknownError()
        {
            // ToolResponse.Fail always sets Error, but test the ToToolResult fallback
            var response = ToolResponse.Fail(null);
            var result = response.ToToolResult();

            Assert.IsFalse(result.Success);
            Assert.AreEqual("Unknown error", result.Error);
        }

        #endregion

        #region ToolResult.Ok / ToolResult.Fail

        [Test]
        public void ToolResult_Ok_SetsOutputAndSuccess()
        {
            var result = ToolResult.Ok("output text", 100.0);

            Assert.IsTrue(result.Success);
            Assert.AreEqual("output text", result.Output);
            Assert.IsNull(result.Error);
            Assert.AreEqual(100.0, result.ExecutionTimeMs, 0.001);
        }

        [Test]
        public void ToolResult_Fail_SetsErrorAndFailure()
        {
            var result = ToolResult.Fail("error text", 50.0);

            Assert.IsFalse(result.Success);
            Assert.IsNull(result.Output);
            Assert.AreEqual("error text", result.Error);
            Assert.AreEqual(50.0, result.ExecutionTimeMs, 0.001);
        }

        #endregion

        #region ToolResult.GetContentForLLM

        [Test]
        public void GetContentForLLM_Success_ReturnsOutput()
        {
            var result = ToolResult.Ok("hello world");

            Assert.AreEqual("hello world", result.GetContentForLLM());
        }

        [Test]
        public void GetContentForLLM_SuccessNullOutput_ReturnsEmpty()
        {
            var result = ToolResult.Ok(null);

            Assert.AreEqual(string.Empty, result.GetContentForLLM());
        }

        [Test]
        public void GetContentForLLM_Failure_ReturnsErrorPrefix()
        {
            var result = ToolResult.Fail("something broke");

            Assert.AreEqual("[Error] something broke", result.GetContentForLLM());
        }

        [Test]
        public void GetContentForLLM_FailureNullError_ReturnsUnknownError()
        {
            var result = ToolResult.Fail(null);

            Assert.AreEqual("[Error] Unknown error", result.GetContentForLLM());
        }

        #endregion

        #region ToolResponse.Partial

        private static ToolItemResult[] MixedItems()
        {
            return new[]
            {
                ToolItemResult.Succeeded("Assets/A.prefab"),
                ToolItemResult.Failed("Assets/B.prefab", "not found"),
                ToolItemResult.Succeeded("Assets/C.prefab")
            };
        }

        [Test]
        public void Partial_IsNotSuccess_AndMarkedPartial()
        {
            var response = ToolResponse.Partial("Replaced objects", MixedItems());

            Assert.IsFalse(response.Success);
            Assert.IsTrue(response.IsPartial);
            Assert.AreEqual(ToolResponse.StatusPartial, response.Status);
            Assert.AreEqual(3, response.Items.Count);
        }

        [Test]
        public void Partial_MessageAndError_SummarizeCountsAndFailures()
        {
            var response = ToolResponse.Partial("Replaced objects", MixedItems());

            StringAssert.StartsWith("PARTIAL:", response.Message);
            StringAssert.Contains("2 of 3 item(s) succeeded, 1 failed", response.Message);
            StringAssert.Contains("Assets/B.prefab (not found)", response.Error);
            StringAssert.DoesNotContain("Assets/A.prefab", response.Error);
        }

        [Test]
        public void Partial_ToJson_ContainsStatusItemsAndSuccessFalse()
        {
            var parsed = JObject.Parse(ToolResponse.Partial("op", MixedItems(), new { extra = 1 }).ToJson());

            Assert.IsFalse(parsed["success"].Value<bool>());
            Assert.AreEqual("partial", parsed["status"].Value<string>());
            Assert.AreEqual(1, parsed["data"]["extra"].Value<int>());
            var items = (JArray)parsed["items"];
            Assert.AreEqual(3, items.Count);
            Assert.IsTrue(items[0]["ok"].Value<bool>());
            Assert.IsNull(items[0]["error"]);
            Assert.IsFalse(items[1]["ok"].Value<bool>());
            Assert.AreEqual("not found", items[1]["error"].Value<string>());
        }

        [Test]
        public void Partial_ToToolResult_IsPartialWithFullJson()
        {
            var result = ToolResponse.Partial("op", MixedItems()).ToToolResult(7.0);

            Assert.IsTrue(result.Success);
            Assert.IsTrue(result.IsPartial);
            Assert.IsFalse(result.IsFullSuccess);
            Assert.IsNull(result.Error);
            Assert.AreEqual("partial", JObject.Parse(result.Output)["status"].Value<string>());
            Assert.AreEqual(7.0, result.ExecutionTimeMs, 0.001);
        }

        [Test]
        public void Partial_ManyFailures_ListIsTruncated()
        {
            var items = new System.Collections.Generic.List<ToolItemResult> { ToolItemResult.Succeeded("ok") };
            for (int i = 0; i < 15; i++) items.Add(ToolItemResult.Failed("t" + i, "e"));

            var response = ToolResponse.Partial(null, items);

            StringAssert.Contains("... and 5 more", response.Error);
            StringAssert.Contains("1 of 16 item(s) succeeded", response.Message);
        }

        [Test]
        public void Partial_NullItems_AreIgnored()
        {
            var response = ToolResponse.Partial("op", new[] { null, ToolItemResult.Failed("x", null) });

            Assert.AreEqual(1, response.Items.Count);
            Assert.AreEqual("Unknown error", response.Items[0].Error);
        }

        [Test]
        public void OkAndFail_ToJson_HaveNoStatusOrItems()
        {
            var ok = JObject.Parse(ToolResponse.OkWithData(new { x = 1 }).ToJson());
            var fail = JObject.Parse(ToolResponse.Fail("bad").ToJson());

            Assert.IsNull(ok["status"]);
            Assert.IsNull(ok["items"]);
            Assert.IsNull(fail["status"]);
            Assert.IsNull(fail["items"]);
            Assert.IsFalse(ToolResponse.Ok().IsPartial);
            Assert.IsFalse(ToolResponse.Fail("bad").IsPartial);
        }

        #endregion

        #region ToolResponse.FromItems

        [Test]
        public void FromItems_AllOk_ReturnsSuccessWithItems()
        {
            var response = ToolResponse.FromItems("Done", new[] { ToolItemResult.Succeeded("a"), ToolItemResult.Succeeded("b") });

            Assert.IsTrue(response.Success);
            Assert.IsFalse(response.IsPartial);
            Assert.AreEqual("Done", response.Message);
            Assert.AreEqual(2, response.Items.Count);
            Assert.IsTrue(response.ToToolResult().IsFullSuccess);
        }

        [Test]
        public void FromItems_AllFailed_ReturnsFailWithEveryReason()
        {
            var response = ToolResponse.FromItems("Replace", new[]
            {
                ToolItemResult.Failed("a", "missing"),
                ToolItemResult.Failed("b", "locked")
            });

            Assert.IsFalse(response.Success);
            Assert.IsFalse(response.IsPartial);
            StringAssert.Contains("0 of 2 item(s) succeeded", response.Error);
            StringAssert.Contains("a (missing)", response.Error);
            StringAssert.Contains("b (locked)", response.Error);

            var result = response.ToToolResult();
            Assert.IsFalse(result.Success);
            StringAssert.StartsWith("[Error] Replace", result.GetContentForLLM());
        }

        [Test]
        public void FromItems_Mixed_ReturnsPartial()
        {
            var response = ToolResponse.FromItems("op", MixedItems());

            Assert.IsTrue(response.IsPartial);
            Assert.IsFalse(response.Success);
        }

        [Test]
        public void FromItems_Empty_ReturnsSuccess()
        {
            var response = ToolResponse.FromItems("nothing to do", null);

            Assert.IsTrue(response.Success);
            Assert.AreEqual(0, response.Items.Count);
        }

        #endregion

        #region ToolResult.Partial

        [Test]
        public void ToolResult_Partial_GetContentForLLM_HasPartialPrefixThenOutput()
        {
            var result = ToolResult.Partial("{\"x\":1}");
            var content = result.GetContentForLLM();

            StringAssert.StartsWith(ToolResult.PartialPrefix, content);
            StringAssert.Contains("NOT a full success", content);
            StringAssert.EndsWith("{\"x\":1}", content);
        }

        [Test]
        public void ToolResult_Partial_NullOutput_ReturnsPrefixOnly()
        {
            Assert.AreEqual(ToolResult.PartialPrefix, ToolResult.Partial(null).GetContentForLLM());
        }

        [Test]
        public void ToolResult_OkAndFail_AreNotPartial()
        {
            Assert.IsFalse(ToolResult.Ok("x").IsPartial);
            Assert.IsTrue(ToolResult.Ok("x").IsFullSuccess);
            Assert.IsFalse(ToolResult.Fail("x").IsPartial);
            Assert.IsFalse(ToolResult.Fail("x").IsFullSuccess);
        }

        [Test]
        public void ToolResult_Partial_ToString_ContainsPartial()
        {
            StringAssert.Contains("Partial", ToolResult.Partial("out", 3.2).ToString());
        }

        #endregion

        #region ToolResult.ToString

        [Test]
        public void ToString_Success_ContainsOkAndTime()
        {
            var result = ToolResult.Ok("short output", 12.3);
            var str = result.ToString();

            StringAssert.Contains("Ok", str);
            StringAssert.Contains("12.3", str);
        }

        [Test]
        public void ToString_Failure_ContainsFailAndTime()
        {
            var result = ToolResult.Fail("error msg", 5.7);
            var str = result.ToString();

            StringAssert.Contains("Fail", str);
            StringAssert.Contains("5.7", str);
        }

        #endregion
    }
}
