using System.Collections;
using UnityEngine;
using CnoEvil.Config;
using CnoEvil.Patches;

namespace CnoEvil.Core
{
    /// <summary>
    /// Countermeasure programs: one key releases a package of flares, chaff and decoys (Program1* / Program2* config).
    /// The types are released in turn (flare, chaff, decoy, flare, ...) through the vanilla station path: the program
    /// selects the station and sets the trigger with Aircraft.Countermeasures, waits until the station ammo goes down,
    /// releases the trigger and waits the program interval. Vanilla sends the station index and the trigger over the
    /// network, so no mod message is necessary. While a program runs, other calls of Aircraft.Countermeasures for the
    /// aircraft are blocked (Aircraft_Countermeasures_Patch): the player controls release the trigger each frame when
    /// the countermeasure button is not held. At the end the station that the pilot selected is selected again.
    /// Only the local player aircraft.
    /// </summary>
    public static class CountermeasureProgram
    {
        private const int TypeCount = 3;
        private const int Flare = 0;
        private const int Chaff = 1;
        private const int Decoy = 2;

        // Seconds to wait for a release before the cartridge counts as released (round trip of a client, lost trigger)
        private const float ReleaseTimeout_s = 1.0f;
        // Vanilla player controls: no countermeasures below this radar altitude (m)
        private const float MinRadarAltitude_m = 0.2f;

        private static readonly int[] s_station = new int[TypeCount];
        private static readonly int[] s_remaining = new int[TypeCount];

        private static Aircraft s_aircraft;
        private static byte s_restoreIndex;
        private static float s_interval;
        private static int s_nextType;
        private static int s_firingType = -1;
        private static int s_ammoBefore;
        private static float s_phaseTime;
        private static bool s_issuing;

        public static bool IsRunning
        {
            get { return s_aircraft != null; }
        }

        /// <summary>
        /// A call of Aircraft.Countermeasures that does not come from the running program of this aircraft.
        /// </summary>
        public static bool BlocksCall(Aircraft aircraft)
        {
            return !s_issuing && s_aircraft != null && aircraft == s_aircraft;
        }

        public static void Update()
        {
            try
            {
                Tick();
            }
            catch (System.Exception ex)
            {
                PatchErrorLog.Once("CountermeasureProgram.Update", ex);
                Finish();
            }
        }

        private static void Tick()
        {
            if (s_aircraft != null)
            {
                Step();
                return;
            }
            if (!CnoEvilConfig.IsModActive) return;

            if (Pressed(CnoEvilConfig.Program1Key.Value))
            {
                Start(CnoEvilConfig.Program1Flares.Value, CnoEvilConfig.Program1Chaff.Value, CnoEvilConfig.Program1Decoys.Value,
                    CnoEvilConfig.Program1Interval_s.Value);
            }
            else if (Pressed(CnoEvilConfig.Program2Key.Value))
            {
                Start(CnoEvilConfig.Program2Flares.Value, CnoEvilConfig.Program2Chaff.Value, CnoEvilConfig.Program2Decoys.Value,
                    CnoEvilConfig.Program2Interval_s.Value);
            }
        }

        private static bool Pressed(KeyCode key)
        {
            return key != KeyCode.None && Input.GetKeyDown(key);
        }

        private static void Start(int flares, int chaff, int decoys, float interval)
        {
            Aircraft aircraft;
            if (!GameManager.GetLocalAircraft(out aircraft) || aircraft == null || aircraft.disabled) return;
            if (aircraft.radarAlt <= MinRadarAltitude_m) return;
            CountermeasureManager manager = aircraft.countermeasureManager;
            IList stations = ChaffDispenserSetup.Stations(manager);
            if (stations == null) return;

            for (int t = 0; t < TypeCount; t++) s_station[t] = -1;
            for (int i = 0; i < stations.Count && i < 255; i++)
            {
                object station = stations[i];
                int type;
                if (DecoyDispenserSetup.IsDecoyStation(station)) type = Decoy;
                else if (ChaffDispenserSetup.IsChaffStation(station)) type = Chaff;
                else if (ChaffDispenserSetup.FirstCountermeasure(station) is FlareEjector) type = Flare;
                else continue;
                if (s_station[type] < 0) s_station[type] = i;
            }

            s_remaining[Flare] = Mathf.Max(0, flares);
            s_remaining[Chaff] = Mathf.Max(0, chaff);
            s_remaining[Decoy] = Mathf.Max(0, decoys);
            bool any = false;
            for (int t = 0; t < TypeCount; t++)
            {
                int index = s_station[t];
                if (index < 0 || CountermeasureManager_NextCountermeasure_Patch.Blocked(manager, index) ||
                    ChaffDispenserSetup.StationAmmo(stations[index]) <= 0)
                {
                    s_remaining[t] = 0;
                }
                if (s_remaining[t] > 0) any = true;
            }
            if (!any) return;

            s_aircraft = aircraft;
            s_restoreIndex = manager.activeIndex;
            s_interval = Mathf.Max(0.0f, interval);
            s_nextType = Flare;
            s_firingType = -1;
            s_phaseTime = Time.time;
        }

        private static void Step()
        {
            Aircraft current;
            bool local = GameManager.GetLocalAircraft(out current) && current == s_aircraft;
            if (!CnoEvilConfig.IsModActive || !local || s_aircraft.disabled)
            {
                Finish();
                return;
            }
            IList stations = ChaffDispenserSetup.Stations(s_aircraft.countermeasureManager);
            if (stations == null)
            {
                Finish();
                return;
            }

            float now = Time.time;
            if (s_firingType >= 0)
            {
                int index = s_station[s_firingType];
                int ammo = (index < stations.Count) ? ChaffDispenserSetup.StationAmmo(stations[index]) : 0;
                int released = s_ammoBefore - ammo;
                if (released <= 0 && ammo > 0 && now - s_phaseTime < ReleaseTimeout_s + EjectionInterval(s_firingType)) return;

                Issue(false, (byte)index);
                // A dispenser with ejection grouping releases more than one cartridge at a time
                s_remaining[s_firingType] = (ammo > 0) ? Mathf.Max(0, s_remaining[s_firingType] - Mathf.Max(1, released)) : 0;
                s_firingType = -1;
                s_phaseTime = now;
                return;
            }

            if (now - s_phaseTime < s_interval) return;
            int type = NextType();
            if (type < 0)
            {
                Finish();
                return;
            }
            int station = s_station[type];
            if (station >= stations.Count)
            {
                s_remaining[type] = 0;
                return;
            }
            s_ammoBefore = ChaffDispenserSetup.StationAmmo(stations[station]);
            if (s_ammoBefore <= 0)
            {
                s_remaining[type] = 0;
                return;
            }
            Issue(true, (byte)station);
            s_firingType = type;
            s_phaseTime = now;
        }

        /// <summary>
        /// The next type in turn that still has cartridges to release; -1 when the program is complete.
        /// </summary>
        private static int NextType()
        {
            for (int k = 0; k < TypeCount; k++)
            {
                int type = (s_nextType + k) % TypeCount;
                if (s_remaining[type] > 0)
                {
                    s_nextType = (type + 1) % TypeCount;
                    return type;
                }
            }
            return -1;
        }

        private static float EjectionInterval(int type)
        {
            if (type == Chaff) return Mathf.Max(0.05f, CnoEvilConfig.ChaffEjectionInterval_s.Value);
            if (type == Decoy) return Mathf.Max(0.1f, CnoEvilConfig.DecoyEjectionInterval_s.Value);
            return 0.0f;
        }

        private static void Issue(bool active, byte index)
        {
            s_issuing = true;
            try
            {
                s_aircraft.Countermeasures(active, index);
            }
            finally
            {
                s_issuing = false;
            }
        }

        /// <summary>
        /// Releases the trigger and selects the station of the pilot again.
        /// </summary>
        private static void Finish()
        {
            Aircraft aircraft = s_aircraft;
            s_aircraft = null;
            s_firingType = -1;
            if (aircraft == null || aircraft.countermeasureManager == null) return;
            try
            {
                s_issuing = true;
                aircraft.Countermeasures(false, s_restoreIndex);
            }
            catch (System.Exception ex)
            {
                PatchErrorLog.Once("CountermeasureProgram.Finish", ex);
            }
            finally
            {
                s_issuing = false;
            }
        }

        public static void ClearAll()
        {
            s_aircraft = null;
            s_firingType = -1;
            s_issuing = false;
        }
    }
}
