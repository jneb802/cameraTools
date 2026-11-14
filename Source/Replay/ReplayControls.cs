using UnityEngine;

namespace cameraTools.Replay
{
    /// <summary>The Replay tab of the cameraTools window. Its state lives here, so hiding the window keeps it.</summary>
    internal sealed class ReplayControls
    {
        private readonly PanelDropdown _music = new();
        private readonly PanelDropdown _targets = new();
        private string _message = "";

        internal void Draw()
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
            if (player.Highlights.Count > 0 && Event.current.type == EventType.Repaint && player.Duration > 0f)
            {
                // A mark above the timeline for each highlight.
                Rect slider = GUILayoutUtility.GetLastRect();
                Color previous = GUI.color;
                GUI.color = new Color(1f, 0.8f, 0.2f);
                foreach (Highlight highlight in player.Highlights)
                {
                    float x = slider.x + slider.width * Mathf.Clamp01(highlight.t / player.Duration);
                    GUI.DrawTexture(new Rect(x - 1.5f, slider.y - 4f, 3f, slider.height + 8f), Texture2D.whiteTexture);
                }
                GUI.color = previous;
            }
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
            GUILayout.Label($"Left-click: aim lock • Secondary attack: attach • {cameraToolsPlugin.HighlightKey.Value}: highlight ({player.Highlights.Count})");
            if (_message.Length > 0) GUILayout.Label(_message);
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
