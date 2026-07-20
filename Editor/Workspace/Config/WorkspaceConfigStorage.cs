using System;
using System.IO;
using AgentCore.Editor.Utils;
using AgentCore.Editor.Workspace.Resolution;
using UnityEngine;

namespace AgentCore.Editor.Workspace.Config
{
    /// <summary>
    /// 读写 WorkspaceRoot/.agentcore/workspace.json。
    /// 文件不存在时返回默认空配置（不报错）。
    /// 只允许读写 WorkspaceRoot 内的路径，不访问 WorkspaceRoot 外部。
    /// </summary>
    public static class WorkspaceConfigStorage
    {
        private const string AgentCoreDir = ".agentcore";
        private const string ConfigFileName = "workspace.json";

        /// <summary>
        /// 加载 WorkspaceRoot 下的 workspace.json。
        /// 文件不存在时返回默认空配置。
        /// </summary>
        /// <param name="workspaceRoot">规范化正斜杠的 WorkspaceRoot 绝对路径。</param>
        /// <returns>WorkspaceConfig 实例（永不为 null）。</returns>
        public static WorkspaceConfig Load(string workspaceRoot)
        {
            var path = GetConfigPath(workspaceRoot);
            if (path == null || !File.Exists(path))
                return new WorkspaceConfig();

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
        /// 保存 WorkspaceConfig 到 WorkspaceRoot/.agentcore/workspace.json。
        /// 自动创建 .agentcore 目录。
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
                File.WriteAllText(path, json, System.Text.Encoding.UTF8);
                return true;
            }
            catch (Exception ex)
            {
                AgentCoreLog.Error($"[AgentCore] WorkspaceConfigStorage.Save failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 获取 workspace.json 的绝对路径。
        /// </summary>
        public static string GetConfigPath(string workspaceRoot)
        {
            if (string.IsNullOrEmpty(workspaceRoot))
                return null;
            return UnityRootResolver.NormalizePath(
                Path.Combine(workspaceRoot, AgentCoreDir, ConfigFileName));
        }

        /// <summary>
        /// 检查 workspace.json 是否存在。
        /// </summary>
        public static bool Exists(string workspaceRoot)
        {
            var path = GetConfigPath(workspaceRoot);
            return path != null && File.Exists(path);
        }
    }
}
