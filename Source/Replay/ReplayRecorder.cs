using System;
using System.Collections.Generic;
using UnityEngine;

namespace cameraTools.Replay
{
    public class ReplayRecorder : MonoBehaviour
    {
        public static ReplayRecorder Instance { get; private set; } = null!;
        public static bool IsRecording { get; private set; }

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

        private void Awake()
        {
            Instance = this;
        }

        public void StartRecording()
        {
            if (IsRecording)
            {
                cameraToolsPlugin.TemplateLogger.LogWarning("Already recording!");
                return;
            }

            if (ReplayPlayer.IsPlaying)
            {
                cameraToolsPlugin.TemplateLogger.LogWarning("Cannot record while replaying!");
                return;
            }

            _currentReplay = new ReplayFile
            {
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };
            _pendingTriggers.Clear();
            _recordStartTime = Time.time;
            IsRecording = true;
            cameraToolsPlugin.TemplateLogger.LogInfo("Recording started");
        }

        public void StopRecording(string name)
        {
            if (!IsRecording || _currentReplay == null)
            {
                cameraToolsPlugin.TemplateLogger.LogWarning("Not currently recording!");
                return;
            }

            IsRecording = false;
            _currentReplay.Duration = Time.time - _recordStartTime;
            _currentReplay.TriggerEvents.AddRange(_pendingTriggers);

            var path = System.IO.Path.Combine(ReplayFileIO.GetReplayDirectory(), name + ".valreplay");
            try
            {
                ReplayFileIO.Write(path, _currentReplay);
                cameraToolsPlugin.TemplateLogger.LogInfo($"Recording saved: {name}.valreplay ({_currentReplay.Frames.Count} frames, {_currentReplay.Duration:F1}s)");
            }
            catch (Exception ex)
            {
                cameraToolsPlugin.TemplateLogger.LogError($"Failed to save replay: {ex.Message}");
            }

            _currentReplay = null;
            _pendingTriggers.Clear();
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
        }

        private void LateUpdate()
        {
            if (!IsRecording || _currentReplay == null)
                return;

            if (ZNetScene.instance == null)
                return;

            var frame = new ReplayFrame
            {
                Time = Time.time - _recordStartTime
            };

            foreach (var kvp in ZNetScene.instance.m_instances)
            {
                var zdo = kvp.Key;
                var nview = kvp.Value;

                if (nview == null || !nview)
                    continue;

                var character = nview.GetComponent<Character>();
                if (character == null)
                    continue;

                var snapshot = new EntitySnapshot
                {
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

                frame.Entities.Add(snapshot);
            }

            _currentReplay.Frames.Add(frame);
        }

        private void OnDestroy()
        {
            if (IsRecording)
            {
                IsRecording = false;
                _currentReplay = null;
                _pendingTriggers.Clear();
            }
        }
    }
}
