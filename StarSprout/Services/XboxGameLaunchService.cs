using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace StarSprout.Services;

/// <summary>Resolves a registered Xbox game activation only when its executable belongs to the selected installation.</summary>
public sealed class XboxGameLaunchService
{
    private const int MaxDiscoveryChars = 1024 * 1024;
    private const string PackageName = "ConcernedApe.StardewValleyPC";
    private static readonly Regex Family = new("^ConcernedApe\\.StardewValleyPC_[a-z0-9]{13}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex Application = new("^[a-zA-Z0-9][a-zA-Z0-9._-]{0,127}$", RegexOptions.CultureInvariant);
    private readonly Func<CancellationToken, Task<string>> discover;

    public XboxGameLaunchService(Func<CancellationToken, Task<string>>? discover = null)
    {
        this.discover = discover ?? DiscoverRegisteredPackages;
    }

    /// <summary>Returns null when the package cannot be verified; the caller may open the official Xbox client instead.</summary>
    public async Task<ProcessStartInfo?> GetGameStartInfoAsync(string gameDirectory, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var selected = SafeDirectory(gameDirectory);
        if (selected == null || !new[] { "Stardew Valley.exe", "StardewValley.exe" }.Any(name => File.Exists(Path.Combine(selected, name)))) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            string json = await discover(timeout.Token).WaitAsync(timeout.Token);
            if (json.Length > MaxDiscoveryChars) return null;
            using var reader = new JsonTextReader(new StringReader(json)) { MaxDepth = 8 };
            var records = JToken.ReadFrom(reader) as JArray;
            if (records == null || records.Count > 100) return null;
            foreach (var record in records.OfType<JObject>())
            {
                string name = record.Value<string>("Name") ?? "";
                string family = record.Value<string>("PackageFamilyName") ?? "";
                string application = record.Value<string>("ApplicationId") ?? "";
                string relativeExecutable = record.Value<string>("Executable") ?? "";
                if (!name.Equals(PackageName, StringComparison.OrdinalIgnoreCase) || !Family.IsMatch(family) || !Application.IsMatch(application)
                    || string.IsNullOrWhiteSpace(relativeExecutable) || relativeExecutable.Length > 4096 || relativeExecutable.Any(char.IsControl)
                    || relativeExecutable.Contains(':') || Path.IsPathRooted(relativeExecutable)) continue;
                string? installed = SafeDirectory(record.Value<string>("InstallLocation") ?? "");
                if (installed == null) continue;
                string executable = Path.GetFullPath(Path.Combine(installed, relativeExecutable));
                if (!executable.StartsWith(installed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || !new[] { "Stardew Valley.exe", "StardewValley.exe", "StardewModdingAPI.exe" }.Contains(Path.GetFileName(executable), StringComparer.OrdinalIgnoreCase)
                    || !string.Equals(Path.GetDirectoryName(executable), selected, StringComparison.OrdinalIgnoreCase) || !File.Exists(executable)) continue;
                if (!NoLinks(executable)) continue;
                // No user text is passed to a shell. Explorer receives one validated AppsFolder identifier.
                var plan = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
                {
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
                };
                plan.ArgumentList.Add("shell:AppsFolder\\" + family + "!" + application);
                return plan;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException
            or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        return null;
    }

    private static async Task<string> DiscoverRegisteredPackages(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return "[]";
        // Fixed read-only script. It neither accepts paths/user text nor activates, installs, or changes a package.
        const string script = """
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
            $records = @(Get-AppxPackage -Name 'ConcernedApe.StardewValleyPC' -ErrorAction Stop | ForEach-Object {
                $package = $_
                $manifest = Get-AppxPackageManifest -Package $package.PackageFullName -ErrorAction Stop
                foreach ($application in $manifest.Package.Applications.Application) {
                    [pscustomobject]@{
                        Name = [string]$package.Name
                        PackageFamilyName = [string]$package.PackageFamilyName
                        InstallLocation = [string]$package.InstallLocation
                        ApplicationId = [string]$application.Id
                        Executable = [string]$application.Executable
                    }
                }
            })
            ConvertTo-Json -InputObject $records -Compress -Depth 4
            """;
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("无法查询 Xbox 游戏注册信息。");
        try
        {
            var output = ReadBounded(process.StandardOutput, MaxDiscoveryChars, ct);
            var errors = ReadBounded(process.StandardError, 64 * 1024, ct);
            await Task.WhenAll(output, errors, process.WaitForExitAsync(ct));
            return process.ExitCode == 0 ? await output : "[]";
        }
        finally
        {
            // This is only the hidden read-only helper process started above, never a client or game.
            if (!process.HasExited)
            {
                try { process.Kill(); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
    }

    private static async Task<string> ReadBounded(StreamReader reader, int maximum, CancellationToken ct)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            int count = await reader.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maximum - result.Length + 1)), ct);
            if (count == 0) return result.ToString();
            if (result.Length + count > maximum) throw new InvalidDataException("Xbox 注册信息超过读取上限。");
            result.Append(buffer, 0, count);
        }
    }

    private static string? SafeDirectory(string value)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Any(char.IsControl) || value.StartsWith("\\\\", StringComparison.Ordinal)
                || !Path.IsPathFullyQualified(value)) return null;
            string full = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Directory.Exists(full) && NoLinks(full) ? full : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    private static bool NoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false; }
            catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
        }
        return true;
    }
}
