using System;
using System.Collections;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using CnoEvil.Components;
using CnoEvil.Core;
using CnoEvil.Config;

namespace CnoEvil.Patches
{
    /// <summary>
    /// Vanilla sorts the countermeasure stations by name after each registration. The chaff station goes back to the end,
    /// so the vanilla station indices (sent over the network) do not change. Attach patch: runs also while the mod is off.
    /// </summary>
    [HarmonyPatch(typeof(CountermeasureManager), "RegisterCountermeasure")]
    public static class CountermeasureManager_RegisterCountermeasure_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(CountermeasureManager __instance, Countermeasure countermeasure)
        {
            try
            {
                if (countermeasure == null) return;
                ChaffDispenserSetup.MoveChaffStationsLast(__instance);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("CountermeasureManager_RegisterCountermeasure_Patch", ex);
            }
        }
    }

    /// <summary>
    /// A new chaff cloud or active decoy (DecoyVisual on the prefab): the current config values (lifetime, drag, fall
    /// speed), the particle frame, and the cloud registry that the seekers read.
    /// </summary>
    [HarmonyPatch(typeof(RadarChaff), "LaunchChaff")]
    public static class RadarChaff_LaunchChaff_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(RadarChaff __instance, Aircraft aircraft)
        {
            try
            {
                if (__instance == null) return;
                DecoyVisual decoy = __instance.GetComponent<DecoyVisual>();
                if (decoy != null)
                {
                    DecoyDispenserSetup.OnLaunched(__instance, decoy);
                    DecoyDispenserSetup.AiDecoyLaunched(aircraft);
                }
                else
                {
                    ChaffDispenserSetup.OnLaunched(__instance);
                }
                ChaffCloudRegistry.Prune();
                ChaffCloudRegistry.Add(__instance, aircraft, decoy != null);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("RadarChaff_LaunchChaff_Patch", ex);
            }
        }
    }

    /// <summary>
    /// Vanilla chaff only adds to the seeker jam accumulator. With the mod the RCS model (ChaffSeduction in the seeker
    /// patches) replaces it.
    /// </summary>
    [HarmonyPatch(typeof(ARHSeeker), "ARHSeeker_OnChaff")]
    public static class ARHSeeker_OnChaff_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            return !CnoEvilConfig.IsModActive;
        }
    }

    [HarmonyPatch(typeof(SARHSeeker), "SARHSeeker_OnChaff")]
    public static class SARHSeeker_OnChaff_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            return !CnoEvilConfig.IsModActive;
        }
    }

    /// <summary>
    /// Player station cycle: the chaff and decoy stations are skipped while they cannot be used here
    /// (ChaffDispenserSetup.Usable, DecoyDispenserSetup.Usable). They are the last stations, so the cycle comes back to
    /// the first.
    /// </summary>
    [HarmonyPatch(typeof(CountermeasureManager), "NextCountermeasure")]
    public static class CountermeasureManager_NextCountermeasure_Patch
    {
        private static bool s_skipping;

        [HarmonyPostfix]
        public static void Postfix(CountermeasureManager __instance)
        {
            try
            {
                if (s_skipping || !Blocked(__instance, __instance.activeIndex)) return;
                s_skipping = true;
                // At most two mod stations to skip
                for (int i = 0; i < 2 && Blocked(__instance, __instance.activeIndex); i++)
                {
                    __instance.NextCountermeasure();
                }
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("CountermeasureManager_NextCountermeasure_Patch", ex);
            }
            finally
            {
                s_skipping = false;
            }
        }

        /// <summary>
        /// A chaff or decoy station that cannot be used here.
        /// </summary>
        public static bool Blocked(CountermeasureManager manager, int index)
        {
            if (!ChaffDispenserSetup.Usable && ChaffDispenserSetup.IsChaffIndex(manager, index)) return true;
            return !DecoyDispenserSetup.Usable && DecoyDispenserSetup.IsDecoyIndex(manager, index);
        }
    }

    /// <summary>
    /// Last guard on the network entry point: a chaff or decoy station index is not sent while it cannot be used here (a
    /// computer without the mod has no such station). The first station is selected instead.
    /// </summary>
    [HarmonyPatch(typeof(Aircraft), "Countermeasures")]
    public static class Aircraft_Countermeasures_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(Aircraft __instance, ref byte index)
        {
            try
            {
                if (index == 255) return;
                if (CountermeasureManager_NextCountermeasure_Patch.Blocked(__instance.countermeasureManager, index)) index = 0;
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("Aircraft_Countermeasures_Patch", ex);
            }
        }
    }

    /// <summary>
    /// AI countermeasure choice against a radar missile. Vanilla takes the first station for the seeker type, the jammer.
    /// With the mod: one active decoy per missile when the missile is not head-on (DecoyDispenserSetup.AiWantsDecoy; the
    /// station stays selected until the decoy is ejected); else the jammer when it has charge and the aircraft is inside
    /// the notch of the guiding radar (no home-on-jam there, and jamming in the notch breaks the Doppler track) or
    /// home-on-jam is off; else chaff while it has cartridges; else the vanilla jammer. Vanilla AI fires the station while
    /// it beams the missile in the last 8 s and repeats the choice each second.
    /// </summary>
    [HarmonyPatch(typeof(CountermeasureManager), "ChooseCountermeasure")]
    public static class CountermeasureManager_ChooseCountermeasure_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(CountermeasureManager __instance, Missile missileThreat, string __result, Aircraft ___aircraft)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive || ___aircraft == null) return;
                if (__result != "ARH" && __result != "SARH") return;
                bool chaffUsable = ChaffDispenserSetup.Usable;
                bool decoyUsable = DecoyDispenserSetup.Usable;
                if (!chaffUsable && !decoyUsable) return;

                IList stations = ChaffDispenserSetup.Stations(__instance);
                if (stations == null) return;
                int chaffIndex = -1;
                int decoyIndex = -1;
                int jammerIndex = -1;
                RadarJammer jammer = null;
                for (int i = 0; i < stations.Count && i < 255; i++)
                {
                    Countermeasure first = ChaffDispenserSetup.FirstCountermeasure(stations[i]);
                    if (first is ChaffEjector)
                    {
                        if (first.ammo <= 0) continue;
                        if (DecoyDispenserSetup.IsDecoyEjector(first))
                        {
                            if (decoyIndex < 0 && decoyUsable) decoyIndex = i;
                        }
                        else if (chaffIndex < 0 && chaffUsable)
                        {
                            chaffIndex = i;
                        }
                    }
                    else if (first is RadarJammer && jammerIndex < 0)
                    {
                        jammerIndex = i;
                        jammer = (RadarJammer)first;
                    }
                }

                if (decoyIndex >= 0 && DecoyDispenserSetup.AiWantsDecoy(___aircraft, missileThreat))
                {
                    DecoyDispenserSetup.AiDecoySelected(___aircraft, missileThreat);
                    if (__instance.activeIndex != decoyIndex) ___aircraft.Countermeasures(false, (byte)decoyIndex);
                    return;
                }

                bool jammerReady = jammer != null && !EcmChargeBudget.IsLow(jammer);
                bool useJammer = jammerReady &&
                                 (!CnoEvilConfig.HOJEnabled.Value || InsideSeekerNotch(___aircraft, missileThreat, __result == "SARH"));
                int index = useJammer ? jammerIndex : chaffIndex;
                if (index < 0 || __instance.activeIndex == index) return;
                ___aircraft.Countermeasures(false, (byte)index);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("CountermeasureManager_ChooseCountermeasure_Patch", ex);
            }
        }

        /// <summary>
        /// The aircraft is inside the clutter notch of the radar that guides this missile (the missile seeker for ARH, the
        /// launcher's radar for SARH), with the notch widened by full own jamming. There the jamming strobe gives no
        /// home-on-jam and breaks the Doppler track (DopplerClutterProcessor).
        /// </summary>
        private static bool InsideSeekerNotch(Aircraft aircraft, Missile missile, bool semiActive)
        {
            if (missile == null || aircraft.rb == null) return false;
            Vector3 radarPos = missile.transform.position;
            if (semiActive && missile.owner != null)
            {
                Radar radar = missile.owner.radar as Radar;
                Transform scan = (radar != null) ? radar.GetScanPoint() : null;
                radarPos = (scan != null) ? scan.position : missile.owner.transform.position;
            }
            DopplerResult doppler = DopplerClutterProcessor.ProcessDopplerAndClutter(radarPos, Vector3.zero,
                aircraft.transform.position, aircraft.rb.velocity, CnoEvilConfig.EffectiveNotchThreshold, false, 1.0f);
            return doppler.IsInsideClutterNotch;
        }
    }

    /// <summary>
    /// Cockpit countermeasure indicator: vanilla knows only FLARE and JAMMER (battery %). The chaff station shows CHAFF
    /// and its cartridges; the decoy station DECOY and its count, or XMIT and the battery seconds while a decoy of the
    /// aircraft transmits. The jammer keeps the vanilla battery %.
    /// </summary>
    [HarmonyPatch(typeof(CountermeasureIndicator), "Refresh")]
    public static class CountermeasureIndicator_Refresh_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(Aircraft ___aircraft, Image ___counterImage, Text ___counterName, Text ___counterAmmo)
        {
            try
            {
                if (___aircraft == null || ___counterName == null || ___counterAmmo == null || !PlayerSettings.hudWeapons) return;
                CountermeasureManager manager = ___aircraft.countermeasureManager;
                IList stations = ChaffDispenserSetup.Stations(manager);
                if (stations == null || manager.activeIndex >= stations.Count) return;
                Countermeasure active = ChaffDispenserSetup.FirstCountermeasure(stations[manager.activeIndex]);

                ChaffEjector chaff = active as ChaffEjector;
                if (chaff != null)
                {
                    bool decoy = DecoyDispenserSetup.IsDecoyEjector(chaff);
                    ___counterName.text = decoy ? DecoyDispenserSetup.HudLabel : ChaffDispenserSetup.HudLabel;
                    ___counterAmmo.text = chaff.ammo.ToString();
                    Color color = chaff.ammo > 0 ? Color.green : Color.grey;
                    // A decoy of this aircraft transmits: XMIT and the seconds of battery left
                    float transmit = decoy ? ChaffCloudRegistry.DecoyTransmitTimeLeft(___aircraft) : 0.0f;
                    if (transmit > 0.0f)
                    {
                        ___counterAmmo.text = string.Format("XMIT {0:F0}", Mathf.Ceil(transmit));
                        color = Color.yellow;
                    }
                    ___counterName.color = color;
                    ___counterAmmo.color = color;
                    if (___counterImage != null)
                    {
                        ___counterImage.enabled = true;
                        ___counterImage.color = color;
                        if (chaff.displayImage != null) ___counterImage.sprite = chaff.displayImage;
                    }
                }
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("CountermeasureIndicator_Refresh_Patch", ex);
            }
        }
    }
}
