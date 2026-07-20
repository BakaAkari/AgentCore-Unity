using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Utils;
using AgentCore.Editor.Workspace;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace AgentCore.Editor.Bootstrap
{
    /// <summary>
    /// 自动收集 Unity 项目信息，生成 PROJECT.md 内容。
    /// 收集的信息包括：Unity 版本、渲染管线、目标平台、项目结构、已安装包等。
    /// 同时注入 Workspace 摘要（WorkspaceRoot、Branch、Scope Roots 表格）。
    /// </summary>
    /// <remarks>
    /// v1.4.0（Step 1）— 主线程零阻塞拆分：
    /// - <see cref="Collect"/> / <see cref="CollectFast"/> — 快速版，仅使用 Unity API 与 UPM manifest（不扫盘）
    /// - <see cref="CollectHeavyAsync"/> — 重量级扫描（脚本统计、命名空间分布、Tags/Layers），后台执行 + 缓存
    /// - <see cref="CollectExtended"/> — 向后兼容入口：命中缓存返回完整版，未命中则返回快速版并触发后台预热
    /// </remarks>
    public static class ProjectContextCollector
    {
        // === 后台缓存（跨调用共享；Domain Reload 会重置） ===
        private static readonly object _cacheLock = new object();
        private static string _cachedHeavyContent;
        private static string _cachedHeavyFingerprint;
        private static DateTime _cachedHeavyAt = DateTime.MinValue;
        private static Task<string> _inflightHeavyTask;
        private static readonly TimeSpan HeavyCacheTtl = TimeSpan.FromMinutes(5);

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

                // Workspace 摘要
                sb.AppendLine("### Workspace 信息");
                sb.AppendLine(CollectWorkspaceSummary());
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
                AgentCoreLog.Warning($"[AgentCore] ProjectContextCollector error: {ex}");
            }

            return sb.ToString();
        }

        /// <summary>
        /// 收集 Workspace 摘要，包含 WorkspaceRoot、UnityRoot、Branch、Fingerprint 和 Scope Roots 表格。
        /// 失败时静默降级，不影响主流程。
        /// </summary>
        private static string CollectWorkspaceSummary()
        {
            var sb = new StringBuilder();
            try
            {
                var ctx = WorkspaceContextService.GetCurrent();

                if (ctx == null || !ctx.IsValid)
                {
                    var status = ctx?.Status.ToString() ?? "null";
                    var err = ctx?.ErrorMessage;
                    sb.AppendLine(string.IsNullOrEmpty(err)
                        ? $"- **Workspace**: 未解析 (Status={status})"
                        : $"- **Workspace**: 解析失败 — {err}");
                    return sb.ToString();
                }

                sb.AppendLine($"- **Workspace Root**: `{ctx.WorkspaceRoot}`");
                sb.AppendLine($"- **Unity Root**: `{ctx.UnityRoot}`");

                if (!string.IsNullOrEmpty(ctx.UnityRootRelativePath))
                    sb.AppendLine($"- **Unity 相对路径**: `{ctx.UnityRootRelativePath}`");

                if (!string.IsNullOrEmpty(ctx.Fingerprint))
                    sb.AppendLine($"- **Fingerprint**: `{ctx.Fingerprint}`");

                // VCS 信息
                if (ctx.Vcs != null && ctx.Vcs.Type != WorkspaceVcsType.None)
                {
                    sb.AppendLine($"- **VCS**: {ctx.Vcs.Type}");
                    if (!string.IsNullOrEmpty(ctx.Vcs.BranchId))
                        sb.AppendLine($"- **Branch**: `{ctx.Vcs.BranchId}`");
                    if (!string.IsNullOrEmpty(ctx.Vcs.Revision))
                        sb.AppendLine($"- **Revision**: {ctx.Vcs.Revision}");
                    if (!string.IsNullOrEmpty(ctx.Vcs.Url))
                        sb.AppendLine($"- **SVN URL**: `{ctx.Vcs.Url}`");
                }

                // Scope Roots 表格
                var enabledRoots = ctx.EnabledRoots;
                if (enabledRoots != null && enabledRoots.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("**Scope Roots:**");
                    sb.AppendLine();
                    sb.AppendLine("| 名称 | Scope | Role | 相对路径 |");
                    sb.AppendLine("|------|-------|------|---------|");
                    foreach (var root in enabledRoots)
                    {
                        if (root == null) continue;
                        var name = root.DisplayName ?? root.Id ?? "?";
                        var relPath = root.RelativePath ?? "(unknown)";
                        sb.AppendLine($"| {name} | {root.ScopeType} | {root.Role} | `{relPath}` |");
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"- **Workspace**: 收集失败 — {ex.Message}");
                AgentCoreLog.Warning($"[AgentCore] WorkspaceSummary collection error: {ex.Message}");
            }

            return sb.ToString();
        }

        /// <summary>
        /// 快速版收集（与 <see cref="Collect"/> 等价）。
        /// 仅使用 Unity API + UPM manifest.json，不做任何 <c>Assets/**</c> 递归扫盘。
        /// 主线程调用总耗时通常 &lt; 50ms。
        /// </summary>
        public static string CollectFast() => Collect();

        /// <summary>
        /// 后台重量级收集：脚本统计、命名空间分布、Tags/Layers、Build Scenes、Key Settings。
        /// 结果按 workspace fingerprint 缓存 5 分钟。多次并发调用共享同一 in-flight task。
        /// <para>
        /// 实现约束：Unity Editor API 大部分只能主线程调用（EditorBuildSettings / PlayerSettings /
        /// UnityEditorInternal.InternalEditorUtility.tags / SortingLayer / QualitySettings 等）。
        /// 因此本方法在主线程完成 Unity API 快照后，把纯磁盘扫描（GetProjectStats /
        /// GetNamespaceDistribution）放到 Task.Run。
        /// </para>
        /// </summary>
        public static Task<string> CollectHeavyAsync(CancellationToken ct = default)
        {
            var fingerprint = TryGetWorkspaceFingerprint();

            lock (_cacheLock)
            {
                // 命中缓存
                if (_cachedHeavyContent != null
                    && string.Equals(_cachedHeavyFingerprint, fingerprint, StringComparison.Ordinal)
                    && DateTime.UtcNow - _cachedHeavyAt < HeavyCacheTtl)
                {
                    return Task.FromResult(_cachedHeavyContent);
                }

                // 复用 in-flight 任务，避免重复扫描
                if (_inflightHeavyTask != null && !_inflightHeavyTask.IsCompleted)
                {
                    return _inflightHeavyTask;
                }

                // 主线程预取 Unity API 数据（必须在此完成）
                var mainThreadSnapshot = CaptureMainThreadSnapshot();
                var assetsPath = Application.dataPath;

                _inflightHeavyTask = Task.Run(() =>
                {
                    ct.ThrowIfCancellationRequested();
                    var content = BuildHeavyContent(assetsPath, mainThreadSnapshot, ct);

                    lock (_cacheLock)
                    {
                        _cachedHeavyContent = content;
                        _cachedHeavyFingerprint = fingerprint;
                        _cachedHeavyAt = DateTime.UtcNow;
                    }

                    return content;
                }, ct);

                return _inflightHeavyTask;
            }
        }

        /// <summary>
        /// 主线程数据快照 — Unity API 调用结果的容器。
        /// 后台线程只读该结构，不再触碰 Unity API。
        /// </summary>
        private sealed class MainThreadSnapshot
        {
            public string BuildScenes;
            public string CustomTagsAndLayers;
            public string KeyProjectSettings;
        }

        /// <summary>
        /// 在主线程捕获所有 Unity API 依赖数据（<see cref="CollectHeavyAsync"/> 内部调用）。
        /// </summary>
        private static MainThreadSnapshot CaptureMainThreadSnapshot()
        {
            var snapshot = new MainThreadSnapshot();

            try { snapshot.BuildScenes = GetBuildScenes(); }
            catch (Exception ex) { snapshot.BuildScenes = $"(Build Scenes 读取失败: {ex.Message})"; }

            try { snapshot.CustomTagsAndLayers = GetCustomTagsAndLayers(); }
            catch (Exception ex) { snapshot.CustomTagsAndLayers = $"(Tags/Layers 读取失败: {ex.Message})"; }

            try { snapshot.KeyProjectSettings = GetKeyProjectSettings(); }
            catch (Exception ex) { snapshot.KeyProjectSettings = $"(关键设置读取失败: {ex.Message})"; }

            return snapshot;
        }

        /// <summary>
        /// 收集扩展项目信息（向后兼容入口）。
        /// <para>
        /// v1.4.0 行为校准：
        /// - 若 <see cref="CollectHeavyAsync"/> 缓存命中，返回 <c>Fast + Heavy</c> 完整版
        /// - 若未命中，返回 <c>Fast + "扩展信息后台生成中"</c>，并异步触发 Heavy 预热
        /// </para>
        /// 调用方（BootstrapLoader）总是能立即拿到主线程零阻塞的结果，
        /// 完整扩展信息将在下次 Bootstrap 加载或后台任务完成后可用。
        /// </summary>
        public static string CollectExtended()
        {
            var fast = Collect();

            string heavy = null;
            lock (_cacheLock)
            {
                if (_cachedHeavyContent != null
                    && DateTime.UtcNow - _cachedHeavyAt < HeavyCacheTtl)
                {
                    heavy = _cachedHeavyContent;
                }
            }

            // 后台预热（fire-and-forget；错误静默）
            if (heavy == null)
            {
                _ = CollectHeavyAsync().ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        AgentCoreLog.Warning($"[AgentCore] Heavy project context prefetch failed: {t.Exception?.GetBaseException().Message}");
                    }
                }, TaskScheduler.Default);
            }

            var sb = new StringBuilder();
            sb.Append(fast);
            sb.AppendLine();

            if (heavy != null)
            {
                sb.Append(heavy);
            }
            else
            {
                sb.AppendLine("### 扩展信息");
                sb.AppendLine("_（脚本统计 / 命名空间分布 / Tags & Layers 正在后台生成，下次 Bootstrap 加载时会自动补齐）_");
            }

            return sb.ToString();
        }

        /// <summary>
        /// 构造重量级内容（在后台线程执行）。
        /// Unity API 数据由主线程通过 <paramref name="snapshot"/> 预取；本方法只做磁盘扫描。
        /// </summary>
        private static string BuildHeavyContent(string assetsPath, MainThreadSnapshot snapshot, CancellationToken ct)
        {
            var sb = new StringBuilder();

            try
            {
                ct.ThrowIfCancellationRequested();
                sb.AppendLine("### 场景列表 (Build Settings)");
                sb.Append(snapshot.BuildScenes);
                sb.AppendLine();

                ct.ThrowIfCancellationRequested();
                sb.AppendLine("### 项目规模");
                sb.Append(GetProjectStatsOffline(assetsPath, ct));
                sb.AppendLine();

                ct.ThrowIfCancellationRequested();
                sb.AppendLine("### 脚本命名空间分布");
                sb.Append(GetNamespaceDistributionOffline(assetsPath, ct));
                sb.AppendLine();

                ct.ThrowIfCancellationRequested();
                sb.AppendLine("### 自定义 Tags & Layers");
                sb.Append(snapshot.CustomTagsAndLayers);
                sb.AppendLine();

                ct.ThrowIfCancellationRequested();
                sb.AppendLine("### 关键设置");
                sb.Append(snapshot.KeyProjectSettings);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                sb.AppendLine($"\n> [WARN] 扩展信息收集部分失败: {ex.Message}");
                AgentCoreLog.Warning($"[AgentCore] ProjectContextCollector heavy error: {ex}");
            }

            return sb.ToString();
        }

        /// <summary>
        /// 后台线程安全版本的 <see cref="GetProjectStats"/> — 用参数接收 assets 路径而非 Application.dataPath。
        /// </summary>
        private static string GetProjectStatsOffline(string assetsPath, CancellationToken ct)
        {
            var sb = new StringBuilder();

            try
            {
                int scriptCount = 0, prefabCount = 0, sceneCount = 0, materialCount = 0;
                int textureCount = 0, audioCount = 0, animCount = 0, shaderCount = 0;
                int totalFiles = 0;

                var files = Directory.GetFiles(assetsPath, "*.*", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
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
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                sb.AppendLine($"(统计失败: {ex.Message})");
            }

            return sb.ToString();
        }

        /// <summary>
        /// 后台线程安全版本的 <see cref="GetNamespaceDistribution"/>。
        /// </summary>
        private static string GetNamespaceDistributionOffline(string assetsPath, CancellationToken ct)
        {
            var sb = new StringBuilder();

            try
            {
                var namespaceCounts = new Dictionary<string, int>();
                var scriptFiles = Directory.GetFiles(assetsPath, "*.cs", SearchOption.AllDirectories);

                foreach (var file in scriptFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var lines = File.ReadLines(file).Take(50);
                        foreach (var line in lines)
                        {
                            var trimmed = line.Trim();
                            if (trimmed.StartsWith("namespace "))
                            {
                                var ns = trimmed.Substring("namespace ".Length).TrimEnd('{', ' ', '\t');
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
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                sb.AppendLine($"(分析失败: {ex.Message})");
            }

            return sb.ToString();
        }

        /// <summary>
        /// 尝试获取当前 workspace fingerprint（用于缓存 key）。
        /// 失败时返回项目根路径的 hash。
        /// </summary>
        private static string TryGetWorkspaceFingerprint()
        {
            try
            {
                var ctx = WorkspaceContextService.GetCurrent();
                if (ctx != null && !string.IsNullOrEmpty(ctx.Fingerprint))
                {
                    return ctx.Fingerprint;
                }
            }
            catch
            {
                // ignore
            }

            return GetProjectPath();
        }

        /// <summary>
        /// 清除后台缓存（测试或用户手动刷新时使用）。
        /// </summary>
        public static void ClearHeavyCache()
        {
            lock (_cacheLock)
            {
                _cachedHeavyContent = null;
                _cachedHeavyFingerprint = null;
                _cachedHeavyAt = DateTime.MinValue;
            }
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
