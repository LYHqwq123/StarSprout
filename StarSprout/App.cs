using System.IO;
using System.Windows;

namespace StarSprout;

public sealed class App : Application
{
    [STAThread]
    public static void Main()
    {
        var args = Environment.GetCommandLineArgs();
        var nxm = args.Skip(1).FirstOrDefault(a => a.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase));
        using var singleton = new Mutex(true, NxmIntegration.MutexName, out var first);
        if (!first && !args.Contains("--smoke") && !args.Contains("--drag-test") && !args.Contains("--browser-test") && !args.Contains("--appearance-test") && !args.Contains("--guide-test"))
        {
            if (nxm is not null) NxmIntegration.SendAsync(nxm).GetAwaiter().GetResult();
            return;
        }
        var app = new App { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.DispatcherUnhandledException += (_, e) =>
        {
            ReportError(e.Exception);
            e.Handled = true;
            app.Shutdown(1);
        };
        try { app.Run(new LauncherWindow(nxm)); }
        catch (Exception ex) { ReportError(ex); Environment.ExitCode = 1; }
    }

    private static void ReportError(Exception exception)
    {
        var args = Environment.GetCommandLineArgs();
        var check = Array.FindIndex(args, a => a is "--smoke" or "--drag-test" or "--browser-test" or "--appearance-test" or "--guide-test");
        if (check >= 0 && check + 1 < args.Length)
        {
            Directory.CreateDirectory(args[check + 1]);
            File.WriteAllText(Path.Combine(args[check + 1], "failed.txt"), exception.ToString());
        }
        else MessageBox.Show(exception.Message, "星芽启动器", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
