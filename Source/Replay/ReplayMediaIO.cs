using System;
using System.IO;
using UnityEngine;

namespace cameraTools.Replay
{
    internal static class ReplayMediaIO
    {
        internal static void Write(BinaryWriter writer, ReplayFile replay, ReplayPaths paths)
        {
            foreach (ReplayFrame frame in replay.Frames)
                foreach (EntitySnapshot entity in frame.Entities)
                {
                    writer.Write(entity.Kind);
                    writer.Write(entity.Visual != null);
                    if (entity.Visual != null) WriteVisual(writer, entity.Visual, paths);
                }
            writer.Write(replay.Effects.Count);
            foreach (EffectTrack track in replay.Effects)
            {
                writer.Write(track.Prefab); writer.Write(track.Start); writer.Write(track.End); writer.Write(track.Frames.Count);
                foreach (EffectFrame frame in track.Frames)
                {
                    writer.Write(frame.Time); Vector(writer, frame.Position); Rotation(writer, frame.Rotation); WriteVisual(writer, frame.Visual, paths);
                }
            }
            writer.Write(replay.Sounds.Count);
            foreach (SoundTrack track in replay.Sounds)
            {
                writer.Write(track.Clip); writer.Write(track.Mixer); writer.Write(track.Frequency); writer.Write(track.Samples);
                writer.Write(track.Start); writer.Write(track.End); writer.Write(track.Loop); writer.Write(track.SpatialBlend);
                writer.Write(track.MinDistance); writer.Write(track.MaxDistance); writer.Write(track.Rolloff); writer.Write(track.Frames.Count);
                foreach (SoundFrame frame in track.Frames)
                {
                    writer.Write(frame.Time); Vector(writer, frame.Position); writer.Write(frame.Volume); writer.Write(frame.Pitch); writer.Write(frame.Pan); writer.Write(frame.Sample);
                }
            }
            writer.Write(replay.Deaths.Count);
            foreach (DeathEvent death in replay.Deaths)
            { writer.Write(death.Time); writer.Write(death.User); writer.Write(death.Id); writer.Write(death.Name); }
        }

        internal static void Read(BinaryReader reader, ReplayFile replay, ReplayPaths? paths)
        {
            foreach (ReplayFrame frame in replay.Frames)
                for (int index = 0; index < frame.Entities.Count; index++)
                {
                    EntitySnapshot entity = frame.Entities[index];
                    entity.Kind = reader.ReadByte();
                    if (reader.ReadBoolean()) entity.Visual = ReadVisual(reader, paths);
                    frame.Entities[index] = entity;
                }
            int count = Count(reader);
            for (int index = 0; index < count; index++)
            {
                EffectTrack track = new() { Prefab = reader.ReadString(), Start = reader.ReadSingle(), End = reader.ReadSingle() };
                int frames = Count(reader);
                for (int f = 0; f < frames; f++) track.Frames.Add(new EffectFrame { Time = reader.ReadSingle(), Position = Vector(reader), Rotation = Rotation(reader), Visual = ReadVisual(reader, paths) });
                replay.Effects.Add(track);
            }
            count = Count(reader);
            for (int index = 0; index < count; index++)
            {
                SoundTrack track = new()
                {
                    Clip = reader.ReadString(), Mixer = reader.ReadString(), Frequency = reader.ReadInt32(), Samples = reader.ReadInt32(),
                    Start = reader.ReadSingle(), End = reader.ReadSingle(), Loop = reader.ReadBoolean(), SpatialBlend = reader.ReadSingle(),
                    MinDistance = reader.ReadSingle(), MaxDistance = reader.ReadSingle(), Rolloff = reader.ReadInt32()
                };
                int frames = Count(reader);
                for (int f = 0; f < frames; f++) track.Frames.Add(new SoundFrame { Time = reader.ReadSingle(), Position = Vector(reader), Volume = reader.ReadSingle(), Pitch = reader.ReadSingle(), Pan = reader.ReadSingle(), Sample = reader.ReadInt32() });
                replay.Sounds.Add(track);
            }
            count = Count(reader);
            for (int index = 0; index < count; index++) replay.Deaths.Add(new DeathEvent { Time = reader.ReadSingle(), User = reader.ReadInt64(), Id = reader.ReadUInt32(), Name = reader.ReadString() });
        }

        private static void WriteVisual(BinaryWriter writer, VisualState visual, ReplayPaths paths)
        {
            Vector(writer, visual.Scale); writer.Write(visual.Nodes.Count);
            foreach (BonePose pose in visual.Nodes) paths.WritePose(writer, pose);
            writer.Write(visual.Particles.Count);
            foreach (ParticleState state in visual.Particles)
            {
                paths.Write(writer, state.Path); writer.Write(state.Time); writer.Write(state.Visible); writer.Write(state.Particles.Length);
                foreach (ParticleSystem.Particle particle in state.Particles)
                {
                    Vector(writer, particle.position); Vector(writer, particle.velocity); Vector(writer, particle.rotation3D); Vector(writer, particle.angularVelocity3D);
                    Vector(writer, particle.startSize3D); Vector(writer, particle.axisOfRotation);
                    writer.Write(particle.startLifetime); writer.Write(particle.remainingLifetime); writer.Write(particle.randomSeed);
                    Color32 color = particle.startColor; writer.Write(color.r); writer.Write(color.g); writer.Write(color.b); writer.Write(color.a);
                }
                writer.Write(state.Trails.HasValue);
                if (state.Trails.HasValue) ParticleTrailIO.Write(writer, state.Trails.Value);
            }
            writer.Write(visual.Trails.Count);
            foreach (TrailState trail in visual.Trails)
            { paths.Write(writer, trail.Path); writer.Write(trail.Visible); writer.Write(trail.Positions.Length); foreach (Vector3 position in trail.Positions) Vector(writer, position); }
            writer.Write(visual.Lights.Count);
            foreach (LightState light in visual.Lights)
            { paths.Write(writer, light.Path); writer.Write(light.Enabled); writer.Write(light.Intensity); writer.Write(light.Range); writer.Write(light.Color.r); writer.Write(light.Color.g); writer.Write(light.Color.b); writer.Write(light.Color.a); }
        }

        private static VisualState ReadVisual(BinaryReader reader, ReplayPaths? paths)
        {
            VisualState visual = new() { Scale = Vector(reader) };
            int count = Count(reader);
            for (int i = 0; i < count; i++) visual.Nodes.Add(paths != null ? paths.ReadPose(reader) : new BonePose { Path = reader.ReadString(), Position = Vector(reader), Rotation = Rotation(reader), Scale = Vector(reader) });
            count = Count(reader);
            for (int i = 0; i < count; i++)
            {
                ParticleState state = new() { Path = paths != null ? paths.Read(reader) : reader.ReadString(), Time = reader.ReadSingle(), Visible = reader.ReadBoolean(), Particles = new ParticleSystem.Particle[Count(reader)] };
                for (int p = 0; p < state.Particles.Length; p++) state.Particles[p] = new ParticleSystem.Particle
                {
                    position = Vector(reader), velocity = Vector(reader), rotation3D = Vector(reader), angularVelocity3D = Vector(reader),
                    startSize3D = Vector(reader), axisOfRotation = Vector(reader), startLifetime = reader.ReadSingle(), remainingLifetime = reader.ReadSingle(), randomSeed = reader.ReadUInt32(),
                    startColor = new Color32(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte())
                };
                if (reader.ReadBoolean()) state.Trails = ParticleTrailIO.Read(reader);
                visual.Particles.Add(state);
            }
            count = Count(reader);
            for (int i = 0; i < count; i++)
            {
                TrailState trail = new() { Path = paths != null ? paths.Read(reader) : reader.ReadString(), Visible = reader.ReadBoolean(), Positions = new Vector3[Count(reader)] };
                for (int p = 0; p < trail.Positions.Length; p++) trail.Positions[p] = Vector(reader);
                visual.Trails.Add(trail);
            }
            count = Count(reader);
            for (int i = 0; i < count; i++) visual.Lights.Add(new LightState { Path = paths != null ? paths.Read(reader) : reader.ReadString(), Enabled = reader.ReadBoolean(), Intensity = reader.ReadSingle(), Range = reader.ReadSingle(), Color = new Color(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()) });
            return visual;
        }

        internal static int Count(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > 1000000) throw new InvalidDataException("Invalid replay media count");
            return count;
        }
        internal static void Vector(BinaryWriter writer, Vector3 value) { writer.Write(value.x); writer.Write(value.y); writer.Write(value.z); }
        internal static Vector3 Vector(BinaryReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        internal static void Rotation(BinaryWriter writer, Quaternion value) { writer.Write(value.x); writer.Write(value.y); writer.Write(value.z); writer.Write(value.w); }
        internal static Quaternion Rotation(BinaryReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }
}
