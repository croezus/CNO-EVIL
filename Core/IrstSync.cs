using System;
using System.Collections.Generic;
using UnityEngine;
using Mirage;
using Mirage.Serialization;
using CnoEvil.Config;

namespace CnoEvil.Core
{
    /// <summary>
    /// IRST bearings of the HMD thermal window (NetKind.IrstBearing, client -> host). The window runs on the machine of
    /// the pilot, the passive tracks run on the host: a mirror client sends each bearing, the host checks it against the
    /// true geometry (angle, range, terrain line of sight, rate) and gives it to RWRTriangulationProcessor.RegisterIrstBearing.
    /// Authority (single player, host) calls RegisterIrstBearing directly.
    /// </summary>
    public static class IrstSync
    {
        // Highest IR intensity the host accepts in the range check (afterburner)
        private const float MaxIntensity = 2.0f;
        private const float MaxZoom = 10.0f;
        private const float MinAngleToleranceDeg = 2.0f;
        private const int MaxBearingsPerSecond = 20;

        private struct RateState
        {
            public float WindowStart;
            public int Count;
        }

        private static readonly Dictionary<INetworkPlayer, RateState> s_rates = new Dictionary<INetworkPlayer, RateState>();

        /// <summary>
        /// A bearing from the local window: direct on authority, to the host on a mirror client, dropped otherwise.
        /// </summary>
        public static void Submit(Aircraft observer, Unit target, Vector3 measuredBearing, float strength01, float sigmaRad)
        {
            if (observer == null || target == null) return;
            NetRole role = CnoEvilNet.Role;
            if (role == NetRole.Authority)
            {
                RWRTriangulationProcessor.RegisterIrstBearing(observer, target, measuredBearing, strength01, sigmaRad);
                return;
            }
            if (role != NetRole.Mirror || target.persistentID.Id == 0u) return;

            PooledNetworkWriter w = CnoEvilNet.Begin(NetKind.IrstBearing);
            CnoEvilNet.WriteUnit(w, target);
            w.WriteSingle(measuredBearing.x);
            w.WriteSingle(measuredBearing.y);
            w.WriteSingle(measuredBearing.z);
            w.WriteByte((byte)Mathf.RoundToInt(Mathf.Clamp01(strength01) * 255.0f));
            w.WriteUInt16((ushort)Mathf.Clamp(Mathf.RoundToInt(sigmaRad * 100000.0f), 1, ushort.MaxValue));
            CnoEvilNet.SendToServer(w, Channel.Unreliable);
        }

        /// <summary>
        /// Host: a bearing from a client with the mod. Dropped when it does not agree with the true geometry.
        /// </summary>
        public static void OnServerBearing(NetPeer peer, NetworkReader r)
        {
            Unit target = CnoEvilNet.ReadUnit(r);
            Vector3 bearing;
            bearing.x = r.ReadSingle();
            bearing.y = r.ReadSingle();
            bearing.z = r.ReadSingle();
            float strength01 = r.ReadByte() / 255.0f;
            float sigmaRad = r.ReadUInt16() / 100000.0f;

            if (peer == null || peer.Connection == null || !CnoEvilConfig.IsModActive || !CnoEvilConfig.HMDThermalIrstEnabled.Value) return;
            Aircraft observer = peer.Aircraft;
            if (observer == null || observer.disabled)
            {
                NetStats.RecordDropped(NetKind.IrstBearing, "client has no aircraft on the host");
                return;
            }
            if (target == null || target.disabled)
            {
                NetStats.RecordDropped(NetKind.IrstBearing, "target not on the host");
                return;
            }
            if (!AllowRate(peer.Connection))
            {
                NetStats.RecordDropped(NetKind.IrstBearing, "rate limit");
                return;
            }
            if (bearing.sqrMagnitude < 0.25f || float.IsNaN(bearing.x) || float.IsNaN(bearing.y) || float.IsNaN(bearing.z))
            {
                NetStats.RecordDropped(NetKind.IrstBearing, "invalid bearing");
                return;
            }

            Vector3 eye = observer.transform.position;
            // Ground vehicles and ships: the middle of the hull, as the window aims
            Vector3 heatPoint = ThermalSignature.GetHeatPoint(target);
            Vector3 toTarget = heatPoint - eye;
            float dist = toTarget.magnitude;
            float maxRange = Mathf.Max(0.0f, CnoEvilConfig.HMDThermalIrstRange_m.Value) * Mathf.Sqrt(MaxIntensity) *
                             Mathf.Pow(MaxZoom, Mathf.Max(0.0f, CnoEvilConfig.HMDThermalIrstZoomExponent.Value));
            if (dist < 1.0f || dist > maxRange)
            {
                NetStats.RecordDropped(NetKind.IrstBearing, "range on the host");
                return;
            }

            // Sigma not below the 10x optics; the angle check uses it
            float minSigmaRad = Mathf.Max(0.0f, CnoEvilConfig.HMDThermalIrstBearingError_deg.Value) * Mathf.Deg2Rad / MaxZoom;
            sigmaRad = Mathf.Max(sigmaRad, minSigmaRad);
            float toleranceDeg = Mathf.Max(3.0f * sigmaRad * Mathf.Rad2Deg, MinAngleToleranceDeg);
            if (Vector3.Angle(bearing, toTarget) > toleranceDeg)
            {
                NetStats.RecordDropped(NetKind.IrstBearing, "angle to the target on the host");
                return;
            }

            if (Physics.Linecast(eye, heatPoint, PhysicsLayers.StaticsMask))
            {
                NetStats.RecordDropped(NetKind.IrstBearing, "no line of sight on the host");
                return;
            }

            RWRTriangulationProcessor.RegisterIrstBearing(observer, target, bearing, strength01, sigmaRad);
        }

        private static bool AllowRate(INetworkPlayer player)
        {
            float now = Time.unscaledTime;
            RateState state;
            s_rates.TryGetValue(player, out state);
            if (now - state.WindowStart >= 1.0f)
            {
                state.WindowStart = now;
                state.Count = 0;
            }
            state.Count++;
            s_rates[player] = state;
            return state.Count <= MaxBearingsPerSecond;
        }

        public static void ClearAll()
        {
            s_rates.Clear();
        }
    }
}
