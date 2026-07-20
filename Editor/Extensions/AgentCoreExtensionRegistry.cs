using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AgentCore.Editor.Config.Settings.Pages;
using UnityEngine;
using AgentCore.Editor.Utils;

namespace AgentCore.Editor.Extensions
{
    /// <summary>
    /// Discovers optional AgentCore extension contributions from loaded editor assemblies.
    /// </summary>
    public static class AgentCoreExtensionRegistry
    {
        private static readonly List<IAgentCorePanelContribution> _panels = new List<IAgentCorePanelContribution>();
        private static readonly List<IAgentCoreSettingsContribution> _settings = new List<IAgentCoreSettingsContribution>();
        private static readonly List<IAgentCoreSettingsPage> _pages = new List<IAgentCoreSettingsPage>();
        private static readonly List<IAgentCoreStatusContribution> _statuses = new List<IAgentCoreStatusContribution>();
        private static bool _isInitialized;

        /// <summary>
        /// Gets all discovered panel contributions ordered by their declared order and identifier.
        /// </summary>
        public static IReadOnlyList<IAgentCorePanelContribution> Panels
        {
            get
            {
                EnsureInitialized();
                return _panels;
            }
        }

        /// <summary>
        /// Gets all discovered settings contributions ordered by their declared order and identifier.
        /// </summary>
        public static IReadOnlyList<IAgentCoreSettingsContribution> Settings
        {
            get
            {
                EnsureInitialized();
                return _settings;
            }
        }

        /// <summary>
        /// Gets all dynamically discovered settings pages contributed by optional components,
        /// ordered by their declared order and identifier.
        /// </summary>
        public static IReadOnlyList<IAgentCoreSettingsPage> Pages
        {
            get
            {
                EnsureInitialized();
                return _pages;
            }
        }

        /// <summary>
        /// Gets all discovered toolbar status contributions ordered by their declared order and identifier.
        /// </summary>
        public static IReadOnlyList<IAgentCoreStatusContribution> Statuses
        {
            get
            {
                EnsureInitialized();
                return _statuses;
            }
        }

        /// <summary>
        /// Clears cached contributions and rescans all currently loaded assemblies.
        /// </summary>
        public static void Refresh()
        {
            _panels.Clear();
            _settings.Clear();
            _pages.Clear();
            _statuses.Clear();

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (var type in GetLoadableTypes(assembly))
                {
                    TryRegisterType(type);
                }
            }

            SortAndDeduplicate(_panels, contribution => contribution.Id);
            SortAndDeduplicate(_settings, contribution => contribution.Id);
            SortAndDeduplicate(_pages, page => page.Id);
            SortAndDeduplicate(_statuses, contribution => contribution.Id);
            _isInitialized = true;
        }

        private static void EnsureInitialized()
        {
            if (_isInitialized)
                return;

            Refresh();
        }

        private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(type => type != null);
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"AgentCore extension registry skipped assembly '{assembly.FullName}': {ex.Message}");
                return Enumerable.Empty<Type>();
            }
        }

        private static void TryRegisterType(Type type)
        {
            if (type == null || type.IsAbstract || type.IsInterface || type.IsGenericTypeDefinition)
                return;

            if (typeof(IAgentCorePanelContribution).IsAssignableFrom(type))
            {
                TryCreateContribution(type, _panels);
            }

            if (typeof(IAgentCoreSettingsContribution).IsAssignableFrom(type))
            {
                TryCreateContribution(type, _settings);
            }

            if (typeof(IAgentCoreSettingsPage).IsAssignableFrom(type))
            {
                // Only discover pages from optional component assemblies (not the main AgentCore.Editor assembly)
                // to avoid double-registering built-in pages.
                var assemblyName = type.Assembly.GetName().Name;
                if (assemblyName != null && assemblyName != "AgentCore.Editor")
                {
                    TryCreateContribution(type, _pages);
                }
            }

            if (typeof(IAgentCoreStatusContribution).IsAssignableFrom(type))
            {
                TryCreateContribution(type, _statuses);
            }
        }

        private static void TryCreateContribution<TContribution>(Type type, List<TContribution> target)
        {
            if (type.GetConstructor(Type.EmptyTypes) == null)
            {
                AgentCoreLog.Warning($"AgentCore extension contribution '{type.FullName}' skipped: missing public parameterless constructor.");
                return;
            }

            try
            {
                var contribution = (TContribution)Activator.CreateInstance(type);
                target.Add(contribution);
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"AgentCore extension contribution '{type.FullName}' failed to initialize: {ex.Message}");
            }
        }

        private static void SortAndDeduplicate<TContribution>(List<TContribution> contributions, Func<TContribution, string> idSelector)
        {
            var unique = contributions
                .Where(contribution => !string.IsNullOrWhiteSpace(idSelector(contribution)))
                .GroupBy(idSelector)
                .Select(group => group.First())
                .OrderBy(GetOrder)
                .ThenBy(idSelector, StringComparer.Ordinal)
                .ToList();

            contributions.Clear();
            contributions.AddRange(unique);
        }

        private static int GetOrder<TContribution>(TContribution contribution)
        {
            switch (contribution)
            {
                case IAgentCorePanelContribution panel:
                    return panel.Order;
                case IAgentCoreSettingsContribution settings:
                    return settings.Order;
                case IAgentCoreSettingsPage page:
                    return page.Order;
                case IAgentCoreStatusContribution status:
                    return status.Order;
                default:
                    return 0;
            }
        }
    }
}
