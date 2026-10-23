using System.ComponentModel;
using BepInEx.Configuration;
using UnityEngine;

namespace CnoEvil.Config
{
    public static partial class CnoEvilConfig
    {
        // 12. HUD & Markers
        public static ConfigEntry<bool> EnableHUDMarkerQualityTint;
        public static ConfigEntry<bool> EnableHMDTargetIdentDegradation;
        public static ConfigEntry<float> IdentMidQuality;
        public static ConfigEntry<float> IdentHighQuality;
        public static ConfigEntry<bool> EnableHUDEWCues;
        public static ConfigEntry<bool> DisableVanillaNotchAids;
        public static ConfigEntry<bool> EnableHUDDetectionStatus;
        public static ConfigEntry<float> DetectionStatusInterval_s;
        public static ConfigEntry<bool> EnableHUDOwnRCS;
        public static ConfigEntry<bool> EnableHUDSnrIndicator;

        // 13. Tactical Map & Minimap
        public static ConfigEntry<bool> EnableMapRWRZones;
        public static ConfigEntry<bool> EnableMinimapRWRZones;
        public static ConfigEntry<bool> HideUnlocatedThreatIcons;
        public static ConfigEntry<float> MapRWRZoneNearRange_m;
        public static ConfigEntry<float> MapRWRZoneNearRadius_m;
        public static ConfigEntry<float> MapRWRZoneFarRange_m;
        public static ConfigEntry<float> MapRWRZoneFarRadius_m;
        public static ConfigEntry<float> RWRSearchStrobeOpacity;
        public static ConfigEntry<float> RWRTrackStrobeOpacity;
        public static ConfigEntry<float> RWRMissileStrobeOpacity;
        public static ConfigEntry<float> MapRWRZoneOpacity;
        public static ConfigEntry<bool> EnableDatalinkStrobes;
        public static ConfigEntry<float> RWRDatalinkStrobeOpacity;
        public static ConfigEntry<bool> ShowMissileAimpointOnMap;
        public static ConfigEntry<bool> MissileAimpointPlayerOnly;
        public static ConfigEntry<float> MinimapZoom;
        public static ConfigEntry<KeyCode> MinimapZoomInKey;
        public static ConfigEntry<KeyCode> MinimapZoomOutKey;
        public static ConfigEntry<float> MinimapLookAhead_m;

        // 14. Cockpit TacScreen
        public enum TacScreenPreset
        {
            AdvancedCanvas,  // Vector display: velocity leaders, data block of the selected target
            NativeEnhanced,  // Vanilla MFD with threat ping colours
            [Description("PortalMFD")]
            PortalMFD        // Portal display: two halves, one page per half, hotkeys only
        }

        // [Description] keeps ConfigurationManager from spacing the capitals ("R A D A R")
        public enum PortalMFDPage
        {
            [Description("RADAR")] RADAR,
            [Description("TSD")] TSD,
            [Description("RWR")] RWR,
            [Description("SMS")] SMS,
            [Description("EOTS")] EOTS,
            [Description("NAV")] NAV,
            [Description("HSI")] HSI
        }

        public enum TacScreenThreatDisplayMode
        {
            EmScanRing,  // EM scan ring only
            Cones,       // RWR strobes only
            Both,
            None
        }

        public static ConfigEntry<TacScreenPreset> TacScreenDisplayPreset;
        public static ConfigEntry<TacScreenThreatDisplayMode> TacScreenThreatDisplay;
        public static ConfigEntry<bool> RWRStrobeCones;
        public static ConfigEntry<float> TacScreenEmScanRingRange_m;
        public static ConfigEntry<float> TacScreenEmScanRingSpacing;
        public static ConfigEntry<float> TacScreenEmScanRingBumpHeight;
        public static ConfigEntry<float> TacScreenEmScanRingNoise;
        public static ConfigEntry<float> TacScreenEmScanRingThickness_px;
        public static ConfigEntry<float> RWRSearchStrobeAngle_deg;
        public static ConfigEntry<float> RWRTrackStrobeAngle_deg;
        public static ConfigEntry<float> RWRMissileStrobeAngle_deg;
        public static ConfigEntry<float> DatalinkStrobeLengthRatio;
        public static ConfigEntry<float> RadarVelocityVectorScale;
        public static ConfigEntry<float> TargetCamMaxZoom;
        public static ConfigEntry<bool> EnableTargetCamIdentDegradation;
        public static ConfigEntry<KeyCode> PortalMFDLeftPageKey;
        public static ConfigEntry<KeyCode> PortalMFDRightPageKey;
        public static ConfigEntry<PortalMFDPage> PortalMFDLeftPage;
        public static ConfigEntry<PortalMFDPage> PortalMFDRightPage;
        public static ConfigEntry<bool> PortalMFDAutoEots;
        public static ConfigEntry<float> PortalMFDTsdRange_km;
        public static ConfigEntry<float> PortalMFDNavRange_km;
        public static ConfigEntry<float> PortalMFDRenderScale;
        public static ConfigEntry<float> PortalUncertaintyRingScale;

        // 15. Missile Trajectory HUD
        public enum MissileTrajectoryMode
        {
            TrailOnly,       // Broken polyline trail behind missile
            TrailAndLead,    // Broken trail behind missile + forward lead vector to target
            LeadOnly         // Straight vector from missile to target
        }

        public static ConfigEntry<bool> EnableMissileTrajectoryHUD;
        public static ConfigEntry<MissileTrajectoryMode> TrajectoryDisplayMode;
        public static ConfigEntry<float> MissileTrailSampleRate;
        public static ConfigEntry<int> MissileTrailSegments;
        public static ConfigEntry<bool> EnableMissileETADisplay;
        public static ConfigEntry<float> MissileTrajectoryLineWidth;

        // 16. HMD Thermal Window
        public static ConfigEntry<bool> EnableHMDThermalWindow;
        public static ConfigEntry<KeyCode> HMDThermalWindowKey;
        public static ConfigEntry<KeyCode> HMDThermalZoomKey;
        public static ConfigEntry<float> HMDThermalWindowSize;
        public static ConfigEntry<float> HMDThermalWindowAspect;
        public static ConfigEntry<float> HMDThermalWindowOffsetY;
        public static ConfigEntry<float> HMDThermalWindowOpacity;
        public static ConfigEntry<float> HMDThermalResolutionScale;
        public static ConfigEntry<float> HMDThermalLodBias;
        public static ConfigEntry<float> HMDThermalNearClip_m;
        public static ConfigEntry<float> HMDThermalMaxRange_m;
        public static ConfigEntry<float> HMDThermalContrast;
        public static ConfigEntry<float> HMDThermalHotSpotScale;

        private const string SectionHud = "12. HUD & Markers";
        private const string SectionMap = "13. Tactical Map & Minimap";
        private const string SectionTacScreen = "14. Cockpit TacScreen";
        private const string SectionTrajectory = "15. Missile Trajectory HUD";
        private const string SectionThermal = "16. HMD Thermal Window";

        private static void BindHudMarkers(ConfigFile c)
        {
            EnableHUDMarkerQualityTint = Bind(c, SectionHud, "EnableHUDMarkerQualityTint", true,
                "Tint the vanilla HUD markers of tracked threats by the faction's tracking quality Q: pale white (low) -> amber -> red (fire-control quality). Markers, positions and locking stay vanilla.");
            EnableHMDTargetIdentDegradation = Bind(c, SectionHud, "EnableHMDTargetIdentDegradation", true,
                "Selected target info on the HMD and in the target box of the PortalMFD TSD page follows the faction's tracking quality Q. " +
                "Low: domain only (AIR? / GRND? / SHIP?) and range to the nearest 10 km. " +
                "Mid: class (FTR, ATK, BMR, EW, HELO / AAA, SAM, RDR, building type, GRND / SHIP) and range to the nearest 2 km. " +
                "High or radar / visual contact: vanilla designation (ships: hull class), exact range and pilot name. Coarse ranges are " +
                "always in km. The label also shows the Q grade (Q>0, Q>25, Q>50, Q>75; none at Q = 0 and at full quality). " +
                "Untracked units stay vanilla.");
            IdentMidQuality = Bind(c, SectionHud, "IdentMidQuality", 0.40f,
                "Tracking quality Q of the mid ident tier (class, range to 2 km). Used by the HMD, the target camera, the PortalMFD TSD " +
                "target box and the enemy tiers of the detection status line.");
            IdentHighQuality = Bind(c, SectionHud, "IdentHighQuality", 0.75f,
                "Tracking quality Q of the high ident tier (full vanilla target info). Same users as IdentMidQuality.");
            EnableHUDEWCues = Bind(c, SectionHud, "EnableHUDEWCues", true,
                "EW and missile cues on the flight HUD compass: [ NOTCH OK ], [ NO CLUTTER ], [ BEAMING ], [ LOCK ], [ ECM ON ], " +
                "[ ECM NOTCH ], [ HOJ RISK ], [ BURN-THROUGH ], [ PITBULL ], [ MISSILE ], [ DL: MISSILE (clock) ].");
            DisableVanillaNotchAids = Bind(c, SectionHud, "DisableVanillaNotchAids", true,
                "Hide the vanilla notch director box (with exact missile range) on the HUD and the notch line on the map.");
            EnableHUDDetectionStatus = Bind(c, SectionHud, "EnableHUDDetectionStatus", true,
                "Show one more line below the altitude on the HUD: the best track that a hostile faction has on your aircraft. " +
                "ID NO: no track. ID MEM: the enemy lost contact and keeps the track in memory. ID LOW / ID MED / ID HIGH: active track, " +
                "with the ident tiers (IdentMidQuality, IdentHighQuality; a radar contact gives HIGH). ID OPT: visual contact. " +
                "In multiplayer the host sends the value to clients that have the mod. A client on a host without the mod does not show the line.");
            DetectionStatusInterval_s = Bind(c, SectionHud, "DetectionStatusInterval_s", 1.0f,
                "Interval (s, minimum 0.25) at which the detection status line and the RCS line are calculated. The host also sends detection status changes to clients at this interval.");
            EnableHUDOwnRCS = Bind(c, SectionHud, "EnableHUDOwnRCS", true,
                "Show one more line below the detection status line on the HUD: the frontal RCS (m2) of your aircraft, " +
                "with external stores and open bay doors. Your computer calculates the value, also in multiplayer.");
            EnableHUDSnrIndicator = Bind(c, SectionHud, "EnableHUDSnrIndicator", true,
                "Show a bar below the G readout on the HUD: the signal (SNR) of the strongest hostile radar on your aircraft, " +
                "-20 dB to +20 dB from its detection threshold (center mark). The bar is amber when the radar detects you " +
                "and red when it guides a missile. Active missile seekers are not shown.");
        }

        private static void BindTacticalMap(ConfigFile c)
        {
            EnableMapRWRZones = Bind(c, SectionMap, "EnableMapRWRZones", true,
                "RWR emitter zones on the full map (M) from your receiver, replacing vanilla's exact ping lines: a translucent circle " +
                "that contains the emitter at a random point (never known to be its center) and a cone from the aircraft to it. " +
                "A located emitter (LocatedQualityThreshold) gets the cone only.");
            EnableMinimapRWRZones = Bind(c, SectionMap, "EnableMinimapRWRZones", true,
                "Same zones on the cockpit minimap.");
            HideUnlocatedThreatIcons = Bind(c, SectionMap, "HideUnlocatedThreatIcons", true,
                "Hide map and minimap icons of enemy tracked threats (aircraft, ships, air defense, radars) the faction has not located: " +
                "no recent radar / visual contact and Q below LocatedQualityThreshold. FALSE: their vanilla icons stay. HUD markers stay " +
                "vanilla, but the vanilla tracking entry from RWR pings also waits for LocatedQualityThreshold.");
            MapRWRZoneNearRange_m = Bind(c, SectionMap, "MapRWRZoneNearRange_m", 3000.0f,
                "Emitter range (m) at which the map zone has its smallest radius (MapRWRZoneNearRadius_m).");
            MapRWRZoneNearRadius_m = Bind(c, SectionMap, "MapRWRZoneNearRadius_m", 1000.0f,
                "Map zone radius (m) at MapRWRZoneNearRange_m and closer.");
            MapRWRZoneFarRange_m = Bind(c, SectionMap, "MapRWRZoneFarRange_m", 80000.0f,
                "Emitter range (m) at which the map zone has its largest radius (MapRWRZoneFarRadius_m); linear in between.");
            MapRWRZoneFarRadius_m = Bind(c, SectionMap, "MapRWRZoneFarRadius_m", 10000.0f,
                "Map zone radius (m) at MapRWRZoneFarRange_m and beyond.");
            RWRSearchStrobeOpacity = Bind(c, SectionMap, "RWRSearchStrobeOpacity", 0.10f,
                "Opacity (0.01-1) of search strobes (yellow) on the map and on the AdvancedCanvas TacScreen.");
            RWRTrackStrobeOpacity = Bind(c, SectionMap, "RWRTrackStrobeOpacity", 0.40f,
                "Opacity (0.01-1) of tracking strobes (orange) on the map and on the AdvancedCanvas TacScreen.");
            RWRMissileStrobeOpacity = Bind(c, SectionMap, "RWRMissileStrobeOpacity", 0.75f,
                "Opacity (0.01-1) of missile guidance strobes (red) on the map and on the AdvancedCanvas TacScreen.");
            MapRWRZoneOpacity = Bind(c, SectionMap, "MapRWRZoneOpacity", 0.10f,
                "Opacity (0.01-1) of the map emitter zone circles.");
            EnableDatalinkStrobes = Bind(c, SectionMap, "EnableDatalinkStrobes", false,
                "Draw threats relayed over the datalink as cyan strobes on the map (needs EnableMapRWRZones or EnableMinimapRWRZones) " +
                "and on the AdvancedCanvas TacScreen. The relay itself works without them.");
            RWRDatalinkStrobeOpacity = Bind(c, SectionMap, "RWRDatalinkStrobeOpacity", 0.35f,
                "Opacity (0.01-1) of datalink strobes.");
            ShowMissileAimpointOnMap = Bind(c, SectionMap, "ShowMissileAimpointOnMap", false,
                "Show missile aim points, CEP offsets and seeker baskets on the map.");
            MissileAimpointPlayerOnly = Bind(c, SectionMap, "MissileAimpointPlayerOnly", true,
                "With ShowMissileAimpointOnMap: only your own missiles. FALSE: all missiles of your faction.");
            MinimapZoom = Bind(c, SectionMap, "MinimapZoom", 1.0f,
                "Cockpit minimap scale relative to vanilla (0.25-8): above 1 zooms in, below 1 zooms out. Changed in game by MinimapZoomInKey / MinimapZoomOutKey.");
            MinimapZoomInKey = Bind(c, SectionMap, "MinimapZoomInKey", KeyCode.RightBracket,
                "Hotkey that zooms the minimap in (x1.25 per press, saved to MinimapZoom).");
            MinimapZoomOutKey = Bind(c, SectionMap, "MinimapZoomOutKey", KeyCode.LeftBracket,
                "Hotkey that zooms the minimap out (x1.25 per press, saved to MinimapZoom).");
            MinimapLookAhead_m = Bind(c, SectionMap, "MinimapLookAhead_m", 0.0f,
                "Distance (m) ahead of the aircraft the minimap is centered on: 0 puts the aircraft in the center (vanilla 4000, centered on the camera).");
        }

        private static void BindCockpitTacScreen(ConfigFile c)
        {
            TacScreenDisplayPreset = Bind(c, SectionTacScreen, "TacScreenDisplayPreset", TacScreenPreset.PortalMFD,
                "'AdvancedCanvas': vector display with velocity leaders and a data block of the selected target. 'NativeEnhanced': vanilla " +
                "MFD with threat ping colours. 'PortalMFD': portal display with two halves; each half shows one page (TSD, RADAR, RWR, " +
                "EOTS, SMS, NAV, HSI). PortalMFDLeftPageKey and PortalMFDRightPageKey cycle the pages; with Shift they cycle the range. " +
                "A change applies at once, also in flight.");
            TacScreenThreatDisplay = Bind(c, SectionTacScreen, "TacScreenThreatDisplay", TacScreenThreatDisplayMode.EmScanRing,
                "RWR threats on the TacScreen. 'EmScanRing': EM scan ring, received radar and jammer energy by bearing " +
                "(yellow middle circle = search, track and noise jamming; orange outer circle = SARH missile guidance; red inner circle = " +
                "active radar seeker); the bumps show no range. 'Cones': RWR strobes, length by received signal strength. 'Both' or 'None'. " +
                "The PortalMFD TSD page draws only the ring ('EmScanRing' or 'Both'). Datalink strobes have their own setting.");
            RWRStrobeCones = Bind(c, SectionTacScreen, "RWRStrobeCones", true,
                "AdvancedCanvas with 'Cones' or 'Both': draw RWR strobes as cones of the angles below. FALSE: thin lines.");
            TacScreenEmScanRingRange_m = Bind(c, SectionTacScreen, "TacScreenEmScanRingRange_m", 25000.0f,
                "Range (m) on the TacScreen scale at which the yellow EM scan circle is drawn; kept between 10% and 75% of the scope radius.");
            TacScreenEmScanRingSpacing = Bind(c, SectionTacScreen, "TacScreenEmScanRingSpacing", 0.02f,
                "Distance of the orange (outside) and red (inside) EM scan circles from the yellow one, as a fraction of the scope radius.");
            TacScreenEmScanRingBumpHeight = Bind(c, SectionTacScreen, "TacScreenEmScanRingBumpHeight", 0.5f,
                "Outward offset of the strongest signal on the EM scan ring, as a fraction of the scope radius.");
            TacScreenEmScanRingNoise = Bind(c, SectionTacScreen, "TacScreenEmScanRingNoise", 0.5f,
                "Strength of the random receiver noise twitches on the EM scan ring (0 = off).");
            TacScreenEmScanRingThickness_px = Bind(c, SectionTacScreen, "TacScreenEmScanRingThickness_px", 0.5f,
                "Line thickness (px, 0.5-6) of the EM scan circles.");
            RWRSearchStrobeAngle_deg = Bind(c, SectionTacScreen, "RWRSearchStrobeAngle_deg", 20.0f,
                "Full cone angle (deg, 1-90) of search strobes (yellow) on the AdvancedCanvas TacScreen; also used for datalink strobes. " +
                "Never narrower than the receiver's bearing error.");
            RWRTrackStrobeAngle_deg = Bind(c, SectionTacScreen, "RWRTrackStrobeAngle_deg", 12.0f,
                "Full cone angle (deg, 1-90) of tracking strobes (orange) on the AdvancedCanvas TacScreen.");
            RWRMissileStrobeAngle_deg = Bind(c, SectionTacScreen, "RWRMissileStrobeAngle_deg", 7.0f,
                "Full cone angle (deg, 1-90) of missile guidance strobes (red) on the AdvancedCanvas TacScreen.");
            DatalinkStrobeLengthRatio = Bind(c, SectionTacScreen, "DatalinkStrobeLengthRatio", 0.85f,
                "Datalink strobes on the AdvancedCanvas TacScreen (EnableDatalinkStrobes) extend to this fraction (0.05-2) of the reported " +
                "distance. RWR strobes use the signal strength.");
            RadarVelocityVectorScale = Bind(c, SectionTacScreen, "RadarVelocityVectorScale", 1.0f,
                "Length multiplier (minimum 0.1) of the target velocity leader lines on the AdvancedCanvas TacScreen.");
            TargetCamMaxZoom = Bind(c, SectionTacScreen, "TargetCamMaxZoom", 15.0f,
                "Maximum magnification of the target camera on the TacScreen (1-40; vanilla 40).");
            EnableTargetCamIdentDegradation = Bind(c, SectionTacScreen, "EnableTargetCamIdentDegradation", true,
                "Target camera info on the TacScreen follows the faction's tracking quality Q, with the ident tiers (IdentMidQuality, IdentHighQuality). " +
                "Low: HMD label (AIR? / GRND? / SHIP?) with the Q grade, range to the nearest 10 km, no grid, heading, altitude or speed. " +
                "Mid: class label, range to the nearest 2 km, heading to 45 deg, altitude and relative altitude to 1000 m (5000 ft), speed to 100 km/h (50 kt); no grid and no closure speed. " +
                "High or radar / visual contact: vanilla values, full unit name with the Q grade and pilot name. Untracked units stay vanilla. " +
                "The map target marker and the map tooltip use the same tiers; below High the tooltip shows no range circles.");
            PortalMFDLeftPageKey = Bind(c, SectionTacScreen, "PortalMFDLeftPageKey", KeyCode.Semicolon,
                "PortalMFD: hotkey that shows the next page on the left half (TSD, RADAR, RWR, EOTS, SMS, NAV, HSI; the page on the right half is skipped). " +
                "With Shift: the next range of the page (TSD, NAV, RADAR), the shortest after the longest. Ignored while typing in a text field.");
            PortalMFDRightPageKey = Bind(c, SectionTacScreen, "PortalMFDRightPageKey", KeyCode.Quote,
                "PortalMFD: the same as PortalMFDLeftPageKey for the right half.");
            PortalMFDLeftPage = Bind(c, SectionTacScreen, "PortalMFDLeftPage", PortalMFDPage.TSD,
                "PortalMFD: page on the left half at spawn. A change in flight shows the page at once.");
            PortalMFDRightPage = Bind(c, SectionTacScreen, "PortalMFDRightPage", PortalMFDPage.RWR,
                "PortalMFD: page on the right half at spawn. A change in flight shows the page at once. When it equals the left page, " +
                "the right half shows TSD (or RADAR when the left page is TSD).");
            PortalMFDAutoEots = Bind(c, SectionTacScreen, "PortalMFDAutoEots", true,
                "PortalMFD: when the target camera or the landing camera comes on and no half shows EOTS, the right half shows the EOTS page. " +
                "When the camera goes off, the half shows its previous page again (the next free page if the left half shows it now).");
            PortalMFDTsdRange_km = Bind(c, SectionTacScreen, "PortalMFDTsdRange_km", 40.0f,
                "PortalMFD: range (km, 5-80) of the TSD page, from the aircraft to the outer ring. Shift + the page key steps through 5, 10, 20, 40, 80.");
            PortalMFDNavRange_km = Bind(c, SectionTacScreen, "PortalMFDNavRange_km", 80.0f,
                "PortalMFD: range (km, 5-80) of the NAV page, from the aircraft to the outer ring. Shift + the page key steps through 5, 10, 20, 40, 80.");
            PortalMFDRenderScale = c.Bind(SectionTacScreen, "PortalMFDRenderScale", 2f, new ConfigDescription(
                "PortalMFD: resolution of the cockpit screen texture, times the vanilla 1024 x 512 (1, 1.5 or 2). " +
                "Higher values give sharper text and lines; the texture also gets mipmaps, so it does not shimmer at an angle. " +
                "A change applies at once.", new AcceptableValueList<float>(1f, 1.5f, 2f)));
            PortalUncertaintyRingScale = Bind(c, SectionTacScreen, "PortalUncertaintyRingScale", 0.30f,
                "PortalMFD TSD / NAV: radius of the uncertainty ring of a hostile track in memory, as a fraction (0.2-3) of the track's CEP " +
                "(larger for weak RWR signals). The ring grows while Q decays.");
        }

        private static void BindMissileTrajectoryHud(ConfigFile c)
        {
            EnableMissileTrajectoryHUD = Bind(c, SectionTrajectory, "EnableMissileTrajectoryHUD", true,
                "HUD lines following the player's missiles to impact.");
            TrajectoryDisplayMode = Bind(c, SectionTrajectory, "TrajectoryDisplayMode", MissileTrajectoryMode.TrailOnly,
                "'TrailOnly', 'TrailAndLead' (trail + vector to the guidance aim point) or 'LeadOnly'.");
            MissileTrailSampleRate = Bind(c, SectionTrajectory, "MissileTrailSampleRate", 8.0f,
                "Trail samples and ETA updates per second (2-30).");
            MissileTrailSegments = Bind(c, SectionTrajectory, "MissileTrailSegments", 3,
                "Trail segments behind each missile (2-6).");
            EnableMissileETADisplay = Bind(c, SectionTrajectory, "EnableMissileETADisplay", true,
                "Time-to-impact countdown next to each missile.");
            MissileTrajectoryLineWidth = Bind(c, SectionTrajectory, "MissileTrajectoryLineWidth", 2.5f,
                "Line width: 2.5 = standard; 1.25 to 6.25 give half to 2.5 x the standard width.");
        }

        private static void BindHmdThermalWindow(ConfigFile c)
        {
            EnableHMDThermalWindow = Bind(c, SectionThermal, "EnableHMDThermalWindow", true,
                "Thermal window on the HMD: an IR image of the view in front of your head, through the airframe. " +
                "Only on aircraft with a target camera, only in the cockpit view. HUD and HMD symbols stay on top of it.");
            HMDThermalWindowKey = Bind(c, SectionThermal, "HMDThermalWindowKey", KeyCode.B,
                "Hotkey that opens and closes the HMD thermal window. It opens only while the window can show.");
            HMDThermalZoomKey = Bind(c, SectionThermal, "HMDThermalZoomKey", KeyCode.V,
                "Hotkey that cycles the zoom of the HMD thermal window: 1x, 5x, 10x. The window opens at 1x.");
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
                "Level of detail of the thermal image as a fraction of the game's LOD bias (0.1-1), times the zoom. Lower values draw " +
                "simpler models in the window and cost less CPU time.");
            HMDThermalNearClip_m = Bind(c, SectionThermal, "HMDThermalNearClip_m", 25.0f,
                "Distance (m, minimum 0.3) from the eye at which the thermal image starts. Everything nearer (your own airframe) is not shown.");
            HMDThermalMaxRange_m = Bind(c, SectionThermal, "HMDThermalMaxRange_m", 15000.0f,
                "Maximum range (m) at 1x zoom at which engines, rocket motors and flares show as hot spots in the window; it grows with the zoom.");
            HMDThermalContrast = Bind(c, SectionThermal, "HMDThermalContrast", 20.0f,
                "Contrast of the thermal image (-100 to 100; the vanilla target camera IR mode uses 1).");
            HMDThermalHotSpotScale = Bind(c, SectionThermal, "HMDThermalHotSpotScale", 1.0f,
                "Size multiplier of the hot spots (0 = no hot spots).");
        }
    }
}
