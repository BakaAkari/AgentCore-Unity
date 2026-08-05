using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace AgentCore.Editor.Utils
{
    /// <summary>
    /// 慢操作自动落盘诊断（BUG1 预防机制）。
    /// <para>
    /// 背景：初始化偶发变慢的问题极难人工复现——用户需要在卡顿瞬间手动导出诊断包，
    /// 命中率很低。这个工具把"事后翻日志"改成"超过阈值就自动写一份持久化报告"，
    /// 不依赖用户在正确时机采取动作，也不受 Unity Console 被清空/截断影响。
    /// </para>
    /// <para>
    /// 报告写入 <c>&lt;project&gt;/Library/AgentCore/slow_init_diagnostics.log</c>（追加模式），
    /// Library 目录默认不纳入版本控制，路径稳定，用户随时可以直接把这个文件发出来。
    /// </para>
    /// </summary>
    public static class SlowOperationDiagnostics
    {
        private static readonly object WriteLock = new object();

        /// <summary>
        /// 若耗时超过阈值，追加一条诊断记录到磁盘。未超过阈值时不写入（避免正常路径产生噪音文件）。
        /// </summary>
        /// <param name="operationName">操作名称，如 "InitializeAsync"。</param>
        /// <param name="elapsedMs">实际耗时（毫秒）。</param>
        /// <param name="thresholdMs">触发落盘的阈值（毫秒）。</param>
        /// <param name="breakdown">可选的分段耗时明细（多行文本）。</param>
        public static void ReportIfSlow(string operationName, long elapsedMs, long thresholdMs, string breakdown = null)
        {
            if (elapsedMs < thresholdMs) return;

            try
            {
                var logDir = Path.Combine(Application.dataPath, "..", "Library", "AgentCore");
                Directory.CreateDirectory(logDir);
                var logPath = Path.Combine(logDir, "slow_init_diagnostics.log");

                var sb = new StringBuilder();
                sb.AppendLine("==================================================");
                sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] SLOW: {operationName} took {elapsedMs}ms (threshold={thresholdMs}ms)");
                sb.AppendLine($"Unity: {Application.unityVersion} | Project: {Application.dataPath}");
                if (!string.IsNullOrEmpty(breakdown))
                {
                    sb.AppendLine("--- Breakdown ---");
                    sb.AppendLine(breakdown);
                }
                sb.AppendLine();

                lock (WriteLock)
                {
                    File.AppendAllText(logPath, sb.ToString(), Encoding.UTF8);
                }

                AgentCoreLog.Warning($"[AgentCore] Slow operation detected ({operationName}: {elapsedMs}ms). " +
                    $"Diagnostic report appended to: {logPath}");
            }
            catch (Exception ex)
            {
                // 诊断落盘本身绝不能影响主流程
                AgentCoreLog.Warning($"[AgentCore] Failed to write slow-operation diagnostic report: {ex.Message}");
            }
        }
    }
}
