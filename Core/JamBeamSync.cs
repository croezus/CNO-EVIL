using System;
using System.Collections.Generic;
using UnityEngine;
using Mirage;
using Mirage.Serialization;
using CnoEvil.Config;

namespace CnoEvil.Core
{
    /// <summary>
    /// Sends the mirror clients the active jamming pod beams (NetKind.JamBeams, unreliable, SendIntervalSeconds):
    /// owner, target, pod index, capacitor fraction and range. The vanilla pod tick runs on the host only, so the client
    /// rebuilds StandoffJamming from these beams (TacScreen EM ring, map jamming cones). Every packet has the full state.
    /// </summary>
    public static class JamBeamSync
    {
        private const float SendIntervalSeconds = 0.2f;
        private const int MaxBeams = 32;

        private static readonly List<PodBeam> s_sendBuffer = new List<PodBeam>();
        private static float s_nextSend;
        private static bool s_handlersRegistered;

        public static void Update()
        {
            try
            {
                if (!s_handlersRegistered)
                {
                    CnoEvilNet.RegisterClientHandler(NetKind.JamBeams, OnJamBeams);
                    s_handlersRegistered = true;
                }
                if (!CnoEvilNet.IsServerActive || !CnoEvilConfig.IsModActive) return;

                float unscaled = Time.unscaledTime;
                if (unscaled < s_nextSend) return;
                s_nextSend = unscaled + SendIntervalSeconds;

                List<NetPeer> peers = CnoEvilNet.Peers;
                if (peers.Count == 0) return;

                float now = Time.timeSinceLevelLoad;
                List<PodBeam> beams = StandoffJamming.Beams;
                s_sendBuffer.Clear();
                for (int i = 0; i < beams.Count && s_sendBuffer.Count < MaxBeams; i++)
                {
                    PodBeam b = beams[i];
                    if (b.Pod == null || !StandoffJamming.IsActive(b, now)) continue;
                    if (b.Owner.persistentID.Id == 0u || b.Target.persistentID.Id == 0u) continue;
                    s_sendBuffer.Add(b);
                }
                if (s_sendBuffer.Count == 0) return;

                long perf = PerfStats.Begin();
                for (int i = 0; i < peers.Count; i++)
                {
                    NetPeer peer = peers[i];
                    if (peer.Connection != null) SendPeer(peer);
                }
                PerfStats.End(PerfSlot.NetServer, perf);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("JamBeamSync.Update", ex);
            }
        }

        private static void SendPeer(NetPeer peer)
        {
            PooledNetworkWriter w = CnoEvilNet.Begin(NetKind.JamBeams);
            w.WriteByte((byte)s_sendBuffer.Count);
            for (int i = 0; i < s_sendBuffer.Count; i++)
            {
                PodBeam b = s_sendBuffer[i];
                CnoEvilNet.WriteUnit(w, b.Owner);
                CnoEvilNet.WriteUnit(w, b.Target);
                w.WriteByte((byte)Mathf.Clamp(b.PodIndex, 0, 255));
                w.WriteByte((byte)Mathf.Clamp(Mathf.RoundToInt(b.PowerFraction * 255.0f), 0, 255));
                CnoEvilNet.WriteMeters(w, b.MaxRange);
            }
            CnoEvilNet.Send(peer.Connection, w, Channel.Unreliable);
        }

        // ---------------------------------------------------------------- client

        private static void OnJamBeams(INetworkPlayer sender, NetworkReader r)
        {
            int count = r.ReadByte();
            for (int i = 0; i < count; i++)
            {
                Unit owner = CnoEvilNet.ReadUnit(r);
                Unit target = CnoEvilNet.ReadUnit(r);
                int podIndex = r.ReadByte();
                float powerFraction = r.ReadByte() / 255.0f;
                float maxRange = CnoEvilNet.ReadMeters(r);
                if (owner == null || target == null) continue;
                StandoffJamming.ApplyRemoteBeam(owner, target, podIndex, powerFraction, maxRange);
            }
        }

        public static void ClearAll()
        {
            s_sendBuffer.Clear();
            s_nextSend = 0.0f;
        }

        public static void Shutdown()
        {
            ClearAll();
            if (s_handlersRegistered)
            {
                CnoEvilNet.RegisterClientHandler(NetKind.JamBeams, null);
                s_handlersRegistered = false;
            }
        }
    }
}
