using System.Collections.Generic;
using System.IO;

namespace cameraTools.Replay
{
    // Each distinct path is defined on first use. Later samples store only its index.
    internal sealed class ReplayPaths
    {
        private readonly Dictionary<string, int> _indices = new();
        private readonly List<string> _paths = new();
        private readonly Dictionary<string, BonePose> _poses = new();

        internal void WritePose(BinaryWriter writer, BonePose pose)
        {
            Write(writer, pose.Path);
            bool found = _poses.TryGetValue(pose.Path, out BonePose previous);
            byte changed = 0;
            if (!found || !pose.Position.Equals(previous.Position)) changed |= 1;
            if (!found || !pose.Rotation.Equals(previous.Rotation)) changed |= 2;
            if (!found || !pose.Scale.Equals(previous.Scale)) changed |= 4;
            writer.Write(changed);
            if ((changed & 1) != 0) ReplayMediaIO.Vector(writer, pose.Position);
            if ((changed & 2) != 0) ReplayMediaIO.Rotation(writer, pose.Rotation);
            if ((changed & 4) != 0) ReplayMediaIO.Vector(writer, pose.Scale);
            _poses[pose.Path] = pose;
        }

        internal BonePose ReadPose(BinaryReader reader)
        {
            string path = Read(reader);
            bool found = _poses.TryGetValue(path, out BonePose pose);
            byte changed = reader.ReadByte();
            if ((changed & ~7) != 0 || (!found && changed != 7))
                throw new InvalidDataException("Invalid replay pose flags");
            pose.Path = path;
            if ((changed & 1) != 0) pose.Position = ReplayMediaIO.Vector(reader);
            if ((changed & 2) != 0) pose.Rotation = ReplayMediaIO.Rotation(reader);
            if ((changed & 4) != 0) pose.Scale = ReplayMediaIO.Vector(reader);
            _poses[path] = pose;
            return pose;
        }

        internal void Write(BinaryWriter writer, string path)
        {
            if (_indices.TryGetValue(path, out int index))
            {
                writer.Write(index);
                return;
            }
            index = _indices.Count;
            _indices.Add(path, index);
            writer.Write(~index);
            writer.Write(path);
        }

        internal string Read(BinaryReader reader)
        {
            int index = reader.ReadInt32();
            if (index < 0)
            {
                if (~index != _paths.Count || _paths.Count >= 1000000)
                    throw new InvalidDataException("Invalid replay path definition");
                string path = reader.ReadString();
                _paths.Add(path);
                return path;
            }
            if (index >= _paths.Count) throw new InvalidDataException("Invalid replay path index");
            return _paths[index];
        }
    }
}
