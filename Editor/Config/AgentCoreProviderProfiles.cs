using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using AgentCore.Editor.Config.Settings;
using AgentCore.Editor.Utils;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace AgentCore.Editor.Config
{
    /// <summary>
    /// Provider Profile 列表存储（v1.13.0），个人本地配置。
    /// <para>
    /// 全部数据（profile 列表、active / 辅助任务 profile、各 profile 的模型与运行时验证 / 缓存字段）只保存在
    /// 本机 <see cref="ProviderProfilesStorage.LocalProfilesPath"/>，不写入 <c>ProjectSettings/</c>，不进版本库；
    /// apiKey 不在此结构中（见 <see cref="SecureKeyStorage"/>）。<see cref="ScriptableSingleton{T}"/> 不带
    /// <c>FilePath</c>，只驻留内存：首次访问（含域重载后）从 JSON 加载，每次修改立即原子写回。
    /// </para>
    /// <para>
    /// 迁移：本地文件不存在时，只读读取旧版 <c>ProjectSettings/AgentCoreProviderProfiles.asset</c> 与
    /// <c>UserSettings/AgentCore/ProviderProfilesState.json</c> 合并后写入本地文件；旧文件从不修改或删除。
    /// </para>
    /// <para>
    /// <c>activeProfileId</c> 为空表示没有 active profile。所有解析走统一入口 <c>ActiveModelConfig</c>。
    /// </para>
    /// </summary>
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
        // v1.14.11: 改为直连 sourceai（172.16.248.60）上的 vLLM 实例（DeepSeek-V4-Flash，
        // TP8+EP，已用 curl 验证 /v1/models 与 /v1/chat/completions 均 200）。该地址同样仅
        // 企业内网可达；vLLM 未设 --api-key，不校验鉴权头，故 API Key 占位值改为无意义的
        // "sk-xxx"（此前网关地址的真实 key 已随之失效，不再需要）。
        private const string DefaultProfileName = "Default";
        private const string DefaultProfileEndpoint = "http://172.16.248.60:8000/v1";
        private const string DefaultProfileApiKeyPlaceholder = "sk-xxx";

        // 字段保持 Unity 可序列化（名称不可改），只为迁移时用 Unity 反序列化器读出旧版 ProjectSettings asset；
        // 运行时数据以本地 JSON 为准。
        [SerializeField] private int schemaVersion = 1;
        [SerializeField] private List<ProviderProfile> profiles = new();
        [SerializeField] private string activeProfileId = "";
        [SerializeField] private string utilityProfileId = "";

        /// <summary>本域内是否已从本地文件加载（域重载后为 false，首次访问重新加载）。</summary>
        [NonSerialized] private bool _loaded;

        /// <summary>
        /// 保护加载与 <see cref="ProviderProfile.modelName"/> 的个人切换：LLM 客户端可能在后台线程经
        /// <see cref="ActiveModelConfig.ModelName"/> 读取，写入只在主线程。
        /// </summary>
        private static readonly object Sync = new object();

        /// <summary>本地文件路径（相对项目根目录）。测试可临时替换以隔离真实配置。</summary>
        internal static string LocalPath = ProviderProfilesStorage.LocalProfilesPath;

        [InitializeOnLoadMethod]
        private static void PreloadOnDomainLoad()
        {
            try
            {
                instance.EnsureLoaded();
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore] Provider profiles preload failed: {ex.Message}");
            }
        }

        /// <summary>增删改及切换 active 时触发，供 UI 订阅刷新。</summary>
        public static event Action OnProfilesChanged;

        /// <summary>只读 profile 列表。</summary>
        public IReadOnlyList<ProviderProfile> Profiles
        {
            get
            {
                EnsureLoaded();
                return profiles;
            }
        }

        /// <summary>当前 active profile id（空 = 无 active profile）。</summary>
        public string ActiveProfileId
        {
            get
            {
                EnsureLoaded();
                return activeProfileId ?? "";
            }
        }

        /// <summary>按 id 查找 profile，找不到返回 null。</summary>
        public ProviderProfile FindById(string id)
        {
            if (string.IsNullOrEmpty(id))
                return null;
            EnsureLoaded();
            if (profiles == null)
                return null;
            foreach (var p in profiles)
            {
                if (p != null && p.id == id)
                    return p;
            }
            return null;
        }

        /// <summary>返回当前 active profile；为空或找不到返回 null。</summary>
        public ProviderProfile GetActive()
            => FindById(ActiveProfileId);

        /// <summary>profile 当前选择的模型名（<see cref="ProviderProfile.modelName"/>）。可在后台线程调用（只读）。</summary>
        public string GetEffectiveModelName(ProviderProfile profile)
        {
            if (profile == null)
                return null;
            lock (Sync)
                return profile.modelName;
        }

        /// <summary>
        /// 设置 profile 的模型（chat 面板 / 设置页模型下拉），直接改 <see cref="ProviderProfile.modelName"/> 并保存到本地文件。
        /// 空白或与当前相同时不做任何事。
        /// </summary>
        public void SetUserModel(string profileId, string model)
        {
            var target = FindById(profileId);
            model = model?.Trim();
            if (target == null || string.IsNullOrEmpty(model) || string.Equals(model, target.modelName, StringComparison.Ordinal))
                return;

            lock (Sync)
                target.modelName = model;
            target.lastUsedAtUnixMs = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            SaveAndNotify();
        }

        /// <summary>辅助任务 profile id（空 = 跟随 active）；指向已删除的 profile 时视为空。</summary>
        public string UtilityProfileId
        {
            get
            {
                EnsureLoaded();
                var id = utilityProfileId ?? "";
                return id.Length == 0 || FindById(id) != null ? id : "";
            }
        }

        /// <summary>辅助任务 profile；未设置或已被删除返回 null（调用方应回落 active）。</summary>
        public ProviderProfile GetUtility()
            => FindById(UtilityProfileId);

        /// <summary>设置辅助任务 profile（空 = 跟随 active）；指向不存在 profile 的 id 视为空。</summary>
        public void SetUtility(string id)
        {
            EnsureLoaded();
            id = FindById(id) != null ? id : "";
            if (string.Equals(utilityProfileId ?? "", id, StringComparison.Ordinal))
                return;
            utilityProfileId = id;
            SaveAndNotify();
        }

        /// <summary>
        /// 解析 profile 的 fallback 链（v1.17.0）：按 <see cref="ProviderProfile.fallbackProfileIds"/> 顺序，
        /// 跳过不存在的 id、自身与重复项。
        /// </summary>
        public List<ProviderProfile> GetFallbackChain(ProviderProfile primary)
        {
            var chain = new List<ProviderProfile>();
            if (primary?.fallbackProfileIds == null) return chain;
            var seen = new HashSet<string> { primary.id };
            foreach (var id in primary.fallbackProfileIds)
            {
                if (string.IsNullOrEmpty(id) || !seen.Add(id)) continue;
                var p = FindById(id);
                if (p != null) chain.Add(p);
            }
            return chain;
        }

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
            EnsureLoaded();
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
        /// 网关拒绝（HTTP 400 "Model name not specified"）。
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
                var models = await service.FetchModelsAsync(p, SecureKeyStorage.GetProfileApiKey(profileId));
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
            EnsureLoaded();
            profiles ??= new List<ProviderProfile>();
            profiles.Add(p);
            SaveAndNotify();
        }

        /// <summary>
        /// 从列表移除指定 profile，并删除其对应的 EditorPrefs apiKey；
        /// 若被移除的是当前 active / 辅助任务 profile，则对应 id 置空。
        /// </summary>
        public void RemoveProfile(string id)
        {
            if (string.IsNullOrEmpty(id))
                return;
            EnsureLoaded();
            if (profiles == null)
                return;

            int removed = profiles.RemoveAll(p => p != null && p.id == id);
            if (removed == 0)
                return;

            // R2 缓解：删 profile 必须同步清理其 apiKey，避免 EditorPrefs 键膨胀。
            SecureKeyStorage.DeleteProfileApiKey(id);

            if (activeProfileId == id)
                activeProfileId = "";
            if (utilityProfileId == id)
                utilityProfileId = "";

            SaveAndNotify();
        }

        /// <summary>
        /// 设置 active profile。<paramref name="id"/> 为空或不存在表示没有 active profile。
        /// 若指向一个存在的 profile，则更新其 lastUsedAtUnixMs。
        /// </summary>
        public void SetActive(string id)
        {
            EnsureLoaded();
            var target = FindById(id);
            activeProfileId = target != null ? id : "";
            if (target != null)
                target.lastUsedAtUnixMs = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            SaveAndNotify();
        }

        /// <summary>
        /// 找到指定 profile 并交给调用方修改字段，随后保存并通知。连接身份变化时清除验证与缓存观测结果。
        /// 找不到则 no-op。
        /// </summary>
        public void UpdateProfile(string id, Action<ProviderProfile> mutate)
        {
            if (mutate == null)
                return;

            var target = FindById(id);
            if (target == null)
                return;

            var previousConnection = AgentCore.Editor.LLM.GatewayDiscovery.Identity(target);
            lock (Sync)
                mutate(target);
            if (previousConnection != AgentCore.Editor.LLM.GatewayDiscovery.Identity(target))
            {
                target.verifiedConnection = null;
                target.verifiedCredential = null;
                target.cacheMetricsKnown = false;
                target.cacheHitObserved = false;
                target.connectionFailed = false;
            }
            SaveAndNotify();
        }

        private void SaveAndNotify()
        {
            SafeSave();
            OnProfilesChanged?.Invoke();
        }

        /// <summary>
        /// 保存到本地文件（内容未变则不写）。IO 异常只记 Warning，不阻塞 Editor
        /// （参考 <see cref="AgentCoreSettings.SafeSave"/> 的健壮性动机）。
        /// </summary>
        internal void SafeSave()
        {
            EnsureLoaded();
            try
            {
                ProviderProfilesStorage.Save(LocalPath, ToData());
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore] Saving provider profiles to '{LocalPath}' failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 丢弃内存数据，下次访问时重新从本地文件加载。测试切换 <see cref="LocalPath"/> 后调用。
        /// </summary>
        internal void ReloadFromDisk()
        {
            lock (Sync)
                _loaded = false;
            EnsureLoaded();
        }

        /// <summary>首次访问（含域重载后）从本地文件加载；本地文件不存在时从旧版文件只读迁移并写入本地文件。</summary>
        private void EnsureLoaded()
        {
            if (_loaded)
                return;

            lock (Sync)
            {
                if (_loaded)
                    return;

                var data = ProviderProfilesStorage.LoadOrMigrate(
                    LocalPath,
                    ProviderProfilesStorage.LegacySharedPath,
                    ProviderProfilesStorage.LegacyUserStatePath,
                    ReadLegacyAsset,
                    out bool needsSave,
                    out bool migrated);
                ApplyData(data);
                _loaded = true;

                if (!needsSave)
                    return;
                try
                {
                    ProviderProfilesStorage.Save(LocalPath, ToData());
                    if (migrated)
                        AgentCoreLog.Info("[AgentCore] Migrated provider profiles to " + LocalPath);
                }
                catch (Exception ex)
                {
                    // 写失败不丢数据：内存中已有完整数据，下次保存再写
                    AgentCoreLog.Warning($"[AgentCore] Saving provider profiles to '{LocalPath}' failed: {ex.Message}");
                }
            }
        }

        private void ApplyData(ProviderProfilesLocalData data)
        {
            data ??= new ProviderProfilesLocalData();
            data.Normalize();
            schemaVersion = data.schemaVersion;
            profiles = data.profiles;
            activeProfileId = data.profiles.Exists(p => p.id == data.activeProfileId) ? data.activeProfileId : "";
            utilityProfileId = data.utilityProfileId;
        }

        private ProviderProfilesLocalData ToData()
        {
            return new ProviderProfilesLocalData
            {
                schemaVersion = schemaVersion,
                activeProfileId = activeProfileId ?? "",
                utilityProfileId = utilityProfileId ?? "",
                profiles = profiles != null ? new List<ProviderProfile>(profiles) : new List<ProviderProfile>()
            };
        }

        /// <summary>
        /// 只读读取旧版 <c>ProjectSettings/AgentCoreProviderProfiles.asset</c>：用 Unity 反序列化器载入临时对象，
        /// 取出数据后立即销毁，不保存、不修改、不删除该文件。
        /// <para>
        /// 载入会构造一个新的 <see cref="AgentCoreProviderProfiles"/>，而 <see cref="ScriptableSingleton{T}"/> 的构造函数
        /// 在单例已存在时会报错，因此载入期间暂时清空基类的 <c>s_Instance</c>，结束后还原。
        /// </para>
        /// </summary>
        internal static ProviderProfilesLocalData ReadLegacyAsset(string path)
        {
            var instanceField = typeof(ScriptableSingleton<AgentCoreProviderProfiles>)
                .GetField("s_Instance", BindingFlags.NonPublic | BindingFlags.Static);
            if (instanceField == null)
            {
                AgentCoreLog.Warning("[AgentCore] Cannot read legacy provider profiles: ScriptableSingleton layout is unknown.");
                return null;
            }

            var current = instanceField.GetValue(null);
            UnityEngine.Object[] loaded = null;
            try
            {
                instanceField.SetValue(null, null);
                loaded = InternalEditorUtility.LoadSerializedFileAndForget(path);
                if (loaded == null)
                    return null;
                foreach (var o in loaded)
                {
                    if (o is AgentCoreProviderProfiles legacy)
                        return legacy.ToData();
                }
                return null;
            }
            finally
            {
                instanceField.SetValue(null, current);
                if (loaded != null)
                {
                    foreach (var o in loaded)
                    {
                        if (o != null && !ReferenceEquals(o, current))
                            DestroyImmediate(o);
                    }
                }
            }
        }
    }
}
