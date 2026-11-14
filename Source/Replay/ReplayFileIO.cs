using System.IO;
using System.IO.Compression;
using System.Text;
using Newtonsoft.Json;

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
            // Uncompressed header first so the replay browser can list files without decompressing them.
            using (BinaryWriter header = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                header.Write(Encoding.ASCII.GetBytes(ReplayFile.Magic));
                header.Write(ReplayFile.Version);
                byte[] info = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(replay.Info));
                header.Write(info.Length);
                header.Write(info);
                header.Write(replay.Thumbnail.Length);
                header.Write(replay.Thumbnail);
            }
            using GZipStream compressed = new GZipStream(stream, CompressionLevel.Fastest);
            using var w = new BinaryWriter(compressed, Encoding.UTF8);

            w.Write(replay.Timestamp);
            w.Write(replay.Duration);
            w.Write(replay.Frames.Count);
            w.Write(replay.TriggerEvents.Count);
            ReplayPaths paths = new ReplayPaths();

            // Frames
            foreach (var frame in replay.Frames)
            {
                w.Write(frame.Time);
                ReplayMediaIO.Vector(w, frame.CameraPosition);
                ReplayMediaIO.Rotation(w, frame.CameraRotation);
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
                            paths.WritePose(w, bone);
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
            ReplayMediaIO.Write(w, replay, paths);
            SceneIO.Write(w, replay);
            ReplayEventIO.Write(w, replay);
        }

        public static ReplayFile Read(string path)
        {
            using var stream = File.OpenRead(path);
            if (IsLegacy(stream))
            {
                // Versions 1-4 were plain files and versions 5-6 one gzip stream, both without a header.
                using Stream legacy = IsGzip(stream) ? new GZipStream(stream, CompressionMode.Decompress) : stream;
                using BinaryReader legacyReader = new BinaryReader(legacy, Encoding.UTF8);
                ReadMagic(legacyReader);
                int legacyVersion = legacyReader.ReadInt32();
                ReplayFile old = new ReplayFile { FileVersion = legacyVersion, Info = { Name = Path.GetFileNameWithoutExtension(path) } };
                ReadBody(legacyReader, legacyVersion, old);
                old.Info.Duration = old.Duration;
                return old;
            }
            ReplayFile replay = new ReplayFile();
            int version = ReadHeader(stream, replay);
            replay.FileVersion = version;
            using GZipStream compressed = new GZipStream(stream, CompressionMode.Decompress);
            using BinaryReader reader = new BinaryReader(compressed, Encoding.UTF8);
            ReadBody(reader, version, replay);
            return replay;
        }

        /// <summary>Reads only the description and preview image.</summary>
        public static ReplayFile ReadInfo(string path)
        {
            using var stream = File.OpenRead(path);
            ReplayFile replay = new ReplayFile { Info = { Name = Path.GetFileNameWithoutExtension(path) } };
            if (IsLegacy(stream))
            {
                using Stream legacy = IsGzip(stream) ? new GZipStream(stream, CompressionMode.Decompress) : stream;
                using BinaryReader reader = new BinaryReader(legacy, Encoding.UTF8);
                ReadMagic(reader);
                reader.ReadInt32();
                replay.Timestamp = reader.ReadInt64();
                replay.Info.CreatedUnix = replay.Timestamp;
                replay.Info.Duration = reader.ReadSingle();
                return replay;
            }
            ReadHeader(stream, replay);
            return replay;
        }

        private static bool IsGzip(Stream stream)
        {
            bool gzip = stream.ReadByte() == 0x1f && stream.ReadByte() == 0x8b;
            stream.Position = 0;
            return gzip;
        }

        private static bool IsLegacy(Stream stream)
        {
            if (IsGzip(stream)) return true;
            byte[] start = new byte[8];
            int read = stream.Read(start, 0, start.Length);
            stream.Position = 0;
            return read < start.Length || System.BitConverter.ToInt32(start, 4) < 7;
        }

        private static void ReadMagic(BinaryReader r)
        {
            string magic = Encoding.ASCII.GetString(r.ReadBytes(4));
            if (magic != ReplayFile.Magic)
                throw new InvalidDataException($"Invalid replay file magic: {magic}");
        }

        private static int ReadHeader(Stream stream, ReplayFile replay)
        {
            using BinaryReader header = new BinaryReader(stream, Encoding.UTF8, true);
            ReadMagic(header);
            int version = header.ReadInt32();
            if (version > ReplayFile.Version)
                throw new InvalidDataException($"Replay version {version} needs a newer cameraTools");
            int infoLength = header.ReadInt32();
            if (infoLength < 0 || infoLength > 64 * 1024 * 1024) throw new InvalidDataException("Invalid replay header");
            replay.Info = JsonConvert.DeserializeObject<ReplayInfo>(Encoding.UTF8.GetString(header.ReadBytes(infoLength))) ?? new ReplayInfo();
            int thumbnailLength = header.ReadInt32();
            if (thumbnailLength < 0 || thumbnailLength > 16 * 1024 * 1024) throw new InvalidDataException("Invalid replay preview");
            replay.Thumbnail = header.ReadBytes(thumbnailLength);
            return version;
        }

        private static void ReadBody(BinaryReader r, int version, ReplayFile replay)
        {
            replay.Timestamp = r.ReadInt64();
            replay.Duration = r.ReadSingle();
            int frameCount = r.ReadInt32();
            int triggerCount = r.ReadInt32();
            ReplayPaths? paths = version >= 8 ? new ReplayPaths() : null;

            // Frames
            for (int f = 0; f < frameCount; f++)
            {
                var frame = new ReplayFrame { Time = r.ReadSingle() };
                if (version >= 7)
                {
                    frame.CameraPosition = ReplayMediaIO.Vector(r);
                    frame.CameraRotation = ReplayMediaIO.Rotation(r);
                }
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
                            snapshot.Pose.Add(paths != null ? paths.ReadPose(r) : new BonePose
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

            if (version >= 6) ReplayMediaIO.Read(r, replay, paths);
            if (version >= 7) SceneIO.Read(r, replay);
            if (version >= 9) ReplayEventIO.Read(r, replay);
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
