using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace cameraTools.Replay.Shots
{
    /// <summary>
    /// A camera move timed in replay seconds. Positions and look targets are world points or offsets from recorded actors,
    /// so the camera stays in step with the fight at any playback speed. Schema documented in the README.
    /// </summary>
    public sealed class ShotFile
    {
        public int schemaVersion = 1;
        public string replay = "";
        public string name = "";
        public float start;
        public float end;
        public List<SpeedKey> speed = new();
        /// <summary>Frozen moments: the fight stops at each freeze's replay time for its seconds while the camera keeps moving.</summary>
        public List<FreezeKey> freezes = new();
        public List<CameraKey> camera = new();
        /// <summary>Seconds of output time the aim takes to catch up with its target; 0 aims exactly.</summary>
        public float lookSmoothing;
        public List<ShakeKey> shake = new();
        public List<ShakeRule> shakeOnEvents = new();
        public List<string> keepInFrame = new();
        /// <summary>What to hide while the shot runs: "drops" hides dropped items and their loot beams.</summary>
        public List<string> hide = new();
        public DofSettings? dof;
        /// <summary>What the shot is meant to show; the check measures framing against it. See <see cref="FramingSpec"/>.</summary>
        public FramingSpec? framing;
        /// <summary>Free text for the agent or editor: what the shot is for.</summary>
        public string note = "";
    }

    public sealed class SpeedKey
    {
        public float t;
        public float speed = 1f;
    }

    public sealed class FreezeKey
    {
        /// <summary>Replay time the fight stops at.</summary>
        public float t;
        /// <summary>Output seconds the fight stays stopped.</summary>
        public float seconds;
    }

    public sealed class CameraKey
    {
        public float t;
        /// <summary>For a key inside a freeze at <see cref="t"/>: output seconds into the freeze, from 0 to the freeze's seconds.</summary>
        public float? freeze;
        /// <summary>[x, y, z] world point, or {"actor": "user:id", "offset": [x, y, z], "frame": "world"|"actor"}.</summary>
        public JToken? position;
        /// <summary>
        /// [x, y, z] world point, or {"actor": "user:id", "height": 0-1 of body height, "offset": [x, y, z]}; with
        /// "between": "user:id" the target is the midpoint of the two actors (each at "height" of its own body).
        /// </summary>
        public JToken? lookAt;
        public float? fov;
        public float? roll;
        /// <summary>How the move eases from this key to the next: linear, ease, in or out.</summary>
        public string ease = "ease";
    }

    public sealed class ShakeKey
    {
        public float t;
        /// <summary>Largest rotation in degrees.</summary>
        public float strength = 0.6f;
        public float duration = 0.3f;
    }

    public sealed class ShakeRule
    {
        /// <summary>death, hit, damage or attack.</summary>
        public string type = "hit";
        /// <summary>Only events of this actor (user:id); any actor when empty.</summary>
        public string? actor;
        public float strength = 0.6f;
        public float duration = 0.25f;
        /// <summary>For hit and damage: only events that took at least this much health.</summary>
        public float minTaken;
    }

    public sealed class DofSettings
    {
        public bool enabled = true;
        /// <summary>A distance in meters, or "lookAt" to focus on the look target.</summary>
        public JToken? focus;
        public float aperture = 0.6f;
        public float focalSize = 0.4f;
    }

    /// <summary>
    /// One moment of a shot: a replay time and, inside a freeze at that time, the output seconds spent in it. Outside freezes
    /// <see cref="Frozen"/> is 0.
    /// </summary>
    public readonly struct ShotTime
    {
        public readonly float Replay;
        public readonly float Frozen;
        public readonly bool IsFrozen;
        public ShotTime(float replay, float frozen = 0f, bool isFrozen = false) { Replay = replay; Frozen = frozen; IsFrozen = isFrozen; }
    }

    /// <summary>The camera a shot asks for at one replay time.</summary>
    public struct ShotPose
    {
        public Vector3 Position;
        public Vector3 LookAt;
        public float Fov;
        public float Roll;
        public float Speed;
    }

    /// <summary>Evaluates a shot against a recording's tracks. The same math drives play, check, preview and render.</summary>
    public sealed class ShotEvaluator
    {
        public const float DefaultFov = 65f;
        public readonly ShotFile Shot;
        private readonly ReplayTracks _tracks;
        private readonly Dictionary<(long, uint), float> _heights;
        private readonly List<(float Time, float Strength, float Duration)> _shakes = new();
        // Each camera key's place on the camera clock (see CameraClock), in key order.
        private readonly float[] _keyClock;
        private const float SameTime = 0.00001f;

        public ShotEvaluator(ShotFile shot, ReplayTracks tracks, Dictionary<(long, uint), float> heights, ReplayEventList events)
        {
            Shot = shot;
            _tracks = tracks;
            _heights = heights;
            Validate();
            Shot.camera.Sort((a, b) => a.t != b.t ? a.t.CompareTo(b.t) : (a.freeze ?? 0f).CompareTo(b.freeze ?? 0f));
            Shot.speed.Sort((a, b) => a.t.CompareTo(b.t));
            Shot.freezes.Sort((a, b) => a.t.CompareTo(b.t));
            _keyClock = Shot.camera.Select(key => CameraClock(key.t, key.freeze ?? 0f)).ToArray();
            foreach (ShakeKey key in shot.shake) _shakes.Add((key.t, key.strength, key.duration));
            foreach (ShakeRule rule in shot.shakeOnEvents)
                foreach (ReplayEvent tag in events.Events)
                {
                    if (!Matches(rule, tag)) continue;
                    if (rule.actor != null && rule.actor.Length > 0 && rule.actor != tag.Actor) continue;
                    if ((tag.Type == ReplayEventType.Hit || tag.Type == ReplayEventType.DamageTaken) && tag.Amount < rule.minTaken) continue;
                    _shakes.Add((tag.Time, rule.strength, rule.duration));
                }
        }

        private static bool Matches(ShakeRule rule, ReplayEvent tag) => rule.type.ToLowerInvariant() switch
        {
            "death" => tag.Type == ReplayEventType.Death,
            "hit" => tag.Type == ReplayEventType.Hit,
            "damage" => tag.Type == ReplayEventType.DamageTaken,
            "attack" => tag.Type == ReplayEventType.AttackStart,
            _ => false
        };

        private void Validate()
        {
            if (Shot.camera.Count == 0) throw new ArgumentException("A shot needs at least one camera key");
            if (Shot.end <= Shot.start) throw new ArgumentException($"Shot end ({Shot.end}) must be after start ({Shot.start})");
            foreach (CameraKey key in Shot.camera)
            {
                if (key.position == null) throw new ArgumentException($"Camera key at {key.t} has no position");
                if (key.lookAt == null) throw new ArgumentException($"Camera key at {key.t} has no lookAt");
                Actor(key.position); Actor(key.lookAt);
                if (key.lookAt is JObject look && look["between"] != null) _tracks.Get((string)look["between"]!);
                Ease(key.ease);
            }
            foreach (string actor in Shot.keepInFrame) _tracks.Get(actor);
            foreach (SpeedKey key in Shot.speed)
                if (key.speed < 0.05f || key.speed > 4f) throw new ArgumentException($"Speed {key.speed} at {key.t} must be from 0.05 to 4");
            foreach (FreezeKey freeze in Shot.freezes)
            {
                if (freeze.t < Shot.start || freeze.t >= Shot.end) throw new ArgumentException($"Freeze at {freeze.t} must be inside the shot, from its start to before its end ({Shot.start}-{Shot.end})");
                if (freeze.seconds <= 0f || freeze.seconds > 60f) throw new ArgumentException($"Freeze at {freeze.t} must last from 0 to 60 seconds, not {freeze.seconds}");
                if (Shot.freezes.Count(other => Mathf.Abs(other.t - freeze.t) < 0.001f) > 1) throw new ArgumentException($"Two freezes at {freeze.t}; join them into one");
            }
            foreach (CameraKey key in Shot.camera)
            {
                if (key.freeze is not float frozen) continue;
                FreezeKey freeze = FreezeAt(key.t) ?? throw new ArgumentException($"Camera key at {key.t} has \"freeze\" but the shot has no freeze at {key.t}");
                if (frozen < 0f || frozen > freeze.seconds + SameTime) throw new ArgumentException($"Camera key at {key.t} has freeze {frozen}, outside the freeze's 0-{freeze.seconds} seconds");
            }
            if (Shot.framing is FramingSpec framing)
            {
                if (framing.subject != null) _tracks.Get(framing.subject);
                if (framing.partner != null) _tracks.Get(framing.partner);
                if (framing.subject == null && Shot.keepInFrame.Count == 0) throw new ArgumentException("framing needs a subject, or keepInFrame actors to take it from");
                Framing.ParseSize(framing.size);
                Framing.ParsePlace(framing.place, 16f / 9f);
                if (framing.minVisible is float visible && (visible < 0f || visible > 1f)) throw new ArgumentException($"framing.minVisible {visible} must be from 0 to 1");
                if (framing.maxForeground is float foreground && (foreground < 0f || foreground > 1f)) throw new ArgumentException($"framing.maxForeground {foreground} must be from 0 to 1");
                if (framing.moment is float moment && (moment < Shot.start || moment > Shot.end)) throw new ArgumentException($"framing.moment {moment} is outside the shot ({Shot.start}-{Shot.end})");
                if (framing.action != null && (framing.action.Length != 2 || framing.action[1] < framing.action[0] || framing.action[0] > Shot.end || framing.action[1] < Shot.start))
                    throw new ArgumentException("framing.action is [from, to] replay seconds inside the shot");
                if (framing.weights != null)
                    foreach (string name in framing.weights.Keys)
                        if (!Framing.DefaultWeights.ContainsKey(name)) throw new ArgumentException($"framing.weights has unknown term '{name}'; use {string.Join(", ", Framing.DefaultWeights.Keys)}");
            }
        }

        private ActorTrack? Actor(JToken token) =>
            token is JObject obj && obj["actor"] != null ? _tracks.Get((string)obj["actor"]!) : null;

        internal static Vector3 Vector(JToken? token, Vector3 fallback)
        {
            if (token is not JArray array) return fallback;
            if (array.Count != 3) throw new ArgumentException($"A point is [x, y, z], not {token.ToString(Formatting.None)}");
            return new Vector3((float)array[0], (float)array[1], (float)array[2]);
        }

        internal static CreativeZoneCameraController.MoveEase Ease(string? ease) =>
            CreativeZoneCameraController.ParseEase(string.IsNullOrEmpty(ease) ? "ease" : ease!);

        /// <summary>Where an actor is at a replay time, holding its first or last sample outside its lifetime.</summary>
        private (Vector3 Position, Quaternion Rotation) ActorPose(ActorTrack track, float time)
        {
            track.Sample(Mathf.Clamp(time, track.Start, track.End), out Vector3 position, out Quaternion rotation);
            return (position, rotation);
        }

        internal float Height(ActorTrack track) => _heights.TryGetValue((track.User, track.Id), out float height) && height > 0f ? height : 1.8f;

        private Vector3 Position(JToken token, float time)
        {
            if (token is JArray) return Vector(token, Vector3.zero);
            ActorTrack track = Actor(token) ?? throw new ArgumentException("A position object needs \"actor\"");
            (Vector3 position, Quaternion rotation) = ActorPose(track, time);
            Vector3 offset = Vector(token["offset"], Vector3.zero);
            bool local = string.Equals((string?)token["frame"], "actor", StringComparison.OrdinalIgnoreCase);
            if (local)
            {
                Quaternion yaw = Quaternion.Euler(0f, rotation.eulerAngles.y, 0f);
                offset = yaw * offset;
            }
            return position + offset;
        }

        private Vector3 Look(JToken token, float time)
        {
            if (token is JArray) return Vector(token, Vector3.zero);
            ActorTrack track = Actor(token) ?? throw new ArgumentException("A lookAt object needs \"actor\"");
            (Vector3 position, Quaternion rotation) = ActorPose(track, time);
            float fraction = token["height"] != null ? (float)token["height"]! : 0.6f;
            Vector3 offset = Vector(token["offset"], Vector3.zero);
            if (string.Equals((string?)token["frame"], "actor", StringComparison.OrdinalIgnoreCase)) offset = Quaternion.Euler(0f, rotation.eulerAngles.y, 0f) * offset;
            Vector3 target = position + Vector3.up * Height(track) * fraction;
            if (token["between"] != null)
            {
                // The midpoint of the two actors; an actor that died holds its last recorded place.
                ActorTrack other = _tracks.Get((string)token["between"]!);
                (Vector3 otherPosition, _) = ActorPose(other, time);
                target = (target + otherPosition + Vector3.up * Height(other) * fraction) * 0.5f;
            }
            return target + offset;
        }

        public float SpeedAt(float time)
        {
            List<SpeedKey> keys = Shot.speed;
            if (keys.Count == 0) return 1f;
            if (time <= keys[0].t) return keys[0].speed;
            if (time >= keys[keys.Count - 1].t) return keys[keys.Count - 1].speed;
            for (int index = 0; index < keys.Count - 1; index++)
                if (time < keys[index + 1].t)
                {
                    float span = keys[index + 1].t - keys[index].t;
                    float u = span > 0.0001f ? Mathf.SmoothStep(0f, 1f, (time - keys[index].t) / span) : 1f;
                    return Mathf.Lerp(keys[index].speed, keys[index + 1].speed, u);
                }
            return keys[keys.Count - 1].speed;
        }

        /// <summary>Output seconds the shot lasts: each replay second plays for 1 / speed seconds, and each freeze for its seconds.</summary>
        public float OutputDuration(float step = 0.001f)
        {
            float total = 0f;
            for (float time = Shot.start; time < Shot.end; time += step) total += Mathf.Min(step, Shot.end - time) / SpeedAt(time);
            return total + FrozenBefore(Shot.end);
        }

        /// <summary>The output time at which a replay time plays (the first frame of a freeze at that time).</summary>
        public float OutputTime(float replayTime, float step = 0.001f)
        {
            float total = 0f;
            for (float time = Shot.start; time < replayTime; time += step) total += Mathf.Min(step, replayTime - time) / SpeedAt(time);
            return total + FrozenBefore(replayTime);
        }

        /// <summary>The freeze at a replay time, if there is one.</summary>
        public FreezeKey? FreezeAt(float time)
        {
            foreach (FreezeKey freeze in Shot.freezes) if (Mathf.Abs(freeze.t - time) < SameTime) return freeze;
            return null;
        }

        /// <summary>Output seconds spent in freezes before a replay time.</summary>
        public float FrozenBefore(float time)
        {
            float total = 0f;
            foreach (FreezeKey freeze in Shot.freezes) if (freeze.t < time - SameTime) total += freeze.seconds;
            return total;
        }

        /// <summary>
        /// Where a moment sits on the camera clock: replay time plus every second frozen before it. Camera keys are placed on
        /// this clock, so the camera moves through a freeze on output time and through the rest of the shot on replay time.
        /// </summary>
        public float CameraClock(float replay, float frozen) => replay + FrozenBefore(replay) + frozen;

        /// <summary>The shot's first output frame.</summary>
        public ShotTime First() => new(Shot.start, 0f, FreezeAt(Shot.start) != null);

        /// <summary>
        /// The next output frame, <paramref name="step"/> output seconds later: on through a freeze's seconds while it lasts,
        /// otherwise replay time moves by step times the speed and stops on the next freeze's time so the freeze starts exactly
        /// there.
        /// </summary>
        public ShotTime Next(ShotTime at, float step)
        {
            if (at.IsFrozen && FreezeAt(at.Replay) is FreezeKey freeze && at.Frozen + step <= freeze.seconds - 0.5f * step)
                return new ShotTime(at.Replay, at.Frozen + step, true);
            float next = at.Replay + step * SpeedAt(at.Replay);
            foreach (FreezeKey upcoming in Shot.freezes)
                if (upcoming.t > at.Replay + SameTime && upcoming.t <= next + SameTime) return new ShotTime(upcoming.t, 0f, true);
            return new ShotTime(next);
        }

        /// <summary>The camera at a replay time, at the start of any freeze there.</summary>
        public ShotPose Evaluate(float time) => Evaluate(new ShotTime(time));

        public ShotPose Evaluate(ShotTime at)
        {
            float time = at.Replay;
            float clock = CameraClock(at.Replay, at.Frozen);
            List<CameraKey> keys = Shot.camera;
            int segment = 0;
            while (segment < keys.Count - 1 && clock >= _keyClock[segment + 1]) segment++;
            float u = 0f;
            if (segment < keys.Count - 1 && clock > _keyClock[segment])
            {
                float span = _keyClock[segment + 1] - _keyClock[segment];
                u = span > 0.0001f ? Mathf.Clamp01((clock - _keyClock[segment]) / span) : 1f;
                u = CreativeZoneCameraController.ApplyEasePublic(u, Ease(keys[segment].ease));
            }
            CameraKey Key(int index) => keys[Mathf.Clamp(index, 0, keys.Count - 1)];
            Vector3 position, look;
            if (keys.Count == 1 || segment >= keys.Count - 1)
            {
                position = Position(Key(segment).position!, time);
                look = Look(Key(segment).lookAt!, time);
            }
            else
            {
                position = CatmullRom(Position(Key(segment - 1).position!, time), Position(Key(segment).position!, time),
                    Position(Key(segment + 1).position!, time), Position(Key(segment + 2).position!, time), u);
                look = CatmullRom(Look(Key(segment - 1).lookAt!, time), Look(Key(segment).lookAt!, time),
                    Look(Key(segment + 1).lookAt!, time), Look(Key(segment + 2).lookAt!, time), u);
            }
            return new ShotPose
            {
                Position = position,
                LookAt = look,
                Fov = Interpolate(keys, segment, u, key => key.fov, DefaultFov),
                Roll = Interpolate(keys, segment, u, key => key.roll, 0f),
                Speed = at.IsFrozen ? 0f : SpeedAt(time)
            };
        }

        /// <summary>A key without the value takes it from the nearest earlier key that has it.</summary>
        private static float Interpolate(List<CameraKey> keys, int segment, float u, Func<CameraKey, float?> value, float fallback)
        {
            float At(int index)
            {
                for (int i = Mathf.Min(index, keys.Count - 1); i >= 0; i--) if (value(keys[i]).HasValue) return value(keys[i])!.Value;
                for (int i = 0; i < keys.Count; i++) if (value(keys[i]).HasValue) return value(keys[i])!.Value;
                return fallback;
            }
            return segment >= keys.Count - 1 ? At(segment) : Mathf.Lerp(At(segment), At(segment + 1), u);
        }

        private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * (2f * p1 + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }

        /// <summary>Rotation offset in degrees (yaw, pitch, roll) from every shake active at the replay time. Same result every run.</summary>
        public Vector3 Shake(float time)
        {
            Vector3 total = Vector3.zero;
            foreach ((float start, float strength, float duration) in _shakes)
            {
                float age = time - start;
                if (age < 0f || age > duration || duration <= 0f) continue;
                float fade = 1f - age / duration;
                float amplitude = strength * fade * fade;
                float phase = age * 28f + start * 13.7f;
                total += new Vector3(Noise(phase, 1.3f), Noise(phase, 7.9f), Noise(phase, 4.1f) * 0.5f) * amplitude;
            }
            return total;
        }

        private static float Noise(float x, float seed) => Mathf.PerlinNoise(x, seed) * 2f - 1f;

        /// <summary>The camera rotation for a pose, with roll and shake.</summary>
        public static Quaternion Rotation(Vector3 position, Vector3 look, float roll, Vector3 shake)
        {
            Vector3 direction = look - position;
            if (direction.sqrMagnitude < 0.0001f) direction = Vector3.forward;
            return Quaternion.LookRotation(direction) * Quaternion.Euler(shake.y, shake.x, roll + shake.z);
        }

        /// <summary>Every actor named in the shot, for the check and for validation.</summary>
        public IEnumerable<ActorTrack> NamedActors()
        {
            foreach (CameraKey key in Shot.camera)
            {
                ActorTrack? a = Actor(key.position!), b = Actor(key.lookAt!);
                if (a != null) yield return a;
                if (b != null) yield return b;
            }
            foreach (string actor in Shot.keepInFrame) yield return _tracks.Get(actor);
        }

        internal static string F(float value) => value.ToString("F2", CultureInfo.InvariantCulture);
        internal static string F(Vector3 value) => $"{F(value.x)},{F(value.y)},{F(value.z)}";
    }
}
