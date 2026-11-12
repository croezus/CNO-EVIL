using HarmonyLib;

namespace CnoEvil.Core
{
    /// <summary>
    /// Run-time binding to Revert To Stone Age (RTSA). The mod has no reference to its assembly: the type is found by
    /// name, and nothing changes when RTSA is not installed. RTSA patches turrets, lasers, FireControl timing, missile
    /// launch / motor / steering, gun lead and the AI launch envelope of air defense; CNO-EVIL does not patch these
    /// methods or only adds independent gates, so both apply. One overlap: both mods have a prefix on
    /// Radar.CanSeeRadarReturn. The RTSA prefix runs first (HarmonyAfter on Radar_CanSeeRadarReturn_Patch); when it
    /// rejects the target (surface radar upper blind cone, extra radar horizon), CNO-EVIL keeps that result
    /// (__runOriginal). The RTSA clutterFactor scale has no effect while CNO-EVIL is active: the CNO-EVIL clutter and
    /// Doppler physics replace the vanilla clutter term (owner decision, ADR-41).
    /// </summary>
    public static class RtsaCompat
    {
        public const string PluginGuid = "com.xbarni.reverttostoneage";

        private static readonly bool s_installed = AccessTools.TypeByName("RevertToStoneAge.RadarClutterPatch") != null;

        public static bool Installed
        {
            get { return s_installed; }
        }
    }
}
