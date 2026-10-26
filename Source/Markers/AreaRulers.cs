using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace cameraTools.Markers
{
    /// <summary>
    /// Draws area shapes the way Infinity Hammer does: pooled clones of the vanilla workbench radius-marker bar
    /// (the <c>Circle_section</c> prefab the workbench and wards lay on the ground), placed along the shape's edge,
    /// each one dropped onto the surface below it. No lines, no custom meshes, so it reads as part of the game.
    /// </summary>
    public abstract class AreaRuler : MonoBehaviour
    {
        private static readonly int GroundMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain");
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static GameObject? _segmentPrefab;
        private static MaterialPropertyBlock? _block;

        public static bool Available => SegmentPrefab != null;

        private static GameObject? SegmentPrefab
        {
            get
            {
                if (_segmentPrefab == null && ZNetScene.instance != null)
                {
                    GameObject? workbench = ZNetScene.instance.GetPrefab("piece_workbench");
                    CircleProjector? projector = workbench != null ? workbench.GetComponentInChildren<CircleProjector>(true) : null;
                    _segmentPrefab = projector != null ? projector.m_prefab : null;
                }

                return _segmentPrefab;
            }
        }

        protected static Vector3 BaseScale => SegmentPrefab != null ? SegmentPrefab.transform.localScale : new Vector3(0.15f, 0.1f, 1f);

        /// <summary>Drop every bar onto the floor or ground below it (a short ray, so upper floors keep their height).</summary>
        public bool SnapToGround = true;

        /// <summary>Slide the bars along the edge over time like the vanilla workbench circle.</summary>
        public bool Animate = true;

        /// <summary>Optional colour; null keeps the vanilla grey.</summary>
        public Color? Tint;

        protected readonly List<GameObject> Segments = new();
        private GameObject? _center;
        private Color? _appliedTint;
        private bool _tintDirty = true;

        protected virtual bool HasCenter => true;

        protected float AnimationTime => Animate ? Time.time : 0f;

        public void Refresh()
        {
            if (!gameObject.activeInHierarchy)
            {
                return;
            }

            CreateLines();
            if (SnapToGround)
            {
                Snap();
            }

            ApplyTint();
        }

        protected abstract void CreateLines();

        private void OnDestroy()
        {
            foreach (GameObject segment in Segments)
            {
                Destroy(segment);
            }

            Segments.Clear();
            if (_center != null)
            {
                Destroy(_center);
                _center = null;
            }
        }

        protected void CreateSegments(int count)
        {
            if (Segments.Count == count)
            {
                return;
            }

            foreach (GameObject segment in Segments)
            {
                Destroy(segment);
            }

            Segments.Clear();
            if (_center != null)
            {
                Destroy(_center);
                _center = null;
            }

            if (count == 0)
            {
                return;
            }

            if (HasCenter)
            {
                _center = CreateMarker();
                _center.transform.localPosition = Vector3.zero;
                _center.transform.localRotation = Quaternion.identity;
                _center.transform.localScale = new Vector3(0.1f, 0.1f, 0.1f);
            }

            for (int i = 0; i < count; i++)
            {
                Segments.Add(CreateMarker());
            }

            _tintDirty = true;
        }

        private GameObject CreateMarker()
        {
            GameObject marker = Instantiate(SegmentPrefab!, transform);
            marker.SetActive(true);
            foreach (Renderer renderer in marker.GetComponentsInChildren<Renderer>(true))
            {
                renderer.gameObject.SetActive(true);
                renderer.enabled = true;
            }

            return marker;
        }

        private void Snap()
        {
            foreach (GameObject segment in Segments)
            {
                Vector3 pos = segment.transform.position;
                if (Physics.Raycast(pos + Vector3.up * 1.5f, Vector3.down, out RaycastHit hit, 12f, GroundMask))
                {
                    pos.y = hit.point.y + 0.05f;
                    segment.transform.position = pos;
                }
            }
        }

        private void ApplyTint()
        {
            if (!_tintDirty && _appliedTint == Tint)
            {
                return;
            }

            _block ??= new MaterialPropertyBlock();
            foreach (Renderer renderer in GetComponentsInChildren<Renderer>(true))
            {
                if (Tint.HasValue)
                {
                    _block.Clear();
                    _block.SetColor(ColorId, Tint.Value);
                    renderer.SetPropertyBlock(_block);
                }
                else
                {
                    renderer.SetPropertyBlock(null);
                }
            }

            _appliedTint = Tint;
            _tintDirty = false;
        }

        protected Transform Get(int index) => Segments[index].transform;

        protected void Set(int index, Vector3 pos) => Get(index).localPosition = pos;

        protected void SetRot(int index, Vector3 direction) => Get(index).localRotation = Quaternion.LookRotation(direction, Vector3.up);
    }

    public class CircleRuler : AreaRuler
    {
        public float Radius = 6f;
        private const float Speed = 0.1f;

        protected override void CreateLines()
        {
            int count = Math.Max(3, (int)(Radius * 4f));
            CreateSegments(count);
            float angle = Mathf.PI * 2f / count;
            float time = AnimationTime * Speed;
            for (int i = 0; i < count; i++)
            {
                float f = i * angle + time;
                Set(i, new Vector3(Mathf.Sin(f) * Radius, 0f, Mathf.Cos(f) * Radius));
            }

            for (int i = 0; i < count; i++)
            {
                Transform prev = Get(i == 0 ? count - 1 : i - 1);
                Transform next = Get(i == count - 1 ? 0 : i + 1);
                SetRot(i, (prev.localPosition - next.localPosition).normalized);
            }
        }
    }

    /// <summary>Width and Depth are half-extents, as in Infinity Hammer: the rectangle spans -Width..+Width and -Depth..+Depth.</summary>
    public class RectangleRuler : AreaRuler
    {
        public float Width = 5f;
        public float Depth = 5f;

        protected override void CreateLines()
        {
            float totalLength = 2f * Width + 2f * Depth;
            int count = Math.Max(3, (int)totalLength);
            int d = (int)Mathf.Max(2f, Mathf.Ceil(count * Depth / totalLength));
            int w = (int)Mathf.Max(2f, Mathf.Ceil(count * Width / totalLength));
            count = 2 * (d + w);
            CreateSegments(count);

            int index = 0;
            for (int i = 0; i < d; i++, index++) SetRot(index, Vector3.forward);
            for (int i = 0; i < w; i++, index++) SetRot(index, Vector3.right);
            for (int i = 0; i < d; i++, index++) SetRot(index, Vector3.back);
            for (int i = 0; i < w; i++, index++) SetRot(index, Vector3.left);

            index = 0;
            float baseTime = AnimationTime * 0.025f * (count - 4);
            const float halfLine = 0.5f;
            const float start = 0.5f;

            Edge(ref index, d, Width * Vector3.left - (Depth + halfLine) * Vector3.forward, Vector3.forward, Depth, start, baseTime);
            Edge(ref index, w, Depth * Vector3.forward - (Width + halfLine) * Vector3.right, Vector3.right, Width, start, baseTime);
            Edge(ref index, d, Width * Vector3.right - (Depth + halfLine) * Vector3.back, Vector3.back, Depth, start, baseTime);
            Edge(ref index, w, Depth * Vector3.back - (Width + halfLine) * Vector3.left, Vector3.left, Width, start, baseTime);
        }

        private void Edge(ref int index, int bars, Vector3 basePos, Vector3 direction, float halfExtent, float start, float baseTime)
        {
            float end = start + 2f * halfExtent;
            float size = 2f * halfExtent * bars / (bars - 1);
            float time = baseTime / bars;
            for (int i = 0; i < bars; i++, index++)
            {
                float percent = ((float)i / bars + time) % 1f;
                Set(index, basePos + percent * size * direction);
                EdgeFix(index, percent, size, start, end, direction);
            }
        }

        private void EdgeFix(int index, float percent, float max, float start, float end, Vector3 direction)
        {
            float pos = percent * max;
            Transform tr = Get(index);
            Vector3 scale = BaseScale;
            if (pos - start < 0.5f)
            {
                scale.z *= pos - start + 0.5f;
                tr.localPosition += direction * (0.5f - scale.z / 2f);
            }

            if (end - pos < 0.5f)
            {
                scale.z *= Mathf.Max(0f, end - pos + 0.5f);
                tr.localPosition -= direction * (0.5f - scale.z / 2f);
            }

            tr.gameObject.SetActive(scale.z > 0f);
            tr.localScale = scale;
        }
    }

    /// <summary>Bars laid along each edge of a world-space polygon; the root sits at the origin.</summary>
    public class PolygonRuler : AreaRuler
    {
        public List<Vector3> Vertices = new();

        protected override bool HasCenter => false;

        protected override void CreateLines()
        {
            int n = Vertices.Count;
            if (n < 2)
            {
                CreateSegments(0);
                return;
            }

            List<(Vector3 pos, Vector3 dir, float length)> bars = new();
            for (int v = 0; v < n; v++)
            {
                Vector3 a = Vertices[v];
                Vector3 b = Vertices[(v + 1) % n];
                Vector3 edge = b - a;
                float length = edge.magnitude;
                if (length < 0.05f)
                {
                    continue;
                }

                Vector3 dir = edge / length;
                int count = Math.Max(1, Mathf.RoundToInt(length / 1.5f));
                float spacing = length / count;
                float barLength = Mathf.Min(1f, spacing * 0.7f);
                for (int i = 0; i < count; i++)
                {
                    bars.Add((a + dir * (spacing * (i + 0.5f)), dir, barLength));
                }
            }

            CreateSegments(bars.Count);
            for (int i = 0; i < bars.Count; i++)
            {
                (Vector3 pos, Vector3 dir, float length) = bars[i];
                Transform tr = Get(i);
                tr.localPosition = pos;
                Vector3 flat = new(dir.x, 0f, dir.z);
                tr.localRotation = Quaternion.LookRotation(flat.sqrMagnitude > 0.0001f ? flat : Vector3.forward, Vector3.up);
                Vector3 scale = BaseScale;
                scale.z *= length;
                tr.localScale = scale;
            }
        }
    }

    /// <summary>Keeps one ruler per visible stored area and one for the area being placed.</summary>
    public class AreaRulerManager : MonoBehaviour
    {
        private readonly Dictionary<string, AreaRuler> _stored = new();
        private readonly List<string> _stale = new();
        private GameObject? _previewRoot;
        private CircleRuler? _previewCircle;
        private RectangleRuler? _previewRectangle;
        private static int _layer = -1;

        private static int Layer => _layer >= 0 ? _layer : _layer = LayerMask.NameToLayer("character_trigger");

        private void LateUpdate()
        {
            if (Player.m_localPlayer == null || !AreaRuler.Available || !MarkerVisibility.Visible)
            {
                ClearAll();
                return;
            }

            UpdateStored();
            UpdatePreview();
        }

        private void UpdateStored()
        {
            Marker? hovered = MarkerMode.Hovered;
            HashSet<string> seen = new();
            foreach (Marker marker in MarkerStore.Markers)
            {
                if (marker.Kind != MarkerKind.Area)
                {
                    continue;
                }

                seen.Add(marker.id);
                AreaRuler ruler = GetOrCreate(marker);
                Configure(ruler, marker);
                if (marker == hovered)
                {
                    ruler.Tint = Color.white;
                }
                else if (MarkerMode.Highlighted == marker)
                {
                    float pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * 6f);
                    ruler.Tint = Color.Lerp(new Color(0.8f, 0.8f, 0.8f), Color.white, pulse);
                }
                else
                {
                    ruler.Tint = null;
                }

                ruler.Refresh();
            }

            _stale.Clear();
            foreach (KeyValuePair<string, AreaRuler> pair in _stored)
            {
                if (!seen.Contains(pair.Key))
                {
                    _stale.Add(pair.Key);
                }
            }

            foreach (string id in _stale)
            {
                Destroy(_stored[id].gameObject);
                _stored.Remove(id);
            }
        }

        private AreaRuler GetOrCreate(Marker marker)
        {
            if (_stored.TryGetValue(marker.id, out AreaRuler existing) && existing != null && ShapeMatches(existing, marker.Shape))
            {
                return existing;
            }

            if (existing != null)
            {
                Destroy(existing.gameObject);
            }

            GameObject root = new($"MarkerArea_{marker.id}") { layer = Layer };
            AreaRuler ruler = marker.Shape switch
            {
                AreaShape.Polygon => root.AddComponent<PolygonRuler>(),
                AreaShape.Rectangle => root.AddComponent<RectangleRuler>(),
                _ => root.AddComponent<CircleRuler>()
            };
            _stored[marker.id] = ruler;
            return ruler;
        }

        private static bool ShapeMatches(AreaRuler ruler, AreaShape shape)
        {
            return shape switch
            {
                AreaShape.Polygon => ruler is PolygonRuler,
                AreaShape.Rectangle => ruler is RectangleRuler,
                _ => ruler is CircleRuler
            };
        }

        private static void Configure(AreaRuler ruler, Marker marker)
        {
            switch (ruler)
            {
                case PolygonRuler polygon:
                    polygon.transform.position = Vector3.zero;
                    polygon.transform.rotation = Quaternion.identity;
                    polygon.Vertices = marker.vertices;
                    break;
                case RectangleRuler rectangle:
                    rectangle.transform.position = marker.position;
                    rectangle.transform.rotation = Quaternion.Euler(0f, marker.rotation, 0f);
                    rectangle.Width = Mathf.Max(0.25f, marker.width / 2f);
                    rectangle.Depth = Mathf.Max(0.25f, marker.depth / 2f);
                    break;
                case CircleRuler circle:
                    circle.transform.position = marker.position;
                    circle.transform.rotation = Quaternion.identity;
                    circle.Radius = Mathf.Max(0.25f, marker.radius);
                    break;
            }
        }

        private void UpdatePreview()
        {
            if (!MarkerMode.PreviewArea(out AreaShape shape, out Vector3 center, out float radius, out float width, out float depth, out float rotation))
            {
                if (_previewRoot != null)
                {
                    _previewRoot.SetActive(false);
                }

                return;
            }

            if (_previewRoot == null)
            {
                _previewRoot = new GameObject("MarkerAreaPreview") { layer = Layer };
                GameObject circleGo = new("Circle") { layer = Layer };
                circleGo.transform.SetParent(_previewRoot.transform, false);
                _previewCircle = circleGo.AddComponent<CircleRuler>();
                GameObject rectGo = new("Rectangle") { layer = Layer };
                rectGo.transform.SetParent(_previewRoot.transform, false);
                _previewRectangle = rectGo.AddComponent<RectangleRuler>();
            }

            _previewRoot.SetActive(true);
            _previewRoot.transform.position = center;
            _previewRoot.transform.rotation = Quaternion.Euler(0f, rotation, 0f);
            bool circle = shape == AreaShape.Circle;
            _previewCircle!.gameObject.SetActive(circle);
            _previewRectangle!.gameObject.SetActive(!circle);
            if (circle)
            {
                _previewCircle.Radius = Mathf.Max(0.25f, radius);
                _previewCircle.Refresh();
            }
            else
            {
                _previewRectangle.Width = Mathf.Max(0.25f, width / 2f);
                _previewRectangle.Depth = Mathf.Max(0.25f, depth / 2f);
                _previewRectangle.Refresh();
            }
        }

        private void ClearAll()
        {
            foreach (AreaRuler ruler in _stored.Values)
            {
                if (ruler != null)
                {
                    Destroy(ruler.gameObject);
                }
            }

            _stored.Clear();
            if (_previewRoot != null)
            {
                Destroy(_previewRoot);
                _previewRoot = null;
                _previewCircle = null;
                _previewRectangle = null;
            }
        }

        private void OnDestroy() => ClearAll();
    }
}
