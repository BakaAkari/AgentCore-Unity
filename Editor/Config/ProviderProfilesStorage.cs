using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using AgentCore.Editor.Utils;
using Newtonsoft.Json;

namespace AgentCore.Editor.Config
{
    /// <summary>
    /// 单个 profile 的运行时 / 用户级状态（v1.18）。
    /// <para>
    /// 这些字段仍挂在 <see cref="ProviderProfile"/> 上供运行时读写（<c>GatewayDiscovery</c> 等），
    /// 但持久化位置是每用户的 <see cref="ProviderProfilesStorage.UserStatePath"/>，不再写入团队共享的
    /// <c>ProjectSettings/AgentCoreProviderProfiles.asset</c>——它们随每次调用变化（验证结果、缓存命中、
    /// 连接失败、最近使用时间），且 <see cref="verifiedCredential"/> 是 API Key 的哈希，不应进版本库。
    /// </para>
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

        /// <summary>所有字段均为默认值。</summary>
        [JsonIgnore]
        public bool IsEmpty =>
            string.IsNullOrEmpty(verifiedConnection) && string.IsNullOrEmpty(verifiedFormat)
            && string.IsNullOrEmpty(verifiedAuth) && string.IsNullOrEmpty(verifiedCredential)
            && !connectionFailed && !cacheMetricsKnown && !cacheHitObserved && lastUsedAtUnixMs == 0;

        public static ProviderProfileRuntimeState Capture(ProviderProfile p)
        {
            return new ProviderProfileRuntimeState
            {
                verifiedConnection = NullIfEmpty(p.verifiedConnection),
                verifiedFormat = NullIfEmpty(p.verifiedFormat),
                verifiedAuth = NullIfEmpty(p.verifiedAuth),
                verifiedCredential = NullIfEmpty(p.verifiedCredential),
                connectionFailed = p.connectionFailed,
                cacheMetricsKnown = p.cacheMetricsKnown,
                cacheHitObserved = p.cacheHitObserved,
                lastUsedAtUnixMs = p.lastUsedAtUnixMs
            };
        }

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

        /// <summary>把 profile 上的运行时字段清为默认值（序列化共享文件前调用）。</summary>
        public static void Clear(ProviderProfile p)
            => new ProviderProfileRuntimeState().ApplyTo(p);

        private static string NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;
    }

    /// <summary>
    /// 每用户的 Provider Profile 状态文件内容（<c>UserSettings/AgentCore/ProviderProfilesState.json</c>）。
    /// </summary>
    internal sealed class ProviderProfilesUserState
    {
        public int schemaVersion = 1;

        /// <summary>
        /// 本用户选择的 active profile；null = 跟随团队默认（共享文件中的 activeProfileId）。
        /// 在 chat 面板 / 设置页切换模型只改这里，不改共享文件。
        /// </summary>
        public string activeProfileId;

        /// <summary>按 profile id 保存的运行时状态。有条目即表示该 profile 以本文件为准（即便全为默认值）。</summary>
        public SortedDictionary<string, ProviderProfileRuntimeState> profiles = new(StringComparer.Ordinal);

        /// <summary>
        /// 本用户在 chat 面板模型下拉里选择的模型（profile id → 模型名）；无条目 = 跟随共享文件中该 profile 的
        /// <see cref="ProviderProfile.modelName"/>（团队默认）。与 <see cref="activeProfileId"/> 一样是个人选择，
        /// 切换模型只改这里，不改共享文件。旧版本把选择直接写进共享 modelName，读取时自然作为默认值保留。
        /// </summary>
        public SortedDictionary<string, string> modelOverrides = new(StringComparer.Ordinal);

        /// <summary>无个人模型选择时不写出该字段，避免旧状态文件仅因多出空字段而改动。</summary>
        public bool ShouldSerializemodelOverrides() => modelOverrides != null && modelOverrides.Count > 0;

        /// <summary>
        /// 本用户选择的辅助任务 profile；null = 跟随团队默认（共享文件中的 utilityProfileId），
        /// "" = 本用户选择"跟随 active"（即便团队默认指定了某个 profile）。设置页切换只改这里，不改共享文件。
        /// </summary>
        public string utilityProfileId;

        /// <summary>未做个人辅助模型选择时不写出该字段，避免旧状态文件改动。</summary>
        public bool ShouldSerializeutilityProfileId() => utilityProfileId != null;

        public string ToJson()
            => JsonConvert.SerializeObject(this, Formatting.Indented).Replace("\r\n", "\n") + "\n";

        /// <summary>解析 JSON；内容为空返回空状态，格式错误抛 <see cref="JsonException"/>。</summary>
        public static ProviderProfilesUserState FromJson(string json)
        {
            var state = string.IsNullOrWhiteSpace(json)
                ? null
                : JsonConvert.DeserializeObject<ProviderProfilesUserState>(json);
            state ??= new ProviderProfilesUserState();
            state.profiles = state.profiles == null
                ? new SortedDictionary<string, ProviderProfileRuntimeState>(StringComparer.Ordinal)
                : new SortedDictionary<string, ProviderProfileRuntimeState>(state.profiles, StringComparer.Ordinal);
            var overrides = new SortedDictionary<string, string>(StringComparer.Ordinal);
            if (state.modelOverrides != null)
            {
                foreach (var pair in state.modelOverrides)
                {
                    if (!string.IsNullOrEmpty(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                        overrides[pair.Key] = pair.Value;
                }
            }
            state.modelOverrides = overrides;
            return state;
        }
    }

    /// <summary>
    /// Provider Profile 分层存储的纯逻辑（v1.18），供 <see cref="AgentCoreProviderProfiles"/> 使用并可单测。
    /// <list type="bullet">
    /// <item><c>ProjectSettings/AgentCoreProviderProfiles.asset</c>：团队共享（进 git），只含团队配置。</item>
    /// <item><see cref="UserStatePath"/>：每用户（不进 git），含运行时状态与个人 active 选择。</item>
    /// <item><see cref="SharedBackupPath"/>：共享文件被替换前的上一版（损坏恢复用）。</item>
    /// </list>
    /// </summary>
    internal static class ProviderProfilesStorage
    {
        /// <summary>每用户状态文件（相对项目根目录）。</summary>
        public const string UserStatePath = "UserSettings/AgentCore/ProviderProfilesState.json";

        /// <summary>共享文件写入前的备份（相对项目根目录）。</summary>
        public const string SharedBackupPath = "Library/AgentCore/AgentCoreProviderProfiles.asset.bak";

        /// <summary>序列化共享文件时使用的临时目录（相对项目根目录）。</summary>
        public const string TempDirectory = "Temp/AgentCore";

        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        /// <summary>不应出现在共享文件中的 <see cref="ProviderProfile"/> 字段（与 <see cref="ProviderProfileRuntimeState"/> 一一对应）。</summary>
        public static readonly HashSet<string> VolatileFieldNames = new(StringComparer.Ordinal)
        {
            nameof(ProviderProfile.verifiedConnection),
            nameof(ProviderProfile.verifiedFormat),
            nameof(ProviderProfile.verifiedAuth),
            nameof(ProviderProfile.verifiedCredential),
            nameof(ProviderProfile.connectionFailed),
            nameof(ProviderProfile.cacheMetricsKnown),
            nameof(ProviderProfile.cacheHitObserved),
            nameof(ProviderProfile.lastUsedAtUnixMs),
        };

        private static readonly Regex KeyLine = new(@"^( *)([A-Za-z_][A-Za-z0-9_]*):", RegexOptions.CultureInvariant);

        /// <summary>
        /// 从 Unity YAML 文本中删除运行时字段所在的行（含多行标量的续行）。
        /// 旧版文件中 <c>verifiedConnection</c> 含换行，Unity 会写成跨行的引号标量：
        /// 续行缩进大于键的缩进，或为空行。
        /// </summary>
        public static string StripVolatileFields(string yaml)
        {
            if (string.IsNullOrEmpty(yaml))
                return yaml ?? string.Empty;

            var lines = yaml.Split('\n');
            var sb = new StringBuilder(yaml.Length);
            int skipIndent = -1;
            int last = lines.Length - 1;
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var content = line.TrimEnd('\r');

                if (skipIndent >= 0)
                {
                    bool blank = content.Trim().Length == 0;
                    if ((blank && i != last) || (!blank && Indent(content) > skipIndent))
                        continue;
                    skipIndent = -1;
                }

                var m = KeyLine.Match(content);
                if (m.Success && VolatileFieldNames.Contains(m.Groups[2].Value))
                {
                    skipIndent = m.Groups[1].Length;
                    continue;
                }

                sb.Append(line);
                if (i != last)
                    sb.Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>比较用的规范化：统一换行并去掉运行时字段。</summary>
        public static string NormalizeShared(string yaml)
            => StripVolatileFields((yaml ?? string.Empty).Replace("\r\n", "\n"));

        /// <summary>
        /// 共享文件写入：新内容与磁盘内容按 <see cref="NormalizeShared"/> 比较，团队配置没有变化则跳过
        /// （包括旧版文件仅多出运行时字段的情况——不会因此产生 VCS 改动）。
        /// </summary>
        /// <returns>true 表示实际写入。</returns>
        public static bool WriteSharedIfChanged(string path, string yaml, string backupPath)
        {
            if (File.Exists(path))
            {
                var existingBytes = File.ReadAllBytes(path);
                var newBytes = Utf8NoBom.GetBytes(yaml);
                if (BytesEqual(existingBytes, newBytes))
                    return false;

                var existing = File.ReadAllText(path);
                if (string.Equals(NormalizeShared(existing), NormalizeShared(yaml), StringComparison.Ordinal))
                    return false;
            }

            AtomicFile.WriteAllText(path, yaml, Utf8NoBom, File.Exists(path) ? backupPath : null);
            return true;
        }

        /// <summary>按字节比较，内容未变则跳过写入。</summary>
        /// <returns>true 表示实际写入。</returns>
        public static bool WriteTextIfChanged(string path, string text, string backupPath = null)
        {
            var newBytes = Utf8NoBom.GetBytes(text ?? string.Empty);
            if (File.Exists(path) && BytesEqual(File.ReadAllBytes(path), newBytes))
                return false;

            AtomicFile.WriteAllText(path, text, Utf8NoBom, File.Exists(path) ? backupPath : null);
            return true;
        }

        /// <summary>
        /// 加载时合并：用户状态中已有条目的 profile，以用户状态覆盖其运行时字段；
        /// 没有条目、但共享文件中带有旧版运行时字段的 profile，把这些值迁入用户状态（不丢数据）。
        /// </summary>
        /// <returns>true 表示有旧数据被迁入，调用方应保存用户状态。</returns>
        public static bool MergeOnLoad(IEnumerable<ProviderProfile> profiles, ProviderProfilesUserState state)
        {
            bool imported = false;
            if (profiles == null || state == null)
                return false;

            foreach (var p in profiles)
            {
                if (p == null || string.IsNullOrEmpty(p.id))
                    continue;

                if (state.profiles.TryGetValue(p.id, out var saved) && saved != null)
                {
                    saved.ApplyTo(p);
                    continue;
                }

                var legacy = ProviderProfileRuntimeState.Capture(p);
                if (!legacy.IsEmpty)
                {
                    state.profiles[p.id] = legacy;
                    imported = true;
                }
            }
            return imported;
        }

        /// <summary>
        /// 解析 profile 对本用户生效的模型名：个人选择优先，否则为共享文件中的 <see cref="ProviderProfile.modelName"/>。
        /// </summary>
        public static string ResolveModelName(ProviderProfile profile, ProviderProfilesUserState state)
        {
            if (profile == null)
                return null;
            if (state?.modelOverrides != null && !string.IsNullOrEmpty(profile.id)
                && state.modelOverrides.TryGetValue(profile.id, out var personal)
                && !string.IsNullOrWhiteSpace(personal))
                return personal;
            return profile.modelName;
        }

        /// <summary>
        /// 记录本用户对 profile 的模型选择。选择与团队默认相同（或为空）时清除个人覆盖，继续跟随团队默认。
        /// </summary>
        /// <returns>true 表示用户状态有变化。</returns>
        public static bool SetModelOverride(ProviderProfilesUserState state, ProviderProfile profile, string model)
        {
            if (state == null || profile == null || string.IsNullOrEmpty(profile.id))
                return false;
            state.modelOverrides ??= new SortedDictionary<string, string>(StringComparer.Ordinal);

            model = model?.Trim();
            if (string.IsNullOrEmpty(model) || string.Equals(model, profile.modelName, StringComparison.Ordinal))
                return state.modelOverrides.Remove(profile.id);

            if (state.modelOverrides.TryGetValue(profile.id, out var existing)
                && string.Equals(existing, model, StringComparison.Ordinal))
                return false;
            state.modelOverrides[profile.id] = model;
            return true;
        }

        /// <summary>
        /// 解析本用户生效的辅助任务 profile id：个人选择优先（"" 表示跟随 active）；
        /// 个人选择指向的 profile 已不存在时回落团队默认。
        /// </summary>
        public static string ResolveUtilityProfileId(string teamDefaultId, ProviderProfilesUserState state, Func<string, bool> profileExists)
        {
            var personal = state?.utilityProfileId;
            if (personal != null && (personal.Length == 0 || (profileExists != null && profileExists(personal))))
                return personal;
            return teamDefaultId ?? "";
        }

        /// <summary>
        /// 记录本用户的辅助任务 profile 选择（"" = 跟随 active）。与团队默认相同时清除个人覆盖，继续跟随团队默认。
        /// </summary>
        /// <returns>true 表示用户状态有变化。</returns>
        public static bool SetUtilityOverride(ProviderProfilesUserState state, string teamDefaultId, string id)
        {
            if (state == null)
                return false;
            id ??= "";
            var next = string.Equals(id, teamDefaultId ?? "", StringComparison.Ordinal) ? null : id;
            if (string.Equals(state.utilityProfileId, next, StringComparison.Ordinal))
                return false;
            state.utilityProfileId = next;
            return true;
        }

        /// <summary>把当前 profile 上的运行时字段写入用户状态（每个 profile 都保留条目）。</summary>
        public static void CaptureAll(IEnumerable<ProviderProfile> profiles, ProviderProfilesUserState state)
        {
            if (profiles == null || state == null)
                return;
            foreach (var p in profiles)
            {
                if (p == null || string.IsNullOrEmpty(p.id))
                    continue;
                state.profiles[p.id] = ProviderProfileRuntimeState.Capture(p);
            }
        }

        private static int Indent(string line)
        {
            int n = 0;
            while (n < line.Length && line[n] == ' ')
                n++;
            return n;
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
