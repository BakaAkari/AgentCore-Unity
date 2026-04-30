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
        RequiresMainThread = true)]
    public class ManageCinemachineTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""create_virtual_camera"", ""set_target"", ""configure_body"", ""configure_aim"", ""set_noise"", ""get_info"", ""list"", ""set_priority"", ""configure_lens"", ""setup_brain""],
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
                }
            },
            ""required"": [""action""]
        }");

        /// <summary>
        /// Tool metadata for registration and LLM discovery.
        /// </summary>
        public ToolMetadata Metadata => new ToolMetadata(
            name: "manage_cinemachine",
            description: "Manage Cinemachine virtual cameras: create, configure body/aim/lens/noise, set targets, priorities, and setup CinemachineBrain. Requires com.unity.cinemachine package.",
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
                    default:
                        response = ToolResponse.Fail($"Unknown action: {action}. Valid actions: create_virtual_camera, set_target, configure_body, configure_aim, set_noise, get_info, list, set_priority, configure_lens, setup_brain");
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
