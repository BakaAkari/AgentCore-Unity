using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AgentCore.Editor.Config.Settings;
using AgentCore.Editor.Utils;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Config
{
    /// <summary>
    /// Provider Profile 列表存储（v1.13.0）。
    /// <para>
    /// 使用 <see cref="ScriptableSingleton{T}"/> 存于
    /// <c>ProjectSettings/AgentCoreProviderProfiles.asset</c>（<see cref="FilePathAttribute.Location.ProjectFolder"/>）。
    /// 与 <see cref="AgentCoreSettings"/>（PreferencesFolder，不进 git）不同——此文件<b>进 git</b>：
    /// endpoint + modelName 供团队共享，apiKey 不在此结构中（见 <see cref="SecureKeyStorage"/>）。
    /// </para>
    /// <para>
    /// <c>activeProfileId</c> 为空表示"用 legacy config"（<see cref="AgentCoreSettings"/> 旧字段）。
    /// 所有解析走统一入口 <c>ActiveModelConfig</c>。
    /// </para>
    /// </summary>
    [FilePath("ProjectSettings/AgentCoreProviderProfiles.asset", FilePathAttribute.Location.ProjectFolder)]
    public class AgentCoreProviderProfiles : ScriptableSingleton<AgentCoreProviderProfiles>
    {
        // ── Default profile 硬编码值（首次使用时自动创建，唯一真源）──
        // v1.14.1: 默认 endpoint 指向内网 NewAPI 网关（无用量限制，可统计用量数据）。
        // 该地址仅企业内网可达，公网无法访问，故 API Key 按用户明确决定内置于源码。
        // v1.14.2: 从 ModelAgentSettingsPage（UI 层）下沉至此（数据层）。此前自动创建仅挂在
        // Settings 面板的 OnActivate/Draw 上，新项目安装后若用户直接打开 Chat Window 而不先
        // 打开 Settings 页，Profiles 列表为空，ActiveModelConfig.ModelName 直接抛
        // InvalidOperationException，导致 AgentLoop 初始化失败。
        // v1.14.3: 单独下沉创建逻辑还不够——新建 profile 的 modelName 留空会让请求带 model=""
        // 被网关拒绝（HTTP 400）。现由 EnsureDefaultProfileIfEmpty（仅创建）+
        // EnsureDefaultProfileWithAutoModelAsync（创建 + fetch 模型名）两个入口分别供
        // UI 层（Settings 面板，自带状态提示）和运行时初始化路径（AgentLoop.InitializeAsync）调用。
        private const string DefaultProfileName = "Default";
        private const string DefaultProfileEndpoint = "http://172.16.248.201:34567/v1";
        private const string DefaultProfileApiKeyPlaceholder = "sk-B7YGb4nVwFb9pZsvLf1p8otnDfbThKOjWKsGgnrmwAdcXYJR";

        /// <summary>数据 schema 版本，供将来迁移用。</summary>
        [SerializeField] private int schemaVersion = 1;

        /// <summary>已保存的 profile 列表。</summary>
        [SerializeField] private List<ProviderProfile> profiles = new();

        /// <summary>当前 active profile 的 id；空 = 用 legacy config。</summary>
        [SerializeField] private string activeProfileId = "";

        /// <summary>增删改及切换 active 时触发，供 UI 订阅刷新。</summary>
        public static event Action OnProfilesChanged;

        /// <summary>只读 profile 列表。</summary>
        public IReadOnlyList<ProviderProfile> Profiles => profiles;

        /// <summary>当前 active profile 的 id（空 = legacy config）。</summary>
        public string ActiveProfileId => activeProfileId;

        /// <summary>按 id 查找 profile，找不到返回 null。</summary>
        public ProviderProfile FindById(string id)
        {
            if (string.IsNullOrEmpty(id) || profiles == null)
                return null;
            foreach (var p in profiles)
            {
                if (p != null && p.id == id)
                    return p;
            }
            return null;
        }

        /// <summary>
        /// 返回 <see cref="activeProfileId"/> 对应的 profile；activeProfileId 为空或找不到返回 null（legacy 模式）。
        /// </summary>
        public ProviderProfile GetActive()
            => FindById(activeProfileId);

        /// <summary>
        /// 若 profile 列表为空，自动创建一个指向内网默认 endpoint 的 Default profile 并设为 active。
        /// <para>
        /// 仅负责创建 profile，不做网络请求。供 <c>ModelAgentSettingsPage</c>（Settings 面板）调用——
        /// UI 层随后自行触发一次带状态提示的异步 fetch。运行时初始化路径（<c>AgentLoop.InitializeAsync</c>）
        /// 请用 <see cref="EnsureDefaultProfileWithAutoModelAsync"/>，否则会漏掉 modelName 自动填充。
        /// </para>
        /// 幂等：已有 profile 时直接返回 false，不做任何事。
        /// </summary>
        /// <returns>true 表示本次调用创建了新的 Default profile；false 表示已存在 profile，未作改动。</returns>
        public bool EnsureDefaultProfileIfEmpty()
        {
            if (profiles != null && profiles.Count > 0)
                return false;

            var p = ProviderProfile.Create(DefaultProfileName);
            p.endpoint = DefaultProfileEndpoint;
            p.modelName = ""; // 由调用方决定是否触发自动 fetch（见 EnsureDefaultProfileWithAutoModelAsync）
            AddProfile(p);
            SecureKeyStorage.SetProfileApiKey(p.id, DefaultProfileApiKeyPlaceholder);
            SetActive(p.id);
            return true;
        }

        /// <summary>
        /// v1.14.3: 组合入口——创建 Default profile（若为空）后，立即异步 fetch 并填入首个可用模型名。
        /// <para>
        /// 专供不经过 Settings 面板 UI 的初始化路径使用（当前为 <c>AgentLoop.CompleteInitialize</c>）。
        /// Settings 面板路径请继续使用 <see cref="EnsureDefaultProfileIfEmpty"/> + 自身的
        /// <c>TryAutoSelectFirstModelAsync</c>（带 UI 状态提示），不要调用本方法，否则会重复 fetch。
        /// </para>
        /// <para>
        /// 背景：新建的 profile modelName 留空会导致后续请求发出 <c>model=""</c>，被 OpenAI-compatible
        /// 网关拒绝（HTTP 400 "Model name not specified"）。此前只有 Settings 面板路径会异步 fetch 补上
        /// modelName，AgentLoop 初始化路径完全没有等价逻辑——上次修复只解决了"抛异常崩溃"，没有解决
        /// "创建出一个永远打不通的空 model 配置"。
        /// </para>
        /// </summary>
        public async Task EnsureDefaultProfileWithAutoModelAsync()
        {
            bool created = EnsureDefaultProfileIfEmpty();
            if (!created)
                return;

            var active = GetActive();
            if (active == null)
                return;

            await TryAutoSelectFirstModelAsync(active.id);
        }

        /// <summary>
        /// 异步 fetch 指定 profile 的可用模型列表，取第一个填入 modelName（仅当仍为空时）。
        /// 静默失败：fetch 失败只记 Warning，不抛异常、不阻塞调用方（初始化路径不能因网络问题卡死）。
        /// </summary>
        private static async Task TryAutoSelectFirstModelAsync(string profileId)
        {
            try
            {
                var store = instance;
                var p = store.FindById(profileId);
                if (p == null)
                    return;

                var service = new ModelSettingsService();
                var models = await service.FetchModelsAsync(p.endpoint, SecureKeyStorage.GetProfileApiKey(profileId));
                if (models == null || models.Count == 0)
                    return;

                AsyncHelper.RunOnMainThread(() =>
                {
                    var current = store.FindById(profileId);
                    if (current != null && string.IsNullOrEmpty(current.modelName))
                    {
                        store.UpdateProfile(profileId, x => x.modelName = models[0]);
                    }
                });
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore] Auto-select default model for '{profileId}' failed: {ex.Message}");
            }
        }

        /// <summary>加入一个 profile 到列表并保存。</summary>
        public void AddProfile(ProviderProfile p)
        {
            if (p == null)
                return;
            profiles ??= new List<ProviderProfile>();
            profiles.Add(p);
            SaveAndNotify();
        }

        /// <summary>
        /// 从列表移除指定 profile，并删除其对应的 EditorPrefs apiKey；
        /// 若被移除的是当前 active，则把 activeProfileId 置空（回落 legacy config）。
        /// </summary>
        public void RemoveProfile(string id)
        {
            if (string.IsNullOrEmpty(id) || profiles == null)
                return;

            int removed = profiles.RemoveAll(p => p != null && p.id == id);
            if (removed == 0)
                return;

            // R2 缓解：删 profile 必须同步清理其 apiKey，避免 EditorPrefs 键膨胀。
            SecureKeyStorage.DeleteProfileApiKey(id);

            if (activeProfileId == id)
                activeProfileId = "";

            SaveAndNotify();
        }

        /// <summary>
        /// 设置 active profile。<paramref name="id"/> 为空表示回落 legacy config。
        /// 若指向一个存在的 profile，则更新其 lastUsedAtUnixMs。
        /// </summary>
        public void SetActive(string id)
        {
            activeProfileId = id ?? "";

            var target = FindById(activeProfileId);
            if (target != null)
                target.lastUsedAtUnixMs = DateTimeOffset.Now.ToUnixTimeMilliseconds();

            SaveAndNotify();
        }

        /// <summary>
        /// 找到指定 profile 并交给调用方修改字段，随后保存并通知。
        /// 找不到则 no-op。
        /// </summary>
        public void UpdateProfile(string id, Action<ProviderProfile> mutate)
        {
            if (mutate == null)
                return;

            var target = FindById(id);
            if (target == null)
                return;

            mutate(target);
            SaveAndNotify();
        }

        private void SaveAndNotify()
        {
            SafeSave();
            OnProfilesChanged?.Invoke();
        }

        /// <summary>
        /// 保存到 <c>ProjectSettings/AgentCoreProviderProfiles.asset</c>。
        /// ProjectSettings 目录在 Unity 项目中恒存在，故直接 Save 并吞掉 IO 异常，
        /// 不阻塞 Editor（参考 <see cref="AgentCoreSettings.SafeSave"/> 的健壮性动机）。
        /// </summary>
        internal void SafeSave()
        {
            try
            {
                Save(true);
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore] AgentCoreProviderProfiles.Save failed: {ex.Message}");
            }
        }
    }
}
