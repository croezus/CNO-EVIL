using System;
using System.Collections.Generic;
using UnityEngine;
using CnoEvil.Config;

namespace CnoEvil.Core
{
    public static partial class DynamicPlacement
    {
        // Used if the turrets of a complex give no range
        private const float DefaultPointRangeMeters = 3000.0f;
        private const float DefaultNearRangeMeters = 6000.0f;
        private const float DefaultMiddleRangeMeters = 20000.0f;

        // A ring is a part of the weapon range outside the edge of the object, inside these limits
        private const float NearRingFraction = 0.25f;
        private const float MinNearRingMeters = 800.0f;
        private const float MaxNearRingMeters = 3000.0f;
        private const float MiddleRingFraction = 0.3f;
        private const float MinMiddleRingMeters = 2500.0f;
        private const float MaxMiddleRingMeters = 8000.0f;
        private const float RingJitter = 0.3f;

        // A core complex must move at least this far, so that a new start is a new picture
        private const float MinCoreShiftMeters = 300.0f;
        private const float MinPointShiftMeters = 150.0f;
        private const float MinPointRadiusFraction = 0.3f;
        private const float MinApproachMeters = 300.0f;
        private const float MaxApproachMeters = 800.0f;
        private const float MinEscortMeters = 300.0f;
        private const float MaxEscortMeters = 800.0f;
        // A hill near the slot of the ring, not the hilltop of the region: the complex stays in its echelon
        private const float SlotClimbMeters = 600.0f;
        // Each failed attempt opens the sector a little
        private const float AttemptSpreadDegrees = 60.0f;

        private const float NearSpacingMeters = 1200.0f;
        private const float MiddleSpacingMeters = 3000.0f;
        private const float PointSpacingMeters = 400.0f;

        // Second pass of a core complex with no site on its ring: any land in the core, in a small formation
        private const int CompactAttempts = 192;
        private const int CompactScoredCandidates = 6;
        private const float MinCompactScale = 0.25f;
        private const float MaxCompactScale = 0.6f;

        private enum Posture
        {
            // Close to the axis of the threat, far from the object
            Forward,
            // All around the object, close to it
            Deep,
            // On one flank of the axis of the threat
            Sector
        }

        private sealed class FactionPlan
        {
            public Posture Posture;
            public float RingScale;
            public float SpreadDegrees;
            public float OffsetDegrees;
        }

        private static FactionPlan PlanOf(string faction, Context ctx)
        {
            FactionPlan plan;
            if (ctx.Plans.TryGetValue(faction, out plan)) return plan;

            plan = new FactionPlan();
            plan.Posture = (Posture)ctx.Random.Next(3);
            switch (plan.Posture)
            {
                case Posture.Forward:
                    plan.RingScale = 1.2f;
                    plan.SpreadDegrees = 70.0f;
                    break;
                case Posture.Deep:
                    plan.RingScale = 0.85f;
                    plan.SpreadDegrees = 160.0f;
                    break;
                default:
                    plan.RingScale = 1.0f;
                    plan.SpreadDegrees = 50.0f;
                    plan.OffsetDegrees = (ctx.Random.Next(2) == 0 ? -1.0f : 1.0f) * Mathf.Lerp(40.0f, 100.0f, (float)ctx.Random.NextDouble());
                    break;
            }
            ctx.Plans[faction] = plan;
            return plan;
        }

        private static float WeaponRange(Complex complex)
        {
            float range = 0.0f;
            if (f_turretMaxRange != null)
            {
                for (int i = 0; i < complex.Units.Count; i++)
                {
                    GameObjectHolder holder = PrefabOf(complex.Units[i]);
                    if (holder.Turrets == null) continue;
                    for (int j = 0; j < holder.Turrets.Length; j++) range = Mathf.Max(range, f_turretMaxRange(holder.Turrets[j]));
                }
            }
            if (range > 0.0f) return range;
            if (complex.Role == Echelon.Middle) return DefaultMiddleRangeMeters;
            return complex.Role == Echelon.Near ? DefaultNearRangeMeters : DefaultPointRangeMeters;
        }

        private static float RingDistance(Complex complex, FactionPlan plan, Context ctx)
        {
            float range = WeaponRange(complex);
            float ring = complex.Role == Echelon.Middle
                ? Mathf.Clamp(range * MiddleRingFraction, MinMiddleRingMeters, MaxMiddleRingMeters)
                : Mathf.Clamp(range * NearRingFraction, MinNearRingMeters, MaxNearRingMeters);
            float jitter = 1.0f + ((float)ctx.Random.NextDouble() * 2.0f - 1.0f) * RingJitter;
            return complex.Node.Radius + ring * plan.RingScale * jitter;
        }

        private static float Spacing(Echelon role)
        {
            if (role == Echelon.Middle) return MiddleSpacingMeters;
            return role == Echelon.Near ? NearSpacingMeters : PointSpacingMeters;
        }

        /// <summary>
        /// Long range units first: the cover goes where they stand. Each role of a node has its own slots in the sector.
        /// </summary>
        private static void PlaceComplexes(List<Complex> complexes, Context ctx, out int moved, out int movedUnits, out int kept)
        {
            List<Complex> order = new List<Complex>(complexes);
            order.Sort(delegate(Complex a, Complex b)
            {
                int role = ((int)b.Role).CompareTo((int)a.Role);
                return role != 0 ? role : b.Units.Count.CompareTo(a.Units.Count);
            });

            Dictionary<string, int> totals = new Dictionary<string, int>();
            for (int i = 0; i < order.Count; i++)
            {
                if (order[i].Outpost) continue;
                string key = SlotKey(order[i]);
                int count;
                totals.TryGetValue(key, out count);
                totals[key] = count + 1;
            }

            Dictionary<string, int> used = new Dictionary<string, int>();
            moved = 0;
            movedUnits = 0;
            kept = 0;
            for (int i = 0; i < order.Count; i++)
            {
                Complex complex = order[i];
                float slot = 0.0f;
                if (!complex.Outpost)
                {
                    string key = SlotKey(complex);
                    int index;
                    used.TryGetValue(key, out index);
                    used[key] = index + 1;
                    slot = (index + 0.5f) / totals[key] * 2.0f - 1.0f;
                }

                if (PlaceComplex(complex, slot, complexes, ctx))
                {
                    moved++;
                    movedUnits += complex.Units.Count;
                }
                else
                {
                    kept++;
                }
            }
        }

        private static string SlotKey(Complex complex)
        {
            return complex.Node.Name + "/" + (int)complex.Role;
        }

        private static Complex FindUnescorted(Complex complex, List<Complex> complexes)
        {
            if (complex.Role != Echelon.Near || complex.Outpost) return null;
            for (int i = 0; i < complexes.Count; i++)
            {
                Complex other = complexes[i];
                if (other.Placed && !other.HasEscort && other.Role == Echelon.Middle && ReferenceEquals(other.Node, complex.Node)) return other;
            }
            return null;
        }

        private static Vector3 RandomAround(Vector3 center, float minimum, float maximum, Context ctx)
        {
            float angle = (float)(ctx.Random.NextDouble() * Math.PI * 2.0);
            float distance = Mathf.Lerp(minimum, maximum, (float)ctx.Random.NextDouble());
            return center + new Vector3(Mathf.Cos(angle), 0.0f, Mathf.Sin(angle)) * distance;
        }

        /// <summary>
        /// A place in the sector of the object. The slot gives the part of the sector, from -1 to 1.
        /// </summary>
        private static Vector3 RingSite(Complex complex, FactionPlan plan, float slot, int attempt, int attempts, Context ctx)
        {
            DefenseNode node = complex.Node;
            if (complex.Role == Echelon.Point && node.IsAirbase)
            {
                if (node.Runways.Count > 0 && (attempt & 1) == 1) return ApproachSite(node, ctx);
                float inside = Mathf.Lerp(MinPointRadiusFraction, 1.0f, (float)ctx.Random.NextDouble()) * node.Radius;
                float around = (float)(ctx.Random.NextDouble() * Math.PI * 2.0);
                return node.Center + new Vector3(Mathf.Cos(around), 0.0f, Mathf.Sin(around)) * inside;
            }

            float widen = AttemptSpreadDegrees * attempt / attempts;
            float jitter = ((float)ctx.Random.NextDouble() * 2.0f - 1.0f) * (10.0f + widen);
            float degrees = Mathf.Atan2(node.Threat.z, node.Threat.x) * Mathf.Rad2Deg + plan.OffsetDegrees + slot * plan.SpreadDegrees + jitter;
            float radians = degrees * Mathf.Deg2Rad;
            return node.Center + new Vector3(Mathf.Cos(radians), 0.0f, Mathf.Sin(radians)) * RingDistance(complex, plan, ctx);
        }

        /// <summary>
        /// Beyond one end of a runway, to the side of the center line: the way that attackers of the runway come.
        /// </summary>
        private static Vector3 ApproachSite(DefenseNode node, Context ctx)
        {
            RunwayZone strip = node.Runways[ctx.Random.Next(node.Runways.Count)];
            bool reverse = ctx.Random.Next(2) == 0;
            Vector3 end = reverse ? strip.Start : strip.End;
            Vector3 along = end - (reverse ? strip.End : strip.Start);
            along.y = 0.0f;
            along.Normalize();
            Vector3 side = new Vector3(-along.z, 0.0f, along.x) * (ctx.Random.Next(2) == 0 ? -1.0f : 1.0f);
            float forward = Mathf.Lerp(MinApproachMeters, MaxApproachMeters, (float)ctx.Random.NextDouble());
            float lateral = RunwayClearanceMeters + Mathf.Lerp(50.0f, 400.0f, (float)ctx.Random.NextDouble());
            return end + along * forward + side * lateral;
        }

        private static Vector3 OutpostSite(Vector3 origin, float minShift, float radius, Context ctx)
        {
            float angle = (float)(ctx.Random.NextDouble() * Math.PI * 2.0);
            float t = (float)Math.Sqrt(ctx.Random.NextDouble());
            return origin + new Vector3(Mathf.Cos(angle), 0.0f, Mathf.Sin(angle)) * Mathf.Lerp(minShift, radius, t);
        }

        /// <summary>
        /// A place in the core of the object, in any direction. An island has land here when the ring is open sea.
        /// </summary>
        private static Vector3 CompactSite(Complex complex, float coreReach, Context ctx)
        {
            DefenseNode node = complex.Node;
            float inner = complex.Role == Echelon.Point ? node.Radius * MinPointRadiusFraction : node.Radius;
            return OutpostSite(node.Center, inner, coreReach, ctx);
        }

        private static float Separation(Complex complex, Vector3 site, List<Complex> complexes)
        {
            float spacing = Spacing(complex.Role);
            float best = spacing;
            for (int i = 0; i < complexes.Count; i++)
            {
                Complex other = complexes[i];
                if (!other.Placed || other.Role != complex.Role || !ReferenceEquals(other.Node, complex.Node)) continue;
                best = Mathf.Min(best, FlatDistance(other.Site, site));
            }
            return best / spacing;
        }

        private static bool PlaceComplex(Complex complex, float slot, List<Complex> complexes, Context ctx)
        {
            List<Member> battery = complex.Units;
            PrepareFormation(battery);
            Member anchor = battery[0];
            Vector3 origin = anchor.Local;
            string faction = anchor.Saved.faction;
            FactionPlan plan = PlanOf(faction, ctx);

            bool radarSite = false;
            bool mobile = false;
            for (int i = 0; i < battery.Count; i++)
            {
                if (IsRadarClass(battery[i].Class)) radarSite = true;
                if (battery[i].Class != SiteClass.RadarBuilding) mobile = true;
            }

            FactionHQ ownHq = HqOf(faction, ctx);
            Vector3 enemy;
            bool hasEnemy = TryNearestEnemy(origin, ownHq, ctx, out enemy);
            float authorEnemyDistance = hasEnemy ? FlatDistance(origin, enemy) : 0.0f;
            float standoff = Mathf.Min(authorEnemyDistance, Mathf.Max(EnemyStandoffMeters, authorEnemyDistance * EnemyStandoffFraction));

            float radius = complex.Role == Echelon.Station ? CnoEvilConfig.DynamicPlacementStationRadius_m.Value : CnoEvilConfig.DynamicPlacementOutpostRadius_m.Value;
            float minShift = complex.Outpost
                ? Mathf.Min(radius * MinShiftFraction, MaxMinShiftMeters)
                : (complex.Role == Echelon.Point ? MinPointShiftMeters : MinCoreShiftMeters);
            float coreReach = complex.Node != null ? complex.Node.Radius + CnoEvilConfig.DynamicPlacementCoreRadius_m.Value : 0.0f;

            Vector3 asset;
            bool hasAsset = TryNearestAsset(origin, faction, radius * 2.0f, ctx, out asset);
            Complex escorted = FindUnescorted(complex, complexes);

            int attempts = radarSite ? RadarCandidateAttempts : CandidateAttempts;
            int scored = radarSite ? RadarScoredCandidates : ScoredCandidates;
            List<Candidate> candidates = new List<Candidate>(scored);
            float minGap = CnoEvilConfig.DynamicPlacementMinGap_m.Value;
            float maxGap = CnoEvilConfig.DynamicPlacementMaxGap_m.Value;
            int viewRejects = 0;
            int linkRejects = 0;

            int groundBefore = ctx.GroundRejects;
            int blockBefore = ctx.BlockRejects;
            int treeBefore = ctx.TreeRejects;
            int roadBefore = ctx.RoadRejects;
            bool compact = false;

            ctx.AllowBaseInterior = !complex.Outpost && complex.Role == Echelon.Point && complex.Node.IsAirbase;
            for (int pass = 0; pass < 2 && candidates.Count == 0; pass++)
            {
                compact = pass == 1;
                if (compact && complex.Outpost) break;
                int limit = compact ? CompactAttempts : attempts;
                int wanted = compact ? CompactScoredCandidates : scored;

                for (int attempt = 0; attempt < limit && candidates.Count < wanted; attempt++)
                {
                    Candidate candidate = new Candidate();
                    candidate.Yaw = (float)((ctx.Random.NextDouble() * 2.0 - 1.0) * MaxBatteryYawDegrees);
                    float size = (float)ctx.Random.NextDouble();
                    candidate.Scale = compact ? Mathf.Lerp(MinCompactScale, MaxCompactScale, size) : Mathf.Lerp(MinFormationScale, MaxFormationScale, size);
                    candidate.Gap = compact ? minGap : Mathf.Lerp(minGap, maxGap, (float)ctx.Random.NextDouble());

                    if (compact)
                    {
                        candidate.Center = CompactSite(complex, coreReach, ctx);
                        if (radarSite && (attempt & 1) == 0) candidate.Center = ClimbHill(candidate.Center, candidate.Center, SlotClimbMeters);
                    }
                    else if (complex.Outpost)
                    {
                        candidate.Center = OutpostSite(origin, minShift, radius, ctx);
                        bool climb = radarSite ? (attempt & 3) != 3 : (attempt & 1) == 0;
                        if (climb) candidate.Center = ClimbHill(candidate.Center, origin, radius);
                        // An outpost stays an outpost
                        if (complex.Node != null && FlatSqr(candidate.Center, complex.Node.Center) < coreReach * coreReach) continue;
                    }
                    else if (escorted != null && (attempt & 3) != 3)
                    {
                        candidate.Center = RandomAround(escorted.Site, MinEscortMeters, MaxEscortMeters, ctx);
                    }
                    else
                    {
                        candidate.Center = RingSite(complex, plan, slot, attempt, attempts, ctx);
                        if (radarSite && (attempt & 3) != 3) candidate.Center = ClimbHill(candidate.Center, candidate.Center, SlotClimbMeters);
                    }

                    if (FlatSqr(candidate.Center, origin) < minShift * minShift) continue;
                    if (hasEnemy)
                    {
                        Vector3 nearest;
                        if (TryNearestEnemy(candidate.Center, ownHq, ctx, out nearest) && FlatDistance(candidate.Center, nearest) < standoff) continue;
                    }
                    if (!TryFitBattery(battery, complex.Index, ref candidate, ctx)) continue;
                    if (!LinksHold(battery, candidate.Ground))
                    {
                        linkRejects++;
                        continue;
                    }
                    if (!HasClearView(battery, ref candidate, ctx))
                    {
                        viewRejects++;
                        continue;
                    }

                    candidate.Score = ScoreSite(ref candidate, origin, radius, anchor, radarSite, mobile, hasEnemy, enemy, hasAsset, asset, ctx);
                    if (!complex.Outpost) candidate.Score += 0.6f * Separation(complex, candidate.Center, complexes);
                    if (radarSite) AddDistinctSite(candidates, ref candidate);
                    else candidates.Add(candidate);
                }
            }
            ctx.AllowBaseInterior = false;
            ctx.ViewRejects += viewRejects;
            ctx.LinkRejects += linkRejects;

            string place = complex.Outpost ? "outpost" : complex.Role + " of " + complex.Node.Name;
            if (candidates.Count == 0)
            {
                if (EvilLog.On(LogCat.Placement))
                {
                    EvilLog.Write(LogCat.Placement, "Dynamic placement: {0} ({1} units, {2}) keeps its position, {3}, points rejected: ground {4}, base or unit {5}, trees {6}, road {7}", anchor.Saved.UniqueName, battery.Count, place,
                        linkRejects > 0 ? "links to radar or fire control break" : (viewRejects > 0 ? "no site with a clear view" : "no valid site"),
                        ctx.GroundRejects - groundBefore, ctx.BlockRejects - blockBefore, ctx.TreeRejects - treeBefore, ctx.RoadRejects - roadBefore);
                }
                return false;
            }

            Candidate chosen = PickCandidate(candidates, ctx);
            ApplyPlan(battery, ref chosen, ctx);
            complex.Placed = true;
            complex.Site = chosen.Center;
            ctx.Moved.Add(complex.Index);
            if (escorted != null && FlatDistance(chosen.Center, escorted.Site) <= MaxEscortMeters * 1.5f) escorted.HasEscort = true;

            if (EvilLog.On(LogCat.Placement))
            {
                EvilLog.Write(LogCat.Placement, "Dynamic placement: {0} ({1} units, {2}) moved {3:F0} m, score {4:F2}, {5} sites, formation x{6:F2}, gap {7:F0} m, worst view {8:F2}, lowest sensor {9:F0} m{10}",
                    anchor.Saved.UniqueName, battery.Count, place, FlatDistance(origin, chosen.Center), chosen.Score, candidates.Count,
                    chosen.Scale, chosen.Gap, chosen.WorstView, chosen.LowestHeight, compact ? ", compact pass" : string.Empty);
            }
            return true;
        }
    }
}
