using System;
using UnityEngine;
using CnoEvil.Config;

namespace CnoEvil.Core
{
    /// <summary>
    /// Vanilla jam tick of a JammingPod (EW-25 Medusa). The vanilla jam of the primary target gives the capacitor
    /// fraction of the tick: StandoffJamming registers the beam, and the mod physics (radar and seeker noise) uses it.
    /// The same vanilla Unit.Jam event also goes to every hostile unit with a jam receiver inside the beam cone, scaled by
    /// the beam pattern and the vanilla range falloff of the pod. These events only drive vanilla side effects
    /// (ship CIWS, vanilla home-on-jam, the jammed label); the mod physics does not read the vanilla jam accumulation.
    /// No extra capacitor charge is drawn.
    /// </summary>
    public static class JammingConeProcessor
    {
        // Beam pattern: flat-top (4th-order super-Gaussian) gain, -10 dB at the cone edge, 0 outside
        private const float EdgeGainLn = 2.302585f; // ln(10)
        private const float MinJamAmount = 0.001f;
        private const float MinFalloff = 0.01f;
        // The radar scan point is near the unit: the first cone test on the unit position uses this cosine margin
        private const float ScanPointConeMargin = 0.01f;

        private static Func<Unit, Action<Unit.JamEventArgs>> s_onJamGetter;
        private static bool s_gettersInitialized;

        // Pod whose vanilla FixedUpdate is running now (set by JammingPod_FixedUpdate_Patch)
        private static JammingPod s_activePod;

        public static void BeginPodTick(JammingPod pod)
        {
            s_activePod = pod;
        }

        public static void EndPodTick()
        {
            s_activePod = null;
        }

        public static void ClearAll()
        {
            s_activePod = null;
        }

        /// <summary>
        /// Full cone angle (deg), 0 or 5-179. 0 = vanilla events to the single target.
        /// </summary>
        public static float ConeAngleDeg
        {
            get
            {
                float angle = (CnoEvilConfig.JammingPodConeAngle_deg != null) ? CnoEvilConfig.JammingPodConeAngle_deg.Value : 0.0f;
                if (angle <= 0.0f) return 0.0f;
                return Mathf.Clamp(angle, StandoffJamming.MinPhysicsConeDeg, 179.0f);
            }
        }

        /// <summary>
        /// Beam gain at u = off-axis angle / cone half-angle: exp(-ln10 * u^4).
        /// u = 0.5 -> 0.87 (-0.6 dB), u = 0.7 -> 0.58, u = 1 -> 0.10 (-10 dB), u > 1 -> 0.
        /// </summary>
        public static float BeamGain(float u)
        {
            if (u < 0.0f) u = -u;
            if (u > 1.0f) return 0.0f;
            float u2 = u * u;
            return Mathf.Exp(-EdgeGainLn * u2 * u2);
        }

        /// <summary>
        /// Called after vanilla Unit.Jam. When the event is the vanilla jam of the active pod, the beam is registered
        /// and the same tick is spread over the cone.
        /// </summary>
        public static void OnUnitJammed(Unit jammed, Unit.JamEventArgs args)
        {
            JammingPod pod = s_activePod;
            if (pod == null) return;
            if (args.jammingUnit == null || args.jammingUnit != pod.attachedUnit) return;

            // Recursion guard: the cone jams below go through Unit.Jam too
            s_activePod = null;
            if (jammed == null) return;

            AnimationCurve falloff = StandoffJamming.GetRangeFalloff(pod);
            if (falloff == null) return;

            // Same distance as vanilla (pod transform to target transform), so the power fraction is exact
            float f0 = falloff.Evaluate(Vector3.Distance(pod.transform.position, jammed.transform.position));
            if (f0 < MinFalloff) return;
            float powerFraction = Mathf.Clamp01(args.jamAmount / f0);

            StandoffJamming.RegisterPodTick(pod, jammed, powerFraction, StandoffJamming.CurveMaxRange(falloff));
            Spread(pod, jammed, powerFraction, falloff);
        }

        private static void Spread(JammingPod pod, Unit primary, float powerFraction, AnimationCurve falloff)
        {
            float coneDeg = ConeAngleDeg;
            if (coneDeg <= 0.0f || powerFraction <= MinJamAmount) return;

            if (!s_gettersInitialized)
            {
                s_gettersInitialized = true;
                s_onJamGetter = FastReflection.CreateFieldGetter<Unit, Action<Unit.JamEventArgs>>("onJam");
            }
            if (s_onJamGetter == null) return;

            Unit owner = pod.attachedUnit;
            if (owner == null) return;
            FactionHQ ownerHq = owner.NetworkHQ;

            Vector3 origin = StandoffJamming.GetPodOrigin(pod);

            Vector3 axis = primary.transform.position - origin;
            float d0 = axis.magnitude;
            if (d0 < 1.0f) return;
            axis /= d0;

            float halfAngleRad = coneDeg * 0.5f * Mathf.Deg2Rad;
            float cosEdge = Mathf.Cos(halfAngleRad);

            var units = UnitRegistry.allUnits;
            if (units == null) return;

            for (int i = 0; i < units.Count; i++)
            {
                Unit u = units[i];
                if (u == null || u == primary || u == owner || u.disabled) continue;

                // Hostile only: friendly and neutral receivers are not jammed (vanilla targets hostiles only)
                FactionHQ hq = u.NetworkHQ;
                if (hq == null || hq == ownerHq) continue;

                // No jam receiver (radar, radar seeker, HUD) listens on this unit
                if (s_onJamGetter(u) == null) continue;

                // Cone test on the unit position first (no transform lookups for units outside the beam)
                Vector3 toUnit = u.transform.position - origin;
                float d = toUnit.magnitude;
                if (d < 1.0f) continue;
                float cosTheta = Vector3.Dot(axis, toUnit) / d;
                if (cosTheta < cosEdge - ScanPointConeMargin) continue;

                Vector3 victimPos = u.transform.position;
                Transform scanPoint = (u.radar != null) ? u.radar.GetScanPoint() : null;
                if (scanPoint != null)
                {
                    victimPos = scanPoint.position;
                    toUnit = victimPos - origin;
                    d = toUnit.magnitude;
                    if (d < 1.0f) continue;
                    cosTheta = Vector3.Dot(axis, toUnit) / d;
                }
                if (cosTheta < cosEdge) continue;

                float theta = Mathf.Acos(Mathf.Clamp(cosTheta, -1.0f, 1.0f));
                float gain = BeamGain(theta / halfAngleRad);
                if (gain <= 0.0f) continue;

                float taper = StandoffJamming.BeyondTargetFactor(d, d0);
                if (taper <= 0.0f) continue;

                float amount = powerFraction * falloff.Evaluate(d) * gain * taper;
                if (amount < MinJamAmount) continue;

                // Terrain and buildings block the beam (same test as vanilla for the primary target)
                RaycastHit hit;
                if (Physics.Linecast(origin, victimPos, out hit, PhysicsLayers.StaticsMask)) continue;

                Unit.JamEventArgs jam = new Unit.JamEventArgs();
                jam.jamAmount = amount;
                jam.jammingUnit = owner;
                u.Jam(jam);
            }
        }
    }
}
