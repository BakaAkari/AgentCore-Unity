using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

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
                Debug.LogWarning($"[AgentCore][Skills] Failed to read '{name}' at {meta.FilePath}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 返回本次会话使用的所有搜索目录（供 UI / diagnostics 显示）。
        /// </summary>
        public IReadOnlyList<string> GetSearchDirectories()
        {
            var dirs = new List<string>();
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (projectRoot != null)
            {
                dirs.Add(Path.Combine(projectRoot, ".agents", "skills"));
                dirs.Add(Path.Combine(projectRoot, "Assets", ".agents", "skills"));
            }
            return dirs;
        }

        private void EnsureScanned()
        {
            if (_isScanned) return;

            try
            {
                foreach (var dir in GetSearchDirectories())
                {
                    if (!Directory.Exists(dir)) continue;
                    ScanDirectory(dir);
                }

                AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore][Skills] Registry scan complete: {_skills.Count} skill(s) found.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AgentCore][Skills] Registry scan failed: {ex.Message}");
            }
            finally
            {
                _isScanned = true;
            }
        }

        /// <summary>
        /// 扫描单个目录下所有子目录中的 SKILL.md 文件。
        /// </summary>
        private void ScanDirectory(string skillsRoot)
        {
            try
            {
                foreach (var subDir in Directory.EnumerateDirectories(skillsRoot))
                {
                    var skillFile = Path.Combine(subDir, "SKILL.md");
                    if (!File.Exists(skillFile)) continue;

                    var meta = TryBuildMetadata(subDir, skillFile);
                    if (meta == null) continue;

                    if (_skills.ContainsKey(meta.Name))
                    {
                        Debug.LogWarning($"[AgentCore][Skills] Duplicate skill name '{meta.Name}'. " +
                                         $"Existing: {_skills[meta.Name].FilePath}; ignoring: {meta.FilePath}");
                        continue;
                    }

                    _skills[meta.Name] = meta;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AgentCore][Skills] Failed to scan '{skillsRoot}': {ex.Message}");
            }
        }

        /// <summary>
        /// 从 SKILL.md 文件构建元数据（不缓存全文）。
        /// </summary>
        private static SkillMetadata TryBuildMetadata(string skillDir, string skillFile)
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
                    charCount: raw.Length);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AgentCore][Skills] Failed to build metadata for '{skillFile}': {ex.Message}");
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
    }
}
