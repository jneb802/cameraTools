# cameraTools

Valheim client-side camera helpers for build recording, screenshots, and replay work.

## Panel

Press `F6` to show or hide the Camera Tools window. It has two tabs, and `F6` hides the whole window at once without changing the camera, the playback or any setting in it. The window is drawn at Valheim's own menu scale (screen size and the game's UI scale setting), with text and controls sized like the inventory and crafting menus, so it keeps the same size relative to the game at every resolution.

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
ct_screenshot [filename] [--overlays]
ct_fov [degrees|reset]
ct_settings list | get <Section.Key> | set <Section.Key> <value>
ct_mark <tool|pick <mark>|list [kind|all]|build [list|new <name>|use <id|name>]|return|show|hide|auto|export|reload|clear [kind]|remove <id|label>|goto <id|label>|shot <label>|point <label> [x y z]>
ct_path <list|play <id|label> [duration] [waypoints|forward|look x y z]|stop|status|preview <id|label>>
record [name] | record start [name] [--rate 30|60] | record stop | record status
replay <file> | replay list | replay info <file> | replay events <file> | replay digest <file> [rate] | replay downsample <file> <new name> | replay folder | replay browser <open|close|status> | replay exit
ct_grid <on|off|status> [spacing] | ct_grid cell <name> | ct_grid probe <x> <z>
ct_trails <on|off|status> [user:id ...]
ct_shot play <file> [from] | stop | status | check <file> [aspect] | preview <file> [times...|count N] [--overlays]
ct_shot render <file> [--fps N] [--size WxH] [--supersample N] [--video] [--keep-frames] [--no-audio] [--fps-cap N] | render status | render cancel | render-all <folder> [options]
ct_shot new <name> | key [time] | save [name]
ct_replay state|status|media|targets|where <user:id>|digest [rate]|map [time|from-to] [sizeMeters] [pixels]|scene-export [margin]|tag [label]|tags|untag <id>|events [death|attack|damage|hit] [user:id]|target [user:id]|find [user:id]|view [user:id] <aim|attach|front|third|projectile>|detach|recorded [on|off|status]|follow <user:id> <x> <y> <z> [local]|pause|play|seek <seconds>|speed <0.1-4>|music [index]|volume <0-1>|ui [show|hide|toggle|status]|exit
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

### Event tags

Every recording carries timestamped tags an agent can read without screenshots. Times are seconds from the start of the replay, the clock `ct_replay seek` uses; a tag is within 0.25 s of the moment it shows in playback.

| Type | Meaning |
|---|---|
| `death` | The actor died: the recording game ran its death, or its synced health reached zero. One per actor. |
| `attack_start` | The actor started an attack: the game triggered a weapon or creature attack animation. A combo has one per swing. |
| `damage_taken` | The actor's synced health dropped between two samples (30 per second), or it died with health left at its last sample (the killing blow). `amount` is the drop and `health` what remains. |
| `hit` | A hit the recording game applied to the actor (the victim), recorded where the game applies it: `attacker`, hit `point` and `direction`, `damage` per type before resistances, health `taken`, `projectile` when one caused it, `hitType`, `skill`, and `result`: `hit`, `blocked`, `parried`, `staggered`, `backstab`, `killed`, `critical`, `dodged`, `guard_broken` (a hit can have several). |

Since file version 10, `attack_start` of a creature also names the creature AI's `target`. A player's target is not guessed.

Each tag names the actor as `user:id` (the id `ct_replay targets` and `ct_replay view` use), its prefab and a readable name.

- `ct_replay events [death|attack|damage] [user:id]` lists the tags of the playing replay.
- Saving a take also writes `<name>.events.json` next to it. `replay events <name>` writes that file for any recording, including older ones; it reads the file in the background, so repeat it until it reports `OK`.

A character another player's game simulates reaches this game only as synced data. Its death and killing blow are tagged when its health reaching zero arrives; when that game removes the character before the update arrives, both are missing rather than guessed.

Hits are recorded only where this game processes them: characters it simulates. In a local world that is every character; hits another player's game processes are missing rather than guessed. Files before version 10 have no hits, so `hit` is reported unavailable with the reason.

Older recordings stay readable. Files before version 9 have no health samples, so `damage_taken` is reported unavailable with the reason; their attacks are read from the saved animation triggers, and their deaths include only characters the recording game simulated. Files before version 6 have no deaths.

### Sample rate

Takes record 30 samples a second. `record start <name> --rate 60`, or the setting `Recording.SampleRate`, records 60 so fast attacks stay smooth in deep slow motion; it stores twice the character samples (a small meadow fight measured 8.0 MB per minute at 60 against 6.5 MB at 30; the surroundings are the same size at both rates). At 0.25x and slower, 60 samples keep fast weapon arcs that 30 samples cut short. The file stores its rate. `replay downsample <file> <new name>` writes a copy with every other sample, to compare the two rates on the same fight.

### Digest

`ct_replay digest [rate]` (in a session) or `replay digest <file> [rate]` (from the main menu) writes `<name>.digest.json` next to the recording: a compact description of the fight for an agent planning shots. Saving a take writes it too. It reads in the background: repeat the command until it reports `OK`. Made from the main menu it has no actor sizes or sun direction, because those come from loaded game data; make it in a session for everything.

| Field | Contents |
|---|---|
| header | `schemaVersion`, `replay`, `fileVersion`, `duration`, `world`, `gameVersion`, `sampleRate`, `trackRate`, `recorder` (the recording player; `recorderInferred` when guessed for files before version 10), `fightArea` (box around the characters in the events) |
| `actors` | id, kind (actor or ragdoll), prefab, readable name, `isPlayer`, `firstSeen`, `lastSeen`, `death`, body `height` and `radius` from the prefab's collider; since version 10 also `level`, `maxHealth`, `faction` |
| `tracks` | per actor, columns sampled at `trackRate` (default 10 per second): `t`, `x`, `y`, `z`, `yaw` (degrees clockwise from north), `speed` (horizontal m/s), `flags` (1 on ground, 2 flying, 4 blocking, 8 in water, 16 falling, 32 crouching), `health` (after the last recorded drop) |
| `projectiles` | prefab, launch time and position, end time and position, `owner` (version 10) |
| `drops` | dropped items: prefab, time and position |
| `events` | the event tags with the actor's position and facing at the time, and highlights |
| `effects` | prefab, start, end and position |
| `lighting` | per environment sample: environment, day fraction, `sunDirection` (unit vector toward the sun), `sunElevation`, `sunYaw`, wind |
| `unavailable` | each field the file cannot provide, with the reason |

`ct_replay where <user:id>` prints an actor's recorded position, facing and health at the current replay time. `ct_replay status` also prints the game's sun direction.

### Highlights

While watching, `H` (setting `Replay.HighlightKey`) tags the current moment with the camera view you have and the selected target, and asks for an optional label. `ct_replay tag [label]`, `ct_replay tags` and `ct_replay untag <id>` do the same from the console. Highlights are saved in `<name>.highlights.json` beside the recording, never in the `.valreplay`, show as marks above the timeline and appear in the digest as `highlight` events.

### Fight guides and the overhead map

- `ct_grid on [spacing]` lays a grid on the ground and floors around the fight: lines every meter (or `spacing`), bold lines every 5 m. Each 5 m cell has a name: letters west to east from `A`, numbers south to north from `1`, counted from the south-west corner of the fight area plus 10 m. `ct_grid cell C4` prints a cell's centre and corners; `ct_grid probe <x> <z>` prints the surface and terrain heights below a point, measured the way the scene export measures them.
- `ct_trails on [user:id ...]` draws each fighter's path in its own colour (the player in white) with a tick every second and a time label every 5 s, red crosses at deaths, orange crosses at hit points, and an arrow showing where each actor faces now.
- The guides never appear in renders, and appear in `ct_screenshot` only with `--overlays`.
- `ct_replay map [time | from-to] [sizeMeters] [pixels]` writes `BepInEx/config/cameraTools/maps/<name>-map-<range>.png`: a straight-down view of the fight area with the grid, cell names, trails over the range and actors at the time, drawn on top so trees cannot hide them. Image up is north. The `.json` beside it converts pixels to world coordinates.
- `ct_replay scene-export [margin]` writes `<name>.scene.json`: the surface height every meter over the fight area plus `margin` (default 30 m), terrain-only heights, and the bounds of every solid object, so camera positions can be chosen with clearance before they are checked in the game.

## Shots

A shot file is a camera move timed in replay seconds. While it plays it owns the replay clock, so the camera and the recorded action stay in step at any speed, and slow motion slows the game's own particles, cloth and water by the same amount. Shot files live in `BepInEx/config/cameraTools/shots/` (subfolders allowed); commands take a name relative to that folder or a full path.

```json
{
  "schemaVersion": 1,
  "replay": "mountainFight1",
  "name": "wolf_kill",
  "start": 10.6,
  "end": 13.4,
  "speed": [{"t": 11.5, "speed": 1.0}, {"t": 11.85, "speed": 0.3}, {"t": 12.5, "speed": 0.3}, {"t": 12.9, "speed": 1.0}],
  "camera": [
    {"t": 10.6, "position": {"actor": "795620938:350156", "offset": [3, 2.2, -4.5]}, "lookAt": {"actor": "795620938:365007", "height": 0.5}, "fov": 55, "ease": "linear"},
    {"t": 13.4, "position": [1446.0, 159.0, -5738.0], "lookAt": [1442.0, 157.5, -5733.0], "fov": 45, "roll": 4}
  ],
  "lookSmoothing": 0.15,
  "shake": [{"t": 12.3, "strength": 0.8, "duration": 0.3}],
  "shakeOnEvents": [{"type": "death", "actor": "795620938:365007", "strength": 0.8, "duration": 0.3}],
  "keepInFrame": ["795620938:350156"],
  "hide": ["drops"],
  "dof": {"enabled": true, "focus": "lookAt", "aperture": 0.6, "focalSize": 0.4}
}
```

| Field | Meaning |
|---|---|
| `start`, `end` | replay seconds the shot covers |
| `speed` | `{t, speed}` keys in replay seconds, 0.05 to 4, blended smoothly between keys. Each replay second plays for `1 / speed` output seconds |
| `camera` | keys at replay times. `position` is a world point `[x, y, z]` or `{actor, offset, frame}`: an offset from the actor's feet, in world axes or, with `"frame": "actor"`, turned with the actor's facing. `lookAt` is a world point or `{actor, height, offset}` where `height` is a fraction of the body height (default 0.6). `fov` in degrees (default 65), `roll` in degrees, `ease` (`linear`, `ease`, `in`, `out`) from this key to the next. Positions and aim follow a smooth curve through the keys; actor-relative keys follow the actor through the whole shot |
| `lookSmoothing` | seconds the aim takes to catch up with its target, so aim on a fast actor does not jitter; 0 aims exactly |
| `shake`, `shakeOnEvents` | rotation shake in degrees, at fixed times or on every `death`, `hit`, `damage` or `attack` event (optionally of one `actor`, `minTaken` for hits). The same every run |
| `keepInFrame` | actors the check requires on screen |
| `hide` | `drops` hides dropped items and their loot beams while the shot runs |
| `dof` | drives the game's depth of field: `focus` is `lookAt` or a distance in meters |

Commands:

- `ct_shot play <file> [from]` plays the shot, from its start or from a replay time inside it (ACCEPTED; poll `ct_shot status` until `mode=hold`); `ct_shot stop` returns the clock to the replay controls.
- `ct_shot check <file> [aspect]` steps through the shot every 0.05 s of replay time without rendering and reports each time range where the camera is under the terrain or inside an object, the view to the `lookAt` target is blocked (naming the object, or `terrain`), a `keepInFrame` actor is off screen or its centre is outside the middle 80% of the frame, or the camera is within 0.5 m of a body. An actor that died may leave the frame after its death. The report is also written to `<file>.check.json`.
- `ct_shot preview <file> [times... | count N] [--overlays]` saves stills through the shot's camera to `shots/.../previews/<shot>-<time>.png`; with `count N` it uses N evenly spaced times plus every death and hit inside the shot.
- `ct_shot new <name>`, `ct_shot key [time]` and `ct_shot save [name]` build a shot by hand: each key stores the free camera at the current replay time.
- `ct_fov <degrees|reset>` sets the field of view outside shots; `ct_status` reports field of view and roll.

### Rendering

`ct_shot render <file> [options]` renders a shot frame by frame into `BepInEx/config/cameraTools/renders/<shot>/`:

- Each frame advances output time by exactly `1/fps`; the replay follows the shot's speed keys and the game's own time step is fixed to the same value, so the result does not depend on how fast the game runs. The game can run slower than real time while rendering.
- Before the first frame it plays the 2 s before the shot at normal speed so effects already in progress exist.
- Frames are the final game image without the HUD, the Camera Tools window, the console, the cursor or the guides.
- `--fps N` (default 60), `--size WxH` (changes the game resolution for the render), `--supersample N` (renders N times larger), `--video` (encodes `<shot>.mp4` with ffmpeg and then deletes the frames unless `--keep-frames`), `--no-audio`, `--overlays` (keeps the guides), `--fps-cap N` (test option: caps the game frame rate while rendering).
- Audio: the game's mixed output is captured frame by frame with Unity's offline audio capture into `<shot>.wav`. `<shot>.cues.json` lists every recorded sound that starts in the shot in output seconds, with its position relative to the camera, for an edit to rebuild.
- `<shot>.render.json` holds the shot, settings, frame count and the replay time, output time, speed, camera position and field of view of every frame.
- `ct_shot render-all <folder> [options]` queues every shot file in a folder; `ct_shot render status` reports `PENDING: ... queue=N` until all are done; `ct_shot render cancel` stops after the current frame.

## Build

```bash
dotnet build
```

The built DLL is in `bin/Debug/`.
