using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using CnoEvil.Config;

namespace CnoEvil.Core
{
    /// <summary>
    /// Number of flares, chaff cartridges and active decoys of one aircraft.
    /// </summary>
    public struct CmCounts
    {
        public int Flares;
        public int Chaff;
        public int Decoys;

        public CmCounts(int flares, int chaff, int decoys)
        {
            Flares = flares;
            Chaff = chaff;
            Decoys = decoys;
        }

        public int Total
        {
            get { return Flares + Chaff + Decoys; }
        }

        /// <summary>
        /// No negative count, not more decoys than decoyCap and a total not above the budget: the excess is removed from
        /// decoys, then chaff, then flares.
        /// </summary>
        public CmCounts Clamped(int budget, int decoyCap)
        {
            CmCounts c = this;
            budget = Mathf.Max(0, budget);
            c.Flares = Mathf.Clamp(c.Flares, 0, budget);
            c.Chaff = Mathf.Clamp(c.Chaff, 0, budget);
            c.Decoys = Mathf.Clamp(c.Decoys, 0, Mathf.Clamp(decoyCap, 0, budget));
            int excess = c.Total - budget;
            if (excess > 0) { int cut = Mathf.Min(excess, c.Decoys); c.Decoys -= cut; excess -= cut; }
            if (excess > 0) { int cut = Mathf.Min(excess, c.Chaff); c.Chaff -= cut; excess -= cut; }
            if (excess > 0) c.Flares -= excess;
            return c;
        }
    }

    /// <summary>
    /// Countermeasure loadout: each aircraft with flares carries flares, chaff and decoys up to the budget of its type in
    /// total (Budget: CountermeasureBudget scaled by the square root of the planform area, not below the vanilla flare
    /// count x VanillaFlareBudgetFactor; at most DecoyBudgetShare of it decoys, enforced in Apply). Aircraft.Awake sets
    /// the default counts (Default: smart split by aircraft type, ADR-77) on every computer (AI and players without a choice). A player sets
    /// the counts with the sliders of the loadout menu (CountermeasureSliders); the choice is kept per aircraft type in
    /// a local file and goes to the host at FLY (CountermeasureSync). The host applies it to the spawned aircraft and
    /// sends the counts to the clients, because vanilla does not synchronize countermeasure ammo.
    /// Apply writes ammo and the private maxAmmo of each ejector (the vanilla rearm refills to maxAmmo) and the ammo of
    /// each station.
    /// </summary>
    public static class CountermeasureLoadout
    {
        private const string FileName = "CNO-EVIL.Countermeasures.txt";

        private static readonly FieldInfo fi_flareMaxAmmo = AccessTools.Field(typeof(FlareEjector), "maxAmmo");
        private static readonly FieldInfo fi_chaffMaxAmmo = AccessTools.Field(typeof(ChaffEjector), "maxAmmo");
        private static readonly Type t_station = AccessTools.Inner(typeof(CountermeasureManager), "CountermeasureStation");
        private static readonly FieldInfo fi_stationAmmo = (t_station != null) ? AccessTools.Field(t_station, "ammo") : null;
        private static readonly FieldInfo fi_stationMaxAmmo = (t_station != null) ? AccessTools.Field(t_station, "maxAmmo") : null;
        private static readonly Func<object, IList> f_stationCountermeasures = (t_station != null)
            ? FastReflection.CreateFieldGetter<IList>(t_station, "countermeasures") : null;

        // Local player choices per aircraft type (UnitDefinition.jsonKey)
        private static readonly Dictionary<string, CmCounts> s_choices = new Dictionary<string, CmCounts>();
        private static bool s_loaded;

        // Last counts that Apply set on each aircraft (instance id): an ejector that another plugin adds later (M2K
        // Spirale in M2KEWSuite.Start) gets them again with Reapply
        private static readonly Dictionary<int, CmCounts> s_applied = new Dictionary<int, CmCounts>();

        /// <summary>
        /// Applies the last counts of the aircraft again (the defaults when Apply did not run for it): a chaff ejector
        /// added after Aircraft.Awake gets its share of the budget.
        /// </summary>
        public static void Reapply(Aircraft aircraft)
        {
            if (aircraft == null) return;
            CmCounts counts;
            if (!s_applied.TryGetValue(aircraft.GetInstanceID(), out counts)) counts = Default(aircraft.definition);
            Apply(aircraft, counts);
        }

        /// <summary>Unit.OnDestroy: forget the counts of a destroyed aircraft.</summary>
        public static void RemoveUnit(Unit unit)
        {
            if ((object)unit == null || s_applied.Count == 0 || !(unit is Aircraft)) return;
            s_applied.Remove(unit.GetInstanceID());
        }

        public static void ClearAll()
        {
            s_applied.Clear();
        }

        // ---------------------------------------------------------------- aircraft type facts

        // Default flare share changes (ADR-77)
        private const float HelicopterFlareBonus = 0.2f;
        private const float AttackerFlareBonus = 0.1f;
        private const float FighterFlareBonus = -0.1f;
        // Limits of the DRFM part of the radar countermeasures in the default split
        private const float MinDecoyShareOfRadar = 0.25f;
        private const float MaxDecoyShareOfRadar = 0.75f;

        private struct TypeFacts
        {
            public int VanillaFlares;
            public bool Helicopter;
        }

        // Prefab data per aircraft type (Key). Not cleared on scene load: prefabs do not change with the scene.
        private static readonly Dictionary<string, TypeFacts> s_facts = new Dictionary<string, TypeFacts>();
        // Flare count of each aircraft prefab before the BVR flare count sweep doubles it (RecordPrefabFlares)
        private static readonly Dictionary<string, int> s_prefabFlares = new Dictionary<string, int>();
        private static readonly Dictionary<string, int> s_sweepFlares = new Dictionary<string, int>();

        /// <summary>
        /// Before the BVR flare count sweep: keeps the flare count of each aircraft prefab that BVR did not change yet.
        /// The first value of a type stays; ejectors of mount prefabs (pods) have no aircraft and are skipped.
        /// </summary>
        public static void RecordPrefabFlares(FlareEjector[] ejectors, Type modifiedFlag)
        {
            if (ejectors == null) return;
            s_sweepFlares.Clear();
            for (int i = 0; i < ejectors.Length; i++)
            {
                FlareEjector e = ejectors[i];
                if (e == null || e.gameObject.scene.IsValid()) continue;
                if (modifiedFlag != null && e.gameObject.GetComponent(modifiedFlag) != null) continue;
                Aircraft aircraft = e.GetComponentInParent<Aircraft>(true);
                string key = (aircraft != null) ? Key(aircraft.definition) : null;
                if (key == null || s_prefabFlares.ContainsKey(key)) continue;
                int sum;
                s_sweepFlares.TryGetValue(key, out sum);
                s_sweepFlares[key] = sum + Mathf.Max(0, e.ammo);
            }
            foreach (KeyValuePair<string, int> kv in s_sweepFlares) s_prefabFlares[kv.Key] = kv.Value;
            s_sweepFlares.Clear();
        }

        private static TypeFacts Facts(UnitDefinition definition)
        {
            TypeFacts facts = default(TypeFacts);
            string key = Key(definition);
            if (key == null) return facts;
            if (s_facts.TryGetValue(key, out facts)) return facts;
            GameObject prefab = definition.unitPrefab;
            if (prefab == null) return facts;

            int recorded;
            if (s_prefabFlares.TryGetValue(key, out recorded))
            {
                facts.VanillaFlares = recorded;
            }
            else
            {
                FlareEjector[] ejectors = prefab.GetComponentsInChildren<FlareEjector>(true);
                for (int i = 0; i < ejectors.Length; i++) facts.VanillaFlares += Mathf.Max(0, ejectors[i].ammo);
            }
            facts.Helicopter = prefab.GetComponentInChildren<SwashRotor>(true) != null;
            s_facts[key] = facts;
            return facts;
        }

        /// <summary>Flare count of the aircraft prefab (before BVR), 0 when unknown.</summary>
        public static int VanillaFlares(UnitDefinition definition)
        {
            return Facts(definition).VanillaFlares;
        }

        /// <summary>
        /// Countermeasure budget of an aircraft type (ADR-76, ADR-77): the larger of
        /// - CountermeasureBudget x sqrt(planform area / reference area) (UnitDefinition length x width; the mass is 0 on
        ///   some aircraft; no area or no reference area: CountermeasureBudget), and
        /// - the vanilla flare count x VanillaFlareBudgetFactor,
        /// in CountermeasureBudgetMin..Max, and never below the vanilla flare count (factor 0 = no vanilla floor).
        /// </summary>
        public static int Budget(UnitDefinition definition)
        {
            int baseBudget = Mathf.Max(0, CnoEvilConfig.CountermeasureBudget.Value);
            float reference = CnoEvilConfig.CountermeasureReferenceArea_m2.Value;
            float area = (definition != null) ? definition.length * definition.width : 0.0f;
            int budget = (reference > 0.0f && area > 0.0f) ? Mathf.RoundToInt(baseBudget * Mathf.Sqrt(area / reference)) : baseBudget;

            float factor = CnoEvilConfig.VanillaFlareBudgetFactor.Value;
            int vanilla = (factor > 0.0f) ? VanillaFlares(definition) : 0;
            if (vanilla > 0) budget = Mathf.Max(budget, Mathf.RoundToInt(vanilla * factor));

            int min = Mathf.Max(0, CnoEvilConfig.CountermeasureBudgetMin.Value);
            int max = Mathf.Max(min, CnoEvilConfig.CountermeasureBudgetMax.Value);
            return Mathf.Max(Mathf.Clamp(budget, min, max), vanilla);
        }

        /// <summary>Highest number of decoys (DRFM) in a budget: DecoyBudgetShare of it, rounded down.</summary>
        public static int DecoyCap(int budget)
        {
            return Mathf.Clamp(Mathf.FloorToInt(budget * Mathf.Clamp01(CnoEvilConfig.DecoyBudgetShare.Value) + 0.001f), 0, budget);
        }

        /// <summary>The counts limited to the budget and decoy cap of the aircraft type.</summary>
        public static CmCounts Clamp(CmCounts counts, UnitDefinition definition)
        {
            int budget = Budget(definition);
            return counts.Clamped(budget, DecoyCap(budget));
        }

        /// <summary>
        /// Part of the budget that is flares by default: DefaultFlareShare, more for a helicopter (low and slow, IR
        /// threats), more for an aircraft without radar in a surface role, less for an aircraft with radar in an air role.
        /// </summary>
        private static float FlareShare(UnitDefinition definition, TypeFacts facts)
        {
            float share = Mathf.Clamp01(CnoEvilConfig.DefaultFlareShare.Value);
            if (definition == null) return share;
            if (facts.Helicopter) return Mathf.Clamp01(share + HelicopterFlareBonus);
            bool radar = definition.typeIdentity.radar > 0.0f;
            RoleIdentity role = definition.roleIdentity;
            if (!radar && role.antiSurface >= role.antiAir) share += AttackerFlareBonus;
            else if (radar && role.antiAir > role.antiSurface) share += FighterFlareBonus;
            return Mathf.Clamp01(share);
        }

        /// <summary>
        /// Default counts of the aircraft type (ADR-77), for AI aircraft and players without a saved choice:
        /// 1. Flares = the flare share of the budget, not less than the vanilla flare count.
        /// 2. The rest are radar countermeasures. The DRFM part grows when chaff hides the aircraft badly: chaff
        ///    effectiveness = ChaffCartridgeRcs_m2 / (that + radarSize). Low RCS (stealth): mostly chaff; high RCS: mostly
        ///    DRFM, not more than the decoy cap.
        /// A type that is off (ChaffEnabled, DecoyEnabled) gives its part to the other type, then to flares.
        /// </summary>
        public static CmCounts Default(UnitDefinition definition)
        {
            int budget = Budget(definition);
            int cap = DecoyCap(budget);
            TypeFacts facts = Facts(definition);
            int flares = Mathf.Clamp(Mathf.Max(facts.VanillaFlares, Mathf.RoundToInt(FlareShare(definition, facts) * budget)), 0, budget);
            int radarPart = budget - flares;
            int chaff = 0;
            int decoys = 0;
            if (radarPart > 0)
            {
                bool chaffOn = CnoEvilConfig.ChaffEnabled.Value;
                bool decoyOn = CnoEvilConfig.DecoyEnabled.Value;
                float rcs = (definition != null) ? Mathf.Max(0.0f, definition.radarSize) : 0.0f;
                float cartridge = Mathf.Max(0.0001f, CnoEvilConfig.ChaffCartridgeRcs_m2.Value);
                float decoyShare = Mathf.Clamp(rcs / (cartridge + rcs), MinDecoyShareOfRadar, MaxDecoyShareOfRadar);
                if (!chaffOn) decoyShare = 1.0f;
                if (!decoyOn) decoyShare = 0.0f;
                decoys = Mathf.Min(cap, Mathf.RoundToInt(radarPart * decoyShare));
                if (chaffOn) chaff = radarPart - decoys;
                else flares += radarPart - decoys;
            }
            return new CmCounts(flares, chaff, decoys).Clamped(budget, cap);
        }

        public static bool HasFlares(Aircraft aircraft)
        {
            return aircraft != null && aircraft.GetComponentInChildren<FlareEjector>(true) != null;
        }

        // ---------------------------------------------------------------- apply

        /// <summary>
        /// Sets the counts on the ejectors and stations of the aircraft. The flares are divided between the flare ejectors
        /// of the aircraft, the chaff between its chaff ejectors and the decoys between its decoy ejectors. Ejectors that
        /// are not awake yet keep the values (their Awake copies ammo to maxAmmo and registers).
        /// Pods keep their countermeasures on top of the budget: they paid for them with a pylon.
        /// - Flare pods of the Aryx Weapons Pack (ECS-16 / ECS-32) add flares to an ejector of the aircraft. A pod attached
        ///   later adds its share itself and takes it back when it is removed.
        /// - A mount with its own FlareEjector or ChaffEjector (MiG-29 mod pods) is not changed: it keeps the count of its
        ///   prefab, and the vanilla rearm refills it to that count.
        /// A ChaffEjector of the aircraft prefab (MiG-29 mod) gets the chaff count like the chaff ejector of this mod.
        /// </summary>
        public static void Apply(Aircraft aircraft, CmCounts counts)
        {
            if (aircraft == null) return;
            FlareEjector[] flareEjectors = aircraft.GetComponentsInChildren<FlareEjector>(true);
            if (flareEjectors.Length == 0) return;
            // Hard limit for every path (default, saved or client choice, host counts, Reapply, vanilla rearm to maxAmmo):
            // not more than the budget and the decoy cap of the aircraft type
            counts = Clamp(counts, aircraft.definition);
            s_applied[aircraft.GetInstanceID()] = counts;
            List<GameObject> mounts = SpawnedMounts(aircraft);

            List<Countermeasure> flares = new List<Countermeasure>();
            for (int i = 0; i < flareEjectors.Length; i++)
            {
                if (!InMount(flareEjectors[i], mounts)) flares.Add(flareEjectors[i]);
            }
            List<Countermeasure> chaff = new List<Countermeasure>();
            List<Countermeasure> decoys = new List<Countermeasure>();
            ChaffEjector[] chaffEjectors = aircraft.GetComponentsInChildren<ChaffEjector>(true);
            for (int i = 0; i < chaffEjectors.Length; i++)
            {
                if (DecoyDispenserSetup.IsDecoyEjector(chaffEjectors[i])) decoys.Add(chaffEjectors[i]);
                // With ChaffEnabled off, a chaff ejector of the aircraft prefab (MiG-29 mod) keeps its prefab count
                else if (!InMount(chaffEjectors[i], mounts) &&
                         (CnoEvilConfig.ChaffEnabled.Value || ChaffDispenserSetup.IsOwnChaffEjector(chaffEjectors[i])))
                {
                    chaff.Add(chaffEjectors[i]);
                }
            }

            Divide(flares, counts.Flares, fi_flareMaxAmmo, aircraft);
            Divide(chaff, counts.Chaff, fi_chaffMaxAmmo, null);
            Divide(decoys, counts.Decoys, fi_chaffMaxAmmo, null);

            RecountStations(aircraft);
        }

        /// <summary>
        /// Divides the count between the ejectors; the first ejectors get the remainder. With an aircraft, a flare ejector
        /// also keeps the flares of the attached Aryx flare pods.
        /// </summary>
        private static void Divide(List<Countermeasure> ejectors, int count, FieldInfo maxAmmo, Aircraft podFlaresOf)
        {
            if (ejectors.Count == 0) return;
            int each = count / ejectors.Count;
            int rest = count - each * ejectors.Count;
            for (int i = 0; i < ejectors.Count; i++)
            {
                int pod = (podFlaresOf != null) ? AryxWeaponsCompat.PodFlares(podFlaresOf, ejectors[i] as FlareEjector) : 0;
                SetAmmo(ejectors[i], maxAmmo, each + (i < rest ? 1 : 0) + pod);
            }
        }

        // ---------------------------------------------------------------- mounts with countermeasures

        private static readonly Func<Hardpoint, GameObject> f_spawnedPrefab =
            FastReflection.CreateFieldGetter<Hardpoint, GameObject>("spawnedPrefab");

        /// <summary>
        /// The mount objects that the hardpoints of the aircraft spawned (Hardpoint.SpawnMount).
        /// </summary>
        private static List<GameObject> SpawnedMounts(Aircraft aircraft)
        {
            List<GameObject> list = new List<GameObject>();
            WeaponManager manager = aircraft.weaponManager;
            if (manager == null || manager.hardpointSets == null || f_spawnedPrefab == null) return list;
            for (int s = 0; s < manager.hardpointSets.Length; s++)
            {
                HardpointSet set = manager.hardpointSets[s];
                if (set == null || set.hardpoints == null) continue;
                for (int h = 0; h < set.hardpoints.Count; h++)
                {
                    GameObject mount = (set.hardpoints[h] != null) ? f_spawnedPrefab(set.hardpoints[h]) : null;
                    if (mount != null) list.Add(mount);
                }
            }
            return list;
        }

        private static bool InMount(Countermeasure countermeasure, List<GameObject> mounts)
        {
            Transform t = countermeasure.transform;
            for (int i = 0; i < mounts.Count; i++)
            {
                if (t.IsChildOf(mounts[i].transform)) return true;
            }
            return false;
        }

        /// <summary>
        /// Flares and chaff cartridges of the FlareEjector and ChaffEjector components in a mount prefab (MiG-29 mod pods),
        /// and the flares that an Aryx flare pod adds. They come on top of the budget.
        /// </summary>
        public static void MountShare(WeaponMount mount, out int flares, out int chaff)
        {
            flares = 0;
            chaff = 0;
            if (mount == null) return;
            flares = AryxWeaponsCompat.PodFlares(mount);
            if (mount.prefab == null) return;
            FlareEjector[] f = mount.prefab.GetComponentsInChildren<FlareEjector>(true);
            for (int i = 0; i < f.Length; i++) flares += Mathf.Max(0, f[i].ammo);
            ChaffEjector[] c = mount.prefab.GetComponentsInChildren<ChaffEjector>(true);
            for (int i = 0; i < c.Length; i++) chaff += Mathf.Max(0, c[i].ammo);
        }

        private static void SetAmmo(Countermeasure countermeasure, FieldInfo maxAmmo, int value)
        {
            countermeasure.ammo = Mathf.Max(0, value);
            if (maxAmmo != null) maxAmmo.SetValue(countermeasure, countermeasure.ammo);
        }

        /// <summary>
        /// Station ammo = sum of its countermeasures (vanilla CountAmmo without its HUD call); maxAmmo = ammo for the
        /// ammo proportion. The HUD of the local aircraft shows the active station again.
        /// </summary>
        public static void RecountStations(Aircraft aircraft)
        {
            CountermeasureManager manager = aircraft.countermeasureManager;
            if (manager == null) return;
            IList stations = ChaffDispenserSetup.Stations(manager);
            if (stations == null || f_stationCountermeasures == null || fi_stationAmmo == null || fi_stationMaxAmmo == null) return;
            for (int s = 0; s < stations.Count; s++)
            {
                object station = stations[s];
                IList list = (station != null) ? f_stationCountermeasures(station) : null;
                if (list == null) continue;
                int sum = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    Countermeasure cm = list[i] as Countermeasure;
                    if (cm != null) sum += cm.ammo;
                }
                fi_stationAmmo.SetValue(station, sum);
                fi_stationMaxAmmo.SetValue(station, sum);
            }

            CombatHUD hud = SceneSingleton<CombatHUD>.i;
            if (hud != null && hud.aircraft == aircraft && stations.Count > 0) manager.UpdateHUD();
        }

        // ---------------------------------------------------------------- local choices

        public static string Key(UnitDefinition definition)
        {
            if (definition == null) return null;
            return !string.IsNullOrEmpty(definition.jsonKey) ? definition.jsonKey : definition.unitName;
        }

        /// <summary>
        /// The choice of the local player for this aircraft type; the defaults when there is none.
        /// </summary>
        public static CmCounts GetChoice(AircraftDefinition definition)
        {
            Load();
            string key = Key(definition);
            CmCounts c;
            if (key != null && s_choices.TryGetValue(key, out c)) return Clamp(c, definition);
            return Default(definition);
        }

        public static void SetChoice(AircraftDefinition definition, CmCounts counts)
        {
            Load();
            string key = Key(definition);
            if (key != null) s_choices[key] = counts;
        }

        private static string FilePath
        {
            get { return Path.Combine(BepInEx.Paths.ConfigPath, FileName); }
        }

        private static void Load()
        {
            if (s_loaded) return;
            s_loaded = true;
            try
            {
                if (!File.Exists(FilePath)) return;
                string[] lines = File.ReadAllLines(FilePath);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    int eq = line.LastIndexOf('=');
                    if (eq <= 0) continue;
                    string[] parts = line.Substring(eq + 1).Split(',');
                    int f, c, d;
                    if (parts.Length != 3 || !int.TryParse(parts[0], out f) || !int.TryParse(parts[1], out c) || !int.TryParse(parts[2], out d)) continue;
                    s_choices[line.Substring(0, eq).Trim()] = new CmCounts(f, c, d);
                }
            }
            catch (Exception ex)
            {
                if (CnoEvilPlugin.Log != null) CnoEvilPlugin.Log.LogWarning("Countermeasure choices not read: " + ex.Message);
            }
        }

        public static void Save()
        {
            if (!s_loaded) return;
            try
            {
                List<string> lines = new List<string>();
                lines.Add("# CNO-EVIL countermeasure loadout per aircraft type: flares,chaff,decoys (loadout menu sliders)");
                foreach (KeyValuePair<string, CmCounts> kv in s_choices)
                {
                    lines.Add(string.Format("{0}={1},{2},{3}", kv.Key, kv.Value.Flares, kv.Value.Chaff, kv.Value.Decoys));
                }
                File.WriteAllLines(FilePath, lines.ToArray());
            }
            catch (Exception ex)
            {
                if (CnoEvilPlugin.Log != null) CnoEvilPlugin.Log.LogWarning("Countermeasure choices not saved: " + ex.Message);
            }
        }
    }
}
