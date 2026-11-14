using System;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace CnoEvil.Core
{
    /// <summary>
    /// Why CnoEvilNet did not send or did not use a packet. The order is the order of the summary line.
    /// </summary>
    public enum NetDrop
    {
        NotConnected = 0,   // send to a connection that is gone, or a client that is not connected
        BeforeWelcome,      // client: a mod packet before the host Welcome
        UnknownKind,        // a kind byte this protocol does not know
        NoHandler,          // client: no handler registered for the kind
        Protocol,           // hello or Welcome of another protocol
        KeyHash,            // Welcome of a host with another settings list
        NotPeer,            // host: Resync, IRST or CM message of a connection without hello
        Empty,              // a packet without data
        Count
    }

    /// <summary>
    /// Traffic of the mod message (CnoEvilPacket) on this computer: packets and bytes for each NetKind in both
    /// directions, the largest packet, and the drops. CnoEvilNet counts in Send, SendToServer and the two receive
    /// handlers (no allocation). Update writes one NET summary line each SummarySeconds when there was traffic;
    /// EndScene writes the rest at the end of the scene.
    /// </summary>
    public static class NetStats
    {
        private const float SummarySeconds = 30.0f;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static readonly int[] s_sentPackets = new int[(int)NetKind.Count];
        private static readonly long[] s_sentBytes = new long[(int)NetKind.Count];
        private static readonly int[] s_sentMax = new int[(int)NetKind.Count];
        private static readonly int[] s_recvPackets = new int[(int)NetKind.Count];
        private static readonly long[] s_recvBytes = new long[(int)NetKind.Count];
        private static readonly int[] s_recvMax = new int[(int)NetKind.Count];
        private static readonly int[] s_drops = new int[(int)NetDrop.Count];
        private static readonly bool[] s_oversizeLogged = new bool[(int)NetKind.Count];
        private static readonly bool[] s_firstReceived = new bool[(int)NetKind.Count];
        private static readonly string[] s_dropKeys = MakeDropKeys();

        private static string[] MakeDropKeys()
        {
            string[] keys = new string[(int)NetKind.Count];
            for (int i = 0; i < keys.Length; i++) keys[i] = "net-drop-" + i;
            return keys;
        }

        private static bool s_any;
        private static float s_periodStart = -1.0f;
        private static float s_nextSummary;

        public static void Sent(byte kind, int bytes)
        {
            if (kind >= (byte)NetKind.Count) return;
            s_sentPackets[kind]++;
            s_sentBytes[kind] += bytes;
            if (bytes > s_sentMax[kind]) s_sentMax[kind] = bytes;
            s_any = true;
            CheckSize(kind, bytes, "sent");
        }

        public static void Received(byte kind, int bytes)
        {
            if (kind >= (byte)NetKind.Count) return;
            s_recvPackets[kind]++;
            s_recvBytes[kind] += bytes;
            if (bytes > s_recvMax[kind]) s_recvMax[kind] = bytes;
            s_any = true;
            CheckSize(kind, bytes, "received");
            if (!s_firstReceived[kind])
            {
                s_firstReceived[kind] = true;
                EvilLog.Write(LogCat.Net, "first {0} received in this scene ({1} B)", (NetKind)kind, bytes);
            }
        }

        /// <summary>
        /// A received record that this computer cannot use, with the reason (for example a unit that is not here).
        /// At most one line for each kind each SummarySeconds; the line tells how many were skipped.
        /// </summary>
        public static void RecordDropped(NetKind kind, string reason)
        {
            if (!EvilLog.On(LogCat.Net)) return;
            EvilLog.Every(LogCat.Net, s_dropKeys[(int)kind], SummarySeconds, "{0} record not used: {1}", kind, reason);
        }

        public static void Drop(NetDrop reason)
        {
            s_drops[(int)reason]++;
            s_any = true;
        }

        // A packet above MaxPacketBytes can be above the transport MTU; one line for each kind and scene
        private static void CheckSize(byte kind, int bytes, string direction)
        {
            if (bytes <= CnoEvilNet.MaxPacketBytes || s_oversizeLogged[kind]) return;
            s_oversizeLogged[kind] = true;
            EvilLog.Write(LogCat.Net, "packet {0} {1} B {2}, above the limit {3} B", (NetKind)kind, bytes, direction, CnoEvilNet.MaxPacketBytes);
        }

        /// <summary>
        /// Called each frame from CnoEvilNet.Update.
        /// </summary>
        public static void Update()
        {
            float now = Time.unscaledTime;
            if (s_periodStart < 0.0f)
            {
                s_periodStart = now;
                s_nextSummary = now + SummarySeconds;
                return;
            }
            if (now < s_nextSummary) return;
            s_nextSummary = now + SummarySeconds;
            WriteSummary(now - s_periodStart);
            s_periodStart = now;
        }

        /// <summary>
        /// End of the scene (CnoEvilPlugin.ResetAll): the counts since the last summary.
        /// </summary>
        public static void EndScene()
        {
            float now = Time.unscaledTime;
            WriteSummary(s_periodStart < 0.0f ? 0.0f : now - s_periodStart);
            s_periodStart = now;
            s_nextSummary = now + SummarySeconds;
            Array.Clear(s_oversizeLogged, 0, s_oversizeLogged.Length);
            Array.Clear(s_firstReceived, 0, s_firstReceived.Length);
        }

        private static void WriteSummary(float seconds)
        {
            if (!s_any) return;
            if (EvilLog.On(LogCat.Net))
            {
                StringBuilder b = new StringBuilder(256);
                b.AppendFormat(Inv, "net {0:F0} s, role {1}, peers {2} | sent:", seconds, CnoEvilNet.Role, CnoEvilNet.Peers.Count);
                AppendKinds(b, s_sentPackets, s_sentBytes, s_sentMax);
                b.Append(" | recv:");
                AppendKinds(b, s_recvPackets, s_recvBytes, s_recvMax);
                b.Append(" | drops:");
                bool anyDrop = false;
                for (int i = 0; i < s_drops.Length; i++)
                {
                    if (s_drops[i] == 0) continue;
                    b.AppendFormat(Inv, " {0} {1}", (NetDrop)i, s_drops[i]);
                    anyDrop = true;
                }
                if (!anyDrop) b.Append(" none");
                EvilLog.Write(LogCat.Net, "{0}", b.ToString());
            }
            Reset();
        }

        private static void AppendKinds(StringBuilder b, int[] packets, long[] bytes, int[] max)
        {
            bool any = false;
            for (int i = 0; i < packets.Length; i++)
            {
                if (packets[i] == 0) continue;
                b.AppendFormat(Inv, " {0} {1} pk {2:F1} KB max {3} B,", (NetKind)i, packets[i], bytes[i] / 1024.0, max[i]);
                any = true;
            }
            if (any) b.Length--;
            else b.Append(" none");
        }

        private static void Reset()
        {
            Array.Clear(s_sentPackets, 0, s_sentPackets.Length);
            Array.Clear(s_sentBytes, 0, s_sentBytes.Length);
            Array.Clear(s_sentMax, 0, s_sentMax.Length);
            Array.Clear(s_recvPackets, 0, s_recvPackets.Length);
            Array.Clear(s_recvBytes, 0, s_recvBytes.Length);
            Array.Clear(s_recvMax, 0, s_recvMax.Length);
            Array.Clear(s_drops, 0, s_drops.Length);
            s_any = false;
        }
    }
}
