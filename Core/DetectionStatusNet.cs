using System;
using System.Collections.Generic;
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
        // Server
        private static readonly Dictionary<INetworkPlayer, byte> s_lastSent = new Dictionary<INetworkPlayer, byte>();
        private static readonly List<INetworkPlayer> s_staleKeys = new List<INetworkPlayer>();
        private static readonly NetChannel s_channel = new NetChannel(NetKind.DetectionTier, OnDetection, ResetClient);

        // Client
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
                s_channel.Register();

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
            if (!s_channel.SendDue(DetectionStatus.Interval)) return;

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
        /// Host: the client asked for all data again (scene load); send its tier at the next interval.
        /// </summary>
        public static void ResendPeer(NetPeer peer)
        {
            if (peer != null && peer.Connection != null) s_lastSent.Remove(peer.Connection);
        }

        /// <summary>
        /// Scene change: the connections stay. The host resends every tier; a client keeps the last received tier
        /// (the host sends only changes; Welcome and role changes reset it).
        /// </summary>
        public static void ClearAll()
        {
            s_lastSent.Clear();
            s_channel.ResetSend();
        }

        /// <summary>
        /// Plugin unload: drop all state.
        /// </summary>
        public static void Shutdown()
        {
            s_channel.Shutdown();
            s_lastSent.Clear();
            ResetClient();
        }
    }
}
