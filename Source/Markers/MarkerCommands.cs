using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace cameraTools.Markers
{
    public static class MarkerCommands
    {
        private static bool _registered;

        public static void Register()
        {
            if (_registered)
            {
                return;
            }

            _registered = true;

            _ = new Terminal.ConsoleCommand("ct_mark",
                "Markers: ct_mark <tool|pick <area|round|square|point|shot|path>|list [kind|all]|build [list|new <name>|use <id|name>]|return|show|hide|auto|export|reload|clear [kind]|remove <id|label>|goto <id|label>|shot <label>|point <label> [x y z]>",
                args =>
                {
                    try
                    {
                        args.Context.AddString(RunMark(args));
                    }
                    catch (Exception ex)
                    {
                        args.Context.AddString($"ERROR: {ex.Message}");
                    }
                });

            _ = new Terminal.ConsoleCommand("ct_path",
                "Paths: ct_path <list|play <id|label> [duration] [waypoints|forward|look x y z]|stop|status|preview <id|label>>",
                args =>
                {
                    try
                    {
                        args.Context.AddString(RunPath(args));
                    }
                    catch (Exception ex)
                    {
                        args.Context.AddString($"ERROR: {ex.Message}");
                    }
                });
        }

        private static string RunMark(Terminal.ConsoleEventArgs args)
        {
            MarkerStore.EnsureLoaded();
            string mode = args.Length >= 2 ? args[1].ToLowerInvariant() : "list";
            switch (mode)
            {
                case "list":
                {
                    bool all = args.Length >= 3 && args[2].ToLowerInvariant() == "all";
                    MarkerKind? kind = args.Length >= 3 && !all ? Marker.ParseKind(args[2]) : null;
                    List<Marker> source = all ? MarkerStore.AllMarkers : MarkerStore.Markers;
                    List<Marker> items = kind == null ? source : source.Where(m => m.Kind == kind.Value).ToList();
                    List<string> lines = new() { $"OK: count={items.Count} build=\"{MarkerStore.ActiveBuild.name}\" world={MarkerStore.WorldName} file={MarkerStore.FilePath}" };
                    lines.AddRange(items.Select(m => m.Describe()));
                    return string.Join("\n", lines);
                }
                case "build":
                {
                    string sub = args.Length >= 3 ? args[2].ToLowerInvariant() : "list";
                    if (sub == "new")
                    {
                        Build created = MarkerStore.NewBuild(JoinLabel(args, 3));
                        return $"OK: build {created.id} \"{created.name}\" created and active";
                    }

                    if (sub == "use")
                    {
                        Build? target = MarkerStore.FindBuild(JoinLabel(args, 3));
                        if (target == null) return $"ERROR: no build matches '{JoinLabel(args, 3)}'";
                        MarkerStore.UseBuild(target);
                        return $"OK: active build {target.id} \"{target.name}\" ({MarkerStore.Markers.Count} markers)";
                    }

                    List<string> lines = new() { $"OK: count={MarkerStore.Builds.Count} active={MarkerStore.ActiveBuild.id}" };
                    lines.AddRange(MarkerStore.Builds.Select(b => $"{b.id} \"{b.name}\" markers={MarkerStore.AllMarkers.Count(m => m.build == b.id)}{(b.id == MarkerStore.ActiveBuild.id ? " (active)" : "")}"));
                    return string.Join("\n", lines);
                }
                case "tool":
                    if (Player.m_localPlayer == null) throw new InvalidOperationException("No local player.");
                    return MarkerTool.GiveTool(Player.m_localPlayer);
                case "pick":
                    if (Player.m_localPlayer == null) throw new InvalidOperationException("No local player.");
                    return MarkerTool.Pick(Player.m_localPlayer, args.Length >= 3 ? args[2] : "");
                case "return":
                    MarkerCamera.Return();
                    return $"OK: freefly={GameCamera.InFreeFly()}";
                case "show":
                    MarkerVisibility.Mode = MarkerVisibilityMode.Show;
                    return "OK: markers shown";
                case "hide":
                    MarkerVisibility.Mode = MarkerVisibilityMode.Hide;
                    return "OK: markers hidden";
                case "auto":
                    MarkerVisibility.Mode = MarkerVisibilityMode.Auto;
                    return $"OK: markers auto (visible={MarkerVisibility.Visible})";
                case "export":
                    return $"OK: saved {MarkerStore.Markers.Count} markers to {MarkerStore.Save()}";
                case "reload":
                    MarkerStore.Load();
                    return $"OK: loaded {MarkerStore.Markers.Count} markers from {MarkerStore.FilePath}";
                case "clear":
                {
                    MarkerKind? kind = args.Length >= 3 ? Marker.ParseKind(args[2]) : null;
                    int removed = MarkerStore.Clear(kind);
                    return $"OK: removed {removed} markers";
                }
                case "remove":
                {
                    Marker marker = Require(args, 2);
                    MarkerStore.Remove(marker);
                    return $"OK: removed {marker.id} \"{marker.label}\"";
                }
                case "goto":
                {
                    Marker marker = Require(args, 2);
                    GoTo(marker);
                    return $"OK: camera at {marker.kind} \"{marker.label}\"";
                }
                case "shot":
                {
                    if (GameCamera.instance == null) throw new InvalidOperationException("GameCamera is not ready.");
                    Transform cam = GameCamera.instance.transform;
                    Marker shot = new()
                    {
                        Kind = MarkerKind.Shot,
                        label = JoinLabel(args, 2),
                        cameraPosition = cam.position,
                        lookDirection = cam.forward
                    };
                    MarkerStore.Add(shot);
                    return $"OK: {shot.Describe()}";
                }
                case "point":
                {
                    if (args.Length < 3) return "Usage: ct_mark point <label> [x y z]";
                    Vector3 position;
                    if (args.Length >= 6)
                    {
                        position = CreativeZoneCameraController.ParseOrigin(args, 3);
                    }
                    else
                    {
                        if (GameCamera.instance == null) throw new InvalidOperationException("GameCamera is not ready.");
                        Transform cam = GameCamera.instance.transform;
                        if (!Physics.Raycast(cam.position, cam.forward, out RaycastHit hit, 200f))
                        {
                            throw new InvalidOperationException("Nothing under the crosshair; give x y z.");
                        }

                        position = hit.point;
                    }

                    Marker point = new() { Kind = MarkerKind.Point, label = args[2], position = position };
                    MarkerStore.Add(point);
                    return $"OK: {point.Describe()}";
                }
                default:
                    return "Usage: ct_mark <tool|pick <mark>|list [kind|all]|build [list|new <name>|use <id|name>]|return|show|hide|auto|export|reload|clear [kind]|remove <id|label>|goto <id|label>|shot <label>|point <label> [x y z]>";
            }
        }

        private static string RunPath(Terminal.ConsoleEventArgs args)
        {
            MarkerStore.EnsureLoaded();
            string mode = args.Length >= 2 ? args[1].ToLowerInvariant() : "list";
            switch (mode)
            {
                case "list":
                {
                    List<Marker> paths = MarkerStore.Markers.Where(m => m.Kind == MarkerKind.Path).ToList();
                    List<string> lines = new() { $"OK: count={paths.Count}" };
                    foreach (Marker p in paths)
                    {
                        lines.Add(p.Describe());
                        for (int i = 0; i < p.waypoints.Count; i++)
                        {
                            Waypoint wp = p.waypoints[i];
                            lines.Add($"  wp{i + 1} pos={Marker.Fmt(wp.position)} fwd={Marker.Fmt(wp.forward)} hold={wp.hold.ToString("F1", CultureInfo.InvariantCulture)}");
                        }
                    }

                    return string.Join("\n", lines);
                }
                case "play":
                {
                    Marker path = Require(args, 2);
                    float? duration = null;
                    int next = 3;
                    if (args.Length > next && float.TryParse(args[next], NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
                    {
                        duration = parsed;
                        next++;
                    }

                    PathLookMode lookMode = PathLookMode.Waypoints;
                    Vector3 target = Vector3.zero;
                    if (args.Length > next)
                    {
                        string look = args[next].ToLowerInvariant();
                        if (look == "forward") lookMode = PathLookMode.Forward;
                        else if (look == "waypoints") lookMode = PathLookMode.Waypoints;
                        else if (look == "look")
                        {
                            if (args.Length < next + 4) return "ERROR: look needs three values: look x y z";
                            lookMode = PathLookMode.Target;
                            target = CreativeZoneCameraController.ParseOrigin(args, next + 1);
                        }
                        else return $"ERROR: unknown look mode {args[next]}; use waypoints, forward, or look x y z";
                    }

                    PathPlayer.Play(path, duration, lookMode, target);
                    return $"OK: path started \"{path.label}\" waypoints={path.waypoints.Count} look={lookMode} {PathPlayer.Status()}";
                }
                case "stop":
                    PathPlayer.Stop();
                    return "OK: path stopped";
                case "status":
                    return PathPlayer.Status();
                case "preview":
                {
                    Marker path = Require(args, 2);
                    MarkerMode.Highlighted = path;
                    MarkerVisibility.Mode = MarkerVisibilityMode.Show;
                    if (path.waypoints.Count > 0)
                    {
                        Waypoint first = path.waypoints[0];
                        CreativeZoneCameraController.PlaceCamera(first.position, first.position + first.forward);
                    }

                    return $"OK: previewing \"{path.label}\"; markers shown until F8 or ct_mark auto";
                }
                default:
                    return "Usage: ct_path <list|play <id|label> [duration] [waypoints|forward|look x y z]|stop|status|preview <id|label>>";
            }
        }

        private static Marker Require(Terminal.ConsoleEventArgs args, int index)
        {
            if (args.Length <= index)
            {
                throw new ArgumentException("Marker id or label required.");
            }

            string key = string.Join(" ", args.Args.Skip(index));
            return MarkerStore.Find(key) ?? MarkerStore.Find(args[index]) ?? throw new ArgumentException($"No marker matches '{key}'.");
        }

        private static string JoinLabel(Terminal.ConsoleEventArgs args, int index)
        {
            return args.Length > index ? string.Join(" ", args.Args.Skip(index)) : "";
        }

        public static void GoTo(Marker marker)
        {
            MarkerCamera.Sent($"at {marker.kind} \"{marker.label}\"");
            switch (marker.Kind)
            {
                case MarkerKind.Shot:
                    CreativeZoneCameraController.PlaceCamera(marker.cameraPosition, marker.cameraPosition + marker.lookDirection.normalized * 10f);
                    break;
                case MarkerKind.Path:
                    Waypoint first = marker.waypoints[0];
                    CreativeZoneCameraController.PlaceCamera(first.position, first.position + first.forward);
                    break;
                case MarkerKind.Area:
                {
                    float span = marker.Shape == AreaShape.Circle ? marker.radius : Mathf.Max(marker.width, marker.depth) / 2f;
                    Vector3 offset = new Vector3(-1f, 0f, -1f).normalized * (span * 2f + 6f) + Vector3.up * (span + 6f);
                    CreativeZoneCameraController.PlaceCamera(marker.position + offset, marker.position);
                    break;
                }
                default:
                    CreativeZoneCameraController.PlaceCamera(marker.position + new Vector3(-3f, 2.5f, -3f), marker.position + Vector3.up * 0.8f);
                    break;
            }
        }

        [HarmonyPatch(typeof(Terminal), nameof(Terminal.InitTerminal))]
        private static class TerminalInitPatch
        {
            private static void Postfix() => Register();
        }
    }
}
