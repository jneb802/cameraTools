using System;
using System.Collections.Generic;
using UnityEngine;

namespace cameraTools.Replay
{
    [DefaultExecutionOrder(20000)]
    public class ReplayPlayer : MonoBehaviour
    {
        public static ReplayPlayer Instance { get; private set; } = null!;
        public static bool IsPlaying { get; private set; }

        private ReplayFile? _replay;
        private GhostManager? _ghostManager;
        private float _playbackTime;
        private float _playbackSpeed = 1f;
        private bool _paused;
        private int _lastTriggerIndex;
        private WorldStatePlayer? _worldStatePlayer;
        private SceneTimelinePlayer? _scene;
        private EnvironmentPlayer? _environment;
        private bool _recordedCamera;
        /// <summary>True when the replay runs in its own session instead of over a loaded world.</summary>
        public bool Isolated { get; private set; }
        public bool HasRecordedCamera => _replay != null && _replay.Info.StartCamera[6] != 0f;
        public bool FollowingRecordedCamera => _recordedCamera;
        public string SceneSummary
        {
            get
            {
                string actors = _ghostManager != null && _ghostManager.MissingNames.Count > 0
                    ? $"Left out, mod not loaded: {string.Join(", ", _ghostManager.MissingNames)}" : "";
                if (_scene == null) return actors.Length > 0 ? actors : Isolated ? "" : "Playing over the loaded world";
                int skipped = 0;
                foreach (int count in _scene.Missing.Values) skipped += count;
                string summary = $"{_scene.ObjectCount} world objects";
                if (skipped > 0) summary += $"; {skipped} objects from unloaded mods left out ({string.Join(", ", _scene.Missing.Keys)})";
                if (actors.Length > 0) summary += "\n" + actors;
                return summary;
            }
        }
        private ReplayMediaPlayer? _media;
        public ReplayCameraRig? CameraRig { get; private set; }
        public ReplayMusic? Music { get; private set; }
        private (long, uint)? _cameraTarget;
        private Vector3 _cameraOffset = new Vector3(0, 2, -6);
        private bool _localOffset;
        private readonly Dictionary<Renderer, bool> _hidden = new();
        private float _nextHideScan;
        public string Media()
        {
            if (_replay == null) return "No replay loaded";
            List<string> lines = new() { $"effects={_replay.Effects.Count} sounds={_replay.Sounds.Count} deaths={_replay.Deaths.Count}" };
            foreach (DeathEvent death in _replay.Deaths) lines.Add($"death {death.Time:F3}s {death.Name}");
            foreach (EffectTrack track in _replay.Effects) lines.Add($"effect {track.Start:F3}-{track.End:F3}s {track.Prefab} frames={track.Frames.Count}");
            foreach (SoundTrack track in _replay.Sounds) lines.Add($"sound {track.Start:F3}-{track.End:F3}s {track.Clip} frames={track.Frames.Count}");
            return string.Join("\n", lines);
        }
        public string Targets()
        {
            if (_replay == null) return "No replay loaded";
            Dictionary<(long, uint), string> targets = new();
            foreach (ReplayFrame frame in _replay.Frames)
                foreach (EntitySnapshot entity in frame.Entities)
                {
                    string kind = entity.Kind == 2 ? "ragdoll" : entity.Kind == 3 ? "drop" : entity.IsProjectile ? "projectile" : "actor";
                    targets[(entity.ZdoUserID, entity.ZdoID)] = $"{entity.ZdoUserID}:{entity.ZdoID} {kind} {_replay.Info.PrefabName(entity.PrefabHash)}";
                }
            return string.Join("; ", targets.Values);
        }
        public void Follow(long user, uint id, Vector3 offset, bool local)
        {
            if (!IsPlaying) throw new InvalidOperationException("Start a replay first");
            if (offset.sqrMagnitude < 0.001f) throw new ArgumentException("Camera offset must be nonzero");
            bool found = false;
            foreach (ReplayFrame frame in _replay!.Frames)
                if (frame.Entities.Exists(entity => entity.ZdoUserID == user && entity.ZdoID == id)) { found = true; break; }
            if (!found) throw new ArgumentException("Recorded target does not exist; use ct_replay targets");
            cameraTools.Markers.PathPlayer.Stop();
            CameraRig?.Clear();
            _cameraTarget = (user, id); _cameraOffset = offset; _localOffset = local;
        }
        public void Detach()
        {
            _cameraTarget = null;
            _recordedCamera = false;
            CameraRig?.Clear();
        }

        // Entities present in the current interpolation window
        private readonly HashSet<(long, uint)> _activeEntities = new HashSet<(long, uint)>();
        private readonly HashSet<(long, uint)> _currentEntities = new();
        private readonly Dictionary<(long, uint), EntitySnapshot> _frameBEntities = new();

        // Public accessors for timeline UI
        public float PlaybackTime => _playbackTime;
        public float Duration => _replay?.Duration ?? 0f;
        public float PlaybackSpeed => _playbackSpeed;
        public bool IsPaused => _paused;
        public bool HasWorldEvents => _replay?.WorldEvents.Count > 0;
        public int WorldEventCount => _replay?.WorldEvents.Count ?? 0;

        private void Awake()
        {
            Instance = this;
        }

        public void FollowRecordedCamera()
        {
            if (!IsPlaying) throw new InvalidOperationException("Start a replay first");
            if (!HasRecordedCamera) throw new InvalidOperationException("This recording has no camera track");
            cameraTools.Markers.PathPlayer.Stop();
            CameraRig?.Clear();
            _cameraTarget = null;
            _recordedCamera = true;
        }

        public void StartPlayback(ReplayFile replay, bool isolated = false)
        {
            if (IsPlaying)
            {
                cameraToolsPlugin.TemplateLogger.LogWarning("Already playing a replay!");
                return;
            }

            if (ReplayRecorder.IsRecording)
            {
                cameraToolsPlugin.TemplateLogger.LogWarning("Cannot replay while recording!");
                return;
            }

            if (replay.Frames.Count == 0)
            {
                cameraToolsPlugin.TemplateLogger.LogWarning("Replay file has no frames!");
                return;
            }

            _replay = replay;
            _ghostManager = new GhostManager(replay.Info);
            _media = new ReplayMediaPlayer(replay);
            CameraRig = new ReplayCameraRig(replay);
            Music = new ReplayMusic();
            _playbackTime = 0f;
            _playbackSpeed = 1f;
            _paused = false;
            _lastTriggerIndex = 0;
            _activeEntities.Clear();
            Isolated = isolated;
            _recordedCamera = false;
            _environment = replay.Environment.Count > 0 ? new EnvironmentPlayer(replay.Environment) : null;

            if (isolated)
            {
                // The recorded objects replace the world, so piece events are already in the scene.
                _worldStatePlayer = null;
                System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
                _scene = new SceneTimelinePlayer(replay);
                _scene.Seek(0f);
                cameraToolsPlugin.TemplateLogger.LogInfo($"[ReplayPlayer] Loaded the recorded world in {clock.ElapsedMilliseconds} ms");
            }
            else if (replay.WorldEvents.Count > 0)
            {
                _worldStatePlayer = new WorldStatePlayer(replay.WorldEvents, _ghostManager);
                _worldStatePlayer.Initialize();
            }
            else
            {
                _worldStatePlayer = null;
            }

            IsPlaying = true;
            CameraToolsPanel.OnReplayStarted();

            // Auto-enter free fly mode
            if (!GameCamera.InFreeFly() && GameCamera.m_instance != null)
                GameCamera.m_instance.ToggleFreeFly();

            cameraToolsPlugin.TemplateLogger.LogInfo($"Replay started: {replay.Duration:F1}s, {replay.Frames.Count} frames");
        }

        public void StopPlayback()
        {
            if (!IsPlaying)
                return;

            CameraRig?.Clear();
            CameraRig = null;
            Music?.Dispose();
            Music = null;

            _worldStatePlayer?.RestoreAll();
            if (_environment != null) EnvironmentPlayer.Restore();
            _environment = null;
            _scene = null;
            _recordedCamera = false;
            Isolated = false;
            _media?.Dispose();
            _media = null;
            _worldStatePlayer = null;
            _ghostManager?.DestroyAll();
            _ghostManager = null;
            _replay = null;
            _activeEntities.Clear();
            _cameraTarget = null;
            foreach (Renderer renderer in _hidden.Keys)
                if (renderer != null) renderer.enabled = _hidden[renderer];
            _hidden.Clear();
            IsPlaying = false;
            _paused = false;
            CameraToolsPanel.OnReplayStopped();

            cameraToolsPlugin.TemplateLogger.LogInfo("Replay stopped");
        }

        public void TogglePause()
        {
            _paused = !_paused;
        }

        public void SetPaused(bool paused)
        {
            _paused = paused;
        }

        public void Seek(float time)
        {
            if (_replay == null) return;
            _playbackTime = Mathf.Clamp(time, 0f, _replay.Duration);

            _lastTriggerIndex = FindTriggerIndex(_playbackTime);

            _worldStatePlayer?.SeekTo(_playbackTime);
            _scene?.Seek(_playbackTime);
            _environment?.Seek(_playbackTime);
        }

        public void AdjustSpeed(float delta)
        {
            _playbackSpeed = Mathf.Clamp(_playbackSpeed + delta, 0.1f, 4f);
        }
        public void SetSpeed(float speed) => _playbackSpeed = Mathf.Clamp(speed, 0.1f, 4f);

        private void LateUpdate()
        {
            if (!IsPlaying || _replay == null || _ghostManager == null)
                return;

            // Advance time
            if (!_paused)
            {
                _playbackTime += Time.deltaTime * _playbackSpeed;
                if (_playbackTime >= _replay.Duration)
                {
                    _playbackTime = _replay.Duration;
                    _paused = true;
                }
            }

            // Find bracketing frames via binary search
            int frameIndex = FindFrameIndex(_playbackTime);
            if (frameIndex < 0)
                return;

            var frameA = _replay.Frames[frameIndex];
            var frameB = (frameIndex + 1 < _replay.Frames.Count) ? _replay.Frames[frameIndex + 1] : frameA;

            float frameDuration = frameB.Time - frameA.Time;
            float t = (frameDuration > 0.0001f) ? Mathf.Clamp01((_playbackTime - frameA.Time) / frameDuration) : 0f;

            // Track which entities are in this window
            HashSet<(long, uint)> currentEntities = _currentEntities;
            currentEntities.Clear();

            // Index frameB entities for quick lookup
            Dictionary<(long, uint), EntitySnapshot> frameBEntities = _frameBEntities;
            frameBEntities.Clear();
            foreach (var e in frameB.Entities)
                frameBEntities[(e.ZdoUserID, e.ZdoID)] = e;

            // Interpolate and update ghosts from frameA
            _scene?.Update(_playbackTime);
            foreach (var a in frameA.Entities)
            {
                var key = (a.ZdoUserID, a.ZdoID);
                if (_scene != null && _scene.Contains(a.ZdoUserID, a.ZdoID))
                {
                    // Replayed as a real object; the recorded frames still control its motion.
                    if (frameBEntities.TryGetValue(key, out EntitySnapshot next))
                        _scene.Move(a.ZdoUserID, a.ZdoID, Vector3.Lerp(a.Position, next.Position, t), Quaternion.Slerp(a.Rotation, next.Rotation, t));
                    else _scene.Move(a.ZdoUserID, a.ZdoID, a.Position, a.Rotation);
                    if (a.Kind == 0 && !a.IsProjectile)
                    {
                        GameObject actor = ZNetScene.instance.FindInstance(new ZDOID(a.ZdoUserID, a.ZdoID));
                        if (actor != null)
                        {
                            EntitySnapshot second = frameBEntities.TryGetValue(key, out EntitySnapshot sample) ? sample : a;
                            currentEntities.Add(key);
                            _ghostManager.BindCharacter(a.ZdoUserID, a.ZdoID, actor);
                            _ghostManager.UpdateGhost(a.ZdoUserID, a.ZdoID, Vector3.Lerp(a.Position, second.Position, t),
                                Quaternion.Slerp(a.Rotation, second.Rotation, t), a.ForwardSpeed, a.SidewaySpeed, a.TurnSpeed, a.AnimBools, a.StateF, a.StateI);
                            _ghostManager.UpdateEquipment(a.ZdoUserID, a.ZdoID, t < 0.5f ? a : second);
                            _ghostManager.ApplyPose(a, second, t);
                            _ghostManager.ApplyVisual(a, second, t);
                        }
                    }
                    continue;
                }
                currentEntities.Add(key);

                _ghostManager.GetOrCreateGhost(a.ZdoUserID, a.ZdoID, a.PrefabHash, a.Position, a.Rotation);

                // Use nearest frame's snapshot for equipment (doesn't interpolate)
                EntitySnapshot equipSource = a;

                if (frameBEntities.TryGetValue(key, out var b))
                {
                    // Interpolate between frames
                    var pos = Vector3.Lerp(a.Position, b.Position, t);
                    var rot = Quaternion.Slerp(a.Rotation, b.Rotation, t);
                    var fwd = Mathf.Lerp(a.ForwardSpeed, b.ForwardSpeed, t);
                    var side = Mathf.Lerp(a.SidewaySpeed, b.SidewaySpeed, t);
                    var turn = Mathf.Lerp(a.TurnSpeed, b.TurnSpeed, t);
                    // Bools use nearest frame
                    byte bools = (t < 0.5f) ? a.AnimBools : b.AnimBools;
                    int stateF = (t < 0.5f) ? a.StateF : b.StateF;
                    int stateI = (t < 0.5f) ? a.StateI : b.StateI;

                    _ghostManager.UpdateGhost(a.ZdoUserID, a.ZdoID, pos, rot, fwd, side, turn, bools, stateF, stateI);
                    if (t >= 0.5f) equipSource = b;
                }
                else
                {
                    // Entity only in frameA, use its data directly
                    _ghostManager.UpdateGhost(a.ZdoUserID, a.ZdoID, a.Position, a.Rotation,
                        a.ForwardSpeed, a.SidewaySpeed, a.TurnSpeed, a.AnimBools, a.StateF, a.StateI);
                }

                _ghostManager.UpdateEquipment(a.ZdoUserID, a.ZdoID, equipSource);
                _ghostManager.ApplyPose(a, frameBEntities.TryGetValue(key, out EntitySnapshot nextPose) ? nextPose : a, t);
                _ghostManager.ApplyVisual(a, frameBEntities.TryGetValue(key, out EntitySnapshot nextVisual) ? nextVisual : a, t);
            }

            // Also include entities that only appear in frameB
            foreach (var b in frameB.Entities)
            {
                if (_playbackTime < frameB.Time) continue;
                var key = (b.ZdoUserID, b.ZdoID);
                if (currentEntities.Contains(key) || (_scene != null && _scene.Contains(b.ZdoUserID, b.ZdoID)))
                    continue;

                currentEntities.Add(key);
                _ghostManager.GetOrCreateGhost(b.ZdoUserID, b.ZdoID, b.PrefabHash, b.Position, b.Rotation);
                _ghostManager.UpdateGhost(b.ZdoUserID, b.ZdoID, b.Position, b.Rotation,
                    b.ForwardSpeed, b.SidewaySpeed, b.TurnSpeed, b.AnimBools, b.StateF, b.StateI);
                _ghostManager.UpdateEquipment(b.ZdoUserID, b.ZdoID, b);
                _ghostManager.ApplyPose(b, b, 0f);
                _ghostManager.ApplyVisual(b, b, 0f);
            }

            // Prune ghosts for entities no longer present
            foreach (var key in _activeEntities)
            {
                if (!currentEntities.Contains(key))
                    _ghostManager.RemoveGhost(key.Item1, key.Item2);
            }
            _activeEntities.Clear();
            foreach (var key in currentEntities)
                _activeEntities.Add(key);

            // Fire trigger events
            FirePendingTriggers();

            // Update world state
            _worldStatePlayer?.UpdatePlayback(_playbackTime);
            _environment?.Update(_playbackTime);
            _media?.Update(_playbackTime, _paused, _playbackSpeed);
            // Hide live subjects locally while their recorded copies are being filmed.
            if (ZNetScene.instance != null && Time.unscaledTime >= _nextHideScan)
            {
                _nextHideScan = Time.unscaledTime + 0.25f;
                if (Isolated)
                {
                    // The session's only live actor is the hidden local player.
                    if (Player.m_localPlayer != null) Hide(Player.m_localPlayer.gameObject);
                }
                else
                    foreach (ZNetView view in ZNetScene.instance.m_instances.Values)
                        if (view != null && (view.GetComponent<Character>() != null || view.GetComponent<Projectile>() != null || view.GetComponent<Ragdoll>() != null || view.GetComponent<ItemDrop>() != null))
                            Hide(view.gameObject);
            }
            CameraRig?.Update();
            Music?.Update(_playbackTime, _playbackSpeed, _paused);
            if (_recordedCamera) ApplyRecordedCamera(frameA, frameB, t);
            if (_cameraTarget.HasValue && GameCamera.m_instance != null)
            {
                (long user, uint id) = _cameraTarget.Value;
                GameObject? target = FindTarget(user, id);
                if (target != null)
                {
                    Transform camera = GameCamera.m_instance.transform;
                    Vector3 offset = _localOffset ? target.transform.rotation * _cameraOffset : _cameraOffset;
                    camera.position = target.transform.position + offset;
                    Vector3 look = target.transform.position - camera.position;
                    if (look.sqrMagnitude > 0.001f)
                        CreativeZoneCameraController.ApplyExternalTransform(camera.position, Quaternion.LookRotation(look));
                }
            }
            if (Isolated) ReplaySession.FollowCamera();
        }

        /// <summary>The object showing a recorded entity: its copy, or the real object the session rebuilt.</summary>
        internal GameObject? FindTarget(long user, uint id)
        {
            GameObject? ghost = _ghostManager?.GetWorldGhostObject(user, id);
            if (ghost != null || _scene == null || !_scene.Contains(user, id) || ZNetScene.instance == null) return ghost;
            return ZNetScene.instance.FindInstance(new ZDOID(user, id));
        }

        private void Hide(GameObject root)
        {
            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>())
            {
                if (!_hidden.ContainsKey(renderer)) _hidden.Add(renderer, renderer.enabled);
                renderer.enabled = false;
            }
        }

        private static void ApplyRecordedCamera(ReplayFrame a, ReplayFrame b, float t) =>
            CreativeZoneCameraController.ApplyExternalTransform(Vector3.Lerp(a.CameraPosition, b.CameraPosition, t),
                Quaternion.Slerp(a.CameraRotation, b.CameraRotation, t));

        private void FirePendingTriggers()
        {
            if (_replay == null || _ghostManager == null)
                return;

            var triggers = _replay.TriggerEvents;
            while (_lastTriggerIndex < triggers.Count && triggers[_lastTriggerIndex].Time <= _playbackTime)
            {
                var te = triggers[_lastTriggerIndex];
                _ghostManager.FireTrigger(te.ZdoUserID, te.ZdoID, te.TriggerName);
                _lastTriggerIndex++;
            }
        }

        private int FindFrameIndex(float time)
        {
            if (_replay == null || _replay.Frames.Count == 0)
                return -1;

            var frames = _replay.Frames;
            int lo = 0, hi = frames.Count - 1;

            if (time <= frames[0].Time)
                return 0;
            if (time >= frames[hi].Time)
                return hi;

            while (lo < hi - 1)
            {
                int mid = (lo + hi) / 2;
                if (frames[mid].Time <= time)
                    lo = mid;
                else
                    hi = mid;
            }

            return lo;
        }

        private int FindTriggerIndex(float time)
        {
            if (_replay == null)
                return 0;

            var triggers = _replay.TriggerEvents;
            int lo = 0, hi = triggers.Count;

            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (triggers[mid].Time <= time)
                    lo = mid + 1;
                else
                    hi = mid;
            }

            return lo;
        }

        private void OnDestroy()
        {
            if (IsPlaying)
                StopPlayback();
        }
    }
}
