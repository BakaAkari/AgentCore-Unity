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
        Description = "Unity uGUI (Canvas-based UI) — create and configure UI elements for runtime user interfaces. " +
                      "Actions: create_canvas (Screen Space/World Space with scaler), create_text (TextMeshPro or legacy Text), " +
                      "create_image (with sprite/color), create_button (with label and optional onClick target), " +
                      "create_panel, create_scroll_view, create_dropdown, create_input_field, create_slider, create_toggle, " +
                      "modify_element (change any RectTransform/UI property), get_info, list_ui_elements. " +
                      "Uses reflection to access UnityEngine.UI types (avoids asmdef coupling). " +
                      "USE FOR: building game UI (menus, HUD, dialogs), setting up Canvas hierarchy, " +
                      "configuring anchors/pivot/size for responsive layout, creating interactive elements. " +
                      "NOT FOR: Editor UI / custom inspectors (use UI Toolkit/manage_ui_toolkit), " +
                      "world-space text that's not UI (use TextMesh component via manage_component). " +
                      "ACTIVATE WHEN: user mentions 'UI', 'Canvas', 'Button', 'Text UI', 'HUD', 'menu', 'uGUI', 'ScrollView', 'panel'.",
        Category = "Specialized",
        Visibility = ToolVisibility.OnDemand,
        RequiresMainThread = true)]
    public class ManageUITool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""create_canvas"", ""create_element"", ""modify_element"", ""get_info"", ""list"", ""set_layout"", ""add_layout_group"", ""configure_canvas"", ""add_ui_component"",
                               ""align_elements"", ""distribute_elements"", ""delete_element"", ""duplicate_element"",
                               ""set_text"", ""set_image"", ""set_interactable"", ""reorder_element"", ""find_element""],
                    ""description"": ""Action to perform""
                },
                ""name"": { ""type"": ""string"", ""description"": ""Element name or target GameObject name"" },
                ""type"": {
                    ""type"": ""string"",
                    ""enum"": [""text"", ""image"", ""button"", ""panel"", ""scroll_view"", ""input_field"", ""dropdown"", ""slider"", ""toggle"", ""raw_image""],
                    ""description"": ""UI element type to create""
                },
                ""render_mode"": {
                    ""type"": ""string"",
                    ""enum"": [""screen_space_overlay"", ""screen_space_camera"", ""world_space"", ""overlay"", ""camera"", ""world""],
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
                ""preset"": {
                    ""type"": ""string"",
                    ""enum"": [""stretch"", ""top-left"", ""top-center"", ""top-right"", ""middle-left"", ""center"", ""middle-right"", ""bottom-left"", ""bottom-center"", ""bottom-right"", ""stretch-horizontal"", ""stretch-vertical""],
                    ""description"": ""RectTransform anchor preset for set_layout action""
                },
                ""layout_type"": {
                    ""type"": ""string"",
                    ""enum"": [""horizontal"", ""vertical"", ""grid""],
                    ""description"": ""Layout group type for add_layout_group action""
                },
                ""spacing"": { ""type"": ""number"", ""description"": ""Spacing for layout groups"" },
                ""padding"": {
                    ""type"": ""object"",
                    ""properties"": { ""left"": {""type"":""integer""}, ""right"": {""type"":""integer""}, ""top"": {""type"":""integer""}, ""bottom"": {""type"":""integer""} },
                    ""description"": ""Padding for layout groups""
                },
                ""cell_size"": {
                    ""type"": ""object"",
                    ""properties"": { ""x"": {""type"":""number""}, ""y"": {""type"":""number""} },
                    ""description"": ""Cell size for GridLayoutGroup""
                },
                ""camera_name"": { ""type"": ""string"", ""description"": ""Camera name for screen_space_camera render mode"" },
                ""component_type"": {
                    ""type"": ""string"",
                    ""enum"": [""button"", ""toggle"", ""slider"", ""dropdown"", ""input_field"", ""scroll_view"", ""image"", ""text""],
                    ""description"": ""UI component type for add_ui_component action""
                },
                ""enabled"": { ""type"": ""boolean"", ""description"": ""Enable or disable element"" },
                ""image"": { ""type"": ""string"", ""description"": ""Sprite asset path for Image component"" },
                ""canvas"": { ""type"": ""string"", ""description"": ""Canvas name filter for list action"" },
                ""targets"": {
                    ""type"": ""array"",
                    ""items"": { ""type"": ""string"" },
                    ""description"": ""List of UI element names for align/distribute actions""
                },
                ""align_axis"": {
                    ""type"": ""string"",
                    ""enum"": [""left"", ""center_h"", ""right"", ""top"", ""center_v"", ""bottom""],
                    ""description"": ""Alignment axis for align_elements action""
                },
                ""distribute_axis"": {
                    ""type"": ""string"",
                    ""enum"": [""horizontal"", ""vertical""],
                    ""description"": ""Distribution axis for distribute_elements action""
                },
                ""new_name"": { ""type"": ""string"", ""description"": ""New name for duplicated element"" },
                ""sibling_index"": { ""type"": ""integer"", ""description"": ""Sibling index for reorder_element (0 = first)"" },
                ""move_to_first"": { ""type"": ""boolean"", ""description"": ""Move element to first sibling position"" },
                ""move_to_last"": { ""type"": ""boolean"", ""description"": ""Move element to last sibling position"" },
                ""search"": { ""type"": ""string"", ""description"": ""Search string for find_element action"" },
                ""interactable"": { ""type"": ""boolean"", ""description"": ""Set interactable state on Button/Toggle/Slider/Dropdown"" }
            },
            ""required"": [""action""]
        }");

        public ToolMetadata Metadata => new ToolMetadata(
            name: "manage_ui",
            description: "Manage Unity legacy UI (uGUI): create Canvas/elements, modify properties, align/distribute elements, set anchor presets, manage layout groups, configure interactability. For new UI Toolkit (UIElements), use manage_ui_toolkit instead.",
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
        private static Type _horizontalLayoutGroupType;
        private static Type _verticalLayoutGroupType;
        private static Type _gridLayoutGroupType;
        private static Type _layoutGroupType;
        private static Type _contentSizeFitterType;
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

            _horizontalLayoutGroupType = ToolHelpers.ResolveComponentType("UnityEngine.UI.HorizontalLayoutGroup");
            _verticalLayoutGroupType = ToolHelpers.ResolveComponentType("UnityEngine.UI.VerticalLayoutGroup");
            _gridLayoutGroupType = ToolHelpers.ResolveComponentType("UnityEngine.UI.GridLayoutGroup");
            _layoutGroupType = ToolHelpers.ResolveComponentType("UnityEngine.UI.LayoutGroup");
            _contentSizeFitterType = ToolHelpers.ResolveComponentType("UnityEngine.UI.ContentSizeFitter");

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
                    case "set_layout":
                        response = HandleSetLayout(parameters);
                        break;
                    case "add_layout_group":
                        response = HandleAddLayoutGroup(parameters);
                        break;
                    case "configure_canvas":
                        response = HandleConfigureCanvas(parameters);
                        break;
                    case "add_ui_component":
                        response = HandleAddUIComponent(parameters);
                        break;
                    case "align_elements":
                        response = HandleAlignElements(parameters);
                        break;
                    case "distribute_elements":
                        response = HandleDistributeElements(parameters);
                        break;
                    case "delete_element":
                        response = HandleDeleteElement(parameters);
                        break;
                    case "duplicate_element":
                        response = HandleDuplicateElement(parameters);
                        break;
                    case "set_text":
                        response = HandleSetText(parameters);
                        break;
                    case "set_image":
                        response = HandleSetImage(parameters);
                        break;
                    case "set_interactable":
                        response = HandleSetInteractable(parameters);
                        break;
                    case "reorder_element":
                        response = HandleReorderElement(parameters);
                        break;
                    case "find_element":
                        response = HandleFindElement(parameters);
                        break;
                    default:
                        response = ToolResponse.Fail(
                            $"Unknown action: '{action}'. Valid actions: create_canvas, create_element, modify_element, get_info, list, " +
                            "set_layout, add_layout_group, configure_canvas, add_ui_component, " +
                            "align_elements, distribute_elements, delete_element, duplicate_element, " +
                            "set_text, set_image, set_interactable, reorder_element, find_element");
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

        /// <summary>
        /// Set RectTransform anchor preset (layout) on a UI element.
        /// Supports presets: stretch, top-left, top-center, top-right, middle-left, center,
        /// middle-right, bottom-left, bottom-center, bottom-right, stretch-horizontal, stretch-vertical.
        /// </summary>
        private ToolResponse HandleSetLayout(JObject parameters)
        {
            var targetName = ToolHelpers.GetRequiredString(parameters, "name");
            var go = ToolHelpers.FindGameObject(targetName);
            if (go == null)
                return ToolResponse.Fail($"GameObject '{targetName}' not found.");

            var rectTransform = go.GetComponent<RectTransform>();
            if (rectTransform == null)
                return ToolResponse.Fail($"GameObject '{targetName}' has no RectTransform. It must be a UI element.");

            var preset = ToolHelpers.GetRequiredString(parameters, "preset").ToLowerInvariant();

            ToolHelpers.RecordUndo(rectTransform, "Set UI Layout");

            switch (preset)
            {
                case "stretch":
                    rectTransform.anchorMin = Vector2.zero;
                    rectTransform.anchorMax = Vector2.one;
                    rectTransform.offsetMin = Vector2.zero;
                    rectTransform.offsetMax = Vector2.zero;
                    rectTransform.pivot = new Vector2(0.5f, 0.5f);
                    break;
                case "top-left":
                    rectTransform.anchorMin = new Vector2(0, 1);
                    rectTransform.anchorMax = new Vector2(0, 1);
                    rectTransform.pivot = new Vector2(0, 1);
                    break;
                case "top-center":
                    rectTransform.anchorMin = new Vector2(0.5f, 1);
                    rectTransform.anchorMax = new Vector2(0.5f, 1);
                    rectTransform.pivot = new Vector2(0.5f, 1);
                    break;
                case "top-right":
                    rectTransform.anchorMin = new Vector2(1, 1);
                    rectTransform.anchorMax = new Vector2(1, 1);
                    rectTransform.pivot = new Vector2(1, 1);
                    break;
                case "middle-left":
                    rectTransform.anchorMin = new Vector2(0, 0.5f);
                    rectTransform.anchorMax = new Vector2(0, 0.5f);
                    rectTransform.pivot = new Vector2(0, 0.5f);
                    break;
                case "center":
                    rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
                    rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
                    rectTransform.pivot = new Vector2(0.5f, 0.5f);
                    break;
                case "middle-right":
                    rectTransform.anchorMin = new Vector2(1, 0.5f);
                    rectTransform.anchorMax = new Vector2(1, 0.5f);
                    rectTransform.pivot = new Vector2(1, 0.5f);
                    break;
                case "bottom-left":
                    rectTransform.anchorMin = new Vector2(0, 0);
                    rectTransform.anchorMax = new Vector2(0, 0);
                    rectTransform.pivot = new Vector2(0, 0);
                    break;
                case "bottom-center":
                    rectTransform.anchorMin = new Vector2(0.5f, 0);
                    rectTransform.anchorMax = new Vector2(0.5f, 0);
                    rectTransform.pivot = new Vector2(0.5f, 0);
                    break;
                case "bottom-right":
                    rectTransform.anchorMin = new Vector2(1, 0);
                    rectTransform.anchorMax = new Vector2(1, 0);
                    rectTransform.pivot = new Vector2(1, 0);
                    break;
                case "stretch-horizontal":
                    rectTransform.anchorMin = new Vector2(0, 0.5f);
                    rectTransform.anchorMax = new Vector2(1, 0.5f);
                    rectTransform.offsetMin = new Vector2(0, rectTransform.offsetMin.y);
                    rectTransform.offsetMax = new Vector2(0, rectTransform.offsetMax.y);
                    rectTransform.pivot = new Vector2(0.5f, 0.5f);
                    break;
                case "stretch-vertical":
                    rectTransform.anchorMin = new Vector2(0.5f, 0);
                    rectTransform.anchorMax = new Vector2(0.5f, 1);
                    rectTransform.offsetMin = new Vector2(rectTransform.offsetMin.x, 0);
                    rectTransform.offsetMax = new Vector2(rectTransform.offsetMax.x, 0);
                    rectTransform.pivot = new Vector2(0.5f, 0.5f);
                    break;
                default:
                    return ToolResponse.Fail($"Unknown preset: '{preset}'. Valid: stretch, top-left, top-center, top-right, middle-left, center, middle-right, bottom-left, bottom-center, bottom-right, stretch-horizontal, stretch-vertical");
            }

            EditorUtility.SetDirty(go);

            var data = new JObject
            {
                ["name"] = go.name,
                ["preset"] = preset,
                ["anchorMin"] = new JObject { ["x"] = rectTransform.anchorMin.x, ["y"] = rectTransform.anchorMin.y },
                ["anchorMax"] = new JObject { ["x"] = rectTransform.anchorMax.x, ["y"] = rectTransform.anchorMax.y },
                ["pivot"] = new JObject { ["x"] = rectTransform.pivot.x, ["y"] = rectTransform.pivot.y }
            };

            return ToolResponse.OkWithData(data, $"Layout preset '{preset}' applied to '{targetName}'.");
        }

        /// <summary>
        /// Add a layout group component (HorizontalLayoutGroup, VerticalLayoutGroup, or GridLayoutGroup) to a UI element.
        /// </summary>
        private ToolResponse HandleAddLayoutGroup(JObject parameters)
        {
            var targetName = ToolHelpers.GetRequiredString(parameters, "name");
            var go = ToolHelpers.FindGameObject(targetName);
            if (go == null)
                return ToolResponse.Fail($"GameObject '{targetName}' not found.");

            if (go.GetComponent<RectTransform>() == null)
                return ToolResponse.Fail($"GameObject '{targetName}' has no RectTransform. It must be a UI element.");

            var layoutTypeStr = ToolHelpers.GetRequiredString(parameters, "layout_type").ToLowerInvariant();
            var spacing = ToolHelpers.GetOptionalFloat(parameters, "spacing", 0f);

            ToolHelpers.RecordUndo(go, "Add Layout Group");

            Type layoutType;
            string layoutTypeName;

            switch (layoutTypeStr)
            {
                case "horizontal":
                    layoutType = _horizontalLayoutGroupType;
                    layoutTypeName = "HorizontalLayoutGroup";
                    break;
                case "vertical":
                    layoutType = _verticalLayoutGroupType;
                    layoutTypeName = "VerticalLayoutGroup";
                    break;
                case "grid":
                    layoutType = _gridLayoutGroupType;
                    layoutTypeName = "GridLayoutGroup";
                    break;
                default:
                    return ToolResponse.Fail($"Invalid layout type: '{layoutTypeStr}'. Valid: horizontal, vertical, grid");
            }

            if (layoutType == null)
                return ToolResponse.Fail($"{layoutTypeName} type not available. Ensure com.unity.ugui package is installed.");

            // Remove existing layout group if any
            if (_layoutGroupType != null)
            {
                var existing = go.GetComponent(_layoutGroupType);
                if (existing != null)
                    UnityEngine.Object.DestroyImmediate(existing);
            }

            var comp = Undo.AddComponent(go, layoutType);

            // Set spacing
            SetPropertyViaReflection(comp, "spacing", spacing);

            // Set padding
            var paddingToken = parameters["padding"] as JObject;
            if (paddingToken != null)
            {
                var paddingType = comp.GetType().GetProperty("padding")?.PropertyType;
                if (paddingType != null)
                {
                    var paddingObj = comp.GetType().GetProperty("padding")?.GetValue(comp);
                    if (paddingObj != null)
                    {
                        var left = paddingToken["left"]?.Value<int>() ?? 0;
                        var right = paddingToken["right"]?.Value<int>() ?? 0;
                        var top = paddingToken["top"]?.Value<int>() ?? 0;
                        var bottom = paddingToken["bottom"]?.Value<int>() ?? 0;

                        paddingType.GetProperty("left")?.SetValue(paddingObj, left);
                        paddingType.GetProperty("right")?.SetValue(paddingObj, right);
                        paddingType.GetProperty("top")?.SetValue(paddingObj, top);
                        paddingType.GetProperty("bottom")?.SetValue(paddingObj, bottom);

                        comp.GetType().GetProperty("padding")?.SetValue(comp, paddingObj);
                    }
                }
            }

            // Set cell size for grid layout
            if (layoutTypeStr == "grid")
            {
                var cellSizeToken = parameters["cell_size"] as JObject;
                if (cellSizeToken != null)
                {
                    float cx = cellSizeToken["x"]?.Value<float>() ?? 100f;
                    float cy = cellSizeToken["y"]?.Value<float>() ?? 100f;
                    SetPropertyViaReflection(comp, "cellSize", new Vector2(cx, cy));
                }
            }

            EditorUtility.SetDirty(go);

            var data = new JObject
            {
                ["name"] = go.name,
                ["layoutType"] = layoutTypeName,
                ["spacing"] = spacing
            };

            return ToolResponse.OkWithData(data, $"{layoutTypeName} added to '{targetName}'.");
        }

        /// <summary>
        /// Configure an existing Canvas component's settings (render mode, sort order, camera).
        /// </summary>
        private ToolResponse HandleConfigureCanvas(JObject parameters)
        {
            var targetName = ToolHelpers.GetRequiredString(parameters, "name");
            var go = ToolHelpers.FindGameObject(targetName);
            if (go == null)
                return ToolResponse.Fail($"GameObject '{targetName}' not found.");

            var canvas = go.GetComponent<Canvas>();
            if (canvas == null)
                return ToolResponse.Fail($"GameObject '{targetName}' has no Canvas component.");

            ToolHelpers.RecordUndo(canvas, "Configure Canvas");
            bool modified = false;

            var renderModeStr = ToolHelpers.GetOptionalString(parameters, "render_mode");
            if (!string.IsNullOrEmpty(renderModeStr))
            {
                switch (renderModeStr.ToLowerInvariant())
                {
                    case "screen_space_overlay":
                    case "overlay":
                        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                        modified = true;
                        break;
                    case "screen_space_camera":
                    case "camera":
                        canvas.renderMode = RenderMode.ScreenSpaceCamera;
                        modified = true;
                        break;
                    case "world_space":
                    case "world":
                        canvas.renderMode = RenderMode.WorldSpace;
                        modified = true;
                        break;
                    default:
                        return ToolResponse.Fail($"Invalid render_mode: '{renderModeStr}'. Valid: overlay, camera, world (or screen_space_overlay, screen_space_camera, world_space)");
                }
            }

            if (parameters["sort_order"] != null)
            {
                canvas.sortingOrder = ToolHelpers.GetOptionalInt(parameters, "sort_order", 0);
                modified = true;
            }

            var cameraName = ToolHelpers.GetOptionalString(parameters, "camera_name");
            if (!string.IsNullOrEmpty(cameraName))
            {
                var camGo = ToolHelpers.FindGameObject(cameraName);
                if (camGo == null)
                    return ToolResponse.Fail($"Camera GameObject '{cameraName}' not found.");
                var cam = camGo.GetComponent<Camera>();
                if (cam == null)
                    return ToolResponse.Fail($"GameObject '{cameraName}' has no Camera component.");
                canvas.worldCamera = cam;
                modified = true;
            }

            if (!modified)
                return ToolResponse.Fail("No configuration parameters provided. Use render_mode, sort_order, or camera_name.");

            EditorUtility.SetDirty(go);

            var data = new JObject
            {
                ["name"] = go.name,
                ["renderMode"] = canvas.renderMode.ToString(),
                ["sortingOrder"] = canvas.sortingOrder,
                ["worldCamera"] = canvas.worldCamera != null ? canvas.worldCamera.gameObject.name : null
            };

            return ToolResponse.OkWithData(data, $"Canvas '{targetName}' configured.");
        }

        /// <summary>
        /// Add a specific UI component (Button, Toggle, Slider, Dropdown, InputField, ScrollView, Image, Text)
        /// to an existing GameObject.
        /// </summary>
        private ToolResponse HandleAddUIComponent(JObject parameters)
        {
            var targetName = ToolHelpers.GetRequiredString(parameters, "name");
            var go = ToolHelpers.FindGameObject(targetName);
            if (go == null)
                return ToolResponse.Fail($"GameObject '{targetName}' not found.");

            if (go.GetComponent<RectTransform>() == null)
                return ToolResponse.Fail($"GameObject '{targetName}' has no RectTransform. It must be a UI element.");

            var componentTypeStr = ToolHelpers.GetRequiredString(parameters, "component_type").ToLowerInvariant();

            ToolHelpers.RecordUndo(go, "Add UI Component");

            string addedTypeName;

            switch (componentTypeStr)
            {
                case "button":
                    if (_buttonType == null)
                        return ToolResponse.Fail("UnityEngine.UI.Button type not available.");
                    if (FindUIComponent(go, _buttonType) != null)
                        return ToolResponse.Fail($"'{targetName}' already has a Button component.");
                    // Button requires Image
                    if (_imageType != null && FindUIComponent(go, _imageType) == null)
                        go.AddComponent(_imageType);
                    go.AddComponent(_buttonType);
                    addedTypeName = "Button";
                    break;

                case "toggle":
                    if (_toggleType == null)
                        return ToolResponse.Fail("UnityEngine.UI.Toggle type not available.");
                    if (FindUIComponent(go, _toggleType) != null)
                        return ToolResponse.Fail($"'{targetName}' already has a Toggle component.");
                    go.AddComponent(_toggleType);
                    addedTypeName = "Toggle";
                    break;

                case "slider":
                    if (_sliderType == null)
                        return ToolResponse.Fail("UnityEngine.UI.Slider type not available.");
                    if (FindUIComponent(go, _sliderType) != null)
                        return ToolResponse.Fail($"'{targetName}' already has a Slider component.");
                    go.AddComponent(_sliderType);
                    addedTypeName = "Slider";
                    break;

                case "dropdown":
                    if (_dropdownType == null || _imageType == null)
                        return ToolResponse.Fail("UnityEngine.UI.Dropdown type not available.");
                    if (FindUIComponent(go, _dropdownType) != null)
                        return ToolResponse.Fail($"'{targetName}' already has a Dropdown component.");
                    if (FindUIComponent(go, _imageType) == null)
                        go.AddComponent(_imageType);
                    go.AddComponent(_dropdownType);
                    addedTypeName = "Dropdown";
                    break;

                case "input_field":
                    if (_inputFieldType == null)
                        return ToolResponse.Fail("UnityEngine.UI.InputField type not available.");
                    if (FindUIComponent(go, _inputFieldType) != null)
                        return ToolResponse.Fail($"'{targetName}' already has an InputField component.");
                    if (_imageType != null && FindUIComponent(go, _imageType) == null)
                        go.AddComponent(_imageType);
                    go.AddComponent(_inputFieldType);
                    addedTypeName = "InputField";
                    break;

                case "scroll_view":
                    if (_scrollRectType == null)
                        return ToolResponse.Fail("UnityEngine.UI.ScrollRect type not available.");
                    if (FindUIComponent(go, _scrollRectType) != null)
                        return ToolResponse.Fail($"'{targetName}' already has a ScrollRect component.");
                    if (_imageType != null && FindUIComponent(go, _imageType) == null)
                        go.AddComponent(_imageType);
                    go.AddComponent(_scrollRectType);
                    addedTypeName = "ScrollRect";
                    break;

                case "image":
                    if (_imageType == null)
                        return ToolResponse.Fail("UnityEngine.UI.Image type not available.");
                    if (FindUIComponent(go, _imageType) != null)
                        return ToolResponse.Fail($"'{targetName}' already has an Image component.");
                    var imgComp = go.AddComponent(_imageType);
                    var colorToken = parameters["color"];
                    if (colorToken != null)
                        SetPropertyViaReflection(imgComp, "color", ToolHelpers.ParseColor(colorToken, Color.white));
                    addedTypeName = "Image";
                    break;

                case "text":
                    if (_textType == null)
                        return ToolResponse.Fail("UnityEngine.UI.Text type not available.");
                    if (FindUIComponent(go, _textType) != null)
                        return ToolResponse.Fail($"'{targetName}' already has a Text component.");
                    var textComp = go.AddComponent(_textType);
                    var textStr = ToolHelpers.GetOptionalString(parameters, "text", "New Text");
                    SetPropertyViaReflection(textComp, "text", textStr);
                    if (parameters["font_size"] != null)
                        SetPropertyViaReflection(textComp, "fontSize", ToolHelpers.GetOptionalInt(parameters, "font_size", 14));
                    addedTypeName = "Text";
                    break;

                default:
                    return ToolResponse.Fail($"Invalid component_type: '{componentTypeStr}'. Valid: button, toggle, slider, dropdown, input_field, scroll_view, image, text");
            }

            EditorUtility.SetDirty(go);

            var data = new JObject
            {
                ["name"] = go.name,
                ["addedComponent"] = addedTypeName,
                ["instanceId"] = go.GetInstanceID()
            };

            return ToolResponse.OkWithData(data, $"{addedTypeName} component added to '{targetName}'.");
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

        #region New Action Handlers

        /// <summary>
        /// Align multiple UI elements along a common axis.
        /// </summary>
        private ToolResponse HandleAlignElements(JObject parameters)
        {
            var targets = parameters["targets"]?.ToObject<List<string>>();
            var alignAxis = ToolHelpers.GetRequiredString(parameters, "align_axis").ToLowerInvariant();

            if (targets == null || targets.Count < 2)
                return ToolResponse.Fail("'targets' must contain at least 2 element names.");

            var rects = new List<(string name, RectTransform rt)>();
            foreach (var name in targets)
            {
                var go = ToolHelpers.FindGameObject(name);
                if (go == null)
                    return ToolResponse.Fail($"UI element '{name}' not found.");
                var rt = go.GetComponent<RectTransform>();
                if (rt == null)
                    return ToolResponse.Fail($"'{name}' does not have a RectTransform component.");
                rects.Add((name, rt));
            }

            // Record undo for all
            foreach (var (_, rt) in rects)
                ToolHelpers.RecordUndo(rt, "Align UI Elements");

            // Compute reference value from first element
            var refRect = rects[0].rt;
            var refPos = refRect.anchoredPosition;
            var refSize = refRect.sizeDelta;

            var aligned = new List<string>();

            foreach (var (name, rt) in rects)
            {
                var pos = rt.anchoredPosition;
                var size = rt.sizeDelta;

                switch (alignAxis)
                {
                    case "left":
                        // Align left edges: set x so left edge matches reference left edge
                        pos.x = refPos.x - refRect.pivot.x * refSize.x + rt.pivot.x * size.x;
                        break;
                    case "center_h":
                        // Align horizontal centers
                        pos.x = refPos.x + (0.5f - refRect.pivot.x) * refSize.x + (rt.pivot.x - 0.5f) * size.x;
                        break;
                    case "right":
                        // Align right edges
                        pos.x = refPos.x + (1f - refRect.pivot.x) * refSize.x - (1f - rt.pivot.x) * size.x;
                        break;
                    case "top":
                        // Align top edges
                        pos.y = refPos.y + (1f - refRect.pivot.y) * refSize.y - (1f - rt.pivot.y) * size.y;
                        break;
                    case "center_v":
                        // Align vertical centers
                        pos.y = refPos.y + (0.5f - refRect.pivot.y) * refSize.y + (rt.pivot.y - 0.5f) * size.y;
                        break;
                    case "bottom":
                        // Align bottom edges
                        pos.y = refPos.y - refRect.pivot.y * refSize.y + rt.pivot.y * size.y;
                        break;
                    default:
                        return ToolResponse.Fail($"Unknown align_axis: '{alignAxis}'. Valid: left, center_h, right, top, center_v, bottom");
                }

                rt.anchoredPosition = pos;
                EditorUtility.SetDirty(rt);
                aligned.Add(name);
            }

            return ToolResponse.OkWithData(new { aligned_count = aligned.Count, axis = alignAxis, reference = rects[0].name },
                $"Aligned {aligned.Count} elements ({alignAxis}) relative to '{rects[0].name}'");
        }

        /// <summary>
        /// Distribute UI elements evenly along an axis.
        /// </summary>
        private ToolResponse HandleDistributeElements(JObject parameters)
        {
            var targets = parameters["targets"]?.ToObject<List<string>>();
            var axis = ToolHelpers.GetOptionalString(parameters, "distribute_axis", "horizontal").ToLowerInvariant();

            if (targets == null || targets.Count < 3)
                return ToolResponse.Fail("'targets' must contain at least 3 element names for distribution.");

            var rects = new List<(string name, RectTransform rt)>();
            foreach (var name in targets)
            {
                var go = ToolHelpers.FindGameObject(name);
                if (go == null)
                    return ToolResponse.Fail($"UI element '{name}' not found.");
                var rt = go.GetComponent<RectTransform>();
                if (rt == null)
                    return ToolResponse.Fail($"'{name}' does not have a RectTransform.");
                rects.Add((name, rt));
            }

            foreach (var (_, rt) in rects)
                ToolHelpers.RecordUndo(rt, "Distribute UI Elements");

            if (axis == "horizontal")
            {
                // Sort by X position
                rects.Sort((a, b) => a.rt.anchoredPosition.x.CompareTo(b.rt.anchoredPosition.x));
                var first = rects[0].rt;
                var last = rects[rects.Count - 1].rt;
                var totalSpan = last.anchoredPosition.x - first.anchoredPosition.x;
                var step = totalSpan / (rects.Count - 1);

                for (int i = 1; i < rects.Count - 1; i++)
                {
                    var pos = rects[i].rt.anchoredPosition;
                    pos.x = first.anchoredPosition.x + step * i;
                    rects[i].rt.anchoredPosition = pos;
                    EditorUtility.SetDirty(rects[i].rt);
                }
            }
            else if (axis == "vertical")
            {
                // Sort by Y position
                rects.Sort((a, b) => a.rt.anchoredPosition.y.CompareTo(b.rt.anchoredPosition.y));
                var first = rects[0].rt;
                var last = rects[rects.Count - 1].rt;
                var totalSpan = last.anchoredPosition.y - first.anchoredPosition.y;
                var step = totalSpan / (rects.Count - 1);

                for (int i = 1; i < rects.Count - 1; i++)
                {
                    var pos = rects[i].rt.anchoredPosition;
                    pos.y = first.anchoredPosition.y + step * i;
                    rects[i].rt.anchoredPosition = pos;
                    EditorUtility.SetDirty(rects[i].rt);
                }
            }
            else
            {
                return ToolResponse.Fail($"Unknown distribute_axis: '{axis}'. Valid: horizontal, vertical");
            }

            return ToolResponse.OkWithData(new { count = rects.Count, axis },
                $"Distributed {rects.Count} elements evenly along {axis} axis");
        }

        /// <summary>
        /// Delete a UI element from the scene.
        /// </summary>
        private ToolResponse HandleDeleteElement(JObject parameters)
        {
            var targetName = ToolHelpers.GetRequiredString(parameters, "target");

            var go = ToolHelpers.FindGameObject(targetName);
            if (go == null)
                return ToolResponse.Fail($"UI element '{targetName}' not found.");

            if (go.GetComponent<RectTransform>() == null)
                return ToolResponse.Fail($"'{targetName}' does not have a RectTransform — it may not be a UI element.");

            var parentName = go.transform.parent != null ? go.transform.parent.gameObject.name : null;
            Undo.DestroyObjectImmediate(go);

            return ToolResponse.OkWithData(new { deleted = targetName, parent = parentName },
                $"Deleted UI element '{targetName}'");
        }

        /// <summary>
        /// Duplicate a UI element.
        /// </summary>
        private ToolResponse HandleDuplicateElement(JObject parameters)
        {
            var targetName = ToolHelpers.GetRequiredString(parameters, "target");
            var newName = ToolHelpers.GetOptionalString(parameters, "new_name", null);

            var go = ToolHelpers.FindGameObject(targetName);
            if (go == null)
                return ToolResponse.Fail($"UI element '{targetName}' not found.");

            if (go.GetComponent<RectTransform>() == null)
                return ToolResponse.Fail($"'{targetName}' does not have a RectTransform — it may not be a UI element.");

            var duplicate = UnityEngine.Object.Instantiate(go, go.transform.parent);
            duplicate.name = newName ?? (targetName + " (Copy)");
            Undo.RegisterCreatedObjectUndo(duplicate, $"Duplicate {targetName}");

            // Offset position slightly
            var rt = duplicate.GetComponent<RectTransform>();
            if (rt != null)
            {
                var pos = rt.anchoredPosition;
                pos.x += 10f;
                pos.y -= 10f;
                rt.anchoredPosition = pos;
            }

            return ToolResponse.OkWithData(new
            {
                original = targetName,
                duplicate = duplicate.name,
                parent = go.transform.parent?.gameObject.name
            }, $"Duplicated '{targetName}' as '{duplicate.name}'");
        }

        /// <summary>
        /// Set text content on a Text or TMP_Text component.
        /// </summary>
        private ToolResponse HandleSetText(JObject parameters)
        {
            var targetName = ToolHelpers.GetRequiredString(parameters, "target");
            var text = ToolHelpers.GetRequiredString(parameters, "text");
            var fontSize = parameters["font_size"] != null ? (int?)parameters["font_size"].Value<int>() : null;

            var go = ToolHelpers.FindGameObject(targetName);
            if (go == null)
                return ToolResponse.Fail($"UI element '{targetName}' not found.");

            ResolveUITypes();

            var changes = new List<string>();

            // Try legacy Text
            if (_textType != null)
            {
                var textComp = go.GetComponent(_textType);
                if (textComp != null)
                {
                    ToolHelpers.RecordUndo(textComp, "Set UI Text");
                    SetPropertyViaReflection(textComp, "text", text);
                    changes.Add($"text = '{text}'");

                    if (fontSize.HasValue)
                    {
                        SetPropertyViaReflection(textComp, "fontSize", fontSize.Value);
                        changes.Add($"fontSize = {fontSize.Value}");
                    }

                    EditorUtility.SetDirty(textComp);
                    return ToolResponse.OkWithData(new { target = targetName, changes },
                        $"Set text on '{targetName}' (UnityEngine.UI.Text)");
                }
            }

            // Try TMP_Text via reflection
            var tmpType = ToolHelpers.ResolveComponentType("TMPro.TMP_Text")
                       ?? ToolHelpers.ResolveComponentType("TMPro.TextMeshProUGUI");
            if (tmpType != null)
            {
                var tmpComp = go.GetComponent(tmpType);
                if (tmpComp != null)
                {
                    ToolHelpers.RecordUndo(tmpComp, "Set TMP Text");
                    SetPropertyViaReflection(tmpComp, "text", text);
                    changes.Add($"text = '{text}'");

                    if (fontSize.HasValue)
                    {
                        SetPropertyViaReflection(tmpComp, "fontSize", (float)fontSize.Value);
                        changes.Add($"fontSize = {fontSize.Value}");
                    }

                    EditorUtility.SetDirty(tmpComp);
                    return ToolResponse.OkWithData(new { target = targetName, changes },
                        $"Set text on '{targetName}' (TextMeshProUGUI)");
                }
            }

            return ToolResponse.Fail($"'{targetName}' has no Text or TextMeshProUGUI component.");
        }

        /// <summary>
        /// Set sprite/image on an Image component.
        /// </summary>
        private ToolResponse HandleSetImage(JObject parameters)
        {
            var targetName = ToolHelpers.GetRequiredString(parameters, "target");
            var imagePath = ToolHelpers.GetRequiredString(parameters, "image");

            var go = ToolHelpers.FindGameObject(targetName);
            if (go == null)
                return ToolResponse.Fail($"UI element '{targetName}' not found.");

            ResolveUITypes();

            if (_imageType == null)
                return ToolResponse.Fail("UnityEngine.UI.Image type not found. Ensure Unity UI package is installed.");

            var imageComp = go.GetComponent(_imageType);
            if (imageComp == null)
                return ToolResponse.Fail($"'{targetName}' does not have an Image component.");

            // Load sprite
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(imagePath);
            if (sprite == null)
            {
                // Try finding by name
                var guids = AssetDatabase.FindAssets($"{System.IO.Path.GetFileNameWithoutExtension(imagePath)} t:Sprite");
                if (guids.Length > 0)
                    sprite = AssetDatabase.LoadAssetAtPath<Sprite>(AssetDatabase.GUIDToAssetPath(guids[0]));
            }

            if (sprite == null)
                return ToolResponse.Fail($"Sprite not found at '{imagePath}'. Provide a valid asset path (e.g. 'Assets/UI/Icons/icon.png').");

            ToolHelpers.RecordUndo(imageComp, "Set UI Image");
            SetPropertyViaReflection(imageComp, "sprite", sprite);
            EditorUtility.SetDirty(imageComp);

            return ToolResponse.OkWithData(new { target = targetName, sprite = sprite.name, path = imagePath },
                $"Set sprite '{sprite.name}' on '{targetName}'");
        }

        /// <summary>
        /// Set interactable state on interactive UI components.
        /// </summary>
        private ToolResponse HandleSetInteractable(JObject parameters)
        {
            var targetName = ToolHelpers.GetRequiredString(parameters, "target");
            var interactable = ToolHelpers.GetOptionalBool(parameters, "interactable", true);

            var go = ToolHelpers.FindGameObject(targetName);
            if (go == null)
                return ToolResponse.Fail($"UI element '{targetName}' not found.");

            ResolveUITypes();

            // Try Button, Toggle, Slider, Dropdown, InputField
            var interactableTypes = new[] { _buttonType, _toggleType, _sliderType, _dropdownType, _inputFieldType };
            foreach (var type in interactableTypes)
            {
                if (type == null) continue;
                var comp = go.GetComponent(type);
                if (comp == null) continue;

                ToolHelpers.RecordUndo(comp, "Set UI Interactable");
                SetPropertyViaReflection(comp, "interactable", interactable);
                EditorUtility.SetDirty(comp);

                return ToolResponse.OkWithData(new
                {
                    target = targetName,
                    component = type.Name,
                    interactable
                }, $"Set interactable={interactable} on '{targetName}' ({type.Name})");
            }

            return ToolResponse.Fail($"'{targetName}' has no interactable UI component (Button, Toggle, Slider, Dropdown, or InputField).");
        }

        /// <summary>
        /// Reorder a UI element within its parent's children.
        /// </summary>
        private ToolResponse HandleReorderElement(JObject parameters)
        {
            var targetName = ToolHelpers.GetRequiredString(parameters, "target");
            var siblingIndex = parameters["sibling_index"] != null ? (int?)parameters["sibling_index"].Value<int>() : null;
            var moveToFirst = ToolHelpers.GetOptionalBool(parameters, "move_to_first", false);
            var moveToLast = ToolHelpers.GetOptionalBool(parameters, "move_to_last", false);

            var go = ToolHelpers.FindGameObject(targetName);
            if (go == null)
                return ToolResponse.Fail($"UI element '{targetName}' not found.");

            if (go.transform.parent == null)
                return ToolResponse.Fail($"'{targetName}' has no parent — cannot reorder root objects.");

            ToolHelpers.RecordUndo(go.transform, "Reorder UI Element");

            int newIndex;
            if (moveToFirst)
            {
                go.transform.SetAsFirstSibling();
                newIndex = 0;
            }
            else if (moveToLast)
            {
                go.transform.SetAsLastSibling();
                newIndex = go.transform.parent.childCount - 1;
            }
            else if (siblingIndex.HasValue)
            {
                go.transform.SetSiblingIndex(siblingIndex.Value);
                newIndex = go.transform.GetSiblingIndex();
            }
            else
            {
                return ToolResponse.Fail("Specify 'sibling_index', 'move_to_first', or 'move_to_last'.");
            }

            EditorUtility.SetDirty(go.transform);

            return ToolResponse.OkWithData(new
            {
                target = targetName,
                sibling_index = newIndex,
                parent = go.transform.parent.gameObject.name
            }, $"Moved '{targetName}' to sibling index {newIndex}");
        }

        /// <summary>
        /// Find UI elements by name, type, or text content.
        /// </summary>
        private ToolResponse HandleFindElement(JObject parameters)
        {
            var search = ToolHelpers.GetRequiredString(parameters, "search");
            var canvasFilter = ToolHelpers.GetOptionalString(parameters, "canvas", null);

            ResolveUITypes();

            // Find all canvases
            var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
            if (!string.IsNullOrEmpty(canvasFilter))
                canvases = canvases.Where(c => c.gameObject.name.Contains(canvasFilter)).ToArray();

            var results = new List<object>();

            foreach (var canvas in canvases)
            {
                SearchUIChildren(canvas.transform, search, canvas.gameObject.name, results);
            }

            return ToolResponse.OkWithData(new
            {
                search,
                count = results.Count,
                elements = results
            }, $"Found {results.Count} UI element(s) matching '{search}'");
        }

        private void SearchUIChildren(Transform parent, string search, string canvasName, List<object> results)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                var go = child.gameObject;

                bool matches = go.name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;

                // Also check text content
                if (!matches && _textType != null)
                {
                    var textComp = go.GetComponent(_textType);
                    if (textComp != null)
                    {
                        var textVal = GetPropertyViaReflection(textComp, "text")?.ToString() ?? "";
                        if (textVal.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
                            matches = true;
                    }
                }

                if (matches)
                {
                    var rt = go.GetComponent<RectTransform>();
                    results.Add(new
                    {
                        name = go.name,
                        canvas = canvasName,
                        path = GetGameObjectPath(go),
                        active = go.activeInHierarchy,
                        position = rt != null ? (object)new { x = rt.anchoredPosition.x, y = rt.anchoredPosition.y } : null,
                        components = go.GetComponents<Component>()
                            .Where(c => c != null && !(c is Transform))
                            .Select(c => c.GetType().Name).ToList()
                    });
                }

                SearchUIChildren(child, search, canvasName, results);
            }
        }

        private static string GetGameObjectPath(GameObject go)
        {
            var path = go.name;
            var parent = go.transform.parent;
            while (parent != null)
            {
                path = parent.gameObject.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }

        private static void SetPropertyViaReflection(Component comp, string propertyName, object value)
        {
            var prop = comp.GetType().GetProperty(propertyName,
                BindingFlags.Public | BindingFlags.Instance);
            if (prop != null && prop.CanWrite)
            {
                prop.SetValue(comp, value);
                return;
            }
            var field = comp.GetType().GetField(propertyName,
                BindingFlags.Public | BindingFlags.Instance);
            field?.SetValue(comp, value);
        }

        #endregion
    }
}
