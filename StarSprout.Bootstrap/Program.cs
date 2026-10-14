using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;

namespace StarSprout.Bootstrap
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                if (args.Contains("--self-test")) return BootstrapTests.Run();
                string report = Argument(args, "--report=");
                var core = new BootstrapCore(AppDomain.CurrentDomain.BaseDirectory);
                if (args.Contains("--check-only") || args.Contains("--dry-run-plan"))
                {
                    RuntimeStatus status = args.Contains("--simulate-missing") ? new RuntimeStatus() : RuntimeDetector.Detect();
                    object result = args.Contains("--dry-run-plan") ? (object)core.Plan(status) : status;
                    string json = new JavaScriptSerializer().Serialize(result);
                    Console.WriteLine(json);
                    if (!string.IsNullOrEmpty(report)) File.WriteAllText(report, json, new UTF8Encoding(false));
                    return args.Contains("--dry-run-plan") || status.Ready ? 0 : 2;
                }
                if (args.Contains("--download-offline"))
                {
                    string directory = Argument(args, "--runtimes-dir=") ?? Path.Combine(core.Root, "runtimes");
                    core.PrepareOfflineAsync(directory, (message, percent) => Console.WriteLine(message), CancellationToken.None).GetAwaiter().GetResult();
                    if (!string.IsNullOrEmpty(report)) File.WriteAllText(report, "{\"downloaded\":true}", new UTF8Encoding(false));
                    return 0;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new BootstrapWindow(core, args));
                return Environment.ExitCode;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                if (!args.Any(x => x.StartsWith("--", StringComparison.Ordinal))) MessageBox.Show(ex.Message, "星芽启动器", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        private static string Argument(string[] args, string prefix)
        {
            return args.Where(x => x.StartsWith(prefix, StringComparison.Ordinal)).Select(x => x.Substring(prefix.Length)).FirstOrDefault();
        }
    }
}
