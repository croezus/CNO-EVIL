using System.Collections.Generic;
using UnityEngine;
using CnoEvil.Config;

namespace CnoEvil.Core
{
    public static partial class DynamicPlacement
    {
        // Vanilla FireControl.SearchForRadar and Turret.SearchForRadar look for a radar or fire control this close
        private const float VanillaLinkMeters = 300.0f;
        // The limit of the plan for one link: the vanilla search must find its unit at each new site
        private const float LinkLimitMeters = 250.0f;
        // Rearmer.Range of the munitions trucks is 300 m. A truck farther from air defense is a service of the base
        private const float SupportLinkMeters = 250.0f;
        // Cover units of a radar station
        private const float StationCoverMeters = 400.0f;
        // Two units of the same class this close work as a pair
        private const float PairMeters = 100.0f;

        private enum Echelon
        {
            Point,
            Near,
            Middle,
            Station
        }

        /// <summary>
        /// Units that fight together. The vanilla links and the template of the unit type make a complex, not the distance
        /// alone: launcher, fire control and radar; station and cover; a pair of one class; the trucks of these units.
        /// </summary>
        private sealed class Complex
        {
            public int Index;
            public List<Member> Units;
            public Echelon Role;
            public DefenseNode Node;
            public bool Outpost;
            public bool Placed;
            public bool HasEscort;
            public Vector3 Site;
        }

        private static int Find(int[] parent, int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }
            return i;
        }

        private static void Join(int[] parent, int[] size, int a, int b)
        {
            int rootA = Find(parent, a);
            int rootB = Find(parent, b);
            if (rootA == rootB) return;
            parent[rootB] = rootA;
            size[rootA] += size[rootB];
        }

        private static bool IsRadarUnit(Member member)
        {
            return IsRadarClass(member.Class);
        }

        /// <summary>
        /// The nearest unit of the faction that the test accepts, closer than the limit. -1 if there is none.
        /// </summary>
        private static int NearestUnit(List<Member> units, int from, float limit, System.Predicate<Member> test)
        {
            int nearest = -1;
            float best = limit * limit;
            for (int i = 0; i < units.Count; i++)
            {
                if (i == from || units[i].Saved.faction != units[from].Saved.faction || !test(units[i])) continue;
                float sqr = FlatSqr(units[i].Local, units[from].Local);
                if (sqr >= best) continue;
                best = sqr;
                nearest = i;
            }
            return nearest;
        }

        private static List<Complex> BuildComplexes(List<Member> members, List<Member> support)
        {
            List<Member> units = new List<Member>(members);
            for (int i = 0; i < support.Count; i++)
            {
                if (support[i].HasFireControl) units.Add(support[i]);
            }

            int[] parent = new int[units.Count];
            int[] size = new int[units.Count];
            for (int i = 0; i < units.Count; i++)
            {
                parent[i] = i;
                size[i] = 1;
            }

            System.Predicate<Member> isProvider = delegate(Member m) { return m.HasFireControl || IsRadarUnit(m); };
            System.Predicate<Member> isRadar = IsRadarUnit;
            for (int i = 0; i < units.Count; i++)
            {
                Member member = units[i];
                int link = -1;
                if (member.NeedsLink) link = NearestUnit(units, i, VanillaLinkMeters, isProvider);
                else if (member.HasFireControl) link = NearestUnit(units, i, VanillaLinkMeters, isRadar);
                if (link >= 0) Join(parent, size, i, link);
            }

            for (int i = 0; i < units.Count; i++)
            {
                if (units[i].Class != SiteClass.RadarBuilding) continue;
                for (int j = 0; j < units.Count; j++)
                {
                    Member cover = units[j];
                    if (cover.Class != SiteClass.ShortRange && cover.Class != SiteClass.Gun) continue;
                    if (cover.Saved.faction != units[i].Saved.faction) continue;
                    if (FlatSqr(cover.Local, units[i].Local) < StationCoverMeters * StationCoverMeters) Join(parent, size, i, j);
                }
            }

            float pairSqr = PairMeters * PairMeters;
            for (int i = 0; i < units.Count; i++)
            {
                for (int j = i + 1; j < units.Count; j++)
                {
                    if (units[i].Class != units[j].Class || units[i].Saved.faction != units[j].Saved.faction) continue;
                    if (units[i].Class != SiteClass.ShortRange && units[i].Class != SiteClass.Gun) continue;
                    if (size[Find(parent, i)] != 1 || size[Find(parent, j)] != 1) continue;
                    if (FlatSqr(units[i].Local, units[j].Local) < pairSqr) Join(parent, size, i, j);
                }
            }

            Dictionary<int, List<Member>> groups = new Dictionary<int, List<Member>>();
            for (int i = 0; i < units.Count; i++)
            {
                int root = Find(parent, i);
                List<Member> group;
                if (!groups.TryGetValue(root, out group))
                {
                    group = new List<Member>();
                    groups[root] = group;
                }
                group.Add(units[i]);
            }

            AddTrucks(units, support, parent, groups);

            List<Complex> complexes = new List<Complex>();
            foreach (List<Member> group in groups.Values)
            {
                // Fire control with no air defense unit around it does not move
                if (!Has(group, SiteClass.None)) continue;
                Complex complex = new Complex();
                complex.Index = complexes.Count;
                complex.Units = group;
                complex.Role = RoleOf(group);
                for (int i = 0; i < group.Count; i++) group[i].Battery = complex.Index;
                complexes.Add(complex);
            }
            return complexes;
        }

        /// <summary>
        /// A truck goes with the air defense unit that the author parked it next to. A truck with no such unit is a service of
        /// the base or of a landing site, also for aircraft, and stays.
        /// </summary>
        private static void AddTrucks(List<Member> units, List<Member> support, int[] parent, Dictionary<int, List<Member>> groups)
        {
            float limitSqr = SupportLinkMeters * SupportLinkMeters;
            for (int i = 0; i < support.Count; i++)
            {
                Member truck = support[i];
                if (truck.HasFireControl) continue;

                int nearest = -1;
                float best = limitSqr;
                for (int j = 0; j < units.Count; j++)
                {
                    if (units[j].Class == SiteClass.Support || units[j].Saved.faction != truck.Saved.faction) continue;
                    float sqr = FlatSqr(units[j].Local, truck.Local);
                    if (sqr >= best) continue;
                    best = sqr;
                    nearest = j;
                }
                if (nearest >= 0) groups[Find(parent, nearest)].Add(truck);
            }
        }

        private static Echelon RoleOf(List<Member> group)
        {
            if (Has(group, SiteClass.RadarBuilding)) return Echelon.Station;
            if (Has(group, SiteClass.Launcher) || Has(group, SiteClass.RadarSam) || Has(group, SiteClass.RadarVehicle)) return Echelon.Middle;
            return Has(group, SiteClass.ShortRange) ? Echelon.Near : Echelon.Point;
        }

        /// <summary>
        /// A complex in the reach of an object of its faction is part of the core. All others are outposts and stay outposts:
        /// a lone radar SAM forward of the base, a radar station with its cover.
        /// </summary>
        private static void AssignNodes(List<Complex> complexes, List<DefenseNode> nodes)
        {
            float core = CnoEvilConfig.DynamicPlacementCoreRadius_m.Value;
            for (int i = 0; i < complexes.Count; i++)
            {
                Complex complex = complexes[i];
                Member anchor = FindAnchor(complex.Units);
                float best = float.MaxValue;
                for (int j = 0; j < nodes.Count; j++)
                {
                    if (nodes[j].Faction != anchor.Saved.faction) continue;
                    float edge = FlatDistance(nodes[j].Center, anchor.Local) - nodes[j].Radius;
                    if (edge >= best) continue;
                    best = edge;
                    complex.Node = nodes[j];
                }
                complex.Outpost = complex.Node == null || best > core || complex.Role == Echelon.Station;
            }
        }

        /// <summary>
        /// The vanilla searches of a unit must find their radar and fire control at the new site.
        /// </summary>
        private static bool LinksHold(List<Member> battery, Vector3[] ground)
        {
            float limitSqr = LinkLimitMeters * LinkLimitMeters;
            bool anyProvider = false;
            bool anyRadar = false;
            for (int i = 0; i < battery.Count; i++)
            {
                if (IsRadarUnit(battery[i])) anyRadar = true;
                if (battery[i].HasFireControl || IsRadarUnit(battery[i])) anyProvider = true;
            }

            for (int i = 0; i < battery.Count; i++)
            {
                Member member = battery[i];
                bool wantsProvider = member.NeedsLink && anyProvider;
                bool wantsRadar = member.HasFireControl && anyRadar;
                if (!wantsProvider && !wantsRadar) continue;

                bool found = false;
                for (int j = 0; j < battery.Count && !found; j++)
                {
                    if (j == i) continue;
                    Member other = battery[j];
                    bool fits = wantsRadar ? IsRadarUnit(other) : (other.HasFireControl || IsRadarUnit(other));
                    found = fits && FlatSqr(ground[i], ground[j]) < limitSqr;
                }
                if (!found) return false;
            }
            return true;
        }
    }
}
