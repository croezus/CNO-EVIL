using System;
using HarmonyLib;
using UnityEngine;
using CnoEvil.Config;

namespace CnoEvil.Patches
{
    /// <summary>
    /// Vanilla missile threat aids that reveal information the ownship sensors do not have:
    /// 1. The HUD notch flight director box ([ARH] / [SARH] with exact missile range) and the map notch evasion line
    ///    are suppressed when DisableVanillaNotchAids is set.
    /// 2. The map vector line from ownship to the incoming missile's true position is hidden while the mod is active;
    ///    radar-guided threats are shown by the RWR map strobes built from PhysRWRReceiver (DynamicMapPatches).
    /// Vanilla visuals return when the mod is toggled off (F10).
    /// </summary>
    public static class NotchAidsPatches
    {
        private static bool ShouldSuppress
        {
            get
            {
                return CnoEvilConfig.IsModActive &&
                       CnoEvilConfig.DisableVanillaNotchAids != null &&
                       CnoEvilConfig.DisableVanillaNotchAids.Value;
            }
        }

        private static void SetActive(GameObject go, bool active)
        {
            if (go != null && go.activeSelf != active) go.SetActive(active);
        }

        [HarmonyPatch(typeof(CombatHUD), "ShowNotchIndicator")]
        public static class CombatHUD_ShowNotchIndicator_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(GameObject __result)
            {
                if (ShouldSuppress) SetActive(__result, false);
            }
        }

        [HarmonyPatch(typeof(DynamicMap), "ShowNotchLine")]
        public static class DynamicMap_ShowNotchLine_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(GameObject __result)
            {
                if (ShouldSuppress) SetActive(__result, false);
            }
        }

        // Vanilla aligns both every frame while the threat item is active; re-show them after F10 / config toggle
        [HarmonyPatch(typeof(ThreatItem), "AlignNotchIndicator", new Type[] { typeof(Vector3) })]
        public static class ThreatItem_AlignNotchIndicator_Patch
        {
            [HarmonyPrefix]
            public static bool Prefix(GameObject ___notchIndicator)
            {
                bool suppress = ShouldSuppress;
                SetActive(___notchIndicator, !suppress);
                return !suppress;
            }
        }

        [HarmonyPatch(typeof(ThreatItem), "AlignNotchLine", new Type[] { typeof(Vector3) })]
        public static class ThreatItem_AlignNotchLine_Patch
        {
            [HarmonyPrefix]
            public static bool Prefix(GameObject ___notchLine)
            {
                bool suppress = ShouldSuppress;
                SetActive(___notchLine, !suppress);
                return !suppress;
            }
        }

        [HarmonyPatch(typeof(ThreatItem), "AlignVectorLine")]
        public static class ThreatItem_AlignVectorLine_Patch
        {
            [HarmonyPrefix]
            public static bool Prefix(GameObject ___vectorLine)
            {
                if (!CnoEvilConfig.IsModActive) return true; // Vanilla re-activates and aligns the line
                SetActive(___vectorLine, false);
                return false;
            }
        }

        // Vanilla OnEnable re-activates the notch line
        [HarmonyPatch(typeof(ThreatItem), "OnEnable")]
        public static class ThreatItem_OnEnable_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(GameObject ___notchLine, GameObject ___notchIndicator)
            {
                if (!ShouldSuppress) return;
                SetActive(___notchLine, false);
                SetActive(___notchIndicator, false);
            }
        }
    }
}
