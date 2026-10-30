using BepInEx.Configuration;

namespace CnoEvil.Config
{
    public static partial class CnoEvilConfig
    {
        // 05. Missile Guidance
        public static ConfigEntry<bool> EnableMissileMemoryCoasting;
        public static ConfigEntry<float> MissileMemoryCoast_s;
        public static ConfigEntry<float> ARHSeekerActivationRange_m;
        public static ConfigEntry<float> ARMSeekerActivationRange_m;
        public static ConfigEntry<float> OpticalSeekerActivationRange_m;
        public static ConfigEntry<float> OpticalSearchRadius_m;
        public static ConfigEntry<float> CruiseTerminalRange_m;
        public static ConfigEntry<float> CruiseTerminalSearchRadius_m;
        public static ConfigEntry<bool> MissileEnforceSelfDestructWatchdog;
        public static ConfigEntry<float> MissileMaxBattlefieldRadius_km;

        // 10. AI Doctrine
        public static ConfigEntry<bool> AIEvaluateMissileLaunchDoctrine;
        public static ConfigEntry<float> AIMinLaunchQuality;
        public static ConfigEntry<float> AIMinLaunchHitProbability;
        public static ConfigEntry<bool> AIDragEnabled;
        public static ConfigEntry<float> AIDragMinImpactTime_s;
        public static ConfigEntry<bool> AITurnAwayFromDecoy;
        public static ConfigEntry<float> AITurnAwayWindow_s;
        public static ConfigEntry<bool> AICrankEnabled;
        public static ConfigEntry<float> AICrankAngle_deg;

        // 11. Datalink
        public static ConfigEntry<bool> EnableAlliedDatalink;
        public static ConfigEntry<float> DatalinkScanInterval_s;
        public static ConfigEntry<float> BasicDatalinkRelayInterval_s;
        public static ConfigEntry<float> MissilePictureCoast_s;
        public static ConfigEntry<float> MissileWarningHold_s;

        private const string SectionGuidance = "05. Missile Guidance";
        private const string SectionAI = "10. AI Doctrine";
        private const string SectionDatalink = "11. Datalink";

        private static void BindMissileGuidance(ConfigFile c)
        {
            EnableMissileMemoryCoasting = Bind(c, SectionGuidance, "EnableMissileMemoryCoasting", true,
                "A radar missile whose target enters the clutter notch keeps its lock for up to MissileMemoryCoast_s while the target " +
                "stays inside the seeker gate around its dead-reckoned position (a target that turns or jinks walks out of it). " +
                "One coast per notch entry: a target that holds the notch longer is lost. FALSE: the lock drops at once.");
            MissileMemoryCoast_s = Bind(c, SectionGuidance, "MissileMemoryCoast_s", 2.5f,
                "Longest memory coast (s) after the target enters the notch.");
            ARHSeekerActivationRange_m = Bind(c, SectionGuidance, "ARHSeekerActivationRange_m", 0.0f,
                "Distance (m) to the datum at which an active radar seeker (AAM-29, AAM-36, NL-98) switches on. Before it the missile " +
                "flies on datalink with the seeker silent; from it on the target's RWR hears the seeker. 0 = vanilla terminalRange of the missile.");
            ARMSeekerActivationRange_m = Bind(c, SectionGuidance, "ARMSeekerActivationRange_m", 0.0f,
                "Distance (m) at which an anti-radiation seeker (ARAD-116, ARAD-45) starts to listen for emitters: to the datum, or to " +
                "the radar designated at launch, whichever is closer. 0 = vanilla: it listens for the whole flight.");
            OpticalSeekerActivationRange_m = Bind(c, SectionGuidance, "OpticalSeekerActivationRange_m", 0.0f,
                "Distance (m) to the target inside which an optical seeker (AGMs, guided bombs, guided shells) can lock. 0 = vanilla: line of sight only.");
            OpticalSearchRadius_m = Bind(c, SectionGuidance, "OpticalSearchRadius_m", 0.0f,
                "Radius (m) around the datum in which an optical seeker finds its target. 0 = vanilla searchRadius of the munition.");
            CruiseTerminalRange_m = Bind(c, SectionGuidance, "CruiseTerminalRange_m", 0.0f,
                "Distance (m) to the datum at which a cruise missile (AGM-99, AShM-300, ALM-C450, ALND-4) starts its terminal attack. 0 = vanilla terminalRange.");
            CruiseTerminalSearchRadius_m = Bind(c, SectionGuidance, "CruiseTerminalSearchRadius_m", 0.0f,
                "Radius (m) around the datum in which a cruise missile finds its target. 0 = vanilla terminalSearchRadius.");
            MissileEnforceSelfDestructWatchdog = Bind(c, SectionGuidance, "MissileEnforceSelfDestructWatchdog", true,
                "Detonate armed missiles that left the battlefield: farther than MissileMaxBattlefieldRadius_km from the map center, " +
                "below -150 m or above 60 km. Missiles younger than 10 s and ballistic missiles are exempt. All other fuzing is vanilla.");
            MissileMaxBattlefieldRadius_km = Bind(c, SectionGuidance, "MissileMaxBattlefieldRadius_km", 120.0f,
                "Missiles farther than this from the map center (km) self-destruct. 0 = no radius limit (the altitude limits stay).");
        }

        private static void BindAIDoctrine(ConfigFile c)
        {
            AIEvaluateMissileLaunchDoctrine = Bind(c, SectionAI, "AIEvaluateMissileLaunchDoctrine", true,
                "AI fires missiles and guided artillery at enemy tracked threats (aircraft, ships, air defense, radars) only with a faction " +
                "track of Q >= AIMinLaunchQuality and, for weapons that fly to the datum, a hit probability >= AIMinLaunchHitProbability. " +
                "A SARH missile is held while its illuminating radar is jammed by a jamming pod. Other targets use vanilla rules. " +
                "FALSE turns off only these launch rules: the AI still needs the faction estimate inside the seeker basket of its weapon " +
                "and leads with the faction-known target velocity.");
            AIMinLaunchQuality = Bind(c, SectionAI, "AIMinLaunchQuality", 0.5f,
                "Lowest track quality Q (0 to 1) at which AI fires a guided weapon at a tracked threat. No track: no fire.");
            AIMinLaunchHitProbability = Bind(c, SectionAI, "AIMinLaunchHitProbability", 0.5f,
                "Lowest probability (0 to 1) that the target is inside the seeker basket or the warhead danger radius of the datum (Gaussian miss " +
                "with the CEP of the track). Applies to weapons that fly to the datum (ARH, ARM on a silent radar, optical, cruise, bombs, artillery).");
            AIDragEnabled = Bind(c, SectionAI, "AIDragEnabled", false,
                "AI aircraft turn their tail to a radar missile while its time to impact is more than AIDragMinImpactTime_s. The missile " +
                "uses its energy in the chase. Nearer, the AI beams the missile (vanilla). FALSE: vanilla beam at all ranges.");
            AIDragMinImpactTime_s = Bind(c, SectionAI, "AIDragMinImpactTime_s", 20.0f,
                "Time to impact (s) above which the AI drags a radar missile. The AI beams it again below this time minus 4 s.");
            AITurnAwayFromDecoy = Bind(c, SectionAI, "AITurnAwayFromDecoy", true,
                "After the AI ejects chaff or a DRFM decoy, it beams the missile on the side away from its cloud, so the seeker cannot " +
                "find it again after the cloud. FALSE: vanilla side (the side of the nose).");
            AITurnAwayWindow_s = Bind(c, SectionAI, "AITurnAwayWindow_s", 6.0f,
                "Seconds after an ejection in which the AI turns away from that chaff cloud or decoy.");
            AICrankEnabled = Bind(c, SectionAI, "AICrankEnabled", true,
                "While its own ARH or SARH missile flies at its target, the AI aircraft turns AICrankAngle_deg off the target. This " +
                "decreases the closure speed and the range at which it meets enemy missiles. ARH: until the seeker locks; SARH: for " +
                "the full flight (the radar continues to light the target). FALSE: vanilla, the AI flies at the target.");
            AICrankAngle_deg = Bind(c, SectionAI, "AICrankAngle_deg", 50.0f,
                "Horizontal angle (deg) between the AI aircraft course and the target during the crank.");
        }

        private static void BindDatalink(ConfigFile c)
        {
            EnableAlliedDatalink = Bind(c, SectionDatalink, "EnableAlliedDatalink", true,
                "Friendly radars, aircraft and spotters share tracks, bearings and missile warnings over the faction datalink. " +
                "FALSE also stops the visual scan of strategic buildings (StrategicBuildingVisualRange_m).");
            DatalinkScanInterval_s = Bind(c, SectionDatalink, "DatalinkScanInterval_s", 0.35f,
                "Seconds (minimum 0.05) between datalink network updates (visual recon scan, track sharing, threat relays).");
            BasicDatalinkRelayInterval_s = Bind(c, SectionDatalink, "BasicDatalinkRelayInterval_s", 2.0f,
                "Seconds between threat relays to human-flown aircraft with a Basic datalink (Advanced datalinks get every update). " +
                "A relayed threat stays shown for 3.5 s or for this interval + 1 s, whichever is longer.");
            MissilePictureCoast_s = Bind(c, SectionDatalink, "MissilePictureCoast_s", 3.0f,
                "Seconds a faction keeps a hostile missile after its radars lose it. In this time the missile moves on its last " +
                "known velocity (dead reckoning) for the missile warning.");
            MissileWarningHold_s = Bind(c, SectionDatalink, "MissileWarningHold_s", 1.0f,
                "Seconds a known hostile missile must stay on a collision course with an aircraft before its missile warning starts " +
                "(alarm, threat list, AI evasion). An RWR contact on the missile seeker, or on its launcher guiding a missile at the " +
                "aircraft, starts the warning at once. The warning stops 2 s after the missile leaves the collision course.");
        }
    }
}
