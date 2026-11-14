using UnityEngine;

namespace cameraTools.Replay
{
    public class ReplayTimelinePanel : MonoBehaviour
    {
        public static bool Visible = true;
        private Rect _windowRect;
        private const int WindowId = 98235;
        private readonly PanelDropdown _music = new();
        private readonly PanelDropdown _targets = new();
        private string _message = "";

        private const float Width = 460f;

        private void Start()
        {
            Vector2 screen = CameraPanelStyle.ReferenceScreen;
            _windowRect = new Rect((screen.x - Width) / 2f, screen.y - 340f, Width, 0f);
        }

        private void OnGUI()
        {
            if (!ReplayPlayer.IsPlaying || !Visible) return;
            GUISkin previous = GUI.skin;
            GUI.skin = CameraPanelStyle.Skin;
            Matrix4x4 matrix = CameraPanelStyle.BeginScaled();
            try
            {
                _windowRect.height = 0;
                _windowRect = GUILayout.Window(WindowId, _windowRect, DrawWindow, "Combat Replay", GUILayout.Width(Width));
                _windowRect = CameraPanelStyle.Clamp(_windowRect);
                CameraPanelStyle.ReplayRect = CameraPanelStyle.ToScreen(_windowRect);
            }
            finally { GUI.matrix = matrix; GUI.skin = previous; }
        }

        private void DrawWindow(int id)
        {
            ReplayPlayer player = ReplayPlayer.Instance;
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{FormatTime(player.PlaybackTime)} / {FormatTime(player.Duration)}", GUILayout.Width(112));
            if (GUILayout.Button(player.IsPaused ? "Play" : "Pause", GUILayout.Width(64))) player.TogglePause();
            if (GUILayout.Button("−", GUILayout.Width(28))) player.AdjustSpeed(-0.25f);
            GUILayout.Label($"{player.PlaybackSpeed:F2}×", GUILayout.Width(46));
            if (GUILayout.Button("+", GUILayout.Width(28))) player.AdjustSpeed(0.25f);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(player.Isolated ? "Back to menu" : "Exit replay", GUILayout.Width(112)))
            {
                if (player.Isolated) ReplaySession.Exit(); else player.StopPlayback();
                GUILayout.EndHorizontal();
                return;
            }
            GUILayout.EndHorizontal();
            float time = GUILayout.HorizontalSlider(player.PlaybackTime, 0, player.Duration);
            if (!Mathf.Approximately(time, player.PlaybackTime)) player.Seek(time);
            ReplayCameraRig? rig = player.CameraRig;
            if (rig != null)
            {
                rig.Selected = _targets.Draw("Target", rig.Labels, rig.Selected);
                GUILayout.BeginHorizontal();
                ViewButton(rig, "Aim at", "aim");
                ViewButton(rig, "Attach", "attach");
                if (GUILayout.Button("Find")) { rig.SeekToSelected(); _message = ""; }
                if (GUILayout.Button("Detach")) { player.Detach(); _message = ""; }
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                ViewButton(rig, "Third person", "third");
                ViewButton(rig, "Front", "front");
                ViewButton(rig, "Projectile", "projectile");
                if (player.HasRecordedCamera && CameraPanelStyle.ToggleButton(player.FollowingRecordedCamera, "Recorded") != player.FollowingRecordedCamera)
                {
                    if (player.FollowingRecordedCamera) player.Detach(); else player.FollowRecordedCamera();
                    _message = "";
                }
                GUILayout.EndHorizontal();
                GUILayout.Label(rig.Status);
            }
            ReplayMusic? music = player.Music;
            if (music != null)
            {
                int selected = _music.Draw("Music", music.Labels, music.Selected);
                if (selected != music.Selected) music.Select(selected);
                GUILayout.BeginHorizontal();
                GUILayout.Label($"Music volume {music.Volume:P0}", GUILayout.Width(150));
                music.Volume = GUILayout.HorizontalSlider(music.Volume, 0, 1);
                GUILayout.EndHorizontal();
                if (music.Selected > 0 && MusicMan.m_masterMusicVolume <= 0)
                    GUILayout.Label("Game music is muted. Raise Master and Music volume in Settings.");
            }
            if (player.SceneSummary.Length > 0) GUILayout.Label(player.SceneSummary);
            GUILayout.Label("Left-click: aim lock • Secondary attack: attach • Ctrl+F1: cursor");
            if (_message.Length > 0) GUILayout.Label(_message);
            GUI.DragWindow(new Rect(0, 0, _windowRect.width, 32));
        }

        private void ViewButton(ReplayCameraRig rig, string label, string view)
        {
            if (!GUILayout.Button(label)) return;
            try { rig.SetView(view); _message = ""; }
            catch (System.Exception error) { _message = error.Message; }
        }

        private static string FormatTime(float seconds) => $"{(int)(seconds / 60)}:{seconds % 60:00.0}";
    }
}
