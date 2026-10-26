using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using AgentCore.Editor.Config.Settings;
using AgentCore.Editor.Utils;
using UnityEditor;
using UnityEditorInternal;
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
    /// <para>
    /// v1.18 分层存储（见 <see cref="ProviderProfilesStorage"/>）：
    /// <list type="bullet">
    /// <item>共享文件只含团队配置（profile 列表、团队默认 active、utility profile），且只在团队配置
    /// 实际变化时写入——写前把序列化结果与磁盘内容比较，未变则跳过；经 <see cref="AtomicFile"/> 原子写入。</item>
    /// <item>运行时状态（连接验证结果、API Key 哈希、缓存命中、连接失败、最近使用时间）与个人选择
    /// （active profile、各 profile 的模型、辅助任务 profile）存于每用户的
    /// <c>UserSettings/AgentCore/ProviderProfilesState.json</c>。团队默认只经 <see cref="SetTeamDefaults"/> /
    /// <see cref="SetTeamDefaultModel"/> / <see cref="UpdateProfile"/> 等明确的团队配置操作修改。</item>
    /// <item>迁移：旧版共享文件中的运行时字段在首次加载时迁入用户状态；共享文件本身不在加载时改写，
    /// 下次团队配置变化保存时这些字段随之消失。</item>
    /// </list>
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
        // v1.14.11: 改为直连 sourceai（172.16.248.60）上的 vLLM 实例（DeepSeek-V4-Flash，
        // TP8+EP，已用 curl 验证 /v1/models 与 /v1/chat/completions 均 200）。该地址同样仅
        // 企业内网可达；vLLM 未设 --api-key，不校验鉴权头，故 API Key 占位值改为无意义的
        // "sk-xxx"（此前网关地址的真实 key 已随之失效，不再需要）。
        private const string DefaultProfileName = "Default";
        private const string DefaultProfileEndpoint = "http://172.16.248.60:8000/v1";
        private const string DefaultProfileApiKeyPlaceholder = "sk-xxx";

        /// <summary>数据 schema 版本，供将来迁移用。</summary>
        [SerializeField] private int schemaVersion = 1;

        /// <summary>已保存的 profile 列表。</summary>
        [SerializeField] private List<ProviderProfile> profiles = new();

        /// <summary>
        /// 团队默认 active profile 的 id（共享）；空 = 用 legacy config。
        /// v1.18: 个人切换写入用户状态（<see cref="ProviderProfilesUserState.activeProfileId"/>），不改此字段。
        /// </summary>
        [SerializeField] private string activeProfileId = "";

        /// <summary>
        /// v1.17.0: 辅助任务（上下文压缩 / 会话标题 / 记忆提取）使用的 profile id；空 = 跟随 active。
        /// 典型用法：主力用 Claude Opus，辅助任务用 Haiku / DeepSeek 这类便宜模型。
        /// 这是团队默认（共享）；个人切换写入用户状态（<see cref="ProviderProfilesUserState.utilityProfileId"/>）。
        /// </summary>
        [SerializeField] private string utilityProfileId = "";

        /// <summary>每用户状态（运行时字段 + 个人 active 选择），首次访问时加载并与共享数据合并。</summary>
        [NonSerialized] private ProviderProfilesUserState _userState;

        /// <summary>
        /// 保护 <see cref="ProviderProfilesUserState.modelOverrides"/>：LLM 客户端可能在后台线程经
        /// <see cref="ActiveModelConfig.ModelName"/> 读取，写入只在主线程。
        /// </summary>
        private static readonly object ModelOverrideLock = new object();

        /// <summary>增删改及切换 active 时触发，供 UI 订阅刷新。</summary>
        public static event Action OnProfilesChanged;

        /// <summary>只读 profile 列表。</summary>
        public IReadOnlyList<ProviderProfile> Profiles
        {
            get
            {
                EnsureUserState();
                return profiles;
            }
        }

        /// <summary>
        /// 当前生效的 active profile id（空 = legacy config）。
        /// 个人选择优先；个人选择指向的 profile 已不存在时回落团队默认。
        /// </summary>
        public string ActiveProfileId
        {
            get
            {
                EnsureUserState();
                var personal = _userState.activeProfileId;
                if (personal != null && (personal.Length == 0 || FindById(personal) != null))
                    return personal;
                return activeProfileId ?? "";
            }
        }

        /// <summary>团队默认 active profile id（共享文件中的值）。</summary>
        public string TeamDefaultProfileId => activeProfileId ?? "";

        /// <summary>按 id 查找 profile，找不到返回 null。</summary>
        public ProviderProfile FindById(string id)
        {
            if (string.IsNullOrEmpty(id) || profiles == null)
                return null;
            EnsureUserState();
            foreach (var p in profiles)
            {
                if (p != null && p.id == id)
                    return p;
            }
            return null;
        }

        /// <summary>
        /// 返回当前生效的 active profile（见 <see cref="ActiveProfileId"/>）；为空或找不到返回 null（legacy 模式）。
        /// </summary>
        public ProviderProfile GetActive()
            => FindById(ActiveProfileId);

        /// <summary>
        /// profile 对本用户生效的模型名：chat 面板里的个人选择（存于 UserSettings）优先，
        /// 否则为共享文件中的 <see cref="ProviderProfile.modelName"/>（团队默认）。
        /// 可在后台线程调用（只读）。
        /// </summary>
        public string GetEffectiveModelName(ProviderProfile profile)
        {
            if (profile == null)
                return null;
            EnsureUserState();
            lock (ModelOverrideLock)
                return ProviderProfilesStorage.ResolveModelName(profile, _userState);
        }

        /// <summary>
        /// 记录本用户为 profile 选择的模型（chat 面板模型下拉）。这是个人选择：只写
        /// <c>UserSettings/AgentCore/ProviderProfilesState.json</c>，不改共享文件；
        /// 选择与团队默认相同时清除个人覆盖。修改团队默认模型请用 <see cref="SetTeamDefaultModel"/>。
        /// </summary>
        public void SetUserModel(string profileId, string model)
        {
            var target = FindById(profileId);
            if (target == null)
                return;

            bool changed;
            lock (ModelOverrideLock)
                changed = ProviderProfilesStorage.SetModelOverride(_userState, target, model);
            if (!changed)
                return;

            target.lastUsedAtUnixMs = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            SaveAndNotify();
        }

        /// <summary>
        /// 修改 profile 的团队默认模型（共享 <see cref="ProviderProfile.modelName"/>，写入
        /// <c>ProjectSettings/AgentCoreProviderProfiles.asset</c>，提交后影响全组）。
        /// 只用于用户明确要改团队默认的操作；同时清除本用户对该 profile 的个人模型选择。
        /// </summary>
        public void SetTeamDefaultModel(string profileId, string model)
        {
            model = model?.Trim() ?? "";
            UpdateProfile(profileId, p => p.modelName = model);
        }

        /// <summary>
        /// 本用户生效的辅助任务 profile id（空 = 跟随 active）。个人选择优先；
        /// 个人选择指向的 profile 已不存在时回落团队默认。
        /// </summary>
        public string UtilityProfileId
        {
            get
            {
                EnsureUserState();
                return ProviderProfilesStorage.ResolveUtilityProfileId(utilityProfileId, _userState, id => FindById(id) != null);
            }
        }

        /// <summary>团队默认辅助任务 profile id（共享文件中的值，空 = 跟随 active）。</summary>
        public string TeamDefaultUtilityProfileId => utilityProfileId ?? "";

        /// <summary>辅助任务 profile；未设置或已被删除返回 null（调用方应回落 active）。</summary>
        public ProviderProfile GetUtility()
            => FindById(UtilityProfileId);

        /// <summary>
        /// 设置本用户的辅助任务 profile（空 = 跟随 active）。个人选择：只写用户状态，不改共享文件；
        /// 与团队默认相同时清除个人覆盖。修改团队默认请用 <see cref="SetTeamDefaults"/>。
        /// </summary>
        public void SetUtility(string id)
        {
            EnsureUserState();
            if (!ProviderProfilesStorage.SetUtilityOverride(_userState, utilityProfileId, id))
                return;
            SaveAndNotify();
        }

        /// <summary>本用户当前的 active / 辅助模型选择是否与团队默认不同。</summary>
        public bool HasPersonalSelection
        {
            get
            {
                EnsureUserState();
                return ActiveProfileId != TeamDefaultProfileId || UtilityProfileId != TeamDefaultUtilityProfileId;
            }
        }

        /// <summary>
        /// 把团队默认 active / 辅助任务 profile 改为指定值（写入共享文件，提交后影响全组），
        /// 并清除本用户对应的个人选择。只用于用户明确要改团队默认的操作（设置页"设为团队默认"）。
        /// 指向不存在 profile 的 id 视为空。
        /// </summary>
        public void SetTeamDefaults(string activeId, string utilityId)
        {
            EnsureUserState();
            activeProfileId = FindById(activeId) != null ? activeId : "";
            utilityProfileId = FindById(utilityId) != null ? utilityId : "";
            _userState.activeProfileId = null;
            _userState.utilityProfileId = null;
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

            EnsureUserState();
            _userState.profiles.Remove(id);
            lock (ModelOverrideLock)
                _userState.modelOverrides?.Remove(id);
            if (_userState.activeProfileId == id)
                _userState.activeProfileId = null;
            if (_userState.utilityProfileId == id)
                _userState.utilityProfileId = null;

            if (activeProfileId == id)
                activeProfileId = "";
            if (utilityProfileId == id)
                utilityProfileId = "";

            SaveAndNotify();
        }

        /// <summary>
        /// 设置 active profile。<paramref name="id"/> 为空表示回落 legacy config。
        /// 若指向一个存在的 profile，则更新其 lastUsedAtUnixMs。
        /// <para>
        /// v1.18: 这是个人选择，写入用户状态，不改共享文件。例外：团队默认为空或指向已删除的 profile 时，
        /// 本次选择同时成为团队默认（新项目首次配置 / 首个 profile），保证团队成员拉取后有可用的默认。
        /// 选择与团队默认相同时清除个人覆盖，继续跟随团队默认。
        /// </para>
        /// </summary>
        public void SetActive(string id)
        {
            EnsureUserState();
            id ??= "";

            var target = FindById(id);
            bool teamDefaultMissing = string.IsNullOrEmpty(activeProfileId) || FindById(activeProfileId) == null;
            if (target != null && teamDefaultMissing)
                activeProfileId = id;

            _userState.activeProfileId = id == (activeProfileId ?? "") ? null : id;

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

            var previousConnection = AgentCore.Editor.LLM.GatewayDiscovery.Identity(target);
            var previousModel = target.modelName;
            mutate(target);
            if (!string.Equals(previousModel, target.modelName, StringComparison.Ordinal))
            {
                // 共享 modelName 被显式修改（设置页 / 自动填充首个模型）：清除本用户对该 profile 的个人模型选择，
                // 让这次修改对自己立即生效，而不是被旧的个人选择遮住。
                EnsureUserState();
                lock (ModelOverrideLock)
                    _userState.modelOverrides?.Remove(id);
            }
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
        /// 保存：运行时状态 → 用户状态文件；团队配置 → <c>ProjectSettings/AgentCoreProviderProfiles.asset</c>。
        /// 两者都先与磁盘内容比较，未变则不写。IO 异常只记 Warning，不阻塞 Editor
        /// （参考 <see cref="AgentCoreSettings.SafeSave"/> 的健壮性动机）。
        /// </summary>
        internal void SafeSave()
        {
            EnsureUserState();

            try
            {
                ProviderProfilesStorage.CaptureAll(profiles, _userState);
                SaveUserState();
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore] Saving provider profile user state failed: {ex.Message}");
            }

            try
            {
                SaveSharedIfChanged();
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore] AgentCoreProviderProfiles.Save failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 首次访问时加载用户状态并与共享数据合并（域重载后 <see cref="_userState"/> 为空，会重新加载）。
        /// 旧版共享文件里的运行时字段迁入用户状态，只写用户状态文件，不改共享文件。
        /// </summary>
        private void EnsureUserState()
        {
            if (_userState != null)
                return;

            _userState = LoadUserState();
            profiles ??= new List<ProviderProfile>();
            if (ProviderProfilesStorage.MergeOnLoad(profiles, _userState))
            {
                try
                {
                    SaveUserState();
                    AgentCoreLog.Info("[AgentCore] Migrated provider profile runtime state to " + ProviderProfilesStorage.UserStatePath);
                }
                catch (Exception ex)
                {
                    // 写失败不丢数据：运行时字段仍在内存中，下次保存再写
                    AgentCoreLog.Warning($"[AgentCore] Saving migrated provider profile user state failed: {ex.Message}");
                }
            }
        }

        private static ProviderProfilesUserState LoadUserState()
        {
            var path = ProviderProfilesStorage.UserStatePath;
            try
            {
                if (!File.Exists(path))
                    return new ProviderProfilesUserState();
                return ProviderProfilesUserState.FromJson(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                // 文件损坏：保留一份副本供排查，再按空状态继续（运行时状态可重新获得，团队配置不受影响）
                AgentCoreLog.Warning($"[AgentCore] Provider profile user state '{path}' is unreadable ({ex.Message}); a copy is kept as '{path}.corrupt'.");
                try
                {
                    File.Copy(path, path + ".corrupt", overwrite: true);
                }
                catch
                {
                    // 副本失败不影响继续运行
                }
                return new ProviderProfilesUserState();
            }
        }

        private void SaveUserState()
        {
            ProviderProfilesStorage.WriteTextIfChanged(ProviderProfilesStorage.UserStatePath, _userState.ToJson());
        }

        /// <summary>
        /// 用 Unity 自身的序列化器生成共享文件内容（与 <c>ScriptableSingleton.Save</c> 格式一致），
        /// 序列化前临时清空运行时字段，再删去对应行，最后与磁盘内容比较、原子写入。
        /// </summary>
        private void SaveSharedIfChanged()
        {
            var path = GetFilePath();
            if (string.IsNullOrEmpty(path))
                return;

            var stash = new List<KeyValuePair<ProviderProfile, ProviderProfileRuntimeState>>();
            if (profiles != null)
            {
                foreach (var p in profiles)
                {
                    if (p == null)
                        continue;
                    stash.Add(new KeyValuePair<ProviderProfile, ProviderProfileRuntimeState>(p, ProviderProfileRuntimeState.Capture(p)));
                    ProviderProfileRuntimeState.Clear(p);
                }
            }

            Directory.CreateDirectory(ProviderProfilesStorage.TempDirectory);
            var tempPath = Path.Combine(ProviderProfilesStorage.TempDirectory,
                "AgentCoreProviderProfiles." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".asset");
            string yaml;
            try
            {
                InternalEditorUtility.SaveToSerializedFileAndForget(new UnityEngine.Object[] { this }, tempPath, true);
                yaml = File.ReadAllText(tempPath);
            }
            finally
            {
                foreach (var entry in stash)
                    entry.Value.ApplyTo(entry.Key);
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch
                {
                    // 临时文件清理失败不影响结果
                }
            }

            ProviderProfilesStorage.WriteSharedIfChanged(
                path,
                ProviderProfilesStorage.StripVolatileFields(yaml),
                ProviderProfilesStorage.SharedBackupPath);
        }
    }
}
