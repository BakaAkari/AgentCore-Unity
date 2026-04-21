using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEditor;
using Newtonsoft.Json.Linq;
using AgentCore.Editor.Tools.Infrastructure;

namespace AgentCore.Editor.Tools.Native.Extended
{
    /// <summary>
    /// Manage Unity Input settings and input axes configuration.
    /// Provides CRUD operations for input axes and input system inspection.
    /// </summary>
    [AgentTool("manage_input",
        Description = "Manage Unity Input settings and input axes configuration",
        Category = "extended",
        RequiresMainThread = true)]
    public class ManageInputTool : IAgentTool
    {
        #region Schema

        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""list_axes"", ""get_axis"", ""add_axis"", ""remove_axis"", ""simulate_key""],
                    ""description"": ""Action to perform""
                },
                ""name"": {
                    ""type"": ""string"",
                    ""description"": ""Axis name (for get_axis, add_axis, remove_axis actions)""
                },
                ""descriptive_name"": {
                    ""type"": ""string"",
                    ""description"": ""Descriptive name for the axis (for add_axis action, optional)""
                },
                ""positive_button"": {
                    ""type"": ""string"",
                    ""description"": ""Positive button (for add_axis action, optional)""
                },
                ""negative_button"": {
                    ""type"": ""string"",
                    ""description"": ""Negative button (for add_axis action, optional)""
                },
                ""alt_positive_button"": {
                    ""type"": ""string"",
                    ""description"": ""Alternative positive button (for add_axis action, optional)""
                },
                ""alt_negative_button"": {
                    ""type"": ""string"",
                    ""description"": ""Alternative negative button (for add_axis action, optional)""
                },
                ""gravity"": {
                    ""type"": ""number"",
                    ""description"": ""Gravity for the axis (for add_axis action, optional)""
                },
                ""dead"": {
                    ""type"": ""number"",
                    ""description"": ""Dead zone for the axis (for add_axis action, optional)""
                },
                ""sensitivity"": {
                    ""type"": ""number"",
                    ""description"": ""Sensitivity for the axis (for add_axis action, optional)""
                },
                ""type"": {
                    ""type"": ""string"",
                    ""enum"": [""key_or_mouse"", ""mouse_movement"", ""joystick""],
                    ""description"": ""Axis type (for add_axis action, default: key_or_mouse)""
                },
                ""axis"": {
                    ""type"": ""string"",
                    ""enum"": [""x"", ""y"", ""3rd"", ""4th"", ""5th"", ""6th"", ""7th"", ""8th"", ""9th"", ""10th""],
                    ""description"": ""Axis of input (for add_axis action, optional)""
                },
                ""joy_num"": {
                    ""type"": ""integer"",
                    ""description"": ""Joystick number (for add_axis action, optional, 0 = all joysticks)""
                },
                ""key"": {
                    ""type"": ""string"",
                    ""description"": ""KeyCode name to simulate (for simulate_key action)""
                }
            },
            ""required"": [""action""]
        }");

        #endregion

        // Axis type mapping
        private static readonly Dictionary<string, int> AxisTypeMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "key_or_mouse", 0 },
            { "mouse_movement", 1 },
            { "joystick", 2 }
        };

        // Axis index mapping
        private static readonly Dictionary<string, int> AxisIndexMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "x", 0 },
            { "y", 1 },
            { "3rd", 2 },
            { "4th", 3 },
            { "5th", 4 },
            { "6th", 5 },
            { "7th", 6 },
            { "8th", 7 },
            { "9th", 8 },
            { "10th", 9 }
        };

        public ToolMetadata Metadata => new ToolMetadata(
            name: "manage_input",
            description: "Manage Unity Input settings and input axes configuration",
            category: "extended",
            parametersSchema: _parametersSchema,
            requiresMainThread: true
        );

        public Task<ToolResult> ExecuteAsync(JObject parameters, CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();
            ToolResponse response;

            try
            {
                var action = ToolHelpers.GetRequiredString(parameters, "action").ToLowerInvariant();

                switch (action)
                {
                    case "list_axes":
                        response = HandleListAxes();
                        break;
                    case "get_axis":
                        response = HandleGetAxis(parameters);
                        break;
                    case "add_axis":
                        response = HandleAddAxis(parameters);
                        break;
                    case "remove_axis":
                        response = HandleRemoveAxis(parameters);
                        break;
                    case "simulate_key":
                        response = HandleSimulateKey(parameters);
                        break;
                    default:
                        response = ToolResponse.Fail(
                            $"Unknown action: '{action}'. Valid actions: list_axes, get_axis, add_axis, remove_axis, simulate_key");
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

        private ToolResponse HandleListAxes()
        {
            var inputManager = GetInputManager();
            if (inputManager == null)
            {
                return ToolResponse.Fail("Failed to load InputManager asset.");
            }

            var axesProp = inputManager.FindProperty("m_Axes");
            if (axesProp == null)
            {
                return ToolResponse.Fail("Failed to find 'm_Axes' property in InputManager.");
            }

            var axesList = new JArray();
            for (int i = 0; i < axesProp.arraySize; i++)
            {
                var axisProp = axesProp.GetArrayElementAtIndex(i);
                var name = axisProp.FindPropertyRelative("m_Name")?.stringValue ?? "(unnamed)";
                var type = axisProp.FindPropertyRelative("type")?.intValue ?? 0;
                var positiveButton = axisProp.FindPropertyRelative("positiveButton")?.stringValue ?? "";
                var negativeButton = axisProp.FindPropertyRelative("negativeButton")?.stringValue ?? "";

                axesList.Add(new JObject
                {
                    ["index"] = i,
                    ["name"] = name,
                    ["type"] = GetAxisTypeName(type),
                    ["positive_button"] = positiveButton,
                    ["negative_button"] = negativeButton
                });
            }

            var data = new JObject
            {
                ["axes"] = axesList,
                ["count"] = axesProp.arraySize
            };

            return ToolResponse.OkWithData(data, $"Found {axesProp.arraySize} input axis/axes.");
        }

        private ToolResponse HandleGetAxis(JObject parameters)
        {
            var name = ToolHelpers.GetRequiredString(parameters, "name");

            var inputManager = GetInputManager();
            if (inputManager == null)
            {
                return ToolResponse.Fail("Failed to load InputManager asset.");
            }

            var axesProp = inputManager.FindProperty("m_Axes");
            if (axesProp == null)
            {
                return ToolResponse.Fail("Failed to find 'm_Axes' property in InputManager.");
            }

            // Find the axis by name (may have multiple entries with same name)
            var results = new JArray();
            for (int i = 0; i < axesProp.arraySize; i++)
            {
                var axisProp = axesProp.GetArrayElementAtIndex(i);
                var axisName = axisProp.FindPropertyRelative("m_Name")?.stringValue;

                if (string.Equals(axisName, name, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(SerializeAxis(axisProp, i));
                }
            }

            if (results.Count == 0)
            {
                return ToolResponse.Fail($"Input axis '{name}' not found.");
            }

            var data = new JObject
            {
                ["name"] = name,
                ["entries"] = results,
                ["entry_count"] = results.Count
            };

            return ToolResponse.OkWithData(data, $"Found {results.Count} entry/entries for axis '{name}'.");
        }

        private ToolResponse HandleAddAxis(JObject parameters)
        {
            var name = ToolHelpers.GetRequiredString(parameters, "name");

            var inputManager = GetInputManager();
            if (inputManager == null)
            {
                return ToolResponse.Fail("Failed to load InputManager asset.");
            }

            var axesProp = inputManager.FindProperty("m_Axes");
            if (axesProp == null)
            {
                return ToolResponse.Fail("Failed to find 'm_Axes' property in InputManager.");
            }

            // Add new axis entry
            axesProp.InsertArrayElementAtIndex(axesProp.arraySize);
            var newAxis = axesProp.GetArrayElementAtIndex(axesProp.arraySize - 1);

            // Set properties
            SetAxisProperty(newAxis, "m_Name", name);
            SetAxisProperty(newAxis, "descriptiveName", ToolHelpers.GetOptionalString(parameters, "descriptive_name", ""));
            SetAxisProperty(newAxis, "descriptiveNegativeName", "");
            SetAxisProperty(newAxis, "positiveButton", ToolHelpers.GetOptionalString(parameters, "positive_button", ""));
            SetAxisProperty(newAxis, "negativeButton", ToolHelpers.GetOptionalString(parameters, "negative_button", ""));
            SetAxisProperty(newAxis, "altPositiveButton", ToolHelpers.GetOptionalString(parameters, "alt_positive_button", ""));
            SetAxisProperty(newAxis, "altNegativeButton", ToolHelpers.GetOptionalString(parameters, "alt_negative_button", ""));

            // Numeric properties
            var gravityProp = newAxis.FindPropertyRelative("gravity");
            if (gravityProp != null)
                gravityProp.floatValue = ToolHelpers.GetOptionalFloat(parameters, "gravity", 0f);

            var deadProp = newAxis.FindPropertyRelative("dead");
            if (deadProp != null)
                deadProp.floatValue = ToolHelpers.GetOptionalFloat(parameters, "dead", 0.001f);

            var sensitivityProp = newAxis.FindPropertyRelative("sensitivity");
            if (sensitivityProp != null)
                sensitivityProp.floatValue = ToolHelpers.GetOptionalFloat(parameters, "sensitivity", 1f);

            // Snap and invert defaults
            var snapProp = newAxis.FindPropertyRelative("snap");
            if (snapProp != null)
                snapProp.boolValue = false;

            var invertProp = newAxis.FindPropertyRelative("invert");
            if (invertProp != null)
                invertProp.boolValue = false;

            // Type
            var typeStr = ToolHelpers.GetOptionalString(parameters, "type", "key_or_mouse");
            var typeProp = newAxis.FindPropertyRelative("type");
            if (typeProp != null && AxisTypeMap.TryGetValue(typeStr, out var typeValue))
            {
                typeProp.intValue = typeValue;
            }

            // Axis
            var axisStr = ToolHelpers.GetOptionalString(parameters, "axis", "x");
            var axisProp = newAxis.FindPropertyRelative("axis");
            if (axisProp != null && AxisIndexMap.TryGetValue(axisStr, out var axisValue))
            {
                axisProp.intValue = axisValue;
            }

            // Joy num
            var joyNumProp = newAxis.FindPropertyRelative("joyNum");
            if (joyNumProp != null)
                joyNumProp.intValue = ToolHelpers.GetOptionalInt(parameters, "joy_num", 0);

            inputManager.ApplyModifiedProperties();

            var data = new JObject
            {
                ["name"] = name,
                ["type"] = typeStr,
                ["positive_button"] = ToolHelpers.GetOptionalString(parameters, "positive_button", ""),
                ["negative_button"] = ToolHelpers.GetOptionalString(parameters, "negative_button", ""),
                ["total_axes"] = axesProp.arraySize
            };

            return ToolResponse.OkWithData(data, $"Input axis '{name}' added successfully.");
        }

        private ToolResponse HandleRemoveAxis(JObject parameters)
        {
            var name = ToolHelpers.GetRequiredString(parameters, "name");

            var inputManager = GetInputManager();
            if (inputManager == null)
            {
                return ToolResponse.Fail("Failed to load InputManager asset.");
            }

            var axesProp = inputManager.FindProperty("m_Axes");
            if (axesProp == null)
            {
                return ToolResponse.Fail("Failed to find 'm_Axes' property in InputManager.");
            }

            // Find and remove all entries with this name
            int removedCount = 0;
            for (int i = axesProp.arraySize - 1; i >= 0; i--)
            {
                var axisProp = axesProp.GetArrayElementAtIndex(i);
                var axisName = axisProp.FindPropertyRelative("m_Name")?.stringValue;

                if (string.Equals(axisName, name, StringComparison.OrdinalIgnoreCase))
                {
                    axesProp.DeleteArrayElementAtIndex(i);
                    removedCount++;
                }
            }

            if (removedCount == 0)
            {
                return ToolResponse.Fail($"Input axis '{name}' not found.");
            }

            inputManager.ApplyModifiedProperties();

            var data = new JObject
            {
                ["name"] = name,
                ["removed_count"] = removedCount,
                ["remaining_axes"] = axesProp.arraySize
            };

            return ToolResponse.OkWithData(data, $"Removed {removedCount} entry/entries for axis '{name}'.");
        }

        private ToolResponse HandleSimulateKey(JObject parameters)
        {
            var keyStr = ToolHelpers.GetRequiredString(parameters, "key");

            // Validate the KeyCode
            if (!Enum.TryParse<KeyCode>(keyStr, true, out var keyCode))
            {
                return ToolResponse.Fail(
                    $"Invalid KeyCode: '{keyStr}'. Use Unity KeyCode enum names like 'Space', 'A', 'LeftArrow', 'Mouse0', etc.");
            }

            // Unity does not provide a direct API for simulating input in the Editor.
            // Return guidance on how to handle this programmatically.
            var data = new JObject
            {
                ["key"] = keyStr,
                ["key_code"] = (int)keyCode,
                ["note"] = "Unity does not provide a direct input simulation API in the Editor. " +
                           "To test input in scripts, consider: " +
                           "1) Use Input.GetKey/GetKeyDown in Play mode with actual key presses. " +
                           "2) Create a custom input abstraction layer that can be mocked. " +
                           "3) Use Unity's Input System package (com.unity.inputsystem) which supports simulated input via InputTestFixture. " +
                           "4) For UI testing, use UnityEngine.EventSystems to send pointer/navigation events.",
                ["workaround_code"] = $"// In a test script using Input System package:\n" +
                                      $"// var keyboard = InputSystem.AddDevice<Keyboard>();\n" +
                                      $"// Press(keyboard.{keyStr.ToLowerInvariant()}Key);\n" +
                                      $"// Release(keyboard.{keyStr.ToLowerInvariant()}Key);"
            };

            return ToolResponse.OkWithData(data,
                $"Key simulation is not directly supported by Unity Editor API. See 'note' and 'workaround_code' for alternatives.");
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Get the InputManager SerializedObject.
        /// </summary>
        private static SerializedObject GetInputManager()
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/InputManager.asset");
            if (assets == null || assets.Length == 0)
                return null;

            return new SerializedObject(assets[0]);
        }

        /// <summary>
        /// Serialize an axis SerializedProperty to JObject.
        /// </summary>
        private static JObject SerializeAxis(SerializedProperty axisProp, int index)
        {
            return new JObject
            {
                ["index"] = index,
                ["name"] = axisProp.FindPropertyRelative("m_Name")?.stringValue ?? "",
                ["descriptive_name"] = axisProp.FindPropertyRelative("descriptiveName")?.stringValue ?? "",
                ["descriptive_negative_name"] = axisProp.FindPropertyRelative("descriptiveNegativeName")?.stringValue ?? "",
                ["negative_button"] = axisProp.FindPropertyRelative("negativeButton")?.stringValue ?? "",
                ["positive_button"] = axisProp.FindPropertyRelative("positiveButton")?.stringValue ?? "",
                ["alt_negative_button"] = axisProp.FindPropertyRelative("altNegativeButton")?.stringValue ?? "",
                ["alt_positive_button"] = axisProp.FindPropertyRelative("altPositiveButton")?.stringValue ?? "",
                ["gravity"] = axisProp.FindPropertyRelative("gravity")?.floatValue ?? 0f,
                ["dead"] = axisProp.FindPropertyRelative("dead")?.floatValue ?? 0f,
                ["sensitivity"] = axisProp.FindPropertyRelative("sensitivity")?.floatValue ?? 0f,
                ["snap"] = axisProp.FindPropertyRelative("snap")?.boolValue ?? false,
                ["invert"] = axisProp.FindPropertyRelative("invert")?.boolValue ?? false,
                ["type"] = GetAxisTypeName(axisProp.FindPropertyRelative("type")?.intValue ?? 0),
                ["axis"] = GetAxisName(axisProp.FindPropertyRelative("axis")?.intValue ?? 0),
                ["joy_num"] = axisProp.FindPropertyRelative("joyNum")?.intValue ?? 0
            };
        }

        /// <summary>
        /// Set a string property on an axis SerializedProperty.
        /// </summary>
        private static void SetAxisProperty(SerializedProperty axisProp, string propertyName, string value)
        {
            var prop = axisProp.FindPropertyRelative(propertyName);
            if (prop != null)
            {
                prop.stringValue = value ?? "";
            }
        }

        /// <summary>
        /// Get human-readable axis type name from int value.
        /// </summary>
        private static string GetAxisTypeName(int type)
        {
            switch (type)
            {
                case 0: return "key_or_mouse";
                case 1: return "mouse_movement";
                case 2: return "joystick";
                default: return $"unknown({type})";
            }
        }

        /// <summary>
        /// Get human-readable axis name from int value.
        /// </summary>
        private static string GetAxisName(int axis)
        {
            switch (axis)
            {
                case 0: return "x";
                case 1: return "y";
                case 2: return "3rd";
                case 3: return "4th";
                case 4: return "5th";
                case 5: return "6th";
                case 6: return "7th";
                case 7: return "8th";
                case 8: return "9th";
                case 9: return "10th";
                default: return $"axis_{axis}";
            }
        }

        #endregion
    }
}
