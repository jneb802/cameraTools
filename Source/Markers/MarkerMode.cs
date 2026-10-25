using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;

namespace cameraTools.Markers
{
    public class MarkerMode : MonoBehaviour
    {
        private static MarkerMode? _instance;
        private static readonly int PlaceMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "piece_nonsolid", "terrain", "vehicle");
        private static readonly string[] AreaTags = { "room", "exterior", "courtyard", "detail", "hero", "entrance", "other" };
        private const int EditWindowId = 98241;
        private const int StatusWindowId = 98242;

        public static ConfigEntry<KeyboardShortcut> ToggleKey = null!;
        public static ConfigEntry<KeyboardShortcut> CycleToolKey = null!;
        public static ConfigEntry<KeyboardShortcut> ShapeKey = null!;
        public static ConfigEntry<KeyboardShortcut> FinishPathKey = null!;
        public static ConfigEntry<KeyboardShortcut> RecordPathKey = null!;
        public static ConfigEntry<KeyboardShortcut> EditKey = null!;
        public static ConfigEntry<KeyboardShortcut> DeleteKey = null!;
        public static ConfigEntry<float> RecordInterval = null!;
        public static ConfigEntry<float> EyeHeight = null!;

        public static bool Active { get; private set; }
        public static bool IsEditing => _instance != null && _instance._editing != null;
        public static Marker? Hovered => _instance?._hovered;
        public static int HoveredWaypoint => _instance?._hoveredWaypoint ?? -1;
        public static Marker? Highlighted;

        public static bool BlockPlayerActions => Active && !GameCamera.InFreeFly();
        public static bool ConsumeScroll => Active && !IsEditing && _instance != null && _instance._tool == MarkerKind.Area && !UiBlocked();

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
        private bool _recording;
        private float _lastSampleTime;

        private Marker? _hovered;
        private int _hoveredWaypoint = -1;

        private Marker? _editing;
        private int _editingWaypoint = -1;
        private string _editLabel = "";
        private string _editNote = "";
        private string _editHold = "0";
        private Rect _editRect;
        private bool _focusLabel;
        private string _flash = "";
        private float _flashUntil;

        public static void BindConfig(ConfigFile config)
        {
            ToggleKey = config.Bind("Markers", "ToggleKey", new KeyboardShortcut(KeyCode.F7), "Toggle marker mode");
            CycleToolKey = config.Bind("Markers", "CycleToolKey", new KeyboardShortcut(KeyCode.T), "Cycle marker tool: area, point, shot, path");
            ShapeKey = config.Bind("Markers", "ShapeKey", new KeyboardShortcut(KeyCode.R), "Cycle area shape: circle, rectangle");
            FinishPathKey = config.Bind("Markers", "FinishPathKey", new KeyboardShortcut(KeyCode.F), "Finish the path being drawn");
            RecordPathKey = config.Bind("Markers", "RecordPathKey", new KeyboardShortcut(KeyCode.G), "Start or stop live path recording");
            EditKey = config.Bind("Markers", "EditKey", new KeyboardShortcut(KeyCode.E), "Edit the marker under the crosshair");
            DeleteKey = config.Bind("Markers", "DeleteKey", new KeyboardShortcut(KeyCode.Delete), "Delete the marker under the crosshair, or the last draft waypoint");
            RecordInterval = config.Bind("Markers", "RecordInterval", 0.5f, "Seconds between samples when live-recording a path");
            EyeHeight = config.Bind("Markers", "EyeHeight", 1.7f, "Camera height above the feet when a path waypoint is placed while walking");
        }

        private void Awake()
        {
            _instance = this;
            _editRect = new Rect(Screen.width / 2f - 220f, Screen.height / 2f - 140f, 440f, 280f);
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
                _instance.CancelEdit();
                _instance._recording = false;
                _instance._hovered = null;
            }
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

            if (!Active)
            {
                if (ToggleKey.Value.IsDown() && !UiBlocked())
                {
                    SetActive(true);
                    Flash("Marker mode on");
                }

                return;
            }

            if (ToggleKey.Value.IsDown() && !UiBlocked() && _editing == null)
            {
                SetActive(false);
                Flash("Marker mode off");
                return;
            }

            MarkerStore.EnsureLoaded();
            if (_editing != null || UiBlocked())
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
                _tool = (MarkerKind)(((int)_tool + 1) % 4);
                if (_tool != MarkerKind.Path && (_recording || _draft.Count > 0))
                {
                    FinishPath();
                }

                Flash($"Tool: {_tool}");
            }

            if (ShapeKey.Value.IsDown() && _tool == MarkerKind.Area)
            {
                _shape = _shape == AreaShape.Circle ? AreaShape.Rectangle : AreaShape.Circle;
                _previewOutline = null;
            }

            if (_tool == MarkerKind.Area)
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
                BeginEdit(_hovered, _hoveredWaypoint);
            }

            if (DeleteKey.Value.IsDown())
            {
                if (_tool == MarkerKind.Path && _draft.Count > 0)
                {
                    _draft.RemoveAt(_draft.Count - 1);
                    Flash($"Draft waypoint removed ({_draft.Count} left)");
                }
                else if (_hovered != null)
                {
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
                        _hovered = null;
                        Flash($"Removed \"{label}\"");
                    }
                }
            }

            if (FinishPathKey.Value.IsDown() && _tool == MarkerKind.Path)
            {
                FinishPath();
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

        private Transform CameraTransform => GameCamera.instance.transform;

        private void UpdatePreviewPoint()
        {
            Transform cam = CameraTransform;
            _hasPreviewPoint = Physics.Raycast(cam.position, cam.forward, out RaycastHit hit, 200f, PlaceMask);
            if (_hasPreviewPoint)
            {
                _previewPoint = hit.point;
            }

            if (_tool == MarkerKind.Area && _hasPreviewPoint && (_previewOutline == null || Time.time - _previewOutlineTime > 0.15f))
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
                        Flash("Nothing under the crosshair to place an area on");
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
                    MarkerStore.Add(area);
                    BeginEdit(area, -1);
                    break;
                case MarkerKind.Point:
                    if (!_hasPreviewPoint)
                    {
                        Flash("Nothing under the crosshair to place a point on");
                        return;
                    }

                    Marker point = new() { Kind = MarkerKind.Point, position = _previewPoint };
                    MarkerStore.Add(point);
                    BeginEdit(point, -1);
                    break;
                case MarkerKind.Shot:
                    Marker shot = new()
                    {
                        Kind = MarkerKind.Shot,
                        cameraPosition = CameraTransform.position,
                        lookDirection = CameraTransform.forward
                    };
                    MarkerStore.Add(shot);
                    BeginEdit(shot, -1);
                    break;
                case MarkerKind.Path:
                    SampleWaypoint(true);
                    break;
            }
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
            MarkerStore.Add(path);
            BeginEdit(path, -1);
        }

        private void BeginEdit(Marker marker, int waypoint)
        {
            _editing = marker;
            _editingWaypoint = waypoint;
            _editLabel = marker.label;
            _editNote = marker.note;
            _editHold = waypoint >= 0 && waypoint < marker.waypoints.Count
                ? marker.waypoints[waypoint].hold.ToString("F1", CultureInfo.InvariantCulture)
                : "0";
            _focusLabel = true;
        }

        private void CancelEdit()
        {
            _editing = null;
            _editingWaypoint = -1;
        }

        private void SaveEdit()
        {
            if (_editing == null)
            {
                return;
            }

            _editing.label = _editLabel.Trim();
            _editing.note = _editNote.Trim();
            if (_editingWaypoint >= 0 && _editingWaypoint < _editing.waypoints.Count &&
                float.TryParse(_editHold, NumberStyles.Float, CultureInfo.InvariantCulture, out float hold))
            {
                _editing.waypoints[_editingWaypoint].hold = Mathf.Clamp(hold, 0f, 30f);
            }

            MarkerStore.Save();
            Flash($"Saved \"{_editing.label}\"");
            CancelEdit();
        }

        private void Flash(string text)
        {
            _flash = text;
            _flashUntil = Time.time + 3f;
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
                    if (m._hasPreviewPoint && m._previewOutline != null)
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

        private void OnGUI()
        {
            if (!Active || Player.m_localPlayer == null)
            {
                return;
            }

            DrawStatus();
            if (_editing != null)
            {
                _editRect = GUILayout.Window(EditWindowId, _editRect, DrawEditWindow, EditTitle());
            }
        }

        private string EditTitle()
        {
            if (_editing == null)
            {
                return "Marker";
            }

            string title = $"{_editing.kind}: {_editing.id}";
            if (_editingWaypoint >= 0)
            {
                title += $"  waypoint {_editingWaypoint + 1}/{_editing.waypoints.Count}";
            }

            return title;
        }

        private void DrawStatus()
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            string tool;
            switch (_tool)
            {
                case MarkerKind.Area:
                    tool = _shape == AreaShape.Circle
                        ? $"AREA circle r={_radius.ToString("F0", ci)}   scroll: radius   ctrl+scroll: rotate   {ShapeKey.Value.MainKey}: shape"
                        : $"AREA rect {_width.ToString("F0", ci)}x{_depth.ToString("F0", ci)} rot={_rotation.ToString("F0", ci)}   scroll: width   shift+scroll: depth   ctrl+scroll: rotate   {ShapeKey.Value.MainKey}: shape";
                    break;
                case MarkerKind.Point:
                    tool = "POINT   click: place under crosshair";
                    break;
                case MarkerKind.Shot:
                    tool = "SHOT   click: save this camera view";
                    break;
                default:
                    tool = $"PATH {_draft.Count} pts{(_recording ? "  REC" : "")}   click: waypoint   {RecordPathKey.Value.MainKey}: record   {FinishPathKey.Value.MainKey}: finish   {DeleteKey.Value.MainKey}: undo";
                    break;
            }

            string hover = _hovered != null
                ? $"looking at: {_hovered.kind} \"{_hovered.label}\"{(_hoveredWaypoint >= 0 ? $" wp {_hoveredWaypoint + 1}" : "")}   {EditKey.Value.MainKey}: edit   {DeleteKey.Value.MainKey}: delete"
                : "";
            string line3 = Time.time < _flashUntil ? _flash : $"{MarkerStore.Markers.Count} markers in {MarkerStore.WorldName}   {CycleToolKey.Value.MainKey}: next tool   {ToggleKey.Value.MainKey}: exit";

            GUIStyle style = new(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 13, normal = { textColor = Color.white } };
            string text = $"MARKERS   {tool}\n{hover}\n{line3}";
            Vector2 size = style.CalcSize(new GUIContent(text));
            GUI.Box(new Rect((Screen.width - size.x) / 2f - 8f, 48f, size.x + 16f, size.y + 10f), text, style);

            // Crosshair
            float cx = Screen.width / 2f;
            float cy = Screen.height / 2f;
            GUI.Box(new Rect(cx - 6f, cy - 1f, 12f, 2f), GUIContent.none);
            GUI.Box(new Rect(cx - 1f, cy - 6f, 2f, 12f), GUIContent.none);
        }

        private void DrawEditWindow(int id)
        {
            if (_editing == null)
            {
                return;
            }

            GUILayout.Label("Label");
            GUI.SetNextControlName("markerLabel");
            _editLabel = GUILayout.TextField(_editLabel, 80);
            GUILayout.Label("Note (becomes the call-out text)");
            _editNote = GUILayout.TextArea(_editNote, 400, GUILayout.Height(60f));

            if (_editing.Kind == MarkerKind.Area)
            {
                GUILayout.Label("Type");
                GUILayout.BeginHorizontal();
                foreach (string tag in AreaTags)
                {
                    bool selected = _editing.tag == tag;
                    if (GUILayout.Toggle(selected, tag, GUI.skin.button) && !selected)
                    {
                        _editing.tag = tag;
                    }
                }

                GUILayout.EndHorizontal();
            }

            if (_editingWaypoint >= 0)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Hold at this waypoint (seconds)", GUILayout.Width(220f));
                _editHold = GUILayout.TextField(_editHold, 6, GUILayout.Width(60f));
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(6f);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Save"))
            {
                SaveEdit();
            }

            if (GUILayout.Button("Delete"))
            {
                Marker doomed = _editing;
                CancelEdit();
                MarkerStore.Remove(doomed);
                Flash($"Removed \"{doomed.label}\"");
            }

            if (GUILayout.Button("Cancel"))
            {
                CancelEdit();
            }

            GUILayout.EndHorizontal();

            Event e = Event.current;
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                CancelEdit();
                e.Use();
            }
            else if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) && GUI.GetNameOfFocusedControl() == "markerLabel")
            {
                SaveEdit();
                e.Use();
            }

            if (_focusLabel)
            {
                GUI.FocusControl("markerLabel");
                _focusLabel = false;
            }

            GUI.DragWindow();
        }
    }
}
