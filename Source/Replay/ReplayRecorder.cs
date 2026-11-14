using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace cameraTools.Replay
{
    [DefaultExecutionOrder(10000)]
    public class ReplayRecorder : MonoBehaviour
    {
        public static ReplayRecorder Instance { get; private set; } = null!;
        public static bool IsRecording { get; private set; }
        public string RecordingName { get; private set; } = "";
        private string _stopReason = "";
        private Task? _saveTask;
        private string _saveSummary = "";
        public bool IsSaving => _saveTask != null;
        public string Status => IsSaving ? $"Saving {RecordingName}; wait for the save-complete message." : IsRecording
            ? $"Recording {RecordingName}: {Time.time - _recordStartTime:F1}s, estimated {_estimatedBytes / (1024f * 1024f):F1}/{cameraToolsPlugin.ReplayMemoryBudgetMiB.Value} MiB, maximum 600s"
            : $"Not recording. Last take: {RecordingName}. {_stopReason}";

        private const int ZdoSalt = 438569;

        // Cached animator parameter hashes
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

        private ReplayFile? _currentReplay;
        private float _recordStartTime;
        private readonly List<TriggerEvent> _pendingTriggers = new List<TriggerEvent>();
        private SceneRecorder? _scene;
        private bool _thumbnailPending;
        private readonly HashSet<(long, uint)> _actors = new();
        private ReplayMediaRecorder? _media;
        private ReplayEventRecorder? _events;
        private float _nextSample;
        private long _estimatedBytes;
        private readonly Dictionary<Animator, List<(string Path, Transform Bone)>> _bones = new();

        private void Awake()
        {
            Instance = this;
        }

        public bool StartRecording(string? name = null)
        {
            CompleteSave();
            if (IsSaving) return false;
            if (IsRecording)
            {
                cameraToolsPlugin.TemplateLogger.LogWarning("Already recording!");
                return false;
            }

            if (ReplayPlayer.IsPlaying || ReplaySession.Active)
            {
                cameraToolsPlugin.TemplateLogger.LogWarning("Cannot record while replaying!");
                return false;
            }

            RecordingName = name ?? DateTime.Now.ToString("yyyy-MM-dd_HHmmss");

            _currentReplay = new ReplayFile
            {
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };
            _pendingTriggers.Clear();
            _recordStartTime = Time.time;
            _nextSample = 0f;
            _bones.Clear();
            _estimatedBytes = 0;
            _stopReason = "";

            _currentReplay.Info = CreateInfo(RecordingName);
            _scene = new SceneRecorder(_currentReplay);
            _thumbnailPending = true;
            _actors.Clear();

            IsRecording = true;
            _events = new ReplayEventRecorder(_currentReplay);
            _media = new ReplayMediaRecorder(_currentReplay, _events);
            cameraToolsPlugin.TemplateLogger.LogInfo($"Recording started: {RecordingName}");
            return true;
        }

        public void StopRecording()
        {
            if (!IsRecording || _currentReplay == null)
            {
                cameraToolsPlugin.TemplateLogger.LogWarning("Not currently recording!");
                return;
            }

            IsRecording = false;
            ReplayMediaRecorder.Active = null;
            _media = null;
            _events = null;
            _currentReplay.Duration = Time.time - _recordStartTime;
            _currentReplay.TriggerEvents.AddRange(_pendingTriggers);
            _scene?.Finish();
            _currentReplay.Info.Duration = _currentReplay.Duration;
            _currentReplay.Info.Actors = _actors.Count;

            var path = System.IO.Path.Combine(ReplayFileIO.GetReplayDirectory(), RecordingName + ".valreplay");
            ReplayFile completed = _currentReplay;
            _currentReplay = null;
            _pendingTriggers.Clear();
            _scene = null;
            _bones.Clear();
            _actors.Clear();
            _saveSummary = $"Recording saved: {RecordingName}.valreplay ({completed.Frames.Count} frames, {completed.Duration:F1}s, {completed.Scene.Count} world objects, {completed.Terrain.Count} zones, {completed.Effects.Count} effects, {completed.Sounds.Count} sounds, {completed.Deaths.Count} deaths, {completed.Attacks.Count} attacks, {completed.Damage.Count} damage samples)";
            // Samples contain copied structs and strings, not live Unity objects.
            _saveTask = Task.Run(() =>
            {
                ReplayFileIO.Write(path, completed);
                ReplayEvents.WriteJson(ReplayEvents.JsonPath(path), ReplayEvents.Build(completed, null));
            });
        }

        private void CompleteSave(bool notify = true)
        {
            if (_saveTask == null || !_saveTask.IsCompleted) return;
            if (_saveTask.IsFaulted || _saveTask.IsCanceled)
            {
                _stopReason = "Save failed: " + (_saveTask.Exception?.GetBaseException().Message ?? "cancelled");
                cameraToolsPlugin.TemplateLogger.LogError(_stopReason);
            }
            else
            {
                cameraToolsPlugin.TemplateLogger.LogInfo(_saveSummary);
                _stopReason = "Saved. " + _stopReason;
            }
            _saveTask = null;
            if (notify && MessageHud.instance != null)
                MessageHud.instance.ShowMessage(MessageHud.MessageType.Center, RecordingName + ": " + _stopReason);
        }

        public void CaptureTrigger(ZDOID zdoid, string triggerName)
        {
            if (!IsRecording)
                return;

            _pendingTriggers.Add(new TriggerEvent
            {
                Time = Time.time - _recordStartTime,
                ZdoUserID = zdoid.UserID,
                ZdoID = zdoid.ID,
                TriggerName = triggerName
            });
            _events?.Trigger(Time.time - _recordStartTime, zdoid, triggerName);
        }

        private void LateUpdate()
        {
            CompleteSave();
            if (!IsRecording || _currentReplay == null)
            {
                if (!ReplaySession.Active) SceneRecorder.Prepare();
                return;
            }

            if (ZNetScene.instance == null)
                return;

            float elapsed = Time.time - _recordStartTime;
            if (elapsed < _nextSample) return;
            _nextSample = elapsed + 1f / 30f;
            if (elapsed >= 600f || _estimatedBytes >= (long)cameraToolsPlugin.ReplayMemoryBudgetMiB.Value * 1024 * 1024)
            {
                _stopReason = elapsed >= 600f ? "Reached the 10-minute limit." : "Reached the configured recording memory budget.";
                cameraToolsPlugin.TemplateLogger.LogWarning(_stopReason + " Saving take.");
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, "Saving replay: " + _stopReason);
                StopRecording(); return;
            }

            var frame = new ReplayFrame
            {
                Time = Time.time - _recordStartTime
            };
            if (GameCamera.m_instance != null)
            {
                frame.CameraPosition = GameCamera.m_instance.transform.position;
                frame.CameraRotation = GameCamera.m_instance.transform.rotation;
                if (_currentReplay.Frames.Count == 0)
                    _currentReplay.Info.StartCamera = new[] { frame.CameraPosition.x, frame.CameraPosition.y, frame.CameraPosition.z,
                        frame.CameraRotation.x, frame.CameraRotation.y, frame.CameraRotation.z, frame.CameraRotation.w };
            }
            if (_thumbnailPending) { _thumbnailPending = false; _currentReplay.Thumbnail = CaptureThumbnail(); }
            _media?.BeginSample();

            foreach (var kvp in ZNetScene.instance.m_instances)
            {
                var zdo = kvp.Key;
                var nview = kvp.Value;

                if (nview == null || !nview)
                    continue;

                var character = nview.GetComponent<Character>();
                Projectile projectile = nview.GetComponent<Projectile>();
                Ragdoll ragdoll = nview.GetComponent<Ragdoll>();
                ItemDrop drop = nview.GetComponent<ItemDrop>();
                if (character == null && projectile == null && ragdoll == null && drop == null)
                    continue;
                if (Player.m_localPlayer != null && Vector3.Distance(nview.transform.position, Player.m_localPlayer.transform.position) > 150f)
                    continue;

                var snapshot = new EntitySnapshot
                {
                    IsProjectile = projectile != null,
                    Kind = projectile != null ? (byte)1 : ragdoll != null ? (byte)2 : drop != null ? (byte)3 : (byte)0,
                    ZdoUserID = zdo.m_uid.UserID,
                    ZdoID = zdo.m_uid.ID,
                    PrefabHash = zdo.GetPrefab(),
                    Position = nview.transform.position,
                    Rotation = nview.transform.rotation,
                    ForwardSpeed = zdo.GetFloat(ZdoSalt + Hash_ForwardSpeed, 0f),
                    SidewaySpeed = zdo.GetFloat(ZdoSalt + Hash_SidewaySpeed, 0f),
                    TurnSpeed = zdo.GetFloat(ZdoSalt + Hash_TurnSpeed, 0f),
                    AnimBools = EntitySnapshot.PackBools(
                        zdo.GetBool(ZdoSalt + Hash_InWater),
                        zdo.GetBool(ZdoSalt + Hash_OnGround),
                        zdo.GetBool(ZdoSalt + Hash_Encumbered),
                        zdo.GetBool(ZdoSalt + Hash_Flying),
                        zdo.GetBool(ZdoSalt + Hash_Falling),
                        zdo.GetBool(ZdoSalt + Hash_Crouching),
                        zdo.GetBool(ZdoSalt + Hash_Blocking)
                    ),
                    StateF = zdo.GetInt(ZdoSalt + Hash_StateF, 0),
                    StateI = zdo.GetInt(ZdoSalt + Hash_StateI, 0)
                };

                Animator animator = nview.GetComponentInChildren<Animator>();
                if (animator != null)
                {
                    if (!_bones.TryGetValue(animator, out List<(string Path, Transform Bone)> bones))
                    {
                        bones = new List<(string, Transform)>();
                        HashSet<Transform> included = new HashSet<Transform>();
                        foreach (SkinnedMeshRenderer renderer in nview.GetComponentsInChildren<SkinnedMeshRenderer>())
                            foreach (Transform bone in renderer.bones)
                                for (Transform current = bone; current != null && current != nview.transform; current = current.parent)
                                    if (included.Add(current)) bones.Add((PosePath(current, nview.transform), current));
                        _bones[animator] = bones;
                    }
                    snapshot.Pose = new List<BonePose>(bones.Count);
                    foreach ((string path, Transform bone) in bones)
                        if (bone != null) snapshot.Pose.Add(new BonePose { Path = path, Position = bone.localPosition, Rotation = bone.localRotation, Scale = bone.localScale });
                }

                var visEquip = nview.GetComponent<VisEquipment>();
                if (visEquip != null)
                {
                    snapshot.HasEquipment = true;
                    snapshot.LeftItem = visEquip.m_leftItem;
                    snapshot.LeftItemVariant = visEquip.m_leftItemVariant;
                    snapshot.RightItem = visEquip.m_rightItem;
                    snapshot.ChestItem = visEquip.m_chestItem;
                    snapshot.LegItem = visEquip.m_legItem;
                    snapshot.HelmetItem = visEquip.m_helmetItem;
                    snapshot.ShoulderItem = visEquip.m_shoulderItem;
                    snapshot.ShoulderItemVariant = visEquip.m_shoulderItemVariant;
                    snapshot.UtilityItem = visEquip.m_utilityItem;
                    snapshot.TrinketItem = visEquip.m_trinketItem ?? "";
                    snapshot.BeardItem = visEquip.m_beardItem;
                    snapshot.HairItem = visEquip.m_hairItem;
                    snapshot.LeftBackItem = visEquip.m_leftBackItem;
                    snapshot.LeftBackItemVariant = visEquip.m_leftBackItemVariant;
                    snapshot.RightBackItem = visEquip.m_rightBackItem;
                }

                if (character != null) _events?.Sample(character, snapshot.ZdoUserID, snapshot.ZdoID, elapsed);
                snapshot.Visual = ReplayVisuals.Capture(nview.gameObject, ragdoll != null, ref _estimatedBytes);
                if (_actors.Add((snapshot.ZdoUserID, snapshot.ZdoID)))
                    _currentReplay.Info.Prefabs[snapshot.PrefabHash] = Utils.GetPrefabName(nview.gameObject);
                frame.Entities.Add(snapshot);
                _media?.Sounds(nview.gameObject, elapsed, ref _estimatedBytes);
                _estimatedBytes += 256 + (snapshot.Pose?.Count ?? 0) * 80L;
            }

            _currentReplay.Frames.Add(frame);
            _media?.Sample(elapsed, ref _estimatedBytes);

            _scene?.Update(frame.Time, ref _estimatedBytes);
        }

        private void OnDestroy()
        {
            if (IsRecording) StopRecording();
            if (_saveTask != null)
            {
                try { _saveTask.Wait(); }
                catch (AggregateException) { /* Report the task failure below. */ }
                CompleteSave(false);
            }
        }

        private static ReplayInfo CreateInfo(string name)
        {
            ReplayInfo info = new() { Name = name, CreatedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), GameVersion = global::Version.GetVersionString() };
            World? world = ZNet.m_world;
            if (world != null)
            {
                info.WorldName = world.m_name; info.SeedName = world.m_seedName;
                info.Seed = world.m_seed; info.WorldGenVersion = world.m_worldGenVersion;
            }
            foreach (BepInEx.PluginInfo plugin in BepInEx.Bootstrap.Chainloader.PluginInfos.Values)
                info.Mods.Add(new ReplayMod { Guid = plugin.Metadata.GUID, Name = plugin.Metadata.Name, Version = plugin.Metadata.Version.ToString() });
            return info;
        }

        // A small preview for the replay browser, rendered from the game camera before the HUD is drawn.
        private static byte[] CaptureThumbnail()
        {
            Camera? camera = Utils.GetMainCamera();
            if (camera == null) return Array.Empty<byte>();
            RenderTexture target = RenderTexture.GetTemporary(384, 216, 24);
            RenderTexture? previousTarget = camera.targetTexture;
            RenderTexture? previousActive = RenderTexture.active;
            Texture2D image = new Texture2D(384, 216, TextureFormat.RGB24, false);
            try
            {
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, 384, 216), 0, 0);
                image.Apply();
                return image.EncodeToJPG(80);
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(target);
                Destroy(image);
            }
        }

        private static string PosePath(Transform bone, Transform root)
        {
            string path = bone.name;
            for (Transform parent = bone.parent; parent != null && parent != root; parent = parent.parent)
                path = parent.name + "/" + path;
            return path;
        }
    }
}
