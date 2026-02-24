using System.Collections.Generic;
using UnityEngine;

namespace cameraTools.Replay
{
    public struct EntitySnapshot
    {
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

    public struct TriggerEvent
    {
        public float Time;
        public long ZdoUserID;
        public uint ZdoID;
        public string TriggerName;
    }

    public class ReplayFrame
    {
        public float Time;
        public List<EntitySnapshot> Entities = new List<EntitySnapshot>();
    }

    public class ReplayFile
    {
        public const string Magic = "VRPL";
        public const int Version = 1;

        public long Timestamp;
        public float Duration;
        public List<ReplayFrame> Frames = new List<ReplayFrame>();
        public List<TriggerEvent> TriggerEvents = new List<TriggerEvent>();
    }
}
