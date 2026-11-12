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
        public static ConfigEntry<float> ARMSeekerMemoryAngleError_deg;
        public static ConfigEntry<float> IRSeekerGimbal_deg;
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
        public static ConfigEntry<bool> AIRadarShutdownEnabled;
        public static ConfigEntry<float> AIRadarShutdownRange_m;
        public static ConfigEntry<float> AIRadarShutdownMissDistance_m;
        public static ConfigEntry<float> AIRadarShutdownMargin_s;
        public static ConfigEntry<float> AIRadarShutdownMaxOff_s;
        public static ConfigEntry<bool> AIRadarInterceptEnabled;
        public static ConfigEntry<float> AIRadarInterceptReserveMobile_s;
        public static ConfigEntry<float> AIRadarInterceptReserveFixed_s;
        public static ConfigEntry<bool> AIRadarRelocateEnabled;
        public static ConfigEntry<float> AIRadarRelocateMinDistance_m;
        public static ConfigEntry<float> AIRadarRelocateMaxDistance_m;

        // 11. Datalink
        public static ConfigEntry<bool> EnableAlliedDatalink;
        public static ConfigEntry<float> DatalinkScanInterval_s;
        public static ConfigEntry<float> BasicDatalinkRelayInterval_s;
        public static ConfigEntry<float> MissilePictureCoast_s;
        public static ConfigEntry<float> MissileWarningHold_s;
        public static ConfigEntry<float> MissileWarningDatalinkTimeToGo_s;
        public static ConfigEntry<float> MissileWarningDatalinkRange_m;
        public static ConfigEntry<float> MissileWarningDatalinkIRRange_m;
        public static ConfigEntry<bool> MissileWarningDatalinkARH;
        public static ConfigEntry<bool> MissileWarningDatalinkSARH;
        public static ConfigEntry<bool> MissileWarningDatalinkIR;
        public static ConfigEntry<bool> MissileWarningDatalinkARM;
        public static ConfigEntry<bool> MissileWarningDatalinkOptical;
        public static ConfigEntry<bool> MissileWarningDatalinkLaser;

        private const string SectionGuidance = "05. Missile Guidance";
        private const string SectionAI = "10. AI Doctrine";
        private const string SectionDatalink = "11. Datalink";
        private const string SectionPlacement = "17. Mission Placement";

        // 17. Mission Placement
        public static ConfigEntry<bool> DynamicPlacementEnabled;
        public static ConfigEntry<float> DynamicPlacementCoreRadius_m;
        public static ConfigEntry<float> DynamicPlacementOutpostRadius_m;
        public static ConfigEntry<float> DynamicPlacementStationRadius_m;
        public static ConfigEntry<float> DynamicPlacementMinGap_m;
        public static ConfigEntry<float> DynamicPlacementMaxGap_m;
        public static ConfigEntry<int> DynamicPlacementSeed;

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
            ARMSeekerMemoryAngleError_deg = Bind(c, SectionGuidance, "ARMSeekerMemoryAngleError_deg", 1.5f,
                "Angle error (deg) of an anti-radiation seeker that loses its emitter (the radar stops, or terrain blocks it). The missile then " +
                "flies to the last seeker position plus the launch miss of its track (CEP from the launch Q), limited to the distance to the " +
                "radar at the loss multiplied by the tangent of this angle. An early shutdown gives the full launch miss, a late shutdown a small miss. " +
                "0 = vanilla: the missile flies to the last true radar position.");
            IRSeekerGimbal_deg = Bind(c, SectionGuidance, "IRSeekerGimbal_deg", 60.0f,
                "Gimbal limit (deg from the missile axis) of an infrared seeker. A locked target (or flare) farther off the axis is lost, " +
                "and the missile does not turn back to it. 0 = vanilla: no limit.");
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

        private static void BindMissionPlacement(ConfigFile c)
        {
            DynamicPlacementEnabled = Bind(c, SectionPlacement, "DynamicPlacementEnabled", true,
                "TRUE: at each mission start the host makes a layered air defense for each faction from the units of the mission, around its airbases and factories. " +
                "Point defense stands at the base, IR SAMs near it, radar SAMs farther out, and outposts stay outposts. Units with a role in the mission, " +
                "units with waypoints and units of the map do not move. FALSE: units stay at the position of the mission file.");
            DynamicPlacementCoreRadius_m = Bind(c, SectionPlacement, "DynamicPlacementCoreRadius_m", 4000.0f,
                "A complex this close in meters to the edge of an airbase or factory group is part of its core and gets a place in the echelons. A complex farther away is an outpost.");
            DynamicPlacementOutpostRadius_m = Bind(c, SectionPlacement, "DynamicPlacementOutpostRadius_m", 6000.0f,
                "Maximum distance in meters that an outpost moves. An outpost does not move into the core.");
            DynamicPlacementStationRadius_m = Bind(c, SectionPlacement, "DynamicPlacementStationRadius_m", 15000.0f,
                "Maximum distance in meters that a radar station building and its cover units move.");
            DynamicPlacementMinGap_m = Bind(c, SectionPlacement, "DynamicPlacementMinGap_m", 3.0f,
                "Minimum space in meters between the edges of two units. Each site gets a random value between this value and DynamicPlacementMaxGap_m.");
            DynamicPlacementMaxGap_m = Bind(c, SectionPlacement, "DynamicPlacementMaxGap_m", 15.0f,
                "Maximum value in meters of the random space between the edges of two units.");
            DynamicPlacementSeed = Bind(c, SectionPlacement, "DynamicPlacementSeed", 0,
                "Random seed of the placement. 0: a new seed at each mission start. Other value: the same sites at each start. " +
                "The BepInEx log shows the seed of each mission.");
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
            AIRadarShutdownEnabled = Bind(c, SectionAI, "AIRadarShutdownEnabled", true,
                "AI radars (air defense, ships, radar sites, AI aircraft) stop transmission when their faction knows an anti-radiation " +
                "missile that flies at them inside AIRadarShutdownRange_m. The radar transmits again after the estimated impact time plus " +
                "AIRadarShutdownMargin_s. While the radar is off it does not scan and does not guide SARH missiles. FALSE: radars stay on (vanilla).");
            AIRadarShutdownRange_m = Bind(c, SectionAI, "AIRadarShutdownRange_m", 15000.0f,
                "Distance (m) between the radar and a known anti-radiation missile at which the AI stops transmission. A missile farther away " +
                "does not stop the radar.");
            AIRadarShutdownMissDistance_m = Bind(c, SectionAI, "AIRadarShutdownMissDistance_m", 1000.0f,
                "The AI stops transmission only when the closest approach of the known missile path to the radar is this distance (m) or less.");
            AIRadarShutdownMargin_s = Bind(c, SectionAI, "AIRadarShutdownMargin_s", 5.0f,
                "Seconds after the estimated impact time of the missile before the radar transmits again.");
            AIRadarShutdownMaxOff_s = Bind(c, SectionAI, "AIRadarShutdownMaxOff_s", 45.0f,
                "Longest time (s) that the radar stays off without a break. After this time it transmits again for at least 10 s, " +
                "also when missiles stay near.");
            AIRadarInterceptEnabled = Bind(c, SectionAI, "AIRadarInterceptEnabled", true,
                "An AI ground or ship radar keeps transmission while the weapons that use this radar (its own and linked launchers " +
                "and guns) can still shoot down the anti-radiation missile in time, or while an interceptor flies at it. The radar stops " +
                "transmission only when the intercept is too late. AI aircraft stop at once. FALSE: the radar stops at once.");
            AIRadarInterceptReserveMobile_s = Bind(c, SectionAI, "AIRadarInterceptReserveMobile_s", 8.0f,
                "A mobile radar vehicle keeps transmission only while the expected intercept is this many seconds (s) or more before " +
                "the missile arrives. This time lets the vehicle pack up and drive away when the intercept fails.");
            AIRadarInterceptReserveFixed_s = Bind(c, SectionAI, "AIRadarInterceptReserveFixed_s", 3.0f,
                "The same time (s) for a radar that cannot move (sites, buildings, ships).");
            AIRadarRelocateEnabled = Bind(c, SectionAI, "AIRadarRelocateEnabled", true,
                "A mobile AI ground vehicle with a radar drives to a new site when it stops transmission against an anti-radiation " +
                "missile (AIRadarShutdownEnabled). The missile then flies to the old position. Ships, buildings and fixed sites do not move. " +
                "FALSE: the vehicle stays at its position.");
            AIRadarRelocateMinDistance_m = Bind(c, SectionAI, "AIRadarRelocateMinDistance_m", 150.0f,
                "Minimum distance (m) from the old position to the new site of a radar vehicle.");
            AIRadarRelocateMaxDistance_m = Bind(c, SectionAI, "AIRadarRelocateMaxDistance_m", 400.0f,
                "Maximum distance (m) from the old position to the new site of a radar vehicle. A radar that serves launchers stays " +
                "inside the link range of these launchers when it can.");
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
                "(alarm, threat list, AI evasion). An RWR contact on the missile seeker, or on the illuminator of a SARH missile that " +
                "homes on the aircraft, starts the warning at once. The warning stops 2 s after the missile leaves the collision course.");
            MissileWarningDatalinkTimeToGo_s = Bind(c, SectionDatalink, "MissileWarningDatalinkTimeToGo_s", 20.0f,
                "A missile without an own RWR or MAWS contact (known over the datalink or by the own radar) starts the missile warning only " +
                "when its time to the closest approach is this or less (s). Also, the missile must have terrain line of sight to the " +
                "aircraft. Lower values give less time to turn away from a long-range shot.");
            MissileWarningDatalinkRange_m = Bind(c, SectionDatalink, "MissileWarningDatalinkRange_m", 25000.0f,
                "Largest distance (m) between the aircraft and a missile without an own RWR or MAWS contact at which the missile warning starts.");
            MissileWarningDatalinkIRRange_m = Bind(c, SectionDatalink, "MissileWarningDatalinkIRRange_m", 5000.0f,
                "Largest distance (m) for an infrared missile without an own RWR or MAWS contact, in place of MissileWarningDatalinkRange_m.");
            MissileWarningDatalinkARH = Bind(c, SectionDatalink, "MissileWarningDatalinkARH", true,
                "Missile warning without an own RWR or MAWS contact (datalink or own radar) for active radar missiles (AAM-29, AAM-36, NL-98). " +
                "FALSE: a missile of this kind is warned only by the own RWR or MAWS.");
            MissileWarningDatalinkSARH = Bind(c, SectionDatalink, "MissileWarningDatalinkSARH", true,
                "Missile warning without an own RWR or MAWS contact (datalink or own radar) for semi-active radar missiles (RAM-45, StratoLance R9). " +
                "FALSE: a missile of this kind is warned only by the own RWR or MAWS.");
            MissileWarningDatalinkIR = Bind(c, SectionDatalink, "MissileWarningDatalinkIR", true,
                "Missile warning without an own RWR or MAWS contact (datalink or own radar) for infrared missiles (MMR-S3, IRM-S1, IRM-S2), up to MissileWarningDatalinkIRRange_m. " +
                "FALSE: a missile of this kind is warned only by the own RWR or MAWS.");
            MissileWarningDatalinkARM = Bind(c, SectionDatalink, "MissileWarningDatalinkARM", false,
                "Missile warning without an own RWR or MAWS contact (datalink or own radar) for anti-radiation missiles (ARAD-116, ARAD-45). " +
                "FALSE: a missile of this kind is warned only by the own RWR or MAWS.");
            MissileWarningDatalinkOptical = Bind(c, SectionDatalink, "MissileWarningDatalinkOptical", false,
                "Missile warning without an own RWR or MAWS contact (datalink or own radar) for optical missiles (AGM-48, AGM-68, ATP-1, Tusko-B, AT-145). " +
                "FALSE: a missile of this kind is warned only by the own RWR or MAWS.");
            MissileWarningDatalinkLaser = Bind(c, SectionDatalink, "MissileWarningDatalinkLaser", false,
                "Missile warning without an own RWR or MAWS contact (datalink or own radar) for laser missiles (AGR-18, AGR-24). " +
                "FALSE: a missile of this kind is warned only by the own RWR or MAWS.");
        }
    }
}
