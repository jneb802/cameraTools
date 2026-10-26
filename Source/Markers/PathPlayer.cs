using System;
using System.Collections.Generic;
using UnityEngine;

namespace cameraTools.Markers
{
    public enum PathLookMode
    {
        Waypoints,
        Forward,
        Target
    }

    public class PathPlayer : MonoBehaviour
    {
        private static PathPlayer? _instance;

        private bool _playing;
        private Marker? _marker;
        private List<Vector3> _samples = new();
        private List<float> _cumulative = new();
        private List<int> _waypointSampleIndex = new();
        private float _moveDuration;
        private float _elapsed;
        private PathLookMode _lookMode;
        private Vector3 _lookTarget;
        private List<float> _holdStarts = new(); // in path-progress seconds, where holds happen
        private float _totalDuration;

        public static bool IsPlaying => _instance != null && _instance._playing;
        public static string PlayingLabel => _instance?._marker?.label ?? "";

        private void Awake()
        {
            _instance = this;
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }
        }

        public static List<Vector3> SampleSpline(List<Waypoint> waypoints, int perSegment)
        {
            List<Vector3> result = new();
            int n = waypoints.Count;
            if (n == 0)
            {
                return result;
            }

            if (n == 1)
            {
                result.Add(waypoints[0].position);
                return result;
            }

            for (int i = 0; i < n - 1; i++)
            {
                Vector3 p0 = waypoints[Mathf.Max(i - 1, 0)].position;
                Vector3 p1 = waypoints[i].position;
                Vector3 p2 = waypoints[i + 1].position;
                Vector3 p3 = waypoints[Mathf.Min(i + 2, n - 1)].position;
                for (int s = 0; s < perSegment; s++)
                {
                    float t = (float)s / perSegment;
                    result.Add(CatmullRom(p0, p1, p2, p3, t));
                }
            }

            result.Add(waypoints[n - 1].position);
            return result;
        }

        private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            return 0.5f * (
                2f * p1 +
                (-p0 + p2) * t +
                (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
                (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }

        public static void Play(Marker marker, float? duration, PathLookMode lookMode, Vector3 lookTarget)
        {
            if (_instance == null)
            {
                throw new InvalidOperationException("PathPlayer is not ready.");
            }

            if (marker.Kind != MarkerKind.Path || marker.waypoints.Count < 2)
            {
                throw new InvalidOperationException("Path needs at least two waypoints.");
            }

            PathPlayer p = _instance;
            const int perSegment = 24;
            p._samples = SampleSpline(marker.waypoints, perSegment);
            p._cumulative = new List<float>(p._samples.Count) { 0f };
            for (int i = 1; i < p._samples.Count; i++)
            {
                p._cumulative.Add(p._cumulative[i - 1] + Vector3.Distance(p._samples[i - 1], p._samples[i]));
            }

            p._waypointSampleIndex = new List<int>();
            for (int w = 0; w < marker.waypoints.Count; w++)
            {
                p._waypointSampleIndex.Add(Mathf.Min(w * perSegment, p._samples.Count - 1));
            }

            float length = p._cumulative[p._cumulative.Count - 1];
            float holds = 0f;
            foreach (Waypoint wp in marker.waypoints) holds += Mathf.Max(0f, wp.hold);
            float total = duration ?? length / 1.6f + holds;
            p._moveDuration = Mathf.Max(0.5f, total - holds);
            p._totalDuration = p._moveDuration + holds;
            p._lookMode = lookMode;
            p._lookTarget = lookTarget;
            p._marker = marker;
            p._elapsed = 0f;
            p._playing = true;

            MarkerCamera.Sent($"path \"{marker.label}\"");
            CreativeZoneCameraController.StopMove();
            CreativeZoneCameraController.StopOrbit();
            CreativeZoneCameraController.SetFreeFly(true);
            p.Apply(0f);
        }

        public static void Stop()
        {
            if (_instance != null)
            {
                _instance._playing = false;
            }
        }

        public static string Status()
        {
            if (_instance == null || !_instance._playing || _instance._marker == null)
            {
                return "OK: playing=False";
            }

            return $"OK: playing=True path=\"{_instance._marker.label}\" elapsed={_instance._elapsed:F1} total={_instance._totalDuration:F1}";
        }

        private void LateUpdate()
        {
            if (!_playing)
            {
                return;
            }

            if (GameCamera.instance == null || _marker == null)
            {
                _playing = false;
                return;
            }

            _elapsed += Time.deltaTime;
            Apply(_elapsed);
            if (_elapsed >= _totalDuration)
            {
                _playing = false;
            }
        }

        // Converts elapsed wall time into a move-time (holds removed), then into arc length.
        private void Apply(float elapsed)
        {
            if (_marker == null)
            {
                return;
            }

            float length = _cumulative[_cumulative.Count - 1];
            float moveTime = 0f;
            float remaining = elapsed;
            // Walk waypoints in order: before reaching a waypoint's arc position we are moving; at it we hold.
            float speed = length / _moveDuration;
            float cursor = 0f; // arc length reached
            int w = 0;
            bool done = false;
            while (!done)
            {
                float nextWaypointArc = w < _marker.waypoints.Count ? _cumulative[_waypointSampleIndex[w]] : length;
                float moveNeeded = (nextWaypointArc - cursor) / speed;
                if (remaining <= moveNeeded)
                {
                    cursor += remaining * speed;
                    moveTime += remaining;
                    done = true;
                    break;
                }

                remaining -= moveNeeded;
                moveTime += moveNeeded;
                cursor = nextWaypointArc;
                if (w >= _marker.waypoints.Count)
                {
                    done = true;
                    break;
                }

                float hold = Mathf.Max(0f, _marker.waypoints[w].hold);
                if (remaining <= hold)
                {
                    done = true;
                    break;
                }

                remaining -= hold;
                w++;
            }

            float arc = Mathf.Clamp(Ease(cursor / Mathf.Max(0.001f, length)) * length, 0f, length);
            Vector3 position = PositionAt(arc, out int segment, out float segT);
            Quaternion rotation = RotationAt(arc, position, segment, segT);
            CreativeZoneCameraController.ApplyExternalTransform(position, rotation);
        }

        private static float Ease(float t)
        {
            // Gentle ease on the first and last 12 percent only, constant speed in between.
            const float e = 0.12f;
            if (t < e)
            {
                float u = t / e;
                return e * (u * u * (3f - 2f * u)) * 0.5f + t * 0.5f;
            }

            if (t > 1f - e)
            {
                float u = (1f - t) / e;
                return 1f - (e * (u * u * (3f - 2f * u)) * 0.5f + (1f - t) * 0.5f);
            }

            return t;
        }

        private Vector3 PositionAt(float arc, out int segment, out float segT)
        {
            int hi = _cumulative.Count - 1;
            int lo = 0;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (_cumulative[mid] <= arc) lo = mid; else hi = mid;
            }

            float segLen = _cumulative[hi] - _cumulative[lo];
            segT = segLen <= 0.0001f ? 0f : Mathf.Clamp01((arc - _cumulative[lo]) / segLen);
            segment = lo;
            return Vector3.Lerp(_samples[lo], _samples[hi], segT);
        }

        private Quaternion RotationAt(float arc, Vector3 position, int segment, float segT)
        {
            if (_marker == null)
            {
                return Quaternion.identity;
            }

            if (_lookMode == PathLookMode.Target)
            {
                Vector3 dir = _lookTarget - position;
                return dir.sqrMagnitude < 0.0001f ? Quaternion.identity : Quaternion.LookRotation(dir.normalized, Vector3.up);
            }

            if (_lookMode == PathLookMode.Forward)
            {
                int ahead = Mathf.Min(segment + 3, _samples.Count - 1);
                Vector3 dir = _samples[ahead] - position;
                if (dir.sqrMagnitude < 0.0001f && segment > 0)
                {
                    dir = position - _samples[segment - 1];
                }

                return dir.sqrMagnitude < 0.0001f ? Quaternion.identity : Quaternion.LookRotation(dir.normalized, Vector3.up);
            }

            // Waypoints mode: find the waypoint interval containing this arc and slerp.
            int w = 0;
            while (w + 1 < _waypointSampleIndex.Count && _cumulative[_waypointSampleIndex[w + 1]] <= arc)
            {
                w++;
            }

            int wNext = Mathf.Min(w + 1, _marker.waypoints.Count - 1);
            float a0 = _cumulative[_waypointSampleIndex[w]];
            float a1 = _cumulative[_waypointSampleIndex[wNext]];
            float t = a1 - a0 <= 0.0001f ? 1f : Mathf.Clamp01((arc - a0) / (a1 - a0));
            t = t * t * (3f - 2f * t);
            return Quaternion.Slerp(_marker.waypoints[w].Rotation, _marker.waypoints[wNext].Rotation, t);
        }
    }
}
