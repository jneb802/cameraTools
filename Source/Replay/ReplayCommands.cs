using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace cameraTools.Replay
{
    public static class ReplayCommands
    {
        public static void Register()
        {
            new Terminal.ConsoleCommand(
                "record",
                "Recording: record [name] | record stop",
                RunRecord,
                optionsFetcher: GetRecordOptions
            );

            new Terminal.ConsoleCommand(
                "replay",
                "Replay: replay <file> | replay list | replay exit",
                RunReplay,
                optionsFetcher: GetReplayOptions
            );
        }

        private static void RunRecord(Terminal.ConsoleEventArgs args)
        {
            // "record stop" — explicitly stop
            if (args.Length >= 2 && args[1].ToLower() == "stop")
            {
                if (!ReplayRecorder.IsRecording)
                {
                    args.Context.AddString("Not currently recording.");
                    return;
                }
                ReplayRecorder.Instance.StopRecording();
                args.Context.AddString($"Recording saved: {ReplayRecorder.Instance.RecordingName}.valreplay");
                return;
            }

            // Toggle: if already recording, stop and save
            if (ReplayRecorder.IsRecording)
            {
                ReplayRecorder.Instance.StopRecording();
                args.Context.AddString($"Recording saved: {ReplayRecorder.Instance.RecordingName}.valreplay");
                return;
            }

            // Start recording with optional custom name
            string? name = args.Length >= 2 ? args[1] : null;
            ReplayRecorder.Instance.StartRecording(name);
            args.Context.AddString($"Recording started: {ReplayRecorder.Instance.RecordingName}");
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
                    ReplayPlayer.Instance.StopPlayback();
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
                terminal.AddString($"  {name} ({info.Length / 1024}KB)");
            }
        }

        private static void PlayReplay(Terminal terminal, string name)
        {
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
            return new List<string> { "stop" };
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
