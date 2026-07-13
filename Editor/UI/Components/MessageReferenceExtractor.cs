using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AgentCore.Editor.UI.Components
{
    /// <summary>
    /// 单个消息引用条目。
    /// </summary>
    public class MessageReference
    {
        public enum RefKind { Asset, GameObject }

        public RefKind Kind { get; }
        public string DisplayLabel { get; }
        public string Target { get; }
        public int Line { get; }

        public MessageReference(RefKind kind, string displayLabel, string target, int line = 0)
        {
            Kind = kind;
            DisplayLabel = displayLabel;
            Target = target;
            Line = line;
        }
    }

    /// <summary>
    /// 从消息文本中提取文件路径 / GameObject 引用。
    /// 只识别高置信度模式，避免误抓普通英文单词。
    /// </summary>
    public static class MessageReferenceExtractor
    {
        // 反引号包裹的资源路径，可选 :line 后缀
        // 例:  `Assets/Scripts/Player.cs`  或  `Assets/Foo.cs:42`  或  `Packages/com.xxx/Editor/Bar.cs`
        private static readonly Regex AssetPathRegex = new Regex(
            @"`(?<path>(?:Assets|Packages|ProjectSettings|Editor|Resources)/[^`\s]+?\.(?:cs|md|txt|json|shader|shadergraph|unity|prefab|asset|mat|controller|anim|fbx|obj|png|jpg|jpeg|tga|psd|hlsl|cginc|inputactions|uss|uxml))(?::(?<line>\d+))?`",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // GameObject 引用：`hierarchy: A/B/C`  或  `[GameObject: Cube]`
        private static readonly Regex HierarchyPathRegex = new Regex(
            @"`hierarchy:\s*(?<path>[^`\n]+?)`",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex GameObjectTagRegex = new Regex(
            @"\[GameObject:\s*(?<name>[^\]\n]+?)\]",
            RegexOptions.Compiled);

        /// <summary>
        /// 提取消息中的所有引用。按出现顺序返回，去重。
        /// </summary>
        public static List<MessageReference> Extract(string content)
        {
            var result = new List<MessageReference>();
            if (string.IsNullOrEmpty(content)) return result;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 资源路径
            foreach (Match m in AssetPathRegex.Matches(content))
            {
                var path = m.Groups["path"].Value;
                var lineStr = m.Groups["line"].Value;
                int line = 0;
                if (!string.IsNullOrEmpty(lineStr)) int.TryParse(lineStr, out line);

                var key = "A:" + path.ToLowerInvariant() + ":" + line;
                if (!seen.Add(key)) continue;

                var display = System.IO.Path.GetFileName(path);
                if (line > 0) display += ":" + line;
                result.Add(new MessageReference(MessageReference.RefKind.Asset, display, path, line));
            }

            // hierarchy: path
            foreach (Match m in HierarchyPathRegex.Matches(content))
            {
                var path = m.Groups["path"].Value.Trim();
                if (string.IsNullOrEmpty(path)) continue;
                var key = "H:" + path.ToLowerInvariant();
                if (!seen.Add(key)) continue;

                var display = path.Contains("/") ? path.Substring(path.LastIndexOf('/') + 1) : path;
                result.Add(new MessageReference(MessageReference.RefKind.GameObject, display, path));
            }

            // [GameObject: name]
            foreach (Match m in GameObjectTagRegex.Matches(content))
            {
                var name = m.Groups["name"].Value.Trim();
                if (string.IsNullOrEmpty(name)) continue;
                var key = "H:" + name.ToLowerInvariant();
                if (!seen.Add(key)) continue;

                result.Add(new MessageReference(MessageReference.RefKind.GameObject, name, name));
            }

            return result;
        }
    }
}
