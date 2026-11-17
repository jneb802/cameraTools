using System.Collections.Generic;
using UnityEngine;

namespace cameraTools.Replay
{
    /// <summary>
    /// Shows what a projectile spawns where it hits (Projectile.m_spawnOnHit), for recordings that did not record it.
    /// The game makes that object with Instantiate, not EffectList, and it is a short-lived network object, so
    /// recordings made before the recorder sampled area objects have no trace of it: a lightning staff's strike was
    /// missing. Each recorded projectile hit (or, for a terrain hit with no hit event, the end of a projectile track
    /// that ended before its time to live) rebuilds the spawned object as a script-free copy at that point and time,
    /// simulates its particles from the impact, replays its LightFlicker fades and TimedDestruction timeouts, and plays
    /// its ZSFX sound. A recording that already holds the spawned prefab as an effect plays that instead.
    /// </summary>
    internal sealed class SpawnOnHitPlayer
    {
        private const float MaxLife = 6f;

        private sealed class Strike
        {
            internal GameObject Prefab = null!;
            internal float Time;
            internal Vector3 Position;
            internal Quaternion Rotation;
            internal float Life;
            internal GameObject? Root;
            internal ParticleSystem[] Systems = System.Array.Empty<ParticleSystem>();
            internal readonly List<(Light Light, float Base, LightFlicker Settings)> Lights = new();
            internal readonly List<(GameObject Child, float Timeout)> Timeouts = new();
            internal readonly List<(AudioSource Source, float Volume, float Pitch)> Sounds = new();
            internal float Applied = float.NaN;
        }

        private readonly List<Strike> _strikes = new();
        private readonly HashSet<AudioSource> _sources = new();

        internal int Count => _strikes.Count;

        internal SpawnOnHitPlayer(ReplayFile file, ReplayTracks tracks)
        {
            if (ZNetScene.instance == null) return;
            HashSet<string> recorded = new();
            foreach (EffectTrack effect in file.Effects) recorded.Add(effect.Prefab);
            Dictionary<(long, uint), HitEvent> firstHit = new();
            foreach (HitEvent hit in file.Hits)
            {
                if (hit.ProjectileUser == 0 && hit.ProjectileId == 0) continue;
                (long, uint) key = (hit.ProjectileUser, hit.ProjectileId);
                if (!firstHit.TryGetValue(key, out HitEvent earlier) || hit.Time < earlier.Time) firstHit[key] = hit;
            }
            foreach (ActorTrack track in tracks.All)
            {
                if (!track.IsProjectile || track.Times.Count == 0) continue;
                GameObject prefab = ZNetScene.instance.GetPrefab(track.Prefab);
                Projectile? projectile = prefab != null ? prefab.GetComponent<Projectile>() : null;
                if (projectile == null || projectile.m_spawnOnHit == null || recorded.Contains(projectile.m_spawnOnHit.name)) continue;
                int last = track.Times.Count - 1;
                Quaternion flight = track.Rotations[last];
                Vector3 position;
                float time;
                if (firstHit.TryGetValue((track.User, track.Id), out HitEvent hit))
                {
                    // The hit point is exact; the last sample can be a metre or more short of it at staff speeds.
                    position = hit.Point; time = hit.Time;
                }
                else
                {
                    // No hit event: a terrain or building hit, or a projectile that ran out of time. Only a hit spawns.
                    if (projectile.m_ttl > 0f && track.End - track.Start >= projectile.m_ttl - 0.1f) continue;
                    position = track.Positions[last]; time = track.End;
                }
                position += flight * projectile.m_spawnOffset;
                Quaternion rotation = Quaternion.identity;
                if (projectile.m_copyProjectileRotation) rotation = flight;
                if (projectile.m_spawnRandomRotation) rotation = Quaternion.Euler(0f, (track.Id * 37u) % 360u, 0f);
                if (projectile.m_spawnFacingRotation) rotation = Quaternion.Euler(0f, flight.eulerAngles.y, 0f);
                _strikes.Add(new Strike { Prefab = projectile.m_spawnOnHit, Time = time, Position = position, Rotation = rotation,
                    Life = Life(projectile.m_spawnOnHit) });
            }
        }

        /// <summary>How long the spawned object shows: its timeouts, light lifetimes and one-shot particle lifetimes.</summary>
        private static float Life(GameObject prefab)
        {
            float life = 0.5f;
            foreach (TimedDestruction timed in prefab.GetComponentsInChildren<TimedDestruction>(true)) life = Mathf.Max(life, timed.m_timeout);
            foreach (LightFlicker flicker in prefab.GetComponentsInChildren<LightFlicker>(true)) life = Mathf.Max(life, flicker.m_ttl);
            foreach (ParticleSystem system in prefab.GetComponentsInChildren<ParticleSystem>(true))
                if (!system.main.loop) life = Mathf.Max(life, system.main.duration + system.main.startLifetime.constantMax);
            return Mathf.Min(life, MaxLife);
        }

        internal bool Owns(AudioSource source) => _sources.Contains(source);

        internal void Update(float time, bool paused, float speed)
        {
            foreach (Strike strike in _strikes)
            {
                float elapsed = time - strike.Time;
                if (elapsed < 0f || elapsed > strike.Life)
                {
                    if (strike.Root != null && strike.Root.activeSelf)
                    {
                        strike.Root.SetActive(false);
                        foreach (var sound in strike.Sounds) sound.Source.Stop();
                    }
                    strike.Applied = float.NaN;
                    continue;
                }
                if (strike.Root == null) Build(strike);
                if (strike.Root == null) continue;
                if (!strike.Root.activeSelf) strike.Root.SetActive(true);
                if (elapsed != strike.Applied)
                {
                    foreach (ParticleSystem system in strike.Systems) system.Simulate(elapsed, true, true, false);
                    foreach (var light in strike.Lights) light.Light.intensity = light.Base * Flicker(light.Settings, elapsed);
                    foreach (var timeout in strike.Timeouts)
                        if (timeout.Child != null) timeout.Child.SetActive(elapsed < timeout.Timeout);
                    strike.Applied = elapsed;
                }
                foreach (var sound in strike.Sounds)
                {
                    AudioSource source = sound.Source;
                    if (source.clip == null || !source.gameObject.activeInHierarchy) continue;
                    source.volume = sound.Volume; source.pitch = sound.Pitch * speed;
                    int sample = Mathf.RoundToInt(elapsed * source.clip.frequency * sound.Pitch);
                    if (sample >= source.clip.samples) { source.Stop(); continue; }
                    if (paused) { source.Pause(); source.timeSamples = sample; continue; }
                    if (!source.isPlaying || Mathf.Abs(source.timeSamples - sample) > source.clip.frequency / 10) source.timeSamples = sample;
                    if (!source.isPlaying) source.Play();
                }
            }
        }

        /// <summary>LightFlicker's intensity curve without its random phase, so every render of a shot matches.</summary>
        private static float Flicker(LightFlicker settings, float elapsed)
        {
            float phase = elapsed * settings.m_flickerSpeed;
            float target = 1f + Mathf.Sin(phase) * Mathf.Sin(phase * 0.56436f) * Mathf.Cos(phase * 0.758348f) * settings.m_flickerIntensity;
            if (settings.m_fadeInDuration > 0f) target *= Utils.LerpStep(0f, settings.m_fadeInDuration, elapsed);
            if (settings.m_ttl > 0f)
            {
                if (elapsed > settings.m_ttl) return 0f;
                target *= 1f - Utils.LerpStep(settings.m_ttl - settings.m_fadeDuration, settings.m_ttl, elapsed);
            }
            return target;
        }

        private void Build(Strike strike)
        {
            GameObject root = GhostManager.CreateVisual(strike.Prefab, strike.Position, strike.Rotation);
            root.name = "Replay spawn on hit " + strike.Prefab.name;
            Transform prefabRoot = strike.Prefab.transform;
            List<ParticleSystem> systems = new();
            foreach (ParticleSystem system in root.GetComponentsInChildren<ParticleSystem>(true))
            {
                // Simulate with children from the topmost systems only, or nested systems would run twice.
                if (system.transform.parent == null || system.transform.parent.GetComponentInParent<ParticleSystem>(true) == null) systems.Add(system);
            }
            strike.Systems = systems.ToArray();
            foreach (LightFlicker flicker in strike.Prefab.GetComponentsInChildren<LightFlicker>(true))
            {
                Light? light = Find(root.transform, prefabRoot, flicker.transform)?.GetComponent<Light>();
                if (light != null) strike.Lights.Add((light, light.intensity, flicker));
            }
            foreach (TimedDestruction timed in strike.Prefab.GetComponentsInChildren<TimedDestruction>(true))
            {
                if (timed.transform == prefabRoot || !timed.m_triggerOnAwake) continue;
                Transform? child = Find(root.transform, prefabRoot, timed.transform);
                if (child != null) strike.Timeouts.Add((child.gameObject, timed.m_timeout));
            }
            foreach (ZSFX sfx in strike.Prefab.GetComponentsInChildren<ZSFX>(true))
            {
                Transform? child = Find(root.transform, prefabRoot, sfx.transform);
                AudioSource? source = child != null ? child.GetComponent<AudioSource>() : null;
                if (source == null || sfx.m_audioClips.Length == 0) continue;
                // ZSFX picks a random clip; pick one from the hit so every render of a shot plays the same one.
                source.clip = sfx.m_audioClips[Mathf.Abs(Mathf.RoundToInt(strike.Time * 1000f)) % sfx.m_audioClips.Length];
                source.enabled = true; source.playOnAwake = false; source.loop = false;
                strike.Sounds.Add((source, (sfx.m_minVol + sfx.m_maxVol) * 0.5f, (sfx.m_minPitch + sfx.m_maxPitch) * 0.5f));
                _sources.Add(source);
            }
            strike.Root = root;
        }

        /// <summary>The copy's transform at the same path below the root as a transform in the prefab.</summary>
        private static Transform? Find(Transform copyRoot, Transform prefabRoot, Transform inPrefab)
        {
            if (inPrefab == prefabRoot) return copyRoot;
            List<int> path = new();
            for (Transform node = inPrefab; node != prefabRoot && node != null; node = node.parent) path.Add(node.GetSiblingIndex());
            Transform current = copyRoot;
            for (int i = path.Count - 1; i >= 0; i--)
            {
                if (path[i] >= current.childCount) return null;
                current = current.GetChild(path[i]);
            }
            return current;
        }

        internal void Dispose()
        {
            foreach (Strike strike in _strikes) if (strike.Root != null) Object.Destroy(strike.Root);
            _strikes.Clear(); _sources.Clear();
        }
    }
}
