using System;
using System.Collections.Generic;
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
                sb.AppendLine($"- **版本号**: {PlayerSettings.bundleVersion}");
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
        /// 收集扩展项目信息，用于 MEMORY.md 初始化。
        /// 包含基础信息 + 场景列表 + 脚本统计 + 项目规模 + Tags/Layers 等。
        /// </summary>
        public static string CollectExtended()
        {
            var sb = new StringBuilder();

            // 先收集基础信息
            sb.Append(Collect());
            sb.AppendLine();

            try
            {
                // Build Settings 中的场景列表
                sb.AppendLine("### 场景列表 (Build Settings)");
                sb.Append(GetBuildScenes());
                sb.AppendLine();

                // 项目规模统计
                sb.AppendLine("### 项目规模");
                sb.Append(GetProjectStats());
                sb.AppendLine();

                // 脚本命名空间分布
                sb.AppendLine("### 脚本命名空间分布");
                sb.Append(GetNamespaceDistribution());
                sb.AppendLine();

                // 自定义 Tags 和 Layers
                sb.AppendLine("### 自定义 Tags & Layers");
                sb.Append(GetCustomTagsAndLayers());
                sb.AppendLine();

                // 关键 ProjectSettings 信息
                sb.AppendLine("### 关键设置");
                sb.Append(GetKeyProjectSettings());
            }
            catch (Exception ex)
            {
                sb.AppendLine($"\n> [WARN] 扩展信息收集部分失败: {ex.Message}");
                Debug.LogWarning($"[AgentCore] ProjectContextCollector extended error: {ex}");
            }

            return sb.ToString();
        }

        /// <summary>
        /// 获取 Unity 项目根目录路径。
        /// 返回正斜杠格式路径，避免 LLM 学习使用反斜杠导致生成无效 JSON。
        /// </summary>
        private static string GetProjectPath()
        {
            // Application.dataPath 返回 "项目路径/Assets"
            var path = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            return path.Replace('\\', '/');
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

        /// <summary>
        /// 获取 Build Settings 中的场景列表。
        /// </summary>
        private static string GetBuildScenes()
        {
            var sb = new StringBuilder();
            var scenes = EditorBuildSettings.scenes;

            if (scenes == null || scenes.Length == 0)
            {
                sb.AppendLine("(Build Settings 中无场景)");
                return sb.ToString();
            }

            for (int i = 0; i < scenes.Length; i++)
            {
                var scene = scenes[i];
                var status = scene.enabled ? "" : "";
                var path = scene.path;
                sb.AppendLine($"- [{status}] `{i}`: {path}");
            }

            return sb.ToString();
        }

        /// <summary>
        /// 获取项目规模统计（文件数量、脚本数量等）。
        /// </summary>
        private static string GetProjectStats()
        {
            var sb = new StringBuilder();
            var assetsPath = Application.dataPath;

            try
            {
                // 统计各类文件数量
                int scriptCount = 0, prefabCount = 0, sceneCount = 0, materialCount = 0;
                int textureCount = 0, audioCount = 0, animCount = 0, shaderCount = 0;
                int totalFiles = 0;

                var files = Directory.GetFiles(assetsPath, "*.*", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    if (file.EndsWith(".meta")) continue;
                    totalFiles++;

                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    switch (ext)
                    {
                        case ".cs": scriptCount++; break;
                        case ".prefab": prefabCount++; break;
                        case ".unity": sceneCount++; break;
                        case ".mat": materialCount++; break;
                        case ".png": case ".jpg": case ".jpeg": case ".tga": case ".psd":
                        case ".exr": case ".hdr": textureCount++; break;
                        case ".wav": case ".mp3": case ".ogg": case ".aiff": audioCount++; break;
                        case ".anim": case ".controller": animCount++; break;
                        case ".shader": case ".shadergraph": case ".shadersubgraph": shaderCount++; break;
                    }
                }

                sb.AppendLine($"- **总文件数**: {totalFiles}（不含 .meta）");
                sb.AppendLine($"- **C# 脚本**: {scriptCount}");
                sb.AppendLine($"- **Prefab**: {prefabCount}");
                sb.AppendLine($"- **场景文件**: {sceneCount}");
                sb.AppendLine($"- **材质**: {materialCount}");
                sb.AppendLine($"- **纹理**: {textureCount}");
                sb.AppendLine($"- **音频**: {audioCount}");
                sb.AppendLine($"- **动画/控制器**: {animCount}");
                sb.AppendLine($"- **Shader**: {shaderCount}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"(统计失败: {ex.Message})");
            }

            return sb.ToString();
        }

        /// <summary>
        /// 获取脚本命名空间分布（帮助理解代码组织结构）。
        /// </summary>
        private static string GetNamespaceDistribution()
        {
            var sb = new StringBuilder();
            var assetsPath = Application.dataPath;

            try
            {
                var namespaceCounts = new Dictionary<string, int>();
                var scriptFiles = Directory.GetFiles(assetsPath, "*.cs", SearchOption.AllDirectories);

                foreach (var file in scriptFiles)
                {
                    try
                    {
                        // 只读取前 50 行来查找 namespace 声明
                        var lines = File.ReadLines(file).Take(50);
                        foreach (var line in lines)
                        {
                            var trimmed = line.Trim();
                            if (trimmed.StartsWith("namespace "))
                            {
                                var ns = trimmed.Substring("namespace ".Length).TrimEnd('{', ' ', '\t');
                                // 取顶级命名空间（第一个 . 之前的部分 + 第二级）
                                var parts = ns.Split('.');
                                var key = parts.Length >= 2
                                    ? $"{parts[0]}.{parts[1]}"
                                    : parts[0];

                                if (!namespaceCounts.ContainsKey(key))
                                    namespaceCounts[key] = 0;
                                namespaceCounts[key]++;
                                break;
                            }
                        }
                    }
                    catch
                    {
                        // 单个文件读取失败，跳过
                    }
                }

                if (namespaceCounts.Count == 0)
                {
                    sb.AppendLine("(未检测到命名空间)");
                }
                else
                {
                    // 按数量降序排列，最多显示 15 个
                    var sorted = namespaceCounts
                        .OrderByDescending(kv => kv.Value)
                        .Take(15);

                    foreach (var kv in sorted)
                    {
                        sb.AppendLine($"- `{kv.Key}.*`: {kv.Value} 个脚本");
                    }

                    if (namespaceCounts.Count > 15)
                    {
                        sb.AppendLine($"- ... 还有 {namespaceCounts.Count - 15} 个命名空间");
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"(分析失败: {ex.Message})");
            }

            return sb.ToString();
        }

        /// <summary>
        /// 获取自定义 Tags 和 Layers。
        /// </summary>
        private static string GetCustomTagsAndLayers()
        {
            var sb = new StringBuilder();

            // 自定义 Tags（排除 Unity 内置的）
            var builtinTags = new HashSet<string>
            {
                "Untagged", "Respawn", "Finish", "EditorOnly",
                "MainCamera", "Player", "GameController"
            };

            var allTags = UnityEditorInternal.InternalEditorUtility.tags;
            var customTags = allTags.Where(t => !builtinTags.Contains(t)).ToArray();

            if (customTags.Length > 0)
            {
                sb.AppendLine("**Tags**: " + string.Join(", ", customTags.Select(t => $"`{t}`")));
            }
            else
            {
                sb.AppendLine("**Tags**: (仅内置标签)");
            }

            // 自定义 Layers（排除 Unity 内置的 0-7 层）
            var customLayers = new StringBuilder();
            int customLayerCount = 0;
            for (int i = 8; i < 32; i++)
            {
                var layerName = LayerMask.LayerToName(i);
                if (!string.IsNullOrEmpty(layerName))
                {
                    if (customLayerCount > 0) customLayers.Append(", ");
                    customLayers.Append($"`{i}:{layerName}`");
                    customLayerCount++;
                }
            }

            if (customLayerCount > 0)
            {
                sb.AppendLine($"**Layers**: {customLayers}");
            }
            else
            {
                sb.AppendLine("**Layers**: (仅内置层)");
            }

            // Sorting Layers
            var sortingLayers = SortingLayer.layers;
            if (sortingLayers.Length > 1) // 排除默认的 "Default"
            {
                var names = sortingLayers.Select(l => $"`{l.name}`");
                sb.AppendLine($"**Sorting Layers**: {string.Join(", ", names)}");
            }

            return sb.ToString();
        }

        /// <summary>
        /// 获取关键 ProjectSettings 信息。
        /// </summary>
        private static string GetKeyProjectSettings()
        {
            var sb = new StringBuilder();

            try
            {
                // 包名
                var bundleId = PlayerSettings.applicationIdentifier;
                if (!string.IsNullOrEmpty(bundleId))
                    sb.AppendLine($"- **Bundle ID**: `{bundleId}`");

                // 色彩空间
                sb.AppendLine($"- **色彩空间**: {PlayerSettings.colorSpace}");

                // Quality Levels
                var qualityNames = QualitySettings.names;
                if (qualityNames != null && qualityNames.Length > 0)
                {
                    sb.AppendLine($"- **Quality Levels**: {string.Join(", ", qualityNames.Select(n => $"`{n}`"))} (当前: `{qualityNames[QualitySettings.GetQualityLevel()]}`)");
                }

                // Physics 设置
                sb.AppendLine($"- **Fixed Timestep**: {Time.fixedDeltaTime:F4}s ({1f / Time.fixedDeltaTime:F0} Hz)");
                sb.AppendLine($"- **Gravity**: ({Physics.gravity.x}, {Physics.gravity.y}, {Physics.gravity.z})");

                // 2D Physics（如果项目可能是 2D）
                if (Physics2D.gravity != new Vector2(0, -9.81f))
                {
                    sb.AppendLine($"- **2D Gravity**: ({Physics2D.gravity.x}, {Physics2D.gravity.y})");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"(设置读取部分失败: {ex.Message})");
            }

            return sb.ToString();
        }
    }
}
