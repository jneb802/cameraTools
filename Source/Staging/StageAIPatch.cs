using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace cameraTools.Staging
{
    /// <summary>
    /// While a creature is staged, its staging steps run in place of its own AI update. The update only runs on the
    /// creature's owner, as in the game, so a creature owned by another client keeps its own AI.
    /// </summary>
    [HarmonyPatch]
    internal static class StageAIPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(MonsterAI), nameof(MonsterAI.UpdateAI));
            yield return AccessTools.Method(typeof(AnimalAI), nameof(AnimalAI.UpdateAI));
        }

        private static bool Prefix(BaseAI __instance, float dt, ref bool __result)
        {
            StagedActor? actor = StagedActor.Of(__instance);
            if (actor == null || actor.Mode == StageMode.Free) return true;
            if (__instance.m_nview == null || !__instance.m_nview.IsValid() || !__instance.m_nview.IsOwner()) return true;
            actor.StepCreature(dt);
            __result = true;
            return false;
        }
    }
}
