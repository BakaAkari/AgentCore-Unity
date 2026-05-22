using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Cloud;
using AgentCore.Editor.Config;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI.Components
{
    /// <summary>
    /// Knowledge Base 面板组件。
    /// 提供 LightRAG 知识库的状态显示、连接测试、文档索引、文档列表管理等功能。
    /// P0：文档列表 + 删除。P1：track_id 轮询真实索引进度。
    /// </summary>
    public class KnowledgeBasePanel : VisualElement
    {
        // ─────────────────────────────────────────────
        //  常量
        // ─────────────────────────────────────────────

        /// <summary>允许索引的文件扩展名</summary>
        private static readonly string[] AllowedExtensions =
        {
            ".md", ".txt", ".cs", ".json", ".xml", ".yaml", ".yml",
            ".html", ".htm", ".rst", ".pdf"
        };

        /// <summary>单文件最大大小（字节）：5MB</summary>
        private const long MaxFileSizeBytes = 5 * 1024 * 1024;

        /// <summary>track_id 轮询间隔（毫秒）</summary>
        private const int PollIntervalMs = 2000;

        /// <summary>track_id 轮询最长等待时间（分钟）</summary>
        private const int PollTimeoutMinutes = 5;

        // ─────────────────────────────────────────────
        //  UI 元素引用 — 状态区
        // ─────────────────────────────────────────────

        private Label _statusEnabledLabel;
        private Label _statusEndpointLabel;
        private Label _statusConnectionLabel;
        private Button _testConnectionButton;
        private Button _openSettingsButton;

        // ─────────────────────────────────────────────
        //  UI 元素引用 — 添加知识区
        // ─────────────────────────────────────────────

        private Button _indexDocumentButton;

        // ─────────────────────────────────────────────
        //  UI 元素引用 — 文档列表区
        // ─────────────────────────────────────────────

        private VisualElement _documentsSection;
        private Button _refreshDocumentsButton;
        private ScrollView _documentsScrollView;
        private Label _documentsSummaryLabel;

        // ─────────────────────────────────────────────
        //  UI 元素引用 — 上次索引结果区
        // ─────────────────────────────────────────────

        private VisualElement _lastResultSection;
        private Label _lastResultLabel;
        private Button _askAgentButton;

        // ─────────────────────────────────────────────
        //  UI 元素引用 — 进度遮罩
        // ─────────────────────────────────────────────

        private VisualElement _progressOverlay;
        private Label _progressLabel;

        // ─────────────────────────────────────────────
        //  状态枚举
        // ─────────────────────────────────────────────

        private enum ConnectionStatus { Unknown, Testing, Connected, Failed }
        private enum IndexStatus { Idle, Uploading, Processing, Success, Failed }

        // ─────────────────────────────────────────────
        //  运行时状态
        // ─────────────────────────────────────────────

        private ConnectionStatus _connectionStatus = ConnectionStatus.Unknown;
        private IndexStatus _indexStatus = IndexStatus.Idle;
        private string _lastIndexedFile = null;
        private string _lastIndexSummary = null;

        /// <summary>测试连接操作的取消令牌</summary>
        private CancellationTokenSource _connectionCts;

        /// <summary>文件上传操作的取消令牌</summary>
        private CancellationTokenSource _indexCts;

        /// <summary>track_id 轮询的取消令牌</summary>
        private CancellationTokenSource _pollCts;

        /// <summary>文档列表刷新的取消令牌</summary>
        private CancellationTokenSource _refreshCts;

        /// <summary>
        /// 当用户点击"Ask Agent about this document"时触发。
        /// 参数为建议的提示词文本。
        /// </summary>
        public event Action<string> OnAskAgentRequested;

        // ─────────────────────────────────────────────
        //  构造
        // ─────────────────────────────────────────────

        /// <summary>
        /// 创建 KnowledgeBasePanel 实例并构建 UI。
        /// </summary>
        public KnowledgeBasePanel()
        {
            AddToClassList("knowledge-panel-content");
            BuildUI();
            RefreshStatus();
        }

        // ─────────────────────────────────────────────
        //  UI 构建
        // ─────────────────────────────────────────────

        private void BuildUI()
        {
            // 标题
            var titleLabel = new Label("Knowledge Base");
            titleLabel.AddToClassList("kb-panel__title");
            Add(titleLabel);

            // ── Status 区块 ──
            var statusSection = CreateSection("状态");
            Add(statusSection);

            _statusEnabledLabel = new Label();
            _statusEnabledLabel.AddToClassList("kb-panel__status-row");
            statusSection.Add(_statusEnabledLabel);

            _statusEndpointLabel = new Label();
            _statusEndpointLabel.AddToClassList("kb-panel__status-row");
            _statusEndpointLabel.AddToClassList("kb-panel__status-row--muted");
            statusSection.Add(_statusEndpointLabel);

            _statusConnectionLabel = new Label();
            _statusConnectionLabel.AddToClassList("kb-panel__status-row");
            statusSection.Add(_statusConnectionLabel);

            // 操作按钮行
            var actionRow = new VisualElement();
            actionRow.AddToClassList("kb-panel__button-row");
            statusSection.Add(actionRow);

            _testConnectionButton = new Button(OnTestConnectionClicked) { text = "测试连接" };
            _testConnectionButton.AddToClassList("kb-panel__button");
            actionRow.Add(_testConnectionButton);

            _openSettingsButton = new Button(OnOpenSettingsClicked) { text = "打开设置" };
            _openSettingsButton.AddToClassList("kb-panel__button");
            _openSettingsButton.AddToClassList("kb-panel__button--secondary");
            actionRow.Add(_openSettingsButton);

            // ── Add Knowledge 区块 ──
            var addSection = CreateSection("添加知识");
            Add(addSection);

            _indexDocumentButton = new Button(OnIndexDocumentClicked) { text = "+ 索引文档..." };
            _indexDocumentButton.AddToClassList("kb-panel__button");
            _indexDocumentButton.AddToClassList("kb-panel__button--primary");
            addSection.Add(_indexDocumentButton);

            var hintLabel = new Label("支持 .md .txt .cs .json .xml .yaml 等格式，最大 5MB");
            hintLabel.AddToClassList("kb-panel__hint");
            addSection.Add(hintLabel);

            // ── 知识库文档区块 ──
            // 手动构建（不用 CreateSection），以便在标题行右侧放刷新按钮
            _documentsSection = new VisualElement();
            _documentsSection.AddToClassList("kb-panel__section");
            _documentsSection.style.flexGrow = 1;
            _documentsSection.style.flexShrink = 1;
            _documentsSection.style.minHeight = 0;
            Add(_documentsSection);

            // 标题行：标题 Label + 刷新按钮（横向布局）
            var docsTitleRow = new VisualElement();
            docsTitleRow.style.flexDirection = FlexDirection.Row;
            docsTitleRow.style.alignItems = Align.Center;
            docsTitleRow.style.marginBottom = 8;
            _documentsSection.Add(docsTitleRow);

            var docsSectionTitle = new Label("知识库文档");
            docsSectionTitle.AddToClassList("kb-panel__section-title");
            docsSectionTitle.style.flexGrow = 1;
            docsSectionTitle.style.marginBottom = 0; // 覆盖 section-title 的 margin-bottom，由父容器控制
            docsTitleRow.Add(docsSectionTitle);

            _refreshDocumentsButton = new Button(OnRefreshDocumentsClicked) { text = "↻ 刷新" };
            _refreshDocumentsButton.AddToClassList("kb-panel__button");
            _refreshDocumentsButton.AddToClassList("kb-panel__button--secondary");
            _refreshDocumentsButton.AddToClassList("kb-panel__button--small");
            docsTitleRow.Add(_refreshDocumentsButton);

            // 文档列表 ScrollView
            _documentsScrollView = new ScrollView(ScrollViewMode.Vertical);
            _documentsScrollView.AddToClassList("kb-panel__docs-scroll");
            // 移除 maxHeight，让 ScrollView 占满剩余空间
            _documentsSection.Add(_documentsScrollView);

            // 初始占位提示
            var docsPlaceholder = new Label("点击「↻ 刷新」加载文档列表");
            docsPlaceholder.AddToClassList("kb-panel__hint");
            docsPlaceholder.name = "docs-placeholder";
            _documentsScrollView.Add(docsPlaceholder);

            // 文档统计摘要
            _documentsSummaryLabel = new Label();
            _documentsSummaryLabel.AddToClassList("kb-panel__hint");
            _documentsSection.Add(_documentsSummaryLabel);

            // ── Last Index Result 区块 ──
            _lastResultSection = CreateSection("上次索引结果");
            _lastResultSection.style.display = DisplayStyle.None;
            Add(_lastResultSection);

            _lastResultLabel = new Label("尚未索引任何文档。");
            _lastResultLabel.AddToClassList("kb-panel__result-label");
            _lastResultSection.Add(_lastResultLabel);

            _askAgentButton = new Button(OnAskAgentClicked) { text = "向 Agent 询问此文档" };
            _askAgentButton.AddToClassList("kb-panel__button");
            _askAgentButton.AddToClassList("kb-panel__button--accent");
            _askAgentButton.style.display = DisplayStyle.None;
            _lastResultSection.Add(_askAgentButton);

            // ── 进度遮罩 ──
            _progressOverlay = new VisualElement();
            _progressOverlay.AddToClassList("kb-panel__progress-overlay");
            _progressOverlay.style.display = DisplayStyle.None;

            _progressLabel = new Label("处理中...");
            _progressLabel.AddToClassList("kb-panel__progress-label");
            _progressOverlay.Add(_progressLabel);
            Add(_progressOverlay);
        }

        /// <summary>
        /// 创建带标题的区块容器。
        /// </summary>
        private static VisualElement CreateSection(string title)
        {
            var section = new VisualElement();
            section.AddToClassList("kb-panel__section");

            var sectionTitle = new Label(title);
            sectionTitle.AddToClassList("kb-panel__section-title");
            section.Add(sectionTitle);

            return section;
        }

        // ─────────────────────────────────────────────
        //  状态刷新
        // ─────────────────────────────────────────────

        /// <summary>
        /// 刷新面板状态显示（从 AgentCoreSettings 读取最新配置）。
        /// </summary>
        public void RefreshStatus()
        {
            var settings = AgentCoreSettings.instance;
            bool enabled = settings.lightragEnabled;
            string endpoint = settings.lightragEndpoint;

            // 启用状态
            _statusEnabledLabel.text = enabled
                ? "LightRAG:  已启用"
                : "LightRAG:  未启用";
            _statusEnabledLabel.EnableInClassList("kb-panel__status--enabled", enabled);
            _statusEnabledLabel.EnableInClassList("kb-panel__status--disabled", !enabled);

            // Endpoint
            _statusEndpointLabel.text = string.IsNullOrEmpty(endpoint)
                ? "Endpoint: (未配置)"
                : $"Endpoint: {endpoint}";

            // 连接状态
            UpdateConnectionStatusLabel();

            // 按钮可用性
            bool canOperate = enabled && !string.IsNullOrEmpty(endpoint);
            _testConnectionButton.SetEnabled(canOperate && _connectionStatus != ConnectionStatus.Testing);
            // 让索引按钮在上传过程中保持启用，允许用户继续操作
            _indexDocumentButton.SetEnabled(canOperate);
            _refreshDocumentsButton?.SetEnabled(canOperate);

            if (!enabled)
            {
                _statusConnectionLabel.text = "连接: — (服务未启用)";
                _statusConnectionLabel.RemoveFromClassList("kb-panel__status--connected");
                _statusConnectionLabel.RemoveFromClassList("kb-panel__status--failed");
            }
        }

        private void UpdateConnectionStatusLabel()
        {
            switch (_connectionStatus)
            {
                case ConnectionStatus.Unknown:
                    _statusConnectionLabel.text = "连接: 未测试";
                    _statusConnectionLabel.RemoveFromClassList("kb-panel__status--connected");
                    _statusConnectionLabel.RemoveFromClassList("kb-panel__status--failed");
                    _statusConnectionLabel.RemoveFromClassList("kb-panel__status--testing");
                    break;
                case ConnectionStatus.Testing:
                    _statusConnectionLabel.text = "连接: 测试中...";
                    _statusConnectionLabel.AddToClassList("kb-panel__status--testing");
                    _statusConnectionLabel.RemoveFromClassList("kb-panel__status--connected");
                    _statusConnectionLabel.RemoveFromClassList("kb-panel__status--failed");
                    break;
                case ConnectionStatus.Connected:
                    _statusConnectionLabel.text = "连接:  已连接";
                    _statusConnectionLabel.AddToClassList("kb-panel__status--connected");
                    _statusConnectionLabel.RemoveFromClassList("kb-panel__status--failed");
                    _statusConnectionLabel.RemoveFromClassList("kb-panel__status--testing");
                    break;
                case ConnectionStatus.Failed:
                    _statusConnectionLabel.text = "连接:  连接失败";
                    _statusConnectionLabel.AddToClassList("kb-panel__status--failed");
                    _statusConnectionLabel.RemoveFromClassList("kb-panel__status--connected");
                    _statusConnectionLabel.RemoveFromClassList("kb-panel__status--testing");
                    break;
            }
        }

        // ─────────────────────────────────────────────
        //  按钮事件 — 连接测试
        // ─────────────────────────────────────────────

        private async void OnTestConnectionClicked()
        {
            var settings = AgentCoreSettings.instance;
            if (!settings.lightragEnabled || string.IsNullOrEmpty(settings.lightragEndpoint))
            {
                EditorUtility.DisplayDialog("提示", "请先在 AgentCore Settings 中启用 LightRAG 并配置 Endpoint。", "确定");
                return;
            }

            _connectionStatus = ConnectionStatus.Testing;
            _testConnectionButton.SetEnabled(false);
            UpdateConnectionStatusLabel();

            try
            {
                _connectionCts?.Cancel();
                _connectionCts = new CancellationTokenSource();
                _connectionCts.CancelAfter(TimeSpan.FromSeconds(15));

                var client = LightRAGClient.FromSettings();
                bool success = await client.TestConnectionAsync(_connectionCts.Token);

                _connectionStatus = success ? ConnectionStatus.Connected : ConnectionStatus.Failed;
            }
            catch (OperationCanceledException)
            {
                _connectionStatus = ConnectionStatus.Failed;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AgentCore] KnowledgeBasePanel.TestConnection failed: {ex.Message}");
                _connectionStatus = ConnectionStatus.Failed;
            }
            finally
            {
                _testConnectionButton.SetEnabled(true);
                UpdateConnectionStatusLabel();
            }
        }

        private static void OnOpenSettingsClicked()
        {
            SettingsService.OpenProjectSettings("Project/AgentCore");
        }

        // ─────────────────────────────────────────────
        //  按钮事件 — 索引文档
        // ─────────────────────────────────────────────

        private async void OnIndexDocumentClicked()
        {
            var settings = AgentCoreSettings.instance;
            if (!settings.lightragEnabled)
            {
                EditorUtility.DisplayDialog("提示", "请先在 AgentCore Settings 中启用 LightRAG 服务。", "确定");
                return;
            }
            if (string.IsNullOrEmpty(settings.lightragEndpoint))
            {
                EditorUtility.DisplayDialog("提示", "请先在 AgentCore Settings 中配置 LightRAG Endpoint。", "确定");
                return;
            }

            // 打开文件选择对话框
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string filePath = EditorUtility.OpenFilePanel(
                "选择要索引的文档",
                projectRoot,
                string.Join(",", AllowedExtensions).Replace(".", ""));

            if (string.IsNullOrEmpty(filePath))
                return; // 用户取消

            // 校验文件路径（必须在项目根目录内）
            string normalizedFile = Path.GetFullPath(filePath).Replace('\\', '/');
            string normalizedRoot = projectRoot.Replace('\\', '/').TrimEnd('/') + "/";
            if (!normalizedFile.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                EditorUtility.DisplayDialog("安全限制",
                    "只允许索引项目根目录内的文件。\n\n" +
                    $"项目根目录：{projectRoot}\n" +
                    $"选择的文件：{filePath}",
                    "确定");
                return;
            }

            // 校验扩展名
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            bool extAllowed = false;
            foreach (var allowed in AllowedExtensions)
            {
                if (ext == allowed) { extAllowed = true; break; }
            }
            if (!extAllowed)
            {
                EditorUtility.DisplayDialog("不支持的文件类型",
                    $"文件类型 '{ext}' 不在支持列表中。\n\n" +
                    $"支持的类型：{string.Join(", ", AllowedExtensions)}",
                    "确定");
                return;
            }

            // 校验文件大小
            var fileInfo = new FileInfo(filePath);
            if (!fileInfo.Exists)
            {
                EditorUtility.DisplayDialog("错误", $"文件不存在：{filePath}", "确定");
                return;
            }
            if (fileInfo.Length > MaxFileSizeBytes)
            {
                float sizeMB = fileInfo.Length / (1024f * 1024f);
                EditorUtility.DisplayDialog("文件过大",
                    $"文件大小 {sizeMB:F1}MB 超过限制（最大 5MB）。\n\n" +
                    "请选择较小的文件，或将大文件拆分后分批索引。",
                    "确定");
                return;
            }

            // 开始索引
            // 不显示进度遮罩，让上传在后台执行
            await UploadAndTrackFileAsync(filePath);
        }

        /// <summary>
        /// 上传文件并通过 track_id 轮询真实索引进度。
        /// </summary>
        private async Task UploadAndTrackFileAsync(string filePath)
        {
            string fileName = Path.GetFileName(filePath);

            // ── 阶段一：上传 ──
            _indexStatus = IndexStatus.Uploading;
            // 不禁用按钮，不显示进度遮罩，让上传在后台执行
            _progressLabel.text = $"正在上传：{fileName}...";
            // 不显示进度遮罩，不阻塞用户交互
            _lastResultSection.style.display = DisplayStyle.None;
            _askAgentButton.style.display = DisplayStyle.None;

            LightRAGIndexResult uploadResult = null;
            string errorMessage = null;

            try
            {
                _indexCts?.Cancel();
                _indexCts = new CancellationTokenSource();
                _indexCts.CancelAfter(TimeSpan.FromSeconds(60));

                var client = LightRAGClient.FromSettings();
                uploadResult = await client.IndexFileAsync(filePath, _indexCts.Token);
            }
            catch (OperationCanceledException)
            {
                errorMessage = "上传超时（60秒）";
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                Debug.LogWarning($"[AgentCore] KnowledgeBasePanel.UploadFile failed: {ex.Message}");
            }

            // 上传失败
            if (uploadResult == null || !uploadResult.Accepted)
            {
                _indexDocumentButton.SetEnabled(true);
                _indexStatus = IndexStatus.Failed;

                _lastResultSection.style.display = DisplayStyle.Flex;
                string reason = uploadResult?.ErrorMessage ?? errorMessage ?? "请检查 LightRAG 服务状态";
                _lastIndexSummary = $" 上传失败：{fileName}\n原因：{reason}";
                _lastResultLabel.text = _lastIndexSummary;
                _lastResultLabel.AddToClassList("kb-panel__result--failed");
                _lastResultLabel.RemoveFromClassList("kb-panel__result--success");
                _askAgentButton.style.display = DisplayStyle.None;
                return;
            }

            // ── 阶段二：等待 LightRAG 处理 ──
            _lastIndexedFile = filePath;

            if (string.IsNullOrEmpty(uploadResult.TrackId))
            {
                // 无 track_id，降级为旧行为（直接显示成功）
                _indexDocumentButton.SetEnabled(true);
                _indexStatus = IndexStatus.Success;

                _lastResultSection.style.display = DisplayStyle.Flex;
                _lastIndexSummary = $" 已上传：{fileName}（无法追踪处理进度）";
                _lastResultLabel.text = _lastIndexSummary;
                _lastResultLabel.AddToClassList("kb-panel__result--success");
                _lastResultLabel.RemoveFromClassList("kb-panel__result--failed");
                _askAgentButton.style.display = DisplayStyle.Flex;

                // 刷新文档列表
                _ = RefreshDocumentsAsync();
                return;
            }

            // 有 track_id，进入轮询阶段
            _indexStatus = IndexStatus.Processing;
            _progressLabel.text = $"LightRAG 处理中：{fileName}...";

            // 显示"处理中"的上次结果（让用户知道进度）
            _lastResultSection.style.display = DisplayStyle.Flex;
            _lastIndexSummary = $"处理中：{fileName}";
            _lastResultLabel.text = _lastIndexSummary;
            _lastResultLabel.RemoveFromClassList("kb-panel__result--failed");
            _lastResultLabel.RemoveFromClassList("kb-panel__result--success");
            _askAgentButton.style.display = DisplayStyle.None;

            // 启动轮询（不 await，让 UI 保持响应）
            _ = PollIndexProgressAsync(uploadResult.TrackId, fileName);
        }

        /// <summary>
        /// 轮询 track_id 直到索引完成或失败。
        /// </summary>
        private async Task PollIndexProgressAsync(string trackId, string fileName)
        {
            _pollCts?.Cancel();
            _pollCts = new CancellationTokenSource();
            _pollCts.CancelAfter(TimeSpan.FromMinutes(PollTimeoutMinutes));

            var client = LightRAGClient.FromSettings();
            bool finished = false;

            try
            {
                while (!_pollCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(PollIntervalMs, _pollCts.Token);

                    var status = await client.TrackStatusAsync(trackId, _pollCts.Token);

                    if (status == null)
                        continue;

                    string s = status.Status?.ToLowerInvariant() ?? "";

                    if (s == "processed")
                    {
                        // 索引完成
                        finished = true;
                        _indexStatus = IndexStatus.Success;
                        _indexDocumentButton.SetEnabled(true);

                        _lastIndexSummary = $" 索引完成：{fileName}";
                        _lastResultLabel.text = _lastIndexSummary;
                        _lastResultLabel.AddToClassList("kb-panel__result--success");
                        _lastResultLabel.RemoveFromClassList("kb-panel__result--failed");
                        _askAgentButton.style.display = DisplayStyle.Flex;

                        // 刷新文档列表
                        _ = RefreshDocumentsAsync();
                        break;
                    }
                    else if (s == "failed")
                    {
                        // 索引失败
                        finished = true;
                        _indexStatus = IndexStatus.Failed;
                        _indexDocumentButton.SetEnabled(true);

                        string errMsg = status.ErrorMsg ?? "LightRAG 处理失败";
                        _lastIndexSummary = $" 索引失败：{fileName}\n原因：{errMsg}";
                        _lastResultLabel.text = _lastIndexSummary;
                        _lastResultLabel.AddToClassList("kb-panel__result--failed");
                        _lastResultLabel.RemoveFromClassList("kb-panel__result--success");
                        _askAgentButton.style.display = DisplayStyle.None;
                        break;
                    }
                    else
                    {
                        // 仍在处理中（pending / processing）
                        _progressLabel.text = $"LightRAG 处理中：{fileName}（{status.Status}）...";
                        _lastIndexSummary = $"处理中：{fileName}（{status.Status}）";
                        _lastResultLabel.text = _lastIndexSummary;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 超时或被取消
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AgentCore] KnowledgeBasePanel.PollIndexProgress failed: {ex.Message}");
            }

            // 如果轮询结束但未完成（超时）
            if (!finished)
            {
                _indexStatus = IndexStatus.Idle;
                _indexDocumentButton.SetEnabled(true);

                _lastIndexSummary = $" 处理超时：{fileName}\n请稍后点击「↻ 刷新」查看文档列表确认结果。";
                _lastResultLabel.text = _lastIndexSummary;
                _lastResultLabel.RemoveFromClassList("kb-panel__result--success");
                _lastResultLabel.RemoveFromClassList("kb-panel__result--failed");
            }
        }

        // ─────────────────────────────────────────────
        //  按钮事件 — 文档列表
        // ─────────────────────────────────────────────

        private async void OnRefreshDocumentsClicked()
        {
            await RefreshDocumentsAsync();
        }

        /// <summary>
        /// 刷新文档列表（可被内部调用）。
        /// </summary>
        private async Task RefreshDocumentsAsync()
        {
            var settings = AgentCoreSettings.instance;
            if (!settings.lightragEnabled || string.IsNullOrEmpty(settings.lightragEndpoint))
            {
                _documentsScrollView.Clear();
                var hint = new Label("请先启用 LightRAG 并配置 Endpoint");
                hint.AddToClassList("kb-panel__hint");
                _documentsScrollView.Add(hint);
                _documentsSummaryLabel.text = "";
                return;
            }

            // 取消上一次刷新
            _refreshCts?.Cancel();
            _refreshCts = new CancellationTokenSource();
            _refreshCts.CancelAfter(TimeSpan.FromSeconds(30));

            _refreshDocumentsButton?.SetEnabled(false);
            _documentsScrollView.Clear();

            var loadingLabel = new Label("加载中...");
            loadingLabel.AddToClassList("kb-panel__hint");
            _documentsScrollView.Add(loadingLabel);
            _documentsSummaryLabel.text = "";

            List<LightRAGDocument> docs = null;
            string errorMsg = null;

            try
            {
                var client = LightRAGClient.FromSettings();
                docs = await client.GetDocumentsAsync(_refreshCts.Token);
            }
            catch (OperationCanceledException)
            {
                errorMsg = "加载超时";
            }
            catch (Exception ex)
            {
                errorMsg = ex.Message;
                Debug.LogWarning($"[AgentCore] KnowledgeBasePanel.RefreshDocuments failed: {ex.Message}");
            }
            finally
            {
                _refreshDocumentsButton?.SetEnabled(true);
            }

            _documentsScrollView.Clear();

            if (errorMsg != null)
            {
                var errLabel = new Label($"加载失败：{errorMsg}");
                errLabel.AddToClassList("kb-panel__hint");
                _documentsScrollView.Add(errLabel);
                _documentsSummaryLabel.text = "";
                return;
            }

            if (docs == null || docs.Count == 0)
            {
                var emptyLabel = new Label("知识库中暂无文档");
                emptyLabel.AddToClassList("kb-panel__hint");
                _documentsScrollView.Add(emptyLabel);
                _documentsSummaryLabel.text = "";
                return;
            }

            // 渲染文档列表
            RenderDocumentList(docs);
        }

        /// <summary>
        /// 渲染文档列表条目。
        /// </summary>
        private void RenderDocumentList(List<LightRAGDocument> docs)
        {
            _documentsScrollView.Clear();

            foreach (var doc in docs)
            {
                var item = BuildDocumentItem(doc);
                _documentsScrollView.Add(item);
            }

            // 统计摘要
            int processed = 0, pending = 0, failed = 0;
            foreach (var d in docs)
            {
                string s = d.Status?.ToLowerInvariant() ?? "";
                if (s == "processed") processed++;
                else if (s == "pending" || s == "processing") pending++;
                else if (s == "failed") failed++;
            }

            var parts = new System.Collections.Generic.List<string>();
            if (processed > 0) parts.Add($"{processed} 已处理");
            if (pending > 0)   parts.Add($"{pending} 处理中");
            if (failed > 0)    parts.Add($"{failed} 失败");

            _documentsSummaryLabel.text = $"共 {docs.Count} 个文档（{string.Join("，", parts)}）";
        }

        /// <summary>
        /// 构建单个文档列表条目。
        /// </summary>
        private VisualElement BuildDocumentItem(LightRAGDocument doc)
        {
            var item = new VisualElement();
            item.AddToClassList("kb-panel__doc-item");

            // 顶部行：文件名 + 状态徽章
            var topRow = new VisualElement();
            topRow.AddToClassList("kb-panel__doc-item__top-row");
            topRow.style.flexDirection = FlexDirection.Row;
            topRow.style.alignItems = Align.Center;
            item.Add(topRow);

            // 文件名
            string displayName = string.IsNullOrEmpty(doc.FilePath)
                ? doc.Id ?? "(未知)"
                : Path.GetFileName(doc.FilePath);

            var nameLabel = new Label(displayName);
            nameLabel.AddToClassList("kb-panel__doc-item__name");
            nameLabel.style.flexGrow = 1;
            topRow.Add(nameLabel);

            // 状态徽章
            string statusText = doc.Status ?? "unknown";
            var statusBadge = new Label(GetStatusDisplayText(statusText));
            statusBadge.AddToClassList("kb-panel__doc-item__status-badge");
            statusBadge.AddToClassList(GetStatusBadgeClass(statusText));
            topRow.Add(statusBadge);

            // 摘要行
            if (!string.IsNullOrEmpty(doc.ContentSummary))
            {
                string summary = doc.ContentSummary.Length > 100
                    ? doc.ContentSummary.Substring(0, 100) + "..."
                    : doc.ContentSummary;

                var summaryLabel = new Label(summary);
                summaryLabel.AddToClassList("kb-panel__doc-item__summary");
                item.Add(summaryLabel);
            }

            // 错误信息行（仅 failed 状态）
            if (!string.IsNullOrEmpty(doc.ErrorMsg) &&
                string.Equals(doc.Status, "failed", StringComparison.OrdinalIgnoreCase))
            {
                var errLabel = new Label($"错误：{doc.ErrorMsg}");
                errLabel.AddToClassList("kb-panel__doc-item__error");
                item.Add(errLabel);
            }

            // 底部行：块数 + 删除按钮
            var bottomRow = new VisualElement();
            bottomRow.AddToClassList("kb-panel__doc-item__bottom-row");
            bottomRow.style.flexDirection = FlexDirection.Row;
            bottomRow.style.alignItems = Align.Center;
            item.Add(bottomRow);

            if (doc.ChunksCount > 0)
            {
                var chunksLabel = new Label($"{doc.ChunksCount} 块");
                chunksLabel.AddToClassList("kb-panel__doc-item__meta");
                chunksLabel.style.flexGrow = 1;
                bottomRow.Add(chunksLabel);
            }
            else
            {
                var spacer = new VisualElement();
                spacer.style.flexGrow = 1;
                bottomRow.Add(spacer);
            }

            // 删除按钮（捕获 doc 变量）
            string docId = doc.Id;
            string fileName = displayName;
            var deleteBtn = new Button(() => OnDeleteDocumentClicked(docId, fileName)) { text = "删除" };
            deleteBtn.AddToClassList("kb-panel__button");
            deleteBtn.AddToClassList("kb-panel__button--danger");
            deleteBtn.AddToClassList("kb-panel__button--small");
            bottomRow.Add(deleteBtn);

            return item;
        }

        private static string GetStatusDisplayText(string status)
        {
            switch (status?.ToLowerInvariant())
            {
                case "processed":  return "已处理";
                case "pending":    return "等待中";
                case "processing": return "处理中";
                case "failed":     return "失败";
                default:           return status ?? "未知";
            }
        }

        private static string GetStatusBadgeClass(string status)
        {
            switch (status?.ToLowerInvariant())
            {
                case "processed":  return "kb-panel__doc-item__status-badge--processed";
                case "pending":
                case "processing": return "kb-panel__doc-item__status-badge--pending";
                case "failed":     return "kb-panel__doc-item__status-badge--failed";
                default:           return "kb-panel__doc-item__status-badge--unknown";
            }
        }

        // ─────────────────────────────────────────────
        //  按钮事件 — 删除文档
        // ─────────────────────────────────────────────

        private async void OnDeleteDocumentClicked(string docId, string fileName)
        {
            bool confirm = EditorUtility.DisplayDialog(
                "确认删除",
                $"确定要从知识库中删除文档「{fileName}」吗？\n\n此操作不可撤销。",
                "删除", "取消");

            if (!confirm) return;

            try
            {
                var client = LightRAGClient.FromSettings();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                bool success = await client.DeleteDocumentAsync(docId, cts.Token);

                if (success)
                {
                    // 刷新文档列表
                    await RefreshDocumentsAsync();
                }
                else
                {
                    EditorUtility.DisplayDialog("删除失败",
                        $"无法删除文档「{fileName}」，请检查 LightRAG 服务状态。", "确定");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AgentCore] KnowledgeBasePanel.DeleteDocument failed: {ex.Message}");
                EditorUtility.DisplayDialog("删除失败",
                    $"删除文档时发生错误：{ex.Message}", "确定");
            }
        }

        // ─────────────────────────────────────────────
        //  按钮事件 — Ask Agent
        // ─────────────────────────────────────────────

        private void OnAskAgentClicked()
        {
            if (string.IsNullOrEmpty(_lastIndexedFile))
                return;

            string fileName = Path.GetFileName(_lastIndexedFile);
            string prompt = $"请基于刚刚索引的文档「{fileName}」，总结关键内容和可执行建议。";
            OnAskAgentRequested?.Invoke(prompt);
        }

        // ─────────────────────────────────────────────
        //  生命周期
        // ─────────────────────────────────────────────

        /// <summary>
        /// 面板被激活时调用（切换到 Knowledge 模块时）。
        /// 刷新配置状态显示并自动加载文档列表。
        /// </summary>
        public void OnActivated()
        {
            RefreshStatus();
            // 自动刷新文档列表
            var settings = AgentCoreSettings.instance;
            if (settings.lightragEnabled && !string.IsNullOrEmpty(settings.lightragEndpoint))
            {
                _ = RefreshDocumentsAsync();
            }
        }

        /// <summary>
        /// 面板被停用时调用（切换离开 Knowledge 模块时）。
        /// 取消进行中的操作。
        /// </summary>
        public void OnDeactivated()
        {
            _connectionCts?.Cancel();
            _refreshCts?.Cancel();
            // 注意：不取消 _indexCts 和 _pollCts，允许后台继续处理
        }

        /// <summary>
        /// 释放资源。
        /// </summary>
        public void Dispose()
        {
            _connectionCts?.Cancel();
            _connectionCts?.Dispose();
            _connectionCts = null;

            _indexCts?.Cancel();
            _indexCts?.Dispose();
            _indexCts = null;

            _pollCts?.Cancel();
            _pollCts?.Dispose();
            _pollCts = null;

            _refreshCts?.Cancel();
            _refreshCts?.Dispose();
            _refreshCts = null;
        }
    }
}
