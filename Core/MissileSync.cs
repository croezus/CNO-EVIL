using System;
using System.Collections.Generic;
using UnityEngine;
using Mirage;
using Mirage.Serialization;
using CnoEvil.Config;
using CnoEvil.Patches;

namespace CnoEvil.Core
{
    /// <summary>
    /// Sends each mirror client the state of the missiles its player launched (NetKind.MissileState, unreliable,
    /// SendIntervalSeconds): launch dispersion, terminal seeker lock and aim point. Missiles fly and guide on the
    /// host only, so the seeker fields and the dispersion draw do not exist on the client. Every packet has the full
    /// state, so a lost packet costs nothing.
    /// </summary>
    public static class MissileSync
    {
        private const float SendIntervalSeconds = 0.25f;
        private const float StateTimeoutSeconds = 1.5f;
        private const int MaxMissiles = 32;

        private const byte FlagLocked = 1;
        private const byte FlagAim = 2;
        private const byte FlagDispersion = 4;

        private struct MirrorState
        {
            public bool Locked;
            public bool HasAim;
            public GlobalPosition Aim;
            public float Time;
        }

        private static readonly Dictionary<Missile, MirrorState> s_states = new Dictionary<Missile, MirrorState>();
        private static readonly List<Missile> s_stale = new List<Missile>();
        private static readonly List<Missile> s_sendBuffer = new List<Missile>();
        private static float s_nextSend;
        private static bool s_handlersRegistered;

        public static void Update()
        {
            try
            {
                if (!s_handlersRegistered)
                {
                    CnoEvilNet.RegisterClientHandler(NetKind.MissileState, OnMissileState);
                    s_handlersRegistered = true;
                }
                if (!CnoEvilNet.IsServerActive || !CnoEvilConfig.IsModActive) return;

                float unscaled = Time.unscaledTime;
                if (unscaled < s_nextSend) return;
                s_nextSend = unscaled + SendIntervalSeconds;

                List<NetPeer> peers = CnoEvilNet.Peers;
                if (peers.Count == 0) return;
                List<Missile> missiles = DatalinkNetwork.ActiveMissiles;
                if (missiles.Count == 0) return;

                long perf = PerfStats.Begin();
                for (int i = 0; i < peers.Count; i++)
                {
                    NetPeer peer = peers[i];
                    if (peer.Connection != null && peer.Player != null) SendPeer(peer, missiles);
                }
                PerfStats.End(PerfSlot.NetServer, perf);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("MissileSync.Update", ex);
            }
        }

        private static void SendPeer(NetPeer peer, List<Missile> missiles)
        {
            s_sendBuffer.Clear();
            for (int i = 0; i < missiles.Count && s_sendBuffer.Count < MaxMissiles; i++)
            {
                Missile m = missiles[i];
                if (m == null || m.disabled || m.persistentID.Id == 0u) continue;
                // The missile stays the player's after its launcher is lost
                Aircraft owner = m.owner as Aircraft;
                if (owner == null || owner.Player != peer.Player) continue;
                s_sendBuffer.Add(m);
            }
            if (s_sendBuffer.Count == 0) return;

            PooledNetworkWriter w = CnoEvilNet.Begin(NetKind.MissileState);
            w.WriteByte((byte)s_sendBuffer.Count);
            for (int i = 0; i < s_sendBuffer.Count; i++)
            {
                Missile m = s_sendBuffer[i];
                Vector3 disp;
                bool hasDisp = TrackUncertaintyCalculator.TryGetMissileDispersion(m, out disp);
                GlobalPosition steer;
                byte flags = 0;
                if (SeekerDispersionHelper.IsSeekerTerminalLocked(m)) flags |= FlagLocked;
                if (SeekerDispersionHelper.TryGetSteerPoint(m, out steer)) flags |= FlagAim;
                if (hasDisp) flags |= FlagDispersion;

                CnoEvilNet.WriteUnit(w, m);
                w.WriteByte(flags);
                if ((flags & FlagAim) != 0) CnoEvilNet.WriteGlobal(w, steer);
                if (hasDisp) WriteHorizontal(w, disp);
            }
            CnoEvilNet.Send(peer.Connection, w, Channel.Unreliable);
        }

        // Horizontal offset in meters as 2 x int16
        private static void WriteHorizontal(NetworkWriter w, Vector3 v)
        {
            w.WriteInt16((short)Mathf.Clamp(Mathf.RoundToInt(v.x), short.MinValue, short.MaxValue));
            w.WriteInt16((short)Mathf.Clamp(Mathf.RoundToInt(v.z), short.MinValue, short.MaxValue));
        }

        private static Vector3 ReadHorizontal(NetworkReader r)
        {
            float x = r.ReadInt16();
            float z = r.ReadInt16();
            return new Vector3(x, 0.0f, z);
        }

        // ---------------------------------------------------------------- client

        private static void OnMissileState(INetworkPlayer sender, NetworkReader r)
        {
            float now = Time.time;
            int count = r.ReadByte();
            for (int i = 0; i < count; i++)
            {
                Missile m = CnoEvilNet.ReadUnit(r) as Missile;
                byte flags = r.ReadByte();
                MirrorState state;
                state.Locked = (flags & FlagLocked) != 0;
                state.HasAim = (flags & FlagAim) != 0;
                state.Aim = state.HasAim ? CnoEvilNet.ReadGlobal(r) : default(GlobalPosition);
                state.Time = now;
                Vector3 disp = Vector3.zero;
                bool hasDisp = (flags & FlagDispersion) != 0;
                if (hasDisp) disp = ReadHorizontal(r);
                if (m == null || m.disabled) continue;
                s_states[m] = state;
                if (hasDisp) TrackUncertaintyCalculator.RegisterMissileDispersion(m, disp);
            }

            // Drop missiles that are gone
            if (s_states.Count == 0) return;
            s_stale.Clear();
            foreach (KeyValuePair<Missile, MirrorState> kv in s_states)
            {
                if (kv.Key == null || kv.Key.disabled || now - kv.Value.Time > StateTimeoutSeconds * 4.0f) s_stale.Add(kv.Key);
            }
            for (int i = 0; i < s_stale.Count; i++) s_states.Remove(s_stale[i]);
        }

        /// <summary>
        /// Mirror client: terminal seeker lock of an own missile, from the host.
        /// </summary>
        public static bool TryGetLocked(Missile missile, out bool locked)
        {
            locked = false;
            MirrorState state;
            if (missile == null || !s_states.TryGetValue(missile, out state) || Time.time - state.Time > StateTimeoutSeconds) return false;
            locked = state.Locked;
            return true;
        }

        /// <summary>
        /// Mirror client: aim point of an own missile, from the host.
        /// </summary>
        public static bool TryGetAim(Missile missile, out GlobalPosition aim)
        {
            aim = default(GlobalPosition);
            MirrorState state;
            if (missile == null || !s_states.TryGetValue(missile, out state) || !state.HasAim || Time.time - state.Time > StateTimeoutSeconds) return false;
            aim = state.Aim;
            return true;
        }

        public static void Forget(Missile missile)
        {
            if ((object)missile != null) s_states.Remove(missile);
        }

        public static void ClearAll()
        {
            s_states.Clear();
            s_stale.Clear();
            s_sendBuffer.Clear();
            s_nextSend = 0.0f;
        }

        public static void Shutdown()
        {
            ClearAll();
            if (s_handlersRegistered)
            {
                CnoEvilNet.RegisterClientHandler(NetKind.MissileState, null);
                s_handlersRegistered = false;
            }
        }
    }
}
