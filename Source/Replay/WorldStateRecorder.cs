using System.Collections.Generic;
using UnityEngine;

namespace cameraTools.Replay
{
    public class WorldStateRecorder
    {
        private struct TrackedPiece
        {
            public int PrefabHash;
            public Vector3 Position;
            public Vector3 Rotation;
            public uint LastRevision;
            public float LastHealth;
            public int LastState;
        }

        private readonly Dictionary<ZDOID, TrackedPiece> _trackedPieces = new Dictionary<ZDOID, TrackedPiece>();
        private readonly HashSet<ZDOID> _nonPieceZdos = new HashSet<ZDOID>();
        private readonly List<WorldEvent> _events = new List<WorldEvent>();

        public void CaptureBaseline()
        {
            if (ZNetScene.instance == null)
                return;

            foreach (var kvp in ZNetScene.instance.m_instances)
            {
                var nview = kvp.Value;
                if (nview == null || !nview)
                    continue;

                var zdoid = kvp.Key.m_uid;

                if (nview.GetComponent<Piece>() == null)
                {
                    _nonPieceZdos.Add(zdoid);
                    continue;
                }

                var zdo = kvp.Key;
                _trackedPieces[zdoid] = new TrackedPiece
                {
                    PrefabHash = zdo.GetPrefab(),
                    Position = nview.transform.position,
                    Rotation = nview.transform.eulerAngles,
                    LastRevision = zdo.DataRevision,
                    LastHealth = GetHealthFraction(nview, zdo),
                    LastState = zdo.GetInt(ZDOVars.s_state, 0)
                };
            }

            cameraToolsPlugin.TemplateLogger.LogInfo($"[WorldStateRecorder] Baseline captured: {_trackedPieces.Count} pieces tracked");
        }

        public void ScanFrame(float time)
        {
            if (ZNetScene.instance == null || ZDOMan.instance == null)
                return;

            // Build current frame set from ZDOMan
            var currentFrameZdos = new HashSet<ZDOID>();
            foreach (var kvp in ZDOMan.instance.m_objectsByID)
                currentFrameZdos.Add(kvp.Key);

            // Check for new and modified ZDOs
            foreach (var kvp in ZNetScene.instance.m_instances)
            {
                var nview = kvp.Value;
                if (nview == null || !nview)
                    continue;

                var zdoid = kvp.Key.m_uid;
                var zdo = kvp.Key;

                if (_trackedPieces.TryGetValue(zdoid, out var tracked))
                {
                    // Check for modifications via DataRevision
                    if (zdo.DataRevision != tracked.LastRevision)
                    {
                        float healthFraction = GetHealthFraction(nview, zdo);
                        int state = zdo.GetInt(ZDOVars.s_state, 0);

                        bool healthChanged = !Mathf.Approximately(healthFraction, tracked.LastHealth);
                        bool stateChanged = state != tracked.LastState;

                        // Always update cached revision and position
                        tracked.LastRevision = zdo.DataRevision;
                        tracked.Position = nview.transform.position;
                        tracked.Rotation = nview.transform.eulerAngles;

                        if (healthChanged || stateChanged)
                        {
                            tracked.LastHealth = healthFraction;
                            tracked.LastState = state;

                            _events.Add(new WorldEvent
                            {
                                Time = time,
                                Type = WorldEventType.StateChanged,
                                ZdoUserID = zdoid.UserID,
                                ZdoID = zdoid.ID,
                                PrefabHash = tracked.PrefabHash,
                                Position = nview.transform.position,
                                Rotation = nview.transform.eulerAngles,
                                HealthFraction = healthFraction,
                                State = state
                            });
                        }

                        _trackedPieces[zdoid] = tracked;
                    }
                }
                else if (!_nonPieceZdos.Contains(zdoid))
                {
                    // New ZDO - check if it has a Piece component
                    if (nview.GetComponent<Piece>() == null)
                    {
                        _nonPieceZdos.Add(zdoid);
                        continue;
                    }

                    float healthFraction = GetHealthFraction(nview, zdo);
                    int state = zdo.GetInt(ZDOVars.s_state, 0);

                    _trackedPieces[zdoid] = new TrackedPiece
                    {
                        PrefabHash = zdo.GetPrefab(),
                        Position = nview.transform.position,
                        Rotation = nview.transform.eulerAngles,
                        LastRevision = zdo.DataRevision,
                        LastHealth = healthFraction,
                        LastState = state
                    };

                    _events.Add(new WorldEvent
                    {
                        Time = time,
                        Type = WorldEventType.Created,
                        ZdoUserID = zdoid.UserID,
                        ZdoID = zdoid.ID,
                        PrefabHash = zdo.GetPrefab(),
                        Position = nview.transform.position,
                        Rotation = nview.transform.eulerAngles,
                        HealthFraction = healthFraction,
                        State = state
                    });
                }
            }

            // Check for destroyed ZDOs — use cached data
            var destroyed = new List<ZDOID>();
            foreach (var zdoid in _trackedPieces.Keys)
            {
                if (!currentFrameZdos.Contains(zdoid))
                    destroyed.Add(zdoid);
            }

            foreach (var zdoid in destroyed)
            {
                var cached = _trackedPieces[zdoid];
                _trackedPieces.Remove(zdoid);

                cameraToolsPlugin.TemplateLogger.LogInfo($"[WorldStateRecorder] Destroyed: zdoid={zdoid.UserID}:{zdoid.ID} prefabHash={cached.PrefabHash} pos={cached.Position}");

                _events.Add(new WorldEvent
                {
                    Time = time,
                    Type = WorldEventType.Destroyed,
                    ZdoUserID = zdoid.UserID,
                    ZdoID = zdoid.ID,
                    PrefabHash = cached.PrefabHash,
                    Position = cached.Position,
                    Rotation = cached.Rotation,
                    HealthFraction = cached.LastHealth,
                    State = cached.LastState
                });
            }
        }

        public List<WorldEvent> GetEvents()
        {
            int created = 0, destroyed = 0, stateChanged = 0, zeroPrefab = 0;
            foreach (var e in _events)
            {
                switch (e.Type)
                {
                    case WorldEventType.Created: created++; break;
                    case WorldEventType.Destroyed: destroyed++; break;
                    case WorldEventType.StateChanged: stateChanged++; break;
                }
                if (e.PrefabHash == 0) zeroPrefab++;
            }
            cameraToolsPlugin.TemplateLogger.LogInfo($"[WorldStateRecorder] Events summary: {_events.Count} total ({created} created, {destroyed} destroyed, {stateChanged} stateChanged, {zeroPrefab} with prefabHash=0)");
            return _events;
        }

        private static float GetHealthFraction(ZNetView nview, ZDO zdo)
        {
            var wearNTear = nview.GetComponent<WearNTear>();
            if (wearNTear == null)
                return 1f;

            float maxHealth = wearNTear.m_health;
            if (maxHealth <= 0f)
                return 1f;

            float currentHealth = zdo.GetFloat(ZDOVars.s_health, maxHealth);
            return Mathf.Clamp01(currentHealth / maxHealth);
        }
    }
}
