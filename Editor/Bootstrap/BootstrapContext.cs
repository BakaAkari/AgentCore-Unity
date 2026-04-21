using System.Text;

namespace AgentCore.Editor.Bootstrap
{
    /// <summary>
    /// Bootstrap Files 加载后的上下文数据。
    /// 包含各层 Bootstrap 文件的内容，最终编译为 System Prompt。
    /// </summary>
    public class BootstrapContext
    {
        /// <summary>
        /// SOUL.md — 角色定义与核心原则（内置）
        /// </summary>
        public string Soul { get; set; }

        /// <summary>
        /// TOOLS.md — 工具使用指南（自动生成）
        /// </summary>
        public string Tools { get; set; }

        /// <summary>
        /// PROJECT.md — 项目上下文（自动收集）
        /// </summary>
        public string Project { get; set; }

        /// <summary>
        /// MEMORY.md — 本地知识文件（用户可编辑，可选）
        /// </summary>
        public string Memory { get; set; }

        /// <summary>
        /// USER.md — 用户偏好（用户可编辑，可选）
        /// </summary>
        public string User { get; set; }

        /// <summary>
        /// 将所有 Bootstrap 内容编译为单一 System Prompt 字符串。
        /// 加载顺序：SOUL -> TOOLS -> PROJECT -> MEMORY -> USER
        /// </summary>
        public string CompileSystemPrompt()
        {
            var sb = new StringBuilder();

            // 1. SOUL — 角色定义（必须）
            if (!string.IsNullOrEmpty(Soul))
            {
                sb.AppendLine(Soul);
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

            // 4. MEMORY — 本地知识（可选）
            if (!string.IsNullOrEmpty(Memory))
            {
                sb.AppendLine("\n---\n");
                sb.AppendLine("## 项目知识（来自 MEMORY.md）\n");
                sb.AppendLine(Memory);
            }

            // 5. USER — 用户偏好（可选）
            if (!string.IsNullOrEmpty(User))
            {
                sb.AppendLine("\n---\n");
                sb.AppendLine("## 用户偏好（来自 USER.md）\n");
                sb.AppendLine(User);
            }

            return sb.ToString();
        }

        /// <summary>
        /// 估算 System Prompt 的 token 数量。
        /// 使用近似算法：中文 ~1.5 token/字，英文 ~0.75 token/word。
        /// </summary>
        public int EstimateTokenCount()
        {
            var prompt = CompileSystemPrompt();
            if (string.IsNullOrEmpty(prompt)) return 0;

            // 简单估算：每 3 个字符约 1 个 token（中英文混合场景的经验值）
            return prompt.Length / 3;
        }
    }
}
