using System.Text;

namespace AgentCore.Editor.Bootstrap
{
    /// <summary>
    /// Bootstrap Files 加载后的上下文数据。
    /// 包含各层 Bootstrap 文件的内容，最终编译为 System Prompt。
    ///
    /// 加载顺序：
    /// 1. SOUL.md — 角色定义（内置不可变）
    /// 1+. SOUL.ext.md — 行为规则扩展（用户可选，追加到 SOUL）
    /// 2. TOOLS.md — 工具使用指南（自动生成）
    /// 3. PROJECT.md — 项目上下文（自动收集）
    /// 3+. PROJECT.md（用户） — 项目约定与个人偏好（用户可编辑，建议 VCS 提交）
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
        /// TOOLS.md — 工具使用指南（自动生成）
        /// </summary>
        public string Tools { get; set; }

        /// <summary>
        /// PROJECT.md — 项目上下文（自动收集）
        /// </summary>
        public string Project { get; set; }

        /// <summary>
        /// Workspace — PROJECT.md 用户内容（用户可编辑，建议 VCS 提交）
        /// 包含项目约定（Project Conventions）和个人偏好（Personal Preferences）
        /// </summary>
        public string Workspace { get; set; }

        /// <summary>
        /// 将所有 Bootstrap 内容编译为单一 System Prompt 字符串。
        /// 加载顺序：SOUL(+SOUL.ext) → TOOLS → PROJECT(auto) → PROJECT.md(user)
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

            // 2. TOOLS — 工具指南（必须）
            if (!string.IsNullOrEmpty(Tools))
            {
                sb.AppendLine("\n---\n");
                sb.AppendLine(Tools);
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

            return sb.ToString();
        }

        /// <summary>
        /// 估算 System Prompt 的 token 数量。
        /// 使用近似算法：每 3 个字符约 1 个 token（中英文混合场景的经验值）。
        /// </summary>
        public int EstimateTokenCount()
        {
            var prompt = CompileSystemPrompt();
            if (string.IsNullOrEmpty(prompt)) return 0;

            return prompt.Length / 3;
        }
    }
}
