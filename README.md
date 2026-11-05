# ![United Sets X](UnitedSets/Assets/Square44x44Logo.targetsize-32.png) United Sets X

**Group any windows into tabs on Windows — hold <kbd>Shift</kbd> and drag one window onto another.**

United Sets X turns windows of any applications into tabs of a single window, like a web browser:
two Chrome profiles, a terminal next to its editor, all your documents of a project... It runs quietly in the
notification area, groups windows when you drag them onto each other, and puts every group on a single taskbar
button. It is free and open source.

<!-- Add a screenshot here, e.g. ![United Sets X](ReadmeAssets/screenshot.png) -->

## Features

- **Drag to group** — hold <kbd>Shift</kbd> (or Ctrl / Alt / Tab, see the settings) while dragging a window by its
  title bar, and drop it onto another window: both become tabs of a new *set* that takes the place of that window.
  Drop it onto an existing set to add a tab. A highlight shows where the window will go.
- **Browser-like tabs** — compact tab bar, reorder tabs, drag a tab to another set to move it, or drag it out to
  release the window. Tabbed windows use the full width of the set.
- **Several sets at once** — each set is its own window, with its own tabs.
- **One taskbar button per set** — the button shows the icons of the grouped apps combined, and hovering it
  shows a thumbnail for each tab (click to switch, close button to close the tab).
- **Runs in the background** — starting United Sets X only adds an icon to the notification area. Closing a set
  never quits the app; use *Exit* in the tray menu. Optional *Start with Windows*.
- **Hotkeys** — <kbd>Win</kbd> + <kbd>`</kbd> / <kbd>Win</kbd> + <kbd>Shift</kbd> + <kbd>`</kbd> switch to the next /
  previous tab of the active set.
- **Behaves like one window** — minimizing or maximizing a tabbed window minimizes or maximizes its set; tabbed
  windows follow the set smoothly, including between monitors with different scaling.
- **Splitable tabs** — show several windows side by side in one tab.
- **Native Windows 11 look** — Mica / Acrylic backdrops and controls that follow your Windows accent color.
- **Safe** — if United Sets X crashes or is killed, windows that were hidden in background tabs are shown again on
  the next start. Releasing a set gives every window back as it was.

## Getting started

1. Download `UnitedSetsX-<version>-win-x64.zip` from the [latest release](https://github.com/FzeNiX/UnitedSetsX/releases/latest).
2. Extract it anywhere (for example `%LocalAppData%\UnitedSetsX`) and run `UnitedSets.exe`.
   Nothing else needs to be installed: .NET and the Windows App SDK are included.
3. United Sets X appears in the notification area. Hold <kbd>Shift</kbd> and drag a window onto another one.

Requirements: Windows 10 version 1809 or later, or Windows 11 (x64).

## Usage

| Action | How |
| --- | --- |
| Group two windows | Hold <kbd>Shift</kbd> and drag a window by its title bar onto another window |
| Add a window to a set | Hold <kbd>Shift</kbd> and drag it onto the set, or use **+** › *Add Window* |
| Move a tab to another set | Drag the tab onto the other set's tab bar |
| Release a window | Drag its tab out of the tab bar, or drag the window by its own title bar |
| Next / previous tab | <kbd>Win</kbd> + <kbd>`</kbd> / <kbd>Win</kbd> + <kbd>Shift</kbd> + <kbd>`</kbd> |
| Tab options (title, crop, borderless...) | Right click or double click the tab |
| New empty set | Left click the tray icon, or **+** › *New Set Window* |
| Settings / Exit | Right click the tray icon |

Everything (grouping key, what closing a set does, taskbar thumbnails, start with Windows, layout, backdrop...) can be
changed in the settings. Settings are stored in `Documents\UnitedSets\default.json`.

## Known limitations

- Windows of applications running as administrator can only be grouped when United Sets X also runs as administrator.
- Some UWP / packaged apps and apps that draw their own window frame may not behave perfectly in a tab.
- Applications that are not DPI aware are stretched by Windows when they are moved to a monitor with a different scaling.
- Windows only (it relies on Win32 window management).

## Troubleshooting

Errors are logged to `%AppData%\UnitedSets\error.log`. Please attach it when you
[report an issue](https://github.com/FzeNiX/UnitedSetsX/issues).

## Building from source

Requirements: the .NET 10 SDK and the Windows SDK 10.0.26100
(Visual Studio Installer, or `winget install Microsoft.WindowsSDK.10.0.26100`).

```
dotnet build UnitedSets/UnitedSets.csproj -c DebugUnpackaged -p:Platform=x64
```

The `DebugUnpackaged` build runs without installing anything else than the Windows App SDK 1.8 runtime:
`UnitedSets/bin/x64/DebugUnpackaged/net10.0-windows10.0.26100.0/UnitedSets.exe`.

To make a release package, see [RELEASING.md](RELEASING.md).

## License

United Sets X is released under the [MIT license](LICENSE). It is based on the code of United Sets by Get,
FireCubeStudios and contributors, also MIT licensed.
