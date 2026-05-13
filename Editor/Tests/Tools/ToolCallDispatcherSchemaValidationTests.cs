using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.LLM;
using AgentCore.Editor.Tools;
using AgentCore.Editor.Tools.Infrastructure;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace AgentCore.Tests.Editor.Tools
{
    /// <summary>
    /// ToolCallDispatcher JSON Schema 参数预校验测试。
    /// 验证 ToolParameterValidator 集成到分发流程后的行为。
    /// </summary>
    [TestFixture]
    public class ToolCallDispatcherSchemaValidationTests
    {
        #region 测试辅助

        /// <summary>用于测试的 fake 工具，记录是否被执行</summary>
        private class FakeTool : IAgentTool
        {
            public bool WasExecuted { get; private set; }
            public ToolMetadata Metadata { get; }

            public FakeTool(string name, JObject schema)
            {
                Metadata = new ToolMetadata(
                    name: name,
                    description: "A fake tool for testing",
                    category: "Test",
                    parametersSchema: schema,
                    requiresMainThread: false
                );
            }

            public Task<ToolResult> ExecuteAsync(JObject parameters, CancellationToken cancellationToken = default)
            {
                WasExecuted = true;
                return Task.FromResult(ToolResult.Ok("executed"));
            }
        }

        private ToolCallDispatcher _dispatcher;
        private FakeTool _fakeTool;

        private static readonly JObject TestSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": { ""type"": ""string"", ""enum"": [""read"", ""write""] },
                ""count"": { ""type"": ""integer"" },
                ""ratio"": { ""type"": ""number"" },
                ""verbose"": { ""type"": ""boolean"" },
                ""tags"": { ""type"": ""array"" },
                ""options"": { ""type"": ""object"" }
            },
            ""required"": [""action""]
        }");

        [SetUp]
        public void SetUp()
        {
            ToolRegistry.Instance.Clear();
            _fakeTool = new FakeTool("test_tool", TestSchema);
            ToolRegistry.Instance.Register(_fakeTool);
            _dispatcher = new ToolCallDispatcher(ToolRegistry.Instance);
        }

        [TearDown]
        public void TearDown()
        {
            ToolRegistry.Instance.Clear();
        }

        private static ToolCall MakeToolCall(string name, string arguments)
        {
            return new ToolCall
            {
                Id = "call_test_001",
                Type = "function",
                Function = new FunctionCall
                {
                    Name = name,
                    Arguments = arguments
                }
            };
        }

        #endregion

        #region 未知工具 & 非法 JSON（已有行为保持不变）

        [Test]
        public async Task UnknownTool_ReturnsError()
        {
            var tc = MakeToolCall("nonexistent_tool", @"{""action"":""read""}");
            var result = await _dispatcher.DispatchAsync(tc);

            Assert.IsFalse(result.Result.Success);
            Assert.That(result.Result.Error, Does.Contain("Unknown tool"));
            Assert.IsFalse(_fakeTool.WasExecuted);
        }

        [Test]
        public async Task InvalidJson_ReturnsError()
        {
            var tc = MakeToolCall("test_tool", "not valid json {{{");
            var result = await _dispatcher.DispatchAsync(tc);

            Assert.IsFalse(result.Result.Success);
            Assert.That(result.Result.Error, Does.Contain("Invalid JSON"));
            Assert.IsFalse(_fakeTool.WasExecuted);
        }

        #endregion

        #region Required 字段校验

        [Test]
        public async Task MissingRequiredField_DoesNotExecuteTool()
        {
            // action 是 required 但未提供
            var tc = MakeToolCall("test_tool", @"{""count"": 5}");
            var result = await _dispatcher.DispatchAsync(tc);

            Assert.IsFalse(result.Result.Success);
            Assert.That(result.Result.Error, Does.Contain("action"));
            Assert.That(result.Result.Error, Does.Contain("required"));
            Assert.IsFalse(_fakeTool.WasExecuted);
        }

        #endregion

        #region 类型校验

        [Test]
        public async Task StringTypeError_DoesNotExecuteTool()
        {
            // action 应为 string，传入 integer
            var tc = MakeToolCall("test_tool", @"{""action"": 123}");
            var result = await _dispatcher.DispatchAsync(tc);

            Assert.IsFalse(result.Result.Success);
            Assert.That(result.Result.Error, Does.Contain("action"));
            Assert.That(result.Result.Error, Does.Contain("string"));
            Assert.IsFalse(_fakeTool.WasExecuted);
        }

        [Test]
        public async Task IntegerTypeError_DoesNotExecuteTool()
        {
            // count 应为 integer，传入 string
            var tc = MakeToolCall("test_tool", @"{""action"": ""read"", ""count"": ""five""}");
            var result = await _dispatcher.DispatchAsync(tc);

            Assert.IsFalse(result.Result.Success);
            Assert.That(result.Result.Error, Does.Contain("count"));
            Assert.That(result.Result.Error, Does.Contain("integer"));
            Assert.IsFalse(_fakeTool.WasExecuted);
        }

        [Test]
        public async Task NumberAcceptsInteger()
        {
            // ratio 是 number 类型，应接受 integer
            var tc = MakeToolCall("test_tool", @"{""action"": ""read"", ""ratio"": 42}");
            var result = await _dispatcher.DispatchAsync(tc);

            Assert.IsTrue(result.Result.Success);
            Assert.IsTrue(_fakeTool.WasExecuted);
        }

        [Test]
        public async Task NumberAcceptsFloat()
        {
            // ratio 是 number 类型，应接受 float
            var tc = MakeToolCall("test_tool", @"{""action"": ""read"", ""ratio"": 3.14}");
            var result = await _dispatcher.DispatchAsync(tc);

            Assert.IsTrue(result.Result.Success);
            Assert.IsTrue(_fakeTool.WasExecuted);
        }

        [Test]
        public async Task NumberTypeError_DoesNotExecuteTool()
        {
            // ratio 应为 number，传入 string
            var tc = MakeToolCall("test_tool", @"{""action"": ""read"", ""ratio"": ""high""}");
            var result = await _dispatcher.DispatchAsync(tc);

            Assert.IsFalse(result.Result.Success);
            Assert.That(result.Result.Error, Does.Contain("ratio"));
            Assert.That(result.Result.Error, Does.Contain("number"));
            Assert.IsFalse(_fakeTool.WasExecuted);
        }

        [Test]
        public async Task BooleanTypeError_DoesNotExecuteTool()
        {
            // verbose 应为 boolean，传入 string
            var tc = MakeToolCall("test_tool", @"{""action"": ""read"", ""verbose"": ""yes""}");
            var result = await _dispatcher.DispatchAsync(tc);

            Assert.IsFalse(result.Result.Success);
            Assert.That(result.Result.Error, Does.Contain("verbose"));
            Assert.That(result.Result.Error, Does.Contain("boolean"));
            Assert.IsFalse(_fakeTool.WasExecuted);
        }

        [Test]
        public async Task ArrayTypeError_DoesNotExecuteTool()
        {
            // tags 应为 array，传入 string
            var tc = MakeToolCall("test_tool", @"{""action"": ""read"", ""tags"": ""tag1,tag2""}");
            var result = await _dispatcher.DispatchAsync(tc);

            Assert.IsFalse(result.Result.Success);
            Assert.That(result.Result.Error, Does.Contain("tags"));
            Assert.That(result.Result.Error, Does.Contain("array"));
            Assert.IsFalse(_fakeTool.WasExecuted);
        }

        [Test]
        public async Task ObjectTypeError_DoesNotExecuteTool()
        {
            // options 应为 object，传入 string
            var tc = MakeToolCall("test_tool", @"{""action"": ""read"", ""options"": ""none""}");
            var result = await _dispatcher.DispatchAsync(tc);

            Assert.IsFalse(result.Result.Success);
            Assert.That(result.Result.Error, Does.Contain("options"));
            Assert.That(result.Result.Error, Does.Contain("object"));
            Assert.IsFalse(_fakeTool.WasExecuted);
        }

        #endregion

        #region Enum 校验

        [Test]
        public async Task EnumMismatch_DoesNotExecuteTool()
        {
            // action 的 enum 是 [read, write]，传入 delete
            var tc = MakeToolCall("test_tool", @"{""action"": ""delete""}");
            var result = await _dispatcher.DispatchAsync(tc);

            Assert.IsFalse(result.Result.Success);
            Assert.That(result.Result.Error, Does.Contain("action"));
            Assert.That(result.Result.Error, Does.Contain("read"));
            Assert.That(result.Result.Error, Does.Contain("write"));
            Assert.IsFalse(_fakeTool.WasExecuted);
        }

        #endregion

        #region 合法参数

        [Test]
        public async Task ValidParameters_ExecutesTool()
        {
            var tc = MakeToolCall("test_tool", @"{""action"": ""read"", ""count"": 10, ""verbose"": true}");
            var result = await _dispatcher.DispatchAsync(tc);

            Assert.IsTrue(result.Result.Success);
            Assert.IsTrue(_fakeTool.WasExecuted);
        }

        [Test]
        public async Task EmptySchema_AllowsExecution()
        {
            // 注册一个空 schema 的工具
            var emptyTool = new FakeTool("empty_schema_tool", new JObject());
            ToolRegistry.Instance.Register(emptyTool);

            var tc = MakeToolCall("empty_schema_tool", @"{""anything"": ""goes""}");
            var result = await _dispatcher.DispatchAsync(tc);

            Assert.IsTrue(result.Result.Success);
            Assert.IsTrue(emptyTool.WasExecuted);
        }

        [Test]
        public async Task NullSchema_AllowsExecution()
        {
            // 注册一个 null schema 的工具
            var nullTool = new FakeTool("null_schema_tool", null);
            ToolRegistry.Instance.Register(nullTool);

            var tc = MakeToolCall("null_schema_tool", @"{""foo"": ""bar""}");
            var result = await _dispatcher.DispatchAsync(tc);

            Assert.IsTrue(result.Result.Success);
            Assert.IsTrue(nullTool.WasExecuted);
        }

        [Test]
        public async Task ExtraFields_AllowedByDefault()
        {
            // 传入 schema 中未声明的额外字段，应允许
            var tc = MakeToolCall("test_tool", @"{""action"": ""read"", ""undeclared_field"": ""value""}");
            var result = await _dispatcher.DispatchAsync(tc);

            Assert.IsTrue(result.Result.Success);
            Assert.IsTrue(_fakeTool.WasExecuted);
        }

        #endregion

        #region ToolParameterValidator 单元测试

        [Test]
        public void Validate_NullSchema_ReturnsTrue()
        {
            var parameters = JObject.Parse(@"{""key"": ""value""}");
            var result = ToolParameterValidator.Validate(parameters, null, out var error);

            Assert.IsTrue(result);
            Assert.IsNull(error);
        }

        [Test]
        public void Validate_EmptySchema_ReturnsTrue()
        {
            var parameters = JObject.Parse(@"{""key"": ""value""}");
            var result = ToolParameterValidator.Validate(parameters, new JObject(), out var error);

            Assert.IsTrue(result);
            Assert.IsNull(error);
        }

        [Test]
        public void Validate_MissingRequired_ReturnsFalse()
        {
            var parameters = JObject.Parse(@"{""count"": 5}");
            var result = ToolParameterValidator.Validate(parameters, TestSchema, out var error);

            Assert.IsFalse(result);
            Assert.That(error, Does.Contain("action"));
            Assert.That(error, Does.Contain("required"));
        }

        [Test]
        public void Validate_TypeMismatch_ReturnsFalse()
        {
            var parameters = JObject.Parse(@"{""action"": ""read"", ""count"": ""not_int""}");
            var result = ToolParameterValidator.Validate(parameters, TestSchema, out var error);

            Assert.IsFalse(result);
            Assert.That(error, Does.Contain("count"));
            Assert.That(error, Does.Contain("integer"));
        }

        [Test]
        public void Validate_EnumMismatch_ReturnsFalse()
        {
            var parameters = JObject.Parse(@"{""action"": ""delete""}");
            var result = ToolParameterValidator.Validate(parameters, TestSchema, out var error);

            Assert.IsFalse(result);
            Assert.That(error, Does.Contain("action"));
            Assert.That(error, Does.Contain("read"));
        }

        [Test]
        public void Validate_ValidParameters_ReturnsTrue()
        {
            var parameters = JObject.Parse(@"{""action"": ""write"", ""count"": 3, ""verbose"": false}");
            var result = ToolParameterValidator.Validate(parameters, TestSchema, out var error);

            Assert.IsTrue(result);
            Assert.IsNull(error);
        }

        #endregion
    }
}
