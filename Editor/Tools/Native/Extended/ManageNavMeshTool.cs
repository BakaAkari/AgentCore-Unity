using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using UnityEngine;
using UnityEngine.AI;
using UnityEditor;
using Newtonsoft.Json.Linq;
using AgentCore.Editor.Tools.Infrastructure;

namespace AgentCore.Editor.Tools.Native.Extended
{
    /// <summary>
    /// Manage NavMesh baking, agents, and navigation settings.
    /// Provides NavMesh bake/clear, agent/obstacle management, and area configuration.
    /// </summary>
    [AgentTool("manage_navmesh",
        Description = "Unity AI Navigation — NavMesh baking and agent/obstacle setup. " +
                      "Actions: bake (generate NavMesh for current scene), clear (remove baked NavMesh), " +
                      "get_settings (bake parameters: agent radius/height/slope/step), " +
                      "add_agent (attach NavMeshAgent to a GameObject), add_obstacle (attach NavMeshObstacle), set_area (configure navigation area costs). " +
                      "USE FOR: setting up AI pathfinding, baking walkable surfaces, configuring agent navigation parameters, " +
                      "adding obstacles that carve or block navigation. " +
                      "NOT FOR: runtime pathfinding queries (NavMesh.CalculatePath is runtime), NavMesh components from AI Navigation package v2. " +
                      "ACTIVATE WHEN: user mentions 'navmesh', 'navigation', 'pathfinding', 'bake navmesh', 'AI agent movement'.",
        Category = "extended",
        RequiresMainThread = true,
        Visibility = ToolVisibility.OnDemand)]
    public class ManageNavMeshTool : IAgentTool
    {
        #region Schema

        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""bake"", ""clear"", ""get_settings"", ""add_agent"", ""add_obstacle"", ""set_area""],
                    ""description"": ""Action to perform""
                },
                ""target"": {
                    ""type"": ""string"",
                    ""description"": ""Target GameObject name or path (for add_agent, add_obstacle, set_area actions)""
                },
                ""speed"": {
                    ""type"": ""number"",
                    ""description"": ""Agent speed (for add_agent action, default: 3.5)""
                },
                ""angular_speed"": {
                    ""type"": ""number"",
                    ""description"": ""Agent angular speed (for add_agent action, default: 120)""
                },
                ""acceleration"": {
                    ""type"": ""number"",
                    ""description"": ""Agent acceleration (for add_agent action, default: 8)""
                },
                ""stopping_distance"": {
                    ""type"": ""number"",
                    ""description"": ""Agent stopping distance (for add_agent action, default: 0)""
                },
                ""auto_braking"": {
                    ""type"": ""boolean"",
                    ""description"": ""Agent auto braking (for add_agent action, default: true)""
                },
                ""radius"": {
                    ""type"": ""number"",
                    ""description"": ""Agent or obstacle radius (for add_agent action, optional)""
                },
                ""height"": {
                    ""type"": ""number"",
                    ""description"": ""Agent or obstacle height (for add_agent action, optional)""
                },
                ""shape"": {
                    ""type"": ""string"",
                    ""enum"": [""box"", ""capsule""],
                    ""description"": ""Obstacle shape (for add_obstacle action, default: capsule)""
                },
                ""carve"": {
                    ""type"": ""boolean"",
                    ""description"": ""Whether obstacle carves the NavMesh (for add_obstacle action, default: true)""
                },
                ""size"": {
                    ""type"": ""object"",
                    ""properties"": {
                        ""x"": { ""type"": ""number"" },
                        ""y"": { ""type"": ""number"" },
                        ""z"": { ""type"": ""number"" }
                    },
                    ""description"": ""Obstacle size (for add_obstacle action, optional)""
                },
                ""center"": {
                    ""type"": ""object"",
                    ""properties"": {
                        ""x"": { ""type"": ""number"" },
                        ""y"": { ""type"": ""number"" },
                        ""z"": { ""type"": ""number"" }
                    },
                    ""description"": ""Obstacle center offset (for add_obstacle action, optional)""
                },
                ""area"": {
                    ""type"": ""string"",
                    ""description"": ""NavMesh area name: walkable, not_walkable, jump, or custom area name (for set_area action)""
                }
            },
            ""required"": [""action""]
        }");

        #endregion

        public ToolMetadata Metadata => new ToolMetadata(
            name: "manage_navmesh",
            description: "Manage NavMesh baking, agents, and navigation settings",
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
                    case "bake":
                        response = HandleBake();
                        break;
                    case "clear":
                        response = HandleClear();
                        break;
                    case "get_settings":
                        response = HandleGetSettings();
                        break;
                    case "add_agent":
                        response = HandleAddAgent(parameters);
                        break;
                    case "add_obstacle":
                        response = HandleAddObstacle(parameters);
                        break;
                    case "set_area":
                        response = HandleSetArea(parameters);
                        break;
                    default:
                        response = ToolResponse.Fail(
                            $"Unknown action: '{action}'. Valid actions: bake, clear, get_settings, add_agent, add_obstacle, set_area");
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

        private ToolResponse HandleBake()
        {
            try
            {
                // NavMeshBuilder is in UnityEditor.AI namespace, access via reflection for compatibility
                var navMeshBuilderType = Type.GetType("UnityEditor.AI.NavMeshBuilder, UnityEditor");
                if (navMeshBuilderType != null)
                {
                    var buildMethod = navMeshBuilderType.GetMethod("BuildNavMesh", BindingFlags.Public | BindingFlags.Static);
                    if (buildMethod != null)
                    {
                        buildMethod.Invoke(null, null);
                        return ToolResponse.Ok("NavMesh baked successfully.");
                    }
                }

                // Fallback: try direct call if the type is accessible
                // UnityEditor.AI.NavMeshBuilder.BuildNavMesh() may be directly available
                return ToolResponse.Fail("NavMeshBuilder.BuildNavMesh() not available. Ensure the AI Navigation package is installed.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Failed to bake NavMesh: {ex.Message}");
            }
        }

        private ToolResponse HandleClear()
        {
            try
            {
                var navMeshBuilderType = Type.GetType("UnityEditor.AI.NavMeshBuilder, UnityEditor");
                if (navMeshBuilderType != null)
                {
                    var clearMethod = navMeshBuilderType.GetMethod("ClearAllNavMeshes", BindingFlags.Public | BindingFlags.Static);
                    if (clearMethod != null)
                    {
                        clearMethod.Invoke(null, null);
                        return ToolResponse.Ok("All NavMeshes cleared successfully.");
                    }
                }

                return ToolResponse.Fail("NavMeshBuilder.ClearAllNavMeshes() not available. Ensure the AI Navigation package is installed.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Failed to clear NavMesh: {ex.Message}");
            }
        }

        private ToolResponse HandleGetSettings()
        {
            var data = new JObject();

            // Get NavMesh settings for the default agent type (index 0)
            var settings = NavMesh.GetSettingsByIndex(0);
            data["default_agent"] = new JObject
            {
                ["agent_type_id"] = settings.agentTypeID,
                ["agent_radius"] = Math.Round(settings.agentRadius, 4),
                ["agent_height"] = Math.Round(settings.agentHeight, 4),
                ["agent_slope"] = Math.Round(settings.agentSlope, 4),
                ["agent_climb"] = Math.Round(settings.agentClimb, 4)
            };

            // List all agent types
            int agentCount = NavMesh.GetSettingsCount();
            var agentTypes = new JArray();
            for (int i = 0; i < agentCount; i++)
            {
                var agentSettings = NavMesh.GetSettingsByIndex(i);
                var agentName = NavMesh.GetSettingsNameFromID(agentSettings.agentTypeID);
                agentTypes.Add(new JObject
                {
                    ["index"] = i,
                    ["name"] = agentName,
                    ["agent_type_id"] = agentSettings.agentTypeID,
                    ["radius"] = Math.Round(agentSettings.agentRadius, 4),
                    ["height"] = Math.Round(agentSettings.agentHeight, 4),
                    ["max_slope"] = Math.Round(agentSettings.agentSlope, 4),
                    ["step_height"] = Math.Round(agentSettings.agentClimb, 4)
                });
            }
            data["agent_types"] = agentTypes;
            data["agent_type_count"] = agentCount;

            // List NavMesh areas
            var areas = new JArray();
            for (int i = 0; i < 32; i++)
            {
                var areaName = NavMesh.GetAreaFromName(GetAreaNameByIndex(i)) >= 0
                    ? GetAreaNameByIndex(i)
                    : null;

                // Use GameObjectUtility to get area names
                var name = GetNavMeshAreaName(i);
                if (!string.IsNullOrEmpty(name))
                {
                    areas.Add(new JObject
                    {
                        ["index"] = i,
                        ["name"] = name,
                        ["cost"] = NavMesh.GetAreaCost(i)
                    });
                }
            }
            data["areas"] = areas;

            return ToolResponse.OkWithData(data, "NavMesh settings retrieved successfully.");
        }

        private ToolResponse HandleAddAgent(JObject parameters)
        {
            var targetName = ToolHelpers.GetRequiredString(parameters, "target");
            var go = ToolHelpers.FindGameObject(targetName);
            if (go == null)
            {
                return ToolResponse.Fail($"GameObject '{targetName}' not found.");
            }

            // Check if already has NavMeshAgent
            var existingAgent = go.GetComponent<NavMeshAgent>();
            if (existingAgent != null)
            {
                return ToolResponse.Fail($"GameObject '{targetName}' already has a NavMeshAgent component.");
            }

            Undo.RecordObject(go, "AgentCore: Add NavMeshAgent");
            var agent = Undo.AddComponent<NavMeshAgent>(go);

            // Configure agent properties
            agent.speed = ToolHelpers.GetOptionalFloat(parameters, "speed", 3.5f);
            agent.angularSpeed = ToolHelpers.GetOptionalFloat(parameters, "angular_speed", 120f);
            agent.acceleration = ToolHelpers.GetOptionalFloat(parameters, "acceleration", 8f);
            agent.stoppingDistance = ToolHelpers.GetOptionalFloat(parameters, "stopping_distance", 0f);
            agent.autoBraking = ToolHelpers.GetOptionalBool(parameters, "auto_braking", true);

            var radius = ToolHelpers.GetOptionalFloat(parameters, "radius", -1f);
            if (radius > 0) agent.radius = radius;

            var height = ToolHelpers.GetOptionalFloat(parameters, "height", -1f);
            if (height > 0) agent.height = height;

            var data = new JObject
            {
                ["target"] = go.name,
                ["speed"] = agent.speed,
                ["angular_speed"] = agent.angularSpeed,
                ["acceleration"] = agent.acceleration,
                ["stopping_distance"] = agent.stoppingDistance,
                ["auto_braking"] = agent.autoBraking,
                ["radius"] = agent.radius,
                ["height"] = agent.height
            };

            return ToolResponse.OkWithData(data, $"NavMeshAgent added to '{go.name}'.");
        }

        private ToolResponse HandleAddObstacle(JObject parameters)
        {
            var targetName = ToolHelpers.GetRequiredString(parameters, "target");
            var go = ToolHelpers.FindGameObject(targetName);
            if (go == null)
            {
                return ToolResponse.Fail($"GameObject '{targetName}' not found.");
            }

            // Check if already has NavMeshObstacle
            var existingObstacle = go.GetComponent<NavMeshObstacle>();
            if (existingObstacle != null)
            {
                return ToolResponse.Fail($"GameObject '{targetName}' already has a NavMeshObstacle component.");
            }

            Undo.RecordObject(go, "AgentCore: Add NavMeshObstacle");
            var obstacle = Undo.AddComponent<NavMeshObstacle>(go);

            // Configure shape
            var shapeStr = ToolHelpers.GetOptionalString(parameters, "shape", "capsule").ToLowerInvariant();
            obstacle.shape = shapeStr == "box"
                ? NavMeshObstacleShape.Box
                : NavMeshObstacleShape.Capsule;

            // Configure carving
            obstacle.carving = ToolHelpers.GetOptionalBool(parameters, "carve", true);

            // Configure size
            var sizeToken = parameters?["size"];
            if (sizeToken != null)
            {
                obstacle.size = ToolHelpers.ParseVector3(sizeToken, obstacle.size);
            }

            // Configure center
            var centerToken = parameters?["center"];
            if (centerToken != null)
            {
                obstacle.center = ToolHelpers.ParseVector3(centerToken, obstacle.center);
            }

            var data = new JObject
            {
                ["target"] = go.name,
                ["shape"] = obstacle.shape.ToString(),
                ["carving"] = obstacle.carving,
                ["size"] = ToolHelpers.Vector3ToJson(obstacle.size),
                ["center"] = ToolHelpers.Vector3ToJson(obstacle.center)
            };

            return ToolResponse.OkWithData(data, $"NavMeshObstacle added to '{go.name}'.");
        }

        private ToolResponse HandleSetArea(JObject parameters)
        {
            var targetName = ToolHelpers.GetRequiredString(parameters, "target");
            var areaName = ToolHelpers.GetRequiredString(parameters, "area");

            var go = ToolHelpers.FindGameObject(targetName);
            if (go == null)
            {
                return ToolResponse.Fail($"GameObject '{targetName}' not found.");
            }

            // Map common area names
            int areaIndex;
            switch (areaName.ToLowerInvariant())
            {
                case "walkable":
                    areaIndex = 0;
                    break;
                case "not_walkable":
                case "not walkable":
                    areaIndex = 1;
                    break;
                case "jump":
                    areaIndex = 2;
                    break;
                default:
                    // Try to find by name
                    areaIndex = NavMesh.GetAreaFromName(areaName);
                    if (areaIndex < 0)
                    {
                        return ToolResponse.Fail($"NavMesh area '{areaName}' not found. Built-in areas: walkable, not_walkable, jump.");
                    }
                    break;
            }

            Undo.RecordObject(go, "AgentCore: Set NavMesh Area");
            GameObjectUtility.SetNavMeshArea(go, areaIndex);

            var data = new JObject
            {
                ["target"] = go.name,
                ["area"] = areaName,
                ["area_index"] = areaIndex
            };

            return ToolResponse.OkWithData(data, $"NavMesh area set to '{areaName}' (index {areaIndex}) on '{go.name}'.");
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Get the built-in area name by index.
        /// </summary>
        private static string GetAreaNameByIndex(int index)
        {
            switch (index)
            {
                case 0: return "Walkable";
                case 1: return "Not Walkable";
                case 2: return "Jump";
                default: return null;
            }
        }

        /// <summary>
        /// Get NavMesh area name using serialized property access.
        /// </summary>
        private static string GetNavMeshAreaName(int index)
        {
            // Built-in areas
            switch (index)
            {
                case 0: return "Walkable";
                case 1: return "Not Walkable";
                case 2: return "Jump";
            }

            // User-defined areas (3-31) - try via SerializedObject on NavMeshProjectSettings
            try
            {
                var navMeshSettingsType = Type.GetType("UnityEditor.NavMeshEditorHelpers, UnityEditor");
                if (navMeshSettingsType != null)
                {
                    var getAreaNameMethod = navMeshSettingsType.GetMethod("GetAreaName", BindingFlags.Public | BindingFlags.Static);
                    if (getAreaNameMethod != null)
                    {
                        var name = getAreaNameMethod.Invoke(null, new object[] { index }) as string;
                        return string.IsNullOrEmpty(name) ? null : name;
                    }
                }

                // Fallback: try GameObjectUtility
                var names = GameObjectUtility.GetNavMeshAreaNames();
                foreach (var name in names)
                {
                    if (NavMesh.GetAreaFromName(name) == index)
                        return name;
                }
            }
            catch
            {
                // Silently ignore
            }

            return null;
        }

        #endregion
    }
}
