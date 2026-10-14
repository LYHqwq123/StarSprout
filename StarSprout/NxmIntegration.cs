using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace StarSprout;

internal static class NxmIntegration
{
    private static readonly string User = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
    internal static string MutexName => "Local\\StarSprout." + User;
    private static string PipeName => "StarSprout.Nxm." + User;

    public static bool IsRegistered(string executable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\nxm\shell\open\command");
            return string.Equals(key?.GetValue("") as string, "\"" + Path.GetFullPath(executable) + "\" \"%1\"", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return false; }
    }

    public static void Register(string executable)
    {
        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable)) throw new FileNotFoundException("请使用完整运行包后注册N网下载入口。", executable);
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\nxm");
        key.SetValue("", "URL:StarSprout Nexus download");
        key.SetValue("URL Protocol", "");
        using (var icon = key.CreateSubKey("DefaultIcon")) icon.SetValue("", "\"" + executable + "\",0");
        using var command = key.CreateSubKey(@"shell\open\command");
        command.SetValue("", "\"" + executable + "\" \"%1\"");
    }

    public static void Listen(Func<string, Task> receive)
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try
                {
                    await pipe.WaitForConnectionAsync();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    using var reader = new StreamReader(pipe, Encoding.UTF8);
                    var buffer = new char[4097];
                    int length = 0, chunk;
                    while (length < buffer.Length && (chunk = await reader.ReadAsync(buffer.AsMemory(length), timeout.Token)) > 0) length += chunk;
                    if (length > 4096) continue;
                    var uri = new string(buffer, 0, length).TrimEnd('\r', '\n');
                    Services.NexusService.ParseNxm(uri);
                    await receive(uri);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ArgumentException) { }
            }
        });
    }

    public static async Task<bool> SendAsync(string value)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(5000);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
            await writer.WriteAsync(value);
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException) { return false; }
    }
}
