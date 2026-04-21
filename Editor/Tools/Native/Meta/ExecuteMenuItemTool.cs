using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Tools.Infrastructure;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Tools.Native.Meta
{
    /// <summary>
    /// Execute Unity Editor menu items by path, and list available menu items.
    /// </summary>
    [AgentTool("execute_menu_item",
        Description = "Execute Unity Editor menu items by path, and list available menu items",
        Category = "meta",
        RequiresMainThread = true)]
    public class ExecuteMenuItemTool : IAgentTool
    {
        #region Schema

        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""execute"", ""list"", ""validate""],
                    ""description"": ""Action to perform: execute a menu item, list common menu items, or validate a menu path""
                },
                ""menu_path"": {
                    ""type"": ""string"",
                    ""description"": ""Menu item path (e.g. 'GameObject/3D Object/Cube', 'Window/General/Console'). Required for execute and validate actions.""
                },
                ""category"": {
                    ""type"": ""string"",
                    ""enum"": [""file"", ""edit"", ""assets"", ""gameobject"", ""component"", ""window"", ""tools"", ""all""],
                    ""description"": ""Category filter for list action (default: all)""
                }
            },
            ""required"": [""action""]
        }");

        #endregion

        #region Menu Item Database

        private static readonly Dictionary<string, List<string>> _menuItems = new()
        {
            ["file"] = new List<string>
            {
                "File/New Scene",
                "File/Open Scene",
                "File/Save",
                "File/Save As...",
                "File/Build Settings..."
            },
            ["edit"] = new List<string>
            {
                "Edit/Undo",
                "Edit/Redo",
                "Edit/Select All",
                "Edit/Preferences...",
                "Edit/Project Settings..."
            },
            ["assets"] = new List<string>
            {
                "Assets/Create/Folder",
                "Assets/Create/C# Script",
                "Assets/Create/Material",
                "Assets/Refresh",
                "Assets/Import New Asset..."
            },
            ["gameobject"] = new List<string>
            {
                "GameObject/Create Empty",
                "GameObject/3D Object/Cube",
                "GameObject/3D Object/Sphere",
                "GameObject/3D Object/Capsule",
                "GameObject/3D Object/Cylinder",
                "GameObject/3D Object/Plane",
                "GameObject/Light/Directional Light",
                "GameObject/Light/Point Light",
                "GameObject/Camera",
                "GameObject/UI/Canvas",
                "GameObject/UI/Text",
                "GameObject/UI/Image",
                "GameObject/UI/Button"
            },
            ["component"] = new List<string>
            {
                "Component/Physics/Rigidbody",
                "Component/Physics/Box Collider",
                "Component/Audio/Audio Source"
            },
            ["window"] = new List<string>
            {
                "Window/General/Console",
                "Window/General/Inspector",
                "Window/General/Scene",
                "Window/General/Game",
                "Window/General/Hierarchy",
                "Window/General/Project",
                "Window/Analysis/Profiler"
            },
            ["tools"] = new List<string>()
        };

        #endregion

        public ToolMetadata Metadata => new ToolMetadata(
            name: "execute_menu_item",
            description: "Execute Unity Editor menu items by path, and list available menu items",
            category: "meta",
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
                    case "execute":
                        response = HandleExecute(parameters);
                        break;
                    case "list":
                        response = HandleList(parameters);
                        break;
                    case "validate":
                        response = HandleValidate(parameters);
                        break;
                    default:
                        response = ToolResponse.Fail(
                            $"Unknown action: '{action}'. Valid actions: execute, list, validate");
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

        private ToolResponse HandleExecute(JObject parameters)
        {
            var menuPath = ToolHelpers.GetRequiredString(parameters, "menu_path");

            bool success = EditorApplication.ExecuteMenuItem(menuPath);

            if (success)
            {
                return ToolResponse.OkWithData(new JObject
                {
                    ["menu_path"] = menuPath,
                    ["executed"] = true
                }, $"Successfully executed menu item: '{menuPath}'");
            }
            else
            {
                return ToolResponse.Fail(
                    $"Failed to execute menu item: '{menuPath}'. The menu item may not exist or may be disabled in the current context.");
            }
        }

        private ToolResponse HandleList(JObject parameters)
        {
            var category = ToolHelpers.GetOptionalString(parameters, "category", "all").ToLowerInvariant();

            var result = new JObject();

            if (category == "all")
            {
                foreach (var kvp in _menuItems)
                {
                    result[kvp.Key] = new JArray(kvp.Value.ToArray());
                }
            }
            else if (_menuItems.TryGetValue(category, out var items))
            {
                result[category] = new JArray(items.ToArray());
            }
            else
            {
                return ToolResponse.Fail(
                    $"Unknown category: '{category}'. Valid categories: file, edit, assets, gameobject, component, window, tools, all");
            }

            return ToolResponse.OkWithData(result, $"Menu items for category: {category}");
        }

        private ToolResponse HandleValidate(JObject parameters)
        {
            var menuPath = ToolHelpers.GetRequiredString(parameters, "menu_path");

            // Try to use internal Unity API via reflection to check menu item existence
            bool? exists = TryCheckMenuItemExists(menuPath);

            var data = new JObject
            {
                ["menu_path"] = menuPath
            };

            if (exists.HasValue)
            {
                data["exists"] = exists.Value;
                data["method"] = "internal_api";
                return ToolResponse.OkWithData(data,
                    exists.Value
                        ? $"Menu item '{menuPath}' exists."
                        : $"Menu item '{menuPath}' does not exist.");
            }
            else
            {
                // Check if it's in our known list
                bool inKnownList = false;
                foreach (var kvp in _menuItems)
                {
                    if (kvp.Value.Contains(menuPath))
                    {
                        inKnownList = true;
                        break;
                    }
                }

                data["in_known_list"] = inKnownList;
                data["method"] = "known_list_check";
                data["note"] = "Cannot definitively validate via internal API. " +
                               (inKnownList
                                   ? "This path is in the known menu items list."
                                   : "This path is not in the known list, but may still be valid. Try executing it directly.");

                return ToolResponse.OkWithData(data,
                    inKnownList
                        ? $"Menu item '{menuPath}' is in the known list."
                        : $"Cannot validate '{menuPath}'. Try executing it directly.");
            }
        }

        #endregion

        #region Internal Helpers

        /// <summary>
        /// Attempt to check menu item existence via Unity internal API (reflection).
        /// Returns null if the internal API is not accessible.
        /// </summary>
        private static bool? TryCheckMenuItemExists(string menuPath)
        {
            try
            {
                // Try UnityEditor.Menu internal class
                var menuType = typeof(EditorApplication).Assembly.GetType("UnityEditor.Menu");
                if (menuType != null)
                {
                    // Try MenuItemExists method
                    var existsMethod = menuType.GetMethod("MenuItemExists",
                        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                    if (existsMethod != null)
                    {
                        return (bool)existsMethod.Invoke(null, new object[] { menuPath });
                    }

                    // Try GetEnabled method as fallback (returns false for non-existent items)
                    var getEnabledMethod = menuType.GetMethod("GetEnabled",
                        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                    if (getEnabledMethod != null)
                    {
                        try
                        {
                            getEnabledMethod.Invoke(null, new object[] { menuPath });
                            return true; // If no exception, menu item exists
                        }
                        catch
                        {
                            return false;
                        }
                    }
                }
            }
            catch
            {
                // Reflection failed, return null to indicate we can't validate
            }

            return null;
        }

        #endregion
    }
}
