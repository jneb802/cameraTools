using System.Collections.Generic;
using UnityEngine;

namespace cameraTools
{
    internal static class CameraPanelStyle
    {
        private static GUISkin? _skin;
        private static readonly List<Texture2D> Textures = new();
        /// <summary>Where the cameraTools window was last drawn, in screen pixels, for pointer blocking.</summary>
        public static Rect PanelRect;

        /// <summary>
        /// Valheim's own menu scale: 1 at 1920x1080 with the game's UI scale at 100%, 1.33 at 2560x1440.
        /// The panels are laid out at that 1920x1080 reference and drawn through this scale, so they keep the
        /// size of the inventory and crafting menus at every resolution and UI scale setting.
        /// </summary>
        public static float Scale => Mathf.Max(0.5f, Mathf.Min(Screen.width / 1920f, Screen.height / 1080f) * GuiScaler.m_largeGuiScale);

        /// <summary>The screen size in reference units, the space the scaled panels are laid out in.</summary>
        public static Vector2 ReferenceScreen => new(Screen.width / Scale, Screen.height / Scale);

        /// <summary>Draws the following IMGUI calls at Valheim's menu scale; returns the matrix to restore.</summary>
        public static Matrix4x4 BeginScaled()
        {
            Matrix4x4 previous = GUI.matrix;
            float scale = Scale;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            return previous;
        }

        public static Rect ToScreen(Rect rect)
        {
            float scale = Scale;
            return new Rect(rect.x * scale, rect.y * scale, rect.width * scale, rect.height * scale);
        }
        public static bool PointerBlocked
        {
            get
            {
                if (Cursor.lockState == CursorLockMode.Locked) return false;
                Vector2 pointer = new(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
                return GUIUtility.hotControl != 0 || (CameraToolsPanel.Visible && PanelRect.Contains(pointer));
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
                // Reference sizes, measured from the crafting menu at 1920x1080 and UI scale 100%: body text 15 px,
                // list rows about 34 px. Controls are a little tighter than the menu's rows to keep the window small.
                foreach (GUIStyle style in new[] { _skin.label, _skin.button, _skin.toggle, _skin.textField, _skin.window, _skin.box })
                { style.fontSize = BodyFontSize; style.normal.textColor = new Color(0.94f, 0.87f, 0.72f); }
                Apply(_skin.window, sprites, "woodpanel_trophys");
                Apply(_skin.button, sprites, "button");
                _skin.window.padding = new RectOffset(14, 14, 32, 10);
                _skin.window.fontSize = 18;
                _skin.window.normal.textColor = new Color(1f, 0.75f, 0.25f);
                _skin.window.onNormal.textColor = _skin.window.normal.textColor;
                _skin.window.font = font;
                _skin.button.hover.textColor = new Color(1f, 0.8f, 0.35f);
                _skin.button.active.textColor = Color.white;
                _skin.button.fixedHeight = 27;
                _skin.button.margin = new RectOffset(2, 2, 2, 2);
                _skin.button.padding = new RectOffset(6, 6, 2, 2);
                _skin.label.wordWrap = true;
                _skin.label.padding = new RectOffset(2, 2, 2, 2);
                _skin.label.margin = new RectOffset(2, 2, 2, 2);
                _skin.textField.fixedHeight = 25;
                _skin.horizontalSlider.margin = new RectOffset(4, 4, 9, 9);
                return _skin;
            }
        }

        public const int BodyFontSize = 15;

        /// <summary>A button that shows its on state with a tick, readable where the small IMGUI check box is not.</summary>
        public static bool ToggleButton(bool value, string label, params GUILayoutOption[] options) =>
            GUILayout.Button(value ? label + " ✓" : label, options) ? !value : value;

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

        /// <summary>Keeps a window, in reference units, on the screen.</summary>
        public static Rect Clamp(Rect rect)
        {
            Vector2 screen = ReferenceScreen;
            rect.x = Mathf.Clamp(rect.x, 0, Mathf.Max(0, screen.x - rect.width));
            rect.y = Mathf.Clamp(rect.y, 0, Mathf.Max(0, screen.y - rect.height));
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
            if (options.Count == 0) { GUILayout.Label(label + ": none"); return selected; }
            selected = Mathf.Clamp(selected, 0, options.Count - 1);
            if (GUILayout.Button(label + ": " + options[selected] + (_open ? "  ▲" : "  ▼"))) _open = !_open;
            if (!_open) return selected;
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(120));
            for (int index = 0; index < options.Count; index++)
                if (GUILayout.Button(options[index])) { selected = index; _open = false; }
            GUILayout.EndScrollView();
            return selected;
        }
    }
}
