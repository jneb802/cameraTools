using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace cameraTools.Replay.Shots
{
    /// <summary>One shot the solver is asked for: a beat, a framing intent, a move type, the side of the line and a time window.</summary>
    public sealed class ShotIntent
    {
        public string name = "";
        public string subject = "";
        public string? partner;
        public float moment;
        public float[]? window;
        /// <summary>1 or -1: the side of the line from subject to partner (positive is to the left walking from subject to partner); 0 for either.</summary>
        public int side;
        /// <summary>static, push, dolly, orbit or track.</summary>
        public string move = "static";
        public JToken? size;
        public JToken? place;
        public float? minVisible;
        public float? maxForeground;
        public float[]? fovs;
        public int angles = 24;
        public int heights = 8;
        /// <summary>Camera pitch range in degrees for the height samples: -5 to 35 by default; a low shot uses -5 to 8.</summary>
        public float[] heightRange = { -5f, 35f };
        public float? lookSmoothing;
        public List<SpeedKey> speed = new();
        public List<ShakeRule> shakeOnEvents = new();
        public List<string> hide = new() { "drops" };
        public int keep = 4;
        public float minScore = 0.6f;
        public string note = "";
    }

    public sealed class SolveFile
    {
        public string replay = "";
        /// <summary>Folder for the written shots, under the shots folder; the intent file's folder when empty.</summary>
        public string @out = "";
        public List<ShotIntent> intents = new();
    }

    /// <summary>
    /// The camera search: candidates in toric space around the two fighters (Lino and Christie 2015), hard rejects, the
    /// framing score over the whole shot, Nelder-Mead refinement from the ten best, a final ranking with the pixel pass,
    /// and the best shot in each angle bin. Never relaxes visibility.
    /// </summary>
    internal sealed class ShotSolver
    {
        private static readonly string[] RelaxOrder = { "composition", "angle", "size", "wider" };

        internal sealed class Candidate
        {
            public float Arc, Height, Fov, Scale = 1f;
            public int Side;
            public ShotFile Shot = null!;
            public Vector3 Camera;
            public ShotAnalysis Search = null!;
            public ShotAnalysis? Full;
            public float Score;
            public float? PixelScore;
            public string? PixelReject;
            public string Bin = "";
            public bool Pass;
            public List<string> Failures = new();
            public List<PixelSample>? Samples;
        }

        private readonly ShotPlayer _player;
        private readonly ShotIntent _intent;
        private readonly string _replay;
        private readonly float _aspect;
        private readonly ReplayPlayer _replayPlayer;
        private readonly Dictionary<(long, uint), float> _heights;
        private readonly CanopyIndex _canopies;
        private readonly ActorTrack _subject;
        private readonly ActorTrack? _partner;
        private readonly Dictionary<string, int> _rejects = new();
        private int _evaluations;
        private readonly List<string> _examples = new();
        private readonly Stopwatch _frameClock = new();

        internal ShotSolver(ShotPlayer player, ShotIntent intent, string replay, float aspect)
        {
            _player = player;
            _intent = intent;
            _replay = replay;
            _aspect = aspect;
            _replayPlayer = ReplayPlayer.Instance;
            _heights = ShotPlayer.Heights(_replayPlayer);
            _subject = _replayPlayer.Tracks.Get(intent.subject);
            if (!string.IsNullOrEmpty(intent.partner)) _partner = _replayPlayer.Tracks.Get(intent.partner!);
            if (intent.name.Length == 0 || intent.name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException($"Intent name '{intent.name}' is not a valid file name");
            if (intent.side is < -1 or > 1) throw new ArgumentException("side is 1, -1 or 0");
            if (intent.heightRange is not { Length: 2 } || intent.heightRange[1] < intent.heightRange[0]) throw new ArgumentException("heightRange is [low, high] in degrees");
            if (intent.angles < 1 || intent.heights < 1) throw new ArgumentException("angles and heights must be at least 1");
            if (!new[] { "static", "push", "dolly", "orbit", "track" }.Contains(intent.move)) throw new ArgumentException($"move '{intent.move}' must be static, push, dolly, orbit or track");
            Framing.ParseSize(intent.size ?? "full");
            Framing.ParsePlace(intent.place, aspect);
            (float start, float end) = Window();
            if (end <= start || intent.moment < start || intent.moment > end) throw new ArgumentException($"{intent.name}: the window must contain the moment {intent.moment}");
            Bounds area = _replayPlayer.Tracks.FightArea();
            area.Expand(new Vector3(60f, 30f, 60f));
            _canopies = CanopyIndex.Around(area, 0f);
        }

        private (float Start, float End) Window()
        {
            float start = _intent.window is { Length: 2 } ? _intent.window[0] : _intent.moment - 1.5f;
            float end = _intent.window is { Length: 2 } ? _intent.window[1] : _intent.moment + 1.0f;
            return (Mathf.Max(0f, start), Mathf.Min(_replayPlayer.Duration, end));
        }

        private float Height(ActorTrack track) => _heights.TryGetValue((track.User, track.Id), out float h) && h > 0f ? h : 1.8f;

        private static Vector3 Feet(ActorTrack track, float time)
        {
            track.Sample(Mathf.Clamp(time, track.Start, track.End), out Vector3 feet, out _);
            return feet;
        }

        // ---------------------------------------------------------------- size class

        private static string SizeName(JToken? size)
        {
            if (size == null) return "full";
            if (size.Type == JTokenType.String) return ((string)size!).Replace('-', '_');
            ((float min, float max), _) = Framing.ParseSize(size);
            float mid = Mathf.Sqrt(min * max);
            return Framing.Sizes.OrderBy(s => Mathf.Abs(Mathf.Log(Mathf.Sqrt(s.Value.Min * s.Value.Max) / mid))).First().Key;
        }

        private static string Wider(string size)
        {
            int index = Array.IndexOf(Framing.SizeOrder, size);
            return index >= 0 && index < Framing.SizeOrder.Length - 1 ? Framing.SizeOrder[index + 1] : size;
        }

        /// <summary>Field of view by shot size, from the framing reference: full and wider 40-55°, medium 30-40°, close 15-25°.</summary>
        private static float[] Fovs(string size) => size switch
        {
            "medium" or "cowboy" => new[] { 30f, 35f, 40f },
            "medium_close" or "close" => new[] { 18f, 22f, 25f },
            _ => new[] { 40f, 47f, 55f }
        };

        /// <summary>The body height fraction the subject's anchor sits at, and its screen height, by shot size.</summary>
        private static (float Body, float Screen) Anchor(string size) => size switch
        {
            "medium_close" or "close" => (0.93f, 0.64f),
            "medium" => (0.8f, 0.49f),
            "cowboy" or "knee" => (0.7f, 0.5f),
            "wide" or "extreme_wide" => (0.55f, 0.5f),
            _ => (0.55f, 0.53f)
        };

        /// <summary>The screen distance between the two fighters' anchors: close together in wide shots, on the thirds when tight.</summary>
        private static float Separation(string size) => size switch
        {
            "extreme_wide" => 0.06f,
            "wide" => 0.12f,
            "full" => 0.22f,
            "knee" or "cowboy" => 0.3f,
            _ => 0.36f
        };

        // ---------------------------------------------------------------- placement

        /// <summary>The camera-space ray through a frame point.</summary>
        private Vector3 ScreenRay(Vector2 point, float fov)
        {
            float tan = Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad);
            return new Vector3((2f * point.x - 1f) * tan * _aspect, (2f * point.y - 1f) * tan, 1f).normalized;
        }

        /// <summary>
        /// Places the camera for one set of search parameters. With a partner the camera lies in toric space: the two
        /// anchors appear at their screen points, arc runs from behind the partner (0) to behind the subject (1) and height
        /// turns the plane about the line between them. Without one it orbits the subject at the distance the size needs.
        /// </summary>
        private (Vector3 Position, Quaternion Rotation)? Place(float arc, float heightDeg, float fov, float scale, int side, string size)
        {
            float time = _intent.moment;
            (float body, float screenY) = Anchor(size);
            Vector3 feetA = Feet(_subject, time);
            float anchorHeight = Height(_subject) * body;
            Vector3 a = feetA + Vector3.up * anchorHeight;
            Vector2 placeA = PlaceA(side, size, screenY);
            if (_partner != null)
            {
                Vector3 feetB = Feet(_partner, time);
                Vector3 b = feetB + Vector3.up * Mathf.Min(anchorHeight, Height(_partner) * 0.9f);
                Vector3 ab = b - a;
                if (new Vector2(ab.x, ab.z).magnitude > 0.6f)
                {
                    Vector2 placeB = new(Mathf.Clamp(placeA.x + (side > 0 ? 1f : -1f) * Separation(size), 0.08f, 0.92f), screenY);
                    Vector3 rayA = ScreenRay(placeA, fov), rayB = ScreenRay(placeB, fov);
                    float alpha = Vector3.Angle(rayA, rayB) * Mathf.Deg2Rad;
                    float length = ab.magnitude;
                    Vector3 u = ab / length;
                    Vector3 across = new Vector3(u.z, 0f, -u.x).normalized * side;
                    Vector3 up = Vector3.ProjectOnPlane(Vector3.up, u).normalized;
                    float phi = heightDeg * Mathf.Deg2Rad;
                    Vector3 w = (Mathf.Cos(phi) * across + Mathf.Sin(phi) * up).normalized;
                    // beta is the angle at the subject between the line to the partner and the line to the camera.
                    float beta = Mathf.Lerp(0.05f, Mathf.PI - alpha - 0.05f, Mathf.Clamp01(arc));
                    float toCamera = length * Mathf.Sin(alpha + beta) / Mathf.Max(0.01f, Mathf.Sin(alpha));
                    Vector3 camera = a + (Mathf.Cos(beta) * u + Mathf.Sin(beta) * w) * toCamera;
                    Vector3 middle = (a + b) * 0.5f;
                    // Toric space gives the direction; the distance is the one the intended size needs (times scale).
                    Vector3 bodyMiddle = feetA + Vector3.up * Height(_subject) * 0.5f;
                    float wanted = Height(_subject) / (2f * Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * SizeMid(size)) * scale;
                    camera = bodyMiddle + (camera - bodyMiddle).normalized * wanted;
                    // The rotation that takes the two screen rays to the two world directions, then levelled.
                    Vector3 worldA = (a - camera).normalized, worldB = (b - camera).normalized;
                    Quaternion screen = Quaternion.LookRotation(rayA, Vector3.Cross(rayA, rayB));
                    Quaternion world = Quaternion.LookRotation(worldA, Vector3.Cross(worldA, worldB));
                    if (Vector3.Cross(rayA, rayB).sqrMagnitude < 1e-8f || Vector3.Cross(worldA, worldB).sqrMagnitude < 1e-8f) return null;
                    Quaternion rotation = world * Quaternion.Inverse(screen);
                    Vector3 forward = rotation * Vector3.forward;
                    // Without an intended place, aim between the two fighters so both stay in the frame when they are far apart.
                    if (_intent.place == null || _intent.place.Type == JTokenType.Null) forward = (middle - camera).normalized;
                    return (camera, Quaternion.LookRotation(forward, Vector3.up));
                }
            }
            // One subject: arc is the angle around the subject's facing, on the requested side.
            Quaternion facing = Quaternion.Euler(0f, Facing(time), 0f);
            float yaw = Mathf.Lerp(-170f, 170f, Mathf.Clamp01(arc));
            float distance = Height(_subject) / (2f * Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * SizeMid(size)) * scale;
            Vector3 direction = facing * Quaternion.Euler(-heightDeg, yaw, 0f) * Vector3.forward;
            Vector3 position = a + direction * distance;
            return (position, Aim(position, a, placeA, fov));
        }

        /// <summary>The size the camera distance aims for: the middle of the intent's own range, or of a named size once relaxed.</summary>
        private float SizeMid(string size)
        {
            if (_intent.size is JArray && size == SizeName(_intent.size))
            {
                ((float min, float max), _) = Framing.ParseSize(_intent.size);
                return Mathf.Sqrt(min * max);
            }
            return Framing.Sizes.TryGetValue(size, out (float Min, float Max) range) ? Mathf.Sqrt(range.Min * range.Max) : 0.8f;
        }

        private Vector2 PlaceA(int side, string size, float screenY)
        {
            if (Framing.ParsePlace(_intent.place, _aspect) is Vector2 place) return place;
            return new Vector2(0.5f - (side > 0 ? 1f : -1f) * Separation(size) * 0.5f, screenY);
        }

        private float Facing(float time)
        {
            _subject.Sample(Mathf.Clamp(time, _subject.Start, _subject.End), out _, out Quaternion rotation);
            return rotation.eulerAngles.y;
        }

        /// <summary>A level camera rotation that puts a point at a frame position.</summary>
        private Quaternion Aim(Vector3 camera, Vector3 point, Vector2 place, float fov)
        {
            Vector3 ray = ScreenRay(place, fov);
            Vector3 toPoint = (point - camera).normalized;
            float yaw = Mathf.Atan2(toPoint.x, toPoint.z) * Mathf.Rad2Deg - Mathf.Atan2(ray.x, ray.z) * Mathf.Rad2Deg;
            float pitch = -Mathf.Asin(Mathf.Clamp(toPoint.y, -1f, 1f)) * Mathf.Rad2Deg + Mathf.Asin(Mathf.Clamp(ray.y, -1f, 1f)) * Mathf.Rad2Deg;
            return Quaternion.Euler(pitch, yaw, 0f);
        }

        // ---------------------------------------------------------------- shot files

        private ShotFile Build(Vector3 camera, Quaternion rotation, float fov, string size, string suffix)
        {
            (float start, float end) = Window();
            float time = _intent.moment;
            Vector3 feet = Feet(_subject, time);
            Vector3 forward = rotation * Vector3.forward;
            float distance = Vector3.Distance(camera, feet + Vector3.up * Height(_subject) * 0.5f);
            Vector3 look = camera + forward * distance;
            // A two-shot without an intended place follows the midpoint of the fighters; otherwise the look follows the subject.
            JObject lookAt = new() { ["actor"] = _intent.subject, ["height"] = 0, ["offset"] = Point(look - feet) };
            if (_partner != null && (_intent.place == null || _intent.place.Type == JTokenType.Null))
            {
                Vector3 middle = (feet + Feet(_partner, time)) * 0.5f;
                lookAt = new JObject { ["actor"] = _intent.subject, ["between"] = _intent.partner, ["height"] = 0, ["offset"] = Point(look - middle) };
            }
            float fraction = (time - start) / Mathf.Max(0.01f, end - start);
            List<CameraKey> keys = new();
            CameraKey Key(float t, JToken position) => new() { t = (float)Math.Round(t, 3), position = position, lookAt = lookAt.DeepClone(), fov = (float)Math.Round(fov, 2), ease = "linear" };
            Vector3 right = rotation * Vector3.right;
            switch (_intent.move)
            {
                case "push":
                {
                    Vector3 travel = forward * distance * 0.2f;
                    keys.Add(Key(start, Point(camera - travel * fraction)));
                    keys.Add(Key(end, Point(camera + travel * (1f - fraction))));
                    break;
                }
                case "dolly":
                {
                    Vector3 travel = right * distance * 0.25f;
                    keys.Add(Key(start, Point(camera - travel * fraction)));
                    keys.Add(Key(end, Point(camera + travel * (1f - fraction))));
                    break;
                }
                case "orbit":
                {
                    Vector3 centre = _partner != null ? (feet + Feet(_partner, time)) * 0.5f : feet;
                    foreach ((float t, float degrees) in new[] { (start, -15f * fraction / 0.5f), (time, 0f), (end, 15f * (1f - fraction) / 0.5f) })
                        keys.Add(Key(t, Point(centre + Quaternion.Euler(0f, degrees, 0f) * (camera - centre))));
                    break;
                }
                case "track":
                    keys.Add(Key(start, new JObject { ["actor"] = _intent.subject, ["offset"] = Point(camera - feet) }));
                    break;
                default:
                    keys.Add(Key(start, Point(camera)));
                    break;
            }
            ShotFile shot = new()
            {
                replay = _replay, name = _intent.name + suffix, start = (float)Math.Round(start, 3), end = (float)Math.Round(end, 3), camera = keys,
                lookSmoothing = _intent.lookSmoothing ?? (_intent.move switch { "orbit" => 0f, "track" => 0.25f, "static" => 0.15f, _ => 0.2f }),
                keepInFrame = _partner != null ? new List<string> { _intent.subject, _intent.partner! } : new List<string> { _intent.subject },
                hide = _intent.hide, speed = _intent.speed, shakeOnEvents = _intent.shakeOnEvents,
                framing = new FramingSpec
                {
                    subject = _intent.subject, partner = _intent.partner, size = size == SizeName(_intent.size) ? (_intent.size ?? "full") : size, place = _intent.place,
                    minVisible = _intent.minVisible, maxForeground = _intent.maxForeground, moment = _intent.moment
                },
                note = _intent.note
            };
            return shot;
        }

        private static JArray Point(Vector3 value) => new(Math.Round(value.x, 3), Math.Round(value.y, 3), Math.Round(value.z, 3));

        // ---------------------------------------------------------------- evaluation

        private CheckSettings Search(int side) => new()
        {
            Step = 0.1f, Fps = 30, GridX = 8, GridY = 5, HardRejects = true, Side = side, SideTime = _intent.moment,
            SideA = Feet(_subject, _intent.moment), SideB = _partner != null ? Feet(_partner, _intent.moment) : Feet(_subject, _intent.moment) + Vector3.forward
        };

        private Candidate? Evaluate(float arc, float height, float fov, float scale, int side, string size, Dictionary<string, float> weights)
        {
            _evaluations++;
            (Vector3 Position, Quaternion Rotation)? placed = Place(arc, height, fov, scale, side, size);
            if (placed == null) { Count("no placement"); return null; }
            ShotFile shot = Build(placed.Value.Position, placed.Value.Rotation, fov, size, "");
            shot.framing!.weights = weights;
            ShotEvaluator evaluator = new(shot, _replayPlayer.Tracks, _heights, _replayPlayer.Events);
            CheckSettings settings = Search(_partner != null ? side : 0);
            ShotAnalysis analysis = ShotCheck.Analyse(evaluator, _aspect, settings, _canopies);
            Candidate candidate = new() { Arc = arc, Height = height, Fov = fov, Scale = scale, Side = side, Shot = shot, Camera = placed.Value.Position, Search = analysis, Score = analysis.Score };
            if (analysis.Rejected != null)
            {
                Count(Reason(analysis.Rejected));
                if (_examples.Count < 40 && _evaluations % 7 == 0) _examples.Add($"side {side} arc {arc:F2} height {height:F0} fov {fov:F0}: {analysis.Rejected}");
            }
            return candidate;
        }

        private static string Reason(string rejected) => rejected.Contains(" at ") ? rejected.Substring(0, rejected.LastIndexOf(" at ", StringComparison.Ordinal)) : rejected;

        private void Count(string reason)
        {
            string key = reason.StartsWith("camera within 0.4 m of") && !reason.Contains("solid") ? "camera within 0.4 m of an actor"
                : reason.StartsWith("camera ") && reason.EndsWith("above the surface") ? "camera under 0.5 m above the surface"
                : reason.Contains("in-frame") ? "fighter in-frame under 0.8" : reason.StartsWith("visible under") ? "visible under 0.5 for more than 15% of steps" : reason;
            _rejects[key] = _rejects.TryGetValue(key, out int count) ? count + 1 : 1;
        }

        /// <summary>Nelder-Mead over arc, height, field of view and distance, from one start.</summary>
        private Candidate Refine(Candidate start, string size, Dictionary<string, float> weights, int budget)
        {
            float[] lower = { 0f, _intent.heightRange[0], Fovs(size).Min() - 5f, 0.6f }, upper = { 1f, _intent.heightRange[1], Fovs(size).Max() + 5f, 1.6f };
            Candidate best = start;
            float Cost(float[] x)
            {
                for (int i = 0; i < 4; i++) x[i] = Mathf.Clamp(x[i], lower[i], upper[i]);
                Candidate? candidate = Evaluate(x[0], x[1], x[2], x[3], start.Side, size, weights);
                if (candidate == null || candidate.Search.Rejected != null) return 1f;
                if (candidate.Score > best.Score) best = candidate;
                return -candidate.Score;
            }
            float[][] simplex = new float[5][];
            float[] steps = { 0.06f, 5f, 5f, 0.15f };
            simplex[0] = new[] { start.Arc, start.Height, start.Fov, start.Scale };
            for (int i = 0; i < 4; i++)
            {
                simplex[i + 1] = (float[])simplex[0].Clone();
                simplex[i + 1][i] += simplex[0][i] + steps[i] > upper[i] ? -steps[i] : steps[i];
            }
            float[] cost = simplex.Select(x => Cost((float[])x.Clone())).ToArray();
            int used = 5;
            while (used < budget)
            {
                int[] order = Enumerable.Range(0, 5).OrderBy(i => cost[i]).ToArray();
                simplex = order.Select(i => simplex[i]).ToArray();
                cost = order.Select(i => cost[i]).ToArray();
                if (cost[4] - cost[0] < 0.002f) break;
                float[] centroid = new float[4];
                for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) centroid[j] += simplex[i][j] / 4f;
                float[] Along(float t) => Enumerable.Range(0, 4).Select(j => centroid[j] + t * (simplex[4][j] - centroid[j])).ToArray();
                float[] reflected = Along(-1f);
                float reflectedCost = Cost(reflected); used++;
                if (reflectedCost < cost[0])
                {
                    float[] expanded = Along(-2f);
                    float expandedCost = Cost(expanded); used++;
                    if (expandedCost < reflectedCost) { simplex[4] = expanded; cost[4] = expandedCost; }
                    else { simplex[4] = reflected; cost[4] = reflectedCost; }
                }
                else if (reflectedCost < cost[3]) { simplex[4] = reflected; cost[4] = reflectedCost; }
                else
                {
                    float[] contracted = Along(0.5f);
                    float contractedCost = Cost(contracted); used++;
                    if (contractedCost < cost[4]) { simplex[4] = contracted; cost[4] = contractedCost; }
                    else
                    {
                        for (int i = 1; i < 5; i++)
                        {
                            for (int j = 0; j < 4; j++) simplex[i][j] = simplex[0][j] + 0.5f * (simplex[i][j] - simplex[0][j]);
                            cost[i] = Cost(simplex[i]); used++;
                        }
                    }
                }
            }
            return best;
        }

        // ---------------------------------------------------------------- bins

        /// <summary>Where the camera is relative to the subject at the moment: the edit needs distinct angles.</summary>
        private string Bin(Candidate candidate)
        {
            float time = _intent.moment;
            Vector3 feet = Feet(_subject, time);
            _subject.Sample(Mathf.Clamp(time, _subject.Start, _subject.End), out _, out Quaternion rotation);
            Vector3 heading = Framing.Heading(_subject, time, rotation, out _);
            Vector3 toCamera = candidate.Camera - (feet + Vector3.up * Height(_subject) * 0.6f);
            float elevation = Mathf.Asin(Mathf.Clamp(toCamera.normalized.y, -1f, 1f)) * Mathf.Rad2Deg;
            Vector3 flat = new(toCamera.x, 0f, toCamera.z);
            float angle = Vector3.Angle(heading, flat);
            if (elevation >= 20f) return "high_wide";
            if (angle <= 25f) return "front";
            if (angle <= 70f) return "front_three_quarter";
            if (angle <= 115f) return "side";
            bool nearSubject = _partner == null || Vector3.Distance(candidate.Camera, feet) < Vector3.Distance(candidate.Camera, Feet(_partner, time));
            return nearSubject && flat.magnitude < 4.5f ? "over_shoulder" : "back";
        }

        // ---------------------------------------------------------------- run

        private bool Yield()
        {
            if (_frameClock.ElapsedMilliseconds < 30) return false;
            _frameClock.Restart();
            return true;
        }

        internal Dictionary<string, object?> Report = new();
        internal List<string> Written = new();

        internal IEnumerator Run(string folder, bool usePixels, int keepOverride, Action<string> progress)
        {
            Stopwatch clock = Stopwatch.StartNew();
            _frameClock.Restart();
            string size = SizeName(_intent.size);
            List<string> relaxed = new();
            Dictionary<string, float> weights = new(Framing.DefaultWeights);
            List<Candidate> finalists = new(), kept = new();
            List<Dictionary<string, object?>> stages = new();
            int keep = keepOverride > 0 ? keepOverride : _intent.keep;
            for (int stage = 0; stage <= RelaxOrder.Length; stage++)
            {
                if (stage > 0)
                {
                    string drop = RelaxOrder[stage - 1];
                    relaxed.Add(drop);
                    if (drop == "wider")
                    {
                        string wider = Wider(size);
                        if (wider == size) break;
                        size = wider;
                    }
                    else weights[drop] = 0f;
                }
                bool newSearch = stage == 0 || RelaxOrder[stage - 1] == "wider";
                if (newSearch)
                {
                    List<Candidate> pool = new();
                    float[] fovs = _intent.fovs is { Length: > 0 } ? _intent.fovs : Fovs(size);
                    int[] sides = _partner == null ? new[] { 1 } : _intent.side == 0 ? new[] { 1, -1 } : new[] { _intent.side };
                    int total = sides.Length * _intent.angles * _intent.heights * fovs.Length, done = 0;
                    foreach (int side in sides)
                        for (int a = 0; a < _intent.angles; a++)
                            for (int h = 0; h < _intent.heights; h++)
                                foreach (float fov in fovs)
                                {
                                    float arc = (a + 0.5f) / _intent.angles;
                                    float height = Mathf.Lerp(_intent.heightRange[0], _intent.heightRange[1], _intent.heights == 1 ? 0.3f : h / (float)(_intent.heights - 1));
                                    Candidate? candidate = Evaluate(arc, height, fov, 1f, side, size, weights);
                                    if (candidate != null && candidate.Search.Rejected == null) pool.Add(candidate);
                                    done++;
                                    if (Yield()) { progress($"solve {_intent.name} search {done}/{total}"); yield return null; }
                                }
                    // Refine from the ten best and from the best of each angle bin, so every bin has a refined shot.
                    foreach (Candidate candidate in pool) candidate.Bin = Bin(candidate);
                    List<Candidate> bestPerBin = pool.GroupBy(c => c.Bin).Select(g => g.OrderByDescending(c => c.Score).First()).ToList();
                    List<Candidate> starts = pool.OrderByDescending(c => c.Score).Take(10).Concat(bestPerBin).Distinct().ToList();
                    List<Candidate> refined = new();
                    for (int index = 0; index < starts.Count; index++)
                    {
                        progress($"solve {_intent.name} refine {index + 1}/{starts.Count}");
                        yield return null;
                        refined.Add(Refine(starts[index], size, weights, 32));
                    }
                    // Keep the refined shots, the best unrefined ones and the three best of each bin.
                    finalists = refined.Concat(pool.OrderByDescending(c => c.Score).Take(20))
                        .Concat(pool.GroupBy(c => c.Bin).SelectMany(g => g.OrderByDescending(c => c.Score).Take(3)))
                        .GroupBy(c => (Mathf.Round(c.Camera.x * 4f), Mathf.Round(c.Camera.y * 4f), Mathf.Round(c.Camera.z * 4f)))
                        .Select(g => g.First()).ToList();
                    foreach (Candidate candidate in finalists)
                    {
                        ShotEvaluator evaluator = new(candidate.Shot, _replayPlayer.Tracks, _heights, _replayPlayer.Events);
                        CheckSettings strict = Search(candidate.Side);
                        strict.Step = 0.05f; strict.Fps = 60; strict.GridX = 16; strict.GridY = 9;
                        ShotAnalysis confirm = ShotCheck.Analyse(evaluator, _aspect, strict, _canopies);
                        if (confirm.Rejected != null) { candidate.Search = confirm; continue; }
                        candidate.Full = ShotCheck.Analyse(evaluator, _aspect, new CheckSettings(), ShotCheck.Canopies(evaluator));
                        candidate.Bin = Bin(candidate);
                        if (Yield()) { progress($"solve {_intent.name} full check"); yield return null; }
                    }
                    finalists = finalists.Where(c => c.Full != null).ToList();
                }
                foreach (Candidate candidate in finalists)
                {
                    candidate.Failures = Failures(candidate, relaxed);
                    candidate.Score = Rescored(candidate, weights);
                }
                if (usePixels)
                {
                    // The ten best, and the best of each bin, ranked again by pixels; repeated while the order brings up
                    // candidates without a pixel count.
                    for (int round = 0; round < 3; round++)
                    {
                        List<Candidate> ranked = finalists.Where(c => c.PixelReject == null).OrderByDescending(Order).Take(10)
                            .Concat(finalists.Where(c => c.PixelReject == null && c.Failures.Count == 0).GroupBy(c => c.Bin).Select(g => g.OrderByDescending(Order).First()))
                            .Distinct().Where(c => c.PixelScore == null).ToList();
                        if (ranked.Count == 0) break;
                        progress($"solve {_intent.name} pixels for {ranked.Count} shots");
                        List<List<PixelSample>> samples = new();
                        yield return _player.PixelSamples(ranked.Select(c => new ShotEvaluator(c.Shot, _replayPlayer.Tracks, _heights, _replayPlayer.Events)).ToList(), _aspect, 4f, null, samples);
                        for (int index = 0; index < ranked.Count; index++)
                        {
                            ranked[index].Samples = samples[index];
                            PixelScore(ranked[index], samples[index], weights);
                        }
                    }
                }
                foreach (Candidate candidate in finalists)
                {
                    if (candidate.Samples != null) PixelScore(candidate, candidate.Samples, weights);
                    candidate.Score = candidate.PixelScore ?? Rescored(candidate, weights);
                    candidate.Pass = candidate.Failures.Count == 0 && candidate.Score >= _intent.minScore && (!usePixels || (candidate.PixelScore != null && candidate.PixelReject == null));
                }
                stages.Add(new Dictionary<string, object?> { ["relaxed"] = relaxed.ToList(), ["size"] = size, ["finalists"] = finalists.Count, ["passing"] = finalists.Count(c => c.Pass) });
                if (finalists.Count > 0) kept = finalists;
                if (finalists.Count(c => c.Pass) >= 1) break;
            }
            // A wider search that met no hard rule leaves the last stage that had candidates.
            finalists = kept;
            // The best passing shot in each angle bin, then, for bins without one, the best shot that met the hard rules
            // (marked "pass": false), so the edit has distinct angles to choose from.
            List<Candidate> chosen = finalists.Where(c => c.Pass).GroupBy(c => c.Bin).Select(g => g.OrderByDescending(c => c.Score).First())
                .OrderByDescending(c => c.Score).Take(keep).ToList();
            foreach (Candidate candidate in finalists.Where(c => !c.Pass && (c.PixelReject == null || !usePixels)).GroupBy(c => c.Bin)
                .Select(g => g.OrderByDescending(c => c.Score).First()).OrderByDescending(c => c.Score))
                if (chosen.Count < keep && chosen.All(c => c.Bin != candidate.Bin)) chosen.Add(candidate);
            string directory = folder;
            Directory.CreateDirectory(directory);
            foreach (Candidate candidate in chosen)
            {
                ShotFile shot = candidate.Shot;
                shot.name = $"{_intent.name}_{candidate.Bin}";
                shot.framing!.weights = null;
                JObject json = JObject.FromObject(shot, JsonSerializer.Create(new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));
                json["score"] = Math.Round(candidate.Score, 3);
                json["bin"] = candidate.Bin;
                json["pass"] = candidate.Pass;
                json["solve"] = JObject.FromObject(Describe(candidate, relaxed));
                string path = Path.Combine(directory, shot.name + ".json");
                File.WriteAllText(path, json.ToString(Formatting.Indented));
                Written.Add(path);
            }
            Report = new Dictionary<string, object?>
            {
                ["intent"] = JObject.FromObject(_intent), ["seconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1), ["evaluations"] = _evaluations, ["rejects"] = _rejects, ["rejectExamples"] = _examples,
                ["stages"] = stages, ["relaxed"] = relaxed, ["written"] = Written.Select(Path.GetFileName).ToList(),
                ["finalists"] = finalists.OrderByDescending(c => c.Score).Select(c => Describe(c, relaxed)).ToList()
            };
            File.WriteAllText(Path.Combine(directory, _intent.name + ".solve.json"), JsonConvert.SerializeObject(Report, Formatting.Indented));
        }

        /// <summary>Ranking order: candidates without failing issues first, then by score.</summary>
        private static float Order(Candidate candidate) => (candidate.Failures.Count == 0 ? 1f : 0f) + candidate.Score;

        private float Rescored(Candidate candidate, Dictionary<string, float> weights) =>
            Framing.ShotScore((candidate.Full ?? candidate.Search).Steps.Where(s => s.Subject != null).Select(s => Framing.Rescore(s, weights)).ToList());

        /// <summary>Scores the shot again with each step's visibility taken from the nearest pixel sample.</summary>
        private void PixelScore(Candidate candidate, List<PixelSample> samples, Dictionary<string, float> weights)
        {
            if (samples.Count == 0 || candidate.Full == null) return;
            int low = 0, counted = 0;
            foreach (PixelSample sample in samples)
            {
                bool partnerCounts = sample.Time >= _intent.moment - 0.5f;
                List<PixelActor> fighters = sample.Actors.Where(a => a.Present && (a.Id == _intent.subject || (a.Id == _intent.partner && partnerCounts))).ToList();
                if (fighters.Count == 0) continue;
                counted++;
                if (fighters.Any(a => a.VisibleFraction < 0.5f)) low++;
            }
            if (counted > 0 && low > 0.15f * counted && candidate.PixelReject == null)
            {
                candidate.PixelReject = $"pixels: visible under 0.5 for {100f * low / counted:F0}% of samples";
                Count("pixels: visible under 0.5 for more than 15% of samples");
            }
            List<float> scores = new();
            foreach (FrameMeasure step in candidate.Full.Steps.Where(s => s.Subject != null))
            {
                PixelSample nearest = samples.OrderBy(s => Mathf.Abs(s.Time - step.Time)).First();
                PixelActor? subject = nearest.Actors.FirstOrDefault(a => a.Id == _intent.subject && a.Present);
                PixelActor? partner = nearest.Actors.FirstOrDefault(a => a.Id == _intent.partner && a.Present);
                if (subject == null) { scores.Add(Framing.Rescore(step, weights)); continue; }
                float visibility = partner != null ? 0.7f * subject.VisibleFraction + 0.3f * partner.VisibleFraction : subject.VisibleFraction;
                scores.Add(Framing.Rescore(step, weights, new Dictionary<string, float> { ["visibility"] = visibility }));
            }
            candidate.PixelScore = Framing.ShotScore(scores);
        }

        /// <summary>Check issues that keep a shot from passing; visibility issues are never relaxed.</summary>
        private static List<string> Failures(Candidate candidate, List<string> relaxed)
        {
            List<string> failures = new();
            foreach (Dictionary<string, object?> issue in candidate.Full?.Issues ?? new List<Dictionary<string, object?>>())
            {
                string type = (string)issue["type"]!;
                bool counts = type switch
                {
                    "subject_hidden" or "pair_split" or "strike_frame" or "foreground_cover" or "below_ground" or "inside_object" or "inside_foliage" => true,
                    "subject_size" => !relaxed.Contains("size"),
                    "subject_place" => !relaxed.Contains("composition"),
                    _ => false
                };
                if (counts) failures.Add($"{type} {issue["from"]}-{issue["to"]}");
            }
            return failures;
        }

        private Dictionary<string, object?> Describe(Candidate candidate, List<string> relaxed)
        {
            FrameMeasure? moment = candidate.Full?.Steps.OrderBy(s => Mathf.Abs(s.Time - _intent.moment)).FirstOrDefault();
            return new Dictionary<string, object?>
            {
                ["bin"] = candidate.Bin, ["score"] = Math.Round(candidate.Score, 3), ["rayScore"] = candidate.Full != null ? Math.Round(candidate.Full.Score, 3) : null,
                ["pixelScore"] = candidate.PixelScore.HasValue ? Math.Round(candidate.PixelScore.Value, 3) : null, ["pixelReject"] = candidate.PixelReject,
                ["pass"] = candidate.Pass, ["failures"] = candidate.Failures, ["relaxed"] = relaxed,
                ["side"] = candidate.Side, ["arc"] = Math.Round(candidate.Arc, 3), ["height"] = Math.Round(candidate.Height, 2), ["fov"] = Math.Round(candidate.Fov, 2), ["scale"] = Math.Round(candidate.Scale, 3),
                ["camera"] = ReplayEvents.Round(candidate.Camera), ["momentSize"] = moment?.Subject != null ? Math.Round(moment.Subject.Size, 3) : null,
                ["issues"] = candidate.Full?.Issues.Select(i => $"{i["type"]} {i["subject"]} {i["from"]}-{i["to"]}").ToList()
            };
        }

        // ---------------------------------------------------------------- file

        internal static SolveFile Read(string path)
        {
            JToken root;
            try { root = JToken.Parse(File.ReadAllText(path)); }
            catch (JsonException error) { throw new ArgumentException($"{Path.GetFileName(path)} is not valid JSON: {error.Message}"); }
            SolveFile file = root is JObject obj && obj["intents"] != null ? root.ToObject<SolveFile>()! : new SolveFile { intents = new List<ShotIntent> { root.ToObject<ShotIntent>()! } };
            if (root is JObject top && top["intents"] == null) { file.replay = (string?)top["replay"] ?? ""; file.@out = (string?)top["out"] ?? ""; }
            if (file.intents.Count == 0) throw new ArgumentException("The intent file has no intents");
            return file;
        }
    }
}
