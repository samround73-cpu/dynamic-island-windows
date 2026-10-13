# User guide

Everything Dynamic Island for Windows can do, and how to use it. (Written for version 0.31.)

An iPhone-style "Dynamic Island" that replaces the Windows taskbar.

## Getting it running

**The easy way:** download **`DynamicIsland.exe`** from the
[latest release](https://github.com/samround73-cpu/dynamic-island-windows/releases/latest), put it somewhere it can
stay (for example a `Dynamic Island` folder in your Documents) and double-click it. Nothing else to install.
It adds itself to the Start menu the first time it runs.

Windows may show a blue "Windows protected your PC" box the first time, because the app isn't signed by a
publisher. Click **More info → Run anyway**.

**Updates are automatic:** the island checks GitHub for a new version a minute after it starts and then every few
hours. When there is one, it downloads it, checks it arrived intact, and swaps it in the next time you're not using the
island (it restarts in a second or two and pops up "Updated to …"). You can turn this off, or check by hand, in
**Settings › About › Updates**; with it off, it just tells you when a new version is out.

**Building it yourself** (from the source code):
1. Install the free **.NET 8 SDK** from Microsoft. `Build.cmd` opens the download page for you if it's missing
   (pick **SDK 8.0 → Windows → x64**).
2. Double-click **`Build.cmd`**. It builds the app into the `App` folder and offers to start it.

## What it puts on your PC

There's no installer: the app is just `DynamicIsland.exe`, wherever you saved it. It updates itself by replacing
that file (the previous version is briefly kept as `DynamicIsland.old.exe` and deleted on the next start). Starting a copy doesn't install or uninstall anything, and
**Settings › About** shows which version is running.

While it runs, it creates:

| What | Where |
|---|---|
| Settings, starred apps, pinned clips and shelf items | `%AppData%\DynamicIsland\settings.json` |
| Error log (only if something goes wrong) | `%AppData%\DynamicIsland\errors.log` |
| Your own taskbar setting, while the island is running | `%AppData%\DynamicIsland\taskbar-state.txt` |
| Start menu shortcut | `%AppData%\Microsoft\Windows\Start Menu\Programs\Dynamic Island.lnk` |
| Start with Windows (only if you turn it on) | A scheduled task called **DynamicIsland** |
| Files .NET unpacks to run the single-file exe | `%Temp%\.net\DynamicIsland` |

To remove it all, use **Settings › About › Remove from this PC…**, then delete `DynamicIsland.exe`.

## How it behaves

- **Normally** the island sits at the top-centre of the screen, showing the time, date and (on a laptop) battery.
- **Rest the mouse on it** (it waits until the mouse actually pauses, so passing over it doesn't count) and it grows into a small tray of your open apps. Click one to bring it
  back (even if it's minimised — those are shown dimmed), click the app in front to minimise it, or hover
  an icon for a live preview (hover a preview for its ✕ to close that window). While music is playing, the tray
  also shows the song with back / play-pause / next. Click the island itself (not an icon) to open everything.
- **When the app under the island is maximised**, the island slides out of the way so it doesn't cover the app.
  Rest the mouse at the top edge, around where the island lives, for about a third of a second to bring it back.
  It then stays for at least 3 seconds, and tucks away once the mouse has been away from it for a little over
  a second. You can also drag files up there and pause to bring it down and drop them on the shelf.
  If nothing maximised is under it (desktop showing, or windows that aren't maximised), it simply stays put.
- **Full-screen games and videos** hide it completely.
- **After you reconnect to the PC** (e.g. reopening a Shadow or Remote Desktop session) or the screen scaling changes,
  the island quietly restarts itself a few seconds later, because Windows can otherwise leave it drawn as a black box.

## The tabs (click the island to open)

| Tab | What's there |
|---|---|
| **Today** | Three cards side by side: the clock (with week and day of the year), the **weather** (now, high/low and the next five hours) and a month calendar — use the arrows (or scroll over it) to change month |
| **Controls** | A list of switches (Wi-Fi, Bluetooth, Sound, Microphone, Dark mode) — click a row to flip it, click its arrow for more options. Beside it: a compact player, the volume bar, and buttons for Windows Settings, Quick settings, Notifications and tray icons |
| **Apps** | A small row of your open apps at the top for quick switching, then every installed app as a list, with **Starred**, **Recent** and **Applications** sections and a search box. Apps that are already open show a gold dot and "Open" — clicking them brings that window back rather than starting a new copy. Click the star to favourite an app (the apps pinned to your old taskbar are starred for you the first time). Middle-click or right-click for a new window |
| **Clipboard** | Recent copies under Pinned / Today / Yesterday, with search, filters (All, Text, Images, Links) and a preview with details. **Enter** pastes into the app you were using, **Ctrl+C** copies, **Ctrl+P** pins, **Del** removes, **Tab** changes filter, double-click pastes. Only pinned text is saved; the rest is forgotten when the island closes. Password-manager copies are skipped |
| **Shelf** | Files you've dropped onto the island. Drag them back out into any folder, email or app (or use **Drag all**). Double-click to open; right-click for Show in folder / Copy path / Remove. The shelf only remembers where files are — nothing is copied or moved |
| **Media** | A big player that glows with a colour picked from the album artwork. Song position, shuffle and repeat (when the app supports them), volume, and a picker for speakers/headphones |
| **Stats** | Rings for CPU, memory, disk and download speed, plus the five heaviest open apps with their CPU and memory use |
| **Power** | Up time, Windows Update status, battery/power and how many apps are open at a glance. Lock, Sleep and Sign out buttons, and slide-to-confirm sliders for Restart and Shut down (they install waiting updates, like the Start menu does). Plus quick launch buttons |

**Searching for apps:** press **Ctrl+Space** anywhere (press it again to close), or with the island open just start typing — it jumps to the Apps tab and searches as you type. The shortcut can be turned off in Settings › Behaviour.
Use **↑ ↓** to choose, **Enter** to open, **Esc** to clear the search. Initials work too ("vsc" finds Visual Studio Code).

Other things you can do:

| You want to… | Do this |
|---|---|
| Close the island | Click anywhere else, or press **Esc** |
| Change the volume | Scroll over the pill |
| Search apps | Start typing, or click the magnifying glass top-left of the open island |
| Start automatically with Windows | Right-click the pill → **Start with Windows** (or Settings › Behaviour). It opens when you sign in, and again when you reconnect (e.g. to a Shadow or Remote Desktop PC) or unlock, if it isn't already running |
| Quit | Right-click the pill → **Exit Dynamic Island** (your normal taskbar comes back) |

**Settings** — the gear at the top-left of the open island. Pick a section on the left:
- **Appearance:** accent colour, island style (Black; Glass — real frosted blur of what's behind, as a panel hanging from the top edge; Glow — an accent halo; or Outline), line icons, battery on the island, music bars.
- **Behaviour:** the hover tray and its delay, tucking away over maximised apps, device pop-ups, the Ctrl+Space shortcut, and Start with Windows (it tells you if Windows has switched it off, with a button to fix it).
- **Weather:** your town (or leave it empty to use Windows' location) and °C or °F.
- **About:** the version, the error-log folder, the project page, and Quit.

**Pop-ups** also appear for Bluetooth devices connecting/disconnecting, USB drives being plugged in or removed,
and sound switching to different speakers or headphones.

**If something goes wrong**, the details are saved to `%AppData%\DynamicIsland\errors.log` (Settings › About › Open error log folder) — include that file when you report a problem.

**If the taskbar ever goes missing** (e.g. the app was force-closed from Task Manager), double-click
`Restore taskbar (emergency).cmd`. It also offers to stop the island starting automatically.

**Apps running as administrator** (Task Manager, for example) can be switched to, but Windows doesn't let normal apps
close them — the island tells you when that happens.

## Known limitations

- Primary monitor only.
- Pinned apps come from your normal taskbar's pinned shortcuts; pinned Store apps (like Settings) may not show.
- Bluetooth battery levels aren't shown yet.
- The battery only appears on PCs that have one — not on desktops or most virtual machines.
- Some websites don't report song length, so the progress bar only appears when it's available.
- A desktop PC without Wi-Fi shows a "Network" tile instead, which opens network settings.

## Line icons

The first time `Build.cmd` runs it downloads the free Tabler Icons set (MIT licence, tabler.io/icons) into
`src\Assets`, used for the matching line-style app icons. Turn them off in Settings › Line icons. If the download
fails, the island just uses normal app icons.

## How the code is organised (`src` folder)

| File | What it does |
|---|---|
| `MainWindow.xaml` | The look: the pill, the pop-out and the tabs |
| `AppCatalog.cs` | Reads every installed app (the Start menu's "All apps" list) with icons |
| `ClipboardHistory.cs` | Remembers what you've copied for the Clipboard tab |
| `AppRow.cs` | One row in the Apps list |
| `MainWindow.Extras.cs` | The Clipboard and Shelf tabs, weather, device pop-ups and Settings |
| `Shelf.cs` | The files on your shelf |
| `WeatherService.cs` | Weather from Open-Meteo (free, no account) |
| `DeviceService.cs` | Spots Bluetooth devices connecting |
| `IslandSettings.cs` | Remembers your settings, starred/recent apps, pinned clips and shelf (in `%AppData%\DynamicIsland\settings.json`) |
| `MainWindow.xaml.cs` | The core behaviour: the island's shapes and animations, auto-hide, tabs |
| `MainWindow.Apps.cs` | The hover tray of open apps, the Apps list, search and smooth scrolling |
| `MainWindow.Panels.cs` | The Today, Controls, Clipboard, Media, Stats and Power tabs |
| `SlideToConfirm.cs` | The "slide to restart / shut down" sliders on the Power tab |
| `ArtColour.cs` | Picks the main colour out of album artwork for the Media tab |
| `UpdateStatus.cs` | Reads whether Windows Update has updates waiting or needs a restart |
| `ToggleTile.cs` | The switch rows on the Controls tab |
| `WindowTracker.cs` | Finds open apps, their names and icons, and spots maximised and full-screen apps |
| `TaskbarManager.cs` | Hides the real taskbar, keeps it hidden, and always puts it back |
| `PreviewWindow.cs` | The live window previews on hover |
| `Backdrops.cs` | The frosted-glass blur and the glow halo, drawn on separate layers just behind the island |
| `MediaService.cs` | "Now playing" info, play/pause/skip and song position |
| `VolumeService.cs` | Volume, speakers name and microphone mute |
| `ConnectivityService.cs` | Wi-Fi and Bluetooth switches and the current network name |
| `SystemStats.cs` | CPU, memory, network, disk and top-apps numbers for the Stats tab |
| `SystemActions.cs` | Dark mode, lock/sleep/restart/shut down, opening apps and settings |
| `StartupManager.cs` | The "Start with Windows" setting |
| `LineIcons.cs` | Picks a line icon and colour for each app |
| `Native.cs` | The direct calls into Windows that everything above relies on |
