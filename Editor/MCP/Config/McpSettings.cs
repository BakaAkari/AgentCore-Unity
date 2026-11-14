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
