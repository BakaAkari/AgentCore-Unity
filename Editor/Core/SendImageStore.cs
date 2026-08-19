using System;

namespace AgentCore.Editor.Core
{
    /// <summary>
    /// Assistant「向聊天窗口发送图像」的运行态引用（v1.16.0）。
    /// <para>
    /// 背景：模型在对话中需要把一张截图/图发给用户（如「把当前画面截图发给我」「把这张图展示出来」）
    /// 时，调用 send_image 工具捕获/取得图。图片 data URL 不能塞进主 LLM 上下文（base64 是噪音），
    /// 因此由 send_image 工具把图暂存在这里，随后 UI 在本次工具调用完成时从本 Store 读取，
    /// 渲染到对应的 assistant 消息气泡，并写入 ConversationTurn.ImageDataUrl 持久化。
    /// </para>
    /// <para>
    /// 生命周期：仅保存「最近一次」assistant 要发送的图；每次 send_image 覆盖旧值。
    /// Editor 会话内有效，不持久化（持久化走 ConversationTurn.ImageDataUrl + SessionStorage）。
    /// 与 <see cref="UserImageStore"/> 职责分离：UserImageStore 存用户上传的图（供 vision_analyze
    /// source=user_image 读），本 Store 存 assistant 主动发送的图（供 UI 渲染到 assistant 气泡）。
    /// </para>
    /// </summary>
    public static class SendImageStore
    {
        private static string _imageDataUrl;
        private static readonly object _lock = new object();

        /// <summary>当前可被 UI 读取的 assistant 待发图 data URL；从未发送过为 null。</summary>
        public static string Current
        {
            get { lock (_lock) return _imageDataUrl; }
        }

        /// <summary>是否存在可用 assistant 图。</summary>
        public static bool HasImage
        {
            get { lock (_lock) return !string.IsNullOrEmpty(_imageDataUrl); }
        }

        /// <summary>
        /// 记录一张 assistant 要发送的图。每次 send_image 调用覆盖旧值。
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
        /// 尝试取当前 assistant 待发图 data URL。
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

        /// <summary>清空（发送完成后由 UI 消费掉；会话重置/切换时也调用）。</summary>
        public static void Clear()
        {
            lock (_lock) _imageDataUrl = null;
        }
    }
}
