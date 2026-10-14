using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using StarSprout.Services;

namespace StarSprout;

internal sealed record BrowserArchive(string Path, string Name, string SourceUrl, bool Install, string GameDirectory);

/// <summary>Original community pages run in a separate browser with no launcher RPC or filesystem bridge.</summary>
internal sealed class CommunityBrowserWindow : Window
{
    private const long MaxArchiveBytes = 512L * 1024 * 1024;
    private readonly WebView2 web = new();
    private readonly TextBlock address = new() { FontSize = 12, Foreground = Brushes.SlateGray, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock status = new() { Text = "软件内浏览原站；ZIP 下载经检查后缓存或安装。", FontSize = 12, Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox install = new() { Content = "下载后安装到当前游戏", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    private readonly Button stop = new() { Content = "取消下载", Padding = new Thickness(12, 6, 12, 6), IsEnabled = false };
    private readonly string profile, downloads;
    private readonly Func<BrowserArchive, CancellationToken, Task<string>> receiveArchive;
    private readonly Func<string, bool, string, CancellationToken, Task<string>> receiveNxm;
    private readonly Func<string> targetDirectory;
    private readonly Func<bool> online;
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<CoreWebView2DownloadOperation> operations = new();
    private Task? initialized;
    private bool closed;
    private CancellationTokenSource? nxmOperation;
    private readonly CoreWebView2Environment? sharedEnvironment;
    private readonly bool popup;
    private CoreWebView2Environment? environment;
    private (string Host, string Folder)? checkMapping;

    public CommunityBrowserWindow(string dataDirectory, Func<BrowserArchive, CancellationToken, Task<string>> receiveArchive,
        Func<string, bool, string, CancellationToken, Task<string>> receiveNxm, Func<string> targetDirectory, Func<bool> online,
        CoreWebView2Environment? sharedEnvironment = null, bool popup = false)
    {
        this.receiveArchive = receiveArchive; this.receiveNxm = receiveNxm; this.targetDirectory = targetDirectory; this.online = online;
        this.sharedEnvironment = sharedEnvironment; this.popup = popup;
        profile = System.IO.Path.Combine(dataDirectory, "Profile"); downloads = System.IO.Path.Combine(dataDirectory, "Downloads");
        CheckDirectory(downloads); Directory.CreateDirectory(downloads);
        Title = "资源浏览 · 星芽启动器"; Width = 1120; Height = 790; MinWidth = 800; MinHeight = 550;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = new SolidColorBrush(Color.FromRgb(247, 249, 252));
        var layout = new DockPanel();
        var toolbar = new DockPanel { Margin = new Thickness(12) };
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, action) in new (string, Action)[] { ("后退", () => { if (web.CoreWebView2?.CanGoBack == true) web.GoBack(); }),
            ("前进", () => { if (web.CoreWebView2?.CanGoForward == true) web.GoForward(); }), ("刷新", () => web.Reload()) })
        {
            var button = new Button { Content = label, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 7, 0) };
            button.Click += (_, _) => { if (online()) action(); else status.Text = "离线模式已停止网页访问。"; };
            controls.Children.Add(button);
        }
        controls.Children.Add(stop); controls.Children.Add(install);
        toolbar.Children.Add(controls); address.Margin = new Thickness(16, 0, 8, 0); toolbar.Children.Add(address);
        DockPanel.SetDock(toolbar, Dock.Top); layout.Children.Add(toolbar);
        var footer = new Border { Padding = new Thickness(14, 10, 14, 10), Child = status };
        DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer); layout.Children.Add(web); Content = layout;
        stop.Click += (_, _) => { nxmOperation?.Cancel(); foreach (var operation in operations.ToArray()) if (operation.State == CoreWebView2DownloadState.InProgress) operation.Cancel(); };
        Closed += (_, _) =>
        {
            closed = true; lifetime.Cancel();
            foreach (var operation in operations.ToArray()) if (operation.State == CoreWebView2DownloadState.InProgress) operation.Cancel();
            web.Dispose();
        };
    }

    public async Task NavigateAsync(string url, bool autoInstall = false)
    {
        if (!online()) throw new InvalidOperationException("离线模式已暂停社区网页访问。");
        ValidateNavigation(url);
        install.IsChecked = autoInstall;
        if (Uri.TryCreate(url, UriKind.Absolute, out var destination) && IsNexusHost(destination.Host))
            status.Text = "在此窗口登录 Nexus 后选择 Manual Download / Slow Download，ZIP 会自动缓存和安装；管理器下载使用已连接的 API 账号。大文件请选择 Standard download。";
        if (!IsVisible) Show(); Activate();
        initialized ??= Initialize();
        await initialized;
        web.CoreWebView2.Navigate(url);
    }

    internal async Task PrepareCheckAsync(string hostname, string folder)
    {
        if (!IsVisible) Show();
        initialized ??= Initialize(); await initialized;
        checkMapping = (hostname, folder);
        web.CoreWebView2.SetVirtualHostNameToFolderMapping(hostname, folder, CoreWebView2HostResourceAccessKind.DenyCors);
    }
    internal Task<string> CheckScriptAsync(string script) => web.CoreWebView2.ExecuteScriptAsync(script);
    internal bool HasLauncherBridge => web.CoreWebView2.Settings.IsWebMessageEnabled || web.CoreWebView2.Settings.AreHostObjectsAllowed;

    private async Task Initialize()
    {
        CheckDirectory(profile);
        environment = sharedEnvironment ?? await CoreWebView2Environment.CreateAsync(null, profile);
        await web.EnsureCoreWebView2Async(environment);
        var core = web.CoreWebView2;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.PermissionRequested += (_, e) =>
        {
            // The local diagnostic issues two synthetic downloads without a real mouse gesture.
            // Actual community pages retain the browser's per-click download requirement.
            var localDiagnostic = checkMapping is { } mapping && Uri.TryCreate(e.Uri, UriKind.Absolute, out var origin) && origin.Host == mapping.Host;
            e.State = localDiagnostic && e.PermissionKind == CoreWebView2PermissionKind.MultipleAutomaticDownloads
                ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
        };
        core.NavigationStarting += (_, e) =>
        {
            if (e.Uri.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase))
            { e.Cancel = true; _ = HandleNxm(e.Uri, install.IsChecked == true, targetDirectory()); return; }
            try { if (!online()) throw new InvalidOperationException("离线模式已暂停网页访问。"); if (!(popup && e.Uri == "about:blank")) ValidateNavigation(e.Uri); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { e.Cancel = true; status.Text = ex.Message; }
        };
        // Cover frame-initiated schemes as well as top-level navigation; never hand them to Windows.
        core.LaunchingExternalUriScheme += (_, e) =>
        {
            e.Cancel = true;
            if (e.Uri.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase)) _ = HandleNxm(e.Uri, install.IsChecked == true, targetDirectory());
            else status.Text = "此链接使用外部应用协议，请在当前窗口继续浏览。";
        };
        core.SourceChanged += (_, _) =>
        {
            if (Uri.TryCreate(core.Source, UriKind.Absolute, out var uri)) address.Text = uri.Host + uri.AbsolutePath;
        };
        core.NavigationCompleted += (_, e) => { if (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.ConnectionAborted) status.Text = "原站页面未加载成功：" + e.WebErrorStatus + "。可刷新重试；不会跳转外部浏览器。"; };
        core.NewWindowRequested += OpenPopup;
        core.WindowCloseRequested += (_, _) => Close();
        core.DownloadStarting += DownloadStarting;
    }

    private async void OpenPopup(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (e.Uri.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase)) { _ = HandleNxm(e.Uri, install.IsChecked == true, targetDirectory()); return; }
        var isLocalCheck = checkMapping is { } mapping && Uri.TryCreate(e.Uri, UriKind.Absolute, out var checkUri) && checkUri.Host == mapping.Host;
        if (!e.IsUserInitiated && !isLocalCheck) return;
        using var deferral = e.GetDeferral();
        CommunityBrowserWindow? child = null;
        try
        {
            if (!online()) return;
            if (e.Uri != "about:blank") ValidateNavigation(e.Uri);
            child = new CommunityBrowserWindow(Path.GetDirectoryName(profile)!, receiveArchive, receiveNxm, targetDirectory, online, environment, true)
            { Owner = this, Icon = Icon, ShowInTaskbar = !isLocalCheck };
            child.install.IsChecked = install.IsChecked;
            if (isLocalCheck) { child.WindowStartupLocation = WindowStartupLocation.Manual; child.Left = -8000; }
            child.Show();
            child.initialized = child.Initialize(); await child.initialized;
            if (checkMapping is { } check) await child.PrepareCheckAsync(check.Host, check.Folder);
            if (closed) { child.Close(); return; }
            e.NewWindow = child.web.CoreWebView2;
            e.Handled = false;
        }
        catch (Exception ex) { child?.Close(); if (!closed) status.Text = "登录弹窗未打开：" + ex.Message; }
    }

    private async Task HandleNxm(string uri, bool autoInstall, string gameDirectory)
    {
        if (nxmOperation is not null) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        nxmOperation = cancellation; stop.IsEnabled = true;
        try
        {
            if (!online()) throw new InvalidOperationException("离线模式已暂停下载。");
            NexusService.ParseNxm(uri);
            status.Text = "正在接收 Nexus 下载授权并下载文件…";
            var message = await receiveNxm(uri, autoInstall, gameDirectory, cancellation.Token);
            if (!closed) status.Text = message;
        }
        catch (NexusBrowserDownloadRequiredException ex)
        {
            if (!closed && !cancellation.IsCancellationRequested)
            {
                try { await NavigateAsync(ex.PageUrl.AbsoluteUri, autoInstall); status.Text = ex.Message; }
                catch (Exception navigationError) { if (!closed) status.Text = navigationError.Message; }
            }
        }
        catch (Exception ex) { if (!closed) status.Text = ex is OperationCanceledException ? "下载已取消。" : ex.Message; }
        finally { nxmOperation = null; if (!closed) stop.IsEnabled = operations.Count > 0; }
    }

    private void DownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        e.Handled = true;
        var operation = e.DownloadOperation;
        var originalName = System.IO.Path.GetFileName(e.ResultFilePath);
        try
        {
            if (!online()) throw new InvalidOperationException("离线模式已暂停下载。");
            ValidateArchiveSource(operation.Uri, web.CoreWebView2.Source);
            if (!originalName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("仅支持完整 SMAPI Mod ZIP；网盘页面、EXE、RAR 或 7z 不会自动安装。");
            if (operation.TotalBytesToReceive > MaxArchiveBytes) throw new InvalidDataException("Mod ZIP 超过 512 MB，已停止下载。");
        }
        catch (Exception ex) { e.Cancel = true; status.Text = ex.Message; return; }
        var archive = new BrowserArchive(System.IO.Path.Combine(downloads, Guid.NewGuid().ToString("N") + ".zip"), originalName,
            operation.Uri.StartsWith("blob:", StringComparison.OrdinalIgnoreCase) ? web.CoreWebView2.Source : operation.Uri,
            install.IsChecked == true, targetDirectory());
        e.ResultFilePath = archive.Path;
        operations.Add(operation); stop.IsEnabled = true;
        operation.BytesReceivedChanged += (_, _) =>
        {
            if (!online() || operation.BytesReceived > MaxArchiveBytes) { operation.Cancel(); return; }
            if (!closed) status.Text = "下载 " + originalName + " · " + (operation.BytesReceived / 1048576d).ToString("F1") + " MB";
        };
        operation.StateChanged += async (_, _) =>
        {
            if (operation.State == CoreWebView2DownloadState.InProgress) return;
            if (!operations.Remove(operation)) return;
            if (!closed) stop.IsEnabled = operations.Count > 0 || nxmOperation is not null;
            try
            {
                if (operation.State != CoreWebView2DownloadState.Completed) throw new InvalidOperationException("下载已停止：" + operation.InterruptReason);
                if (!closed) status.Text = "下载完成，正在检查 ZIP 与模组清单…";
                var message = await receiveArchive(archive, lifetime.Token);
                if (!closed) status.Text = message;
            }
            catch (Exception ex) { if (!closed) status.Text = ex is OperationCanceledException ? "下载已取消。" : ex.Message; }
            finally
            {
                try { CheckDirectory(archive.Path); if (File.Exists(archive.Path)) File.Delete(archive.Path); }
                catch (IOException) { }
            }
        };
    }

    internal static Uri ValidateNavigation(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) throw new ArgumentException("网页地址无效。");
        // Fragment links are normal browser navigation; downloads remain verified by ContentService.
        var clean = new UriBuilder(uri) { Fragment = "" }.Uri;
        ContentService.ValidateUrl(clean.AbsoluteUri);
        if (uri.Host.Equals("starsprout.local", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".starsprout.local", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("社区网页不能访问启动器本地资源。");
        return uri;
    }
    internal static void ValidateArchiveSource(string url, string pageUrl)
    {
        if (!url.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)) { ValidateNavigation(url); return; }
        // Object URLs are browser-owned bytes, still subject to the same ZIP/manifest checks after download.
        var origin = ValidateNavigation(url[5..]);
        var page = ValidateNavigation(pageUrl);
        if (!origin.GetLeftPart(UriPartial.Authority).Equals(page.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("下载对象不属于当前网页。");
    }
    private static bool IsNexusHost(string host) => host.Equals("nexusmods.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".nexusmods.com", StringComparison.OrdinalIgnoreCase);
    private static void CheckDirectory(string path)
    {
        for (var part = System.IO.Path.GetFullPath(path); !string.IsNullOrEmpty(part); part = System.IO.Path.GetDirectoryName(part))
            if ((File.Exists(part) || Directory.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("网页缓存目录不支持链接路径。");
    }
}
