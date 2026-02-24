using UnityEngine;

namespace cameraTools.Replay
{
    public class ReplayTimelinePanel : MonoBehaviour
    {
        private Rect _windowRect;
        private const int WindowId = 98235;

        private void Start()
        {
            float x = (Screen.width - 400f) / 2f;
            _windowRect = new Rect(x, Screen.height - 140f, 400f, 120f);
        }

        private void OnGUI()
        {
            if (!ReplayPlayer.IsPlaying)
                return;

            _windowRect = GUI.Window(WindowId, _windowRect, DrawWindow, "Replay Timeline");

            // Clamp to screen bounds
            _windowRect.x = Mathf.Clamp(_windowRect.x, 0f, Screen.width - _windowRect.width);
            _windowRect.y = Mathf.Clamp(_windowRect.y, 0f, Screen.height - _windowRect.height);
        }

        private void DrawWindow(int id)
        {
            var player = ReplayPlayer.Instance;
            if (player == null)
                return;

            float currentTime = player.PlaybackTime;
            float duration = player.Duration;

            // Time display
            string worldInfo = player.HasWorldEvents ? $"  [{player.WorldEventCount} world events]" : "";
            GUILayout.Label($"Time: {FormatTime(currentTime)} / {FormatTime(duration)}{worldInfo}");

            // Scrub slider
            float newTime = GUILayout.HorizontalSlider(currentTime, 0f, duration);
            if (!Mathf.Approximately(newTime, currentTime))
                player.Seek(newTime);

            GUILayout.Space(8f);

            // Controls row
            GUILayout.BeginHorizontal();

            if (GUILayout.Button(player.IsPaused ? "Play" : "Pause", GUILayout.Width(60f)))
                player.TogglePause();

            if (GUILayout.Button("Stop", GUILayout.Width(60f)))
                player.StopPlayback();

            GUILayout.FlexibleSpace();

            GUILayout.Label($"Speed: {player.PlaybackSpeed:F2}x", GUILayout.Width(90f));

            if (GUILayout.Button("-", GUILayout.Width(25f)))
                player.AdjustSpeed(-0.25f);

            if (GUILayout.Button("+", GUILayout.Width(25f)))
                player.AdjustSpeed(0.25f);

            GUILayout.EndHorizontal();

            GUI.DragWindow();
        }

        private static string FormatTime(float seconds)
        {
            int mins = (int)(seconds / 60f);
            float secs = seconds % 60f;
            return $"{mins}:{secs:00.0}";
        }
    }
}
