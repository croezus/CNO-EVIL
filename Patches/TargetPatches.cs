using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using CnoEvil.Components;
using CnoEvil.Core;
using CnoEvil.Config;

namespace CnoEvil.Patches
{
    [HarmonyPatch(typeof(Unit), "InitializeUnit")]
    public static class Unit_InitializeUnit_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(Unit __instance)
        {
            if (__instance == null) return;

            PhysRadarTarget target = __instance.gameObject.GetComponent<PhysRadarTarget>();
            if (target == null)
            {
                target = __instance.gameObject.AddComponent<PhysRadarTarget>();
            }
            target.InitializeProfile();

            DatalinkNetwork.RegisterUnit(__instance);
        }
    }

    [HarmonyPatch(typeof(Unit), "OnDestroy")]
    public static class Unit_OnDestroy_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(Unit __instance)
        {
            if (__instance == null) return;

            Missile missile = __instance as Missile;
            if (missile != null)
            {
                int id = missile.GetInstanceID();
                ARHSeeker_GetRadarReturn_Patch.ClearCoastTimer(id);
                SARHSeeker_GetTrackingStrength_Patch.ClearCoastTimer(id);
                DatalinkNetwork.DeregisterMissile(missile);
                TrackUncertaintyCalculator.UnregisterMissile(missile);
            }
            else
            {
                DatalinkNetwork.DeregisterUnit(__instance);
            }

            RWRTriangulationProcessor.RemoveTrack(__instance);
            VanillaKnowledgeExpiry.RemoveUnit(__instance);
        }
    }

    /// <summary>
    /// Visual reconnaissance from the vanilla optics. Each vanilla TargetDetector check (every checkInterval s:
    /// target within visualRange, visibility x magnification and maxSpeed, terrain line of sight by the vanilla raycast job)
    /// that finds a target gives the spotter's faction a visual contact (DatalinkNetwork.OnVisualDetection), held for
    /// 1.5 x checkInterval. Every unit with optics is a spotter (aircraft, ships, all ground vehicles); it does not have to
    /// be a datalink node. Radars (visualRange 0) are skipped: their contacts come from the radar patches.
    /// Vanilla runs the checks on the server and for the local aircraft.
    /// </summary>
    [HarmonyPatch(typeof(TargetDetector), "DetectTarget")]
    public static class TargetDetector_DetectTarget_Patch
    {
        private const float HoldIntervalFactor = 1.5f;
        private static readonly Func<TargetDetector, float> f_checkInterval = FastReflection.CreateFieldGetter<TargetDetector, float>("checkInterval");

        [HarmonyPostfix]
        public static void Postfix(TargetDetector __instance, Unit target)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive) return;
                if (__instance == null || __instance is Radar || target == null) return;

                Unit spotter = __instance.GetAttachedUnit();
                if (spotter == null) return;
                float interval = (f_checkInterval != null) ? f_checkInterval(__instance) : 4.0f;
                DatalinkNetwork.OnVisualDetection(spotter, target, interval * HoldIntervalFactor);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("TargetDetector_DetectTarget_Patch", ex);
            }
        }
    }

    /// <summary>
    /// Aircraft optics follow the avionics generation instead of the vanilla magnification (CI-22 2, FS-12 0.7):
    /// range = target visibility x RWRTierDatabase.GetOpticsMagnification (AircraftOpticsMagnificationTier1..3).
    /// The vanilla visualRange still limits the candidates (VisualCheck). Vanilla detection uses this range too,
    /// so the vanilla tracking database, HUD markers and AI target search agree with the faction tracks.
    /// Ships, ground vehicles and radars stay vanilla; F10 restores vanilla.
    /// </summary>
    [HarmonyPatch(typeof(TargetDetector), "InVisualRange")]
    public static class TargetDetector_InVisualRange_Patch
    {
        private static readonly Func<TargetDetector, Transform> f_scanner = FastReflection.CreateFieldGetter<TargetDetector, Transform>("scanner");

        [HarmonyPrefix]
        public static bool Prefix(TargetDetector __instance, Unit target, ref bool __result)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive) return true;
                if (__instance == null || target == null || __instance is Radar || f_scanner == null) return true;

                Aircraft aircraft = __instance.GetAttachedUnit() as Aircraft;
                if (aircraft == null) return true;
                Transform scanner = f_scanner(__instance);
                if (scanner == null) return true;

                float magnification = RWRTierDatabase.GetOpticsMagnification(aircraft);
                __result = FastMath.InRange(scanner.GlobalPosition(), target.GlobalPosition(), target.GetVisibility() * magnification);
                return false;
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("TargetDetector_InVisualRange_Patch", ex);
                return true;
            }
        }
    }

    /// <summary>
    /// The faction's own track on a hostile threat (RWRTriangulationProcessor), used by AI doctrine. Vanilla FactionHQ
    /// queries (known position, accuracy, tracking) and everything shown on the HUD stay with the vanilla trackingDatabase.
    /// </summary>
    internal static class FactionTrackKnowledge
    {
        /// <summary>
        /// True when the faction holds a track on this hostile tracked threat (ThreatClassification).
        /// </summary>
        public static bool TryGetHostileTrack(FactionHQ hq, Unit unit, out TriangulationTrack track)
        {
            track = null;
            if (!CnoEvilConfig.IsModActive || hq == null || unit == null || unit is Missile) return false;
            if (unit.NetworkHQ != null && unit.NetworkHQ == hq) return false;
            if (!ThreatClassification.IsTrackedThreat(unit, hq)) return false;

            track = RWRTriangulationProcessor.GetTrack(hq, unit);
            return track != null;
        }
    }

    [HarmonyPatch(typeof(Aircraft), "Awake")]
    public static class Aircraft_Awake_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(Aircraft __instance)
        {
            if (__instance == null) return;

            PhysRWRReceiver rwr = __instance.gameObject.GetComponent<PhysRWRReceiver>();
            if (rwr == null)
            {
                __instance.gameObject.AddComponent<PhysRWRReceiver>();
            }
        }
    }

    [HarmonyPatch(typeof(Radar))]
    public static class Radar_Lifecycle_Patches
    {
        [HarmonyPatch("Awake")]
        [HarmonyPostfix]
        public static void Awake_Postfix(Radar __instance)
        {
            if (__instance == null) return;

            PhysRadarEmitter emitter = __instance.gameObject.GetComponent<PhysRadarEmitter>();
            if (emitter == null)
            {
                emitter = __instance.gameObject.AddComponent<PhysRadarEmitter>();
            }
            emitter.InitializeSpecs();
            DatalinkNetwork.RegisterRadar(__instance);
        }

        [HarmonyPatch("AttachToUnit")]
        [HarmonyPostfix]
        public static void AttachToUnit_Postfix(Radar __instance, Unit unit)
        {
            if (__instance == null) return;

            // Awake runs before the radar is attached, so the specs were derived without the owning unit
            PhysRadarEmitter emitter = PhysRadarEmitter.Get(__instance);
            if (emitter != null && unit != null)
            {
                emitter.AttachedUnit = unit;
                emitter.InitializeSpecs();
            }
            DatalinkNetwork.RegisterRadar(__instance);
        }

        [HarmonyPatch("OnDestroy")]
        [HarmonyPostfix]
        public static void OnDestroy_Postfix(Radar __instance)
        {
            if (__instance == null) return;
            DatalinkNetwork.DeregisterRadar(__instance);
        }
    }

    /// <summary>
    /// Vanilla RadarLocator registers the emitter of every RWR ping in the vanilla trackingDatabase
    /// (CmdUpdateTrackingInfo), which gives the HUD, the AI and the vanilla seekers the true position from one
    /// bearing. The vanilla method runs only while the faction has located the emitter: a recent precise contact,
    /// or tracking quality at or above MapReconnaissanceQualityThreshold (the same test as the map icons,
    /// UnitMapIcon_UpdateIcon_Patch.IsLocated). Vanilla then makes its own entry, its recon reward and its map icon.
    /// Without a recent precise contact, vanilla may only make the entry, not refresh it: each refresh writes the true
    /// position and TrackingInfo.GetPosition returns the live position for 4 s after it. The entry then ages the
    /// vanilla way at the locate point. The mod does not write to the vanilla database (ADR-14). Vanilla behaviour
    /// returns with F10.
    /// </summary>
    [HarmonyPatch(typeof(RadarLocator), "RadarLocator_OnRadarWarning")]
    public static class RadarLocator_OnRadarWarning_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(Aircraft.OnRadarWarning source, Aircraft ___aircraft)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive) return true;
                if (CnoEvilConfig.EnableRWRTriangulationSystem != null &&
                    !CnoEvilConfig.EnableRWRTriangulationSystem.Value) return true;

                Unit emitter = source.emitter;
                if (emitter == null || ___aircraft == null) return true;
                if (emitter is Missile) return true; // Missile seeker emissions stay vanilla (MWS, onlySurface filter)

                FactionHQ hq = ___aircraft.NetworkHQ;
                if (hq == null) return true; // Vanilla returns early itself
                if (!ThreatClassification.IsTrackedThreat(emitter, hq)) return true; // No mod track for this type

                // Read-only lookup: GetTrack would record a radar illumination as a side effect
                Dictionary<Unit, TriangulationTrack> tracks = RWRTriangulationProcessor.GetTrackDictionary(hq);
                TriangulationTrack track;
                if (tracks == null || !tracks.TryGetValue(emitter, out track)) return false;

                if (track.HasRecentPreciseContact) return true; // Real contact: the vanilla live position is correct
                if (!UnitMapIcon_UpdateIcon_Patch.IsLocated(track)) return false;

                // Located by passive fixes only: vanilla makes the entry, icon and reward once. A refresh would write
                // the true position again and open the 4 s live window of TrackingInfo.GetPosition again.
                return hq.GetTrackingData(emitter.persistentID) == null;
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("RadarLocator_OnRadarWarning_Patch", ex);
                return true;
            }
        }
    }
}
