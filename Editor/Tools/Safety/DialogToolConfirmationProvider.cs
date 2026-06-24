using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Tools.Safety
{
    /// <summary>
    /// G.1.c 默认实现：基于 <see cref="EditorUtility.DisplayDialog(string, string, string, string)"/> 的阻塞式确认提供者。
    /// <para>
    /// 工作原理：
    /// <list type="number">
    ///   <item>通过 <see cref="EditorApplication.delayCall"/> 将弹窗调度到 Unity 主线程；</item>
    ///   <item>构造人类可读的消息文本（Title / Description / Reasons / Targets / ParameterSummary）；</item>
    ///   <item>等待用户点击 "Approve" / "Reject"，再用 <see cref="TaskCompletionSource{TResult}"/> 把布尔结果回送到调用方；</item>
    ///   <item>支持 <paramref name="ct"/> 取消与 60s 超时（超时视为拒绝）。</item>
    /// </list>
    /// </para>
    /// <para>
    /// 已知限制（G.1.c 阶段可接受，G.5 改造）：
    /// <list type="bullet">
    ///   <item><c>DisplayDialog</c> 是模态阻塞，弹窗期间 Editor UI 无法响应；</item>
    ///   <item>无法在弹窗期间显示工具调用进度，所有"批准/拒绝"决策都是一次性的；</item>
    ///   <item>批量工具调用会顺序弹出多个对话框，没有"全部批准"选项；</item>
    ///   <item>无 session-level 记忆，每次都重新询问（D4 决策）。</item>
    /// </list>
    /// </para>
    /// </summary>
    public sealed class DialogToolConfirmationProvider : IToolConfirmationProvider
    {
        /// <summary>日志前缀</summary>
        private const string LogPrefix = "[AgentCore] DialogConfirmation: ";

        /// <summary>默认超时（秒）。超时视为拒绝。</summary>
        public const int DefaultTimeoutSeconds = 60;

        /// <summary>"批准" 按钮文本（OK）。</summary>
        private const string ApproveButton = "Approve";

        /// <summary>"拒绝" 按钮文本（Cancel）。</summary>
        private const string RejectButton = "Reject";

        /// <summary>消息中 Reasons 最大展示条数</summary>
        private const int MaxReasonsDisplay = 6;

        /// <summary>消息中 Targets 最大展示条数</summary>
        private const int MaxTargetsDisplay = 6;

        /// <summary>消息中 ParameterSummary 最大展示条数</summary>
        private const int MaxParamsDisplay = 8;

        /// <summary>单条参数值的最大展示长度（超过截断）</summary>
        private const int MaxParamValueLength = 200;

        /// <summary>本实例的超时（秒），默认 60s。</summary>
        private readonly int _timeoutSeconds;

        /// <summary>
        /// 创建对话框确认提供者。
        /// </summary>
        /// <param name="timeoutSeconds">弹窗等待超时（秒），<c>&lt;= 0</c> 表示不超时。默认 60s。</param>
        public DialogToolConfirmationProvider(int timeoutSeconds = DefaultTimeoutSeconds)
        {
            _timeoutSeconds = timeoutSeconds;
        }

        /// <inheritdoc />
        public Task<bool> RequestConfirmationAsync(ToolConfirmationRequest request, CancellationToken ct)
        {
            if (request == null)
            {
                Debug.LogWarning($"{LogPrefix}Null request → auto reject.");
                return Task.FromResult(false);
            }

            // 取消令牌已在请求时取消 → 直接拒绝
            if (ct.IsCancellationRequested)
            {
                Debug.Log($"{LogPrefix}Cancellation requested before prompt; auto reject '{request.ToolName}'.");
                return Task.FromResult(false);
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            // 1) 注册取消：取消 → 拒绝（弹窗本身无法主动关闭，但调用方会忽略后续结果）
            CancellationTokenRegistration ctReg = ct.Register(() =>
            {
                if (tcs.TrySetResult(false))
                {
                    Debug.Log($"{LogPrefix}Confirmation cancelled by token; treated as reject for '{request.ToolName}'.");
                }
            });

            // 2) 超时：超时 → 拒绝
            CancellationTokenSource timeoutCts = null;
            if (_timeoutSeconds > 0)
            {
                timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_timeoutSeconds));
                timeoutCts.Token.Register(() =>
                {
                    if (tcs.TrySetResult(false))
                    {
                        Debug.LogWarning(
                            $"{LogPrefix}Confirmation timeout after {_timeoutSeconds}s; auto reject '{request.ToolName}'.");
                    }
                });
            }

            string title = BuildTitle(request);
            string message = BuildMessage(request);

            // 3) 调度到 Unity 主线程展示对话框
            EditorApplication.delayCall += () =>
            {
                // 已经因 cancel/timeout 结束 → 不再弹窗，避免 ghost 对话框
                if (tcs.Task.IsCompleted)
                {
                    return;
                }

                bool approved;
                try
                {
                    approved = EditorUtility.DisplayDialog(title, message, ApproveButton, RejectButton);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"{LogPrefix}DisplayDialog failed: {ex.Message} → auto reject.");
                    tcs.TrySetResult(false);
                    return;
                }

                if (tcs.TrySetResult(approved))
                {
                    Debug.Log(
                        $"{LogPrefix}User {(approved ? "approved" : "rejected")} '{request.ToolName}'.");
                }
            };

            // 4) 清理 registration / timeoutCts
            tcs.Task.ContinueWith(_ =>
            {
                ctReg.Dispose();
                timeoutCts?.Dispose();
            }, TaskScheduler.Default);

            return tcs.Task;
        }

        // ---------------------------------------------------------------
        // 消息构造
        // ---------------------------------------------------------------

        private static string BuildTitle(ToolConfirmationRequest request)
        {
            // 控制总长度，避免 OS 标题栏截断
            const int maxTitleLen = 80;
            string t = string.IsNullOrEmpty(request.Title) ? request.ToolName : request.Title;
            t = $"AgentCore — Confirm Tool: {t}";
            return t.Length <= maxTitleLen ? t : t.Substring(0, maxTitleLen - 3) + "...";
        }

        private static string BuildMessage(ToolConfirmationRequest request)
        {
            var sb = new StringBuilder();
            sb.Append("Tool: ").AppendLine(request.ToolName);
            if (!string.IsNullOrEmpty(request.Action))
            {
                sb.Append("Action: ").AppendLine(request.Action);
            }

            // 风险概览
            var risk = request.Risk;
            sb.Append("Risk: ")
              .Append(risk.ToolRisk)
              .Append(" | PathRisk: ").Append(risk.PathRisk)
              .Append(" | Capabilities: ").Append(risk.Capabilities)
              .AppendLine();

            if (!string.IsNullOrEmpty(request.Description))
            {
                sb.AppendLine();
                sb.AppendLine("Description:");
                sb.AppendLine(Truncate(request.Description, 400));
            }

            // 触发原因
            if (request.Reasons != null && request.Reasons.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Why confirmation is required:");
                int shown = 0;
                foreach (var r in request.Reasons)
                {
                    if (shown >= MaxReasonsDisplay)
                    {
                        sb.Append("  ... (+").Append(request.Reasons.Count - shown).AppendLine(" more)");
                        break;
                    }
                    sb.Append("  - ").AppendLine(r);
                    shown++;
                }
            }

            // 受影响目标
            if (request.Targets != null && request.Targets.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Targets:");
                int shown = 0;
                foreach (var t in request.Targets)
                {
                    if (shown >= MaxTargetsDisplay)
                    {
                        sb.Append("  ... (+").Append(request.Targets.Count - shown).AppendLine(" more)");
                        break;
                    }
                    sb.Append("  - ").AppendLine(t);
                    shown++;
                }
            }

            // 参数摘要
            if (request.ParameterSummary != null && request.ParameterSummary.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Parameters:");
                int shown = 0;
                foreach (var kv in request.ParameterSummary)
                {
                    if (shown >= MaxParamsDisplay)
                    {
                        sb.Append("  ... (+").Append(request.ParameterSummary.Count - shown).AppendLine(" more)");
                        break;
                    }
                    sb.Append("  ").Append(kv.Key).Append(" = ")
                      .AppendLine(Truncate(kv.Value, MaxParamValueLength));
                    shown++;
                }
            }

            sb.AppendLine();
            sb.AppendLine("Press \"Approve\" to execute this tool, or \"Reject\" to abort and inform the assistant.");
            return sb.ToString();
        }

        private static string Truncate(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Length <= maxLen ? s : s.Substring(0, maxLen) + "...";
        }
    }
}
