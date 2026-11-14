using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

namespace cameraTools.Replay
{
    /// <summary>A drop in an actor's synced health between two recorded samples.</summary>
    public struct DamageEvent
    {
        public float Time;
        public long User;
        public uint Id;
        public float Amount;
        public float Health;
    }

    /// <summary>An attack animation trigger: the moment an attack starts.</summary>
    public struct AttackEvent
    {
        public float Time;
        public long User;
        public uint Id;
        public string Animation;
    }

    public enum ReplayEventType { Death, AttackStart, DamageTaken }

    /// <summary>One tagged moment, timed in seconds from the start of the replay (the clock ct_replay seek uses).</summary>
    public sealed class ReplayEvent
    {
        public float Time;
        public ReplayEventType Type;
        public long User;
        public uint Id;
        public string Prefab = "";
        public string Name = "";
        public float Amount;
        public float Health;
        public string Animation = "";
        public string Actor => User + ":" + Id;
    }

    public sealed class ReplayEventList
    {
        public readonly List<ReplayEvent> Events = new();
        /// <summary>Why a type has no events in this file, for each type the file cannot provide.</summary>
        public readonly Dictionary<ReplayEventType, string> Unavailable = new();
        /// <summary>Limits of a type that is available, such as data older files did not save.</summary>
        public readonly Dictionary<ReplayEventType, string> Notes = new();
        public int FileVersion;
        public float Duration;
        public string Replay = "";
    }

    /// <summary>
    /// Builds the agent-readable event tags of a recording. The list depends only on the file (and, for files
    /// older than version 9, on which weapons and creatures are loaded), so repeated reads and seeks give the same list.
    /// </summary>
    internal static class ReplayEvents
    {
        /// <summary>How far a tag's time can be from the moment it shows in playback.</summary>
        public const float ToleranceSeconds = 0.25f;
        private static readonly string[] TypeNames = { "death", "attack_start", "damage_taken" };
        private static HashSet<string>? _attackAnimations;

        public static string Name(ReplayEventType type) => TypeNames[(int)type];

        public static bool TryParse(string text, out ReplayEventType type)
        {
            switch (text.ToLowerInvariant())
            {
                case "death": case "deaths": type = ReplayEventType.Death; return true;
                case "attack": case "attacks": case "attack_start": type = ReplayEventType.AttackStart; return true;
                case "damage": case "damage_taken": type = ReplayEventType.DamageTaken; return true;
                default: type = ReplayEventType.Death; return false;
            }
        }

        /// <summary>The attack animation names of every loaded weapon and creature attack. Main thread only.</summary>
        internal static HashSet<string> AttackAnimations()
        {
            if (_attackAnimations != null) return _attackAnimations;
            HashSet<string> names = new();
            void AddItem(GameObject? item)
            {
                ItemDrop? drop = item != null ? item.GetComponent<ItemDrop>() : null;
                if (drop == null) return;
                ItemDrop.ItemData.SharedData shared = drop.m_itemData.m_shared;
                if (shared.m_attack != null && !string.IsNullOrEmpty(shared.m_attack.m_attackAnimation)) names.Add(shared.m_attack.m_attackAnimation);
                if (shared.m_secondaryAttack != null && !string.IsNullOrEmpty(shared.m_secondaryAttack.m_attackAnimation)) names.Add(shared.m_secondaryAttack.m_attackAnimation);
            }
            void AddItems(IEnumerable<GameObject>? items) { if (items != null) foreach (GameObject item in items) AddItem(item); }
            if (ObjectDB.instance == null || ObjectDB.instance.m_items.Count == 0) return names;
            AddItems(ObjectDB.instance.m_items);
            if (ZNetScene.instance == null) return names;
            // Creature attacks are items held by the creature prefab, not all of them listed in ObjectDB.
            foreach (GameObject prefab in ZNetScene.instance.m_namedPrefabs.Values)
            {
                Humanoid? humanoid = prefab != null ? prefab.GetComponent<Humanoid>() : null;
                if (humanoid == null) continue;
                AddItems(humanoid.m_defaultItems); AddItems(humanoid.m_randomWeapon); AddItems(humanoid.m_randomShield);
                if (humanoid.m_randomSets != null) foreach (Humanoid.ItemSet set in humanoid.m_randomSets) AddItems(set.m_items);
                if (humanoid.m_randomItems != null) foreach (Humanoid.RandomItem item in humanoid.m_randomItems) AddItem(item.m_prefab);
            }
            _attackAnimations = names;
            cameraToolsPlugin.TemplateLogger.LogInfo($"[ReplayEvents] {names.Count} attack animations from loaded weapons and creatures");
            return names;
        }

        /// <summary>True when the trigger starts an attack: Attack.Start triggers the animation, or the animation plus a chain or variant number.</summary>
        internal static bool IsAttack(string trigger, HashSet<string> animations)
        {
            if (animations.Contains(trigger)) return true;
            int end = trigger.Length;
            while (end > 0 && char.IsDigit(trigger[end - 1])) end--;
            return end < trigger.Length && end > 0 && animations.Contains(trigger.Substring(0, end));
        }

        /// <summary>Builds the tags. Safe off the main thread when <paramref name="attackAnimations"/> was built beforehand.</summary>
        internal static ReplayEventList Build(ReplayFile replay, HashSet<string>? attackAnimations)
        {
            ReplayEventList list = new() { FileVersion = replay.FileVersion, Duration = replay.Duration, Replay = replay.Info.Name };
            Dictionary<(long, uint), int> prefabs = new();
            foreach (ReplayFrame frame in replay.Frames)
                foreach (EntitySnapshot entity in frame.Entities)
                    if (!prefabs.ContainsKey((entity.ZdoUserID, entity.ZdoID))) prefabs[(entity.ZdoUserID, entity.ZdoID)] = entity.PrefabHash;
            Dictionary<(long, uint), string> deathNames = new();
            foreach (DeathEvent death in replay.Deaths) deathNames[(death.User, death.Id)] = death.Name;

            ReplayEvent Event(float time, ReplayEventType type, long user, uint id)
            {
                (long, uint) key = (user, id);
                string prefab = prefabs.TryGetValue(key, out int hash) ? (replay.Info.Prefabs.TryGetValue(hash, out string name) ? name : hash.ToString()) : "";
                string readable = replay.ActorNames.TryGetValue(key, out string actorName) ? actorName :
                    deathNames.TryGetValue(key, out string death) ? death.Replace("(Clone)", "") : prefab;
                return new ReplayEvent { Time = time, Type = type, User = user, Id = id, Prefab = prefab, Name = readable };
            }

            if (replay.FileVersion < 6) list.Unavailable[ReplayEventType.Death] = $"File version {replay.FileVersion} saved no deaths; version 6 and later do";
            else
            {
                HashSet<(long, uint)> dead = new();
                foreach (DeathEvent death in replay.Deaths.OrderBy(death => death.Time))
                    if (dead.Add((death.User, death.Id))) list.Events.Add(Event(death.Time, ReplayEventType.Death, death.User, death.Id));
                if (replay.FileVersion < 9)
                    list.Notes[ReplayEventType.Death] = $"File version {replay.FileVersion} saved only deaths of characters the recording game simulated; deaths of characters another player's game simulated are missing";
            }

            if (replay.FileVersion >= 9)
                foreach (AttackEvent attack in replay.Attacks)
                {
                    ReplayEvent tag = Event(attack.Time, ReplayEventType.AttackStart, attack.User, attack.Id);
                    tag.Animation = attack.Animation;
                    list.Events.Add(tag);
                }
            else if (attackAnimations == null || attackAnimations.Count == 0)
                list.Unavailable[ReplayEventType.AttackStart] = $"File version {replay.FileVersion} saved animation triggers only; reading attacks from them needs the item data that loads with the game";
            else
            {
                foreach (TriggerEvent trigger in replay.TriggerEvents)
                {
                    if (!IsAttack(trigger.TriggerName, attackAnimations)) continue;
                    ReplayEvent tag = Event(trigger.Time, ReplayEventType.AttackStart, trigger.ZdoUserID, trigger.ZdoID);
                    tag.Animation = trigger.TriggerName;
                    list.Events.Add(tag);
                }
                list.Notes[ReplayEventType.AttackStart] = $"File version {replay.FileVersion}: attacks were read from the saved animation triggers using the weapons and creatures that are loaded now";
            }

            if (replay.FileVersion < 9) list.Unavailable[ReplayEventType.DamageTaken] = $"File version {replay.FileVersion} saved no health samples; version 9 and later do";
            else
                foreach (DamageEvent damage in replay.Damage)
                {
                    ReplayEvent tag = Event(damage.Time, ReplayEventType.DamageTaken, damage.User, damage.Id);
                    tag.Amount = damage.Amount; tag.Health = damage.Health;
                    list.Events.Add(tag);
                }

            list.Events.Sort((a, b) =>
            {
                int order = a.Time.CompareTo(b.Time);
                if (order == 0) order = a.Type.CompareTo(b.Type);
                if (order == 0) order = a.User.CompareTo(b.User);
                return order != 0 ? order : a.Id.CompareTo(b.Id);
            });
            return list;
        }

        internal static string Line(ReplayEvent tag)
        {
            StringBuilder line = new();
            line.Append("event t=").Append(tag.Time.ToString("F3", CultureInfo.InvariantCulture))
                .Append(" type=").Append(Name(tag.Type)).Append(" actor=").Append(tag.Actor)
                .Append(" prefab=").Append(tag.Prefab).Append(" name=\"").Append(tag.Name).Append('"');
            if (tag.Type == ReplayEventType.DamageTaken)
                line.Append(" amount=").Append(tag.Amount.ToString("F1", CultureInfo.InvariantCulture))
                    .Append(" health=").Append(tag.Health.ToString("F1", CultureInfo.InvariantCulture));
            if (tag.Type == ReplayEventType.AttackStart) line.Append(" animation=").Append(tag.Animation);
            return line.ToString();
        }

        internal static IEnumerable<string> Summary(ReplayEventList list)
        {
            yield return $"events replay={list.Replay} fileVersion={list.FileVersion} duration={list.Duration.ToString("F3", CultureInfo.InvariantCulture)} toleranceSeconds={ToleranceSeconds.ToString(CultureInfo.InvariantCulture)} total={list.Events.Count}";
            foreach (ReplayEventType type in new[] { ReplayEventType.Death, ReplayEventType.AttackStart, ReplayEventType.DamageTaken })
            {
                if (list.Unavailable.TryGetValue(type, out string reason)) yield return $"type {Name(type)} available=false reason=\"{reason}\"";
                else
                {
                    string note = list.Notes.TryGetValue(type, out string text) ? $" note=\"{text}\"" : "";
                    yield return $"type {Name(type)} available=true count={list.Events.Count(tag => tag.Type == type)}{note}";
                }
            }
        }

        /// <summary>The JSON file an agent reads next to a recording: &lt;name&gt;.events.json.</summary>
        internal static string JsonPath(string replayPath) => Path.ChangeExtension(replayPath, ".events.json");

        internal static void WriteJson(string path, ReplayEventList list)
        {
            Dictionary<string, object> types = new();
            foreach (ReplayEventType type in new[] { ReplayEventType.Death, ReplayEventType.AttackStart, ReplayEventType.DamageTaken })
            {
                Dictionary<string, object> entry = new() { ["available"] = !list.Unavailable.ContainsKey(type) };
                if (list.Unavailable.TryGetValue(type, out string reason)) entry["reason"] = reason;
                else entry["count"] = list.Events.Count(tag => tag.Type == type);
                if (list.Notes.TryGetValue(type, out string note)) entry["note"] = note;
                types[Name(type)] = entry;
            }
            List<Dictionary<string, object>> events = new();
            foreach (ReplayEvent tag in list.Events)
            {
                Dictionary<string, object> entry = new()
                {
                    ["t"] = System.Math.Round(tag.Time, 3), ["type"] = Name(tag.Type), ["actor"] = tag.Actor,
                    ["prefab"] = tag.Prefab, ["name"] = tag.Name
                };
                if (tag.Type == ReplayEventType.DamageTaken) { entry["amount"] = System.Math.Round(tag.Amount, 2); entry["health"] = System.Math.Round(tag.Health, 2); }
                if (tag.Type == ReplayEventType.AttackStart) entry["animation"] = tag.Animation;
                events.Add(entry);
            }
            Dictionary<string, object> document = new()
            {
                ["replay"] = list.Replay,
                ["fileVersion"] = list.FileVersion,
                ["duration"] = System.Math.Round(list.Duration, 3),
                ["timeOrigin"] = "Seconds from the start of the replay; the clock that ct_replay seek and ct_replay status use",
                ["toleranceSeconds"] = ToleranceSeconds,
                ["definitions"] = new Dictionary<string, string>
                {
                    ["death"] = "The actor died: the game ran its death (the recording game simulated the actor), or its synced health reached zero. One per actor.",
                    ["attack_start"] = "The actor started an attack: the game triggered a weapon or creature attack animation. A combo has one per swing.",
                    ["damage_taken"] = "The actor's synced health dropped between two recorded samples (30 per second), or the actor died with health left at its last sample (the killing blow). amount is the drop; health is what remains."
                },
                ["types"] = types,
                ["events"] = events
            };
            string temporary = path + ".writing";
            File.WriteAllText(temporary, JsonConvert.SerializeObject(document, Formatting.Indented));
            if (File.Exists(path)) File.Delete(path);
            File.Move(temporary, path);
        }
    }

    /// <summary>Samples actor health and names while recording, and keeps one death per actor.</summary>
    internal sealed class ReplayEventRecorder
    {
        private readonly ReplayFile _file;
        private readonly HashSet<string> _attackAnimations;
        private readonly Dictionary<(long, uint), float> _health = new();
        private readonly HashSet<(long, uint)> _dead = new();

        internal ReplayEventRecorder(ReplayFile file)
        {
            _file = file;
            _attackAnimations = ReplayEvents.AttackAnimations();
        }

        internal void Sample(Character character, long user, uint id, float time)
        {
            (long, uint) key = (user, id);
            if (!_file.ActorNames.ContainsKey(key)) _file.ActorNames[key] = character.GetHoverName();
            float health = character.GetHealth();
            bool sampled = _health.TryGetValue(key, out float previous);
            _health[key] = health;
            if (!sampled || health >= previous - 0.01f) return;
            _file.Damage.Add(new DamageEvent { Time = time, User = user, Id = id, Amount = previous - health, Health = Mathf.Max(0f, health) });
            // A character another player's game simulates dies there; this game sees its health reach zero.
            if (health <= 0f) Death(time, user, id, character.name);
        }

        internal void Death(float time, long user, uint id, string name)
        {
            if (!_dead.Add((user, id))) return;
            _file.Deaths.Add(new DeathEvent { Time = time, User = user, Id = id, Name = name });
            // A character without a death animation is removed in the frame it dies, before the next health
            // sample, so its killing blow is the health it had at its last sample.
            if (_health.TryGetValue((user, id), out float previous) && previous > 0f)
            {
                _file.Damage.Add(new DamageEvent { Time = time, User = user, Id = id, Amount = previous, Health = 0f });
                _health[(user, id)] = 0f;
            }
        }

        internal void Trigger(float time, ZDOID id, string trigger)
        {
            if (ReplayEvents.IsAttack(trigger, _attackAnimations))
                _file.Attacks.Add(new AttackEvent { Time = time, User = id.UserID, Id = id.ID, Animation = trigger });
        }
    }

    internal static class ReplayEventIO
    {
        internal static void Write(BinaryWriter writer, ReplayFile replay)
        {
            writer.Write(replay.Damage.Count);
            foreach (DamageEvent damage in replay.Damage)
            { writer.Write(damage.Time); writer.Write(damage.User); writer.Write(damage.Id); writer.Write(damage.Amount); writer.Write(damage.Health); }
            writer.Write(replay.Attacks.Count);
            foreach (AttackEvent attack in replay.Attacks)
            { writer.Write(attack.Time); writer.Write(attack.User); writer.Write(attack.Id); writer.Write(attack.Animation ?? ""); }
            writer.Write(replay.ActorNames.Count);
            foreach (KeyValuePair<(long, uint), string> name in replay.ActorNames)
            { writer.Write(name.Key.Item1); writer.Write(name.Key.Item2); writer.Write(name.Value ?? ""); }
        }

        internal static void Read(BinaryReader reader, ReplayFile replay)
        {
            int count = ReplayMediaIO.Count(reader);
            for (int index = 0; index < count; index++)
                replay.Damage.Add(new DamageEvent { Time = reader.ReadSingle(), User = reader.ReadInt64(), Id = reader.ReadUInt32(), Amount = reader.ReadSingle(), Health = reader.ReadSingle() });
            count = ReplayMediaIO.Count(reader);
            for (int index = 0; index < count; index++)
                replay.Attacks.Add(new AttackEvent { Time = reader.ReadSingle(), User = reader.ReadInt64(), Id = reader.ReadUInt32(), Animation = reader.ReadString() });
            count = ReplayMediaIO.Count(reader);
            for (int index = 0; index < count; index++)
                replay.ActorNames[(reader.ReadInt64(), reader.ReadUInt32())] = reader.ReadString();
        }
    }
}
