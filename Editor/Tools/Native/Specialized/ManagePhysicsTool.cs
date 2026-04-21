using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Tools.Infrastructure;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Tools.Native.Specialized
{
    /// <summary>
    /// Manage physics settings, colliders, rigidbodies, and joints.
    /// Directly calls Unity Physics API.
    /// </summary>
    [AgentTool("manage_physics",
        Description = "Manage physics settings, colliders, rigidbodies, and joints",
        Category = "specialized",
        RequiresMainThread = true)]
    public class ManagePhysicsTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""get_settings"", ""set_settings"", ""add_rigidbody"", ""add_collider"", ""add_joint"", ""raycast""],
                    ""description"": ""Action to perform""
                },
                ""target"": { ""type"": ""string"", ""description"": ""Target GameObject name"" },
                ""gravity"": {
                    ""type"": ""object"",
                    ""properties"": { ""x"": {""type"":""number""}, ""y"": {""type"":""number""}, ""z"": {""type"":""number""} },
                    ""description"": ""Gravity vector""
                },
                ""default_solver_iterations"": { ""type"": ""integer"", ""description"": ""Default solver iterations"" },
                ""default_solver_velocity_iterations"": { ""type"": ""integer"", ""description"": ""Default solver velocity iterations"" },
                ""bounce_threshold"": { ""type"": ""number"", ""description"": ""Bounce threshold"" },
                ""sleep_threshold"": { ""type"": ""number"", ""description"": ""Sleep threshold"" },
                ""default_contact_offset"": { ""type"": ""number"", ""description"": ""Default contact offset"" },
                ""auto_simulation"": { ""type"": ""boolean"", ""description"": ""Auto simulation enabled"" },
                ""mass"": { ""type"": ""number"", ""description"": ""Rigidbody mass (default: 1)"" },
                ""drag"": { ""type"": ""number"", ""description"": ""Rigidbody drag"" },
                ""angular_drag"": { ""type"": ""number"", ""description"": ""Rigidbody angular drag"" },
                ""use_gravity"": { ""type"": ""boolean"", ""description"": ""Use gravity (default: true)"" },
                ""is_kinematic"": { ""type"": ""boolean"", ""description"": ""Is kinematic (default: false)"" },
                ""constraints"": { ""type"": ""string"", ""description"": ""Rigidbody constraints, comma-separated (e.g. freeze_position_x,freeze_rotation_y)"" },
                ""type"": {
                    ""type"": ""string"",
                    ""enum"": [""box"", ""sphere"", ""capsule"", ""mesh"", ""fixed"", ""hinge"", ""spring"", ""character"", ""configurable""],
                    ""description"": ""Collider or joint type""
                },
                ""is_trigger"": { ""type"": ""boolean"", ""description"": ""Is trigger collider (default: false)"" },
                ""center"": {
                    ""type"": ""object"",
                    ""properties"": { ""x"": {""type"":""number""}, ""y"": {""type"":""number""}, ""z"": {""type"":""number""} },
                    ""description"": ""Collider center offset""
                },
                ""size"": {
                    ""type"": ""object"",
                    ""properties"": { ""x"": {""type"":""number""}, ""y"": {""type"":""number""}, ""z"": {""type"":""number""} },
                    ""description"": ""Box collider size""
                },
                ""radius"": { ""type"": ""number"", ""description"": ""Sphere/capsule collider radius"" },
                ""height"": { ""type"": ""number"", ""description"": ""Capsule collider height"" },
                ""connected_body"": { ""type"": ""string"", ""description"": ""Connected body GameObject name for joints"" },
                ""break_force"": { ""type"": ""number"", ""description"": ""Joint break force"" },
                ""break_torque"": { ""type"": ""number"", ""description"": ""Joint break torque"" },
                ""origin"": {
                    ""type"": ""object"",
                    ""properties"": { ""x"": {""type"":""number""}, ""y"": {""type"":""number""}, ""z"": {""type"":""number""} },
                    ""description"": ""Raycast origin""
                },
                ""direction"": {
                    ""type"": ""object"",
                    ""properties"": { ""x"": {""type"":""number""}, ""y"": {""type"":""number""}, ""z"": {""type"":""number""} },
                    ""description"": ""Raycast direction""
                },
                ""max_distance"": { ""type"": ""number"", ""description"": ""Raycast max distance"" },
                ""layer_mask"": { ""type"": ""string"", ""description"": ""Layer mask name for raycast"" }
            },
            ""required"": [""action""]
        }");

        public ToolMetadata Metadata => new ToolMetadata(
            name: "manage_physics",
            description: "Manage physics settings, colliders, rigidbodies, and joints",
            category: "specialized",
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
                    case "get_settings":
                        response = HandleGetSettings();
                        break;
                    case "set_settings":
                        response = HandleSetSettings(parameters);
                        break;
                    case "add_rigidbody":
                        response = HandleAddRigidbody(parameters);
                        break;
                    case "add_collider":
                        response = HandleAddCollider(parameters);
                        break;
                    case "add_joint":
                        response = HandleAddJoint(parameters);
                        break;
                    case "raycast":
                        response = HandleRaycast(parameters);
                        break;
                    default:
                        response = ToolResponse.Fail(
                            $"Unknown action: '{action}'. Valid actions: get_settings, set_settings, add_rigidbody, add_collider, add_joint, raycast");
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

        private ToolResponse HandleGetSettings()
        {
            var data = new JObject
            {
                ["gravity"] = ToolHelpers.Vector3ToJson(Physics.gravity),
                ["defaultSolverIterations"] = Physics.defaultSolverIterations,
                ["defaultSolverVelocityIterations"] = Physics.defaultSolverVelocityIterations,
                ["bounceThreshold"] = Physics.bounceThreshold,
                ["sleepThreshold"] = Physics.sleepThreshold,
                ["defaultContactOffset"] = Physics.defaultContactOffset,
                ["autoSimulation"] = Physics.simulationMode != SimulationMode.Script,
                ["defaultMaxAngularSpeed"] = Physics.defaultMaxAngularSpeed,
                ["queriesHitTriggers"] = Physics.queriesHitTriggers,
                ["queriesHitBackfaces"] = Physics.queriesHitBackfaces
            };

            return ToolResponse.OkWithData(data, "Physics settings retrieved.");
        }

        private ToolResponse HandleSetSettings(JObject parameters)
        {
            bool modified = false;

            var gravityToken = parameters["gravity"];
            if (gravityToken != null)
            {
                Physics.gravity = ToolHelpers.ParseVector3(gravityToken, Physics.gravity);
                modified = true;
            }

            if (parameters["default_solver_iterations"] != null)
            {
                Physics.defaultSolverIterations = ToolHelpers.GetOptionalInt(parameters, "default_solver_iterations", Physics.defaultSolverIterations);
                modified = true;
            }

            if (parameters["default_solver_velocity_iterations"] != null)
            {
                Physics.defaultSolverVelocityIterations = ToolHelpers.GetOptionalInt(parameters, "default_solver_velocity_iterations", Physics.defaultSolverVelocityIterations);
                modified = true;
            }

            if (parameters["bounce_threshold"] != null)
            {
                Physics.bounceThreshold = ToolHelpers.GetOptionalFloat(parameters, "bounce_threshold", Physics.bounceThreshold);
                modified = true;
            }

            if (parameters["sleep_threshold"] != null)
            {
                Physics.sleepThreshold = ToolHelpers.GetOptionalFloat(parameters, "sleep_threshold", Physics.sleepThreshold);
                modified = true;
            }

            if (parameters["default_contact_offset"] != null)
            {
                Physics.defaultContactOffset = ToolHelpers.GetOptionalFloat(parameters, "default_contact_offset", Physics.defaultContactOffset);
                modified = true;
            }

            if (parameters["auto_simulation"] != null)
            {
                bool autoSim = ToolHelpers.GetOptionalBool(parameters, "auto_simulation", true);
                Physics.simulationMode = autoSim ? SimulationMode.FixedUpdate : SimulationMode.Script;
                modified = true;
            }

            if (!modified)
                return ToolResponse.Fail("No settings parameters provided to modify.");

            return ToolResponse.Ok("Physics settings updated.");
        }

        private ToolResponse HandleAddRigidbody(JObject parameters)
        {
            var targetName = ToolHelpers.GetRequiredString(parameters, "target");
            var go = ToolHelpers.FindGameObject(targetName);
            if (go == null)
                return ToolResponse.Fail($"GameObject '{targetName}' not found.");

            // Check if already has Rigidbody
            if (go.GetComponent<Rigidbody>() != null)
                return ToolResponse.Fail($"GameObject '{targetName}' already has a Rigidbody component.");

            ToolHelpers.RecordUndo(go, "Add Rigidbody");
            var rb = Undo.AddComponent<Rigidbody>(go);

            rb.mass = ToolHelpers.GetOptionalFloat(parameters, "mass", 1f);

            if (parameters["drag"] != null)
                rb.linearDamping = ToolHelpers.GetOptionalFloat(parameters, "drag", 0f);

            if (parameters["angular_drag"] != null)
                rb.angularDamping = ToolHelpers.GetOptionalFloat(parameters, "angular_drag", 0.05f);

            rb.useGravity = ToolHelpers.GetOptionalBool(parameters, "use_gravity", true);
            rb.isKinematic = ToolHelpers.GetOptionalBool(parameters, "is_kinematic", false);

            // Parse constraints
            var constraintsStr = ToolHelpers.GetOptionalString(parameters, "constraints");
            if (!string.IsNullOrEmpty(constraintsStr))
            {
                rb.constraints = ParseConstraints(constraintsStr);
            }

            EditorUtility.SetDirty(go);

            var data = new JObject
            {
                ["target"] = go.name,
                ["mass"] = rb.mass,
                ["drag"] = rb.linearDamping,
                ["angularDrag"] = rb.angularDamping,
                ["useGravity"] = rb.useGravity,
                ["isKinematic"] = rb.isKinematic,
                ["constraints"] = rb.constraints.ToString()
            };

            return ToolResponse.OkWithData(data, $"Rigidbody added to '{targetName}'.");
        }

        private ToolResponse HandleAddCollider(JObject parameters)
        {
            var targetName = ToolHelpers.GetRequiredString(parameters, "target");
            var go = ToolHelpers.FindGameObject(targetName);
            if (go == null)
                return ToolResponse.Fail($"GameObject '{targetName}' not found.");

            var typeStr = ToolHelpers.GetRequiredString(parameters, "type").ToLowerInvariant();
            var isTrigger = ToolHelpers.GetOptionalBool(parameters, "is_trigger", false);

            ToolHelpers.RecordUndo(go, "Add Collider");
            Collider collider;

            switch (typeStr)
            {
                case "box":
                {
                    var box = Undo.AddComponent<BoxCollider>(go);
                    box.isTrigger = isTrigger;
                    var centerToken = parameters["center"];
                    if (centerToken != null)
                        box.center = ToolHelpers.ParseVector3(centerToken);
                    var sizeToken = parameters["size"];
                    if (sizeToken != null)
                        box.size = ToolHelpers.ParseVector3(sizeToken, Vector3.one);
                    collider = box;
                    break;
                }
                case "sphere":
                {
                    var sphere = Undo.AddComponent<SphereCollider>(go);
                    sphere.isTrigger = isTrigger;
                    var centerToken = parameters["center"];
                    if (centerToken != null)
                        sphere.center = ToolHelpers.ParseVector3(centerToken);
                    if (parameters["radius"] != null)
                        sphere.radius = ToolHelpers.GetOptionalFloat(parameters, "radius", 0.5f);
                    collider = sphere;
                    break;
                }
                case "capsule":
                {
                    var capsule = Undo.AddComponent<CapsuleCollider>(go);
                    capsule.isTrigger = isTrigger;
                    var centerToken = parameters["center"];
                    if (centerToken != null)
                        capsule.center = ToolHelpers.ParseVector3(centerToken);
                    if (parameters["radius"] != null)
                        capsule.radius = ToolHelpers.GetOptionalFloat(parameters, "radius", 0.5f);
                    if (parameters["height"] != null)
                        capsule.height = ToolHelpers.GetOptionalFloat(parameters, "height", 2f);
                    collider = capsule;
                    break;
                }
                case "mesh":
                {
                    var meshFilter = go.GetComponent<MeshFilter>();
                    if (meshFilter == null || meshFilter.sharedMesh == null)
                        return ToolResponse.Fail($"GameObject '{targetName}' needs a MeshFilter with a mesh for MeshCollider.");
                    var mesh = Undo.AddComponent<MeshCollider>(go);
                    mesh.isTrigger = isTrigger;
                    collider = mesh;
                    break;
                }
                default:
                    return ToolResponse.Fail($"Invalid collider type: '{typeStr}'. Valid: box, sphere, capsule, mesh");
            }

            EditorUtility.SetDirty(go);

            var data = new JObject
            {
                ["target"] = go.name,
                ["colliderType"] = collider.GetType().Name,
                ["isTrigger"] = collider.isTrigger
            };

            return ToolResponse.OkWithData(data, $"{collider.GetType().Name} added to '{targetName}'.");
        }

        private ToolResponse HandleAddJoint(JObject parameters)
        {
            var targetName = ToolHelpers.GetRequiredString(parameters, "target");
            var go = ToolHelpers.FindGameObject(targetName);
            if (go == null)
                return ToolResponse.Fail($"GameObject '{targetName}' not found.");

            var typeStr = ToolHelpers.GetRequiredString(parameters, "type").ToLowerInvariant();

            ToolHelpers.RecordUndo(go, "Add Joint");
            Joint joint;

            switch (typeStr)
            {
                case "fixed":
                    joint = Undo.AddComponent<FixedJoint>(go);
                    break;
                case "hinge":
                    joint = Undo.AddComponent<HingeJoint>(go);
                    break;
                case "spring":
                    joint = Undo.AddComponent<SpringJoint>(go);
                    break;
                case "character":
                    joint = Undo.AddComponent<CharacterJoint>(go);
                    break;
                case "configurable":
                    joint = Undo.AddComponent<ConfigurableJoint>(go);
                    break;
                default:
                    return ToolResponse.Fail($"Invalid joint type: '{typeStr}'. Valid: fixed, hinge, spring, character, configurable");
            }

            // Connected body
            var connectedBodyName = ToolHelpers.GetOptionalString(parameters, "connected_body");
            if (!string.IsNullOrEmpty(connectedBodyName))
            {
                var connectedGo = ToolHelpers.FindGameObject(connectedBodyName);
                if (connectedGo == null)
                    return ToolResponse.Fail($"Connected body GameObject '{connectedBodyName}' not found.");
                var connectedRb = connectedGo.GetComponent<Rigidbody>();
                if (connectedRb == null)
                    return ToolResponse.Fail($"Connected body '{connectedBodyName}' does not have a Rigidbody.");
                joint.connectedBody = connectedRb;
            }

            if (parameters["break_force"] != null)
                joint.breakForce = ToolHelpers.GetOptionalFloat(parameters, "break_force", Mathf.Infinity);

            if (parameters["break_torque"] != null)
                joint.breakTorque = ToolHelpers.GetOptionalFloat(parameters, "break_torque", Mathf.Infinity);

            EditorUtility.SetDirty(go);

            var data = new JObject
            {
                ["target"] = go.name,
                ["jointType"] = joint.GetType().Name,
                ["connectedBody"] = joint.connectedBody != null ? joint.connectedBody.gameObject.name : null,
                ["breakForce"] = joint.breakForce,
                ["breakTorque"] = joint.breakTorque
            };

            return ToolResponse.OkWithData(data, $"{joint.GetType().Name} added to '{targetName}'.");
        }

        private ToolResponse HandleRaycast(JObject parameters)
        {
            var originToken = parameters["origin"];
            if (originToken == null)
                return ToolResponse.Fail("Parameter 'origin' is required for raycast.");

            var directionToken = parameters["direction"];
            if (directionToken == null)
                return ToolResponse.Fail("Parameter 'direction' is required for raycast.");

            var origin = ToolHelpers.ParseVector3(originToken);
            var direction = ToolHelpers.ParseVector3(directionToken);

            if (direction == Vector3.zero)
                return ToolResponse.Fail("Raycast direction cannot be zero.");

            var maxDistance = ToolHelpers.GetOptionalFloat(parameters, "max_distance", Mathf.Infinity);

            int layerMask = -1; // Everything
            var layerMaskStr = ToolHelpers.GetOptionalString(parameters, "layer_mask");
            if (!string.IsNullOrEmpty(layerMaskStr))
            {
                int layer = LayerMask.NameToLayer(layerMaskStr);
                if (layer == -1)
                    return ToolResponse.Fail($"Layer '{layerMaskStr}' not found.");
                layerMask = 1 << layer;
            }

            RaycastHit hit;
            bool didHit = Physics.Raycast(origin, direction.normalized, out hit, maxDistance, layerMask);

            var data = new JObject
            {
                ["hit"] = didHit,
                ["origin"] = ToolHelpers.Vector3ToJson(origin),
                ["direction"] = ToolHelpers.Vector3ToJson(direction),
                ["maxDistance"] = maxDistance
            };

            if (didHit)
            {
                data["hitPoint"] = ToolHelpers.Vector3ToJson(hit.point);
                data["hitNormal"] = ToolHelpers.Vector3ToJson(hit.normal);
                data["hitDistance"] = Math.Round(hit.distance, 4);
                data["hitCollider"] = hit.collider != null ? hit.collider.gameObject.name : null;
                data["hitColliderType"] = hit.collider != null ? hit.collider.GetType().Name : null;
                data["hitInstanceId"] = hit.collider != null ? hit.collider.gameObject.GetInstanceID() : 0;
            }

            return ToolResponse.OkWithData(data, didHit ? $"Raycast hit '{hit.collider?.gameObject.name}' at distance {hit.distance:F2}." : "Raycast did not hit anything.");
        }

        #endregion

        #region Helpers

        private static RigidbodyConstraints ParseConstraints(string constraintsStr)
        {
            RigidbodyConstraints result = RigidbodyConstraints.None;
            var parts = constraintsStr.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var part in parts)
            {
                switch (part.Trim().ToLowerInvariant())
                {
                    case "freeze_position_x": result |= RigidbodyConstraints.FreezePositionX; break;
                    case "freeze_position_y": result |= RigidbodyConstraints.FreezePositionY; break;
                    case "freeze_position_z": result |= RigidbodyConstraints.FreezePositionZ; break;
                    case "freeze_rotation_x": result |= RigidbodyConstraints.FreezeRotationX; break;
                    case "freeze_rotation_y": result |= RigidbodyConstraints.FreezeRotationY; break;
                    case "freeze_rotation_z": result |= RigidbodyConstraints.FreezeRotationZ; break;
                    case "freeze_position": result |= RigidbodyConstraints.FreezePosition; break;
                    case "freeze_rotation": result |= RigidbodyConstraints.FreezeRotation; break;
                    case "freeze_all": result |= RigidbodyConstraints.FreezeAll; break;
                }
            }

            return result;
        }

        #endregion
    }
}
