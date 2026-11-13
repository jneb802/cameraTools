using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace cameraTools.Replay
{
    // Keep identity rather than a clone reference: seeking can destroy and recreate clones.
    public sealed class ReplayCameraRig
    {
        private readonly List<(long, uint)> _ids = new();
        private readonly List<float> _starts = new();
        public readonly List<string> Labels = new();
        private (long, uint)? _aim;
        private (long, uint)? _attachment;
        private Vector3 _aimPoint;
        private Vector3 _offset;
        private bool _waiting;
        internal static bool Updating;
        public int Selected { get; set; }
        public string Status => _waiting ? "Target absent here; seek to its recorded lifetime" :
            _attachment.HasValue ? "Attached: camera moves and turns with target" :
            _aim.HasValue ? "Aim locked: camera position is free" : "Free camera";

        public ReplayCameraRig(ReplayFile replay)
        {
            HashSet<(long, uint)> seen = new();
            foreach (ReplayFrame frame in replay.Frames)
                foreach (EntitySnapshot entity in frame.Entities)
                {
                    (long, uint) key = (entity.ZdoUserID, entity.ZdoID);
                    if (!seen.Add(key)) continue;
                    _ids.Add(key);
                    _starts.Add(frame.Time);
                    string kind = entity.IsProjectile ? "Projectile" : entity.Kind == 2 ? "Ragdoll" : entity.Kind == 3 ? "Drop" : "Actor";
                    Labels.Add($"{kind}: {replay.Info.PrefabName(entity.PrefabHash)} [{entity.ZdoID}] at {frame.Time:F1}s");
                }
        }

        private GameObject? Get((long, uint) id) => ReplayPlayer.Instance.FindTarget(id.Item1, id.Item2);

        public void SeekToSelected()
        {
            if (Selected < 0 || Selected >= _starts.Count) return;
            ReplayPlayer.Instance.SetPaused(true);
            ReplayPlayer.Instance.Seek(_starts[Selected]);
        }

        public void Select(long user, uint id)
        {
            int index = _ids.IndexOf((user, id));
            if (index < 0) throw new ArgumentException("Recorded target does not exist");
            Selected = index;
        }

        public void SetView(string view)
        {
            if (Selected < 0 || Selected >= _ids.Count) return;
            (long, uint) id = _ids[Selected];
            GameObject? target = Get(id);
            if (target == null) throw new InvalidOperationException("Target is absent at this time; seek to its recorded lifetime first");
            GameCamera camera = GameCamera.m_instance;
            ReplayPlayer.Instance.Detach();
            CreativeZoneCameraController.StopOrbit();
            CreativeZoneCameraController.StopMove();
            cameraTools.Markers.PathPlayer.Stop();
            if (view == "aim")
            {
                _aim = id;
                _aimPoint = target.transform.InverseTransformPoint(Center(target));
                return;
            }
            _attachment = id;
            Quaternion rotation = camera.transform.rotation;
            switch (view)
            {
                case "attach": _offset = target.transform.InverseTransformPoint(camera.transform.position); break;
                case "front": _offset = new Vector3(0, 1.6f, 4f); rotation = target.transform.rotation * Quaternion.Euler(0, 180, 0); break;
                case "third": _offset = new Vector3(0, 1.6f, -4f); rotation = target.transform.rotation; break;
                case "projectile": _offset = new Vector3(0, 0.3f, -2f); rotation = target.transform.rotation; break;
                default: _attachment = null; throw new ArgumentException("Use aim, attach, front, third or projectile");
            }
            camera.m_freeFlyLockon = target.transform;
            camera.m_freeFlyLockonOffset = _offset;
            camera.transform.position = target.transform.TransformPoint(_offset);
            camera.transform.rotation = rotation;
            SetAngles(camera, Quaternion.Inverse(target.transform.rotation) * rotation);
            camera.m_freeFlyVel = Vector3.zero;
            camera.m_freeFlySavedVel = Vector3.zero;
        }

        public void Clear()
        {
            _aim = null; _attachment = null; _waiting = false;
            GameCamera camera = GameCamera.m_instance;
            if (camera == null) return;
            camera.m_freeFlyTarget = null; camera.m_freeFlyLockon = null;
            SetAngles(camera, camera.transform.rotation);
        }

        private static void SetAngles(GameCamera camera, Quaternion rotation)
        {
            camera.m_freeFlyYaw = Mathf.DeltaAngle(0, rotation.eulerAngles.y);
            camera.m_freeFlyPitch = Mathf.DeltaAngle(0, rotation.eulerAngles.x);
        }

        private static Vector3 Center(GameObject target)
        {
            Renderer? renderer = target.GetComponentInChildren<SkinnedMeshRenderer>();
            if (renderer == null) renderer = target.GetComponentInChildren<MeshRenderer>();
            return renderer != null ? renderer.bounds.center : target.transform.position;
        }

        private bool Pick(out Vector3 point)
        {
            Transform camera = GameCamera.m_instance.transform;
            Ray ray = new(camera.position, camera.forward);
            float nearest = 10000f;
            int selected = -1;
            for (int index = 0; index < _ids.Count; index++)
            {
                GameObject? target = Get(_ids[index]);
                if (target == null || !target.activeInHierarchy) continue;
                foreach (Renderer renderer in target.GetComponentsInChildren<Renderer>())
                {
                    if (!renderer.enabled || renderer is ParticleSystemRenderer || renderer is TrailRenderer) continue;
                    Bounds bounds = renderer.bounds;
                    bounds.Expand(0.08f); // Small projectile meshes remain selectable.
                    if (bounds.IntersectRay(ray, out float distance) && distance < nearest)
                    { nearest = distance; selected = index; }
                }
            }
            point = ray.GetPoint(nearest);
            if (selected < 0) return false;
            // Opaque world geometry must not select an actor behind it.
            int mask = LayerMask.GetMask("terrain", "piece", "static_solid");
            if (Physics.Raycast(ray, out RaycastHit hit, nearest, mask)) return false;
            Selected = selected;
            return true;
        }

        public void Update()
        {
            GameCamera camera = GameCamera.m_instance;
            if (camera == null || !GameCamera.InFreeFly()) return;
            bool blocked = Console.IsVisible() || Cursor.lockState != CursorLockMode.Locked || CameraPanelStyle.PointerBlocked;
            if (!blocked)
            {
                bool aim = ZInput.GetButtonDown("Attack") || (ZInput.GetButtonDown("JoyRStick") && !ZInput.GetButton("JoyAltKeys"));
                bool attach = ZInput.GetButtonDown("SecondaryAttack") || ZInput.GetButtonDown("JoyLStick");
                if (aim || attach)
                {
                    if ((aim && _aim.HasValue) || (attach && _attachment.HasValue)) Clear();
                    else if (Pick(out Vector3 point))
                    {
                        SetView(attach ? "attach" : "aim");
                        if (aim) _aimPoint = Get(_ids[Selected])!.transform.InverseTransformPoint(point);
                    }
                }
            }
            GameObject? aimTarget = _aim.HasValue ? Get(_aim.Value) : null;
            GameObject? attachment = _attachment.HasValue ? Get(_attachment.Value) : null;
            _waiting = (_aim.HasValue && aimTarget == null) || (_attachment.HasValue && attachment == null);
            camera.m_freeFlyTarget = aimTarget != null ? aimTarget.transform : null;
            camera.m_freeFlyTargetOffset = _aimPoint;
            camera.m_freeFlyLockon = attachment != null ? attachment.transform : null;
            camera.m_freeFlyLockonOffset = _offset;
            if (_waiting) return; // Hold the last view; rebind on a backward seek.
            Updating = true;
            try
            {
                if (!blocked) camera.UpdateFreeFly(Time.deltaTime);
                else
                {
                    if (attachment != null)
                    {
                        camera.transform.position = attachment.transform.TransformPoint(_offset);
                        camera.transform.rotation = attachment.transform.rotation * Quaternion.Euler(0, camera.m_freeFlyYaw, 0) * Quaternion.Euler(camera.m_freeFlyPitch, 0, 0);
                    }
                    if (aimTarget != null)
                    {
                        Vector3 direction = aimTarget.transform.TransformPoint(_aimPoint) - camera.transform.position;
                        if (direction.sqrMagnitude > 0.0001f) camera.transform.rotation = Quaternion.LookRotation(direction);
                    }
                }
                if (attachment != null) _offset = camera.m_freeFlyLockonOffset;
            }
            finally { Updating = false; }
        }
    }

    [HarmonyPatch(typeof(GameCamera), nameof(GameCamera.UpdateFreeFly))]
    internal static class ReplayFreeFlyPatch
    {
        // Run vanilla movement after replay transforms are updated, not one frame earlier.
        private static bool Prefix() => ReplayPlayer.IsPlaying ? ReplayCameraRig.Updating :
            !(CameraPanelStyle.CameraVisible && Cursor.lockState != CursorLockMode.Locked) && !CameraPanelStyle.PointerBlocked;

        private static bool ButtonDown(string name)
        {
            if (ReplayPlayer.IsPlaying && (name == "Attack" || name == "SecondaryAttack" || name == "JoyLStick" || name == "JoyRStick")) return false;
            return ZInput.GetButtonDown(name);
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            System.Reflection.MethodInfo original = AccessTools.Method(typeof(ZInput), nameof(ZInput.GetButtonDown), new[] { typeof(string) });
            System.Reflection.MethodInfo replacement = AccessTools.Method(typeof(ReplayFreeFlyPatch), nameof(ButtonDown));
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(original)) instruction.operand = replacement;
                yield return instruction;
            }
        }
    }
}
