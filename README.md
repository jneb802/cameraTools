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
ct_orbit <start|stop|status> [duration] [degrees] [x y z]
ct_screenshot [filename]
```

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
