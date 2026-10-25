using System;
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
            if (MarkerVisibility.Mode == MarkerVisibilityMode.Hide)
            {
                MarkerVisibility.Mode = MarkerVisibilityMode.Auto;
            }

            MarkerMode.Flash("Camera returned to your character");
        }

        public static void Tick()
        {
            if (!string.IsNullOrEmpty(_what) && !GameCamera.InFreeFly())
            {
                _what = "";
            }
        }
    }

    public class MarkerMode : MonoBehaviour
    {
        private static MarkerMode? _instance;
        private static readonly int PlaceMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "piece_nonsolid", "terrain", "vehicle");
        public static readonly string[] AreaTags = { "room", "exterior", "courtyard", "detail", "hero", "entrance", "other" };

        public static ConfigEntry<KeyboardShortcut> ToggleKey = null!;
        public static ConfigEntry<KeyboardShortcut> CycleToolKey = null!;
        public static ConfigEntry<KeyboardShortcut> ShapeKey = null!;
        public static ConfigEntry<KeyboardShortcut> FinishKey = null!;
        public static ConfigEntry<KeyboardShortcut> RecordPathKey = null!;
        public static ConfigEntry<KeyboardShortcut> EditKey = null!;
        public static ConfigEntry<KeyboardShortcut> DeleteKey = null!;
        public static ConfigEntry<KeyboardShortcut> ReturnKey = null!;
        public static ConfigEntry<float> RecordInterval = null!;
        public static ConfigEntry<float> EyeHeight = null!;

        public static bool Active { get; private set; }
        public static bool IsEditing => false;
        public static Marker? Hovered => _instance?._hovered;
        public static int HoveredWaypoint => _instance?._hoveredWaypoint ?? -1;
        public static Marker? Highlighted;
        public static Marker? Selected;

        public static MarkerKind Tool
        {
            get => _instance?._tool ?? MarkerKind.Area;
            set { if (_instance != null) _instance.SetTool(value); }
        }

        public static AreaShape Shape
        {
            get => _instance?._shape ?? AreaShape.Circle;
            set { if (_instance != null) { _instance._shape = value; _instance._previewOutline = null; } }
        }

        public static bool BlockPlayerActions => Active && !GameCamera.InFreeFly();
        public static bool ConsumeScroll => Active && _instance != null && _instance._tool == MarkerKind.Area && _instance._shape != AreaShape.Polygon && !UiBlocked();

        private MarkerKind _tool = MarkerKind.Area;
        private AreaShape _shape = AreaShape.Circle;
        private float _radius = 6f;
        private float _width = 10f;
        private float _depth = 10f;
        private float _rotation;
        private bool _hasPreviewPoint;
        private Vector3 _previewPoint;
        private List<Vector3>? _previewOutline;
        private float _previewOutlineTime;

        private readonly List<Waypoint> _draft = new();
        private readonly List<Vector3> _polygonDraft = new();
        private bool _recording;
        private float _lastSampleTime;

        private Marker? _hovered;
        private int _hoveredWaypoint = -1;

        private string _flash = "";
        private float _flashUntil;

        public static void BindConfig(ConfigFile config)
        {
            ToggleKey = config.Bind("Markers", "ToggleKey", new KeyboardShortcut(KeyCode.F7), "Toggle marker mode");
            CycleToolKey = config.Bind("Markers", "CycleToolKey", new KeyboardShortcut(KeyCode.T), "Cycle marker tool: area, point, shot, path");
            ShapeKey = config.Bind("Markers", "ShapeKey", new KeyboardShortcut(KeyCode.R), "Cycle area shape: circle, rectangle, polygon");
            FinishKey = config.Bind("Markers", "FinishKey", new KeyboardShortcut(KeyCode.F), "Finish the path or polygon being drawn");
            RecordPathKey = config.Bind("Markers", "RecordPathKey", new KeyboardShortcut(KeyCode.G), "Start or stop live path recording");
            EditKey = config.Bind("Markers", "EditKey", new KeyboardShortcut(KeyCode.E), "Rename the marker under the crosshair (or set a waypoint hold)");
            DeleteKey = config.Bind("Markers", "DeleteKey", new KeyboardShortcut(KeyCode.Delete), "Delete the marker under the crosshair, or the last draft point");
            ReturnKey = config.Bind("Markers", "ReturnKey", new KeyboardShortcut(KeyCode.F8), "Leave the free camera and return to your character (after Go to, Preview or path playback)");
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

        public static void SetActive(bool active)
        {
            if (_instance == null)
            {
                return;
            }

            Active = active;
            if (active)
            {
                MarkerStore.EnsureLoaded();
                MarkerVisibility.Mode = MarkerVisibilityMode.Auto;
            }
            else
            {
                _instance._recording = false;
                _instance._hovered = null;
            }
        }

        private void SetTool(MarkerKind tool)
        {
            if (_tool == MarkerKind.Path && tool != MarkerKind.Path && (_recording || _draft.Count > 0))
            {
                FinishPath();
            }

            if (_tool == MarkerKind.Area && tool != MarkerKind.Area && _polygonDraft.Count > 0)
            {
                FinishPolygon();
            }

            _tool = tool;
            _previewOutline = null;
        }

        public static bool UiBlocked()
        {
            return Console.IsVisible()
                   || (Chat.instance != null && Chat.instance.HasFocus())
                   || TextInput.IsVisible()
                   || Menu.IsVisible()
                   || InventoryGui.IsVisible()
                   || Minimap.IsOpen();
        }

        private void Update()
        {
            if (Player.m_localPlayer == null || GameCamera.instance == null)
            {
                return;
            }

            MarkerCamera.Tick();
            if (ReturnKey.Value.IsDown() && !UiBlocked() && GameCamera.InFreeFly())
            {
                MarkerCamera.Return();
                return;
            }

            if (!Active)
            {
                if (ToggleKey.Value.IsDown() && !UiBlocked())
                {
                    SetActive(true);
                    Flash("Marker mode on");
                }

                return;
            }

            if (ToggleKey.Value.IsDown() && !UiBlocked())
            {
                SetActive(false);
                Flash("Marker mode off");
                return;
            }

            MarkerStore.EnsureLoaded();
            if (UiBlocked())
            {
                return;
            }

            UpdatePreviewPoint();
            UpdateHover();

            if (_recording && Time.time - _lastSampleTime >= RecordInterval.Value)
            {
                SampleWaypoint(false);
            }

            if (CycleToolKey.Value.IsDown())
            {
                SetTool((MarkerKind)(((int)_tool + 1) % 4));
                Flash($"Tool: {_tool}");
            }

            if (ShapeKey.Value.IsDown() && _tool == MarkerKind.Area)
            {
                if (_polygonDraft.Count > 0)
                {
                    FinishPolygon();
                }

                _shape = (AreaShape)(((int)_shape + 1) % 3);
                _previewOutline = null;
            }

            if (_tool == MarkerKind.Area && _shape != AreaShape.Polygon)
            {
                float scroll = ZInput.m_instance != null ? ZInput.m_instance.Internal_GetMouseScrollWheel() : 0f;
                if (Mathf.Abs(scroll) > 0.0001f)
                {
                    float step = scroll > 0f ? 1f : -1f;
                    bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                    bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
                    if (ctrl)
                    {
                        _rotation = (_rotation + step * 15f + 360f) % 360f;
                    }
                    else if (_shape == AreaShape.Circle)
                    {
                        _radius = Mathf.Clamp(_radius + step * (_radius >= 10f ? 2f : 1f), 1f, 120f);
                    }
                    else if (shift)
                    {
                        _depth = Mathf.Clamp(_depth + step * 2f, 1f, 200f);
                    }
                    else
                    {
                        _width = Mathf.Clamp(_width + step * 2f, 1f, 200f);
                    }

                    _previewOutline = null;
                }
            }

            if (ZInput.GetMouseButtonDown(0))
            {
                Place();
            }

            if (EditKey.Value.IsDown() && _hovered != null)
            {
                Rename(_hovered, _hoveredWaypoint);
            }

            if (DeleteKey.Value.IsDown())
            {
                HandleDelete();
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
                    Flash("Recording path: move, then press the record key again or finish");
                }
                else
                {
                    Flash($"Recording paused ({_draft.Count} waypoints)");
                }
            }
        }

        private void HandleDelete()
        {
            if (_tool == MarkerKind.Path && _draft.Count > 0)
            {
                _draft.RemoveAt(_draft.Count - 1);
                Flash($"Draft waypoint removed ({_draft.Count} left)");
                return;
            }

            if (_tool == MarkerKind.Area && _polygonDraft.Count > 0)
            {
                _polygonDraft.RemoveAt(_polygonDraft.Count - 1);
                Flash($"Polygon vertex removed ({_polygonDraft.Count} left)");
                return;
            }

            if (_hovered == null)
            {
                return;
            }

            if (_hovered.Kind == MarkerKind.Path && _hoveredWaypoint >= 0 && _hovered.waypoints.Count > 2)
            {
                _hovered.waypoints.RemoveAt(_hoveredWaypoint);
                _hovered.InvalidateCache();
                MarkerStore.Save();
                Flash($"Waypoint {_hoveredWaypoint + 1} removed from \"{_hovered.label}\"");
            }
            else
            {
                string label = _hovered.label;
                MarkerStore.Remove(_hovered);
                if (Selected == _hovered) Selected = null;
                _hovered = null;
                Flash($"Removed \"{label}\"");
            }
        }

        private Transform CameraTransform => GameCamera.instance.transform;

        private void UpdatePreviewPoint()
        {
            Transform cam = CameraTransform;
            _hasPreviewPoint = Physics.Raycast(cam.position, cam.forward, out RaycastHit hit, 200f, PlaceMask);
            if (_hasPreviewPoint)
            {
                _previewPoint = hit.point;
            }

            if (_tool == MarkerKind.Area && _shape != AreaShape.Polygon && _hasPreviewPoint && (_previewOutline == null || Time.time - _previewOutlineTime > 0.15f))
            {
                _previewOutline = MarkerRenderer.BuildOutline(_previewPoint, _shape, _radius, _width, _depth, _rotation);
                _previewOutlineTime = Time.time;
            }
        }

        private void UpdateHover()
        {
            Transform cam = CameraTransform;
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

        private void Place()
        {
            switch (_tool)
            {
                case MarkerKind.Area:
                    if (!_hasPreviewPoint)
                    {
                        Flash("Nothing under the crosshair to place on");
                        return;
                    }

                    if (_shape == AreaShape.Polygon)
                    {
                        if (_polygonDraft.Count >= 3 && Vector3.Distance(_polygonDraft[0], _previewPoint) < 1.5f)
                        {
                            FinishPolygon();
                            return;
                        }

                        _polygonDraft.Add(_previewPoint);
                        Flash(_polygonDraft.Count >= 3
                            ? $"Vertex {_polygonDraft.Count}; click the first vertex or press {FinishKey.Value.MainKey} to close"
                            : $"Vertex {_polygonDraft.Count}");
                        return;
                    }

                    Marker area = new()
                    {
                        Kind = MarkerKind.Area,
                        position = _previewPoint,
                        Shape = _shape,
                        radius = _radius,
                        width = _width,
                        depth = _depth,
                        rotation = _rotation,
                        tag = "room"
                    };
                    Commit(area, "Area name");
                    break;
                case MarkerKind.Point:
                    if (!_hasPreviewPoint)
                    {
                        Flash("Nothing under the crosshair to place on");
                        return;
                    }

                    Commit(new Marker { Kind = MarkerKind.Point, position = _previewPoint }, "Point name");
                    break;
                case MarkerKind.Shot:
                    Commit(new Marker
                    {
                        Kind = MarkerKind.Shot,
                        cameraPosition = CameraTransform.position,
                        lookDirection = CameraTransform.forward
                    }, "Shot name");
                    break;
                case MarkerKind.Path:
                    SampleWaypoint(true);
                    break;
            }
        }

        private void Commit(Marker marker, string topic)
        {
            MarkerStore.Add(marker);
            Selected = marker;
            Flash($"Added {marker.kind} \"{marker.label}\"");
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

        private void SampleWaypoint(bool manual)
        {
            Vector3 position;
            Vector3 forward = CameraTransform.forward;
            if (GameCamera.InFreeFly())
            {
                position = CameraTransform.position;
            }
            else
            {
                position = Player.m_localPlayer.transform.position + Vector3.up * EyeHeight.Value;
            }

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
                Flash($"Waypoint {_draft.Count} added");
            }
        }

        private void FinishPath()
        {
            _recording = false;
            if (_draft.Count < 2)
            {
                _draft.Clear();
                Flash("Path discarded: needs at least two waypoints");
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
                Flash("Polygon discarded: needs at least three vertices");
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

        public static void Flash(string text)
        {
            if (_instance == null) return;
            _instance._flash = text;
            _instance._flashUntil = Time.time + 3f;
        }

        public static string StatusLine1()
        {
            if (_instance == null) return "";
            MarkerMode m = _instance;
            if (!Active && MarkerCamera.Active)
            {
                if (PathPlayer.IsPlaying) return $"CAMERA  playing path \"{PathPlayer.PlayingLabel}\"";
                return $"CAMERA  {MarkerCamera.What}";
            }

            CultureInfo ci = CultureInfo.InvariantCulture;
            switch (m._tool)
            {
                case MarkerKind.Area:
                    return m._shape switch
                    {
                        AreaShape.Circle => $"AREA circle r={m._radius.ToString("F0", ci)}   scroll: radius   ctrl+scroll: rotate   {ShapeKey.Value.MainKey}: shape",
                        AreaShape.Rectangle => $"AREA rect {m._width.ToString("F0", ci)}x{m._depth.ToString("F0", ci)} rot={m._rotation.ToString("F0", ci)}   scroll: width   shift+scroll: depth   ctrl+scroll: rotate   {ShapeKey.Value.MainKey}: shape",
                        _ => $"AREA polygon {m._polygonDraft.Count} vertices   click: add vertex   click first vertex or {FinishKey.Value.MainKey}: close   {DeleteKey.Value.MainKey}: undo   {ShapeKey.Value.MainKey}: shape"
                    };
                case MarkerKind.Point:
                    return "POINT   click: place under crosshair";
                case MarkerKind.Shot:
                    return "SHOT   click: save this camera view";
                default:
                    return $"PATH {m._draft.Count} pts{(m._recording ? "  REC" : "")}   click: waypoint   {RecordPathKey.Value.MainKey}: record   {FinishKey.Value.MainKey}: finish   {DeleteKey.Value.MainKey}: undo";
            }
        }

        public static string StatusLine2()
        {
            if (_instance == null) return "";
            MarkerMode m = _instance;
            if (Time.time < m._flashUntil)
            {
                return m._flash;
            }

            if (!Active && MarkerCamera.Active)
            {
                return $"{ReturnKey.Value.MainKey}: return to your character   {ToggleKey.Value.MainKey}: marker mode   Tab: marker panel";
            }

            if (m._hovered != null)
            {
                return $"looking at {m._hovered.kind} \"{m._hovered.label}\"{(m._hoveredWaypoint >= 0 ? $" wp {m._hoveredWaypoint + 1}" : "")}   {EditKey.Value.MainKey}: rename   {DeleteKey.Value.MainKey}: delete";
            }

            string back = GameCamera.InFreeFly() ? $"   {ReturnKey.Value.MainKey}: return to character" : "";
            return $"{MarkerStore.ActiveBuild.name}: {MarkerStore.Markers.Count} markers   {CycleToolKey.Value.MainKey}: next tool   {ToggleKey.Value.MainKey}: exit{back}   Tab: panel";
        }

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
                case MarkerKind.Area:
                    if (m._shape == AreaShape.Polygon)
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

                        if (m._hasPreviewPoint)
                        {
                            MarkerRenderer.DrawCross(m._previewPoint + Vector3.up * 0.1f, color, 0.5f);
                        }
                    }
                    else if (m._hasPreviewPoint && m._previewOutline != null)
                    {
                        MarkerRenderer.DrawOutline(m._previewOutline, color);
                        MarkerRenderer.DrawCross(m._previewPoint + Vector3.up * 0.2f, color, 0.6f);
                    }

                    break;
                case MarkerKind.Point:
                    if (m._hasPreviewPoint)
                    {
                        MarkerRenderer.DrawPost(m._previewPoint, color, 1.5f);
                    }

                    break;
                case MarkerKind.Path:
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
