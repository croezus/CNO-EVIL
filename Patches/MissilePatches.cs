using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using CnoEvil.Config;
using CnoEvil.Components;
using CnoEvil.Core;

namespace CnoEvil.Patches
{
    /// <summary>
    /// Event-driven hook for player-launched missiles to trigger Flight HUD trajectory lines and ETA tracking,
    /// and cooperative Datalink network missile tracking.
    /// </summary>
    [HarmonyPatch(typeof(Unit))]
    public static class MissilePatches
    {

        [HarmonyPatch("RegisterMissile")]
        [HarmonyPostfix]
        public static void RegisterMissile_Postfix(Unit __instance, Missile missile)
        {
            if (missile == null || __instance == null) return;
            if (!CnoEvilConfig.IsModActive) return;

            // Track active airborne missile for allied radar surveillance & datalink early warning
            DatalinkNetwork.RegisterMissile(missile);

            if (CnoEvilConfig.EnableMissileTrajectoryHUD == null || !CnoEvilConfig.EnableMissileTrajectoryHUD.Value) return;

            try
            {
                // Only track munitions launched by the local player's aircraft
                bool isPlayerAircraft = false;
                if (CombatHUD.i != null && CombatHUD.i.aircraft != null)
                {
                    if (CombatHUD.i.aircraft == __instance || (missile.owner != null && missile.owner == CombatHUD.i.aircraft))
                    {
                        isPlayerAircraft = true;
                    }
                }

                if (isPlayerAircraft)
                {
                    if (MissileTrajectoryHUDVisualizer.Instance != null)
                    {
                        MissileTrajectoryHUDVisualizer.Instance.OnMissileLaunched(missile);
                    }
                    else if (FlightHud.i != null)
                    {
                        MissileTrajectoryHUDVisualizer visualizer = FlightHud.i.gameObject.GetComponent<MissileTrajectoryHUDVisualizer>();
                        if (visualizer == null)
                        {
                            visualizer = FlightHud.i.gameObject.AddComponent<MissileTrajectoryHUDVisualizer>();
                        }
                        visualizer.OnMissileLaunched(missile);
                    }
                }
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("MissilePatches.RegisterMissile_Postfix", ex);
            }
        }

        [HarmonyPatch("DeregisterMissile")]
        [HarmonyPostfix]
        public static void DeregisterMissile_Postfix(Unit __instance, Missile missile)
        {
            if (missile == null) return;

            int id = missile.GetInstanceID();
            ARHSeeker_GetRadarReturn_Patch.ClearCoastTimer(id);
            SARHSeeker_GetTrackingStrength_Patch.ClearCoastTimer(id);

            // Deregister from allied Datalink surveillance network
            DatalinkNetwork.DeregisterMissile(missile);

            MunitionAudit.OnEnd(missile);

            // Deregister launch dispersion offset
            TrackUncertaintyCalculator.UnregisterMissile(missile);
            SeekerDispersionHelper.ForgetMissile(missile);
            MissileSync.Forget(missile);

            try
            {
                if (MissileTrajectoryHUDVisualizer.Instance != null)
                {
                    MissileTrajectoryHUDVisualizer.Instance.OnMissileDeregistered(missile);
                }
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("MissilePatches.DeregisterMissile_Postfix", ex);
            }
        }
    }

    /// <summary>
    /// AI launch doctrine shared by every fire path (CombatAI target lists, WeaponStation.Fire, turret target choice).
    /// </summary>
    internal static class AIDoctrine
    {
        public static float MinLaunchQuality
        {
            get { return CnoEvilConfig.AIMinLaunchQuality != null ? CnoEvilConfig.AIMinLaunchQuality.Value : 0.5f; }
        }

        public static float MinLaunchHitProbability
        {
            get { return CnoEvilConfig.AIMinLaunchHitProbability != null ? CnoEvilConfig.AIMinLaunchHitProbability.Value : 0.5f; }
        }

        /// <summary>
        /// A guided weapon (missile, guided bomb, guided shell) fires at a tracked threat (aircraft, ship, air defense,
        /// radar; ThreatClassification.IsTrackedThreat) only when:
        /// 1. the faction has a track of the target with Q >= AIMinLaunchQuality;
        /// 2. for a weapon that flies to the datum, the hit probability is >= AIMinLaunchHitProbability:
        ///    P = 1 - 0.5^((R / CEP)^2), R = max(seeker acquisition radius, warhead danger radius),
        ///    CEP = sqrt(track CEP^2 + vanilla round CEP^2) (Gaussian miss, half of the rounds inside the CEP).
        /// SARH, laser and IR seekers home on the illumination, the spot or the heat source: rule 1 only.
        /// Unguided weapons and other targets (trucks, tanks, buildings): vanilla rules.
        /// No faction or no track on a tracked target: hold fire.
        /// </summary>
        public static bool MeetsLaunchDoctrine(FactionHQ hq, Unit launcher, Unit target, WeaponInfo weaponInfo)
        {
            if (target == null || weaponInfo == null) return true;
            MunitionProfile p = MunitionCatalog.Get(weaponInfo);
            if (p.Kind == SeekerKind.Unguided) return true;
            if (hq == null) return false;
            if (!ThreatClassification.IsTrackedThreat(target, hq)) return true;

            TriangulationTrack track;
            if (!RWRTriangulationProcessor.TryPeekTrack(hq, target, out track) || track == null) return false;
            if (track.TrackingQuality < MinLaunchQuality) return false;
            if (MunitionCatalog.IgnoresDatum(p)) return true;

            float launchDistance = (launcher != null) ? Vector3.Distance(launcher.transform.position, target.transform.position) : 0.0f;
            float trackCep = SeekerDispersionHelper.PredictLaunchCep(track, launchDistance);
            float cep = Mathf.Sqrt(trackCep * trackCep + p.RoundCep * p.RoundCep);
            if (cep <= 0.0f) return true;

            float basket = Mathf.Max(MunitionCatalog.GetAcquisitionRadius(p, target, target.RCS), p.LethalRadius);
            return TrackUncertaintyCalculator.HitProbability(basket, cep) >= MinLaunchHitProbability;
        }

        /// <summary>
        /// The AI of this unit is under the doctrine: the doctrine is on, and the unit is not flown by a human player.
        /// </summary>
        public static bool AppliesTo(Unit unit)
        {
            if (!CnoEvilConfig.IsModActive || unit == null) return false;
            if (CnoEvilConfig.AIEvaluateMissileLaunchDoctrine == null || !CnoEvilConfig.AIEvaluateMissileLaunchDoctrine.Value) return false;
            if (CombatHUD.i != null && CombatHUD.i.aircraft == unit) return false;
            Aircraft aircraft = unit as Aircraft;
            return aircraft == null || aircraft.Player == null;
        }
    }

    /// <summary>
    /// Removes the targets that fail AIDoctrine.MeetsLaunchDoctrine from AI missile target lists.
    /// </summary>
    [HarmonyPatch(typeof(CombatAI), "LookForMissileTargets")]
    public static class CombatAI_LookForMissileTargets_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(Aircraft aircraft, WeaponStation weaponStation, List<Unit> outTargets, ref int __result)
        {
            try
            {
                if (!AIDoctrine.AppliesTo(aircraft)) return;
                if (weaponStation == null || weaponStation.WeaponInfo == null || outTargets == null || outTargets.Count == 0) return;

                for (int i = outTargets.Count - 1; i >= 0; i--)
                {
                    if (!AIDoctrine.MeetsLaunchDoctrine(aircraft.NetworkHQ, aircraft, outTargets[i], weaponStation.WeaponInfo))
                    {
                        outTargets.RemoveAt(i);
                    }
                }
                __result = outTargets.Count;
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("CombatAI_LookForMissileTargets_Patch", ex);
            }
        }
    }

    /// <summary>
    /// Last gate of every AI shot (aircraft, ships, air defense, artillery): a guided weapon that fails
    /// AIDoctrine.MeetsLaunchDoctrine does not fire. Each station is judged by its own weapon.
    /// </summary>
    [HarmonyPatch(typeof(WeaponStation), "Fire")]
    public static class WeaponStation_Fire_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(WeaponStation __instance, Unit owner, Unit target)
        {
            try
            {
                if (!AIDoctrine.AppliesTo(owner)) return true;
                if (__instance.WeaponInfo == null || target == null) return true;

                return AIDoctrine.MeetsLaunchDoctrine(owner.NetworkHQ, owner, target, __instance.WeaponInfo);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("WeaponStation_Fire_Patch", ex);
                return true;
            }
        }
    }

    /// <summary>
    /// Turret target choice: a turret skips a candidate that none of its stations may fire at, so it does not hold
    /// a target it cannot engage. A station with an unguided weapon always passes.
    /// </summary>
    [HarmonyPatch(typeof(Turret), "AssessTargetPriority")]
    public static class Turret_AssessTargetPriority_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(Unit targetCandidate, Unit ___attachedUnit, WeaponStation[] ___weaponStations)
        {
            try
            {
                if (!AIDoctrine.AppliesTo(___attachedUnit)) return true;
                if (targetCandidate == null || ___weaponStations == null || ___weaponStations.Length == 0) return true;

                FactionHQ hq = ___attachedUnit.NetworkHQ;
                for (int i = 0; i < ___weaponStations.Length; i++)
                {
                    WeaponStation st = ___weaponStations[i];
                    if (st == null || st.WeaponInfo == null) return true;
                    if (AIDoctrine.MeetsLaunchDoctrine(hq, ___attachedUnit, targetCandidate, st.WeaponInfo)) return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("Turret_AssessTargetPriority_Patch", ex);
                return true;
            }
        }
    }

    /// <summary>
    /// Vanilla requires the target position to be accurate to 100 m for every AI attack mode. For radar threats
    /// the mod tracks, the faction's own error estimate must instead fall within what the weapon still kills
    /// (MunitionCatalog.GetAcceptableAimError): seeker acquisition radius, warhead danger radius, or 100 m.
    /// The launch dispersion is gated separately (AIDoctrine.MeetsLaunchDoctrine).
    /// </summary>
    [HarmonyPatch(typeof(AIPilotCombatModes), "ManageTarget")]
    public static class AIPilotCombatModes_ManageTarget_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(Aircraft ___aircraft, Unit ___currentTarget, WeaponInfo ___currentWeaponInfo, ref bool ___targetAccurate)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive) return;
                if (___aircraft == null || ___currentTarget == null || ___currentTarget.disabled) return;

                TriangulationTrack track;
                if (!FactionTrackKnowledge.TryGetHostileTrack(___aircraft.NetworkHQ, ___currentTarget, out track)) return;

                GlobalPosition estimate;
                float error;
                if (!RWRTriangulationProcessor.TryGetFactionEstimate(track, ___currentTarget, out estimate, out error))
                {
                    ___targetAccurate = false; // Vanilla returns early and would keep a stale value
                    return;
                }

                float allowed = MunitionCatalog.GetAcceptableAimError(___currentWeaponInfo, ___currentTarget);
                ___targetAccurate = error <= allowed;
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("AIPilotCombatModes_ManageTarget_Patch", ex);
            }
        }
    }

    /// <summary>
    /// Intercept lead: the transpiler replaces the true target velocity of the vanilla lead
    /// (destination = knownPos + targetVel * min(dist / speed, 20 s)) with the velocity the faction knows.
    /// </summary>
    [HarmonyPatch(typeof(AIPilotCombatModes), "FlyToTarget")]
    public static class AIPilotCombatModes_FlyToTarget_Patch
    {
        private static readonly Func<AIPilotCombatModes, Unit> f_getCurrentTarget =
            FastReflection.CreateFieldGetter<AIPilotCombatModes, Unit>("currentTarget");
        private static readonly Func<AIPilotCombatModes, Aircraft> f_getAircraft =
            FastReflection.CreateFieldGetter<AIPilotCombatModes, Aircraft>("aircraft");

        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var getVelocity = AccessTools.PropertyGetter(typeof(Rigidbody), "velocity");
            var leadVelocity = AccessTools.Method(typeof(AIPilotCombatModes_FlyToTarget_Patch), "KnownLeadVelocity");
            var codes = new List<CodeInstruction>(instructions);
            for (int i = 0; i < codes.Count; i++)
            {
                if (getVelocity != null && leadVelocity != null && codes[i].Calls(getVelocity))
                {
                    // Stack: rb -> rb, this -> KnownLeadVelocity(rb, this)
                    var replacement = new CodeInstruction(System.Reflection.Emit.OpCodes.Call, leadVelocity);
                    replacement.labels.AddRange(codes[i].labels);
                    replacement.blocks.AddRange(codes[i].blocks);
                    codes[i] = replacement;
                    codes.Insert(i, new CodeInstruction(System.Reflection.Emit.OpCodes.Ldarg_0));
                    return codes;
                }
            }
            if (CnoEvilPlugin.Log != null)
            {
                CnoEvilPlugin.Log.LogWarning("AIPilotCombatModes_FlyToTarget_Patch: Rigidbody.velocity read not found, vanilla lead kept");
            }
            return codes;
        }

        /// <summary>
        /// Target velocity for the vanilla lead: the faction-known velocity for a mod-tracked hostile threat,
        /// the vanilla true velocity for everything else and while the mod is off.
        /// </summary>
        public static Vector3 KnownLeadVelocity(Rigidbody rb, AIPilotCombatModes self)
        {
            try
            {
                Vector3 trueVel = rb.velocity;
                if (!CnoEvilConfig.IsModActive || self == null || f_getCurrentTarget == null || f_getAircraft == null) return trueVel;

                Unit target = f_getCurrentTarget(self);
                Aircraft aircraft = f_getAircraft(self);
                if (aircraft == null) return trueVel;

                TriangulationTrack track;
                if (!FactionTrackKnowledge.TryGetHostileTrack(aircraft.NetworkHQ, target, out track)) return trueVel;
                return RWRTriangulationProcessor.GetFactionLeadVelocity(track, target);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("AIPilotCombatModes_FlyToTarget_Patch.KnownLeadVelocity", ex);
                return (rb != null) ? rb.velocity : Vector3.zero;
            }
        }
    }

    /// <summary>
    /// Munition audit log: records the detonation point and cause (MunitionAudit). Runs while the mod is off too,
    /// so the log can compare vanilla and mod.
    /// </summary>
    [HarmonyPatch(typeof(Missile), "Detonate")]
    public static class Missile_Detonate_Audit_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(Missile __instance, bool hitArmor, bool hitTerrain)
        {
            try
            {
                if (!MunitionAudit.Enabled) return;
                MunitionAudit.OnDetonate(__instance, hitArmor, hitTerrain);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("Missile_Detonate_Audit_Patch", ex);
            }
        }
    }

    /// <summary>
    /// Launch: the launch dispersion and the audit line. Missile.StartMissile calls Unit.RegisterMissile before
    /// TargetIDChanged resolves Missile.target, so the target is known only after StartMissile.
    /// </summary>
    [HarmonyPatch(typeof(Missile), "StartMissile")]
    public static class Missile_StartMissile_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(Missile __instance)
        {
            if (__instance == null) return;
            try
            {
                Unit target = SeekerDispersionHelper.GetTargetForMissile(__instance);
                if (CnoEvilConfig.IsModActive && target != null && !target.disabled)
                {
                    SeekerDispersionHelper.GetDispersionForMissile(__instance, target);
                }
                // Audit log also while the mod is off (vanilla comparison). Without an owner the missile is never
                // deregistered, so it gets no record.
                if (__instance.owner != null) MunitionAudit.OnLaunch(__instance.owner, __instance, target);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("Missile_StartMissile_Patch", ex);
            }
        }
    }

    /// <summary>
    /// Munition audit log: target changes in flight (Missile.TargetIDChanged).
    /// </summary>
    [HarmonyPatch(typeof(Missile), "TargetIDChanged")]
    public static class Missile_TargetIDChanged_Audit_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(Missile __instance)
        {
            try
            {
                if (!MunitionAudit.Enabled) return;
                MunitionAudit.OnRetarget(__instance, SeekerDispersionHelper.GetTargetForMissile(__instance));
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("Missile_TargetIDChanged_Audit_Patch", ex);
            }
        }
    }

    /// <summary>
    /// Server watchdog: detonates munitions that left the battlefield (MissileMaxBattlefieldRadiusKm, below -150 m,
    /// above 60 km). All other fuzing (miss, lost target, low speed) is vanilla SlowChecks.
    /// </summary>
    [HarmonyPatch(typeof(Missile), "ServerFixedUpdate")]
    public static class Missile_ServerFixedUpdate_Watchdog_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(Missile __instance)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive || __instance == null || __instance.disabled) return;
                if (CnoEvilConfig.MissileEnforceSelfDestructWatchdog == null || !CnoEvilConfig.MissileEnforceSelfDestructWatchdog.Value) return;

                // Strict safety gate: NEVER detonate young or un-armed munitions!
                // Protects launching aircraft, pylon release, canister launch, and low-altitude cruise missiles.
                if (__instance.timeSinceSpawn < 10.0f || !__instance.IsArmed()) return;

                // Throttle checks: run every 10 frames (~0.2s) to maintain zero performance overhead
                if ((Time.frameCount + __instance.GetInstanceID()) % 10 != 0) return;

                // Ballistic missile (Piledriver TBM) flies vanilla: 250 km range and an apogee above the limits below
                MissileSeeker seeker = SeekerDispersionHelper.GetSeeker(__instance);
                if (seeker is BallisticMissileGuidance) return;

                GlobalPosition gPos = __instance.GlobalPosition();
                Vector3 vel = __instance.rb != null ? __instance.rb.velocity : Vector3.zero;

                // 1. Universal Battlefield Boundary, Deep Water & Edge-of-Atmosphere Check
                // Uses true world coordinates (GlobalPosition), completely immune to floating origin offsets!
                float maxRadiusKm = (CnoEvilConfig.MissileMaxBattlefieldRadiusKm != null) ? CnoEvilConfig.MissileMaxBattlefieldRadiusKm.Value : 120.0f;
                float maxRadiusM = maxRadiusKm * 1000.0f;
                float distSq = gPos.x * gPos.x + gPos.z * gPos.z;
                if (distSq > maxRadiusM * maxRadiusM || gPos.y < -150.0f || gPos.y > 60000.0f)
                {
                    __instance.Detonate(vel, false, false);
                    return;
                }
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("Missile_ServerFixedUpdate_Watchdog_Patch", ex);
            }
        }
    }
}
