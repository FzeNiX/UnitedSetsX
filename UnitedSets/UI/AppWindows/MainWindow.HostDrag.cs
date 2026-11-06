using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using UnitedSets.Helpers;
using UnitedSets.Services;
using WindowHoster;
using Rectangle = System.Drawing.Rectangle;

namespace UnitedSets.UI.AppWindows;

/// <summary>
/// Dragging the set window like a native window.
/// </summary>
/// <remarks>
/// <para>
/// The tabbed windows belong to other applications: they can only follow the set window after it moved, so they
/// always trailed one frame (or more) behind it while it was dragged ("jelly"), and they jumped around when Windows
/// animated the set window into a snap layout.
/// </para>
/// <para>
/// Instead, when the user starts dragging the set window, a picture of the tabbed windows is shown inside the set
/// window (so it moves exactly with it) and the real windows are hidden. When the drag ends (and the snap animation,
/// if any, is over), the windows are put in place and shown again, under a copy of the picture that is removed once
/// they have painted (many applications, e.g. Chrome, show one white frame when shown).
/// </para>
/// </remarks>
partial class MainWindow
{
    enum HostDragState
    {
        /// <summary>Not dragging</summary>
        None,
        /// <summary>The picture is shown (under the tabbed windows), the windows are still visible</summary>
        Prepared,
        /// <summary>The tabbed windows are hidden: only the picture moves with the set window</summary>
        Hidden,
        /// <summary>The drag ended: waiting for the set window to settle, then showing the windows again</summary>
        Ending,
        /// <summary>No picture could be taken (e.g. something covers the windows): the windows follow as before</summary>
        Unavailable
    }
    enum HostDragEndPhase { Settling, Showing, Revealing }

    HostDragState hostDrag;
    HostDragEndPhase hostDragEndPhase;
    bool sizeMoveIsMove, inSizeMove;
    Image? hostDragOverlay;
    BgraImage? hostDragPicture;
    long hostDragPreparedAt;
    int hostDragRenderedFrames, hostDragWindowsToShow;
    DispatcherQueueTimer? hostDragTimer;
    DragCoverWindow? hostDragCover;
    Rectangle hostDragEndRect;
    /// <summary>Size of the set window, to know when Windows snaps/maximizes it (it animates that)</summary>
    System.Drawing.Size hostDragLastSize;
    long hostDragLastResizeAt;

    const uint WM_SYSCOMMAND = 0x0112, WM_ENTERSIZEMOVE = 0x0231, WM_EXITSIZEMOVE = 0x0232;
    const uint SC_MASK = 0xFFF0, SC_MOVE = 0xF010;
    /// <summary>The picture must have been rendered before hiding the windows (or this long has passed)</summary>
    const int MaxPictureRenderWaitMs = 150;
    /// <summary>Time between the picture being rendered and it being on screen</summary>
    const int MinPictureShowMs = 20;
    /// <summary>Snap/maximize animation of the set window after the drag</summary>
    const int SnapAnimationMs = 350;
    /// <summary>Time for the applications to paint their windows once shown</summary>
    const int RevealDelayMs = 80;

    /// <summary>
    /// Handles the move/size loop messages. Returns <c>true</c> when the tabbed windows must not follow the set
    /// window for this <c>WM_MOVING</c>/<c>WM_WINDOWPOSCHANGED</c> (they are hidden).
    /// </summary>
    bool HandleHostDragMessage(uint message, nuint wParam, nint lParam)
    {
        try
        {
            switch (message)
            {
                case WM_SYSCOMMAND:
                    sizeMoveIsMove = ((uint)wParam & SC_MASK) == SC_MOVE;
                    return false;
                case WM_ENTERSIZEMOVE:
                    inSizeMove = true;
                    if (hostDrag is HostDragState.None && sizeMoveIsMove)
                        // the mouse button was just pressed on the title bar: prepare now, the windows are hidden
                        // as soon as the set window starts moving
                        PrepareHostDrag();
                    sizeMoveIsMove = false;
                    return false;
                case 0x0216 /* WM_MOVING */:
                    return OnHostMoving(lParam);
                case 0x0047 /* WM_WINDOWPOSCHANGED */:
                    if (hostDrag is HostDragState.Hidden or HostDragState.Ending)
                    {
                        var rect = GetWindowRectangle();
                        if (rect.Size != hostDragLastSize)
                        {
                            // snapped/maximized when released, or restored at the start of the drag
                            hostDragLastSize = rect.Size;
                            hostDragLastResizeAt = Environment.TickCount64;
                        }
                        // moved/resized after the drag ended: Windows snapped or maximized it (animated)
                        if (hostDrag is HostDragState.Ending && hostDragEndPhase is HostDragEndPhase.Settling && rect != hostDragEndRect)
                        {
                            hostDragEndRect = rect;
                            StartHostDragTimer(SnapAnimationMs);
                        }
                    }
                    return hostDrag is HostDragState.Hidden or HostDragState.Ending;
                case WM_EXITSIZEMOVE:
                    inSizeMove = false;
                    OnExitSizeMove();
                    return false;
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("Dragging the set window", ex);
            AbortHostDrag();
        }
        return false;
    }

    static bool IsLeftButtonDown()
    {
        // the primary button may be the right one
        var vk = GetSystemMetrics(23 /* SM_SWAPBUTTON */) != 0 ? 0x02 : 0x01;
        return (GetAsyncKeyState(vk) & 0x8000) != 0;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vk);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern int GetSystemMetrics(int index);

    bool OnHostMoving(nint movingRect)
    {
        switch (hostDrag)
        {
            case HostDragState.None:
                // started without SC_MOVE (we did not see it): prepare now, hide a bit later
                if (!inSizeMove || !PrepareHostDrag())
                {
                    hostDrag = HostDragState.Unavailable;
                    return false;
                }
                return false;
            case HostDragState.Prepared:
                if (!IsPictureOnScreen())
                {
                    // not yet: keep the set window where it is for this step (a few ms at most at the start of the
                    // drag), rather than letting the windows trail behind it
                    var current = GetWindowRectangle();
                    var proposed = System.Runtime.InteropServices.Marshal.PtrToStructure<MovingRect>(movingRect);
                    if (proposed.Right - proposed.Left != current.Width || proposed.Bottom - proposed.Top != current.Height)
                        // being restored from maximized (dragged off the top): leave that alone
                        return false;
                    System.Runtime.InteropServices.Marshal.StructureToPtr(
                        new MovingRect { Left = current.Left, Top = current.Top, Right = current.Right, Bottom = current.Bottom },
                        movingRect, false);
                    return true;
                }
                return HideWindowsForHostDrag();
            case HostDragState.Hidden:
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// The picture must be on screen before the windows disappear (otherwise the empty set window shows): a frame was
    /// rendered with it, plus a bit of time for the screen to show that frame. (Rendered is only raised when something
    /// changed: don't wait for more frames than that.)
    /// </summary>
    bool IsPictureOnScreen()
    {
        var sincePrepared = Environment.TickCount64 - hostDragPreparedAt;
        return hostDragRenderedFrames >= 1 && sincePrepared >= MinPictureShowMs || sincePrepared >= MaxPictureRenderWaitMs;
    }

    /// <summary>
    /// Hides the tabbed windows: from now on only the picture moves with the set window
    /// </summary>
    /// <returns>Whether windows were hidden</returns>
    bool HideWindowsForHostDrag()
    {
        if (RegisteredWindowController.HideForHostDrag(Win32Window.Handle) > 0)
        {
            hostDrag = HostDragState.Hidden;
            StopWatchingRender();
            return true;
        }
        RemoveHostDragPicture();
        hostDrag = HostDragState.Unavailable;
        return false;
    }

    /// <summary>
    /// Ends a drag of the set window (the windows are hidden): shows them again once the set window has settled
    /// </summary>
    void EndHostDrag()
    {
        hostDrag = HostDragState.Ending;
        hostDragEndPhase = HostDragEndPhase.Settling;
        hostDragEndRect = GetWindowRectangle();
        // Windows snaps/maximizes the window when it is released (just before or just after this) and
        // animates it: the windows are shown once the animation is over
        var sinceResize = Environment.TickCount64 - hostDragLastResizeAt;
        StartHostDragTimer(sinceResize < 200 ? (int)Math.Max(60, SnapAnimationMs - sinceResize) : 60);
    }

    void OnExitSizeMove()
    {
        switch (hostDrag)
        {
            case HostDragState.Prepared:
                // clicked the title bar without moving
                RemoveHostDragPicture();
                hostDrag = HostDragState.None;
                break;
            case HostDragState.Unavailable:
                hostDrag = HostDragState.None;
                break;
            case HostDragState.Hidden:
                EndHostDrag();
                break;
        }
    }

    /// <summary>
    /// Takes a picture of the tabbed windows and shows it in the set window, under the windows
    /// </summary>
    bool PrepareHostDrag()
    {
        if (IsClosed || Content?.XamlRoot is null) return false;
        var host = Win32Window.Handle;
        var windows = RegisteredWindowController.GetVisibleHostedAreas(host);
        if (windows.Count is 0) return false;
        var area = windows.Select(x => x.Area).Aggregate(Rectangle.Union);
        // where the picture goes: where the windows belong (a window dragged by its title bar moved a bit already)
        var place = windows.Select(x => x.Expected).Aggregate(Rectangle.Union);
        if (place.Size != area.Size) place = area;
        // only what is inside the set window
        var setRect = GetWindowRectangle();
        var visiblePlace = Rectangle.Intersect(place, setRect);
        if (visiblePlace.Width <= 0 || visiblePlace.Height <= 0) return false;
        area = new(area.X + visiblePlace.X - place.X, area.Y + visiblePlace.Y - place.Y, visiblePlace.Width, visiblePlace.Height);
        area.Intersect(new(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79))); // the virtual screen
        if (area.Size != visiblePlace.Size) return false;
        // something else in front of the windows would end up in the picture
        var expected = windows.Select(x => x.Hwnd).Append(host).ToHashSet();
        if (!ScreenCapture.IsShowing(area, expected.Contains)) return false;
        if (ScreenCapture.Capture(area) is not { } picture) return false;

        var overlay = EnsureHostDragOverlay();
        var bitmap = new WriteableBitmap(picture.Width, picture.Height);
        using (var stream = bitmap.PixelBuffer.AsStream())
            stream.Write(picture.Pixels, 0, picture.Pixels.Length);
        bitmap.Invalidate();
        overlay.Source = bitmap;
        overlay.Margin = ToRootGridMargin(visiblePlace);
        overlay.Visibility = Visibility.Visible;
        hostDragPicture = picture;
        hostDragPreparedAt = Environment.TickCount64;
        hostDragRenderedFrames = 0;
        CompositionTarget.Rendered -= OnHostDragPictureRendered;
        CompositionTarget.Rendered += OnHostDragPictureRendered;
        hostDrag = HostDragState.Prepared;
        hostDragLastSize = GetWindowRectangle().Size;
        hostDragLastResizeAt = 0;
        return true;
    }

    void OnHostDragPictureRendered(object? sender, RenderedEventArgs e)
    {
        hostDragRenderedFrames++;
        StopWatchingRender();
    }

    void StopWatchingRender() => CompositionTarget.Rendered -= OnHostDragPictureRendered;

    Image EnsureHostDragOverlay()
    {
        if (hostDragOverlay is not null) return hostDragOverlay;
        hostDragOverlay = new Image
        {
            Stretch = Stretch.Fill,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Visibility = Visibility.Collapsed
        };
        Canvas.SetZIndex(hostDragOverlay, 1000);
        Grid.SetRowSpan(hostDragOverlay, 100);
        Grid.SetColumnSpan(hostDragOverlay, 100);
        RootGrid.Children.Add(hostDragOverlay);
        return hostDragOverlay;
    }

    /// <summary>
    /// The margin that makes an element of <see cref="RootGrid"/> cover the given screen rectangle.
    /// The picture stays anchored to the edges of the window: it stretches with it (e.g. when the window is
    /// restored from maximized at the start of the drag, or when its size changes on another monitor).
    /// </summary>
    Thickness ToRootGridMargin(Rectangle screenRect)
    {
        var scale = Content.XamlRoot.RasterizationScale;
        var origin = new System.Drawing.Point();
        Windows.Win32.PInvoke.ClientToScreen(new(Win32Window.Handle), ref origin);
        var rootOffset = RootGrid.TransformToVisual(null).TransformPoint(new(0, 0));
        var border = RootGrid.BorderThickness;
        var innerWidth = RootGrid.ActualWidth - border.Left - border.Right;
        var innerHeight = RootGrid.ActualHeight - border.Top - border.Bottom;
        var left = (screenRect.X - origin.X) / scale - rootOffset.X - border.Left;
        var top = (screenRect.Y - origin.Y) / scale - rootOffset.Y - border.Top;
        var right = innerWidth - left - screenRect.Width / scale;
        var bottom = innerHeight - top - screenRect.Height / scale;
        return new(left, top, right, bottom);
    }

    /// <summary>
    /// Where the picture is on screen now (physical pixels)
    /// </summary>
    Rectangle? GetHostDragPictureScreenRect()
    {
        if (hostDragOverlay is not { } overlay || Content?.XamlRoot is null) return null;
        var scale = Content.XamlRoot.RasterizationScale;
        var origin = new System.Drawing.Point();
        Windows.Win32.PInvoke.ClientToScreen(new(Win32Window.Handle), ref origin);
        var bounds = overlay.TransformToVisual(null).TransformBounds(new(0, 0, overlay.ActualWidth, overlay.ActualHeight));
        var left = origin.X + (int)Math.Round(bounds.X * scale);
        var top = origin.Y + (int)Math.Round(bounds.Y * scale);
        var right = origin.X + (int)Math.Round((bounds.X + bounds.Width) * scale);
        var bottom = origin.Y + (int)Math.Round((bounds.Y + bounds.Height) * scale);
        if (right <= left || bottom <= top) return null;
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    void StartHostDragTimer(int milliseconds)
    {
        if (hostDragTimer is null)
        {
            hostDragTimer = DispatcherQueue.CreateTimer();
            hostDragTimer.IsRepeating = false;
            hostDragTimer.Tick += OnHostDragTimer;
        }
        hostDragTimer.Stop();
        hostDragTimer.Interval = TimeSpan.FromMilliseconds(milliseconds);
        hostDragTimer.Start();
    }

    void OnHostDragTimer(DispatcherQueueTimer sender, object args)
    {
        try
        {
            if (hostDrag is not HostDragState.Ending) return;
            switch (hostDragEndPhase)
            {
                case HostDragEndPhase.Settling:
                    if (CheckRideAlongSnap())
                    {
                        // the set window was snapped/maximized (animated): wait for it
                        hostDragEndRect = GetWindowRectangle();
                        StartHostDragTimer(SnapAnimationMs);
                        break;
                    }
                    ShowWindowsAfterHostDrag();
                    break;
                case HostDragEndPhase.Showing:
                    // some window did not report back in time: reveal anyway
                case HostDragEndPhase.Revealing:
                    CompleteHostDrag();
                    break;
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("Dragging the set window (end)", ex);
            AbortHostDrag();
        }
    }

    void ShowWindowsAfterHostDrag()
    {
        if (IsClosed)
        {
            AbortHostDrag();
            return;
        }
        // cover the area with the picture: the windows are shown under it
        hostDragCover?.Dispose();
        hostDragCover = hostDragPicture is { } picture && GetHostDragPictureScreenRect() is { } rect
            ? DragCoverWindow.Show(Win32Window.Handle, rect, picture)
            : null;
        hostDragEndPhase = HostDragEndPhase.Showing;
        hostDragWindowsToShow = RegisteredWindowController.ShowAfterHostDrag(Win32Window.Handle,
            () => DispatcherQueue.TryEnqueue(OnWindowShownAfterHostDrag));
        if (hostDragWindowsToShow <= 0)
            CompleteHostDrag();
        else
            // safety net
            StartHostDragTimer(1000);
    }

    void OnWindowShownAfterHostDrag()
    {
        if (hostDrag is not HostDragState.Ending || hostDragEndPhase is not HostDragEndPhase.Showing) return;
        if (--hostDragWindowsToShow > 0) return;
        hostDragEndPhase = HostDragEndPhase.Revealing;
        StartHostDragTimer(RevealDelayMs);
    }

    void CompleteHostDrag()
    {
        hostDragTimer?.Stop();
        hostDragCover?.Dispose();
        hostDragCover = null;
        RemoveHostDragPicture();
        hostDrag = HostDragState.None;
        FinishRideAlong();
    }

    /// <summary>
    /// Something went wrong: make sure no window stays hidden
    /// </summary>
    void AbortHostDrag()
    {
        if (mouseHook != 0)
        {
            UnhookWindowsHookEx(mouseHook);
            mouseHook = 0;
        }
        rideAlongWindow = 0;
        try
        {
            if (hostDrag is HostDragState.Hidden or HostDragState.Ending)
                RegisteredWindowController.ShowAfterHostDrag(Win32Window.Handle, static () => { });
        }
        catch { }
        try { CompleteHostDrag(); } catch { }
    }

    void RemoveHostDragPicture()
    {
        StopWatchingRender();
        hostDragPicture = null;
        if (hostDragOverlay is { } overlay)
        {
            overlay.Visibility = Visibility.Collapsed;
            overlay.Source = null;
        }
    }

    Rectangle GetWindowRectangle()
    {
        Windows.Win32.PInvoke.GetWindowRect(new(Win32Window.Handle), out var r);
        return Rectangle.FromLTRB(r.left, r.top, r.right, r.bottom);
    }
}
