using System;
using UnityEngine;

namespace AgentCore.Editor.Utils
{
    /// <summary>
    /// AgentCore 日志分级枚举 (v1.6.5+)。
    /// <para>
    /// 枚举顺序体现"包含"关系:高级别包含低级别 (Debug 显示所有,Silent 不显示任何)。
    /// </para>
    /// </summary>
    public enum LogLevel
    {
        /// <summary>完全静默,连 Error/Warning 都不输出。慎用。</summary>
        Silent = 0,

        /// <summary>仅输出 Error。</summary>
        Error = 1,

        /// <summary>输出 Warning + Error。</summary>
        Warning = 2,

        /// <summary>输出 Info + Warning + Error (默认)。</summary>
        Info = 3,

        /// <summary>输出 Debug + Info + Warning + Error (含流式 token、每 event 细节等高频日志)。</summary>
        Debug = 4
    }

    /// <summary>
    /// AgentCore 统一日志封装 (v1.6.5+)。
    /// <para>
    /// 通过 <see cref="AgentCore.Editor.Config.AgentCoreSettings.logLevel"/> 控制输出级别,
    /// 用户可在 Project Settings > AgentCore > Dashboard 里选择档位。
    /// </para>
    /// <para>
    /// 迁移指引:
    /// <list type="bullet">
    ///   <item><description><c>Debug.Log</c> 里高频 (每 token/每 event) → <see cref="Debug"/></description></item>
    ///   <item><description><c>Debug.Log</c> 里 turn 级 (每 tool call/每 turn) → <see cref="Info"/> (默认可见)</description></item>
    ///   <item><description><c>Debug.LogWarning</c> → <see cref="Warning"/></description></item>
    ///   <item><description><c>Debug.LogError</c> → <see cref="Error"/></description></item>
    /// </list>
    /// </para>
    /// </summary>
    public static class AgentCoreLog
    {
        /// <summary>缓存日志级别,避免每次 Debug.Log 都访问 ScriptableSingleton (性能优化)。</summary>
        private static LogLevel _cachedLevel = LogLevel.Info;

        /// <summary>缓存是否已初始化 (延迟从 Settings 读一次)。</summary>
        private static bool _cacheInitialized;

        /// <summary>
        /// 获取当前生效的日志级别。
        /// <para>首次访问时从 <see cref="AgentCore.Editor.Config.AgentCoreSettings"/> 读取,
        /// 之后走缓存;设置变更时通过 <see cref="Invalidate"/> 主动失效。</para>
        /// </summary>
        public static LogLevel CurrentLevel
        {
            get
            {
                if (!_cacheInitialized)
                {
                    RefreshCache();
                }
                return _cachedLevel;
            }
        }

        /// <summary>让缓存失效,下次访问时重新从 Settings 读取。</summary>
        public static void Invalidate()
        {
            _cacheInitialized = false;
        }

        private static void RefreshCache()
        {
            try
            {
                var settings = AgentCore.Editor.Config.AgentCoreSettings.instance;
                _cachedLevel = settings != null ? settings.logLevel : LogLevel.Info;
            }
            catch
            {
                _cachedLevel = LogLevel.Info;
            }
            _cacheInitialized = true;
        }

        /// <summary>
        /// Debug 级日志:高频细节 (流式 token、每 event、每 chunk)。
        /// 默认 Info 级不显示,需切到 Debug 才可见。
        /// </summary>
        public static void Debug(string message)
        {
            if (CurrentLevel >= LogLevel.Debug)
            {
                UnityEngine.Debug.Log(message);
            }
        }

        /// <summary>
        /// Info 级日志:关键业务事件 (session load、tool call started、state change 等)。
        /// 默认可见。
        /// </summary>
        public static void Info(string message)
        {
            if (CurrentLevel >= LogLevel.Info)
            {
                UnityEngine.Debug.Log(message);
            }
        }

        /// <summary>
        /// Warning 级日志:需要注意但不阻塞的问题。
        /// Silent 以外均可见。
        /// </summary>
        public static void Warning(string message)
        {
            if (CurrentLevel >= LogLevel.Warning)
            {
                UnityEngine.Debug.LogWarning(message);
            }
        }

        /// <summary>
        /// Error 级日志:错误但已被处理。
        /// Silent 以外均可见。
        /// </summary>
        public static void Error(string message)
        {
            if (CurrentLevel >= LogLevel.Error)
            {
                UnityEngine.Debug.LogError(message);
            }
        }

        /// <summary>
        /// Error 级日志附带异常。
        /// </summary>
        public static void Error(string message, Exception ex)
        {
            if (CurrentLevel >= LogLevel.Error)
            {
                UnityEngine.Debug.LogError($"{message}\n{ex}");
            }
        }
    }
}
