using System.Collections.Generic;
using System.IO;

namespace cameraTools.Replay
{
    // Each distinct path is defined on first use. Later samples store only its index.
    internal sealed class ReplayPaths
    {
        private readonly Dictionary<string, int> _indices = new();
        private readonly List<string> _paths = new();

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
