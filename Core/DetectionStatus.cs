using System.Collections.Generic;
using UnityEngine;
using CnoEvil.Config;

namespace CnoEvil.Core
{
    /// <summary>
    /// Best knowledge that a hostile faction has of a unit, in the tiers of the HMD target info.
    /// Order is significant: a higher value is better knowledge.
    /// </summary>
    public enum DetectionTier : byte
    {
        None = 0,
        Memory = 1,
        Low = 2,
        Mid = 3,
        High = 4,
        Optical = 5
    }

    /// <summary>
    /// Detection status of a human player's aircraft for the HUD line "ID NO / MEM / LOW / MED / HIGH / OPT".
    /// Read-only on the faction tracks (no GetTrack side effects). Only the machine that runs the hostile
    /// sensors (host / single player) has the data; clients get the tier from DetectionStatusNet.
    /// </summary>
    public static class DetectionStatus
    {
        private static readonly string[] s_labels = { "ID NO", "ID MEM", "ID LOW", "ID MED", "ID HIGH", "ID OPT" };

        public static float Interval
        {
            get
            {
                float v = CnoEvilConfig.DetectionStatusInterval_s != null ? CnoEvilConfig.DetectionStatusInterval_s.Value : 1.0f;
                return Mathf.Max(0.25f, v);
            }
        }

        public static bool IsEnabled
        {
            get
            {
                return CnoEvilConfig.IsModActive &&
                       CnoEvilConfig.EnableHUDDetectionStatus != null && CnoEvilConfig.EnableHUDDetectionStatus.Value;
            }
        }

        public static string GetLabel(DetectionTier tier)
        {
            int i = (int)tier;
            return (i >= 0 && i < s_labels.Length) ? s_labels[i] : s_labels[0];
        }

        /// <summary>
        /// Highest tier over all factions other than the unit's own.
        /// </summary>
        public static DetectionTier Evaluate(Unit own)
        {
            if (own == null || own.disabled) return DetectionTier.None;
            FactionHQ ownHq = own.NetworkHQ;
            DetectionTier best = DetectionTier.None;

            Dictionary<FactionHQ, Dictionary<Unit, TriangulationTrack>> all = RWRTriangulationProcessor.GetAllFactionTracks();
            foreach (KeyValuePair<FactionHQ, Dictionary<Unit, TriangulationTrack>> kv in all)
            {
                if (kv.Key == null || kv.Key == ownHq || kv.Value == null) continue;
                TriangulationTrack track;
                if (!kv.Value.TryGetValue(own, out track) || track == null) continue;

                DetectionTier tier = GetTier(track);
                if (tier > best) best = tier;
                if (best == DetectionTier.Optical) break;
            }
            return best;
        }

        private static DetectionTier GetTier(TriangulationTrack track)
        {
            if (track.HasVisualContact) return DetectionTier.Optical;
            if (track.HadActiveContact && track.IsActivelyDetected)
            {
                IdentTier ident = TargetIdentification.GetTier(track);
                if (ident == IdentTier.High) return DetectionTier.High;
                if (ident == IdentTier.Mid) return DetectionTier.Mid;
                return DetectionTier.Low;
            }
            if (track.IsInMemoryState()) return DetectionTier.Memory;
            return DetectionTier.None;
        }
    }
}
