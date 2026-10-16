using BepInEx.Configuration;

namespace CnoEvil.Config
{
    public static partial class CnoEvilConfig
    {
        // 05. Missile Guidance
        public static ConfigEntry<bool> EnableMissileMemoryCoasting;
        public static ConfigEntry<float> MissileMemoryCoastSeconds;
        public static ConfigEntry<bool> EnableSilentARHMidcourse;
        public static ConfigEntry<float> ARHTerminalActivationDistanceMeters;
        public static ConfigEntry<float> ARADTerminalActivationDistanceMeters;
        public static ConfigEntry<float> MissileTerminalActivationDistanceMeters;
        public static ConfigEntry<bool> SARHEnableLoftTerrainClearance;
        public static ConfigEntry<bool> MissileEnforceSelfDestructWatchdog;
        public static ConfigEntry<float> MissileMaxBattlefieldRadiusKm;

        // 10. AI Doctrine
        public static ConfigEntry<bool> AIEvaluateMissileLaunchDoctrine;
        public static ConfigEntry<float> AIArtilleryMinLaunchQuality;

        // 11. Datalink
        public static ConfigEntry<bool> EnableAlliedDatalink;
        public static ConfigEntry<float> DatalinkScanIntervalSeconds;
        public static ConfigEntry<float> BasicDatalinkRelayIntervalSeconds;

        private const string SectionGuidance = "05. Missile Guidance";
        private const string SectionAI = "10. AI Doctrine";
        private const string SectionDatalink = "11. Datalink";

        private static void BindMissileGuidance(ConfigFile c)
        {
            EnableMissileMemoryCoasting = Bind(c, SectionGuidance, "EnableMissileMemoryCoasting", true,
                "A radar missile that loses its target in the clutter notch keeps flying on the predicted track for MissileMemoryCoastSeconds instead of dropping lock at once.");
            MissileMemoryCoastSeconds = Bind(c, SectionGuidance, "MissileMemoryCoastSeconds", 2.5f,
                "Seconds of memory coasting after the target enters the notch.");
            EnableSilentARHMidcourse = Bind(c, SectionGuidance, "EnableSilentARHMidcourse", true,
                "ARH missiles fly midcourse on datalink with the seeker off; the target's RWR hears them only after seeker activation.");
            ARHTerminalActivationDistanceMeters = Bind(c, SectionGuidance, "ARHTerminalActivationDistanceMeters", 10000.0f,
                "Distance (m) to the aim point at which an active radar seeker switches on.");
            ARADTerminalActivationDistanceMeters = Bind(c, SectionGuidance, "ARADTerminalActivationDistanceMeters", 7000.0f,
                "Distance (m) to the aim point at which an anti-radiation seeker starts homing on emissions.");
            MissileTerminalActivationDistanceMeters = Bind(c, SectionGuidance, "MissileTerminalActivationDistanceMeters", 2800.0f,
                "Distance (m) to the aim point at which optical seekers (AGMs, guided bombs and shells) start searching.");
            SARHEnableLoftTerrainClearance = Bind(c, SectionGuidance, "SARHEnableLoftTerrainClearance", true,
                "Long-range SARH missiles loft over ridges instead of flying into terrain.");
            MissileEnforceSelfDestructWatchdog = Bind(c, SectionGuidance, "MissileEnforceSelfDestructWatchdog", true,
                "Detonate missiles that left the battlefield, overshot a ballistic datum, or are ARMs past their datum without a radar to home on.");
            MissileMaxBattlefieldRadiusKm = Bind(c, SectionGuidance, "MissileMaxBattlefieldRadiusKm", 120.0f,
                "Missiles farther than this from the map center (km) self-destruct.");
        }

        private static void BindAIDoctrine(ConfigFile c)
        {
            AIEvaluateMissileLaunchDoctrine = Bind(c, SectionAI, "AIEvaluateMissileLaunchDoctrine", true,
                "AI fires missiles at enemy tracked threats (aircraft, ships, air defense, radars) only when the launch dispersion (m) from its faction's track is not larger than what the weapon still kills: its seeker acquisition radius, its warhead danger radius, or 100 m. Other targets use vanilla rules.");
            AIArtilleryMinLaunchQuality = Bind(c, SectionAI, "AIArtilleryMinLaunchQuality", 0.9f,
                "Minimum track quality Q (0 to 1) of the target at which AI fires guided artillery (155 mm and guided naval shells, MLRS rockets). Targets without a faction track get no fire. A round fired at or above this Q gets no track dispersion. The fire limit applies only when AIEvaluateMissileLaunchDoctrine is on.");
        }

        private static void BindDatalink(ConfigFile c)
        {
            EnableAlliedDatalink = Bind(c, SectionDatalink, "EnableAlliedDatalink", true,
                "Friendly radars, aircraft and spotters share tracks, bearings and missile warnings over the faction datalink.");
            DatalinkScanIntervalSeconds = Bind(c, SectionDatalink, "DatalinkScanIntervalSeconds", 0.35f,
                "Seconds between datalink network updates (visual recon scan, track sharing).");
            BasicDatalinkRelayIntervalSeconds = Bind(c, SectionDatalink, "BasicDatalinkRelayIntervalSeconds", 2.0f,
                "Seconds between threat relays to aircraft with a Basic datalink (Advanced datalinks relay every update).");
        }
    }
}
