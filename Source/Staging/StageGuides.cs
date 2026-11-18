using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using cameraTools.Markers;
using cameraTools.Replay;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace cameraTools.Staging
{
    internal enum GuideKind { Mark, Ring, Path }

    internal enum GuideState { Active, Next, Off }

    /// <summary>
    /// A guide for the player during a staged take: a mark to stand on, a ring at a distance around an actor (where
    /// to stop), or a path to walk. Guides are drawn only on the live game screen and are never recorded: they are
    /// immediate-mode lines, not world objects, and nothing is drawn while a replay plays or a shot renders.
    /// </summary>
    internal sealed class StageGuide
    {
        public string Id = "";
        public GuideKind Kind;
        public GuideState State = GuideState.Active;
        public string Label = "";
        /// <summary>Mark: where it is. Ring: its centre.</summary>
        public StageTarget? At;
        /// <summary>Mark: the facing arrow. Ring: the middle of the arc.</summary>
        public StageTarget? Toward;
        public float? Facing;
        public float Radius = 0.8f;
        public float Arc = 70f;
        public List<Vector3> Points = new();
        public string Path = "";
        /// <summary>enter, leave or stop (inside and still); empty for none.</summary>
        public string Trigger = "";
        /// <summary>The label the trigger records, for actions and recordings that wait with 'after'.</summary>
        public string On = "";

        public bool Armed = true;
        public bool WasInside;
        public float StillFor;
        public float FiredAt = -100f;
        public Vector3 LastPlayer;

        public static readonly string[] Triggers = { "", "enter", "leave", "stop" };

        public static StageGuide Parse(JObject json)
        {
            StageGuide guide = new()
            {
                Id = StageActorSpec.Text(json, "id") ?? throw new ArgumentException("Every guide needs an id"),
                Label = (StageActorSpec.Text(json, "label") ?? "").Replace('_', ' '),
                At = StageTarget.Parse(json["at"] ?? json["around"]),
                Toward = StageTarget.Parse(json["toward"]),
                Facing = json.Value<float?>("facing"),
                Arc = json.Value<float?>("arc") ?? 70f,
                Path = StageActorSpec.Text(json, "path") ?? "",
                Trigger = (StageActorSpec.Text(json, "trigger") ?? "").ToLowerInvariant(),
                On = StageActorSpec.Text(json, "on") ?? ""
            };
            string kind = (StageActorSpec.Text(json, "kind") ?? (json["around"] != null ? "ring" : json["points"] != null || json["path"] != null ? "path" : "mark")).ToLowerInvariant();
            guide.Kind = kind switch
            {
                "mark" => GuideKind.Mark,
                "ring" => GuideKind.Ring,
                "path" => GuideKind.Path,
                _ => throw new ArgumentException($"Guide {guide.Id}: kind is mark, ring or path, not '{kind}'")
            };
            guide.State = ParseState(StageActorSpec.Text(json, "state") ?? "active");
            guide.Radius = json.Value<float?>("radius") ?? (guide.Kind == GuideKind.Ring ? 10f : 0.8f);
            if (json["points"] is JArray points)
            {
                foreach (JToken point in points)
                {
                    StageTarget target = StageTarget.Parse(point) ?? throw new ArgumentException($"Guide {guide.Id}: a point is empty");
                    guide.Points.Add(target.Point ?? throw new ArgumentException($"Guide {guide.Id}: points are [x, z] or [x, y, z]"));
                }
            }
            if (guide.Kind != GuideKind.Path && guide.At == null)
                throw new ArgumentException($"Guide {guide.Id}: a {kind} needs {(guide.Kind == GuideKind.Ring ? "'around'" : "'at'")}");
            if (guide.Kind == GuideKind.Path && guide.Points.Count < 2 && guide.Path.Length == 0)
                throw new ArgumentException($"Guide {guide.Id}: a path needs two or more 'points' or a path marker in 'path'");
            if (guide.Radius <= 0f || guide.Radius > 60f) throw new ArgumentException($"Guide {guide.Id}: radius is 0 to 60 m");
            if (guide.Arc <= 0f || guide.Arc > 360f) throw new ArgumentException($"Guide {guide.Id}: arc is 0 to 360 degrees");
            if (!Triggers.Contains(guide.Trigger)) throw new ArgumentException($"Guide {guide.Id}: trigger is enter, leave or stop, not '{guide.Trigger}'");
            if (guide.Kind == GuideKind.Path && guide.Trigger.Length > 0) throw new ArgumentException($"Guide {guide.Id}: a path has no trigger; put a mark at its end");
            if (guide.Trigger.Length > 0 && guide.On.Length == 0) guide.On = guide.Id;
            return guide;
        }

        public static GuideState ParseState(string text) => text.ToLowerInvariant() switch
        {
            "active" or "on" or "current" => GuideState.Active,
            "next" or "dim" => GuideState.Next,
            "off" or "hide" => GuideState.Off,
            _ => throw new ArgumentException($"A guide's state is active, next or off, not '{text}'")
        };

        /// <summary>The centre on the floor: a mark's spot or a ring's centre (an actor's feet when it is one).</summary>
        public Vector3 Centre() => At!.Ground();

        /// <summary>Where the arrow or the arc's middle points, flat; null when neither 'toward' nor 'facing' is set.</summary>
        public Vector3? Direction(Vector3 centre)
        {
            Vector3 direction;
            if (Toward != null) direction = Toward.Ground() - centre;
            else if (Facing is float facing) direction = StagedActor.Compass(facing);
            else return null;
            direction.y = 0f;
            return direction.sqrMagnitude > 0.0001f ? direction.normalized : null;
        }

        public List<Vector3> PathPoints()
        {
            if (Points.Count >= 2) return Points.Select(p => StageTarget.SnapToGround(p)).ToList();
            Marker marker = MarkerStore.Find(Path) ?? throw new ArgumentException($"No path marker '{Path}'");
            return marker.waypoints.Select(w => w.position).ToList();
        }

        public string Describe()
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            string where = Kind switch
            {
                GuideKind.Mark => $"at {At} radius={Radius.ToString("F1", ci)}",
                GuideKind.Ring => $"around {At} radius={Radius.ToString("F1", ci)} arc={Arc.ToString("F0", ci)}",
                _ => Points.Count > 0 ? $"{Points.Count} points" : $"path {Path}"
            };
            string trigger = Trigger.Length > 0 ? $" trigger={Trigger} on={On}{(Armed ? "" : " (fired)")}" : "";
            return $"guide {Id} {Kind.ToString().ToLowerInvariant()} {where} state={State.ToString().ToLowerInvariant()}{trigger}{(Label.Length > 0 ? $" label=\"{Label}\"" : "")}";
        }
    }

    /// <summary>Keeps the guides, checks their triggers against the player, and draws them on the live game screen.</summary>
    internal sealed class StageGuides : MonoBehaviour
    {
        private const float StopSpeed = 0.25f;
        private const float StopSeconds = 0.3f;
        private static readonly Color ActiveColor = new(1f, 0.55f, 0.1f, 1f);
        private static readonly Color NextColor = new(0.85f, 0.85f, 0.85f, 0.5f);
        private static readonly Color FiredColor = new(0.35f, 1f, 0.4f, 1f);

        public static StageGuides? Instance { get; private set; }

        private readonly Dictionary<string, StageGuide> _guides = new(StringComparer.OrdinalIgnoreCase);
        private static Material? _material;
        private GUIStyle? _labelStyle;
        private GUIStyle? _statusStyle;
        private float _recordingSince = -1f;

        public IEnumerable<StageGuide> All => _guides.Values;

        private void Awake() => Instance = this;
        private void OnEnable() => Camera.onPostRender += Draw;
        private void OnDisable() => Camera.onPostRender -= Draw;

        /// <summary>Guides show only on the live screen: never in a replay, a replay session or a shot render.</summary>
        private static bool Live => Player.m_localPlayer != null && GameCamera.instance != null && !ReplayHooks.Replaying;

        public StageGuide Set(StageGuide guide)
        {
            _guides[guide.Id] = guide;
            return guide;
        }

        public StageGuide Get(string id) =>
            _guides.TryGetValue(id, out StageGuide guide) ? guide : throw new ArgumentException($"No guide '{id}' (ct_stage guide list shows them)");

        public void SetState(string id, GuideState state)
        {
            StageGuide guide = Get(id);
            guide.State = state;
            if (state != GuideState.Off) Rearm(guide);
        }

        public static void Rearm(StageGuide guide)
        {
            guide.Armed = true;
            guide.WasInside = false;
            guide.StillFor = 0f;
            guide.FiredAt = -100f;
        }

        public bool Remove(string id) => _guides.Remove(id);

        public int Clear()
        {
            int count = _guides.Count;
            _guides.Clear();
            return count;
        }

        // ---- triggers ----

        private void Update()
        {
            if (ReplayRecorder.IsRecording && _recordingSince < 0f) _recordingSince = Time.time;
            else if (!ReplayRecorder.IsRecording) _recordingSince = -1f;
            Player? player = Player.m_localPlayer;
            if (player == null || ReplayHooks.Replaying || _guides.Count == 0) return;
            Vector3 position = player.transform.position;
            foreach (StageGuide guide in _guides.Values.ToList())
            {
                // Only the lit (active) guide fires: a dim "next" guide shows what comes after but must not trigger early.
                if (guide.State != GuideState.Active || !guide.Armed || guide.Trigger.Length == 0) continue;
                Vector3 centre;
                try { centre = guide.Centre(); }
                catch (ArgumentException) { continue; } // an actor it follows is gone
                bool inside = Flat(position - centre).magnitude <= guide.Radius;
                float speed = Time.deltaTime > 0f ? Flat(position - guide.LastPlayer).magnitude / Time.deltaTime : 0f;
                guide.LastPlayer = position;
                guide.StillFor = inside && speed < StopSpeed ? guide.StillFor + Time.deltaTime : 0f;
                bool fire = guide.Trigger switch
                {
                    "enter" => inside,
                    "leave" => guide.WasInside && Flat(position - centre).magnitude > guide.Radius + 0.3f,
                    "stop" => guide.StillFor >= StopSeconds,
                    _ => false
                };
                if (inside) guide.WasInside = true;
                if (!fire) continue;
                guide.Armed = false;
                guide.FiredAt = Time.time;
                StageRunner.Instance?.Fire(guide.On, $"guide {guide.Id} {guide.Trigger} (player {Flat(position - centre).magnitude.ToString("F1", CultureInfo.InvariantCulture)} m from its centre)");
            }
        }

        private static Vector3 Flat(Vector3 v) => new(v.x, 0f, v.z);

        // ---- drawing ----

        private static Material Material
        {
            get
            {
                if (_material != null) return _material;
                _material = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave };
                _material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                _material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                _material.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                _material.SetInt("_ZWrite", 0);
                return _material;
            }
        }

        private void Draw(Camera cam)
        {
            if (_guides.Count == 0 || !Live || cam.gameObject != GameCamera.instance.gameObject) return;
            Material.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.LessEqual);
            Material.SetPass(0);
            GL.PushMatrix();
            GL.MultMatrix(Matrix4x4.identity);
            GL.Begin(GL.LINES);
            DrawAll(1f);
            GL.End();
            // Faint through walls and bodies, so a mark behind the boss still reads.
            Material.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Greater);
            Material.SetPass(0);
            GL.Begin(GL.LINES);
            DrawAll(0.25f);
            GL.End();
            GL.PopMatrix();
        }

        private void DrawAll(float alpha)
        {
            float pulse = 0.75f + 0.25f * Mathf.Sin(Time.time * 5f);
            foreach (StageGuide guide in _guides.Values)
            {
                if (guide.State == GuideState.Off) continue;
                Color color = Time.time - guide.FiredAt < 1.5f ? FiredColor : guide.State == GuideState.Active ? ActiveColor : NextColor;
                color.a *= alpha * (guide.State == GuideState.Active ? pulse : 1f);
                try
                {
                    switch (guide.Kind)
                    {
                        case GuideKind.Mark: DrawMark(guide, color); break;
                        case GuideKind.Ring: DrawRing(guide, color); break;
                        case GuideKind.Path: DrawPath(guide.PathPoints(), color); break;
                    }
                }
                catch (ArgumentException) { } // an actor or marker it uses is gone; draw the rest
            }
        }

        private static void DrawMark(StageGuide guide, Color color)
        {
            Vector3 centre = guide.Centre();
            for (float offset = -0.06f; offset <= 0.061f; offset += 0.06f) Circle(centre, guide.Radius + offset, 0f, 360f, color);
            MarkerRenderer.DrawCross(centre + Vector3.up * 0.06f, color, guide.Radius * 0.5f);
            MarkerRenderer.DrawPost(centre, color, 2.2f);
            if (guide.Direction(centre) is Vector3 direction)
                Arrow(centre + direction * guide.Radius, centre + direction * (guide.Radius + 1.2f), color);
        }

        private static void DrawRing(StageGuide guide, Color color)
        {
            Vector3 centre = guide.Centre();
            Vector3 middle = guide.Direction(centre) ?? Vector3.forward;
            float half = guide.Arc >= 360f ? 180f : guide.Arc * 0.5f;
            float yaw = Mathf.Atan2(middle.x, middle.z) * Mathf.Rad2Deg;
            for (float offset = -0.08f; offset <= 0.081f; offset += 0.08f) Circle(centre, guide.Radius + offset, yaw - half, yaw + half, color);
            // Ticks pointing in toward the centre: "stop at this line".
            for (float angle = yaw - half; angle <= yaw + half + 0.01f; angle += Mathf.Max(4f, guide.Arc / 12f))
            {
                Vector3 outward = StagedActor.Compass(angle);
                Vector3 a = OnFloor(centre + outward * guide.Radius, centre.y);
                Vector3 b = OnFloor(centre + outward * (guide.Radius - 0.6f), centre.y);
                MarkerRenderer.Line(a, b, color);
            }
        }

        private static void DrawPath(List<Vector3> points, Color color)
        {
            for (int i = 1; i < points.Count; i++)
            {
                Vector3 a = points[i - 1] + Vector3.up * 0.08f;
                Vector3 b = points[i] + Vector3.up * 0.08f;
                MarkerRenderer.Line(a, b, color);
                float length = Vector3.Distance(a, b);
                for (float d = 1.5f; d < length; d += 2.5f) Chevron(Vector3.Lerp(a, b, d / length), (b - a).normalized, color);
            }
            Arrow(points[points.Count - 2] + Vector3.up * 0.08f, points[points.Count - 1] + Vector3.up * 0.08f, color);
        }

        private static void Circle(Vector3 centre, float radius, float fromDeg, float toDeg, Color color)
        {
            int segments = Mathf.Max(8, Mathf.CeilToInt((toDeg - fromDeg) / 4f));
            Vector3 previous = OnFloor(centre + StagedActor.Compass(fromDeg) * radius, centre.y);
            for (int i = 1; i <= segments; i++)
            {
                Vector3 next = OnFloor(centre + StagedActor.Compass(Mathf.Lerp(fromDeg, toDeg, i / (float)segments)) * radius, centre.y);
                MarkerRenderer.Line(previous, next, color);
                previous = next;
            }
        }

        /// <summary>A point dropped onto the floor near <paramref name="nearY"/>, raised a little so it is not hidden in it.</summary>
        private static Vector3 OnFloor(Vector3 point, float nearY) =>
            new(point.x, (StageFloor.Below(point.x, point.z, nearY) ?? nearY) + 0.06f, point.z);

        private static void Arrow(Vector3 from, Vector3 to, Color color)
        {
            MarkerRenderer.Line(from, to, color);
            Chevron(to, (to - from).normalized, color);
        }

        private static void Chevron(Vector3 tip, Vector3 forward, Color color)
        {
            Vector3 flat = Flat(forward).normalized;
            if (flat.sqrMagnitude < 0.0001f) return;
            Vector3 side = Vector3.Cross(Vector3.up, flat) * 0.35f;
            MarkerRenderer.Line(tip, tip - flat * 0.45f + side, color);
            MarkerRenderer.Line(tip, tip - flat * 0.45f - side, color);
        }

        // ---- labels and status ----

        private void OnGUI()
        {
            if (!Live) return;
            Camera cam = GameCamera.instance.GetComponent<Camera>();
            if (cam == null) return;
            _labelStyle ??= new GUIStyle(GUI.skin.box) { alignment = TextAnchor.MiddleCenter, fontSize = 15, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
            _statusStyle ??= new GUIStyle(GUI.skin.box) { alignment = TextAnchor.MiddleCenter, fontSize = 18, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };

            foreach (StageGuide guide in _guides.Values)
            {
                if (guide.State == GuideState.Off || guide.Label.Length == 0) continue;
                Vector3 anchor;
                try { anchor = LabelAnchor(guide); }
                catch (ArgumentException) { continue; }
                Vector3 screen = cam.WorldToScreenPoint(anchor);
                if (screen.z <= 0f) continue;
                GUIContent content = new(guide.Label);
                Vector2 size = _labelStyle.CalcSize(content) + new Vector2(12f, 6f);
                Color previous = GUI.color;
                GUI.color = guide.State == GuideState.Active ? ActiveColor : NextColor;
                GUI.Label(new Rect(screen.x - size.x / 2f, Screen.height - screen.y - size.y, size.x, size.y), content, _labelStyle);
                GUI.color = previous;
            }

            // Only while guides are up or a scene runs: plain recordings and OBS footage get no status text.
            if (_guides.Count == 0 && !(StageRunner.Instance?.Running ?? false)) return;
            string status = Status();
            if (status.Length == 0) return;
            GUIContent statusContent = new(status);
            Vector2 statusSize = _statusStyle.CalcSize(statusContent) + new Vector2(20f, 8f);
            Color before = GUI.color;
            GUI.color = ReplayRecorder.IsRecording ? new Color(1f, 0.3f, 0.3f, 1f) : ActiveColor;
            GUI.Label(new Rect((Screen.width - statusSize.x) / 2f, 40f, statusSize.x, statusSize.y), statusContent, _statusStyle);
            GUI.color = before;
        }

        private static Vector3 LabelAnchor(StageGuide guide)
        {
            Vector3 centre = guide.Kind == GuideKind.Path ? guide.PathPoints()[0] : guide.Centre();
            if (guide.Kind != GuideKind.Ring) return centre + Vector3.up * 2.6f;
            Vector3 middle = guide.Direction(centre) ?? Vector3.forward;
            return OnFloor(centre + middle * guide.Radius, centre.y) + Vector3.up * 1.2f;
        }

        private string Status()
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            if (ReplayRecorder.IsRecording && ReplayRecorder.Instance != null)
                return $"REC {ReplayRecorder.Instance.RecordingName} {(Time.time - _recordingSince).ToString("F0", ci)} s";
            StageRunner? runner = StageRunner.Instance;
            if (runner != null && runner.WaitingToRecord is string waiting) return "READY: " + waiting;
            return "";
        }
    }
}
