using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AgentCore.Editor.Config.Settings;
using AgentCore.Editor.Utils;
using UnityEditor;

namespace AgentCore.Editor.Config
{
    /// <summary>
    /// 各 Provider Profile 的可用模型列表缓存（v1.17.0）。
    /// <para>
    /// 设置页 "Fetch Models" 与 chat 面板的模型下拉共用同一份数据：存于 <see cref="SessionState"/>
    /// （跨域重载保留，Editor 重启后按需重新拉取）。拉取经 <see cref="ModelSettingsService.FetchModelsAsync(ProviderProfile,string,System.Threading.CancellationToken)"/>，
    /// 与设置页相同的协议/鉴权处理。
    /// </para>
    /// </summary>
    public static class ProviderModelCatalog
    {
        private const string KeyPrefix = "AgentCore.ModelCatalog.";

        private static readonly HashSet<string> Fetching = new();

        /// <summary>某个 profile 的模型列表更新或拉取状态变化（主线程触发）。参数为 profileId。</summary>
        public static event Action<string> OnCatalogChanged;

        /// <summary>已缓存的模型列表（未拉取过返回空列表）。</summary>
        public static List<string> Get(string profileId)
        {
            if (string.IsNullOrEmpty(profileId)) return new List<string>();
            var raw = SessionState.GetString(KeyPrefix + profileId, string.Empty);
            return string.IsNullOrEmpty(raw)
                ? new List<string>()
                : new List<string>(raw.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries));
        }

        /// <summary>是否拉取过（拉取结果为空也算）。</summary>
        public static bool HasFetched(string profileId)
            => !string.IsNullOrEmpty(profileId) && SessionState.GetBool(KeyPrefix + profileId + ".fetched", false);

        /// <summary>是否正在拉取。</summary>
        public static bool IsFetching(string profileId) => profileId != null && Fetching.Contains(profileId);

        /// <summary>写入模型列表（设置页拉取成功后也调用）。</summary>
        public static void Set(string profileId, IReadOnlyList<string> models)
        {
            if (string.IsNullOrEmpty(profileId)) return;
            SessionState.SetString(KeyPrefix + profileId, models == null ? string.Empty : string.Join("\n", models));
            SessionState.SetBool(KeyPrefix + profileId + ".fetched", true);
            OnCatalogChanged?.Invoke(profileId);
        }

        /// <summary>
        /// 异步拉取并缓存模型列表；同一 profile 同时只拉一次。失败只记 Warning，返回 false。
        /// </summary>
        public static async Task<bool> FetchAsync(string profileId)
        {
            var profile = AgentCoreProviderProfiles.instance.FindById(profileId);
            if (profile == null || string.IsNullOrWhiteSpace(profile.endpoint) || !Fetching.Add(profileId))
                return false;

            OnCatalogChanged?.Invoke(profileId);
            try
            {
                var models = await new ModelSettingsService().FetchModelsAsync(profile, SecureKeyStorage.GetProfileApiKey(profileId));
                AsyncHelper.RunOnMainThread(() =>
                {
                    Fetching.Remove(profileId);
                    Set(profileId, models ?? new List<string>());
                });
                return true;
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore] Fetch models for profile '{profile.displayName}' failed: {ex.Message}");
                AsyncHelper.RunOnMainThread(() =>
                {
                    Fetching.Remove(profileId);
                    OnCatalogChanged?.Invoke(profileId);
                });
                return false;
            }
        }
    }
}
