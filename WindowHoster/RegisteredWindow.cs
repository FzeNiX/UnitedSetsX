using Get.EasyCSharp;
using WinWrapper.Windowing;
using System;
using System.ComponentModel;
using Microsoft.UI.Dispatching;
using WinWrapper;
using System.Threading.Tasks;
using System.Diagnostics;
using Process = WinWrapper.Process;
namespace WindowHoster;

public partial class RegisteredWindow : INotifyPropertyChanged
{
    /// <summary>
    /// Sets the global compatability mode check for No Moving mode
    /// </summary>
    public static Func<Window, bool>
        NoMovingModeChecker
    { get; set; } = (window) => window.Class.Name is "RAIL_WINDOW";
    /// <summary>
    /// Sets the global blacklist checker
    /// </summary>
    public static Func<Window, bool> BlacklistChecker { get; set; } = (window)
        => window.Class.Name is
            "Shell_TrayWnd" // Taskbar
            or "Progman" or "WorkerW" // Desktop
            or "WindowsDashboard" // I forget
            or "Windows.UI.Core.CoreWindow" // Quick Settings and Notification Center (other uwp apps should already be ApplicationFrameHost)
        ;
    /// <summary>
    /// Decides what happens when the user drags a hosted window by its own title bar
    /// (called when the drag starts, on the hook thread)
    /// </summary>
    public static Func<Window, UserMoveAction> UserMoveActionChooser { get; set; } = _ => UserMoveAction.Detach;
    /// <summary>
    /// Raised (on the hook thread) when the user presses the mouse button on the title bar (or border) of a hosted
    /// window: it may be about to be dragged (see <see cref="HostDragRequested"/>), or not (<see cref="UserMoveEnded"/>).
    /// The second argument is the host.
    /// </summary>
    public static event Action<RegisteredWindow, nint>? UserMoveStarting;
    /// <summary>
    /// Raised (on the hook thread) when the user releases a hosted window after <see cref="UserMoveStarting"/>, when
    /// it was not dragged with the host following it (that ends with <see cref="HostDragEnded"/>).
    /// </summary>
    public static event Action<RegisteredWindow, nint>? UserMoveEnded;
    /// <summary>
    /// Raised (on the hook thread) when the user starts dragging a hosted window by its own title bar and
    /// <see cref="UserMoveActionChooser"/> chose <see cref="UserMoveAction.MoveHost"/>. The application keeps dragging
    /// its window (it is not put back while that lasts): the host (second argument) should follow the mouse until
    /// <see cref="HostDragEnded"/>. The third argument is where the window was when the drag started (screen pixels):
    /// the application moves it by as much as the mouse moved since the button was pressed.
    /// </summary>
    public static event Action<RegisteredWindow, nint, System.Drawing.Point>? HostDragRequested;
    /// <summary>
    /// Raised (on the hook thread) when the drag started with <see cref="HostDragRequested"/> ends (the mouse button
    /// was released). The window may have been snapped or maximized by Windows (Aero Snap) and must be put back.
    /// </summary>
    public static event Action<RegisteredWindow, nint>? HostDragEnded;
    /// <summary>
    /// The user drags this window by its title bar and the host follows (see <see cref="HostDragRequested"/>)
    /// </summary>
    public bool IsDraggingHost => draggingHost;
    volatile bool draggingHost;
    nint draggingHostHandle;
    /// <summary>
    /// Raised (on the hook thread) when a hosted window gets minimized or maximized by itself,
    /// e.g. using its own minimize/maximize button, Win+Down or Win+Up. The hosted window is
    /// restored automatically; the handler typically minimizes/maximizes the host window instead.
    /// </summary>
    public static event Action<RegisteredWindow, HostedWindowStateRequest>? HostedWindowStateRequested;
    internal readonly WindowStylingState InitalStylingState;
    public Window Window { get; }
    [AutoNotifyProperty(SetVisibility = GeneratorVisibility.Private, OnChanged = nameof(CompatablityModeChanged))]
    CompatablityMode _CompatablityMode;

    private RegisteredWindow(Window WindowToHost, bool shouldBeHidden = false)
    {
        if (WindowToHost.IsMaximizedReliable() || WindowToHost.IsMinimizedReliable())
            WindowToHost.SendMessage(WindowMessages.SysCommand, /* SC_RESTORE */ 0xF120, 0);

        InitalStylingState = WindowStylingState.GetCurrentState(WindowToHost);
        // No show/hide animation while it is a tab: switching tabs hides one window and shows another, and the
        // fade animations made both visible (blended) for ~100 ms, which looked like a flicker.
        SetTransitionsDisabled(WindowToHost, true);

        Window = WindowToHost;

        CompatablityMode = CompatablityMode with { NoMoving = NoMovingModeChecker(WindowToHost) };

        var skipOwnProcess = Window.OwnerProcess.Id == Process.Current.Id;
        registeredWindowClosedEvent = WinEvents.Register(
            Window.Handle,
            WinEventTypes.ObjectDestroyed,
            skipOwnProcess,
            (_, _, idObject, idChild, _, _) =>
            {
                // EVENT_OBJECT_DESTROY is also raised when the caret/cursor/scrollbar of the window
                // is destroyed. Treating that as the window being closed made tabs disappear while
                // the (hidden) window was still alive.
                if (!WinEvents.IsWindowItself(idObject, idChild)) return;
                if (!IsValid) return;
                IsValid = false;
                Dispose();
                Closed?.Invoke();
            }
        );
        if (!CompatablityMode.NoMoving)
        {
            registeredPosSizeChangedEvent = WinEvents.Register(
                Window.Handle,
                WinEventTypes.PositionSizeChanged,
                skipOwnProcess,
                (_, _, idObject, idChild, _, _) =>
                {
                    if (!WinEvents.IsWindowItself(idObject, idChild)) return;
                    OnPositionChangedExternally();
                }
            );
            registeredMoveSizeStartEvent = WinEvents.Register(
                Window.Handle,
                WinEventTypes.WindowMovedStart,
                skipOwnProcess,
                (_, _, idObject, idChild, _, _) =>
                {
                    if (!WinEvents.IsWindowItself(idObject, idChild)) return;
                    if (CurrentController is not { } controller) return;
                    userMoveSizeStartBounds = Window.Bounds;
                    IsUserMovingOrResizing = true;
                    UserMoveStarting?.Invoke(this, controller.ParentHandle);
                }
            );
            registeredMoveSizeEndEvent = WinEvents.Register(
                Window.Handle,
                WinEventTypes.WindowMovedEnd,
                skipOwnProcess,
                (_, _, idObject, idChild, _, _) =>
                {
                    if (!WinEvents.IsWindowItself(idObject, idChild)) return;
                    if (draggingHost)
                    {
                        draggingHost = false;
                        HostDragEnded?.Invoke(this, draggingHostHandle);
                        return;
                    }
                    if (CurrentController is { } controller)
                        UserMoveEnded?.Invoke(this, controller.ParentHandle);
                    if (!IsUserMovingOrResizing) return;
                    IsUserMovingOrResizing = false;
                    // the user resized the window by its border (or dragged it while detaching is disabled):
                    // put it back to where it belongs
                    CurrentController?.QueueUpdatePosition(resetCorrections: true);
                }
            );
        }
        registeredWindowShownEvent = WinEvents.Register(
            Window.Handle,
            WinEventTypes.WindowShown,
            skipOwnProcess,
            (_, _, idObject, idChild, _, _) =>
            {
                // also raised when the caret is shown
                if (!WinEvents.IsWindowItself(idObject, idChild)) return;
                if (CurrentController is { IsHiddenForHostDrag: true, Updating: false } hidden)
                {
                    // shown by something else while the host is dragged (e.g. Windows maximized it)
                    hidden.HideAgainForHostDrag();
                    return;
                }
                if (CurrentController is null || !CurrentController.Updating)
                {
                    ShownByUser?.Invoke();
                }
            }
        );
        if (shouldBeHidden && IsValid)
            WindowToHost.IsVisible = false;
        Closed += delegate { BecomesInvalid?.Invoke(); };
        Detached += delegate { BecomesInvalid?.Invoke(); };
        Properties = new(this);
        AllRegistered[this] = 0;
    }
    WinEventsRegistrationParameters registeredWindowClosedEvent, registeredPosSizeChangedEvent,
        registeredWindowShownEvent, registeredMoveSizeStartEvent, registeredMoveSizeEndEvent;
    System.Drawing.Rectangle userMoveSizeStartBounds;
    /// <summary>
    /// Whether the user is currently in the move/size modal loop of the window (dragging its title bar or border)
    /// </summary>
    public bool IsUserMovingOrResizing { get; private set; }

    void OnPositionChangedExternally()
    {
        if (!IsValid) return;
        if (CurrentController is not { } controller) return;
        if (controller.Updating)
            // current controller is doing the update
            return;
        if (draggingHost || controller.IsHiddenForHostDrag)
            // being dragged by the user (the host follows), or hidden while the host is dragged:
            // put in place when the drag ends (restoring a maximized hidden window would show it)
            return;
        var bounds = Window.Bounds;
        if (bounds == controller.LatestRequestedRect)
            // same position
            return;
        if (Window.IsMinimizedReliable() || Window.IsMaximizedReliable())
        {
            // The hosted window was minimized/maximized directly (e.g. its own buttons, Win+Down/Up).
            // A hosted window can't do that on its own, so restore it and let the host decide.
            var request = Window.IsMinimizedReliable() ? HostedWindowStateRequest.Minimize : HostedWindowStateRequest.Maximize;
            Window.SendMessage(WindowMessages.SysCommand, /* SC_RESTORE */ 0xF120, 0);
            controller.QueueUpdatePosition(resetCorrections: true);
            HostedWindowStateRequested?.Invoke(this, request);
            return;
        }
        if (IsUserMovingOrResizing)
        {
            // Only when the user drags the window (title bar), not when they resize it by the border
            var start = userMoveSizeStartBounds;
            var isMove = bounds.Size == start.Size && bounds.Location != start.Location;
            if (isMove)
            {
                switch (UserMoveActionChooser(Window))
                {
                    case UserMoveAction.Detach:
                        IsUserMovingOrResizing = false;
                        controller.DispatcherQueue.TryEnqueue(Detach);
                        return;
                    case UserMoveAction.MoveHost:
                        IsUserMovingOrResizing = false;
                        // the application keeps dragging its window (hidden by the host soon), the host follows
                        draggingHost = true;
                        draggingHostHandle = controller.ParentHandle;
                        HostDragRequested?.Invoke(this, controller.ParentHandle, start.Location);
                        return;
                }
            }
            // otherwise wait for the user to finish (move size end will put it back)
            return;
        }
        // The window moved or resized itself (or another program moved it): put it back.
        controller.OnHostedWindowChangedItself();
    }

    void CompatablityModeChanged()
    {
        if (CompatablityMode.NoOwner)
        {
            var window = Window;
            window[WindowExStyles.TOOLWINDOW] = true;
        }
    }
    internal RegisteredWindowController? CurrentController;
    /// <summary>
    /// The last measured invisible resize borders (see <see cref="RegisteredWindowController"/>)
    /// </summary>
    internal InvisibleBorders? CachedInvisibleBorders;
    /// <summary>
    /// Every valid registered window (used to keep the hidden windows of a host sized like the visible one)
    /// </summary>
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<RegisteredWindow, byte> AllRegistered = new();
    /// <summary>
    /// The host window this window was last shown in
    /// </summary>
    internal nint LastHost;
    /// <summary>
    /// The area (screen pixels, without the invisible borders compensation) this window last covered in <see cref="LastHost"/>
    /// </summary>
    internal System.Drawing.Rectangle LastArea;

    internal void InternalUpdateParent(Window parent)
    {
        LastHost = parent.Handle;
        var window = Window;
        // A tabbed window has no taskbar button of its own: the host represents it.
        // (WS_EX_APPWINDOW forces a button even for owned windows. The original extended styles are
        // restored when the window is detached.)
        if (window[WindowExStyles.APPWINDOW])
            window[WindowExStyles.APPWINDOW] = false;
        window.Owner = parent;
        CompatablityMode = CompatablityMode with { NoOwner = Window.Owner != parent };
    }
    bool disposed;
    private void Dispose()
    {
        if (disposed) return;
        disposed = true;
        AllRegistered.TryRemove(this, out _);
        registeredWindowClosedEvent.Unregister();
        registeredPosSizeChangedEvent.Unregister();
        registeredWindowShownEvent.Unregister();
        registeredMoveSizeStartEvent.Unregister();
        registeredMoveSizeEndEvent.Unregister();
        IsValid = false;
    }
    /// <summary>
    /// Gets whether <see cref="RegisteredWindow"/> is still valid. <see cref="RegisteredWindow"/> is valid if the window
    /// is not detached or closed.
    /// </summary>
    public bool IsValid { get; internal set; } = true;
    /// <summary>
    /// Raises when the window becomes detached. This may cause by <see cref="Detach"/> or <see cref="DetachAsync"/> call.
    /// It may also caused by the user moving the window out of position or if the host moved too fast.
    /// </summary>
    public event Action? Detached;
    /// <summary>
    /// Raises when the window is closed.
    /// </summary>
    public event Action? Closed;
    /// <summary>
    /// Raises when the window becomes invalid.
    /// </summary>
    public event Action? BecomesInvalid;
    /// <summary>
    /// Raises when the window becomes visible either by user or programmatic action outside
    /// RegisteredWindow control.
    /// </summary>
    public event Action? ShownByUser;
    /// <inheritdoc cref="DetachAsync"/>
    public async void Detach() => await DetachAsync();
    /// <summary>
    /// Release control of the current window and reset all customizations. Do not close the window.
    /// </summary>
    public async Task DetachAsync()
    {
        if (!IsValid) return;
        var WindowToHost = Window;
        // stop following the host before restoring anything so the controller
        // doesn't move the window back while we are detaching it
        CurrentController?.Unregister(hideWindow: false);
        Dispose();
        try
        {
            WindowToHost.Region = InitalStylingState.Region;
            Properties.ActivateCrop = false;
            Properties.BorderlessWindow = false;
            WindowToHost.Owner = default;
            WindowToHost.Style = InitalStylingState.Style;
            // NoOwner compatibility mode adds WS_EX_TOOLWINDOW which hides the window from the taskbar
            // and Alt+Tab. Always restore the original extended styles.
            WindowToHost.ExStyle = InitalStylingState.ExStyles;
            SetTransitionsDisabled(WindowToHost, false);
            WindowToHost.IsResizable = InitalStylingState.IsResizable;
            WindowToHost.IsVisible = true;

            WindowToHost.Focus();
            WindowToHost.Redraw();
            WindowToHost.SetAsForegroundWindow();
        }
        catch (Exception ex)
        {
            // the window may have been closed in the mean time
            Debug.WriteLine($"[RegisteredWindow] Detach failed: {ex}");
        }
        await Task.Delay(100);
        try
        {
            WindowToHost.Redraw();
            WindowToHost.IsVisible = true;
        }
        catch { }
        Detached?.Invoke();
    }

    /// <summary>
    /// Creates a new <see cref="RegisteredWindowController"/>. Only one <see cref="RegisteredWindowController"/> may exists at a time per window.
    /// </summary>
    /// <param name="parentWindow">The parent window.</param>
    /// <param name="dispatcherQueue">The dispatcher queue. Used for defining event callback thread.</param>
    /// <returns>A new registered window controller</returns>
    /// <exception cref="InvalidOperationException">If <see cref="RegisteredWindowController"/> for the current window already exists and has not been unregistered.</exception>
    public RegisteredWindowController? GetController(Window parentWindow, DispatcherQueue dispatcherQueue)
    {
        if (!IsValid) return null;
        if (CurrentController is not null)
            throw new InvalidOperationException("You already have a controller. Please unregister that one to get a new controller");
        CurrentController = new(parentWindow, this, dispatcherQueue);
        return CurrentController;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>
    /// Registers a new window.
    /// </summary>
    /// <returns>A <see cref="RegisteredWindow"/> if the window is not part of the blacklist. Returns <c>null</c> otherwise.</returns>
    public static RegisteredWindow? Register(Window window, bool shouldBeHidden = false)
    {
        if (!window.IsValid) return null;
        if (BlacklistChecker(window)) return null;
        return new(window, shouldBeHidden);
    }
}
internal readonly record struct InvisibleBorders(int Left, int Top, int Right, int Bottom);
partial class RegisteredWindow
{
    static unsafe void SetTransitionsDisabled(Window window, bool disabled)
    {
        Windows.Win32.Foundation.BOOL value = disabled;
        Windows.Win32.PInvoke.DwmSetWindowAttribute((Windows.Win32.Foundation.HWND)window.Handle,
            Windows.Win32.Graphics.Dwm.DWMWINDOWATTRIBUTE.DWMWA_TRANSITIONS_FORCEDISABLED, &value, (uint)sizeof(Windows.Win32.Foundation.BOOL));
    }
}
/// <summary>
/// What happens when the user drags a hosted window by its own title bar
/// </summary>
public enum UserMoveAction
{
    /// <summary>The window leaves the host (it is detached and keeps being dragged)</summary>
    Detach,
    /// <summary>The window is put back where it belongs when the user releases it</summary>
    PutBack,
    /// <summary>The host is dragged instead (see <see cref="RegisteredWindow.HostDragRequested"/>)</summary>
    MoveHost
}
public enum HostedWindowStateRequest
{
    Minimize,
    Maximize
}
