using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
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
            var codes = new List<CodeInstruction>(instructions);
            MethodInfo original = AccessTools.Method(typeof(FactionHQ), "IsTargetBeingTracked", new Type[] { typeof(Unit) });
            MethodInfo replacement = AccessTools.Method(typeof(StandoffJamming), "PodCanTrackTarget");
            int replaced = 0;
            if (original != null && replacement != null)
            {
                for (int i = 0; i < codes.Count; i++)
                {
                    if (codes[i].operand != null && codes[i].operand.Equals(original))
                    {
                        // In place: labels and exception blocks stay on the instruction
                        codes[i].opcode = OpCodes.Call;
                        codes[i].operand = replacement;
                        replaced++;
                    }
                }
            }
            if (replaced == 0 && CnoEvilPlugin.Log != null)
            {
                CnoEvilPlugin.Log.LogWarning("JammingPod_FixedUpdate_Patch: IsTargetBeingTracked call not found, vanilla pod gate kept");
            }
            return codes;
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
    /// After the vanilla jam of the pod target, jams the hostile units inside the pod beam cone.
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
            }
            catch (Exception ex)
            {
                JammingConeProcessor.EndPodTick();
                PatchErrorLog.Once("Unit_Jam_Patch", ex);
            }
        }
    }
}
