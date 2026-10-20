using System;
using UnityEngine;
using CnoEvil.Config;

namespace CnoEvil.Core
{
    public enum RwrThreatState
    {
        None,
        Search,          // Wide-area low PRF search sweep
        Track,           // Dedicated Single-Target Track (STT) / Medium PRF
        MissileGuidance  // Continuous Wave (CW) or terminal active seeker illumination
    }

    public struct JammerSpecs
    {
        public float PowerWatts;          // Pjam (W) (e.g., 200W - 1000W)
        public float AntennaGainLinear;   // Gjam (~10 dBi ≈ 10.0)
        public float BandwidthHz;         // Bjam (e.g., 100 MHz barrage or 20 MHz spot)
        public float SystemLossLinear;    // Ljam (~2.0)

        public static JammerSpecs CreateStandardPod()
        {
            JammerSpecs s;
            s.PowerWatts = 450.0f;
            s.AntennaGainLinear = 8.0f;
            s.BandwidthHz = 40.0e6f; // 40 MHz spot noise
            s.SystemLossLinear = 1.8f;
            return s;
        }

        // Jammer transmit power per unit of vanilla ECM intensity (config: EcmWattsPerIntensity)
        public static float WattsPerEcmIntensity
        {
            get { return Mathf.Max(0.0f, CnoEvilConfig.EcmWattsPerIntensity.Value); }
        }

        /// <summary>
        /// Self-screening jammer radiating the vanilla ECM intensity (IRadarReturn.GetECMIntensity()).
        /// Single scale shared by every radar / seeker path so all receivers see the same jammer.
        /// </summary>
        public static JammerSpecs CreateForIntensity(float ecmIntensity)
        {
            JammerSpecs s = CreateStandardPod();
            s.PowerWatts = Mathf.Max(0.0f, ecmIntensity) * WattsPerEcmIntensity;
            return s;
        }
    }

    public static class ElectronicWarfare
    {
        public const float FourPiSquared = 157.91367f; // (4 * pi)^2

        /// <summary>
        /// JammerBurnThroughRatio K (config). Every radar and seeker receiver divides Pj by K.
        /// </summary>
        public static float BurnThroughRatio
        {
            get
            {
                return (CnoEvilConfig.JammerBurnThroughRatio != null)
                    ? Mathf.Max(0.1f, CnoEvilConfig.JammerBurnThroughRatio.Value)
                    : 1.0f;
            }
        }

        /// <summary>
        /// Calculates received noise jamming power Pj (Watts) at the radar receiver using 1/R² one-way path loss:
        /// Pj = (Pjam * Gjam * Gr * λ² * Br) / ((4π)² * R² * Bjam * Ljam)
        /// </summary>
        public static float CalculateJammerPowerAtReceiver(
            float rangeMeters,
            ref JammerSpecs jammer,
            ref RadarSpecs radar)
        {
            return CalculateJammerPowerAtReceiver(rangeMeters, ref jammer, ref radar, radar.AntennaGainLinear);
        }

        /// <summary>
        /// Same as above with the receiver antenna gain toward the jammer (Gr): main lobe = radar.AntennaGainLinear,
        /// a jammer off the receiver beam axis enters with less (sidelobe) gain.
        /// </summary>
        public static float CalculateJammerPowerAtReceiver(
            float rangeMeters,
            ref JammerSpecs jammer,
            ref RadarSpecs radar,
            float receiverGainLinear)
        {
            if (rangeMeters < 50.0f) rangeMeters = 50.0f;
            if (jammer.PowerWatts <= 0.0f) return 0.0f;

            float r2 = rangeMeters * rangeMeters;

            // Bandwidth ratio factor (portion of jamming noise that enters the radar receiver IF filter)
            float bwRatio = Mathf.Clamp01(radar.BandwidthHz / Mathf.Max(radar.BandwidthHz, jammer.BandwidthHz));

            float numerator = jammer.PowerWatts * jammer.AntennaGainLinear * receiverGainLinear *
                              (radar.WavelengthMeters * radar.WavelengthMeters) * bwRatio;

            float denominator = FourPiSquared * r2 * jammer.SystemLossLinear;
            if (denominator <= 0.0f) return 0.0f;

            return numerator / denominator;
        }

        /// <summary>
        /// Calculates the physical burn-through range R_burn (meters) where the processed echo Pr
        /// exceeds the received jamming Pj by the detection threshold (Pr / Pj = SNR_min).
        /// Dividing CalculateEchoPower by CalculateJammerPowerAtReceiver (λ² and one Gr cancel):
        /// R_burn² = (Pt * Gt * Gp * σ * Ljam * K) / (4π * Pjam * Gjam * (Br/Bjam) * SNR_min * Lsys)
        /// where K is the JammerBurnThroughRatio the caller applied to Pj (Pj / K).
        /// Atmospheric loss is neglected (sub-dB at burn-through ranges).
        /// </summary>
        public static float CalculateBurnThroughRange(
            float targetRcsSqm,
            float snrThreshold_dB,
            ref JammerSpecs jammer,
            ref RadarSpecs radar,
            float burnThroughRatio = 1.0f)
        {
            if (jammer.PowerWatts <= 0.0f) return 100000.0f;
            if (targetRcsSqm <= 0.001f) targetRcsSqm = 0.001f;

            float snrThresholdLinear = Mathf.Pow(10.0f, snrThreshold_dB * 0.1f);
            float bwRatio = Mathf.Clamp01(radar.BandwidthHz / Mathf.Max(radar.BandwidthHz, jammer.BandwidthHz));
            float procGain = Mathf.Max(1.0f, radar.ProcessingGainLinear);

            float numerator = radar.PeakPowerWatts * CnoEvilConfig.RadarPowerFactor * radar.AntennaGainLinear * procGain * targetRcsSqm *
                              jammer.SystemLossLinear * Mathf.Max(0.1f, burnThroughRatio);
            float denominator = (4.0f * Mathf.PI) * jammer.PowerWatts * jammer.AntennaGainLinear *
                                bwRatio * snrThresholdLinear * radar.SystemLossLinear;

            if (denominator <= 0.0f) return 100000.0f;

            float rBurnSquared = numerator / denominator;
            if (rBurnSquared <= 0.0f) return 100.0f;

            return Mathf.Sqrt(rBurnSquared);
        }

        /// <summary>
        /// Evaluates Home-On-Jam (HOJ) seeker guidance.
        /// HOJ is strictly disabled if the target is in the notch gate!
        /// When a target radiates ECM while flying straight, the seeker tracks the jamming strobe angle.
        /// </summary>
        public static bool EvaluateHOJGuidance(
            float ecmIntensity,
            bool isInsideClutterNotch,
            float slantRange)
        {
            if (ecmIntensity < 0.15f) return false;

            // If the target is inside the notch, HOJ is strictly blocked!
            if (isInsideClutterNotch)
            {
                return false;
            }

            // At point-blank range (< 150m), radar echo dominates
            if (slantRange < 150.0f) return false;

            return true;
        }

        // Seeker angle gate used while coasting on memory: ~4° Ku-band seeker beam → ~2° half-beamwidth
        public const float SeekerHalfBeamwidthRad = 0.035f;
        public const float MinMemoryCoastGateMeters = 30.0f;

        /// <summary>
        /// Fraction of the seeker angle gate usable while coasting.
        /// ECM (VGPO/RGPO) corrupts the last velocity estimate; clutter residue degrades it less.
        /// </summary>
        private static float CalculateTrackMemoryRetention(float ecmIntensity, bool hasClutterBackground)
        {
            if (ecmIntensity > 0.1f) return 0.3f;
            if (hasClutterBackground) return 0.7f;
            return 1.0f;
        }

        /// <summary>
        /// Memory coast (dead reckoning) after the target drops into the Doppler notch.
        /// The seeker extrapolates the target from its state at coast start; the track survives while the
        /// prediction error stays inside the seeker gate (range * tan(half-beamwidth) * retention)
        /// and the coast has not exceeded maxCoastSeconds. A target that keeps flying straight in the notch
        /// stays inside the gate; one that turns or jinks walks out of it.
        /// </summary>
        public static bool EvaluateMemoryCoast(
            ref MemoryCoastState state,
            float now,
            GlobalPosition targetPosition,
            float seekerRangeMeters,
            float ecmIntensity,
            bool hasClutterBackground,
            float maxCoastSeconds,
            out float predictionErrorMeters)
        {
            float t = now - state.StartTime;
            GlobalPosition predicted = state.TargetPositionAtStart + state.TargetVelocityAtStart * t;
            predictionErrorMeters = (targetPosition - predicted).magnitude;

            if (t >= maxCoastSeconds) return false;

            float gate = Mathf.Max(MinMemoryCoastGateMeters, seekerRangeMeters * Mathf.Tan(SeekerHalfBeamwidthRad));
            gate *= CalculateTrackMemoryRetention(ecmIntensity, hasClutterBackground);
            return predictionErrorMeters <= gate;
        }
    }

    /// <summary>
    /// Target state frozen when a seeker enters memory coast (global coordinates survive floating-origin shifts).
    /// </summary>
    public struct MemoryCoastState
    {
        public float StartTime;
        public GlobalPosition TargetPositionAtStart;
        public Vector3 TargetVelocityAtStart;

        public static MemoryCoastState Begin(float now, GlobalPosition targetPosition, Vector3 targetVelocity)
        {
            MemoryCoastState s;
            s.StartTime = now;
            s.TargetPositionAtStart = targetPosition;
            s.TargetVelocityAtStart = targetVelocity;
            return s;
        }
    }
}
