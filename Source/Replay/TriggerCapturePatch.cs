using System.Reflection;
using HarmonyLib;

namespace cameraTools.Replay
{
    [HarmonyPatch]
    public static class TriggerCapturePatch
    {
        static MethodBase TargetMethod()
        {
            return typeof(ZSyncAnimation).GetMethod(
                "RPC_SetTrigger",
                BindingFlags.NonPublic | BindingFlags.Instance
            );
        }

        static void Postfix(ZSyncAnimation __instance, string name)
        {
            if (!ReplayRecorder.IsRecording)
                return;

            var nview = __instance.GetComponent<ZNetView>();
            if (nview == null)
                return;

            var zdo = nview.GetZDO();
            if (zdo == null)
                return;

            ReplayRecorder.Instance.CaptureTrigger(zdo.m_uid, name);
        }
    }
}
