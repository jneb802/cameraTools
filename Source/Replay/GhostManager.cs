using System.Collections.Generic;
using UnityEngine;

namespace cameraTools.Replay
{
    public class GhostManager
    {
        // Key: "userID:objectID" string for easy lookup
        private readonly Dictionary<long, Dictionary<uint, GhostInstance>> _ghosts =
            new Dictionary<long, Dictionary<uint, GhostInstance>>();

        private readonly HashSet<(long, uint)> _worldGhostKeys = new HashSet<(long, uint)>();

        private class GhostInstance
        {
            public GameObject GameObject;
            public Animator? Animator;
            public VisEquipment? VisEquipment;

            public GhostInstance(GameObject go, Animator? animator, VisEquipment? visEquipment)
            {
                GameObject = go;
                Animator = animator;
                VisEquipment = visEquipment;
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
            var visEquipment = ghost.GetComponent<VisEquipment>();

            // Store
            if (!_ghosts.TryGetValue(userID, out innerDict))
            {
                innerDict = new Dictionary<uint, GhostInstance>();
                _ghosts[userID] = innerDict;
            }
            innerDict[objectID] = new GhostInstance(ghost, animator, visEquipment);

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

        public void UpdateEquipment(long userID, uint objectID, EntitySnapshot snapshot)
        {
            if (!snapshot.HasEquipment)
                return;

            if (!_ghosts.TryGetValue(userID, out var innerDict) ||
                !innerDict.TryGetValue(objectID, out var ghost))
                return;

            if (ghost.VisEquipment == null)
                return;

            var ve = ghost.VisEquipment;
            ve.m_leftItem = snapshot.LeftItem ?? "";
            ve.m_leftItemVariant = snapshot.LeftItemVariant;
            ve.m_rightItem = snapshot.RightItem ?? "";
            ve.m_chestItem = snapshot.ChestItem ?? "";
            ve.m_legItem = snapshot.LegItem ?? "";
            ve.m_helmetItem = snapshot.HelmetItem ?? "";
            ve.m_shoulderItem = snapshot.ShoulderItem ?? "";
            ve.m_shoulderItemVariant = snapshot.ShoulderItemVariant;
            ve.m_utilityItem = snapshot.UtilityItem ?? "";
            ve.m_trinketItem = snapshot.TrinketItem ?? "";
            ve.m_beardItem = snapshot.BeardItem ?? "";
            ve.m_hairItem = snapshot.HairItem ?? "";
            ve.m_leftBackItem = snapshot.LeftBackItem ?? "";
            ve.m_leftBackItemVariant = snapshot.LeftBackItemVariant;
            ve.m_rightBackItem = snapshot.RightBackItem ?? "";
        }

        public void FireTrigger(long userID, uint objectID, string triggerName)
        {
            if (!_ghosts.TryGetValue(userID, out var innerDict) ||
                !innerDict.TryGetValue(objectID, out var ghost))
                return;

            if (ghost.Animator != null)
                ghost.Animator.SetTrigger(triggerName);
        }

        public GameObject? GetOrCreateWorldGhost(long userID, uint objectID, int prefabHash, Vector3 position, Quaternion rotation)
        {
            var key = (userID, objectID);

            if (_ghosts.TryGetValue(userID, out var innerDict) &&
                innerDict.TryGetValue(objectID, out var existing))
            {
                if (existing.GameObject != null)
                    return existing.GameObject;
                innerDict.Remove(objectID);
            }

            var prefab = ZNetScene.instance?.GetPrefab(prefabHash);
            if (prefab == null)
                return null;

            ZNetView.m_forceDisableInit = true;
            var ghost = Object.Instantiate(prefab, position, rotation);
            ZNetView.m_forceDisableInit = false;

            // Disable sync components
            DisableComponent<ZSyncTransform>(ghost);
            DisableComponent<ZSyncAnimation>(ghost);

            // Disable WearNTear behavior but keep component for VFX refs
            var wearNTear = ghost.GetComponent<WearNTear>();
            if (wearNTear != null)
                wearNTear.enabled = false;

            // Make kinematic and disable colliders
            var rb = ghost.GetComponent<Rigidbody>();
            if (rb != null)
                rb.isKinematic = true;

            foreach (var collider in ghost.GetComponentsInChildren<Collider>())
                collider.enabled = false;

            var animator = ghost.GetComponentInChildren<Animator>();
            var visEquipment = ghost.GetComponent<VisEquipment>();

            if (!_ghosts.TryGetValue(userID, out innerDict))
            {
                innerDict = new Dictionary<uint, GhostInstance>();
                _ghosts[userID] = innerDict;
            }
            innerDict[objectID] = new GhostInstance(ghost, animator, visEquipment);
            _worldGhostKeys.Add(key);

            return ghost;
        }

        public GameObject? GetWorldGhostObject(long userID, uint objectID)
        {
            if (_ghosts.TryGetValue(userID, out var innerDict) &&
                innerDict.TryGetValue(objectID, out var ghost) &&
                ghost.GameObject != null)
            {
                return ghost.GameObject;
            }
            return null;
        }

        public void RemoveWorldGhost(long userID, uint objectID)
        {
            _worldGhostKeys.Remove((userID, objectID));
            RemoveGhost(userID, objectID);
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
            _worldGhostKeys.Clear();
        }

        private static void DisableComponent<T>(GameObject go) where T : MonoBehaviour
        {
            var comp = go.GetComponent<T>();
            if (comp != null)
                comp.enabled = false;
        }
    }
}
