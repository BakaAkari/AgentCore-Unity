using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentCore.Editor.McpClient
{
    /// <summary>MCP 客户端传输：发送 JSON-RPC 消息、接收服务器消息。</summary>
    public interface IMcpClientTransport : IDisposable
    {
        event Action<JObject> MessageReceived;

        event Action<string> Closed;

        Task StartAsync(CancellationToken ct);

        Task SendAsync(JObject message, CancellationToken ct);

        /// <summary>最近的诊断输出（stdio 服务器的 stderr 等），设置页显示。</summary>
        string Diagnostics { get; }
    }

    /// <summary>
    /// stdio 传输：启动服务器进程，按行收发 JSON。子进程加入 <see cref="ChildProcessJob"/>，编辑器退出或重载时整树结束。
    /// Windows 上 <c>npx</c> 这类 .cmd 命令经 cmd.exe 启动。
    /// </summary>
    public sealed class StdioClientTransport : IMcpClientTransport
    {
        private readonly McpServerConfig _config;
        private readonly object _writeLock = new object();
        private readonly Queue<string> _stderr = new Queue<string>();
        private Process _process;
        private volatile bool _disposed;

        public StdioClientTransport(McpServerConfig config)
        {
            _config = config;
        }

        public event Action<JObject> MessageReceived;

        public event Action<string> Closed;

        public string Diagnostics
        {
            get
            {
                lock (_stderr)
                    return string.Join("\n", _stderr);
            }
        }

        public Task StartAsync(CancellationToken ct)
        {
            var (fileName, arguments) = ResolveCommand(_config.Command, _config.Args);
            var info = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                WorkingDirectory = string.IsNullOrEmpty(_config.Cwd) ? McpServerConfigStore.ProjectRoot : _config.Cwd,
            };
            foreach (var pair in _config.Env)
                info.EnvironmentVariables[pair.Key] = pair.Value;

            _process = Process.Start(info);
            if (_process == null)
                throw new InvalidOperationException("Could not start " + _config.Command);
            ChildProcessJob.TryAssign(_process);

            new Thread(ReadStdout) { IsBackground = true, Name = "agentcore-mcp-client-stdout" }.Start();
            new Thread(ReadStderr) { IsBackground = true, Name = "agentcore-mcp-client-stderr" }.Start();
            return Task.CompletedTask;
        }

        /// <summary>
        /// Windows：命令不带路径扩展名时按 PATH + PATHEXT 查找；落到 .cmd / .bat 时用 cmd.exe /c 启动（Process 不能直接启动批处理）。
        /// </summary>
        internal static (string fileName, string arguments) ResolveCommand(string command, IReadOnlyList<string> args)
        {
            if (!ProcessArgs.IsWindows)
                return (command, ProcessArgs.Join(args));

            var resolved = FindOnPath(command) ?? command;
            var ext = Path.GetExtension(resolved).ToLowerInvariant();
            if (ext == ".cmd" || ext == ".bat")
            {
                var inner = new List<string> { resolved };
                inner.AddRange(args);
                return ("cmd.exe", "/d /s /c \"" + ProcessArgs.Join(inner) + "\"");
            }
            return (resolved, ProcessArgs.Join(args));
        }

        private static string FindOnPath(string command)
        {
            if (string.IsNullOrEmpty(command))
                return null;
            if (Path.IsPathRooted(command) && File.Exists(command))
                return command;
            var extensions = Path.HasExtension(command)
                ? new[] { string.Empty }
                : (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';').Where(e => e.Length > 0).ToArray();
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir))
                    continue;
                foreach (var ext in extensions)
                {
                    try
                    {
                        var candidate = Path.Combine(dir.Trim(), command + ext);
                        if (File.Exists(candidate))
                            return candidate;
                    }
                    catch (ArgumentException)
                    {
                        // PATH 中的非法项
                    }
                }
            }
            return null;
        }

        private void ReadStdout()
        {
            try
            {
                string line;
                while ((line = _process.StandardOutput.ReadLine()) != null)
                {
                    if (line.Length == 0 || line[0] != '{')
                        continue; // 服务器不该往 stdout 写非协议内容；忽略
                    JObject message;
                    try
                    {
                        message = JObject.Parse(line);
                    }
                    catch (JsonException)
                    {
                        continue;
                    }
                    MessageReceived?.Invoke(message);
                }
            }
            catch (Exception)
            {
                // 进程结束
            }
            if (!_disposed)
                Closed?.Invoke("The server process exited" + (_process.HasExited ? $" (code {_process.ExitCode})." : "."));
        }

        private void ReadStderr()
        {
            try
            {
                string line;
                while ((line = _process.StandardError.ReadLine()) != null)
                {
                    lock (_stderr)
                    {
                        _stderr.Enqueue(line);
                        while (_stderr.Count > 20)
                            _stderr.Dequeue();
                    }
                }
            }
            catch (Exception)
            {
                // 进程结束
            }
        }

        public Task SendAsync(JObject message, CancellationToken ct)
        {
            lock (_writeLock)
            {
                if (_process == null || _process.HasExited)
                    throw new IOException("The server process is not running.");
                var writer = _process.StandardInput;
                writer.Write(message.ToString(Formatting.None));
                writer.Write('\n');
                writer.Flush();
            }
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _disposed = true;
            if (_process == null)
                return;
            try { _process.StandardInput.Close(); } catch (Exception) { /* ignore */ }
            try
            {
                if (!_process.WaitForExit(500))
                    ChildProcessJob.KillTree(_process);
            }
            catch (Exception)
            {
                // ignore
            }
            _process.Dispose();
        }
    }

    /// <summary>
    /// Streamable HTTP 传输（客户端，2025-03-26 ~ 2025-11-25 形态）：每条消息一个 POST，响应为 JSON 或 SSE；
    /// initialize 返回的 Mcp-Session-Id 用于后续请求。不打开独立的 GET 通知流（列表变化通知在下次刷新时体现）。
    /// </summary>
    public sealed class HttpClientTransport : IMcpClientTransport
    {
        private static readonly HttpClient Client = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

        private readonly McpServerConfig _config;
        private string _sessionId;
        private string _protocolVersion;

        public HttpClientTransport(McpServerConfig config)
        {
            _config = config;
        }

        public event Action<JObject> MessageReceived;

        public event Action<string> Closed;

        public string Diagnostics { get; private set; } = string.Empty;

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        /// <summary>initialize 之后由会话设置，作为 MCP-Protocol-Version 请求头。</summary>
        public void SetProtocolVersion(string version) => _protocolVersion = version;

        public async Task SendAsync(JObject message, CancellationToken ct)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Post, _config.Url))
            {
                request.Content = new StringContent(message.ToString(Formatting.None), new UTF8Encoding(false), "application/json");
                request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
                foreach (var header in _config.Headers)
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                if (_sessionId != null)
                    request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
                if (_protocolVersion != null)
                    request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", _protocolVersion);

                HttpResponseMessage response;
                try
                {
                    response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    Diagnostics = ex.Message;
                    throw new IOException("HTTP request failed: " + ex.Message, ex);
                }

                using (response)
                {
                    if (response.Headers.TryGetValues("Mcp-Session-Id", out var ids))
                        _sessionId = ids.FirstOrDefault() ?? _sessionId;

                    if ((int)response.StatusCode == 202 || response.Content == null)
                        return;
                    if (!response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType != "application/json")
                    {
                        Diagnostics = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                        if ((int)response.StatusCode == 404 && _sessionId != null)
                            Closed?.Invoke("The server ended the session.");
                        throw new IOException(Diagnostics);
                    }

                    var mediaType = response.Content.Headers.ContentType?.MediaType;
                    using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        if (mediaType == "text/event-stream")
                        {
                            var data = new StringBuilder();
                            string line;
                            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                            {
                                if (line.StartsWith("data:", StringComparison.Ordinal))
                                {
                                    data.Append(line.Substring(5).TrimStart());
                                }
                                else if (line.Length == 0 && data.Length > 0)
                                {
                                    Dispatch(data.ToString());
                                    data.Clear();
                                }
                            }
                            if (data.Length > 0)
                                Dispatch(data.ToString());
                        }
                        else
                        {
                            var body = await reader.ReadToEndAsync().ConfigureAwait(false);
                            if (!string.IsNullOrWhiteSpace(body))
                                Dispatch(body);
                        }
                    }
                }
            }
        }

        private void Dispatch(string json)
        {
            try
            {
                var token = JToken.Parse(json);
                if (token is JObject message)
                    MessageReceived?.Invoke(message);
                else if (token is JArray batch)
                {
                    foreach (var item in batch.OfType<JObject>())
                        MessageReceived?.Invoke(item);
                }
            }
            catch (JsonException)
            {
                // 非 JSON：忽略
            }
        }

        public void Dispose()
        {
            if (_sessionId == null)
                return;
            // 结束服务器端会话（尽力而为）
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Delete, _config.Url))
                {
                    foreach (var header in _config.Headers)
                        request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
                    Client.SendAsync(request).Wait(1000);
                }
            }
            catch (Exception)
            {
                // ignore
            }
        }
    }
}
