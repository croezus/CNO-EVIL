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

        private enum MissileLink { None, TargetId, Guiding }

        /// <summary>
        /// One pass over the radar's guided missiles. Guiding: a live missile guided by this radar flies at the target
        /// (SARH illumination / datalink support). TargetId: vanilla Radar.CheckIsTarget (a guided missile carries this
        /// target's ID) without its null dereference: guidedMissiles is created in Radar.Awake, so a radar whose Awake
        /// has not run has none.
        /// </summary>
        private static MissileLink GetMissileLink(System.Collections.Generic.List<Missile> guidedMissiles, Unit target)
        {
            if (guidedMissiles == null) return MissileLink.None;
            MissileLink link = MissileLink.None;
            for (int i = 0; i < guidedMissiles.Count; i++)
            {
                Missile m = guidedMissiles[i];
                if (m == null) continue;
                if (!m.disabled && SeekerDispersionHelper.GetTargetForMissile(m) == target) return MissileLink.Guiding;
                if (m.targetID == target.persistentID) link = MissileLink.TargetId;
            }
            return link;
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

            try
            {
                return Evaluate(__instance, radarReturn, dist, clutterFactor, ref __result, ___guidedMissiles);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("Radar_CanSeeRadarReturn_Patch", ex);
                return true;
            }
        }

        private static bool Evaluate(
            Radar __instance,
            IRadarReturn radarReturn,
            float dist,
            float clutterFactor,
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
            ShipRadarReturn shipReturn = radarReturn as ShipRadarReturn;
            Unit targetUnit = (shipReturn != null) ? (Unit)shipReturn.Ship : radarReturn as Unit;
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

            // Radar targets are aircraft and missiles (the vanilla IRadarReturn types) and ships (ShipRadarReturn,
            // queued by Radar_RadarCheck_Patch); ground vehicles never get here.

            // A scanned missile receives the radar one way, before any detection threshold: the vanilla call raises
            // Missile.onRadarPing, which is the only way an ARM seeker finds a radar it was not launched at.
            // Missiles only: the Aircraft implementation sends the vanilla RWR warning.
            if (radarReturn is Missile)
            {
                radarReturn.GetRadarReturn(radarPos, __instance, __instance.GetAttachedUnit(), dist, clutterFactor, __instance.RadarParameters, true);
            }

            Vector3 targetPos = targetUnit.transform.position;
            Vector3 targetVel = targetUnit.rb != null ? targetUnit.rb.velocity : Vector3.zero;
            Unit radarUnit = __instance.GetAttachedUnit();
            bool debugActive = CnoEvilConfig.RadarTelemetryWanted;

            // Aspect RCS, ECM, Doppler notch, radar equation, jamming, clutter -> SNR. No linecast: evaluated first,
            // so a target below the SNR threshold or in the notch costs no path check (the debug telemetry needs both)
            RadarSkinSignal sig = RadarSignalModel.EvaluateRadar(__instance, radarReturn, targetUnit, radarPos, radarVel, dist);

            // Painted but below the detection threshold: the inward dip of the EM scan ring of a human player.
            // The vanilla terrain raycast and horizon gate already ran before this prefix.
            if (sig.SnrDb < CnoEvilConfig.EffectiveMinSNR && !CnoEvilNet.IsMirrorClient)
            {
                Aircraft paintedAircraft = targetUnit as Aircraft;
                if (paintedAircraft != null && SeekerDispersionHelper.ShouldSendRadarWarning(paintedAircraft))
                {
                    PhysRWRReceiver rwr = PhysRWRReceiver.Get(paintedAircraft);
                    if (rwr != null)
                    {
                        RwrThreatState paintState = GetMissileLink(___guidedMissiles, targetUnit) == MissileLink.Guiding ? RwrThreatState.MissileGuidance : RwrThreatState.Search;
                        rwr.RegisterBelowThreshold(radarUnit, paintState, RadarSignalModel.BelowThresholdDepth01(sig.SnrDb), false);
                    }
                }
            }

            // Active decoy in the track gate of a radar that guides a missile at the target or holds a missile lock on it
            // (RadarDecoySeduction): the radar tracks the decoy, not the target, and its faction track gets a false fix.
            // A new capture needs a target track; a held capture continues while the decoy transmits.
            bool decoyCaptured = false;
            if (RadarDecoySeduction.Active && GetMissileLink(___guidedMissiles, targetUnit) != MissileLink.None)
            {
                ChaffCloud decoy;
                bool targetTracked = sig.SnrDb >= CnoEvilConfig.EffectiveMinSNR && !sig.Doppler.TrackRejectedByNotch;
                if ((targetTracked || RadarDecoySeduction.TryGetCapture(__instance, targetUnit, out decoy)) &&
                    RadarDecoySeduction.Evaluate(__instance, targetUnit, radarPos, targetPos, sig.DynamicRcs, out decoy))
                {
                    decoyCaptured = true;
                    RWRTriangulationProcessor.RecordDecoyContact(radarUnit != null ? radarUnit.NetworkHQ : null, targetUnit, decoy, __instance);
                    if (!debugActive)
                    {
                        __result = false;
                        return false;
                    }
                }
            }

            if (!debugActive && (sig.SnrDb < CnoEvilConfig.EffectiveMinSNR || sig.Doppler.TrackRejectedByNotch))
            {
                __result = false;
                return false;
            }

            // Line of sight: 4/3 refracted path over the curved Earth, the sea surface, terrain and ships.
            // A ship is seen from the top of its superstructure.
            bool horizonOccluded = false;
            float targetClearRadius = Mathf.Max(5.0f, targetUnit.maxRadius * 1.5f);
            Vector3 losTargetPos = (shipReturn != null) ? shipReturn.GetAimPoint() : targetPos;
            bool byCurvature;
            if (RadarPhysics.IsRadarPathBlocked(radarPos, losTargetPos, targetClearRadius, debugActive, out byCurvature))
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

            bool detected = snrPass && !sig.Doppler.TrackRejectedByNotch && !horizonOccluded && !decoyCaptured;

            // Compute physical illumination state (Missile Guidance, Hard Lock, Tracking, Search)
            RadarIlluminationState state = RadarIlluminationState.Search;
            MissileLink missileLink = GetMissileLink(___guidedMissiles, targetUnit);
            if (missileLink == MissileLink.Guiding)
            {
                // This radar guides a missile against this very target (SARH illumination / datalink support)
                state = RadarIlluminationState.MissileGuidance;
            }
            else if (missileLink == MissileLink.TargetId)
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

                    // Vanilla OnRadarWarning: map and TacScreen pings of a player, the descent of an AI pilot
                    SeekerDispersionHelper.SendRadarWarning(targetAircraft, radarUnit);
                }
            }

            __result = detected;
            return false; // Skip vanilla CanSeeRadarReturn
        }
    }

    /// <summary>
    /// Ships as radar targets. Vanilla Radar.RadarCheck requests only units with IRadarReturn (aircraft, missiles).
    /// This postfix queues every hostile ship with the same filters (other HQ, 2 x maxRange, radar cone), through the
    /// vanilla DetectionRequest job: terrain raycast, Radar.CanSeeRadarReturn (mod physics), TargetDetector.DetectTarget.
    /// The horizon gate uses the top of the superstructure, not the hull reference point of the vanilla
    /// RequestRadarCheck gate. F10 restores vanilla (no ship returns).
    /// </summary>
    [HarmonyPatch(typeof(Radar), "RadarCheck")]
    public static class Radar_RadarCheck_Patch
    {
        private static readonly System.Func<DetectorManager, System.Collections.Generic.List<DetectionRequest>> s_getLoSRequests =
            FastReflection.CreateFieldGetter<DetectorManager, System.Collections.Generic.List<DetectionRequest>>("LoSRequests");

        [HarmonyPostfix]
        public static void Postfix(Radar __instance, float ___radarCone)
        {
            if (!CnoEvilConfig.IsModActive) return;

            try
            {
                QueueShips(__instance, ___radarCone);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("Radar_RadarCheck_Patch", ex);
            }
        }

        private static void QueueShips(Radar radar, float radarCone)
        {
            System.Collections.Generic.List<ShipRadarReturn> ships = ShipRadarReturn.All;
            if (radar == null || ships.Count == 0) return;

            Unit radarUnit = radar.GetAttachedUnit();
            FactionHQ radarHq = (radarUnit != null) ? radarUnit.NetworkHQ : null;
            Transform scanner = radar.GetScanPoint();
            if (scanner == null) return;

            JobManager jobs = SceneSingleton<JobManager>.i;
            if (jobs == null || jobs.detector == null) return;
            System.Collections.Generic.List<DetectionRequest> requests = (s_getLoSRequests != null) ? s_getLoSRequests(jobs.detector) : null;

            Vector3 scanPos = scanner.position;
            Vector3 scanForward = scanner.forward;
            float maxRange = radar.RadarParameters.maxRange * 2.0f;
            float maxRangeSqr = maxRange * maxRange;
            float seaY = Datum.LocalSeaY;

            for (int i = 0; i < ships.Count; i++)
            {
                ShipRadarReturn entry = ships[i];
                Ship ship = entry.Ship;
                if (ship == null || ship.disabled || ship == radarUnit) continue;

                FactionHQ shipHq = ship.NetworkHQ;
                if (shipHq == null || shipHq == radarHq) continue;

                Vector3 shipPos = ship.transform.position;
                Vector3 toShip = shipPos - scanPos;
                if (toShip.sqrMagnitude > maxRangeSqr) continue;
                if (radarCone > 0.0f && Vector3.Angle(toShip, scanForward) > radarCone) continue;

                // Smooth-sea horizon (4/3 Earth with the mod curvature setting) from the superstructure top
                Vector3 aim = entry.GetAimPoint();
                float horizon = Mathf.Sqrt(RadarPhysics.HorizonTwoRadius() * Mathf.Max(0.0f, scanPos.y - seaY)) +
                                Mathf.Sqrt(RadarPhysics.HorizonTwoRadius() * Mathf.Max(0.0f, aim.y - seaY));
                float hx = toShip.x;
                float hz = toShip.z;
                if (hx * hx + hz * hz > horizon * horizon) continue;

                if (requests != null)
                {
                    requests.Add(new DetectionRequest(radar, ship, entry, toShip.magnitude, 0.0f));
                }
                else
                {
                    DetectorManager.RequestRadarCheck(radar, ship, entry);
                }
            }
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
