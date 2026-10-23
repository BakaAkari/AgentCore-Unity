using System;
using System.Collections.Generic;
using System.Diagnostics;
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

namespace AgentCore.Editor.Tools.FileSystem
{
    /// <summary>
    /// General-purpose file system tool — read, write, search, list, copy, move, and delete
    /// any file type within the Unity project directory.
    /// <para>
    /// Complements <c>manage_script</c> (C#-only) and <c>manage_asset</c> (AssetDatabase-only)
    /// by providing raw file system access for non-.cs files, config files, files outside Assets/,
    /// and content-based search across the entire project.
    /// </para>
    /// </summary>
    [AgentTool("manage_file",
        Description = "General-purpose file I/O for ANY file in the project workspace. " +
                      "Actions: read, write, list_directory, search_content, file_info, delete, copy, move, create_directory. " +
                      "Supports all text formats (json, xml, yaml, txt, md, shader, uxml, uss, asmdef, etc). " +
                      "USE FOR: config files, non-C# source, content search across files (regex supported), directory listing, " +
                      "files outside Assets/ (Packages/, ProjectSettings/, etc), creating/editing .meta or .asset in raw text form. " +
                      "NOT FOR: C# scripts (use manage_script), Unity asset import settings (use manage_asset_import/manage_texture_import/manage_model_import), " +
                      "binary files (images, audio — read will fail on non-text). " +
                      "Returns: file content with line numbers (read), success/failure (write/delete/copy/move), directory tree (list), match results with context lines (search). " +
                      "IMPORTANT: paths are relative to project root; write creates parent directories automatically; search_content supports regex and glob file_pattern filtering.",
        Category = "FileSystem",
        RequiresMainThread = false,
        MayModifyScripts = true,
        RiskLevel = ToolRiskLevel.High,
        Capabilities = ToolCapability.WriteProjectFiles | ToolCapability.DeleteProjectFiles,
        ReadOnlyActions = new[] { "read_file", "file_info", "list_directory", "search_content", "ascii" })]
    public class ManageFileTool : IAgentTool
    {
        /// <summary>
        /// Maximum file size in bytes that can be read (5 MB).
        /// </summary>
        private const long MaxReadSize = 5 * 1024 * 1024;

        /// <summary>
        /// Maximum file size in bytes that can be written (10 MB).
        /// </summary>
        private const long MaxWriteSize = 10 * 1024 * 1024;

        /// <summary>
        /// Maximum number of results returned by search and list operations.
        /// </summary>
        private const int DefaultMaxResults = 100;

        /// <summary>
        /// Maximum number of context lines shown around each search match.
        /// </summary>
        private const int DefaultContextLines = 2;

        /// <summary>
        /// Overall wall-clock budget for one search_content call. When exceeded, the partial
        /// result is returned with truncatedReason = "timeout".
        /// </summary>
        private static readonly TimeSpan SearchTimeBudget = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Maximum number of files scanned by one search_content call (truncatedReason = "file_cap").
        /// </summary>
        private const int SearchFileCap = 20000;

        /// <summary>
        /// Maximum number of bytes read by one search_content call (truncatedReason = "file_cap").
        /// </summary>
        private const long SearchByteBudget = 256L * 1024 * 1024;

        /// <summary>
        /// Generated / cache directories directly under the project root that are never scanned
        /// unless they are the explicitly requested path.
        /// </summary>
        private static readonly HashSet<string> ExcludedProjectDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Library", "Temp", "Logs"
        };

        /// <summary>
        /// Build output directories skipped at any depth (unless explicitly requested).
        /// </summary>
        private static readonly HashSet<string> ExcludedDirsAnywhere = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "obj"
        };

        /// <summary>
        /// Extensions skipped by search_content (binary formats, plus large Unity YAML assets that would flood results).
        /// </summary>
        private static readonly HashSet<string> SearchSkippedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tga", ".psd", ".tif", ".tiff",
            ".wav", ".mp3", ".ogg", ".aiff", ".flac",
            ".fbx", ".obj", ".blend", ".3ds", ".dae", ".max",
            ".dll", ".exe", ".so", ".dylib", ".pdb",
            ".asset", ".unity", ".prefab", ".mat", ".controller", ".anim",
            ".ttf", ".otf", ".woff", ".woff2",
            ".zip", ".rar", ".7z", ".gz", ".tar",
            ".pdf", ".doc", ".docx",
            ".mp4", ".avi", ".mov", ".mkv",
            ".cubemap", ".exr", ".hdr"
        };

        /// <summary>
        /// Lines longer than this are clipped in ripgrep results (minified / generated files).
        /// </summary>
        private const int SearchMaxLineChars = 2000;

        /// <summary>
        /// The project root directory (parent of Assets/).
        /// </summary>
        private static readonly string ProjectRoot = Path.GetFullPath(
            Path.Combine(UnityEngine.Application.dataPath, ".."));

        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""read_file"", ""write_file"", ""list_directory"", ""search_content"", ""file_info"", ""delete"", ""copy"", ""move"", ""create_directory""],
                    ""description"": ""Action to perform""
                },
                ""path"": {
                    ""type"": ""string"",
                    ""description"": ""File or directory path relative to project root (e.g., 'Assets/Config/settings.json', 'Packages/com.my.pkg/README.md', 'ProjectSettings/ProjectSettings.asset')""
                },
                ""content"": {
                    ""type"": ""string"",
                    ""description"": ""File content for write_file action""
                },
                ""destination"": {
                    ""type"": ""string"",
                    ""description"": ""Destination path for copy/move actions (relative to project root)""
                },
                ""pattern"": {
                    ""type"": ""string"",
                    ""description"": ""Glob pattern for list_directory (e.g., '*.json', '*.xml') or regex pattern for search_content""
                },
                ""recursive"": {
                    ""type"": ""boolean"",
                    ""description"": ""Whether to search/list recursively (default: true)""
                },
                ""encoding"": {
                    ""type"": ""string"",
                    ""enum"": [""utf-8"", ""utf-8-bom"", ""ascii"", ""utf-16""],
                    ""description"": ""File encoding for read/write (default: 'utf-8')""
                },
                ""create_directories"": {
                    ""type"": ""boolean"",
                    ""description"": ""Whether to create parent directories if they don't exist for write_file (default: true)""
                },
                ""overwrite"": {
                    ""type"": ""boolean"",
                    ""description"": ""Whether to overwrite existing file for write_file/copy (default: true)""
                },
                ""max_results"": {
                    ""type"": ""integer"",
                    ""description"": ""Maximum number of results for list/search (default: 100). Library/, Temp/, Logs/ and obj/ are skipped unless they are the requested path.""
                },
                ""context_lines"": {
                    ""type"": ""integer"",
                    ""description"": ""Number of context lines around each search match (default: 2)""
                },
                ""offset"": {
                    ""type"": ""integer"",
                    ""description"": ""Line offset (1-based) for read_file to start reading from (default: 1)""
                },
                ""limit"": {
                    ""type"": ""integer"",
                    ""description"": ""Maximum number of lines to read for read_file (default: 0 = all lines)""
                },
                ""line_numbers"": {
                    ""type"": ""boolean"",
                    ""description"": ""Whether to include line numbers in read_file output (default: true)""
                },
                ""file_pattern"": {
                    ""type"": ""string"",
                    ""description"": ""Glob filter for files scanned by search_content, '|' separates several (e.g., '*.json|*.yaml'; default: '*')""
                },
                ""case_sensitive"": {
                    ""type"": ""boolean"",
                    ""description"": ""Whether search_content is case-sensitive (default: false)""
                },
                ""include_hidden"": {
                    ""type"": ""boolean"",
                    ""description"": ""Whether to include hidden files/directories (starting with '.') in list_directory (default: false)""
                }
            },
            ""required"": [""action""]
        }");

        /// <summary>
        /// Tool metadata for registration and LLM tool definition.
        /// </summary>
        public ToolMetadata Metadata => new ToolMetadata(
            name: "manage_file",
            description: "General-purpose file operations — read, write, list, search content, copy, move, delete any file in the project. " +
                         "Supports all file types (json, xml, yaml, txt, md, shader, etc). " +
                         "Use this for non-C# files, config files, content search, or files outside Assets/. " +
                         "For C# scripts use manage_script; for Unity asset operations use manage_asset.",
            category: "FileSystem",
            parametersSchema: _parametersSchema,
            requiresMainThread: false
        );

        /// <summary>
        /// Execute the file system operation specified by the action parameter.
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
                    case "read_file":
                        response = HandleReadFile(parameters);
                        break;
                    case "write_file":
                        response = HandleWriteFile(parameters);
                        break;
                    case "list_directory":
                        response = HandleListDirectory(parameters, cancellationToken);
                        break;
                    case "search_content":
                        response = HandleSearchContent(parameters, cancellationToken);
                        break;
                    case "file_info":
                        response = HandleFileInfo(parameters);
                        break;
                    case "delete":
                        response = HandleDelete(parameters);
                        break;
                    case "copy":
                        response = HandleCopy(parameters);
                        break;
                    case "move":
                        response = HandleMove(parameters);
                        break;
                    case "create_directory":
                        response = HandleCreateDirectory(parameters);
                        break;
                    default:
                        response = ToolResponse.Fail(
                            $"Unknown action: '{action}'. Valid actions: read_file, write_file, list_directory, search_content, file_info, delete, copy, move, create_directory");
                        break;
                }
            }
            catch (RegexMatchTimeoutException ex)
            {
                // Never report a timed-out regex as "0 matches".
                response = ToolResponse.Fail(SafeRegex.DescribeTimeout(ex.Pattern, "manage_file search_content"));
            }
            catch (OperationCanceledException)
            {
                response = ToolResponse.Fail("Operation cancelled; no complete result was produced.");
            }
            catch (ArgumentException ex)
            {
                response = ToolResponse.Fail(ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                response = ToolResponse.Fail($"Access denied: {ex.Message}");
            }
            catch (IOException ex)
            {
                response = ToolResponse.Fail($"IO error: {ex.Message}");
            }
            catch (Exception ex)
            {
                response = ToolResponse.Fail($"Unexpected error: {ex.Message}");
            }

            sw.Stop();
            var result = response.ToToolResult(sw.Elapsed.TotalMilliseconds);

            // Mark compile-related if a .cs file was modified
            if (result.Success)
            {
                var action = ToolHelpers.GetOptionalString(parameters, "action", "");
                var path = ToolHelpers.GetOptionalString(parameters, "path", "");
                var dest = ToolHelpers.GetOptionalString(parameters, "destination", "");

                bool modifiesScript = (action == "write_file" || action == "delete" || action == "move")
                                      && (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                                          || dest.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
                if (modifiesScript)
                {
                    result.IsCompileRelated = true;
                }
            }

            return Task.FromResult(result);
        }

        #region Path Security

        /// <summary>
        /// Resolve a relative path to an absolute path within the project root.
        /// Throws if the resolved path escapes the project directory.
        /// </summary>
        private static string ResolveSafePath(string relativePath)
        {
            // 分量级根约束 + .. 折叠 + 链接 / 机密文件拒绝
            // （原 StartsWith(ProjectRoot) 不带分隔符，D:\Proj2 会被当成 D:\Proj 的子目录）
            return SafePath.ResolveUnderOrThrow(ProjectRoot, relativePath);
        }

        /// <summary>
        /// Convert an absolute path back to a project-relative path.
        /// </summary>
        private static string ToRelativePath(string absolutePath)
        {
            if (SafePath.IsUnder(absolutePath, ProjectRoot))
            {
                var relative = absolutePath.Substring(ProjectRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return relative.Replace('\\', '/');
            }
            return absolutePath.Replace('\\', '/');
        }

        #endregion

        #region Search Helpers

        /// <summary>
        /// Whether a sub-directory is a generated / cache directory that scans skip by default.
        /// </summary>
        private static bool IsExcludedDirectory(string parentFullPath, string dirName)
        {
            if (ExcludedDirsAnywhere.Contains(dirName))
                return true;

            return ExcludedProjectDirs.Contains(dirName)
                   && string.Equals(
                       Path.GetFullPath(parentFullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                       ProjectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                       StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Enumerate sub-directories of <paramref name="root"/> (not including the root itself),
        /// pruning excluded / hidden directories instead of walking into them. The root is always
        /// scanned even if it is itself an excluded directory (explicit request).
        /// Inaccessible directories are skipped individually.
        /// </summary>
        private static IEnumerable<string> EnumerateDirectoriesPruned(
            string root, bool recursive, bool includeHidden, ISet<string> excludedHits, CancellationToken ct)
        {
            var pending = new Stack<string>();
            pending.Push(root);

            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var current = pending.Pop();

                string[] children;
                try
                {
                    children = Directory.GetDirectories(current);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    continue;
                }

                Array.Sort(children, StringComparer.OrdinalIgnoreCase);
                var toVisit = new List<string>();
                foreach (var child in children)
                {
                    var name = Path.GetFileName(child);
                    if (IsExcludedDirectory(current, name))
                    {
                        excludedHits?.Add(ToRelativePath(child));
                        continue;
                    }

                    if (!includeHidden && name.StartsWith("."))
                        continue;

                    toVisit.Add(child);
                }

                foreach (var child in toVisit)
                    yield return child;

                if (!recursive)
                    continue;

                // Push in reverse so siblings are visited in sorted order
                for (int i = toVisit.Count - 1; i >= 0; i--)
                    pending.Push(toVisit[i]);
            }
        }

        /// <summary>
        /// Files directly inside <paramref name="directory"/> matching <paramref name="pattern"/>;
        /// an inaccessible directory yields nothing instead of aborting the whole scan.
        /// </summary>
        private static IEnumerable<string> SafeEnumerateFiles(string directory, string pattern)
        {
            try
            {
                return Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return Array.Empty<string>();
            }
        }

        #endregion

        #region Encoding Helpers

        /// <summary>
        /// Get the System.Text.Encoding from the encoding parameter string.
        /// </summary>
        private static Encoding GetEncoding(string encodingName)
        {
            switch (encodingName?.ToLowerInvariant())
            {
                case "utf-8-bom":
                    return new UTF8Encoding(true);
                case "ascii":
                    return Encoding.ASCII;
                case "utf-16":
                    return Encoding.Unicode;
                case "utf-8":
                case null:
                case "":
                    return new UTF8Encoding(false);
                default:
                    return new UTF8Encoding(false);
            }
        }

        #endregion

        #region Action Handlers

        /// <summary>
        /// Read file content with optional line range and line numbers.
        /// </summary>
        private ToolResponse HandleReadFile(JObject parameters)
        {
            var path = ToolHelpers.GetRequiredString(parameters, "path");
            var fullPath = ResolveSafePath(path);

            if (!File.Exists(fullPath))
                return ToolResponse.Fail($"File not found: '{path}'");

            var fileInfo = new FileInfo(fullPath);
            if (fileInfo.Length > MaxReadSize)
                return ToolResponse.Fail($"File too large ({fileInfo.Length / 1024}KB). Maximum: {MaxReadSize / 1024}KB. Use offset/limit to read a portion.");

            var encodingName = ToolHelpers.GetOptionalString(parameters, "encoding");
            var encoding = GetEncoding(encodingName);
            var offset = ToolHelpers.GetOptionalInt(parameters, "offset", 1);
            var limit = ToolHelpers.GetOptionalInt(parameters, "limit", 0);
            var lineNumbers = ToolHelpers.GetOptionalBool(parameters, "line_numbers", true);

            if (offset < 1) offset = 1;

            var allLines = File.ReadAllLines(fullPath, encoding);
            var totalLines = allLines.Length;

            // Apply offset and limit
            var startIndex = offset - 1; // Convert to 0-based
            if (startIndex >= totalLines)
                return ToolResponse.Fail($"Offset {offset} exceeds total line count ({totalLines}).");

            var endIndex = limit > 0 ? Math.Min(startIndex + limit, totalLines) : totalLines;
            var selectedLines = allLines.Skip(startIndex).Take(endIndex - startIndex).ToArray();

            // Build output
            var sb = new StringBuilder();
            for (int i = 0; i < selectedLines.Length; i++)
            {
                if (lineNumbers)
                    sb.AppendLine($"{startIndex + i + 1,5} | {selectedLines[i]}");
                else
                    sb.AppendLine(selectedLines[i]);
            }

            var data = new JObject
            {
                ["path"] = ToRelativePath(fullPath),
                ["total_lines"] = totalLines,
                ["showing_from"] = offset,
                ["showing_to"] = startIndex + selectedLines.Length,
                ["content"] = sb.ToString()
            };

            var showingInfo = limit > 0
                ? $"Showing lines {offset}-{startIndex + selectedLines.Length} of {totalLines}"
                : $"Read {totalLines} lines";

            return ToolResponse.OkWithData(data, $"{showingInfo} from '{path}'");
        }

        /// <summary>
        /// Write content to a file, optionally creating parent directories.
        /// </summary>
        private ToolResponse HandleWriteFile(JObject parameters)
        {
            var path = ToolHelpers.GetRequiredString(parameters, "path");
            var content = ToolHelpers.GetRequiredString(parameters, "content");
            var fullPath = ResolveSafePath(path);

            if (content.Length > MaxWriteSize)
                return ToolResponse.Fail($"Content too large ({content.Length / 1024}KB). Maximum: {MaxWriteSize / 1024}KB.");

            var createDirs = ToolHelpers.GetOptionalBool(parameters, "create_directories", true);
            var overwrite = ToolHelpers.GetOptionalBool(parameters, "overwrite", true);
            var encodingName = ToolHelpers.GetOptionalString(parameters, "encoding");
            var encoding = GetEncoding(encodingName);

            bool isNew = !File.Exists(fullPath);

            if (!isNew && !overwrite)
                return ToolResponse.Fail($"File already exists: '{path}'. Set overwrite=true to replace.");

            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                if (!createDirs)
                    return ToolResponse.Fail($"Directory does not exist: '{Path.GetDirectoryName(path)}'. Set create_directories=true to create it.");
                Directory.CreateDirectory(dir);
            }

            AgentCore.Editor.Utils.AtomicFile.WriteAllText(fullPath, content, encoding);

            var lineCount = content.Split('\n').Length;
            var data = new JObject
            {
                ["path"] = ToRelativePath(fullPath),
                ["lines_written"] = lineCount,
                ["bytes_written"] = new FileInfo(fullPath).Length,
                ["created"] = isNew
            };

            return ToolResponse.OkWithData(data, isNew
                ? $"Created file '{path}' ({lineCount} lines)"
                : $"Updated file '{path}' ({lineCount} lines)");
        }

        /// <summary>
        /// List files and directories with optional glob pattern filtering.
        /// Library/, Temp/, Logs/ (project root) and obj/ (any depth) are pruned unless requested explicitly.
        /// </summary>
        private ToolResponse HandleListDirectory(JObject parameters, CancellationToken ct)
        {
            var path = ToolHelpers.GetOptionalString(parameters, "path", ".");
            var fullPath = ResolveSafePath(path);

            if (!Directory.Exists(fullPath))
                return ToolResponse.Fail($"Directory not found: '{path}'");

            var pattern = ToolHelpers.GetOptionalString(parameters, "pattern", "*");
            var recursive = ToolHelpers.GetOptionalBool(parameters, "recursive", true);
            var maxResults = ToolHelpers.GetOptionalInt(parameters, "max_results", DefaultMaxResults);
            var includeHidden = ToolHelpers.GetOptionalBool(parameters, "include_hidden", false);

            var entries = new JArray();
            var excludedDirs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            int totalCount = 0;
            bool truncated = false;

            var dirs = EnumerateDirectoriesPruned(fullPath, recursive, includeHidden, excludedDirs, ct).ToList();

            // List directories first
            foreach (var dir in dirs)
            {
                if (totalCount >= maxResults)
                {
                    truncated = true;
                    break;
                }

                entries.Add(new JObject
                {
                    ["name"] = Path.GetFileName(dir),
                    ["path"] = ToRelativePath(dir),
                    ["type"] = "directory"
                });
                totalCount++;
            }

            // Then list files matching pattern
            if (!truncated)
            {
                foreach (var dir in new[] { fullPath }.Concat(dirs))
                {
                    if (truncated) break;
                    ct.ThrowIfCancellationRequested();

                    foreach (var file in SafeEnumerateFiles(dir, pattern))
                    {
                        var fileName = Path.GetFileName(file);
                        if (!includeHidden && fileName.StartsWith("."))
                            continue;

                        if (totalCount >= maxResults)
                        {
                            truncated = true;
                            break;
                        }

                        var fi = new FileInfo(file);
                        entries.Add(new JObject
                        {
                            ["name"] = fileName,
                            ["path"] = ToRelativePath(file),
                            ["type"] = "file",
                            ["size"] = fi.Length,
                            ["extension"] = fi.Extension
                        });
                        totalCount++;
                    }
                }
            }

            var data = new JObject
            {
                ["directory"] = ToRelativePath(fullPath),
                ["pattern"] = pattern,
                ["recursive"] = recursive,
                ["count"] = totalCount,
                ["truncated"] = truncated,
                ["truncatedReason"] = truncated ? "max_results" : null,
                ["excluded_dirs"] = new JArray(excludedDirs),
                ["entries"] = entries
            };

            var msg = truncated
                ? $"Listed {totalCount} entries (truncated at max_results={maxResults}) in '{path}'"
                : $"Listed {totalCount} entries in '{path}'";
            if (excludedDirs.Count > 0)
                msg += $"; skipped generated dirs: {string.Join(", ", excludedDirs)} (pass one as path to list it)";

            return ToolResponse.OkWithData(data, msg);
        }

        /// <summary>
        /// Search file contents using regex pattern across the project.
        /// <para>
        /// Engine: ripgrep when it is available (linear-time Rust regex — no catastrophic backtracking —
        /// multi-threaded, honours .gitignore); otherwise, or when rg cannot compile the pattern
        /// (look-around / backreferences) or fails to run, the built-in .NET scanner. The result shape is
        /// identical for both; <c>engine</c> says which one answered and <c>engine_note</c> why rg was not used.
        /// </para>
        /// </summary>
        private ToolResponse HandleSearchContent(JObject parameters, CancellationToken ct)
        {
            var searchPattern = ToolHelpers.GetRequiredString(parameters, "pattern");
            var path = ToolHelpers.GetOptionalString(parameters, "path", "Assets");
            var fullPath = ResolveSafePath(path);

            if (!Directory.Exists(fullPath))
                return ToolResponse.Fail($"Directory not found: '{path}'");

            var request = new ContentSearchRequest
            {
                Pattern = searchPattern,
                FullPath = fullPath,
                Recursive = ToolHelpers.GetOptionalBool(parameters, "recursive", true),
                MaxResults = Math.Max(1, ToolHelpers.GetOptionalInt(parameters, "max_results", DefaultMaxResults)),
                ContextLines = Math.Max(0, ToolHelpers.GetOptionalInt(parameters, "context_lines", DefaultContextLines)),
                CaseSensitive = ToolHelpers.GetOptionalBool(parameters, "case_sensitive", false),
                FilePatterns = ToolHelpers.GetOptionalString(parameters, "file_pattern", "*").Split('|')
                    .Select(fp => fp.Trim())
                    .Where(fp => fp.Length > 0)
                    .DefaultIfEmpty("*")
                    .ToArray(),
                IncludeHidden = ToolHelpers.GetOptionalBool(parameters, "include_hidden", false),
            };

            string engineNote = null;
            if (RgLocator.TryGetExecutable(out var rgPath))
            {
                var rgResponse = SearchContentWithRipgrep(rgPath, request, ct, out engineNote);
                if (rgResponse != null)
                    return rgResponse;
            }

            return SearchContentDotnet(request, engineNote, ct);
        }

        /// <summary>
        /// Parsed search_content parameters shared by both engines.
        /// </summary>
        private sealed class ContentSearchRequest
        {
            public string Pattern;
            public string FullPath;
            public bool Recursive;
            public int MaxResults;
            public int ContextLines;
            public bool CaseSensitive;
            public string[] FilePatterns;
            public bool IncludeHidden;
        }

        /// <summary>
        /// Whether file_pattern names this extension explicitly (e.g. '*.prefab'), which re-enables
        /// an extension that search_content skips by default.
        /// </summary>
        private static bool IsExtensionRequested(string[] filePatterns, string extension)
        {
            return filePatterns.Any(fp => fp != "*" && fp != "*.*" && fp.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// search_content via ripgrep. Returns null — with <paramref name="fallbackNote"/> explaining why when it
        /// matters — when the .NET scanner should answer instead; an rg-specific problem is never an error.
        /// <para>
        /// Scope mirrors the .NET scanner: Library/ Temp/ Logs/ under the project root and obj/ at any depth are
        /// skipped unless they are the requested path; hidden entries need include_hidden; the same binary /
        /// Unity-YAML extensions are skipped unless file_pattern names them; files over 5 MB are skipped.
        /// Unlike the .NET scanner, rg also honours .gitignore / .ignore files inside the searched directories.
        /// </para>
        /// </summary>
        private ToolResponse SearchContentWithRipgrep(string rgPath, ContentSearchRequest req, CancellationToken ct, out string fallbackNote)
        {
            fallbackNote = null;
            var excludedDirs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var options = new RgSearchOptions
            {
                Pattern = req.Pattern,
                CaseSensitive = req.CaseSensitive,
                WorkingDirectory = ProjectRoot,
                IncludeHidden = req.IncludeHidden,
                MaxFileSizeBytes = MaxReadSize,
                MaxColumns = SearchMaxLineChars,
                MaxLineChars = SearchMaxLineChars,
                ContextLines = req.ContextLines,
                MaxResults = req.MaxResults,
                KeepCountingAfterLimit = true,
                Timeout = SearchTimeBudget,
            };

            bool allFiles = req.FilePatterns.Any(fp => fp == "*" || fp == "*.*");
            if (!allFiles)
                options.IncludeGlobs.AddRange(req.FilePatterns);

            foreach (var ext in SearchSkippedExtensions)
            {
                if (allFiles || !IsExtensionRequested(req.FilePatterns, ext))
                    options.ExcludeGlobs.Add("*" + ext);
            }

            foreach (var dirName in ExcludedDirsAnywhere)
                options.ExcludeGlobs.Add(dirName + "/");

            bool isProjectRoot = string.Equals(
                req.FullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                ProjectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
            var relativeRoot = isProjectRoot ? "." : ToRelativePath(req.FullPath);

            if (!req.Recursive)
            {
                options.MaxDepth = 1;
                options.Paths.Add(relativeRoot);
            }
            else if (isProjectRoot)
            {
                // Name the project root's children explicitly so Library/ Temp/ Logs/ are pruned without being
                // walked (an rg glob cannot cheaply anchor a directory exclusion to the root).
                string[] childDirs;
                try
                {
                    childDirs = Directory.GetDirectories(ProjectRoot);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    return null;
                }
                Array.Sort(childDirs, StringComparer.OrdinalIgnoreCase);

                foreach (var child in childDirs)
                {
                    var name = Path.GetFileName(child);
                    if (IsExcludedDirectory(ProjectRoot, name))
                    {
                        excludedDirs.Add(ToRelativePath(child));
                        continue;
                    }
                    if (!req.IncludeHidden && name.StartsWith("."))
                        continue;
                    options.Paths.Add(name);
                }

                // rg searches explicitly named files regardless of --glob, so root-level files are filtered here.
                var rootFiles = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var fp in req.FilePatterns)
                {
                    foreach (var file in SafeEnumerateFiles(ProjectRoot, fp))
                    {
                        var fileName = Path.GetFileName(file);
                        var ext = Path.GetExtension(file);
                        if (!req.IncludeHidden && fileName.StartsWith("."))
                            continue;
                        if (SearchSkippedExtensions.Contains(ext) && (allFiles || !IsExtensionRequested(req.FilePatterns, ext)))
                            continue;
                        rootFiles.Add(fileName);
                    }
                }
                options.Paths.AddRange(rootFiles);

                if (options.Paths.Count == 0)
                    return null; // nothing to search; the .NET scanner answers trivially
            }
            else
            {
                options.Paths.Add(relativeRoot);
                foreach (var dirName in ExcludedDirsAnywhere)
                {
                    var nested = Path.Combine(req.FullPath, dirName);
                    if (Directory.Exists(nested))
                        excludedDirs.Add(ToRelativePath(nested));
                }
            }

            RgSearchResult rg;
            try
            {
                rg = RgSearch.Run(rgPath, options, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                fallbackNote = $"ripgrep failed ({ex.Message}); searched with the built-in .NET scanner instead.";
                return null;
            }

            switch (rg.Status)
            {
                case RgSearchStatus.RegexUnsupported:
                    fallbackNote = "ripgrep's regex engine (Rust syntax: no look-around or backreferences) could not compile this pattern " +
                                   $"({rg.ErrorMessage}); searched with the .NET regex engine instead.";
                    return null;
                case RgSearchStatus.Failed:
                    if (rg.StartFailed)
                        RgLocator.MarkUnavailable(rg.ErrorMessage);
                    fallbackNote = $"ripgrep failed ({rg.ErrorMessage}); searched with the built-in .NET scanner instead.";
                    return null;
            }

            var matches = new JArray();
            foreach (var m in rg.Matches)
            {
                matches.Add(new JObject
                {
                    ["file"] = m.Path,
                    ["line"] = m.Line,
                    ["column"] = m.Column,
                    ["match"] = m.Match,
                    ["context"] = m.Context
                });
            }

            var overMax = rg.TotalMatches > req.MaxResults;
            var data = new JObject
            {
                ["pattern"] = req.Pattern,
                ["search_path"] = ToRelativePath(req.FullPath),
                ["files_searched"] = rg.FilesSearched,
                ["files_matched"] = rg.FilesMatched,
                ["files_unreadable"] = rg.FilesUnreadable,
                ["matches"] = matches,
                ["totalMatches"] = rg.TotalMatches,
                ["totalIsLowerBound"] = rg.TotalIsLowerBound,
                ["truncated"] = rg.Truncated,
                ["truncatedReason"] = rg.TruncatedReason,
                ["excluded_dirs"] = new JArray(excludedDirs),
                ["engine"] = "ripgrep"
            };

            var plus = rg.TotalIsLowerBound ? "+" : "";
            var msg = $"Found {rg.TotalMatches}{plus} matching lines in {rg.FilesMatched}{plus} files (searched {rg.FilesSearched}{plus} files with ripgrep; .gitignore'd files skipped)";
            if (overMax)
                msg += $"; showing {matches.Count} (max_results={req.MaxResults})";
            if (rg.TruncatedReason == "timeout")
                msg += $"; scan stopped after {SearchTimeBudget.TotalSeconds:0}s budget, narrow path/file_pattern for complete results";
            if (excludedDirs.Count > 0)
                msg += $"; skipped generated dirs: {string.Join(", ", excludedDirs)} (pass one as path to search it)";

            return ToolResponse.OkWithData(data, msg);
        }

        /// <summary>
        /// search_content via the built-in .NET scanner.
        /// <para>
        /// Safety: <see cref="SafeRegex"/> caps the pattern length and gives each match a timeout; a timeout
        /// is returned as an error, never as "0 matches". The scan honours cancellation per file and
        /// stops at a time / file / byte budget, returning what it found with truncatedReason set.
        /// </para>
        /// </summary>
        private ToolResponse SearchContentDotnet(ContentSearchRequest req, string engineNote, CancellationToken ct)
        {
            var searchPattern = req.Pattern;
            var fullPath = req.FullPath;
            var recursive = req.Recursive;
            var maxResults = req.MaxResults;
            var contextLines = req.ContextLines;
            var caseSensitive = req.CaseSensitive;
            var includeHidden = req.IncludeHidden;

            // Validate regex (shared SafeRegex: length cap + match timeout; invalid patterns explain why)
            Regex regex;
            try
            {
                regex = SafeRegex.Create(searchPattern, ignoreCase: !caseSensitive);
            }
            catch (ArgumentException ex)
            {
                return ToolResponse.Fail(engineNote != null ? $"{ex.Message} (note: {engineNote})" : ex.Message);
            }

            var matches = new JArray();
            var excludedDirs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            int matchCount = 0;
            int filesSearched = 0;
            int filesMatched = 0;
            int filesUnreadable = 0;
            long bytesRead = 0;
            string scanStopReason = null;
            var budget = Stopwatch.StartNew();

            var binaryExtensions = SearchSkippedExtensions;
            var filePatterns = req.FilePatterns;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var directories = new[] { fullPath }.Concat(recursive
                ? EnumerateDirectoriesPruned(fullPath, true, includeHidden, excludedDirs, ct)
                : Enumerable.Empty<string>());

            foreach (var dir in directories)
            {
                if (scanStopReason != null) break;

                foreach (var fp in filePatterns)
                {
                    if (scanStopReason != null) break;

                    foreach (var file in SafeEnumerateFiles(dir, fp))
                    {
                        ct.ThrowIfCancellationRequested();

                        if (budget.Elapsed > SearchTimeBudget)
                        {
                            scanStopReason = "timeout";
                            break;
                        }

                        if (filesSearched >= SearchFileCap || bytesRead >= SearchByteBudget)
                        {
                            scanStopReason = "file_cap";
                            break;
                        }

                        if (!seen.Add(file))
                            continue;

                        var fileName = Path.GetFileName(file);
                        if (!includeHidden && fileName.StartsWith("."))
                            continue;

                        var ext = Path.GetExtension(file);
                        if (binaryExtensions.Contains(ext))
                            continue;

                        string[] lines;
                        try
                        {
                            // Skip very large files
                            var fi = new FileInfo(file);
                            if (fi.Length > MaxReadSize)
                                continue;

                            lines = File.ReadAllLines(file);
                            bytesRead += fi.Length;
                        }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                        {
                            // Locked / inaccessible file: counted, not fatal
                            filesUnreadable++;
                            continue;
                        }

                        filesSearched++;
                        bool fileHasMatch = false;

                        // RegexMatchTimeoutException propagates on purpose: it becomes an error, not 0 results.
                        for (int i = 0; i < lines.Length; i++)
                        {
                            var m = regex.Match(lines[i]);
                            if (!m.Success)
                                continue;

                            if (!fileHasMatch)
                            {
                                filesMatched++;
                                fileHasMatch = true;
                            }

                            matchCount++;

                            // Past max_results: keep counting so totalMatches stays honest, but stop collecting.
                            if (matchCount > maxResults)
                                continue;

                            // Build context
                            var contextStart = Math.Max(0, i - contextLines);
                            var contextEnd = Math.Min(lines.Length - 1, i + contextLines);
                            var contextSb = new StringBuilder();
                            for (int j = contextStart; j <= contextEnd; j++)
                            {
                                var prefix = j == i ? ">>>" : "   ";
                                contextSb.AppendLine($"{prefix} {j + 1,5} | {lines[j]}");
                            }

                            matches.Add(new JObject
                            {
                                ["file"] = ToRelativePath(file),
                                ["line"] = i + 1,
                                ["column"] = m.Index + 1,
                                ["match"] = m.Value,
                                ["context"] = contextSb.ToString().TrimEnd()
                            });
                        }
                    }
                }
            }

            var overMax = matchCount > maxResults;
            var truncated = overMax || scanStopReason != null;
            var truncatedReason = scanStopReason ?? (overMax ? "max_results" : null);

            var data = new JObject
            {
                ["pattern"] = searchPattern,
                ["search_path"] = ToRelativePath(fullPath),
                ["files_searched"] = filesSearched,
                ["files_matched"] = filesMatched,
                ["files_unreadable"] = filesUnreadable,
                ["matches"] = matches,
                ["totalMatches"] = matchCount,
                ["totalIsLowerBound"] = scanStopReason != null,
                ["truncated"] = truncated,
                ["truncatedReason"] = truncatedReason,
                ["excluded_dirs"] = new JArray(excludedDirs),
                ["engine"] = "dotnet"
            };
            if (engineNote != null)
                data["engine_note"] = engineNote;

            var msg = $"Found {matchCount}{(scanStopReason != null ? "+" : "")} matching lines in {filesMatched} files (searched {filesSearched} files)";
            if (overMax)
                msg += $"; showing first {matches.Count} (max_results={maxResults})";
            if (scanStopReason == "timeout")
                msg += $"; scan stopped after {SearchTimeBudget.TotalSeconds:0}s budget, narrow path/file_pattern for complete results";
            else if (scanStopReason == "file_cap")
                msg += $"; scan stopped at file/byte cap ({SearchFileCap} files / {SearchByteBudget / (1024 * 1024)}MB), narrow path/file_pattern for complete results";
            if (excludedDirs.Count > 0)
                msg += $"; skipped generated dirs: {string.Join(", ", excludedDirs)} (pass one as path to search it)";
            if (engineNote != null)
                msg += $"; note: {engineNote}";

            return ToolResponse.OkWithData(data, msg);
        }

        /// <summary>
        /// Get detailed information about a file or directory.
        /// </summary>
        private ToolResponse HandleFileInfo(JObject parameters)
        {
            var path = ToolHelpers.GetRequiredString(parameters, "path");
            var fullPath = ResolveSafePath(path);

            if (File.Exists(fullPath))
            {
                var fi = new FileInfo(fullPath);
                var data = new JObject
                {
                    ["path"] = ToRelativePath(fullPath),
                    ["type"] = "file",
                    ["exists"] = true,
                    ["name"] = fi.Name,
                    ["extension"] = fi.Extension,
                    ["size_bytes"] = fi.Length,
                    ["size_display"] = FormatFileSize(fi.Length),
                    ["created_utc"] = fi.CreationTimeUtc.ToString("yyyy-MM-dd HH:mm:ss"),
                    ["modified_utc"] = fi.LastWriteTimeUtc.ToString("yyyy-MM-dd HH:mm:ss"),
                    ["is_readonly"] = fi.IsReadOnly,
                    ["directory"] = ToRelativePath(fi.DirectoryName)
                };

                // Count lines for text files
                if (IsTextFile(fi.Extension) && fi.Length <= MaxReadSize)
                {
                    try
                    {
                        var lineCount = File.ReadAllLines(fullPath).Length;
                        data["line_count"] = lineCount;
                    }
                    catch
                    {
                        // Ignore if can't read
                    }
                }

                return ToolResponse.OkWithData(data, $"File info: '{path}' ({FormatFileSize(fi.Length)})");
            }

            if (Directory.Exists(fullPath))
            {
                var di = new DirectoryInfo(fullPath);
                int fileCount = 0;
                int dirCount = 0;
                long totalSize = 0;

                try
                {
                    var files = di.GetFiles("*", SearchOption.AllDirectories);
                    fileCount = files.Length;
                    totalSize = files.Sum(f => f.Length);
                    dirCount = di.GetDirectories("*", SearchOption.AllDirectories).Length;
                }
                catch (UnauthorizedAccessException)
                {
                    // Partial count is fine
                }

                var data = new JObject
                {
                    ["path"] = ToRelativePath(fullPath),
                    ["type"] = "directory",
                    ["exists"] = true,
                    ["name"] = di.Name,
                    ["created_utc"] = di.CreationTimeUtc.ToString("yyyy-MM-dd HH:mm:ss"),
                    ["modified_utc"] = di.LastWriteTimeUtc.ToString("yyyy-MM-dd HH:mm:ss"),
                    ["file_count"] = fileCount,
                    ["directory_count"] = dirCount,
                    ["total_size_bytes"] = totalSize,
                    ["total_size_display"] = FormatFileSize(totalSize)
                };

                return ToolResponse.OkWithData(data, $"Directory info: '{path}' ({fileCount} files, {dirCount} subdirs)");
            }

            return ToolResponse.OkWithData(new JObject
            {
                ["path"] = path,
                ["exists"] = false
            }, $"Path does not exist: '{path}'");
        }

        /// <summary>
        /// Delete a file or empty directory.
        /// </summary>
        private ToolResponse HandleDelete(JObject parameters)
        {
            var path = ToolHelpers.GetRequiredString(parameters, "path");
            var fullPath = ResolveSafePath(path);

            if (File.Exists(fullPath))
            {
                var fi = new FileInfo(fullPath);
                var size = fi.Length;
                File.Delete(fullPath);

                // Also delete .meta file if it exists (Unity convention)
                var metaPath = fullPath + ".meta";
                if (File.Exists(metaPath))
                    File.Delete(metaPath);

                return ToolResponse.Ok($"Deleted file '{path}' ({FormatFileSize(size)})");
            }

            if (Directory.Exists(fullPath))
            {
                var di = new DirectoryInfo(fullPath);
                if (di.GetFileSystemInfos().Length > 0)
                    return ToolResponse.Fail($"Directory '{path}' is not empty. Delete its contents first or use a different approach.");

                Directory.Delete(fullPath);

                // Also delete .meta file if it exists
                var metaPath = fullPath + ".meta";
                if (File.Exists(metaPath))
                    File.Delete(metaPath);

                return ToolResponse.Ok($"Deleted empty directory '{path}'");
            }

            return ToolResponse.Fail($"Path not found: '{path}'");
        }

        /// <summary>
        /// Copy a file to a new location.
        /// </summary>
        private ToolResponse HandleCopy(JObject parameters)
        {
            var sourcePath = ToolHelpers.GetRequiredString(parameters, "path");
            var destPath = ToolHelpers.GetRequiredString(parameters, "destination");
            var overwrite = ToolHelpers.GetOptionalBool(parameters, "overwrite", true);

            var fullSource = ResolveSafePath(sourcePath);
            var fullDest = ResolveSafePath(destPath);

            if (!File.Exists(fullSource))
                return ToolResponse.Fail($"Source file not found: '{sourcePath}'");

            if (File.Exists(fullDest) && !overwrite)
                return ToolResponse.Fail($"Destination file already exists: '{destPath}'. Set overwrite=true to replace.");

            // Ensure destination directory exists
            var destDir = Path.GetDirectoryName(fullDest);
            if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                Directory.CreateDirectory(destDir);

            File.Copy(fullSource, fullDest, overwrite);

            var data = new JObject
            {
                ["source"] = ToRelativePath(fullSource),
                ["destination"] = ToRelativePath(fullDest),
                ["size"] = new FileInfo(fullDest).Length
            };

            return ToolResponse.OkWithData(data, $"Copied '{sourcePath}' → '{destPath}'");
        }

        /// <summary>
        /// Move/rename a file or directory.
        /// </summary>
        private ToolResponse HandleMove(JObject parameters)
        {
            var sourcePath = ToolHelpers.GetRequiredString(parameters, "path");
            var destPath = ToolHelpers.GetRequiredString(parameters, "destination");

            var fullSource = ResolveSafePath(sourcePath);
            var fullDest = ResolveSafePath(destPath);

            if (File.Exists(fullSource))
            {
                if (File.Exists(fullDest))
                    return ToolResponse.Fail($"Destination file already exists: '{destPath}'. Delete it first or use copy with overwrite.");

                // Ensure destination directory exists
                var destDir = Path.GetDirectoryName(fullDest);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                    Directory.CreateDirectory(destDir);

                File.Move(fullSource, fullDest);

                // Also move .meta file if it exists
                var sourceMetaPath = fullSource + ".meta";
                var destMetaPath = fullDest + ".meta";
                if (File.Exists(sourceMetaPath))
                    File.Move(sourceMetaPath, destMetaPath);

                var data = new JObject
                {
                    ["source"] = ToRelativePath(fullSource),
                    ["destination"] = ToRelativePath(fullDest)
                };

                return ToolResponse.OkWithData(data, $"Moved '{sourcePath}' → '{destPath}'");
            }

            if (Directory.Exists(fullSource))
            {
                if (Directory.Exists(fullDest))
                    return ToolResponse.Fail($"Destination directory already exists: '{destPath}'.");

                Directory.Move(fullSource, fullDest);

                // Also move .meta file if it exists
                var sourceMetaPath = fullSource + ".meta";
                var destMetaPath = fullDest + ".meta";
                if (File.Exists(sourceMetaPath))
                    File.Move(sourceMetaPath, destMetaPath);

                var data = new JObject
                {
                    ["source"] = ToRelativePath(fullSource),
                    ["destination"] = ToRelativePath(fullDest)
                };

                return ToolResponse.OkWithData(data, $"Moved directory '{sourcePath}' → '{destPath}'");
            }

            return ToolResponse.Fail($"Source path not found: '{sourcePath}'");
        }

        /// <summary>
        /// Create a directory (including nested directories).
        /// </summary>
        private ToolResponse HandleCreateDirectory(JObject parameters)
        {
            var path = ToolHelpers.GetRequiredString(parameters, "path");
            var fullPath = ResolveSafePath(path);

            if (Directory.Exists(fullPath))
                return ToolResponse.Ok($"Directory already exists: '{path}'");

            if (File.Exists(fullPath))
                return ToolResponse.Fail($"A file with the same name already exists: '{path}'");

            Directory.CreateDirectory(fullPath);

            return ToolResponse.Ok($"Created directory '{path}'");
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Check if a file extension indicates a text file.
        /// </summary>
        private static bool IsTextFile(string extension)
        {
            var textExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".cs", ".js", ".ts", ".json", ".xml", ".yaml", ".yml", ".txt", ".md",
                ".html", ".htm", ".css", ".scss", ".less", ".csv", ".tsv",
                ".shader", ".cginc", ".hlsl", ".glsl", ".compute",
                ".asmdef", ".asmref", ".rsp",
                ".py", ".rb", ".lua", ".sh", ".bat", ".cmd", ".ps1",
                ".cfg", ".ini", ".conf", ".config", ".properties",
                ".gitignore", ".gitattributes", ".editorconfig",
                ".log", ".uss", ".uxml", ".template"
            };
            return textExtensions.Contains(extension);
        }

        /// <summary>
        /// Format file size in human-readable form.
        /// </summary>
        private static string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
            return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
        }

        #endregion
    }
}
