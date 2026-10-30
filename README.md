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
ct_mark <tool|pick <mark>|list [kind|all]|build [list|new <name>|use <id|name>]|return|show|hide|auto|export|reload|clear [kind]|remove <id|label>|goto <id|label>|shot <label>|point <label> [x y z]>
ct_path <list|play <id|label> [duration] [waypoints|forward|look x y z]|stop|status|preview <id|label>>
```

## Markers

Markers are a client-side overlay for planning shots of a build. They are never written to the world and only render while the **Marker tool** is in your hand and the camera is on your character, so a free-camera recording cannot contain one. They are stored per world as JSON under `BepInEx/config/cameraTools/markers/<world>.json`, grouped into **builds** so one world file can hold several bases (`ct_mark build new <name>`, `ct_mark build use <name>`).

The Marker tool is a hammer. Craft it from the hands tab for free, or type `ct_mark tool` to get it equipped. It works like any build tool:

| Action | Does |
|---|---|
| Right click | opens the build menu with the six marks: Area, Round area, Square area, Point of interest, Shot, Path |
| Left click | places the selected mark at the ghost; the game's text dialog asks for its name |
| Middle click or `Delete` | removes the mark under the crosshair (a path waypoint if you look at one), or the last point of the area or path being drawn |
| `E` | renames the mark under the crosshair; on a path waypoint it sets the hold time in seconds |
| `G` | starts and stops live path recording while you walk |
| `F` | finishes the area or path being drawn |
| `F8` | returns the camera to your character after `ct_mark goto`, `ct_path play` or `ct_path preview` |

| Mark | Place with | What it stores |
|---|---|---|
| Area | click each corner, then click the first corner again to close | a labelled polygon with a type tag |
| Round area | the ring follows the aim point; scroll sets the radius; click to place | a labelled circle |
| Square area | the frame follows the aim point; scroll sets the width, Shift+scroll the depth, Ctrl+scroll turns it; click to place | a labelled rectangle |
| Point of interest | click the thing | a labelled post marking one item worth a close-up |
| Shot | click while looking through the camera you want; works from the free camera too | the camera position and look direction |
| Path | click to drop a waypoint where you stand, looking where you look; click again without moving to finish | an ordered list of waypoints with look directions and optional hold times |

Wards, no-build zones, deep snow and dungeons do not block marks, and the tool never removes real pieces.

Areas are drawn the way Infinity Hammer draws its tool rulers: with the game's own workbench radius-marker bars laid along the edge and dropped onto the surface below, so they look like a ward or workbench circle. The live radius or width and depth shows under the piece description in the build HUD.

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
