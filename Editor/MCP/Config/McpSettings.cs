using AgentCore.Editor.Components.MCP.Bridge;
using UnityEditor;

namespace AgentCore.Editor.Components.MCP.Config
{
    /// <summary>MCP 调用的审查级别。</summary>
    public enum McpReviewLevel
    {
        /// <summary>跟随 AgentCore Chat 当前的信任状态（含 YOLO）。默认。</summary>
        FollowAgentCore = 0,

        /// <summary>信任 MCP 客户端：外部调用一律直通（客户端自己会询问用户），等同 MCP 专用的 YOLO。</summary>
        TrustClient = 1,
    }

    /// <summary>需要确认时在哪里确认。</summary>
    public enum McpConfirmLocation
    {
        /// <summary>Unity 的 AgentCore 窗口。默认。</summary>
        UnityWindow = 0,

        /// <summary>客户端支持 elicitation 时在客户端里确认，否则回到 Unity 窗口。</summary>
        ClientWhenSupported = 1,
    }

    /// <summary>
    /// MCP 的个人设置（每用户、每工程，存 EditorPrefs；不进版本库）。
    /// 读取线程安全（缓存值）；写入在主线程（设置页）。
    /// </summary>
    public static class McpSettings
    {
        private static string Prefix => "AgentCore.Mcp." + McpPaths.ProjectRoot + ".";

        private static int _reviewLevel = -1;
        private static int _confirmLocation = -1;

        public static McpReviewLevel ReviewLevel
        {
            get
            {
                if (_reviewLevel < 0)
                    _reviewLevel = EditorPrefs.GetInt(Prefix + "ReviewLevel", 0);
                return (McpReviewLevel)_reviewLevel;
            }
            set
            {
                _reviewLevel = (int)value;
                EditorPrefs.SetInt(Prefix + "ReviewLevel", _reviewLevel);
            }
        }

        public static McpConfirmLocation ConfirmLocation
        {
            get
            {
                if (_confirmLocation < 0)
                    _confirmLocation = EditorPrefs.GetInt(Prefix + "ConfirmLocation", 0);
                return (McpConfirmLocation)_confirmLocation;
            }
            set
            {
                _confirmLocation = (int)value;
                EditorPrefs.SetInt(Prefix + "ConfirmLocation", _confirmLocation);
            }
        }

        /// <summary>是否开启本机 HTTP 端点（给只能填 URL 的客户端，或多个客户端共用）。默认关闭。</summary>
        public static bool HttpEnabled
        {
            get => EditorPrefs.GetBool(Prefix + "HttpEnabled", false);
            set => EditorPrefs.SetBool(Prefix + "HttpEnabled", value);
        }

        /// <summary>HTTP 端点端口：首次开启时选一个空闲端口并记住，之后保持不变（客户端配置不用改）。0 = 未选。</summary>
        public static int HttpPort
        {
            get => EditorPrefs.GetInt(Prefix + "HttpPort", 0);
            set => EditorPrefs.SetInt(Prefix + "HttpPort", value);
        }

        /// <summary>主线程上预读一次，之后后台线程读缓存值。</summary>
        internal static void Warm()
        {
            _ = ReviewLevel;
            _ = ConfirmLocation;
        }

        /// <summary>测试用：直接设定缓存值，不写 EditorPrefs。</summary>
        internal static void OverrideForTests(McpReviewLevel? review, McpConfirmLocation? location)
        {
            _reviewLevel = review.HasValue ? (int)review.Value : -1;
            _confirmLocation = location.HasValue ? (int)location.Value : -1;
        }
    }
}
