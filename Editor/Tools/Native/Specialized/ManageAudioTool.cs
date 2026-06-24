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
    /// Manage audio sources, listeners, and audio settings.
    /// Directly calls Unity AudioSource / AudioSettings API.
    /// </summary>
    [AgentTool("manage_audio",
        Description = "Manage audio sources, listeners, and audio settings",
        Category = "specialized",
        Visibility = ToolVisibility.OnDemand,
        RequiresMainThread = true)]
    public class ManageAudioTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""add_source"", ""modify_source"", ""play"", ""stop"", ""get_info"", ""list"", ""get_settings""],
                    ""description"": ""Action to perform""
                },
                ""target"": { ""type"": ""string"", ""description"": ""Target GameObject name"" },
                ""clip"": { ""type"": ""string"", ""description"": ""AudioClip asset path"" },
                ""volume"": { ""type"": ""number"", ""description"": ""Volume (0-1, default: 1)"" },
                ""pitch"": { ""type"": ""number"", ""description"": ""Pitch (default: 1)"" },
                ""loop"": { ""type"": ""boolean"", ""description"": ""Loop playback (default: false)"" },
                ""play_on_awake"": { ""type"": ""boolean"", ""description"": ""Play on awake (default: true)"" },
                ""spatial_blend"": { ""type"": ""number"", ""description"": ""Spatial blend (0=2D, 1=3D)"" },
                ""min_distance"": { ""type"": ""number"", ""description"": ""Minimum distance for 3D sound"" },
                ""max_distance"": { ""type"": ""number"", ""description"": ""Maximum distance for 3D sound"" }
            },
            ""required"": [""action""]
        }");

        public ToolMetadata Metadata => new ToolMetadata(
            name: "manage_audio",
            description: "Manage audio sources, listeners, and audio settings",
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
                    case "add_source":
                        response = HandleAddSource(parameters);
                        break;
                    case "modify_source":
                        response = HandleModifySource(parameters);
                        break;
                    case "play":
                        response = HandlePlay(parameters);
                        break;
                    case "stop":
                        response = HandleStop(parameters);
                        break;
                    case "get_info":
                        response = HandleGetInfo(parameters);
                        break;
                    case "list":
                        response = HandleList();
                        break;
                    case "get_settings":
                        response = HandleGetSettings();
                        break;
                    default:
                        response = ToolResponse.Fail(
                            $"Unknown action: '{action}'. Valid actions: add_source, modify_source, play, stop, get_info, list, get_settings");
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

        private ToolResponse HandleAddSource(JObject parameters)
        {
            var targetName = ToolHelpers.GetRequiredString(parameters, "target");
            var go = ToolHelpers.FindGameObject(targetName);
            if (go == null)
                return ToolResponse.Fail($"GameObject '{targetName}' not found.");

            ToolHelpers.RecordUndo(go, "Add AudioSource");
            var source = Undo.AddComponent<AudioSource>(go);

            ApplyAudioSourceProperties(source, parameters);

            EditorUtility.SetDirty(go);

            var data = SerializeAudioSource(go, source);
            return ToolResponse.OkWithData(data, $"AudioSource added to '{targetName}'.");
        }

        private ToolResponse HandleModifySource(JObject parameters)
        {
            var targetName = ToolHelpers.GetRequiredString(parameters, "target");
            var go = ToolHelpers.FindGameObject(targetName);
            if (go == null)
                return ToolResponse.Fail($"GameObject '{targetName}' not found.");

            var source = go.GetComponent<AudioSource>();
            if (source == null)
                return ToolResponse.Fail($"GameObject '{targetName}' does not have an AudioSource component.");

            ToolHelpers.RecordUndo(source, "Modify AudioSource");

            ApplyAudioSourceProperties(source, parameters);

            EditorUtility.SetDirty(source);

            var data = SerializeAudioSource(go, source);
            return ToolResponse.OkWithData(data, $"AudioSource on '{targetName}' modified.");
        }

        private ToolResponse HandlePlay(JObject parameters)
        {
            var targetName = ToolHelpers.GetRequiredString(parameters, "target");
            var go = ToolHelpers.FindGameObject(targetName);
            if (go == null)
                return ToolResponse.Fail($"GameObject '{targetName}' not found.");

            var source = go.GetComponent<AudioSource>();
            if (source == null)
                return ToolResponse.Fail($"GameObject '{targetName}' does not have an AudioSource component.");

            // Optionally load a temporary clip
            var clipPath = ToolHelpers.GetOptionalString(parameters, "clip");
            if (!string.IsNullOrEmpty(clipPath))
            {
                clipPath = ToolHelpers.NormalizeAssetPath(clipPath);
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(clipPath);
                if (clip == null)
                    return ToolResponse.Fail($"AudioClip not found at: {clipPath}");
                source.clip = clip;
            }

            if (source.clip == null)
                return ToolResponse.Fail($"AudioSource on '{targetName}' has no clip assigned.");

            source.Play();

            return ToolResponse.Ok($"Playing audio on '{targetName}' (clip: {source.clip.name}).");
        }

        private ToolResponse HandleStop(JObject parameters)
        {
            var targetName = ToolHelpers.GetRequiredString(parameters, "target");
            var go = ToolHelpers.FindGameObject(targetName);
            if (go == null)
                return ToolResponse.Fail($"GameObject '{targetName}' not found.");

            var source = go.GetComponent<AudioSource>();
            if (source == null)
                return ToolResponse.Fail($"GameObject '{targetName}' does not have an AudioSource component.");

            source.Stop();

            return ToolResponse.Ok($"Stopped audio on '{targetName}'.");
        }

        private ToolResponse HandleGetInfo(JObject parameters)
        {
            var targetName = ToolHelpers.GetRequiredString(parameters, "target");
            var go = ToolHelpers.FindGameObject(targetName);
            if (go == null)
                return ToolResponse.Fail($"GameObject '{targetName}' not found.");

            var source = go.GetComponent<AudioSource>();
            if (source == null)
                return ToolResponse.Fail($"GameObject '{targetName}' does not have an AudioSource component.");

            var data = SerializeAudioSource(go, source);
            return ToolResponse.OkWithData(data, $"AudioSource info for '{targetName}'.");
        }

        private ToolResponse HandleList()
        {
            var allSources = UnityEngine.Object.FindObjectsByType<AudioSource>(FindObjectsSortMode.None);
            var allListeners = UnityEngine.Object.FindObjectsByType<AudioListener>(FindObjectsSortMode.None);

            var sourcesArray = new JArray();
            foreach (var source in allSources)
            {
                sourcesArray.Add(new JObject
                {
                    ["name"] = source.gameObject.name,
                    ["instanceId"] = source.gameObject.GetInstanceID(),
                    ["clip"] = source.clip != null ? source.clip.name : null,
                    ["volume"] = source.volume,
                    ["isPlaying"] = source.isPlaying,
                    ["loop"] = source.loop,
                    ["spatialBlend"] = source.spatialBlend
                });
            }

            var listenersArray = new JArray();
            foreach (var listener in allListeners)
            {
                listenersArray.Add(new JObject
                {
                    ["name"] = listener.gameObject.name,
                    ["instanceId"] = listener.gameObject.GetInstanceID(),
                    ["enabled"] = listener.enabled
                });
            }

            var data = new JObject
            {
                ["audioSourceCount"] = sourcesArray.Count,
                ["audioSources"] = sourcesArray,
                ["audioListenerCount"] = listenersArray.Count,
                ["audioListeners"] = listenersArray
            };

            return ToolResponse.OkWithData(data, $"Found {sourcesArray.Count} AudioSource(s) and {listenersArray.Count} AudioListener(s).");
        }

        private ToolResponse HandleGetSettings()
        {
            AudioConfiguration config = AudioSettings.GetConfiguration();

            var data = new JObject
            {
                ["outputSampleRate"] = AudioSettings.outputSampleRate,
                ["speakerMode"] = AudioSettings.speakerMode.ToString(),
                ["dspBufferSize"] = config.dspBufferSize,
                ["sampleRate"] = config.sampleRate,
                ["numRealVoices"] = config.numRealVoices,
                ["numVirtualVoices"] = config.numVirtualVoices,
                ["driverCapabilities"] = AudioSettings.driverCapabilities.ToString()
            };

            return ToolResponse.OkWithData(data, "Audio settings retrieved.");
        }

        #endregion

        #region Helpers

        private void ApplyAudioSourceProperties(AudioSource source, JObject parameters)
        {
            var clipPath = ToolHelpers.GetOptionalString(parameters, "clip");
            if (!string.IsNullOrEmpty(clipPath))
            {
                clipPath = ToolHelpers.NormalizeAssetPath(clipPath);
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(clipPath);
                if (clip != null)
                    source.clip = clip;
            }

            if (parameters["volume"] != null)
                source.volume = Mathf.Clamp01(ToolHelpers.GetOptionalFloat(parameters, "volume", 1f));

            if (parameters["pitch"] != null)
                source.pitch = ToolHelpers.GetOptionalFloat(parameters, "pitch", 1f);

            if (parameters["loop"] != null)
                source.loop = ToolHelpers.GetOptionalBool(parameters, "loop", false);

            if (parameters["play_on_awake"] != null)
                source.playOnAwake = ToolHelpers.GetOptionalBool(parameters, "play_on_awake", true);

            if (parameters["spatial_blend"] != null)
                source.spatialBlend = Mathf.Clamp01(ToolHelpers.GetOptionalFloat(parameters, "spatial_blend", 0f));

            if (parameters["min_distance"] != null)
                source.minDistance = ToolHelpers.GetOptionalFloat(parameters, "min_distance", 1f);

            if (parameters["max_distance"] != null)
                source.maxDistance = ToolHelpers.GetOptionalFloat(parameters, "max_distance", 500f);
        }

        private static JObject SerializeAudioSource(GameObject go, AudioSource source)
        {
            var data = new JObject
            {
                ["name"] = go.name,
                ["instanceId"] = go.GetInstanceID(),
                ["clip"] = source.clip != null ? source.clip.name : null,
                ["clipPath"] = source.clip != null ? AssetDatabase.GetAssetPath(source.clip) : null,
                ["volume"] = source.volume,
                ["pitch"] = source.pitch,
                ["loop"] = source.loop,
                ["playOnAwake"] = source.playOnAwake,
                ["spatialBlend"] = source.spatialBlend,
                ["minDistance"] = source.minDistance,
                ["maxDistance"] = source.maxDistance,
                ["isPlaying"] = source.isPlaying,
                ["mute"] = source.mute,
                ["priority"] = source.priority,
                ["rolloffMode"] = source.rolloffMode.ToString(),
                ["dopplerLevel"] = source.dopplerLevel,
                ["spread"] = source.spread,
                ["enabled"] = source.enabled
            };

            return data;
        }

        #endregion
    }
}
