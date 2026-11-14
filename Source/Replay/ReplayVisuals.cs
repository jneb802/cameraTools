using System;
using System.Collections.Generic;
using UnityEngine;

namespace cameraTools.Replay
{
    internal static class ReplayVisuals
    {
        internal static string Path(Transform child, Transform root)
        {
            if (child == root) return "";
            string path = child.GetSiblingIndex().ToString();
            for (Transform parent = child.parent; parent != null && parent != root; parent = parent.parent)
                path = parent.GetSiblingIndex() + "/" + path;
            return path;
        }

        internal static Transform? Find(Transform root, string path)
        {
            if (path.Length == 0) return root;
            Transform current = root;
            foreach (string segment in path.Split('/'))
            {
                if (!int.TryParse(segment, out int index) || index < 0 || index >= current.childCount) return null;
                current = current.GetChild(index);
            }
            return current;
        }

        internal static VisualState Capture(GameObject root, bool nodes, ref long bytes)
        {
            VisualState result = new() { Scale = root.transform.lossyScale };
            if (nodes)
                foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
                    if (child != root.transform)
                        result.Nodes.Add(new BonePose { Path = Path(child, root.transform), Position = child.localPosition, Rotation = child.localRotation, Scale = child.localScale });
            foreach (ParticleSystem system in root.GetComponentsInChildren<ParticleSystem>(true))
            {
                ParticleSystemRenderer renderer = system.GetComponent<ParticleSystemRenderer>();
                ParticleSystem.Particle[] particles = new ParticleSystem.Particle[system.particleCount];
                int count = system.GetParticles(particles);
                if (count != particles.Length) Array.Resize(ref particles, count);
                ParticleState state = new() { Path = Path(system.transform, root.transform), Time = system.time,
                    Visible = system.gameObject.activeInHierarchy && renderer != null && renderer.enabled, Particles = particles };
                if (system.trails.enabled) { state.Trails = system.GetTrails(); bytes += ParticleTrailIO.Size(state.Trails.Value); }
                result.Particles.Add(state);
                bytes += 128 + particles.Length * 128L;
            }
            foreach (TrailRenderer trail in root.GetComponentsInChildren<TrailRenderer>(true))
            {
                Vector3[] positions = new Vector3[trail.positionCount];
                int count = trail.GetPositions(positions);
                if (positions.Length != count) Array.Resize(ref positions, count);
                result.Trails.Add(new TrailState { Path = Path(trail.transform, root.transform), Visible = trail.enabled && trail.gameObject.activeInHierarchy, Positions = positions });
                bytes += 128 + positions.Length * 12L;
            }
            foreach (Light light in root.GetComponentsInChildren<Light>(true))
                result.Lights.Add(new LightState { Path = Path(light.transform, root.transform), Enabled = light.enabled && light.gameObject.activeInHierarchy,
                    Intensity = light.intensity, Range = light.range, Color = light.color });
            bytes += 128 + result.Nodes.Count * 80L + result.Lights.Count * 64L;
            return result;
        }
    }

    internal sealed class VisualPlayer
    {
        private readonly GameObject _root;
        private readonly Dictionary<string, Transform?> _transforms = new();
        private readonly Dictionary<uint, ParticleSystem.Particle> _nextParticles = new();
        private ParticleSystem.Particle[] _buffer = Array.Empty<ParticleSystem.Particle>();

        internal VisualPlayer(GameObject root) { _root = root; }
        private Transform? Find(string path)
        {
            if (_root == null) return null;
            // Equipment mods can replace children after creation or after a seek. Unity's destroyed
            // objects still have managed references, so test Unity equality before reusing the cache.
            if (!_transforms.TryGetValue(path, out Transform? result) || result == null)
            { result = ReplayVisuals.Find(_root.transform, path); if (result != null) _transforms[path] = result; }
            return result != null ? result : null;
        }

        private void Activate(Transform node)
        {
            for (Transform current = node; current != null; current = current.parent)
            {
                if (!current.gameObject.activeSelf) current.gameObject.SetActive(true);
                if (current == _root.transform) break;
            }
        }

        internal void Apply(VisualState first, VisualState second, float fraction)
        {
            if (_root == null) return;
            _root.transform.localScale = Vector3.Lerp(first.Scale, second.Scale, fraction);
            for (int i = 0; i < first.Nodes.Count; i++)
            {
                BonePose a = first.Nodes[i];
                BonePose b = i < second.Nodes.Count && second.Nodes[i].Path == a.Path ? second.Nodes[i] : a;
                Transform? node = Find(a.Path);
                if (node == null) continue;
                node.localPosition = Vector3.Lerp(a.Position, b.Position, fraction);
                node.localRotation = Quaternion.Slerp(a.Rotation, b.Rotation, fraction);
                node.localScale = Vector3.Lerp(a.Scale, b.Scale, fraction);
            }
            for (int i = 0; i < first.Particles.Count; i++)
            {
                ParticleState state = first.Particles[i];
                ParticleSystem? system = Find(state.Path)?.GetComponent<ParticleSystem>();
                if (system == null) continue;
                ParticleSystemRenderer renderer = system.GetComponent<ParticleSystemRenderer>();
                if (renderer != null) renderer.enabled = state.Visible;
                if (!state.Visible) { system.Clear(false); continue; }
                Activate(system.transform);
                system.Pause(false);
                system.time = state.Time;
                int count = state.Particles.Length;
                if (_buffer.Length < count) _buffer = new ParticleSystem.Particle[count];
                _nextParticles.Clear();
                if (i < second.Particles.Count && second.Particles[i].Path == state.Path)
                    foreach (ParticleSystem.Particle particle in second.Particles[i].Particles) _nextParticles[particle.randomSeed] = particle;
                for (int p = 0; p < count; p++)
                {
                    ParticleSystem.Particle particle = state.Particles[p];
                    if (_nextParticles.TryGetValue(particle.randomSeed, out ParticleSystem.Particle next))
                    {
                        particle.position = Vector3.Lerp(particle.position, next.position, fraction);
                        particle.rotation3D = Vector3.Lerp(particle.rotation3D, next.rotation3D, fraction);
                        particle.remainingLifetime = Mathf.Lerp(particle.remainingLifetime, next.remainingLifetime, fraction);
                    }
                    _buffer[p] = particle;
                }
                system.SetParticles(_buffer, count);
                if (state.Trails.HasValue) system.SetTrails(state.Trails.Value);
            }
            foreach (TrailState state in first.Trails)
            {
                TrailRenderer? trail = Find(state.Path)?.GetComponent<TrailRenderer>();
                if (trail == null) continue;
                if (state.Visible) Activate(trail.transform);
                trail.enabled = state.Visible; trail.emitting = false; trail.time = 100000f;
                trail.Clear();
                if (state.Visible && state.Positions.Length > 0) trail.AddPositions(state.Positions);
            }
            foreach (LightState state in first.Lights)
            {
                Light? light = Find(state.Path)?.GetComponent<Light>();
                if (light == null) continue;
                if (state.Enabled) Activate(light.transform);
                light.enabled = state.Enabled; light.intensity = state.Intensity; light.range = state.Range; light.color = state.Color;
            }
        }
    }
}
