using AgentCore.Editor.Components.VCS.Tools;
using AgentCore.Editor.UI.Components;
using UnityEditor;

namespace AgentCore.Editor.Components.VCS.UI
{
    /// <summary>
    /// 把 VCS 状态桥接到 Hub 左侧导航按钮的驱动器。
    /// <para>
    /// 职责：
    /// <list type="bullet">
    /// <item>按检测到的 VCS 类型（<see cref="VcsType"/>）把导航按钮标签动态改为 SVN / GIT / P4；未检测到时保留默认 "VCS"。</item>
    /// <item>订阅 <see cref="VcsRemoteStatusMonitor.StatusChanged"/>，远端有更新（<c>HasRemoteChanges</c>）时把按钮标记为告警高亮（变黄），否则清除。</item>
    /// </list>
    /// 通过通用的 <see cref="HubNavBadgeBus"/> 推送状态，不直接持有 HubRail / ChatWindow，符合 VCS asmdef → 主 Editor 的单向依赖。
    /// </para>
    /// </summary>
    [InitializeOnLoad]
    public static class VcsHubNavBadgeDriver
    {
        /// <summary>Hub 模块 ID，必须与 <see cref="VersionControlPanelContribution.Id"/> 一致。</summary>
        private const string ModuleId = "version-control";

        static VcsHubNavBadgeDriver()
        {
            // 域重载后重新绑定并推送一次当前状态（HubNavBadgeBus 会保留快照，
            // 供之后打开的 ChatWindow 主动拉取）。
            VcsRemoteStatusMonitor.StatusChanged += OnRemoteStatusChanged;
            PublishCurrent(VcsRemoteStatusMonitor.LastStatus);
        }

        private static void OnRemoteStatusChanged(VcsSyncStatus status)
        {
            PublishCurrent(status);
        }

        /// <summary>
        /// 根据当前 VCS 类型与远端状态推送导航角标。
        /// </summary>
        private static void PublishCurrent(VcsSyncStatus status)
        {
            var vcsType = VcsDetector.DetectVcs();
            var label = ResolveLabel(vcsType);

            // 只有成功检测到远端且确实落后时才告警。检测失败 / 无远端变更 / 未配置 VCS 均不高亮。
            var alert = status != null && status.Success && status.HasRemoteChanges;

            HubNavBadgeBus.Publish(new HubNavBadgeState
            {
                ModuleId = ModuleId,
                LabelOverride = label,
                Alert = alert
            });
        }

        /// <summary>
        /// VCS 类型 → 导航按钮显示名。未检测到时返回 null（保留默认 "VCS" 标签）。
        /// </summary>
        private static string ResolveLabel(VcsType vcsType)
        {
            switch (vcsType)
            {
                case VcsType.Svn:      return "SVN";
                case VcsType.Git:      return "GIT";
                case VcsType.Perforce: return "P4";
                default:               return null;
            }
        }
    }
}
