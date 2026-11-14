using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace cameraTools.Replay
{
    public class GhostManager
    {
        [HarmonyPatch(typeof(EnemyHud), nameof(EnemyHud.UpdateHuds))]
        private static class ReplayCharacterHud
        {
            private static void Prefix(EnemyHud __instance)
            {
                if (!ReplaySession.Active || !ReplayPlayer.IsPlaying) return;
                // Replay colliders cannot be hovered. Keep nearby creatures' native level displays
                // available when the user shows the HUD, including displays added by mods.
                foreach (EnemyHud.HudData hud in __instance.m_huds.Values) hud.m_hoverTimer = 0f;
            }
        }
        // Key: "userID:objectID" string for easy lookup
        private readonly Dictionary<long, Dictionary<uint, GhostInstance>> _ghosts =
            new Dictionary<long, Dictionary<uint, GhostInstance>>();

        private readonly HashSet<(long, uint)> _worldGhostKeys = new HashSet<(long, uint)>();
        private readonly HashSet<int> _missing = new();
        private readonly ReplayInfo _info;
        /// <summary>Names of recorded characters and objects whose prefab is not loaded.</summary>
        public readonly SortedSet<string> MissingNames = new();

        public GhostManager(ReplayInfo info) { _info = info; }

        private class GhostInstance
        {
            public GameObject GameObject;
            public Animator? Animator;
            public VisEquipment? VisEquipment;
            public readonly Dictionary<string, Transform> Bones = new();
            public VisualPlayer Visual;
            public bool OwnsObject = true;
            public readonly List<AudioSource> Audio = new();

            public GhostInstance(GameObject go, Animator? animator, VisEquipment? visEquipment)
            {
                GameObject = go;
                Animator = animator;
                VisEquipment = visEquipment;
                Visual = new VisualPlayer(go);
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
                // Usually a creature or item from a mod that is not loaded; it is left out of the replay.
                if (_missing.Add(prefabHash))
                {
                    string name = _info.PrefabName(prefabHash);
                    MissingNames.Add(name);
                    cameraToolsPlugin.TemplateLogger.LogWarning($"Prefab not loaded, left out of the replay: {name}");
                }
                return null;
            }

            GameObject ghost = CreateVisual(prefab, position, rotation);

            var animator = ghost.GetComponentInChildren<Animator>();
            var visEquipment = ghost.GetComponent<VisEquipment>();
            if (!_ghosts.TryGetValue(userID, out innerDict))
            {
                innerDict = new Dictionary<uint, GhostInstance>();
                _ghosts[userID] = innerDict;
            }
            innerDict[objectID] = new GhostInstance(ghost, animator, visEquipment);
            return ghost;
        }

        internal static GameObject CreateVisual(GameObject prefab, Vector3 position, Quaternion rotation)
        {
            bool previousDisable = ZNetView.m_forceDisableInit;
            GameObject ghost;
            GameObject staging = new GameObject("Replay staging");
            staging.SetActive(false);
            ZNetView.m_forceDisableInit = true;
            try
            {
                ghost = Object.Instantiate(prefab, position, rotation, staging.transform);
                RemoveGameplayScripts(ghost);
                foreach (Collider collider in ghost.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
                foreach (Rigidbody body in ghost.GetComponentsInChildren<Rigidbody>(true)) body.isKinematic = true;
                foreach (Animator animation in ghost.GetComponentsInChildren<Animator>(true)) animation.fireEvents = false;
                foreach (AudioSource source in ghost.GetComponentsInChildren<AudioSource>(true))
                { source.playOnAwake = false; source.enabled = false; }
                foreach (ParticleSystem particles in ghost.GetComponentsInChildren<ParticleSystem>(true))
                { ParticleSystem.MainModule main = particles.main; main.playOnAwake = false; particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear); }
                ghost.transform.SetParent(null, true);
            }
            finally
            {
                ZNetView.m_forceDisableInit = previousDisable;
                Object.Destroy(staging);
            }

            return ghost;
        }

        internal void ApplyVisual(EntitySnapshot a, EntitySnapshot b, float fraction)
        {
            if (a.Visual != null && _ghosts.TryGetValue(a.ZdoUserID, out Dictionary<uint, GhostInstance> entries)
                && entries.TryGetValue(a.ZdoID, out GhostInstance ghost))
            {
                ghost.Visual.Apply(a.Visual, b.Visual ?? a.Visual, fraction);
                if (!ghost.OwnsObject)
                {
                    // Equipment scripts can add audio sources after the character is created.
                    ghost.GameObject.GetComponentsInChildren(true, ghost.Audio);
                    foreach (AudioSource source in ghost.Audio) { source.playOnAwake = false; source.enabled = false; }
                }
            }
        }

        internal void BindCharacter(long user, uint id, GameObject character)
        {
            if (!_ghosts.TryGetValue(user, out Dictionary<uint, GhostInstance> entries))
            {
                entries = new Dictionary<uint, GhostInstance>();
                _ghosts[user] = entries;
            }
            if (entries.TryGetValue(id, out GhostInstance existing) && existing.GameObject == character) return;
            GhostInstance instance = new GhostInstance(character, character.GetComponentInChildren<Animator>(), character.GetComponent<VisEquipment>())
            { OwnsObject = false };
            entries[id] = instance;
            foreach (Animator animator in character.GetComponentsInChildren<Animator>(true))
            { animator.fireEvents = false; animator.enabled = false; }
            DisableComponent<ZSyncAnimation>(character);
            DisableComponent<ZSyncTransform>(character);
            foreach (FootStep footsteps in character.GetComponentsInChildren<FootStep>(true)) footsteps.enabled = false;
            foreach (CharacterAnimEvent events in character.GetComponentsInChildren<CharacterAnimEvent>(true)) events.enabled = false;
            foreach (Collider collider in character.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
            foreach (Rigidbody body in character.GetComponentsInChildren<Rigidbody>(true)) body.isKinematic = true;
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
            ve.m_leftItem = snapshot.LeftItem;
            ve.m_leftItemVariant = snapshot.LeftItemVariant;
            ve.m_rightItem = snapshot.RightItem;
            ve.m_chestItem = snapshot.ChestItem;
            ve.m_legItem = snapshot.LegItem;
            ve.m_helmetItem = snapshot.HelmetItem;
            ve.m_shoulderItem = snapshot.ShoulderItem;
            ve.m_shoulderItemVariant = snapshot.ShoulderItemVariant;
            ve.m_utilityItem = snapshot.UtilityItem;
            ve.m_trinketItem = snapshot.TrinketItem ?? "";
            ve.m_beardItem = snapshot.BeardItem;
            ve.m_hairItem = snapshot.HairItem;
            ve.m_leftBackItem = snapshot.LeftBackItem;
            ve.m_leftBackItemVariant = snapshot.LeftBackItemVariant;
            ve.m_rightBackItem = snapshot.RightBackItem;
        }

        public void FireTrigger(long userID, uint objectID, string triggerName)
        {
            if (!_ghosts.TryGetValue(userID, out var innerDict) ||
                !innerDict.TryGetValue(objectID, out var ghost))
                return;

            if (ghost.Animator != null)
                ghost.Animator.SetTrigger(triggerName);
        }

        public void ApplyPose(EntitySnapshot a, EntitySnapshot b, float fraction)
        {
            if (a.Pose == null || a.Pose.Count == 0 || !_ghosts.TryGetValue(a.ZdoUserID, out Dictionary<uint, GhostInstance> entries)
                || !entries.TryGetValue(a.ZdoID, out GhostInstance ghost)) return;
            if (ghost.Animator != null) ghost.Animator.enabled = false;
            for (int index = 0; index < a.Pose.Count; index++)
            {
                BonePose first = a.Pose[index];
                BonePose second = b.Pose != null && index < b.Pose.Count && b.Pose[index].Path == first.Path ? b.Pose[index] : first;
                if (!ghost.Bones.TryGetValue(first.Path, out Transform bone))
                {
                    bone = ghost.GameObject.transform.Find(first.Path);
                    if (bone == null) continue;
                    ghost.Bones[first.Path] = bone;
                }
                bone.localPosition = Vector3.Lerp(first.Position, second.Position, fraction);
                bone.localRotation = Quaternion.Slerp(first.Rotation, second.Rotation, fraction);
                bone.localScale = Vector3.Lerp(first.Scale, second.Scale, fraction);
            }
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
                if (ghost.GameObject != null && ghost.OwnsObject)
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
                    if (ghost.GameObject != null && ghost.OwnsObject)
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

        private static void RemoveGameplayScripts(GameObject ghost)
        {
            List<MonoBehaviour> remaining = new List<MonoBehaviour>(ghost.GetComponentsInChildren<MonoBehaviour>(true));
            // VisEquipment needs the view during Awake. With forceDisableInit set,
            // ZNetView destroys itself without creating or registering a ZDO.
            remaining.RemoveAll(behavior => behavior is VisEquipment || behavior is ZNetView);
            while (remaining.Count > 0)
            {
                int removable = remaining.FindIndex(candidate => !remaining.Exists(other => other != candidate && Requires(other, candidate)));
                if (removable < 0) throw new System.InvalidOperationException("Replay prefab has cyclic component requirements");
                MonoBehaviour component = remaining[removable];
                remaining.RemoveAt(removable);
                Object.DestroyImmediate(component);
            }
        }

        private static bool Requires(MonoBehaviour dependent, MonoBehaviour dependency)
        {
            if (dependent.gameObject != dependency.gameObject) return false;
            System.Type type = dependency.GetType();
            foreach (RequireComponent requirement in dependent.GetType().GetCustomAttributes(typeof(RequireComponent), true))
                if ((requirement.m_Type0 != null && requirement.m_Type0.IsAssignableFrom(type)) ||
                    (requirement.m_Type1 != null && requirement.m_Type1.IsAssignableFrom(type)) ||
                    (requirement.m_Type2 != null && requirement.m_Type2.IsAssignableFrom(type))) return true;
            return false;
        }
    }
}
