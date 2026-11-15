using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;

namespace cameraTools.Replay.Shots
{
    /// <summary>
    /// Preview stills at a fixed size, whatever the screen size: the game's sky and main cameras render into a texture of
    /// the requested size. A second copy carries the framing overlay: thirds lines, a 10% margin band, each measured
    /// actor's projected box and head box with its name and measurements.
    /// </summary>
    internal static class FramingStill
    {
        private const int LabelLayer = 31;
        private static Material? _lines;
        private static TMP_FontAsset? _font;

        private static Material Lines
        {
            get
            {
                if (_lines != null) return _lines;
                _lines = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave };
                _lines.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                _lines.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                _lines.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                _lines.SetInt("_ZWrite", 0);
                _lines.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                return _lines;
            }
        }

        internal static readonly Color SubjectColor = new(1f, 0.85f, 0.2f), PartnerColor = new(0.3f, 0.85f, 1f), KeepColor = new(0.85f, 0.85f, 0.85f);

        internal static Color RoleColor(string role) => role == "subject" ? SubjectColor : role == "partner" ? PartnerColor : KeepColor;

        /// <summary>Renders the game picture into a texture of the given size: the sky camera first, then the main camera.</summary>
        internal static Texture2D Capture(int width, int height, out RenderTexture target)
        {
            target = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
            GameCamera game = GameCamera.m_instance;
            Camera main = game.m_camera;
            Camera? sky = game.m_skyCamera;
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = target;
            GL.Clear(true, true, Color.black);
            RenderTexture.active = previous;
            try
            {
                foreach (Camera camera in new[] { sky, main })
                {
                    if (camera == null) continue;
                    RenderTexture? old = camera.targetTexture;
                    camera.targetTexture = target;
                    camera.ResetAspect();
                    camera.Render();
                    camera.targetTexture = old;
                    camera.ResetAspect();
                }
            }
            catch (Exception error) { cameraToolsPlugin.TemplateLogger.LogWarning($"[Shots] Still render failed: {error.Message}"); }
            return Read(target);
        }

        internal static Texture2D Read(RenderTexture target)
        {
            Texture2D image = new(target.width, target.height, TextureFormat.RGB24, false);
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = target;
            image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
            image.Apply(false);
            RenderTexture.active = previous;
            return image;
        }

        /// <summary>Draws the overlay onto the still's render texture and returns the result.</summary>
        internal static Texture2D Overlay(RenderTexture target, FrameMeasure frame, string header, Dictionary<string, float> brightness)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = target;
            GL.PushMatrix();
            GL.LoadOrtho();
            Lines.SetPass(0);
            // The 10% margin band.
            GL.Begin(GL.QUADS);
            GL.Color(new Color(0f, 0f, 0f, 0.28f));
            Quad(0f, 0f, 1f, 0.1f); Quad(0f, 0.9f, 1f, 1f); Quad(0f, 0.1f, 0.1f, 0.9f); Quad(0.9f, 0.1f, 1f, 0.9f);
            GL.End();
            GL.Begin(GL.LINES);
            GL.Color(new Color(1f, 1f, 1f, 0.55f));
            foreach (float third in new[] { 1f / 3f, 2f / 3f }) { Line(third, 0f, third, 1f); Line(0f, third, 1f, third); }
            foreach (ActorFrame actor in frame.Actors)
            {
                Color color = RoleColor(actor.Role);
                GL.Color(color);
                Rectangle(actor.Box);
                Rectangle(new Rect(actor.Box.xMin + 0.002f, actor.Box.yMin + 0.002f, actor.Box.width - 0.004f, actor.Box.height - 0.004f));
                GL.Color(new Color(color.r, color.g, color.b, 0.8f));
                Rectangle(actor.HeadBox);
                // A tick on the side the actor leads toward.
                if (actor.Lead.HasValue) { float y = actor.Box.center.y; Line(actor.Center.x, y, actor.Center.x + (actor.Lead > 0.5f ? 0.04f : -0.04f), y); }
            }
            GL.End();
            GL.PopMatrix();
            RenderTexture.active = previous;

            List<(string Text, Vector2 At, Color Color)> labels = new() { (header, new Vector2(0.012f, 0.985f), Color.white) };
            foreach (ActorFrame actor in frame.Actors)
            {
                string lead = actor.Lead.HasValue ? $" lead {actor.Lead:F2}" : "";
                string light = brightness.TryGetValue(actor.Id, out float stops) ? $" sep {stops:+0.0;-0.0}st" : "";
                labels.Add(($"{actor.Name} {actor.Role}", new Vector2(Mathf.Clamp(actor.HeadBox.xMin, 0.01f, 0.8f), Mathf.Clamp(actor.Box.yMax + 0.045f, 0.06f, 0.99f)), RoleColor(actor.Role)));
                labels.Add(($"size {actor.Size:F2} vis {actor.Visible:F2}{lead}{light}", new Vector2(Mathf.Clamp(actor.Box.xMin, 0.01f, 0.7f), Mathf.Clamp(actor.Box.yMin - 0.01f, 0.05f, 0.95f)), RoleColor(actor.Role)));
            }
            Labels(target, labels);
            return Read(target);
        }

        private static void Quad(float x0, float y0, float x1, float y1)
        {
            GL.Vertex3(x0, y0, 0f); GL.Vertex3(x1, y0, 0f); GL.Vertex3(x1, y1, 0f); GL.Vertex3(x0, y1, 0f);
        }

        private static void Line(float x0, float y0, float x1, float y1)
        {
            GL.Vertex3(x0, y0, 0f); GL.Vertex3(x1, y1, 0f);
        }

        private static void Rectangle(Rect box)
        {
            if (box.width <= 0f) return;
            Line(box.xMin, box.yMin, box.xMax, box.yMin); Line(box.xMax, box.yMin, box.xMax, box.yMax);
            Line(box.xMax, box.yMax, box.xMin, box.yMax); Line(box.xMin, box.yMax, box.xMin, box.yMin);
        }

        /// <summary>Text drawn by an orthographic camera far from the world that sees only the label layer.</summary>
        private static void Labels(RenderTexture target, List<(string Text, Vector2 At, Color Color)> labels)
        {
            _font ??= Resources.FindObjectsOfTypeAll<TMP_FontAsset>().OrderByDescending(font => font.name.Contains("Averia")).FirstOrDefault();
            float aspect = target.width / (float)target.height;
            Vector3 origin = new(0f, -3000f, 0f);
            GameObject holder = new("cameraTools still labels");
            List<GameObject> created = new() { holder };
            try
            {
                holder.transform.position = origin;
                Camera camera = holder.AddComponent<Camera>();
                camera.enabled = false;
                camera.orthographic = true;
                camera.orthographicSize = 0.5f;
                camera.nearClipPlane = 0.01f;
                camera.farClipPlane = 10f;
                camera.clearFlags = CameraClearFlags.Nothing;
                camera.cullingMask = 1 << LabelLayer;
                camera.targetTexture = target;
                camera.aspect = aspect;
                foreach ((string text, Vector2 at, Color color) in labels)
                {
                    GameObject item = new("cameraTools still label") { layer = LabelLayer };
                    created.Add(item);
                    item.transform.position = origin + new Vector3((at.x - 0.5f) * aspect, at.y - 0.5f, 1f);
                    TextMeshPro label = item.AddComponent<TextMeshPro>();
                    if (_font != null) label.font = _font;
                    label.text = text;
                    label.fontSize = 0.3f;
                    label.color = color;
                    label.enableWordWrapping = false;
                    label.alignment = TextAlignmentOptions.TopLeft;
                    label.rectTransform.pivot = new Vector2(0f, 1f);
                    label.rectTransform.sizeDelta = new Vector2(2f, 0.05f);
                    label.outlineWidth = 0.25f;
                    label.outlineColor = new Color32(0, 0, 0, 255);
                    label.ForceMeshUpdate();
                }
                camera.Render();
                camera.targetTexture = null;
            }
            finally
            {
                foreach (GameObject item in created) UnityEngine.Object.Destroy(item);
            }
        }

        /// <summary>
        /// How much brighter (positive) or darker each actor is than what is directly behind it, in stops: the mean
        /// brightness of its visible pixels against the rest of its box grown by half.
        /// </summary>
        internal static Dictionary<string, float> Brightness(Texture2D still, FrameMeasure frame, PixelPass pixels, ReplayPlayer player)
        {
            Dictionary<string, float> result = new();
            Color32[] colors = still.GetPixels32();
            foreach (ActorFrame actor in frame.Actors)
            {
                (long user, uint id) = CommandText.Actor(actor.Id);
                GameObject? shown = player.FindTarget(user, id);
                List<GameObject?> others = frame.Actors.Where(a => a != actor).Select(a => { (long u, uint i) = CommandText.Actor(a.Id); return player.FindTarget(u, i); }).ToList();
                bool[]? mask = pixels.Mask(frame.Camera, frame.Rotation, frame.Fov, shown, others);
                if (mask == null) continue;
                Rect grown = new(actor.Box.center.x - actor.Box.width * 0.75f, actor.Box.center.y - actor.Box.height * 0.75f, actor.Box.width * 1.5f, actor.Box.height * 1.5f);
                double subject = 0, background = 0; int subjectCount = 0, backgroundCount = 0;
                for (int y = 0; y < still.height; y++)
                {
                    float fy = (y + 0.5f) / still.height;
                    if (fy < grown.yMin || fy > grown.yMax) continue;
                    int my = Mathf.Clamp((int)(fy * pixels.Height), 0, pixels.Height - 1);
                    for (int x = 0; x < still.width; x++)
                    {
                        float fx = (x + 0.5f) / still.width;
                        if (fx < grown.xMin || fx > grown.xMax) continue;
                        int mx = Mathf.Clamp((int)(fx * pixels.Width), 0, pixels.Width - 1);
                        Color32 c = colors[y * still.width + x];
                        double luminance = (0.2126 * c.r + 0.7152 * c.g + 0.0722 * c.b) / 255.0;
                        if (mask[my * pixels.Width + mx]) { subject += luminance; subjectCount++; }
                        else { background += luminance; backgroundCount++; }
                    }
                }
                if (subjectCount == 0 || backgroundCount == 0) continue;
                double a = Math.Max(subject / subjectCount, 0.002), b = Math.Max(background / backgroundCount, 0.002);
                result[actor.Id] = (float)Math.Log(a / b, 2);
            }
            return result;
        }

        internal static void Write(string path, Texture2D image) => File.WriteAllBytes(path, image.EncodeToPNG());
    }
}
