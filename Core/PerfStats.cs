using System.Diagnostics;
using System.Text;
using UnityEngine;
using CnoEvil.Config;

namespace CnoEvil.Core
{
    /// <summary>
    /// Measured CPU time and call counts of the main mod subsystems, and the mod network traffic.
    /// With LogPerfStats on, the totals go to the BepInEx log every LogIntervalSeconds, then reset.
    /// Usage: long t = PerfStats.Begin(); ... PerfStats.End(PerfSlot.X, t). Begin returns 0 when the log is off.
    /// </summary>
    public enum PerfSlot
    {
        RadarCheck,
        SarhSeeker,
        DatalinkCycle,
        TrackUpdate,
        NetServer,
        NetClient,
        Count
    }

    public static class PerfStats
    {
        private const float LogIntervalSeconds = 10.0f;

        private static readonly string[] s_names = { "RadarCheck", "SarhSeeker", "DatalinkCycle", "TrackUpdate", "NetServer", "NetClient" };
        private static readonly long[] s_ticks = new long[(int)PerfSlot.Count];
        private static readonly int[] s_calls = new int[(int)PerfSlot.Count];
        private static long s_bytesSent;
        private static int s_packetsSent;
        private static long s_bytesReceived;
        private static int s_packetsReceived;
        private static float s_nextLogTime;
        private static readonly StringBuilder s_sb = new StringBuilder(512);

        public static bool Enabled
        {
            get { return CnoEvilConfig.LogPerfStats != null && CnoEvilConfig.LogPerfStats.Value; }
        }

        public static long Begin()
        {
            return Enabled ? Stopwatch.GetTimestamp() : 0L;
        }

        public static void End(PerfSlot slot, long start)
        {
            if (start == 0L) return;
            s_ticks[(int)slot] += Stopwatch.GetTimestamp() - start;
            s_calls[(int)slot]++;
        }

        public static void CountSent(int bytes)
        {
            s_bytesSent += bytes;
            s_packetsSent++;
        }

        public static void CountReceived(int bytes)
        {
            s_bytesReceived += bytes;
            s_packetsReceived++;
        }

        /// <summary>
        /// Called every frame from the plugin. Writes and resets the totals at the log interval.
        /// </summary>
        public static void Tick()
        {
            if (!Enabled) return;
            float now = Time.unscaledTime;
            if (s_nextLogTime <= 0.0f)
            {
                s_nextLogTime = now + LogIntervalSeconds;
                Reset();
                return;
            }
            if (now < s_nextLogTime) return;
            s_nextLogTime = now + LogIntervalSeconds;

            double toMs = 1000.0 / Stopwatch.Frequency;
            s_sb.Length = 0;
            s_sb.Append("[CNO-EVIL perf] per s:");
            for (int i = 0; i < (int)PerfSlot.Count; i++)
            {
                if (s_calls[i] == 0) continue;
                s_sb.AppendFormat(" {0} {1:F2} ms / {2} calls;", s_names[i], s_ticks[i] * toMs / LogIntervalSeconds, s_calls[i] / (int)LogIntervalSeconds);
            }
            if (s_packetsSent > 0 || s_packetsReceived > 0)
            {
                s_sb.AppendFormat(" net out {0} B / {1} pkt; net in {2} B / {3} pkt;",
                    s_bytesSent / (long)LogIntervalSeconds, s_packetsSent / (int)LogIntervalSeconds,
                    s_bytesReceived / (long)LogIntervalSeconds, s_packetsReceived / (int)LogIntervalSeconds);
            }
            CnoEvilPlugin.Log.LogInfo(s_sb.ToString());
            Reset();
        }

        private static void Reset()
        {
            for (int i = 0; i < (int)PerfSlot.Count; i++)
            {
                s_ticks[i] = 0L;
                s_calls[i] = 0;
            }
            s_bytesSent = 0L;
            s_packetsSent = 0;
            s_bytesReceived = 0L;
            s_packetsReceived = 0;
        }
    }
}
