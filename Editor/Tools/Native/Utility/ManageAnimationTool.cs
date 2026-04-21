using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Tools.Infrastructure;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace AgentCore.Editor.Tools.Native.Utility
{
    /// <summary>
    /// Manage animation clips and Animator controllers.
    /// Directly calls Unity Animation API as part of the native tool system.
    /// </summary>
    [AgentTool("manage_animation",
        Description = "Manage animation clips and Animator controllers",
        Category = "Animation",
        RequiresMainThread = true)]
    public class ManageAnimationTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""list_clips"", ""get_clip_info"", ""list_parameters"", ""set_parameter"", ""get_controller_info"", ""list_animator_states""],
                    ""description"": ""Action to perform""
                },
                ""path"": {
                    ""type"": ""string"",
                    ""description"": ""Asset path (AnimationClip or AnimatorController)""
                },
                ""target"": {
                    ""type"": ""string"",
                    ""description"": ""Target GameObject with Animator component""
                },
                ""parameterName"": {
                    ""type"": ""string"",
                    ""description"": ""Animator parameter name""
                },
                ""parameterValue"": {
                    ""description"": ""Parameter value (type depends on parameter type)""
                },
                ""layerIndex"": {
                    ""type"": ""integer"",
                    ""description"": ""Animator layer index (default: 0)""
                }
            },
            ""required"": [""action""]
        }");

        public ToolMetadata Metadata => new ToolMetadata(
            name: "manage_animation",
            description: "Manage animation clips and Animator controllers",
            category: "Animation",
            parametersSchema: _parametersSchema,
            requiresMainThread: true
        );

        public Task<ToolResult> ExecuteAsync(JObject parameters, CancellationToken cancellationToken = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ToolResponse response;

            try
            {
                var action = ToolHelpers.GetRequiredString(parameters, "action").ToLowerInvariant();

                switch (action)
                {
                    case "list_clips":
                        response = HandleListClips(parameters);
                        break;
                    case "get_clip_info":
                        response = HandleGetClipInfo(parameters);
                        break;
                    case "list_parameters":
                        response = HandleListParameters(parameters);
                        break;
                    case "set_parameter":
                        response = HandleSetParameter(parameters);
                        break;
                    case "get_controller_info":
                        response = HandleGetControllerInfo(parameters);
                        break;
                    case "list_animator_states":
                        response = HandleListAnimatorStates(parameters);
                        break;
                    default:
                        response = ToolResponse.Fail(
                            $"Unknown action: '{action}'. Valid actions: list_clips, get_clip_info, list_parameters, set_parameter, get_controller_info, list_animator_states");
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

        private ToolResponse HandleListClips(JObject parameters)
        {
            try
            {
                var path = ToolHelpers.GetOptionalString(parameters, "path");
                var target = ToolHelpers.GetOptionalString(parameters, "target");

                var clips = new JArray();

                if (!string.IsNullOrEmpty(path))
                {
                    // List clips from an asset (FBX, AnimatorController, etc.)
                    path = ToolHelpers.NormalizeAssetPath(path);
                    var assets = AssetDatabase.LoadAllAssetsAtPath(path);
                    if (assets == null || assets.Length == 0)
                        return ToolResponse.Fail($"No assets found at path: {path}");

                    foreach (var asset in assets)
                    {
                        if (asset is AnimationClip clip && !clip.name.StartsWith("__preview__"))
                        {
                            clips.Add(new JObject
                            {
                                ["name"] = clip.name,
                                ["length"] = Math.Round(clip.length, 4),
                                ["frameRate"] = clip.frameRate,
                                ["isLooping"] = clip.isLooping,
                                ["wrapMode"] = clip.wrapMode.ToString()
                            });
                        }
                    }
                }
                else if (!string.IsNullOrEmpty(target))
                {
                    // List clips from a GameObject's Animator
                    var go = ToolHelpers.FindGameObject(target);
                    if (go == null)
                        return ToolResponse.Fail($"GameObject not found: '{target}'");

                    var animator = go.GetComponent<Animator>();
                    if (animator == null)
                        return ToolResponse.Fail($"GameObject '{target}' has no Animator component.");

                    if (animator.runtimeAnimatorController == null)
                        return ToolResponse.Fail($"Animator on '{target}' has no controller assigned.");

                    foreach (var clip in animator.runtimeAnimatorController.animationClips)
                    {
                        if (clip == null) continue;
                        clips.Add(new JObject
                        {
                            ["name"] = clip.name,
                            ["length"] = Math.Round(clip.length, 4),
                            ["frameRate"] = clip.frameRate,
                            ["isLooping"] = clip.isLooping,
                            ["wrapMode"] = clip.wrapMode.ToString()
                        });
                    }
                }
                else
                {
                    // Search all animation clips in the project
                    var guids = AssetDatabase.FindAssets("t:AnimationClip");
                    int count = 0;
                    foreach (var guid in guids)
                    {
                        if (count >= 50) break;
                        var clipPath = AssetDatabase.GUIDToAssetPath(guid);
                        var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
                        if (clip != null && !clip.name.StartsWith("__preview__"))
                        {
                            clips.Add(new JObject
                            {
                                ["name"] = clip.name,
                                ["path"] = clipPath,
                                ["length"] = Math.Round(clip.length, 4),
                                ["isLooping"] = clip.isLooping
                            });
                            count++;
                        }
                    }
                }

                return ToolResponse.OkWithData(new JObject
                {
                    ["clips"] = clips,
                    ["count"] = clips.Count
                }, $"Found {clips.Count} animation clips.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"List clips failed: {ex.Message}");
            }
        }

        private ToolResponse HandleGetClipInfo(JObject parameters)
        {
            try
            {
                var path = ToolHelpers.GetRequiredString(parameters, "path");
                path = ToolHelpers.NormalizeAssetPath(path);

                // Try loading as direct AnimationClip first
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);

                // If not found directly, search sub-assets
                if (clip == null)
                {
                    var assets = AssetDatabase.LoadAllAssetsAtPath(path);
                    if (assets != null)
                    {
                        clip = assets.OfType<AnimationClip>()
                            .FirstOrDefault(c => !c.name.StartsWith("__preview__"));
                    }
                }

                if (clip == null)
                    return ToolResponse.Fail($"AnimationClip not found at path: {path}");

                var info = new JObject
                {
                    ["name"] = clip.name,
                    ["path"] = path,
                    ["length"] = Math.Round(clip.length, 4),
                    ["frameRate"] = clip.frameRate,
                    ["totalFrames"] = Mathf.RoundToInt(clip.length * clip.frameRate),
                    ["isLooping"] = clip.isLooping,
                    ["wrapMode"] = clip.wrapMode.ToString(),
                    ["isHumanMotion"] = clip.isHumanMotion,
                    ["legacy"] = clip.legacy,
                    ["hasGenericRootTransform"] = clip.hasGenericRootTransform,
                    ["hasMotionCurves"] = clip.hasMotionCurves,
                    ["hasRootCurves"] = clip.hasRootCurves
                };

                // Curve bindings
                var bindings = AnimationUtility.GetCurveBindings(clip);
                var bindingArray = new JArray();
                foreach (var binding in bindings)
                {
                    bindingArray.Add(new JObject
                    {
                        ["path"] = binding.path,
                        ["propertyName"] = binding.propertyName,
                        ["type"] = binding.type.Name
                    });
                }
                info["curveBindings"] = bindingArray;
                info["curveCount"] = bindings.Length;

                // Object reference bindings
                var objBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
                if (objBindings.Length > 0)
                {
                    var objBindingArray = new JArray();
                    foreach (var binding in objBindings)
                    {
                        objBindingArray.Add(new JObject
                        {
                            ["path"] = binding.path,
                            ["propertyName"] = binding.propertyName,
                            ["type"] = binding.type.Name
                        });
                    }
                    info["objectReferenceBindings"] = objBindingArray;
                }

                // Animation events
                var events = AnimationUtility.GetAnimationEvents(clip);
                if (events.Length > 0)
                {
                    var eventArray = new JArray();
                    foreach (var evt in events)
                    {
                        eventArray.Add(new JObject
                        {
                            ["functionName"] = evt.functionName,
                            ["time"] = Math.Round(evt.time, 4),
                            ["intParameter"] = evt.intParameter,
                            ["floatParameter"] = Math.Round(evt.floatParameter, 4),
                            ["stringParameter"] = evt.stringParameter
                        });
                    }
                    info["events"] = eventArray;
                }

                return ToolResponse.OkWithData(info, $"Animation clip info for '{clip.name}'.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Get clip info failed: {ex.Message}");
            }
        }

        private ToolResponse HandleListParameters(JObject parameters)
        {
            try
            {
                AnimatorController controller = ResolveAnimatorController(parameters);
                if (controller == null)
                    return ToolResponse.Fail("AnimatorController not found. Provide 'path' to a .controller asset or 'target' GameObject with Animator.");

                var paramArray = new JArray();
                foreach (var param in controller.parameters)
                {
                    var p = new JObject
                    {
                        ["name"] = param.name,
                        ["type"] = param.type.ToString()
                    };

                    switch (param.type)
                    {
                        case AnimatorControllerParameterType.Float:
                            p["defaultValue"] = param.defaultFloat;
                            break;
                        case AnimatorControllerParameterType.Int:
                            p["defaultValue"] = param.defaultInt;
                            break;
                        case AnimatorControllerParameterType.Bool:
                            p["defaultValue"] = param.defaultBool;
                            break;
                        case AnimatorControllerParameterType.Trigger:
                            p["defaultValue"] = false;
                            break;
                    }

                    paramArray.Add(p);
                }

                return ToolResponse.OkWithData(new JObject
                {
                    ["controllerName"] = controller.name,
                    ["parameters"] = paramArray,
                    ["parameterCount"] = paramArray.Count
                }, $"Found {paramArray.Count} parameters in controller '{controller.name}'.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"List parameters failed: {ex.Message}");
            }
        }

        private ToolResponse HandleSetParameter(JObject parameters)
        {
            try
            {
                AnimatorController controller = ResolveAnimatorController(parameters);
                if (controller == null)
                    return ToolResponse.Fail("AnimatorController not found. Provide 'path' to a .controller asset or 'target' GameObject with Animator.");

                var parameterName = ToolHelpers.GetRequiredString(parameters, "parameterName");
                var valueToken = parameters["parameterValue"];
                if (valueToken == null)
                    return ToolResponse.Fail("Parameter 'parameterValue' is required.");

                // Find the parameter
                var controllerParams = controller.parameters;
                int paramIndex = -1;
                for (int i = 0; i < controllerParams.Length; i++)
                {
                    if (controllerParams[i].name == parameterName)
                    {
                        paramIndex = i;
                        break;
                    }
                }

                if (paramIndex < 0)
                    return ToolResponse.Fail($"Parameter '{parameterName}' not found in controller '{controller.name}'.");

                var param = controllerParams[paramIndex];
                ToolHelpers.RecordUndo(controller, "Set Animator Parameter");

                switch (param.type)
                {
                    case AnimatorControllerParameterType.Float:
                        param.defaultFloat = valueToken.Value<float>();
                        break;
                    case AnimatorControllerParameterType.Int:
                        param.defaultInt = valueToken.Value<int>();
                        break;
                    case AnimatorControllerParameterType.Bool:
                        param.defaultBool = valueToken.Value<bool>();
                        break;
                    case AnimatorControllerParameterType.Trigger:
                        // Triggers don't have a persistent default value
                        return ToolResponse.Fail("Cannot set default value for Trigger parameters.");
                }

                controllerParams[paramIndex] = param;
                controller.parameters = controllerParams;

                EditorUtility.SetDirty(controller);
                AssetDatabase.SaveAssets();

                return ToolResponse.Ok($"Set parameter '{parameterName}' default value on controller '{controller.name}'.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Set parameter failed: {ex.Message}");
            }
        }

        private ToolResponse HandleGetControllerInfo(JObject parameters)
        {
            try
            {
                AnimatorController controller = ResolveAnimatorController(parameters);
                if (controller == null)
                    return ToolResponse.Fail("AnimatorController not found. Provide 'path' to a .controller asset or 'target' GameObject with Animator.");

                var info = new JObject
                {
                    ["name"] = controller.name,
                    ["path"] = AssetDatabase.GetAssetPath(controller),
                    ["layerCount"] = controller.layers.Length,
                    ["parameterCount"] = controller.parameters.Length
                };

                // Layers
                var layers = new JArray();
                for (int i = 0; i < controller.layers.Length; i++)
                {
                    var layer = controller.layers[i];
                    var layerInfo = new JObject
                    {
                        ["index"] = i,
                        ["name"] = layer.name,
                        ["defaultWeight"] = layer.defaultWeight,
                        ["blendingMode"] = layer.blendingMode.ToString()
                    };

                    // State count in this layer
                    if (layer.stateMachine != null)
                    {
                        layerInfo["stateCount"] = layer.stateMachine.states.Length;
                        layerInfo["subStateMachineCount"] = layer.stateMachine.stateMachines.Length;

                        if (layer.stateMachine.defaultState != null)
                            layerInfo["defaultState"] = layer.stateMachine.defaultState.name;
                    }

                    layers.Add(layerInfo);
                }
                info["layers"] = layers;

                // Parameters summary
                var paramArray = new JArray();
                foreach (var param in controller.parameters)
                {
                    paramArray.Add(new JObject
                    {
                        ["name"] = param.name,
                        ["type"] = param.type.ToString()
                    });
                }
                info["parameters"] = paramArray;

                // Animation clips used
                var clipNames = new JArray();
                foreach (var clip in controller.animationClips)
                {
                    if (clip != null)
                        clipNames.Add(clip.name);
                }
                info["animationClips"] = clipNames;
                info["clipCount"] = clipNames.Count;

                return ToolResponse.OkWithData(info, $"Controller info for '{controller.name}'.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Get controller info failed: {ex.Message}");
            }
        }

        private ToolResponse HandleListAnimatorStates(JObject parameters)
        {
            try
            {
                AnimatorController controller = ResolveAnimatorController(parameters);
                if (controller == null)
                    return ToolResponse.Fail("AnimatorController not found. Provide 'path' to a .controller asset or 'target' GameObject with Animator.");

                var layerIndex = ToolHelpers.GetOptionalInt(parameters, "layerIndex", 0);

                if (layerIndex < 0 || layerIndex >= controller.layers.Length)
                    return ToolResponse.Fail($"Layer index {layerIndex} is out of range. Controller has {controller.layers.Length} layer(s).");

                var layer = controller.layers[layerIndex];
                var stateMachine = layer.stateMachine;

                if (stateMachine == null)
                    return ToolResponse.Fail($"Layer {layerIndex} has no state machine.");

                var states = new JArray();
                CollectStates(stateMachine, states, "");

                return ToolResponse.OkWithData(new JObject
                {
                    ["controllerName"] = controller.name,
                    ["layerIndex"] = layerIndex,
                    ["layerName"] = layer.name,
                    ["states"] = states,
                    ["stateCount"] = states.Count,
                    ["defaultState"] = stateMachine.defaultState?.name
                }, $"Found {states.Count} states in layer '{layer.name}' of controller '{controller.name}'.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"List animator states failed: {ex.Message}");
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Resolve AnimatorController from either 'path' or 'target' parameter.
        /// </summary>
        private AnimatorController ResolveAnimatorController(JObject parameters)
        {
            var path = ToolHelpers.GetOptionalString(parameters, "path");
            var target = ToolHelpers.GetOptionalString(parameters, "target");

            if (!string.IsNullOrEmpty(path))
            {
                path = ToolHelpers.NormalizeAssetPath(path);
                return AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            }

            if (!string.IsNullOrEmpty(target))
            {
                var go = ToolHelpers.FindGameObject(target);
                if (go == null) return null;

                var animator = go.GetComponent<Animator>();
                if (animator == null || animator.runtimeAnimatorController == null) return null;

                // Try to get the AnimatorController from the runtime controller
                if (animator.runtimeAnimatorController is AnimatorController ac)
                    return ac;

                // If it's an AnimatorOverrideController, get the base controller
                if (animator.runtimeAnimatorController is AnimatorOverrideController overrideController)
                {
                    if (overrideController.runtimeAnimatorController is AnimatorController baseController)
                        return baseController;
                }

                // Try loading from asset path
                var assetPath = AssetDatabase.GetAssetPath(animator.runtimeAnimatorController);
                if (!string.IsNullOrEmpty(assetPath))
                    return AssetDatabase.LoadAssetAtPath<AnimatorController>(assetPath);
            }

            return null;
        }

        /// <summary>
        /// Recursively collect all states from a state machine and its sub-state machines.
        /// </summary>
        private void CollectStates(AnimatorStateMachine stateMachine, JArray states, string prefix)
        {
            foreach (var childState in stateMachine.states)
            {
                var state = childState.state;
                var stateInfo = new JObject
                {
                    ["name"] = state.name,
                    ["fullPath"] = string.IsNullOrEmpty(prefix) ? state.name : $"{prefix}/{state.name}",
                    ["speed"] = state.speed,
                    ["speedMultiplierParameter"] = state.speedParameter,
                    ["tag"] = state.tag,
                    ["writeDefaultValues"] = state.writeDefaultValues
                };

                // Motion info
                if (state.motion != null)
                {
                    stateInfo["motion"] = state.motion.name;
                    stateInfo["motionType"] = state.motion.GetType().Name;

                    if (state.motion is AnimationClip clip)
                    {
                        stateInfo["clipLength"] = Math.Round(clip.length, 4);
                        stateInfo["clipIsLooping"] = clip.isLooping;
                    }
                    else if (state.motion is BlendTree)
                    {
                        stateInfo["isBlendTree"] = true;
                    }
                }

                // Transitions
                var transitions = state.transitions;
                if (transitions.Length > 0)
                {
                    var transArray = new JArray();
                    foreach (var trans in transitions)
                    {
                        var transInfo = new JObject
                        {
                            ["destinationState"] = trans.destinationState?.name ?? "(exit)",
                            ["hasExitTime"] = trans.hasExitTime,
                            ["exitTime"] = Math.Round(trans.exitTime, 4),
                            ["duration"] = Math.Round(trans.duration, 4),
                            ["hasFixedDuration"] = trans.hasFixedDuration,
                            ["conditionCount"] = trans.conditions.Length
                        };

                        if (trans.conditions.Length > 0)
                        {
                            var conditions = new JArray();
                            foreach (var cond in trans.conditions)
                            {
                                conditions.Add(new JObject
                                {
                                    ["parameter"] = cond.parameter,
                                    ["mode"] = cond.mode.ToString(),
                                    ["threshold"] = Math.Round(cond.threshold, 4)
                                });
                            }
                            transInfo["conditions"] = conditions;
                        }

                        transArray.Add(transInfo);
                    }
                    stateInfo["transitions"] = transArray;
                }

                states.Add(stateInfo);
            }

            // Recurse into sub-state machines
            foreach (var childSM in stateMachine.stateMachines)
            {
                var subPrefix = string.IsNullOrEmpty(prefix)
                    ? childSM.stateMachine.name
                    : $"{prefix}/{childSM.stateMachine.name}";
                CollectStates(childSM.stateMachine, states, subPrefix);
            }
        }

        #endregion
    }
}
