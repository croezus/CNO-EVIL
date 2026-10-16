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
            if (!CnoEvilConfig.IsModActive || missile == null || __instance == null) return;

            // Track active airborne missile for allied radar surveillance & datalink early warning
            DatalinkNetwork.RegisterMissile(missile);

            // Calculate and register track uncertainty dispersion for the launched munition
            try
            {
                Unit target = SeekerDispersionHelper.GetTargetForMissile(missile);
                if (target != null && !target.disabled)
                {
                    SeekerDispersionHelper.GetDispersionForMissile(missile, target);
                }
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("MissilePatches.RegisterMissile dispersion", ex);
            }

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

            // Deregister launch dispersion offset
            TrackUncertaintyCalculator.UnregisterMissile(missile);

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
    /// AI missile launch doctrine shared by the CombatAI / WeaponStation patches.
    /// </summary>
    internal static class AIDoctrine
    {
        /// <summary>
        /// The launch dispersion the faction's track gives (CEP from Q, sensor range, memory and fix error) must
        /// fall within what the munition still kills: its seeker acquisition radius, its warhead danger radius,
        /// or the vanilla 100 m. Applies only to radar threats the mod tracks; other targets (trucks, buildings, ...)
        /// keep vanilla launch rules. No track on a tracked threat: hold fire.
        /// </summary>
        public static bool MeetsLaunchDoctrine(FactionHQ hq, Unit launcher, Unit target, WeaponInfo weaponInfo)
        {
            if (hq == null || target == null || weaponInfo == null) return true; // No faction: no track knowledge to judge by
            if (!ThreatClassification.IsTrackedThreat(target, hq)) return true;

            Dictionary<Unit, TriangulationTrack> tracks = RWRTriangulationProcessor.GetTrackDictionary(hq);
            TriangulationTrack track;
            if (tracks == null || !tracks.TryGetValue(target, out track) || track == null) return false;

            float launchDistance = (launcher != null) ? Vector3.Distance(launcher.transform.position, target.transform.position) : 0.0f;
            return SeekerDispersionHelper.PredictLaunchCep(track, target, launchDistance) <=
                   MunitionClassification.GetAcceptableAimError(weaponInfo, launchDistance);
        }

        public static float ArtilleryMinLaunchQuality
        {
            get { return CnoEvilConfig.AIArtilleryMinLaunchQuality != null ? CnoEvilConfig.AIArtilleryMinLaunchQuality.Value : 0.9f; }
        }

        /// <summary>
        /// Guided artillery (MunitionClassification.IsGuidedArtillery) fires only when the faction's track of the
        /// target has Q >= AIArtilleryMinLaunchQuality. No track (no Q): hold fire. Such a round gets no track
        /// dispersion (SeekerDispersionHelper.GetDispersionForMissile).
        /// </summary>
        public static bool MeetsArtilleryLaunchDoctrine(FactionHQ hq, Unit target)
        {
            if (hq == null || target == null) return true; // No faction: no track knowledge to judge by

            Dictionary<Unit, TriangulationTrack> tracks = RWRTriangulationProcessor.GetTrackDictionary(hq);
            TriangulationTrack track;
            if (tracks == null || !tracks.TryGetValue(target, out track) || track == null) return false;
            return track.TrackingQuality >= ArtilleryMinLaunchQuality;
        }
    }

    /// <summary>
    /// Removes targets below the munition's required tracking quality from AI missile target lists.
    /// </summary>
    [HarmonyPatch(typeof(CombatAI), "LookForMissileTargets")]
    public static class CombatAI_LookForMissileTargets_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(Aircraft aircraft, WeaponStation weaponStation, List<Unit> outTargets, ref int __result)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive) return;
                if (CnoEvilConfig.AIEvaluateMissileLaunchDoctrine == null || !CnoEvilConfig.AIEvaluateMissileLaunchDoctrine.Value) return;
                if (aircraft == null || weaponStation == null || weaponStation.WeaponInfo == null || outTargets == null || outTargets.Count == 0) return;

                // Never restrict human player manual weapon selection/firing
                if (CombatHUD.i != null && CombatHUD.i.aircraft == aircraft) return;

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
    /// Missile launch doctrine for all AI platforms (SAM sites, ships, vehicles, aircraft): hold fire below the
    /// munition's required tracking quality.
    /// </summary>
    [HarmonyPatch(typeof(WeaponStation), "Fire")]
    public static class WeaponStation_Fire_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(WeaponStation __instance, Unit owner, Unit target)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive) return true;
                if (CnoEvilConfig.AIEvaluateMissileLaunchDoctrine == null || !CnoEvilConfig.AIEvaluateMissileLaunchDoctrine.Value) return true;
                if (__instance.WeaponInfo == null || target == null || owner == null) return true;

                // Never block the local human player's manual fire commands
                if (CombatHUD.i != null && CombatHUD.i.aircraft == owner) return true;

                if (MunitionClassification.IsGuidedArtillery(__instance.WeaponInfo))
                {
                    return AIDoctrine.MeetsArtilleryLaunchDoctrine(owner.NetworkHQ, target);
                }
                if (!__instance.WeaponInfo.missile) return true;

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
    /// Turret target choice: a turret armed only with guided artillery skips candidates that fail
    /// AIDoctrine.MeetsArtilleryLaunchDoctrine, so it does not hold a target it may not fire at.
    /// </summary>
    [HarmonyPatch(typeof(Turret), "AssessTargetPriority")]
    public static class Turret_AssessTargetPriority_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(Unit targetCandidate, Unit ___attachedUnit, WeaponStation[] ___weaponStations)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive) return true;
                if (CnoEvilConfig.AIEvaluateMissileLaunchDoctrine == null || !CnoEvilConfig.AIEvaluateMissileLaunchDoctrine.Value) return true;
                if (targetCandidate == null || ___attachedUnit == null || ___weaponStations == null || ___weaponStations.Length == 0) return true;
                if (CombatHUD.i != null && CombatHUD.i.aircraft == ___attachedUnit) return true;

                for (int i = 0; i < ___weaponStations.Length; i++)
                {
                    WeaponStation st = ___weaponStations[i];
                    if (st == null || !MunitionClassification.IsGuidedArtillery(st.WeaponInfo)) return true;
                }
                return AIDoctrine.MeetsArtilleryLaunchDoctrine(___attachedUnit.NetworkHQ, targetCandidate);
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
    /// (MunitionClassification.GetAcceptableAimError): seeker acquisition radius, warhead danger radius, or 100 m.
    /// The launch dispersion is gated separately (AIDoctrine.MeetsLaunchDoctrine).
    /// </summary>
    [HarmonyPatch(typeof(AIPilotCombatModes), "ManageTarget")]
    public static class AIPilotCombatModes_ManageTarget_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(Aircraft ___aircraft, Unit ___currentTarget, WeaponInfo ___currentWeaponInfo, float ___targetDist, ref bool ___targetAccurate)
        {
            try
            {
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

                float allowed = MunitionClassification.GetAcceptableAimError(___currentWeaponInfo, ___targetDist);
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
    /// Active server-side watchdog that terminates munitions that have missed their target,
    /// overshot ballistic apogee without hitting, or exceeded maximum battlefield tactical boundaries.
    /// </summary>
    [HarmonyPatch(typeof(Missile), "ServerFixedUpdate")]
    public static class Missile_ServerFixedUpdate_Watchdog_Patch
    {
        private static readonly Func<ARMSeeker, Radar> f_getArmRadar = FastReflection.CreateFieldGetter<ARMSeeker, Radar>("targetedRadar");
        private static readonly Func<ARMSeeker, GlobalPosition> f_getArmPos = FastReflection.CreateFieldGetter<ARMSeeker, GlobalPosition>("knownPos");

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

                // 2. ARM Anti-Radiation Seeker Miss Watchdog (ARAD-116, ARAD-45)
                {
                    ARMSeeker arm = seeker as ARMSeeker;
                    if (arm != null)
                    {
                        if (__instance.timeSinceSpawn > 25.0f && !__instance.EngineOn())
                        {
                            Radar targeted = (f_getArmRadar != null) ? f_getArmRadar(arm) : null;
                            if (targeted == null && f_getArmPos != null)
                            {
                                GlobalPosition targetG = f_getArmPos(arm);
                                if (targetG != default(GlobalPosition))
                                {
                                    Vector3 toTarget = targetG - gPos;
                                    // Only detonate if missile overshot the last known radar coordinate by more than 3 km:
                                    if (Vector3.Dot(toTarget, vel) < 0f && toTarget.sqrMagnitude > 9000000.0f)
                                    {
                                        __instance.Detonate(vel, false, false);
                                        return;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("Missile_ServerFixedUpdate_Watchdog_Patch", ex);
            }
        }
    }
}
