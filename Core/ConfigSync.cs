using System;
using System.Collections.Generic;
using UnityEngine;
using BepInEx.Configuration;
using Mirage;
using Mirage.Serialization;
using CnoEvil.Config;

namespace CnoEvil.Core
{
    /// <summary>
    /// Host authority for the config (NetKind.HostConfig, reliable). In multiplayer the host decides every entry in
    /// CnoEvilConfig.HostEntries (mechanics and the display settings that change what a player knows); a client keeps
    /// only its visual settings. The host sends all values after Welcome and again after a change.
    /// The client sets the host values on its entries (SettingChanged handlers run) with saving off, so the host values
    /// never reach its .cfg; its own values come back when it leaves the session. KeyHash (in Welcome) makes sure both
    /// computers have the same entry list.
    /// </summary>
    public static class ConfigSync
    {
        private const float ResendIntervalSeconds = 0.5f;
        private const int ChunkSize = 150;          // 150 values x at most 6 bytes (type + value) stay below MaxPacketBytes

        private const byte TypeBool = 0;
        private const byte TypeInt = 1;
        private const byte TypeFloat = 2;
        private const byte TypeEnum = 3;

        private static readonly NetChannel s_channel = new NetChannel(NetKind.HostConfig, OnHostConfig, OnWelcomed);
        private static bool s_subscribed;
        private static bool s_hasKeyHash;
        private static uint s_keyHash;

        // Host
        private static bool s_dirty;

        // Client
        private static object[] s_hostValues;
        private static object[] s_backup;
        private static bool s_applied;
        private static bool s_overridden;
        private static bool s_applying;
        private static bool s_savedSaveOnSet;

        /// <summary>
        /// FNV-1a of section / key / type of every host entry in order. Same value on host and client = same entry list.
        /// </summary>
        public static uint KeyHash
        {
            get
            {
                if (s_hasKeyHash) return s_keyHash;
                uint h = 2166136261u;
                List<ConfigEntryBase> entries = CnoEvilConfig.HostEntries;
                for (int i = 0; i < entries.Count; i++)
                {
                    ConfigDefinition d = entries[i].Definition;
                    h = Hash(h, d.Section);
                    h = Hash(h, d.Key);
                    h = Hash(h, entries[i].SettingType.Name);
                }
                s_keyHash = h;
                s_hasKeyHash = true;
                return h;
            }
        }

        private static uint Hash(uint h, string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                h ^= s[i];
                h *= 16777619u;
            }
            h ^= '/';
            h *= 16777619u;
            return h;
        }

        /// <summary>
        /// Client: all host values are set on the entries.
        /// </summary>
        public static bool HostConfigApplied
        {
            get { return s_applied; }
        }

        /// <summary>
        /// Called every frame from the plugin, also while the mod is off.
        /// </summary>
        public static void Update()
        {
            try
            {
                s_channel.Register();
                if (!s_subscribed && CnoEvilConfig.File != null)
                {
                    CnoEvilConfig.File.SettingChanged += OnSettingChanged;
                    s_subscribed = true;
                }

                if (!CnoEvilNet.IsServerActive)
                {
                    s_dirty = false;
                    return;
                }
                if (!s_dirty || !s_channel.SendDue(ResendIntervalSeconds)) return;
                s_dirty = false;
                List<NetPeer> peers = CnoEvilNet.Peers;
                for (int i = 0; i < peers.Count; i++) SendTo(peers[i].Connection);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("ConfigSync.Update", ex);
            }
        }

        // ---------------------------------------------------------------- host

        /// <summary>
        /// Host: sends all host values to one client (after Welcome).
        /// </summary>
        public static void SendTo(INetworkPlayer player)
        {
            if (player == null) return;
            List<ConfigEntryBase> entries = CnoEvilConfig.HostEntries;
            int total = entries.Count;
            int start = 0;
            do
            {
                int count = Mathf.Min(ChunkSize, total - start);
                PooledNetworkWriter w = CnoEvilNet.Begin(NetKind.HostConfig);
                w.WriteUInt16((ushort)start);
                w.WriteUInt16((ushort)count);
                w.WriteUInt16((ushort)total);
                for (int i = start; i < start + count; i++) WriteValue(w, entries[i]);
                CnoEvilNet.Send(player, w, Channel.Reliable);
                start += count;
            }
            while (start < total);
        }

        private static void WriteValue(NetworkWriter w, ConfigEntryBase entry)
        {
            Type t = entry.SettingType;
            object v = entry.BoxedValue;
            if (t == typeof(bool))
            {
                w.WriteByte(TypeBool);
                w.WriteByte((bool)v ? (byte)1 : (byte)0);
            }
            else if (t == typeof(int))
            {
                w.WriteByte(TypeInt);
                w.WritePackedInt32((int)v);
            }
            else if (t == typeof(float))
            {
                w.WriteByte(TypeFloat);
                w.WriteSingle((float)v);
            }
            else if (t.IsEnum)
            {
                w.WriteByte(TypeEnum);
                w.WritePackedInt32(Convert.ToInt32(v));
            }
            else
            {
                throw new NotSupportedException("ConfigSync: no wire type for " + t.Name + " (" + entry.Definition.Key + ")");
            }
        }

        private static void OnSettingChanged(object sender, SettingChangedEventArgs e)
        {
            if (s_applying || e == null || e.ChangedSetting == null) return;
            int index = CnoEvilConfig.HostEntries.IndexOf(e.ChangedSetting);
            if (index < 0) return;

            if (CnoEvilNet.IsServerActive)
            {
                s_dirty = true;
                return;
            }
            if (!s_overridden || s_hostValues == null || s_hostValues[index] == null) return;

            // Client in a session: the edit is the player's own value for later; the host value stays in use
            s_backup[index] = e.ChangedSetting.BoxedValue;
            SetValue(e.ChangedSetting, s_hostValues[index]);
        }

        // ---------------------------------------------------------------- client

        private static void OnHostConfig(INetworkPlayer sender, NetworkReader r)
        {
            List<ConfigEntryBase> entries = CnoEvilConfig.HostEntries;
            int start = r.ReadUInt16();
            int count = r.ReadUInt16();
            int total = r.ReadUInt16();
            if (total != entries.Count || start + count > total) return;
            if (s_hostValues == null || s_hostValues.Length != total) s_hostValues = new object[total];

            for (int i = start; i < start + count; i++)
            {
                byte type = r.ReadByte();
                object value;
                switch (type)
                {
                    case TypeBool: value = r.ReadByte() != 0; break;
                    case TypeInt: value = r.ReadPackedInt32(); break;
                    case TypeFloat: value = r.ReadSingle(); break;
                    case TypeEnum: value = Enum.ToObject(entries[i].SettingType, r.ReadPackedInt32()); break;
                    default: return;
                }
                s_hostValues[i] = (value.GetType() == entries[i].SettingType) ? value : null;
            }

            // The last chunk completes the set
            if (start + count == total) Apply();
        }

        private static void Apply()
        {
            List<ConfigEntryBase> entries = CnoEvilConfig.HostEntries;
            ConfigFile file = CnoEvilConfig.File;
            if (file == null || s_hostValues == null) return;

            if (!s_overridden)
            {
                s_backup = new object[entries.Count];
                for (int i = 0; i < entries.Count; i++) s_backup[i] = entries[i].BoxedValue;
                s_savedSaveOnSet = file.SaveOnConfigSet;
                file.SaveOnConfigSet = false;
                s_overridden = true;
            }

            int changed = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                if (s_hostValues[i] != null && SetValue(entries[i], s_hostValues[i])) changed++;
            }
            if (!s_applied && CnoEvilPlugin.Log != null)
            {
                CnoEvilPlugin.Log.LogInfo(string.Format("[CNO-EVIL] Host settings in use: {0} entries, {1} differ from this computer.", entries.Count, changed));
            }
            s_applied = true;
        }

        private static bool SetValue(ConfigEntryBase entry, object value)
        {
            if (Equals(entry.BoxedValue, value)) return false;
            s_applying = true;
            try
            {
                entry.BoxedValue = value;
            }
            finally
            {
                s_applying = false;
            }
            return true;
        }

        private static void OnWelcomed()
        {
            // A new session: wait for the host values before the mod runs
            s_applied = false;
            s_hostValues = null;
        }

        /// <summary>
        /// The client left the session (or the plugin unloads): its own values come back and are saved with any
        /// visual change made in the session.
        /// </summary>
        public static void Restore()
        {
            s_applied = false;
            s_hostValues = null;
            if (!s_overridden) return;
            s_overridden = false;

            List<ConfigEntryBase> entries = CnoEvilConfig.HostEntries;
            ConfigFile file = CnoEvilConfig.File;
            try
            {
                for (int i = 0; i < entries.Count && s_backup != null && i < s_backup.Length; i++)
                {
                    if (s_backup[i] != null) SetValue(entries[i], s_backup[i]);
                }
                if (file != null)
                {
                    file.SaveOnConfigSet = s_savedSaveOnSet;
                    file.Save();
                }
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("ConfigSync.Restore", ex);
            }
            s_backup = null;
        }

        public static void Shutdown()
        {
            Restore();
            if (s_subscribed && CnoEvilConfig.File != null) CnoEvilConfig.File.SettingChanged -= OnSettingChanged;
            s_subscribed = false;
            s_dirty = false;
            s_channel.Shutdown();
        }
    }
}
