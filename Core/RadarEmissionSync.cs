using System;
using System.Collections.Generic;
using Mirage;
using Mirage.Serialization;
using CnoEvil.Config;

namespace CnoEvil.Core
{
    /// <summary>
    /// Sends the mirror clients the ground and ship radars that RadarEmcon holds off (NetKind.RadarEmission, reliable).
    /// Vanilla has no sync of TargetDetector.activated for these units (aircraft use the vanilla RpcToggleRadar).
    /// Every packet has the full list: at once after a change, and every SendIntervalSeconds for a late join.
    /// The client switches off the listed radars and switches on again only the radars that it switched off.
    /// </summary>
    public static class RadarEmissionSync
    {
        private const float SendIntervalSeconds = 2.0f;
        private const int MaxUnits = 200;

        private static readonly NetChannel s_channel = new NetChannel(NetKind.RadarEmission, OnRadarEmission);
        private static readonly HashSet<Unit> s_clientSilenced = new HashSet<Unit>();
        private static readonly HashSet<Unit> s_received = new HashSet<Unit>();
        private static readonly List<Unit> s_restore = new List<Unit>();

        public static void Update()
        {
            try
            {
                s_channel.Register();
                if (CnoEvilNet.IsMirrorClient)
                {
                    // The host mod is off: vanilla state again
                    if (!CnoEvilConfig.IsModActive && s_clientSilenced.Count > 0) RestoreClient();
                    return;
                }
                if (!CnoEvilNet.IsServerActive) return;

                bool changed = RadarEmcon.ConsumeSilencedChanged();
                if (changed) s_channel.ResetSend();
                if (!s_channel.SendDue(SendIntervalSeconds)) return;

                List<NetPeer> peers = CnoEvilNet.Peers;
                if (peers.Count == 0) return;
                for (int i = 0; i < peers.Count; i++)
                {
                    NetPeer peer = peers[i];
                    if (peer.Connection != null) SendPeer(peer);
                }
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("RadarEmissionSync.Update", ex);
            }
        }

        private static void SendPeer(NetPeer peer)
        {
            List<Unit> units = RadarEmcon.SilencedSurfaceUnits;
            int count = 0;
            for (int i = 0; i < units.Count && count < MaxUnits; i++)
            {
                if (units[i] != null && units[i].persistentID.Id != 0u) count++;
            }
            PooledNetworkWriter w = CnoEvilNet.Begin(NetKind.RadarEmission);
            w.WriteByte((byte)count);
            int written = 0;
            for (int i = 0; i < units.Count && written < count; i++)
            {
                if (units[i] == null || units[i].persistentID.Id == 0u) continue;
                CnoEvilNet.WriteUnit(w, units[i]);
                written++;
            }
            CnoEvilNet.Send(peer.Connection, w, Channel.Reliable);
        }

        // ---------------------------------------------------------------- client

        private static void OnRadarEmission(INetworkPlayer sender, NetworkReader r)
        {
            int count = r.ReadByte();
            s_received.Clear();
            for (int i = 0; i < count; i++)
            {
                Unit u = CnoEvilNet.ReadUnit(r);
                if (u != null) s_received.Add(u);
            }
            if (!CnoEvilNet.IsMirrorClient) return;

            // Switch on the radars that the host no longer holds off
            s_restore.Clear();
            foreach (Unit u in s_clientSilenced)
            {
                if (!s_received.Contains(u)) s_restore.Add(u);
            }
            for (int i = 0; i < s_restore.Count; i++)
            {
                Unit u = s_restore[i];
                s_clientSilenced.Remove(u);
                if (u != null && u.radar != null) u.radar.activated = true;
            }
            s_restore.Clear();

            foreach (Unit u in s_received)
            {
                if (u == null || u.radar == null || s_clientSilenced.Contains(u)) continue;
                if (!u.radar.activated) continue;
                u.radar.activated = false;
                if (u.radar.detectedTargets != null) u.radar.detectedTargets.Clear();
                s_clientSilenced.Add(u);
            }
            s_received.Clear();
        }

        private static void RestoreClient()
        {
            foreach (Unit u in s_clientSilenced)
            {
                if (u != null && u.radar != null) u.radar.activated = true;
            }
            s_clientSilenced.Clear();
        }

        public static void ClearAll()
        {
            s_clientSilenced.Clear();
            s_received.Clear();
            s_restore.Clear();
            s_channel.ResetSend();
        }

        public static void Shutdown()
        {
            ClearAll();
            s_channel.Shutdown();
        }
    }
}
