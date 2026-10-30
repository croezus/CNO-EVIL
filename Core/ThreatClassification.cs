namespace CnoEvil.Core
{
    /// <summary>
    /// Which units the mod keeps tracks on (passive fixes, tracking quality Q, HUD tint, AI launch doctrine),
    /// identified the way the game does: by Unit subclass and the vanilla definition types
    /// (VehicleDefinition.vehicleType, BuildingDefinition.buildingType) that pick HUD and map icons.
    /// </summary>
    public static class ThreatClassification
    {
        /// <summary>
        /// Aircraft, ships, air-defense vehicles (AAA, IR SAM, radar SAM, radar vehicle), radar buildings,
        /// and any other unit that carries a radar. Missiles are never threats. With a friendlyHq only units of another
        /// faction are threats: units of friendlyHq and units of no faction (neutral map radars) are not.
        /// With a null friendlyHq the test is by type only.
        /// </summary>
        public static bool IsTrackedThreat(Unit unit, FactionHQ friendlyHq)
        {
            if (unit == null || unit.disabled || unit is Missile) return false;
            if (friendlyHq != null)
            {
                FactionHQ unitHq = DatalinkNetwork.GetUnitFactionHQ(unit);
                if (unitHq == null || unitHq == friendlyHq) return false;
            }

            if (unit.radar is Radar) return true;
            if (unit is Aircraft || unit is Ship) return true;

            if (unit is GroundVehicle)
            {
                VehicleDefinition vehicle = unit.definition as VehicleDefinition;
                if (vehicle == null) return false;
                switch (vehicle.vehicleType)
                {
                    case VehicleType.AAA:
                    case VehicleType.IR_SAM:
                    case VehicleType.R_SAM:
                    case VehicleType.RDR:
                        return true;
                    default:
                        return false;
                }
            }

            if (unit is Building)
            {
                BuildingDefinition building = unit.definition as BuildingDefinition;
                return building != null && building.buildingType == BuildingType.RDR;
            }

            return false;
        }

        /// <summary>
        /// Units the portal TSD page shows: aircraft, radar SAMs, radar vehicles, radar buildings and ships with a radar.
        /// AAA, IR SAMs, other ground units and ships without a radar stay off the page.
        /// </summary>
        public static bool IsTsdContact(Unit unit)
        {
            if (unit == null) return false;
            if (unit is Aircraft) return true;
            if (unit is Ship) return unit.radar is Radar;

            if (unit is GroundVehicle)
            {
                VehicleDefinition vehicle = unit.definition as VehicleDefinition;
                return vehicle != null && (vehicle.vehicleType == VehicleType.R_SAM || vehicle.vehicleType == VehicleType.RDR);
            }

            if (unit is Building)
            {
                BuildingDefinition building = unit.definition as BuildingDefinition;
                return building != null && building.buildingType == BuildingType.RDR;
            }

            return false;
        }

        /// <summary>
        /// Mission structures the game marks as strategic targets (UnitDefinition.typeIdentity.strategic > 0, the same
        /// test that fills vanilla FactionHQ.strategicTargets): factories, hangars, shelters, depots, ammo dumps,
        /// helipads, refineries. Only faction-owned buildings that are not of friendlyHq count.
        /// </summary>
        public static bool IsStrategicBuilding(Unit unit, FactionHQ friendlyHq)
        {
            if (unit == null || unit.disabled || !(unit is Building)) return false;
            if (unit.definition == null || unit.definition.typeIdentity.strategic <= 0.0f) return false;
            // Buildings carry no radar, so the HQ radar-list fallback of GetUnitFactionHQ is not needed
            FactionHQ hq = (unit.NetworkHQ != null) ? unit.NetworkHQ : unit.MapHQ;
            return hq != null && hq != friendlyHq;
        }

        /// <summary>
        /// Units the faction keeps a tracking quality Q track on: tracked threats and strategic buildings.
        /// Map icons, AI launch doctrine and the datalink use IsTrackedThreat only.
        /// </summary>
        public static bool HasQualityTrack(Unit unit, FactionHQ friendlyHq)
        {
            return IsTrackedThreat(unit, friendlyHq) || IsStrategicBuilding(unit, friendlyHq);
        }
    }
}
