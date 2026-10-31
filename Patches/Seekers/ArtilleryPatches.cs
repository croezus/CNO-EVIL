using System;
using HarmonyLib;
using UnityEngine;
using CnoEvil.Core;

namespace CnoEvil.Patches
{
    /// <summary>
    /// Guided artillery (155 mm, MLRS): launch datum = faction track + dispersion. Vanilla starts the datum on the
    /// ground point along the launch velocity at sea level (y = 0) and moves it to the target only while the
    /// faction tracks it; the mod datum has the target altitude. The vanilla round error (CEP) stays.
    /// </summary>
    [HarmonyPatch(typeof(InertialSeekerShell), "Initialize", new Type[] { typeof(Unit), typeof(GlobalPosition) })]
    public static class InertialSeekerShell_Initialize_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(
            InertialSeekerShell __instance,
            Unit target,
            Missile ___missile,
            ref GlobalPosition ___knownPos,
            ref Vector3 ___knownVel)
        {
            try
            {
                if (!SeekerDispersionHelper.ShouldRun(__instance, ___missile)) return;

                Unit t = (target != null && !target.disabled) ? target : SeekerDispersionHelper.GetTargetForMissile(___missile);
                SeekerDispersionHelper.ApplyLaunchDatum(___missile, t, ref ___knownPos, ref ___knownVel);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("InertialSeekerShell_Initialize_Patch", ex);
            }
        }
    }

    /// <summary>
    /// Vanilla moves the datum to the true target while the faction tracks it. For a target the mod guides, the
    /// datum is the faction track + dispersion, or stays at the last track datum when the faction lost it.
    /// Before the first track datum the vanilla update stands (+ dispersion), so the round never keeps the
    /// sea-level start datum because of the mod.
    /// </summary>
    [HarmonyPatch(typeof(InertialSeekerShell), "UpdateTargetPosition")]
    public static class InertialSeekerShell_UpdateTargetPosition_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(GlobalPosition ___knownPos, out GlobalPosition __state)
        {
            __state = ___knownPos;
        }

        [HarmonyPostfix]
        public static void Postfix(
            InertialSeekerShell __instance,
            bool ___useDatalink,
            Missile ___missile,
            Unit ___targetUnit,
            ref GlobalPosition ___knownPos,
            ref Vector3 ___knownVel,
            GlobalPosition __state)
        {
            try
            {
                if (!SeekerDispersionHelper.ShouldRun(__instance, ___missile) || !___useDatalink) return;

                SeekerDispersionHelper.CorrectDatum(___missile, ___targetUnit, ref ___knownPos, ref ___knownVel, __state, true);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("InertialSeekerShell_UpdateTargetPosition_Patch", ex);
            }
        }
    }

    /// <summary>
    /// Ballistic missile (Piledriver TBM) impact point at launch: for a target the mod guides, the faction track +
    /// dispersion, else the launcher aimpoint + dispersion. At a map point without a target, the aimpoint
    /// (vanilla replaces it with a point 100 km ahead). The flight (SetTrajectory, burn, re-entry) stays vanilla.
    /// </summary>
    [HarmonyPatch(typeof(BallisticMissileGuidance), "Initialize", new Type[] { typeof(Unit), typeof(GlobalPosition) })]
    public static class BallisticMissileGuidance_Initialize_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(
            BallisticMissileGuidance __instance,
            Unit target,
            GlobalPosition aimpoint,
            Missile ___missile,
            float ___maxTargetSpeed,
            ref GlobalPosition ___knownPos,
            ref Vector3 ___knownVel,
            ref Vector3 ___errorOffset,
            ref float ___targetAngle)
        {
            try
            {
                if (!SeekerDispersionHelper.ShouldRun(__instance, ___missile)) return;

                Unit t = (target != null && !target.disabled) ? target : SeekerDispersionHelper.GetTargetForMissile(___missile);
                if (t != null)
                {
                    if (!SeekerDispersionHelper.IsModGuided(___missile, t)) return;

                    GlobalPosition known;
                    Vector3 knownVel;
                    if (SeekerDispersionHelper.TryGetDatalinkTargetState(___missile, t, out known, out knownVel))
                    {
                        ___knownPos = known;
                        ___knownVel = Vector3.ClampMagnitude(knownVel, ___maxTargetSpeed);
                    }
                    else
                    {
                        Vector3 disp = SeekerDispersionHelper.GetDispersionForMissile(___missile, t);
                        Vector3 local = ((aimpoint != default(GlobalPosition)) ? aimpoint : ___knownPos).ToLocalPosition();
                        local.x += disp.x;
                        local.z += disp.z;
                        ___knownPos = local.ToGlobalPosition();
                        ___knownVel = Vector3.zero;
                    }
                    return;
                }

                // Map point without a target: vanilla ignores the aimpoint and flies 100 km ahead
                if (aimpoint != default(GlobalPosition))
                {
                    ___knownPos = aimpoint;
                    ___knownVel = Vector3.zero;
                    ___errorOffset = Vector3.zero;
                    ___missile.SetAimpoint(aimpoint, Vector3.zero);

                    // Ballistic launch angle for the true distance to the aimpoint
                    Vector3 diff = aimpoint.ToLocalPosition() - ___missile.transform.position;
                    diff.y = 0f;
                    float dist = diff.magnitude;
                    float dV = ___missile.GetRemainingDeltaV();
                    if (dV > 10f)
                    {
                        float sinVal = Mathf.Clamp01(dist * 9.81f / (dV * dV));
                        float angle = Mathf.Asin(sinVal) * 0.5f * Mathf.Rad2Deg;
                        if (angle <= 45f) angle = 90f - angle;
                        if (!float.IsNaN(angle)) ___targetAngle = angle;
                    }
                }
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("BallisticMissileGuidance_Initialize_Patch", ex);
            }
        }
    }
}
