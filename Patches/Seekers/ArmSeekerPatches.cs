using System;
using System.Collections.Generic;
using System.Reflection;
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
                if (!SeekerDispersionHelper.ShouldRun(__instance, ___missile)) return;

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
    /// Optional ARM activation range (ARMSeekerActivationRange_m > 0): the passive seeker listens only within
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

    /// <summary>
    /// With an activation range: outside it the seeker evaluates and tracks nothing; inside it the radar designated
    /// at launch is restored when it emits (TrackCurrentTarget), then the vanilla line-of-sight check runs.
    /// </summary>
    [HarmonyPatch]
    public static class ARMSeeker_ActivationRange_Patch
    {
        private const string TrackCurrentTargetName = "TrackCurrentTarget";

        [HarmonyTargetMethods]
        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(ARMSeeker), "EvaluateRadarSources");
            yield return AccessTools.Method(typeof(ARMSeeker), TrackCurrentTargetName);
        }

        [HarmonyPrefix]
        public static bool Prefix(ARMSeeker __instance, MethodBase __originalMethod, ref Radar __result, Missile ___missile, GlobalPosition ___knownPos, ref Radar ___targetedRadar)
        {
            try
            {
                if (MunitionCatalog.ArmActivationRange <= 0.0f) return true;
                if (!SeekerDispersionHelper.ShouldRun(__instance, ___missile)) return true;

                if (!ArmActivationGate.IsActive(__instance, ___missile, ___knownPos))
                {
                    __result = null;
                    return false;
                }

                if (__originalMethod.Name == TrackCurrentTargetName)
                {
                    Radar designated = ArmActivationGate.GetDesignatedRadar(__instance);
                    if (___targetedRadar == null && designated != null && designated.activated)
                    {
                        ___targetedRadar = designated;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("ARMSeeker_ActivationRange_Patch", ex);
                return true;
            }
        }
    }

    /// <summary>
    /// Emitter loss (ADR-74). While the seeker tracks a radar, vanilla Seek writes the true radar position into the
    /// datum each frame. When the radar stops (or terrain blocks it), the seeker keeps only its angle memory: the datum
    /// gets the launch miss of the missile (CEP of the launch track), limited to the distance at the loss multiplied by
    /// tan(ARMSeekerMemoryAngleError_deg). A poor launch track and an early shutdown can make the missile miss.
    /// TrackCurrentTarget runs first in Seek, before the datum is used for the aimpoint, so the offset applies in the
    /// same frame. One offset per lock: a new lock writes the true position again.
    /// </summary>
    [HarmonyPatch(typeof(ARMSeeker), "TrackCurrentTarget")]
    public static class ARMSeeker_EmitterLoss_Patch
    {
        private sealed class LockState
        {
            public bool Locked;
        }

        private static readonly ConditionalWeakTable<ARMSeeker, LockState> s_states = new ConditionalWeakTable<ARMSeeker, LockState>();

        [HarmonyPostfix]
        public static void Postfix(ARMSeeker __instance, Radar __result, Missile ___missile, Radar ___targetedRadar, ref GlobalPosition ___knownPos)
        {
            try
            {
                if (!SeekerDispersionHelper.ShouldRun(__instance, ___missile)) return;
                if (!___missile.LocalSim) return;

                LockState state = s_states.GetOrCreateValue(__instance);
                if (__result != null)
                {
                    state.Locked = true;
                    return;
                }
                if (!state.Locked) return;
                state.Locked = false;

                // A destroyed radar: the missile hit it or the site is dead. Nothing to hide.
                Radar lost = ___targetedRadar;
                if (lost == null || !lost.IsOperational()) return;

                float angleDeg = CnoEvilConfig.ARMSeekerMemoryAngleError_deg.Value;
                if (angleDeg <= 0.0f) return;

                Vector3 offset;
                if (!TrackUncertaintyCalculator.TryGetMissileDispersion(___missile, out offset)) return;
                offset.y = 0.0f;
                float mag = offset.magnitude;
                if (mag < 0.01f) return;

                float range = Vector3.Distance(___missile.transform.position, ___knownPos.ToLocalPosition());
                float limit = range * Mathf.Tan(Mathf.Min(angleDeg, 45.0f) * Mathf.Deg2Rad);
                if (mag > limit) offset *= limit / mag;

                ___knownPos = ___knownPos + offset;

                if (EvilLog.On(LogCat.Missile))
                {
                    EvilLog.Write(LogCat.Missile, "ARM {0} lost emitter {1} at {2:F0} m: datum offset {3:F0} m (launch miss {4:F0} m)",
                        ___missile.GetInstanceID(), EvilLog.Name(lost.GetAttachedUnit()), range, offset.magnitude, mag);
                }
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("ARMSeeker_EmitterLoss_Patch", ex);
            }
        }
    }
}
