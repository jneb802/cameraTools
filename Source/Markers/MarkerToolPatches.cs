using HarmonyLib;
using UnityEngine;

namespace cameraTools.Markers
{
    // The item is cloned from the Hammer as soon as the item database exists (main menu and in game).
    [HarmonyPatch(typeof(ObjectDB), "Awake")]
    internal static class RegisterMarkerToolOnAwake
    {
        private static void Postfix(ObjectDB __instance) => MarkerTool.EnsureItem(__instance);
    }

    [HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.CopyOtherDB))]
    internal static class RegisterMarkerToolOnCopy
    {
        private static void Postfix(ObjectDB __instance) => MarkerTool.EnsureItem(__instance);
    }

    // Pieces need in-game materials, so they are built when the network scene comes up.
    [HarmonyPatch(typeof(ZNetScene), "Awake")]
    internal static class RegisterMarkerPrefabs
    {
        private static void Postfix(ZNetScene __instance) => MarkerTool.EnsurePrefabs(__instance);
    }

    // Left click with a marker piece selected: store a marker instead of building anything.
    [HarmonyPatch(typeof(Player), nameof(Player.TryPlacePiece))]
    internal static class PlaceMarkerInsteadOfPiece
    {
        private static bool Prefix(Player __instance, Piece piece, ref bool __result)
        {
            if (__instance != Player.m_localPlayer || !MarkerTool.IsMarkerPiece(piece))
            {
                return true;
            }

            __result = MarkerMode.PlaceFromTool(__instance, piece);
            return false;
        }
    }

    // Middle click with the marker tool removes the marker under the crosshair and never a real piece.
    [HarmonyPatch(typeof(Player), "RemovePiece")]
    internal static class RemoveMarkerInsteadOfPiece
    {
        private static bool Prefix(Player __instance, ref bool __result)
        {
            if (__instance != Player.m_localPlayer || !MarkerTool.IsEquipped(__instance))
            {
                return true;
            }

            MarkerMode.RemoveFromTool(__instance);
            __result = false;
            return false;
        }
    }

    // Do not highlight walls as removable while the marker tool is out.
    [HarmonyPatch(typeof(Player), "UpdateWearNTearHover")]
    internal static class NoPieceHoverWithMarkerTool
    {
        private static bool Prefix(Player __instance)
        {
            if (__instance != Player.m_localPlayer || !MarkerTool.IsEquipped(__instance))
            {
                return true;
            }

            __instance.m_hoveringPiece = null;
            return false;
        }
    }

    // Marks are not buildings: wards, no-build zones, deep snow and dungeons must not block them.
    [HarmonyPatch(typeof(Player), "UpdatePlacementGhost")]
    internal static class RelaxMarkerPlacementStatus
    {
        private static void Postfix(Player __instance)
        {
            if (__instance != Player.m_localPlayer || __instance.m_placementGhost == null)
            {
                return;
            }

            if (!MarkerTool.IsMarkerPiece(__instance.m_placementGhost.GetComponent<Piece>()))
            {
                return;
            }

            switch (__instance.m_placementStatus)
            {
                case Player.PlacementStatus.Valid:
                case Player.PlacementStatus.NoRayHits:
                    return;
                default:
                    __instance.m_placementStatus = Player.PlacementStatus.Valid;
                    if (__instance.m_placementGhost.activeSelf)
                    {
                        __instance.SetPlacementGhostValid(true);
                    }

                    return;
            }
        }
    }

    // E renames the marker under the crosshair; do not open doors or chests at the same time.
    [HarmonyPatch(typeof(Player), "Interact", typeof(GameObject), typeof(bool), typeof(bool))]
    internal static class BlockInteractWhileHoveringMarker
    {
        private static bool Prefix(Player __instance)
        {
            return !(__instance == Player.m_localPlayer && MarkerMode.Placing && MarkerMode.Hovered != null);
        }
    }
}
