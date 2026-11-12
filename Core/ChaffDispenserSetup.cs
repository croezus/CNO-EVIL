using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using CnoEvil.Config;

namespace CnoEvil.Core
{
    /// <summary>
    /// Chaff equipment from the unused vanilla chaff code: the game has ChaffEjector (a copy of FlareEjector) and RadarChaff
    /// (a ballistic cloud that raises Unit.onAddRadarChaff), but no prefab uses them. Each aircraft with a FlareEjector gets
    /// a ChaffEjector at its flare dispenser (same ejection points and sound) with the default chaff count of the aircraft type
    /// (CountermeasureLoadout sets the count of the player choice).
    /// The chaff prefab is built at run time: a RadarChaff with silver glint particles and a faint haze, with the materials
    /// of the vanilla flare. The chaff station is always the last station, so the station indices of vanilla stay the same
    /// (the indices go over the network in CmdCountermeasures / RpcCountermeasures).
    /// </summary>
    public static class ChaffDispenserSetup
    {
        public const string StationName = "Radar Chaff";
        public const string HudLabel = "CHAFF";

        // Terminal fall speed of the dipoles (m/s): the gravity of the vanilla drag model is set to drag x speed^2
        private const float DipoleFallSpeed = 1.5f;
        private const int GlintCount = 220;
        private const int HazeCount = 14;

        private static readonly FieldInfo fi_flarePrefab = AccessTools.Field(typeof(FlareEjector), "flarePrefab");
        private static readonly FieldInfo fi_flareVelocity = AccessTools.Field(typeof(FlareEjector), "ejectionVelocity");
        private static readonly FieldInfo fi_flareVelocityVariance = AccessTools.Field(typeof(FlareEjector), "ejectionVelocityVariance");
        private static readonly FieldInfo fi_flarePoints = AccessTools.Field(typeof(FlareEjector), "ejectionPoints");
        private static readonly FieldInfo fi_flareSound = AccessTools.Field(typeof(FlareEjector), "ejectionSound");
        private static readonly FieldInfo fi_flareVolume = AccessTools.Field(typeof(FlareEjector), "ejectionVolume");

        private static readonly FieldInfo fi_chaffPrefab = AccessTools.Field(typeof(ChaffEjector), "chaffPrefab");
        private static readonly FieldInfo fi_chaffVelocity = AccessTools.Field(typeof(ChaffEjector), "ejectionVelocity");
        private static readonly FieldInfo fi_chaffVelocityVariance = AccessTools.Field(typeof(ChaffEjector), "ejectionVelocityVariance");
        private static readonly FieldInfo fi_chaffPoints = AccessTools.Field(typeof(ChaffEjector), "ejectionPoints");
        private static readonly FieldInfo fi_chaffDoors = AccessTools.Field(typeof(ChaffEjector), "flareDoors");
        private static readonly FieldInfo fi_chaffInterval = AccessTools.Field(typeof(ChaffEjector), "ejectionInterval");
        private static readonly FieldInfo fi_chaffGrouping = AccessTools.Field(typeof(ChaffEjector), "ejectionGrouping");
        private static readonly FieldInfo fi_chaffSound = AccessTools.Field(typeof(ChaffEjector), "ejectionSound");
        private static readonly FieldInfo fi_chaffVolume = AccessTools.Field(typeof(ChaffEjector), "ejectionVolume");

        private static readonly FieldInfo fi_rcTime = AccessTools.Field(typeof(RadarChaff), "chaffTime");
        private static readonly FieldInfo fi_rcDrag = AccessTools.Field(typeof(RadarChaff), "drag");
        private static readonly FieldInfo fi_rcParticles = AccessTools.Field(typeof(RadarChaff), "chaffParticles");
        private static readonly FieldInfo fi_rcGravity = AccessTools.Field(typeof(RadarChaff), "gravityVector");
        private static readonly FieldInfo fi_rcLayermask = AccessTools.Field(typeof(RadarChaff), "layermask");

        private static readonly FieldInfo fi_irFlareParticles = AccessTools.Field(typeof(IRFlare), "flareParticles");
        private static readonly FieldInfo fi_irSmokeParticles = AccessTools.Field(typeof(IRFlare), "smokeParticles");
        private static readonly FieldInfo fi_irLayermask = AccessTools.Field(typeof(IRFlare), "layermask");

        private static readonly Func<CountermeasureManager, IList> f_stations =
            FastReflection.CreateFieldGetter<CountermeasureManager, IList>("countermeasureStations");
        private static readonly Func<object, IList> f_stationCountermeasures =
            FastReflection.CreateFieldGetter<IList>(AccessTools.Inner(typeof(CountermeasureManager), "CountermeasureStation"), "countermeasures");

        private static GameObject s_templateRoot;
        private static GameObject s_template;
        private static bool s_templateFailed;
        private static bool s_reordering;

        // Instance ids of the chaff ejectors that this mod added (Attach). An aircraft of another mod can have its own
        // ChaffEjector (MiG-29 mod): that station is not ours, so the network gate and the station order do not apply to it.
        private static readonly HashSet<int> s_ownEjectors = new HashSet<int>();

        /// <summary>
        /// The chaff station may be selected and fired here. All computers must have the chaff station: the host only when
        /// every client runs the mod, a client only on a host with the mod (else a station index of chaff would not exist
        /// on the other side).
        /// </summary>
        public static bool Usable
        {
            get
            {
                if (!CnoEvilConfig.ChaffEnabled.Value || !CnoEvilConfig.IsModActive) return false;
                switch (CnoEvilNet.Role)
                {
                    case NetRole.Authority: return !CnoEvilNet.HasClientsWithoutMod;
                    case NetRole.Mirror: return true;
                    default: return false;
                }
            }
        }

        /// <summary>
        /// Adds the chaff dispenser to an aircraft with flares (Aircraft.Awake). Runs also while the mod is off, so the
        /// station list is the same on every computer with the mod.
        /// </summary>
        public static void Attach(Aircraft aircraft)
        {
            // The M2K plugin adds its own chaff ejector (Spirale) in M2KEWSuite.Start: it is the chaff station of the
            // aircraft (ADR-64). AttachInstead adds this one if the plugin cannot.
            if (M2KCompat.WillAddChaff(aircraft)) return;
            AttachInstead(aircraft);
        }

        /// <summary>
        /// Adds the chaff dispenser without the M2K check: M2KEW_BuildChaff_Patch, when the plugin made no ejector.
        /// </summary>
        public static void AttachInstead(Aircraft aircraft)
        {
            if (aircraft == null || fi_chaffPrefab == null || fi_chaffPoints == null || fi_flarePoints == null) return;
            ChaffEjector[] existing = aircraft.GetComponentsInChildren<ChaffEjector>(true);
            for (int i = 0; i < existing.Length; i++)
            {
                if (!DecoyDispenserSetup.IsDecoyEjector(existing[i])) return;
            }
            FlareEjector flare = aircraft.GetComponentInChildren<FlareEjector>(true);
            if (flare == null) return;

            GameObject template = GetTemplate(flare);
            if (template == null) return;

            Array flarePoints = fi_flarePoints.GetValue(flare) as Array;
            if (flarePoints == null || flarePoints.Length == 0) return;
            ChaffEjector.EjectionPoint[] points = new ChaffEjector.EjectionPoint[flarePoints.Length];
            for (int i = 0; i < flarePoints.Length; i++)
            {
                FlareEjector.EjectionPoint fp = (FlareEjector.EjectionPoint)flarePoints.GetValue(i);
                ChaffEjector.EjectionPoint cp = new ChaffEjector.EjectionPoint();
                if (fp != null)
                {
                    cp.part = fp.part;
                    cp.transform = fp.transform;
                    cp.sound = fp.sound;
                }
                points[i] = cp;
            }

            // Inactive while the fields are set: ChaffEjector.Awake copies ammo to maxAmmo and Countermeasure.Awake registers
            // the station with the countermeasure manager of the aircraft
            GameObject go = new GameObject("CnoEvil_ChaffEjector");
            go.SetActive(false);
            go.transform.SetParent(flare.transform, false);
            ChaffEjector ejector = go.AddComponent<ChaffEjector>();

            fi_chaffPrefab.SetValue(ejector, template);
            fi_chaffPoints.SetValue(ejector, points);
            if (fi_chaffDoors != null) fi_chaffDoors.SetValue(ejector, Array.CreateInstance(fi_chaffDoors.FieldType.GetElementType(), 0));
            SetFloat(fi_chaffInterval, ejector, Mathf.Max(0.05f, CnoEvilConfig.ChaffEjectionInterval_s.Value));
            if (fi_chaffGrouping != null) fi_chaffGrouping.SetValue(ejector, 1);
            CopyField(fi_flareVelocity, flare, fi_chaffVelocity, ejector);
            CopyField(fi_flareVelocityVariance, flare, fi_chaffVelocityVariance, ejector);
            CopyField(fi_flareSound, flare, fi_chaffSound, ejector);
            CopyField(fi_flareVolume, flare, fi_chaffVolume, ejector);

            ejector.displayName = StationName;
            ejector.displayImage = flare.displayImage;
            ejector.chargeable = false;
            ejector.ammo = CountermeasureLoadout.Default(aircraft.definition).Chaff;
            ejector.aircraft = aircraft;
            s_ownEjectors.Add(ejector.GetInstanceID());

            go.SetActive(true);
        }

        /// <summary>
        /// The chaff ejector that this mod added to the aircraft (not a ChaffEjector of the aircraft prefab or of a pod).
        /// </summary>
        public static bool IsOwnChaffEjector(Countermeasure countermeasure)
        {
            return (object)countermeasure != null && countermeasure is ChaffEjector &&
                   s_ownEjectors.Contains(countermeasure.GetInstanceID());
        }

        /// <summary>
        /// The chaff station of this mod (its first countermeasure is the ejector of Attach).
        /// </summary>
        public static bool IsOwnChaffStation(object station)
        {
            return IsOwnChaffEjector(FirstCountermeasure(station));
        }

        public static bool IsOwnChaffIndex(CountermeasureManager manager, int index)
        {
            IList stations = Stations(manager);
            return stations != null && index >= 0 && index < stations.Count && IsOwnChaffStation(stations[index]);
        }

        /// <summary>
        /// Unit.OnDestroy: forget the chaff ejector of a destroyed aircraft.
        /// </summary>
        public static void RemoveUnit(Unit unit)
        {
            if ((object)unit == null || s_ownEjectors.Count == 0 || !(unit is Aircraft)) return;
            ChaffEjector[] ejectors = unit.GetComponentsInChildren<ChaffEjector>(true);
            for (int i = 0; i < ejectors.Length; i++)
            {
                if ((object)ejectors[i] != null) s_ownEjectors.Remove(ejectors[i].GetInstanceID());
            }
        }

        public static void ClearAll()
        {
            s_ownEjectors.Clear();
        }

        /// <summary>
        /// RadarChaff.LaunchChaff: the current config values for the new cloud, and the particles in the floating-origin frame.
        /// </summary>
        public static void OnLaunched(RadarChaff chaff)
        {
            if (chaff == null) return;
            float drag = Mathf.Max(0.001f, CnoEvilConfig.ChaffDrag.Value);
            SetFloat(fi_rcTime, chaff, Mathf.Max(1.0f, CnoEvilConfig.ChaffLifetime_s.Value));
            SetFloat(fi_rcDrag, chaff, drag);
            // Vanilla subtracts gravityVector from the velocity: up = falling
            if (fi_rcGravity != null) fi_rcGravity.SetValue(chaff, new Vector3(0.0f, drag * DipoleFallSpeed * DipoleFallSpeed, 0.0f));

            // The cloud object turns to its velocity every frame: the particles live in the parent (Datum.origin) frame,
            // so they stay where they were released and move with the floating origin
            Transform frame = chaff.transform.parent;
            ParticleSystem[] systems = chaff.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < systems.Length; i++)
            {
                ParticleSystem.MainModule main = systems[i].main;
                if (frame != null)
                {
                    main.simulationSpace = ParticleSystemSimulationSpace.Custom;
                    main.customSimulationSpace = frame;
                }
                else
                {
                    main.simulationSpace = ParticleSystemSimulationSpace.World;
                }
            }
        }

        // ---------------------------------------------------------------- stations

        /// <summary>
        /// True when the first countermeasure of this station (CountermeasureManager.CountermeasureStation) is a ChaffEjector
        /// with chaff (not the decoy ejector, DecoyDispenserSetup).
        /// </summary>
        public static bool IsChaffStation(object station)
        {
            Countermeasure first = FirstCountermeasure(station);
            return first is ChaffEjector && !DecoyDispenserSetup.IsDecoyEjector(first);
        }

        public static Countermeasure FirstCountermeasure(object station)
        {
            if (station == null || f_stationCountermeasures == null) return null;
            IList list = f_stationCountermeasures(station);
            return (list != null && list.Count > 0) ? list[0] as Countermeasure : null;
        }

        /// <summary>
        /// Sum of the ammo of all countermeasures of this station (live value; the station ammo field is only recounted
        /// after a fire).
        /// </summary>
        public static int StationAmmo(object station)
        {
            if (station == null || f_stationCountermeasures == null) return 0;
            IList list = f_stationCountermeasures(station);
            if (list == null) return 0;
            int sum = 0;
            for (int i = 0; i < list.Count; i++)
            {
                Countermeasure cm = list[i] as Countermeasure;
                if (cm != null) sum += cm.ammo;
            }
            return sum;
        }

        public static IList Stations(CountermeasureManager manager)
        {
            return (manager != null && f_stations != null) ? f_stations(manager) : null;
        }

        /// <summary>
        /// Vanilla sorts the stations by name after each registration: the chaff station of this mod goes back to the end,
        /// then the decoy station after it (vanilla, chaff, decoy). A chaff station of the aircraft prefab (MiG-29 mod)
        /// keeps its vanilla place: a computer without CNO-EVIL has the same index for it.
        /// </summary>
        public static void MoveChaffStationsLast(CountermeasureManager manager)
        {
            if (s_reordering) return;
            IList stations = Stations(manager);
            if (stations == null || stations.Count < 2) return;
            s_reordering = true;
            try
            {
                MoveToEnd(stations, false);
                MoveToEnd(stations, true);
            }
            finally
            {
                s_reordering = false;
            }
        }

        private static void MoveToEnd(IList stations, bool decoy)
        {
            int count = stations.Count;
            int moved = 0;
            for (int i = 0; i < count - moved; )
            {
                object station = stations[i];
                if (decoy ? DecoyDispenserSetup.IsDecoyStation(station) : IsOwnChaffStation(station))
                {
                    stations.RemoveAt(i);
                    stations.Add(station);
                    moved++;
                }
                else
                {
                    i++;
                }
            }
        }

        // ---------------------------------------------------------------- prefab

        private static GameObject GetTemplate(FlareEjector flare)
        {
            if (s_template != null) return s_template;
            if (s_templateFailed) return null;

            try
            {
                GameObject flarePrefab = (fi_flarePrefab != null) ? fi_flarePrefab.GetValue(flare) as GameObject : null;
                IRFlare irFlare = (flarePrefab != null) ? flarePrefab.GetComponentInChildren<IRFlare>(true) : null;
                Material glintMaterial = ParticleMaterial(irFlare, fi_irFlareParticles);
                Material hazeMaterial = ParticleMaterial(irFlare, fi_irSmokeParticles);
                if (glintMaterial == null) glintMaterial = hazeMaterial;
                if (glintMaterial == null)
                {
                    s_templateFailed = true;
                    if (CnoEvilPlugin.Log != null) CnoEvilPlugin.Log.LogWarning("Chaff: no flare particle material found, chaff is not available.");
                    return null;
                }

                // Inactive root: the template does not run; its clones (Spawner.SpawnLocal -> Instantiate) are active
                s_templateRoot = new GameObject("CnoEvil_ChaffTemplates");
                s_templateRoot.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(s_templateRoot);

                GameObject template = new GameObject("CnoEvil_Chaff");
                template.transform.SetParent(s_templateRoot.transform, false);
                RadarChaff chaff = template.AddComponent<RadarChaff>();

                float life = Mathf.Max(1.0f, CnoEvilConfig.ChaffLifetime_s.Value);
                ParticleSystem glints = template.AddComponent<ParticleSystem>();
                BuildGlints(glints, glintMaterial, life);
                if (hazeMaterial != null)
                {
                    GameObject hazeObject = new GameObject("Haze");
                    hazeObject.transform.SetParent(template.transform, false);
                    BuildHaze(hazeObject.AddComponent<ParticleSystem>(), hazeMaterial, life);
                }

                if (fi_rcParticles != null) fi_rcParticles.SetValue(chaff, glints);
                if (fi_rcLayermask != null && fi_irLayermask != null && irFlare != null) fi_rcLayermask.SetValue(chaff, fi_irLayermask.GetValue(irFlare));
                SetFloat(fi_rcTime, chaff, life);

                s_template = template;
                return s_template;
            }
            catch (Exception ex)
            {
                s_templateFailed = true;
                if (CnoEvilPlugin.Log != null) CnoEvilPlugin.Log.LogWarning("Chaff: prefab build failed, chaff is not available: " + ex.Message);
                return null;
            }
        }

        private static Material ParticleMaterial(IRFlare flare, FieldInfo field)
        {
            if (flare == null || field == null) return null;
            ParticleSystem ps = field.GetValue(flare) as ParticleSystem;
            if (ps == null) return null;
            ParticleSystemRenderer renderer = ps.GetComponent<ParticleSystemRenderer>();
            return (renderer != null) ? renderer.sharedMaterial : null;
        }

        /// <summary>
        /// Silver glints: a burst of small strips released at the dispenser that spread to a cloud of about 20 m and almost
        /// stop (dipoles), then sink slowly. The size curve oscillates about 5 times per second and each glint has its own
        /// lifetime, so the cloud twinkles.
        /// </summary>
        private static void BuildGlints(ParticleSystem ps, Material material, float life)
        {
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            ParticleSystem.MainModule main = ps.main;
            main.duration = life + 1.0f;
            main.loop = false;
            main.playOnAwake = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(life * 0.6f, life);
            main.startSpeed = new ParticleSystem.MinMaxCurve(4.0f, 14.0f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.25f, 0.6f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0.0f, Mathf.PI * 2.0f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.75f, 0.78f, 0.82f, 0.85f), new Color(1.0f, 1.0f, 1.0f, 1.0f));
            main.gravityModifier = 0.01f;
            main.maxParticles = GlintCount;

            ParticleSystem.EmissionModule emission = ps.emission;
            emission.rateOverTime = 0.0f;
            emission.SetBursts(new ParticleSystem.Burst[]
            {
                new ParticleSystem.Burst(0.0f, (short)(GlintCount * 2 / 3)),
                new ParticleSystem.Burst(0.12f, (short)(GlintCount / 3))
            });

            ParticleSystem.ShapeModule shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 1.0f;

            ParticleSystem.LimitVelocityOverLifetimeModule limit = ps.limitVelocityOverLifetime;
            limit.enabled = true;
            limit.limit = 0.6f;
            limit.dampen = 0.06f;

            ParticleSystem.RotationOverLifetimeModule rotation = ps.rotationOverLifetime;
            rotation.enabled = true;
            rotation.z = new ParticleSystem.MinMaxCurve(-8.0f, 8.0f);

            ParticleSystem.SizeOverLifetimeModule size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1.0f, TwinkleCurve(life));

            ParticleSystem.ColorOverLifetimeModule color = ps.colorOverLifetime;
            color.enabled = true;
            Gradient fade = new Gradient();
            fade.SetKeys(
                new GradientColorKey[] { new GradientColorKey(Color.white, 0.0f), new GradientColorKey(Color.white, 1.0f) },
                new GradientAlphaKey[] { new GradientAlphaKey(0.0f, 0.0f), new GradientAlphaKey(1.0f, 0.02f), new GradientAlphaKey(0.8f, 0.5f), new GradientAlphaKey(0.0f, 1.0f) });
            color.color = new ParticleSystem.MinMaxGradient(fade);

            ParticleSystemRenderer renderer = ps.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.sharedMaterial = material;
        }

        /// <summary>
        /// Faint haze: a few large, almost transparent puffs that show the bloom of the cloud.
        /// </summary>
        private static void BuildHaze(ParticleSystem ps, Material material, float life)
        {
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            ParticleSystem.MainModule main = ps.main;
            main.duration = life + 1.0f;
            main.loop = false;
            main.playOnAwake = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(life * 0.4f, life * 0.7f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(2.0f, 6.0f);
            main.startSize = new ParticleSystem.MinMaxCurve(5.0f, 11.0f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0.0f, Mathf.PI * 2.0f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.85f, 0.87f, 0.9f, 0.10f), new Color(0.95f, 0.95f, 1.0f, 0.16f));
            main.maxParticles = HazeCount;

            ParticleSystem.EmissionModule emission = ps.emission;
            emission.rateOverTime = 0.0f;
            emission.SetBursts(new ParticleSystem.Burst[] { new ParticleSystem.Burst(0.05f, (short)HazeCount) });

            ParticleSystem.ShapeModule shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 2.0f;

            ParticleSystem.LimitVelocityOverLifetimeModule limit = ps.limitVelocityOverLifetime;
            limit.enabled = true;
            limit.limit = 0.4f;
            limit.dampen = 0.05f;

            ParticleSystem.SizeOverLifetimeModule size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1.0f, AnimationCurve.EaseInOut(0.0f, 0.4f, 1.0f, 1.6f));

            ParticleSystem.ColorOverLifetimeModule color = ps.colorOverLifetime;
            color.enabled = true;
            Gradient fade = new Gradient();
            fade.SetKeys(
                new GradientColorKey[] { new GradientColorKey(Color.white, 0.0f), new GradientColorKey(Color.white, 1.0f) },
                new GradientAlphaKey[] { new GradientAlphaKey(0.0f, 0.0f), new GradientAlphaKey(1.0f, 0.08f), new GradientAlphaKey(0.0f, 1.0f) });
            color.color = new ParticleSystem.MinMaxGradient(fade);

            ParticleSystemRenderer renderer = ps.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.sharedMaterial = material;
        }

        private static AnimationCurve TwinkleCurve(float life)
        {
            const float TwinkleHz = 5.0f;
            int halfCycles = Mathf.Clamp(Mathf.RoundToInt(life * TwinkleHz * 2.0f), 4, 200);
            Keyframe[] keys = new Keyframe[halfCycles + 1];
            for (int i = 0; i <= halfCycles; i++)
            {
                float t = (float)i / halfCycles;
                keys[i] = new Keyframe(t, (i % 2 == 0) ? 1.0f : 0.2f);
            }
            return new AnimationCurve(keys);
        }

        private static void SetFloat(FieldInfo field, object target, float value)
        {
            if (field != null && target != null) field.SetValue(target, value);
        }

        private static void CopyField(FieldInfo from, object source, FieldInfo to, object target)
        {
            if (from == null || to == null || source == null || target == null) return;
            to.SetValue(target, from.GetValue(source));
        }
    }

    /// <summary>
    /// Run-time binding to the plugins of the MiG-29 mods (MiG-29 Fulcrum: MiG29Instruments, MiG-29SMT:
    /// MiG29SMTInstruments). CNO-EVIL has no reference to their assemblies: the types are found by name, and nothing
    /// happens when a plugin is not installed. Both plugins have the same API (Chaff, InstrumentDriver, FlightState).
    /// The aircraft uses the vanilla TacScreen tacScreen_Multirole1 (PortalMFD layout of that prefab) and has its own
    /// FlareEjector and ChaffEjector; its countermeasure pods have their own ejectors (CountermeasureLoadout).
    /// The plugin has its own chaff model for chaff of the MiG: a cloud list per seeker (Chaff.OnChaff, a prefix on the
    /// vanilla OnChaff of the radar seekers), jamming of SARH and ARH seekers and a move of the ARH aimpoint to the cloud
    /// (Chaff.Seduce, postfixes on ARHSeeker.DatalinkMode / TerminalMode). While CNO-EVIL is active, its chaff model
    /// (ChaffSeduction) replaces the vanilla OnChaff, so the plugin model is stopped: the chaff would act two times
    /// (MiG29Chaff_* patches in ChaffPatches). The chaff programme of the plugin (Chaff.OnFire, several pairs per press)
    /// does not apply to a CNO-EVIL countermeasure program, which counts each cartridge. The cockpit displays of the
    /// plugin read CNO-EVIL sensor data (MiG29InstrumentDriver_Sample_Patch in CockpitWarningLightsPatches).
    /// </summary>
    public static class MiG29Compat
    {
        public const string FulcrumGuid = "iornman.mig29.instruments";
        public const string SmtGuid = "iornman.mig29smt.instruments";

        private static readonly string[] s_namespaces = { "MiG29Instruments.", "MiG29SMTInstruments." };

        /// <summary>
        /// The type with this short name in each installed plugin.
        /// </summary>
        public static List<Type> Types(string shortName)
        {
            List<Type> list = new List<Type>();
            for (int i = 0; i < s_namespaces.Length; i++)
            {
                Type t = AccessTools.TypeByName(s_namespaces[i] + shortName);
                if (t != null) list.Add(t);
            }
            return list;
        }

        /// <summary>
        /// The method with this name (the overload with this parameter types) of the type in each installed plugin.
        /// </summary>
        public static List<MethodBase> Methods(string typeName, string name, Type[] parameters)
        {
            List<MethodBase> list = new List<MethodBase>();
            List<Type> types = Types(typeName);
            for (int i = 0; i < types.Count; i++)
            {
                MethodInfo m = AccessTools.Method(types[i], name, parameters);
                if (m != null) list.Add(m);
            }
            return list;
        }
    }

    /// <summary>
    /// Soft dependency on the plugins of the Mirage 2000D mod (M2K.dll: M2KEW, M2KMfd, M2KRadar and others, ADR-64).
    /// CNO-EVIL has no reference to the assembly: the types are found by name (global namespace), and nothing happens
    /// when the mod is not installed.
    /// - M2KEW adds M2KEWSuite to each aircraft of its key list (M2KEWPlugin.IsMirage, Aircraft.Awake). The suite makes
    ///   its own chaff ejector (Spirale, M2KEWSuite.BuildChaff in Start); it is the chaff station of the aircraft, so
    ///   Attach adds no second station, and M2KEW_BuildChaff_Patch gives Spirale its share of the budget.
    /// - M2KMfd and M2KVtb draw their pages on the TacScreen canvas (TacScreenPortalMFD hides them under the portal);
    ///   while CNO-EVIL is active they show the sensor knowledge of CNO-EVIL (patches in CockpitWarningLightsPatches).
    /// </summary>
    public static class M2KCompat
    {
        public const string EwGuid = "paul.nuclearoption.m2kew";
        public const string MfdGuid = "m2k.mfd";
        public const string RadarGuid = "m2k.radar";

        private static bool s_resolved;
        private static Func<Unit, bool> s_isMirage;

        public static Type FindType(string name)
        {
            return AccessTools.TypeByName(name);
        }

        /// <summary>
        /// The declared method of the type, as a list for TargetMethods (empty when the mod is not installed).
        /// </summary>
        public static List<MethodBase> Method(string typeName, string name, Type[] parameters)
        {
            List<MethodBase> list = new List<MethodBase>();
            Type t = FindType(typeName);
            MethodInfo m = (t != null) ? AccessTools.DeclaredMethod(t, name, parameters) : null;
            if (m != null) list.Add(m);
            return list;
        }

        /// <summary>
        /// M2KEW will add its chaff ejector to this aircraft (M2KEWPlugin.IsMirage: the jsonKey is in its key list).
        /// </summary>
        public static bool WillAddChaff(Aircraft aircraft)
        {
            if (aircraft == null) return false;
            if (!s_resolved)
            {
                s_resolved = true;
                try
                {
                    Type plugin = FindType("M2KEWPlugin");
                    MethodInfo m = (plugin != null) ? AccessTools.DeclaredMethod(plugin, "IsMirage", new Type[] { typeof(Unit) }) : null;
                    if (m != null && m.IsStatic && m.ReturnType == typeof(bool))
                        s_isMirage = (Func<Unit, bool>)Delegate.CreateDelegate(typeof(Func<Unit, bool>), m);
                }
                catch (Exception ex)
                {
                    PatchErrorLog.Once("M2KCompat.IsMirage", ex);
                }
            }
            if (s_isMirage == null) return false;
            try
            {
                return s_isMirage(aircraft);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("M2KCompat.WillAddChaff", ex);
                return false;
            }
        }
    }
}
