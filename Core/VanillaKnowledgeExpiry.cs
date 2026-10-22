using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Mirage;
using Mirage.Serialization;

namespace CnoEvil.Core
{
    /// <summary>
    /// Vanilla FactionHQ never forgets a unit (only on disable or faction change), so its trackingDatabase entry kept
    /// an old true position after the faction's own track expired. When a mod track expires and the vanilla entry is
    /// also older than the same memory window, the faction forgets the unit through the vanilla removal methods:
    /// FactionHQ.DeregisterTrackedUnit, and for the local player's faction the map icon and HUD marker, which cache the
    /// TrackingInfo object. The next real contact re-adds all three through vanilla RpcUpdateTrackingInfo.
    /// The mod only removes vanilla knowledge, it never adds to it (ADR-15).
    /// In multiplayer the host decides: it sends each forgotten unit to the mirror clients of that faction
    /// (NetKind.Forget, reliable), and the client forgets it through the same vanilla methods.
    /// </summary>
    public static class VanillaKnowledgeExpiry
    {
        private const float ProcessIntervalSeconds = 1.0f;

        private static readonly Dictionary<FactionHQ, HashSet<Unit>> s_pending = new Dictionary<FactionHQ, HashSet<Unit>>();
        private static readonly Stack<HashSet<Unit>> s_setPool = new Stack<HashSet<Unit>>();
        private static readonly List<Unit> s_doneBuffer = new List<Unit>();
        private static readonly List<FactionHQ> s_emptyHqBuffer = new List<FactionHQ>();
        private static float s_nextProcessTime;

        // Host: units forgotten in this Process pass, sent to the mirror clients of their faction
        private struct ForgetRecord
        {
            public FactionHQ Hq;
            public uint Id;
        }
        private static readonly List<ForgetRecord> s_outbox = new List<ForgetRecord>();
        private const int MaxIdsPerPacket = 200;
        private static bool s_handlerRegistered;

        private static bool s_reflectionReady;
        private static MethodInfo s_hqChangeFactionMethod;
        private static Func<DynamicMap, Dictionary<Unit, UnitMapIcon>> s_iconLookupGetter;
        private static Func<CombatHUD, Dictionary<Unit, HUDUnitMarker>> s_markerLookupGetter;
        private static Action<UnitMapIcon, Unit> s_mapIconOnUnitDisabled;
        private static Action<HUDUnitMarker, Unit> s_markerOnDisableUnit;

        /// <summary>
        /// Called when the faction's mod track on the unit expires. The vanilla entry is checked in Process.
        /// </summary>
        public static void MarkExpired(FactionHQ hq, Unit unit)
        {
            if (hq == null || unit == null || unit is Missile) return;
            if (unit.NetworkHQ != null && unit.NetworkHQ == hq) return;

            HashSet<Unit> set;
            if (!s_pending.TryGetValue(hq, out set))
            {
                set = s_setPool.Count > 0 ? s_setPool.Pop() : new HashSet<Unit>();
                s_pending[hq] = set;
            }
            set.Add(unit);
        }

        public static void Process(float now)
        {
            if (s_pending.Count == 0 || now < s_nextProcessTime) return;
            s_nextProcessTime = now + ProcessIntervalSeconds;

            try
            {
                s_emptyHqBuffer.Clear();
                foreach (var kvp in s_pending)
                {
                    FactionHQ hq = kvp.Key;
                    HashSet<Unit> set = kvp.Value;
                    s_doneBuffer.Clear();

                    if (hq == null)
                    {
                        set.Clear();
                    }
                    else
                    {
                        Dictionary<Unit, TriangulationTrack> tracks = RWRTriangulationProcessor.GetTrackDictionary(hq);
                        foreach (Unit unit in set)
                        {
                            if (unit == null || unit.disabled)
                            {
                                s_doneBuffer.Add(unit);
                                continue;
                            }
                            // The faction has a track again: its knowledge is current
                            if (tracks != null && tracks.ContainsKey(unit))
                            {
                                s_doneBuffer.Add(unit);
                                continue;
                            }
                            TrackingInfo info = hq.GetTrackingData(unit.persistentID);
                            if (info == null)
                            {
                                s_doneBuffer.Add(unit);
                                continue;
                            }
                            // A recent vanilla contact (for example visual or missile warning) keeps the entry
                            if (now - info.lastSpottedTime <= RWRTriangulationProcessor.GetTargetMemoryDuration(unit)) continue;

                            Forget(hq, unit);
                            if (CnoEvilNet.IsServerActive && unit.persistentID.Id != 0u)
                            {
                                ForgetRecord rec;
                                rec.Hq = hq;
                                rec.Id = unit.persistentID.Id;
                                s_outbox.Add(rec);
                            }
                            s_doneBuffer.Add(unit);
                        }
                        for (int i = 0; i < s_doneBuffer.Count; i++)
                        {
                            set.Remove(s_doneBuffer[i]);
                        }
                    }

                    if (set.Count == 0) s_emptyHqBuffer.Add(hq);
                }

                for (int i = 0; i < s_emptyHqBuffer.Count; i++)
                {
                    HashSet<Unit> set = s_pending[s_emptyHqBuffer[i]];
                    s_pending.Remove(s_emptyHqBuffer[i]);
                    set.Clear();
                    s_setPool.Push(set);
                }

                SendForgotten();
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("VanillaKnowledgeExpiry.Process", ex);
            }
            finally
            {
                s_doneBuffer.Clear();
                s_emptyHqBuffer.Clear();
            }
        }

        private static void Forget(FactionHQ hq, Unit unit)
        {
            EnsureReflection();

            // RpcUpdateTrackingInfo subscribes these again when it re-adds the entry; remove them so they do not stack
            unit.onDisableUnit -= hq.DeregisterTrackedUnit;
            if (s_hqChangeFactionMethod != null)
            {
                unit.onChangeFaction -= (Action<Unit>)Delegate.CreateDelegate(typeof(Action<Unit>), hq, s_hqChangeFactionMethod);
            }

            hq.DeregisterTrackedUnit(unit);

            // The map icon and the HUD marker cache the removed TrackingInfo; remove them the way vanilla does on disable
            DynamicMap map = SceneSingleton<DynamicMap>.i;
            if (map != null && map.HQ == hq && s_iconLookupGetter != null && s_mapIconOnUnitDisabled != null)
            {
                Dictionary<Unit, UnitMapIcon> icons = s_iconLookupGetter(map);
                UnitMapIcon icon;
                if (icons != null && icons.TryGetValue(unit, out icon) && icon != null)
                {
                    s_mapIconOnUnitDisabled(icon, unit);
                }
            }

            CombatHUD hud = SceneSingleton<CombatHUD>.i;
            if (hud != null && hud.aircraft != null && hud.aircraft.NetworkHQ == hq &&
                s_markerLookupGetter != null && s_markerOnDisableUnit != null)
            {
                Dictionary<Unit, HUDUnitMarker> markers = s_markerLookupGetter(hud);
                HUDUnitMarker marker;
                if (markers != null && markers.TryGetValue(unit, out marker) && marker != null)
                {
                    s_markerOnDisableUnit(marker, unit);
                }
            }
        }

        private static void SendForgotten()
        {
            if (s_outbox.Count == 0) return;
            List<NetPeer> peers = CnoEvilNet.Peers;
            for (int p = 0; p < peers.Count; p++)
            {
                NetPeer peer = peers[p];
                if (peer.Connection == null || peer.Faction == null) continue;
                PooledNetworkWriter w = null;
                int count = 0;
                for (int i = 0; i < s_outbox.Count; i++)
                {
                    if (s_outbox[i].Hq != peer.Faction) continue;
                    if (w == null) w = CnoEvilNet.Begin(NetKind.Forget);
                    w.WritePackedUInt32(s_outbox[i].Id);
                    count++;
                    if (count >= MaxIdsPerPacket)
                    {
                        w.WritePackedUInt32(0u);
                        CnoEvilNet.Send(peer.Connection, w, Channel.Reliable);
                        w = null;
                        count = 0;
                    }
                }
                if (w != null)
                {
                    w.WritePackedUInt32(0u);
                    CnoEvilNet.Send(peer.Connection, w, Channel.Reliable);
                }
            }
            s_outbox.Clear();
        }

        /// <summary>
        /// Registers the mirror client handler. Called every frame from the plugin, also while the mod is off.
        /// </summary>
        public static void UpdateNet()
        {
            if (s_handlerRegistered) return;
            CnoEvilNet.RegisterClientHandler(NetKind.Forget, OnForget);
            s_handlerRegistered = true;
        }

        private static void OnForget(INetworkPlayer sender, NetworkReader r)
        {
            FactionHQ hq = RWRTriangulationProcessor.GetPlayerFactionHQ();
            while (true)
            {
                Unit unit = null;
                uint id = r.ReadPackedUInt32();
                if (id == 0u) return;
                PersistentID pid = default(PersistentID);
                pid.Id = id;
                if (!UnitRegistry.TryGetUnit(pid, out unit) || unit == null || unit.disabled || hq == null) continue;
                if (unit.NetworkHQ != null && unit.NetworkHQ == hq) continue;
                try
                {
                    RWRTriangulationProcessor.RemoveRemoteTrack(unit);
                    if (hq.GetTrackingData(unit.persistentID) != null) Forget(hq, unit);
                }
                catch (Exception ex)
                {
                    PatchErrorLog.Once("VanillaKnowledgeExpiry.OnForget", ex);
                }
            }
        }

        private static void EnsureReflection()
        {
            if (s_reflectionReady) return;
            s_reflectionReady = true;

            s_hqChangeFactionMethod = AccessTools.Method(typeof(FactionHQ), "HQ_OnUnitChangeFaction", new Type[] { typeof(Unit) });
            s_iconLookupGetter = FastReflection.CreateFieldGetter<DynamicMap, Dictionary<Unit, UnitMapIcon>>("iconLookup");
            s_markerLookupGetter = FastReflection.CreateFieldGetter<CombatHUD, Dictionary<Unit, HUDUnitMarker>>("markerLookup");
            s_mapIconOnUnitDisabled = CreateOpenDelegate<UnitMapIcon>("UnitMapIcon_OnUnitDisabled");
            s_markerOnDisableUnit = CreateOpenDelegate<HUDUnitMarker>("HUDIcon_OnDisableUnit");

            if (s_hqChangeFactionMethod == null) Warn("FactionHQ.HQ_OnUnitChangeFaction not found");
        }

        private static Action<T, Unit> CreateOpenDelegate<T>(string methodName) where T : class
        {
            MethodInfo mi = AccessTools.Method(typeof(T), methodName, new Type[] { typeof(Unit) });
            if (mi == null)
            {
                Warn(typeof(T).Name + "." + methodName + " not found");
                return null;
            }
            return (Action<T, Unit>)Delegate.CreateDelegate(typeof(Action<T, Unit>), null, mi);
        }

        private static void Warn(string message)
        {
            if (CnoEvilPlugin.Log != null)
            {
                CnoEvilPlugin.Log.LogWarning("VanillaKnowledgeExpiry: " + message);
            }
        }

        public static void RemoveUnit(Unit unit)
        {
            if (unit == null) return;
            foreach (var kvp in s_pending)
            {
                kvp.Value.Remove(unit);
            }
        }

        public static void ClearAll()
        {
            foreach (var kvp in s_pending)
            {
                kvp.Value.Clear();
                s_setPool.Push(kvp.Value);
            }
            s_pending.Clear();
            s_doneBuffer.Clear();
            s_emptyHqBuffer.Clear();
            s_outbox.Clear();
            s_nextProcessTime = 0f;
        }

        public static void Shutdown()
        {
            ClearAll();
            if (s_handlerRegistered)
            {
                CnoEvilNet.RegisterClientHandler(NetKind.Forget, null);
                s_handlerRegistered = false;
            }
        }
    }
}
