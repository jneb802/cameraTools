using HarmonyLib;
using UnityEngine;

namespace cameraTools
{
    [HarmonyPatch(typeof(Player), nameof(Player.FixedUpdate))]
    public static class PlayerFollowPatch
    {
        public static bool FollowEnabled;

        static void Postfix(Player __instance)
        {
            if (!FollowEnabled)
                return;

            if (__instance != Player.m_localPlayer)
                return;

            if (!GameCamera.InFreeFly() || GameCamera.m_instance == null)
                return;

            Vector3 camPos = GameCamera.m_instance.transform.position;

            // Move both the transform and Rigidbody so physics doesn't fight us
            __instance.transform.position = camPos;
            __instance.m_body.position = camPos;
            __instance.m_body.linearVelocity = Vector3.zero;
            __instance.m_body.angularVelocity = Vector3.zero;
            __instance.m_body.useGravity = false;
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.FixedUpdate))]
    public static class PlayerFollowGravityRestore
    {
        // Separate patch to restore gravity when follow mode is turned off
        static void Prefix(Player __instance)
        {
            if (__instance != Player.m_localPlayer)
                return;

            // Restore gravity when not in follow-fly mode
            if (!PlayerFollowPatch.FollowEnabled || !GameCamera.InFreeFly())
            {
                if (__instance.m_body != null && !__instance.m_body.useGravity && !__instance.IsDebugFlying())
                    __instance.m_body.useGravity = true;
            }
        }
    }
}
