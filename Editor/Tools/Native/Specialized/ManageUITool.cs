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
    /// Manage Unity UI elements including Canvas, Text, Image, Button and other UI components.
    /// Uses reflection to access UnityEngine.UI types to avoid asmdef reference issues.
    /// </summary>
    [AgentTool("manage_ui",
        Description = "Manage Unity UI elements including Canvas, Text, Image, Button and other UI components",
        Category = "specialized",
        RequiresMainThread = true)]
    public class ManageUITool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""create_canvas"", ""create_element"", ""modify_element"", ""get_info"", ""list""],
                    ""description"": ""Action to perform""
                },
                ""name"": { ""type"": ""string"", ""description"": ""Element name"" },
                ""type"": {
                    ""type"": ""string"",
                    ""enum"": [""text"", ""image"", ""button"", ""panel"", ""scroll_view"", ""input_field"", ""dropdown"", ""slider"", ""toggle"", ""raw_image""],
                    ""description"": ""UI element type to create""
                },
                ""render_mode"": {
                    ""type"": ""string"",
                    ""enum"": [""screen_space_overlay"", ""screen_space_camera"", ""world_space""],
                    ""description"": ""Canvas render mode""
                },
                ""sort_order"": { ""type"": ""integer"", ""description"": ""Canvas sort order"" },
                ""parent"": { ""type"": ""string"", ""description"": ""Parent UI object name (defaults to first Canvas)"" },
                ""target"": { ""type"": ""string"", ""description"": ""Target UI element name"" },
                ""text"": { ""type"": ""string"", ""description"": ""Text content"" },
                ""color"": {
                    ""type"": ""object"",
                    ""properties"": { ""r"": {""type"":""number""}, ""g"": {""type"":""number""}, ""b"": {""type"":""number""}, ""a"": {""type"":""number""} },
                    ""description"": ""Element color""
                },
                ""font_size"": { ""type"": ""integer"", ""description"": ""Font size for text elements"" },
                ""size"": {
                    ""type"": ""object"",
                    ""properties"": { ""width"": {""type"":""number""}, ""height"": {""type"":""number""} },
                    ""description"": ""Element size""
                },
                ""position"": {
                    ""type"": ""object"",
                    ""properties"": { ""x"": {""type"":""number""}, ""y"": {""type"":""number""} },
                    ""description"": ""Element anchored position""
                },
                ""anchor"": {
                    ""type"": ""string"",
                    ""enum"": [""top_left"", ""top_center"", ""top_right"", ""middle_left"", ""center"", ""middle_right"", ""bottom_left"", ""bottom_center"", ""bottom_right"", ""stretch""],
                    ""description"": ""Anchor preset""
                },
                ""enabled"": { ""type"": ""boolean"", ""description"": ""Enable or disable element"" },
                ""image"": { ""type"": ""string"", ""description"": ""Sprite asset path for Image component"" },
                ""canvas"": { ""type"": ""string"", ""description"": ""Canvas name filter for list action"" }
            },
            ""required"": [""action""]
        }");

        public ToolMetadata Metadata => new ToolMetadata(
            name: "manage_ui",
            description: "Manage Unity UI elements including Canvas, Text, Image, Button and other UI components",
            category: "specialized",
            parametersSchema: _parametersSchema,
            requiresMainThread: true
        );

        // Cached UI types resolved via reflection
        private static Type _canvasScalerType;
        private static Type _graphicRaycasterType;
        private static Type _textType;
        private static Type _imageType;
        private static Type _buttonType;
        private static Type _rawImageType;
        private static Type _inputFieldType;
        private static Type _dropdownType;
        private static Type _sliderType;
        private static Type _toggleType;
        private static Type _scrollRectType;
        private static Type _maskType;
        private static Type _graphicType;
        private static bool _typesResolved;

        private static void ResolveUITypes()
        {
            if (_typesResolved) return;

            _canvasScalerType = ToolHelpers.ResolveComponentType("UnityEngine.UI.CanvasScaler");
            _graphicRaycasterType = ToolHelpers.ResolveComponentType("UnityEngine.UI.GraphicRaycaster");
            _textType = ToolHelpers.ResolveComponentType("UnityEngine.UI.Text");
            _imageType = ToolHelpers.ResolveComponentType("UnityEngine.UI.Image");
            _buttonType = ToolHelpers.ResolveComponentType("UnityEngine.UI.Button");
            _rawImageType = ToolHelpers.ResolveComponentType("UnityEngine.UI.RawImage");
            _inputFieldType = ToolHelpers.ResolveComponentType("UnityEngine.UI.InputField");
            _dropdownType = ToolHelpers.ResolveComponentType("UnityEngine.UI.Dropdown");
            _sliderType = ToolHelpers.ResolveComponentType("UnityEngine.UI.Slider");
            _toggleType = ToolHelpers.ResolveComponentType("UnityEngine.UI.Toggle");
            _scrollRectType = ToolHelpers.ResolveComponentType("UnityEngine.UI.ScrollRect");
            _maskType = ToolHelpers.ResolveComponentType("UnityEngine.UI.Mask");
            _graphicType = ToolHelpers.ResolveComponentType("UnityEngine.UI.Graphic");

            _typesResolved = true;
        }

        public Task<ToolResult> ExecuteAsync(JObject parameters, CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();
            ToolResponse response;

            try
            {
                ResolveUITypes();

                var action = ToolHelpers.GetRequiredString(parameters, "action").ToLowerInvariant();

                switch (action)
                {
                    case "create_canvas":
                        response = HandleCreateCanvas(parameters);
                        break;
                    case "create_element":
                        response = HandleCreateElement(parameters);
                        break;
                    case "modify_element":
                        response = HandleModifyElement(parameters);
                        break;
                    case "get_info":
                        response = HandleGetInfo(parameters);
                        break;
                    case "list":
                        response = HandleList(parameters);
                        break;
                    default:
                        response = ToolResponse.Fail(
                            $"Unknown action: '{action}'. Valid actions: create_canvas, create_element, modify_element, get_info, list");
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

        private ToolResponse HandleCreateCanvas(JObject parameters)
        {
            var name = ToolHelpers.GetOptionalString(parameters, "name", "Canvas");
            var renderModeStr = ToolHelpers.GetOptionalString(parameters, "render_mode", "screen_space_overlay");
            var sortOrder = ToolHelpers.GetOptionalInt(parameters, "sort_order", 0);

            var go = new GameObject(name);
            ToolHelpers.RegisterCreatedObject(go, "Create Canvas");

            var canvas = go.AddComponent<Canvas>();

            switch (renderModeStr.ToLowerInvariant())
            {
                case "screen_space_overlay": canvas.renderMode = RenderMode.ScreenSpaceOverlay; break;
                case "screen_space_camera": canvas.renderMode = RenderMode.ScreenSpaceCamera; break;
                case "world_space": canvas.renderMode = RenderMode.WorldSpace; break;
                default:
                    return ToolResponse.Fail($"Invalid render_mode: '{renderModeStr}'. Valid: screen_space_overlay, screen_space_camera, world_space");
            }

            canvas.sortingOrder = sortOrder;

            // Add CanvasScaler via reflection
            if (_canvasScalerType != null)
                go.AddComponent(_canvasScalerType);

            // Add GraphicRaycaster via reflection
            if (_graphicRaycasterType != null)
                go.AddComponent(_graphicRaycasterType);

            EditorUtility.SetDirty(go);

            var data = new JObject
            {
                ["name"] = go.name,
                ["instanceId"] = go.GetInstanceID(),
                ["renderMode"] = canvas.renderMode.ToString(),
                ["sortingOrder"] = canvas.sortingOrder,
                ["hasCanvasScaler"] = _canvasScalerType != null,
                ["hasGraphicRaycaster"] = _graphicRaycasterType != null
            };

            return ToolResponse.OkWithData(data, $"Canvas '{name}' created.");
        }

        private ToolResponse HandleCreateElement(JObject parameters)
        {
            var typeStr = ToolHelpers.GetRequiredString(parameters, "type").ToLowerInvariant();
            var name = ToolHelpers.GetOptionalString(parameters, "name", typeStr.Substring(0, 1).ToUpper() + typeStr.Substring(1));

            // Find parent
            Transform parent = FindParentTransform(parameters);
            if (parent == null)
                return ToolResponse.Fail("No Canvas found in scene. Create a Canvas first using create_canvas action.");

            var go = new GameObject(name);
            ToolHelpers.RegisterCreatedObject(go, "Create UI Element");
            go.transform.SetParent(parent, false);

            // Ensure RectTransform exists (setting parent to Canvas child auto-adds it)
            var rectTransform = go.GetComponent<RectTransform>();
            if (rectTransform == null)
                rectTransform = go.AddComponent<RectTransform>();

            string createdType = typeStr;

            switch (typeStr)
            {
                case "text":
                    if (_textType != null)
                    {
                        var textComp = go.AddComponent(_textType);
                        SetPropertyViaReflection(textComp, "text", ToolHelpers.GetOptionalString(parameters, "text", "New Text"));
                        SetPropertyViaReflection(textComp, "fontSize", ToolHelpers.GetOptionalInt(parameters, "font_size", 14));
                        var colorToken = parameters["color"];
                        if (colorToken != null)
                            SetPropertyViaReflection(textComp, "color", ToolHelpers.ParseColor(colorToken, Color.black));
                        else
                            SetPropertyViaReflection(textComp, "color", Color.black);
                    }
                    else
                    {
                        return ToolResponse.Fail("UnityEngine.UI.Text type not available. Ensure com.unity.ugui package is installed.");
                    }
                    break;

                case "image":
                    if (_imageType != null)
                    {
                        var imgComp = go.AddComponent(_imageType);
                        var colorToken = parameters["color"];
                        if (colorToken != null)
                            SetPropertyViaReflection(imgComp, "color", ToolHelpers.ParseColor(colorToken, Color.white));

                        var spritePath = ToolHelpers.GetOptionalString(parameters, "image");
                        if (!string.IsNullOrEmpty(spritePath))
                        {
                            spritePath = ToolHelpers.NormalizeAssetPath(spritePath);
                            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(spritePath);
                            if (sprite != null)
                                SetPropertyViaReflection(imgComp, "sprite", sprite);
                        }
                    }
                    else
                    {
                        return ToolResponse.Fail("UnityEngine.UI.Image type not available. Ensure com.unity.ugui package is installed.");
                    }
                    break;

                case "button":
                    if (_imageType != null && _buttonType != null)
                    {
                        // Button needs an Image component
                        var imgComp = go.AddComponent(_imageType);
                        var colorToken = parameters["color"];
                        if (colorToken != null)
                            SetPropertyViaReflection(imgComp, "color", ToolHelpers.ParseColor(colorToken, Color.white));

                        go.AddComponent(_buttonType);

                        // Create child text
                        var textStr = ToolHelpers.GetOptionalString(parameters, "text", "Button");
                        if (_textType != null)
                        {
                            var textGo = new GameObject("Text");
                            ToolHelpers.RegisterCreatedObject(textGo, "Create Button Text");
                            textGo.transform.SetParent(go.transform, false);
                            var textRect = textGo.GetComponent<RectTransform>();
                            if (textRect == null)
                                textRect = textGo.AddComponent<RectTransform>();
                            // Stretch text to fill button
                            textRect.anchorMin = Vector2.zero;
                            textRect.anchorMax = Vector2.one;
                            textRect.offsetMin = Vector2.zero;
                            textRect.offsetMax = Vector2.zero;

                            var textComp = textGo.AddComponent(_textType);
                            SetPropertyViaReflection(textComp, "text", textStr);
                            SetPropertyViaReflection(textComp, "fontSize", ToolHelpers.GetOptionalInt(parameters, "font_size", 14));
                            SetPropertyViaReflection(textComp, "color", Color.black);
                            SetPropertyViaReflection(textComp, "alignment", 4); // TextAnchor.MiddleCenter = 4
                        }
                    }
                    else
                    {
                        return ToolResponse.Fail("UnityEngine.UI.Button/Image types not available. Ensure com.unity.ugui package is installed.");
                    }
                    break;

                case "panel":
                    if (_imageType != null)
                    {
                        var imgComp = go.AddComponent(_imageType);
                        var colorToken = parameters["color"];
                        if (colorToken != null)
                            SetPropertyViaReflection(imgComp, "color", ToolHelpers.ParseColor(colorToken, new Color(1, 1, 1, 0.4f)));
                        else
                            SetPropertyViaReflection(imgComp, "color", new Color(1, 1, 1, 0.4f));
                    }
                    else
                    {
                        return ToolResponse.Fail("UnityEngine.UI.Image type not available.");
                    }
                    break;

                case "raw_image":
                    if (_rawImageType != null)
                    {
                        go.AddComponent(_rawImageType);
                    }
                    else
                    {
                        return ToolResponse.Fail("UnityEngine.UI.RawImage type not available.");
                    }
                    break;

                case "input_field":
                    if (_imageType != null && _inputFieldType != null && _textType != null)
                    {
                        go.AddComponent(_imageType);
                        var inputField = go.AddComponent(_inputFieldType);

                        // Create child text for display
                        var textGo = new GameObject("Text");
                        ToolHelpers.RegisterCreatedObject(textGo, "Create InputField Text");
                        textGo.transform.SetParent(go.transform, false);
                        var textRect = textGo.GetComponent<RectTransform>();
                        if (textRect == null)
                            textRect = textGo.AddComponent<RectTransform>();
                        textRect.anchorMin = Vector2.zero;
                        textRect.anchorMax = Vector2.one;
                        textRect.offsetMin = new Vector2(10, 6);
                        textRect.offsetMax = new Vector2(-10, -7);

                        var textComp = textGo.AddComponent(_textType);
                        SetPropertyViaReflection(textComp, "text", "");
                        SetPropertyViaReflection(textComp, "fontSize", 14);
                        SetPropertyViaReflection(textComp, "color", Color.black);
                        SetPropertyViaReflection(textComp, "supportRichText", false);

                        // Link text component to input field
                        SetPropertyViaReflection(inputField, "textComponent", textComp);
                    }
                    else
                    {
                        return ToolResponse.Fail("Required UI types not available for InputField.");
                    }
                    break;

                case "dropdown":
                    if (_dropdownType != null && _imageType != null)
                    {
                        go.AddComponent(_imageType);
                        go.AddComponent(_dropdownType);
                    }
                    else
                    {
                        return ToolResponse.Fail("UnityEngine.UI.Dropdown type not available.");
                    }
                    break;

                case "slider":
                    if (_sliderType != null)
                    {
                        go.AddComponent(_sliderType);
                    }
                    else
                    {
                        return ToolResponse.Fail("UnityEngine.UI.Slider type not available.");
                    }
                    break;

                case "toggle":
                    if (_toggleType != null)
                    {
                        go.AddComponent(_toggleType);
                    }
                    else
                    {
                        return ToolResponse.Fail("UnityEngine.UI.Toggle type not available.");
                    }
                    break;

                case "scroll_view":
                    if (_scrollRectType != null && _imageType != null)
                    {
                        go.AddComponent(_imageType);
                        go.AddComponent(_scrollRectType);

                        // Create viewport
                        var viewport = new GameObject("Viewport");
                        ToolHelpers.RegisterCreatedObject(viewport, "Create ScrollView Viewport");
                        viewport.transform.SetParent(go.transform, false);
                        var vpRect = viewport.AddComponent<RectTransform>();
                        vpRect.anchorMin = Vector2.zero;
                        vpRect.anchorMax = Vector2.one;
                        vpRect.offsetMin = Vector2.zero;
                        vpRect.offsetMax = Vector2.zero;
                        viewport.AddComponent(_imageType);
                        if (_maskType != null)
                            viewport.AddComponent(_maskType);

                        // Create content
                        var content = new GameObject("Content");
                        ToolHelpers.RegisterCreatedObject(content, "Create ScrollView Content");
                        content.transform.SetParent(viewport.transform, false);
                        var contentRect = content.AddComponent<RectTransform>();
                        contentRect.anchorMin = new Vector2(0, 1);
                        contentRect.anchorMax = new Vector2(1, 1);
                        contentRect.pivot = new Vector2(0.5f, 1);
                        contentRect.sizeDelta = new Vector2(0, 300);
                    }
                    else
                    {
                        return ToolResponse.Fail("Required UI types not available for ScrollView.");
                    }
                    break;

                default:
                    return ToolResponse.Fail($"Invalid UI element type: '{typeStr}'. Valid: text, image, button, panel, scroll_view, input_field, dropdown, slider, toggle, raw_image");
            }

            // Apply size
            var sizeToken = parameters["size"] as JObject;
            if (sizeToken != null)
            {
                float width = sizeToken["width"]?.Value<float>() ?? rectTransform.sizeDelta.x;
                float height = sizeToken["height"]?.Value<float>() ?? rectTransform.sizeDelta.y;
                rectTransform.sizeDelta = new Vector2(width, height);
            }
            else if (rectTransform.sizeDelta == Vector2.zero)
            {
                // Set default size for new elements
                rectTransform.sizeDelta = new Vector2(160, 30);
            }

            // Apply position
            var posToken = parameters["position"] as JObject;
            if (posToken != null)
            {
                float x = posToken["x"]?.Value<float>() ?? 0;
                float y = posToken["y"]?.Value<float>() ?? 0;
                rectTransform.anchoredPosition = new Vector2(x, y);
            }

            // Apply anchor
            var anchorStr = ToolHelpers.GetOptionalString(parameters, "anchor");
            if (!string.IsNullOrEmpty(anchorStr))
            {
                ApplyAnchorPreset(rectTransform, anchorStr);
            }

            EditorUtility.SetDirty(go);

            var data = new JObject
            {
                ["name"] = go.name,
                ["instanceId"] = go.GetInstanceID(),
                ["type"] = createdType,
                ["parent"] = parent.gameObject.name,
                ["sizeDelta"] = new JObject { ["width"] = rectTransform.sizeDelta.x, ["height"] = rectTransform.sizeDelta.y },
                ["anchoredPosition"] = new JObject { ["x"] = rectTransform.anchoredPosition.x, ["y"] = rectTransform.anchoredPosition.y }
            };

            return ToolResponse.OkWithData(data, $"UI element '{name}' ({typeStr}) created.");
        }

        private ToolResponse HandleModifyElement(JObject parameters)
        {
            var targetName = ToolHelpers.GetRequiredString(parameters, "target");
            var go = ToolHelpers.FindGameObject(targetName);
            if (go == null)
                return ToolResponse.Fail($"GameObject '{targetName}' not found.");

            var rectTransform = go.GetComponent<RectTransform>();
            if (rectTransform == null)
                return ToolResponse.Fail($"GameObject '{targetName}' is not a UI element (no RectTransform).");

            ToolHelpers.RecordUndo(go, "Modify UI Element");
            bool modified = false;

            // Modify text
            var textStr = ToolHelpers.GetOptionalString(parameters, "text");
            if (textStr != null)
            {
                var textComp = FindUIComponent(go, _textType);
                if (textComp != null)
                {
                    ToolHelpers.RecordUndo(textComp, "Modify UI Text");
                    SetPropertyViaReflection(textComp, "text", textStr);
                    modified = true;
                }
                else
                {
                    // Try child text (e.g., Button's child Text)
                    for (int i = 0; i < go.transform.childCount; i++)
                    {
                        var childTextComp = FindUIComponent(go.transform.GetChild(i).gameObject, _textType);
                        if (childTextComp != null)
                        {
                            ToolHelpers.RecordUndo(childTextComp, "Modify UI Text");
                            SetPropertyViaReflection(childTextComp, "text", textStr);
                            modified = true;
                            break;
                        }
                    }
                }
            }

            // Modify color
            var colorToken = parameters["color"];
            if (colorToken != null)
            {
                var graphicComp = FindUIComponent(go, _graphicType);
                if (graphicComp != null)
                {
                    ToolHelpers.RecordUndo(graphicComp, "Modify UI Color");
                    SetPropertyViaReflection(graphicComp, "color", ToolHelpers.ParseColor(colorToken, Color.white));
                    modified = true;
                }
            }

            // Modify font size
            if (parameters["font_size"] != null)
            {
                var textComp = FindUIComponent(go, _textType);
                if (textComp != null)
                {
                    ToolHelpers.RecordUndo(textComp, "Modify UI Font Size");
                    SetPropertyViaReflection(textComp, "fontSize", ToolHelpers.GetOptionalInt(parameters, "font_size", 14));
                    modified = true;
                }
            }

            // Modify size
            var sizeToken = parameters["size"] as JObject;
            if (sizeToken != null)
            {
                ToolHelpers.RecordUndo(rectTransform, "Modify UI Size");
                float width = sizeToken["width"]?.Value<float>() ?? rectTransform.sizeDelta.x;
                float height = sizeToken["height"]?.Value<float>() ?? rectTransform.sizeDelta.y;
                rectTransform.sizeDelta = new Vector2(width, height);
                modified = true;
            }

            // Modify position
            var posToken = parameters["position"] as JObject;
            if (posToken != null)
            {
                ToolHelpers.RecordUndo(rectTransform, "Modify UI Position");
                float x = posToken["x"]?.Value<float>() ?? rectTransform.anchoredPosition.x;
                float y = posToken["y"]?.Value<float>() ?? rectTransform.anchoredPosition.y;
                rectTransform.anchoredPosition = new Vector2(x, y);
                modified = true;
            }

            // Modify enabled
            if (parameters["enabled"] != null)
            {
                go.SetActive(ToolHelpers.GetOptionalBool(parameters, "enabled", true));
                modified = true;
            }

            // Modify image/sprite
            var imagePath = ToolHelpers.GetOptionalString(parameters, "image");
            if (!string.IsNullOrEmpty(imagePath))
            {
                var imgComp = FindUIComponent(go, _imageType);
                if (imgComp != null)
                {
                    imagePath = ToolHelpers.NormalizeAssetPath(imagePath);
                    var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(imagePath);
                    if (sprite != null)
                    {
                        ToolHelpers.RecordUndo(imgComp, "Modify UI Image");
                        SetPropertyViaReflection(imgComp, "sprite", sprite);
                        modified = true;
                    }
                    else
                    {
                        return ToolResponse.Fail($"Sprite not found at: {imagePath}");
                    }
                }
            }

            if (modified)
                EditorUtility.SetDirty(go);

            var data = SerializeUIElement(go);
            data["modified"] = modified;
            return ToolResponse.OkWithData(data, modified ? $"UI element '{targetName}' modified." : $"No modifications applied to '{targetName}'.");
        }

        private ToolResponse HandleGetInfo(JObject parameters)
        {
            var targetName = ToolHelpers.GetRequiredString(parameters, "target");
            var go = ToolHelpers.FindGameObject(targetName);
            if (go == null)
                return ToolResponse.Fail($"GameObject '{targetName}' not found.");

            var data = SerializeUIElement(go);
            return ToolResponse.OkWithData(data, $"UI element info for '{targetName}'.");
        }

        private ToolResponse HandleList(JObject parameters)
        {
            var canvasFilter = ToolHelpers.GetOptionalString(parameters, "canvas");
            var allCanvases = UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None);

            var canvasArray = new JArray();

            foreach (var canvas in allCanvases)
            {
                if (!string.IsNullOrEmpty(canvasFilter) &&
                    !string.Equals(canvas.gameObject.name, canvasFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                var canvasInfo = new JObject
                {
                    ["name"] = canvas.gameObject.name,
                    ["instanceId"] = canvas.gameObject.GetInstanceID(),
                    ["renderMode"] = canvas.renderMode.ToString(),
                    ["sortingOrder"] = canvas.sortingOrder,
                    ["enabled"] = canvas.enabled
                };

                // List children
                var children = new JArray();
                CollectUIChildren(canvas.transform, children, 0, 3); // max depth 3
                canvasInfo["children"] = children;
                canvasInfo["childCount"] = canvas.transform.childCount;

                canvasArray.Add(canvasInfo);
            }

            var data = new JObject
            {
                ["canvasCount"] = canvasArray.Count,
                ["canvases"] = canvasArray
            };

            return ToolResponse.OkWithData(data, $"Found {canvasArray.Count} Canvas(es) in scene.");
        }

        #endregion

        #region Helpers

        private Transform FindParentTransform(JObject parameters)
        {
            var parentName = ToolHelpers.GetOptionalString(parameters, "parent");
            if (!string.IsNullOrEmpty(parentName))
            {
                var parentGo = ToolHelpers.FindGameObject(parentName);
                if (parentGo != null)
                    return parentGo.transform;
            }

            // Find first Canvas in scene
            var canvas = UnityEngine.Object.FindFirstObjectByType<Canvas>();
            return canvas != null ? canvas.transform : null;
        }

        private static void ApplyAnchorPreset(RectTransform rect, string anchor)
        {
            switch (anchor.ToLowerInvariant())
            {
                case "top_left":
                    rect.anchorMin = new Vector2(0, 1);
                    rect.anchorMax = new Vector2(0, 1);
                    rect.pivot = new Vector2(0, 1);
                    break;
                case "top_center":
                    rect.anchorMin = new Vector2(0.5f, 1);
                    rect.anchorMax = new Vector2(0.5f, 1);
                    rect.pivot = new Vector2(0.5f, 1);
                    break;
                case "top_right":
                    rect.anchorMin = new Vector2(1, 1);
                    rect.anchorMax = new Vector2(1, 1);
                    rect.pivot = new Vector2(1, 1);
                    break;
                case "middle_left":
                    rect.anchorMin = new Vector2(0, 0.5f);
                    rect.anchorMax = new Vector2(0, 0.5f);
                    rect.pivot = new Vector2(0, 0.5f);
                    break;
                case "center":
                    rect.anchorMin = new Vector2(0.5f, 0.5f);
                    rect.anchorMax = new Vector2(0.5f, 0.5f);
                    rect.pivot = new Vector2(0.5f, 0.5f);
                    break;
                case "middle_right":
                    rect.anchorMin = new Vector2(1, 0.5f);
                    rect.anchorMax = new Vector2(1, 0.5f);
                    rect.pivot = new Vector2(1, 0.5f);
                    break;
                case "bottom_left":
                    rect.anchorMin = new Vector2(0, 0);
                    rect.anchorMax = new Vector2(0, 0);
                    rect.pivot = new Vector2(0, 0);
                    break;
                case "bottom_center":
                    rect.anchorMin = new Vector2(0.5f, 0);
                    rect.anchorMax = new Vector2(0.5f, 0);
                    rect.pivot = new Vector2(0.5f, 0);
                    break;
                case "bottom_right":
                    rect.anchorMin = new Vector2(1, 0);
                    rect.anchorMax = new Vector2(1, 0);
                    rect.pivot = new Vector2(1, 0);
                    break;
                case "stretch":
                    rect.anchorMin = Vector2.zero;
                    rect.anchorMax = Vector2.one;
                    rect.offsetMin = Vector2.zero;
                    rect.offsetMax = Vector2.zero;
                    rect.pivot = new Vector2(0.5f, 0.5f);
                    break;
            }
        }

        private static Component FindUIComponent(GameObject go, Type type)
        {
            if (type == null || go == null) return null;
            return go.GetComponent(type);
        }

        private static void SetPropertyViaReflection(object target, string propertyName, object value)
        {
            if (target == null) return;
            var type = target.GetType();

            var prop = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (prop != null && prop.CanWrite)
            {
                // Handle enum conversion for TextAnchor (alignment)
                if (prop.PropertyType.IsEnum && value is int intVal)
                {
                    value = Enum.ToObject(prop.PropertyType, intVal);
                }
                prop.SetValue(target, value);
                return;
            }

            var field = type.GetField(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (field != null)
            {
                if (field.FieldType.IsEnum && value is int intVal2)
                {
                    value = Enum.ToObject(field.FieldType, intVal2);
                }
                field.SetValue(target, value);
            }
        }

        private static object GetPropertyViaReflection(object target, string propertyName)
        {
            if (target == null) return null;
            var type = target.GetType();

            var prop = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (prop != null && prop.CanRead)
                return prop.GetValue(target);

            var field = type.GetField(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (field != null)
                return field.GetValue(target);

            return null;
        }

        private JObject SerializeUIElement(GameObject go)
        {
            var data = new JObject
            {
                ["name"] = go.name,
                ["instanceId"] = go.GetInstanceID(),
                ["activeSelf"] = go.activeSelf,
                ["activeInHierarchy"] = go.activeInHierarchy
            };

            var rectTransform = go.GetComponent<RectTransform>();
            if (rectTransform != null)
            {
                data["rectTransform"] = new JObject
                {
                    ["anchoredPosition"] = new JObject { ["x"] = rectTransform.anchoredPosition.x, ["y"] = rectTransform.anchoredPosition.y },
                    ["sizeDelta"] = new JObject { ["width"] = rectTransform.sizeDelta.x, ["height"] = rectTransform.sizeDelta.y },
                    ["anchorMin"] = new JObject { ["x"] = rectTransform.anchorMin.x, ["y"] = rectTransform.anchorMin.y },
                    ["anchorMax"] = new JObject { ["x"] = rectTransform.anchorMax.x, ["y"] = rectTransform.anchorMax.y },
                    ["pivot"] = new JObject { ["x"] = rectTransform.pivot.x, ["y"] = rectTransform.pivot.y }
                };
            }

            // List components
            var components = new JArray();
            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp == null) continue;
                var compInfo = new JObject
                {
                    ["type"] = comp.GetType().Name,
                    ["fullType"] = comp.GetType().FullName
                };

                // Extract text content if available
                if (_textType != null && _textType.IsInstanceOfType(comp))
                {
                    compInfo["text"] = GetPropertyViaReflection(comp, "text")?.ToString();
                    compInfo["fontSize"] = (int?)GetPropertyViaReflection(comp, "fontSize");
                }

                // Extract image sprite if available
                if (_imageType != null && _imageType.IsInstanceOfType(comp))
                {
                    var sprite = GetPropertyViaReflection(comp, "sprite") as Sprite;
                    compInfo["sprite"] = sprite != null ? sprite.name : null;
                }

                components.Add(compInfo);
            }
            data["components"] = components;

            // Children count
            data["childCount"] = go.transform.childCount;

            return data;
        }

        private static void CollectUIChildren(Transform parent, JArray array, int depth, int maxDepth)
        {
            if (depth >= maxDepth) return;

            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                var childInfo = new JObject
                {
                    ["name"] = child.gameObject.name,
                    ["instanceId"] = child.gameObject.GetInstanceID(),
                    ["active"] = child.gameObject.activeSelf
                };

                // List component type names
                var compNames = new JArray();
                foreach (var comp in child.GetComponents<Component>())
                {
                    if (comp == null) continue;
                    if (comp is Transform) continue;
                    compNames.Add(comp.GetType().Name);
                }
                childInfo["components"] = compNames;

                if (child.childCount > 0 && depth + 1 < maxDepth)
                {
                    var subChildren = new JArray();
                    CollectUIChildren(child, subChildren, depth + 1, maxDepth);
                    childInfo["children"] = subChildren;
                }

                array.Add(childInfo);
            }
        }

        #endregion
    }
}
