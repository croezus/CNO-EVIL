using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using CnoEvil.Core;
using CnoEvil.Config;

namespace CnoEvil.Patches
{
    /// <summary>
    /// ARAD launch datum: the faction track + launch dispersion (Y at the target level). A radar that emits is
    /// found by the vanilla passive seeker for the whole flight; a radar that stays silent leaves the missile on
    /// the datum: with a good track it hits the site, with a poor track it falls on empty ground.
    /// </summary>
    [HarmonyPatch(typeof(ARMSeeker), "Initialize", new Type[] { typeof(Unit), typeof(GlobalPosition) })]
    public static class ARMSeeker_Initialize_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(ARMSeeker __instance, Unit target, Missile ___missile, ref GlobalPosition ___knownPos, ref Vector3 ___knownVel)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive) return;
                if (__instance == null || ___missile == null) return;

                Radar r = (target != null) ? (target.radar as Radar) : null;
                if (r != null) ArmActivationGate.RegisterDesignatedRadar(__instance, r);

                SeekerDispersionHelper.ApplyLaunchDatum(___missile, target, ref ___knownPos, ref ___knownVel);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("ARMSeeker_Initialize_Patch", ex);
            }
        }
    }

    /// <summary>
    /// Optional ARM activation range (ARMSeekerActivationRangeMeters > 0): the passive seeker listens only within
    /// this distance of the datum or of the radar designated at launch. 0 = vanilla.
    /// </summary>
    public static class ArmActivationGate
    {
        private static readonly ConditionalWeakTable<ARMSeeker, Radar> s_designatedRadars = new ConditionalWeakTable<ARMSeeker, Radar>();

        public static void RegisterDesignatedRadar(ARMSeeker seeker, Radar radar)
        {
            if (seeker == null || radar == null) return;
            s_designatedRadars.Remove(seeker);
            s_designatedRadars.Add(seeker, radar);
        }

        public static Radar GetDesignatedRadar(ARMSeeker seeker)
        {
            Radar designated;
            return (seeker != null && s_designatedRadars.TryGetValue(seeker, out designated)) ? designated : null;
        }

        /// <summary>
        /// True when the seeker may listen: no activation range set, or the missile is within it.
        /// </summary>
        public static bool IsActive(ARMSeeker seeker, Missile missile, GlobalPosition knownPos)
        {
            float range = MunitionCatalog.ArmActivationRange;
            if (range <= 0.0f) return true;

            Vector3 missilePos = missile.transform.position;
            float dist = Vector3.Distance(missilePos, knownPos.ToLocalPosition());
            Radar designated = GetDesignatedRadar(seeker);
            if (designated != null)
            {
                dist = Mathf.Min(dist, Vector3.Distance(missilePos, designated.transform.position));
            }
            return dist <= range;
        }
    }

    [HarmonyPatch(typeof(ARMSeeker), "EvaluateRadarSources")]
    public static class ARMSeeker_EvaluateRadarSources_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(ARMSeeker __instance, ref Radar __result, Missile ___missile, GlobalPosition ___knownPos)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive) return true;
                if (__instance == null || ___missile == null) return true;
                if (ArmActivationGate.IsActive(__instance, ___missile, ___knownPos)) return true;

                __result = null;
                return false;
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("ARMSeeker_EvaluateRadarSources_Patch", ex);
                return true;
            }
        }
    }

    /// <summary>
    /// With an activation range: outside it the seeker tracks nothing; inside it the radar designated at launch is
    /// restored when it emits, then the vanilla line-of-sight check runs.
    /// </summary>
    [HarmonyPatch(typeof(ARMSeeker), "TrackCurrentTarget")]
    public static class ARMSeeker_TrackCurrentTarget_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(ARMSeeker __instance, ref Radar __result, Missile ___missile, GlobalPosition ___knownPos, ref Radar ___targetedRadar)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive) return true;
                if (__instance == null || ___missile == null) return true;
                if (MunitionCatalog.ArmActivationRange <= 0.0f) return true;

                if (!ArmActivationGate.IsActive(__instance, ___missile, ___knownPos))
                {
                    __result = null;
                    return false;
                }

                Radar designated = ArmActivationGate.GetDesignatedRadar(__instance);
                if (___targetedRadar == null && designated != null && designated.activated)
                {
                    ___targetedRadar = designated;
                }
                return true;
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("ARMSeeker_TrackCurrentTarget_Patch", ex);
                return true;
            }
        }
    }
}
