using System;
using System.IO;
using System.Text;

namespace AgentCore.Editor.Components.MCP.Bridge
{
    /// <summary>
    /// MCP 相关文件都放在工程的 <c>Library/AgentCore/Mcp/</c> 下（不进版本控制、按工程隔离）。
    /// </summary>
    public static class McpPaths
    {
        /// <summary>Unity 工程根（编辑器的当前目录；线程安全，不依赖 Application.dataPath）。</summary>
        public static string ProjectRoot { get; } = Path.GetFullPath(Directory.GetCurrentDirectory());

        public static string McpDirectory => Path.Combine(ProjectRoot, "Library", "AgentCore", "Mcp");

        public static string EndpointFile => Path.Combine(McpDirectory, "endpoint.json");

        public static string AuditFile => Path.Combine(McpDirectory, "audit.jsonl");

        public static string ToolsCacheFile => Path.Combine(McpDirectory, "tools-cache.json");

        /// <summary>
        /// 写临时文件再替换：relay 读到的发现文件永远是完整的。不经 <c>AtomicFile</c>，以免被检查点当成工具写入记录。
        /// </summary>
        public static void WriteAtomic(string path, string contents)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temp = path + ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            File.WriteAllText(temp, contents, new UTF8Encoding(false));
            try
            {
                // relay 每次重连都会读发现文件：撞上它正在读时 Replace 会失败，短暂重试
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        if (File.Exists(path))
                            File.Replace(temp, path, null);
                        else
                            File.Move(temp, path);
                        return;
                    }
                    catch (IOException) when (attempt < 20)
                    {
                        System.Threading.Thread.Sleep(25);
                    }
                    catch (UnauthorizedAccessException) when (attempt < 20)
                    {
                        System.Threading.Thread.Sleep(25);
                    }
                }
            }
            finally
            {
                if (File.Exists(temp))
                {
                    try { File.Delete(temp); } catch (Exception) { /* 下次写入时再清 */ }
                }
            }
        }
    }
}
