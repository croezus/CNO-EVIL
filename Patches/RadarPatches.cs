using System;
using HarmonyLib;
using UnityEngine;
using CnoEvil.Core;
using CnoEvil.Config;
using CnoEvil.Components;
using NuclearOption.Jobs;

namespace CnoEvil.Patches
{
    [HarmonyPatch(typeof(Radar), "CanSeeRadarReturn")]
    public static class Radar_CanSeeRadarReturn_Patch
    {
        private static readonly System.Func<CombatHUD, System.Collections.Generic.List<Unit>> s_getTargetList =
            FastReflection.CreateFieldGetter<CombatHUD, System.Collections.Generic.List<Unit>>("targetList");

        private static bool IsGuidingMissileAt(System.Collections.Generic.List<Missile> guidedMissiles, Unit target)
        {
            if (guidedMissiles == null) return false;
            for (int i = 0; i < guidedMissiles.Count; i++)
            {
                Missile m = guidedMissiles[i];
                if (m != null && !m.disabled && SeekerDispersionHelper.GetTargetForMissile(m) == target) return true;
            }
            return false;
        }

        /// <summary>
        /// Telemetry label of an emitting platform, from its unit type and definition (not its display name).
        /// </summary>
        private static string ClassifyEmitterPlatform(Unit emitterUnit)
        {
            if (emitterUnit == null) return "Ground SAM";
            if (emitterUnit is Aircraft) return "Airborne";
            if (emitterUnit is Ship) return "Naval";

            UnitDefinition def = emitterUnit.definition;
            if (def != null && (def.code == "SAM" || def.code == "AA" || def.roleIdentity.antiAir > 0.5f))
            {
                return "Ground SAM";
            }
            return "Ground / Station";
        }

        [HarmonyPrefix]
        public static bool Prefix(
            Radar __instance,
            IRadarReturn radarReturn,
            float dist,
            float clutterFactor,
            ref bool __result,
            System.Collections.Generic.List<Missile> ___guidedMissiles)
        {
            // A/B Toggle: If mod is disabled, run original vanilla calculation!
            if (!CnoEvilConfig.IsModActive)
            {
                return true;
            }

            long perf = PerfStats.Begin();
            try
            {
                return Evaluate(__instance, radarReturn, dist, ref __result, ___guidedMissiles);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("Radar_CanSeeRadarReturn_Patch", ex);
                return true;
            }
            finally
            {
                PerfStats.End(PerfSlot.RadarCheck, perf);
            }
        }

        private static bool Evaluate(
            Radar __instance,
            IRadarReturn radarReturn,
            float dist,
            ref bool __result,
            System.Collections.Generic.List<Missile> ___guidedMissiles)
        {
            if (__instance == null || radarReturn == null)
            {
                __result = false;
                return false;
            }

            Transform scanner = __instance.GetScanPoint();
            Vector3 radarPos = scanner != null ? scanner.position : __instance.transform.position;
            Vector3 radarVel = __instance.GetVelocity();

            // Extract target Unit or Aircraft
            Unit targetUnit = radarReturn as Unit;
            if (targetUnit == null)
            {
                Component comp = radarReturn as Component;
                if (comp != null) targetUnit = comp.GetComponentInParent<Unit>();
            }

            if (targetUnit == null || targetUnit.disabled)
            {
                __result = false;
                return false;
            }

            // Radar targets are aircraft and missiles only (the vanilla IRadarReturn types); ships and vehicles never get here.

            Vector3 targetPos = targetUnit.transform.position;
            Vector3 targetVel = targetUnit.rb != null ? targetUnit.rb.velocity : Vector3.zero;
            Unit radarUnit = __instance.GetAttachedUnit();
            bool debugActive = CnoEvilConfig.RadarTelemetryWanted;

            // Aspect RCS, ECM, Doppler notch, radar equation, jamming, clutter -> SNR. No linecast: evaluated first,
            // so a target below the SNR threshold or in the notch costs no path check (the debug telemetry needs both)
            RadarSkinSignal sig = RadarSignalModel.EvaluateRadar(__instance, radarReturn, targetUnit, radarPos, radarVel, dist);
            if (!debugActive && (sig.SnrDb < CnoEvilConfig.EffectiveMinSNR || sig.Doppler.TrackRejectedByNotch))
            {
                __result = false;
                return false;
            }

            // Line of sight: 4/3 refracted path over the curved Earth, the sea surface, terrain and ships
            bool horizonOccluded = false;
            float targetClearRadius = Mathf.Max(5.0f, targetUnit.maxRadius * 1.5f);
            bool byCurvature;
            if (RadarPhysics.IsRadarPathBlocked(radarPos, targetPos, targetClearRadius, debugActive, out byCurvature))
            {
                // Terrain masks always stop here; a horizon mask continues only to fill the debug telemetry
                if (!byCurvature || !debugActive)
                {
                    __result = false;
                    return false;
                }
                horizonOccluded = true;
            }

            float dynamicRcs = sig.DynamicRcs;
            float storesRcs = sig.StoresRcs;
            float ecmIntensity = sig.EcmIntensity;
            bool jammerActive = sig.JammerActive;
            float echoPower = sig.EchoPower;
            float noiseFloor = sig.NoiseFloor;
            float clutterPower = sig.ClutterPower;
            float jammerPower = sig.JammerPower;
            float rBurn = sig.BurnRange;
            float snrDb = sig.SnrDb;
            bool snrPass = snrDb >= CnoEvilConfig.EffectiveMinSNR;

            bool detected = snrPass && !sig.Doppler.TrackRejectedByNotch && !horizonOccluded;

            // Compute physical illumination state (Missile Guidance, Hard Lock, Tracking, Search)
            RadarIlluminationState state = RadarIlluminationState.Search;
            if (IsGuidingMissileAt(___guidedMissiles, targetUnit))
            {
                // This radar guides a missile against this very target (SARH illumination / datalink support)
                state = RadarIlluminationState.MissileGuidance;
            }
            else if (__instance.CheckIsTarget(targetUnit))
            {
                // Single-target track: the radar has this target locked
                state = RadarIlluminationState.HardLock;
            }
            else if (snrDb >= CnoEvilConfig.EffectiveMinSNR + 3.0f)
            {
                state = RadarIlluminationState.Tracking;
            }

            // Stream to telemetry HUD if this radar is connected to the player's aircraft or targeting player.
            // Only the debug overlays (F9 / F11) read the telemetry.
            bool isPlayerInvolved = false;
            int priority = 0;
            try
            {
                if (debugActive && CombatHUD.i != null && CombatHUD.i.aircraft != null)
                {
                    Unit playerUnit = CombatHUD.i.aircraft;
                    bool isPlayerRadar = (radarUnit == playerUnit);
                    bool isTargetingPlayer = (targetUnit == playerUnit);

                    if (isPlayerRadar)
                    {
                        isPlayerInvolved = true;
                        // Prioritize player's selected/designated target on CombatHUD
                        System.Collections.Generic.List<Unit> tList = (s_getTargetList != null) ? s_getTargetList(CombatHUD.i) : null;
                        bool isDesignatedTarget = (tList != null && tList.Count > 0 && tList[0] == targetUnit);

                        if (isDesignatedTarget)
                        {
                            priority = 5000;
                        }
                        else if (detected)
                        {
                            priority = 1000 - (int)Mathf.Clamp(dist * 0.01f, 0.0f, 900.0f);
                        }
                        else
                        {
                            priority = 5; // Very low priority for occluded/undetected background targets
                        }
                    }
                    else if (isTargetingPlayer)
                    {
                        // Incoming threat targeting the player
                        isPlayerInvolved = true;
                        priority = detected ? 50 : 1;

                        if (detected)
                        {
                            Unit emitterUnit = radarUnit;
                            string platformType = ClassifyEmitterPlatform(emitterUnit);

                            RadarTelemetry.RegisterIllumination(
                                __instance.GetInstanceID(),
                                (emitterUnit != null && !string.IsNullOrEmpty(emitterUnit.unitName)) ? emitterUnit.unitName : __instance.name,
                                platformType,
                                radarPos,
                                __instance.transform.forward,
                                dist,
                                snrDb,
                                echoPower,
                                state
                            );
                        }
                    }
                }
            }
            catch
            {
                isPlayerInvolved = false;
            }

            if (isPlayerInvolved)
            {
                RadarTelemetry.UpdateTelemetry(
                    radarPos,
                    radarVel,
                    targetPos,
                    targetVel,
                    targetUnit.unitName,
                    dist,
                    echoPower,
                    noiseFloor,
                    clutterPower,
                    jammerPower,
                    snrDb,
                    CnoEvilConfig.EffectiveMinSNR,
                    dynamicRcs,
                    storesRcs,
                    ref sig.Doppler,
                    rBurn,
                    jammerActive,
                    ecmIntensity,
                    horizonOccluded,
                    priority
                );
            }

            // Trigger warning on target if detected
            if (detected)
            {
                // Register active radar illumination for tracking quality accumulation & PIP refinement
                try
                {
                    Unit hqUnit = radarUnit;
                    if (hqUnit == null) hqUnit = __instance.GetComponentInParent<Unit>();
                    FactionHQ radarHq = (hqUnit != null) ? hqUnit.NetworkHQ : null;
                    if (ThreatClassification.IsTrackedThreat(targetUnit, radarHq))
                    {
                        if (radarHq != null)
                        {
                            RWRTriangulationProcessor.RecordRadarIllumination(radarHq, targetUnit, __instance);
                        }
                        else
                        {
                            RWRTriangulationProcessor.RecordRadarIllumination(targetUnit, __instance);
                        }
                    }
                }
                catch { }

                // A mirror client gets the RWR picture of its aircraft from the host (the host runs every radar)
                Aircraft targetAircraft = targetUnit as Aircraft;
                if (targetAircraft != null && !CnoEvilNet.IsMirrorClient)
                {
                    PhysRWRReceiver rwr = PhysRWRReceiver.Get(targetAircraft);
                    if (rwr != null)
                    {
                        RwrThreatState rwrState = RwrThreatState.Search;
                        if (state == RadarIlluminationState.MissileGuidance)
                        {
                            rwrState = RwrThreatState.MissileGuidance;
                        }
                        else if (state == RadarIlluminationState.HardLock)
                        {
                            rwrState = RwrThreatState.Track;
                        }

                        rwr.RegisterRadarPing(radarUnit, __instance, rwrState, RadarSignalModel.RadarPingSignal01(snrDb), false);
                    }

                    // For a human player (local or remote), ensure OnRadarWarning fires so map and TacScreen display pings
                    if (SeekerDispersionHelper.ShouldSendRadarWarning(targetAircraft))
                    {
                        SeekerDispersionHelper.SendRadarWarning(targetAircraft, radarUnit);
                    }
                }
            }

            __result = detected;
            return false; // Skip vanilla CanSeeRadarReturn
        }
    }

    /// <summary>
    /// Vanilla RequestRadarCheck drops a target before the range check when the horizontal distance is more than
    /// sqrt(2 RE h_radar) + sqrt(2 RE h_target) (k = 1, heights above sea level). That gate comes before
    /// Radar.CanSeeRadarReturn, so the transpiler sets its 2 * RE to RadarPhysics.HorizonTwoRadius (2 * k * RE with
    /// 4/3 refraction while the mod is active). The smooth sea-level sphere is the smallest possible obstruction, so the
    /// gate stays a valid early reject; the terrain path check is in RadarPhysics.IsRadarPathBlocked.
    /// The vanilla clutter term in the same method uses the same horizon distance and also moves to 4/3.
    /// </summary>
    [HarmonyPatch(typeof(DetectorManager), "RequestRadarCheck")]
    public static class DetectorManager_RequestRadarCheck_Patch
    {
        [HarmonyTranspiler]
        public static System.Collections.Generic.IEnumerable<CodeInstruction> Transpiler(System.Collections.Generic.IEnumerable<CodeInstruction> instructions)
        {
            var twoRadius = AccessTools.Method(typeof(RadarPhysics), "HorizonTwoRadius");
            var codes = new System.Collections.Generic.List<CodeInstruction>(instructions);
            var hits = new System.Collections.Generic.List<int>();
            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == System.Reflection.Emit.OpCodes.Ldc_R4 && codes[i].operand is float &&
                    (float)codes[i].operand == RadarPhysics.VanillaTwoEarthRadius)
                {
                    hits.Add(i);
                }
            }

            if (twoRadius == null || hits.Count != 2)
            {
                if (CnoEvilPlugin.Log != null)
                {
                    CnoEvilPlugin.Log.LogWarning(string.Format(
                        "DetectorManager_RequestRadarCheck_Patch: expected 2 horizon constants, found {0}; vanilla k = 1 horizon kept", hits.Count));
                }
                return codes;
            }

            for (int h = 0; h < hits.Count; h++)
            {
                CodeInstruction old = codes[hits[h]];
                var replacement = new CodeInstruction(System.Reflection.Emit.OpCodes.Call, twoRadius);
                replacement.labels.AddRange(old.labels);
                replacement.blocks.AddRange(old.blocks);
                codes[hits[h]] = replacement;
            }
            return codes;
        }
    }
}
