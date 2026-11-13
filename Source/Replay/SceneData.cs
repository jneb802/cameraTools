using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

namespace cameraTools.Replay
{
    // Everything a replay needs to show without the original world: the recording's world
    // objects (as Valheim ZDO data), the base terrain of each loaded zone, and the weather.

    /// <summary>One networked world object, such as a building piece, tree, rock or terrain edit.</summary>
    public sealed class SceneObject
    {
        public long User;
        public uint Id;
        public int Prefab;
        /// <summary>Seconds into the recording when the object appeared; 0 when it was already there.</summary>
        public float Created;
        /// <summary>Seconds into the recording when the object was destroyed; infinity when it survived.</summary>
        public float Destroyed = float.PositiveInfinity;
        public List<SceneState> States = new();
    }

    /// <summary>The object's full ZDO data at one time, in Valheim's network format (ZDO.Serialize).</summary>
    public sealed class SceneState
    {
        public float Time;
        public Vector3 Position;
        public byte[] Data = System.Array.Empty<byte>();
    }

    /// <summary>Base terrain of one 64 m zone before terrain edits, so playback needs no world generator.</summary>
    public sealed class TerrainPatch
    {
        public int ZoneX;
        public int ZoneY;
        public int Width;
        public float Scale;
        public float[] Heights = System.Array.Empty<float>();
        public Color32[] Mask = System.Array.Empty<Color32>();
        public int[] CornerBiomes = new int[4];
    }

    public struct EnvironmentSample
    {
        public float Time;
        public string Environment;
        public float DayFraction;
        public float WindAngle;
        public float WindIntensity;
    }

    public sealed class ReplayMod
    {
        public string Guid = "";
        public string Name = "";
        public string Version = "";
    }

    /// <summary>Small description stored uncompressed at the front of the file, read by the replay browser.</summary>
    public sealed class ReplayInfo
    {
        public string Name = "";
        public long CreatedUnix;
        public float Duration;
        public string GameVersion = "";
        public string WorldName = "";
        public string SeedName = "";
        public int Seed;
        public int WorldGenVersion;
        public float[] StartCamera = new float[7];
        public int Zones;
        public int SceneObjects;
        public int Actors;
        public List<ReplayMod> Mods = new();
        /// <summary>Prefab hash to name, for every prefab the recording uses, so playback can name what it cannot load.</summary>
        public Dictionary<int, string> Prefabs = new();

        [JsonIgnore] public Vector3 StartPosition => new(StartCamera[0], StartCamera[1], StartCamera[2]);
        [JsonIgnore] public Quaternion StartRotation => new(StartCamera[3], StartCamera[4], StartCamera[5], StartCamera[6]);
        [JsonIgnore] public bool CanLoadAlone => Zones > 0;

        /// <summary>The loaded prefab's name, or the name saved in the recording when its mod is not loaded.</summary>
        public string PrefabName(int hash)
        {
            GameObject? prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(hash) : null;
            if (prefab != null) return prefab.name;
            return Prefabs.TryGetValue(hash, out string name) ? name : hash.ToString();
        }
    }
}
