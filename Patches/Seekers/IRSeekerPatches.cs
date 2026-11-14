using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using CnoEvil.Core;
using CnoEvil.Config;

namespace CnoEvil.Patches
{
    /// <summary>
    /// Gimbal limit of the infrared seeker (ADR-72). Vanilla IRSeeker has no angle limit: a locked seeker tracks its
    /// IR source at any angle, so a missile that overshoots turns back. After the vanilla lock check, a source more than
    /// IRSeekerGimbal_deg off the missile axis is lost the vanilla way (LoseLock, IRTarget = null, the same path as a
    /// blocked line of sight); vanilla then flies the drift datum. 0 = vanilla.
    /// </summary>
    [HarmonyPatch(typeof(IRSeeker), "IRLockCheck")]
    public static class IRSeeker_IRLockCheck_Patch
    {
        private static readonly Action<IRSeeker> s_loseLock = CreateLoseLock();

        private static Action<IRSeeker> CreateLoseLock()
        {
            try
            {
                MethodInfo mi = AccessTools.Method(typeof(IRSeeker), "LoseLock");
                if (mi == null) return null;
                return (Action<IRSeeker>)Delegate.CreateDelegate(typeof(Action<IRSeeker>), null, mi);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("IRSeeker_IRLockCheck_Patch.LoseLock", ex);
                return null;
            }
        }

        [HarmonyPostfix]
        public static void Postfix(
            IRSeeker __instance,
            ref bool __result,
            Missile ___missile,
            ref IRSource ___IRTarget)
        {
            try
            {
                if (!__result || s_loseLock == null) return;
                if (!SeekerDispersionHelper.ShouldRun(__instance, ___missile) || CnoEvilNet.IsMirrorClient) return;
                float limit = CnoEvilConfig.IRSeekerGimbal_deg.Value;
                if (limit <= 0.0f || ___IRTarget == null || ___IRTarget.transform == null) return;

                Transform missileTf = ___missile.transform;
                float angle = Vector3.Angle(missileTf.forward, ___IRTarget.transform.position - missileTf.position);
                if (angle <= limit) return;

                if (EvilLog.On(LogCat.Missile))
                {
                    EvilLog.Write(LogCat.Missile, "IR seeker {0} lost its {1} outside the gimbal limit ({2:F0} deg > {3:F0} deg)",
                        ___missile.GetInstanceID(), ___IRTarget.flare ? "flare" : "target", angle, limit);
                }
                s_loseLock(__instance);
                ___IRTarget = null;
                __result = false;
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("IRSeeker_IRLockCheck_Patch", ex);
            }
        }
    }
}
