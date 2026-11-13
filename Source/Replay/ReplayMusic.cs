using System;
using System.Collections.Generic;
using UnityEngine;

namespace cameraTools.Replay
{
    public sealed class ReplayMusic : IDisposable
    {
        private readonly List<(AudioClip clip, float volume)> _tracks = new();
        public readonly List<string> Labels = new() { "Off" };
        public AudioSource Source { get; }
        public int Selected { get; private set; }
        public float Volume { get; set; } = 1f;

        public ReplayMusic()
        {
            GameObject root = new("Replay music");
            Source = root.AddComponent<AudioSource>();
            Source.playOnAwake = false;
            Source.loop = true;
            Source.spatialBlend = 0;
            if (MusicMan.instance == null) return;
            Source.outputAudioMixerGroup = MusicMan.instance.m_musicMixer;
            List<MusicMan.NamedMusic> music = new(MusicMan.instance.m_music);
            music.Sort((a, b) => string.Compare(a.m_name, b.m_name, StringComparison.OrdinalIgnoreCase));
            foreach (MusicMan.NamedMusic track in music)
            {
                if (!track.m_enabled || track.m_clips == null) continue;
                for (int index = 0; index < track.m_clips.Length; index++)
                {
                    AudioClip clip = track.m_clips[index];
                    if (clip == null) continue;
                    Labels.Add(track.m_clips.Length > 1 ? $"{track.m_name} ({index + 1})" : track.m_name);
                    _tracks.Add((clip, track.m_volume));
                }
            }
        }

        public void Select(int index)
        {
            if (index < 0 || index >= Labels.Count) throw new ArgumentOutOfRangeException(nameof(index));
            Source.Stop();
            Selected = index;
            Source.clip = index == 0 ? null : _tracks[index - 1].clip;
        }

        public void Update(float time, float speed, bool paused)
        {
            if (Selected == 0 || Source.clip == null) return;
            AudioClip clip = Source.clip;
            Source.volume = Volume * _tracks[Selected - 1].volume * MusicMan.m_masterMusicVolume;
            Source.pitch = speed;
            int sample = (int)((double)time * clip.frequency % Math.Max(1, clip.samples));
            int actual = Source.timeSamples;
            if ((paused || !Source.isPlaying) ? actual != sample : Math.Abs(actual - sample) > clip.frequency / 8)
                Source.timeSamples = sample;
            if (paused) Source.Pause();
            else if (!Source.isPlaying) Source.Play();
        }

        public void Dispose()
        {
            Source.Stop();
            UnityEngine.Object.Destroy(Source.gameObject);
        }
    }
}
