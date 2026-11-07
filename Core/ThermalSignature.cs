using UnityEngine;
using CnoEvil.Config;

namespace CnoEvil.Core
{
    /// <summary>
    /// Heat of ground vehicles and ships, which register no IR sources in vanilla (only engines and flares do).
    /// Read by the HMD thermal window (hot spots, IRST) and by the host check of IRST bearings, so both aim at the same point.
    /// </summary>
    public static class ThermalSignature
    {
        // Above this speed (m/s) a vehicle runs its engine at drive power, below it at idle
        private const float MovingSpeed = 1.0f;
        private const float IdleFraction = 0.5f;
        // Same bound as IrstSync.MaxIntensity, so the host range check holds
        private const float MaxIntensity = 2.0f;

        public static bool IsGroundHeatTarget(Unit unit)
        {
            return unit != null && !unit.disabled && (unit is GroundVehicle || unit is Ship);
        }

        /// <summary>Middle of the hull for ground vehicles and ships (the transform is at the ground), else the transform.</summary>
        public static Vector3 GetHeatPoint(Unit unit)
        {
            Transform t = unit.transform;
            if (!(unit is GroundVehicle || unit is Ship) || unit.definition == null) return t.position;
            return t.position + t.up * Mathf.Max(1.0f, unit.definition.height * 0.5f);
        }

        public static float GetHeatIntensity(Unit unit)
        {
            float heat;
            if (unit is Ship)
            {
                heat = CnoEvilConfig.HMDThermalShipHeat.Value;
            }
            else
            {
                heat = CnoEvilConfig.HMDThermalGroundHeat.Value;
                if (unit.speed < MovingSpeed) heat *= IdleFraction;
            }
            return Mathf.Clamp(heat, 0.0f, MaxIntensity);
        }

        /// <summary>Apparent radius (m) of the hot spot: half the larger hull dimension.</summary>
        public static float GetHeatRadius(Unit unit)
        {
            if (unit.definition == null) return 2.0f;
            return Mathf.Max(2.0f, 0.5f * Mathf.Max(unit.definition.length, unit.definition.width));
        }
    }
}
