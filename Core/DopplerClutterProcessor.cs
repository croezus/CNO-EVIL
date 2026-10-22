using System;
using UnityEngine;
using CnoEvil.Config;

namespace CnoEvil.Core
{
    public struct DopplerResult
    {
        public float RelativeRadialVelocity;  // Closing speed (+ toward radar, - away) (m/s)
        public float TargetGroundRadialSpeed; // Target speed projected onto LOS (m/s)
        public float ElevationAngleDeg;       // Angle relative to horizontal plane (+ up, - down)
        public bool IsLookingDown;            // True if radar is looking down against terrain background
        public bool HasClutterBackground;     // True if ground/terrain clutter backdrop exists behind/around target
        public bool IsInsideClutterNotch;     // True if ground-relative speed < effective notch threshold
        public bool TrackRejectedByNotch;     // True if notch filter drops the return
        public bool IsHOJVulnerable;          // True if ECM is active outside look-down clutter notch
        public float PlatformSpeed;           // Radar platform speed (m/s): half-width of the sidelobe clutter band
        public float EffectiveNotch;          // Notch half-width with ECM widening (m/s)
        public bool RotorModulation;          // Helicopter HERM: rotor sidebands lie outside the main-lobe clutter line
        public bool SeaSurfaceTarget;         // Ship: sea-surface search, no notch rejection, sea clutter in the cell
    }

    public static class DopplerClutterProcessor
    {
        /// <summary>
        /// Evaluates radial velocity, ground clutter notch gate, and look-down conditions,
        /// taking into account ECM Doppler smearing (VGPO) and HOJ vulnerability.
        /// A sea-surface target (ship) is never rejected by the notch: sea-search modes detect it by its return
        /// against the sea clutter of its range cell (non-coherent processing), so the clutter power decides.
        /// </summary>
        public static DopplerResult ProcessDopplerAndClutter(
            Vector3 radarPosition,
            Vector3 radarVelocity,
            Vector3 targetPosition,
            Vector3 targetVelocity,
            float notchThreshold_mps,
            bool isHelicopterWithHERM,
            float targetEcmIntensity = 0.0f,
            float targetAltitudeAboveTerrain = 500.0f,
            bool seaSurfaceTarget = false)
        {
            DopplerResult res;

            Vector3 losVec = targetPosition - radarPosition;
            float dist = losVec.magnitude;
            if (dist < 1.0f) dist = 1.0f;
            Vector3 losUnit = losVec / dist;

            // Relative closing velocity: positive if closing, negative if opening
            Vector3 relVel = targetVelocity - radarVelocity;
            res.RelativeRadialVelocity = -Vector3.Dot(relVel, losUnit);

            // Ground-relative radial velocity of target:
            // When beaming (flying perpendicular to radar beam), Dot(targetVelocity, losUnit) ≈ 0
            res.TargetGroundRadialSpeed = Mathf.Abs(Vector3.Dot(targetVelocity, losUnit));

            // Elevation angle of beam
            float horizontalDist = Mathf.Sqrt(losVec.x * losVec.x + losVec.z * losVec.z);
            res.ElevationAngleDeg = Mathf.Atan2(losVec.y, Mathf.Max(1.0f, horizontalDist)) * Mathf.Rad2Deg;

            // Strict geometric look-down: radar line-of-sight points below the local horizon
            bool isGeometricLookDown = res.ElevationAngleDeg < -0.2f;
            res.IsLookingDown = isGeometricLookDown;

            // Clutter background condition:
            // Ground clutter behind target in the antenna main beam is present if:
            // 1) Geometric look-down against terrain surface
            // 2) Nap-Of-the-Earth (NOE) flight (< 120m AGL)
            // 3) Low elevation near horizon (< 1.5° elevation and < 300m AGL)
            bool hasClutterBackground = isGeometricLookDown ||
                                        (targetAltitudeAboveTerrain < 120.0f) ||
                                        (targetAltitudeAboveTerrain < 300.0f && res.ElevationAngleDeg < 1.5f);
            res.HasClutterBackground = hasClutterBackground;

            // Calculate effective notch threshold factoring in ECM Doppler noise smearing (VGPO)
            float expansionMultiplier = (CnoEvilConfig.ECMNotchWidening != null)
                ? Mathf.Max(0.0f, CnoEvilConfig.ECMNotchWidening.Value)
                : 1.5f;
            float effectiveNotch = notchThreshold_mps * (1.0f + Mathf.Clamp01(targetEcmIntensity) * expansionMultiplier);
            res.EffectiveNotch = effectiveNotch;
            res.PlatformSpeed = radarVelocity.magnitude;
            res.RotorModulation = isHelicopterWithHERM;
            res.SeaSurfaceTarget = seaSurfaceTarget;

            // Beaming / Notch check:
            res.IsInsideClutterNotch = res.TargetGroundRadialSpeed < effectiveNotch;

            // Rejection logic:
            if (seaSurfaceTarget)
            {
                // Sea-surface search: the clutter power of the cell (ClutterDopplerSuppression = 1 in the notch) decides
                res.TrackRejectedByNotch = false;
            }
            else if (res.IsInsideClutterNotch)
            {
                if (isHelicopterWithHERM)
                {
                    // Spinning rotor blades generate wideband micro-Doppler sidebands (200+ m/s)
                    // Helicopter remains visible despite zero hull translational velocity
                    res.TrackRejectedByNotch = false;
                }
                else if (targetEcmIntensity > 0.05f)
                {
                    // Prime Synergy: Active ECM + Beaming completely destroys Doppler tracker!
                    // DRFM VGPO smearing causes catastrophic velocity gate rejection on any altitude
                    res.TrackRejectedByNotch = true;
                }
                else if (hasClutterBackground)
                {
                    // Clean notch with ground clutter: target return is buried inside main-lobe or sidelobe ground clutter
                    res.TrackRejectedByNotch = true;
                }
                else
                {
                    // Clear / open sky (чисте небо) without ground clutter: filter is bypassed, notch is 90% ignored
                    res.TrackRejectedByNotch = false;
                }
            }
            else
            {
                res.TrackRejectedByNotch = false;
            }

            // HOJ vulnerability: HOJ can ONLY guide if target is flying straight outside the notch gate!
            // When beaming inside the notch, the Doppler channel and angular discrimination are collapsed
            if (targetEcmIntensity > 0.15f)
            {
                res.IsHOJVulnerable = !res.IsInsideClutterNotch;
            }
            else
            {
                res.IsHOJVulnerable = false;
            }

            return res;
        }

        /// <summary>
        /// RCS that passes the Doppler filter. A helicopter with HERM inside the notch against a clutter background
        /// keeps only its rotor blade flash: the hull line is in the clutter, the rotor sidebands are outside it.
        /// Every other target keeps its full RCS (the notch gate and the clutter power handle it).
        /// </summary>
        public static float FilteredTargetRcs(ref DopplerResult d, float dynamicRcs, float rotorFlashRcs)
        {
            if (d.RotorModulation && d.IsInsideClutterNotch && d.HasClutterBackground && rotorFlashRcs > 0.0f)
            {
                return Mathf.Min(dynamicRcs, rotorFlashRcs);
            }
            return dynamicRcs;
        }

        /// <summary>
        /// Part of the main-lobe clutter power that stays in the Doppler filter of the target (0..1):
        /// 1 inside the notch, a smooth decrease to the sidelobe level up to NotchClutterEdgeFactor x notch,
        /// the sidelobe level while the closing speed is inside the clutter band of the moving radar
        /// (|closing| &lt;= platform speed), 0 in the clutter-free region. 0 without a clutter background.
        /// A helicopter with HERM skips the notch and skirt: the radar tracks its rotor sidebands, not the hull line.
        /// </summary>
        public static float ClutterDopplerSuppression(ref DopplerResult d, float sidelobeLevelDb)
        {
            if (!d.HasClutterBackground) return 0.0f;

            float notch = d.EffectiveNotch;
            float v = d.TargetGroundRadialSpeed;
            if (v < notch && !d.RotorModulation) return 1.0f;

            float sidelobeDb = Mathf.Min(0.0f, sidelobeLevelDb);
            float edge = notch * Mathf.Max(1.0f, CnoEvilConfig.EffectiveNotchClutterEdgeFactor);
            if (v < edge && !d.RotorModulation)
            {
                // Main-lobe clutter skirt: dB-linear from 0 dB at the notch to the sidelobe level at the edge
                float t = (v - notch) / (edge - notch);
                return Mathf.Pow(10.0f, t * sidelobeDb * 0.1f);
            }

            bool inSidelobeBand = Mathf.Abs(d.RelativeRadialVelocity) <= d.PlatformSpeed;
            return inSidelobeBand ? Mathf.Pow(10.0f, sidelobeDb * 0.1f) : 0.0f;
        }
    }
}
