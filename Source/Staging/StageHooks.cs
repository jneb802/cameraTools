using System;
using UnityEngine;

namespace cameraTools.Staging
{
    /// <summary>
    /// The public contract a mod uses to let ct_stage build its rooms in a live world, such as Valheim Endgame's
    /// encounter rooms. It uses only Unity and system types, so a mod can bind to it by reflection and needs no
    /// reference to cameraTools. With no provider registered, ct_stage room reports that no mod builds rooms.
    /// </summary>
    public static class StageHooks
    {
        /// <summary>Raised when the contract changes in a way a caller must handle.</summary>
        public const int Version = 1;

        internal static Func<string[]>? RoomIds { get; private set; }
        internal static Func<string, Vector3, GameObject?>? BuildRoom { get; private set; }
        internal static Func<GameObject, Vector3[]>? RoomAnchors { get; private set; }

        internal static Action<GameObject, bool>? CreatureStaged { get; private set; }

        /// <summary>
        /// Registers a callback for creatures entering and leaving staging. It gets the creature and true when
        /// ct_stage takes it over (spawn or take), false when it is released back to its own AI. A mod uses it to
        /// pause its own encounter mechanics (shields, summons, nests) on a creature held for filming.
        /// </summary>
        public static void StagedCreatures(Action<GameObject, bool> staged) => CreatureStaged = staged;

        internal static void Notify(Character character, bool staged)
        {
            try { CreatureStaged?.Invoke(character.gameObject, staged); }
            catch (Exception error) { cameraToolsPlugin.TemplateLogger.LogWarning($"[StageHooks] Staged-creature callback failed: {error.Message}"); }
        }

        /// <summary>
        /// Registers how to build a room for staging.
        /// <paramref name="ids"/> lists the room ids that can be built.
        /// <paramref name="build"/> builds the room with its floor origin at the given position, with no encounter
        /// (no waves, gates, portals or rewards), and returns its active root, or null when it cannot.
        /// <paramref name="anchors"/> returns three points of a built room: where a boss stands, the direction
        /// that boss faces (a unit vector), and where the player stands inside the room's environment zone.
        /// </summary>
        public static void RoomProvider(Func<string[]> ids, Func<string, Vector3, GameObject?> build, Func<GameObject, Vector3[]> anchors)
        {
            RoomIds = ids;
            BuildRoom = build;
            RoomAnchors = anchors;
        }
    }
}
