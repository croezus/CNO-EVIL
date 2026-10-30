using System;
using System.Collections.Generic;
using UnityEngine;
using CnoEvil.Core;
using CnoEvil.Config;

namespace CnoEvil.Components
{
    public class PhysRadarTarget : MonoBehaviour
    {
        private static readonly Dictionary<Unit, PhysRadarTarget> s_registry = new Dictionary<Unit, PhysRadarTarget>();

        public static PhysRadarTarget Get(Unit unit)
        {
            if (unit == null) return null;
            PhysRadarTarget target;
            if (!s_registry.TryGetValue(unit, out target))
            {
                target = unit.GetComponent<PhysRadarTarget>();
                if (target != null) s_registry[unit] = target;
            }
            return target;
        }

        public static void ClearAll()
        {
            s_registry.Clear();
        }

        public Unit AttachedUnit;
        public Aircraft AttachedAircraft;
        public Missile AttachedMissile;
        public AspectRCSProfile Profile;

        private float _lastStoreCheckTime = 0.0f;
        private float _cachedStoresPenalty = 0.0f;

        public float CachedStoresPenalty
        {
            get { return _cachedStoresPenalty; }
        }

        private void Awake()
        {
            AttachedUnit = GetComponent<Unit>();
            if (AttachedUnit != null) s_registry[AttachedUnit] = this;
            AttachedAircraft = GetComponent<Aircraft>();
            AttachedMissile = GetComponent<Missile>();

            InitializeProfile();
        }

        private void OnDestroy()
        {
            if (AttachedUnit != null) s_registry.Remove(AttachedUnit);
        }

        public void InitializeProfile()
        {
            // Clean airframe RCS from the unit definition. Unit.RCS also accumulates vanilla mount RCS
            // (Hardpoint.ModifyRCS), which CalculateStoresRCSPenalty already models.
            float baseRcs = 5.0f;
            if (AttachedUnit != null)
            {
                baseRcs = (AttachedUnit.definition != null && AttachedUnit.definition.radarSize > 0.0f)
                    ? AttachedUnit.definition.radarSize
                    : AttachedUnit.RCS;
                baseRcs = Mathf.Max(0.0002f, baseRcs);
            }

            if (AttachedMissile != null)
            {
                Profile = AspectRCSProfile.CreateMissile(baseRcs);
            }
            else if (AttachedAircraft != null)
            {
                // Swashplate rotor heads exist only on helicopters (SAH-46 Chicane, UH-90 Ibis)
                bool isHeli = GetComponentInChildren<SwashRotor>(true) != null;

                if (isHeli)
                {
                    Profile = AspectRCSProfile.CreateHelicopter(baseRcs);
                }
                else if (IsLargeAirframe(AttachedUnit.definition))
                {
                    // Large airframe (VL-49 Tarantula); stealth flying wings (SFB-81, AB-4) get the stealth multipliers by blend
                    Profile = AspectRCSProfile.CreateLargeAircraft(baseRcs);
                }
                else
                {
                    Profile = AspectRCSProfile.CreateFighter(baseRcs);
                }
            }
            else
            {
                InitializeSurfaceProfile(baseRcs);
            }
        }

        // Game airframe sizes: fighters 13-19 m long / 9-19 m span; VL-49 28.5 x 22.7 m
        private const float LargeAirframeLengthMeters = 24.0f;
        private const float LargeAirframeSpanMeters = 22.0f;

        private static bool IsLargeAirframe(UnitDefinition def)
        {
            return def != null && (def.length >= LargeAirframeLengthMeters || def.width >= LargeAirframeSpanMeters);
        }

        /// <summary>
        /// Ships, vehicles and structures. The game gives every ship radarSize 1.0, from a 26 m landing craft
        /// to a 267 m carrier; ship broadside RCS comes from real-world analogs (ShipRcsDatabase).
        /// Vehicles and buildings keep the game radarSize as their mean return.
        /// </summary>
        private void InitializeSurfaceProfile(float baseRcs)
        {
            UnitDefinition def = AttachedUnit != null ? AttachedUnit.definition : null;
            float length = def != null ? def.length : 0.0f;
            float width = def != null ? def.width : 0.0f;
            float height = def != null ? def.height : 0.0f;

            float broadside;
            if (AttachedUnit is Ship)
            {
                broadside = ShipRcsDatabase.GetBroadsideRcs(def);
            }
            else
            {
                // Keep the mean of the bow and broadside returns at the game radarSize
                float bowRatio = (length > 0.1f && width > 0.1f) ? Mathf.Max(0.2f, width / length) : 0.5f;
                broadside = baseRcs * 2.0f / (1.0f + bowRatio);
            }

            Profile = AspectRCSProfile.CreateSurface(Mathf.Max(0.0002f, broadside), length, width, height);
        }

        public float GetDynamicRCS(Vector3 radarPosition)
        {
            float rcs = AspectRCS.CalculateAspectRCS(transform, radarPosition, ref Profile);

            // Rotor blade flash (HERM) adds to the hull return of helicopters
            if (IsHelicopterWithHERM())
            {
                rcs += AspectRCS.GetHelicopterRotorFlash(ref Profile);
            }

            return rcs + GetStoresRCS();
        }

        /// <summary>
        /// RCS seen by a radar dead ahead (nose aspect, level): airframe, rotor flash, stores and open bays.
        /// </summary>
        public float GetFrontalRCS()
        {
            return Profile.NoseRCS + GetRotorFlashRCS() + GetStoresRCS();
        }

        private float GetStoresRCS()
        {
            if (!CnoEvilConfig.ExternalStoresRCSEnabled.Value) return 0.0f;
            if (Time.time - _lastStoreCheckTime > 0.25f)
            {
                _cachedStoresPenalty = AspectRCS.CalculateStoresRCSPenalty(AttachedUnit);
                _lastStoreCheckTime = Time.time;
            }
            return _cachedStoresPenalty;
        }

        /// <summary>
        /// RCS of the rotor blade flash (HERM) alone, 0 when the target has no HERM.
        /// </summary>
        public float GetRotorFlashRCS()
        {
            return IsHelicopterWithHERM() ? AspectRCS.GetHelicopterRotorFlash(ref Profile) : 0.0f;
        }

        public bool IsHelicopterWithHERM()
        {
            if (!CnoEvilConfig.HelicopterRotorModulationEnabled.Value)
            {
                return false;
            }
            return Profile.IsHelicopter;
        }
    }
}
