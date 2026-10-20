using System.Collections.Generic;
using UnityEngine;
using CnoEvil.Config;
using CnoEvil.Patches;

namespace CnoEvil.Core
{
    /// <summary>
    /// Munition audit log (MunitionAuditLog or DebugLogging): one line per launch (weapon, seeker class, launcher,
    /// target, Q, CEP, miss vector) and one per end of flight (time, Q, CEP, miss to target and to the dispersed
    /// datum, target state, cause). Writes only on these events, never per frame. Host or single player only.
    /// </summary>
    public static class MunitionAudit
    {
        private sealed class Record
        {
            public string Weapon;
            public SeekerKind Kind;
            public string Launcher;
            public Unit Target;
            public float LaunchTime;
            public float LaunchDistance;
            public float Quality = -1.0f;
            public float Cep;
            public Vector3 Dispersion;
            public bool HasDispersion;
            public bool Launched;
            public int Retargets;
            public bool TargetDropped;

            // Target watch (Unit.onDisableUnit): when and where the current target died
            public Unit Watched;
            public System.Action<Unit> OnTargetDisabled;
            public float TargetDeathTime = -1.0f;
            public GlobalPosition TargetDeathPos;

            public bool Detonated;
            public float DetonationTime;
            public GlobalPosition DetonationPos;
            public bool HasTargetAtDetonation;
            public GlobalPosition TargetPosAtDetonation;
            public bool HitArmor;
            public bool HitTerrain;
            public bool MissedTarget;
            public bool LosingGround;
        }

        private static readonly Dictionary<int, Record> s_records = new Dictionary<int, Record>();

        /// <summary>
        /// A mirror client neither detonates munitions (Missile.Detonate is server only) nor draws the dispersion.
        /// </summary>
        public static bool Enabled
        {
            get
            {
                if (CnoEvilNet.IsMirrorClient) return false;
                return (CnoEvilConfig.MunitionAuditLog != null && CnoEvilConfig.MunitionAuditLog.Value) ||
                       (CnoEvilConfig.DebugLogging != null && CnoEvilConfig.DebugLogging.Value);
            }
        }

        public static void ClearAll()
        {
            foreach (Record r in s_records.Values) Unwatch(r);
            s_records.Clear();
        }

        private static Record GetRecord(Missile missile)
        {
            int id = missile.GetInstanceID();
            Record r;
            if (!s_records.TryGetValue(id, out r))
            {
                r = new Record();
                s_records[id] = r;
            }
            return r;
        }

        private static string NameOf(Unit unit)
        {
            if (unit == null) return "none";
            return !string.IsNullOrEmpty(unit.unitName) ? unit.unitName : unit.name;
        }

        private static void Watch(Record r, Unit target)
        {
            if ((object)r.Watched != null && r.Watched == target) return;
            Unwatch(r);
            r.TargetDeathTime = -1.0f;
            if (target == null) return;
            if (target.disabled)
            {
                r.TargetDeathTime = Time.timeSinceLevelLoad;
                r.TargetDeathPos = target.GlobalPosition();
                return;
            }
            if (r.OnTargetDisabled == null)
            {
                Record rec = r;
                r.OnTargetDisabled = delegate(Unit u)
                {
                    if (rec.TargetDeathTime >= 0.0f || u == null) return;
                    rec.TargetDeathTime = Time.timeSinceLevelLoad;
                    rec.TargetDeathPos = u.GlobalPosition();
                };
            }
            target.onDisableUnit += r.OnTargetDisabled;
            r.Watched = target;
        }

        private static void Unwatch(Record r)
        {
            if ((object)r.Watched != null && r.OnTargetDisabled != null)
            {
                try { r.Watched.onDisableUnit -= r.OnTargetDisabled; }
                catch { }
            }
            r.Watched = null;
        }

        /// <summary>
        /// The launch dispersion was drawn (SeekerDispersionHelper.GetDispersionForMissile).
        /// </summary>
        public static void OnDispersion(Missile missile, Unit target, float quality, float cep, Vector3 dispersion)
        {
            if (missile == null || !Enabled) return;
            Record r = GetRecord(missile);
            r.Quality = quality;
            r.Cep = cep;
            r.Dispersion = dispersion;
            r.HasDispersion = true;
            if (r.Target == null) r.Target = target;
        }

        /// <summary>
        /// Missile.StartMissile, after the target is resolved.
        /// </summary>
        public static void OnLaunch(Unit launcher, Missile missile, Unit target)
        {
            if (missile == null || !Enabled || CnoEvilPlugin.Log == null) return;
            Record r = GetRecord(missile);
            if (r.Launched) return;
            r.Launched = true;

            MunitionProfile p = MunitionCatalog.Get(missile);
            r.Weapon = p.Name;
            r.Kind = p.Kind;
            Unit owner = (missile.owner != null) ? missile.owner : launcher;
            r.Launcher = NameOf(owner);
            if (target != null) r.Target = target;
            Watch(r, r.Target);
            r.LaunchTime = Time.timeSinceLevelLoad;

            string at;
            if (r.Target != null)
            {
                r.LaunchDistance = Vector3.Distance(missile.transform.position, r.Target.transform.position);
                at = string.Format("{0}: distance {1:F0} m", NameOf(r.Target), r.LaunchDistance);
            }
            else
            {
                GlobalPosition aim;
                float d = (SeekerDispersionHelper.TryGetSteerPoint(missile, out aim) && aim != default(GlobalPosition))
                    ? Vector3.Distance(missile.transform.position, aim.ToLocalPosition()) : 0.0f;
                at = (d > 1.0f) ? string.Format("map point: distance {0:F0} m", d) : "no target";
            }

            CnoEvilPlugin.Log.LogInfo(string.Format(
                "[CNO-EVIL audit] launch {0} ({1}) by {2} at {3}, {4}",
                r.Weapon, r.Kind, r.Launcher, at, FormatTrack(r)));
        }

        /// <summary>
        /// Missile.TargetIDChanged in flight: a new target, or the target dropped.
        /// </summary>
        public static void OnRetarget(Missile missile, Unit target)
        {
            if (missile == null || !Enabled) return;
            Record r;
            if (!s_records.TryGetValue(missile.GetInstanceID(), out r) || !r.Launched || r.Detonated) return;
            if (target == null)
            {
                if (r.Target != null) r.TargetDropped = true;
                return;
            }
            if (target == r.Target) return;
            r.Retargets++;
            r.Target = target;
            r.TargetDropped = false;
            Watch(r, target);
        }

        /// <summary>
        /// Missile.Detonate (server). The first call counts.
        /// </summary>
        public static void OnDetonate(Missile missile, bool hitArmor, bool hitTerrain)
        {
            if (missile == null || !Enabled) return;
            Record r;
            if (!s_records.TryGetValue(missile.GetInstanceID(), out r) || r.Detonated) return;
            r.Detonated = true;
            r.DetonationTime = Time.timeSinceLevelLoad;
            r.DetonationPos = missile.GlobalPosition();
            r.HitArmor = hitArmor;
            r.HitTerrain = hitTerrain;
            if (r.Target != null && !r.Target.disabled)
            {
                r.HasTargetAtDetonation = true;
                r.TargetPosAtDetonation = r.Target.GlobalPosition();
            }
            try
            {
                r.MissedTarget = missile.MissedTarget();
                r.LosingGround = missile.LosingGround();
            }
            catch { }
        }

        /// <summary>
        /// Unit.DeregisterMissile: end of flight.
        /// </summary>
        public static void OnEnd(Missile missile)
        {
            if ((object)missile == null) return;
            int id = missile.GetInstanceID();
            Record r;
            if (!s_records.TryGetValue(id, out r)) return;
            s_records.Remove(id);
            Unwatch(r);
            if (!r.Launched || !Enabled || CnoEvilPlugin.Log == null) return;

            float endTime = r.Detonated ? r.DetonationTime : Time.timeSinceLevelLoad;
            Vector3 endPos = r.Detonated ? r.DetonationPos.ToLocalPosition()
                : (missile != null ? missile.transform.position : Vector3.zero);

            // Target reference point: where it died if that was before the end of flight, else where it was at the
            // detonation, else where it is now
            string miss = "no target";
            string state = "";
            float missToTarget = -1.0f;
            if (r.Target != null)
            {
                Vector3 targetPos;
                if (r.TargetDeathTime >= 0.0f && r.TargetDeathTime < endTime)
                {
                    targetPos = r.TargetDeathPos.ToLocalPosition();
                    state = string.Format("target destroyed {0:F1} s before end (miss to its last position)", endTime - r.TargetDeathTime);
                }
                else if (r.HasTargetAtDetonation)
                {
                    targetPos = r.TargetPosAtDetonation.ToLocalPosition();
                    state = (r.TargetDeathTime >= 0.0f) ? "target destroyed at impact" : "target alive at impact";
                }
                else
                {
                    targetPos = r.Target.transform.position;
                    state = r.Target.disabled ? "target destroyed" : "target alive";
                }

                Vector3 d = endPos - targetPos;
                missToTarget = d.magnitude;
                miss = string.Format("{0:F0} m ({1:F0} m horizontal) to target", missToTarget, new Vector2(d.x, d.z).magnitude);
                if (r.HasDispersion)
                {
                    Vector3 dd = endPos - (targetPos + new Vector3(r.Dispersion.x, 0.0f, r.Dispersion.z));
                    miss += string.Format(", {0:F0} m ({1:F0} m horizontal) to datum", dd.magnitude, new Vector2(dd.x, dd.z).magnitude);
                }
            }
            if (r.Retargets > 0) state += string.Format("{0}retargets {1}", state.Length > 0 ? ", " : "", r.Retargets);
            if (r.TargetDropped) state += (state.Length > 0 ? ", " : "") + "target dropped";

            string cause;
            if (!r.Detonated) cause = "removed without detonation";
            else if (r.HitArmor)
            {
                // Missile.Detonate does not say which unit was hit: far from the target means another one
                float hitRadius = (r.Target != null) ? Mathf.Max(20.0f, r.Target.maxRadius * 2.0f) : 0.0f;
                cause = (r.Target != null && missToTarget > hitRadius) ? "hit armor of another unit" : "hit armor";
            }
            else if (r.HitTerrain) cause = (r.DetonationPos.y < 2.0f) ? "hit water or terrain at sea level" : "hit terrain";
            else if (r.MissedTarget) cause = "missed target (aim point behind)";
            else if (r.LosingGround) cause = "losing ground";
            else cause = "fuze or self-destruct";

            CnoEvilPlugin.Log.LogInfo(string.Format(
                "[CNO-EVIL audit] end {0} ({1}) by {2} at {3}: flight {4:F1} s, {5}, miss {6}{7}{8}, cause: {9}",
                r.Weapon, r.Kind, r.Launcher, NameOf(r.Target), endTime - r.LaunchTime, FormatTrack(r), miss,
                state.Length > 0 ? ", " : "", state, cause));
        }

        private static string FormatTrack(Record r)
        {
            if (!r.HasDispersion) return "no dispersion";
            string q = (r.Quality >= 0.0f) ? r.Quality.ToString("F2") : "no track";
            return string.Format("Q {0}, CEP {1:F0} m, miss vector {2:F0} m", q, r.Cep, r.Dispersion.magnitude);
        }
    }
}
