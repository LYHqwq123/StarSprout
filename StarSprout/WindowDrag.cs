using System.Runtime.InteropServices;

namespace StarSprout;

internal static class WindowDrag
{
    // WebView2 owns a native child window, so WPF's Mouse.LeftButton is unreliable here.
    internal static bool Begin(IntPtr window)
    {
        if ((GetAsyncKeyState(1) & 0x8000) == 0) return false;
        ReleaseCapture();
        SendMessage(window, 0x0112, (IntPtr)0xF012, IntPtr.Zero);
        return true;
    }

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReleaseCapture();
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
