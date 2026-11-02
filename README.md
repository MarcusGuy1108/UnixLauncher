# UnixLauncher

A macOS-style desktop for Windows 10/11: a top menu bar, a movable dock, Control Centre,
Notification Centre, Spotlight, Launchpad, and red/yellow/green window buttons.
Built with C# / WPF on .NET 10.

> **Status:** this was written and compile-checked on Linux, but hasn't been run on Windows
> yet. Expect some rough edges on first launch. If something misbehaves, check
> `%AppData%\UnixLauncher\error.log`.

## Features

| Feature | What it does |
|---|---|
| **Menu bar** | Logo menu in Apple-menu order (About This PC, System Settings, App Store, Force Quit, Sleep / Restart / Shut Down, Lock, Log Out), with the launcher's own items at the end. Then the active app in bold, with About / Hide / Hide Others / Show All / Quit. Then **the app's own File / Edit / View… menus**, mirrored like the macOS global menu bar (classic Windows menu bars only), and a **Window** menu. On the right: battery, Wi-Fi, Spotlight, Control Centre, and a clock that opens Notification Centre. |
| **Dock** | Pinned apps, folders and files, plus a dot under running apps. Running apps that aren't pinned get their own section, and the Recycle Bin sits at the end. **Left / Bottom / Right**, icon size, magnification and auto-hide. Drag icons to reorder them. Drop `.exe`, `.lnk` or folders from Explorer onto it to pin them. Clicking a running app brings it forward, as on macOS. Clicking a folder (e.g. Downloads) opens a **stack** of its newest items. Right-click an icon for its windows, Keep / Remove, Show in File Explorer, Hide, Quit and Force Quit. Right-click the shelf for dock options. |
| **Finder** | A macOS-style file browser, first in the dock. Sidebar (Favourites, drives, Recycle Bin), back / forward, **Icons** and **List** views (sortable Name / Date Modified / Size / Kind columns), search in the current folder and below, path bar and status bar. **Space** for Quick Look (pictures, text, or a summary card). New Folder, Rename, Move to Recycle Bin, Get Info (Windows Properties), Open With, copy / cut / paste (works with Explorer's clipboard), drag and drop in, out and onto sidebar places. Copies and moves use Windows' own progress and "replace or skip" dialogs. Its menus show in the menu bar when it's in front, and also when the desktop is, as on macOS. Shortcuts: Ctrl+N new window, Ctrl+Shift+N new folder, F2 rename, Delete, Alt+Enter Get Info, Alt+←/→/↑ back / forward / enclosing folder, Ctrl+1/2 views, Ctrl+F search. |
| **Window buttons** | The launcher's own windows have real traffic lights. A small overlay also adds working close / minimise / zoom dots to the title bar of whichever app is focused. Nothing is injected into other apps, and their own buttons stay put. |
| **Control Centre** | macOS 26 layout of glass pills and circles: Wi-Fi (with the network name), Bluetooth, Nearby sharing, Now Playing, Focus, Task View, Screen Mirroring (Cast), display brightness (laptops), volume with sound output, and Dark Mode / Calculator / Timer / Screenshot buttons. **Edit Controls** opens its settings. Right-click a toggle to open its Windows settings page. |
| **Notification Centre** | Mirrors every app's Windows notifications, with dismiss and Clear All, plus a calendar widget. |
| **Spotlight** | **Alt+Space**. Search apps, do quick maths (`12*7`), search files, search the web. |
| **Launchpad** | Full-screen grid of every installed app (desktop and Store), with search. Right-click › Add to Dock. |
| **Full-screen apps** | The menu bar and dock hide while a full-screen app or game is in front. |

## Settings

The Settings window (logo menu › Launcher Settings…) is laid out like macOS System Settings:

- **General:** start at sign-in, and where settings and logs live.
- **Appearance:** menu bar background on or off (off gives the clear macOS 26 bar), font, and custom icons.
- **Control Centre:** for Wi-Fi, Bluetooth, Focus, Sound, Now Playing, Battery and Spotlight, choose
  *Show in Menu Bar*, *Show When Active* or *Don't Show in Menu Bar*.
- **Desktop & Dock:** dock size, magnification, position and auto-hide, plus the traffic-light overlay.
- **Windows Integration:** replace parts of Windows (below), hide the taskbar, and shell mode.

## Replacing Windows features

All off by default, and each can be switched back at any time, under **Settings › Windows Integration**:

| Option | What changes |
|---|---|
| **Open folders in Finder** | Folders opened anywhere (desktop, Explorer, "Open folder" in other apps, drives) and **Win+E** open in Finder. It adds a per-user "open" action to folders and drives in the registry, the way other Explorer replacements do; switching it off removes only that action. |
| **Windows key opens Launchpad** | A tap of the Windows key opens Launchpad instead of Start. Win + other keys (Win+D, Win+L, Win+Shift+S…) keep working. |
| **Win+S opens Spotlight** | Win+S and Win+Q open Spotlight instead of Windows Search. |
| **Win+A opens Control Centre** | Instead of Windows' Quick Settings. |
| **Win+N opens Notification Centre** | Instead of Windows' notification panel. |

The keyboard options use a low-level keyboard hook. It only reacts to those Windows-key combinations, and
it injects an unused key code so the Start menu doesn't also pop up. If you move `UnixLauncher.exe`, the
folder option repairs its registry entry the next time the launcher starts.

## Requirements

- Windows 10 version 2004 (build 19041) or later, or Windows 11
- To build: [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). To run a framework-dependent build: .NET 10 Desktop Runtime.

## Build and run

```bash
dotnet run --project src/UnixLauncher
```

Publish a single `.exe` into a folder you'll keep. Do this before using shell mode, because
the registry points at that exact path:

```bash
dotnet publish src/UnixLauncher -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o C:\Tools\UnixLauncher
```

Tests (pure logic, runs on any OS):

```bash
dotnet test
```

## Two modes

**On top of Explorer (default).** Explorer keeps running, so File Explorer, Windows toasts and
everything else keep working. The launcher sets the Windows taskbar to auto-hide and hides it,
then puts it back exactly as it was when you quit. Your original auto-hide setting is saved to
disk, so it survives a crash too. Turn on *Start UnixLauncher when I sign in* to keep it.

**As the Windows shell.** *Launcher Settings › Use UnixLauncher as my Windows shell* sets a
per-user `Shell` value under `HKCU\Software\Microsoft\Windows NT\CurrentVersion\Winlogon`.
From the next sign-in, Explorer's desktop and taskbar don't start, and UnixLauncher:

- runs your startup apps (Run keys and Startup folders, respecting Task Manager's enabled /
  disabled switches)
- reserves screen space itself
- shows notification banners, since Explorer isn't there to do it

File Explorer windows still open normally from the dock.

**If shell mode goes wrong:** press **Ctrl+Alt+Del › Task Manager › Run new task**, type
`explorer.exe`, then untick the option in Launcher Settings. Or delete the `Shell` value under
`HKCU\Software\Microsoft\Windows NT\CurrentVersion\Winlogon` with `regedit` and sign out.

## Notifications

On first launch Windows asks whether UnixLauncher may read your notifications. If you said no,
turn it on in *Settings › Privacy & security › Notifications*. Some Windows builds only allow
this for apps with package identity (MSIX). If the Notification Centre shows "isn't available
to this build", the next step is packaging the app as MSIX (not done yet).

## Icons and branding

No Apple artwork or fonts are included. Every icon can be replaced by dropping PNG/ICO files
into `%AppData%\UnixLauncher\icons\`. [`icons/README.md`](icons/README.md) lists every file
name (menu logo, Launchpad, Recycle Bin, Control Centre glyphs, per-app icons…). The UI font is
a setting, so you can use any font you have installed.

## Settings file

`%AppData%\UnixLauncher\settings.json`. Most things are in the Settings window. Also worth
knowing:

- `OverlayExclude`: exe names whose title bar the traffic-light overlay should leave alone.
  Browsers put tabs at the top-left, so e.g. `["chrome.exe", "msedge.exe"]`.
- `Pinned`: the dock, in order.

## Known limitations

- **Primary monitor only** for the menu bar and dock.
- **System tray icons** live on the Windows taskbar, which is hidden. Use
  *Logo menu › Show Windows Taskbar* to reach them in on-top mode. In shell mode there is no
  tray yet.
- The traffic-light overlay assumes a standard ~32 px title bar. Apps with custom title bars
  may look off; add them to `OverlayExclude`.
- Focus only silences the launcher's own banners. Windows' own Do Not Disturb is a right-click
  on the Focus tile.
- The Win key still opens the Windows Start menu (on-top mode).
- Spotlight's hotkey is fixed to Alt+Space, so it clashes with PowerToys Run if you use that.
- Mirrored app menus only work for apps with a classic Windows menu bar (e.g. Notepad++, Registry
  Editor, older tools). Browsers, Store apps, Electron apps and Office draw their own, so they just
  get the Window menu. Those menus use Windows' own (dark) menu style.
- Finder takes over folders only with *Open folders in Finder* switched on. Apps that start `explorer.exe`
  directly, and special places such as This PC or Control Panel, still open File Explorer.
  Finder has no column or gallery view, no tabs, and no tags. Its search walks the disk (up to 1000 results)
  rather than using the Windows Search index. "Show in File Explorer" in its right-click menu reaches
  everything else Windows offers (Send to, Compress, etc.).
- The launcher UI is dark-only.
- Frosted glass behind the dock, panels and launcher windows needs **Windows 11 22H2 or later**,
  which provides the blur and rounded corners. On older Windows the menu bar and Launchpad are still
  blurred, but the dock and panels are solid.

## Project layout

```
src/UnixLauncher/
  App.xaml(.cs)        startup, single instance, mode detection
  Theme.xaml           colours, menus, traffic lights, window chrome, sliders
  Native.cs            Win32 interop
  Logic.cs             pure logic (magnification, app matching, search, calculator, command lines)
  Settings.cs          settings.json
  Sys.cs               launching, power, taskbar, shell registration, startup apps, Recycle Bin
  WorkArea.cs          reserving screen edges (AppBar / work area)
  AppIndex.cs          installed apps (shell:AppsFolder)
  Icons.cs             icon overrides + shell icons
  RunningApps.cs       open windows, foreground/move hooks
  SystemControls.cs    volume, brightness, radios, dark mode, media, battery
  Notifications.cs     Windows notification listener
  Views/               MenuBar, DockWindow, ControlCentre, NotificationCentre, Spotlight,
                       Launchpad, TrafficLightOverlay, SettingsWindow, AboutWindow
tests/UnixLauncher.Tests/   xUnit tests for Logic.cs
icons/                 icon override guide (copied next to the exe)
```
