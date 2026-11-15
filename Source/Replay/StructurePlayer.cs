using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace cameraTools.Replay
{
    /// <summary>
    /// Rebuilds recorded mod structures (encounter rooms) in a replay session with their mod's replay builder,
    /// shows each one only between its recorded start and end, and keeps exactly one copy of each through seeks.
    /// A structure no loaded mod builds is left out with one log line.
    /// </summary>
    internal sealed class StructurePlayer
    {
        private readonly List<StructureTrack> _tracks;
        private readonly Dictionary<StructureTrack, GameObject> _built = new();
        private readonly HashSet<StructureTrack> _failed = new();
        private readonly HashSet<string> _reported = new();

        internal StructurePlayer(ReplayFile replay)
        {
            _tracks = replay.Structures;
            if (_tracks.Count > 0) cameraToolsPlugin.TemplateLogger.LogInfo($"[StructurePlayer] {_tracks.Count} recorded structures: {string.Join(", ", _tracks.Select(Describe))}");
        }

        internal void Update(float time)
        {
            foreach (StructureTrack track in _tracks)
            {
                bool active = track.Start <= time && time < track.End;
                if (_built.TryGetValue(track, out GameObject instance))
                {
                    if (instance == null) { _built.Remove(track); if (!active) continue; }
                    else
                    {
                        if (instance.activeSelf != active) instance.SetActive(active);
                        continue;
                    }
                }
                if (!active || _failed.Contains(track)) continue;
                GameObject? built = ReplayHooks.Build(track.Name, track.Position, track.Rotation);
                if (built == null)
                {
                    _failed.Add(track);
                    if (_reported.Add(track.Name))
                        cameraToolsPlugin.TemplateLogger.LogWarning($"[StructurePlayer] Structure skipped, no loaded mod builds it: {track.Name}");
                    continue;
                }
                built.transform.SetPositionAndRotation(track.Position, track.Rotation);
                built.SetActive(true);
                _built[track] = built;
                // Camera checks and floor queries in the same frame must see the new colliders.
                Physics.SyncTransforms();
                cameraToolsPlugin.TemplateLogger.LogInfo($"[StructurePlayer] Built {Describe(track)}");
            }
        }

        /// <summary>One line for agents: the recorded structures, which are shown now, and how many scene roots carry each name.</summary>
        internal string Status(float time)
        {
            Dictionary<string, int> roots = new();
            for (int index = 0; index < SceneManager.sceneCount; index++)
                foreach (GameObject root in SceneManager.GetSceneAt(index).GetRootGameObjects())
                    if (root.activeInHierarchy) roots[root.name] = roots.TryGetValue(root.name, out int count) ? count + 1 : 1;
            int shown = _built.Count(pair => pair.Value != null && pair.Value.activeInHierarchy);
            IEnumerable<string> entries = _tracks.Select(track =>
            {
                string state = _failed.Contains(track) ? "skipped" : _built.TryGetValue(track, out GameObject instance) && instance != null && instance.activeInHierarchy ? "shown" :
                    track.Start <= time && time < track.End ? "pending" : "hidden";
                return $"{Describe(track)} {state} roots={(roots.TryGetValue(track.Name, out int count) ? count : 0)}";
            });
            return $"structures={_tracks.Count} shown={shown} skipped={_failed.Count} time={time:F2} | {string.Join(" | ", entries)}";
        }

        internal void Dispose()
        {
            foreach (GameObject instance in _built.Values)
                if (instance != null) Object.Destroy(instance);
            _built.Clear();
        }

        private static string Describe(StructureTrack track) =>
            $"{track.Name} at {track.Position.x:F1},{track.Position.y:F1},{track.Position.z:F1} {track.Start:F2}-{(float.IsPositiveInfinity(track.End) ? "end" : track.End.ToString("F2"))}s";
    }
}
