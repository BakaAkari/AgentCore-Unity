using System;

namespace AgentCore.Editor.Core
{
    /// <summary>
    /// 用户本次会话「最近上传的图像」的运行态引用。
    /// <para>
    /// 背景：用户通过按钮/粘贴在 chat 里上传图像后，主模型（如 GLM-5.2）不支持看图，
    /// 完整 data URL 不能塞进主 LLM 上下文（base64 是噪音）。因此把最近一次用户图
    /// 存在这里，配合 <see cref="ToolUrl"/> 让主模型用 <c>vision_analyze source=user_image</c>
    /// 短引用即可让视觉模型分析它，全程不污染主模型上下文。
    /// </para>
    /// <para>
    /// 生命周期：仅保存「最近一次」用户图；每次用户发送新图覆盖旧值。Editor 会话内有效，
    /// 不持久化（持久化走 ConversationTurn.ImageDataUrl + SessionStorage，这里是给工具读的本轮运行态）。
    /// </para>
    /// </summary>
    public static class UserImageStore
    {
        private static string _imageDataUrl;
        private static readonly object _lock = new object();

        /// <summary>当前可被 vision_analyze 读取的用户图 data URL；从未上传过为 null。</summary>
        public static string Current
        {
            get { lock (_lock) return _imageDataUrl; }
        }

        /// <summary>是否存在可用用户图。</summary>
        public static bool HasImage
        {
            get { lock (_lock) return !string.IsNullOrEmpty(_imageDataUrl); }
        }

        /// <summary>
        /// 记录一张用户图。每次用户发送新图调用（覆盖旧值）。
        /// </summary>
        /// <param name="imageDataUrl">完整 data URL（data:image/...;base64,...）。null/空则清空。</param>
        public static void Set(string imageDataUrl)
        {
            lock (_lock)
            {
                _imageDataUrl = string.IsNullOrEmpty(imageDataUrl) ? null : imageDataUrl;
            }
        }

        /// <summary>
        /// 尝试取当前用户图 data URL。
        /// </summary>
        /// <param name="url">输出参数：当前 data URL（无图时为 null）。</param>
        /// <returns>有图返回 true。</returns>
        public static bool TryGetCurrent(out string url)
        {
            lock (_lock)
            {
                url = _imageDataUrl;
                return !string.IsNullOrEmpty(url);
            }
        }

        /// <summary>清空（会话重置/切换时调用）。</summary>
        public static void Clear()
        {
            lock (_lock) _imageDataUrl = null;
        }
    }
}
