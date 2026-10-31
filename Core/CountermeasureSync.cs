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
    /// Countermeasure loadout in multiplayer. Vanilla does not synchronize countermeasure ammo: each computer counts
    /// for itself. A mirror client sends its slider choice at FLY (NetKind.CmChoice, client -> host, before the vanilla
    /// spawn request on the same reliable connection). The host applies the choice of the player in Spawner.SpawnAircraft
    /// and sends the counts with the unit id to every client with the mod (NetKind.CmCounts); a client that does not have
    /// the unit yet keeps the counts until it appears. A client that joins or reloads its scene gets all counts again.
    /// </summary>
    public static class CountermeasureSync
    {
        private const float PendingTimeout_s = 30.0f;
        private const float PruneInterval_s = 5.0f;

        private struct Pending
        {
            public CmCounts Counts;
            public float Since;
        }

        private static readonly NetChannel s_channel = new NetChannel(NetKind.CmCounts, OnCounts, ResetClient);

        // Host
        private static readonly Dictionary<INetworkPlayer, CmCounts> s_playerChoices = new Dictionary<INetworkPlayer, CmCounts>();
        private static readonly Dictionary<uint, CmCounts> s_assigned = new Dictionary<uint, CmCounts>();
        // Client
        private static readonly Dictionary<uint, Pending> s_pending = new Dictionary<uint, Pending>();

        private static readonly List<uint> s_ids = new List<uint>();
        private static float s_nextPrune;

        /// <summary>
        /// Called every frame from the plugin after CnoEvilNet.Update.
        /// </summary>
        public static void Update()
        {
            try
            {
                s_channel.Register();
                if (s_pending.Count > 0) ApplyPending();

                float now = Time.unscaledTime;
                if (now < s_nextPrune) return;
                s_nextPrune = now + PruneInterval_s;
                if (!CnoEvilNet.IsServerActive)
                {
                    s_playerChoices.Clear();
                    s_assigned.Clear();
                    return;
                }
                PruneHost();
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("CountermeasureSync.Update", ex);
            }
        }

        // ---------------------------------------------------------------- local player

        /// <summary>
        /// FLY in the loadout menu: keep the choice, and on a mirror client send it to the host.
        /// </summary>
        public static void SubmitChoice(AircraftDefinition definition, CmCounts counts)
        {
            CountermeasureLoadout.SetChoice(definition, counts);
            CountermeasureLoadout.Save();
            if (CnoEvilNet.Role != NetRole.Mirror) return;

            PooledNetworkWriter w = CnoEvilNet.Begin(NetKind.CmChoice);
            WriteCounts(w, counts);
            CnoEvilNet.SendToServer(w, Channel.Reliable);
        }

        // ---------------------------------------------------------------- host

        /// <summary>
        /// Host: the slider choice of a client with the mod, for its next spawn.
        /// </summary>
        public static void OnServerChoice(NetPeer peer, NetworkReader r)
        {
            CmCounts counts = ReadCounts(r);
            if (peer == null || peer.Connection == null) return;
            s_playerChoices[peer.Connection] = counts.Clamped(CountermeasureLoadout.Budget);
        }

        /// <summary>
        /// Host: Spawner.SpawnAircraft made the aircraft of a player. The local player uses its saved choice, a remote
        /// player the choice it sent, else the defaults (already set in Aircraft.Awake).
        /// </summary>
        public static void OnAircraftSpawned(Player player, Aircraft aircraft)
        {
            if (player == null || aircraft == null || !CnoEvilConfig.IsModActive) return;
            if (!CountermeasureLoadout.HasFlares(aircraft)) return;

            INetworkPlayer owner = player.Owner;
            CmCounts counts;
            if (owner == null || owner.IsHost) counts = CountermeasureLoadout.GetChoice(aircraft.definition);
            else if (!s_playerChoices.TryGetValue(owner, out counts)) return;
            counts = counts.Clamped(CountermeasureLoadout.Budget);

            CountermeasureLoadout.Apply(aircraft, counts);
            uint id = aircraft.persistentID.Id;
            if (id == 0u) return;
            s_assigned[id] = counts;

            List<NetPeer> peers = CnoEvilNet.Peers;
            for (int i = 0; i < peers.Count; i++) SendCounts(peers[i].Connection, id, counts);
        }

        /// <summary>
        /// Host: a client joined or reloaded its scene; send all player counts again.
        /// </summary>
        public static void SendAllTo(INetworkPlayer player)
        {
            if (player == null) return;
            foreach (KeyValuePair<uint, CmCounts> kv in s_assigned) SendCounts(player, kv.Key, kv.Value);
        }

        private static void SendCounts(INetworkPlayer player, uint id, CmCounts counts)
        {
            if (player == null) return;
            PooledNetworkWriter w = CnoEvilNet.Begin(NetKind.CmCounts);
            w.WritePackedUInt32(id);
            WriteCounts(w, counts);
            CnoEvilNet.Send(player, w, Channel.Reliable);
        }

        private static void PruneHost()
        {
            s_ids.Clear();
            foreach (KeyValuePair<uint, CmCounts> kv in s_assigned)
            {
                if (FindAircraft(kv.Key) == null) s_ids.Add(kv.Key);
            }
            for (int i = 0; i < s_ids.Count; i++) s_assigned.Remove(s_ids[i]);

            if (s_playerChoices.Count == 0) return;
            List<INetworkPlayer> stale = null;
            foreach (KeyValuePair<INetworkPlayer, CmCounts> kv in s_playerChoices)
            {
                if (kv.Key != null && kv.Key.IsConnected) continue;
                if (stale == null) stale = new List<INetworkPlayer>();
                stale.Add(kv.Key);
            }
            if (stale != null) for (int i = 0; i < stale.Count; i++) s_playerChoices.Remove(stale[i]);
        }

        // ---------------------------------------------------------------- client

        private static void OnCounts(INetworkPlayer sender, NetworkReader r)
        {
            uint id = r.ReadPackedUInt32();
            CmCounts counts = ReadCounts(r);
            if (id == 0u) return;
            Aircraft aircraft = FindAircraft(id);
            if (aircraft != null)
            {
                if (CnoEvilConfig.IsModActive) CountermeasureLoadout.Apply(aircraft, counts);
                s_pending.Remove(id);
                return;
            }
            Pending p;
            p.Counts = counts;
            p.Since = Time.unscaledTime;
            s_pending[id] = p;
        }

        private static void ApplyPending()
        {
            s_ids.Clear();
            float now = Time.unscaledTime;
            foreach (KeyValuePair<uint, Pending> kv in s_pending)
            {
                Aircraft aircraft = FindAircraft(kv.Key);
                if (aircraft != null)
                {
                    if (CnoEvilConfig.IsModActive) CountermeasureLoadout.Apply(aircraft, kv.Value.Counts);
                    s_ids.Add(kv.Key);
                }
                else if (now - kv.Value.Since > PendingTimeout_s)
                {
                    s_ids.Add(kv.Key);
                }
            }
            for (int i = 0; i < s_ids.Count; i++) s_pending.Remove(s_ids[i]);
        }

        private static void ResetClient()
        {
            s_pending.Clear();
        }

        // ---------------------------------------------------------------- helpers

        private static Aircraft FindAircraft(uint id)
        {
            PersistentID pid = default(PersistentID);
            pid.Id = id;
            Unit unit;
            return UnitRegistry.TryGetUnit(pid, out unit) ? unit as Aircraft : null;
        }

        private static void WriteCounts(NetworkWriter w, CmCounts counts)
        {
            w.WriteUInt16((ushort)Mathf.Clamp(counts.Flares, 0, ushort.MaxValue));
            w.WriteUInt16((ushort)Mathf.Clamp(counts.Chaff, 0, ushort.MaxValue));
            w.WriteUInt16((ushort)Mathf.Clamp(counts.Decoys, 0, ushort.MaxValue));
        }

        private static CmCounts ReadCounts(NetworkReader r)
        {
            int flares = r.ReadUInt16();
            int chaff = r.ReadUInt16();
            int decoys = r.ReadUInt16();
            return new CmCounts(flares, chaff, decoys);
        }

        /// <summary>
        /// Scene change: the units are gone. The connections and the player choices stay.
        /// </summary>
        public static void ClearAll()
        {
            s_assigned.Clear();
            s_pending.Clear();
        }

        /// <summary>
        /// Plugin unload: drop all state.
        /// </summary>
        public static void Shutdown()
        {
            s_channel.Shutdown();
            s_playerChoices.Clear();
            s_assigned.Clear();
            s_pending.Clear();
        }
    }
}
