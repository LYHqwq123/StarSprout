using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace StarSprout.Bootstrap
{
    internal static class SignatureVerifier
    {
        public static void VerifyMicrosoft(string path)
        {
            var file = new WinTrustFileInfo { Size = (uint)Marshal.SizeOf(typeof(WinTrustFileInfo)), FilePath = Path.GetFullPath(path) };
            IntPtr pointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WinTrustFileInfo)));
            Marshal.StructureToPtr(file, pointer, false);
            var trust = new WinTrustData {
                Size = (uint)Marshal.SizeOf(typeof(WinTrustData)), UiChoice = 2,
                UnionChoice = 1, FileInfo = pointer, StateAction = 1,
                // Use Windows trust roots and cached revocation data. Offline installation never
                // requires a network certificate lookup; cryptographic chain checks still apply.
                ProviderFlags = 0x1000
            };
            Guid action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            try
            {
                int result = WinVerifyTrust(new IntPtr(-1), ref action, ref trust);
                if (result != 0) throw new InvalidDataException("安装程序的 Windows 签名校验失败（0x" + result.ToString("X8") + "）。请重新下载微软官方安装包。");
                using (var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path)))
                {
                    if (!string.Equals(certificate.GetNameInfo(X509NameType.SimpleName, false), "Microsoft Corporation", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("安装程序签名者不是 Microsoft Corporation，已停止安装。");
                }
            }
            finally
            {
                trust.StateAction = 2;
                WinVerifyTrust(new IntPtr(-1), ref action, ref trust);
                Marshal.DestroyStructure(pointer, typeof(WinTrustFileInfo));
                Marshal.FreeHGlobal(pointer);
            }
        }

        public static void VerifySha512(string path, string expected)
        {
            if (string.IsNullOrWhiteSpace(expected) || expected.Length != 128 || !IsHex(expected))
                throw new InvalidDataException("微软发布信息中的 SHA-512 校验值无效。");
            string actual;
            using (var stream = File.OpenRead(path))
            using (var hash = SHA512.Create()) actual = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "");
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("运行库下载不完整或被修改，SHA-512 校验失败。");
        }

        private static bool IsHex(string value)
        {
            foreach (char c in value) if (!Uri.IsHexDigit(c)) return false;
            return true;
        }

        [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WinTrustData data);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustFileInfo
        {
            public uint Size;
            [MarshalAs(UnmanagedType.LPWStr)] public string FilePath;
            public IntPtr FileHandle;
            public IntPtr KnownSubject;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustData
        {
            public uint Size;
            public IntPtr PolicyCallbackData;
            public IntPtr SipClientData;
            public uint UiChoice;
            public uint RevocationChecks;
            public uint UnionChoice;
            public IntPtr FileInfo;
            public uint StateAction;
            public IntPtr StateData;
            public IntPtr UrlReference;
            public uint ProviderFlags;
            public uint UiContext;
            public IntPtr SignatureSettings;
        }
    }
}
