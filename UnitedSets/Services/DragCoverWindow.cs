using System.Runtime.InteropServices;
using UnitedSets.Helpers;
using Rectangle = System.Drawing.Rectangle;

namespace UnitedSets.Services;

/// <summary>
/// A click-through window that shows a picture, above the tabbed windows of a set.
/// </summary>
/// <remarks>
/// Used when a set window was dragged: its tabbed windows were hidden during the drag and must be shown again.
/// Many applications (e.g. Chrome) show one blank (white) frame when their window is shown, so the picture of the
/// windows covers them until they have painted. Layered window: its content is there as soon as it is shown.
/// </remarks>
sealed class DragCoverWindow : IDisposable
{
    nint hwnd;
    static WndProcDelegate? wndProc; // kept alive for the window class
    static bool registered;
    const string ClassName = "UnitedSetsDragCover";

    /// <summary>
    /// Shows <paramref name="image"/> stretched over <paramref name="rect"/> (screen pixels), above the windows owned by <paramref name="owner"/>.
    /// </summary>
    public static DragCoverWindow? Show(nint owner, Rectangle rect, BgraImage image)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return null;
        try
        {
            EnsureClass();
            var hwnd = CreateWindowEx(
                WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                ClassName, "United Sets drag cover", WS_POPUP,
                rect.X, rect.Y, rect.Width, rect.Height, owner, 0, GetModuleHandle(null), 0);
            if (hwnd == 0) return null;
            var cover = new DragCoverWindow { hwnd = hwnd };
            int disabled = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, ref disabled, sizeof(int));
            if (!cover.SetImage(rect, image))
            {
                cover.Dispose();
                return null;
            }
            SetWindowPos(hwnd, 0 /* HWND_TOP */, rect.X, rect.Y, rect.Width, rect.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            return cover;
        }
        catch (Exception ex)
        {
            CrashLog.Write("Drag cover", ex);
            return null;
        }
    }

    bool SetImage(Rectangle rect, BgraImage image)
    {
        var screenDc = GetDC(0);
        var srcDc = CreateCompatibleDC(screenDc);
        var dstDc = CreateCompatibleDC(screenDc);
        nint srcBmp = 0, dstBmp = 0, oldSrc = 0, oldDst = 0;
        try
        {
            srcBmp = CreateDib(screenDc, image.Width, image.Height, out var srcBits);
            dstBmp = CreateDib(screenDc, rect.Width, rect.Height, out _);
            if (srcBmp == 0 || dstBmp == 0) return false;
            Marshal.Copy(image.Pixels, 0, srcBits, image.Pixels.Length);
            oldSrc = SelectObject(srcDc, srcBmp);
            oldDst = SelectObject(dstDc, dstBmp);
            if (image.Width == rect.Width && image.Height == rect.Height)
                BitBlt(dstDc, 0, 0, rect.Width, rect.Height, srcDc, 0, 0, SRCCOPY);
            else
            {
                SetStretchBltMode(dstDc, HALFTONE);
                SetBrushOrgEx(dstDc, 0, 0, 0);
                StretchBlt(dstDc, 0, 0, rect.Width, rect.Height, srcDc, 0, 0, image.Width, image.Height, SRCCOPY);
            }
            var pos = new POINT { X = rect.X, Y = rect.Y };
            var size = new SIZE { cx = rect.Width, cy = rect.Height };
            var src = new POINT();
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255 };
            return UpdateLayeredWindow(hwnd, screenDc, ref pos, ref size, dstDc, ref src, 0, ref blend, ULW_OPAQUE);
        }
        finally
        {
            if (oldSrc != 0) SelectObject(srcDc, oldSrc);
            if (oldDst != 0) SelectObject(dstDc, oldDst);
            if (srcBmp != 0) DeleteObject(srcBmp);
            if (dstBmp != 0) DeleteObject(dstBmp);
            DeleteDC(srcDc);
            DeleteDC(dstDc);
            ReleaseDC(0, screenDc);
        }
    }

    static nint CreateDib(nint dc, int width, int height, out nint bits)
    {
        var bmi = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height,
            biPlanes = 1,
            biBitCount = 32
        };
        return CreateDIBSection(dc, ref bmi, 0, out bits, 0, 0);
    }

    static void EnsureClass()
    {
        if (registered) return;
        wndProc = DefWindowProc;
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = ClassName
        };
        RegisterClassEx(ref wc);
        registered = true;
    }

    public void Dispose()
    {
        if (hwnd == 0) return;
        ShowWindow(hwnd, SW_HIDE);
        DestroyWindow(hwnd);
        hwnd = 0;
    }

    const uint WS_POPUP = 0x80000000;
    const uint WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000;
    const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    const int SW_HIDE = 0;
    const uint ULW_OPAQUE = 0x4;
    const uint SRCCOPY = 0x00CC0020;
    const int HALFTONE = 4;
    const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;
    delegate nint WndProcDelegate(nint hwnd, uint msg, nint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "RegisterClassExW")]
    static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateWindowExW")]
    static extern nint CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    static extern nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(nint hWnd, int nCmdShow);
    [DllImport("user32.dll")] static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(nint hwnd, nint hdcDst, ref POINT pptDst, ref SIZE psize, nint hdcSrc, ref POINT pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);
    [DllImport("user32.dll")] static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")] static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] static extern nint CreateDIBSection(nint dc, ref BITMAPINFOHEADER bmi, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] static extern bool BitBlt(nint dest, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] static extern bool StretchBlt(nint dest, int x, int y, int w, int h, nint src, int sx, int sy, int sw, int sh, uint rop);
    [DllImport("gdi32.dll")] static extern int SetStretchBltMode(nint dc, int mode);
    [DllImport("gdi32.dll")] static extern bool SetBrushOrgEx(nint dc, int x, int y, nint old);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
    static extern nint GetModuleHandle(string? lpModuleName);
}
