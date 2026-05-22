using System;
using System.Collections.Generic;
using System.Linq;
using AgentCore.Editor.Config.Settings.Sections;

namespace AgentCore.Editor.Config.Settings
{
    /// <summary>
    /// Registers and exposes AgentCore settings sections.
    /// </summary>
    public static class AgentCoreSettingsRegistry
    {
        private static readonly List<IAgentCoreSettingsSection> _sections = new List<IAgentCoreSettingsSection>();
        private static bool _initialized;

        /// <summary>
        /// Gets all registered settings sections ordered by their declared order and identifier.
        /// </summary>
        public static IReadOnlyList<IAgentCoreSettingsSection> Sections
        {
            get
            {
                EnsureInitialized();
                return _sections;
            }
        }

        /// <summary>
        /// Clears and rebuilds the settings section registry.
        /// </summary>
        public static void Refresh()
        {
            _sections.Clear();
            RegisterBuiltInSections();

            var sorted = _sections
                .Where(section => section != null && !string.IsNullOrWhiteSpace(section.Id))
                .GroupBy(section => section.Id, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(section => section.Order)
                .ThenBy(section => section.Id, StringComparer.Ordinal)
                .ToList();

            _sections.Clear();
            _sections.AddRange(sorted);
            _initialized = true;
        }

        /// <summary>
        /// Gets a section by identifier.
        /// </summary>
        /// <param name="id">The stable section identifier.</param>
        /// <returns>The matching section, or null when absent.</returns>
        public static IAgentCoreSettingsSection GetSection(string id)
        {
            EnsureInitialized();
            return _sections.FirstOrDefault(section => section.Id == id);
        }

        private static void EnsureInitialized()
        {
            if (_initialized)
                return;

            Refresh();
        }

        private static void RegisterBuiltInSections()
        {
            Register(new GeneralSettingsSection());
            Register(new ModelSettingsSection());
            Register(new AgentSettingsSection());
            Register(new ContextSettingsSection());
            Register(new MemorySettingsSection());
            Register(new KnowledgeSettingsSection());
            Register(new ContextManagementSettingsSection());
            Register(new ExtensionsSettingsSection());
            Register(new ToolsSettingsSection());
            Register(new InterfaceSettingsSection());
            Register(new DiagnosticsSettingsSection());
        }

        private static void Register(IAgentCoreSettingsSection section)
        {
            _sections.Add(section);
        }
    }
}
