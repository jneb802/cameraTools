using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace cameraTools.Staging
{
    /// <summary>ct_stage: place, hold, walk, face, attack and release creatures and the player, by hand or from a scene file.</summary>
    internal static class StageCommands
    {
        public static readonly string[] Reference =
        {
            "ct_stage run <scene> [record=<name>] [rate=30|60] - run BepInEx/config/cameraTools/scenes/<scene>.json: clear the area, place the actors, start each action on time (ACCEPTED; poll ct_stage status until state=idle).",
            "ct_stage stop - stop the scene (a recording it started is saved). ct_stage clear [radius] - remove staged creatures, free the player, and with a radius remove other creatures around the player.",
            "ct_stage status | log [lines] | list - scene state and every actor; what happened and when; the scene files.",
            "ct_stage spawn <id> <prefab> <x> <y|ground> <z> [facing=deg] [toward=<actor|marker|x,z>] [level=n] [health=n] [state=hold|sleep|free] - a held creature at a point.",
            "ct_stage take <id> <creature name> [radius=40] - stage the nearest creature of that name. ct_stage remove <id> - despawn a staged creature or free the player.",
            "ct_stage do <id|player> <hold|walk|face|attack|release|wake|sleep|place|equip|block|health> [key=value ...] - one action now. Keys: points=x,z;x,y,z path=<marker> to=<actor|marker|x,z> stop=m pace=walk|run|sprint then=hold|release",
            "  toward=<actor|marker|x,z> facing=deg attacks=name,name gap=s count=n hold=s secondary=true hunt=true|false target=<actor> at=x,y,z item=<prefab> value=<health> after=<label> delay=s.",
            "ct_stage attacks <id|prefab> - the attack names a creature can be told to use.",
            "ct_stage guide mark <id> at=<actor|marker|x,z> [radius=0.8] [toward=..|facing=deg] [label=text_with_underscores] [state=active|next|off] [trigger=enter|leave|stop] [on=<label>] - a spot to stand on.",
            "ct_stage guide ring <id> around=<actor|marker|x,z> radius=<m> [toward=..|facing=deg] [arc=70] [label=..] [state=..] [trigger=enter|leave|stop] [on=<label>] - where to stop, at a distance around an actor.",
            "ct_stage guide path <id> points=x,z;x,z;... | path=<marker> [label=..] [state=..] - a route to walk. ct_stage guide state <id> <active|next|off> | arm <id> | remove <id> | clear | list.",
            "  Guides show only on the live screen (never recorded, never in replays or renders). A trigger records its label; ct_stage do ... after=<label> [delay=s] waits for it."
        };

        internal static void Register()
        {
            new Terminal.ConsoleCommand("ct_stage", "Stage creatures and the player: ct_stage <run|stop|clear|status|log|list|spawn|take|remove|do|attacks|guide> ...", args =>
            {
                try
                {
                    foreach (string line in Run(args).Split('\n')) args.Context.AddString(line);
                }
                catch (Exception error) { args.Context.AddString(CommandText.Error(error)); }
            }, isCheat: false);
        }

        private static StageRunner Runner => StageRunner.Instance ?? throw new InvalidOperationException("The stage runner is not loaded");

        private static string Run(Terminal.ConsoleEventArgs args)
        {
            string mode = args.Length > 1 ? args[1].ToLowerInvariant() : "status";
            switch (mode)
            {
                case "run":
                {
                    string name = CommandText.Arg(args, 2, "ct_stage run <scene> [record=<name>] [rate=30|60]");
                    Dictionary<string, string> options = Options(args, 3);
                    int? rate = options.TryGetValue("rate", out string r) ? (int)CommandText.Number(r, 30, 60, "rate") : null;
                    return Runner.Run(StageScene.Load(name), options.TryGetValue("record", out string record) ? record : null, rate);
                }
                case "stop":
                    return "OK: " + Runner.Stop("stopped by command");
                case "clear":
                {
                    int removed = Runner.RemoveAll();
                    int cleared = 0;
                    if (args.Length > 2 && Player.m_localPlayer != null)
                        cleared = StageRunner.ClearArea(Player.m_localPlayer.transform.position, CommandText.Number(args[2], 1, 500, "radius"), drops: true);
                    int guides = StageGuides.Instance?.Clear() ?? 0;
                    return $"OK: removed {removed} staged actors, {cleared} other creatures and {guides} guides";
                }
                case "status":
                    return "OK: " + Runner.StatusLine() + string.Concat(Runner.Actors.Where(a => a != null).Select(a => "\n" + a.StatusLine()))
                           + string.Concat((StageGuides.Instance?.All ?? Enumerable.Empty<StageGuide>()).Select(g => "\n" + g.Describe()));
                case "guide":
                    return "OK: " + Guide(args);
                case "log":
                {
                    int lines = args.Length > 2 ? (int)CommandText.Number(args[2], 1, 400, "lines") : 60;
                    return "OK: " + Runner.StatusLine() + string.Concat(Runner.LogLines(lines).Select(l => "\n" + l));
                }
                case "list":
                {
                    if (!Directory.Exists(StageScene.Directory)) return $"OK: no scenes in {StageScene.Directory}";
                    string[] files = Directory.GetFiles(StageScene.Directory, "*.json").Select(Path.GetFileNameWithoutExtension).OrderBy(n => n).ToArray()!;
                    return $"OK: {files.Length} scenes in {StageScene.Directory}" + string.Concat(files.Select(f => "\n" + f));
                }
                case "spawn":
                {
                    const string usage = "ct_stage spawn <id> <prefab> <x> <y|ground> <z> [key=value ...]";
                    JObject json = OptionsJson(args, 7);
                    json["id"] = CommandText.Arg(args, 2, usage);
                    json["prefab"] = CommandText.Arg(args, 3, usage);
                    json["at"] = new JArray(
                        CommandText.Number(CommandText.Arg(args, 4, usage), -30000, 30000, "x"),
                        IsGround(CommandText.Arg(args, 5, usage)) ? JValue.CreateNull() : new JValue(CommandText.Number(args[5], -1000, 10000, "y")),
                        CommandText.Number(CommandText.Arg(args, 6, usage), -30000, 30000, "z"));
                    StagedActor actor = Runner.Create(StageActorSpec.Parse(json));
                    return "OK: " + actor.StatusLine();
                }
                case "take":
                {
                    const string usage = "ct_stage take <id> <creature name> [radius=40]";
                    JObject json = OptionsJson(args, 4);
                    json["id"] = CommandText.Arg(args, 2, usage);
                    json["take"] = CommandText.Arg(args, 3, usage);
                    StagedActor actor = Runner.Create(StageActorSpec.Parse(json));
                    return "OK: " + actor.StatusLine();
                }
                case "remove":
                {
                    StagedActor actor = Runner.Actor(CommandText.Arg(args, 2, "ct_stage remove <id>"));
                    Runner.Remove(actor);
                    return $"OK: removed {actor.Id}";
                }
                case "do":
                {
                    const string usage = "ct_stage do <id> <action> [key=value ...]";
                    JObject json = OptionsJson(args, 4);
                    json["actor"] = CommandText.Arg(args, 2, usage);
                    json["do"] = CommandText.Arg(args, 3, usage);
                    StageAction action = StageAction.Parse(json);
                    StagedActor actor = Runner.Actor(action.Actor);
                    if (action.After.Length > 0)
                    {
                        Runner.Queue(action);
                        return $"OK: queued {action.Describe()} after '{action.After}'{(action.Delay > 0f ? $" + {action.Delay.ToString("F1", CultureInfo.InvariantCulture)} s" : "")}; {Runner.PendingCount} waiting";
                    }
                    Runner.Apply(action);
                    return $"OK: {action.Describe()}; " + actor.StatusLine();
                }
                case "attacks":
                    return "OK: " + Attacks(CommandText.Arg(args, 2, "ct_stage attacks <id|prefab>"));
                default:
                    throw new ArgumentException("Use run, stop, clear, status, log, list, spawn, take, remove, do or attacks");
            }
        }

        private static string Attacks(string idOrPrefab)
        {
            List<ItemDrop.ItemData> items = new();
            string name = idOrPrefab;
            Character? live = StageRunner.FindActorCharacter(idOrPrefab);
            if (live is Humanoid humanoid)
            {
                items.AddRange(humanoid.GetInventory().GetAllItems().Where(i => i.IsWeapon()));
                name = $"{idOrPrefab} ({Utils.GetPrefabName(live.gameObject)})";
            }
            else
            {
                Humanoid prefab = StageRunner.FindPrefab(idOrPrefab).GetComponent<Humanoid>() ?? throw new ArgumentException($"{idOrPrefab} is not a humanoid creature; it has no attack list");
                IEnumerable<GameObject> sources = (prefab.m_defaultItems ?? Array.Empty<GameObject>())
                    .Concat(prefab.m_randomWeapon ?? Array.Empty<GameObject>())
                    .Concat((prefab.m_randomSets ?? Array.Empty<Humanoid.ItemSet>()).SelectMany(s => s.m_items ?? Array.Empty<GameObject>()));
                foreach (GameObject source in sources)
                {
                    ItemDrop drop = source != null ? source.GetComponent<ItemDrop>() : null!;
                    if (drop == null || !drop.m_itemData.IsWeapon()) continue;
                    ItemDrop.ItemData copy = drop.m_itemData.Clone();
                    copy.m_dropPrefab = source;
                    items.Add(copy);
                }
            }
            CultureInfo ci = CultureInfo.InvariantCulture;
            IEnumerable<string> lines = items.Select(i =>
                $"\nattack {(i.m_dropPrefab != null ? i.m_dropPrefab.name : i.m_shared.m_name)} animation={i.m_shared.m_attack.m_attackAnimation} " +
                $"range={i.m_shared.m_aiAttackRange.ToString("F1", ci)} interval={i.m_shared.m_aiAttackInterval.ToString("F1", ci)}");
            return $"{items.Count} attacks for {name}" + string.Concat(lines.Distinct());
        }

        private static string Guide(Terminal.ConsoleEventArgs args)
        {
            StageGuides guides = StageGuides.Instance ?? throw new InvalidOperationException("Guides are not loaded");
            string mode = CommandText.Arg(args, 2, "ct_stage guide <mark|ring|path|state|arm|remove|clear|list> ...").ToLowerInvariant();
            switch (mode)
            {
                case "mark":
                case "ring":
                case "path":
                {
                    JObject json = OptionsJson(args, 4);
                    json["id"] = CommandText.Arg(args, 3, $"ct_stage guide {mode} <id> [key=value ...]");
                    json["kind"] = mode;
                    return guides.Set(StageGuide.Parse(json)).Describe();
                }
                case "state":
                {
                    string id = CommandText.Arg(args, 3, "ct_stage guide state <id> <active|next|off>");
                    guides.SetState(id, StageGuide.ParseState(CommandText.Arg(args, 4, "ct_stage guide state <id> <active|next|off>")));
                    return guides.Get(id).Describe();
                }
                case "arm":
                {
                    StageGuide guide = guides.Get(CommandText.Arg(args, 3, "ct_stage guide arm <id>"));
                    StageGuides.Rearm(guide);
                    return guide.Describe();
                }
                case "remove":
                {
                    string id = CommandText.Arg(args, 3, "ct_stage guide remove <id>");
                    return guides.Remove(id) ? $"removed guide {id}" : throw new ArgumentException($"No guide '{id}'");
                }
                case "clear":
                    return $"removed {guides.Clear()} guides";
                case "list":
                {
                    List<StageGuide> all = guides.All.ToList();
                    return $"{all.Count} guides" + string.Concat(all.Select(g => "\n" + g.Describe()));
                }
                default:
                    throw new ArgumentException("Use mark, ring, path, state, arm, remove, clear or list");
            }
        }

        private static bool IsGround(string value) => value.Equals("ground", StringComparison.OrdinalIgnoreCase) || value.Equals("g", StringComparison.OrdinalIgnoreCase);

        private static Dictionary<string, string> Options(Terminal.ConsoleEventArgs args, int from)
        {
            Dictionary<string, string> options = new(StringComparer.OrdinalIgnoreCase);
            for (int i = from; i < args.Length; i++)
            {
                int equals = args[i].IndexOf('=');
                if (equals <= 0) throw new ArgumentException($"Options are key=value, not '{args[i]}'");
                options[args[i].Substring(0, equals)] = args[i].Substring(equals + 1);
            }
            return options;
        }

        /// <summary>key=value options in the scene file's form: points and comma lists of numbers become arrays.</summary>
        private static JObject OptionsJson(Terminal.ConsoleEventArgs args, int from)
        {
            JObject json = new();
            foreach (KeyValuePair<string, string> option in Options(args, from))
            {
                string key = option.Key.ToLowerInvariant();
                string value = option.Value;
                if (key == "points")
                    json[key] = new JArray(value.Split(';').Where(p => p.Length > 0).Select(p => (JToken)Point(p)));
                else if ((key == "at" || key == "toward" || key == "target" || key == "to" || key == "around") && value.Contains(','))
                    json[key] = Point(value);
                else if (key == "attacks" || key == "label" || key == "on" || key == "after")
                    json[key] = value;
                else if (bool.TryParse(value, out bool flag))
                    json[key] = flag;
                else if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float number))
                    json[key] = number;
                else
                    json[key] = value;
            }
            return json;
        }

        private static JArray Point(string text) =>
            new(text.Split(',').Select(v => IsGround(v.Trim()) ? JValue.CreateNull() : new JValue(CommandText.Number(v.Trim(), -30000, 30000, "coordinate"))).Cast<object>().ToArray());
    }
}
