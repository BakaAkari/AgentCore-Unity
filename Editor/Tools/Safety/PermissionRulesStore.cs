using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AgentCore.Editor.Utils;

namespace AgentCore.Editor.Tools.Safety
{
    /// <summary>
    /// 权限规则的文件存储（P2）。
    /// <list type="bullet">
    ///   <item><description><see cref="UserRelativePath"/>：每用户规则 + 本地停用的项目规则 id（不进版本库）。</description></item>
    ///   <item><description><see cref="ProjectRelativePath"/>：团队共享默认规则（进版本库）。</description></item>
    /// </list>
    /// <para>
    /// 与 Provider Profile 分层存储一致：ProjectSettings 放团队配置，UserSettings 放个人配置；
    /// 只在显式修改时经 <see cref="AtomicFile"/> 写盘，被替换的旧内容备份到 <c>Library/AgentCore</c>。
    /// 读取按文件时间戳 / 大小缓存，外部手改文件后下一次工具调用即生效。
    /// 文件缺失 / 损坏一律视为"无规则"——绝不因配置问题拦截工具调用。
    /// </para>
    /// </summary>
    public sealed class PermissionRulesStore : IPermissionRuleSource
    {
        /// <summary>每用户规则文件（相对项目根目录）。</summary>
        public const string UserRelativePath = "UserSettings/AgentCore/PermissionRules.json";

        /// <summary>项目共享规则文件（相对项目根目录）。</summary>
        public const string ProjectRelativePath = "ProjectSettings/AgentCore/PermissionRules.json";

        private const string UserBackupRelativePath = "Library/AgentCore/PermissionRules.user.json.bak";
        private const string ProjectBackupRelativePath = "Library/AgentCore/PermissionRules.project.json.bak";

        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        private static readonly object DefaultLock = new object();
        private static PermissionRulesStore _default;

        /// <summary>当前 Unity 项目的规则存储（懒加载）。</summary>
        public static PermissionRulesStore Default
        {
            get
            {
                lock (DefaultLock)
                {
                    return _default ??= new PermissionRulesStore(SafePath.ProjectRoot);
                }
            }
        }

        /// <summary>规则经本类修改并保存后触发。</summary>
        public static event Action Changed;

        private readonly object _lock = new object();
        private readonly string _userPath;
        private readonly string _projectPath;
        private readonly string _userBackupPath;
        private readonly string _projectBackupPath;

        private PermissionRulesDocument _user = new PermissionRulesDocument();
        private PermissionRulesDocument _project = new PermissionRulesDocument();
        private FileStamp _userStamp;
        private FileStamp _projectStamp;
        private bool _loadedOnce;

        /// <summary>最近一次加载用户文件的错误（null 表示正常）。</summary>
        public string UserLoadError { get; private set; }

        /// <summary>最近一次加载项目文件的错误（null 表示正常）。</summary>
        public string ProjectLoadError { get; private set; }

        /// <summary>用户规则文件绝对路径。</summary>
        public string UserFilePath => _userPath;

        /// <summary>项目规则文件绝对路径。</summary>
        public string ProjectFilePath => _projectPath;

        /// <param name="projectRoot">项目根目录（测试可传临时目录）。</param>
        public PermissionRulesStore(string projectRoot)
        {
            if (string.IsNullOrEmpty(projectRoot))
                throw new ArgumentNullException(nameof(projectRoot));
            _userPath = Path.GetFullPath(Path.Combine(projectRoot, UserRelativePath));
            _projectPath = Path.GetFullPath(Path.Combine(projectRoot, ProjectRelativePath));
            _userBackupPath = Path.GetFullPath(Path.Combine(projectRoot, UserBackupRelativePath));
            _projectBackupPath = Path.GetFullPath(Path.Combine(projectRoot, ProjectBackupRelativePath));
        }

        // ------------------------------------------------------------------
        // 读取
        // ------------------------------------------------------------------

        /// <inheritdoc />
        public IReadOnlyList<ScopedPermissionRule> GetActiveRules()
        {
            lock (_lock)
            {
                RefreshIfStale();
                if (_user.rules.Count == 0 && _project.rules.Count == 0)
                    return Array.Empty<ScopedPermissionRule>();

                var disabled = new HashSet<string>(_user.disabledProjectRules, StringComparer.Ordinal);
                var list = new List<ScopedPermissionRule>(_user.rules.Count + _project.rules.Count);
                foreach (var r in _user.rules)
                {
                    if (r.enabled)
                        list.Add(new ScopedPermissionRule(r.Clone(), PermissionRuleScope.User));
                }
                foreach (var r in _project.rules)
                {
                    if (r.enabled && (string.IsNullOrEmpty(r.id) || !disabled.Contains(r.id)))
                        list.Add(new ScopedPermissionRule(r.Clone(), PermissionRuleScope.Project));
                }
                return list;
            }
        }

        /// <summary>返回某个来源的全部规则（含停用的，副本），供设置 UI 展示。</summary>
        public List<PermissionRule> GetRules(PermissionRuleScope scope)
        {
            lock (_lock)
            {
                RefreshIfStale();
                var src = scope == PermissionRuleScope.User ? _user.rules : _project.rules;
                var copy = new List<PermissionRule>(src.Count);
                foreach (var r in src)
                    copy.Add(r.Clone());
                return copy;
            }
        }

        /// <summary>某条项目规则是否被本用户本地停用。</summary>
        public bool IsProjectRuleDisabledLocally(string ruleId)
        {
            if (string.IsNullOrEmpty(ruleId))
                return false;
            lock (_lock)
            {
                RefreshIfStale();
                return _user.disabledProjectRules.Contains(ruleId);
            }
        }

        /// <summary>强制重新读取两份文件。</summary>
        public void Reload()
        {
            lock (_lock)
            {
                _loadedOnce = false;
                RefreshIfStale();
            }
        }

        // ------------------------------------------------------------------
        // 写入（仅显式修改时）
        // ------------------------------------------------------------------

        /// <summary>新增规则（自动补 id）并保存对应文件。</summary>
        public PermissionRule AddRule(PermissionRuleScope scope, PermissionRule rule)
        {
            if (rule == null) throw new ArgumentNullException(nameof(rule));
            var copy = rule.Clone();
            if (string.IsNullOrEmpty(copy.id))
                copy.id = PermissionRule.NewId();
            lock (_lock)
            {
                RefreshIfStale();
                Doc(scope).rules.Add(copy);
                Save(scope);
            }
            RaiseChanged();
            return copy.Clone();
        }

        /// <summary>按 id 替换规则（可跨 scope 移动：<paramref name="newScope"/> 与原 scope 不同则移动）。</summary>
        /// <returns>是否找到并更新。</returns>
        public bool UpdateRule(PermissionRuleScope scope, PermissionRule rule, PermissionRuleScope? newScope = null)
        {
            if (rule == null || string.IsNullOrEmpty(rule.id)) return false;
            var target = newScope ?? scope;
            lock (_lock)
            {
                RefreshIfStale();
                var list = Doc(scope).rules;
                int index = list.FindIndex(r => r.id == rule.id);
                if (index < 0) return false;

                if (target == scope)
                {
                    list[index] = rule.Clone();
                    Save(scope);
                }
                else
                {
                    list.RemoveAt(index);
                    Doc(target).rules.Add(rule.Clone());
                    Save(scope);
                    Save(target);
                }
            }
            RaiseChanged();
            return true;
        }

        /// <summary>按 id 删除规则。</summary>
        public bool RemoveRule(PermissionRuleScope scope, string ruleId)
        {
            if (string.IsNullOrEmpty(ruleId)) return false;
            lock (_lock)
            {
                RefreshIfStale();
                int removed = Doc(scope).rules.RemoveAll(r => r.id == ruleId);
                if (removed == 0) return false;
                bool unDisabled = scope == PermissionRuleScope.Project && _user.disabledProjectRules.Remove(ruleId);
                Save(scope);
                if (unDisabled)
                    Save(PermissionRuleScope.User);
            }
            RaiseChanged();
            return true;
        }

        /// <summary>本地停用 / 恢复某条项目规则（只写用户文件，不改团队文件）。</summary>
        public void SetProjectRuleDisabledLocally(string ruleId, bool disabled)
        {
            if (string.IsNullOrEmpty(ruleId)) return;
            lock (_lock)
            {
                RefreshIfStale();
                var list = _user.disabledProjectRules;
                bool has = list.Contains(ruleId);
                if (disabled == has) return;
                if (disabled) list.Add(ruleId);
                else list.Remove(ruleId);
                Save(PermissionRuleScope.User);
            }
            RaiseChanged();
        }

        // ------------------------------------------------------------------
        // 内部
        // ------------------------------------------------------------------

        private PermissionRulesDocument Doc(PermissionRuleScope scope)
            => scope == PermissionRuleScope.User ? _user : _project;

        private void Save(PermissionRuleScope scope)
        {
            bool user = scope == PermissionRuleScope.User;
            var path = user ? _userPath : _projectPath;
            var doc = Doc(scope);
            var json = doc.ToJson();
            var bytes = Utf8NoBom.GetBytes(json);
            if (File.Exists(path))
            {
                try
                {
                    if (BytesEqual(File.ReadAllBytes(path), bytes))
                        return;
                }
                catch
                {
                    // 读失败则照常写入
                }
            }

            string backupPath = null;
            if (File.Exists(path))
            {
                backupPath = user ? _userBackupPath : _projectBackupPath;
                var backupDir = Path.GetDirectoryName(backupPath);
                if (!string.IsNullOrEmpty(backupDir))
                    Directory.CreateDirectory(backupDir);
            }

            AtomicFile.WriteAllBytes(path, bytes, backupPath);
            var stamp = FileStamp.Of(path);
            if (user)
            {
                _userStamp = stamp;
                UserLoadError = null;
            }
            else
            {
                _projectStamp = stamp;
                ProjectLoadError = null;
            }
        }

        private void RefreshIfStale()
        {
            var userStamp = FileStamp.Of(_userPath);
            if (!_loadedOnce || !userStamp.Equals(_userStamp))
            {
                _user = LoadDocument(_userPath, out var err);
                UserLoadError = err;
                _userStamp = userStamp;
            }

            var projectStamp = FileStamp.Of(_projectPath);
            if (!_loadedOnce || !projectStamp.Equals(_projectStamp))
            {
                _project = LoadDocument(_projectPath, out var err);
                ProjectLoadError = err;
                _projectStamp = projectStamp;
            }

            _loadedOnce = true;
        }

        private static PermissionRulesDocument LoadDocument(string path, out string error)
        {
            error = null;
            try
            {
                if (!File.Exists(path))
                    return new PermissionRulesDocument();
                return PermissionRulesDocument.FromJson(File.ReadAllText(path, Utf8NoBom));
            }
            catch (Exception ex)
            {
                // 损坏 / 无法读取：视为无规则（不拦截任何调用），保存时旧内容会被备份到 Library/AgentCore。
                error = ex.Message;
                AgentCoreLog.Warning($"[AgentCore] Failed to load permission rules from '{path}': {ex.Message}. Rules in this file are ignored.");
                return new PermissionRulesDocument();
            }
        }

        private static void RaiseChanged()
        {
            try
            {
                Changed?.Invoke();
            }
            catch
            {
                // 订阅方异常不影响保存
            }
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        private readonly struct FileStamp : IEquatable<FileStamp>
        {
            private readonly bool _exists;
            private readonly long _ticks;
            private readonly long _length;

            private FileStamp(bool exists, long ticks, long length)
            {
                _exists = exists;
                _ticks = ticks;
                _length = length;
            }

            public static FileStamp Of(string path)
            {
                try
                {
                    var info = new FileInfo(path);
                    return info.Exists
                        ? new FileStamp(true, info.LastWriteTimeUtc.Ticks, info.Length)
                        : default;
                }
                catch
                {
                    return default;
                }
            }

            public bool Equals(FileStamp other)
                => _exists == other._exists && _ticks == other._ticks && _length == other._length;

            public override bool Equals(object obj) => obj is FileStamp other && Equals(other);

            public override int GetHashCode() => _ticks.GetHashCode() ^ _length.GetHashCode();
        }
    }
}
