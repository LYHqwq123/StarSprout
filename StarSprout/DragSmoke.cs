using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Newtonsoft.Json;

namespace StarSprout;

internal static class DragSmoke
{
    internal static async Task Run(Window window, string output)
    {
        Directory.CreateDirectory(output);
        window.Left = 140;
        window.Top = 110;
        ShowWindow(new WindowInteropHelper(window).Handle, 1);
        window.Activate();
        SetForegroundWindow(new WindowInteropHelper(window).Handle);
        await Task.Delay(400);
        var point = window.PointToScreen(new Point(230, 28));
        double beforeX = window.Left, beforeY = window.Top;
        GetCursorPos(out var previous);
        try
        {
            await Task.Run(async () =>
            {
                SetCursorPos((int)point.X, (int)point.Y);
                mouse_event(2, 0, 0, 0, UIntPtr.Zero);
                await Task.Delay(200);
                SetCursorPos((int)point.X + 120, (int)point.Y + 90);
                await Task.Delay(250);
                mouse_event(4, 0, 0, 0, UIntPtr.Zero);
                await Task.Delay(200);
            });
            bool moved = Math.Abs(window.Left - beforeX) > 20 && Math.Abs(window.Top - beforeY) > 20;
            await File.WriteAllTextAsync(Path.Combine(output, "drag-result.json"), JsonConvert.SerializeObject(new { passed = moved, beforeX, beforeY, afterX = window.Left, afterY = window.Top }));
            if (!moved) throw new InvalidOperationException("标题栏实际拖动检查没有移动窗口。");
        }
        finally
        {
            mouse_event(4, 0, 0, 0, UIntPtr.Zero);
            SetCursorPos(previous.X, previous.Y);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll", EntryPoint = "SetPhysicalCursorPos")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
}
