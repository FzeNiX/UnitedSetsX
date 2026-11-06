using System.Runtime.InteropServices;
using Rectangle = System.Drawing.Rectangle;

namespace UnitedSets.Helpers;

/// <summary>
/// An image as 32 bit BGRA pixels, top-down, fully opaque (alpha 255).
/// </summary>
sealed class BgraImage(int width, int height, byte[] pixels)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Pixels { get; } = pixels;
}

/// <summary>
/// Copies pixels from the screen (what is actually displayed, including other applications' windows).
/// </summary>
static class ScreenCapture
{
    /// <summary>
    /// Copies a screen rectangle (physical pixels).
    /// </summary>
    public static BgraImage? Capture(Rectangle rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0 || rect.Width > 16384 || rect.Height > 16384) return null;
        var screenDc = GetDC(0);
        if (screenDc == 0) return null;
        var memDc = CreateCompatibleDC(screenDc);
        nint bits;
        var bmi = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = rect.Width,
            biHeight = -rect.Height, // top-down
            biPlanes = 1,
            biBitCount = 32
        };
        var dib = CreateDIBSection(screenDc, ref bmi, 0, out bits, 0, 0);
        try
        {
            if (memDc == 0 || dib == 0) return null;
            var old = SelectObject(memDc, dib);
            var ok = BitBlt(memDc, 0, 0, rect.Width, rect.Height, screenDc, rect.X, rect.Y, SRCCOPY);
            SelectObject(memDc, old);
            if (!ok) return null;
            GdiFlush();
            var pixels = new byte[rect.Width * rect.Height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            MakeOpaque(pixels);
            return new(rect.Width, rect.Height, pixels);
        }
        finally
        {
            if (dib != 0) DeleteObject(dib);
            if (memDc != 0) DeleteDC(memDc);
            ReleaseDC(0, screenDc);
        }
    }

    /// <summary>
    /// GDI leaves the alpha channel at 0 (= fully transparent for XAML)
    /// </summary>
    static void MakeOpaque(byte[] pixels)
    {
        var span = MemoryMarshal.Cast<byte, uint>(pixels.AsSpan());
        for (int i = 0; i < span.Length; i++)
            span[i] |= 0xFF000000;
    }

    /// <summary>
    /// Whether the given windows are what is visible at a grid of points of <paramref name="rect"/>
    /// (nothing like another window, a menu or a notification covers them).
    /// </summary>
    public static bool IsShowing(Rectangle rect, Func<nint, bool> isExpectedRootWindow)
    {
        for (int ix = 1; ix <= 3; ix++)
            for (int iy = 1; iy <= 3; iy++)
            {
                var pt = new POINT { X = rect.Left + rect.Width * ix / 4, Y = rect.Top + rect.Height * iy / 4 };
                if (!isExpectedRootWindow(GetAncestor(WindowFromPoint(pt), GA_ROOT)))
                    return false;
            }
        return true;
    }

    const uint SRCCOPY = 0x00CC0020;
    const uint GA_ROOT = 2;
    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }
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
    [DllImport("user32.dll")] static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("user32.dll")] static extern nint WindowFromPoint(POINT pt);
    [DllImport("user32.dll")] static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("gdi32.dll")] static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] static extern nint CreateDIBSection(nint dc, ref BITMAPINFOHEADER bmi, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] static extern bool BitBlt(nint dest, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] static extern bool GdiFlush();
}
