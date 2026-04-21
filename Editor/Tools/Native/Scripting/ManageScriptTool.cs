using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Tools.Infrastructure;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Tools.Native.Scripting
{
    /// <summary>
    /// Manage C# scripts — read, write, create from template, delete, list, and get info.
    /// Directly calls System.IO and AssetDatabase APIs.
    /// </summary>
    [AgentTool("manage_script",
        Description = "Manage C# scripts — read, write, create from template, delete, list, and get info",
        Category = "Scripting",
        RequiresMainThread = true,
        MayModifyScripts = true)]
    public class ManageScriptTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""read"", ""write"", ""create"", ""delete"", ""list"", ""get_info""],
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
                }
            },
            ""required"": [""action""]
        }");

        public ToolMetadata Metadata => new ToolMetadata(
            name: "manage_script",
            description: "Manage C# scripts — read, write, create from template, delete, list, and get info",
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
                    default:
                        response = ToolResponse.Fail(
                            $"Unknown action: '{action}'. Valid actions: read, write, create, delete, list, get_info");
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

            // Mark compile-related for write/create/delete actions
            if (result.Success)
            {
                var action = ToolHelpers.GetOptionalString(parameters, "action", "");
                if (action == "write" || action == "create" || action == "delete")
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
