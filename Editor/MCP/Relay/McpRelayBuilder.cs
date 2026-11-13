using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using AgentCore.Editor.Components.MCP.Bridge;
using AgentCore.Editor.Utils;
using UnityEditor;

namespace AgentCore.Editor.Components.MCP.Relay
{
    /// <summary>
    /// 用 Unity 自带的 Roslyn（NetCoreRuntime/dotnet + DotNetSdkRoslyn/csc.dll）把 <c>Editor/MCP/Relay~/</c> 的源码
    /// 编译成 <c>Library/AgentCore/Mcp/relay/&lt;源码哈希&gt;/AgentCoreMcpRelay.exe</c>，由 Unity 自带 Mono 运行。
    /// 包里不放二进制；源码不变时复用已有产物。
    /// </summary>
    public static class McpRelayBuilder
    {
        public const string RelayExeName = "AgentCoreMcpRelay.exe";
        private const string NewtonsoftDllName = "Newtonsoft.Json.dll";
        private static readonly TimeSpan CompileTimeout = TimeSpan.FromSeconds(120);

        /// <summary>编辑器自带工具链的路径（主线程上解析一次）。</summary>
        public sealed class Toolchain
        {
            public string ContentsPath;
            public string Dotnet;
            public string Csc;
            public string Mono;
            public string ReferenceDir;
            public string NewtonsoftDll;
            public string SourceDir;
            public string OutputRoot;

            public IEnumerable<string> References => new[]
            {
                Path.Combine(ReferenceDir, "mscorlib.dll"),
                Path.Combine(ReferenceDir, "System.dll"),
                Path.Combine(ReferenceDir, "System.Core.dll"),
                Path.Combine(ReferenceDir, "Facades", "netstandard.dll"),
                Path.Combine(ReferenceDir, "Facades", "System.Runtime.dll"),
                Path.Combine(ReferenceDir, "Facades", "System.Threading.Tasks.dll"),
            };
        }

        public sealed class BuildResult
        {
            public bool Success;
            public string RelayPath;
            public string MonoPath;
            public string Error;
        }

        private static Task<BuildResult> _current;

        /// <summary>最近一次成功构建的结果（设置卡片与配置生成使用）。</summary>
        public static BuildResult Last { get; private set; }

        /// <summary>解析工具链（主线程）。</summary>
        public static Toolchain ResolveToolchain()
        {
            var contents = EditorApplication.applicationContentsPath;
            var exe = ProcessArgs.IsWindows ? ".exe" : string.Empty;
            return new Toolchain
            {
                ContentsPath = contents,
                Dotnet = Path.Combine(contents, "NetCoreRuntime", "dotnet" + exe),
                Csc = Path.Combine(contents, "DotNetSdkRoslyn", "csc.dll"),
                Mono = Path.Combine(contents, "MonoBleedingEdge", "bin", "mono" + exe),
                ReferenceDir = Path.Combine(contents, "MonoBleedingEdge", "lib", "mono", "4.7.1-api"),
                NewtonsoftDll = typeof(Newtonsoft.Json.Linq.JObject).Assembly.Location,
                SourceDir = FindSourceDir(),
                OutputRoot = Path.Combine(McpPaths.McpDirectory, "relay"),
            };
        }

        /// <summary>relay 源码目录：本程序集所在包的 Editor/MCP/Relay~。</summary>
        private static string FindSourceDir()
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(McpRelayBuilder).Assembly);
            if (package != null && !string.IsNullOrEmpty(package.resolvedPath))
                return Path.Combine(package.resolvedPath, "Editor", "MCP", "Relay~");
            return null;
        }

        /// <summary>确保 relay 已构建（主线程调用；编译在后台进行）。并发调用共享同一次构建。</summary>
        public static Task<BuildResult> EnsureBuiltAsync()
        {
            if (_current != null && !_current.IsCompleted)
                return _current;
            var toolchain = ResolveToolchain();
            _current = Task.Run(() => Build(toolchain));
            _current.ContinueWith(t =>
            {
                if (t.Status == TaskStatus.RanToCompletion)
                {
                    Last = t.Result;
                    if (!t.Result.Success)
                        AgentCoreLog.Warning("[AgentCore][MCP] Relay build failed: " + t.Result.Error);
                }
            });
            return _current;
        }

        /// <summary>构建（可在后台线程调用）。</summary>
        public static BuildResult Build(Toolchain toolchain)
        {
            try
            {
                var missing = new[] { toolchain.Dotnet, toolchain.Csc, toolchain.Mono, toolchain.NewtonsoftDll }
                    .Concat(toolchain.References)
                    .Where(p => string.IsNullOrEmpty(p) || !File.Exists(p))
                    .ToList();
                if (string.IsNullOrEmpty(toolchain.SourceDir) || !Directory.Exists(toolchain.SourceDir))
                    missing.Add(toolchain.SourceDir ?? "Editor/MCP/Relay~");
                if (missing.Count > 0)
                    return Fail("Unity's bundled toolchain or the relay source is missing: " + string.Join(", ", missing));

                var sources = Directory.GetFiles(toolchain.SourceDir, "*.cs", SearchOption.AllDirectories)
                    .OrderBy(p => p, StringComparer.Ordinal)
                    .ToList();
                if (sources.Count == 0)
                    return Fail("No relay source files in " + toolchain.SourceDir);

                var hash = ComputeHash(sources, toolchain.NewtonsoftDll);
                var outputDir = Path.Combine(toolchain.OutputRoot, hash);
                var relayPath = Path.Combine(outputDir, RelayExeName);
                if (File.Exists(relayPath) && File.Exists(Path.Combine(outputDir, NewtonsoftDllName)))
                    return new BuildResult { Success = true, RelayPath = relayPath, MonoPath = toolchain.Mono };

                var staging = outputDir + ".building-" + Guid.NewGuid().ToString("N").Substring(0, 6);
                Directory.CreateDirectory(staging);
                File.Copy(toolchain.NewtonsoftDll, Path.Combine(staging, NewtonsoftDllName), true);

                var args = new List<string>
                {
                    toolchain.Csc, "-nologo", "-nostdlib", "-noconfig", "-optimize", "-target:exe",
                    "-out:" + Path.Combine(staging, RelayExeName),
                };
                args.AddRange(toolchain.References.Select(r => "-r:" + r));
                args.Add("-r:" + Path.Combine(staging, NewtonsoftDllName));
                args.AddRange(sources);

                var (exitCode, output) = Run(toolchain.Dotnet, args);
                if (exitCode != 0)
                {
                    TryDeleteDirectory(staging);
                    return Fail("Relay compilation failed:\n" + output.Trim());
                }

                if (Directory.Exists(outputDir))
                    TryDeleteDirectory(outputDir);
                Directory.Move(staging, outputDir);
                CleanupOldBuilds(toolchain.OutputRoot, outputDir);
                return new BuildResult { Success = true, RelayPath = relayPath, MonoPath = toolchain.Mono };
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
        }

        private static BuildResult Fail(string error) => new BuildResult { Success = false, Error = error };

        private static string ComputeHash(IEnumerable<string> sources, string newtonsoft)
        {
            using (var sha = SHA256.Create())
            {
                var sb = new StringBuilder();
                foreach (var source in sources)
                {
                    sb.Append(Path.GetFileName(source)).Append('\n');
                    sb.Append(File.ReadAllText(source)).Append('\n');
                }
                sb.Append(new FileInfo(newtonsoft).Length);
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                var hex = new StringBuilder(16);
                for (var i = 0; i < 8; i++)
                    hex.Append(bytes[i].ToString("x2"));
                return hex.ToString();
            }
        }

        private static (int exitCode, string output) Run(string fileName, IEnumerable<string> args)
        {
            var info = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = ProcessArgs.Join(args),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            // 编译器输出用英文，便于排查与搜索
            info.EnvironmentVariables["DOTNET_CLI_UI_LANGUAGE"] = "en";
            info.EnvironmentVariables["VSLANG"] = "1033";

            using (var process = Process.Start(info))
            {
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit((int)CompileTimeout.TotalMilliseconds))
                {
                    try { process.Kill(); } catch (Exception) { /* ignore */ }
                    return (-1, "Compiler timed out.");
                }
                return (process.ExitCode, stdout.Result + stderr.Result);
            }
        }

        private static void CleanupOldBuilds(string root, string keep)
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(root))
                {
                    if (!string.Equals(Path.GetFullPath(dir), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase))
                        TryDeleteDirectory(dir); // 正在被客户端运行的旧版本会删除失败，留到下次
                }
            }
            catch (Exception)
            {
                // best effort
            }
        }

        private static void TryDeleteDirectory(string dir)
        {
            try { Directory.Delete(dir, true); } catch (Exception) { /* in use */ }
        }
    }
}
