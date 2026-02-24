using System.Collections.Generic;
using UnityEngine;

namespace cameraTools.Replay
{
    public class GhostManager
    {
        // Key: "userID:objectID" string for easy lookup
        private readonly Dictionary<long, Dictionary<uint, GhostInstance>> _ghosts =
            new Dictionary<long, Dictionary<uint, GhostInstance>>();

        private class GhostInstance
        {
            public GameObject GameObject;
            public Animator? Animator;

            public GhostInstance(GameObject go, Animator? animator)
            {
                GameObject = go;
                Animator = animator;
            }
        }

        // Animator parameter hashes (no salt needed when driving Animator directly)
        private static readonly int Hash_ForwardSpeed = Animator.StringToHash("forward_speed");
        private static readonly int Hash_SidewaySpeed = Animator.StringToHash("sideway_speed");
        private static readonly int Hash_TurnSpeed = Animator.StringToHash("turn_speed");
        private static readonly int Hash_InWater = Animator.StringToHash("inWater");
        private static readonly int Hash_OnGround = Animator.StringToHash("onGround");
        private static readonly int Hash_Encumbered = Animator.StringToHash("encumbered");
        private static readonly int Hash_Flying = Animator.StringToHash("flying");
        private static readonly int Hash_Falling = Animator.StringToHash("falling");
        private static readonly int Hash_Crouching = Animator.StringToHash("crouching");
        private static readonly int Hash_Blocking = Animator.StringToHash("blocking");
        private static readonly int Hash_StateF = Animator.StringToHash("statef");
        private static readonly int Hash_StateI = Animator.StringToHash("statei");

        public GameObject? GetOrCreateGhost(long userID, uint objectID, int prefabHash, Vector3 position, Quaternion rotation)
        {
            if (_ghosts.TryGetValue(userID, out var innerDict) &&
                innerDict.TryGetValue(objectID, out var existing))
            {
                if (existing.GameObject != null)
                    return existing.GameObject;
                // Ghost was destroyed externally, remove stale entry
                innerDict.Remove(objectID);
            }

            // Look up the prefab
            var prefab = ZNetScene.instance?.GetPrefab(prefabHash);
            if (prefab == null)
            {
                cameraToolsPlugin.TemplateLogger.LogWarning($"Prefab not found for hash {prefabHash}");
                return null;
            }

            // Spawn ghost without ZDO registration
            ZNetView.m_forceDisableInit = true;
            var ghost = Object.Instantiate(prefab, position, rotation);
            ZNetView.m_forceDisableInit = false;

            // Disable AI, sync, physics, drops
            DisableComponent<BaseAI>(ghost);
            DisableComponent<MonsterAI>(ghost);
            DisableComponent<AnimalAI>(ghost);
            DisableComponent<ZSyncTransform>(ghost);
            DisableComponent<ZSyncAnimation>(ghost);
            DisableComponent<CharacterDrop>(ghost);

            // Make kinematic and disable colliders
            var rb = ghost.GetComponent<Rigidbody>();
            if (rb != null)
                rb.isKinematic = true;

            foreach (var collider in ghost.GetComponentsInChildren<Collider>())
                collider.enabled = false;

            var animator = ghost.GetComponentInChildren<Animator>();

            // Store
            if (!_ghosts.TryGetValue(userID, out innerDict))
            {
                innerDict = new Dictionary<uint, GhostInstance>();
                _ghosts[userID] = innerDict;
            }
            innerDict[objectID] = new GhostInstance(ghost, animator);

            return ghost;
        }

        public void UpdateGhost(long userID, uint objectID, Vector3 position, Quaternion rotation,
            float forwardSpeed, float sidewaySpeed, float turnSpeed, byte animBools, int stateF, int stateI)
        {
            if (!_ghosts.TryGetValue(userID, out var innerDict) ||
                !innerDict.TryGetValue(objectID, out var ghost))
                return;

            if (ghost.GameObject == null)
                return;

            ghost.GameObject.transform.position = position;
            ghost.GameObject.transform.rotation = rotation;

            if (ghost.Animator == null)
                return;

            ghost.Animator.SetFloat(Hash_ForwardSpeed, forwardSpeed);
            ghost.Animator.SetFloat(Hash_SidewaySpeed, sidewaySpeed);
            ghost.Animator.SetFloat(Hash_TurnSpeed, turnSpeed);
            ghost.Animator.SetBool(Hash_InWater, (animBools & (1 << 0)) != 0);
            ghost.Animator.SetBool(Hash_OnGround, (animBools & (1 << 1)) != 0);
            ghost.Animator.SetBool(Hash_Encumbered, (animBools & (1 << 2)) != 0);
            ghost.Animator.SetBool(Hash_Flying, (animBools & (1 << 3)) != 0);
            ghost.Animator.SetBool(Hash_Falling, (animBools & (1 << 4)) != 0);
            ghost.Animator.SetBool(Hash_Crouching, (animBools & (1 << 5)) != 0);
            ghost.Animator.SetBool(Hash_Blocking, (animBools & (1 << 6)) != 0);
            ghost.Animator.SetInteger(Hash_StateF, stateF);
            ghost.Animator.SetInteger(Hash_StateI, stateI);
        }

        public void FireTrigger(long userID, uint objectID, string triggerName)
        {
            if (!_ghosts.TryGetValue(userID, out var innerDict) ||
                !innerDict.TryGetValue(objectID, out var ghost))
                return;

            if (ghost.Animator != null)
                ghost.Animator.SetTrigger(triggerName);
        }

        public void RemoveGhost(long userID, uint objectID)
        {
            if (!_ghosts.TryGetValue(userID, out var innerDict))
                return;

            if (innerDict.TryGetValue(objectID, out var ghost))
            {
                if (ghost.GameObject != null)
                    Object.Destroy(ghost.GameObject);
                innerDict.Remove(objectID);
            }

            if (innerDict.Count == 0)
                _ghosts.Remove(userID);
        }

        public void DestroyAll()
        {
            foreach (var innerDict in _ghosts.Values)
            {
                foreach (var ghost in innerDict.Values)
                {
                    if (ghost.GameObject != null)
                        Object.Destroy(ghost.GameObject);
                }
            }
            _ghosts.Clear();
        }

        private static void DisableComponent<T>(GameObject go) where T : MonoBehaviour
        {
            var comp = go.GetComponent<T>();
            if (comp != null)
                comp.enabled = false;
        }
    }
}
