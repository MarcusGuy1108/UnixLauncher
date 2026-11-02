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
| **Finder** | A macOS-style file browser, first in the dock. Sidebar (Favourites, **This PC**, drives, **Recycle Bin**), back / forward, **Icons** and **List** views (sortable Name / Date Modified / Size / Kind columns), search in the current folder and below, path bar and status bar. **Space** for Quick Look (pictures, text, or a summary card). New Folder, Rename, Move to Recycle Bin, Get Info (Windows Properties), Open With, copy / cut / paste (works with Explorer's clipboard), drag and drop in, out and onto sidebar places. Copies and moves use Windows' own progress and "replace or skip" dialogs. **This PC** lists every drive with its free space, and the path bar starts there. **Recycle Bin** opens inside Finder, with Put Back, Delete Immediately and Empty; drag files onto it in the sidebar to trash them, and the dock's Recycle Bin opens it too. Its menus show in the menu bar when it's in front, and also when the desktop is, as on macOS. Shortcuts: Ctrl+N new window, Ctrl+Shift+N new folder, F2 rename, Delete, Alt+Enter Get Info, Alt+←/→/↑ back / forward / enclosing folder, Ctrl+1/2 views, Ctrl+F search. |
| **Window buttons** | The launcher's own windows have real traffic lights. Other apps get working round close / minimise / zoom buttons on the top-left of the focused window. Where the app already has something in that corner (a tab, its icon, its title), they appear in a glass pill when you point at the corner, and pointing at a background window's corner shows its buttons too. Windows' own buttons are taken off the title bar natively (the window's system menu is switched off, so Windows draws no buttons; snapping and Win+↑ still work) and put back when you quit or switch this off. Apps that draw their own buttons (browsers, Discord, Office, Terminal) keep theirs. Buttons a window doesn't have show grey. |
| **Animations** | Panels fade and grow in and fade out. The launcher's windows zoom open, fade when closed and shrink towards the dock when minimised. Dock icons magnify and settle back, and bounce until the app's window appears. Launchpad zooms in and out, and banners slide in and out. Menus are translucent with a real blur, and once one menu is open, pointing at another menu bar title opens it, as on a Mac. All of it follows Windows' *Animation effects* setting (off = no motion, like macOS "Reduce motion"). |
| **Keyboard** | Optional **Alt as ⌘**: Alt sits where ⌘ is on a Mac keyboard, so ⌘C/⌘V/⌘Q/⌘W/⌘←… work from muscle memory. See below. |
| **Hot corners** | Settings › Desktop & Dock: Mission Control (Task View), Desktop, Launchpad, Notification Centre, Put Display to Sleep, Lock Screen. |
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
| **Open folders in Finder** | Folders, drives, **This PC** and the **Recycle Bin** open in Finder wherever they're opened from: the desktop, Explorer, "Open folder" in other apps. **Win+E** opens Finder too. It adds a per-user "open" action to folders, drives, This PC and the Recycle Bin in the registry, the way other Explorer replacements do. Switching it off removes only that action. |
| **Windows key opens Launchpad** | A tap of the Windows key opens Launchpad instead of Start. Win + other keys (Win+D, Win+L, Win+Shift+S…) keep working. |
| **Win+S opens Spotlight** | Win+S and Win+Q open Spotlight instead of Windows Search. |
| **Win+A opens Control Centre** | Instead of Windows' Quick Settings. |
| **Win+N opens Notification Centre** | Instead of Windows' notification panel. |

The keyboard options use a low-level keyboard hook. It only reacts to those Windows-key combinations, and
it injects an unused key code so the Start menu doesn't also pop up. If you move `UnixLauncher.exe`, the
folder option repairs its registry entry the next time the launcher starts.

## Using it alongside a Mac

**Alt as ⌘ Command** (Settings › Keyboard, off by default). The key next to the space bar does what ⌘ does:

| On your Mac | Here | What happens |
|---|---|---|
| ⌘C ⌘V ⌘X ⌘Z ⌘A ⌘S ⌘F ⌘T ⌘N ⌘W ⌘P ⌘R ⌘L ⌘1–9 ⌘= ⌘− | Alt + same key | Apps receive Ctrl + that key |
| ⌘⇧Z | Alt+Shift+Z | Redo (Ctrl+Y) |
| ⌘Q / ⌘H / ⌘M / ⌘` | Alt+Q / H / M / ` | Quit the app / hide it / minimise / next window of the app |
| ⌘← ⌘→ / ⌘↑ ⌘↓ / ⌘⌫ | Alt + arrows / Backspace | Line start / end, top / bottom, delete to line start (hold Shift to select) |
| ⌘[ ⌘] | Alt+[ / Alt+] | Back / forward |
| ⌥← ⌥→ / ⌥⌫ ⌥⌦ | Win + arrows / Backspace / Delete | Word left / right, delete a word (hold Shift to select). The Windows key sits where ⌥ is; Win+← / Win+→ don't snap windows while this is on |
| ⌘⇧3 / ⌘⇧4 / ⌘⇧5 | Alt+Shift+3 / 4 / 5 | Whole-screen screenshot (saved to Pictures › Screenshots) / pick an area |
| ⌘Space / ⌘Tab | Alt+Space / Alt+Tab | Spotlight / Windows' own app switcher, in the same place |

How it behaves:

- **Ctrl still works**, so Windows habits keep working too.
- **A tap of Alt on its own does nothing**, as with ⌘.
- **Alt combinations that aren't Mac shortcuts pass through unchanged:** Alt+Tab, Alt+F4, Alt+Enter (full screen in
  games and players), and Ctrl+Alt+anything.
- **Only the left Alt is used.** Right Alt (AltGr) types characters on many layouts.
- **Finder follows macOS Finder:** Return renames, ⌘↓ opens, ⌘⌫ trashes, ⌘⇧G goes to a folder, ⌘I gets info, ⌘⇧. shows hidden files, ⌘, opens Launcher Settings.
- **Menus write shortcuts the macOS way** (⇧⌘N, ⌘⌫) while this is on.
- **San Francisco font:** install SF Pro (free from developer.apple.com/fonts) and the launcher uses it automatically.

**Gaming.** Whenever a full-screen game (exclusive or borderless) is in front, the launcher steps aside:

- The menu bar, dock and window buttons hide.
- Alt-as-⌘, the Windows-key options and hot corners all pause, so every key reaches the game exactly as normal.

For games you play in a window, add the exe under *Desktop & Dock › Leave these apps alone*. Do the same for
apps that use Alt+click (Blender, Photoshop…).

**Windows settings worth matching to your Mac** (these are Windows' own, so the launcher doesn't change them):

- Settings › Bluetooth & devices › Touchpad › Three-finger gestures: swipe up = Task View (Mission Control), and set
  left/right to *Switch desktops*.
- Settings › Bluetooth & devices › Touchpad › Scroll & zoom: *Down motion scrolls up* = natural scrolling.
- Settings › Accessibility › Visual effects: *Animation effects* controls the launcher's animations too.

## Uninstalling

Any of these does the same thing:

- **Windows Settings › Apps › Installed apps › UnixLauncher › Uninstall**. UnixLauncher lists itself there
  (per user, no admin rights needed).
- **Launcher Settings › General › Uninstall…**
- `UnixLauncher.exe --uninstall`. Add `--quiet` to skip the questions, e.g. from a script or from
  Task Manager › Run new task if the desktop is broken.

It quits the running launcher, then puts back everything it changed:

| What it changed | How it's put back |
|---|---|
| Folders / drives / This PC / Recycle Bin open in Finder | Its "open" actions are removed, so File Explorer opens them again |
| UnixLauncher as the Windows shell | The per-user `Shell` value is removed (only if it points at UnixLauncher), and Explorer is started straight away so you get your desktop and taskbar back without signing out |
| Hidden taskbar | Shown again, with your original auto-hide setting restored |
| Windows key and Win+E / S / A / N | Go back to Windows as soon as the launcher has quit (the keyboard hook only lives in its process) |
| Start at sign-in | Removed |
| Screen space for the menu bar and dock | Released |
| Apps list entry, session marker | Removed |

It then asks whether to delete your settings, custom icons and logs (`%AppData%\UnixLauncher`), and finally
deletes its own program files. Only files with UnixLauncher's names are deleted; the folder is removed only
if that leaves it empty. Dark Mode is left as you last set it, since that's a Windows setting you chose.

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
- With Alt as ⌘ on, Left Alt+Shift no longer switches keyboard language (use Win+Space, as on a Mac
  ⌃Space), and Alt+click becomes a plain click unless the app is in the "leave alone" list.
- The Windows key is where ⌥ Option sits on a Mac, but it isn't remapped: Win+arrows still snap windows rather than
  jumping by word. Ctrl+arrows jump by word, as on Windows.
- Other apps' windows minimise, maximise and open with Windows' own animations; only the launcher's own windows
  get the macOS ones. Alt+Tab is Windows' switcher (it shows windows rather than macOS's row of app icons).
- Mirrored app menus only work for apps with a classic Windows menu bar (e.g. Notepad++, Registry
  Editor, older tools). Browsers, Store apps, Electron apps and Office draw their own, so they just
  get the Window menu. Those menus use Windows' own (dark) menu style.
- Finder takes over folders only with *Open folders in Finder* switched on. A few things still open File
  Explorer: apps that start `explorer.exe` directly, "Open file location" / "Show in folder" in other apps
  (Windows handles that inside Explorer), and other shell places such as Network and Control Panel.
- Hiding Windows' title bar buttons switches off the window's system menu, so Alt+Space and right-clicking the title
  bar show no menu in those apps while it's on. Apps that draw their own buttons keep them.
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
