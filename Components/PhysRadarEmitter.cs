using System;
using System.Collections.Generic;
using UnityEngine;
using CnoEvil.Config;
using CnoEvil.Core;

namespace CnoEvil.Components
{
    public class PhysRadarEmitter : MonoBehaviour
    {
        private static readonly Dictionary<Radar, PhysRadarEmitter> s_registry = new Dictionary<Radar, PhysRadarEmitter>();

        public static PhysRadarEmitter Get(Radar radar)
        {
            if (radar == null) return null;
            PhysRadarEmitter emitter;
            if (!s_registry.TryGetValue(radar, out emitter))
            {
                emitter = radar.GetComponent<PhysRadarEmitter>();
                if (emitter != null) s_registry[radar] = emitter;
            }
            return emitter;
        }

        public static void ClearAll()
        {
            s_registry.Clear();
        }

        public Radar TargetRadar;
        public Unit AttachedUnit;
        public RadarSpecs Specs;
        public float ThermalNoiseFloor = 0.0f;

        private void Awake()
        {
            TargetRadar = GetComponent<Radar>();
            if (TargetRadar != null) s_registry[TargetRadar] = this;
            AttachedUnit = GetComponentInParent<Unit>();

            InitializeSpecs();
        }

        private void OnDestroy()
        {
            if (TargetRadar != null) s_registry.Remove(TargetRadar);
        }

        public void InitializeSpecs()
        {
            if (AttachedUnit == null)
            {
                AttachedUnit = GetComponentInParent<Unit>();
            }

            RadarTemplate template;
            float referenceRange;
            Specs = RadarSpecs.GetRadarSpecsForPlatform(AttachedUnit, TargetRadar, out template, out referenceRange);
            ThermalNoiseFloor = RadarPhysics.CalculateThermalNoiseFloor(Specs.BandwidthHz, Specs.NoiseFigureLinear);

            if (CnoEvilConfig.DebugLogging != null && CnoEvilConfig.DebugLogging.Value && CnoEvilPlugin.Log != null)
            {
                CnoEvilPlugin.Log.LogInfo(string.Format("[CNO-EVIL] Radar {0} (jsonKey {1}): template {2}, range on 1 m2 {3:F0} km, Pt {4:F0} W",
                    AttachedUnit != null ? AttachedUnit.unitName : name,
                    (AttachedUnit != null && AttachedUnit.definition != null) ? AttachedUnit.definition.jsonKey : "-",
                    template, referenceRange * 0.001f, Specs.PeakPowerWatts));
            }
        }
    }
}
