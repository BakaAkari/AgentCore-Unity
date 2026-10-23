using System;
using System.Collections.Generic;

namespace AgentCore.Editor.Components.VCS.Tools
{
    /// <summary>
    /// 解析 git 以 <c>-z</c>（NUL 分隔）输出的路径列表，例如
    /// <c>git diff-tree --no-commit-id --name-only -r -z &lt;rev&gt;</c>。
    /// <para>
    /// <c>-z</c> 模式下路径不做 C 风格引号转义，中文、空格、引号、换行都原样输出（UTF-8），
    /// 因此只能按 NUL 切分，不能按行切分或 Trim（路径首尾的空格是文件名的一部分）。
    /// </para>
    /// </summary>
    public static class GitOutputParser
    {
        /// <summary>
        /// 把 NUL 分隔的输出拆成路径列表。忽略空记录（末尾的终止 NUL）；不修改路径内容。
        /// </summary>
        public static List<string> ParseNulSeparatedPaths(string output)
        {
            var paths = new List<string>();
            if (string.IsNullOrEmpty(output))
                return paths;

            foreach (var record in output.Split('\0'))
            {
                if (record.Length == 0)
                    continue;
                paths.Add(record);
            }
            return paths;
        }
    }
}
