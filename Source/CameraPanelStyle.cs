using System.Collections.Generic;
using UnityEngine;

namespace cameraTools
{
    internal static class CameraPanelStyle
    {
        private static GUISkin? _skin;
        private static readonly List<Texture2D> Textures = new();
        public static Rect CameraRect;
        public static Rect ReplayRect;
        public static bool CameraVisible;
        public static bool PointerBlocked
        {
            get
            {
                if (Cursor.lockState == CursorLockMode.Locked) return false;
                Vector2 pointer = new(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
                return GUIUtility.hotControl != 0 || (CameraVisible && CameraRect.Contains(pointer)) ||
                    (Replay.ReplayPlayer.IsPlaying && Replay.ReplayTimelinePanel.Visible && ReplayRect.Contains(pointer));
            }
        }

        public static GUISkin Skin
        {
            get
            {
                if (_skin != null) return _skin;
                _skin = Object.Instantiate(GUI.skin);
                Dictionary<string, Sprite> sprites = new();
                foreach (Sprite sprite in Resources.FindObjectsOfTypeAll<Sprite>()) sprites[sprite.name] = sprite;
                Font? font = null;
                foreach (Font candidate in Resources.FindObjectsOfTypeAll<Font>())
                    if (candidate.name == "AveriaSerifLibre-Regular") { font = candidate; break; }
                if (font != null) _skin.font = font;
                foreach (GUIStyle style in new[] { _skin.label, _skin.button, _skin.toggle, _skin.textField, _skin.window, _skin.box })
                { style.fontSize = 18; style.normal.textColor = new Color(0.94f, 0.87f, 0.72f); }
                Apply(_skin.window, sprites, "woodpanel_trophys");
                Apply(_skin.button, sprites, "button");
                _skin.window.padding = new RectOffset(24, 24, 46, 20);
                _skin.window.fontSize = 24;
                _skin.window.normal.textColor = new Color(1f, 0.75f, 0.25f);
                _skin.window.onNormal.textColor = _skin.window.normal.textColor;
                _skin.window.font = font;
                _skin.button.hover.textColor = new Color(1f, 0.8f, 0.35f);
                _skin.button.active.textColor = Color.white;
                _skin.button.fixedHeight = 34;
                _skin.button.margin = new RectOffset(3, 3, 4, 4);
                _skin.label.wordWrap = true;
                _skin.label.padding = new RectOffset(2, 2, 4, 4);
                _skin.toggle.fixedHeight = 30;
                _skin.textField.fixedHeight = 32;
                _skin.horizontalSlider.margin = new RectOffset(5, 5, 12, 12);
                return _skin;
            }
        }

        private static void Apply(GUIStyle style, Dictionary<string, Sprite> sprites, string name)
        {
            if (!sprites.TryGetValue(name, out Sprite sprite)) return;
            Rect rect = sprite.textureRect;
            RenderTexture temporary = RenderTexture.GetTemporary(sprite.texture.width, sprite.texture.height);
            RenderTexture? previous = RenderTexture.active;
            try
            {
                Graphics.Blit(sprite.texture, temporary);
                RenderTexture.active = temporary;
                Texture2D texture = new((int)rect.width, (int)rect.height, TextureFormat.RGBA32, false);
                texture.ReadPixels(rect, 0, 0);
                if (name == "woodpanel_trophys")
                {
                    // The native litpanel material darkens this artwork with _Brightness=0.37.
                    // IMGUI does not run that material, so bake its contrast into our private copy.
                    Color[] pixels = texture.GetPixels();
                    for (int index = 0; index < pixels.Length; index++)
                    {
                        Color pixel = pixels[index];
                        pixels[index] = new Color(pixel.r * 0.37f, pixel.g * 0.37f, pixel.b * 0.37f, pixel.a);
                    }
                    texture.SetPixels(pixels);
                }
                texture.Apply();
                Textures.Add(texture);
                style.normal.background = texture;
                style.onNormal.background = texture;
                style.hover.background = texture;
                style.active.background = texture;
                style.focused.background = texture;
                style.border = new RectOffset((int)sprite.border.x, (int)sprite.border.z, (int)sprite.border.w, (int)sprite.border.y);
            }
            finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(temporary); }
        }

        public static Rect Clamp(Rect rect)
        {
            rect.x = Mathf.Clamp(rect.x, 0, Mathf.Max(0, Screen.width - rect.width));
            rect.y = Mathf.Clamp(rect.y, 0, Mathf.Max(0, Screen.height - rect.height));
            return rect;
        }

        public static void Dispose()
        {
            foreach (Texture2D texture in Textures) Object.Destroy(texture);
            Textures.Clear();
            if (_skin != null) Object.Destroy(_skin);
            _skin = null;
        }
    }

    internal sealed class PanelDropdown
    {
        private bool _open;
        private Vector2 _scroll;
        public int Draw(string label, IReadOnlyList<string> options, int selected)
        {
            if (options.Count == 0) { GUILayout.Label(label + ": No targets"); return selected; }
            selected = Mathf.Clamp(selected, 0, options.Count - 1);
            if (GUILayout.Button(label + ": " + options[selected] + (_open ? "  ▲" : "  ▼"))) _open = !_open;
            if (!_open) return selected;
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(145));
            for (int index = 0; index < options.Count; index++)
                if (GUILayout.Button(options[index])) { selected = index; _open = false; }
            GUILayout.EndScrollView();
            return selected;
        }
    }
}
