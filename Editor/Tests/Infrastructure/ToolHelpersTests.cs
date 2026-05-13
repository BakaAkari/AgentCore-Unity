using System;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using AgentCore.Editor.Tools.Infrastructure;
using UnityEngine;

namespace AgentCore.Tests.Editor.Infrastructure
{
    /// <summary>
    /// ToolHelpers 参数解析方法的单元测试。
    /// </summary>
    [TestFixture]
    public class ToolHelpersTests
    {
        #region GetRequiredString

        [Test]
        public void GetRequiredString_ExistingKey_ReturnsValue()
        {
            var parameters = JObject.Parse("{\"action\":\"create\"}");

            Assert.AreEqual("create", ToolHelpers.GetRequiredString(parameters, "action"));
        }

        [Test]
        public void GetRequiredString_MissingKey_ThrowsArgumentException()
        {
            var parameters = JObject.Parse("{\"action\":\"create\"}");

            var ex = Assert.Throws<ArgumentException>(() =>
                ToolHelpers.GetRequiredString(parameters, "missing_key"));

            StringAssert.Contains("missing_key", ex.Message);
        }

        [Test]
        public void GetRequiredString_EmptyValue_ThrowsArgumentException()
        {
            var parameters = JObject.Parse("{\"action\":\"\"}");

            Assert.Throws<ArgumentException>(() =>
                ToolHelpers.GetRequiredString(parameters, "action"));
        }

        [Test]
        public void GetRequiredString_NullParameters_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() =>
                ToolHelpers.GetRequiredString(null, "action"));
        }

        #endregion

        #region GetOptionalString

        [Test]
        public void GetOptionalString_ExistingKey_ReturnsValue()
        {
            var parameters = JObject.Parse("{\"name\":\"test\"}");

            Assert.AreEqual("test", ToolHelpers.GetOptionalString(parameters, "name"));
        }

        [Test]
        public void GetOptionalString_MissingKey_ReturnsNull()
        {
            var parameters = JObject.Parse("{\"name\":\"test\"}");

            Assert.IsNull(ToolHelpers.GetOptionalString(parameters, "missing"));
        }

        [Test]
        public void GetOptionalString_MissingKey_ReturnsDefaultValue()
        {
            var parameters = JObject.Parse("{\"name\":\"test\"}");

            Assert.AreEqual("fallback", ToolHelpers.GetOptionalString(parameters, "missing", "fallback"));
        }

        [Test]
        public void GetOptionalString_NullParameters_ReturnsDefault()
        {
            Assert.IsNull(ToolHelpers.GetOptionalString(null, "key"));
            Assert.AreEqual("def", ToolHelpers.GetOptionalString(null, "key", "def"));
        }

        #endregion

        #region GetOptionalInt

        [Test]
        public void GetOptionalInt_ExistingKey_ReturnsValue()
        {
            var parameters = JObject.Parse("{\"count\":42}");

            Assert.AreEqual(42, ToolHelpers.GetOptionalInt(parameters, "count"));
        }

        [Test]
        public void GetOptionalInt_MissingKey_ReturnsDefault()
        {
            var parameters = JObject.Parse("{\"count\":42}");

            Assert.AreEqual(0, ToolHelpers.GetOptionalInt(parameters, "missing"));
            Assert.AreEqual(10, ToolHelpers.GetOptionalInt(parameters, "missing", 10));
        }

        [Test]
        public void GetOptionalInt_NullParameters_ReturnsDefault()
        {
            Assert.AreEqual(0, ToolHelpers.GetOptionalInt(null, "key"));
            Assert.AreEqual(5, ToolHelpers.GetOptionalInt(null, "key", 5));
        }

        #endregion

        #region GetOptionalFloat

        [Test]
        public void GetOptionalFloat_ExistingKey_ReturnsValue()
        {
            var parameters = JObject.Parse("{\"speed\":3.14}");

            Assert.AreEqual(3.14f, ToolHelpers.GetOptionalFloat(parameters, "speed"), 0.001f);
        }

        [Test]
        public void GetOptionalFloat_MissingKey_ReturnsDefault()
        {
            var parameters = JObject.Parse("{\"speed\":3.14}");

            Assert.AreEqual(0f, ToolHelpers.GetOptionalFloat(parameters, "missing"));
            Assert.AreEqual(1.5f, ToolHelpers.GetOptionalFloat(parameters, "missing", 1.5f), 0.001f);
        }

        #endregion

        #region GetOptionalBool

        [Test]
        public void GetOptionalBool_ExistingKey_ReturnsValue()
        {
            var parameters = JObject.Parse("{\"verbose\":true}");

            Assert.IsTrue(ToolHelpers.GetOptionalBool(parameters, "verbose"));
        }

        [Test]
        public void GetOptionalBool_MissingKey_ReturnsDefault()
        {
            var parameters = JObject.Parse("{\"verbose\":true}");

            Assert.IsFalse(ToolHelpers.GetOptionalBool(parameters, "missing"));
            Assert.IsTrue(ToolHelpers.GetOptionalBool(parameters, "missing", true));
        }

        [Test]
        public void GetOptionalBool_NullParameters_ReturnsDefault()
        {
            Assert.IsFalse(ToolHelpers.GetOptionalBool(null, "key"));
            Assert.IsTrue(ToolHelpers.GetOptionalBool(null, "key", true));
        }

        #endregion

        #region GetRequiredEnum

        private enum TestAction { Create, Delete, Update }

        [Test]
        public void GetRequiredEnum_ValidValue_ReturnsEnum()
        {
            var parameters = JObject.Parse("{\"action\":\"Create\"}");

            Assert.AreEqual(TestAction.Create, ToolHelpers.GetRequiredEnum<TestAction>(parameters, "action"));
        }

        [Test]
        public void GetRequiredEnum_CaseInsensitive_ReturnsEnum()
        {
            var parameters = JObject.Parse("{\"action\":\"delete\"}");

            Assert.AreEqual(TestAction.Delete, ToolHelpers.GetRequiredEnum<TestAction>(parameters, "action"));
        }

        [Test]
        public void GetRequiredEnum_InvalidValue_ThrowsWithValidValues()
        {
            var parameters = JObject.Parse("{\"action\":\"invalid\"}");

            var ex = Assert.Throws<ArgumentException>(() =>
                ToolHelpers.GetRequiredEnum<TestAction>(parameters, "action"));

            StringAssert.Contains("invalid", ex.Message);
            StringAssert.Contains("Create", ex.Message);
            StringAssert.Contains("Delete", ex.Message);
            StringAssert.Contains("Update", ex.Message);
        }

        [Test]
        public void GetRequiredEnum_MissingKey_ThrowsArgumentException()
        {
            var parameters = JObject.Parse("{}");

            Assert.Throws<ArgumentException>(() =>
                ToolHelpers.GetRequiredEnum<TestAction>(parameters, "action"));
        }

        #endregion

        #region GetOptionalEnum

        [Test]
        public void GetOptionalEnum_ValidValue_ReturnsEnum()
        {
            var parameters = JObject.Parse("{\"mode\":\"Update\"}");

            Assert.AreEqual(TestAction.Update, ToolHelpers.GetOptionalEnum(parameters, "mode", TestAction.Create));
        }

        [Test]
        public void GetOptionalEnum_MissingKey_ReturnsDefault()
        {
            var parameters = JObject.Parse("{}");

            Assert.AreEqual(TestAction.Create, ToolHelpers.GetOptionalEnum(parameters, "mode", TestAction.Create));
        }

        [Test]
        public void GetOptionalEnum_InvalidValue_ReturnsDefault()
        {
            var parameters = JObject.Parse("{\"mode\":\"nonsense\"}");

            Assert.AreEqual(TestAction.Delete, ToolHelpers.GetOptionalEnum(parameters, "mode", TestAction.Delete));
        }

        #endregion

        #region GetOptionalObject / GetOptionalArray

        [Test]
        public void GetOptionalObject_ExistingKey_ReturnsJObject()
        {
            var parameters = JObject.Parse("{\"config\":{\"x\":1}}");
            var result = ToolHelpers.GetOptionalObject(parameters, "config");

            Assert.IsNotNull(result);
            Assert.AreEqual(1, result["x"].Value<int>());
        }

        [Test]
        public void GetOptionalObject_MissingKey_ReturnsNull()
        {
            var parameters = JObject.Parse("{}");

            Assert.IsNull(ToolHelpers.GetOptionalObject(parameters, "config"));
        }

        [Test]
        public void GetOptionalArray_ExistingKey_ReturnsJArray()
        {
            var parameters = JObject.Parse("{\"items\":[1,2,3]}");
            var result = ToolHelpers.GetOptionalArray(parameters, "items");

            Assert.IsNotNull(result);
            Assert.AreEqual(3, result.Count);
        }

        [Test]
        public void GetOptionalArray_MissingKey_ReturnsNull()
        {
            var parameters = JObject.Parse("{}");

            Assert.IsNull(ToolHelpers.GetOptionalArray(parameters, "items"));
        }

        #endregion

        #region ParseVector3

        [Test]
        public void ParseVector3_ValidObject_ReturnsVector()
        {
            var token = JObject.Parse("{\"x\":1.0,\"y\":2.0,\"z\":3.0}");
            var result = ToolHelpers.ParseVector3(token);

            Assert.AreEqual(1.0f, result.x, 0.001f);
            Assert.AreEqual(2.0f, result.y, 0.001f);
            Assert.AreEqual(3.0f, result.z, 0.001f);
        }

        [Test]
        public void ParseVector3_PartialObject_UsesDefaultForMissing()
        {
            var token = JObject.Parse("{\"x\":5.0}");
            var result = ToolHelpers.ParseVector3(token);

            Assert.AreEqual(5.0f, result.x, 0.001f);
            Assert.AreEqual(0f, result.y, 0.001f);
            Assert.AreEqual(0f, result.z, 0.001f);
        }

        [Test]
        public void ParseVector3_NullToken_ReturnsDefault()
        {
            var result = ToolHelpers.ParseVector3(null, new Vector3(1, 2, 3));

            Assert.AreEqual(new Vector3(1, 2, 3), result);
        }

        [Test]
        public void ParseVector3_NonObjectToken_ReturnsDefault()
        {
            var token = new JValue("not an object");
            var result = ToolHelpers.ParseVector3(token, Vector3.one);

            Assert.AreEqual(Vector3.one, result);
        }

        #endregion

        #region ParseColor

        [Test]
        public void ParseColor_RGBAObject_ReturnsColor()
        {
            var token = JObject.Parse("{\"r\":1.0,\"g\":0.0,\"b\":0.0,\"a\":1.0}");
            var result = ToolHelpers.ParseColor(token);

            Assert.AreEqual(1.0f, result.r, 0.001f);
            Assert.AreEqual(0.0f, result.g, 0.001f);
            Assert.AreEqual(0.0f, result.b, 0.001f);
            Assert.AreEqual(1.0f, result.a, 0.001f);
        }

        [Test]
        public void ParseColor_HexString_ReturnsColor()
        {
            var token = new JValue("#FF0000");
            var result = ToolHelpers.ParseColor(token);

            Assert.AreEqual(1.0f, result.r, 0.001f);
            Assert.AreEqual(0.0f, result.g, 0.001f);
            Assert.AreEqual(0.0f, result.b, 0.001f);
        }

        [Test]
        public void ParseColor_NullToken_ReturnsDefault()
        {
            var defaultColor = Color.blue;
            var result = ToolHelpers.ParseColor(null, defaultColor);

            Assert.AreEqual(defaultColor, result);
        }

        [Test]
        public void ParseColor_InvalidHex_ReturnsDefault()
        {
            var token = new JValue("not-a-color");
            var result = ToolHelpers.ParseColor(token, Color.white);

            Assert.AreEqual(Color.white, result);
        }

        #endregion

        #region Vector3ToJson / QuaternionToJson

        [Test]
        public void Vector3ToJson_ReturnsCorrectXYZ()
        {
            var v = new Vector3(1.5f, 2.5f, 3.5f);
            var json = ToolHelpers.Vector3ToJson(v);

            Assert.AreEqual(1.5, json["x"].Value<double>(), 0.001);
            Assert.AreEqual(2.5, json["y"].Value<double>(), 0.001);
            Assert.AreEqual(3.5, json["z"].Value<double>(), 0.001);
        }

        [Test]
        public void QuaternionToJson_ReturnsEulerAngles()
        {
            var q = Quaternion.Euler(90, 0, 0);
            var json = ToolHelpers.QuaternionToJson(q);

            Assert.AreEqual(90.0, json["x"].Value<double>(), 0.1);
            Assert.AreEqual(0.0, json["y"].Value<double>(), 0.1);
            Assert.AreEqual(0.0, json["z"].Value<double>(), 0.1);
        }

        #endregion
    }
}
