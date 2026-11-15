using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

namespace cameraTools.Replay
{
    /// <summary>A moment the user tagged while watching, with the camera view they had.</summary>
    public sealed class Highlight
    {
        public int id;
        public float t;
        public string label = "";
        public float[] cameraPosition = new float[3];
        public float[] cameraForward = new float[3];
        public float fov;
        public string target = "";
        public long createdUnix;
    }

    /// <summary>
    /// Highlights live in &lt;name&gt;.highlights.json beside the recording, so the .valreplay file never changes
    /// and a recording can be shared without the user's notes.
    /// </summary>
    internal static class Highlights
    {
        internal static string JsonPath(string replayPath) => Path.ChangeExtension(replayPath, ".highlights.json");

        internal static List<Highlight> Load(string replayPath)
        {
            string path = JsonPath(replayPath);
            if (!File.Exists(path)) return new List<Highlight>();
            try { return JsonConvert.DeserializeObject<List<Highlight>>(File.ReadAllText(path)) ?? new List<Highlight>(); }
            catch (Exception error)
            {
                cameraToolsPlugin.TemplateLogger.LogWarning($"Could not read {path}: {error.Message}");
                return new List<Highlight>();
            }
        }

        internal static void Save(string replayPath, List<Highlight> highlights)
        {
            string path = JsonPath(replayPath);
            string temporary = path + ".writing";
            File.WriteAllText(temporary, JsonConvert.SerializeObject(highlights.OrderBy(h => h.t).ToList(), Formatting.Indented));
            if (File.Exists(path)) File.Delete(path);
            File.Move(temporary, path);
        }

        /// <summary>Tags the playing replay's current moment with the current camera.</summary>
        internal static Highlight Add(string label)
        {
            ReplayPlayer player = ReplayPlayer.Instance;
            string path = player.FilePath ?? throw new InvalidOperationException("This replay was not opened from a file");
            List<Highlight> list = Load(path);
            Transform camera = GameCamera.m_instance.transform;
            Camera? view = Utils.GetMainCamera();
            Highlight highlight = new()
            {
                id = list.Count == 0 ? 1 : list.Max(h => h.id) + 1,
                t = (float)Math.Round(player.PlaybackTime, 3),
                label = label,
                cameraPosition = new[] { camera.position.x, camera.position.y, camera.position.z },
                cameraForward = new[] { camera.forward.x, camera.forward.y, camera.forward.z },
                fov = view != null ? view.fieldOfView : 0f,
                target = player.CameraRig?.SelectedActor ?? "",
                createdUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };
            list.Add(highlight);
            Save(path, list);
            player.InvalidateHighlights();
            return highlight;
        }

        internal static bool Remove(int id)
        {
            string path = ReplayPlayer.Instance.FilePath ?? throw new InvalidOperationException("This replay was not opened from a file");
            List<Highlight> list = Load(path);
            int removed = list.RemoveAll(h => h.id == id);
            if (removed == 0) return false;
            Save(path, list);
            ReplayPlayer.Instance.InvalidateHighlights();
            return true;
        }

        internal static string Line(Highlight h) =>
            $"highlight id={h.id} t={h.t.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} label=\"{h.label}\" target={(h.target.Length > 0 ? h.target : "none")} " +
            $"camera={string.Join(",", h.cameraPosition.Select(v => v.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)))} fov={h.fov:F1}";
    }

    /// <summary>Asks for an optional label with the game's text prompt, then saves the highlight.</summary>
    internal sealed class HighlightPrompt : TextReceiver
    {
        public string GetText() => "";

        public void SetText(string text)
        {
            try
            {
                Highlight highlight = Highlights.Add(text.Trim());
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, $"Highlight {highlight.id} at {highlight.t:F1}s");
            }
            catch (Exception error) { cameraToolsPlugin.TemplateLogger.LogWarning("Highlight not saved: " + error.Message); }
        }
    }
}
