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

            try
            {
                PhysRadarTarget target = __instance.gameObject.GetComponent<PhysRadarTarget>();
                if (target == null)
                {
                    target = __instance.gameObject.AddComponent<PhysRadarTarget>();
                }
                target.InitializeProfile();
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("Unit_InitializeUnit_Patch.RadarTarget", ex);
            }

            try
            {
                DatalinkNetwork.RegisterUnit(__instance);

                Ship ship = __instance as Ship;
                if (ship != null) ShipRadarReturn.Register(ship);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("Unit_InitializeUnit_Patch.Register", ex);
            }
        }
    }

    [HarmonyPatch(typeof(Unit), "OnDestroy")]
    public static class Unit_OnDestroy_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(Unit __instance)
        {
            if ((object)__instance == null) return;

            // Each cleanup step on its own: one failed registry must not leave the others with the dead unit
            Missile missile = __instance as Missile;
            if ((object)missile != null)
            {
                // Rounds that never passed Unit.DeregisterMissile (unguided, untargeted) are cleaned here too
                int id = missile.GetInstanceID();
                try { ARHSeeker_GetRadarReturn_Patch.ClearCoastTimer(id); } catch (Exception ex) { Fail("ArhCoast", ex); }
                try { SARHSeeker_GetTrackingStrength_Patch.ClearCoastTimer(id); } catch (Exception ex) { Fail("SarhCoast", ex); }
                try { ChaffSeduction.Clear(id); } catch (Exception ex) { Fail("Chaff", ex); }
                try { DecoyDispenserSetup.ForgetMissile(id); } catch (Exception ex) { Fail("DecoyMissile", ex); }
                try { DatalinkNetwork.DeregisterMissile(missile); } catch (Exception ex) { Fail("DatalinkMissile", ex); }
                try { TrackUncertaintyCalculator.UnregisterMissile(missile); } catch (Exception ex) { Fail("Uncertainty", ex); }
                try { AIMissileTactics.ForgetMissile(missile); } catch (Exception ex) { Fail("TacticsMissile", ex); }
                try { SeekerDispersionHelper.ForgetMissile(missile); } catch (Exception ex) { Fail("Dispersion", ex); }
                try { MissileThreatInference.Forget(missile); } catch (Exception ex) { Fail("ThreatInference", ex); }
                try { MissileSync.Forget(missile); } catch (Exception ex) { Fail("MissileSync", ex); }
            }
            else
            {
                int id = __instance.GetInstanceID();
                try { DatalinkNetwork.DeregisterUnit(__instance); } catch (Exception ex) { Fail("DatalinkUnit", ex); }
                try { RadarDecoySeduction.RemoveUnit(__instance); } catch (Exception ex) { Fail("DecoySeduction", ex); }
                try { DecoyDispenserSetup.ForgetAircraft(id); } catch (Exception ex) { Fail("DecoyAircraft", ex); }
                try { AIMissileTactics.ForgetAircraft(id); } catch (Exception ex) { Fail("TacticsAircraft", ex); }
            }

            try { RWRTriangulationProcessor.RemoveTrack(__instance); } catch (Exception ex) { Fail("Track", ex); }
            try { VanillaKnowledgeExpiry.RemoveUnit(__instance); } catch (Exception ex) { Fail("VanillaExpiry", ex); }
            try { ShipRadarReturn.Unregister(__instance); } catch (Exception ex) { Fail("Ship", ex); }
            try { StandoffJamming.OnUnitDestroyed(__instance); } catch (Exception ex) { Fail("Standoff", ex); }
            try { AryxWeaponsCompat.RemoveUnit(__instance); } catch (Exception ex) { Fail("Aryx", ex); }
        }

        private static void Fail(string step, Exception ex)
        {
            PatchErrorLog.Once("Unit_OnDestroy_Patch." + step, ex);
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
    /// Ships, ground vehicles and radars stay vanilla; F10 restores vanilla. A targeting pod on a hardpoint (Aryx ETS-15)
    /// keeps its own magnification when it is better than the aircraft optics.
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
                // A targeting pod of another mod (Aryx ETS-15) brings its own optics
                if (AryxWeaponsCompat.IsPodDetector(__instance)) magnification = Mathf.Max(magnification, __instance.GetVisualMagnification());
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

            return RWRTriangulationProcessor.TryPeekTrack(hq, unit, out track);
        }
    }

    [HarmonyPatch(typeof(Aircraft), "Awake")]
    public static class Aircraft_Awake_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(Aircraft __instance)
        {
            if (__instance == null) return;

            try
            {
                PhysRWRReceiver rwr = __instance.gameObject.GetComponent<PhysRWRReceiver>();
                if (rwr == null)
                {
                    __instance.gameObject.AddComponent<PhysRWRReceiver>();
                }
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("Aircraft_Awake_Patch.Rwr", ex);
            }

            try
            {
                ChaffDispenserSetup.Attach(__instance);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("Aircraft_Awake_Patch.Chaff", ex);
            }

            try
            {
                DecoyDispenserSetup.Attach(__instance);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("Aircraft_Awake_Patch.Decoy", ex);
            }

            // Default flare count on every computer; the player choice comes later (CountermeasureSync)
            if (!CnoEvilConfig.IsModActive) return;
            try
            {
                CountermeasureLoadout.Apply(__instance, CountermeasureLoadout.Default());
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("Aircraft_Awake_Patch.Countermeasures", ex);
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

            try
            {
                PhysRadarEmitter emitter = __instance.gameObject.GetComponent<PhysRadarEmitter>();
                if (emitter == null)
                {
                    emitter = __instance.gameObject.AddComponent<PhysRadarEmitter>();
                }
                emitter.InitializeSpecs();
                DatalinkNetwork.RegisterRadar(__instance);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("Radar_Lifecycle_Patches.Awake", ex);
            }
        }

        [HarmonyPatch("AttachToUnit")]
        [HarmonyPostfix]
        public static void AttachToUnit_Postfix(Radar __instance, Unit unit)
        {
            if (__instance == null) return;

            try
            {
                // Awake runs before the radar is attached, so the specs were derived without the owning unit
                PhysRadarEmitter emitter = PhysRadarEmitter.Get(__instance);
                if (emitter != null && unit != null)
                {
                    emitter.AttachedUnit = unit;
                    emitter.InitializeSpecs();
                }
                DatalinkNetwork.RegisterRadar(__instance);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("Radar_Lifecycle_Patches.AttachToUnit", ex);
            }
        }

        [HarmonyPatch("OnDestroy")]
        [HarmonyPostfix]
        public static void OnDestroy_Postfix(Radar __instance)
        {
            if ((object)__instance == null) return;
            try
            {
                DatalinkNetwork.DeregisterRadar(__instance);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("Radar_Lifecycle_Patches.OnDestroy", ex);
            }
        }
    }

    /// <summary>
    /// Vanilla RadarLocator registers the emitter of every RWR ping in the vanilla trackingDatabase
    /// (CmdUpdateTrackingInfo), which gives the HUD, the AI and the vanilla seekers the true position from one
    /// bearing. The vanilla method runs only while the faction knows the emitter well enough for an HMD marker: a
    /// recent precise contact, located, or Q at or above HMDMarkerQualityThreshold
    /// (UnitMapIcon_UpdateIcon_Patch.IsHmdVisible). Vanilla then makes its own entry, its recon reward, its HMD marker
    /// and its map icon (the map icon waits for LocatedQualityThreshold). Without a recent precise contact, vanilla
    /// may make the entry, but refresh it only once after each passive fix that found the emitter again after a gap
    /// (TriangulationTrack.PassiveReacquireTime, ADR-46): each refresh writes the true position and
    /// TrackingInfo.GetPosition returns the live position for 4 s after it. Otherwise the entry ages the vanilla way
    /// at the locate point. Vanilla runs this on the server only. The mod does not write to the vanilla database
    /// (ADR-14). Vanilla behaviour returns with F10.
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

                Unit emitter = source.emitter;
                if (emitter == null || ___aircraft == null) return true;
                if (emitter is Missile) return true; // Missile seeker emissions stay vanilla (MWS, onlySurface filter)

                FactionHQ hq = ___aircraft.NetworkHQ;
                if (hq == null) return true; // Vanilla returns early itself
                if (DatalinkNetwork.GetUnitFactionHQ(emitter) == null) return false; // Neutral radar: no vanilla entry
                if (!ThreatClassification.IsTrackedThreat(emitter, hq)) return true; // No mod track for this type

                // Read-only lookup
                TriangulationTrack track;
                if (!RWRTriangulationProcessor.TryPeekTrack(hq, emitter, out track) || track == null) return false;

                if (track.HasRecentPreciseContact) return true; // Real contact: the vanilla live position is correct
                if (!UnitMapIcon_UpdateIcon_Patch.IsHmdVisible(track)) return false;

                // Known by passive means only: vanilla makes the entry, marker, icon and reward once. A refresh writes
                // the true position again and opens the 4 s live window of TrackingInfo.GetPosition again, so it is
                // allowed only once after a passive fix that found the emitter again after a gap (ADR-46).
                TrackingInfo info = hq.GetTrackingData(emitter.persistentID);
                if (info == null) return true;
                return track.PassiveReacquireTime != 0.0f && track.PassiveReacquireTime > info.lastSpottedTime;
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("RadarLocator_OnRadarWarning_Patch", ex);
                return true;
            }
        }
    }
}
