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
        Visibility = ToolVisibility.OnDemand,
        RequiresMainThread = true)]
    public class ManagePhysicsTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""get_settings"", ""set_settings"", ""add_rigidbody"", ""add_collider"", ""add_joint"", ""raycast"", ""add_constant_force"", ""configure_collision"", ""add_trigger_zone"", ""overlap_test""],
                    ""description"": ""Action to perform""
                },
                ""target"": { ""type"": ""string"", ""description"": ""Target GameObject name"" },
                ""name"": { ""type"": ""string"", ""description"": ""Name for created objects (add_trigger_zone)"" },
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
                ""shape"": {
                    ""type"": ""string"",
                    ""enum"": [""box"", ""sphere"", ""capsule""],
                    ""description"": ""Shape for trigger zone or overlap test""
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
                    ""description"": ""Box collider size or overlap box half-extents""
                },
                ""radius"": { ""type"": ""number"", ""description"": ""Sphere/capsule collider radius or overlap sphere radius"" },
                ""height"": { ""type"": ""number"", ""description"": ""Capsule collider height"" },
                ""position"": {
                    ""type"": ""object"",
                    ""properties"": { ""x"": {""type"":""number""}, ""y"": {""type"":""number""}, ""z"": {""type"":""number""} },
                    ""description"": ""Position for trigger zone or overlap test""
                },
                ""connected_body"": { ""type"": ""string"", ""description"": ""Connected body GameObject name for joints"" },
                ""break_force"": { ""type"": ""number"", ""description"": ""Joint break force"" },
                ""break_torque"": { ""type"": ""number"", ""description"": ""Joint break torque"" },
                ""force"": {
                    ""type"": ""object"",
                    ""properties"": { ""x"": {""type"":""number""}, ""y"": {""type"":""number""}, ""z"": {""type"":""number""} },
                    ""description"": ""Force vector for ConstantForce""
                },
                ""torque"": {
                    ""type"": ""object"",
                    ""properties"": { ""x"": {""type"":""number""}, ""y"": {""type"":""number""}, ""z"": {""type"":""number""} },
                    ""description"": ""Torque vector for ConstantForce""
                },
                ""relative_force"": {
                    ""type"": ""object"",
                    ""properties"": { ""x"": {""type"":""number""}, ""y"": {""type"":""number""}, ""z"": {""type"":""number""} },
                    ""description"": ""Relative force vector for ConstantForce""
                },
                ""relative_torque"": {
                    ""type"": ""object"",
                    ""properties"": { ""x"": {""type"":""number""}, ""y"": {""type"":""number""}, ""z"": {""type"":""number""} },
                    ""description"": ""Relative torque vector for ConstantForce""
                },
                ""layer1"": { ""type"": ""string"", ""description"": ""First layer name or index for collision configuration"" },
                ""layer2"": { ""type"": ""string"", ""description"": ""Second layer name or index for collision configuration"" },
                ""ignore"": { ""type"": ""boolean"", ""description"": ""Whether to ignore collision between layers (default: true)"" },
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
                ""layer_mask"": { ""type"": ""string"", ""description"": ""Layer mask name for raycast or overlap test"" }
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
                    case "add_constant_force":
                        response = HandleAddConstantForce(parameters);
                        break;
                    case "configure_collision":
                        response = HandleConfigureCollision(parameters);
                        break;
                    case "add_trigger_zone":
                        response = HandleAddTriggerZone(parameters);
                        break;
                    case "overlap_test":
                        response = HandleOverlapTest(parameters);
                        break;
                    default:
                        response = ToolResponse.Fail(
                            $"Unknown action: '{action}'. Valid actions: get_settings, set_settings, add_rigidbody, add_collider, add_joint, raycast, add_constant_force, configure_collision, add_trigger_zone, overlap_test");
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
                rb.drag = ToolHelpers.GetOptionalFloat(parameters, "drag", 0f);

            if (parameters["angular_drag"] != null)
                rb.angularDrag = ToolHelpers.GetOptionalFloat(parameters, "angular_drag", 0.05f);

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
                ["drag"] = rb.drag,
                ["angularDrag"] = rb.angularDrag,
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

        /// <summary>
        /// Add a ConstantForce component to a GameObject with specified force and torque vectors.
        /// Requires a Rigidbody on the target (will add one if missing).
        /// </summary>
        private ToolResponse HandleAddConstantForce(JObject parameters)
        {
            var targetName = ToolHelpers.GetRequiredString(parameters, "target");
            var go = ToolHelpers.FindGameObject(targetName);
            if (go == null)
                return ToolResponse.Fail($"GameObject '{targetName}' not found.");

            // ConstantForce requires Rigidbody
            var rb = go.GetComponent<Rigidbody>();
            if (rb == null)
            {
                ToolHelpers.RecordUndo(go, "Add Rigidbody for ConstantForce");
                rb = Undo.AddComponent<Rigidbody>(go);
            }

            // Check if already has ConstantForce
            var existing = go.GetComponent<ConstantForce>();
            if (existing != null)
                return ToolResponse.Fail($"GameObject '{targetName}' already has a ConstantForce component.");

            ToolHelpers.RecordUndo(go, "Add ConstantForce");
            var cf = Undo.AddComponent<ConstantForce>(go);

            var forceToken = parameters["force"];
            if (forceToken != null)
                cf.force = ToolHelpers.ParseVector3(forceToken);

            var torqueToken = parameters["torque"];
            if (torqueToken != null)
                cf.torque = ToolHelpers.ParseVector3(torqueToken);

            var relForceToken = parameters["relative_force"];
            if (relForceToken != null)
                cf.relativeForce = ToolHelpers.ParseVector3(relForceToken);

            var relTorqueToken = parameters["relative_torque"];
            if (relTorqueToken != null)
                cf.relativeTorque = ToolHelpers.ParseVector3(relTorqueToken);

            EditorUtility.SetDirty(go);

            var data = new JObject
            {
                ["target"] = go.name,
                ["force"] = ToolHelpers.Vector3ToJson(cf.force),
                ["torque"] = ToolHelpers.Vector3ToJson(cf.torque),
                ["relativeForce"] = ToolHelpers.Vector3ToJson(cf.relativeForce),
                ["relativeTorque"] = ToolHelpers.Vector3ToJson(cf.relativeTorque)
            };

            return ToolResponse.OkWithData(data, $"ConstantForce added to '{targetName}'.");
        }

        /// <summary>
        /// Configure the physics collision layer matrix using Physics.IgnoreLayerCollision.
        /// </summary>
        private ToolResponse HandleConfigureCollision(JObject parameters)
        {
            var layer1Str = ToolHelpers.GetRequiredString(parameters, "layer1");
            var layer2Str = ToolHelpers.GetRequiredString(parameters, "layer2");
            var ignore = ToolHelpers.GetOptionalBool(parameters, "ignore", true);

            int layer1 = ResolveLayer(layer1Str);
            if (layer1 < 0)
                return ToolResponse.Fail($"Layer '{layer1Str}' not found. Provide a valid layer name or index (0-31).");

            int layer2 = ResolveLayer(layer2Str);
            if (layer2 < 0)
                return ToolResponse.Fail($"Layer '{layer2Str}' not found. Provide a valid layer name or index (0-31).");

            Physics.IgnoreLayerCollision(layer1, layer2, ignore);

            var data = new JObject
            {
                ["layer1"] = LayerMask.LayerToName(layer1),
                ["layer1Index"] = layer1,
                ["layer2"] = LayerMask.LayerToName(layer2),
                ["layer2Index"] = layer2,
                ["ignoreCollision"] = ignore
            };

            return ToolResponse.OkWithData(data, ignore
                ? $"Collision between layer '{LayerMask.LayerToName(layer1)}' and '{LayerMask.LayerToName(layer2)}' is now ignored."
                : $"Collision between layer '{LayerMask.LayerToName(layer1)}' and '{LayerMask.LayerToName(layer2)}' is now enabled.");
        }

        /// <summary>
        /// Create a trigger zone — a new GameObject with a trigger collider at the specified position.
        /// </summary>
        private ToolResponse HandleAddTriggerZone(JObject parameters)
        {
            var name = ToolHelpers.GetOptionalString(parameters, "name", "TriggerZone");
            var shapeStr = ToolHelpers.GetOptionalString(parameters, "shape", "box").ToLowerInvariant();

            var go = new GameObject(name);
            ToolHelpers.RegisterCreatedObject(go, "Create Trigger Zone");

            // Set position
            var posToken = parameters["position"];
            if (posToken != null)
                go.transform.position = ToolHelpers.ParseVector3(posToken);

            Collider collider;

            switch (shapeStr)
            {
                case "box":
                {
                    var box = go.AddComponent<BoxCollider>();
                    box.isTrigger = true;
                    var sizeToken = parameters["size"];
                    if (sizeToken != null)
                        box.size = ToolHelpers.ParseVector3(sizeToken, Vector3.one);
                    collider = box;
                    break;
                }
                case "sphere":
                {
                    var sphere = go.AddComponent<SphereCollider>();
                    sphere.isTrigger = true;
                    if (parameters["radius"] != null)
                        sphere.radius = ToolHelpers.GetOptionalFloat(parameters, "radius", 0.5f);
                    collider = sphere;
                    break;
                }
                case "capsule":
                {
                    var capsule = go.AddComponent<CapsuleCollider>();
                    capsule.isTrigger = true;
                    if (parameters["radius"] != null)
                        capsule.radius = ToolHelpers.GetOptionalFloat(parameters, "radius", 0.5f);
                    if (parameters["height"] != null)
                        capsule.height = ToolHelpers.GetOptionalFloat(parameters, "height", 2f);
                    collider = capsule;
                    break;
                }
                default:
                    UnityEngine.Object.DestroyImmediate(go);
                    return ToolResponse.Fail($"Invalid shape: '{shapeStr}'. Valid: box, sphere, capsule");
            }

            EditorUtility.SetDirty(go);

            var data = new JObject
            {
                ["name"] = go.name,
                ["instanceId"] = go.GetInstanceID(),
                ["shape"] = shapeStr,
                ["isTrigger"] = true,
                ["position"] = ToolHelpers.Vector3ToJson(go.transform.position),
                ["colliderType"] = collider.GetType().Name
            };

            return ToolResponse.OkWithData(data, $"Trigger zone '{name}' ({shapeStr}) created.");
        }

        /// <summary>
        /// Perform an overlap test at a position using Physics.OverlapSphere or Physics.OverlapBox.
        /// Returns all colliders found within the overlap volume.
        /// </summary>
        private ToolResponse HandleOverlapTest(JObject parameters)
        {
            var posToken = parameters["position"];
            if (posToken == null)
                return ToolResponse.Fail("Parameter 'position' is required for overlap_test.");

            var position = ToolHelpers.ParseVector3(posToken);
            var shapeStr = ToolHelpers.GetOptionalString(parameters, "shape", "sphere").ToLowerInvariant();

            int layerMask = -1; // Everything
            var layerMaskStr = ToolHelpers.GetOptionalString(parameters, "layer_mask");
            if (!string.IsNullOrEmpty(layerMaskStr))
            {
                int layer = LayerMask.NameToLayer(layerMaskStr);
                if (layer == -1)
                    return ToolResponse.Fail($"Layer '{layerMaskStr}' not found.");
                layerMask = 1 << layer;
            }

            Collider[] results;

            switch (shapeStr)
            {
                case "sphere":
                {
                    float radius = ToolHelpers.GetOptionalFloat(parameters, "radius", 1f);
                    results = Physics.OverlapSphere(position, radius, layerMask);
                    break;
                }
                case "box":
                {
                    var sizeToken = parameters["size"];
                    Vector3 halfExtents = sizeToken != null
                        ? ToolHelpers.ParseVector3(sizeToken, Vector3.one * 0.5f)
                        : Vector3.one * 0.5f;
                    results = Physics.OverlapBox(position, halfExtents, Quaternion.identity, layerMask);
                    break;
                }
                default:
                    return ToolResponse.Fail($"Invalid shape: '{shapeStr}'. Valid: sphere, box");
            }

            var colliderArray = new JArray();
            foreach (var col in results)
            {
                if (col == null) continue;
                colliderArray.Add(new JObject
                {
                    ["name"] = col.gameObject.name,
                    ["instanceId"] = col.gameObject.GetInstanceID(),
                    ["colliderType"] = col.GetType().Name,
                    ["isTrigger"] = col.isTrigger,
                    ["layer"] = LayerMask.LayerToName(col.gameObject.layer)
                });
            }

            var data = new JObject
            {
                ["position"] = ToolHelpers.Vector3ToJson(position),
                ["shape"] = shapeStr,
                ["hitCount"] = colliderArray.Count,
                ["colliders"] = colliderArray
            };

            return ToolResponse.OkWithData(data, $"Overlap test found {colliderArray.Count} collider(s).");
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Resolve a layer from either a name string or an integer index string.
        /// </summary>
        private static int ResolveLayer(string layerStr)
        {
            // Try parsing as integer first
            if (int.TryParse(layerStr, out int layerIndex))
            {
                if (layerIndex >= 0 && layerIndex <= 31)
                    return layerIndex;
                return -1;
            }

            // Try as layer name
            int layer = LayerMask.NameToLayer(layerStr);
            return layer;
        }

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
