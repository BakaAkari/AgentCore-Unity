using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Workspace.Config;
using AgentCore.Editor.Workspace.Resolution;
using UnityEditor;
using AgentCore.Editor.Utils;

namespace AgentCore.Editor.Workspace
{
    /// <summary>
    /// 提供统一的 WorkspaceContext 快照。
    /// 单例服务，供所有模块（Bootstrap、VCS、Tools、RAG、Memory、Indexing）消费。
    ///
    /// 不阻塞主线程：
    ///   - 主线程首次调用 GetCurrent() 只做纯文件系统解析（.svn 目录探测 + workspace.json），立即返回。
    ///   - 检测到 SVN 工作副本时，在后台线程运行 `svn info` 补全 URL / 分支 / Revision，
    ///     完成后回到主线程替换缓存并触发 <see cref="ContextChanged"/>。
    ///   - 上一次成功的 svn info 结果按 UnityRoot 记在 EditorPrefs，探测完成前先用它（Fingerprint 保持稳定）；
    ///     从未探测过时 URL / 分支为空，探测完成后补上。
    ///   - 非主线程调用且尚无缓存时返回 null，并投递一次主线程解析（UnityRoot 依赖 Application.dataPath）。
    ///
    /// Domain Reload 安全：
    ///   - 静态缓存在 Domain Reload 后自动失效（_cached = null）。
    ///   - 首次调用 GetCurrent() 时重新解析；后台探测结果在重载后通过 MainThread 投递时被丢弃。
    ///   - 不使用 [InitializeOnLoad] 自动初始化，避免 Editor 启动时阻塞。
    /// </summary>
    public static class WorkspaceContextService
    {
        private const string LastKnownSvnInfoPrefKeyPrefix = "AgentCore.Workspace.LastSvnInfo.";

        private static volatile WorkspaceContext _cached;
        private static bool _isResolving;

        // 缓存代次：InvalidateCache / Refresh 时递增，过期代次的后台探测结果被丢弃
        private static int _generation = 1; // 从 1 开始：0 留给 _probeGeneration 表示“无探测”

        // 正在运行后台 svn info 的代次（0 = 无），避免同一代次重复启动
        private static int _probeGeneration;

        // 非主线程请求解析时，避免每次调用都投递
        private static int _resolvePosted;

        /// <summary>
        /// 后台 svn info 探测完成、缓存被替换后在主线程触发（例如 URL / 分支 / Fingerprint 补全）。
        /// </summary>
        public static event Action ContextChanged;

        /// <summary>后台 svn info 探测是否正在进行。</summary>
        public static bool IsProbing => Volatile.Read(ref _probeGeneration) != 0;

        /// <summary>
        /// 获取当前 WorkspaceContext 快照，从不阻塞。
        /// 主线程首次调用时做快速解析并在后台补全 SVN 元数据；后续调用返回缓存值。
        /// 非主线程且尚无缓存、或解析正在进行中时返回 null。
        /// </summary>
        /// <returns>WorkspaceContext 快照，或 null（解析中 / 非主线程尚未解析）。</returns>
        public static WorkspaceContext GetCurrent()
        {
            var cached = _cached;
            if (cached != null)
                return cached;

            if (!MainThread.IsMainThread)
            {
                RequestResolveOnMainThread();
                return null;
            }

            if (_isResolving)
                return null;

            return ResolveAndCache();
        }

        /// <summary>
        /// 返回当前缓存（可能为 null），不触发解析。
        /// </summary>
        public static WorkspaceContext PeekCached() => _cached;

        /// <summary>
        /// 强制重新解析 WorkspaceContext，清除缓存。
        /// 用于用户手动刷新（Settings 页面 Refresh 按钮）或 WorkspaceRoot 变化后。
        /// 立即返回快速解析结果，SVN 元数据在后台重新探测，完成后触发 <see cref="ContextChanged"/>。
        /// </summary>
        /// <returns>新解析的 WorkspaceContext 快照（非主线程调用时为 null，解析已投递到主线程）。</returns>
        public static WorkspaceContext Refresh()
        {
            InvalidateCache();
            return GetCurrent();
        }

        /// <summary>
        /// 清除缓存，下次 GetCurrent() 时重新解析。
        /// </summary>
        public static void InvalidateCache()
        {
            Interlocked.Increment(ref _generation);
            _cached = null;
        }

        /// <summary>
        /// 检查缓存是否有效（已解析且非 Error 状态）。
        /// </summary>
        public static bool IsCacheValid => _cached != null && _cached.IsValid;

        // ── 私有解析逻辑 ──────────────────────────────────────────────────────

        private static void RequestResolveOnMainThread()
        {
            if (Interlocked.Exchange(ref _resolvePosted, 1) != 0)
                return;

            MainThread.Post(() =>
            {
                Interlocked.Exchange(ref _resolvePosted, 0);
                GetCurrent();
            });
        }

        /// <summary>主线程：快速解析并缓存，必要时启动后台 svn info 探测。</summary>
        private static WorkspaceContext ResolveAndCache()
        {
            _isResolving = true;
            try
            {
                var generation = Volatile.Read(ref _generation);

                // Step 1: 解析 UnityRoot（Application.dataPath，主线程）
                var unityRoot = UnityRootResolver.Resolve();
                if (string.IsNullOrEmpty(unityRoot))
                {
                    _cached = WorkspaceContext.CreateError("UnityRootResolver returned null — cannot determine Unity project root.");
                    return _cached;
                }

                // Step 2: 只做 .svn 目录探测；有工作副本时先用上次的 svn info 结果，再在后台刷新
                var svnInfo = SvnWorkspaceInfoResolver.ResolveFast(unityRoot);
                if (svnInfo.IsWorkingCopy)
                {
                    var lastKnown = LoadLastKnownSvnInfo(unityRoot);
                    if (lastKnown != null && string.Equals(lastKnown.RootPath, svnInfo.RootPath, StringComparison.OrdinalIgnoreCase))
                        svnInfo = lastKnown;

                    StartSvnProbe(unityRoot, generation);
                }

                _cached = Build(unityRoot, svnInfo);
                return _cached;
            }
            catch (Exception ex)
            {
                AgentCoreLog.Error($"[AgentCore] WorkspaceContextService.Resolve() failed: {ex}");
                _cached = WorkspaceContext.CreateError($"Unexpected error during workspace resolution: {ex.Message}");
                return _cached;
            }
            finally
            {
                _isResolving = false;
            }
        }

        /// <summary>主线程：在后台线程运行 svn info，结果投递回主线程应用。</summary>
        private static void StartSvnProbe(string unityRoot, int generation)
        {
            if (Volatile.Read(ref _probeGeneration) == generation)
                return;
            Volatile.Write(ref _probeGeneration, generation);

            Task.Run(() => SvnWorkspaceInfoResolver.Resolve(unityRoot))
                .ContinueWith(t =>
                {
                    var info = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
                    MainThread.Post(() => ApplySvnProbe(unityRoot, generation, info));
                }, TaskScheduler.Default);
        }

        /// <summary>主线程：用后台 svn info 结果重建上下文。过期代次的结果直接丢弃。</summary>
        private static void ApplySvnProbe(string unityRoot, int generation, WorkspaceVcsInfo info)
        {
            if (Volatile.Read(ref _probeGeneration) == generation)
                Volatile.Write(ref _probeGeneration, 0);

            if (info == null || generation != Volatile.Read(ref _generation) || _cached == null)
                return;

            try
            {
                if (info.IsCommandAvailable && info.IsWorkingCopy && !string.IsNullOrEmpty(info.Url))
                    SaveLastKnownSvnInfo(unityRoot, info);

                _cached = Build(unityRoot, info);
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore] WorkspaceContextService: applying svn info failed: {ex.Message}");
                return;
            }

            try { ContextChanged?.Invoke(); }
            catch (Exception ex) { AgentCoreLog.Error($"[AgentCore] WorkspaceContextService.ContextChanged handler error: {ex}"); }
        }

        /// <summary>由 UnityRoot + SVN 信息组装 WorkspaceContext（纯文件系统，不启动子进程）。</summary>
        private static WorkspaceContext Build(string unityRoot, WorkspaceVcsInfo svnInfo)
        {
            // 解析 WorkspaceRoot（含 VCS 元数据）
            var (workspaceRoot, status, vcsInfo) = WorkspaceRootResolver.Resolve(unityRoot, svnInfo);
            if (string.IsNullOrEmpty(workspaceRoot))
            {
                return WorkspaceContext.CreateError("WorkspaceRootResolver returned null workspace root.");
            }

            // 计算 UnityRootRelativePath
            var unityRelPath = ComputeRelativePath(workspaceRoot, unityRoot);

            // 加载 workspace.json 配置
            WorkspaceConfig config = null;
            try { config = WorkspaceConfigStorage.Load(workspaceRoot); }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore] WorkspaceContextService: failed to load workspace.json: {ex.Message}");
            }

            // 解析 Scope Roots
            var roots = ScopeRootResolver.Resolve(workspaceRoot, unityRoot, config);

            // 构建 Fingerprint
            var enabledPaths = new System.Collections.Generic.List<string>();
            foreach (var r in roots)
                if (r.IsEnabled) enabledPaths.Add(r.RelativePath ?? string.Empty);

            var fingerprint = WorkspaceFingerprintBuilder.Build(
                workspaceRoot,
                vcsInfo?.Url,
                vcsInfo?.RepositoryRoot,
                vcsInfo?.BranchId,
                unityRelPath,
                enabledPaths);

            // 组装 WorkspaceContext
            return new WorkspaceContext
            {
                WorkspaceRoot = workspaceRoot,
                UnityRoot = unityRoot,
                UnityRootRelativePath = unityRelPath ?? string.Empty,
                Fingerprint = fingerprint,
                Vcs = vcsInfo,
                Roots = roots,
                Status = status,
                ResolvedAt = DateTime.UtcNow
            };
        }

        // ── 上次 svn info 结果（EditorPrefs，主线程） ─────────────────────────

        private static string LastKnownPrefKey(string unityRoot)
        {
            var sb = new StringBuilder(LastKnownSvnInfoPrefKeyPrefix);
            foreach (var c in unityRoot.ToLowerInvariant())
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.ToString();
        }

        private static WorkspaceVcsInfo LoadLastKnownSvnInfo(string unityRoot)
        {
            try
            {
                var json = EditorPrefs.GetString(LastKnownPrefKey(unityRoot), string.Empty);
                if (string.IsNullOrEmpty(json))
                    return null;
                var info = JsonHelper.Deserialize<WorkspaceVcsInfo>(json);
                if (info == null || string.IsNullOrEmpty(info.RootPath))
                    return null;
                info.Type = WorkspaceVcsType.Svn;
                info.IsWorkingCopy = true;
                return info;
            }
            catch
            {
                return null;
            }
        }

        private static void SaveLastKnownSvnInfo(string unityRoot, WorkspaceVcsInfo info)
        {
            try
            {
                EditorPrefs.SetString(LastKnownPrefKey(unityRoot), JsonHelper.Serialize(info));
            }
            catch
            {
                // 只是启动加速用的缓存，失败无影响
            }
        }

        private static string ComputeRelativePath(string workspaceRoot, string fullPath)
        {
            if (string.IsNullOrEmpty(workspaceRoot) || string.IsNullOrEmpty(fullPath))
                return string.Empty;

            var baseNorm = workspaceRoot.TrimEnd('/') + "/";
            var fullNorm = fullPath.TrimEnd('/');

            if (fullNorm.StartsWith(baseNorm, StringComparison.OrdinalIgnoreCase))
                return fullNorm.Substring(baseNorm.Length);

            // UnityRoot 与 WorkspaceRoot 相同（FallbackToUnityRoot 场景）
            if (string.Equals(fullNorm, workspaceRoot.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                return string.Empty;

            return string.Empty;
        }
    }
}
