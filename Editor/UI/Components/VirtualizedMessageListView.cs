using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI.Components
{
    /// <summary>
    /// 消息列表管理器。
    /// <para>
    /// 通过 DOM 池化技术解决长上下文导致的卡顿问题：
    /// 当消息项超过 <see cref="MaxVisibleItems"/> 时，将最旧的消息项从 DOM 中移除，
    /// 仅保留最近的消息项在 DOM 中渲染，并在顶部显示"加载更多"占位符。
    /// 用户点击"加载更多"时，批量恢复旧消息项到 DOM。
    /// </para>
    /// <para>
    /// 当用户向上滚动查看历史消息后，再滚动回底部时，自动重新折叠旧消息，
    /// 只保留最新的 <see cref="MaxVisibleItems"/> 条在 DOM 中，以恢复性能。
    /// </para>
    /// <para>
    /// 所有消息项的 VisualElement 引用始终保留在内存中（<see cref="_allItems"/>），
    /// 只有 DOM 挂载状态会变化，因此 <see cref="AgentCore.Editor.UI.ChatWindow"/> 中的
    /// <c>_messageBubbles</c> 字典仍然有效，流式更新不受影响。
    /// </para>
    /// </summary>
    public class MessageListManager
    {
        #region 常量

        /// <summary>DOM 中最多同时保留的消息项数量</summary>
        private const int MaxVisibleItems = 8;

        /// <summary>点击"加载更多"时每次恢复的消息项数量</summary>
        private const int LoadMoreBatchSize = 4;

        /// <summary>判断"到达底部"的滚动偏差阈值（像素）</summary>
        private const float ScrollBottomThreshold = 20f;

        #endregion

        #region 私有字段

        /// <summary>消息容器（ScrollView 内部的 VisualElement）</summary>
        private readonly VisualElement _container;

        /// <summary>ScrollView 引用（用于监听滚动事件）</summary>
        private ScrollView _scrollView;

        /// <summary>所有消息项（按添加顺序排列，包含已从 DOM 移除的旧项）</summary>
        private readonly List<VisualElement> _allItems = new();

        /// <summary>当前已从 DOM 移除的旧消息项数量（即隐藏项的起始偏移）</summary>
        private int _hiddenCount = 0;

        /// <summary>"加载更多"占位符元素（显示在列表顶部）</summary>
        private VisualElement _loadMorePlaceholder;

        /// <summary>用户是否正在查看历史消息（已向上滚动离开底部）</summary>
        private bool _userScrolledUp = false;

        #endregion

        #region 构造函数

        /// <summary>
        /// 创建消息列表管理器。
        /// </summary>
        /// <param name="container">消息容器（ScrollView 内部的 VisualElement）</param>
        public MessageListManager(VisualElement container)
        {
            _container = container;
        }

        /// <summary>
        /// 绑定 ScrollView，启用"滚动到底部时自动重新折叠"功能。
        /// </summary>
        /// <param name="scrollView">包含消息容器的 ScrollView</param>
        public void AttachScrollView(ScrollView scrollView)
        {
            if (_scrollView != null)
            {
                _scrollView.UnregisterCallback<GeometryChangedEvent>(OnScrollViewGeometryChanged);
            }
            _scrollView = scrollView;
            if (_scrollView != null)
            {
                // 监听滚动偏移变化（Unity UI Toolkit 通过 GeometryChangedEvent 或 scrollOffset 属性变化触发）
                _scrollView.RegisterCallback<GeometryChangedEvent>(OnScrollViewGeometryChanged);
                // 监听用户滚动
                _scrollView.verticalScroller.valueChanged += OnScrollValueChanged;
            }
        }

        #endregion

        #region 公开方法

        /// <summary>
        /// 添加消息项到列表末尾。
        /// 如果 DOM 中的项数超过阈值，自动将最旧的项移出 DOM。
        /// </summary>
        /// <param name="item">要添加的 VisualElement</param>
        public void AddItem(VisualElement item)
        {
            if (item == null) return;

            _allItems.Add(item);
            _container.Add(item);

            // 检查是否需要裁剪旧消息
            TrimOldItemsIfNeeded();
        }

        /// <summary>
        /// 清空所有消息项（包括 DOM 和内存中的引用）。
        /// </summary>
        public void Clear()
        {
            // 移除"加载更多"占位符
            RemoveLoadMorePlaceholder();

            // 清空容器
            _container.Clear();

            // 清空内存引用
            _allItems.Clear();
            _hiddenCount = 0;
        }

        /// <summary>
        /// 获取当前管理的消息项总数（包括已从 DOM 移除的旧项）。
        /// </summary>
        public int TotalCount => _allItems.Count;

        /// <summary>
        /// 获取当前 DOM 中的消息项数量。
        /// </summary>
        public int VisibleCount => _allItems.Count - _hiddenCount;

        /// <summary>
        /// 解绑 ScrollView，清理事件监听。
        /// </summary>
        public void DetachScrollView()
        {
            if (_scrollView != null)
            {
                _scrollView.UnregisterCallback<GeometryChangedEvent>(OnScrollViewGeometryChanged);
                _scrollView.verticalScroller.valueChanged -= OnScrollValueChanged;
                _scrollView = null;
            }
        }

        #endregion

        #region 私有方法

        /// <summary>
        /// 响应 ScrollView 的 verticalScroller 值变化。
        /// 当用户滚动到底部时，自动重新折叠旧消息。
        /// </summary>
        private void OnScrollValueChanged(float value)
        {
            if (_scrollView == null || _hiddenCount == 0) return;

            // 检查是否到达底部
            var scroller = _scrollView.verticalScroller;
            bool atBottom = scroller.highValue <= 0f || (scroller.highValue - value) <= ScrollBottomThreshold;

            if (atBottom && _userScrolledUp)
            {
                // 用户从历史消息滚回底部，重新折叠旧消息
                _userScrolledUp = false;
                RecollapseOldItems();
            }
            else if (!atBottom)
            {
                _userScrolledUp = true;
            }
        }

        /// <summary>
        /// 响应 ScrollView 几何变化（布局更新时检查滚动位置）。
        /// </summary>
        private void OnScrollViewGeometryChanged(GeometryChangedEvent evt)
        {
            if (_scrollView == null || _hiddenCount == 0 || !_userScrolledUp) return;

            var scroller = _scrollView.verticalScroller;
            bool atBottom = scroller.highValue <= 0f ||
                            (scroller.highValue - scroller.value) <= ScrollBottomThreshold;

            if (atBottom)
            {
                _userScrolledUp = false;
                RecollapseOldItems();
            }
        }

        /// <summary>
        /// 重新折叠旧消息：将 DOM 中超出 MaxVisibleItems 的旧消息移出，只保留最新的。
        /// 在用户滚回底部时调用，以恢复性能。
        /// </summary>
        private void RecollapseOldItems()
        {
            int visibleCount = _allItems.Count - _hiddenCount;
            if (visibleCount <= MaxVisibleItems) return;

            // 计算需要重新隐藏的项数
            int toHide = visibleCount - MaxVisibleItems;

            for (int i = 0; i < toHide; i++)
            {
                int idx = _hiddenCount + i;
                if (idx < _allItems.Count)
                {
                    var item = _allItems[idx];
                    if (item.parent == _container)
                        _container.Remove(item);
                }
            }

            _hiddenCount += toHide;

            // 确保占位符存在并更新文本
            EnsureLoadMorePlaceholder();
            UpdateLoadMoreButtonText();
        }

        /// <summary>
        /// 检查 DOM 中的项数是否超过阈值，超过时裁剪最旧的项。
        /// </summary>
        private void TrimOldItemsIfNeeded()
        {
            int visibleCount = _allItems.Count - _hiddenCount;
            if (visibleCount <= MaxVisibleItems) return;

            // 计算需要隐藏的项数
            int toHide = visibleCount - MaxVisibleItems;

            for (int i = 0; i < toHide; i++)
            {
                int idx = _hiddenCount + i;
                if (idx < _allItems.Count)
                {
                    var item = _allItems[idx];
                    if (item.parent == _container)
                    {
                        _container.Remove(item);
                    }
                }
            }

            _hiddenCount += toHide;

            // 确保"加载更多"占位符存在
            EnsureLoadMorePlaceholder();
        }

        /// <summary>
        /// 确保"加载更多"占位符存在于容器顶部。
        /// </summary>
        private void EnsureLoadMorePlaceholder()
        {
            if (_loadMorePlaceholder != null) return;

            _loadMorePlaceholder = CreateLoadMorePlaceholder();
            // 插入到容器最顶部（索引 0）
            _container.Insert(0, _loadMorePlaceholder);
        }

        /// <summary>
        /// 移除"加载更多"占位符。
        /// </summary>
        private void RemoveLoadMorePlaceholder()
        {
            if (_loadMorePlaceholder == null) return;
            if (_loadMorePlaceholder.parent == _container)
            {
                _container.Remove(_loadMorePlaceholder);
            }
            _loadMorePlaceholder = null;
        }

        /// <summary>
        /// 创建"加载更多"占位符 VisualElement。
        /// </summary>
        private VisualElement CreateLoadMorePlaceholder()
        {
            var placeholder = new VisualElement();
            placeholder.style.flexDirection = FlexDirection.Row;
            placeholder.style.justifyContent = Justify.Center;
            placeholder.style.alignItems = Align.Center;
            placeholder.style.paddingTop = 8;
            placeholder.style.paddingBottom = 8;
            placeholder.style.marginBottom = 4;

            var btn = new Button(() => LoadMoreItems());
            btn.text = $"加载更多历史消息（已隐藏 {_hiddenCount} 条）";
            btn.style.fontSize = 11;
            btn.style.color = new StyleColor(new Color(0.6f, 0.8f, 1f));
            btn.style.backgroundColor = new StyleColor(new Color(0.15f, 0.2f, 0.3f, 0.8f));
            btn.style.borderTopLeftRadius = 6;
            btn.style.borderTopRightRadius = 6;
            btn.style.borderBottomLeftRadius = 6;
            btn.style.borderBottomRightRadius = 6;
            btn.style.borderTopWidth = 1;
            btn.style.borderBottomWidth = 1;
            btn.style.borderLeftWidth = 1;
            btn.style.borderRightWidth = 1;
            btn.style.borderTopColor = new StyleColor(new Color(0.3f, 0.5f, 0.7f, 0.6f));
            btn.style.borderBottomColor = new StyleColor(new Color(0.3f, 0.5f, 0.7f, 0.6f));
            btn.style.borderLeftColor = new StyleColor(new Color(0.3f, 0.5f, 0.7f, 0.6f));
            btn.style.borderRightColor = new StyleColor(new Color(0.3f, 0.5f, 0.7f, 0.6f));
            btn.style.paddingLeft = 16;
            btn.style.paddingRight = 16;
            btn.style.paddingTop = 5;
            btn.style.paddingBottom = 5;

            placeholder.Add(btn);
            return placeholder;
        }

        /// <summary>
        /// 加载更多历史消息（将隐藏的旧消息批量恢复到 DOM）。
        /// </summary>
        private void LoadMoreItems()
        {
            if (_hiddenCount == 0) return;

            // 计算本次要恢复的项数
            int toRestore = Mathf.Min(LoadMoreBatchSize, _hiddenCount);
            int newHiddenCount = _hiddenCount - toRestore;

            // 从旧到新，将项插入到容器中（占位符之后）
            // 占位符在索引 0，所以从索引 1 开始插入
            int insertIndex = 1;
            for (int i = newHiddenCount; i < _hiddenCount; i++)
            {
                var item = _allItems[i];
                _container.Insert(insertIndex, item);
                insertIndex++;
            }

            _hiddenCount = newHiddenCount;

            // 更新或移除占位符
            if (_hiddenCount == 0)
            {
                RemoveLoadMorePlaceholder();
            }
            else
            {
                // 更新按钮文本
                UpdateLoadMoreButtonText();
            }
        }

        /// <summary>
        /// 更新"加载更多"按钮的文本（显示剩余隐藏数量）。
        /// </summary>
        private void UpdateLoadMoreButtonText()
        {
            if (_loadMorePlaceholder == null) return;
            var btn = _loadMorePlaceholder.Q<Button>();
            if (btn != null)
            {
                btn.text = $"加载更多历史消息（已隐藏 {_hiddenCount} 条）";
            }
        }

        #endregion
    }
}
