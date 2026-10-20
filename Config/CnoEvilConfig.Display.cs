using BepInEx.Configuration;
using UnityEngine;

namespace CnoEvil.Config
{
    public static partial class CnoEvilConfig
    {
        // 12. HUD & Markers
        public static ConfigEntry<bool> EnableHMDTrackQualityColoring;
        public static ConfigEntry<bool> EnableHMDTargetIdentDegradation;
        public static ConfigEntry<float> HMDIdentMidQuality;
        public static ConfigEntry<float> HMDIdentHighQuality;
        public static ConfigEntry<bool> EnableHUDNotchIndicator;
        public static ConfigEntry<bool> DisableVanillaNotchAids;
        public static ConfigEntry<bool> EnableHUDDetectionStatus;
        public static ConfigEntry<float> DetectionStatusIntervalSeconds;
        public static ConfigEntry<bool> EnableHUDOwnRCS;

        // 13. Tactical Map & Minimap
        public static ConfigEntry<bool> EnableRealisticRWRMapStrobes;
        public static ConfigEntry<bool> EnableMinimapRWRStrobes;
        public static ConfigEntry<bool> HideUntriangulatedRadarIcons;
        public static ConfigEntry<float> MapReconnaissanceQualityThreshold;
        public static ConfigEntry<float> MapRWRZoneNearRange;
        public static ConfigEntry<float> MapRWRZoneNearRadius;
        public static ConfigEntry<float> MapRWRZoneFarRange;
        public static ConfigEntry<float> MapRWRZoneFarRadius;
        public static ConfigEntry<float> RWRUncertaintyCircleScale;
        public static ConfigEntry<float> RWRSearchConeOpacity;
        public static ConfigEntry<float> RWRTrackConeOpacity;
        public static ConfigEntry<float> RWRMissileConeOpacity;
        public static ConfigEntry<float> RWRUncertaintyZoneOpacity;
        public static ConfigEntry<bool> EnableDatalinkVisualStrobes;
        public static ConfigEntry<float> RWRDatalinkConeOpacity;
        public static ConfigEntry<bool> ShowMissileAimpointOnMap;
        public static ConfigEntry<bool> ShowPlayerMissilesOnlyOnMap;
        public static ConfigEntry<float> MinimapZoom;
        public static ConfigEntry<KeyCode> MinimapZoomInKey;
        public static ConfigEntry<KeyCode> MinimapZoomOutKey;
        public static ConfigEntry<float> MinimapLookAhead;

        // 14. Cockpit TacScreen
        public enum TacScreenPreset
        {
            AdvancedCanvas,  // Custom vector canvas with leader lines & calibrated airspeed
            NativeEnhanced   // Vanilla MFD prefabs with CNO-EVIL threat coloring & physical filters
        }

        public enum TacScreenThreatDisplayMode
        {
            EmScanRing,  // EM scan ring only
            Cones,       // RWR strobes only
            Both,
            None
        }

        public static ConfigEntry<TacScreenPreset> TacScreenDisplayPreset;
        public static ConfigEntry<bool> EnableAdvancedCockpitRadarUI;
        public static ConfigEntry<TacScreenThreatDisplayMode> TacScreenThreatDisplay;
        public static ConfigEntry<bool> EnableRWRBearingCone;
        public static ConfigEntry<float> TacScreenEmScanRingRangeMeters;
        public static ConfigEntry<float> TacScreenEmScanRingSpacing;
        public static ConfigEntry<float> TacScreenEmScanRingBumpHeight;
        public static ConfigEntry<float> TacScreenEmScanRingNoise;
        public static ConfigEntry<float> TacScreenEmScanRingThickness;
        public static ConfigEntry<float> CockpitRWRSearchSectorAngle;
        public static ConfigEntry<float> CockpitRWRTrackSectorAngle;
        public static ConfigEntry<float> CockpitRWRMissileSectorAngle;
        public static ConfigEntry<float> CockpitRWRThreatDistanceRatio;
        public static ConfigEntry<string> RadarDisplayUnitSystem;
        public static ConfigEntry<float> RadarVelocityVectorScale;
        public static ConfigEntry<float> TargetCamMaxZoom;
        public static ConfigEntry<bool> EnableTargetCamIdentDegradation;

        // 15. Missile Trajectory HUD
        public enum MissileTrajectoryMode
        {
            TrailOnly,       // Broken polyline trail behind missile
            TrailAndLead,    // Broken trail behind missile + forward lead vector to target
            LeadOnly         // Straight vector from missile to target
        }

        public static ConfigEntry<bool> EnableMissileTrajectoryHUD;
        public static ConfigEntry<MissileTrajectoryMode> TrajectoryDisplayMode;
        public static ConfigEntry<float> MissileTrajectoryUpdateRate;
        public static ConfigEntry<int> MissileTrajectoryMaxSegments;
        public static ConfigEntry<bool> EnableMissileETADisplay;
        public static ConfigEntry<float> MissileTrajectoryLineWidth;

        // 16. HMD Thermal Window
        public static ConfigEntry<bool> EnableHMDThermalWindow;
        public static ConfigEntry<KeyCode> HMDThermalWindowKey;
        public static ConfigEntry<float> HMDThermalWindowSize;
        public static ConfigEntry<float> HMDThermalWindowAspect;
        public static ConfigEntry<float> HMDThermalWindowOffsetY;
        public static ConfigEntry<float> HMDThermalWindowOpacity;
        public static ConfigEntry<float> HMDThermalResolutionScale;
        public static ConfigEntry<float> HMDThermalLodBias;
        public static ConfigEntry<float> HMDThermalNearClip;
        public static ConfigEntry<float> HMDThermalMaxRange;
        public static ConfigEntry<float> HMDThermalContrast;
        public static ConfigEntry<float> HMDThermalHotSpotScale;

        private const string SectionHud = "12. HUD & Markers";
        private const string SectionMap = "13. Tactical Map & Minimap";
        private const string SectionTacScreen = "14. Cockpit TacScreen";
        private const string SectionTrajectory = "15. Missile Trajectory HUD";
        private const string SectionThermal = "16. HMD Thermal Window";

        private static void BindHudMarkers(ConfigFile c)
        {
            EnableHMDTrackQualityColoring = Bind(c, SectionHud, "EnableHMDTrackQualityColoring", true,
                "Tint the vanilla HUD markers of tracked threats by the faction's tracking quality Q: pale white (low) -> amber -> red (fire-control quality). Markers, positions and locking stay vanilla.");
            EnableHMDTargetIdentDegradation = Bind(c, SectionHud, "EnableHMDTargetIdentDegradation", true,
                "Selected target info on the HMD follows the faction's tracking quality Q. Low: domain only (AIR? / GRND? / SHIP?) and range to the nearest 10 km. " +
                "Mid: class (FTR, ATK, BMR, AEW, HELO / AAA, SAM, RDR / SHIP) and range to the nearest 2 km. High or radar / visual contact: vanilla designation " +
                "(ships: hull class), exact range and pilot name. Coarse ranges are always in km. The label also shows the Q grade " +
                "(Q>0, Q>25, Q>50, Q>75; none at full quality). Untracked units stay vanilla.");
            HMDIdentMidQuality = Bind(c, SectionHud, "HMDIdentMidQuality", 0.40f,
                "Tracking quality Q from which the HMD and the target camera show the target class and range to 2 km.");
            HMDIdentHighQuality = Bind(c, SectionHud, "HMDIdentHighQuality", 0.75f,
                "Tracking quality Q from which the HMD and the target camera show the full vanilla target info.");
            EnableHUDNotchIndicator = Bind(c, SectionHud, "EnableHUDNotchIndicator", true,
                "EW cues on the flight HUD compass: [ NOTCH ], [ ECM NOTCH ], [ HOJ RISK ], [ BURN-THROUGH ].");
            DisableVanillaNotchAids = Bind(c, SectionHud, "DisableVanillaNotchAids", true,
                "Hide the vanilla notch director box (with exact missile range) on the HUD and the notch line on the map.");
            EnableHUDDetectionStatus = Bind(c, SectionHud, "EnableHUDDetectionStatus", true,
                "Show one more line below the altitude on the HUD: the best track that a hostile faction has on your aircraft. " +
                "ID NO: no track. ID MEM: the enemy lost contact and keeps the track in memory. ID LOW / ID MED / ID HIGH: active track, " +
                "with the tiers of the enemy HMD (HMDIdentMidQuality, HMDIdentHighQuality; a radar contact gives HIGH). ID OPT: visual contact. " +
                "In multiplayer the host sends the value to clients that have the mod. A client on a host without the mod does not show the line.");
            DetectionStatusIntervalSeconds = Bind(c, SectionHud, "DetectionStatusIntervalSeconds", 1.0f,
                "Interval (s, minimum 0.25) at which the detection status line and the RCS line are calculated. The host also sends detection status changes to clients at this interval.");
            EnableHUDOwnRCS = Bind(c, SectionHud, "EnableHUDOwnRCS", true,
                "Show one more line below the detection status line on the HUD: the frontal RCS (m2) of your aircraft, " +
                "with external stores and open bay doors. Your computer calculates the value, also in multiplayer.");
        }

        private static void BindTacticalMap(ConfigFile c)
        {
            EnableRealisticRWRMapStrobes = Bind(c, SectionMap, "EnableRealisticRWRMapStrobes", true,
                "RWR emitter zones on the full map (M) from the ownship receiver, replacing vanilla's exact ping lines: " +
                "a translucent circle containing the emitter at a random point (never known to be its center) and a cone from the aircraft to it.");
            EnableMinimapRWRStrobes = Bind(c, SectionMap, "EnableMinimapRWRStrobes", true,
                "Same zones on the cockpit minimap.");
            HideUntriangulatedRadarIcons = Bind(c, SectionMap, "HideUntriangulatedRadarIcons", true,
                "Hide map and minimap icons of enemy tracked threats (aircraft, ships, air defense, radars) the faction has not located: bearing only, or Q below MapReconnaissanceQualityThreshold. FALSE: vanilla icons. HUD markers are always vanilla.");
            MapReconnaissanceQualityThreshold = Bind(c, SectionMap, "MapReconnaissanceQualityThreshold", 0.75f,
                "Tracking quality Q needed for a map icon when there is no radar or visual contact; the icon sits at the faction estimate.");
            MapRWRZoneNearRange = Bind(c, SectionMap, "MapRWRZoneNearRange", 3000.0f,
                "Emitter range (m) at which the map zone has its smallest radius (MapRWRZoneNearRadius).");
            MapRWRZoneNearRadius = Bind(c, SectionMap, "MapRWRZoneNearRadius", 1000.0f,
                "Map zone radius (m) at MapRWRZoneNearRange and closer.");
            MapRWRZoneFarRange = Bind(c, SectionMap, "MapRWRZoneFarRange", 80000.0f,
                "Emitter range (m) at which the map zone has its largest radius (MapRWRZoneFarRadius); linear in between.");
            MapRWRZoneFarRadius = Bind(c, SectionMap, "MapRWRZoneFarRadius", 10000.0f,
                "Map zone radius (m) at MapRWRZoneFarRange and beyond.");
            RWRUncertaintyCircleScale = Bind(c, SectionMap, "RWRUncertaintyCircleScale", 0.30f,
                "Scale of a track's uncertainty radius (weapon dispersion); does not affect the map zones.");
            RWRSearchConeOpacity = Bind(c, SectionMap, "RWRSearchConeOpacity", 0.10f,
                "Opacity (0.01-1) of search strobes (yellow; map cones and TacScreen).");
            RWRTrackConeOpacity = Bind(c, SectionMap, "RWRTrackConeOpacity", 0.40f,
                "Opacity (0.01-1) of tracking strobes (orange; map cones and TacScreen).");
            RWRMissileConeOpacity = Bind(c, SectionMap, "RWRMissileConeOpacity", 0.75f,
                "Opacity (0.01-1) of missile guidance strobes (red; map cones and TacScreen).");
            RWRUncertaintyZoneOpacity = Bind(c, SectionMap, "RWRUncertaintyZoneOpacity", 0.10f,
                "Opacity (0.01-1) of map emitter zones.");
            EnableDatalinkVisualStrobes = Bind(c, SectionMap, "EnableDatalinkVisualStrobes", false,
                "Draw threats relayed over the datalink as cyan strobes on the map and TacScreen (the relay itself works without them).");
            RWRDatalinkConeOpacity = Bind(c, SectionMap, "RWRDatalinkConeOpacity", 0.35f,
                "Opacity (0.01-1) of datalink strobes.");
            ShowMissileAimpointOnMap = Bind(c, SectionMap, "ShowMissileAimpointOnMap", false,
                "Show missile aim points, CEP offsets and seeker baskets on the map.");
            ShowPlayerMissilesOnlyOnMap = Bind(c, SectionMap, "ShowPlayerMissilesOnlyOnMap", true,
                "Aim point display only for the player's own missiles (FALSE: all friendly missiles).");
            MinimapZoom = Bind(c, SectionMap, "MinimapZoom", 1.0f,
                "Cockpit minimap scale relative to vanilla (0.25-8): above 1 zooms in, below 1 zooms out. Changed in game by MinimapZoomInKey / MinimapZoomOutKey.");
            MinimapZoomInKey = Bind(c, SectionMap, "MinimapZoomInKey", KeyCode.RightBracket,
                "Hotkey that zooms the minimap in (x1.25 per press).");
            MinimapZoomOutKey = Bind(c, SectionMap, "MinimapZoomOutKey", KeyCode.LeftBracket,
                "Hotkey that zooms the minimap out (x1.25 per press).");
            MinimapLookAhead = Bind(c, SectionMap, "MinimapLookAhead", 0.0f,
                "Distance (m) ahead of the aircraft the minimap is centered on: 0 puts the aircraft in the center (vanilla 4000, centered on the camera).");
        }

        private static void BindCockpitTacScreen(ConfigFile c)
        {
            TacScreenDisplayPreset = Bind(c, SectionTacScreen, "TacScreenDisplayPreset", TacScreenPreset.AdvancedCanvas,
                "'AdvancedCanvas': vector display with velocity leaders and airspeed blocks. 'NativeEnhanced': vanilla MFD with threat coloring.");
            EnableAdvancedCockpitRadarUI = Bind(c, SectionTacScreen, "EnableAdvancedCockpitRadarUI", true,
                "Enables the AdvancedCanvas display (FALSE forces vanilla TacScreen drawing).");
            TacScreenThreatDisplay = Bind(c, SectionTacScreen, "TacScreenThreatDisplay", TacScreenThreatDisplayMode.EmScanRing,
                "RWR threats on the TacScreen (AdvancedCanvas). 'EmScanRing': EM scan ring, received radar and jammer energy by bearing " +
                "(yellow middle circle = search, track and noise jamming; orange outer circle = SARH missile guidance; red inner circle = active radar seeker); " +
                "the bumps show no range. " +
                "'Cones': RWR strobes, length by received signal strength. 'Both' or 'None'. Datalink strobes have their own setting.");
            EnableRWRBearingCone = Bind(c, SectionTacScreen, "EnableRWRBearingCone", true,
                "Draw TacScreen strobes as cones of the angles below. FALSE: thin lines.");
            TacScreenEmScanRingRangeMeters = Bind(c, SectionTacScreen, "TacScreenEmScanRingRangeMeters", 13000.0f,
                "Range (m) on the TacScreen scale at which the yellow EM scan circle is drawn; kept between 10% and 75% of the scope radius.");
            TacScreenEmScanRingSpacing = Bind(c, SectionTacScreen, "TacScreenEmScanRingSpacing", 0.02f,
                "Distance of the orange (outside) and red (inside) EM scan circles from the yellow one, as a fraction of the scope radius.");
            TacScreenEmScanRingBumpHeight = Bind(c, SectionTacScreen, "TacScreenEmScanRingBumpHeight", 0.3f,
                "Outward offset of the strongest signal on the EM scan ring, as a fraction of the scope radius.");
            TacScreenEmScanRingNoise = Bind(c, SectionTacScreen, "TacScreenEmScanRingNoise", 0.5f,
                "Strength of the random receiver noise twitches on the EM scan ring (0 = off).");
            TacScreenEmScanRingThickness = Bind(c, SectionTacScreen, "TacScreenEmScanRingThickness", 0.5f,
                "Line thickness (px, 0.5-6) of the EM scan circles.");
            CockpitRWRSearchSectorAngle = Bind(c, SectionTacScreen, "CockpitRWRSearchSectorAngle", 20.0f,
                "Full cone angle (deg, 1-90) of search strobes (yellow) on the TacScreen; also used for datalink strobes.");
            CockpitRWRTrackSectorAngle = Bind(c, SectionTacScreen, "CockpitRWRTrackSectorAngle", 12.0f,
                "Full cone angle (deg, 1-90) of tracking strobes (orange) on the TacScreen.");
            CockpitRWRMissileSectorAngle = Bind(c, SectionTacScreen, "CockpitRWRMissileSectorAngle", 7.0f,
                "Full cone angle (deg, 1-90) of missile guidance strobes (red) on the TacScreen.");
            CockpitRWRThreatDistanceRatio = Bind(c, SectionTacScreen, "CockpitRWRThreatDistanceRatio", 0.85f,
                "Datalink strobes on the TacScreen extend to this fraction of the reported distance (RWR strobes use signal strength).");
            RadarDisplayUnitSystem = Bind(c, SectionTacScreen, "RadarDisplayUnitSystem", "Metric",
                "Airspeed units in radar data blocks: 'Metric' (km/h) or 'Aviation' (knots).");
            RadarVelocityVectorScale = Bind(c, SectionTacScreen, "RadarVelocityVectorScale", 1.0f,
                "Length multiplier of target velocity leader lines.");
            TargetCamMaxZoom = Bind(c, SectionTacScreen, "TargetCamMaxZoom", 15.0f,
                "Maximum magnification of the target camera on the TacScreen (1-40; vanilla 40).");
            EnableTargetCamIdentDegradation = Bind(c, SectionTacScreen, "EnableTargetCamIdentDegradation", true,
                "Target camera info on the TacScreen follows the faction's tracking quality Q, with the HMD tiers (HMDIdentMidQuality, HMDIdentHighQuality). " +
                "Low: HMD label (AIR? / GRND? / SHIP?) with the Q grade, range to the nearest 10 km, no grid, heading, altitude or speed. " +
                "Mid: class label, range to the nearest 2 km, heading to 45 deg, altitude and relative altitude to 1000 m (5000 ft), speed to 100 km/h (50 kt); no grid and no closure speed. " +
                "High or radar / visual contact: vanilla values, full unit name with the Q grade and pilot name. Untracked units stay vanilla.");
        }

        private static void BindMissileTrajectoryHud(ConfigFile c)
        {
            EnableMissileTrajectoryHUD = Bind(c, SectionTrajectory, "EnableMissileTrajectoryHUD", true,
                "HUD lines following the player's missiles to impact.");
            TrajectoryDisplayMode = Bind(c, SectionTrajectory, "TrajectoryDisplayMode", MissileTrajectoryMode.TrailOnly,
                "'TrailOnly', 'TrailAndLead' (trail + vector to the guidance aim point) or 'LeadOnly'.");
            MissileTrajectoryUpdateRate = Bind(c, SectionTrajectory, "MissileTrajectoryUpdateRate", 8.0f,
                "Telemetry updates per second (2-30).");
            MissileTrajectoryMaxSegments = Bind(c, SectionTrajectory, "MissileTrajectoryMaxSegments", 3,
                "Trail segments behind each missile (2-6).");
            EnableMissileETADisplay = Bind(c, SectionTrajectory, "EnableMissileETADisplay", true,
                "Time-to-impact countdown next to each missile.");
            MissileTrajectoryLineWidth = Bind(c, SectionTrajectory, "MissileTrajectoryLineWidth", 2.5f,
                "Line thickness multiplier.");
        }

        private static void BindHmdThermalWindow(ConfigFile c)
        {
            EnableHMDThermalWindow = Bind(c, SectionThermal, "EnableHMDThermalWindow", true,
                "Thermal window on the HMD (like the F-35 helmet): an IR image of the view in front of your head, through the airframe. " +
                "Only on aircraft with a target camera, only in the cockpit view. HUD and HMD symbols stay on top of it.");
            HMDThermalWindowKey = Bind(c, SectionThermal, "HMDThermalWindowKey", KeyCode.B,
                "Hotkey that opens and closes the HMD thermal window.");
            HMDThermalWindowSize = Bind(c, SectionThermal, "HMDThermalWindowSize", 0.417f,
                "Height of the window as a fraction of the screen height (0.1-1). 0.417 = 450 px at 1080p.");
            HMDThermalWindowAspect = Bind(c, SectionThermal, "HMDThermalWindowAspect", 1.444f,
                "Width of the window divided by its height (0.5-3). 1.444 = 650 x 450.");
            HMDThermalWindowOffsetY = Bind(c, SectionThermal, "HMDThermalWindowOffsetY", 0.0f,
                "Vertical position of the window center from the screen center, as a fraction of the screen height (negative = down).");
            HMDThermalWindowOpacity = Bind(c, SectionThermal, "HMDThermalWindowOpacity", 0.35f,
                "Opacity of the thermal window (0-1). Hot spots fade with it.");
            HMDThermalResolutionScale = Bind(c, SectionThermal, "HMDThermalResolutionScale", 0.85f,
                "Render resolution of the window relative to its size on the screen (0.25-1). Lower values cost less GPU time.");
            HMDThermalLodBias = Bind(c, SectionThermal, "HMDThermalLodBias", 0.5f,
                "Level of detail of the thermal image as a fraction of the game's LOD bias (0.1-1). Lower values draw simpler models in the window and cost less CPU time.");
            HMDThermalNearClip = Bind(c, SectionThermal, "HMDThermalNearClip", 25.0f,
                "Distance (m) from the eye at which the thermal image starts. Everything nearer (your own airframe) is not shown.");
            HMDThermalMaxRange = Bind(c, SectionThermal, "HMDThermalMaxRange", 15000.0f,
                "Maximum range (m) at which engines, rocket motors and flares show as hot spots in the window.");
            HMDThermalContrast = Bind(c, SectionThermal, "HMDThermalContrast", 20.0f,
                "Contrast of the thermal image (-100 to 100; the vanilla target camera IR mode uses 1).");
            HMDThermalHotSpotScale = Bind(c, SectionThermal, "HMDThermalHotSpotScale", 1.0f,
                "Size multiplier of the hot spots (0 = no hot spots).");
        }
    }
}
