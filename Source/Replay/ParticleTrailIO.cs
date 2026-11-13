using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace cameraTools.Replay
{
    // Unity exposes GetTrails/SetTrails, but keeps the trail buffer fields internal.
    internal static class ParticleTrailIO
    {
        private static readonly FieldInfo Positions = Field("positions");
        private static readonly FieldInfo Front = Field("frontPositions");
        private static readonly FieldInfo Back = Field("backPositions");
        private static readonly FieldInfo Counts = Field("positionCounts");
        private static readonly FieldInfo Offsets = Field("textureOffsets");
        private static readonly FieldInfo MaxTrails = Field("maxTrailCount");
        private static readonly FieldInfo MaxPositions = Field("maxPositionsPerTrailCount");
        private static FieldInfo Field(string name) => typeof(ParticleSystem.Trails).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new System.NotSupportedException("Unity particle trail field missing: " + name);

        internal static long Size(ParticleSystem.Trails trails) => ((List<Vector4>)Positions.GetValue(trails)).Count * 16L +
            ((List<int>)Counts.GetValue(trails)).Count * 16L + 64;
        internal static void Write(BinaryWriter writer, ParticleSystem.Trails trails)
        {
            object data = trails;
            List<Vector4> positions = (List<Vector4>)Positions.GetValue(data);
            writer.Write(positions.Count);
            foreach (Vector4 point in positions) { writer.Write(point.x); writer.Write(point.y); writer.Write(point.z); writer.Write(point.w); }
            foreach (FieldInfo field in new[] { Front, Back, Counts })
            { List<int> values = (List<int>)field.GetValue(data); writer.Write(values.Count); foreach (int value in values) writer.Write(value); }
            List<float> offsets = (List<float>)Offsets.GetValue(data);
            writer.Write(offsets.Count); foreach (float offset in offsets) writer.Write(offset);
            writer.Write((int)MaxTrails.GetValue(data)); writer.Write((int)MaxPositions.GetValue(data));
        }
        internal static ParticleSystem.Trails Read(BinaryReader reader)
        {
            object data = default(ParticleSystem.Trails);
            int count = ReplayMediaIO.Count(reader);
            List<Vector4> positions = new(count);
            for (int i = 0; i < count; i++) positions.Add(new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()));
            Positions.SetValue(data, positions);
            foreach (FieldInfo field in new[] { Front, Back, Counts })
            {
                count = ReplayMediaIO.Count(reader); List<int> values = new(count);
                for (int i = 0; i < count; i++) values.Add(reader.ReadInt32());
                field.SetValue(data, values);
            }
            count = ReplayMediaIO.Count(reader); List<float> offsets = new(count);
            for (int i = 0; i < count; i++) offsets.Add(reader.ReadSingle());
            Offsets.SetValue(data, offsets); MaxTrails.SetValue(data, reader.ReadInt32()); MaxPositions.SetValue(data, reader.ReadInt32());
            return (ParticleSystem.Trails)data;
        }
    }
}
