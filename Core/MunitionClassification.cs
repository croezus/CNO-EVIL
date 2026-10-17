using System;
using System.Collections.Generic;
using UnityEngine;
using CnoEvil.Config;

namespace CnoEvil.Core
{
    /// <summary>
    /// Munition properties from game data for AI launch doctrine: how far the aim point may be off and the
    /// weapon still kills the target (seeker acquisition, warhead danger radius).
    /// </summary>
    public static class MunitionClassification
    {
        public static void ClearCache()
        {
            s_seekerBasketCache.Clear();
            s_guidedArtilleryCache.Clear();
        }

        private static readonly Dictionary<WeaponInfo, bool> s_guidedArtilleryCache = new Dictionary<WeaponInfo, bool>();
        private static readonly Func<Gun, MissileDefinition> f_gunGuidedProjectile = FastReflection.CreateFieldGetter<Gun, MissileDefinition>("guidedProjectile");

        /// <summary>
        /// Guided artillery round: an inertial shell or rocket (155 mm shell, MLRS rocket) or a guided naval shell
        /// (76 / 127 mm, optical terminal seeker).
        /// </summary>
        public static bool IsGuidedArtillerySeeker(MissileSeeker seeker)
        {
            return seeker is InertialSeekerShell || seeker is OpticalSeekerShell;
        }

        /// <summary>
        /// Weapon that fires guided artillery rounds: a gun with a guided projectile or a launcher whose missile
        /// has an artillery seeker (IsGuidedArtillerySeeker).
        /// </summary>
        public static bool IsGuidedArtillery(WeaponInfo weaponInfo)
        {
            if (weaponInfo == null) return false;
            bool result;
            if (s_guidedArtilleryCache.TryGetValue(weaponInfo, out result)) return result;

            result = false;
            GameObject prefab = weaponInfo.weaponPrefab;
            if (prefab != null)
            {
                MissileDefinition projectile = null;
                Gun gun = prefab.GetComponentInChildren<Gun>(true);
                if (gun != null && f_gunGuidedProjectile != null) projectile = f_gunGuidedProjectile(gun);
                if (projectile == null)
                {
                    MissileLauncher launcher = prefab.GetComponentInChildren<MissileLauncher>(true);
                    if (launcher != null) projectile = launcher.missile;
                }

                GameObject round = (projectile != null && projectile.unitPrefab != null) ? projectile.unitPrefab : prefab;
                result = IsGuidedArtillerySeeker(round.GetComponentInChildren<MissileSeeker>(true));
            }
            s_guidedArtilleryCache[weaponInfo] = result;
            return result;
        }

        private struct SeekerBasket
        {
            public bool HomesOnEmission;   // ARM: accepts emitters within AcceptanceAngleDeg of its heading
            public float AcceptanceAngleDeg;
            public float FixedRadius;      // ARH / optical: search basket around the datum
            public float LethalRadius;     // Warhead danger radius
        }

        private static readonly Dictionary<WeaponInfo, SeekerBasket> s_seekerBasketCache = new Dictionary<WeaponInfo, SeekerBasket>();
        private static readonly Func<ARMSeeker, float> f_armMaxTargetAngle = FastReflection.CreateFieldGetter<ARMSeeker, float>("maxTargetAngle");
        private static readonly Func<ARHSeeker, float> f_arhTerminalRange = FastReflection.CreateFieldGetter<ARHSeeker, float>("terminalRange");
        private static readonly Func<ARHSeeker, float> f_arhMaxTrackingAngle = FastReflection.CreateFieldGetter<ARHSeeker, float>("maxTrackingAngle");
        private static readonly Func<OpticalSeeker, float> f_opticalSearchRadius = FastReflection.CreateFieldGetter<OpticalSeeker, float>("searchRadius");
        private static readonly Func<OpticalSeekerBomb, float> f_bombSearchRadius = FastReflection.CreateFieldGetter<OpticalSeekerBomb, float>("searchRadius");
        private static readonly Func<OpticalSeekerCruiseMissile, float> f_cruiseSearchRadius = FastReflection.CreateFieldGetter<OpticalSeekerCruiseMissile, float>("terminalSearchRadius");

        // Vanilla Missile.CheckExclusionZone: danger radius = 13 * blastYield^(1/3)
        private const float VanillaDangerRadiusPerCubeRootYield = 13.0f;

        // Vanilla AIPilotCombatModes.ManageTarget accuracy requirement for every attack mode
        public const float VanillaTargetAccuracy = 100.0f;

        /// <summary>
        /// How far the launcher's aim point may be off and the weapon still kills the target: the largest of
        /// the vanilla 100 m accuracy, the seeker acquisition radius and the warhead danger radius.
        /// Seeker acquisition: an anti-radiation seeker accepts emitters within maxTargetAngle of its heading
        /// (launchDistance * tan), an active radar seeker searches maxTrackingAngle around its datum when it goes
        /// active at terminalRange, an optical seeker searches searchRadius (cruise missile: terminalSearchRadius).
        /// IR / SARH / laser / ballistic / unguided weapons acquire nothing by themselves: for them (for example the
        /// Piledriver TBM) the limit is the warhead danger radius, so the AI may launch when the target is inside the blast.
        /// </summary>
        public static float GetAcceptableAimError(WeaponInfo weaponInfo, float launchDistance)
        {
            if (weaponInfo == null) return VanillaTargetAccuracy;

            SeekerBasket basket;
            if (!s_seekerBasketCache.TryGetValue(weaponInfo, out basket))
            {
                basket = EvaluateSeekerBasket(weaponInfo);
                s_seekerBasketCache[weaponInfo] = basket;
            }

            float seekerRadius = basket.FixedRadius;
            if (basket.HomesOnEmission)
            {
                seekerRadius = Mathf.Max(0.0f, launchDistance) * Mathf.Tan(Mathf.Clamp(basket.AcceptanceAngleDeg, 0.0f, 80.0f) * Mathf.Deg2Rad);
            }
            return Mathf.Max(VanillaTargetAccuracy, Mathf.Max(seekerRadius, basket.LethalRadius));
        }

        private static SeekerBasket EvaluateSeekerBasket(WeaponInfo weaponInfo)
        {
            SeekerBasket basket = new SeekerBasket();
            if (!weaponInfo.missile || weaponInfo.weaponPrefab == null) return basket;

            Missile missile = weaponInfo.weaponPrefab.GetComponentInChildren<Missile>(true);
            if (missile != null)
            {
                basket.LethalRadius = VanillaDangerRadiusPerCubeRootYield * Mathf.Pow(Mathf.Max(0.0f, missile.GetYield()), 1.0f / 3.0f);
            }

            MissileSeeker seeker = weaponInfo.weaponPrefab.GetComponentInChildren<MissileSeeker>(true);
            ARMSeeker arm = seeker as ARMSeeker;
            ARHSeeker arh = seeker as ARHSeeker;
            OpticalSeeker optical = seeker as OpticalSeeker;
            OpticalSeekerBomb bomb = seeker as OpticalSeekerBomb;
            OpticalSeekerCruiseMissile cruise = seeker as OpticalSeekerCruiseMissile;
            if (arm != null && f_armMaxTargetAngle != null)
            {
                basket.HomesOnEmission = true;
                basket.AcceptanceAngleDeg = f_armMaxTargetAngle(arm);
            }
            else if (arh != null && f_arhTerminalRange != null && f_arhMaxTrackingAngle != null)
            {
                float angle = Mathf.Clamp(f_arhMaxTrackingAngle(arh), 0.0f, 80.0f);
                basket.FixedRadius = Mathf.Max(0.0f, f_arhTerminalRange(arh)) * Mathf.Tan(angle * Mathf.Deg2Rad);
            }
            else if (optical != null && f_opticalSearchRadius != null)
            {
                basket.FixedRadius = Mathf.Max(0.0f, f_opticalSearchRadius(optical));
            }
            else if (bomb != null && f_bombSearchRadius != null)
            {
                basket.FixedRadius = Mathf.Max(0.0f, f_bombSearchRadius(bomb));
            }
            else if (cruise != null && f_cruiseSearchRadius != null)
            {
                basket.FixedRadius = Mathf.Max(0.0f, f_cruiseSearchRadius(cruise));
            }

            if (CnoEvilConfig.DebugLogging != null && CnoEvilConfig.DebugLogging.Value && CnoEvilPlugin.Log != null)
            {
                CnoEvilPlugin.Log.LogInfo(string.Format("[CNO-EVIL] Seeker basket {0}: ARM={1} angle={2:F1} deg, search radius={3:F0} m, danger radius={4:F0} m",
                    weaponInfo.weaponName, basket.HomesOnEmission, basket.AcceptanceAngleDeg, basket.FixedRadius, basket.LethalRadius));
            }
            return basket;
        }

        /// <summary>
        /// Electronic warfare aircraft (e.g. EW-25 Medusa): an aircraft that carries a vanilla jammer weapon
        /// (WeaponInfo.jammer). Used for the datalink node role label.
        /// </summary>
        public static bool HasJammerWeapon(Aircraft aircraft)
        {
            if (aircraft == null || aircraft.weaponStations == null) return false;
            for (int i = 0; i < aircraft.weaponStations.Count; i++)
            {
                WeaponStation st = aircraft.weaponStations[i];
                if (st != null && st.WeaponInfo != null && st.WeaponInfo.jammer) return true;
            }
            return false;
        }

    }
}
