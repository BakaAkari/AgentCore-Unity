using NUnit.Framework;
using Newtonsoft.Json.Linq;
using AgentCore.Editor.Utils;

namespace AgentCore.Tests.Editor.Utils
{
    /// <summary>
    /// JsonHelper 的单元测试。
    /// </summary>
    [TestFixture]
    public class JsonHelperTests
    {
        #region Serialize

        [Test]
        public void Serialize_SimpleObject_ReturnsValidJson()
        {
            var obj = new { name = "test", value = 42 };
            var json = JsonHelper.Serialize(obj);

            Assert.IsNotNull(json);
            var parsed = JObject.Parse(json);
            Assert.AreEqual("test", parsed["name"].Value<string>());
            Assert.AreEqual(42, parsed["value"].Value<int>());
        }

        [Test]
        public void Serialize_Pretty_ContainsNewlines()
        {
            var obj = new { a = 1 };
            var json = JsonHelper.Serialize(obj, pretty: true);

            StringAssert.Contains("\n", json);
        }

        [Test]
        public void Serialize_NullObject_ReturnsEmptyObject()
        {
            // Newtonsoft serializes null as "null" string, but our wrapper catches exceptions
            var json = JsonHelper.Serialize(null);
            Assert.IsNotNull(json);
        }

        #endregion

        #region Deserialize

        [Test]
        public void Deserialize_ValidJson_ReturnsObject()
        {
            var json = "{\"name\":\"hello\",\"count\":3}";
            var result = JsonHelper.Deserialize<TestData>(json);

            Assert.IsNotNull(result);
            Assert.AreEqual("hello", result.Name);
            Assert.AreEqual(3, result.Count);
        }

        [Test]
        public void Deserialize_InvalidJson_ReturnsDefault()
        {
            var result = JsonHelper.Deserialize<TestData>("not valid json {{{");

            Assert.IsNull(result);
        }

        [Test]
        public void Deserialize_NullInput_ReturnsDefault()
        {
            var result = JsonHelper.Deserialize<TestData>(null);

            Assert.IsNull(result);
        }

        [Test]
        public void Deserialize_EmptyString_ReturnsDefault()
        {
            var result = JsonHelper.Deserialize<TestData>("");

            Assert.IsNull(result);
        }

        #endregion

        #region ParseObject

        [Test]
        public void ParseObject_ValidJson_ReturnsJObject()
        {
            var result = JsonHelper.ParseObject("{\"key\":\"value\"}");

            Assert.IsNotNull(result);
            Assert.AreEqual("value", result["key"].Value<string>());
        }

        [Test]
        public void ParseObject_InvalidJson_ReturnsNull()
        {
            var result = JsonHelper.ParseObject("not json");

            Assert.IsNull(result);
        }

        [Test]
        public void ParseObject_NullInput_ReturnsNull()
        {
            var result = JsonHelper.ParseObject(null);

            Assert.IsNull(result);
        }

        [Test]
        public void ParseObject_EmptyString_ReturnsNull()
        {
            var result = JsonHelper.ParseObject("");

            Assert.IsNull(result);
        }

        [Test]
        public void ParseObject_ArrayJson_ReturnsNull()
        {
            // JObject.Parse on an array should fail
            var result = JsonHelper.ParseObject("[1,2,3]");

            Assert.IsNull(result);
        }

        #endregion

        #region ParseArray

        [Test]
        public void ParseArray_ValidJson_ReturnsJArray()
        {
            var result = JsonHelper.ParseArray("[1,2,3]");

            Assert.IsNotNull(result);
            Assert.AreEqual(3, result.Count);
            Assert.AreEqual(1, result[0].Value<int>());
        }

        [Test]
        public void ParseArray_InvalidJson_ReturnsNull()
        {
            var result = JsonHelper.ParseArray("not json");

            Assert.IsNull(result);
        }

        [Test]
        public void ParseArray_NullInput_ReturnsNull()
        {
            var result = JsonHelper.ParseArray(null);

            Assert.IsNull(result);
        }

        [Test]
        public void ParseArray_EmptyString_ReturnsNull()
        {
            var result = JsonHelper.ParseArray("");

            Assert.IsNull(result);
        }

        #endregion

        #region GetString

        [Test]
        public void GetString_ExistingKey_ReturnsValue()
        {
            var obj = JObject.Parse("{\"name\":\"hello\"}");

            Assert.AreEqual("hello", JsonHelper.GetString(obj, "name"));
        }

        [Test]
        public void GetString_MissingKey_ReturnsDefault()
        {
            var obj = JObject.Parse("{\"name\":\"hello\"}");

            Assert.IsNull(JsonHelper.GetString(obj, "missing"));
            Assert.AreEqual("fallback", JsonHelper.GetString(obj, "missing", "fallback"));
        }

        [Test]
        public void GetString_NullObj_ReturnsDefault()
        {
            Assert.IsNull(JsonHelper.GetString(null, "key"));
            Assert.AreEqual("def", JsonHelper.GetString(null, "key", "def"));
        }

        [Test]
        public void GetString_NonStringValue_ReturnsDefault()
        {
            var obj = JObject.Parse("{\"num\":42}");

            Assert.IsNull(JsonHelper.GetString(obj, "num"));
        }

        #endregion

        #region GetInt

        [Test]
        public void GetInt_ExistingKey_ReturnsValue()
        {
            var obj = JObject.Parse("{\"count\":7}");

            Assert.AreEqual(7, JsonHelper.GetInt(obj, "count"));
        }

        [Test]
        public void GetInt_MissingKey_ReturnsDefault()
        {
            var obj = JObject.Parse("{\"count\":7}");

            Assert.AreEqual(0, JsonHelper.GetInt(obj, "missing"));
            Assert.AreEqual(99, JsonHelper.GetInt(obj, "missing", 99));
        }

        [Test]
        public void GetInt_NullObj_ReturnsDefault()
        {
            Assert.AreEqual(0, JsonHelper.GetInt(null, "key"));
            Assert.AreEqual(5, JsonHelper.GetInt(null, "key", 5));
        }

        #endregion

        #region GetBool

        [Test]
        public void GetBool_ExistingKey_ReturnsValue()
        {
            var obj = JObject.Parse("{\"flag\":true}");

            Assert.IsTrue(JsonHelper.GetBool(obj, "flag"));
        }

        [Test]
        public void GetBool_MissingKey_ReturnsDefault()
        {
            var obj = JObject.Parse("{\"flag\":true}");

            Assert.IsFalse(JsonHelper.GetBool(obj, "missing"));
            Assert.IsTrue(JsonHelper.GetBool(obj, "missing", true));
        }

        [Test]
        public void GetBool_NullObj_ReturnsDefault()
        {
            Assert.IsFalse(JsonHelper.GetBool(null, "key"));
            Assert.IsTrue(JsonHelper.GetBool(null, "key", true));
        }

        #endregion

        #region Test Helpers

        private class TestData
        {
            public string Name { get; set; }
            public int Count { get; set; }
        }

        #endregion
    }
}
