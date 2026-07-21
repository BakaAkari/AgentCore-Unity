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
                    ""description"": ""Maximum number of results for list/search (default: 100)""
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
                        response = HandleListDirectory(parameters);
                        break;
                    case "search_content":
                        response = HandleSearchContent(parameters);
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
            if (string.IsNullOrWhiteSpace(relativePath))
                throw new ArgumentException("Path cannot be empty.");

            // Normalize separators
            var normalized = relativePath.Replace('\\', '/');

            // Block obvious traversal attempts
            if (normalized.Contains(".."))
            {
                // Resolve and verify
                var candidate = Path.GetFullPath(Path.Combine(ProjectRoot, normalized));
                if (!candidate.StartsWith(ProjectRoot, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"Path traversal detected: '{relativePath}' resolves outside the project directory.");
                return candidate;
            }

            var fullPath = Path.GetFullPath(Path.Combine(ProjectRoot, normalized));
            if (!fullPath.StartsWith(ProjectRoot, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Path '{relativePath}' resolves outside the project directory.");

            return fullPath;
        }

        /// <summary>
        /// Convert an absolute path back to a project-relative path.
        /// </summary>
        private static string ToRelativePath(string absolutePath)
        {
            if (absolutePath.StartsWith(ProjectRoot, StringComparison.OrdinalIgnoreCase))
            {
                var relative = absolutePath.Substring(ProjectRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return relative.Replace('\\', '/');
            }
            return absolutePath.Replace('\\', '/');
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

            File.WriteAllText(fullPath, content, encoding);

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
        /// </summary>
        private ToolResponse HandleListDirectory(JObject parameters)
        {
            var path = ToolHelpers.GetOptionalString(parameters, "path", ".");
            var fullPath = ResolveSafePath(path);

            if (!Directory.Exists(fullPath))
                return ToolResponse.Fail($"Directory not found: '{path}'");

            var pattern = ToolHelpers.GetOptionalString(parameters, "pattern", "*");
            var recursive = ToolHelpers.GetOptionalBool(parameters, "recursive", true);
            var maxResults = ToolHelpers.GetOptionalInt(parameters, "max_results", DefaultMaxResults);
            var includeHidden = ToolHelpers.GetOptionalBool(parameters, "include_hidden", false);

            var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

            var entries = new JArray();
            int totalCount = 0;
            bool truncated = false;

            try
            {
                // List directories first
                var dirs = Directory.GetDirectories(fullPath, "*", searchOption);
                foreach (var dir in dirs)
                {
                    var dirName = Path.GetFileName(dir);
                    if (!includeHidden && dirName.StartsWith("."))
                        continue;

                    if (totalCount >= maxResults)
                    {
                        truncated = true;
                        break;
                    }

                    entries.Add(new JObject
                    {
                        ["name"] = dirName,
                        ["path"] = ToRelativePath(dir),
                        ["type"] = "directory"
                    });
                    totalCount++;
                }

                // Then list files matching pattern
                if (!truncated)
                {
                    var files = Directory.GetFiles(fullPath, pattern, searchOption);
                    foreach (var file in files)
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
            catch (UnauthorizedAccessException)
            {
                // Skip inaccessible directories
            }

            var data = new JObject
            {
                ["directory"] = ToRelativePath(fullPath),
                ["pattern"] = pattern,
                ["recursive"] = recursive,
                ["count"] = totalCount,
                ["truncated"] = truncated,
                ["entries"] = entries
            };

            var msg = truncated
                ? $"Listed {totalCount} entries (truncated at max_results={maxResults}) in '{path}'"
                : $"Listed {totalCount} entries in '{path}'";

            return ToolResponse.OkWithData(data, msg);
        }

        /// <summary>
        /// Search file contents using regex pattern across the project.
        /// </summary>
        private ToolResponse HandleSearchContent(JObject parameters)
        {
            var searchPattern = ToolHelpers.GetRequiredString(parameters, "pattern");
            var path = ToolHelpers.GetOptionalString(parameters, "path", "Assets");
            var fullPath = ResolveSafePath(path);

            if (!Directory.Exists(fullPath))
                return ToolResponse.Fail($"Directory not found: '{path}'");

            var recursive = ToolHelpers.GetOptionalBool(parameters, "recursive", true);
            var maxResults = ToolHelpers.GetOptionalInt(parameters, "max_results", DefaultMaxResults);
            var contextLines = ToolHelpers.GetOptionalInt(parameters, "context_lines", DefaultContextLines);
            var caseSensitive = ToolHelpers.GetOptionalBool(parameters, "case_sensitive", false);
            var filePattern = ToolHelpers.GetOptionalString(parameters, "file_pattern", "*");
            var includeHidden = ToolHelpers.GetOptionalBool(parameters, "include_hidden", false);

            // Validate regex
            Regex regex;
            try
            {
                var options = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                options |= RegexOptions.Compiled;
                regex = new Regex(searchPattern, options);
            }
            catch (ArgumentException ex)
            {
                return ToolResponse.Fail($"Invalid regex pattern: {ex.Message}");
            }

            var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var matches = new JArray();
            int matchCount = 0;
            int filesSearched = 0;
            int filesMatched = 0;
            bool truncated = false;

            // Binary file extensions to skip
            var binaryExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
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

            try
            {
                string[] filePatterns;
                if (filePattern.Contains("|"))
                    filePatterns = filePattern.Split('|');
                else
                    filePatterns = new[] { filePattern };

                var allFiles = new List<string>();
                foreach (var fp in filePatterns)
                {
                    allFiles.AddRange(Directory.GetFiles(fullPath, fp.Trim(), searchOption));
                }

                // Deduplicate
                allFiles = allFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                foreach (var file in allFiles)
                {
                    if (truncated) break;

                    var fileName = Path.GetFileName(file);
                    if (!includeHidden && fileName.StartsWith("."))
                        continue;

                    var ext = Path.GetExtension(file);
                    if (binaryExtensions.Contains(ext))
                        continue;

                    // Skip very large files
                    var fi = new FileInfo(file);
                    if (fi.Length > MaxReadSize)
                        continue;

                    filesSearched++;

                    try
                    {
                        var lines = File.ReadAllLines(file);
                        bool fileHasMatch = false;

                        for (int i = 0; i < lines.Length; i++)
                        {
                            if (regex.IsMatch(lines[i]))
                            {
                                if (!fileHasMatch)
                                {
                                    filesMatched++;
                                    fileHasMatch = true;
                                }

                                matchCount++;
                                if (matchCount > maxResults)
                                {
                                    truncated = true;
                                    break;
                                }

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
                                    ["column"] = regex.Match(lines[i]).Index + 1,
                                    ["match"] = regex.Match(lines[i]).Value,
                                    ["context"] = contextSb.ToString().TrimEnd()
                                });
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // Skip files that can't be read (binary, locked, etc.)
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Skip inaccessible directories
            }

            var data = new JObject
            {
                ["pattern"] = searchPattern,
                ["search_path"] = ToRelativePath(fullPath),
                ["files_searched"] = filesSearched,
                ["files_matched"] = filesMatched,
                ["match_count"] = matchCount,
                ["truncated"] = truncated,
                ["matches"] = matches
            };

            return ToolResponse.OkWithData(data,
                $"Found {matchCount} matches in {filesMatched} files (searched {filesSearched} files)");
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
