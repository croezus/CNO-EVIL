using System;
using System.Collections.Generic;
using UnityEngine;
using Mirage;
using Mirage.Serialization;
using CnoEvil.Config;
using CnoEvil.Components;
using CnoEvil.Patches;

namespace CnoEvil.Core
{
    /// <summary>
    /// Sends each mirror client the RWR picture of its aircraft (NetKind.Feed, unreliable, SendIntervalSeconds):
    /// threat pings, datalink relays and below-threshold illuminations (EM scan ring dips).
    /// The host runs every radar and seeker, so only its PhysRWRReceiver of that aircraft has the pings with the
    /// physical state and signal (SARH guidance = MissileGuidance). A packet carries the pings and datalink relays of
    /// the last RepeatWindowSeconds, so one lost packet does not lose a ping. The client stores them without the
    /// track processor (the faction track comes from TrackSync) and runs the vanilla warning RPC body locally.
    /// </summary>
    public static class RwrFeedSync
    {
        private const float SendIntervalSeconds = 0.2f;
        private const float RepeatWindowSeconds = 0.4f;
        private const int MaxThreats = 32;
        private const int MaxDatalinkThreats = 20;
        private const int MaxBelowThreshold = 8;   // Worst case of all three blocks stays below MaxPacketBytes
        private const byte FlagSeeker = 1;
        private const byte FlagMissile = 1;

        private static float s_nextSend;
        private static bool s_handlersRegistered;
        private static readonly List<RwrThreatEntry> s_threatBuffer = new List<RwrThreatEntry>();
        private static readonly List<DatalinkThreatEntry> s_datalinkBuffer = new List<DatalinkThreatEntry>();
        private static readonly List<RwrThreatEntry> s_belowBuffer = new List<RwrThreatEntry>();

        public static void Update()
        {
            try
            {
                if (!s_handlersRegistered)
                {
                    CnoEvilNet.RegisterClientHandler(NetKind.Feed, OnFeed);
                    s_handlersRegistered = true;
                }
                if (!CnoEvilNet.IsServerActive || !CnoEvilConfig.IsModActive) return;

                float unscaled = Time.unscaledTime;
                if (unscaled < s_nextSend) return;
                s_nextSend = unscaled + SendIntervalSeconds;

                List<NetPeer> peers = CnoEvilNet.Peers;
                if (peers.Count == 0) return;
                long perf = PerfStats.Begin();
                for (int i = 0; i < peers.Count; i++)
                {
                    NetPeer peer = peers[i];
                    if (peer.Connection == null || peer.Aircraft == null || peer.Aircraft.disabled) continue;
                    PhysRWRReceiver rwr = PhysRWRReceiver.Get(peer.Aircraft);
                    if (rwr != null) SendPeer(peer, rwr);
                }
                PerfStats.End(PerfSlot.NetServer, perf);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("RwrFeedSync.Update", ex);
            }
        }

        private static void SendPeer(NetPeer peer, PhysRWRReceiver rwr)
        {
            float now = Time.time;
            float since = now - RepeatWindowSeconds;

            s_threatBuffer.Clear();
            foreach (RwrThreatEntry e in rwr.ActiveThreats)
            {
                if (e == null || e.EmitterUnit == null || e.LastPingTime < since || e.EmitterUnit.persistentID.Id == 0u) continue;
                s_threatBuffer.Add(e);
                if (s_threatBuffer.Count >= MaxThreats) break;
            }
            s_datalinkBuffer.Clear();
            foreach (DatalinkThreatEntry d in rwr.ActiveDatalinkThreats)
            {
                if (d == null || d.ThreatUnit == null || d.LastPingTime < since || d.ThreatUnit.persistentID.Id == 0u) continue;
                s_datalinkBuffer.Add(d);
                if (s_datalinkBuffer.Count >= MaxDatalinkThreats) break;
            }
            s_belowBuffer.Clear();
            foreach (RwrThreatEntry e in rwr.BelowThresholdPings)
            {
                if (e == null || e.EmitterUnit == null || e.LastPingTime < since || e.EmitterUnit.persistentID.Id == 0u) continue;
                s_belowBuffer.Add(e);
                if (s_belowBuffer.Count >= MaxBelowThreshold) break;
            }
            if (s_threatBuffer.Count == 0 && s_datalinkBuffer.Count == 0 && s_belowBuffer.Count == 0) return;

            PooledNetworkWriter w = CnoEvilNet.Begin(NetKind.Feed);
            CnoEvilNet.WriteUnit(w, peer.Aircraft);

            w.WriteByte((byte)s_threatBuffer.Count);
            for (int i = 0; i < s_threatBuffer.Count; i++)
            {
                RwrThreatEntry e = s_threatBuffer[i];
                CnoEvilNet.WriteUnit(w, e.EmitterUnit);
                w.WriteByte((byte)e.State);
                w.WriteByte((byte)Mathf.RoundToInt(Mathf.Clamp01(e.SignalStrength01) * 255.0f));
                w.WriteByte(e.IsActiveSeeker ? FlagSeeker : (byte)0);
                WriteDirection(w, e.LastDirection);
                WriteShortAge(w, now - e.LastPingTime);
            }

            w.WriteByte((byte)s_datalinkBuffer.Count);
            for (int i = 0; i < s_datalinkBuffer.Count; i++)
            {
                DatalinkThreatEntry d = s_datalinkBuffer[i];
                CnoEvilNet.WriteUnit(w, d.SourceAlliedUnit);
                CnoEvilNet.WriteUnit(w, d.ThreatUnit);
                CnoEvilNet.WriteGlobal(w, GlobalPositionExtensions.ToGlobalPosition(d.EstimatedPosition));
                CnoEvilNet.WriteVelocity(w, d.EstimatedVelocity);
                w.WriteByte(d.IsMissile ? FlagMissile : (byte)0);
                WriteShortAge(w, now - d.LastPingTime);
            }

            // Below-threshold illuminations (EM scan ring dips); the signal byte is the dip depth
            w.WriteByte((byte)s_belowBuffer.Count);
            for (int i = 0; i < s_belowBuffer.Count; i++)
            {
                RwrThreatEntry e = s_belowBuffer[i];
                CnoEvilNet.WriteUnit(w, e.EmitterUnit);
                w.WriteByte((byte)e.State);
                w.WriteByte((byte)Mathf.RoundToInt(Mathf.Clamp01(e.SignalStrength01) * 255.0f));
                w.WriteByte(e.IsActiveSeeker ? FlagSeeker : (byte)0);
                WriteDirection(w, e.LastDirection);
                WriteShortAge(w, now - e.LastPingTime);
            }

            CnoEvilNet.Send(peer.Connection, w, Channel.Unreliable);
        }

        // World direction: yaw 0..360 deg as ushort, pitch -90..90 deg as sbyte
        private static void WriteDirection(NetworkWriter w, Vector3 dir)
        {
            if (dir.sqrMagnitude < 1e-6f) dir = Vector3.forward;
            dir.Normalize();
            float yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            if (yaw < 0.0f) yaw += 360.0f;
            float pitch = Mathf.Asin(Mathf.Clamp(dir.y, -1.0f, 1.0f)) * Mathf.Rad2Deg;
            w.WriteUInt16((ushort)(Mathf.RoundToInt(yaw / 360.0f * 65536.0f) & 0xFFFF));
            w.WriteByte((byte)(sbyte)Mathf.Clamp(Mathf.RoundToInt(pitch * 127.0f / 90.0f), -127, 127));
        }

        private static Vector3 ReadDirection(NetworkReader r)
        {
            float yaw = r.ReadUInt16() / 65536.0f * 360.0f * Mathf.Deg2Rad;
            float pitch = (sbyte)r.ReadByte() * 90.0f / 127.0f * Mathf.Deg2Rad;
            float c = Mathf.Cos(pitch);
            return new Vector3(Mathf.Sin(yaw) * c, Mathf.Sin(pitch), Mathf.Cos(yaw) * c);
        }

        // Age in 20 ms steps (max 5.1 s)
        private static void WriteShortAge(NetworkWriter w, float age)
        {
            w.WriteByte((byte)Mathf.Clamp(Mathf.RoundToInt(age * 50.0f), 0, 255));
        }

        private static float ReadShortAge(NetworkReader r)
        {
            return r.ReadByte() * 0.02f;
        }

        // ---------------------------------------------------------------- client

        private static void OnFeed(INetworkPlayer sender, NetworkReader r)
        {
            float now = Time.time;
            Aircraft aircraft = CnoEvilNet.ReadUnit(r) as Aircraft;
            Aircraft local = (CombatHUD.i != null) ? CombatHUD.i.aircraft : null;
            // The aircraft changed while the packet was on the way: drop it
            if (aircraft == null || aircraft != local || aircraft.disabled) return;
            PhysRWRReceiver rwr = PhysRWRReceiver.Get(aircraft);
            if (rwr == null) return;
            bool modActive = CnoEvilConfig.IsModActive;

            int threats = r.ReadByte();
            for (int i = 0; i < threats; i++)
            {
                Unit emitter = CnoEvilNet.ReadUnit(r);
                RwrThreatState state = (RwrThreatState)Mathf.Min(r.ReadByte(), (int)RwrThreatState.MissileGuidance);
                float signal = r.ReadByte() / 255.0f;
                bool seeker = (r.ReadByte() & FlagSeeker) != 0;
                Vector3 dir = ReadDirection(r);
                float pingTime = now - ReadShortAge(r);
                if (emitter == null || emitter.disabled || !modActive) continue;
                if (rwr.ApplyRemoteThreat(emitter, state, signal, seeker, dir, pingTime))
                {
                    SeekerDispersionHelper.OnRemotePing(aircraft, emitter);
                }
            }

            int datalink = r.ReadByte();
            for (int i = 0; i < datalink; i++)
            {
                Unit source = CnoEvilNet.ReadUnit(r);
                Unit threat = CnoEvilNet.ReadUnit(r);
                GlobalPosition pos = CnoEvilNet.ReadGlobal(r);
                Vector3 vel = CnoEvilNet.ReadVelocity(r);
                bool isMissile = (r.ReadByte() & FlagMissile) != 0;
                float pingTime = now - ReadShortAge(r);
                if (threat == null || threat.disabled || !modActive) continue;
                rwr.ApplyRemoteDatalinkThreat(source, threat, pos.ToLocalPosition(), vel, isMissile, pingTime);
            }

            int below = r.ReadByte();
            for (int i = 0; i < below; i++)
            {
                Unit emitter = CnoEvilNet.ReadUnit(r);
                RwrThreatState state = (RwrThreatState)Mathf.Min(r.ReadByte(), (int)RwrThreatState.MissileGuidance);
                float depth = r.ReadByte() / 255.0f;
                bool seeker = (r.ReadByte() & FlagSeeker) != 0;
                Vector3 dir = ReadDirection(r);
                float pingTime = now - ReadShortAge(r);
                if (emitter == null || emitter.disabled || !modActive) continue;
                rwr.ApplyRemoteBelowThreshold(emitter, state, depth, seeker, dir, pingTime);
            }
        }

        public static void ClearAll()
        {
            s_nextSend = 0.0f;
            s_threatBuffer.Clear();
            s_datalinkBuffer.Clear();
            s_belowBuffer.Clear();
        }

        public static void Shutdown()
        {
            ClearAll();
            if (s_handlersRegistered)
            {
                CnoEvilNet.RegisterClientHandler(NetKind.Feed, null);
                s_handlersRegistered = false;
            }
        }
    }
}
