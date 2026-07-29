using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;
using UnityEditor;
using UnityEditorInternal;
using Newtonsoft.Json.Linq;
using AgentCore.Editor.Tools.Infrastructure;

namespace AgentCore.Editor.Tools.Native.Extended
{
    /// <summary>
    /// Manage Unity tags, layers, and sorting layers.
    /// Provides CRUD operations for tags, layers, sorting layers, and object tag/layer assignment.
    /// </summary>
    [AgentTool("manage_tags_layers",
        Description = "Unity Tags, Layers, and Sorting Layers management (Edit > Project Settings > Tags and Layers). " +
                      "Actions: list_tags, add_tag, remove_tag, list_layers, set_layer (assign layer to slot 8-31), " +
                      "list_sorting_layers, add_sorting_layer, remove_sorting_layer, assign_tag (set tag on GameObject), assign_layer (set layer on GameObject). " +
                      "USE FOR: creating custom tags for gameplay logic (CompareTag), setting up physics/rendering layers for LayerMask filtering, " +
                      "managing 2D sprite render order via sorting layers, bulk-assigning tags/layers to objects. " +
                      "NOT FOR: querying objects by tag/layer (use find_gameobjects with tag/layer filter), " +
                      "physics layer collision matrix (use manage_editor get_project_settings). " +
                      "ACTIVATE WHEN: user mentions 'tags', 'layers', 'sorting layer', 'LayerMask', 'add tag', 'custom layer'.",
        Category = "Extended",
        RequiresMainThread = true,
        Visibility = ToolVisibility.OnDemand)]
    public class ManageTagsLayersTool : IAgentTool
    {
        #region Schema

        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""list_tags"", ""add_tag"", ""remove_tag"", ""list_layers"", ""set_layer"", ""list_sorting_layers"", ""add_sorting_layer"", ""set_object_tag"", ""set_object_layer""],
                    ""description"": ""Action to perform""
                },
                ""tag"": {
                    ""type"": ""string"",
                    ""description"": ""Tag name (for add_tag, remove_tag, set_object_tag actions)""
                },
                ""index"": {
                    ""type"": ""integer"",
                    ""description"": ""Layer index 8-31 for user layers (for set_layer action)""
                },
                ""name"": {
                    ""type"": ""string"",
                    ""description"": ""Layer or sorting layer name (for set_layer, add_sorting_layer actions)""
                },
                ""target"": {
                    ""type"": ""string"",
                    ""description"": ""Target GameObject name or path (for set_object_tag, set_object_layer actions)""
                },
                ""layer"": {
                    ""type"": [""string"", ""integer""],
                    ""description"": ""Layer name or index (for set_object_layer action)""
                },
                ""include_children"": {
                    ""type"": ""boolean"",
                    ""description"": ""Whether to apply layer to all children (for set_object_layer action, default: false)""
                }
            },
            ""required"": [""action""]
        }");

        #endregion

        public ToolMetadata Metadata => new ToolMetadata(
            name: "manage_tags_layers",
            description: "Manage Unity tags, layers, and sorting layers",
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
                    case "list_tags":
                        response = HandleListTags();
                        break;
                    case "add_tag":
                        response = HandleAddTag(parameters);
                        break;
                    case "remove_tag":
                        response = HandleRemoveTag(parameters);
                        break;
                    case "list_layers":
                        response = HandleListLayers();
                        break;
                    case "set_layer":
                        response = HandleSetLayer(parameters);
                        break;
                    case "list_sorting_layers":
                        response = HandleListSortingLayers();
                        break;
                    case "add_sorting_layer":
                        response = HandleAddSortingLayer(parameters);
                        break;
                    case "set_object_tag":
                        response = HandleSetObjectTag(parameters);
                        break;
                    case "set_object_layer":
                        response = HandleSetObjectLayer(parameters);
                        break;
                    default:
                        response = ToolResponse.Fail(
                            $"Unknown action: '{action}'. Valid actions: list_tags, add_tag, remove_tag, list_layers, set_layer, list_sorting_layers, add_sorting_layer, set_object_tag, set_object_layer");
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

        private ToolResponse HandleListTags()
        {
            var tags = InternalEditorUtility.tags;
            var tagList = new JArray();
            foreach (var tag in tags)
            {
                tagList.Add(tag);
            }

            var data = new JObject
            {
                ["tags"] = tagList,
                ["count"] = tags.Length
            };

            return ToolResponse.OkWithData(data, $"Found {tags.Length} tag(s).");
        }

        private ToolResponse HandleAddTag(JObject parameters)
        {
            var tag = ToolHelpers.GetRequiredString(parameters, "tag");

            // Check if tag already exists
            var existingTags = InternalEditorUtility.tags;
            if (existingTags.Contains(tag))
            {
                return ToolResponse.Fail($"Tag '{tag}' already exists.");
            }

            // Add tag via TagManager SerializedObject
            var tagManager = GetTagManager();
            if (tagManager == null)
            {
                return ToolResponse.Fail("Failed to load TagManager asset.");
            }

            var tagsProp = tagManager.FindProperty("tags");
            if (tagsProp == null)
            {
                return ToolResponse.Fail("Failed to find 'tags' property in TagManager.");
            }

            // Add new tag at the end
            tagsProp.InsertArrayElementAtIndex(tagsProp.arraySize);
            var newTag = tagsProp.GetArrayElementAtIndex(tagsProp.arraySize - 1);
            newTag.stringValue = tag;
            tagManager.ApplyModifiedProperties();

            var data = new JObject
            {
                ["tag"] = tag,
                ["total_tags"] = InternalEditorUtility.tags.Length
            };

            return ToolResponse.OkWithData(data, $"Tag '{tag}' added successfully.");
        }

        private ToolResponse HandleRemoveTag(JObject parameters)
        {
            var tag = ToolHelpers.GetRequiredString(parameters, "tag");

            // Check built-in tags that cannot be removed
            var builtInTags = new[] { "Untagged", "Respawn", "Finish", "EditorOnly", "MainCamera", "Player", "GameController" };
            if (builtInTags.Contains(tag))
            {
                return ToolResponse.Fail($"Cannot remove built-in tag '{tag}'.");
            }

            var tagManager = GetTagManager();
            if (tagManager == null)
            {
                return ToolResponse.Fail("Failed to load TagManager asset.");
            }

            var tagsProp = tagManager.FindProperty("tags");
            if (tagsProp == null)
            {
                return ToolResponse.Fail("Failed to find 'tags' property in TagManager.");
            }

            // Find and remove the tag
            bool found = false;
            for (int i = 0; i < tagsProp.arraySize; i++)
            {
                if (tagsProp.GetArrayElementAtIndex(i).stringValue == tag)
                {
                    tagsProp.DeleteArrayElementAtIndex(i);
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return ToolResponse.Fail($"Tag '{tag}' not found in custom tags.");
            }

            tagManager.ApplyModifiedProperties();

            return ToolResponse.Ok($"Tag '{tag}' removed successfully.");
        }

        private ToolResponse HandleListLayers()
        {
            var namedLayers = InternalEditorUtility.layers;
            var layerList = new JArray();

            // List all 32 layers with their names
            for (int i = 0; i < 32; i++)
            {
                var layerName = LayerMask.LayerToName(i);
                if (!string.IsNullOrEmpty(layerName))
                {
                    layerList.Add(new JObject
                    {
                        ["index"] = i,
                        ["name"] = layerName,
                        ["is_builtin"] = i < 8
                    });
                }
            }

            var data = new JObject
            {
                ["layers"] = layerList,
                ["named_count"] = namedLayers.Length,
                ["user_layer_range"] = "8-31"
            };

            return ToolResponse.OkWithData(data, $"Found {namedLayers.Length} named layer(s).");
        }

        private ToolResponse HandleSetLayer(JObject parameters)
        {
            var index = ToolHelpers.GetOptionalInt(parameters, "index", -1);
            var name = ToolHelpers.GetRequiredString(parameters, "name");

            if (index < 8 || index > 31)
            {
                return ToolResponse.Fail($"Layer index must be between 8 and 31 (user layers). Got: {index}");
            }

            var tagManager = GetTagManager();
            if (tagManager == null)
            {
                return ToolResponse.Fail("Failed to load TagManager asset.");
            }

            var layersProp = tagManager.FindProperty("layers");
            if (layersProp == null)
            {
                return ToolResponse.Fail("Failed to find 'layers' property in TagManager.");
            }

            if (index >= layersProp.arraySize)
            {
                return ToolResponse.Fail($"Layer index {index} is out of range (array size: {layersProp.arraySize}).");
            }

            var previousName = layersProp.GetArrayElementAtIndex(index).stringValue;
            layersProp.GetArrayElementAtIndex(index).stringValue = name;
            tagManager.ApplyModifiedProperties();

            var data = new JObject
            {
                ["index"] = index,
                ["name"] = name,
                ["previous_name"] = previousName ?? "(empty)"
            };

            return ToolResponse.OkWithData(data, $"Layer {index} set to '{name}'.");
        }

        private ToolResponse HandleListSortingLayers()
        {
            var sortingLayers = SortingLayer.layers;
            var layerList = new JArray();

            foreach (var layer in sortingLayers)
            {
                layerList.Add(new JObject
                {
                    ["id"] = layer.id,
                    ["name"] = layer.name,
                    ["value"] = layer.value
                });
            }

            var data = new JObject
            {
                ["sorting_layers"] = layerList,
                ["count"] = sortingLayers.Length
            };

            return ToolResponse.OkWithData(data, $"Found {sortingLayers.Length} sorting layer(s).");
        }

        private ToolResponse HandleAddSortingLayer(JObject parameters)
        {
            var name = ToolHelpers.GetRequiredString(parameters, "name");

            // Check if sorting layer already exists
            if (SortingLayer.layers.Any(l => l.name == name))
            {
                return ToolResponse.Fail($"Sorting layer '{name}' already exists.");
            }

            var tagManager = GetTagManager();
            if (tagManager == null)
            {
                return ToolResponse.Fail("Failed to load TagManager asset.");
            }

            var sortingLayersProp = tagManager.FindProperty("m_SortingLayers");
            if (sortingLayersProp == null)
            {
                return ToolResponse.Fail("Failed to find 'm_SortingLayers' property in TagManager.");
            }

            // Add new sorting layer
            sortingLayersProp.InsertArrayElementAtIndex(sortingLayersProp.arraySize);
            var newLayer = sortingLayersProp.GetArrayElementAtIndex(sortingLayersProp.arraySize - 1);

            // Set the name
            var nameProp = newLayer.FindPropertyRelative("name");
            if (nameProp != null)
            {
                nameProp.stringValue = name;
            }

            // Generate a unique ID
            var uniqueIdProp = newLayer.FindPropertyRelative("uniqueID");
            if (uniqueIdProp != null)
            {
                uniqueIdProp.intValue = GenerateUniqueSortingLayerId(sortingLayersProp);
            }

            tagManager.ApplyModifiedProperties();

            var data = new JObject
            {
                ["name"] = name,
                ["total_sorting_layers"] = SortingLayer.layers.Length
            };

            return ToolResponse.OkWithData(data, $"Sorting layer '{name}' added successfully.");
        }

        private ToolResponse HandleSetObjectTag(JObject parameters)
        {
            var targetName = ToolHelpers.GetRequiredString(parameters, "target");
            var tag = ToolHelpers.GetRequiredString(parameters, "tag");

            var go = ToolHelpers.FindGameObject(targetName);
            if (go == null)
            {
                return ToolResponse.Fail($"GameObject '{targetName}' not found.");
            }

            // Verify tag exists
            var existingTags = InternalEditorUtility.tags;
            if (!existingTags.Contains(tag))
            {
                return ToolResponse.Fail($"Tag '{tag}' does not exist. Add it first using add_tag action.");
            }

            var previousTag = go.tag;
            Undo.RecordObject(go, "AgentCore: Set Tag");
            go.tag = tag;

            var data = new JObject
            {
                ["target"] = go.name,
                ["tag"] = tag,
                ["previous_tag"] = previousTag
            };

            return ToolResponse.OkWithData(data, $"Tag of '{go.name}' set to '{tag}'.");
        }

        private ToolResponse HandleSetObjectLayer(JObject parameters)
        {
            var targetName = ToolHelpers.GetRequiredString(parameters, "target");
            var includeChildren = ToolHelpers.GetOptionalBool(parameters, "include_children", false);

            var go = ToolHelpers.FindGameObject(targetName);
            if (go == null)
            {
                return ToolResponse.Fail($"GameObject '{targetName}' not found.");
            }

            // Resolve layer from name or index
            int layerIndex;
            var layerToken = parameters?["layer"];
            if (layerToken == null)
            {
                return ToolResponse.Fail("Parameter 'layer' is required (layer name or index).");
            }

            if (layerToken.Type == JTokenType.Integer)
            {
                layerIndex = layerToken.Value<int>();
                if (layerIndex < 0 || layerIndex > 31)
                {
                    return ToolResponse.Fail($"Layer index must be between 0 and 31. Got: {layerIndex}");
                }
            }
            else
            {
                var layerName = layerToken.ToString();
                layerIndex = LayerMask.NameToLayer(layerName);
                if (layerIndex < 0)
                {
                    return ToolResponse.Fail($"Layer '{layerName}' not found.");
                }
            }

            var previousLayer = go.layer;
            Undo.RecordObject(go, "AgentCore: Set Layer");
            go.layer = layerIndex;

            int childrenAffected = 0;
            if (includeChildren)
            {
                childrenAffected = SetLayerRecursive(go.transform, layerIndex);
            }

            var data = new JObject
            {
                ["target"] = go.name,
                ["layer_index"] = layerIndex,
                ["layer_name"] = LayerMask.LayerToName(layerIndex),
                ["previous_layer"] = LayerMask.LayerToName(previousLayer),
                ["include_children"] = includeChildren,
                ["children_affected"] = childrenAffected
            };

            return ToolResponse.OkWithData(data, $"Layer of '{go.name}' set to '{LayerMask.LayerToName(layerIndex)}' (index {layerIndex}).");
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Get the TagManager SerializedObject.
        /// </summary>
        private static SerializedObject GetTagManager()
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
            if (assets == null || assets.Length == 0)
                return null;

            return new SerializedObject(assets[0]);
        }

        /// <summary>
        /// Recursively set layer on all children.
        /// </summary>
        private static int SetLayerRecursive(Transform parent, int layer)
        {
            int count = 0;
            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                Undo.RecordObject(child.gameObject, "AgentCore: Set Layer (Child)");
                child.gameObject.layer = layer;
                count++;
                count += SetLayerRecursive(child, layer);
            }
            return count;
        }

        /// <summary>
        /// Generate a unique sorting layer ID.
        /// </summary>
        private static int GenerateUniqueSortingLayerId(SerializedProperty sortingLayersProp)
        {
            var usedIds = new HashSet<int>();
            for (int i = 0; i < sortingLayersProp.arraySize - 1; i++)
            {
                var idProp = sortingLayersProp.GetArrayElementAtIndex(i).FindPropertyRelative("uniqueID");
                if (idProp != null)
                {
                    usedIds.Add(idProp.intValue);
                }
            }

            // Generate a random unique ID
            var rng = new System.Random();
            int id;
            do
            {
                id = rng.Next(1, int.MaxValue);
            } while (usedIds.Contains(id));

            return id;
        }

        #endregion
    }
}
