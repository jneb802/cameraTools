using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace cameraTools.Staging
{
    /// <summary>
    /// A mod room built in the live world for staging, with no encounter. The room comes from the mod that registered
    /// <see cref="StageHooks.RoomProvider"/>. By default it is built 4,500 m above the player, and the player is moved
    /// just inside it, so the room's environment zone (which follows the local player) applies, and the player's model
    /// is hidden so it cannot appear in shots. Clearing removes the room and returns the player, visible, to where they were.
    /// Actors can be placed and aimed with the names <see cref="Boss"/>, <see cref="Stand"/> and <see cref="Origin"/>.
    /// </summary>
    internal static class StageRoom
    {
        public const string Boss = "room.boss";
        public const string Stand = "room.stand";
        public const string Origin = "room.origin";
        public const float DefaultHeight = 4500f;

        private static GameObject? _room;
        private static string _id = "";
        private static Vector3 _origin;
        private static Vector3 _boss;
        private static Vector3 _bossForward = Vector3.forward;
        private static Vector3 _stand;
        private static Vector3 _return;
        private static bool _moved;
        private static readonly List<Renderer> Hidden = new();

        public static bool Active => _room != null;

        /// <summary>The direction the room's boss marker faces; null when no room is built.</summary>
        public static Vector3? BossForward => _room != null ? _bossForward : null;

        public static string[] RoomIds => StageHooks.RoomIds?.Invoke() ?? Array.Empty<string>();

        public static bool IsRoomName(string name) =>
            name.Equals(Boss, StringComparison.OrdinalIgnoreCase) || name.Equals(Stand, StringComparison.OrdinalIgnoreCase) || name.Equals(Origin, StringComparison.OrdinalIgnoreCase);

        /// <summary>The point a room name stands for. Throws when no room is built.</summary>
        public static Vector3 Resolve(string name)
        {
            if (_room == null) throw new InvalidOperationException($"{name} needs a room: ct_stage room <id> first");
            if (name.Equals(Boss, StringComparison.OrdinalIgnoreCase)) return _boss;
            if (name.Equals(Stand, StringComparison.OrdinalIgnoreCase)) return _stand;
            return _origin;
        }

        /// <summary>Builds a room, replacing the one built before. Returns a one-line report.</summary>
        public static string Build(string id, bool movePlayer, bool hidePlayer, float height)
        {
            Player? player = Player.m_localPlayer;
            if (player == null) throw new InvalidOperationException("Load a world first");
            if (StageHooks.BuildRoom == null || StageHooks.RoomAnchors == null)
                throw new InvalidOperationException("No mod builds rooms for staging (Valheim Endgame provides them)");
            Clear();

            Vector3 here = player.transform.position;
            Vector3 origin = new(here.x, height, here.z);
            GameObject room = StageHooks.BuildRoom(id, origin) ?? throw new ArgumentException($"No room '{id}'. Rooms: {string.Join(", ", RoomIds)}");
            Vector3[] anchors = StageHooks.RoomAnchors(room);
            if (anchors.Length < 3)
            {
                UnityEngine.Object.Destroy(room);
                throw new InvalidOperationException("The room provider returned fewer than 3 anchor points");
            }

            _room = room;
            _id = id;
            _origin = origin;
            _boss = anchors[0];
            _bossForward = Flat(anchors[1]);
            _stand = anchors[2];
            _return = here;
            Physics.SyncTransforms();

            if (movePlayer) Move(player, _stand + Vector3.up * 0.1f);
            if (hidePlayer)
                foreach (Renderer renderer in player.GetComponentsInChildren<Renderer>())
                    if (renderer.enabled) { renderer.enabled = false; Hidden.Add(renderer); }
            return Status();
        }

        /// <summary>Removes the room, shows the player and returns them to where they were. False when no room was built.</summary>
        public static bool Clear()
        {
            foreach (Renderer renderer in Hidden) if (renderer != null) renderer.enabled = true;
            Hidden.Clear();
            if (_room == null) return false;
            UnityEngine.Object.Destroy(_room);
            _room = null;
            Player? player = Player.m_localPlayer;
            // A real teleport: the zone around the original spot was unloaded while the player was up here, and the game's
            // teleport holds the player until the ground is loaded. Moving the body would drop it through the world.
            if (_moved && player != null) player.TeleportTo(_return, player.transform.rotation, distantTeleport: true);
            _moved = false;
            return true;
        }

        public static string Status()
        {
            if (_room == null) return "room=none";
            CultureInfo ci = CultureInfo.InvariantCulture;
            float heading = (Mathf.Atan2(_bossForward.x, _bossForward.z) * Mathf.Rad2Deg + 360f) % 360f;
            return $"room={_id} origin={Point(_origin)} boss={Point(_boss)} boss_facing={heading.ToString("F0", ci)} stand={Point(_stand)} player_hidden={Hidden.Count > 0}";
        }

        // Player.TeleportTo would snap a distant destination to the terrain far below, so move the body directly.
        private static void Move(Player player, Vector3 position)
        {
            player.transform.position = position;
            player.m_body.position = position;
            player.m_body.linearVelocity = Vector3.zero;
            player.m_maxAirAltitude = position.y;
            _moved = true;
        }

        private static Vector3 Flat(Vector3 direction)
        {
            direction.y = 0f;
            return direction.sqrMagnitude < 0.0001f ? Vector3.forward : direction.normalized;
        }

        private static string Point(Vector3 p) => $"{p.x.ToString("F1", CultureInfo.InvariantCulture)},{p.y.ToString("F1", CultureInfo.InvariantCulture)},{p.z.ToString("F1", CultureInfo.InvariantCulture)}";
    }
}
