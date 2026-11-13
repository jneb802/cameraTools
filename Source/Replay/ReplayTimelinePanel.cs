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

        private void Start() => _windowRect = new Rect((Screen.width - 680f) / 2, Screen.height - 510f, 680f, 490f);

        private void OnGUI()
        {
            if (!ReplayPlayer.IsPlaying || !Visible) return;
            GUISkin previous = GUI.skin;
            GUI.skin = CameraPanelStyle.Skin;
            try
            {
                _windowRect.height = 0;
                _windowRect = GUILayout.Window(WindowId, _windowRect, DrawWindow, "Combat Replay", GUILayout.Width(680));
                _windowRect = CameraPanelStyle.Clamp(_windowRect);
                CameraPanelStyle.ReplayRect = _windowRect;
            }
            finally { GUI.skin = previous; }
        }

        private void DrawWindow(int id)
        {
            ReplayPlayer player = ReplayPlayer.Instance;
            GUILayout.Label($"{FormatTime(player.PlaybackTime)} / {FormatTime(player.Duration)}    •    {(player.IsPaused ? "Paused" : "Playing")}");
            float time = GUILayout.HorizontalSlider(player.PlaybackTime, 0, player.Duration);
            if (!Mathf.Approximately(time, player.PlaybackTime)) player.Seek(time);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(player.IsPaused ? "Play" : "Pause", GUILayout.Width(100))) player.TogglePause();
            if (GUILayout.Button("−", GUILayout.Width(40))) player.AdjustSpeed(-0.25f);
            GUILayout.Label($"{player.PlaybackSpeed:F2}×", GUILayout.Width(75));
            if (GUILayout.Button("+", GUILayout.Width(40))) player.AdjustSpeed(0.25f);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(player.Isolated ? "Back to menu" : "Exit replay", GUILayout.Width(150)))
            {
                if (player.Isolated) ReplaySession.Exit(); else player.StopPlayback();
                GUILayout.EndHorizontal();
                return;
            }
            GUILayout.EndHorizontal();
            ReplayCameraRig? rig = player.CameraRig;
            if (rig != null)
            {
                GUILayout.Space(8);
                rig.Selected = _targets.Draw("Target", rig.Labels, rig.Selected);
                GUILayout.BeginHorizontal();
                ViewButton(rig, "Aim at", "aim");
                ViewButton(rig, "Attach here", "attach");
                if (GUILayout.Button("Find target")) { rig.SeekToSelected(); _message = ""; }
                if (GUILayout.Button("Detach")) { player.Detach(); _message = ""; }
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                ViewButton(rig, "Third person", "third");
                ViewButton(rig, "Front", "front");
                ViewButton(rig, "Projectile", "projectile");
                if (player.HasRecordedCamera && GUILayout.Button(player.FollowingRecordedCamera ? "Recorded camera ✓" : "Recorded camera"))
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
                GUILayout.Label($"Music volume {music.Volume:P0}", GUILayout.Width(180));
                music.Volume = GUILayout.HorizontalSlider(music.Volume, 0, 1);
                GUILayout.EndHorizontal();
                if (music.Selected > 0 && MusicMan.m_masterMusicVolume <= 0)
                    GUILayout.Label("Game music is muted. Raise Master and Music volume in Settings.");
            }
            if (player.SceneSummary.Length > 0) GUILayout.Label(player.SceneSummary);
            GUILayout.Label("Left-click: aim lock • Secondary attack: attach\nCtrl+F1: cursor • Drag title: move panel");
            if (_message.Length > 0) GUILayout.Label(_message);
            GUI.DragWindow(new Rect(0, 0, _windowRect.width, 40));
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
