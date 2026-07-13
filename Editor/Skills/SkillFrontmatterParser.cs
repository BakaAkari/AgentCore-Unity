using System.Collections.Generic;
using System.Text;

namespace AgentCore.Editor.Skills
{
    /// <summary>
    /// 极简 YAML frontmatter 解析器（只支持 key: value 单行格式）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Skill 前置 frontmatter 使用示例：
    /// <code>
    /// ---
    /// name: unity-runtime-dev
    /// description: Runtime code development
    /// category: development
    /// version: 1.0
    /// ---
    /// </code>
    /// </para>
    /// <para>
    /// 故意不引入 YamlDotNet 依赖：Skill frontmatter 只有 4-5 个扁平 key，
    /// 手写 parser 比引入 ~1MB 依赖更符合 ADR-17 极简原则。
    /// 不支持嵌套对象 / 数组 / 多行字符串等复杂 YAML 特性。
    /// </para>
    /// </remarks>
    public static class SkillFrontmatterParser
    {
        private const string Delimiter = "---";

        /// <summary>
        /// 解析结果。
        /// </summary>
        public struct Result
        {
            /// <summary>frontmatter 的 key/value 字典（可能为空）。</summary>
            public Dictionary<string, string> Fields;

            /// <summary>剥离 frontmatter 后的正文内容。</summary>
            public string Body;
        }

        /// <summary>
        /// 从文件内容中提取 frontmatter 字段和正文。
        /// 若没有 frontmatter，<see cref="Result.Fields"/> 为空字典，<see cref="Result.Body"/> 为原文。
        /// </summary>
        public static Result Parse(string fileContent)
        {
            var result = new Result
            {
                Fields = new Dictionary<string, string>(),
                Body = fileContent ?? string.Empty
            };

            if (string.IsNullOrEmpty(fileContent))
                return result;

            var lines = fileContent.Split('\n');
            if (lines.Length < 3) return result;

            // 首行必须是 --- （允许前导空白但不允许其他字符）
            if (lines[0].Trim() != Delimiter) return result;

            // 寻找结束 ---
            int endIndex = -1;
            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].Trim() == Delimiter)
                {
                    endIndex = i;
                    break;
                }
            }

            if (endIndex < 0) return result;

            // 解析 key: value
            for (int i = 1; i < endIndex; i++)
            {
                var line = lines[i].TrimEnd('\r');
                var colonIndex = line.IndexOf(':');
                if (colonIndex <= 0) continue;

                var key = line.Substring(0, colonIndex).Trim();
                var value = line.Substring(colonIndex + 1).Trim().Trim('"', '\'');
                if (!string.IsNullOrEmpty(key))
                    result.Fields[key] = value;
            }

            // 拼接正文（endIndex 后所有内容）
            var bodyBuilder = new StringBuilder();
            for (int i = endIndex + 1; i < lines.Length; i++)
            {
                bodyBuilder.Append(lines[i]);
                if (i < lines.Length - 1) bodyBuilder.Append('\n');
            }
            result.Body = bodyBuilder.ToString().TrimStart('\n', '\r');

            return result;
        }
    }
}
