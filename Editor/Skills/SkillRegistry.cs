using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using AgentCore.Editor.Utils;

namespace AgentCore.Editor.Skills
{
    /// <summary>
    /// Skill 注册表 — 扫描磁盘上的 SKILL.md 文件，构建可查询的元数据缓存。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 单例风格（与 <c>ToolRegistry</c> 一致）。ADR-18 D1-a 决策：
    /// 目录为 <c>&lt;project-root&gt;/.agents/skills/&lt;name&gt;/SKILL.md</c>，
    /// 与 workspace 根 AGENTS.md §7.2 约定完全对齐；也允许 Unity 项目内
    /// <c>Assets/.agents/skills/</c> 位置作为项目内覆盖（少见场景）。
    /// </para>
    /// <para>
    /// 全文延迟加载：<see cref="GetContent"/> 按需 File.ReadAllText，
    /// <c>list</c> 操作只返回 <see cref="SkillMetadata"/>。
    /// </para>
    /// </remarks>
    public sealed class SkillRegistry
    {
        private static SkillRegistry _instance;
        public static SkillRegistry Instance => _instance ??= new SkillRegistry();

        private readonly Dictionary<string, SkillMetadata> _skills =
            new Dictionary<string, SkillMetadata>(StringComparer.OrdinalIgnoreCase);

        private bool _isScanned;

        /// <summary>
        /// 强制重新扫描（丢弃缓存）。
        /// </summary>
        public void Rescan()
        {
            _skills.Clear();
            _isScanned = false;
            EnsureScanned();
        }

        /// <summary>
        /// 获取所有已注册 skill 的元数据（按 name 升序）。首次调用会触发扫描。
        /// </summary>
        public IReadOnlyList<SkillMetadata> GetAll()
        {
            EnsureScanned();
            return _skills.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// 查询单个 skill 元数据。
        /// </summary>
        public SkillMetadata TryGet(string name)
        {
            EnsureScanned();
            if (string.IsNullOrWhiteSpace(name)) return null;
            return _skills.TryGetValue(name.Trim(), out var meta) ? meta : null;
        }

        /// <summary>
        /// 按需读取 skill 全文（不含 frontmatter）。用于 <c>LoadSkillTool</c> 激活时。
        /// </summary>
        /// <returns>剥离 frontmatter 后的正文；skill 不存在或读取失败返回 null。</returns>
        public string GetContent(string name)
        {
            var meta = TryGet(name);
            if (meta == null || !File.Exists(meta.FilePath))
                return null;

            try
            {
                var raw = File.ReadAllText(meta.FilePath);
                var parsed = SkillFrontmatterParser.Parse(raw);
                return parsed.Body;
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore][Skills] Failed to read '{name}' at {meta.FilePath}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 返回本次会话使用的所有搜索目录（供 UI / diagnostics 显示）。
        /// 顺序即优先级：项目外部目录在前（可覆盖内置），插件内置 Builtin 目录在后。
        /// </summary>
        public IReadOnlyList<string> GetSearchDirectories()
        {
            return GetScanRoots().Select(r => r.Path).ToList();
        }

        /// <summary>
        /// 每个扫描根：路径 + 是否内置。内置根位于插件包内 <c>Editor/Skills/Builtin/</c>，随包分发。
        /// 外部项目目录（.agents/skills 等）在前，内置在后 ⇒ 同名时外部优先（<see cref="EnsureScanned"/> 先扫先得）。
        /// </summary>
        private IReadOnlyList<SkillScanRoot> GetScanRoots()
        {
            var roots = new List<SkillScanRoot>();
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (projectRoot != null)
            {
                // 外部（项目内）—— 最高优先级，可覆盖内置
                roots.Add(new SkillScanRoot(Path.Combine(projectRoot, ".agents", "skills"), isBuiltin: false));
                roots.Add(new SkillScanRoot(Path.Combine(projectRoot, "Assets", ".agents", "skills"), isBuiltin: false));
            }

            // 内置（插件包内）—— 出厂能力，随包分发，无同名外部 skill 时生效
            var builtinRoot = BuiltinSkillsRoot;
            if (builtinRoot != null)
                roots.Add(new SkillScanRoot(builtinRoot, isBuiltin: true));

            return roots;
        }

        /// <summary>
        /// 插件内置 skill 目录的绝对路径（<c>&lt;package&gt;/Editor/Skills/Builtin</c>）。
        /// 用 <see cref="UnityEditor.PackageManager.PackageInfo.FindForAssembly"/> 定位包路径——比硬编码
        /// <c>Packages/com.agentcore.unity/...</c> 更健壮（无论包以 file:/tgz/embedded 方式被引用都能命中）。
        /// 解析失败时返回 null（内置 skill 不可用但不阻塞外部目录）。
        /// </summary>
        private static string BuiltinSkillsRoot
        {
            get
            {
                try
                {
                    var pkg = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(SkillRegistry).Assembly);
                    if (pkg == null) return null;
                    var root = Path.Combine(pkg.resolvedPath, "Editor", "Skills", "Builtin");
                    return Directory.Exists(root) ? root : null;
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        private void EnsureScanned()
        {
            if (_isScanned) return;

            try
            {
                foreach (var root in GetScanRoots())
                {
                    if (!Directory.Exists(root.Path)) continue;
                    ScanDirectory(root);
                }

                AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore][Skills] Registry scan complete: {_skills.Count} skill(s) found ({(IsBuiltinCount())} builtin).");
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore][Skills] Registry scan failed: {ex.Message}");
            }
            finally
            {
                _isScanned = true;
            }
        }

        private int IsBuiltinCount()
        {
            int n = 0;
            foreach (var kv in _skills) if (kv.Value.IsBuiltin) n++;
            return n;
        }

        /// <summary>
        /// 扫描单个扫描根下所有子目录中的 SKILL.md 文件。
        /// </summary>
        private void ScanDirectory(SkillScanRoot root)
        {
            try
            {
                foreach (var subDir in Directory.EnumerateDirectories(root.Path))
                {
                    var skillFile = Path.Combine(subDir, "SKILL.md");
                    if (!File.Exists(skillFile)) continue;

                    var meta = TryBuildMetadata(subDir, skillFile, root.IsBuiltin);
                    if (meta == null) continue;

                    if (_skills.ContainsKey(meta.Name))
                    {
                        if (!meta.IsBuiltin)
                        {
                            AgentCoreLog.Warning($"[AgentCore][Skills] Duplicate skill name '{meta.Name}'. " +
                                             $"Existing: {_skills[meta.Name].FilePath}; ignoring: {meta.FilePath}");
                        }
                        // 内置 skill 因外部同名而跳过 = 预期的"外部覆盖内置"，不视为错误，不 print warning。
                        continue;
                    }

                    _skills[meta.Name] = meta;
                }
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore][Skills] Failed to scan '{root.Path}': {ex.Message}");
            }
        }

        /// <summary>
        /// 从 SKILL.md 文件构建元数据（不缓存全文）。
        /// </summary>
        private static SkillMetadata TryBuildMetadata(string skillDir, string skillFile, bool isBuiltin)
        {
            try
            {
                var raw = File.ReadAllText(skillFile);
                var parsed = SkillFrontmatterParser.Parse(raw);

                var name = ResolveField(parsed.Fields, "name", Path.GetFileName(skillDir));
                var description = ResolveField(
                    parsed.Fields,
                    "description",
                    ExtractFallbackDescription(parsed.Body));
                var category = ResolveField(parsed.Fields, "category", null);
                var version = ResolveField(parsed.Fields, "version", null);

                return new SkillMetadata(
                    name: name,
                    description: description,
                    category: category,
                    version: version,
                    filePath: skillFile,
                    charCount: raw.Length,
                    isBuiltin: isBuiltin);
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore][Skills] Failed to build metadata for '{skillFile}': {ex.Message}");
                return null;
            }
        }

        private static string ResolveField(Dictionary<string, string> fields, string key, string fallback)
        {
            if (fields != null && fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;
            return fallback;
        }

        /// <summary>
        /// 无 frontmatter description 时的兜底：取正文第一段有意义的文本（跳过 # 标题）。
        /// </summary>
        private static string ExtractFallbackDescription(string body)
        {
            if (string.IsNullOrEmpty(body)) return string.Empty;

            var lines = body.Split('\n');
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;
                if (trimmed.StartsWith("#")) continue;
                if (trimmed.StartsWith("<!--")) continue;

                // 取第一行有效内容，最长 120 字符
                return trimmed.Length > 120 ? trimmed.Substring(0, 120) + "..." : trimmed;
            }
            return string.Empty;
        }

        /// <summary>
        /// 一个 skill 扫描根：目录路径 + 是否内置。
        /// 内置根在插件包内（随包分发）；外部根在项目内（可覆盖内置）。
        /// </summary>
        private readonly struct SkillScanRoot
        {
            public readonly string Path;
            public readonly bool IsBuiltin;

            public SkillScanRoot(string path, bool isBuiltin)
            {
                Path = path;
                IsBuiltin = isBuiltin;
            }
        }
    }
}
