using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Config;
using AgentCore.Editor.Skills;
using AgentCore.Editor.Tools.Infrastructure;
using AgentCore.Editor.Tools.Safety;
using Newtonsoft.Json.Linq;
using UnityEngine;
using AgentCore.Editor.Utils;

namespace AgentCore.Editor.Tools.Native.Meta
{
    /// <summary>
    /// 元工具：允许 LLM 按需加载和卸载 Skill（ADR-18 Phase 1）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 此工具始终可见（AlwaysVisible）。LLM 遇到需要专业领域指引的任务时，
    /// 先调 <c>action=list</c> 查看可用 skill，再调 <c>action=load</c> 加载所需 skill。
    /// 加载后 skill 内容以带 marker 的 system message 形式常驻上下文，直到 <c>unload</c> 或会话结束。
    /// </para>
    /// <para>
    /// 与 <see cref="RequestToolsTool"/> 结构对称，但作用域是"知识"而非"工具"。
    /// </para>
    /// </remarks>
    [AgentTool("load_skill",
        Description = "Load domain-specific skill guides (workflows / conventions / checklists) into the current conversation. " +
            "action:list — enumerate all available skills with names and descriptions (call this BEFORE claiming you lack expertise). " +
            "action:load — load one skill by name; the guide is injected into every subsequent turn until unloaded or session ends. " +
            "action:list_loaded — show what is currently active. " +
            "action:unload — remove a skill from context. " +
            "action:reload — force re-read from disk (useful after skill file edits). " +
            "Skills live in <project-root>/.agents/skills/<name>/SKILL.md and are separate from PROJECT.md. " +
            "USE FOR: architecture decisions (unity-blueprints), scene wiring (unity-scene-contracts), performance investigation (unity-performance-analysis), " +
            "documentation writing (unity-documentation), pattern selection (unity-patterns), and other domain-specific tasks. " +
            "Prefer loading a skill over asking the user for guidance you should already have.",
        Category = "Meta",
        RequiresMainThread = false,
        RiskLevel = ToolRiskLevel.Low,
        Capabilities = ToolCapability.None,
        Visibility = ToolVisibility.AlwaysVisible)]
    public class LoadSkillTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""list"", ""load"", ""list_loaded"", ""unload"", ""reload""],
                    ""description"": ""Action to perform. See tool description for semantics.""
                },
                ""name"": {
                    ""type"": ""string"",
                    ""description"": ""(load / unload / reload) Skill name to operate on. Must exactly match a skill from action=list.""
                }
            },
            ""required"": [""action""]
        }");

        public ToolMetadata Metadata => new ToolMetadata(
            name: "load_skill",
            description: "Discover, load, and unload domain skill guides. Skills provide task-specific workflows without cluttering the base system prompt.",
            category: "Meta",
            parametersSchema: _parametersSchema,
            requiresMainThread: false);

        /// <summary>
        /// 当前会话的 Skill 作用域状态引用。
        /// 由 <c>AgentLoop.SkillContext.cs</c> 在初始化时通过 <see cref="SetScopeState"/> 注入。
        /// </summary>
        private static SkillScopeState _scopeState;

        /// <summary>
        /// 当 LLM 调 <c>load</c> / <c>unload</c> / <c>reload</c> 时，触发本事件通知 AgentLoop
        /// 更新 skill system message 集合。参数：(skillName, action) — action ∈ {"load", "unload", "reload"}。
        /// </summary>
        public static event Action<string, string> OnSkillMutation;

        public static void SetScopeState(SkillScopeState state)
        {
            _scopeState = state;
        }

        public Task<ToolResult> ExecuteAsync(JObject parameters, CancellationToken cancellationToken = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ToolResponse response;

            try
            {
                if (!AgentCoreSettings.instance.skillsEnabled)
                {
                    response = ToolResponse.Fail("Skill system is disabled in AgentCore Settings.");
                }
                else
                {
                    var action = ToolHelpers.GetRequiredString(parameters, "action").ToLowerInvariant();
                    switch (action)
                    {
                        case "list":
                            response = HandleList();
                            break;
                        case "load":
                            response = HandleLoad(parameters);
                            break;
                        case "list_loaded":
                            response = HandleListLoaded();
                            break;
                        case "unload":
                            response = HandleUnload(parameters);
                            break;
                        case "reload":
                            response = HandleReload(parameters);
                            break;
                        default:
                            response = ToolResponse.Fail(
                                $"Unknown action: '{action}'. Valid actions: list, load, list_loaded, unload, reload.");
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                response = ToolResponse.Fail($"Error: {ex.Message}");
            }

            sw.Stop();
            return Task.FromResult(response.ToToolResult(sw.Elapsed.TotalMilliseconds));
        }

        private static ToolResponse HandleList()
        {
            var all = SkillRegistry.Instance.GetAll();
            var state = _scopeState;

            if (all.Count == 0)
            {
                var dirs = SkillRegistry.Instance.GetSearchDirectories();
                return ToolResponse.OkWithData(
                    new JObject
                    {
                        ["total"] = 0,
                        ["search_directories"] = new JArray(dirs)
                    },
                    "No skills found. Place SKILL.md files under <project-root>/.agents/skills/<name>/ to expose them.");
            }

            var result = new JObject
            {
                ["total"] = all.Count,
                ["skills"] = new JArray(all.Select(s => new JObject
                {
                    ["name"] = s.Name,
                    ["description"] = s.Description,
                    ["category"] = s.Category,
                    ["version"] = s.Version,
                    ["estimated_tokens"] = s.EstimatedTokens,
                    ["is_loaded"] = state != null && state.IsLoaded(s.Name)
                }))
            };

            return ToolResponse.OkWithData(result,
                $"Found {all.Count} available skill(s). Use action=load with a name to activate one.");
        }

        private ToolResponse HandleLoad(JObject parameters)
        {
            var state = _scopeState;
            if (state == null)
                return ToolResponse.Fail("Skill scoping is not initialized. Cannot load skills.");

            var name = ToolHelpers.GetRequiredString(parameters, "name").Trim();
            var meta = SkillRegistry.Instance.TryGet(name);
            if (meta == null)
                return ToolResponse.Fail($"Skill '{name}' not found. Use action=list to see available skills.");

            if (state.IsLoaded(name))
            {
                return ToolResponse.OkWithData(
                    BuildStatusData(name, meta, alreadyLoaded: true),
                    $"Skill '{name}' is already loaded. Use action=reload to force refresh from disk.");
            }

            var content = SkillRegistry.Instance.GetContent(name);
            if (string.IsNullOrEmpty(content))
                return ToolResponse.Fail($"Skill '{name}' file is empty or unreadable at {meta.FilePath}.");

            state.MarkLoaded(name);

            try { OnSkillMutation?.Invoke(name, "load"); }
            catch (Exception ex) { AgentCoreLog.Warning($"[AgentCore][Skills] OnSkillMutation handler failed: {ex.Message}"); }

            // 软 token budget warning（ADR-18 D5-b）
            var totalTokens = ComputeLoadedTokens(state);
            var warning = totalTokens > SkillSoftBudgetTokens
                ? $" WARNING: Total loaded skill tokens ~{totalTokens} exceed soft budget {SkillSoftBudgetTokens}. Consider unloading unused skills."
                : string.Empty;

            return ToolResponse.OkWithData(
                BuildStatusData(name, meta, alreadyLoaded: false),
                $"Skill '{name}' loaded (~{meta.EstimatedTokens} tokens). It will be present in all subsequent turns.{warning}");
        }

        private static ToolResponse HandleListLoaded()
        {
            var state = _scopeState;
            if (state == null || state.LoadedCount == 0)
            {
                return ToolResponse.OkWithData(
                    new JObject { ["total"] = 0, ["loaded"] = new JArray() },
                    "No skills currently loaded.");
            }

            var loaded = state.LoadedSkills.Select(name =>
            {
                var meta = SkillRegistry.Instance.TryGet(name);
                return new JObject
                {
                    ["name"] = name,
                    ["description"] = meta?.Description ?? "(not found in registry)",
                    ["estimated_tokens"] = meta?.EstimatedTokens ?? 0
                };
            });

            var totalTokens = ComputeLoadedTokens(state);
            return ToolResponse.OkWithData(
                new JObject
                {
                    ["total"] = state.LoadedCount,
                    ["total_estimated_tokens"] = totalTokens,
                    ["loaded"] = new JArray(loaded)
                },
                $"{state.LoadedCount} skill(s) currently loaded (~{totalTokens} tokens).");
        }

        private ToolResponse HandleUnload(JObject parameters)
        {
            var state = _scopeState;
            if (state == null)
                return ToolResponse.Fail("Skill scoping is not initialized.");

            var name = ToolHelpers.GetRequiredString(parameters, "name").Trim();

            if (!state.IsLoaded(name))
                return ToolResponse.Fail($"Skill '{name}' is not currently loaded.");

            state.Unload(name);

            try { OnSkillMutation?.Invoke(name, "unload"); }
            catch (Exception ex) { AgentCoreLog.Warning($"[AgentCore][Skills] OnSkillMutation handler failed: {ex.Message}"); }

            return ToolResponse.OkWithData(
                new JObject { ["name"] = name, ["remaining"] = state.LoadedCount },
                $"Skill '{name}' unloaded. {state.LoadedCount} skill(s) remain loaded.");
        }

        private ToolResponse HandleReload(JObject parameters)
        {
            var state = _scopeState;
            if (state == null)
                return ToolResponse.Fail("Skill scoping is not initialized.");

            var name = ToolHelpers.GetRequiredString(parameters, "name").Trim();

            // 强制刷新 registry 缓存，重新读盘
            SkillRegistry.Instance.Rescan();

            var meta = SkillRegistry.Instance.TryGet(name);
            if (meta == null)
            {
                // registry 刷新后 skill 不见了，同步清理 state
                if (state.IsLoaded(name)) state.Unload(name);
                return ToolResponse.Fail($"Skill '{name}' no longer exists after reload.");
            }

            // 触发 message 重建（AgentLoop 会移除旧的、插入新的）
            state.MarkLoaded(name); // 幂等操作

            try { OnSkillMutation?.Invoke(name, "reload"); }
            catch (Exception ex) { AgentCoreLog.Warning($"[AgentCore][Skills] OnSkillMutation handler failed: {ex.Message}"); }

            return ToolResponse.OkWithData(
                BuildStatusData(name, meta, alreadyLoaded: false),
                $"Skill '{name}' reloaded from disk (~{meta.EstimatedTokens} tokens).");
        }

        private static JObject BuildStatusData(string name, SkillMetadata meta, bool alreadyLoaded)
        {
            return new JObject
            {
                ["name"] = name,
                ["description"] = meta.Description,
                ["category"] = meta.Category,
                ["estimated_tokens"] = meta.EstimatedTokens,
                ["already_loaded"] = alreadyLoaded
            };
        }

        private const int SkillSoftBudgetTokens = 15000; // ADR-18 D5-b

        private static int ComputeLoadedTokens(SkillScopeState state)
        {
            int total = 0;
            foreach (var name in state.LoadedSkills)
            {
                var m = SkillRegistry.Instance.TryGet(name);
                if (m != null) total += m.EstimatedTokens;
            }
            return total;
        }
    }
}
