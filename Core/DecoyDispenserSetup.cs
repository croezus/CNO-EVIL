using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using CnoEvil.Components;
using CnoEvil.Config;

namespace CnoEvil.Core
{
    /// <summary>
    /// Expendable active decoy (DRFM repeater in a flare cartridge). Each aircraft with a FlareEjector gets a second
    /// ChaffEjector with DefaultDecoyCount decoys (CountermeasureLoadout sets the count of the player choice): the vanilla chaff ejector already fires over the network
    /// (CmdCountermeasures / RpcCountermeasures) and spawns a ballistic RadarChaff on every computer. The decoy prefab is
    /// built at run time: a RadarChaff with cartridge drag and full gravity, a tumbling 55 mm cylinder, an ejection puff
    /// and a beacon (DecoyVisual). The decoy station is the last station, after chaff (ChaffDispenserSetup.MoveChaffStationsLast).
    /// The physics is in ChaffCloud (equivalent RCS of a repeater) and ChaffSeduction.
    /// </summary>
    public static class DecoyDispenserSetup
    {
        public const string StationName = "DRFM Decoy";
        public const string HudLabel = "DECOY";

        // Seconds the decoy falls after its battery is empty before the object is removed
        private const float FallTail_s = 4.0f;
        private const float Gravity = 9.81f;
        // Cartridge size (m): 55 mm diameter, 140 mm length
        private const float BodyDiameter = 0.055f;
        private const float BodyLength = 0.14f;
        private const float BeaconBlinkHz = 2.0f;
        // AI: a decoy against a missile that is more than this angle from the nose (it falls behind the aircraft)
        private const float AiMinMissileAngle_deg = 60.0f;

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

        // AI: missiles that already got a decoy (one decoy per missile)
        private static readonly HashSet<int> s_aiDecoyMissiles = new HashSet<int>();
        // AI: aircraft id -> missile id of the decoy station selection that has not ejected yet
        private static readonly Dictionary<int, int> s_aiPendingDecoy = new Dictionary<int, int>();

        private static GameObject s_templateRoot;
        private static GameObject s_template;
        private static bool s_templateFailed;

        /// <summary>
        /// The decoy station may be selected and fired here (same network rule as chaff).
        /// </summary>
        public static bool Usable
        {
            get
            {
                if (!CnoEvilConfig.DecoyEnabled.Value || !CnoEvilConfig.IsModActive) return false;
                switch (CnoEvilNet.Role)
                {
                    case NetRole.Authority: return !CnoEvilNet.HasClientsWithoutMod;
                    case NetRole.Mirror: return true;
                    default: return false;
                }
            }
        }

        /// <summary>
        /// True for the decoy ejector (a ChaffEjector with the decoy station name).
        /// </summary>
        public static bool IsDecoyEjector(Countermeasure countermeasure)
        {
            return countermeasure is ChaffEjector && countermeasure.displayName == StationName;
        }

        public static bool IsDecoyStation(object station)
        {
            return IsDecoyEjector(ChaffDispenserSetup.FirstCountermeasure(station));
        }

        public static bool IsDecoyIndex(CountermeasureManager manager, int index)
        {
            System.Collections.IList stations = ChaffDispenserSetup.Stations(manager);
            return stations != null && index >= 0 && index < stations.Count && IsDecoyStation(stations[index]);
        }

        /// <summary>
        /// Adds the decoy dispenser to an aircraft with flares (Aircraft.Awake, after the chaff dispenser). Runs also while
        /// the mod is off, so the station list is the same on every computer with the mod.
        /// </summary>
        public static void Attach(Aircraft aircraft)
        {
            if (aircraft == null || fi_chaffPrefab == null || fi_chaffPoints == null || fi_flarePoints == null) return;
            ChaffEjector[] existing = aircraft.GetComponentsInChildren<ChaffEjector>(true);
            for (int i = 0; i < existing.Length; i++)
            {
                if (IsDecoyEjector(existing[i])) return;
            }
            FlareEjector flare = aircraft.GetComponentInChildren<FlareEjector>(true);
            if (flare == null) return;

            Array flarePoints = fi_flarePoints.GetValue(flare) as Array;
            if (flarePoints == null || flarePoints.Length == 0) return;
            GameObject template = GetTemplate(flare, flarePoints);
            if (template == null) return;

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

            // Inactive while the fields are set (see ChaffDispenserSetup.Attach)
            GameObject go = new GameObject("CnoEvil_DecoyEjector");
            go.SetActive(false);
            go.transform.SetParent(flare.transform, false);
            ChaffEjector ejector = go.AddComponent<ChaffEjector>();

            fi_chaffPrefab.SetValue(ejector, template);
            fi_chaffPoints.SetValue(ejector, points);
            if (fi_chaffDoors != null) fi_chaffDoors.SetValue(ejector, Array.CreateInstance(fi_chaffDoors.FieldType.GetElementType(), 0));
            SetFloat(fi_chaffInterval, ejector, Mathf.Max(0.1f, CnoEvilConfig.DecoyEjectionInterval_s.Value));
            if (fi_chaffGrouping != null) fi_chaffGrouping.SetValue(ejector, 1);
            CopyField(fi_flareVelocity, flare, fi_chaffVelocity, ejector);
            CopyField(fi_flareVelocityVariance, flare, fi_chaffVelocityVariance, ejector);
            CopyField(fi_flareSound, flare, fi_chaffSound, ejector);
            CopyField(fi_flareVolume, flare, fi_chaffVolume, ejector);

            ejector.displayName = StationName;
            ejector.displayImage = flare.displayImage;
            ejector.chargeable = false;
            ejector.ammo = CountermeasureLoadout.Default().Decoys;
            ejector.aircraft = aircraft;

            go.SetActive(true);
        }

        /// <summary>
        /// RadarChaff.LaunchChaff of a decoy: the current config values, and the ejection puff in the floating-origin frame
        /// (the body and the beacon move with the decoy).
        /// </summary>
        public static void OnLaunched(RadarChaff decoy, DecoyVisual visual)
        {
            if (decoy == null) return;
            SetFloat(fi_rcTime, decoy, TotalLifetime());
            SetFloat(fi_rcDrag, decoy, Mathf.Max(0.0f, CnoEvilConfig.DecoyDrag.Value));
            // Vanilla subtracts gravityVector from the velocity: up = falling
            if (fi_rcGravity != null) fi_rcGravity.SetValue(decoy, new Vector3(0.0f, Gravity, 0.0f));

            Transform frame = decoy.transform.parent;
            ParticleSystem[] systems = decoy.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < systems.Length; i++)
            {
                if (visual != null && systems[i] == visual.Beacon) continue;
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

        // ---------------------------------------------------------------- AI

        /// <summary>
        /// AI: a decoy against this missile now. Not yet used against it, and the missile is not head-on while the seeker
        /// tracks the leading edge (the decoy falls behind the aircraft, farther from a missile in front).
        /// </summary>
        public static bool AiWantsDecoy(Aircraft aircraft, Missile missile)
        {
            if (aircraft == null || missile == null) return false;
            if (s_aiDecoyMissiles.Contains(missile.GetInstanceID())) return false;
            if (!CnoEvilConfig.DecoyLeadingEdgeRejection.Value) return true;
            Vector3 toMissile = missile.transform.position - aircraft.transform.position;
            return Vector3.Angle(aircraft.transform.forward, toMissile) > AiMinMissileAngle_deg;
        }

        /// <summary>
        /// AI selected the decoy station against this missile. The missile counts as served only when a decoy is ejected
        /// (AiDecoyLaunched): vanilla fires the station only in the last seconds of the beam, and the choice repeats each
        /// second until then.
        /// </summary>
        public static void AiDecoySelected(Aircraft aircraft, Missile missile)
        {
            if (aircraft != null && missile != null) s_aiPendingDecoy[aircraft.GetInstanceID()] = missile.GetInstanceID();
        }

        /// <summary>
        /// RadarChaff.LaunchChaff of a decoy: the missile that the aircraft selected the decoy against got its decoy.
        /// </summary>
        public static void AiDecoyLaunched(Aircraft aircraft)
        {
            if (aircraft == null) return;
            int key = aircraft.GetInstanceID();
            int missileId;
            if (!s_aiPendingDecoy.TryGetValue(key, out missileId)) return;
            s_aiPendingDecoy.Remove(key);
            s_aiDecoyMissiles.Add(missileId);
        }

        public static void ForgetMissile(int missileId)
        {
            s_aiDecoyMissiles.Remove(missileId);
        }

        public static void ForgetAircraft(int aircraftId)
        {
            s_aiPendingDecoy.Remove(aircraftId);
        }

        public static void ClearAll()
        {
            s_aiDecoyMissiles.Clear();
            s_aiPendingDecoy.Clear();
        }

        // ---------------------------------------------------------------- prefab

        private static float TotalLifetime()
        {
            return Mathf.Max(0.0f, CnoEvilConfig.DecoyArmDelay_s.Value) + Mathf.Max(0.5f, CnoEvilConfig.DecoyActiveTime_s.Value) + FallTail_s;
        }

        private static GameObject GetTemplate(FlareEjector flare, Array flarePoints)
        {
            if (s_template != null) return s_template;
            if (s_templateFailed) return null;

            try
            {
                GameObject flarePrefab = (fi_flarePrefab != null) ? fi_flarePrefab.GetValue(flare) as GameObject : null;
                IRFlare irFlare = (flarePrefab != null) ? flarePrefab.GetComponentInChildren<IRFlare>(true) : null;
                Material glowMaterial = ParticleMaterial(irFlare, fi_irFlareParticles);
                Material smokeMaterial = ParticleMaterial(irFlare, fi_irSmokeParticles);
                if (glowMaterial == null)
                {
                    s_templateFailed = true;
                    if (CnoEvilPlugin.Log != null) CnoEvilPlugin.Log.LogWarning("Decoy: no flare particle material found, decoys are not available.");
                    return null;
                }

                // Inactive root: the template does not run; its clones are active
                s_templateRoot = new GameObject("CnoEvil_DecoyTemplates");
                s_templateRoot.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(s_templateRoot);

                GameObject template = new GameObject("CnoEvil_Decoy");
                template.transform.SetParent(s_templateRoot.transform, false);
                RadarChaff decoy = template.AddComponent<RadarChaff>();
                DecoyVisual visual = template.AddComponent<DecoyVisual>();

                // Tumble node: vanilla RadarChaff turns the root to its velocity every frame
                GameObject tumble = new GameObject("Tumble");
                tumble.transform.SetParent(template.transform, false);
                visual.Tumble = tumble.transform;
                BuildBody(tumble.transform, BodyMaterial(flarePoints));

                GameObject beaconObject = new GameObject("Beacon");
                beaconObject.transform.SetParent(template.transform, false);
                ParticleSystem beacon = beaconObject.AddComponent<ParticleSystem>();
                BuildBeacon(beacon, glowMaterial);
                visual.Beacon = beacon;

                if (smokeMaterial != null)
                {
                    GameObject puffObject = new GameObject("Puff");
                    puffObject.transform.SetParent(template.transform, false);
                    BuildPuff(puffObject.AddComponent<ParticleSystem>(), smokeMaterial);
                }

                // Vanilla destroys the object when chaffTime ends only while chaffParticles plays: the looping beacon
                if (fi_rcParticles != null) fi_rcParticles.SetValue(decoy, beacon);
                if (fi_rcLayermask != null && fi_irLayermask != null && irFlare != null) fi_rcLayermask.SetValue(decoy, fi_irLayermask.GetValue(irFlare));
                SetFloat(fi_rcTime, decoy, TotalLifetime());

                s_template = template;
                return s_template;
            }
            catch (Exception ex)
            {
                s_templateFailed = true;
                if (CnoEvilPlugin.Log != null) CnoEvilPlugin.Log.LogWarning("Decoy: prefab build failed, decoys are not available: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// A lit (not particle) material of the aircraft part at the flare dispenser, as a dark grey copy without texture.
        /// Fallback: the built-in Standard shader.
        /// </summary>
        private static Material BodyMaterial(Array flarePoints)
        {
            Material source = null;
            for (int i = 0; i < flarePoints.Length && source == null; i++)
            {
                FlareEjector.EjectionPoint fp = (FlareEjector.EjectionPoint)flarePoints.GetValue(i);
                if (fp == null || fp.part == null) continue;
                MeshRenderer renderer = fp.part.GetComponentInChildren<MeshRenderer>(true);
                if (renderer != null) source = renderer.sharedMaterial;
            }

            Material material;
            if (source != null)
            {
                material = new Material(source);
            }
            else
            {
                Shader standard = Shader.Find("Standard");
                if (standard == null) return null;
                material = new Material(standard);
            }
            material.name = "CnoEvil_DecoyBody";
            if (material.HasProperty("_MainTex")) material.mainTexture = null;
            if (material.HasProperty("_Color")) material.color = new Color(0.22f, 0.23f, 0.24f, 1.0f);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", new Color(0.22f, 0.23f, 0.24f, 1.0f));
            return material;
        }

        /// <summary>
        /// 55 mm x 140 mm cylinder along the forward axis, with a lighter end cap.
        /// </summary>
        private static void BuildBody(Transform parent, Material material)
        {
            if (material == null) return;
            // Unity cylinder: height 2, diameter 1, along Y
            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            body.name = "Body";
            RemoveCollider(body);
            body.transform.SetParent(parent, false);
            body.transform.localRotation = Quaternion.Euler(90.0f, 0.0f, 0.0f);
            body.transform.localScale = new Vector3(BodyDiameter, BodyLength * 0.5f, BodyDiameter);
            body.GetComponent<MeshRenderer>().sharedMaterial = material;

            Material capMaterial = new Material(material);
            capMaterial.name = "CnoEvil_DecoyCap";
            Color cap = new Color(0.55f, 0.56f, 0.55f, 1.0f);
            if (capMaterial.HasProperty("_Color")) capMaterial.color = cap;
            if (capMaterial.HasProperty("_BaseColor")) capMaterial.SetColor("_BaseColor", cap);

            GameObject end = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            end.name = "Cap";
            RemoveCollider(end);
            end.transform.SetParent(parent, false);
            end.transform.localRotation = Quaternion.Euler(90.0f, 0.0f, 0.0f);
            end.transform.localPosition = new Vector3(0.0f, 0.0f, BodyLength * 0.5f);
            end.transform.localScale = new Vector3(BodyDiameter * 1.02f, 0.004f, BodyDiameter * 1.02f);
            end.GetComponent<MeshRenderer>().sharedMaterial = capMaterial;
        }

        private static void RemoveCollider(GameObject go)
        {
            Collider collider = go.GetComponent<Collider>();
            if (collider != null) UnityEngine.Object.DestroyImmediate(collider);
        }

        /// <summary>
        /// Faint red flash, BeaconBlinkHz per second, attached to the decoy. Loops for the whole lifetime; DecoyVisual
        /// switches the emission.
        /// </summary>
        private static void BuildBeacon(ParticleSystem ps, Material material)
        {
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            ParticleSystem.MainModule main = ps.main;
            main.duration = 1.0f;
            main.loop = true;
            main.playOnAwake = true;
            main.startLifetime = 0.12f;
            main.startSpeed = 0.0f;
            main.startSize = 0.7f;
            main.startColor = new Color(1.0f, 0.12f, 0.08f, 0.55f);
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.maxParticles = 2;

            ParticleSystem.EmissionModule emission = ps.emission;
            emission.rateOverTime = BeaconBlinkHz;

            ParticleSystem.ShapeModule shape = ps.shape;
            shape.enabled = false;

            ParticleSystem.ColorOverLifetimeModule color = ps.colorOverLifetime;
            color.enabled = true;
            Gradient fade = new Gradient();
            fade.SetKeys(
                new GradientColorKey[] { new GradientColorKey(Color.white, 0.0f), new GradientColorKey(Color.white, 1.0f) },
                new GradientAlphaKey[] { new GradientAlphaKey(1.0f, 0.0f), new GradientAlphaKey(1.0f, 0.5f), new GradientAlphaKey(0.0f, 1.0f) });
            color.color = new ParticleSystem.MinMaxGradient(fade);

            ParticleSystemRenderer renderer = ps.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.sharedMaterial = material;
        }

        /// <summary>
        /// Small grey puff of the ejection charge, left behind in the air.
        /// </summary>
        private static void BuildPuff(ParticleSystem ps, Material material)
        {
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            ParticleSystem.MainModule main = ps.main;
            main.duration = 0.5f;
            main.loop = false;
            main.playOnAwake = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.5f, 2.5f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.5f, 2.0f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.6f, 1.0f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0.0f, Mathf.PI * 2.0f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.7f, 0.7f, 0.7f, 0.30f), new Color(0.85f, 0.85f, 0.85f, 0.40f));
            main.maxParticles = 6;

            ParticleSystem.EmissionModule emission = ps.emission;
            emission.rateOverTime = 0.0f;
            emission.SetBursts(new ParticleSystem.Burst[] { new ParticleSystem.Burst(0.0f, (short)6) });

            ParticleSystem.ShapeModule shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.2f;

            ParticleSystem.SizeOverLifetimeModule size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1.0f, AnimationCurve.EaseInOut(0.0f, 1.0f, 1.0f, 3.0f));

            ParticleSystem.ColorOverLifetimeModule color = ps.colorOverLifetime;
            color.enabled = true;
            Gradient fade = new Gradient();
            fade.SetKeys(
                new GradientColorKey[] { new GradientColorKey(Color.white, 0.0f), new GradientColorKey(Color.white, 1.0f) },
                new GradientAlphaKey[] { new GradientAlphaKey(1.0f, 0.0f), new GradientAlphaKey(0.0f, 1.0f) });
            color.color = new ParticleSystem.MinMaxGradient(fade);

            ParticleSystemRenderer renderer = ps.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.sharedMaterial = material;
        }

        private static Material ParticleMaterial(IRFlare flare, FieldInfo field)
        {
            if (flare == null || field == null) return null;
            ParticleSystem ps = field.GetValue(flare) as ParticleSystem;
            if (ps == null) return null;
            ParticleSystemRenderer renderer = ps.GetComponent<ParticleSystemRenderer>();
            return (renderer != null) ? renderer.sharedMaterial : null;
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
}
