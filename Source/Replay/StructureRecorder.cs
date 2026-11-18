using System.Collections.Generic;
using UnityEngine;

namespace cameraTools.Replay
{
    /// <summary>
    /// Records when the still objects mods reported through <see cref="ReplayHooks.Structure"/> (encounter rooms)
    /// appear and disappear near the recording player, with their pose. They are not sampled every frame.
    /// </summary>
    internal sealed class StructureRecorder
    {
        internal static StructureRecorder? Active;
        // Rooms are stacked 200 m apart straight below each other, so only the horizontal distance counts.
        private const float Range = 300f;
        private readonly ReplayFile _file;
        private readonly float _start;
        private readonly Dictionary<GameObject, string> _watched = new();
        private readonly Dictionary<GameObject, StructureTrack> _open = new();

        internal StructureRecorder(ReplayFile file)
        {
            _file = file;
            _start = Time.time;
            Active = this;
            foreach ((GameObject instance, string name) in ReplayHooks.Structures()) Watch(instance, name);
        }

        internal void Watch(GameObject instance, string name)
        {
            _watched[instance] = name;
            if (instance.activeInHierarchy) Open(instance, name, Time.time - _start);
        }

        private void Open(GameObject instance, string name, float time)
        {
            if (_open.ContainsKey(instance) || Player.m_localPlayer == null) return;
            Vector3 offset = instance.transform.position - Player.m_localPlayer.transform.position;
            if (new Vector2(offset.x, offset.z).magnitude > Range) return;
            StructureTrack track = new() { Name = name, Position = instance.transform.position, Rotation = instance.transform.rotation, Start = time };
            _open[instance] = track;
            _file.Structures.Add(track);
        }

        internal void Sample(float time)
        {
            List<GameObject>? removed = null;
            foreach (KeyValuePair<GameObject, string> pair in _watched)
            {
                bool present = pair.Key != null && pair.Key.activeInHierarchy;
                if (!present && _open.TryGetValue(pair.Key!, out StructureTrack track))
                {
                    track.End = time;
                    _open.Remove(pair.Key!);
                }
                if (pair.Key == null) (removed ??= new List<GameObject>()).Add(pair.Key!);
                else if (present) Open(pair.Key, pair.Value, time);
            }
            if (removed != null) foreach (GameObject instance in removed) _watched.Remove(instance);
        }

        internal void Finish()
        {
            if (Active == this) Active = null;
            foreach (StructureTrack track in _file.Structures)
                cameraToolsPlugin.TemplateLogger.LogInfo($"[StructureRecorder] {track.Name} at {track.Position} from {track.Start:F2}s to {(float.IsPositiveInfinity(track.End) ? "the end" : track.End.ToString("F2") + "s")}");
        }
    }
}
