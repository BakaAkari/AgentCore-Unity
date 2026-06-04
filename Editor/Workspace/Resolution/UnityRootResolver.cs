using System.IO;
using UnityEngine;

namespace AgentCore.Editor.Workspace.Resolution
{
    /// <summary>
    /// 识别当前 Unity 工程根目录。
    /// 输入：Application.dataPath（Unity 内置 Assets 目录路径）。
    /// 输出：规范化正斜杠的 UnityRoot 绝对路径。
    /// </summary>
    public static class UnityRootResolver
    {
        /// <summary>
        /// 解析当前 Unity 工程根目录。
        /// </summary>
        /// <returns>
        /// 规范化正斜杠的 UnityRoot 绝对路径；
        /// 若无法确定则返回 null。
        /// </returns>
        public static string Resolve()
        {
            try
            {
                var dataPath = Application.dataPath;
                if (string.IsNullOrEmpty(dataPath))
                    return null;

                // Application.dataPath 是 .../UnityRoot/Assets
                // UnityRoot = 其父目录
                var parent = Directory.GetParent(dataPath);
                if (parent == null)
                    return null;

                var unityRoot = NormalizePath(parent.FullName);

                // 校验：必须存在 Assets/ 目录
                if (!Directory.Exists(Path.Combine(parent.FullName, "Assets")))
                    return null;

                // 校验：存在 ProjectSettings/ 或 Packages/manifest.json（增强可信度）
                var hasProjectSettings = Directory.Exists(Path.Combine(parent.FullName, "ProjectSettings"));
                var hasManifest = File.Exists(Path.Combine(parent.FullName, "Packages", "manifest.json"));
                if (!hasProjectSettings && !hasManifest)
                {
                    // 仍然返回，但调用方可通过日志感知
                    Debug.LogWarning("[AgentCore] UnityRootResolver: ProjectSettings/ and Packages/manifest.json both missing, result may be unreliable.");
                }

                return unityRoot;
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[AgentCore] UnityRootResolver.Resolve() failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 将路径规范化为正斜杠格式，并去除末尾斜杠。
        /// </summary>
        public static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;
            return path.Replace('\\', '/').TrimEnd('/');
        }
    }
}
