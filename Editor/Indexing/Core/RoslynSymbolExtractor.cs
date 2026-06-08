using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AgentCore.Editor.Components.Indexing.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AgentCore.Editor.Components.Indexing.Core
{
    /// <summary>
    /// 使用 Roslyn SyntaxTree（语法树级，不使用 SemanticModel）从 C# 文件中提取符号信息。
    ///
    /// 提取的符号类型：class, interface, struct, enum, method, property, field, event, constructor, delegate。
    /// 不提取：方法体内局部变量、lambda、匿名类型、using 指令、attribute 参数值。
    /// </summary>
    public static class RoslynSymbolExtractor
    {
        /// <summary>
        /// 从指定文件提取所有符号。
        /// </summary>
        /// <param name="filePath">C# 文件绝对路径。</param>
        /// <param name="fileId">已持久化的文件 ID（用于关联符号记录）。</param>
        /// <param name="root">该文件所属的 IndexRoot（用于填充 Scope 冗余字段）。</param>
        /// <param name="workspace">当前 IndexWorkspace（用于填充 BranchId 等字段）。</param>
        /// <returns>提取结果，包含符号列表和文件元数据。</returns>
        public static ExtractionResult ExtractFromFile(
            string filePath,
            int fileId,
            IndexRoot root,
            IndexWorkspace workspace)
        {
            if (string.IsNullOrEmpty(filePath))
                return ExtractionResult.Fail(filePath, "File path is null or empty.");

            if (!File.Exists(filePath))
                return ExtractionResult.Fail(filePath, $"File not found: {filePath}");

            string sourceText;
            try
            {
                sourceText = File.ReadAllText(filePath, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                return ExtractionResult.Fail(filePath, $"Failed to read file: {ex.Message}");
            }

            // 计算内容 hash（MD5，用于增量索引变更检测）
            var contentHash = ComputeMd5(sourceText);
            var fileInfo = new FileInfo(filePath);
            var lastModified = fileInfo.LastWriteTimeUtc.Ticks;
            var fileSize = fileInfo.Length;

            SyntaxTree syntaxTree;
            try
            {
                syntaxTree = CSharpSyntaxTree.ParseText(sourceText);
            }
            catch (Exception ex)
            {
                return ExtractionResult.Fail(filePath, $"Roslyn parse failed: {ex.Message}",
                    contentHash, lastModified, fileSize);
            }

            var root2 = syntaxTree.GetRoot();
            var symbols = new List<SymbolInfo>();
            var lines = sourceText.Split('\n');

            // 提取所有命名空间声明（支持嵌套和 file-scoped）
            var namespaceMap = BuildNamespaceMap(root2);

            // 遍历所有类型声明
            ExtractTypeDeclarations(root2, fileId, filePath, root, workspace, namespaceMap, lines, symbols);

            var indexedFile = new IndexedFile
            {
                Id = fileId,
                FilePath = filePath.Replace('\\', '/'),
                RelativeToRoot = ComputeRelativePath(filePath, root?.RootPath),
                ContentHash = contentHash,
                LastModified = lastModified,
                LastIndexed = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                FileSize = fileSize,
                HasErrors = false,
                SymbolCount = symbols.Count,
            };

            return ExtractionResult.Success(indexedFile, symbols, syntaxTree);
        }

        // ── 类型声明提取 ──────────────────────────────────────────────────────────

        private static void ExtractTypeDeclarations(
            SyntaxNode rootNode,
            int fileId,
            string filePath,
            IndexRoot root,
            IndexWorkspace workspace,
            Dictionary<SyntaxNode, string> namespaceMap,
            string[] lines,
            List<SymbolInfo> symbols)
        {
            foreach (var node in rootNode.DescendantNodes())
            {
                switch (node)
                {
                    case ClassDeclarationSyntax cls:
                        symbols.Add(ExtractClass(cls, fileId, filePath, root, workspace, namespaceMap, lines));
                        break;
                    case InterfaceDeclarationSyntax iface:
                        symbols.Add(ExtractInterface(iface, fileId, filePath, root, workspace, namespaceMap, lines));
                        break;
                    case StructDeclarationSyntax str:
                        symbols.Add(ExtractStruct(str, fileId, filePath, root, workspace, namespaceMap, lines));
                        break;
                    case EnumDeclarationSyntax enm:
                        symbols.Add(ExtractEnum(enm, fileId, filePath, root, workspace, namespaceMap, lines));
                        break;
                    case DelegateDeclarationSyntax del:
                        symbols.Add(ExtractDelegate(del, fileId, filePath, root, workspace, namespaceMap, lines));
                        break;
                    case MethodDeclarationSyntax method:
                        // 只提取直接属于类型的方法（不提取嵌套在方法体内的局部函数）
                        if (method.Parent is TypeDeclarationSyntax)
                            symbols.Add(ExtractMethod(method, fileId, filePath, root, workspace, namespaceMap, lines));
                        break;
                    case PropertyDeclarationSyntax prop:
                        if (prop.Parent is TypeDeclarationSyntax)
                            symbols.Add(ExtractProperty(prop, fileId, filePath, root, workspace, namespaceMap, lines));
                        break;
                    case FieldDeclarationSyntax field:
                        if (field.Parent is TypeDeclarationSyntax)
                        {
                            foreach (var variable in field.Declaration.Variables)
                                symbols.Add(ExtractField(field, variable, fileId, filePath, root, workspace, namespaceMap, lines));
                        }
                        break;
                    case EventDeclarationSyntax evt:
                        if (evt.Parent is TypeDeclarationSyntax)
                            symbols.Add(ExtractEvent(evt, fileId, filePath, root, workspace, namespaceMap, lines));
                        break;
                    case EventFieldDeclarationSyntax evtField:
                        if (evtField.Parent is TypeDeclarationSyntax)
                        {
                            foreach (var variable in evtField.Declaration.Variables)
                                symbols.Add(ExtractEventField(evtField, variable, fileId, filePath, root, workspace, namespaceMap, lines));
                        }
                        break;
                    case ConstructorDeclarationSyntax ctor:
                        if (ctor.Parent is TypeDeclarationSyntax)
                            symbols.Add(ExtractConstructor(ctor, fileId, filePath, root, workspace, namespaceMap, lines));
                        break;
                }
            }
        }

        // ── 各类型提取方法 ────────────────────────────────────────────────────────

        private static SymbolInfo ExtractClass(
            ClassDeclarationSyntax node, int fileId, string filePath,
            IndexRoot root, IndexWorkspace workspace,
            Dictionary<SyntaxNode, string> namespaceMap, string[] lines)
        {
            var ns = GetNamespace(node, namespaceMap);
            var name = node.Identifier.Text;
            var baseTypes = ExtractBaseTypes(node.BaseList);
            var genericParams = ExtractGenericParams(node.TypeParameterList);
            var modifiers = node.Modifiers;

            return CreateSymbol(fileId, filePath, root, workspace, "class", name, ns,
                GetAccessibility(modifiers),
                isStatic: modifiers.Any(SyntaxKind.StaticKeyword),
                isAbstract: modifiers.Any(SyntaxKind.AbstractKeyword),
                isPartial: modifiers.Any(SyntaxKind.PartialKeyword),
                baseTypes: baseTypes,
                genericParams: genericParams,
                lineNumber: node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                lines: lines,
                declarationNode: node);
        }

        private static SymbolInfo ExtractInterface(
            InterfaceDeclarationSyntax node, int fileId, string filePath,
            IndexRoot root, IndexWorkspace workspace,
            Dictionary<SyntaxNode, string> namespaceMap, string[] lines)
        {
            var ns = GetNamespace(node, namespaceMap);
            var name = node.Identifier.Text;
            var baseTypes = ExtractBaseTypes(node.BaseList);
            var genericParams = ExtractGenericParams(node.TypeParameterList);
            var modifiers = node.Modifiers;

            return CreateSymbol(fileId, filePath, root, workspace, "interface", name, ns,
                GetAccessibility(modifiers),
                isPartial: modifiers.Any(SyntaxKind.PartialKeyword),
                baseTypes: baseTypes,
                genericParams: genericParams,
                lineNumber: node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                lines: lines,
                declarationNode: node);
        }

        private static SymbolInfo ExtractStruct(
            StructDeclarationSyntax node, int fileId, string filePath,
            IndexRoot root, IndexWorkspace workspace,
            Dictionary<SyntaxNode, string> namespaceMap, string[] lines)
        {
            var ns = GetNamespace(node, namespaceMap);
            var name = node.Identifier.Text;
            var baseTypes = ExtractBaseTypes(node.BaseList);
            var genericParams = ExtractGenericParams(node.TypeParameterList);
            var modifiers = node.Modifiers;

            return CreateSymbol(fileId, filePath, root, workspace, "struct", name, ns,
                GetAccessibility(modifiers),
                isPartial: modifiers.Any(SyntaxKind.PartialKeyword),
                baseTypes: baseTypes,
                genericParams: genericParams,
                lineNumber: node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                lines: lines,
                declarationNode: node);
        }

        private static SymbolInfo ExtractEnum(
            EnumDeclarationSyntax node, int fileId, string filePath,
            IndexRoot root, IndexWorkspace workspace,
            Dictionary<SyntaxNode, string> namespaceMap, string[] lines)
        {
            var ns = GetNamespace(node, namespaceMap);
            var name = node.Identifier.Text;
            var modifiers = node.Modifiers;

            return CreateSymbol(fileId, filePath, root, workspace, "enum", name, ns,
                GetAccessibility(modifiers),
                lineNumber: node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                lines: lines,
                declarationNode: node);
        }

        private static SymbolInfo ExtractDelegate(
            DelegateDeclarationSyntax node, int fileId, string filePath,
            IndexRoot root, IndexWorkspace workspace,
            Dictionary<SyntaxNode, string> namespaceMap, string[] lines)
        {
            var ns = GetNamespace(node, namespaceMap);
            var name = node.Identifier.Text;
            var modifiers = node.Modifiers;
            var returnType = node.ReturnType.ToString();
            var parameters = node.ParameterList.ToString();
            var genericParams = ExtractGenericParams(node.TypeParameterList);

            return CreateSymbol(fileId, filePath, root, workspace, "delegate", name, ns,
                GetAccessibility(modifiers),
                returnType: returnType,
                parameters: parameters,
                genericParams: genericParams,
                lineNumber: node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                lines: lines,
                declarationNode: node);
        }

        private static SymbolInfo ExtractMethod(
            MethodDeclarationSyntax node, int fileId, string filePath,
            IndexRoot root, IndexWorkspace workspace,
            Dictionary<SyntaxNode, string> namespaceMap, string[] lines)
        {
            var ns = GetNamespace(node, namespaceMap);
            var name = node.Identifier.Text;
            var modifiers = node.Modifiers;
            var returnType = node.ReturnType.ToString();
            var parameters = node.ParameterList.ToString();

            return CreateSymbol(fileId, filePath, root, workspace, "method", name, ns,
                GetAccessibility(modifiers),
                isStatic: modifiers.Any(SyntaxKind.StaticKeyword),
                isAbstract: modifiers.Any(SyntaxKind.AbstractKeyword),
                isVirtual: modifiers.Any(SyntaxKind.VirtualKeyword),
                isOverride: modifiers.Any(SyntaxKind.OverrideKeyword),
                returnType: returnType,
                parameters: parameters,
                lineNumber: node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                lines: lines,
                declarationNode: node);
        }

        private static SymbolInfo ExtractProperty(
            PropertyDeclarationSyntax node, int fileId, string filePath,
            IndexRoot root, IndexWorkspace workspace,
            Dictionary<SyntaxNode, string> namespaceMap, string[] lines)
        {
            var ns = GetNamespace(node, namespaceMap);
            var name = node.Identifier.Text;
            var modifiers = node.Modifiers;
            var returnType = node.Type.ToString();

            return CreateSymbol(fileId, filePath, root, workspace, "property", name, ns,
                GetAccessibility(modifiers),
                isStatic: modifiers.Any(SyntaxKind.StaticKeyword),
                returnType: returnType,
                lineNumber: node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                lines: lines,
                declarationNode: node);
        }

        private static SymbolInfo ExtractField(
            FieldDeclarationSyntax node, VariableDeclaratorSyntax variable,
            int fileId, string filePath,
            IndexRoot root, IndexWorkspace workspace,
            Dictionary<SyntaxNode, string> namespaceMap, string[] lines)
        {
            var ns = GetNamespace(node, namespaceMap);
            var name = variable.Identifier.Text;
            var modifiers = node.Modifiers;
            var returnType = node.Declaration.Type.ToString();

            return CreateSymbol(fileId, filePath, root, workspace, "field", name, ns,
                GetAccessibility(modifiers),
                isStatic: modifiers.Any(SyntaxKind.StaticKeyword),
                isReadOnly: modifiers.Any(SyntaxKind.ReadOnlyKeyword),
                isConst: modifiers.Any(SyntaxKind.ConstKeyword),
                returnType: returnType,
                lineNumber: node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                lines: lines,
                declarationNode: node);
        }

        private static SymbolInfo ExtractEvent(
            EventDeclarationSyntax node, int fileId, string filePath,
            IndexRoot root, IndexWorkspace workspace,
            Dictionary<SyntaxNode, string> namespaceMap, string[] lines)
        {
            var ns = GetNamespace(node, namespaceMap);
            var name = node.Identifier.Text;
            var modifiers = node.Modifiers;
            var returnType = node.Type.ToString();

            return CreateSymbol(fileId, filePath, root, workspace, "event", name, ns,
                GetAccessibility(modifiers),
                returnType: returnType,
                lineNumber: node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                lines: lines,
                declarationNode: node);
        }

        private static SymbolInfo ExtractEventField(
            EventFieldDeclarationSyntax node, VariableDeclaratorSyntax variable,
            int fileId, string filePath,
            IndexRoot root, IndexWorkspace workspace,
            Dictionary<SyntaxNode, string> namespaceMap, string[] lines)
        {
            var ns = GetNamespace(node, namespaceMap);
            var name = variable.Identifier.Text;
            var modifiers = node.Modifiers;
            var returnType = node.Declaration.Type.ToString();

            return CreateSymbol(fileId, filePath, root, workspace, "event", name, ns,
                GetAccessibility(modifiers),
                returnType: returnType,
                lineNumber: node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                lines: lines,
                declarationNode: node);
        }

        private static SymbolInfo ExtractConstructor(
            ConstructorDeclarationSyntax node, int fileId, string filePath,
            IndexRoot root, IndexWorkspace workspace,
            Dictionary<SyntaxNode, string> namespaceMap, string[] lines)
        {
            var ns = GetNamespace(node, namespaceMap);
            var name = node.Identifier.Text;
            var modifiers = node.Modifiers;
            var parameters = node.ParameterList.ToString();

            return CreateSymbol(fileId, filePath, root, workspace, "constructor", name, ns,
                GetAccessibility(modifiers),
                parameters: parameters,
                lineNumber: node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                lines: lines,
                declarationNode: node);
        }

        // ── 工厂方法 ──────────────────────────────────────────────────────────────

        private static SymbolInfo CreateSymbol(
            int fileId, string filePath, IndexRoot root, IndexWorkspace workspace,
            string symbolType, string name, string ns, string accessibility,
            bool isStatic = false, bool isAbstract = false, bool isPartial = false,
            bool isVirtual = false, bool isOverride = false,
            bool isReadOnly = false, bool isConst = false,
            string returnType = null, string parameters = null,
            string[] baseTypes = null, string[] genericParams = null,
            int lineNumber = 0, string[] lines = null, SyntaxNode declarationNode = null)
        {
            var fullName = string.IsNullOrEmpty(ns) || ns == "<global>"
                ? name
                : $"{ns}.{name}";

            var snippet = GenerateDeclarationSnippet(lines, lineNumber - 1, declarationNode);

            return new SymbolInfo
            {
                FileId = fileId,
                FilePath = filePath.Replace('\\', '/'),
                SymbolType = symbolType,
                Name = name,
                Namespace = string.IsNullOrEmpty(ns) ? "<global>" : ns,
                FullName = fullName,
                Accessibility = accessibility,
                IsStatic = isStatic,
                IsAbstract = isAbstract,
                IsPartial = isPartial,
                IsVirtual = isVirtual,
                IsOverride = isOverride,
                IsReadOnly = isReadOnly,
                IsConst = isConst,
                ReturnType = returnType,
                Parameters = parameters,
                BaseTypes = baseTypes != null ? string.Join(", ", baseTypes) : null,
                GenericParams = genericParams != null ? string.Join(", ", genericParams) : null,
                DeclarationSnippet = snippet,
                LineNumber = lineNumber,
                // 冗余 Scope 字段（快速过滤用）
                ScopeType = root?.ScopeType ?? IndexScopeType.Unknown,
                ScopeName = root?.ScopeName ?? string.Empty,
                Role = root?.Role ?? IndexRootRole.ReadOnlyReference,
                ReadOnly = root?.ReadOnly ?? true,
                BranchId = workspace?.BranchId ?? string.Empty,
            };
        }

        // ── 辅助方法 ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 构建 SyntaxNode → 命名空间字符串 的映射表（支持嵌套命名空间）。
        /// </summary>
        private static Dictionary<SyntaxNode, string> BuildNamespaceMap(SyntaxNode root)
        {
            var map = new Dictionary<SyntaxNode, string>();

            foreach (var node in root.DescendantNodesAndSelf())
            {
                string ns = null;
                if (node is NamespaceDeclarationSyntax nsDecl)
                    ns = nsDecl.Name.ToString();
                else if (node is FileScopedNamespaceDeclarationSyntax fsNs)
                    ns = fsNs.Name.ToString();

                if (ns != null)
                    map[node] = ns;
            }

            return map;
        }

        /// <summary>
        /// 获取节点所在的命名空间（向上查找最近的 namespace 声明）。
        /// </summary>
        private static string GetNamespace(SyntaxNode node, Dictionary<SyntaxNode, string> namespaceMap)
        {
            var current = node.Parent;
            var parts = new List<string>();

            while (current != null)
            {
                if (namespaceMap.TryGetValue(current, out var ns))
                    parts.Insert(0, ns);
                current = current.Parent;
            }

            return parts.Count > 0 ? string.Join(".", parts) : "<global>";
        }

        /// <summary>
        /// 从 BaseListSyntax 提取基类/接口名称列表。
        /// </summary>
        private static string[] ExtractBaseTypes(BaseListSyntax baseList)
        {
            if (baseList == null) return null;
            var result = new List<string>();
            foreach (var type in baseList.Types)
                result.Add(type.Type.ToString());
            return result.Count > 0 ? result.ToArray() : null;
        }

        /// <summary>
        /// 从 TypeParameterListSyntax 提取泛型参数名称列表。
        /// </summary>
        private static string[] ExtractGenericParams(TypeParameterListSyntax typeParams)
        {
            if (typeParams == null) return null;
            var result = new List<string>();
            foreach (var param in typeParams.Parameters)
                result.Add(param.Identifier.Text);
            return result.Count > 0 ? result.ToArray() : null;
        }

        /// <summary>
        /// 从修饰符列表推断可访问性字符串。
        /// </summary>
        private static string GetAccessibility(SyntaxTokenList modifiers)
        {
            if (modifiers.Any(SyntaxKind.PublicKeyword)) return "public";
            if (modifiers.Any(SyntaxKind.ProtectedKeyword) && modifiers.Any(SyntaxKind.InternalKeyword)) return "protected internal";
            if (modifiers.Any(SyntaxKind.PrivateKeyword) && modifiers.Any(SyntaxKind.ProtectedKeyword)) return "private protected";
            if (modifiers.Any(SyntaxKind.ProtectedKeyword)) return "protected";
            if (modifiers.Any(SyntaxKind.InternalKeyword)) return "internal";
            if (modifiers.Any(SyntaxKind.PrivateKeyword)) return "private";
            return "private"; // C# 默认
        }

        /// <summary>
        /// 生成声明片段（取声明行 ± 2 行，最多 5 行，去除方法体，保留 XML 注释）。
        /// </summary>
        private static string GenerateDeclarationSnippet(string[] lines, int zeroBasedLine, SyntaxNode node)
        {
            if (lines == null || zeroBasedLine < 0 || zeroBasedLine >= lines.Length)
                return null;

            // 向上查找 XML 文档注释（/// 行）
            var startLine = zeroBasedLine;
            while (startLine > 0 && lines[startLine - 1].TrimStart().StartsWith("///"))
                startLine--;

            // 最多取 5 行
            var endLine = Math.Min(startLine + 4, lines.Length - 1);

            var sb = new StringBuilder();
            for (var i = startLine; i <= endLine; i++)
            {
                var line = lines[i];
                // 截断超过 200 字符的行
                if (line.Length > 200)
                    line = line.Substring(0, 200) + "...";
                sb.AppendLine(line);

                // 遇到方法体开始 { 或语句结束 ; 时停止（保留签名）
                var trimmed = line.TrimEnd();
                if (i >= zeroBasedLine && (trimmed.EndsWith("{") || trimmed.EndsWith(";")))
                    break;
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// 计算文件相对于 Root 的路径。
        /// </summary>
        private static string ComputeRelativePath(string filePath, string rootPath)
        {
            if (string.IsNullOrEmpty(rootPath)) return filePath;
            var normalizedFile = filePath.Replace('\\', '/');
            var normalizedRoot = rootPath.Replace('\\', '/').TrimEnd('/') + '/';
            return normalizedFile.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                ? normalizedFile.Substring(normalizedRoot.Length)
                : normalizedFile;
        }

        /// <summary>
        /// 计算字符串的 MD5 hash（用于内容变更检测）。
        /// </summary>
        private static string ComputeMd5(string content)
        {
            using (var md5 = MD5.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(content);
                var hash = md5.ComputeHash(bytes);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }
    }

    /// <summary>
    /// 单文件符号提取结果。
    /// </summary>
    public sealed class ExtractionResult
    {
        /// <summary>是否提取成功（false 表示文件读取或解析失败）。</summary>
        public bool IsSuccess { get; private set; }

        /// <summary>文件路径。</summary>
        public string FilePath { get; private set; }

        /// <summary>错误信息（仅 IsSuccess=false 时有值）。</summary>
        public string ErrorMessage { get; private set; }

        /// <summary>文件元数据（包含 ContentHash、LastModified 等）。</summary>
        public IndexedFile File { get; private set; }

        /// <summary>提取到的符号列表。</summary>
        public IReadOnlyList<SymbolInfo> Symbols { get; private set; }

        /// <summary>
        /// 已解析的 SyntaxTree（供 DependencyExtractor 复用，避免重复解析）。
        /// 仅 IsSuccess=true 时有值。
        /// </summary>
        public SyntaxTree SyntaxTree { get; private set; }

        /// <summary>
        /// 创建成功结果。
        /// </summary>
        public static ExtractionResult Success(IndexedFile file, List<SymbolInfo> symbols, SyntaxTree syntaxTree = null)
        {
            return new ExtractionResult
            {
                IsSuccess = true,
                FilePath = file.FilePath,
                File = file,
                Symbols = symbols,
                SyntaxTree = syntaxTree,
            };
        }

        /// <summary>
        /// 创建失败结果（文件读取失败，无内容 hash）。
        /// </summary>
        public static ExtractionResult Fail(string filePath, string error)
        {
            return new ExtractionResult
            {
                IsSuccess = false,
                FilePath = filePath,
                ErrorMessage = error,
                File = new IndexedFile
                {
                    FilePath = filePath?.Replace('\\', '/') ?? string.Empty,
                    HasErrors = true,
                    ErrorMessage = error,
                    LastIndexed = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                },
                Symbols = Array.Empty<SymbolInfo>(),
            };
        }

        /// <summary>
        /// 创建失败结果（文件可读但解析失败，有内容 hash）。
        /// </summary>
        public static ExtractionResult Fail(string filePath, string error,
            string contentHash, long lastModified, long fileSize)
        {
            return new ExtractionResult
            {
                IsSuccess = false,
                FilePath = filePath,
                ErrorMessage = error,
                File = new IndexedFile
                {
                    FilePath = filePath?.Replace('\\', '/') ?? string.Empty,
                    ContentHash = contentHash,
                    LastModified = lastModified,
                    FileSize = fileSize,
                    HasErrors = true,
                    ErrorMessage = error,
                    LastIndexed = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                },
                Symbols = Array.Empty<SymbolInfo>(),
            };
        }
    }
}
