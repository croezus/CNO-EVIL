using System;
using HarmonyLib;
using CnoEvil.Core;

namespace CnoEvil.Patches
{
    /// <summary>
    /// Registers IR sources for the HMD thermal window. Register-only: runs also while the mod is off,
    /// so the registry is complete when the mod is switched on (F10).
    /// </summary>
    [HarmonyPatch(typeof(Unit), "AddIRSource")]
    public static class Unit_AddIRSource_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(Unit __instance, IRSource source)
        {
            try
            {
                IRSourceRegistry.Add(__instance, source);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("Unit.AddIRSource", ex);
            }
        }
    }

    [HarmonyPatch(typeof(Unit), "RemoveIRSource")]
    public static class Unit_RemoveIRSource_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(IRSource source)
        {
            try
            {
                IRSourceRegistry.Remove(source);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("Unit.RemoveIRSource", ex);
            }
        }
    }
}
