using System.IO;
using System.IO.Compression;
using System.Text;

namespace cameraTools.Replay
{
    public static class ReplayFileIO
    {
        public static void Write(string path, ReplayFile replay)
        {
            string temporary = path + ".saving-" + System.Guid.NewGuid().ToString("N");
            try
            {
                WriteContents(temporary, replay);
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private static void WriteContents(string path, ReplayFile replay)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            using var stream = File.Create(path);
            using GZipStream compressed = new GZipStream(stream, CompressionLevel.Fastest);
            using var w = new BinaryWriter(compressed, Encoding.UTF8);

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
                    w.Write(e.IsProjectile);
                    w.Write(e.Pose?.Count ?? 0);
                    if (e.Pose != null)
                    {
                        foreach (BonePose bone in e.Pose)
                        {
                            w.Write(bone.Path);
                            w.Write(bone.Position.x); w.Write(bone.Position.y); w.Write(bone.Position.z);
                            w.Write(bone.Rotation.x); w.Write(bone.Rotation.y); w.Write(bone.Rotation.z); w.Write(bone.Rotation.w);
                            w.Write(bone.Scale.x); w.Write(bone.Scale.y); w.Write(bone.Scale.z);
                        }
                    }
                    // Equipment (version 3+; item hashes as int since version 4)
                    w.Write(e.HasEquipment);
                    if (e.HasEquipment)
                    {
                        w.Write(e.LeftItem);
                        w.Write(e.LeftItemVariant);
                        w.Write(e.RightItem);
                        w.Write(e.ChestItem);
                        w.Write(e.LegItem);
                        w.Write(e.HelmetItem);
                        w.Write(e.ShoulderItem);
                        w.Write(e.ShoulderItemVariant);
                        w.Write(e.UtilityItem);
                        w.Write(e.TrinketItem ?? "");
                        w.Write(e.BeardItem);
                        w.Write(e.HairItem);
                        w.Write(e.LeftBackItem);
                        w.Write(e.LeftBackItemVariant);
                        w.Write(e.RightBackItem);
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
            ReplayMediaIO.Write(w, replay);
        }

        public static ReplayFile Read(string path)
        {
            using var stream = File.OpenRead(path);
            bool compressed = stream.ReadByte() == 0x1f && stream.ReadByte() == 0x8b;
            stream.Position = 0;
            using Stream payload = compressed ? new GZipStream(stream, CompressionMode.Decompress) : stream;
            using var r = new BinaryReader(payload, Encoding.UTF8);

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
                    if (version >= 5)
                    {
                        snapshot.IsProjectile = r.ReadBoolean();
                        int boneCount = r.ReadInt32();
                        if (boneCount < 0 || boneCount > 4096)
                            throw new InvalidDataException("Invalid pose count");
                        snapshot.Pose = new System.Collections.Generic.List<BonePose>(boneCount);
                        for (int bone = 0; bone < boneCount; bone++)
                            snapshot.Pose.Add(new BonePose
                            {
                                Path = r.ReadString(),
                                Position = new UnityEngine.Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                                Rotation = new UnityEngine.Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                                Scale = new UnityEngine.Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle())
                            });
                    }
                    if (version >= 3 && r.ReadBoolean())
                    {
                        snapshot.HasEquipment = true;
                        snapshot.LeftItem = ReadItemHash(r, version);
                        snapshot.LeftItemVariant = r.ReadInt32();
                        snapshot.RightItem = ReadItemHash(r, version);
                        snapshot.ChestItem = ReadItemHash(r, version);
                        snapshot.LegItem = ReadItemHash(r, version);
                        snapshot.HelmetItem = ReadItemHash(r, version);
                        snapshot.ShoulderItem = ReadItemHash(r, version);
                        snapshot.ShoulderItemVariant = r.ReadInt32();
                        snapshot.UtilityItem = ReadItemHash(r, version);
                        snapshot.TrinketItem = r.ReadString();
                        snapshot.BeardItem = ReadItemHash(r, version);
                        snapshot.HairItem = ReadItemHash(r, version);
                        snapshot.LeftBackItem = ReadItemHash(r, version);
                        snapshot.LeftBackItemVariant = r.ReadInt32();
                        snapshot.RightBackItem = ReadItemHash(r, version);
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

            if (version >= 6) ReplayMediaIO.Read(r, replay);
            return replay;
        }

        public static string GetReplayDirectory()
        {
            var dir = Path.Combine(BepInEx.Paths.ConfigPath, "cameraTools", "replays");
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            return dir;
        }

        // Version 3 files stored item prefab names; version 4+ store the int prefab hash
        // that VisEquipment uses directly. Empty names map to 0 (no item).
        private static int ReadItemHash(BinaryReader r, int version)
        {
            if (version >= 4)
                return r.ReadInt32();
            string name = r.ReadString();
            return string.IsNullOrEmpty(name) ? 0 : name.GetStableHashCode();
        }
    }
}
