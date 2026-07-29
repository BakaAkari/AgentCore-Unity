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
        Description = "Unity Physics system — Rigidbody, Colliders, Joints, global physics settings, and spatial queries. " +
                      "Actions: get_settings/set_settings (gravity, default solver iterations, layer collision matrix), " +
                      "add_rigidbody (with mass/drag/constraints), add_collider (Box/Sphere/Capsule/Mesh with auto-size or custom), " +
                      "add_joint (Fixed/Hinge/Spring/Configurable with anchor/limits), " +
                      "raycast (single or all hits, 3D or 2D via dimension= ; layer_mask can be int, string, or string[]), " +
                      "add_constant_force, configure_collision (layer matrix on/off), add_trigger_zone, " +
                      "overlap_test (sphere/box/capsule with optional orientation; 3D or 2D), " +
                      "list_scene_physics_stats (rigidbody / collider / trigger / per-layer counts — S2 perf diagnostics), " +
                      "get_collision_matrix (32x32 layer collision matrix full snapshot). " +
                      "USE FOR: setting up physics on objects, configuring colliders to match geometry, " +
                      "creating joints between objects, testing raycasts for level design verification, adjusting global gravity, " +
                      "diagnosing scene physics performance (list_scene_physics_stats), auditing layer collision setup (get_collision_matrix). " +
                      "NOT FOR: runtime physics simulation (Editor raycasts work but simulation requires Play mode). " +
                      "ACTIVATE WHEN: user mentions 'physics', 'collider', 'rigidbody', 'joint', 'gravity', 'raycast', 'trigger', 'collision', 'physics debug', 'layer matrix'.",
        Category = "Specialized",
        Visibility = ToolVisibility.OnDemand,
        RequiresMainThread = true)]
    public class ManagePhysicsTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""get_settings"", ""set_settings"", ""add_rigidbody"", ""add_collider"", ""add_joint"", ""raycast"", ""add_constant_force"", ""configure_collision"", ""add_trigger_zone"", ""overlap_test"", ""list_scene_physics_stats"", ""get_collision_matrix""],
                    ""description"": ""Action to perform""
                },
                ""mode"": {
                    ""type"": ""string"",
                    ""enum"": [""single"", ""all""],
                    ""description"": ""(raycast) 'single' = first hit only (default); 'all' = all hits via RaycastAll.""
                },
                ""dimension"": {
                    ""type"": ""string"",
                    ""enum"": [""3d"", ""2d""],
                    ""description"": ""(raycast, overlap_test, get_collision_matrix) '3d' (default) uses Physics; '2d' uses Physics2D. For 2D projects.""
                },
                ""query_trigger_interaction"": {
                    ""type"": ""string"",
                    ""enum"": [""collide"", ""ignore"", ""use_global""],
                    ""description"": ""(raycast, overlap_test) Whether to hit trigger colliders. Default: use_global (Physics.queriesHitTriggers)""
                },
                ""orientation"": {
                    ""type"": ""object"",
                    ""properties"": { ""x"": {""type"":""number""}, ""y"": {""type"":""number""}, ""z"": {""type"":""number""}, ""w"": {""type"":""number""} },
                    ""description"": ""(overlap_test box/capsule) Quaternion orientation. If omitted, uses Quaternion.identity.""
                },
                ""half_extents"": {
                    ""type"": ""object"",
                    ""properties"": { ""x"": {""type"":""number""}, ""y"": {""type"":""number""}, ""z"": {""type"":""number""} },
                    ""description"": ""(overlap_test box) Half-extents alias for legacy 'size' param (they mean the same). Use whichever you prefer.""
                },
                ""center"": {
                    ""type"": ""object"",
                    ""properties"": { ""x"": {""type"":""number""}, ""y"": {""type"":""number""}, ""z"": {""type"":""number""} },
                    ""description"": ""(add_collider, overlap_test) Collider center offset OR overlap query center (legacy alias for 'position' in overlap_test).""
                },
                ""point0"": {
                    ""type"": ""object"",
                    ""properties"": { ""x"": {""type"":""number""}, ""y"": {""type"":""number""}, ""z"": {""type"":""number""} },
                    ""description"": ""(overlap_test capsule) First capsule endpoint (world space).""
                },
                ""point1"": {
                    ""type"": ""object"",
                    ""properties"": { ""x"": {""type"":""number""}, ""y"": {""type"":""number""}, ""z"": {""type"":""number""} },
                    ""description"": ""(overlap_test capsule) Second capsule endpoint (world space).""
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
                ""layer_mask"": {
                    ""description"": ""(raycast, overlap_test) Layer mask. Accepts: single layer name (string, e.g. 'Default'), array of layer names (string[], e.g. ['Default','Enemy']), integer bitmask (e.g. 5 = layer 0 | layer 2), or 'everything'/-1. Legacy single-string form still supported.""
                }
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
                    case "list_scene_physics_stats":
                        response = HandleListScenePhysicsStats(parameters);
                        break;
                    case "get_collision_matrix":
                        response = HandleGetCollisionMatrix(parameters);
                        break;
                    default:
                        response = ToolResponse.Fail(
                            $"Unknown action: '{action}'. Valid actions: get_settings, set_settings, add_rigidbody, add_collider, add_joint, raycast, add_constant_force, configure_collision, add_trigger_zone, overlap_test, list_scene_physics_stats, get_collision_matrix");
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
            var mode = ToolHelpers.GetOptionalString(parameters, "mode", "single").ToLowerInvariant();
            if (mode != "single" && mode != "all")
                return ToolResponse.Fail($"Invalid 'mode': '{mode}'. Valid: single, all.");

            var dimension = ToolHelpers.GetOptionalString(parameters, "dimension", "3d").ToLowerInvariant();
            if (dimension != "3d" && dimension != "2d")
                return ToolResponse.Fail($"Invalid 'dimension': '{dimension}'. Valid: 3d, 2d.");

            var qtiStr = ToolHelpers.GetOptionalString(parameters, "query_trigger_interaction", "use_global").ToLowerInvariant();
            if (!TryParseQueryTriggerInteraction(qtiStr, out var qti, out var qtiErr))
                return ToolResponse.Fail(qtiErr);

            if (!TryParseLayerMask(parameters["layer_mask"], out var layerMask, out var lmErr))
                return ToolResponse.Fail(lmErr);

            var data = new JObject
            {
                ["origin"] = ToolHelpers.Vector3ToJson(origin),
                ["direction"] = ToolHelpers.Vector3ToJson(direction),
                ["maxDistance"] = maxDistance,
                ["mode"] = mode,
                ["dimension"] = dimension,
                ["query_trigger_interaction"] = qtiStr
            };

            if (dimension == "2d")
            {
                Vector2 origin2d = new Vector2(origin.x, origin.y);
                Vector2 dir2d = new Vector2(direction.x, direction.y);
                if (dir2d == Vector2.zero)
                    return ToolResponse.Fail("Raycast2D direction (xy) cannot be zero.");
                dir2d = dir2d.normalized;

                if (mode == "all")
                {
                    var hits = Physics2D.RaycastAll(origin2d, dir2d, maxDistance, layerMask);
                    var hitsArray = new JArray();
                    foreach (var h in hits)
                    {
                        if (h.collider == null) continue;
                        hitsArray.Add(SerializeRaycastHit2D(h));
                    }
                    data["hit"] = hitsArray.Count > 0;
                    data["hits"] = hitsArray;
                    data["hit_count"] = hitsArray.Count;
                    return ToolResponse.OkWithData(data, $"Raycast2D (all) hit {hitsArray.Count} collider(s).");
                }
                else
                {
                    var h = Physics2D.Raycast(origin2d, dir2d, maxDistance, layerMask);
                    data["hit"] = h.collider != null;
                    if (h.collider != null)
                    {
                        AppendHit2DFields(data, h);
                    }
                    return ToolResponse.OkWithData(data,
                        h.collider != null
                            ? $"Raycast2D hit '{h.collider.gameObject.name}' at distance {h.distance:F2}."
                            : "Raycast2D did not hit anything.");
                }
            }

            // 3D
            var dirN = direction.normalized;
            if (mode == "all")
            {
                var hits = Physics.RaycastAll(origin, dirN, maxDistance, layerMask, qti);
                var hitsArray = new JArray();
                foreach (var h in hits)
                {
                    if (h.collider == null) continue;
                    hitsArray.Add(SerializeRaycastHit(h));
                }
                data["hit"] = hitsArray.Count > 0;
                data["hits"] = hitsArray;
                data["hit_count"] = hitsArray.Count;
                return ToolResponse.OkWithData(data, $"Raycast (all) hit {hitsArray.Count} collider(s).");
            }
            else
            {
                bool didHit = Physics.Raycast(origin, dirN, out var hit, maxDistance, layerMask, qti);
                data["hit"] = didHit;
                if (didHit)
                {
                    // v1.7.16 legacy fields kept for compatibility
                    data["hitPoint"] = ToolHelpers.Vector3ToJson(hit.point);
                    data["hitNormal"] = ToolHelpers.Vector3ToJson(hit.normal);
                    data["hitDistance"] = Math.Round(hit.distance, 4);
                    data["hitCollider"] = hit.collider != null ? hit.collider.gameObject.name : null;
                    data["hitColliderType"] = hit.collider != null ? hit.collider.GetType().Name : null;
                    data["hitInstanceId"] = hit.collider != null ? hit.collider.gameObject.GetInstanceID() : 0;
                }
                return ToolResponse.OkWithData(data,
                    didHit
                        ? $"Raycast hit '{hit.collider?.gameObject.name}' at distance {hit.distance:F2}."
                        : "Raycast did not hit anything.");
            }
        }

        private static JObject SerializeRaycastHit(RaycastHit h)
        {
            return new JObject
            {
                ["point"] = ToolHelpers.Vector3ToJson(h.point),
                ["normal"] = ToolHelpers.Vector3ToJson(h.normal),
                ["distance"] = Math.Round(h.distance, 4),
                ["collider"] = h.collider != null ? h.collider.gameObject.name : null,
                ["collider_type"] = h.collider != null ? h.collider.GetType().Name : null,
                ["instance_id"] = h.collider != null ? h.collider.gameObject.GetInstanceID() : 0,
                ["is_trigger"] = h.collider != null && h.collider.isTrigger,
                ["layer"] = h.collider != null ? LayerMask.LayerToName(h.collider.gameObject.layer) : null
            };
        }

        private static JObject SerializeRaycastHit2D(RaycastHit2D h)
        {
            return new JObject
            {
                ["point"] = new JObject { ["x"] = h.point.x, ["y"] = h.point.y },
                ["normal"] = new JObject { ["x"] = h.normal.x, ["y"] = h.normal.y },
                ["distance"] = Math.Round(h.distance, 4),
                ["fraction"] = Math.Round(h.fraction, 4),
                ["collider"] = h.collider != null ? h.collider.gameObject.name : null,
                ["collider_type"] = h.collider != null ? h.collider.GetType().Name : null,
                ["instance_id"] = h.collider != null ? h.collider.gameObject.GetInstanceID() : 0,
                ["is_trigger"] = h.collider != null && h.collider.isTrigger,
                ["layer"] = h.collider != null ? LayerMask.LayerToName(h.collider.gameObject.layer) : null
            };
        }

        private static void AppendHit2DFields(JObject data, RaycastHit2D h)
        {
            data["hitPoint"] = new JObject { ["x"] = h.point.x, ["y"] = h.point.y };
            data["hitNormal"] = new JObject { ["x"] = h.normal.x, ["y"] = h.normal.y };
            data["hitDistance"] = Math.Round(h.distance, 4);
            data["hitFraction"] = Math.Round(h.fraction, 4);
            data["hitCollider"] = h.collider != null ? h.collider.gameObject.name : null;
            data["hitColliderType"] = h.collider != null ? h.collider.GetType().Name : null;
            data["hitInstanceId"] = h.collider != null ? h.collider.gameObject.GetInstanceID() : 0;
        }

        /// <summary>
        /// v1.9.5 (G05): Parse 'layer_mask' param supporting single string, string[], int, or 'everything'/-1.
        /// Returns bitmask (int). Layer '-1' / null / omitted / 'everything' → all layers (mask = ~0).
        /// </summary>
        private static bool TryParseLayerMask(JToken token, out int mask, out string error)
        {
            error = null;
            if (token == null || token.Type == JTokenType.Null)
            {
                mask = ~0; // Everything
                return true;
            }

            switch (token.Type)
            {
                case JTokenType.Integer:
                    mask = token.Value<int>();
                    return true;

                case JTokenType.String:
                {
                    var s = token.Value<string>()?.Trim();
                    if (string.IsNullOrEmpty(s))
                    {
                        mask = ~0;
                        return true;
                    }
                    if (string.Equals(s, "everything", StringComparison.OrdinalIgnoreCase))
                    {
                        mask = ~0;
                        return true;
                    }
                    if (int.TryParse(s, out var intMask))
                    {
                        mask = intMask;
                        return true;
                    }
                    var layer = LayerMask.NameToLayer(s);
                    if (layer == -1)
                    {
                        mask = 0;
                        error = $"Layer '{s}' not found.";
                        return false;
                    }
                    mask = 1 << layer;
                    return true;
                }

                case JTokenType.Array:
                {
                    int accum = 0;
                    foreach (var item in (JArray)token)
                    {
                        if (item == null || item.Type == JTokenType.Null) continue;
                        if (item.Type == JTokenType.Integer)
                        {
                            accum |= item.Value<int>();
                            continue;
                        }
                        var name = item.Value<string>()?.Trim();
                        if (string.IsNullOrEmpty(name)) continue;
                        if (string.Equals(name, "everything", StringComparison.OrdinalIgnoreCase))
                        {
                            mask = ~0;
                            return true;
                        }
                        var layer = LayerMask.NameToLayer(name);
                        if (layer == -1)
                        {
                            mask = 0;
                            error = $"Layer '{name}' not found in array.";
                            return false;
                        }
                        accum |= (1 << layer);
                    }
                    mask = accum;
                    return true;
                }

                default:
                    mask = 0;
                    error = $"'layer_mask' has unsupported type '{token.Type}'. Use string, string[], or integer.";
                    return false;
            }
        }

        private static bool TryParseQueryTriggerInteraction(string s, out QueryTriggerInteraction qti, out string error)
        {
            error = null;
            switch (s)
            {
                case "collide":
                    qti = QueryTriggerInteraction.Collide;
                    return true;
                case "ignore":
                    qti = QueryTriggerInteraction.Ignore;
                    return true;
                case "use_global":
                case "":
                case null:
                    qti = QueryTriggerInteraction.UseGlobal;
                    return true;
                default:
                    qti = QueryTriggerInteraction.UseGlobal;
                    error = $"Invalid 'query_trigger_interaction': '{s}'. Valid: collide, ignore, use_global.";
                    return false;
            }
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
            // v1.9.5 (G05): dimension (3d/2d), capsule shape, orientation, half_extents alias, query_trigger_interaction, richer layer_mask.
            // Legacy: 'position' / 'size' kept as primary field names; 'center' / 'half_extents' are aliases.
            var posToken = parameters["position"] ?? parameters["center"];
            if (posToken == null)
                return ToolResponse.Fail("Parameter 'position' (or 'center') is required for overlap_test.");

            var position = ToolHelpers.ParseVector3(posToken);
            var shapeStr = ToolHelpers.GetOptionalString(parameters, "shape", "sphere").ToLowerInvariant();

            var dimension = ToolHelpers.GetOptionalString(parameters, "dimension", "3d").ToLowerInvariant();
            if (dimension != "3d" && dimension != "2d")
                return ToolResponse.Fail($"Invalid 'dimension': '{dimension}'. Valid: 3d, 2d.");

            var qtiStr = ToolHelpers.GetOptionalString(parameters, "query_trigger_interaction", "use_global").ToLowerInvariant();
            if (!TryParseQueryTriggerInteraction(qtiStr, out var qti, out var qtiErr))
                return ToolResponse.Fail(qtiErr);

            if (!TryParseLayerMask(parameters["layer_mask"], out var layerMask, out var lmErr))
                return ToolResponse.Fail(lmErr);

            var data = new JObject
            {
                ["position"] = ToolHelpers.Vector3ToJson(position),
                ["shape"] = shapeStr,
                ["dimension"] = dimension,
                ["query_trigger_interaction"] = qtiStr
            };

            if (dimension == "2d")
            {
                Vector2 pos2 = new Vector2(position.x, position.y);
                Collider2D[] hits2d;
                switch (shapeStr)
                {
                    case "sphere":
                    case "circle":
                    {
                        float radius = ToolHelpers.GetOptionalFloat(parameters, "radius", 1f);
                        hits2d = Physics2D.OverlapCircleAll(pos2, radius, layerMask);
                        break;
                    }
                    case "box":
                    {
                        var sizeToken = parameters["size"] ?? parameters["half_extents"];
                        Vector3 halfExtents = sizeToken != null
                            ? ToolHelpers.ParseVector3(sizeToken, Vector3.one * 0.5f)
                            : Vector3.one * 0.5f;
                        Vector2 boxSize = new Vector2(halfExtents.x * 2f, halfExtents.y * 2f); // OverlapBox2D takes size (not half)
                        float angleZ = 0f;
                        var orientToken = parameters["orientation"];
                        if (orientToken != null && orientToken.Type == JTokenType.Object)
                        {
                            // Interpret Quaternion → Euler.z for 2D
                            var q = ParseQuaternion(orientToken);
                            angleZ = q.eulerAngles.z;
                        }
                        hits2d = Physics2D.OverlapBoxAll(pos2, boxSize, angleZ, layerMask);
                        break;
                    }
                    default:
                        return ToolResponse.Fail($"Invalid shape for dimension=2d: '{shapeStr}'. Valid: sphere/circle, box.");
                }

                var arr = new JArray();
                foreach (var col in hits2d)
                {
                    if (col == null) continue;
                    arr.Add(new JObject
                    {
                        ["name"] = col.gameObject.name,
                        ["instanceId"] = col.gameObject.GetInstanceID(),
                        ["colliderType"] = col.GetType().Name,
                        ["isTrigger"] = col.isTrigger,
                        ["layer"] = LayerMask.LayerToName(col.gameObject.layer)
                    });
                }
                data["hitCount"] = arr.Count;
                data["colliders"] = arr;
                return ToolResponse.OkWithData(data, $"Overlap2D test found {arr.Count} collider(s).");
            }

            // 3D
            Collider[] results;
            switch (shapeStr)
            {
                case "sphere":
                {
                    float radius = ToolHelpers.GetOptionalFloat(parameters, "radius", 1f);
                    results = Physics.OverlapSphere(position, radius, layerMask, qti);
                    break;
                }
                case "box":
                {
                    var sizeToken = parameters["size"] ?? parameters["half_extents"];
                    Vector3 halfExtents = sizeToken != null
                        ? ToolHelpers.ParseVector3(sizeToken, Vector3.one * 0.5f)
                        : Vector3.one * 0.5f;
                    Quaternion orientation = Quaternion.identity;
                    var orientToken = parameters["orientation"];
                    if (orientToken != null && orientToken.Type == JTokenType.Object)
                    {
                        orientation = ParseQuaternion(orientToken);
                    }
                    results = Physics.OverlapBox(position, halfExtents, orientation, layerMask, qti);
                    break;
                }
                case "capsule":
                {
                    var p0Token = parameters["point0"];
                    var p1Token = parameters["point1"];
                    if (p0Token == null || p1Token == null)
                    {
                        // Fallback: use 'position' as center + orientation Y-axis vertical + 'height'/'radius' to synthesize point0/point1.
                        // This is convenience for callers that don't want to compute two endpoints manually.
                        var height = ToolHelpers.GetOptionalFloat(parameters, "height", 2f);
                        var radiusC = ToolHelpers.GetOptionalFloat(parameters, "radius", 0.5f);
                        Quaternion orient = Quaternion.identity;
                        var orientTok = parameters["orientation"];
                        if (orientTok != null && orientTok.Type == JTokenType.Object)
                            orient = ParseQuaternion(orientTok);
                        // Capsule main axis = local Y after applying orientation
                        var axis = orient * Vector3.up;
                        var halfSegment = Mathf.Max(0f, (height / 2f) - radiusC);
                        var p0 = position - axis * halfSegment;
                        var p1 = position + axis * halfSegment;
                        results = Physics.OverlapCapsule(p0, p1, radiusC, layerMask, qti);
                        data["synthesized_capsule"] = true;
                        data["point0"] = ToolHelpers.Vector3ToJson(p0);
                        data["point1"] = ToolHelpers.Vector3ToJson(p1);
                        data["radius"] = radiusC;
                        data["height"] = height;
                    }
                    else
                    {
                        var p0 = ToolHelpers.ParseVector3(p0Token);
                        var p1 = ToolHelpers.ParseVector3(p1Token);
                        var radiusC = ToolHelpers.GetOptionalFloat(parameters, "radius", 0.5f);
                        results = Physics.OverlapCapsule(p0, p1, radiusC, layerMask, qti);
                        data["point0"] = ToolHelpers.Vector3ToJson(p0);
                        data["point1"] = ToolHelpers.Vector3ToJson(p1);
                        data["radius"] = radiusC;
                    }
                    break;
                }
                default:
                    return ToolResponse.Fail($"Invalid shape: '{shapeStr}'. Valid: sphere, box, capsule.");
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
            data["hitCount"] = colliderArray.Count;
            data["colliders"] = colliderArray;
            return ToolResponse.OkWithData(data, $"Overlap test found {colliderArray.Count} collider(s).");
        }

        /// <summary>
        /// v1.9.5 (G05): New action `list_scene_physics_stats`. Iterates loaded scenes and counts
        /// Rigidbody/Collider/Trigger by layer. Complements manage_physics diagnosis workflow (S2).
        /// </summary>
        private ToolResponse HandleListScenePhysicsStats(JObject parameters)
        {
            int rigidbodyCount = 0;
            int kinematicCount = 0;
            int staticColliderCount = 0;
            int triggerCount = 0;
            int meshColliderCount = 0;
            int convexMeshColliderCount = 0;
            var perLayerCount = new int[32];

            var rigidbodies = UnityEngine.Object.FindObjectsOfType<Rigidbody>(true);
            foreach (var rb in rigidbodies)
            {
                if (rb == null) continue;
                rigidbodyCount++;
                if (rb.isKinematic) kinematicCount++;
            }

            var colliders = UnityEngine.Object.FindObjectsOfType<Collider>(true);
            foreach (var col in colliders)
            {
                if (col == null) continue;
                if (col.isTrigger) triggerCount++;
                else if (col.GetComponent<Rigidbody>() == null) staticColliderCount++;

                if (col is MeshCollider mc)
                {
                    meshColliderCount++;
                    if (mc.convex) convexMeshColliderCount++;
                }

                var layer = col.gameObject.layer;
                if (layer >= 0 && layer < 32) perLayerCount[layer]++;
            }

            var perLayer = new JObject();
            for (int i = 0; i < 32; i++)
            {
                if (perLayerCount[i] == 0) continue;
                var layerName = LayerMask.LayerToName(i);
                var key = string.IsNullOrEmpty(layerName) ? $"layer_{i}" : layerName;
                perLayer[key] = perLayerCount[i];
            }

            // 2D companion counts
            var rb2d = UnityEngine.Object.FindObjectsOfType<Rigidbody2D>(true);
            var col2d = UnityEngine.Object.FindObjectsOfType<Collider2D>(true);
            int trigger2dCount = 0;
            foreach (var c in col2d) { if (c != null && c.isTrigger) trigger2dCount++; }

            var data = new JObject
            {
                ["rigidbody_count"] = rigidbodyCount,
                ["kinematic_rigidbody_count"] = kinematicCount,
                ["collider_count"] = colliders.Length,
                ["static_collider_count"] = staticColliderCount,
                ["trigger_count"] = triggerCount,
                ["mesh_collider_count"] = meshColliderCount,
                ["convex_mesh_collider_count"] = convexMeshColliderCount,
                ["per_layer_collider_count"] = perLayer,
                ["physics_2d"] = new JObject
                {
                    ["rigidbody2d_count"] = rb2d.Length,
                    ["collider2d_count"] = col2d.Length,
                    ["trigger2d_count"] = trigger2dCount
                }
            };
            var summary = $"Scene physics: {rigidbodyCount} Rigidbody ({kinematicCount} kinematic), " +
                          $"{colliders.Length} Collider ({staticColliderCount} static, {triggerCount} trigger, {meshColliderCount} mesh).";
            return ToolResponse.OkWithData(data, summary);
        }

        /// <summary>
        /// v1.9.5 (G05): New action `get_collision_matrix`. Returns full 32x32 layer collision matrix
        /// snapshot. Supports dimension=3d (default) or 2d. Layer name omitted when empty.
        /// </summary>
        private ToolResponse HandleGetCollisionMatrix(JObject parameters)
        {
            var dimension = ToolHelpers.GetOptionalString(parameters, "dimension", "3d").ToLowerInvariant();
            if (dimension != "3d" && dimension != "2d")
                return ToolResponse.Fail($"Invalid 'dimension': '{dimension}'. Valid: 3d, 2d.");

            var layersInfo = new JArray();
            for (int i = 0; i < 32; i++)
            {
                var name = LayerMask.LayerToName(i);
                layersInfo.Add(new JObject
                {
                    ["index"] = i,
                    ["name"] = name
                });
            }

            // 32x32 matrix — matrix[i][j] = true if layer i collides with layer j
            var matrix = new JArray();
            var ignoredPairs = new JArray();
            for (int i = 0; i < 32; i++)
            {
                var row = new JArray();
                for (int j = 0; j < 32; j++)
                {
                    bool ignored = dimension == "2d"
                        ? Physics2D.GetIgnoreLayerCollision(i, j)
                        : Physics.GetIgnoreLayerCollision(i, j);
                    var collides = !ignored;
                    row.Add(collides);
                    // Record only upper triangle (i <= j) to avoid duplicates
                    if (ignored && i <= j)
                    {
                        ignoredPairs.Add(new JObject
                        {
                            ["layer_a"] = i,
                            ["layer_a_name"] = LayerMask.LayerToName(i),
                            ["layer_b"] = j,
                            ["layer_b_name"] = LayerMask.LayerToName(j)
                        });
                    }
                }
                matrix.Add(row);
            }

            var data = new JObject
            {
                ["dimension"] = dimension,
                ["layers"] = layersInfo,
                ["matrix"] = matrix,
                ["ignored_pair_count"] = ignoredPairs.Count,
                ["ignored_pairs"] = ignoredPairs
            };
            return ToolResponse.OkWithData(data,
                $"Collision matrix ({dimension}): {ignoredPairs.Count} ignored layer pair(s) out of 528 unique combos.");
        }

        /// <summary>Parse a JObject { x, y, z, w } into a Quaternion. Defaults to identity when fields missing.</summary>
        private static Quaternion ParseQuaternion(JToken token)
        {
            if (token == null || token.Type != JTokenType.Object)
                return Quaternion.identity;
            var obj = (JObject)token;
            float x = obj["x"]?.Value<float>() ?? 0f;
            float y = obj["y"]?.Value<float>() ?? 0f;
            float z = obj["z"]?.Value<float>() ?? 0f;
            float w = obj["w"]?.Value<float>() ?? 1f;
            var q = new Quaternion(x, y, z, w);
            // If (0,0,0,0) was passed, treat as identity — Physics.OverlapBox requires non-zero quaternion.
            if (q == default) return Quaternion.identity;
            return q;
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
