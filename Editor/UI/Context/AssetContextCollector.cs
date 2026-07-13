using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.UI.Context
{
    /// <summary>
    /// 采集 Project Browser 中选中的 Asset。
    /// 支持任意 Unity Asset（脚本/纹理/预制体/材质/场景/音频/字体/ScriptableObject/...）。
    /// 根据类型输出针对性的元信息。
    /// </summary>
    public static class AssetContextCollector
    {
        /// <summary>
        /// 采集当前选中的 asset。使用 Selection.assetGUIDs 而非 Selection.objects，
        /// 因为 assetGUIDs 只包含 Project 目录下的资源，能过滤掉场景 GameObject。
        /// </summary>
        public static ContextIngestResult Collect()
        {
            var guids = Selection.assetGUIDs;
            if (guids == null || guids.Length == 0) return ContextIngestResult.Empty();

            var paths = new string[guids.Length];
            int assetCount = 0;
            for (int i = 0; i < guids.Length; i++)
            {
                var path = AssetDatabase.GUIDToAssetPath(guids[i]);
                // 过滤掉目录条目
                if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path)) continue;
                paths[assetCount++] = path;
            }

            if (assetCount == 0) return ContextIngestResult.Empty();

            var label = BuildLabel(paths, assetCount);
            var sb = new StringBuilder(512);

            sb.Append("Total assets: ").Append(assetCount).Append('\n');

            if (assetCount <= ContextIngestLimits.AssetDetailLimit)
            {
                for (int i = 0; i < assetCount; i++)
                {
                    AppendAssetDetailed(sb, paths[i]);
                    if (i < assetCount - 1) sb.Append('\n');
                }
            }
            else
            {
                // 只列路径 + 类型
                for (int i = 0; i < assetCount; i++)
                {
                    var path = paths[i];
                    var type = AssetDatabase.GetMainAssetTypeAtPath(path);
                    sb.Append("- ").Append(path).Append(" (").Append(type?.Name ?? "?").Append(")\n");
                }

                return ContextIngestResult.OkWithWarning(
                    label,
                    sb.ToString(),
                    $"{assetCount} assets selected; only paths shown. Reduce selection for detailed metadata.",
                    truncated: true);
            }

            return ContextIngestResult.Ok(label, sb.ToString());
        }

        // ---------- Label ----------

        private static string BuildLabel(string[] paths, int count)
        {
            var firstName = Path.GetFileName(paths[0] ?? string.Empty);
            if (count == 1) return $"Asset: {firstName}";
            var secondName = count >= 2 ? Path.GetFileName(paths[1] ?? string.Empty) : string.Empty;
            if (count == 2) return $"Assets: {firstName}, {secondName}";
            return $"Assets: {firstName}, {secondName} (+{count - 2} more)";
        }

        // ---------- 单 asset 详情 ----------

        private static void AppendAssetDetailed(StringBuilder sb, string path)
        {
            sb.Append("### ").Append(path).Append('\n');

            var mainType = AssetDatabase.GetMainAssetTypeAtPath(path);
            if (mainType == null)
            {
                sb.Append("Type: <unknown>\n");
                return;
            }

            sb.Append("Type: ").Append(mainType.Name).Append('\n');

            // 文件大小
            var absPath = Path.GetFullPath(path);
            if (File.Exists(absPath))
            {
                try
                {
                    var size = new FileInfo(absPath).Length;
                    sb.Append("Size: ").Append(FormatBytes(size)).Append('\n');
                }
                catch { /* ignore */ }
            }

            // GUID（LLM 可能需要）
            sb.Append("GUID: ").Append(AssetDatabase.AssetPathToGUID(path)).Append('\n');

            // Importer 类型专项元数据
            AppendTypeSpecificMetadata(sb, path, mainType);
        }

        private static void AppendTypeSpecificMetadata(StringBuilder sb, string path, System.Type mainType)
        {
            // 脚本文件：读取前 N 行注释/using
            if (mainType == typeof(MonoScript))
            {
                AppendMonoScriptMeta(sb, path);
                return;
            }

            // 纹理
            if (mainType == typeof(Texture2D) || mainType == typeof(Texture))
            {
                AppendTextureMeta(sb, path);
                return;
            }

            // 预制体
            if (mainType == typeof(GameObject))
            {
                AppendPrefabMeta(sb, path);
                return;
            }

            // 材质
            if (mainType == typeof(Material))
            {
                AppendMaterialMeta(sb, path);
                return;
            }

            // Scene
            if (mainType == typeof(SceneAsset))
            {
                sb.Append("Note: Scene asset (open via SceneManager to inspect contents).\n");
                return;
            }

            // ScriptableObject / 其他 UnityEngine.Object：只显示 mainType
            // 已在 AppendAssetDetailed 里输出 Type，无需重复
        }

        private static void AppendMonoScriptMeta(StringBuilder sb, string path)
        {
            var absPath = Path.GetFullPath(path);
            if (!File.Exists(absPath)) return;

            try
            {
                var lines = File.ReadAllLines(absPath);
                var preview = new StringBuilder();
                int shown = 0;
                for (int i = 0; i < lines.Length && shown < 30; i++)
                {
                    var line = lines[i];
                    preview.Append("    ").Append(line).Append('\n');
                    shown++;
                }
                if (lines.Length > 30)
                {
                    preview.Append("    ...(").Append(lines.Length - 30).Append(" more lines)\n");
                }
                sb.Append("Preview:\n").Append(preview);
            }
            catch (System.Exception ex)
            {
                sb.Append("(read failed: ").Append(ex.Message).Append(")\n");
            }
        }

        private static void AppendTextureMeta(StringBuilder sb, string path)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null)
            {
                sb.Append("TextureType: ").Append(importer.textureType).Append('\n');
                sb.Append("MaxTextureSize: ").Append(importer.maxTextureSize).Append('\n');
                sb.Append("MipmapEnabled: ").Append(importer.mipmapEnabled).Append('\n');
                sb.Append("sRGB: ").Append(importer.sRGBTexture).Append('\n');
            }

            var tex = AssetDatabase.LoadAssetAtPath<Texture>(path);
            if (tex != null)
            {
                sb.Append("Dimensions: ").Append(tex.width).Append('x').Append(tex.height).Append('\n');
            }
        }

        private static void AppendPrefabMeta(StringBuilder sb, string path)
        {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (go == null) return;

            var comps = go.GetComponents<Component>();
            sb.Append("Root components: ");
            for (int i = 0; i < comps.Length; i++)
            {
                if (comps[i] == null) continue;
                if (i > 0) sb.Append(", ");
                sb.Append(comps[i].GetType().Name);
            }
            sb.Append('\n');

            var childCount = go.transform.childCount;
            sb.Append("Direct children: ").Append(childCount).Append('\n');
        }

        private static void AppendMaterialMeta(StringBuilder sb, string path)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null) return;

            sb.Append("Shader: ").Append(mat.shader != null ? mat.shader.name : "<null>").Append('\n');
            sb.Append("RenderQueue: ").Append(mat.renderQueue).Append('\n');
        }

        // ---------- Helpers ----------

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024.0).ToString("F1") + " MB";
            return (bytes / 1024.0 / 1024.0 / 1024.0).ToString("F1") + " GB";
        }
    }
}
