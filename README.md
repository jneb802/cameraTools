# cameraTools

Valheim client-side camera helpers for build recording, screenshots, and replay work.

## Panel

Press `F6` to open the camera tools panel. The panel can:

- Toggle freefly.
- Toggle HUD visibility.
- Toggle player-follow-camera mode.
- Show camera distance and angle from origin `0,0,0`.
- Prepare the camera for capture.
- Start or stop a 360 degree orbit.
- Capture a PNG screenshot.

## CLI Commands

These commands are regular Valheim terminal commands, so they can be run through `valheimCLI`.

```text
ct_freefly <on|off|toggle|status>
ct_hud <show|hide|toggle|status>
ct_prepare [x y z]
ct_status [x y z]
ct_place <cameraX cameraY cameraZ> <lookX lookY lookZ>
ct_move <start|stop|status> [duration] [startX startY startZ endX endY endZ] [lookX lookY lookZ] [linear|ease|in|out]
ct_orbit <start|stop|status> [duration] [degrees] [x y z]
ct_screenshot [filename]
ct_mark <list [kind|all]|build [list|new <name>|use <id|name>]|panel|return|show|hide|auto|mode on|off|export|reload|clear [kind]|remove <id|label>|goto <id|label>|shot <label>|point <label> [x y z]>
ct_path <list|play <id|label> [duration] [waypoints|forward|look x y z]|stop|status|preview <id|label>>
```

## Markers

Markers are a client-side overlay for planning shots of a build. They are never written to the world and are hidden automatically whenever `ct_move`, `ct_orbit`, `ct_path play` or `ct_prepare` runs, so a recording cannot contain one. They are stored per world as JSON under `BepInEx/config/cameraTools/markers/<world>.json`.

Press `F7` to enter marker mode. A status strip under the compass shows the current tool and keys. Everything is rebindable in the config file. Markers are grouped into **builds**; the overlay and the panel show the active build only, so one world file can hold several bases.

Open the inventory (`Tab`) and click the camera icon beside the player panel (left of the Praetoris server guide icon when that mod is installed) for the **marker dialog**: pick the tool and shape, switch builds (`<`, `>`, New, Rename), toggle marker mode and overlay visibility, browse the active build's markers, edit the selected marker's label, note and type, and Go to, Play path, Preview or Delete it. `Escape` closes the dialog. Naming a new marker uses the game's own text dialog.

Go to, Preview and path playback move the free camera. Press `F8` (or the dialog's Return camera button, or `ct_mark return`) to leave the free camera and return to your character; the status strip shows this hint whenever the camera is away.

| Tool | Place with | What it stores |
|---|---|---|
| Area | left click on the ground; scroll changes the radius (or width), shift+scroll the depth, ctrl+scroll rotates, `R` cycles circle, rectangle and polygon. Polygon: click each corner, then click the first corner again or press `F` to close | a labelled circle, rectangle or polygon with a type tag (room, exterior, courtyard, detail, hero, entrance, other) |
| Point | left click on anything | a labelled post marking one item of interest, with a note |
| Shot | left click while looking through the camera you want | the camera position and look direction |
| Path | left click adds a waypoint at the camera (freefly) or at eye height (walking); `G` starts and stops live recording while you move; `F` finishes; `Delete` removes the last waypoint | an ordered list of waypoints with look directions and optional hold times |

`T` cycles the tool. Look at a marker and press `E` to rename it (on a path waypoint, `E` sets its hold time), or `Delete` to remove it. Left click does not attack and `E` does not interact with the world while marker mode is on.

`ct_path play <label> [duration] [waypoints|forward|look x y z]` flies the camera along a path on a smooth spline at constant speed, pausing at waypoints with a hold time. The look direction is interpolated between waypoints by default, follows the travel direction with `forward`, or stays on a fixed target with `look x y z`. Without a duration the path plays at walking speed, about 1.6 m/s.

If no origin is given, commands use `0,0,0`.

Screenshots are written under:

```text
BepInEx/config/cameraTools/screenshots/
```

## Build

```bash
dotnet build
```

The built DLL is in `bin/Debug/`.
