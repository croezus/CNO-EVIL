using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using CnoEvil.Core;
using CnoEvil.Config;

namespace CnoEvil.Patches
{
    /// <summary>
    /// Shared state of the seeker patches: the launch dispersion of each munition, the datum the launching
    /// faction knows, seeker lock state for the displays, and the radar warning RPC.
    ///
    /// Vanilla-first rule: the seeker patches never call Missile.SetAimpoint or Missile.SetTarget. They move the
    /// vanilla datum (knownPos) by the launch dispersion, or hold it when the faction lost the target, and the
    /// vanilla seeker flies, searches, fuzes and detonates. Targets the mod keeps no quality track on
    /// (ThreatClassification.HasQualityTrack: trucks, tanks, ordinary buildings) stay fully vanilla.
    /// </summary>
    public static class SeekerDispersionHelper
    {
        private static readonly Func<Missile, Unit> getMissileTarget = FastReflection.CreateFieldGetter<Missile, Unit>("target");
        private static readonly Func<MissileSeeker, Unit> getSeekerTarget = FastReflection.CreateFieldGetter<MissileSeeker, Unit>("targetUnit");
        private static readonly Func<ARHSeeker, bool> f_arhLock = FastReflection.CreateFieldGetter<ARHSeeker, bool>("radarLockEstablished");
        private static readonly Func<OpticalSeeker, bool> f_optVisual = FastReflection.CreateFieldGetter<OpticalSeeker, bool>("hasVisual");
        private static readonly Func<OpticalSeekerCruiseMissile, bool> f_cruiseTerminal = FastReflection.CreateFieldGetter<OpticalSeekerCruiseMissile, bool>("terminalMode");
        private static readonly Func<OpticalSeekerBomb, bool> f_bombVisual = FastReflection.CreateFieldGetter<OpticalSeekerBomb, bool>("hasVisual");
        private static readonly Func<ARMSeeker, Radar> f_armRadar = FastReflection.CreateFieldGetter<ARMSeeker, Radar>("targetedRadar");
        private static readonly Func<SARHSeeker, float> f_sarhStrength = FastReflection.CreateFieldGetter<SARHSeeker, float>("trackingStrength");
        private static readonly Func<SARHSeeker, float> f_sarhTimeWithoutTrack = FastReflection.CreateFieldGetter<SARHSeeker, float>("timeWithoutTrack");

        private static readonly Func<OpticalSeekerShell, bool> f_shellVisual = FastReflection.CreateFieldGetter<OpticalSeekerShell, bool>("hasVisual");
        private static readonly Func<LaserSeeker, bool> f_laserLock = FastReflection.CreateFieldGetter<LaserSeeker, bool>("hasLock");
        private static readonly Func<IRSeeker, bool> f_irAchievedLock = FastReflection.CreateFieldGetter<IRSeeker, bool>("achievedLock");
        private static readonly Func<IRSeeker, IRSource> f_irTarget = FastReflection.CreateFieldGetter<IRSeeker, IRSource>("IRTarget");
        private static readonly Func<Missile, GlobalPosition> f_missileAimPoint = FastReflection.CreateFieldGetter<Missile, GlobalPosition>("aimPoint");
        private static readonly Func<Missile, MissileSeeker> f_missileSeeker = FastReflection.CreateFieldGetter<Missile, MissileSeeker>("seeker");
        private static readonly Func<OpticalSeekerCruiseMissile, GlobalPosition> f_cruiseKnownPos = FastReflection.CreateFieldGetter<OpticalSeekerCruiseMissile, GlobalPosition>("knownPos");

        // Missiles whose datum came from the faction track at least once: without a track they hold the datum
        private static readonly HashSet<int> s_hasTrackDatum = new HashSet<int>();
        // Missiles whose seeker found the real target once
        private static readonly HashSet<int> s_seekerAcquired = new HashSet<int>();

        /// <summary>
        /// World position the missile is steering to before terminal seeker lock. Cruise missile: the seeker datum
        /// (Missile.aimPoint is the vanilla terrain waypoint, a moving point about 6 s ahead of the missile).
        /// Other seekers: the target plus the launch dispersion drawn from the faction's track quality, else the
        /// missile's own aim point, else the target.
        /// </summary>
        public static bool TryGetGuidanceAimPoint(Missile missile, Unit target, out Vector3 worldPos)
        {
            worldPos = Vector3.zero;
            if (missile == null) return false;

            GlobalPosition steer;
            if (GetSeeker(missile) is OpticalSeekerCruiseMissile)
            {
                // Mirror client: the seeker runs on the host; the datum comes from MissileSync
                if (CnoEvilNet.IsMirrorClient ? MissileSync.TryGetAim(missile, out steer) : TryGetSteerPoint(missile, out steer))
                {
                    worldPos = steer.ToLocalPosition();
                    return true;
                }
            }

            bool hasTarget = target != null && !target.disabled;
            Vector3 disp;
            if (hasTarget && TrackUncertaintyCalculator.TryGetMissileDispersion(missile, out disp) && disp != Vector3.zero)
            {
                worldPos = target.transform.position + new Vector3(disp.x, 0f, disp.z);
                return true;
            }
            // Mirror client: the missile guides on the host; its aim point comes from MissileSync
            GlobalPosition mirrorAim;
            if (CnoEvilNet.IsMirrorClient && MissileSync.TryGetAim(missile, out mirrorAim))
            {
                worldPos = mirrorAim.ToLocalPosition();
                return true;
            }
            if (TryGetSteerPoint(missile, out steer))
            {
                worldPos = steer.ToLocalPosition();
                return true;
            }
            if (hasTarget)
            {
                worldPos = target.transform.position;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Point the missile steers to, from its own guidance state: the seeker datum (knownPos) of a cruise missile,
        /// else Missile.aimPoint.
        /// </summary>
        public static bool TryGetSteerPoint(Missile missile, out GlobalPosition point)
        {
            point = default(GlobalPosition);
            if (missile == null) return false;
            OpticalSeekerCruiseMissile cruise = GetSeeker(missile) as OpticalSeekerCruiseMissile;
            if (cruise != null && f_cruiseKnownPos != null)
            {
                point = f_cruiseKnownPos(cruise);
                return true;
            }
            if (f_missileAimPoint != null)
            {
                point = f_missileAimPoint(missile);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Faction whose tracks guide the missile: its own HQ, else its launcher's. The player's faction is used
        /// only for the player's own missiles (GetTrack(null) would silently return the player's tracks).
        /// </summary>
        public static FactionHQ GetMissileFaction(Missile missile)
        {
            if (missile == null) return null;
            if (missile.NetworkHQ != null) return missile.NetworkHQ;
            if (missile.owner != null && missile.owner.NetworkHQ != null) return missile.owner.NetworkHQ;
            Aircraft player = (CombatHUD.i != null) ? CombatHUD.i.aircraft : null;
            return (player != null && missile.owner == player) ? RWRTriangulationProcessor.GetPlayerFactionHQ() : null;
        }

        /// <summary>
        /// The mod guides this missile's datum: the target is a hostile tracked threat (aircraft, ship, air defense,
        /// radar), or a strategic building the faction has a track on. All other targets fly fully vanilla
        /// (no dispersion, vanilla datalink). A strategic building without a track keeps the vanilla datum, because
        /// vanilla knows the strategic targets of the mission from the start.
        /// </summary>
        public static bool IsModGuided(Missile missile, Unit target)
        {
            if (missile == null || target == null || target.disabled) return false;
            FactionHQ hq = GetMissileFaction(missile);
            if (hq == null) return false;
            if (ThreatClassification.IsTrackedThreat(target, hq)) return true;
            TriangulationTrack track;
            return ThreatClassification.IsStrategicBuilding(target, hq) && RWRTriangulationProcessor.TryPeekTrack(hq, target, out track);
        }

        private const float RadarWarningRpcIntervalSeconds = 0.5f;
        private static readonly Dictionary<long, float> s_lastRadarWarning = new Dictionary<long, float>();

        /// <summary>
        /// Aircraft.RpcGetRadarWarning is a [ClientRpc] to every observer: send it from the server only and at most
        /// every 0.5 s per emitter / target pair (seekers update at 4 Hz). The aircraft of the host gets the warning
        /// locally; an AI aircraft gets it locally on the server, where its AI runs (AIPilotCombatModes descends when
        /// painted; RadarLocator is gated by RadarLocator_OnRadarWarning_Patch); a player with the mod gets it from the
        /// RWR feed (RwrFeedSync); only a player without the mod gets the vanilla RPC.
        /// </summary>
        public static void SendRadarWarning(Aircraft target, Unit emitter)
        {
            if (target == null || emitter == null || !target.IsServer) return;
            if (!PassRateLimit(target, emitter)) return;

            if ((CombatHUD.i != null && target == CombatHUD.i.aircraft) || target.Player == null)
            {
                InvokeLocalRadarWarning(target, emitter);
                return;
            }
            if (CnoEvilNet.FindPeer(target) != null)
            {
                // The RPC would also run on a listen host (server listeners, for example RadarLocator); keep that
                if (target.IsClient) InvokeLocalRadarWarning(target, emitter);
                return;
            }

            try { target.RpcGetRadarWarning(emitter); } catch { }
        }

        private static bool PassRateLimit(Aircraft target, Unit emitter)
        {
            long key = ((long)target.GetInstanceID() << 32) ^ (uint)emitter.GetInstanceID();
            float now = Time.time;
            float last;
            if (s_lastRadarWarning.TryGetValue(key, out last) && now - last < RadarWarningRpcIntervalSeconds) return false;
            if (s_lastRadarWarning.Count > 2048) s_lastRadarWarning.Clear();
            s_lastRadarWarning[key] = now;
            return true;
        }

        private static Action<Aircraft, Unit> s_radarWarningUserCode;
        private static bool s_radarWarningLookupDone;

        /// <summary>
        /// Runs the body of the vanilla warning RPC on this computer only (map ping, TacScreen, sound), at most every
        /// 0.5 s per emitter / target pair. The body has a generated name, so it is found by its prefix.
        /// </summary>
        public static void InvokeLocalRadarWarning(Aircraft target, Unit emitter)
        {
            if (target == null || emitter == null) return;
            if (!s_radarWarningLookupDone)
            {
                s_radarWarningLookupDone = true;
                try
                {
                    MethodInfo[] methods = typeof(Aircraft).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    for (int i = 0; i < methods.Length; i++)
                    {
                        if (methods[i].Name.StartsWith("UserCode_RpcGetRadarWarning", StringComparison.Ordinal) && methods[i].GetParameters().Length == 1)
                        {
                            s_radarWarningUserCode = (Action<Aircraft, Unit>)Delegate.CreateDelegate(typeof(Action<Aircraft, Unit>), methods[i]);
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    PatchErrorLog.Once("SeekerDispersionHelper.InvokeLocalRadarWarning", ex);
                }
            }
            if (s_radarWarningUserCode == null) return;
            try { s_radarWarningUserCode(target, emitter); }
            catch (Exception ex) { PatchErrorLog.Once("SeekerDispersionHelper.InvokeLocalRadarWarning", ex); }
        }

        /// <summary>
        /// Mirror client: the RWR feed brought a new ping of this emitter.
        /// </summary>
        public static void OnRemotePing(Aircraft target, Unit emitter)
        {
            if (target == null || emitter == null) return;
            if (!PassRateLimit(target, emitter)) return;
            InvokeLocalRadarWarning(target, emitter);
        }

        /// <summary>
        /// A human player flies the aircraft: the local player, or a remote player (Aircraft.Player is a SyncVar, set on
        /// the server). The below-threshold paint (EM scan ring dip) is shown only to humans.
        /// </summary>
        public static bool ShouldSendRadarWarning(Aircraft target)
        {
            if (target == null) return false;
            if (CombatHUD.i != null && target == CombatHUD.i.aircraft) return true;
            return target.Player != null;
        }

        public static void ClearAll()
        {
            s_lastRadarWarning.Clear();
            s_hasTrackDatum.Clear();
            s_seekerAcquired.Clear();
        }

        /// <summary>
        /// Removes the per-missile datum state (Unit.DeregisterMissile).
        /// </summary>
        public static void ForgetMissile(Missile missile)
        {
            if ((object)missile == null) return;
            int id = missile.GetInstanceID();
            s_hasTrackDatum.Remove(id);
            s_seekerAcquired.Remove(id);
        }

        /// <summary>
        /// Target state the missile's own faction knows, for datalink midcourse guidance:
        /// actively detected -> live position + launch dispersion and target velocity;
        /// in track memory -> last known position (contact or fix center) + dispersion, zero velocity;
        /// no track (the faction does not know the target) -> false.
        /// </summary>
        public static bool TryGetDatalinkTargetState(Missile missile, Unit target, out GlobalPosition position, out Vector3 velocity)
        {
            position = default(GlobalPosition);
            velocity = Vector3.zero;
            if (missile == null || target == null || target.disabled) return false;

            FactionHQ hq = GetMissileFaction(missile);
            TriangulationTrack track;
            if (hq == null || !RWRTriangulationProcessor.TryPeekTrack(hq, target, out track) || !track.HadActiveContact) return false;

            Vector3 basePos;
            if (track.IsActivelyDetected)
            {
                basePos = target.transform.position;
                velocity = (target.rb != null) ? target.rb.velocity : Vector3.zero;
            }
            else if (track.IsInMemoryState() && track.LastKnownTime > 0.0f)
            {
                basePos = track.LastKnownGlobalPosition.ToLocalPosition();
            }
            else
            {
                return false;
            }

            Vector3 disp = GetDispersionForMissile(missile, target);
            basePos.x += disp.x;
            basePos.z += disp.z; // Altitude (Y) preserved at the known target level
            position = basePos.ToGlobalPosition();
            return true;
        }

        /// <summary>
        /// Common guard of the seeker patches: the mod is on, and the seeker and its missile exist.
        /// </summary>
        public static bool ShouldRun(UnityEngine.Object seeker, Missile missile)
        {
            return CnoEvilConfig.IsModActive && seeker != null && missile != null;
        }

        /// <summary>
        /// The seeker found the real target once (visual, radar lock, terminal attack): from now on the datum is
        /// the seeker's own and the mod does not move it again.
        /// </summary>
        public static void MarkSeekerAcquired(Missile missile)
        {
            if ((object)missile != null) s_seekerAcquired.Add(missile.GetInstanceID());
        }

        public static bool HasSeekerAcquired(Missile missile)
        {
            return (object)missile != null && s_seekerAcquired.Contains(missile.GetInstanceID());
        }

        /// <summary>
        /// Datum at launch (seeker Initialize postfix): the faction track + dispersion, else the vanilla datum +
        /// dispersion. No change for a target the mod does not guide. Y stays at the target level of the datum.
        /// </summary>
        public static void ApplyLaunchDatum(Missile missile, Unit target, ref GlobalPosition knownPos, ref Vector3 knownVel)
        {
            if (!IsModGuided(missile, target)) return;

            GlobalPosition trackPos;
            Vector3 trackVel;
            if (TryGetDatalinkTargetState(missile, target, out trackPos, out trackVel))
            {
                knownPos = trackPos;
                knownVel = trackVel;
                s_hasTrackDatum.Add(missile.GetInstanceID());
                return;
            }
            Vector3 disp = GetDispersionForMissile(missile, target);
            knownPos = knownPos + new Vector3(disp.x, 0.0f, disp.z);
        }

        /// <summary>
        /// Datum after a vanilla datalink update (postfix, with the datum before the update):
        /// faction track -> track position + dispersion;
        /// no track, but the datum came from a track before -> the datum before the update (the faction does not
        /// learn more than it knows);
        /// no track yet -> the vanilla update stands, + dispersion when addDispersionToVanilla (seekers whose vanilla
        /// update does not add an error of its own).
        /// No change for a target the mod does not guide, or after the seeker found the real target.
        /// </summary>
        public static void CorrectDatum(Missile missile, Unit target, ref GlobalPosition knownPos, ref Vector3 knownVel,
            GlobalPosition before, bool addDispersionToVanilla)
        {
            if (!IsModGuided(missile, target) || HasSeekerAcquired(missile)) return;

            GlobalPosition trackPos;
            Vector3 trackVel;
            if (TryGetDatalinkTargetState(missile, target, out trackPos, out trackVel))
            {
                knownPos = trackPos;
                knownVel = trackVel;
                s_hasTrackDatum.Add(missile.GetInstanceID());
                return;
            }
            if (s_hasTrackDatum.Contains(missile.GetInstanceID()))
            {
                knownPos = before;
                knownVel = Vector3.zero;
                return;
            }
            if (addDispersionToVanilla && knownPos != before)
            {
                Vector3 disp = GetDispersionForMissile(missile, target);
                knownPos = knownPos + new Vector3(disp.x, 0.0f, disp.z);
            }
        }

        /// <summary>
        /// Limits the registered dispersion of the missile to maxMeters (vanilla rules that drop the target when
        /// the datum is too far from it). Returns the dispersion after the limit.
        /// </summary>
        public static Vector3 LimitDispersion(Missile missile, Unit target, float maxMeters)
        {
            Vector3 disp = GetDispersionForMissile(missile, target);
            if (disp.magnitude > maxMeters)
            {
                disp = disp.normalized * maxMeters;
                TrackUncertaintyCalculator.RegisterMissileDispersion(missile, disp);
            }
            return disp;
        }

        public static MissileSeeker GetSeeker(Missile missile)
        {
            if (missile == null) return null;
            MissileSeeker seeker = (f_missileSeeker != null) ? f_missileSeeker(missile) : null;
            return seeker != null ? seeker : missile.GetComponent<MissileSeeker>();
        }

        public static Unit GetTargetForMissile(Missile missile)
        {
            if (missile == null) return null;
            Unit target = null;
            if (getMissileTarget != null)
            {
                target = getMissileTarget(missile);
            }
            if (target == null && getSeekerTarget != null)
            {
                MissileSeeker seeker = GetSeeker(missile);
                if (seeker != null)
                {
                    target = getSeekerTarget(seeker);
                }
            }
            return target;
        }

        public static bool IsSeekerTerminalLocked(Missile missile)
        {
            if (missile == null) return false;
            // Mirror client: the seeker runs on the host only
            bool mirrorLocked;
            if (CnoEvilNet.IsMirrorClient && MissileSync.TryGetLocked(missile, out mirrorLocked)) return mirrorLocked;
            MissileSeeker seeker = GetSeeker(missile);
            if (seeker == null) return false;

            ARHSeeker arh = seeker as ARHSeeker;
            if (arh != null)
            {
                return f_arhLock != null && f_arhLock(arh);
            }
            OpticalSeeker opt = seeker as OpticalSeeker;
            if (opt != null)
            {
                return f_optVisual != null && f_optVisual(opt);
            }
            OpticalSeekerCruiseMissile cruise = seeker as OpticalSeekerCruiseMissile;
            if (cruise != null)
            {
                return f_cruiseTerminal != null && f_cruiseTerminal(cruise);
            }
            OpticalSeekerBomb bomb = seeker as OpticalSeekerBomb;
            if (bomb != null)
            {
                return f_bombVisual != null && f_bombVisual(bomb);
            }
            OpticalSeekerShell shell = seeker as OpticalSeekerShell;
            if (shell != null)
            {
                return f_shellVisual != null && f_shellVisual(shell);
            }
            ARMSeeker arm = seeker as ARMSeeker;
            if (arm != null)
            {
                return f_armRadar != null && f_armRadar(arm) != null;
            }
            SARHSeeker sarh = seeker as SARHSeeker;
            if (sarh != null)
            {
                return f_sarhStrength != null && f_sarhStrength(sarh) > 0.05f &&
                       f_sarhTimeWithoutTrack != null && f_sarhTimeWithoutTrack(sarh) < 0.1f;
            }
            LaserSeeker laser = seeker as LaserSeeker;
            if (laser != null)
            {
                return f_laserLock != null && f_laserLock(laser);
            }
            IRSeeker ir = seeker as IRSeeker;
            if (ir != null)
            {
                return f_irAchievedLock != null && f_irAchievedLock(ir) &&
                       f_irTarget != null && f_irTarget(ir) != null;
            }
            return false;
        }

        /// <summary>
        /// Launch dispersion of the missile, drawn once: a Gaussian miss with the CEP of the faction's track
        /// (Q and launch distance). Zero for a target the mod does not guide.
        /// </summary>
        public static Vector3 GetDispersionForMissile(Missile missile, Unit target)
        {
            if (missile == null) return Vector3.zero;
            Vector3 offset;
            if (TrackUncertaintyCalculator.TryGetMissileDispersion(missile, out offset))
            {
                return offset;
            }
            // Mirror client: the host draws the dispersion and sends it (MissileSync)
            if (CnoEvilNet.IsMirrorClient) return Vector3.zero;

            if (target == null) target = GetTargetForMissile(missile);
            if (target == null) return Vector3.zero;

            if (!IsModGuided(missile, target))
            {
                TrackUncertaintyCalculator.RegisterMissileDispersion(missile, Vector3.zero);
                return Vector3.zero;
            }

            FactionHQ hq = GetMissileFaction(missile);
            TriangulationTrack track;
            float quality = RWRTriangulationProcessor.TryPeekTrack(hq, target, out track) ? track.TrackingQuality : 0.0f;
            Vector3 from = (missile.owner != null) ? missile.owner.transform.position : missile.transform.position;
            float cep = TrackUncertaintyCalculator.CalculateCep(quality, Vector3.Distance(from, target.transform.position));

            offset = TrackUncertaintyCalculator.GenerateHorizontalDispersion(cep);
            TrackUncertaintyCalculator.RegisterMissileDispersion(missile, offset);
            return offset;
        }

        /// <summary>
        /// CEP (m) a weapon fired now on this track gets (CalculateCep). 0 when track dispersion is off.
        /// </summary>
        public static float PredictLaunchCep(TriangulationTrack track, float launchDistance)
        {
            if (!CnoEvilConfig.EnableTrackUncertaintyDispersion.Value) return 0.0f;
            float quality = (track != null) ? track.TrackingQuality : 0.0f;
            return TrackUncertaintyCalculator.CalculateCep(quality, launchDistance);
        }
    }
}
