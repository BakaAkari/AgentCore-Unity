using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Tools.Infrastructure;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace AgentCore.Editor.Tools.Native.Specialized
{
    /// <summary>
    /// Manage Unity UI Toolkit assets: UXML documents, USS stylesheets, VisualTreeAsset,
    /// PanelSettings, and runtime UIDocument components.
    /// Covers creating/editing UXML/USS files, querying elements, and configuring UIDocument.
    /// </summary>
    [AgentTool("manage_ui_toolkit",
        Description = "Unity UI Toolkit (UIElements) — the modern declarative UI system for both Editor and runtime. " +
                      "Actions: create_uxml (generate UXML document with element hierarchy), edit_uxml (modify existing UXML), " +
                      "create_uss (generate USS stylesheet with selectors), edit_uss, " +
                      "create_ui_document (attach UIDocument component to GameObject with UXML/PanelSettings), " +
                      "query_elements (inspect visual tree structure of a UIDocument), " +
                      "create_panel_settings (PanelSettings asset for resolution/scaling), " +
                      "generate_code (C# template for binding UI elements). " +
                      "USE FOR: creating modern runtime UI (recommended over uGUI for new projects in Unity 2023+), " +
                      "building Editor tools/inspectors/windows with UI Toolkit, USS styling (flexbox-like layout). " +
                      "NOT FOR: legacy uGUI Canvas/Button/Image (use manage_ui), IMGUI OnGUI code. " +
                      "ACTIVATE WHEN: user mentions 'UI Toolkit', 'UXML', 'USS', 'UIDocument', 'VisualElement', 'UIElements', 'modern UI'.",
        Category = "Specialized",
        Visibility = ToolVisibility.OnDemand,
        RequiresMainThread = true,
        MayModifyScripts = false)]
    public class ManageUIToolkitTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [
                        ""create_uxml"", ""create_uss"", ""add_element"", ""remove_element"",
                        ""set_attribute"", ""set_style"", ""add_class"", ""remove_class"",
                        ""query_element"", ""list_elements"", ""get_uxml_content"", ""get_uss_content"",
                        ""create_panel_settings"", ""configure_ui_document"", ""list_ui_documents"",
                        ""create_editor_window_template"", ""validate_uxml"", ""add_binding"",
                        ""create_custom_element_template"", ""list_assets""
                    ],
                    ""description"": ""Action to perform on UI Toolkit assets""
                },
                ""path"": {
                    ""type"": ""string"",
                    ""description"": ""Asset path (relative to Assets/), e.g. 'UI/MainMenu.uxml' or 'UI/Styles.uss'""
                },
                ""element_type"": {
                    ""type"": ""string"",
                    ""enum"": [""VisualElement"", ""Label"", ""Button"", ""TextField"", ""Toggle"",
                               ""Slider"", ""SliderInt"", ""ProgressBar"", ""ScrollView"", ""ListView"",
                               ""DropdownField"", ""EnumField"", ""IntegerField"", ""FloatField"",
                               ""Vector2Field"", ""Vector3Field"", ""ColorField"", ""ObjectField"",
                               ""Foldout"", ""GroupBox"", ""TwoPaneSplitView"", ""TabView"", ""Tab"",
                               ""Image"", ""IMGUIContainer"", ""TemplateContainer""],
                    ""description"": ""UI Toolkit element type to add""
                },
                ""element_name"": {
                    ""type"": ""string"",
                    ""description"": ""Element name attribute (used as selector #name)""
                },
                ""parent_name"": {
                    ""type"": ""string"",
                    ""description"": ""Parent element name to append child to (empty = root)""
                },
                ""class_names"": {
                    ""type"": ""array"",
                    ""items"": { ""type"": ""string"" },
                    ""description"": ""CSS class names to add to element""
                },
                ""attributes"": {
                    ""type"": ""object"",
                    ""description"": ""Key-value pairs of UXML attributes to set (e.g. text, value, label, tooltip)""
                },
                ""style_properties"": {
                    ""type"": ""object"",
                    ""description"": ""Inline style properties as key-value pairs (e.g. width=200px, background-color=#FF0000)""
                },
                ""selector"": {
                    ""type"": ""string"",
                    ""description"": ""USS selector string (e.g. '.my-class', '#my-id', 'Button', 'Label.title')""
                },
                ""css_rules"": {
                    ""type"": ""object"",
                    ""description"": ""CSS property-value pairs to add to a USS selector rule""
                },
                ""query"": {
                    ""type"": ""string"",
                    ""description"": ""Query string to find elements (name, class, or type)""
                },
                ""game_object"": {
                    ""type"": ""string"",
                    ""description"": ""GameObject name for configure_ui_document action""
                },
                ""uxml_asset"": {
                    ""type"": ""string"",
                    ""description"": ""UXML asset path to assign to UIDocument""
                },
                ""panel_settings_asset"": {
                    ""type"": ""string"",
                    ""description"": ""PanelSettings asset path to assign to UIDocument""
                },
                ""sort_order"": {
                    ""type"": ""integer"",
                    ""description"": ""Sort order for UIDocument""
                },
                ""panel_name"": {
                    ""type"": ""string"",
                    ""description"": ""Name for new PanelSettings asset""
                },
                ""scale_mode"": {
                    ""type"": ""string"",
                    ""enum"": [""ConstantPixelSize"", ""ConstantPhysicalSize"", ""ScaleWithScreenSize""],
                    ""description"": ""PanelSettings scale mode""
                },
                ""reference_resolution"": {
                    ""type"": ""object"",
                    ""properties"": { ""x"": {""type"":""number""}, ""y"": {""type"":""number""} },
                    ""description"": ""Reference resolution for ScaleWithScreenSize mode""
                },
                ""window_class_name"": {
                    ""type"": ""string"",
                    ""description"": ""C# class name for create_editor_window_template""
                },
                ""namespace"": {
                    ""type"": ""string"",
                    ""description"": ""C# namespace for generated code""
                },
                ""binding_path"": {
                    ""type"": ""string"",
                    ""description"": ""Serialized property binding path for add_binding""
                },
                ""custom_element_name"": {
                    ""type"": ""string"",
                    ""description"": ""Class name for create_custom_element_template""
                },
                ""search_folder"": {
                    ""type"": ""string"",
                    ""description"": ""Folder to search for assets in list_assets action. Default: Assets/""
                }
            },
            ""required"": [""action""]
        }");

        /// <summary>
        /// Tool metadata for registration and LLM discovery.
        /// </summary>
        public ToolMetadata Metadata => new ToolMetadata(
            name: "manage_ui_toolkit",
            description: "Manage Unity UI Toolkit (UIElements): create/edit UXML documents and USS stylesheets, configure UIDocument components, query elements, manage PanelSettings, and generate UI Toolkit code templates. Use this for the new Unity UI system (not legacy uGUI).",
            category: "Specialized",
            parametersSchema: _parametersSchema,
            requiresMainThread: true
        );

        /// <summary>
        /// Execute a UI Toolkit management action.
        /// </summary>
        public Task<ToolResult> ExecuteAsync(JObject parameters, CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();
            ToolResponse response;

            try
            {
                var action = ToolHelpers.GetRequiredString(parameters, "action").ToLowerInvariant();

                switch (action)
                {
                    case "create_uxml":
                        response = HandleCreateUxml(parameters);
                        break;
                    case "create_uss":
                        response = HandleCreateUss(parameters);
                        break;
                    case "add_element":
                        response = HandleAddElement(parameters);
                        break;
                    case "remove_element":
                        response = HandleRemoveElement(parameters);
                        break;
                    case "set_attribute":
                        response = HandleSetAttribute(parameters);
                        break;
                    case "set_style":
                        response = HandleSetStyle(parameters);
                        break;
                    case "add_class":
                        response = HandleAddClass(parameters);
                        break;
                    case "remove_class":
                        response = HandleRemoveClass(parameters);
                        break;
                    case "query_element":
                        response = HandleQueryElement(parameters);
                        break;
                    case "list_elements":
                        response = HandleListElements(parameters);
                        break;
                    case "get_uxml_content":
                        response = HandleGetUxmlContent(parameters);
                        break;
                    case "get_uss_content":
                        response = HandleGetUssContent(parameters);
                        break;
                    case "create_panel_settings":
                        response = HandleCreatePanelSettings(parameters);
                        break;
                    case "configure_ui_document":
                        response = HandleConfigureUIDocument(parameters);
                        break;
                    case "list_ui_documents":
                        response = HandleListUIDocuments(parameters);
                        break;
                    case "create_editor_window_template":
                        response = HandleCreateEditorWindowTemplate(parameters);
                        break;
                    case "validate_uxml":
                        response = HandleValidateUxml(parameters);
                        break;
                    case "add_binding":
                        response = HandleAddBinding(parameters);
                        break;
                    case "create_custom_element_template":
                        response = HandleCreateCustomElementTemplate(parameters);
                        break;
                    case "list_assets":
                        response = HandleListAssets(parameters);
                        break;
                    default:
                        response = ToolResponse.Fail(
                            $"Unknown action: '{action}'. Valid actions: create_uxml, create_uss, add_element, remove_element, " +
                            "set_attribute, set_style, add_class, remove_class, query_element, list_elements, " +
                            "get_uxml_content, get_uss_content, create_panel_settings, configure_ui_document, " +
                            "list_ui_documents, create_editor_window_template, validate_uxml, add_binding, " +
                            "create_custom_element_template, list_assets");
                        break;
                }
            }
            catch (Exception ex)
            {
                response = ToolResponse.Fail($"Error executing manage_ui_toolkit '{parameters?["action"]}': {ex.Message}");
            }

            sw.Stop();
            return Task.FromResult(response.ToToolResult(sw.Elapsed.TotalMilliseconds));
        }

        // ─────────────────────────────────────────────────────────────────────
        // UXML File Operations
        // ─────────────────────────────────────────────────────────────────────

        private ToolResponse HandleCreateUxml(JObject parameters)
        {
            var path = ToolHelpers.GetRequiredString(parameters, "path");
            path = NormalizePath(path, ".uxml");

            var fullPath = Path.Combine(Application.dataPath, "..", path);
            fullPath = Path.GetFullPath(fullPath);

            if (File.Exists(fullPath))
                return ToolResponse.Fail($"UXML file already exists: {path}. Use get_uxml_content to read it or add_element to modify it.");

            EnsureDirectory(fullPath);

            var content = GenerateEmptyUxml();
            File.WriteAllText(fullPath, content);
            AssetDatabase.ImportAsset(path);

            return ToolResponse.OkWithData(new { path, content }, $"Created UXML document: {path}");
        }

        private ToolResponse HandleCreateUss(JObject parameters)
        {
            var path = ToolHelpers.GetRequiredString(parameters, "path");
            path = NormalizePath(path, ".uss");

            var fullPath = Path.Combine(Application.dataPath, "..", path);
            fullPath = Path.GetFullPath(fullPath);

            if (File.Exists(fullPath))
                return ToolResponse.Fail($"USS file already exists: {path}. Use get_uss_content to read it or set_style to modify it.");

            EnsureDirectory(fullPath);

            var content = GenerateEmptyUss();
            File.WriteAllText(fullPath, content);
            AssetDatabase.ImportAsset(path);

            return ToolResponse.OkWithData(new { path, content }, $"Created USS stylesheet: {path}");
        }

        private ToolResponse HandleGetUxmlContent(JObject parameters)
        {
            var path = ToolHelpers.GetRequiredString(parameters, "path");
            path = NormalizePath(path, ".uxml");

            var fullPath = GetFullAssetPath(path);
            if (!File.Exists(fullPath))
                return ToolResponse.Fail($"UXML file not found: {path}");

            var content = File.ReadAllText(fullPath);
            return ToolResponse.OkWithData(new { path, content, line_count = content.Split('\n').Length },
                $"Read UXML: {path}");
        }

        private ToolResponse HandleGetUssContent(JObject parameters)
        {
            var path = ToolHelpers.GetRequiredString(parameters, "path");
            path = NormalizePath(path, ".uss");

            var fullPath = GetFullAssetPath(path);
            if (!File.Exists(fullPath))
                return ToolResponse.Fail($"USS file not found: {path}");

            var content = File.ReadAllText(fullPath);
            return ToolResponse.OkWithData(new { path, content, line_count = content.Split('\n').Length },
                $"Read USS: {path}");
        }

        private ToolResponse HandleValidateUxml(JObject parameters)
        {
            var path = ToolHelpers.GetRequiredString(parameters, "path");
            path = NormalizePath(path, ".uxml");

            var fullPath = GetFullAssetPath(path);
            if (!File.Exists(fullPath))
                return ToolResponse.Fail($"UXML file not found: {path}");

            var content = File.ReadAllText(fullPath);
            var errors = new List<string>();
            var warnings = new List<string>();

            // Basic XML validation
            try
            {
                var doc = new System.Xml.XmlDocument();
                doc.LoadXml(content);

                // Check root element
                var root = doc.DocumentElement;
                if (root == null)
                {
                    errors.Add("UXML has no root element.");
                }
                else if (root.Name != "ui:UXML" && root.Name != "UXML")
                {
                    warnings.Add($"Root element is '{root.Name}', expected 'ui:UXML'.");
                }

                // Check for xmlns declarations
                if (root != null && root.GetAttribute("xmlns:ui") == "" && root.GetAttribute("xmlns:uie") == "")
                {
                    warnings.Add("Missing xmlns:ui namespace declaration. Add: xmlns:ui=\"UnityEngine.UIElements\"");
                }

                // Count elements
                var allElements = doc.GetElementsByTagName("*");
                var elementCount = allElements.Count;

                return ToolResponse.OkWithData(new
                {
                    path,
                    valid = errors.Count == 0,
                    errors,
                    warnings,
                    element_count = elementCount
                }, errors.Count == 0 ? $"UXML is valid ({elementCount} elements)" : $"UXML has {errors.Count} error(s)");
            }
            catch (System.Xml.XmlException ex)
            {
                errors.Add($"XML parse error at line {ex.LineNumber}: {ex.Message}");
                return ToolResponse.OkWithData(new
                {
                    path,
                    valid = false,
                    errors,
                    warnings
                }, $"UXML has XML errors");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Element Manipulation
        // ─────────────────────────────────────────────────────────────────────

        private ToolResponse HandleAddElement(JObject parameters)
        {
            var path = ToolHelpers.GetRequiredString(parameters, "path");
            path = NormalizePath(path, ".uxml");

            var elementType = ToolHelpers.GetRequiredString(parameters, "element_type");
            var elementName = ToolHelpers.GetOptionalString(parameters, "element_name");
            var parentName = ToolHelpers.GetOptionalString(parameters, "parent_name");
            var classNames = parameters["class_names"]?.ToObject<List<string>>() ?? new List<string>();
            var attributes = parameters["attributes"] as JObject;
            var styleProps = parameters["style_properties"] as JObject;

            var fullPath = GetFullAssetPath(path);
            if (!File.Exists(fullPath))
                return ToolResponse.Fail($"UXML file not found: {path}. Use create_uxml first.");

            var content = File.ReadAllText(fullPath);
            var doc = new System.Xml.XmlDocument();
            doc.LoadXml(content);

            // Ensure namespace
            var nsManager = new System.Xml.XmlNamespaceManager(doc.NameTable);
            nsManager.AddNamespace("ui", "UnityEngine.UIElements");
            nsManager.AddNamespace("uie", "UnityEditor.UIElements");

            // Find parent node
            System.Xml.XmlNode parentNode;
            if (!string.IsNullOrEmpty(parentName))
            {
                parentNode = FindElementByName(doc, parentName);
                if (parentNode == null)
                    return ToolResponse.Fail($"Parent element '{parentName}' not found in {path}.");
            }
            else
            {
                // Append to root
                parentNode = doc.DocumentElement;
                if (parentNode == null)
                    return ToolResponse.Fail("UXML has no root element.");
            }

            // Determine namespace prefix
            var prefix = GetElementPrefix(elementType);
            var qualifiedName = string.IsNullOrEmpty(prefix) ? elementType : $"{prefix}:{elementType}";

            // Create element
            var newElement = doc.CreateElement(qualifiedName, GetNamespaceUri(prefix));

            // Set name attribute
            if (!string.IsNullOrEmpty(elementName))
                newElement.SetAttribute("name", elementName);

            // Set class attribute
            if (classNames.Count > 0)
                newElement.SetAttribute("class", string.Join(" ", classNames));

            // Set additional attributes
            if (attributes != null)
            {
                foreach (var prop in attributes.Properties())
                    newElement.SetAttribute(prop.Name, prop.Value?.ToString() ?? "");
            }

            // Set inline style
            if (styleProps != null)
            {
                var styleStr = BuildInlineStyle(styleProps);
                if (!string.IsNullOrEmpty(styleStr))
                    newElement.SetAttribute("style", styleStr);
            }

            parentNode.AppendChild(newElement);

            // Save
            var settings = new System.Xml.XmlWriterSettings
            {
                Indent = true,
                IndentChars = "    ",
                NewLineChars = "\n",
                OmitXmlDeclaration = false
            };
            using (var writer = System.Xml.XmlWriter.Create(fullPath, settings))
                doc.Save(writer);

            AssetDatabase.ImportAsset(path);

            return ToolResponse.OkWithData(new
            {
                path,
                added_element = qualifiedName,
                name = elementName,
                parent = parentName ?? "root"
            }, $"Added <{qualifiedName}> to {path}");
        }

        private ToolResponse HandleRemoveElement(JObject parameters)
        {
            var path = ToolHelpers.GetRequiredString(parameters, "path");
            path = NormalizePath(path, ".uxml");
            var elementName = ToolHelpers.GetRequiredString(parameters, "element_name");

            var fullPath = GetFullAssetPath(path);
            if (!File.Exists(fullPath))
                return ToolResponse.Fail($"UXML file not found: {path}");

            var content = File.ReadAllText(fullPath);
            var doc = new System.Xml.XmlDocument();
            doc.LoadXml(content);

            var node = FindElementByName(doc, elementName);
            if (node == null)
                return ToolResponse.Fail($"Element '{elementName}' not found in {path}.");

            node.ParentNode?.RemoveChild(node);

            var settings = new System.Xml.XmlWriterSettings
            {
                Indent = true,
                IndentChars = "    ",
                NewLineChars = "\n",
                OmitXmlDeclaration = false
            };
            using (var writer = System.Xml.XmlWriter.Create(fullPath, settings))
                doc.Save(writer);

            AssetDatabase.ImportAsset(path);

            return ToolResponse.Ok($"Removed element '{elementName}' from {path}");
        }

        private ToolResponse HandleSetAttribute(JObject parameters)
        {
            var path = ToolHelpers.GetRequiredString(parameters, "path");
            path = NormalizePath(path, ".uxml");
            var elementName = ToolHelpers.GetRequiredString(parameters, "element_name");
            var attributes = parameters["attributes"] as JObject;

            if (attributes == null || !attributes.HasValues)
                return ToolResponse.Fail("'attributes' parameter is required and must have at least one property.");

            var fullPath = GetFullAssetPath(path);
            if (!File.Exists(fullPath))
                return ToolResponse.Fail($"UXML file not found: {path}");

            var content = File.ReadAllText(fullPath);
            var doc = new System.Xml.XmlDocument();
            doc.LoadXml(content);

            var node = FindElementByName(doc, elementName) as System.Xml.XmlElement;
            if (node == null)
                return ToolResponse.Fail($"Element '{elementName}' not found in {path}.");

            var changed = new List<string>();
            foreach (var prop in attributes.Properties())
            {
                node.SetAttribute(prop.Name, prop.Value?.ToString() ?? "");
                changed.Add(prop.Name);
            }

            var settings = new System.Xml.XmlWriterSettings
            {
                Indent = true,
                IndentChars = "    ",
                NewLineChars = "\n",
                OmitXmlDeclaration = false
            };
            using (var writer = System.Xml.XmlWriter.Create(fullPath, settings))
                doc.Save(writer);

            AssetDatabase.ImportAsset(path);

            return ToolResponse.OkWithData(new { path, element = elementName, changed_attributes = changed },
                $"Updated {changed.Count} attribute(s) on '{elementName}'");
        }

        private ToolResponse HandleSetStyle(JObject parameters)
        {
            var path = ToolHelpers.GetRequiredString(parameters, "path");
            var styleProps = parameters["style_properties"] as JObject;
            var selector = ToolHelpers.GetOptionalString(parameters, "selector");
            var cssRules = parameters["css_rules"] as JObject;

            if (!string.IsNullOrEmpty(selector))
            {
                // Modify USS file
                path = NormalizePath(path, ".uss");
                var fullPath = GetFullAssetPath(path);
                if (!File.Exists(fullPath))
                    return ToolResponse.Fail($"USS file not found: {path}. Use create_uss first.");

                var rules = cssRules ?? styleProps;
                if (rules == null || !rules.HasValues)
                    return ToolResponse.Fail("'css_rules' or 'style_properties' is required for USS selector modification.");

                var ussContent = File.ReadAllText(fullPath);
                var newRule = BuildUssRule(selector, rules);

                // Check if selector already exists
                if (ussContent.Contains(selector + " {") || ussContent.Contains(selector + "{"))
                {
                    // Append properties to existing rule (simple approach: add before closing brace)
                    var insertIdx = FindSelectorInsertPoint(ussContent, selector);
                    if (insertIdx >= 0)
                    {
                        var propsStr = string.Join("\n", rules.Properties()
                            .Select(p => $"    {p.Name}: {p.Value};"));
                        ussContent = ussContent.Insert(insertIdx, "\n" + propsStr);
                    }
                    else
                    {
                        ussContent += "\n\n" + newRule;
                    }
                }
                else
                {
                    ussContent += "\n\n" + newRule;
                }

                File.WriteAllText(fullPath, ussContent);
                AssetDatabase.ImportAsset(path);

                return ToolResponse.OkWithData(new { path, selector, rule = newRule },
                    $"Added/updated CSS rule for '{selector}' in {path}");
            }
            else
            {
                // Modify inline style on UXML element
                path = NormalizePath(path, ".uxml");
                var elementName = ToolHelpers.GetRequiredString(parameters, "element_name");

                if (styleProps == null || !styleProps.HasValues)
                    return ToolResponse.Fail("'style_properties' is required for inline style modification.");

                var fullPath = GetFullAssetPath(path);
                if (!File.Exists(fullPath))
                    return ToolResponse.Fail($"UXML file not found: {path}");

                var content = File.ReadAllText(fullPath);
                var doc = new System.Xml.XmlDocument();
                doc.LoadXml(content);

                var node = FindElementByName(doc, elementName) as System.Xml.XmlElement;
                if (node == null)
                    return ToolResponse.Fail($"Element '{elementName}' not found in {path}.");

                var existingStyle = node.GetAttribute("style") ?? "";
                var newStyle = MergeInlineStyles(existingStyle, styleProps);
                node.SetAttribute("style", newStyle);

                var settings = new System.Xml.XmlWriterSettings
                {
                    Indent = true,
                    IndentChars = "    ",
                    NewLineChars = "\n",
                    OmitXmlDeclaration = false
                };
                using (var writer = System.Xml.XmlWriter.Create(fullPath, settings))
                    doc.Save(writer);

                AssetDatabase.ImportAsset(path);

                return ToolResponse.OkWithData(new { path, element = elementName, style = newStyle },
                    $"Updated inline style on '{elementName}'");
            }
        }

        private ToolResponse HandleAddClass(JObject parameters)
        {
            var path = ToolHelpers.GetRequiredString(parameters, "path");
            path = NormalizePath(path, ".uxml");
            var elementName = ToolHelpers.GetRequiredString(parameters, "element_name");
            var classNames = parameters["class_names"]?.ToObject<List<string>>();

            if (classNames == null || classNames.Count == 0)
                return ToolResponse.Fail("'class_names' array is required.");

            var fullPath = GetFullAssetPath(path);
            if (!File.Exists(fullPath))
                return ToolResponse.Fail($"UXML file not found: {path}");

            var content = File.ReadAllText(fullPath);
            var doc = new System.Xml.XmlDocument();
            doc.LoadXml(content);

            var node = FindElementByName(doc, elementName) as System.Xml.XmlElement;
            if (node == null)
                return ToolResponse.Fail($"Element '{elementName}' not found in {path}.");

            var existing = (node.GetAttribute("class") ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            foreach (var cls in classNames)
                if (!existing.Contains(cls))
                    existing.Add(cls);

            node.SetAttribute("class", string.Join(" ", existing));

            var settings = new System.Xml.XmlWriterSettings
            {
                Indent = true,
                IndentChars = "    ",
                NewLineChars = "\n",
                OmitXmlDeclaration = false
            };
            using (var writer = System.Xml.XmlWriter.Create(fullPath, settings))
                doc.Save(writer);

            AssetDatabase.ImportAsset(path);

            return ToolResponse.OkWithData(new { path, element = elementName, classes = existing },
                $"Added class(es) to '{elementName}'");
        }

        private ToolResponse HandleRemoveClass(JObject parameters)
        {
            var path = ToolHelpers.GetRequiredString(parameters, "path");
            path = NormalizePath(path, ".uxml");
            var elementName = ToolHelpers.GetRequiredString(parameters, "element_name");
            var classNames = parameters["class_names"]?.ToObject<List<string>>();

            if (classNames == null || classNames.Count == 0)
                return ToolResponse.Fail("'class_names' array is required.");

            var fullPath = GetFullAssetPath(path);
            if (!File.Exists(fullPath))
                return ToolResponse.Fail($"UXML file not found: {path}");

            var content = File.ReadAllText(fullPath);
            var doc = new System.Xml.XmlDocument();
            doc.LoadXml(content);

            var node = FindElementByName(doc, elementName) as System.Xml.XmlElement;
            if (node == null)
                return ToolResponse.Fail($"Element '{elementName}' not found in {path}.");

            var existing = (node.GetAttribute("class") ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            existing.RemoveAll(c => classNames.Contains(c));

            if (existing.Count > 0)
                node.SetAttribute("class", string.Join(" ", existing));
            else
                node.RemoveAttribute("class");

            var settings = new System.Xml.XmlWriterSettings
            {
                Indent = true,
                IndentChars = "    ",
                NewLineChars = "\n",
                OmitXmlDeclaration = false
            };
            using (var writer = System.Xml.XmlWriter.Create(fullPath, settings))
                doc.Save(writer);

            AssetDatabase.ImportAsset(path);

            return ToolResponse.OkWithData(new { path, element = elementName, remaining_classes = existing },
                $"Removed class(es) from '{elementName}'");
        }

        private ToolResponse HandleAddBinding(JObject parameters)
        {
            var path = ToolHelpers.GetRequiredString(parameters, "path");
            path = NormalizePath(path, ".uxml");
            var elementName = ToolHelpers.GetRequiredString(parameters, "element_name");
            var bindingPath = ToolHelpers.GetRequiredString(parameters, "binding_path");

            var fullPath = GetFullAssetPath(path);
            if (!File.Exists(fullPath))
                return ToolResponse.Fail($"UXML file not found: {path}");

            var content = File.ReadAllText(fullPath);
            var doc = new System.Xml.XmlDocument();
            doc.LoadXml(content);

            var node = FindElementByName(doc, elementName) as System.Xml.XmlElement;
            if (node == null)
                return ToolResponse.Fail($"Element '{elementName}' not found in {path}.");

            node.SetAttribute("binding-path", bindingPath);

            var settings = new System.Xml.XmlWriterSettings
            {
                Indent = true,
                IndentChars = "    ",
                NewLineChars = "\n",
                OmitXmlDeclaration = false
            };
            using (var writer = System.Xml.XmlWriter.Create(fullPath, settings))
                doc.Save(writer);

            AssetDatabase.ImportAsset(path);

            return ToolResponse.OkWithData(new { path, element = elementName, binding_path = bindingPath },
                $"Set binding-path='{bindingPath}' on '{elementName}'");
        }

        // ─────────────────────────────────────────────────────────────────────
        // Query / List
        // ─────────────────────────────────────────────────────────────────────

        private ToolResponse HandleQueryElement(JObject parameters)
        {
            var path = ToolHelpers.GetRequiredString(parameters, "path");
            path = NormalizePath(path, ".uxml");
            var query = ToolHelpers.GetRequiredString(parameters, "query");

            var fullPath = GetFullAssetPath(path);
            if (!File.Exists(fullPath))
                return ToolResponse.Fail($"UXML file not found: {path}");

            var content = File.ReadAllText(fullPath);
            var doc = new System.Xml.XmlDocument();
            doc.LoadXml(content);

            var results = new List<object>();
            var allNodes = doc.GetElementsByTagName("*");

            foreach (System.Xml.XmlElement el in allNodes)
            {
                var name = el.GetAttribute("name");
                var cls = el.GetAttribute("class");
                var localName = el.LocalName;

                bool matches = false;
                if (query.StartsWith("#"))
                    matches = name == query.Substring(1);
                else if (query.StartsWith("."))
                    matches = cls != null && cls.Split(' ').Contains(query.Substring(1));
                else
                    matches = localName.Equals(query, StringComparison.OrdinalIgnoreCase) ||
                              name.Equals(query, StringComparison.OrdinalIgnoreCase);

                if (matches)
                {
                    results.Add(new
                    {
                        type = localName,
                        name,
                        classes = cls,
                        style = el.GetAttribute("style"),
                        binding_path = el.GetAttribute("binding-path"),
                        attribute_count = el.Attributes.Count,
                        child_count = el.ChildNodes.Count
                    });
                }
            }

            return ToolResponse.OkWithData(new { path, query, count = results.Count, elements = results },
                $"Found {results.Count} element(s) matching '{query}'");
        }

        private ToolResponse HandleListElements(JObject parameters)
        {
            var path = ToolHelpers.GetRequiredString(parameters, "path");
            path = NormalizePath(path, ".uxml");

            var fullPath = GetFullAssetPath(path);
            if (!File.Exists(fullPath))
                return ToolResponse.Fail($"UXML file not found: {path}");

            var content = File.ReadAllText(fullPath);
            var doc = new System.Xml.XmlDocument();
            doc.LoadXml(content);

            var elements = new List<object>();
            CollectElements(doc.DocumentElement, elements, 0);

            return ToolResponse.OkWithData(new { path, count = elements.Count, elements },
                $"Listed {elements.Count} element(s) in {path}");
        }

        private void CollectElements(System.Xml.XmlNode node, List<object> results, int depth)
        {
            if (node == null) return;
            if (node is System.Xml.XmlElement el)
            {
                results.Add(new
                {
                    depth,
                    type = el.LocalName,
                    name = el.GetAttribute("name"),
                    classes = el.GetAttribute("class"),
                    binding_path = el.GetAttribute("binding-path")
                });
            }
            foreach (System.Xml.XmlNode child in node.ChildNodes)
                CollectElements(child, results, depth + 1);
        }

        // ─────────────────────────────────────────────────────────────────────
        // PanelSettings & UIDocument
        // ─────────────────────────────────────────────────────────────────────

        private ToolResponse HandleCreatePanelSettings(JObject parameters)
        {
            var panelName = ToolHelpers.GetOptionalString(parameters, "panel_name", "PanelSettings");
            var path = ToolHelpers.GetOptionalString(parameters, "path", $"Assets/UI/{panelName}.asset");
            path = NormalizePath(path, ".asset");

            var fullPath = GetFullAssetPath(path);
            EnsureDirectory(fullPath);

            // Use AssetDatabase to create PanelSettings
            var panelSettingsType = typeof(UnityEngine.UIElements.PanelSettings);
            var asset = ScriptableObject.CreateInstance(panelSettingsType);
            if (asset == null)
                return ToolResponse.Fail("Failed to create PanelSettings instance.");

            // Configure scale mode
            var scaleModeStr = ToolHelpers.GetOptionalString(parameters, "scale_mode", "ScaleWithScreenSize");
            var scaleModeField = panelSettingsType.GetProperty("scaleMode");
            if (scaleModeField != null && Enum.TryParse(scaleModeField.PropertyType, scaleModeStr, true, out var scaleMode))
                scaleModeField.SetValue(asset, scaleMode);

            // Configure reference resolution
            var refRes = parameters["reference_resolution"] as JObject;
            if (refRes != null)
            {
                var refResProp = panelSettingsType.GetProperty("referenceResolution");
                if (refResProp != null)
                {
                    var x = refRes["x"]?.Value<float>() ?? 1920f;
                    var y = refRes["y"]?.Value<float>() ?? 1080f;
                    refResProp.SetValue(asset, new Vector2(x, y));
                }
            }

            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();

            return ToolResponse.OkWithData(new { path, scale_mode = scaleModeStr },
                $"Created PanelSettings: {path}");
        }

        private ToolResponse HandleConfigureUIDocument(JObject parameters)
        {
            var gameObjectName = ToolHelpers.GetRequiredString(parameters, "game_object");
            var uxmlAssetPath = ToolHelpers.GetOptionalString(parameters, "uxml_asset");
            var panelSettingsPath = ToolHelpers.GetOptionalString(parameters, "panel_settings_asset");
            var sortOrder = parameters["sort_order"] != null
                ? (int?)parameters["sort_order"].Value<int>()
                : null;

            // Find or create GameObject
            var go = GameObject.Find(gameObjectName);
            if (go == null)
            {
                go = new GameObject(gameObjectName);
                Undo.RegisterCreatedObjectUndo(go, $"Create {gameObjectName}");
            }

            // Get or add UIDocument component
            var uiDocType = typeof(UnityEngine.UIElements.UIDocument);
            var uiDoc = go.GetComponent(uiDocType) as UnityEngine.UIElements.UIDocument;
            if (uiDoc == null)
            {
                uiDoc = Undo.AddComponent(go, uiDocType) as UnityEngine.UIElements.UIDocument;
            }

            if (uiDoc == null)
                return ToolResponse.Fail($"Failed to add UIDocument component to '{gameObjectName}'.");

            var changes = new List<string>();

            // Assign UXML asset
            if (!string.IsNullOrEmpty(uxmlAssetPath))
            {
                var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlAssetPath);
                if (vta == null)
                    return ToolResponse.Fail($"VisualTreeAsset not found at: {uxmlAssetPath}");
                uiDoc.visualTreeAsset = vta;
                changes.Add($"visualTreeAsset = {uxmlAssetPath}");
            }

            // Assign PanelSettings
            if (!string.IsNullOrEmpty(panelSettingsPath))
            {
                var ps = AssetDatabase.LoadAssetAtPath<PanelSettings>(panelSettingsPath);
                if (ps == null)
                    return ToolResponse.Fail($"PanelSettings not found at: {panelSettingsPath}");
                uiDoc.panelSettings = ps;
                changes.Add($"panelSettings = {panelSettingsPath}");
            }

            // Set sort order
            if (sortOrder.HasValue)
            {
                uiDoc.sortingOrder = sortOrder.Value;
                changes.Add($"sortingOrder = {sortOrder.Value}");
            }

            EditorUtility.SetDirty(go);

            return ToolResponse.OkWithData(new
            {
                game_object = gameObjectName,
                changes,
                uxml = uiDoc.visualTreeAsset != null ? AssetDatabase.GetAssetPath(uiDoc.visualTreeAsset) : null,
                panel_settings = uiDoc.panelSettings != null ? AssetDatabase.GetAssetPath(uiDoc.panelSettings) : null,
                sort_order = uiDoc.sortingOrder
            }, $"Configured UIDocument on '{gameObjectName}': {changes.Count} change(s)");
        }

        private ToolResponse HandleListUIDocuments(JObject parameters)
        {
            var uiDocType = typeof(UnityEngine.UIElements.UIDocument);
            var allDocs = UnityEngine.Object.FindObjectsOfType(uiDocType) as UnityEngine.UIElements.UIDocument[];

            if (allDocs == null || allDocs.Length == 0)
                return ToolResponse.OkWithData(new { count = 0, documents = new object[0] },
                    "No UIDocument components found in the scene.");

            var docs = allDocs.Select(d => new
            {
                game_object = d.gameObject.name,
                uxml = d.visualTreeAsset != null ? AssetDatabase.GetAssetPath(d.visualTreeAsset) : null,
                panel_settings = d.panelSettings != null ? AssetDatabase.GetAssetPath(d.panelSettings) : null,
                sort_order = d.sortingOrder,
                active = d.gameObject.activeInHierarchy
            }).ToList();

            return ToolResponse.OkWithData(new { count = docs.Count, documents = docs },
                $"Found {docs.Count} UIDocument(s) in scene");
        }

        // ─────────────────────────────────────────────────────────────────────
        // Code Templates
        // ─────────────────────────────────────────────────────────────────────

        private ToolResponse HandleCreateEditorWindowTemplate(JObject parameters)
        {
            var windowClassName = ToolHelpers.GetRequiredString(parameters, "window_class_name");
            var ns = ToolHelpers.GetOptionalString(parameters, "namespace", "MyGame.Editor");
            var uxmlPath = ToolHelpers.GetOptionalString(parameters, "uxml_asset", $"Assets/UI/{windowClassName}.uxml");
            var outputPath = ToolHelpers.GetOptionalString(parameters, "path", $"Assets/Editor/{windowClassName}.cs");

            var csContent = GenerateEditorWindowTemplate(windowClassName, ns, uxmlPath);
            var uxmlContent = GenerateEditorWindowUxml(windowClassName);

            // Write CS file
            var csFullPath = GetFullAssetPath(outputPath);
            EnsureDirectory(csFullPath);
            File.WriteAllText(csFullPath, csContent);

            // Write UXML file
            var uxmlFullPath = GetFullAssetPath(uxmlPath);
            EnsureDirectory(uxmlFullPath);
            if (!File.Exists(uxmlFullPath))
                File.WriteAllText(uxmlFullPath, uxmlContent);

            AssetDatabase.Refresh();

            return ToolResponse.OkWithData(new
            {
                cs_path = outputPath,
                uxml_path = uxmlPath,
                class_name = windowClassName,
                namespace_name = ns
            }, $"Created EditorWindow template: {windowClassName}");
        }

        private ToolResponse HandleCreateCustomElementTemplate(JObject parameters)
        {
            var elementName = ToolHelpers.GetRequiredString(parameters, "custom_element_name");
            var ns = ToolHelpers.GetOptionalString(parameters, "namespace", "MyGame.UI");
            var outputPath = ToolHelpers.GetOptionalString(parameters, "path", $"Assets/UI/{elementName}.cs");

            var csContent = GenerateCustomElementTemplate(elementName, ns);

            var fullPath = GetFullAssetPath(outputPath);
            EnsureDirectory(fullPath);
            File.WriteAllText(fullPath, csContent);
            AssetDatabase.ImportAsset(outputPath);

            return ToolResponse.OkWithData(new
            {
                path = outputPath,
                class_name = elementName,
                namespace_name = ns
            }, $"Created custom VisualElement template: {elementName}");
        }

        // ─────────────────────────────────────────────────────────────────────
        // Asset Listing
        // ─────────────────────────────────────────────────────────────────────

        private ToolResponse HandleListAssets(JObject parameters)
        {
            var searchFolder = ToolHelpers.GetOptionalString(parameters, "search_folder", "Assets");

            var uxmlGuids = AssetDatabase.FindAssets("t:VisualTreeAsset", new[] { searchFolder });
            var ussGuids = AssetDatabase.FindAssets("t:StyleSheet", new[] { searchFolder });
            var panelGuids = AssetDatabase.FindAssets("t:PanelSettings", new[] { searchFolder });

            var uxmlAssets = uxmlGuids.Select(g => AssetDatabase.GUIDToAssetPath(g)).ToList();
            var ussAssets = ussGuids.Select(g => AssetDatabase.GUIDToAssetPath(g)).ToList();
            var panelAssets = panelGuids.Select(g => AssetDatabase.GUIDToAssetPath(g)).ToList();

            return ToolResponse.OkWithData(new
            {
                search_folder = searchFolder,
                uxml_count = uxmlAssets.Count,
                uss_count = ussAssets.Count,
                panel_settings_count = panelAssets.Count,
                uxml_assets = uxmlAssets,
                uss_assets = ussAssets,
                panel_settings_assets = panelAssets
            }, $"Found {uxmlAssets.Count} UXML, {ussAssets.Count} USS, {panelAssets.Count} PanelSettings in '{searchFolder}'");
        }

        // ─────────────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────────────

        private static string NormalizePath(string path, string expectedExtension)
        {
            if (!path.StartsWith("Assets/") && !path.StartsWith("Assets\\"))
                path = "Assets/" + path;
            if (!path.EndsWith(expectedExtension, StringComparison.OrdinalIgnoreCase))
                path += expectedExtension;
            return path.Replace('\\', '/');
        }

        private static string GetFullAssetPath(string assetPath)
        {
            // assetPath is relative to project root (e.g. "Assets/UI/Main.uxml")
            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            return Path.GetFullPath(Path.Combine(projectRoot, assetPath));
        }

        private static void EnsureDirectory(string fullFilePath)
        {
            var dir = Path.GetDirectoryName(fullFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }

        private static string GenerateEmptyUxml()
        {
            return @"<?xml version=""1.0"" encoding=""utf-8""?>
<ui:UXML
    xmlns:ui=""UnityEngine.UIElements""
    xmlns:uie=""UnityEditor.UIElements""
    xsi=""http://www.w3.org/2001/XMLSchema-instance""
    engine=""UnityEngine.UIElements""
    editor=""UnityEditor.UIElements""
    noNamespaceSchemaLocation=""../UIElementsSchema/UIElements.xsd""
    editor-extension-mode=""False"">
    <ui:VisualElement name=""root"" style=""flex-grow: 1;"" />
</ui:UXML>";
        }

        private static string GenerateEmptyUss()
        {
            return @"/* UI Toolkit Stylesheet */

.container {
    flex-direction: column;
    flex-grow: 1;
    padding: 8px;
}

.title {
    font-size: 18px;
    -unity-font-style: bold;
    margin-bottom: 8px;
}

.button {
    margin: 4px;
    padding: 6px 12px;
}
";
        }

        private static string GetElementPrefix(string elementType)
        {
            // Editor-only elements use "uie" prefix
            var editorElements = new HashSet<string>
            {
                "PropertyField", "InspectorElement", "ObjectField", "ColorField",
                "CurveField", "GradientField", "LayerField", "LayerMaskField",
                "MaskField", "TagField", "EnumFlagsField", "ToolbarMenu",
                "Toolbar", "ToolbarButton", "ToolbarToggle", "ToolbarSearchField",
                "ToolbarSpacer", "ToolbarPopupSearchField"
            };
            return editorElements.Contains(elementType) ? "uie" : "ui";
        }

        private static string GetNamespaceUri(string prefix)
        {
            return prefix == "uie" ? "UnityEditor.UIElements" : "UnityEngine.UIElements";
        }

        private static System.Xml.XmlNode FindElementByName(System.Xml.XmlDocument doc, string name)
        {
            var allNodes = doc.GetElementsByTagName("*");
            foreach (System.Xml.XmlElement el in allNodes)
            {
                if (el.GetAttribute("name") == name)
                    return el;
            }
            return null;
        }

        private static string BuildInlineStyle(JObject styleProps)
        {
            var parts = styleProps.Properties()
                .Select(p => $"{p.Name}: {p.Value};")
                .ToList();
            return string.Join(" ", parts);
        }

        private static string MergeInlineStyles(string existing, JObject newProps)
        {
            // Parse existing style into dict
            var styleDict = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(existing))
            {
                foreach (var part in existing.Split(';'))
                {
                    var trimmed = part.Trim();
                    if (string.IsNullOrEmpty(trimmed)) continue;
                    var colonIdx = trimmed.IndexOf(':');
                    if (colonIdx > 0)
                    {
                        var key = trimmed.Substring(0, colonIdx).Trim();
                        var val = trimmed.Substring(colonIdx + 1).Trim();
                        styleDict[key] = val;
                    }
                }
            }

            // Merge new props
            foreach (var prop in newProps.Properties())
                styleDict[prop.Name] = prop.Value?.ToString() ?? "";

            return string.Join(" ", styleDict.Select(kv => $"{kv.Key}: {kv.Value};"));
        }

        private static string BuildUssRule(string selector, JObject rules)
        {
            var props = rules.Properties()
                .Select(p => $"    {p.Name}: {p.Value};")
                .ToList();
            return $"{selector} {{\n{string.Join("\n", props)}\n}}";
        }

        private static int FindSelectorInsertPoint(string ussContent, string selector)
        {
            // Find the closing brace of the selector block
            var selectorIdx = ussContent.IndexOf(selector, StringComparison.Ordinal);
            if (selectorIdx < 0) return -1;

            var openBrace = ussContent.IndexOf('{', selectorIdx);
            if (openBrace < 0) return -1;

            var closeBrace = ussContent.IndexOf('}', openBrace);
            if (closeBrace < 0) return -1;

            return closeBrace; // Insert before closing brace
        }

        private static string GenerateEditorWindowTemplate(string className, string ns, string uxmlPath)
        {
            return $@"using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace {ns}
{{
    /// <summary>
    /// {className} — Unity Editor Window using UI Toolkit.
    /// </summary>
    public class {className} : EditorWindow
    {{
        [MenuItem(""Window/{className}"")]
        public static void ShowWindow()
        {{
            var window = GetWindow<{className}>();
            window.titleContent = new GUIContent(""{className}"");
            window.minSize = new Vector2(400, 300);
            window.Show();
        }}

        private void CreateGUI()
        {{
            // Load UXML
            var visualTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(""{uxmlPath}"");
            if (visualTree != null)
            {{
                visualTree.CloneTree(rootVisualElement);
            }}
            else
            {{
                // Fallback: build UI in code
                var root = rootVisualElement;
                root.style.padding = new StyleEdgeInsets(new StyleLength(8));

                var title = new Label(""{className}"");
                title.style.fontSize = 18;
                title.style.unityFontStyleAndWeight = FontStyle.Bold;
                root.Add(title);
            }}

            // Register callbacks
            RegisterCallbacks();
        }}

        private void RegisterCallbacks()
        {{
            // TODO: Register button callbacks, value change events, etc.
            // Example:
            // var myButton = rootVisualElement.Q<Button>(""my-button"");
            // myButton?.RegisterCallback<ClickEvent>(evt => OnMyButtonClicked());
        }}
    }}
}}
";
        }

        private static string GenerateEditorWindowUxml(string className)
        {
            return $@"<?xml version=""1.0"" encoding=""utf-8""?>
<ui:UXML
    xmlns:ui=""UnityEngine.UIElements""
    xmlns:uie=""UnityEditor.UIElements""
    editor-extension-mode=""True"">
    <ui:VisualElement name=""root"" style=""flex-grow: 1; padding: 8px;"">
        <ui:Label name=""title"" text=""{className}"" style=""font-size: 18px; -unity-font-style: bold; margin-bottom: 8px;"" />
        <ui:VisualElement name=""content"" style=""flex-grow: 1;"" />
        <ui:VisualElement name=""footer"" style=""flex-direction: row; justify-content: flex-end; margin-top: 8px;"">
            <ui:Button name=""ok-button"" text=""OK"" style=""width: 80px;"" />
            <ui:Button name=""cancel-button"" text=""Cancel"" style=""width: 80px; margin-left: 4px;"" />
        </ui:VisualElement>
    </ui:VisualElement>
</ui:UXML>";
        }

        private static string GenerateCustomElementTemplate(string className, string ns)
        {
            return $@"using UnityEngine;
using UnityEngine.UIElements;

namespace {ns}
{{
    /// <summary>
    /// {className} — Custom UI Toolkit VisualElement.
    /// Register with [UxmlElement] attribute for use in UXML.
    /// </summary>
    [UxmlElement]
    public partial class {className} : VisualElement
    {{
        // UxmlTraits for UXML attribute support (Unity 2022 style)
        public new class UxmlFactory : UxmlFactory<{className}, UxmlTraits> {{ }}

        public new class UxmlTraits : VisualElement.UxmlTraits
        {{
            // Define UXML attributes here
            // Example: UxmlStringAttributeDescription _label = new UxmlStringAttributeDescription {{ name = ""label"", defaultValue = """" }};

            public override void Init(VisualElement ve, IUxmlAttributes bag, CreationContext cc)
            {{
                base.Init(ve, bag, cc);
                var element = ({className})ve;
                // Initialize from UXML attributes
                // element.Label = _label.GetValueFromBag(bag, cc);
            }}
        }}

        public {className}()
        {{
            // Add USS class for styling
            AddToClassList(""{ToKebabCase(className)}"");

            // Build element hierarchy
            BuildUI();
        }}

        private void BuildUI()
        {{
            // TODO: Build your element hierarchy here
            var label = new Label(""Hello from {className}"");
            Add(label);
        }}
    }}
}}
";
        }

        private static string ToKebabCase(string pascalCase)
        {
            if (string.IsNullOrEmpty(pascalCase)) return pascalCase;
            var result = System.Text.RegularExpressions.Regex.Replace(
                pascalCase, @"(?<!^)([A-Z])", "-$1").ToLower();
            return result;
        }
    }
}
