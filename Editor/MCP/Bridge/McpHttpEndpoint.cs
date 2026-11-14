using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using AgentCore.Editor.Components.MCP.Config;
using AgentCore.Editor.Components.MCP.Relay;
using AgentCore.Editor.Utils;
using Newtonsoft.Json.Linq;

namespace AgentCore.Editor.Components.MCP.Bridge
{
    /// <summary>
    /// 本机 HTTP 端点：开启后以独立进程运行 relay 的 HTTP 模式（<c>--http</c>），监听 <c>http://127.0.0.1:{port}/mcp</c>。
    /// 进程独立于编辑器域：Domain Reload 不影响它；编辑器退出时它自己退出（<c>--parent-pid</c>）。
    /// 访问令牌存在 <c>Library/AgentCore/Mcp/http-token</c>（每工程一个，保持不变，客户端配置不用改）。
    /// </summary>
    public static class McpHttpEndpoint
    {
        private static string StateFile => Path.Combine(McpPaths.McpDirectory, "http.json");

        public static string TokenFile => Path.Combine(McpPaths.McpDirectory, "http-token");

        /// <summary>最近一次启动失败的原因（设置卡片显示）。</summary>
        public static string LastError { get; private set; }

        public static string Url => $"http://127.0.0.1:{McpSettings.HttpPort}/mcp";

        /// <summary>访问令牌（不存在时生成）。</summary>
        public static string Token
        {
            get
            {
                try
                {
                    if (File.Exists(TokenFile))
                    {
                        var existing = File.ReadAllText(TokenFile).Trim();
                        if (existing.Length >= 32)
                            return existing;
                    }
                    var bytes = new byte[32];
                    using (var rng = RandomNumberGenerator.Create())
                        rng.GetBytes(bytes);
                    var token = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                    McpPaths.WriteAtomic(TokenFile, token);
                    return token;
                }
                catch (Exception ex)
                {
                    LastError = "Could not create the HTTP token: " + ex.Message;
                    return null;
                }
            }
        }

        /// <summary>是否有一个属于本编辑器、仍在运行的 HTTP relay。</summary>
        public static bool IsRunning => ReadState(out var pid, out var port) && ProcessAlive(pid) && port == McpSettings.HttpPort;

        /// <summary>按设置启动或停止（主线程调用；启动在后台进行）。</summary>
        public static void Apply()
        {
            if (!McpSettings.HttpEnabled)
            {
                Stop();
                return;
            }
            if (IsRunning)
                return;

            if (McpSettings.HttpPort <= 0)
                McpSettings.HttpPort = FindFreePort();
            var port = McpSettings.HttpPort;
            var token = Token;
            var editorPid = Process.GetCurrentProcess().Id;
            var project = McpPaths.ProjectRoot;

            McpRelayBuilder.EnsureBuiltAsync().ContinueWith(build =>
            {
                if (build.Status != TaskStatus.RanToCompletion || !build.Result.Success)
                {
                    LastError = "The relay is not available: " + (build.Result?.Error ?? build.Exception?.GetBaseException().Message);
                    return;
                }
                if (token == null)
                    return;
                Launch(build.Result, project, port, editorPid);
            });
        }

        private static void Launch(McpRelayBuilder.BuildResult build, string project, int port, int editorPid)
        {
            try
            {
                if (!IsPortFree(port))
                {
                    LastError = $"Port {port} is in use by another program. Turn the HTTP endpoint off and on to pick a new port (client configs must be copied again).";
                    return;
                }

                var info = new ProcessStartInfo
                {
                    FileName = build.MonoPath,
                    Arguments = ProcessArgs.Join(new[]
                    {
                        build.RelayPath, "--project", project, "--http", port.ToString(),
                        "--token-file", TokenFile, "--parent-pid", editorPid.ToString()
                    }),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                var process = Process.Start(info);
                if (process == null)
                {
                    LastError = "Could not start the HTTP relay.";
                    return;
                }

                McpPaths.WriteAtomic(StateFile, new JObject { ["pid"] = process.Id, ["port"] = port }.ToString());

                // 等它开始监听
                for (var i = 0; i < 50; i++)
                {
                    if (process.HasExited)
                    {
                        LastError = $"The HTTP relay exited immediately (code {process.ExitCode}).";
                        return;
                    }
                    if (!IsPortFree(port))
                    {
                        LastError = null;
                        AgentCoreLog.Info($"[AgentCore][MCP] HTTP endpoint listening on http://127.0.0.1:{port}/mcp");
                        return;
                    }
                    System.Threading.Thread.Sleep(100);
                }
                LastError = "The HTTP relay did not start listening in time.";
            }
            catch (Exception ex)
            {
                LastError = "Could not start the HTTP relay: " + ex.Message;
            }
        }

        public static void Stop()
        {
            if (ReadState(out var pid, out _) && ProcessAlive(pid))
            {
                try
                {
                    using (var process = Process.GetProcessById(pid))
                        process.Kill();
                }
                catch (Exception)
                {
                    // 已退出
                }
            }
            try
            {
                if (File.Exists(StateFile))
                    File.Delete(StateFile);
            }
            catch (Exception)
            {
                // ignore
            }
        }

        private static bool ReadState(out int pid, out int port)
        {
            pid = 0;
            port = 0;
            try
            {
                if (!File.Exists(StateFile))
                    return false;
                var json = JObject.Parse(File.ReadAllText(StateFile));
                pid = (int?)json["pid"] ?? 0;
                port = (int?)json["port"] ?? 0;
                return pid > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool ProcessAlive(int pid)
        {
            try
            {
                using (var process = Process.GetProcessById(pid))
                    return !process.HasExited;
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static int FindFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        internal static bool IsPortFree(int port)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }

        /// <summary>Claude Code：HTTP 传输 + 令牌请求头。</summary>
        public static string ClaudeCodeCommand(string url, string token) =>
            $"claude mcp add --transport http --scope user agentcore-http {url} --header \"Authorization: Bearer {token}\"";

        /// <summary>Codex：config.toml 片段（url + http_headers）。</summary>
        public static string CodexToml(string url, string token)
        {
            var sb = new StringBuilder();
            sb.Append("[mcp_servers.agentcore_http]\n");
            sb.Append("url = ").Append(McpClientConfig.TomlString(url)).Append('\n');
            sb.Append("http_headers = { Authorization = ").Append(McpClientConfig.TomlString("Bearer " + token)).Append(" }\n");
            sb.Append("tool_timeout_sec = 600\n");
            return sb.ToString();
        }
    }
}
