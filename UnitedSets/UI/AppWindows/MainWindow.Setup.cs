using System.Runtime.CompilerServices;
using Cube.UI.Icons;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using UnitedSets.Apps;
using UnitedSets.Cells.Data;
using UnitedSets.Configurations;
using UnitedSets.Mvvm.Services;
using UnitedSets.Tabs;
using WindowHoster;
using WinWrapper.Windowing;
using Icon = WinWrapper.Icon;
using Keyboard = WinWrapper.Input.Keyboard;

namespace UnitedSets.UI.AppWindows;

partial class MainWindow : NativeHelperWindow
{
    /// <summary>
    /// Whether this is the first set window of the app (it restores the previous session, handles the command line...)
    /// </summary>
    readonly bool IsFirstSetWindow;
    public MainWindow()
    {
        IsFirstSetWindow = UnitedSetsApp.Current.SetWindows.Count is 0;
        Tabs.CollectionChanged += OnTabsCollectionChanged;
        UnitedSetsApp.Current.RegisterUnitedSetsWindow(this);

        Init();
        // created now: added to the tree during the first drag, it was not shown in time
        EnsureHostDragOverlay();
        taskbarTabs = new(this);
        Closed += (_, _) => IsClosed = true;
        SelectedTabProp.Watch(tab =>
        {
            // updates that arrive after the window was closed (e.g. its last tab was closed) must not touch it:
            // that threw 0x800710DD and crashed the app
            if (IsClosed) return;
            taskbarTabs.OnSelectionChanged(previousSelectedTab);
            previousSelectedTab = tab;
            tab?.IsFlashing = false;
            UpdateHomeVisibility();
            UpdateTitle();
        });

        SetupBasicWindow();

        ((OverlappedPresenter)AppWindow.Presenter).SetBorderAndTitleBar(true, true);


        SetupCustomization();

        SetupEvent();
        SetupUIThreadLoopTimer(out timer);

        // SetupTaskbarMode();

        // Implementation
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void SetupBasicWindow()
        {
            Title = Constants.AppName;
            ExtendsContentIntoTitleBar = true;
            // Compact (32px) caption buttons: the tab strip is 36px high. With the tall (48px) option the
            // caption buttons were partly covered by the tabbed window below the strip.
            AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
            //SetTitleBar(CustomDragRegion);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void SetupEvent()
        {
            ((FrameworkElement)Content).FirstLoadedEv(() =>
            {
                LeftInset = GridLengthFromPixelInt(AppWindow.TitleBar.LeftInset);
                RightInset = GridLengthFromPixelInt(AppWindow.TitleBar.RightInset);
            });
            AppWindow.Closing += OnWindowClosing;
            ClosingFlyout.CloseRequest += RequestCloseAsync;
            Activated += FirstRun;
            TabBase.OnUpdateStatusLoopComplete += OnDifferentThreadLoop;
            if (IsFirstSetWindow)
                // static event: only subscribe once (the handler finds the right window)
                EmptyCell.ValidDrop += OnDropOverCell;
            WindowMessageMonitor.WindowMessageReceived += OnWindowMessageReceived;
            // Receive HSHELL_FLASH so tabs can show that their window wants attention
            ShellHookMessage = (WindowMessages)Windows.Win32.PInvoke.RegisterWindowMessage("SHELLHOOK");
            Windows.Win32.PInvoke.RegisterShellHookWindow(new(Win32Window.Handle));
        }
        // dragging a tabbed window by its own title bar
        RegisteredWindow.UserMoveActionChooser = _ => UnitedSetsApp.Current.Settings.TabbedWindowDrag.Value switch
        {
            TabbedWindowDragBehaviors.TakeOut => UserMoveAction.Detach,
            TabbedWindowDragBehaviors.PutBack => UserMoveAction.PutBack,
            // moves the whole set, unless the grouping key is held: then the window comes out of the set
            // (and can be dropped onto another set)
            _ => Services.WindowDragToGroupService.IsTakeOutKeyDown() ? UserMoveAction.Detach : UserMoveAction.MoveHost
        };
    }

    WindowMessages ShellHookMessage;
    readonly TaskbarTabs taskbarTabs;
    TabBase? previousSelectedTab;
    int uiTicks;


    private void UpdateTitle()
    {
        if (IsClosed) return;
        var prefix = Configuration.TitlePrefix;
        if (SelectedTab is { } tab)
            prefix = Tabs.Count > 1
                ? $"{prefix} {tab.Title} (+{Tabs.Count - 1} Tabs) - "
                : $"{prefix} {tab.Title} - ";

        Title = $"{prefix}{Constants.AppName}";
    }

    public USConfig Configuration => UnitedSetsApp.Current.Configuration.MainConfiguration;

    [Event(typeof(TypedEventHandler<object, WindowActivatedEventArgs>))]
    void FirstRun()
    {
        Activated -= FirstRun;
        var icoFile = Path.IsPathRooted(Configuration.TaskbarIco) ? Configuration.TaskbarIco : Path.Combine(USConfig.RootLocation, Configuration.TaskbarIco!);
        try
        {
            var icon = Icon.Load(icoFile);
            Win32Window.SmallIcon = Win32Window.LargeIcon = icon;
        }
        catch (Exception ex)
        {
            Helpers.CrashLog.Write("Loading taskbar icon", ex);
        }

        if (Keyboard.IsShiftDown)
            Win32Window.SetAppId($"UnitedSets {Win32Window.Handle}");
        if (IsFirstSetWindow)
            UnitedSetsApp.Current.HandleCLICmds();
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private partial void SetupUIThreadLoopTimer(out DispatcherQueueTimer timer)
    {
        timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(500);
        timer.Tick += OnUIThreadTimerLoop;
        timer.Start();
    }
}
