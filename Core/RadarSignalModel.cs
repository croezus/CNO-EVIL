using UnityEngine;
using CnoEvil.Config;
using CnoEvil.Components;

namespace CnoEvil.Core
{
    /// <summary>
    /// Skin-return signal of a search/track radar against one target, with every term of the SNR.
    /// </summary>
    public struct RadarSkinSignal
    {
        public float DynamicRcs;
        public float StoresRcs;
        public float EcmIntensity;
        public bool JammerActive;
        public DopplerResult Doppler;
        public float EchoPower;
        public float NoiseFloor;
        public float ClutterPower;
        public float JammerPower;      // self-screen ECM of the target + stand-off jamming
        public float StandoffJamPower; // stand-off jamming part (jamming pods)
        public float BurnRange;
        public float SnrDb;
    }

    /// <summary>
    /// Signal calculation shared by the server (radar scan, ARH seeker) and the client RWR. The client gets only
    /// the emitter in the warning RPC and runs the same calculation, so its strobe matches the host value.
    /// No side effects: callers do LOS, horizon, memory coast and telemetry.
    /// </summary>
    public static class RadarSignalModel
    {
        /// <summary>
        /// Stand-off jamming (jamming pods) in a seeker receiver at seekerPos that looks at the target.
        /// </summary>
        public static float SeekerStandoffJamPower(Missile missile, Vector3 seekerPos, Vector3 targetPos, ref RadarSpecs receiverSpecs)
        {
            if (missile == null) return 0.0f;
            return StandoffJamming.JamPowerAtReceiver(seekerPos, targetPos - seekerPos, ref receiverSpecs,
                CnoEvilConfig.EffectiveSeekerSidelobeClutterDb, missile.NetworkHQ, missile.GetInstanceID());
        }

        /// <summary>
        /// Aspect RCS, ECM, Doppler notch, radar equation, jamming, clutter and SNR of a radar against a target.
        /// </summary>
        public static RadarSkinSignal EvaluateRadar(Radar radar, IRadarReturn radarReturn, Unit target, Vector3 radarPos, Vector3 radarVel, float dist)
        {
            RadarSkinSignal s = new RadarSkinSignal();

            Vector3 targetPos = target.transform.position;
            Vector3 targetVel = target.rb != null ? target.rb.velocity : Vector3.zero;

            // Dynamic 3D aspect RCS & HERM
            PhysRadarTarget physTarget = PhysRadarTarget.Get(target);
            s.DynamicRcs = target.RCS;
            bool isHeliHERM = false;
            float rotorFlashRcs = 0.0f;
            if (physTarget != null)
            {
                s.DynamicRcs = physTarget.GetDynamicRCS(radarPos);
                s.StoresRcs = physTarget.CachedStoresPenalty;
                isHeliHERM = physTarget.IsHelicopterWithHERM();
                rotorFlashRcs = physTarget.GetRotorFlashRCS();
            }

            // Electronic jamming status
            s.EcmIntensity = (radarReturn != null) ? radarReturn.GetECMIntensity() : 0.0f;
            s.JammerActive = s.EcmIntensity > 0.01f;

            // Pulse-Doppler ground clutter notch filter (widened by ECM Doppler noise)
            s.Doppler = DopplerClutterProcessor.ProcessDopplerAndClutter(
                radarPos,
                radarVel,
                targetPos,
                targetVel,
                CnoEvilConfig.EffectiveNotchThreshold,
                isHeliHERM,
                s.EcmIntensity,
                target.radarAlt,
                target is Ship
            );

            // Radar range equation (cached specs from the emitter when available)
            PhysRadarEmitter emitter = PhysRadarEmitter.Get(radar);
            RadarSpecs specs;
            if (emitter != null && emitter.ThermalNoiseFloor > 0.0f)
            {
                specs = emitter.Specs;
                s.NoiseFloor = emitter.ThermalNoiseFloor;
            }
            else
            {
                specs = RadarSpecs.CreateFromVanilla(
                    radar.RadarParameters.maxRange,
                    radar.RadarParameters.maxSignal,
                    radar.RadarParameters.minSignal
                );
                s.NoiseFloor = RadarPhysics.CalculateThermalNoiseFloor(specs.BandwidthHz, specs.NoiseFigureLinear);
            }

            // The notch itself is the TrackRejectedByNotch gate plus the main-lobe clutter power below.
            // Only a HERM helicopter in the notch loses echo: its hull line is filtered, the rotor flash passes.
            float filteredRcs = DopplerClutterProcessor.FilteredTargetRcs(ref s.Doppler, s.DynamicRcs, rotorFlashRcs);
            s.EchoPower = RadarPhysics.CalculateEchoPower(dist, filteredRcs, ref specs);

            if (s.JammerActive)
            {
                JammerSpecs jammer = JammerSpecs.CreateForIntensity(s.EcmIntensity);
                float burnThroughRatio = ElectronicWarfare.BurnThroughRatio;
                s.JammerPower = ElectronicWarfare.CalculateJammerPowerAtReceiver(dist, ref jammer, ref specs) / burnThroughRatio;
                s.BurnRange = ElectronicWarfare.CalculateBurnThroughRange(filteredRcs,CnoEvilConfig.EffectiveMinSNR, ref jammer, ref specs, burnThroughRatio);
            }

            // Stand-off jamming of the radar receiver (jamming pod beams): main lobe toward the target, sidelobes elsewhere
            Unit radarUnit = radar.GetAttachedUnit();
            s.StandoffJamPower = StandoffJamming.JamPowerAtReceiver(radarPos, targetPos - radarPos, ref specs,
                CnoEvilConfig.EffectiveRadarSidelobeClutterDb, (radarUnit != null) ? radarUnit.NetworkHQ : null, radar.GetInstanceID());
            if (s.StandoffJamPower > 0.0f)
            {
                s.JammerPower += s.StandoffJamPower;
                s.JammerActive = true;
            }

            // Ground clutter left in the target Doppler filter (main lobe in the notch, sidelobes in the clutter band)
            float radarHeightAgl = (radarUnit != null) ? radarUnit.radarAlt : 0.0f;
            s.ClutterPower = RadarPhysics.CalculateClutterPower(dist, s.Doppler.ElevationAngleDeg, radarHeightAgl, s.Doppler.TargetHeightAgl, ref specs, s.Doppler.SeaSurfaceTarget) *
                             DopplerClutterProcessor.ClutterDopplerSuppression(ref s.Doppler, CnoEvilConfig.EffectiveRadarSidelobeClutterDb);
            s.SnrDb = RadarPhysics.CalculateSNRdB(s.EchoPower, s.NoiseFloor, s.ClutterPower, s.JammerPower);
            return s;
        }

        /// <summary>
        /// Aspect RCS, ECM and Doppler notch of an ARH seeker against its target (before memory coast).
        /// dynamicRcs is the RCS that passes the Doppler filter.
        /// </summary>
        public static DopplerResult EvaluateArhDoppler(Vector3 missilePos, Vector3 missileVel, Unit target, out float dynamicRcs, out float ecmIntensity)
        {
            Vector3 targetPos = target.transform.position;
            Vector3 targetVel = target.rb != null ? target.rb.velocity : Vector3.zero;

            // Aspect-dependent RCS & HERM
            PhysRadarTarget physTarget = PhysRadarTarget.Get(target);
            dynamicRcs = target.RCS;
            bool isHeliHERM = false;
            float rotorFlashRcs = 0.0f;
            if (physTarget != null)
            {
                dynamicRcs = physTarget.GetDynamicRCS(missilePos);
                isHeliHERM = physTarget.IsHelicopterWithHERM();
                rotorFlashRcs = physTarget.GetRotorFlashRCS();
            }

            // Electronic jamming status
            IRadarReturn radarReturn = target as IRadarReturn;
            ecmIntensity = (radarReturn != null) ? radarReturn.GetECMIntensity() : 0.0f;

            // Pulse-Doppler notch filter (expanded by ECM Doppler smearing / VGPO)
            DopplerResult doppler = DopplerClutterProcessor.ProcessDopplerAndClutter(
                missilePos,
                missileVel,
                targetPos,
                targetVel,
                CnoEvilConfig.EffectiveNotchThreshold,
                isHeliHERM,
                ecmIntensity,
                target.radarAlt,
                target is Ship
            );

            // Seeker SNR uses the RCS that passes the Doppler filter (HERM helicopter in the notch: rotor flash only)
            dynamicRcs = DopplerClutterProcessor.FilteredTargetRcs(ref doppler, dynamicRcs, rotorFlashRcs);
            return doppler;
        }

        /// <summary>
        /// Ku-band active seeker SNR (calibrated Tier 0), with the target ECM and the stand-off jamming at the seeker.
        /// </summary>
        public static float EvaluateArhSnrDb(Missile missile, Vector3 targetPos, float dist, float dynamicRcs, float ecmIntensity, ref DopplerResult doppler, out float echoPower)
        {
            RadarSpecs specs = RadarSpecs.CreateSeekerRadar();

            float noiseFloor = RadarPhysics.CalculateThermalNoiseFloor(specs.BandwidthHz, specs.NoiseFigureLinear);
            echoPower = RadarPhysics.CalculateEchoPower(dist, dynamicRcs, ref specs);

            float jammerPower = 0.0f;
            if (ecmIntensity > 0.01f)
            {
                JammerSpecs jammer = JammerSpecs.CreateForIntensity(ecmIntensity);
                jammerPower = ElectronicWarfare.CalculateJammerPowerAtReceiver(dist, ref jammer, ref specs) / ElectronicWarfare.BurnThroughRatio;
            }
            float seekerHeightAgl = 0.0f;
            if (missile != null)
            {
                seekerHeightAgl = missile.radarAlt;
                jammerPower += SeekerStandoffJamPower(missile, missile.transform.position, targetPos, ref specs);
            }

            float clutterPower = RadarPhysics.CalculateClutterPower(dist, doppler.ElevationAngleDeg, seekerHeightAgl, doppler.TargetHeightAgl, ref specs, doppler.SeaSurfaceTarget) *
                                 DopplerClutterProcessor.ClutterDopplerSuppression(ref doppler, CnoEvilConfig.EffectiveSeekerSidelobeClutterDb);
            return RadarPhysics.CalculateSNRdB(echoPower, noiseFloor, clutterPower, jammerPower);
        }

        /// <summary>
        /// ARH seeker return strength: skin return 1..15 from SNR, Home-On-Jam strobe 3..8 from ECM, 0 if not tracked.
        /// </summary>
        public static float ArhSeekerStrength(float snrDb, float ecmIntensity, bool isHojHoming)
        {
            if (snrDb >= CnoEvilConfig.EffectiveMinSNR)
            {
                return Mathf.Clamp((snrDb - CnoEvilConfig.EffectiveMinSNR) * 0.5f + 1.0f, 1.0f, 15.0f);
            }
            if (isHojHoming)
            {
                return Mathf.Clamp(ecmIntensity * 6.0f, 3.0f, 8.0f);
            }
            return 0.0f;
        }

        /// <summary>
        /// RWR signal 0..1 of a radar ping: 0 at the detection threshold, 1 at 20 dB above it.
        /// </summary>
        public static float RadarPingSignal01(float snrDb)
        {
            return Mathf.Clamp01((snrDb - CnoEvilConfig.EffectiveMinSNR) / 20.0f);
        }

        /// <summary>
        /// EM scan ring dip 0..1 of an illumination below the detection threshold: 0 at the threshold,
        /// 1 at 20 dB below it. 0 at or above the threshold.
        /// </summary>
        public static float BelowThresholdDepth01(float snrDb)
        {
            return Mathf.Clamp01((CnoEvilConfig.EffectiveMinSNR - snrDb) / 20.0f);
        }

        /// <summary>
        /// RWR signal 0..1 of a seeker ping from the seeker return strength.
        /// </summary>
        public static float SeekerPingSignal01(float strength)
        {
            return Mathf.Clamp01(strength / 10.0f);
        }
    }
}
