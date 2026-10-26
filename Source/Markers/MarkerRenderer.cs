using System.Collections.Generic;
using UnityEngine;

namespace cameraTools.Markers
{
    public class MarkerRenderer : MonoBehaviour
    {
        private static Material? _lineMaterial;
        private const float LabelRange = 160f;
        private GUIStyle? _labelStyle;

        public static readonly Color AreaColor = new(0.25f, 0.9f, 0.9f, 1f);
        public static readonly Color PointColor = new(1f, 0.8f, 0.2f, 1f);
        public static readonly Color ShotColor = new(1f, 0.35f, 0.8f, 1f);
        public static readonly Color PathColor = new(0.45f, 1f, 0.45f, 1f);
        public static readonly Color PreviewColor = new(1f, 1f, 1f, 0.8f);
        public static readonly Color HoverColor = Color.white;

        private void OnEnable()
        {
            Camera.onPostRender += Draw;
        }

        private void OnDisable()
        {
            Camera.onPostRender -= Draw;
        }

        private static Material LineMaterial
        {
            get
            {
                if (_lineMaterial == null)
                {
                    Shader shader = Shader.Find("Hidden/Internal-Colored");
                    _lineMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                    _lineMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                    _lineMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    _lineMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                    _lineMaterial.SetInt("_ZWrite", 0);
                }

                return _lineMaterial;
            }
        }

        private static bool IsGameCamera(Camera cam)
        {
            return GameCamera.instance != null && cam.gameObject == GameCamera.instance.gameObject;
        }

        public static Color ColorFor(MarkerKind kind)
        {
            switch (kind)
            {
                case MarkerKind.Area: return AreaColor;
                case MarkerKind.Shot: return ShotColor;
                case MarkerKind.Path: return PathColor;
                default: return PointColor;
            }
        }

        private void Draw(Camera cam)
        {
            if (!IsGameCamera(cam) || Player.m_localPlayer == null)
            {
                return;
            }

            bool showStored = MarkerVisibility.Visible;
            bool showPreview = MarkerMode.Placing;
            if (!showStored && !showPreview)
            {
                return;
            }

            LineMaterial.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.LessEqual);
            LineMaterial.SetPass(0);
            GL.PushMatrix();
            GL.MultMatrix(Matrix4x4.identity);
            GL.Begin(GL.LINES);
            DrawAll(showStored, showPreview, 1f);
            GL.End();

            // Second pass: faint lines through walls so hidden markers still read.
            LineMaterial.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Greater);
            LineMaterial.SetPass(0);
            GL.Begin(GL.LINES);
            DrawAll(showStored, showPreview, 0.18f);
            GL.End();
            GL.PopMatrix();
        }

        private void DrawAll(bool showStored, bool showPreview, float alpha)
        {
            if (showStored)
            {
                MarkerStore.EnsureLoaded();
                Marker? hovered = MarkerMode.Hovered;
                foreach (Marker marker in MarkerStore.Markers)
                {
                    Color color = marker == hovered ? HoverColor : ColorFor(marker.Kind);
                    if (MarkerMode.Highlighted == marker)
                    {
                        color = Color.Lerp(color, Color.white, 0.5f + 0.5f * Mathf.Sin(Time.time * 6f));
                    }

                    color.a = alpha;
                    DrawMarker(marker, color, marker == hovered ? MarkerMode.HoveredWaypoint : -1);
                }
            }

            if (showPreview)
            {
                MarkerMode.DrawPreview(alpha);
            }
        }

        public static void DrawMarker(Marker marker, Color color, int hoveredWaypoint)
        {
            switch (marker.Kind)
            {
                case MarkerKind.Area:
                    break; // drawn by AreaRulerManager with the game's own radius-marker bars
                case MarkerKind.Point:
                    DrawPost(marker.position, color, 1.5f);
                    break;
                case MarkerKind.Shot:
                    DrawShot(marker.cameraPosition, marker.lookDirection, color);
                    break;
                case MarkerKind.Path:
                    DrawPath(marker, color, hoveredWaypoint);
                    break;
            }
        }

        public static void DrawPost(Vector3 basePos, Color color, float height)
        {
            Vector3 top = basePos + Vector3.up * height;
            Line(basePos, top, color);
            DrawDiamond(top, color, 0.25f);
            DrawCross(basePos + Vector3.up * 0.05f, color, 0.4f);
        }

        public static void DrawDiamond(Vector3 center, Color color, float size)
        {
            Vector3 u = Vector3.up * size;
            Vector3 x = Vector3.right * size;
            Vector3 z = Vector3.forward * size;
            Line(center + u, center + x, color); Line(center + x, center - u, color);
            Line(center - u, center - x, color); Line(center - x, center + u, color);
            Line(center + u, center + z, color); Line(center + z, center - u, color);
            Line(center - u, center - z, color); Line(center - z, center + u, color);
        }

        public static void DrawCross(Vector3 center, Color color, float size)
        {
            Line(center + Vector3.right * size, center - Vector3.right * size, color);
            Line(center + Vector3.forward * size, center - Vector3.forward * size, color);
        }

        public static void DrawShot(Vector3 position, Vector3 direction, Color color)
        {
            if (direction.sqrMagnitude < 0.0001f)
            {
                direction = Vector3.forward;
            }

            Quaternion rot = Quaternion.LookRotation(direction.normalized, Vector3.up);
            float depth = 1.6f;
            float halfH = depth * Mathf.Tan(65f * 0.5f * Mathf.Deg2Rad);
            float halfW = halfH * 9f / 16f;
            Vector3 c = position + rot * Vector3.forward * depth;
            Vector3 r = rot * Vector3.right * halfW;
            Vector3 u = rot * Vector3.up * halfH;
            Vector3[] corners = { c - r - u, c + r - u, c + r + u, c - r + u };
            for (int i = 0; i < 4; i++)
            {
                Line(position, corners[i], color);
                Line(corners[i], corners[(i + 1) % 4], color);
            }

            Color faint = color;
            faint.a *= 0.5f;
            Line(c, position + rot * Vector3.forward * 6f, faint);
            DrawDiamond(position, color, 0.15f);
        }

        public static void DrawPath(Marker marker, Color color, int hoveredWaypoint)
        {
            if (marker.CachedSpline == null)
            {
                marker.CachedSpline = PathPlayer.SampleSpline(marker.waypoints, 10);
            }

            DrawPolyline(marker.CachedSpline, color);
            DrawWaypoints(marker.waypoints, color, hoveredWaypoint);
        }

        public static void DrawPolyline(List<Vector3> points, Color color)
        {
            for (int i = 1; i < points.Count; i++)
            {
                Line(points[i - 1], points[i], color);
            }
        }

        public static void DrawWaypoints(List<Waypoint> waypoints, Color color, int hoveredWaypoint)
        {
            Color faint = color;
            faint.a *= 0.6f;
            for (int i = 0; i < waypoints.Count; i++)
            {
                Waypoint wp = waypoints[i];
                Color c = i == hoveredWaypoint ? HoverColor : color;
                DrawDiamond(wp.position, c, i == hoveredWaypoint ? 0.3f : 0.18f);
                if (wp.forward.sqrMagnitude > 0.0001f)
                {
                    Line(wp.position, wp.position + wp.forward.normalized * 1.2f, faint);
                }

                if (wp.hold > 0.01f)
                {
                    DrawCross(wp.position + Vector3.up * 0.35f, c, 0.2f);
                }
            }
        }

        public static void Line(Vector3 a, Vector3 b, Color color)
        {
            GL.Color(color);
            GL.Vertex(a);
            GL.Vertex(b);
        }

        private void OnGUI()
        {
            if (Player.m_localPlayer == null || GameCamera.instance == null)
            {
                return;
            }

            if (!MarkerVisibility.Visible)
            {
                return;
            }

            _labelStyle ??= new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 13,
                wordWrap = false,
                normal = { textColor = Color.white }
            };

            Camera cam = GameCamera.instance.GetComponent<Camera>();
            if (cam == null)
            {
                return;
            }

            Marker? hovered = MarkerMode.Hovered;
            foreach (Marker marker in MarkerStore.Markers)
            {
                Vector3 anchor = marker.Anchor;
                if (marker.Kind == MarkerKind.Point)
                {
                    anchor = marker.position + Vector3.up * 1.9f;
                }

                if (Vector3.Distance(anchor, cam.transform.position) > LabelRange)
                {
                    continue;
                }

                Vector3 screen = cam.WorldToScreenPoint(anchor);
                if (screen.z <= 0f)
                {
                    continue;
                }

                string text = marker.label;
                if (marker.Kind == MarkerKind.Area && !string.IsNullOrEmpty(marker.tag))
                {
                    text += $" [{marker.tag}]";
                }

                if (marker == hovered && !string.IsNullOrEmpty(marker.note))
                {
                    text += "\n" + marker.note;
                }

                Color color = marker == hovered ? Color.white : ColorFor(marker.Kind);
                GUIContent content = new(text);
                Vector2 size = _labelStyle.CalcSize(content);
                size.x += 10f;
                size.y += 4f;
                float x = screen.x - size.x / 2f;
                float y = Screen.height - screen.y - size.y - 6f;
                Color prev = GUI.color;
                GUI.color = color;
                GUI.Label(new Rect(x, y, size.x, size.y), content, _labelStyle);
                GUI.color = prev;
            }
        }
    }
}
