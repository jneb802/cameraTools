using HarmonyLib;
using UnityEngine;

namespace cameraTools.Replay
{
    /// <summary>
    /// Records each hit where the owning game applies it (Character.RPC_Damage), with what happened to it on the way:
    /// block, parry, stagger, backstab, critical hit, dodge and kill. Only hits this game processes are seen.
    /// </summary>
    internal static class HitCapture
    {
        // RPC_Damage runs synchronously on the owner, so one hit is in progress at a time.
        internal static Character? Victim;
        internal static HitResult Flags;
        internal static ZDOID Projectile = ZDOID.None;

        internal struct State
        {
            public bool Active;
            public float Health;
            public bool Staggering;
            public float BackstabTime;
            public float[] Damage;
            public Vector3 Point;
            public Vector3 Direction;
            public ZDOID Attacker;
            public ZDOID Projectile;
            public string HitType;
            public string Skill;
            public Character? PreviousVictim;
            public HitResult PreviousFlags;
        }

        [HarmonyPatch(typeof(Character), nameof(Character.RPC_Damage))]
        private static class DamagePatch
        {
            private static void Prefix(Character __instance, HitData hit, out State __state)
            {
                __state = default;
                if (!ReplayRecorder.IsRecording || hit == null || __instance.m_nview == null || !__instance.m_nview.IsValid() || !__instance.m_nview.IsOwner()) return;
                if (__instance.IsDead() || __instance.GetHealth() <= 0f) return;
                __state = new State
                {
                    Active = true,
                    Health = __instance.GetHealth(),
                    Staggering = __instance.IsStaggering(),
                    BackstabTime = __instance.m_backstabTime,
                    Damage = CombatData.Damage(hit.m_damage),
                    Point = hit.m_point,
                    Direction = hit.m_dir,
                    Attacker = hit.m_attacker,
                    Projectile = Projectile,
                    HitType = hit.m_hitType.ToString(),
                    Skill = hit.m_skill.ToString(),
                    PreviousVictim = Victim,
                    PreviousFlags = Flags
                };
                Victim = __instance;
                Flags = hit.m_dodgeable && __instance.IsDodgeInvincible() ? HitResult.Dodged : HitResult.None;
            }

            private static void Postfix(Character __instance, State __state)
            {
                if (!__state.Active) return;
                HitResult result = Flags;
                Victim = __state.PreviousVictim;
                Flags = __state.PreviousFlags;
                float taken = Mathf.Max(0f, __state.Health - __instance.GetHealth());
                if (__instance.m_backstabTime != __state.BackstabTime) result |= HitResult.Backstab;
                if (__state.Staggering && !__instance.IsPlayer() && taken > 0f) result |= HitResult.Critical;
                if (__instance.GetHealth() <= 0f || __instance.IsDead()) result |= HitResult.Killed;
                if (taken > 0f) result |= HitResult.Hit;
                float raw = 0f;
                foreach (float value in __state.Damage) raw += value;
                // Pushback and parry deflection send empty hits; they are not hits for a viewer.
                if (raw <= 0f && result == HitResult.None) return;
                ZDOID victim = __instance.GetZDOID();
                ReplayRecorder.Instance.CaptureHit(new HitEvent
                {
                    VictimUser = victim.UserID, VictimId = victim.ID,
                    AttackerUser = __state.Attacker.UserID, AttackerId = __state.Attacker.ID,
                    ProjectileUser = __state.Projectile.UserID, ProjectileId = __state.Projectile.ID,
                    Point = __state.Point, Direction = __state.Direction, Damage = __state.Damage,
                    Taken = taken, Result = result, HitType = __state.HitType, Skill = __state.Skill
                });
            }
        }

        [HarmonyPatch(typeof(Character), nameof(Character.AddStaggerDamage))]
        private static class StaggerPatch
        {
            private static void Postfix(Character __instance, bool __result)
            {
                if (__result && Victim == __instance) Flags |= HitResult.Staggered;
            }
        }

        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.BlockAttack))]
        private static class BlockPatch
        {
            private static void Prefix(Humanoid __instance, HitData hit, out (bool Attempt, bool Perfect, float Before) __state)
            {
                __state = default;
                if (Victim != __instance) return;
                ItemDrop.ItemData? blocker = __instance.GetCurrentBlocker();
                bool attempt = blocker != null && Vector3.Dot(hit.m_dir, __instance.transform.forward) <= 0f;
                bool perfect = attempt && blocker!.m_shared.m_timedBlockBonus > 1f && __instance.m_blockTimer != -1f && __instance.m_blockTimer < 0.25f;
                __state = (attempt, perfect, hit.GetTotalBlockableDamage());
            }

            private static void Postfix(Humanoid __instance, HitData hit, (bool Attempt, bool Perfect, float Before) __state)
            {
                if (!__state.Attempt || Victim != __instance) return;
                bool blocked = hit.GetTotalBlockableDamage() < __state.Before - 0.01f || __state.Before <= 0f;
                if (blocked) Flags |= __state.Perfect ? HitResult.Blocked | HitResult.Parried : HitResult.Blocked;
                else Flags |= HitResult.GuardBroken;
            }
        }

        [HarmonyPatch(typeof(global::Projectile), nameof(global::Projectile.OnHit))]
        private static class ProjectilePatch
        {
            private static void Prefix(global::Projectile __instance, out ZDOID __state)
            {
                __state = Projectile;
                if (ReplayRecorder.IsRecording && __instance.m_nview != null && __instance.m_nview.IsValid()) Projectile = __instance.m_nview.GetZDO().m_uid;
            }

            private static void Postfix(ZDOID __state) => Projectile = __state;
        }
    }
}
