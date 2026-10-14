using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StarSprout.Services;

namespace StarSprout;

internal static class AppearanceSmoke
{
    internal static async Task Run(Window window, WebView2 browser, AppearanceService appearance, string output)
    {
        window.Dispatcher.VerifyAccess();
        if (browser.CoreWebView2 is null || !browser.CoreWebView2.Source.StartsWith("https://starsprout.local/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("外观检查需要已初始化的启动器页面。");
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        var original = appearance.GetSettings();
        var settingsPath = Path.Combine(Directory.GetParent(appearance.AssetsDirectory)!.FullName, "appearance-settings.json");
        var backgroundPath = Path.Combine(appearance.AssetsDirectory, "background.png");
        var settingsBytes = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : null;
        var backgroundBytes = File.Exists(backgroundPath) ? File.ReadAllBytes(backgroundPath) : null;
        var fixtureDirectory = Path.Combine(output, ".appearance-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureDirectory);
        var themes = new Dictionary<string, JObject>(StringComparer.Ordinal);
        JObject? imageResult = null;
        var checks = 0;
        var restored = false;
        Exception? failure = null;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("外观检查失败：" + message);
            checks++;
        }
        try
        {
            appearance.ClearBackground();
            foreach (var theme in new[] { "light", "dark", "oled", "system" })
            {
                var input = Input(original); input.Theme = theme;
                appearance.SaveSettings(input);
                await Refresh(browser);
                var rendered = await State(browser);
                themes.Add(theme, rendered);
                Check(rendered.Value<string>("savedTheme") == theme, theme + " 配置未通过页面快照更新。");
                var expected = theme == "system" ? (rendered.Value<bool>("systemDark") ? "dark" : "light") : theme;
                Check(rendered.Value<string>("theme") == expected, theme + " 未应用到 document 主题。");
                Check(rendered.Value<bool>("appearancePage"), "外观页面未显示。");
                await Capture(browser, Path.Combine(output, "theme-" + theme + ".png"));
            }
            Check(new[] { "light", "dark", "oled" }.Select(theme => themes[theme].Value<string>("backgroundColor")).Distinct(StringComparer.Ordinal).Count() == 3,
                "浅色、深色、OLED 的实际背景颜色相同。");
            var systemTheme = themes["system"].Value<string>("theme")!;
            Check(themes["system"].Value<string>("backgroundColor") == themes[systemTheme].Value<string>("backgroundColor"),
                "跟随系统主题的实际颜色没有匹配解析主题。");

            var fixture = Path.Combine(fixtureDirectory, "four-pastel-pixels.png");
            CreateImage(fixture);
            var custom = Input(original); custom.Theme = "light"; custom.Blur = 0; custom.Opacity = 100;
            appearance.SaveSettings(custom);
            appearance.ImportBackground(fixture);
            await Refresh(browser);
            var background = await State(browser);
            Check(background.Value<string>("backgroundUrl")?.StartsWith("https://appearance.starsprout.local/background.png?", StringComparison.Ordinal) == true,
                "背景导入后没有生成本地资源 URL。");
            Check(background.Value<string>("backgroundImage")?.Contains("appearance.starsprout.local/background.png", StringComparison.Ordinal) == true,
                "背景 URL 未应用到 CSS。");
            await browser.CoreWebView2.ExecuteScriptAsync("""
                window.__appearanceSmokeImage={done:false};
                (()=>{const image=new Image();image.src=state.appearance.backgroundUrl;
                image.decode().then(()=>{window.__appearanceSmokeImage={done:true,width:image.naturalWidth,height:image.naturalHeight};})
                .catch(error=>{window.__appearanceSmokeImage={done:true,error:String(error.message||error)};});})();
                """);
            imageResult = await Wait(browser, "window.__appearanceSmokeImage");
            Check(imageResult.Value<string>("error") is null && imageResult.Value<int>("width") == 2 && imageResult.Value<int>("height") == 2,
                "WebView 无法解码导入的 2×2 PNG。");
            await Capture(browser, Path.Combine(output, "custom-background.png"));
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            try
            {
                appearance.ClearBackground();
                if (original.HasBackground && backgroundBytes is not null)
                {
                    var restoreDirectory = Path.Combine(fixtureDirectory, "restore");
                    Directory.CreateDirectory(restoreDirectory);
                    var originalFile = Path.Combine(restoreDirectory, original.BackgroundName);
                    File.WriteAllBytes(originalFile, backgroundBytes);
                    appearance.ImportBackground(originalFile);
                }
                appearance.SaveSettings(Input(original));
                RestoreFile(backgroundPath, backgroundBytes);
                RestoreFile(settingsPath, settingsBytes);
                var current = appearance.GetSettings();
                restored = JsonConvert.SerializeObject(Input(current)) == JsonConvert.SerializeObject(Input(original))
                    && current.HasBackground == original.HasBackground && current.BackgroundName == original.BackgroundName
                    && SameFile(backgroundPath, backgroundBytes) && SameFile(settingsPath, settingsBytes);
                if (!restored) throw new InvalidOperationException("外观检查未能恢复原设置或背景文件。");
                await Refresh(browser);
            }
            catch (Exception ex) { failure = failure is null ? ex : new AggregateException(failure, ex); }
            finally
            {
                // Remove only this call's fresh fixture folder after verifying its absolute boundary.
                var resolved = Path.GetFullPath(fixtureDirectory);
                if (!resolved.StartsWith(output.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("外观测试文件清理路径越界。");
                try { Directory.Delete(resolved, true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { failure = failure is null ? ex : new AggregateException(failure, ex); }
            }
        }
        await File.WriteAllTextAsync(Path.Combine(output, "appearance-result.json"), JsonConvert.SerializeObject(new
        {
            passed = failure is null && restored, checks, restored, themes, image = imageResult,
            error = failure?.ToString()
        }, Formatting.Indented));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static AppearanceSettingsInput Input(AppearanceSettingsView value) => new()
    {
        Theme = value.Theme, Accent = value.Accent, Blur = value.Blur, Opacity = value.Opacity,
        Fit = value.Fit, Overlay = value.Overlay, PanelOpacity = value.PanelOpacity
    };

    private static async Task Refresh(WebView2 browser)
    {
        await browser.CoreWebView2.ExecuteScriptAsync("""
            window.__appearanceSmokeRefresh={done:false};
            rpc('snapshot').then(snapshot=>{appearanceDraft=null;accept(snapshot);window.app.showPage('appearance');
            window.__appearanceSmokeRefresh={done:true};}).catch(error=>{window.__appearanceSmokeRefresh={done:true,error:String(error.message||error)};});
            """);
        var result = await Wait(browser, "window.__appearanceSmokeRefresh");
        if (result.Value<string>("error") is { } error) throw new InvalidOperationException("外观快照刷新失败：" + error);
        await Task.Delay(350);
    }

    private static async Task<JObject> State(WebView2 browser) => JObject.Parse(await browser.CoreWebView2.ExecuteScriptAsync("""
        ({theme:document.documentElement.dataset.theme,savedTheme:state.appearance.theme,
        systemDark:window.matchMedia('(prefers-color-scheme: dark)').matches,
        backgroundColor:getComputedStyle(document.body).backgroundColor,
        backgroundImage:getComputedStyle(document.getElementById('backgroundLayer')).backgroundImage,
        backgroundUrl:state.appearance.backgroundUrl,
        appearancePage:document.querySelector('[data-page="appearance"]')?.classList.contains('active')===true})
        """));

    private static async Task<JObject> Wait(WebView2 browser, string expression)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var json = await browser.CoreWebView2.ExecuteScriptAsync(expression);
            if (json != "null")
            {
                var result = JObject.Parse(json);
                if (result.Value<bool>("done")) return result;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException("外观页面检查在 10 秒内没有完成。");
    }

    private static async Task Capture(WebView2 browser, string path)
    {
        using var output = File.Create(path);
        await browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, output);
    }

    private static void CreateImage(string path)
    {
        byte[] pixels = [225, 231, 245, 255, 247, 237, 221, 255, 219, 239, 249, 255, 226, 244, 230, 255];
        var image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    private static void RestoreFile(string path, byte[]? bytes)
    {
        if (bytes is not null) File.WriteAllBytes(path, bytes);
        else if (File.Exists(path)) File.Delete(path);
    }

    private static bool SameFile(string path, byte[]? bytes) => bytes is null ? !File.Exists(path)
        : File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes);
}
