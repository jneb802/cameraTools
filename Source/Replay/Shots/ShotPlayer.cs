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
        private Vector3 _lastLook;
        private float _lastRoll;
        private Quaternion _lastRotation = Quaternion.identity;
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
        internal static Dictionary<(long, uint), float> Heights(ReplayPlayer player)
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
            _lastLook = look;
            _lastRoll = pose.Roll;
            _lastRotation = rotation;
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

        /// <summary>
        /// Steps the shot every 0.05 s of replay time as a render would show it (look smoothing and shake included) and
        /// reports the old checks and the framing measurements; see <see cref="ShotCheck"/>. Writes the report with the
        /// per-step series to &lt;shot&gt;.check.json.
        /// </summary>
        internal string Check(string path, float aspect)
        {
            ShotEvaluator evaluator = Evaluator(path);
            Stopwatch clock = Stopwatch.StartNew();
            ShotAnalysis analysis = ShotCheck.Analyse(evaluator, aspect, new CheckSettings(), ShotCheck.Canopies(evaluator));
            string report = Path.ChangeExtension(path, ".check.json");
            WriteCheck(report, path, evaluator, aspect, analysis, null);
            _jobResult = $"check {evaluator.Shot.name}: {analysis.Issues.Count} issues score={analysis.Score:F2}";
            return $"OK: issues={analysis.Issues.Count} score={analysis.Score:F2} seconds={clock.Elapsed.TotalSeconds:F2} report={report}" + IssueLines(analysis.Issues);
        }

        private static string IssueLines(IEnumerable<Dictionary<string, object?>> issues)
        {
            string lines = string.Join("\n", issues.Select(i => $"issue type={i["type"]} subject={i["subject"]} from={i["from"]} to={i["to"]}" +
                (i.TryGetValue("value", out object? value) ? $" value={value}" : "") + (i.TryGetValue("method", out object? method) ? $" method={method}" : "") + $" detail=\"{i["detail"]}\""));
            return lines.Length > 0 ? "\n" + lines : "";
        }

        private static void WriteCheck(string report, string path, ShotEvaluator evaluator, float aspect, ShotAnalysis analysis, Dictionary<string, object?>? pixels)
        {
            Dictionary<string, object?> json = new()
            {
                ["shot"] = evaluator.Shot.name, ["file"] = path, ["steps"] = analysis.StepCount, ["stepSeconds"] = 0.05, ["aspect"] = Math.Round(aspect, 4),
                ["outputSeconds"] = Math.Round(evaluator.OutputDuration(), 3), ["score"] = Math.Round(analysis.Score, 3), ["issues"] = analysis.Issues,
                ["bodies"] = new Framing(evaluator, aspect).Bodies(),
                ["series"] = analysis.Steps.Select(step => Framing.Json(step, true)).ToList()
            };
            if (pixels != null) json["pixels"] = pixels;
            File.WriteAllText(report, JsonConvert.SerializeObject(json, Formatting.Indented));
        }

        /// <summary>
        /// The check with the exact pixel pass: at each sampled time the replay seeks, waits for the actors, and a small
        /// object buffer counts each measured actor's visible pixels. Reports the same issue types with method "pixels".
        /// </summary>
        internal string CheckPixels(string path, float aspect, float rate, bool debug)
        {
            ShotEvaluator evaluator = Evaluator(path);
            if (_mode == Mode.Render || _rendering) throw new InvalidOperationException("A render is running; use ct_shot render cancel");
            _shotPath = path;
            StartCoroutine(PixelRoutine(evaluator, path, aspect, rate, debug));
            return $"ACCEPTED: pixel check of {evaluator.Shot.name} at {rate:F0} samples a second; poll ct_shot status until mode=idle";
        }

        private IEnumerator PixelRoutine(ShotEvaluator evaluator, string path, float aspect, float rate, bool debug)
        {
            Stopwatch clock = Stopwatch.StartNew();
            ShotAnalysis analysis = ShotCheck.Analyse(evaluator, aspect, new CheckSettings(), ShotCheck.Canopies(evaluator));
            List<List<PixelSample>> all = new();
            yield return PixelSamples(new List<ShotEvaluator> { evaluator }, aspect, rate, debug ? Path.Combine(Path.GetDirectoryName(path)!, "pixels", evaluator.Shot.name) : null, all);
            List<PixelSample> samples = all[0];
            Framing framing = new(evaluator, aspect);
            IssueRanges ranges = new("pixels");
            List<Dictionary<string, object?>> series = new(), differences = new();
            foreach (PixelSample sample in samples)
            {
                FrameMeasure? rays = analysis.Steps.OrderBy(step => Mathf.Abs(step.Time - sample.Time)).FirstOrDefault();
                foreach (PixelActor actor in sample.Actors)
                {
                    if (!actor.Present) continue;
                    ActorTrack track = ReplayPlayer.Instance.Tracks.Get(actor.Id);
                    string role = framing.Measured.First(m => m.Track == track).Role;
                    string name = framing.Name(track);
                    bool counts = framing.InAction(sample.Time) && (role != "partner" || framing.Moment is not float moment || sample.Time >= moment - 0.5f);
                    if (actor.VisibleFraction < framing.MinVisible && counts) ranges.Mark("subject_hidden", actor.Id, sample.Time, $"{name} ({role}) {actor.VisibleFraction:P0} of its pixels visible", actor.VisibleFraction);
                    if (role == "partner" && actor.InFrame < Framing.InFrameNeeded && counts) ranges.Mark("pair_split", actor.Id, sample.Time, $"{name} {actor.InFrame:P0} of its pixels in frame", actor.InFrame);
                    if (role == "subject" && actor.InFrame < Framing.InFrameNeeded && counts) ranges.Mark("subject_cut", actor.Id, sample.Time, $"{name} {actor.InFrame:P0} of its pixels in frame", actor.InFrame);
                    if (role == "subject" && counts && SmoothedStops(samples, sample, actor.Id) is float stops && Mathf.Abs(stops) <= Framing.LowSeparationStops)
                        ranges.Mark("low_separation", actor.Id, sample.Time, $"{name} is {stops:+0.00;-0.00} stops against what is behind it, needs more than ±{Framing.LowSeparationStops:F2}", Mathf.Abs(stops));
                    float? ray = rays?.Actors.FirstOrDefault(a => a.Id == actor.Id)?.Visible;
                    if (ray.HasValue && Mathf.Abs(ray.Value - actor.VisibleFraction) > 0.2f)
                        differences.Add(new Dictionary<string, object?> { ["t"] = Math.Round(sample.Time, 2), ["actor"] = actor.Id, ["rays"] = Framing.R(ray.Value), ["pixels"] = Framing.R(actor.VisibleFraction) });
                }
                if ((sample.Foreground > framing.MaxForeground || sample.Actors.Any(a => a.HeadCovered)) && framing.InAction(sample.Time))
                    ranges.Mark("foreground_cover", "", sample.Time, sample.Actors.Any(a => a.HeadCovered) ? $"foreground pixels in a head box ({sample.Foreground:P0} of the frame)" : $"foreground covers {sample.Foreground:P0} of the frame", sample.Foreground, false);
                if (sample.Clutter is float clutter && clutter > Framing.MaxClutter && framing.InAction(sample.Time))
                    ranges.Mark("clutter", "", sample.Time, $"things other than the subject cover {clutter:P0} of the frame up to {Framing.ClutterDepth:F0} m past the subject, limit {Framing.MaxClutter:P0}", clutter, false);
                ranges.Step();
                series.Add(new Dictionary<string, object?>
                {
                    ["t"] = Math.Round(sample.Time, 3), ["foreground"] = Framing.R(sample.Foreground), ["clutter"] = sample.Clutter.HasValue ? Framing.R(sample.Clutter.Value) : null,
                    ["actors"] = sample.Actors.ToDictionary(a => a.Id, a => (object?)new Dictionary<string, object?>
                    {
                        ["present"] = a.Present, ["visiblePixels"] = a.Visible, ["alonePixels"] = a.Alone, ["visible"] = Framing.R(a.VisibleFraction), ["inFrame"] = Framing.R(a.InFrame), ["headCovered"] = a.HeadCovered,
                        ["stops"] = a.Stops.HasValue ? Math.Round(a.Stops.Value, 2) : null
                    })
                });
            }
            ranges.Finish();
            analysis.Issues.AddRange(ranges.Issues);
            Dictionary<string, object?> pixels = new()
            {
                ["rate"] = rate, ["buffer"] = "256x144", ["seconds"] = Math.Round(clock.Elapsed.TotalSeconds, 2), ["samples"] = samples.Count,
                ["issues"] = ranges.Issues, ["raysDiffer"] = differences, ["series"] = series
            };
            string report = Path.ChangeExtension(path, ".check.json");
            WriteCheck(report, path, evaluator, aspect, analysis, pixels);
            _jobResult = $"pixel check {evaluator.Shot.name}: {ranges.Issues.Count} pixel issues, {analysis.Issues.Count - ranges.Issues.Count} ray issues, score={analysis.Score:F2}, " +
                $"{differences.Count} samples where rays and pixels differ by more than 0.2, {clock.Elapsed.TotalSeconds:F1}s report={report}";
        }

        /// <summary>
        /// An actor's separation in stops averaged over the samples within 0.15 s of one, so one frame of a bright spark or a
        /// dark gap does not open or close a low-separation range.
        /// </summary>
        internal static float? SmoothedStops(List<PixelSample> samples, PixelSample at, string actor)
        {
            List<float> near = samples.Where(s => Mathf.Abs(s.Time - at.Time) <= 0.15f + 0.001f)
                .Select(s => s.Actors.FirstOrDefault(a => a.Id == actor && a.Present)?.Stops).Where(v => v.HasValue).Select(v => v!.Value).ToList();
            return near.Count > 0 ? near.Average() : null;
        }

        /// <summary>
        /// Seeks to each sampled time, waits for the actors and counts pixels through the camera a render would use.
        /// Several shots over the same replay time (the solver's candidates) share each seek: every shot's camera is
        /// measured at every sampled time inside it. The times are those of the first shot, widened to cover them all.
        /// </summary>
        internal IEnumerator PixelSamples(List<ShotEvaluator> evaluators, float aspect, float rate, string? debugFolder, List<List<PixelSample>> samples)
        {
            List<Framing> framings = evaluators.Select(e => new Framing(e, aspect)).ToList();
            List<List<CameraFrame>> frames = evaluators.Select(e => ShotCheck.Simulate(e, 60)).ToList();
            foreach (ShotEvaluator _ in evaluators) samples.Add(new List<PixelSample>());
            float start = evaluators.Min(e => e.Shot.start), end = evaluators.Max(e => e.Shot.end);
            List<float> times = new();
            for (float time = start; time <= end + 0.0001f; time += 1f / rate) times.Add(Mathf.Min(time, end));
            using PixelPass pass = new(Mathf.RoundToInt(144 * aspect), 144);
            using (new CleanFrame(false))
            {
                Begin(evaluators[0], Mode.Hold, times[0]);
                _cancel = false;
                for (int index = 0; index < times.Count && !_cancel; index++)
                {
                    float time = times[index];
                    _job = $"pixels {evaluators[0].Shot.name}{(evaluators.Count > 1 ? $" and {evaluators.Count - 1} more" : "")} {index + 1}/{times.Count}";
                    ReplayPlayer.Instance.Seek(time);
                    _target = time;
                    _hasSmoothLook = false;
                    yield return WaitForActors(time);
                    yield return null;
                    for (int shot = 0; shot < evaluators.Count; shot++)
                    {
                        ShotFile file = evaluators[shot].Shot;
                        if (time < file.start - 0.0001f || time > file.end + 0.0001f) continue;
                        CameraFrame camera = ShotCheck.At(frames[shot], time);
                        FrameMeasure measure = framings[shot].Measure(camera.Time, camera.Position, camera.Rotation, camera.Fov, rays: false);
                        List<(string, GameObject?, Rect, Rect, bool)> actors = new();
                        float? subjectDepth = null;
                        foreach ((ActorTrack track, string role) in framings[shot].Measured)
                        {
                            if (!track.Sample(time, out Vector3 feet, out _)) continue;
                            ActorFrame? measured = measure.Actors.FirstOrDefault(a => a.Id == track.Key);
                            actors.Add((track.Key, ReplayPlayer.Instance.FindTarget(track.User, track.Id), measured?.HeadBox ?? Rect.zero, measured?.Box ?? Rect.zero, measured?.Large != true));
                            // The subject is first when present; clutter is measured against the depth of its body's middle.
                            if (role == "subject") subjectDepth = Vector3.Dot(feet + Vector3.up * evaluators[shot].Height(track) * 0.5f - camera.Position, camera.Rotation * Vector3.forward);
                        }
                        string? debug = debugFolder != null ? Path.Combine(debugFolder, $"{file.name}-{time.ToString("F2", CultureInfo.InvariantCulture)}.png") : null;
                        samples[shot].Add(pass.Measure(time, camera.Position, camera.Rotation, camera.Fov, actors, Framing.ForegroundLimit(measure), subjectDepth, true, debug));
                    }
                }
            }
            Stop();
            _job = "";
        }

        // ---------------------------------------------------------------- preview

        internal sealed class PreviewOptions
        {
            public int Count = 4;
            public bool Overlays;
            /// <summary>Strip mode: N evenly spaced stills plus each event, at a fixed size, each also with the framing overlay.</summary>
            public int Strip;
            public int Width = 640, Height = 360;
        }

        internal string Preview(string path, List<float> times, PreviewOptions options)
        {
            ShotEvaluator evaluator = Evaluator(path);
            if (times.Count == 0)
            {
                int n = Mathf.Max(1, options.Strip > 0 ? options.Strip : options.Count);
                for (int index = 0; index < n; index++) times.Add(evaluator.Shot.start + (evaluator.Shot.end - evaluator.Shot.start) * (n == 1 ? 0.5f : index / (float)(n - 1)));
                foreach (ReplayEvent tag in ReplayPlayer.Instance.Events.Events)
                    if (tag.Time > evaluator.Shot.start && tag.Time < evaluator.Shot.end && (tag.Type == ReplayEventType.Death || tag.Type == ReplayEventType.Hit)) times.Add(tag.Time);
                if (evaluator.Shot.framing?.moment is float moment) times.Add(moment);
            }
            foreach (float time in times)
                if (time < evaluator.Shot.start - 0.001f || time > evaluator.Shot.end + 0.001f) throw new ArgumentException($"Preview time {time} is outside the shot ({evaluator.Shot.start}-{evaluator.Shot.end})");
            _shotPath = path;
            List<float> ordered = times.Select(t => (float)Math.Round(t, 2)).Distinct().OrderBy(t => t).ToList();
            StartCoroutine(PreviewRoutine(evaluator, ordered, options));
            return $"ACCEPTED: saving {ordered.Count} stills{(options.Strip > 0 ? $" at {options.Width}x{options.Height} with framing overlays" : "")}; poll ct_shot status until mode=idle";
        }

        private IEnumerator PreviewRoutine(ShotEvaluator evaluator, List<float> times, PreviewOptions options)
        {
            string directory = Path.Combine(Path.GetDirectoryName(_shotPath)!, "previews");
            System.IO.Directory.CreateDirectory(directory);
            List<string> written = new();
            List<Dictionary<string, object?>> strip = new();
            bool fixedSize = options.Strip > 0;
            float aspect = fixedSize ? options.Width / (float)options.Height : Screen.width / (float)Mathf.Max(1, Screen.height);
            Framing? framing = fixedSize ? new Framing(evaluator, aspect) : null;
            PixelPass? pixels = fixedSize ? new PixelPass(Mathf.RoundToInt(144 * aspect), 144) : null;
            using (new CleanFrame(options.Overlays))
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
                    string stem = Path.Combine(directory, $"{evaluator.Shot.name}-{time.ToString("F2", CultureInfo.InvariantCulture)}");
                    if (!fixedSize)
                    {
                        Texture2D image = ScreenCapture.CaptureScreenshotAsTexture();
                        File.WriteAllBytes(stem + ".png", image.EncodeToPNG());
                        Destroy(image);
                        written.Add(stem + ".png");
                        continue;
                    }
                    Texture2D clean = FramingStill.Capture(options.Width, options.Height, out RenderTexture target);
                    try
                    {
                        Transform camera = GameCamera.m_instance.transform;
                        FrameMeasure measure = framing!.Measure(time, camera.position, camera.rotation, CurrentFov());
                        Dictionary<string, float> brightness = FramingStill.Brightness(clean, measure, pixels!, ReplayPlayer.Instance);
                        FramingStill.Write(stem + ".png", clean);
                        string header = $"{evaluator.Shot.name}  t={time:F2}  score {measure.Score:F2}  fg {measure.Foreground:F2}{(measure.HeadCovered ? " HEAD COVERED" : "")}";
                        Texture2D overlay = FramingStill.Overlay(target, measure, header, brightness);
                        FramingStill.Write(stem + "-overlay.png", overlay);
                        Destroy(overlay);
                        written.Add(stem + ".png");
                        Dictionary<string, object?> entry = Framing.Json(measure, true);
                        entry["clean"] = Path.GetFileName(stem + ".png");
                        entry["overlay"] = Path.GetFileName(stem + "-overlay.png");
                        entry["camera"] = ReplayEvents.Round(camera.position);
                        entry["rotation"] = Quat(camera.rotation);
                        entry["fov"] = Math.Round(CurrentFov(), 2);
                        entry["brightnessStops"] = brightness.ToDictionary(b => b.Key, b => (object)Math.Round(b.Value, 2));
                        strip.Add(entry);
                    }
                    finally
                    {
                        Destroy(clean);
                        RenderTexture.ReleaseTemporary(target);
                    }
                }
            }
            pixels?.Dispose();
            if (fixedSize)
                File.WriteAllText(Path.Combine(directory, evaluator.Shot.name + ".strip.json"), JsonConvert.SerializeObject(new Dictionary<string, object?>
                {
                    ["shot"] = evaluator.Shot.name, ["width"] = options.Width, ["height"] = options.Height, ["stills"] = strip
                }, Formatting.Indented));
            Stop();
            _job = "";
            _jobResult = $"preview {evaluator.Shot.name}: {written.Count} stills in {directory}";
        }

        internal static double[] Quat(Quaternion rotation) => new[] { Math.Round(rotation.x, 5), Math.Round(rotation.y, 5), Math.Round(rotation.z, 5), Math.Round(rotation.w, 5) };

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
                    Framing logFraming = new(evaluator, Screen.width / (float)Mathf.Max(1, Screen.height));
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
                        Quaternion rotation = GameCamera.m_instance != null ? GameCamera.m_instance.transform.rotation : _lastRotation;
                        frameLog.Add(new Dictionary<string, object>
                        {
                            ["frame"] = index, ["replayTime"] = Math.Round(replayTime, 4), ["outputTime"] = Math.Round(outputTime, 4),
                            ["speed"] = Math.Round(evaluator.SpeedAt(replayTime), 3), ["camera"] = ReplayEvents.Round(position), ["fov"] = Math.Round(_lastFov, 2),
                            ["rotation"] = Quat(rotation), ["lookAt"] = ReplayEvents.Round(_lastLook), ["roll"] = Math.Round(_lastRoll, 2),
                            ["framing"] = Framing.Json(logFraming.Measure(replayTime, position, rotation, _lastFov, rays: false), false)
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
                        ["shot"] = shot, ["shotFile"] = _shotPath, ["aspect"] = Math.Round(Screen.width / (float)Mathf.Max(1, Screen.height), 4), ["fps"] = options.Fps, ["supersample"] = options.Supersample, ["width"] = Screen.width * options.Supersample, ["height"] = Screen.height * options.Supersample,
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

        // ---------------------------------------------------------------- solve

        /// <summary>
        /// Searches for camera positions for each intent in the file (see <see cref="ShotSolver"/>) and writes the best shot
        /// in each angle bin with its framing block, score and bin. Runs over many frames: poll ct_shot status until mode=idle.
        /// </summary>
        internal string Solve(string path, bool usePixels, int keep)
        {
            if (!ReplayPlayer.IsPlaying) throw new InvalidOperationException("Open the shot's replay first");
            if (_mode == Mode.Render || _rendering) throw new InvalidOperationException("A render is running; use ct_shot render cancel");
            if (_solving) throw new InvalidOperationException("A solve is running; poll ct_shot status");
            SolveFile file = ShotSolver.Read(path);
            ReplayPlayer player = ReplayPlayer.Instance;
            string playing = Path.GetFileNameWithoutExtension(player.FilePath ?? player.File.Info.Name);
            if (file.replay.Length > 0 && !string.Equals(file.replay, playing, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"The intents are for replay '{file.replay}' but '{playing}' is playing");
            float aspect = 16f / 9f;
            // Built first so a bad intent fails now, not halfway through.
            List<ShotSolver> solvers = file.intents.Select(intent => new ShotSolver(this, intent, playing, aspect)).ToList();
            string folder = file.@out.Length > 0 ? (Path.IsPathRooted(file.@out) ? file.@out : Path.Combine(Directory, file.@out)) : Path.GetDirectoryName(path)!;
            _shotPath = path;
            _solving = true;
            StartCoroutine(SolveRoutine(solvers, folder, usePixels, keep));
            return $"ACCEPTED: solving {solvers.Count} intents into {folder}; poll ct_shot status until mode=idle and no job";
        }

        private bool _solving;

        private IEnumerator SolveRoutine(List<ShotSolver> solvers, string folder, bool usePixels, int keep)
        {
            Stopwatch clock = Stopwatch.StartNew();
            List<string> written = new(), failed = new();
            _cancel = false;
            foreach (ShotSolver solver in solvers)
            {
                if (_cancel) break;
                IEnumerator run = solver.Run(folder, usePixels, keep, text => _job = text);
                while (true)
                {
                    object? current;
                    try
                    {
                        if (!run.MoveNext()) break;
                        current = run.Current;
                    }
                    catch (Exception error)
                    {
                        failed.Add(error.Message);
                        cameraToolsPlugin.TemplateLogger.LogError($"[Shots] Solve failed: {error}");
                        break;
                    }
                    yield return current;
                }
                written.AddRange(solver.Written.Select(Path.GetFileNameWithoutExtension));
            }
            _job = "";
            _solving = false;
            _jobResult = $"solve: wrote {written.Count} shots in {clock.Elapsed.TotalSeconds:F0}s folder={folder} shots={string.Join(",", written)}" + (failed.Count > 0 ? " errors=" + string.Join("; ", failed) : "");
        }

        // ---------------------------------------------------------------- measure a render again

        /// <summary>
        /// Measures a finished render again from the camera its frame log recorded, with the check's code, and compares
        /// the subject's size and position with the shot's check report when there is one.
        /// </summary>
        internal string Measure(string target, float? aspectOverride)
        {
            if (!ReplayPlayer.IsPlaying) throw new InvalidOperationException("Open the shot's replay first");
            string path = target.EndsWith(".render.json") ? target : System.IO.Directory.Exists(target)
                ? System.IO.Directory.GetFiles(target, "*.render.json").FirstOrDefault() ?? throw new ArgumentException($"No .render.json in {target}")
                : Path.Combine(Paths.ConfigPath, "cameraTools", "renders", target, target + ".render.json");
            if (!File.Exists(path)) throw new ArgumentException($"Render log not found: {path}");
            Newtonsoft.Json.Linq.JObject log = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path));
            ShotFile shot = log["shot"]!.ToObject<ShotFile>() ?? throw new ArgumentException("The render log has no shot");
            ReplayPlayer player = ReplayPlayer.Instance;
            ShotEvaluator evaluator = new(shot, player.Tracks, Heights(player), player.Events);
            float aspect = aspectOverride ?? (log["aspect"] != null ? (float)log["aspect"]! : (float)log["width"]! / (float)log["height"]!);
            Framing framing = new(evaluator, aspect);
            List<FrameMeasure> measures = new();
            int lastBin = -1;
            foreach (Newtonsoft.Json.Linq.JToken frame in (Newtonsoft.Json.Linq.JArray)log["frameLog"]!)
            {
                if (frame["rotation"] is not Newtonsoft.Json.Linq.JArray rotation) throw new InvalidOperationException("This render's frame log has no rotation; render it again with this version");
                float time = (float)frame["replayTime"]!;
                int bin = Mathf.FloorToInt((time - shot.start) / 0.05f + 0.0001f);
                if (bin == lastBin) continue;
                lastBin = bin;
                Vector3 camera = ShotEvaluator.Vector(frame["camera"], Vector3.zero);
                Quaternion turn = new((float)rotation[0], (float)rotation[1], (float)rotation[2], (float)rotation[3]);
                measures.Add(framing.Measure(time, camera, turn, (float)frame["fov"]!));
            }
            string output = Path.Combine(Path.GetDirectoryName(path)!, shot.name + ".measure.json");
            float score = Framing.ShotScore(measures.Where(m => m.Subject != null).Select(m => m.Score).ToList());
            string compare = "";
            string? check = System.IO.Directory.Exists(Directory) ? System.IO.Directory.GetFiles(Directory, shot.name + ".check.json", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() : null;
            Dictionary<string, object?> comparison = new();
            if (check != null && framing.SubjectId != null)
            {
                Newtonsoft.Json.Linq.JObject report = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(check));
                float maxSize = 0f, maxCenter = 0f, maxTime = 0f; int compared = 0;
                foreach (Newtonsoft.Json.Linq.JToken step in (Newtonsoft.Json.Linq.JArray?)report["series"] ?? new Newtonsoft.Json.Linq.JArray())
                {
                    float time = (float)step["t"]!;
                    FrameMeasure? nearest = measures.OrderBy(m => Mathf.Abs(m.Time - time)).FirstOrDefault();
                    Newtonsoft.Json.Linq.JToken? actor = step["actors"]?[framing.SubjectId];
                    ActorFrame? mine = nearest?.Subject;
                    if (nearest == null || actor == null || mine == null || Mathf.Abs(nearest.Time - time) > 0.02f) continue;
                    compared++;
                    maxTime = Mathf.Max(maxTime, Mathf.Abs(nearest.Time - time));
                    maxSize = Mathf.Max(maxSize, Mathf.Abs((float)actor["size"]! - mine.Size));
                    maxCenter = Mathf.Max(maxCenter, (new Vector2((float)actor["center"]![0]!, (float)actor["center"]![1]!) - mine.Center).magnitude);
                }
                comparison = new Dictionary<string, object?> { ["check"] = check, ["steps"] = compared, ["maxSizeDifference"] = Framing.R(maxSize), ["maxCenterDifference"] = Framing.R(maxCenter), ["maxTimeDifference"] = Framing.R(maxTime) };
                compare = $" vsCheck steps={compared} maxSize={maxSize:F3} maxCenter={maxCenter:F3}";
            }
            File.WriteAllText(output, JsonConvert.SerializeObject(new Dictionary<string, object?>
            {
                ["shot"] = shot.name, ["render"] = path, ["aspect"] = Math.Round(aspect, 4), ["score"] = Framing.R(score), ["comparison"] = comparison,
                ["series"] = measures.Select(m => Framing.Json(m, true)).ToList()
            }, Formatting.Indented));
            return $"OK: measured {measures.Count} steps score={score:F2}{compare} report={output}";
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
