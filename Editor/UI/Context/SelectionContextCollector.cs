using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.UI.Context
{
    /// <summary>
    /// 采集 Unity Hierarchy 当前选中的 GameObject（支持单选/多选）。
    /// - 单个 GO：完整组件列表 + SerializedProperty 字段快照
    /// - 20 个以内：每个 GO 名称 + 组件类型 + 头部字段
    /// - 20-100 个：只列名称 + 组件类型
    /// - &gt;100 个：只列前 100 名称 + "N more" 提示
    /// </summary>
    public static class SelectionContextCollector
    {
        /// <summary>
        /// 尝试从 Selection.gameObjects 采集。为空返回 <see cref="ContextIngestResult.Empty"/>。
        /// </summary>
        public static ContextIngestResult Collect()
        {
            var gameObjects = Selection.gameObjects;
            if (gameObjects == null || gameObjects.Length == 0)
                return ContextIngestResult.Empty();

            var count = gameObjects.Length;
            var label = BuildLabel(gameObjects);

            var sb = new StringBuilder(1024);

            // 头部元数据
            sb.Append("Total selected: ").Append(count).Append(" GameObject");
            if (count > 1) sb.Append('s');
            sb.Append('\n');

            if (count <= ContextIngestLimits.SelectionDetailLimit)
            {
                // 详细模式：每个 GO 完整组件字段
                for (int i = 0; i < count; i++)
                {
                    AppendGameObjectDetailed(sb, gameObjects[i], includeFields: true);
                    if (i < count - 1) sb.Append('\n');
                }
            }
            else if (count <= ContextIngestLimits.SelectionNameLimit)
            {
                // 中量：只列名称 + 组件类型
                for (int i = 0; i < count; i++)
                {
                    AppendGameObjectDetailed(sb, gameObjects[i], includeFields: false);
                    if (i < count - 1) sb.Append('\n');
                }
            }
            else
            {
                // 大量：只列前 N 个名称
                for (int i = 0; i < ContextIngestLimits.SelectionNameLimit; i++)
                {
                    var go = gameObjects[i];
                    sb.Append("- ").Append(BuildHierarchyPath(go)).Append('\n');
                }
                sb.Append("...(and ").Append(count - ContextIngestLimits.SelectionNameLimit).Append(" more)\n");

                return ContextIngestResult.OkWithWarning(
                    label,
                    sb.ToString(),
                    $"Selection has {count} GameObjects; only names of first {ContextIngestLimits.SelectionNameLimit} are shown. Reduce selection for detailed component info.",
                    truncated: true);
            }

            return ContextIngestResult.Ok(label, sb.ToString());
        }

        // ---------- Label 构建 ----------

        private static string BuildLabel(GameObject[] gos)
        {
            if (gos.Length == 1) return $"Selection: {gos[0].name}";
            if (gos.Length == 2) return $"Selection: {gos[0].name}, {gos[1].name}";
            return $"Selection: {gos[0].name}, {gos[1].name} (+{gos.Length - 2} more)";
        }

        // ---------- 单个 GO 采集 ----------

        private static void AppendGameObjectDetailed(StringBuilder sb, GameObject go, bool includeFields)
        {
            if (go == null) return;

            sb.Append("### ").Append(BuildHierarchyPath(go)).Append('\n');
            sb.Append("Active: ").Append(go.activeInHierarchy)
              .Append(" | Layer: ").Append(LayerMask.LayerToName(go.layer))
              .Append(" | Tag: ").Append(go.tag).Append('\n');

            var components = go.GetComponents<Component>();
            if (components == null || components.Length == 0)
            {
                sb.Append("(no components)\n");
                return;
            }

            sb.Append("Components:\n");
            foreach (var comp in components)
            {
                if (comp == null)
                {
                    sb.Append("  - <missing script>\n");
                    continue;
                }

                var typeName = comp.GetType().Name;
                sb.Append("  - ").Append(typeName);

                if (includeFields)
                {
                    var fields = CollectSerializedFields(comp);
                    if (!string.IsNullOrEmpty(fields))
                    {
                        sb.Append(" {\n").Append(fields).Append("    }");
                    }
                }
                sb.Append('\n');
            }
        }

        // ---------- SerializedProperty 字段采集 ----------

        /// <summary>
        /// 用 SerializedObject 遍历一个 Component 的顶层字段，格式化为多行 "key: value"。
        /// - 不递归深入嵌套结构（复杂结构只显示类型名）
        /// - 值截断到 <see cref="ContextIngestLimits.FieldValueMaxLength"/>
        /// - 最多列出 <see cref="ContextIngestLimits.ComponentFieldMaxCount"/> 个字段
        /// </summary>
        private static string CollectSerializedFields(Component comp)
        {
            SerializedObject so = null;
            try
            {
                so = new SerializedObject(comp);
            }
            catch
            {
                return null;
            }

            var sb = new StringBuilder();
            int fieldCount = 0;

            var iter = so.GetIterator();
            // 进入首个属性
            bool enterChildren = true;
            while (iter.NextVisible(enterChildren))
            {
                enterChildren = false; // 只遍历顶层

                // 跳过 Unity 内置字段
                var propName = iter.name;
                if (propName == "m_Script" || propName == "m_ObjectHideFlags" ||
                    propName == "m_CorrespondingSourceObject" || propName == "m_PrefabInstance" ||
                    propName == "m_PrefabAsset" || propName == "m_GameObject" ||
                    propName == "m_Enabled" || propName == "m_EditorHideFlags" ||
                    propName == "m_Name" || propName == "m_EditorClassIdentifier")
                    continue;

                var value = FormatPropertyValue(iter);
                sb.Append("      ").Append(propName).Append(": ").Append(value).Append('\n');

                fieldCount++;
                if (fieldCount >= ContextIngestLimits.ComponentFieldMaxCount)
                {
                    sb.Append("      ...(more fields)\n");
                    break;
                }
            }

            so.Dispose();

            return fieldCount == 0 ? null : sb.ToString();
        }

        private static string FormatPropertyValue(SerializedProperty prop)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer: return prop.intValue.ToString();
                case SerializedPropertyType.Boolean: return prop.boolValue.ToString();
                case SerializedPropertyType.Float: return prop.floatValue.ToString("G");
                case SerializedPropertyType.String:
                    return ContextIngestFormatter.TruncateValue(
                        "\"" + (prop.stringValue ?? string.Empty) + "\"",
                        ContextIngestLimits.FieldValueMaxLength);
                case SerializedPropertyType.Color: return prop.colorValue.ToString();
                case SerializedPropertyType.ObjectReference:
                    var refObj = prop.objectReferenceValue;
                    return refObj == null ? "null" : $"<{refObj.GetType().Name}: {refObj.name}>";
                case SerializedPropertyType.LayerMask: return prop.intValue.ToString();
                case SerializedPropertyType.Enum:
                    var idx = prop.enumValueIndex;
                    return (idx >= 0 && idx < prop.enumNames.Length) ? prop.enumNames[idx] : idx.ToString();
                case SerializedPropertyType.Vector2: return prop.vector2Value.ToString();
                case SerializedPropertyType.Vector3: return prop.vector3Value.ToString();
                case SerializedPropertyType.Vector4: return prop.vector4Value.ToString();
                case SerializedPropertyType.Rect: return prop.rectValue.ToString();
                case SerializedPropertyType.Bounds: return prop.boundsValue.ToString();
                case SerializedPropertyType.Quaternion: return prop.quaternionValue.eulerAngles.ToString();
                case SerializedPropertyType.AnimationCurve:
                    var curve = prop.animationCurveValue;
                    return curve == null ? "null" : $"<AnimationCurve: {curve.length} keys>";
                case SerializedPropertyType.Gradient: return "<Gradient>";
                case SerializedPropertyType.ArraySize: return prop.intValue.ToString();
                case SerializedPropertyType.Generic:
                    if (prop.isArray) return $"<array[{prop.arraySize}]>";
                    return "<complex>";
                default:
                    return "<" + prop.propertyType + ">";
            }
        }

        // ---------- Hierarchy 路径 ----------

        /// <summary>
        /// 构建从 root 到当前 GO 的完整 Hierarchy 路径。
        /// 例如 "Environment/Buildings/Warehouse01"。
        /// </summary>
        public static string BuildHierarchyPath(GameObject go)
        {
            if (go == null) return "<null>";
            if (go.transform.parent == null) return go.name;

            var parts = new List<string>();
            var t = go.transform;
            while (t != null)
            {
                parts.Add(t.name);
                t = t.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }
    }
}
