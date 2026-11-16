using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace cameraTools.Replay.Shots
{
    /// <summary>The shot file's "framing" block: what the shot is meant to show. Every field is optional.</summary>
    public sealed class FramingSpec
    {
        /// <summary>The featured actor (user:id). Defaults to the first keepInFrame actor.</summary>
        public string? subject;
        /// <summary>The second fighter. Defaults to the second keepInFrame actor.</summary>
        public string? partner;
        /// <summary>A named size from <see cref="Framing.Sizes"/>, or [min, max] body height ÷ frame height.</summary>
        public JToken? size;
        /// <summary>"left", "centre" or "right" third, or [x, y] in the frame (0-1, y up).</summary>
        public JToken? place;
        public float? minVisible;
        public float? maxForeground;
        /// <summary>The impact time in replay seconds: both fighters must be fully in frame and visible then.</summary>
        public float? moment;
        /// <summary>
        /// [from, to] replay seconds where the framing rules apply. Outside it the fighters may enter and leave the frame:
        /// a locked-off shot holds its frame while the action plays through it. The whole shot when absent.
        /// </summary>
        public float[]? action;
        /// <summary>Score term weights by name (visibility, size, position, margin, angle, composition).</summary>
        public Dictionary<string, float>? weights;
    }

    /// <summary>One actor measured in one frame. Frame coordinates run 0-1, x to the right and y up.</summary>
    public sealed class ActorFrame
    {
        public string Id = "";
        public string Role = "";
        public string Name = "";
        /// <summary>The projected capsule (both end circles), not clipped to the frame.</summary>
        public Rect Box;
        public Rect HeadBox;
        /// <summary>Body height ÷ frame height: the projected feet-to-head distance.</summary>
        public float Size;
        /// <summary>The centre of the part of the box inside the frame (the whole box when none of it is).</summary>
        public Vector2 Center;
        /// <summary>Frame height above the top of the head; negative when the head is cut off.</summary>
        public float Headroom;
        /// <summary>Space ahead ÷ (space ahead + space behind), or null when the actor faces the camera or away.</summary>
        public float? Lead;
        public bool Moving;
        public float InFrame;
        public float Visible;
        /// <summary>Feet, eye and joint heights projected to the frame, for the edge-cut test.</summary>
        public float FeetY, HeadY;
        public float Distance;
        public float BodyHeight;
        /// <summary>Much taller than the subject: in-frame counts only the part of the body up to 1.3 times the subject's height, where the fight happens.</summary>
        public bool Large;
    }

    public sealed class FrameMeasure
    {
        public float Time;
        public Vector3 Camera;
        public Quaternion Rotation;
        public float Fov;
        public readonly List<ActorFrame> Actors = new();
        public float Foreground;
        public bool HeadCovered;
        /// <summary>Frame width from the left edge of the subject and partner boxes to the right edge, when both are at least half in frame.</summary>
        public float? PairSpread;
        /// <summary>The largest share of the frame one unmeasured actor covers nearer than the subject, and its name.</summary>
        public float InFront;
        public string InFrontName = "";
        public float Score;
        public readonly Dictionary<string, float> Terms = new();
        public ActorFrame? Subject => Actors.FirstOrDefault(a => a.Role == "subject");
        public ActorFrame? Partner => Actors.FirstOrDefault(a => a.Role == "partner");
    }

    /// <summary>
    /// Framing measurements from the recorded tracks: each actor is a vertical capsule of its recorded height and radius.
    /// Replay characters have no colliders, so other actors block the view through their capsules, solid objects through
    /// physics rays, and tree canopies through the canopy boxes. Used by check, solve, preview overlays and the render log.
    /// </summary>
    internal sealed class Framing
    {
        internal static readonly int SolidMask = LayerMask.GetMask("piece", "static_solid", "Default", "Default_small", "vehicle", "terrain");
        internal static readonly int TerrainMask = LayerMask.GetMask("terrain");

        /// <summary>Shot sizes as full body height ÷ frame height (derived from where the frame edge cuts the body).</summary>
        internal static readonly Dictionary<string, (float Min, float Max)> Sizes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["extreme_wide"] = (0.03f, 0.15f), ["wide"] = (0.2f, 0.5f), ["full"] = (0.75f, 0.9f), ["knee"] = (1.2f, 1.4f),
            ["cowboy"] = (1.4f, 1.6f), ["medium"] = (2.2f, 2.5f), ["medium_close"] = (3.0f, 3.4f), ["close"] = (4.5f, 5.5f)
        };
        internal static readonly string[] SizeOrder = { "close", "medium_close", "medium", "cowboy", "knee", "full", "wide", "extreme_wide" };
        /// <summary>A featured player smaller than a third of the frame height is a speck (the default size rule).</summary>
        internal const float SpeckLimit = 0.33f;
        /// <summary>Subject and partner in-frame ratio needed across the action (calibrated from the user's labels: a body cut by the bottom edge).</summary>
        internal const float InFrameNeeded = 0.95f;
        /// <summary>
        /// The same rule in the pixel pass, which counts carried weapons and capes: a sword swinging past the frame edge
        /// leaves the body inside (mountainFight1 b04_fight_high_wide at 19.70, labelled good, is 89% in frame by pixels).
        /// </summary>
        internal const float PixelInFrameNeeded = 0.85f;
        /// <summary>The widest the pair may spread across the frame (calibrated: fighters at opposite edges with an empty middle).</summary>
        internal const float MaxPairSpread = 0.7f;
        /// <summary>The share of the frame one unmeasured actor nearer than the subject may cover (calibrated: a pack wolf crossing in front).</summary>
        internal const float MaxInFront = 0.15f;
        // Joint heights as a fraction of body height: ankle, knee, waist, neck.
        private static readonly (string Name, float Height)[] Joints = { ("ankle", 0.04f), ("knee", 0.28f), ("waist", 0.53f), ("neck", 0.87f) };
        internal static readonly Dictionary<string, float> DefaultWeights = new()
        {
            ["visibility"] = 0.35f, ["size"] = 0.15f, ["position"] = 0.15f, ["margin"] = 0.15f, ["angle"] = 0.10f, ["composition"] = 0.10f
        };

        internal readonly ShotEvaluator Evaluator;
        internal readonly ReplayPlayer Player;
        internal readonly float Aspect;
        internal readonly string? SubjectId, PartnerId;
        internal readonly ActorTrack? SubjectTrack, PartnerTrack;
        internal readonly (float Min, float Max) SizeRange;
        internal readonly bool SizeSet;
        internal readonly Vector2? Place;
        internal readonly float MinVisible, MaxForeground;
        internal readonly float? Moment;
        internal readonly (float From, float To)? Action;
        internal readonly Dictionary<string, float> Weights;
        /// <summary>Measured actors in order: subject, partner, then the other keepInFrame actors.</summary>
        internal readonly List<(ActorTrack Track, string Role)> Measured = new();
        private readonly List<ActorTrack> _bodies;
        private readonly Dictionary<(long, uint), float> _radii = new();
        private readonly Dictionary<(long, uint), float> _reach = new();
        private static readonly Dictionary<int, float> ReachCache = new();

        /// <summary>
        /// How far an actor's drawn body reaches from its centre line: half the horizontal size of its prefab's meshes,
        /// times 0.5, never less than the collider radius. A collider is thinner than the body it stands for (arms,
        /// wings, a leaning torso), so other actors block the view with this radius.
        /// </summary>
        private static float Reach(int prefabHash)
        {
            if (ReachCache.TryGetValue(prefabHash, out float cached)) return cached;
            float reach = 0f;
            GameObject? prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabHash) : null;
            if (prefab != null)
            {
                Matrix4x4 toRoot = prefab.transform.worldToLocalMatrix;
                bool any = false;
                Bounds total = default;
                foreach (Renderer renderer in prefab.GetComponentsInChildren<Renderer>(true))
                {
                    Bounds local;
                    if (renderer is SkinnedMeshRenderer skinned) local = skinned.localBounds;
                    else if (renderer is MeshRenderer && renderer.GetComponent<MeshFilter>()?.sharedMesh is Mesh mesh) local = mesh.bounds;
                    else continue;
                    if (!renderer.gameObject.activeSelf) continue;
                    Matrix4x4 matrix = toRoot * renderer.transform.localToWorldMatrix;
                    for (int corner = 0; corner < 8; corner++)
                    {
                        Vector3 point = matrix.MultiplyPoint3x4(local.center + Vector3.Scale(local.extents, new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1)));
                        if (!any) { total = new Bounds(point, Vector3.zero); any = true; } else total.Encapsulate(point);
                    }
                }
                if (any) reach = Mathf.Max(total.extents.x, total.extents.z) * prefab.transform.localScale.x * 0.5f;
            }
            ReachCache[prefabHash] = reach;
            return reach;
        }

        /// <summary>The radius other actors' views test against: the collider radius or the body's reach, whichever is larger.</summary>
        internal float BlockRadius(ActorTrack track) => Mathf.Max(Radius(track), _reach.TryGetValue((track.User, track.Id), out float reach) ? Mathf.Min(reach, Evaluator.Height(track) * 0.45f) : 0f);
        private readonly HashSet<(long, uint)> _deaths;
        private readonly CanopyIndex _canopies;
        private CanopyIndex _local = new();
        /// <summary>Rays per side of the foreground grid; the search uses a coarser grid.</summary>
        internal int GridX = 16, GridY = 9;

        internal Framing(ShotEvaluator evaluator, float aspect, CanopyIndex? canopies = null)
        {
            Evaluator = evaluator;
            Player = ReplayPlayer.Instance;
            Aspect = aspect;
            ShotFile shot = evaluator.Shot;
            FramingSpec spec = shot.framing ?? new FramingSpec();
            // Without a framing block the featured actor is the player among the keepInFrame actors, else the first one.
            string? player = shot.keepInFrame.FirstOrDefault(actor => Player.File.Actors.TryGetValue(CommandText.Actor(actor), out ActorInfo info) ? info.IsPlayer
                : Player.Tracks.Get(actor).Prefab == "Player".GetStableHashCode());
            SubjectId = spec.subject ?? player ?? shot.keepInFrame.FirstOrDefault();
            PartnerId = spec.partner ?? (spec.subject == null ? shot.keepInFrame.FirstOrDefault(actor => actor != SubjectId) : null);
            if (SubjectId != null) SubjectTrack = Player.Tracks.Get(SubjectId);
            if (PartnerId != null) PartnerTrack = Player.Tracks.Get(PartnerId);
            (SizeRange, SizeSet) = ParseSize(spec.size);
            Place = ParsePlace(spec.place, Aspect);
            MinVisible = spec.minVisible ?? 0.6f;
            MaxForeground = spec.maxForeground ?? 0.15f;
            Moment = spec.moment;
            if (spec.action is { Length: 2 }) Action = (spec.action[0], spec.action[1]);
            Weights = new Dictionary<string, float>(DefaultWeights);
            if (spec.weights != null) foreach (KeyValuePair<string, float> weight in spec.weights) Weights[weight.Key] = weight.Value;
            if (SubjectTrack != null) Measured.Add((SubjectTrack, "subject"));
            if (PartnerTrack != null && PartnerTrack != SubjectTrack) Measured.Add((PartnerTrack, "partner"));
            foreach (string actor in shot.keepInFrame)
            {
                ActorTrack track = Player.Tracks.Get(actor);
                if (Measured.All(m => m.Track != track)) Measured.Add((track, "keep"));
            }
            _bodies = Player.Tracks.All.Where(t => t.Kind == 0 && !t.IsProjectile).ToList();
            Dictionary<int, (float Height, float Radius)> sizes = CombatData.Sizes(Player.File.Info);
            foreach (ActorTrack track in _bodies)
                _reach[(track.User, track.Id)] = Reach(track.Prefab);
            foreach (ActorTrack track in _bodies)
                _radii[(track.User, track.Id)] = Player.File.Actors.TryGetValue((track.User, track.Id), out ActorInfo info) && info.Radius > 0f ? info.Radius
                    : sizes.TryGetValue(track.Prefab, out (float Height, float Radius) size) ? size.Radius : 0.5f;
            _deaths = new HashSet<(long, uint)>(Player.File.Deaths.Select(death => (death.User, death.Id)));
            _canopies = canopies ?? CanopyIndex.Around(Player.Tracks.FightArea(), 40f);
        }

        internal static ((float, float) Range, bool Set) ParseSize(JToken? size)
        {
            if (size == null || size.Type == JTokenType.Null) return ((SpeckLimit, 6f), false);
            if (size.Type == JTokenType.String)
            {
                string name = ((string)size!).Replace('-', '_').Replace(' ', '_');
                if (name == "medium_close_up") name = "medium_close";
                if (name == "close_up") name = "close";
                if (Sizes.TryGetValue(name, out (float Min, float Max) range)) return (range, true);
                throw new ArgumentException($"framing.size '{size}' is not one of {string.Join(", ", Sizes.Keys)} or [min, max]");
            }
            if (size is JArray array && array.Count == 2 && (float)array[0] > 0f && (float)array[1] >= (float)array[0]) return (((float)array[0], (float)array[1]), true);
            throw new ArgumentException($"framing.size must be a size name or [min, max], not {size.ToString(Newtonsoft.Json.Formatting.None)}");
        }

        internal static Vector2? ParsePlace(JToken? place, float aspect)
        {
            if (place == null || place.Type == JTokenType.Null) return null;
            if (place.Type == JTokenType.String)
                return ((string)place!).ToLowerInvariant() switch
                {
                    "left" => new Vector2(1f / 3f, 0.5f),
                    "centre" or "center" => new Vector2(0.5f, 0.5f),
                    "right" => new Vector2(2f / 3f, 0.5f),
                    _ => throw new ArgumentException($"framing.place must be left, centre, right or [x, y], not '{place}'")
                };
            if (place is JArray array && array.Count == 2) return new Vector2((float)array[0], (float)array[1]);
            throw new ArgumentException($"framing.place must be left, centre, right or [x, y], not {place.ToString(Newtonsoft.Json.Formatting.None)}");
        }

        internal float Radius(ActorTrack track) => _radii.TryGetValue((track.User, track.Id), out float radius) ? radius : 0.5f;
        internal bool Died(ActorTrack track) => _deaths.Contains((track.User, track.Id));
        internal string Name(ActorTrack track) => Player.File.Info.PrefabName(track.Prefab);

        /// <summary>Collider radius and body reach per measured prefab, for the check report.</summary>
        internal Dictionary<string, object> Bodies() => _bodies.GroupBy(t => t.Prefab).ToDictionary(g => Name(g.First()),
            g => (object)new Dictionary<string, object> { ["height"] = R(Evaluator.Height(g.First())), ["radius"] = R(Radius(g.First())), ["blockRadius"] = R(BlockRadius(g.First())) });

        // ---------------------------------------------------------------- projection

        internal static Matrix4x4 Clip(Vector3 position, Quaternion rotation, float fov, float aspect)
        {
            Matrix4x4 projection = Matrix4x4.Perspective(fov, aspect, 0.1f, 5000f);
            Matrix4x4 view = Matrix4x4.TRS(position, rotation, Vector3.one).inverse;
            return projection * Matrix4x4.Scale(new Vector3(1, 1, -1)) * view;
        }

        internal static Vector2? Viewport(Matrix4x4 clip, Vector3 point)
        {
            Vector4 p = clip * new Vector4(point.x, point.y, point.z, 1f);
            if (p.w <= 0.0001f) return null;
            return new Vector2((p.x / p.w + 1f) * 0.5f, (p.y / p.w + 1f) * 0.5f);
        }

        /// <summary>The world direction through a frame point (0-1, y up).</summary>
        internal static Vector3 Ray(Quaternion rotation, float fov, float aspect, float x, float y)
        {
            float tan = Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad);
            return (rotation * new Vector3((2f * x - 1f) * tan * aspect, (2f * y - 1f) * tan, 1f)).normalized;
        }

        private static Rect Bounds(IEnumerable<Vector2> points)
        {
            float xMin = float.MaxValue, yMin = float.MaxValue, xMax = float.MinValue, yMax = float.MinValue;
            foreach (Vector2 p in points) { xMin = Mathf.Min(xMin, p.x); xMax = Mathf.Max(xMax, p.x); yMin = Mathf.Min(yMin, p.y); yMax = Mathf.Max(yMax, p.y); }
            return xMin > xMax ? Rect.zero : Rect.MinMaxRect(xMin, yMin, xMax, yMax);
        }

        private static float InsideFraction(Rect box)
        {
            float area = box.width * box.height;
            if (area <= 1e-6f) return box.xMin >= 0f && box.xMax <= 1f && box.yMin >= 0f && box.yMax <= 1f ? 1f : 0f;
            float w = Mathf.Max(0f, Mathf.Min(1f, box.xMax) - Mathf.Max(0f, box.xMin));
            float h = Mathf.Max(0f, Mathf.Min(1f, box.yMax) - Mathf.Max(0f, box.yMin));
            return Mathf.Clamp01(w * h / area);
        }

        /// <summary>Where an actor is, or false when it is not in the recording at the time.</summary>
        internal static bool Pose(ActorTrack track, float time, out Vector3 feet, out Quaternion rotation) => track.Sample(time, out feet, out rotation);

        /// <summary>The direction an actor faces, or moves when it moves faster than 1 m/s; flat.</summary>
        internal static Vector3 Heading(ActorTrack track, float time, Quaternion rotation, out bool moving)
        {
            moving = false;
            if (track.Sample(time - 0.1f, out Vector3 before, out _) && track.Sample(time + 0.1f, out Vector3 after, out _))
            {
                Vector3 velocity = after - before; velocity.y = 0f;
                if (velocity.magnitude / 0.2f > 1f) { moving = true; return velocity.normalized; }
            }
            Vector3 forward = rotation * Vector3.forward; forward.y = 0f;
            return forward.sqrMagnitude > 1e-6f ? forward.normalized : Vector3.forward;
        }

        // ---------------------------------------------------------------- one frame

        /// <summary>Measures one camera pose at a replay time. With rays false only the projected values are filled.</summary>
        internal FrameMeasure Measure(float time, Vector3 camera, Quaternion rotation, float fov, bool rays = true)
        {
            FrameMeasure frame = new() { Time = time, Camera = camera, Rotation = rotation, Fov = fov };
            Matrix4x4 clip = Clip(camera, rotation, fov, Aspect);
            Vector3 forward = rotation * Vector3.forward;
            Vector3 right = rotation * Vector3.right;
            List<(ActorTrack Track, Vector3 Feet, float Height, float Radius)> present = new();
            foreach (ActorTrack body in _bodies)
                if (body.Sample(time, out Vector3 feet, out _)) present.Add((body, feet, Evaluator.Height(body), BlockRadius(body)));
            // Only trees between the camera and the measured actors can block the view.
            Bounds reach = new(camera, Vector3.one * 6f);
            foreach ((ActorTrack track, string _) in Measured)
                if (track.Sample(time, out Vector3 feet, out _)) { reach.Encapsulate(feet + Vector3.up * Evaluator.Height(track)); reach.Encapsulate(feet); }
            reach.Expand(4f);
            _local = _canopies.Within(reach);
            foreach ((ActorTrack track, string role) in Measured)
            {
                if (!track.Sample(time, out Vector3 feet, out Quaternion actorRotation)) continue;
                float height = Evaluator.Height(track), radius = Radius(track);
                ActorFrame actor = new() { Id = track.Key, Role = role, Name = Name(track), BodyHeight = height };
                Vector3 toCamera = camera - (feet + Vector3.up * height * 0.5f);
                actor.Distance = toCamera.magnitude;
                // The capsule's end circles, eight points each, projected without clipping to the frame.
                List<Vector2> points = new(), head = new();
                bool behind = false;
                for (int index = 0; index < 8; index++)
                {
                    float angle = index * Mathf.PI / 4f;
                    Vector3 offset = new(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
                    foreach (float y in new[] { 0f, height })
                    {
                        Vector2? p = Viewport(clip, feet + offset + Vector3.up * y);
                        if (p == null) behind = true; else points.Add(p.Value);
                    }
                    float headRadius = Mathf.Min(radius, height * 0.09f);
                    Vector3 headOffset = new(Mathf.Cos(angle) * headRadius, 0f, Mathf.Sin(angle) * headRadius);
                    foreach (float y in new[] { height * 0.84f, height })
                        if (Viewport(clip, feet + headOffset + Vector3.up * y) is Vector2 h) head.Add(h);
                }
                actor.Box = Bounds(points);
                actor.HeadBox = Bounds(head);
                Vector2? top = Viewport(clip, feet + Vector3.up * height), bottom = Viewport(clip, feet);
                actor.FeetY = bottom?.y ?? actor.Box.yMin;
                actor.HeadY = top?.y ?? actor.Box.yMax;
                actor.Size = top != null && bottom != null ? Mathf.Abs(top.Value.y - bottom.Value.y) : actor.Box.height;
                // The centre of the part inside the frame, so a tight shot of the upper body is placed by what shows.
                Rect shown = Rect.MinMaxRect(Mathf.Max(0f, actor.Box.xMin), Mathf.Max(0f, actor.Box.yMin), Mathf.Min(1f, actor.Box.xMax), Mathf.Min(1f, actor.Box.yMax));
                actor.Center = shown.width > 0f && shown.height > 0f ? shown.center : actor.Box.center;
                actor.Headroom = 1f - actor.Box.yMax;
                // Points behind the camera cannot be projected; the part of the body in front counts in proportion.
                actor.InFrame = points.Count == 0 ? 0f : InsideFraction(actor.Box) * (behind ? points.Count / 16f : 1f);
                // A creature much taller than the subject is framed from the subject's level, so its head may leave the
                // frame: only the part of its body where the fight happens has to be in it.
                float subjectHeight = SubjectTrack != null && track != SubjectTrack ? Evaluator.Height(SubjectTrack) : 0f;
                if (subjectHeight > 0f && height > subjectHeight * 1.8f)
                {
                    actor.Large = true;
                    List<Vector2> contact = new();
                    bool contactBehind = false;
                    for (int index = 0; index < 8; index++)
                    {
                        float angle = index * Mathf.PI / 4f;
                        Vector3 offset = new(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
                        foreach (float y in new[] { 0f, subjectHeight * 1.3f })
                        {
                            Vector2? p = Viewport(clip, feet + offset + Vector3.up * y);
                            if (p == null) contactBehind = true; else contact.Add(p.Value);
                        }
                    }
                    actor.InFrame = contact.Count == 0 ? 0f : InsideFraction(Bounds(contact)) * (contactBehind ? contact.Count / 16f : 1f);
                }
                Vector3 heading = Heading(track, time, actorRotation, out actor.Moving);
                // Lead room is measured when the actor moves or faces across the frame, not toward or away from the camera.
                float across = Vector3.Dot(heading, right);
                Vector3 flatForward = new Vector3(forward.x, 0f, forward.z).normalized;
                if (Mathf.Abs(across) > 0.5f * Mathf.Max(0.0001f, new Vector2(Vector3.Dot(heading, right), Vector3.Dot(heading, flatForward)).magnitude))
                {
                    float ahead = across > 0f ? 1f - actor.Box.xMax : actor.Box.xMin;
                    float back = across > 0f ? actor.Box.xMin : 1f - actor.Box.xMax;
                    ahead = Mathf.Max(0f, ahead); back = Mathf.Max(0f, back);
                    actor.Lead = ahead + back > 0.0001f ? ahead / (ahead + back) : 0.5f;
                }
                actor.Visible = rays ? VisibleFraction(camera, track, feet, height, radius, present) : 1f;
                frame.Actors.Add(actor);
            }
            if (frame.Subject is ActorFrame first && frame.Partner is ActorFrame second && first.InFrame >= 0.5f && second.InFrame >= 0.5f)
                frame.PairSpread = Mathf.Max(first.Box.xMax, second.Box.xMax) - Mathf.Min(first.Box.xMin, second.Box.xMin);
            if (rays) Foreground(frame, rotation, fov, present);
            Score(frame);
            return frame;
        }

        /// <summary>
        /// Rays from the camera to a 3 × 5 grid on the camera-facing side of the capsule plus the head; a ray counts as
        /// blocked by a solid object, a tree canopy or another actor's capsule.
        /// </summary>
        private float VisibleFraction(Vector3 camera, ActorTrack self, Vector3 feet, float height, float radius,
            List<(ActorTrack Track, Vector3 Feet, float Height, float Radius)> present)
        {
            Vector3 toCamera = camera - feet; toCamera.y = 0f;
            Vector3 facing = toCamera.sqrMagnitude > 1e-4f ? toCamera.normalized : Vector3.forward;
            Vector3 side = Vector3.Cross(Vector3.up, facing);
            int seen = 0, total = 0;
            void Test(Vector3 point)
            {
                total++;
                if (!Blocked(camera, point, self, present)) seen++;
            }
            foreach (float u in new[] { -0.6f, 0f, 0.6f })
                foreach (float v in new[] { 0.1f, 0.3f, 0.5f, 0.7f, 0.85f })
                    Test(feet + Vector3.up * height * v + side * radius * u + facing * radius * Mathf.Sqrt(1f - u * u) * 0.9f);
            Test(feet + Vector3.up * height * 0.95f + facing * Mathf.Min(radius, height * 0.09f) * 0.9f);
            return total > 0 ? seen / (float)total : 1f;
        }

        private bool Blocked(Vector3 from, Vector3 to, ActorTrack self, List<(ActorTrack Track, Vector3 Feet, float Height, float Radius)> present)
        {
            Vector3 direction = to - from;
            float distance = direction.magnitude;
            if (distance < 0.05f) return false;
            direction /= distance;
            if (Physics.Raycast(from, direction, distance - 0.05f, SolidMask, QueryTriggerInteraction.Ignore)) return true;
            foreach ((ActorTrack track, Vector3 feet, float height, float radius) in present)
                if (track != self && RayCapsule(from, direction, feet, height, radius, out float hit) && hit < distance - 0.05f) return true;
            return _local.Blocks(from, direction, distance - 0.3f, out _);
        }

        /// <summary>Distance along a ray to a vertical capsule (a cylinder with round ends), if it hits.</summary>
        internal static bool RayCapsule(Vector3 origin, Vector3 direction, Vector3 feet, float height, float radius, out float distance)
        {
            distance = float.MaxValue;
            float bottom = feet.y + radius, top = feet.y + Mathf.Max(radius, height - radius);
            // The cylinder, solved in the ground plane.
            Vector2 o = new(origin.x - feet.x, origin.z - feet.z), d = new(direction.x, direction.z);
            float a = Vector2.Dot(d, d), b = 2f * Vector2.Dot(o, d), c = Vector2.Dot(o, o) - radius * radius;
            if (a > 1e-8f)
            {
                float disc = b * b - 4f * a * c;
                if (disc >= 0f)
                {
                    float t = (-b - Mathf.Sqrt(disc)) / (2f * a);
                    if (c <= 0f) t = 0f;
                    float y = origin.y + direction.y * t;
                    if (t >= 0f && y >= bottom && y <= top) { distance = t; return true; }
                }
            }
            bool any = false;
            foreach (float y in new[] { bottom, top })
            {
                Vector3 centre = new(feet.x, y, feet.z);
                Vector3 oc = origin - centre;
                float bb = Vector3.Dot(oc, direction), cc = oc.sqrMagnitude - radius * radius;
                float disc = bb * bb - cc;
                if (disc < 0f) continue;
                float t = -bb - Mathf.Sqrt(disc);
                if (cc <= 0f) t = 0f;
                if (t >= 0f && t < distance) { distance = t; any = true; }
            }
            return any;
        }

        /// <summary>
        /// A grid of screen rays: the fraction that hits anything (actors included) nearer than both 2 m and half the
        /// distance to the nearest measured actor, and whether any such hit falls in a measured actor's head box.
        /// </summary>
        private void Foreground(FrameMeasure frame, Quaternion rotation, float fov, List<(ActorTrack Track, Vector3 Feet, float Height, float Radius)> present)
        {
            float limit = ForegroundLimit(frame);
            int hits = 0, total = 0;
            bool Near(float x, float y)
            {
                Vector3 direction = Ray(rotation, fov, Aspect, x, y);
                if (Physics.Raycast(frame.Camera, direction, limit, SolidMask, QueryTriggerInteraction.Ignore)) return true;
                foreach ((ActorTrack _, Vector3 feet, float height, float radius) in present)
                    if (RayCapsule(frame.Camera, direction, feet, height, radius, out float hit) && hit < limit) return true;
                return _local.Blocks(frame.Camera, direction, limit, out _);
            }
            for (int gx = 0; gx < GridX; gx++)
                for (int gy = 0; gy < GridY; gy++)
                {
                    total++;
                    if (Near((gx + 0.5f) / GridX, (gy + 0.5f) / GridY)) hits++;
                }
            frame.Foreground = total > 0 ? hits / (float)total : 0f;
            // Actors that are not measured (another wolf of the pack, a bystander) crossing between the camera and the
            // subject, at any distance nearer than the subject.
            if (frame.Subject is ActorFrame subject)
                foreach ((ActorTrack track, Vector3 feet, float height, float radius) in present)
                {
                    if (Measured.Any(m => m.Track == track)) continue;
                    int covered = 0;
                    for (int gx = 0; gx < GridX; gx++)
                        for (int gy = 0; gy < GridY; gy++)
                            if (RayCapsule(frame.Camera, Ray(rotation, fov, Aspect, (gx + 0.5f) / GridX, (gy + 0.5f) / GridY), feet, height, radius, out float hit) && hit < subject.Distance)
                                covered++;
                    float share = total > 0 ? covered / (float)total : 0f;
                    if (share > frame.InFront) { frame.InFront = share; frame.InFrontName = Name(track); }
                }
            foreach (ActorFrame actor in frame.Actors)
            {
                if (actor.Role == "keep" || actor.HeadBox.width <= 0f) continue;
                Rect box = actor.HeadBox;
                for (int ix = 0; ix < 3 && !frame.HeadCovered; ix++)
                    for (int iy = 0; iy < 3 && !frame.HeadCovered; iy++)
                    {
                        float x = Mathf.Lerp(box.xMin, box.xMax, (ix + 0.5f) / 3f), y = Mathf.Lerp(box.yMin, box.yMax, (iy + 0.5f) / 3f);
                        if (x < 0f || x > 1f || y < 0f || y > 1f) continue;
                        if (Near(x, y)) frame.HeadCovered = true;
                    }
            }
        }

        /// <summary>
        /// Anything nearer than both 2 m and half the distance to the nearest framed subject is foreground. Framed means the
        /// subject, or another measured actor with at least 80% of its body in the frame: a partner right beside the
        /// camera is itself foreground, not the measure.
        /// </summary>
        internal static float ForegroundLimit(FrameMeasure frame)
        {
            IEnumerable<ActorFrame> framed = frame.Actors.Where(a => a.Role == "subject" || a.InFrame >= 0.8f);
            float nearest = framed.Any() ? framed.Min(a => a.Distance) : frame.Actors.Count > 0 ? frame.Actors.Max(a => a.Distance) : 10f;
            return Mathf.Min(2f, nearest * 0.5f);
        }

        // ---------------------------------------------------------------- score

        /// <summary>Size closeness: 1 inside the range, falling off with the size ratio outside it.</summary>
        internal static float SizeTerm(float size, (float Min, float Max) range)
        {
            if (size >= range.Min && size <= range.Max) return 1f;
            float ratio = size < range.Min ? range.Min / Mathf.Max(size, 1e-4f) : size / range.Max;
            float log = Mathf.Log(ratio);
            return Mathf.Exp(-log * log / (2f * 0.35f * 0.35f));
        }

        /// <summary>Liu et al. 2010 rule-of-thirds energy (σ 0.17) for a point.</summary>
        internal static float Thirds(Vector2 point)
        {
            float best = float.MaxValue;
            foreach (float x in new[] { 1f / 3f, 2f / 3f })
                foreach (float y in new[] { 1f / 3f, 2f / 3f })
                    best = Mathf.Min(best, (point - new Vector2(x, y)).magnitude);
            float line = Mathf.Min(Mathf.Min(Mathf.Abs(point.x - 1f / 3f), Mathf.Abs(point.x - 2f / 3f)), Mathf.Min(Mathf.Abs(point.y - 1f / 3f), Mathf.Abs(point.y - 2f / 3f)));
            float d = Mathf.Min(best, line * 1.5f);
            return Mathf.Exp(-d * d / (2f * 0.17f * 0.17f));
        }

        private void Score(FrameMeasure frame)
        {
            ActorFrame? subject = frame.Subject ?? frame.Actors.FirstOrDefault();
            ActorFrame? partner = frame.Partner;
            if (subject == null) { frame.Score = 0f; return; }
            float visibility = partner != null ? 0.7f * subject.Visible + 0.3f * partner.Visible : subject.Visible;
            float size = SizeTerm(subject.Size, SizeRange);
            float position;
            if (Place is Vector2 place) position = Mathf.Clamp01(1f - (subject.Center - place).magnitude / 0.3f);
            else
            {
                // No intended place: Cinemachine's hard limit, the centre must stay in the middle 80%.
                float edge = Mathf.Max(Mathf.Max(0.1f - subject.Center.x, subject.Center.x - 0.9f), Mathf.Max(0.1f - subject.Center.y, subject.Center.y - 0.9f));
                position = edge <= 0f ? 1f : Mathf.Clamp01(1f - edge / 0.2f);
            }
            float inFrame = partner != null ? Mathf.Min(subject.InFrame, partner.InFrame) : subject.InFrame;
            float head = subject.Box.yMax <= 1f ? 1f : 0.6f;
            float lead = subject.Lead is float l ? Mathf.Clamp01(l / 0.6f) : 1f;
            float margin = inFrame * head * (0.7f + 0.3f * lead) * (EdgeCut(subject) != null ? 0.85f : 1f) * (frame.HeadCovered ? 0.6f : 1f)
                * Mathf.Clamp01(1f - Mathf.Max(0f, frame.Foreground - MaxForeground) * 2f)
                * Mathf.Clamp01(1f - Mathf.Max(0f, (frame.PairSpread ?? 0f) - MaxPairSpread) * 2f)
                * Mathf.Clamp01(1f - Mathf.Max(0f, frame.InFront - MaxInFront) * 2f);
            // View angle: level to moderately high cameras, and beside the player when the partner is much bigger.
            Vector3 forward = frame.Rotation * Vector3.forward;
            float pitch = Mathf.Asin(Mathf.Clamp(-forward.y, -1f, 1f)) * Mathf.Rad2Deg;
            float angle = pitch < -20f ? Mathf.Clamp01(1f - (-20f - pitch) / 30f) : pitch > 35f ? Mathf.Clamp01(1f - (pitch - 35f) / 30f) : 1f;
            if (partner != null && partner.BodyHeight > subject.BodyHeight * 1.8f && partner.Distance < subject.Distance) angle *= 0.5f;
            if (frame.Fov > 60f && subject.Distance < 6f) angle *= 0.8f;
            float composition;
            if (Place is Vector2 placeAgain && Mathf.Abs(placeAgain.x - 0.5f) < 0.01f) composition = Mathf.Clamp01(1f - Mathf.Abs(subject.Center.x - 0.5f) / 0.25f);
            else
            {
                Vector2 eye = new(subject.Center.x, Mathf.Lerp(subject.FeetY, subject.HeadY, 0.93f));
                float thirds = Mathf.Max(Thirds(subject.Center), Thirds(eye));
                // Visual balance: the area-weighted centre of the measured actors near the frame centre.
                float weight = 0f; Vector2 sum = Vector2.zero;
                foreach (ActorFrame actor in frame.Actors)
                {
                    float area = Mathf.Clamp01(actor.Box.width) * Mathf.Clamp01(actor.Box.height);
                    sum += actor.Center * area; weight += area;
                }
                float balance = weight > 0f ? Mathf.Clamp01(1f - Mathf.Abs(sum.x / weight - 0.5f) * 2f) : 1f;
                composition = 0.5f * thirds + 0.5f * balance;
            }
            frame.Terms["visibility"] = visibility;
            frame.Terms["size"] = size;
            frame.Terms["position"] = position;
            frame.Terms["margin"] = margin;
            frame.Terms["angle"] = angle;
            frame.Terms["composition"] = composition;
            float total = 0f, weights = 0f;
            foreach (KeyValuePair<string, float> term in frame.Terms)
            {
                float w = Weights.TryGetValue(term.Key, out float value) ? value : 0f;
                total += w * term.Value; weights += w;
            }
            frame.Score = weights > 0f ? total / weights : 0f;
        }

        /// <summary>The joint a frame edge cuts within 3% of body height, if any (subject only).</summary>
        internal static string? EdgeCut(ActorFrame actor)
        {
            float span = actor.HeadY - actor.FeetY;
            if (Mathf.Abs(span) < 1e-4f) return null;
            foreach (float edge in new[] { 0f, 1f })
            {
                // Only an edge that crosses the body cuts it.
                if (edge <= Mathf.Min(actor.FeetY, actor.HeadY) || edge >= Mathf.Max(actor.FeetY, actor.HeadY)) continue;
                float along = (edge - actor.FeetY) / span;
                foreach ((string name, float height) in Joints)
                    if (Mathf.Abs(along - height) <= 0.03f) return name;
            }
            return null;
        }

        /// <summary>A frame's score again with other weights, from its stored terms.</summary>
        internal static float Rescore(FrameMeasure frame, Dictionary<string, float> weights, Dictionary<string, float>? replace = null)
        {
            float total = 0f, sum = 0f;
            foreach (KeyValuePair<string, float> term in frame.Terms)
            {
                float w = weights.TryGetValue(term.Key, out float value) ? value : 0f;
                float v = replace != null && replace.TryGetValue(term.Key, out float other) ? other : term.Value;
                total += w * v; sum += w;
            }
            return sum > 0f ? total / sum : 0f;
        }

        internal static float ShotScore(IReadOnlyList<float> scores)
        {
            if (scores.Count == 0) return 0f;
            List<float> sorted = scores.OrderBy(s => s).ToList();
            float p10 = sorted[Mathf.Clamp(Mathf.FloorToInt(0.1f * (sorted.Count - 1)), 0, sorted.Count - 1)];
            return 0.7f * scores.Average() + 0.3f * p10;
        }

        /// <summary>
        /// Whether the partner must be in frame at a time: always without a moment; with one, from 0.5 s before it, so
        /// an attacker may run into the frame before the strike.
        /// </summary>
        internal bool PartnerCounts(ActorFrame actor, float time) =>
            InAction(time) && (actor.Role != "partner" || Moment is not float moment || time >= moment - 0.5f);

        /// <summary>
        /// Night separation: the subject's brightness against what is directly behind it, averaged over 0.3 s, stays within
        /// this many stops for two samples in a row or more (from framing.md, Night Separation). On mountainFight1 the player
        /// fades into green fog over the last 0.2 s of the b05 shots; a longer minimum missed it.
        /// </summary>
        internal const float LowSeparationStops = 0.32f, LowSeparationSeconds = 0.1f;
        /// <summary>
        /// The share of the frame that things other than the subject and the terrain may cover from the camera to
        /// <see cref="ClutterDepth"/> metres past the subject (clutter beside the subject, at any distance from the camera).
        /// </summary>
        internal const float MaxClutter = 0.35f, ClutterDepth = 2f;

        /// <summary>Whether a time is inside the framing block's action range (always, without one).</summary>
        internal bool InAction(float time) => Action is not (float from, float to) || (time >= from - 0.001f && time <= to + 0.001f);

        // ---------------------------------------------------------------- serialise

        internal static double R(float value) => Math.Round(value, 3);

        internal static object Point(Vector2 value) => new[] { R(value.x), R(value.y) };

        internal static object Box(Rect box) => new[] { R(box.xMin), R(box.yMin), R(box.xMax), R(box.yMax) };

        internal static Dictionary<string, object?> Json(FrameMeasure frame, bool full)
        {
            Dictionary<string, object?> actors = new();
            foreach (ActorFrame actor in frame.Actors)
            {
                Dictionary<string, object?> entry = new()
                {
                    ["role"] = actor.Role, ["size"] = R(actor.Size), ["center"] = Point(actor.Center), ["inFrame"] = R(actor.InFrame)
                };
                if (full)
                {
                    entry["box"] = Box(actor.Box);
                    entry["headBox"] = Box(actor.HeadBox);
                    entry["headroom"] = R(actor.Headroom);
                    entry["lead"] = actor.Lead.HasValue ? R(actor.Lead.Value) : null;
                    entry["visible"] = R(actor.Visible);
                    entry["distance"] = R(actor.Distance);
                }
                actors[actor.Id] = entry;
            }
            Dictionary<string, object?> json = new() { ["t"] = Math.Round(frame.Time, 3), ["score"] = R(frame.Score), ["actors"] = actors };
            if (full)
            {
                Vector3 forward = frame.Rotation * Vector3.forward;
                json["camera"] = new[] { Math.Round(frame.Camera.x, 3), Math.Round(frame.Camera.y, 3), Math.Round(frame.Camera.z, 3) };
                json["forward"] = new[] { R(forward.x), R(forward.y), R(forward.z) };
                json["fov"] = Math.Round(frame.Fov, 2);
                json["foreground"] = R(frame.Foreground);
                json["headCovered"] = frame.HeadCovered;
                json["pairSpread"] = frame.PairSpread.HasValue ? R(frame.PairSpread.Value) : null;
                json["inFront"] = R(frame.InFront);
                if (frame.InFront > 0f) json["inFrontActor"] = frame.InFrontName;
                json["terms"] = frame.Terms.ToDictionary(t => t.Key, t => (object)R(t.Value));
            }
            return json;
        }
    }

    /// <summary>
    /// Tree canopies near the fight. Branches have no colliders; a tree's drawn shape shrunk to its middle, above its
    /// trunk, stands in for them.
    /// </summary>
    internal sealed class CanopyIndex
    {
        private static readonly string[] TreeNames = { "tree", "beech", "birch", "oak", "yggashoot", "pine", "fir", "shrub", "bush" };
        private readonly List<(string Name, Bounds Bounds)> _canopies = new();
        internal int Count => _canopies.Count;

        internal static CanopyIndex Around(Bounds area, float margin)
        {
            CanopyIndex index = new();
            Vector3 extents = area.extents + new Vector3(margin, margin + 60f, margin);
            HashSet<GameObject> seen = new();
            foreach (Collider collider in Physics.OverlapBox(area.center, extents, Quaternion.identity, LayerMask.GetMask("static_solid", "Default", "Default_small", "piece"), QueryTriggerInteraction.Ignore))
            {
                if (collider == null) continue;
                GameObject root = collider.GetComponentInParent<ZNetView>()?.gameObject ?? collider.transform.root.gameObject;
                if (!seen.Add(root)) continue;
                string name = Utils.GetPrefabName(root);
                if (!TreeNames.Any(name.ToLowerInvariant().Contains)) continue;
                Renderer[] renderers = root.GetComponentsInChildren<Renderer>().Where(r => r.enabled && r is not ParticleSystemRenderer).ToArray();
                if (renderers.Length == 0) continue;
                Bounds canopy = renderers[0].bounds;
                foreach (Renderer renderer in renderers) canopy.Encapsulate(renderer.bounds);
                // The middle of the drawn shape, starting a fifth of the way up: branches, not the trunk base or the tips.
                float bottom = canopy.min.y + canopy.size.y * 0.2f;
                canopy = new Bounds(new Vector3(canopy.center.x, (bottom + canopy.max.y) / 2f, canopy.center.z),
                    new Vector3(canopy.size.x * 0.55f, canopy.max.y - bottom, canopy.size.z * 0.55f));
                index._canopies.Add((name, canopy));
            }
            return index;
        }

        /// <summary>The canopies that overlap a box.</summary>
        internal CanopyIndex Within(Bounds area)
        {
            CanopyIndex index = new();
            foreach ((string name, Bounds bounds) in _canopies) if (bounds.Intersects(area)) index._canopies.Add((name, bounds));
            return index;
        }

        /// <summary>True when the ray passes through a canopy within the distance, or starts inside one.</summary>
        internal bool Blocks(Vector3 origin, Vector3 direction, float distance, out string name)
        {
            Ray ray = new(origin, direction);
            foreach ((string tree, Bounds bounds) in _canopies)
            {
                if (bounds.Contains(origin)) { name = tree; return true; }
                if (bounds.IntersectRay(ray, out float along) && along < distance) { name = tree; return true; }
            }
            name = "";
            return false;
        }

        /// <summary>The canopy the camera is inside, or the one the view to the target passes through.</summary>
        internal string? Foliage(Vector3 camera, Vector3 look, out bool inside)
        {
            inside = false;
            Vector3 direction = look - camera;
            float distance = direction.magnitude;
            if (distance < 0.5f) return null;
            Ray ray = new(camera, direction / distance);
            foreach ((string tree, Bounds bounds) in _canopies)
            {
                if (bounds.Contains(camera)) { inside = true; return tree; }
                if (bounds.IntersectRay(ray, out float along) && along < distance - 0.5f && !bounds.Contains(look)) return tree;
            }
            return null;
        }
    }
}
