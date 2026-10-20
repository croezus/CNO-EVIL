using BepInEx.Configuration;

namespace CnoEvil.Config
{
    public static partial class CnoEvilConfig
    {
        // 05. Missile Guidance
        public static ConfigEntry<bool> EnableMissileMemoryCoasting;
        public static ConfigEntry<float> MissileMemoryCoastSeconds;
        public static ConfigEntry<bool> EnableSilentARHMidcourse;
        public static ConfigEntry<float> ARHSeekerActivationRangeMeters;
        public static ConfigEntry<float> ARMSeekerActivationRangeMeters;
        public static ConfigEntry<float> OpticalSeekerActivationRangeMeters;
        public static ConfigEntry<float> OpticalSearchRadiusMeters;
        public static ConfigEntry<float> CruiseTerminalRangeMeters;
        public static ConfigEntry<float> CruiseTerminalSearchRadiusMeters;
        public static ConfigEntry<bool> MissileEnforceSelfDestructWatchdog;
        public static ConfigEntry<float> MissileMaxBattlefieldRadiusKm;
        public static ConfigEntry<bool> MunitionAuditLog;

        // 10. AI Doctrine
        public static ConfigEntry<bool> AIEvaluateMissileLaunchDoctrine;
        public static ConfigEntry<float> AIMinLaunchQuality;
        public static ConfigEntry<float> AIMinLaunchHitProbability;

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
            ARHSeekerActivationRangeMeters = Bind(c, SectionGuidance, "ARHSeekerActivationRangeMeters", 0.0f,
                "Distance (m) to the datum at which an active radar seeker (AAM-29, AAM-36, NL-98) switches on. 0 = vanilla terminalRange of the missile.");
            ARMSeekerActivationRangeMeters = Bind(c, SectionGuidance, "ARMSeekerActivationRangeMeters", 0.0f,
                "Distance (m) to the datum at which an anti-radiation seeker (ARAD-116, ARAD-45) starts to listen for emitters. 0 = vanilla: it listens for the whole flight.");
            OpticalSeekerActivationRangeMeters = Bind(c, SectionGuidance, "OpticalSeekerActivationRangeMeters", 0.0f,
                "Distance (m) to the target inside which an optical seeker (AGMs, guided bombs, guided shells) can lock. 0 = vanilla: line of sight only.");
            OpticalSearchRadiusMeters = Bind(c, SectionGuidance, "OpticalSearchRadiusMeters", 0.0f,
                "Radius (m) around the datum in which an optical seeker finds its target. 0 = vanilla searchRadius of the munition.");
            CruiseTerminalRangeMeters = Bind(c, SectionGuidance, "CruiseTerminalRangeMeters", 0.0f,
                "Distance (m) to the datum at which a cruise missile (AGM-99, AShM-300, ALM-C450, ALND-4) starts its terminal attack. 0 = vanilla terminalRange.");
            CruiseTerminalSearchRadiusMeters = Bind(c, SectionGuidance, "CruiseTerminalSearchRadiusMeters", 0.0f,
                "Radius (m) around the datum in which a cruise missile finds its target. 0 = vanilla terminalSearchRadius.");
            MissileEnforceSelfDestructWatchdog = Bind(c, SectionGuidance, "MissileEnforceSelfDestructWatchdog", true,
                "Detonate missiles that left the battlefield (MissileMaxBattlefieldRadiusKm, below -150 m, above 60 km). All other fuzing is vanilla.");
            MissileMaxBattlefieldRadiusKm = Bind(c, SectionGuidance, "MissileMaxBattlefieldRadiusKm", 120.0f,
                "Missiles farther than this from the map center (km) self-destruct.");
            MunitionAuditLog = Bind(c, SectionGuidance, "MunitionAuditLog", false,
                "Write one log line for each launch (weapon, target, Q, CEP, miss vector) and each detonation (miss distance, cause) to LogOutput.log.");
        }

        private static void BindAIDoctrine(ConfigFile c)
        {
            AIEvaluateMissileLaunchDoctrine = Bind(c, SectionAI, "AIEvaluateMissileLaunchDoctrine", true,
                "AI fires missiles and guided artillery at enemy tracked threats (aircraft, ships, air defense, radars) only with a faction track of Q >= AIMinLaunchQuality and, for weapons that fly to the datum, a hit probability >= AIMinLaunchHitProbability. Other targets use vanilla rules.");
            AIMinLaunchQuality = Bind(c, SectionAI, "AIMinLaunchQuality", 0.5f,
                "Lowest track quality Q (0 to 1) at which AI fires a guided weapon at a tracked threat. No track: no fire.");
            AIMinLaunchHitProbability = Bind(c, SectionAI, "AIMinLaunchHitProbability", 0.5f,
                "Lowest probability (0 to 1) that the target is inside the seeker basket or the warhead danger radius of the datum (Gaussian miss with the CEP of the track). Applies to weapons that fly to the datum (ARH, ARM on a silent radar, optical, cruise, bombs, artillery).");
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
