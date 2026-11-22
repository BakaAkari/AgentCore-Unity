using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AgentCore.Editor.Utils;
using Newtonsoft.Json;

namespace AgentCore.Editor.Config
{
    /// <summary>
    /// 本地 Provider Profile 文件内容（<see cref="ProviderProfilesStorage.LocalProfilesPath"/>）：
    /// profile 列表（含运行时验证 / 缓存字段）、active 与辅助任务 profile。仅本机个人使用，不进版本库。
    /// </summary>
    internal sealed class ProviderProfilesLocalData
    {
        public int schemaVersion = 1;

        /// <summary>active profile id；空 = 无 active profile。</summary>
        public string activeProfileId = "";

        /// <summary>辅助任务 profile id；空 = 跟随 active。</summary>
        public string utilityProfileId = "";

        public List<ProviderProfile> profiles = new();

        /// <summary>UTF-8 文本，LF 换行并以换行结尾；同一内容序列化结果稳定。</summary>
        public string ToJson()
            => JsonConvert.SerializeObject(this, Formatting.Indented).Replace("\r\n", "\n") + "\n";

        /// <summary>解析 JSON；内容为空返回空数据，格式错误抛 <see cref="JsonException"/>。</summary>
        public static ProviderProfilesLocalData FromJson(string json)
        {
            var data = string.IsNullOrWhiteSpace(json)
                ? null
                : JsonConvert.DeserializeObject<ProviderProfilesLocalData>(json);
            data ??= new ProviderProfilesLocalData();
            data.Normalize();
            return data;
        }

        /// <summary>补齐 null 字段并去掉无效条目。</summary>
        public void Normalize()
        {
            activeProfileId ??= "";
            utilityProfileId ??= "";
            profiles ??= new List<ProviderProfile>();
            profiles.RemoveAll(p => p == null || string.IsNullOrEmpty(p.id));
            foreach (var p in profiles)
                p.fallbackProfileIds ??= new List<string>();
        }
    }

    /// <summary>
    /// 旧版（v1.18–v1.20）单个 profile 的运行时状态，仅用于读取 <see cref="ProviderProfilesStorage.LegacyUserStatePath"/> 迁移。
    /// </summary>
    [Serializable]
    internal sealed class ProviderProfileRuntimeState
    {
        public string verifiedConnection;
        public string verifiedFormat;
        public string verifiedAuth;
        public string verifiedCredential;
        public bool connectionFailed;
        public bool cacheMetricsKnown;
        public bool cacheHitObserved;
        public long lastUsedAtUnixMs;

        public void ApplyTo(ProviderProfile p)
        {
            p.verifiedConnection = verifiedConnection;
            p.verifiedFormat = verifiedFormat;
            p.verifiedAuth = verifiedAuth;
            p.verifiedCredential = verifiedCredential;
            p.connectionFailed = connectionFailed;
            p.cacheMetricsKnown = cacheMetricsKnown;
            p.cacheHitObserved = cacheHitObserved;
            p.lastUsedAtUnixMs = lastUsedAtUnixMs;
        }
    }

    /// <summary>
    /// 旧版（v1.18–v1.20）每用户状态文件内容，仅用于迁移读取。
    /// <c>activeProfileId</c> / <c>utilityProfileId</c> 为 null 表示跟随旧共享文件中的值；
    /// <c>utilityProfileId</c> 为 "" 表示跟随 active。
    /// </summary>
    internal sealed class ProviderProfilesUserState
    {
        public string activeProfileId;
        public string utilityProfileId;
        public Dictionary<string, ProviderProfileRuntimeState> profiles = new(StringComparer.Ordinal);
        public Dictionary<string, string> modelOverrides = new(StringComparer.Ordinal);

        /// <summary>解析 JSON；内容为空返回空状态，格式错误抛 <see cref="JsonException"/>。</summary>
        public static ProviderProfilesUserState FromJson(string json)
        {
            var state = string.IsNullOrWhiteSpace(json)
                ? null
                : JsonConvert.DeserializeObject<ProviderProfilesUserState>(json);
            state ??= new ProviderProfilesUserState();
            state.profiles ??= new Dictionary<string, ProviderProfileRuntimeState>(StringComparer.Ordinal);
            state.modelOverrides ??= new Dictionary<string, string>(StringComparer.Ordinal);
            return state;
        }
    }

    /// <summary>
    /// Provider Profile 本地存储的纯逻辑，供 <see cref="AgentCoreProviderProfiles"/> 使用并可单测（所有路径可由调用方传入）。
    /// <list type="bullet">
    /// <item><see cref="LocalProfilesPath"/>：唯一的读写位置，原子写入，上一版保留为 <c>.bak</c> 供损坏恢复。</item>
    /// <item><see cref="LegacySharedPath"/> / <see cref="LegacyUserStatePath"/>：旧版文件，只在本地文件不存在时只读迁移一次，从不修改或删除。</item>
    /// </list>
    /// </summary>
    internal static class ProviderProfilesStorage
    {
        /// <summary>本地 profile 文件（相对项目根目录）。</summary>
        public const string LocalProfilesPath = "UserSettings/AgentCore/ProviderProfiles.json";

        /// <summary>旧版团队共享文件（只读迁移来源）。</summary>
        public const string LegacySharedPath = "ProjectSettings/AgentCoreProviderProfiles.asset";

        /// <summary>旧版每用户状态文件（只读迁移来源）。</summary>
        public const string LegacyUserStatePath = "UserSettings/AgentCore/ProviderProfilesState.json";

        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        /// <summary><paramref name="path"/> 的上一版备份路径。</summary>
        public static string BackupPathFor(string path) => path + ".bak";

        /// <summary><paramref name="path"/> 损坏时保留的副本路径。</summary>
        public static string CorruptPathFor(string path) => path + ".corrupt";

        /// <summary>按字节比较，内容未变则跳过写入；写入为原子替换（UTF-8 无 BOM）。</summary>
        /// <returns>true 表示实际写入。</returns>
        public static bool WriteTextIfChanged(string path, string text, string backupPath = null)
        {
            var newBytes = Utf8NoBom.GetBytes(text ?? string.Empty);
            if (File.Exists(path) && BytesEqual(File.ReadAllBytes(path), newBytes))
                return false;

            AtomicFile.WriteAllText(path, text, Utf8NoBom, File.Exists(path) ? backupPath : null);
            return true;
        }

        /// <summary>保存本地文件，被替换的旧内容保留为 <see cref="BackupPathFor"/>。</summary>
        /// <returns>true 表示实际写入。</returns>
        public static bool Save(string path, ProviderProfilesLocalData data)
            => WriteTextIfChanged(path, (data ?? new ProviderProfilesLocalData()).ToJson(), BackupPathFor(path));

        /// <summary>
        /// 读取本地文件。文件不存在返回 null。内容损坏时先把原文件复制为 <see cref="CorruptPathFor"/>，
        /// 再尝试上一版 <see cref="BackupPathFor"/>；备份也不可用则返回 null。
        /// </summary>
        /// <param name="recovered">true 表示数据来自备份（调用方应重新保存）。</param>
        public static ProviderProfilesLocalData Load(string path, out bool recovered)
        {
            recovered = false;
            if (!File.Exists(path))
                return null;

            try
            {
                return ProviderProfilesLocalData.FromJson(File.ReadAllText(path, Utf8NoBom));
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore] Provider profiles '{path}' is unreadable ({ex.Message}); a copy is kept as '{CorruptPathFor(path)}'.");
                try
                {
                    File.Copy(path, CorruptPathFor(path), overwrite: true);
                }
                catch
                {
                    // 副本失败不影响继续恢复
                }
            }

            var backup = BackupPathFor(path);
            if (!File.Exists(backup))
                return null;
            try
            {
                var data = ProviderProfilesLocalData.FromJson(File.ReadAllText(backup, Utf8NoBom));
                recovered = true;
                // 立即把已验证的备份还原到主路径，避免后续 Save() 把损坏的主文件覆盖写进 .bak。
                try
                {
                    File.Copy(backup, path, overwrite: true);
                }
                catch
                {
                    // 还原主文件失败不影响从内存恢复；后续 Save 会重试
                }
                AgentCoreLog.Warning($"[AgentCore] Provider profiles restored from '{backup}'.");
                return data;
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore] Provider profiles backup '{backup}' is unreadable ({ex.Message}).");
                return null;
            }
        }

        /// <summary>
        /// 加载本地 profile；本地文件不存在（或损坏且无可用备份）时，从旧版文件只读迁移。
        /// 不写任何文件——<paramref name="needsSave"/> 为 true 时由调用方保存到 <paramref name="localPath"/>。
        /// </summary>
        /// <param name="readLegacyShared">读取旧版共享 asset 的方法（Unity 反序列化；测试可替换）。</param>
        public static ProviderProfilesLocalData LoadOrMigrate(
            string localPath,
            string legacySharedPath,
            string legacyUserStatePath,
            Func<string, ProviderProfilesLocalData> readLegacyShared,
            out bool needsSave,
            out bool migrated)
        {
            migrated = false;
            var local = Load(localPath, out needsSave);
            if (local != null)
                return local;

            ProviderProfilesLocalData shared = null;
            if (!string.IsNullOrEmpty(legacySharedPath) && File.Exists(legacySharedPath) && readLegacyShared != null)
            {
                try
                {
                    shared = readLegacyShared(legacySharedPath);
                }
                catch (Exception ex)
                {
                    AgentCoreLog.Warning($"[AgentCore] Reading legacy provider profiles '{legacySharedPath}' failed: {ex.Message}");
                }
            }

            ProviderProfilesUserState state = null;
            if (!string.IsNullOrEmpty(legacyUserStatePath) && File.Exists(legacyUserStatePath))
            {
                try
                {
                    state = ProviderProfilesUserState.FromJson(File.ReadAllText(legacyUserStatePath, Utf8NoBom));
                }
                catch (Exception ex)
                {
                    AgentCoreLog.Warning($"[AgentCore] Reading legacy provider profile state '{legacyUserStatePath}' failed: {ex.Message}");
                }
            }

            if (shared == null)
                return new ProviderProfilesLocalData();

            migrated = true;
            needsSave = true;
            return MergeLegacy(shared, state);
        }

        /// <summary>
        /// 把旧版每用户状态合并进旧版共享数据：个人 active / 辅助任务选择（指向存在的 profile 时）、
        /// 个人模型选择写入对应 profile 的 <see cref="ProviderProfile.modelName"/>、运行时状态覆盖 profile 上的旧值。
        /// </summary>
        public static ProviderProfilesLocalData MergeLegacy(ProviderProfilesLocalData shared, ProviderProfilesUserState state)
        {
            var data = shared ?? new ProviderProfilesLocalData();
            data.Normalize();
            if (state == null)
                return data;

            bool Exists(string id) => data.profiles.Exists(p => p.id == id);

            var personalActive = state.activeProfileId;
            if (personalActive != null && (personalActive.Length == 0 || Exists(personalActive)))
                data.activeProfileId = personalActive;

            var personalUtility = state.utilityProfileId;
            if (personalUtility != null && (personalUtility.Length == 0 || Exists(personalUtility)))
                data.utilityProfileId = personalUtility;

            foreach (var p in data.profiles)
            {
                if (state.modelOverrides != null && state.modelOverrides.TryGetValue(p.id, out var model)
                    && !string.IsNullOrWhiteSpace(model))
                    p.modelName = model.Trim();
                if (state.profiles != null && state.profiles.TryGetValue(p.id, out var runtime) && runtime != null)
                    runtime.ApplyTo(p);
            }
            return data;
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                    return false;
            }
            return true;
        }
    }
}
