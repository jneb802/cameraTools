using System.Collections.Generic;
using UnityEngine;

namespace cameraTools.Replay
{
    /// <summary>
    /// Plays the recorded world objects back as real ZDOs in the replay session. Every object is owned by
    /// a peer that is never connected, so the game shows each object the way a client shows another
    /// player's objects: scripts render the ZDO data, but no owner-only simulation (decay, growth,
    /// smelting, spawning) runs. Seeking sets every object to its recorded state at that time.
    /// </summary>
    internal sealed class SceneTimelinePlayer
    {
        /// <summary>The absent owner of every replayed object.</summary>
        internal const long ReplayOwner = 0x7265706c6179L;
        private const float FrequentChangeSeconds = 1f;

        private readonly List<SceneObject> _objects = new();
        private readonly HashSet<(long, uint)> _ids = new();
        private readonly int[] _applied;
        private readonly List<(float Time, int Index)> _events = new();
        private readonly ZPackage _package = new();
        private int _cursor;
        private float _time = -1f;
        internal readonly Dictionary<string, int> Missing = new();

        internal SceneTimelinePlayer(ReplayFile replay)
        {
            foreach (SceneObject item in replay.Scene)
            {
                if (item.States.Count == 0) continue;
                if (ZNetScene.instance.GetPrefab(item.Prefab) == null)
                {
                    // The mod that added this prefab is not loaded: leave the object out.
                    string name = replay.Info.PrefabName(item.Prefab);
                    Missing[name] = Missing.TryGetValue(name, out int count) ? count + 1 : 1;
                    continue;
                }
                int index = _objects.Count;
                _objects.Add(item);
                _ids.Add((item.User, item.Id));
                if (item.Created > 0f) _events.Add((item.Created, index));
                foreach (SceneState state in item.States)
                    if (state.Time > item.Created) _events.Add((state.Time, index));
                if (!float.IsPositiveInfinity(item.Destroyed)) _events.Add((item.Destroyed, index));
            }
            _events.Sort((a, b) => a.Time.CompareTo(b.Time));
            _applied = new int[_objects.Count];
            for (int index = 0; index < _applied.Length; index++) _applied[index] = -1;
            int skipped = 0;
            foreach (int count in Missing.Values) skipped += count;
            cameraToolsPlugin.TemplateLogger.LogInfo($"[SceneTimelinePlayer] {_objects.Count} objects, {_events.Count} changes, {skipped} objects skipped for {Missing.Count} unavailable prefabs");
            foreach (KeyValuePair<string, int> missing in Missing)
                cameraToolsPlugin.TemplateLogger.LogWarning($"[SceneTimelinePlayer] Prefab not loaded, {missing.Value} objects skipped: {missing.Key}");
        }

        internal int ObjectCount => _objects.Count;

        /// <summary>True when the object is replayed as a real game object rather than as a copy.</summary>
        internal bool Contains(long user, uint id) => _ids.Contains((user, id));

        /// <summary>Moves a replayed object to a recorded per-frame pose. The game moves objects owned by
        /// another peer toward their data position and rotation every frame, so this keeps them smooth.</summary>
        internal void Move(long user, uint id, Vector3 position, Quaternion rotation)
        {
            ZDO? zdo = ZDOMan.instance.GetZDO(new ZDOID(user, id));
            if (zdo == null) return;
            zdo.InternalSetPosition(position);
            // Set the field directly: SetRotation raises the data revision, which makes objects reload their data.
            zdo.m_rotation = rotation.eulerAngles;
        }

        internal void Update(float time)
        {
            if (time < _time) { Seek(time); return; }
            _time = time;
            while (_cursor < _events.Count && _events[_cursor].Time <= time)
            {
                Reconcile(_events[_cursor].Index, time, false);
                _cursor++;
            }
        }

        internal void Seek(float time)
        {
            _time = time;
            for (int index = 0; index < _objects.Count; index++) Reconcile(index, time, true);
            int low = 0, high = _events.Count;
            while (low < high)
            {
                int middle = (low + high) / 2;
                if (_events[middle].Time <= time) low = middle + 1; else high = middle;
            }
            _cursor = low;
        }

        private void Reconcile(int index, float time, bool seeking)
        {
            SceneObject item = _objects[index];
            int desired = item.Created <= time && time < item.Destroyed ? StateAt(item.States, time) : -1;
            int applied = _applied[index];
            if (desired == applied) return;
            _applied[index] = desired;
            ZDOID id = new ZDOID(item.User, item.Id);
            ZDO? zdo = ZDOMan.instance.GetZDO(id);
            if (desired < 0)
            {
                if (zdo != null) ZDOMan.instance.HandleDestroyedZDO(id);
                return;
            }
            SceneState state = item.States[desired];
            if (zdo == null)
            {
                zdo = ZDOMan.instance.CreateNewZDO(id, state.Position, item.Prefab);
                Load(zdo, state);
                return;
            }
            ZDOExtraData.Release(zdo, id);
            Load(zdo, state);
            bool frequent = desired > 0 && state.Time - item.States[desired - 1].Time < FrequentChangeSeconds;
            Refresh(zdo, seeking || !frequent);
        }

        private static int StateAt(List<SceneState> states, float time)
        {
            int low = 0, high = states.Count - 1, result = 0;
            while (low <= high)
            {
                int middle = (low + high) / 2;
                if (states[middle].Time <= time) { result = middle; low = middle + 1; }
                else high = middle - 1;
            }
            return result;
        }

        private void Load(ZDO zdo, SceneState state)
        {
            zdo.SetOwnerInternal(ReplayOwner);
            zdo.InternalSetPosition(state.Position);
            zdo.m_rotation = Vector3.zero;
            _package.Load(state.Data);
            zdo.Deserialize(_package);
            zdo.DataRevision++;
        }

        // Many objects read their ZDO only when created or when the owner sends an RPC, which no
        // replay sends. Rebuild those objects so they show the new data. Objects that change often
        // (fires, smelters, terrain edits) read their ZDO continuously, so they keep their instance.
        private static void Refresh(ZDO zdo, bool rebuild)
        {
            if (!ZNetScene.instance.m_instances.TryGetValue(zdo, out ZNetView view) || view == null) return;
            WearNTear wear = view.GetComponent<WearNTear>();
            if (wear != null) { wear.UpdateVisual(false); return; }
            if (rebuild && view.GetComponent<TerrainComp>() == null) ZNetScene.instance.Destroy(view.gameObject);
        }
    }
}
