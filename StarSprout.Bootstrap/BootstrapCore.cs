using Microsoft.Win32;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace StarSprout.Bootstrap
{
    internal sealed class RuntimeStatus
    {
        public string DotNetDesktopVersion { get; set; }
        public string DotNetRoot { get; set; }
        public string WebView2Version { get; set; }
        public bool DotNetDesktopInstalled { get { return !string.IsNullOrEmpty(DotNetDesktopVersion); } }
        public bool WebView2Installed { get { return !string.IsNullOrEmpty(WebView2Version); } }
        public bool Ready { get { return DotNetDesktopInstalled && WebView2Installed; } }
    }

    internal static class RuntimeDetector
    {
        internal const string WebViewGuid = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
        public static RuntimeStatus Detect()
        {
            var status = new RuntimeStatus();
            var roots = new List<string> {
                Environment.GetEnvironmentVariable("DOTNET_ROOT_X64"), Environment.GetEnvironmentVariable("DOTNET_ROOT"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet")
            };
            try
            {
                using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var key = machine.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64"))
                    if (key != null) roots.Add(key.GetValue("InstallLocation") as string);
            }
            catch (System.Security.SecurityException) { }
            foreach (string root in roots.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string desktopPath = Path.Combine(root, "shared", "Microsoft.WindowsDesktop.App");
                if (!File.Exists(Path.Combine(root, "dotnet.exe")) || !Directory.Exists(desktopPath)) continue;
                try
                {
                    var installed = Directory.GetDirectories(desktopPath).Select(Path.GetFileName)
                        .Where(IsDesktop10).Select(x => new { Text = x, Version = Version.Parse(x) })
                        .OrderByDescending(x => x.Version);
                    foreach (var version in installed)
                    {
                        string desktop = Path.Combine(desktopPath, version.Text);
                        string core = Path.Combine(root, "shared", "Microsoft.NETCore.App", version.Text);
                        if (File.Exists(Path.Combine(desktop, "PresentationFramework.dll")) && File.Exists(Path.Combine(core, "System.Private.CoreLib.dll")))
                        {
                            status.DotNetDesktopVersion = version.Text;
                            status.DotNetRoot = root;
                            break;
                        }
                    }
                }
                catch (UnauthorizedAccessException) { }
                if (status.DotNetDesktopInstalled) break;
            }
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                try
                {
                    using (var baseKey = RegistryKey.OpenBaseKey(hive, view))
                    using (var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\EdgeUpdate\Clients\" + WebViewGuid))
                    {
                        string version = key == null ? null : key.GetValue("pv") as string;
                        if (IsWebViewVersion(version)) status.WebView2Version = version;
                    }
                }
                catch (System.Security.SecurityException) { }
            }
            return status;
        }

        public static bool IsDesktop10(string value)
        {
            Version version;
            return Version.TryParse(value, out version) && version.Major == 10 && version.Minor == 0 && version.Build >= 0;
        }
        public static bool IsWebViewVersion(string value)
        {
            Version version;
            return Version.TryParse(value, out version) && version > new Version(0, 0, 0, 0);
        }
    }

    internal sealed class InstallerInfo
    {
        public string Version { get; set; }
        public string Url { get; set; }
        public string Sha512 { get; set; }
    }

    internal sealed class BootstrapCore
    {
        public const string MetadataUrl = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json";
        public const string WebViewBootstrapUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
        public const string WebViewStandaloneUrl = "https://go.microsoft.com/fwlink/?linkid=2124701";
        public string Root { get; private set; }
        public string AppPath { get { return Path.Combine(Root, "app", "StarSprout.App.exe"); } }

        public BootstrapCore(string root) { Root = Path.GetFullPath(root); }

        public object Plan(RuntimeStatus status)
        {
            var steps = new List<object>();
            if (!status.DotNetDesktopInstalled) steps.Add(new {
                runtime = ".NET 10 Desktop Runtime x64", bundled = Path.Combine(Root, "runtimes", "windowsdesktop-runtime-win-x64.exe"),
                officialMetadata = MetadataUrl, arguments = "/install /quiet /norestart", integrity = "SHA-512 + Microsoft Authenticode", elevation = "Windows UAC"
            });
            if (!status.WebView2Installed) steps.Add(new {
                runtime = "Microsoft Edge WebView2 Runtime", bundled = Path.Combine(Root, "runtimes", "MicrosoftEdgeWebView2Setup.exe"),
                officialDownload = WebViewBootstrapUrl, arguments = "/silent /install", integrity = "Microsoft Authenticode", elevation = "Per-user installation"
            });
            return new { dryRun = true, detected = status, installSteps = steps, app = AppPath, appExists = File.Exists(AppPath), installsExecuted = 0 };
        }

        public async Task EnsureAsync(Action<string, int> progress, CancellationToken token)
        {
            RuntimeStatus status = RuntimeDetector.Detect();
            if (status.Ready) return;
            string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StarSprout", "runtime-cache", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(cache);
            try
            {
                if (!status.DotNetDesktopInstalled)
                {
                    string installer = Path.Combine(Root, "runtimes", "windowsdesktop-runtime-win-x64.exe");
                    if (!File.Exists(installer))
                    {
                        progress("正在获取 .NET 10 Desktop Runtime 官方版本…", -1);
                        InstallerInfo info = SelectDesktopInstaller(await ReadMetadataAsync(token));
                        installer = Path.Combine(cache, "windowsdesktop-runtime-win-x64.exe");
                        await DownloadAsync(info.Url, installer, ".NET 10 Desktop Runtime", progress, token);
                        SignatureVerifier.VerifySha512(installer, info.Sha512);
                    }
                    else
                    {
                        progress("正在检查离线 .NET 安装包…", -1);
                        string hashFile = installer + ".sha512";
                        if (File.Exists(hashFile)) SignatureVerifier.VerifySha512(installer, File.ReadAllText(hashFile).Trim());
                    }
                    SignatureVerifier.VerifyMicrosoft(installer);
                    progress("正在安装 .NET 10 Desktop Runtime，请允许 Windows 安装提示…", -1);
                    await InstallAsync(installer, "/install /quiet /norestart", true, token);
                    if (!RuntimeDetector.Detect().DotNetDesktopInstalled) throw new InvalidOperationException("安装结束，但未检测到 .NET 10 Desktop Runtime x64。请重启 Windows 后重新打开星芽，或检查安装日志。");
                }
                token.ThrowIfCancellationRequested();
                if (!status.WebView2Installed)
                {
                    string installer = Path.Combine(Root, "runtimes", "MicrosoftEdgeWebView2Setup.exe");
                    if (!File.Exists(installer))
                    {
                        installer = Path.Combine(cache, "MicrosoftEdgeWebView2Setup.exe");
                        await DownloadAsync(WebViewBootstrapUrl, installer, "WebView2 Runtime", progress, token);
                    }
                    SignatureVerifier.VerifyMicrosoft(installer);
                    progress("正在安装 Microsoft Edge WebView2 Runtime…", -1);
                    await InstallAsync(installer, "/silent /install", false, token);
                    if (!RuntimeDetector.Detect().WebView2Installed) throw new InvalidOperationException("安装结束，但未检测到 WebView2 Runtime。请重启 Windows 后重新打开星芽，或检查安装日志。");
                }
            }
            finally
            {
                // Only remove this bootstrapper's freshly created, isolated cache directory.
                try { Directory.Delete(cache, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        public async Task PrepareOfflineAsync(string directory, Action<string, int> progress, CancellationToken token)
        {
            directory = Path.GetFullPath(directory);
            Directory.CreateDirectory(directory);
            var info = SelectDesktopInstaller(await ReadMetadataAsync(token));
            string desktop = Path.Combine(directory, "windowsdesktop-runtime-win-x64.exe");
            await DownloadAsync(info.Url, desktop, ".NET 10 Desktop Runtime", progress, token);
            SignatureVerifier.VerifySha512(desktop, info.Sha512);
            SignatureVerifier.VerifyMicrosoft(desktop);
            File.WriteAllText(desktop + ".sha512", info.Sha512, new UTF8Encoding(false));
            string webView = Path.Combine(directory, "MicrosoftEdgeWebView2Setup.exe");
            await DownloadAsync(WebViewStandaloneUrl, webView, "WebView2 离线完整运行库", progress, token);
            SignatureVerifier.VerifyMicrosoft(webView);
            File.WriteAllText(Path.Combine(directory, "runtime-manifest.json"), new JavaScriptSerializer().Serialize(new {
                dotnet = info, webview2 = new { source = WebViewStandaloneUrl, standalone = true }, preparedUtc = DateTime.UtcNow.ToString("o")
            }), new UTF8Encoding(false));
        }

        public void Launch(string[] arguments)
        {
            if (!File.Exists(AppPath)) throw new FileNotFoundException("未找到 app\\StarSprout.App.exe。请完整解压启动器后再打开。", AppPath);
            var start = new ProcessStartInfo(AppPath) {
                WorkingDirectory = Path.GetDirectoryName(AppPath), UseShellExecute = false,
                Arguments = string.Join(" ", arguments.Select(QuoteArgument))
            };
            Process.Start(start);
        }

        private static string QuoteArgument(string argument)
        {
            // Windows CommandLineToArgvW escaping, including final backslashes inside quotes.
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char character in argument)
            {
                if (character == '\\') { slashes++; continue; }
                if (character == '"') result.Append('\\', slashes * 2 + 1).Append('"');
                else result.Append('\\', slashes).Append(character);
                slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }

        private static async Task InstallAsync(string installer, string arguments, bool elevate, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Process process;
            try
            {
                process = Process.Start(new ProcessStartInfo(installer, arguments) {
                    UseShellExecute = true, Verb = elevate ? "runas" : "", WindowStyle = ProcessWindowStyle.Hidden
                });
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                if (ex.NativeErrorCode == 1223) throw new OperationCanceledException("Windows 安装授权已取消。", ex, token);
                throw;
            }
            if (process == null) throw new InvalidOperationException("无法启动微软运行库安装程序。");
            using (process)
            {
                // Do not forcibly terminate an installer mid-write. A cancellation prevents
                // the next installation and application start, after this installer exits.
                await Task.Run(() => process.WaitForExit());
                token.ThrowIfCancellationRequested();
                if (process.ExitCode != 0 && process.ExitCode != 3010 && process.ExitCode != 1641)
                    throw new InvalidOperationException("微软运行库安装失败，错误码 " + process.ExitCode + "。请检查网络或系统安装日志后重试。");
            }
        }

        private static async Task<string> ReadMetadataAsync(CancellationToken token)
        {
            using (var client = CreateClient())
            using (var response = await client.GetAsync(MetadataUrl, token))
            {
                response.EnsureSuccessStatusCode();
                ValidateMicrosoftUrl(response.RequestMessage.RequestUri.AbsoluteUri);
                return await response.Content.ReadAsStringAsync();
            }
        }

        internal static InstallerInfo SelectDesktopInstaller(string json)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
            var document = serializer.DeserializeObject(json) as Dictionary<string, object>;
            object releasesObject;
            if (document == null || !document.TryGetValue("releases", out releasesObject)) throw new InvalidDataException("微软 .NET 版本信息格式无效。");
            var candidates = new List<InstallerInfo>();
            foreach (object releaseObject in (IEnumerable)releasesObject)
            {
                var release = releaseObject as Dictionary<string, object>;
                object desktopObject;
                if (release == null || !release.TryGetValue("windowsdesktop", out desktopObject)) continue;
                var desktop = desktopObject as Dictionary<string, object>;
                object versionObject, filesObject;
                if (desktop == null || !desktop.TryGetValue("version", out versionObject) || !desktop.TryGetValue("files", out filesObject)) continue;
                string version = versionObject as string;
                if (!RuntimeDetector.IsDesktop10(version)) continue;
                foreach (object fileObject in (IEnumerable)filesObject)
                {
                    var file = fileObject as Dictionary<string, object>;
                    if (file == null || !file.ContainsKey("rid") || !file.ContainsKey("name") || !file.ContainsKey("url") || !file.ContainsKey("hash")) continue;
                    if ((string)file["rid"] != "win-x64" || !((string)file["name"]).EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    string url = (string)file["url"];
                    ValidateMicrosoftUrl(url);
                    candidates.Add(new InstallerInfo { Version = version, Url = url, Sha512 = (string)file["hash"] });
                }
            }
            var selected = candidates.OrderByDescending(x => Version.Parse(x.Version)).FirstOrDefault();
            if (selected == null) throw new InvalidDataException("微软版本列表中没有可用的 .NET 10 Desktop Runtime x64 正式版安装包。");
            return selected;
        }

        internal static void ValidateMicrosoftUrl(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || uri.Scheme != "https" || !(uri.Host.Equals("microsoft.com", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith(".microsoft.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("dotnetcli.azureedge.net", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.Equals("dotnetcli.blob.core.windows.net", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("运行库下载地址不是受支持的微软官方 HTTPS 地址。");
        }

        private static HttpClient CreateClient()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromMinutes(12) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("StarSprout-Bootstrap/0.2");
            return client;
        }

        private static async Task DownloadAsync(string url, string destination, string name, Action<string, int> progress, CancellationToken token)
        {
            ValidateMicrosoftUrl(url);
            string partial = destination + ".part";
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    progress("正在下载 " + name + (attempt == 2 ? "（第 2 次尝试）" : "") + "…", -1);
                    using (var client = CreateClient())
                    using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token))
                    {
                        response.EnsureSuccessStatusCode();
                        ValidateMicrosoftUrl(response.RequestMessage.RequestUri.AbsoluteUri);
                        long? length = response.Content.Headers.ContentLength;
                        if (length > 800L * 1024 * 1024) throw new InvalidDataException("运行库安装包超出允许大小。");
                        using (var input = await response.Content.ReadAsStreamAsync())
                        using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                        {
                            byte[] buffer = new byte[81920];
                            long written = 0;
                            int count;
                            while ((count = await input.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
                            {
                                written += count;
                                if (written > 800L * 1024 * 1024) throw new InvalidDataException("运行库安装包超出允许大小。");
                                await output.WriteAsync(buffer, 0, count, token);
                                progress("正在下载 " + name + "…", length.HasValue && length.Value > 0 ? (int)Math.Min(100, written * 100 / length.Value) : -1);
                            }
                            if (written == 0 || (length.HasValue && written != length.Value)) throw new IOException("运行库下载不完整。");
                        }
                    }
                    if (File.Exists(destination)) File.Delete(destination);
                    File.Move(partial, destination);
                    return;
                }
                catch (Exception ex) when ((ex is HttpRequestException || ex is IOException) && attempt < 2)
                {
                    if (File.Exists(partial)) File.Delete(partial);
                    await Task.Delay(1200, token);
                }
                finally
                {
                    if (File.Exists(partial)) File.Delete(partial);
                }
            }
        }
    }
}
