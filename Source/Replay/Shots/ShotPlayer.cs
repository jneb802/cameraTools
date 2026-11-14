using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace cameraTools.Replay.Shots
{
    /// <summary>
    /// Plays shot files against the playing replay. The shot owns the replay clock while it runs, so the camera and the
    /// recorded action stay in step at any speed. Check, preview and render use the same evaluation.
    /// Runs after ReplayPlayer so actors are placed for the frame before the camera follows them.
    /// </summary>
    [DefaultExecutionOrder(20001)]
    public sealed class ShotPlayer : MonoBehaviour
    {
        internal static ShotPlayer Instance { get; private set; } = null!;
        private enum Mode { Idle, Play, Hold, Render }

        private Mode _mode;
        private ShotEvaluator? _evaluator;
        private string _shotPath = "";
        private ShotFile? _editing;
        private string _editingPath = "";
        private float _target;
        private Vector3 _smoothLook;
        private bool _hasSmoothLook;
        private float _lastFov = ShotEvaluator.DefaultFov;
        private string _job = "";
        private string _jobResult = "";
        private bool _cancel;
        private readonly Queue<(string Path, RenderOptions Options)> _queue = new();
        private bool? _autoFocus;
        private bool _dofWasEnabled;

        internal sealed class RenderOptions
        {
            public int Fps = 60;
            public int Width;
            public int Height;
            public int Supersample = 1;
            public bool Video;
            public bool Audio = true;
            public bool Overlays;
            public bool KeepFrames;
            /// <summary>Test option: caps the game's frame rate during the render to prove output does not depend on it.</summary>
            public int FpsCap;
        }

        private void Awake() => Instance = this;

        internal static string Directory => Path.Combine(Paths.ConfigPath, "cameraTools", "shots");

        internal static string Resolve(string name)
        {
            if (File.Exists(name)) return Path.GetFullPath(name);
            string path = Path.Combine(Directory, name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? name : name + ".json");
            if (File.Exists(path)) return path;
            throw new ArgumentException($"Shot file not found: {name} (looked in {Directory})");
        }

        internal static ShotFile Read(string path)
        {
            try { return JsonConvert.DeserializeObject<ShotFile>(File.ReadAllText(path)) ?? throw new ArgumentException("The shot file is empty"); }
            catch (JsonException error) { throw new ArgumentException($"{Path.GetFileName(path)} is not a valid shot file: {error.Message}"); }
        }

        /// <summary>Body heights of every recorded character: saved with version 10 files, otherwise read from loaded prefabs.</summary>
        private static Dictionary<(long, uint), float> Heights(ReplayPlayer player)
        {
            Dictionary<(long, uint), float> heights = new();
            Dictionary<int, (float Height, float Radius)> sizes = CombatData.Sizes(player.File.Info);
            foreach (ActorTrack track in player.Tracks.All)
            {
                (long, uint) key = (track.User, track.Id);
                if (player.File.Actors.TryGetValue(key, out ActorInfo info) && info.Height > 0f) heights[key] = info.Height;
                else if (sizes.TryGetValue(track.Prefab, out (float Height, float Radius) size)) heights[key] = size.Height;
            }
            return heights;
        }

        internal static ShotEvaluator Evaluator(string path)
        {
            if (!ReplayPlayer.IsPlaying) throw new InvalidOperationException("Open the shot's replay first");
            ReplayPlayer player = ReplayPlayer.Instance;
            ShotFile shot = Read(path);
            string playing = Path.GetFileNameWithoutExtension(player.FilePath ?? player.File.Info.Name);
            if (shot.replay.Length > 0 && !string.Equals(shot.replay, playing, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"The shot is for replay '{shot.replay}' but '{playing}' is playing");
            if (shot.end > player.Duration + 0.01f) throw new ArgumentException($"Shot end {shot.end} is after the replay ends ({player.Duration:F2})");
            return new ShotEvaluator(shot, player.Tracks, Heights(player), player.Events);
        }

        private static float Clamp(float value) => Mathf.Clamp(value, 0f, ReplayPlayer.Instance.Duration);

        // The shot clock: the replay time this frame should show. Set before the frame by play, preview and render.
        private float Clock(float realDelta)
        {
            if (_mode == Mode.Play && _evaluator != null)
            {
                float speed = _evaluator.SpeedAt(_target);
                _target += realDelta * speed;
                SetTimeScale(_evaluator.SpeedAt(_target));
                if (_target >= _evaluator.Shot.end) { _target = _evaluator.Shot.end; Finish(); }
            }
            return _target;
        }

        private static void SetTimeScale(float scale) => Game.m_timeScale = Mathf.Clamp(scale, 0.05f, 4f);

        private void Begin(ShotEvaluator evaluator, Mode mode, float time)
        {
            if (_mode == Mode.Render) throw new InvalidOperationException("A render is running; use ct_shot render cancel");
            ReplayPlayer player = ReplayPlayer.Instance;
            player.Detach();
            CreativeZoneCameraController.StopOrbit();
            CreativeZoneCameraController.StopMove();
            cameraTools.Markers.PathPlayer.Stop();
            _evaluator = evaluator;
            _mode = mode;
            _target = Clamp(time);
            _hasSmoothLook = false;
            player.Seek(_target);
            player.SetPaused(false);
            player.Clock = Clock;
            BeginFocus();
        }

        private void Finish()
        {
            if (_mode == Mode.Render) return;
            _mode = Mode.Hold;
        }

        /// <summary>The ct_shot stop command: also ends a running preview or render.</summary>
        internal string StopCommand()
        {
            _cancel = true;
            return Stop();
        }

        internal string Stop()
        {
            if (ReplayPlayer.IsPlaying)
            {
                ReplayPlayer.Instance.Clock = null;
                ReplayPlayer.Instance.SetPaused(true);
            }
            if (_mode != Mode.Render) _mode = Mode.Idle;
            SetTimeScale(1f);
            EndFocus();
            RestoreHidden();
            return Status();
        }

        internal string Status()
        {
            string shot = _evaluator != null ? $"shot={_evaluator.Shot.name} file={_shotPath} start={ShotEvaluator.F(_evaluator.Shot.start)} end={ShotEvaluator.F(_evaluator.Shot.end)} " +
                $"outputSeconds={ShotEvaluator.F(_evaluator.OutputDuration())}" : "shot=none";
            string replay = ReplayPlayer.IsPlaying ? $"replayTime={ReplayPlayer.Instance.PlaybackTime:F3}" : "replayTime=none";
            string speed = _evaluator != null ? $"speed={_evaluator.SpeedAt(_target):F2}" : "speed=1";
            string job = _job.Length > 0 ? $" job=\"{_job}\"" : "";
            string result = _jobResult.Length > 0 ? $" last=\"{_jobResult}\"" : "";
            string camera = GameCamera.m_instance != null ? $" camera={ShotEvaluator.F(GameCamera.m_instance.transform.position)}" : "";
            return $"mode={_mode.ToString().ToLowerInvariant()} {shot} {replay} {speed} timeScale={Time.timeScale:F2} fov={CurrentFov():F1}{camera}{job}{result}";
        }

        private static float CurrentFov()
        {
            Camera? camera = Utils.GetMainCamera();
            return camera != null ? camera.fieldOfView : 0f;
        }

        internal static void SetFov(float fov)
        {
            GameCamera camera = GameCamera.m_instance;
            if (camera == null) return;
            fov = Mathf.Clamp(fov, 1f, 160f);
            camera.m_camera.fieldOfView = fov;
            if (camera.m_skyCamera != null) camera.m_skyCamera.fieldOfView = fov;
        }

        // Renderers and lights a shot hid, with the state to restore.
        private readonly Dictionary<Renderer, bool> _hiddenRenderers = new();
        private readonly Dictionary<Light, bool> _hiddenLights = new();

        private void HideRequested()
        {
            if (_evaluator == null || !_evaluator.Shot.hide.Any(h => string.Equals(h, "drops", StringComparison.OrdinalIgnoreCase)) || ZNetScene.instance == null) return;
            foreach (ZNetView view in ZNetScene.instance.m_instances.Values)
            {
                if (view == null || view.GetComponent<ItemDrop>() == null) continue;
                foreach (Renderer renderer in view.GetComponentsInChildren<Renderer>(true))
                {
                    if (!_hiddenRenderers.ContainsKey(renderer)) _hiddenRenderers[renderer] = renderer.enabled;
                    renderer.enabled = false;
                }
                foreach (Light light in view.GetComponentsInChildren<Light>(true))
                {
                    if (!_hiddenLights.ContainsKey(light)) _hiddenLights[light] = light.enabled;
                    light.enabled = false;
                }
            }
        }

        private void RestoreHidden()
        {
            foreach (KeyValuePair<Renderer, bool> entry in _hiddenRenderers) if (entry.Key != null) entry.Key.enabled = entry.Value;
            foreach (KeyValuePair<Light, bool> entry in _hiddenLights) if (entry.Key != null) entry.Key.enabled = entry.Value;
            _hiddenRenderers.Clear();
            _hiddenLights.Clear();
        }

        private void LateUpdate()
        {
            if (_mode == Mode.Idle || _evaluator == null) return;
            HideRequested();
            if (!ReplayPlayer.IsPlaying) { _mode = Mode.Idle; _evaluator = null; SetTimeScale(1f); return; }
            Apply(_target, _mode == Mode.Render ? 1f / Mathf.Max(1, _render?.Fps ?? 60) : Time.unscaledDeltaTime, smooth: _mode != Mode.Hold || _hasSmoothLook);
        }

        private void Apply(float time, float delta, bool smooth)
        {
            ShotPose pose = _evaluator!.Evaluate(time);
            Vector3 look = pose.LookAt;
            float smoothing = _evaluator.Shot.lookSmoothing;
            if (smooth && smoothing > 0f && _hasSmoothLook) look = Vector3.Lerp(_smoothLook, look, 1f - Mathf.Exp(-delta / smoothing));
            _smoothLook = look;
            _hasSmoothLook = true;
            Quaternion rotation = ShotEvaluator.Rotation(pose.Position, look, pose.Roll, _evaluator.Shake(time));
            CreativeZoneCameraController.ApplyExternalTransform(pose.Position, rotation);
            if (GameCamera.m_instance != null) GameCamera.m_instance.transform.rotation = rotation;
            SetFov(pose.Fov);
            _lastFov = pose.Fov;
            Focus(pose.Position, look);
        }

        // Depth of field: the game's DepthOfField effect on the main camera, driven by the shot when the shot asks for it.
        private void BeginFocus()
        {
            DofSettings? dof = _evaluator?.Shot.dof;
            CameraEffects? effects = CameraEffects.m_instance;
            if (dof == null || !dof.enabled || effects == null) return;
            if (Traverse.Create(effects).Field("m_dof").GetValue() is not Behaviour behaviour) return;
            if (!_autoFocus.HasValue) { _autoFocus = effects.m_dofAutoFocus; _dofWasEnabled = behaviour.enabled; }
            effects.m_dofAutoFocus = false;
            behaviour.enabled = true;
        }

        private void EndFocus()
        {
            CameraEffects? effects = CameraEffects.m_instance;
            if (effects == null || !_autoFocus.HasValue) return;
            effects.m_dofAutoFocus = _autoFocus.Value;
            if (Traverse.Create(effects).Field("m_dof").GetValue() is Behaviour behaviour) behaviour.enabled = _dofWasEnabled;
            _autoFocus = null;
        }

        private void Focus(Vector3 position, Vector3 look)
        {
            DofSettings? dof = _evaluator?.Shot.dof;
            CameraEffects? effects = CameraEffects.m_instance;
            if (dof == null || !dof.enabled || effects == null) return;
            object? component = Traverse.Create(effects).Field("m_dof").GetValue();
            if (component == null) return;
            float distance = dof.focus != null && dof.focus.Type is Newtonsoft.Json.Linq.JTokenType.Float or Newtonsoft.Json.Linq.JTokenType.Integer
                ? (float)dof.focus : Vector3.Distance(position, look);
            Traverse dofFields = Traverse.Create(component);
            dofFields.Field("focalLength").SetValue(distance);
            dofFields.Field("focalSize").SetValue(dof.focalSize);
            dofFields.Field("aperture").SetValue(dof.aperture);
        }

        internal string Play(string path, float? from = null)
        {
            ShotEvaluator evaluator = Evaluator(path);
            if (from.HasValue && (from < evaluator.Shot.start || from >= evaluator.Shot.end))
                throw new ArgumentException($"Start time {from} is outside the shot ({evaluator.Shot.start}-{evaluator.Shot.end})");
            _shotPath = path;
            Begin(evaluator, Mode.Play, from ?? evaluator.Shot.start);
            return $"ACCEPTED: playing; poll ct_shot status until mode=hold. " + Status();
        }

        // ---------------------------------------------------------------- check

        private static readonly int SolidMask = LayerMask.GetMask("piece", "static_solid", "Default", "Default_small", "vehicle", "terrain");
        private static readonly int TerrainMask = LayerMask.GetMask("terrain");

        /// <summary>
        /// Steps the shot every 0.05 s of replay time without rendering and reports time ranges where the camera is under
        /// the ground or inside an object, the view to the look target is blocked, a keepInFrame actor leaves the frame,
        /// or the camera is within 0.5 m of a body.
        /// </summary>
        internal string Check(string path, float aspect)
        {
            CanopyCache.Clear();
            ShotEvaluator evaluator = Evaluator(path);
            ReplayPlayer player = ReplayPlayer.Instance;
            Dictionary<string, (float From, float To, string Detail)> open = new();
            List<Dictionary<string, object>> issues = new();
            void Close(string key)
            {
                if (!open.TryGetValue(key, out (float From, float To, string Detail) range)) return;
                open.Remove(key);
                string[] parts = key.Split('|');
                issues.Add(new Dictionary<string, object> { ["type"] = parts[0], ["subject"] = parts.Length > 1 ? parts[1] : "", ["from"] = Math.Round(range.From, 2), ["to"] = Math.Round(range.To, 2), ["detail"] = range.Detail });
            }
            List<ActorTrack> actors = player.Tracks.All.Where(t => t.Kind == 0 && !t.IsProjectile).ToList();
            Dictionary<(long, uint), float> radii = new();
            Dictionary<int, (float Height, float Radius)> sizes = CombatData.Sizes(player.File.Info);
            foreach (ActorTrack track in actors)
                radii[(track.User, track.Id)] = player.File.Actors.TryGetValue((track.User, track.Id), out ActorInfo info) && info.Radius > 0f ? info.Radius
                    : sizes.TryGetValue(track.Prefab, out (float Height, float Radius) size) ? size.Radius : 0.5f;
            HashSet<(long, uint)> deaths = new(player.File.Deaths.Select(death => (death.User, death.Id)));
            int steps = 0;
            for (float time = evaluator.Shot.start; time <= evaluator.Shot.end + 0.0001f; time += 0.05f)
            {
                steps++;
                ShotPose pose = evaluator.Evaluate(time);
                HashSet<string> now = new();
                void Mark(string key, string detail)
                {
                    now.Add(key);
                    if (open.TryGetValue(key, out (float From, float To, string Detail) range)) open[key] = (range.From, time, range.Detail);
                    else open[key] = (time, time, detail);
                }
                if (Physics.Raycast(pose.Position + Vector3.up * 1000f, Vector3.down, out RaycastHit ground, 2000f, TerrainMask, QueryTriggerInteraction.Ignore) && pose.Position.y < ground.point.y + 0.1f)
                    Mark("below_ground", $"camera {ShotEvaluator.F(pose.Position.y - ground.point.y)} m from the terrain surface");
                Collider[] inside = Physics.OverlapSphere(pose.Position, 0.15f, SolidMask & ~TerrainMask, QueryTriggerInteraction.Ignore);
                if (inside.Length > 0) Mark("inside_object|" + Blocker(inside[0]), "camera inside a solid object");
                Vector3 toLook = pose.LookAt - pose.Position;
                float distance = toLook.magnitude;
                if (distance > 0.6f && Physics.Raycast(pose.Position, toLook / distance, out RaycastHit blocker, distance - 0.5f, SolidMask, QueryTriggerInteraction.Ignore))
                    Mark("view_blocked|" + Blocker(blocker.collider), $"blocked {ShotEvaluator.F(blocker.distance)} m from the camera, look target {ShotEvaluator.F(distance)} m away");
                string? foliage = Foliage(pose.Position, pose.LookAt, out bool insideFoliage);
                if (foliage != null) Mark((insideFoliage ? "inside_foliage|" : "view_foliage|") + foliage, insideFoliage ? "camera inside a tree's branches" : "view passes through a tree's branches");
                Matrix4x4 projection = Matrix4x4.Perspective(pose.Fov, aspect, 0.1f, 5000f);
                Matrix4x4 view = Matrix4x4.TRS(pose.Position, ShotEvaluator.Rotation(pose.Position, pose.LookAt, pose.Roll, Vector3.zero), Vector3.one).inverse;
                Matrix4x4 clip = projection * Matrix4x4.Scale(new Vector3(1, 1, -1)) * view;
                foreach (string actor in evaluator.Shot.keepInFrame)
                {
                    ActorTrack track = player.Tracks.Get(actor);
                    if (!track.Sample(time, out Vector3 feet, out _))
                    {
                        // An actor that died is removed (its ragdoll takes over); leaving the frame then is expected.
                        if (!(time > track.End && deaths.Contains((track.User, track.Id)))) Mark("keep_absent|" + actor, "actor not present at this time");
                        continue;
                    }
                    float height = evaluator.Height(track);
                    Vector2? center = Viewport(clip, feet + Vector3.up * height * 0.5f);
                    bool anyVisible = new[] { feet, feet + Vector3.up * height * 0.5f, feet + Vector3.up * height }.Any(p => Viewport(clip, p) is Vector2 v && v.x >= 0 && v.x <= 1 && v.y >= 0 && v.y <= 1);
                    if (!anyVisible) Mark("keep_off_screen|" + actor, "actor fully outside the frame");
                    else if (center is not Vector2 c || c.x < 0.1f || c.x > 0.9f || c.y < 0.1f || c.y > 0.9f) Mark("keep_near_edge|" + actor, "actor centre outside the middle 80% of the frame");
                }
                foreach (ActorTrack track in actors)
                {
                    if (!track.Sample(time, out Vector3 feet, out _)) continue;
                    float height = evaluator.Height(track);
                    Vector3 closest = new(feet.x, Mathf.Clamp(pose.Position.y, feet.y, feet.y + height), feet.z);
                    float gap = Vector3.Distance(pose.Position, closest) - radii[(track.User, track.Id)];
                    if (gap < 0.5f) Mark("too_close|" + track.Key, $"camera {ShotEvaluator.F(gap)} m from {player.File.Info.PrefabName(track.Prefab)}");
                }
                foreach (string key in open.Keys.ToList()) if (!now.Contains(key)) Close(key);
            }
            foreach (string key in open.Keys.ToList()) Close(key);
            string report = Path.ChangeExtension(path, ".check.json");
            File.WriteAllText(report, JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                ["shot"] = evaluator.Shot.name, ["file"] = path, ["steps"] = steps, ["stepSeconds"] = 0.05, ["aspect"] = Math.Round(aspect, 4),
                ["outputSeconds"] = Math.Round(evaluator.OutputDuration(), 3), ["issues"] = issues
            }, Formatting.Indented));
            _jobResult = issues.Count == 0 ? $"check {evaluator.Shot.name}: no issues" : $"check {evaluator.Shot.name}: {issues.Count} issues";
            string lines = string.Join("\n", issues.Select(i => $"issue type={i["type"]} subject={i["subject"]} from={i["from"]} to={i["to"]} detail=\"{i["detail"]}\""));
            return (issues.Count == 0 ? "OK: issues=0" : $"OK: issues={issues.Count}") + $" report={report}" + (lines.Length > 0 ? "\n" + lines : "");
        }

        private static readonly string[] TreeNames = { "tree", "beech", "birch", "oak", "yggashoot", "pine", "fir", "shrub", "bush" };
        private static readonly Dictionary<GameObject, Bounds> CanopyCache = new();

        /// <summary>
        /// Tree branches have no colliders, so the ray checks cannot see them. A tree's drawn size shrunk to its middle,
        /// above its trunk, stands in for the branches: the camera inside it, or the view through it, is reported.
        /// </summary>
        private static string? Foliage(Vector3 camera, Vector3 look, out bool inside)
        {
            inside = false;
            Vector3 direction = look - camera;
            float distance = direction.magnitude;
            if (distance < 0.5f) return null;
            RaycastHit[] hits = Physics.SphereCastAll(camera, 5f, direction / distance, distance, LayerMask.GetMask("static_solid", "Default", "Default_small", "piece"), QueryTriggerInteraction.Ignore);
            Collider[] around = Physics.OverlapSphere(camera, 5f, LayerMask.GetMask("static_solid", "Default", "Default_small", "piece"), QueryTriggerInteraction.Ignore);
            foreach (Collider collider in hits.Select(hit => hit.collider).Concat(around))
            {
                if (collider == null) continue;
                GameObject root = collider.GetComponentInParent<ZNetView>()?.gameObject ?? collider.transform.root.gameObject;
                string name = Utils.GetPrefabName(root);
                string lower = name.ToLowerInvariant();
                if (!TreeNames.Any(lower.Contains)) continue;
                if (!CanopyCache.TryGetValue(root, out Bounds canopy))
                {
                    Renderer[] renderers = root.GetComponentsInChildren<Renderer>().Where(r => r.enabled && r is not ParticleSystemRenderer).ToArray();
                    if (renderers.Length == 0) continue;
                    canopy = renderers[0].bounds;
                    foreach (Renderer renderer in renderers) canopy.Encapsulate(renderer.bounds);
                    // The middle of the drawn shape, starting a fifth of the way up: branches, not the trunk base or the tips.
                    float bottom = canopy.min.y + canopy.size.y * 0.2f;
                    canopy = new Bounds(new Vector3(canopy.center.x, (bottom + canopy.max.y) / 2f, canopy.center.z),
                        new Vector3(canopy.size.x * 0.55f, canopy.max.y - bottom, canopy.size.z * 0.55f));
                    CanopyCache[root] = canopy;
                }
                if (canopy.Contains(camera)) { inside = true; return name; }
                if (canopy.IntersectRay(new Ray(camera, direction / distance), out float along) && along < distance - 0.5f && !canopy.Contains(look)) return name;
            }
            return null;
        }

        private static string Blocker(Collider collider) =>
            collider.gameObject.layer == LayerMask.NameToLayer("terrain") ? "terrain" : Utils.GetPrefabName((collider.GetComponentInParent<ZNetView>()?.gameObject ?? collider.transform.root.gameObject));

        private static Vector2? Viewport(Matrix4x4 clip, Vector3 point)
        {
            Vector4 p = clip * new Vector4(point.x, point.y, point.z, 1f);
            if (p.w <= 0.0001f) return null;
            return new Vector2((p.x / p.w + 1f) * 0.5f, (p.y / p.w + 1f) * 0.5f);
        }

        // ---------------------------------------------------------------- preview

        internal string Preview(string path, List<float> times, int count, bool overlays)
        {
            ShotEvaluator evaluator = Evaluator(path);
            if (times.Count == 0)
            {
                int n = Mathf.Max(1, count);
                for (int index = 0; index < n; index++) times.Add(evaluator.Shot.start + (evaluator.Shot.end - evaluator.Shot.start) * (n == 1 ? 0.5f : index / (float)(n - 1)));
                foreach (ReplayEvent tag in ReplayPlayer.Instance.Events.Events)
                    if (tag.Time > evaluator.Shot.start && tag.Time < evaluator.Shot.end && (tag.Type == ReplayEventType.Death || tag.Type == ReplayEventType.Hit)) times.Add(tag.Time);
            }
            foreach (float time in times)
                if (time < evaluator.Shot.start - 0.001f || time > evaluator.Shot.end + 0.001f) throw new ArgumentException($"Preview time {time} is outside the shot ({evaluator.Shot.start}-{evaluator.Shot.end})");
            _shotPath = path;
            StartCoroutine(PreviewRoutine(evaluator, times.Distinct().OrderBy(t => t).ToList(), overlays));
            return $"ACCEPTED: saving {times.Count} stills; poll ct_shot status until mode=idle";
        }

        private IEnumerator PreviewRoutine(ShotEvaluator evaluator, List<float> times, bool overlays)
        {
            string directory = Path.Combine(Path.GetDirectoryName(_shotPath)!, "previews");
            System.IO.Directory.CreateDirectory(directory);
            List<string> written = new();
            using (new CleanFrame(overlays))
            {
                Begin(evaluator, Mode.Hold, times[0]);
                _cancel = false;
                foreach (float time in times)
                {
                    if (_cancel) break;
                    _job = $"preview {written.Count + 1}/{times.Count}";
                    ReplayPlayer.Instance.Seek(time);
                    _target = time;
                    _hasSmoothLook = false;
                    // A seek rebuilds the recorded objects; the game creates them over the next frames.
                    yield return WaitForActors(time);
                    for (int frame = 0; frame < 6; frame++) yield return null;
                    yield return new WaitForEndOfFrame();
                    Texture2D image = ScreenCapture.CaptureScreenshotAsTexture();
                    string file = Path.Combine(directory, $"{evaluator.Shot.name}-{time.ToString("F2", CultureInfo.InvariantCulture)}.png");
                    File.WriteAllBytes(file, image.EncodeToPNG());
                    Destroy(image);
                    written.Add(file);
                }
            }
            Stop();
            _job = "";
            _jobResult = $"preview {evaluator.Shot.name}: {written.Count} stills in {directory}";
        }

        /// <summary>
        /// Waits until every character recorded within 60 m of the shot's camera at the time has its object in the scene,
        /// for at most 5 seconds of real time.
        /// </summary>
        private IEnumerator WaitForActors(float time)
        {
            ReplayPlayer player = ReplayPlayer.Instance;
            Vector3 camera = _evaluator != null ? _evaluator.Evaluate(Mathf.Clamp(time, _evaluator.Shot.start, _evaluator.Shot.end)).Position : GameCamera.m_instance.transform.position;
            List<ActorTrack> expected = new();
            foreach (ActorTrack track in player.Tracks.All)
                if (track.Kind == 0 && !track.IsProjectile && track.Sample(time, out Vector3 position, out _) && Vector3.Distance(position, camera) < 60f)
                    expected.Add(track);
            // A seek destroys changed objects at the end of the frame and the game recreates them over the next frames;
            // until then the old object still answers. Let the destruction happen, then require five settled frames.
            yield return null;
            yield return null;
            float deadline = Time.realtimeSinceStartup + 5f;
            int settled = 0;
            while (Time.realtimeSinceStartup < deadline && settled < 5)
            {
                settled = expected.All(track => player.FindTarget(track.User, track.Id) is GameObject shown && shown != null && shown.activeInHierarchy) ? settled + 1 : 0;
                yield return null;
            }
            if (settled >= 5) yield break;
            List<string> missing = expected.Where(track => player.FindTarget(track.User, track.Id) == null).Select(track => track.Key).ToList();
            if (missing.Count > 0) cameraToolsPlugin.TemplateLogger.LogWarning($"[Shots] Characters still missing at {time:F2}s after 5 s: {string.Join(", ", missing)}");
        }

        /// <summary>Hides everything that is not the game picture for as long as it lives: window, overlays, console, HUD, cursor.</summary>
        private sealed class CleanFrame : IDisposable
        {
            private readonly bool _panel, _hidden, _hud;
            internal CleanFrame(bool overlays)
            {
                _panel = CameraToolsPanel.Visible;
                _hidden = FightOverlay.Hidden;
                _hud = Hud.instance != null && Hud.instance.m_userHidden;
                CameraToolsPanel.Visible = false;
                FightOverlay.Hidden = !overlays;
                if (Hud.instance != null) Hud.instance.m_userHidden = true;
                if (Console.instance != null) Console.instance.m_chatWindow.gameObject.SetActive(false);
                Cursor.visible = false;
            }

            public void Dispose()
            {
                CameraToolsPanel.Visible = _panel;
                FightOverlay.Hidden = _hidden;
                if (Hud.instance != null) Hud.instance.m_userHidden = _hud;
            }
        }

        // ---------------------------------------------------------------- render

        private RenderOptions? _render;
        // True from the moment a render is accepted until its files are written, including the gap between queued shots.
        private bool _rendering;

        internal string Render(string path, RenderOptions options)
        {
            if (_rendering) { _queue.Enqueue((path, options)); return $"ACCEPTED: queued {Path.GetFileName(path)} position={_queue.Count}"; }
            ShotEvaluator evaluator = Evaluator(path);
            _shotPath = path;
            _rendering = true;
            StartCoroutine(RenderRoutine(evaluator, options));
            return $"ACCEPTED: rendering {evaluator.Shot.name}; poll ct_shot render status until it reports OK";
        }

        internal string RenderAll(string folder, RenderOptions options)
        {
            string directory = System.IO.Directory.Exists(folder) ? folder : Path.Combine(Directory, folder);
            if (!System.IO.Directory.Exists(directory)) throw new ArgumentException($"No shot folder {folder}");
            List<string> files = System.IO.Directory.GetFiles(directory, "*.json")
                .Where(file => !file.EndsWith(".check.json") && !file.EndsWith(".render.json") && !file.EndsWith(".cues.json")).OrderBy(file => file).ToList();
            if (files.Count == 0) throw new ArgumentException($"No shot files in {directory}");
            foreach (string file in files) Evaluator(file);
            foreach (string file in files.Skip(_rendering ? 0 : 1)) _queue.Enqueue((file, options));
            if (!_rendering) Render(files[0], options);
            return $"ACCEPTED: rendering {files.Count} shots; poll ct_shot render status until queue=0 and it reports OK";
        }

        internal string RenderStatus()
        {
            if (_rendering || _mode == Mode.Render || _queue.Count > 0) return $"PENDING: {_job} queue={_queue.Count}";
            return $"OK: {_jobResult}";
        }

        internal string Cancel()
        {
            _queue.Clear();
            _cancel = true;
            return "OK: cancelling; poll ct_shot render status";
        }

        private IEnumerator RenderRoutine(ShotEvaluator evaluator, RenderOptions options)
        {
            Stopwatch clock = Stopwatch.StartNew();
            ShotFile shot = evaluator.Shot;
            string root = Path.Combine(Paths.ConfigPath, "cameraTools", "renders", shot.name);
            if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, true);
            string frames = Path.Combine(root, "frames");
            System.IO.Directory.CreateDirectory(frames);
            _render = options;
            _cancel = false;
            int originalWidth = Screen.width, originalHeight = Screen.height;
            List<Task> writes = new();
            List<Dictionary<string, object>> frameLog = new();
            List<float> audio = new();
            string audioMethod = "none";
            string error = "";
            float step = 1f / options.Fps;
            int frameRate = Application.targetFrameRate, vSync = QualitySettings.vSyncCount;
            if (options.FpsCap > 0) { QualitySettings.vSyncCount = 0; Application.targetFrameRate = options.FpsCap; }
            using (new CleanFrame(options.Overlays))
            {
                if (options.Width > 0 && (options.Width != Screen.width || options.Height != Screen.height))
                {
                    Screen.SetResolution(options.Width, options.Height, Screen.fullScreenMode);
                    for (int wait = 0; wait < 30 && (Screen.width != options.Width || Screen.height != options.Height); wait++) yield return null;
                    if (Screen.width != options.Width || Screen.height != options.Height)
                        error = $"The display did not accept {options.Width}x{options.Height}; it is {Screen.width}x{Screen.height}";
                }
                if (error.Length == 0)
                {
                    // Warm up from 2 s before the start at normal speed, so effects already in progress exist.
                    float warm = Mathf.Max(0f, shot.start - 2f);
                    Begin(evaluator, Mode.Render, warm);
                    yield return WaitForActors(warm);
                    Time.captureDeltaTime = step;
                    SetTimeScale(1f);
                    _job = $"render {shot.name} warm-up";
                    for (float time = warm; time < shot.start - 0.0001f && !_cancel; time = Mathf.Min(shot.start, time + step))
                    {
                        _target = Mathf.Min(shot.start, time + step);
                        yield return null;
                    }
                    // A shot without a speed key at its start begins at that speed.
                    float replayTime = shot.start;
                    float outputTime = 0f;
                    int total = Mathf.CeilToInt(evaluator.OutputDuration() * options.Fps);
                    bool recordAudio = options.Audio;
                    if (recordAudio)
                    {
                        try { AudioRenderer.Start(); audioMethod = "AudioRenderer"; }
                        catch (Exception audioError) { recordAudio = false; audioMethod = "cues only: " + audioError.Message; }
                    }
                    int index = 0;
                    // Half a frame of tolerance, so summing 1/fps steps cannot add a frame at the end.
                    while (replayTime < shot.end - 0.5f * step * evaluator.SpeedAt(replayTime) && !_cancel)
                    {
                        _target = replayTime;
                        SetTimeScale(evaluator.SpeedAt(replayTime));
                        _job = $"render {shot.name} frame={index + 1}/{total}";
                        yield return new WaitForEndOfFrame();
                        Texture2D image = ScreenCapture.CaptureScreenshotAsTexture(Mathf.Clamp(options.Supersample, 1, 4));
                        byte[] raw = image.GetRawTextureData();
                        GraphicsFormat format = image.graphicsFormat;
                        uint width = (uint)image.width, height = (uint)image.height;
                        Destroy(image);
                        string file = Path.Combine(frames, $"{index:D6}.png");
                        writes.RemoveAll(task => task.IsCompleted);
                        if (writes.Count >= 8) Task.WaitAny(writes.ToArray());
                        writes.Add(Task.Run(() => File.WriteAllBytes(file, ImageConversion.EncodeArrayToPNG(raw, format, width, height))));
                        if (recordAudio)
                        {
                            int samples = AudioRenderer.GetSampleCountForCaptureFrame();
                            int channels = AudioChannels();
                            NativeArray<float> buffer = new(samples * channels, Allocator.Temp);
                            if (AudioRenderer.Render(buffer)) audio.AddRange(buffer);
                            buffer.Dispose();
                        }
                        Vector3 position = GameCamera.m_instance != null ? GameCamera.m_instance.transform.position : Vector3.zero;
                        frameLog.Add(new Dictionary<string, object>
                        {
                            ["frame"] = index, ["replayTime"] = Math.Round(replayTime, 4), ["outputTime"] = Math.Round(outputTime, 4),
                            ["speed"] = Math.Round(evaluator.SpeedAt(replayTime), 3), ["camera"] = ReplayEvents.Round(position), ["fov"] = Math.Round(_lastFov, 2)
                        });
                        index++;
                        outputTime += step;
                        replayTime += step * evaluator.SpeedAt(replayTime);
                    }
                    if (recordAudio) AudioRenderer.Stop();
                    Task.WaitAll(writes.ToArray());
                    Time.captureDeltaTime = 0f;
                    Stop();
                    _mode = Mode.Idle;
                    _job = $"render {shot.name} writing files";
                    string wav = Path.Combine(root, shot.name + ".wav");
                    if (audio.Count > 0) WriteWav(wav, audio, AudioSettings.outputSampleRate, AudioChannels());
                    WriteCues(Path.Combine(root, shot.name + ".cues.json"), evaluator);
                    File.WriteAllText(Path.Combine(root, shot.name + ".render.json"), JsonConvert.SerializeObject(new Dictionary<string, object>
                    {
                        ["shot"] = shot, ["fps"] = options.Fps, ["supersample"] = options.Supersample, ["width"] = Screen.width * options.Supersample, ["height"] = Screen.height * options.Supersample,
                        ["frames"] = index, ["outputSeconds"] = Math.Round(index * step, 4), ["audio"] = audio.Count > 0 ? Path.GetFileName(wav) : audioMethod,
                        ["audioMethod"] = audioMethod, ["renderSeconds"] = Math.Round(clock.Elapsed.TotalSeconds, 2), ["cancelled"] = _cancel, ["frameLog"] = frameLog
                    }, Formatting.Indented));
                    if (options.Video && !_cancel)
                    {
                        _job = $"render {shot.name} encoding video";
                        Task<string> encode = Task.Run(() => Encode(root, shot.name, options.Fps, audio.Count > 0 ? wav : null));
                        while (!encode.IsCompleted) yield return null;
                        error = encode.Result;
                        // Frames take several megabytes each; the encoded video replaces them unless asked to keep them.
                        if (error.Length == 0 && !options.KeepFrames) System.IO.Directory.Delete(frames, true);
                    }
                    _jobResult = (_cancel ? "cancelled " : "rendered ") + $"{shot.name}: {index} frames at {options.Fps} fps in {clock.Elapsed.TotalSeconds:F0}s folder={root} audio={audioMethod}" + (error.Length > 0 ? " error=" + error : "");
                }
                else _jobResult = $"render {shot.name} failed: {error}";
                if (Screen.width != originalWidth || Screen.height != originalHeight)
                {
                    // Wait for the change to apply, so a queued render does not record the size in between as its original.
                    Screen.SetResolution(originalWidth, originalHeight, Screen.fullScreenMode);
                    for (int wait = 0; wait < 60 && (Screen.width != originalWidth || Screen.height != originalHeight); wait++) yield return null;
                }
            }
            Time.captureDeltaTime = 0f;
            Application.targetFrameRate = frameRate;
            QualitySettings.vSyncCount = vSync;
            SetTimeScale(1f);
            _mode = Mode.Idle;
            _render = null;
            _job = "";
            _rendering = false;
            while (_queue.Count > 0 && !_cancel && !_rendering)
            {
                (string next, RenderOptions nextOptions) = _queue.Dequeue();
                try { Render(next, nextOptions); }
                catch (Exception failure) { _jobResult = $"render {Path.GetFileName(next)} failed: {failure.Message}"; }
            }
        }

        private static int AudioChannels() => AudioSettings.speakerMode switch
        {
            AudioSpeakerMode.Mono => 1,
            AudioSpeakerMode.Quad => 4,
            AudioSpeakerMode.Surround => 5,
            AudioSpeakerMode.Mode5point1 => 6,
            AudioSpeakerMode.Mode7point1 => 8,
            _ => 2
        };

        private static void WriteWav(string path, List<float> samples, int rate, int channels)
        {
            using BinaryWriter writer = new(File.Create(path));
            int bytes = samples.Count * 2;
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + bytes);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)channels);
            writer.Write(rate); writer.Write(rate * channels * 2); writer.Write((short)(channels * 2)); writer.Write((short)16);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(bytes);
            foreach (float sample in samples) writer.Write((short)Mathf.Clamp(sample * 32767f, -32768f, 32767f));
        }

        /// <summary>Every recorded sound that starts inside the shot, timed in output seconds, for an edit to rebuild.</summary>
        private static void WriteCues(string path, ShotEvaluator evaluator)
        {
            ReplayFile replay = ReplayPlayer.Instance.File;
            List<Dictionary<string, object>> cues = new();
            foreach (SoundTrack track in replay.Sounds)
            {
                if (track.Start < evaluator.Shot.start || track.Start >= evaluator.Shot.end) continue;
                ShotPose pose = evaluator.Evaluate(track.Start);
                Vector3 position = track.Frames.Count > 0 ? track.Frames[0].Position : pose.LookAt;
                Vector3 relative = Quaternion.Inverse(ShotEvaluator.Rotation(pose.Position, pose.LookAt, pose.Roll, Vector3.zero)) * (position - pose.Position);
                cues.Add(new Dictionary<string, object>
                {
                    ["clip"] = track.Clip, ["replayStart"] = Math.Round(track.Start, 3), ["outputStart"] = Math.Round(evaluator.OutputTime(track.Start), 3),
                    ["replayEnd"] = Math.Round(track.End, 3), ["volume"] = track.Frames.Count > 0 ? Math.Round(track.Frames[0].Volume, 3) : 1d,
                    ["pitch"] = track.Frames.Count > 0 ? Math.Round(track.Frames[0].Pitch, 3) : 1d, ["speedAtStart"] = Math.Round(evaluator.SpeedAt(track.Start), 3),
                    ["relativeToCamera"] = ReplayEvents.Round(relative), ["distance"] = Math.Round(relative.magnitude, 2)
                });
            }
            File.WriteAllText(path, JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                ["shot"] = evaluator.Shot.name, ["timeOrigin"] = "outputStart is seconds from the first rendered frame", ["cues"] = cues
            }, Formatting.Indented));
        }

        /// <summary>Encodes the frames with the GPU encoder when ffmpeg has one (several times faster), otherwise with x264.</summary>
        private static string Encode(string root, string name, int fps, string? wav)
        {
            string output = Path.Combine(root, name + ".mp4");
            string audio = wav != null ? $"-i \"{wav}\" -c:a aac -b:a 256k -shortest " : "";
            string input = $"-y -loglevel error -framerate {fps} -i \"{Path.Combine(root, "frames", "%06d.png")}\" {audio}";
            string error = RunFfmpeg(input + $"-c:v h264_nvenc -preset p6 -tune hq -rc vbr -cq 16 -b:v 0 -pix_fmt yuv420p \"{output}\"");
            if (error.Length == 0) return "";
            return RunFfmpeg(input + $"-c:v libx264 -preset medium -crf 14 -pix_fmt yuv420p \"{output}\"");
        }

        private static string RunFfmpeg(string arguments)
        {
            ProcessStartInfo start = new("ffmpeg", arguments) { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
            try
            {
                using Process process = Process.Start(start)!;
                string errors = process.StandardError.ReadToEnd();
                process.WaitForExit();
                return process.ExitCode == 0 ? "" : "ffmpeg: " + errors.Trim();
            }
            catch (Exception failure) { return "ffmpeg not available: " + failure.Message; }
        }

        // ---------------------------------------------------------------- hand-made shots

        internal string NewShot(string name)
        {
            if (!ReplayPlayer.IsPlaying) throw new InvalidOperationException("Open a replay first");
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException($"'{name}' is not a valid file name");
            ReplayPlayer player = ReplayPlayer.Instance;
            _editing = new ShotFile { name = name, replay = Path.GetFileNameWithoutExtension(player.FilePath ?? player.File.Info.Name), start = player.PlaybackTime, end = player.PlaybackTime };
            _editingPath = Path.Combine(Directory, name + ".json");
            return $"editing={name} keys=0";
        }

        internal string AddKey(float? time)
        {
            if (_editing == null) throw new InvalidOperationException("Start a shot with ct_shot new <name> first");
            Transform camera = GameCamera.m_instance.transform;
            float t = time ?? ReplayPlayer.Instance.PlaybackTime;
            Vector3 look = camera.position + camera.forward * 10f;
            _editing.camera.RemoveAll(key => Mathf.Abs(key.t - t) < 0.001f);
            _editing.camera.Add(new CameraKey
            {
                t = (float)Math.Round(t, 3),
                position = new Newtonsoft.Json.Linq.JArray(Math.Round(camera.position.x, 3), Math.Round(camera.position.y, 3), Math.Round(camera.position.z, 3)),
                lookAt = new Newtonsoft.Json.Linq.JArray(Math.Round(look.x, 3), Math.Round(look.y, 3), Math.Round(look.z, 3)),
                fov = (float)Math.Round(CurrentFov(), 2),
                roll = (float)Math.Round(Mathf.DeltaAngle(0f, camera.eulerAngles.z), 2)
            });
            _editing.camera.Sort((a, b) => a.t.CompareTo(b.t));
            _editing.start = _editing.camera[0].t;
            _editing.end = Mathf.Max(_editing.camera[_editing.camera.Count - 1].t, _editing.start + 0.1f);
            return $"editing={_editing.name} keys={_editing.camera.Count} start={ShotEvaluator.F(_editing.start)} end={ShotEvaluator.F(_editing.end)}";
        }

        internal string Save(string? name)
        {
            if (_editing == null) throw new InvalidOperationException("Nothing to save; use ct_shot new <name> and ct_shot key");
            string path = name != null ? Path.Combine(Directory, name.EndsWith(".json") ? name : name + ".json") : _editingPath;
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (name != null) _editing.name = Path.GetFileNameWithoutExtension(path);
            File.WriteAllText(path, JsonConvert.SerializeObject(_editing, Formatting.Indented));
            return $"saved={path} keys={_editing.camera.Count}";
        }

    }
}
