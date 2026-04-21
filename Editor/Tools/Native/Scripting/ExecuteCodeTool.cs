using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Tools.Infrastructure;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Tools.Native.Scripting
{
    /// <summary>
    /// Execute simple C# expressions via reflection.
    /// Supports static method calls, property reads, and simple Unity API queries.
    /// For complex code, recommend using manage_script to create and run scripts.
    /// </summary>
    [AgentTool("execute_code",
        Description = "Execute simple C# expressions via reflection — static method calls, property reads, and Unity API queries",
        Category = "Scripting",
        RequiresMainThread = true,
        MayModifyScripts = false)]
    public class ExecuteCodeTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""evaluate""],
                    ""description"": ""Action to perform""
                },
                ""code"": {
                    ""type"": ""string"",
                    ""description"": ""C# expression to evaluate (e.g., 'UnityEngine.Application.dataPath', 'UnityEditor.EditorApplication.isPlaying')""
                },
                ""context"": {
                    ""type"": ""string"",
                    ""enum"": [""editor"", ""scene""],
                    ""description"": ""Execution context (default: 'editor')""
                }
            },
            ""required"": [""action"", ""code""]
        }");

        /// <summary>
        /// Allowed namespace prefixes for security.
        /// </summary>
        private static readonly string[] AllowedNamespaces = new[]
        {
            "UnityEngine",
            "UnityEditor",
            "System",
            "System.IO",
            "System.Linq"
        };

        public ToolMetadata Metadata => new ToolMetadata(
            name: "execute_code",
            description: "Execute simple C# expressions via reflection — static method calls, property reads, and Unity API queries",
            category: "Scripting",
            parametersSchema: _parametersSchema,
            requiresMainThread: true
        );

        public Task<ToolResult> ExecuteAsync(JObject parameters, CancellationToken cancellationToken = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ToolResponse response;

            try
            {
                var action = ToolHelpers.GetRequiredString(parameters, "action").ToLowerInvariant();

                switch (action)
                {
                    case "evaluate":
                        response = HandleEvaluate(parameters);
                        break;
                    default:
                        response = ToolResponse.Fail(
                            $"Unknown action: '{action}'. Valid actions: evaluate");
                        break;
                }
            }
            catch (ArgumentException ex)
            {
                response = ToolResponse.Fail(ex.Message);
            }
            catch (Exception ex)
            {
                response = ToolResponse.Fail($"Unexpected error: {ex.Message}");
            }

            sw.Stop();
            return Task.FromResult(response.ToToolResult(sw.Elapsed.TotalMilliseconds));
        }

        #region Action Handlers

        private ToolResponse HandleEvaluate(JObject parameters)
        {
            var code = ToolHelpers.GetRequiredString(parameters, "code");
            var context = ToolHelpers.GetOptionalString(parameters, "context", "editor");

            // Trim whitespace and trailing semicolons
            code = code.Trim().TrimEnd(';');

            if (string.IsNullOrEmpty(code))
                return ToolResponse.Fail("Code expression is empty.");

            // Security check: block dangerous patterns
            if (ContainsDangerousPattern(code))
                return ToolResponse.Fail(
                    "Code contains potentially dangerous operations (Process.Start, File.Delete, etc.). " +
                    "Use manage_script to create a script for complex operations.");

            // Try to evaluate the expression
            try
            {
                var result = EvaluateExpression(code);

                var data = new JObject
                {
                    ["expression"] = code,
                    ["context"] = context
                };

                if (result != null)
                {
                    data["result"] = FormatResult(result);
                    data["resultType"] = result.GetType().FullName;
                }
                else
                {
                    data["result"] = "null";
                    data["resultType"] = "null";
                }

                return ToolResponse.OkWithData(data, $"Evaluated: {code}");
            }
            catch (TargetInvocationException tie)
            {
                var inner = tie.InnerException ?? tie;
                return ToolResponse.Fail($"Execution error: {inner.Message}");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail(
                    $"Cannot evaluate expression: {ex.Message}. " +
                    "This tool supports simple static property/method access (e.g., 'UnityEngine.Application.dataPath'). " +
                    "For complex code, use manage_script to create and run a script.");
            }
        }

        #endregion

        #region Expression Evaluation

        /// <summary>
        /// Evaluate a simple expression like "Type.Property" or "Type.Method()" via reflection.
        /// </summary>
        private object EvaluateExpression(string expression)
        {
            // Try to parse as a member access chain: Namespace.Type.Member.SubMember...
            // Strategy: try progressively longer type names, then resolve the remaining as members

            var parts = expression.Split('.');
            if (parts.Length < 2)
                throw new ArgumentException(
                    $"Expression must be in format 'Type.Member' or 'Namespace.Type.Member'. Got: '{expression}'");

            // Check if it looks like a method call (has parentheses)
            var lastPart = parts[parts.Length - 1];
            bool isMethodCall = lastPart.Contains("(");
            string methodArgs = null;

            if (isMethodCall)
            {
                var parenStart = lastPart.IndexOf('(');
                var parenEnd = lastPart.LastIndexOf(')');
                if (parenEnd > parenStart)
                {
                    methodArgs = lastPart.Substring(parenStart + 1, parenEnd - parenStart - 1).Trim();
                }
                parts[parts.Length - 1] = lastPart.Substring(0, lastPart.IndexOf('('));
            }

            // Try to resolve the type by testing progressively longer prefixes
            Type resolvedType = null;
            int memberStartIndex = -1;

            for (int i = parts.Length - 1; i >= 1; i--)
            {
                var typeName = string.Join(".", parts, 0, i);
                resolvedType = ResolveType(typeName);
                if (resolvedType != null)
                {
                    memberStartIndex = i;
                    break;
                }
            }

            if (resolvedType == null)
                throw new ArgumentException($"Could not resolve type from expression: '{expression}'");

            // Security: check namespace
            if (!IsAllowedType(resolvedType))
                throw new ArgumentException(
                    $"Type '{resolvedType.FullName}' is not in the allowed namespaces. " +
                    $"Allowed: {string.Join(", ", AllowedNamespaces)}");

            // Resolve member chain
            object currentValue = null;
            Type currentType = resolvedType;
            bool isStatic = true;

            for (int i = memberStartIndex; i < parts.Length; i++)
            {
                var memberName = parts[i];
                bool isLastPart = (i == parts.Length - 1);
                bool callAsMethod = isLastPart && isMethodCall;

                if (callAsMethod)
                {
                    // Try method invocation
                    var method = currentType.GetMethod(memberName,
                        BindingFlags.Public | (isStatic ? BindingFlags.Static : BindingFlags.Instance) |
                        BindingFlags.FlattenHierarchy);

                    if (method == null)
                        throw new ArgumentException($"Method '{memberName}' not found on type '{currentType.Name}'");

                    var methodParams = method.GetParameters();
                    object[] args;

                    if (string.IsNullOrEmpty(methodArgs))
                    {
                        args = new object[0];
                    }
                    else if (methodParams.Length == 0)
                    {
                        args = new object[0];
                    }
                    else
                    {
                        args = ParseMethodArguments(methodArgs, methodParams);
                    }

                    currentValue = method.Invoke(isStatic ? null : currentValue, args);
                    currentType = method.ReturnType;
                }
                else
                {
                    // Try property first
                    var prop = currentType.GetProperty(memberName,
                        BindingFlags.Public | (isStatic ? BindingFlags.Static : BindingFlags.Instance) |
                        BindingFlags.FlattenHierarchy);

                    if (prop != null)
                    {
                        currentValue = prop.GetValue(isStatic ? null : currentValue);
                        currentType = prop.PropertyType;
                    }
                    else
                    {
                        // Try field
                        var field = currentType.GetField(memberName,
                            BindingFlags.Public | (isStatic ? BindingFlags.Static : BindingFlags.Instance) |
                            BindingFlags.FlattenHierarchy);

                        if (field != null)
                        {
                            currentValue = field.GetValue(isStatic ? null : currentValue);
                            currentType = field.FieldType;
                        }
                        else
                        {
                            throw new ArgumentException(
                                $"Member '{memberName}' not found on type '{currentType.Name}'");
                        }
                    }
                }

                isStatic = false; // After first member access, subsequent accesses are instance-based
            }

            return currentValue;
        }

        /// <summary>
        /// Resolve a type name across all loaded assemblies.
        /// </summary>
        private Type ResolveType(string typeName)
        {
            // Direct lookup
            var type = Type.GetType(typeName);
            if (type != null) return type;

            // Search all assemblies
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetType(typeName);
                if (type != null) return type;
            }

            return null;
        }

        /// <summary>
        /// Check if a type is in the allowed namespaces.
        /// </summary>
        private bool IsAllowedType(Type type)
        {
            if (type == null || type.Namespace == null) return false;
            return AllowedNamespaces.Any(ns => type.Namespace.StartsWith(ns));
        }

        /// <summary>
        /// Parse simple method arguments from a string.
        /// Supports: strings ("..."), numbers, booleans, null.
        /// </summary>
        private object[] ParseMethodArguments(string argsString, ParameterInfo[] paramInfos)
        {
            if (string.IsNullOrWhiteSpace(argsString))
                return new object[0];

            // Simple split by comma (doesn't handle commas inside strings well, but sufficient for basic use)
            var argStrings = SplitArguments(argsString);
            var args = new object[argStrings.Length];

            for (int i = 0; i < argStrings.Length && i < paramInfos.Length; i++)
            {
                args[i] = ConvertArgument(argStrings[i].Trim(), paramInfos[i].ParameterType);
            }

            return args;
        }

        /// <summary>
        /// Split argument string by commas, respecting quoted strings.
        /// </summary>
        private string[] SplitArguments(string argsString)
        {
            var args = new List<string>();
            var current = "";
            bool inQuotes = false;
            char quoteChar = '"';

            for (int i = 0; i < argsString.Length; i++)
            {
                char c = argsString[i];

                if (inQuotes)
                {
                    if (c == quoteChar)
                        inQuotes = false;
                    else
                        current += c;
                }
                else
                {
                    if (c == '"' || c == '\'')
                    {
                        inQuotes = true;
                        quoteChar = c;
                    }
                    else if (c == ',')
                    {
                        args.Add(current.Trim());
                        current = "";
                    }
                    else
                    {
                        current += c;
                    }
                }
            }

            if (!string.IsNullOrEmpty(current.Trim()))
                args.Add(current.Trim());

            return args.ToArray();
        }

        /// <summary>
        /// Convert a string argument to the target parameter type.
        /// </summary>
        private object ConvertArgument(string value, Type targetType)
        {
            if (value == "null") return null;
            if (value == "true") return true;
            if (value == "false") return false;

            if (targetType == typeof(string))
                return value;
            if (targetType == typeof(int))
                return int.Parse(value);
            if (targetType == typeof(float))
                return float.Parse(value);
            if (targetType == typeof(double))
                return double.Parse(value);
            if (targetType == typeof(bool))
                return bool.Parse(value);
            if (targetType == typeof(long))
                return long.Parse(value);

            // Try generic conversion
            return Convert.ChangeType(value, targetType);
        }

        /// <summary>
        /// Format a result object for JSON output.
        /// </summary>
        private JToken FormatResult(object result)
        {
            if (result == null) return JValue.CreateNull();

            // Primitive types
            if (result is string s) return new JValue(s);
            if (result is bool b) return new JValue(b);
            if (result is int i) return new JValue(i);
            if (result is long l) return new JValue(l);
            if (result is float f) return new JValue(f);
            if (result is double d) return new JValue(d);
            if (result is decimal dec) return new JValue(dec);

            // Unity types
            if (result is Vector3 v3) return ToolHelpers.Vector3ToJson(v3);
            if (result is Vector2 v2) return new JObject { ["x"] = v2.x, ["y"] = v2.y };
            if (result is Color color) return new JValue($"#{ColorUtility.ToHtmlStringRGBA(color)}");
            if (result is Quaternion q) return ToolHelpers.QuaternionToJson(q);

            // GameObject
            if (result is GameObject go) return ToolHelpers.SerializeGameObject(go);

            // Component
            if (result is Component comp) return ToolHelpers.SerializeComponent(comp);

            // Enum
            if (result is Enum e) return new JValue(e.ToString());

            // Arrays and collections
            if (result is System.Collections.IEnumerable enumerable && !(result is string))
            {
                var arr = new JArray();
                int count = 0;
                foreach (var item in enumerable)
                {
                    if (count >= 100) // Limit array output
                    {
                        arr.Add("[... truncated]");
                        break;
                    }
                    arr.Add(FormatResult(item));
                    count++;
                }
                return arr;
            }

            // Fallback: ToString
            return new JValue(result.ToString());
        }

        /// <summary>
        /// Check for dangerous code patterns.
        /// </summary>
        private bool ContainsDangerousPattern(string code)
        {
            var dangerous = new[]
            {
                "Process.Start",
                "Process.Kill",
                "File.Delete",
                "File.Move",
                "Directory.Delete",
                "Environment.Exit",
                "AppDomain.Unload",
                "Assembly.Load",
                "Activator.CreateInstance",
                "Runtime.InteropServices",
                "DllImport",
                "unsafe",
                "Marshal."
            };

            return dangerous.Any(p => code.Contains(p));
        }

        #endregion
    }
}
