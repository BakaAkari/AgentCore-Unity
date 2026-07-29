using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgentCore.Editor.LLM;
using Newtonsoft.Json.Linq;
using UnityEngine;
using AgentCore.Editor.Utils;

namespace AgentCore.Editor.Core
{
    #region 文件变更类型

    /// <summary>
    /// 文件变更类型枚举。
    /// 描述工具调用对文件产生的变更类型。
    /// </summary>
    public enum FileChangeType
    {
        /// <summary>新建文件</summary>
        Created,

        /// <summary>修改文件</summary>
        Modified,

        /// <summary>删除文件</summary>
        Deleted,

        /// <summary>移动/重命名文件</summary>
        Moved,

        /// <summary>复制文件</summary>
        Copied
    }

    #endregion

    #region 文件变更记录

    /// <summary>
    /// 文件变更记录 — 描述单次工具调用对单个文件的变更。
    /// <para>
    /// 包含文件路径、变更类型、执行工具名、增减行数等信息。
    /// 同一文件在同一会话中可能被多次修改，每次修改产生一条记录，
    /// 但 <see cref="FileChangeTracker"/> 会合并同一文件的多次变更。
    /// </para>
    /// </summary>
    public class FileChangeRecord
    {
        /// <summary>文件相对路径（相对于项目根目录）</summary>
        public string FilePath { get; set; }

        /// <summary>变更类型</summary>
        public FileChangeType ChangeType { get; set; }

        /// <summary>执行变更的工具名称</summary>
        public string ToolName { get; set; }

        /// <summary>工具的具体 action（如 write, create, delete 等）</summary>
        public string Action { get; set; }

        /// <summary>新增行数</summary>
        public int LinesAdded { get; set; }

        /// <summary>删除行数</summary>
        public int LinesRemoved { get; set; }

        /// <summary>变更发生时间（UTC）</summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// 获取变更的净行数变化（正数表示净增，负数表示净减）。
        /// </summary>
        public int NetLineChange => LinesAdded - LinesRemoved;

        /// <inheritdoc />
        public override string ToString()
        {
            var typeIcon = ChangeType switch
            {
                FileChangeType.Created => "[+]",
                FileChangeType.Modified => "[~]",
                FileChangeType.Deleted => "[-]",
                FileChangeType.Moved => "[>]",
                FileChangeType.Copied => "[=]",
                _ => "[?]"
            };
            return $"{typeIcon} {FilePath} +{LinesAdded} -{LinesRemoved}";
        }
    }

    #endregion

    #region 合并后的文件变更摘要

    /// <summary>
    /// 合并后的文件变更摘要 — 同一文件的多次变更合并为一条摘要。
    /// <para>
    /// 用于 UI 展示，每个文件只显示一行，累计所有变更的增减行数。
    /// 变更类型取最后一次变更的类型（如先 Created 再 Modified，最终显示 Modified）。
    /// </para>
    /// </summary>
    public class FileChangeSummary
    {
        /// <summary>文件相对路径</summary>
        public string FilePath { get; set; }

        /// <summary>最终变更类型（取最后一次变更的类型）</summary>
        public FileChangeType ChangeType { get; set; }

        /// <summary>累计新增行数</summary>
        public int TotalLinesAdded { get; set; }

        /// <summary>累计删除行数</summary>
        public int TotalLinesRemoved { get; set; }

        /// <summary>最后一次变更时间</summary>
        public DateTime LastModified { get; set; }

        /// <summary>
        /// 获取变更类型的显示图标。
        /// </summary>
        public string TypeIcon => ChangeType switch
        {
            FileChangeType.Created => "+",
            FileChangeType.Modified => "~",
            FileChangeType.Deleted => "-",
            FileChangeType.Moved => ">",
            FileChangeType.Copied => "=",
            _ => "?"
        };
    }

    #endregion

    #region 文件变更追踪器

    /// <summary>
    /// 文件变更追踪器 — 追踪当前会话中所有工具调用产生的文件变更。
    /// <para>
    /// 核心功能：
    /// <list type="number">
    ///   <item>在工具执行前，通过 <see cref="SnapshotBeforeExecution"/> 记录目标文件的行数快照</item>
    ///   <item>在工具执行后，通过 <see cref="TrackFromToolCalls"/> 解析工具参数和结果，计算增减行数</item>
    ///   <item>通过 <see cref="GetSummaries"/> 获取合并后的文件变更摘要列表</item>
    /// </list>
    /// </para>
    /// <para>
    /// 设计要点：
    /// <list type="bullet">
    ///   <item>会话级别：每个会话独立追踪，会话切换/重置时清空</item>
    ///   <item>不持久化：变更记录仅在内存中，不保存到 SessionData</item>
    ///   <item>近似计算：行数变化基于文件总行数对比，不是 git diff 级别的精确统计</item>
    /// </list>
    /// </para>
    /// </summary>
    public class FileChangeTracker
    {
        #region 私有字段

        /// <summary>所有文件变更记录（按时间顺序）</summary>
        private readonly List<FileChangeRecord> _records = new List<FileChangeRecord>();

        /// <summary>执行前的文件行数快照（key: 绝对路径, value: 行数）</summary>
        private readonly Dictionary<string, int> _lineCountSnapshots = new Dictionary<string, int>();

        /// <summary>已追踪的工具名称和对应的 action 映射</summary>
        private static readonly Dictionary<string, HashSet<string>> TrackedToolActions = new Dictionary<string, HashSet<string>>
        {
            ["manage_script"] = new HashSet<string> { "write", "create", "delete", "add_method", "add_field" },
            ["manage_file"] = new HashSet<string> { "write_file", "delete", "move", "copy" },
            ["manage_asset"] = new HashSet<string> { "delete", "move", "copy" }
        };

        #endregion

        #region 公开属性

        /// <summary>
        /// 当前会话中的文件变更记录数量。
        /// </summary>
        public int RecordCount => _records.Count;

        /// <summary>
        /// 是否有文件变更。
        /// </summary>
        public bool HasChanges => _records.Count > 0;

        #endregion

        #region 快照

        /// <summary>
        /// 在工具执行前，解析所有 tool_calls 的参数，提取可能被修改的文件路径，
        /// 记录这些文件的当前行数作为快照。
        /// </summary>
        /// <param name="toolCalls">LLM 返回的工具调用列表</param>
        public void SnapshotBeforeExecution(List<ToolCall> toolCalls)
        {
            _lineCountSnapshots.Clear();

            if (toolCalls == null) return;

            foreach (var tc in toolCalls)
            {
                var toolName = tc.Function?.Name;
                if (string.IsNullOrEmpty(toolName)) continue;

                // 只处理已知的文件修改工具
                if (!TrackedToolActions.ContainsKey(toolName)) continue;

                try
                {
                    var args = ParseArguments(tc.Function?.Arguments);
                    if (args == null) continue;

                    var action = args["action"]?.ToString()?.ToLowerInvariant();
                    if (string.IsNullOrEmpty(action)) continue;

                    if (!TrackedToolActions[toolName].Contains(action)) continue;

                    // 提取文件路径并记录行数
                    var paths = ExtractTargetPaths(toolName, action, args);
                    foreach (var path in paths)
                    {
                        SnapshotFile(path);
                    }
                }
                catch (Exception ex)
                {
                    AgentCoreLog.Warning($"[AgentCore] FileChangeTracker: Failed to snapshot for {toolName}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 记录单个文件的行数快照。
        /// </summary>
        /// <param name="relativePath">文件相对路径</param>
        private void SnapshotFile(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath)) return;

            var absolutePath = Path.GetFullPath(relativePath);
            if (_lineCountSnapshots.ContainsKey(absolutePath)) return;

            if (File.Exists(absolutePath))
            {
                try
                {
                    var lineCount = CountLines(absolutePath);
                    _lineCountSnapshots[absolutePath] = lineCount;
                }
                catch
                {
                    // 文件可能被锁定，记录为 0
                    _lineCountSnapshots[absolutePath] = 0;
                }
            }
            else
            {
                // 文件不存在（可能是新建），记录为 -1 表示不存在
                _lineCountSnapshots[absolutePath] = -1;
            }
        }

        #endregion

        #region 追踪

        /// <summary>
        /// 从工具调用列表中追踪文件变更。
        /// 在工具执行完成后调用，解析每个工具调用的参数来确定文件变更。
        /// </summary>
        /// <param name="toolCalls">LLM 返回的工具调用列表</param>
        /// <param name="results">工具执行结果列表（与 toolCalls 一一对应）</param>
        public void TrackFromToolCalls(List<ToolCall> toolCalls, List<Tools.ToolCallResult> results)
        {
            if (toolCalls == null || results == null) return;

            foreach (var result in results)
            {
                // 只追踪成功的工具调用
                if (!result.Result.Success) continue;

                var toolName = result.ToolName;
                if (!TrackedToolActions.ContainsKey(toolName)) continue;

                try
                {
                    var args = ParseArguments(result.ToolCall.Function?.Arguments);
                    if (args == null) continue;

                    var action = args["action"]?.ToString()?.ToLowerInvariant();
                    if (string.IsNullOrEmpty(action)) continue;

                    if (!TrackedToolActions[toolName].Contains(action)) continue;

                    // 根据工具和 action 创建变更记录
                    var records = CreateChangeRecords(toolName, action, args);
                    _records.AddRange(records);
                }
                catch (Exception ex)
                {
                    AgentCoreLog.Warning($"[AgentCore] FileChangeTracker: Failed to track {toolName}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 根据工具名、action 和参数创建文件变更记录。
        /// </summary>
        private List<FileChangeRecord> CreateChangeRecords(string toolName, string action, JObject args)
        {
            var records = new List<FileChangeRecord>();

            switch (toolName)
            {
                case "manage_script":
                    records.AddRange(CreateManageScriptRecords(action, args));
                    break;

                case "manage_file":
                    records.AddRange(CreateManageFileRecords(action, args));
                    break;

                case "manage_asset":
                    records.AddRange(CreateManageAssetRecords(action, args));
                    break;
            }

            return records;
        }

        /// <summary>
        /// 为 manage_script 工具创建变更记录。
        /// </summary>
        private List<FileChangeRecord> CreateManageScriptRecords(string action, JObject args)
        {
            var records = new List<FileChangeRecord>();
            var path = args["path"]?.ToString();
            if (string.IsNullOrEmpty(path)) return records;

            var changeType = action switch
            {
                "create" => FileChangeType.Created,
                "delete" => FileChangeType.Deleted,
                "write" => DetermineWriteChangeType(path),
                "add_method" => FileChangeType.Modified,
                "add_field" => FileChangeType.Modified,
                _ => FileChangeType.Modified
            };

            var (added, removed) = CalculateLineChanges(path, changeType);

            records.Add(new FileChangeRecord
            {
                FilePath = NormalizePath(path),
                ChangeType = changeType,
                ToolName = "manage_script",
                Action = action,
                LinesAdded = added,
                LinesRemoved = removed,
                Timestamp = DateTime.UtcNow
            });

            return records;
        }

        /// <summary>
        /// 为 manage_file 工具创建变更记录。
        /// </summary>
        private List<FileChangeRecord> CreateManageFileRecords(string action, JObject args)
        {
            var records = new List<FileChangeRecord>();

            switch (action)
            {
                case "write_file":
                {
                    var path = args["path"]?.ToString();
                    if (string.IsNullOrEmpty(path)) break;

                    var changeType = DetermineWriteChangeType(path);
                    var (added, removed) = CalculateLineChanges(path, changeType);

                    records.Add(new FileChangeRecord
                    {
                        FilePath = NormalizePath(path),
                        ChangeType = changeType,
                        ToolName = "manage_file",
                        Action = action,
                        LinesAdded = added,
                        LinesRemoved = removed,
                        Timestamp = DateTime.UtcNow
                    });
                    break;
                }

                case "delete":
                {
                    var path = args["path"]?.ToString();
                    if (string.IsNullOrEmpty(path)) break;

                    var (added, removed) = CalculateLineChanges(path, FileChangeType.Deleted);

                    records.Add(new FileChangeRecord
                    {
                        FilePath = NormalizePath(path),
                        ChangeType = FileChangeType.Deleted,
                        ToolName = "manage_file",
                        Action = action,
                        LinesAdded = added,
                        LinesRemoved = removed,
                        Timestamp = DateTime.UtcNow
                    });
                    break;
                }

                case "move":
                {
                    var source = args["source"]?.ToString();
                    var destination = args["destination"]?.ToString();

                    if (!string.IsNullOrEmpty(source))
                    {
                        records.Add(new FileChangeRecord
                        {
                            FilePath = NormalizePath(source),
                            ChangeType = FileChangeType.Moved,
                            ToolName = "manage_file",
                            Action = action,
                            LinesAdded = 0,
                            LinesRemoved = 0,
                            Timestamp = DateTime.UtcNow
                        });
                    }

                    if (!string.IsNullOrEmpty(destination))
                    {
                        records.Add(new FileChangeRecord
                        {
                            FilePath = NormalizePath(destination),
                            ChangeType = FileChangeType.Moved,
                            ToolName = "manage_file",
                            Action = action,
                            LinesAdded = 0,
                            LinesRemoved = 0,
                            Timestamp = DateTime.UtcNow
                        });
                    }
                    break;
                }

                case "copy":
                {
                    var source = args["source"]?.ToString();
                    var destination = args["destination"]?.ToString();

                    if (!string.IsNullOrEmpty(destination))
                    {
                        var (added, _) = CalculateLineChanges(destination, FileChangeType.Copied);

                        records.Add(new FileChangeRecord
                        {
                            FilePath = NormalizePath(destination),
                            ChangeType = FileChangeType.Copied,
                            ToolName = "manage_file",
                            Action = action,
                            LinesAdded = added,
                            LinesRemoved = 0,
                            Timestamp = DateTime.UtcNow
                        });
                    }
                    break;
                }
            }

            return records;
        }

        /// <summary>
        /// 为 manage_asset 工具创建变更记录。
        /// </summary>
        private List<FileChangeRecord> CreateManageAssetRecords(string action, JObject args)
        {
            var records = new List<FileChangeRecord>();

            switch (action)
            {
                case "delete":
                {
                    var path = args["path"]?.ToString();
                    if (string.IsNullOrEmpty(path)) break;

                    records.Add(new FileChangeRecord
                    {
                        FilePath = NormalizePath(path),
                        ChangeType = FileChangeType.Deleted,
                        ToolName = "manage_asset",
                        Action = action,
                        LinesAdded = 0,
                        LinesRemoved = 0,
                        Timestamp = DateTime.UtcNow
                    });
                    break;
                }

                case "move":
                {
                    var path = args["path"]?.ToString();
                    var newPath = args["new_path"]?.ToString();

                    if (!string.IsNullOrEmpty(path))
                    {
                        records.Add(new FileChangeRecord
                        {
                            FilePath = NormalizePath(path),
                            ChangeType = FileChangeType.Moved,
                            ToolName = "manage_asset",
                            Action = action,
                            LinesAdded = 0,
                            LinesRemoved = 0,
                            Timestamp = DateTime.UtcNow
                        });
                    }

                    if (!string.IsNullOrEmpty(newPath))
                    {
                        records.Add(new FileChangeRecord
                        {
                            FilePath = NormalizePath(newPath),
                            ChangeType = FileChangeType.Moved,
                            ToolName = "manage_asset",
                            Action = action,
                            LinesAdded = 0,
                            LinesRemoved = 0,
                            Timestamp = DateTime.UtcNow
                        });
                    }
                    break;
                }

                case "copy":
                {
                    var path = args["path"]?.ToString();
                    var newPath = args["new_path"]?.ToString();

                    if (!string.IsNullOrEmpty(newPath))
                    {
                        records.Add(new FileChangeRecord
                        {
                            FilePath = NormalizePath(newPath),
                            ChangeType = FileChangeType.Copied,
                            ToolName = "manage_asset",
                            Action = action,
                            LinesAdded = 0,
                            LinesRemoved = 0,
                            Timestamp = DateTime.UtcNow
                        });
                    }
                    break;
                }
            }

            return records;
        }

        #endregion

        #region 查询

        /// <summary>
        /// 获取所有原始变更记录。
        /// </summary>
        /// <returns>变更记录列表（按时间顺序）</returns>
        public IReadOnlyList<FileChangeRecord> GetAllRecords()
        {
            return _records;
        }

        /// <summary>
        /// 获取合并后的文件变更摘要列表。
        /// 同一文件的多次变更合并为一条摘要，累计增减行数。
        /// </summary>
        /// <returns>文件变更摘要列表（按最后修改时间倒序）</returns>
        public List<FileChangeSummary> GetSummaries()
        {
            var summaryMap = new Dictionary<string, FileChangeSummary>(StringComparer.OrdinalIgnoreCase);

            foreach (var record in _records)
            {
                if (!summaryMap.TryGetValue(record.FilePath, out var summary))
                {
                    summary = new FileChangeSummary
                    {
                        FilePath = record.FilePath,
                        ChangeType = record.ChangeType,
                        TotalLinesAdded = 0,
                        TotalLinesRemoved = 0,
                        LastModified = record.Timestamp
                    };
                    summaryMap[record.FilePath] = summary;
                }

                summary.TotalLinesAdded += record.LinesAdded;
                summary.TotalLinesRemoved += record.LinesRemoved;
                summary.ChangeType = record.ChangeType; // 取最后一次变更类型
                summary.LastModified = record.Timestamp;
            }

            return summaryMap.Values
                .OrderByDescending(s => s.LastModified)
                .ToList();
        }

        /// <summary>
        /// 获取总新增行数。
        /// </summary>
        public int TotalLinesAdded => _records.Sum(r => r.LinesAdded);

        /// <summary>
        /// 获取总删除行数。
        /// </summary>
        public int TotalLinesRemoved => _records.Sum(r => r.LinesRemoved);

        /// <summary>
        /// 获取变更文件数量（去重后）。
        /// </summary>
        public int ChangedFileCount => _records
            .Select(r => r.FilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        #endregion

        #region 清空

        /// <summary>
        /// 清空所有变更记录和快照。
        /// 在会话切换或重置时调用。
        /// </summary>
        public void Clear()
        {
            _records.Clear();
            _lineCountSnapshots.Clear();
        }

        #endregion

        #region Domain Reload 序列化

        /// <summary>
        /// 将当前所有文件变更记录序列化为 JSON 字符串。
        /// 用于在 Domain Reload 前保存到 <see cref="DomainReloadState"/>。
        /// </summary>
        /// <returns>JSON 字符串，如果没有记录则返回 null</returns>
        public string SerializeToJson()
        {
            if (_records.Count == 0)
                return null;

            try
            {
                var array = new JArray();
                foreach (var record in _records)
                {
                    var obj = new JObject
                    {
                        ["filePath"] = record.FilePath,
                        ["changeType"] = (int)record.ChangeType,
                        ["toolName"] = record.ToolName,
                        ["action"] = record.Action,
                        ["linesAdded"] = record.LinesAdded,
                        ["linesRemoved"] = record.LinesRemoved,
                        ["timestamp"] = record.Timestamp.ToString("o")
                    };
                    array.Add(obj);
                }
                return array.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore] FileChangeTracker.SerializeToJson failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 从 JSON 字符串恢复文件变更记录。
        /// 用于在 Domain Reload 后从 <see cref="DomainReloadState"/> 恢复数据。
        /// 恢复的记录会追加到现有记录之后（不会清空现有记录）。
        /// </summary>
        /// <param name="json">JSON 字符串（由 <see cref="SerializeToJson"/> 生成）</param>
        /// <returns>恢复的记录数量</returns>
        public int RestoreFromJson(string json)
        {
            if (string.IsNullOrEmpty(json))
                return 0;

            try
            {
                var array = JArray.Parse(json);
                int restoredCount = 0;

                foreach (var token in array)
                {
                    var obj = token as JObject;
                    if (obj == null) continue;

                    var record = new FileChangeRecord
                    {
                        FilePath = obj["filePath"]?.ToString() ?? string.Empty,
                        ChangeType = (FileChangeType)(obj["changeType"]?.Value<int>() ?? 0),
                        ToolName = obj["toolName"]?.ToString() ?? string.Empty,
                        Action = obj["action"]?.ToString() ?? string.Empty,
                        LinesAdded = obj["linesAdded"]?.Value<int>() ?? 0,
                        LinesRemoved = obj["linesRemoved"]?.Value<int>() ?? 0,
                        Timestamp = DateTime.TryParse(obj["timestamp"]?.ToString(), out var ts)
                            ? ts
                            : DateTime.UtcNow
                    };

                    _records.Add(record);
                    restoredCount++;
                }

                return restoredCount;
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore] FileChangeTracker.RestoreFromJson failed: {ex.Message}");
                return 0;
            }
        }

        #endregion

        #region 辅助方法

        /// <summary>
        /// 解析工具参数 JSON 字符串。
        /// </summary>
        private static JObject ParseArguments(string arguments)
        {
            if (string.IsNullOrEmpty(arguments)) return null;

            try
            {
                return JObject.Parse(arguments);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 从工具参数中提取目标文件路径列表。
        /// </summary>
        private static List<string> ExtractTargetPaths(string toolName, string action, JObject args)
        {
            var paths = new List<string>();

            switch (toolName)
            {
                case "manage_script":
                {
                    var path = args["path"]?.ToString();
                    if (!string.IsNullOrEmpty(path)) paths.Add(path);
                    break;
                }

                case "manage_file":
                {
                    switch (action)
                    {
                        case "write_file":
                        case "delete":
                        {
                            var path = args["path"]?.ToString();
                            if (!string.IsNullOrEmpty(path)) paths.Add(path);
                            break;
                        }
                        case "move":
                        case "copy":
                        {
                            var source = args["source"]?.ToString();
                            var destination = args["destination"]?.ToString();
                            if (!string.IsNullOrEmpty(source)) paths.Add(source);
                            if (!string.IsNullOrEmpty(destination)) paths.Add(destination);
                            break;
                        }
                    }
                    break;
                }

                case "manage_asset":
                {
                    var path = args["path"]?.ToString();
                    var newPath = args["new_path"]?.ToString();
                    if (!string.IsNullOrEmpty(path)) paths.Add(path);
                    if (!string.IsNullOrEmpty(newPath)) paths.Add(newPath);
                    break;
                }
            }

            return paths;
        }

        /// <summary>
        /// 判断 write 操作是创建还是修改（基于快照中的记录）。
        /// </summary>
        private FileChangeType DetermineWriteChangeType(string path)
        {
            if (string.IsNullOrEmpty(path)) return FileChangeType.Modified;

            var absolutePath = Path.GetFullPath(path);
            if (_lineCountSnapshots.TryGetValue(absolutePath, out var snapshotLines))
            {
                // -1 表示快照时文件不存在，说明是新建
                return snapshotLines == -1 ? FileChangeType.Created : FileChangeType.Modified;
            }

            // 没有快照记录，检查文件是否存在
            return File.Exists(absolutePath) ? FileChangeType.Modified : FileChangeType.Created;
        }

        /// <summary>
        /// 计算文件的增减行数。
        /// 基于执行前快照和执行后的实际文件行数对比。
        /// </summary>
        /// <param name="path">文件相对路径</param>
        /// <param name="changeType">变更类型</param>
        /// <returns>(新增行数, 删除行数)</returns>
        private (int added, int removed) CalculateLineChanges(string path, FileChangeType changeType)
        {
            if (string.IsNullOrEmpty(path)) return (0, 0);

            var absolutePath = Path.GetFullPath(path);

            switch (changeType)
            {
                case FileChangeType.Created:
                {
                    // 新建文件：新增行数 = 文件总行数
                    var newLines = GetCurrentLineCount(absolutePath);
                    return (newLines, 0);
                }

                case FileChangeType.Deleted:
                {
                    // 删除文件：删除行数 = 快照中的行数
                    if (_lineCountSnapshots.TryGetValue(absolutePath, out var oldLines) && oldLines > 0)
                    {
                        return (0, oldLines);
                    }
                    return (0, 0);
                }

                case FileChangeType.Modified:
                {
                    // 修改文件：对比快照和当前行数
                    var newLines = GetCurrentLineCount(absolutePath);
                    if (_lineCountSnapshots.TryGetValue(absolutePath, out var oldLines) && oldLines >= 0)
                    {
                        var diff = newLines - oldLines;
                        return (Math.Max(0, diff), Math.Max(0, -diff));
                    }
                    // 没有快照，无法精确计算，返回当前行数作为新增
                    return (newLines, 0);
                }

                case FileChangeType.Copied:
                {
                    // 复制文件：新增行数 = 新文件总行数
                    var newLines = GetCurrentLineCount(absolutePath);
                    return (newLines, 0);
                }

                case FileChangeType.Moved:
                default:
                    return (0, 0);
            }
        }

        /// <summary>
        /// 获取文件当前的行数。
        /// </summary>
        private static int GetCurrentLineCount(string absolutePath)
        {
            try
            {
                if (File.Exists(absolutePath))
                {
                    return CountLines(absolutePath);
                }
            }
            catch
            {
                // 文件可能被锁定
            }
            return 0;
        }

        /// <summary>
        /// 流式统计文件行数。
        /// <para>
        /// 相比 <c>File.ReadAllLines(path).Length</c>，此实现不会把整个文件内容读入内存后
        /// 再分配一个字符串数组：<see cref="StreamReader.ReadLine"/> 逐行读取、只累加计数，
        /// 内存占用降至 O(1)，避免大文件（数千行源码）在 LLM 每轮 tool call 前后扫描时的分配开销。
        /// </para>
        /// </summary>
        private static int CountLines(string absolutePath)
        {
            using var reader = new StreamReader(absolutePath);
            int count = 0;
            while (reader.ReadLine() != null) count++;
            return count;
        }

        /// <summary>
        /// 规范化文件路径（统一使用正斜杠，移除多余的路径分隔符）。
        /// </summary>
        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            return path.Replace('\\', '/').TrimStart('/');
        }

        #endregion
    }

    #endregion
}
