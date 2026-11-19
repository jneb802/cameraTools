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
ct_shot play <file> [from] | stop | status | check <file> [aspect] [--pixels [rate]] [--pixels-debug] | preview <file> [times...|count N] [--strip N] [--size WxH] [--overlays]
ct_shot solve <intent file> [--no-pixels] [--keep N] | measure <render folder or .render.json> [aspect]
ct_shot render <file> [--fps N] [--size WxH] [--supersample N] [--video] [--keep-frames] [--no-audio] [--fps-cap N] | render status | render cancel | render-all <folder> [options]
ct_shot new <name> | key [time] | save [name]
ct_replay state|status|media|structures|targets|where <user:id>|digest [rate]|map [time|from-to] [sizeMeters] [pixels]|scene-export [margin]|tag [label]|tags|untag <id>|events [death|attack|damage|hit] [user:id]|target [user:id]|find [user:id]|view [user:id] <aim|attach|front|third|projectile>|detach|recorded [on|off|status]|follow <user:id> <x> <y> <z> [local]|pause|play|seek <seconds>|speed <0.1-4>|music [index]|volume <0-1>|ui [show|hide|toggle|status]|exit
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

- every character, projectile, ragdoll and dropped item within 150 m: position, every bone, equipment, effects, sounds and deaths, 60 times a second (30 when set);
- the camera you recorded with;
- the surroundings: every world object your game had loaded (building pieces, trees, rocks, location objects, terrain edits and their changes during the take), the base terrain of each loaded 64 m zone, and the weather, wind and time of day;
- objects other mods build only on your client and report through the replay contract (see [Objects from other mods](#objects-from-other-mods)): rooms with their position, rotation and when they appear and are removed (file version 11 and later), and visuals sampled like effects.

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

### Objects from other mods

Some mods build objects on every client without a ZDO, so the recorder cannot see them on its own: Valheim Endgame builds its encounter rooms this way, and draws boss visuals (the Uber Eikthyr vortex pulses, the Yagluth gaze glow, the Queen legion shield, nest shields and links, the Elder curse seed, Moder's flames) on each client from shared timing. Such a mod reports them through `cameraTools.Replay.ReplayHooks`, a public static class that uses only Unity and system types, so the mod binds it by reflection and neither mod needs the other installed:

| Member | Use |
|---|---|
| `Version` | Contract version (1). A caller checks it before binding. |
| `Replaying` | True while a replay plays. The mod draws none of its own per-client visuals then; the replay draws the recorded ones. |
| `InSession` | True in a replay session. The mod runs no encounter logic, builds no rooms of its own and does nothing to the local player. |
| `Structure(GameObject, name)` | Records a large still object (a room): name, position, rotation, and when it appears and is destroyed or deactivated. Rooms more than 300 m away horizontally are left out. |
| `Visual(GameObject, name)` | Records a visual like a game effect, sampled every frame: pose, scale, child nodes, particles, lights and trails (not LineRenderers), until it is destroyed or deactivated; reactivating it starts a new track. `name` is the prefab to rebuild it from. |
| `Builder(prefix, build)` | Registers how to build presentation copies of recorded names that start with `prefix`. The builder gets the name, position and rotation and returns an object with no gameplay scripts, or null for a name it does not know. |

In a replay session each recorded structure is built by its mod's builder when the replay reaches its start time, hidden after its end time and destroyed when the replay closes; seeking keeps exactly one copy. The copies keep their colliders, so `ct_shot check`, `ct_shot solve`, `ct_replay scene-export` and `ct_replay map` see their walls and floors. A structure no loaded mod builds, for example because Valheim Endgame is not installed, is left out with one log line, and the rest of the recording plays. Recorded visuals play like recorded effects in every replay; one whose prefab is not loaded is left out with one log line. `ct_replay structures` prints one line: each recorded structure, whether it is shown now, and how many scene roots carry its name, which shows duplicates.

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

Takes record 60 samples a second, so fast attacks stay smooth in deep slow motion. `record start <name> --rate 30`, or the setting `Recording.SampleRate`, records 30 to save memory and disk; 60 stores twice the character samples (a small meadow fight measured 8.0 MB per minute at 60 against 6.5 MB at 30; the surroundings are the same size at both rates). At 0.25x and slower, 60 samples keep fast weapon arcs that 30 samples cut short. The file stores its rate. `replay downsample <file> <new name>` writes a copy with every other sample, to compare the two rates on the same fight.

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
| `camera` | keys at replay times; a key with `"freeze": s` sits `s` output seconds into the freeze at its `t`. `position` is a world point `[x, y, z]` or `{actor, offset, frame}`: an offset from the actor's feet, in world axes or, with `"frame": "actor"`, turned with the actor's facing. `lookAt` is a world point or `{actor, height, offset}` where `height` is a fraction of the body height (default 0.6); with `"between": "<user:id>"` it is the midpoint of the two actors, each at `height` of its own body (an actor that died holds its last place), so a two-shot stays on both fighters. `fov` in degrees (default 65), `roll` in degrees, `ease` (`linear`, `ease`, `in`, `out`) from this key to the next. Positions and aim follow a smooth curve through the keys; actor-relative keys follow the actor through the whole shot |
| `freezes` | `{t, seconds}`: the fight stops at replay time `t` for `seconds` of output while the camera keeps moving (a frozen-moment orbit). See [Freezes](#freezes) |
| `lookSmoothing` | seconds the aim takes to catch up with its target, so aim on a fast actor does not jitter; 0 aims exactly |
| `shake`, `shakeOnEvents` | rotation shake in degrees, at fixed times or on every `death`, `hit`, `damage` or `attack` event (optionally of one `actor`, `minTaken` for hits). The same every run |
| `keepInFrame` | actors the check requires on screen |
| `hide` | `drops` hides dropped items and their loot beams while the shot runs |
| `dof` | drives the game's depth of field: `focus` is `lookAt` or a distance in meters |
| `framing` | what the shot is meant to show; the check measures against it (see [Framing](#framing)) |

### Freezes

A freeze stops the recorded fight at one replay time while the camera keeps moving, like bullet time in The Matrix:

```json
"freezes": [{"t": 64.18, "seconds": 2.5}],
"camera": [
  {"t": 63.95, "position": [-293.4, 46.0, -8940.0], "lookAt": {"actor": "1276435241:411080", "height": 0.55}},
  {"t": 64.18, "position": [-295.0, 46.2, -8943.6], "lookAt": {"actor": "1276435241:411080", "height": 0.55}},
  {"t": 64.18, "freeze": 1.25, "position": [-293.0, 46.6, -8947.4], "lookAt": {"actor": "1276435241:411080", "height": 0.55}},
  {"t": 64.18, "freeze": 2.5, "position": [-289.0, 46.8, -8947.0], "lookAt": {"actor": "1276435241:411080", "height": 0.55}},
  {"t": 64.6, "position": [-288.2, 46.8, -8946.0], "lookAt": {"actor": "1276435241:411080", "height": 0.4}}
]
```

- During a freeze the game's time scale is 0: characters, particles, cloth, water and recorded effects all stop, and the replay's recorded sounds and music pause. Replay time stays at `t` for `seconds` of output, then the shot carries on from `t` at its speed keys.
- Camera keys are placed on a camera clock: replay time plus every second frozen before it, plus a key's own `freeze`. The camera curve runs through keys inside a freeze on output time and through the rest of the shot on replay time, so one smooth move can enter, cross and leave a freeze. A key at `t` without `freeze` is the freeze's first frame.
- Actor-relative positions and look targets stay where the actor is at `t`. Shake is evaluated at replay time, so it holds still during a freeze.
- `t` must be inside the shot and before its end; a shot may have several freezes at different times. Ramping the speed down before a freeze (for example 1 → 0.3 → 0.05 over a few tenths of a second) makes the stop read as slow motion settling rather than a pause.
- The output length is the speed integral plus every freeze's seconds; `outputSeconds` in `ct_shot status` includes them.
- The check measures the camera every 0.25 s of output inside a freeze; the pixel pass measures replay times only, so it sees a freeze at its first frame. `ct_shot preview` and `--strip` add stills at the start, middle and end of each freeze, named `<shot>-<time>f<seconds>.png`; ask for one with `<time>@<seconds>`.
- The render's frame log gives each frame's `frozen` seconds (0 outside freezes) and `speed` 0 while frozen.

Commands:

- `ct_shot play <file> [from]` plays the shot, from its start or from a replay time inside it (ACCEPTED; poll `ct_shot status` until `mode=hold`); `ct_shot stop` returns the clock to the replay controls.
- `ct_shot check <file> [aspect] [--pixels [rate]] [--pixels-debug]` steps through the shot every 0.05 s of replay time, with the camera a render would have (look smoothing and shake included), without rendering. It reports each time range where the camera is under the terrain or inside an object, the view to the `lookAt` target is blocked (naming the object, or `terrain`), the view passes through or starts inside a tree's branches, a `keepInFrame` actor is off screen or its centre is outside the middle 80% of the frame, or the camera is within 0.5 m of a body, and the framing issues below. An actor that died may leave the frame after its death. The reply starts `OK: issues=N score=S`; the report, with the per-step framing series, is written to `<file>.check.json`. See [Framing](#framing) for `--pixels`.
- `ct_shot preview <file> [times... | time@frozen... | count N] [--overlays]` saves stills through the shot's camera to `shots/.../previews/<shot>-<time>.png`; with `count N` it uses N evenly spaced times plus every death and hit inside the shot. With `--strip N [--size WxH]` it saves N evenly spaced stills plus every death and hit and the framing moment at a fixed size (default 640x360, whatever the screen size), each twice: clean, and `<shot>-<time>-overlay.png` with the framing overlay. `<shot>.strip.json` holds each still's measurements.
- `ct_shot solve <intent file> [--no-pixels] [--keep N]` searches for camera positions; see [Solve](#solve).
- `ct_shot measure <render folder or .render.json> [aspect]` measures a finished render again from its frame log with the check's code, writes `<shot>.measure.json` beside it, and compares the subject's size and position with the shot's newest check report.
- `ct_shot new <name>`, `ct_shot key [time]` and `ct_shot save [name]` build a shot by hand: each key stores the free camera at the current replay time.
- `ct_fov <degrees|reset>` sets the field of view outside shots; `ct_status` reports field of view and roll.

### Framing

The `framing` block says what a shot is meant to show. Every field is optional; a shot without the block checks as before, with the framing defaults below.

```json
"framing": {"subject": "795620938:350156", "partner": "795620938:360271", "size": "full", "place": "left",
            "minVisible": 0.6, "maxForeground": 0.15, "moment": 27.03, "action": [26.43, 27.43], "weights": {"composition": 0}}
```

| Field | Meaning |
|---|---|
| `subject` | the featured actor. Default: the player among `keepInFrame`, else the first `keepInFrame` actor |
| `partner` | the other fighter. Default: the next `keepInFrame` actor |
| `size` | body height ÷ frame height: `extreme_wide` (0.03-0.15), `wide` (0.2-0.5), `full` (0.75-0.9), `knee` (1.2-1.4), `cowboy` (1.4-1.6), `medium` (2.2-2.5), `medium_close` (3.0-3.4), `close` (4.5-5.5), or `[min, max]`. Default: at least 0.33 (a featured player is never a speck) |
| `place` | `left`, `centre` or `right` third, or `[x, y]` (frame 0-1, y up). Default: none (the centre must stay in the middle 80%) |
| `minVisible` | the visible fraction each fighter needs. Default 0.6 |
| `maxForeground` | the frame fraction foreground may cover. Default 0.15 |
| `moment` | the impact time in replay seconds: the strike-frame rule applies there |
| `action` | `[from, to]` replay seconds inside the shot where the framing rules apply. Outside it fighters may enter and leave the frame (a locked-off shot holds its frame while the action plays through it): the in-frame, split-pair, hidden, size, place, lead-room, edge-cut, spread, in-front, foreground and clutter rules, the `keepInFrame` checks and the score count only inside it, in the ray check, the pixel pass and solve. The strike-frame rule at `moment` is unchanged. Default: the whole shot |
| `weights` | score term weights: `visibility` 0.35, `size` 0.15, `position` 0.15, `margin` 0.15, `angle` 0.10, `composition` 0.10 |

Each step measures every `keepInFrame` actor and the subject and partner from the recorded tracks, as vertical capsules of the recorded height and radius:

- the projected box (both end circles of the capsule, not clipped to the frame) and head box; `size` (projected feet-to-head distance ÷ frame height); `center` (of the part of the box inside the frame); `headroom`; `lead` (space ahead ÷ space ahead and behind, toward the movement direction above 1 m/s, otherwise the facing direction; not measured when the actor faces within 30° of the camera or away from it); `inFrame` (the share of the box inside the frame); `visible`.
- `visible`: rays from the camera to a 3 × 5 grid on the camera-facing side of the capsule plus the head, blocked by solid objects, tree canopies and other actors' capsules. Replay characters have no colliders, so the capsules come from the tracks; another actor blocks with its body reach (half the horizontal size of its prefab's meshes, times 0.5, at least its collider radius), because a collider is thinner than arms, wings and a leaning body. `bodies` in the report lists the radii used.
- `foreground`: a 16 × 9 grid of screen rays; the share that hits anything, actors included, nearer than both 2 m and half the distance to the nearest framed subject (the subject, or an actor at least 80% in frame). `headCovered`: such a hit inside a subject's head box.
- `pairSpread`: the frame width from the left edge of the subject and partner boxes to the right edge of either, when both are at least half in frame.
- `inFront`: the largest share of the 16 × 9 grid of screen rays that one actor outside the measured ones (another wolf of the pack, a bystander) covers nearer than the subject, at any distance (`inFrontActor` names it). Those actors also block the visible rays.
- A creature more than 1.8 times the subject's height counts as `large`: its in-frame ratio uses only its body up to 1.3 times the subject's height, so a camera at the player's level may cut off its head.

Framing issues, each with a time range and the worst `value`:

| Issue | When |
|---|---|
| `subject_hidden` | a measured actor's visible fraction is under `minVisible` (the partner, like `pair_split`, only from 0.5 s before `moment`) |
| `subject_size` | the subject's size is outside the intended range (without `size`: under 0.33) |
| `subject_place` | the subject's centre is more than 0.15 of the frame width from `place` |
| `pair_split` | the partner's in-frame ratio is under 0.95 (with a `moment`, only from 0.5 s before it: an attacker may run into the frame) |
| `subject_cut` | the subject's in-frame ratio is under 0.95 (a frame edge cuts the body, the bottom edge included) |
| `pair_spread` | the subject and partner span more than 70% of the frame width: fighters at opposite edges with an empty middle |
| `actor_in_front` | an actor that is not measured covers more than 15% of the frame nearer than the subject |
| `foreground_cover` | foreground over `maxForeground`, or touching a head box |
| `no_lead_room` | the subject's lead ratio is under 0.6 for more than 0.3 s |
| `edge_cut` | a frame edge within 3% of body height of the subject's ankle, knee, waist or neck |
| `strike_frame` | at `moment`, the subject or partner is less than 95% in frame, its head is outside the frame, or it is less than `minVisible` visible |
| `low_separation` | pixel pass only: the subject's brightness against the background directly behind it, averaged over the samples within 0.15 s, stays within ±0.32 stops for two samples in a row or more (0.1 s at 10 samples a second; the b05 shots of mountainFight1 fade into fog over their last 0.2 s) |
| `clutter` | pixel pass only: things other than the subject and the terrain (the partner, other actors, ragdolls, rocks, trees) cover more than 35% of the frame from the camera to 2 m past the subject, however near or far that is. A `large` partner is left out: it is meant to fill a large-creature frame |

`subject_cut`, `pair_spread` and `actor_in_front` came from the user's labels on mountainFight1 (see the odin `framing.md`, Calibration); `pair_split` was 0.8 before.

The score is 0 to 1. Each step's terms (visibility, size, position, margin, view angle, composition) are weighted by `weights`; the shot score is 0.7 × the mean + 0.3 × the 10th percentile of the step scores, so one bad moment is not hidden by the average. `check.json` holds `score`, `issues` and `series` (one entry per step: camera, direction, field of view, foreground, terms, and per actor its box, head box, size, centre, headroom, lead, in-frame ratio, visible fraction and distance).

`--pixels [rate]` adds the exact pixel pass: at `rate` samples a second (default 10) the replay seeks, waits for the actors, and renders a small buffer (144 pixels high, at the check's aspect) through the same camera. Each measured actor is drawn in a flat colour over the scene's depth and over the other actors: `visible` is the pixels that pass the depth test ÷ the pixels of the actor drawn alone, `inFrame` the share of its pixels inside the frame (drawn alone with a view twice as wide), and `foreground` the share of the frame nearer than the foreground limit, `clutter` the share covered by anything but the subject, the terrain and a `large` partner up to 2 m past the depth of the subject's body middle, and each actor's `stops`: the brightness of its visible pixels against the rest of its box grown by half, in the game picture rendered at the buffer size through the same camera (the strip's `sep`). It catches tree leaves, creature meshes and broken pieces that capsules and colliders miss (on mountainFight1 a Fenring hid the player completely while the rays reported 100% visible). The same issue types are reported with `"method": "pixels"` (`pair_split` and `subject_cut` at 0.85 in frame, because the pixels include carried weapons and capes that swing past the frame edge); `pixels.raysDiffer` lists the samples where the ray and pixel visible fractions differ by more than 0.2. It runs over several frames: the reply is `ACCEPTED`, then poll `ct_shot status` until no `job=`; `last=` gives the counts and the time it took (about 5-15 s for a 3 s shot). `--pixels-debug` also writes each sample's buffer to `shots/.../pixels/<shot>/`: red is the first actor's visible pixels, green the same actor drawn alone, blue the foreground.

The overlay on `--strip` stills draws the thirds lines, a shaded 10% margin, each measured actor's box and head box labelled with its name and role, and under each box its size, visible fraction, lead ratio and brightness against the background in stops (`sep`; negative is darker than what is behind it).

### Solve

`ct_shot solve <intent file> [--no-pixels] [--keep N]` searches for camera positions and writes shot files. The intent file holds one intent or `{"replay", "out", "intents": [...]}`; `out` is a folder under `shots/` (default: the intent file's folder).

```json
{"name": "b08_fight", "subject": "795620938:350156", "partner": "795620938:360271", "moment": 27.03, "window": [25.53, 27.83],
 "side": -1, "move": "static", "size": [0.35, 0.9], "place": null, "minVisible": 0.6, "maxForeground": 0.15,
 "fovs": [40, 47, 55], "angles": 24, "heights": 8, "heightRange": [-5, 35], "keep": 4, "minScore": 0.6,
 "speed": [], "shakeOnEvents": [], "hide": ["drops"], "note": ""}
```

- `side`: 1 or -1, the side of the line from subject to partner (1 is the left walking from the subject to the partner); 0 tries both. `move`: `static`, `push` (20% closer over the shot), `dolly` (sideways by a quarter of the distance), `orbit` (30° around the pair), `track` (moves with the subject) or `locked`. The camera is at the searched position at `moment`; the look target follows the subject (or the fighters' midpoint), except in a `locked` shot.
- `locked`: camera position and look target are both fixed world points, one key, `lookSmoothing` 0: the frame holds while the action plays through it. The frame is aimed at the average of both fighters' anchor points over the action range, not at one moment, and sized from the intent's `size`. `action` (`[from, to]`, default 0.6 s before `moment` to 0.4 s after) is written to the shot's `framing.action`, so fighters may enter and leave the frame outside it; the hard rejects and the score count only inside it. Any move can take `action`.
- Candidates: toric-space placements around the two fighters (Lino and Christie 2015): `angles` positions on the arc from behind the partner to behind the subject on the allowed side × `heights` turns of the plane about the line between them over `heightRange` degrees × 3 fields of view (by size: full and wider 40/47/55°, cowboy and medium 30/35/40°, closer 18/22/25°). The anchors land at screen points set by the size (closer together in wider shots); the toric placement gives the direction, and the camera stands at the distance the intended size needs. Without `place` the camera then aims at the midpoint between the two fighters and the shot's look target follows that midpoint (`between`), so both stay in frame when one leaps in from far away. Without a partner the camera circles the subject.
- Hard rejects: a 0.4 m sphere at the camera touches a solid object or an actor's capsule; the camera is under 0.5 m above the surface; the camera is on the wrong side of the line at `moment`; the subject's in-frame ratio is under 0.8 at any step, or the partner's from 0.5 s before `moment`; the visible fraction is under 0.5 for more than 15% of steps (rays, then pixels). With `action`, the in-frame and visible rejects count only inside it.
- The rest are scored over the whole shot with the check's score, the ten best and the best of each angle bin are refined with Nelder-Mead over arc, height, field of view and distance (32 evaluations each), checked in full, and the ten best plus the best of each bin are scored again with the pixel pass (4 samples a second; visibility from pixels), unless `--no-pixels`.
- Angle bins, at `moment`: `high_wide` (camera 20° or more above the subject), `front` (within 25° of where the subject faces), `front_three_quarter` (25-70°), `side` (70-115°), `over_shoulder` (behind, nearer the subject than the partner, within 4.5 m) and `back` (behind otherwise).
- The pixel pass also measures separation and clutter: a shot with `low_separation` (two samples in a row) or `clutter` over 35% inside the action has its score multiplied by 0.85 for each (`pixelIssues` in the report); at night every angle may separate poorly, so neither rejects.
- A shot passes with no `subject_hidden`, `pair_split`, `subject_cut`, `pair_spread`, `actor_in_front`, `strike_frame`, `foreground_cover`, `subject_size` or `subject_place` issue and a score of at least `minScore`. When nothing passes, the solver drops constraints in this order: composition (and `subject_place`), then view angle, then size (and `subject_size`), then a wider shot size. It never relaxes visibility.
- Fewer than three angle bins with a shot that met the hard rejects: a fill search runs only in the missing bins (placements outside them are skipped before they are measured), with twice as many arc positions and at 1, 1.35 and 1.8 times the distance (a wider frame keeps fast fighters in it), then refines, checks and ranks them like the main search. Such shots are often wider than the intended size and come back with `"pass": false`.
- It writes the best passing shot of each bin, then, for bins without one, the best shot that met the hard rejects (with `"pass": false`), up to `keep` (default 4), as `<name>_<bin>.json` with the `framing` block, `score`, `bin`, `pass` and `solve` (search parameters, ray and pixel scores, relaxed constraints, issues), and `<name>.solve.json` with the search report: evaluations, reject counts by reason with examples, stages, `fill` (missing bins, evaluations, bins added), `bins` (per bin: placements measured, rejects by reason, finalists, passing, the shot written, and `why` when none was) and every finalist. It runs over many frames (about 1.5-4 minutes per intent): the reply is `ACCEPTED`; poll `ct_shot status` until no `job=`.

### Rendering

`ct_shot render <file> [options]` renders a shot frame by frame into `BepInEx/config/cameraTools/renders/<shot>/`:

- Each frame advances output time by exactly `1/fps`; the replay follows the shot's speed keys and the game's own time step is fixed to the same value, so the result does not depend on how fast the game runs. The game can run slower than real time while rendering.
- Before the first frame it plays the 2 s before the shot at normal speed so effects already in progress exist.
- Frames are the final game image without the HUD, the Camera Tools window, the console, the cursor or the guides.
- `--fps N` (default 60), `--size WxH` (changes the game resolution for the render), `--supersample N` (renders N times larger), `--video` (encodes `<shot>.mp4` with ffmpeg and then deletes the frames unless `--keep-frames`), `--no-audio`, `--overlays` (keeps the guides), `--fps-cap N` (test option: caps the game frame rate while rendering).
- Audio: the game's mixed output is captured frame by frame with Unity's offline audio capture into `<shot>.wav`. `<shot>.cues.json` lists every recorded sound that starts in the shot in output seconds, with its position relative to the camera, for an edit to rebuild.
- `<shot>.render.json` holds the shot, settings, frame count and, for every frame, the replay time, output time, seconds into a freeze (`frozen`), speed, camera position, rotation (quaternion `[x, y, z, w]`), look target, roll, field of view and `framing` (size, centre and in-frame ratio of the subject, partner and `keepInFrame` actors), so a finished render can be measured again (`ct_shot measure`) and an edit can check its cuts.
- `ct_shot render-all <folder> [options]` queues every shot file in a folder; `ct_shot render status` reports `PENDING: ... queue=N` until all are done; `ct_shot render cancel` stops after the current frame.

## Staging

`ct_stage` places creatures and the player, holds them still, walks them along routes, makes them attack on cue and releases them to their own AI, so a fight can be recorded the same way take after take. While a creature is staged its own AI update does not run; the staging steps drive it with the game's path finding and attacks, so it moves and attacks with its normal animations. Only the owner of a creature can stage it (in a local world, every creature).

By hand:

```text
ct_stage spawn m Morgen 45 ground 47 toward=player state=sleep   # a held, sleeping Morgen at a point
ct_stage take c1 Charred_Melee                                    # stage the nearest existing Charred
ct_stage attacks Morgen                                           # the attack names it can be told to use
ct_stage do m wake                                                # a sleeping Morgen bursts out of the ground
ct_stage do c1 walk path=charge to=player stop=1.3 pace=run       # along a path marker, then up to the player
ct_stage do m attack attacks=Morgen_swipe_1,Morgen_bodyslam toward=player
ct_stage do player attack count=3 toward=c1                       # a three-swing combo at c1
ct_stage do c1 release target=player                              # back to its own AI, hunting the player
```

Actions (`ct_stage do <id|player> <action> [key=value ...]`):

| Action | Does | Keys |
|---|---|---|
| `hold` | stand still, facing `toward` or `facing` | `toward`, `facing` |
| `walk` | walk the points in order, then hold (or release) | `points` (`x,z;x,y,z`), `path` (a path marker: its waypoints and their holds), `to` (an actor, marker or point to finish at, followed while it moves), `stop` (metres short of `to`: 1.5 for an actor, else 0), `pace` (`walk`, `run`, `sprint`), `toward` (face at the end), `then` (`hold`, `release`) |
| `face` | turn to `toward` or `facing` | `toward`, `facing` |
| `attack` | creature: the named attacks in order, each after the previous ends plus `gap`; player: `count` swings, each pressed `gap` after the previous one starts and held until the game starts it, so a combo chains | `attacks` (names from `ct_stage attacks`), `count`, `gap` (0.3), `hold` (seconds to hold the button, for charged attacks), `secondary`, `toward` |
| `block` | the player blocks for `hold` seconds | `hold`, `toward` |
| `release` | back to its own AI | `hunt` (true), `target` (an actor to attack) |
| `wake`, `sleep` | a creature's sleep, as at the start of a camp | |
| `place` | teleport to `at`, facing `toward` or `facing` | `at`, `toward`, `facing` |
| `equip` | equip an item from the inventory | `item` (prefab name) |

Points are `[x, z]` (snapped to the ground) or `[x, y, z]`; `facing` is degrees clockwise from north (+z). Targets (`toward`, `to`, `target`) are an actor id, `player`, a marker label or a point.

A scene file in `BepInEx/config/cameraTools/scenes/<scene>.json` runs a whole fight: `ct_stage run <scene> [record=<name>] [rate=30|60]` clears the area, places every actor, starts the recording (after `record.lead` seconds, so scene time 0 is in the take), starts each action at its time `t` or `delay` seconds after the action labelled `after` finishes, and ends at `end` (or when nothing is left to do, plus `record.tail`). Every action and its result is written to `ct_stage log` and, when recording, to the take's highlights as `stage: <actor> <what>` at the recording time, so beats are exact.

```json
{
  "name": "charge",
  "record": { "name": "charge1", "rate": 60, "lead": 1, "tail": 2 },
  "clear": { "radius": 40, "at": [45, 40] },
  "end": 24,
  "actors": [
    { "id": "player", "at": [45, null, 29.5], "facing": 0, "item": "SwordBlackmetal" },
    { "id": "m", "prefab": "Morgen", "at": [45, null, 47], "toward": "player", "state": "sleep" },
    { "id": "c1", "prefab": "Charred_Melee", "at": [28, null, 62], "toward": "player" }
  ],
  "actions": [
    { "t": 0.5, "actor": "c1", "do": "walk", "path": "charge", "to": "player", "stop": 1.3, "pace": "run", "label": "arrive" },
    { "after": "arrive", "delay": 0.2, "actor": "c1", "do": "attack", "attacks": "charred_greatsword_swing", "toward": "player" },
    { "after": "arrive", "delay": 0.1, "actor": "player", "do": "block", "toward": "c1", "hold": 1.2, "label": "block" },
    { "after": "block", "delay": 0.1, "actor": "player", "do": "attack", "count": 3, "toward": "c1", "label": "combo" },
    { "after": "combo", "delay": 0.3, "actor": "m", "do": "wake", "label": "wake" },
    { "after": "wake", "delay": 2.5, "actor": "m", "do": "attack", "attacks": "Morgen_bodyslam", "toward": "player" }
  ]
}
```

Actors: `id` (`player` is the local player), `prefab` (spawn) or `take` (stage the nearest creature of that name within `radius`), `at`, `facing` or `toward`, `level`, `health`, `state` (`hold`, `sleep`, `free`), `item`. `clear` is `true`/`false` or `{radius, at, drops}`: it removes creatures that are not staged and dropped items.

### Staging in a mod room

A mod can give `ct_stage` rooms to build in the live world. Valheim Endgame does: `ct_stage room list` shows its rooms. `ct_stage room <id> [move=true|false] [hide=true|false] [height=4500]` builds the room 4,500 m above the player with no encounter (no waves, gates, portals or rewards), moves the player just inside it so the room's own environment applies (`uber_elder_rootbound` gives `GDKing`, `uber_eikthyr_grove` gives `Eikthyr`; run `resetenv` first if a debug `env` is set) and hides the player's model. Three names then work wherever a point is accepted: `room.boss` (the room's boss marker), `room.stand` (where the player stands) and `room.origin`. A creature spawned at `room.boss` faces the marker's direction unless `facing` or `toward` is given.

```text
ct_stage room uber_elder_rootbound
ct_stage spawn e VE_UberElder room.boss facing=0        # held, idling, facing +z from its first frame
ct_stage attacks VE_UberElder                           # gd_king_scream, gd_king_shoot, VE_Elder_Shockwave, ...
ct_stage do e attack attacks=VE_Elder_Shockwave         # plays on cue, then holds again
ct_stage clear                                          # removes the boss and the room, teleports the player back
```

A scene file takes `"room": "<id>"` or `{ "id", "move", "hide", "height" }`; the room is built before the actors, and `clear` and the next `run` remove it. The creature is created already facing its direction, so it never walks toward the hidden player. While a creature is staged its AI update does not run, so the game's own animator keeps its idle animation playing; the mod that registered the room also pauses its own encounter mechanics (shield, summons, nests) on staged creatures through `StageHooks.StagedCreatures`, and turns them on again on `release`.

Mods bind to `cameraTools.Staging.StageHooks` by reflection (`RoomProvider`, `StagedCreatures`; `Version` 1), as they do for `ReplayHooks`.

Notes:

- Creature mods that add spawns (Star Level System's spawn rate) also copy staged spawns; an unstaged copy fights normally. Stage in a profile without them, or `ct_stage clear <radius>` after spawning.
- A hit on the player's block delays the player's next swing by about 0.9 s; that is the game's block recoil.
- `ct_stage stop` ends a scene and saves its recording; `ct_stage clear [radius]` removes staged creatures and frees the player.

## Build

```bash
dotnet build
```

The built DLL is in `bin/Debug/`.
