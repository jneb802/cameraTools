using System.IO;
using System.Text;

namespace cameraTools.Replay
{
    public static class ReplayFileIO
    {
        public static void Write(string path, ReplayFile replay)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            using var stream = File.Create(path);
            using var w = new BinaryWriter(stream, Encoding.UTF8);

            // Header
            w.Write(Encoding.ASCII.GetBytes(ReplayFile.Magic)); // 4 bytes
            w.Write(ReplayFile.Version);
            w.Write(replay.Timestamp);
            w.Write(replay.Duration);
            w.Write(replay.Frames.Count);
            w.Write(replay.TriggerEvents.Count);

            // Frames
            foreach (var frame in replay.Frames)
            {
                w.Write(frame.Time);
                w.Write(frame.Entities.Count);
                foreach (var e in frame.Entities)
                {
                    // ZDOID (12 bytes)
                    w.Write(e.ZdoUserID);
                    w.Write(e.ZdoID);
                    // Prefab hash
                    w.Write(e.PrefabHash);
                    // Position (12 bytes)
                    w.Write(e.Position.x);
                    w.Write(e.Position.y);
                    w.Write(e.Position.z);
                    // Rotation (16 bytes)
                    w.Write(e.Rotation.x);
                    w.Write(e.Rotation.y);
                    w.Write(e.Rotation.z);
                    w.Write(e.Rotation.w);
                    // Anim floats (12 bytes)
                    w.Write(e.ForwardSpeed);
                    w.Write(e.SidewaySpeed);
                    w.Write(e.TurnSpeed);
                    // Anim bools (1 byte)
                    w.Write(e.AnimBools);
                    // State ints (8 bytes)
                    w.Write(e.StateF);
                    w.Write(e.StateI);
                }
            }

            // Trigger events
            foreach (var t in replay.TriggerEvents)
            {
                w.Write(t.Time);
                w.Write(t.ZdoUserID);
                w.Write(t.ZdoID);
                w.Write(t.TriggerName);
            }
        }

        public static ReplayFile Read(string path)
        {
            using var stream = File.OpenRead(path);
            using var r = new BinaryReader(stream, Encoding.UTF8);

            // Header
            var magic = Encoding.ASCII.GetString(r.ReadBytes(4));
            if (magic != ReplayFile.Magic)
                throw new InvalidDataException($"Invalid replay file magic: {magic}");

            var version = r.ReadInt32();
            if (version > ReplayFile.Version)
                throw new InvalidDataException($"Unsupported replay version: {version}");

            var replay = new ReplayFile
            {
                Timestamp = r.ReadInt64(),
                Duration = r.ReadSingle()
            };
            int frameCount = r.ReadInt32();
            int triggerCount = r.ReadInt32();

            // Frames
            for (int f = 0; f < frameCount; f++)
            {
                var frame = new ReplayFrame { Time = r.ReadSingle() };
                int entityCount = r.ReadInt32();
                for (int e = 0; e < entityCount; e++)
                {
                    frame.Entities.Add(new EntitySnapshot
                    {
                        ZdoUserID = r.ReadInt64(),
                        ZdoID = r.ReadUInt32(),
                        PrefabHash = r.ReadInt32(),
                        Position = new UnityEngine.Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                        Rotation = new UnityEngine.Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                        ForwardSpeed = r.ReadSingle(),
                        SidewaySpeed = r.ReadSingle(),
                        TurnSpeed = r.ReadSingle(),
                        AnimBools = r.ReadByte(),
                        StateF = r.ReadInt32(),
                        StateI = r.ReadInt32()
                    });
                }
                replay.Frames.Add(frame);
            }

            // Trigger events
            for (int t = 0; t < triggerCount; t++)
            {
                replay.TriggerEvents.Add(new TriggerEvent
                {
                    Time = r.ReadSingle(),
                    ZdoUserID = r.ReadInt64(),
                    ZdoID = r.ReadUInt32(),
                    TriggerName = r.ReadString()
                });
            }

            return replay;
        }

        public static string GetReplayDirectory()
        {
            var dir = Path.Combine(BepInEx.Paths.ConfigPath, "cameraTools", "replays");
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
