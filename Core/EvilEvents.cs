using System;
using System.Collections.Generic;
using UnityEngine;
using CnoEvil.Components;
using CnoEvil.Patches;

namespace CnoEvil.Core
{
    /// <summary>
    /// State changes for the session log (EvilLog): missile flights, faction tracks, scan results that involve the player,
    /// the self-protection jammer, and the snapshot of the mark key. Read-only for the game: it keeps only its own last
    /// states, and it logs a change, not each frame. A caller tests EvilLog.On first.
    /// </summary>
    public static class EvilEvents
    {
        private const float TrackRecheckSeconds = 3.0f;
        private const float RadarRecheckSeconds = 2.0f;
        private const float MissileCheckSeconds = 0.25f;
        private const float LaunchHoldLogSeconds = 20.0f;
        private const float JammerOffAfterSeconds = 1.0f;
        private const float JammerEmptyFraction = 0.02f;
        private const float TelemetryMaxAgeSeconds = 5.0f;
        private const int MarkMaxMissiles = 30;

        private sealed class MissileState
        {
            public string Label;
            public Unit Target;
            public float StartTime;
            public bool Locked;
            public bool EverLocked;
        }

        private sealed class TrackState
        {
            public int Band;
            public bool Memory;
            public bool Located;
            public float NextCheck;
        }

        private sealed class RadarState
        {
            public TelemetryDetection Outcome;
            public float NextCheck;
        }

        private sealed class JammerState
        {
            public Aircraft Aircraft;
            public float StartTime;
            public float LastFireTime;
            public bool EmptyLogged;
        }

        private static readonly Func<CombatHUD, List<Unit>> s_getTargetList =
            FastReflection.CreateFieldGetter<CombatHUD, List<Unit>>("targetList");

        private static readonly Dictionary<int, MissileState> s_missiles = new Dictionary<int, MissileState>();
        private static readonly Dictionary<long, TrackState> s_tracks = new Dictionary<long, TrackState>();
        private static readonly Dictionary<long, RadarState> s_radars = new Dictionary<long, RadarState>();
        private static readonly Dictionary<int, JammerState> s_jammers = new Dictionary<int, JammerState>();
        private static readonly List<int> s_jammersOff = new List<int>();

        private static float s_nextMissileCheck;
        private static int s_markCount;

        // Missiles

        /// <summary>
        /// Missile.StartMissile, after the launch dispersion is drawn.
        /// </summary>
        public static void MissileLaunched(Missile missile, Unit target)
        {
            try
            {
                Unit owner = missile.owner;
                MunitionProfile profile = MunitionCatalog.Get(missile);
                string label = profile.Name + "#" + missile.GetInstanceID();

                MissileState state = new MissileState();
                state.Label = label;
                state.Target = target;
                state.StartTime = Time.timeSinceLevelLoad;
                s_missiles[missile.GetInstanceID()] = state;

                FactionHQ hq = (owner != null) ? DatalinkNetwork.GetUnitFactionHQ(owner) : null;
                TriangulationTrack track = null;
                if (hq != null && target != null) RWRTriangulationProcessor.TryPeekTrack(hq, target, out track);
                Vector3 offset;
                TrackUncertaintyCalculator.TryGetMissileDispersion(missile, out offset);
                float range = (target != null) ? Vector3.Distance(missile.transform.position, target.transform.position) : 0.0f;

                EvilLog.Write(LogCat.Missile, "launch {0} ({1}) by {2} at {3}: range {4:F0} m, {5}, aim offset {6:F0} m",
                    label, profile.Kind, EvilLog.Name(owner), EvilLog.Name(target), range,
                    TrackText(track), offset.magnitude);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("EvilEvents.MissileLaunched", ex);
            }
        }

        /// <summary>
        /// Unit.DeregisterMissile: the end of the flight.
        /// </summary>
        public static void MissileEnded(Missile missile)
        {
            try
            {
                MissileState state;
                int id = missile.GetInstanceID();
                if (!s_missiles.TryGetValue(id, out state)) return;
                s_missiles.Remove(id);

                Unit target = state.Target;
                bool targetThere = target != null;
                EvilLog.Write(LogCat.Missile, "end {0} after {1:F1} s: target {2}, {3}, seeker lock {4}",
                    state.Label, Time.timeSinceLevelLoad - state.StartTime, EvilLog.Name(target),
                    targetThere ? string.Format("distance {0:F0} m{1}", Vector3.Distance(missile.transform.position, target.transform.position),
                        target.disabled ? ", target disabled" : "") : "target gone",
                    state.EverLocked ? "achieved" : "never");
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("EvilEvents.MissileEnded", ex);
            }
        }

        /// <summary>
        /// Plugin Update: seeker lock and loss of the missiles in flight.
        /// </summary>
        public static void Tick()
        {
            if (!EvilLog.On(LogCat.Missile) || s_missiles.Count == 0) return;
            float now = Time.unscaledTime;
            if (now < s_nextMissileCheck) return;
            s_nextMissileCheck = now + MissileCheckSeconds;

            List<Missile> missiles = DatalinkNetwork.ActiveMissiles;
            for (int i = 0; i < missiles.Count; i++)
            {
                Missile missile = missiles[i];
                MissileState state;
                if (missile == null || !s_missiles.TryGetValue(missile.GetInstanceID(), out state)) continue;

                bool locked = SeekerDispersionHelper.IsSeekerTerminalLocked(missile);
                if (locked == state.Locked) continue;
                state.Locked = locked;
                if (locked) state.EverLocked = true;
                EvilLog.Write(LogCat.Missile, "{0} seeker {1} after {2:F1} s, target distance {3:F0} m",
                    state.Label, locked ? "lock" : "lock lost", Time.timeSinceLevelLoad - state.StartTime,
                    state.Target != null ? Vector3.Distance(missile.transform.position, state.Target.transform.position) : 0.0f);
            }
        }

        // AI

        /// <summary>
        /// The AI doctrine holds a launch. value and limit are the numbers of the failed rule (both 0 when it has none).
        /// </summary>
        public static void LaunchHold(Unit launcher, Unit target, WeaponInfo weapon, string reason, float value, float limit)
        {
            try
            {
                string key = "hold " + (launcher != null ? launcher.GetInstanceID() : 0) + " " + (target != null ? target.GetInstanceID() : 0) + " " + reason;
                EvilLog.Every(LogCat.AI, key, LaunchHoldLogSeconds, "{0} holds {1} at {2}: {3}{4}",
                    EvilLog.Name(launcher), MunitionCatalog.Get(weapon).Name, EvilLog.Name(target), reason,
                    limit > 0.0f ? string.Format(" ({0:F2} < {1:F2})", value, limit) : "");
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("EvilEvents.LaunchHold", ex);
            }
        }

        // Tracks

        /// <summary>
        /// Track update of a faction: new track, change of Q band, memory state and located state. A track is checked at
        /// most each TrackRecheckSeconds, so a Q that moves around a band edge gives few lines.
        /// </summary>
        public static void TrackTick(FactionHQ hq, Unit target, TriangulationTrack track, float now)
        {
            try
            {
                long key = TrackKey(hq, target);
                int band = QBand(track.TrackingQuality);
                bool memory = track.IsInMemoryState();
                TrackState state;
                if (!s_tracks.TryGetValue(key, out state))
                {
                    state = new TrackState();
                    state.Band = band;
                    state.Memory = memory;
                    state.Located = track.PassiveLocated;
                    state.NextCheck = now + TrackRecheckSeconds;
                    s_tracks[key] = state;
                    EvilLog.Write(LogCat.Track, "{0} new track {1}: {2}", FactionName(hq), EvilLog.Name(target), TrackText(track));
                    return;
                }

                if (now < state.NextCheck) return;
                if (band == state.Band && memory == state.Memory && track.PassiveLocated == state.Located) return;

                int oldBand = state.Band;
                state.Band = band;
                state.Memory = memory;
                state.Located = track.PassiveLocated;
                state.NextCheck = now + TrackRecheckSeconds;
                EvilLog.Write(LogCat.Track, "{0} track {1}: Q band {2} -> {3}, {4}", FactionName(hq), EvilLog.Name(target), oldBand, band, TrackText(track));
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("EvilEvents.TrackTick", ex);
            }
        }

        /// <summary>
        /// A track leaves the faction.
        /// </summary>
        public static void TrackForgotten(FactionHQ hq, Unit target, string reason)
        {
            try
            {
                if (s_tracks.Remove(TrackKey(hq, target)))
                {
                    EvilLog.Write(LogCat.Track, "{0} forgets {1}: {2}", FactionName(hq), EvilLog.Name(target), reason);
                }
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("EvilEvents.TrackForgotten", ex);
            }
        }

        // Radar

        /// <summary>
        /// Result of a radar scan. Only a scan by or of the player aircraft, and only a change of the result, at most each
        /// RadarRecheckSeconds for one radar and target.
        /// </summary>
        public static void RadarOutcome(Radar radar, Unit radarUnit, Unit target, TelemetryDetection outcome, float range, float requiredSnr, ref RadarSkinSignal sig)
        {
            try
            {
                Unit player = (CombatHUD.i != null) ? CombatHUD.i.aircraft : null;
                if (player == null || target is Missile || (radarUnit != player && target != player)) return;

                long key = ((long)radar.GetInstanceID() << 32) ^ (uint)target.GetInstanceID();
                float now = Time.timeSinceLevelLoad;
                RadarState state;
                if (!s_radars.TryGetValue(key, out state))
                {
                    state = new RadarState();
                    s_radars[key] = state;
                }
                else if (state.Outcome == outcome || now < state.NextCheck)
                {
                    return;
                }
                state.Outcome = outcome;
                state.NextCheck = now + RadarRecheckSeconds;

                EvilLog.Write(LogCat.Radar, "{0} -> {1}: {2}, range {3:F0} m, SNR {4:F1} dB (need {5:F1}), RCS {6:F3} m2, jam {7:E1} W, burn-through {8:F0} m",
                    EvilLog.Name(radarUnit), EvilLog.Name(target), outcome, range, sig.SnrDb, requiredSnr, sig.FilteredRcs, sig.JammerPower, sig.BurnRange);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("EvilEvents.RadarOutcome", ex);
            }
        }

        // Electronic warfare

        /// <summary>
        /// Each Fire of a self-protection jammer: fraction is the output (charge of the supply, 0-1).
        /// </summary>
        public static void JammerFire(Aircraft aircraft, float fraction)
        {
            try
            {
                float now = Time.timeSinceLevelLoad;
                int id = aircraft.GetInstanceID();
                JammerState state;
                if (!s_jammers.TryGetValue(id, out state))
                {
                    state = new JammerState();
                    state.Aircraft = aircraft;
                    state.StartTime = now;
                    s_jammers[id] = state;
                    EvilLog.Write(LogCat.EW, "jammer on: {0}, charge {1:F2}", EvilLog.Name(aircraft), fraction);
                }
                state.LastFireTime = now;

                if (fraction > JammerEmptyFraction)
                {
                    state.EmptyLogged = false;
                }
                else if (!state.EmptyLogged)
                {
                    state.EmptyLogged = true;
                    EvilLog.Write(LogCat.EW, "jammer out of charge: {0} after {1:F1} s", EvilLog.Name(aircraft), now - state.StartTime);
                }
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("EvilEvents.JammerFire", ex);
            }
        }

        /// <summary>
        /// Plugin housekeeping (2 s): a jammer that stopped firing.
        /// </summary>
        public static void Housekeeping()
        {
            if (s_jammers.Count == 0) return;
            float now = Time.timeSinceLevelLoad;
            s_jammersOff.Clear();
            foreach (KeyValuePair<int, JammerState> entry in s_jammers)
            {
                if (now - entry.Value.LastFireTime > JammerOffAfterSeconds) s_jammersOff.Add(entry.Key);
            }
            for (int i = 0; i < s_jammersOff.Count; i++)
            {
                JammerState state = s_jammers[s_jammersOff[i]];
                s_jammers.Remove(s_jammersOff[i]);
                EvilLog.Write(LogCat.EW, "jammer off: {0} after {1:F1} s", EvilLog.Name(state.Aircraft), state.LastFireTime - state.StartTime);
            }
        }

        // Player

        /// <summary>
        /// The player gets an aircraft.
        /// </summary>
        public static void PlayerAircraft(Aircraft aircraft)
        {
            NuclearOption.SavedMission.Mission mission = MissionManager.CurrentMission;
            EvilLog.Write(LogCat.Data, "player aircraft {0}, mission {1}", EvilLog.Name(aircraft), mission != null ? mission.Name : "none");
        }

        /// <summary>
        /// Mark key: a numbered mark and what the player has now. Returns the mark number, 0 when the session log is off.
        /// </summary>
        public static int Mark()
        {
            if (!EvilLog.On(LogCat.Mark)) return 0;
            s_markCount++;
            try
            {
                EvilLog.Write(LogCat.Mark, "=== MARK {0} ===", s_markCount);
                Aircraft player = (CombatHUD.i != null) ? CombatHUD.i.aircraft : null;
                FactionHQ hq = RWRTriangulationProcessor.GetPlayerFactionHQ();
                EvilLog.Write(LogCat.Mark, "player {0}, speed {1:F0} m/s, faction {2}, mod active {3}", EvilLog.Name(player),
                    (player != null && player.rb != null) ? player.rb.velocity.magnitude : 0.0f, FactionName(hq), CnoEvil.Config.CnoEvilConfig.IsModActive);

                List<Unit> targets = (CombatHUD.i != null && s_getTargetList != null) ? s_getTargetList(CombatHUD.i) : null;
                if (targets != null)
                {
                    for (int i = 0; i < targets.Count; i++) MarkTarget(player, hq, targets[i]);
                }
                MarkMissiles();
                MarkTelemetry();
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("EvilEvents.Mark", ex);
            }
            return s_markCount;
        }

        public static void ClearAll()
        {
            s_missiles.Clear();
            s_tracks.Clear();
            s_radars.Clear();
            s_jammers.Clear();
            s_jammersOff.Clear();
            s_nextMissileCheck = 0.0f;
        }

        private static void MarkTarget(Unit player, FactionHQ hq, Unit target)
        {
            if (target == null) return;
            TriangulationTrack track = null;
            if (hq != null) RWRTriangulationProcessor.TryPeekTrack(hq, target, out track);
            EvilLog.Write(LogCat.Mark, "target {0}: range {1:F0} m, {2}", EvilLog.Name(target),
                player != null ? Vector3.Distance(player.transform.position, target.transform.position) : 0.0f, TrackText(track));
        }

        private static void MarkMissiles()
        {
            List<Missile> missiles = DatalinkNetwork.ActiveMissiles;
            int count = Mathf.Min(missiles.Count, MarkMaxMissiles);
            for (int i = 0; i < count; i++)
            {
                Missile missile = missiles[i];
                if (missile == null) continue;
                Unit target = SeekerDispersionHelper.GetTargetForMissile(missile);
                EvilLog.Write(LogCat.Mark, "missile #{0} by {1} at {2}: target distance {3:F0} m, seeker lock {4}", missile.GetInstanceID(),
                    EvilLog.Name(missile.owner), EvilLog.Name(target),
                    target != null ? Vector3.Distance(missile.transform.position, target.transform.position) : 0.0f,
                    SeekerDispersionHelper.IsSeekerTerminalLocked(missile));
            }
        }

        // The F9 data exists only while F9 or F11 is on
        private static void MarkTelemetry()
        {
            RadarTelemetrySnapshot scan = RadarTelemetry.Latest;
            if (scan.HasTarget && Time.time - scan.LastUpdateTime < TelemetryMaxAgeSeconds)
            {
                EvilLog.Write(LogCat.Mark, "scan {0} -> {1}: {2}, SNR {3:F1} dB (need {4:F1}), RCS {5:F3} m2, notch state {6}",
                    scan.RadarName, scan.TargetName, scan.Detection, scan.SNRdB, scan.RequiredSNRdB, scan.FilteredRCS, scan.NotchState);
            }
            List<ActiveRadarIlluminator> rows = RadarTelemetry.GetActiveIlluminators();
            for (int i = 0; i < rows.Count; i++)
            {
                ActiveRadarIlluminator row = rows[i];
                EvilLog.Write(LogCat.Mark, "on player {0} ({1}): {2}, {3} {4:F1} dB, range {5:F0} m{6}", row.SourceName, row.PlatformType, row.State,
                    row.SignalLabel, row.SNRdB, row.DistanceMeters, row.Note != null ? ", " + row.Note : "");
            }
        }

        private static string TrackText(TriangulationTrack track)
        {
            if (track == null) return "no track";
            string source;
            if (track.IsLased) source = "laser";
            else if (track.HasVisualContact) source = "visual";
            else if (track.HasRecentPreciseContact) source = "radar";
            else if (track.IsInMemoryState()) source = "memory";
            else source = "passive";
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "Q {0:F2}, {1}{2}, sensor range {3:F0} m",
                track.TrackingQuality, source, track.PassiveLocated ? ", located" : "", track.SensorRangeMeters);
        }

        private static string FactionName(FactionHQ hq)
        {
            return (hq != null && hq.faction != null) ? hq.faction.factionName : "no faction";
        }

        private static int QBand(float q)
        {
            return Mathf.Clamp((int)(q * 4.0f), 0, 3);
        }

        private static long TrackKey(FactionHQ hq, Unit target)
        {
            int hqId = ((object)hq != null) ? hq.GetInstanceID() : 0;
            int targetId = ((object)target != null) ? target.GetInstanceID() : 0;
            return ((long)hqId << 32) ^ (uint)targetId;
        }
    }
}
