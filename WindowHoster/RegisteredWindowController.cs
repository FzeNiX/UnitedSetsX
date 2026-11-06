using Get.EasyCSharp;
using WinWrapper.Windowing;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using System;
using System.Collections.Generic;
using System.Drawing;
using Microsoft.UI.Dispatching;
using System.Threading;
using WinWrapper.Input;
namespace WindowHoster;

/// <summary>
/// Keeps a <see cref="RegisteredWindow"/> positioned over a rectangle of its host window.
/// </summary>
/// <remarks>
/// Positioning happens on a dedicated background thread so that a hung hosted application
/// (cross-process <c>SetWindowPos</c> may block) never freezes the host UI.
/// </remarks>
public partial class RegisteredWindowController
{
    /// <summary>
    /// How many times in a row the controller puts back a window that moves/resizes itself
    /// before giving up (until the host area changes). Prevents fighting with applications
    /// that snap their size (e.g. console windows) or enforce a minimum size.
    /// </summary>
    const int MaxConsecutiveCorrections = 3;
    internal bool Updating { get; private set; } = false;
    volatile bool isRegistered = true;
    readonly Window Parent;
    readonly RegisteredWindow self;
    readonly AutoResetEvent updateSignal = new(false);
    readonly WinEventsRegistrationParameters parentPositionChangedRegistration;
    [Property(OnChanged = nameof(CointainerRectangleChanged))]
    Windows.Foundation.Rect _ContainerRectangle;
    /// <summary>
    /// The scale used to convert <see cref="ContainerRectangle"/> (in DIPs) into physical pixels.
    /// Should be the host's <c>XamlRoot.RasterizationScale</c>. If 0, the scale of the display
    /// the host window is on is used.
    /// </summary>
    public double RasterizationScale { get; set; }
    internal Rectangle LatestRequestedRect { get; private set; }
    int consecutiveCorrections;
    /// <summary>
    /// <see cref="Environment.TickCount64"/> of the last time the host window moved/resized
    /// </summary>
    long lastParentMove;
    /// <summary>
    /// When to do a final "settle" update after the host stopped moving (0 = none)
    /// </summary>
    long settleAt;
    /// <summary>
    /// The host window is being moved/resized. While this is the case, a hosted window that resizes itself
    /// (e.g. because it moved to a monitor with a different DPI) is not corrected immediately:
    /// fighting with it at every frame is what made the window look "jelly". A final update is done
    /// once the host stops moving.
    /// </summary>
    internal bool ParentMovedRecently => Environment.TickCount64 - Interlocked.Read(ref lastParentMove) < 250;

    static readonly System.Collections.Concurrent.ConcurrentDictionary<RegisteredWindowController, byte> Active = new();

    // ---- deferred hiding (tab switches without a flash) ----
    static readonly object pendingHideLock = new();
    static readonly List<(nint Host, RegisteredWindow Window, long Deadline)> pendingHides = [];
    static Timer? pendingHideTimer;
    /// <summary>
    /// How long to wait for another window to be shown before hiding anyway (e.g. switching to an empty tab)
    /// </summary>
    const int PendingHideTimeoutMs = 300;

    static void SchedulePendingHide(nint host, RegisteredWindow window)
    {
        lock (pendingHideLock)
        {
            pendingHides.Add((host, window, Environment.TickCount64 + PendingHideTimeoutMs));
            pendingHideTimer ??= new Timer(_ => HideExpired(), null, 50, 50);
        }
    }

    static void HideExpired()
    {
        List<RegisteredWindow> toHide = [];
        lock (pendingHideLock)
        {
            var now = Environment.TickCount64;
            for (int i = pendingHides.Count - 1; i >= 0; i--)
                if (pendingHides[i].Deadline <= now)
                {
                    toHide.Add(pendingHides[i].Window);
                    pendingHides.RemoveAt(i);
                }
        }
        foreach (var w in toHide) HideIfStillUnused(w);
    }

    /// <summary>
    /// Hides the windows that were waiting to be hidden on <paramref name="host"/>, now that <paramref name="shown"/> covers the area
    /// </summary>
    static void FlushPendingHides(nint host, RegisteredWindow shown)
    {
        List<RegisteredWindow> toHide = [];
        lock (pendingHideLock)
        {
            for (int i = pendingHides.Count - 1; i >= 0; i--)
                if (pendingHides[i].Host == host || pendingHides[i].Window == shown)
                {
                    if (pendingHides[i].Window != shown)
                        toHide.Add(pendingHides[i].Window);
                    pendingHides.RemoveAt(i);
                }
        }
        foreach (var w in toHide) HideIfStillUnused(w);
    }

    /// <summary>
    /// The window of the previous tab of <paramref name="host"/>, if it is still visible (waiting to be hidden)
    /// </summary>
    static Window? GetVisiblePendingHide(nint host, RegisteredWindow except)
    {
        lock (pendingHideLock)
        {
            for (int i = pendingHides.Count - 1; i >= 0; i--)
            {
                var (h, w, _) = pendingHides[i];
                if (h != host || w == except || !w.IsValid || w.CurrentController is not null) continue;
                var window = w.Window;
                if (window.IsVisible) return window;
            }
        }
        return null;
    }

    static void HideIfStillUnused(RegisteredWindow w)
    {
        try
        {
            // detached, closed, or shown again (selected again, moved to another set) in the mean time
            if (!w.IsValid || w.CurrentController is not null) return;
            var window = w.Window;
            window.IsVisible = false;
        }
        catch { }
    }

    /// <summary>
    /// Position of the hosted window relative to the host window rectangle, as of the last full update
    /// </summary>
    System.Drawing.Point relativeToHost;
    bool hasRelativeToHost;

    /// <summary>
    /// Call from the host window procedure when it moves or is about to move (<c>WM_MOVING</c>,
    /// <c>WM_WINDOWPOSCHANGED</c>) with its new window position (screen pixels, like <c>GetWindowRect</c>).
    /// The hosted windows are moved right away, together with the host. They used to follow only after a
    /// WinEvent round trip, which made them trail behind (the "jelly" effect), especially across monitors.
    /// Calling it several times with the same position does nothing.
    /// </summary>
    public static void OnHostMoved(nint hostHwnd, int hostX, int hostY)
    {
        foreach (var (controller, _) in Active)
        {
            if (controller.Parent.Handle != hostHwnd || !controller.isRegistered || !controller.hasRelativeToHost) continue;
            // hidden while the host is dragged, or dragged by the user (the host follows it):
            // it is put in place when the drag ends
            if (controller.hiddenForHostDrag || controller.self.IsDraggingHost) continue;
            var self = controller.self;
            if (!self.IsValid || self.CompatablityMode.NoMoving) continue;
            var rect = controller.LatestRequestedRect;
            if (rect.Width <= 0 || rect.Height <= 0) continue;
            var target = new System.Drawing.Point(hostX + controller.relativeToHost.X, hostY + controller.relativeToHost.Y);
            if (rect.Location == target) continue;
            rect.Location = target;
            controller.LatestRequestedRect = rect;
            Interlocked.Exchange(ref controller.lastParentMove, Environment.TickCount64);
            // Moved by the positioning thread right away (without waiting for the WinEvent round trip).
            // Not with SWP_ASYNCWINDOWPOS: that runs SetWindowPos on the hosted application's thread, in its DPI
            // awareness context, and DPI unaware windows were then placed ~1/1.5 off on a 150% monitor.
            lock (controller.moveLock)
                controller.moveTarget = target;
            Interlocked.Exchange(ref controller.moveRequested, 1);
            controller.updateSignal.Set();
        }
    }

    /// <summary>
    /// The size of the area changed (from, to): the hidden windows of the other tabs need the new size too
    /// </summary>
    (System.Drawing.Rectangle From, System.Drawing.Rectangle To)? pendingSiblingSync;

    /// <summary>
    /// Gives the hidden windows of the other tabs of the same host the new size (when the host is resized or
    /// maximized), so they are ready when their tab is selected. They used to be resized only when shown, so
    /// the application had to lay out and repaint the whole window at that moment (it looked like a resize/flicker).
    /// Only windows that covered the same area are synced (not the cells of splitable tabs).
    /// </summary>
    void SyncHiddenSiblings(System.Drawing.Rectangle from, System.Drawing.Rectangle to)
    {
        var host = Parent.Handle;
        foreach (var (rw, _) in RegisteredWindow.AllRegistered)
        {
            try
            {
                if (rw == self || rw.LastHost != host || !rw.IsValid || rw.CurrentController is not null) continue;
                if (rw.CompatablityMode.NoMoving || rw.Properties.ActivateCrop) continue;
                if (rw.LastArea.Size != from.Size) continue;
                var window = rw.Window;
                if (window.IsVisible) continue;
                var target = Compensate(to, rw.CachedInvisibleBorders ?? default);
                rw.LastArea = to;
                if (window.Bounds != target)
                    window.Bounds = target;
            }
            catch { }
        }
    }

    internal nint ParentHandle => Parent.Handle;

    /// <summary>
    /// Asks every hosted window of <paramref name="hostHwnd"/> to be put where it belongs (asynchronously)
    /// </summary>
    public static void RefreshHost(nint hostHwnd)
    {
        foreach (var (controller, _) in Active)
            if (controller.Parent.Handle == hostHwnd && controller.isRegistered)
                controller.QueueUpdatePosition(resetCorrections: true);
    }

    // ---- dragging the host ----
    // A hosted window is another application's window: it can only be moved after the host moved, by another
    // SetWindowPos that the application has to process. While the user drags the host, the hosted window was always
    // one frame (or more) behind it ("jelly"). Instead, the host shows a picture of the hosted windows while it is
    // dragged (see HideForHostDrag) and the real windows are put back in place when the drag ends.

    /// <summary>
    /// Hidden by <see cref="HideForHostDrag"/> (until <see cref="ShowAfterHostDrag"/>)
    /// </summary>
    volatile bool hiddenForHostDrag;
    /// <summary>
    /// Set by <see cref="ShowAfterHostDrag"/>: the positioning thread puts the window in place and shows it
    /// </summary>
    volatile bool showAfterHostDrag;
    Action? shownAfterHostDrag;

    /// <summary>
    /// The screen rectangles (physical pixels) of the hosted windows of <paramref name="hostHwnd"/> that are on screen
    /// right now (as shown, without the invisible borders): where they are, and where they should be (they may have
    /// been moved a bit, e.g. when the user started dragging one by its title bar).
    /// </summary>
    public static List<(nint Hwnd, Rectangle Area, Rectangle Expected)> GetVisibleHostedAreas(nint hostHwnd)
    {
        List<(nint, Rectangle, Rectangle)> areas = [];
        foreach (var (controller, _) in Active)
        {
            if (controller.Parent.Handle != hostHwnd || !controller.isRegistered || controller.hiddenForHostDrag) continue;
            var self = controller.self;
            if (!self.IsValid || self.CompatablityMode.NoMoving) continue;
            var hwnd = (HWND)self.Window.Handle;
            if (!PInvoke.IsWindowVisible(hwnd) || PInvoke.IsIconic(hwnd)) continue;
            if (GetFrameBounds(hwnd) is not { } frame) continue;
            var expected = frame;
            var requested = controller.LatestRequestedRect;
            if (requested.Width > 0 && PInvoke.GetWindowRect(hwnd, out var actual))
                expected.Offset(requested.X - actual.left, requested.Y - actual.top);
            areas.Add((hwnd, frame, expected));
        }
        return areas;
    }

    static unsafe Rectangle? GetFrameBounds(HWND hwnd)
    {
        RECT frame;
        if (PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, &frame, (uint)sizeof(RECT)).Failed &&
            !PInvoke.GetWindowRect(hwnd, out frame))
            return null;
        if (frame.right <= frame.left || frame.bottom <= frame.top) return null;
        return Rectangle.FromLTRB(frame.left, frame.top, frame.right, frame.bottom);
    }

    /// <summary>
    /// Hides the visible hosted windows of <paramref name="hostHwnd"/> while the host is being dragged (call on the
    /// host thread, once the host shows a picture of them). They stop following the host until
    /// <see cref="ShowAfterHostDrag"/> is called. Windows of applications that don't respond are left alone.
    /// </summary>
    /// <returns>How many windows were hidden</returns>
    public static int HideForHostDrag(nint hostHwnd)
    {
        int count = 0;
        foreach (var (controller, _) in Active)
        {
            if (controller.Parent.Handle != hostHwnd || !controller.isRegistered || controller.hiddenForHostDrag) continue;
            var self = controller.self;
            if (!self.IsValid || self.CompatablityMode.NoMoving) continue;
            var hwnd = (HWND)self.Window.Handle;
            if (!PInvoke.IsWindowVisible(hwnd) || PInvoke.IsIconic(hwnd) || PInvoke.IsHungAppWindow(hwnd)) continue;
            controller.hiddenForHostDrag = true;
            controller.Updating = true;
            try
            {
                PInvoke.SetWindowPos(hwnd, HWND.Null, 0, 0, 0, 0,
                    Windows.Win32.UI.WindowsAndMessaging.SET_WINDOW_POS_FLAGS.SWP_NOMOVE |
                    Windows.Win32.UI.WindowsAndMessaging.SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
                    Windows.Win32.UI.WindowsAndMessaging.SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
                    Windows.Win32.UI.WindowsAndMessaging.SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
                    Windows.Win32.UI.WindowsAndMessaging.SET_WINDOW_POS_FLAGS.SWP_HIDEWINDOW);
            }
            finally
            {
                controller.Updating = false;
            }
            count++;
        }
        return count;
    }

    /// <summary>
    /// Puts the windows hidden by <see cref="HideForHostDrag"/> back in place in the host and shows them
    /// (asynchronously). <paramref name="onShown"/> is called once per window (on another thread), after it is shown.
    /// </summary>
    /// <returns>How many windows will be shown (= how many times <paramref name="onShown"/> will be called)</returns>
    public static int ShowAfterHostDrag(nint hostHwnd, Action onShown)
    {
        int count = 0;
        foreach (var (controller, _) in Active)
        {
            if (controller.Parent.Handle != hostHwnd || !controller.hiddenForHostDrag || controller.showAfterHostDrag) continue;
            controller.shownAfterHostDrag = onShown;
            controller.showAfterHostDrag = true;
            controller.updateSignal.Set();
            count++;
        }
        return count;
    }

    /// <summary>
    /// Whether the window is hidden because the host is being dragged
    /// </summary>
    internal bool IsHiddenForHostDrag => hiddenForHostDrag;

    /// <summary>
    /// The window was shown by something else while hidden for a drag of the host (e.g. Windows maximized it because it
    /// was dragged to the top of the screen): hide it again.
    /// </summary>
    internal void HideAgainForHostDrag()
    {
        if (!hiddenForHostDrag || showAfterHostDrag) return;
        var hwnd = (HWND)self.Window.Handle;
        Updating = true;
        try
        {
            PInvoke.SetWindowPos(hwnd, HWND.Null, 0, 0, 0, 0,
                Windows.Win32.UI.WindowsAndMessaging.SET_WINDOW_POS_FLAGS.SWP_NOMOVE |
                Windows.Win32.UI.WindowsAndMessaging.SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
                Windows.Win32.UI.WindowsAndMessaging.SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
                Windows.Win32.UI.WindowsAndMessaging.SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
                Windows.Win32.UI.WindowsAndMessaging.SET_WINDOW_POS_FLAGS.SWP_HIDEWINDOW);
        }
        finally
        {
            Updating = false;
        }
    }

    void DoShowAfterHostDrag()
    {
        var callback = Interlocked.Exchange(ref shownAfterHostDrag, null);
        try
        {
            if (!isRegistered || !self.IsValid) return;
            Updating = true;
            try
            {
                var window = self.Window;
                // maximized by Windows during the drag (dragged to the top of the screen): back to normal, still hidden
                if (PInvoke.IsZoomed((HWND)window.Handle) || PInvoke.IsIconic((HWND)window.Handle))
                    WindowPlacementHelper.RestoreHidden(window.Handle);
                // in place first (still hidden), then shown without changing the z-order: the host covers it
                // with a picture until the application has painted it
                SetBounds(window);
                PInvoke.SetWindowPos((HWND)window.Handle, HWND.Null, 0, 0, 0, 0,
                    Windows.Win32.UI.WindowsAndMessaging.SET_WINDOW_POS_FLAGS.SWP_NOMOVE |
                    Windows.Win32.UI.WindowsAndMessaging.SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
                    Windows.Win32.UI.WindowsAndMessaging.SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
                    Windows.Win32.UI.WindowsAndMessaging.SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
                    Windows.Win32.UI.WindowsAndMessaging.SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
                // the invisible borders can only be measured once the window is visible
                SetBounds(window);
            }
            finally
            {
                Updating = false;
            }
        }
        finally
        {
            hiddenForHostDrag = false;
            showAfterHostDrag = false;
            callback?.Invoke();
        }
    }

    readonly object moveLock = new();
    System.Drawing.Point moveTarget;
    int moveRequested, fullUpdateRequested;

    void DoMove()
    {
        if (!isRegistered || !self.IsValid || self.CompatablityMode.NoMoving) return;
        System.Drawing.Point target;
        lock (moveLock) target = moveTarget;
        Updating = true;
        try
        {
            PInvoke.SetWindowPos((HWND)self.Window.Handle, HWND.Null, target.X, target.Y, 0, 0,
                Windows.Win32.UI.WindowsAndMessaging.SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
                Windows.Win32.UI.WindowsAndMessaging.SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
                Windows.Win32.UI.WindowsAndMessaging.SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
        }
        finally
        {
            Updating = false;
        }
    }

    /// <summary>
    /// The hosted window moved or resized itself: put it back, unless the host is moving (see <see cref="ParentMovedRecently"/>).
    /// </summary>
    internal void OnHostedWindowChangedItself()
    {
        if (ParentMovedRecently)
        {
            ScheduleSettle();
            return;
        }
        QueueUpdatePosition(resetCorrections: false);
    }

    void ScheduleSettle()
    {
        Interlocked.Exchange(ref settleAt, Environment.TickCount64 + 300);
        // wake the thread so it starts waiting with the new timeout
        updateSignal.Set();
    }

    /// <summary>
    /// Requests the window position to be updated (asynchronously).
    /// </summary>
    internal void UpdatePosition() => QueueUpdatePosition(resetCorrections: true);

    /// <param name="resetCorrections">
    /// <c>true</c> if this update is caused by the host (container moved/resized, user action),
    /// <c>false</c> if it is a correction because the hosted window moved itself.
    /// </param>
    internal void QueueUpdatePosition(bool resetCorrections)
    {
        if (!isRegistered) return;
        if (resetCorrections)
            consecutiveCorrections = 0;
        else if (Interlocked.Increment(ref consecutiveCorrections) > MaxConsecutiveCorrections)
            return;
        Interlocked.Exchange(ref fullUpdateRequested, 1);
        updateSignal.Set();
    }

    void DoUpdatePosition()
    {
        if (!isRegistered || !self.IsValid) return;
        if (self.CompatablityMode.NoMoving) return;
        Updating = true;
        try
        {
            var window = self.Window;
            if (queueSetVisible)
            {
                queueSetVisible = false;
                // compute the position first so the window doesn't flash at its old location
                SetBounds(window);
                if (GetVisiblePendingHide(Parent.Handle, self) is { } previous)
                {
                    // Switching tabs: show the new window right below the previous one first, so the application
                    // has painted it before it comes to the front (otherwise its first, still empty, frame flashed)
                    PInvoke.SetWindowPos((HWND)window.Handle, (HWND)previous.Handle, 0, 0, 0, 0,
                        Windows.Win32.UI.WindowsAndMessaging.SET_WINDOW_POS_FLAGS.SWP_NOMOVE |
                        Windows.Win32.UI.WindowsAndMessaging.SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
                        Windows.Win32.UI.WindowsAndMessaging.SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
                        Windows.Win32.UI.WindowsAndMessaging.SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
                    SetBounds(window);
                    Thread.Sleep(40);
                    PInvoke.SetWindowPos((HWND)window.Handle, HWND.Null /* HWND_TOP */, 0, 0, 0, 0,
                        Windows.Win32.UI.WindowsAndMessaging.SET_WINDOW_POS_FLAGS.SWP_NOMOVE |
                        Windows.Win32.UI.WindowsAndMessaging.SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
                        Windows.Win32.UI.WindowsAndMessaging.SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
                }
                else
                {
                    window.IsVisible = true;
                    // the invisible borders can only be measured reliably once the window is visible
                    SetBounds(window);
                }
                // now that this window covers the area, hide the window of the previous tab (see Unregister)
                FlushPendingHides(Parent.Handle, self);
                // do not steal focus if we are doing things with other window
                Thread.Sleep(100);
                if (isRegistered && !Cursor.IsLeftButtonDown)
                    window.SetAsForegroundWindow();
            }
            else
            {
                SetBounds(window);
            }
        }
        finally
        {
            Updating = false;
        }
    }

    void SetBounds(Window window)
    {
        System.Drawing.Point windowOffset = default;
        unsafe
        {
            _ = PInvoke.MapWindowPoints(
                (HWND)Parent.Handle,
                default,
                &windowOffset,
                1
            );
        }
        var scale = RasterizationScale > 0 ? RasterizationScale : Parent.CurrentDisplay.ScaleFactor / 100d;
        var requestedRect = RectToScreen(ContainerRectangle, windowOffset, scale);
        if (requestedRect.Width <= 0 || requestedRect.Height <= 0)
            // the host is not laid out yet
            return;
        var previousArea = self.LastArea;
        self.LastArea = requestedRect;
        if (previousArea.Size != requestedRect.Size && !previousArea.IsEmpty)
            pendingSiblingSync = (previousArea, requestedRect);
        if (self.Properties.ActivateCrop)
        {
            var cropRegion = self.Properties.CropRegion;
            requestedRect = requestedRect with
            {
                X = requestedRect.X - cropRegion.Left,
                Y = requestedRect.Y - cropRegion.Top,
                Width = requestedRect.Width + cropRegion.Left + cropRegion.Right,
                Height = requestedRect.Height + cropRegion.Top + cropRegion.Bottom
            };
            var currentBounds = window.Bounds;
            if (self.Properties.ForceInvalidateCrop || currentBounds != requestedRect)
            {
                self.Properties.ForceInvalidateCrop = false;
                _ = window.SetRegionAsync(new(
                    cropRegion.Left,
                    cropRegion.Top,
                    requestedRect.Width - cropRegion.Left - cropRegion.Right,
                    requestedRect.Height - cropRegion.Top - cropRegion.Bottom
                ));
            }
        }
        else
        {
            var insets = GetInvisibleBorders(window);
            var compensated = Compensate(requestedRect, insets);
            LatestRequestedRect = compensated;
            RememberRelativePosition(compensated);
            if (window.Bounds != compensated)
                window.Bounds = compensated;
            // The borders can change with the size/style (and DWM may report stale values right after
            // the window is shown): measure again and correct once if needed.
            if (MeasureInvisibleBorders(window) is { } measured && measured != insets)
            {
                self.CachedInvisibleBorders = measured;
                compensated = Compensate(requestedRect, measured);
                LatestRequestedRect = compensated;
                RememberRelativePosition(compensated);
                window.Bounds = compensated;
            }
            return;
        }
        LatestRequestedRect = requestedRect;
        RememberRelativePosition(requestedRect);
        if (window.Bounds != requestedRect)
            window.Bounds = requestedRect;
    }

    void RememberRelativePosition(Rectangle hostedRect)
    {
        if (!PInvoke.GetWindowRect((HWND)Parent.Handle, out var host)) return;
        relativeToHost = new(hostedRect.X - host.left, hostedRect.Y - host.top);
        hasRelativeToHost = true;
    }

    static Rectangle Compensate(Rectangle target, InvisibleBorders b) => new(
        target.X - b.Left,
        target.Y - b.Top,
        target.Width + b.Left + b.Right,
        target.Height + b.Top + b.Bottom
    );

    /// <summary>
    /// Since Windows 10, normal windows have invisible resize borders (usually ~7px on the left, right and bottom)
    /// that are part of the window rectangle. Without compensating, hosted windows have visible gaps.
    /// </summary>
    InvisibleBorders GetInvisibleBorders(Window window)
    {
        if (MeasureInvisibleBorders(window) is { } measured)
            self.CachedInvisibleBorders = measured;
        return self.CachedInvisibleBorders ?? default;
    }

    /// <summary>
    /// Measures the invisible borders. Returns <c>null</c> when the window is hidden or minimized
    /// (DWM reports stale values then).
    /// </summary>
    static unsafe InvisibleBorders? MeasureInvisibleBorders(Window window)
    {
        var hwnd = (HWND)window.Handle;
        if (!PInvoke.IsWindowVisible(hwnd) || PInvoke.IsIconic(hwnd)) return null;
        RECT frame;
        if (PInvoke.DwmGetWindowAttribute(
                hwnd,
                DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS,
                &frame,
                (uint)sizeof(RECT)
            ).Failed)
            return null;
        if (!PInvoke.GetWindowRect(hwnd, out var rect))
            return null;
        var borders = new InvisibleBorders(
            frame.left - rect.left,
            frame.top - rect.top,
            rect.right - frame.right,
            rect.bottom - frame.bottom
        );
        // sanity check: the frame must be inside the window rectangle and the borders must be small
        const int MaxBorder = 64;
        if (borders.Left is < 0 or > MaxBorder || borders.Top is < 0 or > MaxBorder ||
            borders.Right is < 0 or > MaxBorder || borders.Bottom is < 0 or > MaxBorder)
            return null;
        return borders;
    }

    static Rectangle RectToScreen(Windows.Foundation.Rect rect, System.Drawing.Point WindowPosOffset, double RasterizationScale)
    {
        return new(
            (int)Math.Round(rect.X * RasterizationScale + WindowPosOffset.X),
            (int)Math.Round(rect.Y * RasterizationScale + WindowPosOffset.Y),
            (int)Math.Round(rect.Width * RasterizationScale),
            (int)Math.Round(rect.Height * RasterizationScale)
        );
    }
    volatile bool queueSetVisible;
    internal readonly DispatcherQueue DispatcherQueue;
    internal RegisteredWindowController(Window Parent, RegisteredWindow self, DispatcherQueue dispatcherQueue)
    {
        this.Parent = Parent;
        this.self = self;
        DispatcherQueue = dispatcherQueue;
        queueSetVisible = true;
        self.InternalUpdateParent(Parent);
        parentPositionChangedRegistration = WinEvents.Register(Parent.Handle, WinEventTypes.PositionSizeChanged, false, ParentPositionChangedHandler);
        thread = new(ThreadLoop)
        {
            IsBackground = true,
            Name = "United Sets Window Positioning"
        };
        Active[this] = 0;
        thread.Start();
    }
    readonly Thread thread;
    void ParentPositionChangedHandler(WinEventTypes eventType, nint hwnd, int idObject, int idChild, uint idEventThread, uint dwmsEventTime)
    {
        if (!WinEvents.IsWindowItself(idObject, idChild)) return;
        Interlocked.Exchange(ref lastParentMove, Environment.TickCount64);
        // one last update once the host stops moving (DPI changes, windows that resized themselves...)
        Interlocked.Exchange(ref settleAt, Environment.TickCount64 + 300);
        // we can't update immedietly when the handler is called
        // so we do it in another thread
        QueueUpdatePosition(resetCorrections: true);
    }
    void CointainerRectangleChanged()
    {
        QueueUpdatePosition(resetCorrections: true);
    }
    void ThreadLoop()
    {
        while (true)
        {
            var settle = Interlocked.Read(ref settleAt);
            var timeout = settle == 0 ? Timeout.Infinite : (int)Math.Max(0, settle - Environment.TickCount64);
            var signaled = updateSignal.WaitOne(timeout);
            if (!isRegistered) break;
            var full = Interlocked.Exchange(ref fullUpdateRequested, 0) == 1;
            var settled = !signaled;
            if (settled)
            {
                // the host stopped moving: final update
                Interlocked.Exchange(ref settleAt, 0);
                consecutiveCorrections = 0;
                full = true;
            }
            try
            {
                if (showAfterHostDrag)
                {
                    Interlocked.Exchange(ref moveRequested, 0);
                    DoShowAfterHostDrag();
                    full = false;
                }
                else if (hiddenForHostDrag || self.IsDraggingHost)
                {
                    // nothing to do while hidden: put in place when shown again
                    Interlocked.Exchange(ref moveRequested, 0);
                    if (full) Interlocked.Exchange(ref fullUpdateRequested, 1);
                    continue;
                }
                if (Interlocked.Exchange(ref moveRequested, 0) == 1)
                    DoMove();
                if (full)
                    DoUpdatePosition();
                // resize the hidden tabs once the host is not being resized anymore
                if (pendingSiblingSync is { } sync && (settled || !ParentMovedRecently))
                {
                    pendingSiblingSync = null;
                    SyncHiddenSiblings(sync.From, sync.To);
                }
            }
            catch (Exception ex)
            {
                // an exception here would otherwise crash the whole process
                System.Diagnostics.Debug.WriteLine($"[RegisteredWindowController] update failed: {ex}");
            }
        }
    }
    /// <summary>
    /// Stops controlling the window. Safe to call multiple times.
    /// </summary>
    /// <param name="hideWindow">Whether to hide the window (when switching away from the tab)</param>
    internal void Unregister(bool hideWindow = true)
    {
        if (!isRegistered) return;
        isRegistered = false;
        Active.TryRemove(this, out _);
        // wake the thread so it can exit
        updateSignal.Set();
        if (Interlocked.Exchange(ref shownAfterHostDrag, null) is { } waiting)
            // the host waits for this window to be shown again: don't make it wait for nothing
            waiting();
        parentPositionChangedRegistration.Unregister();
        if (hideWindow && self.IsValid)
            // Not right away: when switching tabs, the window of the new tab is shown a few ms later.
            // Hiding first showed the empty host in between (a white flash).
            SchedulePendingHide(Parent.Handle, self);
        if (self.CurrentController == this)
            self.CurrentController = null;
    }
}
