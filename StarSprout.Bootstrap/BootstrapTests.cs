using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace StarSprout.Bootstrap
{
    internal static class BootstrapTests
    {
        private static int checks;
        public static int Run()
        {
            Check(RuntimeDetector.IsDesktop10("10.0.0"), "Accept stable Desktop10 baseline");
            Check(RuntimeDetector.IsDesktop10("10.0.11"), "Accept patched Desktop10");
            Check(!RuntimeDetector.IsDesktop10("10.0.99-preview.1"), "Reject prerelease runtime");
            Check(!RuntimeDetector.IsDesktop10("9.0.12"), "Reject older runtime");
            Check(!RuntimeDetector.IsDesktop10("11.0.0"), "Do not mistake newer major for Desktop10");
            Check(!RuntimeDetector.IsDesktop10("garbage"), "Reject malformed runtime version");
            Check(!RuntimeDetector.IsWebViewVersion("0.0.0.0"), "Reject empty WebView registration");
            Check(!RuntimeDetector.IsWebViewVersion(null), "Reject absent WebView registration");
            Check(RuntimeDetector.IsWebViewVersion("141.0.3537.71"), "Accept installed WebView");
            string validUrl = "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.11/windowsdesktop-runtime-10.0.11-win-x64.exe";
            var serializer = new JavaScriptSerializer();
            var releases = new[] {
                Entry("10.0.0", "win-x64", "windowsdesktop-runtime-win-x64.exe", validUrl),
                Entry("10.0.11", "win-x64", "windowsdesktop-runtime-win-x64.exe", validUrl),
                Entry("10.0.99-preview.1", "win-x64", "windowsdesktop-runtime-win-x64.exe", validUrl),
                Entry("10.0.100", "win-arm64", "windowsdesktop-runtime-win-arm64.exe", validUrl),
                Entry("10.0.101", "win-x64", "windowsdesktop-runtime-win-x64.zip", validUrl),
                Entry("11.0.0", "win-x64", "windowsdesktop-runtime-win-x64.exe", validUrl)
            };
            string fixture = serializer.Serialize(new { releases = releases });
            var selected = BootstrapCore.SelectDesktopInstaller(fixture);
            Check(selected.Version == "10.0.11", "Choose latest stable x64 EXE, ignoring preview/ARM64/ZIP/other major");
            ExpectFailure(() => BootstrapCore.SelectDesktopInstaller("{\"releases\":[]}"), "No matching installer fails closed");
            ExpectFailure(() => BootstrapCore.ValidateMicrosoftUrl("http://builds.dotnet.microsoft.com/setup.exe"), "Reject HTTP downgrade");
            ExpectFailure(() => BootstrapCore.ValidateMicrosoftUrl("https://microsoft.com.attacker.example/setup.exe"), "Reject deceptive Microsoft hostname");
            ExpectFailure(() => BootstrapCore.ValidateMicrosoftUrl("https://attacker.blob.core.windows.net/setup.exe"), "Reject unrelated Azure storage");
            string temporary = Path.GetTempFileName();
            try
            {
                File.WriteAllText(temporary, "runtime installer fixture", Encoding.UTF8);
                string hash;
                using (var stream = File.OpenRead(temporary))
                using (var sha = SHA512.Create()) hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
                SignatureVerifier.VerifySha512(temporary, hash);
                checks++;
                File.AppendAllText(temporary, "tamper");
                ExpectFailure(() => SignatureVerifier.VerifySha512(temporary, hash), "Reject changed download");
                ExpectFailure(() => SignatureVerifier.VerifySha512(temporary, "invalid"), "Reject malformed metadata hash");
                ExpectFailure(() => SignatureVerifier.VerifyMicrosoft(temporary), "Reject unsigned installer before execution");
            }
            finally { File.Delete(temporary); }
            var core = new BootstrapCore(AppDomain.CurrentDomain.BaseDirectory);
            string plan = serializer.Serialize(core.Plan(new RuntimeStatus()));
            Check(plan.Contains("\"installsExecuted\":0") && plan.Contains("Windows UAC") && plan.Contains("2124703"), "Missing-runtime dry plan never executes installation");
            string readyPlan = serializer.Serialize(core.Plan(new RuntimeStatus { DotNetDesktopVersion = "10.0.11", WebView2Version = "141.0.0.0" }));
            Check(readyPlan.Contains("\"installSteps\":[]"), "Installed runtimes do not trigger installation");
            Console.WriteLine("PASS " + checks + " bootstrap checks; no registry writes or installers executed.");
            return 0;
        }

        private static object Entry(string version, string rid, string name, string url)
        {
            return new { windowsdesktop = new { version = version, files = new[] { new { rid = rid, name = name, url = url, hash = new string('a', 128) } } } };
        }
        private static void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + name);
            checks++;
        }
        private static void ExpectFailure(Action action, string name)
        {
            try { action(); } catch (InvalidDataException) { checks++; return; }
            throw new InvalidOperationException("FAIL: " + name);
        }
    }
}
