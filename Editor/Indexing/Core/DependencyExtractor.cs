using System;
using System.Collections.Generic;
using System.Linq;
using AgentCore.Editor.Components.Indexing.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AgentCore.Editor.Components.Indexing.Core
{
    /// <summary>
    /// 使用 Roslyn SyntaxTree（语法树级，不使用 SemanticModel）从 C# 文件中提取类型依赖关系。
    ///
    /// 提取的依赖类型（对应 <see cref="DependencyKind"/> 常量）：
    /// <list type="bullet">
    ///   <item>inheritance — 类继承基类</item>
    ///   <item>interface_impl — 类/结构体实现接口</item>
    ///   <item>field_type — 字段类型引用</item>
    ///   <item>method_param — 方法参数类型引用</item>
    ///   <item>method_return — 方法返回类型引用</item>
    ///   <item>attribute — 特性引用</item>
    ///   <item>generic_arg — 泛型参数约束引用</item>
    ///   <item>using_directive — using 指令（文件级依赖）</item>
    /// </list>
    ///
    /// 不提取：方法体内局部变量类型、lambda 参数、匿名类型、字面量。
    /// </summary>
    public static class DependencyExtractor
    {
        // ── 公开入口 ────────────────────────────────────────────────────────────

        /// <summary>
        /// 从已解析的 SyntaxTree 中提取所有依赖关系。
        /// </summary>
        /// <param name="tree">已解析的 Roslyn SyntaxTree。</param>
        /// <param name="workspaceId">所属 workspace ID（填充到每条记录）。</param>
        /// <param name="fileId">已持久化的文件 ID（填充到每条记录）。</param>
        /// <param name="symbolIdMap">
        /// 符号名称 → 数据库 ID 的映射（由 <see cref="RoslynSymbolExtractor"/> 提取后构建）。
        /// 用于将 <see cref="SymbolDependency.FromSymbolId"/> 和 <see cref="SymbolDependency.ToSymbolId"/> 关联到已知符号。
        /// 可为 null，此时两个 ID 字段均为 null。
        /// </param>
        /// <returns>提取到的依赖关系列表。</returns>
        public static List<SymbolDependency> ExtractFromTree(
            SyntaxTree tree,
            int workspaceId,
            int fileId,
            Dictionary<string, int> symbolIdMap = null)
        {
            if (tree == null) throw new ArgumentNullException(nameof(tree));

            var root = tree.GetRoot();
            var results = new List<SymbolDependency>();
            var map = symbolIdMap ?? new Dictionary<string, int>();

            // 1. using 指令（文件级）
            ExtractUsingDirectives(root, workspaceId, fileId, results);

            // 2. 类型声明内的依赖
            foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                ExtractTypeDeclarationDeps(typeDecl, workspaceId, fileId, map, results);
            }

            // 3. delegate 声明（顶层或嵌套）
            foreach (var delegateDecl in root.DescendantNodes().OfType<DelegateDeclarationSyntax>())
            {
                ExtractDelegateDeps(delegateDecl, workspaceId, fileId, map, results);
            }

            return results;
        }

        // ── 私有提取方法 ────────────────────────────────────────────────────────

        /// <summary>提取文件顶层 using 指令（文件级依赖，FromSymbolId = null）。</summary>
        private static void ExtractUsingDirectives(
            SyntaxNode root,
            int workspaceId,
            int fileId,
            List<SymbolDependency> results)
        {
            foreach (var usingDir in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
            {
                // 跳过 using static 和 using alias（只记录命名空间引用）
                if (usingDir.StaticKeyword.IsKind(SyntaxKind.StaticKeyword)) continue;
                if (usingDir.Alias != null) continue;

                var namespaceName = usingDir.Name?.ToString();
                if (string.IsNullOrWhiteSpace(namespaceName)) continue;

                results.Add(new SymbolDependency
                {
                    WorkspaceId = workspaceId,
                    FromFileId = fileId,
                    FromSymbolId = null,
                    ToTypeName = namespaceName,
                    ToSymbolId = null,
                    DependencyKind = DependencyKind.UsingDirective,
                    SourceLine = GetLine(usingDir)
                });
            }
        }

        /// <summary>提取类型声明（class/interface/struct/record）内的所有依赖。</summary>
        private static void ExtractTypeDeclarationDeps(
            TypeDeclarationSyntax typeDecl,
            int workspaceId,
            int fileId,
            Dictionary<string, int> symbolIdMap,
            List<SymbolDependency> results)
        {
            var typeName = GetQualifiedName(typeDecl);
            symbolIdMap.TryGetValue(typeName, out var fromSymbolId_raw);
            int? fromSymbolId = symbolIdMap.ContainsKey(typeName) ? (int?)fromSymbolId_raw : null;

            // 特性
            ExtractAttributeDeps(typeDecl.AttributeLists, workspaceId, fileId, fromSymbolId, symbolIdMap, results);

            // 基类 / 接口实现
            if (typeDecl.BaseList != null)
            {
                bool isInterface = typeDecl is InterfaceDeclarationSyntax;
                bool firstBase = true;

                foreach (var baseType in typeDecl.BaseList.Types)
                {
                    var baseTypeName = ExtractTypeName(baseType.Type);
                    if (string.IsNullOrWhiteSpace(baseTypeName)) continue;

                    // 对于 class/struct：第一个 base 通常是基类（非接口），其余是接口
                    // 对于 interface：所有 base 都是接口
                    string kind;
                    if (isInterface)
                    {
                        kind = DependencyKind.InterfaceImpl;
                    }
                    else if (firstBase && typeDecl is ClassDeclarationSyntax)
                    {
                        // 启发式：首字母大写且不以 'I' 开头 → 基类；否则接口
                        // 这是语法级近似，无法 100% 准确（SemanticModel 才能确定）
                        kind = IsLikelyInterface(baseTypeName)
                            ? DependencyKind.InterfaceImpl
                            : DependencyKind.Inheritance;
                    }
                    else
                    {
                        kind = DependencyKind.InterfaceImpl;
                    }

                    results.Add(new SymbolDependency
                    {
                        WorkspaceId = workspaceId,
                        FromFileId = fileId,
                        FromSymbolId = fromSymbolId,
                        ToTypeName = baseTypeName,
                        ToSymbolId = symbolIdMap.TryGetValue(baseTypeName, out var toId) ? (int?)toId : null,
                        DependencyKind = kind,
                        SourceLine = GetLine(baseType)
                    });

                    firstBase = false;
                }
            }

            // 泛型参数约束
            ExtractTypeParameterConstraints(typeDecl.ConstraintClauses, workspaceId, fileId, fromSymbolId, symbolIdMap, results);

            // 成员
            foreach (var member in typeDecl.Members)
            {
                ExtractMemberDeps(member, workspaceId, fileId, fromSymbolId, symbolIdMap, results);
            }
        }

        /// <summary>提取 delegate 声明的依赖（返回类型 + 参数类型 + 特性）。</summary>
        private static void ExtractDelegateDeps(
            DelegateDeclarationSyntax delegateDecl,
            int workspaceId,
            int fileId,
            Dictionary<string, int> symbolIdMap,
            List<SymbolDependency> results)
        {
            var delegateName = GetQualifiedName(delegateDecl);
            int? fromSymbolId = symbolIdMap.TryGetValue(delegateName, out var id) ? (int?)id : null;

            // 特性
            ExtractAttributeDeps(delegateDecl.AttributeLists, workspaceId, fileId, fromSymbolId, symbolIdMap, results);

            // 返回类型
            AddTypeRef(delegateDecl.ReturnType, DependencyKind.MethodReturn,
                workspaceId, fileId, fromSymbolId, symbolIdMap, results);

            // 参数类型
            foreach (var param in delegateDecl.ParameterList.Parameters)
            {
                if (param.Type != null)
                    AddTypeRef(param.Type, DependencyKind.MethodParam,
                        workspaceId, fileId, fromSymbolId, symbolIdMap, results);
            }
        }

        /// <summary>提取成员（字段、属性、方法、构造函数、事件）的依赖。</summary>
        private static void ExtractMemberDeps(
            MemberDeclarationSyntax member,
            int workspaceId,
            int fileId,
            int? fromSymbolId,
            Dictionary<string, int> symbolIdMap,
            List<SymbolDependency> results)
        {
            switch (member)
            {
                case FieldDeclarationSyntax field:
                    ExtractAttributeDeps(field.AttributeLists, workspaceId, fileId, fromSymbolId, symbolIdMap, results);
                    AddTypeRef(field.Declaration.Type, DependencyKind.FieldType,
                        workspaceId, fileId, fromSymbolId, symbolIdMap, results);
                    break;

                case PropertyDeclarationSyntax prop:
                    ExtractAttributeDeps(prop.AttributeLists, workspaceId, fileId, fromSymbolId, symbolIdMap, results);
                    AddTypeRef(prop.Type, DependencyKind.FieldType,
                        workspaceId, fileId, fromSymbolId, symbolIdMap, results);
                    break;

                case EventDeclarationSyntax evt:
                    ExtractAttributeDeps(evt.AttributeLists, workspaceId, fileId, fromSymbolId, symbolIdMap, results);
                    AddTypeRef(evt.Type, DependencyKind.FieldType,
                        workspaceId, fileId, fromSymbolId, symbolIdMap, results);
                    break;

                case EventFieldDeclarationSyntax evtField:
                    ExtractAttributeDeps(evtField.AttributeLists, workspaceId, fileId, fromSymbolId, symbolIdMap, results);
                    AddTypeRef(evtField.Declaration.Type, DependencyKind.FieldType,
                        workspaceId, fileId, fromSymbolId, symbolIdMap, results);
                    break;

                case MethodDeclarationSyntax method:
                    ExtractAttributeDeps(method.AttributeLists, workspaceId, fileId, fromSymbolId, symbolIdMap, results);
                    AddTypeRef(method.ReturnType, DependencyKind.MethodReturn,
                        workspaceId, fileId, fromSymbolId, symbolIdMap, results);
                    foreach (var param in method.ParameterList.Parameters)
                    {
                        if (param.Type != null)
                            AddTypeRef(param.Type, DependencyKind.MethodParam,
                                workspaceId, fileId, fromSymbolId, symbolIdMap, results);
                    }
                    ExtractTypeParameterConstraints(method.ConstraintClauses, workspaceId, fileId, fromSymbolId, symbolIdMap, results);
                    break;

                case ConstructorDeclarationSyntax ctor:
                    ExtractAttributeDeps(ctor.AttributeLists, workspaceId, fileId, fromSymbolId, symbolIdMap, results);
                    foreach (var param in ctor.ParameterList.Parameters)
                    {
                        if (param.Type != null)
                            AddTypeRef(param.Type, DependencyKind.MethodParam,
                                workspaceId, fileId, fromSymbolId, symbolIdMap, results);
                    }
                    break;

                case OperatorDeclarationSyntax op:
                    ExtractAttributeDeps(op.AttributeLists, workspaceId, fileId, fromSymbolId, symbolIdMap, results);
                    AddTypeRef(op.ReturnType, DependencyKind.MethodReturn,
                        workspaceId, fileId, fromSymbolId, symbolIdMap, results);
                    foreach (var param in op.ParameterList.Parameters)
                    {
                        if (param.Type != null)
                            AddTypeRef(param.Type, DependencyKind.MethodParam,
                                workspaceId, fileId, fromSymbolId, symbolIdMap, results);
                    }
                    break;

                case IndexerDeclarationSyntax indexer:
                    ExtractAttributeDeps(indexer.AttributeLists, workspaceId, fileId, fromSymbolId, symbolIdMap, results);
                    AddTypeRef(indexer.Type, DependencyKind.MethodReturn,
                        workspaceId, fileId, fromSymbolId, symbolIdMap, results);
                    foreach (var param in indexer.ParameterList.Parameters)
                    {
                        if (param.Type != null)
                            AddTypeRef(param.Type, DependencyKind.MethodParam,
                                workspaceId, fileId, fromSymbolId, symbolIdMap, results);
                    }
                    break;

                // 嵌套类型递归处理（由外层 DescendantNodes 已覆盖，此处跳过避免重复）
                case TypeDeclarationSyntax _:
                    break;
            }
        }

        /// <summary>提取特性列表中的类型引用。</summary>
        private static void ExtractAttributeDeps(
            SyntaxList<AttributeListSyntax> attributeLists,
            int workspaceId,
            int fileId,
            int? fromSymbolId,
            Dictionary<string, int> symbolIdMap,
            List<SymbolDependency> results)
        {
            foreach (var attrList in attributeLists)
            {
                foreach (var attr in attrList.Attributes)
                {
                    var attrName = attr.Name?.ToString();
                    if (string.IsNullOrWhiteSpace(attrName)) continue;

                    // 规范化：去掉 Attribute 后缀（如果有）
                    var normalizedName = attrName.EndsWith("Attribute")
                        ? attrName.Substring(0, attrName.Length - "Attribute".Length)
                        : attrName;

                    results.Add(new SymbolDependency
                    {
                        WorkspaceId = workspaceId,
                        FromFileId = fileId,
                        FromSymbolId = fromSymbolId,
                        ToTypeName = normalizedName,
                        ToSymbolId = symbolIdMap.TryGetValue(normalizedName, out var toId) ? (int?)toId : null,
                        DependencyKind = DependencyKind.Attribute,
                        SourceLine = GetLine(attr)
                    });
                }
            }
        }

        /// <summary>提取泛型参数约束中的类型引用。</summary>
        private static void ExtractTypeParameterConstraints(
            SyntaxList<TypeParameterConstraintClauseSyntax> constraintClauses,
            int workspaceId,
            int fileId,
            int? fromSymbolId,
            Dictionary<string, int> symbolIdMap,
            List<SymbolDependency> results)
        {
            foreach (var clause in constraintClauses)
            {
                foreach (var constraint in clause.Constraints)
                {
                    if (constraint is TypeConstraintSyntax typeConstraint)
                    {
                        AddTypeRef(typeConstraint.Type, DependencyKind.GenericArg,
                            workspaceId, fileId, fromSymbolId, symbolIdMap, results);
                    }
                }
            }
        }

        // ── 辅助方法 ────────────────────────────────────────────────────────────

        /// <summary>
        /// 将一个 TypeSyntax 解析为类型名称字符串，并添加到结果列表。
        /// 跳过内置类型（void, int, string 等）和 var。
        /// </summary>
        private static void AddTypeRef(
            TypeSyntax typeSyntax,
            string kind,
            int workspaceId,
            int fileId,
            int? fromSymbolId,
            Dictionary<string, int> symbolIdMap,
            List<SymbolDependency> results)
        {
            if (typeSyntax == null) return;

            var typeName = ExtractTypeName(typeSyntax);
            if (string.IsNullOrWhiteSpace(typeName)) return;
            if (IsBuiltinType(typeName)) return;

            results.Add(new SymbolDependency
            {
                WorkspaceId = workspaceId,
                FromFileId = fileId,
                FromSymbolId = fromSymbolId,
                ToTypeName = typeName,
                ToSymbolId = symbolIdMap.TryGetValue(typeName, out var toId) ? (int?)toId : null,
                DependencyKind = kind,
                SourceLine = GetLine(typeSyntax)
            });
        }

        /// <summary>
        /// 从 TypeSyntax 提取类型名称（去掉泛型参数，保留最外层名称）。
        /// 例如：List&lt;string&gt; → List，Dictionary&lt;K,V&gt; → Dictionary，int? → int。
        /// </summary>
        private static string ExtractTypeName(TypeSyntax typeSyntax)
        {
            if (typeSyntax == null) return null;

            switch (typeSyntax)
            {
                case PredefinedTypeSyntax predefined:
                    return predefined.Keyword.ValueText;

                case IdentifierNameSyntax identifier:
                    return identifier.Identifier.ValueText;

                case QualifiedNameSyntax qualified:
                    // 取最右侧名称（如 System.Collections.Generic.List → List）
                    return ExtractTypeName(qualified.Right);

                case GenericNameSyntax generic:
                    // 提取泛型基础名称（如 List<T> → List）
                    // 同时递归提取泛型参数作为 generic_arg 依赖（由调用方处理）
                    return generic.Identifier.ValueText;

                case ArrayTypeSyntax array:
                    return ExtractTypeName(array.ElementType);

                case NullableTypeSyntax nullable:
                    return ExtractTypeName(nullable.ElementType);

                case PointerTypeSyntax pointer:
                    return ExtractTypeName(pointer.ElementType);

                case TupleTypeSyntax _:
                    // 元组类型跳过（太复杂，语法级无法准确提取）
                    return null;

                default:
                    // 兜底：直接 ToString 取第一个 token
                    var text = typeSyntax.ToString();
                    var idx = text.IndexOf('<');
                    return idx > 0 ? text.Substring(0, idx).Trim() : text.Trim();
            }
        }

        /// <summary>
        /// 获取类型声明的限定名称（用于在 symbolIdMap 中查找）。
        /// 格式：Namespace.ClassName（不含泛型参数）。
        /// </summary>
        private static string GetQualifiedName(TypeDeclarationSyntax typeDecl)
        {
            var name = typeDecl.Identifier.ValueText;
            var ns = GetEnclosingNamespace(typeDecl);
            return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
        }

        /// <summary>获取 delegate 声明的限定名称。</summary>
        private static string GetQualifiedName(DelegateDeclarationSyntax delegateDecl)
        {
            var name = delegateDecl.Identifier.ValueText;
            var ns = GetEnclosingNamespace(delegateDecl);
            return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
        }

        /// <summary>获取节点所在的命名空间（向上遍历 NamespaceDeclarationSyntax）。</summary>
        private static string GetEnclosingNamespace(SyntaxNode node)
        {
            var parts = new List<string>();
            var current = node.Parent;
            while (current != null)
            {
                if (current is NamespaceDeclarationSyntax nsDecl)
                    parts.Insert(0, nsDecl.Name.ToString());
                current = current.Parent;
            }
            return string.Join(".", parts);
        }

        /// <summary>获取语法节点的起始行号（1-based）。</summary>
        private static int GetLine(SyntaxNode node)
        {
            return node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        }

        /// <summary>获取语法 token 的起始行号（1-based）。</summary>
        private static int GetLine(SyntaxNodeOrToken nodeOrToken)
        {
            return nodeOrToken.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        }

        /// <summary>
        /// 启发式判断一个类型名称是否像接口（以大写 I 开头，后跟大写字母）。
        /// 这是语法级近似，不能 100% 准确。
        /// </summary>
        private static bool IsLikelyInterface(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return false;
            // 取最后一段（去掉命名空间前缀）
            var lastDot = typeName.LastIndexOf('.');
            var simpleName = lastDot >= 0 ? typeName.Substring(lastDot + 1) : typeName;
            return simpleName.Length >= 2
                && simpleName[0] == 'I'
                && char.IsUpper(simpleName[1]);
        }

        /// <summary>判断是否为 C# 内置类型关键字（不需要记录依赖）。</summary>
        private static bool IsBuiltinType(string typeName)
        {
            switch (typeName)
            {
                case "void":
                case "bool":
                case "byte":
                case "sbyte":
                case "char":
                case "short":
                case "ushort":
                case "int":
                case "uint":
                case "long":
                case "ulong":
                case "float":
                case "double":
                case "decimal":
                case "string":
                case "object":
                case "dynamic":
                case "var":
                case "nint":
                case "nuint":
                    return true;
                default:
                    return false;
            }
        }
    }
}
