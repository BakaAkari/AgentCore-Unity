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
