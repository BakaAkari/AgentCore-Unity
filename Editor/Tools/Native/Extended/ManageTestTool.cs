using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Tools.Infrastructure;
using AgentCore.Editor.Tools.Safety;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Tools.Native.Extended
{
    /// <summary>
    /// Run and manage Unity Test Runner tests (EditMode and PlayMode).
    /// Uses reflection to access Test Framework API since it is an optional package.
    /// </summary>
    [AgentTool("manage_test",
        Description = "Run and manage Unity Test Runner tests (EditMode and PlayMode). Supports listing, running, cancelling tests and creating test scripts/fixtures.",
        Category = "Extended",
        RequiresMainThread = true,
        RiskLevel = ToolRiskLevel.Medium,
        Capabilities = ToolCapability.ExecuteCode | ToolCapability.ModifyScripts,
        Visibility = ToolVisibility.OnDemand)]
    public class ManageTestTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""list_tests"", ""run_tests"", ""get_results"", ""create_test"", ""cancel"", ""create_test_fixture""],
                    ""description"": ""Action to perform""
                },
                ""mode"": { ""type"": ""string"", ""description"": ""Test mode: edit, play, or all (default: all)"" },
                ""filter"": { ""type"": ""string"", ""description"": ""Filter string for test names (optional)"" },
                ""name"": { ""type"": ""string"", ""description"": ""Test class/fixture name for create_test/create_test_fixture"" },
                ""path"": { ""type"": ""string"", ""description"": ""Output folder path for create_test/create_test_fixture (default: Assets/Tests)"" },
                ""namespace"": { ""type"": ""string"", ""description"": ""Namespace for create_test_fixture (optional)"" },
                ""description"": { ""type"": ""string"", ""description"": ""Description comment for the test fixture (optional)"" }
            },
            ""required"": [""action""]
        }");

        /// <summary>
        /// Tool metadata for auto-discovery registration.
        /// </summary>
        public ToolMetadata Metadata => new ToolMetadata(
            name: "manage_test",
            description: "Run and manage Unity Test Runner tests (EditMode and PlayMode). Supports listing, running, cancelling tests and creating test scripts/fixtures.",
            category: "Extended",
            parametersSchema: _parametersSchema,
            requiresMainThread: true
        );

        // Cached reflection types
        private static Type _testRunnerApiType;
        private static Type _filterType;
        private static Type _testModeType;
        private static bool _reflectionInitialized;
        private static string _reflectionError;

        /// <summary>
        /// Execute the test management action specified in parameters.
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
                    case "list_tests":
                        response = HandleListTests(parameters);
                        break;
                    case "run_tests":
                        response = HandleRunTests(parameters);
                        break;
                    case "get_results":
                        response = HandleGetResults(parameters);
                        break;
                    case "create_test":
                        response = HandleCreateTest(parameters);
                        break;
                    case "cancel":
                        response = HandleCancel(parameters);
                        break;
                    case "create_test_fixture":
                        response = HandleCreateTestFixture(parameters);
                        break;
                    default:
                        response = ToolResponse.Fail($"Unknown action: {action}. Valid actions: list_tests, run_tests, get_results, create_test, cancel, create_test_fixture");
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

        #region Action Handlers

        /// <summary>
        /// List tests in the project by scanning assemblies for [Test] and [UnityTest] attributes.
        /// </summary>
        private ToolResponse HandleListTests(JObject parameters)
        {
            string mode = ToolHelpers.GetOptionalString(parameters, "mode", "all").ToLowerInvariant();
            string filter = ToolHelpers.GetOptionalString(parameters, "filter");

            if (mode != "edit" && mode != "play" && mode != "all")
                return ToolResponse.Fail($"Invalid mode: {mode}. Must be edit, play, or all.");

            // Try reflection-based TestRunnerApi first
            var apiResult = TryListTestsViaApi(mode, filter);
            if (apiResult != null)
                return apiResult;

            // Fallback: scan assemblies for test attributes
            return ListTestsByReflection(mode, filter);
        }

        /// <summary>
        /// Run tests using the TestRunnerApi via reflection.
        /// </summary>
        private ToolResponse HandleRunTests(JObject parameters)
        {
            string mode = ToolHelpers.GetRequiredString(parameters, "mode").ToLowerInvariant();
            string filter = ToolHelpers.GetOptionalString(parameters, "filter");

            if (mode != "edit" && mode != "play")
                return ToolResponse.Fail($"Invalid mode: {mode}. Must be edit or play.");

            if (!EnsureReflectionTypes())
                return ToolResponse.Fail($"Test Framework not available: {_reflectionError}. Please install 'com.unity.test-framework' package via Package Manager.");

            try
            {
                // Get TestRunnerApi instance
                var apiInstance = ScriptableObject.CreateInstance(_testRunnerApiType);
                if (apiInstance == null)
                    return ToolResponse.Fail("Failed to create TestRunnerApi instance.");

                try
                {
                    // Build ExecutionSettings
                    var executionSettingsType = FindType("UnityEditor.TestTools.TestRunner.Api.ExecutionSettings");
                    if (executionSettingsType == null)
                        return ToolResponse.Fail("Could not find ExecutionSettings type. Test Framework may be incompatible.");

                    // Resolve TestMode enum value
                    object testModeValue = ResolveTestMode(mode);
                    if (testModeValue == null)
                        return ToolResponse.Fail("Could not resolve TestMode enum value.");

                    // Create filter if needed
                    object filterObj = null;
                    if (_filterType != null)
                    {
                        filterObj = Activator.CreateInstance(_filterType);
                        // Set testMode on filter
                        var testModeProp = _filterType.GetProperty("testMode") ?? _filterType.GetProperty("TestMode");
                        if (testModeProp != null)
                            testModeProp.SetValue(filterObj, testModeValue);

                        // Set name filter if provided
                        if (!string.IsNullOrEmpty(filter))
                        {
                            var testNamesProp = _filterType.GetProperty("testNames") ?? _filterType.GetProperty("TestNames");
                            if (testNamesProp != null)
                                testNamesProp.SetValue(filterObj, new[] { filter });
                        }
                    }

                    // Create ExecutionSettings
                    object executionSettings;
                    var filterConstructor = executionSettingsType.GetConstructor(new[] { _filterType });
                    if (filterConstructor != null && filterObj != null)
                    {
                        executionSettings = filterConstructor.Invoke(new[] { filterObj });
                    }
                    else
                    {
                        executionSettings = Activator.CreateInstance(executionSettingsType);
                        // Try to set filter property
                        var filterProp = executionSettingsType.GetProperty("filter") ?? executionSettingsType.GetProperty("Filter");
                        if (filterProp != null && filterObj != null)
                            filterProp.SetValue(executionSettings, filterObj);
                    }

                    // Call Execute
                    var executeMethod = _testRunnerApiType.GetMethod("Execute",
                        new[] { executionSettingsType });
                    if (executeMethod == null)
                    {
                        // Try with different overloads
                        executeMethod = _testRunnerApiType.GetMethods()
                            .FirstOrDefault(m => m.Name == "Execute");
                    }

                    if (executeMethod == null)
                        return ToolResponse.Fail("Could not find Execute method on TestRunnerApi.");

                    executeMethod.Invoke(apiInstance, new[] { executionSettings });

                    return ToolResponse.OkWithData(new
                    {
                        status = "started",
                        mode,
                        filter = filter ?? "(none)",
                        message = "Tests started. Use get_results to check results after completion."
                    }, $"Test run started in {mode} mode" + (filter != null ? $" with filter '{filter}'" : ""));
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(apiInstance);
                }
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Failed to run tests: {ex.Message}");
            }
        }

        /// <summary>
        /// Get the most recent test results by reading test result XML files.
        /// </summary>
        private ToolResponse HandleGetResults(JObject parameters)
        {
            string mode = ToolHelpers.GetOptionalString(parameters, "mode", "all").ToLowerInvariant();

            if (mode != "edit" && mode != "play" && mode != "all")
                return ToolResponse.Fail($"Invalid mode: {mode}. Must be edit, play, or all.");

            var results = new List<object>();

            // Look for test result files in common locations
            var projectPath = Path.GetDirectoryName(Application.dataPath);
            var possiblePaths = new[]
            {
                Path.Combine(projectPath, "TestResults"),
                Path.Combine(projectPath, "Artifacts", "TestResults"),
                projectPath
            };

            var resultFiles = new List<string>();
            foreach (var dir in possiblePaths)
            {
                if (Directory.Exists(dir))
                {
                    resultFiles.AddRange(Directory.GetFiles(dir, "*.xml")
                        .Where(f => f.Contains("TestResults") || f.Contains("test-results") || f.Contains("EditMode") || f.Contains("PlayMode")));
                }
            }

            // Filter by mode
            if (mode == "edit")
                resultFiles = resultFiles.Where(f => f.Contains("EditMode") || !f.Contains("PlayMode")).ToList();
            else if (mode == "play")
                resultFiles = resultFiles.Where(f => f.Contains("PlayMode") || !f.Contains("EditMode")).ToList();

            if (resultFiles.Count == 0)
            {
                // Try to get results via reflection from TestRunnerApi
                var apiResults = TryGetResultsViaApi(mode);
                if (apiResults != null)
                    return apiResults;

                return ToolResponse.OkWithData(new
                {
                    mode,
                    resultFiles = 0,
                    message = "No test result files found. Run tests first using run_tests action.",
                    searchedPaths = possiblePaths
                }, "No test results found. Run tests first.");
            }

            // Parse the most recent result file
            var latestFile = resultFiles.OrderByDescending(f => File.GetLastWriteTime(f)).First();
            var fileInfo = new FileInfo(latestFile);

            try
            {
                var content = File.ReadAllText(latestFile);
                // Extract basic stats from NUnit XML format
                var stats = ParseTestResultXml(content);

                return ToolResponse.OkWithData(new
                {
                    mode,
                    resultFile = latestFile,
                    lastModified = fileInfo.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    stats,
                    rawContentLength = content.Length
                }, $"Test results from {fileInfo.LastWriteTime:yyyy-MM-dd HH:mm:ss}: {stats}");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Failed to parse test results from '{latestFile}': {ex.Message}");
            }
        }

        /// <summary>
        /// Create a test script template file.
        /// </summary>
        private ToolResponse HandleCreateTest(JObject parameters)
        {
            string name = ToolHelpers.GetRequiredString(parameters, "name");
            string mode = ToolHelpers.GetOptionalString(parameters, "mode", "edit").ToLowerInvariant();
            string path = ToolHelpers.GetOptionalString(parameters, "path", "Assets/Tests");

            if (mode != "edit" && mode != "play")
                return ToolResponse.Fail($"Invalid mode: {mode}. Must be edit or play.");

            // Sanitize class name
            string className = SanitizeClassName(name);
            if (string.IsNullOrEmpty(className))
                return ToolResponse.Fail($"Invalid test name: {name}. Must be a valid C# identifier.");

            // Determine subfolder
            string subFolder = mode == "edit" ? "EditMode" : "PlayMode";
            string fullDir = Path.Combine(path, subFolder);

            // Ensure directory exists
            if (!Directory.Exists(fullDir))
                Directory.CreateDirectory(fullDir);

            // Generate test content
            string content = mode == "edit"
                ? GenerateEditModeTestTemplate(className)
                : GeneratePlayModeTestTemplate(className);

            string filePath = Path.Combine(fullDir, $"{className}.cs");

            // Check if file already exists
            if (File.Exists(filePath))
                return ToolResponse.Fail($"Test file already exists: {filePath}");

            File.WriteAllText(filePath, content);
            AssetDatabase.Refresh();

            // Also ensure asmdef exists for the test folder
            string asmdefPath = Path.Combine(fullDir, $"Tests.{subFolder}.asmdef");
            if (!File.Exists(asmdefPath))
            {
                string asmdefContent = GenerateTestAsmdef(subFolder, mode);
                File.WriteAllText(asmdefPath, asmdefContent);
            }

            AssetDatabase.Refresh();

            return ToolResponse.OkWithData(new
            {
                filePath,
                className,
                mode,
                asmdefPath,
                asmdefCreated = !File.Exists(asmdefPath)
            }, $"Created {mode} mode test '{className}' at {filePath}");
        }

        /// <summary>
        /// Cancel currently running tests via TestRunnerApi reflection.
        /// </summary>
        private ToolResponse HandleCancel(JObject parameters)
        {
            if (!EnsureReflectionTypes())
                return ToolResponse.Fail($"Test Framework not available: {_reflectionError}. Please install 'com.unity.test-framework' package via Package Manager.");

            try
            {
                var apiInstance = ScriptableObject.CreateInstance(_testRunnerApiType);
                if (apiInstance == null)
                    return ToolResponse.Fail("Failed to create TestRunnerApi instance.");

                try
                {
                    // Try multiple method names for cancellation across Unity versions
                    var cancelMethod = _testRunnerApiType.GetMethod("CancelTestRun")
                        ?? _testRunnerApiType.GetMethod("Cancel")
                        ?? _testRunnerApiType.GetMethod("StopTestRun");

                    if (cancelMethod == null)
                    {
                        // Try to find any method containing "cancel" or "stop" (case-insensitive)
                        cancelMethod = _testRunnerApiType.GetMethods()
                            .FirstOrDefault(m => m.Name.IndexOf("cancel", StringComparison.OrdinalIgnoreCase) >= 0
                                || m.Name.IndexOf("stop", StringComparison.OrdinalIgnoreCase) >= 0);
                    }

                    if (cancelMethod == null)
                        return ToolResponse.Fail("Could not find a cancel/stop method on TestRunnerApi. Your Unity Test Framework version may not support programmatic cancellation.");

                    // Invoke with no parameters or with default parameters
                    var methodParams = cancelMethod.GetParameters();
                    if (methodParams.Length == 0)
                    {
                        cancelMethod.Invoke(apiInstance, null);
                    }
                    else
                    {
                        // Pass default values for all parameters
                        var args = new object[methodParams.Length];
                        for (int i = 0; i < methodParams.Length; i++)
                        {
                            args[i] = methodParams[i].HasDefaultValue
                                ? methodParams[i].DefaultValue
                                : (methodParams[i].ParameterType.IsValueType
                                    ? Activator.CreateInstance(methodParams[i].ParameterType)
                                    : null);
                        }
                        cancelMethod.Invoke(apiInstance, args);
                    }

                    return ToolResponse.OkWithData(new
                    {
                        status = "cancelled",
                        method = cancelMethod.Name,
                        message = "Test run cancellation requested successfully."
                    }, "Test run cancellation requested.");
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(apiInstance);
                }
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Failed to cancel tests: {ex.Message}");
            }
        }

        /// <summary>
        /// Create a comprehensive test fixture template with OneTimeSetUp, OneTimeTearDown,
        /// SetUp, TearDown, and sample test methods organized in a proper fixture structure.
        /// </summary>
        private ToolResponse HandleCreateTestFixture(JObject parameters)
        {
            string name = ToolHelpers.GetRequiredString(parameters, "name");
            string mode = ToolHelpers.GetOptionalString(parameters, "mode", "edit").ToLowerInvariant();
            string path = ToolHelpers.GetOptionalString(parameters, "path", "Assets/Tests");
            string namespaceName = ToolHelpers.GetOptionalString(parameters, "namespace");
            string description = ToolHelpers.GetOptionalString(parameters, "description");

            if (mode != "edit" && mode != "play")
                return ToolResponse.Fail($"Invalid mode: {mode}. Must be edit or play.");

            // Sanitize class name
            string className = SanitizeFixtureName(name);
            if (string.IsNullOrEmpty(className))
                return ToolResponse.Fail($"Invalid fixture name: {name}. Must be a valid C# identifier.");

            // Determine subfolder
            string subFolder = mode == "edit" ? "EditMode" : "PlayMode";
            string fullDir = Path.Combine(path, subFolder);

            // Ensure directory exists
            if (!Directory.Exists(fullDir))
                Directory.CreateDirectory(fullDir);

            // Generate fixture content
            string content = mode == "edit"
                ? GenerateEditModeFixtureTemplate(className, namespaceName, description)
                : GeneratePlayModeFixtureTemplate(className, namespaceName, description);

            string filePath = Path.Combine(fullDir, $"{className}.cs");

            // Check if file already exists
            if (File.Exists(filePath))
                return ToolResponse.Fail($"Test fixture file already exists: {filePath}");

            File.WriteAllText(filePath, content);

            // Also ensure asmdef exists for the test folder
            string asmdefPath = Path.Combine(fullDir, $"Tests.{subFolder}.asmdef");
            bool asmdefCreated = false;
            if (!File.Exists(asmdefPath))
            {
                string asmdefContent = GenerateTestAsmdef(subFolder, mode);
                File.WriteAllText(asmdefPath, asmdefContent);
                asmdefCreated = true;
            }

            AssetDatabase.Refresh();

            return ToolResponse.OkWithData(new
            {
                filePath,
                className,
                mode,
                namespaceName = namespaceName ?? "(none)",
                asmdefPath,
                asmdefCreated,
                features = new[] { "OneTimeSetUp", "OneTimeTearDown", "SetUp", "TearDown", "Category", "TestFixture", "Sample Tests" }
            }, $"Created {mode} mode test fixture '{className}' at {filePath}");
        }

        #endregion

        #region Reflection Helpers

        /// <summary>
        /// Initialize reflection types for Test Framework API.
        /// </summary>
        private static bool EnsureReflectionTypes()
        {
            if (_reflectionInitialized)
                return _testRunnerApiType != null;

            _reflectionInitialized = true;

            try
            {
                _testRunnerApiType = FindType("UnityEditor.TestTools.TestRunner.Api.TestRunnerApi");
                _filterType = FindType("UnityEditor.TestTools.TestRunner.Api.Filter");
                _testModeType = FindType("UnityEditor.TestTools.TestRunner.Api.TestMode");

                if (_testRunnerApiType == null)
                {
                    _reflectionError = "TestRunnerApi type not found. com.unity.test-framework package may not be installed.";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                _reflectionError = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Find a type by full name across all loaded assemblies.
        /// </summary>
        private static Type FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(fullName);
                if (type != null) return type;
            }
            return null;
        }

        /// <summary>
        /// Resolve the TestMode enum value from a string mode.
        /// </summary>
        private static object ResolveTestMode(string mode)
        {
            if (_testModeType == null) return null;

            try
            {
                switch (mode)
                {
                    case "edit":
                        return Enum.Parse(_testModeType, "EditMode");
                    case "play":
                        return Enum.Parse(_testModeType, "PlayMode");
                    case "all":
                        // Try to combine both flags
                        var editVal = Enum.Parse(_testModeType, "EditMode");
                        var playVal = Enum.Parse(_testModeType, "PlayMode");
                        int combined = (int)editVal | (int)playVal;
                        return Enum.ToObject(_testModeType, combined);
                    default:
                        return null;
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Try to list tests via the TestRunnerApi reflection.
        /// </summary>
        private ToolResponse TryListTestsViaApi(string mode, string filter)
        {
            if (!EnsureReflectionTypes())
                return null; // Fall back to assembly scanning

            try
            {
                var apiInstance = ScriptableObject.CreateInstance(_testRunnerApiType);
                if (apiInstance == null) return null;

                try
                {
                    // Try to call RetrieveTestList
                    var retrieveMethod = _testRunnerApiType.GetMethod("RetrieveTestList");
                    if (retrieveMethod == null) return null;

                    // This is async callback-based, so we can't easily use it synchronously
                    // Fall back to assembly scanning
                    return null;
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(apiInstance);
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Try to get test results via the TestRunnerApi.
        /// </summary>
        private ToolResponse TryGetResultsViaApi(string mode)
        {
            // TestRunnerApi doesn't provide a direct way to get past results synchronously
            // Return null to indicate fallback
            return null;
        }

        #endregion

        #region Assembly Scanning

        /// <summary>
        /// List tests by scanning loaded assemblies for [Test] and [UnityTest] attributes.
        /// </summary>
        private ToolResponse ListTestsByReflection(string mode, string filter)
        {
            var testMethods = new List<object>();
            var testAttributeType = FindType("NUnit.Framework.TestAttribute");
            var unityTestAttributeType = FindType("UnityEngine.TestTools.UnityTestAttribute");

            if (testAttributeType == null && unityTestAttributeType == null)
            {
                return ToolResponse.Fail("NUnit.Framework not found. Please install 'com.unity.test-framework' package via Package Manager.");
            }

            var assemblies = AppDomain.CurrentDomain.GetAssemblies();

            foreach (var assembly in assemblies)
            {
                // Skip non-test assemblies for performance
                var asmName = assembly.GetName().Name;
                if (!asmName.Contains("Test", StringComparison.OrdinalIgnoreCase) &&
                    !asmName.Contains("test", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        // Check if this is an EditMode or PlayMode test class
                        bool isPlayMode = IsPlayModeTestClass(type);
                        bool isEditMode = !isPlayMode;

                        if (mode == "edit" && isPlayMode) continue;
                        if (mode == "play" && isEditMode) continue;

                        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                        {
                            bool isTest = false;
                            string testType = "Test";

                            if (testAttributeType != null && method.GetCustomAttributes(testAttributeType, true).Length > 0)
                            {
                                isTest = true;
                                testType = "Test";
                            }
                            else if (unityTestAttributeType != null && method.GetCustomAttributes(unityTestAttributeType, true).Length > 0)
                            {
                                isTest = true;
                                testType = "UnityTest";
                            }

                            if (!isTest) continue;

                            string fullName = $"{type.FullName}.{method.Name}";

                            // Apply filter
                            if (!string.IsNullOrEmpty(filter) &&
                                !fullName.Contains(filter, StringComparison.OrdinalIgnoreCase))
                                continue;

                            testMethods.Add(new
                            {
                                name = method.Name,
                                className = type.Name,
                                fullName,
                                assembly = asmName,
                                testType,
                                mode = isPlayMode ? "play" : "edit"
                            });
                        }
                    }
                }
                catch
                {
                    // Skip assemblies that fail to enumerate types
                }
            }

            return ToolResponse.OkWithData(new
            {
                totalTests = testMethods.Count,
                filterMode = mode,
                filter = filter ?? "(none)",
                tests = testMethods.Take(200).ToArray(),
                truncated = testMethods.Count > 200,
                source = "assembly_scan"
            }, $"Found {testMethods.Count} tests" + (filter != null ? $" matching '{filter}'" : ""));
        }

        /// <summary>
        /// Determine if a test class is a PlayMode test by checking for UnityTest attribute
        /// or if it's in a PlayMode assembly.
        /// </summary>
        private bool IsPlayModeTestClass(Type type)
        {
            var asmName = type.Assembly.GetName().Name;
            if (asmName.Contains("PlayMode", StringComparison.OrdinalIgnoreCase))
                return true;

            // Check if any method has [UnityTest] attribute (commonly used in PlayMode)
            var unityTestType = FindType("UnityEngine.TestTools.UnityTestAttribute");
            if (unityTestType != null)
            {
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (method.GetCustomAttributes(unityTestType, true).Length > 0)
                        return true;
                }
            }

            return false;
        }

        #endregion

        #region Template Generation

        /// <summary>
        /// Generate an EditMode test script template.
        /// </summary>
        private string GenerateEditModeTestTemplate(string className)
        {
            return $@"using NUnit.Framework;
using UnityEngine;
using UnityEditor;

/// <summary>
/// EditMode tests for {className}.
/// </summary>
public class {className}
{{
    [SetUp]
    public void SetUp()
    {{
        // Initialize test fixtures here
    }}

    [TearDown]
    public void TearDown()
    {{
        // Clean up after each test
    }}

    [Test]
    public void SampleTest_WhenCondition_ExpectedResult()
    {{
        // Arrange
        var expected = true;

        // Act
        var actual = true;

        // Assert
        Assert.AreEqual(expected, actual);
    }}

    [Test]
    public void SampleTest_GameObjectCreation()
    {{
        // Arrange & Act
        var go = new GameObject(""TestObject"");

        // Assert
        Assert.IsNotNull(go);
        Assert.AreEqual(""TestObject"", go.name);

        // Cleanup
        Object.DestroyImmediate(go);
    }}
}}
";
        }

        /// <summary>
        /// Generate a PlayMode test script template.
        /// </summary>
        private string GeneratePlayModeTestTemplate(string className)
        {
            return $@"using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

/// <summary>
/// PlayMode tests for {className}.
/// </summary>
public class {className}
{{
    [SetUp]
    public void SetUp()
    {{
        // Initialize test fixtures here
    }}

    [TearDown]
    public void TearDown()
    {{
        // Clean up after each test
    }}

    [Test]
    public void SampleTest_WhenCondition_ExpectedResult()
    {{
        // Arrange
        var expected = true;

        // Act
        var actual = true;

        // Assert
        Assert.AreEqual(expected, actual);
    }}

    [UnityTest]
    public IEnumerator SampleUnityTest_WaitsOneFrame()
    {{
        // Arrange
        var go = new GameObject(""TestObject"");

        // Act
        yield return null; // Wait one frame

        // Assert
        Assert.IsNotNull(go);

        // Cleanup
        Object.Destroy(go);
    }}
}}
";
        }

        /// <summary>
        /// Generate an EditMode test fixture template with full lifecycle methods.
        /// </summary>
        private string GenerateEditModeFixtureTemplate(string className, string namespaceName, string description)
        {
            string desc = description ?? $"EditMode test fixture for {className}";
            string indent = string.IsNullOrEmpty(namespaceName) ? "" : "    ";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("using NUnit.Framework;");
            sb.AppendLine("using UnityEngine;");
            sb.AppendLine("using UnityEditor;");
            sb.AppendLine();

            if (!string.IsNullOrEmpty(namespaceName))
            {
                sb.AppendLine($"namespace {namespaceName}");
                sb.AppendLine("{");
            }

            sb.AppendLine($"{indent}/// <summary>");
            sb.AppendLine($"{indent}/// {desc}");
            sb.AppendLine($"{indent}/// </summary>");
            sb.AppendLine($"{indent}[TestFixture]");
            sb.AppendLine($"{indent}[Category(\"EditMode\")]");
            sb.AppendLine($"{indent}public class {className}");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    #region Lifecycle");
            sb.AppendLine();
            sb.AppendLine($"{indent}    /// <summary>");
            sb.AppendLine($"{indent}    /// Called once before all tests in this fixture. Use for expensive setup.");
            sb.AppendLine($"{indent}    /// </summary>");
            sb.AppendLine($"{indent}    [OneTimeSetUp]");
            sb.AppendLine($"{indent}    public void OneTimeSetUp()");
            sb.AppendLine($"{indent}    {{");
            sb.AppendLine($"{indent}        // One-time initialization (e.g., load shared resources)");
            sb.AppendLine($"{indent}    }}");
            sb.AppendLine();
            sb.AppendLine($"{indent}    /// <summary>");
            sb.AppendLine($"{indent}    /// Called once after all tests in this fixture. Use for expensive cleanup.");
            sb.AppendLine($"{indent}    /// </summary>");
            sb.AppendLine($"{indent}    [OneTimeTearDown]");
            sb.AppendLine($"{indent}    public void OneTimeTearDown()");
            sb.AppendLine($"{indent}    {{");
            sb.AppendLine($"{indent}        // One-time cleanup (e.g., unload shared resources)");
            sb.AppendLine($"{indent}    }}");
            sb.AppendLine();
            sb.AppendLine($"{indent}    /// <summary>");
            sb.AppendLine($"{indent}    /// Called before each test method. Use for per-test setup.");
            sb.AppendLine($"{indent}    /// </summary>");
            sb.AppendLine($"{indent}    [SetUp]");
            sb.AppendLine($"{indent}    public void SetUp()");
            sb.AppendLine($"{indent}    {{");
            sb.AppendLine($"{indent}        // Per-test initialization");
            sb.AppendLine($"{indent}    }}");
            sb.AppendLine();
            sb.AppendLine($"{indent}    /// <summary>");
            sb.AppendLine($"{indent}    /// Called after each test method. Use for per-test cleanup.");
            sb.AppendLine($"{indent}    /// </summary>");
            sb.AppendLine($"{indent}    [TearDown]");
            sb.AppendLine($"{indent}    public void TearDown()");
            sb.AppendLine($"{indent}    {{");
            sb.AppendLine($"{indent}        // Per-test cleanup");
            sb.AppendLine($"{indent}    }}");
            sb.AppendLine();
            sb.AppendLine($"{indent}    #endregion");
            sb.AppendLine();
            sb.AppendLine($"{indent}    #region Tests");
            sb.AppendLine();
            sb.AppendLine($"{indent}    [Test]");
            sb.AppendLine($"{indent}    public void SampleTest_WhenCondition_ShouldExpectedBehavior()");
            sb.AppendLine($"{indent}    {{");
            sb.AppendLine($"{indent}        // Arrange");
            sb.AppendLine($"{indent}        var expected = true;");
            sb.AppendLine();
            sb.AppendLine($"{indent}        // Act");
            sb.AppendLine($"{indent}        var actual = true;");
            sb.AppendLine();
            sb.AppendLine($"{indent}        // Assert");
            sb.AppendLine($"{indent}        Assert.AreEqual(expected, actual);");
            sb.AppendLine($"{indent}    }}");
            sb.AppendLine();
            sb.AppendLine($"{indent}    [Test]");
            sb.AppendLine($"{indent}    [Category(\"Integration\")]");
            sb.AppendLine($"{indent}    public void SampleIntegrationTest_GameObjectCreation()");
            sb.AppendLine($"{indent}    {{");
            sb.AppendLine($"{indent}        // Arrange & Act");
            sb.AppendLine($"{indent}        var go = new GameObject(\"TestObject\");");
            sb.AppendLine();
            sb.AppendLine($"{indent}        // Assert");
            sb.AppendLine($"{indent}        Assert.IsNotNull(go);");
            sb.AppendLine($"{indent}        Assert.AreEqual(\"TestObject\", go.name);");
            sb.AppendLine();
            sb.AppendLine($"{indent}        // Cleanup");
            sb.AppendLine($"{indent}        Object.DestroyImmediate(go);");
            sb.AppendLine($"{indent}    }}");
            sb.AppendLine();
            sb.AppendLine($"{indent}    #endregion");
            sb.AppendLine($"{indent}}}");

            if (!string.IsNullOrEmpty(namespaceName))
            {
                sb.AppendLine("}");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Generate a PlayMode test fixture template with full lifecycle methods and coroutine support.
        /// </summary>
        private string GeneratePlayModeFixtureTemplate(string className, string namespaceName, string description)
        {
            string desc = description ?? $"PlayMode test fixture for {className}";
            string indent = string.IsNullOrEmpty(namespaceName) ? "" : "    ";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("using System.Collections;");
            sb.AppendLine("using NUnit.Framework;");
            sb.AppendLine("using UnityEngine;");
            sb.AppendLine("using UnityEngine.TestTools;");
            sb.AppendLine();

            if (!string.IsNullOrEmpty(namespaceName))
            {
                sb.AppendLine($"namespace {namespaceName}");
                sb.AppendLine("{");
            }

            sb.AppendLine($"{indent}/// <summary>");
            sb.AppendLine($"{indent}/// {desc}");
            sb.AppendLine($"{indent}/// </summary>");
            sb.AppendLine($"{indent}[TestFixture]");
            sb.AppendLine($"{indent}[Category(\"PlayMode\")]");
            sb.AppendLine($"{indent}public class {className}");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    #region Lifecycle");
            sb.AppendLine();
            sb.AppendLine($"{indent}    /// <summary>");
            sb.AppendLine($"{indent}    /// Called once before all tests in this fixture.");
            sb.AppendLine($"{indent}    /// </summary>");
            sb.AppendLine($"{indent}    [OneTimeSetUp]");
            sb.AppendLine($"{indent}    public void OneTimeSetUp()");
            sb.AppendLine($"{indent}    {{");
            sb.AppendLine($"{indent}        // One-time initialization (e.g., load scene, create persistent objects)");
            sb.AppendLine($"{indent}    }}");
            sb.AppendLine();
            sb.AppendLine($"{indent}    /// <summary>");
            sb.AppendLine($"{indent}    /// Called once after all tests in this fixture.");
            sb.AppendLine($"{indent}    /// </summary>");
            sb.AppendLine($"{indent}    [OneTimeTearDown]");
            sb.AppendLine($"{indent}    public void OneTimeTearDown()");
            sb.AppendLine($"{indent}    {{");
            sb.AppendLine($"{indent}        // One-time cleanup");
            sb.AppendLine($"{indent}    }}");
            sb.AppendLine();
            sb.AppendLine($"{indent}    /// <summary>");
            sb.AppendLine($"{indent}    /// Called before each test method.");
            sb.AppendLine($"{indent}    /// </summary>");
            sb.AppendLine($"{indent}    [SetUp]");
            sb.AppendLine($"{indent}    public void SetUp()");
            sb.AppendLine($"{indent}    {{");
            sb.AppendLine($"{indent}        // Per-test initialization");
            sb.AppendLine($"{indent}    }}");
            sb.AppendLine();
            sb.AppendLine($"{indent}    /// <summary>");
            sb.AppendLine($"{indent}    /// Called after each test method.");
            sb.AppendLine($"{indent}    /// </summary>");
            sb.AppendLine($"{indent}    [TearDown]");
            sb.AppendLine($"{indent}    public void TearDown()");
            sb.AppendLine($"{indent}    {{");
            sb.AppendLine($"{indent}        // Per-test cleanup");
            sb.AppendLine($"{indent}    }}");
            sb.AppendLine();
            sb.AppendLine($"{indent}    #endregion");
            sb.AppendLine();
            sb.AppendLine($"{indent}    #region Tests");
            sb.AppendLine();
            sb.AppendLine($"{indent}    [Test]");
            sb.AppendLine($"{indent}    public void SampleTest_WhenCondition_ShouldExpectedBehavior()");
            sb.AppendLine($"{indent}    {{");
            sb.AppendLine($"{indent}        // Arrange");
            sb.AppendLine($"{indent}        var expected = true;");
            sb.AppendLine();
            sb.AppendLine($"{indent}        // Act");
            sb.AppendLine($"{indent}        var actual = true;");
            sb.AppendLine();
            sb.AppendLine($"{indent}        // Assert");
            sb.AppendLine($"{indent}        Assert.AreEqual(expected, actual);");
            sb.AppendLine($"{indent}    }}");
            sb.AppendLine();
            sb.AppendLine($"{indent}    [UnityTest]");
            sb.AppendLine($"{indent}    public IEnumerator SampleUnityTest_WaitsForFrames()");
            sb.AppendLine($"{indent}    {{");
            sb.AppendLine($"{indent}        // Arrange");
            sb.AppendLine($"{indent}        var go = new GameObject(\"TestObject\");");
            sb.AppendLine($"{indent}        var rb = go.AddComponent<Rigidbody>();");
            sb.AppendLine();
            sb.AppendLine($"{indent}        // Act — wait for physics to process");
            sb.AppendLine($"{indent}        yield return new WaitForFixedUpdate();");
            sb.AppendLine($"{indent}        yield return new WaitForFixedUpdate();");
            sb.AppendLine();
            sb.AppendLine($"{indent}        // Assert");
            sb.AppendLine($"{indent}        Assert.IsNotNull(rb);");
            sb.AppendLine($"{indent}        Assert.IsTrue(go.activeInHierarchy);");
            sb.AppendLine();
            sb.AppendLine($"{indent}        // Cleanup");
            sb.AppendLine($"{indent}        Object.Destroy(go);");
            sb.AppendLine($"{indent}    }}");
            sb.AppendLine();
            sb.AppendLine($"{indent}    [UnityTest]");
            sb.AppendLine($"{indent}    public IEnumerator SampleUnityTest_WithTimeout()");
            sb.AppendLine($"{indent}    {{");
            sb.AppendLine($"{indent}        // Arrange");
            sb.AppendLine($"{indent}        float startTime = Time.time;");
            sb.AppendLine($"{indent}        float timeout = 2f;");
            sb.AppendLine();
            sb.AppendLine($"{indent}        // Act — wait until condition or timeout");
            sb.AppendLine($"{indent}        while (Time.time - startTime < timeout)");
            sb.AppendLine($"{indent}        {{");
            sb.AppendLine($"{indent}            // Check condition here");
            sb.AppendLine($"{indent}            if (true) break; // Replace with actual condition");
            sb.AppendLine($"{indent}            yield return null;");
            sb.AppendLine($"{indent}        }}");
            sb.AppendLine();
            sb.AppendLine($"{indent}        // Assert");
            sb.AppendLine($"{indent}        Assert.IsTrue(Time.time - startTime < timeout, \"Test timed out\");");
            sb.AppendLine($"{indent}    }}");
            sb.AppendLine();
            sb.AppendLine($"{indent}    #endregion");
            sb.AppendLine($"{indent}}}");

            if (!string.IsNullOrEmpty(namespaceName))
            {
                sb.AppendLine("}");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Generate a test assembly definition file.
        /// </summary>
        private string GenerateTestAsmdef(string subFolder, string mode)
        {
            var optionalUnityRefs = mode == "play"
                ? @"""UnityEngine.TestRunner"", ""UnityEditor.TestRunner"""
                : @"""UnityEditor.TestRunner""";

            var testMode = mode == "play" ? "playmode" : "editmode";

            return $@"{{
    ""name"": ""Tests.{subFolder}"",
    ""rootNamespace"": """",
    ""references"": [
        {optionalUnityRefs}
    ],
    ""includePlatforms"": [
        ""Editor""
    ],
    ""excludePlatforms"": [],
    ""allowUnsafeCode"": false,
    ""overrideReferences"": true,
    ""precompiledReferences"": [
        ""nunit.framework.dll""
    ],
    ""autoReferenced"": false,
    ""defineConstraints"": [
        ""UNITY_INCLUDE_TESTS""
    ],
    ""versionDefines"": [],
    ""noEngineReferences"": false
}}";
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Sanitize a string to be a valid C# class name.
        /// </summary>
        private string SanitizeClassName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            // Remove invalid characters
            var sanitized = new string(name.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());

            // Ensure it starts with a letter or underscore
            if (sanitized.Length > 0 && char.IsDigit(sanitized[0]))
                sanitized = "_" + sanitized;

            // Ensure it ends with "Tests" if not already
            if (!sanitized.EndsWith("Tests", StringComparison.OrdinalIgnoreCase) &&
                !sanitized.EndsWith("Test", StringComparison.OrdinalIgnoreCase))
                sanitized += "Tests";

            return string.IsNullOrEmpty(sanitized) ? null : sanitized;
        }

        /// <summary>
        /// Sanitize a string to be a valid C# fixture class name.
        /// Appends "Fixture" suffix if not already present.
        /// </summary>
        private string SanitizeFixtureName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            // Remove invalid characters
            var sanitized = new string(name.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());

            // Ensure it starts with a letter or underscore
            if (sanitized.Length > 0 && char.IsDigit(sanitized[0]))
                sanitized = "_" + sanitized;

            // Ensure it ends with "Fixture" or "Tests" if not already
            if (!sanitized.EndsWith("Fixture", StringComparison.OrdinalIgnoreCase) &&
                !sanitized.EndsWith("Tests", StringComparison.OrdinalIgnoreCase) &&
                !sanitized.EndsWith("Test", StringComparison.OrdinalIgnoreCase))
                sanitized += "Fixture";

            return string.IsNullOrEmpty(sanitized) ? null : sanitized;
        }

        /// <summary>
        /// Parse basic statistics from NUnit XML test result format.
        /// </summary>
        private object ParseTestResultXml(string xml)
        {
            // Simple regex-free parsing for key attributes
            int total = ExtractIntAttribute(xml, "total");
            int passed = ExtractIntAttribute(xml, "passed");
            int failed = ExtractIntAttribute(xml, "failed");
            int skipped = ExtractIntAttribute(xml, "skipped");
            int inconclusive = ExtractIntAttribute(xml, "inconclusive");
            string result = ExtractStringAttribute(xml, "result");
            string duration = ExtractStringAttribute(xml, "duration");

            return new
            {
                total,
                passed,
                failed,
                skipped,
                inconclusive,
                result = result ?? "Unknown",
                duration = duration ?? "N/A"
            };
        }

        /// <summary>
        /// Extract an integer attribute value from XML content.
        /// </summary>
        private int ExtractIntAttribute(string xml, string attrName)
        {
            string pattern = attrName + "=\"";
            int idx = xml.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return 0;

            idx += pattern.Length;
            int endIdx = xml.IndexOf('"', idx);
            if (endIdx < 0) return 0;

            string val = xml.Substring(idx, endIdx - idx);
            return int.TryParse(val, out int result) ? result : 0;
        }

        /// <summary>
        /// Extract a string attribute value from XML content.
        /// </summary>
        private string ExtractStringAttribute(string xml, string attrName)
        {
            string pattern = attrName + "=\"";
            int idx = xml.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;

            idx += pattern.Length;
            int endIdx = xml.IndexOf('"', idx);
            if (endIdx < 0) return null;

            return xml.Substring(idx, endIdx - idx);
        }

        #endregion
    }
}
