using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using CnoEvil.Components;
using CnoEvil.Core;
using CnoEvil.Config;

namespace CnoEvil.Patches
{
    /// <summary>
    /// Marks the JammingPod whose vanilla tick runs now, so Unit_Jam_Patch can register its beam and spread the jam over the cone.
    /// </summary>
    [HarmonyPatch(typeof(JammingPod), "FixedUpdate")]
    public static class JammingPod_FixedUpdate_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(JammingPod __instance)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive) return;
                JammingConeProcessor.BeginPodTick(__instance);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("JammingPod_FixedUpdate_Patch.Prefix", ex);
            }
        }

        [HarmonyPostfix]
        public static void Postfix()
        {
            JammingConeProcessor.EndPodTick();
        }

        /// <summary>
        /// The vanilla tick jams only a target that FactionHQ.IsTargetBeingTracked (vanilla sighting in the last 4 s).
        /// StandoffJamming.PodCanTrackTarget also accepts the faction track, so the pod stays on a passively located
        /// emitter. Same stack shape (FactionHQ, Unit -> bool).
        /// </summary>
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return PerceivedMissileTarget.Replace(instructions,
                AccessTools.Method(typeof(FactionHQ), "IsTargetBeingTracked", new Type[] { typeof(Unit) }),
                AccessTools.Method(typeof(StandoffJamming), "PodCanTrackTarget"),
                "JammingPod_FixedUpdate_Patch: IsTargetBeingTracked call not found, vanilla pod gate kept");
        }
    }

    /// <summary>
    /// Map: the jamming cone of the pod (JammingConeMapVisualizer) replaces the vanilla straight line from the jammer to
    /// the jammed radar. Vanilla only toggles the line image, so the line object is set inactive once. The jammed label stays.
    /// </summary>
    [HarmonyPatch(typeof(JammedMarker), "Update")]
    public static class JammedMarker_Update_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(GameObject ___vectorLine)
        {
            try
            {
                if (___vectorLine == null) return;
                bool show = !CnoEvilConfig.IsModActive;
                if (___vectorLine.activeSelf != show) ___vectorLine.SetActive(show);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("JammedMarker_Update_Patch", ex);
            }
        }
    }

    /// <summary>
    /// After the vanilla jam of the pod target, jams the hostile units inside the pod beam cone. Each jammed aircraft
    /// (target or cone unit) gets a measured bearing to the jammer for the passive tracking. Unit.Jam runs on the server only.
    /// </summary>
    [HarmonyPatch(typeof(Unit), "Jam")]
    public static class Unit_Jam_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(Unit __instance, Unit.JamEventArgs args)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive) return;
                JammingConeProcessor.OnUnitJammed(__instance, args);

                Aircraft jammedAircraft = __instance as Aircraft;
                PhysRWRReceiver rwr = (jammedAircraft != null) ? PhysRWRReceiver.Get(jammedAircraft) : null;
                if (rwr != null && args.jamAmount > 0.0f) rwr.RegisterJamStrobe(args.jammingUnit, args.jamAmount);
            }
            catch (Exception ex)
            {
                JammingConeProcessor.EndPodTick();
                PatchErrorLog.Once("Unit_Jam_Patch", ex);
            }
        }
    }

    /// <summary>
    /// Self-protection jammer transmit budget (EcmChargeBudget): after the vanilla Fire (power draw, intensity, ECM delta)
    /// the jamming is recorded for the shared power supply drain (PowerSupply_FixedUpdate_Patch), and the intensity becomes
    /// jammingIntensity x charge fraction; the ECM of the aircraft is corrected by the difference. Vanilla Update
    /// removes jamIntensityPrev when the jammer stops, so both fields hold the new value. Postfix only: the ImprovedAI mod
    /// prefixes Fire.
    /// </summary>
    [HarmonyPatch(typeof(RadarJammer), "Fire")]
    public static class RadarJammer_Fire_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(RadarJammer __instance, PowerSupply ___powerSupply, float ___powerUsage, float ___jammingIntensity,
                                   ref float ___jamIntensityCurrent, ref float ___jamIntensityPrev)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive || !EcmChargeBudget.Enabled) return;
                Aircraft aircraft = __instance.aircraft;
                if (aircraft == null || ___powerSupply == null) return;

                float current = ___jamIntensityCurrent;
                float vanillaDrawn = (___jammingIntensity > 0.0f) ? current / ___jammingIntensity * ___powerUsage : 0.0f;
                float fraction = EcmChargeBudget.OnFire(___powerSupply, vanillaDrawn);

                float scaled = ___jammingIntensity * fraction;
                aircraft.AddECMIntensity(scaled - current);
                ___jamIntensityCurrent = scaled;
                ___jamIntensityPrev = scaled;
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("RadarJammer_Fire_Patch", ex);
            }
        }
    }

    /// <summary>
    /// Jammer budget on the power supply (EcmChargeBudget): the engine refill of a supply that feeds a jammer is limited to
    /// maxCharge / EcmRechargeTime_s plus the draw of the other users, and the jammer drain since the last tick is set to
    /// maxCharge / EcmJamDuration_s. The prefix saves powerDrawn before vanilla resets it.
    /// </summary>
    [HarmonyPatch(typeof(PowerSupply), "FixedUpdate")]
    public static class PowerSupply_FixedUpdate_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(PowerSupply __instance, float ___charge, float ___powerDrawn)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive || !EcmChargeBudget.Enabled) return;
                EcmChargeBudget.BeforeSupplyTick(__instance, ___charge, ___powerDrawn);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("PowerSupply_FixedUpdate_Patch.Prefix", ex);
            }
        }

        [HarmonyPostfix]
        public static void Postfix(PowerSupply __instance, Aircraft ___aircraft, ref float ___charge, float ___maxCharge)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive || !EcmChargeBudget.Enabled) return;
                EcmChargeBudget.AfterSupplyTick(__instance, ___aircraft, ref ___charge, ___maxCharge);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("PowerSupply_FixedUpdate_Patch.Postfix", ex);
            }
        }
    }

    [HarmonyPatch(typeof(RadarJammer), "OnDestroy")]
    public static class RadarJammer_OnDestroy_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(PowerSupply ___powerSupply)
        {
            EcmChargeBudget.Remove(___powerSupply);
        }
    }

    /// <summary>
    /// HMD jamming marker. Vanilla puts it at the true jammer position. With the mod it is hidden: HMDJamCue shows
    /// the noise sector of the jammer instead. Vanilla keeps the lifetime of the marker.
    /// </summary>
    [HarmonyPatch]
    public static class RadarWarning_JammingIcon_Position_Patch
    {
        private static Func<object, Image> s_getImage;
        private static Func<object, Text> s_getText;

        public static MethodBase TargetMethod()
        {
            Type iconType = AccessTools.Inner(typeof(RadarWarning), "JammingIcon");
            if (iconType == null) return null;
            s_getImage = FastReflection.CreateFieldGetter<Image>(iconType, "image");
            s_getText = FastReflection.CreateFieldGetter<Text>(iconType, "text");
            if (s_getImage == null || s_getText == null) return null;
            return AccessTools.Method(iconType, "Position");
        }

        [HarmonyPostfix]
        public static void Postfix(object __instance, bool __result)
        {
            try
            {
                if (!__result || !CnoEvilConfig.IsModActive || !CnoEvilConfig.EnableHMDJamCue.Value) return;

                // Vanilla Position sets enabled every frame; the postfix runs before the frame renders
                Image image = s_getImage(__instance);
                if (image != null) image.enabled = false;
                Text text = s_getText(__instance);
                if (text != null) text.enabled = false;
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("RadarWarning_JammingIcon_Position_Patch", ex);
            }
        }
    }
}
