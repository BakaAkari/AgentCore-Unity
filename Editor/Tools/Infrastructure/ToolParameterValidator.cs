using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace AgentCore.Editor.Tools.Infrastructure
{
    /// <summary>
    /// 工具参数校验器 — 基于 JSON Schema 子集对 LLM 传入的参数进行预校验。
    /// <para>
    /// 支持的 JSON Schema 子集：
    /// <list type="bullet">
    ///   <item><c>type: object</c> — 顶层参数必须是对象</item>
    ///   <item><c>required</c> — 缺失字段直接失败</item>
    ///   <item><c>properties</c> — 仅校验已声明字段的类型</item>
    ///   <item><c>type</c> — 支持 string / number / integer / boolean / array / object</item>
    ///   <item><c>enum</c> — 字段值必须在枚举列表内</item>
    /// </list>
    /// </para>
    /// <para>
    /// 设计原则：
    /// <list type="bullet">
    ///   <item>空 schema 或无 properties 时保持宽松，允许执行</item>
    ///   <item>未声明的额外字段默认允许（不校验 additionalProperties）</item>
    ///   <item>不引入第三方 JSON Schema 库，保持轻量</item>
    /// </list>
    /// </para>
    /// </summary>
    public static class ToolParameterValidator
    {
        /// <summary>
        /// 校验参数是否符合给定的 JSON Schema 子集。
        /// </summary>
        /// <param name="parameters">LLM 传入的参数（已解析为 JObject）</param>
        /// <param name="schema">工具的 ParametersSchema（JSON Schema 格式）</param>
        /// <param name="errorMessage">校验失败时的错误信息，成功时为 null</param>
        /// <returns>校验是否通过</returns>
        public static bool Validate(JObject parameters, JObject schema, out string errorMessage)
        {
            errorMessage = null;

            // 空 schema 或无内容时保持宽松
            if (schema == null || !schema.HasValues)
                return true;

            // 无 properties 定义时保持宽松
            var properties = schema["properties"] as JObject;
            if (properties == null || !properties.HasValues)
                return true;

            // 校验 required 字段
            var required = schema["required"] as JArray;
            if (required != null)
            {
                foreach (var req in required)
                {
                    var fieldName = req.ToString();
                    if (parameters[fieldName] == null || parameters[fieldName].Type == JTokenType.Null)
                    {
                        errorMessage = $"Missing required parameter '{fieldName}'.";
                        return false;
                    }
                }
            }

            // 校验已声明字段的类型
            foreach (var prop in properties)
            {
                var fieldName = prop.Key;
                var fieldSchema = prop.Value as JObject;
                if (fieldSchema == null)
                    continue;

                var paramValue = parameters[fieldName];
                // 字段不存在且不在 required 中，跳过
                if (paramValue == null || paramValue.Type == JTokenType.Null)
                    continue;

                // 校验类型
                var expectedType = fieldSchema["type"]?.ToString();
                if (!string.IsNullOrEmpty(expectedType))
                {
                    if (!ValidateType(paramValue, expectedType, out var typeError))
                    {
                        errorMessage = $"Parameter '{fieldName}' {typeError}";
                        return false;
                    }
                }

                // 校验 enum
                var enumValues = fieldSchema["enum"] as JArray;
                if (enumValues != null)
                {
                    if (!ValidateEnum(paramValue, enumValues, out var enumError))
                    {
                        errorMessage = $"Parameter '{fieldName}' {enumError}";
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// 校验值是否符合指定的 JSON Schema 类型。
        /// </summary>
        /// <param name="value">参数值</param>
        /// <param name="expectedType">期望的 JSON Schema 类型</param>
        /// <param name="error">错误描述</param>
        /// <returns>是否通过</returns>
        private static bool ValidateType(JToken value, string expectedType, out string error)
        {
            error = null;

            switch (expectedType)
            {
                case "string":
                    if (value.Type != JTokenType.String)
                    {
                        error = $"expected string but got {GetFriendlyTypeName(value.Type)}.";
                        return false;
                    }
                    return true;

                case "integer":
                    if (value.Type != JTokenType.Integer)
                    {
                        error = $"expected integer but got {GetFriendlyTypeName(value.Type)}.";
                        return false;
                    }
                    return true;

                case "number":
                    // number 接受 integer 和 float
                    if (value.Type != JTokenType.Integer && value.Type != JTokenType.Float)
                    {
                        error = $"expected number but got {GetFriendlyTypeName(value.Type)}.";
                        return false;
                    }
                    return true;

                case "boolean":
                    if (value.Type != JTokenType.Boolean)
                    {
                        error = $"expected boolean but got {GetFriendlyTypeName(value.Type)}.";
                        return false;
                    }
                    return true;

                case "array":
                    if (value.Type != JTokenType.Array)
                    {
                        error = $"expected array but got {GetFriendlyTypeName(value.Type)}.";
                        return false;
                    }
                    return true;

                case "object":
                    if (value.Type != JTokenType.Object)
                    {
                        error = $"expected object but got {GetFriendlyTypeName(value.Type)}.";
                        return false;
                    }
                    return true;

                default:
                    // 未知类型不校验，保持宽松
                    return true;
            }
        }

        /// <summary>
        /// 校验值是否在枚举列表内。
        /// </summary>
        /// <param name="value">参数值</param>
        /// <param name="enumValues">枚举值列表</param>
        /// <param name="error">错误描述</param>
        /// <returns>是否通过</returns>
        private static bool ValidateEnum(JToken value, JArray enumValues, out string error)
        {
            error = null;

            foreach (var enumVal in enumValues)
            {
                if (JToken.DeepEquals(value, enumVal))
                    return true;
            }

            // 构建枚举列表字符串
            var allowedValues = new List<string>();
            foreach (var ev in enumValues)
            {
                allowedValues.Add(ev.ToString());
            }

            var allowedStr = string.Join(", ", allowedValues);
            error = $"expected one of [{allowedStr}] but got '{value}'.";
            return false;
        }

        /// <summary>
        /// 获取 JTokenType 的友好名称。
        /// </summary>
        /// <param name="type">JToken 类型</param>
        /// <returns>友好名称字符串</returns>
        private static string GetFriendlyTypeName(JTokenType type)
        {
            switch (type)
            {
                case JTokenType.String: return "string";
                case JTokenType.Integer: return "integer";
                case JTokenType.Float: return "number";
                case JTokenType.Boolean: return "boolean";
                case JTokenType.Array: return "array";
                case JTokenType.Object: return "object";
                case JTokenType.Null: return "null";
                default: return type.ToString().ToLowerInvariant();
            }
        }
    }
}
