using System;
using System.Collections.Generic;
using UnityEngine;
using Mirage;
using Mirage.Serialization;
using CnoEvil.Config;

namespace CnoEvil.Core
{
    /// <summary>
    /// Sends the faction tracks of the host to each mirror client (NetKind.TrackBatch, reliable).
    /// The host keeps per client the state it sent last and sends a track only when it changed:
    /// flags, Q by QStep or more, a timestamp advanced by TimestampStepSeconds or more, a new fix, a fix center moved by
    /// CenterMoveMeters or more, or RefreshSeconds without a send. Removed tracks are sent as removals.
    /// Times are sent as ages (the host and client level clocks are different); positions as GlobalPosition.
    /// </summary>
    public static class TrackSync
    {
        private const float SendIntervalSeconds = 0.5f;
        private const float TimestampStepSeconds = 1.5f;
        private const float RefreshSeconds = 5.0f;
        private const float CenterMoveMeters = 25.0f;
        private const int QStep = 4;                // 4/255, about 1/64

        private const byte TagEnd = 0;
        private const byte TagUpsert = 1;
        private const byte TagRemove = 2;

        private const byte FlagTriangulated = 1;
        private const byte FlagHadActive = 2;
        private const byte FlagFixCenter = 4;

        // Reserve for one record when a packet is split
        private const int RecordReserveBytes = 96;

        private class SentTrack
        {
            public byte Flags;
            public byte Q;
            public float LastUpdate;
            public float LastFix;
            public float LastPrecise;
            public float LastRadar;
            public float VisualHold;
            public float LastActive;
            public float LastKnown;
            public float SensorRangeTime;
            public float Error;
            public float Uncertainty;
            public float Range;
            public GlobalPosition Center;
            public float SentAt;
            public int Stamp;
        }

        private class PeerState
        {
            public FactionHQ Faction;
            public bool NeedsReset = true;
            public readonly Dictionary<Unit, SentTrack> Sent = new Dictionary<Unit, SentTrack>();
        }

        private static readonly Dictionary<NetPeer, PeerState> s_peerStates = new Dictionary<NetPeer, PeerState>();
        private static readonly List<NetPeer> s_deadPeers = new List<NetPeer>();
        private static readonly List<Unit> s_removed = new List<Unit>();
        private static readonly List<SentTrack> s_pool = new List<SentTrack>();
        private static float s_nextSend;
        private static int s_stamp;
        private static bool s_handlersRegistered;

        public static void Update()
        {
            try
            {
                if (!s_handlersRegistered)
                {
                    CnoEvilNet.RegisterClientHandler(NetKind.TrackBatch, OnTrackBatch);
                    CnoEvilNet.RegisterClientHandler(NetKind.TrackReset, OnTrackReset);
                    CnoEvilNet.ClientWelcomed += RWRTriangulationProcessor.ResetRemoteTracks;
                    s_handlersRegistered = true;
                }

                if (!CnoEvilNet.IsServerActive || !CnoEvilConfig.IsModActive)
                {
                    if (s_peerStates.Count > 0) ClearAll();
                    return;
                }

                float now = Time.unscaledTime;
                if (now < s_nextSend) return;
                s_nextSend = now + SendIntervalSeconds;

                long perf = PerfStats.Begin();
                SendAll();
                PerfStats.End(PerfSlot.NetServer, perf);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("TrackSync.Update", ex);
            }
        }

        private static void SendAll()
        {
            List<NetPeer> peers = CnoEvilNet.Peers;

            // Drop the state of peers that left
            s_deadPeers.Clear();
            foreach (KeyValuePair<NetPeer, PeerState> kv in s_peerStates)
            {
                if (!peers.Contains(kv.Key)) s_deadPeers.Add(kv.Key);
            }
            for (int i = 0; i < s_deadPeers.Count; i++)
            {
                ReleaseAll(s_peerStates[s_deadPeers[i]]);
                s_peerStates.Remove(s_deadPeers[i]);
            }

            Dictionary<FactionHQ, Dictionary<Unit, TriangulationTrack>> all = RWRTriangulationProcessor.GetAllFactionTracks();
            for (int i = 0; i < peers.Count; i++)
            {
                NetPeer peer = peers[i];
                if (peer.Connection == null) continue;
                PeerState state;
                if (!s_peerStates.TryGetValue(peer, out state))
                {
                    state = new PeerState();
                    s_peerStates[peer] = state;
                }

                if (state.Faction != peer.Faction)
                {
                    state.Faction = peer.Faction;
                    state.NeedsReset = true;
                }
                if (state.NeedsReset)
                {
                    ReleaseAll(state);
                    state.NeedsReset = false;
                    PooledNetworkWriter reset = CnoEvilNet.Begin(NetKind.TrackReset);
                    CnoEvilNet.Send(peer.Connection, reset, Channel.Reliable);
                }
                if (state.Faction == null) continue;

                Dictionary<Unit, TriangulationTrack> tracks;
                all.TryGetValue(state.Faction, out tracks);
                SendPeer(peer, state, tracks);
            }
        }

        private static void SendPeer(NetPeer peer, PeerState state, Dictionary<Unit, TriangulationTrack> tracks)
        {
            float now = Time.timeSinceLevelLoad;
            s_stamp++;
            PooledNetworkWriter w = null;
            int records = 0;

            if (tracks != null)
            {
                foreach (KeyValuePair<Unit, TriangulationTrack> kv in tracks)
                {
                    Unit unit = kv.Key;
                    TriangulationTrack track = kv.Value;
                    if (unit == null || unit.disabled || track == null || unit.persistentID.Id == 0u) continue;

                    SentTrack sent;
                    bool known = state.Sent.TryGetValue(unit, out sent);
                    if (!known)
                    {
                        sent = RentSent();
                        state.Sent[unit] = sent;
                    }
                    sent.Stamp = s_stamp;

                    byte flags = 0;
                    if (track.IsTriangulated) flags |= FlagTriangulated;
                    if (track.HadActiveContact) flags |= FlagHadActive;
                    if (track.IsTriangulated && !track.HasRecentPreciseContact) flags |= FlagFixCenter;
                    byte q = (byte)Mathf.RoundToInt(Mathf.Clamp01(track.TrackingQuality) * 255.0f);
                    GlobalPosition center = GlobalPositionExtensions.ToGlobalPosition(track.EstimatedCenterWorldPos);

                    if (known && !IsDirty(sent, track, flags, q, center, now)) continue;

                    if (w == null) w = CnoEvilNet.Begin(NetKind.TrackBatch);
                    WriteRecord(w, unit, track, flags, q, center, now);
                    records++;

                    sent.Flags = flags;
                    sent.Q = q;
                    sent.LastUpdate = track.LastUpdateTime;
                    sent.LastFix = track.LastFixTime;
                    sent.LastPrecise = track.LastPreciseContactTime;
                    sent.LastRadar = track.LastRadarIlluminatedTime;
                    sent.VisualHold = track.VisualHoldUntil;
                    sent.LastActive = track.LastActiveDetectionTime;
                    sent.LastKnown = track.LastKnownTime;
                    sent.SensorRangeTime = track.SensorRangeTime;
                    sent.Error = track.TriangulationErrorMeters;
                    sent.Uncertainty = track.UncertaintyRadiusMeters;
                    sent.Range = track.SensorRangeMeters;
                    sent.Center = center;
                    sent.SentAt = now;

                    if (w.ByteLength > CnoEvilNet.MaxPacketBytes - RecordReserveBytes)
                    {
                        w.WriteByte(TagEnd);
                        CnoEvilNet.Send(peer.Connection, w, Channel.Reliable);
                        w = null;
                    }
                }
            }

            // Removals: tracks the faction no longer has
            s_removed.Clear();
            foreach (KeyValuePair<Unit, SentTrack> kv in state.Sent)
            {
                if (kv.Value.Stamp != s_stamp) s_removed.Add(kv.Key);
            }
            for (int i = 0; i < s_removed.Count; i++)
            {
                Unit unit = s_removed[i];
                SentTrack sent = state.Sent[unit];
                state.Sent.Remove(unit);
                s_pool.Add(sent);
                if ((object)unit == null) continue;
                // A destroyed unit is removed on the client by its own update; its id may already be gone
                uint id = (unit != null) ? unit.persistentID.Id : 0u;
                if (id == 0u) continue;
                if (w == null) w = CnoEvilNet.Begin(NetKind.TrackBatch);
                w.WriteByte(TagRemove);
                w.WritePackedUInt32(id);
                records++;
                if (w.ByteLength > CnoEvilNet.MaxPacketBytes - RecordReserveBytes)
                {
                    w.WriteByte(TagEnd);
                    CnoEvilNet.Send(peer.Connection, w, Channel.Reliable);
                    w = null;
                }
            }

            if (w != null)
            {
                w.WriteByte(TagEnd);
                CnoEvilNet.Send(peer.Connection, w, Channel.Reliable);
            }
        }

        private static bool IsDirty(SentTrack sent, TriangulationTrack track, byte flags, byte q, GlobalPosition center, float now)
        {
            if (flags != sent.Flags) return true;
            if (Mathf.Abs(q - sent.Q) >= QStep || (q != sent.Q && (q == 0 || q == 255))) return true;
            if (track.LastFixTime != sent.LastFix) return true;
            if (TimeAdvanced(track.LastUpdateTime, sent.LastUpdate)) return true;
            if (TimeAdvanced(track.LastPreciseContactTime, sent.LastPrecise)) return true;
            if (TimeAdvanced(track.LastRadarIlluminatedTime, sent.LastRadar)) return true;
            if (TimeAdvanced(track.VisualHoldUntil, sent.VisualHold)) return true;
            if (TimeAdvanced(track.LastActiveDetectionTime, sent.LastActive)) return true;
            if (TimeAdvanced(track.LastKnownTime, sent.LastKnown)) return true;
            if (TimeAdvanced(track.SensorRangeTime, sent.SensorRangeTime)) return true;
            if (Changed(track.TriangulationErrorMeters, sent.Error)) return true;
            if (Changed(track.UncertaintyRadiusMeters, sent.Uncertainty)) return true;
            if (Changed(track.SensorRangeMeters, sent.Range)) return true;
            if ((flags & FlagFixCenter) != 0 && (center - sent.Center).sqrMagnitude > CenterMoveMeters * CenterMoveMeters) return true;
            return now - sent.SentAt > RefreshSeconds;
        }

        private static bool TimeAdvanced(float value, float sent)
        {
            if (value <= 0.0f) return sent > 0.0f;
            if (sent <= 0.0f) return true;
            return value - sent >= TimestampStepSeconds || value < sent;
        }

        private static bool Changed(float value, float sent)
        {
            return Mathf.Abs(value - sent) > Mathf.Max(10.0f, Mathf.Abs(sent) * 0.1f);
        }

        private static void WriteRecord(NetworkWriter w, Unit unit, TriangulationTrack track, byte flags, byte q, GlobalPosition center, float now)
        {
            w.WriteByte(TagUpsert);
            w.WritePackedUInt32(unit.persistentID.Id);
            w.WriteByte(flags);
            w.WriteByte(q);
            CnoEvilNet.WriteAge(w, now, track.LastUpdateTime);
            CnoEvilNet.WriteAge(w, now, track.LastFixTime);
            CnoEvilNet.WriteAge(w, now, track.LastPreciseContactTime);
            CnoEvilNet.WriteAge(w, now, track.LastRadarIlluminatedTime);
            CnoEvilNet.WriteAge(w, now, track.LastActiveDetectionTime);
            CnoEvilNet.WriteAge(w, now, track.LastKnownTime);
            CnoEvilNet.WriteAge(w, now, track.SensorRangeTime);
            CnoEvilNet.WriteAge(w, now, track.LastPreciseVelocityTime);
            CnoEvilNet.WriteRemaining(w, now, track.VisualHoldUntil);
            CnoEvilNet.WriteMeters(w, track.TriangulationErrorMeters);
            CnoEvilNet.WriteMeters(w, track.UncertaintyRadiusMeters);
            CnoEvilNet.WriteMeters(w, track.SensorRangeMeters);
            if ((flags & FlagFixCenter) != 0) CnoEvilNet.WriteGlobal(w, center);
            if (track.LastKnownTime > 0.0f)
            {
                CnoEvilNet.WriteGlobal(w, track.LastKnownGlobalPosition);
                CnoEvilNet.WriteVelocity(w, track.LastKnownVelocity);
            }
            if (track.LastPreciseVelocityTime > 0.0f) CnoEvilNet.WriteVelocity(w, track.LastPreciseVelocity);
        }

        // ---------------------------------------------------------------- client

        private static void OnTrackBatch(INetworkPlayer sender, NetworkReader r)
        {
            float now = Time.timeSinceLevelLoad;
            while (true)
            {
                byte tag = r.ReadByte();
                if (tag == TagEnd) return;
                if (tag == TagRemove)
                {
                    Unit removed = CnoEvilNet.ReadUnit(r);
                    if (removed != null) RWRTriangulationProcessor.RemoveRemoteTrack(removed);
                    continue;
                }
                if (tag != TagUpsert) return;

                Unit unit = CnoEvilNet.ReadUnit(r);
                RemoteTrackData d = default(RemoteTrackData);
                byte flags = r.ReadByte();
                d.IsTriangulated = (flags & FlagTriangulated) != 0;
                d.HadActiveContact = (flags & FlagHadActive) != 0;
                d.HasFixCenter = (flags & FlagFixCenter) != 0;
                d.TrackingQuality = r.ReadByte() / 255.0f;
                d.LastUpdateTime = CnoEvilNet.ReadAge(r, now);
                d.LastFixTime = CnoEvilNet.ReadAge(r, now);
                d.LastPreciseContactTime = CnoEvilNet.ReadAge(r, now);
                d.LastRadarIlluminatedTime = CnoEvilNet.ReadAge(r, now);
                d.LastActiveDetectionTime = CnoEvilNet.ReadAge(r, now);
                d.LastKnownTime = CnoEvilNet.ReadAge(r, now);
                d.SensorRangeTime = CnoEvilNet.ReadAge(r, now);
                d.LastPreciseVelocityTime = CnoEvilNet.ReadAge(r, now);
                d.VisualHoldUntil = CnoEvilNet.ReadRemaining(r, now);
                d.TriangulationErrorMeters = CnoEvilNet.ReadMeters(r);
                d.UncertaintyRadiusMeters = CnoEvilNet.ReadMeters(r);
                d.SensorRangeMeters = CnoEvilNet.ReadMeters(r);
                if (d.HasFixCenter) d.EstimatedCenter = CnoEvilNet.ReadGlobal(r);
                if (d.LastKnownTime > 0.0f)
                {
                    d.LastKnownGlobalPosition = CnoEvilNet.ReadGlobal(r);
                    d.LastKnownVelocity = CnoEvilNet.ReadVelocity(r);
                }
                if (d.LastPreciseVelocityTime > 0.0f) d.LastPreciseVelocity = CnoEvilNet.ReadVelocity(r);

                if (unit != null) RWRTriangulationProcessor.ApplyRemoteTrack(unit, ref d);
            }
        }

        private static void OnTrackReset(INetworkPlayer sender, NetworkReader r)
        {
            RWRTriangulationProcessor.ResetRemoteTracks();
        }

        // ---------------------------------------------------------------- state

        private static SentTrack RentSent()
        {
            if (s_pool.Count == 0) return new SentTrack();
            SentTrack s = s_pool[s_pool.Count - 1];
            s_pool.RemoveAt(s_pool.Count - 1);
            return s;
        }

        private static void ReleaseAll(PeerState state)
        {
            foreach (KeyValuePair<Unit, SentTrack> kv in state.Sent) s_pool.Add(kv.Value);
            state.Sent.Clear();
        }

        /// <summary>
        /// Scene change: every client gets a reset and all tracks again.
        /// </summary>
        public static void ClearAll()
        {
            foreach (KeyValuePair<NetPeer, PeerState> kv in s_peerStates) ReleaseAll(kv.Value);
            s_peerStates.Clear();
            s_deadPeers.Clear();
            s_removed.Clear();
            s_nextSend = 0.0f;
        }

        public static void Shutdown()
        {
            ClearAll();
            s_pool.Clear();
            if (s_handlersRegistered)
            {
                CnoEvilNet.RegisterClientHandler(NetKind.TrackBatch, null);
                CnoEvilNet.RegisterClientHandler(NetKind.TrackReset, null);
                CnoEvilNet.ClientWelcomed -= RWRTriangulationProcessor.ResetRemoteTracks;
                s_handlersRegistered = false;
            }
        }
    }
}
