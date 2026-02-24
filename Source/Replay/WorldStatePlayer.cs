using System.Collections.Generic;
using UnityEngine;

namespace cameraTools.Replay
{
    public class WorldStatePlayer
    {
        private readonly List<WorldEvent> _events;
        private readonly GhostManager _ghostManager;
        private int _eventIndex;

        // Real objects hidden during playback (Created events for objects still in world)
        private readonly Dictionary<ZDOID, ZNetView> _hiddenObjects = new Dictionary<ZDOID, ZNetView>();

        // Real objects whose visual state was overridden
        private readonly Dictionary<ZDOID, OriginalState> _overriddenObjects = new Dictionary<ZDOID, OriginalState>();

        // Lookup for real objects
        private readonly Dictionary<ZDOID, ZNetView> _zdoidLookup = new Dictionary<ZDOID, ZNetView>();

        // Track which world ghosts exist
        private readonly HashSet<ZDOID> _activeWorldGhosts = new HashSet<ZDOID>();

        // Pre-categorized ZDOIDs
        private readonly HashSet<ZDOID> _destroyedCategory = new HashSet<ZDOID>();  // existed before, destroyed during recording
        private readonly HashSet<ZDOID> _createdCategory = new HashSet<ZDOID>();    // created during recording, still exists

        private static readonly int Hash_State = Animator.StringToHash("state");

        private struct OriginalState
        {
            public float HealthFraction;
            public int State;
        }

        public WorldStatePlayer(List<WorldEvent> events, GhostManager ghostManager)
        {
            _events = events;
            _ghostManager = ghostManager;
        }

        public void Initialize()
        {
            _eventIndex = 0;
            _hiddenObjects.Clear();
            _overriddenObjects.Clear();
            _activeWorldGhosts.Clear();
            _destroyedCategory.Clear();
            _createdCategory.Clear();

            // Build real-object lookup
            _zdoidLookup.Clear();
            if (ZNetScene.instance != null)
            {
                foreach (var kvp in ZNetScene.instance.m_instances)
                {
                    if (kvp.Value != null && kvp.Value)
                        _zdoidLookup[kvp.Key.m_uid] = kvp.Value;
                }
            }

            // Categorize events by ZDOID
            var hasCreated = new HashSet<ZDOID>();
            var hasDestroyed = new HashSet<ZDOID>();

            foreach (var evt in _events)
            {
                var zdoid = new ZDOID(evt.ZdoUserID, evt.ZdoID);
                if (evt.Type == WorldEventType.Created)
                    hasCreated.Add(zdoid);
                else if (evt.Type == WorldEventType.Destroyed)
                    hasDestroyed.Add(zdoid);
            }

            // Destroyed category: has Destroyed, no Created, real object missing
            foreach (var zdoid in hasDestroyed)
            {
                bool inCreated = hasCreated.Contains(zdoid);
                bool inWorld = _zdoidLookup.ContainsKey(zdoid);
                if (!inCreated && !inWorld)
                    _destroyedCategory.Add(zdoid);
                else
                    cameraToolsPlugin.TemplateLogger.LogInfo($"[WorldStatePlayer] Destroyed zdoid={zdoid.UserID}:{zdoid.ID} skipped category: inCreated={inCreated} inWorld={inWorld}");
            }

            cameraToolsPlugin.TemplateLogger.LogInfo($"[WorldStatePlayer] Init: {_events.Count} events, {hasCreated.Count} created, {hasDestroyed.Count} destroyed, {_destroyedCategory.Count} in destroyed category, {_zdoidLookup.Count} objects in world");

            // Created category: has Created, no Destroyed → hide real object
            foreach (var zdoid in hasCreated)
            {
                if (!hasDestroyed.Contains(zdoid) && _zdoidLookup.TryGetValue(zdoid, out var nview) && nview != null && nview)
                {
                    _createdCategory.Add(zdoid);
                    nview.gameObject.SetActive(false);
                    _hiddenObjects[zdoid] = nview;
                }
            }

            // Spawn ghosts for destroyed category at their first event state
            foreach (var evt in _events)
            {
                var zdoid = new ZDOID(evt.ZdoUserID, evt.ZdoID);
                if (_destroyedCategory.Contains(zdoid) && !_activeWorldGhosts.Contains(zdoid))
                {
                    cameraToolsPlugin.TemplateLogger.LogInfo($"[WorldStatePlayer] Spawning ghost for destroyed piece: zdoid={zdoid.UserID}:{zdoid.ID} prefabHash={evt.PrefabHash} pos={evt.Position}");
                    var rot = Quaternion.Euler(evt.Rotation);
                    var ghost = _ghostManager.GetOrCreateWorldGhost(evt.ZdoUserID, evt.ZdoID, evt.PrefabHash, evt.Position, rot);
                    if (ghost != null)
                    {
                        _activeWorldGhosts.Add(zdoid);
                        ApplyVisualState(ghost, evt.HealthFraction, evt.State);
                        cameraToolsPlugin.TemplateLogger.LogInfo($"[WorldStatePlayer] Ghost spawned successfully");
                    }
                    else
                    {
                        cameraToolsPlugin.TemplateLogger.LogWarning($"[WorldStatePlayer] Ghost spawn FAILED for prefabHash={evt.PrefabHash}");
                    }
                }
            }
        }

        public void UpdatePlayback(float currentTime)
        {
            while (_eventIndex < _events.Count && _events[_eventIndex].Time <= currentTime)
            {
                ProcessEvent(_events[_eventIndex]);
                _eventIndex++;
            }
        }

        public void SeekTo(float time)
        {
            // Reset all state
            foreach (var zdoid in _activeWorldGhosts)
                _ghostManager.RemoveWorldGhost(zdoid.UserID, zdoid.ID);
            _activeWorldGhosts.Clear();

            // Unhide hidden objects
            foreach (var kvp in _hiddenObjects)
            {
                if (kvp.Value != null && kvp.Value)
                    kvp.Value.gameObject.SetActive(true);
            }
            _hiddenObjects.Clear();

            // Restore overridden objects
            foreach (var kvp in _overriddenObjects)
            {
                if (_zdoidLookup.TryGetValue(kvp.Key, out var nview) && nview != null && nview)
                    ApplyVisualState(nview.gameObject, kvp.Value.HealthFraction, kvp.Value.State);
            }
            _overriddenObjects.Clear();

            // Re-initialize and replay to target time
            Initialize();
            _eventIndex = 0;
            UpdatePlayback(time);
        }

        public void RestoreAll()
        {
            // Destroy world ghosts
            foreach (var zdoid in _activeWorldGhosts)
                _ghostManager.RemoveWorldGhost(zdoid.UserID, zdoid.ID);
            _activeWorldGhosts.Clear();

            // Reactivate hidden objects
            foreach (var kvp in _hiddenObjects)
            {
                if (kvp.Value != null && kvp.Value)
                    kvp.Value.gameObject.SetActive(true);
            }
            _hiddenObjects.Clear();

            // Restore original visual states
            foreach (var kvp in _overriddenObjects)
            {
                if (_zdoidLookup.TryGetValue(kvp.Key, out var nview) && nview != null && nview)
                    ApplyVisualState(nview.gameObject, kvp.Value.HealthFraction, kvp.Value.State);
            }
            _overriddenObjects.Clear();
        }

        private void ProcessEvent(WorldEvent evt)
        {
            var zdoid = new ZDOID(evt.ZdoUserID, evt.ZdoID);
            var rot = Quaternion.Euler(evt.Rotation);

            switch (evt.Type)
            {
                case WorldEventType.Created:
                {
                    var ghost = _ghostManager.GetOrCreateWorldGhost(evt.ZdoUserID, evt.ZdoID, evt.PrefabHash, evt.Position, rot);
                    if (ghost != null)
                    {
                        _activeWorldGhosts.Add(zdoid);
                        ApplyVisualState(ghost, evt.HealthFraction, evt.State);

                        // Play placement VFX
                        var piece = ghost.GetComponent<Piece>();
                        if (piece != null && piece.m_placeEffect != null)
                            piece.m_placeEffect.Create(evt.Position, rot);
                    }
                    break;
                }

                case WorldEventType.Destroyed:
                {
                    // Play destruction VFX from ghost or prefab
                    var ghostObj = _ghostManager.GetWorldGhostObject(evt.ZdoUserID, evt.ZdoID);
                    if (ghostObj != null)
                    {
                        var wnt = ghostObj.GetComponent<WearNTear>();
                        if (wnt != null && wnt.m_destroyedEffect != null)
                            wnt.m_destroyedEffect.Create(evt.Position, rot);
                    }

                    _ghostManager.RemoveWorldGhost(evt.ZdoUserID, evt.ZdoID);
                    _activeWorldGhosts.Remove(zdoid);
                    break;
                }

                case WorldEventType.StateChanged:
                {
                    // Update ghost if it exists
                    var ghostObj = _ghostManager.GetWorldGhostObject(evt.ZdoUserID, evt.ZdoID);
                    if (ghostObj != null)
                    {
                        ApplyVisualState(ghostObj, evt.HealthFraction, evt.State);
                    }
                    else if (_zdoidLookup.TryGetValue(zdoid, out var nview) && nview != null && nview)
                    {
                        // Override real object — cache original state on first override
                        if (!_overriddenObjects.ContainsKey(zdoid))
                        {
                            var zdo = nview.GetZDO();
                            var wnt = nview.GetComponent<WearNTear>();
                            float origHealth = 1f;
                            if (wnt != null && zdo != null && wnt.m_health > 0f)
                                origHealth = Mathf.Clamp01(zdo.GetFloat(ZDOVars.s_health, wnt.m_health) / wnt.m_health);

                            int origState = zdo != null ? zdo.GetInt(ZDOVars.s_state, 0) : 0;

                            _overriddenObjects[zdoid] = new OriginalState
                            {
                                HealthFraction = origHealth,
                                State = origState
                            };
                        }

                        ApplyVisualState(nview.gameObject, evt.HealthFraction, evt.State);
                    }
                    break;
                }
            }
        }

        private static void ApplyVisualState(GameObject go, float healthFraction, int state)
        {
            // WearNTear visual tier
            var wnt = go.GetComponent<WearNTear>();
            if (wnt != null)
            {
                if (wnt.m_new != null) wnt.m_new.SetActive(healthFraction > 0.75f);
                if (wnt.m_worn != null) wnt.m_worn.SetActive(healthFraction > 0.25f && healthFraction <= 0.75f);
                if (wnt.m_broken != null) wnt.m_broken.SetActive(healthFraction <= 0.25f);
            }

            // Door state
            var animator = go.GetComponentInChildren<Animator>();
            if (animator != null)
                animator.SetInteger(Hash_State, state);
        }
    }
}
