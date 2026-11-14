using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace cameraTools.Replay
{
    public static class ReplayCommands
    {
        internal const string ReplayUsage = "ct_replay state|status|media|targets|events [death|attack|damage] [user:id]|target [user:id]|find [user:id]|view [user:id] <aim|attach|front|third|projectile>|detach|recorded [on|off|status]|follow <user:id> <x> <y> <z> [local]|pause|play|seek <seconds>|speed <0.1-4>|music [index]|volume <0-1>|ui [show|hide|toggle|status]|exit";
        internal const string RecordUsage = "record [name] | record start [name] | record stop | record status";
        internal const string FileUsage = "replay <file> | replay list | replay info <file> | replay events <file> | replay folder | replay browser <open|close|status> | replay exit";

        public static void Register()
        {
            new Terminal.ConsoleCommand("ct_replay", ReplayUsage, args =>
            {
                try
                {
                    string action = args.Length > 1 ? args[1].ToLowerInvariant() : "status";
                    // These answer in any game state, so an agent can wait for a session to finish loading.
                    if (action == "state") { args.Context.AddString("OK: " + State()); return; }
                    if (!ReplayPlayer.IsPlaying) throw new InvalidOperationException("Start a replay first. " + State());
                    ReplayPlayer player = ReplayPlayer.Instance;
                    ReplayCameraRig rig = player.CameraRig!;
                    switch (action)
                    {
                        case "status": args.Context.AddString("OK: " + Status(player)); return;
                        case "recorded":
                            string mode = args.Length > 2 ? args[2].ToLowerInvariant() : "on";
                            if (mode != "status")
                            {
                                if (CommandText.Switch(mode, player.FollowingRecordedCamera)) player.FollowRecordedCamera();
                                else if (player.FollowingRecordedCamera) player.Detach();
                            }
                            args.Context.AddString($"OK: recordedCamera={player.FollowingRecordedCamera} available={player.HasRecordedCamera}");
                            return;
                        case "exit":
                            bool isolated = player.Isolated;
                            if (isolated) ReplaySession.Exit(); else player.StopPlayback();
                            args.Context.AddString(isolated ? "ACCEPTED: returning to the main menu; poll ct_replay state until state=menu" : "OK: replay stopped");
                            return;
                        case "target":
                            if (args.Length > 2) Select(rig, args[2]);
                            args.Context.AddString($"OK: target={rig.SelectedActor} label=\"{(rig.Labels.Count > 0 ? rig.Labels[rig.Selected] : "")}\"");
                            return;
                        case "view":
                            if (args.Length > 3) Select(rig, args[2]);
                            rig.SetView(CommandText.Arg(args, args.Length > 3 ? 3 : 2, "ct_replay view [user:id] <aim|attach|front|third|projectile>").ToLowerInvariant());
                            args.Context.AddString($"OK: target={rig.SelectedActor} camera=\"{rig.Status}\"");
                            return;
                        case "find":
                            if (args.Length > 2) Select(rig, args[2]);
                            rig.SeekToSelected();
                            args.Context.AddString($"OK: target={rig.SelectedActor} time={player.PlaybackTime:F3} paused={player.IsPaused}");
                            return;
                        case "music":
                            ReplayMusic music = player.Music!;
                            if (args.Length > 2)
                            {
                                int index = (int)CommandText.Number(args[2], 0, music.Labels.Count - 1, "Music index");
                                music.Select(index);
                            }
                            args.Context.AddString($"OK: music={music.Selected} label=\"{music.Labels[music.Selected]}\" volume={music.Volume:F2}");
                            args.Context.AddString(string.Join("; ", music.Labels.Select((label, index) => $"{index}: {label}")));
                            return;
                        case "volume":
                            player.Music!.Volume = CommandText.Number(CommandText.Arg(args, 2, "ct_replay volume <0-1>"), 0, 1, "Music volume");
                            args.Context.AddString($"OK: volume={player.Music.Volume:F2}");
                            return;
                        case "ui":
                            if (args.Length > 2 && args[2].ToLowerInvariant() != "status") ReplayTimelinePanel.Visible = CommandText.Switch(args[2], ReplayTimelinePanel.Visible);
                            else if (args.Length <= 2) ReplayTimelinePanel.Visible = !ReplayTimelinePanel.Visible;
                            args.Context.AddString($"OK: replayPanel={ReplayTimelinePanel.Visible}");
                            return;
                        case "targets": args.Context.AddString("OK: " + player.Targets()); return;
                        case "events":
                            foreach (string line in EventLines(player.Events, args)) args.Context.AddString(line);
                            return;
                        case "media":
                            foreach (string line in player.Media().Split('\n')) args.Context.AddString(line);
                            args.Context.AddString("OK: media");
                            return;
                        case "pause": player.SetPaused(true); break;
                        case "play": player.SetPaused(false); break;
                        case "detach": player.Detach(); break;
                        case "seek": player.Seek(CommandText.Number(CommandText.Arg(args, 2, "ct_replay seek <seconds>"), 0, player.Duration, "Seek time")); break;
                        case "speed": player.SetSpeed(CommandText.Number(CommandText.Arg(args, 2, "ct_replay speed <0.1-4>"), 0.1f, 4f, "Speed")); break;
                        case "follow":
                            (long user, uint id) = CommandText.Actor(CommandText.Arg(args, 2, "ct_replay follow <user:id> <x> <y> <z> [local]"));
                            Vector3 offset = new(Coordinate(args, 3), Coordinate(args, 4), Coordinate(args, 5));
                            player.Follow(user, id, offset, args.Length > 6 && args[6].ToLowerInvariant() == "local");
                            break;
                        default: throw new ArgumentException("Unknown replay action. Usage: " + ReplayUsage);
                    }
                    args.Context.AddString($"OK: {action} " + Status(player));
                }
                catch (Exception error) { args.Context.AddString(CommandText.Error(error)); }
            });
            new Terminal.ConsoleCommand("record", "Recording: " + RecordUsage, RunRecord, optionsFetcher: () => new List<string> { "start", "stop", "status" });
            new Terminal.ConsoleCommand(
                "replay",
                "Replay: replay <file> (from the main menu: opens it on its own; in a world: plays it over the world) | replay list | replay info <file> | replay events <file> (writes <file>.events.json) | replay folder | replay browser <open|close|status> | replay exit",
                RunReplay,
                optionsFetcher: GetReplayOptions
            );
        }

        /// <summary>Where the game and cameraTools are, in one line that answers in every game state.</summary>
        internal static string State()
        {
            string state = ReplayPlayer.IsPlaying ? (ReplayPlayer.Instance.Isolated ? "session" : "overlay")
                : ReplaySession.Active || ReplayBrowser.Loading ? "loading"
                : FejdStartup.instance != null ? "menu"
                : Player.m_localPlayer != null ? "world" : "loading";
            return $"state={state} playing={ReplayPlayer.IsPlaying} browser={(ReplayBrowser.Visible ? "open" : "closed")} recording={ReplayRecorder.Instance.State}";
        }

        private static string Status(ReplayPlayer player) =>
            $"time={player.PlaybackTime:F3} duration={player.Duration:F3} paused={player.IsPaused} speed={player.PlaybackSpeed:F2} session={player.Isolated} " +
            $"recordedCamera={player.FollowingRecordedCamera} target={player.CameraRig?.SelectedActor} camera=\"{player.CameraRig?.Status}\" " +
            $"music={player.Music?.Selected} volume={player.Music?.Volume:F2} replayPanel={ReplayTimelinePanel.Visible} scene=\"{player.SceneSummary.Replace('\n', ' ')}\"";

        private static void Select(ReplayCameraRig rig, string actor)
        {
            (long user, uint id) = CommandText.Actor(actor);
            rig.Select(user, id);
        }

        private static float Coordinate(Terminal.ConsoleEventArgs args, int index) =>
            CommandText.Number(CommandText.Arg(args, index, "ct_replay follow <user:id> <x> <y> <z> [local]"), -100000f, 100000f, "Offset");

        private static void RunRecord(Terminal.ConsoleEventArgs args)
        {
            ReplayRecorder recorder = ReplayRecorder.Instance;
            string action = args.Length >= 2 ? args[1].ToLowerInvariant() : "toggle";
            if (action == "status") { args.Context.AddString("OK: " + recorder.StatusLine); return; }
            bool stop = action == "stop" || (action == "toggle" && ReplayRecorder.IsRecording);
            if (stop)
            {
                if (!ReplayRecorder.IsRecording) { args.Context.AddString("ERROR: unavailable: Not recording. " + recorder.StatusLine); return; }
                recorder.StopRecording();
                args.Context.AddString("ACCEPTED: saving; poll 'record status' until state=idle. " + recorder.StatusLine);
                return;
            }
            if (ReplayRecorder.IsRecording) { args.Context.AddString("ERROR: unavailable: Already recording. " + recorder.StatusLine); return; }
            // "record <name>" and "record start [name]" both start a take.
            string? name = action == "start" ? (args.Length >= 3 ? args[2] : null) : action == "toggle" ? null : args[1];
            if (name != null && name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { args.Context.AddString($"ERROR: invalid-argument: '{name}' is not a valid file name"); return; }
            if (recorder.StartRecording(name)) args.Context.AddString("OK: " + recorder.StatusLine);
            else args.Context.AddString("ERROR: unavailable: Cannot start recording while a replay or save is active. " + recorder.StatusLine);
        }

        private static void RunReplay(Terminal.ConsoleEventArgs args)
        {
            if (args.Length < 2) { args.Context.AddString("ERROR: invalid-argument: Usage: " + FileUsage); return; }
            try
            {
                switch (args[1].ToLowerInvariant())
                {
                    case "list": ListReplays(args.Context); return;
                    case "folder": args.Context.AddString("OK: folder=" + ReplayFileIO.GetReplayDirectory()); return;
                    case "events": ExportEvents(args.Context, Find(CommandText.Arg(args, 2, "replay events <file>"))); return;
                    case "info":
                        foreach (string line in Info(Find(CommandText.Arg(args, 2, "replay info <file>")))) args.Context.AddString(line);
                        return;
                    case "browser":
                        string mode = args.Length > 2 ? args[2].ToLowerInvariant() : "status";
                        if (mode == "open")
                        {
                            if (FejdStartup.instance == null || ReplaySession.Active) throw new InvalidOperationException("The replay browser opens from the main menu");
                            ReplayBrowser.Open();
                        }
                        else if (mode == "close") ReplayBrowser.Close();
                        else if (mode != "status") throw new ArgumentException("Usage: replay browser <open|close|status>");
                        args.Context.AddString($"OK: browser={(ReplayBrowser.Visible ? "open" : "closed")}");
                        return;
                    case "exit":
                        if (!ReplayPlayer.IsPlaying) throw new InvalidOperationException("No replay is playing");
                        bool session = ReplayPlayer.Instance.Isolated;
                        if (session) ReplaySession.Exit(); else ReplayPlayer.Instance.StopPlayback();
                        args.Context.AddString(session ? "ACCEPTED: returning to the main menu; poll ct_replay state until state=menu" : "OK: replay stopped");
                        return;
                    default: PlayReplay(args.Context, args[1]); return;
                }
            }
            catch (Exception error) { args.Context.AddString(CommandText.Error(error)); }
        }

        /// <summary>Finds a recording by file name, with or without the extension and in any letter case.</summary>
        internal static string Find(string name)
        {
            string directory = ReplayFileIO.GetReplayDirectory();
            string wanted = name.EndsWith(".valreplay", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - ".valreplay".Length) : name;
            string exact = Path.Combine(directory, wanted + ".valreplay");
            if (File.Exists(exact)) return exact;
            string[] matches = Directory.GetFiles(directory, "*.valreplay")
                .Where(path => string.Equals(Path.GetFileNameWithoutExtension(path), wanted, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 1) return matches[0];
            if (matches.Length > 1) throw new ArgumentException($"Several recordings match '{name}' when letter case is ignored; use the exact name");
            throw new ArgumentException($"Replay file not found: {name}. Use replay list");
        }

        /// <summary>The summary, then one line per tag, optionally limited to one type and one actor.</summary>
        private static IEnumerable<string> EventLines(ReplayEventList list, Terminal.ConsoleEventArgs args)
        {
            ReplayEventType? type = null;
            string? actor = null;
            for (int index = 2; index < args.Length; index++)
            {
                if (ReplayEvents.TryParse(args[index], out ReplayEventType parsed)) type = parsed;
                else actor = $"{CommandText.Actor(args[index]).User}:{CommandText.Actor(args[index]).Id}";
            }
            yield return "OK: " + string.Join(" | ", ReplayEvents.Summary(list));
            foreach (ReplayEvent tag in list.Events)
                if ((type == null || tag.Type == type) && (actor == null || tag.Actor == actor)) yield return ReplayEvents.Line(tag);
        }

        private static readonly Dictionary<string, Task> EventExports = new();

        // Reading a large recording takes seconds, so the file is read and written in the background.
        private static void ExportEvents(Terminal terminal, string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            string json = ReplayEvents.JsonPath(path);
            if (EventExports.TryGetValue(path, out Task export))
            {
                if (!export.IsCompleted) { terminal.AddString($"PENDING: writing {json}"); return; }
                EventExports.Remove(path);
                if (export.IsFaulted) throw new InvalidOperationException("Could not write the events: " + export.Exception?.GetBaseException().Message);
            }
            if (File.Exists(json) && File.GetLastWriteTimeUtc(json) >= File.GetLastWriteTimeUtc(path))
            {
                terminal.AddString($"OK: events json={json}");
                JObject document = JObject.Parse(File.ReadAllText(json));
                foreach (JProperty type in ((JObject?)document["types"])?.Properties() ?? Enumerable.Empty<JProperty>())
                    terminal.AddString($"type {type.Name} {type.Value.ToString(Newtonsoft.Json.Formatting.None)}");
                return;
            }
            HashSet<string> attacks = ReplayEvents.AttackAnimations();
            EventExports[path] = Task.Run(() => ReplayEvents.WriteJson(json, ReplayEvents.Build(ReplayFileIO.Read(path), attacks)));
            terminal.AddString($"ACCEPTED: writing {json}; run 'replay events {name}' again until it reports OK");
        }

        private static IEnumerable<string> Info(string path)
        {
            ReplayFile header = ReplayFileIO.ReadInfo(path);
            ReplayInfo info = header.Info;
            List<string> missing = new(), changed = new();
            ReplayBrowser.CompareMods(info, missing, changed);
            yield return $"OK: name={Path.GetFileNameWithoutExtension(path)} bytes={new FileInfo(path).Length} duration={info.Duration.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"created={DateTimeOffset.FromUnixTimeSeconds(info.CreatedUnix).UtcDateTime:yyyy-MM-ddTHH:mm:ssZ} opensAlone={info.CanLoadAlone}";
            if (!info.CanLoadAlone) { yield return "needsWorld=true: play it with 'replay <file>' inside its own world"; yield break; }
            yield return $"world=\"{info.WorldName}\" seed={info.SeedName} zones={info.Zones} worldObjects={info.SceneObjects} actors={info.Actors}";
            yield return $"gameVersion={info.GameVersion} currentGameVersion={(global::Version.GetVersionString())}";
            yield return $"missingMods={missing.Count}{(missing.Count > 0 ? " " + string.Join(", ", missing) : "")}";
            yield return $"changedMods={changed.Count}{(changed.Count > 0 ? " " + string.Join(", ", changed) : "")}";
        }

        private static void ListReplays(Terminal terminal)
        {
            string[] files = Directory.GetFiles(ReplayFileIO.GetReplayDirectory(), "*.valreplay");
            terminal.AddString($"OK: replays={files.Length}");
            foreach (string file in files.OrderBy(Path.GetFileName))
            {
                string standalone;
                try { standalone = ReplayFileIO.ReadInfo(file).Info.CanLoadAlone ? "opens on its own" : "needs its world"; }
                catch (Exception error) { standalone = "unreadable: " + error.Message; }
                terminal.AddString($"  {Path.GetFileNameWithoutExtension(file)} ({new FileInfo(file).Length / 1024}KB, {standalone})");
            }
        }

        private static void PlayReplay(Terminal terminal, string name)
        {
            if (ReplayRecorder.Instance.IsSaving) throw new InvalidOperationException("Wait for the recording to save. " + ReplayRecorder.Instance.StatusLine);
            if (ReplayPlayer.IsPlaying) throw new InvalidOperationException("A replay is already playing; use replay exit first");
            string path = Find(name);
            string file = Path.GetFileNameWithoutExtension(path);
            if (FejdStartup.instance != null)
            {
                string error = ReplayBrowser.OpenSession(path);
                if (error.Length > 0) throw new InvalidOperationException("Failed to open replay: " + error);
                terminal.AddString($"ACCEPTED: opening replay session {file}; poll ct_replay state until state=session");
                return;
            }
            ReplayFile replay = ReplayFileIO.Read(path);
            ReplayPlayer.Instance.StartPlayback(replay);
            if (!ReplayPlayer.IsPlaying) throw new InvalidOperationException("The replay did not start; see the log");
            terminal.AddString($"OK: playing replay {file} over the loaded world duration={replay.Duration.ToString("F1", CultureInfo.InvariantCulture)}");
        }

        private static List<string> GetReplayOptions()
        {
            List<string> options = new() { "list", "info", "events", "folder", "browser", "exit" };
            string directory = ReplayFileIO.GetReplayDirectory();
            if (Directory.Exists(directory)) options.AddRange(Directory.GetFiles(directory, "*.valreplay").Select(Path.GetFileNameWithoutExtension));
            return options;
        }
    }
}
