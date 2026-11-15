using System.Collections.Generic;
using UnityEngine;

namespace cameraTools.Replay
{
    /// <summary>One recorded entity's samples in time order, read from the replay frames.</summary>
    public sealed class ActorTrack
    {
        public long User;
        public uint Id;
        public byte Kind;
        public bool IsProjectile;
        public int Prefab;
        public readonly List<float> Times = new();
        public readonly List<Vector3> Positions = new();
        public readonly List<Quaternion> Rotations = new();
        public readonly List<byte> Bools = new();
        public float Start => Times[0];
        public float End => Times[Times.Count - 1];
        public string Key => User + ":" + Id;
        public string KindName => IsProjectile ? "projectile" : Kind == 2 ? "ragdoll" : Kind == 3 ? "drop" : "actor";

        /// <summary>Position and rotation at a replay time, blended between samples the same way playback blends them.</summary>
        public bool Sample(float time, out Vector3 position, out Quaternion rotation)
        {
            position = Positions[0]; rotation = Rotations[0];
            if (time < Start - 0.0001f || time > End + 0.0001f) return false;
            int index = Index(time);
            if (index >= Times.Count - 1) { position = Positions[Times.Count - 1]; rotation = Rotations[Times.Count - 1]; return true; }
            float span = Times[index + 1] - Times[index];
            // A gap longer than a few samples means the entity left the recorded area and came back; do not blend across it.
            if (span > 0.5f) { position = Positions[index]; rotation = Rotations[index]; return true; }
            float t = span > 0.0001f ? Mathf.Clamp01((time - Times[index]) / span) : 0f;
            position = Vector3.Lerp(Positions[index], Positions[index + 1], t);
            rotation = Quaternion.Slerp(Rotations[index], Rotations[index + 1], t);
            return true;
        }

        /// <summary>The last sample at or before the time.</summary>
        public int Index(float time)
        {
            int lo = 0, hi = Times.Count - 1;
            if (time <= Times[0]) return 0;
            if (time >= Times[hi]) return hi;
            while (lo < hi - 1)
            {
                int mid = (lo + hi) / 2;
                if (Times[mid] <= time) lo = mid; else hi = mid;
            }
            return lo;
        }
    }

    /// <summary>Every entity's track in a recording, so tools can ask where an actor is at any replay time without playing it.</summary>
    public sealed class ReplayTracks
    {
        public readonly Dictionary<(long, uint), ActorTrack> ByKey = new();
        public readonly List<ActorTrack> All = new();

        public ReplayTracks(ReplayFile replay)
        {
            foreach (ReplayFrame frame in replay.Frames)
                foreach (EntitySnapshot entity in frame.Entities)
                {
                    (long, uint) key = (entity.ZdoUserID, entity.ZdoID);
                    if (!ByKey.TryGetValue(key, out ActorTrack track))
                    {
                        track = new ActorTrack { User = entity.ZdoUserID, Id = entity.ZdoID, Kind = entity.Kind, IsProjectile = entity.IsProjectile, Prefab = entity.PrefabHash };
                        ByKey[key] = track;
                        All.Add(track);
                    }
                    track.Times.Add(frame.Time);
                    track.Positions.Add(entity.Position);
                    track.Rotations.Add(entity.Rotation);
                    track.Bools.Add(entity.AnimBools);
                }
        }

        public ActorTrack Get(string actor)
        {
            (long user, uint id) = CommandText.Actor(actor);
            return ByKey.TryGetValue((user, id), out ActorTrack track) ? track : throw new System.ArgumentException($"No recorded actor {actor}; use ct_replay targets");
        }

        /// <summary>The box around the given characters' tracks (every character when none are given): the fight area.</summary>
        public Bounds FightArea(ICollection<(long, uint)>? only = null)
        {
            bool any = false;
            Bounds bounds = default;
            foreach (ActorTrack track in All)
            {
                if (track.Kind != 0 || track.IsProjectile) continue;
                if (only != null && only.Count > 0 && !only.Contains((track.User, track.Id))) continue;
                foreach (Vector3 position in track.Positions)
                {
                    if (!any) { bounds = new Bounds(position, Vector3.zero); any = true; }
                    else bounds.Encapsulate(position);
                }
            }
            return bounds;
        }
    }
}
