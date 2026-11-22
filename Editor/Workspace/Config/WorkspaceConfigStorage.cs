using System;
using System.IO;
using AgentCore.Editor.Utils;
using AgentCore.Editor.Workspace.Resolution;

namespace AgentCore.Editor.Workspace.Config
{
    /// <summary>
    /// 读写个人本地 Workspace 配置 <c>UserSettings/AgentCore/WorkspaceConfig.json</c>。
    /// <para>
    /// 主路径位于 Unity 工程根目录的 UserSettings 下（WorkspaceRoot 等于或包含 Unity 工程时）；
    /// WorkspaceRoot 与当前 Unity 工程无关（如测试临时目录）时落在 <c>{workspaceRoot}/UserSettings/AgentCore/</c>。
    /// 主路径不存在时只读回退遗留的 <c>{workspaceRoot}/.agentcore/workspace.json</c>；本类绝不创建 <c>.agentcore/</c>。
    /// 文件不存在时返回默认空配置（不报错）。
    /// </para>
    /// </summary>
    public static class WorkspaceConfigStorage
    {
        /// <summary>主配置文件（相对 Unity 工程根目录）。</summary>
        public const string RelativePath = "UserSettings/AgentCore/WorkspaceConfig.json";

        private const string LegacyAgentCoreDir = ".agentcore";
        private const string LegacyConfigFileName = "workspace.json";

        /// <summary>
        /// 加载 Workspace 配置：主路径优先，不存在时只读回退遗留 <c>.agentcore/workspace.json</c>。
        /// 文件不存在时返回默认空配置。
        /// </summary>
        /// <param name="workspaceRoot">规范化正斜杠的 WorkspaceRoot 绝对路径。</param>
        /// <returns>WorkspaceConfig 实例（永不为 null）。</returns>
        public static WorkspaceConfig Load(string workspaceRoot)
        {
            var path = GetConfigPath(workspaceRoot);
            if (path == null)
                return new WorkspaceConfig();
            if (!File.Exists(path))
            {
                path = GetLegacyConfigPath(workspaceRoot);
                if (!File.Exists(path))
                    return new WorkspaceConfig();
            }

            try
            {
                var json = File.ReadAllText(path, System.Text.Encoding.UTF8);
                var config = JsonHelper.Deserialize<WorkspaceConfig>(json);
                return config ?? new WorkspaceConfig();
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore] WorkspaceConfigStorage.Load failed: {ex.Message}");
                return new WorkspaceConfig();
            }
        }

        /// <summary>
        /// 保存 WorkspaceConfig 到主路径 <c>UserSettings/AgentCore/WorkspaceConfig.json</c>（自动创建目录）。
        /// 遗留 <c>.agentcore/workspace.json</c> 不会被修改。
        /// </summary>
        /// <param name="workspaceRoot">规范化正斜杠的 WorkspaceRoot 绝对路径。</param>
        /// <param name="config">要保存的配置。</param>
        /// <returns>保存成功返回 true；失败返回 false。</returns>
        public static bool Save(string workspaceRoot, WorkspaceConfig config)
        {
            var path = GetConfigPath(workspaceRoot);
            if (path == null)
            {
                AgentCoreLog.Warning("[AgentCore] WorkspaceConfigStorage.Save: workspaceRoot is null or empty.");
                return false;
            }

            try
            {
                config.LastModified = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var json = JsonHelper.Serialize(config, pretty: true);
                AtomicFile.WriteAllText(path, json, System.Text.Encoding.UTF8);
                return true;
            }
            catch (Exception ex)
            {
                AgentCoreLog.Error($"[AgentCore] WorkspaceConfigStorage.Save failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 获取主配置文件的绝对路径（正斜杠）。
        /// </summary>
        public static string GetConfigPath(string workspaceRoot)
        {
            if (string.IsNullOrEmpty(workspaceRoot))
                return null;
            return UnityRootResolver.NormalizePath(
                Path.Combine(ResolveStorageRoot(workspaceRoot), RelativePath));
        }

        /// <summary>
        /// 获取遗留 <c>{workspaceRoot}/.agentcore/workspace.json</c> 的绝对路径（只读回退）。
        /// </summary>
        public static string GetLegacyConfigPath(string workspaceRoot)
        {
            if (string.IsNullOrEmpty(workspaceRoot))
                return null;
            return UnityRootResolver.NormalizePath(
                Path.Combine(workspaceRoot, LegacyAgentCoreDir, LegacyConfigFileName));
        }

        /// <summary>
        /// 检查主配置或遗留配置是否存在。
        /// </summary>
        public static bool Exists(string workspaceRoot)
        {
            var path = GetConfigPath(workspaceRoot);
            if (path == null)
                return false;
            return File.Exists(path) || File.Exists(GetLegacyConfigPath(workspaceRoot));
        }

        // WorkspaceRoot 等于或包含当前 Unity 工程时落到工程的 UserSettings；否则（与当前工程无关的目录）落在 workspaceRoot 下。
        // 优先使用 UnityRootResolver 主线程预热缓存，避免受其它插件修改 cwd 的影响；仅缓存不可用时回退检查 cwd。
        private static string ResolveStorageRoot(string workspaceRoot)
        {
            var workspace = UnityRootResolver.NormalizePath(Path.GetFullPath(workspaceRoot));
            var unityRoot = UnityRootResolver.GetCachedOrResolve();
            if (string.IsNullOrEmpty(unityRoot))
            {
                try
                {
                    var cwd = UnityRootResolver.NormalizePath(Path.GetFullPath(Directory.GetCurrentDirectory()));
                    if (Directory.Exists(Path.Combine(cwd, "Assets"))
                        && Directory.Exists(Path.Combine(cwd, "ProjectSettings")))
                        unityRoot = cwd;
                }
                catch (Exception)
                {
                    // 忽略 cwd 解析异常
                }
            }

            if (!string.IsNullOrEmpty(unityRoot)
                && (string.Equals(unityRoot, workspace, StringComparison.OrdinalIgnoreCase)
                    || unityRoot.StartsWith(workspace + "/", StringComparison.OrdinalIgnoreCase)))
                return unityRoot;

            return workspace;
        }
    }
}
