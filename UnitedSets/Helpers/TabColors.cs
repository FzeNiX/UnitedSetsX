using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using UnitedSets.Configurations;
using UnitedSets.Tabs;
using Rectangle = System.Drawing.Rectangle;

namespace UnitedSets.Helpers;

/// <summary>
/// Tab colors, to tell tabs apart at a glance (e.g. several Chrome profiles).
/// <list type="bullet">
/// <item>Automatic: the color of the window's title bar (Chrome and Edge use the color of the profile there).</item>
/// <item>Chosen by the user: remembered per app, and per profile for browsers (each Chrome/Edge profile has its own
/// AppUserModelID), so every window of that profile gets the same color, now and later.</item>
/// </list>
/// </summary>
static class TabColors
{
    /// <summary>
    /// The colors that can be picked (like Chrome's tab group colors)
    /// </summary>
    public static readonly (string Name, Color Color)[] Palette =
    [
        ("Grey", Rgb(0x8A, 0x8F, 0x98)),
        ("Blue", Rgb(0x42, 0x85, 0xF4)),
        ("Red", Rgb(0xE5, 0x48, 0x3E)),
        ("Yellow", Rgb(0xF2, 0xB4, 0x00)),
        ("Green", Rgb(0x2E, 0xA0, 0x4F)),
        ("Pink", Rgb(0xE8, 0x4C, 0x9A)),
        ("Purple", Rgb(0x9B, 0x59, 0xF0)),
        ("Cyan", Rgb(0x12, 0xAE, 0xC4)),
        ("Orange", Rgb(0xF5, 0x8A, 0x2E)),
    ];

    static Color Rgb(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);

    // ---- colors chosen by the user ----

    /// <summary>
    /// identity → color ("#RRGGBB", or "none" for no color). No entry: automatic.
    /// </summary>
    static Dictionary<string, string>? chosen;
    static readonly object chosenLock = new();
    static string FilePath => Path.Combine(USConfig.AppDataPath, "tab-colors.txt");

    static Dictionary<string, string> Chosen
    {
        get
        {
            if (chosen is not null) return chosen;
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(FilePath))
                    foreach (var line in File.ReadAllLines(FilePath))
                    {
                        var tab = line.LastIndexOf('\t');
                        if (tab <= 0) continue;
                        dict[line[..tab]] = line[(tab + 1)..].Trim();
                    }
            }
            catch (Exception ex)
            {
                CrashLog.Write("Loading tab colors", ex);
            }
            return chosen = dict;
        }
    }

    /// <summary>
    /// Whether the user chose a color for <paramref name="identity"/>. <paramref name="color"/> is <c>null</c> if
    /// they chose no color.
    /// </summary>
    public static bool TryGetChosen(string identity, out Color? color)
    {
        color = null;
        string? value;
        lock (chosenLock)
            if (!Chosen.TryGetValue(identity, out value)) return false;
        color = ParseHex(value);
        return true;
    }

    /// <summary>
    /// Remembers the color for <paramref name="identity"/>: a color, <c>null</c> for no color, or
    /// <paramref name="automatic"/> to forget the choice. Every tab is updated.
    /// </summary>
    public static void SetChosen(string identity, Color? color, bool automatic = false)
    {
        lock (chosenLock)
        {
            if (automatic) Chosen.Remove(identity);
            else Chosen[identity] = color is { } c ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : "none";
            try
            {
                Directory.CreateDirectory(USConfig.AppDataPath);
                File.WriteAllLines(FilePath, Chosen.Select(x => $"{x.Key}\t{x.Value}"));
            }
            catch (Exception ex)
            {
                CrashLog.Write("Saving tab colors", ex);
            }
        }
        TabBase.RefreshAllTabColors();
    }

    static Color? ParseHex(string value)
    {
        if (value.Length != 7 || value[0] != '#') return null;
        try
        {
            return Rgb(Convert.ToByte(value[1..3], 16), Convert.ToByte(value[3..5], 16), Convert.ToByte(value[5..7], 16));
        }
        catch { return null; }
    }

    // ---- which app/profile a window belongs to ----

    /// <summary>
    /// What a chosen color applies to: the AppUserModelID of the window if it has one (each Chrome/Edge profile has
    /// its own, e.g. <c>Chrome.UserData.Profile1</c>), otherwise the program.
    /// </summary>
    public static string? GetIdentity(nint hwnd)
    {
        if (GetAppUserModelId(hwnd) is { Length: > 0 } aumid)
            return "aumid:" + aumid;
        if (GetProgramName(hwnd) is { Length: > 0 } exe)
            return "exe:" + exe.ToLowerInvariant();
        return null;
    }

    /// <summary>
    /// A short description of what a color chosen for this identity applies to
    /// </summary>
    public static string DescribeIdentity(string identity)
    {
        if (identity.StartsWith("aumid:", StringComparison.OrdinalIgnoreCase))
        {
            var id = identity[6..];
            // Chrome.UserData.Profile1, MSEdge.UserData.Default...
            var parts = id.Split('.');
            if (parts.Length >= 3 && parts[^2].Equals("UserData", StringComparison.OrdinalIgnoreCase))
                return $"every window of this {parts[0]} profile ({parts[^1]})";
            if (id.Equals("Chrome", StringComparison.OrdinalIgnoreCase) || id.StartsWith("MSEdge", StringComparison.OrdinalIgnoreCase))
                return $"every window of this {parts[0]} profile";
            return "every window of this app";
        }
        if (identity.StartsWith("exe:", StringComparison.OrdinalIgnoreCase))
            return $"every {identity[4..]} window";
        return "this tab";
    }

    static string? GetAppUserModelId(nint hwnd)
    {
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0 || store is null) return null;
            try
            {
                var key = new PROPERTYKEY { fmtid = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
                var value = default(PROPVARIANT);
                if (store.GetValue(ref key, out value) != 0) return null;
                try
                {
                    return value.vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(value.p) : null;
                }
                finally
                {
                    PropVariantClear(ref value);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }
        catch
        {
            return null;
        }
    }

    static string? GetProgramName(nint hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return null;
        var process = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (process == 0) return null;
        try
        {
            var buffer = new char[1024];
            var size = buffer.Length;
            if (!QueryFullProcessImageName(process, 0, buffer, ref size)) return null;
            return Path.GetFileNameWithoutExtension(new string(buffer, 0, size));
        }
        finally
        {
            CloseHandle(process);
        }
    }

    // ---- automatic color ----

    /// <summary>
    /// Looks at the color of the window's title bar. <paramref name="color"/> is that color if it is a real color
    /// (not white, black or gray), e.g. the color of a Chrome profile, otherwise <c>null</c>.
    /// Only reads what is on screen when the window is visible; a hidden window is asked to paint itself
    /// (<c>PrintWindow</c>, fine since nobody sees it).
    /// </summary>
    /// <returns><c>false</c> if the title bar could not be seen (e.g. something covers it)</returns>
    public static bool TryDetectTitleBarColor(nint hwnd, out Color? color)
    {
        color = null;
        try
        {
            if (!IsWindow(hwnd) || IsIconic(hwnd)) return false;
            if (GetFrameBounds(hwnd) is not { } frame || frame.Width < 64 || frame.Height < 32) return false;
            // a strip just below the top edge of the window
            var strip = new Rectangle(frame.X + 8, frame.Y + 1, frame.Width - 16, 4);
            BgraImage? image;
            if (IsWindowVisible(hwnd))
            {
                if (!ScreenCapture.IsShowing(strip, root => root == hwnd)) return false;
                image = ScreenCapture.Capture(strip);
            }
            else
                image = CaptureHidden(hwnd, strip);
            if (image is null) return false;
            if (DominantColor(image) is { } dominant && IsColorful(dominant) && !LooksLikeAccentTitleBar(dominant))
                color = dominant;
            return true;
        }
        catch (Exception ex)
        {
            CrashLog.Write("Detecting tab color", ex);
            return false;
        }
    }

    static BgraImage? CaptureHidden(nint hwnd, Rectangle strip)
    {
        if (!GetWindowRect(hwnd, out var wr)) return null;
        int w = wr.Right - wr.Left, h = wr.Bottom - wr.Top;
        if (w <= 0 || h <= 0 || w > 16384 || h > 16384) return null;
        using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            var dc = g.GetHdc();
            try
            {
                if (!PrintWindow(hwnd, dc, 2 /* PW_RENDERFULLCONTENT */)) return null;
            }
            finally
            {
                g.ReleaseHdc(dc);
            }
        }
        var x0 = strip.X - wr.Left;
        var y0 = strip.Y - wr.Top;
        if (x0 < 0 || y0 < 0 || x0 + strip.Width > w || y0 + strip.Height > h) return null;
        var pixels = new byte[strip.Width * strip.Height * 4];
        var data = bmp.LockBits(new(x0, y0, strip.Width, strip.Height), System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        try
        {
            for (int row = 0; row < strip.Height; row++)
                Marshal.Copy(data.Scan0 + row * data.Stride, pixels, row * strip.Width * 4, strip.Width * 4);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return new(strip.Width, strip.Height, pixels);
    }

    /// <summary>
    /// The most common color, if it covers a good part of the image
    /// </summary>
    static Color? DominantColor(BgraImage image)
    {
        var px = image.Pixels;
        var count = image.Width * image.Height;
        if (count == 0) return null;
        var buckets = new Dictionary<int, (int Count, long R, long G, long B)>();
        for (int i = 0; i < px.Length; i += 4)
        {
            int b = px[i], g = px[i + 1], r = px[i + 2];
            var key = (r >> 4) << 8 | (g >> 4) << 4 | (b >> 4);
            buckets.TryGetValue(key, out var e);
            buckets[key] = (e.Count + 1, e.R + r, e.G + g, e.B + b);
        }
        var best = buckets.Values.MaxBy(x => x.Count);
        if (best.Count < count * 0.35) return null;
        return Rgb((byte)(best.R / best.Count), (byte)(best.G / best.Count), (byte)(best.B / best.Count));
    }

    static void ToHsv(Color c, out double h, out double s, out double v)
    {
        double r = c.R / 255d, g = c.G / 255d, b = c.B / 255d;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        v = max;
        s = max <= 0 ? 0 : d / max;
        if (d <= 0) h = 0;
        else if (max == r) h = 60 * (((g - b) / d) % 6);
        else if (max == g) h = 60 * ((b - r) / d + 2);
        else h = 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
    }

    static Color FromHsv(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x)
        };
        return Rgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    static bool IsColorful(Color c)
    {
        ToHsv(c, out _, out var s, out var v);
        return s >= 0.15 && v >= 0.10;
    }

    /// <summary>
    /// A clear version of a detected color: same hue, but readable as a tab color on both light and dark themes
    /// (Chrome's dark mode uses very dark profile colors).
    /// </summary>
    public static Color ToTabColor(Color c)
    {
        ToHsv(c, out var h, out var s, out var v);
        return FromHsv(h, Math.Clamp(s, 0.45, 0.80), Math.Clamp(v, 0.75, 0.95));
    }

    /// <summary>
    /// With "Show accent color on title bars" on, every normal window has an accent colored title bar: that is not
    /// a color of the app.
    /// </summary>
    static bool LooksLikeAccentTitleBar(Color c)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("ColorPrevalence") is not int prevalence || prevalence == 0) return false;
            var settings = new Windows.UI.ViewManagement.UISettings();
            foreach (var type in new[] {
                Windows.UI.ViewManagement.UIColorType.Accent, Windows.UI.ViewManagement.UIColorType.AccentDark1,
                Windows.UI.ViewManagement.UIColorType.AccentDark2, Windows.UI.ViewManagement.UIColorType.AccentLight1 })
            {
                var a = settings.GetColorValue(type);
                if (Math.Abs(a.R - c.R) + Math.Abs(a.G - c.G) + Math.Abs(a.B - c.B) < 30) return true;
            }
        }
        catch { }
        return false;
    }

    static Rectangle? GetFrameBounds(nint hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out RECT r, Marshal.SizeOf<RECT>()) != 0 &&
            !GetWindowRect(hwnd, out r))
            return null;
        if (r.Right <= r.Left || r.Bottom <= r.Top) return null;
        return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct PROPERTYKEY { public Guid fmtid; public uint pid; }
    [StructLayout(LayoutKind.Sequential)]
    struct PROPVARIANT { public ushort vt; ushort r1, r2, r3; public nint p; public nint p2; }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        [PreserveSig] int Commit();
    }
    [DllImport("shell32.dll")]
    static extern int SHGetPropertyStoreForWindow(nint hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT pvar);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("kernel32.dll")] static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    static extern bool QueryFullProcessImageName(nint process, uint flags, char[] name, ref int size);
    [DllImport("user32.dll")] static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern bool PrintWindow(nint hwnd, nint dc, uint flags);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out RECT value, int size);
}
