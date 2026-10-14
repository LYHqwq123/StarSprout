using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace StarSprout;

internal static class WindowBounds
{
    internal static void Attach(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(Hook);
            var round = 2;
            DwmSetWindowAttribute(handle, 33, ref round, sizeof(int));
        };
    }
    private static IntPtr Hook(IntPtr window, int message, IntPtr wparam, IntPtr lparam, ref bool handled)
    {
        if (message != 0x0024) return IntPtr.Zero;
        var monitor = MonitorFromWindow(window, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;
        var bounds = Marshal.PtrToStructure<MinMaxInfo>(lparam);
        bounds.MaxPosition = new Point { X = info.Work.Left - info.Monitor.Left, Y = info.Work.Top - info.Monitor.Top };
        bounds.MaxSize = new Point { X = info.Work.Right - info.Work.Left, Y = info.Work.Bottom - info.Work.Top };
        if (HwndSource.FromHwnd(window)?.RootVisual is Window managed)
        {
            var scale = HwndSource.FromHwnd(window)?.CompositionTarget?.TransformToDevice ?? System.Windows.Media.Matrix.Identity;
            bounds.MinTrackSize = new Point { X = (int)Math.Ceiling(managed.MinWidth * scale.M11), Y = (int)Math.Ceiling(managed.MinHeight * scale.M22) };
        }
        Marshal.StructureToPtr(bounds, lparam, false); handled = true;
        return IntPtr.Zero;
    }
    internal static bool FitsWorkArea(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(handle, 2), ref info) || !GetWindowRect(handle, out var rect)) return false;
        return rect.Left >= info.Work.Left - 8 && rect.Top >= info.Work.Top - 8 && rect.Right <= info.Work.Right + 8 && rect.Bottom <= info.Work.Bottom + 8;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public int Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
