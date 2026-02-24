using System.Collections.Generic;
using UnityEngine;

namespace cameraTools.Replay
{
    public class WorldStateRecorder
    {
        private readonly HashSet<ZDOID> _trackedZdos = new HashSet<ZDOID>();
        private readonly HashSet<ZDOID> _nonPieceZdos = new HashSet<ZDOID>();
        private readonly Dictionary<ZDOID, uint> _lastRevision = new Dictionary<ZDOID, uint>();
        private readonly Dictionary<ZDOID, float> _lastHealth = new Dictionary<ZDOID, float>();
        private readonly Dictionary<ZDOID, int> _lastState = new Dictionary<ZDOID, int>();
        private readonly Dictionary<ZDOID, ZNetView> _zdoidToNview = new Dictionary<ZDOID, ZNetView>();
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
                _zdoidToNview[zdoid] = nview;

                if (nview.GetComponent<Piece>() == null)
                {
                    _nonPieceZdos.Add(zdoid);
                    continue;
                }

                _trackedZdos.Add(zdoid);

                var zdo = kvp.Key;
                _lastRevision[zdoid] = zdo.DataRevision;

                float healthFraction = GetHealthFraction(nview, zdo);
                int state = zdo.GetInt(ZDOVars.s_state, 0);

                _lastHealth[zdoid] = healthFraction;
                _lastState[zdoid] = state;
            }
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

                if (_trackedZdos.Contains(zdoid))
                {
                    // Check for modifications via DataRevision
                    if (!_lastRevision.TryGetValue(zdoid, out uint lastRev) || zdo.DataRevision != lastRev)
                    {
                        _lastRevision[zdoid] = zdo.DataRevision;

                        float healthFraction = GetHealthFraction(nview, zdo);
                        int state = zdo.GetInt(ZDOVars.s_state, 0);

                        bool healthChanged = !_lastHealth.TryGetValue(zdoid, out float lastH) || !Mathf.Approximately(healthFraction, lastH);
                        bool stateChanged = !_lastState.TryGetValue(zdoid, out int lastS) || state != lastS;

                        if (healthChanged || stateChanged)
                        {
                            _lastHealth[zdoid] = healthFraction;
                            _lastState[zdoid] = state;

                            _events.Add(new WorldEvent
                            {
                                Time = time,
                                Type = WorldEventType.StateChanged,
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
                }
                else if (!_nonPieceZdos.Contains(zdoid))
                {
                    // New ZDO - check if it has a Piece component
                    _zdoidToNview[zdoid] = nview;

                    if (nview.GetComponent<Piece>() == null)
                    {
                        _nonPieceZdos.Add(zdoid);
                        continue;
                    }

                    _trackedZdos.Add(zdoid);
                    _lastRevision[zdoid] = zdo.DataRevision;

                    float healthFraction = GetHealthFraction(nview, zdo);
                    int state = zdo.GetInt(ZDOVars.s_state, 0);

                    _lastHealth[zdoid] = healthFraction;
                    _lastState[zdoid] = state;

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

            // Check for destroyed ZDOs
            var destroyed = new List<ZDOID>();
            foreach (var zdoid in _trackedZdos)
            {
                if (!currentFrameZdos.Contains(zdoid))
                    destroyed.Add(zdoid);
            }

            foreach (var zdoid in destroyed)
            {
                _trackedZdos.Remove(zdoid);

                _lastHealth.TryGetValue(zdoid, out float lastHealth);
                _lastState.TryGetValue(zdoid, out int lastState);

                // Try to get last known prefab hash and position
                int prefabHash = 0;
                var position = Vector3.zero;
                var rotation = Vector3.zero;

                if (_zdoidToNview.TryGetValue(zdoid, out var nview) && nview != null && nview)
                {
                    position = nview.transform.position;
                    rotation = nview.transform.eulerAngles;
                    var zdo = nview.GetZDO();
                    if (zdo != null)
                        prefabHash = zdo.GetPrefab();
                }

                _events.Add(new WorldEvent
                {
                    Time = time,
                    Type = WorldEventType.Destroyed,
                    ZdoUserID = zdoid.UserID,
                    ZdoID = zdoid.ID,
                    PrefabHash = prefabHash,
                    Position = position,
                    Rotation = rotation,
                    HealthFraction = lastHealth,
                    State = lastState
                });

                _lastRevision.Remove(zdoid);
                _lastHealth.Remove(zdoid);
                _lastState.Remove(zdoid);
                _zdoidToNview.Remove(zdoid);
            }
        }

        public List<WorldEvent> GetEvents() => _events;

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
