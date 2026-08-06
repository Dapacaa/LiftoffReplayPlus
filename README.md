# LiftoffReplayPlus

Liftoff Replay Plus is an unofficial BepInEx mod for Liftoff 1.7.5 that adds playback controls, race-line visualization, delta comparison, name tags, orbit camera, freecam, rebindable control bindings, Xray, Speed Heatmap, Dark Mode, and various quality of life changes. It is not affiliated with Liftoff or its developers.

A massive thank you to <span style="color: purple;">H.K.</span> for helping bug test during development.

<p align="center">
  <img src="https://github.com/Dapacaa/LiftoffReplayPlus/releases/download/media/PlaybackUI.gif" alt="Playback bar" width="760">
</p>

## Requirements

- Liftoff 1.7.5

## Installation

### New install (full release ZIP)

The release ZIP bundles everything needed: the BepInEx loader, Unity Doorstop, and the plugin itself.

1. Download `LiftoffReplayPlus.zip` from the [latest release](https://github.com/Dapacaa/LiftoffReplayPlus/releases/latest).
2. Find your Liftoff installation folder - the folder that contains `Liftoff.exe`. In Steam, right-click Liftoff -> Manage -> Browse local files. (By default: `C:\Program Files (x86)\Steam\steamapps\common\Liftoff`.)
3. Extract the ZIP into that folder, so `BepInEx\`, `winhttp.dll`, and `doorstop_config.ini` end up next to `Liftoff.exe`.
4. Start Liftoff. The mod is loaded automatically; no further setup is needed.

### Already have BepInEx installed?

Use the standalone `LiftoffReplayPlus.dll` instead:

1. Download `LiftoffReplayPlus.dll` from the [latest release](https://github.com/Dapacaa/LiftoffReplayPlus/releases/latest).
2. Place it in `<Liftoff>\BepInEx\plugins\`.
3. Start Liftoff.

## User Interface

The default Liftoff UI has been removed and fully revamped. It consists of the Playback bar, the Stick Cam overlay, the Settings panel, and the Control Bindings window.

<p align="center">
  <img src="https://github.com/Dapacaa/LiftoffReplayPlus/releases/download/media/screenshot-ui.png" alt="User interface" width="430">
  <img src="https://github.com/Dapacaa/LiftoffReplayPlus/releases/download/media/Settingsmenu.gif" alt="Settings panel" width="300">
</p>

- **Fully draggable** - every element, including the playback bar and the Stick Cam overlay, can be dragged anywhere on the screen.
- **Resizable** - the size of both the playback bar and the Stick Cam overlay can be changed from the Settings panel.
- **Pinnable** - use the pin button to keep the playback bar (or an open window) on screen instead of fading away when the mouse is idle.
- **Persistent** - overlay positions and sizes, Dark Mode, Stick Cam, key bindings, and the Settings panel's open/closed state are saved through BepInEx preferences and restored on the next launch. Certain settings are not persisted.

## Leaderboard Menu

The leaderboard screen includes a few quality-of-life improvements:

- **Track search** - type in the track selector to filter the track list by name, so you do not have to scroll through every track.
- **Drag to select pilots** - click and drag across the pilot rows to check or uncheck multiple pilot toggles at once. The game's limit of 5 selected pilots still applies.

<p align="center">
  <img src="https://github.com/Dapacaa/LiftoffReplayPlus/releases/download/media/Leaderboardmenusearch.gif" alt="Track search" width="420">
  <img src="https://github.com/Dapacaa/LiftoffReplayPlus/releases/download/media/Leaderboardmenudrag.gif" alt="Drag to select pilots" width="190">
</p>

## Useful Features

### Race Lines, Deltas, and Name Tags

Every selected pilot's flight path is drawn as a colored race line, so you can rewatch a lap and study where each drone raced. Toggle lines with `L` to see all pilots at once, or use the Settings panel (settings gear icon) to show only specific pilot lines.

- **Race Lines** - each pilot has their own colored line, with a Smooth or Original visual and a Show Lines Through Objects option in the Settings.
- **Delta Display** (`M`) - shows how far ahead or behind each drone is versus the reference pilot (marked next to the target button). Delta values are updated live for the finished lap.
- **Name Tags** (`N`) - each drone carries an in-world name tag, so it is easy to tell who is who, even while flying around.

<p align="center">
  <img src="https://github.com/Dapacaa/LiftoffReplayPlus/releases/download/media/RacelinesNametags.gif" alt="Toggling race lines, deltas and name tags" width="420">
  <img src="https://github.com/Dapacaa/LiftoffReplayPlus/releases/download/media/RacelineDeltaControls.png" alt="Race line controls settings" width="280">
</p>

### Xray

View the path of the currently spectated pilot drone through objects with a wireframe Xray (`X`) display.

<p align="center">
  <img src="https://github.com/Dapacaa/LiftoffReplayPlus/releases/download/media/Xray.gif" alt="Xray showing race lines through terrain" width="420">
</p>

### Speed Heatmap

Color the race lines by speed, so a glance shows exactly where each pilot braked and where they carried the most speed around a corner. Slow sections are drawn red, transitioning through orange and yellow up to green as the drone speeds up (`H` toggles).

<p align="center">
  <img src="https://github.com/Dapacaa/LiftoffReplayPlus/releases/download/media/SpeedHeatmap.png" alt="Speed heatmap overlay" width="620">
</p>

## Replay Controls

| Control | Action |
| --- | --- |
| `K` | Toggle line smoothing |
| `R` | Restart the current replay |
| `X` | Toggle Xray |
| `Space` | Play/pause replay or restart a finished replay |
| `1` through `5` | Spectate the corresponding pilot |
| Left / Right arrow | Step replay backward / forward |
| `,` / `.` | Decrease / increase playback speed |
| `N` | Toggle name tags |
| `L` | Toggle all race lines |
| `J` | Toggle Show Lines Through Objects |
| `M` | Toggle Delta Display |
| `H` | Toggle Speed Heatmap |
| `/` | Toggle replay loop |
| `W`, `A`, `S`, `D` | Move/Enter free camera once active |
| `Shift` | Move the free camera faster |
| Hold `Z` | Temporary camera zoom |
| Left-click drone name | Spectate that drone while in freecam/orbit |
| Mouse wheel | Change orbit distance |
| Right mouse button | Orbit from the native camera or look with the free camera |
| Middle mouse button or `V` | Return to the drone camera |

All keys listed above are the defaults and can be rebound in-game. Open the Control Bindings window from the keyboard icon on the playback bar, click the button next to any action, and press the key you want (Escape cancels); a Reset Defaults button restores the stock bindings. Changes are saved and remembered across sessions.

## Enabling / Disabling the Mod

Liftoff detects mods and blocks posting multiplayer times on some maps while they are active. To temporarily disable the mod (and BepInEx entirely) without uninstalling:

1. Close Liftoff.
2. In the Liftoff installation folder, rename the `BepInEx` folder to something else, for example `BepInEx_Disabled`.
3. Start Liftoff - it runs completely unmodded.
4. To re-enable, close Liftoff and rename the folder back to `BepInEx`.

No game files are modified; this only hides the mod loader from the game.

## Building From Source

The project targets .NET Framework 4.7.2 and compiles against DLLs from a local Windows Liftoff installation. Install a .NET SDK with .NET Framework 4.7.2 targeting-pack support. It deliberately does not copy those dependencies to the build output.

```powershell
dotnet build -c Release -p:LiftoffDir="D:\SteamLibrary\steamapps\common\Liftoff"
```

The resulting plugin is `bin\Release\net472\LiftoffReplayPlus.dll`.

If Liftoff is installed elsewhere, replace the value of `LiftoffDir`. The directory must contain `Liftoff_Data\Managed` and `BepInEx\core`.

## License

Released under the [MIT License](LICENSE).
