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

        /// <summary>
        /// Recalibrates every registered radar (a setting that enters the calibration changed, e.g. AtmosphericLossEnabled).
        /// </summary>
        public static void RecalibrateAll()
        {
            foreach (PhysRadarEmitter emitter in s_registry.Values)
            {
                if (emitter != null) emitter.InitializeSpecs();
            }
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
            if ((object)TargetRadar != null) s_registry.Remove(TargetRadar);
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

            if (EvilLog.On(LogCat.Radar))
            {
                string jsonKey = (AttachedUnit != null && AttachedUnit.definition != null) ? AttachedUnit.definition.jsonKey : "-";
                // One line for each radar model: a mission has many units of one type
                EvilLog.Once(LogCat.Radar, string.Format("Radar {0} {1} {2:F0} W", jsonKey, template, Specs.PeakPowerWatts),
                    "Radar {0} (jsonKey {1}): template {2}, range on 1 m2 {3:F0} km, Pt {4:F0} W",
                    AttachedUnit != null ? EvilLog.Label(AttachedUnit) : name, jsonKey, template, referenceRange * 0.001f, Specs.PeakPowerWatts);
            }
        }
    }
}
