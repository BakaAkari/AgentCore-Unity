using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Tools.Infrastructure;
using AgentCore.Editor.Tools.Safety;
using AgentCore.Editor.Utils;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Tools.Native.Scripting
{
    /// <summary>
    /// Manage C# scripts — read, write, create from template, delete, list, get info, analyze, find references, and add methods/fields.
    /// Directly calls System.IO and AssetDatabase APIs.
    /// </summary>
    [AgentTool("manage_script",
        Description = "Read, write, create, and delete C# script files (.cs). Also provides code analysis capabilities. " +
            "Actions: read (get file content), write (overwrite entire file or specific section), create (from template or content), " +
            "delete, list (find scripts by path/pattern), get_info (class structure), analyze (API usage/dependencies), " +
            "find_references (where a type/method is used), add_method/add_field (inject code into existing class). " +
            "TRIGGERS DOMAIN RELOAD: write/create/delete on .cs files causes Unity to recompile. Wait for compilation before continuing. " +
            "Use for: all C# source file operations. NOT for: non-C# files (use manage_file), runtime code evaluation (use execute_code), " +
            "symbol search across codebase (use search_code when available).",
        Category = "Scripting",
        RequiresMainThread = true,
        MayModifyScripts = true,
        RiskLevel = ToolRiskLevel.High,
        Capabilities = ToolCapability.ModifyScripts | ToolCapability.WriteProjectFiles | ToolCapability.DeleteProjectFiles,
        ReadOnlyActions = new[] { "analyze", "find_references", "get_info", "list", "read", "search" },
        // v1.12+ ModifyRuntimeState: 所有 .cs 源码 write action 在 Play Mode 中硬禁止。
        // 理由:修改源码需 Domain Reload 才生效,而 Domain Reload 会退出 Play Mode —— 运行时改代码毫无意义。
        // Agent 若需运行时验证逻辑,应改用 execute_code (运行时 REPL) 或直接改运行时对象字段。
        PlaymodeHardBlockedActions = new[] { "write", "create", "delete", "add_method", "add_field" })]
    public class ManageScriptTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""read"", ""write"", ""create"", ""delete"", ""list"", ""get_info"", ""analyze"", ""find_references"", ""add_method"", ""add_field"", ""search""],
                    ""description"": ""Action to perform""
                },
                ""path"": {
                    ""type"": ""string"",
                    ""description"": ""Script file path relative to Assets/ (e.g., 'Assets/Scripts/MyScript.cs')""
                },
                ""content"": {
                    ""type"": ""string"",
                    ""description"": ""Script content for write action""
                },
                ""create_directory"": {
                    ""type"": ""boolean"",
                    ""description"": ""Whether to create parent directories if they don't exist (default: true)""
                },
                ""class_name"": {
                    ""type"": ""string"",
                    ""description"": ""Class name for create action (default: inferred from filename)""
                },
                ""template"": {
                    ""type"": ""string"",
                    ""enum"": [""monobehaviour"", ""scriptableobject"", ""editor"", ""editorwindow"", ""custom_editor"", ""plain""],
                    ""description"": ""Script template type for create action (default: 'monobehaviour')""
                },
                ""namespace"": {
                    ""type"": ""string"",
                    ""description"": ""Namespace for create action (optional)""
                },
                ""directory"": {
                    ""type"": ""string"",
                    ""description"": ""Directory to list scripts in (default: 'Assets')""
                },
                ""recursive"": {
                    ""type"": ""boolean"",
                    ""description"": ""Whether to search recursively for list action (default: true)""
                },
                ""pattern"": {
                    ""type"": ""string"",
                    ""description"": ""File pattern for list action (default: '*.cs')""
                },
                ""method_name"": {
                    ""type"": ""string"",
                    ""description"": ""Method name for add_method action""
                },
                ""return_type"": {
                    ""type"": ""string"",
                    ""description"": ""Return type for add_method action (default: 'void')""
                },
                ""parameters"": {
                    ""type"": ""string"",
                    ""description"": ""Parameter list for add_method action (e.g., 'int count, string name')""
                },
                ""body"": {
                    ""type"": ""string"",
                    ""description"": ""Method body for add_method action (without braces)""
                },
                ""access"": {
                    ""type"": ""string"",
                    ""enum"": [""public"", ""private"", ""protected"", ""internal""],
                    ""description"": ""Access modifier for add_method/add_field (default: 'public')""
                },
                ""field_name"": {
                    ""type"": ""string"",
                    ""description"": ""Field name for add_field action""
                },
                ""field_type"": {
                    ""type"": ""string"",
                    ""description"": ""Field type for add_field action (e.g., 'int', 'string', 'GameObject')""
                },
                ""default_value"": {
                    ""type"": ""string"",
                    ""description"": ""Default value for add_field action (optional)""
                },
                ""serialized"": {
                    ""type"": ""boolean"",
                    ""description"": ""Whether to add [SerializeField] attribute for add_field (default: true)""
                },
                ""query"": {
                    ""type"": ""string"",
                    ""description"": ""Search query for search action""
                },
                ""case_sensitive"": {
                    ""type"": ""boolean"",
                    ""description"": ""Whether search is case-sensitive (default: false)""
                },
                ""use_regex"": {
                    ""type"": ""boolean"",
                    ""description"": ""Whether to use regex pattern matching (default: false)""
                },
                ""context_lines"": {
                    ""type"": ""integer"",
                    ""description"": ""Number of context lines to show around matches (default: 2)""
                }
            },
            ""required"": [""action""]
        }");

        public ToolMetadata Metadata => new ToolMetadata(
            name: "manage_script",
            description: "Manage C# scripts — read, write, create, delete, list, get info, analyze API, find references, add methods/fields, search content",
            category: "Scripting",
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
                    case "read":
                        response = HandleRead(parameters);
                        break;
                    case "write":
                        response = HandleWrite(parameters);
                        break;
                    case "create":
                        response = HandleCreate(parameters);
                        break;
                    case "delete":
                        response = HandleDelete(parameters);
                        break;
                    case "list":
                        response = HandleList(parameters);
                        break;
                    case "get_info":
                        response = HandleGetInfo(parameters);
                        break;
                    case "analyze":
                        response = HandleAnalyze(parameters);
                        break;
                    case "find_references":
                        response = HandleFindReferences(parameters);
                        break;
                    case "add_method":
                        response = HandleAddMethod(parameters);
                        break;
                    case "add_field":
                        response = HandleAddField(parameters);
                        break;
                    case "search":
                        response = HandleSearch(parameters);
                        break;
                    default:
                        response = ToolResponse.Fail(
                            $"Unknown action: '{action}'. Valid actions: read, write, create, delete, list, get_info, analyze, find_references, add_method, add_field, search");
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
            var result = response.ToToolResult(sw.Elapsed.TotalMilliseconds);

            // Mark compile-related for actions that modify script files
            if (result.Success)
            {
                var action = ToolHelpers.GetOptionalString(parameters, "action", "");
                if (action == "write" || action == "create" || action == "delete" ||
                    action == "add_method" || action == "add_field")
                {
                    result.IsCompileRelated = true;
                }
            }

            return Task.FromResult(result);
        }

        #region Action Handlers

        private ToolResponse HandleRead(JObject parameters)
        {
            var path = ToolHelpers.GetRequiredString(parameters, "path");
            path = ToolHelpers.NormalizeAssetPath(path);

            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
                return ToolResponse.Fail($"Script file not found: {path}");

            var content = File.ReadAllText(fullPath);
            return ToolResponse.OkWithData(new JObject
            {
                ["path"] = path,
                ["content"] = content,
                ["lineCount"] = content.Split('\n').Length
            }, $"Read script: {path}");
        }

        private ToolResponse HandleWrite(JObject parameters)
        {
            var path = ToolHelpers.GetRequiredString(parameters, "path");
            var content = ToolHelpers.GetRequiredString(parameters, "content");
            var createDirectory = ToolHelpers.GetOptionalBool(parameters, "create_directory", true);

            path = ToolHelpers.NormalizeAssetPath(path);
            var fullPath = Path.GetFullPath(path);

            if (createDirectory)
            {
                ToolHelpers.EnsureDirectoryExists(fullPath);
            }

            var isNew = !File.Exists(fullPath);
            File.WriteAllText(fullPath, content);

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            AssetDatabase.Refresh();

            return ToolResponse.OkWithData(new JObject
            {
                ["path"] = path,
                ["isNew"] = isNew,
                ["bytes"] = content.Length
            }, isNew ? $"Created script: {path}" : $"Updated script: {path}");
        }

        private ToolResponse HandleCreate(JObject parameters)
        {
            var path = ToolHelpers.GetRequiredString(parameters, "path");
            path = ToolHelpers.NormalizeAssetPath(path);

            // Ensure .cs extension
            if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                path += ".cs";

            var fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath))
                return ToolResponse.Fail($"Script already exists: {path}. Use 'write' action to overwrite.");

            // Infer class name from filename
            var fileName = Path.GetFileNameWithoutExtension(path);
            var className = ToolHelpers.GetOptionalString(parameters, "class_name", fileName);
            var template = ToolHelpers.GetOptionalString(parameters, "template", "monobehaviour").ToLowerInvariant();
            var namespaceName = ToolHelpers.GetOptionalString(parameters, "namespace");

            var content = GenerateTemplate(className, template, namespaceName);
            if (content == null)
                return ToolResponse.Fail(
                    $"Unknown template: '{template}'. Valid templates: monobehaviour, scriptableobject, editor, editorwindow, custom_editor, plain");

            ToolHelpers.EnsureDirectoryExists(fullPath);
            File.WriteAllText(fullPath, content);

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            AssetDatabase.Refresh();

            return ToolResponse.OkWithData(new JObject
            {
                ["path"] = path,
                ["className"] = className,
                ["template"] = template
            }, $"Created script from '{template}' template: {path}");
        }

        private ToolResponse HandleDelete(JObject parameters)
        {
            var path = ToolHelpers.GetRequiredString(parameters, "path");
            path = ToolHelpers.NormalizeAssetPath(path);

            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
                return ToolResponse.Fail($"Script file not found: {path}");

            var success = AssetDatabase.DeleteAsset(path);
            if (!success)
            {
                // Fallback to direct file deletion
                File.Delete(fullPath);
                AssetDatabase.Refresh();
            }

            return ToolResponse.Ok($"Deleted script: {path}");
        }

        private ToolResponse HandleList(JObject parameters)
        {
            var directory = ToolHelpers.GetOptionalString(parameters, "directory", "Assets");
            var recursive = ToolHelpers.GetOptionalBool(parameters, "recursive", true);
            var pattern = ToolHelpers.GetOptionalString(parameters, "pattern", "*.cs");

            directory = ToolHelpers.NormalizeAssetPath(directory);
            var fullDir = Path.GetFullPath(directory);

            if (!Directory.Exists(fullDir))
                return ToolResponse.Fail($"Directory not found: {directory}");

            var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var files = Directory.GetFiles(fullDir, pattern, searchOption);

            var results = new JArray();
            foreach (var file in files)
            {
                // Convert to asset-relative path
                var relativePath = file.Replace("\\", "/");
                var assetsIndex = relativePath.IndexOf("Assets/", StringComparison.OrdinalIgnoreCase);
                if (assetsIndex >= 0)
                    relativePath = relativePath.Substring(assetsIndex);

                results.Add(new JObject
                {
                    ["path"] = relativePath,
                    ["name"] = Path.GetFileName(file),
                    ["size"] = new FileInfo(file).Length
                });
            }

            return ToolResponse.OkWithData(new JObject
            {
                ["directory"] = directory,
                ["count"] = results.Count,
                ["files"] = results
            }, $"Found {results.Count} script(s) in {directory}");
        }

        private ToolResponse HandleGetInfo(JObject parameters)
        {
            var path = ToolHelpers.GetRequiredString(parameters, "path");
            path = ToolHelpers.NormalizeAssetPath(path);

            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
                return ToolResponse.Fail($"Script file not found: {path}");

            var fileInfo = new FileInfo(fullPath);
            var content = File.ReadAllText(fullPath);

            var info = new JObject
            {
                ["path"] = path,
                ["fileName"] = fileInfo.Name,
                ["size"] = fileInfo.Length,
                ["lastModified"] = fileInfo.LastWriteTimeUtc.ToString("o"),
                ["lineCount"] = content.Split('\n').Length
            };

            // Detect script type from content
            if (content.Contains(": MonoBehaviour"))
                info["scriptType"] = "MonoBehaviour";
            else if (content.Contains(": ScriptableObject"))
                info["scriptType"] = "ScriptableObject";
            else if (content.Contains(": Editor"))
                info["scriptType"] = "Editor";
            else if (content.Contains(": EditorWindow"))
                info["scriptType"] = "EditorWindow";
            else
                info["scriptType"] = "Plain";

            // Detect namespace
            var nsLine = content.Split('\n')
                .FirstOrDefault(l => l.TrimStart().StartsWith("namespace "));
            if (nsLine != null)
            {
                var ns = nsLine.Trim().Replace("namespace ", "").Replace("{", "").Trim();
                info["namespace"] = ns;
            }

            // Check if it's an asset in the database
            var asset = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
            if (asset != null)
            {
                info["assetGuid"] = AssetDatabase.AssetPathToGUID(path);
                var scriptClass = asset.GetClass();
                if (scriptClass != null)
                {
                    info["className"] = scriptClass.Name;
                    info["fullClassName"] = scriptClass.FullName;
                }
            }

            return ToolResponse.OkWithData(info, $"Script info: {path}");
        }

        /// <summary>
        /// Analyzes a script's public API — classes, base types, public methods, public fields, and attributes.
        /// Uses regex-based parsing on the source file content.
        /// </summary>
        private ToolResponse HandleAnalyze(JObject parameters)
        {
            var path = ToolHelpers.GetRequiredString(parameters, "path");
            path = ToolHelpers.NormalizeAssetPath(path);

            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
                return ToolResponse.Fail($"Script file not found: {path}");

            var content = File.ReadAllText(fullPath);
            var result = new JObject { ["path"] = path };

            // Detect namespace
            var nsMatch = Regex.Match(content, @"namespace\s+([\w.]+)");
            if (nsMatch.Success)
                result["namespace"] = nsMatch.Groups[1].Value;

            // Detect using statements
            var usings = new JArray();
            foreach (Match m in Regex.Matches(content, @"^\s*using\s+([\w.]+)\s*;", RegexOptions.Multiline))
            {
                usings.Add(m.Groups[1].Value);
            }
            result["usings"] = usings;

            // Detect classes/structs/interfaces
            var typePattern = @"\b(public|internal|private|protected)?\s*(abstract|sealed|static|partial)?\s*(class|struct|interface|enum)\s+(\w+)(?:\s*<[^>]+>)?(?:\s*:\s*([^\{]+))?";
            var types = new JArray();
            foreach (Match m in Regex.Matches(content, typePattern))
            {
                var typeInfo = new JObject
                {
                    ["access"] = string.IsNullOrWhiteSpace(m.Groups[1].Value) ? "internal" : m.Groups[1].Value.Trim(),
                    ["modifier"] = m.Groups[2].Value.Trim(),
                    ["kind"] = m.Groups[3].Value.Trim(),
                    ["name"] = m.Groups[4].Value.Trim()
                };

                if (m.Groups[5].Success && !string.IsNullOrWhiteSpace(m.Groups[5].Value))
                {
                    var baseTypes = m.Groups[5].Value.Trim().TrimEnd('{').Trim();
                    typeInfo["baseTypes"] = baseTypes;
                }

                types.Add(typeInfo);
            }
            result["types"] = types;

            // Detect public methods
            var methodPattern = @"^\s*(public|protected)\s+(?:(static|virtual|override|abstract|async)\s+)*(?:([\w<>\[\],\s]+?)\s+)(\w+)\s*\(([^)]*)\)";
            var methods = new JArray();
            foreach (Match m in Regex.Matches(content, methodPattern, RegexOptions.Multiline))
            {
                var methodInfo = new JObject
                {
                    ["access"] = m.Groups[1].Value.Trim(),
                    ["modifier"] = m.Groups[2].Value.Trim(),
                    ["returnType"] = m.Groups[3].Value.Trim(),
                    ["name"] = m.Groups[4].Value.Trim(),
                    ["parameters"] = m.Groups[5].Value.Trim()
                };
                methods.Add(methodInfo);
            }
            result["publicMethods"] = methods;

            // Detect public fields and properties
            var fieldPattern = @"^\s*(?:\[([^\]]+)\]\s*)*\s*(public|protected)\s+(?:(static|readonly|const)\s+)*([\w<>\[\],\s]+?)\s+(\w+)\s*(?:=\s*([^;]+))?\s*;";
            var fields = new JArray();
            foreach (Match m in Regex.Matches(content, fieldPattern, RegexOptions.Multiline))
            {
                var fieldInfo = new JObject
                {
                    ["access"] = m.Groups[2].Value.Trim(),
                    ["modifier"] = m.Groups[3].Value.Trim(),
                    ["type"] = m.Groups[4].Value.Trim(),
                    ["name"] = m.Groups[5].Value.Trim()
                };

                if (!string.IsNullOrWhiteSpace(m.Groups[1].Value))
                    fieldInfo["attributes"] = m.Groups[1].Value.Trim();
                if (m.Groups[6].Success && !string.IsNullOrWhiteSpace(m.Groups[6].Value))
                    fieldInfo["defaultValue"] = m.Groups[6].Value.Trim();

                fields.Add(fieldInfo);
            }
            result["publicFields"] = fields;

            // Detect properties (auto-properties and with getters/setters)
            var propPattern = @"^\s*(public|protected)\s+(?:(static|virtual|override|abstract)\s+)*([\w<>\[\],\s]+?)\s+(\w+)\s*\{";
            var properties = new JArray();
            foreach (Match m in Regex.Matches(content, propPattern, RegexOptions.Multiline))
            {
                // Skip methods (they have parentheses before braces)
                var propName = m.Groups[4].Value.Trim();
                if (propName == "get" || propName == "set" || propName == "value") continue;

                properties.Add(new JObject
                {
                    ["access"] = m.Groups[1].Value.Trim(),
                    ["modifier"] = m.Groups[2].Value.Trim(),
                    ["type"] = m.Groups[3].Value.Trim(),
                    ["name"] = propName
                });
            }
            result["publicProperties"] = properties;

            // Detect class-level attributes
            var classAttrPattern = @"^\s*\[([^\]]+)\]\s*$";
            var attributes = new JArray();
            foreach (Match m in Regex.Matches(content, classAttrPattern, RegexOptions.Multiline))
            {
                var attr = m.Groups[1].Value.Trim();
                // Only include if the next non-empty line is a class/struct declaration
                attributes.Add(attr);
            }
            result["attributes"] = attributes;

            result["lineCount"] = content.Split('\n').Length;

            return ToolResponse.OkWithData(result, $"Analyzed script: {path}");
        }

        /// <summary>
        /// Finds all GameObjects in the current scene that reference a given script.
        /// Searches all components on all GameObjects for the script type.
        /// </summary>
        private ToolResponse HandleFindReferences(JObject parameters)
        {
            var path = ToolHelpers.GetRequiredString(parameters, "path");
            path = ToolHelpers.NormalizeAssetPath(path);

            var monoScript = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
            if (monoScript == null)
                return ToolResponse.Fail($"Script not found at path: {path}");

            var scriptType = monoScript.GetClass();
            if (scriptType == null)
                return ToolResponse.Fail($"Could not resolve class type for script: {path}. The script may have compilation errors.");

            var references = new JArray();

            // Search all root GameObjects in all loaded scenes
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;

                foreach (var rootGo in scene.GetRootGameObjects())
                {
                    SearchGameObjectForScript(rootGo, scriptType, references, scene.name);
                }
            }

            return ToolResponse.OkWithData(new JObject
            {
                ["scriptPath"] = path,
                ["scriptClass"] = scriptType.FullName,
                ["references"] = references,
                ["referenceCount"] = references.Count
            }, $"Found {references.Count} reference(s) to '{scriptType.Name}' in loaded scenes.");
        }

        /// <summary>
        /// Adds a method to an existing script file.
        /// Inserts the method before the last closing brace of the class.
        /// </summary>
        private ToolResponse HandleAddMethod(JObject parameters)
        {
            var path = ToolHelpers.GetRequiredString(parameters, "path");
            path = ToolHelpers.NormalizeAssetPath(path);

            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
                return ToolResponse.Fail($"Script file not found: {path}");

            var methodName = ToolHelpers.GetRequiredString(parameters, "method_name");
            var returnType = ToolHelpers.GetOptionalString(parameters, "return_type", "void");
            var methodParams = ToolHelpers.GetOptionalString(parameters, "parameters", "");
            var body = ToolHelpers.GetOptionalString(parameters, "body", "");
            var access = ToolHelpers.GetOptionalString(parameters, "access", "public");

            var content = File.ReadAllText(fullPath);

            // Check if method already exists
            var methodExistsPattern = $@"\b{Regex.Escape(methodName)}\s*\(";
            if (Regex.IsMatch(content, methodExistsPattern))
                return ToolResponse.Fail($"Method '{methodName}' already exists in '{path}'.");

            // Build the method string
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine($"        {access} {returnType} {methodName}({methodParams})");
            sb.AppendLine("        {");
            if (!string.IsNullOrWhiteSpace(body))
            {
                // Indent each line of the body
                foreach (var line in body.Split('\n'))
                {
                    sb.AppendLine($"            {line.TrimEnd('\r')}");
                }
            }
            else
            {
                sb.AppendLine("            ");
            }
            sb.AppendLine("        }");

            // Find the last closing brace of the class (second-to-last '}' in the file)
            var insertIndex = FindClassClosingBraceIndex(content);
            if (insertIndex < 0)
                return ToolResponse.Fail("Could not find class closing brace in script. Ensure the file contains a valid class definition.");

            var newContent = content.Substring(0, insertIndex) + sb.ToString() + content.Substring(insertIndex);

            File.WriteAllText(fullPath, newContent);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            return ToolResponse.OkWithData(new JObject
            {
                ["path"] = path,
                ["methodName"] = methodName,
                ["returnType"] = returnType,
                ["access"] = access
            }, $"Added method '{access} {returnType} {methodName}({methodParams})' to '{path}'.");
        }

        /// <summary>
        /// Adds a field to an existing script file.
        /// Inserts the field at the beginning of the class body (after the opening brace).
        /// </summary>
        private ToolResponse HandleAddField(JObject parameters)
        {
            var path = ToolHelpers.GetRequiredString(parameters, "path");
            path = ToolHelpers.NormalizeAssetPath(path);

            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
                return ToolResponse.Fail($"Script file not found: {path}");

            var fieldName = ToolHelpers.GetRequiredString(parameters, "field_name");
            var fieldType = ToolHelpers.GetRequiredString(parameters, "field_type");
            var defaultValue = ToolHelpers.GetOptionalString(parameters, "default_value");
            var serialized = ToolHelpers.GetOptionalBool(parameters, "serialized", true);
            var access = ToolHelpers.GetOptionalString(parameters, "access", "public");

            var content = File.ReadAllText(fullPath);

            // Check if field already exists
            var fieldExistsPattern = $@"\b{Regex.Escape(fieldName)}\s*[;=]";
            if (Regex.IsMatch(content, fieldExistsPattern))
                return ToolResponse.Fail($"Field '{fieldName}' already exists in '{path}'.");

            // Build the field string
            var sb = new StringBuilder();

            // Add [SerializeField] attribute for non-public fields, or [SerializeField] if requested
            if (serialized && access != "public")
            {
                sb.AppendLine("        [SerializeField]");
            }
            else if (serialized && access == "public")
            {
                // Public fields are serialized by default in Unity, no attribute needed
            }

            sb.Append($"        {access} {fieldType} {fieldName}");
            if (!string.IsNullOrWhiteSpace(defaultValue))
            {
                sb.Append($" = {defaultValue}");
            }
            sb.AppendLine(";");

            // Find the class opening brace and insert after it
            var insertIndex = FindClassOpeningBraceIndex(content);
            if (insertIndex < 0)
                return ToolResponse.Fail("Could not find class opening brace in script. Ensure the file contains a valid class definition.");

            // Insert after the opening brace (and newline)
            var afterBrace = insertIndex + 1;
            // Skip any newline after the brace
            if (afterBrace < content.Length && content[afterBrace] == '\r') afterBrace++;
            if (afterBrace < content.Length && content[afterBrace] == '\n') afterBrace++;

            var newContent = content.Substring(0, afterBrace) + sb.ToString() + content.Substring(afterBrace);

            File.WriteAllText(fullPath, newContent);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            return ToolResponse.OkWithData(new JObject
            {
                ["path"] = path,
                ["fieldName"] = fieldName,
                ["fieldType"] = fieldType,
                ["access"] = access,
                ["serialized"] = serialized
            }, $"Added field '{access} {fieldType} {fieldName}' to '{path}'.");
        }

        /// <summary>
        /// Searches for content in script files.
        /// Supports case-sensitive/insensitive search and regex patterns.
        /// </summary>
        private ToolResponse HandleSearch(JObject parameters)
        {
            var query = ToolHelpers.GetRequiredString(parameters, "query");
            var directory = ToolHelpers.GetOptionalString(parameters, "directory", "Assets");
            var recursive = ToolHelpers.GetOptionalBool(parameters, "recursive", true);
            var caseSensitive = ToolHelpers.GetOptionalBool(parameters, "case_sensitive", false);
            var useRegex = ToolHelpers.GetOptionalBool(parameters, "use_regex", false);
            var contextLines = ToolHelpers.GetOptionalInt(parameters, "context_lines", 2);

            directory = ToolHelpers.NormalizeAssetPath(directory);
            var fullDir = Path.GetFullPath(directory);

            if (!Directory.Exists(fullDir))
                return ToolResponse.Fail($"Directory not found: {directory}");

            var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var files = Directory.GetFiles(fullDir, "*.cs", searchOption);

            var results = new JArray();
            var totalMatches = 0;

            foreach (var file in files)
            {
                try
                {
                    var content = File.ReadAllText(file);
                    var lines = content.Split('\n');

                    // Convert to asset-relative path
                    var relativePath = file.Replace("\\", "/");
                    var assetsIndex = relativePath.IndexOf("Assets/", StringComparison.OrdinalIgnoreCase);
                    if (assetsIndex >= 0)
                        relativePath = relativePath.Substring(assetsIndex);

                    var fileMatches = new JArray();

                    if (useRegex)
                    {
                        // Regex search
                        var regexOptions = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                        Regex regex;
                        try
                        {
                            regex = new Regex(query, regexOptions);
                        }
                        catch (ArgumentException ex)
                        {
                            return ToolResponse.Fail($"Invalid regex pattern: {ex.Message}");
                        }

                        for (int i = 0; i < lines.Length; i++)
                        {
                            if (regex.IsMatch(lines[i]))
                            {
                                fileMatches.Add(CreateMatchResult(lines, i, contextLines));
                                totalMatches++;
                            }
                        }
                    }
                    else
                    {
                        // Plain text search
                        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                        for (int i = 0; i < lines.Length; i++)
                        {
                            if (lines[i].IndexOf(query, comparison) >= 0)
                            {
                                fileMatches.Add(CreateMatchResult(lines, i, contextLines));
                                totalMatches++;
                            }
                        }
                    }

                    if (fileMatches.Count > 0)
                    {
                        results.Add(new JObject
                        {
                            ["path"] = relativePath,
                            ["matchCount"] = fileMatches.Count,
                            ["matches"] = fileMatches
                        });
                    }
                }
                catch (Exception ex)
                {
                    // Skip files that can't be read
                    AgentCoreLog.Warning($"Could not search file {file}: {ex.Message}");
                }
            }

            return ToolResponse.OkWithData(new JObject
            {
                ["query"] = query,
                ["directory"] = directory,
                ["caseSensitive"] = caseSensitive,
                ["useRegex"] = useRegex,
                ["filesSearched"] = files.Length,
                ["filesWithMatches"] = results.Count,
                ["totalMatches"] = totalMatches,
                ["results"] = results
            }, $"Found {totalMatches} match(es) in {results.Count} file(s)");
        }

        /// <summary>
        /// Creates a match result with context lines.
        /// </summary>
        private static JObject CreateMatchResult(string[] lines, int matchLineIndex, int contextLines)
        {
            var startLine = Math.Max(0, matchLineIndex - contextLines);
            var endLine = Math.Min(lines.Length - 1, matchLineIndex + contextLines);

            var contextArray = new JArray();
            for (int i = startLine; i <= endLine; i++)
            {
                contextArray.Add(new JObject
                {
                    ["lineNumber"] = i + 1,
                    ["content"] = lines[i].TrimEnd('\r'),
                    ["isMatch"] = i == matchLineIndex
                });
            }

            return new JObject
            {
                ["lineNumber"] = matchLineIndex + 1,
                ["line"] = lines[matchLineIndex].TrimEnd('\r'),
                ["context"] = contextArray
            };
        }

        #endregion

        #region Script Analysis Helpers

        /// <summary>
        /// Recursively searches a GameObject hierarchy for components of the specified type.
        /// </summary>
        private static void SearchGameObjectForScript(GameObject go, System.Type scriptType, JArray results, string sceneName)
        {
            var components = go.GetComponents<Component>();
            foreach (var comp in components)
            {
                if (comp == null) continue;
                if (comp.GetType() == scriptType || comp.GetType().IsSubclassOf(scriptType))
                {
                    results.Add(new JObject
                    {
                        ["gameObject"] = go.name,
                        ["path"] = GetGameObjectPath(go),
                        ["scene"] = sceneName,
                        ["componentType"] = comp.GetType().Name,
                        ["instanceId"] = go.GetInstanceID()
                    });
                }
            }

            // Recurse into children
            for (int i = 0; i < go.transform.childCount; i++)
            {
                SearchGameObjectForScript(go.transform.GetChild(i).gameObject, scriptType, results, sceneName);
            }
        }

        /// <summary>
        /// Gets the full hierarchy path of a GameObject.
        /// </summary>
        private static string GetGameObjectPath(GameObject go)
        {
            var path = go.name;
            var parent = go.transform.parent;
            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }

        /// <summary>
        /// Finds the index of the last class closing brace in the content.
        /// This is the second-to-last '}' (last one is namespace closing if present, or the class itself).
        /// </summary>
        private static int FindClassClosingBraceIndex(string content)
        {
            // Find all closing braces and their positions
            var bracePositions = new List<int>();
            for (int i = 0; i < content.Length; i++)
            {
                if (content[i] == '}')
                    bracePositions.Add(i);
            }

            if (bracePositions.Count < 1)
                return -1;

            // Check if there's a namespace — if so, the class closing brace is second-to-last
            bool hasNamespace = Regex.IsMatch(content, @"^\s*namespace\s+", RegexOptions.Multiline);

            if (hasNamespace && bracePositions.Count >= 2)
                return bracePositions[bracePositions.Count - 2];
            else
                return bracePositions[bracePositions.Count - 1];
        }

        /// <summary>
        /// Finds the index of the first class/struct/interface opening brace.
        /// </summary>
        private static int FindClassOpeningBraceIndex(string content)
        {
            // Match class/struct/interface declaration and find its opening brace
            var classMatch = Regex.Match(content, @"\b(class|struct|interface)\s+\w+[^{]*\{");
            if (!classMatch.Success)
                return -1;

            return classMatch.Index + classMatch.Length - 1;
        }

        #endregion

        #region Template Generation

        private string GenerateTemplate(string className, string template, string namespaceName)
        {
            string body;

            switch (template)
            {
                case "monobehaviour":
                    body = GenerateMonoBehaviour(className);
                    break;
                case "scriptableobject":
                    body = GenerateScriptableObject(className);
                    break;
                case "editor":
                    body = GenerateEditor(className);
                    break;
                case "editorwindow":
                    body = GenerateEditorWindow(className);
                    break;
                case "custom_editor":
                    body = GenerateCustomEditor(className);
                    break;
                case "plain":
                    body = GeneratePlain(className);
                    break;
                default:
                    return null;
            }

            if (!string.IsNullOrEmpty(namespaceName))
            {
                body = WrapInNamespace(body, namespaceName);
            }

            return body;
        }

        private string GenerateMonoBehaviour(string className)
        {
            return $@"using UnityEngine;

public class {className} : MonoBehaviour
{{
    void Start()
    {{
        
    }}

    void Update()
    {{
        
    }}
}}
";
        }

        private string GenerateScriptableObject(string className)
        {
            return $@"using UnityEngine;

[CreateAssetMenu(fileName = ""{className}"", menuName = ""ScriptableObjects/{className}"")]
public class {className} : ScriptableObject
{{
    
}}
";
        }

        private string GenerateEditor(string className)
        {
            return $@"using UnityEngine;
using UnityEditor;

[CustomEditor(typeof(MonoBehaviour))]
public class {className} : Editor
{{
    public override void OnInspectorGUI()
    {{
        base.OnInspectorGUI();
    }}
}}
";
        }

        private string GenerateEditorWindow(string className)
        {
            return $@"using UnityEngine;
using UnityEditor;

public class {className} : EditorWindow
{{
    [MenuItem(""Window/{className}"")]
    public static void ShowWindow()
    {{
        GetWindow<{className}>(""{className}"");
    }}

    private void OnGUI()
    {{
        
    }}
}}
";
        }

        private string GenerateCustomEditor(string className)
        {
            // Remove "Editor" suffix to get target class name
            var targetClass = className.EndsWith("Editor")
                ? className.Substring(0, className.Length - 6)
                : className + "Target";

            return $@"using UnityEngine;
using UnityEditor;

[CustomEditor(typeof({targetClass}))]
public class {className} : Editor
{{
    public override void OnInspectorGUI()
    {{
        serializedObject.Update();

        // Draw default inspector
        DrawDefaultInspector();

        serializedObject.ApplyModifiedProperties();
    }}
}}
";
        }

        private string GeneratePlain(string className)
        {
            return $@"using System;

public class {className}
{{
    
}}
";
        }

        private string WrapInNamespace(string content, string namespaceName)
        {
            // Find the first class/struct/interface declaration and wrap everything after usings
            var lines = content.Split('\n').ToList();
            var usingLines = new List<string>();
            var bodyLines = new List<string>();
            var pastUsings = false;

            foreach (var line in lines)
            {
                var trimmed = line.TrimStart();
                if (!pastUsings && (trimmed.StartsWith("using ") || string.IsNullOrWhiteSpace(trimmed)))
                {
                    usingLines.Add(line);
                }
                else
                {
                    pastUsings = true;
                    bodyLines.Add("    " + line);
                }
            }

            var result = string.Join("\n", usingLines);
            result += $"\nnamespace {namespaceName}\n{{\n";
            result += string.Join("\n", bodyLines);
            result += "\n}\n";

            return result;
        }

        #endregion
    }
}
