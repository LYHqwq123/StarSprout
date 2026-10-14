using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Interop;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using StarSprout.Services;

namespace StarSprout;

public sealed class LauncherWindow : Window
{
    private readonly WebView2 browser = new();
    private readonly string data = AppData();
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly StardewService service;
    private readonly StardewService offlineGame;
    private readonly ContentService contentService;
    private readonly LogAnalysisService analysis;
    private readonly NexusService nexus;
    private readonly AppearanceService appearance;
    private readonly HomeService home;
    private readonly SmapiDownloadService smapiDownloads;
    private int? calendarYear, calendarMonth;
    private string launchWarning = "";
    private readonly CommunityCatalogService communities;
    private readonly ModTroubleshootingService normalModState, offlineModState;
    private readonly Dictionary<long, string> pendingNexusInstalls = new();
    private bool maximized;
    private string operationNotice = "";
    private ModInstallResult? installationResult;
    private readonly bool appearanceCheck = Environment.GetCommandLineArgs().Contains("--appearance-test");
    private CancellationTokenSource? currentOperation;
    private ContentCatalog? lastCatalog;
    private readonly bool smokeCheck = Environment.GetCommandLineArgs().Contains("--smoke");
    private readonly string? initialNxm;
    private string runningGameDirectory = "";
    private Process? game;
    private bool closing;
    private readonly JsonSerializerSettings jsonSettings = new() { ContractResolver = new CamelCasePropertyNamesContractResolver() };

    public LauncherWindow(string? nxmLink = null)
    {
        Title = "星芽启动器 · StarSprout";
        Width = 1160; Height = 760; MinWidth = 900; MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.None;
        WindowBounds.Attach(this);
        ResizeMode = ResizeMode.CanResize;
        Background = new SolidColorBrush(Color.FromRgb(248, 250, 253));
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0) });
        using (var icon = Assembly.GetExecutingAssembly().GetManifestResourceStream("StarSprout.icon.ico"))
            if (icon != null) Icon = BitmapFrame.Create(icon, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var testSaves = appearanceCheck ? Path.Combine(data, "TestProfile", "Saves") : null;
        service = new StardewService(Path.Combine(data, "Settings"), testSaves);
        offlineGame = new StardewService(Path.Combine(data, "OfflineGame"), testSaves);
        contentService = new ContentService(Path.Combine(data, "Content"), service);
        analysis = new LogAnalysisService(Path.Combine(data, "AI"), service);
        nexus = new NexusService(Path.Combine(data, "Nexus"), contentService);
        appearance = new AppearanceService(Path.Combine(data, "Appearance"));
        home = new HomeService(Path.Combine(data, "Home"));
        smapiDownloads = new SmapiDownloadService(Path.Combine(data, "SMAPI"));
        smapiDownloads.Progress += progress => Dispatcher.BeginInvoke(() =>
        {
            if (!closing) browser.CoreWebView2?.PostWebMessageAsJson(JsonConvert.SerializeObject(new { type = "progress", progress }, jsonSettings));
        });
        communities = new CommunityCatalogService(Path.Combine(data, "Communities"));
        normalModState = new ModTroubleshootingService(service, Path.Combine(data, "Settings"));
        offlineModState = new ModTroubleshootingService(offlineGame, Path.Combine(data, "OfflineGame"));
        nexus.OpenLoginPage = url => OpenCommunityLink(url);
        StateChanged += (_, _) =>
        {
            maximized = WindowState == WindowState.Maximized;
            if (!closing) browser.CoreWebView2?.PostWebMessageAsJson(JsonConvert.SerializeObject(new { type = "windowState", maximized }, jsonSettings));
        };
        initialNxm = nxmLink;
        contentService.Progress += progress => Dispatcher.BeginInvoke(() =>
        {
            if (closing) return;
            try { browser.CoreWebView2?.PostWebMessageAsJson(JsonConvert.SerializeObject(new { type = "progress", progress }, jsonSettings)); }
            catch (ObjectDisposedException) { }
        });
        Content = browser;
        Loaded += Initialize;
        Closed += (_, _) => { closing = true; currentOperation?.Cancel(); browser.Dispose(); game?.Dispose(); contentService.Dispose(); analysis.Dispose(); nexus.Dispose(); communities.Dispose(); smapiDownloads.Dispose(); };
    }

    private async void Initialize(object sender, RoutedEventArgs e)
    {
        var arguments = Environment.GetCommandLineArgs();
        var smoke = Array.IndexOf(arguments, "--smoke");
        if (smoke >= 0 || arguments.Contains("--browser-test") || appearanceCheck) { ShowInTaskbar = false; Left = -8000; }
        var ui = Path.Combine(data, "Ui");
        Directory.CreateDirectory(ui);
        using (var input = Assembly.GetExecutingAssembly().GetManifestResourceStream("StarSprout.index.html")!)
        using (var output = File.Create(Path.Combine(ui, "index.html"))) await input.CopyToAsync(output);
        var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(data, "WebView"));
        await browser.EnsureCoreWebView2Async(environment);
        var core = browser.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.SetVirtualHostNameToFolderMapping("starsprout.local", ui, CoreWebView2HostResourceAccessKind.DenyCors);
        core.SetVirtualHostNameToFolderMapping("appearance.starsprout.local", appearance.AssetsDirectory, CoreWebView2HostResourceAccessKind.Allow);
        core.SetVirtualHostNameToFolderMapping("home.starsprout.local", home.AssetsDirectory, CoreWebView2HostResourceAccessKind.Allow);
        core.WebMessageReceived += Receive;
        core.NewWindowRequested += (_, ev) => { ev.Handled = true; };
        core.NavigationStarting += (_, ev) =>
        {
            if (!ev.Uri.StartsWith("https://starsprout.local/", StringComparison.OrdinalIgnoreCase)) ev.Cancel = true;
        };
        if (!appearanceCheck && !contentService.GetState().Offline && string.IsNullOrWhiteSpace(service.Settings.GameDirectory))
        {
            var folders = await Task.Run(service.DetectGameFolders);
            if (folders.Count > 0) service.SetGameDirectory(folders[0]);
        }
        core.Navigate("https://starsprout.local/index.html");
        var dragCheck = Array.IndexOf(arguments, "--drag-test");
        var browserCheck = Array.IndexOf(arguments, "--browser-test");
        var themeCheck = Array.IndexOf(arguments, "--appearance-test");
        if (themeCheck >= 0 && themeCheck + 1 < arguments.Length)
        {
            for (var i = 0; i < 50 && await core.ExecuteScriptAsync("Boolean(window.appReady)") != "true"; i++) await Task.Delay(100);
            await AppearanceSmoke.Run(this, browser, appearance, arguments[themeCheck + 1]); Close();
        }
        else if (browserCheck >= 0 && browserCheck + 1 < arguments.Length)
        {
            await BrowserSmoke.Run(this, arguments[browserCheck + 1]); Close();
        }
        else if (dragCheck >= 0 && dragCheck + 1 < arguments.Length)
        {
            for (var i = 0; i < 50 && await browser.CoreWebView2.ExecuteScriptAsync("Boolean(window.appReady)") != "true"; i++) await Task.Delay(200);
            await DragSmoke.Run(this, arguments[dragCheck + 1]);
            Close();
        }
        else if (smoke >= 0 && smoke + 1 < arguments.Length) await Smoke(arguments[smoke + 1]);
        else
        {
            NxmIntegration.Listen(value => Dispatcher.InvokeAsync(() => HandleNxm(value)).Task.Unwrap());
            for (var i = 0; i < 50 && await core.ExecuteScriptAsync("Boolean(window.appReady)") != "true"; i++) await Task.Delay(200);
            if (initialNxm is not null) await HandleNxm(initialNxm);
        }
    }

    private async void Receive(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!e.Source.StartsWith("https://starsprout.local/", StringComparison.OrdinalIgnoreCase)) return;
        long? id = null;
        try
        {
            var request = JObject.Parse(e.WebMessageAsJson);
            id = request.Value<long>("id");
            var action = request.Value<string>("action") ?? "";
            var payload = request["payload"] as JObject ?? new JObject();
            if (action == "close") { currentOperation?.Cancel(); Close(); return; }
            if (action == "maximize")
            {
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                browser.CoreWebView2.PostWebMessageAsJson(JsonConvert.SerializeObject(new { id, result = new { maximized } }, jsonSettings));
                return;
            }
            if (action == "minimize")
            {
                WindowState = WindowState.Minimized;
                browser.CoreWebView2.PostWebMessageAsJson(JsonConvert.SerializeObject(new { id, result = true }, jsonSettings));
                return;
            }
            if (action == "drag")
            {
                var moved = WindowDrag.Begin(new WindowInteropHelper(this).Handle);
                browser.CoreWebView2.PostWebMessageAsJson(JsonConvert.SerializeObject(new { id, result = moved }, jsonSettings));
                return;
            }
            if (action == "cancelTask")
            {
                currentOperation?.Cancel();
                browser.CoreWebView2.PostWebMessageAsJson(JsonConvert.SerializeObject(new { id, result = true }, jsonSettings));
                return;
            }
            await gate.WaitAsync();
            object? result;
            currentOperation = new CancellationTokenSource();
            try { result = await Perform(action, payload, currentOperation.Token); }
            finally { currentOperation.Dispose(); currentOperation = null; gate.Release(); }
            if (!closing) browser.CoreWebView2.PostWebMessageAsJson(JsonConvert.SerializeObject(new { id, result }, jsonSettings));
        }
        catch (Exception ex)
        {
            if (!closing && browser.CoreWebView2 != null) browser.CoreWebView2.PostWebMessageAsJson(JsonConvert.SerializeObject(new { id, error = ex is OperationCanceledException ? "操作已取消。" : ex.Message }, jsonSettings));
        }
    }

    private async Task<object?> Perform(string action, JObject payload, CancellationToken ct)
    {
        var active = ActiveGame();
        contentService.SetInstallation(active);
        analysis.SetInstallation(active);
        communities.Offline = contentService.GetState().Offline;
        if (IsRunning() && (action is "toggleMod" or "setModsEnabled" or "disableAllMods" or "restoreModState" or "removeMod" or "importMod" or "installCached" ||
            action is "downloadContent" or "downloadUrl" or "downloadNexus" or "downloadCommunity" && payload.Value<bool>("install")))
            throw new InvalidOperationException("请先退出游戏，再安装或调整 Mod；下载到缓存仍可进行。");
        switch (action)
        {
            case "snapshot": return await Snapshot();
            case "downloadSmapi":
                smapiDownloads.Offline = contentService.GetState().Offline;
                var smapiPackage = await smapiDownloads.DownloadAsync(payload.Value<string>("source") ?? "domestic", ct);
                operationNotice = smapiPackage.Message;
                OpenUrl(smapiPackage.Directory);
                return await Snapshot();
            case "homeMonth":
                var selectedYear = payload.Value<int>("year"); var selectedMonth = payload.Value<int>("month");
                home.GetState(selectedYear, selectedMonth); calendarYear = selectedYear; calendarMonth = selectedMonth; return await Snapshot();
            case "homeToday": calendarYear = null; calendarMonth = null; return await Snapshot();
            case "chooseHomeImage":
                var homeImage = new OpenFileDialog { Title = "选择首页游戏图片", Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp", CheckFileExists = true };
                if (homeImage.ShowDialog(this) == true) await Task.Run(() => home.ImportImage(homeImage.FileName), ct);
                return await Snapshot();
            case "clearHomeImage": home.ClearImage(); return await Snapshot();
            case "saveAppearance": appearance.SaveSettings(payload.ToObject<AppearanceSettingsInput>()!); return await Snapshot();
            case "chooseBackground":
                var image = new OpenFileDialog { Title = "选择启动器背景图片", Filter = "背景图片|*.png;*.jpg;*.jpeg;*.bmp", CheckFileExists = true };
                if (image.ShowDialog(this) == true) await Task.Run(() => appearance.ImportBackground(image.FileName), ct);
                return await Snapshot();
            case "clearBackground": appearance.ClearBackground(); return await Snapshot();
            case "chooseGame":
                var folder = new OpenFolderDialog { Title = "选择包含 Stardew Valley.exe 的文件夹", InitialDirectory = active.Settings.GameDirectory };
                if (folder.ShowDialog(this) == true) active.SetGameDirectory(folder.FolderName);
                return await Snapshot();
            case "detectGame":
                if (contentService.GetState().Offline) throw new InvalidOperationException("独立版请使用「选择游戏目录」，自动检测用于游戏库中的已安装游戏。");
                var folders = await Task.Run(service.DetectGameFolders);
                if (folders.Count == 0) throw new InvalidOperationException("没有找到游戏，请手动选择安装文件夹。");
                service.SetGameDirectory(folders[0]);
                return await Snapshot();
            case "setMode":
                active.Settings.UseSmapi = payload.Value<bool>("useSmapi");
                active.SaveSettings();
                return await Snapshot();
            case "launch":
                if (IsRunning()) throw new InvalidOperationException("游戏正在运行。");
                game = await Task.Run(() => active.Launch(payload.Value<bool>("useSmapi")), ct);
                runningGameDirectory = active.Settings.GameDirectory;
                RecordLaunchDay(payload.Value<string>("accent"));
                return await Snapshot();
            case "importMod":
                var zip = new OpenFileDialog { Title = "导入 Mod ZIP", Filter = "Mod ZIP (*.zip)|*.zip", CheckFileExists = true };
                if (zip.ShowDialog(this) == true) await Task.Run(() => active.InstallModZip(zip.FileName), ct);
                return await Snapshot();
            case "toggleMod":
                var mod = active.GetMods().FirstOrDefault(m => string.Equals(m.DirectoryPath, payload.Value<string>("directoryPath"), StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException("Mod 已移动，请刷新列表。");
                await Task.Run(() => ActiveModState().SetEnabled(new[] { mod.DirectoryPath }, payload.Value<bool>("enabled")), ct);
                return await Snapshot();
            case "removeMod":
                var removal = active.GetMods().FirstOrDefault(m => string.Equals(m.DirectoryPath, payload.Value<string>("directoryPath"), StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("Mod 已移动，请刷新列表。");
                if (MessageBox.Show(this, $"删除 {removal.Name}？\n\n将从当前游戏移除该 Mod，并保存完整副本，方便恢复。\n{removal.DirectoryPath}", "删除 Mod", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return new { cancelled = true };
                var removed = await Task.Run(() => active.RemoveMod(removal.DirectoryPath), ct);
                operationNotice = $"已删除 {removed.Name}，完整副本已保存。";
                if (removed.CleanupPending) operationNotice += " 被占用的临时文件位于游戏目录，游戏不会加载它。";
                return await Snapshot();
            case "setModsEnabled":
                var selections = payload["directoryPaths"]?.ToObject<List<string>>() ?? new();
                await Task.Run(() => ActiveModState().SetEnabled(selections, payload.Value<bool>("enabled")), ct); return await Snapshot();
            case "disableAllMods": await Task.Run(ActiveModState().DisableAll, ct); return await Snapshot();
            case "restoreModState": await Task.Run(ActiveModState().Restore, ct); return await Snapshot();
            case "backupSave":
                var save = active.GetSaves().FirstOrDefault(s => string.Equals(s.DirectoryPath, payload.Value<string>("directoryPath"), StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException("存档已移动，请刷新列表。");
                return new { path = await Task.Run(() => active.BackupSave(save), ct) };
            case "catalog": return lastCatalog = await contentService.GetCatalogAsync(smokeCheck, ct);
            case "catalogCached": return lastCatalog = await contentService.GetCatalogAsync(true, ct);
            case "contentDetail": return await contentService.GetDetailAsync(payload.Value<string>("itemId") ?? "", ct);
            case "communityList":
                if (contentService.GetState().Offline) throw new InvalidOperationException("离线模式已暂停社区目录查询，可安装已缓存资源。");
                return await communities.ListAsync(payload.Value<string>("source") ?? "3dm", payload.Value<string>("query"), payload.Value<int?>("page") ?? 1, ct);
            case "communityDetail":
                if (contentService.GetState().Offline) throw new InvalidOperationException("离线模式已暂停社区详情查询。");
                return await communities.GetDetailAsync(payload.Value<string>("source") ?? "3dm", payload.Value<string>("id") ?? "", ct);
            case "downloadCommunity":
                if (contentService.GetState().Offline) throw new InvalidOperationException("离线模式已暂停社区下载。");
                if (payload.Value<bool>("install") && !active.GetInstallation().HasGame) throw new InvalidOperationException("请先在游戏库选择一次游戏目录，之后即可一键下载安装。");
                contentService.ReportPhase("正在查找社区的公开 ZIP", "resolving");
                var detail = await communities.GetDetailAsync(payload.Value<string>("source") ?? "3dm", payload.Value<string>("id") ?? "", ct);
                var downloadIndex = payload.Value<int?>("downloadIndex") ?? -1;
                if (downloadIndex == -1) downloadIndex = detail.Downloads.FindIndex(d => d.CanInstall);
                if (downloadIndex < 0 || downloadIndex >= detail.Downloads.Count || !detail.Downloads[downloadIndex].CanInstall)
                    throw new InvalidOperationException("此 Mod 没有公开 ZIP，无法直接下载安装，请在详情中查看作者的下载方式。");
                var communityDownload = await contentService.DownloadUrlAsync(detail.Downloads[downloadIndex].Url, null, payload.Value<bool>("install"), ct, detail.Name, detail.Version, detail.SourceUrl);
                SetDownloadNotice(communityDownload);
                return await Snapshot();
            case "addFeed": await contentService.AddFeedAsync(payload.Value<string>("url") ?? "", ct); return await Snapshot();
            case "removeFeed": contentService.RemoveFeed(payload.Value<string>("url") ?? ""); return await Snapshot();
            case "downloadContent":
                SetDownloadNotice(await contentService.DownloadAsync(payload.Value<string>("itemId") ?? "", payload.Value<bool>("install"), ct)); return await Snapshot();
            case "downloadUrl":
                SetDownloadNotice(await contentService.DownloadUrlAsync(payload.Value<string>("url") ?? "", payload.Value<string>("sha256"), payload.Value<bool>("install"), ct)); return await Snapshot();
            case "installCached": SetDownloadNotice(await contentService.InstallCached(payload.Value<string>("itemId") ?? "", ct)); return await Snapshot();
            case "setOffline":
                if (IsRunning()) throw new InvalidOperationException("请先退出游戏，再切换离线目录。");
                contentService.SetOffline(payload.Value<bool>("offline"));
                smapiDownloads.Offline = payload.Value<bool>("offline");
                communities.Offline = payload.Value<bool>("offline");
                if (payload.Value<bool>("offline")) pendingNexusInstalls.Clear();
                return await Snapshot();
            case "exportOffline":
                var export = new SaveFileDialog { Title = "导出已下载的离线 Mod 包", Filter = "离线包 (*.zip)|*.zip", FileName = "StarSprout-Offline-Mods.zip" };
                if (export.ShowDialog(this) == true) await contentService.ExportOfflineAsync(export.FileName, ct);
                return await Snapshot();
            case "importOffline":
                var import = new OpenFileDialog { Title = "导入星芽离线包", Filter = "离线包 (*.zip)|*.zip", CheckFileExists = true };
                if (import.ShowDialog(this) == true) await contentService.ImportOfflineAsync(import.FileName, ct);
                return await Snapshot();
            case "chooseOfflineGame":
                var offlineFolder = new OpenFolderDialog { Title = "选择独立版 / GOG 游戏目录", InitialDirectory = offlineGame.Settings.GameDirectory };
                if (offlineFolder.ShowDialog(this) == true) offlineGame.SetGameDirectory(offlineFolder.FolderName);
                return await Snapshot();
            case "launchOffline":
                if (IsRunning()) throw new InvalidOperationException("游戏正在运行。");
                game = await Task.Run(() => offlineGame.Launch(payload.Value<bool>("useSmapi")), ct);
                runningGameDirectory = offlineGame.Settings.GameDirectory;
                RecordLaunchDay(payload.Value<string>("accent"));
                return await Snapshot();
            case "saveAnalysis": analysis.SaveSettings(payload.ToObject<AnalysisSettingsInput>()!); return await Snapshot();
            case "selectAnalysisProfile": analysis.SelectProfile(payload.Value<string>("profileId") ?? ""); return await Snapshot();
            case "deleteAnalysisProfile": analysis.DeleteProfile(payload.Value<string>("profileId") ?? ""); return await Snapshot();
            case "analysisModels":
                if (contentService.GetState().Offline) throw new InvalidOperationException("离线模式已暂停联网模型查询，可手动填写模型名称。");
                return await analysis.GetModelsAsync(ct);
            case "analysisPreview": return await Task.Run(analysis.GetPreview, ct);
            case "analyzeLog":
                if (contentService.GetState().Offline) throw new InvalidOperationException("离线模式只提供本地检查，联网 AI 分析已暂停。");
                if (!payload.Value<bool>("confirmed")) throw new InvalidOperationException("请先查看脱敏预览并同意发送到已配置的 API。");
                return await analysis.AnalyzeAsync(ct);
            case "saveNexusKey": await nexus.SaveApiKeyAsync(payload.Value<string>("apiKey") ?? "", ct); return await Snapshot();
            case "clearNexus": nexus.ClearKey(); return await Snapshot();
            case "saveNexusApplication": nexus.SaveApplicationId(payload.Value<string>("applicationId") ?? ""); return await Snapshot();
            case "startNexusLogin":
                await nexus.StartSsoAsync(ct); return await Snapshot();
            case "getNexusMod": return await nexus.GetModAsync(payload.Value<long>("modId"), ct);
            case "downloadNexus": SetDownloadNotice(await nexus.DownloadFileAsync(payload.Value<long>("modId"), payload.Value<long>("fileId"), payload.Value<bool>("install"), ct)); return await Snapshot();
            case "registerNxm": NxmIntegration.Register(Path.Combine(AppRoot(), "StarSprout.exe")); return true;
            case "openNexusLink":
                await OpenCommunityLink(NexusService.NormalizeModPageUrl(payload.Value<string>("url") ?? "").AbsoluteUri, payload.Value<bool>("install")); return true;
            case "openNexusMod":
                var modId = payload.Value<long>("modId");
                if (modId <= 0) throw new ArgumentException("请填写有效的 Mod ID。");
                var fileId = payload.Value<long>("fileId");
                var fileQuery = fileId > 0 ? $"&file_id={fileId}&nmm=1" : "";
                await OpenCommunityLink($"https://www.nexusmods.com/stardewvalley/mods/{modId}?tab=files{fileQuery}", payload.Value<bool>("install")); return true;
            case "openCommunity":
                var communityUrl = payload.Value<string>("kind") switch
                {
                    "3dm" => "https://mod.3dmgame.com/stardewvalley/133", "smallfire" => "https://svmbbs.smallfire.cn/",
                    "nexus" => "https://www.nexusmods.com/stardewvalley",
                    "esous" => "https://esous.cn/", "gog" => "https://www.gog.com/en/game/stardew_valley",
                    _ => throw new ArgumentException("未知社区。")
                };
                if (payload.Value<string>("id") is { Length: > 0 } communityId && payload.Value<string>("kind") is "3dm" or "smallfire")
                    communityUrl = (await communities.GetDetailAsync(payload.Value<string>("kind")!, communityId, ct)).SourceUrl;
                if (payload.Value<string>("kind") == "gog") OpenUrl(communityUrl);
                else await OpenCommunityLink(communityUrl, payload.Value<bool>("install"));
                return true;
            case "openContentSource":
                var catalog = lastCatalog ?? await contentService.GetCatalogAsync(true);
                var item = catalog.Items.FirstOrDefault(i => i.Id == payload.Value<string>("itemId")) ?? throw new ArgumentException("未找到来源，请刷新目录。");
                await OpenCommunityLink(ContentService.ValidateUrl(item.SourceUrl).AbsoluteUri, payload.Value<bool>("install")); return true;
            case "openFolder":
                OpenFolder(payload.Value<string>("kind") ?? ""); return true;
            case "openUrl":
                var url = payload.Value<string>("kind") switch
                {
                    "nexus" => "https://www.nexusmods.com/stardewvalley",
                    "smapi" => "https://smapi.io/",
                    "docs" => "https://stardewvalleywiki.com/Modding:Installing_SMAPI_on_Windows",
                    "source" => "https://github.com/Mystic-Stars/Axolotl",
                    "author" => "https://github.com/LYHqwq123",
                    "project" => "https://github.com/LYHqwq123/StarSprout",
                    "junigrid" => "https://github.com/MLD-yu/JuniGrid",
                    "smapiSource" => "https://github.com/Pathoschild/SMAPI",
                    "webview2" => "https://github.com/MicrosoftEdge/WebView2Feedback",
                    "json" => "https://github.com/JamesNK/Newtonsoft.Json",
                    "nexusKeys" => "https://www.nexusmods.com/users/myaccount?tab=api%20keys",
                    _ => throw new ArgumentException("未知链接。")
                };
                if (payload.Value<string>("kind") is "nexus" or "nexusKeys") await OpenCommunityLink(url);
                else Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return true;
            case "minimize": WindowState = WindowState.Minimized; return true;
            case "close": Close(); return null;
            default: throw new ArgumentException("未知操作。");
        }
    }

    private bool IsRunning()
    {
        try { return game is not null && !game.HasExited; }
        catch (InvalidOperationException) { return false; }
    }

    private StardewService ActiveGame() => contentService.GetState().Offline ? offlineGame : service;
    private ModTroubleshootingService ActiveModState() => contentService.GetState().Offline ? offlineModState : normalModState;
    private Task<object> Snapshot() => Task.Run<object>(() => new
    {
        installation = ActiveGame().GetInstallation(),
        settings = ActiveGame().Settings,
        mods = ActiveGame().GetMods(),
        saves = ActiveGame().GetSaves(),
        log = ActiveGame().GetRecentLog(),
        running = IsRunning() && runningGameDirectory == ActiveGame().Settings.GameDirectory,
        content = contentService.GetState(), analysis = analysis.GetSettings(), nexus = nexus.GetState(),
        appearance = appearance.GetSettings(), window = new { maximized }, modTroubleshooting = ActiveModState().GetState(),
        home = home.GetState(calendarYear, calendarMonth), launchWarning,
        smapiDownload = smapiDownloads.GetState(),
        community = communities.GetState(),
        operationNotice, installationResult,
        offlineGame = new { installation = offlineGame.GetInstallation(), settings = offlineGame.Settings, running = IsRunning() && runningGameDirectory == offlineGame.Settings.GameDirectory }
    });

    private void OpenFolder(string kind)
    {
        var roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StardewValley");
        var folder = kind switch
        {
            "game" => ActiveGame().Settings.GameDirectory,
            "mods" when !string.IsNullOrWhiteSpace(ActiveGame().Settings.GameDirectory) => Path.Combine(ActiveGame().Settings.GameDirectory, "Mods"),
            "saves" => Path.Combine(roaming, "Saves"),
            "logs" => Path.Combine(roaming, "ErrorLogs"),
            "backups" => Path.Combine(data, contentService.GetState().Offline ? "OfflineGame" : "Settings", "Backups"),
            "cache" => contentService.GetState().CacheDirectory,
            "removedMods" => Path.Combine(data, contentService.GetState().Offline ? "OfflineGame" : "Settings", "RemovedMods"),
            _ => throw new ArgumentException("请先选择游戏文件夹。")
        };
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("文件夹尚不存在。");
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }

    private async Task Smoke(string directory)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        for (var i = 0; i < 50; i++)
        {
            if (await browser.CoreWebView2.ExecuteScriptAsync("Boolean(window.appReady)") == "true") break;
            if (i == 49) throw new InvalidOperationException("界面没有完成初始化。");
            await Task.Delay(200);
        }
        foreach (var page in new[] { "home", "mods", "downloads", "offline", "saves", "logs", "settings", "appearance", "about" })
        {
            await browser.CoreWebView2.ExecuteScriptAsync($"window.app.showPage('{page}')");
            await Task.Delay(300);
            using var output = File.Create(Path.Combine(directory, page + ".png"));
            await browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, output);
        }
        await browser.CoreWebView2.ExecuteScriptAsync("resourceTab='nexus';window.app.showPage('downloads')");
        await Task.Delay(300);
        using (var output = File.Create(Path.Combine(directory, "nexus-links.png")))
            await browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, output);
        await browser.CoreWebView2.ExecuteScriptAsync("window.app.showPage('home');document.getElementById('maximizeButton').click()");
        await Task.Delay(400);
        if (WindowState != WindowState.Maximized || !WindowBounds.FitsWorkArea(this)) throw new InvalidOperationException("最大化检查未通过或窗口遮挡任务栏。");
        using (var output = File.Create(Path.Combine(directory, "home-maximized.png")))
            await browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, output);
        await browser.CoreWebView2.ExecuteScriptAsync("document.getElementById('maximizeButton').click()");
        await Task.Delay(300);
        if (WindowState != WindowState.Normal) throw new InvalidOperationException("窗口还原检查未通过。");
        await File.WriteAllTextAsync(Path.Combine(directory, "window-result.json"), "{\"maximize\":true,\"restore\":true,\"taskbarPreserved\":true}");
        await File.WriteAllTextAsync(Path.Combine(directory, "passed.txt"), "Nine StarSprout pages rendered successfully; no games, downloads or external API calls made.");
        Close();
    }

    private async Task HandleNxm(string uri)
    {
        await gate.WaitAsync();
        currentOperation = new CancellationTokenSource();
        try
        {
            contentService.SetInstallation(ActiveGame());
            var parsed = NexusService.ParseNxm(uri);
            var result = await nexus.HandleNxmAsync(uri, currentOperation.Token);
            if (pendingNexusInstalls.Remove(parsed.ModId, out var expectedGame) && !IsRunning() && ActiveGame().GetInstallation().HasGame &&
                ActiveGame().Settings.GameDirectory.Equals(expectedGame, StringComparison.OrdinalIgnoreCase))
                result = await contentService.InstallCached(result.ItemId, currentOperation.Token);
            SetDownloadNotice(result);
            if (!closing) await browser.CoreWebView2.ExecuteScriptAsync("refresh();toast(" + JsonConvert.SerializeObject(operationNotice) + ");");
        }
        catch (NexusBrowserDownloadRequiredException ex)
        {
            if (!closing) await OpenCommunityLink(ex.PageUrl.AbsoluteUri);
        }
        catch (Exception ex)
        {
            if (!closing) await browser.CoreWebView2.ExecuteScriptAsync("toast(" + JsonConvert.SerializeObject(ex.Message) + ",true);");
        }
        finally { currentOperation.Dispose(); currentOperation = null; gate.Release(); }
    }

    private Task OpenCommunityLink(string url, bool install = false)
    {
        if (contentService.GetState().Offline) throw new InvalidOperationException("离线模式已暂停社区网页访问。");
        var uri = CommunityBrowserWindow.ValidateNavigation(url);
        if (uri.Host.Equals("www.nexusmods.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("nexusmods.com", StringComparison.OrdinalIgnoreCase))
        {
            var match = System.Text.RegularExpressions.Regex.Match(uri.AbsolutePath, @"^/(?:games/)?stardewvalley/mods/(\d+)/?$");
            if (match.Success && long.TryParse(match.Groups[1].Value, out var modId))
            {
                if (install && !IsRunning() && ActiveGame().GetInstallation().HasGame) pendingNexusInstalls[modId] = ActiveGame().Settings.GameDirectory;
                else pendingNexusInstalls.Remove(modId);
            }
        }
        OpenUrl(uri.AbsoluteUri);
        return Task.CompletedTask;
    }

    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    private void RecordLaunchDay(string? accent)
    {
        launchWarning = "";
        try { home.RecordSuccessfulLaunch(accent ?? appearance.GetSettings().Accent); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        { launchWarning = "游戏已启动，但启动日历未保存：" + ex.Message; }
    }
    private void SetDownloadNotice(DownloadResult result)
    {
        installationResult = result.InstallationResult;
        operationNotice = result.Installed ? $"{result.Name}：处理完成。" : $"{result.Name}：已下载到缓存。";
        if (installationResult is { } report)
        {
            if (report.InstalledCount > 0) operationNotice += $"已安装 {report.InstalledCount} 个 Mod。";
            if (report.UpdatedCount > 0) operationNotice += $"已自动备份并更新 {report.UpdatedCount} 个。";
            if (report.PatchedCount > 0) operationNotice += $"已自动备份并应用 {report.PatchedCount} 个汉化补丁。";
            if (report.SkippedCount > 0) operationNotice += $"已安装的相同或较新版本保留 {report.SkippedCount} 个。";
            if (report.Warnings.Count > 0) operationNotice += " " + string.Join("；", report.Warnings.Take(3));
        }
    }
    private static string AppRoot()
    {
        var root = Path.GetFullPath(AppContext.BaseDirectory);
        var parent = Directory.GetParent(root.TrimEnd(Path.DirectorySeparatorChar));
        return Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)).Equals("app", StringComparison.OrdinalIgnoreCase) && parent is not null ? parent.FullName : root;
    }
    private static string AppData()
    {
        var args = Environment.GetCommandLineArgs(); var check = Array.IndexOf(args, "--appearance-test");
        return check >= 0 && check + 1 < args.Length ? Path.Combine(Path.GetFullPath(args[check + 1]), "isolated-data") : Path.Combine(AppRoot(), "StarSproutData");
    }
}
