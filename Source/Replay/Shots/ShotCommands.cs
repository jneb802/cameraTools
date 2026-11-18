using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace cameraTools.Replay.Shots
{
    /// <summary>Console commands for shots, the fight guides, the overhead map and field of view.</summary>
    internal static class ShotCommands
    {
        internal const string ShotUsage = "ct_shot play <file> [from] | stop | status | check <file> [aspect] [--pixels [rate]] | preview <file> [times...|count N] [--strip N] [--size WxH] [--overlays] | solve <intent> [--no-pixels] [--keep N] | measure <render> | " +
            "render <file> [--fps N] [--size WxH] [--supersample N] [--video] [--keep-frames] [--no-audio] [--fps-cap N] | render status | render cancel | render-all <folder> [options] | new <name> | key [time] | save [name]";

        internal static void Register()
        {
            new Terminal.ConsoleCommand("ct_shot", ShotUsage, args =>
            {
                try
                {
                    ShotPlayer shots = ShotPlayer.Instance;
                    string action = args.Length > 1 ? args[1].ToLowerInvariant() : "status";
                    switch (action)
                    {
                        case "status": args.Context.AddString("OK: " + shots.Status()); return;
                        case "stop": args.Context.AddString("OK: " + shots.StopCommand()); return;
                        case "load":
                        case "play":
                            args.Context.AddString(shots.Play(ShotPlayer.Resolve(CommandText.Arg(args, 2, "ct_shot play <file> [from]")),
                                args.Length > 3 ? CommandText.Number(args[3], 0f, 100000f, "Start time") : null));
                            return;
                        case "check":
                        {
                            string path = ShotPlayer.Resolve(CommandText.Arg(args, 2, "ct_shot check <file> [aspect] [--pixels [rate]] [--pixels-debug]"));
                            float aspect = Screen.width / (float)Mathf.Max(1, Screen.height);
                            float? rate = null;
                            bool debug = false;
                            for (int index = 3; index < args.Length; index++)
                            {
                                if (args[index] == "--pixels")
                                {
                                    rate = 10f;
                                    if (index + 1 < args.Length && !args[index + 1].StartsWith("--")) rate = CommandText.Number(args[++index], 0.5f, 60f, "Pixel samples a second");
                                }
                                else if (args[index] == "--pixels-debug") { debug = true; rate ??= 10f; }
                                else aspect = CommandText.Number(args[index], 0.2f, 5f, "Aspect");
                            }
                            if (rate.HasValue) { args.Context.AddString(shots.CheckPixels(path, aspect, rate.Value, debug)); return; }
                            foreach (string line in shots.Check(path, aspect).Split('\n')) args.Context.AddString(line);
                            return;
                        }
                        case "preview":
                        {
                            string path = ShotPlayer.Resolve(CommandText.Arg(args, 2, "ct_shot preview <file> [times...|time@frozen...|count N] [--strip N] [--size WxH] [--overlays]"));
                            List<ShotTime> times = new();
                            ShotPlayer.PreviewOptions options = new();
                            for (int index = 3; index < args.Length; index++)
                            {
                                if (args[index] == "--overlays") options.Overlays = true;
                                else if (args[index] == "count") options.Count = (int)CommandText.Number(CommandText.Arg(args, ++index, "count N"), 1, 60, "Count");
                                else if (args[index] == "--strip") options.Strip = (int)CommandText.Number(CommandText.Arg(args, ++index, "--strip N"), 1, 60, "Strip stills");
                                else if (args[index] == "--size") (options.Width, options.Height) = Size(CommandText.Arg(args, ++index, "--size WxH"));
                                else if (args[index].Contains('@'))
                                {
                                    // <replay time>@<seconds into the freeze at that time>
                                    string[] parts = args[index].Split('@');
                                    times.Add(new ShotTime(CommandText.Number(parts[0], 0f, 100000f, "Preview time"), CommandText.Number(parts[1], 0f, 60f, "Seconds into the freeze"), true));
                                }
                                else times.Add(new ShotTime(CommandText.Number(args[index], 0f, 100000f, "Preview time")));
                            }
                            if (options.Strip == 0 && args.Args.Contains("--size")) options.Strip = Mathf.Max(1, times.Count == 0 ? options.Count : times.Count);
                            args.Context.AddString(shots.Preview(path, times, options));
                            return;
                        }
                        case "solve":
                        {
                            string path = ShotPlayer.Resolve(CommandText.Arg(args, 2, "ct_shot solve <intent file> [--no-pixels] [--keep N]"));
                            bool usePixels = true;
                            int keep = 0;
                            for (int index = 3; index < args.Length; index++)
                            {
                                if (args[index] == "--no-pixels") usePixels = false;
                                else if (args[index] == "--keep") keep = (int)CommandText.Number(CommandText.Arg(args, ++index, "--keep N"), 1, 10, "Shots to keep");
                                else throw new ArgumentException($"Unknown solve option {args[index]}");
                            }
                            args.Context.AddString(shots.Solve(path, usePixels, keep));
                            return;
                        }
                        case "measure":
                            args.Context.AddString(shots.Measure(CommandText.Arg(args, 2, "ct_shot measure <render folder or .render.json> [aspect]"),
                                args.Length > 3 ? CommandText.Number(args[3], 0.2f, 5f, "Aspect") : (float?)null));
                            return;
                        case "render":
                        {
                            string target = CommandText.Arg(args, 2, "ct_shot render <file> [options] | render status | render cancel");
                            if (target == "status") { args.Context.AddString(shots.RenderStatus()); return; }
                            if (target == "cancel") { args.Context.AddString(shots.Cancel()); return; }
                            args.Context.AddString(shots.Render(ShotPlayer.Resolve(target), Options(args, 3)));
                            return;
                        }
                        case "render-all":
                            args.Context.AddString(shots.RenderAll(CommandText.Arg(args, 2, "ct_shot render-all <folder> [options]"), Options(args, 3)));
                            return;
                        case "new": args.Context.AddString("OK: " + shots.NewShot(CommandText.Arg(args, 2, "ct_shot new <name>"))); return;
                        case "key":
                            args.Context.AddString("OK: " + shots.AddKey(args.Length > 2 ? CommandText.Number(args[2], 0f, 100000f, "Key time") : null));
                            return;
                        case "save": args.Context.AddString("OK: " + shots.Save(args.Length > 2 ? args[2] : null)); return;
                        default: throw new ArgumentException("Usage: " + ShotUsage);
                    }
                }
                catch (Exception error) { args.Context.AddString(CommandText.Error(error)); }
            }, optionsFetcher: () => new List<string> { "play", "stop", "status", "check", "preview", "solve", "measure", "render", "render-all", "new", "key", "save" });

            Command("ct_fov", "Field of view: ct_fov [degrees|reset]", args =>
            {
                if (GameCamera.m_instance == null) throw new InvalidOperationException("GameCamera is not ready.");
                if (args.Length > 1)
                    ShotPlayer.SetFov(args[1].ToLowerInvariant() == "reset" ? GameCamera.m_instance.m_fov : CommandText.Number(args[1], 1f, 160f, "Field of view"));
                return $"fov={GameCamera.m_instance.m_camera.fieldOfView.ToString("F1", CultureInfo.InvariantCulture)}";
            });

            Command("ct_grid", "Ground grid in a replay: ct_grid <on|off|status> [spacing] | ct_grid cell <name> | ct_grid probe <x> <z>", args =>
            {
                RequireReplay();
                string mode = args.Length > 1 ? args[1].ToLowerInvariant() : "status";
                if (mode == "cell") return FightOverlay.Instance.Cell(CommandText.Arg(args, 2, "ct_grid cell <name>"));
                if (mode == "probe") return FightOverlay.Probe(CommandText.Number(CommandText.Arg(args, 2, "ct_grid probe <x> <z>"), -100000f, 100000f, "x"), CommandText.Number(CommandText.Arg(args, 3, "ct_grid probe <x> <z>"), -100000f, 100000f, "z"));
                if (mode == "status") return FightOverlay.Instance.Status();
                float spacing = args.Length > 2 ? CommandText.Number(args[2], 0.25f, 5f, "Spacing") : 0f;
                return FightOverlay.Instance.SetGrid(CommandText.Switch(mode, FightOverlay.GridOn), spacing);
            });

            Command("ct_trails", "Actor trails in a replay: ct_trails <on|off|status> [user:id ...]", args =>
            {
                RequireReplay();
                string mode = args.Length > 1 ? args[1].ToLowerInvariant() : "status";
                if (mode == "status") return FightOverlay.Instance.Status();
                foreach (string actor in args.Args.Skip(2)) ReplayPlayer.Instance.Tracks.Get(actor);
                return FightOverlay.Instance.SetTrails(CommandText.Switch(mode, FightOverlay.TrailsOn), args.Args.Skip(2));
            });

        }

        /// <summary>ct_replay map [time | from-to] [sizeMeters] [pixels]</summary>
        internal static string Map(Terminal.ConsoleEventArgs args, int from)
        {
            float? time = null;
            (float, float)? range = null;
            if (args.Length > from && args[from] != "all")
            {
                string[] parts = args[from].Split('-');
                if (parts.Length == 2) range = (CommandText.Number(parts[0], 0f, 100000f, "From"), CommandText.Number(parts[1], 0f, 100000f, "To"));
                else time = CommandText.Number(args[from], 0f, 100000f, "Time");
            }
            float size = args.Length > from + 1 ? CommandText.Number(args[from + 1], 10f, 2000f, "Size") : 0f;
            int pixels = args.Length > from + 2 ? (int)CommandText.Number(args[from + 2], 256f, 8192f, "Pixels") : 2048;
            return FightOverlay.Instance.Map(time, range, size, pixels);
        }

        private static void RequireReplay()
        {
            if (!ReplayPlayer.IsPlaying) throw new InvalidOperationException("Open a replay first");
        }

        private static ShotPlayer.RenderOptions Options(Terminal.ConsoleEventArgs args, int from)
        {
            ShotPlayer.RenderOptions options = new();
            for (int index = from; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--fps": options.Fps = (int)CommandText.Number(CommandText.Arg(args, ++index, "--fps N"), 1, 240, "Frames per second"); break;
                    case "--supersample": options.Supersample = (int)CommandText.Number(CommandText.Arg(args, ++index, "--supersample N"), 1, 4, "Supersample"); break;
                    case "--video": options.Video = true; break;
                    case "--keep-frames": options.KeepFrames = true; break;
                    case "--no-audio": options.Audio = false; break;
                    case "--overlays": options.Overlays = true; break;
                    case "--fps-cap": options.FpsCap = (int)CommandText.Number(CommandText.Arg(args, ++index, "--fps-cap N"), 5, 1000, "Frame rate cap"); break;
                    case "--size": (options.Width, options.Height) = Size(CommandText.Arg(args, ++index, "--size WxH")); break;
                    default: throw new ArgumentException($"Unknown render option {args[index]}");
                }
            }
            return options;
        }

        private static (int Width, int Height) Size(string text)
        {
            string[] size = text.ToLowerInvariant().Split('x');
            if (size.Length != 2) throw new ArgumentException("--size is WIDTHxHEIGHT, like 1920x1080");
            return ((int)CommandText.Number(size[0], 160, 7680, "Width"), (int)CommandText.Number(size[1], 90, 4320, "Height"));
        }

        private static void Command(string name, string usage, Func<Terminal.ConsoleEventArgs, string> body)
        {
            new Terminal.ConsoleCommand(name, usage, args =>
            {
                try { args.Context.AddString("OK: " + body(args)); }
                catch (Exception error) { args.Context.AddString(CommandText.Error(error)); }
            });
        }
    }
}
