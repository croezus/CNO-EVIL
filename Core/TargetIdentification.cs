using System.Collections.Generic;
using UnityEngine;
using CnoEvil.Config;

namespace CnoEvil.Core
{
    public enum IdentTier
    {
        Low,
        Mid,
        High
    }

    /// <summary>
    /// Progressive target identification by the faction's tracking quality Q, for the HMD target info
    /// and the target camera screen.
    /// Low: domain only (AIR? / GRND? / SHIP?). Mid: class (FTR, SAM, SHIP...). High: the vanilla designation.
    /// Classes come from vanilla types: UnitDefinition.roleIdentity, VehicleType, BuildingType, ShipType.
    /// </summary>
    public static class TargetIdentification
    {
        private static readonly Dictionary<UnitDefinition, bool> s_rotorcraft = new Dictionary<UnitDefinition, bool>();

        /// <summary>
        /// High with a recent radar / visual contact, otherwise by Q against IdentMidQuality / IdentHighQuality.
        /// </summary>
        public static IdentTier GetTier(TriangulationTrack track)
        {
            if (track == null) return IdentTier.Low;
            if (track.HasRecentPreciseContact) return IdentTier.High;

            float midSetting = CnoEvilConfig.IdentMidQuality.Value;
            float highSetting = CnoEvilConfig.IdentHighQuality.Value;
            // Swapped settings still give three tiers
            float mid = Mathf.Min(midSetting, highSetting);
            float high = Mathf.Max(midSetting, highSetting);
            float q = track.TrackingQuality;
            if (q >= high) return IdentTier.High;
            if (q >= mid) return IdentTier.Mid;
            return IdentTier.Low;
        }

        public static string GetLabel(Unit unit, IdentTier tier)
        {
            if (unit == null) return string.Empty;
            UnitDefinition def = unit.definition;

            if (unit is Aircraft)
            {
                if (tier == IdentTier.Low) return "AIR?";
                if (tier == IdentTier.Mid) return GetAircraftClass(unit);
                return def != null ? def.code : "AIR";
            }

            if (unit is Ship)
            {
                if (tier == IdentTier.Low) return "SHIP?";
                if (tier == IdentTier.Mid) return "SHIP";
                // Vanilla code is "SHP" for every ship, so the hull class is the full identification
                ShipDefinition ship = def as ShipDefinition;
                return ship != null ? ship.shipType.ToString() : "SHIP";
            }

            if (tier == IdentTier.Low) return "GRND?";
            if (tier == IdentTier.Mid) return GetGroundClass(def);
            return def != null ? def.code : "GRND";
        }

        /// <summary>
        /// HELO (swashplate rotor), EW (anti-radar role: the EW-25 Medusa jammer), BMR (pure anti-surface), FTR (anti-air role), else ATK.
        /// </summary>
        private static string GetAircraftClass(Unit unit)
        {
            UnitDefinition def = unit.definition;
            if (def == null) return "AIR";
            if (IsRotorcraft(unit)) return "HELO";

            RoleIdentity role = def.roleIdentity;
            if (role.antiRadar >= 0.8f) return "EW";
            if (role.antiSurface >= 0.95f) return "BMR";
            if (role.antiAir >= 0.9f) return "FTR";
            return "ATK";
        }

        private static string GetGroundClass(UnitDefinition def)
        {
            VehicleDefinition vehicle = def as VehicleDefinition;
            if (vehicle != null)
            {
                switch (vehicle.vehicleType)
                {
                    case VehicleType.AAA: return "AAA";
                    case VehicleType.IR_SAM:
                    case VehicleType.R_SAM: return "SAM";
                    case VehicleType.RDR: return "RDR";
                }
            }

            BuildingDefinition building = def as BuildingDefinition;
            if (building != null) return building.buildingType.ToString(); // FAC, HGR, DEP, AMMO, RDR, ...

            return "GRND";
        }

        private static bool IsRotorcraft(Unit unit)
        {
            UnitDefinition def = unit.definition;
            bool rotor;
            if (s_rotorcraft.TryGetValue(def, out rotor)) return rotor;
            rotor = unit.GetComponentInChildren<SwashRotor>(true) != null;
            s_rotorcraft[def] = rotor;
            return rotor;
        }

        /// <summary>
        /// Coarse tracking quality grade: Q>0, Q>25, Q>50, Q>75; empty at Q = 0 and at full quality.
        /// </summary>
        public static string FormatQuality(float q)
        {
            q = Mathf.Clamp01(q);
            if (q <= 0.0f || q >= 1.0f) return string.Empty;
            if (q > 0.75f) return "Q>75";
            if (q > 0.50f) return "Q>50";
            if (q > 0.25f) return "Q>25";
            return "Q>0";
        }

        /// <summary>
        /// Low: nearest 10 km. Mid: nearest 2 km. High: vanilla reading (exact, player units).
        /// </summary>
        public static string FormatRange(float meters, IdentTier tier)
        {
            if (tier == IdentTier.High) return UnitConverter.DistanceReading(meters);

            float step = (tier == IdentTier.Low) ? 10f : 2f;
            float km = Mathf.Round(meters * 0.001f / step) * step;
            if (km <= 0f) return string.Format("<{0:F0} km", step);
            return string.Format("~{0:F0} km", km);
        }

        /// <summary>
        /// Altitude to the nearest 1000 m (metric) or 5000 ft (imperial), in the vanilla unit style.
        /// Signed adds '+' to positive values (relative altitude).
        /// </summary>
        public static string FormatCoarseAltitude(float meters, bool signed)
        {
            bool imperial = PlayerSettings.unitSystem == PlayerSettings.UnitSystem.Imperial;
            float value = imperial ? meters * 3.28084f : meters;
            float step = imperial ? 5000f : 1000f;
            float rounded = Mathf.Round(value / step) * step;
            string sign = (signed && rounded > 0f) ? "+" : string.Empty;
            return string.Format("~{0}{1:F0}{2}", sign, rounded, imperial ? "ft" : "m");
        }

        /// <summary>
        /// Speed to the nearest 100 km/h (metric) or 50 kt (imperial).
        /// </summary>
        public static string FormatCoarseSpeed(float mps)
        {
            bool imperial = PlayerSettings.unitSystem == PlayerSettings.UnitSystem.Imperial;
            float value = imperial ? mps * 1.94384f : mps * 3.6f;
            float step = imperial ? 50f : 100f;
            return string.Format("~{0:F0}{1}", Mathf.Round(value / step) * step, imperial ? "kt" : "km/h");
        }

        /// <summary>
        /// Heading to the nearest 45 deg ("~045" and a degree sign).
        /// </summary>
        public static string FormatCoarseHeading(float degrees)
        {
            float rounded = Mathf.Repeat(Mathf.Round(degrees / 45f) * 45f, 360f);
            return string.Format("~{0:000}\u00B0", rounded);
        }
    }
}
