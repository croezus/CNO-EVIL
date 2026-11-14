using System;
using System.Collections.Generic;
using UnityEngine;
using CnoEvil.Config;

namespace CnoEvil.Core
{
    /// <summary>
    /// Kind of a munition or external store, from vanilla technical data (WeaponInfo / WeaponMount flags and
    /// prefab components). Selects the database RCS when the game data has no value.
    /// </summary>
    public enum MunitionClass
    {
        Missile,
        GlideBomb,
        Bomb,
        JammerPod,
        FuelTank,
        GunPod,
        CountermeasurePod,
        CargoPod,
        OtherStore
    }

    /// <summary>
    /// Radar cross section of munitions and external stores. Source order for each value:
    /// 1. exact-key override (tables below, for content of other mods);
    /// 2. game / mod data: the prefab's UnitDefinition.radarSize, WeaponMount.RCS / emptyRCS;
    /// 3. database value of the MunitionClass, from the technical data (mass per round, store type).
    /// WeaponMount data is authoritative: vanilla gives 0 to internal guns, hooks and cargo on purpose.
    /// Only fuel tanks without any mount RCS use the database value.
    /// </summary>
    public static class MunitionRCSDatabase
    {
        // Vanilla carriage RCS per round is ~0.5x the in-flight radarSize
        // (AAM-29 0.005 / 0.010, PAB-250 0.050 / 0.100, GPO-500 0.075 / 0.150, AGM-68 0.025 / 0.050).
        public const float CarriageToFlightRcsRatio = 0.5f;

        // Database values (m², one round in flight), from the vanilla data export (0.30.2)
        // Missiles: AAMs, rockets and small AGMs up to 300 kg are 0.01; 300-1000 kg (ARAD-116, AGM-99, AShM-300,
        // AShM-200, ALM-C450) median 0.02; heavier (ALND-4 0.05) 0.03.
        private const float SmallMissileMaxMassKg = 300.0f;
        private const float MediumMissileMaxMassKg = 1000.0f;
        private const float SmallMissileRCS = 0.01f;
        private const float MediumMissileRCS = 0.02f;
        private const float HeavyMissileRCS = 0.03f;
        private const float GlideBombRCS = 0.01f;          // PAB-80LR, glide bomb mounts
        private const float GenericBombRCS = 0.10f;        // Bomb without mass data
        private const float JammerPodRCS = 0.005f;         // JammingPod1 mount
        private const float GunPodRCS = 0.01f;             // 12.7 / 20 / 25 mm gun pods 0.003-0.01
        private const float CountermeasurePodRCS = 0.01f;  // SpecialFlarePod mount
        private const float GenericStoreRCS = 0.05f;       // Cargo and other stores

        // Unguided bombs: in-flight radarSize against mass per round (PAB-125, PAB-250, GPO-500, GPO-2P, Demolition).
        // Fuel tanks use the same curve with the full store mass (similar streamlined body; no vanilla tank exists).
        private static readonly float[] BombMassKg = { 125.0f, 250.0f, 500.0f, 1000.0f, 10000.0f };
        private static readonly float[] BombRcs = { 0.05f, 0.10f, 0.15f, 0.20f, 1.0f };

        public struct StoreRcsOverride
        {
            public float EmptyRCS;      // Pylon / pod shell, once per hardpoint
            public float PerRoundRCS;   // Each loaded round

            public StoreRcsOverride(float emptyRcs, float perRoundRcs)
            {
                EmptyRCS = emptyRcs;
                PerRoundRCS = perRoundRcs;
            }
        }

        // In-flight RCS of one round, by exact WeaponInfo asset name, for example:
        // { "Aryx_HASAM", 0.008f },
        private static readonly Dictionary<string, float> s_flightRcsOverride =
            new Dictionary<string, float>(StringComparer.Ordinal)
        {
            // MiG-29SMT mod: the KAB-500L (about 530 kg) has radarSize 0.001, 100 x less than the vanilla PAB-250.
            // Value of the vanilla GPO-500.
            { "mig29smt_KAB500L_info", 0.15f }
        };

        // Carriage RCS, by exact WeaponMount.jsonKey, for example:
        // { "Aryx_DropTank_1200", new StoreRcsOverride(0.30f, 0.0f) },
        private static readonly Dictionary<string, StoreRcsOverride> s_mountRcsOverride =
            new Dictionary<string, StoreRcsOverride>(StringComparer.Ordinal)
        {
            // MiG-29SMT mod: KAB-500L mount RCS 0.001 per round; vanilla carriage is 0.5 x the flight RCS (GPO-500 0.075).
            // The pylon value (emptyRCS 0.001) stays.
            { "mig29smt_KAB500L_single", new StoreRcsOverride(0.001f, 0.075f) },
            // Mirage 2000D mod (M2K): the single mounts have RCS 0 and emptyRCS 0, so an external store would have no
            // carriage RCS; its double and triple mounts have values (MICA double 0.01 / 0.001, Mk 82 triple 0.15 /
            // 0.002). Per round: the vanilla carriage ratio 0.5 x the radarSize of the weapon; pylon 0.001 as on the
            // double mounts. Drop tanks RPL-522 / RPL-541 are munitions with radarSize 0.15 (no FuelTank component).
            // Targeting pod: the value of the M2K jamming pod (0.005).
            { "M2KW_mica_aft", new StoreRcsOverride(0.001f, 0.005f) },
            { "M2KW_mica_fwd", new StoreRcsOverride(0.001f, 0.005f) },
            { "M2KW_mica_wing_in", new StoreRcsOverride(0.001f, 0.005f) },
            { "M2KW_mica_wing_out", new StoreRcsOverride(0.001f, 0.005f) },
            { "M2KW_magic2_aft", new StoreRcsOverride(0.001f, 0.005f) },
            { "M2KW_magic2_wing_in", new StoreRcsOverride(0.001f, 0.005f) },
            { "M2KW_magic2_wing_out", new StoreRcsOverride(0.001f, 0.005f) },
            { "M2KW_mk82_aft", new StoreRcsOverride(0.001f, 0.05f) },
            { "M2KW_mk82_center", new StoreRcsOverride(0.001f, 0.05f) },
            { "M2KW_mk82_fwd", new StoreRcsOverride(0.001f, 0.05f) },
            { "M2KW_mk82_wing_in", new StoreRcsOverride(0.001f, 0.05f) },
            { "M2KW_mk82_wing_out", new StoreRcsOverride(0.001f, 0.05f) },
            { "M2KW_samp250_aft", new StoreRcsOverride(0.001f, 0.05f) },
            { "M2KW_samp250_center", new StoreRcsOverride(0.001f, 0.05f) },
            { "M2KW_samp250_fwd", new StoreRcsOverride(0.001f, 0.05f) },
            { "M2KW_samp250_wing_in", new StoreRcsOverride(0.001f, 0.05f) },
            { "M2KW_samp250_wing_out", new StoreRcsOverride(0.001f, 0.05f) },
            { "M2KW_gbu12_aft", new StoreRcsOverride(0.001f, 0.05f) },
            { "M2KW_gbu12_center", new StoreRcsOverride(0.001f, 0.05f) },
            { "M2KW_gbu12_wing_in", new StoreRcsOverride(0.001f, 0.05f) },
            { "M2KW_gbu12_wing_out", new StoreRcsOverride(0.001f, 0.05f) },
            { "M2KW_gbu49_aft", new StoreRcsOverride(0.001f, 0.05f) },
            { "M2KW_gbu49_center", new StoreRcsOverride(0.001f, 0.05f) },
            { "M2KW_gbu49_wing_in", new StoreRcsOverride(0.001f, 0.05f) },
            { "M2KW_gbu49_wing_out", new StoreRcsOverride(0.001f, 0.05f) },
            { "M2KW_aasm_aft", new StoreRcsOverride(0.001f, 0.025f) },
            { "M2KW_aasm_center", new StoreRcsOverride(0.001f, 0.025f) },
            { "M2KW_durandal_aft", new StoreRcsOverride(0.001f, 0.025f) },
            { "M2KW_durandal_center", new StoreRcsOverride(0.001f, 0.025f) },
            { "M2KW_durandal_fwd", new StoreRcsOverride(0.001f, 0.025f) },
            { "M2KW_scalp_center", new StoreRcsOverride(0.001f, 0.0025f) },
            { "M2KW_asmpa_center", new StoreRcsOverride(0.001f, 0.0025f) },
            { "M2K_RPL522", new StoreRcsOverride(0.001f, 0.075f) },
            { "M2K_RPL541", new StoreRcsOverride(0.001f, 0.075f) },
            { "M2K_FLIR", new StoreRcsOverride(0.005f, 0.0f) }
        };

        private static readonly Dictionary<WeaponInfo, float> s_radarSizeCache = new Dictionary<WeaponInfo, float>();
        private static readonly Dictionary<WeaponMount, StoreRcsOverride> s_mountCache = new Dictionary<WeaponMount, StoreRcsOverride>();

        public static void ClearAll()
        {
            s_radarSizeCache.Clear();
            s_mountCache.Clear();
        }

        /// <summary>
        /// Class of a weapon from its WeaponInfo flags and prefab components.
        /// </summary>
        public static MunitionClass ClassifyWeapon(WeaponInfo info)
        {
            if (info == null) return MunitionClass.OtherStore;
            if (info.missile) return MunitionClass.Missile;
            if (info.glideBomb) return MunitionClass.GlideBomb;
            if (info.bomb) return MunitionClass.Bomb;
            if (info.jammer) return MunitionClass.JammerPod;
            if (info.gun) return MunitionClass.GunPod;
            if (info.cargo || info.troops) return MunitionClass.CargoPod;
            return MunitionClass.OtherStore;
        }

        /// <summary>
        /// Class of an external store from its WeaponMount flags and prefab components, else from its weapon.
        /// </summary>
        public static MunitionClass ClassifyMount(WeaponMount mount)
        {
            if (mount == null) return MunitionClass.OtherStore;
            if (mount.prefab != null)
            {
                if (mount.prefab.GetComponentInChildren<FuelTank>(true) != null) return MunitionClass.FuelTank;
                if (mount.prefab.GetComponentInChildren<JammingPod>(true) != null) return MunitionClass.JammerPod;
            }
            if (mount.countermeasure) return MunitionClass.CountermeasurePod;
            if (mount.Cargo || mount.Troops) return MunitionClass.CargoPod;
            return ClassifyWeapon(mount.info);
        }

        /// <summary>
        /// Database RCS (m²) of one item of a class. massKg is the mass per round (munitions) or of the store.
        /// </summary>
        public static float GetClassRCS(MunitionClass cls, float massKg)
        {
            switch (cls)
            {
                case MunitionClass.Missile:
                    if (massKg <= SmallMissileMaxMassKg) return SmallMissileRCS;
                    if (massKg <= MediumMissileMaxMassKg) return MediumMissileRCS;
                    return HeavyMissileRCS;
                case MunitionClass.GlideBomb: return GlideBombRCS;
                case MunitionClass.Bomb: return (massKg > 0.0f) ? InterpolateBombRcs(massKg) : GenericBombRCS;
                case MunitionClass.FuelTank: return (massKg > 0.0f) ? InterpolateBombRcs(massKg) : GenericBombRCS;
                case MunitionClass.JammerPod: return JammerPodRCS;
                case MunitionClass.GunPod: return GunPodRCS;
                case MunitionClass.CountermeasurePod: return CountermeasurePodRCS;
                default: return GenericStoreRCS;
            }
        }

        // Log-log interpolation on the vanilla bomb points, clamped at the ends
        private static float InterpolateBombRcs(float massKg)
        {
            if (massKg <= BombMassKg[0]) return BombRcs[0];
            int last = BombMassKg.Length - 1;
            if (massKg >= BombMassKg[last]) return BombRcs[last];

            for (int i = 0; i < last; i++)
            {
                if (massKg <= BombMassKg[i + 1])
                {
                    float t = Mathf.Log(massKg / BombMassKg[i]) / Mathf.Log(BombMassKg[i + 1] / BombMassKg[i]);
                    return Mathf.Exp(Mathf.Lerp(Mathf.Log(BombRcs[i]), Mathf.Log(BombRcs[i + 1]), t));
                }
            }
            return BombRcs[last];
        }

        /// <summary>
        /// In-flight RCS (m²) of one round: override, else the weapon prefab's UnitDefinition.radarSize,
        /// else the database value of its class.
        /// </summary>
        public static float GetMunitionRadarSize(WeaponInfo info)
        {
            if (info == null) return GenericStoreRCS;

            float rcs;
            if (s_radarSizeCache.TryGetValue(info, out rcs)) return rcs;

            string source;
            if (s_flightRcsOverride.TryGetValue(info.name, out rcs))
            {
                source = "override";
            }
            else
            {
                rcs = -1.0f;
                source = "data";
                if (info.weaponPrefab != null)
                {
                    Unit prefabUnit = info.weaponPrefab.GetComponent<Unit>();
                    if (prefabUnit != null && prefabUnit.definition != null && prefabUnit.definition.radarSize > 0.0f)
                    {
                        rcs = prefabUnit.definition.radarSize;
                    }
                }
                if (rcs < 0.0f)
                {
                    rcs = GetClassRCS(ClassifyWeapon(info), info.massPerRound);
                    source = "class " + ClassifyWeapon(info);
                }
            }

            s_radarSizeCache[info] = rcs;
            LogValue("munition", info.name, source, rcs);
            return rcs;
        }

        /// <summary>
        /// Carriage RCS (m²) of a weapon that has no WeaponMount (built into the airframe).
        /// Missiles and bombs: in-flight RCS x carriage ratio per round. Other built-in weapons (guns): 0, as in vanilla.
        /// </summary>
        public static float GetFallbackCarriageRCS(WeaponInfo info, int ammo)
        {
            if (!IsMunition(info) || ammo <= 0) return 0.0f;
            return GetMunitionRadarSize(info) * CarriageToFlightRcsRatio * ammo;
        }

        public static bool IsMunition(WeaponInfo info)
        {
            return info != null && (info.missile || info.bomb || info.glideBomb);
        }

        /// <summary>
        /// Carriage RCS of an external mount: override, else the vanilla WeaponMount values
        /// (emptyRCS once per hardpoint, GetRCSPerRound per loaded round); a fuel tank without any mount RCS
        /// gets the database value of its mass.
        /// </summary>
        public static void GetMountRCS(WeaponMount mount, out float emptyRcs, out float perRoundRcs)
        {
            emptyRcs = 0.0f;
            perRoundRcs = 0.0f;
            if (mount == null) return;

            StoreRcsOverride value;
            if (!s_mountCache.TryGetValue(mount, out value))
            {
                string source = "data";
                if (!string.IsNullOrEmpty(mount.jsonKey) && s_mountRcsOverride.TryGetValue(mount.jsonKey, out value))
                {
                    source = "override";
                }
                else
                {
                    value.EmptyRCS = mount.emptyRCS;
                    value.PerRoundRCS = (mount.ammo > 0) ? mount.GetRCSPerRound() : 0.0f;
                    if (mount.RCS <= 0.0f && mount.emptyRCS <= 0.0f && ClassifyMount(mount) == MunitionClass.FuelTank)
                    {
                        value.EmptyRCS = GetClassRCS(MunitionClass.FuelTank, mount.mass);
                        value.PerRoundRCS = 0.0f;
                        source = "class FuelTank";
                    }
                }
                s_mountCache[mount] = value;
                LogValue("mount", mount.jsonKey, source, value.EmptyRCS + value.PerRoundRCS * Mathf.Max(0, mount.ammo));
            }

            emptyRcs = value.EmptyRCS;
            perRoundRcs = value.PerRoundRCS;
        }

        private static void LogValue(string kind, string key, string source, float rcs)
        {
            if (EvilLog.On(LogCat.Data))
            {
                EvilLog.Write(LogCat.Data, "RCS {0} {1}: {2:F4} m2 ({3})", kind, key, rcs, source);
            }
        }
    }
}
