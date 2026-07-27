using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Tools.Infrastructure;
using AgentCore.Editor.Tools.Safety;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Tools.Native.Meta
{
    /// <summary>
    /// Manage Unity EditorPrefs and PlayerPrefs (G08, v1.9.6).
    ///
    /// **CRITICAL SAFETY NOTES** (per plans/v1.10.0-handoff.md §6.3):
    /// - Prefs mutations are **intentionally non-undoable** — Ctrl+Z does NOT reverse them
    /// - `delete` / `delete_all` are irreversible; user should be warned before invocation
    /// - EditorPrefs are per-machine per-Unity-user, not project-scoped
    /// - PlayerPrefs are per-user per-application, persist across builds
    ///
    /// Supported value types:
    /// - EditorPrefs: string / int / float / bool (bool is EditorPrefs-only)
    /// - PlayerPrefs: string / int / float
    ///
    /// Unity does not expose a public API to enumerate PlayerPrefs keys (registry/plist scan required).
    /// list_editor_keys works only for EditorPrefs since 2021 (via EditorPrefs.HasKey probing prefixes is unreliable).
    /// This tool exposes has/get/set/delete/delete_all — enumeration is delegated to platform-specific tooling.
    /// </summary>
    [AgentTool("manage_prefs",
        Description = "Manage Unity EditorPrefs (per-machine editor settings) and PlayerPrefs (per-user application settings). " +
                      "Actions: has (check key exists), get (read value with type), set (write value), delete (remove key), delete_all (**WIPE ALL** — extreme caution). " +
                      "WARNING: Prefs mutations are intentionally non-undoable. Ctrl+Z will NOT reverse them. delete_all is destructive and irreversible. " +
                      "USE FOR: reading/writing editor tool configuration (EditorPrefs), game save state / user prefs (PlayerPrefs), migrating settings, cleaning up stale keys. " +
                      "NOT FOR: project settings (use manage_editor:set_project_setting), scene state, asset metadata. " +
                      "Value types: EditorPrefs supports string / int / float / bool; PlayerPrefs supports string / int / float. " +
                      "ACTIVATE WHEN: user mentions 'editor pref', 'player pref', 'editorprefs', 'playerprefs', 'save my setting', 'clear editor cache', 'delete my saved key'.",
        Category = "Meta",
        RequiresMainThread = true,
        RiskLevel = ToolRiskLevel.High,
        Capabilities = ToolCapability.ModifyProjectSettings,
        RequiresConfirmation = true,
        Visibility = ToolVisibility.OnDemand,
        ReadOnlyActions = new[] { "has", "get" })]
    public class ManagePrefsTool : IAgentTool
    {
        #region Schema

        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""has"", ""get"", ""set"", ""delete"", ""delete_all""],
                    ""description"": ""Action to perform. get/has are read-only. set/delete are destructive (non-undoable). delete_all wipes ALL keys of the chosen store.""
                },
                ""store"": {
                    ""type"": ""string"",
                    ""enum"": [""editor"", ""player""],
                    ""description"": ""'editor' targets EditorPrefs (per-machine); 'player' targets PlayerPrefs (per-user application state).""
                },
                ""key"": {
                    ""type"": ""string"",
                    ""description"": ""Preference key. Required for has/get/set/delete.""
                },
                ""value_type"": {
                    ""type"": ""string"",
                    ""enum"": [""string"", ""int"", ""float"", ""bool""],
                    ""description"": ""Value type for get/set. 'bool' is EditorPrefs-only. For get, this determines which typed accessor is used (Unity does not track type per key).""
                },
                ""value"": {
                    ""description"": ""Value for 'set' action. Type must match value_type. (JSON string / integer / number / boolean.)""
                },
                ""default_value"": {
                    ""description"": ""Optional fallback for 'get' when key does not exist. Type must match value_type.""
                },
                ""confirm_delete_all"": {
                    ""type"": ""boolean"",
                    ""description"": ""(delete_all) Must be set to true to arm the destructive wipe. Guards against accidental invocation.""
                }
            },
            ""required"": [""action"", ""store""]
        }");

        #endregion

        public ToolMetadata Metadata => new ToolMetadata(
            name: "manage_prefs",
            description: "Manage Unity EditorPrefs and PlayerPrefs — destructive operations are not undoable.",
            category: "Meta",
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
                var storeStr = ToolHelpers.GetRequiredString(parameters, "store").ToLowerInvariant();
                if (storeStr != "editor" && storeStr != "player")
                {
                    response = ToolResponse.Fail($"Invalid 'store': '{storeStr}'. Valid: editor, player.");
                }
                else
                {
                    var isEditor = storeStr == "editor";
                    switch (action)
                    {
                        case "has":
                            response = HandleHas(parameters, isEditor);
                            break;
                        case "get":
                            response = HandleGet(parameters, isEditor);
                            break;
                        case "set":
                            response = HandleSet(parameters, isEditor);
                            break;
                        case "delete":
                            response = HandleDelete(parameters, isEditor);
                            break;
                        case "delete_all":
                            response = HandleDeleteAll(parameters, isEditor);
                            break;
                        default:
                            response = ToolResponse.Fail(
                                $"Unknown action: '{action}'. Valid: has, get, set, delete, delete_all.");
                            break;
                    }
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

        #region Handlers

        private static ToolResponse HandleHas(JObject parameters, bool isEditor)
        {
            var key = ToolHelpers.GetRequiredString(parameters, "key");
            bool exists = isEditor ? EditorPrefs.HasKey(key) : PlayerPrefs.HasKey(key);
            var data = new JObject
            {
                ["store"] = isEditor ? "editor" : "player",
                ["key"] = key,
                ["exists"] = exists
            };
            return ToolResponse.OkWithData(data, exists ? $"Key '{key}' exists in {(isEditor ? "EditorPrefs" : "PlayerPrefs")}." : $"Key '{key}' does not exist.");
        }

        private static ToolResponse HandleGet(JObject parameters, bool isEditor)
        {
            var key = ToolHelpers.GetRequiredString(parameters, "key");
            var valueType = ToolHelpers.GetOptionalString(parameters, "value_type", "string").ToLowerInvariant();

            bool exists = isEditor ? EditorPrefs.HasKey(key) : PlayerPrefs.HasKey(key);
            var data = new JObject
            {
                ["store"] = isEditor ? "editor" : "player",
                ["key"] = key,
                ["value_type"] = valueType,
                ["exists"] = exists
            };
            var defaultToken = parameters["default_value"];

            if (!exists && defaultToken == null)
            {
                data["value"] = null;
                return ToolResponse.OkWithData(data, $"Key '{key}' not found. No default_value provided.");
            }

            switch (valueType)
            {
                case "string":
                {
                    string def = defaultToken != null ? defaultToken.ToString() : string.Empty;
                    var v = isEditor ? EditorPrefs.GetString(key, def) : PlayerPrefs.GetString(key, def);
                    data["value"] = v;
                    return ToolResponse.OkWithData(data, $"'{key}' = \"{v}\" (string)");
                }
                case "int":
                {
                    int def = defaultToken != null ? defaultToken.Value<int>() : 0;
                    var v = isEditor ? EditorPrefs.GetInt(key, def) : PlayerPrefs.GetInt(key, def);
                    data["value"] = v;
                    return ToolResponse.OkWithData(data, $"'{key}' = {v} (int)");
                }
                case "float":
                {
                    float def = defaultToken != null ? defaultToken.Value<float>() : 0f;
                    var v = isEditor ? EditorPrefs.GetFloat(key, def) : PlayerPrefs.GetFloat(key, def);
                    data["value"] = v;
                    return ToolResponse.OkWithData(data, $"'{key}' = {v} (float)");
                }
                case "bool":
                {
                    if (!isEditor)
                        return ToolResponse.Fail("bool value_type is only supported for EditorPrefs (store='editor'). PlayerPrefs has no GetBool — represent bools as int 0/1.");
                    bool def = defaultToken != null ? defaultToken.Value<bool>() : false;
                    var v = EditorPrefs.GetBool(key, def);
                    data["value"] = v;
                    return ToolResponse.OkWithData(data, $"'{key}' = {v} (bool)");
                }
                default:
                    return ToolResponse.Fail($"Invalid 'value_type': '{valueType}'. Valid: string, int, float, bool.");
            }
        }

        private static ToolResponse HandleSet(JObject parameters, bool isEditor)
        {
            var key = ToolHelpers.GetRequiredString(parameters, "key");
            var valueType = ToolHelpers.GetRequiredString(parameters, "value_type").ToLowerInvariant();
            var valueToken = parameters["value"];
            if (valueToken == null || valueToken.Type == JTokenType.Null)
                return ToolResponse.Fail("Parameter 'value' is required for set.");

            var data = new JObject
            {
                ["store"] = isEditor ? "editor" : "player",
                ["key"] = key,
                ["value_type"] = valueType,
                ["previously_existed"] = isEditor ? EditorPrefs.HasKey(key) : PlayerPrefs.HasKey(key)
            };

            switch (valueType)
            {
                case "string":
                {
                    var s = valueToken.ToString();
                    if (isEditor) EditorPrefs.SetString(key, s); else PlayerPrefs.SetString(key, s);
                    data["value"] = s;
                    break;
                }
                case "int":
                {
                    if (!ToolHelpers.TryCoerceInt(valueToken, "value", out var v)) throw new ArgumentException($"value expected int, got {valueToken.Type}");
                    if (isEditor) EditorPrefs.SetInt(key, v); else PlayerPrefs.SetInt(key, v);
                    data["value"] = v;
                    break;
                }
                case "float":
                {
                    if (!ToolHelpers.TryCoerceFloat(valueToken, "value", out var v)) throw new ArgumentException($"value expected float, got {valueToken.Type}");
                    if (isEditor) EditorPrefs.SetFloat(key, v); else PlayerPrefs.SetFloat(key, v);
                    data["value"] = v;
                    break;
                }
                case "bool":
                {
                    if (!isEditor)
                        return ToolResponse.Fail("bool value_type is only supported for EditorPrefs. For PlayerPrefs, use int 0/1.");
                    if (!ToolHelpers.TryCoerceBool(valueToken, "value", out var v)) throw new ArgumentException($"value expected bool, got {valueToken.Type}");
                    EditorPrefs.SetBool(key, v);
                    data["value"] = v;
                    break;
                }
                default:
                    return ToolResponse.Fail($"Invalid 'value_type': '{valueType}'. Valid: string, int, float, bool.");
            }

            // PlayerPrefs.Save flushes to disk immediately (EditorPrefs auto-flushes)
            if (!isEditor) PlayerPrefs.Save();
            return ToolResponse.OkWithData(data, $"Set {(isEditor ? "EditorPrefs" : "PlayerPrefs")}['{key}'] = ({valueType}) {data["value"]}. NOTE: This action is not undoable via Ctrl+Z.");
        }

        private static ToolResponse HandleDelete(JObject parameters, bool isEditor)
        {
            var key = ToolHelpers.GetRequiredString(parameters, "key");
            bool existed = isEditor ? EditorPrefs.HasKey(key) : PlayerPrefs.HasKey(key);
            if (isEditor) EditorPrefs.DeleteKey(key); else PlayerPrefs.DeleteKey(key);
            if (!isEditor) PlayerPrefs.Save();
            var data = new JObject
            {
                ["store"] = isEditor ? "editor" : "player",
                ["key"] = key,
                ["previously_existed"] = existed
            };
            return ToolResponse.OkWithData(data,
                existed
                    ? $"Deleted {(isEditor ? "EditorPrefs" : "PlayerPrefs")}['{key}']. This action is NOT undoable — the value is permanently gone."
                    : $"Key '{key}' did not exist. No change made.");
        }

        private static ToolResponse HandleDeleteAll(JObject parameters, bool isEditor)
        {
            var confirm = ToolHelpers.GetOptionalBool(parameters, "confirm_delete_all", false);
            if (!confirm)
            {
                return ToolResponse.Fail(
                    $"delete_all is a destructive operation. To confirm, set 'confirm_delete_all: true'. " +
                    $"This will PERMANENTLY delete ALL {(isEditor ? "EditorPrefs" : "PlayerPrefs")} keys. This action is NOT undoable.");
            }
            if (isEditor)
            {
                EditorPrefs.DeleteAll();
            }
            else
            {
                PlayerPrefs.DeleteAll();
                PlayerPrefs.Save();
            }
            var data = new JObject
            {
                ["store"] = isEditor ? "editor" : "player",
                ["deleted_all"] = true
            };
            return ToolResponse.OkWithData(data,
                $"WIPED ALL {(isEditor ? "EditorPrefs" : "PlayerPrefs")} keys for this {(isEditor ? "machine + Unity user" : "user + application")}. This action is NOT undoable.");
        }

        #endregion
    }
}
