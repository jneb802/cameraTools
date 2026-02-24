using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace cameraTools.Replay
{
    public static class ReplayCommands
    {
        public static void Register()
        {
            var cmd = new Terminal.ConsoleCommand(
                "replay",
                "Replay system: replay <file> | replay list | replay exit | replay record | replay record stop <name>",
                Run,
                optionsFetcher: GetOptions
            );
        }

        private static void Run(Terminal.ConsoleEventArgs args)
        {
            if (args.Length < 2)
            {
                args.Context.AddString("Usage: replay <file> | list | exit | record [stop <name>]");
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

                case "record":
                    HandleRecord(args);
                    break;

                default:
                    // Treat as filename
                    PlayReplay(args.Context, sub);
                    break;
            }
        }

        private static void HandleRecord(Terminal.ConsoleEventArgs args)
        {
            if (args.Length >= 3 && args[2].ToLower() == "stop")
            {
                if (args.Length < 4)
                {
                    args.Context.AddString("Usage: replay record stop <name>");
                    return;
                }
                string name = args[3];
                ReplayRecorder.Instance.StopRecording(name);
                args.Context.AddString($"Recording saved: {name}.valreplay");
                return;
            }

            ReplayRecorder.Instance.StartRecording();
            args.Context.AddString("Recording started. Use 'replay record stop <name>' to save.");
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

        private static List<string> GetOptions()
        {
            var options = new List<string> { "list", "exit", "record" };

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
