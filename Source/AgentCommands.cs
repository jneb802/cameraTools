using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx.Configuration;
using cameraTools.Replay;

namespace cameraTools
{
    /// <summary>
    /// Console commands for the panel controls that had none, the settings, and a command reference,
    /// so an agent can run every camera and replay control without the menus.
    /// </summary>
    internal static class AgentCommands
    {
        private static readonly string[] Reference =
        {
            "cameraTools commands. Replies: 'OK:' done; 'ACCEPTED:' started, poll the named status command; 'PENDING:' still running;",
            "'ERROR: invalid-argument:' bad input, nothing changed; 'ERROR: unavailable:' not possible in this game state, nothing changed.",
            "Coordinates are Valheim world meters (x y z). Times are seconds. Actors are user:id from ct_replay targets.",
            "ct_help - this reference.",
            "ct_ui <show|hide|toggle|status> [camera|replay] - the Camera Tools window (F6) and its tab. The Replay tab exists only while a replay plays.",
            "ct_freefly <on|off|toggle|status> - free camera. Needs a loaded world or replay.",
            "ct_hud <show|hide|toggle|status> - game HUD.",
            "ct_follow <on|off|toggle|status> - the player follows the free camera.",
            "ct_smooth [0-1] - free camera smoothness; no value reports it.",
            "ct_frame <on|off|toggle|status> - 9:16 crop guide lines.",
            "ct_prepare [x y z] - free camera on, HUD off, then status relative to the origin.",
            "ct_status [x y z] - camera position, distance, angles and aim error relative to the origin (default 0 0 0).",
            "ct_place <cameraX cameraY cameraZ> <lookX lookY lookZ> - place the camera and aim it.",
            "ct_orbit <start|stop|status> [duration=20] [degrees=360] [x y z] - orbit the origin from the current camera position.",
            "ct_move <start|stop|status> [duration startX startY startZ endX endY endZ] [lookX lookY lookZ] [linear|ease|in|out] - straight move.",
            "ct_screenshot [filename] - PNG into BepInEx/config/cameraTools/screenshots.",
            "ct_settings list | get <Section.Key> | set <Section.Key> <value> - cameraTools config; set saves the file.",
            "record [name] | record start [name] - start a take (in a world, not during a replay). record stop - save it (ACCEPTED; poll record status until state=idle).",
            "record status - state=idle|recording|saving with name, seconds and memory estimate.",
            "replay list | replay info <file> | replay folder - recordings and their details (any state).",
            "replay <file> - main menu: opens a session (ACCEPTED; poll ct_replay state until state=session). In a world: plays over the world.",
            "replay browser <open|close|status> - the main menu Replays window. replay exit - leave the replay.",
            "ct_replay state - state=menu|loading|world|session|overlay, playing, browser and recording; answers in any state.",
            "ct_replay status - time, duration, paused, speed, camera, target, music and panel state. The commands below need a playing replay.",
            "ct_replay play | pause | seek <0-duration> | speed <0.1-4> - playback.",
            "ct_replay targets - every recorded actor as user:id kind prefab. ct_replay target [user:id] - select or report the target.",
            "ct_replay view [user:id] <aim|attach|front|third|projectile> - aim at or attach the camera to the target. ct_replay detach - free camera.",
            "ct_replay find [user:id] - pause and seek to where the target first appears.",
            "ct_replay recorded [on|off|status] - follow the camera used while recording. ct_replay follow <user:id> <x> <y> <z> [local] - fixed offset.",
            "ct_replay music [index] | volume <0-1> - replay music. ct_replay ui [show|hide|toggle|status] - the window on its Replay tab.",
            "ct_replay media - recorded effects, sounds and deaths. ct_replay exit - leave the replay (session: ACCEPTED, poll state until menu).",
            "ct_replay events [death|attack|damage] [user:id] - timestamped event tags of the playing replay.",
            "replay events <file> - writes <file>.events.json next to the recording (ACCEPTED; repeat until OK). New takes write it when they save."
        };

        internal static void Register()
        {
            new Terminal.ConsoleCommand("ct_help", "List every cameraTools command with its arguments", args =>
            {
                foreach (string line in Reference) args.Context.AddString(line);
            });

            Command("ct_ui", "Window visibility and tab: ct_ui <show|hide|toggle|status> [camera|replay]", args =>
            {
                string mode = args.Length > 1 ? args[1].ToLowerInvariant() : "status";
                if (mode != "status") CameraToolsPanel.Visible = CommandText.Switch(mode, CameraToolsPanel.Visible);
                if (args.Length > 2)
                {
                    string tab = args[2].ToLowerInvariant();
                    if (tab == "replay" && !ReplayPlayer.IsPlaying) throw new InvalidOperationException("The Replay tab exists only while a replay plays");
                    if (tab != "camera" && tab != "replay") throw new ArgumentException("Use camera or replay");
                    CameraToolsPanel.Tab = tab == "replay" ? CameraToolsPanel.PanelTab.Replay : CameraToolsPanel.PanelTab.Camera;
                }
                return $"panel={CameraToolsPanel.Visible} tab={CameraToolsPanel.Tab.ToString().ToLowerInvariant()} replayTab={ReplayPlayer.IsPlaying}";
            });

            Command("ct_follow", "Player follows the free camera: ct_follow <on|off|toggle|status>", args =>
            {
                if (args.Length > 1 && args[1].ToLowerInvariant() != "status") PlayerFollowPatch.FollowEnabled = CommandText.Switch(args[1], PlayerFollowPatch.FollowEnabled);
                return $"follow={PlayerFollowPatch.FollowEnabled}";
            });

            Command("ct_smooth", "Free camera smoothness: ct_smooth [0-1]", args =>
            {
                if (GameCamera.instance == null) throw new InvalidOperationException("GameCamera is not ready.");
                if (args.Length > 1) GameCamera.instance.SetFreeFlySmoothness(CommandText.Number(args[1], 0f, 1f, "Smoothness"));
                return $"smoothness={GameCamera.instance.GetFreeFlySmoothness().ToString("F2", CultureInfo.InvariantCulture)}";
            });

            Command("ct_frame", "9:16 crop guide: ct_frame <on|off|toggle|status>", args =>
            {
                if (args.Length > 1 && args[1].ToLowerInvariant() != "status") CameraToolsPanel.FrameGuide = CommandText.Switch(args[1], CameraToolsPanel.FrameGuide);
                return $"frameGuide={CameraToolsPanel.FrameGuide}";
            });

            new Terminal.ConsoleCommand("ct_settings", "cameraTools config: ct_settings list | get <Section.Key> | set <Section.Key> <value>", args =>
            {
                try
                {
                    ConfigFile config = cameraToolsPlugin.Instance.Config;
                    string action = args.Length > 1 ? args[1].ToLowerInvariant() : "list";
                    if (action == "list")
                    {
                        args.Context.AddString($"OK: settings={config.Count}");
                        foreach (ConfigDefinition key in config.Keys.OrderBy(key => key.Section).ThenBy(key => key.Key))
                            args.Context.AddString(Describe(key, config[key]));
                        return;
                    }
                    if (action != "get" && action != "set") throw new ArgumentException("Usage: ct_settings list | get <Section.Key> | set <Section.Key> <value>");
                    string name = CommandText.Arg(args, 2, $"ct_settings {action} <Section.Key>");
                    ConfigDefinition definition = config.Keys.FirstOrDefault(key => string.Equals($"{key.Section}.{key.Key}", name, StringComparison.OrdinalIgnoreCase))
                        ?? throw new ArgumentException($"No setting {name}; use ct_settings list");
                    ConfigEntryBase entry = config[definition];
                    if (action == "set")
                    {
                        string value = string.Join(" ", Enumerable.Range(3, Math.Max(0, args.Length - 3)).Select(index => args[index]));
                        if (value.Length == 0) throw new ArgumentException($"Usage: ct_settings set {name} <value>");
                        object parsed;
                        try { parsed = TomlTypeConverter.ConvertToValue(value, entry.SettingType); }
                        catch (Exception error) { throw new ArgumentException($"'{value}' is not a valid {entry.SettingType.Name}: {error.Message}"); }
                        if (entry.Description.AcceptableValues != null && !entry.Description.AcceptableValues.IsValid(parsed))
                            throw new ArgumentException($"'{value}' is outside the allowed values: {entry.Description.AcceptableValues.ToDescriptionString()}");
                        entry.BoxedValue = parsed;
                        config.Save();
                    }
                    args.Context.AddString("OK: " + Describe(definition, entry));
                }
                catch (Exception error) { args.Context.AddString(CommandText.Error(error)); }
            });
        }

        private static string Describe(ConfigDefinition key, ConfigEntryBase entry)
        {
            string allowed = entry.Description.AcceptableValues != null ? " allowed=\"" + entry.Description.AcceptableValues.ToDescriptionString().Replace("# ", "") + "\"" : "";
            return $"{key.Section}.{key.Key}={entry.GetSerializedValue()} type={entry.SettingType.Name} default={TomlTypeConverter.ConvertToString(entry.DefaultValue, entry.SettingType)}{allowed}";
        }

        /// <summary>Registers a command whose body returns the state line; errors become ERROR replies.</summary>
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
