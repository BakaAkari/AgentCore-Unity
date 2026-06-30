using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Tools.Infrastructure;
using AgentCore.Editor.Tools.Safety;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace AgentCore.Editor.Tools.Native.Extended
{
    /// <summary>
    /// Manage Unity packages: list, search, install, remove, get info, check versions,
    /// and inspect dependencies via the Unity Package Manager API.
    /// </summary>
    [AgentTool("manage_package",
        Description = "Unity Package Manager (UPM) operations. " +
                      "Actions: list (all installed packages with versions), search (query Unity registry), " +
                      "install (add package by name@version or git URL), remove (uninstall package), " +
                      "get_info (detailed metadata for installed package), check_updates (find available upgrades), " +
                      "get_dependencies (dependency tree of a package). " +
                      "USE FOR: installing packages from Unity registry or git, removing unused packages, " +
                      "checking what version is installed, finding if a package exists in the registry, understanding dependency chains. " +
                      "NOT FOR: Asset Store packages (those are .unitypackage imports), NuGet packages, custom package development. " +
                      "REQUIRES CONFIRMATION: install/remove actions modify the project manifest and trigger reimport. " +
                      "ACTIVATE WHEN: user mentions 'install package', 'UPM', 'package manager', 'add package', 'remove package', 'package version'.",
        Category = "Extended",
        RequiresMainThread = true,
        RiskLevel = ToolRiskLevel.High,
        Capabilities = ToolCapability.InstallPackages,
        RequiresConfirmation = true,
        Visibility = ToolVisibility.OnDemand)]
    public class ManagePackageTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""list"", ""search"", ""get_info"", ""install"", ""remove"", ""get_versions"", ""check_installed"", ""get_dependencies"", ""refresh""],
                    ""description"": ""Action to perform on packages""
                },
                ""packageName"": {
                    ""type"": ""string"",
                    ""description"": ""Package identifier (e.g. 'com.unity.cinemachine')""
                },
                ""version"": {
                    ""type"": ""string"",
                    ""description"": ""Package version to install (optional, defaults to latest)""
                },
                ""query"": {
                    ""type"": ""string"",
                    ""description"": ""Search query string (for search action)""
                },
                ""includeBuiltIn"": {
                    ""type"": ""boolean"",
                    ""description"": ""Include built-in packages in list (default: false)""
                }
            },
            ""required"": [""action""]
        }");

        /// <summary>
        /// Tool metadata for registration and LLM discovery.
        /// </summary>
        public ToolMetadata Metadata => new ToolMetadata(
            name: "manage_package",
            description: "Manage Unity packages: list installed, search registry, install/remove packages, get info, check versions, and inspect dependencies via Unity Package Manager.",
            category: "Extended",
            parametersSchema: _parametersSchema,
            requiresMainThread: true
        );

        /// <summary>
        /// Maximum time in milliseconds to wait for a Package Manager request to complete.
        /// </summary>
        private const int MaxWaitTimeMs = 30000;

        /// <summary>
        /// Polling interval in milliseconds for Package Manager requests.
        /// </summary>
        private const int PollIntervalMs = 50;

        /// <summary>
        /// Execute a package management action.
        /// </summary>
        public Task<ToolResult> ExecuteAsync(JObject parameters, CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();
            ToolResponse response;

            try
            {
                var action = ToolHelpers.GetRequiredString(parameters, "action").ToLowerInvariant();

                switch (action)
                {
                    case "list":
                        response = HandleList(parameters);
                        break;
                    case "search":
                        response = HandleSearch(parameters);
                        break;
                    case "get_info":
                        response = HandleGetInfo(parameters);
                        break;
                    case "install":
                        response = HandleInstall(parameters);
                        break;
                    case "remove":
                        response = HandleRemove(parameters);
                        break;
                    case "get_versions":
                        response = HandleGetVersions(parameters);
                        break;
                    case "check_installed":
                        response = HandleCheckInstalled(parameters);
                        break;
                    case "get_dependencies":
                        response = HandleGetDependencies(parameters);
                        break;
                    case "refresh":
                        response = HandleRefresh();
                        break;
                    default:
                        response = ToolResponse.Fail($"Unknown action: {action}. Valid actions: list, search, get_info, install, remove, get_versions, check_installed, get_dependencies, refresh");
                        break;
                }
            }
            catch (Exception ex)
            {
                response = ToolResponse.Fail($"Error: {ex.Message}");
            }

            sw.Stop();
            return Task.FromResult(response.ToToolResult(sw.Elapsed.TotalMilliseconds));
        }

        #region Request Helpers

        /// <summary>
        /// Wait for a Package Manager request to complete with timeout.
        /// </summary>
        private static bool WaitForRequest(Request request, int timeoutMs = MaxWaitTimeMs)
        {
            var elapsed = 0;
            while (!request.IsCompleted && elapsed < timeoutMs)
            {
                System.Threading.Thread.Sleep(PollIntervalMs);
                elapsed += PollIntervalMs;
            }
            return request.IsCompleted;
        }

        /// <summary>
        /// Build a standard error message from a failed Package Manager request.
        /// </summary>
        private static string GetRequestError(Request request)
        {
            if (request.Error != null)
                return $"{request.Error.errorCode}: {request.Error.message}";
            return "Unknown Package Manager error.";
        }

        /// <summary>
        /// List all installed packages and return them as a collection.
        /// </summary>
        private static PackageCollection ListInstalledPackages(bool includeOffline = false)
        {
            var request = Client.List(includeOffline);
            if (!WaitForRequest(request))
                return null;
            if (request.Status != StatusCode.Success)
                return null;
            return request.Result;
        }

        #endregion

        #region Action Handlers

        /// <summary>
        /// List all installed packages.
        /// </summary>
        private ToolResponse HandleList(JObject parameters)
        {
            var includeBuiltIn = ToolHelpers.GetOptionalBool(parameters, "includeBuiltIn", false);

            var request = Client.List(true);
            if (!WaitForRequest(request))
                return ToolResponse.Fail("Package list request timed out.");

            if (request.Status != StatusCode.Success)
                return ToolResponse.Fail($"Failed to list packages: {GetRequestError(request)}");

            var packages = new List<Dictionary<string, object>>();

            foreach (var pkg in request.Result)
            {
                // Filter built-in packages if not requested
                if (!includeBuiltIn && pkg.source == PackageSource.BuiltIn)
                    continue;

                packages.Add(new Dictionary<string, object>
                {
                    { "name", pkg.name },
                    { "version", pkg.version },
                    { "displayName", pkg.displayName },
                    { "source", pkg.source.ToString() },
                    { "status", pkg.status.ToString() }
                });
            }

            // Sort by name
            packages.Sort((a, b) => string.Compare((string)a["name"], (string)b["name"], StringComparison.Ordinal));

            var result = new Dictionary<string, object>
            {
                { "packages", packages },
                { "count", packages.Count },
                { "includeBuiltIn", includeBuiltIn }
            };

            return ToolResponse.OkWithData(result, $"Found {packages.Count} installed package(s).");
        }

        /// <summary>
        /// Search for packages in the Unity registry.
        /// </summary>
        private ToolResponse HandleSearch(JObject parameters)
        {
            var query = ToolHelpers.GetOptionalString(parameters, "query", "");

            if (string.IsNullOrEmpty(query))
                return ToolResponse.Fail("Parameter 'query' is required for search action.");

            var request = Client.SearchAll();
            if (!WaitForRequest(request))
                return ToolResponse.Fail("Package search request timed out.");

            if (request.Status != StatusCode.Success)
                return ToolResponse.Fail($"Failed to search packages: {GetRequestError(request)}");

            var queryLower = query.ToLowerInvariant();
            var matches = new List<Dictionary<string, object>>();

            foreach (var pkg in request.Result)
            {
                if (pkg.name.ToLowerInvariant().Contains(queryLower) ||
                    (pkg.displayName != null && pkg.displayName.ToLowerInvariant().Contains(queryLower)) ||
                    (pkg.description != null && pkg.description.ToLowerInvariant().Contains(queryLower)))
                {
                    matches.Add(new Dictionary<string, object>
                    {
                        { "name", pkg.name },
                        { "version", pkg.version },
                        { "displayName", pkg.displayName ?? pkg.name },
                        { "description", TruncateString(pkg.description, 120) }
                    });
                }
            }

            var result = new Dictionary<string, object>
            {
                { "query", query },
                { "packages", matches },
                { "count", matches.Count }
            };

            return ToolResponse.OkWithData(result, $"Found {matches.Count} package(s) matching '{query}'.");
        }

        /// <summary>
        /// Get detailed information about a specific package.
        /// </summary>
        private ToolResponse HandleGetInfo(JObject parameters)
        {
            var packageName = ToolHelpers.GetRequiredString(parameters, "packageName");

            // First try to find it in installed packages
            var listRequest = Client.List(true);
            if (!WaitForRequest(listRequest))
                return ToolResponse.Fail("Package list request timed out.");

            if (listRequest.Status != StatusCode.Success)
                return ToolResponse.Fail($"Failed to list packages: {GetRequestError(listRequest)}");

            PackageInfo foundPkg = null;
            foreach (var pkg in listRequest.Result)
            {
                if (string.Equals(pkg.name, packageName, StringComparison.OrdinalIgnoreCase))
                {
                    foundPkg = pkg;
                    break;
                }
            }

            if (foundPkg == null)
            {
                // Try searching in registry
                var searchRequest = Client.SearchAll();
                if (WaitForRequest(searchRequest) && searchRequest.Status == StatusCode.Success)
                {
                    foreach (var pkg in searchRequest.Result)
                    {
                        if (string.Equals(pkg.name, packageName, StringComparison.OrdinalIgnoreCase))
                        {
                            foundPkg = pkg;
                            break;
                        }
                    }
                }
            }

            if (foundPkg == null)
                return ToolResponse.Fail($"Package '{packageName}' not found in installed packages or Unity registry.");

            var info = new Dictionary<string, object>
            {
                { "name", foundPkg.name },
                { "displayName", foundPkg.displayName ?? foundPkg.name },
                { "version", foundPkg.version },
                { "description", foundPkg.description },
                { "source", foundPkg.source.ToString() },
                { "status", foundPkg.status.ToString() },
                { "category", foundPkg.category ?? "unknown" },
                { "documentationUrl", foundPkg.documentationUrl ?? "" },
                { "changelogUrl", foundPkg.changelogUrl ?? "" }
            };

            // Dependencies
            if (foundPkg.dependencies != null && foundPkg.dependencies.Length > 0)
            {
                var deps = foundPkg.dependencies.Select(d => new Dictionary<string, object>
                {
                    { "name", d.name },
                    { "version", d.version }
                }).ToList();
                info["dependencies"] = deps;
            }

            // Versions
            if (foundPkg.versions != null)
            {
                var versions = new Dictionary<string, object>();
                if (!string.IsNullOrEmpty(foundPkg.versions.recommended))
                    versions["recommended"] = foundPkg.versions.recommended;
                if (!string.IsNullOrEmpty(foundPkg.versions.latest))
                    versions["latest"] = foundPkg.versions.latest;
                if (foundPkg.versions.compatible != null && foundPkg.versions.compatible.Length > 0)
                    versions["compatible"] = foundPkg.versions.compatible;
                if (foundPkg.versions.all != null && foundPkg.versions.all.Length > 0)
                    versions["allCount"] = foundPkg.versions.all.Length;
                info["versions"] = versions;
            }

            return ToolResponse.OkWithData(info, $"Package info for '{packageName}'.");
        }

        /// <summary>
        /// Install a package by name, optionally with a specific version.
        /// </summary>
        private ToolResponse HandleInstall(JObject parameters)
        {
            var packageName = ToolHelpers.GetRequiredString(parameters, "packageName");
            var version = ToolHelpers.GetOptionalString(parameters, "version", null);

            var packageId = string.IsNullOrEmpty(version) ? packageName : $"{packageName}@{version}";

            var request = Client.Add(packageId);
            if (!WaitForRequest(request))
                return ToolResponse.Fail($"Package install request timed out for '{packageId}'. The installation may still be in progress.");

            if (request.Status != StatusCode.Success)
                return ToolResponse.Fail($"Failed to install '{packageId}': {GetRequestError(request)}");

            var pkg = request.Result;
            var result = new Dictionary<string, object>
            {
                { "name", pkg.name },
                { "version", pkg.version },
                { "displayName", pkg.displayName ?? pkg.name },
                { "source", pkg.source.ToString() }
            };

            return ToolResponse.OkWithData(result, $"Successfully installed '{pkg.displayName ?? pkg.name}' v{pkg.version}.");
        }

        /// <summary>
        /// Remove an installed package.
        /// </summary>
        private ToolResponse HandleRemove(JObject parameters)
        {
            var packageName = ToolHelpers.GetRequiredString(parameters, "packageName");

            var request = Client.Remove(packageName);
            if (!WaitForRequest(request))
                return ToolResponse.Fail($"Package remove request timed out for '{packageName}'. The removal may still be in progress.");

            if (request.Status != StatusCode.Success)
                return ToolResponse.Fail($"Failed to remove '{packageName}': {GetRequestError(request)}");

            return ToolResponse.Ok($"Successfully removed package '{packageName}'.");
        }

        /// <summary>
        /// Get all available versions for a package.
        /// </summary>
        private ToolResponse HandleGetVersions(JObject parameters)
        {
            var packageName = ToolHelpers.GetRequiredString(parameters, "packageName");

            // Search for the package to get version info
            var searchRequest = Client.SearchAll();
            if (!WaitForRequest(searchRequest))
                return ToolResponse.Fail("Package search request timed out.");

            if (searchRequest.Status != StatusCode.Success)
                return ToolResponse.Fail($"Failed to search packages: {GetRequestError(searchRequest)}");

            PackageInfo foundPkg = null;
            foreach (var pkg in searchRequest.Result)
            {
                if (string.Equals(pkg.name, packageName, StringComparison.OrdinalIgnoreCase))
                {
                    foundPkg = pkg;
                    break;
                }
            }

            // Also check installed packages
            if (foundPkg == null)
            {
                var listRequest = Client.List(true);
                if (WaitForRequest(listRequest) && listRequest.Status == StatusCode.Success)
                {
                    foreach (var pkg in listRequest.Result)
                    {
                        if (string.Equals(pkg.name, packageName, StringComparison.OrdinalIgnoreCase))
                        {
                            foundPkg = pkg;
                            break;
                        }
                    }
                }
            }

            if (foundPkg == null)
                return ToolResponse.Fail($"Package '{packageName}' not found.");

            var result = new Dictionary<string, object>
            {
                { "name", foundPkg.name },
                { "displayName", foundPkg.displayName ?? foundPkg.name },
                { "currentVersion", foundPkg.version }
            };

            if (foundPkg.versions != null)
            {
                if (!string.IsNullOrEmpty(foundPkg.versions.recommended))
                    result["recommended"] = foundPkg.versions.recommended;
                if (!string.IsNullOrEmpty(foundPkg.versions.latest))
                    result["latest"] = foundPkg.versions.latest;
                if (foundPkg.versions.compatible != null)
                    result["compatible"] = foundPkg.versions.compatible;
                if (foundPkg.versions.all != null)
                    result["all"] = foundPkg.versions.all;
            }

            return ToolResponse.OkWithData(result, $"Version info for '{packageName}'.");
        }

        /// <summary>
        /// Check if a specific package is installed and return its status.
        /// </summary>
        private ToolResponse HandleCheckInstalled(JObject parameters)
        {
            var packageName = ToolHelpers.GetRequiredString(parameters, "packageName");

            var listRequest = Client.List(true);
            if (!WaitForRequest(listRequest))
                return ToolResponse.Fail("Package list request timed out.");

            if (listRequest.Status != StatusCode.Success)
                return ToolResponse.Fail($"Failed to list packages: {GetRequestError(listRequest)}");

            PackageInfo foundPkg = null;
            foreach (var pkg in listRequest.Result)
            {
                if (string.Equals(pkg.name, packageName, StringComparison.OrdinalIgnoreCase))
                {
                    foundPkg = pkg;
                    break;
                }
            }

            if (foundPkg == null)
            {
                var result = new Dictionary<string, object>
                {
                    { "packageName", packageName },
                    { "installed", false }
                };
                return ToolResponse.OkWithData(result, $"Package '{packageName}' is NOT installed.");
            }
            else
            {
                var result = new Dictionary<string, object>
                {
                    { "packageName", packageName },
                    { "installed", true },
                    { "version", foundPkg.version },
                    { "displayName", foundPkg.displayName ?? foundPkg.name },
                    { "source", foundPkg.source.ToString() },
                    { "status", foundPkg.status.ToString() }
                };
                return ToolResponse.OkWithData(result, $"Package '{packageName}' is installed (v{foundPkg.version}).");
            }
        }

        /// <summary>
        /// Get the dependency tree for a specific package.
        /// </summary>
        private ToolResponse HandleGetDependencies(JObject parameters)
        {
            var packageName = ToolHelpers.GetRequiredString(parameters, "packageName");

            // Search installed packages first
            var listRequest = Client.List(true);
            if (!WaitForRequest(listRequest))
                return ToolResponse.Fail("Package list request timed out.");

            if (listRequest.Status != StatusCode.Success)
                return ToolResponse.Fail($"Failed to list packages: {GetRequestError(listRequest)}");

            PackageInfo foundPkg = null;
            foreach (var pkg in listRequest.Result)
            {
                if (string.Equals(pkg.name, packageName, StringComparison.OrdinalIgnoreCase))
                {
                    foundPkg = pkg;
                    break;
                }
            }

            // Fallback to registry search
            if (foundPkg == null)
            {
                var searchRequest = Client.SearchAll();
                if (WaitForRequest(searchRequest) && searchRequest.Status == StatusCode.Success)
                {
                    foreach (var pkg in searchRequest.Result)
                    {
                        if (string.Equals(pkg.name, packageName, StringComparison.OrdinalIgnoreCase))
                        {
                            foundPkg = pkg;
                            break;
                        }
                    }
                }
            }

            if (foundPkg == null)
                return ToolResponse.Fail($"Package '{packageName}' not found.");

            var dependencies = new List<Dictionary<string, object>>();

            if (foundPkg.dependencies != null)
            {
                foreach (var dep in foundPkg.dependencies)
                {
                    var depInfo = new Dictionary<string, object>
                    {
                        { "name", dep.name },
                        { "version", dep.version }
                    };

                    // Check if dependency is installed
                    var isInstalled = false;
                    foreach (var pkg in listRequest.Result)
                    {
                        if (string.Equals(pkg.name, dep.name, StringComparison.OrdinalIgnoreCase))
                        {
                            isInstalled = true;
                            depInfo["installedVersion"] = pkg.version;
                            break;
                        }
                    }
                    depInfo["installed"] = isInstalled;

                    dependencies.Add(depInfo);
                }
            }

            var result = new Dictionary<string, object>
            {
                { "packageName", packageName },
                { "version", foundPkg.version },
                { "dependencies", dependencies },
                { "dependencyCount", dependencies.Count }
            };

            return ToolResponse.OkWithData(result, $"Package '{packageName}' has {dependencies.Count} direct dependency(ies).");
        }

        /// <summary>
        /// Force refresh the Package Manager cache.
        /// </summary>
        private ToolResponse HandleRefresh()
        {
            // Resolve forces a re-resolution of all packages
            var resolveMethod = typeof(Client).GetMethod("Resolve", Type.EmptyTypes);
            var resolveResult = resolveMethod != null ? resolveMethod.Invoke(null, null) : null;
            var request = resolveResult as Request;
            if (request == null)
                return ToolResponse.Ok("Package Manager cache refresh requested successfully.");

            if (!WaitForRequest(request))
                return ToolResponse.Fail("Package resolve/refresh request timed out.");

            if (request.Status != StatusCode.Success)
                return ToolResponse.Fail($"Failed to refresh packages: {GetRequestError(request)}");

            return ToolResponse.Ok("Package Manager cache refreshed successfully.");
        }

        #endregion

        #region Utilities

        /// <summary>
        /// Truncate a string to a maximum length, appending "..." if truncated.
        /// </summary>
        private static string TruncateString(string input, int maxLength)
        {
            if (string.IsNullOrEmpty(input)) return "";
            if (input.Length <= maxLength) return input;
            return input.Substring(0, maxLength - 3) + "...";
        }

        #endregion
    }
}
