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
        Welcome = 2,        // host -> client: protocol, host mod switch
        HostState = 3,      // host -> client: host mod switch changed
        DetectionTier = 4,  // host -> client: detection status line tier
        TrackBatch = 5,     // host -> client: faction track upserts and removals
        TrackReset = 6,     // host -> client: drop all faction tracks (faction change)
        Feed = 7,           // host -> client: RWR threats and datalink threats of the client aircraft
        MissileState = 8,   // host -> client: dispersion, lock state and aim point of own missiles
        Forget = 9,         // host -> client: units the faction forgot (ADR-15)
        JamBeams = 10,      // host -> client: active jamming pod beams
        Count = 11
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
    }

    /// <summary>
    /// Multiplayer transport and roles. The host is authoritative for all sensor knowledge; it sends each client with
    /// the mod (hello) only the data of that client: its faction tracks, its RWR and datalink picture, its own missiles.
    /// Peers without the mod never get a mod message (Mirage charges an unknown message as a player error).
    /// The serializer of CnoEvilPacket is set by hand because the Mirage weaver does not process this assembly.
    /// </summary>
    public static class CnoEvilNet
    {
        public const ushort Protocol = 4;

        // Payload limit of one packet: below the smallest transport MTU (Steam 1198 bytes) with headers
        public const int MaxPacketBytes = 1100;

        private const float WelcomeTimeoutSeconds = 8.0f;
        private const float PeerRefreshSeconds = 1.0f;
        private const float HelloGraceSeconds = 10.0f;

        private static bool s_typesRegistered;
        private static bool s_disabled;

        private static readonly NetHandler[] s_serverHandlers = new NetHandler[(int)NetKind.Count];
        private static readonly NetHandler[] s_clientHandlers = new NetHandler[(int)NetKind.Count];

        // Role
        private static NetRole s_role = NetRole.Authority;
        private static bool s_modAllowed = true;

        // Server
        private static MessageHandler s_serverHandler;
        private static NetworkServer s_server;
        private static readonly List<NetPeer> s_peers = new List<NetPeer>();
        private static readonly List<INetworkPlayer> s_sendList = new List<INetworkPlayer>();
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

        public static bool IsServerActive
        {
            get { return s_server != null && s_server.Active; }
        }

        public static void RegisterServerHandler(NetKind kind, NetHandler handler)
        {
            s_serverHandlers[(int)kind] = handler;
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
                if (player == null || !player.IsConnected) return;
                CnoEvilPacket packet;
                packet.Data = w.ToArraySegment();
                player.Send(packet, channel);
                PerfStats.CountSent(packet.Data.Count);
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
                if (s_client == null || !s_client.IsConnected) return;
                CnoEvilPacket packet;
                packet.Data = w.ToArraySegment();
                s_client.Send(packet, channel);
                PerfStats.CountSent(packet.Data.Count);
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
                    s_clientHandler = null;
                    s_client = null;
                    ResetClient();
                }

                NetRole role;
                if (!remoteClient) role = NetRole.Authority;
                else if (s_welcomed) role = NetRole.Mirror;
                else if (s_disabled || (s_helloSent && Time.unscaledTime - s_helloTime > WelcomeTimeoutSeconds)) role = NetRole.Inert;
                else role = NetRole.Pending;
                SetRole(role);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("CnoEvilNet.Update", ex);
            }
        }

        private static void SetRole(NetRole role)
        {
            if (role != s_role && CnoEvilPlugin.Log != null)
            {
                CnoEvilPlugin.Log.LogInfo(string.Format("[CNO-EVIL] Network role: {0}", role));
                if (role == NetRole.Inert && !s_inertLogged)
                {
                    s_inertLogged = true;
                    CnoEvilPlugin.Log.LogWarning("[CNO-EVIL] The host does not run CNO-EVIL protocol " + Protocol +
                                                 ". All players must use the same CNO-EVIL version. The mod is off on this computer for this session.");
                }
            }
            s_role = role;
            s_modAllowed = role == NetRole.Authority || (role == NetRole.Mirror && s_hostModActive);
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
            }

            float now = Time.unscaledTime;
            if (now >= s_nextPeerRefresh)
            {
                s_nextPeerRefresh = now + PeerRefreshSeconds;
                RefreshPeers();
            }

            // The clients follow the host mod switch (F10), so all computers run the same rules
            bool hostActive = CnoEvilConfig.ModEnabled == null || CnoEvilConfig.ModEnabled.Value;
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

        private static void RefreshPeers()
        {
            NetworkManagerNuclearOption nm = NetworkManagerNuclearOption.i;
            List<Player> gamePlayers = (nm != null) ? nm.GamePlayers : null;
            for (int i = s_peers.Count - 1; i >= 0; i--)
            {
                NetPeer peer = s_peers[i];
                if (peer.Connection == null || !peer.Connection.IsConnected)
                {
                    s_peers.RemoveAt(i);
                    continue;
                }

                Player found = null;
                if (gamePlayers != null)
                {
                    for (int p = 0; p < gamePlayers.Count; p++)
                    {
                        Player player = gamePlayers[p];
                        if (player != null && player.Owner == peer.Connection)
                        {
                            found = player;
                            break;
                        }
                    }
                }
                peer.Player = found;
                Aircraft aircraft = (found != null) ? found.Aircraft : null;
                peer.Aircraft = (aircraft != null && !aircraft.disabled) ? aircraft : null;
                peer.Faction = (found != null) ? found.HQ : null;
            }

            WarnPlayersWithoutMod(gamePlayers);
        }

        /// <summary>
        /// The mod must run on all computers. A remote player without hello after HelloGraceSeconds gets one log warning;
        /// that player keeps the vanilla RPCs only.
        /// </summary>
        private static void WarnPlayersWithoutMod(List<Player> gamePlayers)
        {
            if (gamePlayers == null) return;
            float now = Time.unscaledTime;
            for (int p = 0; p < gamePlayers.Count; p++)
            {
                Player player = gamePlayers[p];
                INetworkPlayer conn = (player != null) ? player.Owner : null;
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
            long perf = PerfStats.Begin();
            try
            {
                if (msg.Data.Array == null || msg.Data.Count < 1) return;
                PerfStats.CountReceived(msg.Data.Count);
                r = NetworkReaderPool.GetReader(msg.Data, null);
                byte kind = r.ReadByte();
                if (kind == (byte)NetKind.Hello)
                {
                    OnHello(player, r);
                    return;
                }
                NetPeer peer = FindPeerByConnection(player);
                if (peer == null || kind >= (byte)NetKind.Count) return;
                NetHandler handler = s_serverHandlers[kind];
                if (handler != null) handler(player, r);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("CnoEvilNet.OnServerPacket", ex);
            }
            finally
            {
                if (r != null) r.Release();
                PerfStats.End(PerfSlot.NetServer, perf);
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
                CnoEvilPlugin.Log.LogWarning(string.Format("[CNO-EVIL] A client uses mod protocol {0}, the host uses {1}. That client runs without the mod.", protocol, Protocol));
                return;
            }

            NetPeer peer = FindPeerByConnection(player);
            if (peer == null)
            {
                peer = new NetPeer();
                peer.Connection = player;
                s_peers.Add(peer);
            }
            s_nextPeerRefresh = 0.0f;

            PooledNetworkWriter w = Begin(NetKind.Welcome);
            w.WriteUInt16(Protocol);
            w.WriteBooleanExtension(CnoEvilConfig.ModEnabled == null || CnoEvilConfig.ModEnabled.Value);
            Send(player, w, Channel.Reliable);
            CnoEvilPlugin.Log.LogInfo("[CNO-EVIL] A client with the mod joined.");
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
            }

            if (!s_helloSent && client.Player != null && client.Player.IsAuthenticated)
            {
                PooledNetworkWriter w = Begin(NetKind.Hello);
                w.WriteUInt16(Protocol);
                SendToServer(w, Channel.Reliable);
                s_helloSent = true;
                s_helloTime = Time.unscaledTime;
            }
        }

        private static void OnClientPacket(INetworkPlayer player, CnoEvilPacket msg)
        {
            PooledNetworkReader r = null;
            long perf = PerfStats.Begin();
            try
            {
                if (msg.Data.Array == null || msg.Data.Count < 1) return;
                PerfStats.CountReceived(msg.Data.Count);
                r = NetworkReaderPool.GetReader(msg.Data, null);
                byte kind = r.ReadByte();
                if (kind == (byte)NetKind.Welcome)
                {
                    ushort protocol = r.ReadUInt16();
                    if (protocol != Protocol) return;
                    s_hostModActive = r.ReadBooleanExtension();
                    s_welcomed = true;
                    SetRole(NetRole.Mirror);
                    if (ClientWelcomed != null) ClientWelcomed();
                    CnoEvilPlugin.Log.LogInfo("[CNO-EVIL] The host runs CNO-EVIL: host knowledge sync is on.");
                    return;
                }
                if (!s_welcomed || kind >= (byte)NetKind.Count) return;
                if (kind == (byte)NetKind.HostState)
                {
                    s_hostModActive = r.ReadBooleanExtension();
                    SetRole(NetRole.Mirror);
                    return;
                }
                NetHandler handler = s_clientHandlers[kind];
                if (handler != null) handler(player, r);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("CnoEvilNet.OnClientPacket", ex);
            }
            finally
            {
                if (r != null) r.Release();
                PerfStats.End(PerfSlot.NetClient, perf);
            }
        }

        private static void ResetClient()
        {
            s_helloSent = false;
            s_welcomed = false;
            s_hostModActive = false;
            s_helloTime = 0.0f;
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
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("CnoEvilNet.Shutdown", ex);
            }
            s_serverHandler = null;
            s_clientHandler = null;
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
        /// Local timestamp from a written age; 0 for "no timestamp".
        /// </summary>
        public static float ReadAge(NetworkReader r, float now)
        {
            ushort v = r.ReadUInt16();
            if (v == ushort.MaxValue) return 0.0f;
            return Mathf.Max(0.0001f, now - v * 0.01f);
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
