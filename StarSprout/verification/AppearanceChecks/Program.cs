using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Newtonsoft.Json;
using StarSprout.Services;

internal static class Program
{
    private static int count;
    [STAThread]
    private static int Main()
    {
        // Fixed output stays under this check project's workspace; only test-created files are changed.
        string testRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(testRoot);
        try
        {
            string data = Path.Combine(testRoot, "profile");
            var service = new AppearanceService(data);
            Check(service.GetSettings().Theme == "light" && !service.GetSettings().HasBackground, "default appearance");
            var input = new AppearanceSettingsInput
            {
                Theme = "oled", Accent = "#12ABCD", Blur = 20, Opacity = 80,
                PanelOpacity = 70, Fit = "contain", Overlay = false
            };
            service.SaveSettings(input);
            var restored = new AppearanceService(data).GetSettings();
            Check(restored.Theme == "oled" && restored.Accent == "#12abcd" && restored.Fit == "contain"
                && restored.Blur == 20 && restored.Opacity == 80 && restored.PanelOpacity == 70 && !restored.Overlay,
                "all preferences survive restart");
            Reject(() => service.SaveSettings(new() { Theme = "unknown" }), "invalid theme rejected");
            Reject(() => service.SaveSettings(new() { Accent = "url(file:///secrets)" }), "CSS injection rejected");
            Reject(() => service.SaveSettings(new() { Blur = 41 }), "blur upper limit");
            Reject(() => service.SaveSettings(new() { Opacity = 9 }), "visibility lower limit");
            Reject(() => service.SaveSettings(new() { PanelOpacity = 49 }), "panel lower limit");
            Reject(() => service.SaveSettings(new() { Fit = "evil" }), "invalid fit");
            Check(service.GetSettings().Theme == "oled", "invalid settings leave existing preferences intact");

            string png = Path.Combine(testRoot, "spring.png");
            WriteImage(png, 128, 64, new PngBitmapEncoder());
            var imported = service.ImportBackground(png);
            Check(imported.HasBackground && imported.BackgroundName == "spring.png", "background imported");
            Check(imported.BackgroundUrl.StartsWith("https://appearance.starsprout.local/background.png?v=")
                && !JsonConvert.SerializeObject(imported).Contains(testRoot, StringComparison.OrdinalIgnoreCase),
                "virtual URL never exposes the selected local path");
            Check(new AppearanceService(data).GetSettings().BackgroundUrl == imported.BackgroundUrl, "image and revision survive restart");
            using (var stored = File.OpenRead(Path.Combine(service.AssetsDirectory, "background.png")))
                Check(BitmapDecoder.Create(stored, BitmapCreateOptions.None, BitmapCacheOption.OnLoad) is PngBitmapDecoder,
                    "stored asset is decoded PNG");

            string jpeg = Path.Combine(testRoot, "autumn.jpg");
            WriteImage(jpeg, 64, 64, new JpegBitmapEncoder());
            var next = service.ImportBackground(jpeg);
            Check(next.BackgroundUrl != imported.BackgroundUrl && next.BackgroundName == "autumn.jpg", "replacement invalidates image cache");

            string wide = Path.Combine(testRoot, "wide.png");
            WriteImage(wide, 4096, 24, new PngBitmapEncoder());
            service.ImportBackground(wide);
            using (var stored = File.OpenRead(Path.Combine(service.AssetsDirectory, "background.png")))
            {
                var frame = BitmapDecoder.Create(stored, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
                Check(frame.PixelWidth <= 3840 && frame.PixelHeight > 0, "large image scaled while keeping valid dimensions");
            }
            string stableUrl = service.GetSettings().BackgroundUrl;
            string bad = Path.Combine(testRoot, "broken.png");
            File.WriteAllText(bad, "this is not a png");
            Reject(() => service.ImportBackground(bad), "damaged source rejected");
            Check(service.GetSettings().BackgroundUrl == stableUrl, "damaged import preserves previous background");
            string huge = Path.Combine(testRoot, "oversized.png");
            using (var stream = File.Create(huge)) stream.SetLength(20L * 1024 * 1024 + 1);
            Reject(() => service.ImportBackground(huge), "oversized file rejected before decoding");
            string tooWide = Path.Combine(testRoot, "too-wide.png");
            WriteImage(tooWide, 12001, 1, new PngBitmapEncoder());
            Reject(() => service.ImportBackground(tooWide), "dimension limit enforced before pixel decoding");
            string disguised = Path.Combine(testRoot, "document.svg");
            File.Copy(png, disguised);
            Reject(() => service.ImportBackground(disguised), "unsupported extension rejected");

            File.WriteAllText(Path.Combine(service.AssetsDirectory, "background.png"), "damaged saved background");
            Check(!new AppearanceService(data).GetSettings().HasBackground, "corrupt stored image recovers without crashing");
            File.WriteAllText(Path.Combine(data, "appearance-settings.json"), "{broken");
            Check(new AppearanceService(data).GetSettings().Theme == "light", "corrupt settings restore defaults");
            service = new AppearanceService(data);
            service.ImportBackground(png);
            service.ClearBackground();
            Check(!service.GetSettings().HasBackground && !File.Exists(Path.Combine(service.AssetsDirectory, "background.png")), "clear removes owned image");
            Check(!new AppearanceService(data).GetSettings().HasBackground, "clear persists after restart");
            Console.WriteLine($"PASS {count} appearance checks");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void WriteImage(string path, int width, int height, BitmapEncoder encoder)
    {
        int stride = checked(width * 3);
        byte[] pixels = new byte[checked(stride * height)];
        for (int i = 0; i < pixels.Length; i += 3)
        {
            pixels[i] = (byte)((i / 3) % 253); pixels[i + 1] = 180; pixels[i + 2] = 60;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Rgb24, null, pixels, stride);
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("Check failed: " + name);
        count++;
    }
    private static void Reject(Action action, string name)
    {
        try { action(); }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            count++;
            return;
        }
        throw new InvalidOperationException("Expected rejection: " + name);
    }
}
