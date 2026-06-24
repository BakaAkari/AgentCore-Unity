using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Tools.Infrastructure;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Tools.Native.Specialized
{
    /// <summary>
    /// Manage Cinemachine virtual cameras, brain, lens, noise, and targets.
    /// Uses reflection to access Cinemachine API (package may not be installed).
    /// Supports both Cinemachine 2.x (CinemachineVirtualCamera) and 3.x (CinemachineCamera).
    /// </summary>
    [AgentTool("manage_cinemachine",
        Description = "Manage Cinemachine virtual cameras: create, configure body/aim/lens/noise, set targets, priorities, and setup CinemachineBrain. Requires com.unity.cinemachine package.",
        Category = "Specialized",
        Visibility = ToolVisibility.OnDemand,
        RequiresMainThread = true)]
    public class ManageCinemachineTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""create_virtual_camera"", ""set_target"", ""configure_body"", ""configure_aim"", ""set_noise"", ""get_info"", ""list"", ""set_priority"", ""configure_lens"", ""setup_brain"",
                               ""create_freelook"", ""configure_freelook_orbits"", ""create_state_driven"", ""add_state_camera"",
                               ""create_clearshot"", ""create_sequencer"", ""add_sequencer_entry"", ""create_dolly_track"",
                               ""configure_impulse"", ""set_blend_list""],
                    ""description"": ""Action to perform on Cinemachine""
                },
                ""target"": { ""type"": ""string"", ""description"": ""Target GameObject name (virtual camera or camera with Brain)"" },
                ""name"": { ""type"": ""string"", ""description"": ""Name for the new virtual camera (create_virtual_camera)"" },
                ""follow"": { ""type"": ""string"", ""description"": ""GameObject name to follow"" },
                ""lookAt"": { ""type"": ""string"", ""description"": ""GameObject name to look at"" },
                ""bodyType"": {
                    ""type"": ""string"",
                    ""enum"": [""transposer"", ""framing_transposer"", ""orbital_transposer"", ""tracked_dolly""],
                    ""description"": ""Body component type""
                },
                ""aimType"": {
                    ""type"": ""string"",
                    ""enum"": [""composer"", ""group_composer"", ""hard_look_at"", ""pov""],
                    ""description"": ""Aim component type""
                },
                ""followOffset"": {
                    ""type"": ""array"", ""items"": { ""type"": ""number"" },
                    ""description"": ""Follow offset [x, y, z] (configure_body)""
                },
                ""damping"": { ""type"": ""number"", ""description"": ""Damping value (configure_body)"" },
                ""trackedObjectOffset"": {
                    ""type"": ""array"", ""items"": { ""type"": ""number"" },
                    ""description"": ""Tracked object offset [x, y, z] (configure_aim)""
                },
                ""lookaheadTime"": { ""type"": ""number"", ""description"": ""Lookahead time in seconds (configure_aim)"" },
                ""amplitudeGain"": { ""type"": ""number"", ""description"": ""Noise amplitude gain (set_noise)"" },
                ""frequencyGain"": { ""type"": ""number"", ""description"": ""Noise frequency gain (set_noise)"" },
                ""noiseProfile"": { ""type"": ""string"", ""description"": ""Noise profile asset name (set_noise)"" },
                ""priority"": { ""type"": ""integer"", ""description"": ""Camera priority (set_priority)"" },
                ""fieldOfView"": { ""type"": ""number"", ""description"": ""Field of view in degrees (configure_lens)"" },
                ""nearClip"": { ""type"": ""number"", ""description"": ""Near clip plane (configure_lens)"" },
                ""farClip"": { ""type"": ""number"", ""description"": ""Far clip plane (configure_lens)"" },
                ""orthographic"": { ""type"": ""boolean"", ""description"": ""Use orthographic projection (configure_lens)"" },
                ""orthographicSize"": { ""type"": ""number"", ""description"": ""Orthographic size (configure_lens)"" },
                ""defaultBlend"": { ""type"": ""number"", ""description"": ""Default blend time in seconds (setup_brain)"" },
                ""blendStyle"": {
                    ""type"": ""string"",
                    ""enum"": [""cut"", ""ease_in_out"", ""ease_in"", ""ease_out"", ""hard_in"", ""hard_out"", ""linear""],
                    ""description"": ""Blend style (setup_brain)""
                },
                ""top_radius"": { ""type"": ""number"", ""description"": ""FreeLook top orbit radius"" },
                ""mid_radius"": { ""type"": ""number"", ""description"": ""FreeLook middle orbit radius"" },
                ""bot_radius"": { ""type"": ""number"", ""description"": ""FreeLook bottom orbit radius"" },
                ""top_height"": { ""type"": ""number"", ""description"": ""FreeLook top orbit height"" },
                ""mid_height"": { ""type"": ""number"", ""description"": ""FreeLook middle orbit height"" },
                ""bot_height"": { ""type"": ""number"", ""description"": ""FreeLook bottom orbit height"" },
                ""animator"": { ""type"": ""string"", ""description"": ""Animator GameObject name for state-driven camera"" },
                ""state_name"": { ""type"": ""string"", ""description"": ""Animator state name for add_state_camera"" },
                ""camera_name"": { ""type"": ""string"", ""description"": ""Virtual camera name for state mapping"" },
                ""cameras"": {
                    ""type"": ""array"",
                    ""items"": { ""type"": ""string"" },
                    ""description"": ""List of virtual camera names (create_clearshot, create_sequencer)""
                },
                ""duration"": { ""type"": ""number"", ""description"": ""Duration in seconds for sequencer entry"" },
                ""hold"": { ""type"": ""boolean"", ""description"": ""Hold last camera in sequencer"" },
                ""track_path"": { ""type"": ""string"", ""description"": ""Asset path for dolly track"" },
                ""impulse_force"": { ""type"": ""number"", ""description"": ""Impulse force magnitude"" },
                ""impulse_channel"": { ""type"": ""integer"", ""description"": ""Impulse channel (0-31)"" },
                ""blend_entries"": {
                    ""type"": ""array"",
                    ""items"": {
                        ""type"": ""object"",
                        ""properties"": {
                            ""from"": { ""type"": ""string"" },
                            ""to"": { ""type"": ""string"" },
                            ""time"": { ""type"": ""number"" },
                            ""style"": { ""type"": ""string"" }
                        }
                    },
                    ""description"": ""Custom blend list entries for set_blend_list""
                }
            },
            ""required"": [""action""]
        }");

        /// <summary>
        /// Tool metadata for registration and LLM discovery.
        /// </summary>
        public ToolMetadata Metadata => new ToolMetadata(
            name: "manage_cinemachine",
            description: "Manage Cinemachine virtual cameras: create VirtualCamera/FreeLook/StateDriven/ClearShot/Sequencer, configure body/aim/lens/noise/impulse, set targets, priorities, blend lists, and setup CinemachineBrain. Requires com.unity.cinemachine package.",
            category: "Specialized",
            parametersSchema: _parametersSchema,
            requiresMainThread: true
        );

        // Cached reflection types — Cinemachine 3.x
        private static Type _cm3CameraType;        // Unity.Cinemachine.CinemachineCamera
        private static Type _cm3BrainType;          // Unity.Cinemachine.CinemachineBrain

        // Cached reflection types — Cinemachine 2.x
        private static Type _cm2VirtualCameraType;  // Cinemachine.CinemachineVirtualCamera
        private static Type _cm2BrainType;          // Cinemachine.CinemachineBrain
        private static Type _cm2TransposerType;     // Cinemachine.CinemachineTransposer
        private static Type _cm2ComposerType;       // Cinemachine.CinemachineComposer
        private static Type _cm2BasicNoiseType;     // Cinemachine.CinemachineBasicMultiChannelPerlin

        private static bool _reflectionInitialized;
        private static bool _cinemachineAvailable;
        private static int _cinemachineVersion; // 2 or 3

        /// <summary>
        /// Execute a Cinemachine management action.
        /// </summary>
        public Task<ToolResult> ExecuteAsync(JObject parameters, CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();
            ToolResponse response;

            try
            {
                if (!EnsureCinemachineTypes())
                {
                    response = ToolResponse.Fail(
                        "Cinemachine package is not installed. Please install 'com.unity.cinemachine' via Package Manager (Window > Package Manager > Unity Registry > Cinemachine). Use manage_package tool to install it.");
                    sw.Stop();
                    return Task.FromResult(response.ToToolResult(sw.Elapsed.TotalMilliseconds));
                }

                var action = ToolHelpers.GetRequiredString(parameters, "action").ToLowerInvariant();

                switch (action)
                {
                    case "create_virtual_camera":
                        response = HandleCreateVirtualCamera(parameters);
                        break;
                    case "set_target":
                        response = HandleSetTarget(parameters);
                        break;
                    case "configure_body":
                        response = HandleConfigureBody(parameters);
                        break;
                    case "configure_aim":
                        response = HandleConfigureAim(parameters);
                        break;
                    case "set_noise":
                        response = HandleSetNoise(parameters);
                        break;
                    case "get_info":
                        response = HandleGetInfo(parameters);
                        break;
                    case "list":
                        response = HandleList();
                        break;
                    case "set_priority":
                        response = HandleSetPriority(parameters);
                        break;
                    case "configure_lens":
                        response = HandleConfigureLens(parameters);
                        break;
                    case "setup_brain":
                        response = HandleSetupBrain(parameters);
                        break;
                    case "create_freelook":
                        response = HandleCreateFreeLook(parameters);
                        break;
                    case "configure_freelook_orbits":
                        response = HandleConfigureFreeLookOrbits(parameters);
                        break;
                    case "create_state_driven":
                        response = HandleCreateStateDriven(parameters);
                        break;
                    case "add_state_camera":
                        response = HandleAddStateCamera(parameters);
                        break;
                    case "create_clearshot":
                        response = HandleCreateClearShot(parameters);
                        break;
                    case "create_sequencer":
                        response = HandleCreateSequencer(parameters);
                        break;
                    case "add_sequencer_entry":
                        response = HandleAddSequencerEntry(parameters);
                        break;
                    case "create_dolly_track":
                        response = HandleCreateDollyTrack(parameters);
                        break;
                    case "configure_impulse":
                        response = HandleConfigureImpulse(parameters);
                        break;
                    case "set_blend_list":
                        response = HandleSetBlendList(parameters);
                        break;
                    default:
                        response = ToolResponse.Fail(
                            $"Unknown action: {action}. Valid actions: create_virtual_camera, set_target, configure_body, configure_aim, " +
                            "set_noise, get_info, list, set_priority, configure_lens, setup_brain, " +
                            "create_freelook, configure_freelook_orbits, create_state_driven, add_state_camera, " +
                            "create_clearshot, create_sequencer, add_sequencer_entry, create_dolly_track, " +
                            "configure_impulse, set_blend_list");
                        break;
                }
            }
            catch (Exception ex)
            {
                response = ToolResponse.Fail($"Error: {ex.Message}");
            }

            sw.Stop();
            return Task.FromResult(response.ToToolResult(sw.Elapsed.TotalMilliseconds));
        }

        #region Reflection Initialization

        /// <summary>
        /// Initialize Cinemachine types via reflection. Checks 3.x first, then 2.x.
        /// Returns true if any Cinemachine version is available.
        /// </summary>
        private static bool EnsureCinemachineTypes()
        {
            if (_reflectionInitialized)
                return _cinemachineAvailable;

            _reflectionInitialized = true;

            // Try Cinemachine 3.x first (Unity.Cinemachine assembly)
            _cm3CameraType = FindType("Unity.Cinemachine.CinemachineCamera", "Unity.Cinemachine");
            if (_cm3CameraType != null)
            {
                _cm3BrainType = FindType("Unity.Cinemachine.CinemachineBrain", "Unity.Cinemachine");
                _cinemachineVersion = 3;
                _cinemachineAvailable = true;
                return true;
            }

            // Fallback to Cinemachine 2.x (Cinemachine assembly)
            _cm2VirtualCameraType = FindType("Cinemachine.CinemachineVirtualCamera", "Cinemachine");
            if (_cm2VirtualCameraType != null)
            {
                _cm2BrainType = FindType("Cinemachine.CinemachineBrain", "Cinemachine");
                _cm2TransposerType = FindType("Cinemachine.CinemachineTransposer", "Cinemachine");
                _cm2ComposerType = FindType("Cinemachine.CinemachineComposer", "Cinemachine");
                _cm2BasicNoiseType = FindType("Cinemachine.CinemachineBasicMultiChannelPerlin", "Cinemachine");
                _cinemachineVersion = 2;
                _cinemachineAvailable = true;
                return true;
            }

            _cinemachineAvailable = false;
            return false;
        }

        /// <summary>
        /// Find a type by full name, searching in a specific assembly first, then all loaded assemblies.
        /// </summary>
        private static Type FindType(string fullName, string assemblyHint)
        {
            // Try Type.GetType with assembly-qualified name
            var type = Type.GetType($"{fullName}, {assemblyHint}");
            if (type != null) return type;

            // Search all loaded assemblies
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name == assemblyHint || asm.FullName.Contains(assemblyHint))
                {
                    type = asm.GetType(fullName);
                    if (type != null) return type;
                }
            }

            return null;
        }

        /// <summary>
        /// Get the virtual camera component type for the detected Cinemachine version.
        /// </summary>
        private static Type GetVirtualCameraType()
        {
            return _cinemachineVersion == 3 ? _cm3CameraType : _cm2VirtualCameraType;
        }

        /// <summary>
        /// Get the brain component type for the detected Cinemachine version.
        /// </summary>
        private static Type GetBrainType()
        {
            return _cinemachineVersion == 3 ? _cm3BrainType : _cm2BrainType;
        }

        /// <summary>
        /// Get a component of the virtual camera type from a GameObject.
        /// </summary>
        private static Component GetVirtualCameraComponent(GameObject go)
        {
            var vcamType = GetVirtualCameraType();
            if (vcamType == null) return null;
            return go.GetComponent(vcamType);
        }

        /// <summary>
        /// Set a property value on a component via reflection.
        /// </summary>
        private static bool SetProperty(Component component, string propertyName, object value)
        {
            var prop = component.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (prop != null && prop.CanWrite)
            {
                prop.SetValue(component, value);
                return true;
            }

            var field = component.GetType().GetField(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (field != null)
            {
                field.SetValue(component, value);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Get a property value from a component via reflection.
        /// </summary>
        private static object GetProperty(Component component, string propertyName)
        {
            var prop = component.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (prop != null && prop.CanRead)
                return prop.GetValue(component);

            var field = component.GetType().GetField(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (field != null)
                return field.GetValue(component);

            return null;
        }

        /// <summary>
        /// Get a pipeline stage component from a Cinemachine 2.x virtual camera.
        /// </summary>
        private static Component GetCinemachineComponent(Component vcam, string stageName)
        {
            // CinemachineVirtualCamera.GetCinemachineComponent(CinemachineCore.Stage)
            var stageEnumType = FindType("Cinemachine.CinemachineCore+Stage", "Cinemachine");
            if (stageEnumType == null) return null;

            if (!Enum.TryParse(stageEnumType, stageName, true, out var stageValue))
                return null;

            var method = vcam.GetType().GetMethod("GetCinemachineComponent",
                BindingFlags.Public | BindingFlags.Instance,
                null, new[] { stageEnumType }, null);

            if (method == null) return null;

            return method.Invoke(vcam, new[] { stageValue }) as Component;
        }

        #endregion

        #region Action Handlers

        /// <summary>
        /// Create a new virtual camera GameObject with Cinemachine component.
        /// </summary>
        private ToolResponse HandleCreateVirtualCamera(JObject parameters)
        {
            var name = ToolHelpers.GetOptionalString(parameters, "name", "Virtual Camera");
            var followName = ToolHelpers.GetOptionalString(parameters, "follow", null);
            var lookAtName = ToolHelpers.GetOptionalString(parameters, "lookAt", null);

            var go = new GameObject(name);
            ToolHelpers.RegisterCreatedObject(go, "Create Cinemachine Virtual Camera");

            var vcamType = GetVirtualCameraType();
            var vcam = go.AddComponent(vcamType);

            if (vcam == null)
                return ToolResponse.Fail($"Failed to add {vcamType.Name} component to '{name}'.");

            // Set Follow target
            if (!string.IsNullOrEmpty(followName))
            {
                var followGo = ToolHelpers.FindGameObject(followName);
                if (followGo != null)
                {
                    SetProperty(vcam, "Follow", followGo.transform);
                }
            }

            // Set LookAt target
            if (!string.IsNullOrEmpty(lookAtName))
            {
                var lookAtGo = ToolHelpers.FindGameObject(lookAtName);
                if (lookAtGo != null)
                {
                    SetProperty(vcam, "LookAt", lookAtGo.transform);
                }
            }

            EditorUtility.SetDirty(go);

            var result = new Dictionary<string, object>
            {
                { "name", go.name },
                { "cinemachineVersion", _cinemachineVersion },
                { "componentType", vcamType.Name },
                { "follow", followName },
                { "lookAt", lookAtName }
            };

            return ToolResponse.OkWithData(result, $"Created virtual camera '{name}' using Cinemachine {_cinemachineVersion}.x.");
        }

        /// <summary>
        /// Set Follow and/or LookAt targets on a virtual camera.
        /// </summary>
        private ToolResponse HandleSetTarget(JObject parameters)
        {
            var targetName = ToolHelpers.GetRequiredString(parameters, "target");
            var followName = ToolHelpers.GetOptionalString(parameters, "follow", null);
            var lookAtName = ToolHelpers.GetOptionalString(parameters, "lookAt", null);

            if (string.IsNullOrEmpty(followName) && string.IsNullOrEmpty(lookAtName))
                return ToolResponse.Fail("At least one of 'follow' or 'lookAt' must be specified.");

            var go = ToolHelpers.FindGameObject(targetName);
            if (go == null)
                return ToolResponse.Fail($"GameObject '{targetName}' not found.");

            var vcam = GetVirtualCameraComponent(go);
            if (vcam == null)
                return ToolResponse.Fail($"'{targetName}' does not have a Cinemachine virtual camera component.");

            ToolHelpers.RecordUndo(vcam, "Set Cinemachine Target");

            var changes = new List<string>();

            if (!string.IsNullOrEmpty(followName))
            {
                var followGo = ToolHelpers.FindGameObject(followName);
                if (followGo == null)
                    return ToolResponse.Fail($"Follow target '{followName}' not found.");
                SetProperty(vcam, "Follow", followGo.transform);
                changes.Add($"Follow → {followName}");
            }

            if (!string.IsNullOrEmpty(lookAtName))
            {
                var lookAtGo = ToolHelpers.FindGameObject(lookAtName);
                if (lookAtGo == null)
                    return ToolResponse.Fail($"LookAt target '{lookAtName}' not found.");
                SetProperty(vcam, "LookAt", lookAtGo.transform);
                changes.Add($"LookAt → {lookAtName}");
            }

            EditorUtility.SetDirty(vcam);

            return ToolResponse.Ok($"Updated targets on '{targetName}': {string.Join(", ", changes)}");
        }

        /// <summary>
        /// Configure the Body component parameters on a virtual camera.
        /// </summary>
        private ToolResponse HandleConfigureBody(JObject parameters)
        {
            var targetName = ToolHelpers.GetRequiredString(parameters, "target");

            var go = ToolHelpers.FindGameObject(targetName);
            if (go == null)
                return ToolResponse.Fail($"GameObject '{targetName}' not found.");

            var vcam = GetVirtualCameraComponent(go);
            if (vcam == null)
                return ToolResponse.Fail($"'{targetName}' does not have a Cinemachine virtual camera component.");

            ToolHelpers.RecordUndo(vcam, "Configure Cinemachine Body");

            var changes = new List<string>();

            if (_cinemachineVersion == 2)
            {
                // Get the Body (Transposer) component
                var body = GetCinemachineComponent(vcam, "Body");
                if (body != null)
                {
                    ToolHelpers.RecordUndo(body, "Configure Cinemachine Body");

                    var followOffsetToken = ToolHelpers.GetOptionalArray(parameters, "followOffset");
                    if (followOffsetToken != null)
                    {
                        var offset = ToolHelpers.ParseVector3(followOffsetToken);
                        SetProperty(body, "m_FollowOffset", offset);
                        changes.Add($"followOffset → {offset}");
                    }

                    var damping = ToolHelpers.GetOptionalFloat(parameters, "damping", -1f);
                    if (damping >= 0f)
                    {
                        SetProperty(body, "m_XDamping", damping);
                        SetProperty(body, "m_YDamping", damping);
                        SetProperty(body, "m_ZDamping", damping);
                        changes.Add($"damping → {damping}");
                    }

                    EditorUtility.SetDirty(body);
                }
                else
                {
                    changes.Add("(no Body component found — set Follow target first or configure body type)");
                }
            }
            else // Cinemachine 3.x
            {
                var followOffsetToken = ToolHelpers.GetOptionalArray(parameters, "followOffset");
                if (followOffsetToken != null)
                {
                    var offset = ToolHelpers.ParseVector3(followOffsetToken);
                    // In CM3, try setting FollowOffset or similar property
                    if (SetProperty(vcam, "FollowOffset", offset))
                        changes.Add($"followOffset → {offset}");
                }

                var damping = ToolHelpers.GetOptionalFloat(parameters, "damping", -1f);
                if (damping >= 0f)
                {
                    if (SetProperty(vcam, "Damping", new Vector3(damping, damping, damping)))
                        changes.Add($"damping → {damping}");
                }
            }

            EditorUtility.SetDirty(vcam);

            if (changes.Count == 0)
                return ToolResponse.Ok($"No body parameters changed on '{targetName}'. Provide followOffset or damping.");

            return ToolResponse.Ok($"Configured body on '{targetName}': {string.Join(", ", changes)}");
        }

        /// <summary>
        /// Configure the Aim component parameters on a virtual camera.
        /// </summary>
        private ToolResponse HandleConfigureAim(JObject parameters)
        {
            var targetName = ToolHelpers.GetRequiredString(parameters, "target");

            var go = ToolHelpers.FindGameObject(targetName);
            if (go == null)
                return ToolResponse.Fail($"GameObject '{targetName}' not found.");

            var vcam = GetVirtualCameraComponent(go);
            if (vcam == null)
                return ToolResponse.Fail($"'{targetName}' does not have a Cinemachine virtual camera component.");

            ToolHelpers.RecordUndo(vcam, "Configure Cinemachine Aim");

            var changes = new List<string>();

            if (_cinemachineVersion == 2)
            {
                var aim = GetCinemachineComponent(vcam, "Aim");
                if (aim != null)
                {
                    ToolHelpers.RecordUndo(aim, "Configure Cinemachine Aim");

                    var offsetToken = ToolHelpers.GetOptionalArray(parameters, "trackedObjectOffset");
                    if (offsetToken != null)
                    {
                        var offset = ToolHelpers.ParseVector3(offsetToken);
                        SetProperty(aim, "m_TrackedObjectOffset", offset);
                        changes.Add($"trackedObjectOffset → {offset}");
                    }

                    var lookahead = ToolHelpers.GetOptionalFloat(parameters, "lookaheadTime", -1f);
                    if (lookahead >= 0f)
                    {
                        SetProperty(aim, "m_LookaheadTime", lookahead);
                        changes.Add($"lookaheadTime → {lookahead}");
                    }

                    EditorUtility.SetDirty(aim);
                }
                else
                {
                    changes.Add("(no Aim component found — set LookAt target first or configure aim type)");
                }
            }
            else // Cinemachine 3.x
            {
                var offsetToken = ToolHelpers.GetOptionalArray(parameters, "trackedObjectOffset");
                if (offsetToken != null)
                {
                    var offset = ToolHelpers.ParseVector3(offsetToken);
                    if (SetProperty(vcam, "TrackedObjectOffset", offset))
                        changes.Add($"trackedObjectOffset → {offset}");
                }

                var lookahead = ToolHelpers.GetOptionalFloat(parameters, "lookaheadTime", -1f);
                if (lookahead >= 0f)
                {
                    if (SetProperty(vcam, "LookaheadTime", lookahead))
                        changes.Add($"lookaheadTime → {lookahead}");
                }
            }

            EditorUtility.SetDirty(vcam);

            if (changes.Count == 0)
                return ToolResponse.Ok($"No aim parameters changed on '{targetName}'. Provide trackedObjectOffset or lookaheadTime.");

            return ToolResponse.Ok($"Configured aim on '{targetName}': {string.Join(", ", changes)}");
        }

        /// <summary>
        /// Set noise (camera shake) parameters on a virtual camera.
        /// </summary>
        private ToolResponse HandleSetNoise(JObject parameters)
        {
            var targetName = ToolHelpers.GetRequiredString(parameters, "target");

            var go = ToolHelpers.FindGameObject(targetName);
            if (go == null)
                return ToolResponse.Fail($"GameObject '{targetName}' not found.");

            var vcam = GetVirtualCameraComponent(go);
            if (vcam == null)
                return ToolResponse.Fail($"'{targetName}' does not have a Cinemachine virtual camera component.");

            ToolHelpers.RecordUndo(vcam, "Set Cinemachine Noise");

            var amplitudeGain = ToolHelpers.GetOptionalFloat(parameters, "amplitudeGain", -1f);
            var frequencyGain = ToolHelpers.GetOptionalFloat(parameters, "frequencyGain", -1f);
            var noiseProfile = ToolHelpers.GetOptionalString(parameters, "noiseProfile", null);

            var changes = new List<string>();

            if (_cinemachineVersion == 2)
            {
                var noise = GetCinemachineComponent(vcam, "Noise");
                if (noise != null)
                {
                    ToolHelpers.RecordUndo(noise, "Set Cinemachine Noise");

                    if (amplitudeGain >= 0f)
                    {
                        SetProperty(noise, "m_AmplitudeGain", amplitudeGain);
                        changes.Add($"amplitudeGain → {amplitudeGain}");
                    }

                    if (frequencyGain >= 0f)
                    {
                        SetProperty(noise, "m_FrequencyGain", frequencyGain);
                        changes.Add($"frequencyGain → {frequencyGain}");
                    }

                    if (!string.IsNullOrEmpty(noiseProfile))
                    {
                        // Try to find noise profile asset
                        var guids = AssetDatabase.FindAssets($"{noiseProfile} t:NoiseSettings");
                        if (guids.Length > 0)
                        {
                            var path = AssetDatabase.GUIDToAssetPath(guids[0]);
                            var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
                            if (asset != null)
                            {
                                SetProperty(noise, "m_NoiseProfile", asset);
                                changes.Add($"noiseProfile → {noiseProfile}");
                            }
                        }
                        else
                        {
                            changes.Add($"(noise profile '{noiseProfile}' not found in project)");
                        }
                    }

                    EditorUtility.SetDirty(noise);
                }
                else
                {
                    return ToolResponse.Fail($"No Noise component found on '{targetName}'. The virtual camera may need a noise profile assigned first in the Inspector.");
                }
            }
            else // Cinemachine 3.x
            {
                if (amplitudeGain >= 0f)
                {
                    if (SetProperty(vcam, "AmplitudeGain", amplitudeGain))
                        changes.Add($"amplitudeGain → {amplitudeGain}");
                }

                if (frequencyGain >= 0f)
                {
                    if (SetProperty(vcam, "FrequencyGain", frequencyGain))
                        changes.Add($"frequencyGain → {frequencyGain}");
                }
            }

            EditorUtility.SetDirty(vcam);

            if (changes.Count == 0)
                return ToolResponse.Ok($"No noise parameters changed on '{targetName}'. Provide amplitudeGain, frequencyGain, or noiseProfile.");

            return ToolResponse.Ok($"Set noise on '{targetName}': {string.Join(", ", changes)}");
        }

        /// <summary>
        /// Get detailed information about a virtual camera.
        /// </summary>
        private ToolResponse HandleGetInfo(JObject parameters)
        {
            var targetName = ToolHelpers.GetRequiredString(parameters, "target");

            var go = ToolHelpers.FindGameObject(targetName);
            if (go == null)
                return ToolResponse.Fail($"GameObject '{targetName}' not found.");

            var vcam = GetVirtualCameraComponent(go);
            if (vcam == null)
                return ToolResponse.Fail($"'{targetName}' does not have a Cinemachine virtual camera component.");

            var info = new Dictionary<string, object>
            {
                { "name", go.name },
                { "cinemachineVersion", _cinemachineVersion },
                { "componentType", vcam.GetType().Name },
                { "active", go.activeInHierarchy }
            };

            // Priority
            var priority = GetProperty(vcam, "Priority");
            if (priority != null) info["priority"] = priority;

            // Follow
            var follow = GetProperty(vcam, "Follow") as Transform;
            info["follow"] = follow != null ? follow.gameObject.name : null;

            // LookAt
            var lookAt = GetProperty(vcam, "LookAt") as Transform;
            info["lookAt"] = lookAt != null ? lookAt.gameObject.name : null;

            // Lens settings
            var lensInfo = new Dictionary<string, object>();
            if (_cinemachineVersion == 2)
            {
                var lens = GetProperty(vcam, "m_Lens");
                if (lens != null)
                {
                    var lensType = lens.GetType();
                    var fov = lensType.GetField("FieldOfView")?.GetValue(lens);
                    var near = lensType.GetField("NearClipPlane")?.GetValue(lens);
                    var far = lensType.GetField("FarClipPlane")?.GetValue(lens);
                    var ortho = lensType.GetField("Orthographic")?.GetValue(lens);
                    var orthoSize = lensType.GetField("OrthographicSize")?.GetValue(lens);

                    if (fov != null) lensInfo["fieldOfView"] = fov;
                    if (near != null) lensInfo["nearClipPlane"] = near;
                    if (far != null) lensInfo["farClipPlane"] = far;
                    if (ortho != null) lensInfo["orthographic"] = ortho;
                    if (orthoSize != null) lensInfo["orthographicSize"] = orthoSize;
                }
            }
            else // CM3
            {
                var lens = GetProperty(vcam, "Lens");
                if (lens != null)
                {
                    var lensType = lens.GetType();
                    var fov = lensType.GetProperty("FieldOfView")?.GetValue(lens)
                              ?? lensType.GetField("FieldOfView")?.GetValue(lens);
                    var near = lensType.GetProperty("NearClipPlane")?.GetValue(lens)
                               ?? lensType.GetField("NearClipPlane")?.GetValue(lens);
                    var far = lensType.GetProperty("FarClipPlane")?.GetValue(lens)
                              ?? lensType.GetField("FarClipPlane")?.GetValue(lens);

                    if (fov != null) lensInfo["fieldOfView"] = fov;
                    if (near != null) lensInfo["nearClipPlane"] = near;
                    if (far != null) lensInfo["farClipPlane"] = far;
                }
            }

            if (lensInfo.Count > 0) info["lens"] = lensInfo;

            // Body/Aim type info (CM2 only)
            if (_cinemachineVersion == 2)
            {
                var body = GetCinemachineComponent(vcam, "Body");
                if (body != null) info["bodyType"] = body.GetType().Name;

                var aim = GetCinemachineComponent(vcam, "Aim");
                if (aim != null) info["aimType"] = aim.GetType().Name;

                var noise = GetCinemachineComponent(vcam, "Noise");
                if (noise != null) info["noiseType"] = noise.GetType().Name;
            }

            return ToolResponse.OkWithData(info, $"Virtual camera info for '{targetName}'.");
        }

        /// <summary>
        /// List all virtual cameras in the scene.
        /// </summary>
        private ToolResponse HandleList()
        {
            var vcamType = GetVirtualCameraType();
            if (vcamType == null)
                return ToolResponse.Fail("Cinemachine virtual camera type not found.");

            var allVcams = UnityEngine.Object.FindObjectsOfType(vcamType);
            var cameras = new List<Dictionary<string, object>>();

            foreach (var obj in allVcams)
            {
                var comp = obj as Component;
                if (comp == null) continue;

                var camInfo = new Dictionary<string, object>
                {
                    { "name", comp.gameObject.name },
                    { "active", comp.gameObject.activeInHierarchy }
                };

                var priority = GetProperty(comp, "Priority");
                if (priority != null) camInfo["priority"] = priority;

                var follow = GetProperty(comp, "Follow") as Transform;
                camInfo["follow"] = follow != null ? follow.gameObject.name : null;

                var lookAt = GetProperty(comp, "LookAt") as Transform;
                camInfo["lookAt"] = lookAt != null ? lookAt.gameObject.name : null;

                cameras.Add(camInfo);
            }

            // Also check for Brain
            var brainType = GetBrainType();
            var brainCount = 0;
            if (brainType != null)
            {
                brainCount = UnityEngine.Object.FindObjectsOfType(brainType).Length;
            }

            var result = new Dictionary<string, object>
            {
                { "cinemachineVersion", _cinemachineVersion },
                { "virtualCameras", cameras },
                { "count", cameras.Count },
                { "brainCount", brainCount }
            };

            return ToolResponse.OkWithData(result, $"Found {cameras.Count} virtual camera(s) and {brainCount} brain(s).");
        }

        /// <summary>
        /// Set the priority of a virtual camera.
        /// </summary>
        private ToolResponse HandleSetPriority(JObject parameters)
        {
            var targetName = ToolHelpers.GetRequiredString(parameters, "target");
            var priority = ToolHelpers.GetOptionalInt(parameters, "priority", 10);

            var go = ToolHelpers.FindGameObject(targetName);
            if (go == null)
                return ToolResponse.Fail($"GameObject '{targetName}' not found.");

            var vcam = GetVirtualCameraComponent(go);
            if (vcam == null)
                return ToolResponse.Fail($"'{targetName}' does not have a Cinemachine virtual camera component.");

            ToolHelpers.RecordUndo(vcam, "Set Cinemachine Priority");

            if (!SetProperty(vcam, "Priority", priority))
            {
                // CM2 uses m_Priority field
                SetProperty(vcam, "m_Priority", priority);
            }

            EditorUtility.SetDirty(vcam);

            return ToolResponse.Ok($"Set priority of '{targetName}' to {priority}.");
        }

        /// <summary>
        /// Configure lens parameters on a virtual camera.
        /// </summary>
        private ToolResponse HandleConfigureLens(JObject parameters)
        {
            var targetName = ToolHelpers.GetRequiredString(parameters, "target");

            var go = ToolHelpers.FindGameObject(targetName);
            if (go == null)
                return ToolResponse.Fail($"GameObject '{targetName}' not found.");

            var vcam = GetVirtualCameraComponent(go);
            if (vcam == null)
                return ToolResponse.Fail($"'{targetName}' does not have a Cinemachine virtual camera component.");

            ToolHelpers.RecordUndo(vcam, "Configure Cinemachine Lens");

            var changes = new List<string>();

            // Get the lens struct
            var lensPropertyName = _cinemachineVersion == 2 ? "m_Lens" : "Lens";
            var lens = GetProperty(vcam, lensPropertyName);

            if (lens != null)
            {
                var lensType = lens.GetType();

                var fov = ToolHelpers.GetOptionalFloat(parameters, "fieldOfView", -1f);
                if (fov > 0f)
                {
                    var fovField = lensType.GetField("FieldOfView") ?? lensType.GetField("m_FieldOfView");
                    if (fovField != null)
                    {
                        fovField.SetValue(lens, fov);
                        changes.Add($"fieldOfView → {fov}");
                    }
                }

                var nearClip = ToolHelpers.GetOptionalFloat(parameters, "nearClip", -1f);
                if (nearClip > 0f)
                {
                    var nearField = lensType.GetField("NearClipPlane") ?? lensType.GetField("m_NearClipPlane");
                    if (nearField != null)
                    {
                        nearField.SetValue(lens, nearClip);
                        changes.Add($"nearClip → {nearClip}");
                    }
                }

                var farClip = ToolHelpers.GetOptionalFloat(parameters, "farClip", -1f);
                if (farClip > 0f)
                {
                    var farField = lensType.GetField("FarClipPlane") ?? lensType.GetField("m_FarClipPlane");
                    if (farField != null)
                    {
                        farField.SetValue(lens, farClip);
                        changes.Add($"farClip → {farClip}");
                    }
                }

                var orthoSize = ToolHelpers.GetOptionalFloat(parameters, "orthographicSize", -1f);
                if (orthoSize > 0f)
                {
                    var orthoSizeField = lensType.GetField("OrthographicSize") ?? lensType.GetField("m_OrthographicSize");
                    if (orthoSizeField != null)
                    {
                        orthoSizeField.SetValue(lens, orthoSize);
                        changes.Add($"orthographicSize → {orthoSize}");
                    }
                }

                var orthographic = ToolHelpers.GetOptionalBool(parameters, "orthographic", false);
                if (parameters["orthographic"] != null)
                {
                    var orthoField = lensType.GetField("Orthographic") ?? lensType.GetField("m_Orthographic");
                    if (orthoField != null)
                    {
                        orthoField.SetValue(lens, orthographic);
                        changes.Add($"orthographic → {orthographic}");
                    }
                }

                // Write back the lens struct
                SetProperty(vcam, lensPropertyName, lens);
            }
            else
            {
                return ToolResponse.Fail($"Could not access lens settings on '{targetName}'.");
            }

            EditorUtility.SetDirty(vcam);

            if (changes.Count == 0)
                return ToolResponse.Ok($"No lens parameters changed on '{targetName}'. Provide fieldOfView, nearClip, farClip, orthographic, or orthographicSize.");

            return ToolResponse.Ok($"Configured lens on '{targetName}': {string.Join(", ", changes)}");
        }

        /// <summary>
        /// Setup or configure CinemachineBrain on a camera GameObject.
        /// </summary>
        private ToolResponse HandleSetupBrain(JObject parameters)
        {
            var targetName = ToolHelpers.GetOptionalString(parameters, "target", null);
            var defaultBlend = ToolHelpers.GetOptionalFloat(parameters, "defaultBlend", -1f);
            var blendStyle = ToolHelpers.GetOptionalString(parameters, "blendStyle", null);

            // Find or use main camera
            GameObject cameraGo;
            if (!string.IsNullOrEmpty(targetName))
            {
                cameraGo = ToolHelpers.FindGameObject(targetName);
                if (cameraGo == null)
                    return ToolResponse.Fail($"GameObject '{targetName}' not found.");
            }
            else
            {
                var mainCam = Camera.main;
                if (mainCam == null)
                    return ToolResponse.Fail("No main camera found. Specify a target GameObject with a Camera component.");
                cameraGo = mainCam.gameObject;
            }

            // Ensure Camera component exists
            if (cameraGo.GetComponent<Camera>() == null)
                return ToolResponse.Fail($"'{cameraGo.name}' does not have a Camera component.");

            var brainType = GetBrainType();
            if (brainType == null)
                return ToolResponse.Fail("CinemachineBrain type not found.");

            // Add or get Brain component
            var brain = cameraGo.GetComponent(brainType);
            bool created = false;
            if (brain == null)
            {
                ToolHelpers.RecordUndo(cameraGo, "Add CinemachineBrain");
                brain = cameraGo.AddComponent(brainType);
                created = true;
            }
            else
            {
                ToolHelpers.RecordUndo(brain, "Configure CinemachineBrain");
            }

            var changes = new List<string>();

            // Configure default blend
            if (defaultBlend >= 0f || !string.IsNullOrEmpty(blendStyle))
            {
                // Get the m_DefaultBlend field (CinemachineBlendDefinition struct)
                var blendFieldName = _cinemachineVersion == 2 ? "m_DefaultBlend" : "DefaultBlend";
                var blendObj = GetProperty(brain, blendFieldName);

                if (blendObj != null)
                {
                    var blendType = blendObj.GetType();

                    if (defaultBlend >= 0f)
                    {
                        var timeField = blendType.GetField("m_Time") ?? blendType.GetField("Time");
                        if (timeField != null)
                        {
                            timeField.SetValue(blendObj, defaultBlend);
                            changes.Add($"defaultBlend → {defaultBlend}s");
                        }
                    }

                    if (!string.IsNullOrEmpty(blendStyle))
                    {
                        var styleField = blendType.GetField("m_Style") ?? blendType.GetField("Style");
                        if (styleField != null)
                        {
                            var styleEnumType = styleField.FieldType;
                            var mappedStyle = MapBlendStyle(blendStyle);
                            if (Enum.TryParse(styleEnumType, mappedStyle, true, out var styleValue))
                            {
                                styleField.SetValue(blendObj, styleValue);
                                changes.Add($"blendStyle → {blendStyle}");
                            }
                        }
                    }

                    SetProperty(brain, blendFieldName, blendObj);
                }
            }

            EditorUtility.SetDirty(brain);

            var action = created ? "Added" : "Configured";
            var msg = changes.Count > 0
                ? $"{action} CinemachineBrain on '{cameraGo.name}': {string.Join(", ", changes)}"
                : $"{action} CinemachineBrain on '{cameraGo.name}'.";

            return ToolResponse.Ok(msg);
        }

        #region Advanced Camera Handlers

        /// <summary>
        /// Create a FreeLook camera (3-orbit rig for third-person follow).
        /// </summary>
        private ToolResponse HandleCreateFreeLook(JObject parameters)
        {
            var name = ToolHelpers.GetOptionalString(parameters, "name", "FreeLook Camera");
            var followName = ToolHelpers.GetOptionalString(parameters, "follow", null);
            var lookAtName = ToolHelpers.GetOptionalString(parameters, "lookAt", null);

            // Find FreeLook type
            var freeLookType = FindType("Cinemachine.CinemachineFreeLook", "Cinemachine")
                            ?? FindType("Unity.Cinemachine.CinemachineFreeLook", "Unity.Cinemachine");

            if (freeLookType == null)
                return ToolResponse.Fail("CinemachineFreeLook type not found. Ensure Cinemachine 2.x is installed (FreeLook is not available in Cinemachine 3.x — use CinemachineCamera with OrbitalFollow instead).");

            var go = new GameObject(name);
            ToolHelpers.RegisterCreatedObject(go, "Create Cinemachine FreeLook");

            var freeLook = go.AddComponent(freeLookType);
            if (freeLook == null)
                return ToolResponse.Fail($"Failed to add CinemachineFreeLook to '{name}'.");

            if (!string.IsNullOrEmpty(followName))
            {
                var followGo = ToolHelpers.FindGameObject(followName);
                if (followGo != null) SetProperty(freeLook, "Follow", followGo.transform);
            }

            if (!string.IsNullOrEmpty(lookAtName))
            {
                var lookAtGo = ToolHelpers.FindGameObject(lookAtName);
                if (lookAtGo != null) SetProperty(freeLook, "LookAt", lookAtGo.transform);
            }

            EditorUtility.SetDirty(go);

            return ToolResponse.OkWithData(new
            {
                name = go.name,
                type = freeLookType.Name,
                follow = followName,
                lookAt = lookAtName
            }, $"Created FreeLook camera '{name}'. Use configure_freelook_orbits to set orbit radii/heights.");
        }

        /// <summary>
        /// Configure the three orbit rings of a FreeLook camera.
        /// </summary>
        private ToolResponse HandleConfigureFreeLookOrbits(JObject parameters)
        {
            var targetName = ToolHelpers.GetRequiredString(parameters, "target");

            var go = ToolHelpers.FindGameObject(targetName);
            if (go == null)
                return ToolResponse.Fail($"GameObject '{targetName}' not found.");

            var freeLookType = FindType("Cinemachine.CinemachineFreeLook", "Cinemachine")
                            ?? FindType("Unity.Cinemachine.CinemachineFreeLook", "Unity.Cinemachine");

            if (freeLookType == null)
                return ToolResponse.Fail("CinemachineFreeLook type not found.");

            var freeLook = go.GetComponent(freeLookType);
            if (freeLook == null)
                return ToolResponse.Fail($"'{targetName}' does not have a CinemachineFreeLook component.");

            ToolHelpers.RecordUndo(freeLook, "Configure FreeLook Orbits");

            // Orbits is an array of Orbit structs: [top, middle, bottom]
            var orbitsField = freeLookType.GetField("m_Orbits") ?? freeLookType.GetField("Orbits");
            if (orbitsField == null)
                return ToolResponse.Fail("Could not find orbits field on CinemachineFreeLook.");

            var orbits = orbitsField.GetValue(freeLook) as Array;
            if (orbits == null || orbits.Length < 3)
                return ToolResponse.Fail("FreeLook orbits array is null or has fewer than 3 entries.");

            var changes = new List<string>();

            // Helper to set orbit values
            void SetOrbit(int index, string radiusKey, string heightKey, string label)
            {
                var orbit = orbits.GetValue(index);
                if (orbit == null) return;
                var orbitType = orbit.GetType();

                var radius = ToolHelpers.GetOptionalFloat(parameters, radiusKey, -1f);
                if (radius >= 0f)
                {
                    var rf = orbitType.GetField("m_Radius") ?? orbitType.GetField("Radius");
                    rf?.SetValue(orbit, radius);
                    changes.Add($"{label} radius → {radius}");
                }

                var height = ToolHelpers.GetOptionalFloat(parameters, heightKey, float.MinValue);
                if (height > float.MinValue)
                {
                    var hf = orbitType.GetField("m_Height") ?? orbitType.GetField("Height");
                    hf?.SetValue(orbit, height);
                    changes.Add($"{label} height → {height}");
                }

                orbits.SetValue(orbit, index);
            }

            SetOrbit(0, "top_radius", "top_height", "top");
            SetOrbit(1, "mid_radius", "mid_height", "mid");
            SetOrbit(2, "bot_radius", "bot_height", "bot");

            orbitsField.SetValue(freeLook, orbits);
            EditorUtility.SetDirty(freeLook);

            if (changes.Count == 0)
                return ToolResponse.Ok($"No orbit parameters changed. Provide top_radius, mid_radius, bot_radius, top_height, mid_height, or bot_height.");

            return ToolResponse.OkWithData(new { target = targetName, changes },
                $"Configured FreeLook orbits on '{targetName}': {string.Join(", ", changes)}");
        }

        /// <summary>
        /// Create a State-Driven camera that switches virtual cameras based on Animator states.
        /// </summary>
        private ToolResponse HandleCreateStateDriven(JObject parameters)
        {
            var name = ToolHelpers.GetOptionalString(parameters, "name", "State Driven Camera");
            var animatorName = ToolHelpers.GetOptionalString(parameters, "animator", null);
            var childCameras = parameters["cameras"]?.ToObject<List<string>>() ?? new List<string>();

            var stateDrivenType = FindType("Cinemachine.CinemachineStateDrivenCamera", "Cinemachine")
                               ?? FindType("Unity.Cinemachine.CinemachineStateDrivenCamera", "Unity.Cinemachine");

            if (stateDrivenType == null)
                return ToolResponse.Fail("CinemachineStateDrivenCamera type not found. Ensure Cinemachine is installed.");

            var go = new GameObject(name);
            ToolHelpers.RegisterCreatedObject(go, "Create State Driven Camera");

            var stateDriven = go.AddComponent(stateDrivenType);
            if (stateDriven == null)
                return ToolResponse.Fail($"Failed to add CinemachineStateDrivenCamera to '{name}'.");

            // Set animator
            if (!string.IsNullOrEmpty(animatorName))
            {
                var animGo = ToolHelpers.FindGameObject(animatorName);
                if (animGo != null)
                {
                    var animator = animGo.GetComponent<Animator>();
                    if (animator != null)
                        SetProperty(stateDriven, "m_AnimatedTarget", animator);
                }
            }

            // Add child virtual cameras
            var addedCameras = new List<string>();
            foreach (var camName in childCameras)
            {
                var camGo = ToolHelpers.FindGameObject(camName);
                if (camGo != null)
                {
                    camGo.transform.SetParent(go.transform);
                    addedCameras.Add(camName);
                }
            }

            EditorUtility.SetDirty(go);

            return ToolResponse.OkWithData(new
            {
                name = go.name,
                animator = animatorName,
                child_cameras = addedCameras
            }, $"Created StateDriven camera '{name}' with {addedCameras.Count} child camera(s). Use add_state_camera to map states.");
        }

        /// <summary>
        /// Map an Animator state to a virtual camera in a State-Driven camera.
        /// </summary>
        private ToolResponse HandleAddStateCamera(JObject parameters)
        {
            var targetName = ToolHelpers.GetRequiredString(parameters, "target");
            var stateName = ToolHelpers.GetRequiredString(parameters, "state_name");
            var cameraName = ToolHelpers.GetRequiredString(parameters, "camera_name");

            var go = ToolHelpers.FindGameObject(targetName);
            if (go == null)
                return ToolResponse.Fail($"GameObject '{targetName}' not found.");

            var stateDrivenType = FindType("Cinemachine.CinemachineStateDrivenCamera", "Cinemachine")
                               ?? FindType("Unity.Cinemachine.CinemachineStateDrivenCamera", "Unity.Cinemachine");

            if (stateDrivenType == null)
                return ToolResponse.Fail("CinemachineStateDrivenCamera type not found.");

            var stateDriven = go.GetComponent(stateDrivenType);
            if (stateDriven == null)
                return ToolResponse.Fail($"'{targetName}' does not have a CinemachineStateDrivenCamera component.");

            // Find the camera to map
            var camGo = ToolHelpers.FindGameObject(cameraName);
            if (camGo == null)
                return ToolResponse.Fail($"Camera GameObject '{cameraName}' not found.");

            ToolHelpers.RecordUndo(stateDriven, "Add State Camera Mapping");

            // Get the m_Instructions array and add a new entry
            var instructionsField = stateDrivenType.GetField("m_Instructions");
            if (instructionsField == null)
                return ToolResponse.Fail("Could not find m_Instructions field on CinemachineStateDrivenCamera.");

            var instructions = instructionsField.GetValue(stateDriven) as Array;
            var instructionType = FindType("Cinemachine.CinemachineStateDrivenCamera+Instruction", "Cinemachine")
                               ?? FindType("Unity.Cinemachine.CinemachineStateDrivenCamera+Instruction", "Unity.Cinemachine");

            if (instructionType == null)
                return ToolResponse.Fail("Could not find Instruction type.");

            // Create new instruction
            var newInstruction = Activator.CreateInstance(instructionType);
            var vcamField = instructionType.GetField("m_VirtualCamera");
            vcamField?.SetValue(newInstruction, GetVirtualCameraComponent(camGo));

            // Expand array
            var currentLen = instructions?.Length ?? 0;
            var newInstructions = Array.CreateInstance(instructionType, currentLen + 1);
            if (instructions != null)
                Array.Copy(instructions, newInstructions, currentLen);
            newInstructions.SetValue(newInstruction, currentLen);
            instructionsField.SetValue(stateDriven, newInstructions);

            EditorUtility.SetDirty(stateDriven);

            return ToolResponse.OkWithData(new
            {
                target = targetName,
                state = stateName,
                camera = cameraName
            }, $"Added state mapping: '{stateName}' → '{cameraName}' on '{targetName}'. Note: state hash must be set manually in Inspector for full functionality.");
        }

        /// <summary>
        /// Create a ClearShot camera that picks the best unobstructed virtual camera.
        /// </summary>
        private ToolResponse HandleCreateClearShot(JObject parameters)
        {
            var name = ToolHelpers.GetOptionalString(parameters, "name", "ClearShot Camera");
            var childCameras = parameters["cameras"]?.ToObject<List<string>>() ?? new List<string>();

            var clearShotType = FindType("Cinemachine.CinemachineClearShot", "Cinemachine")
                             ?? FindType("Unity.Cinemachine.CinemachineClearShot", "Unity.Cinemachine");

            if (clearShotType == null)
                return ToolResponse.Fail("CinemachineClearShot type not found. Ensure Cinemachine is installed.");

            var go = new GameObject(name);
            ToolHelpers.RegisterCreatedObject(go, "Create ClearShot Camera");

            var clearShot = go.AddComponent(clearShotType);
            if (clearShot == null)
                return ToolResponse.Fail($"Failed to add CinemachineClearShot to '{name}'.");

            // Add child virtual cameras
            var addedCameras = new List<string>();
            foreach (var camName in childCameras)
            {
                var camGo = ToolHelpers.FindGameObject(camName);
                if (camGo != null)
                {
                    camGo.transform.SetParent(go.transform);
                    addedCameras.Add(camName);
                }
            }

            EditorUtility.SetDirty(go);

            return ToolResponse.OkWithData(new
            {
                name = go.name,
                type = clearShotType.Name,
                child_cameras = addedCameras
            }, $"Created ClearShot camera '{name}' with {addedCameras.Count} child camera(s).");
        }

        /// <summary>
        /// Create a Sequencer camera that plays through a list of virtual cameras in order.
        /// </summary>
        private ToolResponse HandleCreateSequencer(JObject parameters)
        {
            var name = ToolHelpers.GetOptionalString(parameters, "name", "Sequencer Camera");
            var childCameras = parameters["cameras"]?.ToObject<List<string>>() ?? new List<string>();
            var hold = ToolHelpers.GetOptionalBool(parameters, "hold", false);

            var sequencerType = FindType("Cinemachine.CinemachineSequencerCamera", "Cinemachine")
                             ?? FindType("Unity.Cinemachine.CinemachineSequencerCamera", "Unity.Cinemachine");

            if (sequencerType == null)
                return ToolResponse.Fail("CinemachineSequencerCamera type not found. Ensure Cinemachine is installed.");

            var go = new GameObject(name);
            ToolHelpers.RegisterCreatedObject(go, "Create Sequencer Camera");

            var sequencer = go.AddComponent(sequencerType);
            if (sequencer == null)
                return ToolResponse.Fail($"Failed to add CinemachineSequencerCamera to '{name}'.");

            // Set loop/hold
            SetProperty(sequencer, "m_Loop", hold);

            // Add child virtual cameras
            var addedCameras = new List<string>();
            foreach (var camName in childCameras)
            {
                var camGo = ToolHelpers.FindGameObject(camName);
                if (camGo != null)
                {
                    camGo.transform.SetParent(go.transform);
                    addedCameras.Add(camName);
                }
            }

            EditorUtility.SetDirty(go);

            return ToolResponse.OkWithData(new
            {
                name = go.name,
                type = sequencerType.Name,
                child_cameras = addedCameras,
                hold
            }, $"Created Sequencer camera '{name}' with {addedCameras.Count} child camera(s). Use add_sequencer_entry to configure timing.");
        }

        /// <summary>
        /// Add a timed entry to a Sequencer camera.
        /// </summary>
        private ToolResponse HandleAddSequencerEntry(JObject parameters)
        {
            var targetName = ToolHelpers.GetRequiredString(parameters, "target");
            var cameraName = ToolHelpers.GetRequiredString(parameters, "camera_name");
            var duration = ToolHelpers.GetOptionalFloat(parameters, "duration", 2f);
            var blendStyle = ToolHelpers.GetOptionalString(parameters, "blendStyle", "ease_in_out");

            var go = ToolHelpers.FindGameObject(targetName);
            if (go == null)
                return ToolResponse.Fail($"GameObject '{targetName}' not found.");

            var sequencerType = FindType("Cinemachine.CinemachineSequencerCamera", "Cinemachine")
                             ?? FindType("Unity.Cinemachine.CinemachineSequencerCamera", "Unity.Cinemachine");

            if (sequencerType == null)
                return ToolResponse.Fail("CinemachineSequencerCamera type not found.");

            var sequencer = go.GetComponent(sequencerType);
            if (sequencer == null)
                return ToolResponse.Fail($"'{targetName}' does not have a CinemachineSequencerCamera component.");

            var camGo = ToolHelpers.FindGameObject(cameraName);
            if (camGo == null)
                return ToolResponse.Fail($"Camera '{cameraName}' not found.");

            ToolHelpers.RecordUndo(sequencer, "Add Sequencer Entry");

            // Get m_Instructions array
            var instructionsField = sequencerType.GetField("m_Instructions");
            if (instructionsField == null)
                return ToolResponse.Fail("Could not find m_Instructions on CinemachineSequencerCamera.");

            var instructions = instructionsField.GetValue(sequencer) as Array;
            var instructionType = FindType("Cinemachine.CinemachineSequencerCamera+Instruction", "Cinemachine")
                               ?? FindType("Unity.Cinemachine.CinemachineSequencerCamera+Instruction", "Unity.Cinemachine");

            if (instructionType == null)
                return ToolResponse.Fail("Could not find Instruction type for CinemachineSequencerCamera.");

            var newInstruction = Activator.CreateInstance(instructionType);
            var vcamField = instructionType.GetField("m_VirtualCamera");
            vcamField?.SetValue(newInstruction, GetVirtualCameraComponent(camGo));

            var holdField = instructionType.GetField("m_Hold");
            holdField?.SetValue(newInstruction, duration);

            // Expand array
            var currentLen = instructions?.Length ?? 0;
            var newInstructions = Array.CreateInstance(instructionType, currentLen + 1);
            if (instructions != null)
                Array.Copy(instructions, newInstructions, currentLen);
            newInstructions.SetValue(newInstruction, currentLen);
            instructionsField.SetValue(sequencer, newInstructions);

            EditorUtility.SetDirty(sequencer);

            return ToolResponse.OkWithData(new
            {
                target = targetName,
                camera = cameraName,
                duration,
                blend_style = blendStyle
            }, $"Added sequencer entry: '{cameraName}' for {duration}s on '{targetName}'.");
        }

        /// <summary>
        /// Create a Dolly Track asset and optionally assign it to a virtual camera.
        /// </summary>
        private ToolResponse HandleCreateDollyTrack(JObject parameters)
        {
            var name = ToolHelpers.GetOptionalString(parameters, "name", "Dolly Track");
            var trackPath = ToolHelpers.GetOptionalString(parameters, "track_path", $"Assets/Cinemachine/{name}.asset");
            var targetCamera = ToolHelpers.GetOptionalString(parameters, "target", null);

            // Find DollyTrack type
            var dollyTrackType = FindType("Cinemachine.CinemachinePathBase", "Cinemachine")
                              ?? FindType("Cinemachine.CinemachineSmoothPath", "Cinemachine")
                              ?? FindType("Unity.Cinemachine.CinemachineSplinePath", "Unity.Cinemachine");

            if (dollyTrackType == null)
                return ToolResponse.Fail("Cinemachine dolly track type not found. Ensure Cinemachine is installed.");

            // Create as a GameObject with the path component
            var go = new GameObject(name);
            ToolHelpers.RegisterCreatedObject(go, "Create Dolly Track");
            var track = go.AddComponent(dollyTrackType);

            if (track == null)
                return ToolResponse.Fail($"Failed to add dolly track component to '{name}'.");

            // If target camera specified, assign the track
            if (!string.IsNullOrEmpty(targetCamera))
            {
                var camGo = ToolHelpers.FindGameObject(targetCamera);
                if (camGo != null)
                {
                    var vcam = GetVirtualCameraComponent(camGo);
                    if (vcam != null)
                    {
                        // Try to set the path on the body component
                        var body = GetCinemachineComponent(vcam, "Body");
                        if (body != null)
                        {
                            SetProperty(body, "m_Path", track);
                        }
                    }
                }
            }

            EditorUtility.SetDirty(go);

            return ToolResponse.OkWithData(new
            {
                name = go.name,
                type = dollyTrackType.Name,
                assigned_to = targetCamera
            }, $"Created dolly track '{name}'. Edit waypoints in the Inspector or Scene view.");
        }

        /// <summary>
        /// Configure CinemachineImpulseSource on a virtual camera for camera shake.
        /// </summary>
        private ToolResponse HandleConfigureImpulse(JObject parameters)
        {
            var targetName = ToolHelpers.GetRequiredString(parameters, "target");
            var force = ToolHelpers.GetOptionalFloat(parameters, "impulse_force", 1f);
            var channel = ToolHelpers.GetOptionalInt(parameters, "impulse_channel", 1);

            var go = ToolHelpers.FindGameObject(targetName);
            if (go == null)
                return ToolResponse.Fail($"GameObject '{targetName}' not found.");

            // Find ImpulseSource type
            var impulseSourceType = FindType("Cinemachine.CinemachineImpulseSource", "Cinemachine")
                                 ?? FindType("Unity.Cinemachine.CinemachineImpulseSource", "Unity.Cinemachine");

            if (impulseSourceType == null)
                return ToolResponse.Fail("CinemachineImpulseSource type not found. Ensure Cinemachine is installed.");

            var impulseSource = go.GetComponent(impulseSourceType);
            bool created = false;
            if (impulseSource == null)
            {
                ToolHelpers.RecordUndo(go, "Add CinemachineImpulseSource");
                impulseSource = go.AddComponent(impulseSourceType);
                created = true;
            }
            else
            {
                ToolHelpers.RecordUndo(impulseSource, "Configure CinemachineImpulseSource");
            }

            // Also add ImpulseListener to any virtual cameras if needed
            var impulseListenerType = FindType("Cinemachine.CinemachineImpulseListener", "Cinemachine")
                                   ?? FindType("Unity.Cinemachine.CinemachineImpulseListener", "Unity.Cinemachine");

            var changes = new List<string>();

            // Set impulse definition
            var impulseDefField = impulseSourceType.GetField("m_ImpulseDefinition")
                               ?? impulseSourceType.GetField("ImpulseDefinition");

            if (impulseDefField != null)
            {
                var impulseDef = impulseDefField.GetValue(impulseSource);
                if (impulseDef != null)
                {
                    var defType = impulseDef.GetType();
                    var ampField = defType.GetField("m_AmplitudeGain") ?? defType.GetField("AmplitudeGain");
                    ampField?.SetValue(impulseDef, force);
                    changes.Add($"amplitude → {force}");

                    var chanField = defType.GetField("m_ImpulseChannel") ?? defType.GetField("ImpulseChannel");
                    chanField?.SetValue(impulseDef, channel);
                    changes.Add($"channel → {channel}");

                    impulseDefField.SetValue(impulseSource, impulseDef);
                }
            }

            EditorUtility.SetDirty(impulseSource);

            var action = created ? "Added" : "Configured";
            return ToolResponse.OkWithData(new
            {
                target = targetName,
                force,
                channel,
                changes
            }, $"{action} CinemachineImpulseSource on '{targetName}'. Call GenerateImpulse() at runtime to trigger shake.");
        }

        /// <summary>
        /// Set custom blend overrides on CinemachineBrain for specific camera transitions.
        /// </summary>
        private ToolResponse HandleSetBlendList(JObject parameters)
        {
            var targetName = ToolHelpers.GetOptionalString(parameters, "target", null);
            var blendEntries = parameters["blend_entries"] as JArray;

            if (blendEntries == null || blendEntries.Count == 0)
                return ToolResponse.Fail("'blend_entries' array is required with at least one entry. Each entry needs 'from', 'to', 'time', and optionally 'style'.");

            // Find the Brain
            GameObject cameraGo;
            if (!string.IsNullOrEmpty(targetName))
            {
                cameraGo = ToolHelpers.FindGameObject(targetName);
                if (cameraGo == null)
                    return ToolResponse.Fail($"GameObject '{targetName}' not found.");
            }
            else
            {
                var mainCam = Camera.main;
                if (mainCam == null)
                    return ToolResponse.Fail("No main camera found. Specify a target.");
                cameraGo = mainCam.gameObject;
            }

            var brainType = GetBrainType();
            if (brainType == null)
                return ToolResponse.Fail("CinemachineBrain type not found.");

            var brain = cameraGo.GetComponent(brainType);
            if (brain == null)
                return ToolResponse.Fail($"No CinemachineBrain found on '{cameraGo.name}'. Use setup_brain first.");

            ToolHelpers.RecordUndo(brain, "Set Cinemachine Blend List");

            // Find the custom blends field
            var customBlendsField = brainType.GetField("m_CustomBlends");
            if (customBlendsField == null)
                return ToolResponse.Fail("Could not find m_CustomBlends on CinemachineBrain. This feature may not be available in your Cinemachine version.");

            var customBlendsType = FindType("Cinemachine.CinemachineBlenderSettings", "Cinemachine")
                                ?? FindType("Unity.Cinemachine.CinemachineBlenderSettings", "Unity.Cinemachine");

            if (customBlendsType == null)
                return ToolResponse.Fail("CinemachineBlenderSettings type not found.");

            var blenderSettings = ScriptableObject.CreateInstance(customBlendsType);
            if (blenderSettings == null)
                return ToolResponse.Fail("Failed to create CinemachineBlenderSettings.");

            // Save as asset
            var assetPath = "Assets/Cinemachine/CustomBlends.asset";
            var dir = System.IO.Path.GetDirectoryName(System.IO.Path.Combine(Application.dataPath, "..", assetPath));
            if (!System.IO.Directory.Exists(dir))
                System.IO.Directory.CreateDirectory(dir);

            AssetDatabase.CreateAsset(blenderSettings, assetPath);

            customBlendsField.SetValue(brain, blenderSettings);
            EditorUtility.SetDirty(brain);
            AssetDatabase.SaveAssets();

            return ToolResponse.OkWithData(new
            {
                target = cameraGo.name,
                blend_asset = assetPath,
                entry_count = blendEntries.Count
            }, $"Created blend list asset at '{assetPath}' and assigned to '{cameraGo.name}'. Configure individual blend entries in the Inspector.");
        }

        #endregion

        /// <summary>
        /// Map user-friendly blend style names to Cinemachine enum names.
        /// </summary>
        private static string MapBlendStyle(string style)
        {
            switch (style.ToLowerInvariant())
            {
                case "cut": return "Cut";
                case "ease_in_out": return "EaseInOut";
                case "ease_in": return "EaseIn";
                case "ease_out": return "EaseOut";
                case "hard_in": return "HardIn";
                case "hard_out": return "HardOut";
                case "linear": return "Linear";
                default: return "EaseInOut";
            }
        }

        #endregion
    }
}
