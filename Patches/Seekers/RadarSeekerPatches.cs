using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using CnoEvil.Core;
using CnoEvil.Config;
using CnoEvil.Components;

namespace CnoEvil.Patches
{
    [HarmonyPatch(typeof(ARHSeeker), "GetRadarReturn")]
    public static class ARHSeeker_GetRadarReturn_Patch
    {
        private static readonly System.Collections.Generic.Dictionary<int, MemoryCoastState> _arhMemoryCoastTimers = new System.Collections.Generic.Dictionary<int, MemoryCoastState>();

        public static void ClearCoastTimer(int missileId)
        {
            _arhMemoryCoastTimers.Remove(missileId);
        }

        public static void ClearAllCoastTimers()
        {
            _arhMemoryCoastTimers.Clear();
        }

        [HarmonyPrefix]
        public static bool Prefix(
            ARHSeeker __instance,
            ref float __result,
            ref float ___lastActiveTrackAttempt,
            ref float ___returnStrength,
            ref float ___targetDist,
            ref bool ___isJammed,
            bool ___homeOnJam,
            Unit ___targetUnit,
            Missile ___missile,
            ref float ___timeWithoutReturn,
            ref bool ___radarLockEstablished,
            float ___terminalRange,
            GlobalPosition ___knownPos)
        {
            // A/B Toggle: If mod is disabled, run original vanilla calculation!
            if (!CnoEvilConfig.IsModActive)
            {
                return true;
            }

            if (__instance == null)
            {
                __result = 0.0f;
                return false;
            }

            float lastAttempt = ___lastActiveTrackAttempt;
            float now = Time.timeSinceLevelLoad;
            if (now - lastAttempt < 0.25f)
            {
                __result = ___returnStrength;
                return false;
            }
            ___lastActiveTrackAttempt = now;

            Unit target = ___targetUnit;
            Missile missile = ___missile;

            if (target == null || target.disabled || missile == null)
            {
                ___returnStrength = 0.0f;
                __result = 0.0f;
                return false;
            }

            Vector3 missilePos = missile.transform.position;
            Vector3 missileVel = missile.rb != null ? missile.rb.velocity : Vector3.zero;
            Vector3 targetPos = target.transform.position;
            Vector3 targetVel = target.rb != null ? target.rb.velocity : Vector3.zero;

            float dist = Vector3.Distance(missilePos, targetPos);
            ___targetDist = dist;

            // Seeker activation: before lock, the seeker transmits only within terminalRange of the datum
            // (vanilla DatalinkMode gate; ARHSeeker_Initialize_Patch applies ARHSeekerActivationRange_m).
            bool lockEstablished = ___radarLockEstablished;
            float distToDatum = Vector3.Distance(missilePos, ___knownPos.ToLocalPosition());

            if (!lockEstablished && distToDatum > ___terminalRange)
            {
                ___returnStrength = 0.0f;
                __result = 0.0f;
                return false;
            }

            // 0-1. Seeker line of sight: 4/3 refracted path over the curved Earth, the sea surface, terrain and ships
            bool seekerByCurvature;
            if (RadarPhysics.IsRadarPathBlocked(missilePos, targetPos, Mathf.Max(5.0f, target.maxRadius * 1.5f), false, out seekerByCurvature))
            {
                ___returnStrength = 0.0f;
                __result = 0.0f;
                return false;
            }

            // 2-4. Aspect RCS & HERM, ECM, Doppler notch (expanded by ECM Doppler smearing / VGPO)
            float dynamicRcs;
            float ecmIntensity;
            DopplerResult doppler = RadarSignalModel.EvaluateArhDoppler(missilePos, missileVel, target, out dynamicRcs, out ecmIntensity);

            if (doppler.TrackRejectedByNotch)
            {
                int missileId = missile.GetInstanceID();
                float nowTime = Time.time;
                GlobalPosition targetPosG = target.GlobalPosition();
                MemoryCoastState coast;
                if (!_arhMemoryCoastTimers.TryGetValue(missileId, out coast))
                {
                    coast = MemoryCoastState.Begin(nowTime, targetPosG, targetVel);
                    _arhMemoryCoastTimers[missileId] = coast;
                }

                float coastDuration = CnoEvilConfig.MissileMemoryCoast_s != null ? CnoEvilConfig.MissileMemoryCoast_s.Value : 2.5f;
                bool enableCoast = CnoEvilConfig.EnableMissileMemoryCoasting == null || CnoEvilConfig.EnableMissileMemoryCoasting.Value;

                float predictionError = 0.0f;
                bool coastHolds = enableCoast && ElectronicWarfare.EvaluateMemoryCoast(
                    ref coast, nowTime, targetPosG, dist, ecmIntensity, doppler.HasClutterBackground, coastDuration, out predictionError);

                if (enableCoast && !coastHolds && !coast.Spent && CnoEvilConfig.DebugLogging.Value && CnoEvilPlugin.Log != null)
                {
                    CnoEvilPlugin.Log.LogInfo(string.Format("ARH memory coast lost after {0:F2}s (prediction error {1:F0} m, range {2:F0} m)",
                        nowTime - coast.StartTime, predictionError, dist));
                }

                if (coastHolds)
                {
                    // Missile guidance computer is in Memory Coasting mode!
                    // Maintains lead pursuit on dead-reckoning extrapolation; retains lock
                    ___returnStrength = 0.4f;
                    __result = 0.4f;
                    return false;
                }

                // The target held the notch longer than the coast: no return. The vanilla seeker logic
                // (timeWithoutReturn, lockPerseverance) loses or reacquires the target. The spent state stays until
                // the target leaves the notch, so the coast does not restart.
                coast.Spent = true;
                _arhMemoryCoastTimers[missileId] = coast;
                ___returnStrength = 0.0f;
                __result = 0.0f;
                return false;
            }
            else
            {
                _arhMemoryCoastTimers.Remove(missile.GetInstanceID());
            }

            // 5. Ku-Band active seeker power calculation (calibrated Tier 0)
            float echoPower;
            float snrDb = RadarSignalModel.EvaluateArhSnrDb(missile, targetPos, dist, dynamicRcs, ecmIntensity, ref doppler, out echoPower);

            // 6. Home-On-Jam (HOJ) and Tracking Evaluation
            // HOJ is strictly enabled ONLY when target is flying straight (NOT in notch gate)
            bool allowHoj = ___homeOnJam ||
                            (CnoEvilConfig.HOJEnabled != null && CnoEvilConfig.HOJEnabled.Value);
            bool isHojHoming = allowHoj && ElectronicWarfare.EvaluateHOJGuidance(
                ecmIntensity,
                doppler.IsInsideClutterNotch,
                dist
            );

            bool skinReturnValid = snrDb >= CnoEvilConfig.EffectiveMinSNR;
            bool targetTracked = skinReturnValid || isHojHoming;

            // Transmitting seeker below its threshold: inward dip of the red EM scan line of a human target
            if (!targetTracked && !CnoEvilNet.IsMirrorClient)
            {
                Aircraft paintedAircraft = target as Aircraft;
                if (paintedAircraft != null &&
                    SeekerDispersionHelper.ShouldSendRadarWarning(paintedAircraft))
                {
                    PhysRWRReceiver rwr = PhysRWRReceiver.Get(paintedAircraft);
                    if (rwr != null)
                    {
                        rwr.RegisterBelowThreshold(missile, RwrThreatState.MissileGuidance, RadarSignalModel.BelowThresholdDepth01(snrDb), true);
                    }
                }
            }

            if (targetTracked)
            {
                // Skin return, or tracking exclusively via Home-On-Jam angle strobe
                float strength = RadarSignalModel.ArhSeekerStrength(snrDb, ecmIntensity, isHojHoming);
                if (!skinReturnValid) ___isJammed = false;

                ___returnStrength = strength;
                __result = strength;

                // Trigger missile terminal warning on target
                Aircraft targetAircraft = target as Aircraft;
                if (targetAircraft != null)
                {
                    // Midcourse the seeker is off (the activation gate above returns first); from activation on it
                    // transmits, so the target RWR hears it
                    PhysRWRReceiver rwr = PhysRWRReceiver.Get(targetAircraft);
                    if (rwr != null)
                    {
                        rwr.RegisterRadarPing(missile, null, RwrThreatState.MissileGuidance, RadarSignalModel.SeekerPingSignal01(strength), true);
                    }

                    if (SeekerDispersionHelper.ShouldSendRadarWarning(targetAircraft))
                    {
                        SeekerDispersionHelper.SendRadarWarning(targetAircraft, missile);
                    }

                    if (CnoEvilConfig.RadarTelemetryWanted && CombatHUD.i != null && targetAircraft == CombatHUD.i.aircraft)
                    {
                        RadarTelemetry.RegisterIllumination(
                            missile != null ? missile.GetInstanceID() : __instance.GetInstanceID(),
                            missile != null ? missile.unitName : "ARH Active Missile Seeker",
                            "Missile Seeker (ARH)",
                            missilePos,
                            missile != null ? missile.transform.forward : Vector3.forward,
                            dist,
                            snrDb,
                            echoPower,
                            RadarIlluminationState.MissileGuidance
                        );
                    }
                }
            }
            else
            {
                ___returnStrength = 0.0f;
                __result = 0.0f;
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(SARHSeeker), "GetTrackingStrength")]
    public static class SARHSeeker_GetTrackingStrength_Patch
    {
        private static readonly System.Collections.Generic.Dictionary<int, MemoryCoastState> _sarhMemoryCoastTimers = new System.Collections.Generic.Dictionary<int, MemoryCoastState>();

        // Vanilla calls GetTrackingStrength every physics step. The bistatic evaluation (two path checks) runs at most
        // once per EvaluateIntervalSeconds per missile; between evaluations the last strength is returned
        // (the ARH seeker uses the same pattern with 0.25 s).
        private const float EvaluateIntervalSeconds = 0.1f;

        private struct CachedStrength
        {
            public float Time;
            public float Strength;
            public Unit Target;
            public Radar Illuminator;
        }

        private static readonly System.Collections.Generic.Dictionary<int, CachedStrength> _lastEvaluation = new System.Collections.Generic.Dictionary<int, CachedStrength>();

        public static void ClearCoastTimer(int missileId)
        {
            _sarhMemoryCoastTimers.Remove(missileId);
            _lastEvaluation.Remove(missileId);
        }

        public static void ClearAllCoastTimers()
        {
            _sarhMemoryCoastTimers.Clear();
            _lastEvaluation.Clear();
        }

        [HarmonyPrefix]
        public static bool Prefix(
            SARHSeeker __instance,
            Unit targetUnit,
            ref float __result,
            Radar ___radarSource,
            Missile ___missile,
            ref float ___trackingStrength,
            ref float ___timeWithoutTrack,
            ref Transform ___targetTransform,
            ref bool ___isJammed)
        {
            // A/B Toggle: If mod is disabled, run original vanilla calculation!
            if (!CnoEvilConfig.IsModActive)
            {
                return true;
            }

            if (__instance == null || targetUnit == null || targetUnit.disabled)
            {
                __result = 0.0f;
                return false;
            }

            Radar illuminator = ___radarSource;
            Missile missile = ___missile;

            if (illuminator == null || missile == null || !illuminator.IsOperational())
            {
                ___trackingStrength = 0.0f;
                __result = 0.0f;
                return false;
            }

            int cacheId = missile.GetInstanceID();
            float evalNow = Time.time;
            CachedStrength cached;
            if (_lastEvaluation.TryGetValue(cacheId, out cached) && evalNow - cached.Time < EvaluateIntervalSeconds &&
                cached.Target == targetUnit && cached.Illuminator == illuminator)
            {
                ___trackingStrength = cached.Strength;
                __result = cached.Strength;
                return false;
            }

            long perf = PerfStats.Begin();
            try
            {
                bool brokeLock;
                float strength = Evaluate(__instance, targetUnit, illuminator, missile,
                    ref ___trackingStrength, ref ___timeWithoutTrack, ref ___targetTransform, ref ___isJammed, out brokeLock);
                __result = strength;
                if (!brokeLock)
                {
                    cached.Time = evalNow;
                    cached.Strength = strength;
                    cached.Target = targetUnit;
                    cached.Illuminator = illuminator;
                    _lastEvaluation[cacheId] = cached;
                }
                else
                {
                    _lastEvaluation.Remove(cacheId);
                }
            }
            finally
            {
                PerfStats.End(PerfSlot.SarhSeeker, perf);
            }
            return false;
        }

        /// <summary>
        /// The illuminating radar loses its own track of the target to stand-off jamming: its monostatic SNR with the
        /// pod jam is below the detection threshold.
        /// </summary>
        public static bool IlluminatorStandoffJammed(Radar illuminator, Unit target, Vector3 illumPos, float illumDist)
        {
            if (illuminator == null || target == null) return false;
            RadarSkinSignal sig = RadarSignalModel.EvaluateRadar(illuminator, target as IRadarReturn, target, illumPos,
                illuminator.GetVelocity(), Mathf.Max(1.0f, illumDist));
            return sig.StandoffJamPower > 0.0f && sig.SnrDb < CnoEvilConfig.EffectiveMinSNR;
        }

        private static float Evaluate(
            SARHSeeker __instance,
            Unit targetUnit,
            Radar illuminator,
            Missile missile,
            ref float ___trackingStrength,
            ref float ___timeWithoutTrack,
            ref Transform ___targetTransform,
            ref bool ___isJammed,
            out bool brokeLock)
        {
            float __result;
            brokeLock = false;

            Transform illumScan = illuminator.GetScanPoint();
            Vector3 illumPos = (illumScan != null) ? illumScan.position : illuminator.transform.position;
            Vector3 targetPos = targetUnit.transform.position;
            Vector3 seekerPos = missile.transform.position;

            Vector3 illumLos = targetPos - illumPos;
            float illumDist = illumLos.magnitude;

            Vector3 seekerLos = targetPos - seekerPos;
            float seekerDist = seekerLos.magnitude;

            // 0-1. Bistatic line of sight (illuminator -> target and seeker -> target): 4/3 refracted path over the
            // curved Earth, the sea surface, terrain and ships
            float targetClearRadius = Mathf.Max(5.0f, targetUnit.maxRadius * 1.5f);
            bool pathByCurvature;
            if (RadarPhysics.IsRadarPathBlocked(illumPos, targetPos, targetClearRadius, false, out pathByCurvature) ||
                RadarPhysics.IsRadarPathBlocked(seekerPos, targetPos, targetClearRadius, false, out pathByCurvature))
            {
                ___trackingStrength = 0.0f;
                __result = 0.0f;
                return __result;
            }

            // Check look-down clutter notch relative to illuminator
            Vector3 targetVel = targetUnit.rb != null ? targetUnit.rb.velocity : Vector3.zero;
            Vector3 illumVel = illuminator.GetVelocity();
            float notchThreshold = CnoEvilConfig.EffectiveNotchThreshold;

            PhysRadarTarget physTarget = PhysRadarTarget.Get(targetUnit);
            float dynamicRcs = (physTarget != null) ? physTarget.GetDynamicRCS(illumPos) : targetUnit.RCS;
            bool isHeliHERM = physTarget != null && physTarget.IsHelicopterWithHERM();

            IRadarReturn radarReturn = targetUnit as IRadarReturn;
            float ecmIntensity = (radarReturn != null) ? radarReturn.GetECMIntensity() : 0.0f;

            DopplerResult doppler = DopplerClutterProcessor.ProcessDopplerAndClutter(
                illumPos,
                illumVel,
                targetPos,
                targetVel,
                notchThreshold,
                isHeliHERM,
                ecmIntensity,
                targetUnit != null ? targetUnit.radarAlt : 500.0f,
                targetUnit is Ship
            );

            // RCS that passes the Doppler filter (HERM helicopter in the notch: rotor flash only)
            if (physTarget != null)
            {
                dynamicRcs = DopplerClutterProcessor.FilteredTargetRcs(ref doppler, dynamicRcs, physTarget.GetRotorFlashRCS());
            }

            if (doppler.TrackRejectedByNotch)
            {
                int missileId = missile.GetInstanceID();
                float nowTime = Time.time;
                GlobalPosition targetPosG = targetUnit.GlobalPosition();
                MemoryCoastState coast;
                if (!_sarhMemoryCoastTimers.TryGetValue(missileId, out coast))
                {
                    coast = MemoryCoastState.Begin(nowTime, targetPosG, targetVel);
                    _sarhMemoryCoastTimers[missileId] = coast;
                }

                float coastDuration = CnoEvilConfig.MissileMemoryCoast_s != null ? CnoEvilConfig.MissileMemoryCoast_s.Value : 2.5f;
                bool enableCoast = CnoEvilConfig.EnableMissileMemoryCoasting == null || CnoEvilConfig.EnableMissileMemoryCoasting.Value;

                float predictionError = 0.0f;
                bool coastHolds = enableCoast && ElectronicWarfare.EvaluateMemoryCoast(
                    ref coast, nowTime, targetPosG, seekerDist, ecmIntensity, doppler.HasClutterBackground, coastDuration, out predictionError);

                if (enableCoast && !coastHolds && !coast.Spent && CnoEvilConfig.DebugLogging.Value && CnoEvilPlugin.Log != null)
                {
                    CnoEvilPlugin.Log.LogInfo(string.Format("SARH memory coast lost after {0:F2}s (prediction error {1:F0} m, range {2:F0} m)",
                        nowTime - coast.StartTime, predictionError, seekerDist));
                }

                if (coastHolds)
                {
                    // SARH Illuminator tracking is in Memory Coasting mode!
                    ___trackingStrength = 0.4f;
                    __result = 0.4f;
                    return __result;
                }

                // The target held the notch longer than the coast: no return. The vanilla seeker logic
                // (timeWithoutTrack) loses or reacquires the target. The spent state stays until the target
                // leaves the notch, so the coast does not restart.
                coast.Spent = true;
                _sarhMemoryCoastTimers[missileId] = coast;
                brokeLock = true;
                ___trackingStrength = 0.0f;
                __result = 0.0f;
                return __result;
            }
            else
            {
                _sarhMemoryCoastTimers.Remove(missile.GetInstanceID());
            }

            // The illuminator must track the target: when a jamming pod denies its own track, it cannot illuminate
            // (vanilla: a jammed radar drops all its SARH missiles). timeWithoutTrack then defeats the missile.
            if (StandoffJamming.AnyActive && IlluminatorStandoffJammed(illuminator, targetUnit, illumPos, illumDist))
            {
                ___isJammed = true;
                ___trackingStrength = 0.0f;
                __result = 0.0f;
                return __result;
            }
            ___isJammed = false;

            // Physical Bistatic Radar Range Equation:
            // Pr = (Pt * Gt * Gr_seeker * λ² * σ * Gp) / ((4π)³ * r1² * r2² * Lsys)
            PhysRadarEmitter emitter = PhysRadarEmitter.Get(illuminator);
            RadarSpecs specs = (emitter != null && emitter.ThermalNoiseFloor > 0.0f)
                ? emitter.Specs
                : RadarSpecs.CreateFromVanilla(
                    illuminator.RadarParameters.maxRange,
                    illuminator.RadarParameters.maxSignal,
                    illuminator.RadarParameters.minSignal
                );

            float r1 = Mathf.Max(50.0f, illumDist); // Illuminator to target (reused)
            float r2 = Mathf.Max(50.0f, seekerDist); // Target to missile seeker (reused)
            float r1_sq = r1 * r1;
            float r2_sq = r2 * r2;
            // Missile receiver: seeker-class antenna, bandwidth and noise figure at the illuminator's wavelength
            RadarSpecs receiverSpecs = specs;
            RadarSpecs seekerClass = RadarSpecs.CreateSeekerRadar();
            receiverSpecs.AntennaGainLinear = seekerClass.AntennaGainLinear;
            receiverSpecs.BandwidthHz = seekerClass.BandwidthHz;
            receiverSpecs.NoiseFigureLinear = seekerClass.NoiseFigureLinear;
            float seekerGain = receiverSpecs.AntennaGainLinear;
            float procGain = Mathf.Max(1.0f, specs.ProcessingGainLinear * 0.7f);

            float numerator = specs.PeakPowerWatts * CnoEvilConfig.RadarPowerFactor * specs.AntennaGainLinear * seekerGain *
                              (specs.WavelengthMeters * specs.WavelengthMeters) * dynamicRcs * procGain;
            float denominator = RadarPhysics.FourPiCubed * r1_sq * r2_sq * specs.SystemLossLinear;
            float echoPower = (denominator > 0.0f) ? (numerator / denominator) : 0.0f;

            // Atmospheric absorption along the bistatic path (illuminator -> target -> seeker)
            echoPower *= RadarPhysics.CalculateAtmosphericLossLinear(r1 + r2, specs.AttrCoeff_dB_per_km);

            // Seeker thermal noise floor
            float noiseFloor = RadarPhysics.CalculateThermalNoiseFloor(receiverSpecs.BandwidthHz, receiverSpecs.NoiseFigureLinear);

            // Target ECM jamming power against seeker receiver
            float jammerPower = 0.0f;
            if (ecmIntensity > 0.01f)
            {
                JammerSpecs jammer = JammerSpecs.CreateForIntensity(ecmIntensity);
                jammerPower = ElectronicWarfare.CalculateJammerPowerAtReceiver(r2, ref jammer, ref receiverSpecs) / ElectronicWarfare.BurnThroughRatio;
            }

            // Stand-off jamming (jamming pods) in the missile receiver
            jammerPower += RadarSignalModel.SeekerStandoffJamPower(missile, seekerPos, targetPos, ref receiverSpecs);

            // Bistatic clutter: the illuminator lights the ground near the target, the seeker receives the patch in its own
            // range cell and beam. Same bistatic equation as the echo with sigma_c in place of sigma; the patch is farther
            // from the seeker (R_g >= r2). The notch comes from the illuminator (approximation); the sidelobe clutter
            // band belongs to the receiver, so it uses the missile speed and the closing speed along the seeker line of sight.
            Vector3 missileVel = (missile.rb != null) ? missile.rb.velocity : Vector3.zero;
            doppler.PlatformSpeed = missileVel.magnitude;
            doppler.RelativeRadialVelocity = -Vector3.Dot(targetVel - missileVel, seekerLos / Mathf.Max(1.0f, seekerDist));
            float seekerHorizDist = Mathf.Sqrt(seekerLos.x * seekerLos.x + seekerLos.z * seekerLos.z);
            float seekerElevationDeg = Mathf.Atan2(seekerLos.y, Mathf.Max(1.0f, seekerHorizDist)) * Mathf.Rad2Deg;
            float seekerGroundRange;
            float clutterRcs = RadarPhysics.CalculateClutterRcs(r2, seekerElevationDeg, missile.radarAlt,
                receiverSpecs.AntennaGainLinear, receiverSpecs.BandwidthHz, out seekerGroundRange, doppler.SeaSurfaceTarget);
            float patchRangeRatio = r2 / Mathf.Max(r2, seekerGroundRange);
            float clutterPower = echoPower * (clutterRcs / Mathf.Max(0.0001f, dynamicRcs)) * patchRangeRatio * patchRangeRatio *
                                 DopplerClutterProcessor.ClutterDopplerSuppression(ref doppler, CnoEvilConfig.EffectiveSeekerSidelobeClutterDb);
            float snrDb = RadarPhysics.CalculateSNRdB(echoPower, noiseFloor, clutterPower, jammerPower);
            float minSnr = CnoEvilConfig.EffectiveMinSNR;

            // Home-On-Jam (HOJ) check for SARH missile
            // HOJ is strictly enabled ONLY when target is flying straight (NOT in notch gate)
            bool allowHoj = (CnoEvilConfig.HOJEnabled != null && CnoEvilConfig.HOJEnabled.Value);
            bool isHojHoming = allowHoj && ElectronicWarfare.EvaluateHOJGuidance(
                ecmIntensity,
                doppler.IsInsideClutterNotch,
                r2
            );

            bool skinReturnValid = snrDb >= minSnr;
            bool targetTracked = skinReturnValid || isHojHoming;

            float strength = 0.0f;
            if (targetTracked)
            {
                if (skinReturnValid)
                {
                    strength = Mathf.Clamp((snrDb - minSnr) * 0.5f + 1.0f, 1.0f, 15.0f);
                }
                else
                {
                    strength = Mathf.Clamp(ecmIntensity * 5.0f, 2.5f, 7.5f);
                    ___isJammed = false;
                }

                ___trackingStrength = strength;
                __result = strength;
            }
            else
            {
                ___trackingStrength = 0.0f;
                __result = 0.0f;
            }

            // Trigger continuous wave (CW) missile lock warning on target
            Aircraft targetAircraft = targetUnit as Aircraft;
            if (targetAircraft != null)
            {
                Unit illumUnit = illuminator != null ? illuminator.GetAttachedUnit() : null;
                PhysRWRReceiver rwr = PhysRWRReceiver.Get(targetAircraft);
                if (rwr != null)
                {
                    rwr.RegisterRadarPing(illumUnit, illuminator, RwrThreatState.MissileGuidance, RadarSignalModel.SeekerPingSignal01(strength), false);
                }

                if (illumUnit != null && SeekerDispersionHelper.ShouldSendRadarWarning(targetAircraft))
                {
                    SeekerDispersionHelper.SendRadarWarning(targetAircraft, illumUnit);
                }

                if (CnoEvilConfig.RadarTelemetryWanted && CombatHUD.i != null && targetAircraft == CombatHUD.i.aircraft)
                {
                    RadarTelemetry.RegisterIllumination(
                        illuminator != null ? illuminator.GetInstanceID() : __instance.GetInstanceID(),
                        illumUnit != null ? illumUnit.unitName : "SARH Radar Illuminator",
                        "SARH CW Illuminator",
                        illumPos,
                        illuminator != null ? illuminator.transform.forward : Vector3.forward,
                        r1,
                        snrDb,
                        echoPower,
                        RadarIlluminationState.MissileGuidance
                    );
                }
            }

            return __result;
        }
    }

    /// <summary>
    /// ARH launch datum: the vanilla datalink error vector becomes the horizontal launch dispersion of the mod
    /// (CEP from the faction track), limited to ArhMaxDispersionMeters, because vanilla DatalinkMode drops the
    /// target when the datum is more than 2000 m from it. ARHSeekerActivationRange_m > 0 replaces terminalRange.
    /// Targets the mod does not guide keep the vanilla error.
    /// </summary>
    [HarmonyPatch(typeof(ARHSeeker), "Initialize", new Type[] { typeof(Unit), typeof(GlobalPosition) })]
    public static class ARHSeeker_Initialize_Patch
    {
        private const float ArhMaxDispersionMeters = 1800.0f;

        [HarmonyPostfix]
        public static void Postfix(
            ARHSeeker __instance,
            Unit target,
            Missile ___missile,
            ref Vector3 ___positionalErrorVector,
            ref GlobalPosition ___knownPos,
            ref Vector3 ___knownVel,
            ref float ___terminalRange)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive) return;
                if (__instance == null || ___missile == null) return;

                float range = (CnoEvilConfig.ARHSeekerActivationRange_m != null) ? CnoEvilConfig.ARHSeekerActivationRange_m.Value : 0.0f;
                if (range > 0.0f) ___terminalRange = range;

                if (!SeekerDispersionHelper.IsModGuided(___missile, target)) return;

                Vector3 disp = SeekerDispersionHelper.LimitDispersion(___missile, target, ArhMaxDispersionMeters);
                // Vanilla knownPos = known position + vanilla error: replace the vanilla error with the mod dispersion
                ___knownPos = ___knownPos + (-___positionalErrorVector);
                ___positionalErrorVector = new Vector3(disp.x, 0.0f, disp.z);
                SeekerDispersionHelper.ApplyLaunchDatum(___missile, target, ref ___knownPos, ref ___knownVel);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("ARHSeeker_Initialize_Patch", ex);
            }
        }
    }

    /// <summary>
    /// ARH datalink midcourse: after the vanilla update (1 s), the datum is the faction track + dispersion, or stays
    /// where it was when the faction lost the target. Vanilla adds positionalErrorVector (the dispersion) to its
    /// own update. After the seeker locks, the datum is vanilla.
    /// </summary>
    [HarmonyPatch(typeof(ARHSeeker), "DatalinkMode")]
    public static class ARHSeeker_DatalinkMode_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(GlobalPosition ___knownPos, out GlobalPosition __state)
        {
            __state = ___knownPos;
        }

        [HarmonyPostfix]
        public static void Postfix(
            ARHSeeker __instance,
            Unit ___targetUnit,
            Missile ___missile,
            bool ___radarLockEstablished,
            float ___lastDatalinkTrackAttempt,
            ref GlobalPosition ___knownPos,
            ref Vector3 ___knownVel,
            GlobalPosition __state)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive) return;
                if (__instance == null || ___missile == null) return;
                if (___radarLockEstablished)
                {
                    SeekerDispersionHelper.MarkSeekerAcquired(___missile);
                    return;
                }
                // Vanilla runs the update once per second; between updates nothing changes
                if (___lastDatalinkTrackAttempt != Time.timeSinceLevelLoad) return;

                SeekerDispersionHelper.CorrectDatum(___missile, ___targetUnit, ref ___knownPos, ref ___knownVel, __state, false);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("ARHSeeker_DatalinkMode_Patch", ex);
            }
        }
    }

    /// <summary>
    /// SARH: while the illuminator gives a return, the vanilla datum (the target) stands. Without a return the
    /// missile flies to what the faction knows (track + dispersion), or holds the datum without a track.
    /// </summary>
    [HarmonyPatch(typeof(SARHSeeker), "LockedMode")]
    public static class SARHSeeker_LockedMode_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(GlobalPosition ___knownPos, out GlobalPosition __state)
        {
            __state = ___knownPos;
        }

        [HarmonyPostfix]
        public static void Postfix(
            SARHSeeker __instance,
            Unit ___targetUnit,
            Missile ___missile,
            float ___trackingStrength,
            ref GlobalPosition ___knownPos,
            ref Vector3 ___knownVel,
            GlobalPosition __state)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive) return;
                if (__instance == null || ___missile == null) return;
                if (___trackingStrength > 0.05f) return;

                SeekerDispersionHelper.CorrectDatum(___missile, ___targetUnit, ref ___knownPos, ref ___knownVel, __state, true);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("SARHSeeker_LockedMode_Patch", ex);
            }
        }
    }
}
