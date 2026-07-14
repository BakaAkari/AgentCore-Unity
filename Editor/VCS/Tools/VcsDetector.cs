using System;
using System.IO;
using AgentCore.Editor.Workspace;
using UnityEngine;

namespace AgentCore.Editor.Components.VCS.Tools
{
    /// <summary>
    /// 版本控制系统检测器。
    /// 按优先级 SVN > Perforce > Git 检测项目使用的 VCS。
    /// SVN 检测优先复用 <see cref="WorkspaceContextService"/> 的缓存结果，避免重复执行 svn 命令。
    /// </summary>
    public static class VcsDetector
    {
        private static VcsType? _cachedType;
        private static string _cachedRootPath;

        /// <summary>
        /// 检测当前项目使用的版本控制系统。
        /// </summary>
        /// <returns>检测到的 VCS 类型。</returns>
        public static VcsType DetectVcs()
        {
            if (_cachedType.HasValue)
                return _cachedType.Value;

            // ── 优先级 1: 复用 WorkspaceContextService 的 SVN 缓存 ──────────
            // WorkspaceContextService 已经运行过 svn info，直接读取结果，
            // 避免 VcsDetector 再次启动子进程。
            try
            {
                var wsCtx = WorkspaceContextService.GetCurrent();
                if (wsCtx != null && wsCtx.Vcs != null)
                {
                    switch (wsCtx.Vcs.Type)
                    {
                        case WorkspaceVcsType.Svn:
                            _cachedType = VcsType.Svn;
                            // 优先使用 WorkspaceRoot（SVN working copy root），
                            // 回退到 Unity 项目根目录。
                            _cachedRootPath = !string.IsNullOrEmpty(wsCtx.WorkspaceRoot)
                                ? wsCtx.WorkspaceRoot
                                : GetProjectRootPath();
                            return VcsType.Svn;

                        case WorkspaceVcsType.Git:
                            _cachedType = VcsType.Git;
                            _cachedRootPath = !string.IsNullOrEmpty(wsCtx.WorkspaceRoot)
                                ? wsCtx.WorkspaceRoot
                                : FindGitRoot(GetProjectRootPath());
                            return VcsType.Git;

                        case WorkspaceVcsType.Perforce:
                            _cachedType = VcsType.Perforce;
                            _cachedRootPath = !string.IsNullOrEmpty(wsCtx.WorkspaceRoot)
                                ? wsCtx.WorkspaceRoot
                                : GetProjectRootPath();
                            return VcsType.Perforce;

                        case WorkspaceVcsType.None:
                            // WorkspaceContext 明确说明无 VCS，跳过后续检测
                            _cachedType = VcsType.None;
                            _cachedRootPath = GetProjectRootPath();
                            return VcsType.None;

                        // WorkspaceVcsType.Unknown → 继续本地检测
                    }
                }
            }
            catch
            {
                // WorkspaceContextService 不可用时，降级到本地检测
            }

            // ── 本地检测（WorkspaceContext 不可用或 VcsType.Unknown 时） ────
            var projectPath = GetProjectRootPath();

            // 优先级 1: SVN（.svn 目录检测）
            if (IsSvnRepository(projectPath))
            {
                _cachedType = VcsType.Svn;
                _cachedRootPath = projectPath;
                return VcsType.Svn;
            }

            // 优先级 2: Perforce
            if (IsPerforceWorkspace(projectPath))
            {
                _cachedType = VcsType.Perforce;
                _cachedRootPath = projectPath;
                return VcsType.Perforce;
            }

            // 优先级 3: Git
            if (IsGitRepository(projectPath))
            {
                _cachedType = VcsType.Git;
                _cachedRootPath = FindGitRoot(projectPath);
                return VcsType.Git;
            }

            _cachedType = VcsType.None;
            _cachedRootPath = projectPath;
            return VcsType.None;
        }

        /// <summary>
        /// 获取 VCS 根目录路径。
        /// 对于 SVN，返回 working copy root（由 WorkspaceContextService 解析）。
        /// </summary>
        public static string GetVcsRootPath()
        {
            if (_cachedRootPath == null)
                DetectVcs();
            return _cachedRootPath;
        }

        /// <summary>
        /// 清除缓存，强制重新检测。
        /// </summary>
        public static void ClearCache()
        {
            _cachedType = null;
            _cachedRootPath = null;
        }

        /// <summary>
        /// 检查指定 VCS 命令是否可用
        /// </summary>
        public static bool IsVcsCommandAvailable(VcsType vcsType)
        {
            try
            {
                var command = vcsType switch
                {
                    VcsType.Svn => "svn --version",
                    VcsType.Perforce => "p4 -V",
                    VcsType.Git => "git --version",
                    _ => null
                };

                if (command == null)
                    return false;

                using var process = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = GetShellCommand(),
                        Arguments = GetShellArguments(command),
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                process.WaitForExit(3000); // 3秒超时
                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        private static string GetProjectRootPath()
        {
            // Unity 项目根目录（包含 Assets 文件夹的目录）
            var dataPath = Application.dataPath;
            return Directory.GetParent(dataPath)?.FullName ?? dataPath;
        }

        private static bool IsSvnRepository(string path)
        {
            // 检查 .svn 目录
            var svnDir = Path.Combine(path, ".svn");
            return Directory.Exists(svnDir);
        }

        private static bool IsPerforceWorkspace(string path)
        {
            // Perforce 通过环境变量或配置文件识别
            // 检查 P4CONFIG 环境变量指定的配置文件
            var p4Config = Environment.GetEnvironmentVariable("P4CONFIG");
            if (!string.IsNullOrEmpty(p4Config))
            {
                var configPath = Path.Combine(path, p4Config);
                if (File.Exists(configPath))
                    return true;
            }

            // 检查常见的 Perforce 配置文件
            var commonConfigs = new[] { ".p4config", "p4config.txt", ".p4ignore" };
            foreach (var config in commonConfigs)
            {
                if (File.Exists(Path.Combine(path, config)))
                    return true;
            }

            // 检查是否设置了 P4CLIENT 环境变量
            var p4Client = Environment.GetEnvironmentVariable("P4CLIENT");
            if (!string.IsNullOrEmpty(p4Client))
                return true;

            return false;
        }

        private static bool IsGitRepository(string path)
        {
            // 向上查找 .git 目录
            var current = new DirectoryInfo(path);
            while (current != null)
            {
                var gitDir = Path.Combine(current.FullName, ".git");
                if (Directory.Exists(gitDir) || File.Exists(gitDir)) // .git 可能是文件（submodule）
                    return true;

                current = current.Parent;
            }
            return false;
        }

        private static string FindGitRoot(string path)
        {
            var current = new DirectoryInfo(path);
            while (current != null)
            {
                var gitDir = Path.Combine(current.FullName, ".git");
                if (Directory.Exists(gitDir) || File.Exists(gitDir))
                    return current.FullName;

                current = current.Parent;
            }
            return path;
        }

        private static string GetShellCommand()
        {
            return Application.platform == RuntimePlatform.WindowsEditor ? "cmd.exe" : "/bin/bash";
        }

        private static string GetShellArguments(string command)
        {
            return Application.platform == RuntimePlatform.WindowsEditor 
                ? $"/c {command}" 
                : $"-c \"{command}\"";
        }
    }
}
