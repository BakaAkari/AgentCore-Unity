using System;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Tools.Infrastructure
{
    /// <summary>
    /// 创建资产时目标路径已存在的处理策略。
    /// </summary>
    public enum AssetConflictMode
    {
        /// <summary>目标已存在时返回失败（默认）。</summary>
        Error,
        /// <summary>改用 AssetDatabase.GenerateUniqueAssetPath 生成的新路径。</summary>
        Unique,
        /// <summary>覆盖已有资产（旧行为）。</summary>
        Overwrite
    }

    /// <summary>
    /// 统一的 "on_conflict" 参数解析与路径冲突判定，供各 create 类 action 共用，
    /// 避免静默覆盖用户已有资产。
    /// </summary>
    public static class AssetConflictPolicy
    {
        /// <summary>参数名（snake_case，与工具 schema 保持一致）。</summary>
        public const string ParameterName = "on_conflict";

        /// <summary>
        /// 参数说明文本（各工具 schema 中 on_conflict 的 description 与此保持一致）。
        /// </summary>
        public const string Description =
            "What to do if an asset already exists at the target path: 'error' (default, fail without touching it), " +
            "'unique' (write to an auto-generated unique path, e.g. 'Name 1.asset'), 'overwrite' (replace the existing asset).";

        /// <summary>
        /// 解析 on_conflict 参数（兼容 camelCase onConflict），缺省为 Error。非法值抛 ArgumentException。
        /// </summary>
        public static AssetConflictMode GetMode(JObject parameters)
        {
            var raw = parameters?[ParameterName]?.ToString();
            if (string.IsNullOrEmpty(raw))
                raw = parameters?["onConflict"]?.ToString();
            if (string.IsNullOrEmpty(raw))
                return AssetConflictMode.Error;

            switch (raw.Trim().ToLowerInvariant())
            {
                case "error": return AssetConflictMode.Error;
                case "unique": return AssetConflictMode.Unique;
                case "overwrite": return AssetConflictMode.Overwrite;
                default:
                    throw new ArgumentException(
                        $"Invalid value for '{ParameterName}': '{raw}'. Valid values: error, unique, overwrite.");
            }
        }

        /// <summary>
        /// 判断资产路径上是否已有资产（AssetDatabase 已导入或磁盘上已存在文件）。
        /// </summary>
        public static bool AssetExists(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return false;
            if (AssetDatabase.LoadMainAssetAtPath(assetPath) != null)
                return true;

            // 尚未导入的文件同样视为冲突
            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            var fullPath = string.IsNullOrEmpty(projectRoot) ? assetPath : Path.Combine(projectRoot, assetPath);
            return File.Exists(fullPath);
        }

        /// <summary>
        /// 按 on_conflict 策略解析最终写入路径。
        /// 返回 true 时 <paramref name="assetPath"/> 为最终应写入的路径；
        /// 返回 false 时 <paramref name="error"/> 为应原样返回给 Agent 的失败信息。
        /// <paramref name="parameterName"/> 仅用于错误提示，camelCase 风格的工具传 "onConflict"。
        /// </summary>
        public static bool TryResolvePath(JObject parameters, ref string assetPath, out string error,
            string parameterName = ParameterName)
        {
            error = null;
            AssetConflictMode mode;
            try
            {
                mode = GetMode(parameters);
            }
            catch (ArgumentException ex)
            {
                error = ex.Message;
                return false;
            }
            return TryResolvePath(mode, ref assetPath, out error, parameterName);
        }

        /// <summary>
        /// 按指定策略解析最终写入路径（见 <see cref="TryResolvePath(JObject, ref string, out string)"/>）。
        /// </summary>
        public static bool TryResolvePath(AssetConflictMode mode, ref string assetPath, out string error,
            string parameterName = ParameterName)
        {
            error = null;
            if (!AssetExists(assetPath))
                return true;

            switch (mode)
            {
                case AssetConflictMode.Overwrite:
                    return true;
                case AssetConflictMode.Unique:
                    assetPath = AssetDatabase.GenerateUniqueAssetPath(assetPath);
                    return true;
                default:
                    error = $"An asset already exists at '{assetPath}'. Nothing was written. " +
                            $"Retry with {parameterName}=\"unique\" to create it at a new non-conflicting path, " +
                            $"or {parameterName}=\"overwrite\" to replace the existing asset (only if the user intends that).";
                    return false;
            }
        }
    }
}
