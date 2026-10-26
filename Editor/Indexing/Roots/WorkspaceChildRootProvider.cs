using System;
using System.Collections.Generic;
using System.IO;
using AgentCore.Editor.Components.Indexing.Models;

namespace AgentCore.Editor.Components.Indexing.Roots
{
    /// <summary>
    /// 扫描 WorkspaceRoot 下的直接子目录，按命名规则自动推断 ScopeType。
    /// 适用于地图/模式/工具等按目录组织的项目结构。
    /// Priority = 30（在 VcsWorkspaceRootProvider 之后执行，补充未被发现的子根）。
    /// </summary>
    public sealed class WorkspaceChildRootProvider : IIndexRootProvider
    {
        /// <inheritdoc/>
        public string ProviderId => "workspace_child";

        /// <inheritdoc/>
        public int Priority => 30;

        // 按目录名前缀/关键词推断 ScopeType 的规则表
        private static readonly (string[] Keywords, IndexScopeType ScopeType, IndexRootRole Role)[] _rules =
        {
            (new[] { "gamemodes", "modes", "gameplay", "玩法" }, IndexScopeType.Mode, IndexRootRole.EditableProjectCode),
            (new[] { "maps", "levels", "scenes", "地图" }, IndexScopeType.Map, IndexRootRole.EditableProjectCode),
            (new[] { "ui", "hud", "interface" }, IndexScopeType.UI, IndexRootRole.EditableProjectCode),
            (new[] { "shared", "common", "core", "framework", "公共" }, IndexScopeType.Shared, IndexRootRole.SharedCode),
            (new[] { "localization", "l10n", "i18n", "locale", "lang" }, IndexScopeType.Localization, IndexRootRole.EditableProjectCode),
            (new[] { "tools", "editor_tools", "build", "pipeline" }, IndexScopeType.Tools, IndexRootRole.ToolingCode),
            (new[] { "plugins", "thirdparty", "third_party", "vendor" }, IndexScopeType.Plugin, IndexRootRole.CommercialPlugin),
            (new[] { "engine", "runtime", "native" }, IndexScopeType.Engine, IndexRootRole.EngineCode),
            (new[] { "generated", "gen", "auto_gen", "codegen" }, IndexScopeType.Generated, IndexRootRole.GeneratedCode),
        };

        /// <inheritdoc/>
        public IReadOnlyList<IndexRoot> DiscoverRoots(IndexWorkspace workspace)
        {
            var result = new List<IndexRoot>();
            if (string.IsNullOrEmpty(workspace?.WorkspaceRoot)) return result;
            if (!Directory.Exists(workspace.WorkspaceRoot)) return result;

            try
            {
                var subDirs = Directory.GetDirectories(workspace.WorkspaceRoot, "*", SearchOption.TopDirectoryOnly);
                foreach (var dir in subDirs)
                {
                    var normalized = dir.Replace('\\', '/');
                    var dirName = Path.GetFileName(normalized);

                    // 跳过隐藏目录和常见非代码目录
                    if (dirName.StartsWith(".")) continue;
                    if (IsExcludedDir(dirName.ToLowerInvariant())) continue;

                    // 跳过 UnityRoot（由 UnityRootProvider 处理）
                    if (!string.IsNullOrEmpty(workspace.UnityRoot) &&
                        string.Equals(normalized, workspace.UnityRoot, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // 按规则推断 ScopeType
                    var (scopeType, role) = InferScope(dirName);
                    if (scopeType == IndexScopeType.Unknown) continue; // 无法识别的目录跳过

                    var readOnly = IndexRoot.InferReadOnly(scopeType, role);
                    var relative = MakeRelative(workspace.WorkspaceRoot, normalized);

                    result.Add(new IndexRoot
                    {
                        RootPath = normalized,
                        RelativeToWorkspace = relative,
                        DisplayName = Path.GetFileName(normalized),
                        ScopeType = scopeType,
                        ScopeName = Path.GetFileName(normalized),
                        Role = role,
                        ReadOnly = readOnly,
                        IsEnabled = true,
                        IsDefaultSearchScope = IndexRoot.InferDefaultSearchScope(scopeType),
                        ProviderId = ProviderId
                    });
                }
            }
            catch
            {
                // 目录扫描失败时静默返回已发现的结果
            }

            return result;
        }

        private static (IndexScopeType, IndexRootRole) InferScope(string dirName)
        {
            foreach (var (keywords, scopeType, role) in _rules)
            {
                foreach (var kw in keywords)
                {
                    if (MatchesKeyword(dirName, kw))
                        return (scopeType, role);
                }
            }
            return (IndexScopeType.Unknown, IndexRootRole.EditableProjectCode);
        }

        /// <summary>
        /// 关键词须是目录名开头的完整单词：其后是名称结尾、分隔符/数字，或驼峰的下一个单词
        /// （"UI_Common"、"UICommon"、"build_tools" 命中；"Builds" 不命中 build，"AgentCore" 不命中 core）。
        /// </summary>
        private static bool MatchesKeyword(string dirName, string keyword)
        {
            if (!dirName.StartsWith(keyword, StringComparison.OrdinalIgnoreCase)) return false;
            var i = keyword.Length;
            if (i == dirName.Length || keyword[i - 1] > 127) return true; // 中文关键词没有大小写/单词边界，按前缀即可
            var next = dirName[i];
            if (!char.IsLetter(next)) return true;
            return char.IsUpper(next) &&
                   (char.IsLower(dirName[i - 1]) || (i + 1 < dirName.Length && char.IsLower(dirName[i + 1])));
        }

        private static bool IsExcludedDir(string dirName)
        {
            return dirName == "library" || dirName == "temp" || dirName == "logs" ||
                   dirName == "obj" || dirName == "bin" || dirName == ".svn" ||
                   dirName == ".git" || dirName == "node_modules" || dirName == "__pycache__";
        }

        private static string MakeRelative(string workspaceRoot, string absolutePath)
        {
            if (string.IsNullOrEmpty(workspaceRoot)) return absolutePath;
            var root = workspaceRoot.TrimEnd('/') + "/";
            if (absolutePath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return absolutePath.Substring(root.Length);
            return absolutePath;
        }
    }
}
