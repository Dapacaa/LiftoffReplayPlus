# LiftoffReplayPlus

Liftoff Replay Plus is an unofficial BepInEx mod for Liftoff 1.7.6 that adds tools for studying replays and flying multiplayer. It is not affiliated with Liftoff or its developers.

- **Replays** - race lines with speed heatmap, live deltas between pilots, stick input graphs, picture-in-picture views, freecam and orbit cameras, xray, and trimming.
- **Multiplayer** - picture-in-picture views and live input graphs for other pilots, freecam, gate splits with lap history, a lobby browser that can join when a slot opens, chat formatting, and workshop auto download.

Every control can be rebound.

A massive thank you to **H.K.** for helping bug test during development.

<p align="center">
  <img src="https://github.com/Dapacaa/LiftoffReplayPlus/releases/download/media/PlaybackUI2.gif" alt="Playback bar" width="760">
</p>

## Contents

- [Requirements](#requirements)
- [Installation](#installation)
- [User Interface](#user-interface)
- [Leaderboard Menu](#leaderboard-menu)
- [Replay Features](#replay-features)
  - [Race Lines, Deltas, and Name Tags](#race-lines-deltas-and-name-tags)
  - [Smooth Drone Trails](#smooth-drone-trails)
  - [Xray](#xray)
  - [Speed Heatmap](#speed-heatmap)
  - [Stick Graph](#stick-graph)
  - [Picture in Picture](#picture-in-picture)
  - [Replay Trim](#replay-trim)
  - [Camera Zoom](#camera-zoom)
  - [Replay Controls](#replay-controls)
- [Multiplayer Features](#multiplayer-features)
  - [Multiplayer Menu](#multiplayer-menu)
  - [Pilot PIP Windows and Live Input Graphs](#pilot-pip-windows-and-live-input-graphs)
  - [Multiplayer Cameras](#multiplayer-cameras)
  - [Gate Splits and Lap History](#gate-splits-and-lap-history)
  - [Lobby Browser and Lobby Watcher](#lobby-browser-and-lobby-watcher)
  - [Leaderboard Replays and Back to Lobby](#leaderboard-replays-and-back-to-lobby)
  - [Drone Switcher](#drone-switcher)
  - [Chat Tools](#chat-tools)
  - [Workshop Download](#workshop-download)
  - [Multiplayer Controls](#multiplayer-controls)
- [Control Bindings](#control-bindings)
- [Enabling / Disabling the Mod](#enabling--disabling-the-mod)
- [Building From Source](#building-from-source)
- [License](#license)

## Requirements

- Liftoff 1.7.6 (Windows)

## Installation

### New install (full release ZIP)

The release ZIP bundles everything needed: the BepInEx loader, Unity Doorstop, and the plugin itself.

1. Download the full ZIP (`LiftoffReplayPlus-<version>.zip`) from the [latest release](https://github.com/Dapacaa/LiftoffReplayPlus/releases/latest).
2. Find your Liftoff installation folder, the one that contains `Liftoff.exe`. In Steam, right-click Liftoff > Manage > Browse local files (by default `C:\Program Files (x86)\Steam\steamapps\common\Liftoff`).
3. Extract the ZIP into that folder, so `BepInEx\`, `winhttp.dll`, and `doorstop_config.ini` end up next to `Liftoff.exe`.
4. Start Liftoff. The mod is loaded automatically; no further setup is needed.

### Already have BepInEx installed?

Use the standalone `LiftoffReplayPlus.dll` instead:

1. Download `LiftoffReplayPlus.dll` from the [latest release](https://github.com/Dapacaa/LiftoffReplayPlus/releases/latest).
2. Place it in `<Liftoff>\BepInEx\plugins\`.
3. Start Liftoff.

## User Interface

The game's replay interface is replaced with the mod's own: the playback bar, the Stick Cam overlay, the Settings panel, Stick Graphs, PIP windows, and the Control Bindings window.

<p align="center">
  <img src="https://github.com/Dapacaa/LiftoffReplayPlus/releases/download/media/Screenshotui.png" alt="User interface" width="620">
</p>

- **Draggable** - every element, including the playback bar and the Stick Cam overlay, can be dragged anywhere on the screen.
- **Resizable** - the playback bar, Stick Cam overlay, input graphs, and PIP windows can be resized from their edges or corners.
- **Pinnable** - use the pin button to keep the playback bar (or an open window) on screen instead of fading away when the mouse is idle.
- **Persistent** - overlay positions and sizes, Dark Mode, Stick Cam, Drone Trails, PIP settings, key bindings, and whether the Settings panel is open are saved in the BepInEx config file and restored on the next launch. A few per-replay choices are not saved.

## Leaderboard Menu

The leaderboard screen includes a few quality-of-life improvements:

- **Track search** - type in the track selector to filter the track list by name, so you do not have to scroll through every track.
- **Drag to select pilots** - click and drag across the pilot rows to check or uncheck multiple pilot toggles at once. The game's limit of 5 selected pilots still applies.

<p align="center">
  <img src="https://github.com/Dapacaa/LiftoffReplayPlus/releases/download/media/Leaderboardmenusearch.gif" alt="Track search" width="420">
  <img src="https://github.com/Dapacaa/LiftoffReplayPlus/releases/download/media/Leaderboardmenudrag.gif" alt="Drag to select pilots" width="190">
</p>

## Replay Features

### Race Lines, Deltas, and Name Tags

Every selected pilot's flight path is drawn as a colored race line, so you can see exactly where each drone flew.

- **Race Lines** - `L` shows or hides all lines; the Line column in the Settings panel (gear icon) shows or hides individual pilots. Lines can be drawn smoothed or as recorded (`K`), and Show Lines Through Objects (`J`) keeps them visible behind terrain.
- **Delta Display** (`M`) - shows how far ahead or behind each drone is compared with the reference pilot, chosen in the Settings panel's Ref column. Matching uses drone position, path shape, direction, and timing, so it stays accurate around corners, crossings, and close passes.
- **Name Tags** (`N`) - an in-world name tag over each drone. In freecam or the orbit camera, click a name tag to spectate that pilot.

<p align="center">
  <img src="https://github.com/Dapacaa/LiftoffReplayPlus/releases/download/media/RacelinesNametags.gif" alt="Toggling race lines, deltas and name tags" width="420">
  <img src="https://github.com/Dapacaa/LiftoffReplayPlus/releases/download/media/RacelineDeltaControls2.png" alt="Race line controls settings" width="280">
</p>

### Smooth Drone Trails

Optional custom trails can be enabled in Settings -> Effects/UI.

- Uses smooth world-space line trails instead of Liftoff's jagged native trails.
- Each trail follows the pilot's race-line color and fades/tapers toward its older end.
- Trail history is capped and movement-sampled to keep the performance impact low.

### Xray

Xray (`X`) turns the track objects around the spectated drone into wireframes, so you can see the drone and its line through walls and terrain.

<p align="center">
  <img src="https://github.com/Dapacaa/LiftoffReplayPlus/releases/download/media/Xray.gif" alt="Xray showing race lines through terrain" width="420">
</p>

### Speed Heatmap

Color the race lines by speed, so a glance shows exactly where each pilot braked and where they carried the most speed around a corner. Slow sections are drawn red, transitioning through orange and yellow up to green as the drone speeds up (`H` toggles).

<p align="center">
  <img src="https://github.com/Dapacaa/LiftoffReplayPlus/releases/download/media/SpeedHeatmap.png" alt="Speed heatmap overlay" width="620">
</p>

### Stick Graph

The Stick Graph is a full-lap chart of a pilot's recorded stick inputs, useful for studying how a lap was flown, for example whether a time loss came from the throttle or a pitch error. Open it for the spectated pilot with `G`, or for any pilot from the Graph column in the Settings panel.

- **Channels** - switch between Throttle, Yaw, Roll, and Pitch from the dropdown in the graph's title bar.
- **Pilot selector** - a dropdown on the title bar switches which pilot the graph shows, without closing the window.
- **Multiple graphs** - open graphs for several pilots at once. The `+` button opens a copy of the current graph, for example to compare two channels of the same pilot side by side.
- **Speed heatmap** (striped button) - colors the trace by the drone's speed at each moment, on the same scale and colors as the race-line heatmap, so you can see what the sticks were doing where the drone slowed down.
- **Playhead** - a white cursor shows exactly where the current replay time is within the lap.
- **Draggable and resizable** - grab anywhere on the graph to move it, or drag its edges and corners to resize it. Use the pin button to keep it from fading out when the mouse is idle.

<p align="center">
  <img src="https://github.com/Dapacaa/LiftoffReplayPlus/releases/download/media/InputGraph.gif" alt="Stick graph showing stick inputs" width="420">
</p>

### Picture in Picture

Watch any pilot's FPV in its own window while the replay plays. Press `P` for the spectated pilot, or use the PIP column in the Settings panel.

- Move, resize, pin, and close each PIP window independently.
- Choose automatic or fixed 15 Hz, 30 Hz, or 60 Hz refresh rates.
- Choose the PIP render resolution and draw distance.

<p align="center">
  <img src="https://github.com/Dapacaa/LiftoffReplayPlus/releases/download/media/PIP.gif" alt="Picture-in-picture replay windows" width="620">
</p>

### Replay Trim

Trim the replay to focus on a specific section, for example studying one gate sequence over and over without the replay running past it. The trimmed region is highlighted orange on the slider and on the stick graphs.

- **Toggle** - the trim button on the playback bar turns trimming on and off.
- **Handles** - drag the orange flag handles on the slider to set the start and end of the trimmed region.

<p align="center">
  <img src="https://github.com/Dapacaa/LiftoffReplayPlus/releases/download/media/Trim.gif" alt="Trimming the replay with flag handles" width="420">
</p>

### Camera Zoom

`Z` toggles camera zoom in freecam or third-person view - the camera smoothly zooms in on the spectated drone.

- **Scroll wheel** while zoomed zooms further in or out, down to 2° FOV for close inspection.
- Press `Z` again to zoom back out.

<p align="center">
  <img src="https://github.com/Dapacaa/LiftoffReplayPlus/releases/download/media/Zoom.gif" alt="Camera zoom on the spectated drone" width="420">
</p>

### Replay Controls

| Control | Action |
| --- | --- |
| `K` | Toggle line smoothing |
| `R` | Restart the current replay |
| `X` | Toggle Xray |
| `Space` | Play/pause replay or restart a finished replay |
| `1` through `5` (or keypad `1`-`5`) | Spectate the corresponding pilot |
| Left / Right arrow | Step replay backward / forward |
| `,` / `.` | Decrease / increase playback speed |
| `N` | Toggle name tags |
| `L` | Toggle all race lines |
| `J` | Toggle Show Lines Through Objects |
| `M` | Toggle Delta Display |
| `H` | Toggle Speed Heatmap |
| `/` | Toggle replay loop |
| `W`, `A`, `S`, `D` | Hold to enter the free camera, then move it |
| `Shift` | Move the free camera faster |
| `Z` | Toggle camera zoom (scroll wheel zooms in/out further while zoomed) |
| `G` | Open the stick graph for the spectated pilot |
| `P` | Open a PIP window for the spectated pilot |
| Left-click a name tag | Spectate that pilot (in freecam or the orbit camera) |
| Mouse wheel | Change orbit distance |
| Right mouse button | Orbit from the native camera or look with the free camera |
| `V` | Toggle first person / third person (replaces the game's three-step cycle); from freecam, return to the drone camera; from the orbit camera, go to first person |
| Middle mouse button | Return to the drone camera |

All keys listed above are the defaults and can be rebound in-game (except the arrow keys and mouse actions, which are fixed). See [Control Bindings](#control-bindings).

## Multiplayer Features

Everything in this section works while you are in a multiplayer room, including when you are flying alone in it.

### Multiplayer Menu

Press `F2` in a multiplayer room to open the multiplayer menu. It is draggable and pinnable like the replay windows, and it remembers whether you left it open, so it comes back by itself the next time you join a room. It holds:

- **Pilots** - every pilot in the room, with buttons to show their name tag, stick cam, live input graph, and PIP window.
- **Drone** - the [Drone Switcher](#drone-switcher).
- **PiP Settings** - refresh rate, resolution, draw distance, names/delta inside PIP windows, and External Monitor PiP.
- **Display** - Live Input Graph, Dark Mode, Names above Drones, and Gate Splits.
- **Workshop** - the room's workshop track, see [Workshop Download](#workshop-download).
- **Lobby Browser** - opens the [Lobby Browser](#lobby-browser-and-lobby-watcher).
- **Keyboard button** (title bar) - opens the [Control Bindings](#control-bindings) window.

<p align="center">
  <img src="https://github.com/Dapacaa/LiftoffReplayPlus/releases/download/media/MPSettingsUI.png" alt="Multiplayer menu" width="300">
</p>

### Pilot PIP Windows and Live Input Graphs

- **PIP windows** - watch any other pilot's FPV in its own window while you fly or spectate. Each window has a pilot dropdown in its title bar and can be moved, resized, pinned, and closed independently.
- **Live Input Graph** - a running chart of a pilot's stick inputs. The history survives resets and respawns, and the gap where no input was received is drawn as a red trace. If the pilot leaves, the graph switches to the next available pilot.
- **Docking** - graphs can be docked inside a PIP window.
- **External Monitor PiP** (Windows only) - shows a PIP window in its own desktop window, so it can sit on a second monitor.
- **Names above Drones** (`N`) - in-world name tags over every pilot's drone.

<p align="center">
  <img src="https://github.com/Dapacaa/LiftoffReplayPlus/releases/download/media/MPPilotWindows.png" alt="Pilot PIP window, live input graph and stick cams" width="420">
  <img src="https://github.com/Dapacaa/LiftoffReplayPlus/releases/download/media/Docking.png" alt="Live input graph and stick cams docked inside a PIP window" width="330">
</p>

### Multiplayer Cameras

- **Freecam** (`E`) - fly a free camera around the track. Your drone's controls are paused while it is on. Move with `W`, `A`, `S`, `D`, look with the right mouse button, and hold `Shift` to go faster. Press `E` again to return.
- **Zoom on a pilot** (`Z`) - the camera zooms in on a pilot from where you are. `1`-`8` switch pilots and the scroll wheel refines the zoom.
- **Spectate orbit** - while spectating another pilot in third person, right-drag to orbit around their drone and use the mouse wheel to change the distance. Middle-click returns to the game's chase view.

<!-- screenshot: multiplayer freecam / zoom -->

### Gate Splits and Lap History

Turn on **Gate Splits** in the multiplayer menu to show a per-gate split panel while racing.

- **Segments or Cumulative** - show gate-to-gate segment times or running lap times, each with the delta to your best.
- **Lap History** - opens a window with a graph of every lap, compared against your best lap or as absolute lap times. Laps can be edited or deleted, and the best can be cleared.
- Laps are saved between sessions.

<!-- screenshot: gate splits panel and lap history -->

### Lobby Browser and Lobby Watcher

Open the **Lobby Browser** from the multiplayer menu to see the public rooms with their host, track, environment, game mode, and player count, and join one directly.

- **Watch lobbies** - select one or more full lobbies to watch.
- **Alert on free slot** - shows a prompt with a Join button when a watched lobby gets a free slot.
- **Auto-join selected** - joins a watched lobby automatically as soon as a slot opens.

<p align="center">
  <img src="https://github.com/Dapacaa/LiftoffReplayPlus/releases/download/media/LobbyViewer.png" alt="Lobby browser" width="620">
</p>

### Leaderboard Replays and Back to Lobby

- **Replays from the in-game leaderboard** - in the multiplayer leaderboard popup, pick a pilot's run and load its replay directly, without leaving the room. Each row also shows the date of the run.
- **Back to Lobby** - while watching a replay from a room, the Back to Lobby button returns you to the room without leaving it, even if the host has changed track in the meantime.

### Drone Switcher

Switch your drone from the multiplayer menu: pick it from a searchable list of your drones, or give any drone its own hotkey with the key button next to it.

### Chat Tools

- **Select and copy** - drag across chat messages to select them, then press `Ctrl+C` or right-click and choose Copy. `Ctrl+Shift+C` copies the whole chat.
- **Text formatting** - the `T` button on the chat window opens a format bar: bold, italic, smaller/larger text, clear formatting, and a color picker with four saved color presets. Formatting counts toward the chat's character limit.

<!-- screenshot: chat format bar -->

### Workshop Download

The multiplayer menu shows the workshop track (and race) the room is using, with a Copy Link button for its Steam workshop page.

- **Download** subscribes to and downloads it from the Steam workshop.
- **Auto Download** does that automatically whenever the room's track changes. Items you already have installed and up to date are skipped.

### Multiplayer Controls

| Control | Action |
| --- | --- |
| `F2` | Open/close the multiplayer menu |
| `E` | Toggle multiplayer freecam |
| `W`, `A`, `S`, `D` | Move the freecam (`Shift` for faster) |
| `Z` | Zoom on a pilot; `1`-`8` switch pilots, scroll wheel refines |
| `N` | Toggle Names above Drones |
| `Ctrl+1` through `Ctrl+5` | Open a PIP window for that pilot |
| `V` | Toggle first person / third person; from freecam or zoom, return to your drone |
| Right mouse button | Look with the freecam, or orbit while spectating in third person |
| Middle mouse button | Return to the drone camera |

Live Input Graph has no default key; give it one in [Control Bindings](#control-bindings).

## Control Bindings

Open the Control Bindings window from the keyboard icon on the replay playback bar or in the multiplayer menu's title bar. Click the button next to any action and press the key you want (Escape cancels). Reset Defaults restores the stock bindings, and changes are saved across sessions.

The default keys avoid Liftoff's own keyboard shortcuts. If you bind a mod action onto a key Liftoff also uses, the mod action wins and Liftoff ignores that press.

## Enabling / Disabling the Mod

While mods are loaded, Liftoff does not post your multiplayer times on some maps. To turn the mod (and BepInEx) off without uninstalling:

1. Close Liftoff.
2. In the Liftoff installation folder, rename the `BepInEx` folder to something else, for example `BepInEx_Disabled`.
3. Start Liftoff. It now runs without any mods.
4. To re-enable, close Liftoff and rename the folder back to `BepInEx`.

No game files are modified; this only hides the mod loader from the game.

## Building From Source

The plugin compiles against the game's own DLLs and BepInEx's, which are not part of this repository. You need:

- Windows with Liftoff installed, and BepInEx installed into it (the [full release ZIP](#new-install-full-release-zip) does that).
- The [.NET SDK](https://dotnet.microsoft.com/download) and the [.NET Framework 4.7.2 Developer Pack](https://dotnet.microsoft.com/download/dotnet-framework/net472).

From the repository folder, build with `LiftoffDir` set to your Liftoff folder (the one that contains `Liftoff.exe`):

```powershell
dotnet build -c Release -p:LiftoffDir="C:\Program Files (x86)\Steam\steamapps\common\Liftoff"
```

The plugin is written to `bin\Release\net472\LiftoffReplayPlus.dll`; copy it to `<Liftoff>\BepInEx\plugins\` to use it. If the build stops with "... was not found", `LiftoffDir` does not point at a folder containing `Liftoff_Data\Managed` and `BepInEx\core`.

## License

Copyright (c) 2026 Dapacaa. Released under the [Mozilla Public License 2.0](LICENSE): you can use, modify, and redistribute the mod, but changes to its source files must be shared under the same license. Versions 1.4.0 and earlier were released under the MIT License.
