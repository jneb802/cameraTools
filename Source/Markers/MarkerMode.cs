using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;

namespace cameraTools.Markers
{
    /// <summary>Receives the label typed into Valheim's own text dialog.</summary>
    public class MarkerLabelReceiver : TextReceiver
    {
        private readonly Marker _marker;

        public MarkerLabelReceiver(Marker marker)
        {
            _marker = marker;
        }

        public string GetText() => _marker.label;

        public void SetText(string text)
        {
            _marker.label = string.IsNullOrWhiteSpace(text) ? _marker.label : text.Trim();
            MarkerStore.Save();
        }
    }

    public class WaypointHoldReceiver : TextReceiver
    {
        private readonly Marker _marker;
        private readonly int _index;

        public WaypointHoldReceiver(Marker marker, int index)
        {
            _marker = marker;
            _index = index;
        }

        public string GetText() => _marker.waypoints[_index].hold.ToString("F1", CultureInfo.InvariantCulture);

        public void SetText(string text)
        {
            if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float hold))
            {
                _marker.waypoints[_index].hold = Mathf.Clamp(hold, 0f, 30f);
                MarkerStore.Save();
            }
        }
    }

    /// <summary>Tracks when the marker tools moved the free camera, so the user always has a way back to the character.</summary>
    public static class MarkerCamera
    {
        private static string _what = "";

        public static bool Active => !string.IsNullOrEmpty(_what) && GameCamera.InFreeFly();

        public static string What => _what;

        public static void Sent(string what)
        {
            _what = what;
            MarkerMode.Say($"Camera {what}. {MarkerMode.ReturnKey.Value.MainKey} returns it to your character.");
        }

        public static void Return()
        {
            PathPlayer.Stop();
            CreativeZoneCameraController.StopMove();
            CreativeZoneCameraController.StopOrbit();
            if (GameCamera.InFreeFly())
            {
                CreativeZoneCameraController.SetFreeFly(false);
            }

            _what = "";
            MarkerVisibility.Mode = MarkerVisibilityMode.Auto;
            MarkerMode.Say("Camera returned to your character");
        }

        public static void Tick()
        {
            if (!string.IsNullOrEmpty(_what) && !GameCamera.InFreeFly())
            {
                _what = "";
            }
        }
    }

    /// <summary>
    /// Marker placement state. The tool itself is the cloned Hammer in <see cref="MarkerTool"/>; this class
    /// turns its build-mode clicks into markers, keeps the polygon, circle and path drafts, and handles
    /// hover, rename and removal. Nothing here runs unless the marker tool is in the player's hand.
    /// </summary>
    public class MarkerMode : MonoBehaviour
    {
        private static MarkerMode? _instance;
        public static readonly string[] AreaTags = { "room", "exterior", "courtyard", "detail", "hero", "entrance", "other" };

        public static ConfigEntry<KeyboardShortcut> FinishKey = null!;
        public static ConfigEntry<KeyboardShortcut> RecordPathKey = null!;
        public static ConfigEntry<KeyboardShortcut> EditKey = null!;
        public static ConfigEntry<KeyboardShortcut> DeleteKey = null!;
        public static ConfigEntry<KeyboardShortcut> ReturnKey = null!;
        public static ConfigEntry<float> RecordInterval = null!;
        public static ConfigEntry<float> EyeHeight = null!;

        /// <summary>True while the marker tool is equipped.</summary>
        public static bool Active { get; private set; }

        /// <summary>True while the marker tool is equipped and the camera is on the character, i.e. marks can be placed.</summary>
        public static bool Placing => Active && !GameCamera.InFreeFly();

        public static Marker? Hovered => _instance?._hovered;
        public static int HoveredWaypoint => _instance?._hoveredWaypoint ?? -1;
        public static Marker? Highlighted;

        public static MarkerKind Tool => _instance?._tool ?? MarkerKind.Point;
        public static AreaShape Shape => _instance?._shape ?? AreaShape.Circle;

        /// <summary>True while the wheel sizes a round or square area, so the camera must not zoom.</summary>
        public static bool ConsumeScroll =>
            _instance != null && Placing && _instance._tool == MarkerKind.Area && _instance._shape != AreaShape.Polygon && _instance._hasPreviewPoint && !UiBlocked();

        /// <summary>The round or square area being placed, for the ruler that draws it at the ghost.</summary>
        public static bool PreviewArea(out AreaShape shape, out Vector3 center, out float radius, out float width, out float depth, out float rotation)
        {
            MarkerMode? m = _instance;
            if (m == null || !Placing || m._tool != MarkerKind.Area || m._shape == AreaShape.Polygon || !m._hasPreviewPoint || UiBlocked())
            {
                shape = AreaShape.Circle;
                center = Vector3.zero;
                radius = width = depth = rotation = 0f;
                return false;
            }

            shape = m._shape;
            center = m._previewPoint;
            radius = m._radius;
            width = m._width;
            depth = m._depth;
            rotation = m._rotation;
            return true;
        }

        /// <summary>Live size text for the build HUD, in Infinity Hammer's style.</summary>
        public static string SizeReadout()
        {
            MarkerMode? m = _instance;
            if (m == null)
            {
                return "";
            }

            CultureInfo ci = CultureInfo.InvariantCulture;
            return m._shape == AreaShape.Circle
                ? $"r: {m._radius.ToString("F1", ci)}"
                : $"w: {m._width.ToString("F1", ci)}, d: {m._depth.ToString("F1", ci)}, a: {m._rotation.ToString("F0", ci)}";
        }

        private MarkerKind _tool = MarkerKind.Point;
        private AreaShape _shape = AreaShape.Circle;
        private bool _hasPreviewPoint;
        private Vector3 _previewPoint;

        private readonly List<Waypoint> _draft = new();
        private readonly List<Vector3> _polygonDraft = new();
        private float _radius = 6f;
        private float _width = 10f;
        private float _depth = 10f;
        private float _rotation;
        private bool _recording;
        private float _lastSampleTime;

        private Marker? _hovered;
        private int _hoveredWaypoint = -1;

        public static void BindConfig(ConfigFile config)
        {
            FinishKey = config.Bind("Markers", "FinishKey", new KeyboardShortcut(KeyCode.F), "Finish the path or area being drawn (clicking the first corner, or clicking twice without moving on a path, does the same)");
            RecordPathKey = config.Bind("Markers", "RecordPathKey", new KeyboardShortcut(KeyCode.G), "Start or stop live path recording while the Path mark is selected");
            EditKey = config.Bind("Markers", "EditKey", new KeyboardShortcut(KeyCode.E), "Rename the marker under the crosshair (on a path waypoint: set its hold time)");
            DeleteKey = config.Bind("Markers", "DeleteKey", new KeyboardShortcut(KeyCode.Delete), "Remove the last draft point, or the marker under the crosshair (middle mouse does the same)");
            ReturnKey = config.Bind("Markers", "ReturnKey", new KeyboardShortcut(KeyCode.F8), "Leave the free camera and return to your character (after goto, preview or path playback)");
            RecordInterval = config.Bind("Markers", "RecordInterval", 0.5f, "Seconds between samples when live-recording a path");
            EyeHeight = config.Bind("Markers", "EyeHeight", 1.7f, "Camera height above the feet when a path waypoint is placed while walking");
        }

        private void Awake()
        {
            _instance = this;
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }
        }

        public static bool UiBlocked()
        {
            return Console.IsVisible()
                   || (Chat.instance != null && Chat.instance.HasFocus())
                   || TextInput.IsVisible()
                   || Menu.IsVisible()
                   || InventoryGui.IsVisible()
                   || Minimap.IsOpen()
                   || Hud.IsPieceSelectionVisible();
        }

        private void Update()
        {
            Player? player = Player.m_localPlayer;
            if (player == null || GameCamera.instance == null)
            {
                return;
            }

            MarkerCamera.Tick();
            if (ReturnKey.Value.IsDown() && !UiBlocked() && GameCamera.InFreeFly())
            {
                MarkerCamera.Return();
                return;
            }

            bool equipped = MarkerTool.IsEquipped(player);
            if (equipped != Active)
            {
                Active = equipped;
                if (equipped)
                {
                    MarkerStore.EnsureLoaded();
                }
                else
                {
                    ClearDrafts();
                    _hovered = null;
                    _hoveredWaypoint = -1;
                }
            }

            if (!Active)
            {
                return;
            }

            MarkerStore.EnsureLoaded();
            SyncToolFromSelection(player);

            bool freeFly = GameCamera.InFreeFly();
            if (freeFly)
            {
                HideGhost(player);
            }

            if (UiBlocked())
            {
                return;
            }

            UpdatePreviewPoint(player, freeFly);
            UpdateHover();
            UpdateAreaSize();

            if (_recording && Time.time - _lastSampleTime >= RecordInterval.Value)
            {
                SampleWaypoint(false);
            }

            // The game ignores player input in the free camera, so the Shot mark is placed here.
            if (freeFly && _tool == MarkerKind.Shot && ZInput.GetButtonDown("Attack"))
            {
                PlaceShot();
            }

            if (EditKey.Value.IsDown() && _hovered != null)
            {
                Rename(_hovered, _hoveredWaypoint);
            }

            if (DeleteKey.Value.IsDown())
            {
                RemoveFromTool(player);
            }

            if (FinishKey.Value.IsDown())
            {
                if (_tool == MarkerKind.Path) FinishPath();
                else if (_tool == MarkerKind.Area && _shape == AreaShape.Polygon) FinishPolygon();
            }

            if (RecordPathKey.Value.IsDown() && _tool == MarkerKind.Path)
            {
                _recording = !_recording;
                if (_recording)
                {
                    _lastSampleTime = -100f;
                    Say($"Recording path: walk the route, then press {RecordPathKey.Value.MainKey} again or {FinishKey.Value.MainKey} to finish");
                }
                else
                {
                    Say($"Recording paused ({_draft.Count} waypoints)");
                }
            }
        }

        private void SyncToolFromSelection(Player player)
        {
            if (!MarkerTool.TryGetTool(MarkerTool.SelectedPiece(player), out MarkerKind kind, out AreaShape shape))
            {
                return;
            }

            if (kind == _tool && shape == _shape)
            {
                return;
            }

            if (_tool == MarkerKind.Path && (_recording || _draft.Count > 0))
            {
                FinishPath();
            }

            if (_tool == MarkerKind.Area && _polygonDraft.Count > 0)
            {
                FinishPolygon();
            }

            _tool = kind;
            _shape = shape;
        }

        private void UpdateAreaSize()
        {
            if (_tool != MarkerKind.Area || _shape == AreaShape.Polygon || !_hasPreviewPoint || ZInput.m_instance == null)
            {
                return;
            }

            float scroll = ZInput.m_instance.Internal_GetMouseScrollWheel();
            if (Mathf.Abs(scroll) < 0.0001f)
            {
                return;
            }

            float step = scroll > 0f ? 1f : -1f;
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if (ctrl)
            {
                _rotation = (_rotation + step * 15f + 360f) % 360f;
            }
            else if (_shape == AreaShape.Circle)
            {
                _radius = Mathf.Clamp(_radius + step * (_radius >= 10f ? 2f : 1f), 0.5f, 120f);
            }
            else if (shift)
            {
                _depth = Mathf.Clamp(_depth + step * 2f, 1f, 200f);
            }
            else
            {
                _width = Mathf.Clamp(_width + step * 2f, 1f, 200f);
            }
        }

        private static void HideGhost(Player player)
        {
            if (player.m_placementGhost != null && player.m_placementGhost.activeSelf)
            {
                player.m_placementGhost.SetActive(false);
            }

            if (player.m_placementMarkerInstance != null && player.m_placementMarkerInstance.activeSelf)
            {
                player.m_placementMarkerInstance.SetActive(false);
            }
        }

        private void UpdatePreviewPoint(Player player, bool freeFly)
        {
            GameObject? ghost = player.m_placementGhost;
            _hasPreviewPoint = !freeFly && ghost != null && ghost.activeSelf && player.m_placementStatus != Player.PlacementStatus.NoRayHits;
            if (_hasPreviewPoint)
            {
                _previewPoint = ghost!.transform.position;
            }
        }

        private void UpdateHover()
        {
            Transform cam = GameCamera.instance.transform;
            Ray ray = new(cam.position, cam.forward);
            Marker? best = null;
            int bestWaypoint = -1;
            float bestScore = float.MaxValue;
            foreach (Marker marker in MarkerStore.Markers)
            {
                if (marker.Kind == MarkerKind.Path)
                {
                    for (int i = 0; i < marker.waypoints.Count; i++)
                    {
                        float score = RayScore(ray, marker.waypoints[i].position, 0.6f);
                        if (score < bestScore)
                        {
                            bestScore = score;
                            best = marker;
                            bestWaypoint = i;
                        }
                    }

                    continue;
                }

                Vector3 anchor = marker.Kind == MarkerKind.Shot ? marker.cameraPosition : marker.position + Vector3.up * 0.8f;
                float tolerance = marker.Kind == MarkerKind.Area ? 1.5f : 0.8f;
                float s = RayScore(ray, anchor, tolerance);
                if (s < bestScore)
                {
                    bestScore = s;
                    best = marker;
                    bestWaypoint = -1;
                }
            }

            _hovered = best;
            _hoveredWaypoint = bestWaypoint;
        }

        private static float RayScore(Ray ray, Vector3 point, float tolerance)
        {
            Vector3 toPoint = point - ray.origin;
            float along = Vector3.Dot(toPoint, ray.direction);
            if (along < 0.2f || along > 120f)
            {
                return float.MaxValue;
            }

            float off = Vector3.Cross(ray.direction, toPoint).magnitude;
            float allowed = tolerance + along * 0.02f;
            return off <= allowed ? off / allowed + along * 0.001f : float.MaxValue;
        }

        /// <summary>Called from the build system's place action. Returns true when the click did something.</summary>
        public static bool PlaceFromTool(Player player, Piece piece)
        {
            if (_instance == null || !MarkerTool.TryGetTool(piece, out MarkerKind kind, out AreaShape shape))
            {
                return false;
            }

            MarkerMode m = _instance;
            m._tool = kind;
            m._shape = shape;
            GameObject? ghost = player.m_placementGhost;
            bool hasPoint = ghost != null && ghost.activeSelf && player.m_placementStatus != Player.PlacementStatus.NoRayHits;
            Vector3 point = hasPoint ? ghost!.transform.position : Vector3.zero;

            switch (kind)
            {
                case MarkerKind.Shot:
                    m.PlaceShot();
                    Swing(player);
                    return true;

                case MarkerKind.Point:
                    if (!hasPoint)
                    {
                        Say("Nothing under the crosshair to mark", true);
                        return false;
                    }

                    m.Commit(new Marker { Kind = MarkerKind.Point, position = point }, "Point name");
                    Swing(player);
                    return true;

                case MarkerKind.Area when shape == AreaShape.Polygon:
                    if (!hasPoint)
                    {
                        Say("Nothing under the crosshair to mark", true);
                        return false;
                    }

                    if (m._polygonDraft.Count >= 3 && Vector3.Distance(m._polygonDraft[0], point) < 1.5f)
                    {
                        m.FinishPolygon();
                    }
                    else
                    {
                        m._polygonDraft.Add(point);
                        Say(m._polygonDraft.Count >= 3
                            ? $"Corner {m._polygonDraft.Count}. Click the first corner to close the area."
                            : $"Corner {m._polygonDraft.Count}");
                    }

                    Swing(player);
                    return true;

                case MarkerKind.Area:
                    if (!hasPoint)
                    {
                        Say("Nothing under the crosshair to mark", true);
                        return false;
                    }

                    m.Commit(new Marker
                    {
                        Kind = MarkerKind.Area,
                        position = point,
                        Shape = shape,
                        radius = m._radius,
                        width = m._width,
                        depth = m._depth,
                        rotation = m._rotation,
                        tag = "room"
                    }, "Area name");
                    Swing(player);
                    return true;

                case MarkerKind.Path:
                    Vector3 eye = m.EyePosition();
                    if (m._draft.Count >= 2 && Vector3.Distance(m._draft[m._draft.Count - 1].position, eye) < 0.5f)
                    {
                        m.FinishPath();
                    }
                    else
                    {
                        m.SampleWaypoint(true);
                    }

                    Swing(player);
                    return true;
            }

            return false;
        }

        /// <summary>Called from the build system's remove action and the Delete key.</summary>
        public static void RemoveFromTool(Player player)
        {
            if (_instance == null)
            {
                return;
            }

            MarkerMode m = _instance;
            if (m._tool == MarkerKind.Path && m._draft.Count > 0)
            {
                m._draft.RemoveAt(m._draft.Count - 1);
                Say($"Waypoint removed ({m._draft.Count} left in the draft)");
                return;
            }

            if (m._tool == MarkerKind.Area && m._polygonDraft.Count > 0)
            {
                m._polygonDraft.RemoveAt(m._polygonDraft.Count - 1);
                Say($"Corner removed ({m._polygonDraft.Count} left in the draft)");
                return;
            }

            if (m._hovered == null)
            {
                Say("No mark under the crosshair", true);
                return;
            }

            if (m._hovered.Kind == MarkerKind.Path && m._hoveredWaypoint >= 0 && m._hovered.waypoints.Count > 2)
            {
                m._hovered.waypoints.RemoveAt(m._hoveredWaypoint);
                m._hovered.InvalidateCache();
                MarkerStore.Save();
                Say($"Waypoint {m._hoveredWaypoint + 1} removed from \"{m._hovered.label}\"");
            }
            else
            {
                string label = m._hovered.label;
                MarkerStore.Remove(m._hovered);
                m._hovered = null;
                Say($"Removed \"{label}\"");
            }

            Swing(player);
        }

        private static void Swing(Player player)
        {
            ItemDrop.ItemData? right = player.GetRightItem();
            if (right == null)
            {
                return;
            }

            player.FaceLookDirection();
            player.m_zanim.SetTrigger(right.m_shared.m_attack.m_attackAnimation);
        }

        private void PlaceShot()
        {
            Transform cam = GameCamera.instance.transform;
            Commit(new Marker { Kind = MarkerKind.Shot, cameraPosition = cam.position, lookDirection = cam.forward }, "Shot name");
        }

        private void Commit(Marker marker, string topic)
        {
            MarkerStore.Add(marker);
            Rename(marker, -1, topic);
        }

        public static void Rename(Marker marker, int waypoint, string? topic = null)
        {
            if (TextInput.instance == null)
            {
                return;
            }

            if (waypoint >= 0 && waypoint < marker.waypoints.Count)
            {
                TextInput.instance.RequestText(new WaypointHoldReceiver(marker, waypoint), $"Hold seconds at waypoint {waypoint + 1} of \"{marker.label}\"", 6);
                return;
            }

            TextInput.instance.RequestText(new MarkerLabelReceiver(marker), topic ?? $"Rename {marker.kind}", 60);
        }

        private Vector3 EyePosition()
        {
            return GameCamera.InFreeFly()
                ? GameCamera.instance.transform.position
                : Player.m_localPlayer.transform.position + Vector3.up * EyeHeight.Value;
        }

        private void SampleWaypoint(bool manual)
        {
            Vector3 position = EyePosition();
            Vector3 forward = GameCamera.instance.transform.forward;
            _lastSampleTime = Time.time;
            if (!manual && _draft.Count > 0)
            {
                Waypoint last = _draft[_draft.Count - 1];
                bool moved = Vector3.Distance(last.position, position) > 0.35f;
                bool turned = Vector3.Angle(last.forward, forward) > 8f;
                if (!moved && !turned)
                {
                    return;
                }
            }

            _draft.Add(new Waypoint { position = position, forward = forward, hold = 0f });
            if (manual)
            {
                Say(_draft.Count >= 2
                    ? $"Waypoint {_draft.Count}. Click again without moving to finish the path."
                    : "Waypoint 1. Walk on and click for the next one.");
            }
        }

        private void FinishPath()
        {
            _recording = false;
            if (_draft.Count < 2)
            {
                _draft.Clear();
                Say("Path discarded: it needs at least two waypoints", true);
                return;
            }

            Marker path = new() { Kind = MarkerKind.Path, waypoints = new List<Waypoint>(_draft) };
            path.position = path.waypoints[0].position;
            _draft.Clear();
            Commit(path, "Path name");
        }

        private void FinishPolygon()
        {
            if (_polygonDraft.Count < 3)
            {
                _polygonDraft.Clear();
                Say("Area discarded: it needs at least three corners", true);
                return;
            }

            Marker area = new()
            {
                Kind = MarkerKind.Area,
                Shape = AreaShape.Polygon,
                vertices = new List<Vector3>(_polygonDraft),
                tag = "room"
            };
            area.position = Marker.Centroid(area.vertices);
            _polygonDraft.Clear();
            Commit(area, "Area name");
        }

        private void ClearDrafts()
        {
            _draft.Clear();
            _polygonDraft.Clear();
            _recording = false;
        }

        /// <summary>Shows a message the way the game does: top left for information, center for a problem.</summary>
        public static void Say(string text, bool problem = false)
        {
            Player? player = Player.m_localPlayer;
            if (player == null)
            {
                return;
            }

            player.Message(problem ? MessageHud.MessageType.Center : MessageHud.MessageType.TopLeft, text);
        }

        /// <summary>Draws the in-progress area, circle or path over the world while the tool is out.</summary>
        public static void DrawPreview(float alpha)
        {
            if (_instance == null)
            {
                return;
            }

            MarkerMode m = _instance;
            Color color = MarkerRenderer.PreviewColor;
            color.a *= alpha;
            switch (m._tool)
            {
                case MarkerKind.Area when m._shape == AreaShape.Polygon:
                {
                    Color poly = MarkerRenderer.AreaColor;
                    poly.a *= alpha;
                    for (int i = 0; i < m._polygonDraft.Count; i++)
                    {
                        MarkerRenderer.DrawPost(m._polygonDraft[i], poly, 1.2f);
                        if (i > 0)
                        {
                            MarkerRenderer.Line(m._polygonDraft[i - 1] + Vector3.up * 0.1f, m._polygonDraft[i] + Vector3.up * 0.1f, poly);
                        }
                    }

                    if (m._polygonDraft.Count > 0 && m._hasPreviewPoint)
                    {
                        MarkerRenderer.Line(m._polygonDraft[m._polygonDraft.Count - 1] + Vector3.up * 0.1f, m._previewPoint + Vector3.up * 0.1f, color);
                        if (m._polygonDraft.Count >= 2)
                        {
                            Color faint = poly;
                            faint.a *= 0.4f;
                            MarkerRenderer.Line(m._previewPoint + Vector3.up * 0.1f, m._polygonDraft[0] + Vector3.up * 0.1f, faint);
                        }
                    }

                    break;
                }
                case MarkerKind.Path:
                {
                    Color pathColor = MarkerRenderer.PathColor;
                    pathColor.a *= alpha;
                    if (m._draft.Count > 0)
                    {
                        MarkerRenderer.DrawPolyline(PathPlayer.SampleSpline(m._draft, 8), pathColor);
                        MarkerRenderer.DrawWaypoints(m._draft, pathColor, -1);
                    }

                    break;
                }
            }
        }
    }
}
