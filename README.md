<div align="center">

# Dynamic Island for Windows

**An iPhone-style Dynamic Island that replaces the Windows 11 taskbar.**

A small black pill sits at the top of your screen with the time, date and what's playing.
Rest your mouse on it to see your open apps. Click it, and it opens into a full control centre for
your apps, music, clipboard, files, system stats and power options.

<a href="https://github.com/samround73-cpu/dynamic-island-windows/releases/latest/download/DynamicIsland.exe"><b>⬇ Download DynamicIsland.exe</b></a> &nbsp;·&nbsp; <a href="../../releases">All releases</a> &nbsp;·&nbsp; <a href="docs/USER-GUIDE.md">User guide</a>

<img src="docs/images/hero.png" alt="The island opened on the Media tab, glowing with a colour taken from the album artwork" width="880">

</div>

---

## Why?

The Windows taskbar takes a strip off the bottom of every screen and is mostly just a row of icons.
Dynamic Island replaces it with something that stays out of the way until you need it:

- **It's small.** Most of the time it's a tidy pill showing the time and date.
- **It moves aside for your work.** When the app underneath is maximised, the island slides up out of sight.
  Rest the mouse at the top edge to bring it back.
- **It's all in one place.** Apps, music, Wi-Fi and Bluetooth, clipboard history, a file shelf, system stats and power
  options all live in one panel, one click away.

## A quick tour

### The pill and the hover tray

The pill shows the time, the date, your battery (on laptops) and little music bars when something's playing.
Rest your mouse on it and it grows into a tray of your open apps. The app you're using is shown at the top with
its window title, and below that are the song controls.

<img src="docs/images/pill-and-tray.png" alt="The compact pill, and the hover tray with open apps and music controls" width="880">

### Apps

Every installed app in one list, with **Starred**, **Recent** and **Applications** sections. Press **Ctrl+Space**
from anywhere, type a name and press Enter (initials work too: "vsc" finds Visual Studio Code). Windows Settings
pages come up too, so "bluetooth" or "display" jumps straight there. Apps that are already open are marked,
and clicking them brings back the window you already have instead of starting a new copy.

<img src="docs/images/apps.png" alt="The Apps tab with search, open-app chips and the app list" width="880">

### Controls

Switches for Wi-Fi, Bluetooth, sound, microphone and dark mode, with a compact music player and volume
alongside, plus shortcuts to Windows Settings, Quick settings, notifications and tray icons.

<img src="docs/images/controls.png" alt="The Controls tab with switches, music player and volume" width="880">

### Media

A full player that takes on the colour of the album artwork, with song position, shuffle, repeat, volume and a
speaker/headphone picker. Works with Spotify, browsers, Media Player and anything else that shows up in Windows'
own media controls.

<img src="docs/images/media.png" alt="The Media tab" width="880">

### Stats

Live rings for CPU, memory, disk and network, and the five open apps using the most CPU and memory.

<img src="docs/images/stats.png" alt="The Stats tab with usage rings and the heaviest open apps" width="880">

### Power

Up time, Windows Update status, power and open apps at a glance. Lock, sleep and sign out are one click
away, while restart and shut down use **slide to confirm** sliders, so they can't happen by accident.
If Windows has updates waiting, they're installed on the way, just like the Start menu does.

<img src="docs/images/power.png" alt="The Power tab with slide-to-restart and slide-to-shut-down" width="880">

### …and more

- **Today:** a greeting, the weather (now and the next few hours) and a month calendar.
- **Clipboard:** your recent copies, with search, filters (text, images, links) and pinning.
  Copies from password managers are skipped.
- **Shelf:** drop files onto the island to keep them handy, then drag them out into any folder, email or chat.
- **Pop-ups:** the island briefly pops out for screenshots (with a preview), finished downloads, volume changes,
  new songs, Bluetooth devices, USB drives and charging. New screenshots and downloads also land on the Shelf.

### Make it yours

Choose from four island styles and any accent colour, and show, hide or reorder the tabs.

<img src="docs/images/styles.png" alt="The four island styles: Black, Glass, Glow and Outline" width="880">

---

## Getting started

> **Requirements:** Windows 11. The Glass style needs Windows 11 22H2 or later.
> Windows 10 (version 2004 or later) may work but hasn't been tested.

1. **Download [`DynamicIsland.exe`](https://github.com/samround73-cpu/dynamic-island-windows/releases/latest/download/DynamicIsland.exe)** from the [latest release](../../releases/latest).
   It's a single file with everything included, so there's nothing else to install.
2. **Put it somewhere it can stay**, for example a `Dynamic Island` folder in your Documents, and double-click it.
3. The first time, Windows may show a blue **"Windows protected your PC"** box, because the app isn't signed
   by a publisher. Click **More info → Run anyway**.

It adds itself to the Start menu the first time it runs. To have it start automatically, right-click the pill and
choose **Start with Windows**. It then opens when you sign in, and again when you reconnect to the PC (handy for
Shadow or Remote Desktop) or unlock it.

**Updates install themselves.** The island checks this page for new releases and, when you're not using it,
swaps in the new version and restarts in a second or two. You can turn that off, or check by hand, in
Settings › About.

<details>
<summary><b>Building it yourself from the source code</b></summary>

1. Install the free [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (`Build.cmd` opens the download page if it's missing).
2. Download this repository (green **Code** button → **Download ZIP**) and unzip it.
3. Double-click **`Build.cmd`**. It builds the app into an `App` folder and offers to start it.

New versions are built automatically by GitHub: pushing a tag like `v0.30.0` builds the exe and publishes a release
(see `.github/workflows/release.yml`).
</details>

### Getting your taskbar back

- **To quit:** right-click the pill → **Exit Dynamic Island**. Your normal taskbar comes straight back.
- **If the app was force-closed** (for example from Task Manager) and the taskbar is still hidden, run
  **`Restore taskbar (emergency).cmd`** (it's in the zip on the release page, and in this repository).
  It can also stop the island starting automatically.

## Good to know

- **Privacy.** Nothing is collected or sent anywhere. The only things fetched from the internet are new versions
  (from this repository's releases) and the weather on the Today tab.
  The weather comes from [Open-Meteo](https://open-meteo.com), and your town name is looked up with
  [BigDataCloud](https://www.bigdatacloud.com), using only your approximate location.
  Settings and pinned items are kept on your PC in `%AppData%\DynamicIsland`.
- **Limitations.** It works on the main monitor only. Some apps don't allow other programs to jump around in a song
  (Spotify, for one), so scrubbing may not work with them. The battery only shows on PCs that have one.
- **Updating and removing.** There's no installer, and updates install themselves from the releases here
  (Settings › About › Updates). Settings › About shows the version and has **Remove from this PC…**, which clears the startup
  entry, Start menu shortcut and settings. The [user guide](docs/USER-GUIDE.md#what-it-puts-on-your-pc) lists every file it creates.
- **Apps running as administrator** (like Task Manager) can be switched to, but Windows doesn't let normal apps
  close them. The island tells you when that happens.
- **Problems?** Details are saved to `%AppData%\DynamicIsland\errors.log` (Settings › About › Open error log folder).
  Attach that file if you [open an issue](../../issues).

The full **[user guide](docs/USER-GUIDE.md)** covers every tab, setting and keyboard shortcut.

## Built with

- C# and WPF on .NET 8, with Windows' own APIs for media, Wi-Fi/Bluetooth, the taskbar and window tracking
- [NAudio](https://github.com/naudio/NAudio) for volume and audio devices
- [Tabler Icons](https://tabler.io/icons) (MIT) for the line-style app icons, downloaded automatically on first build

## Licence

[MIT](LICENSE): free to use, change and share.

---

<sub>Inspired by the iPhone's Dynamic Island. This is an independent project, not affiliated with or endorsed by
Apple or Microsoft.</sub>
