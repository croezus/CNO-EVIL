using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using NuclearOption.SavedMission;
using RoadPathfinding;
using UnityEngine;
using CnoEvil.Config;

namespace CnoEvil.Core
{
    /// <summary>
    /// A layered air defense at each mission start (ADR-58). The mission file has fixed coordinates, so the host plans new
    /// positions for the units that the mission does not need at an exact place. The units of the mission are the pool: the
    /// plan adds none and removes none. A complex is made from vanilla links and unit types (DynamicPlacement.Complexes),
    /// each complex belongs to the core of an object or is an outpost (DynamicPlacement.Nodes), and the echelons of the core
    /// get their places around the object (DynamicPlacement.Echelons). The site checks in this file use the checks of vanilla
    /// GroundVehicle.NavigateOffRoad (normal . up, sea level). Each unit with a sensor must have a clear view from its place:
    /// the vanilla target search starts with a terrain ray. A core complex with no site on its ring tries any land in the core in a small formation; with no valid site it keeps the position of the mission file.
    ///
    /// The plan is a table, not a change of the Mission: Spawner_TrySpawnSavedUnitInternal_Patch puts the new position in
    /// the SavedUnit for the one spawn call and restores it, so the mission data stay the author's data. Clients get
    /// the position from the spawned unit (NetworkstartPosition), so no network message is necessary.
    /// </summary>
    public static partial class DynamicPlacement
    {
        private const int CandidateAttempts = 48;
        private const int ScoredCandidates = 8;
        private const int RadarCandidateAttempts = 96;
        private const int RadarScoredCandidates = 16;
        // Part of the search radius that a battery must move at least, so that a new start is a new position
        private const float MinShiftFraction = 0.35f;
        private const float MaxMinShiftMeters = 2000.0f;
        private const float MaxBatteryYawDegrees = 60.0f;

        // The formation of a site: the distances of the mission file from the anchor, multiplied by a random value
        private const float MinFormationScale = 0.5f;
        private const float MaxFormationScale = 1.25f;
        // A unit that finds no place at its point searches around it, not farther than this from the anchor
        private const float FormationReachMeters = 300.0f;
        private const float FormationReachFactor = 1.25f;
        private const int AnchorSearchAttempts = 3;
        private const int UnitSearchAttempts = 8;
        private const float UnitSearchStepMeters = 30.0f;
        // A truck stays next to its unit, a tractor at its trailer
        private const int TruckSearchAttempts = 3;
        private const float TruckSearchStepMeters = 5.0f;
        // A truck this close to its unit is a tractor at its trailer: the offset stays. A truck farther away closes up with the formation
        private const float RigidTruckMeters = 30.0f;

        // Vanilla GroundVehicle.NavigateOffRoad accepts a point with normal . up above 0.9
        private const float MinGroundNormalY = 0.9f;
        private const float SeaClearanceMeters = 2.0f;
        private const float RoadClearanceMeters = 15.0f;
        private const float RunwayClearanceMeters = 200.0f;
        private const float TaxiClearanceMeters = 40.0f;
        // Half the width of the vanilla tree group mesh (TreeClump1). One position in TreeRenderer.PositionData is one group
        private const float TreeClearanceMeters = 25.0f;
        private const float MinUnitRadiusMeters = 4.0f;
        // A vehicle higher than this above the terrain stands on something
        private const float OnTerrainToleranceMeters = 4.0f;
        // A vehicle this close to the edge of a building, ship or container is part of that position (revetment, shelter, deck)
        private const float AttachedClearanceMeters = 4.0f;

        // A move does not take a battery closer to the enemy than this, or than the author's position if that is closer
        private const float EnemyStandoffMeters = 2000.0f;
        // A long move also keeps this part of the author's distance, so that a radar station stays behind the front
        private const float EnemyStandoffFraction = 0.75f;

        // GroundVehicle.TryAttachToSurface casts a 10 m ray down from the spawn position
        private const float VehicleDropMeters = 1.0f;

        private const int HorizonAzimuths = 16;
        private const float AntennaHeightMeters = 12.0f;
        private const float HorizonRangeMeters = 15000.0f;
        private const float HorizonRiseMeters = 300.0f;
        private const float OpticsHeightMeters = 4.0f;
        // For a unit that has no vanilla sensor value
        private const float DefaultVisualRangeMeters = 5000.0f;
        private const int ElevationSamples = 8;
        private const float ElevationSampleMeters = 600.0f;
        private const float ElevationFullScoreMeters = 150.0f;
        // A sensor may stand this far below the mean ground around it: flat ground must pass, a hollow must not
        private const float LowGroundToleranceMeters = 3.0f;
        // The view that a site must give, or the view of the author's position if that is less
        private const float RequiredView = 0.75f;

        private const int ClimbSteps = 8;
        private const int ClimbDirections = 8;
        private const float ClimbStepMeters = 200.0f;
        // Two radar sites closer than this are the same hill: the better one stays, so that the choice has real options
        private const float SameHillMeters = 300.0f;

        private enum SiteClass
        {
            None,
            // AAA, CRAM and laser
            Gun,
            // IR SAM
            ShortRange,
            // Radar SAM with no own radar: it needs an illuminator and has trucks around it
            Launcher,
            // Radar SAM with its own radar: it fights alone
            RadarSam,
            RadarVehicle,
            RadarBuilding,
            Support
        }

        private struct Pose
        {
            public GlobalPosition Position;
            public Quaternion Rotation;
        }

        private sealed class Member
        {
            public SavedUnit Saved;
            public UnitDefinition Definition;
            public SiteClass Class;
            public Vector3 Local;
            public float Radius;
            public float AboveGround;
            public int Battery = -1;
            // A turret that looks for a radar or fire control (vanilla searchForRadar)
            public bool NeedsLink;
            public bool HasFireControl;
            // Set for one battery by PrepareFormation
            public Vector3 Offset;
            public int ParentSlot = -1;
            public bool AvoidTrees;
            public float SensorHeight;
            public float SensorRange;
            public float MinView;
        }

        private struct Obstacle
        {
            public Vector3 Local;
            public float Radius;
            public int Battery;
            public string Faction;
            public FactionHQ Hq;
            public bool Solid;
            public bool Asset;
        }

        private struct RunwayZone
        {
            public Vector3 Start;
            public Vector3 End;
        }

        private struct BaseZone
        {
            public Vector3 Center;
            public float Radius;
        }

        private struct Candidate
        {
            public Vector3 Center;
            public float Yaw;
            public float Scale;
            public float Gap;
            public float Score;
            public float WorstView;
            public float LowestHeight;
            public Vector3[] Ground;
        }

        /// <summary>
        /// The tree groups of one TreeRenderer in a grid. Trees have no collider (the GPU draws them), so a terrain ray
        /// does not see them. The positions are GlobalPositions. The grid lives only while the plan is made.
        /// </summary>
        private sealed class TreeGrid
        {
            private const float CellMeters = 128.0f;

            private float m_minX;
            private float m_minZ;
            private int m_width;
            private int m_height;
            private int[] m_cellStart;
            private float[] m_x;
            private float[] m_z;

            public int Count
            {
                get { return m_x.Length; }
            }

            public static TreeGrid Build(byte[] data)
            {
                int count = data.Length / 12;
                float[] raw = new float[count * 3];
                Buffer.BlockCopy(data, 0, raw, 0, count * 12);

                float minX = float.MaxValue;
                float minZ = float.MaxValue;
                float maxX = float.MinValue;
                float maxZ = float.MinValue;
                for (int i = 0; i < count; i++)
                {
                    float x = raw[i * 3];
                    float z = raw[i * 3 + 2];
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (z < minZ) minZ = z;
                    if (z > maxZ) maxZ = z;
                }

                TreeGrid grid = new TreeGrid();
                grid.m_minX = minX;
                grid.m_minZ = minZ;
                grid.m_width = (int)((maxX - minX) / CellMeters) + 1;
                grid.m_height = (int)((maxZ - minZ) / CellMeters) + 1;
                grid.m_x = new float[count];
                grid.m_z = new float[count];

                int cells = grid.m_width * grid.m_height;
                int[] start = new int[cells + 1];
                for (int i = 0; i < count; i++) start[grid.CellOf(raw[i * 3], raw[i * 3 + 2]) + 1]++;
                for (int i = 0; i < cells; i++) start[i + 1] += start[i];

                int[] next = new int[cells];
                Array.Copy(start, next, cells);
                for (int i = 0; i < count; i++)
                {
                    float x = raw[i * 3];
                    float z = raw[i * 3 + 2];
                    int slot = next[grid.CellOf(x, z)]++;
                    grid.m_x[slot] = x;
                    grid.m_z[slot] = z;
                }
                grid.m_cellStart = start;
                return grid;
            }

            private int CellOf(float x, float z)
            {
                return (int)((z - m_minZ) / CellMeters) * m_width + (int)((x - m_minX) / CellMeters);
            }

            public bool IsNear(float x, float z, float reach)
            {
                int firstColumn = Mathf.Max(0, Mathf.FloorToInt((x - reach - m_minX) / CellMeters));
                int lastColumn = Mathf.Min(m_width - 1, Mathf.FloorToInt((x + reach - m_minX) / CellMeters));
                int firstRow = Mathf.Max(0, Mathf.FloorToInt((z - reach - m_minZ) / CellMeters));
                int lastRow = Mathf.Min(m_height - 1, Mathf.FloorToInt((z + reach - m_minZ) / CellMeters));
                float reachSqr = reach * reach;

                for (int row = firstRow; row <= lastRow; row++)
                {
                    for (int column = firstColumn; column <= lastColumn; column++)
                    {
                        int cell = row * m_width + column;
                        for (int i = m_cellStart[cell]; i < m_cellStart[cell + 1]; i++)
                        {
                            float dx = m_x[i] - x;
                            float dz = m_z[i] - z;
                            if (dx * dx + dz * dz < reachSqr) return true;
                        }
                    }
                }
                return false;
            }
        }

        private sealed class Context
        {
            public System.Random Random;
            public RoadNetwork Roads;
            public float SeaY;
            public readonly List<Obstacle> Obstacles = new List<Obstacle>();
            public readonly List<RunwayZone> Runways = new List<RunwayZone>();
            public readonly List<BaseZone> Bases = new List<BaseZone>();
            public readonly List<TreeGrid> Trees = new List<TreeGrid>();
            public int GroundRejects;
            public int BlockRejects;
            public int RoadRejects;
            public int TreeRejects;
            public int ViewRejects;
            public int LinkRejects;
            // Set while the point defense of an airbase looks for a place
            public bool AllowBaseInterior;
            public readonly HashSet<int> Moved = new HashSet<int>();
            public readonly List<RoadNetwork> TaxiNetworks = new List<RoadNetwork>();
            public readonly Dictionary<string, FactionPlan> Plans = new Dictionary<string, FactionPlan>();
            public readonly Dictionary<string, FactionHQ> Hqs = new Dictionary<string, FactionHQ>();
            public readonly Dictionary<string, int> GroundColliders = new Dictionary<string, int>();
        }

        /// <summary>
        /// The parts of a prefab that the plan reads. One entry for each definition.
        /// </summary>
        private sealed class GameObjectHolder
        {
            public Turret[] Turrets;
            public bool NeedsLink;
            public bool HasFireControl;
            public bool HasRadar;
            public bool HasRepairer;
        }

        // Turret.TargetAcquisitionMode.searchForRadar. The enum is a private nested type
        private const int SearchForRadarMode = 2;
        private static readonly Func<Turret, float> f_turretMaxRange = FastReflection.CreateFieldGetter<Turret, float>("maxRange");
        private static readonly FieldInfo s_turretMode = typeof(Turret).GetField("targetAcquisitionMode", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly Dictionary<UnitDefinition, GameObjectHolder> s_prefabs = new Dictionary<UnitDefinition, GameObjectHolder>();

        private static GameObjectHolder PrefabOf(Member member)
        {
            return PrefabOf(member.Definition);
        }

        private static GameObjectHolder PrefabOf(UnitDefinition definition)
        {
            GameObjectHolder holder;
            if (s_prefabs.TryGetValue(definition, out holder)) return holder;

            holder = new GameObjectHolder();
            if (definition.unitPrefab != null)
            {
                holder.Turrets = definition.unitPrefab.GetComponentsInChildren<Turret>(true);
                holder.HasFireControl = definition.unitPrefab.GetComponentInChildren<FireControl>(true) != null;
                holder.HasRadar = definition.unitPrefab.GetComponentInChildren<Radar>(true) != null;
                holder.HasRepairer = definition.unitPrefab.GetComponentInChildren<Repairer>(true) != null;
                for (int i = 0; i < holder.Turrets.Length && s_turretMode != null; i++)
                {
                    if (Convert.ToInt32(s_turretMode.GetValue(holder.Turrets[i])) == SearchForRadarMode) holder.NeedsLink = true;
                }
            }
            // A radar SAM with no own radar always needs a radar, also if its turret asks in another way
            VehicleDefinition vehicle = definition as VehicleDefinition;
            if (vehicle != null && vehicle.vehicleType == VehicleType.R_SAM && !holder.HasRadar) holder.NeedsLink = true;
            s_prefabs[definition] = holder;
            return holder;
        }

        private static readonly Dictionary<SavedUnit, Pose> s_plan = new Dictionary<SavedUnit, Pose>();
        private static Mission s_plannedMission;

        public static void ClearAll()
        {
            s_plan.Clear();
            s_plannedMission = null;
            s_prefabs.Clear();
            s_runtimeTrees = null;
        }

        /// <summary>
        /// The position of the plan for this unit, if a battery moved it.
        /// </summary>
        public static bool TryGetPose(SavedUnit saved, out GlobalPosition position, out Quaternion rotation)
        {
            Pose pose;
            if (saved != null && s_plan.TryGetValue(saved, out pose))
            {
                position = pose.Position;
                rotation = pose.Rotation;
                return true;
            }
            position = default(GlobalPosition);
            rotation = Quaternion.identity;
            return false;
        }

        /// <summary>
        /// Called before each SpawnUnitOutcome. Makes the plan once per mission, when the start objective spawns its units.
        /// </summary>
        public static void PlanMissionStart(Objective completedObjective)
        {
            Mission mission = MissionManager.CurrentMission;
            if (mission == null || ReferenceEquals(mission, s_plannedMission)) return;
            if (mission.RuntimeObjectives == null || completedObjective == null) return;
            if (!ReferenceEquals(completedObjective, mission.RuntimeObjectives.StartObjective)) return;

            s_plannedMission = mission;
            s_plan.Clear();
            if (!IsAllowed(mission)) return;

            Stopwatch timer = Stopwatch.StartNew();
            int seed = CnoEvilConfig.DynamicPlacementSeed.Value;
            if (seed == 0) seed = Environment.TickCount;

            Context ctx = BuildContext(seed);
            HashSet<string> named = CollectNamedUnits(mission);
            List<Member> members = new List<Member>();
            List<Member> support = new List<Member>();
            int excluded;
            CollectMembers(mission, ctx, named, members, support, out excluded);

            List<Complex> complexes = BuildComplexes(members, support);
            AddObstacles(mission, ctx, complexes);
            List<DefenseNode> nodes = BuildNodes(mission, ctx);
            AssignNodes(complexes, nodes);

            int moved;
            int movedUnits;
            int kept;
            PlaceComplexes(complexes, ctx, out moved, out movedUnits, out kept);

            LogResult(mission, seed, movedUnits, moved, kept, excluded, nodes.Count, timer, ctx);
        }

        private static bool IsAllowed(Mission mission)
        {
            if (!CnoEvilConfig.DynamicPlacementEnabled.Value || !CnoEvilConfig.IsModActive) return false;
            if (CnoEvilNet.IsMirrorClient || GameManager.IsPlayingFromEditor) return false;
            GameState state = GameManager.gameState;
            if (state != GameState.SinglePlayer && state != GameState.Multiplayer) return false;
            return mission.Name == null || !mission.Name.StartsWith("Tutorial", StringComparison.OrdinalIgnoreCase);
        }

        private static Context BuildContext(int seed)
        {
            Context ctx = new Context();
            ctx.Random = new System.Random(seed);
            ctx.SeaY = Datum.LocalSeaY;

            LevelInfo level = LevelInfo.i;
            if (level != null)
            {
                RoadNetwork roads = level.roadNetwork;
                if (roads != null && roads.Exists()) ctx.Roads = roads;
            }
            AddAirfields(ctx);
            LoadTrees(ctx);
            return ctx;
        }

        /// <summary>
        /// Airbase areas, runways and taxi networks as local positions (valid until the next floating origin shift).
        /// </summary>
        private static void AddAirfields(Context ctx)
        {
            foreach (Airbase airbase in FactionRegistry.airbaseLookup.Values)
            {
                if (airbase == null) continue;
                if (airbase.center != null)
                {
                    BaseZone zone;
                    zone.Center = airbase.center.position;
                    zone.Radius = airbase.GetRadius();
                    ctx.Bases.Add(zone);
                }
                RoadNetwork taxi = airbase.GetTaxiNetwork();
                if (taxi != null && taxi.Exists()) ctx.TaxiNetworks.Add(taxi);
                if (airbase.runways == null) continue;
                for (int i = 0; i < airbase.runways.Length; i++)
                {
                    Airbase.Runway runway = airbase.runways[i];
                    if (runway == null || runway.Start == null || runway.End == null) continue;
                    RunwayZone strip;
                    strip.Start = runway.Start.position;
                    strip.End = runway.End.position;
                    ctx.Runways.Add(strip);
                }
            }
        }

        private static void LoadTrees(Context ctx)
        {
            // Inactive objects too: a headless server turns off the DetailRenderer, but the map has the same trees
            NuclearOption.Effects.TreeRenderer[] renderers = UnityEngine.Object.FindObjectsOfType<NuclearOption.Effects.TreeRenderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                TextAsset positions = renderers[i].PositionData;
                if (positions == null) continue;
                byte[] data = positions.bytes;
                if (data != null && data.Length >= 12) ctx.Trees.Add(TreeGrid.Build(data));
            }
        }

        /// <summary>
        /// All strings in the saved objectives and outcomes. A unit name in this set has a role in the mission logic.
        /// The objective data have different fields for each type, so the walk reads the fields, not known types.
        /// </summary>
        private static HashSet<string> CollectNamedUnits(Mission mission)
        {
            HashSet<string> names = new HashSet<string>();
            HashSet<object> visited = new HashSet<object>();
            CollectStrings(mission.objectives, names, visited, 0);
            CollectStrings(mission.outcomes, names, visited, 0);
            return names;
        }

        private static void CollectStrings(object value, HashSet<string> names, HashSet<object> visited, int depth)
        {
            if (value == null || depth > 5) return;
            string text = value as string;
            if (text != null)
            {
                names.Add(text);
                return;
            }

            Type type = value.GetType();
            if (type.IsPrimitive || type.IsEnum || value is UnityEngine.Object) return;
            // Only the data classes of the game and generic collections: System.Type and similar have nothing to read
            if (type.Assembly != typeof(Mission).Assembly && !type.IsGenericType && !type.IsArray) return;
            if (!type.IsValueType && !visited.Add(value)) return;

            IEnumerable list = value as IEnumerable;
            if (list != null)
            {
                foreach (object item in list) CollectStrings(item, names, visited, depth + 1);
                return;
            }

            for (Type t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                FieldInfo[] fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                for (int i = 0; i < fields.Length; i++)
                {
                    Type fieldType = fields[i].FieldType;
                    if (fieldType.IsPrimitive || fieldType.IsEnum || typeof(Delegate).IsAssignableFrom(fieldType)) continue;
                    CollectStrings(fields[i].GetValue(value), names, visited, depth + 1);
                }
            }
        }

        private static SiteClass Classify(UnitDefinition definition)
        {
            VehicleDefinition vehicle = definition as VehicleDefinition;
            if (vehicle != null)
            {
                switch (vehicle.vehicleType)
                {
                    case VehicleType.AAA:
                        return SiteClass.Gun;
                    case VehicleType.IR_SAM:
                        return SiteClass.ShortRange;
                    case VehicleType.R_SAM:
                        return HasOwnRadar(definition) ? SiteClass.RadarSam : SiteClass.Launcher;
                    case VehicleType.RDR:
                        return SiteClass.RadarVehicle;
                    default:
                        return SiteClass.None;
                }
            }
            BuildingDefinition building = definition as BuildingDefinition;
            if (building != null && building.buildingType == BuildingType.RDR) return SiteClass.RadarBuilding;
            return SiteClass.None;
        }

        private static bool HasOwnRadar(UnitDefinition definition)
        {
            return PrefabOf(definition).HasRadar;
        }

        private static bool IsRadarClass(SiteClass siteClass)
        {
            return siteClass == SiteClass.RadarSam || siteClass == SiteClass.RadarVehicle || siteClass == SiteClass.RadarBuilding;
        }

        private static float VisualRange(UnitDefinition definition)
        {
            float range = 0.0f;
            if (definition.unitPrefab != null)
            {
                TargetDetector[] detectors = definition.unitPrefab.GetComponentsInChildren<TargetDetector>(true);
                for (int i = 0; i < detectors.Length; i++) range = Mathf.Max(range, detectors[i].GetVisualRange());
            }
            return range > 0.0f ? range : DefaultVisualRangeMeters;
        }

        private static bool IsSupportVehicle(UnitDefinition definition)
        {
            VehicleDefinition vehicle = definition as VehicleDefinition;
            return vehicle != null && (vehicle.vehicleType == VehicleType.TRUCK || PrefabOf(definition).HasRepairer);
        }

        private static float UnitRadius(UnitDefinition definition)
        {
            if (definition == null) return MinUnitRadiusMeters;
            return Mathf.Max(MinUnitRadiusMeters, 0.5f * Mathf.Max(definition.length, definition.width));
        }

        private struct FixedLink
        {
            public Vector3 Local;
            public string Faction;
            public bool Provider;
            public bool Dependent;
        }

        private static void CollectMembers(Mission mission, Context ctx, HashSet<string> named, List<Member> members, List<Member> support, out int excluded)
        {
            excluded = 0;
            List<FixedLink> fixedLinks = new List<FixedLink>();

            for (int i = 0; i < mission.vehicles.Count; i++)
            {
                SavedVehicle vehicle = mission.vehicles[i];
                UnitDefinition definition;
                if (vehicle == null || !Encyclopedia.Lookup.TryGetValue(vehicle.type, out definition)) continue;
                SiteClass siteClass = Classify(definition);
                bool noWaypoints = vehicle.waypoints == null || vehicle.waypoints.Count == 0;
                if (siteClass == SiteClass.None)
                {
                    // A truck without holdPosition has no route of its own either: it only starts next to the complex
                    if (!noWaypoints || !IsSupportVehicle(definition)) continue;
                    Member truck = TryMakeMember(vehicle, definition, SiteClass.Support, ctx, named, mission);
                    if (truck != null) support.Add(truck);
                    continue;
                }

                bool free = vehicle.holdPosition && noWaypoints;
                Member member = free ? TryMakeMember(vehicle, definition, siteClass, ctx, named, mission) : null;
                if (member != null)
                {
                    members.Add(member);
                    continue;
                }
                excluded++;
                AddFixedLink(fixedLinks, vehicle.globalPosition.ToLocalPosition(), vehicle.faction, definition);
            }

            for (int i = 0; i < mission.buildings.Count; i++)
            {
                SavedBuilding building = mission.buildings[i];
                UnitDefinition definition;
                if (building == null || !Encyclopedia.Lookup.TryGetValue(building.type, out definition)) continue;
                if (Classify(definition) != SiteClass.RadarBuilding) continue;

                bool free = string.IsNullOrEmpty(building.Airbase) && !building.capturable;
                Member member = free ? TryMakeMember(building, definition, SiteClass.RadarBuilding, ctx, named, mission) : null;
                if (member != null)
                {
                    members.Add(member);
                    continue;
                }
                excluded++;
                AddFixedLink(fixedLinks, building.globalPosition.ToLocalPosition(), building.faction, definition);
            }

            // A unit linked to a unit that must stay also stays, else the vanilla search does not find its radar
            bool changed = true;
            while (changed)
            {
                changed = HoldLinked(members, fixedLinks, ref excluded);
                if (HoldLinked(support, fixedLinks, ref excluded)) changed = true;
            }
        }

        private static void AddFixedLink(List<FixedLink> links, Vector3 local, string faction, UnitDefinition definition)
        {
            GameObjectHolder prefab = PrefabOf(definition);
            if (!prefab.NeedsLink && !prefab.HasRadar && !prefab.HasFireControl) return;
            FixedLink link;
            link.Local = local;
            link.Faction = faction;
            link.Provider = prefab.HasRadar || prefab.HasFireControl;
            link.Dependent = prefab.NeedsLink || prefab.HasFireControl;
            links.Add(link);
        }

        private static bool HoldLinked(List<Member> units, List<FixedLink> links, ref int excluded)
        {
            bool changed = false;
            float linkSqr = VanillaLinkMeters * VanillaLinkMeters;
            for (int i = units.Count - 1; i >= 0; i--)
            {
                Member member = units[i];
                bool provider = member.HasFireControl || IsRadarClass(member.Class);
                bool dependent = member.NeedsLink || member.HasFireControl;
                if (!provider && !dependent) continue;

                for (int j = 0; j < links.Count; j++)
                {
                    FixedLink link = links[j];
                    if (link.Faction != member.Saved.faction || FlatSqr(link.Local, member.Local) >= linkSqr) continue;
                    if (!(dependent && link.Provider) && !(provider && link.Dependent)) continue;

                    units.RemoveAt(i);
                    excluded++;
                    AddFixedLink(links, member.Local, member.Saved.faction, member.Definition);
                    changed = true;
                    break;
                }
            }
            return changed;
        }

        private static Member TryMakeMember(SavedUnit saved, UnitDefinition definition, SiteClass siteClass, Context ctx, HashSet<string> named, Mission mission)
        {
            if (saved.PlacementType != PlacementType.Custom || saved.HasSpawned) return null;
            if (named.Contains(saved.UniqueName)) return null;

            Vector3 local = saved.globalPosition.ToLocalPosition();
            RaycastHit hit;
            if (!PathfindingAgent.RaycastTerrain(local, out hit) || hit.point.y <= ctx.SeaY) return null;

            float aboveGround = local.y - hit.point.y;
            if (siteClass != SiteClass.RadarBuilding)
            {
                if (aboveGround > OnTerrainToleranceMeters) return null;
                if (IsAttached(local, mission)) return null;
            }

            Member member = new Member();
            member.Saved = saved;
            member.Definition = definition;
            member.Class = siteClass;
            member.Local = local;
            member.Radius = UnitRadius(definition);
            member.AboveGround = aboveGround;
            GameObjectHolder prefab = PrefabOf(definition);
            member.NeedsLink = prefab.NeedsLink;
            member.HasFireControl = prefab.HasFireControl;
            return member;
        }

        private static bool IsAttached(Vector3 local, Mission mission)
        {
            return IsNearAny(local, mission.buildings) || IsNearAny(local, mission.containers) || IsNearAny(local, mission.ships);
        }

        private static bool IsNearAny<T>(Vector3 local, List<T> units) where T : SavedUnit
        {
            if (units == null) return false;
            for (int i = 0; i < units.Count; i++)
            {
                T unit = units[i];
                UnitDefinition definition;
                if (unit == null || !Encyclopedia.Lookup.TryGetValue(unit.type, out definition)) continue;
                float reach = UnitRadius(definition) + AttachedClearanceMeters;
                if (FlatSqr(unit.globalPosition.ToLocalPosition(), local) < reach * reach) return true;
            }
            return false;
        }

        /// <summary>
        /// True if the group has a unit of this class. SiteClass.None asks for any air defense unit.
        /// </summary>
        private static bool Has(List<Member> group, SiteClass siteClass)
        {
            for (int i = 0; i < group.Count; i++)
            {
                SiteClass own = group[i].Class;
                if (siteClass == SiteClass.None ? own != SiteClass.Support : own == siteClass) return true;
            }
            return false;
        }

        private static void AddObstacles(Mission mission, Context ctx, List<Complex> complexes)
        {
            Dictionary<SavedUnit, int> batteryOf = new Dictionary<SavedUnit, int>();
            for (int i = 0; i < complexes.Count; i++)
            {
                for (int j = 0; j < complexes[i].Units.Count; j++) batteryOf[complexes[i].Units[j].Saved] = i;
            }

            AddSavedObstacles(mission.vehicles, false, ctx, batteryOf);
            AddSavedObstacles(mission.buildings, true, ctx, batteryOf);
            AddSavedObstacles(mission.ships, false, ctx, batteryOf);
            AddSavedObstacles(mission.containers, false, ctx, batteryOf);
            AddSavedObstacles(mission.scenery, false, ctx, batteryOf);
            AddSavedObstacles(mission.aircraft, false, ctx, batteryOf);

            // Units of the map scene. Mission units have not spawned yet
            for (int i = 0; i < UnitRegistry.allUnits.Count; i++)
            {
                Unit unit = UnitRegistry.allUnits[i];
                if (unit == null) continue;
                Obstacle obstacle = new Obstacle();
                obstacle.Local = unit.transform.position;
                obstacle.Radius = UnitRadius(unit.definition);
                obstacle.Battery = -1;
                obstacle.Solid = true;
                ctx.Obstacles.Add(obstacle);
            }
        }

        private static void AddSavedObstacles<T>(List<T> units, bool asset, Context ctx, Dictionary<SavedUnit, int> batteryOf) where T : SavedUnit
        {
            if (units == null) return;
            for (int i = 0; i < units.Count; i++)
            {
                T unit = units[i];
                if (unit == null) continue;
                UnitDefinition definition;
                Encyclopedia.Lookup.TryGetValue(unit.type, out definition);

                int battery;
                bool moves = batteryOf.TryGetValue(unit, out battery);

                Obstacle obstacle = new Obstacle();
                obstacle.Local = unit.globalPosition.ToLocalPosition();
                obstacle.Radius = UnitRadius(definition);
                obstacle.Battery = moves ? battery : -1;
                obstacle.Faction = unit.faction;
                obstacle.Hq = HqOf(unit.faction, ctx);
                obstacle.Solid = true;
                obstacle.Asset = asset && Classify(definition) == SiteClass.None;
                ctx.Obstacles.Add(obstacle);
            }
        }

        private static FactionHQ HqOf(string faction, Context ctx)
        {
            if (string.IsNullOrEmpty(faction)) return null;
            FactionHQ hq;
            if (!ctx.Hqs.TryGetValue(faction, out hq))
            {
                hq = FactionRegistry.HqFromName(faction);
                ctx.Hqs[faction] = hq;
            }
            return hq;
        }

        /// <summary>
        /// Puts the anchor first, then the fighting units by distance from it, then the trucks, each with its unit. Also sets
        /// the values of the unit that do not change between the candidate sites.
        /// </summary>
        private static void PrepareFormation(List<Member> battery)
        {
            Member anchor = FindAnchor(battery);
            battery.Sort(delegate(Member a, Member b)
            {
                int rank = FormationRank(a, anchor).CompareTo(FormationRank(b, anchor));
                return rank != 0 ? rank : FlatSqr(a.Local, anchor.Local).CompareTo(FlatSqr(b.Local, anchor.Local));
            });

            bool launchers = Has(battery, SiteClass.Launcher);
            for (int i = 0; i < battery.Count; i++)
            {
                Member member = battery[i];
                member.Offset = member.Local - anchor.Local;
                member.AvoidTrees = launchers || member.Class == SiteClass.RadarBuilding;
                member.ParentSlot = member.Class == SiteClass.Support ? NearestFighter(battery, member) : -1;

                member.SensorRange = 0.0f;
                if (IsRadarClass(member.Class))
                {
                    member.SensorHeight = AntennaHeightMeters;
                    member.SensorRange = HorizonRangeMeters;
                }
                else if (member.Class == SiteClass.ShortRange)
                {
                    member.SensorHeight = OpticsHeightMeters;
                    member.SensorRange = VisualRange(member.Definition);
                }
                if (member.SensorRange <= 0.0f) continue;

                Vector3 authorGround = member.Local + Vector3.down * member.AboveGround;
                member.MinView = Mathf.Min(RequiredView, HorizonCoverage(authorGround, member.SensorHeight, member.SensorRange));
            }
        }

        private static int FormationRank(Member member, Member anchor)
        {
            if (ReferenceEquals(member, anchor)) return 0;
            return member.Class == SiteClass.Support ? 2 : 1;
        }

        /// <summary>
        /// The radar station, else the radar nearest to the center of the battery, else the fighting unit nearest to it.
        /// </summary>
        private static Member FindAnchor(List<Member> battery)
        {
            Vector3 center = Vector3.zero;
            for (int i = 0; i < battery.Count; i++) center += battery[i].Local;
            center /= battery.Count;

            Member best = null;
            int bestRank = int.MaxValue;
            float bestSqr = float.MaxValue;
            for (int i = 0; i < battery.Count; i++)
            {
                Member member = battery[i];
                if (member.Class == SiteClass.Support) continue;
                int rank = member.Class == SiteClass.RadarBuilding ? 0 : (IsRadarClass(member.Class) ? 1 : 2);
                float sqr = FlatSqr(member.Local, center);
                if (rank > bestRank || (rank == bestRank && sqr >= bestSqr)) continue;
                best = member;
                bestRank = rank;
                bestSqr = sqr;
            }
            return best;
        }

        private static int NearestFighter(List<Member> battery, Member truck)
        {
            int nearest = 0;
            float best = float.MaxValue;
            for (int i = 0; i < battery.Count; i++)
            {
                if (battery[i].Class == SiteClass.Support) continue;
                float sqr = FlatSqr(battery[i].Local, truck.Local);
                if (sqr < best)
                {
                    best = sqr;
                    nearest = i;
                }
            }
            return nearest;
        }

        /// <summary>
        /// Each unit with a sensor must see from its place. Flat ground passes; a hollow, or a place worse than the author's
        /// position with less than RequiredView, does not.
        /// </summary>
        private static bool HasClearView(List<Member> battery, ref Candidate candidate, Context ctx)
        {
            float worstView = 1.0f;
            float lowest = float.MaxValue;
            for (int i = 0; i < battery.Count; i++)
            {
                Member member = battery[i];
                if (member.SensorRange <= 0.0f) continue;

                Vector3 ground = candidate.Ground[i];
                float height = HeightAboveSurroundings(ground, ctx);
                if (height < -LowGroundToleranceMeters) return false;
                float view = HorizonCoverage(ground, member.SensorHeight, member.SensorRange);
                if (view < member.MinView) return false;

                worstView = Mathf.Min(worstView, view);
                lowest = Mathf.Min(lowest, height);
            }
            candidate.WorstView = worstView;
            candidate.LowestHeight = lowest < float.MaxValue ? lowest : 0.0f;
            return true;
        }

        private static void AddDistinctSite(List<Candidate> candidates, ref Candidate candidate)
        {
            float sameSqr = SameHillMeters * SameHillMeters;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (FlatSqr(candidates[i].Center, candidate.Center) >= sameSqr) continue;
                if (candidate.Score > candidates[i].Score) candidates[i] = candidate;
                return;
            }
            candidates.Add(candidate);
        }

        /// <summary>
        /// Walks to a local top of the terrain, not farther than the radius from the origin.
        /// </summary>
        private static Vector3 ClimbHill(Vector3 start, Vector3 origin, float radius)
        {
            RaycastHit hit;
            if (!PathfindingAgent.RaycastTerrain(start, out hit)) return start;

            Vector3 current = start;
            float height = hit.point.y;
            float limitSqr = radius * radius;
            for (int step = 0; step < ClimbSteps; step++)
            {
                Vector3 best = current;
                bool higher = false;
                for (int i = 0; i < ClimbDirections; i++)
                {
                    float angle = i * (Mathf.PI * 2.0f / ClimbDirections);
                    Vector3 sample = current + new Vector3(Mathf.Cos(angle), 0.0f, Mathf.Sin(angle)) * ClimbStepMeters;
                    if (FlatSqr(sample, origin) > limitSqr || !PathfindingAgent.RaycastTerrain(sample, out hit)) continue;
                    if (hit.point.y <= height) continue;
                    height = hit.point.y;
                    best = sample;
                    higher = true;
                }
                if (!higher) break;
                current = best;
            }
            return current;
        }

        private static bool TryNearestEnemy(Vector3 from, FactionHQ ownHq, Context ctx, out Vector3 enemy)
        {
            enemy = from;
            if (ownHq == null) return false;
            float best = float.MaxValue;
            bool found = false;
            for (int i = 0; i < ctx.Obstacles.Count; i++)
            {
                Obstacle obstacle = ctx.Obstacles[i];
                if (obstacle.Hq == null || obstacle.Hq == ownHq) continue;
                float sqr = FlatSqr(obstacle.Local, from);
                if (sqr < best)
                {
                    best = sqr;
                    enemy = obstacle.Local;
                    found = true;
                }
            }
            return found;
        }

        private static bool TryNearestAsset(Vector3 from, string faction, float limit, Context ctx, out Vector3 asset)
        {
            asset = from;
            float best = limit * limit;
            bool found = false;
            for (int i = 0; i < ctx.Obstacles.Count; i++)
            {
                Obstacle obstacle = ctx.Obstacles[i];
                if (!obstacle.Asset || obstacle.Faction != faction) continue;
                float sqr = FlatSqr(obstacle.Local, from);
                if (sqr < best)
                {
                    best = sqr;
                    asset = obstacle.Local;
                    found = true;
                }
            }
            return found;
        }

        private static bool TryRoadDistance(Vector3 local, out float distance, Context ctx)
        {
            distance = 0.0f;
            if (ctx.Roads == null) return false;
            GlobalPosition nearest;
            Vector3 direction;
            if (!ctx.Roads.TryGetNearestPoint(GlobalPositionExtensions.ToGlobalPosition(local), out nearest, out direction)) return false;
            distance = FlatDistance(nearest.ToLocalPosition(), local);
            return true;
        }

        /// <summary>
        /// All units get a place or the site fails: a battery never breaks up.
        /// </summary>
        private static bool TryFitBattery(List<Member> battery, int index, ref Candidate candidate, Context ctx)
        {
            Quaternion turn = Quaternion.AngleAxis(candidate.Yaw, Vector3.up);
            Vector3[] ground = new Vector3[battery.Count];
            for (int i = 0; i < battery.Count; i++)
            {
                Member member = battery[i];
                Vector3 wanted;
                float step = UnitSearchStepMeters;
                int attempts = UnitSearchAttempts;
                float reach = 0.0f;
                if (i == 0)
                {
                    wanted = candidate.Center;
                    attempts = AnchorSearchAttempts;
                }
                else if (member.ParentSlot >= 0)
                {
                    wanted = ground[member.ParentSlot] + turn * TruckOffset(member.Local - battery[member.ParentSlot].Local, candidate.Scale);
                    step = TruckSearchStepMeters;
                    attempts = TruckSearchAttempts;
                }
                else
                {
                    wanted = ground[0] + turn * (member.Offset * candidate.Scale);
                    reach = Mathf.Max(FormationReachMeters, FormationReachFactor * FlatDistance(member.Local, battery[0].Local));
                }

                Vector3 point;
                if (!TryFitUnit(wanted, step, attempts, reach, battery, i, index, ground, candidate.Gap, ctx, out point)) return false;
                ground[i] = point;
            }
            candidate.Ground = ground;
            return true;
        }

        private static Vector3 TruckOffset(Vector3 offset, float scale)
        {
            float distance = new Vector2(offset.x, offset.z).magnitude;
            if (scale >= 1.0f || distance <= RigidTruckMeters) return offset;
            return offset * Mathf.Max(scale, RigidTruckMeters / distance);
        }

        private static bool TryFitUnit(Vector3 wanted, float step, int attempts, float reach, List<Member> battery, int slot, int index, Vector3[] placed,
                                       float gap, Context ctx, out Vector3 point)
        {
            if (IsValidSpot(wanted, battery, slot, index, placed, gap, ctx, out point)) return true;
            for (int i = 0; i < attempts; i++)
            {
                float angle = (float)(ctx.Random.NextDouble() * Math.PI * 2.0);
                Vector3 nudged = wanted + new Vector3(Mathf.Cos(angle), 0.0f, Mathf.Sin(angle)) * (step * (i + 1));
                if (reach > 0.0f && FlatSqr(nudged, placed[0]) > reach * reach) continue;
                if (IsValidSpot(nudged, battery, slot, index, placed, gap, ctx, out point)) return true;
            }
            return false;
        }

        private static bool IsValidSpot(Vector3 wanted, List<Member> battery, int slot, int index, Vector3[] placed, float gap, Context ctx, out Vector3 point)
        {
            point = wanted;
            Member member = battery[slot];
            if (IsInAirbase(wanted, ctx) || IsBlockedByUnit(wanted, battery, slot, index, placed, gap, ctx))
            {
                ctx.BlockRejects++;
                return false;
            }
            if (member.AvoidTrees && IsNearTree(wanted, member.Radius, ctx))
            {
                ctx.TreeRejects++;
                return false;
            }

            if (!TryGround(wanted, ctx, out point))
            {
                ctx.GroundRejects++;
                return false;
            }

            float tolerance = Mathf.Clamp(member.Radius * 0.25f, 1.5f, 3.0f);
            for (int i = 0; i < 4; i++)
            {
                Vector3 corner = wanted;
                corner.x += (i < 2 ? member.Radius : -member.Radius);
                corner.z += ((i & 1) == 0 ? member.Radius : -member.Radius);
                Vector3 cornerPoint;
                if (!TryGround(corner, ctx, out cornerPoint) || Mathf.Abs(cornerPoint.y - point.y) > tolerance)
                {
                    ctx.GroundRejects++;
                    return false;
                }
            }

            float road;
            if (!TryRoadDistance(wanted, out road, ctx) || road >= RoadClearanceMeters + member.Radius) return true;
            ctx.RoadRejects++;
            return false;
        }

        private static bool TryGround(Vector3 local, Context ctx, out Vector3 point)
        {
            point = local;
            RaycastHit hit;
            if (!PathfindingAgent.RaycastTerrain(local, out hit)) return false;
            if (hit.normal.y < MinGroundNormalY || hit.point.y < ctx.SeaY + SeaClearanceMeters) return false;
            if (hit.collider == null || hit.collider.GetComponentInParent<Unit>() != null) return false;
            point = hit.point;
            return true;
        }

        private static bool IsInAirbase(Vector3 local, Context ctx)
        {
            if (ctx.AllowBaseInterior)
            {
                if (IsOnTaxiway(local, ctx)) return true;
            }
            else
            {
                for (int i = 0; i < ctx.Bases.Count; i++)
                {
                    BaseZone zone = ctx.Bases[i];
                    if (FlatSqr(zone.Center, local) < zone.Radius * zone.Radius) return true;
                }
            }
            for (int i = 0; i < ctx.Runways.Count; i++)
            {
                RunwayZone strip = ctx.Runways[i];
                if (FlatDistanceToSegment(local, strip.Start, strip.End) < RunwayClearanceMeters) return true;
            }
            return false;
        }

        private static bool IsOnTaxiway(Vector3 local, Context ctx)
        {
            GlobalPosition global = GlobalPositionExtensions.ToGlobalPosition(local);
            for (int i = 0; i < ctx.TaxiNetworks.Count; i++)
            {
                GlobalPosition nearest;
                Vector3 direction;
                if (ctx.TaxiNetworks[i].TryGetNearestPoint(global, out nearest, out direction) && FlatDistance(nearest.ToLocalPosition(), local) < TaxiClearanceMeters) return true;
            }
            return false;
        }

        private static bool IsNearTree(Vector3 local, float radius, Context ctx)
        {
            if (ctx.Trees.Count == 0) return false;
            GlobalPosition global = GlobalPositionExtensions.ToGlobalPosition(local);
            float reach = radius + TreeClearanceMeters;
            for (int i = 0; i < ctx.Trees.Count; i++)
            {
                if (ctx.Trees[i].IsNear(global.x, global.z, reach)) return true;
            }
            return false;
        }

        private static bool IsBlockedByUnit(Vector3 local, List<Member> battery, int slot, int index, Vector3[] placed, float gap, Context ctx)
        {
            Member member = battery[slot];
            float radius = member.Radius;
            for (int i = 0; i < ctx.Obstacles.Count; i++)
            {
                Obstacle obstacle = ctx.Obstacles[i];
                if (!obstacle.Solid || obstacle.Battery == index || ctx.Moved.Contains(obstacle.Battery)) continue;
                float reach = obstacle.Radius + radius + gap;
                if (FlatSqr(obstacle.Local, local) < reach * reach) return true;
            }
            for (int i = 0; i < slot; i++)
            {
                Member other = battery[i];
                float reach = other.Radius + radius + gap;
                // The author can park closer than the gap (a tractor at its trailer): a truck keeps that distance to its group
                if (IsRigidPair(member, other, i, slot)) reach = Mathf.Min(reach, Mathf.Max(0.0f, FlatDistance(other.Local, member.Local) - 0.5f));
                if (FlatSqr(placed[i], local) < reach * reach) return true;
            }
            return false;
        }

        private static bool IsRigidPair(Member a, Member b, int slotOfB, int slotOfA)
        {
            if (a.ParentSlot < 0) return b.ParentSlot == slotOfA;
            return a.ParentSlot == slotOfB || a.ParentSlot == b.ParentSlot;
        }

        private static float ScoreSite(ref Candidate candidate, Vector3 origin, float radius, Member anchor, bool radarSite, bool mobile, bool hasEnemy, Vector3 enemy,
                                       bool hasAsset, Vector3 asset, Context ctx)
        {
            Vector3 ground = candidate.Ground[0];
            bool sensor = anchor.SensorRange > 0.0f;
            float horizon = HorizonCoverage(ground, sensor ? anchor.SensorHeight : AntennaHeightMeters, sensor ? anchor.SensorRange : HorizonRangeMeters);
            float height = Mathf.Clamp01(HeightAboveSurroundings(ground, ctx) / ElevationFullScoreMeters);

            float forward = 0.5f;
            if (hasEnemy)
            {
                Vector3 toEnemy = enemy - origin;
                Vector3 toSite = candidate.Center - origin;
                toEnemy.y = 0.0f;
                toSite.y = 0.0f;
                forward = 0.5f + 0.5f * Vector3.Dot(toEnemy.normalized, toSite.normalized);
            }

            float cover = hasAsset ? 1.0f - Mathf.Clamp01(FlatDistance(candidate.Center, asset) / radius) : 0.5f;

            // A radar wants height and open horizon, its cover stays near the object; a short range unit also looks to the enemy
            if (radarSite) return horizon + height + (mobile ? 0.2f * cover : 0.0f);
            return 0.8f * horizon + 0.4f * height + 0.2f * forward + 0.4f * cover;
        }

        /// <summary>
        /// The part of the directions with no terrain up to the range of the sensor, on a ray that rises at a low angle.
        /// </summary>
        private static float HorizonCoverage(Vector3 ground, float sensorHeight, float range)
        {
            Vector3 antenna = ground + Vector3.up * sensorHeight;
            float rise = range * (HorizonRiseMeters / HorizonRangeMeters);
            int mask = (int)PhysicsLayers.StaticsMask;
            int open = 0;
            for (int i = 0; i < HorizonAzimuths; i++)
            {
                float angle = i * (Mathf.PI * 2.0f / HorizonAzimuths);
                Vector3 far = antenna + new Vector3(Mathf.Cos(angle), 0.0f, Mathf.Sin(angle)) * range + Vector3.up * rise;
                if (!Physics.Linecast(antenna, far, mask)) open++;
            }
            return (float)open / HorizonAzimuths;
        }

        /// <summary>
        /// Meters above the mean ground around this point. Negative in a hollow.
        /// </summary>
        private static float HeightAboveSurroundings(Vector3 ground, Context ctx)
        {
            float sum = 0.0f;
            int count = 0;
            for (int i = 0; i < ElevationSamples; i++)
            {
                float angle = i * (Mathf.PI * 2.0f / ElevationSamples);
                Vector3 sample = ground + new Vector3(Mathf.Cos(angle), 0.0f, Mathf.Sin(angle)) * ElevationSampleMeters;
                RaycastHit hit;
                if (!PathfindingAgent.RaycastTerrain(sample, out hit)) continue;
                sum += Mathf.Max(hit.point.y, ctx.SeaY);
                count++;
            }
            if (count == 0) return 0.0f;
            return ground.y - sum / count;
        }

        /// <summary>
        /// A weighted choice, not the best site: the best site alone would be the same at each start.
        /// </summary>
        private static Candidate PickCandidate(List<Candidate> candidates, Context ctx)
        {
            float lowest = float.MaxValue;
            for (int i = 0; i < candidates.Count; i++) lowest = Mathf.Min(lowest, candidates[i].Score);

            float total = 0.0f;
            float[] weights = new float[candidates.Count];
            for (int i = 0; i < candidates.Count; i++)
            {
                float margin = candidates[i].Score - lowest + 0.1f;
                weights[i] = margin * margin;
                total += weights[i];
            }

            float roll = (float)ctx.Random.NextDouble() * total;
            for (int i = 0; i < candidates.Count; i++)
            {
                roll -= weights[i];
                if (roll <= 0.0f) return candidates[i];
            }
            return candidates[candidates.Count - 1];
        }

        private static void ApplyPlan(List<Member> battery, ref Candidate chosen, Context ctx)
        {
            for (int i = 0; i < battery.Count; i++)
            {
                Member member = battery[i];
                Vector3 ground = chosen.Ground[i];
                Vector3 local;
                Quaternion rotation;

                if (member.Class == SiteClass.RadarBuilding)
                {
                    local = ground + Vector3.up * member.AboveGround;
                    rotation = Quaternion.AngleAxis(chosen.Yaw, Vector3.up) * member.Saved.rotation;
                }
                else
                {
                    // TryAttachToSurface then aligns the vehicle to the ground; a vertical ray needs a level rotation
                    local = ground + Vector3.up * (member.Definition.spawnOffset.y + VehicleDropMeters);
                    rotation = Quaternion.Euler(0.0f, member.Saved.rotation.eulerAngles.y + chosen.Yaw, 0.0f);
                }

                Pose pose;
                pose.Position = GlobalPositionExtensions.ToGlobalPosition(local);
                pose.Rotation = rotation;
                s_plan[member.Saved] = pose;

                Obstacle obstacle = new Obstacle();
                obstacle.Local = ground;
                obstacle.Radius = member.Radius;
                obstacle.Battery = -1;
                obstacle.Solid = true;
                ctx.Obstacles.Add(obstacle);

                RaycastHit hit;
                if (EvilLog.On(LogCat.Placement) && PathfindingAgent.RaycastTerrain(ground, out hit) && hit.collider != null)
                {
                    int count;
                    ctx.GroundColliders.TryGetValue(hit.collider.name, out count);
                    ctx.GroundColliders[hit.collider.name] = count + 1;
                }
            }
        }

        private static void LogResult(Mission mission, int seed, int movedUnits, int moved, int kept, int excluded, int nodes, Stopwatch timer, Context ctx)
        {
            if (CnoEvilPlugin.Log == null) return;

            int mapRadars = 0;
            for (int i = 0; i < UnitRegistry.allUnits.Count; i++)
            {
                Unit unit = UnitRegistry.allUnits[i];
                if (unit != null && unit.BuiltIn && Classify(unit.definition) != SiteClass.None) mapRadars++;
            }

            int treeGroups = 0;
            for (int i = 0; i < ctx.Trees.Count; i++) treeGroups += ctx.Trees[i].Count;

            string postures = string.Empty;
            foreach (KeyValuePair<string, FactionPlan> entry in ctx.Plans) postures += " " + entry.Key + " " + entry.Value.Posture + ",";

            CnoEvilPlugin.Log.LogInfo(string.Format(
                "Dynamic placement of {0}: seed {1},{2} {3} nodes, {4} units moved in {5} complexes, {6} complexes with no valid site, {7} units excluded, {8} air defense units of the map scene, {9} tree groups, {10} points rejected by trees, {11} sites rejected by view, {12} sites rejected by links, {13} ms",
                mission.Name, seed, postures, nodes, movedUnits, moved, kept, excluded, mapRadars, treeGroups, ctx.TreeRejects, ctx.ViewRejects, ctx.LinkRejects, timer.ElapsedMilliseconds));

            if (EvilLog.On(LogCat.Placement))
            {
                foreach (KeyValuePair<string, int> entry in ctx.GroundColliders)
                {
                    EvilLog.Write(LogCat.Placement, "Dynamic placement ground collider {0}: {1}", entry.Key, entry.Value);
                }
            }
        }

        private static float FlatSqr(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return dx * dx + dz * dz;
        }

        private static float FlatDistance(Vector3 a, Vector3 b)
        {
            return Mathf.Sqrt(FlatSqr(a, b));
        }

        private static float FlatDistanceToSegment(Vector3 point, Vector3 start, Vector3 end)
        {
            float dx = end.x - start.x;
            float dz = end.z - start.z;
            float lengthSqr = dx * dx + dz * dz;
            if (lengthSqr <= 0.0f) return FlatDistance(point, start);
            float t = Mathf.Clamp01(((point.x - start.x) * dx + (point.z - start.z) * dz) / lengthSqr);
            Vector3 nearest = new Vector3(start.x + dx * t, point.y, start.z + dz * t);
            return FlatDistance(point, nearest);
        }
    }
}
