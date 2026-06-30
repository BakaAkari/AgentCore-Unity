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
using UnityEngine.Playables;

namespace AgentCore.Editor.Tools.Native.Specialized
{
    /// <summary>
    /// Manage Unity Timeline: create timeline assets, add/remove tracks, add clips,
    /// set bindings, control playback, and query timeline info.
    /// Uses reflection to access Timeline API (com.unity.timeline package may not be installed).
    /// </summary>
    [AgentTool("manage_timeline",
        Description = "Unity Timeline — sequenced animation, audio, and cinematic authoring. " +
                      "Actions: create_timeline (new TimelineAsset), add_track (Animation/Audio/Activation/Signal/Cinemachine/Control), " +
                      "add_clip (place clip on track with start/duration), remove_track, remove_clip, " +
                      "set_binding (bind track to scene object), get_info (timeline structure: tracks/clips/duration), " +
                      "play/pause/stop (preview playback in Editor). " +
                      "USE FOR: creating cutscene sequences, coordinating animations with audio, " +
                      "setting up camera switches over time, activation tracks to show/hide objects at specific times. " +
                      "NOT FOR: Animator Controller state machines (use manage_animation), individual AnimationClip editing, " +
                      "runtime Playable API programming. " +
                      "PREREQUISITE: com.unity.timeline package must be installed. " +
                      "ACTIVATE WHEN: user mentions 'timeline', 'cutscene', 'sequence', 'cinematic', 'playback', 'tracks and clips'.",
        Category = "Specialized",
        Visibility = ToolVisibility.OnDemand,
        RequiresMainThread = true)]
    public class ManageTimelineTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""create"", ""add_track"", ""remove_track"", ""list_tracks"", ""add_clip"", ""set_duration"", ""play"", ""set_binding"", ""get_info""],
                    ""description"": ""Action to perform on timeline""
                },
                ""target"": { ""type"": ""string"", ""description"": ""Target GameObject with PlayableDirector"" },
                ""targetObject"": { ""type"": ""string"", ""description"": ""GameObject to add PlayableDirector to (for create)"" },
                ""name"": { ""type"": ""string"", ""description"": ""Name for the timeline asset"" },
                ""assetPath"": { ""type"": ""string"", ""description"": ""Asset path to save Timeline (e.g. Assets/Timelines/MyTimeline.playable)"" },
                ""trackType"": {
                    ""type"": ""string"",
                    ""enum"": [""animation"", ""audio"", ""activation"", ""control"", ""signal""],
                    ""description"": ""Type of track to add""
                },
                ""trackName"": { ""type"": ""string"", ""description"": ""Name of the track"" },
                ""bindingObject"": { ""type"": ""string"", ""description"": ""GameObject to bind to the track"" },
                ""start"": { ""type"": ""number"", ""description"": ""Clip start time in seconds"" },
                ""duration"": { ""type"": ""number"", ""description"": ""Duration in seconds"" },
                ""clipAssetPath"": { ""type"": ""string"", ""description"": ""Asset path for clip content (e.g. AnimationClip)"" },
                ""wrapMode"": {
                    ""type"": ""string"",
                    ""enum"": [""none"", ""loop"", ""hold""],
                    ""description"": ""Timeline wrap mode""
                },
                ""state"": {
                    ""type"": ""string"",
                    ""enum"": [""play"", ""pause"", ""stop""],
                    ""description"": ""Playback state""
                }
            },
            ""required"": [""action""]
        }");

        /// <summary>
        /// Tool metadata for registration and LLM discovery.
        /// </summary>
        public ToolMetadata Metadata => new ToolMetadata(
            name: "manage_timeline",
            description: "Manage Unity Timeline: create timeline assets, add/remove tracks, add clips, set bindings, control playback. Requires com.unity.timeline package.",
            category: "Specialized",
            parametersSchema: _parametersSchema,
            requiresMainThread: true
        );

        // Cached reflection types
        private static Type _timelineAssetType;
        private static Type _animationTrackType;
        private static Type _audioTrackType;
        private static Type _activationTrackType;
        private static Type _controlTrackType;
        private static Type _signalTrackType;
        private static Type _trackAssetType;
        private static Type _timelineClipType;
        private static bool _reflectionInitialized;
        private static bool _timelineAvailable;

        /// <summary>
        /// Execute a timeline management action.
        /// </summary>
        public Task<ToolResult> ExecuteAsync(JObject parameters, CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();
            ToolResponse response;

            try
            {
                if (!EnsureTimelineTypes())
                {
                    response = ToolResponse.Fail(
                        "Timeline package is not installed. Please install 'com.unity.timeline' via Package Manager (Window > Package Manager > Unity Registry > Timeline).");
                    sw.Stop();
                    return Task.FromResult(response.ToToolResult(sw.Elapsed.TotalMilliseconds));
                }

                var action = ToolHelpers.GetRequiredString(parameters, "action").ToLowerInvariant();

                switch (action)
                {
                    case "create":
                        response = HandleCreate(parameters);
                        break;
                    case "add_track":
                        response = HandleAddTrack(parameters);
                        break;
                    case "remove_track":
                        response = HandleRemoveTrack(parameters);
                        break;
                    case "list_tracks":
                        response = HandleListTracks(parameters);
                        break;
                    case "add_clip":
                        response = HandleAddClip(parameters);
                        break;
                    case "set_duration":
                        response = HandleSetDuration(parameters);
                        break;
                    case "play":
                        response = HandlePlay(parameters);
                        break;
                    case "set_binding":
                        response = HandleSetBinding(parameters);
                        break;
                    case "get_info":
                        response = HandleGetInfo(parameters);
                        break;
                    default:
                        response = ToolResponse.Fail($"Unknown action: {action}. Valid actions: create, add_track, remove_track, list_tracks, add_clip, set_duration, play, set_binding, get_info");
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
        /// Initialize Timeline types via reflection. Returns true if Timeline package is available.
        /// </summary>
        private static bool EnsureTimelineTypes()
        {
            if (_reflectionInitialized)
                return _timelineAvailable;

            _reflectionInitialized = true;

            _timelineAssetType = Type.GetType("UnityEngine.Timeline.TimelineAsset, Unity.Timeline");
            if (_timelineAssetType == null)
            {
                _timelineAvailable = false;
                return false;
            }

            _trackAssetType = Type.GetType("UnityEngine.Timeline.TrackAsset, Unity.Timeline");
            _animationTrackType = Type.GetType("UnityEngine.Timeline.AnimationTrack, Unity.Timeline");
            _audioTrackType = Type.GetType("UnityEngine.Timeline.AudioTrack, Unity.Timeline");
            _activationTrackType = Type.GetType("UnityEngine.Timeline.ActivationTrack, Unity.Timeline");
            _controlTrackType = Type.GetType("UnityEngine.Timeline.ControlTrack, Unity.Timeline");
            _signalTrackType = Type.GetType("UnityEngine.Timeline.SignalTrack, Unity.Timeline");
            _timelineClipType = Type.GetType("UnityEngine.Timeline.TimelineClip, Unity.Timeline");

            _timelineAvailable = true;
            return true;
        }

        /// <summary>
        /// Get the track Type for a given track type name.
        /// </summary>
        private static Type GetTrackType(string trackType)
        {
            switch (trackType.ToLowerInvariant())
            {
                case "animation": return _animationTrackType;
                case "audio": return _audioTrackType;
                case "activation": return _activationTrackType;
                case "control": return _controlTrackType;
                case "signal": return _signalTrackType;
                default: return null;
            }
        }

        #endregion

        #region Action Handlers

        /// <summary>
        /// Create a new Timeline asset and attach a PlayableDirector to a GameObject.
        /// </summary>
        private ToolResponse HandleCreate(JObject parameters)
        {
            var name = ToolHelpers.GetOptionalString(parameters, "name", "New Timeline");
            var targetObject = ToolHelpers.GetOptionalString(parameters, "targetObject", null);
            var assetPath = ToolHelpers.GetOptionalString(parameters, "assetPath", $"Assets/Timelines/{name}.playable");

            var normalizedPath = ToolHelpers.NormalizeAssetPath(assetPath);
            ToolHelpers.EnsureDirectoryExists(normalizedPath);

            // Create TimelineAsset via ScriptableObject.CreateInstance
            var timelineAsset = ScriptableObject.CreateInstance(_timelineAssetType);
            if (timelineAsset == null)
                return ToolResponse.Fail("Failed to create TimelineAsset instance.");

            timelineAsset.name = name;
            AssetDatabase.CreateAsset(timelineAsset, normalizedPath);
            AssetDatabase.SaveAssets();

            // Attach PlayableDirector to target GameObject
            GameObject go = null;
            if (!string.IsNullOrEmpty(targetObject))
            {
                go = ToolHelpers.FindGameObject(targetObject);
                if (go == null)
                    return ToolResponse.Fail($"Target GameObject '{targetObject}' not found.");
            }
            else
            {
                // Create a new GameObject for the director
                go = new GameObject(name);
                ToolHelpers.RegisterCreatedObject(go, "Create Timeline");
            }

            ToolHelpers.RecordUndo(go, "Add PlayableDirector");
            var director = go.GetComponent<PlayableDirector>();
            if (director == null)
                director = go.AddComponent<PlayableDirector>();

            director.playableAsset = timelineAsset as PlayableAsset;

            return ToolResponse.OkWithData(new
            {
                gameObject = go.name,
                assetPath = normalizedPath,
                timelineName = name
            }, $"Created Timeline '{name}' at '{normalizedPath}' with PlayableDirector on '{go.name}'");
        }

        /// <summary>
        /// Add a track to the Timeline asset.
        /// </summary>
        private ToolResponse HandleAddTrack(JObject parameters)
        {
            var director = FindDirector(parameters);
            if (director == null)
                return ToolResponse.Fail("PlayableDirector not found. Provide 'target' with the GameObject name.");

            var timelineAsset = director.playableAsset;
            if (timelineAsset == null)
                return ToolResponse.Fail("PlayableDirector has no Timeline asset assigned.");

            var trackTypeStr = ToolHelpers.GetRequiredString(parameters, "trackType");
            var trackName = ToolHelpers.GetOptionalString(parameters, "trackName", null);
            var bindingObjectName = ToolHelpers.GetOptionalString(parameters, "bindingObject", null);

            var trackType = GetTrackType(trackTypeStr);
            if (trackType == null)
                return ToolResponse.Fail($"Unknown track type: {trackTypeStr}. Valid types: animation, audio, activation, control, signal");

            ToolHelpers.RecordUndo(timelineAsset as UnityEngine.Object, "Add Timeline Track");

            // Call TimelineAsset.CreateTrack(Type, TrackAsset parent, string name)
            var createTrackMethod = _timelineAssetType.GetMethod("CreateTrack",
                new Type[] { typeof(Type), _trackAssetType, typeof(string) });

            if (createTrackMethod == null)
                return ToolResponse.Fail("Could not find CreateTrack method on TimelineAsset.");

            var track = createTrackMethod.Invoke(timelineAsset, new object[] { trackType, null, trackName ?? trackTypeStr });
            if (track == null)
                return ToolResponse.Fail("Failed to create track.");

            // Set binding if specified
            if (!string.IsNullOrEmpty(bindingObjectName))
            {
                var bindingGo = ToolHelpers.FindGameObject(bindingObjectName);
                if (bindingGo != null)
                {
                    SetTrackBinding(director, track, bindingGo, trackTypeStr);
                }
            }

            EditorUtility.SetDirty(timelineAsset as UnityEngine.Object);
            AssetDatabase.SaveAssets();

            // Get track name via reflection
            var trackNameProp = _trackAssetType.GetProperty("name");
            var actualName = trackNameProp?.GetValue(track) as string ?? trackTypeStr;

            return ToolResponse.OkWithData(new
            {
                trackType = trackTypeStr,
                trackName = actualName,
                bindingObject = bindingObjectName
            }, $"Added {trackTypeStr} track '{actualName}' to timeline");
        }

        /// <summary>
        /// Remove a track from the Timeline asset by name.
        /// </summary>
        private ToolResponse HandleRemoveTrack(JObject parameters)
        {
            var director = FindDirector(parameters);
            if (director == null)
                return ToolResponse.Fail("PlayableDirector not found. Provide 'target' with the GameObject name.");

            var timelineAsset = director.playableAsset;
            if (timelineAsset == null)
                return ToolResponse.Fail("PlayableDirector has no Timeline asset assigned.");

            var trackName = ToolHelpers.GetRequiredString(parameters, "trackName");

            ToolHelpers.RecordUndo(timelineAsset as UnityEngine.Object, "Remove Timeline Track");

            // Get all tracks
            var tracks = GetTracks(timelineAsset);
            object targetTrack = null;

            foreach (var track in tracks)
            {
                var nameProp = _trackAssetType.GetProperty("name");
                var name = nameProp?.GetValue(track) as string;
                if (string.Equals(name, trackName, StringComparison.OrdinalIgnoreCase))
                {
                    targetTrack = track;
                    break;
                }
            }

            if (targetTrack == null)
                return ToolResponse.Fail($"Track '{trackName}' not found in timeline.");

            // Call TimelineAsset.DeleteTrack(TrackAsset)
            var deleteMethod = _timelineAssetType.GetMethod("DeleteTrack", new Type[] { _trackAssetType });
            if (deleteMethod == null)
                return ToolResponse.Fail("Could not find DeleteTrack method on TimelineAsset.");

            deleteMethod.Invoke(timelineAsset, new object[] { targetTrack });

            EditorUtility.SetDirty(timelineAsset as UnityEngine.Object);
            AssetDatabase.SaveAssets();

            return ToolResponse.Ok($"Removed track '{trackName}' from timeline");
        }

        /// <summary>
        /// List all tracks in the Timeline asset.
        /// </summary>
        private ToolResponse HandleListTracks(JObject parameters)
        {
            var director = FindDirector(parameters);
            if (director == null)
                return ToolResponse.Fail("PlayableDirector not found. Provide 'target' with the GameObject name.");

            var timelineAsset = director.playableAsset;
            if (timelineAsset == null)
                return ToolResponse.Fail("PlayableDirector has no Timeline asset assigned.");

            var tracks = GetTracks(timelineAsset);
            var trackInfos = new List<object>();

            foreach (var track in tracks)
            {
                var nameProp = _trackAssetType.GetProperty("name");
                var trackName = nameProp?.GetValue(track) as string ?? "Unnamed";

                var typeName = track.GetType().Name;

                // Get clip count via GetClips()
                var getClipsMethod = _trackAssetType.GetMethod("GetClips");
                int clipCount = 0;
                if (getClipsMethod != null)
                {
                    var clips = getClipsMethod.Invoke(track, null) as System.Collections.IEnumerable;
                    if (clips != null)
                    {
                        foreach (var _ in clips)
                            clipCount++;
                    }
                }

                // Check if muted
                var mutedProp = _trackAssetType.GetProperty("muted");
                bool muted = mutedProp != null && (bool)mutedProp.GetValue(track);

                // Check if locked
                var lockedProp = _trackAssetType.GetProperty("locked");
                bool locked = lockedProp != null && (bool)lockedProp.GetValue(track);

                trackInfos.Add(new
                {
                    name = trackName,
                    type = typeName,
                    clipCount,
                    muted,
                    locked
                });
            }

            return ToolResponse.OkWithData(new
            {
                trackCount = trackInfos.Count,
                tracks = trackInfos
            }, $"Found {trackInfos.Count} tracks in timeline");
        }

        /// <summary>
        /// Add a clip to a track.
        /// </summary>
        private ToolResponse HandleAddClip(JObject parameters)
        {
            var director = FindDirector(parameters);
            if (director == null)
                return ToolResponse.Fail("PlayableDirector not found. Provide 'target' with the GameObject name.");

            var timelineAsset = director.playableAsset;
            if (timelineAsset == null)
                return ToolResponse.Fail("PlayableDirector has no Timeline asset assigned.");

            var trackName = ToolHelpers.GetRequiredString(parameters, "trackName");
            var start = ToolHelpers.GetOptionalFloat(parameters, "start", 0f);
            var duration = ToolHelpers.GetOptionalFloat(parameters, "duration", 1f);
            var clipAssetPath = ToolHelpers.GetOptionalString(parameters, "clipAssetPath", null);

            // Find the track
            var track = FindTrackByName(timelineAsset, trackName);
            if (track == null)
                return ToolResponse.Fail($"Track '{trackName}' not found in timeline.");

            ToolHelpers.RecordUndo(timelineAsset as UnityEngine.Object, "Add Timeline Clip");

            object clip = null;

            // If clipAssetPath is provided, try to create clip from asset
            if (!string.IsNullOrEmpty(clipAssetPath))
            {
                var clipAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(clipAssetPath);
                if (clipAsset == null)
                    return ToolResponse.Fail($"Clip asset not found at path: {clipAssetPath}");

                // Try CreateClip with the asset type
                var createClipGeneric = _trackAssetType.GetMethods()
                    .FirstOrDefault(m => m.Name == "CreateClip" && m.IsGenericMethod);

                if (createClipGeneric != null && clipAsset is AnimationClip)
                {
                    // For AnimationTrack, use CreateClip<AnimationPlayableAsset>
                    var animPlayableType = Type.GetType("UnityEngine.Timeline.AnimationPlayableAsset, Unity.Timeline");
                    if (animPlayableType != null)
                    {
                        var specificCreate = createClipGeneric.MakeGenericMethod(animPlayableType);
                        clip = specificCreate.Invoke(track, null);

                        // Set the clip reference on the AnimationPlayableAsset
                        if (clip != null && _timelineClipType != null)
                        {
                            var assetProp = _timelineClipType.GetProperty("asset");
                            var playableAsset = assetProp?.GetValue(clip);
                            if (playableAsset != null)
                            {
                                var clipProp = animPlayableType.GetProperty("clip");
                                clipProp?.SetValue(playableAsset, clipAsset);
                            }
                        }
                    }
                }
            }

            // Fallback: create a default clip
            if (clip == null)
            {
                var createDefaultClip = _trackAssetType.GetMethod("CreateDefaultClip");
                if (createDefaultClip != null)
                {
                    clip = createDefaultClip.Invoke(track, null);
                }
            }

            if (clip == null)
                return ToolResponse.Fail("Failed to create clip on track.");

            // Set start and duration
            if (_timelineClipType != null)
            {
                var startProp = _timelineClipType.GetProperty("start");
                startProp?.SetValue(clip, (double)start);

                var durationProp = _timelineClipType.GetProperty("duration");
                durationProp?.SetValue(clip, (double)duration);
            }

            EditorUtility.SetDirty(timelineAsset as UnityEngine.Object);
            AssetDatabase.SaveAssets();

            return ToolResponse.OkWithData(new
            {
                trackName,
                start,
                duration,
                clipAssetPath
            }, $"Added clip to track '{trackName}' (start={start}s, duration={duration}s)");
        }

        /// <summary>
        /// Set the timeline duration and wrap mode.
        /// </summary>
        private ToolResponse HandleSetDuration(JObject parameters)
        {
            var director = FindDirector(parameters);
            if (director == null)
                return ToolResponse.Fail("PlayableDirector not found. Provide 'target' with the GameObject name.");

            var timelineAsset = director.playableAsset;
            if (timelineAsset == null)
                return ToolResponse.Fail("PlayableDirector has no Timeline asset assigned.");

            var duration = ToolHelpers.GetOptionalFloat(parameters, "duration", -1f);
            var wrapModeStr = ToolHelpers.GetOptionalString(parameters, "wrapMode", null);

            ToolHelpers.RecordUndo(timelineAsset as UnityEngine.Object, "Set Timeline Duration");

            if (duration > 0f)
            {
                // Set fixedDuration on TimelineAsset
                var fixedDurationProp = _timelineAssetType.GetProperty("fixedDuration");
                fixedDurationProp?.SetValue(timelineAsset, (double)duration);

                // Set durationMode to FixedLength
                var durationModeType = Type.GetType("UnityEngine.Timeline.TimelineAsset+DurationMode, Unity.Timeline");
                var durationModeProp = _timelineAssetType.GetProperty("durationMode");
                if (durationModeType != null && durationModeProp != null)
                {
                    var fixedLengthValue = Enum.Parse(durationModeType, "FixedLength");
                    durationModeProp.SetValue(timelineAsset, fixedLengthValue);
                }
            }

            if (!string.IsNullOrEmpty(wrapModeStr))
            {
                ToolHelpers.RecordUndo(director, "Set Timeline Wrap Mode");
                switch (wrapModeStr.ToLowerInvariant())
                {
                    case "none":
                        director.extrapolationMode = DirectorWrapMode.None;
                        break;
                    case "loop":
                        director.extrapolationMode = DirectorWrapMode.Loop;
                        break;
                    case "hold":
                        director.extrapolationMode = DirectorWrapMode.Hold;
                        break;
                }
            }

            EditorUtility.SetDirty(timelineAsset as UnityEngine.Object);
            AssetDatabase.SaveAssets();

            return ToolResponse.OkWithData(new
            {
                duration,
                wrapMode = wrapModeStr ?? director.extrapolationMode.ToString()
            }, $"Set timeline duration={duration}s, wrapMode={wrapModeStr ?? director.extrapolationMode.ToString()}");
        }

        /// <summary>
        /// Control timeline playback (play/pause/stop).
        /// </summary>
        private ToolResponse HandlePlay(JObject parameters)
        {
            var director = FindDirector(parameters);
            if (director == null)
                return ToolResponse.Fail("PlayableDirector not found. Provide 'target' with the GameObject name.");

            var state = ToolHelpers.GetRequiredString(parameters, "state").ToLowerInvariant();

            switch (state)
            {
                case "play":
                    director.time = 0;
                    director.Play();
                    return ToolResponse.Ok("Timeline playback started");

                case "pause":
                    director.Pause();
                    return ToolResponse.OkWithData(new { currentTime = director.time }, "Timeline paused");

                case "stop":
                    director.Stop();
                    director.time = 0;
                    return ToolResponse.Ok("Timeline stopped");

                default:
                    return ToolResponse.Fail($"Unknown state: {state}. Valid states: play, pause, stop");
            }
        }

        /// <summary>
        /// Set the binding object for a track.
        /// </summary>
        private ToolResponse HandleSetBinding(JObject parameters)
        {
            var director = FindDirector(parameters);
            if (director == null)
                return ToolResponse.Fail("PlayableDirector not found. Provide 'target' with the GameObject name.");

            var timelineAsset = director.playableAsset;
            if (timelineAsset == null)
                return ToolResponse.Fail("PlayableDirector has no Timeline asset assigned.");

            var trackName = ToolHelpers.GetRequiredString(parameters, "trackName");
            var bindingObjectName = ToolHelpers.GetRequiredString(parameters, "bindingObject");

            var track = FindTrackByName(timelineAsset, trackName);
            if (track == null)
                return ToolResponse.Fail($"Track '{trackName}' not found in timeline.");

            var bindingGo = ToolHelpers.FindGameObject(bindingObjectName);
            if (bindingGo == null)
                return ToolResponse.Fail($"Binding GameObject '{bindingObjectName}' not found.");

            ToolHelpers.RecordUndo(director, "Set Timeline Binding");

            // Determine the appropriate component to bind based on track type
            var trackType = track.GetType();
            UnityEngine.Object bindingTarget = bindingGo;

            if (trackType == _animationTrackType)
            {
                var animator = bindingGo.GetComponent<Animator>();
                if (animator == null)
                    animator = bindingGo.AddComponent<Animator>();
                bindingTarget = animator;
            }
            else if (trackType == _audioTrackType)
            {
                var audioSource = bindingGo.GetComponent<AudioSource>();
                if (audioSource == null)
                    audioSource = bindingGo.AddComponent<AudioSource>();
                bindingTarget = audioSource;
            }

            director.SetGenericBinding(track as UnityEngine.Object, bindingTarget);

            return ToolResponse.OkWithData(new
            {
                trackName,
                bindingObject = bindingObjectName,
                bindingType = bindingTarget.GetType().Name
            }, $"Set binding for track '{trackName}' to '{bindingObjectName}'");
        }

        /// <summary>
        /// Get information about the Timeline asset and PlayableDirector.
        /// </summary>
        private ToolResponse HandleGetInfo(JObject parameters)
        {
            var director = FindDirector(parameters);
            if (director == null)
                return ToolResponse.Fail("PlayableDirector not found. Provide 'target' with the GameObject name.");

            var timelineAsset = director.playableAsset;
            if (timelineAsset == null)
                return ToolResponse.Fail("PlayableDirector has no Timeline asset assigned.");

            // Get duration
            var durationProp = _timelineAssetType.GetProperty("duration");
            double duration = durationProp != null ? (double)durationProp.GetValue(timelineAsset) : 0;

            // Get output track count
            var outputTrackCountProp = _timelineAssetType.GetProperty("outputTrackCount");
            int outputTrackCount = outputTrackCountProp != null ? (int)outputTrackCountProp.GetValue(timelineAsset) : 0;

            // Get root track count
            var rootTrackCountProp = _timelineAssetType.GetProperty("rootTrackCount");
            int rootTrackCount = rootTrackCountProp != null ? (int)rootTrackCountProp.GetValue(timelineAsset) : 0;

            // Get frame rate
            var frameRateProp = _timelineAssetType.GetProperty("editorSettings");
            double frameRate = 60;
            if (frameRateProp != null)
            {
                var editorSettings = frameRateProp.GetValue(timelineAsset);
                if (editorSettings != null)
                {
                    var fpsProp = editorSettings.GetType().GetProperty("frameRate");
                    if (fpsProp != null)
                        frameRate = Convert.ToDouble(fpsProp.GetValue(editorSettings));
                }
            }

            // Get duration mode
            var durationModeProp = _timelineAssetType.GetProperty("durationMode");
            string durationMode = durationModeProp != null ? durationModeProp.GetValue(timelineAsset)?.ToString() : "Unknown";

            // List tracks summary
            var tracks = GetTracks(timelineAsset);
            var trackSummary = new List<object>();
            foreach (var track in tracks)
            {
                var nameProp = _trackAssetType.GetProperty("name");
                var tName = nameProp?.GetValue(track) as string ?? "Unnamed";
                trackSummary.Add(new { name = tName, type = track.GetType().Name });
            }

            return ToolResponse.OkWithData(new
            {
                gameObject = director.gameObject.name,
                assetPath = AssetDatabase.GetAssetPath(timelineAsset),
                duration,
                durationMode,
                frameRate,
                outputTrackCount,
                rootTrackCount,
                wrapMode = director.extrapolationMode.ToString(),
                playState = director.state.ToString(),
                currentTime = director.time,
                tracks = trackSummary
            }, $"Timeline info for '{director.gameObject.name}'");
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Find a PlayableDirector from the 'target' parameter.
        /// </summary>
        private PlayableDirector FindDirector(JObject parameters)
        {
            var target = ToolHelpers.GetOptionalString(parameters, "target", null);

            if (!string.IsNullOrEmpty(target))
            {
                var go = ToolHelpers.FindGameObject(target);
                if (go != null)
                    return go.GetComponent<PlayableDirector>();
            }

            // Fallback: find any PlayableDirector in scene
            return UnityEngine.Object.FindObjectOfType<PlayableDirector>();
        }

        /// <summary>
        /// Get all output tracks from a TimelineAsset via reflection.
        /// </summary>
        private List<object> GetTracks(PlayableAsset timelineAsset)
        {
            var result = new List<object>();

            var getOutputTracksMethod = _timelineAssetType.GetMethod("GetOutputTracks");
            if (getOutputTracksMethod != null)
            {
                var tracks = getOutputTracksMethod.Invoke(timelineAsset, null) as System.Collections.IEnumerable;
                if (tracks != null)
                {
                    foreach (var track in tracks)
                        result.Add(track);
                }
            }

            return result;
        }

        /// <summary>
        /// Find a track by name in the TimelineAsset.
        /// </summary>
        private object FindTrackByName(PlayableAsset timelineAsset, string trackName)
        {
            var tracks = GetTracks(timelineAsset);
            foreach (var track in tracks)
            {
                var nameProp = _trackAssetType.GetProperty("name");
                var name = nameProp?.GetValue(track) as string;
                if (string.Equals(name, trackName, StringComparison.OrdinalIgnoreCase))
                    return track;
            }
            return null;
        }

        /// <summary>
        /// Set the binding for a track on the PlayableDirector.
        /// </summary>
        private void SetTrackBinding(PlayableDirector director, object track, GameObject bindingGo, string trackTypeStr)
        {
            UnityEngine.Object bindingTarget = bindingGo;

            switch (trackTypeStr.ToLowerInvariant())
            {
                case "animation":
                    var animator = bindingGo.GetComponent<Animator>();
                    if (animator == null)
                        animator = bindingGo.AddComponent<Animator>();
                    bindingTarget = animator;
                    break;
                case "audio":
                    var audioSource = bindingGo.GetComponent<AudioSource>();
                    if (audioSource == null)
                        audioSource = bindingGo.AddComponent<AudioSource>();
                    bindingTarget = audioSource;
                    break;
            }

            director.SetGenericBinding(track as UnityEngine.Object, bindingTarget);
        }

        #endregion
    }
}
