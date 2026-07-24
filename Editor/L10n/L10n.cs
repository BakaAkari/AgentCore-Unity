using System.Globalization;

namespace AgentCore.Editor.L10n
{
    /// <summary>
    /// AgentCore 本地化 API 门面. 所有 UI 代码通过 <see cref="Tr(string,string)"/> 获取本地化文本.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 约定:
    /// <list type="bullet">
    ///   <item>Key 用点分层命名 (如 <c>chat.status.idle</c>, <c>session.button.new</c>).</item>
    ///   <item>调用方**必须**提供 fallback (即原始中文), 避免语言包缺 key 时 UI 显示裸 key.</item>
    ///   <item>格式化用 <see cref="Tr(string,string,object[])"/>, 内部走 <see cref="string.Format(IFormatProvider,string,object[])"/> 用 <see cref="CultureInfo.InvariantCulture"/>.</item>
    /// </list>
    /// </para>
    /// <para>
    /// 本地化范围: 仅"用户直接看到的 UI 文本". <b>不</b>本地化 LLM 系统提示 / 工具错误 / 日志.
    /// </para>
    /// </remarks>
    public static class L10n
    {
        /// <summary>
        /// 获取 key 对应的本地化文本.
        /// </summary>
        /// <param name="key">分层命名的 key (如 <c>chat.status.idle</c>).</param>
        /// <param name="fallback">
        /// 语言包缺 key 时的兜底文本 (必传, 通常是原始中文).
        /// 兜底链: 当前语言 → 英文 → 该 fallback → key 本身.
        /// </param>
        public static string Tr(string key, string fallback)
        {
            return LanguageResourceLoader.Get(key, fallback);
        }

        /// <summary>
        /// 带参数格式化的本地化文本. 内部使用 <see cref="CultureInfo.InvariantCulture"/> 保证数字/日期格式跨语言稳定.
        /// </summary>
        /// <param name="key">分层命名的 key.</param>
        /// <param name="fallback">缺 key 兜底文本 (可含 <c>{0}</c>).</param>
        /// <param name="args">格式化参数.</param>
        public static string Tr(string key, string fallback, params object[] args)
        {
            var template = LanguageResourceLoader.Get(key, fallback);
            if (args == null || args.Length == 0) return template;

            try
            {
                return string.Format(CultureInfo.InvariantCulture, template, args);
            }
            catch
            {
                // 模板占位符与参数不匹配时降级到 fallback 模板
                try
                {
                    return string.Format(CultureInfo.InvariantCulture, fallback ?? template, args);
                }
                catch
                {
                    return template;
                }
            }
        }
    }
}
