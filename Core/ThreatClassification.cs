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
        /// and any other unit that carries a radar. Missiles and units of friendlyHq are never threats.
        /// </summary>
        public static bool IsTrackedThreat(Unit unit, FactionHQ friendlyHq)
        {
            if (unit == null || unit.disabled || unit is Missile) return false;
            if (friendlyHq != null && DatalinkNetwork.GetUnitFactionHQ(unit) == friendlyHq) return false;

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
