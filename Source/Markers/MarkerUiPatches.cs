using HarmonyLib;
using UnityEngine;

namespace cameraTools.Markers
{
    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Update))]
    internal static class MarkerPanelEscapePatch
    {
        private static bool Prefix()
        {
            if (!MarkerPanel.PanelOpen)
            {
                return true;
            }

            if (ZInput.GetKeyDown(KeyCode.Escape) || ZInput.GetButtonDown("JoyButtonB"))
            {
                MarkerPanel.Close();
                ZInput.ResetButtonStatus("JoyButtonB");
                return false;
            }

            // Letters typed into the label or note must not trigger inventory shortcuts.
            if (MarkerPanel.FieldFocused)
            {
                ZInput.ResetButtonStatus("Inventory");
                ZInput.ResetButtonStatus("Use");
                ZInput.ResetButtonStatus("JoyButtonY");
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Hide))]
    internal static class MarkerPanelHidePatch
    {
        private static void Postfix() => MarkerPanel.Close();
    }

    [HarmonyPatch(typeof(PlayerController), "TakeInput")]
    internal static class MarkerPanelPlayerInputPatch
    {
        private static void Postfix(ref bool __result)
        {
            if (MarkerPanel.FieldFocused)
            {
                __result = false;
            }
        }
    }
}
