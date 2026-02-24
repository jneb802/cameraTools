using UnityEngine;

namespace cameraTools
{
    public class FreeFlyPanel : MonoBehaviour
    {
        private bool _showPanel;
        private bool _showFrameGuide;
        private Rect _windowRect;
        private string _smoothnessText = "0.5";
        private const int WindowId = 98234;
        private Texture2D? _lineTexture;
        private const float LineWidth = 2f;

        private void Start()
        {
            float y = (Screen.height - 180f) / 2f;
            _windowRect = new Rect(20f, y, 250f, 180f);

            _lineTexture = new Texture2D(1, 1);
            _lineTexture.SetPixel(0, 0, new Color(1f, 0f, 0f, 0.8f));
            _lineTexture.Apply();
        }

        private void Update()
        {
            if (cameraToolsPlugin.PanelToggleKey.Value.IsDown())
            {
                _showPanel = !_showPanel;
                cameraToolsPlugin.TemplateLogger.LogDebug(_showPanel ? "Camera Tools panel opened" : "Camera Tools panel closed");
            }
        }

        private void OnGUI()
        {
            if (!_showPanel || GameCamera.m_instance == null)
                return;

            _windowRect = GUI.Window(WindowId, _windowRect, DrawWindow, "Camera Tools");

            // Clamp to screen bounds
            _windowRect.x = Mathf.Clamp(_windowRect.x, 0f, Screen.width - _windowRect.width);
            _windowRect.y = Mathf.Clamp(_windowRect.y, 0f, Screen.height - _windowRect.height);

            if (_showFrameGuide && _lineTexture != null)
            {
                float cropWidth = Screen.height * (9f / 16f);
                float leftX = (Screen.width - cropWidth) / 2f;
                float rightX = (Screen.width + cropWidth) / 2f;
                GUI.DrawTexture(new Rect(leftX - LineWidth, 0, LineWidth, Screen.height), _lineTexture);
                GUI.DrawTexture(new Rect(rightX, 0, LineWidth, Screen.height), _lineTexture);
            }
        }

        private void DrawWindow(int id)
        {
            GUILayout.Space(4f);

            // Free fly toggle
            bool isFreeFly = GameCamera.InFreeFly();
            bool toggled = GUILayout.Toggle(isFreeFly, "Free Fly");
            if (toggled != isFreeFly)
                GameCamera.m_instance.ToggleFreeFly();

            GUILayout.Space(8f);

            // Smoothness slider
            float currentSmooth = GameCamera.m_instance.GetFreeFlySmoothness();
            GUILayout.Label($"Smoothness: {currentSmooth:F2}");
            float newSmooth = GUILayout.HorizontalSlider(currentSmooth, 0f, 1f);
            if (!Mathf.Approximately(newSmooth, currentSmooth))
            {
                GameCamera.m_instance.SetFreeFlySmoothness(newSmooth);
                _smoothnessText = newSmooth.ToString("F2");
            }

            GUILayout.Space(8f);

            // Precise entry row
            GUILayout.BeginHorizontal();
            _smoothnessText = GUILayout.TextField(_smoothnessText, GUILayout.Width(80f));
            if (GUILayout.Button("Apply"))
            {
                if (float.TryParse(_smoothnessText, out float parsed))
                {
                    parsed = Mathf.Clamp01(parsed);
                    GameCamera.m_instance.SetFreeFlySmoothness(parsed);
                    _smoothnessText = parsed.ToString("F2");
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(8f);
            _showFrameGuide = GUILayout.Toggle(_showFrameGuide, "Show 9:16 Frame");

            GUI.DragWindow();
        }

        private void OnDestroy()
        {
            if (_lineTexture != null)
                Destroy(_lineTexture);
        }
    }
}
