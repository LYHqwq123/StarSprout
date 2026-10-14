using System.IO;
using System.Security.Cryptography;
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
        var testRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(testRoot);
        try
        {
            var now = new DateTimeOffset(2028, 2, 29, 0, 5, 0, TimeSpan.FromHours(14));
            string profile = Path.Combine(testRoot, "profile");
            var service = new HomeService(profile, () => now);
            var initial = service.GetState();
            Check(initial.Year == 2028 && initial.Month == 2 && initial.Today == "2028-02-29" && initial.Days.Count == 0,
                "calendar uses the launcher's local date, including leap day");
            Check(!initial.HasImage && initial.ImageUrl == "", "homepage initially has built-in artwork");
            var first = service.RecordSuccessfulLaunch("#AABBCC");
            Check(first.Days is [{ Date: "2028-02-29", Accent: "#aabbcc", Count: 1 }], "successful launch records accent and local date");
            var again = service.RecordSuccessfulLaunch("#123456");
            Check(again.Days is [{ Count: 2, Accent: "#123456" }], "same day updates its ring color and count without duplicates");
            first.Days[0].Count = 42;
            Check(service.GetState().Days[0].Count == 2, "returned calendar cannot mutate stored records");
            now = new DateTimeOffset(2028, 3, 1, 0, 1, 0, TimeSpan.FromHours(14));
            Check(service.GetState().Days.Count == 0, "next month omits prior month's dates");
            service.RecordSuccessfulLaunch("#008800");
            Check(service.GetState(2028, 2).Days[0].Date == "2028-02-29" && service.GetState(2028, 3).Days[0].Date == "2028-03-01",
                "month navigation selects only requested launch dates");
            Check(new HomeService(profile, () => now).GetState(2028, 2).Days[0].Count == 2, "play dates survive restart");
            byte[] stableHistory = File.ReadAllBytes(Path.Combine(profile, "play-calendar.json"));
            Reject(() => service.RecordSuccessfulLaunch("red;url(file:///private)"), "invalid CSS color rejected");
            Check(stableHistory.SequenceEqual(File.ReadAllBytes(Path.Combine(profile, "play-calendar.json"))), "invalid input leaves persisted history intact");
            Reject(() => service.GetState(2028, 13), "invalid month rejected");
            Reject(() => service.GetState(0, 1), "invalid year rejected");
            Check(!Directory.EnumerateFiles(profile, "*.tmp").Any(), "atomic writes leave no temporary files");

            string source = Path.Combine(testRoot, "chosen-home.jpg");
            WriteImage(source, new JpegBitmapEncoder(), 100, 50);
            byte[] originalHash = SHA256.HashData(File.ReadAllBytes(source));
            var globalAppearance = new AppearanceService(profile);
            var imported = service.ImportImage(source);
            Check(imported.HasImage && imported.ImageName == "chosen-home.jpg", "homepage image imports independently");
            Check(imported.ImageUrl.StartsWith("https://home.starsprout.local/background.png?v=", StringComparison.Ordinal)
                && !JsonConvert.SerializeObject(imported).Contains(testRoot, StringComparison.OrdinalIgnoreCase),
                "homepage exposes only a fixed virtual host URL");
            Check(!globalAppearance.GetSettings().HasBackground, "homepage import leaves launcher background unchanged");
            Check(originalHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source))), "selected image remains unchanged");
            using (var asset = File.OpenRead(Path.Combine(service.AssetsDirectory, "background.png")))
                Check(BitmapDecoder.Create(asset, BitmapCreateOptions.None, BitmapCacheOption.OnLoad) is PngBitmapDecoder,
                    "homepage stores a decoded PNG");
            Check(new HomeService(profile, () => now).GetState().ImageUrl == imported.ImageUrl, "homepage artwork persists after restart");
            string second = Path.Combine(testRoot, "second.png");
            WriteImage(second, new PngBitmapEncoder(), 96, 72);
            Check(service.ImportImage(second).ImageUrl != imported.ImageUrl, "replacement image invalidates cache");
            string damaged = Path.Combine(testRoot, "damaged.png");
            File.WriteAllText(damaged, "<script>not an image</script>");
            string beforeBad = service.GetState().ImageUrl;
            Reject(() => service.ImportImage(damaged), "damaged image rejected");
            Check(service.GetState().ImageUrl == beforeBad, "failed import preserves previous homepage image");
            service.ClearImage();
            Check(!service.GetState().HasImage && !File.Exists(Path.Combine(service.AssetsDirectory, "background.png")), "clear removes only owned homepage asset");
            Check(!new HomeService(profile, () => now).GetState().HasImage && File.Exists(source), "clear persists and preserves source image");
            Check(service.GetState(2028, 2).Days.Count == 1, "image changes preserve play history");

            string badProfile = Path.Combine(testRoot, "bad-profile");
            Directory.CreateDirectory(badProfile);
            File.WriteAllText(Path.Combine(badProfile, "play-calendar.json"), "{broken");
            Check(new HomeService(badProfile, () => now).GetState().Days.Count == 0, "corrupt calendar recovers without crashing");
            var oversized = Enumerable.Range(0, 3665).Select(index => new HomePlayDay
                { Date = new DateOnly(2000, 1, 1).AddDays(index).ToString("yyyy-MM-dd"), Accent = "#ABCDEF", Count = 1 }).ToList();
            oversized.Add(new() { Date = "2028-03-01", Accent = "url(evil)", Count = 1 });
            oversized.Add(new() { Date = "2028-03-99", Accent = "#112233", Count = 1 });
            oversized.Add(new() { Date = "2028-03-01", Accent = "#112233", Count = -1 });
            File.WriteAllText(Path.Combine(badProfile, "play-calendar.json"), JsonConvert.SerializeObject(new { days = oversized }));
            var bounded = new HomeService(badProfile, () => now);
            Check(bounded.GetState(2000, 1).Days.Count == 26, "history retains at most 3660 dates and drops oldest dates");
            Check(bounded.GetState().Days.Count == 0, "invalid saved dates, colors and counts are ignored");
            bounded.RecordSuccessfulLaunch("#338855");
            var saved = JsonConvert.DeserializeObject<Dictionary<string, List<HomePlayDay>>>(File.ReadAllText(Path.Combine(badProfile, "play-calendar.json")))!;
            Check(saved["days"].Count == 3660 && saved["days"].Last().Date == "2028-03-01", "new launch enforces the retention bound when persisting");
            Console.WriteLine($"PASS {count} home checks");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void WriteImage(string path, BitmapEncoder encoder, int width, int height)
    {
        int stride = width * 3;
        byte[] pixels = new byte[stride * height];
        for (int index = 0; index < pixels.Length; index += 3)
        { pixels[index] = (byte)(index % 255); pixels[index + 1] = 130; pixels[index + 2] = 60; }
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Rgb24, null, pixels, stride)));
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
        catch (Exception ex) when (ex is ArgumentException or IOException) { count++; return; }
        throw new InvalidOperationException("Expected rejection: " + name);
    }
}
