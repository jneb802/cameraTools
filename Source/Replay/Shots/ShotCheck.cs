using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace cameraTools.Replay.Shots
{
    /// <summary>One output frame of a shot as a render shows it: the aim includes look smoothing and shake.</summary>
    internal struct CameraFrame
    {
        public float Time;
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Look;
        public Vector3 Target;
        public float Fov;
        public float Roll;
    }

    /// <summary>Time ranges of one problem, opened and closed as the check steps through a shot.</summary>
    internal sealed class IssueRanges
    {
        private sealed class Range
        {
            public float From, To;
            public string Detail = "";
            public float? Value;
            public bool Low;
        }

        private readonly Dictionary<string, Range> _open = new();
        private readonly HashSet<string> _now = new();
        internal readonly List<Dictionary<string, object?>> Issues = new();
        private readonly string _method;

        internal IssueRanges(string method) => _method = method;

        /// <summary>Marks a problem at a time. With a value, the report keeps the worst one: the lowest when low is true.</summary>
        internal void Mark(string type, string subject, float time, string detail, float? value = null, bool low = true)
        {
            string key = type + "|" + subject;
            _now.Add(key);
            if (_open.TryGetValue(key, out Range range))
            {
                range.To = time;
                if (value.HasValue && (!range.Value.HasValue || (low ? value < range.Value : value > range.Value))) { range.Value = value; range.Detail = detail; }
            }
            else _open[key] = new Range { From = time, To = time, Detail = detail, Value = value, Low = low };
        }

        /// <summary>Closes every range not marked since the last step.</summary>
        internal void Step()
        {
            foreach (string key in _open.Keys.ToList()) if (!_now.Contains(key)) Close(key);
            _now.Clear();
        }

        internal void Finish()
        {
            foreach (string key in _open.Keys.ToList()) Close(key);
        }

        private void Close(string key)
        {
            Range range = _open[key];
            _open.Remove(key);
            string[] parts = key.Split('|');
            // A lack of lead room matters only when it lasts more than 0.3 s.
            if (parts[0] == "no_lead_room" && range.To - range.From < 0.3f) return;
            Dictionary<string, object?> issue = new()
            {
                ["type"] = parts[0], ["subject"] = parts.Length > 1 ? parts[1] : "", ["from"] = Math.Round(range.From, 2), ["to"] = Math.Round(range.To, 2),
                ["detail"] = range.Detail
            };
            if (range.Value.HasValue) issue["value"] = Math.Round(range.Value.Value, 3);
            if (_method.Length > 0) issue["method"] = _method;
            Issues.Add(issue);
        }
    }

    /// <summary>The result of stepping through a shot: issues, per-step framing and the shot score.</summary>
    internal sealed class ShotAnalysis
    {
        public readonly List<Dictionary<string, object?>> Issues = new();
        public readonly List<FrameMeasure> Steps = new();
        public float Score;
        public int StepCount;
        /// <summary>Set by the solver's hard rejects: the first reason the shot cannot be used.</summary>
        public string? Rejected;
    }

    /// <summary>Settings for one pass through a shot: the full check, or the faster search the solver runs.</summary>
    internal sealed class CheckSettings
    {
        public float Step = 0.05f;
        public int Fps = 60;
        public int GridX = 16, GridY = 9;
        /// <summary>Stop at the first hard reject (the solver's rules) instead of reporting issues.</summary>
        public bool HardRejects;
        public int Side;
        public float? SideTime;
        public Vector3 SideA, SideB;
        public bool Legacy = true;
    }

    internal static class ShotCheck
    {
        /// <summary>
        /// The camera of every output frame, as a render sets it: the replay advances by 1/fps times the speed, and the aim
        /// eases toward its target with the shot's look smoothing.
        /// </summary>
        internal static List<CameraFrame> Simulate(ShotEvaluator evaluator, int fps)
        {
            List<CameraFrame> frames = new();
            ShotFile shot = evaluator.Shot;
            float step = 1f / fps;
            Vector3? smooth = null;
            // A render warms up from 2 s before the start at normal speed, and its aim eases toward the target all that
            // time; doing the same here makes the check measure the frames the render will show.
            if (shot.lookSmoothing > 0f)
            {
                float warm = Mathf.Max(0f, shot.start - 2f);
                smooth = evaluator.Evaluate(warm).LookAt;
                for (float t = warm; t < shot.start - 0.0001f; t = Mathf.Min(shot.start, t + step))
                {
                    Vector3 target = evaluator.Evaluate(Mathf.Min(shot.start, t + step)).LookAt;
                    smooth = Vector3.Lerp(smooth.Value, target, 1f - Mathf.Exp(-step / shot.lookSmoothing));
                }
            }
            float time = shot.start;
            while (true)
            {
                ShotPose pose = evaluator.Evaluate(Mathf.Min(time, shot.end));
                Vector3 look = pose.LookAt;
                if (smooth.HasValue && shot.lookSmoothing > 0f) look = Vector3.Lerp(smooth.Value, look, 1f - Mathf.Exp(-step / shot.lookSmoothing));
                smooth = look;
                frames.Add(new CameraFrame
                {
                    Time = Mathf.Min(time, shot.end), Position = pose.Position, Look = look, Target = pose.LookAt, Fov = pose.Fov, Roll = pose.Roll,
                    Rotation = ShotEvaluator.Rotation(pose.Position, look, pose.Roll, evaluator.Shake(time))
                });
                if (time >= shot.end - 0.5f * step * evaluator.SpeedAt(time)) break;
                time += step * evaluator.SpeedAt(time);
            }
            return frames;
        }

        /// <summary>The simulated frame nearest a replay time.</summary>
        internal static CameraFrame At(List<CameraFrame> frames, float time)
        {
            int lo = 0, hi = frames.Count - 1;
            while (lo < hi - 1)
            {
                int mid = (lo + hi) / 2;
                if (frames[mid].Time <= time) lo = mid; else hi = mid;
            }
            return Mathf.Abs(frames[lo].Time - time) <= Mathf.Abs(frames[hi].Time - time) ? frames[lo] : frames[hi];
        }

        /// <summary>The frames to measure: the first output frame in each step of replay time, and the last frame.</summary>
        internal static List<CameraFrame> StepFrames(List<CameraFrame> frames, float start, float step)
        {
            List<CameraFrame> chosen = new();
            int last = -1;
            foreach (CameraFrame frame in frames)
            {
                int bin = Mathf.FloorToInt((frame.Time - start) / step + 0.0001f);
                if (bin == last) continue;
                last = bin;
                chosen.Add(frame);
            }
            if (chosen.Count > 0 && chosen[chosen.Count - 1].Time < frames[frames.Count - 1].Time - 0.001f) chosen.Add(frames[frames.Count - 1]);
            return chosen;
        }

        internal static ShotAnalysis Analyse(ShotEvaluator evaluator, float aspect, CheckSettings settings, CanopyIndex canopies)
        {
            ShotAnalysis analysis = new();
            ReplayPlayer player = ReplayPlayer.Instance;
            Framing framing = new(evaluator, aspect, canopies) { GridX = settings.GridX, GridY = settings.GridY };
            IssueRanges ranges = new("");
            ShotFile shot = evaluator.Shot;
            List<CameraFrame> steps = StepFrames(Simulate(evaluator, settings.Fps), shot.start, settings.Step);
            List<ActorTrack> bodies = player.Tracks.All.Where(t => t.Kind == 0 && !t.IsProjectile).ToList();
            HashSet<(long, uint)> deaths = new(player.File.Deaths.Select(death => (death.User, death.Id)));
            List<float> scores = new();
            int hidden = 0, measuredSteps = 0;
            float momentGap = float.MaxValue;
            FrameMeasure? momentFrame = null;
            foreach (CameraFrame step in steps)
            {
                float time = step.Time;
                analysis.StepCount++;
                if (settings.HardRejects)
                {
                    string? reject = HardReject(step, framing, bodies, settings);
                    if (reject != null) { analysis.Rejected = $"{reject} at {time:F2}s"; return analysis; }
                }
                else if (settings.Legacy) Legacy(step, evaluator, framing, bodies, deaths, ranges, canopies);
                if (framing.SubjectTrack == null) { ranges.Step(); continue; }
                FrameMeasure frame = framing.Measure(time, step.Position, step.Rotation, step.Fov);
                analysis.Steps.Add(frame);
                if (frame.Subject != null) { scores.Add(frame.Score); measuredSteps++; }
                // The strike frame is the last step at or before the moment: a victim's track ends when it dies at the moment.
                if (framing.Moment is float moment && time <= moment + 0.001f && moment - time < momentGap) { momentGap = moment - time; momentFrame = frame; }
                if (settings.HardRejects)
                {
                    bool lowVisible = false;
                    foreach (ActorFrame actor in frame.Actors)
                    {
                        if (actor.Role == "keep") continue;
                        if (actor.InFrame < 0.8f && framing.PartnerCounts(actor, time)) { analysis.Rejected = $"{actor.Role} {actor.Name} in-frame {actor.InFrame:F2} (size {actor.Size:F2}, centre {actor.Center.x:F2},{actor.Center.y:F2}) at {time:F2}s"; return analysis; }
                        if (actor.Visible < 0.5f && framing.PartnerCounts(actor, time)) lowVisible = true;
                    }
                    if (lowVisible) hidden++;
                    continue;
                }
                Framed(frame, framing, ranges);
                ranges.Step();
            }
            if (settings.HardRejects && measuredSteps > 0 && hidden > 0.15f * measuredSteps)
            {
                analysis.Rejected = $"visible under 0.5 for {100f * hidden / measuredSteps:F0}% of steps";
                return analysis;
            }
            if (!settings.HardRejects && momentFrame != null) Strike(momentFrame, framing, ranges);
            ranges.Finish();
            analysis.Issues.AddRange(ranges.Issues.OrderBy(i => (double)i["from"]!));
            analysis.Score = Framing.ShotScore(scores);
            return analysis;
        }

        /// <summary>The solver's rules that make a camera unusable whatever its score.</summary>
        private static string? HardReject(CameraFrame step, Framing framing, List<ActorTrack> bodies, CheckSettings settings)
        {
            Vector3 camera = step.Position;
            if (Physics.CheckSphere(camera, 0.4f, Framing.SolidMask, QueryTriggerInteraction.Ignore)) return "camera within 0.4 m of a solid object";
            if (Physics.Raycast(camera + Vector3.up * 500f, Vector3.down, out RaycastHit ground, 1000f, Framing.SolidMask, QueryTriggerInteraction.Ignore))
            {
                // The highest surface under the camera, including rocks and floors.
                if (camera.y - ground.point.y < 0.5f) return $"camera {camera.y - ground.point.y:F2} m above the surface";
            }
            foreach (ActorTrack body in bodies)
            {
                if (!body.Sample(step.Time, out Vector3 feet, out _)) continue;
                float height = framing.Evaluator.Height(body);
                Vector3 closest = new(feet.x, Mathf.Clamp(camera.y, feet.y, feet.y + height), feet.z);
                if (Vector3.Distance(camera, closest) < framing.Radius(body) + 0.4f) return $"camera within 0.4 m of {framing.Name(body)}";
            }
            if (settings.Side != 0 && settings.SideTime is float sideTime && Mathf.Abs(step.Time - sideTime) <= settings.Step * 0.5f + 0.001f)
            {
                Vector3 line = settings.SideB - settings.SideA, toCamera = camera - settings.SideA;
                float cross = line.z * toCamera.x - line.x * toCamera.z;
                if (Mathf.Sign(cross) != settings.Side) return "camera on the wrong side of the line";
            }
            return null;
        }

        /// <summary>The checks every shot has had: ground, solid objects, the view to the look target, keepInFrame, distance to bodies.</summary>
        private static void Legacy(CameraFrame step, ShotEvaluator evaluator, Framing framing, List<ActorTrack> bodies, HashSet<(long, uint)> deaths, IssueRanges ranges, CanopyIndex canopies)
        {
            float time = step.Time;
            Vector3 camera = step.Position;
            if (Physics.Raycast(camera + Vector3.up * 1000f, Vector3.down, out RaycastHit ground, 2000f, Framing.TerrainMask, QueryTriggerInteraction.Ignore) && camera.y < ground.point.y + 0.1f)
                ranges.Mark("below_ground", "", time, $"camera {ShotEvaluator.F(camera.y - ground.point.y)} m from the terrain surface");
            Collider[] inside = Physics.OverlapSphere(camera, 0.15f, Framing.SolidMask & ~Framing.TerrainMask, QueryTriggerInteraction.Ignore);
            if (inside.Length > 0) ranges.Mark("inside_object", Blocker(inside[0]), time, "camera inside a solid object");
            Vector3 toLook = step.Target - camera;
            float distance = toLook.magnitude;
            if (distance > 0.6f && Physics.Raycast(camera, toLook / distance, out RaycastHit blocker, distance - 0.5f, Framing.SolidMask, QueryTriggerInteraction.Ignore))
                ranges.Mark("view_blocked", Blocker(blocker.collider), time, $"blocked {ShotEvaluator.F(blocker.distance)} m from the camera, look target {ShotEvaluator.F(distance)} m away");
            string? foliage = canopies.Foliage(camera, step.Target, out bool insideFoliage);
            if (foliage != null) ranges.Mark(insideFoliage ? "inside_foliage" : "view_foliage", foliage, time, insideFoliage ? "camera inside a tree's branches" : "view passes through a tree's branches");
            Matrix4x4 clip = Framing.Clip(camera, ShotEvaluator.Rotation(camera, step.Look, step.Roll, Vector3.zero), step.Fov, framing.Aspect);
            ReplayPlayer player = framing.Player;
            foreach (string actor in evaluator.Shot.keepInFrame)
            {
                ActorTrack track = player.Tracks.Get(actor);
                if (!track.Sample(time, out Vector3 feet, out _))
                {
                    // An actor that died is removed (its ragdoll takes over); leaving the frame then is expected.
                    if (!(time > track.End && deaths.Contains((track.User, track.Id)))) ranges.Mark("keep_absent", actor, time, "actor not present at this time");
                    continue;
                }
                float height = evaluator.Height(track);
                Vector2? center = Framing.Viewport(clip, feet + Vector3.up * height * 0.5f);
                bool anyVisible = new[] { feet, feet + Vector3.up * height * 0.5f, feet + Vector3.up * height }.Any(p => Framing.Viewport(clip, p) is Vector2 v && v.x >= 0 && v.x <= 1 && v.y >= 0 && v.y <= 1);
                if (!anyVisible) ranges.Mark("keep_off_screen", actor, time, "actor fully outside the frame");
                else if (center is not Vector2 c || c.x < 0.1f || c.x > 0.9f || c.y < 0.1f || c.y > 0.9f) ranges.Mark("keep_near_edge", actor, time, "actor centre outside the middle 80% of the frame");
            }
            foreach (ActorTrack track in bodies)
            {
                if (!track.Sample(time, out Vector3 feet, out _)) continue;
                float height = evaluator.Height(track);
                Vector3 closest = new(feet.x, Mathf.Clamp(camera.y, feet.y, feet.y + height), feet.z);
                float gap = Vector3.Distance(camera, closest) - framing.Radius(track);
                if (gap < 0.5f) ranges.Mark("too_close", track.Key, time, $"camera {ShotEvaluator.F(gap)} m from {framing.Name(track)}");
            }
        }

        /// <summary>The framing issues of one measured step.</summary>
        internal static void Framed(FrameMeasure frame, Framing framing, IssueRanges ranges)
        {
            float time = frame.Time;
            foreach (ActorFrame actor in frame.Actors)
            {
                if (actor.Visible < framing.MinVisible && framing.PartnerCounts(actor, time))
                    ranges.Mark("subject_hidden", actor.Id, time, $"{actor.Name} ({actor.Role}) {actor.Visible:P0} visible, needs {framing.MinVisible:P0}", actor.Visible);
                if (actor.Role == "partner" && actor.InFrame < 0.8f && framing.PartnerCounts(actor, time))
                    ranges.Mark("pair_split", actor.Id, time, $"{actor.Name} {actor.InFrame:P0} in frame, needs 80%", actor.InFrame);
                if (actor.Role != "subject") continue;
                (float min, float max) = framing.SizeRange;
                if (actor.Size < min || actor.Size > max)
                {
                    string intended = framing.SizeSet ? $"{min:F2}-{max:F2}" : $"at least {min:F2} (speck limit)";
                    ranges.Mark("subject_size", actor.Id, time, $"{actor.Name} body is {actor.Size:F2} of frame height, intended {intended}", actor.Size, actor.Size < min);
                }
                if (framing.Place is Vector2 place)
                {
                    Vector2 offset = actor.Center - place;
                    float gap = new Vector2(offset.x, offset.y / framing.Aspect).magnitude;
                    if (gap > 0.15f) ranges.Mark("subject_place", actor.Id, time, $"{actor.Name} centre {gap:F2} of frame width from the intended place", gap, false);
                }
                if (actor.Lead is float lead && lead < 0.6f)
                    ranges.Mark("no_lead_room", actor.Id, time, $"{actor.Name} lead room {lead:F2} ({(actor.Moving ? "moving" : "facing")}), needs 0.6", lead);
                if (Framing.EdgeCut(actor) is string joint)
                    ranges.Mark("edge_cut", actor.Id, time, $"a frame edge cuts {actor.Name} at the {joint}");
            }
            if (frame.Foreground > framing.MaxForeground || frame.HeadCovered)
                ranges.Mark("foreground_cover", "", time, frame.HeadCovered ? $"foreground covers a head box ({frame.Foreground:P0} of the frame)"
                    : $"foreground covers {frame.Foreground:P0} of the frame, limit {framing.MaxForeground:P0}", frame.Foreground, false);
        }

        /// <summary>The strike-frame rule at the shot's moment: both fighters fully in frame, heads inside and visible.</summary>
        private static void Strike(FrameMeasure frame, Framing framing, IssueRanges ranges)
        {
            foreach (ActorFrame actor in frame.Actors)
            {
                if (actor.Role == "keep") continue;
                List<string> faults = new();
                if (actor.InFrame < 0.95f) faults.Add($"{actor.InFrame:P0} in frame");
                if (!actor.Large && (actor.HeadBox.yMax > 1f || actor.HeadBox.xMin < 0f || actor.HeadBox.xMax > 1f)) faults.Add("head outside the frame");
                if (actor.Visible < framing.MinVisible) faults.Add($"{actor.Visible:P0} visible");
                if (faults.Count > 0) ranges.Mark("strike_frame", actor.Id, frame.Time, $"at the moment {actor.Name} ({actor.Role}): {string.Join(", ", faults)}");
            }
            framing.Measured.Where(m => m.Role != "keep" && !frame.Actors.Any(a => a.Id == m.Track.Key)).ToList()
                .ForEach(m => ranges.Mark("strike_frame", m.Track.Key, frame.Time, $"at the moment {framing.Name(m.Track)} is not present"));
            ranges.Step();
        }

        internal static string Blocker(Collider collider) =>
            collider.gameObject.layer == LayerMask.NameToLayer("terrain") ? "terrain" : Utils.GetPrefabName(collider.GetComponentInParent<ZNetView>()?.gameObject ?? collider.transform.root.gameObject);

        /// <summary>Tree canopies around the fight and every camera position of the shot.</summary>
        internal static CanopyIndex Canopies(ShotEvaluator evaluator)
        {
            Bounds area = ReplayPlayer.Instance.Tracks.FightArea();
            for (float time = evaluator.Shot.start; time <= evaluator.Shot.end; time += 0.25f) area.Encapsulate(evaluator.Evaluate(time).Position);
            return CanopyIndex.Around(area, 15f);
        }
    }
}
