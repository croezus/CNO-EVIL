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

            // Terminal Pitbull Acquisition Basket:
            // Prior to active radar lock acquisition, the missile must be within the ARH terminal basket range
            // (default 10000m / 10km) of the datum (displaced aimpoint), not of the true target.
            // Missiles remain silent on datalink until within this range, providing realistic defensive reaction time.
            bool lockEstablished = ___radarLockEstablished;
            float pitbullDist = SeekerDispersionHelper.ArhPitbullDistance;
            float distToDatum = Vector3.Distance(missilePos, ___knownPos.ToLocalPosition());

            if (!lockEstablished && distToDatum > pitbullDist)
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

                float coastDuration = CnoEvilConfig.MissileMemoryCoastSeconds != null ? CnoEvilConfig.MissileMemoryCoastSeconds.Value : 2.5f;
                bool enableCoast = CnoEvilConfig.EnableMissileMemoryCoasting == null || CnoEvilConfig.EnableMissileMemoryCoasting.Value;

                float predictionError = 0.0f;
                bool coastHolds = enableCoast && ElectronicWarfare.EvaluateMemoryCoast(
                    ref coast, nowTime, targetPosG, dist, ecmIntensity, doppler.HasClutterBackground, coastDuration, out predictionError);

                if (enableCoast && !coastHolds && CnoEvilPlugin.Log != null)
                {
                    CnoEvilPlugin.Log.LogDebug(string.Format("ARH memory coast lost after {0:F2}s (prediction error {1:F0} m, range {2:F0} m)",
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

                // Target held notch continuously for full duration or decoyed! Full break-lock
                _arhMemoryCoastTimers.Remove(missileId);
                ___returnStrength = -1.0f;
                ___timeWithoutReturn = 999.0f;
                ___radarLockEstablished = false;
                ___isJammed = true;

                try
                {
                    missile.SetTarget(null);
                    // Defeat lead pursuit: direct missile aimpoint straight ahead along its current velocity vector
                    Vector3 forwardDir = (missile.rb != null && missile.rb.velocity.sqrMagnitude > 100.0f)
                        ? missile.rb.velocity.normalized
                        : missile.transform.forward;
                    GlobalPosition missilePosG = missile.GlobalPosition();
                    missile.SetAimpoint(missilePosG + forwardDir * 10000.0f, Vector3.zero);
                }
                catch { }

                __result = -1.0f;
                return false;
            }
            else
            {
                _arhMemoryCoastTimers.Remove(missile.GetInstanceID());
            }

            // 5. Ku-Band active seeker power calculation (calibrated Tier 0)
            float echoPower;
            float snrDb = RadarSignalModel.EvaluateArhSnrDb(dist, dynamicRcs, ecmIntensity, missile.radarAlt, ref doppler, out echoPower);

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
                    // Real-World ARH Midcourse vs Terminal:
                    // During midcourse inertial flight (radarLockEstablished == false),
                    // the active seeker transmitter is OFF. Target RWR does NOT detect active seeker RF!
                    // Only when radarLockEstablished == true (Terminal Pitbull phase) does the seeker emit active RF pulses!
                    bool isLockEstablished = ___radarLockEstablished;
                    bool enableSilentMidcourse = CnoEvilConfig.EnableSilentARHMidcourse == null || CnoEvilConfig.EnableSilentARHMidcourse.Value;

                    if (!enableSilentMidcourse || isLockEstablished)
                    {
                        PhysRWRReceiver rwr = PhysRWRReceiver.Get(targetAircraft);
                        if (rwr != null)
                        {
                            rwr.RegisterRadarPing(missile, null, RwrThreatState.MissileGuidance, RadarSignalModel.SeekerPingSignal01(strength), true);
                        }

                        if (SeekerDispersionHelper.ShouldSendRadarWarning(targetAircraft))
                        {
                            SeekerDispersionHelper.SendRadarWarning(targetAircraft, missile);
                        }

                        if (CombatHUD.i != null && targetAircraft == CombatHUD.i.aircraft)
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

        public static void ClearCoastTimer(int missileId)
        {
            _sarhMemoryCoastTimers.Remove(missileId);
        }

        public static void ClearAllCoastTimers()
        {
            _sarhMemoryCoastTimers.Clear();
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
                return false;
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
                targetUnit != null ? targetUnit.radarAlt : 500.0f
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

                float coastDuration = CnoEvilConfig.MissileMemoryCoastSeconds != null ? CnoEvilConfig.MissileMemoryCoastSeconds.Value : 2.5f;
                bool enableCoast = CnoEvilConfig.EnableMissileMemoryCoasting == null || CnoEvilConfig.EnableMissileMemoryCoasting.Value;

                float predictionError = 0.0f;
                bool coastHolds = enableCoast && ElectronicWarfare.EvaluateMemoryCoast(
                    ref coast, nowTime, targetPosG, seekerDist, ecmIntensity, doppler.HasClutterBackground, coastDuration, out predictionError);

                if (enableCoast && !coastHolds && CnoEvilPlugin.Log != null)
                {
                    CnoEvilPlugin.Log.LogDebug(string.Format("SARH memory coast lost after {0:F2}s (prediction error {1:F0} m, range {2:F0} m)",
                        nowTime - coast.StartTime, predictionError, seekerDist));
                }

                if (coastHolds)
                {
                    // SARH Illuminator tracking is in Memory Coasting mode!
                    ___trackingStrength = 0.4f;
                    __result = 0.4f;
                    return false;
                }

                _sarhMemoryCoastTimers.Remove(missileId);
                ___trackingStrength = 0.0f;
                ___timeWithoutTrack = 999.0f;
                ___targetTransform = null;
                ___isJammed = true;

                try
                {
                    missile.SetTarget(null);
                    Vector3 forwardDir = (missile.rb != null && missile.rb.velocity.sqrMagnitude > 100.0f)
                        ? missile.rb.velocity.normalized
                        : missile.transform.forward;
                    GlobalPosition missilePosG = missile.GlobalPosition();
                    missile.SetAimpoint(missilePosG + forwardDir * 10000.0f, Vector3.zero);
                }
                catch { }

                __result = 0.0f;
                return false;
            }
            else
            {
                _sarhMemoryCoastTimers.Remove(missile.GetInstanceID());
            }

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
                receiverSpecs.AntennaGainLinear, receiverSpecs.BandwidthHz, out seekerGroundRange);
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

                if (CombatHUD.i != null && targetAircraft == CombatHUD.i.aircraft)
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

            return false;
        }
    }

    public static class SeekerDispersionHelper
    {
        private static readonly Func<Missile, Unit> getMissileTarget = FastReflection.CreateFieldGetter<Missile, Unit>("target");
        private static readonly Func<MissileSeeker, Unit> getSeekerTarget = FastReflection.CreateFieldGetter<MissileSeeker, Unit>("targetUnit");
        private static readonly Func<ARHSeeker, bool> f_arhLock = FastReflection.CreateFieldGetter<ARHSeeker, bool>("radarLockEstablished");
        private static readonly Func<OpticalSeeker, bool> f_optVisual = FastReflection.CreateFieldGetter<OpticalSeeker, bool>("hasVisual");
        private static readonly Func<OpticalSeekerCruiseMissile, bool> f_cruiseTerminal = FastReflection.CreateFieldGetter<OpticalSeekerCruiseMissile, bool>("terminalMode");
        private static readonly Func<OpticalSeekerBomb, bool> f_bombVisual = FastReflection.CreateFieldGetter<OpticalSeekerBomb, bool>("hasVisual");
        private static readonly Func<ARMSeeker, Radar> f_armRadar = FastReflection.CreateFieldGetter<ARMSeeker, Radar>("targetedRadar");
        private static readonly Func<SARHSeeker, float> f_sarhStrength = FastReflection.CreateFieldGetter<SARHSeeker, float>("trackingStrength");
        private static readonly Func<SARHSeeker, float> f_sarhTimeWithoutTrack = FastReflection.CreateFieldGetter<SARHSeeker, float>("timeWithoutTrack");

        private static readonly Func<OpticalSeekerShell, bool> f_shellVisual = FastReflection.CreateFieldGetter<OpticalSeekerShell, bool>("hasVisual");
        private static readonly Func<LaserSeeker, bool> f_laserLock = FastReflection.CreateFieldGetter<LaserSeeker, bool>("hasLock");
        private static readonly Func<IRSeeker, bool> f_irAchievedLock = FastReflection.CreateFieldGetter<IRSeeker, bool>("achievedLock");
        private static readonly Func<IRSeeker, IRSource> f_irTarget = FastReflection.CreateFieldGetter<IRSeeker, IRSource>("IRTarget");
        private static readonly Func<Missile, GlobalPosition> f_missileAimPoint = FastReflection.CreateFieldGetter<Missile, GlobalPosition>("aimPoint");

        /// <summary>
        /// World position the missile is steering to before terminal seeker lock: the target plus the launch
        /// dispersion drawn from the faction's track quality, else the missile's own aim point, else the target.
        /// </summary>
        public static bool TryGetGuidanceAimPoint(Missile missile, Unit target, out Vector3 worldPos)
        {
            worldPos = Vector3.zero;
            if (missile == null) return false;

            bool hasTarget = target != null && !target.disabled;
            Vector3 disp;
            if (hasTarget && TrackUncertaintyCalculator.TryGetMissileDispersion(missile, out disp) && disp != Vector3.zero)
            {
                worldPos = target.transform.position + new Vector3(disp.x, 0f, disp.z);
                return true;
            }
            if (f_missileAimPoint != null)
            {
                worldPos = f_missileAimPoint(missile).ToLocalPosition();
                return true;
            }
            if (hasTarget)
            {
                worldPos = target.transform.position;
                return true;
            }
            return false;
        }

        private static readonly Func<Missile, MissileSeeker> f_missileSeeker = FastReflection.CreateFieldGetter<Missile, MissileSeeker>("seeker");

        public static float ArhPitbullDistance
        {
            get { return CnoEvilConfig.ARHTerminalActivationDistanceMeters != null ? CnoEvilConfig.ARHTerminalActivationDistanceMeters.Value : 10000.0f; }
        }

        public static float ArmActivationDistance
        {
            get { return CnoEvilConfig.ARADTerminalActivationDistanceMeters != null ? CnoEvilConfig.ARADTerminalActivationDistanceMeters.Value : 7000.0f; }
        }

        public static float TerminalActivationDistance
        {
            get { return CnoEvilConfig.MissileTerminalActivationDistanceMeters != null ? CnoEvilConfig.MissileTerminalActivationDistanceMeters.Value : 2800.0f; }
        }

        /// <summary>
        /// Faction whose tracks guide the missile: its own HQ, else its launcher's. The player's faction is used
        /// only for the player's own missiles (GetTrack(null) would silently return the player's tracks).
        /// </summary>
        public static FactionHQ GetMissileFaction(Missile missile)
        {
            if (missile == null) return null;
            if (missile.NetworkHQ != null) return missile.NetworkHQ;
            if (missile.owner != null && missile.owner.NetworkHQ != null) return missile.owner.NetworkHQ;
            Aircraft player = (CombatHUD.i != null) ? CombatHUD.i.aircraft : null;
            return (player != null && missile.owner == player) ? RWRTriangulationProcessor.GetPlayerFactionHQ() : null;
        }

        private const float RadarWarningRpcIntervalSeconds = 0.5f;
        private static readonly Dictionary<long, float> s_lastRadarWarning = new Dictionary<long, float>();

        /// <summary>
        /// Aircraft.RpcGetRadarWarning is a [ClientRpc]: send it from the server only and at most every 0.5 s
        /// per emitter / target pair (seekers update at 4 Hz).
        /// </summary>
        public static void SendRadarWarning(Aircraft target, Unit emitter)
        {
            if (target == null || emitter == null || !target.IsServer) return;

            long key = ((long)target.GetInstanceID() << 32) ^ (uint)emitter.GetInstanceID();
            float now = Time.time;
            float last;
            if (s_lastRadarWarning.TryGetValue(key, out last) && now - last < RadarWarningRpcIntervalSeconds) return;
            if (s_lastRadarWarning.Count > 512) s_lastRadarWarning.Clear();
            s_lastRadarWarning[key] = now;

            try { target.RpcGetRadarWarning(emitter); } catch { }
        }

        /// <summary>
        /// Vanilla sends the warning RPC to every aircraft. Send it to a human player only: the local player, or a
        /// remote player (Aircraft.Player is a SyncVar, set on the server). AI aircraft read PhysRWRReceiver directly.
        /// </summary>
        public static bool ShouldSendRadarWarning(Aircraft target)
        {
            if (target == null) return false;
            if (CombatHUD.i != null && target == CombatHUD.i.aircraft) return true;
            return target.Player != null;
        }

        public static void ClearAll()
        {
            s_lastRadarWarning.Clear();
        }

        /// <summary>
        /// Target state the missile's own faction knows, for datalink midcourse guidance:
        /// actively detected -> live position + launch dispersion and target velocity;
        /// in track memory -> frozen last known position + dispersion, zero velocity;
        /// no track (the faction does not know the target) -> false, the missile keeps its last datum.
        /// </summary>
        public static bool TryGetDatalinkTargetState(Missile missile, Unit target, out GlobalPosition position, out Vector3 velocity)
        {
            position = default(GlobalPosition);
            velocity = Vector3.zero;
            if (missile == null || target == null || target.disabled) return false;

            FactionHQ hq = GetMissileFaction(missile);
            TriangulationTrack track = (hq != null) ? RWRTriangulationProcessor.GetTrack(hq, target) : null;
            if (track == null || !track.HadActiveContact) return false;

            Vector3 basePos;
            bool includeFixError = true;
            if (track.IsActivelyDetected)
            {
                basePos = target.transform.position;
                velocity = (target.rb != null) ? target.rb.velocity : Vector3.zero;
            }
            else if (track.IsInMemoryState())
            {
                basePos = track.LastKnownGlobalPosition.ToLocalPosition();
                // A fix center already contains the fix error: do not add it again
                includeFixError = track.TriangulationErrorMeters <= 0.0f;
            }
            else
            {
                return false;
            }

            Vector3 disp = GetDispersionForMissile(missile, target, includeFixError);
            basePos.x += disp.x;
            basePos.z += disp.z; // Altitude (Y) preserved at the known target level
            position = basePos.ToGlobalPosition();
            return true;
        }

        public static MissileSeeker GetSeeker(Missile missile)
        {
            if (missile == null) return null;
            MissileSeeker seeker = (f_missileSeeker != null) ? f_missileSeeker(missile) : null;
            return seeker != null ? seeker : missile.GetComponent<MissileSeeker>();
        }

        public static Unit GetTargetForMissile(Missile missile)
        {
            if (missile == null) return null;
            Unit target = null;
            if (getMissileTarget != null)
            {
                target = getMissileTarget(missile);
            }
            if (target == null && getSeekerTarget != null)
            {
                MissileSeeker seeker = GetSeeker(missile);
                if (seeker != null)
                {
                    target = getSeekerTarget(seeker);
                }
            }
            return target;
        }

        public static bool IsBallisticOrInertial(MissileSeeker seeker)
        {
            if (seeker == null) return false;
            return seeker is InertialSeekerShell ||
                   seeker is BallisticMissileGuidance ||
                   seeker is OpticalSeekerHighDrag;
        }

        public static bool IsSeekerTerminalLocked(Missile missile)
        {
            if (missile == null) return false;
            MissileSeeker seeker = GetSeeker(missile);
            if (seeker == null) return false;

            ARHSeeker arh = seeker as ARHSeeker;
            if (arh != null)
            {
                return f_arhLock != null && f_arhLock(arh);
            }
            OpticalSeeker opt = seeker as OpticalSeeker;
            if (opt != null)
            {
                return f_optVisual != null && f_optVisual(opt);
            }
            OpticalSeekerCruiseMissile cruise = seeker as OpticalSeekerCruiseMissile;
            if (cruise != null)
            {
                return f_cruiseTerminal != null && f_cruiseTerminal(cruise);
            }
            OpticalSeekerBomb bomb = seeker as OpticalSeekerBomb;
            if (bomb != null)
            {
                return f_bombVisual != null && f_bombVisual(bomb);
            }
            OpticalSeekerShell shell = seeker as OpticalSeekerShell;
            if (shell != null)
            {
                return f_shellVisual != null && f_shellVisual(shell);
            }
            ARMSeeker arm = seeker as ARMSeeker;
            if (arm != null)
            {
                return f_armRadar != null && f_armRadar(arm) != null;
            }
            SARHSeeker sarh = seeker as SARHSeeker;
            if (sarh != null)
            {
                return f_sarhStrength != null && f_sarhStrength(sarh) > 0.05f &&
                       f_sarhTimeWithoutTrack != null && f_sarhTimeWithoutTrack(sarh) < 0.1f;
            }
            LaserSeeker laser = seeker as LaserSeeker;
            if (laser != null)
            {
                return f_laserLock != null && f_laserLock(laser);
            }
            IRSeeker ir = seeker as IRSeeker;
            if (ir != null)
            {
                return f_irAchievedLock != null && f_irAchievedLock(ir) &&
                       f_irTarget != null && f_irTarget(ir) != null;
            }
            return false;
        }

        public static Vector3 GetDispersionForMissile(Missile missile, Unit target)
        {
            return GetDispersionForMissile(missile, target, true);
        }

        /// <summary>
        /// Launch dispersion of the missile, drawn once from the faction's track. includeFixError = false
        /// gives the same draw without the passive fix error, for a datum at a fix center.
        /// The CEP range is the track's sensor range, not the launcher range.
        /// </summary>
        public static Vector3 GetDispersionForMissile(Missile missile, Unit target, bool includeFixError)
        {
            if (missile == null) return Vector3.zero;
            Vector3 offset;
            if (TrackUncertaintyCalculator.TryGetMissileDispersion(missile, includeFixError, out offset))
            {
                return offset;
            }

            if (target == null)
            {
                target = GetTargetForMissile(missile);
            }

            if (target == null) return Vector3.zero;

            FactionHQ hq = GetMissileFaction(missile);
            TriangulationTrack track = (hq != null) ? RWRTriangulationProcessor.GetTrack(hq, target) : null;

            // Guided artillery fired on a track at the artillery launch Q flies to the datum without dispersion
            if (track != null && track.TrackingQuality >= AIDoctrine.ArtilleryMinLaunchQuality &&
                MunitionClassification.IsGuidedArtillerySeeker(GetSeeker(missile)))
            {
                TrackUncertaintyCalculator.RegisterMissileDispersion(missile, Vector3.zero, Vector3.zero);
                return Vector3.zero;
            }

            LaunchCepInputs c = GetLaunchCepInputs(track, target, Vector3.Distance(missile.transform.position, target.transform.position));
            Vector3 offsetWithoutFix;
            offset = TrackUncertaintyCalculator.GenerateHorizontalDispersion(c.Distance, c.Quality, c.InMemory, c.MemoryElapsed,
                c.IsTriangulated, c.DriftSpeed, c.FixError, out offsetWithoutFix);
            TrackUncertaintyCalculator.RegisterMissileDispersion(missile, offset, offsetWithoutFix);
            return includeFixError ? offset : offsetWithoutFix;
        }

        /// <summary>
        /// Largest launch dispersion a missile fired now on this track can get (the CEP radius; the edge-biased
        /// draw never goes past it). 0 when track dispersion is off. launcherDistance is used only when the
        /// track has no sensor range.
        /// </summary>
        public static float PredictLaunchCep(TriangulationTrack track, Unit target, float launcherDistance)
        {
            if (target == null) return 0.0f;
            if (CnoEvilConfig.EnableTrackUncertaintyDispersion == null || !CnoEvilConfig.EnableTrackUncertaintyDispersion.Value) return 0.0f;

            LaunchCepInputs c = GetLaunchCepInputs(track, target, launcherDistance);
            return TrackUncertaintyCalculator.CalculateDispersionRadius(c.Distance, c.Quality, c.InMemory, c.MemoryElapsed,
                c.IsTriangulated, c.DriftSpeed, c.FixError);
        }

        private struct LaunchCepInputs
        {
            public float Distance;
            public float Quality;
            public bool InMemory;
            public float MemoryElapsed;
            public bool IsTriangulated;
            public float DriftSpeed;
            public float FixError;
        }

        /// <summary>
        /// CEP inputs from the faction's track. The angular error belongs to the sensor that holds the track;
        /// the launcher range is only a fallback.
        /// </summary>
        private static LaunchCepInputs GetLaunchCepInputs(TriangulationTrack track, Unit target, float launcherDistance)
        {
            LaunchCepInputs c = new LaunchCepInputs();
            c.Quality = (track != null) ? track.TrackingQuality : 0.15f;
            c.InMemory = (track != null) && track.IsInMemoryState();
            c.MemoryElapsed = (track != null) ? (Time.timeSinceLevelLoad - track.LastActiveDetectionTime) : 0f;
            c.IsTriangulated = (track != null) && track.IsTriangulated;
            c.Distance = (track != null && track.SensorRangeMeters > 0.0f) ? track.SensorRangeMeters : launcherDistance;
            c.DriftSpeed = TrackUncertaintyCalculator.GetMemoryDriftSpeed(target,
                (track != null) ? track.LastKnownVelocity.magnitude : target.rb != null ? target.rb.velocity.magnitude : 0f);
            c.FixError = (track != null) ? track.TriangulationErrorMeters : 0f;
            return c;
        }
    }

    /// <summary>
    /// Injects horizontal track uncertainty displacement (Dx, Dz, Dy=0) into ARAD anti-radiation missiles (ARMSeeker).
    /// During inertial flight, the missile flies towards the displaced aimpoint.
    /// If the enemy radar is actively transmitting within the seeker gimbal cone (maxTargetAngle), the passive RF seeker
    /// locks onto the radiation lobe and homes directly into the antenna.
    /// If the enemy radar shuts down during flight, the missile continues towards the stored aimpoint:
    /// with high team intelligence (Q->1.0), this scores a precision hit on the silent site;
    /// with low intelligence (Q=0.2), the weapon detonates harmlessly in empty terrain.
    /// </summary>
    [HarmonyPatch(typeof(ARMSeeker), "Initialize", new Type[] { typeof(Unit), typeof(GlobalPosition) })]
    public static class ARMSeeker_Initialize_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(ARMSeeker __instance, Unit target, GlobalPosition aimpoint, Missile ___missile, ref GlobalPosition ___knownPos)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive) return;
                if (__instance == null) return;

                Radar r = (target != null) ? (target.radar as Radar) : null;
                if (r != null)
                {
                    ARMSeeker_TrackCurrentTarget_Patch.RegisterDesignatedRadar(__instance, r);
                }

                Vector3 disp = SeekerDispersionHelper.GetDispersionForMissile(___missile, target);

                if (disp != Vector3.zero)
                {
                    Vector3 local = ___knownPos.ToLocalPosition();
                    local.x += disp.x;
                    local.z += disp.z;
                    // Altitude (Y) is strictly preserved at target level!
                    ___knownPos = local.ToGlobalPosition();
                }
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("ARMSeeker_Initialize_Patch", ex);
            }
        }
    }

    /// <summary>
    /// Delays ARM passive RF seeker radar source evaluation until within terminal activation basket (default 7000m / 7.0km).
    /// Before 7 km, munition navigates via datalink/inertial guidance towards displaced aimpoint.
    /// </summary>
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

                if (!ARMSeeker_TrackCurrentTarget_Patch.IsWithinActivationBasket(__instance, ___missile, ___knownPos))
                {
                    // Munition is still in midcourse datalink/inertial guidance towards displaced coordinates:
                    // Passive RF receiver does not evaluate radar sources until within 7 km basket.
                    __result = null;
                    return false;
                }
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("ARMSeeker_EvaluateRadarSources_Patch", ex);
            }
            return true;
        }
    }

    /// <summary>
    /// Delays ARM current target tracking until within terminal passive RF acquisition basket (default 7000m / 7.0km).
    /// Before 7 km, munition navigates via datalink towards displaced coordinates.
    /// Within 7 km, passive RF receiver detects enemy radar radiation and homes directly on transmitter antenna.
    /// Overrides vanilla 10,000m LineOfSight clamp with 50,000m terrain linecast.
    /// If radar shuts down, munition continues towards stored displaced aimpoint.
    /// </summary>
    [HarmonyPatch(typeof(ARMSeeker), "TrackCurrentTarget")]
    public static class ARMSeeker_TrackCurrentTarget_Patch
    {
        private static readonly ConditionalWeakTable<ARMSeeker, Radar> _designatedRadars = new ConditionalWeakTable<ARMSeeker, Radar>();
        /// <summary>
        /// Passive RF terminal basket: within ARADTerminalActivationDistanceMeters of the (displaced) waypoint
        /// or of the radar designated at launch.
        /// </summary>
        public static bool IsWithinActivationBasket(ARMSeeker seeker, Missile missile, GlobalPosition knownPos)
        {
            Vector3 missilePos = missile.transform.position;
            float dist = Vector3.Distance(missilePos, knownPos.ToLocalPosition());

            Radar designated;
            if (_designatedRadars.TryGetValue(seeker, out designated) && designated != null)
            {
                dist = Mathf.Min(dist, Vector3.Distance(missilePos, designated.transform.position));
            }
            return dist <= SeekerDispersionHelper.ArmActivationDistance;
        }

        public static void RegisterDesignatedRadar(ARMSeeker seeker, Radar radar)
        {
            if (seeker == null || radar == null) return;
            _designatedRadars.Remove(seeker);
            _designatedRadars.Add(seeker, radar);
        }

        [HarmonyPrefix]
        public static bool Prefix(ARMSeeker __instance, ref Radar __result, Missile ___missile, GlobalPosition ___knownPos,
            ref Radar ___targetedRadar, ref float ___lastLOSCheck)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive) return true;
                if (__instance == null || ___missile == null) return true;

                Radar designated = null;
                _designatedRadars.TryGetValue(__instance, out designated);

                if (!IsWithinActivationBasket(__instance, ___missile, ___knownPos))
                {
                    // Beyond 7 km: midcourse datalink guidance towards displaced waypoint
                    __result = null;
                    return false;
                }

                // Within 7 km: check current targeted radar or restore designated radar
                Radar targeted = ___targetedRadar;
                if (targeted == null && designated != null && designated.activated)
                {
                    targeted = designated;
                    ___targetedRadar = targeted;
                }

                if (targeted == null || !targeted.activated)
                {
                    __result = null;
                    return false;
                }

                // Check LineOfSight up to 50,000m (overriding vanilla 10,000m clamp)
                if (Time.timeSinceLevelLoad - ___lastLOSCheck > 0.25f)
                {
                    ___lastLOSCheck = Time.timeSinceLevelLoad;

                    Unit targetUnit = targeted.GetAttachedUnit();
                    if (targetUnit == null || !targetUnit.LineOfSight(___missile.transform.position, 50000f))
                    {
                        __result = null;
                        return false;
                    }
                }

                __result = targeted;
                return false;
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("ARMSeeker_TrackCurrentTarget_Patch", ex);
            }
            return true;
        }
    }

    /// <summary>
    /// Injects horizontal track uncertainty dispersion into ARH active radar missiles (ARHSeeker).
    /// Sets positionalErrorVector to pure horizontal offset (Dx, Dz, Dy=0) based on team intelligence at launch.
    /// Sets terminalRange to ARHTerminalActivationDistanceMeters (default 10 km pitbull basket).
    /// </summary>
    [HarmonyPatch(typeof(ARHSeeker), "Initialize", new Type[] { typeof(Unit), typeof(GlobalPosition) })]
    public static class ARHSeeker_Initialize_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(
            ARHSeeker __instance,
            Unit target,
            GlobalPosition aimpoint,
            Missile ___missile,
            ref Vector3 ___positionalErrorVector,
            ref float ___terminalRange)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive) return;
                if (__instance == null) return;

                Vector3 disp = SeekerDispersionHelper.GetDispersionForMissile(___missile, target);

                if (disp != Vector3.zero)
                {
                    ___positionalErrorVector = new Vector3(disp.x, 0f, disp.z);
                }

                float pitbullDist = SeekerDispersionHelper.ArhPitbullDistance;

                ___terminalRange = pitbullDist;
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("ARHSeeker_Initialize_Patch", ex);
            }
        }
    }

    /// <summary>
    /// Guides ARH missiles in datalink midcourse towards the target as the launching faction knows it
    /// (live track, or last known position in track memory), displaced by the launch dispersion.
    /// If the faction has no track, the missile keeps its previous datum. Once the seeker acquires
    /// autonomous active lock at terminal pitbull range, this patch yields to the seeker radar.
    /// Sets terminalRange in Prefix so the vanilla distance gate uses the pitbull basket (default 10 km).
    /// </summary>
    [HarmonyPatch(typeof(ARHSeeker), "DatalinkMode")]
    public static class ARHSeeker_DatalinkMode_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(ARHSeeker __instance, ref float ___terminalRange, GlobalPosition ___knownPos, out GlobalPosition __state)
        {
            __state = ___knownPos;
            try
            {
                if (!CnoEvilConfig.IsModActive || __instance == null) return;
                ___terminalRange = SeekerDispersionHelper.ArhPitbullDistance;
            }
            catch { }
        }

        [HarmonyPostfix]
        public static void Postfix(
            ARHSeeker __instance,
            Unit ___targetUnit,
            Missile ___missile,
            bool ___radarLockEstablished,
            ref GlobalPosition ___knownPos,
            ref Vector3 ___positionalErrorVector,
            GlobalPosition __state)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive) return;
                if (__instance == null || ___missile == null) return;
                if (___radarLockEstablished) return; // Seeker radar acquired the physical target directly

                Unit target = ___targetUnit;
                if (target == null || target.disabled) return;

                GlobalPosition known;
                Vector3 knownVel;
                // Dispersion is already contained in the datum, so the vanilla error vector is cleared
                ___knownPos = SeekerDispersionHelper.TryGetDatalinkTargetState(___missile, target, out known, out knownVel)
                    ? known
                    : __state;
                ___positionalErrorVector = Vector3.zero;
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("ARHSeeker_DatalinkMode_Patch", ex);
            }
        }
    }

    /// <summary>
    /// SARH guidance: while the carrier radar illuminates the target the missile rides the reflected beam
    /// straight to it. Without illumination it falls back to what the faction knows about the target
    /// (track or track memory, displaced by launch dispersion), else keeps its previous datum.
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
            GlobalPosition __state)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive) return;
                if (__instance == null || ___missile == null) return;

                Unit target = ___targetUnit;
                if (target == null || target.disabled) return;

                // Active illumination: the seeker homes on the reflected CW return, no datalink dispersion
                if (___trackingStrength > 0.05f)
                {
                    ___knownPos = GlobalPositionExtensions.GlobalPosition(target);
                    return;
                }

                GlobalPosition known;
                Vector3 knownVel;
                ___knownPos = SeekerDispersionHelper.TryGetDatalinkTargetState(___missile, target, out known, out knownVel)
                    ? known
                    : __state;
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("SARHSeeker_LockedMode_Patch", ex);
            }
        }
    }

    /// <summary>
    /// Provides smart obstacle clearance and midcourse lofting for long-range SARH missiles (StratoLance R9, etc.).
    /// Prevents missiles from crashing into mountain ridges during low-altitude / standoff engagements.
    /// </summary>
    [HarmonyPatch(typeof(SARHSeeker), "SendTargetInfo")]
    public static class SARHSeeker_SendTargetInfo_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(
            SARHSeeker __instance,
            Missile ___missile,
            GlobalPosition ___knownPos,
            Vector3 ___knownVel)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive) return;
                if (__instance == null || ___missile == null || ___missile.disabled) return;
                if (CnoEvilConfig.SARHEnableLoftTerrainClearance == null || !CnoEvilConfig.SARHEnableLoftTerrainClearance.Value) return;

                // Safety gate: allow 4.0s of undisturbed flight for launch pitchover and initial boost
                if (___missile.timeSinceSpawn < 4.0f) return;

                Vector3 missilePos = ___missile.transform.position;
                Vector3 targetPos = ___knownPos.ToLocalPosition();
                Vector3 toTarget = targetPos - missilePos;
                float dist = toTarget.magnitude;

                // Midcourse / standoff flight (> 6 km): terrain obstacle clearance & lofting
                if (dist > 6000.0f)
                {
                    int mask = (int)PhysicsLayers.StaticsMask | (int)PhysicsLayers.ShipsMask;
                    RaycastHit hit;
                    if (Physics.Linecast(missilePos + Vector3.up * 5.0f, targetPos + Vector3.up * 5.0f, out hit, mask))
                    {
                        if (hit.collider != null)
                        {
                            float hitDist = hit.distance;
                            // Ensure the obstacle is an intermediate mountain ridge between missile and target,
                            // rather than ground near the launcher pad or terrain beneath the target:
                            if (hitDist > 1000.0f && hitDist < dist - 3000.0f)
                            {
                                float clearAlt = hit.point.y + 350.0f;
                                if (missilePos.y < clearAlt)
                                {
                                    Vector3 loftPoint = hit.point + Vector3.up * 350.0f;
                                    ___missile.SetAimpoint(loftPoint.ToGlobalPosition(), Vector3.zero);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("SARHSeeker_SendTargetInfo_Patch", ex);
            }
        }
    }

    /// <summary>
    /// Injects horizontal track uncertainty displacement (Dx, Dz, Dy=0) into optical munitions (AGM-68, AGM-48, ATP-1, glide bombs).
    /// Enforces hasVisual = false at launch to prevent instant standoff lock.
    /// Directs missile autopilot towards displaced waypoint.
    /// </summary>
    [HarmonyPatch(typeof(OpticalSeeker), "Initialize", new Type[] { typeof(Unit), typeof(GlobalPosition) })]
    public static class OpticalSeeker_Initialize_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(
            OpticalSeeker __instance,
            Unit target,
            GlobalPosition aimpoint,
            Missile ___missile,
            ref GlobalPosition ___knownPos,
            ref Vector3 ___knownVel,
            ref bool ___hasVisual)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive) return;
                if (__instance == null || ___missile == null) return;

                ___hasVisual = false;

                Vector3 disp = SeekerDispersionHelper.GetDispersionForMissile(___missile, target);
                if (disp != Vector3.zero)
                {
                    Vector3 local = ___knownPos.ToLocalPosition();
                    local.x += disp.x;
                    local.z += disp.z;
                    ___knownPos = local.ToGlobalPosition();

                    ___missile.SetAimpoint(___knownPos, ___knownVel);
                    ___missile.SetTarget(null);
                }
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("OpticalSeeker_Initialize_Patch", ex);
            }
        }
    }

    /// <summary>
    /// Replaces vanilla OpticalCheck to enforce terminal distance gating (MissileTerminalActivationDistanceMeters, default 2800m).
    /// In midcourse flight (> 2800m), suppresses visual lock and prevents vanilla from overwriting knownPos with true target coordinates.
    /// In terminal phase (<= 2800m), evaluates optical acquisition: target must physically reside inside pitbull basket
    /// around the displaced aimpoint, with unblocked line of sight and within gimbal angle limits.
    /// </summary>
    [HarmonyPatch(typeof(OpticalSeeker), "OpticalCheck")]
    public static class OpticalSeeker_OpticalCheck_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(
            OpticalSeeker __instance,
            Missile ___missile,
            Unit ___targetUnit,
            Transform ___targetTransform,
            ref GlobalPosition ___knownPos,
            ref Vector3 ___knownVel,
            ref bool ___hasVisual,
            ref bool ___targetObstructed,
            ref float ___lastOpticalCheck,
            float ___searchRadius,
            float ___searchAngle,
            bool ___useDatalink)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive) return true;
                if (__instance == null || ___missile == null) return true;

                ___lastOpticalCheck = Time.timeSinceLevelLoad;

                Unit target = ___targetUnit;
                if (target == null || target.disabled)
                {
                    ___hasVisual = false;
                    ___targetObstructed = false;
                    return false;
                }

                Vector3 missilePos = ___missile.transform.position;
                Vector3 aimpointLocal = ___knownPos.ToLocalPosition();
                float distToAimpoint = Vector3.Distance(missilePos, aimpointLocal);
                float distToTarget = Vector3.Distance(missilePos, target.transform.position);

                float pitbullDist = SeekerDispersionHelper.TerminalActivationDistance;

                // 1. Standoff Midcourse Range Gate: missile must be in terminal approach
                if (distToAimpoint > pitbullDist && distToTarget > pitbullDist)
                {
                    ___hasVisual = false;
                    ___targetObstructed = false;
                    return false;
                }

                // 2. Terminal Basket Gate: target must physically reside inside pitbull basket of displaced aimpoint
                float basketRadius = Mathf.Max(___searchRadius, pitbullDist);
                bool inBasket = FastMath.InRange(target.GlobalPosition(), ___knownPos, basketRadius + target.maxRadius);

                if (!inBasket)
                {
                    ___targetObstructed = false;
                    ___hasVisual = false;
                    return false;
                }

                // 3. Seeker Visual Acquisition Range: missile must be within optical resolution distance of target
                if (distToTarget > pitbullDist)
                {
                    ___hasVisual = false;
                    ___targetObstructed = false;
                    return false;
                }

                // 4. Seeker gimbal / boresight angle check (within seeker FOV cone)
                Vector3 toTarget = target.transform.position - missilePos;
                float gimbalAngle = (___searchAngle > 10.0f) ? ___searchAngle : 90.0f;
                bool inGimbal = Vector3.Angle(toTarget, ___missile.transform.forward) < gimbalAngle;

                if (!inGimbal)
                {
                    ___hasVisual = false;
                    ___targetObstructed = false;
                    return false;
                }

                // 5. Line of sight check
                bool los = target.LineOfSight(missilePos, pitbullDist);

                if (los)
                {
                    ___hasVisual = true;
                    ___targetObstructed = false;
                }
                else
                {
                    ___hasVisual = false;
                    ___targetObstructed = true;
                }

                return false;
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("OpticalSeeker_OpticalCheck_Patch", ex);
                return true;
            }
        }
    }

    /// <summary>
    /// Optical munition midcourse guidance (hasVisual == false): steers to the target as the launching faction
    /// knows it (track / track memory + launch dispersion), or keeps the previous datum when the faction has
    /// no track. Once the seeker has a direct visual lock, vanilla lead pursuit takes over.
    /// </summary>
    [HarmonyPatch(typeof(OpticalSeeker), "GetTargetParameters")]
    public static class OpticalSeeker_GetTargetParameters_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(GlobalPosition ___knownPos, out GlobalPosition __state)
        {
            __state = ___knownPos;
        }

        [HarmonyPostfix]
        public static void Postfix(
            OpticalSeeker __instance,
            bool ___hasVisual,
            Unit ___targetUnit,
            Missile ___missile,
            ref GlobalPosition ___knownPos,
            ref Vector3 ___knownVel,
            GlobalPosition __state)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive) return;
                if (__instance == null || ___missile == null) return;
                if (___hasVisual) return; // Optical sensor acquired direct visual line-of-sight

                Unit target = ___targetUnit;
                if (target == null || target.disabled) return;

                GlobalPosition known;
                Vector3 knownVel;
                if (SeekerDispersionHelper.TryGetDatalinkTargetState(___missile, target, out known, out knownVel))
                {
                    ___knownPos = known;
                    ___knownVel = knownVel;
                }
                else
                {
                    ___knownPos = __state;
                    ___knownVel = Vector3.zero;
                }

                ___missile.SetAimpoint(___knownPos, ___knownVel);
                ___missile.SetTarget(null);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("OpticalSeeker_GetTargetParameters_Patch", ex);
            }
        }
    }

    /// <summary>
    /// Injects horizontal track uncertainty displacement into cruise missiles (OpticalSeekerCruiseMissile).
    /// </summary>
    [HarmonyPatch(typeof(OpticalSeekerCruiseMissile), "Initialize", new Type[] { typeof(Unit), typeof(GlobalPosition) })]
    public static class OpticalSeekerCruiseMissile_Initialize_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(
            OpticalSeekerCruiseMissile __instance,
            Unit target,
            GlobalPosition aimpoint,
            Missile ___missile,
            ref GlobalPosition ___knownPos,
            ref GlobalPosition ___aimPos,
            ref bool ___terminalMode)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive) return;
                if (__instance == null || ___missile == null) return;

                ___terminalMode = false;

                Vector3 disp = SeekerDispersionHelper.GetDispersionForMissile(___missile, target);
                if (disp != Vector3.zero)
                {
                    Vector3 local = ___knownPos.ToLocalPosition();
                    local.x += disp.x;
                    local.z += disp.z;
                    ___knownPos = local.ToGlobalPosition();
                    ___aimPos = ___knownPos;

                    ___missile.SetAimpoint(___knownPos, Vector3.zero);
                    ___missile.SetTarget(null);
                }
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("OpticalSeekerCruiseMissile_Initialize_Patch", ex);
            }
        }
    }

    /// <summary>
    /// Cruise missile datalink updates: the known position follows the launching faction's track
    /// (+ launch dispersion), or stays at the previous datum when the faction has no track.
    /// </summary>
    [HarmonyPatch(typeof(OpticalSeekerCruiseMissile), "UpdateTargetParameters")]
    public static class OpticalSeekerCruiseMissile_UpdateTargetParameters_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(GlobalPosition ___knownPos, out GlobalPosition __state)
        {
            __state = ___knownPos;
        }

        [HarmonyPostfix]
        public static void Postfix(
            OpticalSeekerCruiseMissile __instance,
            Unit ___targetUnit,
            Missile ___missile,
            bool ___terminalMode,
            ref GlobalPosition ___knownPos,
            ref GlobalPosition ___aimPos,
            GlobalPosition __state)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive) return;
                if (__instance == null || ___missile == null) return;
                if (___terminalMode) return;

                Unit target = ___targetUnit;
                if (target == null || target.disabled) return;

                GlobalPosition known;
                Vector3 knownVel;
                ___knownPos = SeekerDispersionHelper.TryGetDatalinkTargetState(___missile, target, out known, out knownVel)
                    ? known
                    : __state;
                ___aimPos = ___knownPos;

                ___missile.SetAimpoint(___knownPos, Vector3.zero);
                ___missile.SetTarget(null);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("OpticalSeekerCruiseMissile_UpdateTargetParameters_Patch", ex);
            }
        }
    }

    /// <summary>
    /// Enforces terminal distance gating and seeker basket check on cruise missiles.
    /// Only triggers terminal mode if target resides within terminal search radius of displaced waypoint.
    /// </summary>
    [HarmonyPatch(typeof(OpticalSeekerCruiseMissile), "PreTerminalMode")]
    public static class OpticalSeekerCruiseMissile_PreTerminalMode_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(
            OpticalSeekerCruiseMissile __instance,
            Missile ___missile,
            Unit ___targetUnit,
            ref Transform ___targetPart,
            ref bool ___terminalMode,
            ref float ___terminalRange,
            float ___terminalSearchRadius,
            ref GlobalPosition ___knownPos,
            ref GlobalPosition ___aimPos,
            ref float ___lastTerminalCheck)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive) return true;
                if (__instance == null || ___missile == null) return true;

                float pitbullDist = SeekerDispersionHelper.TerminalActivationDistance;
                ___terminalRange = pitbullDist;

                if (Time.timeSinceLevelLoad - ___lastTerminalCheck < 0.5f) return false;
                ___lastTerminalCheck = Time.timeSinceLevelLoad;

                if (__instance.CheckWaypoint())
                {
                    ___aimPos = ___knownPos;
                }

                GlobalPosition terrainWp = __instance.TerrainWaypoint(___aimPos);
                ___missile.SetAimpoint(terrainWp, Vector3.zero);

                if (___missile.timeSinceSpawn <= 6.0f) return false;

                if (FastMath.InRange(___missile.GlobalPosition(), ___knownPos, pitbullDist))
                {
                    Unit target = ___targetUnit;
                    if (target != null && !target.disabled)
                    {
                        float searchRad = Mathf.Max(___terminalSearchRadius, pitbullDist);
                        if (FastMath.InRange(target.GlobalPosition(), ___knownPos, searchRad + target.maxRadius))
                        {
                            ___targetPart = target.GetRandomPart();
                            ___terminalMode = true;
                            ___missile.Arm();
                            return false;
                        }
                        else
                        {
                            // Target outside terminal basket: cruise missile continues to displaced waypoint
                            return false;
                        }
                    }
                    else
                    {
                        if (___missile.rb != null)
                        {
                            ___missile.Detonate(___missile.rb.velocity, false, false);
                        }
                        return false;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("OpticalSeekerCruiseMissile_PreTerminalMode_Patch", ex);
                return true;
            }
        }
    }

    /// <summary>
    /// Injects horizontal track uncertainty displacement into free-fall guided bombs (OpticalSeekerBomb).
    /// </summary>
    [HarmonyPatch(typeof(OpticalSeekerBomb), "Initialize", new Type[] { typeof(Unit), typeof(GlobalPosition) })]
    public static class OpticalSeekerBomb_Initialize_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(
            OpticalSeekerBomb __instance,
            Unit target,
            GlobalPosition aimpoint,
            Missile ___missile,
            ref GlobalPosition ___knownPos,
            ref GlobalPosition ___aimPos,
            ref bool ___hasVisual)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive) return;
                if (__instance == null || ___missile == null) return;

                ___hasVisual = false;

                Vector3 disp = SeekerDispersionHelper.GetDispersionForMissile(___missile, target);
                if (disp != Vector3.zero)
                {
                    Vector3 local = ___knownPos.ToLocalPosition();
                    local.x += disp.x;
                    local.z += disp.z;
                    ___knownPos = local.ToGlobalPosition();
                    ___aimPos = ___knownPos;

                    ___missile.SetAimpoint(___knownPos, Vector3.zero);
                    ___missile.SetTarget(null);
                }
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("OpticalSeekerBomb_Initialize_Patch", ex);
            }
        }
    }

    /// <summary>
    /// Delays optical visual acquisition for guided bombs until within terminal distance (2800m) of displaced aimpoint.
    /// </summary>
    [HarmonyPatch(typeof(OpticalSeekerBomb), "TrackVisual")]
    public static class OpticalSeekerBomb_TrackVisual_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(
            OpticalSeekerBomb __instance,
            Missile ___missile,
            Unit ___targetUnit,
            GlobalPosition ___knownPos,
            float ___searchRadius,
            ref float ___lastVisualCheck,
            ref bool __result)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive) return true;
                if (__instance == null || ___missile == null) return true;

                ___lastVisualCheck = Time.timeSinceLevelLoad;

                Unit target = ___targetUnit;
                if (target == null || target.disabled)
                {
                    __result = false;
                    return false;
                }

                float pitbullDist = SeekerDispersionHelper.TerminalActivationDistance;

                Vector3 bombPos = ___missile.transform.position;
                Vector3 aimLocal = ___knownPos.ToLocalPosition();
                float distToAimpoint = Vector3.Distance(bombPos, aimLocal);
                float distToTarget = Vector3.Distance(bombPos, target.transform.position);

                // 1. Terminal range gate
                if (distToAimpoint > pitbullDist && distToTarget > pitbullDist)
                {
                    __result = false;
                    return false;
                }

                // 2. Terminal basket gate: target must be within pitbull basket of aimpoint
                float basketRadius = Mathf.Max(___searchRadius, pitbullDist);
                if (!FastMath.InRange(target.GlobalPosition(), ___knownPos, basketRadius + target.maxRadius))
                {
                    __result = false;
                    return false;
                }

                // 3. Sensor acquisition range
                if (distToTarget > pitbullDist)
                {
                    __result = false;
                    return false;
                }

                // 4. Line of Sight
                __result = target.LineOfSight(bombPos, pitbullDist);
                return false;
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("OpticalSeekerBomb_TrackVisual_Patch", ex);
                return true;
            }
        }
    }

    /// <summary>
    /// Optical guided bomb guidance: maintains the faction-known (displaced) aimpoint while the seeker has no visual,
    /// or keeps the previous datum when the faction has no track.
    /// </summary>
    [HarmonyPatch(typeof(OpticalSeekerBomb), "GetTargetParameters")]
    public static class OpticalSeekerBomb_GetTargetParameters_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(GlobalPosition ___knownPos, out GlobalPosition __state)
        {
            __state = ___knownPos;
        }

        [HarmonyPostfix]
        public static void Postfix(
            OpticalSeekerBomb __instance,
            bool ___hasVisual,
            Unit ___targetUnit,
            Missile ___missile,
            ref GlobalPosition ___knownPos,
            GlobalPosition __state)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive) return;
                if (__instance == null || ___missile == null) return;
                if (___hasVisual) return;

                Unit target = ___targetUnit;
                if (target == null || target.disabled) return;

                GlobalPosition known;
                Vector3 knownVel;
                ___knownPos = SeekerDispersionHelper.TryGetDatalinkTargetState(___missile, target, out known, out knownVel)
                    ? known
                    : __state;

                ___missile.SetTarget(null);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("OpticalSeekerBomb_GetTargetParameters_Patch", ex);
            }
        }
    }

    /// <summary>
    /// Prevents vanilla IRSeeker.Initialize from dropping thermal lock when team datalink track uncertainty
    /// exceeds 500m. Heatseekers home directly on engine thermal plumes (physical line-of-sight), not datalink coordinates.
    /// </summary>
    [HarmonyPatch(typeof(IRSeeker), "Initialize", new Type[] { typeof(Unit), typeof(GlobalPosition) })]
    public static class IRSeeker_Initialize_Patch
    {
        private static readonly MethodInfo m_onTargetFlare = AccessTools.Method(typeof(IRSeeker), "IRSeeker_OnTargetFlare");

        [HarmonyPostfix]
        public static void Postfix(
            IRSeeker __instance,
            Unit target,
            ref IRSource ___IRTarget,
            ref bool ___targetOnLaunch,
            ref bool ___achievedLock,
            Missile ___missile)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive) return;
                if (__instance == null || target == null || target.disabled || ___missile == null) return;

                // If vanilla already has a valid IRTarget, thermal lock succeeded
                if (___IRTarget != null) return;

                // CNO-EVIL conflict fix:
                // Check if thermal lock should have succeeded based on line-of-sight and thermal signature
                if (target.HasIRSignature() && target.LineOfSight(___missile.transform.position, 1500f))
                {
                    IRSource source = target.GetIRSource();
                    if (source != null && !source.flare)
                    {
                        ___IRTarget = source;
                        ___targetOnLaunch = true;
                        ___achievedLock = true;
                        ___missile.SetTarget(target);

                        if (m_onTargetFlare != null)
                        {
                            Action<IRSource> flareDelegate = (Action<IRSource>)Delegate.CreateDelegate(typeof(Action<IRSource>), __instance, m_onTargetFlare);
                            target.onAddIRSource += flareDelegate;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("IRSeeker_Initialize_Patch", ex);
            }
        }
    }

    /// <summary>
    /// Injects team reconnaissance quality (Q) track uncertainty dispersion into guided/ballistic artillery shells.
    /// </summary>
    [HarmonyPatch(typeof(InertialSeekerShell), "Initialize", new Type[] { typeof(Unit), typeof(GlobalPosition) })]
    public static class InertialSeekerShell_Initialize_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(
            InertialSeekerShell __instance,
            Unit target,
            Missile ___missile,
            ref GlobalPosition ___knownPos)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive) return;
                if (__instance == null || ___missile == null) return;

                Unit t = (target != null && !target.disabled) ? target : SeekerDispersionHelper.GetTargetForMissile(___missile);
                if (t == null) return;

                Vector3 disp = SeekerDispersionHelper.GetDispersionForMissile(___missile, t);
                if (disp != Vector3.zero)
                {
                    Vector3 local = ___knownPos.ToLocalPosition();
                    local.x += disp.x;
                    local.z += disp.z;
                    ___knownPos = local.ToGlobalPosition();
                }
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("InertialSeekerShell_Initialize_Patch", ex);
            }
        }
    }

    /// <summary>
    /// Replaces vanilla InertialSeekerShell datalink updates (knownPos = targetUnit.GlobalPosition(), i.e. perfect
    /// knowledge) with what the launching faction knows (track / track memory + launch dispersion).
    /// Without a track the shell keeps its previous datum.
    /// </summary>
    [HarmonyPatch(typeof(InertialSeekerShell), "UpdateTargetPosition")]
    public static class InertialSeekerShell_UpdateTargetPosition_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(
            InertialSeekerShell __instance,
            bool ___useDatalink,
            Missile ___missile,
            Unit ___targetUnit,
            ref GlobalPosition ___knownPos,
            ref Vector3 ___knownVel)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive) return true;
                if (__instance == null || ___missile == null) return true;
                if (!___useDatalink) return false;

                GlobalPosition known;
                Vector3 knownVel;
                if (SeekerDispersionHelper.TryGetDatalinkTargetState(___missile, ___targetUnit, out known, out knownVel))
                {
                    ___knownPos = known;
                    ___knownVel = knownVel;
                }
                return false;
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("InertialSeekerShell_UpdateTargetPosition_Patch", ex);
                return true;
            }
        }
    }

    /// <summary>
    /// Sets the impact point of the ballistic missile (Piledriver TBM) at launch, the only mod influence on it:
    /// the launching faction's track datum plus launch dispersion. Without a track the launcher's aimpoint plus
    /// dispersion is used. Fixes vanilla bug where aimpoint was ignored when firing at ground markers / untargeted points.
    /// The flight itself (SetTrajectory, burn, re-entry) stays vanilla.
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
                if (!CnoEvilConfig.IsModActive) return;
                if (__instance == null || ___missile == null) return;

                Unit t = (target != null && !target.disabled) ? target : SeekerDispersionHelper.GetTargetForMissile(___missile);
                if (t != null)
                {
                    GlobalPosition known;
                    Vector3 knownVel;
                    if (SeekerDispersionHelper.TryGetDatalinkTargetState(___missile, t, out known, out knownVel))
                    {
                        ___knownPos = known;
                        ___knownVel = Vector3.ClampMagnitude(knownVel, ___maxTargetSpeed);
                    }
                    else
                    {
                        // No faction track: the launcher's aimpoint is the best datum, displaced by dispersion
                        Vector3 disp = SeekerDispersionHelper.GetDispersionForMissile(___missile, t);
                        Vector3 local = ((aimpoint != default(GlobalPosition)) ? aimpoint : ___knownPos).ToLocalPosition();
                        local.x += disp.x;
                        local.z += disp.z;
                        ___knownPos = local.ToGlobalPosition();
                        ___knownVel = Vector3.zero;
                    }
                }
                else
                {
                    // Target is a ground waypoint / GPS map coordinate:
                    // Fix vanilla bug where aimpoint was completely ignored and replaced with forward * 100km!
                    if (aimpoint != default(GlobalPosition))
                    {
                        ___knownPos = aimpoint;
                        ___knownVel = Vector3.zero;
                        ___errorOffset = Vector3.zero;
                        ___missile.SetAimpoint(aimpoint, Vector3.zero);

                        // Recalculate ballistic target angle for true distance to aimpoint:
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
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("BallisticMissileGuidance_Initialize_Patch", ex);
            }
        }
    }

    /// <summary>
    /// Injects horizontal track uncertainty displacement (Dx, Dz, Dy=0) into guided cannon shells (OpticalSeekerShell).
    /// </summary>
    [HarmonyPatch(typeof(OpticalSeekerShell), "Initialize", new Type[] { typeof(Unit), typeof(GlobalPosition) })]
    public static class OpticalSeekerShell_Initialize_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(
            OpticalSeekerShell __instance,
            Unit target,
            Missile ___missile,
            ref GlobalPosition ___knownPos)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive) return;
                if (__instance == null || ___missile == null) return;

                Unit t = (target != null && !target.disabled) ? target : SeekerDispersionHelper.GetTargetForMissile(___missile);
                if (t == null) return;

                Vector3 disp = SeekerDispersionHelper.GetDispersionForMissile(___missile, t);
                if (disp == Vector3.zero) return;

                Vector3 local = ___knownPos.ToLocalPosition();
                local.x += disp.x;
                local.z += disp.z;
                ___knownPos = local.ToGlobalPosition();

                ___missile.SetTarget(null);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("OpticalSeekerShell_Initialize_Patch", ex);
            }
        }
    }

    /// <summary>
    /// Gating check for guided cannon shells: requires target to be inside the terminal pitbull basket (2800m),
    /// within optical look angle, and unobstructed before visual lock can be acquired.
    /// </summary>
    [HarmonyPatch(typeof(OpticalSeekerShell), "TrackVisual")]
    public static class OpticalSeekerShell_TrackVisual_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(
            OpticalSeekerShell __instance,
            ref bool __result,
            ref float ___lastVisualCheck,
            Unit ___targetUnit,
            Missile ___missile,
            GlobalPosition ___knownPos)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive) return true;
                if (__instance == null || ___missile == null)
                {
                    __result = false;
                    return false;
                }

                ___lastVisualCheck = Time.timeSinceLevelLoad;

                Unit target = ___targetUnit;
                if (target == null || target.disabled)
                {
                    __result = false;
                    return false;
                }

                Vector3 shellPos = ___missile.transform.position;
                Vector3 targetPos = target.transform.position;
                float distToTarget = Vector3.Distance(shellPos, targetPos);

                float pitbullDist = SeekerDispersionHelper.TerminalActivationDistance;

                // 1. Terminal Pitbull Basket check
                Vector3 disp = Vector3.zero;
                if (TrackUncertaintyCalculator.TryGetMissileDispersion(___missile, out disp))
                {
                    if (disp.magnitude > pitbullDist)
                    {
                        __result = false;
                        return false;
                    }
                }

                // 2. Optical look angle check (must be within seeker FOV)
                Vector3 toTarget = (targetPos - shellPos).normalized;
                float lookAngle = Vector3.Angle(___missile.transform.forward, toTarget);
                if (lookAngle > 45.0f)
                {
                    __result = false;
                    return false;
                }

                // 3. Sensor acquisition range
                if (distToTarget > pitbullDist)
                {
                    __result = false;
                    return false;
                }

                // 4. Line of Sight
                __result = target.LineOfSight(shellPos, pitbullDist);
                return false;
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("OpticalSeekerShell_TrackVisual_Patch", ex);
                return true;
            }
        }
    }

    /// <summary>
    /// Guided cannon shell guidance: maintains the faction-known (displaced) aimpoint while the seeker has no visual,
    /// or keeps the previous datum when the faction has no track.
    /// </summary>
    [HarmonyPatch(typeof(OpticalSeekerShell), "GetTargetParameters")]
    public static class OpticalSeekerShell_GetTargetParameters_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(GlobalPosition ___knownPos, out GlobalPosition __state)
        {
            __state = ___knownPos;
        }

        [HarmonyPostfix]
        public static void Postfix(
            OpticalSeekerShell __instance,
            bool ___hasVisual,
            Unit ___targetUnit,
            Missile ___missile,
            ref GlobalPosition ___knownPos,
            GlobalPosition __state)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive) return;
                if (__instance == null || ___missile == null) return;
                if (___hasVisual) return;

                Unit target = ___targetUnit;
                if (target == null || target.disabled) return;

                GlobalPosition known;
                Vector3 knownVel;
                ___knownPos = SeekerDispersionHelper.TryGetDatalinkTargetState(___missile, target, out known, out knownVel)
                    ? known
                    : __state;

                ___missile.SetTarget(null);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("OpticalSeekerShell_GetTargetParameters_Patch", ex);
            }
        }
    }

}
