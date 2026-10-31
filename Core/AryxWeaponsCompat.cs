using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace CnoEvil.Core
{
    /// <summary>
    /// Run-time binding to the Aryx Weapons Pack (AryxWeaponryExpansion). The mod has no reference to its assembly: the
    /// types are found by name, and every user does nothing when the pack is not installed.
    /// - Flare pods (ECS-16, ECS-32: AryxExternalFlareLauncher) add their flares to the first FlareEjector of the
    ///   aircraft; CountermeasureLoadout keeps this share on top of the budget.
    /// - Hardpoint add-ons (flare pods, ETS-15 targeting pod) have no WeaponStation; they are registered here for the
    ///   store RCS (AspectRCS) and the pod flare share.
    /// - The ETS-15 detector keeps its own optics; the inertial seeker (HSM-750) and the ABM seeker (R-100N) get a
    ///   munition class (MunitionCatalog).
    /// </summary>
    public static class AryxWeaponsCompat
    {
        public const string PluginGuid = "AryxWeaponryExpansion";
        private const string Ns = "AryxWeaponryExpansion.";

        public static readonly Type FlareLauncherType = AccessTools.TypeByName(Ns + "AryxExternalFlareLauncher");
        public static readonly Type TargetingPodType = AccessTools.TypeByName(Ns + "AryxExternalTargetingPodAddon");
        public static readonly Type PodDetectorType = AccessTools.TypeByName(Ns + "AryxHardpointTargetDetector");
        public static readonly Type InertialSeekerType = AccessTools.TypeByName(Ns + "AryxInertialSeeker");
        public static readonly Type AbmSeekerType = AccessTools.TypeByName(Ns + "AryxABMSeeker");

        private static readonly Func<object, object> f_applied = Getter("contributionApplied");
        private static readonly Func<object, object> f_appliedCount = Getter("appliedFlareCount");
        private static readonly Func<object, object> f_addedFlares = Getter("addedFlares");
        private static readonly Func<object, FlareEjector> f_flareEjector = (FlareLauncherType != null)
            ? FastReflection.CreateFieldGetter<FlareEjector>(FlareLauncherType, "flareEjector") : null;
        private static readonly Func<Weapon, Hardpoint> f_hardpoint = FastReflection.CreateFieldGetter<Weapon, Hardpoint>("hardpoint");
        private static readonly Func<Weapon, WeaponMount> f_mount = FastReflection.CreateFieldGetter<Weapon, WeaponMount>("mount");

        // Hardpoint add-ons of each unit (no WeaponStation)
        private static readonly Dictionary<Unit, List<Weapon>> s_addons = new Dictionary<Unit, List<Weapon>>();
        private static readonly List<Weapon> s_empty = new List<Weapon>();

        public static bool Installed
        {
            get { return FlareLauncherType != null; }
        }

        // Value-type fields (bool, int) are read boxed: these reads run only at spawn, apply, rearm and in the menu
        private static Func<object, object> Getter(string field)
        {
            if (FlareLauncherType == null) return null;
            System.Reflection.FieldInfo fi = AccessTools.Field(FlareLauncherType, field);
            if (fi == null) return null;
            return fi.GetValue;
        }

        private static int ReadInt(Func<object, object> getter, object instance)
        {
            if (getter == null || instance == null) return 0;
            object v = getter(instance);
            return (v is int) ? (int)v : 0;
        }

        // ---------------------------------------------------------------- flare pods

        /// <summary>
        /// Flares that the attached flare pods of the aircraft added to this ejector (0 without the pack). The pods are
        /// read from the add-on registry, not from the aircraft hierarchy: a mount with an AeroPart is moved to the
        /// scene root (AeroPart.CreateRB) on the computer that simulates the aircraft.
        /// </summary>
        public static int PodFlares(Aircraft aircraft, FlareEjector ejector)
        {
            if (aircraft == null || ejector == null || FlareLauncherType == null || f_applied == null || f_flareEjector == null) return 0;
            List<Weapon> addons = GetAddons(aircraft);
            int sum = 0;
            for (int i = 0; i < addons.Count; i++)
            {
                Weapon l = addons[i];
                if (!FlareLauncherType.IsInstanceOfType(l) || f_flareEjector(l) != ejector) continue;
                object applied = f_applied(l);
                if (applied is bool && (bool)applied) sum += ReadInt(f_appliedCount, l);
            }
            return sum;
        }

        /// <summary>
        /// Flares that a mount adds when it is attached (sum of addedFlares of its flare launchers, ECS-32: 2 x 16).
        /// </summary>
        public static int PodFlares(WeaponMount mount)
        {
            if (mount == null || mount.prefab == null || FlareLauncherType == null) return 0;
            Component[] launchers = mount.prefab.GetComponentsInChildren(FlareLauncherType, true);
            int sum = 0;
            for (int i = 0; i < launchers.Length; i++)
            {
                sum += Mathf.Max(0, ReadInt(f_addedFlares, launchers[i]));
            }
            return sum;
        }

        // ---------------------------------------------------------------- hardpoint add-ons

        public static void RegisterAddon(Weapon weapon, Unit unit)
        {
            if (weapon == null || unit == null) return;
            List<Weapon> list;
            if (!s_addons.TryGetValue(unit, out list))
            {
                list = new List<Weapon>();
                s_addons[unit] = list;
            }
            if (!list.Contains(weapon)) list.Add(weapon);
        }

        public static void UnregisterAddon(Weapon weapon)
        {
            if ((object)weapon == null) return;
            foreach (KeyValuePair<Unit, List<Weapon>> kv in s_addons)
            {
                if (kv.Value.Remove(weapon)) return;
            }
        }

        /// <summary>
        /// Hardpoint add-ons of the unit (do not change the list).
        /// </summary>
        public static List<Weapon> GetAddons(Unit unit)
        {
            List<Weapon> list;
            if ((object)unit == null || !s_addons.TryGetValue(unit, out list)) return s_empty;
            // An add-on destroyed without its RemoveContribution (scene teardown) leaves a dead entry
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i] == null) list.RemoveAt(i);
            }
            return list;
        }

        public static Hardpoint GetHardpoint(Weapon weapon)
        {
            return (weapon != null && f_hardpoint != null) ? f_hardpoint(weapon) : null;
        }

        public static WeaponMount GetMount(Weapon weapon)
        {
            return (weapon != null && f_mount != null) ? f_mount(weapon) : null;
        }

        // ---------------------------------------------------------------- detectors and seekers

        /// <summary>
        /// The target detector of an ETS-15 targeting pod (its own optics, not the aircraft's).
        /// </summary>
        public static bool IsPodDetector(TargetDetector detector)
        {
            return PodDetectorType != null && detector != null && detector.GetType() == PodDetectorType;
        }

        public static bool IsInertialSeeker(MissileSeeker seeker)
        {
            return InertialSeekerType != null && seeker != null && InertialSeekerType.IsInstanceOfType(seeker);
        }

        public static void RemoveUnit(Unit unit)
        {
            if ((object)unit != null) s_addons.Remove(unit);
        }

        public static void ClearAll()
        {
            s_addons.Clear();
        }
    }
}
