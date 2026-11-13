using System.Collections.Generic;
using UnityEngine;

namespace cameraTools.Replay
{
    public sealed class VisualState
    {
        public Vector3 Scale = Vector3.one;
        public List<BonePose> Nodes = new();
        public List<ParticleState> Particles = new();
        public List<TrailState> Trails = new();
        public List<LightState> Lights = new();
    }

    public sealed class ParticleState
    {
        public string Path = "";
        public float Time;
        public bool Visible;
        public ParticleSystem.Particle[] Particles = System.Array.Empty<ParticleSystem.Particle>();
        public ParticleSystem.Trails? Trails;
    }

    public sealed class TrailState
    {
        public string Path = "";
        public bool Visible;
        public Vector3[] Positions = System.Array.Empty<Vector3>();
    }

    public struct LightState
    {
        public string Path;
        public bool Enabled;
        public float Intensity;
        public float Range;
        public Color Color;
    }

    public sealed class EffectTrack
    {
        public string Prefab = "";
        public float Start;
        public float End;
        public List<EffectFrame> Frames = new();
    }

    public sealed class EffectFrame
    {
        public float Time;
        public Vector3 Position;
        public Quaternion Rotation;
        public VisualState Visual = new();
    }

    public sealed class SoundTrack
    {
        public string Clip = "";
        public string Mixer = "";
        public int Frequency;
        public int Samples;
        public float Start;
        public float End;
        public bool Loop;
        public float SpatialBlend;
        public float MinDistance;
        public float MaxDistance;
        public int Rolloff;
        public List<SoundFrame> Frames = new();
    }

    public struct SoundFrame
    {
        public float Time;
        public Vector3 Position;
        public float Volume;
        public float Pitch;
        public float Pan;
        public int Sample;
    }

    public struct DeathEvent
    {
        public float Time;
        public long User;
        public uint Id;
        public string Name;
    }
}
