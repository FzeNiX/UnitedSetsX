using System.Runtime.InteropServices;
using UnitedSets.Helpers;
using UnitedSets.Mvvm.Services;
using WindowHoster;
using Rectangle = System.Drawing.Rectangle;

namespace UnitedSets.UI.AppWindows;

/// <summary>
/// Dragging a tabbed window by its own title bar moves the whole set.
/// </summary>
/// <remarks>
/// The application keeps dragging its own window (taking a drag over from another process is not reliable): the
/// window is hidden like when the set window is dragged by its tab bar (see MainWindow.HostDrag.cs), and the set
/// window follows the mouse (low level mouse hook, so it moves as soon as the mouse does). When the drag ends, the
/// window is put back in the set. If Windows snapped or maximized it (dragged to a screen edge), the set window is
/// snapped or maximized instead.
/// </remarks>
partial class MainWindow
{
    /// <summary>The tabbed window being dragged by its title bar (0 if none)</summary>
    nint rideAlongWindow;
    /// <summary>The tabbed window whose drag just ended (to check whether Windows snapped it, and to activate it)</summary>
    nint rideAlongEndedWindow;
    bool rideAlongSnapChecked;
    /// <summary>Released at the top of the screen: maximize the set</summary>
    bool rideAlongMaximize;
    System.Drawing.Point rideAlongStartCursor, rideAlongStartPosition;
    System.Drawing.Size rideAlongStartFrameSize;
    nint mouseHook;
    LowLevelMouseProc? mouseHookProc;

    /// <summary>The tabbed window whose title bar was pressed, with the picture already prepared (0 if none)</summary>
    nint rideAlongPrepared;

    /// <summary>
    /// The mouse button was pressed on a tabbed window's title bar (see <see cref="RegisteredWindow.UserMoveStarting"/>).
    /// If it is going to move the set, the picture is prepared now: it is on screen by the time the set starts moving.
    /// </summary>
    internal void PrepareRideAlong(RegisteredWindow window)
    {
        if (IsClosed || inSizeMove || rideAlongWindow != 0 || hostDrag is not HostDragState.None) return;
        if (Settings.TabbedWindowDrag.Value is not TabbedWindowDragBehaviors.MoveSet ||
            Services.WindowDragToGroupService.IsTakeOutKeyDown())
            return;
        var hwnd = window.Window.Handle;
        // on the title bar, not on a border (resizing)
        Windows.Win32.PInvoke.GetCursorPos(out var cursor);
        if (GetFrameBounds(hwnd) is not { } frame) return;
        frame.Inflate(-4, -4);
        if (!frame.Contains(cursor.X, cursor.Y)) return;
        if (PrepareHostDrag())
            rideAlongPrepared = hwnd;
    }

    /// <summary>
    /// The tabbed window was released without being dragged (see <see cref="RegisteredWindow.UserMoveEnded"/>)
    /// </summary>
    internal void CancelRideAlong(RegisteredWindow window)
    {
        if (rideAlongPrepared != window.Window.Handle || rideAlongWindow != 0) return;
        rideAlongPrepared = 0;
        if (hostDrag is HostDragState.Prepared)
        {
            RemoveHostDragPicture();
            hostDrag = HostDragState.None;
        }
    }

    /// <summary>
    /// The user started dragging a tabbed window by its own title bar (see <see cref="RegisteredWindow.HostDragRequested"/>)
    /// </summary>
    /// <param name="windowStart">Where the tabbed window was when the drag started</param>
    internal void StartRideAlong(RegisteredWindow window, System.Drawing.Point windowStart)
    {
        var hwnd = window.Window.Handle;
        var prepared = rideAlongPrepared == hwnd && hostDrag is HostDragState.Prepared;
        rideAlongPrepared = 0;
        if (IsClosed || inSizeMove || rideAlongWindow != 0 || !(prepared || hostDrag is HostDragState.None)) return;
        try
        {
            rideAlongWindow = hwnd;
            rideAlongEndedWindow = 0;
            rideAlongMaximize = false;
            rideAlongStartFrameSize = GetFrameBounds(hwnd)?.Size ?? default;
            // where the mouse button was pressed: the application moved its window by as much as the mouse moved since
            Windows.Win32.PInvoke.GetCursorPos(out var cursor);
            var moved = GetFrameBounds(hwnd) is { } now && GetWindowRect(hwnd, out var r)
                ? new System.Drawing.Point(r.Left - windowStart.X, r.Top - windowStart.Y)
                : default;
            var startCursor = new System.Drawing.Point(cursor.X - moved.X, cursor.Y - moved.Y);
            // a picture of the windows (hidden as soon as it is on screen)
            if (!prepared)
                PrepareHostDrag();
            if (IsZoomed(Win32Window.Handle))
                // like dragging a maximized window by its title bar: it is restored under the mouse
                RestoreUnderMouse(startCursor);
            rideAlongStartCursor = startCursor;
            rideAlongStartPosition = GetWindowRectangle().Location;
            mouseHookProc ??= OnLowLevelMouse;
            mouseHook = SetWindowsHookEx(14 /* WH_MOUSE_LL */, mouseHookProc, GetModuleHandle(null), 0);
            // the window was already dragged a bit: catch up
            FollowMouse(cursor.X, cursor.Y);
        }
        catch (Exception ex)
        {
            CrashLog.Write("Dragging a set by a tabbed window", ex);
        }
    }

    void RestoreUnderMouse(System.Drawing.Point cursor)
    {
        var before = GetWindowRectangle();
        var beforeFrame = GetFrameBounds(Win32Window.Handle) ?? before;
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            presenter.Restore();
        var after = GetWindowRectangle();
        var afterFrame = GetFrameBounds(Win32Window.Handle) ?? after;
        if (before.Width <= 0 || after == before) return;
        // the grabbed point stays under the mouse: same place horizontally (in proportion), same height
        var x = cursor.X - (int)Math.Round((cursor.X - beforeFrame.X) * (double)afterFrame.Width / beforeFrame.Width) - (afterFrame.X - after.X);
        var y = beforeFrame.Y - (afterFrame.Y - after.Y);
        Windows.Win32.PInvoke.SetWindowPos(new(Win32Window.Handle), default, x, y, 0, 0,
            Windows.Win32.UI.WindowsAndMessaging.SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
            Windows.Win32.UI.WindowsAndMessaging.SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
            Windows.Win32.UI.WindowsAndMessaging.SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
    }

    nint OnLowLevelMouse(int code, nint message, nint data)
    {
        if (code >= 0 && rideAlongWindow != 0)
        {
            try
            {
                if (message is 0x0200 /* WM_MOUSEMOVE */)
                {
                    var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(data);
                    FollowMouse(info.X, info.Y);
                }
                else if (message is 0x0202 or 0x0205 /* WM_LBUTTONUP, WM_RBUTTONUP */)
                {
                    var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(data);
                    if (IsAtTopOfScreen(info.X, info.Y))
                    {
                        // Released at the top of the screen: Windows would maximize the (hidden) tabbed window, and
                        // that shows it. End its drag before it gets this release; the set is maximized instead.
                        rideAlongMaximize = true;
                        SendMessageTimeout(rideAlongWindow, 0x001F /* WM_CANCELMODE */, 0, 0, 2 /* SMTO_ABORTIFHUNG */, 100, out _);
                    }
                    // the end of the drag is normally reported by the application's window; in case it isn't
                    var window = rideAlongWindow;
                    var timer = DispatcherQueue.CreateTimer();
                    timer.Interval = TimeSpan.FromMilliseconds(500);
                    timer.IsRepeating = false;
                    timer.Tick += (_, _) =>
                    {
                        if (rideAlongWindow == window)
                            EndRideAlong(window);
                    };
                    timer.Start();
                }
            }
            catch (Exception ex)
            {
                CrashLog.Write("Dragging a set by a tabbed window (mouse)", ex);
            }
        }
        return CallNextHookEx(0, code, message, data);
    }

    void FollowMouse(int x, int y)
    {
        if (rideAlongWindow == 0 || IsClosed) return;
        Windows.Win32.PInvoke.SetWindowPos(new(Win32Window.Handle), default,
            rideAlongStartPosition.X + x - rideAlongStartCursor.X, rideAlongStartPosition.Y + y - rideAlongStartCursor.Y, 0, 0,
            Windows.Win32.UI.WindowsAndMessaging.SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
            Windows.Win32.UI.WindowsAndMessaging.SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
            Windows.Win32.UI.WindowsAndMessaging.SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
        if (hostDrag is HostDragState.Prepared && IsPictureOnScreen())
            HideWindowsForHostDrag();
    }

    /// <summary>
    /// The drag of the tabbed window ended (see <see cref="RegisteredWindow.HostDragEnded"/>)
    /// </summary>
    internal void EndRideAlong(RegisteredWindow window) => EndRideAlong(window.Window.Handle);

    void EndRideAlong(nint hwnd)
    {
        if (rideAlongWindow != hwnd) return;
        rideAlongWindow = 0;
        if (mouseHook != 0)
        {
            UnhookWindowsHookEx(mouseHook);
            mouseHook = 0;
        }
        try
        {
            if (hostDrag is HostDragState.Prepared)
                // not hidden yet: nothing to cover
                RemoveHostDragPicture();
            // like the end of a drag of the set window (see OnHostDragTimer): wait a bit (Windows may snap the
            // dragged window, see CheckRideAlongSnap), then put the windows back in place and show them
            rideAlongEndedWindow = hwnd;
            rideAlongSnapChecked = false;
            hostDrag = HostDragState.Ending;
            hostDragEndPhase = HostDragEndPhase.Settling;
            hostDragEndRect = GetWindowRectangle();
            StartHostDragTimer(80);
        }
        catch (Exception ex)
        {
            CrashLog.Write("Dragging a set by a tabbed window (end)", ex);
            AbortHostDrag();
        }
    }

    /// <summary>
    /// Windows snaps or maximizes a window dragged to a screen edge: the dragged tabbed window was snapped instead of
    /// the set. Does the same with the set window (the tabbed window is put back in it afterwards).
    /// </summary>
    /// <returns>Whether the set window was snapped/maximized (it is animated: wait before showing the windows)</returns>
    bool CheckRideAlongSnap()
    {
        if (rideAlongSnapChecked || rideAlongEndedWindow == 0) return false;
        rideAlongSnapChecked = true;
        var hwnd = rideAlongEndedWindow;
        if (!IsWindow(hwnd)) return false;
        if (rideAlongMaximize || IsZoomed(hwnd))
        {
            // (the tabbed window is made normal again when it is shown, see RegisteredWindowController)
            rideAlongMaximize = false;
            Maximize();
            return true;
        }
        if (rideAlongStartFrameSize == default || GetFrameBounds(hwnd) is not { } frame || frame.Size == rideAlongStartFrameSize)
            return false;
        // snapped into a zone: the set window takes that place (its visible frame where the window's frame is)
        var rect = GetWindowRectangle();
        var setFrame = GetFrameBounds(Win32Window.Handle) ?? rect;
        int left = setFrame.Left - rect.Left, top = setFrame.Top - rect.Top;
        int right = rect.Right - setFrame.Right, bottom = rect.Bottom - setFrame.Bottom;
        Windows.Win32.PInvoke.SetWindowPos(new(Win32Window.Handle), default,
            frame.Left - left, frame.Top - top, frame.Width + left + right, frame.Height + top + bottom,
            Windows.Win32.UI.WindowsAndMessaging.SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
            Windows.Win32.UI.WindowsAndMessaging.SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
        return true;
    }

    /// <summary>
    /// After the windows were put back: the dragged window gets the focus back (the user clicked it)
    /// </summary>
    void FinishRideAlong()
    {
        var hwnd = rideAlongEndedWindow;
        rideAlongEndedWindow = 0;
        if (hwnd == 0) return;
        // windows that were not hidden were moved by their application: put them back
        RegisteredWindowController.RefreshHost(Win32Window.Handle);
        if (IsWindow(hwnd))
            SetForegroundWindow(hwnd);
    }

    /// <summary>
    /// Whether the point is on the top edge of a screen (where Windows maximizes a window that is dropped)
    /// </summary>
    static bool IsAtTopOfScreen(int x, int y)
    {
        var monitor = MonitorFromPoint(new() { X = x, Y = y }, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        return monitor != 0 && GetMonitorInfo(monitor, ref info) && y <= info.Monitor.Top + 1;
    }

    static unsafe Rectangle? GetFrameBounds(nint hwnd)
    {
        RECT r;
        if (DwmGetWindowAttribute(hwnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, &r, sizeof(RECT)) != 0 && !GetWindowRect(hwnd, out r))
            return null;
        if (r.Right <= r.Left || r.Bottom <= r.Top) return null;
        return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    delegate nint LowLevelMouseProc(int code, nint message, nint data);
    [StructLayout(LayoutKind.Sequential)]
    struct MSLLHOOKSTRUCT { public int X, Y; public uint MouseData, Flags, Time; public nint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] static extern nint MonitorFromPoint(POINT point, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);
    [DllImport("user32.dll", SetLastError = true)]
    static extern nint SetWindowsHookEx(int idHook, LowLevelMouseProc proc, nint module, uint threadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
    static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll")] static extern bool IsZoomed(nint hwnd);
    [DllImport("user32.dll")] static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint hwnd, out RECT rect);
    [DllImport("dwmapi.dll")] static extern unsafe int DwmGetWindowAttribute(nint hwnd, int attribute, void* value, int size);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    static extern nint SendMessageTimeout(nint hwnd, uint msg, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
}
