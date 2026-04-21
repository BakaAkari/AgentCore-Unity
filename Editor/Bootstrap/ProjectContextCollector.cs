using System;
using System.IO;
using System.Linq;
using System.Text;
using AgentCore.Editor.Utils;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace AgentCore.Editor.Bootstrap
{
    /// <summary>
    /// 自动收集 Unity 项目信息，生成 PROJECT.md 内容。
    /// 收集的信息包括：Unity 版本、渲染管线、目标平台、项目结构、已安装包等。
    /// </summary>
    public static class ProjectContextCollector
    {
        /// <summary>
        /// 收集项目上下文信息，返回 Markdown 格式的文本。
        /// </summary>
        public static string Collect()
        {
            var sb = new StringBuilder();

            try
            {
                // 基本信息
                sb.AppendLine($"- **项目路径**: `{GetProjectPath()}`");
                sb.AppendLine($"- **Unity 版本**: {Application.unityVersion}");
                sb.AppendLine($"- **渲染管线**: {DetectRenderPipeline()}");
                sb.AppendLine($"- **脚本后端**: {PlayerSettings.GetScriptingBackend(EditorUserBuildSettings.selectedBuildTargetGroup)}");
                sb.AppendLine($"- **目标平台**: {EditorUserBuildSettings.activeBuildTarget}");
                sb.AppendLine($"- **API 兼容级别**: {PlayerSettings.GetApiCompatibilityLevel(EditorUserBuildSettings.selectedBuildTargetGroup)}");
                sb.AppendLine($"- **公司名称**: {PlayerSettings.companyName}");
                sb.AppendLine($"- **产品名称**: {PlayerSettings.productName}");
                sb.AppendLine();

                // 项目结构摘要
                sb.AppendLine("### 项目结构摘要");
                sb.AppendLine("```");
                sb.AppendLine(GetDirectoryTree("Assets", 2));
                sb.AppendLine("```");
                sb.AppendLine();

                // 已安装的关键包
                sb.AppendLine("### 已安装的关键包");
                sb.AppendLine(GetInstalledPackages());
            }
            catch (Exception ex)
            {
                sb.AppendLine($"\n> [WARN] 项目信息收集部分失败: {ex.Message}");
                Debug.LogWarning($"[AgentCore] ProjectContextCollector error: {ex}");
            }

            return sb.ToString();
        }

        /// <summary>
        /// 获取 Unity 项目根目录路径。
        /// </summary>
        private static string GetProjectPath()
        {
            // Application.dataPath 返回 "项目路径/Assets"
            return Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
        }

        /// <summary>
        /// 检测当前使用的渲染管线。
        /// </summary>
        private static string DetectRenderPipeline()
        {
            var currentRP = GraphicsSettings.currentRenderPipeline;
            if (currentRP == null)
                return "Built-in Render Pipeline";

            var typeName = currentRP.GetType().Name;
            if (typeName.Contains("Universal") || typeName.Contains("URP"))
                return $"Universal Render Pipeline (URP) — {currentRP.name}";
            if (typeName.Contains("HighDefinition") || typeName.Contains("HDRP"))
                return $"High Definition Render Pipeline (HDRP) — {currentRP.name}";

            return $"{typeName} — {currentRP.name}";
        }

        /// <summary>
        /// 生成目录树（限制深度和数量，避免大型项目 token 爆炸）。
        /// </summary>
        private static string GetDirectoryTree(string relativePath, int maxDepth)
        {
            var projectRoot = GetProjectPath();
            var fullPath = Path.Combine(projectRoot, relativePath);

            if (!Directory.Exists(fullPath))
                return $"{relativePath}/ (not found)";

            var sb = new StringBuilder();
            BuildDirectoryTree(sb, fullPath, projectRoot, "", maxDepth, 0);
            return sb.ToString();
        }

        private static void BuildDirectoryTree(
            StringBuilder sb, string dirPath, string projectRoot,
            string indent, int maxDepth, int currentDepth)
        {
            var dirName = Path.GetFileName(dirPath);

            sb.AppendLine($"{indent}{dirName}/");

            if (currentDepth >= maxDepth)
            {
                // 超过深度限制，显示子目录数量
                var subDirCount = 0;
                try { subDirCount = Directory.GetDirectories(dirPath).Length; }
                catch { /* ignore */ }

                if (subDirCount > 0)
                    sb.AppendLine($"{indent}  ... ({subDirCount} subdirectories)");
                return;
            }

            try
            {
                var dirs = Directory.GetDirectories(dirPath)
                    .OrderBy(d => Path.GetFileName(d))
                    .Take(20) // 最多显示 20 个子目录
                    .ToArray();

                var totalDirs = Directory.GetDirectories(dirPath).Length;

                foreach (var dir in dirs)
                {
                    var name = Path.GetFileName(dir);
                    // 跳过隐藏目录和常见的无关目录
                    if (name.StartsWith(".") || name == "Library" || name == "Temp" ||
                        name == "Logs" || name == "obj" || name == "Build")
                        continue;

                    BuildDirectoryTree(sb, dir, projectRoot, indent + "  ", maxDepth, currentDepth + 1);
                }

                if (totalDirs > 20)
                    sb.AppendLine($"{indent}  ... and {totalDirs - 20} more directories");
            }
            catch (Exception)
            {
                // 权限问题等，忽略
            }
        }

        /// <summary>
        /// 获取已安装的 UPM 包列表（只列出非 Unity 内置模块的包）。
        /// </summary>
        private static string GetInstalledPackages()
        {
            var sb = new StringBuilder();
            var manifestPath = Path.Combine(GetProjectPath(), "Packages", "manifest.json");

            if (!File.Exists(manifestPath))
            {
                sb.AppendLine("(manifest.json not found)");
                return sb.ToString();
            }

            try
            {
                var manifestJson = File.ReadAllText(manifestPath);
                var manifest = JsonHelper.ParseObject(manifestJson);
                var dependencies = manifest?["dependencies"] as JObject;

                if (dependencies == null)
                {
                    sb.AppendLine("(no dependencies found)");
                    return sb.ToString();
                }

                foreach (var dep in dependencies)
                {
                    // 跳过 Unity 内置模块（com.unity.modules.*）
                    if (dep.Key.StartsWith("com.unity.modules."))
                        continue;

                    sb.AppendLine($"- `{dep.Key}`: {dep.Value}");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"(error reading manifest: {ex.Message})");
            }

            return sb.ToString();
        }
    }
}
