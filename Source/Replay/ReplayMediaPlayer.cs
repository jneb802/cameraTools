using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;

namespace cameraTools.Replay
{
    internal sealed class ReplayMediaPlayer
    {
        internal static ReplayMediaPlayer? Active;
        private readonly ReplayFile _file;
        private readonly Dictionary<EffectTrack, (GameObject Root, VisualPlayer Visual)> _effects = new();
        private readonly Dictionary<SoundTrack, AudioSource> _sounds = new();
        private readonly Dictionary<string, GameObject> _prefabs = new();
        private readonly Dictionary<string, AudioClip> _clips = new();
        private readonly Dictionary<string, AudioMixerGroup> _mixers = new();
        private readonly HashSet<string> _missing = new();
        private readonly Dictionary<AudioSource, bool> _muted = new();
        private readonly Dictionary<Renderer, bool> _hidden = new();
        private float _nextScan;
        private float _lastTime = -1f;

        internal ReplayMediaPlayer(ReplayFile file)
        {
            _file = file;
            Active = this;
            foreach (GameObject prefab in Resources.FindObjectsOfTypeAll<GameObject>())
                if (!prefab.scene.IsValid()) _prefabs[prefab.name] = prefab;
            foreach (KeyValuePair<string, GameObject> pair in ReplayMediaRecorder.Prefabs)
                if (pair.Value != null) _prefabs[pair.Key] = pair.Value;
            foreach (AudioClip clip in Resources.FindObjectsOfTypeAll<AudioClip>())
                _clips[ClipKey(clip.name, clip.frequency, clip.samples)] = clip;
            foreach (AudioMixerGroup mixer in Resources.FindObjectsOfTypeAll<AudioMixerGroup>()) _mixers[mixer.name] = mixer;
        }
        internal void HideLiveEffect(GameObject root)
        {
            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!_hidden.ContainsKey(renderer)) _hidden[renderer] = renderer.enabled;
                renderer.enabled = false;
            }
            foreach (AudioSource source in root.GetComponentsInChildren<AudioSource>(true))
            {
                if (!_muted.ContainsKey(source)) _muted[source] = source.mute;
                source.mute = true;
            }
        }
        private static string ClipKey(string name, int frequency, int samples) => name + ":" + frequency + ":" + samples;
        private void Missing(string name)
        {
            if (_missing.Add(name)) cameraToolsPlugin.TemplateLogger.LogWarning("Replay asset unavailable: " + name);
        }

        internal void Update(float time, bool paused, float speed)
        {
            foreach (EffectTrack track in _file.Effects)
            {
                bool active = time >= track.Start && time <= track.End && track.Frames.Count > 0;
                if (!active)
                {
                    if (_effects.TryGetValue(track, out var inactive)) inactive.Root.SetActive(false);
                    continue;
                }
                if (!_effects.TryGetValue(track, out var effect))
                {
                    GameObject? prefab = ZNetScene.instance?.GetPrefab(track.Prefab);
                    if (prefab == null) _prefabs.TryGetValue(track.Prefab, out prefab);
                    if (prefab == null) { Missing("effect " + track.Prefab); continue; }
                    GameObject root = GhostManager.CreateVisual(prefab, Vector3.zero, Quaternion.identity);
                    effect = (root, new VisualPlayer(root)); _effects[track] = effect;
                }
                effect.Root.SetActive(true);
                int index = Index(track.Frames, time, frame => frame.Time);
                EffectFrame a = track.Frames[index];
                EffectFrame b = track.Frames[Mathf.Min(index + 1, track.Frames.Count - 1)];
                float t = Fraction(time, a.Time, b.Time);
                effect.Root.transform.position = Vector3.Lerp(a.Position, b.Position, t);
                effect.Root.transform.rotation = Quaternion.Slerp(a.Rotation, b.Rotation, t);
                effect.Visual.Apply(a.Visual, b.Visual, t);
            }
            bool discontinuity = time < _lastTime || Mathf.Abs(time - _lastTime) > 0.2f;
            foreach (SoundTrack track in _file.Sounds)
            {
                if (time < track.Start || time > track.End + 1f / 30f || track.Frames.Count == 0)
                {
                    if (_sounds.TryGetValue(track, out AudioSource inactive)) inactive.Stop();
                    continue;
                }
                if (!_sounds.TryGetValue(track, out AudioSource source))
                {
                    if (!_clips.TryGetValue(ClipKey(track.Clip, track.Frequency, track.Samples), out AudioClip clip))
                    { Missing("audio " + track.Clip); continue; }
                    GameObject root = new GameObject("Replay audio " + track.Clip);
                    source = root.AddComponent<AudioSource>(); source.playOnAwake = false; source.clip = clip;
                    source.loop = track.Loop; source.spatialBlend = track.SpatialBlend; source.minDistance = track.MinDistance;
                    source.maxDistance = track.MaxDistance; source.rolloffMode = (AudioRolloffMode)track.Rolloff;
                    if (_mixers.TryGetValue(track.Mixer, out AudioMixerGroup mixer)) source.outputAudioMixerGroup = mixer;
                    _sounds[track] = source;
                }
                int index = Index(track.Frames, time, frame => frame.Time);
                SoundFrame a = track.Frames[index];
                SoundFrame b = track.Frames[Mathf.Min(index + 1, track.Frames.Count - 1)];
                float t = Fraction(time, a.Time, b.Time);
                source.transform.position = Vector3.Lerp(a.Position, b.Position, t);
                source.volume = Mathf.Lerp(a.Volume, b.Volume, t); source.pitch = Mathf.Lerp(a.Pitch, b.Pitch, t) * speed;
                source.panStereo = Mathf.Lerp(a.Pan, b.Pan, t);
                int sample = a.Sample + Mathf.RoundToInt((time - a.Time) * track.Frequency * a.Pitch);
                sample = track.Loop ? ((sample % track.Samples) + track.Samples) % track.Samples : Mathf.Clamp(sample, 0, track.Samples - 1);
                if (paused) { source.Pause(); source.timeSamples = sample; }
                else
                {
                    if (discontinuity || !source.isPlaying || Mathf.Abs(source.timeSamples - sample) > track.Frequency / 10)
                        source.timeSamples = sample;
                    if (!source.isPlaying) source.Play();
                }
            }
            _lastTime = time;
            if (Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + 0.25f;
                foreach (AudioSource source in Object.FindObjectsByType<AudioSource>(FindObjectsSortMode.None))
                {
                    if (_sounds.ContainsValue(source)) continue;
                    if (!_muted.ContainsKey(source)) _muted[source] = source.mute;
                    source.mute = true;
                }
            }
        }

        private static int Index<T>(List<T> frames, float time, System.Func<T, float> getTime)
        {
            int lo = 0, hi = frames.Count - 1;
            while (lo < hi) { int mid = (lo + hi + 1) / 2; if (getTime(frames[mid]) <= time) lo = mid; else hi = mid - 1; }
            return lo;
        }
        private static float Fraction(float time, float a, float b) => b > a ? Mathf.Clamp01((time - a) / (b - a)) : 0;

        internal void Dispose()
        {
            Active = null;
            foreach (var effect in _effects.Values) Object.Destroy(effect.Root);
            foreach (AudioSource source in _sounds.Values) Object.Destroy(source.gameObject);
            foreach (KeyValuePair<AudioSource, bool> pair in _muted) if (pair.Key != null) pair.Key.mute = pair.Value;
            foreach (KeyValuePair<Renderer, bool> pair in _hidden) if (pair.Key != null) pair.Key.enabled = pair.Value;
            _effects.Clear(); _sounds.Clear(); _muted.Clear();
        }
    }
}
