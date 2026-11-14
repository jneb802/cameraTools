using System.Collections.Generic;
using UnityEngine;

namespace cameraTools.Replay
{
    public struct EntitySnapshot
    {
        public bool IsProjectile;
        public List<BonePose>? Pose;
        public byte Kind; // 0 character, 1 projectile, 2 ragdoll, 3 dropped item
        public VisualState? Visual;
        // Identity (12 bytes wire: long userID + uint id)
        public long ZdoUserID;
        public uint ZdoID;
        public int PrefabHash;

        // Transform
        public Vector3 Position;
        public Quaternion Rotation;

        // Animator floats
        public float ForwardSpeed;
        public float SidewaySpeed;
        public float TurnSpeed;

        // Animator bools packed into one byte
        // bit 0: inWater, bit 1: onGround, bit 2: encumbered, bit 3: flying
        // bit 4: falling, bit 5: crouching, bit 6: blocking
        public byte AnimBools;

        // Animator ints (weapon animation state)
        public int StateF;
        public int StateI;

        // Equipment visuals (only populated for entities with VisEquipment).
        // Item fields are Valheim item prefab hashes (VisEquipment stores int hashes since 1.0).
        public bool HasEquipment;
        public int LeftItem;
        public int LeftItemVariant;
        public int RightItem;
        public int ChestItem;
        public int LegItem;
        public int HelmetItem;
        public int ShoulderItem;
        public int ShoulderItemVariant;
        public int UtilityItem;
        public string TrinketItem; // still a prefab name in Valheim 1.0
        public int BeardItem;
        public int HairItem;
        public int LeftBackItem;
        public int LeftBackItemVariant;
        public int RightBackItem;

        public bool GetBool(int bit) => (AnimBools & (1 << bit)) != 0;

        public static byte PackBools(bool inWater, bool onGround, bool encumbered,
            bool flying, bool falling, bool crouching, bool blocking)
        {
            byte b = 0;
            if (inWater) b |= 1 << 0;
            if (onGround) b |= 1 << 1;
            if (encumbered) b |= 1 << 2;
            if (flying) b |= 1 << 3;
            if (falling) b |= 1 << 4;
            if (crouching) b |= 1 << 5;
            if (blocking) b |= 1 << 6;
            return b;
        }
    }

    public struct BonePose
    {
        public string Path;
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Scale;
    }

    public struct TriggerEvent
    {
        public float Time;
        public long ZdoUserID;
        public uint ZdoID;
        public string TriggerName;
    }

    public enum WorldEventType : byte
    {
        Created = 0,
        Destroyed = 1,
        StateChanged = 2
    }

    public struct WorldEvent
    {
        public float Time;
        public WorldEventType Type;
        public long ZdoUserID;
        public uint ZdoID;
        public int PrefabHash;
        public Vector3 Position;
        public Vector3 Rotation; // euler angles
        public float HealthFraction; // 0-1
        public int State; // door state, etc.
    }

    public class ReplayFrame
    {
        public float Time;
        public Vector3 CameraPosition;
        public Quaternion CameraRotation = Quaternion.identity;
        public List<EntitySnapshot> Entities = new List<EntitySnapshot>();
    }

    public class ReplayFile
    {
        public const string Magic = "VRPL";
        public const int Version = 10;

        /// <summary>The format version the file was saved with; older files lack some data.</summary>
        public int FileVersion = Version;
        /// <summary>The file this recording was read from; null for a take that has not been saved.</summary>
        public string? SourcePath;
        public ReplayInfo Info = new();
        public byte[] Thumbnail = System.Array.Empty<byte>();
        public long Timestamp;
        public float Duration;
        public List<ReplayFrame> Frames = new List<ReplayFrame>();
        public List<TriggerEvent> TriggerEvents = new List<TriggerEvent>();
        public List<WorldEvent> WorldEvents = new List<WorldEvent>();
        public List<EffectTrack> Effects = new();
        public List<SoundTrack> Sounds = new();
        public List<DeathEvent> Deaths = new();
        public List<DamageEvent> Damage = new();
        public List<AttackEvent> Attacks = new();
        /// <summary>Readable actor names (player names, localized creature names), saved since version 9.</summary>
        public Dictionary<(long, uint), string> ActorNames = new();
        public List<SceneObject> Scene = new();
        public List<TerrainPatch> Terrain = new();
        public List<EnvironmentSample> Environment = new();
        /// <summary>Samples per second the take was recorded at; 30 for files before version 10.</summary>
        public float SampleRate = 30f;
        /// <summary>The recording player's character, or (0, 0) when unknown (files before version 10).</summary>
        public (long, uint) Recorder;
        /// <summary>Level, health, faction and size of each recorded character (version 10 and later).</summary>
        public Dictionary<(long, uint), ActorInfo> Actors = new();
        /// <summary>Every hit the recording game processed (version 10 and later).</summary>
        public List<HitEvent> Hits = new();
        public Dictionary<(long, uint), (long, uint)> ProjectileOwners = new();
        /// <summary>The creature AI's target when it started an attack, keyed by index into <see cref="Attacks"/>.</summary>
        public Dictionary<int, (long, uint)> AttackTargets = new();
        public bool HasScene => Scene.Count > 0;
    }
}
