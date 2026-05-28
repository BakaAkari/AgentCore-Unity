# VCS Panel TreeView 重构方案

## 1. 目标

将 VCS Panel 的 Working Copy Status 从当前的"目录列表 + 文件列表"分离结构改造为**树形嵌套结构**，使用 Unity UI Toolkit 的 TreeView。

### 当前结构
```
Directories (3)
  ├─ Assets/Scripts (5 files)
  ├─ Assets/Prefabs (2 files)
  └─ Assets/Scenes (1 file)

Files (8)
  ├─ Assets/Scripts/Player.cs [Modified]
  ├─ Assets/Scripts/Enemy.cs [Added]
  └─ ...
```

### 目标结构
```
Working Copy Status (8 files)
  ├─ 📁 Assets/
  │   ├─ 📁 Scripts/
  │   │   ├─ 📄 Player.cs [Modified]
  │   │   ├─ 📄 Enemy.cs [Added]
  │   │   └─ 📄 GameManager.cs [Modified]
  │   ├─ 📁 Prefabs/
  │   │   └─ 📄 Bullet.prefab [Modified]
  │   └─ 📁 Scenes/
  │       └─ 📄 MainScene.unity [Modified]
```

---

## 2. 数据结构设计

### 2.1 树节点模型

```csharp
/// <summary>
/// VCS 树节点 - 表示文件夹或文件
/// </summary>
public class VcsTreeNode
{
    public string Name { get; set; }              // 节点名称（文件名或文件夹名）
    public string FullPath { get; set; }          // 完整路径
    public bool IsDirectory { get; set; }         // 是否为文件夹
    public VcsFileStatus FileStatus { get; set; } // 文件状态（仅文件节点有效）
    public List<VcsTreeNode> Children { get; set; } = new List<VcsTreeNode>();
    public VcsTreeNode Parent { get; set; }       // 父节点引用
    
    // 辅助属性
    public int Id { get; set; }                   // TreeView 需要的唯一 ID
    public bool HasChanges => IsDirectory 
        ? Children.Any(c => c.HasChanges) 
        : FileStatus != null;
}
```

### 2.2 树构建算法

从扁平的 `List<VcsFileStatus>` 构建树形结构：

```csharp
private VcsTreeNode BuildTree(List<VcsFileStatus> files)
{
    var root = new VcsTreeNode 
    { 
        Name = "Root", 
        FullPath = "", 
        IsDirectory = true,
        Id = 0
    };
    
    int nextId = 1;
    
    foreach (var file in files)
    {
        var parts = file.FilePath.Split('/');
        var currentNode = root;
        
        // 遍历路径的每一部分
        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            var isLastPart = (i == parts.Length - 1);
            var pathSoFar = string.Join("/", parts.Take(i + 1));
            
            // 查找或创建子节点
            var child = currentNode.Children.FirstOrDefault(c => c.Name == part);
            if (child == null)
            {
                child = new VcsTreeNode
                {
                    Name = part,
                    FullPath = pathSoFar,
                    IsDirectory = !isLastPart,
                    FileStatus = isLastPart ? file : null,
                    Parent = currentNode,
                    Id = nextId++
                };
                currentNode.Children.Add(child);
            }
            
            currentNode = child;
        }
    }
    
    // 按名称排序：文件夹在前，文件在后
    SortTreeRecursively(root);
    
    return root;
}

private void SortTreeRecursively(VcsTreeNode node)
{
    if (node.Children.Count == 0) return;
    
    node.Children = node.Children
        .OrderBy(n => !n.IsDirectory)  // 文件夹优先
        .ThenBy(n => n.Name)           // 按名称排序
        .ToList();
    
    foreach (var child in node.Children)
    {
        SortTreeRecursively(child);
    }
}
```

---

## 3. Unity TreeView 实现

### 3.1 TreeView 初始化

Unity 2022.3+ 支持 UI Toolkit TreeView。关键 API：

```csharp
private TreeView _statusTreeView;
private VcsTreeNode _treeRoot;
private Dictionary<int, VcsTreeNode> _nodeById = new Dictionary<int, VcsTreeNode>();

private void BuildStatusTreeView()
{
    _statusTreeView = new TreeView();
    _statusTreeView.AddToClassList("vcs-tree-view");
    
    // 设置 TreeView 的数据源和渲染回调
    _statusTreeView.makeItem = MakeTreeItem;
    _statusTreeView.bindItem = BindTreeItem;
    _statusTreeView.unbindItem = UnbindTreeItem;
    
    // 设置选择模式
    _statusTreeView.selectionType = SelectionType.Multiple;
    _statusTreeView.selectionChanged += OnTreeSelectionChanged;
    
    // 注册右键菜单
    _statusTreeView.AddManipulator(new ContextualMenuManipulator(BuildTreeContextMenu));
    
    // 添加到 UI
    statusSection.Add(_statusTreeView);
}
```

### 3.2 TreeView 数据绑定

```csharp
private void UpdateTreeView(List<VcsFileStatus> files)
{
    // 构建树
    _treeRoot = BuildTree(files);
    _nodeById.Clear();
    CollectNodesById(_treeRoot, _nodeById);
    
    // 设置 TreeView 数据源
    var rootItems = _treeRoot.Children.Select(c => 
        TreeViewItemData<VcsTreeNode>.Create(c.Id, c, GetChildrenIds(c))
    ).ToList();
    
    _statusTreeView.SetRootItems(rootItems);
    _statusTreeView.Rebuild();
}

private IEnumerable<TreeViewItemData<VcsTreeNode>> GetChildrenIds(VcsTreeNode node)
{
    return node.Children.Select(c => 
        TreeViewItemData<VcsTreeNode>.Create(c.Id, c, GetChildrenIds(c))
    );
}

private void CollectNodesById(VcsTreeNode node, Dictionary<int, VcsTreeNode> dict)
{
    dict[node.Id] = node;
    foreach (var child in node.Children)
    {
        CollectNodesById(child, dict);
    }
}
```

### 3.3 TreeView Item 渲染

```csharp
private VisualElement MakeTreeItem()
{
    var item = new VisualElement();
    item.style.flexDirection = FlexDirection.Row;
    item.style.alignItems = Align.Center;
    item.style.paddingLeft = 4;
    item.style.paddingTop = 2;
    item.style.paddingBottom = 2;
    
    // 图标
    var icon = new Label();
    icon.name = "icon";
    icon.style.marginRight = 4;
    icon.style.fontSize = 14;
    item.Add(icon);
    
    // 状态徽章（仅文件）
    var badge = new Label();
    badge.name = "badge";
    badge.AddToClassList("status-badge");
    badge.style.display = DisplayStyle.None;
    item.Add(badge);
    
    // 名称
    var label = new Label();
    label.name = "label";
    label.style.flexGrow = 1;
    item.Add(label);
    
    return item;
}

private void BindTreeItem(VisualElement element, int index)
{
    var itemData = _statusTreeView.GetItemDataForIndex<VcsTreeNode>(index);
    if (itemData == null) return;
    
    var node = itemData;
    
    var icon = element.Q<Label>("icon");
    var badge = element.Q<Label>("badge");
    var label = element.Q<Label>("label");
    
    // 设置图标
    icon.text = node.IsDirectory ? "📁" : "📄";
    
    // 设置名称
    label.text = node.Name;
    
    // 设置状态徽章（仅文件）
    if (!node.IsDirectory && node.FileStatus != null)
    {
        badge.style.display = DisplayStyle.Flex;
        badge.text = GetStateBadge(node.FileStatus.State);
        badge.RemoveFromClassList("state-modified");
        badge.RemoveFromClassList("state-added");
        badge.RemoveFromClassList("state-deleted");
        badge.RemoveFromClassList("state-conflicted");
        badge.RemoveFromClassList("state-untracked");
        badge.AddToClassList($"state-{node.FileStatus.State.ToString().ToLowerInvariant()}");
    }
    else
    {
        badge.style.display = DisplayStyle.None;
    }
    
    // 存储节点引用
    element.userData = node;
}

private void UnbindTreeItem(VisualElement element, int index)
{
    element.userData = null;
}
```

---

## 4. 功能迁移

### 4.1 选择功能

```csharp
private HashSet<int> _selectedNodeIds = new HashSet<int>();

private void OnTreeSelectionChanged(IEnumerable<object> selectedItems)
{
    _selectedNodeIds.Clear();
    
    foreach (var item in selectedItems)
    {
        if (item is VcsTreeNode node)
        {
            _selectedNodeIds.Add(node.Id);
        }
    }
    
    // 更新 UI 状态
    UpdateSelectionDependentUI();
}

private List<VcsTreeNode> GetSelectedNodes()
{
    return _selectedNodeIds
        .Select(id => _nodeById.TryGetValue(id, out var node) ? node : null)
        .Where(n => n != null)
        .ToList();
}

private List<string> GetSelectedFilePaths()
{
    return GetSelectedNodes()
        .Where(n => !n.IsDirectory)
        .Select(n => n.FullPath)
        .ToList();
}
```

### 4.2 右键菜单

```csharp
private void BuildTreeContextMenu(ContextualMenuPopulateEvent evt)
{
    var clickedElement = evt.target as VisualElement;
    var node = clickedElement?.userData as VcsTreeNode;
    
    if (node == null) return;
    
    // 确保右键点击的节点被选中
    if (!_selectedNodeIds.Contains(node.Id))
    {
        _statusTreeView.SetSelection(node.Id);
    }
    
    var selectedNodes = GetSelectedNodes();
    var isSingleSelection = selectedNodes.Count == 1;
    var selectedNode = isSingleSelection ? selectedNodes[0] : null;
    
    if (selectedNode != null && selectedNode.IsDirectory)
    {
        // 文件夹右键菜单
        BuildDirectoryContextMenu(evt, selectedNode.FullPath);
    }
    else if (selectedNode != null && !selectedNode.IsDirectory)
    {
        // 文件右键菜单
        BuildFileContextMenu(evt, selectedNode.FileStatus);
    }
    else if (selectedNodes.Count > 1)
    {
        // 多选右键菜单
        BuildMultiSelectionContextMenu(evt, selectedNodes);
    }
}

private void BuildDirectoryContextMenu(ContextualMenuPopulateEvent evt, string directoryPath)
{
    // 复用现有的目录右键菜单逻辑
    evt.menu.AppendAction("Commit...", _ => OnCommitDirectoryClicked(directoryPath), 
        SupportsExternalFileTool("Commit") ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
    
    evt.menu.AppendAction("Update", _ => OnUpdateDirectoryClicked(directoryPath),
        SupportsExternalFileTool("Update") ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
    
    // ... 其他目录操作
}

private void BuildFileContextMenu(ContextualMenuPopulateEvent evt, VcsFileStatus file)
{
    // 复用现有的文件右键菜单逻辑
    BuildStatusItemContextMenu(evt, file);
}
```

### 4.3 多选操作

```csharp
private void BuildMultiSelectionContextMenu(ContextualMenuPopulateEvent evt, List<VcsTreeNode> selectedNodes)
{
    var selectedFiles = selectedNodes.Where(n => !n.IsDirectory).ToList();
    
    if (selectedFiles.Count > 0)
    {
        evt.menu.AppendAction($"Revert {selectedFiles.Count} File(s)", _ => 
        {
            var filePaths = selectedFiles.Select(n => n.FullPath).ToList();
            RevertFilesAsync(filePaths, 
                $"Revert {filePaths.Count} Selected File(s)?",
                $"This will discard local changes in the selected {filePaths.Count} file(s).\n\nThis action cannot be undone.",
                "Revert Selected");
        });
        
        // ... 其他批量操作
    }
}
```

---

## 5. 性能优化

### 5.1 虚拟化渲染

Unity TreeView 默认支持虚拟化渲染（只渲染可见项），无需额外配置。

### 5.2 增量更新

```csharp
private void UpdateTreeViewIncremental(List<VcsFileStatus> newFiles)
{
    // 如果文件列表变化不大，可以增量更新而不是完全重建
    var oldPaths = new HashSet<string>(_currentFiles.Select(f => f.FilePath));
    var newPaths = new HashSet<string>(newFiles.Select(f => f.FilePath));
    
    var added = newPaths.Except(oldPaths).ToList();
    var removed = oldPaths.Except(newPaths).ToList();
    var changed = newFiles.Where(f => oldPaths.Contains(f.FilePath)).ToList();
    
    if (added.Count + removed.Count < 10 && changed.Count < 20)
    {
        // 增量更新
        // ... 实现增量更新逻辑
    }
    else
    {
        // 完全重建
        UpdateTreeView(newFiles);
    }
}
```

---

## 6. 样式调整

### 6.1 USS 样式

```css
.vcs-tree-view {
    flex-grow: 1;
    min-height: 200px;
}

.vcs-tree-view .unity-tree-view__item {
    padding-left: 4px;
    padding-right: 4px;
}

.vcs-tree-view .unity-tree-view__item:hover {
    background-color: rgba(255, 255, 255, 0.05);
}

.vcs-tree-view .unity-tree-view__item--selected {
    background-color: rgba(68, 138, 255, 0.3);
}

.vcs-tree-view .status-badge {
    margin-left: 4px;
    margin-right: 4px;
    padding: 2px 6px;
    border-radius: 3px;
    font-size: 10px;
    font-weight: bold;
}

.vcs-tree-view .state-modified {
    background-color: rgba(255, 165, 0, 0.3);
    color: #FFA500;
}

.vcs-tree-view .state-added {
    background-color: rgba(0, 255, 0, 0.3);
    color: #00FF00;
}

.vcs-tree-view .state-deleted {
    background-color: rgba(255, 0, 0, 0.3);
    color: #FF0000;
}

.vcs-tree-view .state-conflicted {
    background-color: rgba(255, 0, 255, 0.3);
    color: #FF00FF;
}

.vcs-tree-view .state-untracked {
    background-color: rgba(128, 128, 128, 0.3);
    color: #808080;
}
```

---

## 7. 实现步骤

### Phase 1: 数据结构（1-2 小时）
1. 创建 `VcsTreeNode` 类
2. 实现 `BuildTree()` 方法
3. 实现 `SortTreeRecursively()` 方法
4. 单元测试树构建逻辑

### Phase 2: TreeView 基础（2-3 小时）
1. 在 `BuildUI()` 中创建 TreeView
2. 实现 `MakeTreeItem()`, `BindTreeItem()`, `UnbindTreeItem()`
3. 实现 `UpdateTreeView()` 数据绑定
4. 测试基本显示功能

### Phase 3: 功能迁移（3-4 小时）
1. 迁移选择功能
2. 迁移右键菜单（文件、文件夹、多选）
3. 迁移状态显示（徽章、图标）
4. 测试所有交互功能

### Phase 4: 样式和优化（1-2 小时）
1. 添加 USS 样式
2. 性能测试和优化
3. 边界情况处理
4. 最终测试

**总计：7-11 小时**

---

## 8. 风险和注意事项

### 8.1 Unity 版本兼容性
- TreeView API 在 Unity 2021.2+ 可用
- Unity 2022.3+ 的 TreeView 更稳定
- 需要测试目标 Unity 版本的 TreeView 行为

### 8.2 性能考虑
- 大型项目（1000+ 文件）时的渲染性能
- 树构建算法的时间复杂度：O(n * m)，n=文件数，m=平均路径深度
- 考虑缓存和增量更新

### 8.3 用户体验
- 默认展开/折叠策略（建议：展开第一层，折叠其余）
- 记住用户的展开/折叠状态
- 滚动位置保持

### 8.4 向后兼容
- 保留现有的 `_currentFiles` 数据
- 保留现有的右键菜单逻辑
- 确保外部工具调用不受影响

---

## 9. 测试计划

### 9.1 功能测试
- [ ] 树形结构正确显示
- [ ] 文件夹/文件图标正确
- [ ] 状态徽章正确显示
- [ ] 单选/多选功能正常
- [ ] 文件右键菜单正常
- [ ] 文件夹右键菜单正常
- [ ] 多选右键菜单正常
- [ ] 外部工具调用正常

### 9.2 性能测试
- [ ] 100 个文件：流畅
- [ ] 500 个文件：流畅
- [ ] 1000 个文件：可接受
- [ ] 5000 个文件：需要优化

### 9.3 边界测试
- [ ] 空文件列表
- [ ] 单个文件
- [ ] 深层嵌套路径（10+ 层）
- [ ] 特殊字符文件名
- [ ] 非常长的文件名

---

## 10. 回滚方案

如果 TreeView 实现遇到问题，可以回滚到当前的分离结构：

1. 保留当前的 `UpdateStatusList()` 方法
2. 使用 `#if TREEVIEW_ENABLED` 条件编译
3. 通过 Settings 提供切换选项

---

## 11. 后续优化

### 11.1 搜索/过滤
- 添加搜索框，按文件名过滤
- 按状态过滤（只显示 Modified、Added 等）

### 11.2 排序选项
- 按名称排序
- 按状态排序
- 按修改时间排序

### 11.3 视图选项
- 紧凑模式 vs 详细模式
- 显示/隐藏文件夹
- 显示/隐藏未修改文件

---

## 12. 总结

这个重构将显著提升 VCS Panel 的用户体验，使其更接近专业 VCS 工具的交互模式。关键挑战在于：

1. **Unity TreeView API 的学习曲线**：需要熟悉 UI Toolkit 的 TreeView 数据绑定模式
2. **性能优化**：确保大型项目中的流畅体验
3. **功能完整性**：确保所有现有功能正确迁移

建议采用**渐进式实现**：先实现基本的树形显示，然后逐步迁移功能，最后优化性能和样式。
