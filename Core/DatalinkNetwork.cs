using System;
using System.Collections.Generic;
using UnityEngine;
using CnoEvil.Config;
using CnoEvil.Components;

namespace CnoEvil.Core
{
    public class DatalinkThreatEntry
    {
        public Unit SourceAlliedUnit;
        public Unit ThreatUnit;
        public Vector3 EstimatedPosition;
        public Vector3 EstimatedVelocity;
        public Vector3 LastDirection;
        public float RelativeBearing;
        public int ClockPosition;
        public float Distance;
        public float LastPingTime;
        public bool IsMissile;
        public string SourceName;
    }

    public class DatalinkNodeSnapshot
    {
        public Unit Unit;
        public string UnitName;
        public string Role;
        public Vector3 Position;
        public bool HasRadar;
        public bool IsAirborne;
        public int SharedRadarCount;
        public int SharedVisualCount;
    }

    public class SharedRadarTrackSnapshot
    {
        public Unit SourceUnit;
        public string SourceName;
        public Unit TargetUnit;
        public string TargetName;
        public Vector3 SourcePos;
        public Vector3 TargetPos;
        public float SlantRangeMeters;
        public float Timestamp;
    }

    public class SharedVisualContactSnapshot
    {
        public Unit SpotterUnit;
        public string SpotterName;
        public Unit TargetUnit;
        public string TargetName;
        public Vector3 SpotterPos;
        public Vector3 TargetPos;
        public float DistanceMeters;
        public float Timestamp;
        public float HoldUntil;     // The contact counts as held until this time (spotter check interval)
    }

    public class RelayedMissileAlertSnapshot
    {
        public Unit RadarUnit;
        public string RadarName;
        public Missile MissileUnit;
        public Vector3 MissilePos;
        public Vector3 MissileVel;
        public float DistToPlayer;
        public float Timestamp;
    }

    public class DatalinkNetworkTelemetry
    {
        public bool NetworkActive;
        public FactionHQ PlayerHQ;
        public string FactionName = "Unknown";
        public float LastCycleTimestamp;
        public float CycleIntervalSeconds = 0.35f;
        public int TotalCyclesProcessed;

        public readonly List<DatalinkNodeSnapshot> Nodes = new List<DatalinkNodeSnapshot>();
        public readonly List<SharedRadarTrackSnapshot> SharedRadarTracks = new List<SharedRadarTrackSnapshot>();
        public readonly List<SharedVisualContactSnapshot> SharedVisualContacts = new List<SharedVisualContactSnapshot>();
        public readonly List<RelayedMissileAlertSnapshot> RelayedMissileAlerts = new List<RelayedMissileAlertSnapshot>();
    }

    /// <summary>
    /// Cooperative Air Defense Network.
    /// Fuses radar tracking data across allied units (AWACS, fighters, SAM batteries, warships)
    /// and broadcasts Tactical Datalink tracks to friendly aircraft, warning them of quiet/stealthy ARH missiles.
    /// </summary>
    public static class DatalinkNetwork
    {
        private static readonly Func<Radar, float> f_radarCone = FastReflection.CreateFieldGetter<Radar, float>("radarCone");
        private static readonly Func<FactionHQ, List<Radar>> f_hqRadars = FastReflection.CreateFieldGetter<FactionHQ, List<Radar>>("radars");

        public static List<Radar> GetHqRadars(FactionHQ hq)
        {
            if (hq == null || f_hqRadars == null) return null;
            return f_hqRadars(hq);
        }

        private static readonly List<Missile> _activeMissiles = new List<Missile>();
        private static readonly HashSet<Missile> _activeMissileSet = new HashSet<Missile>();
        public static List<Missile> ActiveMissiles { get { return _activeMissiles; } }
        private static readonly List<Radar> _registeredRadars = new List<Radar>();
        private static readonly HashSet<Radar> _registeredRadarSet = new HashSet<Radar>();
        public static List<Radar> RegisteredRadars { get { return _registeredRadars; } }

        public static bool IsRadarRegistered(Radar radar)
        {
            return radar != null && _registeredRadarSet.Contains(radar);
        }
        private static float _lastScanTime = 0.0f;
        private static float _lastCensusTime = 0.0f;

        private static readonly List<Unit> _allOperationalUnits = new List<Unit>();
        private static readonly HashSet<Unit> _allOperationalUnitSet = new HashSet<Unit>();
        public static List<Unit> AllOperationalUnits { get { return _allOperationalUnits; } }

        // Faction-owned strategic buildings (ThreatClassification.IsStrategicBuilding) for the visual recon scan
        private static readonly List<Unit> _strategicBuildings = new List<Unit>();
        private static readonly HashSet<Unit> _strategicBuildingSet = new HashSet<Unit>();
        private static readonly List<FactionHQ> s_spottedByFactions = new List<FactionHQ>();

        // Spatial hash of operational units (XZ cells), rebuilt each cycle, so the strategic building scan only tests nearby spotters
        private static readonly Dictionary<long, List<Unit>> s_unitGrid = new Dictionary<long, List<Unit>>();
        private static readonly List<Unit> s_gridCandidates = new List<Unit>();
        private static readonly List<List<Unit>> s_gridCellPool = new List<List<Unit>>();
        private static float s_gridCellSize = 1.0f;

        private static long GridKey(int cx, int cz)
        {
            return ((long)cx << 32) ^ (uint)cz;
        }

        private static void BuildUnitGrid()
        {
            s_gridCellSize = RWRTriangulationProcessor.StrategicBuildingVisualRange; // The grid serves only the building scan
            // Return the cell lists to the pool and drop all keys, so cells a unit has left do not stay in the grid
            foreach (KeyValuePair<long, List<Unit>> kv in s_unitGrid)
            {
                kv.Value.Clear();
                s_gridCellPool.Add(kv.Value);
            }
            s_unitGrid.Clear();

            for (int i = 0; i < _allOperationalUnits.Count; i++)
            {
                Unit u = _allOperationalUnits[i];
                if (u == null || u.disabled) continue;
                Vector3 p = u.transform.position;
                long key = GridKey(Mathf.FloorToInt(p.x / s_gridCellSize), Mathf.FloorToInt(p.z / s_gridCellSize));
                List<Unit> cell;
                if (!s_unitGrid.TryGetValue(key, out cell))
                {
                    int last = s_gridCellPool.Count - 1;
                    if (last >= 0)
                    {
                        cell = s_gridCellPool[last];
                        s_gridCellPool.RemoveAt(last);
                    }
                    else
                    {
                        cell = new List<Unit>();
                    }
                    s_unitGrid[key] = cell;
                }
                cell.Add(u);
            }
        }

        // Fills s_gridCandidates with every unit in the cells that can lie within range of pos (superset; callers keep the exact distance test).
        private static List<Unit> QueryUnitGrid(Vector3 pos, float range)
        {
            s_gridCandidates.Clear();
            int span = Mathf.Max(1, Mathf.CeilToInt(range / s_gridCellSize));

            // Range much larger than a cell: take every occupied cell
            // instead of probing more cells than the grid holds
            long probes = (2L * span + 1) * (2L * span + 1);
            if (probes > s_unitGrid.Count)
            {
                foreach (KeyValuePair<long, List<Unit>> kv in s_unitGrid) s_gridCandidates.AddRange(kv.Value);
                return s_gridCandidates;
            }

            int cx = Mathf.FloorToInt(pos.x / s_gridCellSize);
            int cz = Mathf.FloorToInt(pos.z / s_gridCellSize);
            for (int x = cx - span; x <= cx + span; x++)
            {
                for (int z = cz - span; z <= cz + span; z++)
                {
                    List<Unit> cell;
                    if (s_unitGrid.TryGetValue(GridKey(x, z), out cell)) s_gridCandidates.AddRange(cell);
                }
            }
            return s_gridCandidates;
        }

        public static readonly DatalinkNetworkTelemetry LatestTelemetry = new DatalinkNetworkTelemetry();

        // Snapshot pools: every field is rewritten on reuse, so a cycle allocates nothing once warm
        private static readonly List<DatalinkNodeSnapshot> s_nodePool = new List<DatalinkNodeSnapshot>();
        private static readonly List<SharedRadarTrackSnapshot> s_radarTrackPool = new List<SharedRadarTrackSnapshot>();
        private static readonly List<SharedVisualContactSnapshot> s_visualPool = new List<SharedVisualContactSnapshot>();
        private static readonly List<RelayedMissileAlertSnapshot> s_missileAlertPool = new List<RelayedMissileAlertSnapshot>();

        private static T RentSnapshot<T>(List<T> pool, int index) where T : class, new()
        {
            if (index < pool.Count) return pool[index];
            T item = new T();
            pool.Add(item);
            return item;
        }

        public static Unit GetRadarAttachedUnit(Radar radar)
        {
            if (radar == null) return null;
            Unit u = radar.GetAttachedUnit();
            if (u != null) return u;
            return radar.GetComponentInParent<Unit>();
        }

        public static FactionHQ GetUnitFactionHQ(Unit unit)
        {
            if (unit == null) return null;
            if (unit.NetworkHQ != null) return unit.NetworkHQ;
            if (unit.MapHQ != null) return unit.MapHQ;

            // Slow path (scan of every HQ radar list) is cached for the current frame
            if (s_hqFallbackFrame != Time.frameCount)
            {
                s_hqFallbackFrame = Time.frameCount;
                s_hqFallbackCache.Clear();
            }
            FactionHQ cached;
            if (s_hqFallbackCache.TryGetValue(unit, out cached)) return cached;
            cached = FindHQByRadar(unit);
            s_hqFallbackCache[unit] = cached;
            return cached;
        }

        private static int s_hqFallbackFrame = -1;
        private static readonly Dictionary<Unit, FactionHQ> s_hqFallbackCache = new Dictionary<Unit, FactionHQ>();

        private static FactionHQ FindHQByRadar(Unit unit)
        {
            try
            {
                var allHqs = FactionRegistry.GetAllHQs();
                if (allHqs != null)
                {
                    foreach (FactionHQ hq in allHqs)
                    {
                        if (hq == null) continue;
                        List<Radar> hqRadars = GetHqRadars(hq);
                        if (hqRadars != null && unit.radar != null && hqRadars.Contains(unit.radar as Radar)) return hq;
                    }
                }
            }
            catch { }
            return null;
        }

        public static string ClassifyNodeRole(Unit unit)
        {
            if (unit == null) return "NODE";
            if (unit is Ship) return "WARSHIP";

            Aircraft aircraft = unit as Aircraft;
            if (aircraft != null)
            {
                if (MunitionClassification.HasJammerWeapon(aircraft)) return "AWACS";
                PhysRadarTarget target = PhysRadarTarget.Get(aircraft);
                if (target != null && target.Profile.IsHelicopter) return "HELO";
                RoleIdentity role = (aircraft.definition != null) ? aircraft.definition.roleIdentity : new RoleIdentity();
                if (role.antiSurface > role.antiAir) return "STRIKE";
                return "FIGHTER";
            }

            if ((unit is GroundVehicle || unit is Building) && ThreatClassification.IsTrackedThreat(unit, null)) return "SAM/GND";
            return "GROUND";
        }

        /// <summary>
        /// Determines whether a unit has operational sensors (radar, optics) or combat communications
        /// capable of participating in the Tactical Datalink network. Identified by vanilla types like
        /// ThreatClassification: Unit subclass, VehicleType / BuildingType and the TargetDetector sensor.
        /// Excludes non-sensor static structures such as hangars, warehouses, fuel tanks, and runways.
        /// </summary>
        public static bool IsDatalinkCapableUnit(Unit unit)
        {
            if (unit == null || unit.disabled || unit is Missile) return false;

            // Vanilla threat types: aircraft, ships, air-defense vehicles (AAA, IR/radar SAM, radar vehicle),
            // radar buildings and any radar carrier
            if (ThreatClassification.IsTrackedThreat(unit, null)) return true;

            // Any other unit with a working vanilla sensor (TargetDetector: radar or optics)
            if (unit.radar != null && unit.radar.IsOperational()) return true;
            Radar childRadar = unit.GetComponentInChildren<Radar>();
            if (childRadar != null && childRadar.IsOperational()) return true;

            // Static non-sensor structures (hangars, depots, fuel tanks, runways)
            if (unit is Building || unit is Scenery || unit is Container) return false;

            // Armed ground vehicles with weapons / targeting optics
            return unit.weaponStations != null && unit.weaponStations.Count > 0
                && (unit.definition == null || !unit.definition.IsObstacle);
        }

        public static void RegisterUnit(Unit unit)
        {
            if (ThreatClassification.IsStrategicBuilding(unit, null))
            {
                if (_strategicBuildingSet.Add(unit)) _strategicBuildings.Add(unit);
                return;
            }
            if (!IsDatalinkCapableUnit(unit)) return;
            if (_allOperationalUnitSet.Add(unit))
            {
                _allOperationalUnits.Add(unit);
            }
        }

        public static void DeregisterUnit(Unit unit)
        {
            if (unit == null) return;
            if (_allOperationalUnitSet.Remove(unit))
            {
                _allOperationalUnits.Remove(unit);
            }
            if (_strategicBuildingSet.Remove(unit))
            {
                _strategicBuildings.Remove(unit);
            }
        }

        public static void RegisterMissile(Missile missile)
        {
            if (missile == null) return;
            if (_activeMissileSet.Add(missile))
            {
                _activeMissiles.Add(missile);
            }
        }

        public static void DeregisterMissile(Missile missile)
        {
            if (missile == null) return;
            if (_activeMissileSet.Remove(missile))
            {
                _activeMissiles.Remove(missile);
            }
        }

        public static void RegisterRadar(Radar radar)
        {
            if (radar == null) return;
            if (_registeredRadarSet.Add(radar))
            {
                _registeredRadars.Add(radar);
            }

            try
            {
                PhysRadarEmitter emitter = PhysRadarEmitter.Get(radar);
                if (emitter == null)
                {
                    emitter = radar.gameObject.GetComponent<PhysRadarEmitter>();
                    if (emitter == null)
                    {
                        emitter = radar.gameObject.AddComponent<PhysRadarEmitter>();
                    }
                }
                if (emitter != null && emitter.Specs.BandwidthHz <= 0f)
                {
                    emitter.InitializeSpecs();
                }

                Unit attachedUnit = GetRadarAttachedUnit(radar);
                if (attachedUnit != null && IsDatalinkCapableUnit(attachedUnit))
                {
                    RegisterUnit(attachedUnit);
                }
            }
            catch { }
        }

        public static void DeregisterRadar(Radar radar)
        {
            if (radar == null) return;
            if (_registeredRadarSet.Remove(radar))
            {
                _registeredRadars.Remove(radar);
            }
        }

        /// <summary>
        /// Self-healing census that populates DatalinkNetwork from native game registries
        /// (FactionRegistry.GetAllHQs, FactionHQ.radars, UnitRegistry.allUnits, and FindObjectsOfType).
        /// Ensures 100% of ground radar stations, SAM batteries, and operational air/ground units
        /// are connected to the network regardless of scene load timing or script execution order.
        /// </summary>
        public static void CensusSceneUnitsAndRadars(bool fullReset = false)
        {
            if (fullReset)
            {
                ClearRegistries();
            }

            try
            {
                // 1. Ingest all radars from FactionRegistry HQs
                var allHqs = FactionRegistry.GetAllHQs();
                if (allHqs != null)
                {
                    foreach (FactionHQ hq in allHqs)
                    {
                        if (hq == null) continue;
                        List<Radar> hqRadars = GetHqRadars(hq);
                        if (hqRadars != null)
                        {
                            for (int r = 0; r < hqRadars.Count; r++)
                            {
                                Radar rad = hqRadars[r];
                                if (rad != null && rad.IsOperational())
                                {
                                    RegisterRadar(rad);
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            try
            {
                // 2. Ingest from UnitRegistry
                if (UnitRegistry.allUnits != null)
                {
                    for (int i = 0; i < UnitRegistry.allUnits.Count; i++)
                    {
                        Unit u = UnitRegistry.allUnits[i];
                        if (u != null && !u.disabled && ThreatClassification.IsStrategicBuilding(u, null))
                        {
                            RegisterUnit(u);
                        }
                        else if (u != null && !u.disabled && IsDatalinkCapableUnit(u))
                        {
                            RegisterUnit(u);
                            Radar rad = u.radar as Radar;
                            if (rad != null && rad.IsOperational())
                            {
                                RegisterRadar(rad);
                            }
                        }
                    }
                }
            }
            catch { }

            // 3. Fallback scan if radars or units are still empty
            if (_registeredRadars.Count == 0)
            {
                try
                {
                    Radar[] foundRadars = UnityEngine.Object.FindObjectsOfType<Radar>();
                    if (foundRadars != null)
                    {
                        for (int i = 0; i < foundRadars.Length; i++)
                        {
                            Radar rad = foundRadars[i];
                            if (rad != null && rad.IsOperational())
                            {
                                RegisterRadar(rad);
                            }
                        }
                    }
                }
                catch { }
            }

            if (_allOperationalUnits.Count == 0)
            {
                try
                {
                    Unit[] foundUnits = UnityEngine.Object.FindObjectsOfType<Unit>();
                    if (foundUnits != null)
                    {
                        for (int i = 0; i < foundUnits.Length; i++)
                        {
                            Unit u = foundUnits[i];
                            if (u != null && !u.disabled && IsDatalinkCapableUnit(u))
                            {
                                RegisterUnit(u);
                            }
                        }
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// Periodically called from Update loop to process allied sensor fusion, universal visual reconnaissance,
        /// and dispatch datalink warnings across all factions in the match.
        /// </summary>
        public static void ProcessDatalinkCycle()
        {
            if (CnoEvilConfig.EnableAlliedDatalink != null && !CnoEvilConfig.EnableAlliedDatalink.Value)
            {
                LatestTelemetry.NetworkActive = false;
                return;
            }

            float now = Time.time;
            if (now - _lastScanTime < CnoEvilConfig.DatalinkScanIntervalSeconds.Value)
            {
                return;
            }
            _lastScanTime = now;

            // Automated self-healing census (runs every 2.5s or whenever census is empty)
            if (now - _lastCensusTime > 2.5f || _registeredRadars.Count == 0 || _allOperationalUnits.Count == 0)
            {
                _lastCensusTime = now;
                CensusSceneUnitsAndRadars(false);
            }

            // 1. Clean up destroyed or disabled units from active registry
            for (int i = _allOperationalUnits.Count - 1; i >= 0; i--)
            {
                if (_allOperationalUnits[i] == null || _allOperationalUnits[i].disabled)
                {
                    _allOperationalUnitSet.Remove(_allOperationalUnits[i]);
                    _allOperationalUnits.RemoveAt(i);
                }
            }

            for (int i = _strategicBuildings.Count - 1; i >= 0; i--)
            {
                if (_strategicBuildings[i] == null || _strategicBuildings[i].disabled)
                {
                    _strategicBuildingSet.Remove(_strategicBuildings[i]);
                    _strategicBuildings.RemoveAt(i);
                }
            }

            // 2. Clean up destroyed missiles in active list
            for (int i = _activeMissiles.Count - 1; i >= 0; i--)
            {
                if (_activeMissiles[i] == null || _activeMissiles[i].disabled)
                {
                    _activeMissileSet.Remove(_activeMissiles[i]);
                    _activeMissiles.RemoveAt(i);
                }
            }

            // 3. Clean up destroyed radars
            for (int i = _registeredRadars.Count - 1; i >= 0; i--)
            {
                if (_registeredRadars[i] == null || !_registeredRadars[i].IsOperational())
                {
                    _registeredRadarSet.Remove(_registeredRadars[i]);
                    _registeredRadars.RemoveAt(i);
                }
            }

            // Prepare telemetry snapshot buffer for player's faction
            Aircraft playerAircraft = CombatHUD.i != null ? CombatHUD.i.aircraft : null;
            FactionHQ playerHq = (playerAircraft != null) ? playerAircraft.NetworkHQ : null;

            LatestTelemetry.TotalCyclesProcessed++;
            LatestTelemetry.LastCycleTimestamp = now;
            LatestTelemetry.CycleIntervalSeconds = CnoEvilConfig.DatalinkScanIntervalSeconds.Value;
            LatestTelemetry.PlayerHQ = playerHq;
            LatestTelemetry.FactionName = (playerHq != null && !string.IsNullOrEmpty(playerHq.name)) ? playerHq.name : "Allied Forces";
            LatestTelemetry.NetworkActive = true;

            LatestTelemetry.Nodes.Clear();
            LatestTelemetry.SharedRadarTracks.Clear();
            LatestTelemetry.SharedVisualContacts.Clear();
            LatestTelemetry.RelayedMissileAlerts.Clear();

            for (int i = 0; i < _allOperationalUnits.Count; i++)
            {
                Unit u = _allOperationalUnits[i];
                if (u == null || u.disabled || !IsDatalinkCapableUnit(u)) continue;
                FactionHQ uHq = GetUnitFactionHQ(u);
                if (playerHq == null || uHq == playerHq)
                {
                    DatalinkNodeSnapshot node = RentSnapshot(s_nodePool, LatestTelemetry.Nodes.Count);
                    node.Unit = u;
                    node.UnitName = !string.IsNullOrEmpty(u.unitName) ? u.unitName : u.name;
                    node.Role = ClassifyNodeRole(u);
                    node.Position = u.transform.position;
                    node.HasRadar = (u.radar != null && u.radar.IsOperational());
                    node.IsAirborne = (u is Aircraft);
                    node.SharedRadarCount = 0;
                    node.SharedVisualCount = 0;
                    LatestTelemetry.Nodes.Add(node);
                }
            }

            BuildUnitGrid();

            // 4. Visual reconnaissance of the player's faction. The contacts come from the vanilla optics of every spotter
            // (OnVisualDetection) and are held for the spotter's check interval. Each cycle copies the held ones to the F8
            // telemetry and relays held threats to the player's RWR (its datalink entries age out after 3.5 s).
            PhysRWRReceiver visualRelayRwr = (playerAircraft != null && !playerAircraft.disabled) ? PhysRWRReceiver.Get(playerAircraft) : null;
            s_expiredVisuals.Clear();
            foreach (KeyValuePair<Unit, SharedVisualContactSnapshot> kv in s_playerVisuals)
            {
                SharedVisualContactSnapshot held = kv.Value;
                if (kv.Key == null || kv.Key.disabled || now >= held.HoldUntil || playerHq == null)
                {
                    s_expiredVisuals.Add(kv.Key);
                    continue;
                }

                Unit heldTarget = held.TargetUnit;
                if (visualRelayRwr != null && held.SpotterUnit != null && held.SpotterUnit != playerAircraft &&
                    ThreatClassification.IsTrackedThreat(heldTarget, playerHq))
                {
                    Vector3 targetVel = heldTarget.rb != null ? heldTarget.rb.velocity : Vector3.zero;
                    RelayToPlayer(visualRelayRwr, held.SpotterUnit, heldTarget, heldTarget.transform.position, targetVel, false);
                }

                SharedVisualContactSnapshot vSnap = RentSnapshot(s_visualPool, LatestTelemetry.SharedVisualContacts.Count);
                vSnap.SpotterUnit = held.SpotterUnit;
                vSnap.SpotterName = held.SpotterName;
                vSnap.TargetUnit = held.TargetUnit;
                vSnap.TargetName = held.TargetName;
                vSnap.SpotterPos = held.SpotterPos;
                vSnap.TargetPos = held.TargetPos;
                vSnap.DistanceMeters = held.DistanceMeters;
                vSnap.Timestamp = held.Timestamp;
                vSnap.HoldUntil = held.HoldUntil;
                LatestTelemetry.SharedVisualContacts.Add(vSnap);

                for (int n = 0; n < LatestTelemetry.Nodes.Count; n++)
                {
                    if (LatestTelemetry.Nodes[n].Unit == held.SpotterUnit)
                    {
                        LatestTelemetry.Nodes[n].SharedVisualCount++;
                        break;
                    }
                }
            }
            for (int i = 0; i < s_expiredVisuals.Count; i++)
            {
                SharedVisualContactSnapshot expired;
                if (s_playerVisuals.TryGetValue(s_expiredVisuals[i], out expired)) s_heldVisualPool.Add(expired);
                s_playerVisuals.Remove(s_expiredVisuals[i]);
            }

            // 4b. Strategic building reconnaissance: an enemy spotter within StrategicBuildingVisualRangeMeters with
            // terrain line of sight identifies the building for its faction. Buildings do not move, so one
            // spotter per faction and cycle is enough.
            for (int b = 0; b < _strategicBuildings.Count; b++)
            {
                Unit building = _strategicBuildings[b];
                if (building == null || building.disabled) continue;

                Vector3 buildingPos = building.transform.position;
                FactionHQ buildingHq = (building.NetworkHQ != null) ? building.NetworkHQ : building.MapHQ;
                if (buildingHq == null) continue;
                float visualRange = RWRTriangulationProcessor.StrategicBuildingVisualRange;
                float rangeSq = visualRange * visualRange;
                s_spottedByFactions.Clear();

                List<Unit> buildingSpotters = QueryUnitGrid(buildingPos, visualRange);
                for (int s = 0; s < buildingSpotters.Count; s++)
                {
                    Unit spotter = buildingSpotters[s];
                    if (spotter == null || spotter.disabled) continue;
                    FactionHQ spotterHq = GetUnitFactionHQ(spotter);
                    if (spotterHq == null || spotterHq == buildingHq || s_spottedByFactions.Contains(spotterHq)) continue;
                    if ((spotter.transform.position - buildingPos).sqrMagnitude >= rangeSq) continue;

                    if (RWRTriangulationProcessor.CanSeeStrategicBuilding(spotter, building))
                    {
                        RWRTriangulationProcessor.RecordVisualContact(spotterHq, building, spotter);
                        s_spottedByFactions.Add(spotterHq);
                    }
                }
            }

            // 5. Universal Radar Illumination Fusion:
            // Operational radars automatically feed active tracks into their faction datalink
            for (int r = 0; r < _registeredRadars.Count; r++)
            {
                Radar alliedRadar = _registeredRadars[r];
                if (alliedRadar == null || !alliedRadar.IsOperational()) continue;
                Unit attachedUnit = GetRadarAttachedUnit(alliedRadar);
                if (attachedUnit == null || attachedUnit.disabled) continue;
                FactionHQ attachedHq = GetUnitFactionHQ(attachedUnit);
                if (attachedHq == null) continue;

                if (alliedRadar.detectedTargets != null && alliedRadar.detectedTargets.Count > 0)
                {
                    for (int d = 0; d < alliedRadar.detectedTargets.Count; d++)
                    {
                        Unit detected = alliedRadar.detectedTargets[d];
                        if (detected == null || detected.disabled) continue;

                        // Exclude friendly missiles from radar datalink tracks
                        Missile detMissile = detected as Missile;
                        if (detMissile != null)
                        {
                            bool isAlliedMissile = (detMissile.owner == playerAircraft) ||
                                (playerHq != null && ((detMissile.owner != null && detMissile.owner.NetworkHQ == playerHq) ||
                                                      (detMissile.NetworkHQ != null && detMissile.NetworkHQ == playerHq)));
                            if (isAlliedMissile) continue;
                        }

                        if (ThreatClassification.IsTrackedThreat(detected, attachedHq))
                        {
                            RWRTriangulationProcessor.RecordRadarIllumination(attachedHq, detected, alliedRadar);

                            if (playerHq != null && attachedHq == playerHq)
                            {
                                if (playerAircraft != null && !playerAircraft.disabled && attachedUnit != playerAircraft)
                                {
                                    PhysRWRReceiver playerRwr = PhysRWRReceiver.Get(playerAircraft);
                                    if (playerRwr != null)
                                    {
                                        Vector3 targetVel = detected.rb != null ? detected.rb.velocity : Vector3.zero;
                                        RelayToPlayer(playerRwr, 
                                            attachedUnit,
                                            detected,
                                            detected.transform.position,
                                            targetVel,
                                            detected is Missile
                                        );
                                    }
                                }

                                SharedRadarTrackSnapshot rSnap = RentSnapshot(s_radarTrackPool, LatestTelemetry.SharedRadarTracks.Count);
                                rSnap.SourceUnit = attachedUnit;
                                rSnap.SourceName = !string.IsNullOrEmpty(attachedUnit.unitName) ? attachedUnit.unitName : attachedUnit.name;
                                rSnap.TargetUnit = detected;
                                rSnap.TargetName = !string.IsNullOrEmpty(detected.unitName) ? detected.unitName : detected.name;
                                rSnap.SourcePos = alliedRadar.GetScanPoint() != null ? alliedRadar.GetScanPoint().position : attachedUnit.transform.position;
                                rSnap.TargetPos = detected.transform.position;
                                rSnap.SlantRangeMeters = Vector3.Distance(rSnap.SourcePos, rSnap.TargetPos);
                                rSnap.Timestamp = now;
                                LatestTelemetry.SharedRadarTracks.Add(rSnap);

                                for (int n = 0; n < LatestTelemetry.Nodes.Count; n++)
                                {
                                    if (LatestTelemetry.Nodes[n].Unit == attachedUnit)
                                    {
                                        LatestTelemetry.Nodes[n].SharedRadarCount++;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // 6. Local Player RWR Inbound Missile Alerts
            if (playerAircraft == null || playerAircraft.disabled) return;

            PhysRWRReceiver rwr = playerAircraft.GetComponent<PhysRWRReceiver>();
            if (rwr == null) return;

            Vector3 playerPos = playerAircraft.transform.position;

            // Scan all active airborne missiles
            for (int i = 0; i < _activeMissiles.Count; i++)
            {
                Missile m = _activeMissiles[i];
                if (m == null || m.disabled) continue;

                // Check if missile is friendly to player (fired by player or allied units)
                bool isFriendly = (m.owner == playerAircraft) ||
                    (playerHq != null && ((m.owner != null && m.owner.NetworkHQ == playerHq) ||
                                          (m.NetworkHQ != null && m.NetworkHQ == playerHq)));

                if (isFriendly) continue;

                Vector3 missilePos = m.transform.position;
                Vector3 missileVel = m.rb != null ? m.rb.velocity : Vector3.zero;
                float distToPlayer = Vector3.Distance(missilePos, playerPos);

                // Only evaluate missiles within 35 km of player
                if (distToPlayer > 35000.0f) continue;

                // Check if missile velocity is closing in toward player (heading threat)
                Vector3 toPlayer = playerPos - missilePos;
                bool isClosing = Vector3.Dot(missileVel, toPlayer.normalized) > 50.0f;
                bool isTargetingPlayer = (m.targetID == playerAircraft.persistentID);

                if (!isClosing && !isTargetingPlayer) continue;

                // Search for ANY allied radar that has detection coverage of this missile
                Unit spottingAlliedUnit = null;
                for (int rIdx = 0; rIdx < _registeredRadars.Count; rIdx++)
                {
                    Radar alliedRadar = _registeredRadars[rIdx];
                    if (alliedRadar == null || !alliedRadar.IsOperational()) continue;

                    Unit alliedUnit = GetRadarAttachedUnit(alliedRadar);
                    if (alliedUnit == null || alliedUnit == playerAircraft) continue;

                    FactionHQ alliedHq = GetUnitFactionHQ(alliedUnit);
                    if (playerHq != null && alliedHq != null && alliedHq != playerHq)
                    {
                        continue;
                    }

                    Vector3 radarPos = alliedRadar.GetScanPoint() != null ? alliedRadar.GetScanPoint().position : alliedRadar.transform.position;
                    float distToMissile = Vector3.Distance(radarPos, missilePos);

                    // Cheap geometric pre-filters: radar max range and forward cone
                    if (distToMissile > alliedRadar.RadarParameters.maxRange) continue;

                    // Check radar forward cone if limited
                    float cone = (f_radarCone != null) ? f_radarCone(alliedRadar) : 360.0f;
                    if (cone < 350.0f)
                    {
                        Vector3 radarFwd = alliedRadar.transform.forward;
                        Vector3 dirToMissile = (missilePos - radarPos).normalized;
                        if (Vector3.Angle(radarFwd, dirToMissile) > cone * 0.5f)
                        {
                            continue;
                        }
                    }

                    // Full radar physics (RCS, SNR, horizon, Doppler notch, terrain) via the patched CanSeeRadarReturn
                    if (!alliedRadar.CanSeeRadarReturn(m, distToMissile, 0f))
                    {
                        continue;
                    }

                    // Spotting successful! Allied radar has solid track on inbound munition!
                    spottingAlliedUnit = alliedUnit;
                    break;
                }

                // If allied unit spotted the incoming missile, transmit Datalink alert to player's RWR
                if (spottingAlliedUnit != null)
                {
                    RelayToPlayer(rwr, 
                        spottingAlliedUnit,
                        m,
                        missilePos,
                        missileVel,
                        true
                    );

                    RelayedMissileAlertSnapshot mSnap = RentSnapshot(s_missileAlertPool, LatestTelemetry.RelayedMissileAlerts.Count);
                    mSnap.RadarUnit = spottingAlliedUnit;
                    mSnap.RadarName = !string.IsNullOrEmpty(spottingAlliedUnit.unitName) ? spottingAlliedUnit.unitName : spottingAlliedUnit.name;
                    mSnap.MissileUnit = m;
                    mSnap.MissilePos = missilePos;
                    mSnap.MissileVel = missileVel;
                    mSnap.DistToPlayer = distToPlayer;
                    mSnap.Timestamp = now;
                    LatestTelemetry.RelayedMissileAlerts.Add(mSnap);
                }
            }
        }

        private static void ClearRegistries()
        {
            _allOperationalUnits.Clear();
            _allOperationalUnitSet.Clear();
            _strategicBuildings.Clear();
            _strategicBuildingSet.Clear();
            s_spottedByFactions.Clear();
            _activeMissiles.Clear();
            _activeMissileSet.Clear();
            _registeredRadars.Clear();
            _registeredRadarSet.Clear();
        }

        // Basic (TADIL-class) datalink: allied track updates reach the receiver at most every 2 s.
        // Advanced (sensor-fusion) datalink receives every network cycle.
        private static readonly Dictionary<Unit, float> s_lastRelayTime = new Dictionary<Unit, float>();

        // Visual contacts of the player's faction (target -> held contact), for the F8 telemetry; pooled
        private static readonly Dictionary<Unit, SharedVisualContactSnapshot> s_playerVisuals = new Dictionary<Unit, SharedVisualContactSnapshot>();
        private static readonly List<SharedVisualContactSnapshot> s_heldVisualPool = new List<SharedVisualContactSnapshot>();
        private static readonly List<Unit> s_expiredVisuals = new List<Unit>();

        /// <summary>
        /// A vanilla TargetDetector (optics, not a radar) of spotter saw target (TargetDetector_DetectTarget_Patch).
        /// The spotter's faction gets a visual contact held for holdSeconds. For the player's faction the contact is also
        /// kept for the datalink cycle (F8 telemetry, RWR relay of threats, step 4). Without the datalink the player's
        /// faction gets only the ownship contacts (same rule as the radar detections).
        /// </summary>
        public static void OnVisualDetection(Unit spotter, Unit target, float holdSeconds)
        {
            if (spotter == null || target == null || spotter.disabled || target.disabled || target is Missile) return;
            FactionHQ spotterHq = GetUnitFactionHQ(spotter);
            if (spotterHq == null) return;
            if (!ThreatClassification.HasQualityTrack(target, spotterHq)) return;

            Aircraft playerAircraft = CombatHUD.i != null ? CombatHUD.i.aircraft : null;
            FactionHQ playerHq = (playerAircraft != null) ? playerAircraft.NetworkHQ : null;
            bool datalinkEnabled = CnoEvilConfig.EnableAlliedDatalink == null || CnoEvilConfig.EnableAlliedDatalink.Value;
            bool playerFaction = playerHq != null && spotterHq == playerHq;
            if (!datalinkEnabled && playerFaction && spotter != playerAircraft) return;

            RWRTriangulationProcessor.RecordVisualContact(spotterHq, target, spotter, holdSeconds);
            if (!playerFaction) return;

            float now = Time.time;
            Vector3 spotterPos = spotter.transform.position;
            Vector3 targetPos = target.transform.position;
            SharedVisualContactSnapshot held;
            if (!s_playerVisuals.TryGetValue(target, out held))
            {
                int last = s_heldVisualPool.Count - 1;
                if (last >= 0)
                {
                    held = s_heldVisualPool[last];
                    s_heldVisualPool.RemoveAt(last);
                }
                else
                {
                    held = new SharedVisualContactSnapshot();
                }
                s_playerVisuals[target] = held;
            }
            held.SpotterUnit = spotter;
            held.SpotterName = !string.IsNullOrEmpty(spotter.unitName) ? spotter.unitName : spotter.name;
            held.TargetUnit = target;
            held.TargetName = !string.IsNullOrEmpty(target.unitName) ? target.unitName : target.name;
            held.SpotterPos = spotterPos;
            held.TargetPos = targetPos;
            held.DistanceMeters = Vector3.Distance(spotterPos, targetPos);
            held.Timestamp = now;
            held.HoldUntil = now + Mathf.Max(1.0f, holdSeconds);
        }

        private static void RelayToPlayer(PhysRWRReceiver rwr, Unit sourceUnit, Unit threat, Vector3 position, Vector3 velocity, bool isMissile)
        {
            if (rwr == null || threat == null) return;

            if (rwr.Capabilities.Datalink != DatalinkTier.Advanced)
            {
                float now = Time.time;
                float last;
                if (s_lastRelayTime.TryGetValue(threat, out last) && now - last < CnoEvilConfig.BasicDatalinkRelayIntervalSeconds.Value) return;
                s_lastRelayTime[threat] = now;
            }

            rwr.RegisterDatalinkThreat(sourceUnit, threat, position, velocity, isMissile);
        }

        public static void ClearAll()
        {
            s_lastRelayTime.Clear();
            s_playerVisuals.Clear();
            s_heldVisualPool.Clear();
            s_expiredVisuals.Clear();
            ClearRegistries();
            s_hqFallbackCache.Clear();
            LatestTelemetry.Nodes.Clear();
            LatestTelemetry.SharedRadarTracks.Clear();
            LatestTelemetry.SharedVisualContacts.Clear();
            LatestTelemetry.RelayedMissileAlerts.Clear();
            LatestTelemetry.NetworkActive = false;
            s_nodePool.Clear();
            s_radarTrackPool.Clear();
            s_visualPool.Clear();
            s_missileAlertPool.Clear();
            s_unitGrid.Clear();
            s_gridCellPool.Clear();
            s_gridCandidates.Clear();
        }
    }
}
