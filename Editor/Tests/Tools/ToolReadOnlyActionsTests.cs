using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AgentCore.Editor.Tools;
using AgentCore.Editor.Tools.Infrastructure;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace AgentCore.Tests.Editor.Tools
{
    /// <summary>
    /// [AgentTool] ReadOnlyActions 白名单一致性测试：
    /// 白名单里的每一项都必须是 schema 中 action 枚举的真实取值（防止把参数值如编码名、构建选项误写进白名单）。
    /// </summary>
    [TestFixture]
    public class ToolReadOnlyActionsTests
    {
        private static IEnumerable<Type> AgentToolTypes()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = asm.GetName().Name;
                if (!name.StartsWith("AgentCore", StringComparison.Ordinal) || name.StartsWith("AgentCore.Tests", StringComparison.Ordinal))
                    continue;

                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }

                foreach (var t in types)
                {
                    if (t.IsAbstract || !typeof(IAgentTool).IsAssignableFrom(t))
                        continue;
                    if (t.GetCustomAttribute<AgentToolAttribute>() != null && t.GetConstructor(Type.EmptyTypes) != null)
                        yield return t;
                }
            }
        }

        [Test]
        public void ReadOnlyActions_AreDeclaredActionEnumValues()
        {
            var problems = new List<string>();
            int checkedTools = 0;

            foreach (var type in AgentToolTypes())
            {
                var attr = type.GetCustomAttribute<AgentToolAttribute>();
                if (attr.ReadOnlyActions == null || attr.ReadOnlyActions.Length == 0)
                    continue;

                var tool = (IAgentTool)Activator.CreateInstance(type);
                var actionEnum = tool.Metadata?.ParametersSchema?["properties"]?["action"]?["enum"] as JArray;
                if (actionEnum == null)
                    continue; // 无 action 枚举的工具（如 batch_execute 的哨兵值）不在本测试范围

                checkedTools++;
                var actions = new HashSet<string>(actionEnum.Select(v => (string)v), StringComparer.OrdinalIgnoreCase);
                foreach (var ro in attr.ReadOnlyActions)
                {
                    if (!actions.Contains(ro))
                        problems.Add($"{attr.Name}: '{ro}'");
                }
            }

            Assert.Greater(checkedTools, 0, "未发现带 ReadOnlyActions 与 action 枚举的工具");
            Assert.IsEmpty(problems, "ReadOnlyActions 含非 action 值: " + string.Join(", ", problems));
        }

        [Test]
        public void ManageFile_ReadOnlyActions_CoverAllReadActions()
        {
            var type = AgentToolTypes().First(t => t.GetCustomAttribute<AgentToolAttribute>().Name == "manage_file");
            var ro = type.GetCustomAttribute<AgentToolAttribute>().ReadOnlyActions;

            CollectionAssert.AreEquivalent(
                new[] { "read_file", "file_info", "list_directory", "search_content" }, ro);
        }
    }
}
