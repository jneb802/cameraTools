using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace cameraTools.Replay.Shots
{
    /// <summary>One actor counted in pixels.</summary>
    internal sealed class PixelActor
    {
        public string Id = "";
        /// <summary>Pixels of the actor that pass the depth test against the scene and the other actors.</summary>
        public int Visible;
        /// <summary>Pixels of the actor drawn alone, inside the frame.</summary>
        public int Alone;
        public float VisibleFraction;
        public float InFrame;
        public bool HeadCovered;
        public bool Present;
    }

    internal sealed class PixelSample
    {
        public float Time;
        public readonly List<PixelActor> Actors = new();
        public float Foreground;
    }

    /// <summary>
    /// Exact framing measurements by rendering a small buffer, as Ranon, Christie and Urli (2010) measure them: each
    /// measured actor is drawn in a flat colour over the scene's depth, so tree leaves and creature meshes count as
    /// drawn, not as capsules and colliders. The camera renders the scene, and a command buffer added after the opaque and
    /// transparent passes draws the actors with the same matrices.
    /// </summary>
    internal sealed class PixelPass : IDisposable
    {
        private const int HiddenLayer = 30;
        private readonly int _width, _height;
        private readonly RenderTexture _target;
        private readonly Texture2D _read;
        private readonly GameObject _holder;
        private readonly Camera _camera;
        private readonly Material _draw, _depthOnly, _farTest;
        private readonly Mesh _quad;

        internal PixelPass(int width = 256, int height = 144)
        {
            _width = width; _height = height;
            _target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1, name = "cameraTools pixel pass" };
            _target.Create();
            _read = new Texture2D(width, height, TextureFormat.RGBA32, false);
            _holder = new GameObject("cameraTools pixel camera");
            UnityEngine.Object.DontDestroyOnLoad(_holder);
            _camera = _holder.AddComponent<Camera>();
            _camera.enabled = false;
            Shader shader = Shader.Find("Hidden/Internal-Colored");
            _draw = Flat(shader, BlendMode.One, BlendMode.Zero, true, CompareFunction.LessEqual);
            _depthOnly = Flat(shader, BlendMode.Zero, BlendMode.One, true, CompareFunction.LessEqual);
            _farTest = Flat(shader, BlendMode.One, BlendMode.Zero, false, CompareFunction.Greater);
            _quad = new Mesh();
            _quad.SetVertices(new List<Vector3> { new(-0.5f, -0.5f, 0f), new(0.5f, -0.5f, 0f), new(0.5f, 0.5f, 0f), new(-0.5f, 0.5f, 0f) });
            _quad.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
        }

        private static Material Flat(Shader shader, BlendMode source, BlendMode destination, bool zWrite, CompareFunction test)
        {
            Material material = new(shader) { hideFlags = HideFlags.HideAndDontSave };
            material.SetColor("_Color", Color.white);
            material.SetInt("_SrcBlend", (int)source);
            material.SetInt("_DstBlend", (int)destination);
            material.SetInt("_ZWrite", zWrite ? 1 : 0);
            material.SetInt("_ZTest", (int)test);
            material.SetInt("_Cull", (int)CullMode.Off);
            return material;
        }

        public void Dispose()
        {
            _target.Release();
            UnityEngine.Object.Destroy(_target);
            UnityEngine.Object.Destroy(_read);
            UnityEngine.Object.Destroy(_holder);
            UnityEngine.Object.Destroy(_draw);
            UnityEngine.Object.Destroy(_depthOnly);
            UnityEngine.Object.Destroy(_farTest);
            UnityEngine.Object.Destroy(_quad);
        }

        /// <summary>The drawn parts of a recorded actor: its mesh renderers, including carried equipment.</summary>
        internal static List<Renderer> Renderers(GameObject? actor)
        {
            if (actor == null) return new List<Renderer>();
            return actor.GetComponentsInChildren<Renderer>().Where(r => r.enabled && r.gameObject.activeInHierarchy && (r is MeshRenderer || r is SkinnedMeshRenderer)).ToList();
        }

        private void Setup(Vector3 position, Quaternion rotation, float fov)
        {
            Camera main = Utils.GetMainCamera();
            if (main != null) _camera.CopyFrom(main);
            _camera.enabled = false;
            _camera.targetTexture = _target;
            _camera.allowHDR = false;
            _camera.allowMSAA = false;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = Color.clear;
            _camera.transform.SetPositionAndRotation(position, rotation);
            _camera.fieldOfView = fov;
            _camera.ResetAspect();
            _camera.ResetProjectionMatrix();
            _camera.RemoveAllCommandBuffers();
        }

        private void Draw(CommandBuffer buffer, List<Renderer> renderers, Material material)
        {
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null) continue;
                int materials = Mathf.Max(1, renderer.sharedMaterials.Length);
                for (int index = 0; index < materials; index++) buffer.DrawRenderer(renderer, material, index, 0);
            }
        }

        /// <summary>Renders with the command buffer drawn after the scene and reads the result back.</summary>
        private Color32[] Render(CommandBuffer buffer, bool scene)
        {
            int mask = _camera.cullingMask;
            if (!scene) _camera.cullingMask = 0;
            else _camera.cullingMask = mask & ~(1 << HiddenLayer);
            _camera.AddCommandBuffer(CameraEvent.AfterForwardAlpha, buffer);
            try { _camera.Render(); }
            finally
            {
                _camera.RemoveCommandBuffer(CameraEvent.AfterForwardAlpha, buffer);
                _camera.cullingMask = mask;
            }
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = _target;
            _read.ReadPixels(new Rect(0, 0, _width, _height), 0, 0);
            _read.Apply(false);
            RenderTexture.active = previous;
            return _read.GetPixels32();
        }

        private static bool Drawn(Color32 pixel) => pixel.r > 0 || pixel.g > 0 || pixel.b > 0 || pixel.a > 0;

        private static int Count(Color32[] pixels, Rect? only = null, int width = 0, int height = 0)
        {
            int count = 0;
            if (only is Rect rect)
            {
                int x0 = Mathf.Clamp(Mathf.FloorToInt(rect.xMin * width), 0, width), x1 = Mathf.Clamp(Mathf.CeilToInt(rect.xMax * width), 0, width);
                int y0 = Mathf.Clamp(Mathf.FloorToInt(rect.yMin * height), 0, height), y1 = Mathf.Clamp(Mathf.CeilToInt(rect.yMax * height), 0, height);
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++)
                        if (Drawn(pixels[y * width + x])) count++;
                return count;
            }
            foreach (Color32 pixel in pixels) if (Drawn(pixel)) count++;
            return count;
        }

        /// <summary>
        /// Counts each actor's pixels for one camera. The actors' renderers move to a layer the camera skips while the
        /// scene draws, then the command buffer draws them over the scene's depth.
        /// </summary>
        internal PixelSample Measure(float time, Vector3 position, Quaternion rotation, float fov, List<(string Id, GameObject? Actor, Rect HeadBox)> actors,
            float foregroundLimit, string? debugImage = null)
        {
            PixelSample sample = new() { Time = time };
            Setup(position, rotation, fov);
            List<List<Renderer>> drawn = actors.Select(a => Renderers(a.Actor)).ToList();
            Dictionary<GameObject, int> layers = new();
            foreach (Renderer renderer in drawn.SelectMany(r => r))
                if (!layers.ContainsKey(renderer.gameObject)) { layers[renderer.gameObject] = renderer.gameObject.layer; renderer.gameObject.layer = HiddenLayer; }
            Color32[]? debugVisible = null, debugAlone = null, debugForeground = null;
            try
            {
                for (int index = 0; index < actors.Count; index++)
                {
                    PixelActor actor = new() { Id = actors[index].Id, Present = drawn[index].Count > 0 };
                    sample.Actors.Add(actor);
                    if (!actor.Present) continue;
                    using CommandBuffer visible = new() { name = "cameraTools visible" };
                    visible.ClearRenderTarget(false, true, Color.clear);
                    for (int other = 0; other < actors.Count; other++) if (other != index) Draw(visible, drawn[other], _depthOnly);
                    Draw(visible, drawn[index], _draw);
                    Color32[] seen = Render(visible, true);
                    actor.Visible = Count(seen);
                    if (index == 0) debugVisible = seen;
                    using CommandBuffer alone = new() { name = "cameraTools alone" };
                    alone.ClearRenderTarget(true, true, Color.clear);
                    Draw(alone, drawn[index], _draw);
                    Color32[] only = Render(alone, false);
                    actor.Alone = Count(only);
                    if (index == 0) debugAlone = only;
                    actor.VisibleFraction = actor.Alone > 0 ? Mathf.Clamp01(actor.Visible / (float)actor.Alone) : 0f;
                    // Drawn alone again with a view twice as wide and tall: the frame is the middle half of that image.
                    Matrix4x4 projection = _camera.projectionMatrix;
                    _camera.projectionMatrix = Matrix4x4.Scale(new Vector3(0.5f, 0.5f, 1f)) * projection;
                    Color32[] wide = Render(alone, false);
                    _camera.projectionMatrix = projection;
                    int all = Count(wide), middle = Count(wide, new Rect(0.25f, 0.25f, 0.5f, 0.5f), _width, _height);
                    actor.InFrame = all > 0 ? Mathf.Clamp01(middle / (float)all) : 0f;
                }
                // Foreground: everything nearer than the limit, actors included, found with a quad at that distance that
                // passes the depth test only where something nearer was drawn.
                using CommandBuffer near = new() { name = "cameraTools foreground" };
                foreach (List<Renderer> renderers in drawn) Draw(near, renderers, _depthOnly);
                near.ClearRenderTarget(false, true, Color.clear);
                float halfHeight = Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * foregroundLimit;
                Matrix4x4 quad = Matrix4x4.TRS(position + rotation * Vector3.forward * foregroundLimit, rotation,
                    new Vector3(halfHeight * 2f * _camera.aspect * 1.2f, halfHeight * 2f * 1.2f, 1f));
                near.DrawMesh(_quad, quad, _farTest, 0, 0);
                Color32[] foreground = Render(near, true);
                debugForeground = foreground;
                sample.Foreground = Count(foreground) / (float)(_width * _height);
                for (int index = 0; index < actors.Count; index++)
                {
                    Rect head = actors[index].HeadBox;
                    if (head.width > 0f && Count(foreground, head, _width, _height) > 0) sample.Actors[index].HeadCovered = true;
                }
            }
            finally
            {
                foreach (KeyValuePair<GameObject, int> layer in layers) if (layer.Key != null) layer.Key.layer = layer.Value;
                _camera.targetTexture = null;
            }
            if (debugImage != null) WriteDebug(debugImage, debugVisible, debugAlone, debugForeground);
            return sample;
        }

        /// <summary>A picture of the counts for checking by eye: red is the first actor visible, green the same actor drawn alone, blue the foreground.</summary>
        private void WriteDebug(string path, Color32[]? visible, Color32[]? alone, Color32[]? foreground)
        {
            Color32[] picture = new Color32[_width * _height];
            for (int index = 0; index < picture.Length; index++)
                picture[index] = new Color32(visible != null && Drawn(visible[index]) ? (byte)255 : (byte)0, alone != null && Drawn(alone[index]) ? (byte)160 : (byte)0,
                    foreground != null && Drawn(foreground[index]) ? (byte)255 : (byte)0, 255);
            Texture2D image = new(_width, _height, TextureFormat.RGBA32, false);
            image.SetPixels32(picture);
            image.Apply(false);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, image.EncodeToPNG());
            UnityEngine.Object.Destroy(image);
        }

        /// <summary>
        /// The visible mask of one actor at the given size, for the brightness of the actor against what is behind it.
        /// Returns null when the actor draws no pixels.
        /// </summary>
        internal bool[]? Mask(Vector3 position, Quaternion rotation, float fov, GameObject? actor, List<GameObject?> others)
        {
            List<Renderer> renderers = Renderers(actor);
            if (renderers.Count == 0) return null;
            Setup(position, rotation, fov);
            List<Renderer> blocking = others.SelectMany(Renderers).ToList();
            Dictionary<GameObject, int> layers = new();
            foreach (Renderer renderer in renderers.Concat(blocking))
                if (!layers.ContainsKey(renderer.gameObject)) { layers[renderer.gameObject] = renderer.gameObject.layer; renderer.gameObject.layer = HiddenLayer; }
            try
            {
                using CommandBuffer visible = new() { name = "cameraTools mask" };
                visible.ClearRenderTarget(false, true, Color.clear);
                Draw(visible, blocking, _depthOnly);
                Draw(visible, renderers, _draw);
                Color32[] pixels = Render(visible, true);
                return pixels.Select(Drawn).ToArray();
            }
            finally
            {
                foreach (KeyValuePair<GameObject, int> layer in layers) if (layer.Key != null) layer.Key.layer = layer.Value;
                _camera.targetTexture = null;
            }
        }

        internal int Width => _width;
        internal int Height => _height;
    }
}
