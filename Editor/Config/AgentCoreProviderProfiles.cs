using System;
using System.Collections.Generic;
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
