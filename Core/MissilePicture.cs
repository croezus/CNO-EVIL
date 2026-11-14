using System.Collections.Generic;
using UnityEngine;
using CnoEvil.Config;

namespace CnoEvil.Core
{
    /// <summary>
    /// What each faction knows about hostile missiles in flight: position and velocity at the last sighting by a radar
    /// of the faction (vanilla radar detection or the full radar physics of DatalinkNetwork), dead-reckoned for
    /// MissilePictureCoast_s after it. Built by the datalink cycle on the host; read by the missile warning
    /// (MissileWarningModel) and the datalink relays. Never written into the vanilla trackingDatabase (ADR-12).
    /// </summary>
    public static class MissilePicture
    {
        public sealed class Sighting
        {
            public Missile Missile;
            public Unit Spotter;
            public Vector3 Position;
            public Vector3 Velocity;
            public float Time;
        }

        private static readonly Dictionary<FactionHQ, Dictionary<Missile, Sighting>> s_pictures =
            new Dictionary<FactionHQ, Dictionary<Missile, Sighting>>();
        private static readonly List<Sighting> s_pool = new List<Sighting>();
        private static readonly List<Missile> s_stale = new List<Missile>();

        /// <summary>
        /// A sensor of the faction sees the missile now.
        /// </summary>
        public static void Record(FactionHQ hq, Missile missile, Unit spotter, float now)
        {
            if (hq == null || missile == null || missile.disabled) return;
            Dictionary<Missile, Sighting> picture;
            if (!s_pictures.TryGetValue(hq, out picture))
            {
                picture = new Dictionary<Missile, Sighting>();
                s_pictures[hq] = picture;
            }

            Sighting s;
            if (!picture.TryGetValue(missile, out s))
            {
                int last = s_pool.Count - 1;
                if (last >= 0)
                {
                    s = s_pool[last];
                    s_pool.RemoveAt(last);
                }
                else
                {
                    s = new Sighting();
                }
                s.Missile = missile;
                picture[missile] = s;
            }
            s.Spotter = spotter;
            s.Position = missile.transform.position;
            s.Velocity = missile.rb != null ? missile.rb.velocity : Vector3.zero;
            s.Time = now;
        }

        /// <summary>
        /// The faction's position and velocity of the missile: the last sighting, dead-reckoned to now while
        /// coasting. False when the faction does not know the missile.
        /// </summary>
        public static bool TryGet(FactionHQ hq, Missile missile, float now, out Vector3 position, out Vector3 velocity)
        {
            position = Vector3.zero;
            velocity = Vector3.zero;
            Dictionary<Missile, Sighting> picture;
            Sighting s;
            if (hq == null || (object)missile == null || !s_pictures.TryGetValue(hq, out picture) || !picture.TryGetValue(missile, out s)) return false;
            float age = now - s.Time;
            if (age > CnoEvilConfig.MissilePictureCoast_s.Value) return false;
            velocity = s.Velocity;
            position = s.Position + s.Velocity * Mathf.Max(0.0f, age);
            return true;
        }

        /// <summary>
        /// All missiles the faction knows (includes coasting sightings); null when it knows none.
        /// </summary>
        public static Dictionary<Missile, Sighting> GetPicture(FactionHQ hq)
        {
            Dictionary<Missile, Sighting> picture;
            return (hq != null && s_pictures.TryGetValue(hq, out picture)) ? picture : null;
        }

        /// <summary>
        /// Drops sightings older than the coast time and missiles that are gone.
        /// </summary>
        public static void Prune(float now)
        {
            float coast = CnoEvilConfig.MissilePictureCoast_s.Value;
            foreach (KeyValuePair<FactionHQ, Dictionary<Missile, Sighting>> kv in s_pictures)
            {
                s_stale.Clear();
                foreach (KeyValuePair<Missile, Sighting> sv in kv.Value)
                {
                    if (sv.Key == null || sv.Key.disabled || now - sv.Value.Time > coast) s_stale.Add(sv.Key);
                }
                for (int i = 0; i < s_stale.Count; i++)
                {
                    s_pool.Add(kv.Value[s_stale[i]]);
                    kv.Value.Remove(s_stale[i]);
                }
            }
            s_stale.Clear();
        }

        public static void Forget(Missile missile)
        {
            if ((object)missile == null) return;
            foreach (KeyValuePair<FactionHQ, Dictionary<Missile, Sighting>> kv in s_pictures)
            {
                Sighting s;
                if (kv.Value.TryGetValue(missile, out s))
                {
                    s_pool.Add(s);
                    kv.Value.Remove(missile);
                }
            }
        }

        public static void ClearAll()
        {
            s_pictures.Clear();
            s_pool.Clear();
            s_stale.Clear();
        }
    }
}
