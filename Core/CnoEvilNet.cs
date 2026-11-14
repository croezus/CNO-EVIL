using System;
using System.Collections.Generic;
using UnityEngine;
using Mirage;
using Mirage.Serialization;
using NuclearOption.Networking;
using CnoEvil.Config;

namespace CnoEvil.Core
{
    /// <summary>
    /// The only Mirage message of the mod. Data starts with a NetKind byte; the rest is the payload of that kind.
    /// One message type gives one message id (fewer id collisions with the game).
    /// </summary>
    public struct CnoEvilPacket
    {
        public ArraySegment<byte> Data;
    }

    /// <summary>
    /// Authority: single player, host or dedicated server; runs all sensors, tracks and guidance.
    /// Pending: multiplayer client that waits for the host Welcome; the mod is off.
    /// Mirror: multiplayer client on a host with the same mod protocol; shows the host knowledge, runs only its own sensors.
    /// Inert: multiplayer client on a host without the mod (or another protocol); the mod is off.
    /// </summary>
    public enum NetRole : byte
    {
        Authority = 0,
        Pending = 1,
        Mirror = 2,
        Inert = 3
    }

    public enum NetKind : byte
    {
        Hello = 1,          // client -> host: protocol, mod version
        Welcome = 2,        // host -> client: protocol, host mod switch, config key hash
        HostState = 3,      // host -> client: host mod switch changed
        DetectionTier = 4,  // host -> client: detection status line tier
        TrackBatch = 5,     // host -> client: faction track upserts and removals
        TrackReset = 6,     // host -> client: drop all faction tracks, faction of the next tracks
        Feed = 7,           // host -> client: RWR threats and datalink threats of the client aircraft
        MissileState = 8,   // host -> client: dispersion, lock state and aim point of own missiles
        Forget = 9,         // host -> client: units the faction forgot (ADR-15)
        JamBeams = 10,      // host -> client: active jamming pod beams
        Resync = 11,        // client -> host: the client lost its mirrored data (scene load), send all again
        HostConfig = 12,    // host -> client: values of the host-controlled config entries (ConfigSync)
        IrstBearing = 13,   // client -> host: IRST bearing of the HMD thermal window (IrstSync)
        CmChoice = 14,      // client -> host: countermeasure slider choice for the next spawn (CountermeasureSync)
        CmCounts = 15,      // host -> client: countermeasure counts of a player aircraft (CountermeasureSync)
        RadarEmission = 16, // host -> client: ground and ship radars held off by RadarEmcon (RadarEmissionSync)
        Count = 17
    }

    public delegate void NetHandler(INetworkPlayer sender, NetworkReader reader);

    /// <summary>
    /// A client that announced the mod with the same protocol (host side).
    /// </summary>
    public class NetPeer
    {
        public INetworkPlayer Connection;
        public Player Player;
        public Aircraft Aircraft;
        public FactionHQ Faction;
        // Unscaled time of the hello; a peer without a Player after NoPlayerWarnSeconds gets one warning
        public float HelloTime;
        public bool NoPlayerWarned;
    }

    /// <summary>
    /// The client handler and the host send timer of one sync message kind. Register runs every frame (also while
    /// the mod is off), SendDue paces the host sends on unscaled time, Shutdown removes the handler on plugin unload.
    /// </summary>
    public sealed class NetChannel
    {
        private readonly NetKind _kind;
        private readonly NetHandler _handler;
        private readonly Action _onWelcomed;
        private bool _registered;
        private float _nextSend;

        public NetChannel(NetKind kind, NetHandler handler, Action onWelcomed = null)
        {
            _kind = kind;
            _handler = handler;
            _onWelcomed = onWelcomed;
        }

        public void Register()
        {
            if (_registered) return;
            CnoEvilNet.RegisterClientHandler(_kind, _handler);
            if (_onWelcomed != null) CnoEvilNet.ClientWelcomed += _onWelcomed;
            _registered = true;
        }

        /// <summary>
        /// True once per interval; starts the next interval.
        /// </summary>
        public bool SendDue(float intervalSeconds)
        {
            float now = Time.unscaledTime;
            if (now < _nextSend) return false;
            _nextSend = now + intervalSeconds;
            return true;
        }

        public void ResetSend()
        {
            _nextSend = 0.0f;
        }

        public void Shutdown()
        {
            if (!_registered) return;
            CnoEvilNet.RegisterClientHandler(_kind, null);
            if (_onWelcomed != null) CnoEvilNet.ClientWelcomed -= _onWelcomed;
            _registered = false;
        }
    }

    /// <summary>
    /// Multiplayer transport and roles. The host is authoritative for all sensor knowledge; it sends each client with
    /// the mod (hello) only the data of that client: its faction tracks, its RWR and datalink picture, its own missiles.
    /// Peers without the mod never get a mod message (Mirage charges an unknown message as a player error).
    /// The serializer of CnoEvilPacket is set by hand because the Mirage weaver does not process this assembly.
    /// </summary>
    public static class CnoEvilNet
    {
        public const ushort Protocol = 12;

        // Payload limit of one packet: below the smallest transport MTU (Steam 1198 bytes) with headers
        public const int MaxPacketBytes = 1100;

        private const float WelcomeTimeoutSeconds = 8.0f;
        private const float PeerRefreshSeconds = 1.0f;
        private const float HelloGraceSeconds = 10.0f;
        private const float NoPlayerWarnSeconds = 10.0f;
        // Delay of the resync request after a scene load, so the units of the new scene exist on the client
        private const float ResyncDelaySeconds = 2.0f;

        private static bool s_typesRegistered;
        private static bool s_disabled;

        private static readonly NetHandler[] s_clientHandlers = new NetHandler[(int)NetKind.Count];

        // Role
        private static NetRole s_role = NetRole.Authority;
        private static bool s_modAllowed = true;

        // Server
        private static MessageHandler s_serverHandler;
        private static NetworkServer s_server;
        private static readonly List<NetPeer> s_peers = new List<NetPeer>();
        private static float s_nextPeerRefresh;
        private static bool s_lastSentHostModActive;
        private static readonly Dictionary<INetworkPlayer, float> s_unmoddedSince = new Dictionary<INetworkPlayer, float>();
        private static readonly List<INetworkPlayer> s_unmoddedStale = new List<INetworkPlayer>();

        // Client
        private static MessageHandler s_clientHandler;
        private static NetworkClient s_client;
        private static bool s_helloSent;
        private static bool s_welcomed;
        private static bool s_hostModActive;
        private static float s_helloTime;
        private static bool s_inertLogged;
        private static bool s_rejected;
        private static float s_resyncAt = -1.0f;

        // Mirage objects whose Disconnected event has the log listener (added once for each object)
        private static NetworkServer s_eventsServer;
        private static NetworkClient s_eventsClient;

        /// <summary>
        /// Raised on a client when the host Welcome arrives (the client drops its old mirrored data).
        /// </summary>
        public static event Action ClientWelcomed;

        public static NetRole Role
        {
            get { return s_role; }
        }

        public static bool IsMirrorClient
        {
            get { return s_role == NetRole.Mirror; }
        }

        /// <summary>
        /// The mod may run on this machine: always with authority, on a client only while the host runs the same mod.
        /// Read by CnoEvilConfig.IsModActive.
        /// </summary>
        public static bool ModAllowed
        {
            get { return s_modAllowed; }
        }

        /// <summary>
        /// Clients with the mod (host side). Do not modify.
        /// </summary>
        public static List<NetPeer> Peers
        {
            get { return s_peers; }
        }

        /// <summary>
        /// Host side: a connected remote player has not announced the mod (also during the hello grace time).
        /// </summary>
        public static bool HasClientsWithoutMod
        {
            get { return s_unmoddedSince.Count > 0; }
        }

        public static bool IsServerActive
        {
            get { return s_server != null && s_server.Active; }
        }

        public static void RegisterClientHandler(NetKind kind, NetHandler handler)
        {
            s_clientHandlers[(int)kind] = handler;
        }

        /// <summary>
        /// The peer that flies this aircraft, or null (host side).
        /// </summary>
        public static NetPeer FindPeer(Aircraft aircraft)
        {
            if (aircraft == null) return null;
            for (int i = 0; i < s_peers.Count; i++)
            {
                if (s_peers[i].Aircraft == aircraft) return s_peers[i];
            }
            return null;
        }

        // ---------------------------------------------------------------- sending

        public static PooledNetworkWriter Begin(NetKind kind)
        {
            PooledNetworkWriter w = NetworkWriterPool.GetWriter();
            w.WriteByte((byte)kind);
            return w;
        }

        public static void Send(INetworkPlayer player, PooledNetworkWriter w, Channel channel)
        {
            try
            {
                if (player == null || !player.IsConnected)
                {
                    NetStats.Drop(NetDrop.NotConnected);
                    return;
                }
                CnoEvilPacket packet;
                packet.Data = w.ToArraySegment();
                player.Send(packet, channel);
                if (packet.Data.Count > 0) NetStats.Sent(packet.Data.Array[packet.Data.Offset], packet.Data.Count);
            }
            finally
            {
                w.Release();
            }
        }

        public static void SendToServer(PooledNetworkWriter w, Channel channel)
        {
            try
            {
                if (s_client == null || !s_client.IsConnected)
                {
                    NetStats.Drop(NetDrop.NotConnected);
                    return;
                }
                CnoEvilPacket packet;
                packet.Data = w.ToArraySegment();
                s_client.Send(packet, channel);
                if (packet.Data.Count > 0) NetStats.Sent(packet.Data.Array[packet.Data.Offset], packet.Data.Count);
            }
            finally
            {
                w.Release();
            }
        }

        // ---------------------------------------------------------------- serializers

        private static bool RegisterTypes()
        {
            if (s_typesRegistered) return true;
            if (s_disabled) return false;
            try
            {
                int id = MessagePacker.GetId<CnoEvilPacket>();
                Type existing;
                if (MessagePacker.MessageTypes.TryGetValue(id, out existing) && existing != typeof(CnoEvilPacket))
                {
                    CnoEvilPlugin.Log.LogError(string.Format("[CNO-EVIL] Message id {0} is used by {1}. Multiplayer sync is off.", id, existing.FullName));
                    s_disabled = true;
                    return false;
                }
                Writer<CnoEvilPacket>.Write = WritePacket;
                Reader<CnoEvilPacket>.Read = ReadPacket;
                MessagePacker.RegisterMessage<CnoEvilPacket>();
                s_typesRegistered = true;
                EvilLog.Write(LogCat.Net, "mod message registered with Mirage: id {0}, protocol {1}", id, Protocol);
                return true;
            }
            catch (Exception ex)
            {
                s_disabled = true;
                CnoEvilPlugin.Log.LogError(string.Format("[CNO-EVIL] Multiplayer messages disabled: {0}", ex));
                return false;
            }
        }

        private static void WritePacket(NetworkWriter w, CnoEvilPacket m)
        {
            w.WriteBytesAndSizeSegment(m.Data);
        }

        private static CnoEvilPacket ReadPacket(NetworkReader r)
        {
            CnoEvilPacket m;
            m.Data = r.ReadBytesAndSizeSegment();
            return m;
        }

        // ---------------------------------------------------------------- update

        /// <summary>
        /// Called every frame from the plugin, also while the mod is off (registration, hello, role).
        /// </summary>
        public static void Update()
        {
            try
            {
                NetworkManagerNuclearOption nm = NetworkManagerNuclearOption.i;
                NetworkServer server = (nm != null) ? nm.Server : null;
                NetworkClient client = (nm != null) ? nm.Client : null;

                if (server != null && server.Active)
                {
                    s_server = server;
                    UpdateServer(server);
                }
                else if (s_serverHandler != null || s_server != null)
                {
                    EvilLog.Write(LogCat.Net, "host: server stopped, {0} peers dropped", s_peers.Count);
                    s_serverHandler = null;
                    s_server = null;
                    s_peers.Clear();
                    s_unmoddedSince.Clear();
                }

                bool remoteClient = client != null && client.Active && client.IsConnected && !client.IsHost &&
                                    (server == null || !server.Active);
                if (remoteClient)
                {
                    s_client = client;
                    UpdateClient(client);
                }
                else if (s_clientHandler != null || s_client != null)
                {
                    EvilLog.Write(LogCat.Net, "client: connection to the host ended");
                    s_clientHandler = null;
                    s_client = null;
                    ResetClient();
                }

                NetRole role;
                string reason;
                if (!remoteClient) { role = NetRole.Authority; reason = (server != null && server.Active) ? "host" : "no remote connection"; }
                else if (s_welcomed) { role = NetRole.Mirror; reason = "host Welcome received"; }
                else if (s_disabled) { role = NetRole.Inert; reason = "mod message not registered"; }
                else if (s_rejected) { role = NetRole.Inert; reason = "host settings list differs"; }
                else if (s_helloSent && Time.unscaledTime - s_helloTime > WelcomeTimeoutSeconds) { role = NetRole.Inert; reason = "no Welcome in time (host without the mod or other protocol)"; }
                else { role = NetRole.Pending; reason = s_helloSent ? "hello sent, waiting for Welcome" : "waiting for authentication"; }
                SetRole(role, reason);
                NetStats.Update();
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("CnoEvilNet.Update", ex);
            }
        }

        private static void SetRole(NetRole role)
        {
            SetRole(role, null);
        }

        private static void SetRole(NetRole role, string reason)
        {
            if (role != s_role && CnoEvilPlugin.Log != null)
            {
                CnoEvilPlugin.Log.LogInfo(string.Format("[CNO-EVIL] Network role: {0}", role));
                if (reason != null) EvilLog.Write(LogCat.Net, "role {0} -> {1}: {2}", s_role, role, reason);
                if (role == NetRole.Inert && !s_inertLogged)
                {
                    s_inertLogged = true;
                    CnoEvilPlugin.Log.LogWarning("[CNO-EVIL] The host does not run CNO-EVIL protocol " + Protocol +
                                                 ". All players must use the same CNO-EVIL version. The mod is off on this computer for this session.");
                }
            }
            s_role = role;
            // A mirror client runs the mod only with the host settings in use
            s_modAllowed = role == NetRole.Authority || (role == NetRole.Mirror && s_hostModActive && ConfigSync.HostConfigApplied);
            if (role != NetRole.Mirror) ConfigSync.Restore();
        }

        private static void UpdateServer(NetworkServer server)
        {
            MessageHandler handler = server.MessageHandler;
            if (handler == null) return;
            if (handler != s_serverHandler)
            {
                if (!RegisterTypes()) return;
                s_peers.Clear();
                handler.RegisterHandler<CnoEvilPacket>(OnServerPacket, false);
                s_serverHandler = handler;
                if (server != s_eventsServer)
                {
                    s_eventsServer = server;
                    if (server.Disconnected != null) server.Disconnected.AddListener(OnServerDisconnected);
                }
                EvilLog.Write(LogCat.Net, "host: mod handler registered, socket {0}, MTU {1} B, mod packet limit {2} B",
                    SocketName(server.SocketFactory), SocketMtu(server.SocketFactory), MaxPacketBytes);
            }

            float now = Time.unscaledTime;
            if (now >= s_nextPeerRefresh)
            {
                s_nextPeerRefresh = now + PeerRefreshSeconds;
                RefreshPeers(server);
            }

            // The clients follow the host mod switch (F10), so all computers run the same rules
            bool hostActive = CnoEvilConfig.ModEnabled.Value;
            if (hostActive != s_lastSentHostModActive && s_peers.Count > 0)
            {
                for (int i = 0; i < s_peers.Count; i++)
                {
                    PooledNetworkWriter w = Begin(NetKind.HostState);
                    w.WriteBooleanExtension(hostActive);
                    Send(s_peers[i].Connection, w, Channel.Reliable);
                }
            }
            s_lastSentHostModActive = hostActive;
        }

        private static void RefreshPeers(NetworkServer server)
        {
            float now = Time.unscaledTime;
            for (int i = s_peers.Count - 1; i >= 0; i--)
            {
                NetPeer peer = s_peers[i];
                if (peer.Connection == null || !peer.Connection.IsConnected)
                {
                    s_peers.RemoveAt(i);
                    EvilLog.Write(LogCat.Net, "host: peer {0} removed (disconnected), {1} peers", peer.Connection, s_peers.Count);
                    continue;
                }

                Player found = PlayerOf(peer.Connection);
                Aircraft aircraft = (found != null) ? found.Aircraft : null;
                if (aircraft != null && aircraft.disabled) aircraft = null;
                FactionHQ faction = (found != null) ? found.HQ : null;
                if (found != peer.Player || aircraft != peer.Aircraft || faction != peer.Faction)
                {
                    // Which per-client streams the host sends now (each sync class skips a peer without these)
                    EvilLog.Write(LogCat.Net, "peer {0}: player {1}, faction {2}, aircraft {3}; streams: tracks and forget {4}, RWR feed and tier {5}, own missiles {6}",
                        peer.Connection,
                        (found != null) ? ("#" + found.PlayerIndex) : "none",
                        (faction != null && faction.faction != null) ? faction.faction.factionName : "none",
                        (aircraft != null) ? EvilLog.Name(aircraft) : "none",
                        (faction != null) ? "on" : "off (no faction)",
                        (aircraft != null) ? "on" : "off (no aircraft)",
                        (found != null) ? "on" : "off (no player)");
                }
                peer.Player = found;
                peer.Aircraft = aircraft;
                peer.Faction = faction;

                // A client player that cannot be found stops every per-client stream; say so once
                if (found == null && !peer.NoPlayerWarned && now - peer.HelloTime > NoPlayerWarnSeconds)
                {
                    peer.NoPlayerWarned = true;
                    CnoEvilPlugin.Log.LogWarning(string.Format("[CNO-EVIL] No player object found for client {0}. That client gets no host sensor data.", peer.Connection));
                }
            }

            WarnPlayersWithoutMod(server);
        }

        /// <summary>
        /// The Player of a connection: the character that NetworkManagerNuclearOption.SpawnCharacter gives the
        /// connection (ServerObjectManager.AddCharacter). NetworkManagerNuclearOption.GamePlayers is never filled by
        /// the game; UnitRegistry.playerLookup is filled only where a client runs (Player.OnStartClient), so it is
        /// only the fallback. Spectators (SpectatorPlayer) have no Player.
        /// </summary>
        private static Player PlayerOf(INetworkPlayer conn)
        {
            if (conn == null) return null;
            NetworkIdentity identity = conn.Identity;
            Player player = (identity != null) ? identity.GetComponent<Player>() : null;
            if (player != null) return player;

            Dictionary<PlayerRef, Player> lookup = UnitRegistry.playerLookup;
            if (lookup == null) return null;
            foreach (KeyValuePair<PlayerRef, Player> kv in lookup)
            {
                if (kv.Value != null && kv.Value.Owner == conn) return kv.Value;
            }
            return null;
        }

        /// <summary>
        /// The mod must run on all computers. A remote player without hello after HelloGraceSeconds gets one log warning;
        /// that player keeps the vanilla RPCs only.
        /// </summary>
        private static void WarnPlayersWithoutMod(NetworkServer server)
        {
            IReadOnlyList<INetworkPlayer> players = (server != null) ? server.AuthenticatedPlayers : null;
            if (players == null) return;
            float now = Time.unscaledTime;
            for (int p = 0; p < players.Count; p++)
            {
                INetworkPlayer conn = players[p];
                if (conn == null || conn.IsHost || FindPeerByConnection(conn) != null) continue;
                float since;
                if (!s_unmoddedSince.TryGetValue(conn, out since))
                {
                    s_unmoddedSince[conn] = now;
                    continue;
                }
                if (since >= 0.0f && now - since > HelloGraceSeconds)
                {
                    s_unmoddedSince[conn] = -1.0f;
                    CnoEvilPlugin.Log.LogWarning(string.Format("[CNO-EVIL] Player {0} does not run CNO-EVIL protocol {1}. All players must use the same CNO-EVIL version; that player sees vanilla sensors only.",
                        conn, Protocol));
                }
            }

            if (s_unmoddedSince.Count == 0) return;
            s_unmoddedStale.Clear();
            foreach (KeyValuePair<INetworkPlayer, float> kv in s_unmoddedSince)
            {
                if (kv.Key == null || !kv.Key.IsConnected || FindPeerByConnection(kv.Key) != null) s_unmoddedStale.Add(kv.Key);
            }
            for (int i = 0; i < s_unmoddedStale.Count; i++) s_unmoddedSince.Remove(s_unmoddedStale[i]);
        }

        private static void OnServerPacket(INetworkPlayer player, CnoEvilPacket msg)
        {
            PooledNetworkReader r = null;
            try
            {
                if (msg.Data.Array == null || msg.Data.Count < 1)
                {
                    NetStats.Drop(NetDrop.Empty);
                    return;
                }
                r = NetworkReaderPool.GetReader(msg.Data, null);
                // Clients send only Hello, Resync, IrstBearing and CmChoice
                byte kind = r.ReadByte();
                NetStats.Received(kind, msg.Data.Count);
                if (kind == (byte)NetKind.Hello) OnHello(player, r);
                else if (kind == (byte)NetKind.Resync) OnResync(player);
                else if (kind == (byte)NetKind.IrstBearing || kind == (byte)NetKind.CmChoice)
                {
                    NetPeer peer = FindPeerByConnection(player);
                    if (peer == null) NetStats.Drop(NetDrop.NotPeer);
                    if (kind == (byte)NetKind.IrstBearing) IrstSync.OnServerBearing(peer, r);
                    else CountermeasureSync.OnServerChoice(peer, r);
                }
                else NetStats.Drop(NetDrop.UnknownKind);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("CnoEvilNet.OnServerPacket", ex);
            }
            finally
            {
                if (r != null) r.Release();
            }
        }

        private static NetPeer FindPeerByConnection(INetworkPlayer player)
        {
            for (int i = 0; i < s_peers.Count; i++)
            {
                if (s_peers[i].Connection == player) return s_peers[i];
            }
            return null;
        }

        private static void OnHello(INetworkPlayer player, NetworkReader r)
        {
            if (player == null) return;
            ushort protocol = r.ReadUInt16();
            if (protocol != Protocol)
            {
                NetStats.Drop(NetDrop.Protocol);
                CnoEvilPlugin.Log.LogWarning(string.Format("[CNO-EVIL] A client uses mod protocol {0}, the host uses {1}. That client runs without the mod.", protocol, Protocol));
                return;
            }

            NetPeer peer = FindPeerByConnection(player);
            if (peer == null)
            {
                peer = new NetPeer();
                peer.Connection = player;
                peer.HelloTime = Time.unscaledTime;
                s_peers.Add(peer);
                EvilLog.Write(LogCat.Net, "host: hello from {0}, protocol {1}; new peer, {2} peers", player, protocol, s_peers.Count);
            }
            else
            {
                EvilLog.Write(LogCat.Net, "host: hello again from peer {0}; Welcome and settings sent again", player);
            }
            s_nextPeerRefresh = 0.0f;

            PooledNetworkWriter w = Begin(NetKind.Welcome);
            w.WriteUInt16(Protocol);
            w.WriteBooleanExtension(CnoEvilConfig.ModEnabled.Value);
            w.WriteUInt32(ConfigSync.KeyHash);
            Send(player, w, Channel.Reliable);
            // The client runs the mod only after these values arrive (same reliable channel, after Welcome)
            ConfigSync.SendTo(player);
            CountermeasureSync.SendAllTo(player);
            CnoEvilPlugin.Log.LogInfo("[CNO-EVIL] A client with the mod joined.");
        }

        private static void OnResync(INetworkPlayer player)
        {
            NetPeer peer = FindPeerByConnection(player);
            if (peer == null)
            {
                NetStats.Drop(NetDrop.NotPeer);
                EvilLog.Write(LogCat.Net, "host: resync from {0} ignored, no hello from that connection", player);
                return;
            }
            EvilLog.Write(LogCat.Net, "host: resync from peer {0}; tracks, tier and countermeasure counts sent again", player);
            TrackSync.ResendPeer(peer);
            DetectionStatusNet.ResendPeer(peer);
            CountermeasureSync.SendAllTo(peer.Connection);
        }

        private static void UpdateClient(NetworkClient client)
        {
            MessageHandler handler = client.MessageHandler;
            if (handler == null) return;
            if (handler != s_clientHandler)
            {
                if (!RegisterTypes()) return;
                ResetClient();
                handler.RegisterHandler<CnoEvilPacket>(OnClientPacket, false);
                s_clientHandler = handler;
                if (client != s_eventsClient)
                {
                    s_eventsClient = client;
                    if (client.Disconnected != null) client.Disconnected.AddListener(OnClientDisconnected);
                }
                EvilLog.Write(LogCat.Net, "client: mod handler registered, socket {0}, MTU {1} B, mod packet limit {2} B",
                    SocketName(client.SocketFactory), SocketMtu(client.SocketFactory), MaxPacketBytes);
            }

            if (!s_helloSent && client.Player != null && client.Player.IsAuthenticated)
            {
                PooledNetworkWriter w = Begin(NetKind.Hello);
                w.WriteUInt16(Protocol);
                SendToServer(w, Channel.Reliable);
                s_helloSent = true;
                s_helloTime = Time.unscaledTime;
                EvilLog.Write(LogCat.Net, "client: hello sent to the host, protocol {0}", Protocol);
            }

            if (s_welcomed && s_resyncAt > 0.0f && Time.unscaledTime >= s_resyncAt)
            {
                s_resyncAt = -1.0f;
                SendToServer(Begin(NetKind.Resync), Channel.Reliable);
                EvilLog.Write(LogCat.Net, "client: resync sent to the host");
            }
        }

        /// <summary>
        /// Mirror client: the scene load dropped the mirrored data; ask the host for all of it again (after a delay).
        /// </summary>
        public static void RequestResync()
        {
            if (s_role != NetRole.Mirror) return;
            s_resyncAt = Time.unscaledTime + ResyncDelaySeconds;
            EvilLog.Write(LogCat.Net, "resync request to the host in {0:F1} s", ResyncDelaySeconds);
        }

        private static void OnClientPacket(INetworkPlayer player, CnoEvilPacket msg)
        {
            PooledNetworkReader r = null;
            try
            {
                if (msg.Data.Array == null || msg.Data.Count < 1)
                {
                    NetStats.Drop(NetDrop.Empty);
                    return;
                }
                r = NetworkReaderPool.GetReader(msg.Data, null);
                byte kind = r.ReadByte();
                NetStats.Received(kind, msg.Data.Count);
                if (kind == (byte)NetKind.Welcome)
                {
                    ushort protocol = r.ReadUInt16();
                    if (protocol != Protocol)
                    {
                        NetStats.Drop(NetDrop.Protocol);
                        EvilLog.Write(LogCat.Net, "client: Welcome with protocol {0} ignored, this computer uses {1}", protocol, Protocol);
                        return;
                    }
                    bool hostModActive = r.ReadBooleanExtension();
                    uint keyHash = r.ReadUInt32();
                    EvilLog.Write(LogCat.Net, "client: Welcome from the host, host mod {0}, settings key hash {1:X8} (this computer {2:X8})",
                        hostModActive ? "on" : "off", keyHash, ConfigSync.KeyHash);
                    if (keyHash != ConfigSync.KeyHash)
                    {
                        // Same protocol, other settings list (other build): the host values cannot be used
                        NetStats.Drop(NetDrop.KeyHash);
                        s_rejected = true;
                        CnoEvilPlugin.Log.LogWarning("[CNO-EVIL] The host uses another CNO-EVIL build (settings list differs). The mod is off on this computer for this session.");
                        return;
                    }
                    s_hostModActive = hostModActive;
                    s_welcomed = true;
                    SetRole(NetRole.Mirror);
                    if (ClientWelcomed != null) ClientWelcomed();
                    CnoEvilPlugin.Log.LogInfo("[CNO-EVIL] The host runs CNO-EVIL: host knowledge sync is on.");
                    return;
                }
                if (!s_welcomed)
                {
                    NetStats.Drop(NetDrop.BeforeWelcome);
                    return;
                }
                if (kind >= (byte)NetKind.Count)
                {
                    NetStats.Drop(NetDrop.UnknownKind);
                    return;
                }
                if (kind == (byte)NetKind.HostState)
                {
                    bool active = r.ReadBooleanExtension();
                    if (active != s_hostModActive) EvilLog.Write(LogCat.Net, "client: host mod switched {0}", active ? "on" : "off");
                    s_hostModActive = active;
                    SetRole(NetRole.Mirror);
                    return;
                }
                NetHandler handler = s_clientHandlers[kind];
                if (handler != null) handler(player, r);
                else NetStats.Drop(NetDrop.NoHandler);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("CnoEvilNet.OnClientPacket", ex);
            }
            finally
            {
                if (r != null) r.Release();
            }
        }

        // ---------------------------------------------------------------- Mirage events (log only)

        private static void OnServerDisconnected(INetworkPlayer player)
        {
            try
            {
                bool wasPeer = FindPeerByConnection(player) != null;
                EvilLog.Write(LogCat.Net, "host: Mirage disconnect of {0} ({1})", player, wasPeer ? "peer with the mod" : "no mod hello");
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("CnoEvilNet.OnServerDisconnected", ex);
            }
        }

        private static void OnClientDisconnected(ClientStoppedReason reason)
        {
            try
            {
                EvilLog.Write(LogCat.Net, "client: Mirage disconnect from the host, reason {0}", reason);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("CnoEvilNet.OnClientDisconnected", ex);
            }
        }

        private static string SocketName(Mirage.SocketLayer.SocketFactory factory)
        {
            return (factory != null) ? factory.GetType().Name : "none";
        }

        private static int SocketMtu(Mirage.SocketLayer.SocketFactory factory)
        {
            try
            {
                return (factory != null) ? factory.MaxPacketSize : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static void ResetClient()
        {
            s_helloSent = false;
            s_welcomed = false;
            s_hostModActive = false;
            s_helloTime = 0.0f;
            s_rejected = false;
            s_resyncAt = -1.0f;
        }

        /// <summary>
        /// Plugin unload: drop the handlers and all state.
        /// </summary>
        public static void Shutdown()
        {
            try
            {
                if (s_serverHandler != null) s_serverHandler.UnregisterHandler<CnoEvilPacket>();
                if (s_clientHandler != null) s_clientHandler.UnregisterHandler<CnoEvilPacket>();
                if (s_eventsServer != null && s_eventsServer.Disconnected != null) s_eventsServer.Disconnected.RemoveListener(OnServerDisconnected);
                if (s_eventsClient != null && s_eventsClient.Disconnected != null) s_eventsClient.Disconnected.RemoveListener(OnClientDisconnected);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("CnoEvilNet.Shutdown", ex);
            }
            s_serverHandler = null;
            s_clientHandler = null;
            s_eventsServer = null;
            s_eventsClient = null;
            s_server = null;
            s_client = null;
            s_peers.Clear();
            s_unmoddedSince.Clear();
            ResetClient();
            s_role = NetRole.Authority;
            s_modAllowed = true;
        }

        // ---------------------------------------------------------------- helpers for the sync classes

        /// <summary>
        /// Age (s) of a host timestamp as ushort centiseconds; 0xFFFF = no timestamp (0) or older than 655 s.
        /// </summary>
        public static void WriteAge(NetworkWriter w, float now, float timestamp)
        {
            if (timestamp <= 0.0f)
            {
                w.WriteUInt16(ushort.MaxValue);
                return;
            }
            float age = Mathf.Max(0.0f, now - timestamp) * 100.0f;
            w.WriteUInt16(age >= ushort.MaxValue - 1 ? (ushort)(ushort.MaxValue - 1) : (ushort)age);
        }

        /// <summary>
        /// Local timestamp from a written age; 0 for "no timestamp". A time older than the local level load is negative
        /// (a client that joined late), so readers test a timestamp with != 0, not > 0.
        /// </summary>
        public static float ReadAge(NetworkReader r, float now)
        {
            ushort v = r.ReadUInt16();
            if (v == ushort.MaxValue) return 0.0f;
            float t = now - v * 0.01f;
            return (t == 0.0f) ? -0.0001f : t;
        }

        public static void WriteUnit(NetworkWriter w, Unit unit)
        {
            w.WritePackedUInt32(unit != null ? unit.persistentID.Id : 0u);
        }

        public static Unit ReadUnit(NetworkReader r)
        {
            uint id = r.ReadPackedUInt32();
            if (id == 0u) return null;
            PersistentID pid = default(PersistentID);
            pid.Id = id;
            Unit unit;
            return UnitRegistry.TryGetUnit(pid, out unit) ? unit : null;
        }

        public static void WriteGlobal(NetworkWriter w, GlobalPosition p)
        {
            w.WriteSingle(p.x);
            w.WriteSingle(p.y);
            w.WriteSingle(p.z);
        }

        public static GlobalPosition ReadGlobal(NetworkReader r)
        {
            GlobalPosition p = default(GlobalPosition);
            p.x = r.ReadSingle();
            p.y = r.ReadSingle();
            p.z = r.ReadSingle();
            return p;
        }

        /// <summary>
        /// Velocity in m/s as 3 x int16 (+-32 km/s).
        /// </summary>
        public static void WriteVelocity(NetworkWriter w, Vector3 v)
        {
            w.WriteInt16((short)Mathf.Clamp(Mathf.RoundToInt(v.x), short.MinValue, short.MaxValue));
            w.WriteInt16((short)Mathf.Clamp(Mathf.RoundToInt(v.y), short.MinValue, short.MaxValue));
            w.WriteInt16((short)Mathf.Clamp(Mathf.RoundToInt(v.z), short.MinValue, short.MaxValue));
        }

        public static Vector3 ReadVelocity(NetworkReader r)
        {
            float x = r.ReadInt16();
            float y = r.ReadInt16();
            float z = r.ReadInt16();
            return new Vector3(x, y, z);
        }

        /// <summary>
        /// Distance as ushort: 1 m steps below 32768 m, 16 m steps above (max about 557 km).
        /// </summary>
        public static void WriteMeters(NetworkWriter w, float meters)
        {
            int m = Mathf.RoundToInt(Mathf.Max(0.0f, meters));
            int v = (m < 32768) ? m : 32768 + (m - 32768) / 16;
            w.WriteUInt16((ushort)Mathf.Min(v, ushort.MaxValue));
        }

        public static float ReadMeters(NetworkReader r)
        {
            int v = r.ReadUInt16();
            return (v < 32768) ? v : 32768.0f + (v - 32768) * 16.0f;
        }

        /// <summary>
        /// Remaining time (s) until a future host timestamp as ushort centiseconds (0 = already past).
        /// </summary>
        public static void WriteRemaining(NetworkWriter w, float now, float until)
        {
            float rem = Mathf.Clamp((until - now) * 100.0f, 0.0f, ushort.MaxValue - 1);
            w.WriteUInt16((ushort)rem);
        }

        public static float ReadRemaining(NetworkReader r, float now)
        {
            ushort v = r.ReadUInt16();
            return (v == 0) ? 0.0f : now + v * 0.01f;
        }
    }
}
