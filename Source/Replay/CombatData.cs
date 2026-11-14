using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace cameraTools.Replay
{
    /// <summary>How a hit resolved. A hit can have several results, for example Staggered and Killed.</summary>
    [Flags]
    public enum HitResult : ushort
    {
        None = 0,
        Hit = 1,
        Blocked = 2,
        Parried = 4,
        Staggered = 8,
        Backstab = 16,
        Killed = 32,
        Critical = 64,
        Dodged = 128,
        GuardBroken = 256
    }

    /// <summary>One hit as the recording game processed it (version 10 and later).</summary>
    public struct HitEvent
    {
        public float Time;
        public long VictimUser;
        public uint VictimId;
        /// <summary>Zero when the hit had no attacker (falls, fire, drowning).</summary>
        public long AttackerUser;
        public uint AttackerId;
        public long ProjectileUser;
        public uint ProjectileId;
        public Vector3 Point;
        public Vector3 Direction;
        /// <summary>Damage per type before resistances, in <see cref="CombatData.DamageTypeNames"/> order.</summary>
        public float[] Damage;
        /// <summary>Health the victim lost from this hit.</summary>
        public float Taken;
        public HitResult Result;
        public string HitType;
        public string Skill;
    }

    /// <summary>Facts about a character that do not change during a take.</summary>
    public struct ActorInfo
    {
        public int Level;
        public float MaxHealth;
        public string Faction;
        public bool IsPlayer;
        public float Height;
        public float Radius;
    }

    internal static class CombatData
    {
        internal static readonly string[] DamageTypeNames = { "blunt", "slash", "pierce", "chop", "pickaxe", "fire", "frost", "lightning", "poison", "spirit" };

        internal static float[] Damage(HitData.DamageTypes damage) => new[]
        {
            damage.m_blunt, damage.m_slash, damage.m_pierce, damage.m_chop, damage.m_pickaxe,
            damage.m_fire, damage.m_frost, damage.m_lightning, damage.m_poison, damage.m_spirit
        };

        internal static IEnumerable<string> ResultNames(HitResult result)
        {
            foreach (HitResult flag in (HitResult[])Enum.GetValues(typeof(HitResult)))
                if (flag != HitResult.None && (result & flag) != 0) yield return Name(flag);
        }

        internal static string Name(HitResult flag) => flag switch
        {
            HitResult.GuardBroken => "guard_broken",
            _ => flag.ToString().ToLowerInvariant()
        };

        /// <summary>The body height and radius of a character prefab, read from its capsule collider.</summary>
        internal static bool Size(GameObject prefab, out float height, out float radius)
        {
            height = 0f; radius = 0f;
            CapsuleCollider? capsule = prefab.GetComponent<CapsuleCollider>();
            float scale = prefab.transform.localScale.y;
            if (capsule != null)
            {
                height = capsule.height * scale;
                radius = capsule.radius * Mathf.Max(prefab.transform.localScale.x, prefab.transform.localScale.z);
                return true;
            }
            Collider? collider = prefab.GetComponentInChildren<Collider>();
            if (collider == null) return false;
            // A prefab that is not in the scene has no world bounds; use the collider's own size.
            if (collider is BoxCollider box) { height = box.size.y * scale; radius = Mathf.Max(box.size.x, box.size.z) * 0.5f * scale; return true; }
            if (collider is SphereCollider sphere) { height = sphere.radius * 2f * scale; radius = sphere.radius * scale; return true; }
            return false;
        }

        /// <summary>Sizes of every character prefab the recording uses, keyed by prefab hash. Main thread only.</summary>
        internal static Dictionary<int, (float Height, float Radius)> Sizes(ReplayInfo info)
        {
            Dictionary<int, (float, float)> sizes = new();
            if (ZNetScene.instance == null) return sizes;
            foreach (KeyValuePair<int, string> prefab in info.Prefabs)
            {
                GameObject? source = ZNetScene.instance.GetPrefab(prefab.Value);
                if (source == null || source.GetComponent<Character>() == null) continue;
                if (Size(source, out float height, out float radius)) sizes[prefab.Key] = (height, radius);
            }
            return sizes;
        }

        internal static void Write(BinaryWriter writer, ReplayFile replay)
        {
            writer.Write(replay.SampleRate);
            writer.Write(replay.Recorder.Item1); writer.Write(replay.Recorder.Item2);
            writer.Write(replay.Actors.Count);
            foreach (KeyValuePair<(long, uint), ActorInfo> actor in replay.Actors)
            {
                writer.Write(actor.Key.Item1); writer.Write(actor.Key.Item2);
                writer.Write(actor.Value.Level); writer.Write(actor.Value.MaxHealth); writer.Write(actor.Value.Faction ?? "");
                writer.Write(actor.Value.IsPlayer); writer.Write(actor.Value.Height); writer.Write(actor.Value.Radius);
            }
            writer.Write(replay.Hits.Count);
            foreach (HitEvent hit in replay.Hits)
            {
                writer.Write(hit.Time);
                writer.Write(hit.VictimUser); writer.Write(hit.VictimId);
                writer.Write(hit.AttackerUser); writer.Write(hit.AttackerId);
                writer.Write(hit.ProjectileUser); writer.Write(hit.ProjectileId);
                ReplayMediaIO.Vector(writer, hit.Point); ReplayMediaIO.Vector(writer, hit.Direction);
                float[] damage = hit.Damage ?? Array.Empty<float>();
                writer.Write((byte)damage.Length);
                foreach (float value in damage) writer.Write(value);
                writer.Write(hit.Taken); writer.Write((ushort)hit.Result);
                writer.Write(hit.HitType ?? ""); writer.Write(hit.Skill ?? "");
            }
            writer.Write(replay.ProjectileOwners.Count);
            foreach (KeyValuePair<(long, uint), (long, uint)> owner in replay.ProjectileOwners)
            {
                writer.Write(owner.Key.Item1); writer.Write(owner.Key.Item2);
                writer.Write(owner.Value.Item1); writer.Write(owner.Value.Item2);
            }
            writer.Write(replay.AttackTargets.Count);
            foreach (KeyValuePair<int, (long, uint)> target in replay.AttackTargets)
            {
                writer.Write(target.Key); writer.Write(target.Value.Item1); writer.Write(target.Value.Item2);
            }
        }

        internal static void Read(BinaryReader reader, ReplayFile replay)
        {
            replay.SampleRate = reader.ReadSingle();
            replay.Recorder = (reader.ReadInt64(), reader.ReadUInt32());
            int count = ReplayMediaIO.Count(reader);
            for (int index = 0; index < count; index++)
            {
                (long, uint) key = (reader.ReadInt64(), reader.ReadUInt32());
                replay.Actors[key] = new ActorInfo
                {
                    Level = reader.ReadInt32(), MaxHealth = reader.ReadSingle(), Faction = reader.ReadString(),
                    IsPlayer = reader.ReadBoolean(), Height = reader.ReadSingle(), Radius = reader.ReadSingle()
                };
            }
            count = ReplayMediaIO.Count(reader);
            for (int index = 0; index < count; index++)
            {
                HitEvent hit = new()
                {
                    Time = reader.ReadSingle(),
                    VictimUser = reader.ReadInt64(), VictimId = reader.ReadUInt32(),
                    AttackerUser = reader.ReadInt64(), AttackerId = reader.ReadUInt32(),
                    ProjectileUser = reader.ReadInt64(), ProjectileId = reader.ReadUInt32(),
                    Point = ReplayMediaIO.Vector(reader), Direction = ReplayMediaIO.Vector(reader)
                };
                hit.Damage = new float[reader.ReadByte()];
                for (int value = 0; value < hit.Damage.Length; value++) hit.Damage[value] = reader.ReadSingle();
                hit.Taken = reader.ReadSingle(); hit.Result = (HitResult)reader.ReadUInt16();
                hit.HitType = reader.ReadString(); hit.Skill = reader.ReadString();
                replay.Hits.Add(hit);
            }
            count = ReplayMediaIO.Count(reader);
            for (int index = 0; index < count; index++)
                replay.ProjectileOwners[(reader.ReadInt64(), reader.ReadUInt32())] = (reader.ReadInt64(), reader.ReadUInt32());
            count = ReplayMediaIO.Count(reader);
            for (int index = 0; index < count; index++)
                replay.AttackTargets[reader.ReadInt32()] = (reader.ReadInt64(), reader.ReadUInt32());
        }
    }
}
