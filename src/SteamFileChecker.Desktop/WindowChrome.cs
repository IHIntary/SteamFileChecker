using System.Runtime.InteropServices;

namespace SteamFileChecker.Desktop;

/// <summary>
/// Removes the native title bar while keeping the standard sizable frame, so Windows still provides resizing on the
/// left/right/bottom edges, Aero Snap, the drop shadow and Windows 11 rounded corners. The page draws its own title area
/// and asks the host to start a move or top-edge resize through <see cref="BeginNonClientDrag"/>.
/// </summary>
internal static class WindowChrome
{
    public const int WM_NCCALCSIZE = 0x0083;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    public const int HTCAPTION = 2, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14;

    private const int SM_CYFRAME = 33, SM_CXPADDEDBORDER = 92;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_BORDER_COLOR = 34;
    private const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_FRAMECHANGED = 0x20;

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NCCALCSIZE_PARAMS { public RECT Rgrc0, Rgrc1, Rgrc2; public IntPtr Lppos; }

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref uint value, int size);

    /// <summary>Dark frame, no 1px border line (Windows 11; ignored on older systems), and recompute the frame now.</summary>
    public static void Apply(IntPtr hwnd)
    {
        uint dark = 1, none = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(uint));
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref none, sizeof(uint));
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
    }

    /// <summary>
    /// Handles WM_NCCALCSIZE after the default processing: the client area is extended back up to the window's top edge,
    /// removing the caption. When maximized the window extends past the screen by the frame thickness, so that much is kept.
    /// </summary>
    public static void AdjustClientArea(ref Message m, uint originalTop)
    {
        var p = Marshal.PtrToStructure<NCCALCSIZE_PARAMS>(m.LParam);
        var top = (int)originalTop;
        if (IsZoomed(m.HWnd))
        {
            var dpi = GetDpiForWindow(m.HWnd);
            top += GetSystemMetricsForDpi(SM_CYFRAME, dpi) + GetSystemMetricsForDpi(SM_CXPADDEDBORDER, dpi);
        }
        p.Rgrc0.Top = top;
        Marshal.StructureToPtr(p, m.LParam, false);
        m.Result = IntPtr.Zero;
    }

    public static uint ProposedTop(IntPtr lParam) => (uint)Marshal.PtrToStructure<NCCALCSIZE_PARAMS>(lParam).Rgrc0.Top;

    /// <summary>Starts the system move or resize loop as if the user pressed on that part of a native frame.</summary>
    public static void BeginNonClientDrag(IntPtr hwnd, int hitTest)
    {
        var cursor = Cursor.Position;
        ReleaseCapture();
        SendMessage(hwnd, WM_NCLBUTTONDOWN, hitTest, (IntPtr)((cursor.Y << 16) | (cursor.X & 0xFFFF)));
    }
}
