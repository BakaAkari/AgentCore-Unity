namespace AgentCore.Editor.Components.MCP.Bridge
{
    /// <summary>
    /// relay ↔ McpBridge 内部 RPC 约定（设计见 plans/mcp-server-design.md §3.3）。
    /// <para>
    /// 传输：127.0.0.1 TCP，UTF-8，每行一个 JSON 对象（无 BOM，'\n' 结尾）。所有消息带 <c>type</c> 字段。
    /// </para>
    /// <para>relay → bridge：</para>
    /// <list type="bullet">
    /// <item><c>hello { token, relayId, relayVersion, bridgeProtocol, client: { name, version } }</c> —— 必须是第一条，5 秒内到达。
    ///       <c>relayId</c> 是 relay 进程的唯一标识，重连时不变，用于把重载期间挂起的结果交还给同一个 relay。</item>
    /// <item><c>listTools { id }</c></item>
    /// <item><c>callTool { id, name, arguments }</c> —— <c>id</c> 在同一 relay 内唯一。</item>
    /// <item><c>cancel { id }</c></item>
    /// <item><c>request { id, method, params }</c> —— 通用请求（resources/list、resources/read、prompts/list、prompts/get 等），
    ///       答复 <c>response { id, result }</c> 或 <c>response { id, error: { code, message } }</c>。</item>
    /// <item><c>resume { ids }</c> —— 重连后发送：取回断开前已发出、尚未收到结果的调用。已完成的立即答复，
    ///       仍在执行的完成后答复，bridge 不认识的用 unknownCalls 告诉 relay（由 relay 判断重发或报告丢失）。</item>
    /// </list>
    /// <para>bridge → relay：</para>
    /// <list type="bullet">
    /// <item><c>helloOk { bridgeProtocol, projectPath, unityVersion, schemaHash }</c>；令牌或版本不符时 <c>helloError { message }</c> 后断开。</item>
    /// <item><c>tools { id, tools: [ { name, description, inputSchema } ], schemaHash }</c></item>
    /// <item><c>progress { id, message }</c></item>
    /// <item><c>result { id, status: "ok" | "partial" | "error", text, images: [ { mimeType, data } ] }</c></item>
    /// <item><c>unknownCalls { ids }</c> —— resume 中 bridge 不认识的调用。编辑器 PID 未变说明调用断开前没送达，relay 重发；
    ///       PID 变了说明编辑器重启过，relay 报告结果丢失。</item>
    /// <item><c>confirm { id, confirmId, title, message }</c> —— 设置为“在客户端确认”时，请发起调用 <c>id</c> 的客户端确认；
    ///       relay 答复 <c>confirmResult { confirmId, approved: true | false | null }</c>，null 表示客户端不支持，改在 Unity 窗口确认。</item>
    /// <item><c>reloading {}</c> —— 即将 Domain Reload，连接随后断开；relay 应重连并保持挂起的调用。</item>
    /// <item><c>ready { schemaHash }</c> —— 重载完成、重连成功后发送。</item>
    /// </list>
    /// </summary>
    public static class McpBridgeProtocol
    {
        /// <summary>内部 RPC 版本：relay 与 bridge 不一致时拒绝连接（不做向下兼容，提示重新复制配置）。</summary>
        public const int Version = 1;

        public const string Hello = "hello";
        public const string HelloOk = "helloOk";
        public const string HelloError = "helloError";
        public const string ListTools = "listTools";
        public const string Tools = "tools";
        public const string CallTool = "callTool";
        public const string Cancel = "cancel";
        public const string Resume = "resume";
        public const string UnknownCalls = "unknownCalls";
        public const string Request = "request";
        public const string Confirm = "confirm";
        public const string ConfirmResult = "confirmResult";
        public const string Response = "response";
        public const string Progress = "progress";
        public const string Result = "result";
        public const string Reloading = "reloading";
        public const string Ready = "ready";

        public const string StatusOk = "ok";
        public const string StatusPartial = "partial";
        public const string StatusError = "error";
    }
}
