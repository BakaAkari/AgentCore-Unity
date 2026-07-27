using System;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Tools.Infrastructure
{
    /// <summary>
    /// 工具实现中常用的辅助方法
    /// </summary>
    public static class ToolHelpers
    {
        #region 参数解析

        /// <summary>
        /// 获取必需的字符串参数
        /// </summary>
        public static string GetRequiredString(JObject parameters, string key)
        {
            var value = parameters?[key]?.ToString();
            if (string.IsNullOrEmpty(value))
                throw new ArgumentException($"Required parameter '{key}' is missing or empty.");
            return value;
        }

        /// <summary>
        /// 获取可选的字符串参数
        /// </summary>
        public static string GetOptionalString(JObject parameters, string key, string defaultValue = null)
        {
            return parameters?[key]?.ToString() ?? defaultValue;
        }

        /// <summary>
        /// 获取必需的枚举参数（字符串转枚举）
        /// </summary>
        public static T GetRequiredEnum<T>(JObject parameters, string key) where T : struct, Enum
        {
            var value = GetRequiredString(parameters, key);
            if (!Enum.TryParse<T>(value, true, out var result))
            {
                var validValues = string.Join(", ", Enum.GetNames(typeof(T)));
                throw new ArgumentException($"Invalid value '{value}' for parameter '{key}'. Valid values: {validValues}");
            }
            return result;
        }

        /// <summary>
        /// 获取可选的枚举参数
        /// </summary>
        public static T GetOptionalEnum<T>(JObject parameters, string key, T defaultValue) where T : struct, Enum
        {
            var value = parameters?[key]?.ToString();
            if (string.IsNullOrEmpty(value)) return defaultValue;
            if (!Enum.TryParse<T>(value, true, out var result))
            {
                return defaultValue;
            }
            return result;
        }

        /// <summary>
        /// 获取可选的 int 参数
        /// </summary>
        public static int GetOptionalInt(JObject parameters, string key, int defaultValue = 0)
        {
            var token = parameters?[key];
            if (token == null || token.Type == JTokenType.Null) return defaultValue;
            return TryCoerceInt(token, key, out var v) ? v : defaultValue;
        }

        /// <summary>
        /// 获取可选的 float 参数
        /// </summary>
        public static float GetOptionalFloat(JObject parameters, string key, float defaultValue = 0f)
        {
            var token = parameters?[key];
            if (token == null || token.Type == JTokenType.Null) return defaultValue;
            return TryCoerceFloat(token, key, out var v) ? v : defaultValue;
        }

        /// <summary>
        /// 获取可选的 bool 参数
        /// </summary>
        public static bool GetOptionalBool(JObject parameters, string key, bool defaultValue = false)
        {
            var token = parameters?[key];
            if (token == null || token.Type == JTokenType.Null) return defaultValue;
            return TryCoerceBool(token, key, out var v) ? v : defaultValue;
        }

        /// <summary>
        /// 尝试将 <see cref="JToken"/> 强制转换为 float。
        /// <para>
        /// 兼容：
        /// <list type="bullet">
        ///   <item><see cref="JTokenType.Float"/> / <see cref="JTokenType.Integer"/> — 直接返回 (fast path)</item>
        ///   <item><see cref="JTokenType.String"/> — 用 <see cref="CultureInfo.InvariantCulture"/> parse (provider bug workaround，对称 Bug E)</item>
        ///   <item><see cref="JTokenType.Boolean"/> — false→0, true→1</item>
        /// </list>
        /// 触发 String coercion 时写一条 warn log，便于后续判断能否撤除。
        /// </para>
        /// </summary>
        public static bool TryCoerceFloat(JToken token, string paramName, out float value)
        {
            value = 0f;
            if (token == null || token.Type == JTokenType.Null) return false;
            switch (token.Type)
            {
                case JTokenType.Float:
                case JTokenType.Integer:
                    value = token.Value<float>();
                    return true;
                case JTokenType.String:
                    if (float.TryParse(token.Value<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                    {
                        AgentCore.Editor.Utils.AgentCoreLog.Warning(
                            $"[AgentCore] Parameter '{paramName}' arrived as JSON string \"{token.Value<string>()}\" but expected number. " +
                            $"Auto-parsed to {value}. Provider may be misserializing numeric values.");
                        return true;
                    }
                    return false;
                case JTokenType.Boolean:
                    value = token.Value<bool>() ? 1f : 0f;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// 尝试将 <see cref="JToken"/> 强制转换为 int。字符串会被 parse；float 会被 truncate。
        /// </summary>
        public static bool TryCoerceInt(JToken token, string paramName, out int value)
        {
            value = 0;
            if (token == null || token.Type == JTokenType.Null) return false;
            switch (token.Type)
            {
                case JTokenType.Integer:
                    value = token.Value<int>();
                    return true;
                case JTokenType.Float:
                    value = (int)token.Value<double>();
                    return true;
                case JTokenType.String:
                    var s = token.Value<string>();
                    if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                    {
                        AgentCore.Editor.Utils.AgentCoreLog.Warning(
                            $"[AgentCore] Parameter '{paramName}' arrived as JSON string \"{s}\" but expected integer. " +
                            $"Auto-parsed to {value}. Provider may be misserializing numeric values.");
                        return true;
                    }
                    // 兜底：允许 "1.0" -> 1
                    if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    {
                        value = (int)d;
                        AgentCore.Editor.Utils.AgentCoreLog.Warning(
                            $"[AgentCore] Parameter '{paramName}' arrived as JSON string \"{s}\" (float-form) but expected integer. " +
                            $"Auto-truncated to {value}. Provider may be misserializing numeric values.");
                        return true;
                    }
                    return false;
                case JTokenType.Boolean:
                    value = token.Value<bool>() ? 1 : 0;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// 尝试将 <see cref="JToken"/> 强制转换为 bool。字符串 "true"/"false"/"1"/"0" 可 parse。
        /// </summary>
        public static bool TryCoerceBool(JToken token, string paramName, out bool value)
        {
            value = false;
            if (token == null || token.Type == JTokenType.Null) return false;
            switch (token.Type)
            {
                case JTokenType.Boolean:
                    value = token.Value<bool>();
                    return true;
                case JTokenType.String:
                    var s = token.Value<string>().Trim();
                    if (bool.TryParse(s, out value))
                    {
                        AgentCore.Editor.Utils.AgentCoreLog.Warning(
                            $"[AgentCore] Parameter '{paramName}' arrived as JSON string \"{s}\" but expected boolean. " +
                            $"Auto-parsed to {value}. Provider may be misserializing boolean values.");
                        return true;
                    }
                    // 数值 fallback: "0" -> false, "1" -> true
                    if (s == "0") { value = false; return true; }
                    if (s == "1") { value = true; return true; }
                    return false;
                case JTokenType.Integer:
                    value = token.Value<int>() != 0;
                    return true;
                case JTokenType.Float:
                    value = token.Value<double>() != 0.0;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// 获取可选的 JObject 参数
        /// </summary>
        public static JObject GetOptionalObject(JObject parameters, string key)
        {
            return parameters?[key] as JObject;
        }

        /// <summary>
        /// 获取可选的 JArray 参数
        /// </summary>
        public static JArray GetOptionalArray(JObject parameters, string key)
        {
            return parameters?[key] as JArray;
        }

        #endregion

        #region GameObject 查找

        /// <summary>
        /// 通过路径或名称查找 GameObject
        /// 支持层级路径（如 "Parent/Child/Target"）
        /// </summary>
        public static GameObject FindGameObject(string nameOrPath)
        {
            if (string.IsNullOrEmpty(nameOrPath))
                return null;

            // 先尝试精确路径查找
            var go = GameObject.Find(nameOrPath);
            if (go != null) return go;

            // 再尝试按名称在所有对象中查找（含 inactive，否则禁用后的对象不可达）
            var allObjects = UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            return allObjects.FirstOrDefault(o => o.name == nameOrPath);
        }

        /// <summary>
        /// 通过 instanceID 查找 GameObject
        /// </summary>
        public static GameObject FindGameObjectByInstanceId(int instanceId)
        {
            return EditorUtility.InstanceIDToObject(instanceId) as GameObject;
        }

        /// <summary>
        /// 按名称查找场景中所有同名 GameObject（用于同名全选场景）。
        /// <para>
        /// 与 <see cref="FindGameObject"/> 的区别：后者只返回第一个匹配，本方法返回全部同名对象。
        /// 传入层级路径（含 '/'）时不适用本方法——路径本身即为消歧手段，应走精确单个查找。
        /// </para>
        /// </summary>
        public static System.Collections.Generic.List<GameObject> FindGameObjectsByName(string name)
        {
            var result = new System.Collections.Generic.List<GameObject>();
            if (string.IsNullOrEmpty(name))
                return result;

            var allObjects = UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var o in allObjects)
            {
                if (o.name == name)
                    result.Add(o);
            }
            return result;
        }

        #endregion

        #region Vector 解析

        /// <summary>
        /// 从 JToken 解析 Vector3
        /// 支持格式: {"x": 1, "y": 2, "z": 3}
        /// </summary>
        public static Vector3 ParseVector3(JToken token, Vector3 defaultValue = default)
        {
            if (token == null) return defaultValue;

            if (token is JObject obj)
            {
                return new Vector3(
                    obj["x"]?.Value<float>() ?? defaultValue.x,
                    obj["y"]?.Value<float>() ?? defaultValue.y,
                    obj["z"]?.Value<float>() ?? defaultValue.z
                );
            }

            return defaultValue;
        }

        /// <summary>
        /// 从 JToken 解析 Color
        /// 支持格式: {"r": 1, "g": 0, "b": 0, "a": 1} 或 "#FF0000"
        /// </summary>
        public static Color ParseColor(JToken token, Color defaultValue = default)
        {
            if (token == null) return defaultValue;

            if (token is JObject obj)
            {
                return new Color(
                    obj["r"]?.Value<float>() ?? defaultValue.r,
                    obj["g"]?.Value<float>() ?? defaultValue.g,
                    obj["b"]?.Value<float>() ?? defaultValue.b,
                    obj["a"]?.Value<float>() ?? 1f
                );
            }

            if (token.Type == JTokenType.String)
            {
                if (ColorUtility.TryParseHtmlString(token.ToString(), out var color))
                    return color;
            }

            return defaultValue;
        }

        /// <summary>
        /// 将 Vector3 转为 JObject
        /// </summary>
        public static JObject Vector3ToJson(Vector3 v)
        {
            return new JObject
            {
                ["x"] = Math.Round(v.x, 4),
                ["y"] = Math.Round(v.y, 4),
                ["z"] = Math.Round(v.z, 4)
            };
        }

        /// <summary>
        /// 将 Quaternion 转为欧拉角 JObject
        /// </summary>
        public static JObject QuaternionToJson(Quaternion q)
        {
            var euler = q.eulerAngles;
            return new JObject
            {
                ["x"] = Math.Round(euler.x, 4),
                ["y"] = Math.Round(euler.y, 4),
                ["z"] = Math.Round(euler.z, 4)
            };
        }

        #endregion

        #region 组件操作

        /// <summary>
        /// 通过类型名称获取 System.Type
        /// 支持完整类型名和简短名称
        /// </summary>
        public static Type ResolveComponentType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
                return null;

            // 先尝试直接查找
            var type = Type.GetType(typeName);
            if (type != null) return type;

            // 在所有程序集中查找
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetType(typeName);
                if (type != null) return type;

                // 尝试 UnityEngine 命名空间
                type = assembly.GetType($"UnityEngine.{typeName}");
                if (type != null) return type;

                // 尝试 UnityEngine.UI 命名空间
                type = assembly.GetType($"UnityEngine.UI.{typeName}");
                if (type != null) return type;
            }

            return null;
        }

        /// <summary>
        /// 序列化组件信息为 JObject
        /// </summary>
        public static JObject SerializeComponent(Component component)
        {
            if (component == null) return null;

            var result = new JObject
            {
                ["type"] = component.GetType().Name,
                ["fullType"] = component.GetType().FullName,
                ["instanceId"] = component.GetInstanceID()
            };

            // 对于常见组件，提取关键属性
            if (component is Transform t)
            {
                result["position"] = Vector3ToJson(t.localPosition);
                result["rotation"] = QuaternionToJson(t.localRotation);
                result["scale"] = Vector3ToJson(t.localScale);
            }
            else if (component is MeshRenderer mr)
            {
                result["enabled"] = mr.enabled;
                if (mr.sharedMaterial != null)
                    result["material"] = mr.sharedMaterial.name;
            }
            else if (component is Collider col)
            {
                result["enabled"] = col.enabled;
                result["isTrigger"] = col.isTrigger;
            }
            else if (component is Rigidbody rb)
            {
                result["mass"] = rb.mass;
                result["useGravity"] = rb.useGravity;
                result["isKinematic"] = rb.isKinematic;
            }
            else if (component is Light light)
            {
                result["type"] = light.type.ToString();
                result["intensity"] = light.intensity;
                result["color"] = $"#{ColorUtility.ToHtmlStringRGBA(light.color)}";
            }
            else if (component is Camera cam)
            {
                result["fieldOfView"] = cam.fieldOfView;
                result["nearClipPlane"] = cam.nearClipPlane;
                result["farClipPlane"] = cam.farClipPlane;
            }

            return result;
        }

        /// <summary>
        /// 序列化 GameObject 基本信息
        /// </summary>
        public static JObject SerializeGameObject(GameObject go, bool includeComponents = false, bool includeChildren = false)
        {
            if (go == null) return null;

            var result = new JObject
            {
                ["name"] = go.name,
                ["instanceId"] = go.GetInstanceID(),
                ["activeSelf"] = go.activeSelf,
                ["activeInHierarchy"] = go.activeInHierarchy,
                ["tag"] = go.tag,
                ["layer"] = LayerMask.LayerToName(go.layer),
                ["isStatic"] = go.isStatic
            };

            // Transform 信息
            var t = go.transform;
            result["transform"] = new JObject
            {
                ["position"] = Vector3ToJson(t.localPosition),
                ["rotation"] = QuaternionToJson(t.localRotation),
                ["scale"] = Vector3ToJson(t.localScale),
                ["worldPosition"] = Vector3ToJson(t.position)
            };

            if (includeComponents)
            {
                var components = new JArray();
                foreach (var comp in go.GetComponents<Component>())
                {
                    if (comp == null) continue; // Missing script
                    components.Add(SerializeComponent(comp));
                }
                result["components"] = components;
            }

            if (includeChildren)
            {
                var children = new JArray();
                for (int i = 0; i < t.childCount; i++)
                {
                    var child = t.GetChild(i).gameObject;
                    children.Add(SerializeGameObject(child, false, false)); // 不递归
                }
                result["children"] = children;
            }

            return result;
        }

        #endregion

        #region 路径和资产

        /// <summary>
        /// 确保目录存在
        /// </summary>
        public static void EnsureDirectoryExists(string assetPath)
        {
            var directory = System.IO.Path.GetDirectoryName(assetPath);
            if (!string.IsNullOrEmpty(directory) && !System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
            }
        }

        /// <summary>
        /// 规范化资产路径（确保以 Assets/ 或 Packages/ 开头）。
        /// 对于已经是有效 Unity 资产路径的输入（如 "Assets", "Assets/Textures"）保持不变，
        /// 对于相对路径（如 "Textures/Enemy.png"）自动添加 "Assets/" 前缀。
        /// </summary>
        public static string NormalizeAssetPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;

            path = path.Replace("\\", "/").TrimEnd('/');

            // 精确等于根目录名称时直接返回
            if (path == "Assets" || path == "Packages") return path;

            if (!path.StartsWith("Assets/") && !path.StartsWith("Packages/"))
            {
                path = "Assets/" + path;
            }

            return path;
        }

        #endregion

        #region Undo 支持

        /// <summary>
        /// 记录对象修改（支持 Undo）
        /// </summary>
        public static void RecordUndo(UnityEngine.Object obj, string operationName)
        {
            if (obj != null)
            {
                Undo.RecordObject(obj, $"AgentCore: {operationName}");
            }
        }

        /// <summary>
        /// 注册创建的对象（支持 Undo）
        /// </summary>
        public static void RegisterCreatedObject(UnityEngine.Object obj, string operationName)
        {
            if (obj != null)
            {
                Undo.RegisterCreatedObjectUndo(obj, $"AgentCore: {operationName}");
            }
        }

        #endregion
    }
}
