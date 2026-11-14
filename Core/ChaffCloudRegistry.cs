using System;
using System.Collections.Generic;
using UnityEngine;
using CnoEvil.Config;

namespace CnoEvil.Core
{
    /// <summary>
    /// One chaff cloud or active decoy in flight: the vanilla RadarChaff object (position, velocity) and its launch time.
    /// </summary>
    public sealed class ChaffCloud
    {
        public RadarChaff Chaff;
        public Unit Owner;
        public float LaunchTime;
        public int Id;
        // Active decoy (DecoyDispenserSetup): a DRFM repeater instead of a dipole cloud
        public bool IsDecoy;

        /// <summary>
        /// Chaff: younger than ChaffLifetime_s. Decoy: its battery is not empty (DecoyArmDelay_s + DecoyActiveTime_s).
        /// </summary>
        public bool Alive
        {
            get
            {
                if (Chaff == null) return false;
                float age = Time.timeSinceLevelLoad - LaunchTime;
                if (IsDecoy) return age < CnoEvilConfig.DecoyArmDelay_s.Value + CnoEvilConfig.DecoyActiveTime_s.Value;
                return age < CnoEvilConfig.ChaffLifetime_s.Value;
            }
        }

        /// <summary>
        /// Seconds of battery left while a decoy transmits; 0 before arming, after the battery, and for chaff.
        /// </summary>
        public float TransmitTimeLeft
        {
            get
            {
                if (!IsDecoy || Chaff == null) return 0.0f;
                float age = Time.timeSinceLevelLoad - LaunchTime;
                float arm = CnoEvilConfig.DecoyArmDelay_s.Value;
                if (age < arm) return 0.0f;
                return Mathf.Max(0.0f, arm + CnoEvilConfig.DecoyActiveTime_s.Value - age);
            }
        }

        /// <summary>
        /// Radar cross section that a transmitter at this range sees (m2, game scale). Chaff: Rcs. Decoy: a repeater with a
        /// fixed output power has the equivalent RCS 4 pi R^2 ERP / (Pt Gt), so it increases with the square of the
        /// transmitter range up to the gain limit DecoyMaxRcs_m2; 0 while it does not transmit.
        /// </summary>
        public float EquivalentRcs(float transmitterRange)
        {
            if (!IsDecoy) return Rcs;
            if (TransmitTimeLeft <= 0.0f) return 0.0f;
            float reference = Mathf.Max(1.0f, CnoEvilConfig.DecoyReferenceRange_m.Value);
            float ratio = transmitterRange / reference;
            float rcs = Mathf.Max(0.0f, CnoEvilConfig.DecoyRcsAtReference_m2.Value) * ratio * ratio;
            return Mathf.Min(rcs, Mathf.Max(0.0f, CnoEvilConfig.DecoyMaxRcs_m2.Value));
        }

        public Vector3 Position
        {
            get { return Chaff.transform.position; }
        }

        public Vector3 Velocity
        {
            get { return ChaffCloudRegistry.ReadVelocity(Chaff); }
        }

        /// <summary>
        /// Radar cross section of the cloud (m2, game scale) at its age: bloom 1 - exp(-3 t / bloom time), full until 40 % of
        /// the lifetime, then a linear fade to 0 as the dipoles spread out of one seeker cell.
        /// </summary>
        public float Rcs
        {
            get
            {
                float age = Time.timeSinceLevelLoad - LaunchTime;
                float bloomTime = Mathf.Max(0.05f, CnoEvilConfig.ChaffBloomTime_s.Value);
                float life = Mathf.Max(1.0f, CnoEvilConfig.ChaffLifetime_s.Value);
                float bloom = 1.0f - Mathf.Exp(-3.0f * age / bloomTime);
                float fade = Mathf.Clamp01((life - age) / (0.6f * life));
                return Mathf.Max(0.0f, CnoEvilConfig.ChaffCartridgeRcs_m2.Value) * bloom * fade;
            }
        }

        /// <summary>
        /// Deceleration of the cloud (m/s2, vector) from the vanilla quadratic drag of RadarChaff: drag x |v| x v.
        /// </summary>
        public Vector3 Acceleration
        {
            get
            {
                Vector3 v = Velocity;
                return -CnoEvilConfig.ChaffDrag.Value * v.magnitude * v;
            }
        }
    }

    /// <summary>
    /// All chaff clouds in flight (RadarChaff.LaunchChaff adds them). Seekers read them in ChaffSeduction.
    /// </summary>
    public static class ChaffCloudRegistry
    {
        private static readonly List<ChaffCloud> s_clouds = new List<ChaffCloud>();
        private static readonly Stack<ChaffCloud> s_pool = new Stack<ChaffCloud>();
        private static int s_nextId = 1;

        private static readonly Func<RadarChaff, Vector3> f_velocity = FastReflection.CreateFieldGetter<RadarChaff, Vector3>("velocity");

        public static List<ChaffCloud> Clouds
        {
            get { return s_clouds; }
        }

        public static bool Any
        {
            get { return s_clouds.Count > 0; }
        }

        /// <summary>
        /// True when at least one active decoy is in flight (it may not transmit yet).
        /// </summary>
        public static bool AnyDecoy
        {
            get
            {
                for (int i = 0; i < s_clouds.Count; i++)
                {
                    if (s_clouds[i].IsDecoy) return true;
                }
                return false;
            }
        }

        public static Vector3 ReadVelocity(RadarChaff chaff)
        {
            return (f_velocity != null && chaff != null) ? f_velocity(chaff) : Vector3.zero;
        }

        public static void Add(RadarChaff chaff, Unit owner, bool isDecoy)
        {
            if (chaff == null) return;
            ChaffCloud cloud = s_pool.Count > 0 ? s_pool.Pop() : new ChaffCloud();
            cloud.Chaff = chaff;
            cloud.Owner = owner;
            cloud.LaunchTime = Time.timeSinceLevelLoad;
            cloud.Id = s_nextId++;
            cloud.IsDecoy = isDecoy;
            s_clouds.Add(cloud);
        }

        /// <summary>
        /// Longest battery time left of the decoys of this unit that transmit now (for the cockpit indicator); 0 = none.
        /// </summary>
        public static float DecoyTransmitTimeLeft(Unit owner)
        {
            float best = 0.0f;
            if (owner == null) return best;
            for (int i = 0; i < s_clouds.Count; i++)
            {
                ChaffCloud cloud = s_clouds[i];
                if (!cloud.IsDecoy || cloud.Owner != owner) continue;
                best = Mathf.Max(best, cloud.TransmitTimeLeft);
            }
            return best;
        }

        /// <summary>
        /// Removes the clouds that are destroyed or older than ChaffLifetime_s.
        /// </summary>
        public static void Prune()
        {
            for (int i = s_clouds.Count - 1; i >= 0; i--)
            {
                ChaffCloud cloud = s_clouds[i];
                if (cloud.Alive) continue;
                s_clouds.RemoveAt(i);
                cloud.Chaff = null;
                cloud.Owner = null;
                s_pool.Push(cloud);
            }
        }

        public static void ClearAll()
        {
            for (int i = 0; i < s_clouds.Count; i++)
            {
                s_clouds[i].Chaff = null;
                s_clouds[i].Owner = null;
                s_pool.Push(s_clouds[i]);
            }
            s_clouds.Clear();
        }
    }
}
