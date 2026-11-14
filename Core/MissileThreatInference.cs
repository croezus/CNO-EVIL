using System;
using System.Collections.Generic;
using UnityEngine;
using CnoEvil.Config;

namespace CnoEvil.Core
{
    /// <summary>
    /// Air defense does not know which unit a hostile missile is aimed at; it sees only the missile's flight path.
    /// The perceived target is the hostile unit (to the missile) closest to the missile's velocity line:
    /// closest point of approach, 3D for aircraft, horizontal for surface units (loft and dive do not count).
    /// Vanilla Missile.InterceptPriority, Turret.AssessTargetPriority and CombatAI.AnalyzeTarget read this
    /// instead of the true target (transpilers in MissilePatches). While the mod is off: the vanilla target.
    /// </summary>
    public static class MissileThreatInference
    {
        // Prediction horizon (s) along the velocity line
        internal const float MaxTimeToGo = 90.0f;
        // Path tolerance that grows with distance: a guided missile still turns to its lead point (about 6 deg)
        internal const float PathToleranceSlope = 0.1f;
        private const float RefreshSeconds = 0.5f;
        private const float MinSpeed = 1.0f;

        private sealed class Entry
        {
            public Unit Target;
            public float Time = -1.0f;
        }

        private static readonly Func<Missile, Unit> f_missileTarget = FastReflection.CreateFieldGetter<Missile, Unit>("target");
        private static readonly Dictionary<int, Entry> s_entries = new Dictionary<int, Entry>();

        /// <summary>
        /// The unit air defense believes the missile is attacking, or null.
        /// </summary>
        public static Unit PerceivedTarget(Missile missile)
        {
            if ((object)missile == null) return null;
            if (!CnoEvilConfig.IsModActive) return VanillaTarget(missile);

            try
            {
                int id = missile.GetInstanceID();
                Entry entry;
                if (!s_entries.TryGetValue(id, out entry))
                {
                    entry = new Entry();
                    s_entries[id] = entry;
                }

                float now = Time.time;
                if (entry.Time < 0.0f || now - entry.Time >= RefreshSeconds)
                {
                    entry.Target = Infer(missile);
                    entry.Time = now;
                }
                return (entry.Target != null && !entry.Target.disabled) ? entry.Target : null;
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("MissileThreatInference.PerceivedTarget", ex);
                return VanillaTarget(missile);
            }
        }

        /// <summary>
        /// PersistentID of PerceivedTarget; PersistentID.None (invalid) when there is none.
        /// </summary>
        public static PersistentID PerceivedTargetId(Missile missile)
        {
            if ((object)missile == null) return default(PersistentID);
            if (!CnoEvilConfig.IsModActive) return missile.targetID;
            Unit target = PerceivedTarget(missile);
            return (target != null) ? target.persistentID : default(PersistentID);
        }

        private static Unit VanillaTarget(Missile missile)
        {
            return (f_missileTarget != null && missile != null) ? f_missileTarget(missile) : null;
        }

        private static Unit Infer(Missile missile)
        {
            if (missile == null || missile.disabled || missile.rb == null) return null;
            List<Unit> units = UnitRegistry.allUnits;
            if (units == null) return null;

            Vector3 vel = missile.rb.velocity;
            float speedSqr = vel.sqrMagnitude;
            if (speedSqr < MinSpeed * MinSpeed) return null;
            Vector3 velFlat = new Vector3(vel.x, 0.0f, vel.z);
            float speedFlatSqr = velFlat.sqrMagnitude;

            FactionHQ missileHq = missile.NetworkHQ;
            if (missileHq == null && missile.owner != null) missileHq = missile.owner.NetworkHQ;

            Vector3 pos = missile.transform.position;
            float reach = Mathf.Sqrt(speedSqr) * MaxTimeToGo;
            float reachSqr = reach * reach;
            float baseTolerance = BaseTolerance(missile);

            Unit best = null;
            float bestRatio = 1.0f;
            for (int i = 0; i < units.Count; i++)
            {
                Unit u = units[i];
                if (u == null || u.disabled || u is Missile) continue;
                FactionHQ hq = u.NetworkHQ;
                if (hq == null || hq == missileHq) continue;

                Vector3 r = u.transform.position - pos;
                if (r.sqrMagnitude > reachSqr) continue;

                float t;
                float miss;
                if (u is Aircraft)
                {
                    t = Vector3.Dot(r, vel) / speedSqr;
                    if (t <= 0.0f || t > MaxTimeToGo) continue;
                    miss = (r - vel * t).magnitude;
                }
                else
                {
                    if (speedFlatSqr < MinSpeed * MinSpeed)
                    {
                        // Vertical dive: the impact point is below the missile
                        t = 0.0f;
                        miss = new Vector3(r.x, 0.0f, r.z).magnitude;
                    }
                    else
                    {
                        Vector3 rFlat = new Vector3(r.x, 0.0f, r.z);
                        t = Vector3.Dot(rFlat, velFlat) / speedFlatSqr;
                        if (t <= 0.0f || t > MaxTimeToGo) continue;
                        miss = (rFlat - velFlat * t).magnitude;
                    }
                }

                float along = Mathf.Sqrt(speedSqr) * t;
                float allowed = u.maxRadius + baseTolerance + PathToleranceSlope * along;
                float ratio = miss / allowed;
                if (ratio <= bestRatio)
                {
                    bestRatio = ratio;
                    best = u;
                }
            }
            return best;
        }

        /// <summary>
        /// Miss distance (m) inside which a missile counts as flying at a unit, without the unit size and the
        /// distance term: the vanilla target accuracy or the warhead lethal radius, whichever is larger.
        /// </summary>
        public static float BaseTolerance(Missile missile)
        {
            return Mathf.Max(MunitionCatalog.VanillaTargetAccuracy, MunitionCatalog.Get(missile).LethalRadius);
        }

        public static void Forget(Missile missile)
        {
            if ((object)missile == null) return;
            s_entries.Remove(missile.GetInstanceID());
        }

        public static void ClearAll()
        {
            s_entries.Clear();
        }
    }
}
