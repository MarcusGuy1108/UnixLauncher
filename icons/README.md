# Icon overrides

UnixLauncher ships **no** Apple artwork. Every icon it shows can be replaced by dropping a
file into an icons folder. No code changes or rebuild are needed.

**Where:** either folder works. The first one wins.

1. `%AppData%\UnixLauncher\icons\`: your own icons. They survive updates. Open it from
   *Logo menu › Launcher Settings… › Open Icons Folder*.
2. `icons\` next to `UnixLauncher.exe`: icons bundled with a build (this folder).

**Format:** `.png` (recommended, with transparency) or `.ico`. For multi-size `.ico` files the
largest image is used. Use at least **256×256** so icons stay sharp when the dock magnifies
them. After adding files, click **Reload Icons** in Settings (or restart the launcher).

Without an override, the launcher falls back to the app's real Windows icon, or to a
Segoe Fluent / MDL2 glyph.

## System icons

| File name            | Where it appears                                   | Fallback               |
|----------------------|----------------------------------------------------|------------------------|
| `menu-logo`          | Far-left menu bar button (the "Apple menu" spot)   | Four-square logo       |
| `about-logo`         | *About This PC* window                             | Laptop glyph           |
| `launchpad`          | Launchpad item in the dock                         | Grid glyph             |
| `trash`              | Recycle Bin in the dock, empty                     | Windows Recycle Bin    |
| `trash-full`         | Recycle Bin in the dock, has items                 | Windows Recycle Bin    |
| `search`             | Menu bar, Spotlight, Launchpad search              | Magnifier glyph        |
| `control-centre`     | Menu bar Control Centre button                     | Menu glyph             |
| `wifi`               | Menu bar + Control Centre Wi-Fi tile               | Wi-Fi glyph            |
| `bluetooth`          | Control Centre Bluetooth tile                      | Bluetooth glyph        |
| `focus`              | Control Centre Focus tile + menu bar (when on)     | Moon glyph             |
| `dark-mode`          | Control Centre Dark Mode tile                      | ◐                      |
| `screen-mirroring`   | Control Centre Screen Mirroring tile               | Monitor glyph          |
| `battery`            | Menu bar battery (laptops)                         | Battery level glyph    |
| `battery-charging`   | Menu bar battery while charging                    | Charging glyph         |
| `media-previous`     | Control Centre Now Playing                         | ⏮ glyph               |
| `media-play`         | Control Centre Now Playing                         | ▶ glyph               |
| `media-pause`        | Control Centre Now Playing                         | ⏸ glyph               |
| `media-next`         | Control Centre Now Playing                         | ⏭ glyph               |

Example: `%AppData%\UnixLauncher\icons\menu-logo.png`

## App icons (dock, Launchpad, Spotlight)

Put these in an `apps` subfolder. Two names are tried, in this order:

1. **Exe name without `.exe`**, e.g. `apps\explorer.png`, `apps\chrome.png`, `apps\Code.png`
2. **Display name** as shown in the dock tooltip / Launchpad, e.g. `apps\Settings.png`,
   `apps\Calculator.png` (characters that aren't allowed in file names become `_`)

Store apps (Settings, Calculator, Photos…) have no exe of their own, so use their display name.

Example: to give File Explorer a Finder-style icon, save it as
`%AppData%\UnixLauncher\icons\apps\explorer.png`.

## Fonts

San Francisco can't be redistributed either. Set any installed font in
*Launcher Settings › Font family*. The default is `Segoe UI Variable Text, Segoe UI`.
