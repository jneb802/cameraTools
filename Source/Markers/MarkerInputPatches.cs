using HarmonyLib;
using UnityEngine;

namespace cameraTools.Markers
{
    // Left click places markers while on foot; do not let the player swing a weapon.
    [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.StartAttack))]
    internal static class BlockAttackWhileMarking
    {
        private static bool Prefix(Humanoid __instance, ref bool __result)
        {
            if (!MarkerMode.Active || __instance != Player.m_localPlayer || !MarkerMode.BlockPlayerActions)
            {
                return true;
            }

            __result = false;
            return false;
        }
    }

    // E edits markers; do not let it open doors or chests at the same time.
    [HarmonyPatch(typeof(Player), "Interact", typeof(GameObject), typeof(bool), typeof(bool))]
    internal static class BlockInteractWhileMarking
    {
        private static bool Prefix(Player __instance)
        {
            return !(MarkerMode.Active && __instance == Player.m_localPlayer && MarkerMode.BlockPlayerActions && MarkerMode.Hovered != null);
        }
    }

    // Typing in the edit window must not move the player or trigger hotkeys.
    [HarmonyPatch(typeof(Player), "TakeInput")]
    internal static class BlockInputWhileEditing
    {
        private static void Postfix(ref bool __result)
        {
            if (MarkerMode.IsEditing)
            {
                __result = false;
            }
        }
    }

    // The area tool owns the scroll wheel; keep the camera from zooming or changing fly speed.
    [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetMouseScrollWheel))]
    internal static class ConsumeScrollWhileMarking
    {
        private static bool Prefix(ref float __result)
        {
            if (!MarkerMode.ConsumeScroll)
            {
                return true;
            }

            __result = 0f;
            return false;
        }
    }
}
