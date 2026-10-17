using System;
using System.Collections.Generic;
using UnityEngine;
using Mirage;
using Mirage.Serialization;
using NuclearOption.Networking;

namespace CnoEvil.Core
{
    /// <summary>
    /// Sends the detection status line to multiplayer clients. The host runs the hostile sensors, so only it
    /// has the hostile faction tracks. The host sends each client with the mod (CnoEvilNet peer) one byte
    /// (NetKind.DetectionTier) when the tier of its aircraft changes, evaluated every DetectionStatus.Interval.
    /// </summary>
    public static class DetectionStatusNet
    {
        private const byte NotSent = 255;

        // Server
        private static readonly Dictionary<INetworkPlayer, byte> s_lastSent = new Dictionary<INetworkPlayer, byte>();
        private static readonly List<INetworkPlayer> s_staleKeys = new List<INetworkPlayer>();
        private static float s_nextServerTick;

        // Client
        private static bool s_handlerRegistered;
        private static bool s_hasServerData;
        private static DetectionTier s_receivedTier = DetectionTier.None;

        /// <summary>
        /// This machine is a pure client: the hostile faction tracks are on the host.
        /// </summary>
        public static bool IsRemoteClient
        {
            get
            {
                NetworkManagerNuclearOption nm = NetworkManagerNuclearOption.i;
                if (nm == null || nm.Client == null || !nm.Client.Active || nm.Client.IsHost) return false;
                return nm.Server == null || !nm.Server.Active;
            }
        }

        /// <summary>
        /// The host has sent at least one tier on this connection (it runs the mod).
        /// </summary>
        public static bool HasServerData
        {
            get { return s_hasServerData; }
        }

        public static DetectionTier ReceivedTier
        {
            get { return s_receivedTier; }
        }

        /// <summary>
        /// Called every frame from the plugin after CnoEvilNet.Update.
        /// </summary>
        public static void Update()
        {
            try
            {
                if (!s_handlerRegistered)
                {
                    CnoEvilNet.RegisterClientHandler(NetKind.DetectionTier, OnDetection);
                    CnoEvilNet.ClientWelcomed += ResetClient;
                    s_handlerRegistered = true;
                }

                if (!CnoEvilNet.IsMirrorClient && (s_hasServerData || s_receivedTier != DetectionTier.None))
                {
                    ResetClient();
                }

                if (CnoEvilNet.IsServerActive) UpdateServer();
                else if (s_lastSent.Count > 0) s_lastSent.Clear();
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("DetectionStatusNet.Update", ex);
            }
        }

        private static void UpdateServer()
        {
            List<NetPeer> peers = CnoEvilNet.Peers;
            if (peers.Count == 0)
            {
                if (s_lastSent.Count > 0) s_lastSent.Clear();
                return;
            }
            float now = Time.unscaledTime;
            if (now < s_nextServerTick) return;
            s_nextServerTick = now + DetectionStatus.Interval;

            // Drop disconnected players
            if (s_lastSent.Count > peers.Count)
            {
                s_staleKeys.Clear();
                foreach (KeyValuePair<INetworkPlayer, byte> kv in s_lastSent)
                {
                    bool found = false;
                    for (int i = 0; i < peers.Count; i++)
                    {
                        if (peers[i].Connection == kv.Key) { found = true; break; }
                    }
                    if (!found) s_staleKeys.Add(kv.Key);
                }
                for (int i = 0; i < s_staleKeys.Count; i++) s_lastSent.Remove(s_staleKeys[i]);
            }

            bool enabled = DetectionStatus.IsEnabled;
            for (int i = 0; i < peers.Count; i++)
            {
                NetPeer peer = peers[i];
                if (peer.Connection == null) continue;
                DetectionTier tier = (enabled && peer.Aircraft != null) ? DetectionStatus.Evaluate(peer.Aircraft) : DetectionTier.None;
                byte value = (byte)tier;
                byte last;
                if (s_lastSent.TryGetValue(peer.Connection, out last) && last == value) continue;
                s_lastSent[peer.Connection] = value;
                PooledNetworkWriter w = CnoEvilNet.Begin(NetKind.DetectionTier);
                w.WriteByte(value);
                CnoEvilNet.Send(peer.Connection, w, Channel.Reliable);
            }
        }

        private static void OnDetection(INetworkPlayer player, NetworkReader r)
        {
            byte tier = r.ReadByte();
            s_hasServerData = true;
            s_receivedTier = (tier <= (byte)DetectionTier.Optical) ? (DetectionTier)tier : DetectionTier.None;
        }

        private static void ResetClient()
        {
            s_hasServerData = false;
            s_receivedTier = DetectionTier.None;
        }

        /// <summary>
        /// Scene change: the connections stay. Resend every tier.
        /// </summary>
        public static void ClearAll()
        {
            s_receivedTier = DetectionTier.None;
            s_lastSent.Clear();
            s_nextServerTick = 0.0f;
        }

        /// <summary>
        /// Plugin unload: drop all state.
        /// </summary>
        public static void Shutdown()
        {
            if (s_handlerRegistered)
            {
                CnoEvilNet.RegisterClientHandler(NetKind.DetectionTier, null);
                CnoEvilNet.ClientWelcomed -= ResetClient;
                s_handlerRegistered = false;
            }
            s_lastSent.Clear();
            ResetClient();
        }
    }
}
