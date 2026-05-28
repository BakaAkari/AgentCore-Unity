using System.Collections.Generic;
using System.Linq;
using AgentCore.Editor.Components.VCS.Tools;

namespace AgentCore.Editor.Components.VCS.UI
{
    /// <summary>
    /// VCS 树节点 - 用于 TreeView 的层级数据结构
    /// </summary>
    public class VcsTreeNode
    {
        /// <summary>
        /// 节点显示名称（文件名或目录名）
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// 完整路径（相对于项目根目录）
        /// </summary>
        public string FullPath { get; set; }

        /// <summary>
        /// 是否为目录节点
        /// </summary>
        public bool IsDirectory { get; set; }

        /// <summary>
        /// 文件状态（仅文件节点有效，目录节点为 null）
        /// </summary>
        public VcsFileStatus FileStatus { get; set; }

        /// <summary>
        /// 子节点列表
        /// </summary>
        public List<VcsTreeNode> Children { get; set; }

        /// <summary>
        /// 父节点引用
        /// </summary>
        public VcsTreeNode Parent { get; set; }

        /// <summary>
        /// TreeView 需要的唯一 ID
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// 目录下的文件变更数量（仅目录节点有效）
        /// </summary>
        public int ChangeCount { get; set; }

        public VcsTreeNode()
        {
            Children = new List<VcsTreeNode>();
        }

        /// <summary>
        /// 获取节点深度（根节点深度为 0）
        /// </summary>
        public int GetDepth()
        {
            int depth = 0;
            var current = Parent;
            while (current != null)
            {
                depth++;
                current = current.Parent;
            }
            return depth;
        }

        /// <summary>
        /// 判断是否为叶子节点
        /// </summary>
        public bool IsLeaf => Children.Count == 0;

        /// <summary>
        /// 递归获取所有后代节点
        /// </summary>
        public IEnumerable<VcsTreeNode> GetAllDescendants()
        {
            foreach (var child in Children)
            {
                yield return child;
                foreach (var descendant in child.GetAllDescendants())
                {
                    yield return descendant;
                }
            }
        }

        /// <summary>
        /// 获取所有后代文件节点
        /// </summary>
        public IEnumerable<VcsTreeNode> GetAllFileNodes()
        {
            return GetAllDescendants().Where(n => !n.IsDirectory);
        }

        /// <summary>
        /// 获取节点路径（用于调试）
        /// </summary>
        public override string ToString()
        {
            return $"{(IsDirectory ? "[DIR]" : "[FILE]")} {FullPath} (ID: {Id})";
        }
    }

    /// <summary>
    /// VCS 树构建器 - 从扁平文件列表构建层级树结构
    /// </summary>
    public static class VcsTreeBuilder
    {
        private static int _nextId = 0;

        /// <summary>
        /// 从文件状态列表构建树结构
        /// </summary>
        /// <param name="files">文件状态列表</param>
        /// <returns>根节点列表（顶层目录和文件）</returns>
        public static List<VcsTreeNode> BuildTree(List<VcsFileStatus> files)
        {
            if (files == null || files.Count == 0)
            {
                return new List<VcsTreeNode>();
            }

            _nextId = 0;
            var root = new VcsTreeNode
            {
                Name = "<root>",
                FullPath = "",
                IsDirectory = true,
                Id = _nextId++
            };

            // 构建树结构
            foreach (var file in files)
            {
                AddFileToTree(root, file);
            }

            // 路径压缩：合并只有单个子目录且没有文件的目录节点（类似 VSCode）
            CompactPaths(root);

            // 计算每个目录的变更数量
            CalculateChangeCounts(root);

            // 排序并返回根节点的子节点
            SortTree(root);
            return root.Children;
        }

        /// <summary>
        /// 将文件添加到树中（自动创建中间目录）
        /// </summary>
        private static void AddFileToTree(VcsTreeNode root, VcsFileStatus file)
        {
            var pathParts = file.FilePath.Split('/', '\\');
            var currentNode = root;

            // 遍历路径的每一部分（除了最后的文件名）
            for (int i = 0; i < pathParts.Length - 1; i++)
            {
                var dirName = pathParts[i];
                if (string.IsNullOrEmpty(dirName))
                    continue;

                // 查找或创建目录节点
                var dirNode = currentNode.Children.FirstOrDefault(c => 
                    c.IsDirectory && c.Name == dirName);

                if (dirNode == null)
                {
                    var dirPath = string.Join("/", pathParts.Take(i + 1));
                    dirNode = new VcsTreeNode
                    {
                        Name = dirName,
                        FullPath = dirPath,
                        IsDirectory = true,
                        Parent = currentNode,
                        Id = _nextId++
                    };
                    currentNode.Children.Add(dirNode);
                }

                currentNode = dirNode;
            }

            // 添加文件节点
            var fileName = pathParts[pathParts.Length - 1];
            var fileNode = new VcsTreeNode
            {
                Name = fileName,
                FullPath = file.FilePath,
                IsDirectory = false,
                FileStatus = file,
                Parent = currentNode,
                Id = _nextId++
            };
            currentNode.Children.Add(fileNode);
        }

        /// <summary>
        /// 递归计算每个目录的变更数量
        /// </summary>
        private static int CalculateChangeCounts(VcsTreeNode node)
        {
            if (!node.IsDirectory)
            {
                return 1; // 文件节点计为 1
            }

            int count = 0;
            foreach (var child in node.Children)
            {
                count += CalculateChangeCounts(child);
            }

            node.ChangeCount = count;
            return count;
        }

        /// <summary>
        /// 递归排序树（目录优先，然后按名称排序）
        /// </summary>
        private static void SortTree(VcsTreeNode node)
        {
            if (node.Children.Count == 0)
                return;

            // 排序：目录优先，然后按名称排序
            node.Children = node.Children
                .OrderByDescending(n => n.IsDirectory)
                .ThenBy(n => n.Name, System.StringComparer.OrdinalIgnoreCase)
                .ToList();

            // 递归排序子节点
            foreach (var child in node.Children)
            {
                SortTree(child);
            }
        }

        /// <summary>
        /// 路径压缩：合并只有单个子目录且没有文件的目录节点
        /// 例如：A/ -> B/ -> C/file.txt 压缩为 A/B/C/file.txt
        /// </summary>
        private static void CompactPaths(VcsTreeNode node)
        {
            if (!node.IsDirectory)
                return;

            // 递归处理所有子节点
            for (int i = 0; i < node.Children.Count; i++)
            {
                var child = node.Children[i];
                if (child.IsDirectory)
                {
                    // 检查是否可以压缩：只有一个子节点且该子节点是目录
                    while (child.Children.Count == 1 && child.Children[0].IsDirectory)
                    {
                        var grandChild = child.Children[0];
                        
                        // 合并路径名称
                        child.Name = child.Name + "/" + grandChild.Name;
                        child.FullPath = grandChild.FullPath;
                         
                        // 将孙子节点提升为子节点
                        child.Children = grandChild.Children;
                        
                        // 更新父节点引用
                        foreach (var newChild in child.Children)
                        {
                            newChild.Parent = child;
                        }
                    }
                    
                    // 递归处理压缩后的子节点
                    CompactPaths(child);
                }
            }
        }

        /// <summary>
        /// 重置 ID 计数器（用于测试）
        /// </summary>
        public static void ResetIdCounter()
        {
            _nextId = 0;
        }

        /// <summary>
        /// 增量更新树结构（性能优化）
        /// </summary>
        /// <param name="existingRoots">现有的根节点列表</param>
        /// <param name="newFiles">新的文件状态列表</param>
        /// <returns>更新后的根节点列表</returns>
        public static List<VcsTreeNode> UpdateTree(List<VcsTreeNode> existingRoots, List<VcsFileStatus> newFiles)
        {
            // 简化实现：直接重建树
            // 未来可以优化为增量更新以提升性能
            return BuildTree(newFiles);
        }

        /// <summary>
        /// 扁平化树结构为列表（用于 TreeView 数据绑定）
        /// </summary>
        public static List<VcsTreeNode> FlattenTree(List<VcsTreeNode> roots)
        {
            var result = new List<VcsTreeNode>();
            foreach (var root in roots)
            {
                FlattenNode(root, result);
            }
            return result;
        }

        private static void FlattenNode(VcsTreeNode node, List<VcsTreeNode> result)
        {
            result.Add(node);
            foreach (var child in node.Children)
            {
                FlattenNode(child, result);
            }
        }
    }
}
