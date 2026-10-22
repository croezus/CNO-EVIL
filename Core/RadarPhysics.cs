using System;
using UnityEngine;
using CnoEvil.Config;

namespace CnoEvil.Core
{
    /// <summary>
    /// Radar hardware sets (band, antenna, receiver, losses). The peak power is scaled to the range of each radar.
    /// </summary>
    public enum RadarTemplate
    {
        FromType,           // Select from the unit type (overrides only)
        Seeker,             // Ku band missile seeker
        Helicopter,         // Ku band mast radar
        Fighter4thGen,      // X band pulse-Doppler
        Fighter5thGenAESA,  // X band AESA
        NavalCBand,         // C band destroyer / frigate air-defense radar
        HeavySBand,         // S band search radar: radar trucks and stations, carriers, other surface units
        TacticalXBand       // X band short-range radar: SAM and AAA vehicles, light ships
    }

    public struct RadarSpecs
    {
        public float PeakPowerWatts;      // Pt (W)
        public float AntennaGainLinear;   // Gt = Gr (linear power ratio, e.g. 32 dBi ≈ 1585)
        public float WavelengthMeters;    // λ = c / f (m): S 0.10 (3 GHz), C 0.0545 (5.5 GHz), X 0.0316 (9.5 GHz), Ku 0.02 (15 GHz)
        public float BandwidthHz;         // B (Hz)
        public float NoiseFigureLinear;   // F (e.g. 3.0 dB ≈ 2.0)
        public float SystemLossLinear;    // Lsys (e.g. 4 dB ≈ 2.51)
        public float ProcessingGainLinear;// Gp (Pulse compression & Doppler integration gain ~25-35 dB = 300-3500)
        public float AttrCoeff_dB_per_km; // Atmospheric loss dB/km

        // SNR of a 1 m² target at the reference range of each template (the templates are tuned to it)
        public const float CalibrationSNR_dB = 10.0f;

        // C band naval air defense (DDG, FFG). Peak power gives 120 km on 1 m².
        public static RadarSpecs CreateNavalAirDefenseDestroyer()
        {
            RadarSpecs specs;
            specs.PeakPowerWatts = 10200.0f;         // 10.2 kW naval air defense
            specs.AntennaGainLinear = 2818.0f;       // ~34.5 dBi
            specs.WavelengthMeters = 0.05451f;
            specs.BandwidthHz = 3.0e6f;              // 3 MHz
            specs.NoiseFigureLinear = 2.0f;          // ~3.0 dB
            specs.SystemLossLinear = 2.51f;          // ~4 dB
            specs.ProcessingGainLinear = 2000.0f;    // ~33.0 dB
            specs.AttrCoeff_dB_per_km = 0.012f;
            return specs;
        }

        // S band heavy search radar (radar vehicles and buildings, CV, LHA, other surface units). 100 km on 1 m².
        public static RadarSpecs CreateHeavyGroundNavalSBand()
        {
            RadarSpecs specs;
            specs.PeakPowerWatts = 1250.0f;          // 1.25 kW calibrated S-band power (100 km on 1 m²)
            specs.AntennaGainLinear = 2500.0f;       // ~34.0 dBi
            specs.WavelengthMeters = 0.09993f;       // S-band wavelength
            specs.BandwidthHz = 2.0e6f;              // 2 MHz
            specs.NoiseFigureLinear = 2.0f;          // ~3.0 dB
            specs.SystemLossLinear = 2.51f;          // ~4 dB
            specs.ProcessingGainLinear = 1500.0f;    // ~31.8 dB
            specs.AttrCoeff_dB_per_km = 0.008f;      // low S-band attenuation
            return specs;
        }

        // X band AESA (aircraft with a stealth airframe). 100 km on 1 m².
        public static RadarSpecs CreateHeavy5thGenAESA()
        {
            RadarSpecs specs;
            specs.PeakPowerWatts = 16400.0f;        // 16.4 kW peak AESA GaN power (100 km on 1 m²)
            specs.AntennaGainLinear = 2818.0f;      // ~34.5 dBi nose array
            specs.WavelengthMeters = 0.03155f;      // X-band
            specs.BandwidthHz = 5.0e6f;             // 5 MHz agile pulse
            specs.NoiseFigureLinear = 1.78f;        // ~2.5 dB ultra-low noise receiver
            specs.SystemLossLinear = 2.0f;          // ~3 dB low AESA distribution losses
            specs.ProcessingGainLinear = 2500.0f;   // ~34.0 dB advanced digital STAP
            specs.AttrCoeff_dB_per_km = 0.018f;
            return specs;
        }

        // X band pulse-Doppler (other fixed-wing aircraft). 80 km on 1 m².
        public static RadarSpecs CreateStandard4thGen()
        {
            RadarSpecs specs;
            specs.PeakPowerWatts = 20000.0f;         // 20 kW pulse-doppler (80 km on 1 m²)
            specs.AntennaGainLinear = 2000.0f;       // ~33.0 dBi
            specs.WavelengthMeters = 0.03155f;       // X-band
            specs.BandwidthHz = 5.0e6f;              // 5 MHz
            specs.NoiseFigureLinear = 2.0f;          // ~3.0 dB
            specs.SystemLossLinear = 2.51f;          // ~4 dB
            specs.ProcessingGainLinear = 2000.0f;    // ~33.0 dB
            specs.AttrCoeff_dB_per_km = 0.018f;
            return specs;
        }

        // X band short-range radar (SAM and AAA vehicles, FFL, LFD, LC, PB). 50 km on 1 m².
        public static RadarSpecs CreateTacticalSAMRadar()
        {
            RadarSpecs specs;
            specs.PeakPowerWatts = 6500.0f;          // 6.5 kW short-range radar (50 km on 1 m²)
            specs.AntennaGainLinear = 1400.0f;       // ~31.5 dBi
            specs.WavelengthMeters = 0.03155f;       // X-band
            specs.BandwidthHz = 4.0e6f;              // 4 MHz
            specs.NoiseFigureLinear = 2.0f;          // ~3.0 dB
            specs.SystemLossLinear = 2.51f;          // ~4 dB
            specs.ProcessingGainLinear = 1200.0f;    // ~30.8 dB
            specs.AttrCoeff_dB_per_km = 0.018f;
            return specs;
        }

        // Ku band helicopter mast radar (aircraft with a swashplate rotor). 35 km on 1 m².
        public static RadarSpecs CreateLightHeloRadar()
        {
            RadarSpecs specs;
            specs.PeakPowerWatts = 3300.0f;          // 3.3 kW compact mast radar (35 km on 1 m²)
            specs.AntennaGainLinear = 2000.0f;       // ~33.0 dBi
            specs.WavelengthMeters = 0.01998f;       // Ku-band
            specs.BandwidthHz = 5.0e6f;              // 5 MHz
            specs.NoiseFigureLinear = 2.0f;          // ~3.0 dB
            specs.SystemLossLinear = 2.51f;          // ~4 dB
            specs.ProcessingGainLinear = 1000.0f;    // ~30.0 dB
            specs.AttrCoeff_dB_per_km = 0.035f;
            return specs;
        }

        // Ku band active missile seeker. 20 km on 1 m².
        public static RadarSpecs CreateSeekerRadar()
        {
            RadarSpecs specs;
            specs.PeakPowerWatts = 4900.0f;          // 4.9 kW active seeker dish (20 km on 1 m²)
            specs.AntennaGainLinear = 900.0f;        // ~29.5 dBi
            specs.WavelengthMeters = 0.01998f;       // Ku-band
            specs.BandwidthHz = 8.0e6f;              // 8 MHz
            specs.NoiseFigureLinear = 2.24f;         // ~3.5 dB
            specs.SystemLossLinear = 3.0f;           // ~4.8 dB
            specs.ProcessingGainLinear = 600.0f;     // ~27.8 dB
            specs.AttrCoeff_dB_per_km = 0.035f;
            return specs;
        }

        public static RadarSpecs CreateTemplate(RadarTemplate template)
        {
            switch (template)
            {
                case RadarTemplate.Seeker: return CreateSeekerRadar();
                case RadarTemplate.Helicopter: return CreateLightHeloRadar();
                case RadarTemplate.Fighter5thGenAESA: return CreateHeavy5thGenAESA();
                case RadarTemplate.NavalCBand: return CreateNavalAirDefenseDestroyer();
                case RadarTemplate.HeavySBand: return CreateHeavyGroundNavalSBand();
                case RadarTemplate.TacticalXBand: return CreateTacticalSAMRadar();
                default: return CreateStandard4thGen();
            }
        }

        /// <summary>
        /// Radar hardware from the vanilla unit type, the same data the game uses for icons and roles:
        /// Unit subclass, ShipDefinition.shipType, VehicleDefinition.vehicleType, airframe radarSize, swashplate rotor.
        /// </summary>
        public static RadarTemplate GetTemplateForUnit(Unit unit)
        {
            if (unit == null) return RadarTemplate.Fighter4thGen;
            if (unit is Missile) return RadarTemplate.Seeker;

            if (unit is Aircraft)
            {
                if (unit.GetComponentInChildren<SwashRotor>(true) != null) return RadarTemplate.Helicopter;
                float rcs = (unit.definition != null) ? unit.definition.radarSize : 0.0f;
                if (AspectRCSProfile.IsStealthAirframe(rcs)) return RadarTemplate.Fighter5thGenAESA;
                return RadarTemplate.Fighter4thGen;
            }

            ShipDefinition ship = unit.definition as ShipDefinition;
            if (ship != null)
            {
                switch (ship.shipType)
                {
                    case ShipType.DDG:
                    case ShipType.FFG:
                        return RadarTemplate.NavalCBand;
                    case ShipType.CV:
                    case ShipType.LHA:
                        return RadarTemplate.HeavySBand;
                    default:
                        return RadarTemplate.TacticalXBand;
                }
            }

            VehicleDefinition vehicle = unit.definition as VehicleDefinition;
            if (vehicle != null)
            {
                switch (vehicle.vehicleType)
                {
                    case VehicleType.AAA:
                    case VehicleType.IR_SAM:
                    case VehicleType.R_SAM:
                        return RadarTemplate.TacticalXBand;
                }
            }

            // Radar vehicles and buildings, other surface units
            return RadarTemplate.HeavySBand;
        }

        /// <summary>
        /// Vanilla range of a radar on a 1 m² target: vanilla signal = maxRange / dist * RCS^0.25 reaches
        /// minSignal at maxRange / minSignal. 0 when the radar has no range data.
        /// </summary>
        public static float GetVanillaReferenceRange(float maxRange, float minSignal)
        {
            if (maxRange <= 0.0f) return 0.0f;
            return (minSignal > 0.001f) ? (maxRange / minSignal) : (maxRange * 2.0f);
        }

        /// <summary>
        /// Sets the peak power so that a 1 m² target gives CalibrationSNR_dB at referenceRangeMeters
        /// (radar equation with the template hardware and, with AtmosphericLossEnabled, two-way atmospheric loss;
        /// RadarPowerFactor not included). Either way the radar keeps its vanilla range.
        /// </summary>
        public static void CalibrateToRange(ref RadarSpecs specs, float referenceRangeMeters)
        {
            if (referenceRangeMeters <= 0.0f) return;

            double r = referenceRangeMeters;
            double noise = RadarPhysics.CalculateThermalNoiseFloor(specs.BandwidthHz, specs.NoiseFigureLinear);
            double requiredPower = noise * Math.Pow(10.0, CalibrationSNR_dB / 10.0);
            bool applyAtmosphere = CnoEvilConfig.AtmosphericLossEnabled == null || CnoEvilConfig.AtmosphericLossEnabled.Value;
            double atmosphere = applyAtmosphere ? Math.Pow(10.0, -specs.AttrCoeff_dB_per_km * 2.0 * r * 0.001 * 0.1) : 1.0;
            double echoPerWatt = (double)specs.AntennaGainLinear * specs.AntennaGainLinear *
                                 specs.WavelengthMeters * specs.WavelengthMeters *
                                 Math.Max(1.0f, specs.ProcessingGainLinear) * atmosphere /
                                 (RadarPhysics.FourPiCubed * r * r * r * r * specs.SystemLossLinear);
            if (echoPerWatt <= 0.0) return;

            specs.PeakPowerWatts = (float)(requiredPower / echoPerWatt);
        }

        public static RadarSpecs GetRadarSpecsForPlatform(Unit attachedUnit, Radar radar)
        {
            RadarTemplate template;
            float referenceRange;
            return GetRadarSpecsForPlatform(attachedUnit, radar, out template, out referenceRange);
        }

        /// <summary>
        /// Radar hardware from the unit type (or a PlatformOverrides entry), peak power scaled to the vanilla
        /// range of the radar. Missile seekers and units without radar data keep the template range.
        /// </summary>
        public static RadarSpecs GetRadarSpecsForPlatform(Unit attachedUnit, Radar radar,
            out RadarTemplate template, out float referenceRange)
        {
            template = GetTemplateForUnit(attachedUnit);
            referenceRange = 0.0f;
            if (radar != null && !(attachedUnit is Missile))
            {
                referenceRange = GetVanillaReferenceRange(radar.RadarParameters.maxRange, radar.RadarParameters.minSignal);
            }

            PlatformOverride entry;
            if (PlatformOverrides.TryGet(attachedUnit, out entry))
            {
                if (entry.Radar != RadarTemplate.FromType) template = entry.Radar;
                if (entry.RadarRangeMeters > 0.0f) referenceRange = entry.RadarRangeMeters;
            }

            RadarSpecs specs = CreateTemplate(template);
            CalibrateToRange(ref specs, referenceRange);
            return specs;
        }

        /// <summary>
        /// Specs for a call that has only vanilla RadarParams: X band pulse-Doppler hardware at the vanilla range.
        /// </summary>
        public static RadarSpecs CreateFromVanilla(float maxRange, float maxSignal, float minSignal)
        {
            RadarSpecs specs = CreateStandard4thGen();
            CalibrateToRange(ref specs, GetVanillaReferenceRange(maxRange, minSignal));
            return specs;
        }
    }

    public static class RadarPhysics
    {
        public static RadarSpecs GetRadarSpecsForPlatform(Unit attachedUnit, Radar radar)
        {
            return RadarSpecs.GetRadarSpecsForPlatform(attachedUnit, radar);
        }

        // Physics constants
        public const float BoltzmannConstant = 1.380649e-23f; // J/K
        public const float StandardTemperatureK = 290.0f;     // T0 (Kelvin)
        public const float SpeedOfLight = 299792458.0f;        // c (m/s)
        public const float FourPiCubed = 1984.4017f;          // (4 * pi)^3
        public const float LandClutterGamma = 0.03f;          // Average land backscatter (m2/m2)
        public const float SeaClutterGamma = 0.001f;          // Sea backscatter, about -30 dB (sea state 3, low grazing)

        // 4/3 Earth refraction constants
        // Real Earth radius RE ≈ 6,371,000 m. With k = 4/3 refraction:
        // k * RE ≈ 8,494,667 m. 2 * k * RE ≈ 16,989,333 m
        public const float EarthRadiusMeters = 6371000.0f;
        public const float RefractionK = 4.0f / 3.0f;
        public const float EffectiveEarthRadiusMeters = EarthRadiusMeters * RefractionK;
        public const float TwoEffectiveRadius = 2.0f * EffectiveEarthRadiusMeters; // ~1.698933e7

        /// <summary>
        /// Receiver thermal noise floor Pn = k * T0 * B * F (Watts)
        /// </summary>
        public static float CalculateThermalNoiseFloor(float bandwidthHz, float noiseFigureLinear)
        {
            if (bandwidthHz <= 0.0f) bandwidthHz = 5.0e6f;
            if (noiseFigureLinear < 1.0f) noiseFigureLinear = 1.0f;
            return BoltzmannConstant * StandardTemperatureK * bandwidthHz * noiseFigureLinear;
        }

        /// <summary>
        /// Monostatic Radar Range Equation for received echo power Pr (Watts)
        /// Pr = (Pt * Gt * Gr * λ² * σ) / ((4π)³ * R⁴ * Lsys) * 10^(-2 * γ * R / 10000)
        /// </summary>
        public static float CalculateEchoPower(
            float rangeMeters,
            float targetRcsSqm,
            ref RadarSpecs specs)
        {
            if (rangeMeters < 10.0f) rangeMeters = 10.0f;
            if (targetRcsSqm <= 0.0001f) targetRcsSqm = 0.0001f;

            float r2 = rangeMeters * rangeMeters;
            float r4 = r2 * r2;

            float numerator = specs.PeakPowerWatts * CnoEvilConfig.RadarPowerFactor * 
                              (specs.AntennaGainLinear * specs.AntennaGainLinear) * 
                              (specs.WavelengthMeters * specs.WavelengthMeters) * 
                              targetRcsSqm;

            float denominator = FourPiCubed * r4 * specs.SystemLossLinear;
            if (denominator <= 0.0f) return 0.0f;

            float pr = (numerator / denominator) * Mathf.Max(1.0f, specs.ProcessingGainLinear);

            // Two-way atmospheric attenuation
            pr *= CalculateAtmosphericLossLinear(2.0f * rangeMeters, specs.AttrCoeff_dB_per_km);

            return pr;
        }

        /// <summary>
        /// Ground clutter power Pc (Watts) for a look-down beam. The clutter patch is a range cell
        /// (c / 2B) times the beam width at range, with sigma0 = gamma * sin(grazing). Its echo uses the
        /// same range equation as a target. The patch is where the beam meets the ground behind the target:
        /// R_g = max(R, h / sin(grazing)) with h the radar height above the terrain (h &lt;= 0: R_g = R).
        /// Returns 0 when the beam does not point below the horizon. This is the main-lobe clutter before
        /// Doppler filtering (see DopplerClutterProcessor.ClutterDopplerSuppression). seaSurface: the patch is sea
        /// (target is a ship), gamma = SeaClutterGamma.
        /// </summary>
        public static float CalculateClutterPower(float rangeMeters, float elevationDeg, float radarHeightAgl, ref RadarSpecs specs, bool seaSurface = false)
        {
            float groundRange;
            float clutterRcs = CalculateClutterRcs(rangeMeters, elevationDeg, radarHeightAgl, specs.AntennaGainLinear, specs.BandwidthHz, out groundRange, seaSurface);
            if (clutterRcs <= 0.0f) return 0.0f;

            return CalculateEchoPower(groundRange, clutterRcs, ref specs);
        }

        /// <summary>
        /// Clutter RCS sigma_c (m²) of the main-lobe ground patch seen by a receiver (see CalculateClutterPower), and
        /// the range of the patch R_g. Returns 0 (R_g = range) when the line of sight does not point below the horizon.
        /// </summary>
        public static float CalculateClutterRcs(float rangeMeters, float elevationDeg, float heightAgl, float antennaGainLinear, float bandwidthHz, out float groundRange, bool seaSurface = false)
        {
            groundRange = Mathf.Max(10.0f, rangeMeters);
            if (elevationDeg >= 0.0f || antennaGainLinear <= 1.0f || bandwidthHz <= 0.0f) return 0.0f;

            float grazingRad = Mathf.Min(-elevationDeg, 89.0f) * Mathf.Deg2Rad;
            float sinGrazing = Mathf.Sin(grazingRad);
            if (heightAgl > 0.0f) groundRange = Mathf.Max(groundRange, heightAgl / sinGrazing);

            float sigma0 = (seaSurface ? SeaClutterGamma : LandClutterGamma) * sinGrazing;
            float beamWidthRad = Mathf.Sqrt(4.0f * Mathf.PI / antennaGainLinear);
            float rangeCell = SpeedOfLight / (2.0f * bandwidthHz);
            float patchArea = rangeCell * groundRange * beamWidthRad / Mathf.Cos(grazingRad);

            return sigma0 * patchArea;
        }

        /// <summary>
        /// Atmospheric attenuation factor (linear, 0..1) over a total propagation path length.
        /// Pass 2R for a monostatic round trip, or r1 + r2 for a bistatic path.
        /// Returns 1.0 when AtmosphericLossEnabled is off.
        /// </summary>
        public static float CalculateAtmosphericLossLinear(float pathLengthMeters, float attenuation_dB_per_km)
        {
            bool applyAtmosphere = CnoEvilConfig.AtmosphericLossEnabled == null || CnoEvilConfig.AtmosphericLossEnabled.Value;
            if (!applyAtmosphere || attenuation_dB_per_km <= 0.0f || pathLengthMeters <= 0.0f) return 1.0f;

            float lossDb = attenuation_dB_per_km * pathLengthMeters * 0.001f;
            return Mathf.Pow(10.0f, -lossDb * 0.1f);
        }

        /// <summary>
        /// Instantaneous Signal-to-Interference-plus-Noise Ratio in Decibels
        /// SNR_dB = 10 * log10(Pr / (Pn + Pc + Pj))
        /// </summary>
        public static float CalculateSNRdB(float echoPowerPr, float noiseFloorPn, float clutterPowerPc, float jammerPowerPj)
        {
            float totalInterference = noiseFloorPn + clutterPowerPc + jammerPowerPj;
            if (totalInterference <= 1e-25f) totalInterference = 1e-25f;

            if (echoPowerPr <= 1e-26f) return -50.0f;

            float snrLinear = echoPowerPr / totalInterference;
            if (snrLinear <= 1e-5f) return -50.0f;

            return 10.0f * Mathf.Log10(snrLinear);
        }

        /// <summary>
        /// Maximum line-of-sight radar horizon distance d_horiz = sqrt(2*k*RE*h1) + sqrt(2*k*RE*h2) in meters.
        /// Strictly evaluated in meters.
        /// For h1 = 3,000m and h2 = 3,000m: d1 ≈ 225.8 km, d2 ≈ 225.8 km, total horizon ≈ 451.5 km.
        /// </summary>
        public static float CalculateRadarHorizonDistance(float radarAltitudeMeters, float targetAltitudeMeters)
        {
            float h1 = Mathf.Max(0.0f, radarAltitudeMeters);
            float h2 = Mathf.Max(0.0f, targetAltitudeMeters);

            float d1 = Mathf.Sqrt(TwoEffectiveRadius * h1);
            float d2 = Mathf.Sqrt(TwoEffectiveRadius * h2);
            return d1 + d2;
        }

        // Vanilla DetectorManager.RequestRadarCheck horizon constant: 2 * EARTH_RADIUS (k = 1)
        public const float VanillaTwoEarthRadius = 2.0f * EarthRadiusMeters;

        // Largest chord error (m) between the bent radar path and its straight linecast segments
        private const float PathSagToleranceMeters = 15.0f;
        private const int MaxPathSegments = 8;

        private static bool CurvatureEnabled
        {
            get { return CnoEvilConfig.EarthCurvatureEnabled == null || CnoEvilConfig.EarthCurvatureEnabled.Value; }
        }

        /// <summary>
        /// 2 * Earth radius for the vanilla horizon gate in DetectorManager.RequestRadarCheck (transpiler):
        /// 2 * k * RE with 4/3 refraction while the mod is active, the vanilla 2 * RE otherwise.
        /// </summary>
        public static float HorizonTwoRadius()
        {
            return CnoEvilConfig.IsModActive && CurvatureEnabled ? TwoEffectiveRadius : VanillaTwoEarthRadius;
        }

        /// <summary>
        /// Radar line of sight over a curved Earth with 4/3 refraction and the real terrain along the path.
        /// The map is flat, so the refracted ray (straight over a sphere of radius k * RE) sags below the chord by
        /// s(x) = x * (d - x) / (2 * k * RE). The bent path is checked against the sea surface (analytic, equal to
        /// sqrt(2kRE h1) + sqrt(2kRE h2) over open sea) and against terrain and ships with linecast segments.
        /// byCurvature is true when the Earth bulge closes a path that the straight line would clear; the straight
        /// line is cast for this label only when classify is true. With EarthCurvatureEnabled off, only the straight
        /// line is checked.
        /// </summary>
        public static bool IsRadarPathBlocked(Vector3 from, Vector3 to, float targetClearRadius, bool classify, out bool byCurvature)
        {
            byCurvature = false;

            Vector3 los = to - from;
            float losDist = los.magnitude;
            if (losDist <= 5.0f) return false;

            Vector3 dir = los / losDist;
            Vector3 start = from + dir * 2.0f;
            Vector3 end = to - dir * 1.0f;
            float clearSqr = targetClearRadius * targetClearRadius;

            if (!CurvatureEnabled)
            {
                return IsSegmentBlocked(start, end, to, clearSqr);
            }

            float dx = to.x - from.x;
            float dz = to.z - from.z;
            float d2 = dx * dx + dz * dz;

            // Sea surface: path height above sea h(t) = hA + (hB - hA) t - a t (1 - t), a = d^2 / (2 k RE)
            float seaY = Datum.LocalSeaY;
            float hA = Mathf.Max(1.0f, from.y - seaY);
            float hB = Mathf.Max(1.0f, to.y - seaY);
            float a = d2 / TwoEffectiveRadius;
            if (a > 0.0f)
            {
                float tMin = Mathf.Clamp01((a - (hB - hA)) / (2.0f * a));
                float hMin = hA + (hB - hA) * tMin - a * tMin * (1.0f - tMin);
                if (hMin < 0.0f)
                {
                    byCurvature = true;
                    return true;
                }
            }

            // Terrain and ships: the bent path as straight segments, chord error <= PathSagToleranceMeters
            float sagMax = a * 0.25f;
            int n = Mathf.Clamp(Mathf.CeilToInt(Mathf.Sqrt(sagMax / PathSagToleranceMeters)), 1, MaxPathSegments);

            bool blocked = false;
            Vector3 prev = start;
            for (int i = 1; i <= n; i++)
            {
                float t = (float)i / n;
                Vector3 next = Vector3.Lerp(start, end, t);
                next.y -= a * t * (1.0f - t);
                if (IsSegmentBlocked(prev, next, to, clearSqr))
                {
                    blocked = true;
                    break;
                }
                prev = next;
            }

            if (!blocked) return false;

            if (n > 1 && classify)
            {
                byCurvature = !IsSegmentBlocked(start, end, to, clearSqr);
            }
            return true;
        }

        private static bool IsSegmentBlocked(Vector3 a, Vector3 b, Vector3 targetPos, float clearSqr)
        {
            RaycastHit hit;
            int obstacleMask = (int)PhysicsLayers.StaticsMask | (int)PhysicsLayers.ShipsMask;
            if (!Physics.Linecast(a, b, out hit, obstacleMask)) return false;
            return hit.collider != null && (hit.point - targetPos).sqrMagnitude > clearSqr;
        }
    }
}
