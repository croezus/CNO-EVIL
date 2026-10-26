using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine.UI;
using TMPro;
using CnoEvil.Core;
using CnoEvil.Config;
using CnoEvil.Components;

namespace CnoEvil.Patches
{
    /// <summary>
    /// Aircraft with a variable nozzle angle have a vanilla NozzleGauge on the cockpit screen (MFDAppManager) too.
    /// With PortalMFD its label shows through the portal display, so the gauge on the cockpit screen is hidden while
    /// the preset is active. The gauge in the HUD and the HMD stays.
    /// </summary>
    [HarmonyPatch(typeof(NozzleGauge))]
    public static class NozzleGaugePatches
    {
        private static readonly Func<NozzleGauge, TextMeshProUGUI> s_getLabel = FastReflection.CreateFieldGetter<NozzleGauge, TextMeshProUGUI>("label");
        private static readonly Func<NozzleGauge, Image[]> s_getFadeImages = FastReflection.CreateFieldGetter<NozzleGauge, Image[]>("fadeImages");

        // Instance id -> gauge is on the cockpit screen
        private static readonly Dictionary<int, bool> s_onCockpitScreen = new Dictionary<int, bool>();
        private static readonly HashSet<int> s_hidden = new HashSet<int>();

        [HarmonyPatch("Refresh")]
        [HarmonyPostfix]
        public static void Refresh_Postfix(NozzleGauge __instance)
        {
            if (!CnoEvilConfig.IsModActive || __instance == null) return;

            try
            {
                int id = __instance.GetInstanceID();
                bool onScreen;
                if (!s_onCockpitScreen.TryGetValue(id, out onScreen))
                {
                    onScreen = __instance.GetComponentInParent<MFDAppManager>(true) != null ||
                               __instance.GetComponentInParent<TacScreen>(true) != null;
                    s_onCockpitScreen[id] = onScreen;
                }
                if (!onScreen) return;

                bool hide = TacScreenPortalMFD.IsPresetActive();
                // Vanilla FadeIn / FadeOut turn the graphics on and off; after a preset change they get their state back
                if (!hide && !s_hidden.Remove(id)) return;
                if (hide) s_hidden.Add(id);

                TextMeshProUGUI label = s_getLabel != null ? s_getLabel(__instance) : null;
                if (label != null) label.enabled = !hide;
                Image[] images = s_getFadeImages != null ? s_getFadeImages(__instance) : null;
                if (images == null) return;
                for (int i = 0; i < images.Length; i++)
                {
                    if (images[i] != null) images[i].enabled = !hide;
                }
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("NozzleGauge.Refresh_Postfix", ex);
            }
        }
    }
}
