using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using AgentCore.Editor.Core;
using AgentCore.Editor.Utils;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Session
{
    /// <summary>
    /// 会话导出器 — 将 <see cref="SessionData"/> 导出为 JSON 文件，或导出为包含
    /// 会话数据 + Unity Editor 诊断信息的完整诊断包（.zip）。
    /// <para>
    /// v1.14.6 起：原 Markdown 导出入口（人类可读格式，仅含对话文本）实际使用价值有限——
    /// 排查 bug 时真正需要的是会话 JSON 以外的运行环境上下文（Editor.log、系统信息、
    /// Domain Reload 中断状态等），这些信息此前完全没有导出入口，只能靠用户手工翻找、
    /// 甚至像 O 盘加密空间那种场景一样完全拿不到。因此把原 Markdown 入口改造为
    /// "完整诊断包"导出，一次性打包：
    /// <list type="bullet">
    ///   <item><b>session.json</b>: 完整会话数据（含 turns/tool_calls/压缩统计）</item>
    ///   <item><b>Editor.log</b> / <b>Editor-prev.log</b>: Unity Editor 日志（若存在）</item>
    ///   <item><b>system_info.json</b>: Unity 版本、OS、编译状态、Scripting Backend 等</item>
    ///   <item><b>domain_reload_state.json</b>: 最近一次 Domain Reload 中断状态快照
    ///     （诊断"卡死"/"中断"类问题的关键线索，含本次新增的 [DIAG] 日志关联字段）</item>
    ///   <item><b>manifest.txt</b>: 导出清单，记录每个文件是否成功打包及原因</item>
    /// </list>
    /// 任一附加文件读取失败都不会中断整体导出——manifest 会记录失败原因，
    /// 保证核心的 session.json 始终能导出成功（fail-soft，不是 fail-fast）。
    /// </para>
    /// </summary>
    public static class SessionExporter
    {
        /// <summary>
        /// 导出格式枚举。
        /// </summary>
        public enum ExportFormat
        {
            /// <summary>完整诊断包（.zip）：会话 JSON + Editor.log + 系统信息 + Domain Reload 状态</summary>
            DiagnosticBundle,

            /// <summary>JSON 格式（.json）：仅会话数据</summary>
            Json
        }

        /// <summary>
        /// 将会话数据导出为 JSON 字符串。
        /// </summary>
        /// <param name="session">会话数据</param>
        /// <returns>格式化的 JSON 字符串</returns>
        /// <exception cref="ArgumentNullException">session 为 null 时抛出</exception>
        public static string ExportToJson(SessionData session)
        {
            if (session == null)
                throw new ArgumentNullException(nameof(session));

            return JsonHelper.Serialize(session, pretty: true);
        }

        /// <summary>
        /// 将会话数据导出到文件。<see cref="ExportFormat.DiagnosticBundle"/> 导出为 .zip，
        /// <see cref="ExportFormat.Json"/> 导出为纯 .json。
        /// </summary>
        /// <param name="session">会话数据</param>
        /// <param name="filePath">目标文件路径</param>
        /// <param name="format">导出格式</param>
        /// <returns>是否导出成功</returns>
        public static bool ExportToFile(SessionData session, string filePath, ExportFormat format)
        {
            if (session == null || string.IsNullOrEmpty(filePath))
                return false;

            try
            {
                // 确保目录存在
                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                if (format == ExportFormat.Json)
                {
                    File.WriteAllText(filePath, ExportToJson(session), Encoding.UTF8);
                    return true;
                }

                if (format == ExportFormat.DiagnosticBundle)
                {
                    return ExportDiagnosticBundle(session, filePath);
                }

                throw new ArgumentOutOfRangeException(nameof(format));
            }
            catch (Exception ex)
            {
                AgentCoreLog.Error($"[AgentCore] Failed to export session: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 获取导出文件的默认文件名。
        /// </summary>
        /// <param name="session">会话数据</param>
        /// <param name="format">导出格式</param>
        /// <returns>建议的文件名</returns>
        public static string GetDefaultFileName(SessionData session, ExportFormat format)
        {
            var title = session?.Title ?? "conversation";
            // 清理文件名中的非法字符
            var safeName = SanitizeFileName(title);
            var date = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var ext = format == ExportFormat.DiagnosticBundle ? "zip" : "json";
            var suffix = format == ExportFormat.DiagnosticBundle ? "_diag" : "";
            return $"AgentCore_{safeName}{suffix}_{date}.{ext}";
        }

        #region 诊断包构建

        /// <summary>
        /// 构建并写出完整诊断包（.zip）。
        /// 打包顺序：session.json（必须成功）→ Editor.log / Editor-prev.log（尽力而为）→
        /// system_info.json（尽力而为）→ domain_reload_state.json（尽力而为）→ manifest.txt（记录全过程）。
        /// </summary>
        private static bool ExportDiagnosticBundle(SessionData session, string filePath)
        {
            var manifest = new StringBuilder();
            manifest.AppendLine("AgentCore 诊断包导出清单");
            manifest.AppendLine($"导出时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            manifest.AppendLine($"会话: {session.Title ?? "(未命名)"} (ID: {session.Id})");
            manifest.AppendLine(new string('-', 40));

            // 若目标文件已存在，先删除
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            // 直接用 ZipArchive 包裹 FileStream，而非 ZipFile.Open 便捷方法——
            // ZipArchive 保证在 .NET Standard 2.0 API Compatibility Level 下可用，
            // 不依赖历史上曾拆分到 System.IO.Compression.FileSystem 的 ZipFile 静态类，
            // 兼容性更可控（Unity 项目的 API Compatibility Level 因项目而异）。
            using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(fileStream, ZipArchiveMode.Create))
            {
                // 1. session.json —— 核心数据，失败即整体失败
                AddTextEntry(zip, "session.json", ExportToJson(session));
                manifest.AppendLine("[OK] session.json — 完整会话数据");

                // 2. Editor.log / Editor-prev.log —— 尽力而为
                TryAddLogFile(zip, manifest, "Editor.log", isPrev: false);
                TryAddLogFile(zip, manifest, "Editor-prev.log", isPrev: true);

                // 3. system_info.json —— 尽力而为
                TryAddSystemInfo(zip, manifest);

                // 4. domain_reload_state.json —— 尽力而为
                TryAddDomainReloadState(zip, manifest);

                // 5. manifest.txt 最后写入（要包含前面所有条目的记录）
                AddTextEntry(zip, "manifest.txt", manifest.ToString());
            }

            return true;
        }

        private static void AddTextEntry(ZipArchive zip, string entryName, string content)
        {
            var entry = zip.CreateEntry(entryName, System.IO.Compression.CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(content);
        }

        private static void AddFileEntry(ZipArchive zip, string entryName, string sourceFilePath)
        {
            // 用共享读方式打开源文件（Unity 可能仍持有 Editor.log 的写锁），
            // 再流式写入 zip entry，避免 ZipFile.CreateEntryFromFile 因文件被占用而失败。
            var entry = zip.CreateEntry(entryName, System.IO.Compression.CompressionLevel.Optimal);
            using var source = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var dest = entry.Open();
            source.CopyTo(dest);
        }

        /// <summary>
        /// 尝试打包 Editor.log / Editor-prev.log。跨平台路径解析对齐
        /// <see cref="AgentCore.Editor.Tools.Native.Utility.ReadConsoleTool"/> 的 get_log_file 实现。
        /// </summary>
        private static void TryAddLogFile(ZipArchive zip, StringBuilder manifest, string fileName, bool isPrev)
        {
            try
            {
                var logPath = GetEditorLogPath(isPrev);
                if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath))
                {
                    manifest.AppendLine($"[SKIP] {fileName} — 文件不存在 (期望路径: {logPath ?? "unknown"})");
                    return;
                }

                AddFileEntry(zip, fileName, logPath);
                var sizeKb = new FileInfo(logPath).Length / 1024.0;
                manifest.AppendLine($"[OK] {fileName} — 来自 {logPath} ({sizeKb:F1} KB)");
            }
            catch (Exception ex)
            {
                manifest.AppendLine($"[FAIL] {fileName} — {ex.Message}");
            }
        }

        /// <summary>
        /// 打包系统/环境信息（Unity 版本、OS、编译状态、Scripting Backend 等）。
        /// 字段范围对齐 read_console 工具的 get_system_info action，便于交叉核对。
        /// </summary>
        private static void TryAddSystemInfo(ZipArchive zip, StringBuilder manifest)
        {
            try
            {
                var activeTarget = EditorUserBuildSettings.activeBuildTarget;
                var activeTargetGroup = BuildPipeline.GetBuildTargetGroup(activeTarget);

                var data = new JObject
                {
                    ["exportedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                    ["unityVersion"] = Application.unityVersion,
                    ["unityEditorPath"] = EditorApplication.applicationPath,
                    ["projectName"] = Application.productName,
                    ["projectPath"] = Path.GetFullPath(Application.dataPath + "/.."),
                    ["activeBuildTarget"] = activeTarget.ToString(),
                    ["activeBuildTargetGroup"] = activeTargetGroup.ToString(),
                    ["isPlaying"] = EditorApplication.isPlaying,
                    ["isCompiling"] = EditorApplication.isCompiling,
                    ["isPaused"] = EditorApplication.isPaused,
                    ["scriptCompilationFailed"] = EditorUtility.scriptCompilationFailed,
                    ["operatingSystem"] = SystemInfo.operatingSystem,
                    ["operatingSystemFamily"] = SystemInfo.operatingSystemFamily.ToString(),
                    ["processorType"] = SystemInfo.processorType,
                    ["processorCount"] = SystemInfo.processorCount,
                    ["systemMemorySize"] = SystemInfo.systemMemorySize,
                    ["graphicsDeviceName"] = SystemInfo.graphicsDeviceName,
                    ["scriptingBackend"] = PlayerSettings.GetScriptingBackend(activeTargetGroup).ToString(),
                    ["apiCompatibilityLevel"] = PlayerSettings.GetApiCompatibilityLevel(activeTargetGroup).ToString(),
                    ["dotNetVersion"] = Environment.Version.ToString(),
                    ["machineName"] = Environment.MachineName,
                    ["userName"] = Environment.UserName,
                };

                AddTextEntry(zip, "system_info.json", data.ToString(Newtonsoft.Json.Formatting.Indented));
                manifest.AppendLine("[OK] system_info.json — Unity/OS/编译环境信息");
            }
            catch (Exception ex)
            {
                manifest.AppendLine($"[FAIL] system_info.json — {ex.Message}");
            }
        }

        /// <summary>
        /// 打包最近一次 Domain Reload 中断状态快照（若从未发生过中断，字段多为空/默认值，
        /// 但仍打包以明确"未中断"这一事实本身也是诊断信息）。
        /// </summary>
        private static void TryAddDomainReloadState(ZipArchive zip, StringBuilder manifest)
        {
            try
            {
                var state = DomainReloadState.instance;
                var data = new JObject
                {
                    ["wasInterrupted"] = state.WasInterrupted,
                    ["interruptedSessionId"] = state.InterruptedSessionId,
                    ["interruptPhase"] = state.InterruptPhase.ToString(),
                    ["lastToolName"] = state.LastToolName,
                    ["interruptTimestamp"] = state.InterruptTimestamp,
                    ["hadPendingToolCalls"] = state.HadPendingToolCalls,
                    ["interruptedToolCallId"] = state.InterruptedToolCallId,
                    ["compilationSucceeded"] = state.CompilationSucceeded,
                    ["compilationErrors"] = state.CompilationErrors,
                    ["pendingUserMessage"] = state.PendingUserMessage,
                    ["lastAssistantContent"] = state.LastAssistantContent,
                    ["lastAssistantReasoning"] = state.LastAssistantReasoning,
                    ["toolResultCompressionSuccessCount"] = state.ToolResultCompressionSuccessCount,
                    ["conversationCompressionSuccessCount"] = state.ConversationCompressionSuccessCount,
                    ["totalTokensSaved"] = state.TotalTokensSaved,
                };

                AddTextEntry(zip, "domain_reload_state.json", data.ToString(Newtonsoft.Json.Formatting.Indented));
                manifest.AppendLine("[OK] domain_reload_state.json — 最近一次 Domain Reload 中断状态");
            }
            catch (Exception ex)
            {
                manifest.AppendLine($"[FAIL] domain_reload_state.json — {ex.Message}");
            }
        }

        /// <summary>
        /// 解析当前平台的 Unity Editor 日志路径。与
        /// <see cref="AgentCore.Editor.Tools.Native.Utility.ReadConsoleTool"/> 中同名私有方法逻辑一致
        /// （该方法为 private，无法直接复用，此处保持路径规则同步：Windows 下 Editor-prev.log
        /// 与 Editor.log 同目录）。
        /// </summary>
        private static string GetEditorLogPath(bool isPrev)
        {
            string fileName = isPrev ? "Editor-prev.log" : "Editor.log";
            string os = SystemInfo.operatingSystemFamily.ToString();
            if (os == "Windows")
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(localAppData, "Unity", "Editor", fileName);
            }
            else if (os == "MacOSX")
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
                return Path.Combine(home, "Library", "Logs", "Unity", fileName);
            }
            else
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
                return Path.Combine(home, ".config", "unity3d", fileName);
            }
        }

        #endregion

        #region 私有方法

        /// <summary>
        /// 清理文件名中的非法字符。
        /// </summary>
        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "conversation";

            var invalidChars = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
            {
                if (Array.IndexOf(invalidChars, c) < 0 && c != ' ')
                {
                    sb.Append(c);
                }
                else if (c == ' ')
                {
                    sb.Append('_');
                }
            }

            var result = sb.ToString();
            // 限制长度
            if (result.Length > 50)
            {
                result = result.Substring(0, 50);
            }

            return string.IsNullOrEmpty(result) ? "conversation" : result;
        }

        #endregion
    }
}
