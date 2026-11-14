using cameraTools.Replay;
using UnityEngine;

namespace cameraTools
{
    /// <summary>
    /// The one cameraTools window: a Camera tab and, while a replay plays, a Replay tab. F6 shows and hides
    /// the whole window; hiding it changes no camera, playback or control state.
    /// </summary>
    public class CameraToolsPanel : MonoBehaviour
    {
        public enum PanelTab { Camera, Replay }

        public static bool Visible;
        public static PanelTab Tab = PanelTab.Camera;
        private static bool _visibleBeforeReplay;
        private bool _showFrameGuide;
        private Rect _windowRect;
        private string _smoothnessText = "0.5";
        private const int WindowId = 98234;
        private const float Width = 600f;
        private Texture2D? _lineTexture;
        private const float LineWidth = 2f;
        private string _lastAction = "";
        private readonly ReplayControls _replay = new();

        /// <summary>A starting replay opens the window on its Replay tab.</summary>
        internal static void OnReplayStarted()
        {
            _visibleBeforeReplay = Visible;
            Visible = true;
            Tab = PanelTab.Replay;
        }

        /// <summary>A stopped replay returns the window to the Camera tab and to the visibility it had before.</summary>
        internal static void OnReplayStopped()
        {
            Visible = _visibleBeforeReplay;
            Tab = PanelTab.Camera;
        }

        private void Start()
        {
            _windowRect = new Rect(20f, Screen.height - 640f, Width, 0f);

            _lineTexture = new Texture2D(1, 1);
            _lineTexture.SetPixel(0, 0, new Color(1f, 0f, 0f, 0.8f));
            _lineTexture.Apply();
        }

        private void Update()
        {
            if (cameraToolsPlugin.PanelToggleKey.Value.IsDown())
            {
                Visible = !Visible;
                cameraToolsPlugin.TemplateLogger.LogDebug(Visible ? "Camera Tools panel opened" : "Camera Tools panel closed");
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

            if (!Visible || GameCamera.m_instance == null)
                return;

            GUISkin previous = GUI.skin;
            GUI.skin = CameraPanelStyle.Skin;
            try
            {
                _windowRect.height = 0;
                _windowRect = GUILayout.Window(WindowId, _windowRect, DrawWindow, "Camera Tools", GUILayout.Width(Width));
                _windowRect = CameraPanelStyle.Clamp(_windowRect);
                CameraPanelStyle.PanelRect = _windowRect;
            }
            finally { GUI.skin = previous; }
        }

        private void DrawWindow(int id)
        {
            if (!ReplayPlayer.IsPlaying) Tab = PanelTab.Camera;
            GUILayout.BeginHorizontal();
            TabButton("Camera", PanelTab.Camera);
            GUI.enabled = ReplayPlayer.IsPlaying;
            TabButton(ReplayPlayer.IsPlaying ? "Replay" : "Replay (not playing)", PanelTab.Replay);
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.Space(6f);

            if (Tab == PanelTab.Replay) _replay.Draw();
            else DrawCamera();

            GUILayout.Label("F6: hide • Ctrl+F1: release/capture cursor • Drag the title to move");
            GUI.DragWindow(new Rect(0, 0, _windowRect.width, 40));
        }

        private static void TabButton(string label, PanelTab tab)
        {
            if (GUILayout.Toggle(Tab == tab, label, GUI.skin.button) && Tab != tab) Tab = tab;
        }

        private void DrawCamera()
        {
            // Free fly toggle
            bool isFreeFly = GameCamera.InFreeFly();
            bool toggled = GUILayout.Toggle(isFreeFly, "Free Fly");
            if (toggled != isFreeFly)
                GameCamera.m_instance.ToggleFreeFly();

            bool hudVisible = !(Hud.instance != null && Hud.instance.m_userHidden);
            bool hudToggled = GUILayout.Toggle(hudVisible, "Show HUD");
            if (hudToggled != hudVisible)
            {
                try
                {
                    CreativeZoneCameraController.SetHudVisible(hudToggled);
                }
                catch (System.Exception ex)
                {
                    _lastAction = ex.Message;
                }
            }

            // Player follows camera toggle (only relevant when free fly is active)
            bool followEnabled = PlayerFollowPatch.FollowEnabled;
            bool followToggled = GUILayout.Toggle(followEnabled, "Player Follows Camera");
            if (followToggled != followEnabled)
                PlayerFollowPatch.FollowEnabled = followToggled;

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

            GUILayout.Space(8f);
            GUILayout.Label(CreativeZoneCameraController.BuildStatus(Vector3.zero));

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Prepare"))
            {
                try
                {
                    CreativeZoneCameraController.SetFreeFly(true);
                    CreativeZoneCameraController.SetHudVisible(false);
                    _lastAction = "Prepared";
                }
                catch (System.Exception ex)
                {
                    _lastAction = ex.Message;
                }
            }

            if (CreativeZoneCameraController.IsOrbiting)
            {
                if (GUILayout.Button("Stop Orbit"))
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
                catch (System.Exception ex)
                {
                    _lastAction = ex.Message;
                }
            }
            GUILayout.EndHorizontal();

            if (GUILayout.Button("Screenshot"))
            {
                try
                {
                    _lastAction = CreativeZoneCameraController.CaptureScreenshot(null);
                }
                catch (System.Exception ex)
                {
                    _lastAction = ex.Message;
                }
            }

            if (!string.IsNullOrWhiteSpace(_lastAction))
            {
                GUILayout.Label(_lastAction);
            }
        }

        private void OnDestroy()
        {
            Visible = false;
            if (_lineTexture != null)
                Destroy(_lineTexture);
        }
    }
}
