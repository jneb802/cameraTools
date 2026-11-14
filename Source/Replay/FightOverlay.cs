using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx;
using Newtonsoft.Json;
using TMPro;
using UnityEngine;

namespace cameraTools.Replay
{
    /// <summary>
    /// Guides for reading a fight in a replay session: a ground grid with named cells, actor trails, facing arrows,
    /// and markers for deaths and hits. Also writes the overhead map image and the scene geometry export.
    /// Nothing here is drawn while <see cref="Hidden"/> is set (renders, screenshots without --overlays).
    /// </summary>
    public sealed class FightOverlay : MonoBehaviour
    {
        internal static FightOverlay Instance { get; private set; } = null!;
        /// <summary>Set by renders and plain screenshots so no guide reaches a picture meant for the video.</summary>
        internal static bool Hidden;
        internal static bool GridOn { get; private set; }
        internal static bool TrailsOn { get; private set; }

        internal const float Major = 5f;
        private static readonly int SurfaceMask = LayerMask.GetMask("terrain", "piece", "static_solid", "Default", "Default_small", "vehicle");
        private static readonly Color[] Palette =
        {
            new(0.95f, 0.35f, 0.25f), new(0.25f, 0.65f, 0.95f), new(0.95f, 0.8f, 0.2f), new(0.45f, 0.85f, 0.35f),
            new(0.85f, 0.4f, 0.85f), new(0.3f, 0.9f, 0.85f), new(1f, 0.6f, 0.2f), new(0.6f, 0.5f, 1f),
            new(0.9f, 0.55f, 0.6f), new(0.7f, 0.9f, 0.5f)
        };

        private float _spacing = 1f;
        private Vector3 _origin;
        private int _columns, _rows;
        private readonly List<(Vector3[] Points, bool Major)> _grid = new();
        private readonly List<TextMeshPro> _labels = new();
        private readonly List<(ActorTrack Track, Color Color, Vector3[] Points)> _trails = new();
        private readonly List<(Vector3 Point, Color Color, float Size)> _marks = new();
        private HashSet<string>? _trailFilter;
        private string _built = "";
        private Material? _material;
        private TMP_FontAsset? _font;
        // While the overhead map renders, trails cover this replay time range and actors show at this time.
        private (float From, float To)? _mapRange;
        private float? _mapTime;

        private void Awake()
        {
            Instance = this;
            Camera.onPostRender += Draw;
        }

        private void OnDestroy() => Camera.onPostRender -= Draw;

        private void LateUpdate()
        {
            bool show = ReplayPlayer.IsPlaying && !Hidden && (GridOn || TrailsOn);
            foreach (TextMeshPro label in _labels) if (label != null && label.gameObject.activeSelf != show) label.gameObject.SetActive(show);
            if (!ReplayPlayer.IsPlaying && _built.Length > 0) Clear();
        }

        /// <summary>The grid's south-west corner and size from the fight area plus a 10 m border, snapped to 5 m.</summary>
        internal static (Vector3 Origin, int Columns, int Rows) Layout(ReplayPlayer player)
        {
            ReplayEventList events = player.Events;
            HashSet<(long, uint)> involved = new(events.Events.Select(e => (e.User, e.Id)));
            if (player.File.Recorder != default) involved.Add(player.File.Recorder);
            Bounds area = player.Tracks.FightArea(involved);
            float minX = Mathf.Floor((area.min.x - 10f) / Major) * Major, minZ = Mathf.Floor((area.min.z - 10f) / Major) * Major;
            float maxX = Mathf.Ceil((area.max.x + 10f) / Major) * Major, maxZ = Mathf.Ceil((area.max.z + 10f) / Major) * Major;
            return (new Vector3(minX, area.center.y, minZ), Mathf.RoundToInt((maxX - minX) / Major), Mathf.RoundToInt((maxZ - minZ) / Major));
        }

        /// <summary>Column letters run west to east (A, B, ... Z, AA), row numbers south to north from 1.</summary>
        internal static string CellName(int column, int row)
        {
            string letters = "";
            for (int value = column + 1; value > 0; value = (value - 1) / 26) letters = (char)('A' + (value - 1) % 26) + letters;
            return letters + (row + 1).ToString(CultureInfo.InvariantCulture);
        }

        internal static (int Column, int Row) ParseCell(string cell)
        {
            string upper = cell.Trim().ToUpperInvariant();
            int split = 0;
            while (split < upper.Length && char.IsLetter(upper[split])) split++;
            if (split == 0 || split == upper.Length || !int.TryParse(upper.Substring(split), out int row) || row < 1)
                throw new ArgumentException($"A cell is letters then a number, like C4, not '{cell}'");
            int column = 0;
            foreach (char letter in upper.Substring(0, split)) column = column * 26 + (letter - 'A' + 1);
            return (column - 1, row - 1);
        }

        internal string Cell(string name)
        {
            ReplayPlayer player = ReplayPlayer.Instance;
            (Vector3 origin, int columns, int rows) = Layout(player);
            (int column, int row) = ParseCell(name);
            if (column >= columns || row >= rows) throw new ArgumentException($"{name} is outside the grid, which runs A1 to {CellName(columns - 1, rows - 1)}");
            float x = origin.x + (column + 0.5f) * Major, z = origin.z + (row + 0.5f) * Major;
            float y = Height(x, z, origin.y);
            return $"cell={name.ToUpperInvariant()} center={F(x)},{F(y)},{F(z)} min={F(origin.x + column * Major)},{F(origin.z + row * Major)} max={F(origin.x + (column + 1) * Major)},{F(origin.z + (row + 1) * Major)}";
        }

        /// <summary>The surface and terrain height straight below a point, measured the same way the scene export measures them.</summary>
        internal static string Probe(float x, float z)
        {
            Vector3 from = new(x, 2000f, z);
            string top = Physics.Raycast(from, Vector3.down, out RaycastHit hit, 4000f, SurfaceMask, QueryTriggerInteraction.Ignore)
                ? $"surface={F(hit.point.y)} object={(hit.collider.gameObject.layer == LayerMask.NameToLayer("terrain") ? "terrain" : Utils.GetPrefabName(hit.collider.GetComponentInParent<ZNetView>()?.gameObject ?? hit.collider.transform.root.gameObject))}" : "surface=none";
            string ground = Physics.Raycast(from, Vector3.down, out RaycastHit floor, 4000f, LayerMask.GetMask("terrain"), QueryTriggerInteraction.Ignore) ? $"terrain={F(floor.point.y)}" : "terrain=none";
            return $"x={F(x)} z={F(z)} {top} {ground}";
        }

        internal string SetGrid(bool on, float spacing)
        {
            GridOn = on;
            if (spacing > 0f && !Mathf.Approximately(spacing, _spacing)) { _spacing = spacing; _built = ""; }
            Rebuild();
            return Status();
        }

        internal string SetTrails(bool on, IEnumerable<string> actors)
        {
            TrailsOn = on;
            HashSet<string> filter = new(actors);
            _trailFilter = filter.Count > 0 ? filter : null;
            _built = "";
            Rebuild();
            return Status();
        }

        internal string Status()
        {
            if (!ReplayPlayer.IsPlaying) return $"grid={GridOn} trails={TrailsOn}";
            (Vector3 origin, int columns, int rows) = Layout(ReplayPlayer.Instance);
            return $"grid={GridOn} trails={TrailsOn} spacing={F(_spacing)} majorSpacing={F(Major)} southWest={F(origin.x)},{F(origin.z)} " +
                $"cells=A1-{CellName(columns - 1, rows - 1)} columns={columns} rows={rows} naming=\"letters west to east, numbers south to north, 5 m cells\"";
        }

        private void Clear()
        {
            foreach (TextMeshPro label in _labels) if (label != null) Destroy(label.gameObject);
            _labels.Clear();
            _grid.Clear();
            _trails.Clear();
            _marks.Clear();
            _built = "";
            if (!ReplayPlayer.IsPlaying) { GridOn = false; TrailsOn = false; }
        }

        private void Rebuild()
        {
            if (!ReplayPlayer.IsPlaying) return;
            ReplayPlayer player = ReplayPlayer.Instance;
            string key = $"{player.FilePath}|{GridOn}|{TrailsOn}|{_spacing}|{(_trailFilter == null ? "" : string.Join(",", _trailFilter))}";
            if (key == _built) return;
            Clear();
            _built = key;
            (_origin, _columns, _rows) = Layout(player);
            if (GridOn) BuildGrid();
            if (TrailsOn) BuildTrails(player);
        }

        private float Height(float x, float z, float fallback)
        {
            Vector3 from = new(x, 2000f, z);
            return Physics.Raycast(from, Vector3.down, out RaycastHit hit, 4000f, SurfaceMask, QueryTriggerInteraction.Ignore) ? hit.point.y : fallback;
        }

        private void BuildGrid()
        {
            float width = _columns * Major, depth = _rows * Major;
            int lines = Mathf.RoundToInt(width / _spacing);
            for (int index = 0; index <= lines; index++)
            {
                float x = _origin.x + index * _spacing;
                _grid.Add((Line(x, _origin.z, x, _origin.z + depth), IsMajor(index * _spacing)));
            }
            lines = Mathf.RoundToInt(depth / _spacing);
            for (int index = 0; index <= lines; index++)
            {
                float z = _origin.z + index * _spacing;
                _grid.Add((Line(_origin.x, z, _origin.x + width, z), IsMajor(index * _spacing)));
            }
            for (int column = 0; column < _columns; column++)
                for (int row = 0; row < _rows; row++)
                {
                    float x = _origin.x + (column + 0.5f) * Major, z = _origin.z + (row + 0.5f) * Major;
                    Label(CellName(column, row), new Vector3(x, Height(x, z, _origin.y) + 0.25f, z), 0.45f, new Color(1f, 1f, 1f, 0.7f));
                }
            // Large column letters along the south and north edges, row numbers along the west and east edges.
            for (int column = 0; column < _columns; column++)
            {
                string letters = CellName(column, 0).TrimEnd('1');
                float x = _origin.x + (column + 0.5f) * Major;
                Label(letters, new Vector3(x, Height(x, _origin.z - 2.5f, _origin.y) + 0.5f, _origin.z - 2.5f), 1.4f, new Color(1f, 0.9f, 0.4f));
                Label(letters, new Vector3(x, Height(x, _origin.z + depth + 2.5f, _origin.y) + 0.5f, _origin.z + depth + 2.5f), 1.4f, new Color(1f, 0.9f, 0.4f));
            }
            for (int row = 0; row < _rows; row++)
            {
                string number = (row + 1).ToString(CultureInfo.InvariantCulture);
                float z = _origin.z + (row + 0.5f) * Major;
                Label(number, new Vector3(_origin.x - 2.5f, Height(_origin.x - 2.5f, z, _origin.y) + 0.5f, z), 1.4f, new Color(1f, 0.9f, 0.4f));
                Label(number, new Vector3(_origin.x + width + 2.5f, Height(_origin.x + width + 2.5f, z, _origin.y) + 0.5f, z), 1.4f, new Color(1f, 0.9f, 0.4f));
            }
            Vector3[] corners = { _origin, _origin + new Vector3(width, 0, 0), _origin + new Vector3(0, 0, depth), _origin + new Vector3(width, 0, depth) };
            foreach (Vector3 corner in corners)
                Label($"{corner.x:F0}, {corner.z:F0}", new Vector3(corner.x, Height(corner.x, corner.z, _origin.y) + 0.4f, corner.z), 1.0f, new Color(0.6f, 1f, 0.6f));
        }

        private bool IsMajor(float offset) => Mathf.Abs(offset / Major - Mathf.Round(offset / Major)) < 0.001f;

        /// <summary>A line laid on the surface: a point every half meter, raised a little so it is not hidden by the ground.</summary>
        private Vector3[] Line(float x0, float z0, float x1, float z1)
        {
            float length = Vector2.Distance(new Vector2(x0, z0), new Vector2(x1, z1));
            int steps = Mathf.Max(1, Mathf.CeilToInt(length / 0.5f));
            Vector3[] points = new Vector3[steps + 1];
            for (int step = 0; step <= steps; step++)
            {
                float t = step / (float)steps;
                float x = Mathf.Lerp(x0, x1, t), z = Mathf.Lerp(z0, z1, t);
                points[step] = new Vector3(x, Height(x, z, _origin.y) + 0.12f, z);
            }
            return points;
        }

        private void BuildTrails(ReplayPlayer player)
        {
            int colorIndex = 0;
            foreach (ActorTrack track in player.Tracks.All)
            {
                if (track.Kind != 0 || track.IsProjectile) continue;
                if (_trailFilter != null && !_trailFilter.Contains(track.Key)) continue;
                if (_trailFilter == null && !Involved(player, track)) continue;
                Color color = player.File.Info.PrefabName(track.Prefab) == "Player" ? Color.white : Palette[colorIndex++ % Palette.Length];
                List<Vector3> points = new();
                for (float time = track.Start; time <= track.End; time += 0.1f)
                {
                    track.Sample(time, out Vector3 position, out _);
                    points.Add(position + Vector3.up * 0.15f);
                }
                _trails.Add((track, color, points.ToArray()));
                for (float second = Mathf.Ceil(track.Start); second <= track.End; second += 1f)
                {
                    track.Sample(second, out Vector3 position, out _);
                    _marks.Add((position + Vector3.up * 0.15f, color, 0.15f));
                    if (Mathf.RoundToInt(second) % 5 == 0) Label($"{second:F0}s", position + Vector3.up * 0.4f, 0.4f, color);
                }
            }
            foreach (ReplayEvent tag in player.Events.Events)
            {
                if (tag.Type == ReplayEventType.Death && player.Tracks.ByKey.TryGetValue((tag.User, tag.Id), out ActorTrack dead))
                {
                    dead.Sample(Mathf.Clamp(tag.Time, dead.Start, dead.End), out Vector3 position, out _);
                    _marks.Add((position + Vector3.up * 0.2f, Color.red, 0.6f));
                    Label($"died {tag.Time:F1}s", position + Vector3.up * 0.8f, 0.5f, Color.red);
                }
                if (tag.Type == ReplayEventType.Hit) _marks.Add((tag.HitData.Point, new Color(1f, 0.5f, 0f), 0.3f));
            }
        }

        private static bool Involved(ReplayPlayer player, ActorTrack track) =>
            (player.File.Recorder.Item1 == track.User && player.File.Recorder.Item2 == track.Id) ||
            player.Events.Events.Any(e => e.User == track.User && e.Id == track.Id);

        private void Label(string text, Vector3 position, float size, Color color)
        {
            _font ??= Resources.FindObjectsOfTypeAll<TMP_FontAsset>().OrderByDescending(font => font.name.Contains("Averia")).FirstOrDefault();
            GameObject holder = new("cameraTools overlay label");
            holder.transform.position = position;
            holder.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            TextMeshPro label = holder.AddComponent<TextMeshPro>();
            if (_font != null) label.font = _font;
            label.text = text;
            label.fontSize = size * 10f;
            label.alignment = TextAlignmentOptions.Center;
            label.color = color;
            label.enableWordWrapping = false;
            label.rectTransform.sizeDelta = new Vector2(20f, 4f);
            _labels.Add(label);
        }

        private Material LineMaterial
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

        private void Draw(Camera camera)
        {
            if (Hidden || !ReplayPlayer.IsPlaying || (!GridOn && !TrailsOn)) return;
            bool game = GameCamera.instance != null && camera.gameObject == GameCamera.instance.gameObject;
            if (!game && camera != _mapCamera) return;
            LineMaterial.SetPass(0);
            GL.PushMatrix();
            GL.MultMatrix(Matrix4x4.identity);
            GL.Begin(GL.LINES);
            foreach ((Vector3[] points, bool major) in _grid)
            {
                GL.Color(major ? new Color(1f, 1f, 1f, 0.85f) : new Color(1f, 1f, 1f, 0.4f));
                Strip(points, major ? 0.03f : 0f);
            }
            float now = _mapTime ?? ReplayPlayer.Instance.PlaybackTime;
            foreach ((ActorTrack track, Color color, Vector3[] points) in _trails)
            {
                GL.Color(color);
                int from = 0, to = points.Length - 1;
                if (_mapRange.HasValue)
                {
                    from = Mathf.Clamp(Mathf.FloorToInt((_mapRange.Value.From - track.Start) / 0.1f), 0, points.Length - 1);
                    to = Mathf.Clamp(Mathf.CeilToInt((_mapRange.Value.To - track.Start) / 0.1f), 0, points.Length - 1);
                }
                for (int index = from; index < to; index++)
                    for (int lane = -1; lane <= 1; lane++)
                    {
                        Vector3 side = Vector3.Cross(Vector3.up, points[index + 1] - points[index]).normalized * (0.09f * lane);
                        GL.Vertex(points[index] + side); GL.Vertex(points[index + 1] + side);
                    }
                if (track.Sample(now, out Vector3 position, out Quaternion rotation))
                {
                    Vector3 forward = rotation * Vector3.forward; forward.y = 0f; forward.Normalize();
                    Vector3 right = Vector3.Cross(Vector3.up, forward);
                    Vector3 tip = position + Vector3.up * 0.3f + forward * 1.6f, root = position + Vector3.up * 0.3f;
                    GL.Vertex(root); GL.Vertex(tip);
                    GL.Vertex(tip); GL.Vertex(tip - forward * 0.5f + right * 0.35f);
                    GL.Vertex(tip); GL.Vertex(tip - forward * 0.5f - right * 0.35f);
                    Circle(position + Vector3.up * 0.3f, 0.6f);
                }
            }
            foreach ((Vector3 point, Color color, float size) in _marks)
            {
                GL.Color(color);
                GL.Vertex(point + new Vector3(-size, 0, -size)); GL.Vertex(point + new Vector3(size, 0, size));
                GL.Vertex(point + new Vector3(-size, 0, size)); GL.Vertex(point + new Vector3(size, 0, -size));
            }
            GL.End();
            GL.PopMatrix();
        }

        private static void Strip(Vector3[] points, float thickness)
        {
            for (int index = 0; index < points.Length - 1; index++)
            {
                GL.Vertex(points[index]); GL.Vertex(points[index + 1]);
                if (thickness <= 0f) continue;
                Vector3 side = Vector3.Cross(Vector3.up, points[index + 1] - points[index]).normalized * thickness;
                GL.Vertex(points[index] + side); GL.Vertex(points[index + 1] + side);
                GL.Vertex(points[index] - side); GL.Vertex(points[index + 1] - side);
            }
        }

        private static void Circle(Vector3 center, float radius)
        {
            for (int step = 0; step < 16; step++)
            {
                float a = step / 16f * Mathf.PI * 2f, b = (step + 1) / 16f * Mathf.PI * 2f;
                GL.Vertex(center + new Vector3(Mathf.Sin(a), 0, Mathf.Cos(a)) * radius);
                GL.Vertex(center + new Vector3(Mathf.Sin(b), 0, Mathf.Cos(b)) * radius);
            }
        }

        private Camera? _mapCamera;
        // An unused layer the game cameras do not draw, for the map's guide pass.
        private const int OverlayLayer = 31;

        private void SetLabels(bool active, int layer)
        {
            foreach (TextMeshPro label in _labels)
            {
                if (label == null) continue;
                label.gameObject.SetActive(active);
                label.gameObject.layer = layer;
            }
        }

        /// <summary>
        /// Renders a straight-down picture of the fight area with the grid and trails, and writes a sidecar that converts
        /// between pixels and world coordinates. Image up is north (+z), right is east (+x).
        /// </summary>
        internal string Map(float? time, (float, float)? range, float sizeMeters, int pixels)
        {
            ReplayPlayer player = ReplayPlayer.Instance;
            (Vector3 origin, int columns, int rows) = Layout(player);
            Vector3 center = origin + new Vector3(columns * Major / 2f, 0f, rows * Major / 2f);
            float size = sizeMeters > 0f ? sizeMeters : Mathf.Max(columns, rows) * Major + 4f;
            bool grid = GridOn, trails = TrailsOn, hidden = Hidden;
            GridOn = true; TrailsOn = true; Hidden = false;
            _mapRange = range ?? (time.HasValue ? null : (0f, player.Duration));
            _mapTime = time ?? player.PlaybackTime;
            Rebuild();
            Camera main = Utils.GetMainCamera();
            GameObject holder = new("cameraTools map camera");
            RenderTexture target = RenderTexture.GetTemporary(pixels, pixels, 24, RenderTextureFormat.ARGB32);
            Texture2D image = new(pixels, pixels, TextureFormat.RGB24, false);
            bool fog = RenderSettings.fog;
            try
            {
                float top = center.y + 150f;
                foreach (ActorTrack track in player.Tracks.All) if (track.Kind == 0) top = Mathf.Max(top, track.Positions.Max(p => p.y) + 100f);
                holder.transform.position = new Vector3(center.x, top, center.z);
                holder.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                _mapCamera = holder.AddComponent<Camera>();
                _mapCamera.enabled = false;
                _mapCamera.orthographic = true;
                _mapCamera.orthographicSize = size / 2f;
                _mapCamera.aspect = 1f;
                _mapCamera.nearClipPlane = 1f;
                _mapCamera.farClipPlane = top - center.y + 400f;
                _mapCamera.cullingMask = main != null ? main.cullingMask : ~0;
                _mapCamera.clearFlags = CameraClearFlags.SolidColor;
                _mapCamera.backgroundColor = new Color(0.1f, 0.1f, 0.12f);
                _mapCamera.targetTexture = target;
                RenderSettings.fog = false;
                // First the world without guides, then the guides alone on top so trees and roofs cannot hide them.
                Hidden = true;
                SetLabels(false, 0);
                _mapCamera.Render();
                Hidden = false;
                _mapCamera.clearFlags = CameraClearFlags.Depth;
                _mapCamera.cullingMask = 1 << OverlayLayer;
                SetLabels(true, OverlayLayer);
                _mapCamera.Render();
                SetLabels(true, 0);
                RenderTexture previous = RenderTexture.active;
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, pixels, pixels), 0, 0);
                image.Apply();
                RenderTexture.active = previous;
            }
            finally
            {
                RenderSettings.fog = fog;
                if (_mapCamera != null) _mapCamera.targetTexture = null;
                _mapCamera = null;
                Destroy(holder);
                RenderTexture.ReleaseTemporary(target);
                _mapRange = null; _mapTime = null;
                GridOn = grid; TrailsOn = trails; Hidden = hidden;
                _built = "";
                Rebuild();
            }
            string directory = Path.Combine(Paths.ConfigPath, "cameraTools", "maps");
            Directory.CreateDirectory(directory);
            string name = Path.GetFileNameWithoutExtension(player.FilePath ?? "replay");
            string suffix = range.HasValue ? $"{range.Value.Item1:F0}-{range.Value.Item2:F0}" : time.HasValue ? $"t{time.Value:F1}" : "all";
            string png = Path.Combine(directory, $"{name}-map-{suffix}.png");
            File.WriteAllBytes(png, image.EncodeToPNG());
            Destroy(image);
            float minX = center.x - size / 2f, maxZ = center.z + size / 2f;
            Dictionary<string, object> sidecar = new()
            {
                ["image"] = Path.GetFileName(png),
                ["pixels"] = pixels,
                ["sizeMeters"] = Math.Round(size, 3),
                ["metersPerPixel"] = Math.Round(size / pixels, 5),
                ["center"] = new[] { Math.Round(center.x, 3), Math.Round(center.z, 3) },
                ["worldToPixel"] = $"px = (x - {minX:F3}) / {size / pixels:F5}; py = ({maxZ:F3} - z) / {size / pixels:F5}; pixel (0,0) is the top-left (north-west) corner",
                ["minX"] = Math.Round(minX, 3), ["maxZ"] = Math.Round(maxZ, 3),
                ["grid"] = new Dictionary<string, object>
                {
                    ["southWest"] = new[] { Math.Round(origin.x, 3), Math.Round(origin.z, 3) }, ["cellMeters"] = Major,
                    ["columns"] = columns, ["rows"] = rows, ["naming"] = "letters west to east from A, numbers south to north from 1"
                },
                ["time"] = time.HasValue ? Math.Round(time.Value, 3) : null!,
                ["range"] = range.HasValue ? new[] { Math.Round(range.Value.Item1, 3), Math.Round(range.Value.Item2, 3) } : null!,
                ["actors"] = _trails.Select(t => new Dictionary<string, object>
                {
                    ["id"] = t.Track.Key, ["prefab"] = player.File.Info.PrefabName(t.Track.Prefab),
                    ["color"] = "#" + ColorUtility.ToHtmlStringRGB(t.Color)
                }).ToList()
            };
            File.WriteAllText(Path.ChangeExtension(png, ".json"), JsonConvert.SerializeObject(sidecar, Formatting.Indented));
            return $"map={png} sidecar={Path.ChangeExtension(png, ".json")} sizeMeters={F(size)} pixels={pixels}";
        }

        /// <summary>
        /// Writes surface heights every meter over the fight area plus a margin, and every solid object's bounds,
        /// so an agent can choose camera positions with clearance before checking them in the game.
        /// </summary>
        internal string SceneExport(float margin)
        {
            ReplayPlayer player = ReplayPlayer.Instance;
            (Vector3 origin, int columns, int rows) = Layout(player);
            float minX = origin.x - margin, minZ = origin.z - margin;
            float maxX = origin.x + columns * Major + margin, maxZ = origin.z + rows * Major + margin;
            int width = Mathf.RoundToInt(maxX - minX) + 1, depth = Mathf.RoundToInt(maxZ - minZ) + 1;
            int terrainMask = LayerMask.GetMask("terrain");
            List<double?[]> surface = new(), terrain = new();
            for (int row = 0; row < depth; row++)
            {
                double?[] top = new double?[width], ground = new double?[width];
                for (int column = 0; column < width; column++)
                {
                    Vector3 from = new(minX + column, 2000f, minZ + row);
                    top[column] = Physics.Raycast(from, Vector3.down, out RaycastHit hit, 4000f, SurfaceMask, QueryTriggerInteraction.Ignore) ? Math.Round(hit.point.y, 2) : null;
                    ground[column] = Physics.Raycast(from, Vector3.down, out RaycastHit floor, 4000f, terrainMask, QueryTriggerInteraction.Ignore) ? Math.Round(floor.point.y, 2) : null;
                }
                surface.Add(top); terrain.Add(ground);
            }
            Vector3 boxCenter = new((minX + maxX) / 2f, origin.y, (minZ + maxZ) / 2f);
            Collider[] colliders = Physics.OverlapBox(boxCenter, new Vector3((maxX - minX) / 2f, 500f, (maxZ - minZ) / 2f), Quaternion.identity,
                LayerMask.GetMask("piece", "static_solid", "Default", "Default_small", "vehicle"), QueryTriggerInteraction.Ignore);
            Dictionary<GameObject, Bounds> objects = new();
            foreach (Collider collider in colliders)
            {
                if (collider.GetComponentInParent<Character>() != null) continue;
                ZNetView? view = collider.GetComponentInParent<ZNetView>();
                GameObject root = view != null ? view.gameObject : collider.transform.root.gameObject;
                if (objects.TryGetValue(root, out Bounds bounds)) { bounds.Encapsulate(collider.bounds); objects[root] = bounds; }
                else objects[root] = collider.bounds;
            }
            List<Dictionary<string, object>> obstacles = new();
            foreach (KeyValuePair<GameObject, Bounds> entry in objects)
            {
                Bounds bounds = entry.Value;
                if (Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z) < 0.5f) continue;
                obstacles.Add(new Dictionary<string, object>
                {
                    ["prefab"] = Utils.GetPrefabName(entry.Key),
                    ["min"] = new[] { Math.Round(bounds.min.x, 2), Math.Round(bounds.min.y, 2), Math.Round(bounds.min.z, 2) },
                    ["max"] = new[] { Math.Round(bounds.max.x, 2), Math.Round(bounds.max.y, 2), Math.Round(bounds.max.z, 2) },
                    ["top"] = Math.Round(bounds.max.y, 2)
                });
            }
            string path = Path.ChangeExtension(player.FilePath ?? throw new InvalidOperationException("This replay was not opened from a file"), ".scene.json");
            Dictionary<string, object> document = new()
            {
                ["replay"] = Path.GetFileNameWithoutExtension(path),
                ["spacingMeters"] = 1,
                ["origin"] = new[] { Math.Round(minX, 3), Math.Round(minZ, 3) },
                ["columns"] = width, ["rows"] = depth,
                ["layout"] = "heights[row][column] is the surface at x = origin[0] + column, z = origin[1] + row. null where nothing was hit",
                ["heights"] = surface,
                ["heightsDescription"] = "top surface: terrain, building pieces, rocks, trees and other solid objects",
                ["terrainHeights"] = terrain,
                ["obstacles"] = obstacles,
                ["obstaclesDescription"] = "each solid world object's combined collider bounds (characters excluded); top is the highest point"
            };
            File.WriteAllText(path, JsonConvert.SerializeObject(document, Formatting.None));
            return $"scene={path} columns={width} rows={depth} obstacles={obstacles.Count}";
        }

        private static string F(float value) => value.ToString("F2", CultureInfo.InvariantCulture);
    }
}
