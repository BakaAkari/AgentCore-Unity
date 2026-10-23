using System;
using System.Runtime.InteropServices;
using System.Text;

namespace AgentCore.Editor.Config
{
    /// <summary>
    /// API Key 静态加密（v1.18）。
    /// <para>
    /// Windows：用 DPAPI（CurrentUser 作用域）加密后再写入 EditorPrefs（注册表），只有同一 Windows 用户
    /// 才能解密。Unity 2022.3 的 Editor 程序集未引用 <c>System.Security.dll</c>
    /// （<c>ProtectedData</c> 不可用），故直接 P/Invoke <c>crypt32.dll</c> 的
    /// <c>CryptProtectData</c> / <c>CryptUnprotectData</c>。
    /// </para>
    /// <para>
    /// 其他平台（macOS / Linux）：保持明文存储，与旧版一致（EditorPrefs 位于用户目录的 plist / 配置文件）。
    /// </para>
    /// <para>
    /// 存储格式：加密值 = <see cref="ProtectedPrefix"/> + Base64(密文)。不带前缀的值视为旧版明文，
    /// 读取时原样返回，由 <see cref="SecureKeyStorage"/> 就地迁移为密文。
    /// </para>
    /// </summary>
    internal static class SecretProtector
    {
        /// <summary>加密值前缀（含格式版本号）。</summary>
        internal const string ProtectedPrefix = "dpapi1:";

        // 附加熵：把密文绑定到 AgentCore 用途，其他程序用同一用户身份也不能直接解出。
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AgentCore.SecureKeyStorage.v1");

        private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

        /// <summary>当前平台是否支持 DPAPI 加密。</summary>
        public static bool IsSupported => Environment.OSVersion.Platform == PlatformID.Win32NT;

        /// <summary>值是否为本类写出的加密格式。</summary>
        public static bool IsProtected(string stored)
            => stored != null && stored.StartsWith(ProtectedPrefix, StringComparison.Ordinal);

        /// <summary>
        /// 加密明文。成功的前提是加密后立即解密能还原出原文（round-trip 校验），
        /// 否则返回 false——调用方应退回明文存储，绝不能写入一个无法还原的值。
        /// </summary>
        public static bool TryProtect(string plain, out string stored)
        {
            stored = null;
            if (string.IsNullOrEmpty(plain) || !IsSupported)
                return false;

            try
            {
                var cipher = Protect(Encoding.UTF8.GetBytes(plain));
                if (cipher == null)
                    return false;

                var candidate = ProtectedPrefix + Convert.ToBase64String(cipher);
                if (!TryUnprotect(candidate, out var roundTrip) || !string.Equals(roundTrip, plain, StringComparison.Ordinal))
                    return false;

                stored = candidate;
                return true;
            }
            catch (Exception)
            {
                // DllNotFound / EntryPointNotFound 等：退回明文
                return false;
            }
        }

        /// <summary>
        /// 解密 <see cref="ProtectedPrefix"/> 格式的值。值不是加密格式、平台不支持或解密失败
        /// （如换了 Windows 用户 / 重装系统）时返回 false。
        /// </summary>
        public static bool TryUnprotect(string stored, out string plain)
        {
            plain = null;
            if (!IsProtected(stored) || !IsSupported)
                return false;

            try
            {
                var cipher = Convert.FromBase64String(stored.Substring(ProtectedPrefix.Length));
                var bytes = Unprotect(cipher);
                if (bytes == null)
                    return false;

                plain = Encoding.UTF8.GetString(bytes);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ── crypt32 P/Invoke ──

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int cbData;
            public IntPtr pbData;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptProtectData(
            ref DataBlob pDataIn, string szDataDescr, ref DataBlob pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectData(
            ref DataBlob pDataIn, IntPtr ppszDataDescr, ref DataBlob pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        private static byte[] Protect(byte[] data)
            => Transform(data, protect: true);

        private static byte[] Unprotect(byte[] data)
            => Transform(data, protect: false);

        private static byte[] Transform(byte[] data, bool protect)
        {
            var input = ToBlob(data);
            var entropy = ToBlob(Entropy);
            var output = new DataBlob();
            try
            {
                // dwFlags 不含 CRYPTPROTECT_LOCAL_MACHINE → CurrentUser 作用域
                bool ok = protect
                    ? CryptProtectData(ref input, "AgentCore", ref entropy, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output);
                if (!ok || output.pbData == IntPtr.Zero)
                    return null;

                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return result;
            }
            finally
            {
                FreeBlob(input);
                FreeBlob(entropy);
                if (output.pbData != IntPtr.Zero)
                    LocalFree(output.pbData);
            }
        }

        private static DataBlob ToBlob(byte[] data)
        {
            var blob = new DataBlob { cbData = data?.Length ?? 0, pbData = IntPtr.Zero };
            if (blob.cbData > 0)
            {
                blob.pbData = Marshal.AllocHGlobal(blob.cbData);
                Marshal.Copy(data, 0, blob.pbData, blob.cbData);
            }
            return blob;
        }

        private static void FreeBlob(DataBlob blob)
        {
            if (blob.pbData != IntPtr.Zero)
            {
                // 明文缓冲区释放前清零
                for (int i = 0; i < blob.cbData; i++)
                    Marshal.WriteByte(blob.pbData, i, 0);
                Marshal.FreeHGlobal(blob.pbData);
            }
        }
    }
}
