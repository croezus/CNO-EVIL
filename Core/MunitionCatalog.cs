using System;
using System.Collections.Generic;
using UnityEngine;
using CnoEvil.Config;

namespace CnoEvil.Core
{
    /// <summary>
    /// Guidance class of a munition: the vanilla MissileSeeker subclass of its round.
    /// </summary>
    public enum SeekerKind
    {
        Unguided,
        ActiveRadar,      // ARHSeeker: AAM-29, AAM-36, NL-98
        SemiActiveRadar,  // SARHSeeker: RAM-45, StratoLance R9
        AntiRadiation,    // ARMSeeker: ARAD-116, ARAD-45
        Infrared,         // IRSeeker: MMR-S3, IRM-S1, IRM-S2
        Laser,            // LaserSeeker: AGR-18, AGR-24
        Optical,          // OpticalSeeker: AGM-48, AGM-68, ATP-1, Tusko-B, AT-145, GS25, glide bombs
        CruiseMissile,    // OpticalSeekerCruiseMissile: AGM-99, AShM-300, ALM-C450, ALND-4
        OpticalBomb,      // OpticalSeekerBomb: PAB, GPO, CBO-400, demolition and nuclear bombs
        OpticalShell,     // OpticalSeekerShell: 76 mm and 127 mm guided shells
        InertialShell,    // InertialSeekerShell: 155 mm guided shell, MLRS rocket
        Ballistic,        // BallisticMissileGuidance: Piledriver TBM
        HighDrag          // OpticalSeekerHighDrag
    }

    /// <summary>
    /// Vanilla data of one munition, read once from the prefab of its round. All values are the game values;
    /// the Effective* methods apply the config overrides (0 = vanilla value).
    /// </summary>
    public sealed class MunitionProfile
    {
        public string Name;
        public SeekerKind Kind;
        public float TerminalRange;    // ARH terminalRange, cruise missile terminalRange (m)
        public float SeekerAngleDeg;   // ARH maxTrackingAngle, ARM maxTargetAngle, optical searchAngle, SARH seekerAngle
        public float SearchRadius;     // Optical / bomb / shell searchRadius, cruise missile terminalSearchRadius (m)
        public float RoundCep;         // InertialSeekerShell.CEP, BallisticMissileGuidance.circularError (m)
        public float LethalRadius;     // Vanilla danger radius 13 * yield^(1/3) (m)
        public bool GuidedArtillery;   // Guided shell or MLRS rocket
    }

    /// <summary>
    /// Munition profiles from game data, for AI launch doctrine, seeker activation and the map basket ring.
    /// </summary>
    public static class MunitionCatalog
    {
        // Vanilla Missile.CheckExclusionZone: danger radius = 13 * blastYield^(1/3)
        private const float VanillaDangerRadiusPerCubeRootYield = 13.0f;

        // Vanilla AIPilotCombatModes.ManageTarget accuracy requirement for every attack mode
        public const float VanillaTargetAccuracy = 100.0f;

        private static readonly Dictionary<WeaponInfo, MunitionProfile> s_profiles = new Dictionary<WeaponInfo, MunitionProfile>();
        private static readonly MunitionProfile s_unguided = new MunitionProfile { Name = "unknown", Kind = SeekerKind.Unguided };

        private static readonly Func<Gun, MissileDefinition> f_gunGuidedProjectile = FastReflection.CreateFieldGetter<Gun, MissileDefinition>("guidedProjectile");
        private static readonly Func<ARHSeeker, float> f_arhTerminalRange = FastReflection.CreateFieldGetter<ARHSeeker, float>("terminalRange");
        private static readonly Func<ARHSeeker, float> f_arhMaxTrackingAngle = FastReflection.CreateFieldGetter<ARHSeeker, float>("maxTrackingAngle");
        private static readonly Func<ARMSeeker, float> f_armMaxTargetAngle = FastReflection.CreateFieldGetter<ARMSeeker, float>("maxTargetAngle");
        private static readonly Func<SARHSeeker, float> f_sarhSeekerAngle = FastReflection.CreateFieldGetter<SARHSeeker, float>("seekerAngle");
        private static readonly Func<OpticalSeeker, float> f_opticalSearchRadius = FastReflection.CreateFieldGetter<OpticalSeeker, float>("searchRadius");
        private static readonly Func<OpticalSeeker, float> f_opticalSearchAngle = FastReflection.CreateFieldGetter<OpticalSeeker, float>("searchAngle");
        private static readonly Func<OpticalSeekerBomb, float> f_bombSearchRadius = FastReflection.CreateFieldGetter<OpticalSeekerBomb, float>("searchRadius");
        private static readonly Func<OpticalSeekerShell, float> f_shellSearchRadius = FastReflection.CreateFieldGetter<OpticalSeekerShell, float>("searchRadius");
        private static readonly Func<OpticalSeekerCruiseMissile, float> f_cruiseTerminalRange = FastReflection.CreateFieldGetter<OpticalSeekerCruiseMissile, float>("terminalRange");
        private static readonly Func<OpticalSeekerCruiseMissile, float> f_cruiseSearchRadius = FastReflection.CreateFieldGetter<OpticalSeekerCruiseMissile, float>("terminalSearchRadius");
        private static readonly Func<InertialSeekerShell, float> f_inertialCep = FastReflection.CreateFieldGetter<InertialSeekerShell, float>("CEP");
        private static readonly Func<BallisticMissileGuidance, float> f_ballisticCep = FastReflection.CreateFieldGetter<BallisticMissileGuidance, float>("circularError");

        public static void ClearCache()
        {
            s_profiles.Clear();
        }

        /// <summary>
        /// Profile of the munition a weapon fires. A weapon without a guided round gets the Unguided profile.
        /// </summary>
        public static MunitionProfile Get(WeaponInfo weaponInfo)
        {
            if (weaponInfo == null) return s_unguided;
            MunitionProfile profile;
            if (s_profiles.TryGetValue(weaponInfo, out profile)) return profile;

            profile = Build(weaponInfo);
            s_profiles[weaponInfo] = profile;
            if (CnoEvilConfig.DebugLogging != null && CnoEvilConfig.DebugLogging.Value && CnoEvilPlugin.Log != null)
            {
                CnoEvilPlugin.Log.LogInfo(string.Format(
                    "[CNO-EVIL] Munition {0}: {1}, terminal range {2:F0} m, seeker angle {3:F0} deg, search radius {4:F0} m, round CEP {5:F0} m, danger radius {6:F0} m, guided artillery {7}",
                    profile.Name, profile.Kind, profile.TerminalRange, profile.SeekerAngleDeg, profile.SearchRadius,
                    profile.RoundCep, profile.LethalRadius, profile.GuidedArtillery));
            }
            return profile;
        }

        /// <summary>
        /// Profile of a missile in flight (from its WeaponInfo, so the values are the prefab values).
        /// </summary>
        public static MunitionProfile Get(Missile missile)
        {
            if (missile == null) return s_unguided;
            WeaponInfo info = null;
            try { info = missile.GetWeaponInfo(); } catch { }
            return Get(info);
        }

        private static MunitionProfile Build(WeaponInfo weaponInfo)
        {
            MunitionProfile p = new MunitionProfile();
            p.Name = !string.IsNullOrEmpty(weaponInfo.weaponName) ? weaponInfo.weaponName : weaponInfo.name;
            p.Kind = SeekerKind.Unguided;

            GameObject prefab = weaponInfo.weaponPrefab;
            if (prefab == null) return p;

            // The round: a gun's guided projectile, a launcher's missile, else the weapon prefab itself (missiles, bombs)
            MissileDefinition projectile = null;
            Gun gun = prefab.GetComponentInChildren<Gun>(true);
            if (gun != null && f_gunGuidedProjectile != null) projectile = f_gunGuidedProjectile(gun);
            if (projectile == null)
            {
                MissileLauncher launcher = prefab.GetComponentInChildren<MissileLauncher>(true);
                if (launcher != null) projectile = launcher.missile;
            }
            GameObject round = (projectile != null && projectile.unitPrefab != null) ? projectile.unitPrefab : prefab;

            Missile missile = round.GetComponentInChildren<Missile>(true);
            if (missile != null)
            {
                p.LethalRadius = VanillaDangerRadiusPerCubeRootYield * Mathf.Pow(Mathf.Max(0.0f, missile.GetYield()), 1.0f / 3.0f);
            }

            ReadSeeker(p, round.GetComponentInChildren<MissileSeeker>(true));
            return p;
        }

        private static void ReadSeeker(MunitionProfile p, MissileSeeker seeker)
        {
            if (seeker == null) return;

            ARHSeeker arh = seeker as ARHSeeker;
            if (arh != null)
            {
                p.Kind = SeekerKind.ActiveRadar;
                if (f_arhTerminalRange != null) p.TerminalRange = f_arhTerminalRange(arh);
                if (f_arhMaxTrackingAngle != null) p.SeekerAngleDeg = f_arhMaxTrackingAngle(arh);
                return;
            }
            SARHSeeker sarh = seeker as SARHSeeker;
            if (sarh != null)
            {
                p.Kind = SeekerKind.SemiActiveRadar;
                if (f_sarhSeekerAngle != null) p.SeekerAngleDeg = f_sarhSeekerAngle(sarh);
                return;
            }
            ARMSeeker arm = seeker as ARMSeeker;
            if (arm != null)
            {
                p.Kind = SeekerKind.AntiRadiation;
                if (f_armMaxTargetAngle != null) p.SeekerAngleDeg = f_armMaxTargetAngle(arm);
                return;
            }
            if (seeker is IRSeeker) { p.Kind = SeekerKind.Infrared; return; }
            if (seeker is LaserSeeker) { p.Kind = SeekerKind.Laser; return; }

            OpticalSeekerCruiseMissile cruise = seeker as OpticalSeekerCruiseMissile;
            if (cruise != null)
            {
                p.Kind = SeekerKind.CruiseMissile;
                if (f_cruiseTerminalRange != null) p.TerminalRange = f_cruiseTerminalRange(cruise);
                if (f_cruiseSearchRadius != null) p.SearchRadius = f_cruiseSearchRadius(cruise);
                return;
            }
            OpticalSeeker optical = seeker as OpticalSeeker;
            if (optical != null)
            {
                p.Kind = SeekerKind.Optical;
                if (f_opticalSearchRadius != null) p.SearchRadius = f_opticalSearchRadius(optical);
                if (f_opticalSearchAngle != null) p.SeekerAngleDeg = f_opticalSearchAngle(optical);
                return;
            }
            OpticalSeekerBomb bomb = seeker as OpticalSeekerBomb;
            if (bomb != null)
            {
                p.Kind = SeekerKind.OpticalBomb;
                if (f_bombSearchRadius != null) p.SearchRadius = f_bombSearchRadius(bomb);
                return;
            }
            OpticalSeekerShell shell = seeker as OpticalSeekerShell;
            if (shell != null)
            {
                p.Kind = SeekerKind.OpticalShell;
                p.GuidedArtillery = true;
                if (f_shellSearchRadius != null) p.SearchRadius = f_shellSearchRadius(shell);
                return;
            }
            InertialSeekerShell inertial = seeker as InertialSeekerShell;
            if (inertial != null)
            {
                p.Kind = SeekerKind.InertialShell;
                p.GuidedArtillery = true;
                if (f_inertialCep != null) p.RoundCep = f_inertialCep(inertial);
                return;
            }
            BallisticMissileGuidance ballistic = seeker as BallisticMissileGuidance;
            if (ballistic != null)
            {
                p.Kind = SeekerKind.Ballistic;
                if (f_ballisticCep != null) p.RoundCep = f_ballisticCep(ballistic);
                return;
            }
            if (seeker is OpticalSeekerHighDrag) p.Kind = SeekerKind.HighDrag;
        }

        // ---------------------------------------------------------------- config overrides (0 = vanilla)

        private static float Override(BepInEx.Configuration.ConfigEntry<float> entry)
        {
            return (entry != null && entry.Value > 0.0f) ? entry.Value : 0.0f;
        }

        /// <summary>
        /// Range (m) to the datum at which an ARH seeker goes active, or at which a cruise missile starts its
        /// terminal search: ARHSeekerActivationRangeMeters / CruiseTerminalRangeMeters, else the vanilla terminalRange.
        /// </summary>
        public static float EffectiveTerminalRange(MunitionProfile p)
        {
            if (p == null) return 0.0f;
            float o = 0.0f;
            if (p.Kind == SeekerKind.ActiveRadar) o = Override(CnoEvilConfig.ARHSeekerActivationRangeMeters);
            else if (p.Kind == SeekerKind.CruiseMissile) o = Override(CnoEvilConfig.CruiseTerminalRangeMeters);
            return (o > 0.0f) ? o : p.TerminalRange;
        }

        /// <summary>
        /// Radius (m) around the datum in which an optical seeker or a cruise missile finds its target:
        /// OpticalSearchRadiusMeters / CruiseTerminalSearchRadiusMeters, else the vanilla value.
        /// </summary>
        public static float EffectiveSearchRadius(MunitionProfile p)
        {
            if (p == null) return 0.0f;
            float o = 0.0f;
            if (p.Kind == SeekerKind.CruiseMissile) o = Override(CnoEvilConfig.CruiseTerminalSearchRadiusMeters);
            else if (p.Kind == SeekerKind.Optical || p.Kind == SeekerKind.OpticalBomb || p.Kind == SeekerKind.OpticalShell)
                o = Override(CnoEvilConfig.OpticalSearchRadiusMeters);
            return (o > 0.0f) ? o : p.SearchRadius;
        }

        /// <summary>
        /// Range (m) to the target inside which an optical seeker may acquire (OpticalSeekerActivationRangeMeters).
        /// 0 = vanilla: no range limit besides line of sight.
        /// </summary>
        public static float OpticalActivationRange
        {
            get { return Override(CnoEvilConfig.OpticalSeekerActivationRangeMeters); }
        }

        /// <summary>
        /// Range (m) to the datum inside which an ARM seeker listens for emitters (ARMSeekerActivationRangeMeters).
        /// 0 = vanilla: the seeker listens for the whole flight.
        /// </summary>
        public static float ArmActivationRange
        {
            get { return Override(CnoEvilConfig.ARMSeekerActivationRangeMeters); }
        }

        // ---------------------------------------------------------------- acquisition basket

        /// <summary>
        /// True when the seeker homes on something other than the datum (illumination, laser spot, IR source),
        /// so the launch dispersion does not decide a hit.
        /// </summary>
        public static bool IgnoresDatum(MunitionProfile p)
        {
            if (p == null) return true;
            return p.Kind == SeekerKind.SemiActiveRadar || p.Kind == SeekerKind.Laser ||
                   p.Kind == SeekerKind.Infrared || p.Kind == SeekerKind.Unguided;
        }

        /// <summary>
        /// Radius (m) around the datum in which the seeker still finds the target.
        /// ARH: the seeker cone at its activation range (maxTrackingAngle), limited by the seeker's noise-limited
        /// detection range on targetRcs (no limit when targetRcs is 0). Optical / bomb / shell / cruise: search radius.
        /// ARM: unlimited while the target radar emits, else 0. Ballistic and inertial rounds: 0.
        /// </summary>
        public static float GetAcquisitionRadius(MunitionProfile p, Unit target, float targetRcs)
        {
            if (p == null) return 0.0f;
            switch (p.Kind)
            {
                case SeekerKind.ActiveRadar:
                {
                    float angle = Mathf.Clamp(p.SeekerAngleDeg, 0.0f, 80.0f);
                    float cone = Mathf.Max(0.0f, EffectiveTerminalRange(p)) * Mathf.Tan(angle * Mathf.Deg2Rad);
                    if (targetRcs <= 0.0f) return cone;
                    return Mathf.Min(cone, ArhDetectionRange(targetRcs));
                }
                case SeekerKind.AntiRadiation:
                {
                    Radar radar = (target != null) ? target.radar as Radar : null;
                    return (radar != null && radar.activated) ? float.PositiveInfinity : 0.0f;
                }
                case SeekerKind.Optical:
                case SeekerKind.OpticalBomb:
                case SeekerKind.OpticalShell:
                case SeekerKind.CruiseMissile:
                    return EffectiveSearchRadius(p);
                default:
                    return 0.0f;
            }
        }

        /// <summary>
        /// Noise-limited range (m) at which the ARH seeker reaches the minimum SNR on a target of rcs (m²):
        /// the radar equation of RadarSignalModel.EvaluateArhSnrDb without clutter and jamming.
        /// </summary>
        public static float ArhDetectionRange(float rcs)
        {
            RadarSpecs specs = RadarSpecs.CreateSeekerRadar();
            float noise = RadarPhysics.CalculateThermalNoiseFloor(specs.BandwidthHz, specs.NoiseFigureLinear);
            float minSnr = CnoEvilConfig.EffectiveMinSNR;
            float lo = 100.0f, hi = 60000.0f;
            for (int i = 0; i < 24; i++)
            {
                float mid = 0.5f * (lo + hi);
                float echo = RadarPhysics.CalculateEchoPower(mid, rcs, ref specs);
                if (RadarPhysics.CalculateSNRdB(echo, noise, 0.0f, 0.0f) >= minSnr) lo = mid;
                else hi = mid;
            }
            return lo;
        }

        /// <summary>
        /// How far the aim point may be off and the weapon still kills the target: the larger of the seeker
        /// acquisition radius and the warhead danger radius, at least the vanilla 100 m.
        /// </summary>
        public static float GetAcceptableAimError(WeaponInfo weaponInfo, Unit target)
        {
            MunitionProfile p = Get(weaponInfo);
            float rcs = (target != null) ? target.RCS : 0.0f;
            float basket = IgnoresDatum(p) ? float.PositiveInfinity : GetAcquisitionRadius(p, target, rcs);
            return Mathf.Max(VanillaTargetAccuracy, Mathf.Max(basket, p.LethalRadius));
        }

        public static bool IsGuidedArtillery(WeaponInfo weaponInfo)
        {
            return weaponInfo != null && Get(weaponInfo).GuidedArtillery;
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
