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
        /// No negative count and a total not above the budget: the excess is removed from decoys, then chaff, then flares.
        /// </summary>
        public CmCounts Clamped(int budget)
        {
            CmCounts c = this;
            budget = Mathf.Max(0, budget);
            c.Flares = Mathf.Clamp(c.Flares, 0, budget);
            c.Chaff = Mathf.Clamp(c.Chaff, 0, budget);
            c.Decoys = Mathf.Clamp(c.Decoys, 0, budget);
            int excess = c.Total - budget;
            if (excess > 0) { int cut = Mathf.Min(excess, c.Decoys); c.Decoys -= cut; excess -= cut; }
            if (excess > 0) { int cut = Mathf.Min(excess, c.Chaff); c.Chaff -= cut; excess -= cut; }
            if (excess > 0) c.Flares -= excess;
            return c;
        }
    }

    /// <summary>
    /// Countermeasure loadout: each aircraft with flares carries flares, chaff and decoys up to CountermeasureBudget in
    /// total. Aircraft.Awake sets the default counts on every computer (AI and players without a choice). A player sets
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

        public static int Budget
        {
            get { return Mathf.Max(0, CnoEvilConfig.CountermeasureBudget.Value); }
        }

        public static CmCounts Default()
        {
            CmCounts c = new CmCounts(CnoEvilConfig.DefaultFlareCount.Value, CnoEvilConfig.DefaultChaffCount.Value, CnoEvilConfig.DefaultDecoyCount.Value);
            return c.Clamped(Budget);
        }

        public static bool HasFlares(Aircraft aircraft)
        {
            return aircraft != null && aircraft.GetComponentInChildren<FlareEjector>(true) != null;
        }

        // ---------------------------------------------------------------- apply

        /// <summary>
        /// Sets the counts on the ejectors and stations of the aircraft. The flares are divided between all flare
        /// ejectors. Ejectors that are not awake yet keep the values (their Awake copies ammo to maxAmmo and registers).
        /// Flare pods (Aryx ECS-16 / ECS-32) that are attached keep their flares on top of the budget: they paid for
        /// them with a pylon. A pod attached later adds its share itself and takes it back when it is removed.
        /// </summary>
        public static void Apply(Aircraft aircraft, CmCounts counts)
        {
            if (aircraft == null) return;
            FlareEjector[] flares = aircraft.GetComponentsInChildren<FlareEjector>(true);
            if (flares.Length == 0) return;

            int each = counts.Flares / flares.Length;
            int rest = counts.Flares - each * flares.Length;
            for (int i = 0; i < flares.Length; i++)
            {
                int pod = AryxWeaponsCompat.PodFlares(aircraft, flares[i]);
                SetAmmo(flares[i], fi_flareMaxAmmo, each + (i < rest ? 1 : 0) + pod);
            }

            ChaffEjector[] chaff = aircraft.GetComponentsInChildren<ChaffEjector>(true);
            for (int i = 0; i < chaff.Length; i++)
            {
                SetAmmo(chaff[i], fi_chaffMaxAmmo, DecoyDispenserSetup.IsDecoyEjector(chaff[i]) ? counts.Decoys : counts.Chaff);
            }

            RecountStations(aircraft);
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

        public static string Key(AircraftDefinition definition)
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
            if (key != null && s_choices.TryGetValue(key, out c)) return c.Clamped(Budget);
            return Default();
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
