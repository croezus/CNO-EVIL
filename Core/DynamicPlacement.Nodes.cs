using System;
using System.Collections.Generic;
using NuclearOption.SavedMission;
using UnityEngine;
using CnoEvil.Config;

namespace CnoEvil.Core
{
    public static partial class DynamicPlacement
    {
        // Factories closer than this are one object
        private const float AssetLinkMeters = 1500.0f;
        // A factory node reaches this far from its outer building
        private const float AssetEdgeMeters = 200.0f;

        /// <summary>
        /// An object that a faction defends: an active airbase with the factories next to it, or a group of factories.
        /// </summary>
        private sealed class DefenseNode
        {
            public string Name;
            public string Faction;
            public Vector3 Center;
            public float Radius;
            public bool IsAirbase;
            // Flat unit vector to the nearest object of an enemy
            public Vector3 Threat;
            public readonly List<RunwayZone> Runways = new List<RunwayZone>();
        }

        /// <summary>
        /// The mission data give the owner of each airbase. An airbase that is disabled or has no faction needs no defense.
        /// </summary>
        private static List<DefenseNode> BuildNodes(Mission mission, Context ctx)
        {
            List<DefenseNode> nodes = new List<DefenseNode>();
            if (mission.airbases != null)
            {
                for (int i = 0; i < mission.airbases.Count; i++)
                {
                    SavedAirbase saved = mission.airbases[i];
                    if (saved == null || saved.Disabled || string.IsNullOrEmpty(saved.faction)) continue;

                    DefenseNode node = new DefenseNode();
                    node.Name = saved.UniqueName;
                    node.Faction = saved.faction;
                    node.Center = saved.Center.ToLocalPosition();
                    node.Radius = saved.Airbase != null ? saved.Airbase.GetRadius() : saved.CaptureRange;
                    node.IsAirbase = true;
                    for (int j = 0; j < ctx.Runways.Count; j++)
                    {
                        RunwayZone strip = ctx.Runways[j];
                        float reach = node.Radius * 1.5f;
                        if (FlatSqr(strip.Start, node.Center) < reach * reach || FlatSqr(strip.End, node.Center) < reach * reach) node.Runways.Add(strip);
                    }
                    nodes.Add(node);
                }
            }

            AddFactoryNodes(mission, nodes);
            for (int i = 0; i < nodes.Count; i++) nodes[i].Threat = ThreatAxis(nodes[i], nodes, ctx);
            return nodes;
        }

        private static bool IsDefendedBuilding(UnitDefinition definition)
        {
            BuildingDefinition building = definition as BuildingDefinition;
            if (building == null) return false;
            return building.buildingType == BuildingType.FAC || building.buildingType == BuildingType.DEP || building.buildingType == BuildingType.AMMO;
        }

        /// <summary>
        /// Factories in the reach of an airbase are part of its core. The others make their own nodes.
        /// </summary>
        private static void AddFactoryNodes(Mission mission, List<DefenseNode> nodes)
        {
            List<SavedBuilding> open = new List<SavedBuilding>();
            float core = CnoEvilConfig.DynamicPlacementCoreRadius_m.Value;
            for (int i = 0; i < mission.buildings.Count; i++)
            {
                SavedBuilding building = mission.buildings[i];
                UnitDefinition definition;
                if (building == null || string.IsNullOrEmpty(building.faction)) continue;
                if (!Encyclopedia.Lookup.TryGetValue(building.type, out definition) || !IsDefendedBuilding(definition)) continue;

                Vector3 local = building.globalPosition.ToLocalPosition();
                bool inCore = false;
                for (int j = 0; j < nodes.Count && !inCore; j++)
                {
                    float reach = nodes[j].Radius + core;
                    inCore = nodes[j].Faction == building.faction && FlatSqr(nodes[j].Center, local) < reach * reach;
                }
                if (!inCore) open.Add(building);
            }

            float linkSqr = AssetLinkMeters * AssetLinkMeters;
            Stack<SavedBuilding> stack = new Stack<SavedBuilding>();
            while (open.Count > 0)
            {
                List<Vector3> group = new List<Vector3>();
                SavedBuilding first = open[open.Count - 1];
                open.RemoveAt(open.Count - 1);
                stack.Push(first);
                while (stack.Count > 0)
                {
                    Vector3 local = stack.Pop().globalPosition.ToLocalPosition();
                    group.Add(local);
                    for (int i = open.Count - 1; i >= 0; i--)
                    {
                        if (open[i].faction != first.faction || FlatSqr(open[i].globalPosition.ToLocalPosition(), local) >= linkSqr) continue;
                        stack.Push(open[i]);
                        open.RemoveAt(i);
                    }
                }

                Vector3 center = Vector3.zero;
                for (int i = 0; i < group.Count; i++) center += group[i];
                center /= group.Count;
                float radius = 0.0f;
                for (int i = 0; i < group.Count; i++) radius = Mathf.Max(radius, FlatDistance(center, group[i]));

                DefenseNode node = new DefenseNode();
                node.Name = first.UniqueName;
                node.Faction = first.faction;
                node.Center = center;
                node.Radius = radius + AssetEdgeMeters;
                nodes.Add(node);
            }
        }

        private static Vector3 ThreatAxis(DefenseNode node, List<DefenseNode> nodes, Context ctx)
        {
            float best = float.MaxValue;
            Vector3 axis = Vector3.zero;
            for (int i = 0; i < nodes.Count; i++)
            {
                if (nodes[i].Faction == node.Faction) continue;
                float sqr = FlatSqr(nodes[i].Center, node.Center);
                if (sqr >= best) continue;
                best = sqr;
                axis = nodes[i].Center - node.Center;
            }

            if (best == float.MaxValue)
            {
                Vector3 enemy;
                if (TryNearestEnemy(node.Center, HqOf(node.Faction, ctx), ctx, out enemy)) axis = enemy - node.Center;
            }
            axis.y = 0.0f;
            if (axis.sqrMagnitude > 1.0f) return axis.normalized;

            float angle = (float)(ctx.Random.NextDouble() * Math.PI * 2.0);
            return new Vector3(Mathf.Cos(angle), 0.0f, Mathf.Sin(angle));
        }
    }
}
