using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using cameraTools.Markers;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace cameraTools.Staging
{
    /// <summary>
    /// A place or something to face: a world point, a staged actor ("player" or an actor id) or a marker.
    /// </summary>
    internal sealed class StageTarget
    {
        public Vector3? Point;
        public bool GroundSnap;
        public string Name = "";

        public static StageTarget? Parse(JToken? token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type == JTokenType.Array)
            {
                List<float> values = token.Select(v => v.Type == JTokenType.Null ? float.NaN : v.Value<float>()).ToList();
                if (values.Count == 2) return new StageTarget { Point = new Vector3(values[0], 0f, values[1]), GroundSnap = true };
                if (values.Count == 3)
                    return float.IsNaN(values[1])
                        ? new StageTarget { Point = new Vector3(values[0], 0f, values[2]), GroundSnap = true }
                        : new StageTarget { Point = new Vector3(values[0], values[1], values[2]) };
                throw new ArgumentException($"A point is [x, z] or [x, y, z], not {token.ToString(Newtonsoft.Json.Formatting.None)}");
            }
            string text = token.Value<string>()?.Trim() ?? "";
            if (text.Length == 0) return null;
            return new StageTarget { Name = text };
        }

        /// <summary>A point on the ground (actors and markers resolve to where they stand).</summary>
        public Vector3 Ground()
        {
            if (Point is Vector3 point) return GroundSnap ? SnapToGround(point) : point;
            if (StageRunner.FindActorCharacter(Name) is Character character) return character.transform.position;
            Marker marker = MarkerStore.Find(Name) ?? throw new ArgumentException($"'{Name}' is not an actor, a marker or a point");
            Vector3 anchor = marker.Kind == MarkerKind.Path && marker.waypoints.Count > 0 ? marker.waypoints[0].position : marker.position;
            return marker.Kind == MarkerKind.Shot ? marker.cameraPosition : anchor;
        }

        /// <summary>The point to aim at: an actor's body centre, otherwise the ground point raised 1 m.</summary>
        public Vector3 AimPoint()
        {
            if (Point == null && StageRunner.FindActorCharacter(Name) is Character character) return character.GetCenterPoint();
            Vector3 ground = Ground();
            return Point is Vector3 && !GroundSnap ? ground : ground + Vector3.up;
        }

        public Character? Character => Point == null ? StageRunner.FindActorCharacter(Name) : null;

        public override string ToString() => Point is Vector3 p ? $"{p.x:F1},{(GroundSnap ? "ground" : p.y.ToString("F1", CultureInfo.InvariantCulture))},{p.z:F1}" : Name;

        public static Vector3 SnapToGround(Vector3 point)
        {
            ZoneSystem? zones = ZoneSystem.instance;
            if (zones == null) return point;
            // In a room far above the terrain (Endgame rooms near y 5,000), the floor is the one under the point at
            // the player's height, not the terrain thousands of metres below.
            if (StageFloor.PlayerAwayFromTerrain(out float playerY) && StageFloor.Below(point.x, point.z, playerY) is float room)
                return new Vector3(point.x, room, point.z);
            float terrain = zones.GetGroundHeight(point);
            return zones.FindFloor(new Vector3(point.x, terrain + 30f, point.z), out float floor)
                ? new Vector3(point.x, floor, point.z)
                : new Vector3(point.x, terrain, point.z);
        }
    }

    /// <summary>One staged creature or the player, placed when the scene starts.</summary>
    internal sealed class StageActorSpec
    {
        public string Id = "";
        public string Prefab = "";
        public string Take = "";
        public float TakeRadius = 40f;
        public StageTarget? At;
        public float? Facing;
        public StageTarget? Toward;
        public int Level = 1;
        public float? Health;
        public string State = "hold";
        public string Item = "";
        /// <summary>ZDO strings to keep while staged, for example {"VE_Encounter": ""} to keep a boss out of an Endgame run.</summary>
        public Dictionary<string, string> Zdo = new(StringComparer.Ordinal);

        public bool IsPlayer => string.Equals(Id, StageRunner.PlayerId, StringComparison.OrdinalIgnoreCase);

        public static StageActorSpec Parse(JObject json)
        {
            StageActorSpec spec = new()
            {
                Id = Text(json, "id") ?? throw new ArgumentException("Every actor needs an id"),
                Prefab = Text(json, "prefab") ?? "",
                Take = Text(json, "take") ?? "",
                TakeRadius = json.Value<float?>("radius") ?? 40f,
                At = StageTarget.Parse(json["at"]),
                Facing = json.Value<float?>("facing"),
                Toward = StageTarget.Parse(json["toward"]),
                Level = json.Value<int?>("level") ?? 1,
                Health = json.Value<float?>("health"),
                State = (Text(json, "state") ?? "hold").ToLowerInvariant(),
                Item = Text(json, "item") ?? ""
            };
            if (json["zdo"] is JObject zdo)
                foreach (JProperty pair in zdo.Properties()) spec.Zdo[pair.Name] = pair.Value.Type == JTokenType.Null ? "" : pair.Value.ToString();
            if (!spec.IsPlayer && spec.Prefab.Length == 0 && spec.Take.Length == 0)
                throw new ArgumentException($"Actor {spec.Id} needs a prefab to spawn or a creature name to take");
            if (!spec.IsPlayer && spec.Take.Length == 0 && spec.At == null)
                throw new ArgumentException($"Actor {spec.Id} needs 'at'");
            if (spec.State != "hold" && spec.State != "sleep" && spec.State != "free")
                throw new ArgumentException($"Actor {spec.Id}: state is hold, sleep or free, not '{spec.State}'");
            return spec;
        }

        internal static string? Text(JObject json, string key) => json[key]?.Type == JTokenType.Null ? null : json.Value<string>(key);
    }

    /// <summary>
    /// One step for one actor. It starts at scene time <see cref="T"/>, or <see cref="Delay"/> seconds after the
    /// action labelled <see cref="After"/> finishes.
    /// </summary>
    internal sealed class StageAction
    {
        public static readonly string[] Kinds = { "hold", "walk", "face", "attack", "release", "wake", "sleep", "place", "equip", "block", "guide", "health" };

        public string Actor = "";
        public string Do = "";
        public float? T;
        public string After = "";
        public float Delay;
        public string Label = "";

        public List<Vector3> Points = new();
        public List<float> PointHolds = new();
        public string Path = "";
        public string Pace = "walk";
        public string Then = "hold";
        /// <summary>The walk's last point, followed while it moves: an actor, a marker or a point.</summary>
        public StageTarget? To;
        /// <summary>How far short of 'to' the walk stops: 1.5 m for an actor, 0 otherwise.</summary>
        public float? Stop;

        public StageTarget? Toward;
        public StageTarget? At;
        public float? Facing;
        public List<string> Attacks = new();
        public float Gap = 0.3f;
        public bool Secondary;
        public int Count = 1;
        public float Hold = 0.1f;
        public bool Hunt = true;
        public StageTarget? Target;
        public string Item = "";
        /// <summary>The health to set (do=health).</summary>
        public float? Value;
        /// <summary>A guide action: which guide, and the state to give it.</summary>
        public string Guide = "";
        public GuideState GuideState = GuideState.Active;

        public bool IsGuide => Do == "guide";
        public string Name => Label.Length > 0 ? Label : IsGuide ? $"guide.{Guide}" : $"{Actor}.{Do}";

        public static StageAction Parse(JObject json)
        {
            // {"guide": "<id>", "state": "active|next|off"} changes a guide; it has no actor.
            bool guide = json["guide"] != null && json["do"] == null;
            StageAction action = new()
            {
                Actor = guide ? "" : StageActorSpec.Text(json, "actor") ?? throw new ArgumentException("Every action needs an actor"),
                Do = guide ? "guide" : (StageActorSpec.Text(json, "do") ?? throw new ArgumentException("Every action needs 'do'")).ToLowerInvariant(),
                Guide = guide ? StageActorSpec.Text(json, "guide") ?? "" : "",
                GuideState = guide ? StageGuide.ParseState(StageActorSpec.Text(json, "state") ?? "active") : GuideState.Active,
                T = json.Value<float?>("t"),
                After = StageActorSpec.Text(json, "after") ?? "",
                Delay = json.Value<float?>("delay") ?? 0f,
                Label = StageActorSpec.Text(json, "label") ?? "",
                Path = StageActorSpec.Text(json, "path") ?? "",
                Pace = (StageActorSpec.Text(json, "pace") ?? "walk").ToLowerInvariant(),
                Then = (StageActorSpec.Text(json, "then") ?? "hold").ToLowerInvariant(),
                To = StageTarget.Parse(json["to"]),
                Stop = json.Value<float?>("stop"),
                Toward = StageTarget.Parse(json["toward"]),
                At = StageTarget.Parse(json["at"]),
                Facing = json.Value<float?>("facing"),
                Gap = json.Value<float?>("gap") ?? 0.3f,
                Secondary = json.Value<bool?>("secondary") ?? false,
                Count = json.Value<int?>("count") ?? 1,
                Hold = json.Value<float?>("hold") ?? 0.1f,
                Hunt = json.Value<bool?>("hunt") ?? true,
                Target = StageTarget.Parse(json["target"]),
                Item = StageActorSpec.Text(json, "item") ?? "",
                Value = json.Value<float?>("value")
            };
            if (!Kinds.Contains(action.Do)) throw new ArgumentException($"'do' is one of {string.Join(", ", Kinds)}, not '{action.Do}'");
            if (action.T == null && action.After.Length == 0) action.T = 0f;
            if (json["points"] is JArray points)
            {
                foreach (JToken point in points)
                {
                    StageTarget target = StageTarget.Parse(point) ?? throw new ArgumentException("A walk point is empty");
                    if (target.Point == null) throw new ArgumentException("Walk points are [x, z] or [x, y, z]");
                    action.Points.Add(target.Point.Value);
                    action.PointHolds.Add(0f);
                }
            }
            if (json["attacks"] is JToken attacks)
                action.Attacks = attacks.Type == JTokenType.Array
                    ? attacks.Select(a => a.Value<string>() ?? "").Where(a => a.Length > 0).ToList()
                    : (attacks.Value<string>() ?? "").Split(',').Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
            if (action.Do == "walk" && action.Points.Count == 0 && action.Path.Length == 0 && action.To == null)
                throw new ArgumentException($"{action.Name}: walk needs 'points', a path marker in 'path', or 'to'");
            if (action.Stop is float stop && (stop < 0f || stop > 50f))
                throw new ArgumentException($"{action.Name}: stop is 0 to 50 m");
            if (action.Do == "walk" && action.Pace != "walk" && action.Pace != "run" && action.Pace != "sprint")
                throw new ArgumentException($"{action.Name}: pace is walk, run or sprint");
            if (action.Do == "walk" && action.Then != "hold" && action.Then != "release")
                throw new ArgumentException($"{action.Name}: then is hold or release");
            if (action.Do == "face" && action.Toward == null && action.Facing == null)
                throw new ArgumentException($"{action.Name}: face needs 'toward' or 'facing'");
            if (action.Do == "place" && action.At == null)
                throw new ArgumentException($"{action.Name}: place needs 'at'");
            if (action.Do == "health" && (action.Value is not float value || value < 1f))
                throw new ArgumentException($"{action.Name}: health needs value=<health of 1 or more>");
            if (action.Do == "equip" && action.Item.Length == 0)
                throw new ArgumentException($"{action.Name}: equip needs 'item'");
            return action;
        }

        /// <summary>Fills <see cref="Points"/> from the path marker, with each waypoint's hold.</summary>
        /// <summary>Index of the point that follows 'to', or -1.</summary>
        public int ToIndex = -1;

        public void ResolvePath()
        {
            if (To != null && ToIndex < 0)
            {
                ResolveMarkerPath();
                ToIndex = Points.Count;
                Points.Add(Vector3.zero);
                PointHolds.Add(0f);
                return;
            }
            ResolveMarkerPath();
        }

        private void ResolveMarkerPath()
        {
            if (Path.Length == 0 || Points.Count > 0) return;
            Marker marker = MarkerStore.Find(Path) ?? throw new ArgumentException($"No path marker '{Path}'");
            if (marker.Kind != MarkerKind.Path || marker.waypoints.Count == 0) throw new ArgumentException($"Marker '{Path}' is not a path with waypoints");
            foreach (Waypoint waypoint in marker.waypoints)
            {
                Points.Add(waypoint.position);
                PointHolds.Add(waypoint.hold);
            }
        }

        public string Describe()
        {
            if (IsGuide) return $"guide {Guide} {GuideState.ToString().ToLowerInvariant()}";
            List<string> parts = new() { $"{Actor} {Do}" };
            if (Points.Count > 0) parts.Add($"{Points.Count} points {Pace}");
            else if (Path.Length > 0) parts.Add($"path {Path} {Pace}");
            if (Attacks.Count > 0) parts.Add(string.Join(",", Attacks));
            if (Do == "attack" && Attacks.Count == 0) parts.Add(Secondary ? "secondary" : "primary");
            if (Toward != null) parts.Add("toward " + Toward);
            if (Target != null) parts.Add("target " + Target);
            if (Item.Length > 0) parts.Add(Item);
            if (Value is float health) parts.Add(health.ToString("F0", CultureInfo.InvariantCulture));
            return string.Join(" ", parts);
        }
    }

    internal sealed class StageRecord
    {
        public string Name = "";
        public int? Rate;
        public float Lead = 1f;
        public float Tail = 2f;
        /// <summary>Start the recording when this label fires (a guide trigger or a labelled action), not at the start.</summary>
        public string After = "";
    }

    /// <summary>A scene file from BepInEx/config/cameraTools/scenes.</summary>
    internal sealed class StageScene
    {
        public string Name = "";
        public StageRecord? Record;
        public float ClearRadius = 40f;
        public StageTarget? ClearAt;
        public bool ClearDrops = true;
        public bool Clear = true;
        public float? End;
        public List<StageActorSpec> Actors = new();
        public List<StageAction> Actions = new();
        public List<StageGuide> Guides = new();

        public static string Directory => System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "cameraTools", "scenes");

        public static string FilePath(string name) =>
            System.IO.Path.Combine(Directory, name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? name : name + ".json");

        public static StageScene Load(string name)
        {
            string path = FilePath(name);
            if (!File.Exists(path)) throw new ArgumentException($"No scene file {path}");
            return Parse(JObject.Parse(File.ReadAllText(path)), System.IO.Path.GetFileNameWithoutExtension(path));
        }

        public static StageScene Parse(JObject json, string fallbackName)
        {
            StageScene scene = new() { Name = StageActorSpec.Text(json, "name") ?? fallbackName, End = json.Value<float?>("end") };
            if (json["record"] is JObject record)
                scene.Record = new StageRecord
                {
                    Name = StageActorSpec.Text(record, "name") ?? scene.Name,
                    Rate = record.Value<int?>("rate"),
                    Lead = record.Value<float?>("lead") ?? 1f,
                    Tail = record.Value<float?>("tail") ?? 2f,
                    After = StageActorSpec.Text(record, "after") ?? ""
                };
            JToken? clear = json["clear"];
            if (clear?.Type == JTokenType.Boolean) scene.Clear = clear.Value<bool>();
            else if (clear is JObject clearJson)
            {
                scene.ClearRadius = clearJson.Value<float?>("radius") ?? 40f;
                scene.ClearAt = StageTarget.Parse(clearJson["at"]);
                scene.ClearDrops = clearJson.Value<bool?>("drops") ?? true;
            }
            foreach (JObject actor in (json["actors"] as JArray ?? new JArray()).OfType<JObject>()) scene.Actors.Add(StageActorSpec.Parse(actor));
            foreach (JObject action in (json["actions"] as JArray ?? new JArray()).OfType<JObject>()) scene.Actions.Add(StageAction.Parse(action));
            foreach (JObject guide in (json["guides"] as JArray ?? new JArray()).OfType<JObject>()) scene.Guides.Add(StageGuide.Parse(guide));

            HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
            foreach (StageActorSpec actor in scene.Actors)
                if (!ids.Add(actor.Id)) throw new ArgumentException($"Two actors are called {actor.Id}");
            HashSet<string> guideIds = new(StringComparer.OrdinalIgnoreCase);
            foreach (StageGuide guide in scene.Guides)
                if (!guideIds.Add(guide.Id)) throw new ArgumentException($"Two guides are called {guide.Id}");
            HashSet<string> labels = new(scene.Actions.Where(a => a.Label.Length > 0).Select(a => a.Label)
                .Concat(scene.Guides.Where(g => g.On.Length > 0).Select(g => g.On)), StringComparer.OrdinalIgnoreCase);
            foreach (StageAction action in scene.Actions)
            {
                if (action.IsGuide && !guideIds.Contains(action.Guide))
                    throw new ArgumentException($"Action {action.Name} changes guide {action.Guide}, which the scene does not have");
                if (!action.IsGuide && !ids.Contains(action.Actor) && !string.Equals(action.Actor, StageRunner.PlayerId, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"Action {action.Name} names actor {action.Actor}, which the scene does not have");
                if (action.After.Length > 0 && !labels.Contains(action.After))
                    throw new ArgumentException($"Action {action.Name} waits for '{action.After}', which no action or guide trigger is labelled");
            }
            if (scene.Record?.After is string recordAfter && recordAfter.Length > 0 && !labels.Contains(recordAfter))
                throw new ArgumentException($"The recording waits for '{recordAfter}', which no action or guide trigger is labelled");
            return scene;
        }
    }
}
