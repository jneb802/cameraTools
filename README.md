# cameraTools

Valheim client-side camera helpers for build recording, screenshots, and replay work.

## Panel

Press `F6` to show or hide the Camera Tools window. It has two tabs, and `F6` hides the whole window at once without changing the camera, the playback or any setting in it.

While a replay plays, the window opens on its **Replay** tab: play, pause, timeline, speed, target and camera views, music, and Exit. The **Camera** tab can:

- Toggle freefly.
- Toggle HUD visibility.
- Toggle player-follow-camera mode.
- Show camera distance and angle from origin `0,0,0`.
- Prepare the camera for capture.
- Start or stop a 360 degree orbit.
- Capture a PNG screenshot.

## CLI Commands

These commands are regular Valheim terminal commands, so they can be run through `valheimCLI`. `ct_help` prints the full reference in game.

```text
ct_help
ct_ui <show|hide|toggle|status> [camera|replay]
ct_freefly <on|off|toggle|status>
ct_hud <show|hide|toggle|status>
ct_follow <on|off|toggle|status>
ct_smooth [0-1]
ct_frame <on|off|toggle|status>
ct_prepare [x y z]
ct_status [x y z]
ct_place <cameraX cameraY cameraZ> <lookX lookY lookZ>
ct_move <start|stop|status> [duration] [startX startY startZ endX endY endZ] [lookX lookY lookZ] [linear|ease|in|out]
ct_orbit <start|stop|status> [duration] [degrees] [x y z]
ct_screenshot [filename]
ct_settings list | get <Section.Key> | set <Section.Key> <value>
ct_mark <tool|pick <mark>|list [kind|all]|build [list|new <name>|use <id|name>]|return|show|hide|auto|export|reload|clear [kind]|remove <id|label>|goto <id|label>|shot <label>|point <label> [x y z]>
ct_path <list|play <id|label> [duration] [waypoints|forward|look x y z]|stop|status|preview <id|label>>
record [name] | record start [name] | record stop | record status
replay <file> | replay list | replay info <file> | replay folder | replay browser <open|close|status> | replay exit
ct_replay state|status|media|targets|target [user:id]|find [user:id]|view [user:id] <aim|attach|front|third|projectile>|detach|recorded [on|off|status]|follow <user:id> <x> <y> <z> [local]|pause|play|seek <seconds>|speed <0.1-4>|music [index]|volume <0-1>|ui [show|hide|toggle|status]|exit
```

### Replies

Every reply starts with a word an agent can check:

| Reply | Meaning |
|---|---|
| `OK:` | Done. The rest of the line is the resulting state as `key=value` pairs. |
| `ACCEPTED:` | Started and still running (saving a take, opening or leaving a session). The reply names the command to poll. |
| `PENDING:` | Still running. |
| `ERROR: invalid-argument:` | The input was wrong. Nothing changed. |
| `ERROR: unavailable:` | Not possible in the current game state, for example `ct_replay seek` with no replay playing. Nothing changed. |

Setters such as `ct_ui hide`, `ct_frame on` or `ct_replay recorded off` set a known state, so repeating them changes nothing. `toggle` inverts.

### Driving a replay from the console

```text
record start fight1          # in a world
record stop                  # ACCEPTED; poll until state=idle
record status
replay list                  # from the main menu
replay info fight1
replay fight1                # ACCEPTED; poll until state=session
ct_replay state
ct_replay targets
ct_replay view 123:456 third
ct_replay seek 12.5
ct_replay play
ct_ui hide
ct_replay status
ct_replay exit               # ACCEPTED; poll until state=menu
```

### Panel controls and their commands

| Panel | Control | Command |
|---|---|---|
| Window | F6 show/hide | `ct_ui show\|hide\|toggle` |
| Window | Camera and Replay tabs | `ct_ui show camera`, `ct_ui show replay` |
| Camera | Free Fly | `ct_freefly on\|off` |
| Camera | Show HUD | `ct_hud show\|hide` |
| Camera | Player Follows Camera | `ct_follow on\|off` |
| Camera | Smoothness slider, value field and Apply | `ct_smooth <0-1>` |
| Camera | Show 9:16 Frame | `ct_frame on\|off` |
| Camera | Status text | `ct_status [x y z]` |
| Camera | Prepare | `ct_prepare` |
| Camera | Orbit 360 / Stop Orbit | `ct_orbit start 20 360` / `ct_orbit stop` |
| Camera | Screenshot | `ct_screenshot [filename]` |
| Replay | Show the window on the Replay tab | `ct_replay ui show` |
| Replay | Time, duration, Playing/Paused | `ct_replay status` |
| Replay | Timeline slider | `ct_replay seek <seconds>` |
| Replay | Play / Pause | `ct_replay play`, `ct_replay pause` |
| Replay | Speed − / + | `ct_replay speed <0.1-4>` |
| Replay | Exit replay / Back to menu | `ct_replay exit` or `replay exit` |
| Replay | Target dropdown | `ct_replay targets`, `ct_replay target <user:id>` |
| Replay | Aim at, Attach here, Third person, Front, Projectile | `ct_replay view [user:id] <aim\|attach\|third\|front\|projectile>` |
| Replay | Find target | `ct_replay find [user:id]` |
| Replay | Detach | `ct_replay detach` |
| Replay | Recorded camera | `ct_replay recorded on\|off` |
| Replay | Camera status text | `ct_replay status` (`camera=`) |
| Replay | Music dropdown | `ct_replay music [index]` |
| Replay | Music volume slider | `ct_replay volume <0-1>` |
| Replay | Scene summary text | `ct_replay status` (`scene=`) |
| Replays window | Open from the main menu / Close | `replay browser open\|close` |
| Replays window | Recording list, Refresh | `replay list` |
| Replays window | Details of the selected recording | `replay info <file>` |
| Replays window | Watch | `replay <file>` |
| Replays window | Open folder | `replay folder` |
| Config file | Every setting | `ct_settings list\|get\|set` |

The mouse shortcuts of the replay camera (left-click aim lock, secondary attack to attach) pick the target under the crosshair; `ct_replay view <user:id> aim|attach` selects it by id instead.

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

## Replays

`record [name]` starts recording what happens around you and `record` again saves it. A take stops by itself after 10 minutes or at the memory budget set in the config. Recordings are files in `BepInEx/config/cameraTools/replays/<name>.valreplay`; copy one into that folder on another machine to watch it there.

A recording holds:

- every character, projectile, ragdoll and dropped item within 150 m: position, every bone, equipment, effects, sounds and deaths, 30 times a second;
- the camera you recorded with;
- the surroundings: every world object your game had loaded (building pieces, trees, rocks, location objects, terrain edits and their changes during the take), the base terrain of each loaded 64 m zone, and the weather, wind and time of day.

### Watching a recording

Open **Replays** on the main menu, pick a recording and press **Watch**. The game opens it on its own: no character, no original world and no travel. It starts paused at the first frame from the recorded camera. The Replay tab of the Camera Tools window (Ctrl+F1 for the cursor, `F6` to hide it for a clean view) plays, pauses, scrubs and changes speed, and the camera can fly freely, lock its aim on a target, attach to it, or follow the **Recorded camera**. **Back to menu** closes it.

The replay session is a temporary world that nothing can change or save:

- The recorded objects belong to a player who is never connected, so they show exactly as a client shows another player's objects. Nothing decays, grows, uses fuel, smelts or spawns.
- Dropped items are rebuilt as real items from their recorded data and moved along their recorded path, so mods that decorate items show them too, for example EpicLoot's loot beams. The hidden player does not pick them up.
- The game's wind, ocean and weather loops play for wherever the camera is; everything else you hear is the recorded sound.
- Every zone counts as generated, so the world adds no vegetation, locations or creatures of its own; the recorded terrain replaces the generated terrain.
- Character and world saves are switched off, and nothing is written to your characters or worlds.

The browser lists the mods the recording used. Objects, creatures and effects from a mod that is not loaded are left out, and the panel names them. A recording made in another game version still opens; objects whose data changed between versions can look different.

Only what your game had loaded is in a recording; beyond about 150 m to 300 m, depending on your view settings, you see bare terrain. Recordings made before cameraTools saved surroundings play with `replay <name>` inside their own world, laid over the loaded world.

## Build

```bash
dotnet build
```

The built DLL is in `bin/Debug/`.
