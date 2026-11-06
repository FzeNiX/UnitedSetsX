using System.Runtime.InteropServices;

namespace WindowHoster;

static class WindowPlacementHelper
{
    /// <summary>
    /// Makes a hidden maximized (or minimized) window normal again without showing it.
    /// (<c>SC_RESTORE</c>/<c>ShowWindow</c> would show it.)
    /// </summary>
    public static void RestoreHidden(nint hwnd)
    {
        var placement = new WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(hwnd, ref placement)) return;
        placement.flags = 0;
        placement.showCmd = 0; // SW_HIDE
        SetWindowPlacement(hwnd, ref placement);
        // SW_HIDE keeps it maximized: clear that state (the window is put in place right after this)
        const int GWL_STYLE = -16;
        const long WS_MAXIMIZE = 0x01000000, WS_MINIMIZE = 0x20000000;
        var style = GetWindowLongPtr(hwnd, GWL_STYLE);
        if ((style & (WS_MAXIMIZE | WS_MINIMIZE)) != 0)
            SetWindowLongPtr(hwnd, GWL_STYLE, (nint)(style & ~(WS_MAXIMIZE | WS_MINIMIZE)));
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct WINDOWPLACEMENT
    {
        public uint length, flags, showCmd;
        public POINT ptMinPosition, ptMaxPosition;
        public RECT rcNormalPosition, rcDevice;
    }
    [DllImport("user32.dll")] static extern bool GetWindowPlacement(nint hwnd, ref WINDOWPLACEMENT placement);
    [DllImport("user32.dll")] static extern bool SetWindowPlacement(nint hwnd, ref WINDOWPLACEMENT placement);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern long GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
}
