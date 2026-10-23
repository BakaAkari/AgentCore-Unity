using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Utils;
using UnityEditor;

namespace AgentCore.Editor.Tools.Infrastructure
{
    /// <summary>
    /// ripgrep 可用性。
    /// </summary>
    public enum RgAvailability
    {
        /// <summary>尚未探测（或探测进行中）。</summary>
        Unknown,
        Available,
        Unavailable
    }

    /// <summary>
    /// 定位 ripgrep（<c>rg</c>）可执行文件。
    /// <para>
    /// 查找顺序：用户在设置页填写的路径（EditorPrefs，按用户保存，不进 ProjectSettings）→
    /// 包内 <c>Editor/Plugins/rg/&lt;平台&gt;/rg(.exe)</c>（目前不随包分发，留作将来经用户同意后捆绑的位置）→
    /// PATH 中的 <c>rg</c> / <c>rg.exe</c> → 常见安装目录（Unity 从 Finder / Dock 启动时 PATH 往往不含 Homebrew 等目录）。
    /// 每个候选都用 <c>rg --version</c> 在后台线程验证，结果在本次会话内缓存；设置页 "Re-detect" 或修改路径会重新探测。
    /// 本类从不下载、从不安装任何文件；找不到 rg 时各搜索工具回退到进程内 .NET 实现，不报错。
    /// </para>
    /// </summary>
    [InitializeOnLoad]
    public static class RgLocator
    {
        private const string CustomPathPrefKey = "AgentCore.Search.RipgrepPath";

        /// <summary>单个候选 <c>rg --version</c> 的超时。</summary>
        private const int ProbeTimeoutMs = 5000;

        private static readonly object _lock = new object();

        // EditorPrefs / PackageInfo 只能在主线程读取：静态构造（主线程）时缓存，后台线程只读这两个镜像
        private static volatile string _customPath;
        private static volatile string _packageRoot;

        private static RgAvailability _availability = RgAvailability.Unknown;
        private static string _executablePath;
        private static string _version;
        private static string _source;
        private static string _probeError;
        private static Task<bool> _probe;

        static RgLocator()
        {
            try { _customPath = EditorPrefs.GetString(CustomPathPrefKey, string.Empty); }
            catch { _customPath = string.Empty; }

            _packageRoot = ResolvePackageRoot();

            // 启动后在后台预探测一次，首次搜索时通常已有结果
            EditorApplication.delayCall += () => { _ = EnsureProbedAsync(); };
        }

        // ── 状态（线程安全，只读缓存，不触发探测） ──

        /// <summary>当前缓存的可用性。</summary>
        public static RgAvailability Availability
        {
            get { lock (_lock) return _availability; }
        }

        /// <summary>探测到的 rg 路径（不可用时为 null）。</summary>
        public static string ExecutablePath
        {
            get { lock (_lock) return _executablePath; }
        }

        /// <summary>rg 版本号（如 "14.1.0"），不可用时为 null。</summary>
        public static string Version
        {
            get { lock (_lock) return _version; }
        }

        /// <summary>找到 rg 的位置类别：custom / bundled / PATH / common-dir。</summary>
        public static string Source
        {
            get { lock (_lock) return _source; }
        }

        /// <summary>最近一次探测的问题说明（例如自定义路径无效）；完全可用时可能为 null。</summary>
        public static string ProbeError
        {
            get { lock (_lock) return _probeError; }
        }

        /// <summary>
        /// 用户指定的 rg 路径（空 = 自动查找）。setter 必须在主线程调用：写 EditorPrefs 并重新探测。
        /// </summary>
        public static string CustomPath
        {
            get => _customPath ?? string.Empty;
            set
            {
                var normalized = (value ?? string.Empty).Trim().Trim('"');
                if (string.Equals(normalized, _customPath ?? string.Empty, StringComparison.Ordinal))
                    return;
                _customPath = normalized;
                try { EditorPrefs.SetString(CustomPathPrefKey, normalized); }
                catch { /* 非主线程调用时只更新本次会话 */ }
                _ = Refresh();
            }
        }

        // ── 探测 ──

        /// <summary>
        /// 显式重新检测（设置页 "Re-detect"、修改路径后）。清空缓存并在后台探测。
        /// </summary>
        public static Task<bool> Refresh()
        {
            return EnsureProbedAsync(force: true);
        }

        /// <summary>
        /// 确保已探测：已有结果直接返回；否则在后台线程执行一次探测（并发调用共享同一次探测）。
        /// </summary>
        public static Task<bool> EnsureProbedAsync(bool force = false)
        {
            lock (_lock)
            {
                if (!force && _availability != RgAvailability.Unknown)
                    return Task.FromResult(_availability == RgAvailability.Available);

                if (_probe != null && !_probe.IsCompleted)
                    return _probe;

                if (force)
                {
                    _availability = RgAvailability.Unknown;
                    _executablePath = null;
                    _version = null;
                    _source = null;
                    _probeError = null;
                }

                var probe = Task.Run(Probe);
                _probe = probe;
                return probe;
            }
        }

        /// <summary>
        /// 取可用的 rg 路径。缓存未就绪时启动探测；在后台线程最多等待 <paramref name="waitMs"/> 毫秒，
        /// 在主线程不等待（本次直接返回 false，调用方走 .NET 实现）。
        /// </summary>
        public static bool TryGetExecutable(out string path, int waitMs = 3000)
        {
            path = null;
            var probe = EnsureProbedAsync();
            if (!probe.IsCompleted)
            {
                bool onMainThread;
                try { onMainThread = MainThread.IsMainThread; }
                catch { onMainThread = false; }

                if (onMainThread || waitMs <= 0)
                    return false;

                try
                {
                    if (!probe.Wait(waitMs))
                        return false;
                }
                catch
                {
                    return false;
                }
            }

            lock (_lock)
            {
                if (_availability != RgAvailability.Available || string.IsNullOrEmpty(_executablePath))
                    return false;
                path = _executablePath;
                return true;
            }
        }

        /// <summary>
        /// 运行 rg 时进程无法启动（文件被删除 / 权限变化）：标记为不可用，后续搜索直接回退 .NET，直到重新检测。
        /// </summary>
        public static void MarkUnavailable(string reason)
        {
            lock (_lock)
            {
                _availability = RgAvailability.Unavailable;
                _probeError = reason;
            }
        }

        /// <summary>设置页 / 诊断用的一行描述。</summary>
        public static string Describe()
        {
            lock (_lock)
            {
                switch (_availability)
                {
                    case RgAvailability.Available:
                        return $"ripgrep {_version ?? "?"} ({_source}: {_executablePath})";
                    case RgAvailability.Unavailable:
                        return "Not found — text search uses the built-in .NET scanner";
                    default:
                        return "Not checked yet";
                }
            }
        }

        private static bool Probe()
        {
            var notes = new List<string>();
            var custom = _customPath;
            string foundPath = null, foundVersion = null, foundSource = null;

            foreach (var candidate in EnumerateCandidates(custom, _packageRoot, Environment.GetEnvironmentVariable("PATH"), ProcessArgs.IsWindows))
            {
                var resolved = ResolveShim(candidate.Path);
                if (!File.Exists(resolved))
                {
                    if (candidate.Source == "custom")
                        notes.Add($"Custom rg path '{candidate.Path}' does not exist; falling back to automatic lookup.");
                    continue;
                }

                if (TryRunVersion(resolved, out var version, out var error))
                {
                    foundPath = resolved;
                    foundVersion = version;
                    foundSource = candidate.Source;
                    break;
                }

                notes.Add($"'{resolved}' --version failed: {error}");
            }

            lock (_lock)
            {
                _availability = foundPath != null ? RgAvailability.Available : RgAvailability.Unavailable;
                _executablePath = foundPath;
                _version = foundVersion;
                _source = foundSource;
                _probeError = notes.Count > 0
                    ? string.Join(" ", notes)
                    : (foundPath == null ? "rg was not found in the custom path, the package Plugins folder, PATH or common install folders." : null);
            }

            if (foundPath != null)
                AgentCoreLog.Debug($"[RgLocator] ripgrep {foundVersion} found at {foundPath} ({foundSource})");
            return foundPath != null;
        }

        /// <summary>候选项：路径 + 来源类别。</summary>
        internal readonly struct RgCandidate
        {
            public readonly string Path;
            public readonly string Source;

            public RgCandidate(string path, string source)
            {
                Path = path;
                Source = source;
            }
        }

        /// <summary>
        /// 按优先级列出候选路径（纯函数，不访问文件系统以外的状态；不检查存在性，便于测试）。
        /// </summary>
        internal static IEnumerable<RgCandidate> EnumerateCandidates(string customPath, string packageRoot, string pathEnv, bool windows)
        {
            var exeName = windows ? "rg.exe" : "rg";
            var seen = new HashSet<string>(windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

            IEnumerable<RgCandidate> Raw()
            {
                if (!string.IsNullOrWhiteSpace(customPath))
                {
                    var custom = customPath.Trim().Trim('"');
                    // 允许填目录：自动拼上可执行文件名
                    if (Directory.Exists(custom))
                        custom = System.IO.Path.Combine(custom, exeName);
                    yield return new RgCandidate(custom, "custom");
                }

                if (!string.IsNullOrEmpty(packageRoot))
                {
                    var pluginDir = System.IO.Path.Combine(packageRoot, "Editor", "Plugins", "rg");
                    foreach (var platform in PlatformFolderNames(windows))
                        yield return new RgCandidate(System.IO.Path.Combine(pluginDir, platform, exeName), "bundled");
                    yield return new RgCandidate(System.IO.Path.Combine(pluginDir, exeName), "bundled");
                }

                if (!string.IsNullOrEmpty(pathEnv))
                {
                    foreach (var dir in pathEnv.Split(windows ? ';' : ':'))
                    {
                        var d = dir.Trim().Trim('"');
                        if (d.Length == 0)
                            continue;
                        string candidate;
                        try { candidate = System.IO.Path.Combine(d, exeName); }
                        catch (ArgumentException) { continue; }
                        yield return new RgCandidate(candidate, "PATH");
                    }
                }

                foreach (var dir in CommonInstallDirs(windows))
                    yield return new RgCandidate(System.IO.Path.Combine(dir, exeName), "common-dir");
            }

            foreach (var c in Raw())
            {
                if (string.IsNullOrEmpty(c.Path) || !seen.Add(c.Path))
                    continue;
                yield return c;
            }
        }

        private static IEnumerable<string> PlatformFolderNames(bool windows)
        {
            string arch;
            try
            {
                arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
            }
            catch
            {
                arch = "x64";
            }

            string os;
            if (windows)
                os = "win";
            else if (IsMac())
                os = "osx";
            else
                os = "linux";

            yield return $"{os}-{arch}";
            yield return os;
        }

        private static bool IsMac()
        {
            try { return RuntimeInformation.IsOSPlatform(OSPlatform.OSX); }
            catch { return false; }
        }

        private static IEnumerable<string> CommonInstallDirs(bool windows)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (windows)
            {
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                if (!string.IsNullOrEmpty(home))
                {
                    yield return System.IO.Path.Combine(home, "scoop", "shims");
                    yield return System.IO.Path.Combine(home, ".cargo", "bin");
                }
                if (!string.IsNullOrEmpty(localAppData))
                    yield return System.IO.Path.Combine(localAppData, "Microsoft", "WinGet", "Links");
                if (!string.IsNullOrEmpty(programData))
                    yield return System.IO.Path.Combine(programData, "chocolatey", "bin");
            }
            else
            {
                yield return "/opt/homebrew/bin";
                yield return "/usr/local/bin";
                yield return "/usr/bin";
                yield return "/snap/bin";
                if (!string.IsNullOrEmpty(home))
                    yield return System.IO.Path.Combine(home, ".cargo", "bin");
            }
        }

        /// <summary>
        /// Scoop 的 shim（<c>rg.exe</c> 旁边有 <c>rg.shim</c>，内容 <c>path = "..."</c>）会再派生真正的 rg 子进程；
        /// 直接使用真实路径，结束进程时不会留下孤儿 rg。
        /// </summary>
        internal static string ResolveShim(string exePath)
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(exePath);
                if (string.IsNullOrEmpty(dir))
                    return exePath;
                var shim = System.IO.Path.Combine(dir, System.IO.Path.GetFileNameWithoutExtension(exePath) + ".shim");
                if (!File.Exists(shim))
                    return exePath;

                foreach (var line in File.ReadAllLines(shim))
                {
                    var trimmed = line.Trim();
                    if (!trimmed.StartsWith("path", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var eq = trimmed.IndexOf('=');
                    if (eq < 0)
                        continue;
                    var target = trimmed.Substring(eq + 1).Trim().Trim('"');
                    if (target.Length > 0 && File.Exists(target))
                        return target;
                }
            }
            catch
            {
                // shim 无法解析：用 shim 本身
            }
            return exePath;
        }

        /// <summary>解析 <c>rg --version</c> 首行（"ripgrep 14.1.0 (rev ...)"）中的版本号。</summary>
        internal static string ParseVersion(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
                return null;
            var firstLine = output.Split('\n')[0].Trim();
            var parts = firstLine.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].Equals("ripgrep", StringComparison.OrdinalIgnoreCase))
                return parts[1];
            return null;
        }

        private static bool TryRunVersion(string exePath, out string version, out string error)
        {
            version = null;
            error = null;
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = exePath,
                        Arguments = "--version",
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    }
                };
                process.Start();
                try { process.StandardInput.Close(); } catch { /* 已退出 */ }

                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(ProbeTimeoutMs))
                {
                    try { process.Kill(); } catch { /* 已退出 */ }
                    error = $"timed out after {ProbeTimeoutMs} ms";
                    return false;
                }

                Task.WaitAll(new Task[] { stdoutTask, stderrTask }, ProbeTimeoutMs);
                var stdout = stdoutTask.IsCompleted ? stdoutTask.Result : string.Empty;
                if (process.ExitCode != 0)
                {
                    var stderr = stderrTask.IsCompleted ? stderrTask.Result : string.Empty;
                    error = $"exit code {process.ExitCode}{(string.IsNullOrWhiteSpace(stderr) ? "" : ": " + stderr.Trim())}";
                    return false;
                }

                version = ParseVersion(stdout);
                if (version == null)
                {
                    error = "output does not look like ripgrep";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static string ResolvePackageRoot()
        {
            try
            {
                var pkg = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(RgLocator).Assembly);
                if (pkg != null && !string.IsNullOrEmpty(pkg.resolvedPath))
                    return pkg.resolvedPath;
            }
            catch
            {
                // 不在包内（例如直接放在 Assets 下）：跳过包内候选
            }
            return null;
        }
    }
}
