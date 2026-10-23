using System;
using System.IO;
using System.Text;

namespace AgentCore.Editor.Utils
{
    /// <summary>
    /// 原子写文件：先写同目录临时文件并落盘，再一次性替换目标。
    /// <para>
    /// <c>File.WriteAllText</c> 先截断再写，写到一半遇到域重载、编辑器崩溃或磁盘满时会留下空文件 / 半截 JSON，
    /// 会话、配置因此整份丢失；脚本被写成半截还会触发一轮错误编译。临时文件以 <c>.</c> 开头，
    /// 位于 Assets 下时 Unity 不会导入它。
    /// </para>
    /// </summary>
    public static class AtomicFile
    {
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        /// <summary>
        /// 原子写入文本。<paramref name="encoding"/> 为 null 时与 <c>File.WriteAllText(path, text)</c> 一致（UTF-8 无 BOM）。
        /// </summary>
        public static void WriteAllText(string path, string contents, Encoding encoding = null)
        {
            WriteAllBytes(path, Encode(contents ?? string.Empty, encoding ?? Utf8NoBom));
        }

        public static void WriteAllBytes(string path, byte[] bytes)
        {
            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var tempPath = Path.Combine(directory ?? string.Empty,
                "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");

            try
            {
                using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }

                Commit(tempPath, fullPath);
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch
                {
                    // 临时文件清理失败不影响结果
                }
            }
        }

        private static void Commit(string tempPath, string fullPath)
        {
            if (!File.Exists(fullPath))
            {
                File.Move(tempPath, fullPath);
                return;
            }

            try
            {
                File.Replace(tempPath, fullPath, null, ignoreMetadataErrors: true);
            }
            catch (Exception ex) when (ex is IOException || ex is PlatformNotSupportedException || ex is UnauthorizedAccessException)
            {
                // 目标被其他进程（杀毒、索引器、Unity 文件监视）短暂占用，或文件系统不支持 Replace：
                // 退回覆盖复制。此时不再是原子替换，但临时文件已完整落盘，不会写出半截内容。
                File.Copy(tempPath, fullPath, overwrite: true);
            }
        }

        private static byte[] Encode(string contents, Encoding encoding)
        {
            var preamble = encoding.GetPreamble();
            var body = encoding.GetBytes(contents);
            if (preamble.Length == 0)
                return body;

            var bytes = new byte[preamble.Length + body.Length];
            Buffer.BlockCopy(preamble, 0, bytes, 0, preamble.Length);
            Buffer.BlockCopy(body, 0, bytes, preamble.Length, body.Length);
            return bytes;
        }
    }
}
