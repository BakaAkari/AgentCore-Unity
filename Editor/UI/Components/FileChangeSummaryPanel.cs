using System;
using System.Collections.Generic;
using AgentCore.Editor.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using AgentCore.Editor.Utils;

namespace AgentCore.Editor.UI.Components
{
    /// <summary>
    /// 文件变更汇总面板 — 在 Chat 窗口输入栏上方显示当前会话中所有被修改的文件。
    /// <para>
    /// 功能：
    /// <list type="bullet">
    ///   <item>可折叠的头部：显示"此对话中已更改 N 个文件" + 总增减行数</item>
    ///   <item>文件列表：每行显示变更类型图标、文件路径、增减行数</item>
    ///   <item>单击文件行：在 Project 窗口中高亮定位</item>
    ///   <item>双击文件行：在 IDE 中打开文件</item>
    /// </list>
    /// </para>
    /// <para>
    /// 纯代码构建 UI，不依赖外部 UXML/USS 文件。
    /// 样式与 <see cref="ToolCallGroup"/> 保持一致。
    /// </para>
    /// </summary>
    public class FileChangeSummaryPanel : VisualElement
    {
        #region 常量

        // 颜色常量（与 ToolCallGroup/ToolCallCard 保持一致）
        private static readonly Color HeaderBg = new Color(0.20f, 0.20f, 0.20f);        // #333333
        private static readonly Color HeaderBgHover = new Color(0.24f, 0.24f, 0.24f);   // #3D3D3D
        private static readonly Color ContentBg = new Color(0.16f, 0.16f, 0.16f);       // #292929
        private static readonly Color TextPrimary = new Color(0.83f, 0.83f, 0.83f);     // #D4D4D4
        private static readonly Color TextSecondary = new Color(0.53f, 0.53f, 0.53f);   // #888888
        private static readonly Color BorderColor = new Color(0.22f, 0.22f, 0.22f);     // #383838

        // 变更类型颜色
        private static readonly Color ColorCreated = new Color(0.30f, 0.69f, 0.31f);    // #4CAF50 绿色
        private static readonly Color ColorModified = new Color(0.95f, 0.61f, 0.07f);   // #F29C12 橙色
        private static readonly Color ColorDeleted = new Color(0.96f, 0.26f, 0.21f);    // #F44336 红色
        private static readonly Color ColorMoved = new Color(0.29f, 0.57f, 0.85f);      // #4A90D9 蓝色
        private static readonly Color ColorCopied = new Color(0.53f, 0.53f, 0.53f);     // #888888 灰色

        // 行数变化颜色
        private static readonly Color LinesAddedColor = new Color(0.30f, 0.69f, 0.31f); // #4CAF50 绿色
        private static readonly Color LinesRemovedColor = new Color(0.96f, 0.26f, 0.21f); // #F44336 红色

        // 文件行悬停颜色
        private static readonly Color FileRowHover = new Color(0.22f, 0.22f, 0.22f);    // #383838

        // 折叠/展开箭头（纯 ASCII，避免 Unity 字体缺字）
        private const string ArrowCollapsed = ">";
        private const string ArrowExpanded = "v";

        // 双击检测时间阈值（毫秒）
        private const long DoubleClickThresholdMs = 400;

        #endregion

        #region UI 元素

        private readonly VisualElement _header;
        private readonly Label _arrowLabel;
        private readonly Label _titleLabel;
        private readonly Label _statsLabel;
        private readonly VisualElement _content;
        private readonly VisualElement _fileListContainer;

        #endregion

        #region 状态

        private bool _isExpanded = false;
        private int _lastFileCount = 0; // v1.9.0+: 缓存以支持语言热切换重绘 title
        private readonly Dictionary<VisualElement, long> _lastClickTimes = new Dictionary<VisualElement, long>();

        #endregion

        #region 构造函数

        /// <summary>
        /// 创建文件变更汇总面板。
        /// </summary>
        public FileChangeSummaryPanel()
        {
            // === 根容器样式 ===
            AddToClassList("file-change-summary-panel");
            style.flexDirection = FlexDirection.Column;
            style.flexShrink = 0; // 防止被 message-scroll-view 挤压
            style.marginLeft = 8;
            style.marginRight = 8;
            style.marginTop = 4;
            style.marginBottom = 4;
            style.borderTopLeftRadius = 4;
            style.borderTopRightRadius = 4;
            style.borderBottomLeftRadius = 4;
            style.borderBottomRightRadius = 4;
            style.borderTopWidth = 1;
            style.borderBottomWidth = 1;
            style.borderLeftWidth = 1;
            style.borderRightWidth = 1;
            style.borderTopColor = BorderColor;
            style.borderBottomColor = BorderColor;
            style.borderLeftColor = BorderColor;
            style.borderRightColor = BorderColor;
            style.overflow = Overflow.Hidden;

            // 默认隐藏（无变更时不显示）
            style.display = DisplayStyle.None;

            // === 头部 ===
            _header = new VisualElement();
            _header.style.flexDirection = FlexDirection.Row;
            _header.style.alignItems = Align.Center;
            _header.style.backgroundColor = HeaderBg;
            _header.style.paddingLeft = 8;
            _header.style.paddingRight = 8;
            _header.style.paddingTop = 6;
            _header.style.paddingBottom = 6;
            _header.style.cursor = StyleKeyword.Initial; // 手型光标
            Add(_header);

            // 箭头
            _arrowLabel = new Label(ArrowCollapsed);
            _arrowLabel.style.fontSize = 10;
            _arrowLabel.style.color = TextSecondary;
            _arrowLabel.style.marginRight = 6;
            _arrowLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            _arrowLabel.style.width = 12;
            _header.Add(_arrowLabel);

            // 标题 (Loc)
            _titleLabel = new Label(AgentCore.Editor.L10n.Loc.Tr("fileChange.header.empty", "此对话中已更改 0 个文件"));
            _titleLabel.style.fontSize = 12;
            _titleLabel.style.color = TextPrimary;
            _titleLabel.style.flexGrow = 1;
            _titleLabel.style.unityTextAlign = TextAnchor.MiddleLeft;
            _header.Add(_titleLabel);

            // 总增减行数统计
            _statsLabel = new Label();
            _statsLabel.style.fontSize = 11;
            _statsLabel.style.color = TextSecondary;
            _statsLabel.style.unityTextAlign = TextAnchor.MiddleRight;
            _statsLabel.style.marginLeft = 8;
            _header.Add(_statsLabel);

            // 头部悬停效果
            _header.RegisterCallback<MouseEnterEvent>(_ => _header.style.backgroundColor = HeaderBgHover);
            _header.RegisterCallback<MouseLeaveEvent>(_ => _header.style.backgroundColor = HeaderBg);

            // 头部点击折叠/展开
            _header.RegisterCallback<ClickEvent>(OnHeaderClicked);

            // === 内容区域 ===
            _content = new VisualElement();
            _content.style.backgroundColor = ContentBg;
            _content.style.paddingLeft = 4;
            _content.style.paddingRight = 4;
            _content.style.paddingTop = 2;
            _content.style.paddingBottom = 2;
            _content.style.maxHeight = 100; // 高度减半，内部列表滚动
            Add(_content);

            // 文件列表容器（ScrollView，支持上下滚动）
            _fileListContainer = new ScrollView(ScrollViewMode.Vertical);
            _fileListContainer.style.flexDirection = FlexDirection.Column;
            _fileListContainer.style.flexGrow = 1;
            _fileListContainer.style.overflow = Overflow.Hidden;
            _content.Add(_fileListContainer);

            // v1.9.0+: 语言热切换 — 仅 title 部分用缓存计数重绘
            RegisterCallback<AttachToPanelEvent>(_ =>
                AgentCore.Editor.L10n.LanguageManager.LanguageChanged += OnLanguageChanged);
            RegisterCallback<DetachFromPanelEvent>(_ =>
                AgentCore.Editor.L10n.LanguageManager.LanguageChanged -= OnLanguageChanged);
        }

        private void OnLanguageChanged(string _)
        {
            if (_lastFileCount <= 0)
            {
                _titleLabel.text = AgentCore.Editor.L10n.Loc.Tr("fileChange.header.empty", "此对话中已更改 0 个文件");
            }
            else
            {
                _titleLabel.text = AgentCore.Editor.L10n.Loc.Tr(
                    "fileChange.header.some", "此对话中已更改 {0} 个文件", _lastFileCount);
            }
        }

        #endregion

        #region 公开方法

        /// <summary>
        /// 更新文件变更列表。
        /// </summary>
        /// <param name="summaries">文件变更摘要列表</param>
        public void UpdateChanges(List<FileChangeSummary> summaries)
        {
            if (summaries == null || summaries.Count == 0)
            {
                style.display = DisplayStyle.None;
                return;
            }

            // 显示面板
            style.display = DisplayStyle.Flex;

            // 计算总计
            int totalAdded = 0;
            int totalRemoved = 0;
            foreach (var s in summaries)
            {
                totalAdded += s.TotalLinesAdded;
                totalRemoved += s.TotalLinesRemoved;
            }

            // 更新标题 (Loc)
            _lastFileCount = summaries.Count;
            _titleLabel.text = AgentCore.Editor.L10n.Loc.Tr(
                "fileChange.header.some", "此对话中已更改 {0} 个文件", summaries.Count);

            // 更新总统计
            _statsLabel.text = FormatLineStats(totalAdded, totalRemoved);

            // 重建文件列表
            _fileListContainer.Clear();
            _lastClickTimes.Clear();

            foreach (var summary in summaries)
            {
                var row = CreateFileRow(summary);
                _fileListContainer.Add(row);
            }

            // 确保折叠状态正确（默认折叠）
            _content.style.display = _isExpanded ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>
        /// 清空面板并隐藏。
        /// </summary>
        public void ClearAndHide()
        {
            _fileListContainer.Clear();
            _lastClickTimes.Clear();
            _lastFileCount = 0;
            _titleLabel.text = AgentCore.Editor.L10n.Loc.Tr("fileChange.header.empty", "此对话中已更改 0 个文件");
            _statsLabel.text = "";
            style.display = DisplayStyle.None;
        }

        #endregion

        #region 文件行构建

        /// <summary>
        /// 创建单个文件变更行。
        /// </summary>
        private VisualElement CreateFileRow(FileChangeSummary summary)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.paddingLeft = 8;
            row.style.paddingRight = 8;
            row.style.paddingTop = 3;
            row.style.paddingBottom = 3;
            row.style.borderBottomWidth = 0;
            row.userData = summary.FilePath;

            // 悬停效果
            row.RegisterCallback<MouseEnterEvent>(_ => row.style.backgroundColor = FileRowHover);
            row.RegisterCallback<MouseLeaveEvent>(_ => row.style.backgroundColor = Color.clear);

            // 点击事件（单击定位 + 双击打开）
            row.RegisterCallback<ClickEvent>(evt =>
            {
                OnFileRowClicked(row, summary.FilePath);
                evt.StopPropagation();
            });

            // 变更类型图标
            var typeIcon = new Label($"[{summary.TypeIcon}]");
            typeIcon.style.fontSize = 11;
            typeIcon.style.color = GetChangeTypeColor(summary.ChangeType);
            typeIcon.style.width = 24;
            typeIcon.style.unityTextAlign = TextAnchor.MiddleCenter;
            typeIcon.style.marginRight = 4;
            row.Add(typeIcon);

            // 文件路径（只显示文件名 + 父目录）
            var displayPath = FormatDisplayPath(summary.FilePath);
            var pathLabel = new Label(displayPath);
            pathLabel.style.fontSize = 11;
            pathLabel.style.flexGrow = 1;
            pathLabel.style.unityTextAlign = TextAnchor.MiddleLeft;
            pathLabel.style.overflow = Overflow.Hidden;
            pathLabel.style.textOverflow = TextOverflow.Ellipsis;
            pathLabel.tooltip = summary.FilePath; // 完整路径作为 tooltip

            // 删除文件特殊样式：红色文字 + "(已删除)" 后缀
            // UIElements Label 不支持 text-decoration:line-through，用颜色+后缀传达删除状态
            if (summary.ChangeType == FileChangeType.Deleted)
            {
                pathLabel.style.color = ColorDeleted;
                pathLabel.text = $"{displayPath}  {AgentCore.Editor.L10n.Loc.Tr("fileChange.deletedSuffix", "(已删除)")}";
            }
            else
            {
                pathLabel.style.color = TextPrimary;
            }

            row.Add(pathLabel);

            // 增减行数
            var lineStatsContainer = new VisualElement();
            lineStatsContainer.style.flexDirection = FlexDirection.Row;
            lineStatsContainer.style.alignItems = Align.Center;
            lineStatsContainer.style.marginLeft = 8;

            if (summary.TotalLinesAdded > 0)
            {
                var addedLabel = new Label($"+{summary.TotalLinesAdded}");
                addedLabel.style.fontSize = 11;
                addedLabel.style.color = LinesAddedColor;
                addedLabel.style.marginRight = 4;
                lineStatsContainer.Add(addedLabel);
            }

            if (summary.TotalLinesRemoved > 0)
            {
                var removedLabel = new Label($"-{summary.TotalLinesRemoved}");
                removedLabel.style.fontSize = 11;
                removedLabel.style.color = LinesRemovedColor;
                lineStatsContainer.Add(removedLabel);
            }

            // 如果没有行数变化（如移动操作），不显示行数
            if (summary.TotalLinesAdded == 0 && summary.TotalLinesRemoved == 0)
            {
                var noChangeLabel = new Label("--");
                noChangeLabel.style.fontSize = 11;
                noChangeLabel.style.color = TextSecondary;
                lineStatsContainer.Add(noChangeLabel);
            }

            row.Add(lineStatsContainer);

            return row;
        }

        #endregion

        #region 事件处理

        /// <summary>
        /// 头部点击 — 折叠/展开。
        /// </summary>
        private void OnHeaderClicked(ClickEvent evt)
        {
            _isExpanded = !_isExpanded;
            _arrowLabel.text = _isExpanded ? ArrowExpanded : ArrowCollapsed;
            _content.style.display = _isExpanded ? DisplayStyle.Flex : DisplayStyle.None;
            evt.StopPropagation();
        }

        /// <summary>
        /// 文件行点击 — 单击定位，双击打开。
        /// </summary>
        private void OnFileRowClicked(VisualElement row, string filePath)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (_lastClickTimes.TryGetValue(row, out var lastClick) &&
                (now - lastClick) < DoubleClickThresholdMs)
            {
                // 双击 — 在 IDE 中打开
                _lastClickTimes.Remove(row);
                OpenFileInIDE(filePath);
            }
            else
            {
                // 单击 — 在 Project 窗口中定位
                _lastClickTimes[row] = now;
                PingFileInProject(filePath);
            }
        }

        /// <summary>
        /// 在 Project 窗口中高亮定位文件。
        /// </summary>
        private static void PingFileInProject(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return;

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(filePath);
            if (asset != null)
            {
                EditorGUIUtility.PingObject(asset);
                Selection.activeObject = asset;
            }
            else
            {
                // 文件可能已被删除，不输出警告（删除文件点击时属于正常行为）
            }
        }

        /// <summary>
        /// 在 IDE 中打开文件。
        /// </summary>
        private static void OpenFileInIDE(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return;

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(filePath);
            if (asset != null)
            {
                AssetDatabase.OpenAsset(asset);
            }
            else
            {
                // 尝试直接用系统打开（非 Assets 目录下的文件）
                try
                {
                    var fullPath = System.IO.Path.GetFullPath(filePath);
                    if (System.IO.File.Exists(fullPath))
                    {
                        UnityEditorInternal.InternalEditorUtility.OpenFileAtLineExternal(fullPath, 1);
                    }
                    else
                    {
                        AgentCoreLog.Warning($"[AgentCore] Cannot open file: {filePath}");
                    }
                }
                catch (Exception ex)
                {
                    AgentCoreLog.Warning($"[AgentCore] Failed to open file: {ex.Message}");
                }
            }
        }

        #endregion

        #region 辅助方法

        /// <summary>
        /// 获取变更类型对应的颜色。
        /// </summary>
        private static Color GetChangeTypeColor(FileChangeType changeType)
        {
            return changeType switch
            {
                FileChangeType.Created => ColorCreated,
                FileChangeType.Modified => ColorModified,
                FileChangeType.Deleted => ColorDeleted,
                FileChangeType.Moved => ColorMoved,
                FileChangeType.Copied => ColorCopied,
                _ => TextSecondary
            };
        }

        /// <summary>
        /// 格式化行数统计文本。
        /// </summary>
        private static string FormatLineStats(int added, int removed)
        {
            var parts = new List<string>();
            if (added > 0) parts.Add($"+{added}");
            if (removed > 0) parts.Add($"-{removed}");
            return parts.Count > 0 ? string.Join("  ", parts) : "";
        }

        /// <summary>
        /// 格式化显示路径（缩短长路径，只保留文件名和最近的父目录）。
        /// </summary>
        private static string FormatDisplayPath(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return filePath;

            // 统一使用正斜杠
            var normalized = filePath.Replace('\\', '/');

            // 如果路径不太长，直接显示
            if (normalized.Length <= 50) return normalized;

            // 取最后两级路径
            var parts = normalized.Split('/');
            if (parts.Length <= 2) return normalized;

            var fileName = parts[parts.Length - 1];
            var parentDir = parts[parts.Length - 2];

            // 如果以 Assets/ 开头，保留 Assets 前缀
            if (normalized.StartsWith("Assets/"))
            {
                return $"Assets/.../{parentDir}/{fileName}";
            }

            return $".../{parentDir}/{fileName}";
        }

        #endregion
    }
}
