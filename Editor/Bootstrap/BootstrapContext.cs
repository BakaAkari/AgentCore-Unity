using System.Text;

namespace AgentCore.Editor.Bootstrap
{
    /// <summary>
    /// Bootstrap Files 加载后的上下文数据。
    /// 包含各层 Bootstrap 文件的内容，最终编译为 System Prompt。
    ///
    /// §3.3 条件化 Section 注入：
    /// - Core sections (永驻 system prompt): SOUL + SOUL.ext + TOOLS 协调模式
    /// - Deferred sections (首轮注入): Active Tools List + Tool Decision Tree + PROJECT(auto) + PROJECT.md(user)
    ///
    /// 加载顺序：
    /// 1. SOUL.md — 角色定义（内置不可变）
    /// 1+. SOUL.ext.md — 行为规则扩展（用户可选，追加到 SOUL）
    /// 2. TOOLS.md — 工具协调模式 + 行为触发器（core）
    /// 2d. TOOLS Deferred — Active Tools List + Tool Decision Tree（deferred）
    /// 3. PROJECT.md — 项目上下文（自动收集，deferred）
    /// 3+. PROJECT.md（用户） — 项目约定与个人偏好（deferred）
    /// </summary>
    public class BootstrapContext
    {
        /// <summary>
        /// SOUL.md — 角色定义与核心原则（内置不可变）
        /// </summary>
        public string Soul { get; set; }

        /// <summary>
        /// SOUL.ext.md — 用户行为规则扩展（可选，追加到 SOUL 之后）
        /// </summary>
        public string SoulExtension { get; set; }

        /// <summary>
        /// TOOLS Core — 工具协调模式和行为触发器（永驻 system prompt）
        /// </summary>
        public string Tools { get; set; }

        /// <summary>
        /// TOOLS Deferred — Active Tools List + Tool Selection Decision Tree（延迟注入）
        /// </summary>
        public string ToolsDeferred { get; set; }

        /// <summary>
        /// PROJECT.md — 项目上下文（自动收集，延迟注入）
        /// </summary>
        public string Project { get; set; }

        /// <summary>
        /// Workspace — PROJECT.md 用户内容（用户可编辑，延迟注入）
        /// 包含项目约定（Project Conventions）和个人偏好（Personal Preferences）
        /// </summary>
        public string Workspace { get; set; }

        /// <summary>
        /// 将 Core sections 编译为 System Prompt 字符串（永驻 _messages[0]）。
        /// 仅包含：SOUL(+SOUL.ext) + TOOLS 协调模式。
        /// </summary>
        public string CompileSystemPrompt()
        {
            var sb = new StringBuilder();

            // 1. SOUL — 角色定义（必须）
            if (!string.IsNullOrEmpty(Soul))
            {
                sb.AppendLine(Soul);

                // 1+. SOUL 扩展（可选，追加）
                if (!string.IsNullOrEmpty(SoulExtension))
                {
                    sb.AppendLine();
                    sb.AppendLine(SoulExtension);
                }
            }

            // 2. TOOLS Core — 协调模式和行为触发器
            if (!string.IsNullOrEmpty(Tools))
            {
                sb.AppendLine("\n---\n");
                sb.AppendLine(Tools);
            }

            return sb.ToString();
        }

        /// <summary>
        /// 将 Deferred sections 编译为延迟注入字符串。
        /// 包含：Active Tools List + Tool Decision Tree + PROJECT(auto) + PROJECT.md(user)。
        /// 在会话首轮用户消息时作为 system message 注入。
        /// </summary>
        /// <returns>延迟注入内容，为空时表示无需注入。</returns>
        public string CompileDeferredContext()
        {
            var sb = new StringBuilder();

            // 2d. TOOLS Deferred — Active Tools List + Decision Tree
            if (!string.IsNullOrEmpty(ToolsDeferred))
            {
                sb.AppendLine(ToolsDeferred);
            }

            // 3. PROJECT — 项目上下文（自动生成）
            if (!string.IsNullOrEmpty(Project))
            {
                sb.AppendLine("\n---\n");
                sb.AppendLine("## 当前项目信息\n");
                sb.AppendLine(Project);
            }

            // 3+. WORKSPACE — 项目配置（用户可编辑）
            if (!string.IsNullOrEmpty(Workspace))
            {
                sb.AppendLine("\n---\n");
                sb.AppendLine("## 项目配置（来自 PROJECT.md）\n");
                sb.AppendLine(Workspace);
            }

            var result = sb.ToString().TrimEnd();
            return string.IsNullOrWhiteSpace(result) ? null : result;
        }

        /// <summary>
        /// 估算 Core System Prompt 的 token 数量。
        /// 使用近似算法：每 3 个字符约 1 个 token（中英文混合场景的经验值）。
        /// </summary>
        public int EstimateTokenCount()
        {
            var prompt = CompileSystemPrompt();
            if (string.IsNullOrEmpty(prompt)) return 0;

            return prompt.Length / 3;
        }

        /// <summary>
        /// 估算 Deferred Context 的 token 数量。
        /// </summary>
        public int EstimateDeferredTokenCount()
        {
            var deferred = CompileDeferredContext();
            if (string.IsNullOrEmpty(deferred)) return 0;

            return deferred.Length / 3;
        }
    }
}
