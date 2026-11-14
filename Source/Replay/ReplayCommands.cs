using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using UnityEngine;

namespace cameraTools.Replay
{
    public static class ReplayCommands
    {
        public static void Register()
        {
            new Terminal.ConsoleCommand("ct_replay", "ct_replay status|media|targets|pause|play|seek <seconds>|speed <0.1-4>|detach|recorded|follow <user:id> <x> <y> <z> [local]|view <user:id> <aim|attach|front|third|projectile>|music [index]|exit", args =>
            {
                try
                {
                    ReplayPlayer player = ReplayPlayer.Instance;
                    if (!ReplayPlayer.IsPlaying) { args.Context.AddString("ERROR: Start a replay first"); return; }
                    string action = args.Length > 1 ? args[1].ToLowerInvariant() : "status";
                    switch (action)
                    {
                        case "status": args.Context.AddString($"time={player.PlaybackTime:F3} duration={player.Duration:F3} paused={player.IsPaused} speed={player.PlaybackSpeed:F2} session={player.Isolated} recordedCamera={player.FollowingRecordedCamera} camera={player.CameraRig?.Status} music={player.Music?.Selected} scene={player.SceneSummary}"); break;
                        case "recorded": player.FollowRecordedCamera(); break;
                        case "exit":
                            if (player.Isolated) ReplaySession.Exit(); else player.StopPlayback();
                            break;
                        case "view":
                            string[] targetId = args[2].Split(':');
                            player.CameraRig!.Select(long.Parse(targetId[0]), uint.Parse(targetId[1]));
                            player.CameraRig.SetView(args[3]);
                            break;
                        case "find":
                            string[] findId = args[2].Split(':');
                            player.CameraRig!.Select(long.Parse(findId[0]), uint.Parse(findId[1]));
                            player.CameraRig.SeekToSelected();
                            break;
                        case "music":
                            if (args.Length < 3) args.Context.AddString(string.Join("; ", player.Music!.Labels.Select((label, index) => $"{index}: {label}")));
                            else player.Music!.Select(int.Parse(args[2]));
                            break;
                        case "targets": args.Context.AddString(player.Targets()); break;
                        case "media":
                            foreach (string line in player.Media().Split('\n')) args.Context.AddString(line);
                            break;
                        case "pause": player.SetPaused(true); break;
                        case "play": player.SetPaused(false); break;
                        case "detach": player.Detach(); break;
                        case "ui":
                            CameraToolsPanel.Visible = !CameraToolsPanel.Visible;
                            if (CameraToolsPanel.Visible) CameraToolsPanel.Tab = CameraToolsPanel.PanelTab.Replay;
                            break;
                        case "seek": player.Seek(Number(args[2])); break;
                        case "speed": player.SetSpeed(Number(args[2])); break;
                        case "follow":
                            string[] id = args[2].Split(':');
                            player.Follow(long.Parse(id[0]), uint.Parse(id[1]), new Vector3(Number(args[3]), Number(args[4]), Number(args[5])), args.Length > 6 && args[6] == "local");
                            break;
                        default: throw new System.ArgumentException("Unknown replay action");
                    }
                    args.Context.AddString("OK: " + action);
                }
                catch (System.Exception error) { args.Context.AddString("ERROR: " + error.Message); }
            });
            new Terminal.ConsoleCommand(
                "record",
                "Recording: record [name] | record stop | record status",
                RunRecord,
                optionsFetcher: GetRecordOptions
            );

            new Terminal.ConsoleCommand(
                "replay",
                "Replay: replay <file> (from the main menu: opens it on its own; in a world: plays it over the world) | replay list | replay exit",
                RunReplay,
                optionsFetcher: GetReplayOptions
            );
        }

        private static float Number(string value)
        {
            float number = float.Parse(value, CultureInfo.InvariantCulture);
            if (float.IsNaN(number) || float.IsInfinity(number)) throw new System.ArgumentException("Use a finite number");
            return number;
        }

        private static void RunRecord(Terminal.ConsoleEventArgs args)
        {
            if (args.Length >= 2 && args[1].ToLowerInvariant() == "status")
            { args.Context.AddString(ReplayRecorder.Instance.Status); return; }
            // "record stop" — explicitly stop
            if (args.Length >= 2 && args[1].ToLower() == "stop")
            {
                if (!ReplayRecorder.IsRecording)
                {
                    args.Context.AddString(ReplayRecorder.Instance.Status);
                    return;
                }
                ReplayRecorder.Instance.StopRecording();
                args.Context.AddString(ReplayRecorder.Instance.Status);
                return;
            }

            // Toggle: if already recording, stop and save
            if (ReplayRecorder.IsRecording)
            {
                ReplayRecorder.Instance.StopRecording();
                args.Context.AddString(ReplayRecorder.Instance.Status);
                return;
            }

            // Start recording with optional custom name
            string? name = args.Length >= 2 ? args[1] : null;
            if (ReplayRecorder.Instance.StartRecording(name))
                args.Context.AddString($"Recording started: {ReplayRecorder.Instance.RecordingName}");
            else args.Context.AddString("Cannot start recording while a replay or save is active. " + ReplayRecorder.Instance.Status);
        }

        private static void RunReplay(Terminal.ConsoleEventArgs args)
        {
            if (args.Length < 2)
            {
                args.Context.AddString("Usage: replay <file> | list | exit");
                return;
            }

            string sub = args[1].ToLower();

            switch (sub)
            {
                case "list":
                    ListReplays(args.Context);
                    break;

                case "exit":
                    if (!ReplayPlayer.IsPlaying)
                    {
                        args.Context.AddString("No replay is currently playing.");
                        return;
                    }
                    if (ReplayPlayer.Instance.Isolated) ReplaySession.Exit();
                    else ReplayPlayer.Instance.StopPlayback();
                    args.Context.AddString("Replay stopped.");
                    break;

                default:
                    PlayReplay(args.Context, sub);
                    break;
            }
        }

        private static void ListReplays(Terminal terminal)
        {
            var dir = ReplayFileIO.GetReplayDirectory();
            var files = Directory.GetFiles(dir, "*.valreplay");
            if (files.Length == 0)
            {
                terminal.AddString("No replay files found.");
                return;
            }

            terminal.AddString($"Replay files ({files.Length}):");
            foreach (var file in files)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var info = new FileInfo(file);
                string standalone;
                try { standalone = ReplayFileIO.ReadInfo(file).Info.CanLoadAlone ? "opens on its own" : "needs its world"; }
                catch (System.Exception error) { standalone = "unreadable: " + error.Message; }
                terminal.AddString($"  {name} ({info.Length / 1024}KB, {standalone})");
            }
        }

        private static void PlayReplay(Terminal terminal, string name)
        {
            if (ReplayRecorder.Instance.IsSaving)
            { terminal.AddString(ReplayRecorder.Instance.Status); return; }
            var path = Path.Combine(ReplayFileIO.GetReplayDirectory(), name + ".valreplay");
            if (!File.Exists(path))
            {
                // Try exact path in case they included the extension
                path = Path.Combine(ReplayFileIO.GetReplayDirectory(), name);
                if (!File.Exists(path))
                {
                    terminal.AddString($"Replay file not found: {name}");
                    return;
                }
            }

            try
            {
                if (FejdStartup.instance != null)
                {
                    string error = ReplayBrowser.OpenSession(path);
                    terminal.AddString(error.Length > 0 ? "Failed to open replay: " + error : $"Opening replay session: {name}");
                    return;
                }
                var replay = ReplayFileIO.Read(path);
                ReplayPlayer.Instance.StartPlayback(replay);
                terminal.AddString($"Playing replay: {name} ({replay.Duration:F1}s)");
            }
            catch (System.Exception ex)
            {
                terminal.AddString($"Failed to load replay: {ex.Message}");
            }
        }

        private static List<string> GetRecordOptions()
        {
            return new List<string> { "stop", "status" };
        }

        private static List<string> GetReplayOptions()
        {
            var options = new List<string> { "list", "exit" };

            var dir = ReplayFileIO.GetReplayDirectory();
            if (Directory.Exists(dir))
            {
                var files = Directory.GetFiles(dir, "*.valreplay")
                    .Select(f => Path.GetFileNameWithoutExtension(f));
                options.AddRange(files);
            }

            return options;
        }
    }
}
