using System.Runtime.CompilerServices;

// 允许测试程序集访问 AgentCore.Editor 的 internal 类型。
// 仅对内部测试开放, 不影响对外公共 API 面。
[assembly: InternalsVisibleTo("AgentCore.Tests.Editor")]
