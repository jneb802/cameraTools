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
        private string _lastAction = "";

        private const float Width = 400f;

        private void Start()
        {
            _windowRect = new Rect(12f, (CameraPanelStyle.ReferenceScreen.y - 300f) / 2f, Width, 0f);

            _lineTexture = new Texture2D(1, 1);
            _lineTexture.SetPixel(0, 0, new Color(1f, 0f, 0f, 0.8f));
            _lineTexture.Apply();
        }

        private void Update()
        {
            if (cameraToolsPlugin.PanelToggleKey.Value.IsDown())
            {
                _showPanel = !_showPanel;
                CameraPanelStyle.CameraVisible = _showPanel;
                cameraToolsPlugin.TemplateLogger.LogDebug(_showPanel ? "Camera Tools panel opened" : "Camera Tools panel closed");
            }
        }

        private void OnGUI()
        {
            if (_showFrameGuide && _lineTexture != null)
            {
                float cropWidth = Screen.height * (9f / 16f);
                float leftX = (Screen.width - cropWidth) / 2f;
                float rightX = (Screen.width + cropWidth) / 2f;
                GUI.DrawTexture(new Rect(leftX - LineWidth, 0, LineWidth, Screen.height), _lineTexture);
                GUI.DrawTexture(new Rect(rightX, 0, LineWidth, Screen.height), _lineTexture);
            }

            if (!_showPanel || GameCamera.m_instance == null)
                return;

            GUISkin previous = GUI.skin;
            GUI.skin = CameraPanelStyle.Skin;
            Matrix4x4 matrix = CameraPanelStyle.BeginScaled();
            try
            {
                _windowRect.height = 0;
                _windowRect = GUILayout.Window(WindowId, _windowRect, DrawWindow, "Camera Tools", GUILayout.Width(Width));
                _windowRect = CameraPanelStyle.Clamp(_windowRect);
                CameraPanelStyle.CameraRect = CameraPanelStyle.ToScreen(_windowRect);
            }
            finally { GUI.matrix = matrix; GUI.skin = previous; }
        }

        private void DrawWindow(int id)
        {
            GUILayout.BeginHorizontal();
            bool isFreeFly = GameCamera.InFreeFly();
            if (CameraPanelStyle.ToggleButton(isFreeFly, "Free fly") != isFreeFly)
                GameCamera.m_instance.ToggleFreeFly();
            bool hudVisible = !(Hud.instance != null && Hud.instance.m_userHidden);
            if (CameraPanelStyle.ToggleButton(hudVisible, "HUD") != hudVisible)
            {
                try { CreativeZoneCameraController.SetHudVisible(!hudVisible); }
                catch (System.Exception ex) { _lastAction = ex.Message; }
            }
            // The player follows the camera only while free fly is on.
            PlayerFollowPatch.FollowEnabled = CameraPanelStyle.ToggleButton(PlayerFollowPatch.FollowEnabled, "Player follows");
            _showFrameGuide = CameraPanelStyle.ToggleButton(_showFrameGuide, "9:16");
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            float currentSmooth = GameCamera.m_instance.GetFreeFlySmoothness();
            GUILayout.Label($"Smoothness {currentSmooth:F2}", GUILayout.Width(136f));
            float newSmooth = GUILayout.HorizontalSlider(currentSmooth, 0f, 1f);
            if (!Mathf.Approximately(newSmooth, currentSmooth))
            {
                GameCamera.m_instance.SetFreeFlySmoothness(newSmooth);
                _smoothnessText = newSmooth.ToString("F2");
            }
            _smoothnessText = GUILayout.TextField(_smoothnessText, GUILayout.Width(46f));
            if (GUILayout.Button("Set", GUILayout.Width(42f)) && float.TryParse(_smoothnessText, out float parsed))
            {
                parsed = Mathf.Clamp01(parsed);
                GameCamera.m_instance.SetFreeFlySmoothness(parsed);
                _smoothnessText = parsed.ToString("F2");
            }
            GUILayout.EndHorizontal();

            Vector3 position = GameCamera.m_instance.transform.position;
            GUILayout.Label($"Camera {position.x:F1}, {position.y:F1}, {position.z:F1}  •  {position.magnitude:F1} m from 0, 0, 0");

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Prepare"))
            {
                try
                {
                    CreativeZoneCameraController.SetFreeFly(true);
                    CreativeZoneCameraController.SetHudVisible(false);
                    _lastAction = "Prepared";
                }
                catch (System.Exception ex) { _lastAction = ex.Message; }
            }
            if (CreativeZoneCameraController.IsOrbiting)
            {
                if (GUILayout.Button("Stop orbit"))
                {
                    CreativeZoneCameraController.StopOrbit();
                    _lastAction = "Orbit stopped";
                }
            }
            else if (GUILayout.Button("Orbit 360"))
            {
                try
                {
                    CreativeZoneCameraController.StartOrbit(Vector3.zero, 20f, 360f);
                    _lastAction = "Orbit started";
                }
                catch (System.Exception ex) { _lastAction = ex.Message; }
            }
            if (GUILayout.Button("Screenshot"))
            {
                try { _lastAction = CreativeZoneCameraController.CaptureScreenshot(null); }
                catch (System.Exception ex) { _lastAction = ex.Message; }
            }
            GUILayout.EndHorizontal();

            if (!string.IsNullOrWhiteSpace(_lastAction)) GUILayout.Label(_lastAction);
            GUILayout.Label("Ctrl+F1: cursor • Drag the title to move");
            GUI.DragWindow(new Rect(0, 0, _windowRect.width, 32));
        }

        private void OnDestroy()
        {
            CameraPanelStyle.CameraVisible = false;
            if (_lineTexture != null)
                Destroy(_lineTexture);
        }
    }
}
