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
                    // Equipment (version 3+)
                    w.Write(e.HasEquipment);
                    if (e.HasEquipment)
                    {
                        w.Write(e.LeftItem ?? "");
                        w.Write(e.LeftItemVariant);
                        w.Write(e.RightItem ?? "");
                        w.Write(e.ChestItem ?? "");
                        w.Write(e.LegItem ?? "");
                        w.Write(e.HelmetItem ?? "");
                        w.Write(e.ShoulderItem ?? "");
                        w.Write(e.ShoulderItemVariant);
                        w.Write(e.UtilityItem ?? "");
                        w.Write(e.TrinketItem ?? "");
                        w.Write(e.BeardItem ?? "");
                        w.Write(e.HairItem ?? "");
                        w.Write(e.LeftBackItem ?? "");
                        w.Write(e.LeftBackItemVariant);
                        w.Write(e.RightBackItem ?? "");
                    }
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

            // World events (version 2+)
            w.Write(replay.WorldEvents.Count);
            foreach (var we in replay.WorldEvents)
            {
                w.Write(we.Time);
                w.Write((byte)we.Type);
                w.Write(we.ZdoUserID);
                w.Write(we.ZdoID);
                w.Write(we.PrefabHash);
                w.Write(we.Position.x);
                w.Write(we.Position.y);
                w.Write(we.Position.z);
                w.Write(we.Rotation.x);
                w.Write(we.Rotation.y);
                w.Write(we.Rotation.z);
                w.Write(we.HealthFraction);
                w.Write(we.State);
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
                    var snapshot = new EntitySnapshot
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
                    };
                    if (version >= 3 && r.ReadBoolean())
                    {
                        snapshot.HasEquipment = true;
                        snapshot.LeftItem = r.ReadString();
                        snapshot.LeftItemVariant = r.ReadInt32();
                        snapshot.RightItem = r.ReadString();
                        snapshot.ChestItem = r.ReadString();
                        snapshot.LegItem = r.ReadString();
                        snapshot.HelmetItem = r.ReadString();
                        snapshot.ShoulderItem = r.ReadString();
                        snapshot.ShoulderItemVariant = r.ReadInt32();
                        snapshot.UtilityItem = r.ReadString();
                        snapshot.TrinketItem = r.ReadString();
                        snapshot.BeardItem = r.ReadString();
                        snapshot.HairItem = r.ReadString();
                        snapshot.LeftBackItem = r.ReadString();
                        snapshot.LeftBackItemVariant = r.ReadInt32();
                        snapshot.RightBackItem = r.ReadString();
                    }
                    frame.Entities.Add(snapshot);
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

            // World events (version 2+)
            if (version >= 2)
            {
                int worldEventCount = r.ReadInt32();
                for (int we = 0; we < worldEventCount; we++)
                {
                    replay.WorldEvents.Add(new WorldEvent
                    {
                        Time = r.ReadSingle(),
                        Type = (WorldEventType)r.ReadByte(),
                        ZdoUserID = r.ReadInt64(),
                        ZdoID = r.ReadUInt32(),
                        PrefabHash = r.ReadInt32(),
                        Position = new UnityEngine.Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                        Rotation = new UnityEngine.Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                        HealthFraction = r.ReadSingle(),
                        State = r.ReadInt32()
                    });
                }
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
