using System.IO;
using UnityEngine;

namespace cameraTools.Replay
{
    internal static class SceneIO
    {
        internal static void Write(BinaryWriter writer, ReplayFile replay)
        {
            writer.Write(replay.Scene.Count);
            foreach (SceneObject item in replay.Scene)
            {
                writer.Write(item.User); writer.Write(item.Id); writer.Write(item.Prefab);
                writer.Write(item.Created); writer.Write(item.Destroyed); writer.Write(item.States.Count);
                foreach (SceneState state in item.States)
                {
                    writer.Write(state.Time); ReplayMediaIO.Vector(writer, state.Position);
                    writer.Write(state.Data.Length); writer.Write(state.Data);
                }
            }
            writer.Write(replay.Terrain.Count);
            foreach (TerrainPatch patch in replay.Terrain)
            {
                writer.Write(patch.ZoneX); writer.Write(patch.ZoneY); writer.Write(patch.Width); writer.Write(patch.Scale);
                foreach (int biome in patch.CornerBiomes) writer.Write(biome);
                writer.Write(patch.Heights.Length);
                foreach (float height in patch.Heights) writer.Write(height);
                writer.Write(patch.Mask.Length);
                foreach (Color32 mask in patch.Mask) { writer.Write(mask.r); writer.Write(mask.g); writer.Write(mask.b); writer.Write(mask.a); }
            }
            writer.Write(replay.Environment.Count);
            foreach (EnvironmentSample sample in replay.Environment)
            {
                writer.Write(sample.Time); writer.Write(sample.Environment ?? ""); writer.Write(sample.DayFraction);
                writer.Write(sample.WindAngle); writer.Write(sample.WindIntensity);
            }
        }

        // A long flight can record far more objects than the media sections allow.
        private static int Count(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > 100_000_000) throw new InvalidDataException("Invalid replay scene count");
            return count;
        }

        internal static void Read(BinaryReader reader, ReplayFile replay)
        {
            int count = Count(reader);
            replay.Scene.Capacity = count;
            for (int index = 0; index < count; index++)
            {
                SceneObject item = new()
                {
                    User = reader.ReadInt64(), Id = reader.ReadUInt32(), Prefab = reader.ReadInt32(),
                    Created = reader.ReadSingle(), Destroyed = reader.ReadSingle()
                };
                int states = Count(reader);
                for (int s = 0; s < states; s++)
                {
                    SceneState state = new() { Time = reader.ReadSingle(), Position = ReplayMediaIO.Vector(reader) };
                    int length = reader.ReadInt32();
                    if (length < 0 || length > 64 * 1024 * 1024) throw new InvalidDataException("Invalid replay scene data");
                    state.Data = reader.ReadBytes(length);
                    item.States.Add(state);
                }
                replay.Scene.Add(item);
            }
            count = Count(reader);
            for (int index = 0; index < count; index++)
            {
                TerrainPatch patch = new() { ZoneX = reader.ReadInt32(), ZoneY = reader.ReadInt32(), Width = reader.ReadInt32(), Scale = reader.ReadSingle() };
                for (int corner = 0; corner < 4; corner++) patch.CornerBiomes[corner] = reader.ReadInt32();
                patch.Heights = new float[Count(reader)];
                for (int h = 0; h < patch.Heights.Length; h++) patch.Heights[h] = reader.ReadSingle();
                patch.Mask = new Color32[Count(reader)];
                for (int m = 0; m < patch.Mask.Length; m++) patch.Mask[m] = new Color32(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
                replay.Terrain.Add(patch);
            }
            count = Count(reader);
            for (int index = 0; index < count; index++)
                replay.Environment.Add(new EnvironmentSample
                {
                    Time = reader.ReadSingle(), Environment = reader.ReadString(), DayFraction = reader.ReadSingle(),
                    WindAngle = reader.ReadSingle(), WindIntensity = reader.ReadSingle()
                });
        }
    }
}
