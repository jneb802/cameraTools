using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace cameraTools.Replay
{
    internal sealed class ReplayMediaRecorder
    {
        internal static ReplayMediaRecorder? Active;
        internal static readonly Dictionary<string, GameObject> Prefabs = new();
        private readonly ReplayFile _file;
        private readonly ReplayEventRecorder _events;
        private readonly float _start;
        private readonly Dictionary<GameObject, EffectTrack> _effects = new();
        private readonly Dictionary<AudioSource, SoundTrack> _sounds = new();
        private readonly HashSet<AudioSource> _observed = new();
        private readonly HashSet<GameObject> _soundRoots = new();
        private readonly Dictionary<AudioSource, int> _lastSoundSample = new();
        private int _sample;
        internal ReplayMediaRecorder(ReplayFile file, ReplayEventRecorder events)
        {
            _file = file; _events = events; _start = Time.time; Active = this;
            foreach (ZSFX sound in Object.FindObjectsByType<ZSFX>(FindObjectsSortMode.None))
                if (Nearby(sound.transform.position)) _soundRoots.Add(sound.gameObject);
        }
        private static bool Nearby(Vector3 position) => Player.m_localPlayer != null && Vector3.Distance(position, Player.m_localPlayer.transform.position) <= 150f;

        internal void Effect(GameObject instance, GameObject prefab)
        {
            Prefabs[prefab.name] = prefab;
            if (!Nearby(instance.transform.position) || _effects.ContainsKey(instance)) return;
            // Networked aftermath is sampled with the entity frames.
            if (instance.GetComponent<Ragdoll>() != null || instance.GetComponent<ItemDrop>() != null) return;
            EffectTrack track = new() { Prefab = prefab.name, Start = Time.time - _start, End = Time.time - _start };
            _effects.Add(instance, track); _file.Effects.Add(track);
        }

        internal void Death(Character character)
        {
            if (!Nearby(character.transform.position)) return;
            ZDO? zdo = character.GetComponent<ZNetView>()?.GetZDO();
            _events.Death(Time.time - _start, zdo?.m_uid.UserID ?? 0, zdo?.m_uid.ID ?? 0, character.name);
        }

        internal void BeginSample() { _observed.Clear(); _sample++; }
        internal void Sounds(GameObject root, float time, ref long bytes)
        {
            foreach (AudioSource source in root.GetComponentsInChildren<AudioSource>(true))
            {
                if (!_observed.Add(source) || !source.isPlaying || source.clip == null) continue;
                bool consecutive = _lastSoundSample.TryGetValue(source, out int previous) && previous == _sample - 1;
                if (!_sounds.TryGetValue(source, out SoundTrack track) || track.Clip != source.clip.name || !consecutive ||
                    (track.Frames.Count > 0 && !source.loop && source.timeSamples < track.Frames[track.Frames.Count - 1].Sample))
                {
                    track = new SoundTrack { Clip = source.clip.name, Frequency = source.clip.frequency, Samples = source.clip.samples,
                        Mixer = source.outputAudioMixerGroup != null ? source.outputAudioMixerGroup.name : "",
                        Start = time, Loop = source.loop, SpatialBlend = source.spatialBlend, MinDistance = source.minDistance,
                        MaxDistance = source.maxDistance, Rolloff = (int)source.rolloffMode };
                    _sounds[source] = track; _file.Sounds.Add(track);
                }
                track.End = time;
                _lastSoundSample[source] = _sample;
                track.Frames.Add(new SoundFrame { Time = time, Position = source.transform.position,
                    Sample = source.timeSamples, Volume = source.mute ? 0f : source.volume, Pitch = source.pitch, Pan = source.panStereo });
                bytes += 64;
            }
        }

        internal void Sample(float time, ref long bytes)
        {
            List<GameObject> removed = new();
            foreach (KeyValuePair<GameObject, EffectTrack> pair in _effects)
            {
                if (pair.Key == null || !pair.Key.activeInHierarchy) { removed.Add(pair.Key!); continue; }
                pair.Value.End = time;
                pair.Value.Frames.Add(new EffectFrame { Time = time, Position = pair.Key.transform.position,
                    Rotation = pair.Key.transform.rotation, Visual = ReplayVisuals.Capture(pair.Key, true, ref bytes) });
                Sounds(pair.Key, time, ref bytes);
            }
            foreach (GameObject root in removed) _effects.Remove(root);
            _soundRoots.RemoveWhere(root => root == null);
            foreach (GameObject root in _soundRoots) Sounds(root, time, ref bytes);
        }

        [HarmonyPatch(typeof(EffectList), nameof(EffectList.Create))]
        private static class EffectPatch
        {
            private static void Postfix(EffectList __instance, int variant, GameObject[] __result)
            {
                int index = 0;
                foreach (EffectList.EffectData data in __instance.m_effectPrefabs)
                {
                    if (!data.m_enabled || (variant >= 0 && data.m_variant >= 0 && variant != data.m_variant)) continue;
                    if (index >= __result.Length) break;
                    GameObject instance = __result[index++];
                    if (data.m_prefab != null && instance != null)
                    {
                        Active?.Effect(instance, data.m_prefab);
                        ReplayMediaPlayer.Active?.HideLiveEffect(instance);
                    }
                }
            }
        }

        [HarmonyPatch(typeof(ZSFX), nameof(ZSFX.Play))]
        private static class SoundPatch
        {
            private static void Postfix(ZSFX __instance)
            {
                if (Active != null && Nearby(__instance.transform.position)) Active._soundRoots.Add(__instance.gameObject);
            }
        }

        [HarmonyPatch(typeof(Character), "OnDeath")]
        private static class DeathPatch
        {
            private static void Prefix(Character __instance) => Active?.Death(__instance);
        }

        [HarmonyPatch(typeof(Player), nameof(Player.OnDeath))]
        private static class PlayerDeathPatch
        {
            private static void Prefix(Player __instance)
            {
                if (__instance.GetComponent<ZNetView>()?.IsOwner() == true) Active?.Death(__instance);
            }
        }
    }
}
