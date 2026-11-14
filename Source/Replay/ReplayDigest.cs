using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

namespace cameraTools.Replay
{
    /// <summary>Game data the digest needs that only exists on the main thread: sizes, sun angles, attack names, highlights.</summary>
    internal sealed class DigestContext
    {
        internal Dictionary<int, (float Height, float Radius)> Sizes = new();
        internal Dictionary<string, float>? SunAngles;
        internal HashSet<string>? AttackAnimations;
        internal List<Highlight> Highlights = new();
        internal float Rate = 10f;

        internal static DigestContext Capture(ReplayFile replay, string? path)
        {
            DigestContext context = new()
            {
                Sizes = CombatData.Sizes(replay.Info),
                AttackAnimations = ReplayEvents.AttackAnimations(),
                Highlights = path != null ? Replay.Highlights.Load(path) : new List<Highlight>()
            };
            if (EnvMan.instance != null)
            {
                context.SunAngles = new Dictionary<string, float>();
                foreach (EnvironmentSample sample in replay.Environment)
                {
                    if (sample.Environment == null || context.SunAngles.ContainsKey(sample.Environment)) continue;
                    EnvSetup? env = EnvMan.instance.GetEnv(sample.Environment);
                    if (env != null) context.SunAngles[sample.Environment] = env.m_sunAngle;
                }
            }
            return context;
        }
    }

    /// <summary>
    /// A compact description of a recording for an agent planning shots: who was there, where they moved and faced,
    /// what happened, and the light. Written as &lt;name&gt;.digest.json beside the recording.
    /// </summary>
    internal static class ReplayDigest
    {
        internal const int SchemaVersion = 1;
        internal static string JsonPath(string replayPath) => Path.ChangeExtension(replayPath, ".digest.json");

        /// <summary>The direction toward the sun, computed the way EnvMan places its light.</summary>
        internal static Vector3 SunDirection(float sunAngle, float dayFraction) =>
            -(Quaternion.Euler(-90f + sunAngle, 0f, 0f) * Quaternion.Euler(0f, -90f, 0f) * Quaternion.Euler(-90f + 360f * dayFraction, 0f, 0f) * Vector3.forward);

        private static double R(float value, int digits = 2) => Math.Round(value, digits);
        private static double[] V(Vector3 value) => new[] { R(value.x), R(value.y), R(value.z) };

        internal static void Write(string path, ReplayFile replay, DigestContext context)
        {
            string temporary = path + ".writing";
            File.WriteAllText(temporary, JsonConvert.SerializeObject(Build(replay, context), Formatting.None));
            if (File.Exists(path)) File.Delete(path);
            File.Move(temporary, path);
        }

        internal static Dictionary<string, object?> Build(ReplayFile replay, DigestContext context)
        {
            ReplayTracks tracks = new(replay);
            ReplayEventList events = ReplayEvents.Build(replay, context.AttackAnimations);
            Dictionary<string, object> unavailable = new();
            foreach (KeyValuePair<ReplayEventType, string> reason in events.Unavailable) unavailable["events." + ReplayEvents.Name(reason.Key)] = reason.Value;

            // Health over time: known after each drop the file saved.
            Dictionary<(long, uint), List<(float Time, float Health)>> health = new();
            foreach (DamageEvent damage in replay.Damage)
            {
                if (!health.TryGetValue((damage.User, damage.Id), out List<(float, float)> list)) health[(damage.User, damage.Id)] = list = new List<(float, float)>();
                list.Add((damage.Time, damage.Health));
            }
            if (replay.FileVersion < 9) unavailable["tracks.health"] = $"File version {replay.FileVersion} saved no health samples; version 9 and later do";
            if (replay.FileVersion < 10)
            {
                unavailable["actors.level"] = $"File version {replay.FileVersion} saved no character levels, maximum health or factions; version 10 and later do";
                unavailable["projectiles.owner"] = $"File version {replay.FileVersion} saved no projectile owners; version 10 and later do";
            }

            // The recorder: saved since version 10; before that, the player nearest the first recorded camera.
            (long, uint) recorder = replay.Recorder;
            bool inferred = false;
            if (recorder == default && replay.Frames.Count > 0)
            {
                float best = float.MaxValue;
                foreach (ActorTrack track in tracks.All)
                {
                    if (track.Kind != 0 || track.IsProjectile || replay.Info.PrefabName(track.Prefab) != "Player") continue;
                    float distance = Vector3.Distance(track.Positions[0], replay.Frames[0].CameraPosition);
                    if (distance < best) { best = distance; recorder = (track.User, track.Id); inferred = true; }
                }
            }

            HashSet<(long, uint)> involved = new(events.Events.Select(e => (e.User, e.Id)));
            if (recorder != default) involved.Add(recorder);
            Bounds area = tracks.FightArea(involved);

            List<Dictionary<string, object?>> actors = new();
            Dictionary<string, object?> trackJson = new();
            List<Dictionary<string, object?>> projectiles = new();
            List<Dictionary<string, object?>> drops = new();
            Dictionary<(long, uint), float> deaths = new();
            foreach (DeathEvent death in replay.Deaths) if (!deaths.ContainsKey((death.User, death.Id))) deaths[(death.User, death.Id)] = death.Time;

            foreach (ActorTrack track in tracks.All)
            {
                string prefab = replay.Info.PrefabName(track.Prefab);
                if (track.IsProjectile)
                {
                    Dictionary<string, object?> entry = new()
                    {
                        ["id"] = track.Key, ["prefab"] = prefab,
                        ["launchTime"] = R(track.Start, 3), ["launchPosition"] = V(track.Positions[0]),
                        ["endTime"] = R(track.End, 3), ["endPosition"] = V(track.Positions[track.Positions.Count - 1])
                    };
                    if (replay.ProjectileOwners.TryGetValue((track.User, track.Id), out (long, uint) owner)) entry["owner"] = owner.Item1 + ":" + owner.Item2;
                    projectiles.Add(entry);
                    continue;
                }
                if (track.Kind == 3)
                {
                    drops.Add(new Dictionary<string, object?> { ["id"] = track.Key, ["prefab"] = prefab, ["time"] = R(track.Start, 3), ["position"] = V(track.Positions[0]) });
                    continue;
                }
                (long, uint) key = (track.User, track.Id);
                Dictionary<string, object?> actor = new()
                {
                    ["id"] = track.Key, ["kind"] = track.KindName, ["prefab"] = prefab,
                    ["name"] = replay.ActorNames.TryGetValue(key, out string name) ? name : prefab,
                    ["isPlayer"] = prefab == "Player",
                    ["firstSeen"] = R(track.Start, 3), ["lastSeen"] = R(track.End, 3),
                    ["death"] = deaths.TryGetValue(key, out float died) ? R(died, 3) : null
                };
                if (replay.Actors.TryGetValue(key, out ActorInfo info))
                {
                    actor["level"] = info.Level; actor["maxHealth"] = R(info.MaxHealth, 1); actor["faction"] = info.Faction;
                    actor["isPlayer"] = info.IsPlayer; actor["height"] = R(info.Height); actor["radius"] = R(info.Radius);
                }
                else if (context.Sizes.TryGetValue(track.Prefab, out (float Height, float Radius) size))
                {
                    actor["height"] = R(size.Height); actor["radius"] = R(size.Radius);
                }
                else if (track.Kind == 0)
                {
                    actor["height"] = null; actor["radius"] = null;
                    actor["sizeUnavailable"] = "The prefab is not loaded in this game (its mod is missing), so its collider cannot be read";
                }
                actors.Add(actor);
                trackJson[track.Key] = Track(track, context.Rate, health.TryGetValue(key, out List<(float, float)> h) ? h : null, replay.FileVersion >= 9);
            }

            List<Dictionary<string, object?>> eventJson = new();
            foreach (ReplayEvent tag in events.Events)
            {
                Dictionary<string, object?> entry = new()
                {
                    ["t"] = R(tag.Time, 3), ["type"] = ReplayEvents.Name(tag.Type), ["actor"] = tag.Actor, ["prefab"] = tag.Prefab, ["name"] = tag.Name
                };
                if (tracks.ByKey.TryGetValue((tag.User, tag.Id), out ActorTrack track) && track.Sample(Mathf.Clamp(tag.Time, track.Start, track.End), out Vector3 position, out Quaternion rotation))
                {
                    entry["position"] = V(position); entry["yaw"] = R(rotation.eulerAngles.y, 1);
                }
                if (tag.Type == ReplayEventType.DamageTaken) { entry["amount"] = R(tag.Amount); entry["health"] = R(tag.Health); }
                if (tag.Type == ReplayEventType.AttackStart) { entry["animation"] = tag.Animation; if (tag.Other.Length > 0) entry["target"] = tag.Other; }
                if (tag.Type == ReplayEventType.Hit)
                {
                    Dictionary<string, object> hit = new();
                    ReplayEvents.AddHit(hit, tag);
                    foreach (KeyValuePair<string, object> field in hit) entry[field.Key] = field.Value;
                }
                eventJson.Add(entry);
            }
            foreach (Highlight highlight in context.Highlights)
                eventJson.Add(new Dictionary<string, object?>
                {
                    ["t"] = R(highlight.t, 3), ["type"] = "highlight", ["id"] = highlight.id, ["label"] = highlight.label,
                    ["cameraPosition"] = highlight.cameraPosition, ["cameraForward"] = highlight.cameraForward, ["fov"] = R(highlight.fov, 1),
                    ["target"] = highlight.target.Length > 0 ? highlight.target : null
                });
            eventJson.Sort((a, b) => ((double)a["t"]!).CompareTo((double)b["t"]!));

            List<Dictionary<string, object?>> effects = replay.Effects.Select(effect => new Dictionary<string, object?>
            {
                ["prefab"] = effect.Prefab, ["start"] = R(effect.Start, 3), ["end"] = R(effect.End, 3),
                ["position"] = effect.Frames.Count > 0 ? V(effect.Frames[0].Position) : null
            }).ToList();

            List<Dictionary<string, object?>> lighting = new();
            if (context.SunAngles == null) unavailable["lighting.sunDirection"] = "The digest was made without a loaded world, so the environments' sun angles could not be read; make it inside a replay session";
            foreach (EnvironmentSample sample in replay.Environment)
            {
                Dictionary<string, object?> entry = new()
                {
                    ["t"] = R(sample.Time, 2), ["environment"] = sample.Environment, ["dayFraction"] = R(sample.DayFraction, 4),
                    ["windAngle"] = R(sample.WindAngle, 1), ["windIntensity"] = R(sample.WindIntensity)
                };
                if (context.SunAngles != null && sample.Environment != null && context.SunAngles.TryGetValue(sample.Environment, out float angle))
                {
                    Vector3 sun = SunDirection(angle, sample.DayFraction);
                    entry["sunDirection"] = new[] { R(sun.x, 3), R(sun.y, 3), R(sun.z, 3) };
                    entry["sunElevation"] = R(Mathf.Asin(Mathf.Clamp(sun.y, -1f, 1f)) * Mathf.Rad2Deg, 1);
                    entry["sunYaw"] = R(Mathf.Atan2(sun.x, sun.z) * Mathf.Rad2Deg, 1);
                }
                lighting.Add(entry);
            }

            return new Dictionary<string, object?>
            {
                ["schemaVersion"] = SchemaVersion,
                ["replay"] = replay.Info.Name,
                ["fileVersion"] = replay.FileVersion,
                ["duration"] = R(replay.Duration, 3),
                ["world"] = replay.Info.WorldName,
                ["gameVersion"] = replay.Info.GameVersion,
                ["sampleRate"] = replay.SampleRate,
                ["trackRate"] = context.Rate,
                ["recorder"] = recorder == default ? null : recorder.Item1 + ":" + recorder.Item2,
                ["recorderInferred"] = inferred,
                ["fightArea"] = new Dictionary<string, object> { ["min"] = V(area.min), ["max"] = V(area.max), ["center"] = V(area.center) },
                ["timeOrigin"] = "Seconds from the start of the replay; the clock ct_replay seek uses",
                ["coordinates"] = "Valheim world meters: x east, y up, z north. yaw is degrees clockwise from north (+z), as Unity's rotation.eulerAngles.y",
                ["trackFields"] = new Dictionary<string, string>
                {
                    ["t"] = "replay seconds", ["x,y,z"] = "position", ["yaw"] = "facing in degrees", ["speed"] = "horizontal speed m/s",
                    ["flags"] = "bit 1 onGround, 2 flying, 4 blocking, 8 inWater, 16 falling, 32 crouching",
                    ["health"] = "health after the last recorded drop; null before the first drop or when the file has no health samples"
                },
                ["unavailable"] = unavailable,
                ["actors"] = actors,
                ["tracks"] = trackJson,
                ["projectiles"] = projectiles,
                ["drops"] = drops,
                ["events"] = eventJson,
                ["effects"] = effects,
                ["lighting"] = lighting
            };
        }

        private static Dictionary<string, object?> Track(ActorTrack track, float rate, List<(float Time, float Health)>? health, bool hasHealth)
        {
            List<double> t = new(), x = new(), y = new(), z = new(), yaw = new(), speed = new();
            List<int> flags = new();
            List<double?> hp = new();
            float step = 1f / Mathf.Max(1f, rate);
            Vector3 previous = track.Positions[0];
            float previousTime = track.Start;
            int healthIndex = -1;
            for (float time = track.Start; time <= track.End + 0.0001f; time += step)
            {
                track.Sample(time, out Vector3 position, out Quaternion rotation);
                byte bools = track.Bools[track.Index(time)];
                t.Add(R(time, 3)); x.Add(R(position.x)); y.Add(R(position.y)); z.Add(R(position.z));
                yaw.Add(R(rotation.eulerAngles.y, 1));
                Vector3 moved = position - previous; moved.y = 0f;
                speed.Add(time > previousTime ? R(moved.magnitude / (time - previousTime)) : 0d);
                previous = position; previousTime = time;
                int bits = 0;
                if ((bools & (1 << 1)) != 0) bits |= 1;
                if ((bools & (1 << 3)) != 0) bits |= 2;
                if ((bools & (1 << 6)) != 0) bits |= 4;
                if ((bools & (1 << 0)) != 0) bits |= 8;
                if ((bools & (1 << 4)) != 0) bits |= 16;
                if ((bools & (1 << 5)) != 0) bits |= 32;
                flags.Add(bits);
                if (health != null) while (healthIndex + 1 < health.Count && health[healthIndex + 1].Time <= time) healthIndex++;
                hp.Add(health != null && healthIndex >= 0 ? R(health[healthIndex].Health, 1) : null);
            }
            Dictionary<string, object?> json = new() { ["t"] = t, ["x"] = x, ["y"] = y, ["z"] = z, ["yaw"] = yaw, ["speed"] = speed, ["flags"] = flags };
            if (hasHealth) json["health"] = hp;
            return json;
        }
    }
}
